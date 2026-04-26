using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
public sealed class ClipboardSecurityGuardTests
{
    [Fact]
    public void AuthorizeSync_DeniesWhenClipboardCapabilityMissing()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionClipboardGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.Chat);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.Chat);

        var result = guard.AuthorizeSync(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant);

        Assert.False(result.IsAllowed);
        Assert.Equal(ClipboardValidationFailure.AuthorizationDenied, result.Failure);
        Assert.Equal(SessionAuthorizationFailure.CapabilityMissing, result.AuthorizationFailure);
    }

    [Fact]
    public void ValidateTransfer_RejectsSessionIdMismatch()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionClipboardGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.Clipboard);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.Clipboard);

        var result = guard.ValidateTransfer(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            descriptor: new ClipboardTransferDescriptor(
                new SessionId("clipboard_other_session"),
                state.HelperAddress!.Value,
                TextLength: 16));

        Assert.False(result.IsAllowed);
        Assert.Equal(ClipboardValidationFailure.SessionIdMismatch, result.Failure);
    }

    [Fact]
    public void ValidateTransfer_RejectsHelperIdentityMismatch()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionClipboardGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.Clipboard);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.Clipboard);

        var result = guard.ValidateTransfer(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            descriptor: new ClipboardTransferDescriptor(
                state.SessionId!.Value,
                new PeerAddress("clipboard.unexpected.helper"),
                TextLength: 16));

        Assert.False(result.IsAllowed);
        Assert.Equal(ClipboardValidationFailure.HelperIdentityMismatch, result.Failure);
    }

    [Fact]
    public void ValidateTransfer_RejectsOversizedClipboardText()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_200_000_000);
        var guard = new SessionClipboardGuard(() => nowUtc);
        var state = CreateApprovedSecurityState(nowUtc, CapabilityGrant.Clipboard);
        var grant = CreateGrant(state, nowUtc, CapabilityGrant.Clipboard);

        var result = guard.ValidateTransfer(
            hasSecurityTransport: true,
            securityState: state,
            grant: grant,
            descriptor: new ClipboardTransferDescriptor(
                state.SessionId!.Value,
                state.HelperAddress!.Value,
                ClipboardTransferDefaults.DefaultMaxTextLength + 1));

        Assert.False(result.IsAllowed);
        Assert.Equal(ClipboardValidationFailure.TextTooLarge, result.Failure);
    }

    private static SessionSecurityState CreateApprovedSecurityState(DateTimeOffset nowUtc, CapabilityGrant capabilities)
    {
        var sessionId = new SessionId("clipboard_guard_session");
        var helpeeAddress = new PeerAddress("clipboard.guard.helpee");
        var helperAddress = new PeerAddress("clipboard.guard.helper");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(
              helperAddress,
              capabilities,
              sessionId,
              nowUtc.AddMinutes(5)));
    }

    private static SessionGrant CreateGrant(SessionSecurityState state, DateTimeOffset nowUtc, CapabilityGrant capabilities)
    {
        return new SessionGrant(
            state.HelperAddress!.Value,
            capabilities,
            state.SessionId!.Value,
            nowUtc.AddMinutes(5));
    }
}
