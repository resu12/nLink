using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.SessionConnect;

public enum InviteIssuedTokenConsumeResultKind
{
    Valid = 0,
    InvalidProof = 1,
    ReplayDetected = 2,
    Revoked = 3,
}

public readonly record struct InviteIssuedTokenConsumeResult(
    InviteIssuedTokenConsumeResultKind Result,
    string? Message = null)
{
    public static InviteIssuedTokenConsumeResult Valid()
        => new(InviteIssuedTokenConsumeResultKind.Valid);

    public static InviteIssuedTokenConsumeResult InvalidProof(string? message = null)
        => new(InviteIssuedTokenConsumeResultKind.InvalidProof, message ?? "Invite token proof is invalid.");

    public static InviteIssuedTokenConsumeResult ReplayDetected(string? message = null)
        => new(InviteIssuedTokenConsumeResultKind.ReplayDetected, message ?? "Invite token was already used.");

    public static InviteIssuedTokenConsumeResult Revoked(string? message = null)
        => new(InviteIssuedTokenConsumeResultKind.Revoked, message ?? "Invite token has been revoked.");
}

public sealed class IssuedSecretInviteTokenFactory : IInviteTokenFactory
{
    private readonly IInviteTokenCodec codec;
    private readonly IInviteIssuedTokenStore issuedTokenStore;

    public IssuedSecretInviteTokenFactory(IInviteTokenCodec codec, IInviteIssuedTokenStore issuedTokenStore)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.issuedTokenStore = issuedTokenStore ?? throw new ArgumentNullException(nameof(issuedTokenStore));
    }

    public InviteTokenCreateResult Create(InviteTokenCreateRequest request, DateTimeOffset nowUtc)
    {
        if (request.Lifetime <= TimeSpan.Zero)
        {
            return InviteTokenCreateResult.Failure(
                InviteTokenCreateError.InvalidRequest,
                "Invite lifetime must be greater than zero.");
        }

        var issuedAtUtcMs = nowUtc.ToUnixTimeMilliseconds();
        var expiresAtUtcMs = nowUtc.Add(request.Lifetime).ToUnixTimeMilliseconds();
        var payload = new InvitePayloadV1
        {
            Version = InvitePayloadV1.CurrentVersion,
            IssuerAddress = request.IssuerAddress,
            TargetAddress = request.TargetAddress,
            SessionId = request.SessionId,
            Capabilities = request.Capabilities,
            IssuedAtUtcMs = issuedAtUtcMs,
            ExpiresAtUtcMs = expiresAtUtcMs,
            Nonce = CreateNonce(),
            BoundHelperAddress = request.BoundHelperAddress,
        };

        var payloadValidation = InvitePayloadV1.Validate(payload);
        if (!payloadValidation.IsValid)
        {
            return InviteTokenCreateResult.Failure(
                InviteTokenCreateError.InvalidPayload,
                payloadValidation.Message ?? "Invite payload is invalid.");
        }

        var payloadBytes = InviteTokenPayloadJson.Serialize(payload);
        var verificationBytes = InviteIssuedSecretProof.Create();
        if (!issuedTokenStore.TryRegisterIssuedToken(payload, verificationBytes, nowUtc, out var failureReason))
        {
            return InviteTokenCreateResult.Failure(
                InviteTokenCreateError.Throttled,
                failureReason ?? "Invite issuance could not be secured.");
        }

        var token = codec.Serialize(new InviteTokenEnvelopeV1(payload, payloadBytes, verificationBytes, RawToken: string.Empty));
        return InviteTokenCreateResult.Success(token, payload);
    }

    private static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return InviteTokenBase64Url.Encode(bytes);
    }
}

public sealed class IssuedSecretInviteTokenValidator : IInviteTokenValidator
{
    private readonly IInviteTokenCodec codec;
    private readonly IInviteExpiryValidator expiryValidator;
    private readonly IInviteIssuedTokenStore? issuedTokenStore;

