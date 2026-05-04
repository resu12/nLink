using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionAuthorizationGuardTests
{
    private static readonly DateTimeOffset NowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);

    [Theory]
    [InlineData(SessionCapability.Chat, CapabilityGrant.Chat)]
    [InlineData(SessionCapability.ScreenShare, CapabilityGrant.ScreenShare)]
    [InlineData(SessionCapability.RemoteControl, CapabilityGrant.RemoteControl)]
    [InlineData(SessionCapability.FileTransfer, CapabilityGrant.FileTransfer)]
    [InlineData(SessionCapability.Clipboard, CapabilityGrant.Clipboard)]
    public void Evaluate_Authorizes_EachGrantedCapability(SessionCapability capability, CapabilityGrant grantFlag)
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(grantFlag);
        var grant = CreateGrant(state, grantFlag, NowUtc.AddMinutes(5));

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            capability: capability);

        Assert.True(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.None, result.Failure);
    }

    [Theory]
    [InlineData(false, true, true, true, SessionAuthorizationFailure.SecurityTransportRequired)]
    [InlineData(true, false, true, true, SessionAuthorizationFailure.InviteNotValidated)]
    [InlineData(true, true, false, true, SessionAuthorizationFailure.HandshakeIncomplete)]
    [InlineData(true, true, true, false, SessionAuthorizationFailure.ApprovalMissing)]
    public void Evaluate_Denies_MissingRuntimePrerequisites(
        bool hasSecurityTransport,
        bool inviteValidated,
        bool handshakeCompleted,
        bool approvalGranted,
        SessionAuthorizationFailure expectedFailure)
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(CapabilityGrant.Chat) with
        {
            InviteValidated = inviteValidated,
            HandshakeCompleted = handshakeCompleted,
            HandshakeState = handshakeCompleted ? SessionHandshakeState.Verified : SessionHandshakeState.Pending,
            ApprovalGranted = approvalGranted,
            ApprovedCapabilities = approvalGranted ? CapabilityGrant.Chat : CapabilityGrant.None,
            ApprovalExpiresAt = approvalGranted ? NowUtc.AddMinutes(5) : null,
        };
        var grant = approvalGranted ? CreateGrant(state, CapabilityGrant.Chat, NowUtc.AddMinutes(5)) : null;

        var result = guard.Evaluate(
            hasSecurityTransport,
            state,
            grant,
            SessionCapability.Chat);

        Assert.False(result.IsAuthorized);
        Assert.Equal(expectedFailure, result.Failure);
    }

    [Fact]
    public void Evaluate_Denies_ExpiredApproval()
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(CapabilityGrant.Chat, expiresAtUtc: NowUtc);
        var grant = CreateGrant(state, CapabilityGrant.Chat, NowUtc);

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            capability: SessionCapability.Chat);

        Assert.False(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.Expired, result.Failure);
    }

    [Fact]
    public void Evaluate_Denies_MissingCapability()
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(CapabilityGrant.Chat);
        var grant = CreateGrant(state, CapabilityGrant.Chat, NowUtc.AddMinutes(5));

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            capability: SessionCapability.FileTransfer);

        Assert.False(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.CapabilityMissing, result.Failure);
    }

    [Fact]
    public void Evaluate_Denies_SessionMismatch()
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(CapabilityGrant.Chat);
        var grant = new SessionGrant(
            state.HelperAddress!.Value,
            CapabilityGrant.Chat,
            new SessionId("authorization_other_session"),
            NowUtc.AddMinutes(5));

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            capability: SessionCapability.Chat);

        Assert.False(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.SessionMismatch, result.Failure);
    }

    [Fact]
    public void Evaluate_Denies_HelperIdentityMismatch()
    {
        var guard = new SessionAuthorizationGuard(() => NowUtc);
        var state = CreateApprovedSecurityState(CapabilityGrant.Chat);
        var grant = new SessionGrant(
            new PeerAddress("authorization.other.helper"),
            CapabilityGrant.Chat,
            state.SessionId!.Value,
            NowUtc.AddMinutes(5));

        var result = guard.Evaluate(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            capability: SessionCapability.Chat);

        Assert.False(result.IsAuthorized);
        Assert.Equal(SessionAuthorizationFailure.HelperIdentityMismatch, result.Failure);
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        CapabilityGrant capabilities,
        DateTimeOffset? expiresAtUtc = null)
    {
        var sessionId = new SessionId("authorization_guard_session");
        var helpeeAddress = new PeerAddress("authorization.helpee");
        var helperAddress = new PeerAddress("authorization.helper");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
            HandshakeCompleted = true,
            HandshakeState = SessionHandshakeState.Verified,
        }).WithApproval(new SessionGrant(
            helperAddress,
            capabilities,
            sessionId,
            expiresAtUtc ?? NowUtc.AddMinutes(5)));
    }

    private static SessionGrant CreateGrant(
        SessionSecurityState state,
        CapabilityGrant capabilities,
        DateTimeOffset expiresAtUtc)
    {
        return new SessionGrant(
            state.HelperAddress!.Value,
            capabilities,
            state.SessionId!.Value,
            expiresAtUtc);
    }
}
