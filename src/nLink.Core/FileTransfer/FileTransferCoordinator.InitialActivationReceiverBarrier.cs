using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

internal enum InitialActivationReceiverBarrierState { Bound, AdmissionAttached, ClosePrepared, Closed, Invalidated }
internal enum InitialActivationReceiverBarrierDecisionStatus { Accepted, Idempotent, Rejected, Stale, Invalidated }
internal enum InitialActivationReceiverBarrierCloseApplyStatus { Applied, Idempotent, Rejected, Stale }
internal enum InitialActivationReceiverBarrierCloseMode { Admitted, InitialResponderBeforeProbe }

internal sealed record InitialActivationReceiverBarrierSource(
    RuntimeOwnerToken RuntimeOwner, RoutePublicationToken RoutePublication,
    int TerminalGeneration, int LegGeneration, long TransportEpoch,
    int BridgeRecoveryGeneration, string CheckpointRequestId, int CheckpointGeneration,
    string DataSessionIdentity, long DataSessionGeneration, IFileTransferDataSession DataSession);

internal sealed record InitialActivationReceiverBarrierAck(
    string MessageId, string SourceFingerprint, string FrameFingerprint, long ObservedUtcMs);

internal sealed record InitialActivationReceiverBarrierNegotiation(
    string SessionId, string TransferId, long TransactionGeneration, long OfferGeneration,
    long PendingAnswerAckGeneration, string Nonce, long PayerDecisionId, long LaunchGeneration,
    long TunaPathLeaseGeneration, string TunaPathLeaseListenerRunId,
    string? AnswerMessageId = null, long AnswerDeadlineUtcMs = 0,
    InitialActivationReceiverBarrierAck? Ack = null,
    RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt? PeerProbe = null);

internal sealed record InitialActivationReceiverBarrierFailureOrigin(
    Guid LaneInstanceId, long ClientGeneration, string ProcessRunId, long FailureGeneration)
{
    public bool IsDefault => LaneInstanceId == Guid.Empty || ClientGeneration <= 0 ||
        string.IsNullOrWhiteSpace(ProcessRunId) || FailureGeneration <= 0;
}

internal sealed record InitialActivationReceiverBarrierAuthority(
    Guid AuthorityId, long BarrierGeneration, InitialActivationReceiverBarrierSource Source,
    long PauseGeneration, InitialActivationReceiverBarrierNegotiation BoundNegotiation)
{
    [JsonIgnore]
    public InitialActivationReceiverBarrierExecutionFence ExecutionFence { get; } = new();
}

internal sealed class InitialActivationReceiverBarrierExecutionFence
{
    private readonly object gate = new();
    private bool valid = true;

    public bool IsValid
    {
        get { lock (gate) { return valid; } }
    }

    public bool TryApply(Func<bool> localAvailabilityOnly)
    {
        lock (gate)
        {
            // No service, transport, lane, or route work may enter this callback.
            return valid && localAvailabilityOnly();
        }
    }

    public bool TryFinalize(Action coordinatorAdoptionOnly)
    {
        lock (gate)
        {
            if (!valid)
            {
                return false;
            }
            coordinatorAdoptionOnly();
            return true;
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            valid = false;
        }
    }
}

internal sealed record InitialActivationReceiverBarrierCloseCommand(
    InitialActivationReceiverBarrierAuthority Authority, RuntimeUnlockAdmissionToken? Admission,
    InitialActivationReceiverBarrierNegotiation Negotiation,
    InitialActivationReceiverBarrierFailureOrigin FailureOrigin,
    InitialActivationReceiverBarrierCloseMode Mode = InitialActivationReceiverBarrierCloseMode.Admitted);

internal sealed record InitialActivationReceiverBarrierCloseReceipt(
    InitialActivationReceiverBarrierCloseCommand Command,
    InitialActivationReceiverBarrierCloseApplyStatus Status, string Reason);

