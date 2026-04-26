using System;
using System.Drawing;
using System.Runtime.Versioning;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRawCaptureFrame : IDisposable
{
    public WindowsRawCaptureFrame(Bitmap bitmap, long capturedTsUtcMs = 0)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
    }

    public Bitmap Bitmap { get; }

    public long CapturedTsUtcMs { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
