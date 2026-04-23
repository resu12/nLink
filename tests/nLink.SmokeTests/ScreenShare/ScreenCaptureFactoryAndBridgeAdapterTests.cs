using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using System.Collections.Concurrent;
using System.Reflection;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class ScreenCaptureFactoryAndBridgeAdapterTests : ScreenCaptureAbstractionTestBase
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
            if (WindowsH264ScreenCaptureSource.IsPreviewRuntimeSupported())
            {
                Assert.Equal("WindowsH264ScreenCaptureSource", source.GetType().Name);
                Assert.True(source.IsSupported);
                return;
            }
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
    public void RealNknClientAdapter_BridgeMessage_WithUnknownPayload_RemainsRawMessage()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-unknown", "screenshare-unknown.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;
        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";
        adapter.HandleStdoutJsonLineForTests(line);
        Assert.NotNull(receivedRawMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_MediaChannelUnknownPayload_RemainsClassifiedAsMedia()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-media-unknown", "screenshare-media-unknown.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;
        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"channel\":\"media\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";
        adapter.HandleStdoutJsonLineForTests(line);
        Assert.NotNull(receivedRawMessage);
        Assert.Equal(NknBridgeChannel.Media, receivedRawMessage!.Channel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_ScreenShareQueueStateEvent_UpdatesBridgeQueueSnapshot()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-queue-state", "screenshare-queue-state.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        var capability = Assert.IsAssignableFrom<IBridgeScreenShareQueueCapability>(adapter);
        BridgeScreenShareQueueStateChangedEventArgs? received = null;
        capability.ScreenShareQueueStateChanged += (_, e) => received = e;
        adapter.HandleStdoutJsonLineForTests("{\"event\":\"screen_share_queue_state\",\"queueDepth\":9,\"queuedBytes\":131072,\"oldestQueuedAgeMs\":275,\"inFlight\":true,\"droppedSinceLast\":2,\"congested\":true,\"severe\":false,\"mode\":\"catch_up_only\"}");
        Assert.NotNull(received);
        Assert.Equal(9, received!.State.QueueDepth);
        Assert.Equal(131072, received.State.QueuedBytes);
        Assert.Equal(275, received.State.OldestQueuedAgeMs);
        Assert.True(received.State.InFlight);
        Assert.Equal(2, received.State.DroppedSinceLast);
        Assert.True(received.State.IsCongested);
        Assert.False(received.State.IsSevere);
        Assert.Equal(BridgeScreenShareQueueMode.CatchUpOnly, received.State.Mode);
        Assert.Equal(received.State, capability.CurrentScreenShareQueueState);
    }

}
