using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

[Flags]
public enum CapabilityGrant
{
    None = 0,
    Chat = 1 << 0,
    ScreenShare = 1 << 1,
    RemoteControl = 1 << 2,
    FileTransfer = 1 << 3,
    Clipboard = 1 << 4,
}

public sealed record ApprovalRequest(
    PeerAddress HelperIdentity,
    CapabilityGrant RequestedCapabilities,
    SessionId SessionId)
{
    public ApprovalDecision CreateDecision(
        CapabilityGrant approvedCapabilities,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset nowUtc)
    {
        if (approvedCapabilities == CapabilityGrant.None)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedCapabilities), "Approval must grant at least one capability.");
        }

        if ((approvedCapabilities & ~SessionSecurityDefaults.AllCapabilityGrants) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedCapabilities), "Approval contains unsupported capabilities.");
        }

        if ((approvedCapabilities & ~RequestedCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedCapabilities), "Approval cannot grant capabilities that were not requested.");
        }

        if ((approvedCapabilities & CapabilityGrant.RemoteControl) == CapabilityGrant.RemoteControl &&
            (approvedCapabilities & CapabilityGrant.ScreenShare) != CapabilityGrant.ScreenShare)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedCapabilities), "Remote control approval requires screen sharing approval.");
        }

        if (expiresAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Approval must expire in the future.");
        }

        return new ApprovalDecision(
            ApprovedCapabilities: approvedCapabilities,
            ExpiresAtUtc: expiresAtUtc,
            HelperIdentity: HelperIdentity,
            SessionId: SessionId);
    }
}

public sealed record ApprovalDecision(
    CapabilityGrant ApprovedCapabilities,
    DateTimeOffset ExpiresAtUtc,
    PeerAddress HelperIdentity,
    SessionId SessionId)
{
    public SessionGrant ToGrant()
        => new(HelperIdentity, ApprovedCapabilities, SessionId, ExpiresAtUtc);
}

public enum SessionHandshakeState
{
    None = 0,
    Pending = 1,
    ChallengeIssued = 2,
    Verified = 3,
    Failed = 4,
    Expired = 5,
    Invalidated = 6,
}

public enum SessionSecureMessageFamily
{
    Chat = 1,
    RemoteControl = 2,
    ScreenShare = 3,
    Lifecycle = 4,
    FileTransfer = 5,
    Clipboard = 6,
}

public sealed record SessionGrant(
    PeerAddress HelperIdentity,
    CapabilityGrant Capabilities,
    SessionId SessionId,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsExpired(DateTimeOffset nowUtc)
        => nowUtc >= ExpiresAtUtc;

    public bool Matches(SessionId sessionId, PeerAddress helperIdentity)
        => SessionId == sessionId && HelperIdentity == helperIdentity;

    public bool Permits(
        CapabilityGrant capability,
        SessionId sessionId,
        PeerAddress helperIdentity,
        DateTimeOffset nowUtc)
    {
        if (!Matches(sessionId, helperIdentity) || IsExpired(nowUtc))
        {
            return false;
        }

        return capability == CapabilityGrant.None || (Capabilities & capability) == capability;
    }
}

public sealed record SessionSecurityState
{
    public static SessionSecurityState Empty { get; } = new();

    public SessionId? SessionId { get; init; }

    public PeerAddress? HelpeeAddress { get; init; }

    public PeerAddress? HelperAddress { get; init; }

    public bool InviteValidated { get; init; }

    public bool HandshakeCompleted { get; init; }

    public bool ApprovalGranted { get; init; }

    public CapabilityGrant ApprovedCapabilities { get; init; }

    public DateTimeOffset? ApprovalExpiresAt { get; init; }

    public SessionHandshakeState HandshakeState { get; init; }

    public DateTimeOffset? HandshakeExpiresAt { get; init; }

    public string? HandshakeFailureReason { get; init; }

    public bool IsApprovalActive(DateTimeOffset nowUtc)
    {
        return ApprovalGranted &&
               ApprovalExpiresAt is DateTimeOffset expiresAtUtc &&
               nowUtc < expiresAtUtc;
    }

    public bool HasCapability(CapabilityGrant capability, DateTimeOffset nowUtc)
    {
        return capability == CapabilityGrant.None
            ? IsApprovalActive(nowUtc)
            : IsApprovalActive(nowUtc) && (ApprovedCapabilities & capability) == capability;
    }

    public static SessionSecurityState CreateHelpeeWaiting(PeerAddress helpeeAddress)
    {
        return Empty with
        {
            HelpeeAddress = helpeeAddress,
        };
    }

    public static SessionSecurityState CreateHelperPending(
        SessionId sessionId,
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        bool inviteValidated)
    {
        return Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = inviteValidated,
            HandshakeState = SessionHandshakeState.Pending,
        };
    }

    public SessionSecurityState WithHandshakeChallenge(
        SessionId sessionId,
        PeerAddress helpeeAddress,
        PeerAddress? helperAddress,
        bool inviteValidated,
        DateTimeOffset expiresAtUtc)
    {
        return this with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = inviteValidated,
            HandshakeCompleted = false,
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
            HandshakeState = SessionHandshakeState.ChallengeIssued,
            HandshakeExpiresAt = expiresAtUtc,
            HandshakeFailureReason = null,
        };
    }

    public SessionSecurityState WithHandshakeVerified(PeerAddress helperAddress)
    {
        return this with
        {
            HelperAddress = helperAddress,
            HandshakeCompleted = true,
            HandshakeState = SessionHandshakeState.Verified,
            HandshakeExpiresAt = null,
            HandshakeFailureReason = null,
        };
    }

    public SessionSecurityState WithHandshakeFailure(SessionHandshakeState state, string? reason)
    {
        var failureState = state is SessionHandshakeState.Failed or SessionHandshakeState.Expired or SessionHandshakeState.Invalidated
            ? state
            : SessionHandshakeState.Failed;

        return this with
        {
            HandshakeCompleted = false,
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
            HandshakeState = failureState,
            HandshakeFailureReason = NormalizeReason(reason),
            HandshakeExpiresAt = null,
        };
    }

    public SessionSecurityState WithApproval(SessionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return this with
        {
            SessionId = grant.SessionId,
            HelperAddress = grant.HelperIdentity,
            ApprovalGranted = true,
            ApprovedCapabilities = grant.Capabilities,
            ApprovalExpiresAt = grant.ExpiresAtUtc,
        };
    }

    public SessionSecurityState WithoutApproval()
    {
        return this with
        {
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
        };
    }

    public SessionSecurityState WithApprovalExpired()
    {
        return WithoutApproval().WithHandshakeFailure(SessionHandshakeState.Invalidated, "approval_expired");
    }

    public SessionSecurityState Invalidate(string? reason)
    {
        return this with
        {
            HandshakeCompleted = false,
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
            HandshakeState = SessionHandshakeState.Invalidated,
            HandshakeExpiresAt = null,
            HandshakeFailureReason = NormalizeReason(reason),
        };
    }

    private static string? NormalizeReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}
