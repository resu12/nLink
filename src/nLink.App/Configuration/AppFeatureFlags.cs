using System;

using NLink.Core.SessionConnect;

namespace NLink.App.Configuration;

internal static class AppFeatureFlags
{
    internal const string AllowInsecureUnboundPublicInvitesEnvVar = InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar;

    public static bool UseEmbeddedWebView { get; } = GetDefaultUseEmbeddedWebView();

    public static bool AllowInsecureUnboundPublicInvites =>
        ReadBoolEnvironmentOverride(AllowInsecureUnboundPublicInvitesEnvVar, defaultValue: false);

    private static bool GetDefaultUseEmbeddedWebView()
    {
        return OperatingSystem.IsWindows();
    }

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
            _ => defaultValue,
        };
    }
}

