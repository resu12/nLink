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
        "2ba980b5696114cbd09fde978ae85d27270adc08f6caead9bd222c0074e9d0c6";

    private const string CoordinatorSnapshotEmittedFamilyCoverage =
        "route_runtime,route_transition,runtime_unlock_retry_lifecycle";

    private const string CoordinatorSnapshotShadowFamilyCoverage =
        "";

    private const string CoordinatorSnapshotAuthoritativeFamilyCoverage =
        "route_runtime,route_transition,runtime_unlock_retry_lifecycle";

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
        context.CoordinatorSnapshotEmitter.Start(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            state.PersistentState.SnapshotStateGeneration,
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(state));
    }

    private static void StartCoordinatorSnapshotStreamLocked(
        InboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        context.CoordinatorSnapshotEmitter.Start(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            state.PersistentState.SnapshotStateGeneration,
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(state));
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
        context.CoordinatorPersistentState = snapshot.State.PersistentState;
        context.CoordinatorSnapshotEmitter.Enqueue(snapshot.Envelope);
        return snapshot.State;
    }

    private static FileTransferCoordinatorState AdoptCoordinatorStateLocked(
        OutboundTransferContext context,
        FileTransferRouteTransitionDecision decision)
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
        FileTransferRouteTransitionDecision decision)
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
        FileTransferCoordinatorDecision decision)
        => decision.SnapshotEnvelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorDecision",
                GetCoordinatorDecisionSnapshotStatus(decision),
                decision.MutationRejectedReason ??
                decision.RuntimeUnlockCommitRejectedReason ??
                decision.Reason)
            : AdoptCoordinatorAuthoredEnvelopeLocked(
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
        FileTransferCoordinatorDecision decision)
        => decision.SnapshotEnvelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorDecision",
                GetCoordinatorDecisionSnapshotStatus(decision),
                decision.MutationRejectedReason ??
                decision.RuntimeUnlockCommitRejectedReason ??
                decision.Reason)
            : AdoptCoordinatorAuthoredEnvelopeLocked(
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
        FileTransferCoordinatorActionDecision decision)
        => decision.SnapshotEnvelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorActionDecision",
                decision.Accepted ? "Accepted" : "Rejected",
                decision.Accepted
                    ? "legacy_family_action_accepted"
                    : decision.RejectionReason)
            : AdoptCoordinatorAuthoredEnvelopeLocked(
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
        FileTransferCoordinatorActionDecision decision)
        => decision.SnapshotEnvelope is null
            ? AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "LegacyFamilyCoordinatorActionDecision",
                decision.Accepted ? "Accepted" : "Rejected",
                decision.Accepted
                    ? "legacy_family_action_accepted"
                    : decision.RejectionReason)
            : AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId,
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            decision.State,
            decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

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
                    "runtime_unlock_retry_lifecycle")
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
                "Coordinator-authored route snapshot envelope is missing or does not match its decision state.");
        }

        adopt(state);
        emitter.Enqueue(envelope);
        return state;
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

    private sealed class CoordinatorSnapshotStreamEmitter
    {
        private readonly ConcurrentQueue<string> pending = new();
        private readonly object stateGate = new();
        private int drainScheduled;
        private bool started;
        private bool completed;
        private long transitionCount;
        private long finalGeneration;
        private string finalHash = "(none)";
        private readonly Dictionary<string, AuthorityEffectBinding>
            authorityEffectBindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> authorityEffectMessages =
            new(StringComparer.Ordinal);

        internal void Start(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            long initialGeneration,
            string initialHash)
        {
            lock (stateGate)
            {
                StartLocked(
                    coordinatorInstanceId,
                    sessionId,
                    transferId,
                    direction.ToString().ToLowerInvariant(),
                    initialGeneration,
                    initialHash);
            }

            ScheduleDrain();
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
                IndexAuthorityEffectBindingsLocked(envelope);

            }

            ScheduleDrain();
        }

        private void StartLocked(
            string coordinatorInstanceId,
            string sessionId,
            string transferId,
            string direction,
            long initialGeneration,
            string initialHash)
        {
            if (started || completed)
            {
                return;
            }

            started = true;
            finalGeneration = initialGeneration;
            finalHash = EvidenceToken(initialHash);
            pending.Enqueue(
                "event=filetransfer_coordinator_snapshot_stream_started; " +
                $"snapshot_contract_version={FileTransferCoordinatorTransitionEnvelope.CurrentContractVersion}; " +
                "snapshot_selection_mode=SnapshotV1Shadow; " +
                $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                $"coordinator_instance_id={EvidenceToken(coordinatorInstanceId)}; " +
                $"session_id={EvidenceToken(sessionId)}; transfer_id={EvidenceToken(transferId)}; " +
                $"direction={direction}; initial_state_generation={initialGeneration}; " +
                $"initial_state_hash={EvidenceToken(initialHash)}; " +
                $"emitted_family_coverage={CoordinatorSnapshotEmittedFamilyCoverage}; " +
                $"shadow_family_coverage={CoordinatorSnapshotShadowFamilyCoverage}; " +
                $"authoritative_family_coverage={CoordinatorSnapshotAuthoritativeFamilyCoverage}; authority_effect=none");
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
                    "snapshot_selection_mode=SnapshotV1Shadow; " +
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
                "snapshot_selection_mode=SnapshotV1Shadow; " +
                $"snapshot_catalog_version={CoordinatorSnapshotCatalogVersion}; " +
                $"snapshot_catalog_hash={CoordinatorSnapshotCatalogHash}; " +
                $"coordinator_instance_id={EvidenceToken(coordinatorInstanceId)}; session_id={EvidenceToken(sessionId)}; " +
                $"transfer_id={EvidenceToken(transferId)}; direction={direction.ToString().ToLowerInvariant()}; " +
                $"transition_id={EvidenceToken(transitionId)}; coordinator_state_generation={boundStateGeneration}; " +
                $"operation_family={family}; operation_key={EvidenceToken(operationKey)}; " +
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
            FileTransferCoordinatorTransitionEnvelope envelope)
        {
            foreach (var operation in envelope.Aggregate.Operations)
            {
                var scope = operation.RouteRuntime?.Boundary ??
                            operation.RouteTransition?.SourceBoundary ??
                            operation.RuntimeUnlockRetry?.SourceBoundary;
                if (operation.ReceiptValidated)
                {
                    IndexAuthorityEffectBindingLocked(
                        envelope,
                        operation,
                        operation.ReceiptIdentity,
                        operation.ReceiptGeneration,
                        operation.ReceiptStatus,
                        operation.ReceiptFingerprint,
                        scope);
                }

                if (operation.RouteTransition is not { } routeTransition)
                {
                    if (operation.RuntimeUnlockRetry is not { } retry)
                    {
                        continue;
                    }

                    foreach (var receipt in retry.Receipts)
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
                            scope);
                    }
                    continue;
                }

                foreach (var receipt in routeTransition.Receipts)
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
                        scope);
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
            FileTransferCoordinatorStateBoundarySnapshot? scope)
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
                envelope.TransitionId,
                envelope.Aggregate.AfterStateGeneration,
                operation.OperationKey,
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

        private static FileTransferCoordinatorTransitionEnvelope
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
            => operation with
            {
                OperationKey = EvidenceToken(operation.OperationKey),
                AuthorityId = EvidenceToken(operation.AuthorityId),
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
            };

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
