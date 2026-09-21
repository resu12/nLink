using NLink.Core.SessionSecurity;

namespace NLink.Core.FileTransfer;

internal enum FileTransferSessionPreparationPhase { Unavailable, Eligible, Pending, Ready, AdmissionReserved, Revoked }
internal enum FileTransferSessionPreparationStatus { Accepted, Idempotent, Rejected, Stale }
internal enum FileTransferAccelerationRequestPurpose { Unavailable, SessionPreparation, TransferActivation }
internal sealed record FileTransferAccelerationRequestSelection(FileTransferAccelerationRequestPurpose Purpose,
    string Reason, FileTransferSessionPreparationPermit? Permit = null);

internal sealed record FileTransferSessionPreparationScope(
    Guid ServiceInstanceId, long AttachmentGeneration, long SecurityGeneration,
    Guid SecurityIncarnation, string SessionId, string HelpeeIdentity, string HelperIdentity,
    DateTimeOffset ApprovalExpiresUtc);

// This permits session preparation only. It is not a route or runtime-owner token.
internal sealed record FileTransferSessionPreparationPermit(
    FileTransferSessionPreparationScope Scope, long Generation, long AdmissionGeneration,
    string Nonce, long PayerDecisionId, bool LocalIsPayer,
    DateTimeOffset IssuedUtc, DateTimeOffset DeadlineUtc);

internal sealed record FileTransferSessionPreparationProof(
    string Nonce, long PayerDecisionId, long OfferGeneration, long SnapshotGeneration,
    long LeaseGeneration, string ListenerRunId, bool LeaseCurrent, bool ListenerReady,
    bool FileLaneNegotiated, bool AnswerReceived, bool AnswerAckDispatched,
    bool AnswerAckReceived, DateTimeOffset ObservedUtc);

internal sealed record FileTransferSessionPreparationReadiness(
    FileTransferSessionPreparationProof Proof, DateTimeOffset ExpiresUtc);

internal sealed record FileTransferSessionPreparationLeaseObservation(
    FileTransferSessionPreparationScope Scope, long SnapshotGeneration, long LeaseGeneration,
    string ListenerRunId, bool LeaseCurrent, bool ListenerReady, bool FileLaneNegotiated,
    DateTimeOffset ObservedUtc);

internal sealed record FileTransferSessionPreparationAdmission(
    FileTransferSessionPreparationScope Scope, long Generation, string TransferId,
    FileTransferDirection Direction, DateTimeOffset ReservedUtc,
    FileTransferSessionPreparationReadiness? Readiness);

internal sealed record FileTransferSessionPreparationState(Guid ServiceInstanceId)
{
    public bool Attached { get; init; }
    public long AttachmentGeneration { get; init; }
    public long LastSecurityGeneration { get; init; }
    public Guid LastSecurityIncarnation { get; init; }
    public long LastPreparationGeneration { get; init; }
    public long AdmissionGeneration { get; init; }
    public FileTransferSessionPreparationPhase Phase { get; init; }
    public FileTransferSessionPreparationScope? Scope { get; init; }
    public FileTransferSessionPreparationPermit? Permit { get; init; }
    public FileTransferSessionPreparationReadiness? Readiness { get; init; }
    public FileTransferSessionPreparationAdmission? Admission { get; init; }
    public FileTransferInitialTunaReadinessEvidence? ClaimedEvidence { get; init; }
}

internal sealed record FileTransferSessionPreparationDecision(
    FileTransferSessionPreparationState State, FileTransferSessionPreparationStatus Status,
    string Reason, FileTransferSessionPreparationPermit? Permit = null,
    FileTransferSessionPreparationAdmission? Admission = null,
    FileTransferInitialTunaReadinessEvidence? Evidence = null);

// Deliberately contains no executable permit or transfer authority.
internal sealed record FileTransferSessionPreparationObservation(
    bool Ready, string Reason, FileTransferSessionPreparationPhase Phase,
    FileTransferDirection Direction, string? EpisodeNonce, long PreparationGeneration,
    long SnapshotGeneration, long LeaseGeneration, DateTimeOffset SourceCapturedUtc,
    DateTimeOffset ExpiresUtc, bool Consumed)
{
    public string AuthorityEffect => "none";
}

