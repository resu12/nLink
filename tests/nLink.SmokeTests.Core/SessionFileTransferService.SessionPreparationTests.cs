using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferCoordinatorSuite", "SessionPreparationService")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed partial class SessionFileTransferServicePreparationTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparedFirstTransfer_UsesBothCoordinatorAdmissionsWithoutMarkingTransportFileActive(bool senderPays)
    {
        using var sendTransport = new PreparationTransport();
        using var receiveTransport = new PreparationTransport();
        sendTransport.Connect(receiveTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(sendTransport);
        receiver.AttachTransport(receiveTransport);
        sendTransport.Install();
        receiveTransport.Install();
        var send = Prepare(sendTransport, senderPays);
        var receive = Prepare(receiveTransport, !senderPays);
        sendTransport.BeforeLeaseRead = () => Assert.False(Monitor.IsEntered(ServiceField(sender, "gate")!));
        receiveTransport.BeforeLeaseRead = () => Assert.False(Monitor.IsEntered(ServiceField(receiver, "gate")!));
        Assert.False(sendTransport.IsFileTunaActiveForRouteSelection);
        Assert.Null(sender.Snapshot.Outbound);
        Assert.Null(receiver.Snapshot.Inbound);

        var started = await Start(sender);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound is not null);
        Assert.Equal("file_tuna_v4", started!.RouteToken);
        Assert.Equal("file_tuna_v4", receiver.Snapshot.Inbound!.RouteToken);
        Assert.Equal(4, Assert.Single(sendTransport.SentOffers).PreferredDataProtocolVersion);
        Assert.False(send.Attachment.Authority.IsCurrent(send.Attachment, send.Epoch, send.Permit));
        Assert.False(receive.Attachment.Authority.IsCurrent(receive.Attachment, receive.Epoch, receive.Permit));
        Assert.Equal(1, sendTransport.LeaseReads);
        Assert.Equal(1, receiveTransport.LeaseReads);
        Assert.Empty(sendTransport.SentDataFrames);
        Assert.Empty(receiveTransport.SentDataFrames);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("disconnect")]
    [InlineData("reject")]
    [InlineData("dispose")]
    [InlineData("key_reset")]
    [InlineData("approval_loss")]
    [InlineData("peer_change")]
    public void LifecycleChange_RejectsLateCompletionAndCannotReopenSameKey(string change)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        switch (change)
        {
            case "reset": service.ResetSessionState(); break;
            case "detach": service.DetachTransport(); service.AttachTransport(transport); break;
            case "disconnect": transport.RaiseDisconnected(); break;
            case "reject":
                var rejected = (EventHandler)typeof(LoopbackFileTransferTransport)
                    .GetField("Rejected", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
                rejected(transport, EventArgs.Empty);
                break;
            case "dispose": service.Dispose(); break;
            case "key_reset": transport.SessionPreparationLifecycle.ResetKey(); break;
            case "approval_loss": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { ApprovalGranted = false }); break;
            case "peer_change": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { HelperAddress = new PeerAddress("other-helper") }); break;
        }
        Assert.False(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
        var late = binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit));
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, late.Status);
        transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security);
        var attachment = transport.SessionPreparationLifecycle.Attachment ?? binding.Attachment;
        var reopened = attachment.Authority.Begin(attachment, binding.Epoch, "repeat", 2, true, DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, reopened.Status);
        Assert.Null(service.Snapshot.Outbound);
        Assert.Null(service.Snapshot.Inbound);
        Assert.Empty(transport.SentOffers);
        Assert.Empty(transport.SentDataFrames);
    }

    [Fact]
    public void ExclusiveOwner_AndLateAttachmentFailClosedUntilNewKeyInstallation()
    {
        using var transport = new PreparationTransport();
        using var first = new SessionFileTransferService();
        using var second = new SessionFileTransferService();
        first.AttachTransport(transport);
        second.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        var wrong = ((IFileTransferSessionPreparationAuthority)second).Begin(binding.Attachment,
            binding.Epoch, "wrong-owner", 1, true, DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, wrong.Status);
        first.DetachTransport();
        second.DetachTransport();
        second.AttachTransport(transport);
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        Assert.False(binding.Attachment.IsCurrent);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, attachment.Authority.Begin(attachment,
            binding.Epoch, "late-attach", 1, true, DateTimeOffset.UtcNow.AddSeconds(30)).Status);
        transport.Install();
        var replacement = Begin(transport);
        Assert.NotEqual(binding.Epoch.Id, replacement.Epoch.Id);
        Assert.False(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
    }

    [Fact]
    public void RepeatedSecurityNotification_DoesNotCreateNewIncarnationOrRenewPermit()
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var first = Begin(transport);
        transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { });
        Assert.Same(first.Epoch, transport.SessionPreparationLifecycle.Current);
        var repeated = first.Attachment.Authority.Begin(first.Attachment, first.Epoch,
            first.Permit.Nonce, first.Permit.PayerDecisionId, first.Permit.LocalIsPayer, first.Permit.DeadlineUtc);
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, repeated.Status);
        Assert.Same(first.Permit, repeated.Permit);
        service.ResetSessionState();
        transport.Install();
        Assert.NotEqual(first.Permit.Scope.SecurityIncarnation, Begin(transport).Permit.Scope.SecurityIncarnation);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("key_replaced")]
    public async Task LifecycleChangeDuringLeaseRead_CannotAdoptPreparedEvidence(string change)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Prepare(transport, true);
        transport.BeforeLeaseRead = () =>
        {
            Assert.False(Monitor.IsEntered(ServiceField(service, "gate")!));
            switch (change)
            {
                case "reset": service.ResetSessionState(); break;
                case "detach": service.DetachTransport(); service.AttachTransport(transport); break;
                case "key_replaced": transport.Install(); break;
            }
        };
        Assert.Null(await Start(service));
        Assert.Empty(transport.SentOffers);
        Assert.Null(service.Snapshot.Outbound);
        Assert.False(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
    }

    [Fact]
    public async Task IncomingOffer_ClosesFenceBeforeQueuedAdmissionStarts()
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SessionFileTransferService).GetField("inboundLifecycleTail", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(receiver, release.Task);
        try
        {
            await peer.SendFileTransferOfferAsync(Offer("file_tuna_v4", 4), CancellationToken.None);
            Assert.Null(receiver.Snapshot.Inbound);
            Assert.False(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
            var late = binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit));
            Assert.Equal(FileTransferSessionPreparationStatus.Stale, late.Status);
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => !transport.SentDeclines.IsEmpty);
        Assert.Null(receiver.Snapshot.Inbound);
        Assert.Equal(0, transport.LeaseReads);
        Assert.Empty(transport.SentDataFrames);
    }

    [Theory]
    [InlineData("file_tuna_v4", 3)]
    [InlineData("file_tuna_v6", 6)]
    public async Task InvalidOffer_DoesNotReserveOrConsumePreparation(string route, int protocol)
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        await peer.SendFileTransferOfferAsync(Offer(route, protocol), CancellationToken.None);
        await WaitUntilAsync(() => !transport.SentDeclines.IsEmpty);
        Assert.True(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
        Assert.Equal(0, transport.LeaseReads);
    }

    [Fact]
    public async Task MissingLease_DoesNotBorrowLegacyPositiveSnapshot()
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(transport);
        transport.Install();
        Prepare(transport, true);
        transport.LeaseAvailable = false;
        transport.SeedInitialTunaReadinessEvidenceForTests("prepared-transfer", FileTransferDirection.Outbound);
        var started = await Start(sender);
        Assert.Equal("regular_nkn_v4_fast", started!.RouteToken);
        Assert.Equal("regular_nkn_v4_fast", Assert.Single(transport.SentOffers).FileTransferRoute);
    }

    private sealed record Binding(FileTransferSessionPreparationAttachment Attachment,
        FileTransferSessionPreparationSecurityEpoch Epoch, FileTransferSessionPreparationPermit Permit);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionWithoutPreparation_ClosesFirstSlotEvenAfterContextReset(bool inbound)
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        if (inbound)
        {
            await peer.SendFileTransferOfferAsync(Offer("regular_nkn_v4_fast", 4), CancellationToken.None);
            await WaitUntilAsync(() => service.Snapshot.Inbound is not null);
        }
        else Assert.NotNull(await Start(service));
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        var epoch = transport.SessionPreparationLifecycle.Current!;
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, attachment.Authority.Begin(attachment,
            epoch, "late", 1, true, DateTimeOffset.UtcNow.AddSeconds(30)).Status);
        service.ResetSessionState();
        transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security);
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, attachment.Authority.Begin(attachment,
            epoch, "after-reset", 1, true, DateTimeOffset.UtcNow.AddSeconds(30)).Status);
        transport.Install();
        Assert.NotEqual(epoch.Id, Begin(transport).Epoch.Id);
    }

    [Fact]
    public async Task AdmissionBeforeApprovalObservation_CannotBeForgottenByLazyRegistration()
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install(publishApproval: false);
        Assert.NotNull(await Start(service));
        transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security);
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        var epoch = transport.SessionPreparationLifecycle.Current!;
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, attachment.Authority.Begin(attachment,
            epoch, "late-approval", 1, true, DateTimeOffset.UtcNow.AddSeconds(30)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadinessObservation_IsNonConsumingAndRechecksLifecycle(bool resetDuringRead)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Prepare(transport, true);
        var before = ServiceField(service, "sessionPreparation");
        if (resetDuringRead) transport.BeforeLeaseRead = service.ResetSessionState;
        var view = binding.Attachment.Authority.Observe(binding.Attachment, binding.Epoch,
            FileTransferDirection.Outbound, DateTimeOffset.UtcNow);
        if (resetDuringRead) Assert.Null(view);
        else
        {
            Assert.True(view!.Ready);
            Assert.False(view.Consumed);
            Assert.Equal("none", view.AuthorityEffect);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
            Assert.True(binding.Attachment.Authority.Observe(binding.Attachment, binding.Epoch,
                FileTransferDirection.Inbound, DateTimeOffset.UtcNow)!.Ready);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
        }
        Assert.Null(service.Snapshot.Outbound);
        Assert.Null(service.Snapshot.Inbound);
        Assert.Empty(transport.SentOffers);
    }

    private static Binding Begin(PreparationTransport transport, bool localIsPayer = true)
    {
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        var epoch = transport.SessionPreparationLifecycle.Current!;
        var decision = attachment.Authority.Begin(attachment, epoch, "preparation-episode", 1,
            localIsPayer, DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, decision.Status);
        return new(attachment, epoch, decision.Permit!);
    }

    private static Binding Prepare(PreparationTransport transport, bool localIsPayer)
    {
        var binding = Begin(transport, localIsPayer);
        var proof = Proof(binding.Permit);
        var decision = binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, proof);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, decision.Status);
        return binding;
    }

    private static FileTransferSessionPreparationProof Proof(FileTransferSessionPreparationPermit permit)
        => new(permit.Nonce, permit.PayerDecisionId, 1, 1, 1, "listener", true, true, true,
            permit.LocalIsPayer, permit.LocalIsPayer, !permit.LocalIsPayer, DateTimeOffset.UtcNow);

    private static Task<FileTransferTransferSnapshot?> Start(SessionFileTransferService service)
        => service.TryStartSendAsync(new FileTransferSendDescriptor("prepared.bin", 4, "prepared-transfer"),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), CancellationToken.None);

    private static FileTransferOfferV2 Offer(string route, int protocol)
        => new() { SessionId = PreparationTransport.Session, TransferId = "prepared-transfer", FileName = "prepared.bin",
            FileSizeBytes = 4, FileTransferRoute = route, PreferredDataProtocolVersion = protocol };

    private static object? ServiceField(SessionFileTransferService service, string name)
        => typeof(SessionFileTransferService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);

    private sealed class PreparationTransport : LoopbackFileTransferTransport,
        IFileTransferSessionPreparationTransport, IFileTransferSessionPreparationLeaseProvider,
        IFileTransferSessionPreparationAdmissionTransport
    {
        internal const string Session = "preparation-service-session";
        public PreparationTransport() : base(Session) { }
        public FileTransferSessionPreparationLifecycle SessionPreparationLifecycle { get; } = new();
        public SessionSecurityState Security { get; private set; } = SessionSecurityState.Empty;
        public Action? BeforeLeaseRead { get; set; }
        public bool LeaseAvailable { get; set; } = true;
        public int LeaseReads { get; private set; }
        public FileTransferSessionPreparationAdmissionLease? RetainSessionPreparationForAdmission(
            FileTransferSessionPreparationAdmission admission, FileTransferInitialTunaReadinessEvidence evidence)
        {
            var attachment = SessionPreparationLifecycle.Attachment!;
            var lease = attachment.Authority.BindAdmission(attachment, SessionPreparationLifecycle.Current!, admission, evidence);
            return lease?.ReportRetained() == true ? lease : null;
        }
        public bool ActivateSessionPreparationAdmission(FileTransferSessionPreparationAdmissionLease lease) => lease.IsAdopted;
        public void Install(bool publishApproval = true)
        {
            SessionPreparationLifecycle.InstallKey(Session);
            Security = new SessionSecurityState
            {
                SessionId = new SessionId(Session), HelpeeAddress = new PeerAddress("helpee"), HelperAddress = new PeerAddress("helper"),
                HandshakeCompleted = true, HandshakeState = SessionHandshakeState.Verified,
                ApprovalGranted = true, ApprovedCapabilities = CapabilityGrant.FileTransfer,
                ApprovalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            };
            if (publishApproval) SessionPreparationLifecycle.ObserveSecurity(Security);
        }
        public bool TryObserveSessionPreparationLease(FileTransferSessionPreparationScope scope,
            long snapshotGeneration, out FileTransferSessionPreparationLeaseObservation? observation)
        {
            LeaseReads++;
            BeforeLeaseRead?.Invoke();
            observation = LeaseAvailable ? new(scope, snapshotGeneration, 1, "listener", true, true, true, DateTimeOffset.UtcNow) : null;
            return observation is not null;
        }
    }
}
