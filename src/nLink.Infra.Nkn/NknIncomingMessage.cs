namespace NLink.Infra.Nkn;

internal enum NknBridgeChannel
{
    Control = 0,
    Media = 1,
    Bulk = 2,
}

internal sealed class NknIncomingMessage : EventArgs
{
    public NknIncomingMessage(
        string source,
        byte[] payload,
        bool isTopic,
        string? topic,
        NknBridgeChannel channel = NknBridgeChannel.Control,
        long bridgeIngressObservedUtcMs = 0,
        long bridgeMessageObservedUtcMs = 0,
        long binaryFrameDecodedUtcMs = 0,
        long socketDataEventEmittedUtcMs = 0,
        long wsReceiverWriteEnteredUtcMs = 0,
        long wsMessageEmittedUtcMs = 0,
        long sdkHandleMsgEnteredUtcMs = 0,
        long clientMessageDispatchUtcMs = 0,
        long multiClientMessageDispatchUtcMs = 0)
    {
        Source = source ?? string.Empty;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        IsTopic = isTopic;
        Topic = topic;
        Channel = channel;
        BridgeIngressObservedUtcMs = bridgeIngressObservedUtcMs;
        BridgeMessageObservedUtcMs = bridgeMessageObservedUtcMs;
        BinaryFrameDecodedUtcMs = binaryFrameDecodedUtcMs;
        SocketDataEventEmittedUtcMs = socketDataEventEmittedUtcMs;
        WsReceiverWriteEnteredUtcMs = wsReceiverWriteEnteredUtcMs;
        WsMessageEmittedUtcMs = wsMessageEmittedUtcMs;
        SdkHandleMsgEnteredUtcMs = sdkHandleMsgEnteredUtcMs;
        ClientMessageDispatchUtcMs = clientMessageDispatchUtcMs;
        MultiClientMessageDispatchUtcMs = multiClientMessageDispatchUtcMs;
    }

    public string Source { get; }

    public byte[] Payload { get; }

    public bool IsTopic { get; }

    public string? Topic { get; }

    public NknBridgeChannel Channel { get; }

    public long BridgeIngressObservedUtcMs { get; }

    public long BridgeMessageObservedUtcMs { get; }

    public long BinaryFrameDecodedUtcMs { get; }

    public long SocketDataEventEmittedUtcMs { get; }

    public long WsReceiverWriteEnteredUtcMs { get; }

    public long WsMessageEmittedUtcMs { get; }

    public long SdkHandleMsgEnteredUtcMs { get; }

    public long ClientMessageDispatchUtcMs { get; }

    public long MultiClientMessageDispatchUtcMs { get; }
}
