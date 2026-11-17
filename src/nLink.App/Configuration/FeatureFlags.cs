using System;
using System.Globalization;
using NLink.Core.Configuration;

namespace NLink.App.Configuration;

public static class FeatureFlags
{
    public const string AllowInsecureRemoteControlSeqGateOverrideEnvVar = "NLINK_ALLOW_INSECURE_REMOTE_CONTROL_SEQ_GATE_OVERRIDE";
    public const string ScreenShareQualityProfileNormal = "normal";
    public const string ScreenShareQualityProfileTunaQuality = "tuna_quality";

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

    public static bool ScreenShareH264InfrastructureEnabled => true;

    public static bool ScreenShareH264DecodeEnabled => true;

    public static bool ScreenShareH264PreviewEnabled => true;

    public static bool ScreenShareDeepDiagnostics =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_DEEP_DIAGNOSTICS", defaultValue: false);

    public static int ScreenShareMaxFps =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_SCREENCAP_MAX_FPS", defaultValue: 15, minValue: 1, maxValue: 30);

    public static int ScreenShareTransportMaxFps =>
        ReadIntEnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS", defaultValue: 8, minValue: 1, maxValue: 15);

    public static bool ScreenShareTransportAutoTuneEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", defaultValue: true);

    public static double ScreenShareScale =>
        ReadDoubleEnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", defaultValue: 1d, minValue: 0.25d, maxValue: 1d);

    public static string ScreenShareQualityProfile =>
        ReadStringEnvironmentOverride(
            "NLINK_FEATURE_SCREENCAP_QUALITY_PROFILE",
            defaultValue: ScreenShareQualityProfileNormal,
            ScreenShareQualityProfileNormal,
            ScreenShareQualityProfileTunaQuality);

    public static bool EnableSessionHeader { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SESSION_HEADER", defaultValue: true);

    public static bool RemoteControlAckEnabled =>
        ReadBoolEnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_ACK", defaultValue: false);

    public static bool RemoteControlSeqGateEnabled =>
        ReadSecurityCriticalBoolEnvironmentOverride(
            "NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE",
            defaultValue: true,
            insecureOverrideOptInVariable: AllowInsecureRemoteControlSeqGateOverrideEnvVar);

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

    private static bool ReadSecurityCriticalBoolEnvironmentOverride(
        string variableName,
        bool defaultValue,
        string insecureOverrideOptInVariable)
    {
        var effective = ReadBoolEnvironmentOverride(variableName, defaultValue);
#if DEBUG
        return effective;
#else
        if (!defaultValue && effective)
        {
            return true;
        }

        if (defaultValue && !effective)
        {
            if (!ReleaseOverridePolicy.AllowUnsafeOverride(variableName, source: "env", category: "remote_control_sequence_gate"))
            {
                return defaultValue;
            }

            return ReadBoolEnvironmentOverride(insecureOverrideOptInVariable, defaultValue: false) &&
                   ReleaseOverridePolicy.AllowUnsafeOverride(insecureOverrideOptInVariable, source: "env", category: "remote_control_sequence_gate")
                ? effective
                : defaultValue;
        }

        return effective;
#endif
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

    private static string ReadStringEnvironmentOverride(string variableName, string defaultValue, params string[] allowedValues)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        foreach (var allowedValue in allowedValues)
        {
            if (string.Equals(normalized, allowedValue, StringComparison.Ordinal))
            {
                return allowedValue;
            }
        }

        return defaultValue;
    }
}
