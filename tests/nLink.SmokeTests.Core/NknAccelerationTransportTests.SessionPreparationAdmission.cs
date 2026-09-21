using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_BothRolesAndDirectionsAdoptOnlyRealCoordinatorPublication(bool helperPays, bool payerSends)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            var originalPayer = pair.PayerLane.Current!;
            var originalDialer = pair.DialerLane.Current!;
            foreach (var (transport, service) in new[] { (pair.Payer, pair.PayerService), (pair.Dialer, pair.DialerService) })
            {
                var state = pair.State(service);
                var lifecycle = ((IFileTransferSessionPreparationTransport)transport).SessionPreparationLifecycle;
                foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
                    Assert.True(lifecycle.Attachment!.Authority.Observe(lifecycle.Attachment, lifecycle.Current!, direction, DateTimeOffset.UtcNow)!.Ready);
                Assert.Same(state, pair.State(service));
            }
            var sender = payerSends ? pair.PayerService : pair.DialerService;
            var receiver = payerSends ? pair.DialerService : pair.PayerService;
            var senderLane = payerSends ? pair.PayerLane : pair.DialerLane;
            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(sender, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            foreach (var (transport, service, lane, direction, contextName) in new[]
            {
                (payerSends ? pair.Payer : pair.Dialer, sender, senderLane, FileTransferDirection.Outbound, "outboundTransfer"),
                (payerSends ? pair.Dialer : pair.Payer, receiver, payerSends ? pair.DialerLane : pair.PayerLane, FileTransferDirection.Inbound, "inboundTransfer"),
            })
            {
                var ownership = AdmissionLease(service);
                Assert.True(ownership.IsAdopted);
                Assert.Equal(direction, ownership.Admission.Direction);
                Assert.True(ownership.Execution.Cancellation.IsCancellationRequested);
                Assert.Equal(pair.State(service).Admission!.Readiness!.Proof.ObservedUtc, ownership.Evidence.SourceCapturedUtc);
                var context = GetPrivateField(service, contextName)!;
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                Assert.Equal(1, publication.PublicationGeneration);
                Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
                Assert.False(ownership.Adopt(state with { CurrentRoutePublicationToken = null }, CancellationToken.None));
                Assert.False(ownership.Adopt(state with { CurrentRoutePublicationToken = publication with { TransferId = "other" } }, CancellationToken.None));
                Assert.False(ownership.Adopt(state, new CancellationToken(canceled: true)));
                Assert.Equal((FileTransferRoute.FileTunaV4, 4, direction), (publication.Route, publication.ProtocolVersion, publication.Direction));
                Assert.True(transport.IsAccelerationAvailableForTests);
                Assert.Equal(1, lane.Retains);
                Assert.Equal(1, lane.Activations);
                Assert.True(((IFileTransferSessionPreparationAdmissionTransport)transport).ActivateSessionPreparationAdmission(ownership));
                InvokePrivateMethod(ownership.Execution, "Expire");
                lane.Emit(false, lane.Request!.ExecutionIdentity); // Late preparation event.
                lane.Emit(false, $"admission:{ownership.Id:N}"); // Late provisional-owner event.
                Assert.True(ownership.IsAdopted);
                Assert.True(lane.IsAvailable);
                Assert.Null(GetPrivateField(transport, "runtimeUnlockRecoveryRetryState"));
            }
            Assert.False(pair.PayerLane.TryStopCurrentLaneLease(originalPayer, "old_preparation_cleanup"));
            Assert.False(pair.DialerLane.TryStopCurrentLaneLease(originalDialer, "old_preparation_cleanup"));
            await sender.CancelTransferAsync("prepared-admission", "test_complete", pair.Token);
            await senderLane.Stopped.Task.WaitAsync(pair.Token);
            Assert.False(AdmissionLease(sender).IsCurrent);
            Assert.Equal(0, pair.PayerLane.GlobalStops + pair.DialerLane.GlobalStops);
        });

    [Theory]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "key")]
    [InlineData(true, "key")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(true, "replacement")]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_RetentionRaceNeverAdoptsOrStopsSuccessor(bool afterRetention, string change)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            NknTunaLaneLease? successor = null;
            void Change()
            {
                Assert.False(Monitor.IsEntered(GetPrivateField(pair.PayerService, "gate")!));
                if (change == "reset") pair.PayerService.ResetSessionState();
                else if (change == "key") InvokePrivateMethod(pair.Payer, "ResetControlSecureState");
                else if (change == "throw") throw new IOException("test_retention_failure");
                else successor = pair.PayerLane.Current = pair.PayerLane.Current! with { ClientGeneration = 99 };
            }
            if (afterRetention) pair.Payer.SessionPreparationAfterRetentionForTests = Change;
            else pair.Payer.SessionPreparationBeforeRetentionForTests = Change;
            var sent = await StartPreparedAdmission(pair.PayerService, pair.Token);
            Assert.NotEqual("file_tuna_v4", sent?.RouteToken);
            Assert.Equal(0, pair.PayerLane.Activations);
            Assert.False(AdmissionLease(pair.PayerService).IsCurrent);
            if (successor is not null)
            {
                Assert.Same(successor, pair.PayerLane.Current);
                Assert.Equal(0, pair.PayerLane.ExactStops);
            }
            else await pair.PayerLane.Stopped.Task.WaitAsync(pair.Token);
            Assert.Equal(0, pair.PayerLane.GlobalStops);
        });

    [Theory]
    [InlineData("admission_copy")]
    [InlineData("direction")]
    [InlineData("transfer")]
    [InlineData("evidence_copy")]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_UnissuedBindingsCannotBorrowAdoptedResource(string change)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            await StartPreparedAdmission(pair.PayerService, pair.Token);
            var lease = AdmissionLease(pair.PayerService);
            var admission = lease.Admission;
            var evidence = lease.Evidence;
            if (change == "admission_copy") admission = admission with { };
            if (change == "direction") admission = admission with { Direction = FileTransferDirection.Inbound };
            if (change == "transfer") admission = admission with { TransferId = "other" };
            if (change == "evidence_copy") evidence = evidence with { };
            Assert.Null(((IFileTransferSessionPreparationAdmissionTransport)pair.Payer).RetainSessionPreparationForAdmission(admission, evidence));
            Assert.True(lease.IsAdopted);
            Assert.True(pair.Payer.IsAccelerationAvailableForTests);
            Assert.Equal(1, pair.PayerLane.Retains);
        });

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("key")]
    [InlineData("stop")]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_AdoptedResourceFollowsScopeAndExplicitStop(string change)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            await StartPreparedAdmission(pair.PayerService, pair.Token);
            var lease = AdmissionLease(pair.PayerService);
            Assert.True(lease.IsAdopted);
            if (change == "reset") pair.PayerService.ResetSessionState();
            else if (change == "detach") pair.PayerService.DetachTransport();
            else if (change == "key") InvokePrivateMethod(pair.Payer, "ResetControlSecureState");
            else await pair.Payer.StopAccelerationAsync("user_locked", pair.Token);
            Assert.False(lease.IsCurrent);
            await pair.PayerLane.Stopped.Task.WaitAsync(pair.Token);
            Assert.False(pair.Payer.IsAccelerationAvailableForTests);
            Assert.Equal(change == "stop" ? 1 : 0, pair.PayerLane.GlobalStops);
        });

    [Theory]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "replacement")]
    [InlineData(true, "replacement")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_ActivationRaceCannotPublishActiveOrStopSuccessor(bool afterActivation, string change)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            NknTunaLaneLease? successor = null;
            void Change()
            {
                Assert.False(Monitor.IsEntered(GetPrivateField(pair.PayerService, "gate")!));
                Assert.True(AdmissionLease(pair.PayerService).IsAdopted);
                if (change == "reset") pair.PayerService.ResetSessionState();
                else if (change == "throw") throw new IOException("activation_failure");
                else successor = pair.PayerLane.Current = pair.PayerLane.Current! with { ClientGeneration = 99 };
            }
            if (afterActivation) pair.Payer.SessionPreparationAfterActivationForTests = Change;
            else pair.Payer.SessionPreparationBeforeActivationForTests = Change;
            var result = await StartPreparedAdmission(pair.PayerService, pair.Token);
            Assert.True(result is null || result.State == FileTransferTransferState.Failed);
            Assert.False(AdmissionLease(pair.PayerService).IsCurrent);
            Assert.False(pair.Payer.IsAccelerationAvailableForTests);
            Assert.Null(pair.DialerService.Snapshot.Inbound);
            if (successor is not null)
            {
                Assert.Same(successor, pair.PayerLane.Current);
                Assert.Equal(0, pair.PayerLane.ExactStops);
            }
            else await pair.PayerLane.Stopped.Task.WaitAsync(pair.Token);
            Assert.Equal(0, pair.PayerLane.GlobalStops);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationAdmission")]
    public Task PreparationAdmission_ExpiryStopsExactOwnerBeforeOrAfterPublication(bool adopted)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            void Expire()
            {
                var lease = AdmissionLease(pair.PayerService);
                typeof(FileTransferSessionPreparationAdmissionLease).GetField("expiresTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(lease, DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks);
                InvokePrivateMethod(lease, "Expire");
            }
            if (!adopted) pair.Payer.SessionPreparationAfterRetentionForTests = Expire;
            var result = await StartPreparedAdmission(pair.PayerService, pair.Token);
            if (adopted)
            {
                Assert.True(AdmissionLease(pair.PayerService).IsAdopted);
                Expire();
            }
            else Assert.NotEqual("file_tuna_v4", result?.RouteToken);
            Assert.False(AdmissionLease(pair.PayerService).IsCurrent);
            await pair.PayerLane.Stopped.Task.WaitAsync(pair.Token);
            Assert.False(pair.Payer.IsAccelerationAvailableForTests);
        });

    private static FileTransferSessionPreparationAdmissionLease AdmissionLease(SessionFileTransferService service)
        => Assert.IsType<FileTransferSessionPreparationAdmissionLease>(GetPrivateField(service, "preparationAdmissionLease"));
    private static Task<FileTransferTransferSnapshot?> StartPreparedAdmission(SessionFileTransferService service, CancellationToken ct)
        => service.TryStartSendAsync(new("prepared.bin", 4, "prepared-admission"),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), ct);
}
