using System.Text.Json;
using System.Text;

namespace NLink.Core.SessionConnect;

public static class InviteQrPayload
{
    public const string Header = "NLINK INVITE";
    public const string InlinePrefix = "NLINK_INVITE:";
    public const string EncodedPrefix = "TOKEN";
    private const string QrType = "nlink_invite";

    public static string Format(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return inviteToken.Trim();
    }

    public static string ExtractTokenOrOriginal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (TryExtractFromJson(normalized, out var inviteToken))
        {
            return inviteToken;
        }

        if (normalized.StartsWith(InlinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[InlinePrefix.Length..].Trim();
        }

        if (normalized.StartsWith(Header, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalized[Header.Length..].Trim();
            if (TryExtractEncodedToken(remainder, out inviteToken))
            {
                return inviteToken;
            }

            if (!string.IsNullOrWhiteSpace(remainder))
            {
                return remainder;
            }
        }

        return normalized;
    }

    private static bool TryExtractFromJson(string value, out string inviteToken)
    {
        inviteToken = string.Empty;

        if (!value.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<InviteQrPayloadV1>(value);
            if (parsed is null || !string.Equals(parsed.Type, QrType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parsed.TokenBase64) &&
                InviteTokenBase64Url.TryDecode(parsed.TokenBase64, out var decodedBytes))
            {
                inviteToken = Encoding.UTF8.GetString(decodedBytes).Trim();
                return !string.IsNullOrWhiteSpace(inviteToken);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Invite))
            {
                inviteToken = parsed.Invite.Trim();
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractEncodedToken(string value, out string inviteToken)
    {
        inviteToken = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var rawLine in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("CODE:", StringComparison.OrdinalIgnoreCase))
            {
                var legacyEncoded = rawLine["CODE:".Length..].Trim();
                if (InviteTokenBase64Url.TryDecode(legacyEncoded, out var legacyDecodedBytes))
                {
                    inviteToken = Encoding.UTF8.GetString(legacyDecodedBytes).Trim();
                    return !string.IsNullOrWhiteSpace(inviteToken);
                }

                continue;
            }

            if (!rawLine.StartsWith(EncodedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encoded = rawLine[EncodedPrefix.Length..].Trim();
            if (InviteTokenBase64Url.TryDecode(encoded, out var decodedBytes))
            {
                inviteToken = Encoding.UTF8.GetString(decodedBytes).Trim();
                return !string.IsNullOrWhiteSpace(inviteToken);
            }
        }

        return false;
    }

    private sealed record InviteQrPayloadV1(string Type, string? TokenBase64, string? Invite);
}
