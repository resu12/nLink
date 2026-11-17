using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BootstrapSourceHold_InstallerMustYieldWithUnspentCredit(bool unspentCredit)
        => RunHeldBootstrapPumpForTest(unspentCredit, "none");

    [Theory]
    [InlineData("spurious_signal")]
    [InlineData("queued_repair")]
    [InlineData("terminal_ready")]
    [InlineData("new_pause")]
    [InlineData("session")]
    [InlineData("provider")]
    [InlineData("terminal")]
    [InlineData("selection_to_submission")]
    public Task BootstrapSourceHold_PumpWaitCannotDispatchHeldWork(string scenario)
        => RunHeldBootstrapPumpForTest(true, scenario);

    private async Task RunHeldBootstrapPumpForTest(bool unspentCredit, string scenario)
    {
        var transferId = $"bootstrap_hold_pump_rca_{unspentCredit}_{scenario}";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        service.AttachTransport(bridge);
        var acceptedBefore = scenario == "queued_repair" ? 8 : 4;
        var context = CreateRegularNknV4OutboundContext(transferId, 4, acceptedBefore, unspentCredit ? 8 : 4,
            DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128);
        SetPrivateProperty(context, "DataSession", session);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "V4LastStateEpoch", 3);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
        var history = FileTransferCoordinatorTests.CreateCorrelatedOutboundServiceSuccessorHistory(CreateCoordinatorStateForTest(context));
        SetPrivateProperty(context, "CoordinatorPersistentState", history.State.PersistentState);
        typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, [context, history, false, false]);
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        StartCoordinatorSnapshotStreamForTest(context);
        if (scenario == "queued_repair")
        {
            var state = new FileTransferStateFrameV4
            {
                SessionId = session.SessionId, TransferId = transferId, Epoch = 3,
                ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 6,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 1 }],
            };
            typeof(SessionFileTransferService).GetMethod("EnqueueV4RepairsFromState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, state]);
            Assert.NotEmpty(GetQueuedV4RepairSends(context));
        }
        if (scenario == "terminal_ready")
        {
            acceptedBefore = 128;
            SetPrivateProperty(context, "V4TerminalReady", true);
            SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 128);
            SetPrivateProperty(context, "ChunksAcceptedForTransport", 128);
            SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 127);
            SetPrivateProperty(context, "BytesAcknowledgedByReceiver", 128L * 21 * 1024);
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod("ShouldCompleteOutboundV4FromTerminalReadyStateLocked", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context])));
        }
        var delayedHold = scenario == "selection_to_submission";
        if (!delayedHold)
        {
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
        }
        var holdGate = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "HasBootstrapSourceHoldLocked" && method.GetParameters()[0].ParameterType == context.GetType());
        Assert.Equal(!delayedHold, Assert.IsType<bool>(holdGate.Invoke(service, [context])));
        var pauseRecognized = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("ShouldPauseOutboundV4SenderPumpForTransportPauseLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context]));
        var previous = SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests;
        var previousIdle = SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests;
        var idleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextIteration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterIdle = false;
        var queuedBefore = GetQueuedV4RepairSends(context).ToArray();
        var deadlineBefore = GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
        using var blockedSource = new BootstrapPumpReadGate(new byte[128 * 21 * 1024]);
        var iterationsBeforeReturn = 0;
        var installerReturned = false;
        var forcedDiagnosticStop = false;
        string? inlineStack = null;
        object? runtime = null;
        try
        {
            // Bound a possible synchronous spin using an existing test-only hook. The hook
            // observes the first seven iterations unchanged; it is not production completion proof.
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = (id, _) =>
            {
                if (id == transferId && afterIdle) { nextIteration.TrySetResult(); }
                if (id != transferId || installerReturned) { return false; }
                if (++iterationsBeforeReturn == 2) { inlineStack = Environment.StackTrace; }
                forcedDiagnosticStop = iterationsBeforeReturn >= 8;
                return forcedDiagnosticStop;
            };
            SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests = (id, _, stage) =>
            {
                if (id != transferId || stage != "idle_delay") { return Task.CompletedTask; }
                idleReached.TrySetResult();
                return releaseIdle.Task;
            };
            var installing = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("TryInstallOutboundSparsePumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, null, delayedHold ? blockedSource : null, !delayedHold, "bootstrap_hold_pump_rca", CancellationToken.None]));
            installerReturned = true;
            await installing.WaitAsync(TimeSpan.FromSeconds(3));
            runtime = installing.GetType().GetProperty("Result")!.GetValue(installing);
            Assert.NotNull(runtime);
            if (delayedHold)
            {
                await blockedSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
                deadlineBefore = GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
                blockedSource.Release.TrySetResult();
            }
            Assert.Single(bridge.Executions);
            Assert.NotNull(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt);
            Assert.Empty(session.SentFrames.OfType<FileTransferChunkBatchFrameV4>());
            Assert.Equal(acceptedBefore, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
            Assert.False(forcedDiagnosticStop,
                $"Installer required diagnostic stop after {iterationsBeforeReturn} inline pump iterations; " +
                $"hold=true; pause_policy={pauseRecognized}; unspent_credit={unspentCredit}; accepted_chunks={acceptedBefore}; payload_frames=0.\n{inlineStack}");
            Assert.False(Assert.IsAssignableFrom<Task>(GetPrivatePropertyValue(runtime, "PumpTask")).IsCompleted);
            await idleReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var readsAtIdle = blockedSource.Reads;
            lock (GetServiceGate(service))
            {
                if (scenario == "new_pause")
                {
                    typeof(SessionFileTransferService).GetMethod("TryPauseOutboundTransportLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(service, [context, "new_transport_failure", false]);
                }
                if (scenario == "session") { SetPrivateProperty(context, "DataSessionGeneration", 18L); }
                if (scenario == "provider") { session.Generation++; }
                if (scenario == "terminal") { SetPrivateProperty(context, "State", FileTransferTransferState.Canceled); }
                if (scenario == "spurious_signal")
                {
                    context.GetType().GetMethod("SignalSparseSenderPump")!.Invoke(context, null);
                }
                afterIdle = true;
            }
            releaseIdle.TrySetResult();
            var pump = Assert.IsAssignableFrom<Task>(GetPrivatePropertyValue(runtime, "PumpTask"));
            if (scenario is "session" or "terminal") { await pump.WaitAsync(TimeSpan.FromSeconds(3)); }
            else
            {
                await nextIteration.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(pump.IsCompleted);
            }
            Assert.Empty(session.SentFrames.OfType<FileTransferChunkBatchFrameV4>());
            Assert.Equal(acceptedBefore, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
            Assert.Equal(queuedBefore, GetQueuedV4RepairSends(context));
            if (scenario != "new_pause") { Assert.Equal(deadlineBefore, GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc")); }
            Assert.Single(bridge.Executions);
            if (delayedHold)
            {
                // The already-selected batch may finish reading before physical admission;
                // the hold must prevent submission and any new selection after that batch.
                Assert.InRange(readsAtIdle, 1, 8 - acceptedBefore);
                Assert.Equal(readsAtIdle, blockedSource.Reads);
            }
        }
        finally
        {
            installerReturned = true;
            releaseIdle.TrySetResult();
            blockedSource.Release.TrySetResult();
            TryCancelContextLifetime(context);
            if (runtime is not null)
            {
                try { await Assert.IsAssignableFrom<Task>(GetPrivatePropertyValue(runtime, "PumpTask")).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
                GetPrivateProperty<CancellationTokenSource>(runtime, "Cancellation").Dispose();
                await Assert.IsAssignableFrom<Stream>(GetPrivatePropertyValue(runtime, "Stream")).DisposeAsync();
            }
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = previous;
            SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests = previousIdle;
            foreach (var phase in bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "rca_cleanup_no_submission");
                phase.Delivery.TrySetResult(held);
                phase.Settlement.TrySetResult(held);
            }
        }
    }

    private sealed class BootstrapPumpReadGate(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reads++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await base.ReadAsync(buffer, ct);
        }
    }
}
