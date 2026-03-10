using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

#pragma warning disable CS0067
public sealed class NknSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, IFileTransferSignalingTransport, IFileTransferChunkBudgetProvider
{
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan AckWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingJoinTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ControlInputReceiveLogWindow = TimeSpan.FromSeconds(1);
    private const int ControlInputReceiveLogBurst = 5;
    // Transport-local abuse bounds. Hard payload-size ceilings still also exist
    // below this layer in the bridge/session envelope/screen-share codecs.
    private const int HighPriorityControlLaneCapacity = 256;
    private const int LowPriorityControlLaneCapacity = 256;
    private static readonly TimeSpan[] AckRetryDelays =
    {
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromMilliseconds(2700),
    };
    private static readonly TimeSpan[] ScreenShareStopRetryDelays =
    {
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(220),
    };
    private static readonly TimeSpan ScreenShareOutboundGateWaitBudget = TimeSpan.FromMilliseconds(25);
    private const int FileTransferMaxBridgePayloadBytes = 64 * 1024;

    private readonly NknTransportOptions options;
    private readonly NknIdentity identity;
    private readonly INknClient client;
    private readonly IInviteTokenValidator inviteTokenValidator;
    private readonly IInviteValidationThrottle inviteValidationThrottle;
    private readonly ISessionHandshakeReplayCache handshakeReplayCache;
    private readonly LruMessageIdCache seenMessageIds = new(500);
    private readonly ConcurrentDictionary<string, PendingAckWait> pendingAcks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outboundSendGate = new(1, 1);
    private readonly object controlOutboundQueueGate = new();
    private readonly object controlInputReceiveLogGate = new();
    private readonly object hostReadyGate = new();
    private readonly object inboundFileTransferDispatchGate = new();
    private readonly LinkedList<QueuedControlEnvelope> highPriorityControlOutboundQueue = new();
    private readonly LinkedList<QueuedControlEnvelope> lowPriorityControlOutboundQueue = new();
    private readonly object gate = new();
    private readonly object controlSecureStateGate = new();
    private readonly ScreenShareFrameReassembler secureScreenShareFrameReassembler = new();
    private readonly SessionReplayWindow inboundControlReplayWindow = new();
    private readonly SessionReplayWindow inboundLifecycleReplayWindow = new();
    private readonly SessionReplayWindow inboundScreenShareReplayWindow = new();
    private readonly SessionReplayWindow inboundFileTransferReplayWindow = new(
        windowSize: FileTransferInboundReplayWindowSize,
        maxForwardAdvance: FileTransferInboundReplayMaxForwardAdvance);
    private readonly Dictionary<string, FileTransferTransportState> fileTransferStates = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, InboundFileTransferDispatchWork> pendingInboundFileTransferDispatch = new();
    private const bool LocalRemoteControlSupported = true;
    private const int FileTransferInboundReplayWindowSize = 32768;
    private const long FileTransferInboundReplayMaxForwardAdvance = 131072;

    private string? currentEnvelopeCode;
    private string? remoteEndpoint;
    private string? lastPeerAddress;
    private SessionEcdhKeyPair? helpeeHostEcdhKeyPair;
    private SessionEcdhKeyPair? helperJoinEcdhKeyPair;
    private string? helperJoinRequestMessageId;
    private PendingJoinRequestState? pendingJoinRequest;
    private PendingInboundHandshakeState? pendingInboundHandshake;
    private PendingOutboundHandshakeState? pendingOutboundHandshake;
    private Timer? pendingInboundHandshakeTimeoutTimer;
    private long pendingInboundHandshakeTimeoutGeneration;
    private bool remoteSupportsRemoteControl;
    private RemoteControlSessionState transportRemoteControlState = RemoteControlSessionState.Default;
    private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
    private LinkedListNode<QueuedControlEnvelope>? queuedLowPriorityMouseMoveNode;
    private bool controlOutboundDrainerActive;
    private long lowLaneDroppedMoves;
    private long lowLaneEnqueuedMoves;
    private int lowLaneMaxDepthSeen;
    private long controlInputReceiveLogWindowStartTicks;
    private int controlInputReceiveLogCount;
    private int controlInputReceiveLogSuppressed;
    private long screenShareOutboundBusyDrops;
    private long screenSharePayloadBytesSent;
    private long screenShareMessagesSent;
    private long highPriorityControlQueueOverflowCount;
    private long highPriorityControlRejectedCount;
    private long highPriorityControlCoalescedCount;
    private long highPriorityControlDroppedForStopCount;
    private byte[]? controlSessionSharedKey;
    private byte[]? fileTransferSessionSharedKey;
    private long nextOutboundControlSecureSequence;
    private long nextOutboundLifecycleSecureSequence;
    private long nextOutboundScreenShareSecureSequence;
    private long nextOutboundFileTransferSecureSequence;
    private long nextInboundFileTransferDispatchOrder;
    private long nextInboundFileTransferDispatchToProcess = 1;
    private long inboundFileTransferDispatchGeneration;
    private bool inboundFileTransferDispatchActive;
    private TaskCompletionSource<bool> hostReadyTcs = CreateHostReadyTcs();
    private bool disposed;

    public NknSignalingTransport()
    {
        options = NknTransportOptions.Load();
        identity = NknIdentityStore.LoadOrCreate(options);
        client = new RealNknClientAdapter(identity, options);
        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        secureScreenShareFrameReassembler.FrameReady += OnSecureScreenShareFrameReady;
        SubscribeClientEvents();

        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);

