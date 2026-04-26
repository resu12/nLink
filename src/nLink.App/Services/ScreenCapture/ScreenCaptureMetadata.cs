using System;

namespace NLink.App.Services.ScreenCapture;

public readonly record struct ScreenCapturePixelRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct ScreenCaptureMetadata(
    // Stable identifier for the active display target backing the current capture scope.
    string DisplayId,
    ScreenCapturePixelRect CaptureRegionPx,
    double? DpiScale);

internal interface IScreenCaptureMetadataSource
{
    bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata);
}