    public IssuedSecretInviteTokenValidator(
        IInviteTokenCodec codec,
        IInviteExpiryValidator expiryValidator,
        IInviteIssuedTokenStore? issuedTokenStore = null)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.expiryValidator = expiryValidator ?? throw new ArgumentNullException(nameof(expiryValidator));
        this.issuedTokenStore = issuedTokenStore;
    }

    public InviteTokenValidationResult Validate(string? token, DateTimeOffset nowUtc, InviteValidationMode validationMode = InviteValidationMode.InspectOnly)
    {
        var parsed = codec.Parse(token);
        if (!parsed.IsSuccess || parsed.Envelope is null)
        {
            var failure = InviteTokenValidationResult.Failure(
                ClassifyParseFailure(parsed.Error),
                parsed.Message ?? "Invite token parse failed.",
                parsed.Error);
            LogValidationFailure(failure, validationMode, payload: null);
            return failure;
        }

        return Validate(parsed.Envelope, nowUtc, validationMode);
    }

    public InviteTokenValidationResult Validate(InviteTokenEnvelopeV1 envelope, DateTimeOffset nowUtc, InviteValidationMode validationMode = InviteValidationMode.InspectOnly)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payloadValidation = InvitePayloadV1.Validate(envelope.Payload);
        if (!payloadValidation.IsValid)
        {
            var failure = InviteTokenValidationResult.Failure(
                payloadValidation.Error == InvitePayloadValidationError.UnsupportedVersion
                    ? InviteValidationResult.UnsupportedVersion
                    : InviteValidationResult.Malformed,
                payloadValidation.Message ?? "Invite payload validation failed.",
                payloadValidation.Error == InvitePayloadValidationError.UnsupportedVersion
                    ? InviteTokenParseError.UnsupportedVersion
                    : InviteTokenParseError.InvalidPayload);
            LogValidationFailure(failure, validationMode, envelope.Payload);
            return failure;
        }

        if (!InviteIssuedSecretProof.IsWellFormed(envelope.SignatureBytes))
        {
            var failure = InviteTokenValidationResult.Failure(
                InviteValidationResult.InvalidSignature,
                "Invite token proof encoding is invalid.");
            LogValidationFailure(failure, validationMode, envelope.Payload);
            return failure;
        }

        var expiry = expiryValidator.Validate(envelope.Payload, nowUtc);
        if (!expiry.IsValid)
        {
            var failure = InviteTokenValidationResult.Failure(
                InviteValidationResult.Expired,
                expiry.Message ?? "Invite token has expired.");
            LogValidationFailure(failure, validationMode, envelope.Payload);
            return failure;
        }

        if (validationMode == InviteValidationMode.ConsumeIfValid)
        {
            var consume = issuedTokenStore?.ConsumeIssuedToken(envelope.Payload, envelope.SignatureBytes, nowUtc)
                ?? InviteIssuedTokenConsumeResult.InvalidProof("Invite security store is unavailable.");
            if (consume.Result != InviteIssuedTokenConsumeResultKind.Valid)
            {
                var failure = InviteTokenValidationResult.Failure(
                    consume.Result switch
                    {
                        InviteIssuedTokenConsumeResultKind.ReplayDetected => InviteValidationResult.ReplayDetected,
                        InviteIssuedTokenConsumeResultKind.Revoked => InviteValidationResult.Revoked,
                        _ => InviteValidationResult.InvalidSignature,
                    },
                    consume.Message ?? "Invite token validation failed.");
                LogValidationFailure(failure, validationMode, envelope.Payload);
                return failure;
            }
        }

        var validatedInvite = new ValidatedInviteV1(
            envelope.Payload,
            envelope.Payload.TargetAddress,
            envelope.Payload.IssuerAddress,
            envelope.Payload.SessionId);
        LogValidationSuccess(validationMode, envelope.Payload);
        return InviteTokenValidationResult.Success(validatedInvite);
    }

    private static InviteValidationResult ClassifyParseFailure(InviteTokenParseError parseError)
    {
        return parseError switch
        {
            InviteTokenParseError.UnsupportedPrefix or InviteTokenParseError.UnsupportedVersion => InviteValidationResult.UnsupportedVersion,
            _ => InviteValidationResult.Malformed,
        };
    }

    private static void LogValidationSuccess(InviteValidationMode validationMode, InvitePayloadV1 payload)
    {
        if (validationMode != InviteValidationMode.ConsumeIfValid)
        {
            return;
        }

        LocalOperationalLog.Info(
            "InviteValidation",
            $"result=Valid; mode={validationMode}; session_id={payload.SessionId.Value}; target={payload.TargetAddress.Value}; exp_utc_ms={payload.ExpiresAtUtcMs}");
    }

    private static void LogValidationFailure(InviteTokenValidationResult result, InviteValidationMode validationMode, InvitePayloadV1? payload)
    {
        var sessionId = payload?.SessionId.Value ?? "(none)";
        var target = payload?.TargetAddress.Value ?? "(none)";
        LocalOperationalLog.Warn(
            "InviteValidation",
            $"result={result.Result}; mode={validationMode}; parse_error={result.ParseError}; session_id={sessionId}; target={target}; message={result.Message ?? "(none)"}");
    }
}

internal static class InviteIssuedSecretProof
{
    private const int ProofBytesLength = 32;

    public static byte[] Create()
    {
        var bytes = new byte[ProofBytesLength];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public static bool IsWellFormed(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == ProofBytesLength;
    }

    public static string ComputeHashKey(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return InviteTokenBase64Url.Encode(hash);
    }
}
