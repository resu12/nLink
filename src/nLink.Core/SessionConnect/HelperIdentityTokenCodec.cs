using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.SessionConnect;

public enum HelperIdentityTokenDecodeError
{
    None = 0,
    Empty = 1,
    InvalidPrefix = 2,
    InvalidEncoding = 3,
    InvalidChecksum = 4,
    InvalidPeerAddress = 5,
}

public readonly record struct HelperIdentityTokenDecodeResult(
    bool IsSuccess,
    PeerAddress? Address,
    HelperIdentityTokenDecodeError Error,
    string? Message = null)
{
    public static HelperIdentityTokenDecodeResult Success(PeerAddress address)
        => new(true, address, HelperIdentityTokenDecodeError.None, null);

    public static HelperIdentityTokenDecodeResult Failure(HelperIdentityTokenDecodeError error, string message)
        => new(false, null, error, message);
}

public static class HelperIdentityTokenCodec
{
    public const string TokenPrefix = "nhid1";

    private const int ChecksumLength = 4;
    private const string DomainSeparator = "nlink/helper-identity-token/v1|";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly sbyte[] DecodeMap = BuildDecodeMap();

    public static string Encode(PeerAddress helperAddress)
    {
        var addressBytes = Utf8.GetBytes(helperAddress.Value);
        var payload = new byte[addressBytes.Length + ChecksumLength];
        Buffer.BlockCopy(addressBytes, 0, payload, 0, addressBytes.Length);
        Buffer.BlockCopy(ComputeChecksum(helperAddress.Value), 0, payload, addressBytes.Length, ChecksumLength);

        var encoded = EncodeBase32(payload);
        return $"{TokenPrefix}-{GroupEncoded(encoded)}";
    }

    public static HelperIdentityTokenDecodeResult Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.Empty,
                "Helper identity token is required.");
        }

        var trimmed = token.Trim();
        if (!trimmed.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidPrefix,
                $"Helper identity token must start with '{TokenPrefix}-'.");
        }

        var remainder = trimmed.Substring(TokenPrefix.Length);
        if (remainder.Length == 0 || remainder[0] != '-')
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidPrefix,
                $"Helper identity token must start with '{TokenPrefix}-'.");
        }

        var body = NormalizeEncodedBody(remainder.Substring(1));
        if (body.Length == 0)
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidEncoding,
                "Helper identity token body is empty.");
        }

        if (!TryDecodeBase32(body, out var payload))
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidEncoding,
                "Helper identity token contains unsupported characters.");
        }

        if (payload.Length <= ChecksumLength)
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidEncoding,
                "Helper identity token payload is too short.");
        }

        var addressLength = payload.Length - ChecksumLength;
        string addressText;
        try
        {
            addressText = Utf8.GetString(payload, 0, addressLength);
        }
        catch (DecoderFallbackException)
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidEncoding,
                "Helper identity token payload is not valid UTF-8.");
        }

        if (!PeerAddress.TryParse(addressText, out var helperAddress))
        {
            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidPeerAddress,
                "Helper identity token did not decode to a valid helper address.");
        }

        var expectedChecksum = ComputeChecksum(helperAddress.Value);
        for (var i = 0; i < ChecksumLength; i++)
        {
            if (payload[addressLength + i] == expectedChecksum[i])
            {
                continue;
            }

            return HelperIdentityTokenDecodeResult.Failure(
                HelperIdentityTokenDecodeError.InvalidChecksum,
                "Helper identity token checksum is invalid.");
        }

        return HelperIdentityTokenDecodeResult.Success(helperAddress);
    }

    private static byte[] ComputeChecksum(string helperAddress)
    {
        var hash = SHA256.HashData(Utf8.GetBytes(DomainSeparator + helperAddress));
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

            buffer = (buffer << 5) | (value & 0x1F);
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
