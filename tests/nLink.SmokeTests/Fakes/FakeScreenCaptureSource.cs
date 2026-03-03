using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests.Fakes;

internal sealed class FakeScreenCaptureSource : IScreenCaptureSource, IAsyncDisposable
{
    public bool IsSupported => true;

    public bool IsStarted { get; private set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public Exception? StartException { get; set; }

    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

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
        return Task.CompletedTask;
    }

    public void RaiseFrame(ScreenCaptureFrameEventArgs frame)
    {
        FrameArrived?.Invoke(this, frame);
    }

    public void RaiseFrame(int width, int height, byte[] encodedFrameData, string encoding)
    {
        FrameArrived?.Invoke(this, new ScreenCaptureFrameEventArgs(width, height, encodedFrameData, encoding));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        IsStarted = false;
        return ValueTask.CompletedTask;
    }
}
