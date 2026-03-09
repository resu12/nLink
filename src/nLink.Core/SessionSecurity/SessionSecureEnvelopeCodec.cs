using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.Chat;
using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public sealed record SessionSecureEnvelopeMetadata(
    SessionSecureMessageFamily Family,
    string MessageType,
    SessionId SessionId,
    PeerAddress SenderIdentity,
    long Sequence,
    string? RequestId);

public sealed record SessionSecureEnvelopeExpectation(
    SessionSecureMessageFamily? Family = null,
    string? MessageType = null,
    SessionId? SessionId = null,
    PeerAddress? SenderIdentity = null,
    string? RequestId = null);

public sealed record SessionSecureEnvelopePayload(
    SessionSecureEnvelopeMetadata Metadata,
    byte[] Plaintext);

public static class SessionSecureEnvelopeCodec
{
    public const int Version = 1;

    private const int MaxMessageTypeLength = 64;
    private const int MaxRequestIdLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Encrypt(
        byte[] key,
        SessionSecureEnvelopeMetadata metadata,
        ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        ValidateMetadata(metadata);

        var wire = new SessionSecureEnvelopeWire
        {
            Version = Version,
            Family = (int)metadata.Family,
            MessageType = metadata.MessageType.Trim(),
            SessionId = metadata.SessionId.Value,
            SenderIdentity = metadata.SenderIdentity.Value,
            Sequence = metadata.Sequence,
            RequestId = NormalizeOptional(metadata.RequestId),
            NonceBase64 = string.Empty,
            TagBase64 = string.Empty,
            CiphertextBase64 = string.Empty,
        };

        var nonce = new byte[ChatAesGcmCrypto.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ChatAesGcmCrypto.TagSize];

        using var aes = new AesGcm(key, ChatAesGcmCrypto.TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAuthenticatedData(wire));

        wire.NonceBase64 = Convert.ToBase64String(nonce);
        wire.TagBase64 = Convert.ToBase64String(tag);
        wire.CiphertextBase64 = Convert.ToBase64String(ciphertext);
        return JsonSerializer.SerializeToUtf8Bytes(wire, JsonOptions);
    }

    public static SessionSecureEnvelopePayload Decrypt(
        byte[] key,
        ReadOnlySpan<byte> encoded,
        SessionSecureEnvelopeExpectation? expectation = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var wire = DeserializeWire(encoded);
        var metadata = ParseMetadata(wire);
        ValidateExpectation(metadata, expectation);

        byte[] nonce;
        byte[] tag;
        byte[] ciphertext;
        try
        {
            nonce = Convert.FromBase64String(wire.NonceBase64);
            tag = Convert.FromBase64String(wire.TagBase64);
            ciphertext = Convert.FromBase64String(wire.CiphertextBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("session_secure_payload_invalid_encoding", ex);
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, ChatAesGcmCrypto.TagSize);
        aes.Decrypt(
            nonce,
            ciphertext,
            tag,
            plaintext,
            BuildAuthenticatedData(wire));

        return new SessionSecureEnvelopePayload(metadata, plaintext);
    }

    private static SessionSecureEnvelopeWire DeserializeWire(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            throw new CryptographicException("session_secure_payload_missing");
        }

        try
        {
            var wire = JsonSerializer.Deserialize<SessionSecureEnvelopeWire>(encoded, JsonOptions);
            if (wire is null ||
                wire.Version != Version ||
                string.IsNullOrWhiteSpace(wire.MessageType) ||
                string.IsNullOrWhiteSpace(wire.SessionId) ||
                string.IsNullOrWhiteSpace(wire.SenderIdentity) ||
                string.IsNullOrWhiteSpace(wire.NonceBase64) ||
                string.IsNullOrWhiteSpace(wire.TagBase64) ||
                string.IsNullOrWhiteSpace(wire.CiphertextBase64))
            {
                throw new CryptographicException("session_secure_payload_invalid");
            }

            return wire;
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("session_secure_payload_invalid", ex);
        }
    }

