using System;
using System.Globalization;

namespace NLink.App.Configuration;

public static class FeatureFlags
{
    public static bool UsePhaseDrivenGating { get; } = false;

    public static bool EnableResponsiveLayout { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_RESPONSIVE_LAYOUT", defaultValue: true);

    public static bool EnableChatHardening { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_CHAT_HARDENING", defaultValue: true);

    public static bool EnableScreenShareScaffold { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCAFFOLD", defaultValue: true);

    public static bool EnableScreenShareCapture { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_CAPTURE", defaultValue: true);

    public static bool EnableScreenSharePreview { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_PREVIEW", defaultValue: true);

    public static bool EnableScreenShareTransport { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT", defaultValue: true);

    public static int ScreenShareMaxFps =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_SCREENCAP_MAX_FPS", defaultValue: 15, minValue: 1, maxValue: 30);

    public static int ScreenShareTransportMaxFps =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS", defaultValue: 8, minValue: 1, maxValue: 8);

    public static bool ScreenShareTransportAutoTuneEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", defaultValue: false);

    public static double ScreenShareScale =>
        ReadDoubleEnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", defaultValue: 0.75d, minValue: 0.25d, maxValue: 1d);

    public static long ScreenShareJpegQuality =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_SCREENCAP_JPEG_QUALITY", defaultValue: 75, minValue: 30, maxValue: 80);

    public static bool EnableSessionHeader { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SESSION_HEADER", defaultValue: true);

    public static bool RemoteControlAckEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_ACK", defaultValue: false);

    public static bool RemoteControlSeqGateEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", defaultValue: true);

    public static bool RemoteControlStateSnapshotEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", defaultValue: false);

    public static int RemoteControlStateSnapshotIntervalMs =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT_INTERVAL_MS", defaultValue: 50, minValue: 20, maxValue: 500);

    public static bool RemoteControlStateSnapshotForceDownEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT_FORCE_DOWN", defaultValue: false);

    private static bool ReadBoolEnvironmentOverride(string variableName, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            "0" => false,
            "false" => false,
            "FALSE" => false,
            "no" => false,
            "NO" => false,
            "off" => false,
            "OFF" => false,
            _ => false,
        };
    }

    private static int ReadIntEnvironmentOverride(string variableName, int defaultValue, int minValue, int maxValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw.Trim(), out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, minValue, maxValue);
    }

    private static double ReadDoubleEnvironmentOverride(string variableName, double defaultValue, double minValue, double maxValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var trimmed = raw.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ||
            double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
        {
            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                return defaultValue;
            }

            return Math.Clamp(parsed, minValue, maxValue);
        }

        return defaultValue;
    }
}
