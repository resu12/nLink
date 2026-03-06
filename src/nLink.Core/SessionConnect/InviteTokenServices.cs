using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core.Logging;

namespace NLink.Core.SessionConnect;

public enum InviteTokenParseError
{
    None = 0,
    EmptyInput = 1,
    InvalidFormat = 2,
    UnsupportedPrefix = 3,
    InvalidPayloadEncoding = 4,
    InvalidSignatureEncoding = 5,
    InvalidPayloadJson = 6,
    InvalidPayload = 7,
    UnsupportedVersion = 8,
}

public sealed record InviteTokenParseResult(
    bool IsSuccess,
    InviteTokenParseError Error,
    InviteTokenEnvelopeV1? Envelope = null,
    string? Message = null)
{
    public static InviteTokenParseResult Success(InviteTokenEnvelopeV1 envelope)
        => new(true, InviteTokenParseError.None, envelope, null);

    public static InviteTokenParseResult Failure(InviteTokenParseError error, string message)
        => new(false, error, null, message);
}

public enum InviteValidationResult
{
    Valid = 0,
    Expired = 1,
    InvalidSignature = 2,
    Malformed = 3,
    ReplayDetected = 4,
    UnsupportedVersion = 5,
    Revoked = 6,
}

public enum InviteTokenValidationError
{
    None = 0,
    ParseFailed = 1,
    SignatureInvalid = 2,
    Expired = 3,
    ReplayDetected = 4,
    UnsupportedVersion = 5,
    Revoked = 6,
}

public enum InviteValidationMode
{
    InspectOnly = 0,
    ConsumeIfValid = 1,
}

public sealed record InviteTokenValidationResult(
    bool IsSuccess,
    InviteValidationResult Result,
    ValidatedInviteV1? Invite = null,
    InviteTokenParseError ParseError = InviteTokenParseError.None,
    string? Message = null)
{
    public InviteTokenValidationError Error => Result switch
    {
        InviteValidationResult.Valid => InviteTokenValidationError.None,
        InviteValidationResult.Expired => InviteTokenValidationError.Expired,
        InviteValidationResult.InvalidSignature => InviteTokenValidationError.SignatureInvalid,
        InviteValidationResult.ReplayDetected => InviteTokenValidationError.ReplayDetected,
        InviteValidationResult.UnsupportedVersion => InviteTokenValidationError.UnsupportedVersion,
        InviteValidationResult.Revoked => InviteTokenValidationError.Revoked,
        _ => InviteTokenValidationError.ParseFailed,
    };

    public static InviteTokenValidationResult Success(ValidatedInviteV1 invite)
        => new(true, InviteValidationResult.Valid, invite, InviteTokenParseError.None, null);

    public static InviteTokenValidationResult Failure(
        InviteValidationResult result,
        string message,
        InviteTokenParseError parseError = InviteTokenParseError.None)
        => new(false, result, null, parseError, message);
}

public enum InviteTokenCreateError
{
    None = 0,
    InvalidRequest = 1,
    InvalidPayload = 2,
    SerializationFailed = 3,
    Throttled = 4,
}

public sealed record InviteTokenCreateResult(
    bool IsSuccess,
    InviteTokenCreateError Error,
    string? Token = null,
    InvitePayloadV1? Payload = null,
    string? Message = null)
{
    public static InviteTokenCreateResult Success(string token, InvitePayloadV1 payload)
        => new(true, InviteTokenCreateError.None, token, payload, null);

    public static InviteTokenCreateResult Failure(InviteTokenCreateError error, string message)
        => new(false, error, null, null, message);
}

public enum InviteExpiryValidationError
{
    None = 0,
    Expired = 1,
}

public sealed record InviteExpiryValidationResult(
    bool IsValid,
    InviteExpiryValidationError Error,
    string? Message = null)
{
    public static InviteExpiryValidationResult Valid()
        => new(true, InviteExpiryValidationError.None, null);

    public static InviteExpiryValidationResult Invalid(InviteExpiryValidationError error, string message)
        => new(false, error, message);
}

public sealed record InviteTokenEnvelopeV1(
    InvitePayloadV1 Payload,
    byte[] PayloadUtf8,
    byte[] SignatureBytes,
    string RawToken);

public interface IInviteTokenCodec
{
    InviteTokenParseResult Parse(string? token);
    string Serialize(InviteTokenEnvelopeV1 envelope);
}

public interface IInviteSignatureService
{
    byte[] Sign(ReadOnlySpan<byte> payloadUtf8);
    bool Verify(ReadOnlySpan<byte> payloadUtf8, ReadOnlySpan<byte> signatureBytes);
}

