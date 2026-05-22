using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.DevLocal;

// DEV ONLY: local machine named-pipe transport for testing two app instances without real networking.
public sealed class DevLocalTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, IHelpRequestSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, IScreenShareCursorOverlayCapabilityProvider, IFileTransferSignalingTransport, IFileTransferProtocolCapabilities, IFileTransferTransportProfileProvider
{
    private const string HelpRequestFrameType = "help_request";
    private const string HelpRequestDecisionFrameType = "help_request_decision";
    private const string JoinFrameType = "join";
    private const string HelloFrameType = "hello";
    private const string SessionHandshakeStartFrameType = "session_handshake_start";
    private const string SessionHandshakeChallengeFrameType = "session_handshake_challenge";
    private const string SessionHandshakeResponseFrameType = "session_handshake_response";
    private const string SessionHandshakeResultFrameType = "session_handshake_result";
    private const string ApproveFrameType = "approve";
    private const string RejectFrameType = "reject";
    private const string ChatFrameType = "chat";
    private const string ControlRequestFrameType = "control_request";
    private const string ControlResponseFrameType = "control_response";
    private const string ControlStartFrameType = "control_start";
    private const string ControlStopFrameType = "control_stop";
    private const string ControlInputFrameType = "control_input";
    private const string ControlAckFrameType = "control_ack";
    private const string ControlStateSnapshotFrameType = "control_state_snapshot";
    private const string ControlDisplayInfoFrameType = "control_display_info";
    private const string ScreenSharePressureStateFrameType = "screenshare_pressure_state";
    private const string ScreenSharePayloadFrameType = "screenshare_payload";
    private const string ScreenShareStopFrameType = "screenshare_stop";
    private const string ScreenShareVideoStreamConfigFrameType = "screenshare_video_stream_config";
    private const string ScreenShareVideoKeyframeRequestFrameType = "screenshare_video_keyframe_request";
    private const string ScreenShareRecoveryReceiptFrameType = "screenshare_recovery_receipt";
    private const string ScreenShareCursorStateFrameType = "screenshare_cursor_state";
    private const string FileTransferOfferFrameType = "file_transfer_offer";
    private const string FileTransferAcceptFrameType = "file_transfer_accept";
    private const string FileTransferDeclineFrameType = "file_transfer_decline";
    private const string FileTransferStartFrameType = "file_transfer_start";
    private const string FileTransferChunkFrameType = "file_transfer_chunk";
    private const string FileTransferWindowUpdateFrameType = "file_transfer_window_update";
    private const string FileTransferMissingRangeFrameType = "file_transfer_missing_range";
    private const string FileTransferPressureStateFrameType = "file_transfer_pressure_state";
    private const string FileTransferSessionOpenFrameType = "file_transfer_session_open";
    private const string FileTransferDataFrameType = "file_transfer_data_frame";
    private const string FileTransferCancelFrameType = "file_transfer_cancel";
    private const string FileTransferErrorFrameType = "file_transfer_error";
    private const string FileTransferCompleteFrameType = "file_transfer_complete";
    private const string FileTransferPauseControlFrameType = "file_transfer_pause_control";
    private const string FileTransferHeartbeatFrameType = "file_transfer_heartbeat";
    private const string FileTransferTransportEpochFrameType = "file_transfer_transport_epoch";
    private const string FileTransferTransportProbeFrameType = "file_transfer_transport_probe";
    private const string FileTransferRepairProofFrameType = "file_transfer_repair_proof";
    private const int ConnectTimeoutMs = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object activeConnectionGate = new();
    private readonly object hostReadyGate = new();
    private readonly IInviteTokenValidator inviteTokenValidator;
    private readonly IInviteValidationThrottle inviteValidationThrottle;
    private readonly ISessionHandshakeReplayCache handshakeReplayCache;
    private readonly ScreenShareVideoFrameReassembler screenShareFrameReassembler = new();
    private readonly object secureStateGate = new();
    private readonly SessionReplayWindow inboundChatReplayWindow = new();
    private readonly SessionReplayWindow inboundControlReplayWindow = new();
    private readonly SessionReplayWindow inboundLifecycleReplayWindow = new();
    private readonly SessionReplayWindow inboundScreenShareReplayWindow = new();
    private readonly SessionReplayWindow inboundFileTransferReplayWindow = new();
    private readonly Dictionary<string, FileTransferTransportState> fileTransferStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportFileTransferDataSession> fileTransferDataSessions = new(StringComparer.Ordinal);
    private const bool LocalRemoteControlSupported = true;
    private const bool LocalScreenShareCursorOverlaySupported = true;
    private readonly DevLocalImpairmentPolicy? impairmentPolicy;
    private readonly string localPeerAddress;
    private SessionConnection? activeConnection;
    private PendingOutboundHandshakeState? pendingOutboundHandshake;
    private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
    private SessionId? activeApprovedSessionId;
    private PeerAddress? activeApprovedHelperAddress;

    public bool SupportsFileTransferV6Streaming => true;
    public FileTransferTransportProfileKind FileTransferTransportProfileKind => FileTransferTransportProfileKind.Default;
    private byte[]? controlSessionSharedKey;
    private long nextOutboundChatSecureSequence;
    private long nextOutboundControlSecureSequence;
    private long nextOutboundLifecycleSecureSequence;
    private long nextOutboundScreenShareSecureSequence;
    private long nextOutboundFileTransferSecureSequence;
    private TaskCompletionSource<bool> hostReadyTcs = CreateHostReadyTcs();
    private bool remoteSupportsRemoteControl;
    private bool remoteSupportsScreenShareCursorOverlay;
    private bool disposed;

    public DevLocalTransport(string? localPeerAddress = null, DevLocalImpairmentOptions? impairmentOptions = null)
    {
        this.localPeerAddress = NormalizeLocalPeerAddress(localPeerAddress);
        if (impairmentOptions is { IsEnabled: true })
        {
            impairmentPolicy = new DevLocalImpairmentPolicy(impairmentOptions);
        }

        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        screenShareFrameReassembler.FrameReady += OnScreenShareFrameReady;
        screenShareFrameReassembler.KeyframeRequested += OnScreenShareKeyframeRequested;
    }

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
    public event EventHandler<IncomingHelpRequestEventArgs>? IncomingHelpRequest;
    public event EventHandler<HelpRequestDecisionEventArgs>? HelpRequestDecisionReceived;

    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;
    public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
    public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
    public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
    public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
    public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
    public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
    public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
    public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
    public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
    public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    public event EventHandler? ScreenShareStopped;
    public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
    public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
    public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
    public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;
    public event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;
    public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
    public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
    public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
    public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
    public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;
    public event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived;
    public event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived;
    public event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived;
    public event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived;
    public event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived;

    public bool LocalSupportsRemoteControl => LocalRemoteControlSupported;
    public string LocalPeerAddress => localPeerAddress;
    public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
    public bool RemoteSupportsRemoteControl => remoteSupportsRemoteControl;
    public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;
    public bool LocalSupportsScreenShareCursorOverlay => LocalScreenShareCursorOverlaySupported;
    public bool RemoteSupportsScreenShareCursorOverlay => remoteSupportsScreenShareCursorOverlay;
    public bool SessionSupportsScreenShareCursorOverlay => LocalSupportsScreenShareCursorOverlay && RemoteSupportsScreenShareCursorOverlay;

    public DevLocalImpairmentMetricsSnapshot GetImpairmentMetricsSnapshot()
        => impairmentPolicy?.GetSnapshot() ??
           new DevLocalImpairmentMetricsSnapshot(
               DevLocalImpairmentProfile.None,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0,
               0);

    public void Dispose()
    {
        disposed = true;
        TryCancelHostReady();
        ClearActiveConnection()?.Dispose();
        screenShareFrameReassembler.FrameReady -= OnScreenShareFrameReady;
        screenShareFrameReassembler.KeyframeRequested -= OnScreenShareKeyframeRequested;
        screenShareFrameReassembler.ClearAll();
    }

    public Task WaitUntilHostReadyAsync(CancellationToken ct)
    {
        Task readyTask;
        lock (hostReadyGate)
        {
            readyTask = hostReadyTcs.Task;
        }

        return readyTask.WaitAsync(ct);
    }