internal static partial class FileTransferCoordinator
{
    // Match existing runtime-unlock handshake and initial-readiness bounds.
    private static readonly TimeSpan SessionPreparationHandshakeLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SessionPreparationReadinessLifetime = TimeSpan.FromMinutes(5);

    internal static FileTransferAccelerationRequestSelection SelectSessionPreparationRequest(
        FileTransferSessionPreparationState state, string sessionId,
        FileTransferCoordinatorState? transfer, DateTimeOffset now)
    {
        if (transfer is not null)
            return !transfer.IsTerminal && transfer.CurrentRoutePublicationToken is { } publication &&
                publication.SessionId == sessionId && IsRoutePublicationTokenCurrent(transfer, publication, out _)
                ? new(FileTransferAccelerationRequestPurpose.TransferActivation, "current_transfer_publication")
                : new(FileTransferAccelerationRequestPurpose.Unavailable, "transfer_publication_unavailable");
        if (state.Scope is not { } scope || scope.SessionId != sessionId || !IsPreparationScopeCurrent(state, scope, now))
            return new(FileTransferAccelerationRequestPurpose.Unavailable, "preparation_scope_unavailable");
        if (state.Phase == FileTransferSessionPreparationPhase.Eligible)
            return new(FileTransferAccelerationRequestPurpose.SessionPreparation, "preparation_eligible");
        if (state.Permit is { } permit && IsSessionPreparationPermitCurrent(state, permit, now))
            return new(FileTransferAccelerationRequestPurpose.SessionPreparation, "preparation_episode_current", permit);
        return new(FileTransferAccelerationRequestPurpose.Unavailable, "preparation_not_eligible");
    }

    internal static FileTransferSessionPreparationState AttachSessionPreparationTransport(
        FileTransferSessionPreparationState state)
        => ClearSessionPreparation(state) with
        {
            Attached = true,
            AttachmentGeneration = checked(state.AttachmentGeneration + 1),
            Phase = FileTransferSessionPreparationPhase.Unavailable,
            Scope = null,
        };

    internal static FileTransferSessionPreparationState DetachSessionPreparationTransport(
        FileTransferSessionPreparationState state)
        => ClearSessionPreparation(state) with
        {
            Attached = false,
            Phase = FileTransferSessionPreparationPhase.Unavailable,
            Scope = null,
        };

    // The service must source securityGeneration/incarnation from actual verified
    // security-session establishment, not manufacture them from status notifications.
    // It must hold its gate and have exclusive, known ownership of the attachment.
    internal static FileTransferSessionPreparationDecision RegisterSessionPreparationScope(
        FileTransferSessionPreparationState state, long attachmentGeneration,
        long securityGeneration, Guid securityIncarnation, SessionSecurityState security,
        DateTimeOffset now)
    {
        if (!state.Attached || state.ServiceInstanceId == Guid.Empty ||
            attachmentGeneration != state.AttachmentGeneration || attachmentGeneration <= 0 ||
            securityGeneration <= 0 || securityIncarnation == Guid.Empty ||
            !security.HandshakeCompleted || security.HandshakeState != SessionHandshakeState.Verified ||
            !security.HasCapability(CapabilityGrant.FileTransfer, now) ||
            security.SessionId is not { } session || string.IsNullOrWhiteSpace(session.Value) ||
            security.HelpeeAddress is not { } helpee || string.IsNullOrWhiteSpace(helpee.Value) ||
            security.HelperAddress is not { } helper || string.IsNullOrWhiteSpace(helper.Value) ||
            helpee == helper || security.ApprovalExpiresAt is not { } expires)
        {
            return PreparationRejected(state, "preparation_scope_unavailable");
        }

        var scope = new FileTransferSessionPreparationScope(state.ServiceInstanceId,
            attachmentGeneration, securityGeneration, securityIncarnation, session.Value,
            helpee.Value, helper.Value, expires);
        if (scope == state.Scope && state.Phase is FileTransferSessionPreparationPhase.Eligible or
                FileTransferSessionPreparationPhase.Pending or FileTransferSessionPreparationPhase.Ready)
        {
            return new(state, FileTransferSessionPreparationStatus.Idempotent, "preparation_scope_current");
        }
        if (securityGeneration <= state.LastSecurityGeneration ||
            securityIncarnation == state.LastSecurityIncarnation)
        {
            return PreparationRejected(state, "preparation_security_incarnation_retired", stale: true);
        }

        var next = ClearSessionPreparation(state) with
        {
            Scope = scope,
            LastSecurityGeneration = securityGeneration,
            LastSecurityIncarnation = securityIncarnation,
            Phase = FileTransferSessionPreparationPhase.Eligible,
        };
        return new(next, FileTransferSessionPreparationStatus.Accepted, "preparation_scope_registered");
    }