public interface IInviteExpiryValidator
{
    InviteExpiryValidationResult Validate(InvitePayloadV1 payload, DateTimeOffset nowUtc);
}

public interface IInviteTokenFactory
{
    InviteTokenCreateResult Create(InviteTokenCreateRequest request, DateTimeOffset nowUtc);
}

public interface IInviteTokenValidator
{
    InviteTokenValidationResult Validate(string? token, DateTimeOffset nowUtc, InviteValidationMode validationMode = InviteValidationMode.InspectOnly);
    InviteTokenValidationResult Validate(InviteTokenEnvelopeV1 envelope, DateTimeOffset nowUtc, InviteValidationMode validationMode = InviteValidationMode.InspectOnly);
}

public interface IInviteReplayCache
{
    bool TryReserve(InvitePayloadV1 payload, DateTimeOffset nowUtc);
}

public sealed class InMemoryInviteReplayCache : IInviteReplayCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> reservations = new(StringComparer.Ordinal);

    public bool TryReserve(InvitePayloadV1 payload, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        var key = BuildReservationKey(payload);

        lock (gate)
        {
            RemoveExpiredReservations(nowUtcMs);

            if (reservations.TryGetValue(key, out var reservedUntilUtcMs) &&
                reservedUntilUtcMs > nowUtcMs)
            {
                return false;
            }

            reservations[key] = payload.ExpiresAtUtcMs;
            return true;
        }
    }

    private void RemoveExpiredReservations(long nowUtcMs)
    {
        foreach (var reservation in reservations.ToArray())
        {
            if (reservation.Value <= nowUtcMs)
            {
                reservations.Remove(reservation.Key);
            }
        }
    }

    private static string BuildReservationKey(InvitePayloadV1 payload)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{payload.Version}|{payload.IssuerAddress.Value}|{payload.TargetAddress.Value}|{payload.SessionId.Value}|{payload.Nonce}");
    }
}

public static class InviteValidationResultExtensions
{
    public static string ToFailureCode(this InviteValidationResult result)
    {
        return result switch
        {
            InviteValidationResult.Expired => "invite_expired",
            InviteValidationResult.InvalidSignature => "invite_signature_invalid",
            InviteValidationResult.Malformed => "invite_malformed",
            InviteValidationResult.ReplayDetected => "invite_replay_detected",
            InviteValidationResult.UnsupportedVersion => "invite_unsupported_version",
            InviteValidationResult.Revoked => "invite_revoked",
            _ => "invite_validation_failed",
        };
    }
}

public sealed class InviteTokenCodec : IInviteTokenCodec
{
    public const string TokenPrefix = "nlinki1";

    public InviteTokenParseResult Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return InviteTokenParseResult.Failure(InviteTokenParseError.EmptyInput, "Invite token is required.");
        }

        var normalized = token.Trim();
        var parts = normalized.Split('.', StringSplitOptions.None);
        if (parts.Length != 3)
        {
            return InviteTokenParseResult.Failure(InviteTokenParseError.InvalidFormat, "Invite token format is invalid.");
        }

        if (!string.Equals(parts[0], TokenPrefix, StringComparison.Ordinal))
        {
            return InviteTokenParseResult.Failure(InviteTokenParseError.UnsupportedPrefix, "Invite token version is not supported.");
        }

        if (!InviteTokenBase64Url.TryDecode(parts[1], out var payloadUtf8))
        {
            return InviteTokenParseResult.Failure(InviteTokenParseError.InvalidPayloadEncoding, "Invite token payload encoding is invalid.");
        }

        if (!InviteTokenBase64Url.TryDecode(parts[2], out var signatureBytes))
        {
            return InviteTokenParseResult.Failure(InviteTokenParseError.InvalidSignatureEncoding, "Invite token signature encoding is invalid.");
        }

        var payloadResult = InviteTokenPayloadJson.TryDeserialize(payloadUtf8);
        if (!payloadResult.IsSuccess)
        {
            return InviteTokenParseResult.Failure(payloadResult.Error, payloadResult.Message ?? "Invite payload is invalid.");
        }

        var envelope = new InviteTokenEnvelopeV1(
            payloadResult.Payload!,
            payloadUtf8,
            signatureBytes,
            normalized);
        return InviteTokenParseResult.Success(envelope);
    }

    public string Serialize(InviteTokenEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.PayloadUtf8);
        ArgumentNullException.ThrowIfNull(envelope.SignatureBytes);

        return $"{TokenPrefix}.{InviteTokenBase64Url.Encode(envelope.PayloadUtf8)}.{InviteTokenBase64Url.Encode(envelope.SignatureBytes)}";
    }
}

