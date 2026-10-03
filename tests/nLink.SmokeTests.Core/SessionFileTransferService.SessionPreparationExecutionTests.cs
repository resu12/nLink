using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferServicePreparationTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("missing_lease")]
    [InlineData("missing_execution")]
    [InlineData("reset_during_capture")]
    [InlineData("canceled_during_capture")]
    [InlineData("admission")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_StartupTransitionRechecksLifetimeAndQueuedTimer(string outcome)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        var epoch = transport.SessionPreparationLifecycle.Current!;
        var permit = attachment.Authority.Begin(attachment, epoch, "cold", 1, true,
            DateTimeOffset.UtcNow.AddMilliseconds(500), listenerStartup: true).Permit!;
        if (outcome == "missing_execution")
        {
            Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, attachment.Authority.BeginHandshake(attachment, epoch, permit).Status);
            Assert.Equal(0, transport.LeaseReads);
            return;
        }
        var execution = Assert.IsType<FileTransferSessionPreparationExecution>(attachment.Authority.BindExecution(attachment, epoch, permit));
        if (outcome == "missing_lease") transport.LeaseAvailable = false;
        if (outcome == "reset_during_capture") transport.BeforeLeaseRead = service.ResetSessionState;
        if (outcome == "canceled_during_capture") transport.BeforeLeaseRead = execution.Retire;
        if (outcome == "admission") await Start(service);
        var decision = attachment.Authority.BeginHandshake(attachment, epoch, permit);
        if (outcome != "ready")
        {
            Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, decision.Status);
            Assert.Null(decision.State.Readiness);
            return;
        }
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, decision.Status);
        typeof(FileTransferSessionPreparationExecution).GetMethod("Expire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(execution, null);
        Assert.True(execution.AllowsPreparation);
        Assert.Equal(decision.State.Handshake!.DeadlineUtc,
            typeof(FileTransferSessionPreparationExecution).GetField("expiresUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution));
        var duplicate = attachment.Authority.BeginHandshake(attachment, epoch, permit);
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, duplicate.Status);
        Assert.Same(decision.State, duplicate.State);
        await Task.Delay(600);
        Assert.True(execution.AllowsPreparation); // The old startup deadline has passed; only Core's handshake deadline applies.
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted,
            attachment.Authority.Complete(attachment, epoch, permit, Proof(permit)).Status);
        service.ResetSessionState();
        Assert.True(execution.Cancellation.IsCancellationRequested);
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, attachment.Authority.BeginHandshake(attachment, epoch, permit).Status);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("dispose")]
    [InlineData("disconnect")]
    [InlineData("key_reset")]
    [InlineData("key_replaced")]
    [InlineData("unbound_key")]
    [InlineData("approval_loss")]
    [InlineData("peer_change")]
    [InlineData("attachment")]
    [InlineData("episode")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public void ExecutionBinding_LifecycleRetirementImmediatelyCancelsPhysicalWork(string change)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        var execution = BindExecution(binding);
        switch (change)
        {
            case "reset": service.ResetSessionState(); break;
            case "detach": service.DetachTransport(); break;
            case "dispose": service.Dispose(); break;
            case "disconnect": transport.RaiseDisconnected(); break;
            case "key_reset": transport.SessionPreparationLifecycle.ResetKey(); break;
            case "key_replaced": transport.Install(); break;
            case "unbound_key": transport.SessionPreparationLifecycle.InstallKey(null); break;
            case "approval_loss": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { ApprovalGranted = false }); break;
            case "peer_change": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { HelperAddress = new PeerAddress("changed") }); break;
            case "attachment": binding.Attachment.Dispose(); break;
            case "episode": binding.Attachment.Authority.Retire(binding.Attachment, binding.Epoch, binding.Permit); break;
        }
        Assert.True(execution.Cancellation.IsCancellationRequested);
        Assert.False(execution.AllowsPreparation);
        Assert.False(execution.IsResourceCurrent);
        Assert.Null(binding.Attachment.Authority.BindExecution(binding.Attachment, binding.Epoch, binding.Permit));
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted,
            binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit)).Status);
        Assert.Null(service.Snapshot.Outbound);
        Assert.Null(service.Snapshot.Inbound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_AdmissionCancelsPendingButPreservesOnlyExactCompletedResources(bool completed)
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        var execution = BindExecution(binding);
        if (completed)
            Assert.Equal(FileTransferSessionPreparationStatus.Accepted,
                binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit)).Status);

        Assert.NotNull(await Start(service));

        var state = Assert.IsType<FileTransferSessionPreparationState>(ServiceField(service, "sessionPreparation"));
        Assert.False(execution.AllowsPreparation);
        Assert.True(execution.Cancellation.IsCancellationRequested);
        Assert.False(execution.IsAdmissionCurrent(state.Admission!));
        if (completed)
            Assert.True(Assert.IsType<FileTransferSessionPreparationAdmissionLease>(ServiceField(service, "preparationAdmissionLease")).IsAdopted);
        Assert.False(execution.IsAdmissionCurrent(state.Admission! with { }));
        Assert.Null(binding.Attachment.Authority.BindExecution(binding.Attachment, binding.Epoch, binding.Permit));
        service.ResetSessionState();
        Assert.True(execution.Cancellation.IsCancellationRequested);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_RetirementDoesNotRunOrAwaitCleanupOnCoreGateOwner()
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var execution = BindExecution(Begin(transport));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = execution.Cancellation.Register(() =>
        {
            entered.TrySetResult(Monitor.IsEntered(ServiceField(service, "gate")!));
            release.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            await Task.Run(service.ResetSessionState).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(execution.Cancellation.IsCancellationRequested);
            Assert.False(await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public void ExecutionBinding_IsExactIdempotentAndCannotBeCreatedAfterSyntheticCompletion(bool completeFirst)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        if (completeFirst)
        {
            binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit));
            Assert.Null(binding.Attachment.Authority.BindExecution(binding.Attachment, binding.Epoch, binding.Permit));
            return;
        }
        var execution = BindExecution(binding);
        Assert.Same(execution, BindExecution(binding));
        Assert.Null(binding.Attachment.Authority.BindExecution(binding.Attachment, binding.Epoch, binding.Permit with { }));
        execution.Retire();
        Assert.False(binding.Attachment.Authority.IsCurrent(binding.Attachment, binding.Epoch, binding.Permit));
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted,
            binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_ExpiryAndQueuedTimerRespectCurrentCorePhase(bool completed)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var attachment = transport.SessionPreparationLifecycle.Attachment!;
        var epoch = transport.SessionPreparationLifecycle.Current!;
        var permit = attachment.Authority.Begin(attachment, epoch, "short-deadline", 1, true,
            DateTimeOffset.UtcNow.AddMilliseconds(500)).Permit!;
        var execution = Assert.IsType<FileTransferSessionPreparationExecution>(attachment.Authority.BindExecution(attachment, epoch, permit));
        if (completed)
        {
            Assert.Equal(FileTransferSessionPreparationStatus.Accepted,
                attachment.Authority.Complete(attachment, epoch, permit, Proof(permit)).Status);
            typeof(FileTransferSessionPreparationExecution).GetMethod("Expire", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(execution, null);
            Assert.True(execution.AllowsPreparation);
            Assert.False(execution.Cancellation.IsCancellationRequested);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.Delay(Timeout.Infinite, execution.Cancellation).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(execution.AllowsPreparation);
        }
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public void ExecutionBinding_OldRetirementCannotCancelSuccessorEpisode()
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var first = Begin(transport);
        var old = BindExecution(first);
        first.Attachment.Authority.Retire(first.Attachment, first.Epoch, first.Permit);
        var successor = Begin(transport);
        var current = BindExecution(successor);
        old.Retire();
        first.Attachment.Authority.Retire(first.Attachment, first.Epoch, first.Permit);
        Assert.True(current.AllowsPreparation);
        Assert.False(current.Cancellation.IsCancellationRequested);
    }

    private static FileTransferSessionPreparationExecution BindExecution(Binding binding)
        => Assert.IsType<FileTransferSessionPreparationExecution>(
            binding.Attachment.Authority.BindExecution(binding.Attachment, binding.Epoch, binding.Permit));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_IncomingAdmissionRetiresBeforeQueuedHandlingButInvalidOfferDoesNot(bool valid)
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(transport);
        transport.Install();
        var execution = BindExecution(Begin(transport));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SessionFileTransferService).GetField("inboundLifecycleTail", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(receiver, release.Task);
        try
        {
            await peer.SendFileTransferOfferAsync(Offer("file_tuna_v4", valid ? 4 : 3), CancellationToken.None);
            Assert.Null(receiver.Snapshot.Inbound);
            Assert.Equal(valid, execution.Cancellation.IsCancellationRequested);
            Assert.Equal(!valid, execution.AllowsPreparation);
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => !transport.SentDeclines.IsEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationExecution")]
    public async Task ExecutionBinding_RevokedResourceCannotBeObservedOrClaimedFromPositiveLeaseRead(bool duringRead)
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Begin(transport);
        var execution = BindExecution(binding);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted,
            binding.Attachment.Authority.Complete(binding.Attachment, binding.Epoch, binding.Permit, Proof(binding.Permit)).Status);
        if (duringRead) transport.BeforeLeaseRead = execution.Retire;
        else execution.Retire();
        Assert.Null(binding.Attachment.Authority.Observe(binding.Attachment, binding.Epoch,
            FileTransferDirection.Outbound, DateTimeOffset.UtcNow));
        var transfer = Assert.IsType<FileTransferTransferSnapshot>(await Start(service));
        Assert.Equal("regular_nkn_v4_fast", transfer.RouteToken);
    }
}
