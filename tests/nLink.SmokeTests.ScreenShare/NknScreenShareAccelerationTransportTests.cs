using System.Collections.Concurrent;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class NknScreenShareAccelerationTransportTests : ScreenCaptureAbstractionTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrame_UsesAccelerationOnlyAfterAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.screen.address");
            var helperClient = new FakeNknClient("helper.tuna.screen.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-screen-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-screen-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    rawNknFrames.Enqueue(e);
                }
            };

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            var firstPayload = CreateVideoFragmentPayload(
                sessionId,
                frameId: 17,
                width: 640,
                height: 360,
                frameBytes: new byte[] { 1, 2, 3 },
                streamEpoch: 1,
                capturedTsUtcMs: 1000,
                isKeyFrame: true);
            await helper.SendScreenSharePayloadAsync(firstPayload, cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);
            var secondPayload = CreateVideoFragmentPayload(
                sessionId,
                frameId: 18,
                width: 640,
                height: 360,
                frameBytes: new byte[] { 4, 5, 6 },
                streamEpoch: 1,
                capturedTsUtcMs: 1016,
                isKeyFrame: false);
            await helper.SendScreenSharePayloadAsync(secondPayload, cts.Token);

            await CoreSmokeTestsBase.WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(NknBridgeChannel.Media, fakeLane.Sent.Single().Lane);
            Assert.True(EnvelopeCodec.TryDeserialize(fakeLane.Sent.Single().Payload, out var acceleratedEnvelope));
            Assert.Equal(MsgType.ScreenShareFrame, acceleratedEnvelope.Type);
            Assert.Single(rawNknFrames);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrame_FallsBackToNknAfterAccelerationDownAndLogsProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.screen.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.screen.fallback.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-screen-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-screen-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var rawNknFrames = new ConcurrentQueue<NknIncomingMessage>();
            var configReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    rawNknFrames.Enqueue(e);
                }
            };
            host.ScreenShareVideoStreamConfigReceived += (_, _) => configReceived.TrySetResult();
            host.ScreenShareFrameCompleted += (_, e) => frameReceived.TrySetResult(e);

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);
            var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
            hostLane.SetAvailable(false, "read_failed");
            await CoreSmokeTestsBase.WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            var payload = CreateVideoFragmentPayload(
                sessionId,
                frameId: 31,
                width: 640,
                height: 360,
                frameBytes: new byte[] { 9, 8, 7 },
                streamEpoch: 1,
                capturedTsUtcMs: 2000,
                isKeyFrame: true);

            await helper.SendScreenSharePayloadAsync(payload, cts.Token);

            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknFrames.Count == 1, TimeSpan.FromSeconds(2));
            var received = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.Equal(31, received.FrameId);
            Assert.Equal(new byte[] { 9, 8, 7 }, received.EncodedFrameBytes);
            Assert.Empty(hostLane.Sent);
            Assert.Empty(helperLane.Sent);
            Assert.Equal(NknBridgeChannel.Media, rawNknFrames.Single().Channel);
            var logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_screen_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_media_ready;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_nkn_frame_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_nkn_frame_received;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_nkn_frame_applied;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_recovered;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_received; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenAndFileFrames_RecoverIndependentlyAfterAccelerationDown()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.mixed.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.mixed.fallback.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-mixed-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-mixed-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var rawNknFileFrames = new ConcurrentQueue<NknIncomingMessage>();
            var rawNknScreenFrames = new ConcurrentQueue<NknIncomingMessage>();
            var configReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostClient.MessageReceived += (_, e) =>
            {
                if (e.IsTopic || !EnvelopeCodec.TryDeserialize(e.Payload, out var env))
                {
                    return;
                }

                if (env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknFileFrames.Enqueue(e);
                }
                else if (env.Type == MsgType.ScreenShareFrame)
                {
                    rawNknScreenFrames.Enqueue(e);
                }
            };
            host.ScreenShareVideoStreamConfigReceived += (_, _) => configReceived.TrySetResult();
            host.ScreenShareFrameCompleted += (_, e) => screenReceived.TrySetResult(e);

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer);
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_mixed_fallback", cts.Token);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
            hostLane.SetAvailable(false, "read_failed");
            await CoreSmokeTestsBase.WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_tuna_mixed_fallback",
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(
                    sessionId,
                    frameId: 42,
                    width: 640,
                    height: 360,
                    frameBytes: new byte[] { 4, 2 },
                    streamEpoch: 1,
                    capturedTsUtcMs: 3000,
                    isKeyFrame: true),
                cts.Token);

            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknFileFrames.Count == 1, TimeSpan.FromSeconds(2));
            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknScreenFrames.Count == 1, TimeSpan.FromSeconds(2));
            var screen = await screenReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.Equal(42, screen.FrameId);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknFileFrames.Single().Channel);
            Assert.Equal(NknBridgeChannel.Media, rawNknScreenFrames.Single().Channel);
            Assert.Empty(hostLane.Sent);
            Assert.Empty(helperLane.Sent);
            var logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_mixed_handoff_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_mixed_handoff_lane_state_changed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_screen_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_received; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
            Assert.Contains("event=screenshare_tuna_handoff_nkn_frame_applied;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenAndFileFrames_LocalSwitchOffStartsReceiverFallbackProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.mixed.local-switch-off.address");
            var helperClient = new FakeNknClient("helper.tuna.mixed.local-switch-off.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-mixed-local-switch-off-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-mixed-local-switch-off-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var rawNknFileFrames = new ConcurrentQueue<NknIncomingMessage>();
            var rawNknScreenFrames = new ConcurrentQueue<NknIncomingMessage>();
            var configReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostClient.MessageReceived += (_, e) =>
            {
                if (e.IsTopic || !EnvelopeCodec.TryDeserialize(e.Payload, out var env))
                {
                    return;
                }

                if (env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknFileFrames.Enqueue(e);
                }
                else if (env.Type == MsgType.ScreenShareFrame)
                {
                    rawNknScreenFrames.Enqueue(e);
                }
            };
            host.ScreenShareVideoStreamConfigReceived += (_, _) => configReceived.TrySetResult();
            host.ScreenShareFrameCompleted += (_, e) => screenReceived.TrySetResult(e);
            host.FileTransferOfferReceived += (_, e) => offerReceived.TrySetResult(e.Message);
            helper.FileTransferAcceptReceived += (_, e) => acceptReceived.TrySetResult(e.Message);
            host.FileTransferSessionOpenReceived += (_, e) => sessionOpenReceived.TrySetResult(e.Message);

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer);
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            const string transferId = "transfer_tuna_mixed_local_switch_off";
            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "mixed-local-switch-off.bin",
                    FileSizeBytes = 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var inboundDataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var logStart = CoreSmokeTestsBase.GetOperationalLogLength();

            await ((ITransportAccelerationControl)host).StopAccelerationAsync("soak_switch_off", cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(
                    sessionId,
                    frameId: 43,
                    width: 640,
                    height: 360,
                    frameBytes: new byte[] { 4, 3 },
                    streamEpoch: 1,
                    capturedTsUtcMs: 3100,
                    isKeyFrame: true),
                cts.Token);

            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknFileFrames.Count == 1, TimeSpan.FromSeconds(2));
            await CoreSmokeTestsBase.WaitUntilAsync(() => rawNknScreenFrames.Count == 1, TimeSpan.FromSeconds(2));
            var receivedFileFrame = await inboundDataSession.ReceiveAsync(cts.Token);
            var screen = await screenReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.IsType<FileTransferChunkBatchFrameV4>(receivedFileFrame);
            Assert.Equal(43, screen.FrameId);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknFileFrames.Single().Channel);
            Assert.Equal(NknBridgeChannel.Media, rawNknScreenFrames.Single().Channel);
            Assert.Empty(hostLane.Sent);
            Assert.Empty(helperLane.Sent);
            await CoreSmokeTestsBase.WaitUntilAsync(
                () =>
                {
                    var tail = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_fallback_nkn_frame_received; message_type=file_transfer_data_frame; channel=bulk", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_fallback_nkn_frame_received; message_type=screenshare_frame; channel=media", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(2));
            var logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=soak_switch_off", logTail, StringComparison.Ordinal);
            Assert.Contains("lanes=file", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_received; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_received; message_type=screenshare_frame; channel=media", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_mixed_handoff_started;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareControlMessages_DoNotUseAccelerationLane()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.screen.control.address");
            var helperClient = new FakeNknClient("helper.tuna.screen.control.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-screen-control-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-screen-control-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var configReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pressureReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ScreenShareVideoStreamConfigReceived += (_, _) => configReceived.TrySetResult();
            host.ScreenSharePressureStateReceived += (_, _) => pressureReceived.TrySetResult();

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            await helper.SendScreenSharePressureStateAsync(
                new ScreenSharePressureStateV1
                {
                    SessionId = sessionId,
                    Mode = ScreenSharePressureMode.Normal,
                    Reason = ScreenSharePressureProtocol.PressureReasonHealthy,
                    SentAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                cts.Token);

            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await pressureReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.Empty(fakeLane.Sent);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrame_InboundAccelerationEchoCompletesFrame()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.screen.echo.address");
            var helperClient = new FakeNknClient("helper.tuna.screen.echo.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-screen-echo-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-screen-echo-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var configReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ScreenShareVideoStreamConfigReceived += (_, _) => configReceived.TrySetResult();
            host.ScreenShareFrameCompleted += (_, e) => frameReceived.TrySetResult(e);

            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.Screen, sessionId);
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            await configReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

            var payload = CreateVideoFragmentPayload(
                sessionId,
                frameId: 41,
                width: 640,
                height: 360,
                frameBytes: new byte[] { 7, 8, 9 },
                streamEpoch: 1,
                capturedTsUtcMs: 4242,
                isKeyFrame: true);
            await helper.SendScreenSharePayloadAsync(payload, cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(() => helperLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            var accelerated = helperLane.Sent.Single();
            Assert.Equal(NknBridgeChannel.Media, accelerated.Lane);

            hostLane.InjectInbound(NknBridgeChannel.Media, accelerated.Payload);
            var received = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

            Assert.Equal(41, received.FrameId);
            Assert.Equal(new byte[] { 7, 8, 9 }, received.EncodedFrameBytes);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }
}
