using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferRouteRuntimeRegressionTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData("tuna_disabled", FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4, "regular_nkn_v4_fast", "v4", "regular_nkn_v4_fast")]
    [InlineData("tuna_configured_inactive", FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4, "regular_nkn_v4_fast", "v4", "regular_nkn_v4_fast")]
    [InlineData("tuna_activation_failed", FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4, "regular_nkn_v4_fast", "v4", "regular_nkn_v4_fast")]
    [InlineData("screen_share_acceleration_only", FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4, "regular_nkn_v4_fast", "v4", "regular_nkn_v4_fast")]
    [InlineData("active_file_tuna", FileTransferRouteResolver.FileTunaV4Token, FileTransferProtocol.ProtocolVersionV4, "file_tuna_v4_fast", "v4", "tuna_strict")]
    [InlineData("post_tuna_fallback", FileTransferRouteResolver.PostTunaFallbackV6Token, FileTransferProtocol.ProtocolVersionV6, "default_v6", "v6", "post_tuna_fallback_strict")]
    public async Task RouteSelectionLifecycle_UsesExpectedProtocolRuntimeAndTelemetry(
        string scenario,
        string routeToken,
        int protocolVersion,
        string runtimeProfile,
        string frameFamily,
        string bridgeRecoveryPolicy)
    {
        var transferId = "p4rt_" + ShortScenarioId(scenario);
        var result = await RunAcceptedLoopbackTransferAsync(scenario, transferId, transport => ConfigureScenarioRouteStatus(transport, scenario));

        AssertWireRoute(result, routeToken, protocolVersion);
        AssertFrameFamily(result, protocolVersion);
        AssertRouteAwareLogConsistency(
            result.LogTail,
            transferId,
            routeToken,
            protocolVersion,
            runtimeProfile,
            frameFamily,
            bridgeRecoveryPolicy);
    }

    [Fact]
    public async Task RegularNknV4Fast_NeverEntersRegularV6Runtime()
    {
        const string transferId = "p4_regular_no_v6";

        var result = await RunAcceptedLoopbackTransferAsync("regular_no_v6", transferId, static _ => { });

        AssertWireRoute(result, FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4);
        Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        var regularBatches = result.SenderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV4>()
            .Where(static frame => frame is not FileTransferChunkBatchFrameV6)
            .ToArray();
        Assert.NotEmpty(regularBatches);
        Assert.All(regularBatches, static batch => Assert.True(batch.ForceRegularNknBulk));
        Assert.DoesNotContain(result.SenderTransport.SentDataFrames, static frame => FileTransferProtocol.IsV6DataFrame(frame));
        Assert.DoesNotContain(result.ReceiverTransport.SentDataFrames, static frame => FileTransferProtocol.IsV6DataFrame(frame));
        Assert.DoesNotContain("event=filetransfer_v6_sender_started; transfer_id=" + transferId, result.LogTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started; transfer_id=" + transferId, result.LogTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound; transfer_id=" + transferId, result.LogTail, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime_profile=primary_regular_nkn_bulk_v6", FilterTransferLog(result.LogTail, transferId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTunaV4_NeverEntersV6Runtime()
    {
        const string transferId = "p4_file_tuna_v4_no_v6";

        var result = await RunAcceptedLoopbackTransferAsync(
            "file_tuna_v4_no_v6",
            transferId,
            transport => ConfigureRouteToken(transport, FileTransferRouteResolver.FileTunaV4Token));

        AssertWireRoute(result, FileTransferRouteResolver.FileTunaV4Token, FileTransferProtocol.ProtocolVersionV4);
        Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        var tunaBatches = result.SenderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV4>()
            .Where(static frame => frame is not FileTransferChunkBatchFrameV6)
            .ToArray();
        Assert.NotEmpty(tunaBatches);
        Assert.All(tunaBatches, static batch => Assert.False(batch.ForceRegularNknBulk));
        Assert.DoesNotContain(result.SenderTransport.SentDataFrames, static frame => FileTransferProtocol.IsV6DataFrame(frame));
        Assert.DoesNotContain(result.ReceiverTransport.SentDataFrames, static frame => FileTransferProtocol.IsV6DataFrame(frame));
        Assert.DoesNotContain("event=filetransfer_v6_sender_started; transfer_id=" + transferId, result.LogTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started; transfer_id=" + transferId, result.LogTail, StringComparison.Ordinal);
        AssertRouteAwareLogConsistency(
            result.LogTail,
            transferId,
            FileTransferRouteResolver.FileTunaV4Token,
            FileTransferProtocol.ProtocolVersionV4,
            "file_tuna_v4_fast",
            "v4",
            "tuna_strict");
    }

    [Theory]
    [InlineData("mixed_regular_v4", FileTransferRouteResolver.RegularNknV4FastToken, "regular_nkn_v4_fast", "regular_nkn_v4_fast")]
    [InlineData("mixed_file_tuna_v4", FileTransferRouteResolver.FileTunaV4Token, "file_tuna_v4_fast", "tuna_strict")]
    public async Task V4Routes_WithActiveScreenShare_StayV4AndExposeMixedTransferState(
        string scenario,
        string routeToken,
        string runtimeProfile,
        string bridgeRecoveryPolicy)
    {
        var transferId = "p4_" + ShortScenarioId(scenario);
        var sessionId = "p4_session_" + ShortScenarioId(scenario);
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            DataSessionSendDelayMs = 2,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        ConfigureRouteToken(senderTransport, routeToken);
        ConfigureRouteToken(receiverTransport, routeToken);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(scenario + ".bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.IsV4MixedScreenShareTransferActive &&
                  receiver.IsV4MixedScreenShareTransferActive,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var result = new RouteRuntimeResult(
            senderTransport,
            receiverTransport,
            Assert.Single(senderTransport.SentOffers),
            Assert.Single(receiverTransport.SentAccepts),
            Assert.Single(senderTransport.SentSessionOpens),
            ReadRouteLogSnapshot(logStart));
        var transferLog = FilterTransferLog(result.LogTail, transferId);

        AssertWireRoute(result, routeToken, FileTransferProtocol.ProtocolVersionV4);
        AssertFrameFamily(result, FileTransferProtocol.ProtocolVersionV4);
        AssertRouteAwareLogConsistency(
            result.LogTail,
            transferId,
            routeToken,
            FileTransferProtocol.ProtocolVersionV4,
            runtimeProfile,
            "v4",
            bridgeRecoveryPolicy);
        Assert.Contains("event=filetransfer_v4_mixed_screenshare_enabled; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("mixed_screenshare=1", transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_sender_started; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("post_tuna_fallback_runtime_guard", FileTransferRouteResolver.PostTunaFallbackV6Token, "post_tuna_fallback_strict")]
    public async Task V6FallbackRoute_NeverEntersRegularV4RouteOrRuntime(
        string scenario,
        string routeToken,
        string bridgeRecoveryPolicy)
    {
        var transferId = "p4_" + ShortScenarioId(scenario);
        var result = await RunAcceptedLoopbackTransferAsync(scenario, transferId, transport => ConfigureRouteToken(transport, routeToken));
        var transferLog = FilterTransferLog(result.LogTail, transferId);

        AssertWireRoute(result, routeToken, FileTransferProtocol.ProtocolVersionV6);
        AssertFrameFamily(result, FileTransferProtocol.ProtocolVersionV6);
        AssertRouteAwareLogConsistency(
            result.LogTail,
            transferId,
            routeToken,
            FileTransferProtocol.ProtocolVersionV6,
            "default_v6",
            "v6",
            bridgeRecoveryPolicy);
        Assert.DoesNotContain("route=regular_nkn_v4_fast", transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_started; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_receiver_started; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_negotiated; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticRegularNknV6_RequiresExplicitOptIn()
    {
        const string defaultTransferId = "p4_diag_default";
        const string diagnosticTransferId = "p4_diag_opt";

        var defaultResult = await RunAcceptedLoopbackTransferAsync(
            "diagnostic_default_guard",
            defaultTransferId,
            static transport =>
            {
                transport.IsTransportAccelerationActive = true;
                transport.ShouldUseFileTransferV6ForAcceleration = false;
                transport.TransportAccelerationStatusReason = "test_primary_regular_nkn_bulk_v6_status_reason_without_opt_in";
            });
        AssertWireRoute(defaultResult, FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4);
        Assert.DoesNotContain("route=diagnostic_regular_nkn_v6", FilterTransferLog(defaultResult.LogTail, defaultTransferId), StringComparison.Ordinal);
        Assert.DoesNotContain("runtime_profile=primary_regular_nkn_bulk_v6", FilterTransferLog(defaultResult.LogTail, defaultTransferId), StringComparison.Ordinal);

        var diagnosticResult = await RunAcceptedLoopbackTransferAsync(
            "diagnostic_opt_in_guard",
            diagnosticTransferId,
            static transport => transport.IsDiagnosticRegularNknV6RouteEnabled = true);
        AssertWireRoute(diagnosticResult, FileTransferRouteResolver.DiagnosticRegularNknV6Token, FileTransferProtocol.ProtocolVersionV6);
        AssertRouteAwareLogConsistency(
            diagnosticResult.LogTail,
            diagnosticTransferId,
            FileTransferRouteResolver.DiagnosticRegularNknV6Token,
            FileTransferProtocol.ProtocolVersionV6,
            "primary_regular_nkn_bulk_v6",
            "v6",
            "primary_regular_nkn_quiet");
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound; transfer_id=" + diagnosticTransferId, diagnosticResult.LogTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6_SuccessConsumesFallbackRouteAndNextTransferUsesRegularV4()
    {
        const string firstTransferId = "p4_post_tuna_fallback_one_shot";
        const string secondTransferId = "p4_after_post_tuna_fallback_regular_v4";
        const string sessionId = "p4_session_post_fallback_one_shot";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
            TransportAccelerationStatusReason = "test_post_tuna_fallback_active",
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
            TransportAccelerationStatusReason = "test_post_tuna_fallback_active",
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var firstPayload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var firstDestination = new NonDisposingMemoryStream();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("post-tuna-fallback-v6-one-shot.bin", firstPayload.Length, firstTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(firstPayload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(firstTransferId, (_, _) => Task.FromResult<Stream>(firstDestination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);
        Assert.Equal(firstPayload, firstDestination.ToArray()[..firstPayload.Length]);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.Contains(
            senderTransport.RouteCompletionNotifications,
            notification => notification.TransferId == firstTransferId &&
                            notification.RouteToken == FileTransferRouteResolver.PostTunaFallbackV6Token &&
                            notification.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6);
        Assert.Contains(
            receiverTransport.RouteCompletionNotifications,
            notification => notification.TransferId == firstTransferId &&
                            notification.RouteToken == FileTransferRouteResolver.PostTunaFallbackV6Token &&
                            notification.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6);

        var secondPayload = Enumerable.Range(0, 128_000).Select(static index => (byte)((index * 7) % 251)).ToArray();
        using var secondDestination = new NonDisposingMemoryStream();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("after-post-tuna-fallback-regular-v4.bin", secondPayload.Length, secondTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(secondPayload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { } inbound &&
                                  inbound.TransferId == secondTransferId &&
                                  inbound.State == FileTransferTransferState.PendingDecision,
            timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(secondTransferId, (_, _) => Task.FromResult<Stream>(secondDestination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { } outbound &&
                  receiver.Snapshot.Inbound is { } inbound &&
                  outbound.TransferId == secondTransferId &&
                  outbound.State == FileTransferTransferState.Completed &&
                  inbound.TransferId == secondTransferId &&
                  inbound.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);
        Assert.Equal(secondPayload, secondDestination.ToArray()[..secondPayload.Length]);

        var logTail = ReadRouteLogSnapshot(logStart);
        var firstLog = FilterTransferLog(logTail, firstTransferId);
        var secondLog = FilterTransferLog(logTail, secondTransferId);
        Assert.Contains("route=post_tuna_fallback_v6", firstLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_sender_started", firstLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_receiver_started", firstLog, StringComparison.Ordinal);
        Assert.Contains("route=regular_nkn_v4_fast", secondLog, StringComparison.Ordinal);
        Assert.DoesNotContain("route=post_tuna_fallback_v6", secondLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_sender_started", secondLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started", secondLog, StringComparison.Ordinal);

        var firstOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == firstTransferId);
        var secondOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == secondTransferId);
        Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, firstOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, firstOffer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, secondOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, secondOffer.PreferredDataProtocolVersion);
        Assert.All(
            senderTransport.SentDataFrames.Where(frame => frame.TransferId == firstTransferId),
            static frame => Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected one-shot fallback V6 frame, got {frame.Type}."));
        Assert.All(
            senderTransport.SentDataFrames.Where(frame => frame.TransferId == secondTransferId),
            static frame => Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected regular V4 frame after one-shot fallback, got {frame.Type}."));
    }

    private static void ConfigureScenarioRouteStatus(LoopbackFileTransferTransport transport, string scenario)
    {
        switch (scenario)
        {
            case "tuna_configured_inactive":
                transport.TransportAccelerationStatusReason = "test_tuna_configured_eligible_inactive";
                break;
            case "tuna_activation_failed":
                transport.IsTransportAccelerationActive = false;
                transport.ShouldUseFileTransferV6ForAcceleration = false;
                transport.TransportAccelerationStatusReason = "test_tuna_activation_failed";
                break;
            case "screen_share_acceleration_only":
                transport.IsTransportAccelerationActive = true;
                transport.ShouldUseFileTransferV6ForAcceleration = false;
                transport.TransportAccelerationStatusReason = "test_screen_share_acceleration_active_file_regular_nkn";
                break;
            case "active_file_tuna":
                transport.IsTransportAccelerationActive = true;
                transport.IsFileTunaActiveForRouteSelection = true;
                transport.TransportAccelerationStatusReason = "test_file_tuna_active";
                break;
            case "post_tuna_fallback":
                transport.IsPostTunaFileFallbackActiveForRouteSelection = true;
                transport.TransportAccelerationStatusReason = "test_post_tuna_file_fallback_active";
                break;
        }
    }

    private static void ConfigureRouteToken(LoopbackFileTransferTransport transport, string routeToken)
    {
        if (string.Equals(routeToken, FileTransferRouteResolver.FileTunaV4Token, StringComparison.Ordinal))
        {
            transport.IsFileTunaActiveForRouteSelection = true;
            transport.IsTransportAccelerationActive = true;
            transport.TransportAccelerationStatusReason = "test_file_tuna_active";
        }
        else if (string.Equals(routeToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal))
        {
            transport.IsPostTunaFileFallbackActiveForRouteSelection = true;
            transport.TransportAccelerationStatusReason = "test_post_tuna_file_fallback_active";
        }
        else if (string.Equals(routeToken, FileTransferRouteResolver.DiagnosticRegularNknV6Token, StringComparison.Ordinal))
        {
            transport.IsDiagnosticRegularNknV6RouteEnabled = true;
        }
    }

    private static async Task<RouteRuntimeResult> RunAcceptedLoopbackTransferAsync(
        string scenario,
        string transferId,
        Action<LoopbackFileTransferTransport> configureTransport)
    {
        var sessionId = "p4_session_" + ShortScenarioId(scenario);
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 96_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        configureTransport(senderTransport);
        configureTransport(receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(scenario + ".bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);
        await WaitUntilAsync(
            () => ReadRouteLogSnapshot(logStart).Contains("event=filetransfer_route_selected; direction=outbound; transfer_id=" + transferId, StringComparison.Ordinal) &&
                  ReadRouteLogSnapshot(logStart).Contains("event=filetransfer_route_selected; direction=inbound; transfer_id=" + transferId, StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        return new RouteRuntimeResult(
            senderTransport,
            receiverTransport,
            Assert.Single(senderTransport.SentOffers),
            Assert.Single(receiverTransport.SentAccepts),
            Assert.Single(senderTransport.SentSessionOpens),
            ReadRouteLogSnapshot(logStart));
    }

    private static void AssertWireRoute(RouteRuntimeResult result, string routeToken, int protocolVersion)
    {
        Assert.Equal(protocolVersion, result.Offer.PreferredDataProtocolVersion);
        Assert.Equal(routeToken, result.Offer.FileTransferRoute);
        Assert.Equal(protocolVersion, result.Accept.AcceptedDataProtocolVersion);
        Assert.Equal(routeToken, result.Accept.FileTransferRoute);
        Assert.Equal(protocolVersion, result.SessionOpen.ProtocolVersion);
        Assert.Equal(routeToken, result.SessionOpen.FileTransferRoute);
    }

    private static void AssertFrameFamily(RouteRuntimeResult result, int protocolVersion)
    {
        if (protocolVersion == FileTransferProtocol.ProtocolVersionV4)
        {
            Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
            Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6);
            Assert.Contains(result.ReceiverTransport.SentDataFrames, static frame => frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6);
            Assert.All(result.SenderTransport.SentDataFrames, static frame => Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected V4 sender frame, got {frame.Type}."));
            Assert.All(result.ReceiverTransport.SentDataFrames, static frame => Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected V4 receiver frame, got {frame.Type}."));
            return;
        }

        Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV6);
        Assert.Contains(result.SenderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
        Assert.Contains(result.ReceiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);
        Assert.All(result.SenderTransport.SentDataFrames, static frame => Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected V6 sender frame, got {frame.Type}."));
        Assert.All(result.ReceiverTransport.SentDataFrames, static frame => Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected V6 receiver frame, got {frame.Type}."));
    }

    private static void AssertRouteAwareLogConsistency(
        string logTail,
        string transferId,
        string routeToken,
        int protocolVersion,
        string runtimeProfile,
        string frameFamily,
        string bridgeRecoveryPolicy)
    {
        var transferLog = FilterTransferLog(logTail, transferId);
        Assert.Contains("event=filetransfer_route_selected; direction=outbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_route_selected; direction=inbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_protocol_negotiated; direction=outbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_protocol_negotiated; direction=inbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_session_opened; direction=outbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_session_opened; direction=inbound; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id=" + transferId, transferLog, StringComparison.Ordinal);

        var routeAwareLines = transferLog
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static line =>
                line.Contains("event=filetransfer_route_selected", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_protocol_negotiated", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_session_opened", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_runtime_started", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_bridge_recovery_policy_selected", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_v4_sender_started", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_v4_receiver_started", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_v6_sender_started", StringComparison.Ordinal) ||
                line.Contains("event=filetransfer_v6_receiver_started", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(routeAwareLines);

        foreach (var line in routeAwareLines)
        {
            Assert.Contains("route=" + routeToken, line, StringComparison.Ordinal);
            Assert.Contains("protocol_version=" + protocolVersion, line, StringComparison.Ordinal);
            Assert.Contains("runtime_profile=" + runtimeProfile, line, StringComparison.Ordinal);
            Assert.Contains("frame_family=" + frameFamily, line, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=" + bridgeRecoveryPolicy, line, StringComparison.Ordinal);
        }
    }

    private static string FilterTransferLog(string logTail, string transferId)
        => string.Join(
            Environment.NewLine,
            logTail
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("transfer_id=" + transferId, StringComparison.Ordinal)));

    private static string ReadRouteLogSnapshot(int logStart)
        => ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();

    private static string ShortScenarioId(string scenario)
        => scenario switch
        {
            "tuna_disabled" => "td",
            "tuna_configured_inactive" => "ti",
            "tuna_activation_failed" => "tf",
            "screen_share_acceleration_only" => "ss",
            "active_file_tuna" => "ft",
            "post_tuna_fallback" => "pf",
            "file_tuna_runtime_guard" => "ft_guard",
            "file_tuna_v4_no_v6" => "ft_v4",
            "post_tuna_fallback_runtime_guard" => "pf_guard",
            "regular_no_v6" => "regular",
            "diagnostic_default_guard" => "diag_default",
            "diagnostic_opt_in_guard" => "diag_opt",
            "mixed_regular_v4" => "mixed_reg",
            "mixed_file_tuna_v4" => "mixed_tuna",
            _ => scenario.Length <= 20 ? scenario : scenario[..20],
        };

    private sealed record RouteRuntimeResult(
        LoopbackFileTransferTransport SenderTransport,
        LoopbackFileTransferTransport ReceiverTransport,
        FileTransferOfferV2 Offer,
        FileTransferAcceptV1 Accept,
        FileTransferSessionOpenV2 SessionOpen,
        string LogTail);
}
