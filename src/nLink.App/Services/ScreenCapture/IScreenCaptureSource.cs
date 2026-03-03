using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Provides a platform-specific source of encoded screen-capture frames.
/// </summary>
public interface IScreenCaptureSource
{
    /// <summary>
    /// Gets a value indicating whether screen capture is supported on the current platform.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Raised when a new encoded frame is available.
    /// </summary>
    event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

    /// <summary>
    /// Starts screen capture.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for startup.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops screen capture and releases any owned resources.
    /// </summary>
    Task StopAsync();
}
