namespace NLink.Infra.Nkn;

internal readonly record struct NknInboundEnvelopeContext(
    string Source,
    NknBridgeChannel Channel,
    Envelope Envelope,
    long BridgeIngressObservedUtcMs,
    long EnvelopeParsedUtcMs,
    long BridgeMessageObservedUtcMs,
    long BinaryFrameDecodedUtcMs,
    long SocketDataEventEmittedUtcMs,
    long WsReceiverWriteEnteredUtcMs,
    long WsMessageEmittedUtcMs,
    long SdkHandleMsgEnteredUtcMs,
    long ClientMessageDispatchUtcMs,
    long MultiClientMessageDispatchUtcMs);
