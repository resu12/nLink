using NLink.App.Services.ScreenCapture;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FakeScreenCaptureSourceTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FakeScreenCaptureSource_RaiseFrame_TriggersFrameArrived()
    {
        var source = new FakeScreenCaptureSource();
        ScreenCaptureFrameEventArgs? received = null;
        source.FrameArrived += (_, frame) => received = frame;

        await source.StartAsync(CancellationToken.None);
        source.RaiseFrame(640, 360, new byte[] { 1, 2, 3 }, "jpeg");
        await source.StopAsync();

        Assert.False(source.IsStarted);
        Assert.NotNull(received);
        Assert.Equal(640, received!.Width);
        Assert.Equal(360, received.Height);
        Assert.Equal("jpeg", received.Encoding);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.EncodedFrameData);
        Assert.False(received.IsKeyFrame);
        Assert.Equal(0, received.StreamEpoch);
    }
}