public sealed class HmacSha256InviteSignatureService : IInviteSignatureService
{
    private readonly byte[] key;

    public HmacSha256InviteSignatureService(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Invite signature key must not be empty.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public byte[] Sign(ReadOnlySpan<byte> payloadUtf8)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payloadUtf8.ToArray());
    }

    public bool Verify(ReadOnlySpan<byte> payloadUtf8, ReadOnlySpan<byte> signatureBytes)
    {
        if (signatureBytes.IsEmpty)
        {
            return false;
        }

        var expected = Sign(payloadUtf8);
        return CryptographicOperations.FixedTimeEquals(expected, signatureBytes);
    }
}

public sealed class InviteExpiryValidator : IInviteExpiryValidator
{
    public InviteExpiryValidationResult Validate(InvitePayloadV1 payload, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        if (nowUtcMs >= payload.ExpiresAtUtcMs)
        {
            return InviteExpiryValidationResult.Invalid(
                InviteExpiryValidationError.Expired,
                "Invite token has expired.");
        }

        return InviteExpiryValidationResult.Valid();
    }
}

public sealed class InviteTokenFactory : IInviteTokenFactory
{
    private readonly IInviteTokenCodec codec;
    private readonly IInviteSignatureService signatureService;
    private readonly IInviteIssueTracker? issueTracker;

    public InviteTokenFactory(IInviteTokenCodec codec, IInviteSignatureService signatureService, IInviteIssueTracker? issueTracker = null)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.signatureService = signatureService ?? throw new ArgumentNullException(nameof(signatureService));
        this.issueTracker = issueTracker;
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

        if (issueTracker is not null &&
            !issueTracker.TryRegisterIssued(payload, nowUtc, out var failureReason))
        {
            return InviteTokenCreateResult.Failure(
                InviteTokenCreateError.Throttled,
                failureReason ?? "Invite issuance is throttled.");
        }

        var payloadBytes = InviteTokenPayloadJson.Serialize(payload);
        var signatureBytes = signatureService.Sign(payloadBytes);
        var token = codec.Serialize(new InviteTokenEnvelopeV1(payload, payloadBytes, signatureBytes, RawToken: string.Empty));
        return InviteTokenCreateResult.Success(token, payload);
    }

    private static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return InviteTokenBase64Url.Encode(bytes);
    }
}

public sealed class InviteTokenValidator : IInviteTokenValidator
{
    private readonly IInviteTokenCodec codec;
    private readonly IInviteSignatureService signatureService;
    private readonly IInviteExpiryValidator expiryValidator;
    private readonly IInviteReplayCache? replayCache;
    private readonly IInviteRevocationStore? revocationStore;

    public InviteTokenValidator(
        IInviteTokenCodec codec,
        IInviteSignatureService signatureService,
        IInviteExpiryValidator expiryValidator,
        IInviteReplayCache? replayCache = null,
        IInviteRevocationStore? revocationStore = null)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.signatureService = signatureService ?? throw new ArgumentNullException(nameof(signatureService));
        this.expiryValidator = expiryValidator ?? throw new ArgumentNullException(nameof(expiryValidator));
        this.replayCache = replayCache;
        this.revocationStore = revocationStore;
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

