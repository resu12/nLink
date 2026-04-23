using System;

namespace NLink.App.Services.ScreenCapture;

internal sealed class WindowsRawCaptureFailureEventArgs : EventArgs
{
    public WindowsRawCaptureFailureEventArgs(string stage, string reason, string? message, bool isFatal = false)
    {
        Stage = string.IsNullOrWhiteSpace(stage) ? "(unknown)" : stage.Trim();
        Reason = string.IsNullOrWhiteSpace(reason) ? "(unknown)" : reason.Trim();
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        IsFatal = isFatal;
    }

    public string Stage { get; }

    public string Reason { get; }

    public string? Message { get; }

    public bool IsFatal { get; }
}
