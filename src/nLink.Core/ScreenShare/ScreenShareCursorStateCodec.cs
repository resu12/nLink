using System.Text.Json;

namespace NLink.Core.ScreenShare;

public static class ScreenShareCursorStateCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Serialize(ScreenShareCursorStateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalized = NormalizeForSerialization(message);
        return JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out ScreenShareCursorStateV1 message)
    {
        message = default!;
        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenShareCursorStateV1>(utf8Json, JsonOptions);
            if (parsed is null ||
                !string.Equals(parsed.Kind, ScreenShareCursorStateProtocol.Kind, StringComparison.Ordinal) ||
                !string.Equals(parsed.Type, ScreenShareCursorStateProtocol.CursorStateTypeV1, StringComparison.Ordinal) ||
                !TryNormalizeRequired(parsed.SessionId, maxLength: 128, out var sessionId) ||
                !TryNormalizeRequired(parsed.DisplayId, maxLength: 128, out var displayId) ||
                parsed.Seq < 0 ||
                parsed.TsUtcMs < 0 ||
                parsed.DisplayInfoRevision <= 0 ||
                !TryNormalizeCoordinate(parsed.Nx, out var nx) ||
                !TryNormalizeCoordinate(parsed.Ny, out var ny))
            {
                return false;
            }

            message = parsed with
            {
                Kind = ScreenShareCursorStateProtocol.Kind,
                Type = ScreenShareCursorStateProtocol.CursorStateTypeV1,
                SessionId = sessionId,
                DisplayId = displayId,
                Seq = parsed.Seq,
                TsUtcMs = parsed.TsUtcMs,
                DisplayInfoRevision = parsed.DisplayInfoRevision,
                Nx = nx,
                Ny = ny,
                Source = NormalizeOptional(parsed.Source, 64) ?? "os_cursor",
                Status = NormalizeOptional(parsed.Status, 64) ?? "unknown",
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ScreenShareCursorStateV1 NormalizeForSerialization(ScreenShareCursorStateV1 message)
    {
        if (!TryNormalizeRequired(message.SessionId, maxLength: 128, out var sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(message));
        }

        if (!TryNormalizeRequired(message.DisplayId, maxLength: 128, out var displayId))
        {
            throw new ArgumentException("Display id is required.", nameof(message));
        }

        if (message.DisplayInfoRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Display info revision must be positive.");
        }

        if (!TryNormalizeCoordinate(message.Nx, out var nx) ||
            !TryNormalizeCoordinate(message.Ny, out var ny))
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Cursor coordinates must be finite and normalized.");
        }

        return message with
        {
            Kind = ScreenShareCursorStateProtocol.Kind,
            Type = ScreenShareCursorStateProtocol.CursorStateTypeV1,
            SessionId = sessionId,
            Seq = Math.Max(0, message.Seq),
            TsUtcMs = Math.Max(0, message.TsUtcMs),
            DisplayId = displayId,
            DisplayInfoRevision = message.DisplayInfoRevision,
            Nx = nx,
            Ny = ny,
            Source = NormalizeOptional(message.Source, 64) ?? "os_cursor",
            Status = NormalizeOptional(message.Status, 64) ?? "unknown",
        };
    }

    private static bool TryNormalizeRequired(string? value, int maxLength, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool TryNormalizeCoordinate(double value, out double normalized)
    {
        normalized = 0d;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d || value > 1d)
        {
            return false;
        }

        normalized = Math.Clamp(value, 0d, 1d);
        return true;
    }
}

