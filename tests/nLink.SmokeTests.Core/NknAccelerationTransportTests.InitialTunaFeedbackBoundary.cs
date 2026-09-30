using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFeedbackBoundary")]
    public Task PreparedInitialTuna_CurrentPublicationDeliversInitialCreditAndPayload(bool helperPays, bool payerSends)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            pair.PayerLane.FilePeer = pair.DialerLane;
            pair.DialerLane.FilePeer = pair.PayerLane;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var sender = payerSends ? pair.PayerService : pair.DialerService;
            var receiver = payerSends ? pair.DialerService : pair.PayerService;
            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(sender, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            using var destination = new MemoryStream();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prior = SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests;
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = id =>
            {
                Assert.Equal("prepared-admission", id);
                entered.TrySetResult();
                return release.Task;
            };
            try
            {
                Assert.NotNull(await receiver.AcceptIncomingTransferAsync("prepared-admission",
                    (_, _) => Task.FromResult<Stream>(destination), pair.Token));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var inbound = GetPrivateField(receiver, "inboundTransfer")!;
                var outbound = GetPrivateField(sender, "outboundTransfer")!;
                lock (GetPrivateField(receiver, "gate")!)
                {
                    var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound);
                    var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                    Assert.Equal((FileTransferRoute.FileTunaV4, 4, 0L, 1),
                        (publication.Route, publication.ProtocolVersion, publication.LiveRouteEpoch, publication.PublicationGeneration));
                    Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
                    Assert.Null(state.CurrentRouteCommit); // Legitimate initial publication, not a route transition.
                    Assert.True(state.PersistentState.FileTransferRouteTransition.BootstrapAgreement!.IsEnabled);
                    Assert.False((bool)Value(inbound, "PullTransportPaused")!);
                    Assert.False((bool)Value(inbound, "UserPaused")!);
                    Assert.False((bool)InvokePrivateMethod(receiver, "HasBootstrapSourceHoldLocked", inbound)!);
                    Assert.True((bool)InvokePrivateMethod(receiver, "UsesInboundBootstrapFeedbackTrackingLocked", inbound)!);
                    var session = Value(inbound, "DataSession")!;
                    Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(session);
                    Assert.True(Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(session).Generation > 0);
                    Assert.False(string.IsNullOrEmpty(state.CurrentDataSessionIdentity));
                    Assert.NotNull(state.CurrentLeg);
                    Assert.Equal(0, destination.Length);
                }
                release.TrySetResult();
                await WaitUntilAsync(() => (int)Value(outbound, "PullV4StateReceivedCountTotal")! > 0 &&
                    (int)Value(outbound, "ChunksAcceptedForTransport")! > 0 &&
                    receiver.Snapshot.Inbound?.BytesTransferred == 4, TimeSpan.FromSeconds(3));
                Assert.Equal(new byte[4], destination.ToArray());
                Assert.True((int)Value(outbound, "ChunksAcceptedForTransport")! > 0);
                Assert.Equal("file_tuna_v4", receiver.Snapshot.Inbound!.RouteToken);
            }
            finally
            {
                release.TrySetResult();
                SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = prior;
                await sender.CancelTransferAsync("prepared-admission", "test_complete", pair.Token);
            }

            static object? Value(object context, string name) => context.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFeedbackBoundary")]
    public Task PreparedInitialTuna_HeldFeedbackCannotPublishAfterReset(bool payerSends)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            pair.PayerLane.FilePeer = pair.DialerLane;
            pair.DialerLane.FilePeer = pair.PayerLane;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var sender = payerSends ? pair.PayerService : pair.DialerService;
            var receiver = payerSends ? pair.DialerService : pair.PayerService;
            await StartPreparedAdmission(sender, pair.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            using var destination = new MemoryStream();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prior = SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests;
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = _ => { entered.TrySetResult(); return release.Task; };
            try
            {
                await receiver.AcceptIncomingTransferAsync("prepared-admission", (_, _) => Task.FromResult<Stream>(destination), pair.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var inbound = GetPrivateField(receiver, "inboundTransfer")!;
                object[] operations;
                lock (GetPrivateField(receiver, "gate")!)
                    operations = ((System.Collections.IDictionary)Value(inbound, "ActiveBootstrapFeedbackOperations")!).Values.Cast<object>().ToArray();
                Assert.NotEmpty(operations);
                receiver.ResetSessionState();
                release.TrySetResult();
                await Task.WhenAll(operations.Select(operation => ((TaskCompletionSource)Value(operation, "Settled")!).Task))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Null(receiver.Snapshot.Inbound);
                Assert.Empty(destination.ToArray());
                Assert.Equal(0, (int)Value(inbound, "PullV4StateSentCountTotal")!);
                Assert.False(AdmissionLease(receiver).IsCurrent);
            }
            finally
            {
                release.TrySetResult();
                SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = prior;
            }
            static object? Value(object context, string name) => context.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);
        });
}
