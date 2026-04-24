using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareFrameLossAttributionReadyPathTests
{
    [Fact]
    public void HelperReadyPathSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-ready-path-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveFrameReady(
            sessionId,
            1,
            10,
            false,
            capturedTsUtcMs: 1000,
            frameReadyObservedUtcMs: 1200);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 10, false, 1060);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 10, false, 1140);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameEmitted(sessionId, 1, 10, false, 1215);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReadyPathSnapshot(sessionId);

        Assert.Equal(60, snapshot.CaptureToFirstFragmentObservedAvgMs);
        Assert.Equal(80, snapshot.FirstFragmentToLastFragmentObservedAvgMs);
        Assert.Equal(60, snapshot.LastFragmentToAssemblyCompleteAvgMs);
        Assert.Equal(15, snapshot.AssemblyCompleteToFrameEmittedAvgMs);
        Assert.Equal("first_fragment_to_last_fragment_observed", snapshot.DominantReadyPathStage);
    }

    [Fact]
    public void HelperReadyPathSnapshot_TracksCompletedFrameThroughReassemblerEmission()
    {
        const string sessionId = "helper-ready-path-reassembler";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        var reassembler = new ScreenShareVideoFrameReassembler();
        var emittedFrameIds = new List<long>();
        reassembler.FrameReady += (_, args) => emittedFrameIds.Add(args.FrameId);
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        reassembler.OnFragment(CreateFragment(sessionId, 1, 0, 0, isKeyFrame: true));
        reassembler.OnFragment(CreateFragment(sessionId, 1, 0, 1, isKeyFrame: true));

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReadyPathSnapshot(sessionId);

        Assert.Contains(0L, emittedFrameIds);
        Assert.True(snapshot.CaptureToFirstFragmentObservedAvgMs >= 0);
        Assert.True(snapshot.FirstFragmentToLastFragmentObservedAvgMs >= 0);
        Assert.True(snapshot.LastFragmentToAssemblyCompleteAvgMs >= 0);
        Assert.True(snapshot.AssemblyCompleteToFrameEmittedAvgMs >= 0);
        Assert.NotEqual("none", snapshot.DominantReadyPathStage);
    }

    private static ScreenShareVideoFragmentV1 CreateFragment(
        string sessionId,
        long streamEpoch,
        long frameId,
        int fragmentIndex,
        bool isKeyFrame)
    {
        return new ScreenShareVideoFragmentV1
        {
            Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            Width = 640,
            Height = 360,
            CapturedTsUtcMs = (streamEpoch * 1000) + frameId,
            Encoding = "h264",
            IsKeyFrame = isKeyFrame,
            FragmentIndex = fragmentIndex,
            FragmentCount = 2,
            Data = new byte[] { (byte)frameId, (byte)fragmentIndex },
        };
    }
}
