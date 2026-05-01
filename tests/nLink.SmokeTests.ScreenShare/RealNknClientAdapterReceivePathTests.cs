using NLink.Core;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class RealNknClientAdapterReceivePathTests
{
    [Fact]
    public void BridgeMessage_StampsBridgeIngressObservedUtcMs()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-receive-path", "screenshare-receive-path.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"channel\":\"media\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.NotNull(receivedRawMessage);
        Assert.True(receivedRawMessage!.BridgeIngressObservedUtcMs > 0);
    }

    [Fact]
    public void MediaBinaryMessage_RoundTripsVersionedBridgeObservedAndSdkTimestamps()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-receive-path-binary", "screenshare-receive-path-binary.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Media,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: "ver=2;b=1731000000123;h=1731000000100;c=1731000000110;m=1731000000120",
                Payload: new byte[] { 1, 2, 3 })
            {
                BinaryFrameDecodedUtcMs = 1731000000456,
            });

        Assert.NotNull(receivedRawMessage);
        Assert.Equal(NknBridgeChannel.Media, receivedRawMessage!.Channel);
        Assert.Equal(1731000000123, receivedRawMessage.BridgeMessageObservedUtcMs);
        Assert.Equal(1731000000456, receivedRawMessage.BinaryFrameDecodedUtcMs);
        Assert.Equal(1731000000100, receivedRawMessage.SdkHandleMsgEnteredUtcMs);
        Assert.Equal(1731000000110, receivedRawMessage.ClientMessageDispatchUtcMs);
        Assert.Equal(1731000000120, receivedRawMessage.MultiClientMessageDispatchUtcMs);
        Assert.True(receivedRawMessage.BridgeIngressObservedUtcMs >= 1731000000456);
    }

    [Fact]
    public void MediaBinaryMessage_RoundTripsVersionedWsAndSdkReceiveTimestamps()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-receive-path-binary-ws", "screenshare-receive-path-binary-ws.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Media,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: "ver=3;b=1731000000123;r=1731000000090;w=1731000000098;h=1731000000100;c=1731000000110;m=1731000000120",
                Payload: new byte[] { 1, 2, 3 })
            {
                BinaryFrameDecodedUtcMs = 1731000000456,
            });

        Assert.NotNull(receivedRawMessage);
        Assert.Equal(1731000000123, receivedRawMessage!.BridgeMessageObservedUtcMs);
        Assert.Equal(1731000000090, receivedRawMessage.WsReceiverWriteEnteredUtcMs);
        Assert.Equal(1731000000098, receivedRawMessage.WsMessageEmittedUtcMs);
        Assert.Equal(1731000000100, receivedRawMessage.SdkHandleMsgEnteredUtcMs);
        Assert.Equal(1731000000110, receivedRawMessage.ClientMessageDispatchUtcMs);
        Assert.Equal(1731000000120, receivedRawMessage.MultiClientMessageDispatchUtcMs);
    }

    [Fact]
    public void MediaBinaryMessage_RoundTripsVersionedSocketWsAndSdkReceiveTimestamps()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-receive-path-binary-socket", "screenshare-receive-path-binary-socket.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Media,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: "ver=4;b=1731000000123;s=1731000000080;r=1731000000090;w=1731000000098;h=1731000000100;c=1731000000110;m=1731000000120",
                Payload: new byte[] { 1, 2, 3 })
            {
                BinaryFrameDecodedUtcMs = 1731000000456,
            });

        Assert.NotNull(receivedRawMessage);
        Assert.Equal(1731000000123, receivedRawMessage!.BridgeMessageObservedUtcMs);
        Assert.Equal(1731000000080, receivedRawMessage.SocketDataEventEmittedUtcMs);
        Assert.Equal(1731000000090, receivedRawMessage.WsReceiverWriteEnteredUtcMs);
        Assert.Equal(1731000000098, receivedRawMessage.WsMessageEmittedUtcMs);
        Assert.Equal(1731000000100, receivedRawMessage.SdkHandleMsgEnteredUtcMs);
        Assert.Equal(1731000000110, receivedRawMessage.ClientMessageDispatchUtcMs);
        Assert.Equal(1731000000120, receivedRawMessage.MultiClientMessageDispatchUtcMs);
    }

    [Fact]
    public void MediaBinaryMessage_LegacyBridgeObservedTimestampStillParses()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-receive-path-binary-legacy", "screenshare-receive-path-binary-legacy.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Media,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: "1731000000123",
                Payload: new byte[] { 1, 2, 3 })
            {
                BinaryFrameDecodedUtcMs = 1731000000456,
            });

        Assert.NotNull(receivedRawMessage);
        Assert.Equal(1731000000123, receivedRawMessage!.BridgeMessageObservedUtcMs);
        Assert.Equal(0, receivedRawMessage.SdkHandleMsgEnteredUtcMs);
        Assert.Equal(0, receivedRawMessage.ClientMessageDispatchUtcMs);
        Assert.Equal(0, receivedRawMessage.MultiClientMessageDispatchUtcMs);
    }

    [Fact]
    public void BulkBinaryMessage_LogsInboundDeliverySummaryWithSubscriberPresence()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("bulk-delivery-summary", "bulk-delivery-summary.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Bulk,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: null,
                Payload: new byte[] { 4, 5, 6 }));

        Assert.NotNull(receivedRawMessage);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_inbound_delivery_summary", logText, StringComparison.Ordinal);
        Assert.Contains("channel=bulk", logText, StringComparison.Ordinal);
        Assert.Contains("subscriber_present_count=1", logText, StringComparison.Ordinal);
        Assert.Contains("subscriber_missing_count=0", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkBinaryMessageWithoutSubscriber_LogsSubscriberMissing()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("bulk-delivery-missing", "bulk-delivery-missing.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        adapter.HandleBinaryBridgeFrameForTests(
            new BridgeBinaryFrame(
                BridgeBinaryFrameKind.Message,
                NknBridgeChannel.Bulk,
                Flags: 0,
                PrimaryText: "peer.test",
                SecondaryText: null,
                Payload: new byte[] { 7, 8, 9 }));

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_inbound_delivery_summary", logText, StringComparison.Ordinal);
        Assert.Contains("channel=bulk", logText, StringComparison.Ordinal);
        Assert.Contains("subscriber_missing_count=1", logText, StringComparison.Ordinal);
    }
}
