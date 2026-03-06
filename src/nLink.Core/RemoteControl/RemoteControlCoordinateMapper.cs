namespace NLink.Core.RemoteControl;

public enum RemoteControlViewerStretchMode
{
    Uniform,
    Fill,
    Stretch,
}

public static class RemoteControlCoordinateMapper
{
    // TODO(v0.5.0-P7): add reverse mapping from normalized coordinates + display descriptors
    // to absolute OS coordinates for pointer-echo and verification tooling.
    public static bool TryMapPointerToNormalized(
        double pointerX,
        double pointerY,
        double viewerWidth,
        double viewerHeight,
        double frameWidth,
        double frameHeight,
        out double nx,
        out double ny,
        RemoteControlViewerStretchMode stretchMode = RemoteControlViewerStretchMode.Uniform)
    {
        nx = 0d;
        ny = 0d;
        if (viewerWidth <= 0d || viewerHeight <= 0d || frameWidth <= 0d || frameHeight <= 0d)
        {
            return false;
        }

        if (!TryComputeContentLayout(
                viewerWidth,
                viewerHeight,
                frameWidth,
                frameHeight,
                stretchMode,
                out var contentWidth,
                out var contentHeight,
                out var offsetX,
                out var offsetY))
        {
            return false;
        }

        nx = Math.Clamp((pointerX - offsetX) / contentWidth, 0d, 1d);
        ny = Math.Clamp((pointerY - offsetY) / contentHeight, 0d, 1d);
        return true;
    }

    private static bool TryComputeContentLayout(
        double viewerWidth,
        double viewerHeight,
        double frameWidth,
        double frameHeight,
        RemoteControlViewerStretchMode stretchMode,
        out double contentWidth,
        out double contentHeight,
        out double offsetX,
        out double offsetY)
    {
        contentWidth = 0d;
        contentHeight = 0d;
        offsetX = 0d;
        offsetY = 0d;

        switch (stretchMode)
        {
            case RemoteControlViewerStretchMode.Uniform:
            {
                var scale = Math.Min(viewerWidth / frameWidth, viewerHeight / frameHeight);
                if (!IsFinitePositive(scale))
                {
                    return false;
                }

                contentWidth = frameWidth * scale;
                contentHeight = frameHeight * scale;
                break;
            }
            case RemoteControlViewerStretchMode.Fill:
            {
                var scale = Math.Max(viewerWidth / frameWidth, viewerHeight / frameHeight);
                if (!IsFinitePositive(scale))
                {
                    return false;
                }

                contentWidth = frameWidth * scale;
                contentHeight = frameHeight * scale;
                break;
            }
            case RemoteControlViewerStretchMode.Stretch:
                contentWidth = viewerWidth;
                contentHeight = viewerHeight;
                break;
            default:
                return false;
        }

        if (!IsFinitePositive(contentWidth) || !IsFinitePositive(contentHeight))
        {
            return false;
        }

        offsetX = (viewerWidth - contentWidth) / 2d;
        offsetY = (viewerHeight - contentHeight) / 2d;
        return true;
    }

    private static bool IsFinitePositive(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
}
