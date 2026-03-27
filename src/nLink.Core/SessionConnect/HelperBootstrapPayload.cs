using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Core.SessionConnect;

public sealed record HelperBootstrapPayload(
    int Version,
    PeerAddress HelperAddress,
    string? HelperId = null,
    string? FingerprintHint = null)
{
    public const int CurrentVersion = 1;
    public const string PayloadType = "nlink_helper_bootstrap";

    public static HelperBootstrapPayload Create(
        PeerAddress helperAddress,
        string? helperId = null,
        string? fingerprintHint = null)
        => new(CurrentVersion, helperAddress, Normalize(helperId), Normalize(fingerprintHint));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class HelperBootstrapQrPayload
{
    private const string InlinePrefix = "NLINK_HELPER:";
    public const string TokenPrefix = "nlinkh1";
    private const string TokenPrefixWithSeparator = TokenPrefix + ".";
    private const byte MagicByte0 = (byte)'N';
    private const byte MagicByte1 = (byte)'H';
    private const byte CompactVersion = 1;
    private const byte HelperIdPresentFlag = 0x01;

    public static string Format(HelperBootstrapPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return FormatCompact(payload);
    }

    public static bool TryParse(string? value, out HelperBootstrapPayload? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith(InlinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[InlinePrefix.Length..].Trim();
        }

        if (TryParseCompact(normalized, out payload))
        {
            return true;
        }

        if (!normalized.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<HelperBootstrapPayloadWire>(normalized);
            if (parsed is null ||
                !string.Equals(parsed.Type, HelperBootstrapPayload.PayloadType, StringComparison.OrdinalIgnoreCase) ||
                parsed.Version != HelperBootstrapPayload.CurrentVersion ||
                !PeerAddress.TryParse(parsed.HelperAddress, out var helperAddress))
            {
                return false;
            }

            payload = HelperBootstrapPayload.Create(
                helperAddress,
                parsed.HelperId,
                parsed.FingerprintHint);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatCompact(HelperBootstrapPayload payload)
    {
        if (payload.Version != HelperBootstrapPayload.CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported helper bootstrap payload version: {payload.Version}.");
        }

        var helperAddressBytes = Encoding.UTF8.GetBytes(payload.HelperAddress.Value);
        var helperIdBytes = string.IsNullOrWhiteSpace(payload.HelperId)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(payload.HelperId);
        var flags = helperIdBytes.Length > 0 ? HelperIdPresentFlag : (byte)0;
        var payloadBytes = new byte[6 + helperAddressBytes.Length + 2 + helperIdBytes.Length];
        payloadBytes[0] = MagicByte0;
        payloadBytes[1] = MagicByte1;
        payloadBytes[2] = CompactVersion;
        payloadBytes[3] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(payloadBytes.AsSpan(4, 2), checked((ushort)helperAddressBytes.Length));
        helperAddressBytes.CopyTo(payloadBytes.AsSpan(6));

        var helperIdLengthOffset = 6 + helperAddressBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payloadBytes.AsSpan(helperIdLengthOffset, 2), checked((ushort)helperIdBytes.Length));
        helperIdBytes.CopyTo(payloadBytes.AsSpan(helperIdLengthOffset + 2));

        return TokenPrefixWithSeparator + EncodeBase64Url(payloadBytes);
    }

    private static bool TryParseCompact(string normalized, out HelperBootstrapPayload? payload)
    {
        payload = null;

        if (!normalized.StartsWith(TokenPrefixWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = normalized[TokenPrefixWithSeparator.Length..].Trim();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = DecodeBase64Url(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length < 8 ||
            bytes[0] != MagicByte0 ||
            bytes[1] != MagicByte1 ||
            bytes[2] != CompactVersion)
        {
            return false;
        }

        var flags = bytes[3];
        var helperAddressLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        var helperAddressOffset = 6;
        if (bytes.Length < helperAddressOffset + helperAddressLength + 2)
        {
            return false;
        }

        var helperAddressText = DecodeUtf8(bytes.AsSpan(helperAddressOffset, helperAddressLength));
        if (helperAddressText is null || !PeerAddress.TryParse(helperAddressText, out var helperAddress))
        {
            return false;
        }

        var helperIdLengthOffset = helperAddressOffset + helperAddressLength;
        var helperIdLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(helperIdLengthOffset, 2));
        var helperIdOffset = helperIdLengthOffset + 2;
        if (bytes.Length != helperIdOffset + helperIdLength)
        {
            return false;
        }

        string? helperId = null;
        if ((flags & HelperIdPresentFlag) != 0)
        {
            if (helperIdLength == 0)
            {
                return false;
            }

            helperId = DecodeUtf8(bytes.AsSpan(helperIdOffset, helperIdLength));
            if (string.IsNullOrWhiteSpace(helperId))
            {
                return false;
            }

            var decodeResult = HelperIdentityTokenCodec.Decode(helperId);
            if (!decodeResult.IsSuccess || decodeResult.Address is null)
            {
                return false;
            }
        }
        else if (helperIdLength != 0)
        {
            return false;
        }

        payload = HelperBootstrapPayload.Create(helperAddress, helperId);
        return true;
    }

    private static string? DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
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
                throw new FormatException("Invalid base64url length.");
        }

        return Convert.FromBase64String(normalized);
    }

    private sealed record HelperBootstrapPayloadWire(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("helperAddress")] string HelperAddress,
        [property: JsonPropertyName("helperId")] string? HelperId,
        [property: JsonPropertyName("fingerprintHint")] string? FingerprintHint);
}