internal sealed record InitialActivationReceiverBarrierOperation(
    InitialActivationReceiverBarrierAuthority Authority, InitialActivationReceiverBarrierState State,
    InitialActivationReceiverBarrierNegotiation Negotiation,
    RuntimeUnlockAdmissionToken? Admission = null,
    InitialActivationReceiverBarrierCloseCommand? CloseCommand = null,
    InitialActivationReceiverBarrierCloseReceipt? CloseReceipt = null,
    string Reason = "receiver_barrier_bound",
    string InvalidatingAdmissionFingerprint = "(none)",
    long ReplacementPauseGeneration = 0);

internal sealed record InitialActivationReceiverBarrierSubstate(
    InitialActivationReceiverBarrierOperation? Current = null,
    InitialActivationReceiverBarrierOperation? LastTerminal = null,
    long LastBarrierGeneration = 0, long LastPauseGeneration = 0)
{
    internal static InitialActivationReceiverBarrierSubstate Empty { get; } = new();
}

internal sealed record InitialActivationReceiverBarrierDecision(
    FileTransferCoordinatorState State, InitialActivationReceiverBarrierDecisionStatus Status,
    string Reason, InitialActivationReceiverBarrierAuthority? Authority = null,
    InitialActivationReceiverBarrierCloseCommand? Command = null,
    InitialActivationReceiverBarrierCloseReceipt? Receipt = null)
{
    public FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope { get; init; }
}

internal static partial class FileTransferCoordinator
{
    internal static InitialActivationReceiverBarrierSource? CreateInitialActivationReceiverBarrierSource(
        FileTransferCoordinatorState state, IFileTransferDataSession dataSession)
    {
        if (dataSession is null || state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication || state.CurrentLeg is not { } leg)
        {
            return null;
        }
        var source = new InitialActivationReceiverBarrierSource(owner, publication,
            state.TerminalGeneration, leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity ?? "", state.CurrentDataSessionGeneration, dataSession);
        return IsInitialActivationReceiverBarrierSourceCurrent(state, source) ? source : null;
    }

