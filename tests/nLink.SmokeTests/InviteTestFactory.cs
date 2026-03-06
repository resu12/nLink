using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

internal static class InviteTestFactory
{
    public static (string RawToken, ValidatedInviteV1 Invite) CreateValidatedInvite(
        PeerAddress targetAddress,
        InviteCapabilities capabilities = InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl,
        SessionId? sessionId = null,
        PeerAddress? boundHelperAddress = null)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: targetAddress,
                TargetAddress: targetAddress,
                SessionId: sessionId ?? new SessionId($"sess_test_{Guid.NewGuid():N}"),
                Capabilities: capabilities,
                Lifetime: TimeSpan.FromMinutes(5),
                BoundHelperAddress: boundHelperAddress),
            nowUtc);

        if (!create.IsSuccess || create.Token is null)
        {
            throw new InvalidOperationException(create.Message ?? "Failed to create validated invite.");
        }

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validate = validator.Validate(create.Token, nowUtc.AddSeconds(1));
        if (!validate.IsSuccess || validate.Invite is null)
        {
            throw new InvalidOperationException(validate.Message ?? "Failed to validate generated invite.");
        }

        return (create.Token, validate.Invite);
    }
}
