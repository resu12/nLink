using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("success")] [InlineData("before")] [InlineData("during")]
    public async Task BootstrapSourceHold_OutboundCapturesPhysicalSettlementAndClosesPayloadAndState(string failureTiming)
    {
        const string transferId = "bootstrap_source_hold_outbound";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        service.AttachTransport(bridge);
        var context = CreateRegularNknV4OutboundContext(transferId, remoteNextExpectedChunkIndex: 4, chunksAcceptedForTransport: 4,
            remoteGrantedUntilExclusive: 4, lastGrantReceivedUtc: DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128);
        SetPrivateProperty(context, "DataSession", session);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "V4LastStateEpoch", 3);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
        AdoptSourceFailureTunaForTest(service, context);
        var register = typeof(SessionFileTransferService).GetMethod("TryRegisterOutboundPhysicalSendLeaseLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = [context, session, 4, 1, false, "", null, null];
        Assert.True(Assert.IsType<bool>(register.Invoke(service, args)));
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = typeof(SessionFileTransferService).GetMethod("BindOutboundPhysicalSendLeaseCompletionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, args[6], physical.Task])!;
        if (failureTiming == "before") { physical.TrySetException(new IOException("modeled source failure before hold")); }
        var observing = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("ObserveOutboundPhysicalSendLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, lease, physical.Task]));
        try
        {
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
            var hold = PhysicalSourceHoldForTest(context);
            if (failureTiming != "before")
            {
                Assert.Single(hold.Capture!.OutboundPhysicalLeases);
                Assert.Null(hold.Receipt);
                Assert.Empty(bridge.Executions);
            }
            var epoch = GetPrivateProperty<int>(context, "V4LastStateEpoch");
            var state = new FileTransferStateFrameV4
            {
                SessionId = session.SessionId, TransferId = transferId, Epoch = epoch + 1,
                ContiguousCommittedChunkIndex = 5, DurableReceivedHighestChunkIndex = 7, CreditUntilChunkIndexExclusive = 10,
            };
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("ApplyOutboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, state, null, null, null, 0L]));
            Assert.Equal(epoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
            object?[] late = [context, session, 5, 1, false, "", null, null];
            Assert.False(Assert.IsType<bool>(register.Invoke(service, late)));
            Assert.Null(late[6]);
            if (failureTiming == "during") { physical.TrySetException(new IOException("modeled captured source failure")); }
            else { physical.TrySetResult(); }
            await observing.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => bridge.Executions.Count == 1, timeoutMs: 3000);
            AssertPhysicalSourceHoldSettledForTest(context, hold.Command);
            Assert.Equal(epoch, Assert.Single(bridge.Executions).Command.Bootstrap.Observation.DurableCheckpoint.StateEpoch);
        }
        finally
        {
            physical.TrySetResult();
            await observing.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var phase in bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_cleanup_no_submission");
                phase.Delivery.TrySetResult(held); phase.Settlement.TrySetResult(held);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapSourceHoldOperation PhysicalSourceHoldForTest(object context)
        => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertPhysicalSourceHoldSettledForTest(object context, FileTransferBootstrapSourceHoldCommand command)
    {
        var current = CreateCoordinatorStateForTest(context);
        Assert.NotNull(current.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt);
        Assert.False(current.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Invalidated);
        Assert.False(current.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
        Assert.Equal(command, current.PersistentState.FileTransferRouteTransition.BootstrapSourceHold.Command);
    }

    [Theory]
    [InlineData("current")] [InlineData("pause")] [InlineData("session")]
    [InlineData("provider")] [InlineData("stream")] [InlineData("terminal")]
    public async Task BootstrapSourceHold_CapturedWriteDrainsUnderOnlyItsOwnPause(string mutation)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var before = fixture.Progress();
        var writing = fixture.WriteAsync();
        var operation = Assert.Single(fixture.Operations());
        var evaluation = LocalStopEvaluationForTest(fixture.Context, fixture.Session);
        try
        {
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
            var hold = CreateCoordinatorStateForTest(fixture.Context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
            Assert.Single(hold.Capture!.InboundWrites);
            Assert.Null(hold.Receipt);
            Assert.Empty(fixture.Bridge.Executions);
            await fixture.WriteAsync(fixture.Batch() with { StartChunkIndex = before.Frontier + 1 });
            Assert.Single(fixture.Operations());
            Assert.Equal(1, fixture.Stream.Writes);
            if (mutation == "terminal")
            {
                await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("TransitionInboundToTerminalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(fixture.Service, [fixture.Context, FileTransferTransferState.Canceled, null, "test terminal", false, null, null, CancellationToken.None]));
            }
            else if (mutation != "current") { fixture.Invalidate(mutation); }
            fixture.Stream.CompleteWrite();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(BootstrapFeedbackSettledForTest(operation).IsCompletedSuccessfully);
            if (mutation != "current")
            {
                Assert.Equal(before, fixture.Progress());
                Assert.Empty(fixture.Bridge.Executions);
                Assert.Null(CreateCoordinatorStateForTest(fixture.Context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt);
                return;
            }
            await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
            var phase = Assert.Single(fixture.Bridge.Executions);
            Assert.Equal(before.Frontier + 1, phase.Command.Bootstrap.Observation.DurableCheckpoint.ContiguousCommittedChunkIndex);
            Assert.Equal(before.Epoch, GetPrivateProperty<int>(fixture.Context, "V4StateEpoch"));
            Assert.Empty(fixture.Session.SentFrames.OfType<FileTransferStateFrameV4>());
            Assert.Equal(hold.Capture, CreateCoordinatorStateForTest(fixture.Context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt!.Capture);
        }
        finally
        {
            fixture.Stream.CompleteWrite();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var phase in fixture.Bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_cleanup_no_submission");
                phase.Delivery.TrySetResult(held); phase.Settlement.TrySetResult(held);
            }
        }
    }

    [Fact]
    public async Task BootstrapSourceHold_NewPauseAfterIssuanceInvalidatesWithoutResume()
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var evaluation = LocalStopEvaluationForTest(fixture.Context, fixture.Session);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
        var phase = Assert.Single(fixture.Bridge.Executions);
        var deadline = GetPrivatePropertyValue(fixture.Context, "PullTransportGraceDeadlineUtc");
        typeof(SessionFileTransferService).GetMethod("TryPauseInboundTransportLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, "new_transport_failure", false]);
        var current = CreateCoordinatorStateForTest(fixture.Context);
        Assert.True(current.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Invalidated);
        Assert.True(current.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
        phase.Delivery.TrySetResult(ServiceBootstrapReceipt(phase.Command, settled: false));
        phase.Settlement.TrySetResult(ServiceBootstrapReceipt(phase.Command, settled: true));
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
        Assert.Single(fixture.Bridge.Executions);
        Assert.False(await SendBootstrapFeedbackForTest(fixture.Service, fixture.Context));
        Assert.Equal(deadline, GetPrivatePropertyValue(fixture.Context, "PullTransportGraceDeadlineUtc"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapSourceHold_FeedbackAdmissionGapRemainsCapturedAndSourceBound(bool replace)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests;
        SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = _ => { entered.TrySetResult(); return release.Task; };
        Task<bool>? sending = null;
        try
        {
            sending = SendBootstrapFeedbackForTest(fixture.Service, fixture.Context);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operation = Assert.Single(BootstrapFeedbackOperationsForTest(fixture.Context));
            Assert.Null(GetPrivatePropertyValue(operation, "Submission"));
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, LocalStopEvaluationForTest(fixture.Context, fixture.Session));
            Assert.Empty(fixture.Bridge.Executions);
            Assert.Single(CreateCoordinatorStateForTest(fixture.Context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Capture!.InboundFeedback);
            var epoch = GetPrivateProperty<int>(fixture.Context, "V4StateEpoch");
            Assert.False(await SendBootstrapFeedbackForTest(fixture.Service, fixture.Context));
            Assert.Equal(epoch, GetPrivateProperty<int>(fixture.Context, "V4StateEpoch"));
            if (replace) { fixture.ReplaceSession(); }
            release.TrySetResult();
            Assert.Equal(!replace, await sending.WaitAsync(TimeSpan.FromSeconds(3)));
            await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(3));
            if (replace)
            {
                Assert.Empty(fixture.Bridge.Executions);
                Assert.Empty(fixture.Replacement.SentFrames);
                Assert.Empty(fixture.Session.SentFrames);
            }
            else
            {
                await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
                Assert.Single(fixture.Session.SentFrames.OfType<FileTransferStateFrameV4>());
            }
        }
        finally
        {
            release.TrySetResult();
            if (sending is not null) { await sending.WaitAsync(TimeSpan.FromSeconds(3)); }
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = previous;
            foreach (var phase in fixture.Bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_cleanup_no_submission");
                phase.Delivery.TrySetResult(held); phase.Settlement.TrySetResult(held);
            }
        }
    }

    [Fact]
    public async Task BootstrapSourceHold_AdmittedWriteCannotCommitAfterDataSessionReplacement()
    {
        const string transferId = "bootstrap_source_hold_stale_write";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var replacement = new InitialBootstrapRecordingDataSession(session.SessionId, transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        using var stream = new BlockingSparseWriteStream();
        SetPrivateProperty(context, "WriteStream", stream);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        var frontier = GetPrivateProperty<int>(context, "NextChunkIndex");
        var bytes = GetPrivateProperty<long>(context, "BytesTransferred");
        var epoch = GetPrivateProperty<int>(context, "V4StateEpoch");
        var batch = new FileTransferChunkBatchFrameV4
        {
            SessionId = session.SessionId, TransferId = transferId, StartChunkIndex = frontier, ChunkCount = 1,
            DataSegments = [new byte[GetPrivateProperty<int>(context, "ChunkSizeBytes")]],
        };
        var writing = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("HandleInboundV4ChunkBatchFromReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, batch, FileTransferTransportKind.Tuna,
                typeof(SessionFileTransferService).GetMethod("CaptureInboundBootstrapWriteIngress", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [context, session])]));
        try
        {
            await stream.WaitForWriteStartedAsync();
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
            Assert.Empty(bridge.Executions);
            Assert.Contains(frontier, GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite"));
            SetPrivateProperty(context, "DataSession", replacement);
            SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1);
            stream.ReleaseWrites();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(GetPrivateProperty<int>(context, "NextChunkIndex") == frontier &&
                GetPrivateProperty<long>(context, "BytesTransferred") == bytes && GetPrivateProperty<int>(context, "V4StateEpoch") == epoch,
                $"stale_write_mutated_source: frontier={frontier}->{GetPrivateProperty<int>(context, "NextChunkIndex")}; " +
                $"bytes={bytes}->{GetPrivateProperty<long>(context, "BytesTransferred")}; state_epoch={epoch}->{GetPrivateProperty<int>(context, "V4StateEpoch")}");
            Assert.Empty(bridge.Executions);
        }
        finally
        {
            stream.ReleaseWrites();
            await writing.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task BootstrapSourceHold_WaitsForAlreadyAdmittedReceiverFeedbackBeforeIssuingPhase()
    {
        await RunBootstrapSourceHoldFeedbackBoundaryAsync(feedbackBeforeBootstrap: true);
    }

    [Fact]
    public async Task BootstrapSourceHold_ClosesFeedbackConstructionBeforePhaseCallback()
    {
        await RunBootstrapSourceHoldFeedbackBoundaryAsync(feedbackBeforeBootstrap: false);
    }

    private static async Task RunBootstrapSourceHoldFeedbackBoundaryAsync(bool feedbackBeforeBootstrap)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync();
        var context = fixture.Context;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.BeforeSendAsyncForTest = (frame, _) =>
        {
            if (frame is not FileTransferStateFrameV4) { return Task.CompletedTask; }
            entered.TrySetResult();
            return release.Task;
        };
        var evaluation = LocalStopEvaluationForTest(context, fixture.Session);
        Task<bool>? sending = null;
        try
        {
            if (feedbackBeforeBootstrap)
            {
                sending = SendBootstrapFeedbackForTest(fixture.Service, context);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
                Assert.Empty(fixture.Bridge.Executions);
                var hold = CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
                Assert.Single(hold.Capture!.InboundFeedback);
                Assert.Null(hold.Receipt);
                Assert.False(sending.IsCompleted);
                release.TrySetResult();
                await sending.WaitAsync(TimeSpan.FromSeconds(3));
            }
            else
            {
                await ProcessInitialPeerFirstBootstrapEvaluationAsync(fixture.Service, fixture.Bridge, evaluation);
                var epoch = GetPrivateProperty<int>(context, "V4StateEpoch");
                Assert.False(await SendBootstrapFeedbackForTest(fixture.Service, context));
                Assert.False(entered.Task.IsCompleted);
                Assert.Equal(epoch, GetPrivateProperty<int>(context, "V4StateEpoch"));
            }
            await WaitUntilAsync(() => fixture.Bridge.Executions.Count == 1, timeoutMs: 3000);
            var execution = Assert.Single(fixture.Bridge.Executions);
            var issued = execution.Command.Bootstrap.Observation.DurableCheckpoint;
            var current = CreateCoordinatorStateForTest(context);
            Assert.Equal(issued, current.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt!.DurableCheckpoint);
            Assert.Equal(GetPrivateProperty<int>(context, "V4StateEpoch"), issued.StateEpoch);
            Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
            Assert.NotNull(GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc"));
            execution.Delivery.TrySetResult(ServiceBootstrapReceipt(execution.Command, settled: false));
            execution.Settlement.TrySetResult(ServiceBootstrapReceipt(execution.Command, settled: true));
            await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.IsReady, timeoutMs: 3000);
            Assert.Equal(issued, CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Bootstrap.Observation.DurableCheckpoint);
        }
        finally
        {
            release.TrySetResult();
            foreach (var execution in fixture.Bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(execution.Command,
                    FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_test_cleanup_no_submission");
                execution.Delivery.TrySetResult(held); execution.Settlement.TrySetResult(held);
            }
            if (sending is not null) { await sending.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }
}
