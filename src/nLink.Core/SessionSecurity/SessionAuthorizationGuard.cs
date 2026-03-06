namespace NLink.Core.SessionSecurity;

public sealed class SessionAuthorizationGuard
{
    private readonly Func<DateTimeOffset> nowProvider;

    public SessionAuthorizationGuard(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public SessionAuthorizationResult Evaluate(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant,
        SessionCapability capability)
    {
        ArgumentNullException.ThrowIfNull(securityState);

        if (!hasSecurityTransport)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.SecurityTransportRequired);
        }

        if (!securityState.InviteValidated)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.InviteNotValidated);
        }

        if (!securityState.HandshakeCompleted ||
            securityState.HandshakeState != SessionHandshakeState.Verified)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.HandshakeIncomplete);
        }

        if (!securityState.ApprovalGranted)
        {
            return SessionAuthorizationResult.Denied(capability, SessionAuthorizationFailure.ApprovalMissing);
        }

        var nowUtc = nowProvider();
        var requiredGrant = SessionAuthorizationService.ToCapabilityGrant(capability);
        if (!securityState.HasCapability(requiredGrant, nowUtc))
        {
            return SessionAuthorizationResult.Denied(
                capability,
                securityState.ApprovalExpiresAt is DateTimeOffset expiresAtUtc && nowUtc >= expiresAtUtc
                    ? SessionAuthorizationFailure.Expired
                    : SessionAuthorizationFailure.CapabilityMissing);
        }

        return SessionAuthorizationService.Evaluate(
            grant,
            securityState.SessionId,
            securityState.HelperAddress,
            nowUtc,
            capability);
    }
}
