using System;

namespace NLink.App.Services.ScreenCapture;

internal sealed class WindowsRawCaptureFrameEventArgs : EventArgs
{
    public WindowsRawCaptureFrameEventArgs(WindowsRawCaptureFrame frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public WindowsRawCaptureFrame Frame { get; }
}
