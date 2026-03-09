using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.SessionConnect;

public enum InviteShareCodeDecodeError
{
    None = 0,
    Empty = 1,
    InvalidPrefix = 2,
    InvalidEncoding = 3,
    InvalidChecksum = 4,
    InvalidToken = 5,
}

public readonly record struct InviteShareCodeDecodeResult(
    bool IsSuccess,
    string? InviteToken,
    InviteShareCodeDecodeError Error,
    string? Message = null)
{
    public static InviteShareCodeDecodeResult Success(string inviteToken)
        => new(true, inviteToken, InviteShareCodeDecodeError.None, null);

    public static InviteShareCodeDecodeResult Failure(InviteShareCodeDecodeError error, string message)
        => new(false, null, error, message);
}

public static class InviteShareCodeCodec
{
    public const string CodePrefix = "ninv1";

    private const int ChecksumLength = 4;
    private const string DomainSeparator = "nlink/invite-share-code/v1|";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly sbyte[] DecodeMap = BuildDecodeMap();

    public static string Encode(string inviteToken)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
        {
            throw new ArgumentException("Invite token is required.", nameof(inviteToken));
        }

        var normalizedToken = inviteToken.Trim();
        var tokenBytes = Utf8.GetBytes(normalizedToken);
        var payload = new byte[tokenBytes.Length + ChecksumLength];
        Buffer.BlockCopy(tokenBytes, 0, payload, 0, tokenBytes.Length);
        Buffer.BlockCopy(ComputeChecksum(normalizedToken), 0, payload, tokenBytes.Length, ChecksumLength);

        var encoded = EncodeBase32(payload);
        return $"{CodePrefix}-{GroupEncoded(encoded)}";
    }

    public static InviteShareCodeDecodeResult Decode(string? shareCode)
    {
        if (string.IsNullOrWhiteSpace(shareCode))
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.Empty,
                "Invite share code is required.");
        }

        var trimmed = shareCode.Trim();
        if (!trimmed.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidPrefix,
                $"Invite share code must start with '{CodePrefix}-'.");
        }

        var remainder = trimmed.Substring(CodePrefix.Length);
        if (remainder.Length == 0 || remainder[0] != '-')
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidPrefix,
                $"Invite share code must start with '{CodePrefix}-'.");
        }

        var body = NormalizeEncodedBody(remainder.Substring(1));
        if (body.Length == 0)
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidEncoding,
                "Invite share code body is empty.");
        }

        if (!TryDecodeBase32(body, out var payload))
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidEncoding,
                "Invite share code contains unsupported characters.");
        }

        if (payload.Length <= ChecksumLength)
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidEncoding,
                "Invite share code payload is too short.");
        }

        var tokenLength = payload.Length - ChecksumLength;
        string inviteToken;
        try
        {
            inviteToken = Utf8.GetString(payload, 0, tokenLength);
        }
        catch (DecoderFallbackException)
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidEncoding,
                "Invite share code payload is not valid UTF-8.");
        }

        if (string.IsNullOrWhiteSpace(inviteToken))
        {
            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidToken,
                "Invite share code did not decode to a valid invite token.");
        }

        var expectedChecksum = ComputeChecksum(inviteToken);
        for (var i = 0; i < ChecksumLength; i++)
        {
            if (payload[tokenLength + i] == expectedChecksum[i])
            {
                continue;
            }

            return InviteShareCodeDecodeResult.Failure(
                InviteShareCodeDecodeError.InvalidChecksum,
                "Invite share code checksum is invalid.");
        }

        return InviteShareCodeDecodeResult.Success(inviteToken);
    }

    private static byte[] ComputeChecksum(string inviteToken)
    {
        var hash = SHA256.HashData(Utf8.GetBytes(DomainSeparator + inviteToken));
        return hash.AsSpan(0, ChecksumLength).ToArray();
    }

    private static string GroupEncoded(string encoded)
    {
        var builder = new StringBuilder(encoded.Length + (encoded.Length / 4));
        for (var i = 0; i < encoded.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                builder.Append('-');
            }

            builder.Append(encoded[i]);
        }

        return builder.ToString();
    }

    private static string NormalizeEncodedBody(string encoded)
    {
        var builder = new StringBuilder(encoded.Length);
        foreach (var ch in encoded)
        {
            if (ch is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private static string EncodeBase32(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var outputLength = (data.Length * 8 + 4) / 5;
        var chars = new char[outputLength];
        var buffer = 0;
        var bitsLeft = 0;
        var outputIndex = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                chars[outputIndex++] = Alphabet[(buffer >> bitsLeft) & 0x1F];
            }
        }

        if (bitsLeft > 0)
        {
            chars[outputIndex++] = Alphabet[(buffer << (5 - bitsLeft)) & 0x1F];
        }

        return new string(chars, 0, outputIndex);
    }

    private static bool TryDecodeBase32(string encoded, out byte[] data)
    {
        if (encoded.Length == 0)
        {
            data = Array.Empty<byte>();
            return true;
        }

        var bytes = new List<byte>((encoded.Length * 5) / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var ch in encoded)
        {
            if (ch >= DecodeMap.Length)
            {
                data = Array.Empty<byte>();
                return false;
            }

            var value = DecodeMap[ch];
            if (value < 0)
            {
                data = Array.Empty<byte>();
                return false;
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            while (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        data = bytes.ToArray();
        return true;
    }

    private static sbyte[] BuildDecodeMap()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);

        for (var i = 0; i < Alphabet.Length; i++)
        {
            map[Alphabet[i]] = (sbyte)i;
        }

        map['O'] = map['0'];
        map['I'] = map['1'];
        map['L'] = map['1'];

        return map;
    }
}