    public async Task HostByAddressAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ResetHostReady();
        remoteSupportsRemoteControl = false;
        remoteSupportsScreenShareCursorOverlay = false;
        UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(localPeerAddress)));
        var cancelRegistration = ct.Register(() => ClearActiveConnection()?.Dispose());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (disposed)
                {
                    break;
                }

                try
                {
                    await HandleSingleHostConnectionAsync(localPeerAddress, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LocalOperationalLog.Error(
                        "DevLocalTransport",
                        $"event=host_connection_failed; peer={localPeerAddress}; ex={ex.GetType().Name}; message={ex.Message}");
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "host_connection_exception"));
                    OnDisconnected();

                    try
                    {
                        await Task.Delay(150, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            TryCancelHostReady();
            cancelRegistration.Dispose();
        }
    }

    public async Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
    {
        var pendingHandshake = new PendingOutboundHandshakeState(
            SessionHandshakeProtocol.CreateSessionId(),
            new PeerAddress(localPeerAddress),
            new PeerAddress(peerAddress.Trim()),
            InviteValidated: false,
            RequestedCapabilities: CapabilityGrant.None,
            InviteToken: null);
        await JoinCoreAsync(peerAddress, pendingHandshake, ct).ConfigureAwait(false);
    }

    public async Task SendHelpRequestAsync(HelpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var client = new NamedPipeClientStream(".", BuildPipeName(request.HelperAddress.Value), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        var connection = new SessionConnection(client);
        await connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = HelpRequestFrameType,
                RequestId = request.RequestId,
                HelpeeAddress = request.HelpeeAddress.Value,
                HelperAddress = request.HelperAddress.Value,
                InviteToken = request.InviteToken,
            },
            ct).ConfigureAwait(false);
    }

    public async Task SendHelpRequestDecisionAsync(HelpRequestDecisionMessage decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);

        using var client = new NamedPipeClientStream(".", BuildPipeName(decision.HelpeeAddress.Value), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        var connection = new SessionConnection(client);
        await connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = HelpRequestDecisionFrameType,
                RequestId = decision.RequestId,
                HelpeeAddress = decision.HelpeeAddress.Value,
                HelperAddress = decision.HelperAddress.Value,
                Accepted = decision.Accepted,
                Reason = decision.Reason,
            },
            ct).ConfigureAwait(false);
    }

    public async Task SendHelpRequestCancellationAsync(HelpRequestMessage request, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var client = new NamedPipeClientStream(".", BuildPipeName(request.HelperAddress.Value), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        var connection = new SessionConnection(client);
        await connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = HelpRequestDecisionFrameType,
                RequestId = request.RequestId,
                HelpeeAddress = request.HelpeeAddress.Value,
                HelperAddress = request.HelperAddress.Value,
                Accepted = false,
                Reason = string.IsNullOrWhiteSpace(reason) ? "request_canceled" : reason.Trim(),
            },
            ct).ConfigureAwait(false);
    }

    public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
        {
            throw new ArgumentException("Invite token is required.", nameof(inviteToken));
        }

        ArgumentNullException.ThrowIfNull(invite);
        var normalizedInviteToken = inviteToken.Trim();
        var validation = inviteTokenValidator.Validate(normalizedInviteToken, DateTimeOffset.UtcNow, InviteValidationMode.InspectOnly);
        if (!validation.IsSuccess || validation.Invite is null)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, validation.Result.ToFailureCode()));
            throw new InvalidOperationException(validation.Message ?? "Invite token is invalid.");
        }

        if (validation.Invite.SessionId != invite.SessionId ||
            validation.Invite.TargetAddress != invite.TargetAddress ||
            validation.Invite.IssuerAddress != invite.IssuerAddress)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_binding_mismatch"));
            throw new InvalidOperationException("Invite token does not match the provided invite context.");
        }

        var helperAddress = new PeerAddress(localPeerAddress);
        if (InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites() &&
            validation.Invite.BoundHelperAddress is null)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_required"));
            throw new InvalidOperationException("Invite token must be bound to the verified helper identity.");
        }

        if (validation.Invite.BoundHelperAddress is not null &&
            validation.Invite.BoundHelperAddress != helperAddress)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_mismatch"));
            throw new InvalidOperationException("Invite token is bound to a different helper identity.");
        }

        var pendingHandshake = new PendingOutboundHandshakeState(
            invite.SessionId,
            helperAddress,
            invite.TargetAddress,
            InviteValidated: true,
            RequestedCapabilities: invite.Payload.Capabilities.ToCapabilityGrant(),
            InviteToken: normalizedInviteToken);
        return JoinCoreAsync(invite.TargetAddress.Value, pendingHandshake, ct);
    }

    private async Task JoinCoreAsync(string peerAddress, PendingOutboundHandshakeState outboundHandshake, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(peerAddress))
        {
            throw new ArgumentException("Peer address is required.", nameof(peerAddress));
        }

        remoteSupportsRemoteControl = false;
        remoteSupportsScreenShareCursorOverlay = false;
        pendingOutboundHandshake = outboundHandshake;
        UpdateSessionSecurityState(SessionSecurityState.CreateHelperPending(
            outboundHandshake.SessionId,
            outboundHandshake.HelpeeAddress,
            outboundHandshake.HelperAddress,
            outboundHandshake.InviteValidated));
        var cancelRegistration = ct.Register(() => ClearActiveConnection()?.Dispose());
        SessionConnection? connection = null;
        var joinStep = "connect";

        try
        {
            var client = new NamedPipeClientStream(
                ".",
                BuildPipeName(peerAddress.Trim()),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeoutMs, ct);
            connection = new SessionConnection(client);
            ReplaceActiveConnection(connection);

            using var helperKeyPair = ChatKeyAgreement.CreateKeyPair();
            var helloReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var readLoopTask = RunJoinReadLoopAsync(connection, helperKeyPair, helloReceived, ct);
            connection.SetReadLoop(readLoopTask);

            joinStep = "send_join";
            await connection.WriteFrameAsync(
                new TransportFrame
                {
                    Type = JoinFrameType,
                    Data = Convert.ToBase64String(helperKeyPair.PublicKey),
                    HelperAddress = localPeerAddress,
                    RemoteControlSupported = LocalSupportsRemoteControl,
                    ScreenShareCursorOverlaySupported = LocalSupportsScreenShareCursorOverlay,
                },
                ct);

            joinStep = "wait_hello";
            await helloReceived.Task.WaitAsync(ct);
            joinStep = "send_handshake_start";
            await SendHandshakeStartAsync(connection, outboundHandshake, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Invalidated, "transport_canceled"));
            OnDisconnected();
        }
        catch (TimeoutException ex)
        {
            LocalOperationalLog.Error(
                "DevLocalTransport",
                $"event=join_failed; peer={peerAddress.Trim()}; step={joinStep}; ex={ex.GetType().Name}; message={ex.Message}");
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "join_timeout"));
            OnDisconnected();
        }
        catch (IOException ex)
        {
            LocalOperationalLog.Error(
                "DevLocalTransport",
                $"event=join_failed; peer={peerAddress.Trim()}; step={joinStep}; ex={ex.GetType().Name}; message={ex.Message}");
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "join_io_failed"));
            OnDisconnected();
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Error(
                "DevLocalTransport",
                $"event=join_failed; peer={peerAddress.Trim()}; step={joinStep}; ex={ex.GetType().Name}; message={ex.Message}");
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "join_exception"));
            OnDisconnected();
        }
        finally
        {
            cancelRegistration.Dispose();
        }
    }

    public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        return connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = ChatFrameType,
                Data = Convert.ToBase64String(CreateSecureChatPayload(payload.Span.ToArray())),
            },
            ct);
    }

    public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlRequestFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlResponseFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlStartFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlStopFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlInputFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlAckFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlStateSnapshotFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(ControlDisplayInfoFrameType, RemoteControlPayloadCodec.Serialize(EnsureControlSessionId(message)), ct);
    }

    public Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(
            ScreenSharePressureStateFrameType,
            ScreenSharePressureStateCodec.Serialize(EnsureScreenSharePressureStateSessionId(message)),
            ct);
    }

    public Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(
            ScreenShareVideoStreamConfigFrameType,
            ScreenShareVideoPayloadCodec.SerializeStreamConfig(message with
            {
                SessionId = ResolveControlSessionId(message.SessionId),
            }),
            ct);
    }

    public Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(
            ScreenShareVideoKeyframeRequestFrameType,
            ScreenShareVideoKeyframeRequestCodec.Serialize(message with
            {
                SessionId = ResolveControlSessionId(message.SessionId),
            }),
            ct);
    }

    public Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendControlFrameAsync(
            ScreenShareRecoveryReceiptFrameType,
            ScreenShareRecoveryReceiptCodec.Serialize(EnsureScreenShareRecoveryReceiptSessionId(message)),
            ct);
    }

    public Task SendScreenShareCursorStateAsync(ScreenShareCursorStateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!SessionSupportsScreenShareCursorOverlay)
        {
            return Task.CompletedTask;
        }

        return SendControlFrameAsync(
            ScreenShareCursorStateFrameType,
            ScreenShareCursorStateCodec.Serialize(EnsureScreenShareCursorStateSessionId(message)),
            ct);
    }

    public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        var rawPayload = payload.Span.ToArray();
        async Task WritePayloadAsync()
        {
            await connection.WriteFrameAsync(
                new TransportFrame
                {
                    Type = ScreenSharePayloadFrameType,
                    Data = Convert.ToBase64String(CreateSecureScreenSharePayload(rawPayload)),
                },
                ct).ConfigureAwait(false);
        }

        if (impairmentPolicy is null)
        {
            return WritePayloadAsync();
        }

        return SendImpairedScreenSharePayloadAsync(impairmentPolicy, payload.Length, WritePayloadAsync, ct);
    }

    public Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferOfferFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferAcceptFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferDeclineFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferSessionOpenTrackedAsync(normalizedMessage, ct);
    }

    private async Task SendFileTransferSessionOpenTrackedAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
    {
        if (!TryValidateAndTrackFileTransferMessage(FileTransferSessionOpenFrameType, message.TransferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            throw new InvalidOperationException($"File-transfer state rejected frame '{FileTransferSessionOpenFrameType}': {failureReason}.");
        }

        await SendFileTransferRawFrameAsync(FileTransferSessionOpenFrameType, FileTransferPayloadCodec.Serialize(message), message.TransferId, ct)
            .ConfigureAwait(false);

        if (!TryValidateAndTrackFileTransferMessage(FileTransferSessionOpenFrameType, message.TransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={FileTransferSessionOpenFrameType}; transfer_id={message.TransferId}; source=devlocal-local");
        }
    }

    public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferCancelFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferErrorFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferCompleteFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferPauseControlFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferHeartbeatFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferTransportEpochFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferTransportProbeFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalizedMessage = EnsureFileTransferSessionId(message);
        return SendFileTransferFrameAsync(FileTransferRepairProofFrameType, FileTransferPayloadCodec.Serialize(normalizedMessage), normalizedMessage.TransferId, ct);
    }

    public Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session id is required.", nameof(sessionId))
            : sessionId.Trim();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        if (!fileTransferDataSessions.TryGetValue(normalizedTransferId, out var session))
        {
            session = new TransportFileTransferDataSession(this, normalizedSessionId, normalizedTransferId);
            fileTransferDataSessions[normalizedTransferId] = session;
        }
        else if (!string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("File-transfer data session id mismatch for existing transfer.");
        }

        return Task.FromResult<IFileTransferDataSession>(session);
    }

    private Task SendControlFrameAsync(string frameType, byte[] payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        return connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = frameType,
                Data = Convert.ToBase64String(CreateSecureControlPayload(frameType, ResolveRequestId(frameType, payload), payload)),
            },
            ct);
    }

    private async Task SendFileTransferFrameAsync(string frameType, byte[] payload, string transferId, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        if (!TryValidateAndTrackFileTransferMessage(frameType, transferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            throw new InvalidOperationException($"File-transfer state rejected frame '{frameType}': {failureReason}.");
        }

        await connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = frameType,
                Data = Convert.ToBase64String(CreateSecureFileTransferPayload(frameType, transferId, payload)),
            },
            ct).ConfigureAwait(false);

        LogFileTransferFrameEvent("sent", frameType, transferId);

        if (!TryValidateAndTrackFileTransferMessage(frameType, transferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={frameType}; transfer_id={transferId}; source=devlocal-local");
        }
    }

    private async Task SendFileTransferRawFrameAsync(string frameType, byte[] payload, string transferId, CancellationToken ct)
    {
        ThrowIfDisposed();

        var connection = GetActiveConnection();
        if (connection is null || !connection.IsConnected)
        {
            throw new InvalidOperationException("No active session connection.");
        }

        await connection.WriteFrameAsync(
            new TransportFrame
            {
                Type = frameType,
                Data = Convert.ToBase64String(CreateSecureFileTransferPayload(frameType, transferId, payload)),
            },
            ct).ConfigureAwait(false);

        LogFileTransferFrameEvent("sent", frameType, transferId);
    }

    private async Task SendFileTransferDataFrameAsync(FileTransferDataFrame frame, string transferId, CancellationToken ct)
    {
        if (impairmentPolicy is not null)
        {
            var decision = impairmentPolicy.ObserveFileTransferDataFrame(frame, transferId);
            if (decision.Drop)
            {
                return;
            }

            if (decision.Delay > TimeSpan.Zero)
            {
                await Task.Delay(decision.Delay, ct).ConfigureAwait(false);
            }
        }

        await SendFileTransferRawFrameAsync(
                FileTransferDataFrameType,
                FileTransferProtocol.IsV4DataFrame(frame)
                    ? FileTransferDataFrameCodec.SerializeLegacyV4(frame)
                    : FileTransferDataFrameCodec.Serialize(frame),
                transferId,
                ct)
            .ConfigureAwait(false);

        if (!TryValidateAndTrackFileTransferDataFrame(frame, inbound: false, applyStateChange: true, out var failureReason))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_data_frame_state_race; transport=devlocal; frame_type={frame.Type}; reason={failureReason}; transfer_id={frame.TransferId}; session_id={frame.SessionId}; source=devlocal-local");
        }
    }

    private static async Task SendImpairedScreenSharePayloadAsync(
        DevLocalImpairmentPolicy impairmentPolicy,
        int payloadBytes,
        Func<Task> writePayloadAsync,
        CancellationToken ct)
    {
        var decision = impairmentPolicy.ObserveScreenShareMediaPayload(payloadBytes);
        if (decision.Drop)
        {
            return;
        }

        if (decision.Delay > TimeSpan.Zero)
        {
            await Task.Delay(decision.Delay, ct).ConfigureAwait(false);
        }

        await writePayloadAsync().ConfigureAwait(false);
    }

    private async Task HandleSingleHostConnectionAsync(string peerAddress, CancellationToken ct)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(
                BuildPipeName(peerAddress),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            TrySetHostReady();
            await server.WaitForConnectionAsync(ct);
            var connection = new SessionConnection(server);
            server = null; // ownership transferred to SessionConnection
            ReplaceActiveConnection(connection);

            try
            {
                var joinFrame = await connection.ReadFrameAsync(ct);
                if (joinFrame is null)
                {
                    await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
                    return;
                }

                if (string.Equals(joinFrame.Type, HelpRequestFrameType, StringComparison.Ordinal))
                {
                    HandleIncomingHelpRequestFrame(joinFrame);
                    return;
                }

                if (string.Equals(joinFrame.Type, HelpRequestDecisionFrameType, StringComparison.Ordinal))
                {
                    HandleIncomingHelpRequestDecisionFrame(joinFrame);
                    return;
                }

                if (!string.Equals(joinFrame.Type, JoinFrameType, StringComparison.Ordinal))
                {
                    await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
                    return;
                }

                byte[] helperPublicKey;
                try
                {
                    helperPublicKey = Convert.FromBase64String(joinFrame.Data ?? string.Empty);
                }
                catch (FormatException)
                {
                    await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
                    return;
                }

                remoteSupportsRemoteControl = joinFrame.RemoteControlSupported == true;
                remoteSupportsScreenShareCursorOverlay = joinFrame.ScreenShareCursorOverlaySupported == true;

                using var hostKeyPair = ChatKeyAgreement.CreateKeyPair();
                var sharedKey = hostKeyPair.DeriveSharedKey(helperPublicKey);
                PeerAddress? helperAddress = string.IsNullOrWhiteSpace(joinFrame.HelperAddress)
                    ? null
                    : new PeerAddress(joinFrame.HelperAddress.Trim());

                await connection.WriteFrameAsync(
                    new TransportFrame
                    {
                        Type = HelloFrameType,
                        Data = Convert.ToBase64String(hostKeyPair.PublicKey),
                        RemoteControlSupported = LocalSupportsRemoteControl,
                        ScreenShareCursorOverlaySupported = LocalSupportsScreenShareCursorOverlay,
                    },
                    ct);

                var handshake = await CompleteHostHandshakeAsync(
                    connection,
                    sharedKey,
                    hostKeyPair.PublicKey,
                    helperAddress,
                    ct).ConfigureAwait(false);
                if (!handshake.IsVerified)
                {
                    return;
                }

                var approvalRequest = handshake.InviteValidated &&
                                      handshake.HelperAddress is PeerAddress helperIdentity &&
                                      handshake.SessionId is SessionId approvedSessionId &&
                                      handshake.RequestedCapabilities != CapabilityGrant.None
                    ? new ApprovalRequest(
                        helperIdentity,
                        handshake.RequestedCapabilities,
                        approvedSessionId)
                    : null;

                var joinRequestArgs = new IncomingJoinRequestEventArgs(
                    approveAsync: (decision, token) => ApproveHostJoinAsync(connection, sharedKey, approvalRequest, decision, token),
                    rejectAsync: (reason, token) => RejectHostJoinAsync(connection, sharedKey, approvalRequest, reason, token),
                    approvalRequest: approvalRequest);

                var handler = IncomingJoinRequest;
                if (handler is null)
                {
                    await joinRequestArgs.RejectAsync(ct);
                }
                else
                {
                    try
                    {
                        handler(this, joinRequestArgs);
                    }
                    catch
                    {
                        await joinRequestArgs.RejectAsync(ct);
                    }
                }

                await RunHostReadLoopAsync(connection, ct);
            }
            finally
            {
                if (ReferenceEquals(GetActiveConnection(), connection))
                {
                    ClearActiveConnection();
                }

                connection.Dispose();
            }
        }
        finally
        {
            if (server is not null)
            {
                try { server.Dispose(); } catch { }
            }
        }
    }

    private void HandleIncomingHelpRequestFrame(TransportFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.RequestId) ||
            string.IsNullOrWhiteSpace(frame.HelpeeAddress) ||
            string.IsNullOrWhiteSpace(frame.HelperAddress) ||
            string.IsNullOrWhiteSpace(frame.InviteToken) ||
            !PeerAddress.TryParse(frame.HelpeeAddress, out var helpeeAddress) ||
            !PeerAddress.TryParse(frame.HelperAddress, out var helperAddress))
        {
            return;
        }

        var validation = inviteTokenValidator.Validate(frame.InviteToken, DateTimeOffset.UtcNow, InviteValidationMode.InspectOnly);
        if (!validation.IsSuccess || validation.Invite is null)
        {
            return;
        }

        if (validation.Invite.BoundHelperAddress is not null &&
            validation.Invite.BoundHelperAddress != helperAddress)
        {
            return;
        }

        IncomingHelpRequest?.Invoke(
            this,
            new IncomingHelpRequestEventArgs(
                new HelpRequestMessage(
                    frame.RequestId,
                    helpeeAddress,
                    helperAddress,
                    frame.InviteToken)));
    }

    private void HandleIncomingHelpRequestDecisionFrame(TransportFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.RequestId) ||
            string.IsNullOrWhiteSpace(frame.HelpeeAddress) ||
            string.IsNullOrWhiteSpace(frame.HelperAddress) ||
            !PeerAddress.TryParse(frame.HelpeeAddress, out var helpeeAddress) ||
            !PeerAddress.TryParse(frame.HelperAddress, out var helperAddress))
        {
            return;
        }

        HelpRequestDecisionReceived?.Invoke(
            this,
            new HelpRequestDecisionEventArgs(
                new HelpRequestDecisionMessage(
                    frame.RequestId,
                    helpeeAddress,
                    helperAddress,
                    frame.Accepted == true,
                    frame.Reason)));
    }

    private async Task RunJoinReadLoopAsync(
        SessionConnection connection,
        ChatKeyPair helperKeyPair,
        TaskCompletionSource helloReceived,
        CancellationToken ct)
    {
        var rejected = false;
        byte[]? pendingSessionKey = null;
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.ReadFrameAsync(ct);
                if (frame is null)
                {
                    break;
                }

                if (string.Equals(frame.Type, HelloFrameType, StringComparison.Ordinal))
                {
                    if (!helloReceived.Task.IsCompleted)
                    {
                        remoteSupportsRemoteControl = frame.RemoteControlSupported == true;
                        remoteSupportsScreenShareCursorOverlay = frame.ScreenShareCursorOverlaySupported == true;
                        var remotePublicKey = Convert.FromBase64String(frame.Data ?? string.Empty);
                        pendingSessionKey = helperKeyPair.DeriveSharedKey(remotePublicKey);
                        helloReceived.TrySetResult();
                    }

                    continue;
                }

                if (string.Equals(frame.Type, SessionHandshakeChallengeFrameType, StringComparison.Ordinal))
                {
                    if (pendingSessionKey is null ||
                        !TryGetPayloadBytes(frame, out var payloadBytes) ||
                        !TryParseHandshakeChallengePayload(payloadBytes, out var challenge))
                    {
                        AbortOutboundHandshake("handshake_challenge_invalid");
                        connection.Dispose();
                        break;
                    }

                    if (pendingOutboundHandshake is null ||
                        challenge.SessionId != pendingOutboundHandshake.SessionId ||
                        challenge.HelpeeAddress != pendingOutboundHandshake.HelpeeAddress ||
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= challenge.ExpiresAtUtcMs)
                    {
                        AbortOutboundHandshake("handshake_challenge_binding_mismatch");
                        connection.Dispose();
                        break;
                    }

                    var expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUtcMs);
                    pendingOutboundHandshake = pendingOutboundHandshake.WithChallenge(challenge.ChallengeNonce, expiresAtUtc);
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeChallenge(
                        challenge.SessionId,
                        challenge.HelpeeAddress,
                        pendingOutboundHandshake.HelperAddress,
                        pendingOutboundHandshake.InviteValidated,
                        expiresAtUtc));

                    var mac = SessionHandshakeProtocol.ComputeResponseMac(
                        pendingSessionKey,
                        challenge.SessionId,
                        pendingOutboundHandshake.HelperAddress,
                        challenge.HelpeeAddress,
                        challenge.ChallengeNonce);
                    var response = new SessionHandshakeResponse(
                        challenge.SessionId,
                        pendingOutboundHandshake.HelperAddress,
                        challenge.ChallengeNonce,
                        Convert.ToBase64String(mac));
                    await connection.WriteFrameAsync(
                        new TransportFrame
                        {
                            Type = SessionHandshakeResponseFrameType,
                            Data = Convert.ToBase64String(SessionHandshakeProtocol.Serialize(response)),
                        },
                        ct);
                    continue;
                }

                if (string.Equals(frame.Type, SessionHandshakeResultFrameType, StringComparison.Ordinal))
                {
                    if (!TryGetPayloadBytes(frame, out var payloadBytes) ||
                        !TryParseHandshakeResultPayload(payloadBytes, out var result) ||
                        pendingOutboundHandshake is null ||
                        result.SessionId != pendingOutboundHandshake.SessionId)
                    {
                        AbortOutboundHandshake("handshake_result_invalid");
                        connection.Dispose();
                        break;
                    }

                    if (!result.Verified)
                    {
                        AbortOutboundHandshake(result.FailureReason ?? "handshake_rejected");
                        connection.Dispose();
                        break;
                    }

                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeVerified(pendingOutboundHandshake.HelperAddress));
                    continue;
                }

                if (string.Equals(frame.Type, ApproveFrameType, StringComparison.Ordinal))
                {
                    if (!TryGetPayloadBytes(frame, out var payloadBytes) ||
                        pendingSessionKey is null ||
                        !TryDecryptLifecyclePayload(payloadBytes, pendingSessionKey, ApproveFrameType, pendingOutboundHandshake?.HelpeeAddress, out var securePayload) ||
                        !SessionHandshakeProtocol.TryDeserializeApprovalDecision(securePayload.Plaintext, out var decision) ||
                        pendingOutboundHandshake is null ||
                        decision.SessionId != pendingOutboundHandshake.SessionId ||
                        decision.HelperIdentity != pendingOutboundHandshake.HelperAddress ||
                        (decision.ApprovedCapabilities & ~pendingOutboundHandshake.RequestedCapabilities) != 0 ||
                        decision.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    {
                        AbortOutboundHandshake("approve_payload_invalid");
                        connection.Dispose();
                        break;
                    }

                    UpdateSessionSecurityState(currentSessionSecurityState.WithApproval(decision.ToGrant()));
                    if (pendingSessionKey is not null)
                    {
                        SetControlSessionSharedKey(pendingSessionKey);
                        SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(pendingSessionKey));
                    }

                    pendingOutboundHandshake = null;
                    SafeRaiseApproved();
                    continue;
                }

                if (string.Equals(frame.Type, RejectFrameType, StringComparison.Ordinal))
                {
                    if (!TryGetPayloadBytes(frame, out var rejectPayloadBytes) ||
                        pendingSessionKey is null ||
                        !TryDecryptLifecyclePayload(rejectPayloadBytes, pendingSessionKey, RejectFrameType, pendingOutboundHandshake?.HelpeeAddress, out var rejectPayload))
                    {
                        AbortOutboundHandshake("reject_payload_invalid");
                        connection.Dispose();
                        break;
                    }

                    var rejectionReason = TryParseRejectReason(rejectPayload.Plaintext, out var parsedRejectReason)
                        ? parsedRejectReason
                        : "join_rejected";
                    rejected = true;
                    pendingOutboundHandshake = null;
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Invalidated, rejectionReason));
                    SafeRaiseRejected();
                    connection.Dispose();
                    break;
                }

                if (string.Equals(frame.Type, ChatFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptChatPayload(payloadBytes, out var plaintext))
                    {
                        SafeRaiseChatMessageReceived(plaintext);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePayloadFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptScreenSharePayload(payloadBytes, out var plaintext))
                    {
                        HandleScreenSharePayload(plaintext);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareVideoStreamConfigFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareVideoStreamConfigFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        ScreenShareVideoPayloadCodec.TryDeserializeStreamConfig(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareVideoStreamConfigFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("stream_config", message.SessionId))
                    {
                        HandleScreenShareVideoStreamConfig(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareVideoKeyframeRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareVideoKeyframeRequestFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        ScreenShareVideoKeyframeRequestCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareVideoKeyframeRequestFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("keyframe_request", message.SessionId))
                    {
                        SafeRaiseScreenShareVideoKeyframeRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareRecoveryReceiptFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareRecoveryReceiptFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        ScreenShareRecoveryReceiptCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareRecoveryReceiptFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("recovery_receipt", message.SessionId))
                    {
                        SafeRaiseScreenShareRecoveryReceiptReceived(message);
                    }

                    continue;
                }

                if (TryHandleFileTransferFrame(frame))
                {
                    continue;
                }

                if (string.Equals(frame.Type, ControlRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlRequestFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlRequest(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlRequestFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_request", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlResponseFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlResponseFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlResponse(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlResponseFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_response", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlResponseReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStartFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStartFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStart(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStartFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_start", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStartReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStopFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStopFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStop(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStopFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_stop", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStopReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlInputFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlInputFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlInput(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlInputFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_input", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlInputReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlAckFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlAckFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlAck(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlAckFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_ack", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlAckReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStateSnapshotFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStateSnapshotFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStateSnapshotFrameType, securePayload.Metadata, message.RequestId) &&
                        TryValidateControlMessageSession("control_state_snapshot", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStateSnapshotReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlDisplayInfoFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlDisplayInfoFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlDisplayInfoFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateControlMessageSession("control_display_info", message.SessionId, requestId: null))
                    {
                        SafeRaiseRemoteControlDisplayInfoReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePressureStateFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenSharePressureStateFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        ScreenSharePressureStateCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenSharePressureStateFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateControlMessageSession("screenshare_pressure_state", message.SessionId, requestId: null))
                    {
                        SafeRaiseScreenSharePressureStateReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareCursorStateFrameType, StringComparison.Ordinal))
                {
                    if (SessionSupportsScreenShareCursorOverlay &&
                        TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareCursorStateFrameType, ResolveExpectedRemotePeerAddressForCurrentSession(), out var securePayload) &&
                        ScreenShareCursorStateCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareCursorStateFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateControlMessageSession("screenshare_cursor_state", message.SessionId, requestId: null))
                    {
                        SafeRaiseScreenShareCursorStateReceived(message);
                    }

                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            if (!helloReceived.Task.IsCompleted)
            {
                helloReceived.TrySetCanceled();
            }

            if (!rejected)
            {
                OnDisconnected();
            }
            if (ReferenceEquals(GetActiveConnection(), connection))
            {
                ClearActiveConnection();
            }

            connection.Dispose();
        }
    }

    private async Task<InboundHandshakeResult> CompleteHostHandshakeAsync(
        SessionConnection connection,
        byte[] sharedKey,
        byte[] hostPublicKey,
        PeerAddress? helperAddress,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && connection.IsConnected)
        {
            var frame = await connection.ReadFrameAsync(ct).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            if (!string.Equals(frame.Type, SessionHandshakeStartFrameType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetPayloadBytes(frame, out var payloadBytes) ||
                !TryParseHandshakeStartPayload(payloadBytes, out var start))
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_start_invalid"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(SessionHandshakeProtocol.CreateSessionId(), Verified: false, FailureReason: "handshake_start_invalid"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
            }

            if (helperAddress is not null && start.HelperAddress != helperAddress)
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_helper_mismatch"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_helper_mismatch"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
            }

            var inviteValidated = false;
            var requestedCapabilities = CapabilityGrant.None;
            var helpeeAddress = new PeerAddress(localPeerAddress);
            if (!string.IsNullOrWhiteSpace(start.InviteToken))
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var validationScopeKey = PersistentInviteSecurityStore.BuildValidationScopeKey(helpeeAddress, start.HelperAddress.Value);
                if (!inviteValidationThrottle.TryAcquire(validationScopeKey, nowUtc, out var retryAfter))
                {
                    LocalOperationalLog.Warn(
                        "InviteValidation",
                        $"result=Throttled; mode={InviteValidationMode.ConsumeIfValid}; session_id={start.SessionId.Value}; target={helpeeAddress.Value}; helper={start.HelperAddress.Value}; retry_after_ms={(long)Math.Ceiling(retryAfter.TotalMilliseconds)}");
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_validation_throttled"));
                    await connection.WriteFrameAsync(
                        CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "invite_validation_throttled"))),
                        ct).ConfigureAwait(false);
                    return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
                }

                var validation = inviteTokenValidator.Validate(start.InviteToken, nowUtc, InviteValidationMode.ConsumeIfValid);
                if (!validation.IsSuccess || validation.Invite is null)
                {
                    var failureReason = validation.Result.ToFailureCode();
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, failureReason));
                    await connection.WriteFrameAsync(
                        CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: failureReason))),
                        ct).ConfigureAwait(false);
                    return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
                }

                if (validation.Invite.TargetAddress != helpeeAddress ||
                    validation.Invite.SessionId != start.SessionId)
                {
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_binding_mismatch"));
                    await connection.WriteFrameAsync(
                        CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "invite_binding_mismatch"))),
                        ct).ConfigureAwait(false);
                    return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
                }

                if (InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites() &&
                    validation.Invite.BoundHelperAddress is null)
                {
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_required"));
                    await connection.WriteFrameAsync(
                        CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "invite_helper_required"))),
                        ct).ConfigureAwait(false);
                    return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
                }

                if (validation.Invite.BoundHelperAddress is not null &&
                    validation.Invite.BoundHelperAddress != start.HelperAddress)
                {
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_mismatch"));
                    await connection.WriteFrameAsync(
                        CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "invite_helper_mismatch"))),
                        ct).ConfigureAwait(false);
                    return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
                }

                inviteValidated = true;
                requestedCapabilities = validation.Invite.Payload.Capabilities.ToCapabilityGrant();
            }

            var expiresAtUtc = DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.HandshakeTimeout);
        var challenge = new SessionHandshakeChallenge(
            start.SessionId,
            helpeeAddress,
            SessionHandshakeProtocol.CreateChallengeNonce(),
            expiresAtUtc.ToUnixTimeMilliseconds(),
            Convert.ToBase64String(hostPublicKey));

        if (!handshakeReplayCache.TryTrackChallenge(
                start.SessionId,
                start.HelperAddress,
                helpeeAddress,
                challenge.ChallengeNonce,
                expiresAtUtc,
                DateTimeOffset.UtcNow))
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_challenge_replay_detected"));
            await connection.WriteFrameAsync(
                CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_challenge_replay_detected"))),
                ct).ConfigureAwait(false);
            return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
        }

        UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(helpeeAddress).WithHandshakeChallenge(
            start.SessionId,
            helpeeAddress,
            start.HelperAddress,
            inviteValidated,
                expiresAtUtc));

            await connection.WriteFrameAsync(
                CreateHandshakeFrame(SessionHandshakeChallengeFrameType, SessionHandshakeProtocol.Serialize(challenge)),
                ct).ConfigureAwait(false);

            var responseFrame = await connection.ReadFrameAsync(ct).ConfigureAwait(false);
            if (responseFrame is null ||
                !string.Equals(responseFrame.Type, SessionHandshakeResponseFrameType, StringComparison.Ordinal) ||
                !TryGetPayloadBytes(responseFrame, out var responsePayloadBytes) ||
                !TryParseHandshakeResponsePayload(responsePayloadBytes, out var response))
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_response_invalid"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_response_invalid"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
            }

            if (DateTimeOffset.UtcNow >= expiresAtUtc ||
                response.SessionId != start.SessionId ||
                response.HelperAddress != start.HelperAddress ||
                !string.Equals(response.ChallengeNonce, challenge.ChallengeNonce, StringComparison.Ordinal))
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Expired, "handshake_response_binding_mismatch"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_response_binding_mismatch"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
            }

            byte[] candidateMac;
            try
            {
                candidateMac = Convert.FromBase64String(response.MacBase64);
            }
            catch (FormatException)
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_response_mac_invalid"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_response_mac_invalid"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
            }

            if (!SessionHandshakeProtocol.VerifyResponseMac(sharedKey, start.SessionId, start.HelperAddress, helpeeAddress, challenge.ChallengeNonce, candidateMac))
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_response_mac_mismatch"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_response_mac_mismatch"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
            }

            if (!handshakeReplayCache.TryConsumeChallenge(
                    start.SessionId,
                    start.HelperAddress,
                    helpeeAddress,
                    challenge.ChallengeNonce,
                    DateTimeOffset.UtcNow))
            {
                UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "handshake_response_replay_detected"));
                await connection.WriteFrameAsync(
                    CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: false, FailureReason: "handshake_response_replay_detected"))),
                    ct).ConfigureAwait(false);
                return new InboundHandshakeResult(false, null, inviteValidated, start.SessionId, requestedCapabilities);
            }

            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeVerified(start.HelperAddress));
            await connection.WriteFrameAsync(
                CreateHandshakeFrame(SessionHandshakeResultFrameType, SessionHandshakeProtocol.Serialize(new SessionHandshakeResult(start.SessionId, Verified: true, FailureReason: null))),
                ct).ConfigureAwait(false);
            return new InboundHandshakeResult(true, start.HelperAddress, inviteValidated, start.SessionId, requestedCapabilities);
        }

        return new InboundHandshakeResult(false, null, false, null, CapabilityGrant.None);
    }

    private async Task RunHostReadLoopAsync(SessionConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.ReadFrameAsync(ct);
                if (frame is null)
                {
                    break;
                }

                if (string.Equals(frame.Type, ChatFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptChatPayload(payloadBytes, out var plaintext))
                    {
                        SafeRaiseChatMessageReceived(plaintext);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePayloadFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptScreenSharePayload(payloadBytes, out var plaintext))
                    {
                        HandleScreenSharePayload(plaintext);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareVideoStreamConfigFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareVideoStreamConfigFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenShareVideoPayloadCodec.TryDeserializeStreamConfig(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareVideoStreamConfigFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("stream_config", message.SessionId))
                    {
                        HandleScreenShareVideoStreamConfig(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareVideoKeyframeRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareVideoKeyframeRequestFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenShareVideoKeyframeRequestCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareVideoKeyframeRequestFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("keyframe_request", message.SessionId))
                    {
                        SafeRaiseScreenShareVideoKeyframeRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareRecoveryReceiptFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareRecoveryReceiptFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenShareRecoveryReceiptCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareRecoveryReceiptFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("recovery_receipt", message.SessionId))
                    {
                        SafeRaiseScreenShareRecoveryReceiptReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareCursorStateFrameType, StringComparison.Ordinal))
                {
                    if (SessionSupportsScreenShareCursorOverlay &&
                        TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareCursorStateFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenShareCursorStateCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareCursorStateFrameType, securePayload.Metadata, requestId: null) &&
                        TryValidateScreenShareMessageSession("cursor_state", message.SessionId))
                    {
                        SafeRaiseScreenShareCursorStateReceived(message);
                    }

                    continue;
                }

                if (TryHandleFileTransferFrame(frame))
                {
                    continue;
                }

                if (string.Equals(frame.Type, ControlRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlRequestFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlRequest(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlRequestFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlResponseFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlResponseFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlResponse(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlResponseFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlResponseReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStartFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStartFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStart(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStartFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlStartReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStopFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStopFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStop(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStopFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlStopReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlInputFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlInputFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlInput(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlInputFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlInputReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlAckFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlAckFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlAck(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlAckFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlAckReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStateSnapshotFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlStateSnapshotFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlStateSnapshotFrameType, securePayload.Metadata, message.RequestId))
                    {
                        SafeRaiseRemoteControlStateSnapshotReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlDisplayInfoFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ControlDisplayInfoFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ControlDisplayInfoFrameType, securePayload.Metadata, requestId: null))
                    {
                        SafeRaiseRemoteControlDisplayInfoReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePressureStateFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenSharePressureStateFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenSharePressureStateCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenSharePressureStateFrameType, securePayload.Metadata, requestId: null))
                    {
                        SafeRaiseScreenSharePressureStateReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenShareCursorStateFrameType, StringComparison.Ordinal))
                {
                    if (SessionSupportsScreenShareCursorOverlay &&
                        TryGetPayloadBytes(frame, out var payloadBytes) &&
                        TryDecryptControlPayload(payloadBytes, ScreenShareCursorStateFrameType, currentSessionSecurityState.HelperAddress, out var securePayload) &&
                        ScreenShareCursorStateCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                        TryValidateControlSecureMetadata(ScreenShareCursorStateFrameType, securePayload.Metadata, requestId: null))
                    {
                        SafeRaiseScreenShareCursorStateReceived(message);
                    }

                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            OnDisconnected();
        }
    }

    private async Task ApproveHostJoinAsync(
        SessionConnection connection,
        byte[] sharedKey,
        ApprovalRequest? approvalRequest,
        ApprovalDecision? decision,
        CancellationToken ct)
    {
        if (approvalRequest is null)
        {
            throw new InvalidOperationException("Approval decision is required.");
        }

        if (decision is null)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=approval_denied; reason=approval_decision_missing; session_id={approvalRequest.SessionId.Value}; helper_identity={approvalRequest.HelperIdentity.Value}; requested_capabilities={approvalRequest.RequestedCapabilities}");
            throw new InvalidOperationException("Explicit approval decision is required.");
        }

        if (decision.SessionId != approvalRequest.SessionId ||
            decision.HelperIdentity != approvalRequest.HelperIdentity ||
            decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=approval_denied; reason=approval_decision_mismatch; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; approved_capabilities={decision.ApprovedCapabilities}; requested_capabilities={approvalRequest.RequestedCapabilities}");
            throw new InvalidOperationException("Approval decision does not match the pending approval request.");
        }

        SetControlSessionSharedKey(sharedKey);
        await connection.WriteFrameAsync(
            CreateSecureTransportFrame(
                ApproveFrameType,
                CreateSecureLifecyclePayload(ApproveFrameType, SessionHandshakeProtocol.Serialize(decision))),
            ct);
        UpdateSessionSecurityState(currentSessionSecurityState.WithApproval(decision.ToGrant()));
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=approval_granted; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; capabilities={decision.ApprovedCapabilities}; expires_at_utc={decision.ExpiresAtUtc:O}");
        SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
        SafeRaiseApproved();
    }

    private async Task RejectHostJoinAsync(SessionConnection connection, byte[] sharedKey, ApprovalRequest? approvalRequest, string? reason, CancellationToken ct)
    {
        var rejectionReason = string.IsNullOrWhiteSpace(reason) ? "join_rejected" : reason.Trim();
        if (currentSessionSecurityState.SessionId is SessionId sessionId &&
            currentSessionSecurityState.HelperAddress is PeerAddress helperAddress)
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=approval_denied; reason={rejectionReason}; session_id={sessionId.Value}; helper_identity={helperAddress.Value}; requested_capabilities={approvalRequest?.RequestedCapabilities ?? CapabilityGrant.None}");
        }

        SetControlSessionSharedKey(sharedKey);
        await connection.WriteFrameAsync(
            CreateSecureTransportFrame(
                RejectFrameType,
                CreateSecureLifecyclePayload(RejectFrameType, JsonSerializer.SerializeToUtf8Bytes(new { reason = rejectionReason }))),
            ct);
        SafeRaiseRejected();
        connection.Dispose();
    }

    private static bool TryParseRejectReason(byte[] payload, out string reason)
    {
        reason = "join_rejected";

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("reason", out var reasonElement) ||
                reasonElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var parsedReason = reasonElement.GetString();
            if (string.IsNullOrWhiteSpace(parsedReason))
            {
                return false;
            }

            reason = parsedReason.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void OnDisconnected()
    {
        pendingOutboundHandshake = null;
        ResetControlSecureState();
        screenShareFrameReassembler.ClearAll();
        UpdateSessionSecurityState(currentSessionSecurityState.Invalidate("transport_disconnected"));
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void SafeRaiseApproved()
    {
        try
        {
            Approved?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void SafeRaiseRejected()
    {
        try
        {
            Rejected?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void SafeRaiseChatMessageReceived(byte[] payloadBytes)
    {
        try
        {
            ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payloadBytes));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlRequestReceived(ControlRequestMessageV1 message)
    {
        try
        {
            RemoteControlRequestReceived?.Invoke(this, new RemoteControlRequestReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlResponseReceived(ControlResponseMessageV1 message)
    {
        try
        {
            RemoteControlResponseReceived?.Invoke(this, new RemoteControlResponseReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlStartReceived(ControlStartMessageV1 message)
    {
        try
        {
            RemoteControlStartReceived?.Invoke(this, new RemoteControlStartReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlStopReceived(ControlStopMessageV1 message)
    {
        try
        {
            RemoteControlStopReceived?.Invoke(this, new RemoteControlStopReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlInputReceived(ControlInputMessageV1 message)
    {
        try
        {
            RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlAckReceived(ControlInputAckV1 ack)
    {
        try
        {
            RemoteControlAckReceived?.Invoke(this, new RemoteControlAckReceivedEventArgs(ack, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlStateSnapshotReceived(ControlStateSnapshotV1 snapshot)
    {
        try
        {
            RemoteControlStateSnapshotReceived?.Invoke(this, new RemoteControlStateSnapshotReceivedEventArgs(snapshot, source: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseRemoteControlDisplayInfoReceived(ControlDisplayInfoMessageV1 message)
    {
        try
        {
            RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void HandleScreenSharePayload(byte[] payloadBytes)
    {
        if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payloadBytes, out var fragments, out _) &&
            fragments.Length > 0)
        {
            if (TryValidateScreenShareMessageSession("frame", fragments[0].SessionId))
            {
                foreach (var fragment in fragments)
                {
                    screenShareFrameReassembler.OnFragment(fragment);
                }
            }

            return;
        }

        if (ScreenSharePayloadCodec.TryDeserializeStop(payloadBytes, out var stop) &&
            TryValidateScreenShareMessageSession("stop", stop.SessionId))
        {
            screenShareFrameReassembler.ClearSession(stop.SessionId);
            SafeRaiseScreenShareStopped();
        }
    }

    private void HandleScreenShareVideoStreamConfig(ScreenShareVideoStreamConfigV1 message)
    {
        screenShareFrameReassembler.OnStreamConfig(message);
        SafeRaiseScreenShareVideoStreamConfigReceived(message);
    }

    private void OnScreenShareFrameReady(object? sender, ScreenShareVideoFrameReadyEventArgs e)
    {
        try
        {
            ScreenShareFrameCompleted?.Invoke(
                this,
                new ScreenShareFrameCompletedEventArgs(
                    e.FrameId,
                    e.Width,
                    e.Height,
                    e.Encoding,
                    e.EncodedFrameBytes,
                    e.CapturedTsUtcMs,
                    SessionId: e.SessionId,
                    IsKeyFrame: e.IsKeyFrame,
                    StreamEpoch: e.StreamEpoch,
                    StreamConfig: e.StreamConfig,
                    FrameReadyObservedUtcMs: e.FrameReadyObservedUtcMs));
        }
        catch
        {
        }
    }

    private void SafeRaiseScreenShareStopped()
    {
        try
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferOfferReceived(FileTransferOfferV2 message)
    {
        LogFileTransferFrameEvent("received", FileTransferOfferFrameType, message.TransferId);
        try
        {
            FileTransferOfferReceived?.Invoke(this, new FileTransferOfferReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferAcceptReceived(FileTransferAcceptV1 message)
    {
        LogFileTransferFrameEvent("received", FileTransferAcceptFrameType, message.TransferId);
        try
        {
            FileTransferAcceptReceived?.Invoke(this, new FileTransferAcceptReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferDeclineReceived(FileTransferDeclineV1 message)
    {
        LogFileTransferFrameEvent("received", FileTransferDeclineFrameType, message.TransferId);
        try
        {
            FileTransferDeclineReceived?.Invoke(this, new FileTransferDeclineReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferSessionOpenReceived(FileTransferSessionOpenV2 message)
    {
        LogFileTransferFrameEvent("received", FileTransferSessionOpenFrameType, message.TransferId);
        try
        {
            FileTransferSessionOpenReceived?.Invoke(this, new FileTransferSessionOpenReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferCancelReceived(FileTransferCancelV1 message)
    {
        LogFileTransferFrameEvent("received", FileTransferCancelFrameType, message.TransferId);
        try
        {
            FileTransferCancelReceived?.Invoke(this, new FileTransferCancelReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferErrorReceived(FileTransferErrorV1 message)
    {
        LogFileTransferFrameEvent("received", FileTransferErrorFrameType, message.TransferId);
        try
        {
            FileTransferErrorReceived?.Invoke(this, new FileTransferErrorReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferCompleteReceived(FileTransferCompleteV1 message)
    {
        LogFileTransferFrameEvent("received", FileTransferCompleteFrameType, message.TransferId);
        try
        {
            FileTransferCompleteReceived?.Invoke(this, new FileTransferCompleteReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferPauseControlReceived(FileTransferPauseControlV6 message)
    {
        LogFileTransferFrameEvent("received", FileTransferPauseControlFrameType, message.TransferId);
        try
        {
            FileTransferPauseControlReceived?.Invoke(this, new FileTransferPauseControlReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferHeartbeatReceived(FileTransferHeartbeatV6 message)
    {
        try
        {
            FileTransferHeartbeatReceived?.Invoke(this, new FileTransferHeartbeatReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferTransportEpochReceived(FileTransferTransportEpochV6 message)
    {
        LogFileTransferFrameEvent("received", FileTransferTransportEpochFrameType, message.TransferId);
        try
        {
            FileTransferTransportEpochReceived?.Invoke(this, new FileTransferTransportEpochReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferTransportProbeReceived(FileTransferTransportProbeV6 message)
    {
        LogFileTransferFrameEvent("received", FileTransferTransportProbeFrameType, message.TransferId);
        try
        {
            FileTransferTransportProbeReceived?.Invoke(this, new FileTransferTransportProbeReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseFileTransferRepairProofReceived(FileTransferRepairProofV6 message)
    {
        LogFileTransferFrameEvent("received", FileTransferRepairProofFrameType, message.TransferId);
        try
        {
            FileTransferRepairProofReceived?.Invoke(this, new FileTransferRepairProofReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private bool TryHandleFileTransferFrame(TransportFrame frame)
    {
        if (!IsFileTransferFrameType(frame.Type))
        {
            return false;
        }

        if (!TryGetPayloadBytes(frame, out var payloadBytes))
        {
            return true;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.Equals(frame.Type, FileTransferOfferFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferOfferFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeOffer(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferOfferFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("offer", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferOfferFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferOfferReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferAcceptFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferAcceptFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeAccept(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferAcceptFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("accept", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferAcceptFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferAcceptReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferDeclineFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferDeclineFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeDecline(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferDeclineFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("decline", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferDeclineFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferDeclineReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferStartFrameType, StringComparison.Ordinal))
        {
            LogUnsupportedLegacyFileTransferFrame(FileTransferStartFrameType);
            return true;
        }

        if (string.Equals(frame.Type, FileTransferChunkFrameType, StringComparison.Ordinal))
        {
            LogUnsupportedLegacyFileTransferFrame(FileTransferChunkFrameType);
            return true;
        }

        if (string.Equals(frame.Type, FileTransferSessionOpenFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferSessionOpenFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeSessionOpen(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferSessionOpenFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("session_open", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferSessionOpenFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferSessionOpenReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferWindowUpdateFrameType, StringComparison.Ordinal))
        {
            LogUnsupportedLegacyFileTransferFrame(FileTransferWindowUpdateFrameType);
            return true;
        }

        if (string.Equals(frame.Type, FileTransferMissingRangeFrameType, StringComparison.Ordinal))
        {
            LogUnsupportedLegacyFileTransferFrame(FileTransferMissingRangeFrameType);
            return true;
        }

        if (string.Equals(frame.Type, FileTransferPressureStateFrameType, StringComparison.Ordinal))
        {
            LogUnsupportedLegacyFileTransferFrame(FileTransferPressureStateFrameType);
            return true;
        }

        if (string.Equals(frame.Type, FileTransferCancelFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferCancelFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeCancel(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferCancelFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("cancel", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferCancelFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferCancelReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferErrorFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferErrorFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeError(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferErrorFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("error", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferErrorFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferErrorReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferCompleteFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferCompleteFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeComplete(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferCompleteFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("complete", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferCompleteFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferCompleteReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferPauseControlFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferPauseControlFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializePauseControl(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferPauseControlFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("pause_control", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferPauseControlFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferPauseControlReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferHeartbeatFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferHeartbeatFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeHeartbeat(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferHeartbeatFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("heartbeat", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferHeartbeatFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferHeartbeatReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferTransportEpochFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferTransportEpochFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeTransportEpoch(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferTransportEpochFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("transport_epoch", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferTransportEpochFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferTransportEpochReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferTransportProbeFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferTransportProbeFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeTransportProbe(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferTransportProbeFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("transport_probe", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferTransportProbeFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferTransportProbeReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferRepairProofFrameType, StringComparison.Ordinal))
        {
            if (TryDecryptFileTransferPayload(payloadBytes, FileTransferRepairProofFrameType, expectedSender, out var securePayload) &&
                FileTransferPayloadCodec.TryDeserializeRepairProof(securePayload.Plaintext, out var message) &&
                TryValidateFileTransferSecureMetadata(FileTransferRepairProofFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("repair_proof", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferMessage(FileTransferRepairProofFrameType, message.TransferId, inbound: true, applyStateChange: true, out _))
            {
                SafeRaiseFileTransferRepairProofReceived(message);
            }

            return true;
        }

        if (string.Equals(frame.Type, FileTransferDataFrameType, StringComparison.Ordinal))
        {
            if (!TryDecryptFileTransferPayload(payloadBytes, FileTransferDataFrameType, expectedSender, out var securePayload))
            {
                return true;
            }

            if ((!FileTransferDataFrameCodec.TryDeserialize(securePayload.Plaintext, out var message) &&
                 !FileTransferDataFrameCodec.TryDeserializeLegacyV4(securePayload.Plaintext, out message)) ||
                message is null)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_frame_decode_failed; transport=devlocal; transfer_id={(currentSessionSecurityState.SessionId?.Value ?? "(unknown)")}; session_id={(currentSessionSecurityState.SessionId?.Value ?? "(unknown)")}");
                return true;
            }

            if (TryValidateFileTransferSecureMetadata(FileTransferDataFrameType, securePayload.Metadata, message.TransferId) &&
                TryValidateFileTransferMessageSession("data_frame", message.SessionId, message.TransferId) &&
                TryValidateAndTrackFileTransferDataFrame(message, inbound: true, applyStateChange: true, out _))
            {
                DeliverFileTransferDataFrame(message);
            }

            return true;
        }

        return false;
    }

    private bool TryValidateFileTransferMessageSession(string messageType, string? messageSessionId, string? transferId)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? null : transferId.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else
        {
            return true;
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; transfer_id={normalizedTransferId ?? "(none)"}; source=devlocal-peer");
        return false;
    }

    private bool TryValidateControlMessageSession(string messageType, string? messageSessionId, string? requestId)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else
        {
            return true;
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=control_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; request_id={requestId ?? "(none)"}; source=devlocal-peer");
        return false;
    }

    private bool TryValidateScreenShareMessageSession(string messageType, string? messageSessionId)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else
        {
            return true;
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screenshare_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; source=devlocal-peer");
        return false;
    }

    private byte[] CreateSecureControlPayload(string frameType, string? requestId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.RemoteControl,
            MessageType: MapSecureControlFrameType(frameType),
            SessionId: sessionId,
            SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
            Sequence: Interlocked.Increment(ref nextOutboundControlSecureSequence),
            RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim());
        return SessionSecureEnvelopeCodec.Encrypt(GetControlSessionSharedKeyOrThrow(), metadata, plaintextPayload);
    }

    private byte[] CreateSecureChatPayload(byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Chat,
            MessageType: MapSecureChatFrameType(),
            SessionId: sessionId,
            SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
            Sequence: Interlocked.Increment(ref nextOutboundChatSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(GetControlSessionSharedKeyOrThrow(), metadata, plaintextPayload);
    }

    private byte[] CreateSecureLifecyclePayload(string frameType, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Lifecycle,
            MessageType: MapSecureLifecycleFrameType(frameType),
            SessionId: sessionId,
            SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
            Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(GetControlSessionSharedKeyOrThrow(), metadata, plaintextPayload);
    }

    private byte[] CreateSecureScreenSharePayload(byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        string messageType;
        if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(plaintextPayload, out var fragments, out _) &&
            fragments.Length > 0)
        {
            messageType = ScreenSharePayloadFrameType;
        }
        else if (ScreenSharePayloadCodec.TryDeserializeStop(plaintextPayload, out _))
        {
            messageType = ScreenShareStopFrameType;
        }
        else
        {
            throw new InvalidOperationException("Screen share payload is invalid.");
        }

        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.ScreenShare,
            MessageType: MapSecureScreenShareFrameType(messageType),
            SessionId: sessionId,
            SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
            Sequence: Interlocked.Increment(ref nextOutboundScreenShareSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(GetControlSessionSharedKeyOrThrow(), metadata, plaintextPayload);
    }

    private byte[] CreateSecureFileTransferPayload(string frameType, string transferId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var sessionRootKey = GetControlSessionSharedKeyOrThrow();

        try
        {
            var fileTransferKey = SessionKeyDerivation.DeriveFileTransferKey(sessionRootKey);
            try
            {
                var metadata = new SessionSecureEnvelopeMetadata(
                    Family: SessionSecureMessageFamily.FileTransfer,
                    MessageType: MapSecureFileTransferFrameType(frameType),
                    SessionId: sessionId,
                    SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
                    Sequence: Interlocked.Increment(ref nextOutboundFileTransferSecureSequence),
                    RequestId: normalizedTransferId);
                return SessionSecureEnvelopeCodec.Encrypt(fileTransferKey, metadata, plaintextPayload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileTransferKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionRootKey);
        }
    }

    private bool TryDecryptControlPayload(
        byte[] encodedPayload,
        string frameType,
        PeerAddress? expectedSender,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = null!;
        if (expectedSender is null ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                GetControlSessionSharedKeyOrThrow(),
                encodedPayload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.RemoteControl,
                    MessageType: MapSecureControlFrameType(frameType),
                    SessionId: sessionId,
                    SenderIdentity: expectedSender));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={frameType}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source=devlocal-peer; ex={ex.GetType().Name}");
            return false;
        }

        lock (secureStateGate)
        {
            if (inboundControlReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence) != SessionReplaySequenceResult.Accepted)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryDecryptChatPayload(byte[] encodedPayload, out byte[] plaintextPayload)
    {
        plaintextPayload = Array.Empty<byte>();
        if (ResolveExpectedRemotePeerAddressForCurrentSession() is not PeerAddress expectedSender ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return false;
        }

        SessionSecureEnvelopePayload securePayload;
        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                GetControlSessionSharedKeyOrThrow(),
                encodedPayload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Chat,
                    MessageType: MapSecureChatFrameType(),
                    SessionId: sessionId,
                    SenderIdentity: expectedSender));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=chat_message_rejected; reason=secure_envelope_invalid; session_id={sessionId.Value}; source=devlocal-peer; ex={ex.GetType().Name}");
            return false;
        }

        lock (secureStateGate)
        {
            if (inboundChatReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence) != SessionReplaySequenceResult.Accepted)
            {
                return false;
            }
        }

        plaintextPayload = securePayload.Plaintext;
        return true;
    }

    private bool TryDecryptLifecyclePayload(
        byte[] encodedPayload,
        byte[] key,
        string frameType,
        PeerAddress? expectedSender,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = null!;
        if (expectedSender is null || pendingOutboundHandshake?.SessionId is not SessionId sessionId)
        {
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                encodedPayload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: MapSecureLifecycleFrameType(frameType),
                    SessionId: sessionId,
                    SenderIdentity: expectedSender));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=lifecycle_message_rejected; message_type={frameType}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source=devlocal-peer; ex={ex.GetType().Name}");
            return false;
        }

        lock (secureStateGate)
        {
            if (inboundLifecycleReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence) != SessionReplaySequenceResult.Accepted)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryDecryptScreenSharePayload(byte[] encodedPayload, out byte[] plaintextPayload)
    {
        plaintextPayload = Array.Empty<byte>();
        if (ResolveExpectedRemotePeerAddressForCurrentSession() is not PeerAddress expectedSender ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return false;
        }

        SessionSecureEnvelopePayload securePayload;
        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                GetControlSessionSharedKeyOrThrow(),
                encodedPayload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.ScreenShare,
                    SessionId: sessionId,
                    SenderIdentity: expectedSender));
        }
        catch
        {
            return false;
        }

        if (!string.Equals(securePayload.Metadata.MessageType, MapSecureScreenShareFrameType(ScreenSharePayloadFrameType), StringComparison.Ordinal) &&
            !string.Equals(securePayload.Metadata.MessageType, MapSecureScreenShareFrameType(ScreenShareStopFrameType), StringComparison.Ordinal))
        {
            return false;
        }

        lock (secureStateGate)
        {
            if (inboundScreenShareReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence) != SessionReplaySequenceResult.Accepted)
            {
                return false;
            }
        }

        plaintextPayload = securePayload.Plaintext;
        return true;
    }

    private bool TryDecryptFileTransferPayload(
        byte[] encodedPayload,
        string frameType,
        PeerAddress? expectedSender,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = null!;
        if (expectedSender is null ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return false;
        }

        var sessionRootKey = GetControlSessionSharedKeyOrThrow();
        try
        {
            var fileTransferKey = SessionKeyDerivation.DeriveFileTransferKey(sessionRootKey);
            try
            {
                securePayload = SessionSecureEnvelopeCodec.Decrypt(
                    fileTransferKey,
                    encodedPayload,
                    new SessionSecureEnvelopeExpectation(
                        Family: SessionSecureMessageFamily.FileTransfer,
                        MessageType: MapSecureFileTransferFrameType(frameType),
                        SessionId: sessionId,
                        SenderIdentity: expectedSender));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileTransferKey);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={frameType}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source=devlocal-peer; ex={ex.GetType().Name}");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionRootKey);
        }

        lock (secureStateGate)
        {
            if (inboundFileTransferReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence) != SessionReplaySequenceResult.Accepted)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryValidateControlSecureMetadata(string frameType, SessionSecureEnvelopeMetadata metadata, string? requestId)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        var normalizedMetadataRequestId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        return string.Equals(normalizedMetadataRequestId, normalizedRequestId, StringComparison.Ordinal);
    }

    private bool TryValidateFileTransferSecureMetadata(string frameType, SessionSecureEnvelopeMetadata metadata, string transferId)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var normalizedMetadataTransferId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        if (string.Equals(normalizedMetadataTransferId, normalizedTransferId, StringComparison.Ordinal))
        {
            return true;
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={frameType}; reason=transfer_id_metadata_mismatch; session_id={metadata.SessionId.Value}; transfer_id={normalizedTransferId}; metadata_transfer_id={normalizedMetadataTransferId ?? "(none)"}; source=devlocal-peer");
        return false;
    }

    private bool TryValidateAndTrackFileTransferMessage(
        string frameType,
        string transferId,
        bool inbound,
        bool applyStateChange,
        out string failureReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (secureStateGate)
        {
            if (!TryGetNextFileTransferState(frameType, normalizedTransferId, inbound, out var nextState, out failureReason))
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_message_rejected; message_type={frameType}; reason={failureReason}; session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={normalizedTransferId}; direction={(inbound ? "inbound" : "outbound")}; source={(inbound ? "devlocal-peer" : "devlocal-local")}");
                return false;
            }

            if (applyStateChange)
            {
                fileTransferStates[normalizedTransferId] = nextState;
            }

            return true;
        }
    }

    private bool TryValidateAndTrackFileTransferDataFrame(
        FileTransferDataFrame frame,
        bool inbound,
        bool applyStateChange,
        out string failureReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(frame.TransferId);

        lock (secureStateGate)
        {
            if (!TryGetNextFileTransferDataFrameState(frame, normalizedTransferId, inbound, out var nextState, out failureReason))
            {
                if (!IsBenignLateFileTransferDataFrameRejection(frame, failureReason))
                {
                    LocalOperationalLog.Warn(
                        "SessionSecurity",
                        $"event=filetransfer_message_rejected; message_type={FileTransferDataFrameType}; reason={failureReason}; session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={normalizedTransferId}; direction={(inbound ? "inbound" : "outbound")}; source={(inbound ? "devlocal-peer" : "devlocal-local")}");
                }
                else
                {
                    LocalOperationalLog.Info(
                        "SessionSecurity",
                        $"event=filetransfer_data_frame_ignored; transport=devlocal; transfer_id={normalizedTransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason={failureReason}; source={(inbound ? "devlocal-peer" : "devlocal-local")}");
                }

                return false;
            }

            if (applyStateChange)
            {
                fileTransferStates[normalizedTransferId] = nextState;
            }

            return true;
        }
    }

    private void OnScreenShareKeyframeRequested(object? sender, ScreenShareVideoKeyframeRequestV1 e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendScreenShareVideoKeyframeRequestAsync(e, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private void SafeRaiseScreenShareVideoStreamConfigReceived(ScreenShareVideoStreamConfigV1 message)
    {
        try
        {
            ScreenShareVideoStreamConfigReceived?.Invoke(this, new ScreenShareVideoStreamConfigReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseScreenShareVideoKeyframeRequestReceived(ScreenShareVideoKeyframeRequestV1 message)
    {
        try
        {
            ScreenShareVideoKeyframeRequestReceived?.Invoke(this, new ScreenShareVideoKeyframeRequestReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseScreenSharePressureStateReceived(ScreenSharePressureStateV1 message)
    {
        try
        {
            ScreenSharePressureStateReceived?.Invoke(this, new ScreenSharePressureStateReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseScreenShareRecoveryReceiptReceived(ScreenShareRecoveryReceiptV1 message)
    {
        try
        {
            ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private void SafeRaiseScreenShareCursorStateReceived(ScreenShareCursorStateV1 message)
    {
        try
        {
            ScreenShareCursorStateReceived?.Invoke(this, new ScreenShareCursorStateReceivedEventArgs(message, peerId: "devlocal-peer"));
        }
        catch
        {
        }
    }

    private bool TryValidateKnownInboundFileTransferDataPath(string frameType, string transferId)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (secureStateGate)
        {
            if (fileTransferStates.TryGetValue(normalizedTransferId, out var currentState) &&
                !currentState.IsTerminal)
            {
                return true;
            }
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={frameType}; reason=unknown_transfer_id; session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={normalizedTransferId}; direction=inbound; source=devlocal-peer");
        return false;
    }

    private bool TryGetNextFileTransferState(
        string frameType,
        string transferId,
        bool inbound,
        out FileTransferTransportState nextState,
        out string failureReason)
    {
        var hasExisting = fileTransferStates.TryGetValue(transferId, out var currentState);
        nextState = default;
        failureReason = string.Empty;

        if (string.Equals(frameType, FileTransferOfferFrameType, StringComparison.Ordinal))
        {
            if (hasExisting)
            {
                failureReason = currentState.IsTerminal ? "transfer_id_reused_after_terminal_state" : "duplicate_transfer_id";
                return false;
            }

            var initiatedLocally = !inbound;
            if (HasActiveFileTransferLocked(initiatedLocally))
            {
                failureReason = "concurrent_transfer_busy";
                return false;
            }

            nextState = new FileTransferTransportState(
                InitiatedLocally: initiatedLocally,
                Phase: FileTransferTransportPhase.Offered);
            return true;
        }

        if (!hasExisting)
        {
            failureReason = "unknown_transfer_id";
            return false;
        }

        if (currentState.IsTerminal)
        {
            if (string.Equals(frameType, FileTransferCancelFrameType, StringComparison.Ordinal) &&
                currentState.Phase == FileTransferTransportPhase.Canceled)
            {
                nextState = currentState;
                return true;
            }

            failureReason = "transfer_already_terminal";
            return false;
        }

        if (string.Equals(frameType, FileTransferAcceptFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase != FileTransferTransportPhase.Offered)
            {
                failureReason = "accept_requires_offer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_accept_for_remote_offer" : "unexpected_outbound_accept_for_local_offer";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Accepted };
            return true;
        }

        if (string.Equals(frameType, FileTransferDeclineFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase != FileTransferTransportPhase.Offered)
            {
                failureReason = "decline_requires_offer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_decline_for_remote_offer" : "unexpected_outbound_decline_for_local_offer";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Declined };
            return true;
        }

        if (string.Equals(frameType, FileTransferStartFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase != FileTransferTransportPhase.Accepted)
            {
                failureReason = "start_requires_accept";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_start_for_local_receiver" : "unexpected_outbound_start_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Started };
            return true;
        }

        if (string.Equals(frameType, FileTransferSessionOpenFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "session_open_requires_accept";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_session_open_for_local_receiver" : "unexpected_outbound_session_open_for_local_receiver";
                return false;
            }

            nextState = currentState.Phase == FileTransferTransportPhase.Accepted
                ? currentState with { Phase = FileTransferTransportPhase.Started }
                : currentState;
            return true;
        }

        if (string.Equals(frameType, FileTransferChunkFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "chunk_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_chunk_for_local_receiver" : "unexpected_outbound_chunk_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (string.Equals(frameType, FileTransferWindowUpdateFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferMissingRangeFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferPressureStateFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted and not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = string.Equals(frameType, FileTransferWindowUpdateFrameType, StringComparison.Ordinal)
                    ? "window_update_requires_start"
                    : string.Equals(frameType, FileTransferMissingRangeFrameType, StringComparison.Ordinal)
                        ? "missing_range_requires_start"
                        : "pressure_state_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = string.Equals(frameType, FileTransferWindowUpdateFrameType, StringComparison.Ordinal)
                    ? (inbound ? "unexpected_inbound_window_update_for_local_receiver" : "unexpected_outbound_window_update_for_remote_receiver")
                    : string.Equals(frameType, FileTransferMissingRangeFrameType, StringComparison.Ordinal)
                        ? (inbound ? "unexpected_inbound_missing_range_for_local_receiver" : "unexpected_outbound_missing_range_for_remote_receiver")
                        : (inbound ? "unexpected_inbound_pressure_state_for_local_receiver" : "unexpected_outbound_pressure_state_for_remote_receiver");
                return false;
            }

            nextState = currentState;
            return true;
        }

        if (string.Equals(frameType, FileTransferPauseControlFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferHeartbeatFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferTransportEpochFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferTransportProbeFrameType, StringComparison.Ordinal) ||
            string.Equals(frameType, FileTransferRepairProofFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = $"{frameType}_requires_accept";
                return false;
            }

            nextState = currentState;
            return true;
        }

        if (string.Equals(frameType, FileTransferCompleteFrameType, StringComparison.Ordinal))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "complete_requires_transfer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_complete_for_remote_sender" : "unexpected_outbound_complete_for_local_sender";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Completed };
            return true;
        }

        if (string.Equals(frameType, FileTransferCancelFrameType, StringComparison.Ordinal))
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "cancel_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Canceled };
            return true;
        }

        if (string.Equals(frameType, FileTransferErrorFrameType, StringComparison.Ordinal))
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "error_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Failed };
            return true;
        }

        failureReason = "unsupported_frame_type";
        return false;
    }

    private bool TryGetNextFileTransferDataFrameState(
        FileTransferDataFrame frame,
        string transferId,
        bool inbound,
        out FileTransferTransportState nextState,
        out string failureReason)
    {
        var hasExisting = fileTransferStates.TryGetValue(transferId, out var currentState);
        nextState = default;
        failureReason = string.Empty;

        if (!FileTransferProtocol.IsV6DataFrame(frame) &&
            !FileTransferProtocol.IsV4DataFrame(frame))
        {
            failureReason = "protocol_not_supported";
            return false;
        }

        if (!hasExisting)
        {
            failureReason = "unknown_transfer_id";
            return false;
        }

        if (currentState.IsTerminal)
        {
            failureReason = "transfer_already_terminal";
            return false;
        }

        if (frame is FileTransferPauseControlFrameV4
            or FileTransferCompleteFrameV4
            or FileTransferCancelFrameV4
            or FileTransferErrorFrameV4)
        {
            failureReason = "lifecycle_data_frame_unsupported";
            return false;
        }

        if (IsV6RecoveryControlDataFrame(frame))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "recovery_control_requires_start";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (IsSenderDataFrame(frame))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "data_frame_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_data_frame_for_local_sender" : "unexpected_outbound_data_frame_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (IsSenderStateControlDataFrame(frame, currentState, inbound))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "state_control_requires_start";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (IsReceiverFeedbackDataFrame(frame))
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted
                and not FileTransferTransportPhase.Started
                and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "feedback_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_feedback_for_local_receiver" : "unexpected_outbound_feedback_for_remote_receiver";
                return false;
            }

            nextState = currentState;
            return true;
        }

        failureReason = "unsupported_data_frame_type";
        return false;
    }

    private static bool IsSenderDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferManifestFrameV4
            or FileTransferChunkBatchFrameV4;

    private static bool IsReceiverFeedbackDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferStateFrameV4;

    private static bool IsV6RecoveryControlDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferTransportEpochFrameV6
            or FileTransferTransportProbeFrameV6
            or FileTransferFrontierRequestFrameV6
            or FileTransferRepairProofFrameV6;

    private static bool IsSenderStateControlDataFrame(
        FileTransferDataFrame frame,
        FileTransferTransportState currentState,
        bool inbound)
        => frame is FileTransferStateFrameV4 state &&
           currentState.InitiatedLocally == !inbound &&
           (state.TransferPaused || !string.IsNullOrWhiteSpace(state.TransferPauseReason));

    private static bool IsTerminalDataFrame(FileTransferDataFrame frame)
        => frame is FileTransferCompleteFrameV4
            or FileTransferCancelFrameV4
            or FileTransferErrorFrameV4;

    private static bool IsBenignLateFileTransferDataFrameRejection(FileTransferDataFrame frame, string failureReason)
        => ((failureReason is "unknown_transfer_id" or "transfer_already_terminal") &&
            (IsReceiverFeedbackDataFrame(frame) || IsV6RecoveryControlDataFrame(frame) || IsTerminalDataFrame(frame) || frame is FileTransferPauseControlFrameV4)) ||
           (failureReason == "transfer_already_terminal" && IsSenderDataFrame(frame));

    private static void LogFileTransferFrameEvent(string direction, string frameType, string transferId)
    {
        if (string.Equals(frameType, FileTransferChunkFrameType, StringComparison.Ordinal))
        {
            return;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_frame_{direction}; transport=devlocal; message_type={frameType}; transfer_id={transferId}");
    }

    private static void LogUnsupportedLegacyFileTransferFrame(string frameType)
    {
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_legacy_frame_ignored; transport=devlocal; message_type={frameType}; reason=v4_only");
    }

    private bool HasActiveFileTransferLocked(bool initiatedLocally)
    {
        foreach (var state in fileTransferStates.Values)
        {
            if (!state.IsTerminal && state.InitiatedLocally == initiatedLocally)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanTransitionToTerminalFileTransferState(FileTransferTransportPhase phase)
        => phase is FileTransferTransportPhase.Offered or
            FileTransferTransportPhase.Accepted or
            FileTransferTransportPhase.Started or
            FileTransferTransportPhase.Transferring;

    private string? ResolveRequestId(string frameType, byte[] payload)
    {
        return frameType switch
        {
            ControlRequestFrameType when RemoteControlPayloadCodec.TryDeserializeControlRequest(payload, out var message) => message.RequestId,
            ControlResponseFrameType when RemoteControlPayloadCodec.TryDeserializeControlResponse(payload, out var message) => message.RequestId,
            ControlStartFrameType when RemoteControlPayloadCodec.TryDeserializeControlStart(payload, out var message) => message.RequestId,
            ControlStopFrameType when RemoteControlPayloadCodec.TryDeserializeControlStop(payload, out var message) => message.RequestId,
            ControlInputFrameType when RemoteControlPayloadCodec.TryDeserializeControlInput(payload, out var message) => message.RequestId,
            ControlAckFrameType when RemoteControlPayloadCodec.TryDeserializeControlAck(payload, out var message) => message.RequestId,
            ControlStateSnapshotFrameType when RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(payload, out var message) => message.RequestId,
            _ => null,
        };
    }

    private PeerAddress ResolveLocalPeerAddressForSecureEnvelope()
        => new(localPeerAddress);

    private PeerAddress? ResolveExpectedRemotePeerAddressForCurrentSession()
    {
        if (currentSessionSecurityState.HelperAddress is PeerAddress helperAddress &&
            !string.Equals(helperAddress.Value, localPeerAddress, StringComparison.Ordinal))
        {
            return helperAddress;
        }

        if (currentSessionSecurityState.HelpeeAddress is PeerAddress helpeeAddress &&
            !string.Equals(helpeeAddress.Value, localPeerAddress, StringComparison.Ordinal))
        {
            return helpeeAddress;
        }

        return pendingOutboundHandshake?.HelpeeAddress;
    }

    private byte[] GetControlSessionSharedKeyOrThrow()
    {
        lock (secureStateGate)
        {
            if (controlSessionSharedKey is null || controlSessionSharedKey.Length == 0)
            {
                throw new InvalidOperationException("Session shared key is not available.");
            }

            return controlSessionSharedKey.AsSpan().ToArray();
        }
    }

    private void SetControlSessionSharedKey(byte[] sharedKey)
    {
        ArgumentNullException.ThrowIfNull(sharedKey);
        lock (secureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
            }

            controlSessionSharedKey = sharedKey.AsSpan().ToArray();
            nextOutboundChatSecureSequence = 0;
            nextOutboundControlSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundChatReplayWindow.Reset();
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }
    }

    private void ResetControlSecureState()
    {
        lock (secureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
                controlSessionSharedKey = null;
            }

            nextOutboundChatSecureSequence = 0;
            nextOutboundControlSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundChatReplayWindow.Reset();
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }
    }

    private static string MapSecureControlFrameType(string frameType) => frameType;

    private static string MapSecureChatFrameType() => "chat_message";

    private static string MapSecureLifecycleFrameType(string frameType) => frameType switch
    {
        ApproveFrameType => ApproveFrameType,
        RejectFrameType => RejectFrameType,
        _ => throw new ArgumentOutOfRangeException(nameof(frameType), frameType, "Unsupported lifecycle frame type."),
    };

    private static string MapSecureScreenShareFrameType(string frameType) => frameType switch
    {
        ScreenSharePayloadFrameType => "screenshare_frame",
        ScreenShareStopFrameType => "screenshare_stop",
        ScreenShareVideoStreamConfigFrameType => "screenshare_video_stream_config",
        ScreenShareVideoKeyframeRequestFrameType => "screenshare_video_keyframe_request",
        ScreenShareRecoveryReceiptFrameType => "screenshare_recovery_receipt",
        ScreenShareCursorStateFrameType => "screenshare_cursor_state",
        _ => throw new ArgumentOutOfRangeException(nameof(frameType), frameType, "Unsupported screen-share frame type."),
    };

    private static string MapSecureFileTransferFrameType(string frameType) => frameType switch
    {
        FileTransferOfferFrameType => FileTransferOfferFrameType,
        FileTransferAcceptFrameType => FileTransferAcceptFrameType,
        FileTransferDeclineFrameType => FileTransferDeclineFrameType,
        FileTransferSessionOpenFrameType => FileTransferSessionOpenFrameType,
        FileTransferStartFrameType => FileTransferStartFrameType,
        FileTransferChunkFrameType => FileTransferChunkFrameType,
        FileTransferWindowUpdateFrameType => FileTransferWindowUpdateFrameType,
        FileTransferMissingRangeFrameType => FileTransferMissingRangeFrameType,
        FileTransferPressureStateFrameType => FileTransferPressureStateFrameType,
        FileTransferDataFrameType => FileTransferDataFrameType,
        FileTransferCancelFrameType => FileTransferCancelFrameType,
        FileTransferErrorFrameType => FileTransferErrorFrameType,
        FileTransferCompleteFrameType => FileTransferCompleteFrameType,
        FileTransferPauseControlFrameType => FileTransferPauseControlFrameType,
        FileTransferHeartbeatFrameType => FileTransferHeartbeatFrameType,
        FileTransferTransportEpochFrameType => FileTransferTransportEpochFrameType,
        FileTransferTransportProbeFrameType => FileTransferTransportProbeFrameType,
        FileTransferRepairProofFrameType => FileTransferRepairProofFrameType,
        _ => throw new ArgumentOutOfRangeException(nameof(frameType), frameType, "Unsupported file-transfer frame type."),
    };

    private ControlRequestMessageV1 EnsureControlSessionId(ControlRequestMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlResponseMessageV1 EnsureControlSessionId(ControlResponseMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStartMessageV1 EnsureControlSessionId(ControlStartMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStopMessageV1 EnsureControlSessionId(ControlStopMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlInputMessageV1 EnsureControlSessionId(ControlInputMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlInputAckV1 EnsureControlSessionId(ControlInputAckV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlStateSnapshotV1 EnsureControlSessionId(ControlStateSnapshotV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ControlDisplayInfoMessageV1 EnsureControlSessionId(ControlDisplayInfoMessageV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenSharePressureStateV1 EnsureScreenSharePressureStateSessionId(ScreenSharePressureStateV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareRecoveryReceiptV1 EnsureScreenShareRecoveryReceiptSessionId(ScreenShareRecoveryReceiptV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private ScreenShareCursorStateV1 EnsureScreenShareCursorStateSessionId(ScreenShareCursorStateV1 message)
        => message with { SessionId = ResolveControlSessionId(message.SessionId) };

    private FileTransferOfferV2 EnsureFileTransferSessionId(FileTransferOfferV2 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferAcceptV1 EnsureFileTransferSessionId(FileTransferAcceptV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferDeclineV1 EnsureFileTransferSessionId(FileTransferDeclineV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferSessionOpenV2 EnsureFileTransferSessionId(FileTransferSessionOpenV2 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferCancelV1 EnsureFileTransferSessionId(FileTransferCancelV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferErrorV1 EnsureFileTransferSessionId(FileTransferErrorV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferCompleteV1 EnsureFileTransferSessionId(FileTransferCompleteV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferPauseControlV6 EnsureFileTransferSessionId(FileTransferPauseControlV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferHeartbeatV6 EnsureFileTransferSessionId(FileTransferHeartbeatV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferTransportEpochV6 EnsureFileTransferSessionId(FileTransferTransportEpochV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferTransportProbeV6 EnsureFileTransferSessionId(FileTransferTransportProbeV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferRepairProofV6 EnsureFileTransferSessionId(FileTransferRepairProofV6 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private string ResolveControlSessionId(string? current)
    {
        return string.IsNullOrWhiteSpace(current)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : current.Trim();
    }

    private static string NormalizeRequiredFileTransferId(string? transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        var normalized = transferId.Trim();
        if (normalized.Length == 0 || normalized.Length > FileTransferProtocol.MaxTransferIdLength)
        {
            throw new ArgumentException("Transfer id is invalid.", nameof(transferId));
        }

        return normalized;
    }

    private void DeliverFileTransferDataFrame(FileTransferDataFrame frame)
    {
        if (!fileTransferDataSessions.TryGetValue(frame.TransferId, out var session))
        {
            session = new TransportFileTransferDataSession(this, frame.SessionId, frame.TransferId);
            fileTransferDataSessions[frame.TransferId] = session;
        }
        else if (!string.Equals(session.SessionId, frame.SessionId, StringComparison.Ordinal))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_data_frame_ignored; transport=devlocal; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason=session_id_mismatch_existing_queue");
            return;
        }

        session.Deliver(frame);
    }

    private sealed class TransportFileTransferDataSession : IFileTransferDataSession
    {
        private readonly DevLocalTransport owner;
        private readonly Channel<FileTransferReceivedDataFrame> frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private int disposed;
        private int activeReader;

        public TransportFileTransferDataSession(DevLocalTransport owner, string sessionId, string transferId)
        {
            this.owner = owner;
            SessionId = sessionId;
            TransferId = transferId;
        }

        public string SessionId { get; }

        public string TransferId { get; }

        public bool IsAvailable => true;

#pragma warning disable CS0067
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;
#pragma warning restore CS0067

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct).ConfigureAwait(false)).Frame;

        public async ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=transport_session_multiple_readers");
            }

            try
            {
                return await frames.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref activeReader, 0);
            }
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            ArgumentNullException.ThrowIfNull(frame);
            return owner.SendFileTransferDataFrameAsync(frame, TransferId, ct);
        }

        public void Deliver(FileTransferDataFrame frame)
        {
            if (disposed != 0)
            {
                return;
            }

            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_data_frame_dispatched; transport=devlocal; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}");
            frames.Writer.TryWrite(new FileTransferReceivedDataFrame(frame, FileTransferTransportKind.RegularNkn, "regular_nkn", DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            frames.Writer.TryComplete();
        }
    }

    private static string GetFileTransferDataFrameChunkIndex(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };


    private static bool IsFileTransferFrameType(string? frameType)
    {
        return string.Equals(frameType, FileTransferOfferFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferAcceptFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferDeclineFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferSessionOpenFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferStartFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferChunkFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferWindowUpdateFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferMissingRangeFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferPressureStateFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferDataFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferCancelFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferErrorFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferCompleteFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferPauseControlFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferHeartbeatFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferTransportEpochFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferTransportProbeFrameType, StringComparison.Ordinal) ||
               string.Equals(frameType, FileTransferRepairProofFrameType, StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string BuildPipeName(string peerAddress)
    {
        return "nlink-dev-mock-" + peerAddress;
    }

    private static string NormalizeLocalPeerAddress(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? $"devlocal.{Guid.NewGuid():N}"
            : value.Trim();

        if (!PeerAddress.TryParse(candidate, out var address))
        {
            throw new ArgumentException("Local peer address is invalid.", nameof(value));
        }

        return address.Value;
    }

    private static TaskCompletionSource<bool> CreateHostReadyTcs()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ResetHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs = CreateHostReadyTcs();
        }
    }

    private void TrySetHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetResult(true);
        }
    }

    private void TryCancelHostReady()
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetCanceled();
        }
    }

    private SessionConnection? GetActiveConnection()
    {
        lock (activeConnectionGate)
        {
            return activeConnection;
        }
    }

    private SessionConnection? ClearActiveConnection()
    {
        lock (activeConnectionGate)
        {
            var previous = activeConnection;
            activeConnection = null;
            return previous;
        }
    }

    private void ReplaceActiveConnection(SessionConnection next)
    {
        SessionConnection? previous;
        lock (activeConnectionGate)
        {
            previous = activeConnection;
            activeConnection = next;
        }

        previous?.Dispose();
    }

    private static bool TryGetPayloadBytes(TransportFrame frame, out byte[] payloadBytes)
    {
        payloadBytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            payloadBytes = Convert.FromBase64String(frame.Data);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static TransportFrame CreateHandshakeFrame(string frameType, byte[] payload)
    {
        return new TransportFrame
        {
            Type = frameType,
            Data = Convert.ToBase64String(payload),
        };
    }

    private static TransportFrame CreateSecureTransportFrame(string frameType, byte[] payload)
        => CreateHandshakeFrame(frameType, payload);

    private void UpdateSessionSecurityState(SessionSecurityState nextState)
    {
        if (nextState is null)
        {
            throw new ArgumentNullException(nameof(nextState));
        }

        nextState = PreserveActiveApprovedSessionIfStale(nextState);

        if (Equals(currentSessionSecurityState, nextState))
        {
            return;
        }

        if (nextState.HandshakeState is SessionHandshakeState.Failed or SessionHandshakeState.Expired or SessionHandshakeState.Invalidated &&
            !string.IsNullOrWhiteSpace(nextState.HandshakeFailureReason) &&
            (currentSessionSecurityState.HandshakeState != nextState.HandshakeState ||
             !string.Equals(currentSessionSecurityState.HandshakeFailureReason, nextState.HandshakeFailureReason, StringComparison.Ordinal)))
        {
            LocalOperationalLog.Warn(
                "SessionHandshake",
                $"event=failure; direction=transport_state; reason={nextState.HandshakeFailureReason}; session_id={nextState.SessionId?.Value ?? "(none)"}; helper_identity={nextState.HelperAddress?.Value ?? "(none)"}");
        }

        currentSessionSecurityState = nextState;
        UpdateActiveApprovedSessionTracking(nextState);
        SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
    }

    private SessionSecurityState PreserveActiveApprovedSessionIfStale(SessionSecurityState nextState)
    {
        if (activeApprovedSessionId is not SessionId activeSessionId ||
            activeApprovedHelperAddress is not PeerAddress activeHelperAddress)
        {
            return nextState;
        }

        if (!ShouldRetainCapabilitySecureState(currentSessionSecurityState))
        {
            return nextState;
        }

        var nextMatchesActive =
            nextState.SessionId == activeSessionId &&
            nextState.HelperAddress == activeHelperAddress;
        if (nextMatchesActive || ShouldRetainCapabilitySecureState(nextState))
        {
            return nextState;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=stale_security_state_ignored; session_id={nextState.SessionId?.Value ?? "(none)"}; helper_identity={nextState.HelperAddress?.Value ?? "(none)"}; active_session_id={activeSessionId.Value}; active_helper_identity={activeHelperAddress.Value}");
        return currentSessionSecurityState;
    }

    private void UpdateActiveApprovedSessionTracking(SessionSecurityState nextState)
    {
        if (ShouldRetainCapabilitySecureState(nextState) &&
            nextState.SessionId is SessionId sessionId &&
            nextState.HelperAddress is PeerAddress helperAddress)
        {
            activeApprovedSessionId = sessionId;
            activeApprovedHelperAddress = helperAddress;
            return;
        }

        if (activeApprovedSessionId is SessionId activeSessionId &&
            nextState.SessionId == activeSessionId)
        {
            activeApprovedSessionId = null;
            activeApprovedHelperAddress = null;
        }

        if (nextState == SessionSecurityState.Empty)
        {
            activeApprovedSessionId = null;
            activeApprovedHelperAddress = null;
        }
    }

    private static bool ShouldRetainCapabilitySecureState(SessionSecurityState state)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        return state.InviteValidated &&
               state.HandshakeCompleted &&
               state.HandshakeState == SessionHandshakeState.Verified &&
               state.ApprovalGranted &&
               state.IsApprovalActive(nowUtc) &&
               state.SessionId is not null &&
               state.HelperAddress is not null;
    }

    private void AbortOutboundHandshake(string reason, SessionHandshakeState failureState = SessionHandshakeState.Failed)
    {
        LocalOperationalLog.Warn(
            "SessionHandshake",
            $"event=failure; direction=outbound; reason={reason}; session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={currentSessionSecurityState.HelperAddress?.Value ?? "(none)"}");
        pendingOutboundHandshake = null;
        UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(failureState, reason));
    }

    private static bool TryParseHandshakeStartPayload(byte[] payload, out SessionHandshakeStart parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            return SessionHandshakeProtocol.TryDeserializeStart(payload, out parsed);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHandshakeChallengePayload(byte[] payload, out SessionHandshakeChallenge parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            return SessionHandshakeProtocol.TryDeserializeChallenge(payload, out parsed);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHandshakeResponsePayload(byte[] payload, out SessionHandshakeResponse parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            return SessionHandshakeProtocol.TryDeserializeResponse(payload, out parsed);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHandshakeResultPayload(byte[] payload, out SessionHandshakeResult parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            return SessionHandshakeProtocol.TryDeserializeResult(payload, out parsed);
        }
        catch
        {
            return false;
        }
    }

    private static async Task SendHandshakeStartAsync(SessionConnection connection, PendingOutboundHandshakeState outboundHandshake, CancellationToken ct)
    {
        var start = new SessionHandshakeStart(
            outboundHandshake.SessionId,
            outboundHandshake.HelperAddress,
            outboundHandshake.InviteToken);
        await connection.WriteFrameAsync(CreateHandshakeFrame(SessionHandshakeStartFrameType, SessionHandshakeProtocol.Serialize(start)), ct).ConfigureAwait(false);
    }

    private enum FileTransferTransportPhase
    {
        Offered = 0,
        Accepted = 1,
        Started = 2,
        Transferring = 3,
        Completed = 4,
        Declined = 5,
        Canceled = 6,
        Failed = 7,
    }

    private readonly record struct FileTransferTransportState(
        bool InitiatedLocally,
        FileTransferTransportPhase Phase)
    {
        public bool IsTerminal
            => Phase is FileTransferTransportPhase.Completed or
                FileTransferTransportPhase.Declined or
                FileTransferTransportPhase.Canceled or
                FileTransferTransportPhase.Failed;
    }

    private sealed class SessionConnection : IDisposable
    {
        private readonly PipeStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private Task? readLoop;
        private int disposed;

        public SessionConnection(PipeStream pipe)
        {
            this.pipe = pipe;
            reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
            writer = new StreamWriter(pipe, Utf8NoBom, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }

        public bool IsConnected => pipe.IsConnected && Volatile.Read(ref disposed) == 0;

        public void SetReadLoop(Task task)
        {
            readLoop = task;
        }

        public async Task<TransportFrame?> ReadFrameAsync(CancellationToken ct)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<TransportFrame>(line, JsonOptions);
        }

        public async Task WriteFrameAsync(TransportFrame frame, CancellationToken ct)
        {
            await writeGate.WaitAsync(ct);
            try
            {
                var line = JsonSerializer.Serialize(frame, JsonOptions);
                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            // Closing the pipe is enough to break any pending read/write loops.
            // Avoid disposing reader/writer synchronously here because they may block
            // while another thread is already in a pipe read during test shutdown.
            try { pipe.Dispose(); } catch { }
            try { writeGate.Dispose(); } catch { }
        }
    }

    private sealed class TransportFrame
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public string? Data { get; init; }

        [JsonPropertyName("helperAddress")]
        public string? HelperAddress { get; init; }

        [JsonPropertyName("helpeeAddress")]
        public string? HelpeeAddress { get; init; }

        [JsonPropertyName("inviteToken")]
        public string? InviteToken { get; init; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; init; }

        [JsonPropertyName("accepted")]
        public bool? Accepted { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("remoteControlSupported")]
        public bool? RemoteControlSupported { get; init; }

        [JsonPropertyName("screenShareCursorOverlaySupported")]
        public bool? ScreenShareCursorOverlaySupported { get; init; }
    }

    private sealed record PendingOutboundHandshakeState(
        SessionId SessionId,
        PeerAddress HelperAddress,
        PeerAddress HelpeeAddress,
        bool InviteValidated,
        CapabilityGrant RequestedCapabilities,
        string? InviteToken)
    {
        public string? ChallengeNonce { get; init; }
        public DateTimeOffset? ChallengeExpiresAtUtc { get; init; }

        public PendingOutboundHandshakeState WithChallenge(string challengeNonce, DateTimeOffset challengeExpiresAtUtc)
        {
            return this with
            {
                ChallengeNonce = challengeNonce,
                ChallengeExpiresAtUtc = challengeExpiresAtUtc,
            };
        }
    }

    private readonly record struct InboundHandshakeResult(
        bool IsVerified,
        PeerAddress? HelperAddress,
        bool InviteValidated,
        SessionId? SessionId,
        CapabilityGrant RequestedCapabilities);
}
