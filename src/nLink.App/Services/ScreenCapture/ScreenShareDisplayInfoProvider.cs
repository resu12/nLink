using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NLink.App.Services.RemoteControl;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareDisplayInfoSnapshot(
    string DisplayId,
    int VirtualDesktopX,
    int VirtualDesktopY,
    int VirtualDesktopWidth,
    int VirtualDesktopHeight,
    int CaptureRegionX,
    int CaptureRegionY,
    int CaptureRegionWidth,
    int CaptureRegionHeight,
    int FrameWidth,
    int FrameHeight,
    double? DpiScale);

internal sealed class ScreenShareDisplayInfoProvider
{
    // Default/fallback until monitor selection is available.
    private const string DefaultDisplayId = ScreenCaptureDisplayIds.Primary;

    public bool TryGetSnapshot(
        IScreenCaptureSource captureSource,
        int frameWidth,
        int frameHeight,
        out ScreenShareDisplayInfoSnapshot snapshot,
        out string reason)
    {
        snapshot = default;
        reason = string.Empty;

        if (captureSource is null)
        {
            reason = "capture_source_missing";
            return false;
        }

        if (frameWidth <= 0 || frameHeight <= 0)
        {
            reason = "invalid_frame_size";
            return false;
        }

        var displayId = DefaultDisplayId;
        var captureRegion = new ScreenCapturePixelRect(0, 0, frameWidth, frameHeight);
        double? dpiScale = null;
        if (captureSource is IScreenCaptureMetadataSource metadataSource &&
            metadataSource.TryGetCaptureMetadata(out var metadata))
        {
            if (!string.IsNullOrWhiteSpace(metadata.DisplayId))
            {
                displayId = metadata.DisplayId.Trim();
            }

            if (metadata.CaptureRegionPx.IsValid)
            {
                captureRegion = metadata.CaptureRegionPx;
            }

            if (metadata.DpiScale.HasValue &&
                metadata.DpiScale.Value > 0d &&
                !double.IsNaN(metadata.DpiScale.Value) &&
                !double.IsInfinity(metadata.DpiScale.Value))
            {
                dpiScale = metadata.DpiScale.Value;
            }

            // TODO(v0.5.0-P7): once monitor selection exists, assert metadata.DisplayId and
            // metadata.CaptureRegionPx represent the currently selected capture target.
        }

        var virtualDesktopX = captureRegion.X;
        var virtualDesktopY = captureRegion.Y;
        var virtualDesktopWidth = captureRegion.Width;
        var virtualDesktopHeight = captureRegion.Height;

        if (RemoteDesktopMetrics.TryGetVirtualDesktopBounds(out var virtualBounds))
        {
            virtualDesktopX = virtualBounds.Left;
            virtualDesktopY = virtualBounds.Top;
            virtualDesktopWidth = virtualBounds.Width;
            virtualDesktopHeight = virtualBounds.Height;
        }
        else if (TryGetAvaloniaVirtualDesktopBounds(out var avaloniaBounds, out var avaloniaDpiScale))
        {
            virtualDesktopX = avaloniaBounds.X;
            virtualDesktopY = avaloniaBounds.Y;
            virtualDesktopWidth = avaloniaBounds.Width;
            virtualDesktopHeight = avaloniaBounds.Height;
            if (!dpiScale.HasValue)
            {
                dpiScale = avaloniaDpiScale;
            }
        }

        if (virtualDesktopWidth <= 0 || virtualDesktopHeight <= 0)
        {
            reason = "virtual_desktop_invalid";
            return false;
        }

        snapshot = new ScreenShareDisplayInfoSnapshot(
            DisplayId: displayId,
            VirtualDesktopX: virtualDesktopX,
            VirtualDesktopY: virtualDesktopY,
            VirtualDesktopWidth: virtualDesktopWidth,
            VirtualDesktopHeight: virtualDesktopHeight,
            CaptureRegionX: captureRegion.X,
            CaptureRegionY: captureRegion.Y,
            CaptureRegionWidth: captureRegion.Width,
            CaptureRegionHeight: captureRegion.Height,
            FrameWidth: frameWidth,
            FrameHeight: frameHeight,
            DpiScale: dpiScale);
        return true;
    }

    private static bool TryGetAvaloniaVirtualDesktopBounds(out PixelRect bounds, out double? dpiScale)
    {
        bounds = default;
        dpiScale = null;

        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow?.Screens is not { } screens ||
                screens.All.Count == 0)
            {
                return false;
            }

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            foreach (var screen in screens.All)
            {
                var b = screen.Bounds;
                if (b.Width <= 0 || b.Height <= 0)
                {
                    continue;
                }

                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.X + b.Width);
                maxY = Math.Max(maxY, b.Y + b.Height);
            }

            if (minX == int.MaxValue || minY == int.MaxValue || maxX <= minX || maxY <= minY)
            {
                return false;
            }

            bounds = new PixelRect(minX, minY, maxX - minX, maxY - minY);
            var primary = screens.Primary;
            if (primary is not null &&
                primary.Scaling > 0d &&
                !double.IsNaN(primary.Scaling) &&
                !double.IsInfinity(primary.Scaling))
            {
                // TODO(v0.5.0-P7): use selected-monitor DPI instead of primary scaling for
                // mixed-DPI multi-monitor setups.
                dpiScale = primary.Scaling;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
