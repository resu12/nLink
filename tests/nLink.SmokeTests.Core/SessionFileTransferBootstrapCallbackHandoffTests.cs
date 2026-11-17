using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    private async Task AssertCallbackRetiredAfterSupervisorHandoffAsync(SessionFileTransferService service, object context,
        SourceFailureDataSession session, BootstrapPhaseTestBridge bridge, FileTransferFallbackBootstrapStateFrameV6 peerWake,
        object callback, TaskCompletionSource release,
        Func<int> sourceOpens, bool nextStopBeforeRetirement = false, int callbackLogStart = 0, string? recoveryOrder = null)
    {
        var source = GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority").Source;
        var solicit = Assert.Single(bridge.Executions);
        session.Receive(peerWake with { SenderRequestId = solicit.Command.Frame.SenderRequestId });
        solicit.Delivery.SetResult(ServiceBootstrapReceipt(solicit.Command, settled: false));
        solicit.Settlement.SetResult(ServiceBootstrapReceipt(solicit.Command, settled: true));
        await CoreSmokeTestsBase.WaitUntilAsync(() => bridge.Executions.Count == 2, TimeSpan.FromSeconds(3));
        var confirm = bridge.Executions[1];
        Assert.Equal(FileTransferBootstrapPhase.Confirm, confirm.Command.Phase);
        confirm.Delivery.SetResult(ServiceBootstrapReceipt(confirm.Command, settled: false));
        confirm.Settlement.SetResult(ServiceBootstrapReceipt(confirm.Command, settled: true));
        await CoreSmokeTestsBase.WaitUntilAsync(() => CallbackSuccessorPublishedForTest(service, context, source), TimeSpan.FromSeconds(3));
        await CoreSmokeTestsBase.WaitUntilAsync(() => sourceOpens() == 2, TimeSpan.FromSeconds(3));
        await CoreSmokeTestsBase.WaitUntilAsync(() => CallbackSuccessorDispositionAppliedForTest(service, context), TimeSpan.FromSeconds(3));
        Assert.Single(((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Values);
        var originalCapture = GetPrivatePropertyValue(callback, "Capture");
        if (recoveryOrder is not null)
        {
            await AssertCallbackRecoveryHandoffAsync(service, context, session, callback, release, sourceOpens,
                recoveryOrder == "retired-recovery-before", callbackLogStart);
            return;
        }
        if (nextStopBeforeRetirement)
        {
            ReactivateCallbackServiceForTest(service, context);
            await CoreSmokeTestsBase.WaitUntilAsync(() => sourceOpens() == 3, TimeSpan.FromSeconds(3));
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
            Assert.True(SourceFailureHoldCapturedForTest(service, context));
            Assert.DoesNotContain(GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority"),
                PhysicalSourceHoldForTest(context).Capture!.OutboundCallbacks);
        }
        var beforeRetirement = SourceFailureEffectsForTest(context);
        release.SetResult();
        await CoreSmokeTestsBase.WaitUntilAsync(() =>
            ((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Count == 0,
            TimeSpan.FromSeconds(3));
        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.Equal(nextStopBeforeRetirement ? 3 : 2, sourceOpens());
        Assert.Equal(beforeRetirement, SourceFailureEffectsForTest(context));
        Assert.Same(originalCapture, GetPrivatePropertyValue(callback, "Capture"));
        Assert.DoesNotContain(session.SentFrames, frame => frame is FileTransferErrorFrameV4);
        var beforeDuplicate = SourceFailureEffectsForTest(context);
        lock (GetServiceGate(service))
        {
            typeof(SessionFileTransferService).GetMethod("RetireBootstrapSourceCallbackLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, callback, "none"]);
        }
        Assert.Equal(beforeDuplicate, SourceFailureEffectsForTest(context));
        Assert.Empty((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!);
        if (nextStopBeforeRetirement)
        {
            var lease = GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority").LeaseGeneration;
            await CoreSmokeTestsBase.WaitUntilAsync(() => ReadCallbackPayloadsForTest(callbackLogStart).Any(payload =>
                payload.GetProperty("stage").GetString() == "Consumed" && payload.GetProperty("leaseGeneration").GetInt64() == lease),
                TimeSpan.FromSeconds(3));
            var payloads = ReadCallbackPayloadsForTest(callbackLogStart);
            var consumed = Assert.Single(payloads.Where(payload => payload.GetProperty("stage").GetString() == "Consumed" &&
                payload.GetProperty("leaseGeneration").GetInt64() == lease));
            var fingerprint = consumed.GetProperty("callbackFingerprint").GetString();
            var captures = payloads.Where(payload => payload.GetProperty("stage").GetString() == "Capture" &&
                payload.GetProperty("capturedCallbacks").EnumerateArray().Any(id => id.GetString() == fingerprint)).ToArray();
            Assert.NotEmpty(captures);
            Assert.All(captures, capture =>
            {
                Assert.Equal(capture.GetProperty("holdCommandFingerprint").GetString(), consumed.GetProperty("holdCommandFingerprint").GetString());
                Assert.Equal(capture.GetProperty("captureFingerprint").GetString(), consumed.GetProperty("captureFingerprint").GetString());
            });
            Assert.Equal("Stale", consumed.GetProperty("disposition").GetString());
            Assert.Equal("none", consumed.GetProperty("allowedEffect").GetString());
        }
        if (!nextStopBeforeRetirement)
        {
            ReactivateCallbackServiceForTest(service, context);
            await CoreSmokeTestsBase.WaitUntilAsync(() => sourceOpens() == 3, TimeSpan.FromSeconds(3));
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, LocalStopEvaluationForTest(context, session));
        }
        Assert.True(SourceFailureHoldCapturedForTest(service, context));
        Assert.Empty(PhysicalSourceHoldForTest(context).Capture!.OutboundCallbacks);
    }

    private static JsonElement[] ReadCallbackPayloadsForTest(int logStart)
    {
        var payloads = new List<JsonElement>();
        foreach (var line in ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal)))
        {
            var encoded = Regex.Match(line, @"(?:^|; )snapshot_payload=([^;]+)").Groups[1].Value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            foreach (var operation in document.RootElement.GetProperty("aggregate").GetProperty("operations").EnumerateArray())
            {
                if (operation.TryGetProperty("routeTransition", out var route) && route.ValueKind == JsonValueKind.Object &&
                    route.TryGetProperty("bootstrapCallbacks", out var callback) && callback.ValueKind == JsonValueKind.Object)
                { payloads.Add(callback.Clone()); }
            }
        }
        return payloads.ToArray();
    }

    private async Task AssertCallbackReceiveGapCannotInstallAsync(SessionFileTransferService service, object context,
        SourceFailureDataSession session, TaskCompletionSource release, Func<int> sourceOpens)
    {
        release.SetResult();
        await CoreSmokeTestsBase.WaitUntilAsync(() =>
            ((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Count == 0,
            TimeSpan.FromSeconds(3));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests;
        SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = (id, _) =>
        {
            if (id != session.TransferId) return Task.CompletedTask;
            received.TrySetResult();
            return dispatch.Task;
        };
        try
        {
            // Issue under the current descriptor, then invalidate before dispatch.
            // Delivering only after invalidation would require the forbidden
            // unbound receive that the install-gap guard now prevents.
            session.Receive(new FileTransferFrontierRequestFrameV6
            { SessionId = session.SessionId, TransferId = session.TransferId, TransportEpoch = 1 });
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            lock (GetServiceGate(service))
            {
                var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
                Assert.Equal(17L, GetPrivateProperty<long>(installed, "DataSessionGeneration"));
                Assert.Equal(17L, GetPrivateProperty<long>(context, "DataSessionGeneration"));
                Assert.NotNull(GetPrivatePropertyValue(context, "BootstrapCallbackAwaitingSuccessor"));
                SetPrivateProperty(context, "DataSessionGeneration", 18L);
            }
            var before = SourceFailureEffectsForTest(context);
            var coordinatorBefore = CreateCoordinatorStateForTest(context).PersistentState;
            var frontierBefore = GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex");
            var acknowledgedBefore = GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver");
            var logStart = GetOperationalLogLength();
            dispatch.SetResult();
            var log = string.Empty;
            await CoreSmokeTestsBase.WaitUntilAsync(() =>
            {
                log = ReadOperationalLogTail(logStart);
                return log.Contains("frontier_receive_authority_gap", StringComparison.Ordinal) &&
                    log.Contains("reason=stale_outbound_sparse_receive_descriptor;", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(3));
            Assert.Contains(log.Split('\n'), line => line.Contains("stage=frontier_receive_authority_gap;", StringComparison.Ordinal) &&
                line.Contains("decision_reason=bootstrap_source_callback_successor_required;", StringComparison.Ordinal));
            Assert.Contains(log.Split('\n'), line => line.Contains("stage=receive_result;", StringComparison.Ordinal) &&
                line.Contains("decision_reason=data_session_generation_mismatch;", StringComparison.Ordinal));
            Assert.Equal(1, sourceOpens());
            Assert.Equal(before, SourceFailureEffectsForTest(context));
            Assert.Equal(coordinatorBefore, CreateCoordinatorStateForTest(context).PersistentState);
            Assert.Equal(frontierBefore, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(acknowledgedBefore, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.DoesNotContain(session.SentFrames, frame => frame is FileTransferErrorFrameV4);
        }
        finally
        {
            dispatch.TrySetResult();
            SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = previous;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCallbackInstallBoundariesForTest(SessionFileTransferService service, object context,
        SourceFailureDataSession session, object stagedInstall)
    {
        lock (GetServiceGate(service))
        {
            var before = SourceFailureEffectsForTest(context);
            var staged = StageSparsePumpInstall(service, context, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"), "callback_capture_stage_race");
            Assert.False(staged.Accepted);
            Assert.Equal("bootstrap_source_callback_successor_required", staged.Reason);
            var committed = CommitSparsePumpInstall(service, context, stagedInstall, session);
            Assert.False(committed.Accepted);
            Assert.Equal("bootstrap_source_callback_successor_required", committed.Reason);
            Assert.Equal(before, SourceFailureEffectsForTest(context));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool CallbackSuccessorDispositionAppliedForTest(SessionFileTransferService service, object context)
    {
        lock (GetServiceGate(service))
        {
            return CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                { DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied };
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool CallbackSuccessorPublishedForTest(SessionFileTransferService service, object context, FileTransferBootstrapLocalStopScope source)
    {
        lock (GetServiceGate(service))
        {
            var state = CreateCoordinatorStateForTest(context);
            return state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
                FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(state, source);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ReactivateCallbackServiceForTest(SessionFileTransferService service, object context)
    {
        lock (GetServiceGate(service))
        {
            var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
            var enqueue = emitter.GetType().GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var decision = FileTransferCoordinatorTests.ReactivateCallbackFallbackForTest(CreateCoordinatorStateForTest(context),
                envelope => enqueue.Invoke(emitter, [envelope]));
            SetPrivateProperty(context, "CoordinatorPersistentState", decision.State.PersistentState);
            typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                    method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, [context, decision, false, false]);
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorDecision ReactivateCallbackFallbackForTest(FileTransferCoordinatorState state,
        Action<FileTransferCoordinatorTransitionEnvelope> capture)
        => ReactivateCorrelatedFallback(state, capture);
}
