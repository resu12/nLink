using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services.ScreenCapture;

internal interface IWindowsRawCaptureSource : IAsyncDisposable
{
    bool IsSupported { get; }

    event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
    event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();

    bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata);
}
