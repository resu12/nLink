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

    private void OnRuntimeUnlockRegularV4RecoverySettlementReported(
        object? sender,
        RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRegularV4RecoverySettlementDecision? decision = null;
        lock (gate)
        {
            var report = args.Report;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (bridgeIsCurrentTransport && report.IsDefault)
            {
                if (outboundTransfer is { } defaultOutbound)
                {
                    decision = ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(defaultOutbound, report);
                }
                else if (inboundTransfer is { } defaultInbound)
                {
                    decision = ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(defaultInbound, report);
                }
            }
            else if (bridgeIsCurrentTransport &&
                     report.Settlement.Direction == FileTransferDirection.Outbound &&
                     outboundTransfer is { } outbound)
            {
                decision = ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(outbound, report);
            }
            else if (bridgeIsCurrentTransport &&
                     report.Settlement.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound)
            {
                decision = ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(inbound, report);
            }
            else if (outboundTransfer is { } rejectionOutbound)
            {
                decision = new RuntimeUnlockRegularV4RecoverySettlementDecision(
                    RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                    "retry_dispatch_regular_v4_recovery_settlement_transfer_missing",
                    report)
                {
                    State = CreateCoordinatorStateLocked(rejectionOutbound),
                };
            }
            else if (inboundTransfer is { } rejectionInbound)
            {
                decision = new RuntimeUnlockRegularV4RecoverySettlementDecision(
                    RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                    "retry_dispatch_regular_v4_recovery_settlement_transfer_missing",
                    report)
                {
                    State = CreateCoordinatorStateLocked(rejectionInbound),
                };
            }
        }

        if (decision is not { } resolvedDecision)
        {
            // No coordinator state exists for this late report. Returning no
            // disposition makes the bridge's synchronous report flow fail closed.
            return;
        }

        // Infra owns accelerationGate while applying this disposition. Keep the
        // coordinator-to-Infra callback outside the Core transfer lock.
        bridge.ObserveRuntimeUnlockRegularV4RecoverySettlementDecision(resolvedDecision);
    }

    private static RuntimeUnlockRegularV4RecoverySettlementDecision
        ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(
            OutboundTransferContext context,
            RuntimeUnlockRegularV4RecoverySettlementReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            CreateCoordinatorStateLocked(context),
            context.LastRuntimeUnlockRegularV4RecoverySettlement,
            report);
        if (decision.State is not null)
        {
            context.LastRuntimeUnlockRegularV4RecoverySettlement =
                decision.State.LastRuntimeUnlockRegularV4RecoverySettlement;
            context.LastCoordinatorIssuedRegularV4RecoverySettlement =
                decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
            context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
                decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
        }

        return decision;
    }

    private static RuntimeUnlockRegularV4RecoverySettlementDecision
        ObserveRuntimeUnlockRegularV4RecoverySettlementLocked(
            InboundTransferContext context,
            RuntimeUnlockRegularV4RecoverySettlementReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            CreateCoordinatorStateLocked(context),
            context.LastRuntimeUnlockRegularV4RecoverySettlement,
            report);
        if (decision.State is not null)
        {
            context.LastRuntimeUnlockRegularV4RecoverySettlement =
                decision.State.LastRuntimeUnlockRegularV4RecoverySettlement;
            context.LastCoordinatorIssuedRegularV4RecoverySettlement =
                decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
            context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
                decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
        }

        return decision;
    }

    private static RuntimeUnlockRetryDispatchSettlementDecision
        ObserveRuntimeUnlockRetryDispatchSettlementLocked(
            OutboundTransferContext context,
            RuntimeUnlockRetryDispatchSettlementReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            CreateCoordinatorStateLocked(context),
            context.CurrentRuntimeUnlockRetryDispatchContract,
            context.LastRuntimeUnlockRetryDispatchSettlement,
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
            context.CurrentRuntimeUnlockRetryDispatchContract,
            context.LastRuntimeUnlockRetryDispatchSettlement,
            report);
        ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(context, decision);
        return decision;
    }

    private static void ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        context.CurrentRuntimeUnlockRetryDispatchContract =
            ReconcileRuntimeUnlockRetryDispatchContract(
                context.CurrentRuntimeUnlockRetryDispatchContract,
                decision.State,
                context.LastRuntimeUnlockRetryDispatchSettlement,
                "coordinator_retry_dispatch_settlement_decision",
                out var reconciliationSettlement);
        context.LastRuntimeUnlockRetryDispatchSettlement =
            reconciliationSettlement ??
            decision.State.LastRuntimeUnlockRetryDispatchSettlement ??
            context.LastRuntimeUnlockRetryDispatchSettlement;
        context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
            decision.State.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
        context.LastCoordinatorIssuedRegularV4RecoverySettlement =
            decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
        context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
            decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
    }

    private static void ApplyRuntimeUnlockRetryDispatchSettlementDecisionLocked(
        InboundTransferContext context,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        context.CurrentRuntimeUnlockRetryDispatchContract =
            ReconcileRuntimeUnlockRetryDispatchContract(
                context.CurrentRuntimeUnlockRetryDispatchContract,
                decision.State,
                context.LastRuntimeUnlockRetryDispatchSettlement,
                "coordinator_retry_dispatch_settlement_decision",
                out var reconciliationSettlement);
        context.LastRuntimeUnlockRetryDispatchSettlement =
            reconciliationSettlement ??
            decision.State.LastRuntimeUnlockRetryDispatchSettlement ??
            context.LastRuntimeUnlockRetryDispatchSettlement;
        context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
            decision.State.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
        context.LastCoordinatorIssuedRegularV4RecoverySettlement =
            decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
        context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
            decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
    }

    private static void ApplyRuntimeUnlockRetryDispatchAuthorityDecisionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryDispatchDecision decision)
    {
        if (decision.Granted &&
            decision.Contract is { } contract &&
            FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
                decision,
                request,
                out _))
        {
            context.CurrentRuntimeUnlockRetryDispatchContract = contract;
            context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
                decision.State.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
            context.LastRuntimeUnlockRetryDispatchSettlement =
                decision.State.LastRuntimeUnlockRetryDispatchSettlement ??
                context.LastRuntimeUnlockRetryDispatchSettlement;
            context.LastRuntimeUnlockRegularV4RecoverySettlement =
                decision.State.LastRuntimeUnlockRegularV4RecoverySettlement;
            context.LastCoordinatorIssuedRegularV4RecoverySettlement =
                decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
            context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
                decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
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
            decision.Contract is { } contract &&
            FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
                decision,
                request,
                out _))
        {
            context.CurrentRuntimeUnlockRetryDispatchContract = contract;
            context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
                decision.State.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
            context.LastRuntimeUnlockRetryDispatchSettlement =
                decision.State.LastRuntimeUnlockRetryDispatchSettlement ??
                context.LastRuntimeUnlockRetryDispatchSettlement;
            context.LastRuntimeUnlockRegularV4RecoverySettlement =
                decision.State.LastRuntimeUnlockRegularV4RecoverySettlement;
            context.LastCoordinatorIssuedRegularV4RecoverySettlement =
                decision.State.LastCoordinatorIssuedRegularV4RecoverySettlement;
            context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
                decision.State.RuntimeUnlockRegularV4RecoverySettlementAvailable;
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
        context.CurrentRuntimeUnlockRetryDispatchContract =
            ReconcileRuntimeUnlockRetryDispatchContract(
                context.CurrentRuntimeUnlockRetryDispatchContract,
                state,
                context.LastRuntimeUnlockRetryDispatchSettlement,
                "coordinator_retry_dispatch_chain_closed",
                out var reconciliationSettlement);
        context.LastRuntimeUnlockRetryDispatchSettlement =
            reconciliationSettlement ??
            state.LastRuntimeUnlockRetryDispatchSettlement ??
            context.LastRuntimeUnlockRetryDispatchSettlement;
        context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
            state.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
        context.LastRuntimeUnlockRegularV4RecoverySettlement =
            state.LastRuntimeUnlockRegularV4RecoverySettlement;
        context.LastCoordinatorIssuedRegularV4RecoverySettlement =
            state.LastCoordinatorIssuedRegularV4RecoverySettlement;
        context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
            state.RuntimeUnlockRegularV4RecoverySettlementAvailable;
    }

    private static void ApplyBoundedRuntimeUnlockRetryDispatchClosureLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state)
    {
        context.CurrentRuntimeUnlockRetryDispatchContract =
            ReconcileRuntimeUnlockRetryDispatchContract(
                context.CurrentRuntimeUnlockRetryDispatchContract,
                state,
                context.LastRuntimeUnlockRetryDispatchSettlement,
                "coordinator_retry_dispatch_chain_closed",
                out var reconciliationSettlement);
        context.LastRuntimeUnlockRetryDispatchSettlement =
            reconciliationSettlement ??
            state.LastRuntimeUnlockRetryDispatchSettlement ??
            context.LastRuntimeUnlockRetryDispatchSettlement;
        context.LastRuntimeUnlockRetryDispatchAuthorityGeneration =
            state.LastRuntimeUnlockRetryDispatchAuthorityGeneration;
        context.LastRuntimeUnlockRegularV4RecoverySettlement =
            state.LastRuntimeUnlockRegularV4RecoverySettlement;
        context.LastCoordinatorIssuedRegularV4RecoverySettlement =
            state.LastCoordinatorIssuedRegularV4RecoverySettlement;
        context.RuntimeUnlockRegularV4RecoverySettlementAvailable =
            state.RuntimeUnlockRegularV4RecoverySettlementAvailable;
    }
}
