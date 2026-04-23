using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareFrameContractsTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFrameEventArgs_DefaultVideoMetadata_RemainsJpegCompatible()
    {
        var frame = new ScreenCaptureFrameEventArgs(640, 360, new byte[] { 0x01 }, "jpeg");

        Assert.Equal("jpeg", frame.Encoding);
        Assert.False(frame.IsKeyFrame);
        Assert.Equal(0, frame.StreamEpoch);
        Assert.Null(frame.StreamConfig);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameCompletedEventArgs_DefaultVideoMetadata_RemainsJpegCompatible()
    {
        var frame = new ScreenShareFrameCompletedEventArgs(7, 640, 360, "jpeg", new byte[] { 0x01 });

        Assert.Equal("jpeg", frame.Encoding);
        Assert.False(frame.IsKeyFrame);
        Assert.Equal(0, frame.StreamEpoch);
        Assert.Null(frame.StreamConfig);
    }
}
