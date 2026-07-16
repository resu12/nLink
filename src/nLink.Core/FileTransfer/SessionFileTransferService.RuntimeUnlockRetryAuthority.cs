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

    private void OnRuntimeUnlockRegularV4RecoveryLifecycleReported(
        object? sender,
        RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        RuntimeUnlockRegularV4RecoveryLifecycleDecision? decision = null;
        lock (gate)
        {
            var report = args.Report;
            var bridgeIsCurrentTransport =
                transport is IRuntimeUnlockRetryDispatchAuthorityBridge currentBridge &&
                ReferenceEquals(currentBridge, bridge);
            if (!report.IsDefault &&
                bridgeIsCurrentTransport &&
                report.Authority.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { } outbound &&
                string.Equals(report.Authority.SessionId, outbound.SessionId, StringComparison.Ordinal) &&
                string.Equals(report.Authority.TransferId, outbound.TransferId, StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRegularV4RecoveryLifecycleLocked(outbound, report);
            }
            else if (!report.IsDefault &&
                     bridgeIsCurrentTransport &&
                     report.Authority.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     string.Equals(report.Authority.SessionId, inbound.SessionId, StringComparison.Ordinal) &&
                     string.Equals(report.Authority.TransferId, inbound.TransferId, StringComparison.Ordinal))
            {
                decision = ObserveRuntimeUnlockRegularV4RecoveryLifecycleLocked(inbound, report);
            }
            else if (outboundTransfer is { } rejectionOutbound)
            {
                decision = new RuntimeUnlockRegularV4RecoveryLifecycleDecision(
                    RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                    "regular_v4_recovery_lifecycle_transfer_missing",
                    report)
                {
                    State = CreateCoordinatorStateLocked(rejectionOutbound),
                };
            }
            else if (inboundTransfer is { } rejectionInbound)
            {
                decision = new RuntimeUnlockRegularV4RecoveryLifecycleDecision(
                    RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                    "regular_v4_recovery_lifecycle_transfer_missing",
                    report)
                {
                    State = CreateCoordinatorStateLocked(rejectionInbound),
                };
            }
        }

        if (decision is { } resolvedDecision)
        {
            bridge.ObserveRuntimeUnlockRegularV4RecoveryLifecycleDecision(resolvedDecision);
        }
    }

    private static RuntimeUnlockRegularV4RecoveryLifecycleDecision
        ObserveRuntimeUnlockRegularV4RecoveryLifecycleLocked(
            OutboundTransferContext context,
            RuntimeUnlockRegularV4RecoveryLifecycleReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            CreateCoordinatorStateLocked(context),
            context.LastRuntimeUnlockRegularV4RecoveryLifecycle,
            report);
        if (decision.State is not null)
        {
            context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
            context.LastRetiredRecoveryLivenessLease = decision.State.LastRetiredRecoveryLivenessLease;
            context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
            context.LastRuntimeUnlockRegularV4RecoveryLifecycle =
                decision.State.LastRuntimeUnlockRegularV4RecoveryLifecycle;
            context.RuntimeUnlockRecoveryLeaseClosureAvailable =
                decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
        }

        return decision;
    }

    private static RuntimeUnlockRegularV4RecoveryLifecycleDecision
        ObserveRuntimeUnlockRegularV4RecoveryLifecycleLocked(
            InboundTransferContext context,
            RuntimeUnlockRegularV4RecoveryLifecycleReport report)
    {
        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            CreateCoordinatorStateLocked(context),
            context.LastRuntimeUnlockRegularV4RecoveryLifecycle,
            report);
        if (decision.State is not null)
        {
            context.CurrentRecoveryLivenessLease = decision.State.CurrentRecoveryLivenessLease;
            context.LastRetiredRecoveryLivenessLease = decision.State.LastRetiredRecoveryLivenessLease;
            context.LastRecoveryLivenessLeaseGeneration = decision.State.LastRecoveryLivenessLeaseGeneration;
            context.LastRuntimeUnlockRegularV4RecoveryLifecycle =
                decision.State.LastRuntimeUnlockRegularV4RecoveryLifecycle;
            context.RuntimeUnlockRecoveryLeaseClosureAvailable =
                decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
        }

        return decision;
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
            context.RuntimeUnlockRecoveryLeaseClosureAvailable =
                decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
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
            context.RuntimeUnlockRecoveryLeaseClosureAvailable =
                decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
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
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract =
            ReconcileRuntimeUnlockRetryDispatchActivePredecessor(
                context.PendingRuntimeUnlockRetryDispatchPredecessorContract,
                context.CurrentRuntimeUnlockRetryDispatchContract);
        context.CurrentRuntimeUnlockRetryDispatchInstallReceipt =
            ReconcileRuntimeUnlockRetryDispatchInstallReceipt(
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt,
                context.CurrentRuntimeUnlockRetryDispatchContract);
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
        context.RuntimeUnlockRecoveryLeaseClosureAvailable =
            decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
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
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract =
            ReconcileRuntimeUnlockRetryDispatchActivePredecessor(
                context.PendingRuntimeUnlockRetryDispatchPredecessorContract,
                context.CurrentRuntimeUnlockRetryDispatchContract);
        context.CurrentRuntimeUnlockRetryDispatchInstallReceipt =
            ReconcileRuntimeUnlockRetryDispatchInstallReceipt(
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt,
                context.CurrentRuntimeUnlockRetryDispatchContract);
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
        context.RuntimeUnlockRecoveryLeaseClosureAvailable =
            decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable;
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
            var previousContract = context.CurrentRuntimeUnlockRetryDispatchContract;
            if (context.CurrentRuntimeUnlockRetryDispatchInstallReceipt is { } currentReceipt &&
                currentReceipt.Authority != contract.Authority)
            {
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt = null;
            }
            if (!ReferenceEquals(previousContract, contract))
            {
                context.PendingRuntimeUnlockRetryDispatchPredecessorContract =
                    CaptureRuntimeUnlockRetryDispatchActivePredecessor(
                        previousContract,
                        contract);
            }
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
            var previousContract = context.CurrentRuntimeUnlockRetryDispatchContract;
            if (context.CurrentRuntimeUnlockRetryDispatchInstallReceipt is { } currentReceipt &&
                currentReceipt.Authority != contract.Authority)
            {
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt = null;
            }
            if (!ReferenceEquals(previousContract, contract))
            {
                context.PendingRuntimeUnlockRetryDispatchPredecessorContract =
                    CaptureRuntimeUnlockRetryDispatchActivePredecessor(
                        previousContract,
                        contract);
            }
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

    private static RuntimeUnlockRetryDispatchContract?
        CaptureRuntimeUnlockRetryDispatchActivePredecessor(
            RuntimeUnlockRetryDispatchContract? previousContract,
            RuntimeUnlockRetryDispatchContract successorContract)
        => successorContract.RequestActivePredecessorSupersession &&
           successorContract.Authority.PredecessorAuthorityId is { } predecessorAuthorityId &&
           previousContract is { Lifecycle.IsSettled: false } predecessor &&
           predecessor.Authority.AuthorityId == predecessorAuthorityId
            ? predecessor
            : null;

    private static RuntimeUnlockRetryDispatchContract?
        ReconcileRuntimeUnlockRetryDispatchActivePredecessor(
            RuntimeUnlockRetryDispatchContract? predecessorContract,
            RuntimeUnlockRetryDispatchContract? currentContract)
        => predecessorContract is { Lifecycle.IsSettled: false } predecessor &&
           currentContract is
           {
               RequestActivePredecessorSupersession: true,
               Lifecycle.IsSettled: false,
           } successor &&
           successor.Authority.PredecessorAuthorityId == predecessor.Authority.AuthorityId
            ? predecessor
            : null;

    private static bool TryApplyCommittedRuntimeUnlockRetryDispatchPredecessorLocked(
        OutboundTransferContext context,
        RuntimeUnlockRetryDispatchDecision decision,
        out string rejectionReason)
    {
        var accepted = TryValidateCommittedRuntimeUnlockRetryDispatchPredecessor(
            context.PendingRuntimeUnlockRetryDispatchPredecessorContract,
            decision,
            out var settlement,
            out rejectionReason);
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract = null;
        if (accepted && settlement is { } report)
        {
            context.LastRuntimeUnlockRetryDispatchSettlement = report;
        }

        return accepted;
    }

    private static bool TryApplyCommittedRuntimeUnlockRetryDispatchPredecessorLocked(
        InboundTransferContext context,
        RuntimeUnlockRetryDispatchDecision decision,
        out string rejectionReason)
    {
        var accepted = TryValidateCommittedRuntimeUnlockRetryDispatchPredecessor(
            context.PendingRuntimeUnlockRetryDispatchPredecessorContract,
            decision,
            out var settlement,
            out rejectionReason);
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract = null;
        if (accepted && settlement is { } report)
        {
            context.LastRuntimeUnlockRetryDispatchSettlement = report;
        }

        return accepted;
    }

    private static bool TryValidateCommittedRuntimeUnlockRetryDispatchPredecessor(
        RuntimeUnlockRetryDispatchContract? predecessorContract,
        RuntimeUnlockRetryDispatchDecision decision,
        out RuntimeUnlockRetryDispatchSettlementReport? settlement,
        out string rejectionReason)
    {
        settlement = null;
        rejectionReason = "accepted";
        if (decision.Contract is not { } successor ||
            !successor.RequestActivePredecessorSupersession)
        {
            if (predecessorContract is null)
            {
                return true;
            }

            rejectionReason = "retry_dispatch_install_unexpected_predecessor";
            return false;
        }

        if (predecessorContract is not { } predecessor ||
            successor.Authority.PredecessorAuthorityId != predecessor.Authority.AuthorityId ||
            predecessor.Lifecycle.SettlementOutcome !=
                RuntimeUnlockRetryDispatchSettlementOutcome.Superseded ||
            predecessor.Lifecycle.SuccessorAuthorityId != successor.Authority.AuthorityId ||
            !string.Equals(
                predecessor.Lifecycle.SettlementReason,
                "coordinator_successor_installed",
                StringComparison.Ordinal) ||
            !predecessor.TryCreateSettlementReport(out var report))
        {
            rejectionReason = "retry_dispatch_install_predecessor_commit_mismatch";
            return false;
        }

        settlement = report;
        return true;
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
        context.CurrentRuntimeUnlockRetryDispatchInstallReceipt =
            ReconcileRuntimeUnlockRetryDispatchInstallReceipt(
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt,
                context.CurrentRuntimeUnlockRetryDispatchContract);
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract = null;
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
        context.RuntimeUnlockRecoveryLeaseClosureAvailable =
            state.RuntimeUnlockRecoveryLeaseClosureAvailable;
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
        context.CurrentRuntimeUnlockRetryDispatchInstallReceipt =
            ReconcileRuntimeUnlockRetryDispatchInstallReceipt(
                context.CurrentRuntimeUnlockRetryDispatchInstallReceipt,
                context.CurrentRuntimeUnlockRetryDispatchContract);
        context.PendingRuntimeUnlockRetryDispatchPredecessorContract = null;
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
        context.RuntimeUnlockRecoveryLeaseClosureAvailable =
            state.RuntimeUnlockRecoveryLeaseClosureAvailable;
    }
}
