using System.Globalization;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport : IInitialActivationReceiverBarrierBridge
{
    private sealed record InitialReceiverBarrierShadow(
        InitialActivationReceiverBarrierAuthority Authority,
        InitialActivationReceiverBarrierNegotiation Negotiation,
        string TransportDataSessionIdentity,
        long TransportDataSessionGeneration,
        InitialActivationReceiverBarrierFailureEventArgs? Failure = null,
        InitialActivationReceiverBarrierCloseReceipt? Applied = null);

    private readonly Dictionary<string, InitialReceiverBarrierShadow> initialReceiverBarriers = new(StringComparer.Ordinal);
    private string? initialReceiverBarrierOwnedPauseSessionId;
    private long initialReceiverBarrierOwnedPauseGeneration;
    private event EventHandler<InitialActivationReceiverBarrierBindingEventArgs>? receiverBarrierBindingRequested;
    private event EventHandler<InitialActivationReceiverBarrierFailureEventArgs>? receiverBarrierFailureObserved;

    private bool IsInitialReceiverBarrierPauseBound(string? sessionId)
    {
        lock (accelerationGate)
        {
            return IsInitialReceiverBarrierPauseBoundUnsafe(sessionId);
        }
    }

    private bool IsInitialReceiverBarrierPauseBoundUnsafe(string? sessionId)
        => sessionId is not null && fileTransferTunaActivationPauseSessionId == sessionId &&
            initialReceiverBarrierOwnedPauseSessionId == sessionId &&
            initialReceiverBarrierOwnedPauseGeneration == fileTransferTunaActivationPauseGeneration;

    event EventHandler<InitialActivationReceiverBarrierBindingEventArgs>? IInitialActivationReceiverBarrierBridge.ReceiverBarrierBindingRequested
    { add => receiverBarrierBindingRequested += value; remove => receiverBarrierBindingRequested -= value; }
    event EventHandler<InitialActivationReceiverBarrierFailureEventArgs>? IInitialActivationReceiverBarrierBridge.ReceiverBarrierFailureObserved
    { add => receiverBarrierFailureObserved += value; remove => receiverBarrierFailureObserved -= value; }

    void IInitialActivationReceiverBarrierBridge.ObserveReceiverDataSessionAttached(IFileTransferDataSession dataSession)
    {
        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(dataSession.TransferId, out var current) ||
                !ReferenceEquals(current, dataSession) || current.IsDisposed)
            {
                return;
            }
        }
        BindOrAttachInitialReceiverBarriers(dataSession.SessionId);
    }

    private InitialActivationReceiverBarrierNegotiation? CaptureInitialReceiverNegotiationUnsafe(string sessionId, string transferId,
        bool includeInvalidatedConvergence = false)
    {
        var transaction = runtimeUnlockTransactionState;
        if (disposed || transaction.SessionId != sessionId || transaction.TransactionGeneration <= 0 ||
            transaction.OfferGeneration <= 0 || transaction.RouteCommitted ||
            (!string.IsNullOrWhiteSpace(transaction.TransferId) && transaction.TransferId != transferId))
        {
            return null;
        }
        if (runtimeUnlockResponderAnswerProbeConvergence is { AuthorityMode: RuntimeUnlockOfferAuthorityMode.InitialActivation } convergence &&
            convergence.SessionId == sessionId && (string.IsNullOrWhiteSpace(convergence.TransferId) || convergence.TransferId == transferId) &&
            convergence.TransactionGeneration == transaction.TransactionGeneration && convergence.OfferGeneration == transaction.OfferGeneration &&
            (includeInvalidatedConvergence || convergence.InvalidationReason is null) && convergence.PairedRetryBindingReceipt is null)
        {
            return new(sessionId, transferId, convergence.TransactionGeneration, convergence.OfferGeneration,
                convergence.PendingAnswerAckGeneration, convergence.Nonce, convergence.PayerDecisionId, convergence.LaunchGeneration,
                convergence.TunaPathLeaseGeneration, convergence.TunaPathLeaseListenerRunId,
                convergence.AnswerMessageId, convergence.AnswerDeadlineUtcMs,
                convergence.AckMessageId is null ? null : new(convergence.AckMessageId, convergence.AckSourceFingerprint!,
                    convergence.AckFrameFingerprint!, convergence.AckObservedUtcMs), convergence.PeerReceipt);
        }
        if (runtimeUnlockOfferProofState is { AuthorityMode: RuntimeUnlockOfferAuthorityMode.InitialActivation, Retired: false } proof &&
            proof.SessionId == sessionId && proof.Generation == transaction.OfferGeneration)
        {
            return new(sessionId, transferId, transaction.TransactionGeneration, proof.Generation,
                pendingAccelerationAnswerAckSessionId == sessionId ? pendingAccelerationAnswerAckGeneration : 0,
                proof.Nonce, proof.PayerDecisionId, runtimeUnlockPreCommitProbeLaunchGeneration,
                transaction.TunaPathLeaseGeneration, transaction.TunaPathLeaseListenerRunId ?? "");
        }
        if (pendingAccelerationAnswerAckAuthorityMode == RuntimeUnlockOfferAuthorityMode.InitialActivation &&
            pendingAccelerationAnswerAckSessionId == sessionId && !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce))
        {
            return new(sessionId, transferId, transaction.TransactionGeneration, transaction.OfferGeneration,
                pendingAccelerationAnswerAckGeneration, pendingAccelerationAnswerAckNonce, pendingAccelerationAnswerAckPayerDecisionId,
                0, transaction.TunaPathLeaseGeneration, transaction.TunaPathLeaseListenerRunId ?? "");
        }
        return null;
    }

    private void BindOrAttachInitialReceiverBarriers(string sessionId)
    {
        TransportFileTransferDataSession[] sessions;
        lock (gate)
        {
            sessions = fileTransferDataSessions.Values.Where(session => !session.IsDisposed && session.SessionId == sessionId).ToArray();
        }
        foreach (var session in sessions)
        {
            long pauseGeneration;
            InitialActivationReceiverBarrierNegotiation negotiation;
            InitialActivationReceiverBarrierAuthority? authority;
            lock (accelerationGate)
            {
                if (fileTransferTunaActivationPauseSessionId != sessionId ||
                    CaptureInitialReceiverNegotiationUnsafe(sessionId, session.TransferId) is not { } captured)
                {
                    continue;
                }
                pauseGeneration = fileTransferTunaActivationPauseGeneration;
                negotiation = captured;
                initialReceiverBarriers.TryGetValue(session.TransferId, out var current);
                authority = current?.Authority.PauseGeneration == pauseGeneration &&
                    ReferenceEquals(current.Authority.Source.DataSession, session) ? current.Authority : null;
                if (current?.Applied is not null && authority is not null)
                {
                    continue;
                }
            }
            var args = new InitialActivationReceiverBarrierBindingEventArgs(session, pauseGeneration, negotiation, authority);
            receiverBarrierBindingRequested?.Invoke(this, args);
            if (args.Decision is not { Authority: { } accepted } decision || decision.Status is not
                (InitialActivationReceiverBarrierDecisionStatus.Accepted or InitialActivationReceiverBarrierDecisionStatus.Idempotent))
            {
                continue;
            }
            lock (accelerationGate)
            {
                if (!disposed && !session.IsDisposed && ReferenceEquals(accepted.Source.DataSession, session) &&
                    fileTransferTunaActivationPauseSessionId == sessionId &&
                    fileTransferTunaActivationPauseGeneration == pauseGeneration &&
                    CaptureInitialReceiverNegotiationUnsafe(sessionId, session.TransferId) == negotiation)
                {
                    // Core attachment generations are per transfer; transport instance
                    // generations are global. Retain this exact physical witness separately.
                    initialReceiverBarriers[session.TransferId] = new(accepted, negotiation,
                        $"{session.TransferId}:{session.Generation.ToString(CultureInfo.InvariantCulture)}", session.Generation);
                    // Retain this bounded ownership marker even if one member is disposed;
                    // a late callback still cannot release another member of the same pause.
                    initialReceiverBarrierOwnedPauseSessionId = sessionId;
                    initialReceiverBarrierOwnedPauseGeneration = pauseGeneration;
                    session.RegisterInitialReceiverBarrierExecution(accepted.ExecutionFence);
                }
            }
        }
    }

    private InitialActivationReceiverBarrierFailureEventArgs[] CaptureInitialReceiverBarrierFailures(AccelerationStateChangedEventArgs args)
    {
        if (args.IsAvailable || args.FailureOrigin is not { IsDefault: false } origin ||
            !IsRuntimeUnlockListenerRearmSidecarDropReason(args.Reason) || accelerationLane?.IsFailureOriginCurrent(origin) != true)
        {
            return [];
        }
        var failures = new List<InitialActivationReceiverBarrierFailureEventArgs>();
        lock (accelerationGate)
        {
            foreach (var pair in initialReceiverBarriers.ToArray())
            {
                var barrier = pair.Value;
                var authority = barrier.Authority;
                if (barrier.Applied is not null || authority.PauseGeneration != fileTransferTunaActivationPauseGeneration ||
                    authority.Source.RuntimeOwner.SessionId != fileTransferTunaActivationPauseSessionId ||
                    CaptureInitialReceiverNegotiationUnsafe(authority.Source.RuntimeOwner.SessionId, pair.Key) is not { } observed)
                {
                    continue;
                }
                // Submit both exact witnesses. Only the coordinator may decide
                // whether a waiting-launch successor can close this barrier.
                var observation = new InitialActivationReceiverBarrierFailureEventArgs(authority, observed,
                    new(origin.LaneInstanceId, origin.ClientGeneration, origin.ProcessRunId, origin.FailureGeneration), args.Reason,
                    barrier.Negotiation);
                initialReceiverBarriers[pair.Key] = barrier with { Failure = observation };
                failures.Add(observation);
            }
        }
        return failures.ToArray();
    }

    bool IInitialActivationReceiverBarrierBridge.IsReceiverBarrierFailureCurrent(InitialActivationReceiverBarrierFailureEventArgs observation)
        => IsInitialReceiverBarrierFailureCurrent(observation);

    private bool IsInitialReceiverBarrierFailureCurrent(InitialActivationReceiverBarrierFailureEventArgs observation)
    {
        InitialReceiverBarrierShadow shadow;
        lock (accelerationGate)
        {
            if (!IsInitialReceiverBarrierFailureCurrentUnsafe(observation))
            {
                return false;
            }
            shadow = initialReceiverBarriers[observation.Authority.Source.RuntimeOwner.TransferId];
        }
        var origin = observation.Origin;
        if (accelerationLane?.IsFailureOriginCurrent(new(origin.LaneInstanceId, origin.ClientGeneration,
                origin.ProcessRunId, origin.FailureGeneration)) != true ||
            FileTransferCoordinator.CreateRoutePublicationAuthority(observation.Authority.Source.RoutePublication) is not { } publication ||
            !TryCaptureRuntimeUnlockRetryNegotiationSourceScope(publication, out var source, out var session, out var checkpoint) ||
            session is null || !ReferenceEquals(session, observation.Authority.Source.DataSession) || session.IsDisposed ||
            source.DataSessionIdentity != shadow.TransportDataSessionIdentity ||
            source.DataSessionGeneration != shadow.TransportDataSessionGeneration ||
            source.TransportEpoch != observation.Authority.Source.TransportEpoch ||
            source.BridgeRecoveryGeneration != observation.Authority.Source.BridgeRecoveryGeneration ||
            (source.CheckpointRequestId ?? "(none)") != observation.Authority.Source.CheckpointRequestId ||
            checkpoint != observation.Authority.Source.CheckpointGeneration)
        {
            return false;
        }
        lock (accelerationGate)
        {
            return IsInitialReceiverBarrierFailureCurrentUnsafe(observation) &&
                ReferenceEquals(initialReceiverBarriers[session.TransferId], shadow);
        }
    }

    private bool IsInitialReceiverBarrierFailureCurrentUnsafe(InitialActivationReceiverBarrierFailureEventArgs observation)
        => !disposed && initialReceiverBarriers.TryGetValue(observation.Authority.Source.RuntimeOwner.TransferId, out var shadow) &&
            shadow.Authority == observation.Authority && ReferenceEquals(shadow.Failure, observation) &&
            shadow.Negotiation == observation.RetainedNegotiation &&
            CaptureInitialReceiverNegotiationUnsafe(observation.Negotiation.SessionId, observation.Negotiation.TransferId,
                includeInvalidatedConvergence: true) == observation.Negotiation &&
            fileTransferTunaActivationPauseGeneration == observation.Authority.PauseGeneration &&
            fileTransferTunaActivationPauseSessionId == observation.Negotiation.SessionId &&
            runtimeUnlockTransactionState.TransactionGeneration == observation.Negotiation.TransactionGeneration &&
            runtimeUnlockTransactionState.OfferGeneration == observation.Negotiation.OfferGeneration &&
            !runtimeUnlockTransactionState.RouteCommitted &&
            fileTransferRouteTransitionExecutionShadow.Current?.Command.Authority.TransferId != observation.Negotiation.TransferId;

    InitialActivationReceiverBarrierCloseReceipt IInitialActivationReceiverBarrierBridge.ApplyReceiverBarrierClose(
        InitialActivationReceiverBarrierCloseCommand command)
    {
        InitialActivationReceiverBarrierFailureEventArgs? observation;
        lock (accelerationGate)
        {
            initialReceiverBarriers.TryGetValue(command.Authority.Source.RuntimeOwner.TransferId, out var captured);
            observation = captured?.Failure;
        }
        if (observation is null || observation.Authority != command.Authority || observation.Negotiation != command.Negotiation ||
            observation.Origin != command.FailureOrigin || !IsInitialReceiverBarrierFailureCurrent(observation))
        {
            return new(command, InitialActivationReceiverBarrierCloseApplyStatus.Stale, "receiver_barrier_bridge_scope_not_current");
        }
        TransportFileTransferDataSession session;
        InitialReceiverBarrierShadow shadow;
        lock (accelerationGate)
        {
            if (!IsInitialReceiverBarrierFailureCurrentUnsafe(observation) ||
                command.Authority.Source.DataSession is not TransportFileTransferDataSession currentSession || currentSession.IsDisposed)
            {
                return new(command, InitialActivationReceiverBarrierCloseApplyStatus.Stale, "receiver_barrier_bridge_scope_replaced");
            }
            session = currentSession;
            shadow = initialReceiverBarriers[session.TransferId];
            if (shadow.Applied is { } applied)
            {
                return applied.Command == command ? applied : new(command, InitialActivationReceiverBarrierCloseApplyStatus.Stale,
                    "receiver_barrier_bridge_command_changed");
            }
        }
        InitialActivationReceiverBarrierCloseReceipt receipt = new(command,
            InitialActivationReceiverBarrierCloseApplyStatus.Stale, "receiver_barrier_failure_origin_replaced");
        var origin = command.FailureOrigin;
        // The lane owns the origin check through the tiny per-session mutation.
        // This callback takes neither the service gate nor accelerationGate.
        accelerationLane?.TryApplyFailureOrigin(new(origin.LaneInstanceId, origin.ClientGeneration,
            origin.ProcessRunId, origin.FailureGeneration), () => command.Authority.ExecutionFence.TryApply(() =>
        {
            receipt = session.ApplyInitialReceiverBarrierClose(command, shadow.TransportDataSessionGeneration);
            return receipt.Status == InitialActivationReceiverBarrierCloseApplyStatus.Applied;
        }));
        lock (accelerationGate)
        {
            if (receipt.Status == InitialActivationReceiverBarrierCloseApplyStatus.Applied &&
                initialReceiverBarriers.TryGetValue(session.TransferId, out var current) && ReferenceEquals(current, shadow))
            {
                initialReceiverBarriers[session.TransferId] = shadow with { Applied = receipt };
            }
        }
        return receipt;
    }
}
