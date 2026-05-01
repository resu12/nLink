using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public sealed record SessionVerificationCode(
    string EmojiSequence,
    string FallbackCode,
    string Source);

public sealed record SessionVerificationMaterial(
    SessionId SessionId,
    PeerAddress HelperAddress,
    PeerAddress HelpeeAddress,
    byte[] SessionRootKey,
    byte[] HelperEcdhPublicKey,
    byte[] HelpeeEcdhPublicKey,
    string ChallengeNonce,
    string SessionContextCode);

public static class SessionVerificationCodeDerivation
{
    public const string SourceHandshakeTranscriptV1 = "handshake_transcript_v1";
    public const int DefaultEmojiCount = 5;

    private const int MinEmojiCount = 4;
    private const int MaxEmojiCount = 10;
    private const int FallbackBytesLength = 6;
    private const string SaltDomainSeparator = "nlink-session-verification-salt-v1|";
    private const string InfoLabel = "nlink-session-verification-code-v1";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] EmojiAlphabetValues =
    {
        "\u2600\uFE0F",
        "\U0001F319",
        "\u2B50",
        "\u2601\uFE0F",
        "\u2744\uFE0F",
        "\U0001F525",
        "\U0001F4A7",
        "\U0001F33F",
        "\U0001F333",
        "\U0001F33C",
        "\U0001F340",
        "\u26F0\uFE0F",
        "\U0001F30A",
        "\u2693\uFE0F",
        "\U0001F9ED",
        "\U0001F511",
        "\U0001F512",
        "\U0001F6E1\uFE0F",
        "\U0001F514",
        "\U0001F4A1",
        "\U0001F4D6",
        "\u270F\uFE0F",
        "\u2709\uFE0F",
        "\U0001F4E6",
        "\U0001F381",
        "\U0001F4F7",
        "\U0001F4F1",
        "\U0001F4BB",
        "\u2328\uFE0F",
        "\u2699\uFE0F",
        "\U0001F527",
        "\U0001F528",
        "\U0001F9F2",
        "\U0001F50B",
        "\U0001F50C",
        "\u231B",
        "\U0001F550",
        "\U0001F4FB",
        "\U0001F6F0\uFE0F",
        "\U0001F680",
        "\U0001F48E",
        "\U0001F451",
        "\U0001F3C6",
        "\U0001F3C5",
        "\U0001F3AF",
        "\U0001F388",
        "\U0001FA81",
        "\u2602\uFE0F",
        "\u26FA\uFE0F",
        "\U0001F5FA\uFE0F",
        "\U0001F4CC",
        "\U0001F4CE",
        "\u2702\uFE0F",
        "\U0001F4CF",
        "\U0001F4CB",
        "\U0001F4C1",
        "\U0001F50D",
        "\U0001F52C",
        "\U0001F52D",
        "\u2697\uFE0F",
        "\U0001F537",
        "\U0001F535",
        "\U0001F7E9",
        "\U0001F536",
    };

    internal static IReadOnlyList<string> DefaultEmojiAlphabet => EmojiAlphabetValues;

    public static SessionVerificationCode Derive(
        SessionVerificationMaterial material,
        int emojiCount = DefaultEmojiCount)
    {
        ValidateMaterial(material);
        if (emojiCount is < MinEmojiCount or > MaxEmojiCount)
        {
            throw new ArgumentOutOfRangeException(nameof(emojiCount), "session_verification_emoji_count_out_of_range");
        }

        var emojiBytesLength = checked((emojiCount * 6 + 7) / 8);
        var outputLength = checked(FallbackBytesLength + emojiBytesLength);
        var transcript = BuildCanonicalTranscript(material);
        var salt = SHA256.HashData(Concat(Utf8.GetBytes(SaltDomainSeparator), transcript));
        var derived = SessionKeyDerivation.HkdfSha256(
            material.SessionRootKey,
            salt,
            Utf8.GetBytes(InfoLabel),
            outputLength);

        var fallbackCode = FormatFallbackCode(derived.AsSpan(0, FallbackBytesLength));
        var emojiSequence = FormatEmojiSequence(
            derived.AsSpan(FallbackBytesLength, emojiBytesLength),
            emojiCount);

        return new SessionVerificationCode(
            emojiSequence,
            fallbackCode,
            SourceHandshakeTranscriptV1);
    }

    private static void ValidateMaterial(SessionVerificationMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (string.IsNullOrWhiteSpace(material.SessionId.Value))
        {
            throw new ArgumentException("session_verification_session_id_missing", nameof(material));
        }

        if (string.IsNullOrWhiteSpace(material.HelperAddress.Value))
        {
            throw new ArgumentException("session_verification_helper_address_missing", nameof(material));
        }

        if (string.IsNullOrWhiteSpace(material.HelpeeAddress.Value))
        {
            throw new ArgumentException("session_verification_helpee_address_missing", nameof(material));
        }

        if (material.SessionRootKey is null || material.SessionRootKey.Length == 0)
        {
            throw new ArgumentException("session_verification_root_key_missing", nameof(material));
        }

        if (material.HelperEcdhPublicKey is null || material.HelperEcdhPublicKey.Length == 0)
        {
            throw new ArgumentException("session_verification_helper_ecdh_key_missing", nameof(material));
        }

        if (material.HelpeeEcdhPublicKey is null || material.HelpeeEcdhPublicKey.Length == 0)
        {
            throw new ArgumentException("session_verification_helpee_ecdh_key_missing", nameof(material));
        }

        if (string.IsNullOrWhiteSpace(material.ChallengeNonce))
        {
            throw new ArgumentException("session_verification_challenge_nonce_missing", nameof(material));
        }

        if (string.IsNullOrWhiteSpace(material.SessionContextCode))
        {
            throw new ArgumentException("session_verification_context_code_missing", nameof(material));
        }
    }

    private static byte[] BuildCanonicalTranscript(SessionVerificationMaterial material)
    {
        using var buffer = new MemoryStream();
        WriteField(buffer, material.SessionId.Value);
        WriteField(buffer, material.HelperAddress.Value);
        WriteField(buffer, material.HelpeeAddress.Value);
        WriteField(buffer, material.HelperEcdhPublicKey);
        WriteField(buffer, material.HelpeeEcdhPublicKey);
        WriteField(buffer, material.ChallengeNonce.Trim());
        WriteField(buffer, material.SessionContextCode.Trim());
        return buffer.ToArray();
    }

    private static void WriteField(Stream destination, string value)
    {
        WriteField(destination, Utf8.GetBytes(value));
    }

    private static void WriteField(Stream destination, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        destination.Write(length);
        destination.Write(value, 0, value.Length);
    }

    private static string FormatFallbackCode(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    private static string FormatEmojiSequence(ReadOnlySpan<byte> bytes, int emojiCount)
    {
        var builder = new StringBuilder(emojiCount * 3);
        var bitBuffer = 0;
        var bitsInBuffer = 0;
        var byteIndex = 0;

        for (var i = 0; i < emojiCount; i++)
        {
            while (bitsInBuffer < 6)
            {
                bitBuffer = (bitBuffer << 8) | bytes[byteIndex++];
                bitsInBuffer += 8;
            }

            bitsInBuffer -= 6;
            var emojiIndex = (bitBuffer >> bitsInBuffer) & 0x3F;
            bitBuffer &= (1 << bitsInBuffer) - 1;

            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(EmojiAlphabetValues[emojiIndex]);
        }

        return builder.ToString();
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        var result = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, result, 0, left.Length);
        Buffer.BlockCopy(right, 0, result, left.Length, right.Length);
        return result;
    }
}
