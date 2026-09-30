using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

// Test-only driver of the real snapshot emitter. It emits each generated
// Coordinator boundary before the corresponding transport publication/use.
internal sealed class FileTransferPublicationIngressHistory : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Type type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
    private readonly object emitter;
    private readonly ConcurrentQueue<string> pending;
    private readonly List<FileTransferCoordinatorTransitionEnvelope> history = new();
    private readonly FileTransferDirection direction;
    private int next;
    private bool completed;
    private FileTransferCoordinatorState? fallbackState;
    private FileTransferCoordinatorState? secondTunaState;
    private readonly SessionFileTransferV4SenderTests.ServiceYieldHistory? serviceYield;

    internal IReadOnlyList<RoutePublicationToken> Tokens { get; private set; }
    internal FileTransferFallbackTailYieldFrameV6? FallbackYieldFrame { get; private set; }

    internal FileTransferPublicationIngressHistory(string sessionId, string transferId, FileTransferDirection direction,
        bool stopAtFallbackYield = false, IFileTransferDataSession? serviceYieldData = null,
        IFileTransferControlPlaneDeliveryTransport? serviceYieldTransport = null)
    {
        this.direction = direction;
        emitter = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetField("drainScheduled", Flags)!.SetValue(emitter, 1);
        pending = (ConcurrentQueue<string>)type.GetField("pending", Flags)!.GetValue(emitter)!;
        if (serviceYieldData is not null)
        {
            Assert.True(stopAtFallbackYield);
            serviceYield = new(serviceYieldData, serviceYieldTransport!, emitter);
        }
        Tokens = FileTransferCoordinatorTests.CreateDataPathIngressRouteCycle(sessionId, transferId, direction, history.Add,
            initializeDataSession: true, captureFallbackYield: stopAtFallbackYield && serviceYield is null ? frame => FallbackYieldFrame = frame : null,
            captureFallbackState: stopAtFallbackYield ? state => fallbackState = state : null,
            captureSecondTunaState: state => secondTunaState = state,
            stopBeforeFallbackYield: serviceYield is not null, coordinatorInstanceId: serviceYield?.CoordinatorInstanceId,
            dataSessionIdentity: serviceYield?.DataSessionIdentity);
    }

    internal FileTransferRoutePublicationEvidence Publish(int index)
    {
        var token = Tokens[index];
        var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(token);
        while (next < history.Count)
        {
            var envelope = history[next++];
            Enqueue(envelope);
            var publication = envelope.Aggregate.Operations.SingleOrDefault(operation =>
                operation.OperationRole == "route_publication" && operation.ReceiptFingerprint == fingerprint &&
                operation.AfterState == "Published");
            if (publication is null) { continue; }
            type.GetMethod("EnqueueAuthorityEffect", Flags)!.Invoke(emitter,
                [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                    envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                    publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                    publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                    publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
            var evidence = Assert.IsType<FileTransferRoutePublicationEvidence>(type.GetMethod("GetRoutePublicationEvidence", Flags)!
                .Invoke(emitter, [token]));
            Drain();
            return evidence;
        }
        throw new InvalidOperationException($"Missing emitted publication for epoch {token.LiveRouteEpoch}");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal FileTransferRoutePublicationEvidence PublishFinalFallback(long transportEpoch = 2)
    {
        Assert.NotNull(secondTunaState);
        Assert.Equal(4, Tokens.Count);
        var state = FileTransferCoordinatorTests.EnterIngressFinalFallback(secondTunaState!, history.Add, transportEpoch);
        Tokens = Tokens.Append(state.CurrentRoutePublicationToken!.Value).ToArray();
        secondTunaState = null;
        return Publish(4);
    }

    public void Dispose()
    {
        if (completed) { return; }
        completed = true;
        while (next < history.Count) { Enqueue(history[next++]); }
        var last = history[^1];
        type.GetMethod("Complete", Flags)!.Invoke(emitter, [last.CoordinatorInstanceId, last.SessionId, last.TransferId, direction]);
        Drain();
        serviceYield?.Dispose();
    }

    internal void PublishFallbackYield()
    {
        Assert.NotNull(FallbackYieldFrame);
        while (next < history.Count) { Enqueue(history[next++]); }
        Drain();
    }

    internal async Task SendServiceYieldAsync()
    {
        Assert.NotNull(serviceYield);
        while (next < history.Count) { Enqueue(history[next++]); }
        Drain();
        serviceYield.Initialize(fallbackState!);
        await serviceYield.SendYieldAsync(Drain);
    }

    internal FileTransferRoutePublicationEvidence ActivateFromServicePeerYield(FileTransferReceivedDataFrame[] received)
    {
        Assert.NotNull(serviceYield);
        foreach (var copy in received) { serviceYield.ApplyPeer(copy); }
        Drain();
        fallbackState = serviceYield.State;
        return ActivateFromPeerYield(Assert.IsType<FileTransferFallbackTailYieldFrameV6>(received[0].Frame));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal FileTransferRoutePublicationEvidence ActivateFromPeerYield(FileTransferFallbackTailYieldFrameV6 peerFrame)
    {
        Assert.NotNull(fallbackState);
        Assert.Equal(3, Tokens.Count);
        var publication = FileTransferCoordinatorTests.CompleteIngressSecondTunaFromPeerYield(fallbackState!, peerFrame, history.Add,
            state => secondTunaState = state, peerAlreadyApplied: serviceYield is not null);
        fallbackState = null;
        Tokens = [.. Tokens, publication];
        return Publish(3);
    }

    private void Drain()
    {
        while (pending.TryDequeue(out var line)) { LocalOperationalLog.Info("SessionFileTransfer", line); }
    }

    private void Enqueue(FileTransferCoordinatorTransitionEnvelope envelope)
    {
        type.GetMethod("Enqueue", Flags)!.Invoke(emitter, [envelope]);
        // The driver projects already validated Coordinator receipts through
        // the production emitter. It does not claim the whole service/bridge
        // workflow ran, invent a receipt, or infer an effect from the rejection.
        switch (envelope.DecisionKind)
        {
            case nameof(FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation):
                EnqueueServiceEffect("EnqueueRuntimeUnlockRetryAuthorityEffect", envelope, "scope_invalidation", "scope_invalidation_applied");
                break;
            case nameof(FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchStage):
                EnqueueServiceEffect("EnqueueRuntimeUnlockRetryAuthorityEffect", envelope, "stage", "dispatch_stage_observed");
                break;
            case nameof(FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall):
                EnqueueServiceEffect("EnqueueRuntimeUnlockRetryInstallAuthorityEffect", envelope);
                break;
            case "FallbackTailQuiescent":
                EnqueueServiceEffect("EnqueueFallbackTailYieldAuthorityEffect", envelope, "yield", "recovery_owner_retired_after_yield");
                break;
            case nameof(FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt):
                EnqueueServiceEffect("EnqueueFallbackTailYieldAuthorityEffect", envelope, "local_proof", "local_proof_sent");
                EnqueueServiceEffect("EnqueueFallbackTailYieldAuthorityEffect", envelope, "local_proof", "local_proof_applied");
                break;
            case nameof(FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt):
                EnqueueServiceEffect("EnqueueFallbackTailYieldAuthorityEffect", envelope, "peer_proof", "peer_proof_observed");
                EnqueueServiceEffect("EnqueueFallbackTailYieldAuthorityEffect", envelope, "paired_release", "paired_release_published");
                break;
        }
        foreach (var operation in envelope.Aggregate.Operations.Where(operation => operation.ReceiptValidated))
        {
            string[] kinds = [];
            if (operation.Family == "route_runtime" && operation.OperationRole == "route_commit" && operation.AfterState == "Committed")
            {
                kinds = ["route_commit_adopted", "target_runtime_leg_installed"];
            }
            else if (operation.Family == "route_transition")
            {
                if (envelope.DecisionKind == nameof(FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain) && operation.OperationRole == "current")
                { kinds = ["pause_installed"]; }
                else if (envelope.DecisionKind == nameof(FileTransferCoordinator.CommitFileTransferRouteTransition))
                {
                    if (operation.OperationRole == "terminal" && operation.BeforeState == "None" && operation.AfterState == "Committed")
                    { kinds = ["route_commit_adopted", "target_runtime_leg_installed"]; }
                    else if (operation.OperationRole == direction.ToString().ToLowerInvariant()) { kinds = ["completed_drain_receipt"]; }
                }
                else if (envelope.DecisionKind == nameof(FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt) &&
                         operation.OperationRole == direction.ToString().ToLowerInvariant())
                { kinds = ["route_transition_disposition_applied", "transition_pause_released", "recovered_epoch_publication"]; }
                else if (envelope.DecisionKind == nameof(FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt) &&
                         operation.OperationRole == direction.ToString().ToLowerInvariant())
                { kinds = ["retained_frame_response", "retained_frame_applied"]; }
            }
            foreach (var kind in kinds)
            {
                type.GetMethod("EnqueueAuthorityEffect", Flags)!.Invoke(emitter,
                    [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                        envelope.Aggregate.AfterStateGeneration, operation.Family, operation.AuthorityId,
                        operation.OperationGeneration, operation.ReceiptIdentity, operation.ReceiptGeneration,
                        operation.ReceiptStatus, operation.ReceiptFingerprint, kind,
                        $"{operation.OperationKey}:{kind}", operation.ReceiptGeneration, operation.OperationKey]);
            }
        }
        foreach (var operation in envelope.Aggregate.Operations.Where(operation =>
                     operation.Family == "responder_probe_handoff" && operation.ResponderProbeHandoff is not null))
        {
            var kind = operation.OperationRole == "route_transition_handoff_pending" ? "route_transition_handoff_supplied"
                : operation.ReceiptStatus == "Invalidated" ? "handoff_invalidated"
                : envelope.DecisionKind switch
                {
                    nameof(FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff) => "handoff_bound",
                    nameof(FileTransferCoordinator.HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork) => "handoff_held",
                    nameof(FileTransferCoordinator.InvalidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoff) => "handoff_invalidated",
                    _ => "convergence_observed",
                };
            foreach (var receipt in operation.ResponderProbeHandoff!.Receipts.Where(receipt => receipt.ReceiptValidated))
            {
                type.GetMethod("EnqueueAuthorityEffect", Flags)!.Invoke(emitter,
                    [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                        envelope.Aggregate.AfterStateGeneration, operation.Family, operation.AuthorityId,
                        operation.OperationGeneration, receipt.ReceiptIdentity, receipt.ReceiptGeneration,
                        receipt.ReceiptStatus, receipt.ReceiptFingerprint, kind,
                        $"{operation.OperationKey}:{kind}", Math.Max(1, receipt.ReceiptGeneration), null]);
            }
        }
    }

    private void EnqueueServiceEffect(string methodName, FileTransferCoordinatorTransitionEnvelope envelope, params object[] tail)
    {
        object[] arguments = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId,
            direction, envelope.Aggregate.AfterStateGeneration, envelope, .. tail];
        typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == methodName && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments);
    }
}
