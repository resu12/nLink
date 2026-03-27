using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

#pragma warning disable CS0067
public sealed partial class NknSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, IHelpRequestSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, IFileTransferSignalingTransport, IFileTransferChunkBudgetProvider, IFileTransferProtocolCapabilities, IFileTransferTransportProfileProvider, IAuthoritativeConnectedAddressSource
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
    private readonly SessionReplayWindow inboundChatReplayWindow = new();
    private readonly SessionReplayWindow inboundControlReplayWindow = new();
    private readonly SessionReplayWindow inboundLifecycleReplayWindow = new();
    private readonly SessionReplayWindow inboundScreenShareReplayWindow = new();
    private readonly SessionReplayWindow inboundFileTransferReplayWindow = new(
        windowSize: FileTransferInboundReplayWindowSize,
        maxForwardAdvance: FileTransferInboundReplayMaxForwardAdvance);
    private readonly Dictionary<string, FileTransferTransportState> fileTransferStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportFileTransferDataSession> fileTransferDataSessions = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, InboundFileTransferDispatchWork> pendingInboundFileTransferControlDispatch = new();
    private readonly NknLifecycleChannel lifecycleChannel;
    private readonly NknSecureControlChannel controlChannel;
    private readonly NknScreenShareChannel screenShareChannel;
    private readonly NknFileTransferChannel fileTransferChannel;
    private readonly NknEnvelopeRouter envelopeRouter;
    private readonly ControlOutboundQueue controlOutboundQueue;
    private const bool LocalRemoteControlSupported = true;
    private const int FileTransferInboundReplayWindowSize = 32768;
    private const long FileTransferInboundReplayMaxForwardAdvance = 131072;

    public bool SupportsFileTransferV3Streaming => true;

    public FileTransferTransportProfileKind FileTransferTransportProfileKind => FileTransferTransportProfileKind.ConservativeNknStartup;

    private string? currentEnvelopeCode;
    private string? remoteEndpoint;
    private string? remoteMediaEndpoint;
    private string? remoteBulkEndpoint;
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
    private SessionId? activeApprovedSessionId;
    private PeerAddress? activeApprovedHelperAddress;
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
    private long nextOutboundChatSecureSequence;
    private long nextOutboundLifecycleSecureSequence;
    private long nextOutboundScreenShareSecureSequence;
    private long nextOutboundFileTransferSecureSequence;
    private long nextInboundFileTransferControlDispatchOrder;
    private long nextInboundFileTransferControlDispatchToProcess = 1;
    private long inboundFileTransferDispatchGeneration;
    private bool inboundFileTransferControlDispatchActive;
    private Task inboundFileTransferLifecycleDispatchTail = Task.CompletedTask;
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
        lifecycleChannel = new NknLifecycleChannel(this);
        controlChannel = new NknSecureControlChannel(this);
        screenShareChannel = new NknScreenShareChannel(this);
        fileTransferChannel = new NknFileTransferChannel(this);
        envelopeRouter = new NknEnvelopeRouter(lifecycleChannel, controlChannel, screenShareChannel, fileTransferChannel);
        controlOutboundQueue = new ControlOutboundQueue(this);
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
        lifecycleChannel = new NknLifecycleChannel(this);
        controlChannel = new NknSecureControlChannel(this);
        screenShareChannel = new NknScreenShareChannel(this);
        fileTransferChannel = new NknFileTransferChannel(this);
        envelopeRouter = new NknEnvelopeRouter(lifecycleChannel, controlChannel, screenShareChannel, fileTransferChannel);
        controlOutboundQueue = new ControlOutboundQueue(this);
        secureScreenShareFrameReassembler.FrameReady += OnSecureScreenShareFrameReady;
        SubscribeClientEvents();
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
    public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
    public event EventHandler<FileTransferStartReceivedEventArgs>? FileTransferStartReceived;
    public event EventHandler<FileTransferChunkReceivedEventArgs>? FileTransferChunkReceived;
    public event EventHandler<FileTransferWindowUpdateReceivedEventArgs>? FileTransferWindowUpdateReceived;
    public event EventHandler<FileTransferMissingRangeReceivedEventArgs>? FileTransferMissingRangeReceived;
    public event EventHandler<FileTransferPressureStateReceivedEventArgs>? FileTransferPressureStateReceived;
    public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;

    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;
    public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    public event EventHandler? ScreenShareStopped;

    public string LocalPeerAddress => string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address;
    bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress =>
        client is IAuthoritativeConnectedAddressSource authoritativeConnectedAddressSource &&
        authoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress;
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

        var isStopPayload = string.Equals(messageType, "stop", StringComparison.Ordinal);
        var destination = isStopPayload ? remoteEndpoint : remoteMediaEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_no_remote_endpoint");
            Log($"SendScreenSharePayloadAsync failed (payload_len={payload.Length}, reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

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
            await client.SendMediaAsync(destination, transportPayload, ct).ConfigureAwait(false);
            Interlocked.Increment(ref screenShareMessagesSent);
            Interlocked.Add(ref screenSharePayloadBytesSent, transportPayload.Length);
            NknRuntimeDiagnostics.IncrementScreenShareMessagesSent();
            NknRuntimeDiagnostics.AddScreenSharePayloadBytesSent(transportPayload.Length);
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


    private Task QueueControlEnvelopeAsync(
        string destination,
        Envelope envelope,
        ControlOutboundLane lane,
        CancellationToken ct,
        bool isLowPriorityMouseMove = false)
        => controlOutboundQueue.QueueEnvelopeAsync(destination, envelope, lane, ct, isLowPriorityMouseMove);

    private void FlushLowPriorityControlOutboundQueue(string reason)
        => controlOutboundQueue.FlushLowPriority(reason);

    private void FlushAllControlOutboundQueues(string reason)
        => controlOutboundQueue.FlushAll(reason);

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
        if (e.Kind == BridgeLifecycleEventKind.Ready)
        {
            SetFileTransferDataSessionsAvailability(
                isAvailable: true,
                reason: "transport_recovered",
                requiresResumeRequest: true);
        }

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

        SetFileTransferDataSessionsAvailability(
            isAvailable: false,
            reason: "transport_disconnected",
            requiresResumeRequest: true);
        NknRuntimeDiagnostics.SetLastError("nkn_client_disconnected");
        UpdateSessionSecurityState(currentSessionSecurityState.Invalidate("transport_disconnected"));
        Log("Client disconnected");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private string? ResolveExpectedRemotePeerAddressForCurrentSession()
    {
        var localAddress = LocalPeerAddress;

        if (!string.IsNullOrWhiteSpace(remoteEndpoint) &&
            !AddressesLikelySamePeer(remoteEndpoint, localAddress))
        {
            return remoteEndpoint;
        }

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

        return null;
    }

    private string? ResolveExpectedRemoteMediaPeerAddressForCurrentSession()
    {
        return string.IsNullOrWhiteSpace(remoteMediaEndpoint)
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : remoteMediaEndpoint;
    }

    private string? ResolveExpectedRemoteBulkPeerAddressForCurrentSession()
    {
        return string.IsNullOrWhiteSpace(remoteBulkEndpoint)
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : remoteBulkEndpoint;
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


    private string ResolveControlSessionId(string? current)
    {
        return string.IsNullOrWhiteSpace(current)
            ? currentSessionSecurityState.SessionId?.Value ?? string.Empty
            : current.Trim();
    }

    private byte[] CreateSecureChatPayload(byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Chat,
            MessageType: MapSecureChatMessageType(),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundChatSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptChatPayload(
        string? source,
        Envelope env,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default!;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError("chat_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_session_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError("chat_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_expected_sender_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError("chat_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_session_key_unavailable");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Chat,
                    MessageType: MapSecureChatMessageType(),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError("chat_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("chat_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=chat_message_rejected; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundChatReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
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
            NknRuntimeDiagnostics.SetLastError($"chat_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"chat_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=chat_message_rejected; reason={replayReason}; session_id={securePayload.Metadata.SessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"Chat secure envelope rejected (msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }
}
#pragma warning restore CS0067
