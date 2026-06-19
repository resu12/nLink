using System.Globalization;

namespace NLink.Core.FileTransfer;

internal enum RuntimeUnlockTransactionState
{
    Idle,
    OfferPreparing,
    OfferSentObserved,
    PeerReceived,
    AnswerReceived,
    RouteCommitPending,
    Committed,
    Failed,
    Retired,
}

internal enum RuntimeUnlockTransactionEventKind
{
    ListenerReady,
    ListenerUnavailable,
    OfferGenerationCreated,
    ObservedSend,
    PeerReceived,
    AnswerReceived,
    Timeout,
    BridgeRecoveryStarted,
    BridgeRecoverySettled,
    BridgeRecoveryExhausted,
    LivenessDeadline,
    RouteCommitted,
    Terminalized,
    Failed,
    Retired,
}

internal enum RuntimeUnlockTunaPathLeaseState
{
    None,
    ListenerStarting,
    ListenerReady,
    Failed,
    Retired,
}

internal readonly record struct RuntimeUnlockTunaPathLeaseSnapshot(
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    long LeaseGeneration,
    string? ListenerRunId,
    long PayerDecisionId,
    RuntimeUnlockTunaPathLeaseState State,
    long CreatedUtcMs,
    long UpdatedUtcMs,
    long DeadlineUtcMs,
    string? FailureReason,
    string? RetiredReason)
{
    public bool IsCurrent => State == RuntimeUnlockTunaPathLeaseState.ListenerReady &&
        LeaseGeneration > 0 &&
        !string.IsNullOrWhiteSpace(ListenerRunId);

    public bool IsTerminal => State is RuntimeUnlockTunaPathLeaseState.Failed or RuntimeUnlockTunaPathLeaseState.Retired;

    public static RuntimeUnlockTunaPathLeaseSnapshot None { get; } = new(
        SessionId: string.Empty,
        TransferId: null,
        TransactionGeneration: 0,
        OfferGeneration: 0,
        LeaseGeneration: 0,
        ListenerRunId: null,
        PayerDecisionId: 0,
        State: RuntimeUnlockTunaPathLeaseState.None,
        CreatedUtcMs: 0,
        UpdatedUtcMs: 0,
        DeadlineUtcMs: 0,
        FailureReason: null,
        RetiredReason: null);
}

internal static class RuntimeUnlockTunaPathLease
{
    public static RuntimeUnlockTunaPathLeaseSnapshot Start(
        string sessionId,
        string? transferId,
        long leaseGeneration,
        string listenerRunId,
        long payerDecisionId,
        long nowUtcMs)
        => new(
            NormalizeSessionId(sessionId),
            NormalizeNullable(transferId),
            TransactionGeneration: 0,
            OfferGeneration: 0,
            LeaseGeneration: Math.Max(1, leaseGeneration),
            ListenerRunId: NormalizeNullable(listenerRunId) ?? Math.Max(1, leaseGeneration).ToString(CultureInfo.InvariantCulture),
            PayerDecisionId: payerDecisionId,
            State: RuntimeUnlockTunaPathLeaseState.ListenerStarting,
            CreatedUtcMs: nowUtcMs,
            UpdatedUtcMs: nowUtcMs,
            DeadlineUtcMs: 0,
            FailureReason: null,
            RetiredReason: null);

    public static RuntimeUnlockTunaPathLeaseSnapshot BindOffer(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        long transactionGeneration,
        long offerGeneration,
        long payerDecisionId,
        long nowUtcMs)
        => lease.State == RuntimeUnlockTunaPathLeaseState.None
            ? lease
            : lease with
            {
                TransactionGeneration = Math.Max(0, transactionGeneration),
                OfferGeneration = Math.Max(0, offerGeneration),
                PayerDecisionId = payerDecisionId > 0 ? payerDecisionId : lease.PayerDecisionId,
                UpdatedUtcMs = nowUtcMs,
            };