    private static SessionSecureEnvelopeMetadata ParseMetadata(SessionSecureEnvelopeWire wire)
    {
        if (!Enum.IsDefined(typeof(SessionSecureMessageFamily), wire.Family))
        {
            throw new CryptographicException("session_secure_family_invalid");
        }

        if (!SessionId.TryParse(wire.SessionId, out var sessionId))
        {
            throw new CryptographicException("session_secure_session_id_invalid");
        }

        if (!PeerAddress.TryParse(wire.SenderIdentity, out var senderIdentity))
        {
            throw new CryptographicException("session_secure_sender_identity_invalid");
        }

        var messageType = wire.MessageType.Trim();
        var requestId = NormalizeOptional(wire.RequestId);
        var metadata = new SessionSecureEnvelopeMetadata(
            (SessionSecureMessageFamily)wire.Family,
            messageType,
            sessionId,
            senderIdentity,
            wire.Sequence,
            requestId);
        ValidateMetadata(metadata);
        return metadata;
    }

    private static void ValidateMetadata(SessionSecureEnvelopeMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.MessageType) ||
            metadata.MessageType.Trim().Length > MaxMessageTypeLength)
        {
            throw new ArgumentException("session_secure_message_type_invalid", nameof(metadata));
        }

        if (metadata.Sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "session_secure_sequence_invalid");
        }

        var normalizedRequestId = NormalizeOptional(metadata.RequestId);
        if (normalizedRequestId is not null && normalizedRequestId.Length > MaxRequestIdLength)
        {
            throw new ArgumentException("session_secure_request_id_invalid", nameof(metadata));
        }
    }

    private static void ValidateExpectation(
        SessionSecureEnvelopeMetadata metadata,
        SessionSecureEnvelopeExpectation? expectation)
    {
        if (expectation is null)
        {
            return;
        }

        if (expectation.Family.HasValue && expectation.Family.Value != metadata.Family)
        {
            throw new InvalidOperationException("session_secure_family_mismatch");
        }

        if (!string.IsNullOrWhiteSpace(expectation.MessageType) &&
            !string.Equals(expectation.MessageType.Trim(), metadata.MessageType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("session_secure_message_type_mismatch");
        }

        if (expectation.SessionId.HasValue && expectation.SessionId.Value != metadata.SessionId)
        {
            throw new InvalidOperationException("session_secure_session_id_mismatch");
        }

        if (expectation.SenderIdentity.HasValue && expectation.SenderIdentity.Value != metadata.SenderIdentity)
        {
            throw new InvalidOperationException("session_secure_sender_identity_mismatch");
        }

        var expectedRequestId = NormalizeOptional(expectation.RequestId);
        if (expectedRequestId is not null &&
            !string.Equals(expectedRequestId, NormalizeOptional(metadata.RequestId), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("session_secure_request_id_mismatch");
        }
    }

    private static byte[] BuildAuthenticatedData(SessionSecureEnvelopeWire wire)
    {
        var requestId = NormalizeOptional(wire.RequestId) ?? string.Empty;
        var aadText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{wire.Version}\u001f{wire.Family}\u001f{wire.MessageType.Trim()}\u001f{wire.SessionId.Trim()}\u001f{wire.SenderIdentity.Trim()}\u001f{wire.Sequence}\u001f{requestId}");
        return Encoding.UTF8.GetBytes(aadText);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class SessionSecureEnvelopeWire
    {
        public int Version { get; set; }

        public int Family { get; set; }

        public string MessageType { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string SenderIdentity { get; set; } = string.Empty;

        public long Sequence { get; set; }

        public string? RequestId { get; set; }

        public string NonceBase64 { get; set; } = string.Empty;

        public string TagBase64 { get; set; } = string.Empty;

        public string CiphertextBase64 { get; set; } = string.Empty;
    }
}