    internal static FileTransferSessionPreparationDecision BeginSessionPreparation(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationScope scope,
        string nonce, long payerDecisionId, bool localIsPayer, DateTimeOffset deadlineUtc,
        DateTimeOffset now)
    {
        if (!IsPreparationScopeCurrent(state, scope, now) || string.IsNullOrWhiteSpace(nonce) ||
            payerDecisionId <= 0)
        {
            return PreparationRejected(state, "preparation_request_invalid");
        }
        if (state.Permit is { } current && state.Phase is
            FileTransferSessionPreparationPhase.Pending or FileTransferSessionPreparationPhase.Ready)
        {
            var exact = current.Nonce == nonce && current.PayerDecisionId == payerDecisionId &&
                current.LocalIsPayer == localIsPayer && current.DeadlineUtc == deadlineUtc;
            var live = IsSessionPreparationPermitCurrent(state, current, now);
            return exact && live
                ? new(state, FileTransferSessionPreparationStatus.Idempotent, "preparation_request_coalesced", current)
                : PreparationRejected(state, "preparation_episode_already_exists");
        }
        if (state.Phase != FileTransferSessionPreparationPhase.Eligible ||
            deadlineUtc <= now || deadlineUtc > now + SessionPreparationHandshakeLifetime ||
            deadlineUtc > scope.ApprovalExpiresUtc)
        {
            return PreparationRejected(state, "preparation_not_eligible");
        }
        var permit = new FileTransferSessionPreparationPermit(scope,
            checked(state.LastPreparationGeneration + 1), state.AdmissionGeneration,
            nonce, payerDecisionId, localIsPayer, now, deadlineUtc);
        return new(state with
        {
            LastPreparationGeneration = permit.Generation,
            Permit = permit,
            Phase = FileTransferSessionPreparationPhase.Pending,
        }, FileTransferSessionPreparationStatus.Accepted, "preparation_permitted", permit);
    }

    internal static FileTransferSessionPreparationDecision CompleteSessionPreparation(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationPermit permit,
        FileTransferSessionPreparationProof proof, DateTimeOffset now)
    {
        if (!IsSessionPreparationPermitCurrent(state, permit, now))
        {
            return PreparationRejected(state, "preparation_callback_stale", stale: true);
        }
        if (state.Phase == FileTransferSessionPreparationPhase.Ready && state.Readiness is { } existing)
        {
            return existing.Proof == proof && now < existing.ExpiresUtc
                ? new(state, FileTransferSessionPreparationStatus.Idempotent, "preparation_completion_coalesced", permit)
                : PreparationRejected(state, "preparation_completion_changed", stale: true);
        }
        if (state.Phase != FileTransferSessionPreparationPhase.Pending || now >= permit.DeadlineUtc ||
            proof.Nonce != permit.Nonce || proof.PayerDecisionId != permit.PayerDecisionId ||
            proof.OfferGeneration <= 0 || proof.SnapshotGeneration <= 0 || proof.LeaseGeneration <= 0 ||
            string.IsNullOrWhiteSpace(proof.ListenerRunId) || !proof.LeaseCurrent || !proof.ListenerReady ||
            !proof.FileLaneNegotiated || proof.ObservedUtc < permit.IssuedUtc || proof.ObservedUtc > now ||
            (permit.LocalIsPayer ? !proof.AnswerReceived || !proof.AnswerAckDispatched : !proof.AnswerAckReceived))
        {
            return PreparationRejected(state, "preparation_handshake_incomplete");
        }
        var expires = proof.ObservedUtc + SessionPreparationReadinessLifetime;
        if (expires > permit.Scope.ApprovalExpiresUtc) expires = permit.Scope.ApprovalExpiresUtc;
        return new(state with
        {
            Phase = FileTransferSessionPreparationPhase.Ready,
            Readiness = new(proof, expires),
        }, FileTransferSessionPreparationStatus.Accepted, "preparation_ready", permit);
    }

