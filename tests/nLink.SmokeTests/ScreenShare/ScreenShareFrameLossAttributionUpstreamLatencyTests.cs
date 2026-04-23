using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareFrameLossAttributionUpstreamLatencyTests
{
    [Fact]
    public void HelperUpstreamLatencySnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-upstream-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveFrameReady(sessionId, 1, 10, false, capturedTsUtcMs: 1000, frameReadyObservedUtcMs: 1300);
        ScreenShareFrameLossAttributionRegistry.ObserveViewerAccepted(sessionId, 1, 10, false, 1320);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeEnqueued(sessionId, 1, 10, false, 1325);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeStarted(sessionId, 1, 10, false, 1340);
        ScreenShareFrameLossAttributionRegistry.ObserveDecodeSucceeded(sessionId, 1, 10, false, 1390);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, false, 1400);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperUpstreamLatencySnapshot(sessionId);

        Assert.Equal(300, snapshot.CaptureToFrameReadyAvgMs);
        Assert.Equal(20, snapshot.FrameReadyToViewerAcceptAvgMs);
        Assert.Equal(5, snapshot.ViewerAcceptToDecodeEnqueueAvgMs);
        Assert.Equal(15, snapshot.DecodeEnqueueToDecodeStartAvgMs);
        Assert.Equal(340, snapshot.CaptureToDecodeStartAvgMs);
        Assert.Equal("capture_to_frame_ready", snapshot.DominantUpstreamLatencyStage);
        Assert.Equal(1, snapshot.WorstEpochByCaptureToDecodeStart);
        Assert.Equal(340, snapshot.WorstEpochCaptureToDecodeStartAvgMs);
    }
}