    internal static InitialActivationReceiverBarrierDecision BindInitialActivationReceiverBarrier(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierSource source,
        long pauseGeneration, InitialActivationReceiverBarrierNegotiation negotiation)
    {
        var substate = state.PersistentState.InitialActivationReceiverBarrier;
        InitialActivationReceiverBarrierDecision decision;
        if (!IsInitialActivationReceiverBarrierSourceCurrent(state, source) || pauseGeneration <= 0 ||
            !IsInitialActivationReceiverBarrierNegotiationValid(source, negotiation))
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Rejected, "receiver_barrier_bind_scope_invalid");
        }
        else if (substate.Current is { } current)
        {
            var exact = SameInitialActivationReceiverBarrierSource(current.Authority.Source, source) &&
                current.Authority.PauseGeneration == pauseGeneration && current.Authority.BoundNegotiation == negotiation;
            decision = new(state, exact ? InitialActivationReceiverBarrierDecisionStatus.Idempotent :
                InitialActivationReceiverBarrierDecisionStatus.Rejected,
                exact ? "receiver_barrier_bind_idempotent" : "receiver_barrier_current_authority_exists", current.Authority);
        }
        else if (pauseGeneration <= substate.LastPauseGeneration)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Stale, "receiver_barrier_pause_generation_retired");
        }
        else
        {
            var authority = new InitialActivationReceiverBarrierAuthority(Guid.NewGuid(),
                substate.LastBarrierGeneration + 1, source, pauseGeneration, negotiation);
            var operation = new InitialActivationReceiverBarrierOperation(authority,
                InitialActivationReceiverBarrierState.Bound, negotiation);
            decision = new(SetInitialActivationReceiverBarrier(state, substate with
            {
                Current = operation, LastBarrierGeneration = authority.BarrierGeneration,
                LastPauseGeneration = pauseGeneration,
            }), InitialActivationReceiverBarrierDecisionStatus.Accepted, operation.Reason, authority);
        }
        return FinalizeInitialActivationReceiverBarrierSnapshot(state, decision,
            nameof(BindInitialActivationReceiverBarrier), "barrier_bind", source, negotiation);
    }

    internal static InitialActivationReceiverBarrierDecision AttachInitialActivationReceiverBarrierAdmission(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierAuthority authority,
        InitialActivationReceiverBarrierSource currentSource, RuntimeUnlockAdmissionToken admission,
        InitialActivationReceiverBarrierNegotiation negotiation)
    {
        var current = state.PersistentState.InitialActivationReceiverBarrier.Current;
        InitialActivationReceiverBarrierDecision decision;
        if (!IsInitialActivationReceiverBarrierCurrent(state, authority, currentSource) || current is null ||
            current.State is not (InitialActivationReceiverBarrierState.Bound or InitialActivationReceiverBarrierState.AdmissionAttached) ||
            admission.IsDefault || state.CurrentRuntimeUnlockAdmission != admission ||
            admission.RuntimeOwner != authority.Source.RuntimeOwner ||
            admission.LegGeneration != authority.Source.LegGeneration ||
            admission.TerminalGeneration != authority.Source.TerminalGeneration ||
            !IsInitialActivationReceiverBarrierNegotiationValid(currentSource, negotiation) ||
            !InitialActivationReceiverBarrierNegotiationExtends(current.Negotiation, negotiation) ||
            negotiation.PeerProbe is { } peer && peer.AdmissionToken != admission ||
            current.Admission is { } priorAdmission && priorAdmission != admission)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Stale,
                "receiver_barrier_admission_scope_not_current", authority);
        }
        else if (current.Admission == admission && current.Negotiation == negotiation)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Idempotent,
                "receiver_barrier_admission_idempotent", authority);
        }
        else
        {
            var updated = current with { State = InitialActivationReceiverBarrierState.AdmissionAttached,
                Admission = admission, Negotiation = negotiation, Reason = "receiver_barrier_admission_attached" };
            decision = new(SetInitialActivationReceiverBarrier(state,
                state.PersistentState.InitialActivationReceiverBarrier with { Current = updated }),
                InitialActivationReceiverBarrierDecisionStatus.Accepted, updated.Reason, authority);
        }
        return FinalizeInitialActivationReceiverBarrierSnapshot(state, decision,
            nameof(AttachInitialActivationReceiverBarrierAdmission), "barrier_admission", currentSource, negotiation);
    }

    internal static InitialActivationReceiverBarrierDecision PrepareInitialActivationReceiverBarrierClose(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierAuthority authority,
        InitialActivationReceiverBarrierSource currentSource, long pauseGeneration,
        InitialActivationReceiverBarrierNegotiation negotiation, InitialActivationReceiverBarrierFailureOrigin failureOrigin)
    {
        var current = state.PersistentState.InitialActivationReceiverBarrier.Current;
        var beforeProbe = current is { Admission: null } && state.CurrentRuntimeUnlockAdmission is null &&
            state.LastRuntimeUnlockAdmissionGeneration == 0 &&
            (current.State == InitialActivationReceiverBarrierState.Bound ||
                current is { State: InitialActivationReceiverBarrierState.ClosePrepared,
                    CloseCommand.Mode: InitialActivationReceiverBarrierCloseMode.InitialResponderBeforeProbe }) &&
            current.Negotiation == authority.BoundNegotiation &&
            IsInitialReceiverPreAdmissionFailureNegotiation(currentSource, current.Negotiation, negotiation);
        var admitted = current is { Admission: { } admission } && state.CurrentRuntimeUnlockAdmission == admission &&
            current.Negotiation == negotiation &&
            current.State is InitialActivationReceiverBarrierState.AdmissionAttached or InitialActivationReceiverBarrierState.ClosePrepared;
        InitialActivationReceiverBarrierDecision decision;
        if (!IsInitialActivationReceiverBarrierCurrent(state, authority, currentSource) || current is null ||
            pauseGeneration != authority.PauseGeneration || (!beforeProbe && !admitted) ||
            failureOrigin is null || failureOrigin.IsDefault)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Stale,
                "receiver_barrier_close_scope_not_current", authority);
        }
        else if (current.CloseCommand is { } existing)
        {
            var exact = existing.FailureOrigin == failureOrigin && existing.Negotiation == negotiation;
            decision = new(state, exact ? InitialActivationReceiverBarrierDecisionStatus.Idempotent :
                InitialActivationReceiverBarrierDecisionStatus.Stale,
                exact ? "receiver_barrier_close_prepare_idempotent" : "receiver_barrier_failure_origin_changed",
                authority, exact ? existing : null);
        }
        else
        {
            var command = new InitialActivationReceiverBarrierCloseCommand(authority, current.Admission, negotiation, failureOrigin,
                beforeProbe ? InitialActivationReceiverBarrierCloseMode.InitialResponderBeforeProbe : InitialActivationReceiverBarrierCloseMode.Admitted);
            var updated = current with { State = InitialActivationReceiverBarrierState.ClosePrepared,
                CloseCommand = command, Reason = "receiver_barrier_close_prepared" };
            decision = new(SetInitialActivationReceiverBarrier(state,
                state.PersistentState.InitialActivationReceiverBarrier with { Current = updated }),
                InitialActivationReceiverBarrierDecisionStatus.Accepted, updated.Reason, authority, command);
        }
        return FinalizeInitialActivationReceiverBarrierSnapshot(state, decision,
            nameof(PrepareInitialActivationReceiverBarrierClose), "barrier_close", currentSource, negotiation);
    }

    internal static InitialActivationReceiverBarrierDecision CommitInitialActivationReceiverBarrierClose(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierSource currentSource,
        InitialActivationReceiverBarrierCloseReceipt receipt)
    {
        var substate = state.PersistentState.InitialActivationReceiverBarrier;
        var command = receipt.Command;
        var authority = command.Authority;
        InitialActivationReceiverBarrierDecision decision;
        if (receipt.Status is not (InitialActivationReceiverBarrierCloseApplyStatus.Applied or InitialActivationReceiverBarrierCloseApplyStatus.Idempotent) ||
            string.IsNullOrWhiteSpace(receipt.Reason) || !IsInitialActivationReceiverBarrierSourceCurrent(state, currentSource) ||
            !SameInitialActivationReceiverBarrierSource(authority.Source, currentSource) ||
            state.CurrentRuntimeUnlockAdmission != command.Admission)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Stale,
                "receiver_barrier_close_receipt_not_current", authority, Receipt: receipt);
        }
        else if (substate.Current is null && substate.LastTerminal is
            { State: InitialActivationReceiverBarrierState.Closed } terminal && terminal.CloseCommand == command)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Idempotent,
                "receiver_barrier_close_idempotent", authority, command, terminal.CloseReceipt);
        }
        else if (substate.Current is not { State: InitialActivationReceiverBarrierState.ClosePrepared } current ||
            current.Authority != authority || current.CloseCommand != command)
        {
            decision = new(state, InitialActivationReceiverBarrierDecisionStatus.Stale,
                "receiver_barrier_close_command_not_current", authority, Receipt: receipt);
        }
        else
        {
            var closed = current with { State = InitialActivationReceiverBarrierState.Closed,
                CloseReceipt = receipt, Reason = "receiver_barrier_closed" };
            decision = new(SetInitialActivationReceiverBarrier(state, substate with { Current = null, LastTerminal = closed }),
                InitialActivationReceiverBarrierDecisionStatus.Accepted, closed.Reason, authority, command, receipt);
        }
        return FinalizeInitialActivationReceiverBarrierSnapshot(state, decision,
            nameof(CommitInitialActivationReceiverBarrierClose), "barrier_close", currentSource, command.Negotiation);
    }

    internal static InitialActivationReceiverBarrierDecision InvalidateInitialActivationReceiverBarrier(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierAuthority authority, string reason,
        long replacementPauseGeneration = 0)
    {
        var substate = state.PersistentState.InitialActivationReceiverBarrier;
        InitialActivationReceiverBarrierDecision decision;
        var validReplacement = reason == "receiver_barrier_pause_replaced"
            ? replacementPauseGeneration > authority.PauseGeneration
            : replacementPauseGeneration == 0;
        if (substate.Current is { } current && current.Authority == authority &&
            !string.IsNullOrWhiteSpace(reason) && validReplacement)
        {
            var terminal = current with { State = InitialActivationReceiverBarrierState.Invalidated,
                Reason = reason, ReplacementPauseGeneration = replacementPauseGeneration };
            decision = new(SetInitialActivationReceiverBarrier(state, substate with { Current = null, LastTerminal = terminal }),
                InitialActivationReceiverBarrierDecisionStatus.Invalidated, reason, authority);
        }
        else
        {
            var exact = substate.Current is null && substate.LastTerminal is
                { State: InitialActivationReceiverBarrierState.Invalidated } terminal && terminal.Authority == authority &&
                terminal.Reason == reason && terminal.ReplacementPauseGeneration == replacementPauseGeneration && validReplacement;
            decision = new(state, exact ? InitialActivationReceiverBarrierDecisionStatus.Idempotent :
                InitialActivationReceiverBarrierDecisionStatus.Stale,
                exact ? "receiver_barrier_invalidation_idempotent" : "receiver_barrier_invalidation_not_current", authority);
        }
        return FinalizeInitialActivationReceiverBarrierSnapshot(state, decision,
            nameof(InvalidateInitialActivationReceiverBarrier), "barrier_close", authority.Source, authority.BoundNegotiation);
    }

    private static FileTransferCoordinatorState SetInitialActivationReceiverBarrier(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierSubstate substate)
        => state with { PersistentState = state.PersistentState with { InitialActivationReceiverBarrier = substate } };

    private static bool SameInitialActivationReceiverBarrierSource(
        InitialActivationReceiverBarrierSource left, InitialActivationReceiverBarrierSource right)
        => left == right && ReferenceEquals(left.DataSession, right.DataSession);

    private static bool IsInitialActivationReceiverBarrierCurrent(FileTransferCoordinatorState state,
        InitialActivationReceiverBarrierAuthority authority, InitialActivationReceiverBarrierSource source)
        => state.PersistentState.InitialActivationReceiverBarrier.Current?.Authority == authority &&
            SameInitialActivationReceiverBarrierSource(authority.Source, source) &&
            IsInitialActivationReceiverBarrierSourceCurrent(state, source);

    internal static bool IsInitialActivationReceiverBarrierSourceCurrent(
        FileTransferCoordinatorState state, InitialActivationReceiverBarrierSource source)
        => source is not null && source.DataSession is not null && !state.IsTerminal &&
            source.DataSession.SessionId == state.SessionId && source.DataSession.TransferId == state.TransferId &&
            state.Direction == FileTransferDirection.Inbound && state.RouteRuntime.UsesRegularNknV4FastRuntime &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            !source.RuntimeOwner.IsDefault && !source.RoutePublication.IsDefault &&
            source.RoutePublication.RuntimeOwner == source.RuntimeOwner &&
            source.RuntimeOwner.Route == FileTransferRoute.RegularNknV4Fast &&
            source.RuntimeOwner.RouteToken == FileTransferRouteResolver.RegularNknV4FastToken &&
            source.RuntimeOwner.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 && source.RuntimeOwner.LiveRouteEpoch == 0 &&
            source.RoutePublication.SessionId == source.RuntimeOwner.SessionId &&
            source.RoutePublication.TransferId == source.RuntimeOwner.TransferId &&
            source.RoutePublication.Direction == FileTransferDirection.Inbound &&
            source.RoutePublication.Route == source.RuntimeOwner.Route && source.RoutePublication.RouteToken == source.RuntimeOwner.RouteToken &&
            source.RoutePublication.ProtocolVersion == source.RuntimeOwner.ProtocolVersion && source.RoutePublication.LiveRouteEpoch == 0 &&
            source.RoutePublication.RuntimeGeneration == source.RuntimeOwner.RuntimeGeneration &&
            source.RoutePublication.TerminalGeneration == source.TerminalGeneration &&
            source.RoutePublication.LegGeneration == source.LegGeneration &&
            source.RoutePublication.RouteCommitGeneration.GetValueOrDefault() == 0 &&
            source.RoutePublication.TargetRoute == FileTransferRoute.RegularNknV4Fast &&
            source.RoutePublication.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            source.RoutePublication.TargetTransport == FileTransferTransportKind.RegularNkn &&
            state.CurrentRuntimeOwner == source.RuntimeOwner && state.CurrentRoutePublicationToken == source.RoutePublication &&
            source.RuntimeOwner.SessionId == state.SessionId && source.RuntimeOwner.TransferId == state.TransferId &&
            source.RuntimeOwner.Direction == FileTransferDirection.Inbound &&
            source.DataSession.SessionId == state.SessionId && source.DataSession.TransferId == state.TransferId &&
            state.TerminalGeneration == source.TerminalGeneration &&
            state.CurrentLeg is { } leg && leg.Generation == source.LegGeneration &&
            leg.TransportEpochId == source.TransportEpoch && leg.BridgeRecoveryGeneration == source.BridgeRecoveryGeneration &&
            (leg.CheckpointRequestId ?? "(none)") == source.CheckpointRequestId && leg.CheckpointGeneration == source.CheckpointGeneration &&
            source.TransportEpoch == 0 && source.BridgeRecoveryGeneration == 0 && source.CheckpointGeneration == 0 &&
            source.CheckpointRequestId == "(none)" && source.DataSessionGeneration > 0 &&
            !string.IsNullOrWhiteSpace(source.DataSessionIdentity) &&
            state.CurrentDataSessionIdentity == source.DataSessionIdentity && state.CurrentDataSessionGeneration == source.DataSessionGeneration &&
            state.CurrentRouteCommit is null && state.PersistentState.FileTransferRouteTransition.CurrentOperation is null &&
            state.PersistentState.FileTransferRouteTransition.PendingSupersession is null;

    private static bool IsInitialActivationReceiverBarrierNegotiationValid(
        InitialActivationReceiverBarrierSource source, InitialActivationReceiverBarrierNegotiation negotiation)
        => negotiation is not null && negotiation.SessionId == source.RuntimeOwner.SessionId &&
            negotiation.TransferId == source.RuntimeOwner.TransferId && negotiation.TransactionGeneration > 0 &&
            negotiation.OfferGeneration > 0 && negotiation.PendingAnswerAckGeneration >= 0 &&
            (negotiation.PendingAnswerAckGeneration > 0 ||
                (negotiation.AnswerMessageId is null && negotiation.Ack is null && negotiation.PeerProbe is null)) &&
            !string.IsNullOrWhiteSpace(negotiation.Nonce) && negotiation.PayerDecisionId > 0 &&
            negotiation.LaunchGeneration >= 0 && negotiation.TunaPathLeaseGeneration >= 0 &&
            (negotiation.TunaPathLeaseGeneration == 0
                ? string.IsNullOrWhiteSpace(negotiation.TunaPathLeaseListenerRunId)
                : !string.IsNullOrWhiteSpace(negotiation.TunaPathLeaseListenerRunId)) &&
            (negotiation.AnswerMessageId is null ? negotiation.AnswerDeadlineUtcMs == 0 :
                !string.IsNullOrWhiteSpace(negotiation.AnswerMessageId) && negotiation.AnswerDeadlineUtcMs > 0) &&
            (negotiation.Ack is null || (!string.IsNullOrWhiteSpace(negotiation.Ack.MessageId) &&
                negotiation.Ack.SourceFingerprint.Length == 64 && negotiation.Ack.FrameFingerprint.Length == 64 &&
                negotiation.Ack.ObservedUtcMs > 0)) &&
            (negotiation.PeerProbe is null || IsInitialActivationReceiverBarrierPeerScopeExact(source, negotiation.PeerProbe));

    private static bool IsInitialActivationReceiverBarrierPeerScopeExact(
        InitialActivationReceiverBarrierSource source, RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt peer)
    {
        var scope = peer.ReceiveScope;
        return !scope.IsDefault && scope.ResponderTransitionMode == RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 &&
            peer.SessionId == source.RuntimeOwner.SessionId && peer.TransferId == source.RuntimeOwner.TransferId &&
            peer.Direction == FileTransferDirection.Inbound && peer.AdmissionToken == scope.Admission &&
            scope.RuntimeOwner == source.RuntimeOwner && scope.RoutePublication == source.RoutePublication &&
            scope.TerminalGeneration == source.TerminalGeneration && scope.LegGeneration == source.LegGeneration &&
            scope.TransportEpoch == source.TransportEpoch && scope.BridgeRecoveryGeneration == source.BridgeRecoveryGeneration &&
            scope.CheckpointRequestId == source.CheckpointRequestId && scope.CheckpointGeneration == source.CheckpointGeneration &&
            scope.DataSessionIdentity == source.DataSessionIdentity && scope.DataSessionGeneration == source.DataSessionGeneration &&
            peer.DataSessionGeneration == source.DataSessionGeneration && peer.PeerTransactionGeneration > 0 &&
            peer.PeerOfferGeneration > 0 && peer.PeerTunaPathLeaseGeneration > 0 &&
            !string.IsNullOrWhiteSpace(peer.ProbeId) && peer.ProbeFrameFingerprint.Length == 64 &&
            peer.ReceivedTransport == FileTransferTransportKind.Tuna && peer.ReceivedUtcMs > 0 &&
            peer.ProbeAckSentUtcMs >= peer.ReceivedUtcMs;
    }

    private static bool InitialActivationReceiverBarrierNegotiationExtends(
        InitialActivationReceiverBarrierNegotiation prior, InitialActivationReceiverBarrierNegotiation next)
        => prior.SessionId == next.SessionId && prior.TransferId == next.TransferId &&
            prior.TransactionGeneration == next.TransactionGeneration && prior.OfferGeneration == next.OfferGeneration &&
            (prior.PendingAnswerAckGeneration == 0 || prior.PendingAnswerAckGeneration == next.PendingAnswerAckGeneration) && prior.Nonce == next.Nonce &&
            prior.PayerDecisionId == next.PayerDecisionId &&
            (prior.LaunchGeneration == 0 || prior.LaunchGeneration == next.LaunchGeneration) &&
            (prior.TunaPathLeaseGeneration == 0 || (prior.TunaPathLeaseGeneration == next.TunaPathLeaseGeneration &&
                prior.TunaPathLeaseListenerRunId == next.TunaPathLeaseListenerRunId)) &&
            (prior.AnswerMessageId is null || (prior.AnswerMessageId == next.AnswerMessageId && prior.AnswerDeadlineUtcMs == next.AnswerDeadlineUtcMs)) &&
            (prior.Ack is null || prior.Ack == next.Ack) && (prior.PeerProbe is null || prior.PeerProbe == next.PeerProbe);

    private static bool IsInitialReceiverPreAdmissionFailureNegotiation(InitialActivationReceiverBarrierSource source,
        InitialActivationReceiverBarrierNegotiation bound, InitialActivationReceiverBarrierNegotiation observed)
        => bound is { LaunchGeneration: 0, PendingAnswerAckGeneration: > 0, TunaPathLeaseGeneration: > 0,
                AnswerMessageId: not null, Ack: not null, PeerProbe: null } &&
            IsInitialActivationReceiverBarrierNegotiationValid(source, observed) &&
            observed with { LaunchGeneration = 0 } == bound;

    private static FileTransferCoordinatorState ReconcileInitialActivationReceiverBarrier(FileTransferCoordinatorState state)
    {
        var substate = state.PersistentState.InitialActivationReceiverBarrier;
        if (substate.Current is not { } current)
        {
            return state;
        }
        var sourceCurrent = IsInitialActivationReceiverBarrierSourceCurrent(state, current.Authority.Source);
        var admissionReplaced = (current.Admission is not null ||
            current.CloseCommand?.Mode == InitialActivationReceiverBarrierCloseMode.InitialResponderBeforeProbe) &&
            state.CurrentRuntimeUnlockAdmission != current.Admission;
        if (sourceCurrent && !admissionReplaced)
        {
            return state;
        }
        return SetInitialActivationReceiverBarrier(state, substate with { Current = null,
            LastTerminal = current with { State = InitialActivationReceiverBarrierState.Invalidated,
                Reason = sourceCurrent ? "receiver_barrier_admission_replaced" : "receiver_barrier_source_boundary_invalidated",
                InvalidatingAdmissionFingerprint = sourceCurrent && state.CurrentRuntimeUnlockAdmission is { } replacement
                    ? CalculateSnapshotFingerprint(replacement) : "(none)" } });
    }
}