    public static bool TryMarkListenerReady(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string sessionId,
        long leaseGeneration,
        string? listenerRunId,
        long nowUtcMs,
        out RuntimeUnlockTunaPathLeaseSnapshot updated)
    {
        updated = lease;
        if (!IsCurrentListenerProof(lease, sessionId, leaseGeneration, listenerRunId))
        {
            return false;
        }

        updated = lease with
        {
            State = RuntimeUnlockTunaPathLeaseState.ListenerReady,
            FailureReason = null,
            RetiredReason = null,
            UpdatedUtcMs = nowUtcMs,
        };
        return true;
    }

    public static RuntimeUnlockTunaPathLeaseSnapshot Fail(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string reason,
        long nowUtcMs)
        => lease.State is RuntimeUnlockTunaPathLeaseState.None or RuntimeUnlockTunaPathLeaseState.Retired
            ? lease
            : lease with
            {
                State = RuntimeUnlockTunaPathLeaseState.Failed,
                FailureReason = NormalizeReason(reason),
                UpdatedUtcMs = nowUtcMs,
            };

    public static RuntimeUnlockTunaPathLeaseSnapshot Retire(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string reason,
        long nowUtcMs)
        => lease.State == RuntimeUnlockTunaPathLeaseState.None
            ? lease
            : lease with
            {
                State = RuntimeUnlockTunaPathLeaseState.Retired,
                RetiredReason = NormalizeReason(reason),
                UpdatedUtcMs = nowUtcMs,
            };

    public static bool IsApplicableToOffer(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string sessionId,
        long transactionGeneration,
        long offerGeneration)
        => lease.LeaseGeneration > 0 &&
           string.Equals(lease.SessionId, NormalizeSessionId(sessionId), StringComparison.Ordinal) &&
           (lease.TransactionGeneration <= 0 || lease.TransactionGeneration == transactionGeneration) &&
           (lease.OfferGeneration <= 0 || lease.OfferGeneration == offerGeneration);

    public static bool CanSatisfyRouteCommit(RuntimeUnlockTunaPathLeaseSnapshot lease)
        => lease.State == RuntimeUnlockTunaPathLeaseState.ListenerReady && lease.IsCurrent;

    private static bool IsCurrentListenerProof(
        RuntimeUnlockTunaPathLeaseSnapshot lease,
        string sessionId,
        long leaseGeneration,
        string? listenerRunId)
        => lease.LeaseGeneration > 0 &&
           lease.LeaseGeneration == leaseGeneration &&
           string.Equals(lease.SessionId, NormalizeSessionId(sessionId), StringComparison.Ordinal) &&
           string.Equals(
               lease.ListenerRunId,
               NormalizeNullable(listenerRunId),
               StringComparison.Ordinal);

    private static string NormalizeSessionId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "runtime_unlock_tuna_path_lease" : reason.Trim();
}

internal readonly record struct RuntimeUnlockTransactionEvent(
    RuntimeUnlockTransactionEventKind Kind,
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    string Reason,
    long UtcMs = 0,
    string? ObservedLane = null);

internal sealed record RuntimeUnlockTransactionSnapshot(
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    RuntimeUnlockTransactionState State,
    long CreatedUtcMs,
    long UpdatedUtcMs,
    long DeadlineUtcMs,
    bool ListenerReady,
    bool ListenerUnavailable,
    bool ObservedSend,
    string? ObservedLane,
    bool PeerReceived,
    bool AnswerReceived,
    bool RouteCommitPending,
    bool RouteCommitted,
    string? FailureReason,
    string? RetiredReason,
    long TunaPathLeaseGeneration = 0,
    RuntimeUnlockTunaPathLeaseState TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.None,
    string? TunaPathLeaseListenerRunId = null,
    bool TunaPathLeaseCurrent = false,
    string? TunaPathLeaseFailureReason = null)
{
    public bool HasPeerVisibleProof => PeerReceived || AnswerReceived;

    public bool IsTerminal => State is
        RuntimeUnlockTransactionState.Committed or
        RuntimeUnlockTransactionState.Failed or
        RuntimeUnlockTransactionState.Retired;

    public bool CanRequestRouteCommit => !IsTerminal &&
        HasPeerVisibleProof &&
        State is RuntimeUnlockTransactionState.PeerReceived or
            RuntimeUnlockTransactionState.AnswerReceived or
            RuntimeUnlockTransactionState.RouteCommitPending;

    public static RuntimeUnlockTransactionSnapshot Idle { get; } = new(
        SessionId: string.Empty,
        TransferId: null,
        TransactionGeneration: 0,
        OfferGeneration: 0,
        State: RuntimeUnlockTransactionState.Idle,
        CreatedUtcMs: 0,
        UpdatedUtcMs: 0,
        DeadlineUtcMs: 0,
        ListenerReady: false,
        ListenerUnavailable: false,
        ObservedSend: false,
        ObservedLane: null,
        PeerReceived: false,
        AnswerReceived: false,
        RouteCommitPending: false,
        RouteCommitted: false,
        FailureReason: null,
        RetiredReason: null);
}

