using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const string CoordinatorSnapshotCatalogVersion =
        "snapshot-authority-v1";

    private const string CoordinatorSnapshotCatalogHash =
        "17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2";

    private const string CoordinatorSnapshotEmittedFamilyCoverage =
        "route_runtime,route_transition,runtime_unlock_retry_lifecycle,receive_recovery,fallback_tail_yield,regular_v4_frontier_repair_pressure,responder_probe_handoff";

    private const string CoordinatorSnapshotShadowFamilyCoverage =
        "";

    private const string CoordinatorSnapshotAuthoritativeFamilyCoverage =
        "route_runtime,route_transition,runtime_unlock_retry_lifecycle,receive_recovery,fallback_tail_yield,regular_v4_frontier_repair_pressure,responder_probe_handoff";

    private static void InitializeCoordinatorSnapshotState(
        OutboundTransferContext context)
        => context.CoordinatorPersistentState = new()
        {
            CoordinatorInstanceId = context.CoordinatorInstanceId,
        };

    private static void InitializeCoordinatorSnapshotState(
        InboundTransferContext context)
        => context.CoordinatorPersistentState = new()
        {
            CoordinatorInstanceId = context.CoordinatorInstanceId,
        };

    private static void StartCoordinatorSnapshotStreamLocked(
        OutboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        var fingerprint =
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(state);
        if (!context.CoordinatorSnapshotEmitter.Start(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            fingerprint))
        {
            return;
        }

        context.CoordinatorPersistentState =
            context.CoordinatorPersistentState with
            {
                SnapshotStateFingerprint = fingerprint,
            };
    }

    private static void StartCoordinatorSnapshotStreamLocked(
        InboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        var fingerprint =
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(state);
        if (!context.CoordinatorSnapshotEmitter.Start(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            fingerprint))
        {
            return;
        }

        context.CoordinatorPersistentState =
            context.CoordinatorPersistentState with
            {
                SnapshotStateFingerprint = fingerprint,
            };
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorState state,
        string decisionKind,
        string decisionStatus,
        string decisionReason)
    {
        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            CreateCoordinatorStateLocked(context),
            state,
            context.CoordinatorInstanceId,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            decisionKind,
            decisionStatus,
            decisionReason);
        AssertNoAuthoritativeFamilyOperations(snapshot.Envelope);
        context.CoordinatorSnapshotEmitter.ValidateEnvelope(
            snapshot.Envelope,
            snapshot.State);
        context.CoordinatorPersistentState = snapshot.State.PersistentState;
        context.CoordinatorSnapshotEmitter.Enqueue(snapshot.Envelope);
        return snapshot.State;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state,
        string decisionKind,
        string decisionStatus,
        string decisionReason)
    {
        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            CreateCoordinatorStateLocked(context),
            state,
            context.CoordinatorInstanceId,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            decisionKind,
            decisionStatus,
            decisionReason);
        AssertNoAuthoritativeFamilyOperations(snapshot.Envelope);
        context.CoordinatorSnapshotEmitter.ValidateEnvelope(
            snapshot.Envelope,
            snapshot.State);
        context.CoordinatorPersistentState = snapshot.State.PersistentState;
        context.CoordinatorSnapshotEmitter.Enqueue(snapshot.Envelope);
        return snapshot.State;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffectIfPresent(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferRouteTransitionDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffectIfPresent(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        EnqueueResponderProbeHandoffDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        RuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        EnqueueResponderProbeHandoffDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferFallbackV6ProofBootstrapDecision decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferFallbackV6ProofBootstrapDecision decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryDecision decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferReceiveRecoveryDecision decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferFallbackTailYieldTransitionDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        context.LastFallbackTailYieldSnapshotEnvelope =
            decision.SnapshotEnvelope;
        EnqueueFallbackTailYieldDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferFallbackTailYieldTransitionDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        context.LastFallbackTailYieldSnapshotEnvelope =
            decision.SnapshotEnvelope;
        EnqueueFallbackTailYieldDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        RegularV4FrontierRepairPressureBindingDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => AdoptRegularV4FrontierRepairPressureStateLocked(
                context,
                adopted),
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        RegularV4FrontierRepairPressureBindingDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => AdoptRegularV4FrontierRepairPressureStateLocked(
                context,
                adopted),
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        RegularV4FrontierRepairPressureRecoveryHandoffDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => AdoptRegularV4FrontierRepairPressureStateLocked(
                context,
                adopted),
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        RegularV4FrontierRepairPressureRecoveryHandoffDecision decision)
    {
        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            adopted => AdoptRegularV4FrontierRepairPressureStateLocked(
                context,
                adopted),
            context.CoordinatorSnapshotEmitter);
        EnqueueRegularV4PressureDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static void AdoptRegularV4FrontierRepairPressureStateLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorState state)
    {
        context.CoordinatorPersistentState = state.PersistentState;
        context.CurrentRepairReplayDecision =
            state.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration =
            state.LastRepairReplayGeneration;
    }

    private static void AdoptRegularV4FrontierRepairPressureStateLocked(
        InboundTransferContext context,
        FileTransferCoordinatorState state)
    {
        context.CoordinatorPersistentState = state.PersistentState;
        context.CurrentRepairReplayDecision =
            state.CurrentRepairReplayDecision;
        context.LastRepairReplayGeneration =
            state.LastRepairReplayGeneration;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
            decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
            decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
            decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
            decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorDecision decision)
    {
        if (decision.SnapshotEnvelope is null)
        {
            return AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorDecision",
                GetCoordinatorDecisionSnapshotStatus(decision),
                decision.MutationRejectedReason ??
                decision.RuntimeUnlockCommitRejectedReason ??
                decision.Reason);
        }

        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);
        if (decision.SnapshotEnvelope.Aggregate.Operations.Any(
                operation => operation.Family == "fallback_tail_yield"))
        {
            context.LastFallbackTailYieldSnapshotEnvelope =
                decision.SnapshotEnvelope;
            EnqueueFallbackTailYieldDecisionAuthorityEffect(
                context,
                decision.SnapshotEnvelope);
        }
        EnqueueRegularV4PressureDecisionAuthorityEffectIfPresent(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferCoordinatorDecision decision)
    {
        if (decision.SnapshotEnvelope is null)
        {
            return AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorDecision",
                GetCoordinatorDecisionSnapshotStatus(decision),
                decision.MutationRejectedReason ??
                decision.RuntimeUnlockCommitRejectedReason ??
                decision.Reason);
        }

        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);
        if (decision.SnapshotEnvelope.Aggregate.Operations.Any(
                operation => operation.Family == "fallback_tail_yield"))
        {
            context.LastFallbackTailYieldSnapshotEnvelope =
                decision.SnapshotEnvelope;
            EnqueueFallbackTailYieldDecisionAuthorityEffect(
                context,
                decision.SnapshotEnvelope);
        }
        EnqueueRegularV4PressureDecisionAuthorityEffectIfPresent(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            decision.SnapshotEnvelope);
        EnqueueResponderProbeHandoffDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);

        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferCoordinatorActionDecision decision)
    {
        if (decision.SnapshotEnvelope is null)
        {
            return AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorActionDecision",
                decision.Accepted ? "Accepted" : "Rejected",
                decision.Accepted
                    ? "legacy_family_action_accepted"
                    : decision.RejectionReason);
        }

        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);
        if (decision.SnapshotEnvelope.Aggregate.Operations.Any(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure"))
        {
            EnqueueRegularV4PressureDecisionAuthorityEffect(
                context.CoordinatorSnapshotEmitter,
                context.CoordinatorInstanceId,
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                state.PersistentState.SnapshotStateGeneration,
                decision.SnapshotEnvelope);
        }
        return state;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        InboundTransferContext context,
        FileTransferCoordinatorActionDecision decision)
    {
        if (decision.SnapshotEnvelope is null)
        {
            return AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorActionDecision",
                decision.Accepted ? "Accepted" : "Rejected",
                decision.Accepted
                    ? "legacy_family_action_accepted"
                    : decision.RejectionReason);
        }

        var state = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);
        if (decision.SnapshotEnvelope.Aggregate.Operations.Any(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure"))
        {
            EnqueueRegularV4PressureDecisionAuthorityEffect(
                context.CoordinatorSnapshotEmitter,
                context.CoordinatorInstanceId,
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                state.PersistentState.SnapshotStateGeneration,
                decision.SnapshotEnvelope);
        }
        EnqueueResponderProbeHandoffDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
        return state;
    }

    private static string GetCoordinatorDecisionSnapshotStatus(
        FileTransferCoordinatorDecision decision)
        => decision.TerminalMutationRejected ||
           !string.IsNullOrWhiteSpace(decision.MutationRejectedReason) ||
           !string.IsNullOrWhiteSpace(
               decision.RuntimeUnlockCommitRejectedReason)
            ? "Rejected"
            : "Accepted";

    private static void AssertNoAuthoritativeFamilyOperations(
        FileTransferCoordinatorTransitionEnvelope envelope)
    {
        var authoritativeOperations = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family is
                    "route_runtime" or
                    "route_transition" or
                    "runtime_unlock_retry_lifecycle" or
                    "receive_recovery" or
                    "fallback_tail_yield" or
                    "regular_v4_frontier_repair_pressure" or
                    "responder_probe_handoff")
            .Select(operation => operation.OperationKey)
            .ToArray();
        if (authoritativeOperations.Length > 0)
        {
            throw new InvalidOperationException(
                "An authoritative Coordinator decision did not provide " +
                "its Coordinator-authored snapshot envelope: " +
                string.Join(",", authoritativeOperations));
        }
    }

    private static void EnqueueResponderProbeHandoffDecisionAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope is null)
        {
            return;
        }

        var responderOperations = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "responder_probe_handoff" &&
                operation.ResponderProbeHandoff is not null)
            .ToArray();
        if (responderOperations.Length == 0)
        {
            return;
        }
        var matches = responderOperations
            .SelectMany(operation => operation.ResponderProbeHandoff!.Receipts
                .Where(receipt => receipt.ReceiptValidated)
                .Select(receipt =>
                {
                    var effectKind = operation.OperationRole ==
                        "route_transition_handoff_pending"
                        ? "route_transition_handoff_supplied"
                        : string.Equals(
                            operation.ReceiptStatus,
                            "Invalidated",
                            StringComparison.Ordinal)
                            ? "handoff_invalidated"
                        : envelope.DecisionKind switch
                        {
                            nameof(FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff) =>
                                "handoff_bound",
                            nameof(FileTransferCoordinator.HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork) =>
                                "handoff_held",
                            nameof(FileTransferCoordinator.InvalidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoff) =>
                                "handoff_invalidated",
                            _ => "convergence_observed",
                        };
                    return (
                        Operation: operation,
                        Receipt: receipt,
                        EffectKind: effectKind);
                }))
            .ToArray();
        if (matches.Length == 0)
        {
            return;
        }

        foreach (var match in matches)
        {
            context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
                context.CoordinatorInstanceId,
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                context.CoordinatorPersistentState.SnapshotStateGeneration,
                match.Operation.Family,
                match.Operation.AuthorityId,
                match.Operation.OperationGeneration,
                match.Receipt.ReceiptIdentity,
                match.Receipt.ReceiptGeneration,
                match.Receipt.ReceiptStatus,
                match.Receipt.ReceiptFingerprint,
                match.EffectKind,
                $"{match.Operation.OperationKey}:{match.EffectKind}",
                Math.Max(1, match.Receipt.ReceiptGeneration));
        }
    }

    private static void RecordResponderProbeHandoffAttemptSnapshotLocked(
        InboundTransferContext context,
        RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
        string status,
        string reason)
    {
        var snapshot = FileTransferCoordinator
            .CreateResponderProbeHandoffAttemptSnapshot(
                CreateCoordinatorStateLocked(context),
                observation,
                "ObserveRuntimeUnlockResponderPeerProbeHandoff",
                status,
                reason);
        _ = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            snapshot.State,
            snapshot.Envelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        EnqueueResponderProbeHandoffDecisionAuthorityEffect(
            context,
            snapshot.Envelope);
    }

    private static FileTransferCoordinatorState AdoptCoordinatorAuthoredEnvelopeLocked(
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferCoordinatorState state,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        Action<FileTransferCoordinatorState> adopt,
        CoordinatorSnapshotStreamEmitter emitter)
    {
        if (envelope is null ||
            !string.Equals(
                envelope.CoordinatorInstanceId,
                coordinatorInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(envelope.TransferId, transferId, StringComparison.Ordinal) ||
            !string.Equals(
                envelope.Direction,
                direction.ToString().ToLowerInvariant(),
                StringComparison.Ordinal) ||
            envelope.Aggregate.AfterStateGeneration !=
            state.PersistentState.SnapshotStateGeneration)
        {
            throw new InvalidOperationException(
                "Coordinator-authored snapshot envelope is missing or does not match its decision state.");
        }

        emitter.ValidateEnvelope(envelope, state);
        adopt(state);
        emitter.Enqueue(envelope);
        return state;
    }

    private static FileTransferCoordinatorTransitionEnvelope
        RecordReceiveRecoveryAttemptSnapshotLocked(
            OutboundTransferContext context,
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferReceiveRecoveryOperationAuthority? authority)
    {
        var snapshot = FileTransferCoordinator.CreateReceiveRecoveryAttemptSnapshot(
            state,
            decisionKind,
            decisionStatus,
            decisionReason,
            command,
            authority);
        _ = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            snapshot.State,
            snapshot.Envelope,
            adopted => context.CoordinatorPersistentState = adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        return snapshot.Envelope;
    }

    private static FileTransferCoordinatorTransitionEnvelope
        RecordReceiveRecoveryAttemptSnapshotLocked(
            InboundTransferContext context,
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferReceiveRecoveryOperationAuthority? authority)
    {
        var snapshot = FileTransferCoordinator.CreateReceiveRecoveryAttemptSnapshot(
            state,
            decisionKind,
            decisionStatus,
            decisionReason,
            command,
            authority);
        _ = AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            snapshot.State,
            snapshot.Envelope,
            adopted => context.CoordinatorPersistentState = adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);
        return snapshot.Envelope;
    }

    private static FileTransferCoordinatorState
        AdoptRuntimeUnlockRetryCoordinatorStateLocked(
            OutboundTransferContext context,
            FileTransferCoordinatorState state,
            FileTransferCoordinatorTransitionEnvelope envelope)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state,
            envelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static FileTransferCoordinatorState
        AdoptRuntimeUnlockRetryCoordinatorStateLocked(
            InboundTransferContext context,
            FileTransferCoordinatorState state,
            FileTransferCoordinatorTransitionEnvelope envelope)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state,
            envelope,
            adopted => context.CoordinatorPersistentState =
                adopted.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static void EnqueueRuntimeUnlockRetryAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string receiptKind,
        string effectKind)
        => EnqueueRuntimeUnlockRetryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            receiptKind,
            effectKind);

    private static void EnqueueRuntimeUnlockRetryAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string receiptKind,
        string effectKind)
        => EnqueueRuntimeUnlockRetryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            receiptKind,
            effectKind);

    private static void EnqueueRuntimeUnlockRetryAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string receiptKind,
        string effectKind)
    {
        if (envelope is null)
        {
            return;
        }

        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "runtime_unlock_retry_lifecycle" &&
                operation.RuntimeUnlockRetry is not null)
            .SelectMany(operation => operation.RuntimeUnlockRetry!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    string.Equals(
                        receipt.Stage,
                        receiptKind,
                        StringComparison.Ordinal))
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        var match = matches[0];
        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            match.Operation.Family,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint,
            effectKind,
            $"{match.Operation.OperationKey}:{effectKind}",
            match.Receipt.ReceiptGeneration);
    }

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string operationRole,
        string effectKind)
        => EnqueueReceiveRecoveryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            operationRole,
            effectKind);

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string operationRole,
        string effectKind)
        => EnqueueReceiveRecoveryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            operationRole,
            effectKind);

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string operationRole,
        string effectKind)
    {
        if (envelope is null)
        {
            return;
        }

        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "receive_recovery" &&
                operation.OperationRole == operationRole &&
                operation.ReceiveRecovery is not null)
            .SelectMany(operation => operation.ReceiveRecovery!.Receipts
                .Where(receipt => receipt.ReceiptValidated)
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        var match = matches[0];
        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            match.Operation.Family,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint,
            effectKind,
            $"{match.Operation.OperationKey}:{effectKind}",
            match.Receipt.ReceiptGeneration);
    }

    private static void EnqueueReceiveRecoveryDecisionAuthorityEffect(
        OutboundTransferContext context,
        FileTransferReceiveRecoveryDecision decision)
    {
        if (decision.SnapshotAuthorityEffectBinding is { } exact)
        {
            EnqueueReceiveRecoveryAuthorityEffect(
                context,
                decision.SnapshotEnvelope,
                exact);
            return;
        }

        EnqueueReceiveRecoveryDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
    }

    private static void EnqueueReceiveRecoveryDecisionAuthorityEffect(
        InboundTransferContext context,
        FileTransferReceiveRecoveryDecision decision)
    {
        if (decision.SnapshotAuthorityEffectBinding is { } exact)
        {
            EnqueueReceiveRecoveryAuthorityEffect(
                context,
                decision.SnapshotEnvelope,
                exact);
            return;
        }

        EnqueueReceiveRecoveryDecisionAuthorityEffect(
            context,
            decision.SnapshotEnvelope);
    }

    private static void EnqueueReceiveRecoveryDecisionAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        var effect = GetReceiveRecoveryDecisionEffect(envelope);
        if (effect is { } exact)
        {
            EnqueueReceiveRecoveryAuthorityEffect(
                context,
                envelope,
                exact.OperationRole,
                exact.EffectKind);
        }
    }

    private static void EnqueueReceiveRecoveryDecisionAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        var effect = GetReceiveRecoveryDecisionEffect(envelope);
        if (effect is { } exact)
        {
            EnqueueReceiveRecoveryAuthorityEffect(
                context,
                envelope,
                exact.OperationRole,
                exact.EffectKind);
        }
    }

    private static void EnqueueReceiveRecoveryAttemptAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string effectKind)
    {
        if (envelope is null)
        {
            return;
        }

        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "receive_recovery" &&
                operation.ReceiveRecovery is not null)
            .SelectMany(operation => operation.ReceiveRecovery!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == "attempted_decision")
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        var match = matches[0];
        context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            match.Operation.Family,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint,
            effectKind,
            $"{match.Operation.OperationKey}:{effectKind}",
            match.Receipt.ReceiptGeneration);
    }

    private static (string OperationRole, string EffectKind)?
        GetReceiveRecoveryDecisionEffect(
            FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope is null)
        {
            return null;
        }

        return envelope.DecisionKind switch
        {
            nameof(FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart) =>
                ("operation_current", "execution_started"),
            nameof(FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt) =>
                ("operation_current", "execution_receipt_observed"),
            nameof(FileTransferCoordinator.ApplyReceiveRecoveryFallbackCheckpointDeliveryReceipt) =>
                ("checkpoint_delivery_terminal", "checkpoint_delivery_observed"),
            nameof(FileTransferCoordinator.ReserveReceiveRecoveryFallbackCheckpointDeliveryResume) =>
                ("checkpoint_resume", "checkpoint_resume_observed"),
            nameof(FileTransferCoordinator.ApplyReceiveRecoveryFallbackCheckpointFrontierRequestRecheckReceipt) =>
                ("checkpoint_frontier_recheck", "checkpoint_frontier_recheck_observed"),
            nameof(FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold) or
            nameof(FileTransferCoordinator.InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold) =>
                (envelope.Aggregate.Operations.Any(operation =>
                    operation.Family == "receive_recovery" &&
                    operation.OperationRole == "initial_activation_hold_current" &&
                    operation.ReceiveRecovery?.Receipts.Any(receipt =>
                        receipt.ReceiptValidated) == true)
                    ? "initial_activation_hold_current"
                    : "initial_activation_hold_terminal",
                 "initial_activation_hold_observed"),
            nameof(FileTransferCoordinator.CommitRuntimeUnlockInitialActivationOfferEnvelopeRefresh) =>
                ("initial_activation_envelope_refresh", "initial_activation_envelope_refresh_observed"),
            _ => null,
        };
    }

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        FileTransferCoordinatorAuthorityEffectBinding binding)
        => EnqueueReceiveRecoveryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            binding);

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        FileTransferCoordinatorAuthorityEffectBinding binding)
        => EnqueueReceiveRecoveryAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope,
            binding);

    private static void EnqueueReceiveRecoveryAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        FileTransferCoordinatorAuthorityEffectBinding binding)
    {
        if (envelope is null)
        {
            return;
        }

        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == binding.Family &&
                operation.OperationKey == binding.OperationKey &&
                operation.OperationKind == binding.OperationKind &&
                operation.OperationRole == binding.OperationRole &&
                operation.AuthorityId == binding.AuthorityId &&
                operation.OperationGeneration == binding.OperationGeneration &&
                operation.ReceiveRecovery is not null)
            .SelectMany(operation => operation.ReceiveRecovery!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == binding.ReceiptStage &&
                    receipt.CommandIdentity == binding.CommandIdentity &&
                    receipt.CommandGeneration == binding.CommandGeneration &&
                    receipt.CommandFingerprint == binding.CommandFingerprint &&
                    receipt.ReceiptIdentity == binding.ReceiptIdentity &&
                    receipt.ReceiptGeneration == binding.ReceiptGeneration &&
                    receipt.ReceiptStatus == binding.ReceiptStatus &&
                    receipt.ReceiptFingerprint == binding.ReceiptFingerprint)
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        var match = matches[0];
        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            match.Operation.Family,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint,
            binding.EffectKind,
            $"{match.Operation.OperationKey}:{binding.EffectKind}",
            match.Receipt.ReceiptGeneration);
    }

    private static void EnqueueRegularV4PressureDecisionAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope is null)
        {
            return;
        }
        if (string.Equals(
                envelope.DecisionStatus,
                "Rejected",
                StringComparison.Ordinal))
        {
            return;
        }

        var hasTerminalSettlement = envelope.Aggregate.Operations.Any(
            operation => operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.OperationRole == "pressure_settlement_terminal" &&
                operation.ReceiptValidated);
        var effectKinds = envelope.DecisionKind switch
        {
            nameof(FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding) =>
                new[] { "pressure_install_staged" },
            nameof(FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding) =>
                new[]
                {
                    "pressure_install_bridge_result",
                    "repair_contract_bound",
                },
            nameof(FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement) =>
                new[] { "pressure_settlement_prepared" },
            nameof(FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement) =>
                new[]
                {
                    "pressure_settlement_bridge_applied",
                    "local_contract_settled",
                    "local_contract_reference_cleared",
                },
            nameof(FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff) =>
                new[] { "recovery_handoff_prepared" },
            nameof(FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff) =>
                new[]
                {
                    "pressure_settlement_bridge_applied",
                    "local_contract_settled",
                    "local_contract_reference_cleared",
                    "recovery_handoff_committed",
                    "projection_detached",
                },
            nameof(FileTransferCoordinator.CommitFileTransferRouteTransition)
                when hasTerminalSettlement =>
                new[]
                {
                    "pressure_settlement_bridge_applied",
                    "local_contract_settled",
                    "local_contract_reference_cleared",
                    "route_commit_local_lifecycle_adopted",
                },
            _ => new[] { "regular_v4_pressure_decision_adopted" },
        };
        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.RegularV4FrontierRepairPressure is not null)
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt => receipt.ReceiptValidated)
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .OrderByDescending(match =>
                match.Receipt.Stage == "attempted_decision" ? 0 : 1)
            .ToArray();
        if (matches.Length == 0)
        {
            return;
        }

        var match = matches[0];
        foreach (var effectKind in effectKinds)
        {
            emitter.EnqueueAuthorityEffect(
                coordinatorInstanceId,
                sessionId,
                transferId,
                direction,
                stateGeneration,
                match.Operation.Family,
                match.Operation.AuthorityId,
                match.Operation.OperationGeneration,
                match.Receipt.ReceiptIdentity,
                match.Receipt.ReceiptGeneration,
                match.Receipt.ReceiptStatus,
                match.Receipt.ReceiptFingerprint,
                effectKind,
                $"{match.Operation.OperationKey}:{effectKind}",
                Math.Max(1, match.Receipt.ReceiptGeneration));
        }
    }

    private static void
        EnqueueRegularV4PressureDecisionAuthorityEffectIfPresent(
            CoordinatorSnapshotStreamEmitter emitter,
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            long stateGeneration,
            FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope?.Aggregate.Operations.Any(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure") != true)
        {
            return;
        }

        EnqueueRegularV4PressureDecisionAuthorityEffect(
            emitter,
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            envelope);
    }

    private static void EnqueueFallbackTailYieldDecisionAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
        => EnqueueFallbackTailYieldDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope);

    private static void EnqueueFallbackTailYieldDecisionAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
        => EnqueueFallbackTailYieldDecisionAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope);

    private static void EnqueueFallbackTailYieldDecisionAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope is null)
        {
            return;
        }

        var effects = envelope.DecisionKind switch
        {
            nameof(FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt) =>
                new[]
                {
                    (Stage: "local_proof", Kind: "local_proof_sent"),
                    (Stage: "local_proof", Kind: "local_proof_applied"),
                },
            nameof(FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt) =>
                new[]
                {
                    (Stage: "peer_proof", Kind: "peer_proof_observed"),
                },
            nameof(FileTransferCoordinator.CarryForwardFallbackTailPairedReleaseToTransportEpoch) =>
                new[]
                {
                    (Stage: "carry_forward", Kind: "transport_epoch_reauthorization_handoff"),
                },
            nameof(FileTransferCoordinator.InvalidateFallbackTailYieldTransportEpochReauthorization) =>
                new[]
                {
                    (Stage: "transport_reauthorization", Kind: "transport_epoch_reauthorization_handoff"),
                },
            nameof(FileTransferCoordinator.BindFallbackTailYieldRetryDispatchHold) or
            nameof(FileTransferCoordinator.RebindFallbackTailYieldRetryDispatchHoldToRepairSuccessor) =>
                new[]
                {
                    (Stage: "RetryDispatchHold", Kind: "retry_dispatch_hold_observed"),
                },
            nameof(FileTransferCoordinator.ApplyFallbackTailYieldRetryDispatchRecheckReceipt) =>
                new[]
                {
                    (Stage: "RetryDispatchRecheck", Kind: "retry_dispatch_recheck_observed"),
                },
            _ => Array.Empty<(string Stage, string Kind)>(),
        };
        foreach (var effect in effects)
        {
            EnqueueFallbackTailYieldAuthorityEffect(
                emitter,
                coordinatorInstanceId,
                sessionId,
                transferId,
                direction,
                stateGeneration,
                envelope,
                effect.Stage,
                effect.Kind);
        }
    }

    private static void EnqueueFallbackTailYieldAuthorityEffect(
        OutboundTransferContext context,
        string stage,
        string kind)
        => EnqueueFallbackTailYieldAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            context.LastFallbackTailYieldSnapshotEnvelope,
            stage,
            kind);

    private static void EnqueueFallbackTailYieldAuthorityEffect(
        InboundTransferContext context,
        string stage,
        string kind)
        => EnqueueFallbackTailYieldAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            context.LastFallbackTailYieldSnapshotEnvelope,
            stage,
            kind);

    private static void EnqueueFallbackTailYieldAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        string stage,
        string kind)
    {
        if (envelope is null)
        {
            return;
        }

        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "fallback_tail_yield" &&
                operation.FallbackTailYield is not null)
            .SelectMany(operation => operation.FallbackTailYield!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == stage)
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        var match = matches[0];
        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            match.Operation.Family,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint,
            kind,
            $"{match.Operation.OperationKey}:{kind}",
            match.Receipt.ReceiptGeneration);
    }

    private static void EnqueueRoutePublicationAuthorityEffect(
        OutboundTransferContext context,
        RoutePublicationToken publication)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_runtime",
            $"publication:{publication.PublicationGeneration}",
            publication.PublicationGeneration,
            $"publication:{publication.PublicationGeneration}",
            publication.PublicationGeneration,
            "Accepted",
            FileTransferCoordinator.CalculateSnapshotFingerprint(publication),
            effectKind: "route_publication_forwarded",
            effectIdentity: $"publication:{publication.PublicationGeneration}",
            effectGeneration: publication.PublicationGeneration);

    private static void EnqueueRouteRuntimeCommitAuthorityEffect(
        OutboundTransferContext context,
        RouteCommitToken commit,
        string effectKind,
        string effectIdentity)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_runtime",
            $"commit:{commit.CommitGeneration}",
            commit.CommitGeneration,
            $"commit:{commit.CommitGeneration}",
            commit.CommitGeneration,
            "Committed",
            FileTransferCoordinator.CalculateSnapshotFingerprint(commit),
            effectKind,
            effectIdentity,
            commit.CommitGeneration);

    private static void EnqueueRouteRuntimeCommitAuthorityEffect(
        InboundTransferContext context,
        RouteCommitToken commit,
        string effectKind,
        string effectIdentity)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_runtime",
            $"commit:{commit.CommitGeneration}",
            commit.CommitGeneration,
            $"commit:{commit.CommitGeneration}",
            commit.CommitGeneration,
            "Committed",
            FileTransferCoordinator.CalculateSnapshotFingerprint(commit),
            effectKind,
            effectIdentity,
            commit.CommitGeneration);

    private static void EnqueueRoutePublicationAuthorityEffect(
        InboundTransferContext context,
        RoutePublicationToken publication)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_runtime",
            $"publication:{publication.PublicationGeneration}",
            publication.PublicationGeneration,
            $"publication:{publication.PublicationGeneration}",
            publication.PublicationGeneration,
            "Accepted",
            FileTransferCoordinator.CalculateSnapshotFingerprint(publication),
            effectKind: "route_publication_forwarded",
            effectIdentity: $"publication:{publication.PublicationGeneration}",
            effectGeneration: publication.PublicationGeneration);

    private static void EnqueueRouteTransitionAuthorityEffect(
        OutboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        string effectKind,
        string effectIdentity,
        string receiptStatus,
        object receipt,
        string? receiptIdentity = null,
        long receiptGeneration = 0)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_transition",
            authority.AuthorityId.ToString(),
            authority.TransitionGeneration,
            receiptIdentity ?? effectIdentity,
            receiptGeneration > 0
                ? receiptGeneration
                : authority.TransitionGeneration,
            receiptStatus,
            FileTransferCoordinator.CalculateSnapshotFingerprint(receipt),
            effectKind,
            effectIdentity,
            authority.TransitionGeneration);

    private static void EnqueueRouteTransitionAuthorityEffect(
        InboundTransferContext context,
        FileTransferRouteTransitionAuthority authority,
        string effectKind,
        string effectIdentity,
        string receiptStatus,
        object receipt,
        string? receiptIdentity = null,
        long receiptGeneration = 0)
        => context.CoordinatorSnapshotEmitter.EnqueueAuthorityEffect(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            "route_transition",
            authority.AuthorityId.ToString(),
            authority.TransitionGeneration,
            receiptIdentity ?? effectIdentity,
            receiptGeneration > 0
                ? receiptGeneration
                : authority.TransitionGeneration,
            receiptStatus,
            FileTransferCoordinator.CalculateSnapshotFingerprint(receipt),
            effectKind,
            effectIdentity,
            authority.TransitionGeneration);

    private static FileTransferRouteTransitionOperation?
        CaptureDrainingRouteTransitionForRejectedTerminalEffect(
            FileTransferCoordinatorDecision decision,
            FileTransferCoordinatorPersistentState persistentState)
        => string.Equals(
               decision.MutationRejectedReason,
               "filetransfer_route_transition_invalidation_required",
               StringComparison.Ordinal) &&
           persistentState.FileTransferRouteTransition.CurrentOperation is
           {
               State: FileTransferRouteTransitionState.Draining,
               PauseBindReceipt: not null,
           } operation
            ? operation
            : null;

    private static FileTransferRouteTransitionOperation
        AssertCommittedRouteTransitionOperation(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority)
    {
        var transitionState =
            state.PersistentState.FileTransferRouteTransition;
        if (transitionState.CurrentOperation is { } current &&
            current.Authority == authority &&
            current.State == FileTransferRouteTransitionState.Committed)
        {
            return current;
        }
        if (transitionState.LastTerminalOperation is { } terminal &&
            terminal.Authority == authority &&
            terminal.State == FileTransferRouteTransitionState.Committed)
        {
            return terminal;
        }

        throw new InvalidOperationException(
            "The exact committed route-transition operation is missing from the Coordinator decision state.");
    }

    private static void EnqueueRejectedTerminalWhileDrainingEffect(
        OutboundTransferContext context,
        FileTransferRouteTransitionOperation? operation)
    {
        if (operation is not { PauseBindReceipt: { } receipt } exact)
        {
            return;
        }

        EnqueueRouteTransitionAuthorityEffect(
            context,
            exact.Authority,
            "terminalized_while_draining",
            $"{exact.Authority.AuthorityId}:terminalized",
            receipt.Status.ToString(),
            receipt,
            $"{exact.Authority.AuthorityId}:pause_bind",
            exact.Authority.TransitionGeneration);
    }

    private static void EnqueueRejectedTerminalWhileDrainingEffect(
        InboundTransferContext context,
        FileTransferRouteTransitionOperation? operation)
    {
        if (operation is not { PauseBindReceipt: { } receipt } exact)
        {
            return;
        }

        EnqueueRouteTransitionAuthorityEffect(
            context,
            exact.Authority,
            "terminalized_while_draining",
            $"{exact.Authority.AuthorityId}:terminalized",
            receipt.Status.ToString(),
            receipt,
            $"{exact.Authority.AuthorityId}:pause_bind",
            exact.Authority.TransitionGeneration);
    }

    internal static FileTransferCoordinatorTransitionEnvelope
        CreateCoordinatorSnapshotEvidenceForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope)
        => CoordinatorSnapshotStreamEmitter.CreateEvidenceEnvelope(envelope);

    private sealed class CoordinatorSnapshotStreamEmitter
    {
        private readonly ConcurrentQueue<string> pending = new();
        private readonly object stateGate = new();
        private int drainScheduled;
        private bool started;
        private bool completed;
        private string coordinatorInstanceId = "(none)";
        private string sessionId = "(none)";
        private string transferId = "(none)";
        private string direction = "(none)";
        private long transitionCount;
        private long finalGeneration;
        private string finalHash = "(none)";
        private readonly Dictionary<string, AuthorityEffectBinding>
            authorityEffectBindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> authorityEffectMessages =
            new(StringComparer.Ordinal);

        internal bool Start(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            long initialGeneration,
            string initialHash)
        {
            bool didStart;
            lock (stateGate)
            {
                didStart = StartLocked(
                    coordinatorInstanceId,
                    sessionId,
                    transferId,
                    direction.ToString().ToLowerInvariant(),
                    initialGeneration,
                    initialHash);
            }

            if (didStart)
            {
                ScheduleDrain();
            }
            return didStart;
        }

        internal void ValidateEnvelope(
            FileTransferCoordinatorTransitionEnvelope envelope,
            FileTransferCoordinatorState state)
        {
            lock (stateGate)
            {
                ValidateEnvelopeTailLocked(envelope);
                if (envelope.Aggregate.AfterStateGeneration !=
                        state.PersistentState.SnapshotStateGeneration ||
                    !string.Equals(
                        envelope.Aggregate.AfterStateFingerprint,
                        state.PersistentState.SnapshotStateFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Coordinator snapshot envelope does not match its returned decision state.");
                }
            }
        }

        internal void Enqueue(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            lock (stateGate)
            {
                if (completed)
                {
                    return;
                }

                if (!started)
                {
                    StartLocked(
                        envelope.CoordinatorInstanceId,
                        envelope.SessionId,
                        envelope.TransferId,
                        envelope.Direction,
                        envelope.Aggregate.BeforeStateGeneration,
                        envelope.Aggregate.BeforeStateFingerprint);
                }

                ValidateEnvelopeTailLocked(envelope);

                var evidenceEnvelope = CreateEvidenceEnvelope(envelope);
                var payloadHash = evidenceEnvelope.CalculatePayloadSha256();
                pending.Enqueue(
                    "event=filetransfer_coordinator_transition_snapshot; " +
                    $"snapshot_contract_version={evidenceEnvelope.SnapshotContractVersion}; " +
                    $"snapshot_selection_mode={evidenceEnvelope.SnapshotSelectionMode}; " +
                    $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                    $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                    $"coordinator_instance_id={evidenceEnvelope.CoordinatorInstanceId}; " +
                    $"session_id={evidenceEnvelope.SessionId}; transfer_id={evidenceEnvelope.TransferId}; " +
                    $"direction={evidenceEnvelope.Direction}; transition_id={evidenceEnvelope.TransitionId}; " +
                    $"before_state_generation={evidenceEnvelope.Aggregate.BeforeStateGeneration}; " +
                    $"after_state_generation={evidenceEnvelope.Aggregate.AfterStateGeneration}; " +
                    $"snapshot_sha256={payloadHash}; snapshot_payload={evidenceEnvelope.ToBase64UrlPayload()}; authority_effect=none");
                transitionCount++;
                finalGeneration = envelope.Aggregate.AfterStateGeneration;
                finalHash = EvidenceToken(envelope.Aggregate.AfterStateFingerprint);
                IndexAuthorityEffectBindingsLocked(envelope, evidenceEnvelope);

            }

            ScheduleDrain();
        }

        private bool StartLocked(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            string direction,
            long initialGeneration,
            string initialHash)
        {
            if (started || completed)
            {
                return false;
            }

            started = true;
            this.coordinatorInstanceId = coordinatorInstanceId;
            this.sessionId = sessionId;
            this.transferId = transferId;
            this.direction = direction;
            finalGeneration = initialGeneration;
            finalHash = EvidenceToken(initialHash);
            pending.Enqueue(
                "event=filetransfer_coordinator_snapshot_stream_started; " +
                $"snapshot_contract_version={FileTransferCoordinatorTransitionEnvelope.CurrentContractVersion}; " +
                "snapshot_selection_mode=SnapshotV1Authoritative; " +
                $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                $"coordinator_instance_id={EvidenceToken(coordinatorInstanceId)}; " +
                $"session_id={EvidenceToken(sessionId)}; transfer_id={EvidenceToken(transferId)}; " +
                $"direction={direction}; initial_state_generation={initialGeneration}; " +
                $"initial_state_hash={EvidenceToken(initialHash)}; " +
                $"emitted_family_coverage={CoordinatorSnapshotEmittedFamilyCoverage}; " +
                $"shadow_family_coverage={CoordinatorSnapshotShadowFamilyCoverage}; " +
                $"authoritative_family_coverage={CoordinatorSnapshotAuthoritativeFamilyCoverage}; authority_effect=none");
            return true;
        }

        private void ValidateEnvelopeTailLocked(
            FileTransferCoordinatorTransitionEnvelope envelope)
        {
            if (!started || completed ||
                !string.Equals(
                    envelope.CoordinatorInstanceId,
                    coordinatorInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(envelope.TransferId, transferId, StringComparison.Ordinal) ||
                !string.Equals(envelope.Direction, direction, StringComparison.Ordinal) ||
                envelope.Aggregate.BeforeStateGeneration != finalGeneration ||
                !string.Equals(
                    EvidenceToken(envelope.Aggregate.BeforeStateFingerprint),
                    finalHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Coordinator snapshot envelope is not contiguous with the emitted stream tail.");
            }
        }

        internal void Complete(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            FileTransferDirection direction)
        {
            lock (stateGate)
            {
                if (completed || !started)
                {
                    return;
                }

                completed = true;
                pending.Enqueue(
                    "event=filetransfer_coordinator_snapshot_stream_completed; " +
                    $"snapshot_contract_version={FileTransferCoordinatorTransitionEnvelope.CurrentContractVersion}; " +
                    "snapshot_selection_mode=SnapshotV1Authoritative; " +
                    $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                    $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                    $"coordinator_instance_id={EvidenceToken(coordinatorInstanceId)}; " +
                    $"session_id={EvidenceToken(sessionId)}; transfer_id={EvidenceToken(transferId)}; " +
                    $"direction={direction.ToString().ToLowerInvariant()}; final_state_generation={finalGeneration}; " +
                    $"final_state_hash={finalHash}; transition_count={transitionCount}; " +
                    $"emitted_family_coverage={CoordinatorSnapshotEmittedFamilyCoverage}; " +
                    $"shadow_family_coverage={CoordinatorSnapshotShadowFamilyCoverage}; " +
                    $"authoritative_family_coverage={CoordinatorSnapshotAuthoritativeFamilyCoverage}; authority_effect=none");
            }

            ScheduleDrain();
        }

        internal void EnqueueAuthorityEffect(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            long stateGeneration,
            string family,
            string authorityId,
            long operationGeneration,
            string receiptIdentity,
            long receiptGeneration,
            string receiptStatus,
            string receiptFingerprint,
            string effectKind = "authority_effect",
            string? effectIdentity = null,
            long effectGeneration = 0)
        {
            lock (stateGate)
            {
                if (completed)
                {
                    return;
                }

                string transitionId;
                string operationKey;
                FileTransferCoordinatorStateBoundarySnapshot? scope;
                long boundStateGeneration;
                var bindingKey = CreateAuthorityEffectBindingKey(
                    family,
                    authorityId,
                    operationGeneration,
                    receiptIdentity,
                    receiptGeneration,
                    receiptStatus,
                    receiptFingerprint);
                if (authorityEffectBindings.TryGetValue(
                        bindingKey,
                        out var binding))
                {
                    transitionId = binding.TransitionId;
                    operationKey = binding.OperationKey;
                    scope = binding.Scope;
                    boundStateGeneration = binding.StateGeneration;
                }
                else
                {
                    transitionId = "(none)";
                    operationKey = "(none)";
                    scope = null;
                    boundStateGeneration = stateGeneration;
                }

                var message =
                    "event=filetransfer_coordinator_authority_effect; " +
                $"snapshot_contract_version={FileTransferCoordinatorTransitionEnvelope.CurrentContractVersion}; " +
                "snapshot_selection_mode=SnapshotV1Authoritative; " +
                $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                $"coordinator_instance_id={EvidenceToken(coordinatorInstanceId)}; session_id={EvidenceToken(sessionId)}; " +
                $"transfer_id={EvidenceToken(transferId)}; direction={direction.ToString().ToLowerInvariant()}; " +
                $"transition_id={transitionId}; coordinator_state_generation={boundStateGeneration}; " +
                $"operation_family={family}; operation_key={operationKey}; " +
                $"operation_authority_id={EvidenceToken(authorityId)}; operation_generation={operationGeneration}; " +
                $"effect_kind={effectKind}; effect_identity={EvidenceToken(effectIdentity ?? receiptIdentity)}; " +
                $"effect_generation={(effectGeneration > 0 ? effectGeneration : receiptGeneration)}; " +
                $"receipt_identity={EvidenceToken(receiptIdentity)}; receipt_generation={receiptGeneration}; " +
                $"receipt_status={receiptStatus}; receipt_fingerprint={EvidenceToken(receiptFingerprint)}; " +
                $"route={scope?.Route ?? "(none)"}; protocol_version={scope?.ProtocolVersion ?? 0}; " +
                $"live_route_epoch={scope?.LiveRouteEpoch ?? 0}; " +
                $"route_publication_generation={scope?.RoutePublicationGeneration ?? 0}; " +
                $"route_commit_generation={scope?.RouteCommitGeneration ?? 0}; " +
                $"runtime_generation={scope?.RuntimeGeneration ?? 0}; terminal_generation={scope?.TerminalGeneration ?? 0}; " +
                $"leg_generation={scope?.LegGeneration ?? 0}; transport_epoch={scope?.TransportEpoch ?? 0}; " +
                $"bridge_recovery_generation={scope?.BridgeRecoveryGeneration ?? 0}; " +
                $"checkpoint_generation={scope?.CheckpointGeneration ?? 0}; " +
                $"checkpoint_request_id={EvidenceToken(scope?.CheckpointRequestId ?? "(none)")}; " +
                $"data_session_identity={EvidenceToken(scope?.DataSessionIdentity ?? "(none)")}; " +
                $"data_session_generation={scope?.DataSessionGeneration ?? 0}; authority_effect=applied";
                if (!authorityEffectMessages.Add(message))
                {
                    return;
                }
                pending.Enqueue(message);
            }
            ScheduleDrain();
        }

        private void IndexAuthorityEffectBindingsLocked(
            FileTransferCoordinatorTransitionEnvelope envelope,
            FileTransferCoordinatorTransitionEnvelope evidenceEnvelope)
        {
            for (var operationIndex = 0;
                 operationIndex < envelope.Aggregate.Operations.Length;
                 operationIndex++)
            {
                var operation = envelope.Aggregate.Operations[operationIndex];
                var evidenceOperation =
                    evidenceEnvelope.Aggregate.Operations[operationIndex];
                var scope = operation.RouteRuntime?.Boundary ??
                            operation.RouteTransition?.SourceBoundary ??
                            operation.RuntimeUnlockRetry?.SourceBoundary ??
                            operation.ReceiveRecovery?.SourceBoundary ??
                            operation.FallbackTailYield?.SourceBoundary ??
                            operation.RegularV4FrontierRepairPressure
                                ?.SourceBoundary ??
                            operation.ResponderProbeHandoff?.SourceBoundary;
                if (operation.ReceiptValidated)
                {
                    IndexAuthorityEffectBindingLocked(
                        envelope,
                        operation,
                        operation.ReceiptIdentity,
                        operation.ReceiptGeneration,
                        operation.ReceiptStatus,
                        operation.ReceiptFingerprint,
                        scope,
                        evidenceEnvelope.TransitionId,
                        evidenceOperation.OperationKey);
                }

                var typedReceipts = operation.RouteTransition?.Receipts ??
                                    operation.RuntimeUnlockRetry?.Receipts ??
                                    operation.ReceiveRecovery?.Receipts ??
                                    operation.FallbackTailYield?.Receipts ??
                                    operation.RegularV4FrontierRepairPressure
                                        ?.Receipts ??
                                    operation.ResponderProbeHandoff?.Receipts ??
                                    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                                        .Empty;
                foreach (var receipt in typedReceipts)
                {
                    if (!receipt.ReceiptValidated)
                    {
                        continue;
                    }
                    IndexAuthorityEffectBindingLocked(
                        envelope,
                        operation,
                        receipt.ReceiptIdentity,
                        receipt.ReceiptGeneration,
                        receipt.ReceiptStatus,
                        receipt.ReceiptFingerprint,
                        scope,
                        evidenceEnvelope.TransitionId,
                        evidenceOperation.OperationKey);
                }
            }
        }

        private void IndexAuthorityEffectBindingLocked(
            FileTransferCoordinatorTransitionEnvelope envelope,
            FileTransferCoordinatorOperationTransitionSnapshot operation,
            string receiptIdentity,
            long receiptGeneration,
            string receiptStatus,
            string receiptFingerprint,
            FileTransferCoordinatorStateBoundarySnapshot? scope,
            string evidenceTransitionId,
            string evidenceOperationKey)
        {
            var key = CreateAuthorityEffectBindingKey(
                operation.Family,
                operation.AuthorityId,
                operation.OperationGeneration,
                receiptIdentity,
                receiptGeneration,
                receiptStatus,
                receiptFingerprint);
            authorityEffectBindings[key] = new(
                evidenceTransitionId,
                envelope.Aggregate.AfterStateGeneration,
                evidenceOperationKey,
                scope);
        }

        private static string CreateAuthorityEffectBindingKey(
            string family,
            string authorityId,
            long operationGeneration,
            string receiptIdentity,
            long receiptGeneration,
            string receiptStatus,
            string receiptFingerprint)
            => string.Join(
                "|",
                family,
                authorityId,
                operationGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                receiptIdentity,
                receiptGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                receiptStatus,
                receiptFingerprint);

        private sealed record AuthorityEffectBinding(
            string TransitionId,
            long StateGeneration,
            string OperationKey,
            FileTransferCoordinatorStateBoundarySnapshot? Scope);

        internal static FileTransferCoordinatorTransitionEnvelope
            CreateEvidenceEnvelope(
                FileTransferCoordinatorTransitionEnvelope envelope)
            => envelope with
            {
                CoordinatorInstanceId = EvidenceToken(envelope.CoordinatorInstanceId),
                SessionId = EvidenceToken(envelope.SessionId),
                TransferId = EvidenceToken(envelope.TransferId),
                TransitionId = EvidenceToken(envelope.TransitionId),
                Aggregate = envelope.Aggregate with
                {
                    BeforeStateFingerprint = EvidenceToken(
                        envelope.Aggregate.BeforeStateFingerprint),
                    AfterStateFingerprint = EvidenceToken(
                        envelope.Aggregate.AfterStateFingerprint),
                    BeforeBoundary = CreateEvidenceBoundary(
                        envelope.Aggregate.BeforeBoundary),
                    AfterBoundary = CreateEvidenceBoundary(
                        envelope.Aggregate.AfterBoundary),
                    Operations = envelope.Aggregate.Operations
                        .Select(CreateEvidenceOperation)
                        .ToImmutableArray(),
                },
            };

        private static FileTransferCoordinatorOperationTransitionSnapshot
            CreateEvidenceOperation(
                FileTransferCoordinatorOperationTransitionSnapshot operation)
        {
            var authorityId = EvidenceToken(operation.AuthorityId);
            return operation with
            {
                OperationKey = CreateEvidenceOperationKey(
                    operation.Family,
                    operation.OperationRole,
                    authorityId,
                    operation.OperationGeneration,
                    operation.OperationKind),
                AuthorityId = authorityId,
                PredecessorAuthorityId = EvidenceToken(
                    operation.PredecessorAuthorityId),
                CommandIdentity = EvidenceToken(operation.CommandIdentity),
                CommandFingerprint = EvidenceToken(operation.CommandFingerprint),
                ReceiptIdentity = EvidenceToken(operation.ReceiptIdentity),
                ReceiptFingerprint = EvidenceToken(operation.ReceiptFingerprint),
                RouteRuntime = operation.RouteRuntime is { } runtime
                    ? runtime with
                    {
                        Boundary = CreateEvidenceBoundary(runtime.Boundary),
                        TokenFingerprint = EvidenceToken(runtime.TokenFingerprint),
                    }
                    : null,
                RouteTransition = operation.RouteTransition is { } transition
                    ? transition with
                    {
                        SourceBoundary = CreateEvidenceBoundary(
                            transition.SourceBoundary),
                        ProofBoundaryFingerprint = EvidenceToken(
                            transition.ProofBoundaryFingerprint),
                        DurableCheckpointFingerprint = EvidenceToken(
                            transition.DurableCheckpointFingerprint),
                        RetainedFrameFingerprint = EvidenceToken(
                            transition.RetainedFrameFingerprint),
                        Receipts = transition.Receipts
                            .Select(CreateEvidenceReceipt)
                            .ToImmutableArray(),
                    }
                    : null,
                RuntimeUnlockRetry = operation.RuntimeUnlockRetry is { } retry
                    ? retry with
                    {
                        SourceBoundary = CreateEvidenceBoundary(
                            retry.SourceBoundary),
                        OfferNonceFingerprint = EvidenceToken(
                            retry.OfferNonceFingerprint),
                        ReleaseAuthorityId = EvidenceToken(
                            retry.ReleaseAuthorityId),
                        SuccessorAuthorityId = EvidenceToken(
                            retry.SuccessorAuthorityId),
                        Receipts = retry.Receipts
                            .Select(CreateEvidenceReceipt)
                            .ToImmutableArray(),
                    }
                    : null,
                ReceiveRecovery = operation.ReceiveRecovery is { } recovery
                    ? recovery with
                    {
                        SourceBoundary = CreateEvidenceBoundary(
                            recovery.SourceBoundary),
                        EffectiveBoundary = CreateEvidenceBoundary(
                            recovery.EffectiveBoundary),
                        ProofIdentity = EvidenceToken(recovery.ProofIdentity),
                        HeldRetryAuthorityId = EvidenceToken(
                            recovery.HeldRetryAuthorityId),
                        ReservedRetryAuthorityId = EvidenceToken(
                            recovery.ReservedRetryAuthorityId),
                        Receipts = recovery.Receipts
                            .Select(CreateEvidenceReceipt)
                            .ToImmutableArray(),
                    }
                    : null,
                FallbackTailYield = operation.FallbackTailYield is { } yield
                    ? yield with
                    {
                        SourceBoundary = CreateEvidenceBoundary(
                            yield.SourceBoundary),
                        EffectiveBoundary = CreateEvidenceBoundary(
                            yield.EffectiveBoundary),
                        TargetBoundary = CreateEvidenceBoundary(
                            yield.TargetBoundary),
                        OwnerKey = EvidenceToken(yield.OwnerKey),
                        PairKey = EvidenceToken(yield.PairKey),
                        LocalProofIdentity = EvidenceToken(
                            yield.LocalProofIdentity),
                        LocalProofFingerprint = EvidenceToken(
                            yield.LocalProofFingerprint),
                        PeerProofIdentity = EvidenceToken(
                            yield.PeerProofIdentity),
                        PeerProofFingerprint = EvidenceToken(
                            yield.PeerProofFingerprint),
                        PairedReleaseAuthorityId = EvidenceToken(
                            yield.PairedReleaseAuthorityId),
                        FailedPredecessorAuthorityId = EvidenceToken(
                            yield.FailedPredecessorAuthorityId),
                        Receipts = yield.Receipts
                            .Select(CreateEvidenceReceipt)
                            .ToImmutableArray(),
                    }
                    : null,
                RegularV4FrontierRepairPressure =
                    operation.RegularV4FrontierRepairPressure is
                        { } pressure
                        ? pressure with
                        {
                            SourceBoundary = CreateEvidenceBoundary(
                                pressure.SourceBoundary),
                            EffectiveBoundary = CreateEvidenceBoundary(
                                pressure.EffectiveBoundary),
                            RepairKey = EvidenceToken(pressure.RepairKey),
                            SuccessorAuthorityId = EvidenceToken(
                                pressure.SuccessorAuthorityId),
                            RecoveryAuthorityId = EvidenceToken(
                                pressure.RecoveryAuthorityId),
                            Receipts = pressure.Receipts
                                .Select(CreateEvidenceReceipt)
                                .ToImmutableArray(),
                        }
                        : null,
                ResponderProbeHandoff =
                    operation.ResponderProbeHandoff is { } responder
                        ? responder with
                        {
                            SourceBoundary = CreateEvidenceBoundary(
                                responder.SourceBoundary),
                            EffectiveBoundary = CreateEvidenceBoundary(
                                responder.EffectiveBoundary),
                            TargetBoundary = CreateEvidenceBoundary(
                                responder.TargetBoundary),
                            TunaPathLeaseListenerRunId = EvidenceToken(
                                responder.TunaPathLeaseListenerRunId),
                            AckMessageId = EvidenceToken(
                                responder.AckMessageId),
                            AckSourceFingerprint = EvidenceToken(
                                responder.AckSourceFingerprint),
                            PeerProbeFrameFingerprint = EvidenceToken(
                                responder.PeerProbeFrameFingerprint),
                            ProbeId = EvidenceToken(responder.ProbeId),
                            OwnerKey = EvidenceToken(responder.OwnerKey),
                            PairKey = EvidenceToken(responder.PairKey),
                            LocalProofIdentity = EvidenceToken(
                                responder.LocalProofIdentity),
                            LocalProofFingerprint = EvidenceToken(
                                responder.LocalProofFingerprint),
                            PeerProofIdentity = EvidenceToken(
                                responder.PeerProofIdentity),
                            PeerProofFingerprint = EvidenceToken(
                                responder.PeerProofFingerprint),
                            PairedReleaseAuthorityId = EvidenceToken(
                                responder.PairedReleaseAuthorityId),
                            HandoffAuthorityId = EvidenceToken(
                                responder.HandoffAuthorityId),
                            LinkedTransitionAuthorityId = EvidenceToken(
                                responder.LinkedTransitionAuthorityId),
                            Receipts = responder.Receipts
                                .Select(CreateEvidenceReceipt)
                                .ToImmutableArray(),
                        }
                        : null,
            };
        }

        private static string CreateEvidenceOperationKey(
            string family,
            string role,
            string authorityId,
            long operationGeneration,
            string operationKind)
            => string.Join(
                "|",
                family,
                role,
                authorityId,
                operationGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                operationKind);

        private static FileTransferCoordinatorReceiptTransitionSnapshot
            CreateEvidenceReceipt(
                FileTransferCoordinatorReceiptTransitionSnapshot receipt)
            => receipt with
            {
                CommandIdentity = EvidenceToken(receipt.CommandIdentity),
                CommandFingerprint = EvidenceToken(receipt.CommandFingerprint),
                ReceiptIdentity = EvidenceToken(receipt.ReceiptIdentity),
                ReceiptFingerprint = EvidenceToken(receipt.ReceiptFingerprint),
            };

        private static FileTransferCoordinatorStateBoundarySnapshot
            CreateEvidenceBoundary(
                FileTransferCoordinatorStateBoundarySnapshot boundary)
            => boundary with
            {
                CheckpointRequestId = EvidenceToken(boundary.CheckpointRequestId),
                DataSessionIdentity = EvidenceToken(boundary.DataSessionIdentity),
            };

        private static string EvidenceToken(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "(none)"
                : value;
            if (string.Equals(normalized, "(none)", StringComparison.Ordinal))
            {
                return normalized;
            }
            return "fte1-" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant()[..24];
        }

        private void ScheduleDrain()
        {
            if (Interlocked.CompareExchange(ref drainScheduled, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                static emitter => emitter.Drain(),
                this,
                preferLocal: false);
        }

        private void Drain()
        {
            while (true)
            {
                while (pending.TryDequeue(out var message))
                {
                    LocalOperationalLog.Info("FileTransferCoordinator", message);
                }

                Interlocked.Exchange(ref drainScheduled, 0);
                if (pending.IsEmpty ||
                    Interlocked.CompareExchange(ref drainScheduled, 1, 0) != 0)
                {
                    return;
                }
            }
        }
    }
}
