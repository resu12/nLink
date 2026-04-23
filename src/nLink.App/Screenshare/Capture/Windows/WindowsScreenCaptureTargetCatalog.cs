using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal static class WindowsScreenCaptureTargetCatalog
{
    private const int MonitorInfoFPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RectStruct lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static IReadOnlyList<ScreenCaptureDisplayOption> GetDisplays()
    {
        var displays = new List<ScreenCaptureDisplayOption>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RectStruct __, IntPtr ___) =>
        {
            if (TryGetDisplayOption(monitor, out var display))
            {
                displays.Add(display);
            }

            return true;
        }, IntPtr.Zero);

        displays.Sort(static (left, right) =>
        {
            var primaryOrder = right.IsPrimary.CompareTo(left.IsPrimary);
            return primaryOrder != 0 ? primaryOrder : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        });
        return displays;
    }

    public static IReadOnlyList<ScreenCaptureWindowOption> GetWindows()
    {
        var windows = new List<ScreenCaptureWindowOption>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return true;
            }

            if (!TryGetWindowRect(hWnd, out var rect) ||
                rect.Right <= rect.Left ||
                rect.Bottom <= rect.Top)
            {
                return true;
            }

            var titleLength = GetWindowTextLengthW(hWnd);
            if (titleLength <= 0)
            {
                return true;
            }

            var buffer = new StringBuilder(titleLength + 1);
            _ = GetWindowTextW(hWnd, buffer, buffer.Capacity);
            var title = buffer.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            windows.Add(new ScreenCaptureWindowOption(
                Id: hWnd.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                Label: title,
                BoundsPx: new ScreenCapturePixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, IntPtr.Zero);

        windows.Sort(static (left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        return windows;
    }

    public static bool TryResolveTarget(
        ScreenCaptureTargetSelection selection,
        double? fallbackDpiScale,
        out ScreenCaptureMetadata metadata,
        out string reason)
    {
        metadata = default;
        reason = string.Empty;

        switch (selection.Mode)
        {
            case ScreenCaptureTargetMode.Display:
                if (!TryResolveDisplay(selection.DisplayId, fallbackDpiScale, out metadata, out reason))
                {
                    return false;
                }

                return true;

            case ScreenCaptureTargetMode.Window:
                if (!TryResolveWindow(selection.WindowId, fallbackDpiScale, out metadata, out reason))
                {
                    return false;
                }

                return true;

            case ScreenCaptureTargetMode.Region:
                if (!TryResolveRegion(selection, fallbackDpiScale, out metadata, out reason))
                {
                    return false;
                }

                return true;

            default:
                return TryResolvePrimaryDisplay(fallbackDpiScale, out metadata, out reason);
        }
    }

    private static bool TryResolvePrimaryDisplay(double? fallbackDpiScale, out ScreenCaptureMetadata metadata, out string reason)
    {
        foreach (var display in GetDisplays())
        {
            if (!display.IsPrimary)
            {
                continue;
            }

            metadata = new ScreenCaptureMetadata(display.Id, display.BoundsPx, display.DpiScale ?? fallbackDpiScale);
            reason = string.Empty;
            return true;
        }

        metadata = default;
        reason = "primary_display_missing";
        return false;
    }

    private static bool TryResolveDisplay(string? displayId, double? fallbackDpiScale, out ScreenCaptureMetadata metadata, out string reason)
    {
        if (string.IsNullOrWhiteSpace(displayId))
        {
            metadata = default;
            reason = "display_missing";
            return false;
        }

        foreach (var display in GetDisplays())
        {
            if (!string.Equals(display.Id, displayId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metadata = new ScreenCaptureMetadata(display.Id, display.BoundsPx, display.DpiScale ?? fallbackDpiScale);
            reason = string.Empty;
            return true;
        }

        metadata = default;
        reason = "display_not_found";
        return false;
    }

    private static bool TryResolveWindow(string? windowId, double? fallbackDpiScale, out ScreenCaptureMetadata metadata, out string reason)
    {
        if (string.IsNullOrWhiteSpace(windowId) ||
            !long.TryParse(windowId.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawHandle))
        {
            metadata = default;
            reason = "window_id_invalid";
            return false;
        }

        var hwnd = new IntPtr(rawHandle);
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            metadata = default;
            reason = "window_unavailable";
            return false;
        }

        if (!TryGetWindowRect(hwnd, out var rect) ||
            rect.Right <= rect.Left ||
            rect.Bottom <= rect.Top)
        {
            metadata = default;
            reason = "window_bounds_invalid";
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !TryGetDisplayOption(monitor, out var display))
        {
            metadata = default;
            reason = "window_display_missing";
            return false;
        }

        metadata = new ScreenCaptureMetadata(
            display.Id,
            new ScreenCapturePixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            display.DpiScale ?? fallbackDpiScale);
        reason = string.Empty;
        return true;
    }

    private static bool TryResolveRegion(
        ScreenCaptureTargetSelection selection,
        double? fallbackDpiScale,
        out ScreenCaptureMetadata metadata,
        out string reason)
    {
        if (!selection.RegionPx.IsValid)
        {
            metadata = default;
            reason = "region_invalid";
            return false;
        }

        if (!TryResolveDisplay(selection.DisplayId, fallbackDpiScale, out var displayMetadata, out reason))
        {
            metadata = default;
            return false;
        }

        var absoluteRegion = new ScreenCapturePixelRect(
            displayMetadata.CaptureRegionPx.X + selection.RegionPx.X,
            displayMetadata.CaptureRegionPx.Y + selection.RegionPx.Y,
            selection.RegionPx.Width,
            selection.RegionPx.Height);

        if (absoluteRegion.X < displayMetadata.CaptureRegionPx.X ||
            absoluteRegion.Y < displayMetadata.CaptureRegionPx.Y ||
            absoluteRegion.X + absoluteRegion.Width > displayMetadata.CaptureRegionPx.X + displayMetadata.CaptureRegionPx.Width ||
            absoluteRegion.Y + absoluteRegion.Height > displayMetadata.CaptureRegionPx.Y + displayMetadata.CaptureRegionPx.Height)
        {
            metadata = default;
            reason = "region_out_of_bounds";
            return false;
        }

        metadata = displayMetadata with
        {
            CaptureRegionPx = absoluteRegion,
        };
        reason = string.Empty;
        return true;
    }

    private static bool TryGetDisplayOption(IntPtr monitor, out ScreenCaptureDisplayOption display)
    {
        display = default!;
        var info = new MonitorInfoEx
        {
            CbSize = Marshal.SizeOf<MonitorInfoEx>(),
        };

        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        var bounds = new ScreenCapturePixelRect(
            info.RcMonitor.Left,
            info.RcMonitor.Top,
            info.RcMonitor.Right - info.RcMonitor.Left,
            info.RcMonitor.Bottom - info.RcMonitor.Top);
        if (!bounds.IsValid)
        {
            return false;
        }

        var isPrimary = (info.DwFlags & MonitorInfoFPrimary) != 0;
        var label = isPrimary
            ? $"Primary display ({info.SzDevice})"
            : $"Display {info.SzDevice}";
        display = new ScreenCaptureDisplayOption(
            info.SzDevice,
            label,
            bounds,
            isPrimary,
            DpiScale: null);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectStruct lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private static bool TryGetWindowRect(IntPtr hwnd, out RectStruct rect)
    {
        if (GetWindowRect(hwnd, out rect))
        {
            return true;
        }

        rect = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public RectStruct RcMonitor;
        public RectStruct RcWork;
        public int DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }
}
