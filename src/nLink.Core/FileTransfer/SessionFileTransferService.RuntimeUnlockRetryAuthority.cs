using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const string RuntimeUnlockRetryMissingSuccessorLimitExhaustedReason =
        "retry_dispatch_missing_successor_limit_exhausted";

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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
        return decision;
    }

    private static RuntimeUnlockRetryDispatchSuccessorDecision
        PrepareRuntimeUnlockRetryDispatchSuccessor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
    {
        if (!FileTransferCoordinator
                .TryGetInstalledRuntimeUnlockRetryDispatchAttempt(
                    state,
                    out var installed) ||
            installed.Authority != evaluation.PredecessorAuthority)
        {
            return new RuntimeUnlockRetryDispatchSuccessorDecision(
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Rejected,
                "retry_dispatch_successor_predecessor_tuple_not_current",
                evaluation)
            {
                State = state,
            };
        }

        return FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchSuccessor(
            state,
            evaluation);
    }

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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
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
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, prepared.State);
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
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, committed.State);
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
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, prepared.State);
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
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, committed.State);
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
        FileTransferCoordinatorState state)
    {
        var previous =
            context.CoordinatorPersistentState.FileTransferReceiveRecovery;
        var previousFallbackTail =
            context.CoordinatorPersistentState.FallbackTailYield;
        context.CoordinatorPersistentState = state.PersistentState;
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
        FileTransferCoordinatorState state)
    {
        var previous =
            context.CoordinatorPersistentState.FileTransferReceiveRecovery;
        var previousFallbackTail =
            context.CoordinatorPersistentState.FallbackTailYield;
        context.CoordinatorPersistentState = state.PersistentState;
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
            return false;
        }

        if (prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State);
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
            prepared.State);
        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, committed.State);
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
            return false;
        }

        if (prepared.Command is not { } command)
        {
            ApplyRuntimeUnlockRetryLifecycleStateLocked(
                context,
                prepared.State);
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
            prepared.State);
        var receipt =
            bridge.ApplyRuntimeUnlockRetryDispatchScopeInvalidation(command);
        var committed =
            FileTransferCoordinator
                .CommitRuntimeUnlockRetryLifecycleScopeInvalidation(
                    prepared.State,
                    receipt);
        if (!committed.Accepted)
        {
            return false;
        }

        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, committed.State);
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
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
    }

    private static void ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(
        InboundTransferContext context,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        ApplyRuntimeUnlockRetryLifecycleStateLocked(context, decision.State);
    }

    private static void ApplyRuntimeUnlockRetryDispatchAuthorityDecisionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryDispatchDecision decision)
    {
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
