using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.DevLocal;

// DEV ONLY: local machine named-pipe transport for testing two app instances without real networking.
public sealed class DevLocalTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IScreenShareSignalingTransport
{
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
    private const string ScreenSharePayloadFrameType = "screenshare_payload";
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
    private readonly ScreenShareFrameReassembler screenShareFrameReassembler = new();
    private const bool LocalRemoteControlSupported = true;
    private readonly string localPeerAddress;
    private SessionConnection? activeConnection;
    private PendingOutboundHandshakeState? pendingOutboundHandshake;
    private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
    private TaskCompletionSource<bool> hostReadyTcs = CreateHostReadyTcs();
    private bool remoteSupportsRemoteControl;
    private bool disposed;

    public DevLocalTransport(string? localPeerAddress = null)
    {
        this.localPeerAddress = NormalizeLocalPeerAddress(localPeerAddress);
        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        screenShareFrameReassembler.FrameReady += OnScreenShareFrameReady;
    }

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

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

    public bool LocalSupportsRemoteControl => LocalRemoteControlSupported;
    public string LocalPeerAddress => localPeerAddress;
    public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
    public bool RemoteSupportsRemoteControl => remoteSupportsRemoteControl;
    public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;

    public void Dispose()
    {
        disposed = true;
        TryCancelHostReady();
        ClearActiveConnection()?.Dispose();
        screenShareFrameReassembler.FrameReady -= OnScreenShareFrameReady;
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
                catch
                {
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
        pendingOutboundHandshake = outboundHandshake;
        UpdateSessionSecurityState(SessionSecurityState.CreateHelperPending(
            outboundHandshake.SessionId,
            outboundHandshake.HelpeeAddress,
            outboundHandshake.HelperAddress,
            outboundHandshake.InviteValidated));
        var cancelRegistration = ct.Register(() => ClearActiveConnection()?.Dispose());
        SessionConnection? connection = null;

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

            await connection.WriteFrameAsync(
                new TransportFrame
                {
                    Type = JoinFrameType,
                    Data = Convert.ToBase64String(helperKeyPair.PublicKey),
                    HelperAddress = localPeerAddress,
                    RemoteControlSupported = LocalSupportsRemoteControl,
                },
                ct);

            await helloReceived.Task.WaitAsync(ct);
            await SendHandshakeStartAsync(connection, outboundHandshake, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnDisconnected();
        }
        catch (TimeoutException)
        {
            OnDisconnected();
        }
        catch (IOException)
        {
            OnDisconnected();
        }
        catch
        {
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
                Data = Convert.ToBase64String(payload.Span),
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

    public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
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
                Type = ScreenSharePayloadFrameType,
                Data = Convert.ToBase64String(payload.Span),
            },
            ct);
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
                Data = Convert.ToBase64String(payload),
            },
            ct);
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
                if (joinFrame is null || !string.Equals(joinFrame.Type, JoinFrameType, StringComparison.Ordinal))
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
                    rejectAsync: token => RejectHostJoinAsync(connection, approvalRequest, token),
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
                        !SessionHandshakeProtocol.TryDeserializeApprovalDecision(payloadBytes, out var decision) ||
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
                        SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(pendingSessionKey));
                    }

                    pendingOutboundHandshake = null;
                    SafeRaiseApproved();
                    continue;
                }

                if (string.Equals(frame.Type, RejectFrameType, StringComparison.Ordinal))
                {
                    rejected = true;
                    pendingOutboundHandshake = null;
                    UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Invalidated, "join_rejected"));
                    SafeRaiseRejected();
                    connection.Dispose();
                    break;
                }

                if (string.Equals(frame.Type, ChatFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        SafeRaiseChatMessageReceived(payloadBytes);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePayloadFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        HandleScreenSharePayload(payloadBytes);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlRequest(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_request", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlResponseFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlResponse(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_response", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlResponseReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStartFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStart(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_start", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStartReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStopFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStop(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_stop", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStopReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlInputFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlInput(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_input", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlInputReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlAckFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlAck(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_ack", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlAckReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStateSnapshotFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_state_snapshot", message.SessionId, message.RequestId))
                    {
                        SafeRaiseRemoteControlStateSnapshotReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlDisplayInfoFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(payloadBytes, out var message) &&
                        TryValidateControlMessageSession("control_display_info", message.SessionId, requestId: null))
                    {
                        SafeRaiseRemoteControlDisplayInfoReceived(message);
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
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        SafeRaiseChatMessageReceived(payloadBytes);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ScreenSharePayloadFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes))
                    {
                        HandleScreenSharePayload(payloadBytes);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlRequestFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlRequest(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlRequestReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlResponseFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlResponse(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlResponseReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStartFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStart(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlStartReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStopFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStop(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlStopReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlInputFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlInput(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlInputReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlAckFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlAck(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlAckReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlStateSnapshotFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlStateSnapshotReceived(message);
                    }

                    continue;
                }

                if (string.Equals(frame.Type, ControlDisplayInfoFrameType, StringComparison.Ordinal))
                {
                    if (TryGetPayloadBytes(frame, out var payloadBytes) &&
                        RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(payloadBytes, out var message))
                    {
                        SafeRaiseRemoteControlDisplayInfoReceived(message);
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

        await connection.WriteFrameAsync(
            CreateHandshakeFrame(ApproveFrameType, SessionHandshakeProtocol.Serialize(decision)),
            ct);
        UpdateSessionSecurityState(currentSessionSecurityState.WithApproval(decision.ToGrant()));
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=approval_granted; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; capabilities={decision.ApprovedCapabilities}; expires_at_utc={decision.ExpiresAtUtc:O}");
        SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
        SafeRaiseApproved();
    }

    private async Task RejectHostJoinAsync(SessionConnection connection, ApprovalRequest? approvalRequest, CancellationToken ct)
    {
        if (currentSessionSecurityState.SessionId is SessionId sessionId &&
            currentSessionSecurityState.HelperAddress is PeerAddress helperAddress)
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=approval_denied; reason=local_reject; session_id={sessionId.Value}; helper_identity={helperAddress.Value}; requested_capabilities={approvalRequest?.RequestedCapabilities ?? CapabilityGrant.None}");
        }

        await connection.WriteFrameAsync(new TransportFrame { Type = RejectFrameType }, ct);
        SafeRaiseRejected();
        connection.Dispose();
    }

    private void OnDisconnected()
    {
        pendingOutboundHandshake = null;
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
        if (ScreenSharePayloadCodec.TryDeserialize(payloadBytes, out var chunk))
        {
            if (TryValidateScreenShareMessageSession("frame", chunk.SessionId))
            {
                screenShareFrameReassembler.OnChunk(chunk);
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

    private void OnScreenShareFrameReady(object? sender, ScreenShareFrameReadyEventArgs e)
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
                    e.TimestampUnixMilliseconds,
                    SessionId: e.SessionId));
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

    private string ResolveControlSessionId(string? current)
    {
        return string.IsNullOrWhiteSpace(current)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : current.Trim();
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

    private void UpdateSessionSecurityState(SessionSecurityState nextState)
    {
        if (nextState is null)
        {
            throw new ArgumentNullException(nameof(nextState));
        }

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
        SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
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

        [JsonPropertyName("remoteControlSupported")]
        public bool? RemoteControlSupported { get; init; }
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
