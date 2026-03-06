using System;

namespace NLink.App.Services.RemoteControl;

internal sealed class DefaultRemoteCoordinateMapper : IRemoteCoordinateMapper
{
    public bool IsMappingAvailable => RemoteDesktopMetrics.TryGetVirtualDesktopBounds(out _);

    public (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny)
    {
        if (!RemoteDesktopMetrics.TryGetVirtualDesktopBounds(out var bounds))
        {
            throw new InvalidOperationException("Virtual desktop bounds are not available.");
        }

        return MapNormalizedToBounds(nx, ny, bounds);
    }

    internal static (int xPx, int yPx) MapNormalizedToBounds(double nx, double ny, RemoteDesktopBounds bounds)
    {
        var clampedNx = Math.Clamp(nx, 0d, 1d);
        var clampedNy = Math.Clamp(ny, 0d, 1d);

        // TODO(v0.5.0-P7): replace virtual-desktop fallback with capture/display descriptors
        // from the active helpee stream target to support precise multi-display routing.
        var xPx = bounds.Left + (int)Math.Round(clampedNx * (bounds.Width - 1d), MidpointRounding.AwayFromZero);
        var yPx = bounds.Top + (int)Math.Round(clampedNy * (bounds.Height - 1d), MidpointRounding.AwayFromZero);
        return (xPx, yPx);
    }
}