    internal static FileTransferSessionPreparationDecision RetireSessionPreparationEpisode(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationPermit permit)
    {
        if (!ReferenceEquals(state.Permit, permit) || state.Phase is not
            (FileTransferSessionPreparationPhase.Pending or FileTransferSessionPreparationPhase.Ready))
        {
            return PreparationRejected(state, "preparation_retirement_stale", stale: true);
        }
        return new(ClearSessionPreparation(state) with { Phase = FileTransferSessionPreparationPhase.Eligible },
            FileTransferSessionPreparationStatus.Accepted, "preparation_episode_retired");
    }

    internal static FileTransferSessionPreparationDecision RevokeSessionPreparationScope(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationScope scope)
        => state.Scope != scope
            ? PreparationRejected(state, "preparation_revocation_stale", stale: true)
            : new(ClearSessionPreparation(state) with { Phase = FileTransferSessionPreparationPhase.Revoked },
                FileTransferSessionPreparationStatus.Accepted, "preparation_scope_revoked");

    internal static FileTransferSessionPreparationDecision ReserveSessionPreparationAdmission(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationScope scope,
        string transferId, FileTransferDirection direction, DateTimeOffset now)
    {
        if (!IsPreparationScopeCurrent(state, scope, now) || string.IsNullOrWhiteSpace(transferId) ||
            !IsPreparationDirectionValid(direction))
        {
            return PreparationRejected(state, "preparation_admission_invalid");
        }
        if (state.Admission is { } existing)
        {
            return existing.TransferId == transferId && existing.Direction == direction
                ? new(state, FileTransferSessionPreparationStatus.Idempotent, "preparation_admission_coalesced", Admission: existing)
                : PreparationRejected(state, "preparation_first_admission_already_reserved");
        }
        var readiness = state.Phase == FileTransferSessionPreparationPhase.Ready &&
            state.Readiness is { } ready && now >= ready.Proof.ObservedUtc && now < ready.ExpiresUtc ? ready : null;
        var admission = new FileTransferSessionPreparationAdmission(scope,
            checked(state.AdmissionGeneration + 1), transferId, direction, now, readiness);
        return new(ClearSessionPreparation(state) with
        {
            AdmissionGeneration = admission.Generation,
            Admission = admission,
            Phase = FileTransferSessionPreparationPhase.AdmissionReserved,
        }, FileTransferSessionPreparationStatus.Accepted, "preparation_admission_reserved", Admission: admission);
    }

    internal static FileTransferSessionPreparationDecision ClaimSessionPreparationReadiness(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationAdmission admission,
        FileTransferSessionPreparationLeaseObservation lease, DateTimeOffset now)
    {
        if (!ReferenceEquals(state.Admission, admission) ||
            state.Phase != FileTransferSessionPreparationPhase.AdmissionReserved ||
            !IsPreparationScopeCurrent(state, admission.Scope, now) ||
            admission.Readiness is not { } ready || now < admission.ReservedUtc ||
            !IsPreparationLeaseCurrent(ready, admission.Scope, lease, admission.ReservedUtc, now))
        {
            return PreparationRejected(state, "preparation_claim_not_current", stale: true);
        }
        if (state.ClaimedEvidence is { } existing)
        {
            return new(state, FileTransferSessionPreparationStatus.Idempotent,
                "preparation_claim_coalesced", Admission: admission, Evidence: existing);
        }
        var proof = ready.Proof;
        // An exact ACK on the dialer proves the peer received its answer. Neither
        // an answer-pending marker nor transport send acceptance alone reaches here.
        var evidence = new FileTransferInitialTunaReadinessEvidence(admission.Scope.SessionId,
            admission.TransferId, admission.Direction, now, proof.LeaseGeneration, proof.ListenerRunId,
            LeaseCurrent: true, ListenerReady: true, proof.OfferGeneration,
            PeerVisibleProof: true, AnswerReceived: true, proof.AnswerAckReceived,
            TunaNegotiatedObserved: true, SnapshotGeneration: proof.SnapshotGeneration,
            SourceCapturedUtc: proof.ObservedUtc);
        return new(state with { ClaimedEvidence = evidence }, FileTransferSessionPreparationStatus.Accepted,
            "preparation_readiness_claimed", Admission: admission, Evidence: evidence);
    }

