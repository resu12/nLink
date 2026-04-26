namespace NLink.App.Services.ScreenCapture;

internal interface IScreenCaptureCursorCaptureControl
{
    bool IsCursorCaptureControlSupported { get; }

    bool IsCursorCaptureEnabled { get; }

    bool TrySetCursorCaptureEnabled(bool enabled, string reason);
}
