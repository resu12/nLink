using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core.Chat;

namespace NLink.Core.ScreenShare;

public sealed record ScreenShareMediaFrameMetadataV2(
    string SessionId,
    long Sequence);

public sealed record ScreenShareMediaFramePayloadV2(
    string SenderIdentity,
    ScreenShareFrameChunkV1 Chunk);

public static class ScreenShareMediaPacketCodec
{
    public const string ScreenShareFrameTypeV2 = "screenshare.frame.v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] EncryptFrame(
        byte[] key,
        string sessionId,
        long sequence,
        string senderIdentity,
        ScreenShareFrameChunkV1 chunk)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(chunk);

        var normalizedSessionId = NormalizeRequired(sessionId, nameof(sessionId));
        var normalizedSenderIdentity = NormalizeRequired(senderIdentity, nameof(senderIdentity));
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "screenshare_media_sequence_invalid");
        }

        if (!string.Equals(chunk.Type, ScreenShareFrameTypeV2, StringComparison.Ordinal))
        {
            throw new ArgumentException("screenshare_media_chunk_type_invalid", nameof(chunk));
        }

        if (!string.Equals(chunk.SessionId?.Trim(), normalizedSessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("screenshare_media_chunk_session_mismatch", nameof(chunk));
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new ScreenShareMediaFramePlaintextV2
            {
                SenderIdentity = normalizedSenderIdentity,
                Chunk = chunk,
            },
            JsonOptions);

        var wire = new ScreenShareMediaFrameWireV2
        {
            Kind = "screenshare",
            Type = ScreenShareFrameTypeV2,
            SessionId = normalizedSessionId,
            Sequence = sequence,
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

        return JsonSerializer.SerializeToUtf8Bytes(
            wire with
            {
                NonceBase64 = Convert.ToBase64String(nonce),
                TagBase64 = Convert.ToBase64String(tag),
                CiphertextBase64 = Convert.ToBase64String(ciphertext),
            },
            JsonOptions);
    }

    public static bool TryDeserializeFrame(
        ReadOnlySpan<byte> encoded,
        out ScreenShareMediaFrameMetadataV2 metadata)
    {
        metadata = default!;

        try
        {
            var wire = DeserializeWire(encoded);
            metadata = new ScreenShareMediaFrameMetadataV2(wire.SessionId, wire.Sequence);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecryptFrame(
        byte[] key,
        ReadOnlySpan<byte> encoded,
        out ScreenShareMediaFrameMetadataV2 metadata,
        out ScreenShareMediaFramePayloadV2 payload)
    {
        metadata = default!;
        payload = default!;

        try
        {
            var wire = DeserializeWire(encoded);
            metadata = new ScreenShareMediaFrameMetadataV2(wire.SessionId, wire.Sequence);

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
                throw new CryptographicException("screenshare_media_packet_invalid_encoding", ex);
            }

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, ChatAesGcmCrypto.TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAuthenticatedData(wire));

            var parsed = JsonSerializer.Deserialize<ScreenShareMediaFramePlaintextV2>(plaintext, JsonOptions);
            if (parsed?.Chunk is null ||
                string.IsNullOrWhiteSpace(parsed.SenderIdentity) ||
                !string.Equals(parsed.Chunk.Type, ScreenShareFrameTypeV2, StringComparison.Ordinal) ||
                !string.Equals(parsed.Chunk.SessionId?.Trim(), wire.SessionId, StringComparison.Ordinal))
            {
                throw new CryptographicException("screenshare_media_packet_plaintext_invalid");
            }

            payload = new ScreenShareMediaFramePayloadV2(parsed.SenderIdentity.Trim(), parsed.Chunk);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ScreenShareMediaFrameWireV2 DeserializeWire(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            throw new CryptographicException("screenshare_media_packet_missing");
        }

        try
        {
            var wire = JsonSerializer.Deserialize<ScreenShareMediaFrameWireV2>(encoded, JsonOptions);
            if (wire is null ||
                !string.IsNullOrWhiteSpace(wire.Kind) && !string.Equals(wire.Kind.Trim(), "screenshare", StringComparison.Ordinal) ||
                !string.Equals(wire.Type?.Trim(), ScreenShareFrameTypeV2, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(wire.SessionId) ||
                wire.Sequence <= 0 ||
                string.IsNullOrWhiteSpace(wire.NonceBase64) ||
                string.IsNullOrWhiteSpace(wire.TagBase64) ||
                string.IsNullOrWhiteSpace(wire.CiphertextBase64))
            {
                throw new CryptographicException("screenshare_media_packet_invalid");
            }

            return wire with
            {
                Kind = string.IsNullOrWhiteSpace(wire.Kind) ? "screenshare" : wire.Kind.Trim(),
                Type = wire.Type!.Trim(),
                SessionId = wire.SessionId.Trim(),
                NonceBase64 = wire.NonceBase64.Trim(),
                TagBase64 = wire.TagBase64.Trim(),
                CiphertextBase64 = wire.CiphertextBase64.Trim(),
            };
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("screenshare_media_packet_invalid", ex);
        }
    }

    private static byte[] BuildAuthenticatedData(ScreenShareMediaFrameWireV2 wire)
    {
        var aadText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{wire.Kind}\u001f{wire.Type}\u001f{wire.SessionId}\u001f{wire.Sequence}");
        return Encoding.UTF8.GetBytes(aadText);
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("screenshare_media_required_value_missing", paramName);
        }

        return value.Trim();
    }

    private sealed record ScreenShareMediaFrameWireV2
    {
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "screenshare";

        [JsonPropertyName("type")]
        public string Type { get; init; } = ScreenShareFrameTypeV2;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; init; } = string.Empty;

        [JsonPropertyName("sequence")]
        public long Sequence { get; init; }

        [JsonPropertyName("nonceBase64")]
        public string NonceBase64 { get; init; } = string.Empty;

        [JsonPropertyName("tagBase64")]
        public string TagBase64 { get; init; } = string.Empty;

        [JsonPropertyName("ciphertextBase64")]
        public string CiphertextBase64 { get; init; } = string.Empty;
    }

    private sealed record ScreenShareMediaFramePlaintextV2
    {
        [JsonPropertyName("senderIdentity")]
        public string SenderIdentity { get; init; } = string.Empty;

        [JsonPropertyName("chunk")]
        public ScreenShareFrameChunkV1? Chunk { get; init; }
    }
}
