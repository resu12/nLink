using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const string RuntimeUnlockRetryMissingSuccessorLimitExhaustedReason =
        "retry_dispatch_missing_successor_limit_exhausted";

    private void OnRuntimeUnlockPostFallbackPreCommitProbeFailureObserved(
        object? sender,
        RuntimeUnlockPostFallbackPreCommitProbeFailureObservedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        var observation = args.Observation;
        RuntimeUnlockPostFallbackPreCommitProbeFailureDecision decision;
        if (observation is null || observation.IsDefault)
        {
            decision = new(
                RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected,
                "post_fallback_precommit_probe_failure_observation_invalid");
        }
        else if (observation.Role ==
                 RuntimeUnlockPostFallbackPreCommitProbeFailureRole.Initiator)
        {
            decision = ObserveOutboundRuntimeUnlockPostFallbackProbeFailure(
                bridge,
                observation);
        }
        else
        {
            decision = ObserveInboundRuntimeUnlockPostFallbackProbeFailure(
                bridge,
                observation);
        }

        args.Decision = decision;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_post_fallback_precommit_probe_sidecar_failure_decided; session_id={FormatProtocolLogValue(observation?.SessionId ?? "(none)")}; transfer_id={FormatProtocolLogValue(observation?.TransferId ?? "(none)")}; direction={observation?.Direction.ToString().ToLowerInvariant() ?? "unknown"}; role={observation?.Role.ToString().ToLowerInvariant() ?? "unknown"}; transaction_generation={observation?.Probe.TransactionGeneration ?? 0}; offer_generation={observation?.Probe.OfferGeneration ?? 0}; tuna_path_lease_generation={observation?.Probe.TunaPathLeaseGeneration ?? 0}; launch_generation={observation?.Probe.LaunchGeneration ?? 0}; probe_id={FormatProtocolLogValue(observation?.Probe.PathProbeId ?? "(none)")}; retry_authority_id={FormatProtocolLogValue(observation?.InstallReceipt?.Authority.AuthorityId.ToString() ?? "(none)")}; install_generation={observation?.InstallReceipt?.InstallGeneration ?? 0}; paired_release_authority_id={FormatProtocolLogValue(observation?.BindingReceipt?.PairedRelease.ReleaseAuthorityId.ToString() ?? "(none)")}; pairing_generation={observation?.BindingReceipt?.PairedRelease.PairingGeneration ?? 0}; route={FormatProtocolLogValue(observation?.SourceScope.PublicationAuthority.RouteToken ?? "(none)")}; protocol_version={observation?.SourceScope.PublicationAuthority.ProtocolVersion ?? 0}; live_route_epoch={observation?.SourceScope.PublicationAuthority.LiveRouteEpoch ?? 0}; runtime_generation={observation?.SourceScope.PublicationAuthority.RuntimeGeneration ?? 0}; terminal_generation={observation?.SourceScope.PublicationAuthority.TerminalGeneration ?? 0}; leg_generation={observation?.SourceScope.PublicationAuthority.LegGeneration ?? 0}; route_publication_generation={observation?.SourceScope.PublicationAuthority.RoutePublicationGeneration ?? 0}; route_commit_generation={observation?.SourceScope.PublicationAuthority.RouteCommitGeneration ?? 0}; transport_epoch={observation?.SourceScope.TransportEpoch ?? 0}; bridge_recovery_generation={observation?.SourceScope.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(observation?.SourceScope.CheckpointRequestId ?? "(none)")}; data_session_identity={FormatProtocolLogValue(observation?.SourceScope.DataSessionIdentity ?? "(none)")}; data_session_generation={observation?.SourceScope.DataSessionGeneration ?? 0}; decision_status={FormatProtocolLogValue(decision.Status.ToString())}; decision_reason={FormatProtocolLogValue(decision.Reason)}; authority_effect=none");
    }

    private RuntimeUnlockPostFallbackPreCommitProbeFailureDecision
        ObserveOutboundRuntimeUnlockPostFallbackProbeFailure(
            IRuntimeUnlockRetryDispatchAuthorityBridge bridge,
            RuntimeUnlockPostFallbackPreCommitProbeFailureObservation observation)
    {
        RuntimeUnlockRetryDispatchInstallReceipt installReceipt;
        lock (gate)
        {
            if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge current ||
                !ReferenceEquals(current, bridge) ||
                observation.InstallReceipt is not { IsDefault: false } installed ||
                outboundTransfer is not { IsTerminal: false } outbound ||
                !outbound.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                !string.Equals(
                    outbound.SessionId,
                    observation.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    outbound.TransferId,
                    observation.TransferId,
                    StringComparison.Ordinal) ||
                outbound.CurrentRoutePublicationToken is not { } publication ||
                !FileTransferCoordinator.MatchesRoutePublicationAuthority(
                    installed.Request.SourcePublicationAuthority,
                    publication) ||
                outbound.FileTransferRouteTransitionExecution is not
                {
                    Authority.ProofBoundary:
                        FileTransferTunaActivationTransitionProofBoundary
                            activation,
                } execution ||
                activation.AcceptedLaunchBoundary.TransactionGeneration !=
                    observation.Probe.TransactionGeneration ||
                activation.AcceptedLaunchBoundary.OfferGeneration !=
                    observation.Probe.OfferGeneration ||
                activation.AcceptedLaunchBoundary.TunaPathLeaseGeneration !=
                    observation.Probe.TunaPathLeaseGeneration ||
                activation.LaunchGeneration !=
                    observation.Probe.LaunchGeneration)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Stale,
                    "post_fallback_precommit_probe_failure_source_not_current");
            }

            if (lastOutboundRuntimeUnlockPostFallbackProbeFailureObservation ==
                observation)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Idempotent,
                    "post_fallback_precommit_probe_failure_idempotent");
            }

            var tail = FileTransferCoordinator.GetFallbackTailYieldSubstate(
                CreateCoordinatorStateLocked(outbound));
            if (tail.CurrentOperation is not
                {
                    PairingState:
                        FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                    PairedRelease: { } release,
                } operation ||
                tail.LastConsumingInstallReceipt != installed ||
                tail.LastConsumedReservation is not { } reservation ||
                reservation.Release != release ||
                reservation.RetryAuthority != installed.Authority ||
                installed.Request.FallbackPairedTailYieldEvidence is not
                    { } pairedEvidence ||
                pairedEvidence.LocalDecision != operation.Decision ||
                operation.Scope.RoutePublication is not { } pairedPublication ||
                !FileTransferCoordinator.MatchesRoutePublicationAuthority(
                    observation.SourceScope.PublicationAuthority,
                    pairedPublication) ||
                observation.SourceScope.TransportEpoch !=
                    operation.Scope.TransportEpoch ||
                observation.SourceScope.BridgeRecoveryGeneration !=
                    operation.Scope.BridgeRecoveryGeneration ||
                !string.Equals(
                    observation.SourceScope.CheckpointRequestId,
                    operation.Scope.CheckpointRequestIdSnapshot,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    observation.SourceScope.DataSessionIdentity,
                    operation.Scope.DataSessionIdentity,
                    StringComparison.Ordinal) ||
                observation.SourceScope.DataSessionGeneration !=
                    operation.Scope.DataSessionGeneration)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected,
                    "post_fallback_precommit_probe_failure_pair_not_current");
            }

            installReceipt = installed;
        }

        LogRuntimeUnlockPreCommitProbeFailedForLaunch(
            FileTransferDirection.Outbound,
            observation.TransferId,
            observation.SessionId,
            observation.Probe.TransactionGeneration,
            observation.Probe.OfferGeneration,
            observation.Probe.TunaPathLeaseGeneration,
            observation.Probe.PathProbeId!,
            "runtime_unlock_tuna_probe_sidecar_closed",
            observation.Probe.LaunchGeneration);
        MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
            FileTransferDirection.Outbound,
            observation.SessionId,
            observation.TransferId,
            "runtime_unlock_tuna_probe_sidecar_closed");

        lock (gate)
        {
            if (outboundTransfer is not { IsTerminal: false } outbound ||
                !string.Equals(
                    outbound.SessionId,
                    observation.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    outbound.TransferId,
                    observation.TransferId,
                    StringComparison.Ordinal))
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Stale,
                    "post_fallback_precommit_probe_failure_source_replaced");
            }

            var tail = FileTransferCoordinator.GetFallbackTailYieldSubstate(
                CreateCoordinatorStateLocked(outbound));
            var predecessorId = installReceipt.Request
                .FallbackPairedTailYieldEvidence?.LocalDecision.AuthorityId;
            if (predecessorId is not { IsDefault: false } ||
                tail.CurrentOperation?.Decision.PredecessorAuthorityId !=
                    predecessorId)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected,
                    "post_fallback_precommit_probe_failure_rollback_rejected");
            }

            lastOutboundRuntimeUnlockPostFallbackProbeFailureObservation =
                observation;
            return new(
                RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Accepted,
                "post_fallback_precommit_probe_failure_rollback_accepted");
        }
    }

    private RuntimeUnlockPostFallbackPreCommitProbeFailureDecision
        ObserveInboundRuntimeUnlockPostFallbackProbeFailure(
            IRuntimeUnlockRetryDispatchAuthorityBridge bridge,
            RuntimeUnlockPostFallbackPreCommitProbeFailureObservation observation)
    {
        lock (gate)
        {
            if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge current ||
                !ReferenceEquals(current, bridge) ||
                observation.BindingReceipt is not { IsDefault: false } binding ||
                runtimeUnlockPostFallbackPairedRetryOfferBindingReceipt != binding ||
                inboundTransfer is not { IsTerminal: false } inbound ||
                !inbound.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                !string.Equals(
                    inbound.SessionId,
                    observation.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    inbound.TransferId,
                    observation.TransferId,
                    StringComparison.Ordinal) ||
                inbound.CurrentRoutePublicationToken is not { } publication ||
                !FileTransferCoordinator.MatchesRoutePublicationAuthority(
                    binding.Request.PublicationAuthority,
                    publication) ||
                !TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                    CreateCoordinatorStateLocked(inbound),
                    out var acceptanceScope,
                    out _) ||
                acceptanceScope != binding.AcceptanceScope)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Stale,
                    "post_fallback_precommit_probe_failure_source_not_current");
            }

            if (lastInboundRuntimeUnlockPostFallbackProbeFailureObservation ==
                observation)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Idempotent,
                    "post_fallback_precommit_probe_failure_idempotent");
            }

            var state = CreateCoordinatorStateLocked(inbound);
            var operation = FileTransferCoordinator
                .GetFallbackTailYieldSubstate(state)
                .CurrentOperation;
            if (operation is not
                {
                    PairingState: FileTransferFallbackTailYieldPairingState.Paired,
                    PairedRelease: { } release,
                } pairedOperation ||
                release != binding.PairedRelease ||
                FileTransferCoordinator.GetEffectiveFallbackTailYieldScope(
                    pairedOperation) != binding.PairedScope)
            {
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected,
                    "post_fallback_precommit_probe_failure_pair_not_current");
            }

            var superseded = FileTransferCoordinator
                .SupersedeFallbackTailPairedYieldAfterRejectedProbe(
                    state,
                    FileTransferFallbackTailRetryCycleSupersessionRole.Responder,
                    pairedOperation.Decision,
                    release,
                    expectedInstallReceipt: null,
                    "sidecar_failed_precommit_probe_rollback");
            if (!superseded.Accepted)
            {
                LogFallbackTailRetryCycleSupersessionDecision(
                    FileTransferDirection.Inbound,
                    inbound.SessionId,
                    inbound.TransferId,
                    pairedOperation,
                    installReceipt: null,
                    transitionAuthority: null,
                    superseded.Status.ToString(),
                    superseded.Reason,
                    "runtime_unlock_tuna_probe_sidecar_closed",
                    observation.Probe.TransactionGeneration,
                    observation.Probe.OfferGeneration,
                    observation.Probe.TunaPathLeaseGeneration,
                    observation.Probe.PathProbeId);
                return new(
                    RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Rejected,
                    superseded.Reason);
            }

            ApplyFallbackTailYieldTransitionToContextLocked(inbound, superseded);
            LogFallbackTailRetryCycleSupersessionDecision(
                FileTransferDirection.Inbound,
                inbound.SessionId,
                inbound.TransferId,
                pairedOperation,
                installReceipt: null,
                transitionAuthority: null,
                superseded.Status.ToString(),
                superseded.Reason,
                "runtime_unlock_tuna_probe_sidecar_closed",
                observation.Probe.TransactionGeneration,
                observation.Probe.OfferGeneration,
                observation.Probe.TunaPathLeaseGeneration,
                observation.Probe.PathProbeId);
            runtimeUnlockPostFallbackPairedRetryOfferBindingReceipt = null;
            ResolveInboundFallbackTailSurvivalLocked(
                inbound,
                "sidecar_failed_precommit_probe_rollback");
            lastInboundRuntimeUnlockPostFallbackProbeFailureObservation =
                observation;
            return new(
                RuntimeUnlockPostFallbackPreCommitProbeFailureStatus.Accepted,
                "post_fallback_precommit_probe_failure_rollback_accepted");
        }
    }

    private void OnRuntimeUnlockRetryDispatchSettlementReported(
        object? sender,
        RuntimeUnlockRetryDispatchSettlementReportedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRetryDispatchSettlementDecision decision;
        lock (gate)
        {
            var report = args.Report;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (bridgeIsCurrentTransport &&
                outboundTransfer is { } outbound &&
                report.Authority.Direction == FileTransferDirection.Outbound &&
                string.Equals(
                    report.Authority.SessionId,
                    outbound.SessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    report.Authority.TransferId,
                    outbound.TransferId,
                    StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRetryDispatchSettlementLocked(outbound, report);
            }
            else if (bridgeIsCurrentTransport &&
                     inboundTransfer is { } inbound &&
                     report.Authority.Direction == FileTransferDirection.Inbound &&
                     string.Equals(
                         report.Authority.SessionId,
                         inbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         report.Authority.TransferId,
                         inbound.TransferId,
                         StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRetryDispatchSettlementLocked(inbound, report);
            }
            else
            {
                decision = new RuntimeUnlockRetryDispatchSettlementDecision(
                    RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                    "retry_dispatch_settlement_transfer_missing",
                    report);
            }
        }

        // Infra owns its retry lock while applying this disposition. Never call it
        // while the Core transfer lock is held.
        bridge.ObserveRuntimeUnlockRetryDispatchSettlementDecision(decision);
    }

    private void OnRuntimeUnlockRetryDispatchDeadlineEvaluationRequested(
        object? sender,
        RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRetryLifecycleDeadlineDecision deadlineDecision;
        lock (gate)
        {
            var evaluation = args.Evaluation;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (!bridgeIsCurrentTransport || evaluation.IsDefault)
            {
                deadlineDecision = new RuntimeUnlockRetryLifecycleDeadlineDecision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    bridgeIsCurrentTransport
                        ? "retry_dispatch_deadline_evaluation_missing"
                        : "retry_dispatch_deadline_bridge_replaced");
            }
            else if (evaluation.Authority.Direction == FileTransferDirection.Outbound &&
                     outboundTransfer is { } outbound &&
                     !outbound.IsTerminal &&
                     string.Equals(
                         evaluation.Authority.SessionId,
                         outbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         evaluation.Authority.TransferId,
                         outbound.TransferId,
                         StringComparison.Ordinal))
            {
                deadlineDecision =
                    EvaluateRuntimeUnlockRetryDispatchDeadlineLocked(
                        outbound,
                        evaluation);
            }
            else if (evaluation.Authority.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     !inbound.IsTerminal &&
                     string.Equals(
                         evaluation.Authority.SessionId,
                         inbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         evaluation.Authority.TransferId,
                         inbound.TransferId,
                         StringComparison.Ordinal))
            {
                deadlineDecision =
                    EvaluateRuntimeUnlockRetryDispatchDeadlineLocked(
                        inbound,
                        evaluation);
            }
            else
            {
                deadlineDecision = new RuntimeUnlockRetryLifecycleDeadlineDecision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_deadline_transfer_context_missing");
            }
        }

        LogRuntimeUnlockRetryLifecycleDeadlineDecision(
            args.Evaluation,
            deadlineDecision);

        if (deadlineDecision.Settlement is not { IsDefault: false } settlement ||
            deadlineDecision.State is null)
        {
            return;
        }

        var settlementStatus = deadlineDecision.Status switch
        {
            RuntimeUnlockRetryLifecycleTransitionStatus.Accepted =>
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent =>
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
            _ => RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
        };
        bridge.ObserveRuntimeUnlockRetryDispatchSettlementDecision(
            new RuntimeUnlockRetryDispatchSettlementDecision(
                settlementStatus,
                deadlineDecision.Reason,
                settlement)
            {
                State = deadlineDecision.State,
            });
    }

    private void OnRuntimeUnlockRetryDispatchExecutionObserved(
        object? sender,
        RuntimeUnlockRetryDispatchExecutionObservedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRetryLifecycleTransitionDecision decision;
        lock (gate)
        {
            var observation = args.Observation;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (!bridgeIsCurrentTransport || observation.IsDefault)
            {
                decision = new RuntimeUnlockRetryLifecycleTransitionDecision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    bridgeIsCurrentTransport
                        ? "retry_dispatch_execution_observation_missing"
                        : "retry_dispatch_execution_bridge_replaced");
            }
            else if (observation.Authority.Direction == FileTransferDirection.Outbound &&
                     outboundTransfer is { } outbound &&
                     !outbound.IsTerminal &&
                     string.Equals(
                         observation.Authority.SessionId,
                         outbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         observation.Authority.TransferId,
                         outbound.TransferId,
                         StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRetryDispatchExecutionLocked(
                    outbound,
                    observation);
            }
            else if (observation.Authority.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     !inbound.IsTerminal &&
                     string.Equals(
                         observation.Authority.SessionId,
                         inbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         observation.Authority.TransferId,
                         inbound.TransferId,
                         StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRetryDispatchExecutionLocked(
                    inbound,
                    observation);
            }
            else
            {
                decision = new RuntimeUnlockRetryLifecycleTransitionDecision(
                    RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                    "retry_dispatch_execution_transfer_context_missing");
            }
        }

        args.Decision = decision;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_execution_decided; {RuntimeUnlockRetryDispatchTelemetry.FormatAuthorityTuple(args.Observation.Authority)}; install_generation={args.Observation.InstallReceipt.InstallGeneration}; attempt_phase={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(args.Observation.AttemptPhase.ToString())}; offer_generation={args.Observation.OfferGeneration}; offer_nonce={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(args.Observation.OfferNonce ?? "(none)")}; dispatch_observed={(args.Observation.DispatchObserved ? 1 : 0)}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Reason)}");
    }

    private void OnRuntimeUnlockRetryDispatchSuccessorEvaluationRequested(
        object? sender,
        RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRetryDispatchSuccessorDecision successorDecision;
        long dataSessionGeneration = -1;
        lock (gate)
        {
            var evaluation = args.Evaluation;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (!bridgeIsCurrentTransport || evaluation.IsDefault)
            {
                successorDecision = new RuntimeUnlockRetryDispatchSuccessorDecision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                    bridgeIsCurrentTransport
                        ? "retry_dispatch_successor_evaluation_missing"
                        : "retry_dispatch_successor_bridge_replaced",
                    evaluation);
            }
            else if (evaluation.PredecessorAuthority.Direction ==
                         FileTransferDirection.Outbound &&
                     outboundTransfer is { } outbound &&
                     !outbound.IsTerminal &&
                     string.Equals(
                         evaluation.PredecessorAuthority.SessionId,
                         outbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         evaluation.PredecessorAuthority.TransferId,
                         outbound.TransferId,
                         StringComparison.Ordinal))
            {
                successorDecision =
                    PrepareRuntimeUnlockRetryDispatchSuccessorLocked(
                        outbound,
                        evaluation);
                dataSessionGeneration = outbound.DataSessionGeneration;
            }
            else if (evaluation.PredecessorAuthority.Direction ==
                         FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     !inbound.IsTerminal &&
                     string.Equals(
                         evaluation.PredecessorAuthority.SessionId,
                         inbound.SessionId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         evaluation.PredecessorAuthority.TransferId,
                         inbound.TransferId,
                         StringComparison.Ordinal))
            {
                successorDecision =
                    PrepareRuntimeUnlockRetryDispatchSuccessorLocked(
                        inbound,
                        evaluation);
                dataSessionGeneration = inbound.DataSessionGeneration;
            }
            else
            {
                successorDecision = new RuntimeUnlockRetryDispatchSuccessorDecision(
                    RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                    "retry_dispatch_successor_transfer_context_missing",
                    evaluation);
            }
        }

        bridge.ObserveRuntimeUnlockRetryDispatchSuccessorDecision(
            successorDecision);
        LogRuntimeUnlockRetryDispatchSuccessorDecision(
            args.Evaluation,
            successorDecision);

        if (successorDecision.AuthorizationDecision is { } authorization)
        {
            ProcessRuntimeUnlockRetryDispatchDecision(
                bridge,
                authorization,
                dataSessionGeneration);
        }
    }

    private static RuntimeUnlockRetryLifecycleTransitionDecision
        ObserveRuntimeUnlockRetryDispatchExecutionLocked(
            OutboundTransferContext context,
            RuntimeUnlockRetryDispatchExecutionObservation observation)
    {
        var decision =
            FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
                CreateCoordinatorStateLocked(context),
                observation.InstallReceipt,
                observation.AttemptPhase,
                observation.OfferGeneration,
                observation.OfferNonce,
                observation.DispatchObserved);
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            decision.SnapshotEnvelope,
            "execution",
            "execution_observed");
        return decision;
    }

    private static RuntimeUnlockRetryLifecycleTransitionDecision
        ObserveRuntimeUnlockRetryDispatchExecutionLocked(
            InboundTransferContext context,
            RuntimeUnlockRetryDispatchExecutionObservation observation)
    {
        var decision =
            FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
                CreateCoordinatorStateLocked(context),
                observation.InstallReceipt,
                observation.AttemptPhase,
                observation.OfferGeneration,
                observation.OfferNonce,
                observation.DispatchObserved);
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            decision.SnapshotEnvelope,
            "execution",
            "execution_observed");
        return decision;
    }

    private static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessorLocked(
            OutboundTransferContext context,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
    {
        var state = CreateCoordinatorStateLocked(context);
        var decision = PrepareRuntimeUnlockRetryDispatchSuccessor(
            state,
            evaluation);
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        return decision;
    }

    private static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessorLocked(
            InboundTransferContext context,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
    {
        var state = CreateCoordinatorStateLocked(context);
        var decision = PrepareRuntimeUnlockRetryDispatchSuccessor(
            state,
            evaluation);
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        return decision;
    }

    private static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
        => FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchSuccessor(
            state,
            evaluation);

    private static void LogRuntimeUnlockRetryDispatchSuccessorDecision(
        RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation,
        RuntimeUnlockRetryDispatchSuccessorDecision decision)
    {
        var request = decision.Request;
        var successorProfile = evaluation.RetiredOfferGeneration == 0
            ? "pre_offer_missing_dispatch"
            : "offer_bound";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_successor_decided; {RuntimeUnlockRetryDispatchTelemetry.FormatAuthorityTuple(evaluation.PredecessorAuthority)}; retired_offer_generation={evaluation.RetiredOfferGeneration}; successor_profile={successorProfile}; predecessor_offer_generation={evaluation.PredecessorOfferGeneration}; predecessor_offer_nonce={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(evaluation.PredecessorOfferNonce ?? "(none)")}; predecessor_dispatch_observed={(evaluation.PredecessorDispatchObserved ? 1 : 0)}; requires_listener_retry={(request is { ListenerRearmRequirement: not RuntimeUnlockRetryDispatchListenerRearmRequirement.None } ? 1 : 0)}; active_predecessor_supersession={(request?.RequestActivePredecessorSupersession == true ? 1 : 0)}; requested_attempt_generation={request?.DispatchAttemptGeneration ?? 0}; requested_missing_dispatch_generation={request?.MissingDispatchSuccessorGeneration ?? 0}; inherited_release_kind={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(evaluation.PredecessorAuthority.ReleaseKind.ToString())}; inherited_release_authority_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(evaluation.PredecessorAuthority.ReleaseAuthorityId?.ToString() ?? "(none)")}; inherited_release_generation={evaluation.PredecessorAuthority.ReleaseGeneration}; inherited_release_outcome={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(evaluation.PredecessorAuthority.ReleaseOutcome)}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Reason)}");
        if (decision.Status !=
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.ChainClosed)
        {
            return;
        }

        var lifecycle = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(
            decision.State);
        var closedAttempt = lifecycle.BoundedClosure?.FinalAttempt ??
                            lifecycle.LastTerminalAttempt;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_chain_closed; {RuntimeUnlockRetryDispatchTelemetry.FormatAuthorityTuple(evaluation.PredecessorAuthority)}; install_generation={closedAttempt?.InstallReceipt?.InstallGeneration ?? 0}; attempt_generation={closedAttempt?.Authority.DispatchAttemptGeneration ?? 0}; missing_dispatch_generation={closedAttempt?.Authority.MissingDispatchSuccessorGeneration ?? 0}; predecessor_authority_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(closedAttempt?.Authority.PredecessorAuthorityId?.ToString() ?? "(none)")}; coordinator_deadline_utc_ms={closedAttempt?.DispatchDeadlineUtcMs ?? 0}; closure_generation={lifecycle.BoundedClosure?.ClosureGeneration ?? 0}; rejected_attempt_generation={request?.DispatchAttemptGeneration ?? 0}; rejected_missing_dispatch_generation={request?.MissingDispatchSuccessorGeneration ?? 0}; lifecycle_state={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(closedAttempt?.State.ToString() ?? "(none)")}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Reason)}");
    }

    private static RuntimeUnlockRetryLifecycleDeadlineDecision
        EvaluateRuntimeUnlockRetryDispatchDeadlineLocked(
            OutboundTransferContext context,
            RuntimeUnlockRetryDispatchDeadlineEvaluation evaluation)
    {
        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator
                .TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
                    state,
                    out var receipt) ||
            receipt != evaluation.InstallReceipt ||
            receipt.Authority != evaluation.Authority)
        {
            return new RuntimeUnlockRetryLifecycleDeadlineDecision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_deadline_install_receipt_not_current")
            {
                State = state,
            };
        }

        var decision =
            FileTransferCoordinator.EvaluateRuntimeUnlockRetryDispatchDeadline(
                state,
                evaluation.Authority.AuthorityId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        return decision;
    }

    private static RuntimeUnlockRetryLifecycleDeadlineDecision
        EvaluateRuntimeUnlockRetryDispatchDeadlineLocked(
            InboundTransferContext context,
            RuntimeUnlockRetryDispatchDeadlineEvaluation evaluation)
    {
        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator
                .TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
                    state,
                    out var receipt) ||
            receipt != evaluation.InstallReceipt ||
            receipt.Authority != evaluation.Authority)
        {
            return new RuntimeUnlockRetryLifecycleDeadlineDecision(
                RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "retry_dispatch_deadline_install_receipt_not_current")
            {
                State = state,
            };
        }

        var decision =
            FileTransferCoordinator.EvaluateRuntimeUnlockRetryDispatchDeadline(
                state,
                evaluation.Authority.AuthorityId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        return decision;
    }

    private static void LogRuntimeUnlockRetryLifecycleDeadlineDecision(
        RuntimeUnlockRetryDispatchDeadlineEvaluation evaluation,
        RuntimeUnlockRetryLifecycleDeadlineDecision decision)
    {
        var lifecycle = decision.State is null
            ? RuntimeUnlockRetryLifecycleSubstate.Empty
            : FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(
                decision.State);
        var attempt = lifecycle.InstalledAttempt ??
                      lifecycle.LastTerminalAttempt;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_deadline_decided; {RuntimeUnlockRetryDispatchTelemetry.FormatAuthorityTuple(evaluation.Authority)}; install_generation={evaluation.InstallReceipt.InstallGeneration}; attempt_generation={evaluation.Authority.DispatchAttemptGeneration}; missing_dispatch_generation={evaluation.Authority.MissingDispatchSuccessorGeneration}; predecessor_authority_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(evaluation.Authority.PredecessorAuthorityId?.ToString() ?? "(none)")}; lifecycle_state={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(attempt?.State.ToString() ?? "(none)")}; coordinator_deadline_utc_ms={attempt?.DispatchDeadlineUtcMs ?? 0}; observed_utc_ms={evaluation.ObservedUtcMs}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(decision.Reason)}");
    }

    private void ScheduleRuntimeUnlockRetryDispatchStageExpiry(
        RuntimeUnlockRetryLifecycleAttemptSnapshot stagedAttempt,
        long dataSessionGeneration)
    {
        _ = Task.Run(
            async () =>
            {
                var delayMilliseconds = Math.Max(
                    1,
                    stagedAttempt.StageExpiresUtcMs -
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await Task.Delay(
                        TimeSpan.FromMilliseconds(delayMilliseconds))
                    .ConfigureAwait(false);

                lock (gate)
                {
                    var nowMs =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    FileTransferCoordinatorState? currentState = null;
                    if (stagedAttempt.Authority.Direction ==
                            FileTransferDirection.Outbound &&
                        outboundTransfer is { } outbound &&
                        !outbound.IsTerminal &&
                        outbound.DataSessionGeneration ==
                            dataSessionGeneration &&
                        string.Equals(
                            outbound.SessionId,
                            stagedAttempt.Authority.SessionId,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            outbound.TransferId,
                            stagedAttempt.Authority.TransferId,
                            StringComparison.Ordinal))
                    {
                        currentState =
                            CreateCoordinatorStateLocked(outbound);
                        _ = TryExpireRuntimeUnlockRetryDispatchStageLocked(
                            outbound,
                            currentState,
                            stagedAttempt.Authority.AuthorityId,
                            nowMs);
                    }
                    else if (stagedAttempt.Authority.Direction ==
                                 FileTransferDirection.Inbound &&
                             inboundTransfer is { } inbound &&
                             !inbound.IsTerminal &&
                             inbound.DataSessionGeneration ==
                                 dataSessionGeneration &&
                             string.Equals(
                                 inbound.SessionId,
                                 stagedAttempt.Authority.SessionId,
                                 StringComparison.Ordinal) &&
                             string.Equals(
                                 inbound.TransferId,
                                 stagedAttempt.Authority.TransferId,
                                 StringComparison.Ordinal))
                    {
                        currentState =
                            CreateCoordinatorStateLocked(inbound);
                        _ = TryExpireRuntimeUnlockRetryDispatchStageLocked(
                            inbound,
                            currentState,
                            stagedAttempt.Authority.AuthorityId,
                            nowMs);
                    }
                }
            },
            CancellationToken.None);
    }

    private bool TryExpireRuntimeUnlockRetryDispatchStageLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorState state,
        FileTransferAuthorityId authorityId,
        long observedUtcMs)
    {
        var prepared =
            FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchStageExpiry(
                state,
                authorityId,
                observedUtcMs);
        if (!prepared.Accepted || prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            prepared.State,
            prepared.SnapshotEnvelope);
        if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return false;
        }

        var staged =
            bridge.StageRuntimeUnlockRetryDispatchScopeInvalidation(command);
        if (!staged.Accepted || staged.Command != command)
        {
            return false;
        }

        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                committed.State,
                committed.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            committed.State,
            committed.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            committed.SnapshotEnvelope,
            "scope_invalidation",
            "scope_invalidation_applied");
        return true;
    }

    private bool TryExpireRuntimeUnlockRetryDispatchStageLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state,
        FileTransferAuthorityId authorityId,
        long observedUtcMs)
    {
        var prepared =
            FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchStageExpiry(
                state,
                authorityId,
                observedUtcMs);
        if (!prepared.Accepted || prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            prepared.State,
            prepared.SnapshotEnvelope);
        if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return false;
        }

        var staged =
            bridge.StageRuntimeUnlockRetryDispatchScopeInvalidation(command);
        if (!staged.Accepted || staged.Command != command)
        {
            return false;
        }

        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                committed.State,
                committed.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            committed.State,
            committed.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            committed.SnapshotEnvelope,
            "scope_invalidation",
            "scope_invalidation_applied");
        return true;
    }

    private bool TryResumePendingRuntimeUnlockRetryDispatchStageExpiryLocked(
        OutboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        var lifecycle =
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not
            {
                Kind:
                    RuntimeUnlockRetryDispatchScopeInvalidationKind
                        .PendingStageExpiry,
                PendingAttempt: { } pendingAttempt,
            })
        {
            return true;
        }

        return TryExpireRuntimeUnlockRetryDispatchStageLocked(
            context,
            state,
            pendingAttempt.Authority.AuthorityId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private bool TryResumePendingRuntimeUnlockRetryDispatchStageExpiryLocked(
        InboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        var lifecycle =
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state);
        if (lifecycle.PendingScopeInvalidationCommand is not
            {
                Kind:
                    RuntimeUnlockRetryDispatchScopeInvalidationKind
                        .PendingStageExpiry,
                PendingAttempt: { } pendingAttempt,
            })
        {
            return true;
        }

        return TryExpireRuntimeUnlockRetryDispatchStageLocked(
            context,
            state,
            pendingAttempt.Authority.AuthorityId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static void ApplyRuntimeUnlockRetryLifecycleStateLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorState state,
        FileTransferCoordinatorTransitionEnvelope? envelope = null)
    {
        var previous =
            context.CoordinatorPersistentState.FileTransferReceiveRecovery;
        var previousFallbackTail =
            context.CoordinatorPersistentState.FallbackTailYield;
        state = envelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                state,
                "RuntimeUnlockRetryLifecycleStateAdopted",
                "Accepted",
                "runtime_unlock_retry_lifecycle_transition")
            : AdoptRuntimeUnlockRetryCoordinatorStateLocked(
                context,
                state,
                envelope);
        LogReceiveRecoveryOperationTransitions(
            previous,
            state.PersistentState.FileTransferReceiveRecovery,
            "runtime_unlock_retry_lifecycle_transition");
        LogFallbackTailYieldAggregateTransitions(
            previousFallbackTail,
            state.PersistentState.FallbackTailYield,
            "runtime_unlock_retry_lifecycle_transition");
    }

    private static void ApplyRuntimeUnlockRetryLifecycleStateLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state,
        FileTransferCoordinatorTransitionEnvelope? envelope = null)
    {
        var previous =
            context.CoordinatorPersistentState.FileTransferReceiveRecovery;
        var previousFallbackTail =
            context.CoordinatorPersistentState.FallbackTailYield;
        state = envelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                state,
                "RuntimeUnlockRetryLifecycleStateAdopted",
                "Accepted",
                "runtime_unlock_retry_lifecycle_transition")
            : AdoptRuntimeUnlockRetryCoordinatorStateLocked(
                context,
                state,
                envelope);
        LogReceiveRecoveryOperationTransitions(
            previous,
            state.PersistentState.FileTransferReceiveRecovery,
            "runtime_unlock_retry_lifecycle_transition");
        LogFallbackTailYieldAggregateTransitions(
            previousFallbackTail,
            state.PersistentState.FallbackTailYield,
            "runtime_unlock_retry_lifecycle_transition");
    }

    private bool TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
        OutboundTransferContext context,
        string reason,
        RuntimeUnlockRetryLifecycleScopeInvalidationDecision? preparedDecision = null)
    {
        if (!TryResumePendingRuntimeUnlockRetryDispatchStageExpiryLocked(context))
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        var previous =
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state);
        var authority = previous.CurrentAuthorizedAuthority;
        var prepared = preparedDecision ??
            FileTransferCoordinator
                .PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(
                    state,
                    reason);
        if (!prepared.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return false;
        }

        if (prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return true;
        }

        if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return false;
        }

        var staged =
            bridge.StageRuntimeUnlockRetryDispatchScopeInvalidation(command);
        if (!staged.Accepted || staged.Command != command)
        {
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            prepared.State,
            prepared.SnapshotEnvelope);
        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                committed.State,
                committed.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            committed.State,
            committed.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            committed.SnapshotEnvelope,
            "scope_invalidation",
            "scope_invalidation_applied");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_scope_invalidated; direction=outbound; session_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(context.SessionId)}; transfer_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(context.TransferId)}; authority_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(authority?.AuthorityId.ToString() ?? "(none)")}; data_session_generation={context.DataSessionGeneration}; invalidation_generation={command.InvalidationGeneration}; invalidation_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(receipt.Status.ToString())}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(committed.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(committed.Reason)}");
        return true;
    }

    private bool TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
        InboundTransferContext context,
        string reason)
    {
        if (!TryResumePendingRuntimeUnlockRetryDispatchStageExpiryLocked(context))
        {
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        var previous =
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state);
        var authority = previous.CurrentAuthorizedAuthority;
        var prepared =
            FileTransferCoordinator
                .PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(
                    state,
                    reason);
        if (!prepared.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return false;
        }

        if (prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State,
                prepared.SnapshotEnvelope);
            return true;
        }

        if (transport is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return false;
        }

        var staged =
            bridge.StageRuntimeUnlockRetryDispatchScopeInvalidation(command);
        if (!staged.Accepted || staged.Command != command)
        {
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            prepared.State,
            prepared.SnapshotEnvelope);
        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                committed.State,
                committed.SnapshotEnvelope);
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            committed.State,
            committed.SnapshotEnvelope);
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            committed.SnapshotEnvelope,
            "scope_invalidation",
            "scope_invalidation_applied");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_lifecycle_scope_invalidated; direction=inbound; session_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(context.SessionId)}; transfer_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(context.TransferId)}; authority_id={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(authority?.AuthorityId.ToString() ?? "(none)")}; data_session_generation={context.DataSessionGeneration}; invalidation_generation={command.InvalidationGeneration}; invalidation_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(receipt.Status.ToString())}; decision_status={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(committed.Status.ToString())}; decision_reason={RuntimeUnlockRetryDispatchTelemetry.SanitizeToken(committed.Reason)}");
        return true;
    }

    private static RuntimeUnlockRetryDispatchSettlementDecision
        ObserveRuntimeUnlockRetryDispatchSettlementLocked(
            OutboundTransferContext context,
            RuntimeUnlockRetryDispatchSettlementReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            CreateCoordinatorStateLocked(context),
            report);
        ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(context, decision);
        return decision;
    }

    private static RuntimeUnlockRetryDispatchSettlementDecision
        ObserveRuntimeUnlockRetryDispatchSettlementLocked(
            InboundTransferContext context,
            RuntimeUnlockRetryDispatchSettlementReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            CreateCoordinatorStateLocked(context),
            report);
        ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(context, decision);
        return decision;
    }

    private static void ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        if (context.IsTerminal)
        {
            return;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        if (decision.Report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded)
        {
            EnqueueRuntimeUnlockRetrySupersededSettlementAuthorityEffect(
                context.CoordinatorSnapshotEmitter, context.CoordinatorInstanceId, context.SessionId,
                context.TransferId, FileTransferDirection.Outbound,
                context.CoordinatorPersistentState.SnapshotStateGeneration, decision);
            return;
        }
        if (TryEnqueueRuntimeUnlockRetrySuccessorSettlementAuthorityEffect(
                context.CoordinatorSnapshotEmitter, context.CoordinatorInstanceId, context.SessionId,
                context.TransferId, FileTransferDirection.Outbound,
                context.CoordinatorPersistentState.SnapshotStateGeneration, decision)) { return; }
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            decision.SnapshotEnvelope,
            "settlement",
            "settlement_observed");
    }

    private static void ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(
        InboundTransferContext context,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        if (context.IsTerminal)
        {
            return;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(
            context,
            decision.State,
            decision.SnapshotEnvelope);
        if (decision.Report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded)
        {
            EnqueueRuntimeUnlockRetrySupersededSettlementAuthorityEffect(
                context.CoordinatorSnapshotEmitter, context.CoordinatorInstanceId, context.SessionId,
                context.TransferId, FileTransferDirection.Inbound,
                context.CoordinatorPersistentState.SnapshotStateGeneration, decision);
            return;
        }
        if (TryEnqueueRuntimeUnlockRetrySuccessorSettlementAuthorityEffect(
                context.CoordinatorSnapshotEmitter, context.CoordinatorInstanceId, context.SessionId,
                context.TransferId, FileTransferDirection.Inbound,
                context.CoordinatorPersistentState.SnapshotStateGeneration, decision)) { return; }
        EnqueueRuntimeUnlockRetryAuthorityEffect(
            context,
            decision.SnapshotEnvelope,
            "settlement",
            "settlement_observed");
    }

    private static void ApplyRuntimeUnlockRetryDispatchAuthorityDecisionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryDispatchDecision decision)
    {
        if (decision.SnapshotEnvelope is not null)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                decision.State,
                decision.SnapshotEnvelope);
            return;
        }

        if (decision.Granted &&
            decision.Contract is not null &&
            FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
                decision,
                request,
                out _))
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
            return;
        }

        if (decision.Status ==
                RuntimeUnlockRetryDispatchDecisionStatus.Held &&
            (string.Equals(
                 decision.Reason,
                 "receive_recovery_lease_pending",
                 StringComparison.Ordinal) ||
             string.Equals(
                 decision.Reason,
                 "fallback_paired_tail_yield_pending",
                 StringComparison.Ordinal)) &&
            decision.State is not null)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                decision.State);
            return;
        }

        if (decision.Status != RuntimeUnlockRetryDispatchDecisionStatus.Rejected ||
            !string.Equals(
                decision.Reason,
                RuntimeUnlockRetryMissingSuccessorLimitExhaustedReason,
                StringComparison.Ordinal) ||
            decision.State is null)
        {
            return;
        }

        ApplyBoundedRuntimeUnlockRetryDispatchClosureLocked(context, decision.State);
    }

    private static void ApplyRuntimeUnlockRetryDispatchAuthorityDecisionLocked(
        InboundTransferContext context,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryDispatchDecision decision)
    {
        if (decision.SnapshotEnvelope is not null)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                decision.State,
                decision.SnapshotEnvelope);
            return;
        }

        if (decision.Granted &&
            decision.Contract is not null &&
            FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
                decision,
                request,
                out _))
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
            return;
        }

        if (decision.Status ==
                RuntimeUnlockRetryDispatchDecisionStatus.Held &&
            string.Equals(
                decision.Reason,
                "receive_recovery_lease_pending",
                StringComparison.Ordinal) &&
            decision.State is not null)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                decision.State);
            return;
        }

        if (decision.Status != RuntimeUnlockRetryDispatchDecisionStatus.Rejected ||
            !string.Equals(
                decision.Reason,
                RuntimeUnlockRetryMissingSuccessorLimitExhaustedReason,
                StringComparison.Ordinal) ||
            decision.State is null)
        {
            return;
        }

        ApplyBoundedRuntimeUnlockRetryDispatchClosureLocked(context, decision.State);
    }

    private static void ApplyBoundedRuntimeUnlockRetryDispatchClosureLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorState state)
    {
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, state);
    }

    private static void ApplyBoundedRuntimeUnlockRetryDispatchClosureLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state)
    {
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, state);
    }
}
