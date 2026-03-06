using System;
using System.Runtime.InteropServices;

namespace NLink.App.Services.RemoteControl;

internal readonly record struct RemoteDesktopBounds(int Left, int Top, int Width, int Height);

internal static class RemoteDesktopMetrics
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static bool TryGetVirtualDesktopBounds(out RemoteDesktopBounds bounds)
    {
        bounds = default;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new RemoteDesktopBounds(left, top, width, height);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
