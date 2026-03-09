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
    string? InviteTokenText = null,
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

    public static ConnectInputResolution ForInvite(ValidatedInviteV1 invite, string inviteTokenText)
        => new(
            IsValid: true,
            Kind: ConnectInputKind.InviteToken,
            TargetAddress: invite.TargetAddress,
            Invite: invite,
            InviteTokenText: inviteTokenText,
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
    private readonly IInviteExpiryValidator inviteExpiryValidator;

    public ConnectInputResolver(IInviteTokenCodec inviteCodec, IInviteExpiryValidator inviteExpiryValidator)
    {
        this.inviteCodec = inviteCodec ?? throw new ArgumentNullException(nameof(inviteCodec));
        this.inviteExpiryValidator = inviteExpiryValidator ?? throw new ArgumentNullException(nameof(inviteExpiryValidator));
    }

    public ConnectInputResolution Resolve(string? input, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ConnectInputResolution.Invalid(
                ConnectInputValidationError.Empty,
                "Enter an NKN address, invite, or invite code.");
        }

        var normalized = InviteQrPayload.ExtractTokenOrOriginal(input);
        if (LooksLikeInviteShareCode(normalized))
        {
            var shareCode = InviteShareCodeCodec.Decode(normalized);
            if (!shareCode.IsSuccess || string.IsNullOrWhiteSpace(shareCode.InviteToken))
            {
                return ConnectInputResolution.Invalid(
                    ConnectInputValidationError.InvalidInviteToken,
                    shareCode.Message ?? "Invite code format is invalid.",
                    InviteTokenValidationError.ParseFailed,
                    InviteTokenParseError.InvalidFormat);
            }

            normalized = shareCode.InviteToken;
        }

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

            // Helper-side resolution is intentionally limited to parse, payload-shape, and expiry checks.
            // Invite authenticity and one-time consumption are enforced by the helpee during handshake.
            var payloadValidation = InvitePayloadV1.Validate(parsed.Envelope.Payload);
            if (!payloadValidation.IsValid)
            {
                var inviteValidationError = payloadValidation.Error == InvitePayloadValidationError.UnsupportedVersion
                    ? InviteTokenValidationError.UnsupportedVersion
                    : InviteTokenValidationError.ParseFailed;
                return ConnectInputResolution.Invalid(
                    ConnectInputValidationError.InvalidInviteToken,
                    payloadValidation.Message ?? "Invite token contents are invalid.",
                    inviteValidationError,
                    payloadValidation.Error == InvitePayloadValidationError.UnsupportedVersion
                        ? InviteTokenParseError.UnsupportedVersion
                        : InviteTokenParseError.InvalidPayload);
            }

            var expiry = inviteExpiryValidator.Validate(parsed.Envelope.Payload, nowUtc);
            if (!expiry.IsValid)
            {
                return ConnectInputResolution.Invalid(
                    ConnectInputValidationError.ExpiredInviteToken,
                    expiry.Message ?? "Invite token has expired.",
                    InviteTokenValidationError.Expired);
            }

            return ConnectInputResolution.ForInvite(
                new ValidatedInviteV1(
                    parsed.Envelope.Payload,
                    parsed.Envelope.Payload.TargetAddress,
                    parsed.Envelope.Payload.IssuerAddress,
                    parsed.Envelope.Payload.SessionId),
                normalized);
        }

        if (PeerAddress.TryParse(normalized, out var address))
        {
            return ConnectInputResolution.ForPeerAddress(address);
        }

        return ConnectInputResolution.Invalid(
            ConnectInputValidationError.UnsupportedInput,
            "Input is not a valid NKN address, invite, or invite code.");
    }

    private static bool LooksLikeInviteToken(string value)
    {
        return value.StartsWith(InviteTokenCodec.TokenPrefix + ".", StringComparison.Ordinal);
    }

    private static bool LooksLikeInviteShareCode(string value)
    {
        return value.StartsWith(InviteShareCodeCodec.CodePrefix + "-", StringComparison.OrdinalIgnoreCase);
    }
}
