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
    public void TransportAccelerationOffer_HelperFallbackPayerDelay_IsShort()
    {
        var field = typeof(NknSignalingTransport).GetField(
            "HelperPaidOfferHelpeePriorityDelay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<TimeSpan>(field.GetValue(null)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProofLogging_IsLowNoiseAfterInitialProof()
    {
        var windowField = typeof(NknSignalingTransport).GetField(
            "TunaFallbackProofLogWindow",
            BindingFlags.Static | BindingFlags.NonPublic);
        var everyFramesField = typeof(NknSignalingTransport).GetField(
            "TunaFallbackProofLogEveryFrames",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(windowField);
        Assert.NotNull(everyFramesField);
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<TimeSpan>(windowField.GetValue(null)));
        Assert.Equal(5000L, Assert.IsType<long>(everyFramesField.GetValue(null)));
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

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaAccelerationLane_SuppressesLocalListenerWithoutEmittingStaleUnavailableEvent()
    {
        var supervisor = new RecordingTunaListenerSidecarSupervisor();
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: true),
            supervisor);
        using var listenerClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(listenerClient, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, listenerClient);
        var roleField = typeof(NknTunaAccelerationLane)
            .GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!;
        roleField.SetValue(lane, Enum.Parse(roleField.FieldType, "Listener"));

        var unavailableEvents = 0;
        lane.StateChanged += (_, e) =>
        {
            if (!e.IsAvailable)
            {
                unavailableEvents++;
            }
        };

        typeof(NknTunaAccelerationLane)
            .GetMethod("StopCurrentListenerBeforeDialer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lane, null);

        Assert.False(listenerClient.IsAvailable);
        Assert.Null(typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lane));
        Assert.Equal(0, unavailableEvents);
        Assert.Equal(["payer_switch_to_dialer"], supervisor.StopReasons);
    }

    [Theory]
    [InlineData("session_security_state_not_eligible", false)]
    [InlineData("reset_session_tracking", false)]
    [InlineData("dispose", false)]
    [InlineData("sidecar_disposed", false)]
    [InlineData("sidecar_read_failed", true)]
    [InlineData("sidecar_tuna_stream_eof", true)]
    [InlineData("sidecar_byte_cap_reached", true)]
    [InlineData("sidecar_remote_byte_cap_reached", true)]
    [InlineData("remote_read_failed", true)]
    [InlineData("header_switch_off", false)]
    [InlineData("remote_header_switch_off", false)]
    [InlineData("soak_switch_off", false)]
    [InlineData("runtime_disabled", false)]
    [InlineData("user_stopped_tuna", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_ResetReasonClassifierDistinguishesFailureFromTeardown(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldStartTunaFallbackProofForResetReason(reason));

    [Theory]
    [InlineData("tuna_stream_eof", "sidecar_tuna_stream_eof", true)]
    [InlineData("sidecar_tuna_stream_eof", "sidecar_tuna_stream_eof", true)]
    [InlineData("remote_closed", "sidecar_remote_closed", true)]
    [InlineData("sidecar_remote_closed", "sidecar_remote_closed", true)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_SidecarResetReasonNormalizerDoesNotDoublePrefix(
        string reason,
        string expectedReason,
        bool startsFallback)
    {
        var normalized = NknSignalingTransport.NormalizeAccelerationSidecarResetReason(reason);

        Assert.Equal(expectedReason, normalized);
        Assert.Equal(startsFallback, NknSignalingTransport.ShouldStartTunaFallbackProofForResetReason(normalized));
    }

    [Theory]
    [InlineData("byte_cap_reached", true)]
    [InlineData("sidecar_remote_closed", true)]
    [InlineData("sidecar_read_failed", true)]
    [InlineData("remote_sidecar_remote_closed", true)]
    [InlineData("header_switch_off", false)]
    [InlineData("remote_header_switch_off", false)]
    [InlineData("user_stopped_tuna", false)]
    [InlineData("reset_session_tracking", false)]
    [InlineData("dispose", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_ImmediateFileProbeClassifierIncludesSidecarDrop(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldStartImmediateFileTransferFallbackProbe(reason));

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
                new FileTransferChunkBatchFrameV6
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
                new FileTransferChunkBatchFrameV6
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
    public async Task FileTransferDataSession_TunaAcceptedEmitsNormalToTunaActivationHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.handoff.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.handoff.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-activation-handoff-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-handoff-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=test_accept", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_RuntimeUnlockPausesRegularNknFileTransferUntilTunaHandoff()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.pause.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.pause.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-pause-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-pause-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_pause",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "tuna_activation_negotiating" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(5));

            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.True(helper.IsAccelerationAvailableForTests);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bulk-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bulk-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedOfferCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref blockedOfferCount);
                    await blockedControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bulk-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bulk-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_bulk_bypass",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedOfferCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_FallsBackToBulkQueueWhenDirectControlCopiesHang()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        var blockedDirectControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bulk-queue-fallback.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bulk-queue-fallback.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedDirectControlCount = 0;
            hostClient.BeforeSendCoreAsync = async (_, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref blockedDirectControlCount);
                    await blockedDirectControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bulk-queue-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bulk-queue-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_bulk_queue_fallback",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedDirectControlCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_priority_failed; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("error=Timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_priority_failed; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_wait_timeout; purpose=offer;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_rejected;", logTail, StringComparison.Ordinal);

            blockedDirectControlOffer.TrySetResult(null);
            await Task.Delay(100, cts.Token);
        }
        finally
        {
            blockedDirectControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_WaitsForSlowActivationControlSend()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.slow-offer.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.slow-offer.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var delayedOfferSendCount = 0;
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref delayedOfferSendCount);
                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-slow-offer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-slow-offer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_slow_offer",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(12));

            Assert.True(Volatile.Read(ref delayedOfferSendCount) > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_rejected;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_wait_timeout; purpose=offer;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetryExhausted_DefersResumeWhileActivationPauseIsActive()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.retry-exhausted.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.retry-exhausted.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-retry-exhausted-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-retry-exhausted-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_retry_exhausted",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_tuna_activation_retry_exhausted_resume_deferred;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_exhausted; reason=offer_queue_rejected;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_retry_exhausted_resume_deferred; reason=offer_queue_rejected;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=retry_exhausted_offer_queue_rejected", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationPayerIntent_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlPayerIntent = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.intent-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.intent-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedIntentCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationPayerIntent)
                {
                    Interlocked.Increment(ref blockedIntentCount);
                    await blockedControlPayerIntent.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-intent-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-intent-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_payer_intent_bypass",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedIntentCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=payer_intent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=payer_intent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_payer_intent_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlPayerIntent.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlAnswer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.answer-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.answer-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedAnswerCount = 0;
            helperClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswer)
                {
                    Interlocked.Increment(ref blockedAnswerCount);
                    await blockedControlAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-answer-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-answer-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_answer_bypass",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedAnswerCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_received_raw;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlAnswer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswerAck_DefersDialerFileTransferHandoffUntilAck()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedAnswerAck = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.ack-gate.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.ack-gate.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedAckCount = 0;
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswerAck)
                {
                    Interlocked.Increment(ref blockedAckCount);
                    await blockedAnswerAck.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-ack-gate-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-ack-gate-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_gate_host",
                cts.Token);
            var helperDataSession = await helper.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_gate_helper",
                cts.Token);
            var helperAvailabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            helperDataSession.AvailabilityChanged += (_, e) => helperAvailabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && Volatile.Read(ref blockedAckCount) > 0,
                TimeSpan.FromSeconds(6));
            Assert.False(helper.IsAccelerationAvailableForTests);
            Assert.DoesNotContain(
                helperAvailabilityEvents,
                e => e.IsAvailable &&
                     e.RequiresResumeRequest &&
                     e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                     e.TargetTransport == FileTransferTransportKind.Tuna);

            blockedAnswerAck.SetResult(null);

            await WaitUntilAsync(
                () => helper.IsAccelerationAvailableForTests &&
                      helperAvailabilityEvents.Any(e =>
                          e.IsAvailable &&
                          e.RequiresResumeRequest &&
                          e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                          e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(6));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_answer_ack_pending;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_answer_ack", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedAnswerAck.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_TunaAcceptedBeforeSessionReplaysPendingNormalToTunaActivationHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.pending.activation.address");
            var helperClient = new FakeNknClient("helper.tuna.file.pending.activation.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-pending-activation-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-pending-activation-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            var logStart = GetOperationalLogLength();
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_pending_handoff_recorded;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_pending_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var replayTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_pending_handoff_replayed;", replayTail, StringComparison.Ordinal);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    "transfer_tuna_pending_activation_handoff",
                    FileTransferDirection.Outbound,
                    11,
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.Tuna,
                    V6TransportEpochState.TargetProofPending,
                    "test_epoch_observed",
                    IsUnresolved: true));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_pending_handoff_cleared;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_TunaFallbackBeforeSessionReplaysPendingRegularNknHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.pending.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.pending.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-pending-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-pending-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_pending_handoff_recorded;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            const string transferId = "transfer_tuna_pending_fallback_handoff";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var replayTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_pending_handoff_replayed;", replayTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=tuna_to_normal_fallback", replayTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=regular_nkn", replayTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_ActiveTunaWithoutPendingIntentSynthesizesNormalToTunaActivationHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.synthesized.activation.address");
            var helperClient = new FakeNknClient("helper.tuna.file.synthesized.activation.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-synthesized-activation-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-synthesized-activation-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            SetPrivateField(helper, "accelerationSessionId", sessionId);
            SetPrivateField(helper, "accelerationNegotiatedLanes", NknAccelerationLaneKind.File);

            var logStart = GetOperationalLogLength();
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_synthesized_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_active_tuna_handoff_synthesized;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=active_tuna_session_registered", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_pending_handoff_replayed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_ActiveSessionCanMoveBackToTunaAfterExplicitReenable()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.reenable.address");
            var helperClient = new FakeNknClient("helper.tuna.file.reenable.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-reenable-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-reenable-id", helperClient.Address),
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
            const string transferId = "transfer_tuna_file_reenable";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));

            await ((ITransportAccelerationControl)helper).StopAccelerationAsync("header_switch_off", cts.Token);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Single(fakeLane.Sent);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 2,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => fakeLane.Sent.Count == 2, TimeSpan.FromSeconds(2));
            Assert.Single(rawNknDataFrames);
            Assert.All(fakeLane.Sent, sent => Assert.Equal(NknBridgeChannel.Bulk, sent.Lane));
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
    public async Task TransportAccelerationRetry_IsSuppressedWhileV6FileTransferEpochIsUnresolved()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.v6-epoch.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.v6-epoch.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-v6-epoch-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-v6-epoch-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    "transfer_v6_epoch_blocks_retry",
                    FileTransferDirection.Outbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "sidecar_byte_cap_reached",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "phase5_v6_epoch_unresolved");
            var blockedTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", blockedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=phase5_v6_epoch_unresolved", blockedTail, StringComparison.Ordinal);

            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    "transfer_v6_epoch_blocks_retry",
                    FileTransferDirection.Outbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Terminal,
                    "transfer_terminal",
                    IsUnresolved: false));
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var retryLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "phase5_v6_epoch_terminal");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(retryLogStart).Contains("event=tuna_acceleration_retry_scheduled; reason=phase5_v6_epoch_terminal", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
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
    public async Task TransportAccelerationOffer_RuntimeUnlockLogsAndRetriesTransientPreflightListenerUnavailable()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.preflight-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.preflight-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-preflight-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-preflight-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var logStart = GetOperationalLogLength();

            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            hostLane.SetCanListen(true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains("retry_scheduled=1", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=preflight_listener_unavailable", tail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls >= 1);
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
    public async Task TransportAccelerationOffer_HelperOnlyUnlockSkipsHelpeePriorityDelayAfterHelpeeDialerOnlyIntent()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousHelpeeIntentGraceDelay = NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromSeconds(6);
        NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = TimeSpan.FromMilliseconds(750);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.helper-only-intent.address");
            var helperClient = new FakeNknClient("helper.tuna.helper-only-intent.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-helper-only-intent-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-helper-only-intent-id", helperClient.Address),
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
                TimeSpan.FromSeconds(4));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_payer_intent_received; intent=dialer_only", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_dialer_only", logTail, StringComparison.Ordinal);
            Assert.Equal(0, hostLane.EnsureListenerCalls);
            Assert.True(hostLane.StartDialerCalls >= 1);
            Assert.True(helperLane.EnsureListenerCalls > 0);
            Assert.Equal(0, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = previousHelpeeIntentGraceDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_HelperOnlyUnlockUsesShortGraceWhenHelpeeIntentIsMissing()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousHelpeeIntentGraceDelay = NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.helper-only-missing-intent.address");
            var helperClient = new FakeNknClient("helper.tuna.helper-only-missing-intent.address");
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-helper-only-missing-intent-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-helper-only-missing-intent-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => helperLane.EnsureListenerCalls > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_unobserved", logTail, StringComparison.Ordinal);
            Assert.True(helperLane.EnsureListenerCalls > 0);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = previousHelpeeIntentGraceDelay;
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
            var droppedOfferGate = new object();
            string? droppedOfferMessageId = null;
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer)
                {
                    lock (droppedOfferGate)
                    {
                        if (droppedOfferMessageId is null)
                        {
                            droppedOfferMessageId = env.MessageId;
                        }

                        if (string.Equals(droppedOfferMessageId, env.MessageId, StringComparison.Ordinal))
                        {
                            droppedOffers++;
                            return Task.FromResult(false);
                        }
                    }
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
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=offer_answer_timeout; attempt=1; delay_ms=250; listener_ready_reuse=1", logTail, StringComparison.Ordinal);
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
    public async Task TransportAccelerationOffer_ReplaysSameOfferBeforeAnswerTimeout()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.offer.replay.address");
            var helperClient = new FakeNknClient("helper.tuna.offer.replay.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var offerSendCount = 0;
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer &&
                    Interlocked.Increment(ref offerSendCount) <= 3)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-replay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-replay-id", helperClient.Address),
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

            Assert.True(Volatile.Read(ref offerSendCount) >= 4);
            Assert.Equal(1, helperLane.StartDialerCalls);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_replay_sent;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
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
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
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
    public async Task TransportAccelerationStop_ResumesRegularNknBeforeBlockedDownNotificationCompletes()
    {
        FakeNknClient.ResetNetwork();
        var blockedDownNotification = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.stop-blocked-down.address");
            var helperClient = new FakeNknClient("helper.tuna.stop-blocked-down.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            var blockedDownCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationDown)
                {
                    Interlocked.Increment(ref blockedDownCount);
                    await blockedDownNotification.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-stop-blocked-down-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-stop-blocked-down-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_stop_blocked_down",
                cts.Token);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var logStart = GetOperationalLogLength();

            var stopTask = ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);

            await WaitUntilAsync(() => Volatile.Read(ref blockedDownCount) > 0, TimeSpan.FromSeconds(2));
            Assert.True(stopTask.IsCompletedSuccessfully);
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.True(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.Equal(0, host.AccelerationDiagnosticsForTests.FallbackEpoch);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_reset; reason=header_switch_off; fallback_proof_suppressed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedDownNotification.TrySetResult(null);
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_FreshPeerRuntimeUnlockClearsUserStoppedSessionGuard()
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
            var logStart = GetOperationalLogLength();

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => !host.IsAccelerationUserStoppedForCurrentSessionForTests,
                TimeSpan.FromSeconds(3));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.Contains("event=tuna_acceleration_user_stop_cleared; trigger=peer_payer_intent", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_RemoteDownDoesNotBlockPeerPaidReunlock()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.remote-stop-reunlock.address");
            var helperClient = new FakeNknClient("helper.tuna.remote-stop-reunlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-remote-stop-reunlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-remote-stop-reunlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)helper).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.True(helper.IsAccelerationUserStoppedForCurrentSessionForTests);
            var hostDialerCallsBeforeReunlock = hostLane.StartDialerCalls;

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
                sequence: 101);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_UserStoppedRejectAfterRuntimeUnlockRetriesFreshOffer()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.peer-stop-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.peer-stop-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-peer-stop-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-peer-stop-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var expectedNonce = "99112233445566778899aabbccddee00";
            SetPrivateField(host, "outboundAccelerationOfferNonce", expectedNonce);
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            var answer = CreateAnswerPayload(
                sessionId,
                expectedNonce,
                accepted: false,
                supportedLanes: Array.Empty<string>(),
                rejectReason: "user_stopped_tuna");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 101);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);
            hostLane.SetCanListen(true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_answer_rejected; reason=user_stopped_tuna; offer_trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=peer_user_stopped_tuna", logTail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
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

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("nonce_mismatch", "reason=nonce_mismatch")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    [InlineData("expired", "reason=expired")]
    [InlineData("unsupported_version", "reason=sidecar_app_protocol_mismatch")]
    [InlineData("unsupported_lane", "event=tuna_acceleration_answer_rejected; reason=unsupported_lane")]
    [InlineData("payer_decision_mismatch", "reason=payer_decision_mismatch")]
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
            if (scenario == "payer_decision_mismatch")
            {
                SetPrivateField(host, "outboundAccelerationOfferPayerDecisionId", 41L);
            }

            var answer = CreateAnswerPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_answer" : sessionId,
                scenario == "nonce_mismatch" ? "dd11223344556677889900aabbccddee" : expectedNonce,
                accepted: true,
                supportedLanes: scenario == "unsupported_lane" ? new[] { "bogus" } : new[] { "file" },
                expiresAtUnixMs: scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : null,
                sidecarProtocolVersion: scenario == "unsupported_version" ? 99 : null,
                payerDecisionId: scenario == "payer_decision_mismatch" ? 42L : 0L);
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
    public async Task TransportAccelerationDown_StartsFallbackProofWhenLocalLaneAlreadyUnavailable()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.down.after-unavailable.address");
            var helperClient = new FakeNknClient("helper.tuna.down.after-unavailable.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-after-unavailable-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-after-unavailable-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            hostLane.SetAvailable(false, "test_local_unavailable_before_down");
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(2));

            var down = CreateDownPayload(sessionId, "ef11223344556677889900aabbccddee");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationDown,
                down,
                "transport_acceleration_down",
                down.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_fallback_started;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("reason=remote_read_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_remote_down; reason=read_failed", logTail, StringComparison.Ordinal);
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
                new FileTransferChunkBatchFrameV6
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
            var logStart = GetOperationalLogLength();
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
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
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_nkn_pending;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData("byte_cap_reached")]
    [InlineData("remote_byte_cap_reached")]
    [Trait("Category", "Smoke")]
    public async Task FileTransferByteCapFallback_WaitsForBulkReceiveProof(string reason)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.cap.probe.address");
            var helperClient = new FakeNknClient("helper.tuna.file.cap.probe.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-cap-probe-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-cap-probe-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_cap_probe";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, reason);
            var availabilityReason = $"sidecar_{reason}";

            await WaitUntilAsync(
                () =>
                {
                    var snapshot = availabilityEvents.ToArray();
                    return snapshot.Any(e => !e.IsAvailable && string.Equals(e.Reason, availabilityReason, StringComparison.Ordinal));
                },
                TimeSpan.FromSeconds(2));

            var events = availabilityEvents.ToArray();
            Assert.Contains(events, e => !e.IsAvailable && string.Equals(e.Reason, availabilityReason, StringComparison.Ordinal) && e.RequiresResumeRequest);
            Assert.DoesNotContain(events, e => e.IsAvailable && string.Equals(e.Reason, "transport_probe_unproven", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_probe_started;", logTail, StringComparison.Ordinal);
            Assert.Contains($"reason={availabilityReason}", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=cap_handoff_immediate", logTail, StringComparison.Ordinal);
            Assert.Contains("delay_ms=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_DoesNotMarkRecoveredUntilV6EpochObserverRecovers()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.v6-proof-gate.address");
            var helperClient = new FakeNknClient("helper.tuna.file.v6-proof-gate.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-v6-proof-gate-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-v6-proof-gate-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_v6_proof_gate";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var completedFromGenericControl = Assert.IsType<bool>(
                InvokePrivateMethod(helper, "CompleteFileTransferFallbackNknProofIfPending", "nkn_control_chat_received", sessionId));
            Assert.False(completedFromGenericControl);
            var genericProofTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_disable_handoff_completed;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("file_v6_epoch_state=recovered", genericProofTail, StringComparison.Ordinal);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    7,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var v6ProofTail = ReadOperationalLogTail(logStart);
            Assert.Contains("proof=filetransfer_v6_epoch_recovered", v6ProofTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_completed;", v6ProofTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_SameEpochWaitingOverridesPriorRecoveredObservation()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.v6-proof-waiting.address");
            var helperClient = new FakeNknClient("helper.tuna.file.v6-proof-waiting.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-v6-proof-waiting-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-v6-proof-waiting-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_v6_proof_waiting";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    18,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var waitingLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    18,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "proof_timeout",
                    IsUnresolved: true));

            var waitingTail = ReadOperationalLogTail(waitingLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observed;", waitingTail, StringComparison.Ordinal);
            Assert.Contains("state=waiting_for_target_transport", waitingTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", waitingTail, StringComparison.Ordinal);

            var fallbackState = GetPrivateField(helper, "tunaFallbackProofState");
            Assert.NotNull(fallbackState);
            var fallbackStateType = fallbackState.GetType();
            Assert.Equal(
                V6TransportEpochState.WaitingForTargetTransport,
                Assert.IsType<V6TransportEpochState>(fallbackStateType.GetProperty("FileV6EpochState")!.GetValue(fallbackState)));
            Assert.Equal(18L, Assert.IsType<long>(fallbackStateType.GetProperty("FileV6TransportEpoch")!.GetValue(fallbackState)));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_AllowsRegularNknRecoveryAfterRecoveredFallback()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.v6-proof-regular-recovery.address");
            var helperClient = new FakeNknClient("helper.tuna.file.v6-proof-regular-recovery.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-v6-proof-regular-recovery-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-v6-proof-regular-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_v6_proof_regular_recovery";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    21,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var recoveryLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    22,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "all_channels_zero_receive_max_restarts",
                    IsUnresolved: true));

            var recoveryTail = ReadOperationalLogTail(recoveryLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observed;", recoveryTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=regular_nkn_recovery", recoveryTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", recoveryTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackRecovered_FromPeerV6EpochSuppressesStaleTunaLaneAndReentry()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.peer-recovered-suppress.address");
            var helperClient = new FakeNknClient("helper.tuna.file.peer-recovered-suppress.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-peer-recovered-suppress-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-peer-recovered-suppress-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    e.Channel == NknBridgeChannel.Bulk &&
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
            const string transferId = "transfer_tuna_file_peer_recovered_suppress";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    17,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "frontier_chunk_proof",
                    IsUnresolved: false));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
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
            var fallbackEventCountBeforeLaneDrop = availabilityEvents.Count(e =>
                e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                !e.IsAvailable);

            fakeLane.SetAvailable(false, "remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_fallback_start_suppressed_final;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_fallback_recovered_state_synthesized;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_file_acceleration_suppressed_regular_nkn_fallback;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_fallback_start_suppressed_final;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_fallback_handoff_suppressed_duplicate;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Equal(
                fallbackEventCountBeforeLaneDrop,
                availabilityEvents.Count(e =>
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    !e.IsAvailable));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackRecovered_SuppressesAcceleratedFileBulkUntilFreshActivation()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.suppress.after.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.suppress.after.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-suppress-after-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-suppress-after-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    e.Channel == NknBridgeChannel.Bulk &&
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
            const string transferId = "transfer_tuna_file_suppress_after_fallback";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(rawNknDataFrames);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "StartTunaFallbackProofAndRebindIfNeeded",
                "receive_stall_recovery",
                sessionId,
                NknAccelerationLaneKind.File);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    11,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Single(fakeLane.Sent);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_file_acceleration_suppressed_regular_nkn_fallback;", logTail, StringComparison.Ordinal);
            Assert.Contains("effective_transport=nkn", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferTunaActivationPause_SuppressesBridgeRecoveredResumeUntilNegotiationResumes()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.pause-recovery.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.pause-recovery.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: false);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-pause-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-pause-recovery-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_pause_recovery";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "selected_payer_starting_listener",
                sessionId,
                "runtime_unlock");

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "tuna_activation_negotiating"),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));
            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "all_channels_zero_receive"));

            await Task.Delay(200, cts.Token);

            Assert.DoesNotContain(
                availabilityEvents,
                e => e.IsAvailable &&
                     e.Reason == "transport_recovered" &&
                     e.HandoffKind == FileTransferTransportHandoffKind.None);

            InvokePrivateMethod(
                host,
                "ResumeFileTransferDataSessionsAfterTunaActivationNegotiation",
                "tuna_activation_negotiated_transport_ready",
                sessionId,
                "test_negotiated");

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.Reason == "tuna_activation_negotiated_transport_ready" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_transport_recovered_suppressed;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=bridge_ready", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=receive_resumed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_RegularNknPrimaryDoesNotStartV6Epoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.file.receive-stall.no-epoch.address");
            var helperClient = new FakeNknClient("helper.file.receive-stall.no-epoch.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-file-receive-stall-no-epoch-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-file-receive-stall-no-epoch-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_file_receive_stall_no_epoch";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "bulk_receive_stalled"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "receive_stall_recovery" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_regular_nkn_receive_recovery_no_epoch;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_ready_unproven;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_PreservesRegularNknEpochKindForDelayedProbe()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-kind.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-kind.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-kind-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-kind-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_kind";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var directKind = Assert.IsType<FileTransferTransportHandoffKind>(
                InvokePrivateMethod(
                    helper,
                    "ResolveFileTransferDataSessionAvailabilityHandoffKind",
                    "bulk_receive_stalled",
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn));
            Assert.Equal(FileTransferTransportHandoffKind.RegularNknRecovery, directKind);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    23,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "receive_stall_recovery",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "transport_recovered_unproven",
                true,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered_unproven" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(
                availabilityEvents,
                e => e.Reason == "transport_recovered_unproven" &&
                     e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_availability_handoff_kind_preserved;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_AfterRecoveredTunaFallbackStartsRegularNknRecoveryEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-after-fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-after-fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-after-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-after-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_after_fallback";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    31,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "all_channels_zero_receive"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "receive_stall_recovery" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered_unproven" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_regular_nkn_receive_recovery_no_epoch;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_WithActiveSessionBroadcastsRecoverableRegularNknEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-exhausted.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-exhausted.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-exhausted-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-exhausted-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_exhausted";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "control_receive_stalled_max_restarts" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_broadcast;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_AfterRecoveredRegularNknEpochUsesCooldown()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-exhausted-recovered.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-exhausted-recovered.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-exhausted-recovered-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-exhausted-recovered-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_exhausted_recovered";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "control_receive_stalled_max_restarts" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    21,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var eventCountBeforeRepeat = availabilityEvents.Count;
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_control_receive_stall_recovery_broadcast_suppressed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Equal(eventCountBeforeRepeat, availabilityEvents.Count);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_after_recovered_epoch_allowed", logTail, StringComparison.Ordinal);
            Assert.Contains("suppress_reason=cooldown", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallback_DoesNotRestartRecoveredV6EpochFromSecondarySidecarError()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.fallback.dedupe.address");
            var helperClient = new FakeNknClient("helper.tuna.file.fallback.dedupe.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-fallback-dedupe-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-fallback-dedupe-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_fallback_dedupe";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    13,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var staleObservationLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    14,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "secondary_sidecar_error",
                    IsUnresolved: true));

            var staleObservationTail = ReadOperationalLogTail(staleObservationLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", staleObservationTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observed;", staleObservationTail, StringComparison.Ordinal);
            Assert.DoesNotContain("file_v6_epoch_state=target_proof_pending", staleObservationTail, StringComparison.Ordinal);

            var eventCountBeforeSecondaryError = availabilityEvents.Count(e =>
                e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                helper,
                "RebindFileTransferDataSessionsForTunaFallback",
                "sidecar_send_failed",
                sessionId,
                NknAccelerationLaneKind.File);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_fallback_handoff_suppressed_duplicate;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Equal(
                eventCountBeforeSecondaryError,
                availabilityEvents.Count(e => e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback));
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
        int? sidecarProtocolVersion = null,
        long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            SenderRole = "helper",
            TunaAddress = "nlink-tuna-sidecar.test-offer-address",
            SupportedLanes = supportedLanes ?? new[] { "file", "screen" },
            PayerDecisionId = payerDecisionId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
        string? rejectReason = null,
        long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            Accepted = accepted,
            SupportedLanes = supportedLanes ?? (accepted ? new[] { "file", "screen" } : Array.Empty<string>()),
            ExpiresAtUnixMs = expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = sidecarProtocolVersion ?? 1,
            RejectReason = rejectReason,
            PayerDecisionId = payerDecisionId,
        };

    private static TransportAccelerationDownPayload CreateDownPayload(string sessionId, string nonce, long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            SupportedLanes = new[] { "file", "screen" },
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = 1,
            Reason = "read_failed",
            PayerDecisionId = payerDecisionId,
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

    private sealed class RecordingTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
    {
        public List<string> StopReasons { get; } = [];

        public bool CanOfferListener => true;

        public Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(
            NknTunaListenerStartRequest request,
            CancellationToken ct)
            => Task.FromResult<NknTunaListenerSidecarEndpoint?>(null);

        public void Stop(string reason)
            => StopReasons.Add(reason);

        public void Dispose()
        {
        }
    }

    private sealed class RetryableTunaAccelerationSession : INknTunaAccelerationSession
    {
        private int canListen;
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
            this.canListen = canListen ? 1 : 0;
            this.failedDialAttemptsBeforeSuccess = failedDialAttemptsBeforeSuccess;
            this.failedListenerAttemptsBeforeSuccess = failedListenerAttemptsBeforeSuccess;
            this.supportedLanes = supportedLanes;
            this.deferSupportedLanesUntilAvailable = deferSupportedLanesUntilAvailable;
        }

        public bool IsAvailable => Volatile.Read(ref available) != 0;

        public bool CanOfferListener => Volatile.Read(ref canListen) != 0;

        public NknAccelerationLaneKind ConfiguredLanes => supportedLanes;

        public NknAccelerationLaneKind SupportedLanes
            => deferSupportedLanesUntilAvailable && !IsAvailable
                ? NknAccelerationLaneKind.None
                : supportedLanes;

        public string? LocalTunaAddress { get; private set; }

        public bool IsLocalPaidListenerActive { get; private set; }

        public int EnsureListenerCalls => Volatile.Read(ref ensureListenerCalls);

        public int StartDialerCalls => Volatile.Read(ref startDialerCalls);

        public void SetCanListen(bool value)
            => Volatile.Write(ref canListen, value ? 1 : 0);

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
            if (!CanOfferListener || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            if (calls <= failedListenerAttemptsBeforeSuccess)
            {
                return Task.FromResult(false);
            }

            LocalTunaAddress = "nlink-tuna-sidecar.test-listener-address";
            IsLocalPaidListenerActive = true;
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
            IsLocalPaidListenerActive = false;
            MarkAvailable("dialer_ready");
            return Task.FromResult(true);
        }

        public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
            => Task.FromResult(false);

        public Task StopAsync(string reason, CancellationToken ct)
        {
            Volatile.Write(ref available, 0);
            IsLocalPaidListenerActive = false;
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(false, reason));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Volatile.Write(ref available, 0);
            IsLocalPaidListenerActive = false;
        }

        private void MarkAvailable(string reason)
        {
            if (Interlocked.Exchange(ref available, 1) == 0)
            {
                StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(true, reason));
            }
        }
    }
}
