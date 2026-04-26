using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests.Fakes;

internal sealed class FakeScreenCaptureSource : IScreenCaptureSource, IScreenCaptureMetadataSource, IScreenCaptureKeyFrameRequestSource, IScreenCaptureCursorCaptureControl, IAsyncDisposable
{
    private EventHandler<ScreenCaptureFrameEventArgs>? frameArrived;
    private readonly List<string> keyFrameRequestReasons = new();
    private readonly List<bool> cursorCaptureEnabledRequests = new();
    private bool cursorCaptureEnabled = true;

    public bool IsSupported => true;

    public bool IsCursorCaptureControlSupported { get; set; } = true;

    public bool IsCursorCaptureEnabled => cursorCaptureEnabled;

    public IReadOnlyList<bool> CursorCaptureEnabledRequests => cursorCaptureEnabledRequests;

    public bool IsStarted { get; private set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public int FrameSubscriberCount { get; private set; }

    public int KeyFrameRequestCount => keyFrameRequestReasons.Count;

    public IReadOnlyList<string> KeyFrameRequestReasons => keyFrameRequestReasons;

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

    public void RequestKeyFrame(string reason)
    {
        keyFrameRequestReasons.Add(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason.Trim());
    }

    public bool TrySetCursorCaptureEnabled(bool enabled, string reason)
    {
        cursorCaptureEnabledRequests.Add(enabled);
        if (!IsCursorCaptureControlSupported)
        {
            cursorCaptureEnabled = true;
            return false;
        }

        cursorCaptureEnabled = enabled;
        return true;
    }

    public void RaiseFrame(ScreenCaptureFrameEventArgs frame)
    {
        frameArrived?.Invoke(this, frame);
    }

    public void RaiseFrame(
        int width,
        int height,
        byte[] encodedFrameData,
        string encoding,
        long capturedTsUtcMs = 0,
        bool isKeyFrame = false,
        long streamEpoch = 0,
        ScreenShareVideoStreamConfigV1? streamConfig = null)
    {
        frameArrived?.Invoke(
            this,
            new ScreenCaptureFrameEventArgs(
                width,
                height,
                encodedFrameData,
                encoding,
                capturedTsUtcMs,
                isKeyFrame,
                streamEpoch,
                streamConfig));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        IsStarted = false;
        frameArrived = null;
        FrameSubscriberCount = 0;
        keyFrameRequestReasons.Clear();
        cursorCaptureEnabledRequests.Clear();
        cursorCaptureEnabled = true;
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
