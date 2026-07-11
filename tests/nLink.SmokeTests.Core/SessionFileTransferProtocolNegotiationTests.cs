using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferProtocolNegotiationTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData("regular", FileTransferRouteResolver.RegularNknV4FastToken, FileTransferProtocol.ProtocolVersionV4, "regular_nkn_v4_fast", "regular_nkn_v4_fast", "file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0")]
    [InlineData("file_tuna", FileTransferRouteResolver.FileTunaV4Token, FileTransferProtocol.ProtocolVersionV4, "file_tuna_v4_fast", "tuna_strict", "file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0")]
    [InlineData("post_tuna_fallback", FileTransferRouteResolver.PostTunaFallbackV6Token, FileTransferProtocol.ProtocolVersionV6, "default_v6", "post_tuna_fallback_strict", "file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0")]
    public async Task OutboundOffer_LogsRouteSelectedTelemetry(
        string scenario,
        string routeToken,
        int protocolVersion,
        string runtimeProfile,
        string bridgeRecoveryPolicy,
        string statusMarkers)
    {
        var transferId = "transfer_protocol_route_selected_" + scenario;
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_route_selected_" + scenario);
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_route_selected_" + scenario);
        ConfigureRoute(senderTransport, routeToken);
        ConfigureRoute(receiverTransport, routeToken);
        if (string.Equals(routeToken, FileTransferRouteResolver.FileTunaV4Token, StringComparison.Ordinal))
        {
            senderTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound);
            receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        }

        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("route-selected-" + scenario + ".bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        Assert.Contains($"route={routeToken}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}", logTail, StringComparison.Ordinal);
        Assert.Contains($"bridge_recovery_policy={bridgeRecoveryPolicy}", logTail, StringComparison.Ordinal);
        Assert.Contains(statusMarkers, logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundOffer_OnRegularNknTransport_AdvertisesV4()
    {
        const string transferId = "transfer_protocol_offer_v4";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_v4");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_v4");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-v4.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, offer.FileTransferRoute);
    }

    [Fact]
    public async Task OutboundOffer_OnActiveFileTunaTransport_AdvertisesV4()
    {
        const string transferId = "transfer_protocol_offer_file_tuna_v4";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_file_tuna_v4")
        {
            IsTransportAccelerationActive = true,
            IsFileTunaActiveForRouteSelection = true,
            ShouldUseFileTransferV6ForAcceleration = false,
            TransportAccelerationStatusReason = "test_tuna_active",
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_file_tuna_v4")
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound);
        receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-v6.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, offer.FileTransferRoute);
    }

    [Fact]
    public async Task FirstMeasuredTransfer_WithFreshTunaReadiness_StartsAndPublishesFileTunaV4ThroughCoordinator()
    {
        const string transferId = "transfer_protocol_first_file_tuna_v4_ready";
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_first_file_tuna_v4_ready");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_first_file_tuna_v4_ready");
        senderTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound);
        receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("first-file-tuna-ready.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            TimeSpan.FromSeconds(10));

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, offer.FileTransferRoute);

        var currentLogTail = ReadOperationalLogTail(logStart);
        var logTail = currentLogTail + Environment.NewLine + ReadOperationalLogText();
        Assert.DoesNotContain("event=filetransfer_initial_tuna_route_selection_denied;", currentLogTail, StringComparison.Ordinal);
        var initialTunaGranted = FindTransferLogLine(
            logTail,
            "event=filetransfer_initial_tuna_route_selection_granted; direction=outbound;",
            transferId);
        Assert.Contains("requested_route=file_tuna_v4; requested_protocol_version=4", initialTunaGranted, StringComparison.Ordinal);
        Assert.Contains("selected_route=file_tuna_v4; selected_protocol_version=4", initialTunaGranted, StringComparison.Ordinal);
        Assert.Contains("requested_transport=tuna", initialTunaGranted, StringComparison.Ordinal);
        Assert.Contains("peer_visible_proof=1", initialTunaGranted, StringComparison.Ordinal);
        Assert.Contains("answer_received=1", initialTunaGranted, StringComparison.Ordinal);

        var outboundRouteSelected = FindTransferLogLine(
            logTail,
            "event=filetransfer_route_selected; direction=outbound;",
            transferId);
        Assert.Contains("route=file_tuna_v4; protocol_version=4", outboundRouteSelected, StringComparison.Ordinal);
        AssertPublicationAuthorityFields(
            outboundRouteSelected,
            route: FileTransferRouteResolver.FileTunaV4Token,
            protocolVersion: FileTransferProtocol.ProtocolVersionV4,
            targetTransport: "tuna");
        Assert.Contains("route_commit_generation=0", outboundRouteSelected, StringComparison.Ordinal);

        var outboundRuntimeStarted = FindTransferLogLine(
            logTail,
            "event=filetransfer_runtime_started; direction=outbound; role=sender;",
            transferId);
        Assert.Contains("route=file_tuna_v4; protocol_version=4", outboundRuntimeStarted, StringComparison.Ordinal);
        AssertPublicationAuthorityFields(
            outboundRuntimeStarted,
            route: FileTransferRouteResolver.FileTunaV4Token,
            protocolVersion: FileTransferProtocol.ProtocolVersionV4,
            targetTransport: "tuna");

        var inboundRouteSelected = FindTransferLogLine(
            logTail,
            "event=filetransfer_route_selected; direction=inbound;",
            transferId);
        var inboundRuntimeStarted = FindTransferLogLine(
            logTail,
            "event=filetransfer_runtime_started; direction=inbound; role=receiver;",
            transferId);
        var outboundHint = Assert.Single(senderTransport.ObservedRouteHints.Where(notification =>
            string.Equals(notification.TransferId, transferId, StringComparison.Ordinal) &&
            notification.PublicationAuthority?.Direction == FileTransferDirection.Outbound));
        var inboundHint = Assert.Single(receiverTransport.ObservedRouteHints.Where(notification =>
            string.Equals(notification.TransferId, transferId, StringComparison.Ordinal) &&
            notification.PublicationAuthority?.Direction == FileTransferDirection.Inbound));
        Assert.NotNull(outboundHint.PublicationAuthority);
        Assert.NotNull(inboundHint.PublicationAuthority);
        Assert.Equal(outboundHint.SessionId, outboundHint.PublicationAuthority!.SessionId);
        Assert.Equal(outboundHint.TransferId, outboundHint.PublicationAuthority.TransferId);
        Assert.Equal(inboundHint.SessionId, inboundHint.PublicationAuthority!.SessionId);
        Assert.Equal(inboundHint.TransferId, inboundHint.PublicationAuthority.TransferId);
        AssertPublicationAuthorityMatches(outboundRouteSelected, outboundHint.PublicationAuthority!);
        AssertPublicationAuthorityMatches(outboundRuntimeStarted, outboundHint.PublicationAuthority!);
        AssertPublicationAuthorityMatches(inboundRouteSelected, inboundHint.PublicationAuthority!);
        AssertPublicationAuthorityMatches(inboundRuntimeStarted, inboundHint.PublicationAuthority!);
        Assert.Equal(1, outboundHint.PublicationAuthority!.RoutePublicationGeneration);
        Assert.Equal(1, inboundHint.PublicationAuthority!.RoutePublicationGeneration);

        var scopedPublicationLog = string.Join(
            Environment.NewLine,
            outboundRouteSelected,
            outboundRuntimeStarted,
            inboundRouteSelected,
            inboundRuntimeStarted);
        Assert.DoesNotContain("route_publication_action_kind=RouteCommitPublication", scopedPublicationLog, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", scopedPublicationLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundOffer_OnActiveFileTunaTransportWithoutReadinessEvidence_AdvertisesRegularV4()
    {
        const string transferId = "transfer_protocol_offer_file_tuna_v4_active_only";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_file_tuna_v4_active_only")
        {
            IsTransportAccelerationActive = true,
            IsFileTunaActiveForRouteSelection = true,
            ShouldUseFileTransferV6ForAcceleration = false,
            TransportAccelerationStatusReason = "test_tuna_active",
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_file_tuna_v4_active_only")
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-active-only.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, offer.FileTransferRoute);
    }

    [Fact]
    public async Task InboundOffer_ExplicitFileTunaV4WithReadinessEvidence_IsAccepted()
    {
        const string transferId = "transfer_protocol_inbound_file_tuna_v4_ready";
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_inbound_file_tuna_v4_ready");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_inbound_file_tuna_v4_ready")
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_inbound_file_tuna_v4_ready",
                TransferId = transferId,
                FileName = "explicit-file-tuna-ready.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, receiver.Snapshot.Inbound!.RouteToken);
        Assert.Empty(receiverTransport.SentDeclines);
    }

    [Fact]
    public async Task InboundOffer_ExplicitFileTunaV4WithRouteCacheOnly_IsDeclined()
    {
        const string transferId = "transfer_protocol_inbound_file_tuna_v4_cache_only";
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_inbound_file_tuna_v4_cache_only");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_inbound_file_tuna_v4_cache_only")
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_inbound_file_tuna_v4_cache_only",
                TransferId = transferId,
                FileName = "explicit-file-tuna-cache-only.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDeclines.Count == 1);
        Assert.Null(receiver.Snapshot.Inbound);
        var decline = Assert.Single(receiverTransport.SentDeclines);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, decline.Reason);
    }

    [Fact]
    public async Task OutboundOffer_OnPostTunaFallbackTransport_AdvertisesFallbackV6()
    {
        const string transferId = "transfer_protocol_offer_fallback_v6";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_fallback_v6")
        {
            IsFileTunaActiveForRouteSelection = true,
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_fallback_v6")
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-fallback-v6.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, offer.FileTransferRoute);
    }

    [Fact]
    public async Task OutboundOffer_DiagnosticRegularNknV6_RequiresExplicitOptIn()
    {
        const string transferId = "transfer_protocol_offer_diagnostic_v6";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var defaultTransport = new LoopbackFileTransferTransport("session_protocol_offer_diagnostic_default");
        using var defaultPeer = new LoopbackFileTransferTransport("session_protocol_offer_diagnostic_default");
        defaultTransport.Connect(defaultPeer);
        using var defaultSender = new SessionFileTransferService();
        defaultSender.AttachTransport(defaultTransport);

        await defaultSender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-diagnostic-default.bin", payload.Length, transferId + "_default"),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => defaultTransport.SentOffers.Count == 1);
        var defaultOffer = Assert.Single(defaultTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, defaultOffer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, defaultOffer.FileTransferRoute);

        using var diagnosticTransport = new LoopbackFileTransferTransport("session_protocol_offer_diagnostic_v6")
        {
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        using var diagnosticPeer = new LoopbackFileTransferTransport("session_protocol_offer_diagnostic_v6")
        {
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        diagnosticTransport.Connect(diagnosticPeer);
        using var diagnosticSender = new SessionFileTransferService();
        using var diagnosticReceiver = new SessionFileTransferService();
        diagnosticSender.AttachTransport(diagnosticTransport);
        diagnosticReceiver.AttachTransport(diagnosticPeer);

        await diagnosticSender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-diagnostic-v6.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => diagnosticReceiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var diagnosticOffer = Assert.Single(diagnosticTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, diagnosticOffer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.DiagnosticRegularNknV6Token, diagnosticOffer.FileTransferRoute);
    }

    [Fact]
    public async Task OutboundOffer_OnAccelerationActiveWithoutFileLane_AdvertisesV4()
    {
        const string transferId = "transfer_protocol_offer_active_without_file_lane_v4";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_active_without_file_lane_v4")
        {
            IsTransportAccelerationActive = true,
            ShouldUseFileTransferV6ForAcceleration = false,
            TransportAccelerationStatusReason = "test_screen_tuna_active_file_regular_nkn",
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_active_without_file_lane_v4");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-active-without-file-lane-v4.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, offer.FileTransferRoute);
    }

    [Fact]
    public async Task OutboundStart_OnNonV6Transport_FailsBeforeOffer()
    {
        const string transferId = "transfer_protocol_transport_incompatible";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_transport_incompatible")
        {
            SupportsFileTransferV6Streaming = false,
        };
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("no-v4.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed);
        Assert.Empty(senderTransport.SentOffers);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Contains("event=filetransfer_v6_required_transport_incompatible", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundAccept_ForV4Offer_SendsV4Accept()
    {
        const string transferId = "transfer_protocol_accept_v4";
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_accept_v4");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_accept_v4");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_accept_v4",
                TransferId = transferId,
                FileName = "accept-v4.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentAccepts.Count == 1);
        var accept = Assert.Single(receiverTransport.SentAccepts);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, accept.AcceptedDataProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, accept.FileTransferRoute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task InboundOffer_WithLegacyOrMissingProtocol_IsDeclinedWithoutPendingTransfer(int? preferredVersion)
    {
        const string transferId = "transfer_protocol_legacy_offer";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_legacy_offer");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_legacy_offer");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_legacy_offer",
                TransferId = transferId,
                FileName = "legacy-offer.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = preferredVersion,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDeclines.Count == 1);
        var decline = Assert.Single(receiverTransport.SentDeclines);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, decline.Reason);
        Assert.Null(receiver.Snapshot.Inbound);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task OutboundAccept_WithLegacyOrMissingProtocol_FailsTransfer(int? acceptedVersion)
    {
        const string transferId = "transfer_protocol_legacy_accept";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_legacy_accept");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_legacy_accept");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("legacy-accept.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_legacy_accept",
                TransferId = transferId,
                AcceptedDataProtocolVersion = acceptedVersion,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(senderTransport.SentDataFrames);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundAccept_WithV4Protocol_StartsV4SenderRuntime()
    {
        const string transferId = "transfer_protocol_v4_accept_guard";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_v4_accept_guard");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_v4_accept_guard");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-accept.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_v4_accept_guard",
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Count == 1);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any());
        Assert.Equal(FileTransferTransferState.Sending, sender.Snapshot.Outbound!.State);
        Assert.Null(sender.Snapshot.Outbound.ErrorCode);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, sessionOpen.ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, sessionOpen.FileTransferRoute);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_negotiated", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_started", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundAccept_WithMismatchedRoute_FailsTransfer()
    {
        const string transferId = "transfer_protocol_accept_route_mismatch";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_accept_route_mismatch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_accept_route_mismatch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("route-mismatch-accept.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_accept_route_mismatch",
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(senderTransport.SentSessionOpens);
    }

    [Theory]
    [InlineData(nameof(FileTransferRouteResolver.PostTunaFallbackV6Token), FileTransferRouteResolver.PostTunaFallbackV6Token)]
    [InlineData(nameof(FileTransferRouteResolver.DiagnosticRegularNknV6Token), FileTransferRouteResolver.DiagnosticRegularNknV6Token)]
    public async Task OutboundAccept_WithV6Route_StartsMatchingSessionOpen(string routeName, string routeToken)
    {
        const string transferId = "transfer_protocol_v6_accept_guard";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_v6_accept_guard");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_v6_accept_guard");
        ConfigureRoute(senderTransport, routeToken);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(routeName + ".bin", payload.Length, transferId + "_" + routeName),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);
        Assert.Equal(routeToken, Assert.Single(senderTransport.SentOffers).FileTransferRoute);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_v6_accept_guard",
                TransferId = transferId + "_" + routeName,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = routeToken,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Count == 1);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, sessionOpen.ProtocolVersion);
        Assert.Equal(routeToken, sessionOpen.FileTransferRoute);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task InboundSessionOpen_WithNonV6Protocol_DoesNotStartDataSession(int protocolVersion)
    {
        const string transferId = "transfer_protocol_non_v4_session_open";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_non_v4_session_open");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_non_v4_session_open")
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_non_v4_session_open",
                TransferId = transferId,
                FileName = "non-v4-session-open.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        var sessionId = receiver.Snapshot.Inbound!.SessionId;

        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = protocolVersion,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Single(receiverTransport.SentErrors);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_session_opened", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_session_open_rejected", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundSessionOpen_WithV4Protocol_StartsReceiverAndWaitsForManifest()
    {
        const string transferId = "transfer_protocol_v4_session_open_guard";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_v4_session_open_guard");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_v4_session_open_guard");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_v4_session_open_guard",
                TransferId = transferId,
                FileName = "v4-session-open.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        var sessionId = receiver.Snapshot.Inbound!.SessionId;

        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_receiver_started", StringComparison.Ordinal));
        Assert.Equal(FileTransferTransferState.AwaitingMetadata, receiver.Snapshot.Inbound!.State);
        Assert.Null(receiver.Snapshot.Inbound.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_route_selected; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains($"route={FileTransferRouteResolver.RegularNknV4FastToken}; protocol_version=4", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_session_opened", logTail, StringComparison.Ordinal);
        Assert.Contains($"route={FileTransferRouteResolver.RegularNknV4FastToken}", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_negotiated", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_session_open_rejected", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundSessionOpen_WithMismatchedRoute_FailsAndSendsError()
    {
        const string transferId = "transfer_protocol_session_open_route_mismatch";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_session_open_route_mismatch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_session_open_route_mismatch");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_session_open_route_mismatch",
                TransferId = transferId,
                FileName = "route-mismatch-session-open.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        var sessionId = receiver.Snapshot.Inbound!.SessionId;

        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Single(receiverTransport.SentErrors);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_runtime_started", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_receiver_started", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started", logTail, StringComparison.Ordinal);
    }

    private static void ConfigureRoute(LoopbackFileTransferTransport transport, string routeToken)
    {
        if (string.Equals(routeToken, FileTransferRouteResolver.FileTunaV4Token, StringComparison.Ordinal))
        {
            transport.IsFileTunaActiveForRouteSelection = true;
        }
        else if (string.Equals(routeToken, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal))
        {
            transport.IsFileTunaActiveForRouteSelection = true;
            transport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        }
        else if (string.Equals(routeToken, FileTransferRouteResolver.DiagnosticRegularNknV6Token, StringComparison.Ordinal))
        {
            transport.IsDiagnosticRegularNknV6RouteEnabled = true;
        }
    }

    private static string FindTransferLogLine(string logText, string eventPrefix, string transferId)
    {
        var matchingLines = logText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(eventPrefix, StringComparison.Ordinal))
            .ToArray();
        var line = matchingLines.LastOrDefault(line => line.Contains($"transfer_id={transferId}", StringComparison.Ordinal)) ??
                   matchingLines.LastOrDefault(line => line.Contains("route=file_tuna_v4; protocol_version=4", StringComparison.Ordinal)) ??
                   matchingLines.LastOrDefault(line => line.Contains("selected_route=file_tuna_v4; selected_protocol_version=4", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(line), $"Missing log line '{eventPrefix}' for transfer '{transferId}'." + Environment.NewLine + logText);
        return line!;
    }

    private static void AssertPublicationAuthorityFields(
        string logLine,
        string route,
        int protocolVersion,
        string targetTransport)
    {
        Assert.Contains("live_route_epoch=", logLine, StringComparison.Ordinal);
        Assert.Contains("route_publication_generation=", logLine, StringComparison.Ordinal);
        Assert.Contains("runtime_generation=", logLine, StringComparison.Ordinal);
        Assert.Contains("terminal_generation=", logLine, StringComparison.Ordinal);
        Assert.Contains("leg_generation=", logLine, StringComparison.Ordinal);
        Assert.Contains("route_commit_generation=", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_route={route}", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_protocol_version={protocolVersion}", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_transport={targetTransport}", logLine, StringComparison.Ordinal);
        Assert.Contains("route_publication_action_kind=RoutePublication", logLine, StringComparison.Ordinal);
        Assert.Contains("coordinator_decision_reason=accepted", logLine, StringComparison.Ordinal);
    }

    private static void AssertPublicationAuthorityMatches(
        string logLine,
        FileTransferRoutePublicationAuthority authority)
    {
        var targetTransport = authority.TargetTransport switch
        {
            FileTransferTransportKind.RegularNkn => "regular_nkn",
            FileTransferTransportKind.Tuna => "tuna",
            _ => "unknown",
        };
        Assert.Contains($"direction={authority.Direction.ToString().ToLowerInvariant()}", logLine, StringComparison.Ordinal);
        Assert.Contains($"route={authority.RouteToken}; protocol_version={authority.ProtocolVersion}", logLine, StringComparison.Ordinal);
        Assert.Contains($"live_route_epoch={authority.LiveRouteEpoch}", logLine, StringComparison.Ordinal);
        Assert.Contains($"route_publication_generation={authority.RoutePublicationGeneration}", logLine, StringComparison.Ordinal);
        Assert.Contains($"runtime_generation={authority.RuntimeGeneration}", logLine, StringComparison.Ordinal);
        Assert.Contains($"terminal_generation={authority.TerminalGeneration}", logLine, StringComparison.Ordinal);
        Assert.Contains($"leg_generation={authority.LegGeneration}", logLine, StringComparison.Ordinal);
        Assert.Contains($"route_commit_generation={authority.RouteCommitGeneration ?? 0}", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_route={authority.TargetRouteToken}", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_protocol_version={authority.TargetProtocolVersion}", logLine, StringComparison.Ordinal);
        Assert.Contains($"target_transport={targetTransport}", logLine, StringComparison.Ordinal);
        Assert.Contains($"route_publication_action_kind={authority.RoutePublicationActionKind}", logLine, StringComparison.Ordinal);
        Assert.Contains($"coordinator_decision_reason={authority.CoordinatorDecisionReason}", logLine, StringComparison.Ordinal);
    }
}
