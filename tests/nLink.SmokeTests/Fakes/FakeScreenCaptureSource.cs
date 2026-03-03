using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests.Fakes;

internal sealed class FakeScreenCaptureSource : IScreenCaptureSource
{
    public bool IsSupported => true;

    public bool IsStarted { get; private set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCallCount++;
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
}
