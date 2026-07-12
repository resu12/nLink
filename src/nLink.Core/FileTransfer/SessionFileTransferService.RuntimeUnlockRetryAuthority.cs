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
    }
}
