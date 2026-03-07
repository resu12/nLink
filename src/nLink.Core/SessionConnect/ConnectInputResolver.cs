namespace NLink.Core.SessionConnect;

public enum ConnectInputValidationError
{
    None = 0,
    Empty = 1,
    InvalidAddress = 2,
    InvalidInviteToken = 3,
    ExpiredInviteToken = 4,
    UnsupportedInput = 5,
}

public sealed record ConnectInputResolution(
    bool IsValid,
    ConnectInputKind Kind,
    PeerAddress? TargetAddress = null,
    ValidatedInviteV1? Invite = null,
    ConnectInputValidationError Error = ConnectInputValidationError.None,
    InviteTokenValidationError InviteValidationError = InviteTokenValidationError.None,
    InviteTokenParseError InviteParseError = InviteTokenParseError.None,
    string? Message = null)
{
    public static ConnectInputResolution ForPeerAddress(PeerAddress address)
        => new(
            IsValid: true,
            Kind: ConnectInputKind.PeerAddress,
            TargetAddress: address,
            Error: ConnectInputValidationError.None);

    public static ConnectInputResolution ForInvite(ValidatedInviteV1 invite)
        => new(
            IsValid: true,
            Kind: ConnectInputKind.InviteToken,
            TargetAddress: invite.TargetAddress,
            Invite: invite,
            Error: ConnectInputValidationError.None);

    public static ConnectInputResolution Invalid(
        ConnectInputValidationError error,
        string message,
        InviteTokenValidationError inviteValidationError = InviteTokenValidationError.None,
        InviteTokenParseError inviteParseError = InviteTokenParseError.None)
        => new(
            IsValid: false,
            Kind: ConnectInputKind.Unknown,
            Error: error,
            InviteValidationError: inviteValidationError,
            InviteParseError: inviteParseError,
            Message: message);
}

public interface IConnectInputResolver
{
    ConnectInputResolution Resolve(string? input, DateTimeOffset nowUtc);
}

public sealed class ConnectInputResolver : IConnectInputResolver
{
    private readonly IInviteTokenCodec inviteCodec;
    private readonly IInviteTokenValidator inviteValidator;

    public ConnectInputResolver(IInviteTokenCodec inviteCodec, IInviteTokenValidator inviteValidator)
    {
        this.inviteCodec = inviteCodec ?? throw new ArgumentNullException(nameof(inviteCodec));
        this.inviteValidator = inviteValidator ?? throw new ArgumentNullException(nameof(inviteValidator));
    }

    public ConnectInputResolution Resolve(string? input, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ConnectInputResolution.Invalid(
                ConnectInputValidationError.Empty,
                "Enter an NKN address or invite token.");
        }

        var normalized = InviteQrPayload.ExtractTokenOrOriginal(input);

        if (LooksLikeInviteToken(normalized))
        {
            var parsed = inviteCodec.Parse(normalized);
            if (!parsed.IsSuccess || parsed.Envelope is null)
            {
                return ConnectInputResolution.Invalid(
                    ConnectInputValidationError.InvalidInviteToken,
                    parsed.Message ?? "Invite token format is invalid.",
                    InviteTokenValidationError.ParseFailed,
                    parsed.Error);
            }

            var validation = inviteValidator.Validate(parsed.Envelope, nowUtc);
            if (!validation.IsSuccess || validation.Invite is null)
            {
                var error = validation.Error == InviteTokenValidationError.Expired
                    ? ConnectInputValidationError.ExpiredInviteToken
                    : ConnectInputValidationError.InvalidInviteToken;
                return ConnectInputResolution.Invalid(
                    error,
                    validation.Message ?? "Invite token is invalid.",
                    validation.Error,
                    validation.ParseError);
            }

            return ConnectInputResolution.ForInvite(validation.Invite);
        }

        if (PeerAddress.TryParse(normalized, out var address))
        {
            return ConnectInputResolution.ForPeerAddress(address);
        }

        return ConnectInputResolution.Invalid(
            ConnectInputValidationError.UnsupportedInput,
            "Input is not a valid NKN address or invite token.");
    }

    private static bool LooksLikeInviteToken(string value)
    {
        return value.StartsWith(InviteTokenCodec.TokenPrefix + ".", StringComparison.Ordinal);
    }
}
