using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapReceiverHold_ProductionFeedbackSettlementGatesPeerResponse(bool secondaryFails)
    {
        FakeNknClient.ResetNetwork();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var localClient = new FakeNknClient("local.receiver-hold.address");
            var peerClient = new FakeNknClient("peer.receiver-hold.address");
            using var local = new NknSignalingTransport(localClient, options, new NknIdentity("local-receiver-hold", localClient.Address));
            using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer-receiver-hold", peerClient.Address));
            var sessionId = await ApproveNknSessionAsync(local, peer, timeout.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            using var fixture = await BootstrapWriteFixture.CreateAsync(sessionId: sessionId);
            using var wireSession = await local.OpenFileTransferDataSessionAsync(sessionId, fixture.Session.TransferId, timeout.Token);
            var physical = Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(wireSession);
            localClient.BeforeSendCoreAsync = async (_, _, lane, _) =>
            {
                if (lane != NknBridgeChannel.Bulk) { return; }
                entered.TrySetResult();
                await release.Task;
                if (secondaryFails) { throw new IOException("modeled secondary feedback failure"); }
            };
            // Join real redundant-send settlement to Core's write/feedback lifecycle.
            // The fixture's phase recorder observes issuance; this is not live route acceptance.
            fixture.Session.PhysicalSubmissionForTest = (frame, ct) => physical.SubmitPhysicalSend(frame, null, ct);
            object? feedback = null;
            try
            {
                await fixture.WriteAsync().WaitAsync(timeout.Token);
                await entered.Task.WaitAsync(timeout.Token);
                Assert.Empty(fixture.Operations());
                feedback = Assert.Single(BootstrapFeedbackOperationsForTest(fixture.Context));
                var submission = GetPrivateProperty<FileTransferPhysicalSendSubmission>(feedback, "Submission");
                Assert.True(submission.DeliveryTask.IsCompletedSuccessfully);
                Assert.False(submission.PhysicalCompletionTask.IsCompleted);
                await RequestReceiverHoldForTest(fixture, true, ReceiverHoldPeerRequestForTest(fixture));
                Assert.Single(PhysicalSourceHoldForTest(fixture.Context).Capture!.InboundFeedback);
                Assert.Null(PhysicalSourceHoldForTest(fixture.Context).Receipt);
                Assert.Empty(fixture.Bridge.Executions);
                release.TrySetResult();
                await BootstrapFeedbackSettledForTest(feedback).WaitAsync(timeout.Token);
                await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
                Assert.Equal(FileTransferBootstrapPhase.Response, Assert.Single(fixture.Bridge.Executions).Command.Phase);
                AssertPhysicalSourceHoldSettledForTest(fixture.Context, PhysicalSourceHoldForTest(fixture.Context).Command);
            }
            finally
            {
                release.TrySetResult();
                if (feedback is not null) { await BootstrapFeedbackSettledForTest(feedback).WaitAsync(timeout.Token); }
                CompleteReceiverHoldPhasesForTest(fixture);
            }
        }
        finally { release.TrySetResult(); FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(false, "write", false)] [InlineData(true, "write", false)]
    [InlineData(false, "write", true)] [InlineData(true, "write", true)]
    [InlineData(false, "settled", false)] [InlineData(true, "settled", false)]
    [InlineData(false, "standalone", false)] [InlineData(true, "standalone", false)]
    public async Task BootstrapReceiverHold_CapturesFeedbackAfterWriteFinalization(bool peer, string kind, bool physicalFailure)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (kind == "settled") { physical.SetResult(); }
        fixture.Session.PhysicalSubmissionForTest = (_, _) => new(Task.CompletedTask, physical.Task);
        object? feedback = null;
        try
        {
            if (kind == "standalone") { Assert.True(await SendBootstrapFeedbackForTest(fixture.Service, fixture.Context)); }
            else { await fixture.WriteAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            Assert.Empty(fixture.Operations());
            var progress = fixture.Progress();
            var before = CreateCoordinatorStateForTest(fixture.Context);
            if (kind != "settled")
            {
                feedback = Assert.Single(BootstrapFeedbackOperationsForTest(fixture.Context));
                Assert.False(BootstrapFeedbackSettledForTest(feedback).IsCompleted);
                if (kind == "write")
                {
                    var source = GetPrivatePropertyValue(feedback, "WriteSource")!;
                    Assert.True(BootstrapFeedbackSettledForTest(GetPrivatePropertyValue(source, "Operation")!).IsCompletedSuccessfully);
                    // Draining a retired write's feedback must not restore ordinary callback authority.
                    Assert.False(ReceiverFeedbackCurrentForTest(fixture, feedback));
                }
            }
            var request = ReceiverHoldPeerRequestForTest(fixture);
            await RequestReceiverHoldForTest(fixture, peer, request);
            var hold = PhysicalSourceHoldForTest(fixture.Context);
            Assert.NotNull(hold);
            Assert.False(hold.Invalidated);
            Assert.NotNull(hold.Capture);
            Assert.Empty(hold.Capture.InboundWrites);
            Assert.Equal(peer ? request : null, hold.Command.PeerWake);
            if (feedback is not null)
            {
                Assert.Equal(GetPrivateProperty<long>(feedback, "Generation"), Assert.Single(hold.Capture.InboundFeedback));
                Assert.Null(hold.Receipt);
                Assert.Empty(fixture.Bridge.Executions);
                await RequestReceiverHoldForTest(fixture, peer, request);
                Assert.Empty(fixture.Bridge.Executions);
                Assert.False(await SendBootstrapFeedbackForTest(fixture.Service, fixture.Context));
            }
            if (physicalFailure) { physical.TrySetException(new IOException("modeled secondary feedback failure")); }
            else { physical.TrySetResult(); }
            if (feedback is not null) { await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3)); }
            await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
            var phase = Assert.Single(fixture.Bridge.Executions);
            Assert.Equal(peer ? FileTransferBootstrapPhase.Response : FileTransferBootstrapPhase.Announce, phase.Command.Phase);
            AssertPhysicalSourceHoldSettledForTest(fixture.Context, hold.Command);
            Assert.Equal(progress, fixture.Progress());
            Assert.Equal(before.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(fixture.Context).CurrentRoutePublicationToken);
            Assert.Equal(before.CurrentRuntimeOwner, CreateCoordinatorStateForTest(fixture.Context).CurrentRuntimeOwner);
            Assert.Empty(BootstrapFeedbackOperationsForTest(fixture.Context));
        }
        finally
        {
            physical.TrySetResult();
            if (feedback is not null) { await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3)); }
            CompleteReceiverHoldPhasesForTest(fixture);
        }
    }

    [Theory]
    [InlineData("write")] [InlineData("feedback")] [InlineData("pending_chunk")] [InlineData("unowned_write")]
    public async Task BootstrapReceiverHold_UnboundWorkDoesNotPoisonNextPeerRequest(string kind)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: kind == "write");
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.PhysicalSubmissionForTest = (_, _) => new(Task.CompletedTask, physical.Task);
        Task? writing = null;
        object? feedback = null;
        var pending = GetPrivateProperty<SortedDictionary<int, byte[]>>(fixture.Context, "PendingChunks");
        var unowned = GetPrivateProperty<HashSet<int>>(fixture.Context, "ReceiverSparseChunksPendingWrite");
        try
        {
            if (kind == "write") { writing = fixture.WriteAsync(); await fixture.Stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (kind == "feedback")
            {
                await fixture.WriteAsync().WaitAsync(TimeSpan.FromSeconds(3));
                feedback = Assert.Single(BootstrapFeedbackOperationsForTest(fixture.Context));
            }
            if (kind is "write" or "feedback") { fixture.Invalidate("pause"); }
            if (kind == "pending_chunk") { pending.Add(99, new byte[1]); }
            if (kind == "unowned_write") { unowned.Add(99); }
            var progress = fixture.Progress();
            var request = ReceiverHoldPeerRequestForTest(fixture);
            await RequestReceiverHoldForTest(fixture, true, request);
            Assert.Null(CreateCoordinatorStateForTest(fixture.Context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold);
            Assert.Null(GetPrivatePropertyValue(fixture.Context, "BootstrapSourceHoldExecution"));
            Assert.Null(GetPrivatePropertyValue(fixture.Context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
            Assert.Empty(fixture.Bridge.Executions);
            physical.TrySetResult(); fixture.Stream.CompleteWrite();
            if (writing is not null) { await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (feedback is not null) { await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3)); }
            pending.Clear(); unowned.Clear();
            Assert.Empty(fixture.Bridge.Executions); // Stale finalization is not an authorized wake.
            Assert.Equal(progress, fixture.Progress());
            await RequestReceiverHoldForTest(fixture, true, request);
            await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
            AssertPhysicalSourceHoldSettledForTest(fixture.Context, PhysicalSourceHoldForTest(fixture.Context).Command);
            Assert.Equal(request, PhysicalSourceHoldForTest(fixture.Context).Command.PeerWake);
        }
        finally
        {
            physical.TrySetResult(); fixture.Stream.CompleteWrite(); pending.Clear(); unowned.Clear();
            if (writing is not null) { await writing.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (feedback is not null) { await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3)); }
            CompleteReceiverHoldPhasesForTest(fixture);
        }
    }

    public static IEnumerable<object[]> ReceiverHoldStaleFeedbackSources()
        => new[] { "session", "provider", "service", "stream", "layout", "agreement", "pause", "context", "publication", "shape", "terminal", "cancel", "user_pause", "peer_pause" }
            .SelectMany(source => new[] { new object[] { source, false }, new object[] { source, true } });

    [Theory]
    [MemberData(nameof(ReceiverHoldStaleFeedbackSources))]
    public async Task BootstrapReceiverHold_RetiredWriteFeedbackCannotCrossSourceChange(string mutation, bool afterCapture)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.PhysicalSubmissionForTest = (_, _) => new(Task.CompletedTask, physical.Task);
        await fixture.WriteAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var feedback = Assert.Single(BootstrapFeedbackOperationsForTest(fixture.Context));
        var progress = fixture.Progress();
        var evaluation = LocalStopEvaluationForTest(fixture.Context, fixture.Session);
        try
        {
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod("IsInboundBootstrapFeedbackDrainCaptureCurrentLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Service, [fixture.Context, feedback])));
            Assert.False(ReceiverFeedbackCurrentForTest(fixture, feedback));
            if (afterCapture)
            {
                await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
                Assert.NotNull(PhysicalSourceHoldForTest(fixture.Context).Capture);
                Assert.Null(PhysicalSourceHoldForTest(fixture.Context).Receipt);
            }
            switch (mutation)
            {
                case "context":
                    typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, null);
                    break;
                case "publication": SetPrivateProperty(fixture.Context, "CurrentRoutePublicationToken", null); break;
                case "shape": SetPrivateProperty(fixture.Context, "ChunkSizeBytes", GetPrivateProperty<int>(fixture.Context, "ChunkSizeBytes") + 1); break;
                case "terminal": SetPrivateProperty(fixture.Context, "State", FileTransferTransferState.Canceled); break;
                case "cancel": TryCancelContextLifetime(fixture.Context); break;
                case "user_pause": SetPrivateProperty(fixture.Context, "UserPaused", true); break;
                case "peer_pause": SetPrivateProperty(fixture.Context, "PeerPaused", true); break;
                default: fixture.Invalidate(mutation); break;
            }
            Assert.False(Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod("IsInboundBootstrapFeedbackDrainCaptureCurrentLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Service, [fixture.Context, feedback])));
            Assert.False(ReceiverFeedbackCurrentForTest(fixture, feedback));
            if (afterCapture)
            {
                Assert.False(Assert.IsType<bool>(typeof(SessionFileTransferService)
                    .GetMethod("IsBootstrapSourceHoldExecutionCurrentLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(fixture.Service, [GetPrivatePropertyValue(fixture.Context, "BootstrapSourceHoldExecution")])));
            }
            else
            {
                await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
                Assert.Null(PhysicalSourceHoldForTest(fixture.Context));
            }
            Assert.False(BootstrapFeedbackSettledForTest(feedback).IsCompleted); // Cancellation is not physical settlement.
            var publication = GetPrivatePropertyValue(fixture.Context, "CurrentRoutePublicationToken");
            physical.TrySetException(new IOException("late stale physical failure"));
            await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(BootstrapFeedbackOperationsForTest(fixture.Context));
            Assert.Empty(fixture.Bridge.Executions);
            Assert.Empty(fixture.Replacement.SentFrames);
            Assert.Equal(progress, fixture.Progress());
            Assert.Equal(publication, GetPrivatePropertyValue(fixture.Context, "CurrentRoutePublicationToken"));
            Assert.Null(PhysicalSourceHoldForTest(fixture.Context)?.Receipt);
        }
        finally
        {
            physical.TrySetResult();
            await BootstrapFeedbackSettledForTest(feedback).WaitAsync(TimeSpan.FromSeconds(3));
            CompleteReceiverHoldPhasesForTest(fixture);
        }
    }

    private static FileTransferFallbackBootstrapRequestFrameV6 ReceiverHoldPeerRequestForTest(BootstrapWriteFixture fixture)
    {
        var state = CreateCoordinatorStateForTest(fixture.Context);
        return new()
        {
            SessionId = fixture.Session.SessionId, TransferId = fixture.Session.TransferId,
            TransportEpoch = state.CurrentLeg!.TransportEpochId + 1,
            SenderRequestId = $"fbc1:{BootstrapPeerIssuerForTest(state)}:371:{new string('b', 64)}",
        };
    }

    private Task RequestReceiverHoldForTest(BootstrapWriteFixture fixture, bool peer, FileTransferFallbackBootstrapRequestFrameV6 request)
    {
        if (!peer) { return ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, LocalStopEvaluationForTest(fixture.Context, fixture.Session)); }
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryHandleBootstrapCorrelationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [FileTransferDirection.Inbound, fixture.Session,
                new FileTransferReceivedDataFrame(request, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)])));
        return Task.CompletedTask;
    }

    private static bool ReceiverFeedbackCurrentForTest(BootstrapWriteFixture fixture, object feedback)
        => Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("IsInboundBootstrapFeedbackCurrentLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, feedback]));

    private static void CompleteReceiverHoldPhasesForTest(BootstrapWriteFixture fixture)
    {
        foreach (var phase in fixture.Bridge.Executions)
        {
            var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "test_cleanup_no_submission");
            phase.Delivery.TrySetResult(held); phase.Settlement.TrySetResult(held);
        }
    }
}