    internal static FileTransferSessionPreparationObservation ObserveSessionPreparation(
        FileTransferSessionPreparationState state, FileTransferSessionPreparationScope scope,
        FileTransferDirection direction, FileTransferSessionPreparationLeaseObservation lease,
        DateTimeOffset requestedUtc, DateTimeOffset now)
    {
        var readiness = state.Readiness ?? state.Admission?.Readiness;
        var ready = IsPreparationDirectionValid(direction) && IsPreparationScopeCurrent(state, scope, now) &&
            state.Phase == FileTransferSessionPreparationPhase.Ready && readiness is not null &&
            requestedUtc >= readiness.Proof.ObservedUtc && requestedUtc <= now &&
            IsPreparationLeaseCurrent(readiness, scope, lease, requestedUtc, now);
        return new(ready, ready ? "preparation_ready" : "preparation_not_ready", state.Phase, direction,
            readiness?.Proof.Nonce, state.LastPreparationGeneration, readiness?.Proof.SnapshotGeneration ?? 0,
            readiness?.Proof.LeaseGeneration ?? 0, readiness?.Proof.ObservedUtc ?? default,
            readiness?.ExpiresUtc ?? default, state.ClaimedEvidence is not null);
    }

    internal static bool IsSessionPreparationPermitCurrent(FileTransferSessionPreparationState state,
        FileTransferSessionPreparationPermit permit, DateTimeOffset now)
        => IsPreparationScopeCurrent(state, permit.Scope, now) && ReferenceEquals(state.Permit, permit) &&
            permit.AdmissionGeneration == state.AdmissionGeneration && now >= permit.IssuedUtc &&
            (state.Phase == FileTransferSessionPreparationPhase.Pending && now < permit.DeadlineUtc ||
             state.Phase == FileTransferSessionPreparationPhase.Ready && state.Readiness is { } ready &&
             now >= ready.Proof.ObservedUtc && now < ready.ExpiresUtc);

    private static bool IsPreparationScopeCurrent(FileTransferSessionPreparationState state,
        FileTransferSessionPreparationScope scope, DateTimeOffset now)
        => state.Attached && state.Scope == scope && state.Phase is not
            (FileTransferSessionPreparationPhase.Unavailable or FileTransferSessionPreparationPhase.Revoked) &&
            now < scope.ApprovalExpiresUtc;

    private static bool IsPreparationLeaseCurrent(FileTransferSessionPreparationReadiness ready,
        FileTransferSessionPreparationScope scope, FileTransferSessionPreparationLeaseObservation lease,
        DateTimeOffset notBefore, DateTimeOffset now)
        => lease.Scope == scope && lease.SnapshotGeneration == ready.Proof.SnapshotGeneration &&
            lease.LeaseGeneration == ready.Proof.LeaseGeneration && lease.ListenerRunId == ready.Proof.ListenerRunId &&
            lease.LeaseCurrent && lease.ListenerReady && lease.FileLaneNegotiated &&
            lease.ObservedUtc >= notBefore && lease.ObservedUtc <= now && now >= ready.Proof.ObservedUtc &&
            now < ready.ExpiresUtc;

    private static bool IsPreparationDirectionValid(FileTransferDirection direction)
        => direction is FileTransferDirection.Inbound or FileTransferDirection.Outbound;

    private static FileTransferSessionPreparationState ClearSessionPreparation(FileTransferSessionPreparationState state)
        => state with { Permit = null, Readiness = null, Admission = null, ClaimedEvidence = null };

    private static FileTransferSessionPreparationDecision PreparationRejected(
        FileTransferSessionPreparationState state, string reason, bool stale = false)
        => new(state, stale ? FileTransferSessionPreparationStatus.Stale : FileTransferSessionPreparationStatus.Rejected, reason);
}
