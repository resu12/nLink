namespace NLink.App.Services.ScreenCapture;

internal static class ScreenCaptureDisplayIds
{
    // Current v0.4.5 behavior captures the primary display only.
    // TODO(v0.5.0-P7): replace with selected monitor/region display identifiers once
    // monitor selection UI and capture target selection are implemented.
    public const string Primary = "primary";
}
