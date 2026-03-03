using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed class ScreenCaptureAbstractionTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFactory_CreateDefault_ReturnsNonNull()
    {
        var source = ScreenCaptureFactory.CreateDefault();

        Assert.NotNull(source);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenCaptureFactory_CreateDefault_ReturnsExpectedPlatformSource()
    {
        var source = ScreenCaptureFactory.CreateDefault();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("WindowsScreenCaptureSource", source.GetType().Name);
            Assert.True(source.IsSupported);
            return;
        }

        Assert.False(source.IsSupported);
        Assert.IsType<NotSupportedCaptureSource>(source);

        try
        {
            await source.StartAsync(CancellationToken.None);
        }
        catch (NotSupportedException)
        {
            return;
        }

        await source.StopAsync();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithScreenShareChunk_RoutesToAssembler()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-route", "screenshare-route.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        ScreenShareFrameChunkV1? receivedChunk = null;
        NknIncomingMessage? receivedRawMessage = null;
        adapter.ScreenShareFrameChunkReceived += (_, chunk) => receivedChunk = chunk;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var chunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-1",
            FrameId = 42,
            Width = 1280,
            Height = 720,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        };

        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.Serialize(chunk));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.NotNull(receivedChunk);
        Assert.Null(receivedRawMessage);
        Assert.Equal(chunk.SessionId, receivedChunk!.SessionId);
        Assert.Equal(chunk.FrameId, receivedChunk.FrameId);
        Assert.Equal(chunk.Width, receivedChunk.Width);
        Assert.Equal(chunk.Height, receivedChunk.Height);
        Assert.Equal(chunk.Encoding, receivedChunk.Encoding);
        Assert.Equal(chunk.ChunkIndex, receivedChunk.ChunkIndex);
        Assert.Equal(chunk.ChunkCount, receivedChunk.ChunkCount);
        Assert.Equal(chunk.DataBase64, receivedChunk.DataBase64);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessages_WithCompleteScreenShareFrame_RaisesCompletedFrame()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-complete", "screenshare-complete.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        ScreenShareFrameCompletedEventArgs? completed = null;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;

        var frameBytes = new byte[] { 11, 22, 33, 44, 55, 66 };
        var firstChunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-2",
            FrameId = 77,
            Width = 1024,
            Height = 768,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 2,
            DataBase64 = Convert.ToBase64String(frameBytes[..3]),
        };

        var secondChunk = firstChunk with
        {
            ChunkIndex = 1,
            DataBase64 = Convert.ToBase64String(frameBytes[3..]),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(firstChunk));
        Assert.Null(completed);

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(secondChunk));

        Assert.NotNull(completed);
        Assert.Equal(77, completed!.FrameId);
        Assert.Equal(1024, completed.Width);
        Assert.Equal(768, completed.Height);
        Assert.Equal("jpeg", completed.Encoding);
        Assert.Equal(frameBytes, completed.EncodedFrameBytes);
    }

    private static string BuildBridgeMessageLine(ScreenShareFrameChunkV1 chunk)
    {
        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.Serialize(chunk));
        return $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1}}";
    }
}