        if (!signatureService.Verify(envelope.PayloadUtf8, envelope.SignatureBytes))
        {
            var failure = InviteTokenValidationResult.Failure(
                InviteValidationResult.InvalidSignature,
                "Invite token signature verification failed.");
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

        if (revocationStore is not null && revocationStore.IsRevoked(envelope.Payload, nowUtc))
        {
            var failure = InviteTokenValidationResult.Failure(
                InviteValidationResult.Revoked,
                "Invite token has been revoked.");
            LogValidationFailure(failure, validationMode, envelope.Payload);
            return failure;
        }

        if (validationMode == InviteValidationMode.ConsumeIfValid &&
            replayCache is not null &&
            !replayCache.TryReserve(envelope.Payload, nowUtc))
        {
            var failure = InviteTokenValidationResult.Failure(
                InviteValidationResult.ReplayDetected,
                "Invite token was already used.");
            LogValidationFailure(failure, validationMode, envelope.Payload);
            return failure;
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

internal static class InviteTokenPayloadJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Serialize(InvitePayloadV1 payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var wire = new InvitePayloadWireV1
        {
            Version = payload.Version,
            IssuerAddress = payload.IssuerAddress.Value,
            TargetAddress = payload.TargetAddress.Value,
            SessionId = payload.SessionId.Value,
            Capabilities = (int)payload.Capabilities,
            IssuedAtUtcMs = payload.IssuedAtUtcMs,
            ExpiresAtUtcMs = payload.ExpiresAtUtcMs,
            Nonce = payload.Nonce,
            BoundHelperAddress = payload.BoundHelperAddress?.Value,
        };

        return JsonSerializer.SerializeToUtf8Bytes(wire, JsonOptions);
    }

    public static InvitePayloadParseResult TryDeserialize(byte[] utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            return InvitePayloadParseResult.Failure(
                InviteTokenParseError.InvalidPayloadJson,
                "Invite payload JSON is empty.");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<InvitePayloadWireV1>(utf8Json, JsonOptions);
            if (parsed is null)
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.InvalidPayloadJson,
                    "Invite payload JSON could not be parsed.");
            }

            if (parsed.Version != InvitePayloadV1.CurrentVersion)
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.UnsupportedVersion,
                    $"Invite payload version '{parsed.Version}' is not supported.");
            }

            if (!PeerAddress.TryParse(parsed.IssuerAddress, out var issuerAddress))
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.InvalidPayload,
                    "Invite payload issuer address is invalid.");
            }

            if (!PeerAddress.TryParse(parsed.TargetAddress, out var targetAddress))
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.InvalidPayload,
                    "Invite payload target address is invalid.");
            }

            if (!SessionId.TryParse(parsed.SessionId, out var sessionId))
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.InvalidPayload,
                    "Invite payload session id is invalid.");
            }

            PeerAddress? boundHelperAddress = null;
            if (!string.IsNullOrWhiteSpace(parsed.BoundHelperAddress))
            {
                if (!PeerAddress.TryParse(parsed.BoundHelperAddress.Trim(), out var parsedBoundHelperAddress))
                {
                    return InvitePayloadParseResult.Failure(
                        InviteTokenParseError.InvalidPayload,
                        "Invite payload helper address is invalid.");
                }

                boundHelperAddress = parsedBoundHelperAddress;
            }

            var payload = new InvitePayloadV1
            {
                Version = parsed.Version,
                IssuerAddress = issuerAddress,
                TargetAddress = targetAddress,
                SessionId = sessionId,
                Capabilities = (InviteCapabilities)parsed.Capabilities,
                IssuedAtUtcMs = parsed.IssuedAtUtcMs,
                ExpiresAtUtcMs = parsed.ExpiresAtUtcMs,
                Nonce = parsed.Nonce?.Trim() ?? string.Empty,
                BoundHelperAddress = boundHelperAddress,
            };

            var payloadValidation = InvitePayloadV1.Validate(payload);
            if (!payloadValidation.IsValid)
            {
                return InvitePayloadParseResult.Failure(
                    InviteTokenParseError.InvalidPayload,
                    payloadValidation.Message ?? "Invite payload validation failed.");
            }

            return InvitePayloadParseResult.Success(payload);
        }
        catch (JsonException)
        {
            return InvitePayloadParseResult.Failure(
                InviteTokenParseError.InvalidPayloadJson,
                "Invite payload JSON format is invalid.");
        }
    }

    internal sealed class InvitePayloadWireV1
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("iss")]
        public string IssuerAddress { get; init; } = string.Empty;

        [JsonPropertyName("tgt")]
        public string TargetAddress { get; init; } = string.Empty;

        [JsonPropertyName("sid")]
        public string SessionId { get; init; } = string.Empty;

        [JsonPropertyName("cap")]
        public int Capabilities { get; init; }

        [JsonPropertyName("iat")]
        public long IssuedAtUtcMs { get; init; }

        [JsonPropertyName("exp")]
        public long ExpiresAtUtcMs { get; init; }

        [JsonPropertyName("n")]
        public string? Nonce { get; init; }

        [JsonPropertyName("hlp")]
        public string? BoundHelperAddress { get; init; }
    }
}

internal readonly record struct InvitePayloadParseResult(
    bool IsSuccess,
    InviteTokenParseError Error,
    InvitePayloadV1? Payload = null,
    string? Message = null)
{
    public static InvitePayloadParseResult Success(InvitePayloadV1 payload)
        => new(true, InviteTokenParseError.None, payload, null);

    public static InvitePayloadParseResult Failure(InviteTokenParseError error, string message)
        => new(false, error, null, message);
}

internal static class InviteTokenBase64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            case 0:
                break;
            default:
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
