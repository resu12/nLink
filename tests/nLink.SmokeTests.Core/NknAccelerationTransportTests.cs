using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NknAccelerationTransportTests : CoreSmokeTestsBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void NknTunaAccelerationOptions_DefaultsDisabledWithFileAndScreenLanes()
    {
        using var enabled = new EnvironmentOverride("NLINK_NKN_TUNA_ENABLED", null);
        using var lanes = new EnvironmentOverride("NLINK_NKN_TUNA_LANES", null);
        using var sidecar = new EnvironmentOverride("NLINK_NKN_TUNA_SIDECAR_EXE", null);
        using var listener = new EnvironmentOverride("NLINK_NKN_TUNA_LISTENER_ENDPOINT", null);

        var options = NknTunaAccelerationOptions.Load();

        Assert.False(options.Enabled);
        Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, options.Lanes);
        Assert.Null(options.SidecarExePath);
        Assert.Null(options.ListenerEndpoint);
        Assert.False(options.CanOfferListener);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_SendsIdleWarmupBeforeFirstDataAndAfterQuietPeriod()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.Screen, queueCapacity: 16);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "screen" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [1, 2, 3], cts.Token));
            var firstWarmup = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);
            var firstData = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);

            Assert.Equal(NknTunaSidecarFrameType.Ping, firstWarmup.Type);
            Assert.Equal(NknTunaSidecarLane.Control, firstWarmup.Lane);
            Assert.Empty(firstWarmup.Payload);
            Assert.Equal(NknTunaSidecarFrameType.Data, firstData.Type);
            Assert.Equal(NknTunaSidecarLane.Media, firstData.Lane);
            Assert.Equal((ulong)1, firstData.Sequence);

            await Task.Delay(700, cts.Token);
            Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [4, 5, 6], cts.Token));
            var secondWarmup = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);
            var secondData = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);

            Assert.Equal(NknTunaSidecarFrameType.Ping, secondWarmup.Type);
            Assert.Equal(NknTunaSidecarFrameType.Data, secondData.Type);
            Assert.Equal((ulong)2, secondData.Sequence);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_SequenceGapIsDiagnosticAndDoesNotDisableLane()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            var received = new ConcurrentQueue<NknIncomingMessage>();
            client.MessageReceived += (_, message) => received.Enqueue(message);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "file" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 1,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 1 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 3,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 3 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 2,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 2 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 4,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 4 },
                cts.Token);

            await WaitUntilAsync(() => received.Count == 4, TimeSpan.FromSeconds(2));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.True(client.IsAvailable);
            Assert.Equal(4, diagnostics.BulkFramesReceived);
            Assert.Equal(1, diagnostics.SequenceGap);
            Assert.Equal(1, diagnostics.SequenceReordered);
            Assert.Equal(string.Empty, diagnostics.LastUnavailableReason);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_InterleavedLaneSequencesAreNotFalseGaps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, queueCapacity: 16);
            var received = new ConcurrentQueue<NknIncomingMessage>();
            client.MessageReceived += (_, message) => received.Enqueue(message);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "file", "screen" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 1,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 1 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Media,
                sequence: 2,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 2 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 3,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 3 },
                cts.Token);

            await WaitUntilAsync(() => received.Count == 3, TimeSpan.FromSeconds(2));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.True(client.IsAvailable);
            Assert.Equal(2, diagnostics.BulkFramesReceived);
            Assert.Equal(1, diagnostics.MediaFramesReceived);
            Assert.Equal(0, diagnostics.SequenceGap);
            Assert.Equal(0, diagnostics.SequenceReordered);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("missing_app_protocol", null, 1, "expected", "sidecar_app_protocol_mismatch")]
    [InlineData("wrong_frame_protocol", 1, 99, "expected", "sidecar_frame_protocol_mismatch")]
    [InlineData("stale_sidecar_version", 1, 1, "0.6.9", "sidecar_version_mismatch")]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_RejectsProtocolOrVersionMismatch(
        string scenario,
        int? appProtocolVersion,
        int? frameProtocolVersion,
        string sidecarVersion,
        string expectedReason)
    {
        _ = scenario;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var status = new Dictionary<string, object?>
            {
                ["event"] = "status",
                ["address"] = "nlink-tuna-sidecar.test-listener-address",
                ["frameProtocolVersion"] = frameProtocolVersion,
                ["sidecarVersion"] = string.Equals(sidecarVersion, "expected", StringComparison.Ordinal)
                    ? NknTunaSidecarCompatibility.ExpectedSidecarVersion
                    : sidecarVersion,
                ["lanes"] = new[] { "file" },
            };
            if (appProtocolVersion.HasValue)
            {
                status["appProtocolVersion"] = appProtocolVersion.Value;
            }

            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(status);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connectTask);
            Assert.Contains(expectedReason, ex.Message, StringComparison.Ordinal);
            Assert.False(client.IsAvailable);
            Assert.Equal(expectedReason, client.GetDiagnosticsSnapshot().LastUnavailableReason);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_MediaQueuePressureFallsBackOneFrameWithoutDisablingTuna()
    {
        var previousTimeout = NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests;
        NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests = 50;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.Screen, queueCapacity: 16);
            typeof(NknTunaSidecarClient)
                .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, 1);

            for (var i = 0; i < 16; i++)
            {
                Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [1, 2, 3], cts.Token));
            }

            Assert.False(await client.TrySendAsync(NknBridgeChannel.Media, [4, 5, 6], cts.Token));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.Equal(16, diagnostics.MediaFramesAccepted);
            Assert.Equal(0, diagnostics.MediaFramesWritten);
            Assert.Equal(1, diagnostics.QueueOverflow);
            Assert.True(client.IsAvailable);
            Assert.True(string.IsNullOrWhiteSpace(diagnostics.LastUnavailableReason));
        }
        finally
        {
            NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_BulkQueuePressureMarksSidecarUnavailableForNknFallback()
    {
        var previousTimeout = NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests;
        NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests = 50;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            typeof(NknTunaSidecarClient)
                .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, 1);

            for (var i = 0; i < 16; i++)
            {
                Assert.True(await client.TrySendAsync(NknBridgeChannel.Bulk, [1, 2, 3], cts.Token));
            }

            Assert.False(await client.TrySendAsync(NknBridgeChannel.Bulk, [4, 5, 6], cts.Token));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.Equal(16, diagnostics.BulkFramesAccepted);
            Assert.Equal(0, diagnostics.BulkFramesWritten);
            Assert.Equal(1, diagnostics.QueueOverflow);
            Assert.False(client.IsAvailable);
            Assert.Equal("queue_overflow", diagnostics.LastUnavailableReason);
        }
        finally
        {
            NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaAccelerationLane_RetainsLastDiagnosticsAfterStop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: false));
        using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, client);

        Assert.True(await lane.TrySendAsync(NknBridgeChannel.Bulk, [1, 2, 3], cts.Token));
        client.MarkUnavailableFromSidecarEvent("sidecar_tuna_stream_eof");
        await lane.StopAsync("test_stop", cts.Token);

        var diagnostics = lane.GetDiagnosticsSnapshot();
        Assert.Equal(1, diagnostics.BulkFramesAccepted);
        Assert.Equal("sidecar_tuna_stream_eof", diagnostics.LastUnavailableReason);
        Assert.Equal("sidecar_tuna_stream_eof", diagnostics.TerminalSidecarReason);
    }

    [Theory]
    [InlineData("session_security_state_not_eligible", false)]
    [InlineData("reset_session_tracking", false)]
    [InlineData("dispose", false)]
    [InlineData("sidecar_disposed", false)]
    [InlineData("sidecar_read_failed", true)]
    [InlineData("sidecar_tuna_stream_eof", true)]
    [InlineData("sidecar_byte_cap_reached", true)]
    [InlineData("remote_read_failed", true)]
    [InlineData("header_switch_off", true)]
    [InlineData("runtime_disabled", true)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_ResetReasonClassifierDistinguishesFailureFromTeardown(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldStartTunaFallbackProofForResetReason(reason));

    [Theory]
    [InlineData("session_security_state_not_eligible", true)]
    [InlineData("reset_session_tracking", true)]
    [InlineData("dispose", true)]
    [InlineData("sidecar_disposed", false)]
    [InlineData("sidecar_read_failed", false)]
    [InlineData("remote_read_failed", false)]
    [InlineData("header_switch_off", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_CompletionReasonClassifierPreservesActiveProofDuringSidecarCleanup(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldCompleteTunaFallbackProofForResetReason(reason));

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_RoutesBulkThroughAccelerationOnlyAfterAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.address");
            var helperClient = new FakeNknClient("helper.tuna.file.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_file_accel", cts.Token);
            var preNegotiationLogStart = GetOperationalLogLength();
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_tuna_file_accel",
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.DoesNotContain("event=tuna_fallback_started;", ReadOperationalLogTail(preNegotiationLogStart), StringComparison.Ordinal);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_tuna_file_accel",
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(NknBridgeChannel.Bulk, fakeLane.Sent.Single().Lane);
            Assert.True(EnvelopeCodec.TryDeserialize(fakeLane.Sent.Single().Payload, out var acceleratedEnvelope));
            Assert.Equal(MsgType.FileTransferDataFrame, acceleratedEnvelope.Type);
            Assert.Single(rawNknDataFrames);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStatus_FollowsNegotiatedHealthyLane()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.status.address");
            var helperClient = new FakeNknClient("helper.tuna.status.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-status-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-status-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var status = (ITransportAccelerationStatus)helper;
            var observedStates = new ConcurrentQueue<bool>();
            status.TransportAccelerationStateChanged += (_, e) => observedStates.Enqueue(e.IsActive);

            Assert.False(status.IsTransportAccelerationActive);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            Assert.True(status.IsTransportAccelerationActive);
            Assert.Contains(true, observedStates);

            fakeLane.SetAvailable(false, "test_down");

            await WaitUntilAsync(() => !status.IsTransportAccelerationActive, TimeSpan.FromSeconds(2));
            Assert.Contains(false, observedStates);
            Assert.Equal("sidecar_test_down", status.TransportAccelerationStatusReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RetriesAfterTransientSidecarUnavailable()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                deferSupportedLanesUntilAvailable: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 1);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.Equal(2, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_LateUnlockCanRetryAfterListenerUnavailableExhausted()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.late-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.late-unlock.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 4);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-late-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-late-unlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => hostLane.EnsureListenerCalls >= 4,
                TimeSpan.FromSeconds(8));
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.False(helper.IsAccelerationAvailableForTests);

            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(4));

            Assert.True(hostLane.EnsureListenerCalls >= 5);
            Assert.Equal(1, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_BothUnlockedSidesUseHelpeeAsPaidListener()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromMilliseconds(250);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.payer-priority.address");
            var helperClient = new FakeNknClient("helper.tuna.payer-priority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-payer-priority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-payer-priority-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(4));

            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.Equal(0, hostLane.StartDialerCalls);
            Assert.Equal(0, helperLane.EnsureListenerCalls);
            Assert.Equal(1, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RetriesWhenInitialOfferGetsNoAnswer()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.offer.noanswer.address");
            var helperClient = new FakeNknClient("helper.tuna.offer.noanswer.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var droppedOffers = 0;
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer &&
                    Interlocked.Increment(ref droppedOffers) == 1)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-noanswer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-noanswer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            Assert.True(droppedOffers >= 1);
            Assert.True(hostLane.EnsureListenerCalls >= 2);
            Assert.Equal(1, helperLane.StartDialerCalls);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_DoesNotAdvertiseOrConnectListenerBeforeApprovedSession()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.preconsent.address");
            var helperClient = new FakeNknClient("helper.tuna.preconsent.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-preconsent-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-preconsent-id", helperClient.Address));
            var pendingJoinRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawOffers = new ConcurrentQueue<Envelope>();
            host.IncomingJoinRequest += (_, e) => pendingJoinRaised.TrySetResult(e);
            hostClient.BeforeSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer)
                {
                    rawOffers.Enqueue(env);
                }

                return Task.CompletedTask;
            };

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                new PeerAddress(host.LocalPeerAddress),
                out var rawToken,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare,
                boundHelperAddress: new PeerAddress(helper.LocalPeerAddress));
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            await pendingJoinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await Task.Delay(250, cts.Token);

            Assert.Equal(0, hostLane.EnsureListenerCalls);
            Assert.Empty(rawOffers);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_PreSessionDoesNotStartDialer()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.presession.address");
            var helperClient = new FakeNknClient("helper.tuna.presession.address");
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-presession-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();
            var offer = CreateOfferPayload("sess_tuna_presession", "00112233445566778899aabbccddeeff");

            var task = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(
                helper,
                "HandleTransportAccelerationOfferAsync",
                hostClient.Address,
                offer,
                "pre-session-offer-message",
                cts.Token));
            await task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(0, helperLane.StartDialerCalls);
            Assert.False(helper.IsAccelerationAvailableForTests);
            Assert.Contains("reason=session_not_eligible", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    [InlineData("expired", "reason=expired")]
    [InlineData("unsupported_version", "reason=sidecar_app_protocol_mismatch")]
    [InlineData("unsupported_lane", "event=tuna_acceleration_answer_sent; accepted=0; reason=unsupported_lane")]
    public async Task TransportAccelerationOffer_InvalidMessagesDoNotStartDialer(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.offer.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.offer.invalid." + scenarioTag);
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "aa11223344556677889900aabbccdd" + scenario.Length.ToString("x2");
            var offer = CreateOfferPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_offer" : sessionId,
                nonce,
                supportedLanes: scenario == "unsupported_lane" ? new[] { "bogus" } : new[] { "file" },
                expiresAtUnixMs: scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : null,
                sidecarProtocolVersion: scenario == "unsupported_version" ? 99 : null);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationOffer,
                offer,
                "transport_acceleration_offer",
                offer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.offer.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.Equal(0, hostLane.StartDialerCalls);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationDown_DisablesPeerAcceleration()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.down.address");
            var helperClient = new FakeNknClient("helper.tuna.down.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var rawDownMessages = new ConcurrentQueue<Envelope>();
            helperClient.MessageReceived += (_, e) =>
            {
                if (e.Channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationDown)
                {
                    rawDownMessages.Enqueue(env);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.True(helper.IsAccelerationAvailableForTests);

            var logStart = GetOperationalLogLength();
            hostLane.SetAvailable(false, "read_failed");

            await WaitUntilAsync(() => rawDownMessages.Count == 1, TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(3));
            Assert.Equal(NknAccelerationLaneKind.None, helper.AccelerationNegotiatedLanesForTests);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_down_notify_queued", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_remote_down", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_UserStoppedActiveSessionRejectsPeerRestart()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.user-stop.address");
            var helperClient = new FakeNknClient("helper.tuna.user-stop.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-user-stop-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-user-stop-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            Assert.True(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            var hostDialerCallsBeforeRestart = hostLane.StartDialerCalls;
            var logStart = GetOperationalLogLength();

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("reason=user_stopped_tuna", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            Assert.Equal(hostDialerCallsBeforeRestart, hostLane.StartDialerCalls);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_RuntimeUnlockClearsUserStoppedSessionGuard()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.user-stop-reunlock.address");
            var helperClient = new FakeNknClient("helper.tuna.user-stop-reunlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-user-stop-reunlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-user-stop-reunlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            Assert.True(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            var hostDialerCallsBeforeReunlock = hostLane.StartDialerCalls;

            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            Assert.Equal(hostDialerCallsBeforeReunlock + 1, hostLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RejectedAnswerPreservesPeerReasonAndClearsNonce()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.reject.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.reject.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-reject-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-reject-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bb11223344556677889900aabbccddee";
            SetPrivateField(host, "outboundAccelerationOfferNonce", nonce);
            var answer = CreateAnswerPayload(sessionId, nonce, accepted: false, rejectReason: "sidecar_unavailable");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_answer_rejected; reason=sidecar_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("nonce_mismatch", "reason=nonce_mismatch")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    [InlineData("expired", "reason=expired")]
    [InlineData("unsupported_version", "reason=sidecar_app_protocol_mismatch")]
    [InlineData("unsupported_lane", "event=tuna_acceleration_answer_rejected; reason=unsupported_lane")]
    public async Task TransportAccelerationAnswer_InvalidMessagesCannotEnableAcceleration(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.answer.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.answer.invalid." + scenarioTag);
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var expectedNonce = "cc11223344556677889900aabbccddee";
            SetPrivateField(host, "outboundAccelerationOfferNonce", expectedNonce);
            var answer = CreateAnswerPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_answer" : sessionId,
                scenario == "nonce_mismatch" ? "dd11223344556677889900aabbccddee" : expectedNonce,
                accepted: true,
                supportedLanes: scenario == "unsupported_lane" ? new[] { "bogus" } : new[] { "file" },
                expiresAtUnixMs: scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : null,
                sidecarProtocolVersion: scenario == "unsupported_version" ? 99 : null);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.answer.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    public async Task TransportAccelerationDown_MismatchDoesNotResetActiveAcceleration(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.down.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.down.invalid." + scenarioTag);
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var down = CreateDownPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_down" : sessionId,
                "ee11223344556677889900aabbccddee");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationDown,
                down,
                "transport_acceleration_down",
                down.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.down.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_FallsBackToNknWhenAccelerationSendFails()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true, sendResult: false);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_fallback";
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
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

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_StartsFallbackProofWhenAccelerationBecomesUnavailableBeforeSend()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.unavailable.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.unavailable.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-unavailable-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-unavailable-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_unavailable_fallback";
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            fakeLane.IsAvailable = false;
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
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

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_unavailable_before_send", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ChatMessage_StaysOnNknAfterAccelerationAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.chat.address");
            var helperClient = new FakeNknClient("helper.tuna.chat.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-chat-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-chat-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknChat = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.Chat)
                {
                    rawNknChat.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await helper.SendChatMessageAsync(new byte[] { 1, 2, 3, 4 }, cts.Token);

            await WaitUntilAsync(() => rawNknChat.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Control, rawNknChat.Single().Channel);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static TransportAccelerationOfferPayload CreateOfferPayload(
        string sessionId,
        string nonce,
        string[]? supportedLanes = null,
        long? expiresAtUnixMs = null,
        int? sidecarProtocolVersion = null)
        => new()
        {
            SessionId = sessionId,
            SenderRole = "helper",
            TunaAddress = "nlink-tuna-sidecar.test-offer-address",
            SupportedLanes = supportedLanes ?? new[] { "file", "screen" },
            ExpiresAtUnixMs = expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = sidecarProtocolVersion ?? 1,
        };

    private static TransportAccelerationAnswerPayload CreateAnswerPayload(
        string sessionId,
        string nonce,
        bool accepted,
        string[]? supportedLanes = null,
        long? expiresAtUnixMs = null,
        int? sidecarProtocolVersion = null,
        string? rejectReason = null)
        => new()
        {
            SessionId = sessionId,
            Accepted = accepted,
            SupportedLanes = supportedLanes ?? (accepted ? new[] { "file", "screen" } : Array.Empty<string>()),
            ExpiresAtUnixMs = expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = sidecarProtocolVersion ?? 1,
            RejectReason = rejectReason,
        };

    private static TransportAccelerationDownPayload CreateDownPayload(string sessionId, string nonce)
        => new()
        {
            SessionId = sessionId,
            SupportedLanes = new[] { "file", "screen" },
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = 1,
            Reason = "read_failed",
        };

    private static Envelope BuildSecureAccelerationEnvelope<TPayload>(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        TPayload payload,
        string secureMessageType,
        string requestId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: secureMessageType,
                SessionId: sessionId,
                SenderIdentity: new PeerAddress(senderTransport.LocalPeerAddress),
                Sequence: sequence,
                RequestId: requestId),
            JsonSerializer.SerializeToUtf8Bytes(payload));

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: msgType,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

    private sealed class RetryableTunaAccelerationSession : INknTunaAccelerationSession
    {
        private readonly bool canListen;
        private readonly int failedDialAttemptsBeforeSuccess;
        private readonly int failedListenerAttemptsBeforeSuccess;
        private readonly NknAccelerationLaneKind supportedLanes;
        private readonly bool deferSupportedLanesUntilAvailable;
        private int available;
        private int ensureListenerCalls;
        private int startDialerCalls;

        public RetryableTunaAccelerationSession(
            bool canListen,
            int failedDialAttemptsBeforeSuccess,
            NknAccelerationLaneKind supportedLanes = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen,
            bool deferSupportedLanesUntilAvailable = false,
            int failedListenerAttemptsBeforeSuccess = 0)
        {
            this.canListen = canListen;
            this.failedDialAttemptsBeforeSuccess = failedDialAttemptsBeforeSuccess;
            this.failedListenerAttemptsBeforeSuccess = failedListenerAttemptsBeforeSuccess;
            this.supportedLanes = supportedLanes;
            this.deferSupportedLanesUntilAvailable = deferSupportedLanesUntilAvailable;
        }

        public bool IsAvailable => Volatile.Read(ref available) != 0;

        public bool CanOfferListener => canListen;

        public NknAccelerationLaneKind ConfiguredLanes => supportedLanes;

        public NknAccelerationLaneKind SupportedLanes
            => deferSupportedLanesUntilAvailable && !IsAvailable
                ? NknAccelerationLaneKind.None
                : supportedLanes;

        public string? LocalTunaAddress { get; private set; }

        public int EnsureListenerCalls => Volatile.Read(ref ensureListenerCalls);

        public int StartDialerCalls => Volatile.Read(ref startDialerCalls);

        public event EventHandler<NknIncomingMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

        public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot()
            => new(IsAvailable, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, 0);

        public Task<bool> EnsureListenerSidecarConnectedAsync(string expectedRemotePeer, CancellationToken ct)
        {
            var calls = Interlocked.Increment(ref ensureListenerCalls);
            if (!canListen || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            if (calls <= failedListenerAttemptsBeforeSuccess)
            {
                return Task.FromResult(false);
            }

            LocalTunaAddress = "nlink-tuna-sidecar.test-listener-address";
            MarkAvailable("listener_ready");
            return Task.FromResult(true);
        }

        public Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct)
        {
            var calls = Interlocked.Increment(ref startDialerCalls);
            if (ct.IsCancellationRequested || calls <= failedDialAttemptsBeforeSuccess)
            {
                return Task.FromResult(false);
            }

            LocalTunaAddress = "nlink-tuna-sidecar.test-dialer-address";
            MarkAvailable("dialer_ready");
            return Task.FromResult(true);
        }

        public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
            => Task.FromResult(false);

        public Task StopAsync(string reason, CancellationToken ct)
        {
            Volatile.Write(ref available, 0);
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(false, reason));
            return Task.CompletedTask;
        }

        public void Dispose()
            => Volatile.Write(ref available, 0);

        private void MarkAvailable(string reason)
        {
            if (Interlocked.Exchange(ref available, 1) == 0)
            {
                StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(true, reason));
            }
        }
    }
}
