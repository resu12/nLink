using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
    private const byte RequestIdPresentFlag = 0x01;
    private static readonly byte[] Magic = "NLS"u8.ToArray();

    public static byte[] Encrypt(
        byte[] key,
        SessionSecureEnvelopeMetadata metadata,
        ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        ValidateMetadata(metadata);

        var wire = CreateWire(metadata);

        var nonce = new byte[ChatAesGcmCrypto.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ChatAesGcmCrypto.TagSize];

        using var aes = new AesGcm(key, ChatAesGcmCrypto.TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAuthenticatedData(wire));

        return SerializeWire(wire, nonce, tag, ciphertext);
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

        var plaintext = new byte[wire.Ciphertext.Length];
        using var aes = new AesGcm(key, ChatAesGcmCrypto.TagSize);
        aes.Decrypt(
            wire.Nonce,
            wire.Ciphertext,
            wire.Tag,
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

        var data = encoded;
        if (data.Length < MinimumWireLength)
        {
            throw new CryptographicException("session_secure_payload_invalid");
        }

        if (!data[..Magic.Length].SequenceEqual(Magic) || data[Magic.Length] != Version)
        {
            throw new CryptographicException("session_secure_payload_invalid");
        }

        var offset = Magic.Length + sizeof(byte);
        var family = data[offset++];
        var flags = data[offset++];
        var sequence = ReadInt64(data, ref offset, "session_secure_payload_invalid");
        var messageTypeLength = data[offset++];
        var sessionIdLength = ReadUInt16(data, ref offset, "session_secure_payload_invalid");
        var senderIdentityLength = ReadUInt16(data, ref offset, "session_secure_payload_invalid");
        var requestIdLength = ReadUInt16(data, ref offset, "session_secure_payload_invalid");
        var ciphertextLength = ReadUInt32(data, ref offset, "session_secure_payload_invalid");

        if ((flags & ~RequestIdPresentFlag) != 0)
        {
            throw new CryptographicException("session_secure_payload_invalid");
        }

        if (((flags & RequestIdPresentFlag) == 0 && requestIdLength != 0) ||
            ((flags & RequestIdPresentFlag) != 0 && requestIdLength == 0))
        {
            throw new CryptographicException("session_secure_payload_invalid");
        }

        var messageType = ReadUtf8(data, ref offset, messageTypeLength, "session_secure_payload_invalid");
        var sessionId = ReadUtf8(data, ref offset, sessionIdLength, "session_secure_payload_invalid");
        var senderIdentity = ReadUtf8(data, ref offset, senderIdentityLength, "session_secure_payload_invalid");
        var requestId = requestIdLength == 0
            ? null
            : ReadUtf8(data, ref offset, requestIdLength, "session_secure_payload_invalid");
        var nonce = ReadBytes(data, ref offset, ChatAesGcmCrypto.NonceSize, "session_secure_payload_invalid");
        var tag = ReadBytes(data, ref offset, ChatAesGcmCrypto.TagSize, "session_secure_payload_invalid");
        var ciphertext = ReadBytes(data, ref offset, checked((int)ciphertextLength), "session_secure_payload_invalid");

        if (offset != data.Length ||
            string.IsNullOrWhiteSpace(messageType) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(senderIdentity))
        {
            throw new CryptographicException("session_secure_payload_invalid");
        }

        return new SessionSecureEnvelopeWire
        {
            Version = Version,
            Family = family,
            MessageType = messageType,
            SessionId = sessionId,
            SenderIdentity = senderIdentity,
            Sequence = sequence,
            RequestId = requestId,
            Nonce = nonce,
            Tag = tag,
            Ciphertext = ciphertext,
        };
    }

    private static SessionSecureEnvelopeMetadata ParseMetadata(SessionSecureEnvelopeWire wire)
    {
        if (!Enum.IsDefined(typeof(SessionSecureMessageFamily), (int)wire.Family))
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

    private static SessionSecureEnvelopeWire CreateWire(SessionSecureEnvelopeMetadata metadata)
    {
        return new SessionSecureEnvelopeWire
        {
            Version = Version,
            Family = checked((byte)metadata.Family),
            MessageType = metadata.MessageType.Trim(),
            SessionId = metadata.SessionId.Value,
            SenderIdentity = metadata.SenderIdentity.Value,
            Sequence = metadata.Sequence,
            RequestId = NormalizeOptional(metadata.RequestId),
        };
    }

    private static byte[] SerializeWire(
        SessionSecureEnvelopeWire wire,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> ciphertext)
    {
        var messageTypeBytes = Encoding.UTF8.GetBytes(wire.MessageType);
        var sessionIdBytes = Encoding.UTF8.GetBytes(wire.SessionId);
        var senderIdentityBytes = Encoding.UTF8.GetBytes(wire.SenderIdentity);
        var requestIdBytes = wire.RequestId is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(wire.RequestId);

        if (messageTypeBytes.Length > byte.MaxValue ||
            sessionIdBytes.Length > ushort.MaxValue ||
            senderIdentityBytes.Length > ushort.MaxValue ||
            requestIdBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("session_secure_payload_invalid", nameof(wire));
        }

        var flags = wire.RequestId is null ? (byte)0 : RequestIdPresentFlag;
        var totalLength =
            Magic.Length +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(long) +
            sizeof(byte) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(uint) +
            messageTypeBytes.Length +
            sessionIdBytes.Length +
            senderIdentityBytes.Length +
            requestIdBytes.Length +
            nonce.Length +
            tag.Length +
            ciphertext.Length;

        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();
        var offset = 0;

        Magic.CopyTo(span[offset..]);
        offset += Magic.Length;
        span[offset++] = Version;
        span[offset++] = wire.Family;
        span[offset++] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], wire.Sequence);
        offset += sizeof(long);
        span[offset++] = checked((byte)messageTypeBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)sessionIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)senderIdentityBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)requestIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], checked((uint)ciphertext.Length));
        offset += sizeof(uint);

        messageTypeBytes.CopyTo(span[offset..]);
        offset += messageTypeBytes.Length;
        sessionIdBytes.CopyTo(span[offset..]);
        offset += sessionIdBytes.Length;
        senderIdentityBytes.CopyTo(span[offset..]);
        offset += senderIdentityBytes.Length;
        requestIdBytes.CopyTo(span[offset..]);
        offset += requestIdBytes.Length;
        nonce.CopyTo(span[offset..]);
        offset += nonce.Length;
        tag.CopyTo(span[offset..]);
        offset += tag.Length;
        ciphertext.CopyTo(span[offset..]);

        return buffer;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset, string errorCode)
    {
        if (offset + sizeof(ushort) > data.Length)
        {
            throw new CryptographicException(errorCode);
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset, string errorCode)
    {
        if (offset + sizeof(uint) > data.Length)
        {
            throw new CryptographicException(errorCode);
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += sizeof(uint);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int offset, string errorCode)
    {
        if (offset + sizeof(long) > data.Length)
        {
            throw new CryptographicException(errorCode);
        }

        var value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += sizeof(long);
        return value;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> data, ref int offset, int length, string errorCode)
    {
        if (length < 0 || offset + length > data.Length)
        {
            throw new CryptographicException(errorCode);
        }

        try
        {
            var value = Encoding.UTF8.GetString(data[offset..(offset + length)]);
            offset += length;
            return value;
        }
        catch (DecoderFallbackException ex)
        {
            throw new CryptographicException(errorCode, ex);
        }
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset, int length, string errorCode)
    {
        if (length < 0 || offset + length > data.Length)
        {
            throw new CryptographicException(errorCode);
        }

        var bytes = data[offset..(offset + length)].ToArray();
        offset += length;
        return bytes;
    }

    private const int MinimumWireLength =
        3 + // magic
        1 + // version
        1 + // family
        1 + // flags
        8 + // sequence
        1 + // message type length
        2 + // session id length
        2 + // sender identity length
        2 + // request id length
        4 + // ciphertext length
        ChatAesGcmCrypto.NonceSize +
        ChatAesGcmCrypto.TagSize;

    private sealed class SessionSecureEnvelopeWire
    {
        public int Version { get; set; }

        public byte Family { get; set; }

        public string MessageType { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string SenderIdentity { get; set; } = string.Empty;

        public long Sequence { get; set; }

        public string? RequestId { get; set; }

        public byte[] Nonce { get; set; } = Array.Empty<byte>();

        public byte[] Tag { get; set; } = Array.Empty<byte>();

        public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    }
}
