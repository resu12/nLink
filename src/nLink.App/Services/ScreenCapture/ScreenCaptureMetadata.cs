using System;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenCapturePixelRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct ScreenCaptureMetadata(
    // Stable identifier for the active capture target (currently "primary").
    // TODO(v0.5.0-P7): support monitor/region-specific identifiers from selection UI.
    string DisplayId,
    ScreenCapturePixelRect CaptureRegionPx,
    double? DpiScale);

internal interface IScreenCaptureMetadataSource
{
    bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata);
}
