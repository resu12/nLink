using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static TimeSpan CurrentV6HeartbeatInterval
        => V6HeartbeatIntervalOverrideForTests ?? TimeSpan.FromMilliseconds(V6HeartbeatIntervalMs);

    private static TimeSpan CurrentV6PeerLivenessTimeout
        => V6PeerLivenessTimeoutOverrideForTests ?? TimeSpan.FromMilliseconds(V6PeerLivenessTimeoutMs);

    private static TimeSpan CurrentV6SenderRequestFeedbackStallRecoveryDelay
        => V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests ?? TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoveryMs);

    private static TimeSpan CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay
        => V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests ??
           TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryMs);

    private static TimeSpan CurrentV6RegularNknSparseRuntimeStateRefreshCooldown
        => V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests ??
           TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStateRefreshCooldownMs);

    private static TimeSpan CurrentV6SenderRequestFeedbackStallRecoveryCooldown
        => TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoveryCooldownMs);

    public bool RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
        => TryRequestFileTransferReceiveRecoveryIfCurrent(
            request,
            requiredSparsePumpInstall: null,
            requiredDataSession: null,
            allowObservationCapture: false);

    public bool TryAuthorizeRecoveryLivenessDeferral(
        FileTransferRecoveryLivenessSnapshot snapshot,
        string reason,
        DateTimeOffset livenessDeferralDeadlineUtc,
        out FileTransferRecoveryLivenessDeferralGrant? grant)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) ||
            string.IsNullOrWhiteSpace(snapshot.TransferId))
        {
            return false;
        }

        var attemptedAction = string.IsNullOrWhiteSpace(reason)
            ? "session_liveness_recovery_state"
            : reason.Trim();
        lock (gate)
        {
            if (outboundTransfer is { } outbound &&
                string.Equals(outbound.SessionId, snapshot.SessionId, StringComparison.Ordinal) &&
                string.Equals(outbound.TransferId, snapshot.TransferId, StringComparison.Ordinal))
            {
                return TryAuthorizeReceiveRecoveryOperationLivenessDeferralLocked(
                    CreateCoordinatorStateLocked(outbound),
                    snapshot,
                    attemptedAction,
                    livenessDeferralDeadlineUtc,
                    out grant);
            }

            if (inboundTransfer is { } inbound &&
                string.Equals(inbound.SessionId, snapshot.SessionId, StringComparison.Ordinal) &&
                string.Equals(inbound.TransferId, snapshot.TransferId, StringComparison.Ordinal))
            {
                return TryAuthorizeReceiveRecoveryOperationLivenessDeferralLocked(
                    CreateCoordinatorStateLocked(inbound),
                    snapshot,
                    attemptedAction,
                    livenessDeferralDeadlineUtc,
                    out grant);
            }
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_recovery_liveness_lease_rejected; direction=unknown; transfer_id={FormatProtocolLogValue(snapshot.TransferId)}; session_id={FormatProtocolLogValue(snapshot.SessionId)}; action_kind=ReceiveRecoveryOperation; source=session_liveness_recovery_state; attempted_action={FormatProtocolLogValue(attemptedAction)}; rejection_reason=current_transfer_missing; reason=current_transfer_missing; route={FormatProtocolLogValue(snapshot.RouteToken)}; protocol_version={snapshot.ProtocolVersion}; live_route_epoch={snapshot.LiveRouteEpoch}; leg_generation={snapshot.TransferLegGeneration}; transport_epoch={snapshot.TransportEpoch}; bridge_recovery_generation={snapshot.BridgeRecoveryGeneration}");
        return false;
    }

    internal static bool TryAuthorizeReceiveRecoveryOperationLivenessDeferralLocked(
        FileTransferCoordinatorState state,
        FileTransferRecoveryLivenessSnapshot snapshot,
        string reason,
        DateTimeOffset livenessDeferralDeadlineUtc,
        out FileTransferRecoveryLivenessDeferralGrant? grant)
    {
        grant = null;
        if (!FileTransferCoordinator.TryGetReceiveRecoveryLivenessDeferral(
                state,
                out var operation,
                out var coordinatorGrant,
                out var maximumDeadlineUtc) ||
            !string.Equals(
                operation.Authority.Observation.SessionId,
                snapshot.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                operation.Authority.Observation.TransferId,
                snapshot.TransferId,
                StringComparison.Ordinal) ||
            !string.Equals(
                operation.Authority.Observation.RouteToken,
                snapshot.RouteToken,
                StringComparison.Ordinal) ||
            operation.Authority.Observation.ProtocolVersion != snapshot.ProtocolVersion ||
            operation.Authority.Observation.LiveRouteEpoch != snapshot.LiveRouteEpoch ||
            operation.Authority.Observation.LegGeneration != snapshot.TransferLegGeneration ||
            operation.Authority.Observation.TransportEpoch != snapshot.TransportEpoch ||
            operation.Authority.Observation.BridgeRecoveryGeneration !=
                snapshot.BridgeRecoveryGeneration ||
            livenessDeferralDeadlineUtc == default ||
            livenessDeferralDeadlineUtc <= DateTimeOffset.UtcNow ||
            livenessDeferralDeadlineUtc > maximumDeadlineUtc)
        {
            return false;
        }

        grant = coordinatorGrant with
        {
            LivenessDeferralDeadlineUtc =
                livenessDeferralDeadlineUtc,
        };
        LogReceiveRecoveryOperationLivenessDeferralGranted(
            operation,
            grant,
            reason);
        return true;
    }

    private static void LogReceiveRecoveryOperationLivenessDeferralGranted(
        FileTransferReceiveRecoveryOperation operation,
        FileTransferRecoveryLivenessDeferralGrant grant,
        string attemptedAction)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_recovery_liveness_lease_granted; direction={grant.Direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(grant.TransferId)}; session_id={FormatProtocolLogValue(grant.SessionId)}; action_kind=ReceiveRecoveryOperation; source=session_liveness_recovery_state; attempted_action={FormatProtocolLogValue(attemptedAction)}; reason={FormatProtocolLogValue(grant.Reason)}; route={FormatProtocolLogValue(grant.RouteToken)}; protocol_version={grant.ProtocolVersion}; live_route_epoch={grant.LiveRouteEpoch}; runtime_generation={grant.RuntimeGeneration}; terminal_generation={grant.TerminalGeneration}; leg_generation={grant.TransferLegGeneration}; transport_epoch={grant.TransportEpoch}; bridge_recovery_generation={grant.BridgeRecoveryGeneration}; recovery_liveness_lease_authority_id={FormatProtocolLogValue(operation.Authority.RecoveryLivenessLease?.AuthorityId.ToString() ?? "(none)")}; recovery_liveness_lease_generation={grant.LeaseGeneration}; recovery_liveness_lease_state={FormatProtocolLogValue(grant.LeaseState)}; receive_recovery_operation_authority_id={FormatProtocolLogValue(operation.Authority.AuthorityId.ToString())}; receive_recovery_operation_generation={operation.Authority.OperationGeneration}; liveness_deferral_applied=0; liveness_deferral_deadline_utc_ms={grant.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds()}");

    private bool TryRequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
        => TryRequestFileTransferReceiveRecoveryIfCurrent(
            request,
            requiredSparsePumpInstall: null,
            requiredDataSession: null,
            allowObservationCapture: true);

    private void OnBridgeRecoveryEvaluationRequested(
        object? sender,
        FileTransferBridgeRecoveryEvaluationRequestedEventArgs args)
    {
        if (sender is not IFileTransferBridgeRecoveryExecutionBridge executionBridge)
        {
            return;
        }

        var evaluation = args.Request;
        _ = Task.Run(() =>
            ProcessBridgeRecoveryEvaluationRequested(executionBridge, evaluation));
    }

    private void OnReceiveRecoveryProofObserved(
        object? sender,
        FileTransferReceiveRecoveryProofObservedEventArgs args)
    {
        if (sender is not IFileTransferBridgeRecoveryExecutionBridge executionBridge)
        {
            return;
        }

        var observation = args.Observation;
        _ = Task.Run(() =>
            ProcessReceiveRecoveryProofObserved(executionBridge, observation));
    }

    private void OnReceiveRecoveryPeerReachabilityObserved(
        object? sender,
        FileTransferReceiveRecoveryPeerReachabilityObservedEventArgs args)
    {
        if (sender is not IFileTransferBridgeRecoveryExecutionBridge executionBridge)
        {
            return;
        }

        var observation = args.Observation;
        _ = Task.Run(() =>
            ProcessReceiveRecoveryPeerReachabilityObserved(
                executionBridge,
                observation));
    }

    private void OnReceiveRecoveryProofStatePublicationRecheckEvidence(
        object? sender,
        SessionLivenessProofEventArgs args)
    {
        if (sender is not ISessionLivenessSignalingTransport livenessTransport ||
            args.Generation <= 0 ||
            args.Sequence <= 0 ||
            args.ObservedUtcMs <= 0 ||
            !string.Equals(
                args.ProofKind,
                "heartbeat_received",
                StringComparison.Ordinal) ||
            args.Lane is not ("control" or "bulk"))
        {
            return;
        }

        var evidence =
            new FileTransferReceiveRecoveryProofStatePublicationRecheckEvidence(
                args.SessionId,
                args.Generation,
                args.Sequence,
                args.ObservedUtcMs,
                args.ProofKind,
                args.Lane);
        _ = Task.Run(() =>
            ProcessReceiveRecoveryProofStatePublicationRecheckEvidence(
                livenessTransport,
                evidence));
    }

    private void ProcessReceiveRecoveryProofStatePublicationRecheckEvidence(
        ISessionLivenessSignalingTransport livenessTransport,
        FileTransferReceiveRecoveryProofStatePublicationRecheckEvidence evidence)
    {
        FileTransferReceiveRecoveryDecision? decision = null;
        FileTransferReceiveRecoveryOperation? operation = null;
        FileTransferReceiveRecoveryProofStatePublicationCommand? command = null;
        InboundTransferContext? publicationContext = null;
        lock (gate)
        {
            if (evidence.IsDefault ||
                transport is not
                    ISessionLivenessSignalingTransport currentLivenessTransport ||
                !ReferenceEquals(currentLivenessTransport, livenessTransport) ||
                inboundTransfer is not { IsTerminal: false } inbound ||
                !string.Equals(
                    inbound.SessionId,
                    evidence.SessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var recovery =
                inbound.CoordinatorPersistentState.FileTransferReceiveRecovery;
            var proofPublication = recovery.ProofStatePublication;
            var exactDuplicateEvidence =
                proofPublication?.LastRecheckEvidence is
                    { } lastRecheckEvidence &&
                FileTransferCoordinator
                    .IsSameReceiveRecoveryProofStatePublicationRecheckEvidenceIdentity(
                        lastRecheckEvidence,
                        evidence) &&
                ((proofPublication.PendingCommand?.RecheckEvidence is
                      { } pendingRecheckEvidence &&
                  FileTransferCoordinator
                      .IsSameReceiveRecoveryProofStatePublicationRecheckEvidenceIdentity(
                          pendingRecheckEvidence,
                          evidence)) ||
                 (proofPublication.LastReceipt?.Command.RecheckEvidence is
                      { } completedRecheckEvidence &&
                  FileTransferCoordinator
                      .IsSameReceiveRecoveryProofStatePublicationRecheckEvidenceIdentity(
                          completedRecheckEvidence,
                          evidence)));
            if (recovery.CurrentOperation is not
                {
                    ExecutionState:
                        FileTransferReceiveRecoveryExecutionState.Completed,
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Retained,
                    ReceiveProof: null,
                } ||
                proofPublication?.LastReceipt is not
                {
                    Outcome:
                        FileTransferReceiveRecoveryProofStatePublicationOutcome
                            .Delivered,
                } ||
                (proofPublication.PendingRecheckArm is null &&
                 !exactDuplicateEvidence))
            {
                return;
            }

            decision = FileTransferCoordinator
                .ObserveReceiveRecoveryProofStatePublicationRecheckEvidence(
                    CreateCoordinatorStateLocked(inbound),
                    evidence);
            ApplyReceiveRecoveryStateLocked(inbound, decision.Value.State);
            operation =
                inbound.CoordinatorPersistentState.FileTransferReceiveRecovery
                    .CurrentOperation;
            command = decision.Value.ProofStatePublicationCommand;
            if (command is { } exactCommand &&
                inbound.ReceiveRecoveryProofStatePublicationInFlightCommand is
                    null)
            {
                inbound.ReceiveRecoveryProofStatePublicationInFlightCommand =
                    exactCommand;
                publicationContext = inbound;
            }
        }

        var authority = operation?.Authority;
        var scope = authority?.Observation;
        var predecessorReceipt = command?.PredecessorReceipt;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_proof_state_publication_recheck_decided; session_id={FormatProtocolLogValue(scope?.SessionId ?? evidence.SessionId)}; transfer_id={FormatProtocolLogValue(scope?.TransferId ?? "(none)")}; direction={scope?.Direction.ToString().ToLowerInvariant() ?? "inbound"}; {FormatReceiveRecoveryObservationScopeFields(scope)}; recovery_operation_authority_id={FormatProtocolLogValue(authority?.AuthorityId.ToString() ?? "(none)")}; recovery_operation_generation={authority?.OperationGeneration ?? 0}; closure_generation={operation?.ClosureGeneration ?? 0}; closure_state={operation?.ClosureState.ToString() ?? "none"}; predecessor_publication_generation={predecessorReceipt?.Command.PublicationGeneration ?? 0}; predecessor_frame_fingerprint={FormatProtocolLogValue(predecessorReceipt?.FrameFingerprint ?? "(none)")}; predecessor_receipt_observed_utc_ms={command?.PredecessorReceiptObservedUtcMs ?? 0}; heartbeat_generation={evidence.HeartbeatGeneration}; heartbeat_sequence={evidence.HeartbeatSequence}; proof_kind={FormatProtocolLogValue(evidence.ProofKind)}; lane={FormatProtocolLogValue(evidence.Lane)}; observed_utc_ms={evidence.ObservedUtcMs}; publication_generation={command?.PublicationGeneration ?? 0}; decision_status={decision?.Status.ToString() ?? "Rejected"}; decision_reason={FormatProtocolLogValue(decision?.Reason ?? "filetransfer_receive_recovery_inbound_operation_missing")}");

        if (publicationContext is not null &&
            command is { } publicationCommand)
        {
            _ = PublishReceiveRecoveryProofStateAsync(
                publicationContext,
                publicationCommand);
        }
    }

    private void ProcessReceiveRecoveryPeerReachabilityObserved(
        IFileTransferBridgeRecoveryExecutionBridge executionBridge,
        FileTransferReceiveRecoveryPeerReachabilityObservation observation)
    {
        FileTransferReceiveRecoveryDecision? decision = null;
        FileTransferReceiveRecoveryOperation? operation = null;
        FileTransferReceiveRecoveryProofStatePublicationCommand? command = null;
        InboundTransferContext? publicationContext = null;
        lock (gate)
        {
            if (observation.IsDefault ||
                transport is not IFileTransferBridgeRecoveryExecutionBridge currentBridge ||
                !ReferenceEquals(currentBridge, executionBridge) ||
                inboundTransfer is not { IsTerminal: false } inbound)
            {
                return;
            }

            var recovery =
                inbound.CoordinatorPersistentState.FileTransferReceiveRecovery;
            if (recovery.CurrentOperation is not { } matched ||
                matched.Authority.ExecutionCommand !=
                    observation.ExecutionCommand)
            {
                return;
            }

            decision =
                FileTransferCoordinator.ObserveReceiveRecoveryPeerReachability(
                    CreateCoordinatorStateLocked(inbound),
                    observation);
            ApplyReceiveRecoveryStateLocked(inbound, decision.Value.State);
            operation =
                inbound.CoordinatorPersistentState.FileTransferReceiveRecovery
                    .CurrentOperation;
            command = decision.Value.ProofStatePublicationCommand;
            if (command is { } exactCommand &&
                inbound.ReceiveRecoveryProofStatePublicationInFlightCommand is null)
            {
                inbound.ReceiveRecoveryProofStatePublicationInFlightCommand =
                    exactCommand;
                publicationContext = inbound;
            }
        }

        var authority = operation?.Authority;
        var scope = authority?.Observation;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_proof_state_publication_decided; session_id={FormatProtocolLogValue(scope?.SessionId ?? observation.ExecutionCommand.Decision.RuntimeOwner.SessionId)}; transfer_id={FormatProtocolLogValue(scope?.TransferId ?? observation.ExecutionCommand.Decision.RuntimeOwner.TransferId)}; direction={scope?.Direction.ToString().ToLowerInvariant() ?? observation.ExecutionCommand.Decision.RuntimeOwner.Direction.ToString().ToLowerInvariant()}; recovery_operation_authority_id={FormatProtocolLogValue(authority?.AuthorityId.ToString() ?? "(none)")}; recovery_operation_generation={authority?.OperationGeneration ?? 0}; closure_generation={operation?.ClosureGeneration ?? 0}; closure_state={operation?.ClosureState.ToString() ?? "none"}; bridge_decision_generation={observation.ExecutionCommand.Decision.BridgeDecisionGeneration}; recovery_liveness_lease_generation={observation.ExecutionCommand.RecoveryLivenessLease?.LeaseGeneration ?? 0}; submission_epoch={observation.SubmissionEpoch}; process_epoch={observation.ProcessEpoch}; process_id={observation.ProcessId}; process_spawn_ticks={observation.ProcessSpawnTicks}; heartbeat_identity={FormatProtocolLogValue(observation.HeartbeatIdentity)}; receive_resumed_identity={FormatProtocolLogValue(observation.ReceiveResumedIdentity)}; publication_generation={command?.PublicationGeneration ?? 0}; decision_status={decision?.Status.ToString() ?? "Rejected"}; decision_reason={FormatProtocolLogValue(decision?.Reason ?? "filetransfer_receive_recovery_inbound_operation_missing")}");

        if (publicationContext is not null &&
            command is { } publicationCommand)
        {
            _ = PublishReceiveRecoveryProofStateAsync(
                publicationContext,
                publicationCommand);
        }
    }

    private async Task PublishReceiveRecoveryProofStateAsync(
        InboundTransferContext context,
        FileTransferReceiveRecoveryProofStatePublicationCommand command)
    {
        var sendResult =
            await SendInboundReceiveRecoveryProofStateAsync(
                    context,
                    command)
                .ConfigureAwait(false);
        FileTransferReceiveRecoveryDecision? decision = null;
        FileTransferReceiveRecoveryOperation? operation = null;
        lock (gate)
        {
            if (context.ReceiveRecoveryProofStatePublicationInFlightCommand ==
                command)
            {
                context.ReceiveRecoveryProofStatePublicationInFlightCommand =
                    null;
            }

            if (!ReferenceEquals(inboundTransfer, context))
            {
                return;
            }

            var receiptObservedUtcMs =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var receipt =
                new FileTransferReceiveRecoveryProofStatePublicationReceipt(
                    command,
                    sendResult.Outcome,
                    sendResult.FrameFingerprint,
                    sendResult.Reason);
            decision =
                FileTransferCoordinator
                    .ApplyReceiveRecoveryProofStatePublicationReceipt(
                        CreateCoordinatorStateLocked(context),
                        receipt,
                        receiptObservedUtcMs);
            ApplyReceiveRecoveryStateLocked(context, decision.Value.State);
            operation =
                context.CoordinatorPersistentState.FileTransferReceiveRecovery
                    .CurrentOperation;
        }

        var observation = command.Authority.Observation;
        var predecessorReceipt = command.PredecessorReceipt;
        var recheckEvidence = command.RecheckEvidence;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; session_id={FormatProtocolLogValue(observation.SessionId)}; transfer_id={FormatProtocolLogValue(observation.TransferId)}; direction={observation.Direction.ToString().ToLowerInvariant()}; {FormatReceiveRecoveryObservationScopeFields(observation)}; recovery_operation_authority_id={FormatProtocolLogValue(command.Authority.AuthorityId.ToString())}; recovery_operation_generation={command.Authority.OperationGeneration}; closure_generation={command.ClosureGeneration}; closure_state={operation?.ClosureState.ToString() ?? "none"}; publication_generation={command.PublicationGeneration}; publication_attempt={(predecessorReceipt is null ? "initial" : "post_delivery_recheck")}; predecessor_publication_generation={predecessorReceipt?.Command.PublicationGeneration ?? 0}; predecessor_frame_fingerprint={FormatProtocolLogValue(predecessorReceipt?.FrameFingerprint ?? "(none)")}; predecessor_receipt_observed_utc_ms={command.PredecessorReceiptObservedUtcMs}; recheck_heartbeat_generation={recheckEvidence?.HeartbeatGeneration ?? 0}; recheck_heartbeat_sequence={recheckEvidence?.HeartbeatSequence ?? 0}; recheck_proof_kind={FormatProtocolLogValue(recheckEvidence?.ProofKind ?? "(none)")}; recheck_lane={FormatProtocolLogValue(recheckEvidence?.Lane ?? "(none)")}; submission_epoch={command.Observation.SubmissionEpoch}; process_epoch={command.Observation.ProcessEpoch}; frame_fingerprint={FormatProtocolLogValue(sendResult.FrameFingerprint)}; v4_state_wire_fingerprint={FormatProtocolLogValue(sendResult.FrameFingerprint)}; publication_outcome={sendResult.Outcome}; publication_reason={FormatProtocolLogValue(sendResult.Reason)}; decision_status={decision?.Status.ToString() ?? "Rejected"}; decision_reason={FormatProtocolLogValue(decision?.Reason ?? "filetransfer_receive_recovery_transfer_replaced")}");
    }

    private void ProcessReceiveRecoveryProofObserved(
        IFileTransferBridgeRecoveryExecutionBridge executionBridge,
        FileTransferReceiveRecoveryProofObservation observation)
    {
        FileTransferReceiveRecoveryDecision? decision = null;
        FileTransferReceiveRecoveryOperation? operation = null;
        RuntimeUnlockRetryDispatchDecision? retryDispatchDecision = null;
        lock (gate)
        {
            if (observation.IsDefault ||
                transport is not IFileTransferBridgeRecoveryExecutionBridge currentBridge ||
                !ReferenceEquals(currentBridge, executionBridge))
            {
                return;
            }

            if (outboundTransfer is { IsTerminal: false } outbound &&
                (outbound.CoordinatorPersistentState
                     .FileTransferReceiveRecovery.CurrentOperation?.Authority
                     .ExecutionCommand == observation.Command ||
                 outbound.CoordinatorPersistentState
                     .FileTransferReceiveRecovery.LastTerminalOperation
                     ?.Authority.ExecutionCommand == observation.Command))
            {
                var recovery =
                    outbound.CoordinatorPersistentState
                        .FileTransferReceiveRecovery;
                FileTransferReceiveRecoveryOperation? outboundOperation =
                    recovery.CurrentOperation is { } current &&
                    current.Authority.ExecutionCommand ==
                        observation.Command
                        ? current
                        : recovery.LastTerminalOperation is { } terminal &&
                          terminal.Authority.ExecutionCommand ==
                              observation.Command
                            ? terminal
                            : null;
                if (outboundOperation is { } matchedOutboundOperation)
                {
                    var proof = new FileTransferReceiveRecoveryProof(
                        matchedOutboundOperation.Authority,
                        observation.ProofIdentity,
                        observation.Reason,
                        observation.ObservedUtcMs);
                    decision =
                        FileTransferCoordinator.ObserveReceiveRecoveryProof(
                            CreateCoordinatorStateLocked(outbound),
                            proof);
                    ApplyReceiveRecoveryStateLocked(
                        outbound,
                        decision.Value.State,
                        initialActivationHoldDecision:
                            decision.Value.InitialActivationHoldDecision);
                    retryDispatchDecision =
                        decision.Value.RetryDispatchDecision;
                    if (retryDispatchDecision is
                        { Granted: true } exactRetry)
                    {
                        ApplyReceiveRecoveryStateLocked(
                            outbound,
                            exactRetry.State,
                            queueInitialActivationHoldDirective: false);
                    }
                    var appliedRecovery =
                        outbound.CoordinatorPersistentState
                            .FileTransferReceiveRecovery;
                    operation =
                        appliedRecovery.CurrentOperation is { } appliedCurrent &&
                        appliedCurrent.Authority ==
                            matchedOutboundOperation.Authority
                            ? appliedCurrent
                            : appliedRecovery.LastTerminalOperation is
                                  { } appliedTerminal &&
                              appliedTerminal.Authority ==
                                  matchedOutboundOperation.Authority
                                ? appliedTerminal
                                : null;
                }
            }
            else if (inboundTransfer is { IsTerminal: false } inbound &&
                     (inbound.CoordinatorPersistentState
                          .FileTransferReceiveRecovery.CurrentOperation
                          ?.Authority.ExecutionCommand ==
                      observation.Command ||
                      inbound.CoordinatorPersistentState
                          .FileTransferReceiveRecovery.LastTerminalOperation
                          ?.Authority.ExecutionCommand ==
                      observation.Command))
            {
                var recovery =
                    inbound.CoordinatorPersistentState
                        .FileTransferReceiveRecovery;
                FileTransferReceiveRecoveryOperation? inboundOperation =
                    recovery.CurrentOperation is { } current &&
                    current.Authority.ExecutionCommand ==
                        observation.Command
                        ? current
                        : recovery.LastTerminalOperation is { } terminal &&
                          terminal.Authority.ExecutionCommand ==
                              observation.Command
                            ? terminal
                            : null;
                if (inboundOperation is { } matchedInboundOperation)
                {
                    var proof = new FileTransferReceiveRecoveryProof(
                        matchedInboundOperation.Authority,
                        observation.ProofIdentity,
                        observation.Reason,
                        observation.ObservedUtcMs);
                    decision =
                        FileTransferCoordinator.ObserveReceiveRecoveryProof(
                            CreateCoordinatorStateLocked(inbound),
                            proof);
                    ApplyReceiveRecoveryStateLocked(
                        inbound,
                        decision.Value.State,
                        initialActivationHoldDecision:
                            decision.Value.InitialActivationHoldDecision);
                    retryDispatchDecision =
                        decision.Value.RetryDispatchDecision;
                    if (retryDispatchDecision is
                        { Granted: true } exactRetry)
                    {
                        ApplyReceiveRecoveryStateLocked(
                            inbound,
                            exactRetry.State,
                            queueInitialActivationHoldDirective: false);
                    }
                    var appliedRecovery =
                        inbound.CoordinatorPersistentState
                            .FileTransferReceiveRecovery;
                    operation =
                        appliedRecovery.CurrentOperation is { } appliedCurrent &&
                        appliedCurrent.Authority ==
                            matchedInboundOperation.Authority
                            ? appliedCurrent
                            : appliedRecovery.LastTerminalOperation is
                                  { } appliedTerminal &&
                              appliedTerminal.Authority ==
                                  matchedInboundOperation.Authority
                                ? appliedTerminal
                                : null;
                }
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_operation_proof_decision_observed; session_id={FormatProtocolLogValue(observation.Command.Decision.RuntimeOwner.SessionId)}; transfer_id={FormatProtocolLogValue(observation.Command.Decision.RuntimeOwner.TransferId)}; direction={observation.Command.Decision.RuntimeOwner.Direction.ToString().ToLowerInvariant()}; recovery_operation_generation={operation?.Authority.OperationGeneration ?? 0}; bridge_decision_generation={observation.Command.Decision.BridgeDecisionGeneration}; recovery_liveness_lease_generation={observation.Command.RecoveryLivenessLease?.LeaseGeneration ?? 0}; proof_identity={FormatProtocolLogValue(observation.ProofIdentity)}; closure_state={operation?.ClosureState.ToString() ?? "none"}; decision_status={decision?.Status.ToString() ?? "Rejected"}; decision_reason={FormatProtocolLogValue(decision?.Reason ?? "filetransfer_receive_recovery_operation_missing")}");
        ProcessReceiveRecoveryRetryDispatchDecision(retryDispatchDecision);
    }

    private void ProcessReceiveRecoveryRetryDispatchDecision(
        RuntimeUnlockRetryDispatchDecision? decision)
    {
        if (decision is not { } exactDecision)
        {
            return;
        }

        IRuntimeUnlockRetryDispatchAuthorityBridge? retryBridge;
        lock (gate)
        {
            retryBridge = transport as
                IRuntimeUnlockRetryDispatchAuthorityBridge;
            if (retryBridge is null)
            {
                _ = RejectRuntimeUnlockRetryDispatchInstallLocked(
                    exactDecision,
                    "retry_dispatch_install_bridge_missing");
                return;
            }
        }

        ProcessRuntimeUnlockRetryDispatchDecision(
            retryBridge,
            exactDecision,
            exactDecision.Contract?.Authority
                .ReceiveRecoveryDataSessionGeneration ?? 0);
    }

    private void ProcessBridgeRecoveryEvaluationRequested(
        IFileTransferBridgeRecoveryExecutionBridge executionBridge,
        FileTransferBridgeRecoveryEvaluationRequest evaluation)
    {
        FileTransferReceiveRecoveryRequest? recoveryRequest = null;
        string dispositionReason;
        lock (gate)
        {
            if (evaluation.IsDefault)
            {
                dispositionReason = "bridge_recovery_evaluation_invalid";
            }
            else if (transport is not IFileTransferBridgeRecoveryExecutionBridge currentBridge ||
                     !ReferenceEquals(currentBridge, executionBridge))
            {
                dispositionReason = "bridge_recovery_evaluation_transport_replaced";
            }
            else
            {
                recoveryRequest = ResolveBridgeRecoveryEvaluationRequestLocked(
                    evaluation,
                    out dispositionReason);
            }
        }

        var requested = recoveryRequest is not null &&
                        TryRequestFileTransferReceiveRecovery(recoveryRequest);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_evaluation_requested; session_id={FormatProtocolLogValue(recoveryRequest?.SessionId ?? evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(recoveryRequest?.TransferId ?? evaluation.TransferId)}; direction={recoveryRequest?.Direction.ToString().ToLowerInvariant() ?? evaluation.Direction?.ToString().ToLowerInvariant() ?? "unknown"}; evaluation_kind={FormatProtocolLogValue(evaluation.EvaluationKind.ToString())}; observation_kind={FormatProtocolLogValue(evaluation.ObservationKind)}; observation_generation={evaluation.ObservationGeneration}; observation_source=infra_health_evidence; observation_reason={FormatProtocolLogValue(evaluation.Reason)}; coordinator_evaluation_requested={(recoveryRequest is not null ? 1 : 0)}; receive_recovery_operation_issued={(requested ? 1 : 0)}; decision_reason={FormatProtocolLogValue(dispositionReason)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bridge_recovery_evaluation_decided; session_id={FormatProtocolLogValue(recoveryRequest?.SessionId ?? evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(recoveryRequest?.TransferId ?? evaluation.TransferId)}; direction={recoveryRequest?.Direction.ToString().ToLowerInvariant() ?? evaluation.Direction?.ToString().ToLowerInvariant() ?? "unknown"}; evaluation_kind={FormatProtocolLogValue(evaluation.EvaluationKind.ToString())}; observation_kind={FormatProtocolLogValue(evaluation.ObservationKind)}; observation_generation={evaluation.ObservationGeneration}; consecutive_observations={evaluation.ConsecutiveObservations}; frames_sent_since_last={evaluation.FramesSentSinceLast}; messages_received_since_last={evaluation.MessagesReceivedSinceLast}; control_last_received_age_ms={evaluation.ControlLastReceivedAgeMs}; bulk_last_received_age_ms={evaluation.BulkLastReceivedAgeMs}; coordinator_evaluation_requested={(recoveryRequest is not null ? 1 : 0)}; bridge_recovery_command_issued={(requested ? 1 : 0)}; decision_reason={FormatProtocolLogValue(dispositionReason)}");
    }

    private FileTransferReceiveRecoveryRequest?
        ResolveBridgeRecoveryEvaluationRequestLocked(
            FileTransferBridgeRecoveryEvaluationRequest evaluation,
            out string reason)
    {
        var outbound = outboundTransfer;
        var inbound = inboundTransfer;
        var outboundMatches =
            outbound is { IsTerminal: false } &&
            (evaluation.Direction is null ||
             evaluation.Direction == FileTransferDirection.Outbound) &&
            (string.IsNullOrWhiteSpace(evaluation.SessionId) ||
             string.Equals(evaluation.SessionId, outbound.SessionId, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(evaluation.TransferId) ||
             string.Equals(evaluation.TransferId, outbound.TransferId, StringComparison.Ordinal));
        var inboundMatches =
            inbound is { IsTerminal: false } &&
            (evaluation.Direction is null ||
             evaluation.Direction == FileTransferDirection.Inbound) &&
            (string.IsNullOrWhiteSpace(evaluation.SessionId) ||
             string.Equals(evaluation.SessionId, inbound.SessionId, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(evaluation.TransferId) ||
             string.Equals(evaluation.TransferId, inbound.TransferId, StringComparison.Ordinal));

        if (outboundMatches == inboundMatches)
        {
            reason = outboundMatches
                ? "bridge_recovery_evaluation_transfer_ambiguous"
                : "bridge_recovery_evaluation_transfer_missing";
            return null;
        }

        if (outboundMatches)
        {
            if (evaluation.ObservationGeneration <=
                outbound!.LastBridgeRecoveryEvaluationObservationGeneration)
            {
                reason = "bridge_recovery_evaluation_duplicate";
                return null;
            }

            outbound.LastBridgeRecoveryEvaluationObservationGeneration =
                evaluation.ObservationGeneration;
            outbound.LastReceiveRecoveryHealthEvaluationGeneration =
                Math.Max(
                    outbound.LastReceiveRecoveryHealthEvaluationGeneration,
                    GetReceiveRecoveryHealthEvaluationGeneration(
                        outbound.CoordinatorPersistentState));
            if (outbound.LastReceiveRecoveryHealthEvaluationGeneration ==
                long.MaxValue)
            {
                reason =
                    "bridge_recovery_evaluation_health_generation_exhausted";
                return null;
            }

            var healthEvaluationGeneration =
                ++outbound.LastReceiveRecoveryHealthEvaluationGeneration;
            reason = "bridge_recovery_evaluation_forwarded";
            var evidence = CreateReceiveRecoveryHealthEvaluationEvidence(
                evaluation,
                healthEvaluationGeneration);
            return new FileTransferReceiveRecoveryRequest(
                outbound.SessionId,
                outbound.TransferId,
                FileTransferDirection.Outbound,
                evaluation.Reason)
            {
                Observation = CreateReceiveRecoveryObservationSnapshotLocked(
                    outbound,
                    FileTransferDirection.Outbound,
                    evaluation.ObservationKind,
                    evaluation.Reason,
                    $"{evaluation.ObservationKind}:{evaluation.ObservationGeneration}",
                    evaluation.ObservedUtcMs,
                    evidence),
            };
        }

        if (evaluation.ObservationGeneration <=
            inbound!.LastBridgeRecoveryEvaluationObservationGeneration)
        {
            reason = "bridge_recovery_evaluation_duplicate";
            return null;
        }

        inbound.LastBridgeRecoveryEvaluationObservationGeneration =
            evaluation.ObservationGeneration;
        inbound.LastReceiveRecoveryHealthEvaluationGeneration =
            Math.Max(
                inbound.LastReceiveRecoveryHealthEvaluationGeneration,
                GetReceiveRecoveryHealthEvaluationGeneration(
                    inbound.CoordinatorPersistentState));
        if (inbound.LastReceiveRecoveryHealthEvaluationGeneration ==
            long.MaxValue)
        {
            reason =
                "bridge_recovery_evaluation_health_generation_exhausted";
            return null;
        }

        var inboundHealthEvaluationGeneration =
            ++inbound.LastReceiveRecoveryHealthEvaluationGeneration;
        reason = "bridge_recovery_evaluation_forwarded";
        var inboundEvidence = CreateReceiveRecoveryHealthEvaluationEvidence(
            evaluation,
            inboundHealthEvaluationGeneration);
        return new FileTransferReceiveRecoveryRequest(
            inbound.SessionId,
            inbound.TransferId,
            FileTransferDirection.Inbound,
            evaluation.Reason)
        {
            Observation = CreateReceiveRecoveryObservationSnapshotLocked(
                inbound,
                FileTransferDirection.Inbound,
                evaluation.ObservationKind,
                evaluation.Reason,
                $"{evaluation.ObservationKind}:{evaluation.ObservationGeneration}",
                evaluation.ObservedUtcMs,
                inboundEvidence),
        };
    }

    private static FileTransferReceiveRecoveryHealthEvaluationEvidence
        CreateReceiveRecoveryHealthEvaluationEvidence(
            FileTransferBridgeRecoveryEvaluationRequest evaluation,
            long evidenceGeneration)
        => new(
            evaluation.EvaluationKind,
            evaluation.ObservationKind,
            evaluation.Reason,
            evidenceGeneration,
            evaluation.ObservedUtcMs,
            evaluation.ConsecutiveObservations,
            evaluation.FramesSentSinceLast,
            evaluation.MessagesReceivedSinceLast,
            evaluation.ControlLastReceivedAgeMs,
            evaluation.BulkLastReceivedAgeMs);

    private static long GetReceiveRecoveryHealthEvaluationGeneration(
        FileTransferCoordinatorPersistentState persistentState)
        => Math.Max(
            persistentState.FileTransferReceiveRecovery.CurrentOperation
                ?.Authority.Observation.HealthEvaluationEvidence
                ?.EvidenceGeneration ?? 0,
            persistentState.FileTransferReceiveRecovery.LastTerminalOperation
                ?.Authority.Observation.HealthEvaluationEvidence
                ?.EvidenceGeneration ?? 0);

    private bool TryRequestFileTransferReceiveRecoveryIfCurrent(
        FileTransferReceiveRecoveryRequest request,
        OutboundSparsePumpInstall? requiredSparsePumpInstall,
        IFileTransferDataSession? requiredDataSession,
        bool allowObservationCapture = true)
    {
        IFileTransferBridgeRecoveryExecutionBridge? executionBridge;
        FileTransferBridgeRecoveryPolicy bridgeRecoveryPolicy;
        var regularV4ReplayArmed = false;
        OutboundTransferContext? regularV4RepairContext = null;
        FileTransferRegularV4FrontierRepairAuthority? regularV4RepairAuthority = null;
        lock (gate)
        {
            if (requiredSparsePumpInstall is not null)
            {
                var sparseInstallRejectionReason = "transfer_identity_mismatch";
                var sparseInstallCurrent =
                    request.Direction == FileTransferDirection.Outbound &&
                    outboundTransfer is { } requiredOutbound &&
                    string.Equals(requiredOutbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
                    string.Equals(requiredOutbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
                    IsOutboundSparsePumpInstallCurrentLocked(
                        requiredOutbound,
                        requiredSparsePumpInstall,
                        requiredDataSession,
                        out sparseInstallRejectionReason);
                if (!sparseInstallCurrent)
                {
                    if (outboundTransfer is { } rejectedOutbound)
                    {
                        LogOutboundSparsePumpInstallRejected(
                            rejectedOutbound,
                            requiredSparsePumpInstall,
                            "receive_recovery_request",
                            sparseInstallRejectionReason);
                    }

                    return false;
                }
            }

            if (!TryResolveReceiveRecoveryObservationLocked(
                    request,
                    allowObservationCapture,
                    out request,
                    out var observationRejectionReason))
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receive_recovery_observation_rejected; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(request.TransferId)}; session_id={FormatProtocolLogValue(request.SessionId)}; source=filetransfer_receive_recovery; reason={FormatProtocolLogValue(observationRejectionReason)}");
                return false;
            }
            if (TrySuppressReceiveRecoveryForSupersededRegularV4RuntimeLocked(request))
            {
                return false;
            }

            if (request.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { } outbound &&
                string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal))
            {
                if (!TryAuthorizeBridgeRecoveryLocked(
                        outbound,
                        request,
                        "filetransfer_receive_recovery",
                        request.Reason,
                        out request))
                {
                    return false;
                }
            }
            else if (request.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
                     string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal))
            {
                if (!TryAuthorizeBridgeRecoveryLocked(
                        inbound,
                        request,
                        "filetransfer_receive_recovery",
                        request.Reason,
                        out request))
                {
                    return false;
                }
            }
            else
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_bridge_recovery_authority_rejected; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(request.TransferId)}; session_id={FormatProtocolLogValue(request.SessionId)}; action_kind=ReceiveRecoveryOperation; source=filetransfer_receive_recovery; attempted_action={FormatProtocolLogValue(request.Reason)}; rejection_reason=current_transfer_missing; reason=current_transfer_missing{FormatReceiveRecoveryAuthorityFields(request)}");
                return false;
            }

            executionBridge = transport as IFileTransferBridgeRecoveryExecutionBridge;
            bridgeRecoveryPolicy = ResolveReceiveRecoveryPolicyForRequestLocked(request);
        }

        if (executionBridge is null)
        {
            InvalidateUndispatchedReceiveRecoveryOperation(
                request,
                "bridge_recovery_execution_bridge_missing");
            var authorityFields = FormatReceiveRecoveryAuthorityFields(request);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_unsupported; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}; rejection_reason=bridge_recovery_execution_bridge_missing{authorityFields}");
            return false;
        }

        var executionStarted = false;
        try
        {
            var executionCommand = request.BridgeRecoveryExecutionCommand!.Value;
            BridgeRecoveryExecutionBeforeFinalDispatchForTests?.Invoke();
            var dispatchGateHeld = false;
            FileTransferBridgeRecoveryExecutionStartResult startResult;
            var commandCurrent = false;
            try
            {
                lock (gate)
                {
                    Monitor.Enter(
                        bridgeRecoveryExecutionDispatchOrdering.Gate,
                        ref dispatchGateHeld);
                    commandCurrent = IsBridgeRecoveryExecutionCommandCurrentLocked(
                        request,
                        FileTransferBridgeRecoveryExecutionState.Issued);
                }

                FlushBridgeRecoveryExecutionInvalidationsUnsafe(
                    bridgeRecoveryExecutionDispatchOrdering);
                if (!commandCurrent)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_bridge_recovery_authority_rejected; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(request.TransferId)}; session_id={FormatProtocolLogValue(request.SessionId)}; action_kind=ReceiveRecoveryOperation; source=filetransfer_receive_recovery; attempted_action={FormatProtocolLogValue(request.Reason)}; rejection_reason=bridge_recovery_execution_command_stale_before_dispatch; reason=bridge_recovery_execution_command_stale_before_dispatch{FormatReceiveRecoveryAuthorityFields(request)}");
                    return false;
                }

                startResult = executionBridge.BeginBridgeRecoveryExecution(executionCommand);
            }
            finally
            {
                if (dispatchGateHeld)
                {
                    Monitor.Exit(bridgeRecoveryExecutionDispatchOrdering.Gate);
                }
            }

            FileTransferReceiveRecoveryDecision? startDecision;
            lock (gate)
            {
                startDecision = ObserveBridgeRecoveryExecutionStartLocked(request, startResult);
                if (startDecision is
                    {
                        Status: FileTransferReceiveRecoveryTransitionStatus.Accepted or
                            FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                    } &&
                    IsBridgeRecoveryExecutionCommandCurrentLocked(
                        request,
                        FileTransferBridgeRecoveryExecutionState.Running))
                {
                    regularV4ReplayArmed = TryQueueOutboundRegularNknV4ReceiveRecoveryReplayLocked(request);
                    if (request.Direction == FileTransferDirection.Outbound &&
                        outboundTransfer is { } regularV4Outbound &&
                        string.Equals(regularV4Outbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
                        string.Equals(regularV4Outbound.TransferId, request.TransferId, StringComparison.Ordinal))
                    {
                        regularV4RepairContext = regularV4Outbound;
                        regularV4RepairAuthority = GetCurrentRegularV4FrontierRepairAuthority(regularV4Outbound);
                    }
                }
                else
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_bridge_recovery_dispatch_became_stale; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(request.TransferId)}; session_id={FormatProtocolLogValue(request.SessionId)}; reason=bridge_recovery_execution_start_not_current; start_status={startResult.Status}; start_reason={FormatProtocolLogValue(startResult.Reason)}; coordinator_decision_status={startDecision?.Status.ToString() ?? "missing"}; coordinator_decision_reason={FormatProtocolLogValue(startDecision?.Reason ?? "current_transfer_missing")}{FormatReceiveRecoveryAuthorityFields(request)}");
                }
            }

            if (startDecision is not
                {
                    Status: FileTransferReceiveRecoveryTransitionStatus.Accepted or
                        FileTransferReceiveRecoveryTransitionStatus.Idempotent,
                } ||
                startResult.Completion is null)
            {
                InvalidateUndispatchedReceiveRecoveryOperation(
                    request,
                    startDecision?.Reason ?? "bridge_recovery_execution_start_rejected");
                return false;
            }

            executionStarted = true;
            _ = ObserveBridgeRecoveryExecutionCompletionAsync(
                request,
                startResult.Completion,
                executionBridge);

            if (regularV4RepairContext is { } repairContext &&
                regularV4RepairAuthority is { } repairAuthority)
            {
                ObserveRegularV4FrontierRepairDispatchBlocker(
                    repairContext,
                    repairAuthority,
                    repairAuthority.PendingRepairCount,
                    "regular_v4_receive_recovery_repair_armed");
            }

            var authorityFields = FormatReceiveRecoveryAuthorityFields(request);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}; regular_v4_peer_silence_replay_armed={(regularV4ReplayArmed ? 1 : 0)}{authorityFields}");
            return true;
        }
        catch (Exception ex)
        {
            if (!executionStarted)
            {
                InvalidateUndispatchedReceiveRecoveryOperation(
                    request,
                    $"bridge_recovery_execution_begin_{ex.GetType().Name}");
            }
            var authorityFields = FormatReceiveRecoveryAuthorityFields(request);
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_failed; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; error={FormatProtocolLogValue(ex.GetType().Name)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}{authorityFields}");
            return executionStarted;
        }
    }

    private bool IsBridgeRecoveryExecutionCommandCurrentLocked(
        FileTransferReceiveRecoveryRequest request,
        FileTransferBridgeRecoveryExecutionState expectedState)
    {
        if (request.BridgeRecoveryExecutionCommand is not { IsDefault: false } command ||
            request.ReceiveRecoveryOperationAuthority is not
                { IsDefault: false } authority)
        {
            return false;
        }

        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var state = CreateCoordinatorStateLocked(outbound);
            var operation = state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation;
            return operation?.Authority == authority &&
                   operation?.Authority.ExecutionCommand == command &&
                   IsReceiveRecoveryExecutionState(
                       operation?.ExecutionState,
                       expectedState) &&
                   FileTransferCoordinator.IsReceiveRecoveryOperationCurrent(
                       state,
                       authority);
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var state = CreateCoordinatorStateLocked(inbound);
            var operation = state.PersistentState.FileTransferReceiveRecovery
                .CurrentOperation;
            return operation?.Authority == authority &&
                   operation?.Authority.ExecutionCommand == command &&
                   IsReceiveRecoveryExecutionState(
                       operation?.ExecutionState,
                       expectedState) &&
                   FileTransferCoordinator.IsReceiveRecoveryOperationCurrent(
                       state,
                       authority);
        }

        return false;
    }

    private static bool IsReceiveRecoveryExecutionState(
        FileTransferReceiveRecoveryExecutionState? operationState,
        FileTransferBridgeRecoveryExecutionState expectedState)
        => expectedState switch
        {
            FileTransferBridgeRecoveryExecutionState.Issued =>
                operationState == FileTransferReceiveRecoveryExecutionState.Issued,
            FileTransferBridgeRecoveryExecutionState.Running =>
                operationState == FileTransferReceiveRecoveryExecutionState.Running,
            _ => false,
        };

    private FileTransferReceiveRecoveryDecision?
        ObserveBridgeRecoveryExecutionStartLocked(
            FileTransferReceiveRecoveryRequest request,
            FileTransferBridgeRecoveryExecutionStartResult result)
    {
        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(
                CreateCoordinatorStateLocked(outbound),
                result,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyReceiveRecoveryStateLocked(outbound, decision.State);
            return decision;
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(
                CreateCoordinatorStateLocked(inbound),
                result,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyReceiveRecoveryStateLocked(inbound, decision.State);
            return decision;
        }

        return null;
    }

    private FileTransferReceiveRecoveryDecision?
        ObserveBridgeRecoveryExecutionReceiptLocked(
            FileTransferReceiveRecoveryRequest request,
            FileTransferBridgeRecoveryExecutionReceipt receipt)
    {
        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
                CreateCoordinatorStateLocked(outbound),
                receipt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyReceiveRecoveryStateLocked(
                outbound,
                decision.State,
                initialActivationHoldDecision:
                    decision.InitialActivationHoldDecision);
            if (decision.RetryDispatchDecision is { Granted: true } retry)
            {
                ApplyReceiveRecoveryStateLocked(
                    outbound,
                    retry.State,
                    queueInitialActivationHoldDirective: false);
                return decision with { State = retry.State };
            }
            return decision;
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
                CreateCoordinatorStateLocked(inbound),
                receipt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyReceiveRecoveryStateLocked(
                inbound,
                decision.State,
                initialActivationHoldDecision:
                    decision.InitialActivationHoldDecision);
            if (decision.RetryDispatchDecision is { Granted: true } retry)
            {
                ApplyReceiveRecoveryStateLocked(
                    inbound,
                    retry.State,
                    queueInitialActivationHoldDirective: false);
                return decision with { State = retry.State };
            }
            return decision;
        }

        return null;
    }

    private async Task ObserveBridgeRecoveryExecutionCompletionAsync(
        FileTransferReceiveRecoveryRequest request,
        Task<FileTransferBridgeRecoveryExecutionReceipt> completion,
        IFileTransferBridgeRecoveryExecutionBridge executionBridge)
    {
        FileTransferBridgeRecoveryExecutionReceipt receipt;
        try
        {
            receipt = await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_bridge_recovery_execution_completion_task_failed; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; bridge_decision_generation={request.BridgeRecoveryExecutionCommand?.Decision.BridgeDecisionGeneration ?? 0}; failure_kind=canceled; decision_reason=physical_worker_terminal_receipt_missing");
            return;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_bridge_recovery_execution_completion_task_failed; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; bridge_decision_generation={request.BridgeRecoveryExecutionCommand?.Decision.BridgeDecisionGeneration ?? 0}; failure_kind={FormatProtocolLogValue(ex.GetType().Name)}; decision_reason=physical_worker_terminal_receipt_missing");
            return;
        }

        FileTransferReceiveRecoveryDecision? executionDecision;
        FileTransferReceiveRecoveryOperation? completedOperation = null;
        RuntimeUnlockRetryDispatchDecision? retryDispatchDecision = null;
        OutboundTransferContext? fallbackCheckpointToResume = null;
        lock (gate)
        {
            executionDecision = ObserveBridgeRecoveryExecutionReceiptLocked(request, receipt);
            completedOperation = executionDecision?.State.PersistentState
                .FileTransferReceiveRecovery.CurrentOperation ??
                executionDecision?.State.PersistentState
                    .FileTransferReceiveRecovery.LastTerminalOperation;
            retryDispatchDecision = executionDecision?.RetryDispatchDecision;
            if (receipt.Outcome ==
                    FileTransferBridgeRecoveryExecutionOutcome.Completed &&
                executionDecision?.Status ==
                    FileTransferReceiveRecoveryTransitionStatus.Accepted &&
                request.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { } outbound &&
                string.Equals(
                    outbound.SessionId,
                    request.SessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    outbound.TransferId,
                    request.TransferId,
                    StringComparison.Ordinal))
            {
                fallbackCheckpointToResume = outbound;
            }
        }

        var receiptAccepted = executionDecision is
        {
            Status: FileTransferReceiveRecoveryTransitionStatus.Accepted or
                FileTransferReceiveRecoveryTransitionStatus.Idempotent,
        };
        var receiptNotificationSucceeded = false;
        try
        {
            executionBridge.ObserveBridgeRecoveryExecutionReceiptDecision(
                receipt,
                receiptAccepted,
                executionDecision?.Reason ?? "current_transfer_missing");
            receiptNotificationSucceeded = true;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_bridge_recovery_execution_receipt_notification_failed; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; bridge_decision_generation={receipt.Command.Decision.BridgeDecisionGeneration}; execution_outcome={receipt.Outcome}; decision_status={executionDecision?.Status.ToString() ?? "missing"}; reason={FormatProtocolLogValue(ex.GetType().Name)}");
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bridge_recovery_execution_completion_decided; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; execution_outcome={receipt.Outcome}; execution_mode={FormatProtocolLogValue(receipt.ExecutionMode)}; execution_reason={FormatProtocolLogValue(receipt.Reason)}; bridge_decision_generation={receipt.Command.Decision.BridgeDecisionGeneration}; lifecycle_source=receive_recovery_operation; receipt_notification_succeeded={(receiptNotificationSucceeded ? 1 : 0)}; decision_status={executionDecision?.Status.ToString() ?? "missing"}; decision_reason={FormatProtocolLogValue(executionDecision?.Reason ?? "current_transfer_missing")}; lifecycle_status={completedOperation?.ExecutionState.ToString() ?? "none"}; lifecycle_reason={FormatProtocolLogValue(receipt.Reason)}{FormatReceiveRecoveryAuthorityFields(request)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_operation_execution_receipt_decision_observed; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; recovery_operation_generation={completedOperation?.Authority.OperationGeneration ?? request.ReceiveRecoveryOperationAuthority?.OperationGeneration ?? 0}; bridge_decision_generation={receipt.Command.Decision.BridgeDecisionGeneration}; recovery_liveness_lease_generation={receipt.Command.RecoveryLivenessLease?.LeaseGeneration ?? 0}; execution_outcome={receipt.Outcome}; execution_mode={FormatProtocolLogValue(receipt.ExecutionMode)}; closure_generation={completedOperation?.ClosureGeneration ?? 0}; closure_state={completedOperation?.ClosureState.ToString() ?? "none"}; decision_status={executionDecision?.Status.ToString() ?? "missing"}; decision_reason={FormatProtocolLogValue(executionDecision?.Reason ?? "current_transfer_missing")}");
        ProcessReceiveRecoveryRetryDispatchDecision(retryDispatchDecision);
        if (fallbackCheckpointToResume is not null)
        {
            QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume(
                fallbackCheckpointToResume,
                "receive_recovery_completed",
                FileTransferReceiveRecoveryFallbackCheckpointResumeTrigger
                    .RecoveryCompleted);
        }
    }

    private void InvalidateUndispatchedReceiveRecoveryOperation(
        FileTransferReceiveRecoveryRequest request,
        string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "receive_recovery_dispatch_failed"
            : reason.Trim();
        if (request.ReceiveRecoveryOperationAuthority is not
            { IsDefault: false } authority)
        {
            return;
        }

        FileTransferReceiveRecoveryDecision? invalidationDecision = null;
        BridgeRecoveryExecutionDispatchOrdering? dispatchOrdering = null;
        lock (gate)
        {
            if (request.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { } outbound &&
                string.Equals(
                    outbound.SessionId,
                    request.SessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    outbound.TransferId,
                    request.TransferId,
                    StringComparison.Ordinal))
            {
                var previousCommand = authority.ExecutionCommand;
                invalidationDecision =
                    FileTransferCoordinator.InvalidateReceiveRecoveryOperation(
                        CreateCoordinatorStateLocked(outbound),
                        authority,
                        normalizedReason);
                if (invalidationDecision.Value.Accepted)
                {
                    ApplyReceiveRecoveryStateLocked(
                        outbound,
                        invalidationDecision.Value.State,
                        initialActivationHoldDecision:
                            invalidationDecision.Value
                                .InitialActivationHoldDecision);
                    QueueBridgeRecoveryExecutionInvalidationLocked(
                        previousCommand,
                        outbound.CurrentBridgeRecoveryExecutionCommand,
                        outbound.BridgeRecoveryExecutionBridge,
                        outbound.BridgeRecoveryExecutionDispatchOrdering,
                        normalizedReason);
                    dispatchOrdering =
                        outbound.BridgeRecoveryExecutionDispatchOrdering;
                }
            }
            else if (request.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     string.Equals(
                         inbound.SessionId,
                         request.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         inbound.TransferId,
                         request.TransferId,
                         StringComparison.Ordinal))
            {
                var previousCommand = authority.ExecutionCommand;
                invalidationDecision =
                    FileTransferCoordinator.InvalidateReceiveRecoveryOperation(
                        CreateCoordinatorStateLocked(inbound),
                        authority,
                        normalizedReason);
                if (invalidationDecision.Value.Accepted)
                {
                    ApplyReceiveRecoveryStateLocked(
                        inbound,
                        invalidationDecision.Value.State,
                        initialActivationHoldDecision:
                            invalidationDecision.Value
                                .InitialActivationHoldDecision);
                    QueueBridgeRecoveryExecutionInvalidationLocked(
                        previousCommand,
                        inbound.CurrentBridgeRecoveryExecutionCommand,
                        inbound.BridgeRecoveryExecutionBridge,
                        inbound.BridgeRecoveryExecutionDispatchOrdering,
                        normalizedReason);
                    dispatchOrdering =
                        inbound.BridgeRecoveryExecutionDispatchOrdering;
                }
            }
        }

        if (dispatchOrdering is not null)
        {
            FlushBridgeRecoveryExecutionInvalidations(dispatchOrdering);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_operation_invalidation_decided; session_id={FormatProtocolLogValue(request.SessionId)}; transfer_id={FormatProtocolLogValue(request.TransferId)}; direction={request.Direction.ToString().ToLowerInvariant()}; recovery_operation_generation={authority.OperationGeneration}; bridge_decision_generation={authority.BridgeRecoveryDecision.BridgeDecisionGeneration}; decision_status={invalidationDecision?.Status.ToString() ?? "missing"}; decision_reason={FormatProtocolLogValue(invalidationDecision?.Reason ?? "current_transfer_missing")}; invalidation_reason={FormatProtocolLogValue(normalizedReason)}");
    }

    private bool TryQueueOutboundRegularNknV4ReceiveRecoveryReplayLocked(FileTransferReceiveRecoveryRequest request)
    {
        if (request.Direction != FileTransferDirection.Outbound ||
            outboundTransfer is not { } context ||
            !string.Equals(context.TransferId, request.TransferId, StringComparison.Ordinal) ||
            !string.Equals(context.SessionId, request.SessionId, StringComparison.Ordinal) ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.RouteRuntime.FrameFamily != FileTransferFrameFamily.V4)
        {
            return false;
        }

        var queuedBefore = context.PullV4SenderPumpRepairQueue.Count;
        var replayCountBefore = context.PullRegularNknV4PeerSilenceSafetyReplayCount;
        MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(
            context,
            string.IsNullOrWhiteSpace(request.Reason)
                ? "receive_recovery"
                : request.Reason);

        var replayArmed = context.PullRegularNknV4PeerSilenceSafetyReplayCount > replayCountBefore ||
            context.PullV4SenderPumpRepairQueue.Count > queuedBefore;
        if (replayArmed)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_regular_v4_receive_recovery_peer_silence_replay_armed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; queued_before={queuedBefore}; queued_after={context.PullV4SenderPumpRepairQueue.Count}; replay_count_before={replayCountBefore}; replay_count_after={context.PullRegularNknV4PeerSilenceSafetyReplayCount}");
        }

        return replayArmed;
    }

    private bool TrySuppressReceiveRecoveryForSupersededRegularV4RuntimeLocked(
        FileTransferReceiveRecoveryRequest request)
    {
        if (!string.Equals(
                request.RouteToken,
                FileTransferRouteResolver.RegularNknV4FastToken,
                StringComparison.Ordinal) ||
            request.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            transport is not IFileTransferRouteStatus routeStatus ||
            (!routeStatus.IsFileTunaActiveForRouteSelection &&
             !routeStatus.IsPostTunaFileFallbackActiveForRouteSelection))
        {
            return false;
        }

        var currentSelection = FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            routeStatus.IsFileTunaActiveForRouteSelection,
            routeStatus.IsPostTunaFileFallbackActiveForRouteSelection,
            routeStatus.IsDiagnosticRegularNknV6RouteEnabled,
            FileTransferTransportHandoffKind.None,
            transport is IFileTransferTransportProfileProvider profile
                ? profile.FileTransferTransportProfileKind
                : FileTransferTransportProfileKind.Default));

        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            RejectSupersededRegularV4ReceiveRecoveryLocked(outbound, request, currentSelection);
            return true;
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            RejectSupersededRegularV4ReceiveRecoveryLocked(inbound, request, currentSelection);
            return true;
        }

        return false;
    }

    private static void RejectSupersededRegularV4ReceiveRecoveryLocked(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        FileTransferRouteSelection currentSelection)
    {
        if (TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                "regular_v4_receive_recovery",
                "receive_recovery_request",
                out var attemptedOwner))
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                attemptedOwner,
                "regular_v4_receive_recovery",
                "receive_recovery_request",
                "transport_route_status_superseded_regular_v4");
        }

        LogSupersededRegularV4ReceiveRecoverySuppressed(
            FileTransferDirection.Outbound,
            request,
            currentSelection);
    }

    private static void RejectSupersededRegularV4ReceiveRecoveryLocked(
        InboundTransferContext context,
        FileTransferReceiveRecoveryRequest request,
        FileTransferRouteSelection currentSelection)
    {
        if (TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                "regular_v4_receive_recovery",
                "receive_recovery_request",
                out var attemptedOwner))
        {
            RejectStaleRuntimeOwnerUpdateLocked(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentRuntimeOwner,
                attemptedOwner,
                "regular_v4_receive_recovery",
                "receive_recovery_request",
                "transport_route_status_superseded_regular_v4");
        }

        LogSupersededRegularV4ReceiveRecoverySuppressed(
            FileTransferDirection.Inbound,
            request,
            currentSelection);
    }

    private static void LogSupersededRegularV4ReceiveRecoverySuppressed(
        FileTransferDirection direction,
        FileTransferReceiveRecoveryRequest request,
        FileTransferRouteSelection currentSelection)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_regular_v4_receive_recovery_suppressed_by_current_route_owner; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(request.TransferId)}; session_id={FormatProtocolLogValue(request.SessionId)}; reason={FormatProtocolLogValue(request.Reason)}; suppression_reason=transport_route_status_superseded_regular_v4; attempted_route={FormatProtocolLogValue(request.RouteToken ?? "(none)")}; attempted_protocol_version={request.ProtocolVersion}; current_route={FormatProtocolLogValue(currentSelection.TelemetryToken)}; current_protocol_version={currentSelection.ProtocolVersion}; live_route_epoch={request.LiveRouteEpoch}; authority_reason={FormatProtocolLogValue(request.AuthorityReason ?? "(none)")}");

    private bool TryResolveReceiveRecoveryObservationLocked(
        FileTransferReceiveRecoveryRequest request,
        bool allowObservationCapture,
        out FileTransferReceiveRecoveryRequest resolved,
        out string rejectionReason)
    {
        resolved = request;
        if (request.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { } outbound &&
            string.Equals(outbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(outbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            var observation = request.Observation;
            if (request.HealthEvaluationRequest is { } healthEvaluation)
            {
                if (!TryCreateSessionLivenessReceiveRecoveryObservationLocked(
                        outbound,
                        healthEvaluation,
                        out observation,
                        out rejectionReason))
                {
                    return false;
                }
            }

            if (observation is null && allowObservationCapture)
            {
                observation = CreateReceiveRecoveryObservationSnapshotLocked(
                    outbound,
                    FileTransferDirection.Outbound,
                    "core_receive_recovery",
                    request.Reason);
            }

            if (observation is null)
            {
                rejectionReason = "receive_recovery_observation_missing";
                return false;
            }

            if (!IsReceiveRecoveryObservationCurrentLocked(
                    outbound,
                    observation,
                    out rejectionReason))
            {
                return false;
            }

            resolved = ApplyReceiveRecoveryObservation(request, observation);
            rejectionReason = "accepted";
            return true;
        }

        if (request.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { } inbound &&
            string.Equals(inbound.TransferId, request.TransferId, StringComparison.Ordinal) &&
            string.Equals(inbound.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            var observation = request.Observation;
            if (request.HealthEvaluationRequest is { } healthEvaluation)
            {
                if (!TryCreateSessionLivenessReceiveRecoveryObservationLocked(
                        inbound,
                        healthEvaluation,
                        out observation,
                        out rejectionReason))
                {
                    return false;
                }
            }

            if (observation is null && allowObservationCapture)
            {
                observation = CreateReceiveRecoveryObservationSnapshotLocked(
                    inbound,
                    FileTransferDirection.Inbound,
                    "core_receive_recovery",
                    request.Reason);
            }

            if (observation is null)
            {
                rejectionReason = "receive_recovery_observation_missing";
                return false;
            }

            if (!IsReceiveRecoveryObservationCurrentLocked(
                    inbound,
                    observation,
                    out rejectionReason))
            {
                return false;
            }

            resolved = ApplyReceiveRecoveryObservation(request, observation);
            rejectionReason = "accepted";
            return true;
        }

        rejectionReason = "current_transfer_missing";
        return false;
    }

    private static bool
        TryCreateSessionLivenessReceiveRecoveryObservationLocked(
            OutboundTransferContext context,
            FileTransferBridgeRecoveryEvaluationRequest evaluation,
            out FileTransferReceiveRecoveryObservationSnapshot? observation,
            out string rejectionReason)
    {
        observation = null;
        context.LastReceiveRecoveryHealthEvaluationGeneration =
            Math.Max(
                context.LastReceiveRecoveryHealthEvaluationGeneration,
                GetReceiveRecoveryHealthEvaluationGeneration(
                    context.CoordinatorPersistentState));
        if (!TryAdvanceSessionLivenessReceiveRecoveryHealthEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                evaluation,
                context.LastSessionLivenessRecoveryEvaluationGeneration,
                context.LastReceiveRecoveryHealthEvaluationGeneration,
                out var sourceGeneration,
                out var evidence,
                out rejectionReason))
        {
            return false;
        }

        context.LastSessionLivenessRecoveryEvaluationGeneration =
            sourceGeneration;
        context.LastReceiveRecoveryHealthEvaluationGeneration =
            evidence.EvidenceGeneration;
        observation = CreateReceiveRecoveryObservationSnapshotLocked(
            context,
            FileTransferDirection.Outbound,
            evaluation.ObservationKind,
            evaluation.Reason,
            $"{evaluation.ObservationKind}:{evidence.EvidenceGeneration}",
            evaluation.ObservedUtcMs,
            evidence);
        return true;
    }

    private static bool
        TryCreateSessionLivenessReceiveRecoveryObservationLocked(
            InboundTransferContext context,
            FileTransferBridgeRecoveryEvaluationRequest evaluation,
            out FileTransferReceiveRecoveryObservationSnapshot? observation,
            out string rejectionReason)
    {
        observation = null;
        context.LastReceiveRecoveryHealthEvaluationGeneration =
            Math.Max(
                context.LastReceiveRecoveryHealthEvaluationGeneration,
                GetReceiveRecoveryHealthEvaluationGeneration(
                    context.CoordinatorPersistentState));
        if (!TryAdvanceSessionLivenessReceiveRecoveryHealthEvidence(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                evaluation,
                context.LastSessionLivenessRecoveryEvaluationGeneration,
                context.LastReceiveRecoveryHealthEvaluationGeneration,
                out var sourceGeneration,
                out var evidence,
                out rejectionReason))
        {
            return false;
        }

        context.LastSessionLivenessRecoveryEvaluationGeneration =
            sourceGeneration;
        context.LastReceiveRecoveryHealthEvaluationGeneration =
            evidence.EvidenceGeneration;
        observation = CreateReceiveRecoveryObservationSnapshotLocked(
            context,
            FileTransferDirection.Inbound,
            evaluation.ObservationKind,
            evaluation.Reason,
            $"{evaluation.ObservationKind}:{evidence.EvidenceGeneration}",
            evaluation.ObservedUtcMs,
            evidence);
        return true;
    }

    private static bool TryAdvanceSessionLivenessReceiveRecoveryHealthEvidence(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferBridgeRecoveryEvaluationRequest evaluation,
        long lastSourceGeneration,
        long lastHealthEvaluationGeneration,
        out long sourceGeneration,
        out FileTransferReceiveRecoveryHealthEvaluationEvidence evidence,
        out string rejectionReason)
    {
        sourceGeneration = lastSourceGeneration;
        evidence = default;
        if (evaluation.IsDefault ||
            evaluation.EvaluationKind !=
                FileTransferBridgeRecoveryEvaluationKind.SessionLivenessTimeout ||
            !string.Equals(
                evaluation.SessionId,
                sessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                evaluation.TransferId,
                transferId,
                StringComparison.Ordinal) ||
            evaluation.Direction != direction)
        {
            rejectionReason =
                "session_liveness_receive_recovery_evidence_invalid";
            return false;
        }

        if (evaluation.ObservationGeneration <= lastSourceGeneration)
        {
            rejectionReason =
                "session_liveness_receive_recovery_evidence_duplicate";
            return false;
        }

        if (lastHealthEvaluationGeneration == long.MaxValue)
        {
            rejectionReason =
                "session_liveness_receive_recovery_health_generation_exhausted";
            return false;
        }

        sourceGeneration = evaluation.ObservationGeneration;
        var healthGeneration = ++lastHealthEvaluationGeneration;
        evidence = CreateReceiveRecoveryHealthEvaluationEvidence(
            evaluation,
            healthGeneration);
        rejectionReason = "accepted";
        return true;
    }

    private static FileTransferReceiveRecoveryRequest ApplyReceiveRecoveryObservation(
        FileTransferReceiveRecoveryRequest request,
        FileTransferReceiveRecoveryObservationSnapshot observation)
        => request with
        {
            Observation = observation,
            RouteToken = observation.RouteToken,
            ProtocolVersion = observation.ProtocolVersion,
            LiveRouteEpoch = observation.LiveRouteEpoch,
            TransferLegGeneration = observation.TransferLegGeneration,
            TransportEpoch = observation.TransportEpoch,
            BridgeRecoveryGeneration = observation.BridgeRecoveryGeneration,
            CheckpointRequestId = observation.CheckpointRequestId,
        };

    private static bool IsReceiveRecoveryObservationCurrentLocked(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryObservationSnapshot observation,
        out string rejectionReason)
        => IsReceiveRecoveryObservationCurrentLocked(
            observation,
            FileTransferDirection.Outbound,
            context.SessionId,
            context.TransferId,
            context.IsTerminal,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch,
            context.CurrentRuntimeOwner,
            context.TerminalGeneration,
            context.CurrentTransferLeg,
            context.CurrentRoutePublicationToken,
            context.RuntimeUnlockRouteCommitToken,
            context.DataSession,
            context.DataSessionGeneration,
            out rejectionReason);

    private static bool IsReceiveRecoveryObservationCurrentLocked(
        InboundTransferContext context,
        FileTransferReceiveRecoveryObservationSnapshot observation,
        out string rejectionReason)
        => IsReceiveRecoveryObservationCurrentLocked(
            observation,
            FileTransferDirection.Inbound,
            context.SessionId,
            context.TransferId,
            context.IsTerminal,
            context.RouteSelection,
            context.CurrentLiveRouteEpoch,
            context.CurrentRuntimeOwner,
            context.TerminalGeneration,
            context.CurrentTransferLeg,
            context.CurrentRoutePublicationToken,
            context.RuntimeUnlockRouteCommitToken,
            context.DataSession,
            context.DataSessionGeneration,
            out rejectionReason);

    private static bool IsReceiveRecoveryObservationCurrentLocked(
        FileTransferReceiveRecoveryObservationSnapshot observation,
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        bool isTerminal,
        FileTransferRouteSelection routeSelection,
        LiveRouteEpoch? liveRouteEpoch,
        RuntimeOwnerToken? runtimeOwner,
        int terminalGeneration,
        FileTransferLeg? leg,
        RoutePublicationToken? publication,
        RouteCommitToken? routeCommit,
        IFileTransferDataSession? dataSession,
        long dataSessionGeneration,
        out string rejectionReason)
    {
        if (observation.ObservationGeneration <= 0 ||
            string.IsNullOrWhiteSpace(observation.Source) ||
            string.IsNullOrWhiteSpace(observation.Reason) ||
            string.IsNullOrWhiteSpace(observation.DataSessionIdentity))
        {
            rejectionReason = "receive_recovery_observation_invalid";
            return false;
        }

        if (isTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (!string.Equals(observation.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(observation.TransferId, transferId, StringComparison.Ordinal) ||
            observation.Direction != direction)
        {
            rejectionReason = "receive_recovery_observation_identity_mismatch";
            return false;
        }

        if (runtimeOwner is not { } owner ||
            !string.Equals(observation.RouteToken, routeSelection.TelemetryToken, StringComparison.Ordinal) ||
            observation.ProtocolVersion != routeSelection.ProtocolVersion ||
            observation.LiveRouteEpoch != (liveRouteEpoch?.EpochId ?? 0) ||
            observation.RuntimeGeneration != owner.RuntimeGeneration ||
            observation.TerminalGeneration != terminalGeneration ||
            observation.TransferLegGeneration != (leg?.Generation ?? 0) ||
            observation.TransportEpoch != (leg?.TransportEpochId ?? 0) ||
            observation.BridgeRecoveryGeneration != (leg?.BridgeRecoveryGeneration ?? 0))
        {
            rejectionReason = "receive_recovery_observation_runtime_scope_mismatch";
            return false;
        }

        if (observation.RoutePublicationGeneration !=
                (publication?.PublicationGeneration ?? 0) ||
            observation.RouteCommitGeneration !=
                routeCommit?.CommitGeneration ||
            !string.Equals(
                observation.CheckpointRequestId,
                leg?.CheckpointRequestId,
                StringComparison.Ordinal))
        {
            rejectionReason = "receive_recovery_observation_publication_scope_mismatch";
            return false;
        }

        if (observation.DataSessionGeneration != dataSessionGeneration ||
            !string.Equals(
                observation.DataSessionIdentity,
                CreateReceiveRecoveryDataSessionIdentity(
                    transferId,
                    dataSession,
                    dataSessionGeneration),
                StringComparison.Ordinal))
        {
            rejectionReason = "receive_recovery_observation_data_session_mismatch";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static string ResolvePostTunaFallbackReceiveRecoveryAuthorityReason(
        FileTransferReceiveRecoveryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthorityReason) &&
            request.AuthorityReason.Trim().StartsWith("post_tuna_fallback", StringComparison.OrdinalIgnoreCase))
        {
            return request.AuthorityReason.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Reason) &&
            request.Reason.Trim().StartsWith("post_tuna_fallback", StringComparison.OrdinalIgnoreCase))
        {
            return request.Reason.Trim();
        }

        if (string.Equals(
                request.Reason,
                "session_liveness_timeout_pending",
                StringComparison.OrdinalIgnoreCase))
        {
            return "post_tuna_fallback_session_liveness_timeout_pending";
        }

        return "post_tuna_fallback_receive_recovery";
    }

    private static TimeSpan ResolveV6PeerLivenessTimeout(V6TransportEpoch? epoch)
    {
        var timeout = CurrentV6PeerLivenessTimeout;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return timeout;
        }

        var epochTimeout = ResolveV6TransportEpochProofTimeout() + CurrentV6HeartbeatInterval * 6;
        return epochTimeout > timeout ? epochTimeout : timeout;
    }

    private static TimeSpan ResolveV6FallbackRecoveryLivenessTimeout()
    {
        var timeout = CurrentV6PeerLivenessTimeout;
        var recoveryTimeout = ResolveV6TransportEpochProofTimeout() + CurrentV6HeartbeatInterval * 6;
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static TimeSpan ResolveV6RegularNknPeerLivenessRepairGrace(TimeSpan peerLivenessTimeout)
    {
        if (peerLivenessTimeout <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var ticks = peerLivenessTimeout.Ticks;
        var graceTicks = ticks > TimeSpan.MaxValue.Ticks / V6RegularNknPeerLivenessRepairGraceMultiplier
            ? TimeSpan.MaxValue.Ticks
            : ticks * V6RegularNknPeerLivenessRepairGraceMultiplier;
        return TimeSpan.FromTicks(graceTicks);
    }

    private static DateTimeOffset? ResolveV6PeerLivenessDeadlineBaseUtc(
        DateTimeOffset? lastPeerLivenessUtc,
        V6TransportEpoch? epoch,
        DateTimeOffset? epochLivenessDeferralUtc)
    {
        var baseUtc = lastPeerLivenessUtc;
        if (IsV6TransportEpochUnresolved(epoch) &&
            epoch is not null &&
            (baseUtc is null ||
             epoch.StartedUtc > baseUtc.Value))
        {
            baseUtc = epoch.StartedUtc;
        }

        if (epochLivenessDeferralUtc is not null &&
            (baseUtc is null ||
             epochLivenessDeferralUtc.Value > baseUtc.Value))
        {
            baseUtc = epochLivenessDeferralUtc.Value;
        }

        return baseUtc;
    }

    private static TimeSpan ResolveOutboundV6PeerLivenessTimeout(OutboundTransferContext context)
    {
        var timeout = ResolveV6PeerLivenessTimeout(context.V6TransportEpoch);
        if (!IsOutboundV6FallbackRecoveryLivenessExtensionActive(context))
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static TimeSpan ResolveInboundV6PeerLivenessTimeout(InboundTransferContext context)
    {
        var timeout = ResolveV6PeerLivenessTimeout(context.V6TransportEpoch);
        if (!IsInboundV6FallbackRecoveryLivenessExtensionActive(context))
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static bool IsOutboundV6FallbackRecoveryLivenessExtensionActive(OutboundTransferContext context)
        => context.V6UseRegularNknRedundantData ||
           context.PullPostTunaRecoveryActive ||
           context.PullTransportPaused ||
           context.PullTransportResumeRequestPending;

    private static bool IsInboundV6FallbackRecoveryLivenessExtensionActive(InboundTransferContext context)
        => context.PullPostTunaRecoveryActive ||
           context.PullTransportPaused ||
           context.PullTransportResumeRequestPending;

    internal static TimeSpan ResolveV6PeerLivenessTimeoutForTests(bool unresolvedEpoch, bool fallbackRecoveryActive)
    {
        var timeout = unresolvedEpoch
            ? ResolveV6PeerLivenessTimeout(new V6TransportEpoch
            {
                EpochId = 1,
                Kind = FileTransferTransportHandoffKind.RegularNknRecovery,
                SourceTransport = FileTransferTransportKind.Tuna,
                TargetTransport = FileTransferTransportKind.RegularNkn,
                Reason = "test",
                StartingCommittedChunkIndex = 0,
                StartingHighestObservedChunkIndex = -1,
                State = V6TransportEpochState.TargetProofPending,
                StartedUtc = DateTimeOffset.UtcNow,
                LastStateChangeUtc = DateTimeOffset.UtcNow,
            })
            : CurrentV6PeerLivenessTimeout;

        if (!fallbackRecoveryActive)
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private void StartOutboundV6HeartbeatLoop(OutboundTransferContext context, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                ShouldUseCurrentV6HeartbeatRuntimeLocked(context) &&
                !string.IsNullOrWhiteSpace(context.SessionId) &&
                !context.V6HeartbeatLoopStarted)
            {
                context.V6HeartbeatLoopStarted = true;
                context.V6LastPeerLivenessUtc ??= DateTimeOffset.UtcNow;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; interval_ms={(long)CurrentV6HeartbeatInterval.TotalMilliseconds}; timeout_ms={(long)CurrentV6PeerLivenessTimeout.TotalMilliseconds}");
        _ = Task.Run(() => RunOutboundV6HeartbeatLoopAsync(context), CancellationToken.None);
    }

    private void StartInboundV6HeartbeatLoop(InboundTransferContext context, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                ShouldUseCurrentV6HeartbeatRuntimeLocked(context) &&
                !string.IsNullOrWhiteSpace(context.SessionId) &&
                !context.V6HeartbeatLoopStarted)
            {
                context.V6HeartbeatLoopStarted = true;
                context.V6LastPeerLivenessUtc ??= DateTimeOffset.UtcNow;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_started; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; interval_ms={(long)CurrentV6HeartbeatInterval.TotalMilliseconds}; timeout_ms={(long)CurrentV6PeerLivenessTimeout.TotalMilliseconds}");
        _ = Task.Run(() => RunInboundV6HeartbeatLoopAsync(context), CancellationToken.None);
    }

    private async Task RunOutboundV6HeartbeatLoopAsync(OutboundTransferContext context)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CurrentV6HeartbeatInterval, context.LifetimeCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                FileTransferHeartbeatV6? heartbeat = null;
                var timedOut = false;
                DateTimeOffset? lastPeerLivenessUtc = null;
                DateTimeOffset? livenessDeadlineBaseUtc = null;
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    if (!ShouldUseCurrentV6HeartbeatRuntimeLocked(context))
                    {
                        StopOutboundV6HeartbeatLoopLocked(context, "route_runtime_changed");
                        return;
                    }

                    peerLivenessTimeout = ResolveOutboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    livenessDeadlineBaseUtc = ResolveV6PeerLivenessDeadlineBaseUtc(
                        lastPeerLivenessUtc,
                        context.V6TransportEpoch,
                        context.V6EpochLivenessDeferralUtc);
                    timedOut = livenessDeadlineBaseUtc is not null &&
                        now - livenessDeadlineBaseUtc.Value >= peerLivenessTimeout;
                    if (!timedOut)
                    {
                        heartbeat = new FileTransferHeartbeatV6
                        {
                            SessionId = context.SessionId,
                            TransferId = context.TransferId,
                            TransportEpoch = context.V6TransportEpoch?.EpochId ?? 0,
                            Sequence = ++context.V6HeartbeatSequence,
                            SentUnixTimeMilliseconds = now.ToUnixTimeMilliseconds(),
                        };
                    }
                }

                if (timedOut)
                {
                    var terminalized = await TerminalizeOutboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    if (terminalized)
                    {
                        return;
                    }

                    continue;
                }

                if (heartbeat is not null)
                {
                    await SendHeartbeatAsync(heartbeat, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private async Task RunInboundV6HeartbeatLoopAsync(InboundTransferContext context)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CurrentV6HeartbeatInterval, context.LifetimeCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                FileTransferHeartbeatV6? heartbeat = null;
                var timedOut = false;
                DateTimeOffset? lastPeerLivenessUtc = null;
                DateTimeOffset? livenessDeadlineBaseUtc = null;
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    if (!ShouldUseCurrentV6HeartbeatRuntimeLocked(context))
                    {
                        StopInboundV6HeartbeatLoopLocked(context, "route_runtime_changed");
                        return;
                    }

                    peerLivenessTimeout = ResolveInboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    livenessDeadlineBaseUtc = ResolveV6PeerLivenessDeadlineBaseUtc(
                        lastPeerLivenessUtc,
                        context.V6TransportEpoch,
                        context.V6EpochLivenessDeferralUtc);
                    timedOut = livenessDeadlineBaseUtc is not null &&
                        now - livenessDeadlineBaseUtc.Value >= peerLivenessTimeout;
                    if (!timedOut)
                    {
                        heartbeat = new FileTransferHeartbeatV6
                        {
                            SessionId = context.SessionId,
                            TransferId = context.TransferId,
                            TransportEpoch = context.V6TransportEpoch?.EpochId ?? 0,
                            Sequence = ++context.V6HeartbeatSequence,
                            SentUnixTimeMilliseconds = now.ToUnixTimeMilliseconds(),
                        };
                    }
                }

                if (timedOut)
                {
                    var terminalized = await TerminalizeInboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    if (terminalized)
                    {
                        return;
                    }

                    continue;
                }

                if (heartbeat is not null)
                {
                    await SendHeartbeatAsync(heartbeat, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private static bool ShouldUseCurrentV6HeartbeatRuntimeLocked(OutboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesV6FeedbackEnvelope;

    private static bool ShouldUseCurrentV6HeartbeatRuntimeLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesV6FeedbackEnvelope;

    private static void StopOutboundV6HeartbeatLoopLocked(OutboundTransferContext context, string reason)
    {
        context.V6HeartbeatLoopStarted = false;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_stopped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}");
    }

    private static void StopInboundV6HeartbeatLoopLocked(InboundTransferContext context, string reason)
    {
        context.V6HeartbeatLoopStarted = false;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_stopped; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}");
    }

    private async Task<bool> TerminalizeOutboundForPeerLivenessTimeoutAsync(OutboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
        if (TryDeferOutboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundEpochToProbe))
        {
            if (outboundEpochToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundEpochToProbe).ConfigureAwait(false);
                SignalOutboundSparseSenderPump(outboundEpochToProbe);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundToSignal))
        {
            if (outboundToSignal is not null)
            {
                SignalOutboundSparseSenderPump(outboundToSignal);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForPostTunaFallbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var postTunaFallbackOutboundToSignal))
        {
            if (postTunaFallbackOutboundToSignal is not null)
            {
                SignalOutboundSparseSenderPump(postTunaFallbackOutboundToSignal);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForRecovery(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundToProbe))
        {
            TryRequestFileTransferReceiveRecovery(AttachFallbackLegAuthority(
                context,
                new FileTransferReceiveRecoveryRequest(
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Outbound,
                    "peer_liveness_stale_receive_recovery"),
                "peer_liveness_stale_receive_recovery"));

            if (outboundToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToProbe).ConfigureAwait(false);
                SignalOutboundSparseSenderPump(outboundToProbe);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var sparseRuntimeOutboundToSignal))
        {
            var primaryRegularNknBulkV6 = false;
            lock (gate)
            {
                primaryRegularNknBulkV6 =
                    ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal &&
                    IsPrimaryRegularNknBulkV6ContextLocked(context);
            }

            if (primaryRegularNknBulkV6)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_recovery_deferred_bridge_recovery; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=peer_liveness_stale; recovery_profile=regular_nkn_checkpoint_sync");
            }
            else
            {
                TryRequestFileTransferReceiveRecovery(AttachFallbackLegAuthority(
                    context,
                    new FileTransferReceiveRecoveryRequest(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Outbound,
                        "v6_sparse_runtime_peer_liveness_recovery"),
                    "v6_sparse_runtime_peer_liveness_recovery"));
            }

            if (sparseRuntimeOutboundToSignal is not null)
            {
                SignalOutboundSparseSenderPump(sparseRuntimeOutboundToSignal);
            }

            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}");
        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Peer disconnected.",
            notifyPeer: true,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForPostTunaFallbackRepair(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToSignal)
    {
        outboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int remoteFrontier;
        int highestAcceptedChunk;
        int inFlightSendCount;
        int pendingRepairCount;
        long peerLivenessSilenceMs;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context) ||
                !IsOutboundPostTunaRecoveryActiveLocked(context) ||
                context.UserPaused ||
                context.PeerPaused ||
                !context.PullSessionActive ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch))
            {
                return false;
            }

            var hasRecoveryWork =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0 ||
                context.PullV4SenderPumpRepairRequests.Count > 0 ||
                context.PullV4SenderPumpRepairQueue.Count > 0 ||
                context.PullPostTunaRecoveryActive;
            if (!hasRecoveryWork)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "post_tuna_fallback_peer_liveness_repair",
                    "post_tuna_fallback_peer_liveness_repair",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_peer_liveness_repair";

            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
            pendingRepairCount = context.PullV4SenderPumpRepairRequests.Count;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            outboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_post_tuna_fallback_repair; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; deferral_count={deferralCount}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}; in_flight_send_count={inFlightSendCount}; pending_repair_count={pendingRepairCount}; transport_paused={(context.PullTransportPaused ? 1 : 0)}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToSignal)
    {
        outboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int remoteFrontier;
        int highestAcceptedChunk;
        long bytesTransferred;
        long bytesAcceptedForTransport;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !ShouldUseV6RegularNknSparseRuntime(context) ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0 ||
                context.PullV4SenderPumpRepairRequests.Count > 0;
            if (!hasUnacknowledgedOutboundData)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "v6_regular_nkn_sparse_runtime",
                    "v6_regular_nkn_sparse_runtime",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
                context.SparseSenderPumpLastWakeReason = IsPrimaryRegularNknBulkV6ContextLocked(context)
                    ? "primary_regular_nkn_bulk_v6_checkpoint_liveness_recovery"
                    : "v6_sparse_runtime_peer_liveness_recovery";

            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            bytesTransferred = context.BytesTransferred;
            bytesAcceptedForTransport = context.BytesAcceptedForTransport;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            outboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; deferral_count={deferralCount}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}; bytes_transferred={bytesTransferred}; bytes_accepted_for_transport={bytesAcceptedForTransport}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToProbe)
    {
        outboundToProbe = null;
        DateTimeOffset now;
        int deferralCount;
        long transportEpoch;
        string epochState;
        string handoffKind;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(context.V6TransportEpoch))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "peer_liveness_epoch_waiting",
                    "peer_liveness_epoch_waiting",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.SparseSenderPumpLastWakeReason = "peer_liveness_epoch_waiting";
            context.StatusMessage = GetV6TransportEpochStatus(context.V6TransportEpoch!);

            transportEpoch = context.V6TransportEpoch!.EpochId;
            epochState = FormatV6TransportEpochState(context.V6TransportEpoch.State);
            handoffKind = FormatFileTransferTransportHandoffKind(context.V6TransportEpoch.Kind);
            deferralCount = context.V6EpochLivenessDeferralCount;
            outboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; handoff_kind={FormatProtocolLogValue(handoffKind)}; target_transport=regular_nkn; deferral_count={deferralCount}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToSignal)
    {
        outboundToSignal = null;
        DateTimeOffset now;
        int recentChunkSends;
        int deferralCount;
        int remoteFrontier;
        int highestAcceptedChunk;
        int inFlightSendCount;
        int priorityRequestCount;
        int normalRequestCount;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;
            if (lastPeerLivenessUtc is not null &&
                peerLivenessSilence >= repairGrace)
            {
                return false;
            }

            var activityWindow = ResolveV6FallbackRecoveryLivenessTimeout();
            recentChunkSends = context.LastChunkSentUtc.Count(pair => now - pair.Value <= activityWindow);
            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0;
            if (recentChunkSends <= 0 && !hasUnacknowledgedOutboundData)
            {
                return false;
            }

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "regular_nkn_feedback_repair",
                    "regular_nkn_feedback_repair",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.SparseSenderPumpLastWakeReason = "regular_nkn_feedback_repair";

            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
            priorityRequestCount = context.V6PriorityRequestedChunks.Count;
            normalRequestCount = context.V6NormalRequestedChunks.Count;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            outboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_regular_nkn_feedback_repair; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; recent_chunk_sends={recentChunkSends}; deferral_count={deferralCount}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}; in_flight_send_count={inFlightSendCount}; priority_request_count={priorityRequestCount}; normal_request_count={normalRequestCount}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForRecovery(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToProbe)
    {
        outboundToProbe = null;
        DateTimeOffset now;
        int recentChunkSends;
        int deferralCount;
        long transportEpoch;
        string epochState;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                context.V6PeerLivenessRecoveryDeferralCount > 0)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var activityWindow = ResolveV6FallbackRecoveryLivenessTimeout();
            recentChunkSends = context.LastChunkSentUtc.Count(pair => now - pair.Value <= activityWindow);
            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0;
            if (recentChunkSends <= 0 && !hasUnacknowledgedOutboundData)
            {
                return false;
            }

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "peer_liveness_recovery",
                    "peer_liveness_recovery",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = now;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.SparseSenderPumpLastWakeReason = "peer_liveness_recovery";
            StartOutboundV6TransportEpochLocked(
                context,
                "peer_liveness_stale_receive_recovery",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
            epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            outboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_recovery; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; recent_chunk_sends={recentChunkSends}; deferral_count={deferralCount}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_recovery_waiting_for_receiver_requests; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; reason=peer_liveness_stale_receive_recovery");
        return true;
    }

    private async Task<bool> TerminalizeInboundForPeerLivenessTimeoutAsync(InboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
        if (TryDeferInboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToProbe))
        {
            if (inboundToProbe is not null)
            {
                await AnnounceAndProbeInboundV6TransportEpochAsync(inboundToProbe).ConfigureAwait(false);
            }

            return false;
        }

        if (TryDeferInboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToSignal))
        {
            if (inboundToSignal is not null)
            {
                var reason = "v6_sparse_runtime_peer_liveness_recovery";
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, inboundToSignal) &&
                        !inboundToSignal.IsTerminal &&
                        IsPrimaryRegularNknBulkV6ContextLocked(inboundToSignal))
                    {
                        reason = V6RegularNknCheckpointSyncRecoveryMode;
                    }
                }

                await SendInboundV4StateAsync(
                        inboundToSignal,
                        reason,
                        terminalReady: false,
                        forceSend: true)
                    .ConfigureAwait(false);
            }

            return false;
        }

        if (TryDeferInboundV6PeerLivenessTimeoutForPostTunaFallbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var postTunaFallbackInboundToSignal))
        {
            if (postTunaFallbackInboundToSignal is not null)
            {
                await SendInboundV6ReceiverStateAsync(
                    postTunaFallbackInboundToSignal,
                    "post_tuna_fallback_peer_liveness_repair",
                    forceSend: true).ConfigureAwait(false);
                await SendInboundV6FrontierRequestAsync(
                    postTunaFallbackInboundToSignal,
                    "post_tuna_fallback_peer_liveness_repair",
                    forceSend: true).ConfigureAwait(false);
            }

            return false;
        }

        if (TryDeferInboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToRefresh))
        {
            if (inboundToRefresh is not null)
            {
                await SendInboundV6ReceiverStateAsync(inboundToRefresh, "regular_nkn_peer_liveness_feedback_repair", forceSend: true).ConfigureAwait(false);
                await SendInboundV6FrontierRequestAsync(inboundToRefresh, "regular_nkn_peer_liveness_feedback_repair", forceSend: true).ConfigureAwait(false);
            }

            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Peer disconnected.",
            sendError: true,
            errorMessage: "Peer disconnected.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForPostTunaFallbackRepair(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToSignal)
    {
        inboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        int sparsePendingWriteCount;
        long bytesTransferred;
        long peerLivenessSilenceMs;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context) ||
                !IsInboundPostTunaRecoveryActiveLocked(context) ||
                context.UserPaused ||
                context.PeerPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NextChunkIndex >= context.ChunkCount ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch))
            {
                return false;
            }

            var hasRecoveryWork =
                context.PullPostTunaRecoveryActive ||
                context.NextChunkIndex <= context.PullHighestReceivedChunkIndex ||
                context.ReceiverSparseChunksPendingWrite.Count > 0;
            if (!hasRecoveryWork)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "post_tuna_fallback_peer_liveness_repair",
                    "post_tuna_fallback_peer_liveness_repair",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;

            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            sparsePendingWriteCount = context.ReceiverSparseChunksPendingWrite.Count;
            bytesTransferred = context.BytesTransferred;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            inboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_post_tuna_fallback_repair; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; deferral_count={deferralCount}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; bytes_transferred={bytesTransferred}; sparse_pending_write_count={sparsePendingWriteCount}; transport_paused={(context.PullTransportPaused ? 1 : 0)}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}");
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToSignal)
    {
        inboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        long bytesTransferred;
        int sparsePendingWriteCount;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !ShouldUseV6RegularNknSparseRuntime(context) ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NextChunkIndex >= context.ChunkCount ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "v6_regular_nkn_sparse_runtime",
                    "v6_regular_nkn_sparse_runtime",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;

            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            bytesTransferred = context.BytesTransferred;
            sparsePendingWriteCount = context.ReceiverSparseChunksPendingWrite.Count;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            inboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; deferral_count={deferralCount}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; bytes_transferred={bytesTransferred}; sparse_pending_write_count={sparsePendingWriteCount}");
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToProbe)
    {
        inboundToProbe = null;
        DateTimeOffset now;
        int deferralCount;
        long transportEpoch;
        string epochState;
        string handoffKind;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(context.V6TransportEpoch))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "peer_liveness_epoch_waiting",
                    "peer_liveness_epoch_waiting",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;
            context.StatusMessage = GetV6TransportEpochStatus(context.V6TransportEpoch!);

            transportEpoch = context.V6TransportEpoch!.EpochId;
            epochState = FormatV6TransportEpochState(context.V6TransportEpoch.State);
            handoffKind = FormatFileTransferTransportHandoffKind(context.V6TransportEpoch.Kind);
            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            inboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; handoff_kind={FormatProtocolLogValue(handoffKind)}; target_transport=regular_nkn; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; deferral_count={deferralCount}");
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToRefresh)
    {
        inboundToRefresh = null;
        DateTimeOffset now;
        int deferralCount;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        long bytesTransferred;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NextChunkIndex >= context.ChunkCount ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;
            if (lastPeerLivenessUtc is not null &&
                peerLivenessSilence >= repairGrace)
            {
                return false;
            }

            if (!TryAuthorizeRecoveryLivenessLeaseLocked(
                    context,
                    "v6_peer_liveness_timeout",
                    "regular_nkn_feedback_repair",
                    "regular_nkn_feedback_repair",
                    now.Add(peerLivenessTimeout),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;

            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            bytesTransferred = context.BytesTransferred;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            inboundToRefresh = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_regular_nkn_feedback_repair; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; bytes_transferred={bytesTransferred}; deferral_count={deferralCount}");
        return true;
    }

    private static bool ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(V6TransportEpoch? epoch)
        => IsV6TransportEpochUnresolved(epoch) &&
           epoch is { TargetTransport: FileTransferTransportKind.RegularNkn };

    private static bool IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(OutboundTransferContext context)
        => IsOutboundV6RegularNknPrimaryPathLocked(context) &&
           !context.PullPostTunaRecoveryActive &&
           context.LastRecoveredV6TransportTargetTransport != FileTransferTransportKind.Tuna &&
           context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.TunaToNormalFallback;

    private static bool IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(InboundTransferContext context)
        => context.V6TransportEpoch is not { TargetTransport: FileTransferTransportKind.Tuna } &&
           !context.PullPostTunaRecoveryActive &&
           context.LastRecoveredV6TransportTargetTransport != FileTransferTransportKind.Tuna &&
           context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.TunaToNormalFallback;

    private void TouchOutboundV6PeerLiveness(
        OutboundTransferContext context,
        string reason,
        OutboundSparsePumpInstall? receiveInstall = null,
        RuntimeOwnerToken? receiveRuntimeOwner = null,
        IFileTransferDataSession? receiveDataSession = null,
        long receiveDataSessionGeneration = 0)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                (receiveDataSession is not null &&
                 !IsOutboundSparseReceiveResultCurrentLocked(
                     context,
                     receiveInstall,
                     receiveRuntimeOwner,
                     receiveDataSession,
                     receiveDataSessionGeneration,
                     out _)))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            context.V6LastPeerLivenessUtc = now;
            context.PullV4LastPeerFrameReceivedUtc = now;
            context.V6EpochLivenessDeferralCount = 0;
            context.V6EpochLivenessDeferralUtc = null;
            context.V6PeerLivenessRecoveryDeferralCount = 0;
            context.V6PeerLivenessRecoveryDeferredUtc = null;
        }
    }

    private void TouchOutboundV6PeerLivenessIfAuthoritative(OutboundTransferContext context, FileTransferReceivedDataFrame received, string reason)
    {
        if (IsAuthoritativeV6PeerLivenessFrame(received))
        {
            TouchOutboundV6PeerLiveness(context, reason);
        }
    }

    private void TouchInboundV6PeerLiveness(InboundTransferContext context, string reason)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            context.V6LastPeerLivenessUtc = now;
            context.PullV4LastPeerFrameReceivedUtc = now;
            context.V6EpochLivenessDeferralCount = 0;
            context.V6EpochLivenessDeferralUtc = null;
        }
    }

    private void TouchInboundV6PeerLivenessIfAuthoritative(InboundTransferContext context, FileTransferReceivedDataFrame received, string reason)
    {
        if (IsAuthoritativeV6PeerLivenessFrame(received))
        {
            TouchInboundV6PeerLiveness(context, reason);
        }
    }

    private static bool IsAuthoritativeV6PeerLivenessFrame(FileTransferReceivedDataFrame received)
        // Source/session/replay checks already ran before this point. Transport origin is still strict for
        // epoch proof, but peer liveness should survive redundant feedback being stamped as Tuna/unknown.
        => FileTransferProtocol.IsV6DataFrame(received.Frame) ||
           received.Frame is FileTransferStateFrameV4;
}
