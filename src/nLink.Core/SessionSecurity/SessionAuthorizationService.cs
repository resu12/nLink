using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public enum SessionCapability
{
    Chat = 1,
    ScreenShare = 2,
    RemoteControl = 3,
    FileTransfer = 4,
    Clipboard = 5,
}

public enum SessionAuthorizationFailure
{
    None = 0,
    SecurityTransportRequired = 1,
    InviteNotValidated = 2,
    HandshakeIncomplete = 3,
    ApprovalMissing = 4,
    SessionIdMissing = 5,
    HelperIdentityMissing = 6,
    SessionMismatch = 7,
    HelperIdentityMismatch = 8,
    Expired = 9,
    CapabilityMissing = 10,
}

public readonly record struct SessionAuthorizationResult(
    SessionCapability Capability,
    bool IsAuthorized,
    SessionAuthorizationFailure Failure)
{
    public static SessionAuthorizationResult Authorized(SessionCapability capability)
        => new(capability, true, SessionAuthorizationFailure.None);

    public static SessionAuthorizationResult Denied(SessionCapability capability, SessionAuthorizationFailure failure)
        => new(capability, false, failure);
}

public static class SessionAuthorizationService
{
    public static SessionAuthorizationResult Evaluate(
        SessionGrant? grant,
        SessionId? sessionId,
        PeerAddress? helperIdentity,
        DateTimeOffset nowUtc,
        SessionCapability capability)
    {
        if (grant is null)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.ApprovalMissing);
        }

        if (sessionId is null)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.SessionIdMissing);
        }

        if (helperIdentity is null)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.HelperIdentityMissing);
        }

        if (grant.SessionId != sessionId)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.SessionMismatch);
        }

        if (grant.HelperIdentity != helperIdentity)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.HelperIdentityMismatch);
        }

        if (grant.IsExpired(nowUtc))
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.Expired);
        }

        var requiredGrant = ToCapabilityGrant(capability);
        var effectiveSessionId = sessionId.Value;
        var effectiveHelperIdentity = helperIdentity.Value;
        if (!grant.Permits(requiredGrant, effectiveSessionId, effectiveHelperIdentity, nowUtc))
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.CapabilityMissing);
        }

        return SessionAuthorizationResult.Authorized(capability);
    }

    public static CapabilityGrant ToCapabilityGrant(SessionCapability capability)
    {
        return capability switch
        {
            SessionCapability.Chat => CapabilityGrant.Chat,
            SessionCapability.ScreenShare => CapabilityGrant.ScreenShare,
            SessionCapability.RemoteControl => CapabilityGrant.RemoteControl,
            SessionCapability.FileTransfer => CapabilityGrant.FileTransfer,
            SessionCapability.Clipboard => CapabilityGrant.Clipboard,
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported session capability."),
        };
    }
}
