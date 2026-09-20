using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const string CoordinatorSnapshotCatalogVersion =
        "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1";

    private const string CoordinatorSnapshotCatalogHash =
        "6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00";

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
        emitter.BeforeStateAdoption(state);
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

    private static void EnqueueRuntimeUnlockRetryInstallAuthorityEffect(
        OutboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
        => EnqueueRuntimeUnlockRetryInstallAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope);

    private static void EnqueueRuntimeUnlockRetryInstallAuthorityEffect(
        InboundTransferContext context,
        FileTransferCoordinatorTransitionEnvelope? envelope)
        => EnqueueRuntimeUnlockRetryInstallAuthorityEffect(
            context.CoordinatorSnapshotEmitter,
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.CoordinatorPersistentState.SnapshotStateGeneration,
            envelope);

    private static void EnqueueRuntimeUnlockRetryInstallAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter,
        string coordinatorInstanceId,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long stateGeneration,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (envelope is null ||
            CreateRuntimeUnlockRetryInstallAuthorityEffectBinding(envelope)
                is not { } binding)
        {
            return;
        }

        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            stateGeneration,
            binding.Family,
            binding.AuthorityId,
            binding.OperationGeneration,
            binding.ReceiptIdentity,
            binding.ReceiptGeneration,
            binding.ReceiptStatus,
            binding.ReceiptFingerprint,
            binding.EffectKind,
            $"{binding.OperationKey}:{binding.EffectKind}",
            binding.ReceiptGeneration,
            binding.OperationKey);
    }

    private static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRuntimeUnlockRetryInstallAuthorityEffectBinding(
            FileTransferCoordinatorTransitionEnvelope envelope)
    {
        if (envelope.DecisionKind != nameof(
                FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall) ||
            envelope.DecisionStatus != "Accepted" ||
            envelope.Aggregate.AfterStateGeneration !=
                envelope.Aggregate.BeforeStateGeneration + 1 ||
            envelope.Aggregate.AfterStateFingerprint ==
                envelope.Aggregate.BeforeStateFingerprint)
        {
            return null;
        }

        var attempts = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "runtime_unlock_retry_lifecycle" &&
                operation.OperationKind == "RetryAttempt" &&
                operation.RuntimeUnlockRetry is not null)
            .ToArray();
        var pendingMatches = attempts
            .Where(operation =>
                operation.OperationRole == "attempt_pending" &&
                operation.BeforeState == "Staged" &&
                operation.AfterState == "None" &&
                operation.StateApplied)
            .SelectMany(operation => operation.RuntimeUnlockRetry!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == "install" &&
                    receipt.ReceiptStatus == "Staged" &&
                    ReceiptMatchesOperation(operation, receipt))
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        var installedMatches = attempts
            .Where(operation =>
                operation.OperationRole == "attempt_installed" &&
                operation.BeforeState == "None" &&
                operation.AfterState == "Installed" &&
                operation.StateApplied)
            .SelectMany(operation => operation.RuntimeUnlockRetry!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == "install" &&
                    receipt.ReceiptStatus == "Installed" &&
                    ReceiptMatchesOperation(operation, receipt))
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (pendingMatches.Length != 1 || installedMatches.Length != 1)
        {
            return null;
        }

        var pending = pendingMatches[0];
        var installed = installedMatches[0];
        if (pending.Operation.AuthorityId != installed.Operation.AuthorityId ||
            pending.Operation.OperationGeneration !=
                installed.Operation.OperationGeneration ||
            pending.Operation.RuntimeUnlockRetry!.AuthorityGeneration !=
                installed.Operation.RuntimeUnlockRetry!.AuthorityGeneration ||
            pending.Operation.RuntimeUnlockRetry.DispatchAttemptGeneration !=
                installed.Operation.RuntimeUnlockRetry.DispatchAttemptGeneration ||
            pending.Operation.RuntimeUnlockRetry.SourceBoundary !=
                installed.Operation.RuntimeUnlockRetry.SourceBoundary ||
            pending.Receipt.CommandIdentity !=
                installed.Receipt.CommandIdentity ||
            pending.Receipt.CommandGeneration !=
                installed.Receipt.CommandGeneration ||
            pending.Receipt.CommandFingerprint !=
                installed.Receipt.CommandFingerprint ||
            pending.Receipt.ReceiptIdentity !=
                installed.Receipt.ReceiptIdentity ||
            pending.Receipt.ReceiptGeneration !=
                installed.Receipt.ReceiptGeneration ||
            pending.Receipt.ReceiptFingerprint !=
                installed.Receipt.ReceiptFingerprint)
        {
            return null;
        }

        return CreateAuthorityEffectBinding(
            "install_committed",
            installed.Operation,
            installed.Receipt);
    }

    internal static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRuntimeUnlockRetryInstallAuthorityEffectBindingForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope)
        => CreateRuntimeUnlockRetryInstallAuthorityEffectBinding(envelope);

    private static void EnqueueRuntimeUnlockRetrySupersededSettlementAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter, string coordinatorInstanceId,
        string sessionId, string transferId, FileTransferDirection direction, long stateGeneration,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        if (CreateRuntimeUnlockRetrySupersededSettlementAuthorityEffectBinding(decision) is not { } binding)
        {
            return;
        }
        emitter.EnqueueAuthorityEffect(coordinatorInstanceId, sessionId, transferId, direction, stateGeneration,
            binding.Family, binding.AuthorityId, binding.OperationGeneration, binding.ReceiptIdentity,
            binding.ReceiptGeneration, binding.ReceiptStatus, binding.ReceiptFingerprint, binding.EffectKind,
            $"{binding.OperationKey}:{binding.EffectKind}", binding.ReceiptGeneration, binding.OperationKey);
    }

    private static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRuntimeUnlockRetrySupersededSettlementAuthorityEffectBinding(RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        if (decision.Status != RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted ||
            decision.SnapshotEnvelope is not { } envelope || decision.State is not { } state)
        {
            return null;
        }
        var report = decision.Report;
        var lifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        if (decision.Reason != "retry_dispatch_settlement_superseded_predecessor" || report.IsDefault ||
            report.Outcome != RuntimeUnlockRetryDispatchSettlementOutcome.Superseded || report.DispatchObserved ||
            report.Reason != "coordinator_successor_installed" || report.OfferGeneration <= 0 ||
            string.IsNullOrWhiteSpace(report.OfferNonce) || lifecycle.RetainedPredecessor is not null ||
            lifecycle.LastTerminalAttempt is not { State: RuntimeUnlockRetryLifecycleAttemptState.Settled } retired ||
            retired.Authority != report.Authority || retired.Settlement != report ||
            lifecycle.InstalledAttempt is not { } successor || !successor.IsInstalled ||
            successor.Authority.PredecessorAuthorityId != report.Authority.AuthorityId ||
            successor.Authority.AuthorityId != report.SuccessorAuthorityId ||
            envelope.DecisionKind != nameof(FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement) ||
            envelope.DecisionStatus != "Accepted" || envelope.DecisionReason != decision.Reason ||
            envelope.SessionId != report.Authority.SessionId || envelope.TransferId != report.Authority.TransferId ||
            envelope.Direction != report.Authority.Direction.ToString().ToLowerInvariant() ||
            envelope.CoordinatorInstanceId != state.PersistentState.CoordinatorInstanceId ||
            envelope.Aggregate.AfterStateGeneration != envelope.Aggregate.BeforeStateGeneration + 1 ||
            envelope.Aggregate.AfterStateGeneration != state.PersistentState.SnapshotStateGeneration ||
            envelope.Aggregate.AfterStateFingerprint != state.PersistentState.SnapshotStateFingerprint ||
            envelope.Aggregate.BeforeStateFingerprint == envelope.Aggregate.AfterStateFingerprint ||
            envelope.Aggregate.BeforeBoundary != envelope.Aggregate.AfterBoundary ||
            envelope.Aggregate.Operations.Length != 2)
        {
            return null;
        }
        var retainedMatches = envelope.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "attempt_retained_predecessor" && operation.BeforeState == "Settled" && operation.AfterState == "None").ToArray();
        var terminalMatches = envelope.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "attempt_terminal" && operation.BeforeState == "None" && operation.AfterState == "Settled").ToArray();
        if (retainedMatches.Length != 1 || terminalMatches.Length != 1) { return null; }
        var retained = retainedMatches[0];
        var terminal = terminalMatches[0];
        var authority = report.Authority;
        var authorityId = authority.AuthorityId.ToString();
        var expectedSource = new FileTransferCoordinatorStateBoundarySnapshot(authority.RouteToken,
            authority.ProtocolVersion, authority.LiveRouteEpoch, authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration ?? 0, authority.RuntimeGeneration, authority.TerminalGeneration,
            authority.LegGeneration, authority.TransportEpoch, authority.BridgeRecoveryGeneration,
            authority.CheckpointGeneration, authority.CheckpointRequestId, authority.DataSessionIdentity, authority.DataSessionGeneration);
        foreach (var operation in new[] { retained, terminal })
        {
            if (operation.Family != "runtime_unlock_retry_lifecycle" || operation.OperationKind != "RetryAttempt" ||
                !operation.StateApplied || operation.AuthorityId != authorityId ||
                operation.OperationGeneration != authority.AuthorityGeneration ||
                operation.OperationKey != $"runtime_unlock_retry_lifecycle|{operation.OperationRole}|{authorityId}|{authority.AuthorityGeneration}|RetryAttempt" ||
                operation.RuntimeUnlockRetry is not { } payload || payload.Role != operation.OperationRole ||
                payload.AuthorityGeneration != authority.AuthorityGeneration || payload.SourceBoundary != expectedSource ||
                payload.SuccessorAuthorityId != successor.Authority.AuthorityId.ToString() ||
                payload.OfferGeneration != report.OfferGeneration || payload.DispatchObserved)
            {
                return null;
            }
        }
        var retainedPayload = retained.RuntimeUnlockRetry!;
        var terminalPayload = terminal.RuntimeUnlockRetry!;
        if (!retainedPayload.Receipts.SequenceEqual(terminalPayload.Receipts) ||
            (retainedPayload with { Role = terminalPayload.Role, Receipts = terminalPayload.Receipts }) != terminalPayload ||
            (retained with { OperationRole = terminal.OperationRole, OperationKey = terminal.OperationKey,
                BeforeState = terminal.BeforeState, AfterState = terminal.AfterState, RuntimeUnlockRetry = terminalPayload }) != terminal)
        {
            return null;
        }
        var receipts = terminalPayload.Receipts.Where(receipt => receipt.Stage == "settlement").ToArray();
        if (receipts.Length != 1) { return null; }
        var settlement = receipts[0];
        if (!settlement.ReceiptValidated || settlement.ReceiptStatus != "Superseded" || settlement.ReceiptReason != report.Reason ||
            settlement.CommandIdentity != $"{authorityId}:settlement" || settlement.ReceiptIdentity != settlement.CommandIdentity ||
            settlement.CommandGeneration != report.OfferGeneration || settlement.ReceiptGeneration != report.OfferGeneration ||
            string.IsNullOrWhiteSpace(settlement.CommandFingerprint) || settlement.CommandFingerprint == "(none)" ||
            string.IsNullOrWhiteSpace(settlement.ReceiptFingerprint) || settlement.ReceiptFingerprint == "(none)" ||
            !ReceiptMatchesOperation(retained, settlement) || !ReceiptMatchesOperation(terminal, settlement))
        {
            return null;
        }
        return CreateAuthorityEffectBinding("settlement_observed", terminal, settlement);
    }

    private static bool TryEnqueueRuntimeUnlockRetrySuccessorSettlementAuthorityEffect(
        CoordinatorSnapshotStreamEmitter emitter, string coordinatorInstanceId,
        string sessionId, string transferId, FileTransferDirection direction, long stateGeneration,
        RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        var lifecycle = decision.State?.PersistentState.RuntimeUnlockRetryLifecycle;
        if (lifecycle?.LastTerminalAttempt is not { } completed ||
            completed.Settlement?.Outcome != RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved || lifecycle.AttemptHistory.IsDefault ||
            !lifecycle.AttemptHistory.Any(attempt =>
                attempt.Authority.AuthorityId == completed.Authority.PredecessorAuthorityId &&
                attempt.Settlement?.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded &&
                attempt.SuccessorAuthorityId == completed.Authority.AuthorityId)) { return false; }
        // A failed exact binding must not fall back to the generic receipt selector.
        if (CreateRuntimeUnlockRetrySuccessorSettlementAuthorityEffectBinding(decision) is { } binding)
        {
            emitter.EnqueueAuthorityEffect(coordinatorInstanceId, sessionId, transferId, direction, stateGeneration,
                binding.Family, binding.AuthorityId, binding.OperationGeneration, binding.ReceiptIdentity,
                binding.ReceiptGeneration, binding.ReceiptStatus, binding.ReceiptFingerprint, binding.EffectKind,
                $"{binding.OperationKey}:{binding.EffectKind}", binding.ReceiptGeneration, binding.OperationKey);
        }
        return true;
    }

    private static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRuntimeUnlockRetrySuccessorSettlementAuthorityEffectBinding(RuntimeUnlockRetryDispatchSettlementDecision decision)
    {
        if (decision.Status != RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted ||
            decision.Reason != "retry_dispatch_settlement_accepted" || decision.SnapshotEnvelope is not { } envelope ||
            decision.State is not { } state) { return null; }
        var report = decision.Report;
        var lifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        if (report.IsDefault || report.Outcome != RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved ||
            !report.DispatchObserved || report.OfferGeneration <= 0 || string.IsNullOrWhiteSpace(report.OfferNonce) ||
            lifecycle.InstalledAttempt is not null || lifecycle.RetainedPredecessor is not null || lifecycle.PendingAttempt is not null ||
            lifecycle.LastTerminalAttempt is not { State: RuntimeUnlockRetryLifecycleAttemptState.Settled } completed ||
            completed.Authority != report.Authority || completed.Settlement != report || !completed.DispatchObserved ||
            completed.OfferGeneration != report.OfferGeneration || completed.OfferNonce != report.OfferNonce || lifecycle.LastSettlement != report ||
            envelope.DecisionKind != nameof(FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement) ||
            envelope.DecisionStatus != "Accepted" || envelope.DecisionReason != decision.Reason ||
            envelope.SessionId != report.Authority.SessionId || envelope.TransferId != report.Authority.TransferId ||
            envelope.Direction != report.Authority.Direction.ToString().ToLowerInvariant() ||
            envelope.CoordinatorInstanceId != state.PersistentState.CoordinatorInstanceId ||
            envelope.Aggregate.AfterStateGeneration != envelope.Aggregate.BeforeStateGeneration + 1 ||
            envelope.Aggregate.AfterStateGeneration != state.PersistentState.SnapshotStateGeneration ||
            envelope.Aggregate.AfterStateFingerprint != state.PersistentState.SnapshotStateFingerprint ||
            envelope.Aggregate.BeforeStateFingerprint == envelope.Aggregate.AfterStateFingerprint ||
            envelope.Aggregate.BeforeBoundary != envelope.Aggregate.AfterBoundary || envelope.Aggregate.Operations.Length != 3 ||
            lifecycle.AttemptHistory.IsDefault) { return null; }
        var predecessors = lifecycle.AttemptHistory.Where(attempt =>
            attempt.Authority.AuthorityId == completed.Authority.PredecessorAuthorityId).ToArray();
        if (predecessors.Length != 1) { return null; }
        var predecessor = predecessors[0];
        if (predecessor.State != RuntimeUnlockRetryLifecycleAttemptState.Settled || predecessor.DispatchObserved ||
            predecessor.Settlement is not { Outcome: RuntimeUnlockRetryDispatchSettlementOutcome.Superseded } retired ||
            retired.Authority != predecessor.Authority || retired.Reason != "coordinator_successor_installed" || retired.DispatchObserved ||
            retired.SuccessorAuthorityId != completed.Authority.AuthorityId || predecessor.SuccessorAuthorityId != retired.SuccessorAuthorityId ||
            predecessor.OfferGeneration <= 0 || retired.OfferGeneration != predecessor.OfferGeneration || retired.OfferNonce != predecessor.OfferNonce)
        { return null; }
        var operations = envelope.Aggregate.Operations;
        var entering = operations.Where(operation => operation.OperationRole == "attempt_terminal" &&
            operation.BeforeState == "None" && operation.AfterState == "Settled").ToArray();
        var removed = operations.Where(operation => operation.OperationRole == "attempt_terminal" &&
            operation.BeforeState == "Settled" && operation.AfterState == "None").ToArray();
        var installed = operations.Where(operation => operation.OperationRole == "attempt_installed" &&
            operation.BeforeState == "SendInFlight" && operation.AfterState == "None").ToArray();
        if (entering.Length != 1 || removed.Length != 1 || installed.Length != 1 ||
            !Matches(entering[0], completed, RuntimeUnlockRetryLifecycleAttemptState.Settled, true, report) ||
            !Matches(removed[0], predecessor, RuntimeUnlockRetryLifecycleAttemptState.Settled, false, retired) ||
            !Matches(installed[0], completed, RuntimeUnlockRetryLifecycleAttemptState.SendInFlight, false, null)) { return null; }
        return CreateAuthorityEffectBinding("settlement_observed", entering[0], entering[0].RuntimeUnlockRetry!.Receipts[^1]);

        // Validate only the small receipt operands already used by the Coordinator
        // projection. Never expand or re-fingerprint the aggregate state graph.
        static bool Matches(FileTransferCoordinatorOperationTransitionSnapshot operation,
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt, RuntimeUnlockRetryLifecycleAttemptState phase,
            bool dispatchObserved, RuntimeUnlockRetryDispatchSettlementReport? settlement)
        {
            var authority = attempt.Authority;
            var authorityId = authority.AuthorityId.ToString();
            if (operation.Family != "runtime_unlock_retry_lifecycle" || operation.OperationKind != "RetryAttempt" ||
                !operation.StateApplied || !operation.ReceiptValidated || operation.AuthorityId != authorityId ||
                operation.PredecessorAuthorityId != (authority.PredecessorAuthorityId?.ToString() ?? "(none)") ||
                operation.OperationGeneration != authority.AuthorityGeneration ||
                operation.OperationKey != $"runtime_unlock_retry_lifecycle|{operation.OperationRole}|{authorityId}|{authority.AuthorityGeneration}|RetryAttempt" ||
                operation.RuntimeUnlockRetry is not { } payload || attempt.StageStatus is not { } stageStatus ||
                attempt.InstallReceipt is not { IsDefault: false } install || install.Authority != authority || install.Request != attempt.Request ||
                attempt.OfferGeneration <= 0 || string.IsNullOrWhiteSpace(attempt.OfferNonce)) { return false; }
            var source = new FileTransferCoordinatorStateBoundarySnapshot(authority.RouteToken,
                authority.ProtocolVersion, authority.LiveRouteEpoch, authority.RoutePublicationGeneration,
                authority.RouteCommitGeneration ?? 0, authority.RuntimeGeneration, authority.TerminalGeneration,
                authority.LegGeneration, authority.TransportEpoch, authority.BridgeRecoveryGeneration,
                authority.CheckpointGeneration, authority.CheckpointRequestId, authority.DataSessionIdentity, authority.DataSessionGeneration);
            var expected = new FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot(operation.OperationRole, source,
                authority.AuthorityGeneration, authority.RecoveryContractGeneration, authority.DispatchAttemptGeneration,
                authority.MissingDispatchSuccessorGeneration, authority.RetiredOfferGeneration, attempt.OfferGeneration,
                Hash(attempt.OfferNonce), attempt.DispatchDeadlineUtcMs, dispatchObserved, authority.ReleaseKind.ToString(),
                authority.ReleaseAuthorityId?.ToString() ?? "(none)", authority.ReleaseGeneration, authority.ReleaseOutcome,
                authority.ListenerRearmRequirement.ToString(), authority.ListenerExecutionMode.ToString(),
                attempt.Request.RequestActivePredecessorSupersession, attempt.SuccessorAuthorityId?.ToString() ?? "(none)", payload.Receipts);
            if (payload != expected) { return false; }
            var receipts = new List<FileTransferCoordinatorReceiptTransitionSnapshot>
            {
                new("stage", $"{authorityId}:stage", authority.DispatchAttemptGeneration, Hash(attempt.Request),
                    $"{authorityId}:stage", authority.DispatchAttemptGeneration, stageStatus.ToString(), attempt.StageDisposition.ToString(),
                    Hash(new { stageStatus, attempt.StageDisposition, attempt.StagedUtcMs, attempt.StageExpiresUtcMs }),
                    stageStatus is RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent),
                new("install", $"{authorityId}:install", install.InstallGeneration, Hash(install.Request),
                    $"{authorityId}:install", install.InstallGeneration, phase.ToString(), "retry_dispatch_install_receipt", Hash(install), true),
                new("execution", $"{authorityId}:execution", attempt.OfferGeneration, Hash(attempt.InstallReceipt),
                    $"{authorityId}:offer:{attempt.OfferGeneration}", attempt.OfferGeneration, phase.ToString(),
                    dispatchObserved ? "dispatch_observed" : "offer_bound",
                    Hash(new { attempt.OfferGeneration, attempt.OfferNonce, DispatchObserved = dispatchObserved, State = phase }), true),
            };
            if (settlement is { } report)
            {
                receipts.Add(new("settlement", $"{authorityId}:settlement", attempt.OfferGeneration,
                    Hash(new { report.Authority, report.OfferGeneration, report.OfferNonce }), $"{authorityId}:settlement",
                    attempt.OfferGeneration, report.Outcome.ToString(), report.Reason, Hash(report), true));
            }
            var last = receipts[^1];
            return payload.Receipts.SequenceEqual(receipts) && ReceiptMatchesOperation(operation, last) &&
                operation.CommandIdentity == last.CommandIdentity && operation.CommandGeneration == last.CommandGeneration &&
                operation.CommandFingerprint == last.CommandFingerprint;
        }
        static string Hash(object? value) => FileTransferCoordinator.CalculateSnapshotFingerprint(value);
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

        if (envelope.DecisionKind == nameof(
                FileTransferCoordinator.CommitFileTransferRouteTransition))
        {
            foreach (var binding in
                CreateRegularV4PressureRouteCommitAuthorityEffectBindings(
                    envelope))
            {
                emitter.EnqueueAuthorityEffect(
                    coordinatorInstanceId,
                    sessionId,
                    transferId,
                    direction,
                    stateGeneration,
                    binding.Family,
                    binding.AuthorityId,
                    binding.OperationGeneration,
                    binding.ReceiptIdentity,
                    binding.ReceiptGeneration,
                    binding.ReceiptStatus,
                    binding.ReceiptFingerprint,
                    binding.EffectKind,
                    $"{binding.OperationKey}:{binding.EffectKind}",
                    Math.Max(1, binding.ReceiptGeneration),
                    binding.OperationKey);
            }

            return;
        }

        if (envelope.DecisionKind == nameof(
                FileTransferCoordinator
                    .CommitRegularV4FrontierRepairPressureRecoveryHandoff))
        {
            foreach (var binding in
                CreateRegularV4PressureRecoveryHandoffAuthorityEffectBindings(
                    envelope))
            {
                emitter.EnqueueAuthorityEffect(
                    coordinatorInstanceId,
                    sessionId,
                    transferId,
                    direction,
                    stateGeneration,
                    binding.Family,
                    binding.AuthorityId,
                    binding.OperationGeneration,
                    binding.ReceiptIdentity,
                    binding.ReceiptGeneration,
                    binding.ReceiptStatus,
                    binding.ReceiptFingerprint,
                    binding.EffectKind,
                    $"{binding.OperationKey}:{binding.EffectKind}",
                    Math.Max(1, binding.ReceiptGeneration),
                    binding.OperationKey);
            }

            return;
        }

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
        string? expectedOperationKey = null;
        if (envelope.DecisionKind == nameof(
                FileTransferCoordinator
                    .CommitRegularV4FrontierRepairPressureSettlement))
        {
            var exactBinding =
                CreateRegularV4PressureSettlementAuthorityEffectBinding(
                    envelope,
                    effectKinds[0]);
            if (exactBinding is null)
            {
                return;
            }

            matches = matches.Where(match =>
                    match.Operation.OperationKey ==
                        exactBinding.OperationKey &&
                    match.Operation.OperationKind ==
                        exactBinding.OperationKind &&
                    match.Operation.OperationRole ==
                        exactBinding.OperationRole &&
                    match.Operation.AuthorityId == exactBinding.AuthorityId &&
                    match.Operation.OperationGeneration ==
                        exactBinding.OperationGeneration &&
                    match.Receipt.Stage == exactBinding.ReceiptStage &&
                    match.Receipt.CommandIdentity ==
                        exactBinding.CommandIdentity &&
                    match.Receipt.CommandGeneration ==
                        exactBinding.CommandGeneration &&
                    match.Receipt.CommandFingerprint ==
                        exactBinding.CommandFingerprint &&
                    match.Receipt.ReceiptIdentity ==
                        exactBinding.ReceiptIdentity &&
                    match.Receipt.ReceiptGeneration ==
                        exactBinding.ReceiptGeneration &&
                    match.Receipt.ReceiptStatus ==
                        exactBinding.ReceiptStatus &&
                    match.Receipt.ReceiptFingerprint ==
                        exactBinding.ReceiptFingerprint)
                .ToArray();
            expectedOperationKey = exactBinding.OperationKey;
        }
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
                Math.Max(1, match.Receipt.ReceiptGeneration),
                expectedOperationKey);
        }
    }

    private static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRegularV4PressureSettlementAuthorityEffectBinding(
            FileTransferCoordinatorTransitionEnvelope envelope,
            string effectKind)
    {
        if (envelope.DecisionKind != nameof(
                FileTransferCoordinator
                    .CommitRegularV4FrontierRepairPressureSettlement) ||
            envelope.DecisionStatus is not ("Accepted" or "Idempotent"))
        {
            return null;
        }

        var accepted = envelope.DecisionStatus == "Accepted";
        var matches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.OperationRole == (accepted
                    ? "pressure_settlement_terminal"
                    : "attempted_decision") &&
                operation.OperationKind == (accepted
                    ? "PressureSettlement"
                    : nameof(FileTransferCoordinator
                        .CommitRegularV4FrontierRepairPressureSettlement)) &&
                (!accepted || operation.AfterState == "Applied") &&
                operation.RegularV4FrontierRepairPressure is not null)
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt =>
                        receipt.ReceiptValidated &&
                        receipt.Stage == (accepted
                            ? "pressure_settlement"
                            : "attempted_decision") &&
                        receipt.ReceiptStatus == (accepted
                            ? "Applied"
                            : "Idempotent"))
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var match = matches[0];
        return new FileTransferCoordinatorAuthorityEffectBinding(
            effectKind,
            match.Operation.Family,
            match.Operation.OperationKey,
            match.Operation.OperationKind,
            match.Operation.OperationRole,
            match.Operation.AuthorityId,
            match.Operation.OperationGeneration,
            match.Receipt.Stage,
            match.Receipt.CommandIdentity,
            match.Receipt.CommandGeneration,
            match.Receipt.CommandFingerprint,
            match.Receipt.ReceiptIdentity,
            match.Receipt.ReceiptGeneration,
            match.Receipt.ReceiptStatus,
            match.Receipt.ReceiptFingerprint);
    }

    internal static FileTransferCoordinatorAuthorityEffectBinding?
        CreateRegularV4PressureSettlementAuthorityEffectBindingForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope)
        => CreateRegularV4PressureSettlementAuthorityEffectBinding(
            envelope,
            "pressure_settlement_bridge_applied");

    private static ImmutableArray<
        FileTransferCoordinatorAuthorityEffectBinding>
        CreateRegularV4PressureRecoveryHandoffAuthorityEffectBindings(
            FileTransferCoordinatorTransitionEnvelope envelope)
    {
        if (envelope.DecisionKind != nameof(
                FileTransferCoordinator
                    .CommitRegularV4FrontierRepairPressureRecoveryHandoff) ||
            envelope.DecisionStatus != "Committed")
        {
            return [];
        }

        var familyOperations = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.RegularV4FrontierRepairPressure is not null)
            .ToArray();
        var settlementMatches = familyOperations
            .Where(operation =>
                operation.OperationRole == "pressure_settlement_terminal" &&
                operation.OperationKind == "PressureSettlement" &&
                operation.BeforeState == "None" &&
                operation.AfterState == "Applied" &&
                operation.StateApplied &&
                operation.ReceiptValidated &&
                operation.ReceiptStatus == "Applied")
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt =>
                        receipt.ReceiptValidated &&
                        receipt.Stage == "pressure_settlement" &&
                        ReceiptMatchesOperation(operation, receipt))
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (settlementMatches.Length != 1)
        {
            return [];
        }

        var settlementMatch = settlementMatches[0];
        var settlement = settlementMatch.Operation;
        var settlementPayload = settlement.RegularV4FrontierRepairPressure!;
        var handoffMatches = familyOperations
            .Where(operation =>
                operation.OperationRole == "recovery_handoff_terminal" &&
                operation.OperationKind == "PressureRecoveryHandoff" &&
                operation.BeforeState == "None" &&
                operation.AfterState == "Committed" &&
                operation.StateApplied &&
                operation.ReceiptValidated &&
                operation.AuthorityId == settlement.AuthorityId &&
                operation.ReceiptIdentity == settlement.ReceiptIdentity &&
                operation.ReceiptGeneration == settlement.ReceiptGeneration &&
                operation.ReceiptStatus == settlement.ReceiptStatus &&
                operation.ReceiptFingerprint == settlement.ReceiptFingerprint)
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt =>
                        receipt.ReceiptValidated &&
                        receipt.Stage == "pressure_settlement" &&
                        ReceiptMatchesOperation(operation, receipt))
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (handoffMatches.Length != 1)
        {
            return [];
        }

        var handoffMatch = handoffMatches[0];
        var handoff = handoffMatch.Operation;
        var handoffPayload = handoff.RegularV4FrontierRepairPressure!;
        var installedRetirements = familyOperations.Where(operation =>
                operation.OperationRole == "pressure_binding_installed" &&
                operation.BeforeState == "Installed" &&
                operation.AfterState == "None" &&
                operation.StateApplied &&
                operation.AuthorityId == settlement.AuthorityId &&
                operation.OperationGeneration ==
                    settlement.OperationGeneration)
            .ToArray();
        var authorityRetirements = familyOperations.Where(operation =>
                operation.OperationRole == "repair_authority_current" &&
                operation.BeforeState == "Authorized" &&
                operation.AfterState == "None" &&
                operation.StateApplied &&
                operation.AuthorityId == settlement.AuthorityId &&
                operation.OperationGeneration ==
                    settlement.OperationGeneration)
            .ToArray();
        var settlementMirrors = familyOperations.Where(operation =>
                operation.OperationRole == "repair_authority_terminal" &&
                operation.OperationKind == "PressureSettlement" &&
                operation.BeforeState == "None" &&
                operation.AfterState == "Applied" &&
                operation.StateApplied &&
                operation.ReceiptValidated &&
                operation.AuthorityId == settlement.AuthorityId &&
                operation.OperationGeneration ==
                    settlement.OperationGeneration &&
                operation.ReceiptIdentity == settlement.ReceiptIdentity &&
                operation.ReceiptGeneration == settlement.ReceiptGeneration &&
                operation.ReceiptStatus == settlement.ReceiptStatus &&
                operation.ReceiptFingerprint == settlement.ReceiptFingerprint)
            .ToArray();
        var attempts = familyOperations.Where(operation =>
                operation.OperationRole == "attempted_decision" &&
                operation.OperationKind == nameof(
                    FileTransferCoordinator
                        .CommitRegularV4FrontierRepairPressureRecoveryHandoff) &&
                !operation.StateApplied &&
                operation.ReceiptValidated &&
                operation.ReceiptStatus == "Committed" &&
                operation.AuthorityId == settlement.AuthorityId &&
                operation.OperationGeneration ==
                    settlement.OperationGeneration)
            .ToArray();
        var recoveryOperations = envelope.Aggregate.Operations.Where(operation =>
                operation.Family == "receive_recovery" &&
                operation.OperationRole == "operation_current" &&
                operation.BeforeState == "None" &&
                operation.AfterState.StartsWith(
                    "Issued",
                    StringComparison.Ordinal) &&
                operation.StateApplied &&
                operation.ReceiveRecovery is not null &&
                operation.AuthorityId == handoffPayload.RecoveryAuthorityId &&
                operation.OperationGeneration ==
                    handoffPayload.RecoveryOperationGeneration &&
                operation.OperationGeneration == handoff.OperationGeneration &&
                operation.OperationKind == handoffPayload.RecoveryKind)
            .ToArray();

        if (installedRetirements.Length != 1 ||
            authorityRetirements.Length != 1 ||
            settlementMirrors.Length != 1 ||
            attempts.Length != 1 ||
            recoveryOperations.Length != 1 ||
            settlementPayload.CurrentAuthorityCleared != true ||
            settlementPayload.RetainedPredecessorCleared ||
            handoffPayload.CurrentAuthorityCleared != true ||
            handoffPayload.RetainedPredecessorCleared ||
            handoffPayload.DecisionGeneration !=
                settlementPayload.DecisionGeneration ||
            handoffPayload.PressureInstallGeneration !=
                settlementPayload.PressureInstallGeneration ||
            handoffPayload.SourceBoundary != settlementPayload.SourceBoundary ||
            handoffPayload.EffectiveBoundary !=
                settlementPayload.EffectiveBoundary ||
            recoveryOperations[0].ReceiveRecovery!.SourceBoundary !=
                settlementPayload.SourceBoundary)
        {
            return [];
        }

        return
        [
            CreateAuthorityEffectBinding(
                "pressure_settlement_bridge_applied",
                settlementMatch.Operation,
                settlementMatch.Receipt),
            CreateAuthorityEffectBinding(
                "local_contract_settled",
                settlementMatch.Operation,
                settlementMatch.Receipt),
            CreateAuthorityEffectBinding(
                "local_contract_reference_cleared",
                settlementMatch.Operation,
                settlementMatch.Receipt),
            CreateAuthorityEffectBinding(
                "recovery_handoff_committed",
                handoffMatch.Operation,
                handoffMatch.Receipt),
            CreateAuthorityEffectBinding(
                "projection_detached",
                handoffMatch.Operation,
                handoffMatch.Receipt),
        ];
    }

    internal static ImmutableArray<
        FileTransferCoordinatorAuthorityEffectBinding>
        CreateRegularV4PressureRecoveryHandoffAuthorityEffectBindingsForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope)
        => CreateRegularV4PressureRecoveryHandoffAuthorityEffectBindings(
            envelope);

    private static bool ReceiptMatchesOperation(
        FileTransferCoordinatorOperationTransitionSnapshot operation,
        FileTransferCoordinatorReceiptTransitionSnapshot receipt)
        => receipt.ReceiptIdentity == operation.ReceiptIdentity &&
           receipt.ReceiptGeneration == operation.ReceiptGeneration &&
           receipt.ReceiptStatus == operation.ReceiptStatus &&
           receipt.ReceiptFingerprint == operation.ReceiptFingerprint;

    private static FileTransferCoordinatorAuthorityEffectBinding
        CreateAuthorityEffectBinding(
            string effectKind,
            FileTransferCoordinatorOperationTransitionSnapshot operation,
            FileTransferCoordinatorReceiptTransitionSnapshot receipt)
        => new(
            effectKind,
            operation.Family,
            operation.OperationKey,
            operation.OperationKind,
            operation.OperationRole,
            operation.AuthorityId,
            operation.OperationGeneration,
            receipt.Stage,
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint);

    private static ImmutableArray<
        FileTransferCoordinatorAuthorityEffectBinding>
        CreateRegularV4PressureRouteCommitAuthorityEffectBindings(
            FileTransferCoordinatorTransitionEnvelope envelope)
    {
        if (envelope.DecisionKind != nameof(
                FileTransferCoordinator.CommitFileTransferRouteTransition) ||
            envelope.DecisionStatus != "Committed")
        {
            return [];
        }

        var settlementMatches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.OperationRole == "pressure_settlement_terminal" &&
                operation.OperationKind == "PressureSettlement" &&
                operation.ReceiptValidated &&
                operation.RegularV4FrontierRepairPressure is not null)
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt =>
                        receipt.ReceiptValidated &&
                        receipt.Stage == "pressure_settlement" &&
                        receipt.ReceiptIdentity ==
                            operation.ReceiptIdentity &&
                        receipt.ReceiptGeneration ==
                            operation.ReceiptGeneration &&
                        receipt.ReceiptStatus == operation.ReceiptStatus &&
                        receipt.ReceiptFingerprint ==
                            operation.ReceiptFingerprint)
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .ToArray();
        var entering = settlementMatches.Where(match =>
                match.Operation.BeforeState == "None" &&
                match.Operation.AfterState == "Applied")
            .ToArray();
        var carried = settlementMatches.Where(match =>
                match.Operation.BeforeState == "Applied" &&
                match.Operation.AfterState == "Applied")
            .ToArray();
        var settlementMatch = entering.Length == 1 && carried.Length == 0
            ? entering[0]
            : entering.Length == 0 && carried.Length == 1
                ? carried[0]
                : default;
        if (settlementMatch.Operation is null)
        {
            return [];
        }

        var mirrorMatches = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family ==
                    "regular_v4_frontier_repair_pressure" &&
                operation.OperationRole == "repair_authority_terminal" &&
                operation.OperationKind == "PressureSettlement" &&
                operation.AuthorityId ==
                    settlementMatch.Operation.AuthorityId &&
                operation.OperationGeneration ==
                    settlementMatch.Operation.OperationGeneration &&
                operation.BeforeState ==
                    settlementMatch.Operation.BeforeState &&
                operation.AfterState == settlementMatch.Operation.AfterState &&
                operation.ReceiptValidated &&
                operation.ReceiptIdentity ==
                    settlementMatch.Receipt.ReceiptIdentity &&
                operation.ReceiptGeneration ==
                    settlementMatch.Receipt.ReceiptGeneration &&
                operation.ReceiptStatus ==
                    settlementMatch.Receipt.ReceiptStatus &&
                operation.ReceiptFingerprint ==
                    settlementMatch.Receipt.ReceiptFingerprint &&
                operation.RegularV4FrontierRepairPressure is not null)
            .SelectMany(operation =>
                operation.RegularV4FrontierRepairPressure!.Receipts
                    .Where(receipt =>
                        receipt.ReceiptValidated &&
                        receipt.Stage == "pressure_settlement" &&
                        receipt.ReceiptIdentity ==
                            settlementMatch.Receipt.ReceiptIdentity &&
                        receipt.ReceiptGeneration ==
                            settlementMatch.Receipt.ReceiptGeneration &&
                        receipt.ReceiptStatus ==
                            settlementMatch.Receipt.ReceiptStatus &&
                        receipt.ReceiptFingerprint ==
                            settlementMatch.Receipt.ReceiptFingerprint)
                    .Select(receipt =>
                        (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (mirrorMatches.Length != 1)
        {
            return [];
        }

        var match = mirrorMatches[0];

        var effectKinds = entering.Length == 1
            ? new[]
            {
                "pressure_settlement_bridge_applied",
                "local_contract_settled",
                "local_contract_reference_cleared",
                "route_commit_local_lifecycle_adopted",
            }
            : new[] { "route_commit_local_lifecycle_adopted" };
        return effectKinds.Select(effectKind =>
                new FileTransferCoordinatorAuthorityEffectBinding(
                    effectKind,
                    match.Operation.Family,
                    match.Operation.OperationKey,
                    match.Operation.OperationKind,
                    match.Operation.OperationRole,
                    match.Operation.AuthorityId,
                    match.Operation.OperationGeneration,
                    match.Receipt.Stage,
                    match.Receipt.CommandIdentity,
                    match.Receipt.CommandGeneration,
                    match.Receipt.CommandFingerprint,
                    match.Receipt.ReceiptIdentity,
                    match.Receipt.ReceiptGeneration,
                    match.Receipt.ReceiptStatus,
                    match.Receipt.ReceiptFingerprint))
            .ToImmutableArray();
    }

    internal static ImmutableArray<
        FileTransferCoordinatorAuthorityEffectBinding>
        CreateRegularV4PressureRouteCommitAuthorityEffectBindingsForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope)
        => CreateRegularV4PressureRouteCommitAuthorityEffectBindings(envelope);

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

        var binding = CreateFallbackTailYieldAuthorityEffectBinding(
            envelope,
            stage,
            kind);
        if (binding is null)
        {
            return;
        }
        var exactBinding = binding;
        var isRecoveryOwnerRetirement = string.Equals(
            kind,
            "recovery_owner_retired_after_yield",
            StringComparison.Ordinal);
        emitter.EnqueueAuthorityEffect(
            coordinatorInstanceId,
            sessionId,
            transferId,
            direction,
            isRecoveryOwnerRetirement
                ? envelope.Aggregate.AfterStateGeneration
                : stateGeneration,
            exactBinding.Family,
            exactBinding.AuthorityId,
            exactBinding.OperationGeneration,
            exactBinding.ReceiptIdentity,
            exactBinding.ReceiptGeneration,
            exactBinding.ReceiptStatus,
            exactBinding.ReceiptFingerprint,
            kind,
            $"{exactBinding.OperationKey}:{kind}",
            exactBinding.ReceiptGeneration,
            isRecoveryOwnerRetirement
                ? exactBinding.OperationKey
                : null);
    }

    private static FileTransferCoordinatorAuthorityEffectBinding?
        CreateFallbackTailYieldAuthorityEffectBinding(
            FileTransferCoordinatorTransitionEnvelope envelope,
            string stage,
            string kind)
    {
        var operations = envelope.Aggregate.Operations
            .Where(operation =>
                operation.Family == "fallback_tail_yield" &&
                operation.FallbackTailYield is not null);
        var isRecoveryOwnerRetirement = string.Equals(
            kind,
            "recovery_owner_retired_after_yield",
            StringComparison.Ordinal);
        var isCheckpointSuccessorPeerProof =
            string.Equals(
                kind,
                "peer_proof_observed",
                StringComparison.Ordinal) &&
            string.Equals(
                envelope.DecisionKind,
                nameof(FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt),
                StringComparison.Ordinal) &&
            string.Equals(
                envelope.DecisionStatus,
                "Accepted",
                StringComparison.Ordinal) &&
            string.Equals(
                envelope.DecisionReason,
                "fallback_tail_initial_peer_proof_receipt_superseded_after_checkpoint_advance",
                StringComparison.Ordinal);
        if (isCheckpointSuccessorPeerProof)
        {
            operations = operations.Where(operation =>
                operation.StateApplied &&
                string.Equals(
                    operation.OperationRole,
                    "peer_proof_pending_initial",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.OperationKind,
                    "PeerProof",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.BeforeState,
                    "None",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.AfterState,
                    "Pending",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.FallbackTailYield!.Role,
                    "peer_proof_pending_initial",
                    StringComparison.Ordinal));
        }
        if (isRecoveryOwnerRetirement)
        {
            if (!string.Equals(
                    envelope.DecisionKind,
                    nameof(FileTransferCoordinatorEventKind.FallbackTailQuiescent),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    envelope.DecisionStatus,
                    "Accepted",
                    StringComparison.Ordinal))
            {
                return null;
            }

            operations = operations.Where(operation =>
                operation.StateApplied &&
                string.Equals(
                    operation.OperationRole,
                    "yield_current",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.OperationKind,
                    FallbackTailYieldKind.Quiescent.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.FallbackTailYield!.Role,
                    "yield_current",
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.FallbackTailYield.YieldKind,
                    FallbackTailYieldKind.Quiescent.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.BeforeState,
                    FileTransferFallbackTailYieldPairingState.None.ToString(),
                    StringComparison.Ordinal) &&
                (string.Equals(
                     operation.AfterState,
                     FileTransferFallbackTailYieldPairingState.YieldIssued
                         .ToString(),
                     StringComparison.Ordinal) ||
                 string.Equals(
                     operation.AfterState,
                     FileTransferFallbackTailYieldPairingState.PeerProofObserved
                         .ToString(),
                     StringComparison.Ordinal)));
        }

        var matches = operations
            .SelectMany(operation => operation.FallbackTailYield!.Receipts
                .Where(receipt =>
                    receipt.ReceiptValidated &&
                    receipt.Stage == stage &&
                    (!isRecoveryOwnerRetirement ||
                     string.Equals(
                         receipt.ReceiptStatus,
                         "Accepted",
                         StringComparison.Ordinal)))
                .Select(receipt => (Operation: operation, Receipt: receipt)))
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var match = matches[0];
        return CreateAuthorityEffectBinding(
            kind,
            match.Operation,
            match.Receipt);
    }

    internal static FileTransferCoordinatorAuthorityEffectBinding?
        CreateFallbackTailYieldAuthorityEffectBindingForTesting(
            FileTransferCoordinatorTransitionEnvelope envelope,
            string stage,
            string kind)
        => CreateFallbackTailYieldAuthorityEffectBinding(
            envelope,
            stage,
            kind);

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
        => CreateCoordinatorSnapshotEvidence(envelope);

    internal static FileTransferCoordinatorTransitionEnvelope
        CreateCoordinatorSnapshotEvidence(FileTransferCoordinatorTransitionEnvelope envelope)
        => CoordinatorSnapshotStreamEmitter.CreateEvidenceEnvelope(envelope);

    private sealed class CoordinatorSnapshotStreamEmitter
    {
        private InitialActivationReceiverBarrierAuthority? receiverBarrierExecutionAuthority;

        internal void BeforeStateAdoption(FileTransferCoordinatorState state)
        {
            var current = state.PersistentState.InitialActivationReceiverBarrier.Current?.Authority;
            var previous = Interlocked.Exchange(ref receiverBarrierExecutionAuthority, current);
            if (!ReferenceEquals(previous, current))
            {
                previous?.ExecutionFence.Invalidate();
            }
        }
        private readonly ConcurrentQueue<string> pending = new();
        private readonly ConcurrentDictionary<string, FrontierCompletionWriteBinding> frontierCompletionMessages =
            new(ReferenceEqualityComparer.Instance);
        private FrontierCompletionWriteBinding? stagedFrontierCompletion;
        private readonly TaskCompletionSource finalSubmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task FinalSubmission => finalSubmission.Task;
        private string? finalMessage;
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
        private readonly Dictionary<string, AuthorityEffectBinding>
            authorityEffectBindingsByOperationKey =
                new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthorityEffectBinding>
            fallbackTailYieldIssuanceBindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> authorityEffectMessages =
            new(StringComparer.Ordinal);
        private readonly HashSet<string>
            fallbackTailRecoveryOwnerRetirementEffectIdentities =
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

        internal void StageFrontierCompletion(FrontierCompletionWriteBinding binding, FileTransferCoordinatorTransitionEnvelope envelope)
        {
            lock (stateGate)
            {
                if (completed || stagedFrontierCompletion is not null || !binding.TryStage(envelope, this))
                {
                    binding.Revoke();
                    stagedFrontierCompletion?.Revoke();
                    stagedFrontierCompletion = null;
                    return;
                }
                stagedFrontierCompletion = binding;
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
                var completion = stagedFrontierCompletion;
                stagedFrontierCompletion = null;
                if (completion?.TryEnqueue(envelope) != true) { completion = null; }
                var message =
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
                    $"snapshot_sha256={payloadHash}; snapshot_payload={evidenceEnvelope.ToBase64UrlPayload()}; authority_effect=none";
                // Key by the exact queued string object, never by equal text or parsed IDs.
                // At most the native catalog's 16 admitted producers can supply bindings.
                if (completion is not null) { frontierCompletionMessages.TryAdd(message, completion); }
                pending.Enqueue(message);
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
            Interlocked.Exchange(ref receiverBarrierExecutionAuthority, null)?.ExecutionFence.Invalidate();
            lock (stateGate)
            {
                if (completed || !started)
                {
                    return;
                }

                completed = true;
                stagedFrontierCompletion?.Revoke();
                stagedFrontierCompletion = null;
                finalMessage = "event=filetransfer_coordinator_snapshot_stream_completed; " +
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
                    $"authoritative_family_coverage={CoordinatorSnapshotAuthoritativeFamilyCoverage}; authority_effect=none";
                pending.Enqueue(finalMessage);
            }

            ScheduleDrain();
        }

        internal FileTransferRoutePublicationEvidence? GetRoutePublicationEvidence(RoutePublicationToken? publication)
        {
            lock (stateGate)
            {
                if (!started || completed || publication is not { IsDefault: false } token ||
                    token.SessionId != sessionId || token.TransferId != transferId ||
                    token.Direction.ToString().ToLowerInvariant() != direction)
                {
                    return null;
                }

                var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(token);
                var identity = $"publication:{token.PublicationGeneration}";
                if (!authorityEffectBindings.TryGetValue(CreateAuthorityEffectBindingKey(
                        "route_runtime", identity, token.PublicationGeneration, identity,
                        token.PublicationGeneration, "Accepted", fingerprint), out var binding))
                {
                    return null;
                }

                // The index supplies a reference only. Consumers must validate
                // the exact authored boundary/receipt; lookup grants no authority.
                return new(EvidenceToken(coordinatorInstanceId), EvidenceToken(sessionId), EvidenceToken(transferId),
                    binding.TransitionId, binding.StateGeneration, finalGeneration, binding.OperationKey, EvidenceToken(fingerprint));
            }
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
            long effectGeneration = 0,
            string? expectedOperationKey = null)
        {
            lock (stateGate)
            {
                if (completed)
                {
                    return;
                }

                var resolvedEffectIdentity = effectIdentity ?? receiptIdentity;
                var resolvedEffectGeneration = effectGeneration > 0
                    ? effectGeneration
                    : receiptGeneration;
                var isYieldRetirement = string.Equals(
                        family,
                        "fallback_tail_yield",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        effectKind,
                        "recovery_owner_retired_after_yield",
                        StringComparison.Ordinal);
                if (isYieldRetirement &&
                    !fallbackTailRecoveryOwnerRetirementEffectIdentities.Add(
                        string.Join(
                            "|",
                            authorityId,
                            operationGeneration.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            resolvedEffectIdentity,
                            resolvedEffectGeneration.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            receiptIdentity,
                            receiptGeneration.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            receiptStatus,
                            receiptFingerprint)))
                {
                    return;
                }

                string transitionId;
                string operationKey;
                FileTransferCoordinatorStateBoundarySnapshot? scope;
                long boundStateGeneration;
                AuthorityEffectBinding? binding;
                var operationBindings = isYieldRetirement
                    ? fallbackTailYieldIssuanceBindings
                    : authorityEffectBindingsByOperationKey;
                var hasBinding = !string.IsNullOrWhiteSpace(expectedOperationKey)
                    ? operationBindings.TryGetValue(
                        CreateAuthorityEffectBindingByOperationKey(
                            expectedOperationKey,
                            receiptIdentity,
                            receiptGeneration,
                            receiptStatus,
                            receiptFingerprint),
                        out binding)
                    : authorityEffectBindings.TryGetValue(
                        CreateAuthorityEffectBindingKey(
                            family,
                            authorityId,
                            operationGeneration,
                            receiptIdentity,
                            receiptGeneration,
                            receiptStatus,
                            receiptFingerprint),
                        out binding);
                if (hasBinding && binding is not null)
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
                $"effect_kind={effectKind}; effect_identity={EvidenceToken(resolvedEffectIdentity)}; " +
                $"effect_generation={resolvedEffectGeneration}; " +
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
                // Continuity receipts attest evidence only, never physical authority.
                if (operation.CheckpointRepairContinuity is not null ||
                    operation.OperationKind == "FallbackCheckpointRepairScopeContinuity" ||
                    operation.RepairOwner is not null ||
                    operation.OperationKind == "FallbackFrontierRepairOwnerEvidence")
                {
                    continue;
                }
                var evidenceOperation =
                    evidenceEnvelope.Aggregate.Operations[operationIndex];
                var scope = operation.RouteRuntime?.Boundary ??
                            operation.RouteTransition?.SourceBoundary ??
                            operation.RuntimeUnlockRetry?.SourceBoundary ??
                            operation.ReceiveRecovery?.SourceBoundary ??
                            operation.FallbackTailYield?.SourceBoundary ??
                            operation.RegularV4FrontierRepairPressure
                                ?.SourceBoundary ??
                            operation.ResponderProbeHandoff?.SourceBoundary ??
                            operation.ReceiverBarrier?.SourceBoundary;
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
                                    operation.ReceiverBarrier?.Receipts ??
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

            // A copied yield receipt in a later local-proof/commit observation
            // must not move the one-shot retirement effect off its issuance.
            var yield = CreateFallbackTailYieldAuthorityEffectBinding(
                envelope, "yield", "recovery_owner_retired_after_yield");
            if (yield is not null)
            {
                var key = CreateAuthorityEffectBindingByOperationKey(
                    yield.OperationKey, yield.ReceiptIdentity,
                    yield.ReceiptGeneration, yield.ReceiptStatus,
                    yield.ReceiptFingerprint);
                if (authorityEffectBindingsByOperationKey.TryGetValue(key, out var binding))
                {
                    fallbackTailYieldIssuanceBindings.TryAdd(key, binding);
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
            authorityEffectBindingsByOperationKey[
                CreateAuthorityEffectBindingByOperationKey(
                    operation.OperationKey,
                    receiptIdentity,
                    receiptGeneration,
                    receiptStatus,
                    receiptFingerprint)] = new(
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

        private static string CreateAuthorityEffectBindingByOperationKey(
            string operationKey,
            string receiptIdentity,
            long receiptGeneration,
            string receiptStatus,
            string receiptFingerprint)
            => string.Join(
                "|",
                operationKey,
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
                        TargetBoundary = transition.ProofBoundaryKind == "fallback-bootstrap-correlation-v1"
                            ? CreateEvidenceBoundary(transition.TargetBoundary) : transition.TargetBoundary,
                        ProofBoundaryFingerprint = EvidenceToken(
                            transition.ProofBoundaryFingerprint),
                        DurableCheckpointFingerprint = EvidenceToken(
                            transition.DurableCheckpointFingerprint),
                        ActivationRetryBinding = transition.ActivationRetryBinding is { } retryBinding
                            ? retryBinding with
                            {
                                RetryAuthorityId = EvidenceToken(retryBinding.RetryAuthorityId),
                                InstallReceiptFingerprint = EvidenceToken(retryBinding.InstallReceiptFingerprint),
                                PairedReleaseAuthorityId = EvidenceToken(retryBinding.PairedReleaseAuthorityId),
                            }
                            : null,
                        RetainedFrameFingerprint = EvidenceToken(
                            transition.RetainedFrameFingerprint),
                        BootstrapCorrelation = transition.BootstrapCorrelation is { } correlation
                            ? CreateBootstrapCorrelationEvidence(correlation) : null,
                        BootstrapInitiation = transition.BootstrapInitiation is { } initiation
                            ? CreateBootstrapInitiationEvidence(initiation) : null,
                        BootstrapCallbacks = transition.BootstrapCallbacks is { } callbacks
                            ? callbacks with
                            {
                                CallbackFingerprint = EvidenceToken(callbacks.CallbackFingerprint),
                                SourceFingerprint = EvidenceToken(callbacks.SourceFingerprint),
                                AgreementFingerprint = EvidenceToken(callbacks.AgreementFingerprint),
                                HoldCommandFingerprint = EvidenceToken(callbacks.HoldCommandFingerprint),
                                CaptureFingerprint = EvidenceToken(callbacks.CaptureFingerprint),
                                CapturedCallbacks = callbacks.CapturedCallbacks.Select(EvidenceToken).ToImmutableArray(),
                            } : null,
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
                ReceiverBarrier = operation.ReceiverBarrier is { } barrier
                    ? barrier with
                    {
                        SourceBoundary = CreateEvidenceBoundary(barrier.SourceBoundary),
                        EffectiveBoundary = CreateEvidenceBoundary(barrier.EffectiveBoundary),
                        Receipts = barrier.Receipts.Select(CreateEvidenceReceipt).ToImmutableArray(),
                    }
                    : null,
                CheckpointRepairContinuity = operation.CheckpointRepairContinuity is { } continuity
                    ? CreateCheckpointContinuityEvidence(continuity)
                    : null,
                RepairOwner = operation.RepairOwner is { } owner ? owner with
                {
                    CoordinatorInstanceId = EvidenceToken(owner.CoordinatorInstanceId),
                    SessionId = EvidenceToken(owner.SessionId),
                    TransferId = EvidenceToken(owner.TransferId),
                    Scope = CreateCheckpointContinuityScopeEvidence(owner.Scope),
                    Repair = owner.Repair with
                    {
                        AuthorityId = EvidenceToken(owner.Repair.AuthorityId),
                        PredecessorAuthorityId = EvidenceToken(owner.Repair.PredecessorAuthorityId),
                        CanonicalRepairKey = EvidenceToken(owner.Repair.CanonicalRepairKey),
                        RangeFingerprint = EvidenceToken(owner.Repair.RangeFingerprint),
                        ProtocolRepairRequestId = EvidenceToken(owner.Repair.ProtocolRepairRequestId),
                        RequestCorrelationId = EvidenceToken(owner.Repair.RequestCorrelationId),
                        CheckpointRequestId = EvidenceToken(owner.Repair.CheckpointRequestId),
                        RepairFingerprint = EvidenceToken(owner.Repair.RepairFingerprint),
                    },
                    Receipts = owner.Receipts.Select(CreateEvidenceReceipt).ToImmutableArray(),
                } : null,
            };
        }

        private static CheckpointRepairContinuityScope CreateCheckpointContinuityScopeEvidence(
            CheckpointRepairContinuityScope scope)
            => scope with
            {
                Boundary = CreateEvidenceBoundary(scope.Boundary),
                RuntimeOwnerFingerprint = EvidenceToken(scope.RuntimeOwnerFingerprint),
                PublicationFingerprint = EvidenceToken(scope.PublicationFingerprint),
                CommitFingerprint = EvidenceToken(scope.CommitFingerprint),
                RetainedLegCheckpointRequestId = EvidenceToken(scope.RetainedLegCheckpointRequestId),
                ScopeFingerprint = EvidenceToken(scope.ScopeFingerprint),
            };

        private static FileTransferBootstrapCorrelationOperationSnapshot CreateBootstrapCorrelationEvidence(
            FileTransferBootstrapCorrelationOperationSnapshot value)
            => value with
            {
                AgreementFingerprint = EvidenceToken(value.AgreementFingerprint),
                SenderIssuerFingerprint = EvidenceToken(value.SenderIssuerFingerprint),
                ReceiverIssuerFingerprint = EvidenceToken(value.ReceiverIssuerFingerprint),
                LocalIdFingerprint = EvidenceToken(value.LocalIdFingerprint),
                PeerIdFingerprint = EvidenceToken(value.PeerIdFingerprint),
                CompletedPeerIdFingerprint = EvidenceToken(value.CompletedPeerIdFingerprint),
                RetainedPeerFrameIdentityFingerprint = EvidenceToken(value.RetainedPeerFrameIdentityFingerprint),
                PredecessorIdentity = EvidenceToken(value.PredecessorIdentity),
                EpochFrameFingerprint = EvidenceToken(value.EpochFrameFingerprint),
                ProofFrameFingerprint = EvidenceToken(value.ProofFrameFingerprint),
                ProofFrameIdentityFingerprint = EvidenceToken(value.ProofFrameIdentityFingerprint),
                DeliveryCopies = value.DeliveryCopies.Select(CreateBootstrapCopyEvidence).ToImmutableArray(),
                SettlementCopies = value.SettlementCopies.Select(CreateBootstrapCopyEvidence).ToImmutableArray(),
                FailureCopies = value.FailureCopies.Select(CreateBootstrapCopyEvidence).ToImmutableArray(),
            };

        private static FileTransferBootstrapInitiationOperationSnapshot CreateBootstrapInitiationEvidence(
            FileTransferBootstrapInitiationOperationSnapshot value)
            => value with
            {
                ObservationFingerprint = EvidenceToken(value.ObservationFingerprint),
                LocalStopFingerprint = EvidenceToken(value.LocalStopFingerprint),
                PeerWakeFingerprint = EvidenceToken(value.PeerWakeFingerprint),
                SourceFingerprint = EvidenceToken(value.SourceFingerprint),
                SourceBoundary = CreateEvidenceBoundary(value.SourceBoundary),
                AgreementFingerprint = EvidenceToken(value.AgreementFingerprint),
                HoldIdentity = EvidenceToken(value.HoldIdentity),
                HoldCommandFingerprint = EvidenceToken(value.HoldCommandFingerprint),
                CaptureFingerprint = EvidenceToken(value.CaptureFingerprint),
                CapturedWorkFingerprint = EvidenceToken(value.CapturedWorkFingerprint),
                DrainReceiptFingerprint = EvidenceToken(value.DrainReceiptFingerprint),
                SettledWorkFingerprint = EvidenceToken(value.SettledWorkFingerprint),
                FrozenCheckpointFingerprint = EvidenceToken(value.FrozenCheckpointFingerprint),
                BaseCommandIdentity = EvidenceToken(value.BaseCommandIdentity),
                BaseCommandFingerprint = EvidenceToken(value.BaseCommandFingerprint),
            };

        private static FileTransferBootstrapPhysicalCopySnapshot CreateBootstrapCopyEvidence(FileTransferBootstrapPhysicalCopySnapshot value)
            => value with { FrameFingerprint = EvidenceToken(value.FrameFingerprint) };

        private static FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot
            CreateCheckpointContinuityEvidence(FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot value)
            => value with
            {
                CoordinatorInstanceId = EvidenceToken(value.CoordinatorInstanceId),
                SessionId = EvidenceToken(value.SessionId),
                TransferId = EvidenceToken(value.TransferId),
                AcceptanceIdentity = EvidenceToken(value.AcceptanceIdentity),
                OriginOperationKey = CreateEvidenceOperationKey("route_runtime", "checkpoint_acceptance_retained",
                    EvidenceToken(value.AcceptanceIdentity), value.AcceptedStateGeneration,
                    "FallbackCheckpointRepairScopeContinuity"),
                OriginReceiptIdentity = EvidenceToken(value.OriginReceiptIdentity),
                OriginReceiptFingerprint = EvidenceToken(value.OriginReceiptFingerprint),
                SourceScope = CreateCheckpointContinuityScopeEvidence(value.SourceScope),
                ClearedScope = CreateCheckpointContinuityScopeEvidence(value.ClearedScope),
                CurrentScope = CreateCheckpointContinuityScopeEvidence(value.CurrentScope),
                AcceptedCheckpoint = value.AcceptedCheckpoint with
                {
                    CheckpointRequestId = EvidenceToken(value.AcceptedCheckpoint.CheckpointRequestId),
                    LeaseFingerprint = EvidenceToken(value.AcceptedCheckpoint.LeaseFingerprint),
                },
                Repair = value.Repair is { } repair ? repair with
                {
                    AuthorityId = EvidenceToken(repair.AuthorityId),
                    PredecessorAuthorityId = EvidenceToken(repair.PredecessorAuthorityId),
                    CanonicalRepairKey = EvidenceToken(repair.CanonicalRepairKey),
                    RangeFingerprint = EvidenceToken(repair.RangeFingerprint),
                    ProtocolRepairRequestId = EvidenceToken(repair.ProtocolRepairRequestId),
                    RequestCorrelationId = EvidenceToken(repair.RequestCorrelationId),
                    CheckpointRequestId = EvidenceToken(repair.CheckpointRequestId),
                    RepairFingerprint = EvidenceToken(repair.RepairFingerprint),
                } : null,
                Receipts = value.Receipts.Select(CreateEvidenceReceipt).ToImmutableArray(),
            };

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

        // Scheduling readiness only: all messages through the original final
        // stream message have reached the writer. Exact persistence is separate.
        internal bool WaitForFinalSubmission(TimeSpan timeout) => finalSubmission.Task.Wait(timeout);

        private void Drain()
        {
            while (true)
            {
                while (pending.TryDequeue(out var message))
                {
                    if (frontierCompletionMessages.TryRemove(message, out var completion)) { completion.Write(message); }
                    else { LocalOperationalLog.Info("FileTransferCoordinator", message); }
                    if (ReferenceEquals(message, finalMessage)) { finalSubmission.TrySetResult(); }
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