        Log($"Initialized | {SensitiveDataRedactor.FormatStructuredFields(" | ", ("address", identity.Address), ("identifier", identity.Identifier))}");
    }

    internal NknSignalingTransport(INknClient client, NknTransportOptions options, NknIdentity identity)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        inviteTokenValidator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        inviteValidationThrottle = InviteTokenServiceFactory.CreateInviteValidationThrottle();
        handshakeReplayCache = new InMemorySessionHandshakeReplayCache();
        secureScreenShareFrameReassembler.FrameReady += OnSecureScreenShareFrameReady;
        SubscribeClientEvents();
    }

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;
    public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

    public event EventHandler? RemoteSessionEnded;
    public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
    public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
    public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
    public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
    public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
    public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
    public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
    public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
    public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
    public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
    public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
    public event EventHandler<FileTransferStartReceivedEventArgs>? FileTransferStartReceived;
    public event EventHandler<FileTransferChunkReceivedEventArgs>? FileTransferChunkReceived;
    public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;

    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;
    public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    public event EventHandler? ScreenShareStopped;

    public string LocalPeerAddress => string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address;
    public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
    public bool CanSendSessionEnd => !disposed && !string.IsNullOrWhiteSpace(currentEnvelopeCode) && !string.IsNullOrWhiteSpace(remoteEndpoint);
    public bool CanSendPendingJoinCancel
    {
        get
        {
            lock (gate)
            {
                return !disposed &&
                       pendingOutboundHandshake?.HelpeeEcdhPublicKey is not null &&
                       helperJoinEcdhKeyPair is not null &&
                       !string.IsNullOrWhiteSpace(helperJoinRequestMessageId) &&
                       !string.IsNullOrWhiteSpace(currentEnvelopeCode) &&
                       !string.IsNullOrWhiteSpace(remoteEndpoint);
            }
        }
    }
    public bool LocalSupportsRemoteControl => LocalRemoteControlSupported;
    public bool RemoteSupportsRemoteControl => remoteSupportsRemoteControl;
    public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;
    internal long LowLaneDroppedMoves => Interlocked.Read(ref lowLaneDroppedMoves);
    internal long LowLaneEnqueuedMoves => Interlocked.Read(ref lowLaneEnqueuedMoves);
    internal int LowLaneMaxDepthSeen => Volatile.Read(ref lowLaneMaxDepthSeen);
    internal long ScreenShareOutboundBusyDrops => Interlocked.Read(ref screenShareOutboundBusyDrops);
    internal long ScreenSharePayloadBytesSent => Interlocked.Read(ref screenSharePayloadBytesSent);
    internal long ScreenShareMessagesSent => Interlocked.Read(ref screenShareMessagesSent);
    internal long NextOutboundFileTransferSecureSequence => Interlocked.Read(ref nextOutboundFileTransferSecureSequence);
    internal long HighPriorityControlQueueOverflowCount => Interlocked.Read(ref highPriorityControlQueueOverflowCount);
    internal long HighPriorityControlRejectedCount => Interlocked.Read(ref highPriorityControlRejectedCount);
    internal long HighPriorityControlCoalescedCount => Interlocked.Read(ref highPriorityControlCoalescedCount);
    internal long HighPriorityControlDroppedForStopCount => Interlocked.Read(ref highPriorityControlDroppedForStopCount);

    public Task WaitUntilHostReadyAsync(CancellationToken ct)
    {
        Task readyTask;
        lock (hostReadyGate)
        {
            readyTask = hostReadyTcs.Task;
        }

        return readyTask.WaitAsync(ct);
    }

    private enum ControlOutboundLane
    {
        High = 0,
        Low = 1,
    }

    public async Task<bool> TryPingBridgeHealthAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (client is not RealNknClientAdapter realClient)
        {
            return false;
        }

        try
        {
            await realClient.PingBridgeAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"TryPingBridgeHealthAsync failed ({ex.GetType().Name})");
            return false;
        }
    }

    internal Task PrepareForReuseAsync()
    {
        ThrowIfDisposed();

        CancelPendingAcks();
        CancelPendingInboundHandshakeTimeout();
        ResetSessionTracking();
        seenMessageIds.Clear();
        currentEnvelopeCode = null;
        lastPeerAddress = null;

        Log("Prepared for reuse");
        return Task.CompletedTask;
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

    private void TryFailHostReady(Exception ex)
    {
        lock (hostReadyGate)
        {
            hostReadyTcs.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryCancelHostReady();

        // Avoid treating normal cleanup as a disconnection.
        client.MessageReceived -= OnClientMessageReceived;
        client.Disconnected -= OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle -= OnBridgeLifecycle;
            realClient.ScreenShareFrameCompleted -= OnScreenShareFrameCompleted;
            realClient.ScreenShareStopped -= OnScreenShareStopped;
        }

        CleanupAsync().GetAwaiter().GetResult();
        DisposeEphemeralKeyState();
        client.Dispose();
        Log("Disposed");
    }

    public async Task HostByAddressAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ResetHostReady();

        ResetSessionTracking();

        seenMessageIds.Clear();
        ReplaceHelpeeHostKeyPair(CreateSessionEcdhKeyPair());

        try
        {
            SetAdapterReliabilityModeHint("Helpee");
            SessionReliabilityLog.RecordStandalone("Helpee", "NKN", SessionReliabilityStage.DiscoveryStarted);
            await client.ConnectAsync(ct);
            NknRuntimeDiagnostics.SetIdentity(
                address: string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address,
                identifier: identity.Identifier,
                keyPath: options.KeyPath,
                seedRpc: options.SeedRpc);
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(LocalPeerAddress)));
            TrySetHostReady();
            Log("HostByAddressAsync ready (address-native)");
        }
        catch (Exception ex)
        {
            TryFailHostReady(ex);
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"HostByAddressAsync failed ({ex.GetType().Name})");
            throw;
        }
    }

    public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
    {
        return Task.FromException(new NotSupportedException("Raw address helper connect is disabled for NKN. Use invite-targeted connect."));
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
            Log($"JoinByInviteAsync rejected before join (result={validation.Result}, parse={validation.ParseError})");
            throw new InvalidOperationException(validation.Message ?? "Invite token is invalid.");
        }

        if (validation.Invite.SessionId != invite.SessionId ||
            validation.Invite.TargetAddress != invite.TargetAddress ||
            validation.Invite.IssuerAddress != invite.IssuerAddress)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_binding_mismatch"));
            Log("JoinByInviteAsync rejected before join (reason=invite_binding_mismatch)");
            throw new InvalidOperationException("Invite token does not match the provided invite context.");
        }

        var hasConnectedHelperAddress =
            client is IAuthoritativeConnectedAddressSource authoritativeConnectedAddressSource
                ? authoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress
                : !string.IsNullOrWhiteSpace(client.Address);
        var helperAddress = new PeerAddress(hasConnectedHelperAddress ? client.Address : identity.Address);
        if (InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites() &&
            validation.Invite.BoundHelperAddress is null)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_required"));
            Log("JoinByInviteAsync rejected before join (reason=invite_helper_required)");
            throw new InvalidOperationException("Invite token must be bound to the verified helper identity.");
        }

        if (validation.Invite.BoundHelperAddress is not null &&
            hasConnectedHelperAddress &&
            !AddressesLikelySamePeer(validation.Invite.BoundHelperAddress.Value.Value, helperAddress.Value))
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_mismatch"));
            Log("JoinByInviteAsync rejected before join (reason=invite_helper_mismatch)");
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

        ResetSessionTracking();

        var sessionContextCode = CreateAddressSessionContextCode();
        currentEnvelopeCode = sessionContextCode;
        seenMessageIds.Clear();
        ReplaceHelperJoinKeyPair(CreateSessionEcdhKeyPair());
        pendingOutboundHandshake = outboundHandshake;

        var destination = peerAddress.Trim();

        try
        {
            SetAdapterReliabilityModeHint("Helper");
            SessionReliabilityLog.RecordStandalone("Helper", "NKN", SessionReliabilityStage.DiscoveryStarted);
            await client.ConnectAsync(ct);
            NknRuntimeDiagnostics.SetIdentity(
                address: string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address,
                identifier: identity.Identifier,
                keyPath: options.KeyPath,
                seedRpc: options.SeedRpc);
            var effectiveHelperAddress = new PeerAddress(string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address);
            pendingOutboundHandshake = outboundHandshake with { HelperAddress = effectiveHelperAddress };
            UpdateSessionSecurityState(SessionSecurityState.CreateHelperPending(
                outboundHandshake.SessionId,
                outboundHandshake.HelpeeAddress,
                effectiveHelperAddress,
                outboundHandshake.InviteValidated));

            remoteEndpoint = destination;
            lastPeerAddress = destination;
            SessionTimeline.Record("DiscoveryFound");
            Log($"JoinByAddressAsync target accepted (endpoint_len={destination.Length})");

            var helperKeyPair = GetHelperJoinKeyPairOrThrow();

            var joinPayload = new JoinRequestPayload
            {
                helperEndpoint = effectiveHelperAddress.Value,
                helperIdentifier = identity.Identifier,
                helperEcdhPublicKey = Convert.ToBase64String(helperKeyPair.PublicKey),
                remoteControlSupported = LocalSupportsRemoteControl,
            };

            var joinEnvelope = CreateEnvelope(
                sessionContextCode,
                MsgType.JoinRequest,
                JsonSerializer.SerializeToUtf8Bytes(joinPayload),
                replyTo: null);

            helperJoinRequestMessageId = joinEnvelope.MessageId;
            await SendEnvelopeWithAckRetryAsync(destination, joinEnvelope, ct);
            await SendHandshakeStartAsync(destination, pendingOutboundHandshake ?? outboundHandshake, ct).ConfigureAwait(false);
            SessionTimeline.Record("JoinRequestSent");
            SessionReliabilityLog.RecordStandalone("Helper", "NKN", SessionReliabilityStage.DiscoveryFoundHost);
            SessionReliabilityLog.RecordStandalone("Helper", "NKN", SessionReliabilityStage.JoinRequestSent);
            Log($"JoinByAddressAsync sent JoinRequest with Ack (msg_id={joinEnvelope.MessageId})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"JoinByAddressAsync failed ({ex.GetType().Name})");
            throw;
        }
    }

    public async Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("chat_no_session_context");
            Log($"SendChatMessageAsync failed (payload_len={payload.Length}, reason=no_session_context)");
            throw new InvalidOperationException("No active session context.");
        }

        var destination = remoteEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError("chat_no_remote_endpoint");
            Log($"SendChatMessageAsync failed (payload_len={payload.Length}, reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var envelope = CreateEnvelope(envelopeCode, MsgType.Chat, payload.ToArray(), replyTo: null);
        await SendEnvelopeWithAckRetryAsync(destination, envelope, ct);
        Log($"SendChatMessageAsync sent Chat with Ack (payload_len={payload.Length}, msg_id={envelope.MessageId})");
    }

    public async Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryParseScreenSharePayload(payload.Span, out var messageType, out var messageSessionId))
        {
            LogScreenShareRejected("send", "payload_invalid", sessionId: null);
            return;
        }

        if (!TryValidateScreenShareSession(messageType, messageSessionId) ||
            !IsScreenShareAuthorizedForDispatch(messageType, messageSessionId))
        {
            return;
        }

        var destination = remoteEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_no_remote_endpoint");
            Log($"SendScreenSharePayloadAsync failed (payload_len={payload.Length}, reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var isStopPayload = string.Equals(messageType, "stop", StringComparison.Ordinal);
        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_session_context_unavailable");
            Log($"SendScreenSharePayloadAsync failed (payload_len={payload.Length}, reason=no_session_context)");
            throw new InvalidOperationException("Session context is not known yet.");
        }

        if (isStopPayload)
        {
            var securePayload = CreateSecureScreenSharePayload(MsgType.ScreenShareStop, payload.ToArray());
            var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareStop, securePayload, replyTo: null);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_send_requested; session_id={messageSessionId}; payload_len={payload.Length}");
            await SendScreenShareStopEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            SendScreenShareStopEnvelopeRetriesFireAndForget(destination, envelope, messageSessionId);
        }
        else
        {
            var securePayload = CreateSecureScreenSharePayload(MsgType.ScreenShareFrame, payload.ToArray());
            var envelope = CreateEnvelope(envelopeCode, MsgType.ScreenShareFrame, securePayload, replyTo: null);
            var transportPayload = EnvelopeCodec.Serialize(envelope);
            if (!await SendScreenShareTransportPayloadAsync(destination, transportPayload, waitForOutboundGate: false, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref screenShareOutboundBusyDrops);
                NknRuntimeDiagnostics.IncrementScreenShareOutboundBusyDrops();
                Log($"SendScreenSharePayloadAsync dropped (payload_len={payload.Length}, reason=outbound_busy)");
                return;
            }
        }

        Log($"SendScreenSharePayloadAsync sent screenshare payload (payload_len={payload.Length})");
    }

    private async Task<bool> SendScreenShareTransportPayloadAsync(
        string destination,
        ReadOnlyMemory<byte> payload,
        bool waitForOutboundGate,
        CancellationToken ct)
    {
        if (waitForOutboundGate)
        {
            await outboundSendGate.WaitAsync(ct).ConfigureAwait(false);
        }
        else if (!await TryAcquireScreenShareOutboundGateAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await client.SendAsync(destination, payload.ToArray(), ct).ConfigureAwait(false);
            Interlocked.Increment(ref screenShareMessagesSent);
            Interlocked.Add(ref screenSharePayloadBytesSent, payload.Length);
            NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
            NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(payload.Length);
            return true;
        }
        finally
        {
            outboundSendGate.Release();
        }
    }

    private async Task<bool> TryAcquireScreenShareOutboundGateAsync(CancellationToken ct)
    {
        if (await outboundSendGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return true;
        }

        return await outboundSendGate.WaitAsync(ScreenShareOutboundGateWaitBudget, ct).ConfigureAwait(false);
    }

    private async Task SendScreenShareStopEnvelopeAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        FlushLowPriorityControlOutboundQueue("screenshare_stop");
        var payload = EnvelopeCodec.Serialize(envelope);
        await SendScreenShareTransportPayloadAsync(destination, payload, waitForOutboundGate: true, ct).ConfigureAwait(false);
    }

    private void SendScreenShareStopEnvelopeRetriesFireAndForget(string destination, Envelope envelope, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in ScreenShareStopRetryDelays)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

                    if (disposed ||
                        !string.Equals(currentSessionSecurityState.SessionId?.Value, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(remoteEndpoint, destination, StringComparison.Ordinal))
                    {
                        return;
                    }

                    await SendScreenShareStopEnvelopeAsync(destination, envelope, CancellationToken.None).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_stop_envelope_resend_dispatched; session_id={sessionId ?? "(none)"}; msg_id={envelope.MessageId}; delay_ms={delay.TotalMilliseconds:F0}");
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                LocalOperationalLog.Warn(
                    "ScreenShareTransport",
                    $"event=screenshare_stop_envelope_resend_failed; session_id={sessionId ?? "(none)"}; msg_id={envelope.MessageId}; ex={ex.GetType().Name}");
                Log($"SendScreenSharePayloadAsync stop envelope resend failed (msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
            }
        });
    }

    public async Task SendSessionEndAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode) ||
            string.IsNullOrWhiteSpace(remoteEndpoint) ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return;
        }

        var payload = CreateSecureLifecyclePayload(
            MsgType.SessionEnd,
            requestId: null,
            JsonSerializer.SerializeToUtf8Bytes(new SessionEndPayload
            {
                sessionId = sessionId.Value,
                reason = "user_exit",
            }));

        var envelope = CreateEnvelope(envelopeCode, MsgType.SessionEnd, payload, replyTo: null);
        SessionTimeline.Record("SessionEndSent");
        await SendEnvelopeWithAckRetryAsync(remoteEndpoint, envelope, ct);
        Log($"SendSessionEndAsync sent SessionEnd with Ack (msg_id={envelope.MessageId})");
    }

    public async Task SendPendingJoinCancelAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        PendingOutboundHandshakeState? pending;
        SessionEcdhKeyPair? helperKeyPair;
        string? joinRequestMessageId;
        string? destination;
        string? envelopeCode;
        lock (gate)
        {
            pending = pendingOutboundHandshake;
            helperKeyPair = helperJoinEcdhKeyPair;
            joinRequestMessageId = helperJoinRequestMessageId;
            destination = remoteEndpoint;
            envelopeCode = currentEnvelopeCode;
        }

        if (pending?.HelpeeEcdhPublicKey is null ||
            helperKeyPair is null ||
            string.IsNullOrWhiteSpace(joinRequestMessageId) ||
            string.IsNullOrWhiteSpace(destination) ||
            string.IsNullOrWhiteSpace(envelopeCode))
        {
            return;
        }

        byte[] sharedKey;
        try
        {
            sharedKey = DeriveSessionKey(helperKeyPair, pending.HelpeeEcdhPublicKey, envelopeCode);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            NknRuntimeDiagnostics.SetLastError("reject_key_derivation_failed");
            Log($"SendPendingJoinCancelAsync key derivation failed (join_msg_id={joinRequestMessageId}, ex={ex.GetType().Name})");
            return;
        }

        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            sharedKey,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.Lifecycle,
                MessageType: "reject",
                SessionId: pending.SessionId,
                SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
                Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
                RequestId: joinRequestMessageId),
            JsonSerializer.SerializeToUtf8Bytes(new RejectSecurePayload
            {
                reason = "helper_cancelled",
            }));

        var rejectPayload = new RejectPayload
        {
            sessionId = pending.SessionId.Value,
            helpeeEcdhPublicKey = Convert.ToBase64String(pending.HelpeeEcdhPublicKey),
            secureEnvelopeBase64 = Convert.ToBase64String(securePayload),
        };
        var envelope = CreateEnvelope(
            envelopeCode,
            MsgType.Reject,
            JsonSerializer.SerializeToUtf8Bytes(rejectPayload),
            joinRequestMessageId);
        await SendEnvelopeWithAckRetryAsync(destination, envelope, ct);
        Log($"SendPendingJoinCancelAsync sent Reject with Ack (msg_id={envelope.MessageId}, reply_to={envelope.ReplyTo})");
    }

    public async Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_no_session_context");
            Log("SendControlRequestAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_no_remote_endpoint");
            Log("SendControlRequestAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlRequest,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlRequest, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlRequestAsync sent ControlRequest (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length})");
    }

    public async Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_no_session_context");
            Log("SendControlResponseAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_no_remote_endpoint");
            Log("SendControlResponseAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlResponse,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlResponse, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlResponseAsync sent ControlResponse (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, decision={message.Decision})");
    }

    public async Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_no_session_context");
            Log("SendControlStartAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_no_remote_endpoint");
            Log("SendControlStartAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStart,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStart, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlStartAsync sent ControlStart (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length})");
    }

    public async Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_no_session_context");
            Log("SendControlStopAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_no_remote_endpoint");
            Log("SendControlStopAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStop,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStop, payload, replyTo: null);
        FlushLowPriorityControlOutboundQueue("control_stop");
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        Log($"SendControlStopAsync sent ControlStop (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, has_reason={message.Reason is not null})");
    }

    public async Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_no_session_context");
            Log("SendControlInputAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_no_remote_endpoint");
            Log("SendControlInputAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlInput,
            message.RequestId,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlInput, payload, replyTo: null);
        var isMouseMove = IsLowPriorityControlInput(message);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlInput, isLowPriorityMouseMove: isMouseMove),
                ct,
                isLowPriorityMouseMove: isMouseMove)
            .ConfigureAwait(false);
        Log($"SendControlInputAsync sent ControlInput (msg_id={envelope.MessageId}, request_id_len={message.RequestId.Length}, kind={message.Kind}, seq={message.Seq})");
    }

    public async Task SendControlAckAsync(ControlInputAckV1 ack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ack);
        ThrowIfDisposed();
        ack = EnsureControlSessionId(ack);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_no_session_context");
            Log("SendControlAckAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_no_remote_endpoint");
            Log("SendControlAckAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlAck,
            ack.RequestId,
            RemoteControlPayloadCodec.Serialize(ack));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlAck, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlAck),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlAckAsync sent ControlAck (msg_id={envelope.MessageId}, request_id_len={ack.RequestId.Length}, ack_seq={ack.AckSeq})");
    }

    public async Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureControlSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_no_session_context");
            Log("SendControlDisplayInfoAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_no_remote_endpoint");
            Log("SendControlDisplayInfoAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlDisplayInfo,
            requestId: null,
            RemoteControlPayloadCodec.Serialize(message));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlDisplayInfo, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlDisplayInfo),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlDisplayInfoAsync sent ControlDisplayInfo (msg_id={envelope.MessageId}, display_id_len={message.DisplayId.Length}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight})");
    }

    public async Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();
        snapshot = EnsureControlSessionId(snapshot);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_no_session_context");
            Log("SendControlStateSnapshotAsync failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_no_remote_endpoint");
            Log("SendControlStateSnapshotAsync failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var payload = CreateSecureControlPayload(
            MsgType.ControlStateSnapshot,
            snapshot.RequestId,
            RemoteControlPayloadCodec.Serialize(snapshot));
        var envelope = CreateEnvelope(envelopeCode, MsgType.ControlStateSnapshot, payload, replyTo: null);
        await QueueControlEnvelopeAsync(
                remoteEndpoint,
                envelope,
                ResolveControlOutboundLane(MsgType.ControlStateSnapshot),
                ct)
            .ConfigureAwait(false);
        Log($"SendControlStateSnapshotAsync sent ControlStateSnapshot (msg_id={envelope.MessageId}, request_id_len={snapshot.RequestId.Length}, seq={snapshot.Seq}, buttons_mask={snapshot.MouseButtonsMask}, modifiers_mask={snapshot.ModifiersMask})");
    }

    public async Task SendFileTransferOfferAsync(FileTransferOfferV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferOffer,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferAccept,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferDecline,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferStartAsync(FileTransferStartV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferStart,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferChunkAsync(FileTransferChunkV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferChunk,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferCancel,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferError,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferComplete,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public int ResolveSafeOutboundChunkSize(FileTransferChunkBudgetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "File size must be positive.");
        }

        var requestedChunkSize = Math.Min(request.RequestedChunkSizeBytes, FileTransferProtocol.MaxChunkRawBytes);
        if (requestedChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Requested chunk size must be positive.");
        }

        var low = 1;
        var high = requestedChunkSize;
        var best = 0;
        while (low <= high)
        {
            var candidate = low + ((high - low) / 2);
            if (!TryCalculateFileTransferChunkCount(request.FileSizeBytes, candidate, out var chunkCount))
            {
                throw new InvalidOperationException("Couldn't determine outbound file-transfer chunk count.");
            }

            if (DoesFileTransferChunkFitTransportBudget(request.TransferId, chunkCount, candidate))
            {
                best = candidate;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (best <= 0)
        {
            throw new InvalidOperationException("No valid file-transfer chunk size fits within the NKN payload budget.");
        }

        return best;
    }

    private static ControlOutboundLane ResolveControlOutboundLane(MsgType messageType, bool isLowPriorityMouseMove = false)
    {
        return messageType switch
        {
            MsgType.ControlInput when isLowPriorityMouseMove => ControlOutboundLane.Low,
            // DisplayInfoChanged is represented as ControlDisplayInfo with a newer revision.
            MsgType.ControlDisplayInfo => ControlOutboundLane.High,
            MsgType.ControlRequest or
                MsgType.ControlResponse or
                MsgType.ControlStart or
                MsgType.ControlStop or
                MsgType.ControlAck or
                MsgType.ControlStateSnapshot => ControlOutboundLane.High,
            _ => ControlOutboundLane.High,
        };
    }

    private async Task SendFileTransferEnvelopeAsync(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        CancellationToken ct)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_no_session_context");
            Log($"SendFileTransfer{messageType}Async failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_no_remote_endpoint");
            Log($"SendFileTransfer{messageType}Async failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryValidateAndTrackFileTransferMessage(messageType, normalizedTransferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_{failureReason}");
            Log($"SendFileTransfer{messageType}Async failed (reason={failureReason}, transfer_id={normalizedTransferId})");
            throw new InvalidOperationException($"File-transfer state rejected message '{messageType}': {failureReason}.");
        }

        var securePayload = CreateSecureFileTransferPayload(messageType, normalizedTransferId, plaintextPayload);
        var envelope = CreateEnvelope(envelopeCode, messageType, securePayload, replyTo: null);
        await SendEnvelopeAsync(remoteEndpoint, envelope, ct).ConfigureAwait(false);

        if (!TryValidateAndTrackFileTransferMessage(messageType, normalizedTransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={normalizedTransferId}; source={LocalPeerAddress}");
        }

        LogFileTransferEnvelopeEvent("sent", messageType, normalizedTransferId, source: LocalPeerAddress);
        Log($"SendFileTransfer{messageType}Async sent (msg_id={envelope.MessageId}, transfer_id_len={normalizedTransferId.Length})");
    }

    private bool DoesFileTransferChunkFitTransportBudget(string transferId, int chunkCount, int rawChunkSize)
    {
        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            throw new InvalidOperationException("Session security state does not have an active session id.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            throw new InvalidOperationException("Session context is not set.");
        }

        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var chunkIndex = Math.Max(0, chunkCount - 1);
        var plaintextPayload = FileTransferPayloadCodec.Serialize(
            new FileTransferChunkV1
            {
                SessionId = sessionId.Value,
                TransferId = normalizedTransferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                DataBase64 = Convert.ToBase64String(new byte[rawChunkSize]),
            });

        var securePayload = CreateSecureFileTransferPayloadForBudgetEstimate(
            MsgType.FileTransferChunk,
            normalizedTransferId,
            plaintextPayload);
        var envelope = new Envelope(
            Version: EnvelopeVersion,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.FileTransferChunk,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
        var transportPayload = EnvelopeCodec.Serialize(envelope);
        return transportPayload.Length <= FileTransferMaxBridgePayloadBytes;
    }

    private static bool TryCalculateFileTransferChunkCount(long fileSizeBytes, int chunkSizeBytes, out int chunkCount)
    {
        chunkCount = 0;
        if (fileSizeBytes <= 0 || chunkSizeBytes <= 0)
        {
            return false;
        }

        try
        {
            chunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes));
            return chunkCount > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private Task QueueControlEnvelopeAsync(
        string destination,
        Envelope envelope,
        ControlOutboundLane lane,
        CancellationToken ct,
        bool isLowPriorityMouseMove = false)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedControlEnvelope(destination, envelope, completion, ct, isLowPriorityMouseMove);
        List<TaskCompletionSource<bool>>? droppedCompletions = null;
        var shouldStartDrainer = false;
        var lowLaneDroppedCount = 0;
        var highLaneRejected = false;
        var highLaneCoalescedCount = 0;
        var highLaneDroppedCount = 0;

        lock (controlOutboundQueueGate)
        {
            if (lane == ControlOutboundLane.Low)
            {
                if (isLowPriorityMouseMove)
                {
                    Interlocked.Increment(ref lowLaneEnqueuedMoves);
                }

                if (isLowPriorityMouseMove && queuedLowPriorityMouseMoveNode is not null)
                {
                    droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                    droppedCompletions.Add(queuedLowPriorityMouseMoveNode.Value.Completion);
                    Interlocked.Increment(ref lowLaneDroppedMoves);
                    lowLaneDroppedCount++;
                    queuedLowPriorityMouseMoveNode.Value = queued;
                }
                else
                {
                    while (lowPriorityControlOutboundQueue.Count >= LowPriorityControlLaneCapacity)
                    {
                        var droppedNode = lowPriorityControlOutboundQueue.First;
                        if (droppedNode is null)
                        {
                            break;
                        }

                        lowPriorityControlOutboundQueue.RemoveFirst();
                        if (ReferenceEquals(droppedNode, queuedLowPriorityMouseMoveNode))
                        {
                            queuedLowPriorityMouseMoveNode = null;
                        }

                        droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                        droppedCompletions.Add(droppedNode.Value.Completion);
                        if (droppedNode.Value.IsLowPriorityMouseMove)
                        {
                            Interlocked.Increment(ref lowLaneDroppedMoves);
                        }
                        lowLaneDroppedCount++;
                    }

                    var inserted = lowPriorityControlOutboundQueue.AddLast(queued);
                    if (isLowPriorityMouseMove)
                    {
                        queuedLowPriorityMouseMoveNode = inserted;
                    }
                }

                if (lowPriorityControlOutboundQueue.Count > lowLaneMaxDepthSeen)
                {
                    lowLaneMaxDepthSeen = lowPriorityControlOutboundQueue.Count;
                }
            }
            else
            {
                if (TryCoalesceHighPriorityEnvelopeWhenFull(envelope, queued, ref droppedCompletions, out highLaneCoalescedCount))
                {
                    // Coalesced into an existing supersedable slot; count does not grow.
                    Interlocked.Increment(ref highPriorityControlQueueOverflowCount);
                    NknRuntimeDiagnostics.IncrementHighPriorityControlQueueOverflows();
                    Interlocked.Add(ref highPriorityControlCoalescedCount, highLaneCoalescedCount);
                    NknRuntimeDiagnostics.AddHighPriorityControlCoalesced(highLaneCoalescedCount);
                }
                else if (highPriorityControlOutboundQueue.Count >= HighPriorityControlLaneCapacity)
                {
                    Interlocked.Increment(ref highPriorityControlQueueOverflowCount);
                    NknRuntimeDiagnostics.IncrementHighPriorityControlQueueOverflows();
                    if (envelope.Type == MsgType.ControlStop)
                    {
                        var droppedNode = FindDroppableHighPriorityNodeForStop();
                        if (droppedNode is not null)
                        {
                            highPriorityControlOutboundQueue.Remove(droppedNode);
                            droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                            droppedCompletions.Add(droppedNode.Value.Completion);
                            highLaneDroppedCount++;
                            Interlocked.Increment(ref highPriorityControlDroppedForStopCount);
                            NknRuntimeDiagnostics.AddHighPriorityControlDroppedForStop(1);
                            highPriorityControlOutboundQueue.AddLast(queued);
                        }
                        else
                        {
                            highLaneRejected = true;
                            droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                            droppedCompletions.Add(queued.Completion);
                        }
                    }
                    else
                    {
                        highLaneRejected = true;
                        droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                        droppedCompletions.Add(queued.Completion);
                    }
                }
                else
                {
                    highPriorityControlOutboundQueue.AddLast(queued);
                }
            }

            if (!controlOutboundDrainerActive &&
                (highPriorityControlOutboundQueue.Count > 0 || lowPriorityControlOutboundQueue.Count > 0))
            {
                controlOutboundDrainerActive = true;
                shouldStartDrainer = true;
            }
        }

        if (droppedCompletions is not null)
        {
            foreach (var dropped in droppedCompletions)
            {
                dropped.TrySetResult(false);
            }

            if (lowLaneDroppedCount > 0)
            {
                Log($"Control outbound low lane dropped stale message(s) (count={lowLaneDroppedCount})");
            }
        }

        if (highLaneRejected)
        {
            Interlocked.Increment(ref highPriorityControlRejectedCount);
            NknRuntimeDiagnostics.IncrementHighPriorityControlRejected();
            Log($"Control outbound high lane rejected message at capacity (type={envelope.Type}, cap={HighPriorityControlLaneCapacity})");
        }
        else if (highLaneCoalescedCount > 0)
        {
            Log($"Control outbound high lane coalesced supersedable message(s) (type={envelope.Type}, count={highLaneCoalescedCount}, cap={HighPriorityControlLaneCapacity})");
        }
        else if (highLaneDroppedCount > 0)
        {
            Log($"Control outbound high lane dropped queued message(s) to prioritize stop (count={highLaneDroppedCount}, cap={HighPriorityControlLaneCapacity})");
        }

        if (shouldStartDrainer)
        {
            _ = Task.Run(DrainControlOutboundQueueAsync);
        }

        return completion.Task;
    }

    private bool TryCoalesceHighPriorityEnvelopeWhenFull(
        Envelope envelope,
        QueuedControlEnvelope queued,
        ref List<TaskCompletionSource<bool>>? droppedCompletions,
        out int replacedCount)
    {
        replacedCount = 0;
        if (!IsSupersedableHighPriorityType(envelope.Type) ||
            highPriorityControlOutboundQueue.Count < HighPriorityControlLaneCapacity)
        {
            return false;
        }

        var existingNode = highPriorityControlOutboundQueue.Last;
        while (existingNode is not null)
        {
            if (existingNode.Value.Envelope.Type == envelope.Type)
            {
                droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                droppedCompletions.Add(existingNode.Value.Completion);
                existingNode.Value = queued;
                replacedCount = 1;
                return true;
            }

            existingNode = existingNode.Previous;
        }

        return false;
    }

    private LinkedListNode<QueuedControlEnvelope>? FindDroppableHighPriorityNodeForStop()
    {
        var droppedNode = highPriorityControlOutboundQueue.First;
        while (droppedNode is not null && droppedNode.Value.Envelope.Type == MsgType.ControlStop)
        {
            droppedNode = droppedNode.Next;
        }

        return droppedNode ?? highPriorityControlOutboundQueue.First;
    }

    private static bool IsSupersedableHighPriorityType(MsgType type)
    {
        return type is MsgType.ControlDisplayInfo or MsgType.ControlStateSnapshot;
    }

    private async Task DrainControlOutboundQueueAsync()
    {
        while (true)
        {
            QueuedControlEnvelope queued;
            lock (controlOutboundQueueGate)
            {
                LinkedListNode<QueuedControlEnvelope>? nextNode = null;
                if (highPriorityControlOutboundQueue.First is not null)
                {
                    nextNode = highPriorityControlOutboundQueue.First;
                    highPriorityControlOutboundQueue.RemoveFirst();
                }
                else if (lowPriorityControlOutboundQueue.First is not null)
                {
                    nextNode = lowPriorityControlOutboundQueue.First;
                    lowPriorityControlOutboundQueue.RemoveFirst();
                    if (ReferenceEquals(nextNode, queuedLowPriorityMouseMoveNode))
                    {
                        queuedLowPriorityMouseMoveNode = null;
                    }
                }
                else
                {
                    controlOutboundDrainerActive = false;
                    return;
                }

                queued = nextNode.Value;
            }

            if (queued.CancellationToken.IsCancellationRequested)
            {
                queued.Completion.TrySetCanceled(queued.CancellationToken);
                continue;
            }

            try
            {
                await SendEnvelopeAsync(queued.Destination, queued.Envelope, queued.CancellationToken).ConfigureAwait(false);
                queued.Completion.TrySetResult(true);
            }
            catch (OperationCanceledException) when (queued.CancellationToken.IsCancellationRequested)
            {
                queued.Completion.TrySetCanceled(queued.CancellationToken);
            }
            catch (Exception ex)
            {
                queued.Completion.TrySetException(ex);
            }
        }
    }

    private void FlushLowPriorityControlOutboundQueue(string reason)
    {
        List<TaskCompletionSource<bool>>? droppedCompletions = null;

        lock (controlOutboundQueueGate)
        {
            while (lowPriorityControlOutboundQueue.First is not null)
            {
                var droppedNode = lowPriorityControlOutboundQueue.First;
                lowPriorityControlOutboundQueue.RemoveFirst();
                droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                droppedCompletions.Add(droppedNode.Value.Completion);
            }

            queuedLowPriorityMouseMoveNode = null;
        }

        if (droppedCompletions is null)
        {
            return;
        }

        foreach (var dropped in droppedCompletions)
        {
            dropped.TrySetResult(false);
        }

        Log($"Control outbound low lane flushed (reason={reason}, dropped={droppedCompletions.Count})");
    }

    private void FlushAllControlOutboundQueues(string reason)
    {
        List<TaskCompletionSource<bool>>? droppedCompletions = null;

        lock (controlOutboundQueueGate)
        {
            while (highPriorityControlOutboundQueue.First is not null)
            {
                var droppedNode = highPriorityControlOutboundQueue.First;
                highPriorityControlOutboundQueue.RemoveFirst();
                droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                droppedCompletions.Add(droppedNode.Value.Completion);
            }

            while (lowPriorityControlOutboundQueue.First is not null)
            {
                var droppedNode = lowPriorityControlOutboundQueue.First;
                lowPriorityControlOutboundQueue.RemoveFirst();
                droppedCompletions ??= new List<TaskCompletionSource<bool>>();
                droppedCompletions.Add(droppedNode.Value.Completion);
            }

            queuedLowPriorityMouseMoveNode = null;
        }

        if (droppedCompletions is null)
        {
            return;
        }

        foreach (var dropped in droppedCompletions)
        {
            dropped.TrySetCanceled();
        }

        Log($"Control outbound lanes flushed (reason={reason}, dropped={droppedCompletions.Count})");
    }

    private static bool IsLowPriorityControlInput(ControlInputMessageV1 message)
    {
        var kind = message.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        // Keep clicks, wheel and keyboard in high lane for responsiveness.
        return string.Equals(kind.Trim(), "mouse_move", StringComparison.Ordinal);
    }

    private void SubscribeClientEvents()
    {
        client.MessageReceived += OnClientMessageReceived;
        client.Disconnected += OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle += OnBridgeLifecycle;
            realClient.ScreenShareFrameCompleted += OnScreenShareFrameCompleted;
            realClient.ScreenShareStopped += OnScreenShareStopped;
        }
    }

    private void OnBridgeLifecycle(object? sender, BridgeLifecycleEvent e)
    {
        BridgeLifecycle?.Invoke(this, e);
    }

    private void OnScreenShareFrameCompleted(object? sender, ScreenShareFrameCompletedEventArgs e)
    {
        if (!TryValidateScreenShareSession("frame", e.SessionId) ||
            !IsScreenShareAuthorizedForDispatch("frame", e.SessionId))
        {
            return;
        }

        ScreenShareFrameCompleted?.Invoke(this, e);
    }

    private void OnScreenShareStopped(object? sender, string sessionId)
    {
        if (!TryValidateScreenShareSession("stop", sessionId))
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_stop_received_transport; session_id={sessionId}");
        ScreenShareStopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnSecureScreenShareFrameReady(object? sender, ScreenShareFrameReadyEventArgs e)
    {
        if (!TryValidateScreenShareSession("frame", e.SessionId) ||
            !IsScreenShareAuthorizedForDispatch("frame", e.SessionId))
        {
            return;
        }

        try
        {
            var metrics = secureScreenShareFrameReassembler.GetMetricsSnapshot();
            ScreenShareFrameCompleted?.Invoke(
                this,
                new ScreenShareFrameCompletedEventArgs(
                    e.FrameId,
                    e.Width,
                    e.Height,
                    e.Encoding,
                    e.EncodedFrameBytes,
                    e.TimestampUnixMilliseconds,
                    ChunksDroppedOlderFrame: metrics.FramesDropped,
                    AssembliesExpired: 0,
                    SessionId: e.SessionId));
        }
        catch (Exception ex)
        {
            Log($"ScreenShareFrameCompleted dispatch failed (source=secure_envelope, ex={ex.GetType().Name})");
        }
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        NknRuntimeDiagnostics.SetLastError("nkn_client_disconnected");
        UpdateSessionSecurityState(currentSessionSecurityState.Invalidate("transport_disconnected"));
        Log("Client disconnected");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnClientMessageReceived(object? sender, NknIncomingMessage e)
    {
        if (disposed)
        {
            return;
        }

        if (IsSelfSource(e.Source))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("self_source");
            Log($"Envelope ignored from self (source={e.Source})");
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.Source))
        {
            lastPeerAddress = e.Source;
        }

        if (!EnvelopeCodec.TryDeserialize(e.Payload, out var env))
        {
            NknRuntimeDiagnostics.SetLastError("envelope_parse_failed");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("parse_failed");
            Log($"Envelope parse failed (payload_len={e.Payload.Length})");
            return;
        }

        NknRuntimeDiagnostics.SetLastEnvelopeType(env.Type.ToString());

        if (env.Type != MsgType.JoinRequest &&
            !string.IsNullOrWhiteSpace(env.Code) &&
            TryGetCurrentEnvelopeCode(out var expectedEnvelopeCode) &&
            !string.Equals(env.Code, expectedEnvelopeCode, StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.SetLastError("envelope_code_mismatch");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("code_mismatch");
            Log($"Envelope ignored (type={env.Type}, payload_len={env.Payload.Length}, msg_id={env.MessageId}, code={env.Code})");
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dedupTimestamp = env.UnixTimeMs > 0 ? env.UnixTimeMs : nowUnixMs;
        if (!seenMessageIds.TryAdd(env.MessageId, dedupTimestamp))
        {
            if ((env.Type == MsgType.JoinRequest ||
                 env.Type == MsgType.Chat ||
                 env.Type == MsgType.SessionHandshakeStart) &&
                !string.IsNullOrWhiteSpace(e.Source))
            {
                SendAckFireAndForget(e.Source, env.Code, env.MessageId);
            }

            Log($"Envelope duplicate ignored (type={env.Type}, msg_id={env.MessageId})");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("duplicate");
            return;
        }

        NknRuntimeDiagnostics.IncrementMessagesReceived();
        Log($"Envelope received (type={env.Type}, payload_len={env.Payload.Length}, msg_id={env.MessageId}, reply_to={env.ReplyTo ?? "-"})");

        try
        {
            switch (env.Type)
            {
                case MsgType.JoinRequest:
                    HandleJoinRequest(e.Source, env);
                    break;
                case MsgType.Approve:
                    HandleApprove(e.Source, env);
                    break;
                case MsgType.Reject:
                    HandleReject(e.Source, env);
                    break;
                case MsgType.Chat:
                    HandleChat(e.Source, env);
                    break;
                case MsgType.Ack:
                    HandleAck(e.Source, env);
                    break;
                case MsgType.SessionEnd:
                    HandleSessionEnd(e.Source, env);
                    break;
                case MsgType.ControlRequest:
                    HandleControlRequest(e.Source, env);
                    break;
                case MsgType.ControlResponse:
                    HandleControlResponse(e.Source, env);
                    break;
                case MsgType.ControlStart:
                    HandleControlStart(e.Source, env);
                    break;
                case MsgType.ControlStop:
                    HandleControlStop(e.Source, env);
                    break;
                case MsgType.ControlInput:
                    HandleControlInput(e.Source, env);
                    break;
                case MsgType.ControlAck:
                    HandleControlAck(e.Source, env);
                    break;
                case MsgType.ControlStateSnapshot:
                    HandleControlStateSnapshot(e.Source, env);
                    break;
                case MsgType.ControlDisplayInfo:
                    HandleControlDisplayInfo(e.Source, env);
                    break;
                case MsgType.SessionHandshakeStart:
                    HandleSessionHandshakeStart(e.Source, env);
                    break;
                case MsgType.SessionHandshakeChallenge:
                    HandleSessionHandshakeChallenge(e.Source, env);
                    break;
                case MsgType.SessionHandshakeResponse:
                    HandleSessionHandshakeResponse(e.Source, env);
                    break;
                case MsgType.SessionHandshakeResult:
                    HandleSessionHandshakeResult(e.Source, env);
                    break;
                case MsgType.ScreenShareFrame:
                    HandleScreenShareFrame(e.Source, env);
                    break;
                case MsgType.ScreenShareStop:
                    HandleScreenShareStop(e.Source, env);
                    break;
                case MsgType.FileTransferOffer:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferAccept:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferDecline:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferStart:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferChunk:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferCancel:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferError:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                case MsgType.FileTransferComplete:
                    EnqueueInboundFileTransferDispatch(e.Source, env);
                    break;
                default:
                    NknRuntimeDiagnostics.SetLastError("unexpected_message_type");
                    NknRuntimeDiagnostics.SetLastEnvelopeDropReason("unexpected_type");
                    Log($"Unexpected envelope type (type={env.Type}, msg_id={env.MessageId})");
                    break;
            }
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
            Log($"Envelope dispatch failed (type={env.Type}, msg_id={env.MessageId}, ex={ex.GetType().Name})");
        }
    }

    private void EnqueueInboundFileTransferDispatch(string source, Envelope env)
    {
        var dispatchOrder = Interlocked.Increment(ref nextInboundFileTransferDispatchOrder);
        var work = new InboundFileTransferDispatchWork(
            Generation: Volatile.Read(ref inboundFileTransferDispatchGeneration),
            Order: dispatchOrder,
            Source: source,
            Envelope: env);
        var shouldStartDrainer = false;

        lock (inboundFileTransferDispatchGate)
        {
            pendingInboundFileTransferDispatch[dispatchOrder] = work;
            if (!inboundFileTransferDispatchActive)
            {
                inboundFileTransferDispatchActive = true;
                shouldStartDrainer = true;
            }
        }

        if (shouldStartDrainer)
        {
            _ = Task.Run(ProcessInboundFileTransferDispatchQueueAsync, CancellationToken.None);
        }
    }

    private async Task ProcessInboundFileTransferDispatchQueueAsync()
    {
        await Task.Yield();

        while (true)
        {
            InboundFileTransferDispatchWork work;
            lock (inboundFileTransferDispatchGate)
            {
                if (!pendingInboundFileTransferDispatch.TryGetValue(nextInboundFileTransferDispatchToProcess, out work))
                {
                    inboundFileTransferDispatchActive = false;
                    return;
                }

                pendingInboundFileTransferDispatch.Remove(nextInboundFileTransferDispatchToProcess);
                nextInboundFileTransferDispatchToProcess++;
            }

            if (disposed || work.Generation != Volatile.Read(ref inboundFileTransferDispatchGeneration))
            {
                continue;
            }

            try
            {
                DispatchInboundFileTransferEnvelope(work.Source, work.Envelope);
            }
            catch (Exception ex)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
                Log($"Envelope dispatch failed (type={work.Envelope.Type}, msg_id={work.Envelope.MessageId}, ex={ex.GetType().Name})");
            }
        }
    }

    private void DispatchInboundFileTransferEnvelope(string source, Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.FileTransferOffer:
                HandleFileTransferOffer(source, env);
                break;
            case MsgType.FileTransferAccept:
                HandleFileTransferAccept(source, env);
                break;
            case MsgType.FileTransferDecline:
                HandleFileTransferDecline(source, env);
                break;
            case MsgType.FileTransferStart:
                HandleFileTransferStart(source, env);
                break;
            case MsgType.FileTransferChunk:
                HandleFileTransferChunk(source, env);
                break;
            case MsgType.FileTransferCancel:
                HandleFileTransferCancel(source, env);
                break;
            case MsgType.FileTransferError:
                HandleFileTransferError(source, env);
                break;
            case MsgType.FileTransferComplete:
                HandleFileTransferComplete(source, env);
                break;
            default:
                NknRuntimeDiagnostics.SetLastError("unexpected_filetransfer_message_type");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("unexpected_filetransfer_type");
                Log($"Unexpected file-transfer envelope type (type={env.Type}, msg_id={env.MessageId})");
                break;
        }
    }

    private void ResetInboundFileTransferDispatchQueue()
    {
        lock (inboundFileTransferDispatchGate)
        {
            pendingInboundFileTransferDispatch.Clear();
            nextInboundFileTransferDispatchToProcess = 1;
            nextInboundFileTransferDispatchOrder = 0;
            inboundFileTransferDispatchActive = false;
            Interlocked.Increment(ref inboundFileTransferDispatchGeneration);
        }
    }

    private void HandleJoinRequest(string source, Envelope env)
    {
        if (!TryParseJoinRequestPayload(env.Payload, out var join))
        {
            NknRuntimeDiagnostics.SetLastError("joinrequest_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("joinrequest_payload_invalid");
            Log($"JoinRequest payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }
        NknRuntimeDiagnostics.IncrementJoinRequestsReceived();
        SessionTimeline.Record("IncomingJoinRequest");

        byte[] helperPubKey;
        try
        {
            var helperPubKeyBase64 = join.helperEcdhPublicKey ?? string.Empty;
            helperPubKey = Convert.FromBase64String(helperPubKeyBase64);
        }
        catch (FormatException)
        {
            NknRuntimeDiagnostics.SetLastError("joinrequest_bad_pubkey");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("joinrequest_bad_pubkey");
            Log($"JoinRequest public key invalid (msg_id={env.MessageId})");
            return;
        }

        if (string.IsNullOrWhiteSpace(join.helperEndpoint))
        {
            NknRuntimeDiagnostics.SetLastError("joinrequest_missing_endpoint");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("joinrequest_missing_endpoint");
            Log($"JoinRequest missing helper endpoint (msg_id={env.MessageId})");
            return;
        }

        if (pendingJoinRequest is not null || pendingInboundHandshake is not null)
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("joinrequest_already_pending");
            Log($"JoinRequest ignored (msg_id={env.MessageId}, reason=join_already_pending)");
            return;
        }

        if (!string.IsNullOrWhiteSpace(remoteEndpoint) &&
            !AddressesLikelySamePeer(remoteEndpoint, join.helperEndpoint))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("joinrequest_active_session");
            Log($"JoinRequest ignored (msg_id={env.MessageId}, reason=active_session)");
            return;
        }

        remoteEndpoint = join.helperEndpoint;
        lastPeerAddress = string.IsNullOrWhiteSpace(source) ? join.helperEndpoint : source;
        remoteSupportsRemoteControl = join.remoteControlSupported == true;
        transportRemoteControlState = transportRemoteControlState with
        {
            SupportsRemoteControl = LocalSupportsRemoteControl,
            PeerSupportsRemoteControl = remoteSupportsRemoteControl,
        };

        if (!TryGetHelpeeHostKeyPair(out _))
        {
            NknRuntimeDiagnostics.SetLastError("host_ecdh_not_ready");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("host_ecdh_not_ready");
            Log($"JoinRequest ignored (msg_id={env.MessageId}, reason=no_host_key)");
            Disconnected?.Invoke(this, EventArgs.Empty);
            return;
        }

        var joinEnvelopeCode = ResolveInboundEnvelopeCode(env.Code);
        currentEnvelopeCode = joinEnvelopeCode;

        ReplacePendingInboundHandshake(new PendingInboundHandshakeState(
            joinRequestMessageId: env.MessageId,
            remoteEndpoint: join.helperEndpoint,
            helperAddress: new PeerAddress(join.helperEndpoint),
            helperEcdhPublicKey: helperPubKey,
            envelopeCode: joinEnvelopeCode));

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        Log($"JoinRequest accepted (msg_id={env.MessageId}, helper_endpoint_len={join.helperEndpoint.Length}, helper_id_len={(join.helperIdentifier ?? string.Empty).Length})");
    }

    private void HandleSessionHandshakeStart(string source, Envelope env)
    {
        if (!TryGetPendingInboundHandshake(out var pending) || pending is null)
        {
            Log($"SessionHandshakeStart ignored (msg_id={env.MessageId}, reason=no_pending_join)");
            return;
        }

        if (!TryParseHandshakeStartPayload(env.Payload, out var start))
        {
            FailInboundHandshake(pending, "handshake_start_invalid", source);
            return;
        }

        if (!string.Equals(pending.JoinRequestMessageId, env.ReplyTo, StringComparison.Ordinal))
        {
            FailInboundHandshake(pending, "handshake_start_replyto_mismatch", source);
            return;
        }

        if (!AddressesLikelySamePeer(start.HelperAddress.Value, pending.HelperAddress.Value) ||
            (!string.IsNullOrWhiteSpace(source) && !AddressesLikelySamePeer(source, pending.RemoteEndpoint)))
        {
            FailInboundHandshake(pending, "handshake_start_helper_mismatch", source);
            return;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        var localAddress = new PeerAddress(LocalPeerAddress);
        var inviteValidated = false;
        var requestedCapabilities = CapabilityGrant.None;

        if (!string.IsNullOrWhiteSpace(start.InviteToken))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var validationScopeKey = PersistentInviteSecurityStore.BuildValidationScopeKey(
                localAddress,
                !string.IsNullOrWhiteSpace(source) ? source : start.HelperAddress.Value);
            if (!inviteValidationThrottle.TryAcquire(validationScopeKey, nowUtc, out var retryAfter))
            {
                LocalOperationalLog.Warn(
                    "InviteValidation",
                    $"result=Throttled; mode={InviteValidationMode.ConsumeIfValid}; session_id={start.SessionId.Value}; target={localAddress.Value}; helper={start.HelperAddress.Value}; retry_after_ms={(long)Math.Ceiling(retryAfter.TotalMilliseconds)}");
                FailInboundHandshake(pending, "invite_validation_throttled", source, start.SessionId);
                return;
            }

            var validation = inviteTokenValidator.Validate(start.InviteToken, nowUtc, InviteValidationMode.ConsumeIfValid);
            if (!validation.IsSuccess || validation.Invite is null)
            {
                FailInboundHandshake(pending, validation.Result.ToFailureCode(), source, start.SessionId);
                return;
            }

            if (validation.Invite.SessionId != start.SessionId ||
                validation.Invite.TargetAddress != localAddress)
            {
                FailInboundHandshake(pending, "invite_binding_mismatch", source, start.SessionId);
                return;
            }

            if (InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites() &&
                validation.Invite.BoundHelperAddress is null)
            {
                FailInboundHandshake(pending, "invite_helper_required", source, start.SessionId);
                return;
            }

            if (validation.Invite.BoundHelperAddress is not null &&
                !AddressesLikelySamePeer(validation.Invite.BoundHelperAddress.Value.Value, start.HelperAddress.Value))
            {
                FailInboundHandshake(pending, "invite_helper_mismatch", source, start.SessionId);
                return;
            }

            inviteValidated = true;
            requestedCapabilities = validation.Invite.Payload.Capabilities.ToCapabilityGrant();
        }

        if (!TryGetHelpeeHostKeyPair(out var helpeeKeyPair) || helpeeKeyPair is null)
        {
            FailInboundHandshake(pending, "host_ecdh_not_ready", source, start.SessionId);
            return;
        }

        var expiresAtUtc = DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.HandshakeTimeout);
        var challenge = new SessionHandshakeChallenge(
            start.SessionId,
            localAddress,
            SessionHandshakeProtocol.CreateChallengeNonce(),
            expiresAtUtc.ToUnixTimeMilliseconds(),
            Convert.ToBase64String(helpeeKeyPair.PublicKey));

        if (!handshakeReplayCache.TryTrackChallenge(
                start.SessionId,
                start.HelperAddress,
                localAddress,
                challenge.ChallengeNonce,
                expiresAtUtc,
                DateTimeOffset.UtcNow))
        {
            FailInboundHandshake(pending, "handshake_challenge_replay_detected", source, start.SessionId);
            return;
        }

        UpdatePendingInboundHandshake(pending.WithChallenge(start.SessionId, inviteValidated, requestedCapabilities, challenge.ChallengeNonce, expiresAtUtc, localAddress));
        UpdateSessionSecurityState(
            SessionSecurityState.CreateHelpeeWaiting(localAddress).WithHandshakeChallenge(
                start.SessionId,
                localAddress,
                pending.HelperAddress,
                inviteValidated,
                expiresAtUtc));

        var envelope = CreateEnvelope(pending.EnvelopeCode, MsgType.SessionHandshakeChallenge, SessionHandshakeProtocol.Serialize(challenge), env.MessageId);
        _ = SendEnvelopeAsync(pending.RemoteEndpoint, envelope, CancellationToken.None);
    }

    private void HandleSessionHandshakeChallenge(string source, Envelope env)
    {
        if (pendingOutboundHandshake is null)
        {
            Log($"SessionHandshakeChallenge ignored (msg_id={env.MessageId}, reason=no_outbound_handshake)");
            return;
        }

        if (!TryParseHandshakeChallengePayload(env.Payload, out var challenge))
        {
            AbortOutboundHandshake("handshake_challenge_invalid");
            return;
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            !AddressesLikelySamePeer(source, pendingOutboundHandshake.HelpeeAddress.Value))
        {
            AbortOutboundHandshake("handshake_challenge_source_mismatch");
            return;
        }

        if (challenge.SessionId != pendingOutboundHandshake.SessionId ||
            challenge.HelpeeAddress != pendingOutboundHandshake.HelpeeAddress)
        {
            AbortOutboundHandshake("handshake_challenge_binding_mismatch");
            return;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= challenge.ExpiresAtUtcMs)
        {
            AbortOutboundHandshake("handshake_challenge_expired", SessionHandshakeState.Expired);
            return;
        }

        var helperKeyPair = GetHelperJoinKeyPairOrThrow();
        if (!TryGetCurrentEnvelopeCode(out var sessionContextCode))
        {
            AbortOutboundHandshake("handshake_no_session_context");
            return;
        }

        byte[] helpeePubKey;
        try
        {
            helpeePubKey = Convert.FromBase64String(challenge.HelpeeEcdhPublicKeyBase64);
        }
        catch (FormatException)
        {
            AbortOutboundHandshake("handshake_bad_host_pubkey");
            return;
        }

        byte[] macKey;
        try
        {
            macKey = DeriveSessionKey(helperKeyPair, helpeePubKey, sessionContextCode);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            NknRuntimeDiagnostics.SetLastError("handshake_key_derivation_failed");
            Log($"SessionHandshakeChallenge key derivation failed (msg_id={env.MessageId}, ex={ex.GetType().Name})");
            AbortOutboundHandshake("handshake_key_derivation_failed");
            return;
        }

        var mac = SessionHandshakeProtocol.ComputeResponseMac(
            macKey,
            challenge.SessionId,
            pendingOutboundHandshake.HelperAddress,
            challenge.HelpeeAddress,
            challenge.ChallengeNonce);
        var response = new SessionHandshakeResponse(
            challenge.SessionId,
            pendingOutboundHandshake.HelperAddress,
            challenge.ChallengeNonce,
            Convert.ToBase64String(mac));
        pendingOutboundHandshake = pendingOutboundHandshake.WithChallenge(
            challenge.ChallengeNonce,
            DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUtcMs),
            helpeePubKey);
        UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeChallenge(
            challenge.SessionId,
            challenge.HelpeeAddress,
            pendingOutboundHandshake.HelperAddress,
            pendingOutboundHandshake.InviteValidated,
            DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUtcMs)));

        var envelope = CreateEnvelope(sessionContextCode, MsgType.SessionHandshakeResponse, SessionHandshakeProtocol.Serialize(response), env.MessageId);
        _ = SendEnvelopeAsync(pendingOutboundHandshake.HelpeeAddress.Value, envelope, CancellationToken.None);
    }

    private void HandleSessionHandshakeResponse(string source, Envelope env)
    {
        if (!TryGetPendingInboundHandshake(out var pending) || pending is null)
        {
            if (TryParseHandshakeResponsePayload(env.Payload, out var replayResponse) &&
                PeerAddress.TryParse(LocalPeerAddress, out var localAddress) &&
                handshakeReplayCache.WasChallengeConsumed(
                    replayResponse.SessionId,
                    replayResponse.HelperAddress,
                    localAddress,
                    replayResponse.ChallengeNonce,
                    DateTimeOffset.UtcNow))
            {
                NknRuntimeDiagnostics.SetLastError("handshake_response_replay_detected");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("handshake_response_replay_detected");
                LocalOperationalLog.Warn(
                    "SessionHandshake",
                    $"event=failure; direction=inbound; reason=handshake_response_replay_detected; session_id={replayResponse.SessionId.Value}; helper_identity={replayResponse.HelperAddress.Value}; peer_id={source ?? "(none)"}");
                Log($"SessionHandshakeResponse rejected (msg_id={env.MessageId}, reason=replay_detected)");
                return;
            }

            Log($"SessionHandshakeResponse ignored (msg_id={env.MessageId}, reason=no_pending_challenge)");
            return;
        }

        if (!TryParseHandshakeResponsePayload(env.Payload, out var response))
        {
            FailInboundHandshake(pending, "handshake_response_invalid", source);
            return;
        }

        if (pending.SessionId is not SessionId sessionId ||
            pending.ChallengeNonce is null ||
            pending.HelpeeAddress is not PeerAddress helpeeAddress ||
            pending.ChallengeExpiresAtUtc is not DateTimeOffset expiresAtUtc)
        {
            FailInboundHandshake(pending, "handshake_response_without_challenge", source);
            return;
        }

        if (DateTimeOffset.UtcNow >= expiresAtUtc)
        {
            FailInboundHandshake(pending, "handshake_response_expired", source, sessionId, SessionHandshakeState.Expired);
            return;
        }

        if (response.SessionId != sessionId ||
            response.HelperAddress != pending.HelperAddress ||
            !string.Equals(response.ChallengeNonce, pending.ChallengeNonce, StringComparison.Ordinal))
        {
            FailInboundHandshake(pending, "handshake_response_binding_mismatch", source, sessionId);
            return;
        }

        byte[] candidateMac;
        try
        {
            candidateMac = Convert.FromBase64String(response.MacBase64);
        }
        catch (FormatException)
        {
            FailInboundHandshake(pending, "handshake_response_mac_invalid", source, sessionId);
            return;
        }

        if (!TryGetHelpeeHostKeyPair(out var helpeeKeyPair) || helpeeKeyPair is null)
        {
            FailInboundHandshake(pending, "host_ecdh_not_ready", source, sessionId);
            return;
        }

        byte[] macKey;
        try
        {
            macKey = DeriveSessionKey(helpeeKeyPair, pending.HelperEcdhPublicKey, pending.EnvelopeCode);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            NknRuntimeDiagnostics.SetLastError("handshake_key_derivation_failed");
            Log($"SessionHandshakeResponse key derivation failed (msg_id={env.MessageId}, ex={ex.GetType().Name})");
            FailInboundHandshake(pending, "handshake_key_derivation_failed", source, sessionId);
            return;
        }

        if (!SessionHandshakeProtocol.VerifyResponseMac(macKey, sessionId, pending.HelperAddress, helpeeAddress, pending.ChallengeNonce, candidateMac))
        {
            FailInboundHandshake(pending, "handshake_response_mac_mismatch", source, sessionId);
            return;
        }

        if (!handshakeReplayCache.TryConsumeChallenge(
                sessionId,
                pending.HelperAddress,
                helpeeAddress,
                pending.ChallengeNonce,
                DateTimeOffset.UtcNow))
        {
            FailInboundHandshake(pending, "handshake_response_replay_detected", source, sessionId);
            return;
        }

        var result = new SessionHandshakeResult(sessionId, Verified: true, FailureReason: null);
        var resultEnvelope = CreateEnvelope(pending.EnvelopeCode, MsgType.SessionHandshakeResult, SessionHandshakeProtocol.Serialize(result), env.MessageId);
        _ = SendEnvelopeAsync(pending.RemoteEndpoint, resultEnvelope, CancellationToken.None);

        var approvalRequest = pending.InviteValidated &&
                              pending.RequestedCapabilities != CapabilityGrant.None
            ? new ApprovalRequest(
                pending.HelperAddress,
                pending.RequestedCapabilities,
                sessionId)
            : null;

        ReplacePendingJoinRequest(new PendingJoinRequestState(
            pending.JoinRequestMessageId,
            pending.RemoteEndpoint,
            pending.HelperAddress,
            pending.HelperEcdhPublicKey,
            pending.EnvelopeCode,
            sessionId,
            approvalRequest));
        ClearPendingInboundHandshake(pending.JoinRequestMessageId);
        UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeVerified(pending.HelperAddress));

        IncomingJoinRequest?.Invoke(
            this,
            new IncomingJoinRequestEventArgs(
                approveAsync: (decision, ct) => ApproveJoinRequestAsync(pending.JoinRequestMessageId, decision, ct),
                rejectAsync: (reason, ct) => RejectJoinRequestAsync(pending.JoinRequestMessageId, reason, ct),
                approvalRequest: approvalRequest));
        NknRuntimeDiagnostics.IncrementIncomingJoinRequestRaised();
    }

    private void HandleSessionHandshakeResult(string source, Envelope env)
    {
        if (pendingOutboundHandshake is null)
        {
            Log($"SessionHandshakeResult ignored (msg_id={env.MessageId}, reason=no_outbound_handshake)");
            return;
        }

        if (!TryParseHandshakeResultPayload(env.Payload, out var result))
        {
            AbortOutboundHandshake("handshake_result_invalid");
            return;
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            !AddressesLikelySamePeer(source, pendingOutboundHandshake.HelpeeAddress.Value))
        {
            AbortOutboundHandshake("handshake_result_source_mismatch");
            return;
        }

        if (result.SessionId != pendingOutboundHandshake.SessionId)
        {
            AbortOutboundHandshake("handshake_result_session_mismatch");
            return;
        }

        if (!result.Verified)
        {
            AbortOutboundHandshake(result.FailureReason ?? "handshake_rejected");
            return;
        }

        UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeVerified(pendingOutboundHandshake.HelperAddress));
    }

    private void HandleApprove(string source, Envelope env)
    {
        if (!TryParseApprovePayload(env.Payload, out var approve))
        {
            NknRuntimeDiagnostics.SetLastError("approve_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_payload_invalid");
            Log($"Approve payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        SessionEcdhKeyPair? helperKeyPair;
        string? expectedJoinRequestId;
        PendingOutboundHandshakeState? outboundHandshake;
        lock (gate)
        {
            helperKeyPair = helperJoinEcdhKeyPair;
            expectedJoinRequestId = helperJoinRequestMessageId;
            outboundHandshake = pendingOutboundHandshake;
        }

        if (helperKeyPair is null || outboundHandshake is null)
        {
            NknRuntimeDiagnostics.SetLastError("approve_missing_helper_ecdh");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_missing_helper_ecdh");
            Log($"Approve ignored (msg_id={env.MessageId}, reason=no_helper_key)");
            return;
        }

        if (!TryValidatePendingOutboundLifecycleMessage(
                "approve",
                approve.sessionId,
                env.MessageId,
                env.ReplyTo,
                source,
                outboundHandshake,
                expectedJoinRequestId))
        {
            return;
        }

        try
        {
            byte[] helpeePubKey;
            try
            {
                var helpeePubKeyBase64 = approve.helpeeEcdhPublicKey ?? string.Empty;
                helpeePubKey = Convert.FromBase64String(helpeePubKeyBase64);
            }
            catch (FormatException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_bad_pubkey");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_bad_pubkey");
                Log($"Approve public key invalid (msg_id={env.MessageId})");
                AbortOutboundHandshake("approve_bad_pubkey");
                return;
            }

            if (!TryGetCurrentEnvelopeCode(out var sessionContextCode))
            {
                NknRuntimeDiagnostics.SetLastError("approve_missing_session_context");
                Log($"Approve ignored (msg_id={env.MessageId}, reason=no_session_context)");
                AbortOutboundHandshake("approve_missing_session_context");
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = DeriveSessionKey(helperKeyPair, helpeePubKey, sessionContextCode);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_key_derivation_failed");
                Log($"Approve key derivation failed (msg_id={env.MessageId}, ex={ex.GetType().Name})");
                AbortOutboundHandshake("approve_key_derivation_failed");
                return;
            }

            SessionSecureEnvelopePayload securePayload;
            try
            {
                var secureEnvelopeBytes = Convert.FromBase64String(approve.secureEnvelopeBase64!);
                if (!TryDecryptLifecyclePayload(
                        source,
                        env.MessageId,
                        "approve",
                        secureEnvelopeBytes,
                        sharedKey,
                        new SessionSecureEnvelopeExpectation(
                            Family: SessionSecureMessageFamily.Lifecycle,
                            MessageType: "approve",
                            SessionId: outboundHandshake.SessionId,
                            SenderIdentity: outboundHandshake.HelpeeAddress,
                            RequestId: expectedJoinRequestId),
                        inboundLifecycleReplayWindow,
                        out securePayload))
                {
                    AbortOutboundHandshake("approve_secure_envelope_invalid");
                    return;
                }
            }
            catch (FormatException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_secure_envelope_invalid");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_secure_envelope_invalid");
                Log($"Approve secure envelope invalid base64 (msg_id={env.MessageId})");
                AbortOutboundHandshake("approve_secure_envelope_invalid");
                return;
            }

            if (!TryParseApproveSecurePayload(securePayload.Plaintext, out var secureApprove))
            {
                NknRuntimeDiagnostics.SetLastError("approve_secure_payload_invalid");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_secure_payload_invalid");
                Log($"Approve secure payload invalid (msg_id={env.MessageId})");
                AbortOutboundHandshake("approve_secure_payload_invalid");
                return;
            }

            ApprovalDecision decision;
            try
            {
                if (!SessionHandshakeProtocol.TryDeserializeApprovalDecision(
                        Convert.FromBase64String(secureApprove.approvalDecisionBase64!),
                        out decision))
                {
                    NknRuntimeDiagnostics.SetLastError("approve_decision_invalid");
                    NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_decision_invalid");
                    Log($"Approve decision invalid (msg_id={env.MessageId})");
                    AbortOutboundHandshake("approve_decision_invalid");
                    return;
                }
            }
            catch (FormatException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_decision_invalid");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_decision_invalid");
                Log($"Approve decision invalid base64 (msg_id={env.MessageId})");
                AbortOutboundHandshake("approve_decision_invalid");
                return;
            }

            remoteSupportsRemoteControl = secureApprove.remoteControlSupported == true;
            transportRemoteControlState = transportRemoteControlState with
            {
                SupportsRemoteControl = LocalSupportsRemoteControl,
                PeerSupportsRemoteControl = remoteSupportsRemoteControl,
            };

            if (decision.SessionId != outboundHandshake.SessionId ||
                decision.HelperIdentity != outboundHandshake.HelperAddress ||
                decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                (decision.ApprovedCapabilities & ~outboundHandshake.RequestedCapabilities) != 0)
            {
                NknRuntimeDiagnostics.SetLastError("approve_decision_mismatch");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_decision_mismatch");
                Log($"Approve decision mismatch (msg_id={env.MessageId})");
                AbortOutboundHandshake("approve_decision_mismatch");
                return;
            }

            lock (gate)
            {
                if (ReferenceEquals(helperJoinEcdhKeyPair, helperKeyPair))
                {
                    helperJoinEcdhKeyPair = null;
                }

                if (string.Equals(helperJoinRequestMessageId, expectedJoinRequestId, StringComparison.Ordinal))
                {
                    helperJoinRequestMessageId = null;
                }
            }

            SetControlSessionSharedKey(sharedKey);
            // Approve can arrive before SessionHandshakeResult on a real network. A valid secure
            // approval envelope proves the same shared-key handshake, so finalize verification here too.
            UpdateSessionSecurityState(
                currentSessionSecurityState
                    .WithHandshakeVerified(outboundHandshake.HelperAddress)
                    .WithApproval(decision.ToGrant()));
            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
            pendingOutboundHandshake = null;
            Approved?.Invoke(this, EventArgs.Empty);
            Log($"Approve dispatched (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
        }
        finally
        {
            helperKeyPair.Dispose();
        }
    }

    private void HandleReject(string source, Envelope env)
    {
        if (!TryParseRejectPayload(env.Payload, out var reject))
        {
            NknRuntimeDiagnostics.SetLastError("reject_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_payload_invalid");
            Log($"Reject payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        PendingOutboundHandshakeState? outboundHandshake;
        PendingJoinRequestState? inboundJoinRequest;
        string? expectedJoinRequestId;
        lock (gate)
        {
            outboundHandshake = pendingOutboundHandshake;
            inboundJoinRequest = pendingJoinRequest;
            expectedJoinRequestId = helperJoinRequestMessageId;
        }

        if (outboundHandshake is null && inboundJoinRequest is null)
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_no_outbound_handshake");
            Log($"Reject ignored (msg_id={env.MessageId}, reason=no_pending_handshake)");
            return;
        }

        if (outboundHandshake is not null &&
            !TryValidatePendingOutboundLifecycleMessage(
                "reject",
                reject.sessionId,
                env.MessageId,
                env.ReplyTo,
                source,
                outboundHandshake,
                expectedJoinRequestId))
        {
            return;
        }

        if (outboundHandshake is null &&
            !TryValidatePendingInboundLifecycleMessage(
                "reject",
                reject.sessionId,
                env.MessageId,
                env.ReplyTo,
                source,
                inboundJoinRequest!))
        {
            return;
        }

        SessionEcdhKeyPair? helperKeyPair;
        SessionEcdhKeyPair? helpeeKeyPair;
        lock (gate)
        {
            helperKeyPair = helperJoinEcdhKeyPair;
            helpeeKeyPair = helpeeHostEcdhKeyPair;
        }

        if (outboundHandshake is not null && helperKeyPair is null)
        {
            NknRuntimeDiagnostics.SetLastError("reject_missing_helper_ecdh");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_missing_helper_ecdh");
            Log($"Reject ignored (msg_id={env.MessageId}, reason=no_helper_key)");
            return;
        }

        if (outboundHandshake is null && helpeeKeyPair is null)
        {
            NknRuntimeDiagnostics.SetLastError("reject_missing_host_ecdh");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_missing_host_ecdh");
            Log($"Reject ignored (msg_id={env.MessageId}, reason=no_host_key)");
            return;
        }

        try
        {
            byte[] helpeePubKey;
            try
            {
                helpeePubKey = Convert.FromBase64String(reject.helpeeEcdhPublicKey ?? string.Empty);
            }
            catch (FormatException)
            {
                NknRuntimeDiagnostics.SetLastError("reject_bad_pubkey");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_bad_pubkey");
                Log($"Reject public key invalid (msg_id={env.MessageId})");
                if (outboundHandshake is not null)
                {
                    AbortOutboundHandshake("reject_bad_pubkey");
                }
                return;
            }

            string? sessionContextCode = null;
            var pendingSessionContext = outboundHandshake is not null ? null : inboundJoinRequest!.EnvelopeCode;
            if ((outboundHandshake is not null && !TryGetCurrentEnvelopeCode(out sessionContextCode)) ||
                (outboundHandshake is null && string.IsNullOrWhiteSpace(pendingSessionContext)))
            {
                NknRuntimeDiagnostics.SetLastError("reject_missing_session_context");
                Log($"Reject ignored (msg_id={env.MessageId}, reason=no_session_context)");
                if (outboundHandshake is not null)
                {
                    AbortOutboundHandshake("reject_missing_session_context");
                }
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = outboundHandshake is not null
                    ? DeriveSessionKey(helperKeyPair!, helpeePubKey, sessionContextCode!)
                    : DeriveSessionKey(helpeeKeyPair!, inboundJoinRequest!.HelperEcdhPublicKey, pendingSessionContext!);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("reject_key_derivation_failed");
                Log($"Reject key derivation failed (msg_id={env.MessageId}, ex={ex.GetType().Name})");
                if (outboundHandshake is not null)
                {
                    AbortOutboundHandshake("reject_key_derivation_failed");
                }
                return;
            }

            SessionSecureEnvelopePayload securePayload;
            try
            {
                var secureEnvelopeBytes = Convert.FromBase64String(reject.secureEnvelopeBase64!);
                if (!TryDecryptLifecyclePayload(
                        source,
                        env.MessageId,
                        "reject",
                        secureEnvelopeBytes,
                        sharedKey,
                        new SessionSecureEnvelopeExpectation(
                            Family: SessionSecureMessageFamily.Lifecycle,
                            MessageType: "reject",
                            SessionId: outboundHandshake?.SessionId ?? inboundJoinRequest!.SessionId,
                            SenderIdentity: outboundHandshake?.HelpeeAddress ?? inboundJoinRequest!.HelperAddress,
                            RequestId: outboundHandshake is not null ? expectedJoinRequestId : inboundJoinRequest!.JoinRequestMessageId),
                        inboundLifecycleReplayWindow,
                        out securePayload))
                {
                    if (outboundHandshake is not null)
                    {
                        AbortOutboundHandshake("reject_secure_envelope_invalid");
                    }
                    return;
                }
            }
            catch (FormatException)
            {
                NknRuntimeDiagnostics.SetLastError("reject_secure_envelope_invalid");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_secure_envelope_invalid");
                Log($"Reject secure envelope invalid base64 (msg_id={env.MessageId})");
                if (outboundHandshake is not null)
                {
                    AbortOutboundHandshake("reject_secure_envelope_invalid");
                }
                return;
            }

            if (!TryParseRejectSecurePayload(securePayload.Plaintext, out var secureReject))
            {
                NknRuntimeDiagnostics.SetLastError("reject_secure_payload_invalid");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("reject_secure_payload_invalid");
                Log($"Reject secure payload invalid (msg_id={env.MessageId})");
                if (outboundHandshake is not null)
                {
                    AbortOutboundHandshake("reject_secure_payload_invalid");
                }
                return;
            }

            var rejectionReason = string.IsNullOrWhiteSpace(secureReject.reason) ? "join_rejected" : secureReject.reason;
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(
                SessionHandshakeState.Invalidated,
                rejectionReason));
            if (outboundHandshake is not null)
            {
                pendingOutboundHandshake = null;
                ClearHelperJoinKeyPair();
                Rejected?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ClearPendingJoinRequest(inboundJoinRequest!.JoinRequestMessageId);
                RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
            }
            Log($"Reject dispatched (msg_id={env.MessageId})");
        }
        finally
        {
            helperKeyPair?.Dispose();
        }
    }

    private void HandleChat(string source, Envelope env)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(env.Payload));
        Log($"Chat dispatched (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
    }

    private void HandleSessionEnd(string source, Envelope env)
    {
        if (!TryDecryptLifecyclePayload(
                source,
                env.MessageId,
                "session_end",
                env.Payload,
                TryGetControlSessionSharedKey(),
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "session_end",
                    SessionId: currentSessionSecurityState.SessionId,
                    SenderIdentity: TryResolveExpectedRemotePeerAddressForLifecycle()),
                inboundLifecycleReplayWindow,
                out var securePayload))
        {
            return;
        }

        if (!TryParseSessionEndPayload(securePayload.Plaintext, out var sessionEnd))
        {
            NknRuntimeDiagnostics.SetLastError("session_end_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("session_end_payload_invalid");
            Log($"SessionEnd payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlMessageSession(
                "session_end",
                sessionEnd.sessionId,
                env.MessageId,
                requestId: null,
                source))
        {
            return;
        }

        SessionTimeline.Record("SessionEndReceived");
        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        Log($"SessionEnd dispatched (msg_id={env.MessageId})");
        RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
    }

    private void HandleScreenShareFrame(string source, Envelope env)
    {
        if (!TryDecryptScreenSharePayload(source, env, MsgType.ScreenShareFrame, out var securePayload))
        {
            return;
        }

        if (!ScreenSharePayloadCodec.TryDeserialize(securePayload.Plaintext, out var chunk))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_frame_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_frame_payload_invalid");
            Log($"ScreenShareFrame payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateScreenShareSecureMetadata("screenshare_frame", securePayload.Metadata, env.MessageId) ||
            !TryValidateControlMessageSession(
                "screenshare_frame",
                chunk.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("frame", chunk.SessionId) ||
            !IsScreenShareAuthorizedForDispatch("frame", chunk.SessionId))
        {
            return;
        }

        secureScreenShareFrameReassembler.OnChunk(chunk);
    }

    private void HandleScreenShareStop(string source, Envelope env)
    {
        if (!TryDecryptScreenSharePayload(source, env, MsgType.ScreenShareStop, out var securePayload))
        {
            return;
        }

        if (!ScreenSharePayloadCodec.TryDeserializeStop(securePayload.Plaintext, out var stop))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_stop_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_stop_payload_invalid");
            Log($"ScreenShareStop payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateScreenShareSecureMetadata("screenshare_stop", securePayload.Metadata, env.MessageId) ||
            !TryValidateControlMessageSession(
                "screenshare_stop",
                stop.SessionId,
                env.MessageId,
                requestId: null,
                source) ||
            !TryValidateScreenShareSession("stop", stop.SessionId))
        {
            return;
        }

        secureScreenShareFrameReassembler.ClearSession(stop.SessionId);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_stop_received_transport; session_id={stop.SessionId}; source={source ?? "(none)"}; msg_id={env.MessageId}; path=envelope");
        ScreenShareStopped?.Invoke(this, EventArgs.Empty);
    }

    private void HandleFileTransferOffer(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferOffer, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeOffer(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_offer_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_offer_payload_invalid");
            Log($"FileTransferOffer payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_offer", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_offer", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_offer", MsgType.FileTransferOffer, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferOffer, message.TransferId, source);
        Log($"FileTransferOffer received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, file_name_len={message.FileName.Length}, size_bytes={message.FileSizeBytes})");
        FileTransferOfferReceived?.Invoke(this, new FileTransferOfferReceivedEventArgs(message, source));
    }

    private void HandleFileTransferAccept(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferAccept, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeAccept(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_accept_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_accept_payload_invalid");
            Log($"FileTransferAccept payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_accept", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_accept", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_accept", MsgType.FileTransferAccept, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferAccept, message.TransferId, source);
        Log($"FileTransferAccept received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length})");
        FileTransferAcceptReceived?.Invoke(this, new FileTransferAcceptReceivedEventArgs(message, source));
    }

    private void HandleFileTransferDecline(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferDecline, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeDecline(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_decline_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_decline_payload_invalid");
            Log($"FileTransferDecline payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_decline", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_decline", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_decline", MsgType.FileTransferDecline, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDecline, message.TransferId, source);
        Log($"FileTransferDecline received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
        FileTransferDeclineReceived?.Invoke(this, new FileTransferDeclineReceivedEventArgs(message, source));
    }

    private void HandleFileTransferStart(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferStart, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeStart(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_start_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_start_payload_invalid");
            Log($"FileTransferStart payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_start", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_start", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_start", MsgType.FileTransferStart, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferStart, message.TransferId, source);
        Log($"FileTransferStart received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk_count={message.ChunkCount}, chunk_size={message.ChunkSizeBytes})");
        FileTransferStartReceived?.Invoke(this, new FileTransferStartReceivedEventArgs(message, source));
    }

    private void HandleFileTransferChunk(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferChunk, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeChunk(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_chunk_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_chunk_payload_invalid");
            Log($"FileTransferChunk payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_chunk", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_chunk", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_chunk", MsgType.FileTransferChunk, message.TransferId, env.MessageId, source))
        {
            return;
        }

        Log($"FileTransferChunk received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk={message.ChunkIndex + 1}/{message.ChunkCount})");
        FileTransferChunkReceived?.Invoke(this, new FileTransferChunkReceivedEventArgs(message, source));
    }

    private void HandleFileTransferCancel(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferCancel, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeCancel(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_cancel_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_cancel_payload_invalid");
            Log($"FileTransferCancel payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_cancel", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_cancel", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_cancel", MsgType.FileTransferCancel, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferCancel, message.TransferId, source);
        Log($"FileTransferCancel received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
        FileTransferCancelReceived?.Invoke(this, new FileTransferCancelReceivedEventArgs(message, source));
    }

    private void HandleFileTransferError(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferError, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeError(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_error_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_error_payload_invalid");
            Log($"FileTransferError payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_error", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_error", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_error", MsgType.FileTransferError, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferError, message.TransferId, source);
        Log($"FileTransferError received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, error_code={message.ErrorCode})");
        FileTransferErrorReceived?.Invoke(this, new FileTransferErrorReceivedEventArgs(message, source));
    }

    private void HandleFileTransferComplete(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferComplete, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeComplete(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_complete_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_complete_payload_invalid");
            Log($"FileTransferComplete payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_complete", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_complete", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_complete", MsgType.FileTransferComplete, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferComplete, message.TransferId, source);
        Log($"FileTransferComplete received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, size_bytes={message.FileSizeBytes})");
        FileTransferCompleteReceived?.Invoke(this, new FileTransferCompleteReceivedEventArgs(message, source));
    }

    private bool TryValidateFileTransferDispatchState(
        string messageType,
        MsgType transportMessageType,
        string transferId,
        string messageId,
        string? source)
    {
        if (TryValidateAndTrackFileTransferMessage(transportMessageType, transferId, inbound: true, applyStateChange: true, out var failureReason))
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; transfer_id={transferId}; source={source ?? "(none)"}; msg_id={messageId}");
        Log($"FileTransfer message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={transferId})");
        return false;
    }

    private void HandleControlRequest(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlRequest, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlRequest(securePayload.Plaintext, out var request))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlrequest_payload_invalid");
            Log($"ControlRequest payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_request", securePayload.Metadata, request.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_request",
                request.SessionId,
                env.MessageId,
                request.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlRequestReceived,
                request.RequestId,
                source,
                Decision: null));
        Log($"ControlRequest received (msg_id={env.MessageId}, request_id_len={request.RequestId.Length}, has_reason={request.Reason is not null})");
        RemoteControlRequestReceived?.Invoke(this, new RemoteControlRequestReceivedEventArgs(request, source));
    }

    private void HandleControlResponse(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlResponse, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlResponse(securePayload.Plaintext, out var response))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlresponse_payload_invalid");
            Log($"ControlResponse payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_response", securePayload.Metadata, response.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_response",
                response.SessionId,
                env.MessageId,
                response.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlResponseReceived,
                response.RequestId,
                source,
                response.Decision));
        Log($"ControlResponse received (msg_id={env.MessageId}, request_id_len={response.RequestId.Length}, decision={response.Decision})");
        RemoteControlResponseReceived?.Invoke(this, new RemoteControlResponseReceivedEventArgs(response, source));
    }

    private void HandleControlStart(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStart, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStart(securePayload.Plaintext, out var start))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstart_payload_invalid");
            Log($"ControlStart payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_start", securePayload.Metadata, start.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_start",
                start.SessionId,
                env.MessageId,
                start.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlStartReceived,
                start.RequestId,
                source,
                Decision: null));
        Log($"ControlStart received (msg_id={env.MessageId}, request_id_len={start.RequestId.Length}, has_token={start.ConsentToken is not null})");
        RemoteControlStartReceived?.Invoke(this, new RemoteControlStartReceivedEventArgs(start, source));
    }

    private void HandleControlStop(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStop, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStop(securePayload.Plaintext, out var stop))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstop_payload_invalid");
            Log($"ControlStop payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_stop", securePayload.Metadata, stop.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_stop",
                stop.SessionId,
                env.MessageId,
                stop.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlStopReceived,
                stop.RequestId,
                source,
                Decision: null));
        Log($"ControlStop received (msg_id={env.MessageId}, request_id_len={stop.RequestId.Length}, has_reason={stop.Reason is not null})");
        RemoteControlStopReceived?.Invoke(this, new RemoteControlStopReceivedEventArgs(stop, source));
    }

    private void HandleControlInput(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlInput, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlInput(securePayload.Plaintext, out var input))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlinput_payload_invalid");
            Log($"ControlInput payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_input", securePayload.Metadata, input.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_input",
                input.SessionId,
                env.MessageId,
                input.RequestId,
                source))
        {
            return;
        }

        LogControlInputReceived(env, input);
        RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(input, source));
    }

    private void HandleControlAck(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlAck, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlAck(securePayload.Plaintext, out var ack))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlack_payload_invalid");
            Log($"ControlAck payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_ack", securePayload.Metadata, ack.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_ack",
                ack.SessionId,
                env.MessageId,
                ack.RequestId,
                source))
        {
            return;
        }

        Log($"ControlAck received (msg_id={env.MessageId}, request_id_len={ack.RequestId.Length}, ack_seq={ack.AckSeq})");
        RemoteControlAckReceived?.Invoke(this, new RemoteControlAckReceivedEventArgs(ack, source));
    }

    private void HandleControlStateSnapshot(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStateSnapshot, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(securePayload.Plaintext, out var snapshot))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstatesnapshot_payload_invalid");
            Log($"ControlStateSnapshot payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_state_snapshot", securePayload.Metadata, snapshot.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_state_snapshot",
                snapshot.SessionId,
                env.MessageId,
                snapshot.RequestId,
                source))
        {
            return;
        }

        Log($"ControlStateSnapshot received (msg_id={env.MessageId}, request_id_len={snapshot.RequestId.Length}, seq={snapshot.Seq}, buttons_mask={snapshot.MouseButtonsMask}, modifiers_mask={snapshot.ModifiersMask})");
        RemoteControlStateSnapshotReceived?.Invoke(this, new RemoteControlStateSnapshotReceivedEventArgs(snapshot, source));
    }

    private void LogControlInputReceived(Envelope env, ControlInputMessageV1 input)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var shouldLogInput = false;
        var suppressedCountToReport = 0;

        lock (controlInputReceiveLogGate)
        {
            if (controlInputReceiveLogWindowStartTicks == 0)
            {
                controlInputReceiveLogWindowStartTicks = nowTicks;
            }
            else if (Stopwatch.GetElapsedTime(controlInputReceiveLogWindowStartTicks, nowTicks) >= ControlInputReceiveLogWindow)
            {
                suppressedCountToReport = controlInputReceiveLogSuppressed;
                controlInputReceiveLogSuppressed = 0;
                controlInputReceiveLogCount = 0;
                controlInputReceiveLogWindowStartTicks = nowTicks;
            }

            if (controlInputReceiveLogCount < ControlInputReceiveLogBurst)
            {
                controlInputReceiveLogCount++;
                shouldLogInput = true;
            }
            else
            {
                controlInputReceiveLogSuppressed++;
            }
        }

        if (suppressedCountToReport > 0)
        {
            Log($"ControlInput received (suppressed={suppressedCountToReport} in previous {ControlInputReceiveLogWindow.TotalSeconds:0.#}s window)");
        }

        if (shouldLogInput)
        {
            Log($"ControlInput received (msg_id={env.MessageId}, request_id_len={input.RequestId.Length}, kind={input.Kind}, seq={input.Seq})");
        }
    }

    private void HandleControlDisplayInfo(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlDisplayInfo, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(securePayload.Plaintext, out var displayInfo))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controldisplayinfo_payload_invalid");
            Log($"ControlDisplayInfo payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_display_info", securePayload.Metadata, requestId: null, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_display_info",
                displayInfo.SessionId,
                env.MessageId,
                transportRemoteControlState.CurrentControlRequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.DisplayInfoChanged,
                transportRemoteControlState.CurrentControlRequestId,
                source,
                Decision: null));
        Log($"ControlDisplayInfo received (msg_id={env.MessageId}, display_id={displayInfo.DisplayId}, revision={displayInfo.Revision}, frame={displayInfo.FrameWidth}x{displayInfo.FrameHeight})");
        RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(displayInfo, source));
    }

    private bool TryValidateControlMessageSession(
        string messageType,
        string? messageSessionId,
        string messageId,
        string? requestId,
        string? source)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
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
        else if (!string.IsNullOrWhiteSpace(expectedSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) &&
                 !AddressMatchesForSessionPolicy(normalizedSource, expectedSource))
        {
            failureReason = "source_identity_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=control_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; request_id={requestId ?? "(none)"}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}");
        Log($"Control message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, request_id={requestId ?? "(none)"})");
        return false;
    }

    private byte[] CreateSecureControlPayload(MsgType messageType, string? requestId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.RemoteControl,
            MessageType: MapSecureControlMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundControlSecureSequence),
            RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim());
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptControlPayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_session_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_expected_sender_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_session_key_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.RemoteControl,
                    MessageType: MapSecureControlMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={MapSecureControlMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundControlReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={MapSecureControlMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateControlSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string? requestId,
        string messageId)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        var normalizedMetadataRequestId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        if (!string.Equals(normalizedMetadataRequestId, normalizedRequestId, StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_request_id_mismatch");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_request_id_mismatch");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={messageType}; reason=secure_request_id_mismatch; request_id={normalizedRequestId ?? "(none)"}; secure_request_id={normalizedMetadataRequestId ?? "(none)"}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_request_id_mismatch)");
            return false;
        }

        return true;
    }

    private byte[] CreateSecureLifecyclePayload(MsgType messageType, string? requestId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Lifecycle,
            MessageType: MapSecureLifecycleMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
            RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim());
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureScreenSharePayload(MsgType messageType, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.ScreenShare,
            MessageType: MapSecureScreenShareMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundScreenShareSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureFileTransferPayload(MsgType messageType, string transferId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetFileTransferSessionSharedKeyOrThrow();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.FileTransfer,
            MessageType: MapSecureFileTransferMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundFileTransferSecureSequence),
            RequestId: normalizedTransferId);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureFileTransferPayloadForBudgetEstimate(MsgType messageType, string transferId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetFileTransferSessionSharedKeyOrThrow();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.FileTransfer,
            MessageType: MapSecureFileTransferMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Math.Max(1L, Interlocked.Read(ref nextOutboundFileTransferSecureSequence) + 1),
            RequestId: normalizedTransferId);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptLifecyclePayload(
        string? source,
        string messageId,
        string messageType,
        byte[] encodedPayload,
        byte[]? key,
        SessionSecureEnvelopeExpectation expectation,
        SessionReplayWindow replayWindow,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (key is null || key.Length == 0)
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_session_key_unavailable");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(key, encodedPayload, expectation);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=lifecycle_message_rejected; message_type={messageType}; reason=secure_envelope_invalid; session_id={expectation.SessionId?.Value ?? "(none)"}; source={source ?? "(none)"}; expected_source={expectation.SenderIdentity?.Value ?? "(none)"}; msg_id={messageId}; ex={ex.GetType().Name}");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = replayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{messageType}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=lifecycle_message_rejected; message_type={messageType}; reason={replayReason}; session_id={securePayload.Metadata.SessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={messageId}");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryDecryptScreenSharePayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_session_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_expected_sender_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_session_key_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.ScreenShare,
                    MessageType: MapSecureScreenShareMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={MapSecureScreenShareMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundScreenShareReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={MapSecureScreenShareMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateScreenShareSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string messageId)
    {
        if (!string.IsNullOrWhiteSpace(metadata.RequestId))
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_request_id_present");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_request_id_present");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={messageType}; reason=secure_request_id_present; secure_request_id={metadata.RequestId}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_request_id_present)");
            return false;
        }

        return true;
    }

    private bool TryDecryptFileTransferPayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_expected_sender_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetFileTransferSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_session_key_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.FileTransfer,
                    MessageType: MapSecureFileTransferMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={MapSecureFileTransferMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundFileTransferReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={MapSecureFileTransferMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateFileTransferSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string transferId,
        string messageId)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var normalizedMetadataTransferId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        if (string.Equals(normalizedMetadataTransferId, normalizedTransferId, StringComparison.Ordinal))
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_transfer_id_mismatch");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_transfer_id_mismatch");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason=secure_transfer_id_mismatch; transfer_id={normalizedTransferId}; secure_transfer_id={normalizedMetadataTransferId ?? "(none)"}");
        Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_transfer_id_mismatch)");
        return false;
    }

    private bool TryValidateFileTransferMessageSession(
        string messageType,
        string? messageSessionId,
        string transferId,
        string messageId,
        string? source)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
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
        else if (!string.IsNullOrWhiteSpace(expectedSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) &&
                 !AddressMatchesForSessionPolicy(normalizedSource, expectedSource))
        {
            failureReason = "source_identity_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; transfer_id={normalizedTransferId}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}");
        Log($"FileTransfer message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={normalizedTransferId})");
        return false;
    }

    private PeerAddress ResolveLocalPeerAddressForSecureEnvelope()
    {
        if (PeerAddress.TryParse(LocalPeerAddress, out var localAddress))
        {
            return localAddress;
        }

        throw new InvalidOperationException("Local peer address is not available for secure control payloads.");
    }

    private byte[] GetControlSessionSharedKeyOrThrow()
    {
        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is null || controlSessionSharedKey.Length == 0)
            {
                throw new InvalidOperationException("Session shared key is not available.");
            }

            return controlSessionSharedKey.AsSpan().ToArray();
        }
    }

    private byte[]? TryGetControlSessionSharedKey()
    {
        lock (controlSecureStateGate)
        {
            return controlSessionSharedKey is null || controlSessionSharedKey.Length == 0
                ? null
                : controlSessionSharedKey.AsSpan().ToArray();
        }
    }

    private void SetControlSessionSharedKey(byte[] sharedKey)
    {
        ArgumentNullException.ThrowIfNull(sharedKey);

        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
            }

            if (fileTransferSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(fileTransferSessionSharedKey);
            }

            controlSessionSharedKey = sharedKey.AsSpan().ToArray();
            fileTransferSessionSharedKey = SessionKeyDerivation.DeriveFileTransferKey(sharedKey);
            nextOutboundControlSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }

        ResetInboundFileTransferDispatchQueue();
    }

    private void ResetControlSecureState()
    {
        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
                controlSessionSharedKey = null;
            }

            if (fileTransferSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(fileTransferSessionSharedKey);
                fileTransferSessionSharedKey = null;
            }

            nextOutboundControlSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }

        ResetInboundFileTransferDispatchQueue();
    }

    private byte[] GetFileTransferSessionSharedKeyOrThrow()
    {
        lock (controlSecureStateGate)
        {
            if (fileTransferSessionSharedKey is null || fileTransferSessionSharedKey.Length == 0)
            {
                throw new InvalidOperationException("File transfer session shared key is not available.");
            }

            return fileTransferSessionSharedKey.AsSpan().ToArray();
        }
    }

    private bool TryValidateAndTrackFileTransferMessage(
        MsgType messageType,
        string transferId,
        bool inbound,
        bool applyStateChange,
        out string failureReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (controlSecureStateGate)
        {
            if (!TryGetNextFileTransferState(messageType, normalizedTransferId, inbound, out var nextState, out failureReason))
            {
                return false;
            }

            if (applyStateChange)
            {
                CommitFileTransferStateLocked(normalizedTransferId, nextState);
            }

            return true;
        }
    }

    private bool TryGetNextFileTransferState(
        MsgType messageType,
        string transferId,
        bool inbound,
        out FileTransferTransportState nextState,
        out string failureReason)
    {
        var hasExisting = fileTransferStates.TryGetValue(transferId, out var currentState);
        nextState = default;
        failureReason = string.Empty;

        if (messageType == MsgType.FileTransferOffer)
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

            nextState = new FileTransferTransportState(initiatedLocally, FileTransferTransportPhase.Offered);
            return true;
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

        if (messageType == MsgType.FileTransferAccept)
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

        if (messageType == MsgType.FileTransferDecline)
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

        if (messageType == MsgType.FileTransferStart)
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

        if (messageType == MsgType.FileTransferChunk)
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

        if (messageType == MsgType.FileTransferComplete)
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

        if (messageType == MsgType.FileTransferCancel)
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "cancel_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Canceled };
            return true;
        }

        if (messageType == MsgType.FileTransferError)
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "error_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Failed };
            return true;
        }

        failureReason = "unsupported_message_type";
        return false;
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

    private void CommitFileTransferStateLocked(string transferId, FileTransferTransportState nextState)
    {
        if (nextState.IsTerminal)
        {
            fileTransferStates.Remove(transferId);
            return;
        }

        fileTransferStates[transferId] = nextState;
    }

    private static bool CanTransitionToTerminalFileTransferState(FileTransferTransportPhase phase)
        => phase is FileTransferTransportPhase.Offered or
            FileTransferTransportPhase.Accepted or
            FileTransferTransportPhase.Started or
            FileTransferTransportPhase.Transferring;

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

    private static string MapSecureControlMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.ControlRequest => "control_request",
            MsgType.ControlResponse => "control_response",
            MsgType.ControlStart => "control_start",
            MsgType.ControlStop => "control_stop",
            MsgType.ControlInput => "control_input",
            MsgType.ControlAck => "control_ack",
            MsgType.ControlStateSnapshot => "control_state_snapshot",
            MsgType.ControlDisplayInfo => "control_display_info",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure control message type."),
        };
    }

    private static string MapSecureLifecycleMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.Approve => "approve",
            MsgType.Reject => "reject",
            MsgType.SessionEnd => "session_end",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure lifecycle message type."),
        };
    }

    private static string MapSecureScreenShareMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.ScreenShareFrame => "screenshare_frame",
            MsgType.ScreenShareStop => "screenshare_stop",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure screen-share message type."),
        };
    }

    private static string MapSecureFileTransferMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.FileTransferOffer => "file_transfer_offer",
            MsgType.FileTransferAccept => "file_transfer_accept",
            MsgType.FileTransferDecline => "file_transfer_decline",
            MsgType.FileTransferStart => "file_transfer_start",
            MsgType.FileTransferChunk => "file_transfer_chunk",
            MsgType.FileTransferCancel => "file_transfer_cancel",
            MsgType.FileTransferError => "file_transfer_error",
            MsgType.FileTransferComplete => "file_transfer_complete",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure file-transfer message type."),
        };
    }

    private static void LogFileTransferEnvelopeEvent(string direction, MsgType messageType, string transferId, string? source)
    {
        if (messageType == MsgType.FileTransferChunk)
        {
            return;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_envelope_{direction}; transport=nkn; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={transferId}; source={source ?? "(none)"}");
    }

    private bool TryValidatePendingOutboundLifecycleMessage(
        string messageType,
        string? messageSessionId,
        string messageId,
        string? replyTo,
        string? source,
        PendingOutboundHandshakeState pending,
        string? expectedJoinRequestId)
    {
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (!string.Equals(normalizedMessageSessionId, pending.SessionId.Value, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!AddressMatchesForSessionPolicy(normalizedSource, pending.HelpeeAddress.Value))
        {
            failureReason = "source_identity_mismatch";
        }
        else if (!string.IsNullOrWhiteSpace(expectedJoinRequestId) && string.IsNullOrWhiteSpace(replyTo))
        {
            failureReason = "missing_replyto";
        }
        else if (!string.IsNullOrWhiteSpace(expectedJoinRequestId) &&
                 !string.IsNullOrWhiteSpace(replyTo) &&
                 !string.Equals(replyTo, expectedJoinRequestId, StringComparison.Ordinal))
        {
            failureReason = "replyto_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=lifecycle_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={pending.SessionId.Value}; source={normalizedSource ?? "(none)"}; expected_source={pending.HelpeeAddress.Value}; reply_to={replyTo ?? "(none)"}; expected_reply_to={expectedJoinRequestId ?? "(none)"}");
        Log($"Lifecycle message rejected (type={messageType}, msg_id={messageId}, reason={failureReason})");
        return false;
    }

    private bool TryValidatePendingInboundLifecycleMessage(
        string messageType,
        string? messageSessionId,
        string messageId,
        string? replyTo,
        string? source,
        PendingJoinRequestState pending)
    {
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (!string.Equals(normalizedMessageSessionId, pending.SessionId.Value, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!AddressMatchesForSessionPolicy(normalizedSource, pending.HelperAddress.Value))
        {
            failureReason = "source_identity_mismatch";
        }
        else if (string.IsNullOrWhiteSpace(replyTo))
        {
            failureReason = "missing_replyto";
        }
        else if (!string.Equals(replyTo, pending.JoinRequestMessageId, StringComparison.Ordinal))
        {
            failureReason = "replyto_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=lifecycle_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={pending.SessionId.Value}; source={normalizedSource ?? "(none)"}; expected_source={pending.HelperAddress.Value}; reply_to={replyTo ?? "(none)"}; expected_reply_to={pending.JoinRequestMessageId}");
        Log($"Lifecycle message rejected (type={messageType}, msg_id={messageId}, reason={failureReason})");
        return false;
    }

    private string? ResolveExpectedRemotePeerAddressForCurrentSession()
    {
        var localAddress = LocalPeerAddress;

        if (currentSessionSecurityState.HelperAddress is PeerAddress helperAddress &&
            !AddressesLikelySamePeer(helperAddress.Value, localAddress))
        {
            return helperAddress.Value;
        }

        if (currentSessionSecurityState.HelpeeAddress is PeerAddress helpeeAddress &&
            !AddressesLikelySamePeer(helpeeAddress.Value, localAddress))
        {
            return helpeeAddress.Value;
        }

        return string.IsNullOrWhiteSpace(remoteEndpoint) ? null : remoteEndpoint;
    }

    private PeerAddress? TryResolveExpectedRemotePeerAddressForLifecycle()
    {
        var expected = ResolveExpectedRemotePeerAddressForCurrentSession();
        return PeerAddress.TryParse(expected, out var peerAddress) ? peerAddress : null;
    }

    private bool TryValidateScreenShareSession(string messageType, string? messageSessionId)
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

        LogScreenShareRejected(messageType, failureReason, normalizedMessageSessionId);
        return false;
    }

    private bool IsScreenShareAuthorizedForDispatch(string messageType, string? messageSessionId)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        string failureReason;

        if (!currentSessionSecurityState.InviteValidated)
        {
            failureReason = "invite_not_validated";
        }
        else if (!currentSessionSecurityState.HandshakeCompleted ||
                 currentSessionSecurityState.HandshakeState != SessionHandshakeState.Verified)
        {
            failureReason = "handshake_not_verified";
        }
        else if (!currentSessionSecurityState.HasCapability(CapabilityGrant.ScreenShare, nowUtc))
        {
            failureReason = currentSessionSecurityState.ApprovalGranted ? "capability_missing" : "approval_missing";
        }
        else
        {
            return true;
        }

        LogScreenShareRejected(messageType, failureReason, messageSessionId);
        return false;
    }

    private void LogScreenShareRejected(string messageType, string reason, string? sessionId)
    {
        NknRuntimeDiagnostics.SetLastError($"screenshare_{reason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"screenshare_{reason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screen_share_message_rejected; message_type={messageType}; reason={reason}; session_id={sessionId ?? "(none)"}; expected_session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={currentSessionSecurityState.HelperAddress?.Value ?? "(none)"}");
        Log($"Screen share message rejected (type={messageType}, reason={reason}, session_id={sessionId ?? "(none)"})");
    }

    private static bool TryParseScreenSharePayload(ReadOnlySpan<byte> payload, out string messageType, out string? messageSessionId)
    {
        if (ScreenSharePayloadCodec.TryDeserialize(payload, out var chunk))
        {
            messageType = "frame";
            messageSessionId = chunk.SessionId;
            return true;
        }

        if (ScreenSharePayloadCodec.TryDeserializeStop(payload, out var stop))
        {
            messageType = "stop";
            messageSessionId = stop.SessionId;
            return true;
        }

        messageType = "payload";
        messageSessionId = null;
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

    private FileTransferOfferV1 EnsureFileTransferSessionId(FileTransferOfferV1 message)
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

    private FileTransferStartV1 EnsureFileTransferSessionId(FileTransferStartV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferChunkV1 EnsureFileTransferSessionId(FileTransferChunkV1 message)
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

    private string ResolveControlSessionId(string? current)
    {
        return string.IsNullOrWhiteSpace(current)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : current.Trim();
    }

    private void HandleAck(string source, Envelope env)
    {
        if (string.IsNullOrWhiteSpace(env.ReplyTo))
        {
            NknRuntimeDiagnostics.SetLastError("ack_missing_replyto");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("ack_missing_replyto");
            Log($"Ack missing reply_to (msg_id={env.MessageId})");
            return;
        }
        NknRuntimeDiagnostics.IncrementAcksReceived();

        if (!pendingAcks.TryGetValue(env.ReplyTo, out var pending))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("ack_no_pending_wait");
            Log($"Ack ignored (no pending wait, msg_id={env.MessageId}, reply_to={env.ReplyTo})");
            return;
        }

        if (!AddressesLikelySamePeer(source, pending.Destination))
        {
            NknRuntimeDiagnostics.IncrementAcksIgnoredSourceMismatch();
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("ack_source_mismatch");
            Log($"Ack ignored (source mismatch, msg_id={env.MessageId}, reply_to={env.ReplyTo}, source_len={source?.Length ?? 0})");
            return;
        }

        if (pendingAcks.TryRemove(env.ReplyTo, out var removed))
        {
            removed.Completion.TrySetResult(true);
        }

        Log($"Ack handled (msg_id={env.MessageId}, reply_to={env.ReplyTo})");
    }

    private async Task ApproveJoinRequestAsync(string joinRequestMessageId, ApprovalDecision? decision, CancellationToken ct)
    {
        PendingJoinRequestState? pending;
        if (!TryBeginPendingJoinDecision(joinRequestMessageId, out pending))
        {
            Log($"Approve ignored (join_msg_id={joinRequestMessageId}, reason=already_handled_or_missing)");
            return;
        }

        try
        {
            var pendingState = pending!;
            if (pendingState.ApprovalRequest is null)
            {
                LocalOperationalLog.Warn("SessionSecurity", "event=approval_denied; reason=approval_request_missing");
                throw new InvalidOperationException("Approval decision does not match the pending join request.");
            }

            if (decision is null)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=approval_denied; reason=approval_decision_missing; session_id={pendingState.ApprovalRequest.SessionId.Value}; helper_identity={pendingState.ApprovalRequest.HelperIdentity.Value}; requested_capabilities={pendingState.ApprovalRequest.RequestedCapabilities}");
                throw new InvalidOperationException("Explicit approval decision is required.");
            }

            if (decision.SessionId != pendingState.ApprovalRequest.SessionId ||
                decision.HelperIdentity != pendingState.ApprovalRequest.HelperIdentity ||
                decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                (decision.ApprovedCapabilities & ~pendingState.ApprovalRequest.RequestedCapabilities) != 0)
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=approval_denied; reason=approval_decision_mismatch; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; approved_capabilities={decision.ApprovedCapabilities}; requested_capabilities={pendingState.ApprovalRequest.RequestedCapabilities}");
                throw new InvalidOperationException("Approval decision does not match the pending join request.");
            }

            if (!TryGetHelpeeHostKeyPair(out var helpeeKeyPair) || helpeeKeyPair is null)
            {
                NknRuntimeDiagnostics.SetLastError("host_ecdh_not_ready");
                Log($"Approve failed (join_msg_id={joinRequestMessageId}, reason=no_host_key)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (string.IsNullOrWhiteSpace(pendingState.EnvelopeCode))
            {
                NknRuntimeDiagnostics.SetLastError("approve_missing_session_context");
                Log($"Approve failed (join_msg_id={joinRequestMessageId}, reason=no_session_context)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = DeriveSessionKey(helpeeKeyPair, pendingState.HelperEcdhPublicKey, pendingState.EnvelopeCode);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_key_derivation_failed");
                Log($"Approve key derivation failed (join_msg_id={joinRequestMessageId}, ex={ex.GetType().Name})");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            var securePayload = SessionSecureEnvelopeCodec.Encrypt(
                sharedKey,
                new SessionSecureEnvelopeMetadata(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "approve",
                    SessionId: decision.SessionId,
                    SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
                    Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
                    RequestId: joinRequestMessageId),
                JsonSerializer.SerializeToUtf8Bytes(new ApproveSecurePayload
                {
                    remoteControlSupported = LocalSupportsRemoteControl,
                    approvalDecisionBase64 = Convert.ToBase64String(SessionHandshakeProtocol.Serialize(decision)),
                }));

            var approvePayload = new ApprovePayload
            {
                sessionId = decision.SessionId.Value,
                helpeeEcdhPublicKey = Convert.ToBase64String(helpeeKeyPair.PublicKey),
                secureEnvelopeBase64 = Convert.ToBase64String(securePayload),
            };

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(approvePayload);
            var envelope = CreateEnvelope(pendingState.EnvelopeCode, MsgType.Approve, payloadBytes, joinRequestMessageId);
            await SendEnvelopeAsync(pendingState.RemoteEndpoint, envelope, ct);

            SetControlSessionSharedKey(sharedKey);
            UpdateSessionSecurityState(currentSessionSecurityState.WithApproval(decision.ToGrant()));
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=approval_granted; session_id={decision.SessionId.Value}; helper_identity={decision.HelperIdentity.Value}; capabilities={decision.ApprovedCapabilities}; expires_at_utc={decision.ExpiresAtUtc:O}");
            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
            Approved?.Invoke(this, EventArgs.Empty);
            Log($"Approve sent (msg_id={envelope.MessageId}, reply_to={envelope.ReplyTo})");
        }
        finally
        {
            ClearPendingJoinRequest(joinRequestMessageId);
        }
    }

    private async Task RejectJoinRequestAsync(string joinRequestMessageId, string? reason, CancellationToken ct)
    {
        PendingJoinRequestState? pending;
        if (!TryBeginPendingJoinDecision(joinRequestMessageId, out pending))
        {
            Log($"Reject ignored (join_msg_id={joinRequestMessageId}, reason=already_handled_or_missing)");
            return;
        }

        try
        {
            var rejectionReason = string.IsNullOrWhiteSpace(reason) ? "join_rejected" : reason.Trim();
            if (string.IsNullOrWhiteSpace(pending!.EnvelopeCode))
            {
                NknRuntimeDiagnostics.SetLastError("reject_missing_session_context");
                Log($"Reject failed (join_msg_id={joinRequestMessageId}, reason=no_session_context)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!TryGetHelpeeHostKeyPair(out var helpeeKeyPair) || helpeeKeyPair is null)
            {
                NknRuntimeDiagnostics.SetLastError("host_ecdh_not_ready");
                Log($"Reject failed (join_msg_id={joinRequestMessageId}, reason=no_host_key)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = DeriveSessionKey(helpeeKeyPair, pending.HelperEcdhPublicKey, pending.EnvelopeCode);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("reject_key_derivation_failed");
                Log($"Reject key derivation failed (join_msg_id={joinRequestMessageId}, ex={ex.GetType().Name})");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            var securePayload = SessionSecureEnvelopeCodec.Encrypt(
                sharedKey,
                new SessionSecureEnvelopeMetadata(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "reject",
                    SessionId: pending.SessionId,
                    SenderIdentity: ResolveLocalPeerAddressForSecureEnvelope(),
                    Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
                    RequestId: joinRequestMessageId),
                JsonSerializer.SerializeToUtf8Bytes(new RejectSecurePayload
                {
                    reason = rejectionReason,
                }));

            var rejectPayload = new RejectPayload
            {
                sessionId = pending.SessionId.Value,
                helpeeEcdhPublicKey = Convert.ToBase64String(helpeeKeyPair.PublicKey),
                secureEnvelopeBase64 = Convert.ToBase64String(securePayload),
            };
            var envelope = CreateEnvelope(
                pending.EnvelopeCode,
                MsgType.Reject,
                JsonSerializer.SerializeToUtf8Bytes(rejectPayload),
                joinRequestMessageId);
            await SendEnvelopeAsync(pending.RemoteEndpoint, envelope, ct);
            if (pending.ApprovalRequest is ApprovalRequest approvalRequest)
            {
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event=approval_denied; reason={rejectionReason}; session_id={approvalRequest.SessionId.Value}; helper_identity={approvalRequest.HelperIdentity.Value}; requested_capabilities={approvalRequest.RequestedCapabilities}");
            }
            Rejected?.Invoke(this, EventArgs.Empty);
            Log($"Reject sent (msg_id={envelope.MessageId}, reply_to={envelope.ReplyTo})");
        }
        finally
        {
            ClearPendingJoinRequest(joinRequestMessageId);
        }
    }

    private async Task SendEnvelopeAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        var bytes = EnvelopeCodec.Serialize(envelope);

        try
        {
            await outboundSendGate.WaitAsync(ct).ConfigureAwait(false);
            NknRuntimeDiagnostics.IncrementMessagesSent();
            try
            {
                await client.SendAsync(destination, bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                outboundSendGate.Release();
            }
            Log($"Envelope sent (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"Envelope send failed (type={envelope.Type}, msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
            throw;
        }
    }

    private async Task SendEnvelopeWithAckRetryAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        var ackWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAck = new PendingAckWait(destination, ackWait);
        if (!pendingAcks.TryAdd(envelope.MessageId, pendingAck))
        {
            throw new InvalidOperationException("pending_ack_exists");
        }

        try
        {
            for (var attempt = 0; attempt <= AckRetryDelays.Length; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                await SendEnvelopeAsync(destination, envelope, ct);

                try
                {
                    await ackWait.Task.WaitAsync(AckWaitTimeout, ct);
                    Log($"Ack received (msg_id={envelope.MessageId}, type={envelope.Type}, attempt={attempt + 1})");
                    return;
                }
                catch (TimeoutException)
                {
                    if (attempt == AckRetryDelays.Length)
                    {
                        NknRuntimeDiagnostics.SetLastError("ack_timeout");
                        Log($"Ack timeout (msg_id={envelope.MessageId}, type={envelope.Type}, attempts={attempt + 1})");
                        if (envelope.Type != MsgType.Chat)
                        {
                            Disconnected?.Invoke(this, EventArgs.Empty);
                        }
                        throw new TimeoutException("Ack was not received.");
                    }

                    var delay = AckRetryDelays[attempt];
                    Log($"Ack retry scheduled (msg_id={envelope.MessageId}, type={envelope.Type}, next_attempt={attempt + 2}, delay_ms={delay.TotalMilliseconds:0})");
                    await Task.Delay(delay, ct);
                }
            }
        }
        finally
        {
            pendingAcks.TryRemove(envelope.MessageId, out _);
        }
    }

    private void SendAckFireAndForget(string destination, string code, string replyToMessageId)
    {
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(replyToMessageId))
        {
            return;
        }

        var ackCode = string.IsNullOrWhiteSpace(code)
            ? (TryGetCurrentEnvelopeCode(out var currentContextCode) ? currentContextCode : "000000")
            : code;
        var ackEnvelope = CreateEnvelope(ackCode, MsgType.Ack, Array.Empty<byte>(), replyToMessageId);

        _ = Task.Run(async () =>
        {
            try
            {
                await SendEnvelopeAsync(destination, ackEnvelope, CancellationToken.None);
                Log($"Ack sent (msg_id={ackEnvelope.MessageId}, reply_to={replyToMessageId})");
            }
            catch (Exception ex)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                Log($"Ack send failed (reply_to={replyToMessageId}, ex={ex.GetType().Name})");
            }
        });
    }

    private async Task CleanupAsync()
    {
        CancelPendingAcks();
        ResetSessionTracking();

        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log($"DisconnectAsync failed ({ex.GetType().Name})");
        }
    }

    private void ResetSessionTracking()
    {
        FlushAllControlOutboundQueues("reset_session_tracking");
        ResetControlSecureState();
        secureScreenShareFrameReassembler.ClearAll();
        remoteEndpoint = null;
        currentEnvelopeCode = null;
        lastPeerAddress = null;
        helperJoinRequestMessageId = null;
        pendingOutboundHandshake = null;
        remoteSupportsRemoteControl = false;
        transportRemoteControlState = RemoteControlSessionState.Default;

        DisposeEphemeralKeyState();
        UpdateSessionSecurityState(SessionSecurityState.Empty);
    }

    private void DisposeEphemeralKeyState(bool preserveHelpeeHostKeyPair = false)
    {
        SessionEcdhKeyPair? helperKeyToDispose = null;
        SessionEcdhKeyPair? helpeeHostKeyToDispose = null;
        CancelPendingInboundHandshakeTimeout();

        lock (gate)
        {
            if (!preserveHelpeeHostKeyPair)
            {
                helpeeHostKeyToDispose = helpeeHostEcdhKeyPair;
                helpeeHostEcdhKeyPair = null;
            }

            helperKeyToDispose = helperJoinEcdhKeyPair;
            helperJoinEcdhKeyPair = null;

            pendingJoinRequest = null;
            pendingInboundHandshake = null;
        }

        helperKeyToDispose?.Dispose();
        helpeeHostKeyToDispose?.Dispose();
    }

    private void ClearActivePeerSessionTracking(bool preserveHelpeeHostKeyPair)
    {
        ResetControlSecureState();
        secureScreenShareFrameReassembler.ClearAll();
        remoteEndpoint = null;
        currentEnvelopeCode = null;
        lastPeerAddress = null;
        helperJoinRequestMessageId = null;
        pendingOutboundHandshake = null;
        remoteSupportsRemoteControl = false;
        transportRemoteControlState = RemoteControlSessionState.Default;
        DisposeEphemeralKeyState(preserveHelpeeHostKeyPair);
    }

    private void CancelPendingAcks()
    {
        foreach (var pair in pendingAcks)
        {
            if (pendingAcks.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetCanceled();
            }
        }
    }

    private void ReplaceHelpeeHostKeyPair(SessionEcdhKeyPair keyPair)
    {
        SessionEcdhKeyPair? previous = null;
        lock (gate)
        {
            previous = helpeeHostEcdhKeyPair;
            helpeeHostEcdhKeyPair = keyPair;
        }

        previous?.Dispose();
    }

    private bool TryGetHelpeeHostKeyPair(out SessionEcdhKeyPair? keyPair)
    {
        lock (gate)
        {
            keyPair = helpeeHostEcdhKeyPair;
            return keyPair is not null;
        }
    }

    private void ReplaceHelperJoinKeyPair(SessionEcdhKeyPair keyPair)
    {
        SessionEcdhKeyPair? previous = null;
        lock (gate)
        {
            previous = helperJoinEcdhKeyPair;
            helperJoinEcdhKeyPair = keyPair;
        }

        previous?.Dispose();
    }

    private SessionEcdhKeyPair GetHelperJoinKeyPairOrThrow()
    {
        lock (gate)
        {
            return helperJoinEcdhKeyPair ?? throw new InvalidOperationException("helper_join_ecdh_missing");
        }
    }

    private void ClearHelperJoinKeyPair()
    {
        SessionEcdhKeyPair? toDispose = null;
        lock (gate)
        {
            toDispose = helperJoinEcdhKeyPair;
            helperJoinEcdhKeyPair = null;
            helperJoinRequestMessageId = null;
        }

        toDispose?.Dispose();
    }

    private void ReplacePendingJoinRequest(PendingJoinRequestState state)
    {
        lock (gate)
        {
            pendingJoinRequest = state;
        }
    }

    private void ReplacePendingInboundHandshake(PendingInboundHandshakeState state)
    {
        lock (gate)
        {
            pendingInboundHandshake = state;
        }

        SchedulePendingInboundHandshakeTimeout(state);
    }

    private void UpdatePendingInboundHandshake(PendingInboundHandshakeState state)
    {
        lock (gate)
        {
            pendingInboundHandshake = state;
        }

        SchedulePendingInboundHandshakeTimeout(state);
    }

    private bool TryGetPendingInboundHandshake(out PendingInboundHandshakeState? state)
    {
        lock (gate)
        {
            state = pendingInboundHandshake;
            return state is not null;
        }
    }

    private bool TryBeginPendingJoinDecision(string joinRequestMessageId, out PendingJoinRequestState? state)
    {
        lock (gate)
        {
            state = pendingJoinRequest;
            if (state is null)
            {
                return false;
            }

            if (!string.Equals(state.JoinRequestMessageId, joinRequestMessageId, StringComparison.Ordinal))
            {
                state = null;
                return false;
            }

            if (state.DecisionSent)
            {
                state = null;
                return false;
            }

            state.DecisionSent = true;
            return true;
        }
    }

    private void ClearPendingJoinRequest(string joinRequestMessageId)
    {
        lock (gate)
        {
            if (pendingJoinRequest is null)
            {
                return;
            }

            if (!string.Equals(pendingJoinRequest.JoinRequestMessageId, joinRequestMessageId, StringComparison.Ordinal))
            {
                return;
            }

            pendingJoinRequest = null;
        }
    }

    private void ClearPendingInboundHandshake(string joinRequestMessageId)
    {
        var cleared = false;
        lock (gate)
        {
            if (pendingInboundHandshake is null)
            {
                return;
            }

            if (!string.Equals(pendingInboundHandshake.JoinRequestMessageId, joinRequestMessageId, StringComparison.Ordinal))
            {
                return;
            }

            pendingInboundHandshake = null;
            cleared = true;
        }

        if (cleared)
        {
            CancelPendingInboundHandshakeTimeout();
        }
    }

    private void SchedulePendingInboundHandshakeTimeout(PendingInboundHandshakeState state)
    {
        CancelPendingInboundHandshakeTimeout();

        if (disposed)
        {
            return;
        }

        var deadlineUtc = GetPendingInboundHandshakeDeadlineUtc(state);
        var delay = deadlineUtc - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.FromMilliseconds(1))
        {
            delay = TimeSpan.FromMilliseconds(1);
        }

        var generation = Interlocked.Increment(ref pendingInboundHandshakeTimeoutGeneration);
        pendingInboundHandshakeTimeoutTimer = new Timer(
            static s =>
            {
                var state = (Tuple<NknSignalingTransport, long>)s!;
                state.Item1.OnPendingInboundHandshakeTimeout(state.Item2);
            },
            Tuple.Create(this, generation),
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelPendingInboundHandshakeTimeout()
    {
        Interlocked.Increment(ref pendingInboundHandshakeTimeoutGeneration);
        var timer = Interlocked.Exchange(ref pendingInboundHandshakeTimeoutTimer, null);
        timer?.Dispose();
    }

    private void OnPendingInboundHandshakeTimeout(long generation)
    {
        if (disposed || generation != Volatile.Read(ref pendingInboundHandshakeTimeoutGeneration))
        {
            return;
        }

        PendingInboundHandshakeState? pending;
        lock (gate)
        {
            pending = pendingInboundHandshake;
        }

        if (pending is null)
        {
            return;
        }

        var deadlineUtc = GetPendingInboundHandshakeDeadlineUtc(pending);
        if (DateTimeOffset.UtcNow < deadlineUtc)
        {
            SchedulePendingInboundHandshakeTimeout(pending);
            return;
        }

        if (pending.ChallengeNonce is not null && pending.SessionId is SessionId challengeSessionId)
        {
            FailInboundHandshake(
                pending,
                "handshake_response_expired",
                source: null,
                challengeSessionId,
                SessionHandshakeState.Expired);
            return;
        }

        FailInboundHandshake(
            pending,
            "handshake_start_timeout",
            source: null,
            sessionId: pending.SessionId,
            failureState: SessionHandshakeState.Expired);
    }

    private static DateTimeOffset GetPendingInboundHandshakeDeadlineUtc(PendingInboundHandshakeState state)
    {
        return state.ChallengeExpiresAtUtc ?? state.CreatedAtUtc.Add(PendingJoinTimeout);
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

        if (!ShouldRetainCapabilitySecureState(nextState))
        {
            ResetControlSecureState();
        }

        currentSessionSecurityState = nextState;
        SyncInboundScreenSharePolicyToBridge(nextState);
        SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
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

    private void SyncInboundScreenSharePolicyToBridge(SessionSecurityState nextState)
    {
        if (client is not RealNknClientAdapter realClient)
        {
            return;
        }

        var shouldEnable = false;
        string? allowedSessionId = null;
        string? allowedSourceAddress = null;
        DateTimeOffset? policyExpiresAtUtc = null;
        var nowUtc = DateTimeOffset.UtcNow;

        if (nextState.InviteValidated &&
            nextState.HandshakeCompleted &&
            nextState.HandshakeState == SessionHandshakeState.Verified &&
            nextState.ApprovalGranted &&
            nextState.ApprovalExpiresAt is DateTimeOffset expiresAtUtc &&
            nextState.SessionId is SessionId sessionId &&
            nextState.HelpeeAddress is PeerAddress helpeeAddress &&
            nextState.HelperAddress is PeerAddress helperAddress &&
            AddressMatchesForSessionPolicy(helperAddress.Value, LocalPeerAddress) &&
            nextState.HasCapability(CapabilityGrant.ScreenShare, nowUtc))
        {
            shouldEnable = true;
            allowedSessionId = sessionId.Value;
            allowedSourceAddress = helpeeAddress.Value;
            policyExpiresAtUtc = expiresAtUtc;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await realClient.UpdateInboundScreenSharePolicyAsync(
                            shouldEnable,
                            allowedSessionId,
                            allowedSourceAddress,
                            policyExpiresAtUtc,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                    // Bridge may not be running yet; the adapter still applies the local fail-closed policy.
                }
                catch (Exception ex)
                {
                    Log($"Bridge screenshare policy sync failed ({ex.GetType().Name})");
                }
            },
            CancellationToken.None);
    }

    private void AbortOutboundHandshake(string reason, SessionHandshakeState failureState = SessionHandshakeState.Failed)
    {
        LocalOperationalLog.Warn(
            "SessionHandshake",
            $"event=failure; direction=outbound; reason={reason}; session_id={currentSessionSecurityState.SessionId?.Value ?? "(none)"}; helper_identity={currentSessionSecurityState.HelperAddress?.Value ?? "(none)"}");
        pendingOutboundHandshake = null;
        UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(failureState, reason));
        ClearActivePeerSessionTracking(preserveHelpeeHostKeyPair: false);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendHandshakeStartAsync(string destination, PendingOutboundHandshakeState outboundHandshake, CancellationToken ct)
    {
        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            throw new InvalidOperationException("No active session context.");
        }

        var start = new SessionHandshakeStart(
            outboundHandshake.SessionId,
            outboundHandshake.HelperAddress,
            outboundHandshake.InviteToken);
        var payload = SessionHandshakeProtocol.Serialize(start);
        var envelope = CreateEnvelope(envelopeCode, MsgType.SessionHandshakeStart, payload, helperJoinRequestMessageId);
        await SendEnvelopeWithAckRetryAsync(destination, envelope, ct).ConfigureAwait(false);
    }

    private void FailInboundHandshake(
        PendingInboundHandshakeState pending,
        string reason,
        string? source,
        SessionId? sessionId = null,
        SessionHandshakeState failureState = SessionHandshakeState.Failed)
    {
        var effectiveSessionId = sessionId ?? pending.SessionId ?? SessionHandshakeProtocol.CreateSessionId();
        var result = new SessionHandshakeResult(effectiveSessionId, Verified: false, FailureReason: reason);
        var envelope = CreateEnvelope(
            pending.EnvelopeCode,
            MsgType.SessionHandshakeResult,
            SessionHandshakeProtocol.Serialize(result),
            pending.JoinRequestMessageId);
        _ = SendEnvelopeAsync(pending.RemoteEndpoint, envelope, CancellationToken.None);

        if (pending.HelpeeAddress is PeerAddress helpeeAddress)
        {
            LocalOperationalLog.Warn(
                "SessionHandshake",
                $"event=failure; direction=inbound; reason={reason}; session_id={effectiveSessionId.Value}; helper_identity={pending.HelperAddress.Value}; peer_id={source ?? "(none)"}");
            UpdateSessionSecurityState(
                SessionSecurityState.CreateHelpeeWaiting(helpeeAddress)
                    .WithHandshakeChallenge(
                        effectiveSessionId,
                        helpeeAddress,
                        pending.HelperAddress,
                        pending.InviteValidated,
                        DateTimeOffset.UtcNow)
                    .WithHandshakeFailure(failureState, reason));
        }
        else
        {
            LocalOperationalLog.Warn(
                "SessionHandshake",
                $"event=failure; direction=inbound; reason={reason}; session_id={effectiveSessionId.Value}; helper_identity={pending.HelperAddress.Value}; peer_id={source ?? "(none)"}");
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(failureState, reason));
        }

        ClearPendingInboundHandshake(pending.JoinRequestMessageId);
        ClearActivePeerSessionTracking(preserveHelpeeHostKeyPair: true);
    }

    private static bool TryParseJoinRequestPayload(byte[] payload, out JoinRequestPayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<JoinRequestPayload>(payload);
            if (dto is null ||
                string.IsNullOrWhiteSpace(dto.helperEndpoint) ||
                string.IsNullOrWhiteSpace(dto.helperEcdhPublicKey))
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseApprovePayload(byte[] payload, out ApprovePayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ApprovePayload>(payload);
            if (dto is null ||
                string.IsNullOrWhiteSpace(dto.sessionId) ||
                string.IsNullOrWhiteSpace(dto.helpeeEcdhPublicKey) ||
                string.IsNullOrWhiteSpace(dto.secureEnvelopeBase64))
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SessionEcdhKeyPair CreateSessionEcdhKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdh.ExportSubjectPublicKeyInfo();
        return new SessionEcdhKeyPair(ecdh, publicKey);
    }

    private static byte[] DeriveSessionKey(SessionEcdhKeyPair localKeyPair, byte[] remotePublicKey, string codeDigits)
    {
        if (localKeyPair is null)
        {
            throw new ArgumentNullException(nameof(localKeyPair));
        }

        if (remotePublicKey is null || remotePublicKey.Length == 0)
        {
            throw new ArgumentException("remote_public_key_missing", nameof(remotePublicKey));
        }

        if (string.IsNullOrWhiteSpace(codeDigits))
        {
            throw new ArgumentException("session_code_missing", nameof(codeDigits));
        }

        using var remoteEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteEcdh.ImportSubjectPublicKeyInfo(remotePublicKey, out _);
        var ikm = localKeyPair.Ecdh.DeriveKeyMaterial(remoteEcdh.PublicKey);

        var saltInput = Encoding.UTF8.GetBytes("nlink-session-salt:" + codeDigits);
        var salt = SHA256.HashData(saltInput);
        var info = Encoding.UTF8.GetBytes("nlink-v1");
        return SessionKeyDerivation.HkdfSha256(ikm, salt, info, 32);
    }

    private static Envelope CreateEnvelope(string code, MsgType type, byte[] payload, string? replyTo)
    {
        return new Envelope(
            Version: EnvelopeVersion,
            Code: code,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: type,
            Payload: payload ?? Array.Empty<byte>(),
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: replyTo);
    }

    private bool TryGetCurrentEnvelopeCode(out string envelopeCode)
    {
        if (!string.IsNullOrWhiteSpace(currentEnvelopeCode))
        {
            envelopeCode = currentEnvelopeCode;
            return true;
        }

        envelopeCode = string.Empty;
        return false;
    }

    private string ResolveInboundEnvelopeCode(string? envelopeCode)
    {
        if (!string.IsNullOrWhiteSpace(envelopeCode))
        {
            return envelopeCode.Trim();
        }

        if (TryGetCurrentEnvelopeCode(out var current))
        {
            return current;
        }

        return "000000";
    }

    private static string CreateAddressSessionContextCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return $"addr.{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private bool IsSelfSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return AddressesLikelySamePeer(source, client.Address);
    }

    private static bool AddressesLikelySamePeer(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        // Multi-client sources may include sub-client prefixes. The final dot segment is the stable pubkey.
        var leftTail = GetAddressTail(left);
        var rightTail = GetAddressTail(right);
        return LooksLikeNknPubKeyTail(leftTail) &&
               LooksLikeNknPubKeyTail(rightTail) &&
               !string.IsNullOrWhiteSpace(rightTail) &&
               string.Equals(leftTail, rightTail, StringComparison.Ordinal);
    }

    internal static bool AddressMatchesForSessionPolicy(string? left, string? right)
    {
        return AddressesLikelySamePeer(left, right);
    }

    private static string GetAddressTail(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var span = address.AsSpan().Trim();
        var lastDot = span.LastIndexOf('.');
        if (lastDot < 0 || lastDot == span.Length - 1)
        {
            return span.ToString();
        }

        return span[(lastDot + 1)..].ToString();
    }

    private static bool LooksLikeNknPubKeyTail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void SetAdapterReliabilityModeHint(string mode)
    {
        if (client is RealNknClientAdapter realClient)
        {
            realClient.SetReliabilityModeHint(mode);
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[nLink][NKN] {message}");
    }

    private enum TransportRemoteControlEventKind
    {
        ControlRequestReceived = 0,
        ControlResponseReceived = 1,
        ControlStartReceived = 2,
        ControlStopReceived = 3,
        DisplayInfoChanged = 4,
    }

    private readonly record struct TransportRemoteControlEvent(
        TransportRemoteControlEventKind Kind,
        string? RequestId,
        string? PeerId,
        string? Decision);

    private static class TransportRemoteControlCoordinator
    {
        public static RemoteControlSessionState Apply(
            RemoteControlSessionState current,
            in TransportRemoteControlEvent evt)
        {
            var requestId = string.IsNullOrWhiteSpace(evt.RequestId) ? null : evt.RequestId.Trim();
            var peerId = string.IsNullOrWhiteSpace(evt.PeerId) ? null : evt.PeerId.Trim();

            return evt.Kind switch
            {
                TransportRemoteControlEventKind.ControlRequestReceived => current with
                {
                    ControlState = ControlState.Requesting,
                    CurrentControlRequestId = requestId,
                    ControllerPeerId = peerId,
                    ConsentToken = null,
                },
                TransportRemoteControlEventKind.ControlResponseReceived when IsAllow(evt.Decision) => current with
                {
                    ControlState = ControlState.Active,
                    CurrentControlRequestId = requestId ?? current.CurrentControlRequestId,
                    ControllerPeerId = current.ControllerPeerId ?? peerId,
                    ConsentToken = null,
                },
                TransportRemoteControlEventKind.ControlResponseReceived when IsDeny(evt.Decision) => current with
                {
                    ControlState = ControlState.Denied,
                    CurrentControlRequestId = requestId ?? current.CurrentControlRequestId,
                    ControllerPeerId = null,
                    ConsentToken = null,
                },
                TransportRemoteControlEventKind.ControlStartReceived => current with
                {
                    ControlState = ControlState.Active,
                    CurrentControlRequestId = requestId ?? current.CurrentControlRequestId,
                    ControllerPeerId = current.ControllerPeerId ?? peerId,
                    ConsentToken = null,
                },
                TransportRemoteControlEventKind.ControlStopReceived => current with
                {
                    ControlState = ControlState.Off,
                    CurrentControlRequestId = null,
                    ControllerPeerId = null,
                    ConsentToken = null,
                },
                TransportRemoteControlEventKind.DisplayInfoChanged when current.ControlState == ControlState.Active => current with
                {
                    ControlState = ControlState.Off,
                    CurrentControlRequestId = null,
                    ControllerPeerId = null,
                    ConsentToken = null,
                },
                _ => current,
            };
        }

        private static bool IsAllow(string? decision)
        {
            return string.Equals(decision?.Trim(), "allow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeny(string? decision)
        {
            var normalized = decision?.Trim();
            return string.Equals(normalized, "deny", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "denied", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryParseApproveSecurePayload(byte[] payload, out ApproveSecurePayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ApproveSecurePayload>(payload);
            if (dto is null || string.IsNullOrWhiteSpace(dto.approvalDecisionBase64))
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRejectPayload(byte[] payload, out RejectPayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            parsed = JsonSerializer.Deserialize<RejectPayload>(payload);
            return parsed is not null &&
                   !string.IsNullOrWhiteSpace(parsed.sessionId) &&
                   !string.IsNullOrWhiteSpace(parsed.helpeeEcdhPublicKey) &&
                   !string.IsNullOrWhiteSpace(parsed.secureEnvelopeBase64);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseRejectSecurePayload(byte[] payload, out RejectSecurePayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<RejectSecurePayload>(payload);
            if (dto is null)
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseSessionEndPayload(byte[] payload, out SessionEndPayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            parsed = JsonSerializer.Deserialize<SessionEndPayload>(payload);
            return parsed is not null && !string.IsNullOrWhiteSpace(parsed.sessionId);
        }
        catch
        {
            return false;
        }
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

    private readonly record struct QueuedControlEnvelope(
        string Destination,
        Envelope Envelope,
        TaskCompletionSource<bool> Completion,
        CancellationToken CancellationToken,
        bool IsLowPriorityMouseMove);

    private sealed class JoinRequestPayload
    {
        public string? helperEndpoint { get; set; }
        public string? helperIdentifier { get; set; }
        public string? helperEcdhPublicKey { get; set; }
        public bool? remoteControlSupported { get; set; }
    }

    private sealed class ApprovePayload
    {
        public string? sessionId { get; set; }
        public string? helpeeEcdhPublicKey { get; set; }
        public string? secureEnvelopeBase64 { get; set; }
    }

    private sealed class ApproveSecurePayload
    {
        public bool? remoteControlSupported { get; set; }
        public string? approvalDecisionBase64 { get; set; }
    }

    private sealed class SessionEndPayload
    {
        public string? sessionId { get; set; }
        public string? reason { get; set; }
    }

    private sealed class RejectPayload
    {
        public string? sessionId { get; set; }
        public string? helpeeEcdhPublicKey { get; set; }
        public string? secureEnvelopeBase64 { get; set; }
    }

    private sealed class RejectSecurePayload
    {
        public string? reason { get; set; }
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
        public bool IsTerminal =>
            Phase is FileTransferTransportPhase.Completed or
                FileTransferTransportPhase.Declined or
                FileTransferTransportPhase.Canceled or
                FileTransferTransportPhase.Failed;
    }

    private sealed class PendingJoinRequestState
    {
        public PendingJoinRequestState(
            string joinRequestMessageId,
            string remoteEndpoint,
            PeerAddress helperAddress,
            byte[] helperEcdhPublicKey,
            string envelopeCode,
            SessionId sessionId,
            ApprovalRequest? approvalRequest)
        {
            JoinRequestMessageId = joinRequestMessageId;
            RemoteEndpoint = remoteEndpoint;
            HelperAddress = helperAddress;
            HelperEcdhPublicKey = helperEcdhPublicKey;
            EnvelopeCode = envelopeCode;
            SessionId = sessionId;
            ApprovalRequest = approvalRequest;
        }

        public string JoinRequestMessageId { get; }
        public string RemoteEndpoint { get; }
        public PeerAddress HelperAddress { get; }
        public byte[] HelperEcdhPublicKey { get; }
        public string EnvelopeCode { get; }
        public SessionId SessionId { get; }
        public ApprovalRequest? ApprovalRequest { get; }
        public bool DecisionSent { get; set; }
    }

    private sealed record PendingInboundHandshakeState
    {
        public PendingInboundHandshakeState(string joinRequestMessageId, string remoteEndpoint, PeerAddress helperAddress, byte[] helperEcdhPublicKey, string envelopeCode)
        {
            JoinRequestMessageId = joinRequestMessageId;
            RemoteEndpoint = remoteEndpoint;
            HelperAddress = helperAddress;
            HelperEcdhPublicKey = helperEcdhPublicKey;
            EnvelopeCode = envelopeCode;
            CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        public string JoinRequestMessageId { get; }
        public string RemoteEndpoint { get; }
        public PeerAddress HelperAddress { get; }
        public byte[] HelperEcdhPublicKey { get; }
        public string EnvelopeCode { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public SessionId? SessionId { get; private init; }
        public bool InviteValidated { get; private init; }
        public CapabilityGrant RequestedCapabilities { get; private init; }
        public string? ChallengeNonce { get; private init; }
        public DateTimeOffset? ChallengeExpiresAtUtc { get; private init; }
        public PeerAddress? HelpeeAddress { get; private init; }

        public PendingInboundHandshakeState WithChallenge(
            SessionId sessionId,
            bool inviteValidated,
            CapabilityGrant requestedCapabilities,
            string challengeNonce,
            DateTimeOffset challengeExpiresAtUtc,
            PeerAddress helpeeAddress)
        {
            return this with
            {
                SessionId = sessionId,
                InviteValidated = inviteValidated,
                RequestedCapabilities = requestedCapabilities,
                ChallengeNonce = challengeNonce,
                ChallengeExpiresAtUtc = challengeExpiresAtUtc,
                HelpeeAddress = helpeeAddress,
            };
        }
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
        public byte[]? HelpeeEcdhPublicKey { get; init; }

        public PendingOutboundHandshakeState WithChallenge(
            string challengeNonce,
            DateTimeOffset challengeExpiresAtUtc,
            byte[] helpeeEcdhPublicKey)
        {
            return this with
            {
                ChallengeNonce = challengeNonce,
                ChallengeExpiresAtUtc = challengeExpiresAtUtc,
                HelpeeEcdhPublicKey = helpeeEcdhPublicKey,
            };
        }
    }

    private sealed class PendingAckWait
    {
        public PendingAckWait(string destination, TaskCompletionSource<bool> completion)
        {
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        public string Destination { get; }
        public TaskCompletionSource<bool> Completion { get; }
    }

    private sealed class SessionEcdhKeyPair : IDisposable
    {
        public SessionEcdhKeyPair(ECDiffieHellman ecdh, byte[] publicKey)
        {
            Ecdh = ecdh ?? throw new ArgumentNullException(nameof(ecdh));
            PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        }

        public ECDiffieHellman Ecdh { get; }
        public byte[] PublicKey { get; }

        public void Dispose()
        {
            Ecdh.Dispose();
        }
    }

    private readonly record struct InboundFileTransferDispatchWork(
        long Generation,
        long Order,
        string Source,
        Envelope Envelope);
}
#pragma warning restore CS0067
