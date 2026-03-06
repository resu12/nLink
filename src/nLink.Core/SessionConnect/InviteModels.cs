namespace NLink.Core.SessionConnect;

public enum ConnectInputKind
{
    Unknown = 0,
    PeerAddress = 1,
    InviteToken = 2,
}

[Flags]
public enum InviteCapabilities
{
    None = 0,
    Chat = 1 << 0,
    ScreenShare = 1 << 1,
    RemoteControl = 1 << 2,
    FileTransfer = 1 << 3,
}

public enum InvitePayloadValidationError
{
    None = 0,
    UnsupportedVersion = 1,
    InvalidIssuerAddress = 2,
    InvalidTargetAddress = 3,
    InvalidSessionId = 4,
    InvalidIssuedAt = 5,
    InvalidExpiry = 6,
    InvalidNonce = 7,
    InvalidHelperAddress = 8,
}

public readonly record struct InvitePayloadValidationResult(
    bool IsValid,
    InvitePayloadValidationError Error,
    string? Message = null)
{
    public static InvitePayloadValidationResult Valid()
        => new(true, InvitePayloadValidationError.None, null);

    public static InvitePayloadValidationResult Invalid(InvitePayloadValidationError error, string message)
        => new(false, error, message);
}

public sealed record InvitePayloadV1
{
    public const int CurrentVersion = 1;
    public const int MaxNonceLength = 128;
    public const int MinNonceBytes = 8;
    public const int MaxNonceBytes = 64;

    public int Version { get; init; } = CurrentVersion;
    public PeerAddress IssuerAddress { get; init; }
    public PeerAddress TargetAddress { get; init; }
    public SessionId SessionId { get; init; }
    public InviteCapabilities Capabilities { get; init; } = InviteCapabilities.None;
    public long IssuedAtUtcMs { get; init; }
    public long ExpiresAtUtcMs { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public PeerAddress? BoundHelperAddress { get; init; }

    public static InvitePayloadValidationResult Validate(InvitePayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Version != CurrentVersion)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.UnsupportedVersion,
                $"Invite payload version '{payload.Version}' is not supported.");
        }

        var issuerValidation = PeerAddress.Validate(payload.IssuerAddress.Value);
        if (!issuerValidation.IsValid)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidIssuerAddress,
                issuerValidation.Message ?? "Invite issuer address is invalid.");
        }

        var targetValidation = PeerAddress.Validate(payload.TargetAddress.Value);
        if (!targetValidation.IsValid)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidTargetAddress,
                targetValidation.Message ?? "Invite target address is invalid.");
        }

        var sessionIdValidation = SessionId.Validate(payload.SessionId.Value);
        if (!sessionIdValidation.IsValid)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidSessionId,
                sessionIdValidation.Message ?? "Invite session id is invalid.");
        }

        if (payload.IssuedAtUtcMs <= 0)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidIssuedAt,
                "Invite issue timestamp must be greater than zero.");
        }

        if (payload.ExpiresAtUtcMs <= payload.IssuedAtUtcMs)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidExpiry,
                "Invite expiry timestamp must be after issue timestamp.");
        }

        if (string.IsNullOrWhiteSpace(payload.Nonce) || payload.Nonce.Length > MaxNonceLength)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidNonce,
                "Invite nonce is missing or too long.");
        }

        if (!InviteTokenBase64Url.TryDecode(payload.Nonce, out var nonceBytes) ||
            nonceBytes.Length < MinNonceBytes ||
            nonceBytes.Length > MaxNonceBytes)
        {
            return InvitePayloadValidationResult.Invalid(
                InvitePayloadValidationError.InvalidNonce,
                "Invite nonce format is invalid.");
        }

        if (payload.BoundHelperAddress is not null)
        {
            var boundHelperAddress = payload.BoundHelperAddress.Value;
            var helperValidation = PeerAddress.Validate(boundHelperAddress.Value);
            if (!helperValidation.IsValid)
            {
                return InvitePayloadValidationResult.Invalid(
                    InvitePayloadValidationError.InvalidHelperAddress,
                    helperValidation.Message ?? "Invite helper address is invalid.");
            }
        }

        return InvitePayloadValidationResult.Valid();
    }
}

public sealed record InviteTokenCreateRequest(
    PeerAddress IssuerAddress,
    PeerAddress TargetAddress,
    SessionId SessionId,
    InviteCapabilities Capabilities,
    TimeSpan Lifetime,
    PeerAddress? BoundHelperAddress = null);

public sealed record ValidatedInviteV1(
    InvitePayloadV1 Payload,
    PeerAddress TargetAddress,
    PeerAddress IssuerAddress,
    SessionId SessionId)
{
    public PeerAddress? BoundHelperAddress => Payload.BoundHelperAddress;
}
