using System;

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

    public static bool EnableSessionHeader { get; } =
        ReadBoolEnvironmentOverride("NLINK_FEATURE_SESSION_HEADER", defaultValue: true);

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
}
