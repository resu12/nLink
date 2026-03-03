using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Screen-capture source used on platforms where capture is not available.
/// </summary>
public sealed class NotSupportedCaptureSource : IScreenCaptureSource
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        throw new PlatformNotSupportedException("Screen capture is not supported on this platform.");
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        throw new PlatformNotSupportedException("Screen capture is not supported on this platform.");
    }
}
