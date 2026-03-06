using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests.Fakes;

internal sealed class FakeScreenCaptureSource : IScreenCaptureSource, IScreenCaptureMetadataSource, IAsyncDisposable
{
    private EventHandler<ScreenCaptureFrameEventArgs>? frameArrived;

    public bool IsSupported => true;

    public bool IsStarted { get; private set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public int FrameSubscriberCount { get; private set; }

    public Exception? StartException { get; set; }
    public TaskCompletionSource<bool>? StopBlocker { get; set; }
    public ScreenCaptureMetadata? CaptureMetadata { get; set; }

    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived
    {
        add
        {
            frameArrived += value;
            FrameSubscriberCount++;
        }
        remove
        {
            frameArrived -= value;
            if (FrameSubscriberCount > 0)
            {
                FrameSubscriberCount--;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCallCount++;
        if (StartException is not null)
        {
            throw StartException;
        }

        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCallCount++;
        IsStarted = false;
        return StopBlocker?.Task ?? Task.CompletedTask;
    }

    public void RaiseFrame(ScreenCaptureFrameEventArgs frame)
    {
        frameArrived?.Invoke(this, frame);
    }

    public void RaiseFrame(int width, int height, byte[] encodedFrameData, string encoding)
    {
        frameArrived?.Invoke(this, new ScreenCaptureFrameEventArgs(width, height, encodedFrameData, encoding));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        IsStarted = false;
        frameArrived = null;
        FrameSubscriberCount = 0;
        return ValueTask.CompletedTask;
    }

    public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
    {
        if (CaptureMetadata.HasValue)
        {
            metadata = CaptureMetadata.Value;
            return true;
        }

        metadata = default;
        return false;
    }
}
