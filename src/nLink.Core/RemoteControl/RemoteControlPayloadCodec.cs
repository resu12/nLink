using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace NLink.Core.RemoteControl;

public sealed record ControlRequestMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("caps")]
    public string[]? Caps { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record ControlResponseMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;

    [JsonPropertyName("consentToken")]
    public string? ConsentToken { get; init; }

    [JsonPropertyName("ttlMs")]
    public long? TtlMs { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record ControlStartMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("consentToken")]
    public string? ConsentToken { get; init; }
}

public sealed record ControlStopMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public enum ControlInputKind
{
    MouseMove,
    MouseButton,
    MouseWheel,
    Key,
}

public enum ControlMouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

public enum ControlButtonAction
{
    Down,
    Up,
}

public enum ControlKeyAction
{
    Down,
    Up,
}

[Flags]
public enum RemoteControlModifiersMask
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3,
    Win = 1 << 4,
}

[Flags]
public enum RemoteControlMouseButtonsMask
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    X1 = 1 << 3,
    X2 = 1 << 4,
}

public sealed record ControlInputMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("displayId")]
    public string? DisplayId { get; init; }

    [JsonPropertyName("displayInfoRevision")]
    public long? DisplayInfoRevision { get; init; }

    [JsonPropertyName("nx")]
    public double? Nx { get; init; }

    [JsonPropertyName("ny")]
    public double? Ny { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("button")]
    public string? Button { get; init; }

    [JsonPropertyName("deltaX")]
    public double? DeltaX { get; init; }

    [JsonPropertyName("deltaY")]
    public double? DeltaY { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("physicalKey")]
    public string? PhysicalKey { get; init; }

    [JsonPropertyName("shift")]
    public bool? Shift { get; init; }

    [JsonPropertyName("ctrl")]
    public bool? Ctrl { get; init; }

    [JsonPropertyName("alt")]
    public bool? Alt { get; init; }

    [JsonPropertyName("meta")]
    public bool? Meta { get; init; }

    [JsonPropertyName("repeat")]
    public bool? Repeat { get; init; }

    [JsonPropertyName("tsUtcMs")]
    public long? TsUtcMs { get; init; }
}

public sealed record ControlInputAckV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("ackSeq")]
    public long AckSeq { get; init; }

    [JsonPropertyName("tsUtcMs")]
    public long TsUtcMs { get; init; }
}

public sealed record ControlStateSnapshotV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("tsUtcMs")]
    public long TsUtcMs { get; init; }

    [JsonPropertyName("modifiersMask")]
    public int ModifiersMask { get; init; }

    [JsonPropertyName("mouseButtonsMask")]
    public int MouseButtonsMask { get; init; }
}

public sealed record ControlDisplayInfoMessageV1
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("displayId")]
    public string DisplayId { get; init; } = string.Empty;

    [JsonPropertyName("virtualDesktopX")]
    public int VirtualDesktopX { get; init; }

    [JsonPropertyName("virtualDesktopY")]
    public int VirtualDesktopY { get; init; }

    [JsonPropertyName("virtualDesktopWidth")]
    public int VirtualDesktopWidth { get; init; }

    [JsonPropertyName("virtualDesktopHeight")]
    public int VirtualDesktopHeight { get; init; }

    [JsonPropertyName("captureRegionX")]
    public int CaptureRegionX { get; init; }

    [JsonPropertyName("captureRegionY")]
    public int CaptureRegionY { get; init; }

    [JsonPropertyName("captureRegionWidth")]
    public int CaptureRegionWidth { get; init; }

    [JsonPropertyName("captureRegionHeight")]
    public int CaptureRegionHeight { get; init; }

    [JsonPropertyName("frameWidth")]
    public int FrameWidth { get; init; }

    [JsonPropertyName("frameHeight")]
    public int FrameHeight { get; init; }

    [JsonPropertyName("dpiScale")]
    public double? DpiScale { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("tsUtcMs")]
    public long TsUtcMs { get; init; }
}

