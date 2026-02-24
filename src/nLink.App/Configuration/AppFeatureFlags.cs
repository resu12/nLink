using System;

namespace NLink.App.Configuration;

internal static class AppFeatureFlags
{
    public static bool UseEmbeddedWebView { get; } = GetDefaultUseEmbeddedWebView();

    private static bool GetDefaultUseEmbeddedWebView()
    {
        return OperatingSystem.IsWindows();
    }
}

