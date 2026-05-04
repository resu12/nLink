using System;

using NLink.Core.SessionConnect;

namespace NLink.App.Configuration;

internal static class AppFeatureFlags
{
    internal const string AllowInsecureUnboundPublicInvitesEnvVar = InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar;

    public static bool UseEmbeddedWebView { get; } = GetDefaultUseEmbeddedWebView();

    public static bool AllowInsecureUnboundPublicInvites =>
        InviteSecurityDiagnostics.AreUnboundPublicInvitesAllowed();

    private static bool GetDefaultUseEmbeddedWebView()
    {
        return OperatingSystem.IsWindows();
    }

}