public static class RemoteControlPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(ControlRequestMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlResponseMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlStartMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlStopMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlInputMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlInputAckV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlStateSnapshotV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static byte[] Serialize(ControlDisplayInfoMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        return JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
    }

    public static bool TryDeserializeControlRequest(ReadOnlySpan<byte> utf8Json, out ControlRequestMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlRequestMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId))
        {
            return false;
        }

        msg = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
            Caps = NormalizeCaps(parsed.Caps),
            Reason = NormalizeNullable(parsed.Reason),
        };
        return true;
    }

    public static bool TryDeserializeControlResponse(ReadOnlySpan<byte> utf8Json, out ControlResponseMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlResponseMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId) ||
            string.IsNullOrWhiteSpace(parsed.Decision))
        {
            return false;
        }

        msg = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
            Decision = parsed.Decision.Trim(),
            ConsentToken = NormalizeNullable(parsed.ConsentToken),
            TtlMs = NormalizeTtl(parsed.TtlMs),
            Reason = NormalizeNullable(parsed.Reason),
        };
        return true;
    }

    public static bool TryDeserializeControlStart(ReadOnlySpan<byte> utf8Json, out ControlStartMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlStartMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId))
        {
            return false;
        }

        msg = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
            ConsentToken = NormalizeNullable(parsed.ConsentToken),
        };
        return true;
    }

    public static bool TryDeserializeControlStop(ReadOnlySpan<byte> utf8Json, out ControlStopMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlStopMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId))
        {
            return false;
        }

        msg = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
            Reason = NormalizeNullable(parsed.Reason),
        };
        return true;
    }

    public static bool TryDeserializeControlInput(ReadOnlySpan<byte> utf8Json, out ControlInputMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlInputMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId) ||
            parsed.Seq < 0 ||
            !TryParseInputKind(parsed.Kind, out var kind))
        {
            return false;
        }

        var normalized = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
            Kind = FormatInputKind(kind),
            DisplayId = NormalizeNullable(parsed.DisplayId),
            DisplayInfoRevision = NormalizeRevision(parsed.DisplayInfoRevision),
            Action = NormalizeNullable(parsed.Action),
            Button = NormalizeNullable(parsed.Button),
            Key = NormalizeNullable(parsed.Key),
            PhysicalKey = NormalizeNullable(parsed.PhysicalKey),
            TsUtcMs = NormalizeTimestamp(parsed.TsUtcMs),
        };

        switch (kind)
        {
            case ControlInputKind.MouseMove:
                if (!TryNormalizeUnitCoordinate(parsed.Nx, out var moveX) ||
                    !TryNormalizeUnitCoordinate(parsed.Ny, out var moveY))
                {
                    return false;
                }

                normalized = normalized with
                {
                    Nx = moveX,
                    Ny = moveY,
                    Action = null,
                    Button = null,
                    DeltaX = null,
                    DeltaY = null,
                    Key = null,
                    PhysicalKey = null,
                    Repeat = null,
                };
                break;

            case ControlInputKind.MouseButton:
                if (!TryParseButtonAction(parsed.Action, out var buttonAction) ||
                    !TryParseMouseButton(parsed.Button, out var button) ||
                    !TryNormalizeUnitCoordinate(parsed.Nx, out var buttonX) ||
                    !TryNormalizeUnitCoordinate(parsed.Ny, out var buttonY))
                {
                    return false;
                }

                normalized = normalized with
                {
                    Nx = buttonX,
                    Ny = buttonY,
                    Action = FormatButtonAction(buttonAction),
                    Button = FormatMouseButton(button),
                    DeltaX = null,
                    DeltaY = null,
                    Key = null,
                    PhysicalKey = null,
                    Repeat = null,
                };
                break;

            case ControlInputKind.MouseWheel:
                if (!TryNormalizeFinite(parsed.DeltaX, out var deltaX) ||
                    !TryNormalizeFinite(parsed.DeltaY, out var deltaY) ||
                    !TryNormalizeUnitCoordinate(parsed.Nx, out var wheelX) ||
                    !TryNormalizeUnitCoordinate(parsed.Ny, out var wheelY))
                {
                    return false;
                }

                normalized = normalized with
                {
                    Nx = wheelX,
                    Ny = wheelY,
                    Action = null,
                    Button = null,
                    DeltaX = deltaX,
                    DeltaY = deltaY,
                    Key = null,
                    PhysicalKey = null,
                    Repeat = null,
                };
                break;

            case ControlInputKind.Key:
                if (!TryParseKeyAction(parsed.Action, out var keyAction) ||
                    string.IsNullOrWhiteSpace(parsed.Key))
                {
                    return false;
                }

                normalized = normalized with
                {
                    Nx = null,
                    Ny = null,
                    Action = FormatKeyAction(keyAction),
                    Button = null,
                    DeltaX = null,
                    DeltaY = null,
                    Key = parsed.Key.Trim(),
                };
                break;

            default:
                return false;
        }

        msg = normalized;
        return true;
    }

    public static bool TryDeserializeControlAck(ReadOnlySpan<byte> utf8Json, out ControlInputAckV1 ack)
    {
        ack = default!;
        if (!TryDeserialize(utf8Json, out ControlInputAckV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId) ||
            parsed.AckSeq <= 0 ||
            parsed.TsUtcMs <= 0)
        {
            return false;
        }

        ack = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
        };
        return true;
    }

    public static bool TryDeserializeControlStateSnapshot(ReadOnlySpan<byte> utf8Json, out ControlStateSnapshotV1 snapshot)
    {
        snapshot = default!;
        if (!TryDeserialize(utf8Json, out ControlStateSnapshotV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.RequestId) ||
            parsed.Seq <= 0 ||
            parsed.TsUtcMs <= 0)
        {
            return false;
        }

        snapshot = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            RequestId = parsed.RequestId.Trim(),
        };
        return true;
    }

    public static bool TryDeserializeControlDisplayInfo(ReadOnlySpan<byte> utf8Json, out ControlDisplayInfoMessageV1 msg)
    {
        msg = default!;
        if (!TryDeserialize(utf8Json, out ControlDisplayInfoMessageV1? parsed) ||
            parsed is null ||
            string.IsNullOrWhiteSpace(parsed.DisplayId) ||
            parsed.VirtualDesktopWidth <= 0 ||
            parsed.VirtualDesktopHeight <= 0 ||
            parsed.CaptureRegionWidth <= 0 ||
            parsed.CaptureRegionHeight <= 0 ||
            parsed.FrameWidth <= 0 ||
            parsed.FrameHeight <= 0 ||
            parsed.Revision < 0 ||
            parsed.TsUtcMs <= 0)
        {
            return false;
        }

        if (!IsFinitePositiveOrNull(parsed.DpiScale))
        {
            return false;
        }

        msg = parsed with
        {
            SessionId = NormalizeRequiredToken(parsed.SessionId),
            DisplayId = parsed.DisplayId.Trim(),
            DpiScale = NormalizeNullableFinitePositive(parsed.DpiScale),
        };
        return true;
    }

    private static bool TryDeserialize<T>(ReadOnlySpan<byte> utf8Json, out T? value)
    {
        value = default;
        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(utf8Json, JsonOptions);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequiredToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string[]? NormalizeCaps(string[]? caps)
    {
        if (caps is null || caps.Length == 0)
        {
            return null;
        }

        var normalized = caps
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static long? NormalizeTtl(long? ttlMs)
    {
        if (!ttlMs.HasValue)
        {
            return null;
        }

        return ttlMs.Value <= 0 ? null : ttlMs.Value;
    }

    private static long? NormalizeTimestamp(long? timestampUtcMs)
    {
        if (!timestampUtcMs.HasValue)
        {
            return null;
        }

        return timestampUtcMs.Value <= 0 ? null : timestampUtcMs.Value;
    }

    private static long? NormalizeRevision(long? revision)
    {
        if (!revision.HasValue)
        {
            return null;
        }

        return revision.Value <= 0 ? null : revision.Value;
    }

    private static bool TryNormalizeUnitCoordinate(double? value, out double normalized)
    {
        normalized = default;
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return false;
        }

        if (value.Value < 0d || value.Value > 1d)
        {
            return false;
        }

        normalized = value.Value;
        return true;
    }

    private static bool TryNormalizeFinite(double? value, out double normalized)
    {
        normalized = default;
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return false;
        }

        normalized = value.Value;
        return true;
    }

    private static bool IsFinitePositiveOrNull(double? value)
    {
        if (!value.HasValue)
        {
            return true;
        }

        return value.Value > 0d &&
               !double.IsNaN(value.Value) &&
               !double.IsInfinity(value.Value);
    }

    private static double? NormalizeNullableFinitePositive(double? value)
    {
        if (!value.HasValue ||
            value.Value <= 0d ||
            double.IsNaN(value.Value) ||
            double.IsInfinity(value.Value))
        {
            return null;
        }

        return value.Value;
    }

    private static bool TryParseInputKind(string? value, out ControlInputKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (CanonicalizeToken(value))
        {
            case "mousemove":
                kind = ControlInputKind.MouseMove;
                return true;
            case "mousebutton":
                kind = ControlInputKind.MouseButton;
                return true;
            case "mousewheel":
                kind = ControlInputKind.MouseWheel;
                return true;
            case "key":
                kind = ControlInputKind.Key;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseButtonAction(string? value, out ControlButtonAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (CanonicalizeToken(value))
        {
            case "down":
                action = ControlButtonAction.Down;
                return true;
            case "up":
                action = ControlButtonAction.Up;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseKeyAction(string? value, out ControlKeyAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (CanonicalizeToken(value))
        {
            case "down":
                action = ControlKeyAction.Down;
                return true;
            case "up":
                action = ControlKeyAction.Up;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseMouseButton(string? value, out ControlMouseButton button)
    {
        button = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (CanonicalizeToken(value))
        {
            case "left":
                button = ControlMouseButton.Left;
                return true;
            case "right":
                button = ControlMouseButton.Right;
                return true;
            case "middle":
                button = ControlMouseButton.Middle;
                return true;
            case "x1":
                button = ControlMouseButton.X1;
                return true;
            case "x2":
                button = ControlMouseButton.X2;
                return true;
            default:
                return false;
        }
    }

    private static string FormatInputKind(ControlInputKind kind)
    {
        return kind switch
        {
            ControlInputKind.MouseMove => "mouse_move",
            ControlInputKind.MouseButton => "mouse_button",
            ControlInputKind.MouseWheel => "mouse_wheel",
            ControlInputKind.Key => "key",
            _ => "unknown",
        };
    }

    private static string FormatMouseButton(ControlMouseButton button)
    {
        return button switch
        {
            ControlMouseButton.Left => "left",
            ControlMouseButton.Right => "right",
            ControlMouseButton.Middle => "middle",
            ControlMouseButton.X1 => "x1",
            ControlMouseButton.X2 => "x2",
            _ => "left",
        };
    }

    private static string FormatButtonAction(ControlButtonAction action)
    {
        return action switch
        {
            ControlButtonAction.Down => "down",
            ControlButtonAction.Up => "up",
            _ => "down",
        };
    }

    private static string FormatKeyAction(ControlKeyAction action)
    {
        return action switch
        {
            ControlKeyAction.Down => "down",
            ControlKeyAction.Up => "up",
            _ => "down",
        };
    }

    private static string CanonicalizeToken(string value)
    {
        var buffer = new char[value.Length];
        var written = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[written++] = char.ToLowerInvariant(ch);
            }
        }

        return written == 0 ? string.Empty : new string(buffer, 0, written);
    }
}
