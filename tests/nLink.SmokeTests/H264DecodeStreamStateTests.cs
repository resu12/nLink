using System;
using Avalonia.Media.Imaging;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class H264DecodeStreamStateTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void H264DecodeStreamState_DropsFramesUntilMatchingConfigArrives()
    {
        var h264Decoder = new FakeH264BitmapDecoder();
        var decoder = new EncodedFrameBitmapDecoder(_ => throw new InvalidOperationException("jpeg should not be used"), h264Decoder);
        var state = new H264DecodeStreamState(decoder);

        var initial = state.Prepare("h264", streamEpoch: 7, streamConfig: null);

        Assert.False(initial.ShouldDecode);
        Assert.False(initial.ConfigApplied);
        Assert.Equal(0, initial.ConfiguredStreamEpoch);
        Assert.Equal(1, h264Decoder.ResetCallCount);

        var configured = state.Prepare(
            "h264",
            streamEpoch: 7,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session",
                StreamEpoch = 7,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            });

        Assert.True(configured.ShouldDecode);
        Assert.True(configured.ConfigApplied);
        Assert.Equal(7, configured.ConfiguredStreamEpoch);
        Assert.Equal(1, h264Decoder.ConfigureCallCount);
        Assert.Equal(7, h264Decoder.LastConfiguredEpoch);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void H264DecodeStreamState_EpochChange_ResetsDecoderAndInvokesCallback()
    {
        var h264Decoder = new FakeH264BitmapDecoder();
        var decoder = new EncodedFrameBitmapDecoder(_ => throw new InvalidOperationException("jpeg should not be used"), h264Decoder);
        var state = new H264DecodeStreamState(decoder);
        var epochChangedCallbacks = 0;

        _ = state.Prepare(
            "h264",
            streamEpoch: 7,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session",
                StreamEpoch = 7,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            });

        var updated = state.Prepare(
            "h264",
            streamEpoch: 8,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session",
                StreamEpoch = 8,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 4, 5, 6 },
            },
            onEpochChanged: () => epochChangedCallbacks++);

        Assert.True(updated.ShouldDecode);
        Assert.True(updated.ConfigApplied);
        Assert.Equal(8, updated.ConfiguredStreamEpoch);
        Assert.Equal(1, epochChangedCallbacks);
        Assert.Equal(1, h264Decoder.ResetCallCount);
        Assert.Equal(2, h264Decoder.ConfigureCallCount);
        Assert.Equal(8, h264Decoder.LastConfiguredEpoch);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void H264DecodeStreamState_NonH264Encoding_PassesThroughWithoutTouchingDecoder()
    {
        var h264Decoder = new FakeH264BitmapDecoder();
        var decoder = new EncodedFrameBitmapDecoder(_ => throw new InvalidOperationException("jpeg should not be used"), h264Decoder);
        var state = new H264DecodeStreamState(decoder);

        var result = state.Prepare("jpeg", streamEpoch: 0, streamConfig: null);

        Assert.True(result.ShouldDecode);
        Assert.False(result.ConfigApplied);
        Assert.Equal(0, h264Decoder.ConfigureCallCount);
        Assert.Equal(0, h264Decoder.ResetCallCount);
    }

    private sealed class FakeH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int ConfigureCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public void Reset()
        {
            ResetCallCount++;
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }
}
