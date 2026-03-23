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
#pragma warning disable CS0067
    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;
#pragma warning restore CS0067

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
