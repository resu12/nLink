using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameLossAttributionReceivePathTests
{
    [Fact]
    public void HelperReceivePathSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-receive-path-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            10,
            false,
            capturedTsUtcMs: 1000,
            envelopeSendUtcMs: 1040,
            bridgeIngressObservedUtcMs: 1080,
            envelopeParsedUtcMs: 1095,
            secureDecryptCompletedUtcMs: 1125,
            fragmentEnvelopeDeserializedUtcMs: 1135);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 10, false, 1145);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReceivePathSnapshot(sessionId);

        Assert.Equal(40, snapshot.CaptureToEnvelopeSendAvgMs);
        Assert.Equal(40, snapshot.EnvelopeSendToBridgeIngressAvgMs);
        Assert.Equal(15, snapshot.BridgeIngressToEnvelopeParsedAvgMs);
        Assert.Equal(30, snapshot.EnvelopeParsedToSecureDecryptAvgMs);
        Assert.Equal(10, snapshot.SecureDecryptToFragmentDeserializeAvgMs);
        Assert.Equal(10, snapshot.FragmentDeserializeToFirstFragmentObservedAvgMs);
        Assert.Equal("capture_to_envelope_send", snapshot.DominantReceivePathStage);
    }

    [Fact]
    public void HelperReceivePathSnapshot_LatchesFirstObservedReceiveTimestamps()
    {
        const string sessionId = "helper-receive-path-latched";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            22,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2060,
            bridgeIngressObservedUtcMs: 2090,
            envelopeParsedUtcMs: 2110,
            secureDecryptCompletedUtcMs: 2140,
            fragmentEnvelopeDeserializedUtcMs: 2150);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 22, false, 2165);

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            22,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2300,
            bridgeIngressObservedUtcMs: 2330,
            envelopeParsedUtcMs: 2350,
            secureDecryptCompletedUtcMs: 2380,
            fragmentEnvelopeDeserializedUtcMs: 2390);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 22, false, 2405);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperReceivePathSnapshot(sessionId);

        Assert.Equal(60, snapshot.CaptureToEnvelopeSendAvgMs);
        Assert.Equal(30, snapshot.EnvelopeSendToBridgeIngressAvgMs);
        Assert.Equal(20, snapshot.BridgeIngressToEnvelopeParsedAvgMs);
        Assert.Equal(30, snapshot.EnvelopeParsedToSecureDecryptAvgMs);
        Assert.Equal(10, snapshot.SecureDecryptToFragmentDeserializeAvgMs);
        Assert.Equal(15, snapshot.FragmentDeserializeToFirstFragmentObservedAvgMs);
    }

    [Fact]
    public void HelperBridgeIngressSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-bridge-ingress-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            33,
            false,
            capturedTsUtcMs: 1000,
            envelopeSendUtcMs: 1040,
            bridgeMessageObservedUtcMs: 1095,
            binaryFrameDecodedUtcMs: 1115,
            bridgeIngressObservedUtcMs: 1130,
            envelopeParsedUtcMs: 1140,
            secureDecryptCompletedUtcMs: 1150,
            fragmentEnvelopeDeserializedUtcMs: 1160);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 33, false, 1170);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperBridgeIngressSnapshot(sessionId);

        Assert.Equal(55, snapshot.EnvelopeSendToBridgeMessageObservedAvgMs);
        Assert.Equal(20, snapshot.BridgeMessageObservedToBinaryFrameDecodedAvgMs);
        Assert.Equal(15, snapshot.BinaryFrameDecodedToBridgeIngressAvgMs);
        Assert.Equal("envelope_send_to_bridge_message_observed", snapshot.DominantBridgeIngressStage);
    }

    [Fact]
    public void HelperNknReceiveSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-nkn-receive-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            44,
            false,
            capturedTsUtcMs: 1000,
            envelopeSendUtcMs: 1040,
            wsReceiverWriteEnteredUtcMs: 1070,
            wsMessageEmittedUtcMs: 1080,
            sdkHandleMsgEnteredUtcMs: 1085,
            clientMessageDispatchUtcMs: 1105,
            multiClientMessageDispatchUtcMs: 1120,
            bridgeMessageObservedUtcMs: 1140,
            binaryFrameDecodedUtcMs: 1150,
            bridgeIngressObservedUtcMs: 1160,
            envelopeParsedUtcMs: 1170,
            secureDecryptCompletedUtcMs: 1180,
            fragmentEnvelopeDeserializedUtcMs: 1190);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 44, false, 1200);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperNknReceiveSnapshot(sessionId);

        Assert.Equal(45, snapshot.EnvelopeSendToSdkHandleMsgEnteredAvgMs);
        Assert.Equal(20, snapshot.SdkHandleMsgEnteredToClientMessageDispatchAvgMs);
        Assert.Equal(15, snapshot.ClientMessageDispatchToMultiClientMessageDispatchAvgMs);
        Assert.Equal(20, snapshot.MultiClientMessageDispatchToBridgeMessageObservedAvgMs);
        Assert.Equal("envelope_send_to_sdk_handle_msg_entered", snapshot.DominantNknReceiveStage);
    }

    [Fact]
    public void HelperWsReceiveSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-ws-receive-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            45,
            false,
            capturedTsUtcMs: 1000,
            envelopeSendUtcMs: 1040,
            wsReceiverWriteEnteredUtcMs: 1100,
            wsMessageEmittedUtcMs: 1125,
            sdkHandleMsgEnteredUtcMs: 1135,
            clientMessageDispatchUtcMs: 1150,
            multiClientMessageDispatchUtcMs: 1160,
            bridgeMessageObservedUtcMs: 1170,
            binaryFrameDecodedUtcMs: 1180,
            bridgeIngressObservedUtcMs: 1190,
            envelopeParsedUtcMs: 1200,
            secureDecryptCompletedUtcMs: 1210,
            fragmentEnvelopeDeserializedUtcMs: 1220);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 45, false, 1230);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperWsReceiveSnapshot(sessionId);

        Assert.Equal(60, snapshot.EnvelopeSendToWsReceiverWriteEnteredAvgMs);
        Assert.Equal(25, snapshot.WsReceiverWriteEnteredToWsMessageEmittedAvgMs);
        Assert.Equal(10, snapshot.WsMessageEmittedToSdkHandleMsgEnteredAvgMs);
        Assert.Equal("envelope_send_to_ws_receiver_write_entered", snapshot.DominantWsReceiveStage);
    }

    [Fact]
    public void HelperSocketReceiveSnapshot_ComputesExpectedStageSpans()
    {
        const string sessionId = "helper-socket-receive-test";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            46,
            false,
            capturedTsUtcMs: 1000,
            envelopeSendUtcMs: 1040,
            socketDataEventEmittedUtcMs: 1080,
            wsReceiverWriteEnteredUtcMs: 1100,
            wsMessageEmittedUtcMs: 1125,
            sdkHandleMsgEnteredUtcMs: 1135,
            clientMessageDispatchUtcMs: 1150,
            multiClientMessageDispatchUtcMs: 1160,
            bridgeMessageObservedUtcMs: 1170,
            binaryFrameDecodedUtcMs: 1180,
            bridgeIngressObservedUtcMs: 1190,
            envelopeParsedUtcMs: 1200,
            secureDecryptCompletedUtcMs: 1210,
            fragmentEnvelopeDeserializedUtcMs: 1220);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 46, false, 1230);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperSocketReceiveSnapshot(sessionId);

        Assert.Equal(40, snapshot.EnvelopeSendToSocketDataEventEmittedAvgMs);
        Assert.Equal(20, snapshot.SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs);
        Assert.Equal("envelope_send_to_socket_data_event_emitted", snapshot.DominantSocketReceiveStage);
    }

    [Fact]
    public void HelperNknReceiveSnapshot_LatchesFirstObservedSdkReceiveTimestamps()
    {
        const string sessionId = "helper-nkn-receive-latched";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            55,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2050,
            wsReceiverWriteEnteredUtcMs: 2080,
            wsMessageEmittedUtcMs: 2088,
            sdkHandleMsgEnteredUtcMs: 2090,
            clientMessageDispatchUtcMs: 2105,
            multiClientMessageDispatchUtcMs: 2115,
            bridgeMessageObservedUtcMs: 2130,
            binaryFrameDecodedUtcMs: 2140,
            bridgeIngressObservedUtcMs: 2150,
            envelopeParsedUtcMs: 2160,
            secureDecryptCompletedUtcMs: 2170,
            fragmentEnvelopeDeserializedUtcMs: 2180);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 55, false, 2190);

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            55,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2300,
            wsReceiverWriteEnteredUtcMs: 2330,
            wsMessageEmittedUtcMs: 2338,
            sdkHandleMsgEnteredUtcMs: 2340,
            clientMessageDispatchUtcMs: 2355,
            multiClientMessageDispatchUtcMs: 2365,
            bridgeMessageObservedUtcMs: 2380,
            binaryFrameDecodedUtcMs: 2390,
            bridgeIngressObservedUtcMs: 2400,
            envelopeParsedUtcMs: 2410,
            secureDecryptCompletedUtcMs: 2420,
            fragmentEnvelopeDeserializedUtcMs: 2430);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 55, false, 2440);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperNknReceiveSnapshot(sessionId);

        Assert.Equal(40, snapshot.EnvelopeSendToSdkHandleMsgEnteredAvgMs);
        Assert.Equal(15, snapshot.SdkHandleMsgEnteredToClientMessageDispatchAvgMs);
        Assert.Equal(10, snapshot.ClientMessageDispatchToMultiClientMessageDispatchAvgMs);
        Assert.Equal(15, snapshot.MultiClientMessageDispatchToBridgeMessageObservedAvgMs);
    }

    [Fact]
    public void HelperWsReceiveSnapshot_LatchesFirstObservedWsReceiveTimestamps()
    {
        const string sessionId = "helper-ws-receive-latched";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            56,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2050,
            wsReceiverWriteEnteredUtcMs: 2090,
            wsMessageEmittedUtcMs: 2100,
            sdkHandleMsgEnteredUtcMs: 2110,
            clientMessageDispatchUtcMs: 2120,
            multiClientMessageDispatchUtcMs: 2130,
            bridgeMessageObservedUtcMs: 2140,
            binaryFrameDecodedUtcMs: 2150,
            bridgeIngressObservedUtcMs: 2160,
            envelopeParsedUtcMs: 2170,
            secureDecryptCompletedUtcMs: 2180,
            fragmentEnvelopeDeserializedUtcMs: 2190);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 56, false, 2200);

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            56,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2300,
            wsReceiverWriteEnteredUtcMs: 2340,
            wsMessageEmittedUtcMs: 2350,
            sdkHandleMsgEnteredUtcMs: 2360,
            clientMessageDispatchUtcMs: 2370,
            multiClientMessageDispatchUtcMs: 2380,
            bridgeMessageObservedUtcMs: 2390,
            binaryFrameDecodedUtcMs: 2400,
            bridgeIngressObservedUtcMs: 2410,
            envelopeParsedUtcMs: 2420,
            secureDecryptCompletedUtcMs: 2430,
            fragmentEnvelopeDeserializedUtcMs: 2440);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 56, false, 2450);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperWsReceiveSnapshot(sessionId);

        Assert.Equal(40, snapshot.EnvelopeSendToWsReceiverWriteEnteredAvgMs);
        Assert.Equal(10, snapshot.WsReceiverWriteEnteredToWsMessageEmittedAvgMs);
        Assert.Equal(10, snapshot.WsMessageEmittedToSdkHandleMsgEnteredAvgMs);
    }

    [Fact]
    public void HelperSocketReceiveSnapshot_LatchesFirstObservedSocketReceiveTimestamps()
    {
        const string sessionId = "helper-socket-receive-latched";
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            57,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2050,
            socketDataEventEmittedUtcMs: 2080,
            wsReceiverWriteEnteredUtcMs: 2090,
            wsMessageEmittedUtcMs: 2100,
            sdkHandleMsgEnteredUtcMs: 2110,
            clientMessageDispatchUtcMs: 2120,
            multiClientMessageDispatchUtcMs: 2130,
            bridgeMessageObservedUtcMs: 2140,
            binaryFrameDecodedUtcMs: 2150,
            bridgeIngressObservedUtcMs: 2160,
            envelopeParsedUtcMs: 2170,
            secureDecryptCompletedUtcMs: 2180,
            fragmentEnvelopeDeserializedUtcMs: 2190);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 57, false, 2200);

        ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
            sessionId,
            1,
            57,
            false,
            capturedTsUtcMs: 2000,
            envelopeSendUtcMs: 2300,
            socketDataEventEmittedUtcMs: 2330,
            wsReceiverWriteEnteredUtcMs: 2340,
            wsMessageEmittedUtcMs: 2350,
            sdkHandleMsgEnteredUtcMs: 2360,
            clientMessageDispatchUtcMs: 2370,
            multiClientMessageDispatchUtcMs: 2380,
            bridgeMessageObservedUtcMs: 2390,
            binaryFrameDecodedUtcMs: 2400,
            bridgeIngressObservedUtcMs: 2410,
            envelopeParsedUtcMs: 2420,
            secureDecryptCompletedUtcMs: 2430,
            fragmentEnvelopeDeserializedUtcMs: 2440);
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(sessionId, 1, 57, false, 2450);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetHelperSocketReceiveSnapshot(sessionId);

        Assert.Equal(30, snapshot.EnvelopeSendToSocketDataEventEmittedAvgMs);
        Assert.Equal(10, snapshot.SocketDataEventEmittedToWsReceiverWriteEnteredAvgMs);
    }
}