internal sealed record RuntimeUnlockTransactionDecision(
    RuntimeUnlockTransactionSnapshot State,
    bool RouteCommitRequired,
    bool RouteCommitted,
    bool OfferGenerationRetired,
    string Reason);

internal readonly record struct RuntimeUnlockRouteCommitProof(
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    bool PeerVisibleProof,
    bool PeerReceived,
    bool AnswerReceived,
    FileTransferRoute TargetRoute,
    int ProtocolVersion,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind TargetTransport,
    RuntimeUnlockTransactionState TransactionState,
    string Reason,
    bool TunaPathLeaseRequired = false,
    long TunaPathLeaseGeneration = 0,
    RuntimeUnlockTunaPathLeaseState TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.None,
    string? TunaPathLeaseListenerRunId = null,
    bool TunaPathLeaseCurrent = false,
    string? TunaPathLeaseFailureReason = null);

internal static class RuntimeUnlockTransaction
{
    public static RuntimeUnlockTransactionDecision Apply(
        RuntimeUnlockTransactionEvent transactionEvent,
        RuntimeUnlockTransactionSnapshot? state)
    {
        var current = state ?? RuntimeUnlockTransactionSnapshot.Idle;
        var reason = NormalizeReason(transactionEvent.Reason);
        var nowMs = transactionEvent.UtcMs > 0
            ? transactionEvent.UtcMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (current.IsTerminal &&
            transactionEvent.Kind is not RuntimeUnlockTransactionEventKind.OfferGenerationCreated)
        {
            return new RuntimeUnlockTransactionDecision(current, false, false, false, reason);
        }

        if (transactionEvent.Kind is RuntimeUnlockTransactionEventKind.OfferGenerationCreated)
        {
            var generation = transactionEvent.TransactionGeneration > 0
                ? transactionEvent.TransactionGeneration
                : Math.Max(1, current.TransactionGeneration + 1);
            var offerGeneration = Math.Max(1, transactionEvent.OfferGeneration);
            var next = new RuntimeUnlockTransactionSnapshot(
                NormalizeSessionId(transactionEvent.SessionId),
                NormalizeNullable(transactionEvent.TransferId),
                generation,
                offerGeneration,
                RuntimeUnlockTransactionState.OfferPreparing,
                nowMs,
                nowMs,
                0,
                ListenerReady: false,
                ListenerUnavailable: false,
                ObservedSend: false,
                ObservedLane: null,
                PeerReceived: false,
                AnswerReceived: false,
                RouteCommitPending: false,
                RouteCommitted: false,
                FailureReason: null,
                RetiredReason: null);

            return new RuntimeUnlockTransactionDecision(next, false, false, false, reason);
        }

        if (!Matches(transactionEvent, current))
        {
            return new RuntimeUnlockTransactionDecision(current, false, false, false, "stale_runtime_unlock_transaction");
        }

        RuntimeUnlockTransactionSnapshot updated = transactionEvent.Kind switch
        {
            RuntimeUnlockTransactionEventKind.ListenerReady => current with
            {
                ListenerReady = true,
                ListenerUnavailable = false,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.ListenerUnavailable => current with
            {
                ListenerReady = false,
                ListenerUnavailable = true,
                State = RuntimeUnlockTransactionState.OfferPreparing,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.ObservedSend => current with
            {
                ObservedSend = true,
                ObservedLane = NormalizeNullable(transactionEvent.ObservedLane),
                State = RuntimeUnlockTransactionState.OfferSentObserved,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.PeerReceived => current with
            {
                PeerReceived = true,
                State = RuntimeUnlockTransactionState.PeerReceived,
                RouteCommitPending = true,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.AnswerReceived => current with
            {
                PeerReceived = true,
                AnswerReceived = true,
                State = RuntimeUnlockTransactionState.AnswerReceived,
                RouteCommitPending = true,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.BridgeRecoveryStarted => current with
            {
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.BridgeRecoverySettled => current with
            {
                State = current.HasPeerVisibleProof
                    ? current.State
                    : RuntimeUnlockTransactionState.OfferPreparing,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.RouteCommitted => current with
            {
                State = RuntimeUnlockTransactionState.Committed,
                RouteCommitted = true,
                RouteCommitPending = false,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.Timeout or
                RuntimeUnlockTransactionEventKind.BridgeRecoveryExhausted or
                RuntimeUnlockTransactionEventKind.LivenessDeadline or
                RuntimeUnlockTransactionEventKind.Failed => current with
            {
                State = RuntimeUnlockTransactionState.Failed,
                RouteCommitPending = false,
                FailureReason = reason,
                UpdatedUtcMs = nowMs,
            },
            RuntimeUnlockTransactionEventKind.Retired or
                RuntimeUnlockTransactionEventKind.Terminalized => current with
            {
                State = RuntimeUnlockTransactionState.Retired,
                RouteCommitPending = false,
                RetiredReason = reason,
                UpdatedUtcMs = nowMs,
            },
            _ => current with { UpdatedUtcMs = nowMs },
        };

        var commitRequired = updated.CanRequestRouteCommit && !current.RouteCommitPending;
        var committed = updated.State == RuntimeUnlockTransactionState.Committed &&
            current.State != RuntimeUnlockTransactionState.Committed;
        var retired = updated.State is RuntimeUnlockTransactionState.Failed or RuntimeUnlockTransactionState.Retired &&
            current.State != updated.State;

        return new RuntimeUnlockTransactionDecision(updated, commitRequired, committed, retired, reason);
    }

    public static RuntimeUnlockRouteCommitProof CreateRouteCommitProof(
        RuntimeUnlockTransactionSnapshot state,
        string reason)
        => new(
            state.SessionId,
            state.TransferId,
            state.TransactionGeneration,
            state.OfferGeneration,
            state.HasPeerVisibleProof,
            state.PeerReceived,
            state.AnswerReceived,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            state.State,
            NormalizeReason(reason),
            TunaPathLeaseRequired: state.TunaPathLeaseGeneration > 0,
            state.TunaPathLeaseGeneration,
            state.TunaPathLeaseState,
            state.TunaPathLeaseListenerRunId,
            state.TunaPathLeaseCurrent,
            state.TunaPathLeaseFailureReason);

    public static RuntimeUnlockRouteCommitProof CreateRouteCommitProof(
        RuntimeUnlockRouteCommitSnapshot snapshot)
        => new(
            snapshot.SessionId,
            snapshot.TransferId,
            snapshot.TransactionGeneration,
            snapshot.OfferGeneration,
            snapshot.PeerVisibleProof,
            snapshot.PeerReceived,
            snapshot.AnswerReceived,
            snapshot.TargetRoute,
            snapshot.ProtocolVersion,
            snapshot.HandoffKind,
            snapshot.TargetTransport,
            Enum.TryParse<RuntimeUnlockTransactionState>(
                snapshot.TransactionState,
                ignoreCase: true,
                out var state)
                ? state
                : RuntimeUnlockTransactionState.Failed,
            NormalizeReason(snapshot.Reason),
            snapshot.TunaPathLeaseRequired,
            snapshot.TunaPathLeaseGeneration,
            Enum.TryParse<RuntimeUnlockTunaPathLeaseState>(
                snapshot.TunaPathLeaseState,
                ignoreCase: true,
                out var leaseState)
                ? leaseState
                : RuntimeUnlockTunaPathLeaseState.None,
            snapshot.TunaPathLeaseListenerRunId,
            snapshot.TunaPathLeaseCurrent,
            snapshot.TunaPathLeaseFailureReason);

    public static bool CanCommitRoute(
        RuntimeUnlockRouteCommitProof proof,
        FileTransferCoordinatorState coordinatorState,
        out string rejectionReason)
    {
        rejectionReason = "none";
        if (coordinatorState.IsTerminal)
        {
            rejectionReason = "terminal_transfer";
            return false;
        }

        if (!string.Equals(proof.SessionId, coordinatorState.SessionId, StringComparison.Ordinal))
        {
            rejectionReason = "session_mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(proof.TransferId) &&
            !string.Equals(proof.TransferId, coordinatorState.TransferId, StringComparison.Ordinal))
        {
            rejectionReason = "transfer_mismatch";
            return false;
        }

        if (proof.TransactionGeneration <= 0 || proof.OfferGeneration <= 0)
        {
            rejectionReason = "generation_missing";
            return false;
        }

        if (!proof.PeerVisibleProof || (!proof.PeerReceived && !proof.AnswerReceived))
        {
            rejectionReason = "peer_visible_proof_missing";
            return false;
        }

        if (proof.TunaPathLeaseRequired)
        {
            if (proof.TunaPathLeaseGeneration <= 0)
            {
                rejectionReason = "tuna_path_lease_generation_missing";
                return false;
            }

            if (proof.TunaPathLeaseState == RuntimeUnlockTunaPathLeaseState.Failed)
            {
                rejectionReason = "tuna_path_lease_failed";
                return false;
            }

            if (proof.TunaPathLeaseState == RuntimeUnlockTunaPathLeaseState.Retired)
            {
                rejectionReason = "tuna_path_lease_retired";
                return false;
            }

            if (proof.TunaPathLeaseState != RuntimeUnlockTunaPathLeaseState.ListenerReady ||
                !proof.TunaPathLeaseCurrent)
            {
                rejectionReason = "tuna_path_lease_unavailable";
                return false;
            }
        }

        if (proof.TransactionState is not RuntimeUnlockTransactionState.PeerReceived and
            not RuntimeUnlockTransactionState.AnswerReceived and
            not RuntimeUnlockTransactionState.RouteCommitPending)
        {
            rejectionReason = "transaction_not_commit_ready";
            return false;
        }

        if (proof.TargetRoute != FileTransferRoute.FileTunaV4 ||
            proof.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            proof.HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation ||
            proof.TargetTransport != FileTransferTransportKind.Tuna)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        if (!CanTransitionFromCurrentRoute(coordinatorState.RouteRuntime, coordinatorState.RouteSelection.ProtocolVersion))
        {
            rejectionReason = "source_route_not_supported";
            return false;
        }

        return true;
    }

    private static bool CanTransitionFromCurrentRoute(
        FileTransferRouteRuntimeDescriptor currentRuntime,
        int negotiatedProtocolVersion)
        => FileTransferCoordinator.CanTransitionToRoute(
            currentRuntime,
            negotiatedProtocolVersion,
            FileTransferRoute.FileTunaV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);

    private static bool Matches(
        RuntimeUnlockTransactionEvent transactionEvent,
        RuntimeUnlockTransactionSnapshot state)
    {
        if (state.State == RuntimeUnlockTransactionState.Idle)
        {
            return false;
        }

        if (!string.Equals(
                NormalizeSessionId(transactionEvent.SessionId),
                state.SessionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var transferId = NormalizeNullable(transactionEvent.TransferId);
        if (!string.IsNullOrWhiteSpace(transferId) &&
            !string.IsNullOrWhiteSpace(state.TransferId) &&
            !string.Equals(transferId, state.TransferId, StringComparison.Ordinal))
        {
            return false;
        }

        if (transactionEvent.TransactionGeneration > 0 &&
            transactionEvent.TransactionGeneration != state.TransactionGeneration)
        {
            return false;
        }

        return transactionEvent.OfferGeneration <= 0 ||
            transactionEvent.OfferGeneration == state.OfferGeneration;
    }

    private static string NormalizeSessionId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "runtime_unlock_transaction" : reason.Trim();
}
