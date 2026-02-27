using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core;

namespace NLink.Infra.Nkn;

#pragma warning disable CS0067
public sealed class NknSignalingTransport : ISignalingTransport
{
    private const int EnvelopeVersion = 1;
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan JoinPresenceWaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AckWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] AckRetryDelays =
    {
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromMilliseconds(2700),
    };

    private readonly NknTransportOptions options;
    private readonly NknIdentity identity;
    private readonly INknClient client;
    private readonly LruMessageIdCache seenMessageIds = new(500);
    private readonly ConcurrentDictionary<string, PendingAckWait> pendingAcks = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private SessionCode? currentCode;
    private string? currentTopic;
    private string? remoteEndpoint;
    private string? lastPeerAddress;
    private CancellationTokenSource? presenceLoopCts;
    private Task? presenceLoopTask;
    private TaskCompletionSource<PresenceAnnouncement>? pendingPresenceWait;
    private SessionEcdhKeyPair? helpeeHostEcdhKeyPair;
    private SessionEcdhKeyPair? helperJoinEcdhKeyPair;
    private string? helperJoinRequestMessageId;
    private PendingJoinRequestState? pendingJoinRequest;
    private bool disposed;

    public NknSignalingTransport()
    {
        options = NknTransportOptions.Load();
        identity = NknIdentityStore.LoadOrCreate(options);
        client = new RealNknClientAdapter(identity, options);
        SubscribeClientEvents();

        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);

        Log($"Initialized | address={identity.Address} | identifier={identity.Identifier} | key_path={options.KeyPath}");
    }

    internal NknSignalingTransport(INknClient client, NknTransportOptions options, NknIdentity identity)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        SubscribeClientEvents();
    }

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    public event EventHandler? Approved;

    public event EventHandler? Rejected;

    public event EventHandler? Disconnected;

    public event EventHandler? RemoteSessionEnded;

    internal event EventHandler<BridgeLifecycleEvent>? BridgeLifecycle;

    public bool CanSendSessionEnd => !disposed && currentCode is not null && !string.IsNullOrWhiteSpace(remoteEndpoint);

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

    internal async Task PrepareForReuseAsync()
    {
        ThrowIfDisposed();

        await StopPresenceLoopAsync();
        await BestEffortUnsubscribeCurrentTopicAsync();
        CancelPendingAcks();
        ResetSessionTracking();
        seenMessageIds.Clear();
        currentCode = null;
        lastPeerAddress = null;

        Log("Prepared for reuse");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // Avoid treating normal cleanup as a disconnection.
        client.MessageReceived -= OnClientMessageReceived;
        client.Disconnected -= OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle -= OnBridgeLifecycle;
        }

        CleanupAsync().GetAwaiter().GetResult();
        DisposeEphemeralKeyState();
        client.Dispose();
        Log("Disposed");
    }

    public async Task HostAsync(SessionCode code, CancellationToken ct)
    {
        ThrowIfDisposed();

        await StopPresenceLoopAsync();
        await BestEffortUnsubscribeCurrentTopicAsync();
        ResetSessionTracking();

        currentCode = code;
        currentTopic = BuildPresenceTopic(code);
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
            await client.SubscribeAsync(currentTopic, ct);
            StartPresenceLoop(code, currentTopic, ct);
            Log($"HostAsync ready (code={code.Digits}, topic={currentTopic})");
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"HostAsync failed ({ex.GetType().Name})");
            throw;
        }
    }

    public async Task JoinAsync(SessionCode code, CancellationToken ct)
    {
        ThrowIfDisposed();

        await StopPresenceLoopAsync();
        await BestEffortUnsubscribeCurrentTopicAsync();
        ResetSessionTracking();

        currentCode = code;
        currentTopic = BuildPresenceTopic(code);
        seenMessageIds.Clear();
        ReplaceHelperJoinKeyPair(CreateSessionEcdhKeyPair());

        TaskCompletionSource<PresenceAnnouncement> presenceWait;
        lock (gate)
        {
            pendingPresenceWait = new TaskCompletionSource<PresenceAnnouncement>(TaskCreationOptions.RunContinuationsAsynchronously);
            presenceWait = pendingPresenceWait;
        }

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
            await client.SubscribeAsync(currentTopic, ct);
            Log($"JoinAsync waiting for Presence (code={code.Digits}, topic={currentTopic})");

            PresenceAnnouncement announcement;
            try
            {
                announcement = await presenceWait.Task.WaitAsync(JoinPresenceWaitTimeout, ct);
            }
            catch (TimeoutException)
            {
                NknRuntimeDiagnostics.SetLastError("Could not find session for code");
                SessionTimeline.Record("DiscoveryTimeout");
                SessionReliabilityLog.RecordStandalone(
                    "Helper",
                    "NKN",
                    SessionReliabilityStage.DiscoveryTimeout,
                    errorCode: "timeout",
                    errorHint: "Could not find that code. Ask them to try a new code.");
                Log($"JoinAsync timeout waiting for Presence (code={code.Digits})");
                await BestEffortUnsubscribeCurrentTopicAsync();
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            remoteEndpoint = announcement.Endpoint;
            lastPeerAddress = announcement.Endpoint;
            SessionTimeline.Record("DiscoveryFound");
            Log($"JoinAsync presence found (endpoint_len={announcement.Endpoint.Length}, identifier_len={announcement.Identifier.Length})");

            await BestEffortUnsubscribeCurrentTopicAsync();

            var helperKeyPair = GetHelperJoinKeyPairOrThrow();

            var joinPayload = new JoinRequestPayload
            {
                helperEndpoint = string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address,
                helperIdentifier = identity.Identifier,
                helperEcdhPublicKey = Convert.ToBase64String(helperKeyPair.PublicKey),
            };

            var joinEnvelope = CreateEnvelope(
                code.Digits,
                MsgType.JoinRequest,
                JsonSerializer.SerializeToUtf8Bytes(joinPayload),
                replyTo: null);

            helperJoinRequestMessageId = joinEnvelope.MessageId;
            await SendEnvelopeWithAckRetryAsync(remoteEndpoint, joinEnvelope, ct);
            SessionTimeline.Record("JoinRequestSent");
            SessionReliabilityLog.RecordStandalone("Helper", "NKN", SessionReliabilityStage.DiscoveryFoundHost);
            SessionReliabilityLog.RecordStandalone("Helper", "NKN", SessionReliabilityStage.JoinRequestSent);
            Log($"JoinAsync sent JoinRequest with Ack (code={code.Digits}, msg_id={joinEnvelope.MessageId})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"JoinAsync failed ({ex.GetType().Name})");
            throw;
        }
        finally
        {
            lock (gate)
            {
                pendingPresenceWait = null;
            }
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

        if (currentCode is null)
        {
            NknRuntimeDiagnostics.SetLastError("chat_no_session_code");
            Log($"SendChatMessageAsync failed (payload_len={payload.Length}, reason=no_session_code)");
            throw new InvalidOperationException("No active session code.");
        }

        var destination = remoteEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError("chat_no_remote_endpoint");
            Log($"SendChatMessageAsync failed (payload_len={payload.Length}, reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var envelope = CreateEnvelope(currentCode.Value.Digits, MsgType.Chat, payload.ToArray(), replyTo: null);
        await SendEnvelopeWithAckRetryAsync(destination, envelope, ct);
        Log($"SendChatMessageAsync sent Chat with Ack (payload_len={payload.Length}, msg_id={envelope.MessageId})");
    }

    public async Task SendSessionEndAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (currentCode is null || string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new SessionEndPayload
        {
            reason = "user_exit",
        });

        var envelope = CreateEnvelope(currentCode.Value.Digits, MsgType.SessionEnd, payload, replyTo: null);
        SessionTimeline.Record("SessionEndSent");
        await SendEnvelopeAsync(remoteEndpoint, envelope, ct);
        Log($"SendSessionEndAsync sent SessionEnd (msg_id={envelope.MessageId})");
    }

    private void SubscribeClientEvents()
    {
        client.MessageReceived += OnClientMessageReceived;
        client.Disconnected += OnClientDisconnected;
        if (client is RealNknClientAdapter realClient)
        {
            realClient.BridgeLifecycle += OnBridgeLifecycle;
        }
    }

    private void OnBridgeLifecycle(object? sender, BridgeLifecycleEvent e)
    {
        BridgeLifecycle?.Invoke(this, e);
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        NknRuntimeDiagnostics.SetLastError("nkn_client_disconnected");
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

        if (!string.IsNullOrWhiteSpace(env.Code) &&
            currentCode is not null &&
            !string.Equals(env.Code, currentCode.Value.Digits, StringComparison.Ordinal))
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
            if ((env.Type == MsgType.JoinRequest || env.Type == MsgType.Chat) && !string.IsNullOrWhiteSpace(e.Source))
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
                case MsgType.Presence:
                    HandlePresence(env);
                    break;
                case MsgType.JoinRequest:
                    HandleJoinRequest(e.Source, env);
                    break;
                case MsgType.Approve:
                    HandleApprove(env);
                    break;
                case MsgType.Reject:
                    HandleReject(env);
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

    private void HandlePresence(Envelope env)
    {
        if (!TryParsePresencePayload(env.Payload, out var presence))
        {
            NknRuntimeDiagnostics.SetLastError("presence_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("presence_payload_invalid");
            Log($"Presence payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (presence.ExpiresAtMs <= nowUnixMs)
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("presence_expired");
            Log($"Presence expired ignored (msg_id={env.MessageId}, expires_at={presence.ExpiresAtMs})");
            return;
        }

        if (string.IsNullOrWhiteSpace(presence.Endpoint))
        {
            NknRuntimeDiagnostics.SetLastError("presence_missing_endpoint");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("presence_missing_endpoint");
            Log($"Presence missing endpoint (msg_id={env.MessageId})");
            return;
        }

        lock (gate)
        {
            pendingPresenceWait?.TrySetResult(presence);
        }

        Log($"Presence accepted (msg_id={env.MessageId}, endpoint_len={presence.Endpoint.Length}, identifier_len={presence.Identifier.Length})");
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

        remoteEndpoint = join.helperEndpoint;
        lastPeerAddress = string.IsNullOrWhiteSpace(source) ? join.helperEndpoint : source;

        if (!TryGetHelpeeHostKeyPair(out _))
        {
            NknRuntimeDiagnostics.SetLastError("host_ecdh_not_ready");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("host_ecdh_not_ready");
            Log($"JoinRequest ignored (msg_id={env.MessageId}, reason=no_host_key)");
            Disconnected?.Invoke(this, EventArgs.Empty);
            return;
        }

        ReplacePendingJoinRequest(new PendingJoinRequestState(
            joinRequestMessageId: env.MessageId,
            remoteEndpoint: join.helperEndpoint,
            helperEcdhPublicKey: helperPubKey));

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        IncomingJoinRequest?.Invoke(
            this,
            new IncomingJoinRequestEventArgs(
                approveAsync: ct => ApproveJoinRequestAsync(env.MessageId, ct),
                rejectAsync: ct => RejectJoinRequestAsync(env.MessageId, ct)));
        NknRuntimeDiagnostics.IncrementIncomingJoinRequestRaised();

        Log($"JoinRequest accepted (msg_id={env.MessageId}, helper_endpoint_len={join.helperEndpoint.Length}, helper_id_len={(join.helperIdentifier ?? string.Empty).Length})");
    }

    private void HandleApprove(Envelope env)
    {
        if (!TryParseApprovePayload(env.Payload, out var approve))
        {
            NknRuntimeDiagnostics.SetLastError("approve_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_payload_invalid");
            Log($"Approve payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            Disconnected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SessionEcdhKeyPair? helperKeyPair;
        string? expectedJoinRequestId;
        lock (gate)
        {
            helperKeyPair = helperJoinEcdhKeyPair;
            expectedJoinRequestId = helperJoinRequestMessageId;
            helperJoinEcdhKeyPair = null;
            helperJoinRequestMessageId = null;
        }

        if (helperKeyPair is null)
        {
            NknRuntimeDiagnostics.SetLastError("approve_missing_helper_ecdh");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_missing_helper_ecdh");
            Log($"Approve ignored (msg_id={env.MessageId}, reason=no_helper_key)");
            Disconnected?.Invoke(this, EventArgs.Empty);
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
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!string.IsNullOrWhiteSpace(env.ReplyTo) && !string.IsNullOrWhiteSpace(expectedJoinRequestId) &&
                !string.Equals(env.ReplyTo, expectedJoinRequestId, StringComparison.Ordinal))
            {
                NknRuntimeDiagnostics.SetLastError("approve_replyto_mismatch");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("approve_replyto_mismatch");
                Log($"Approve reply_to mismatch (msg_id={env.MessageId}, reply_to={env.ReplyTo})");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            var codeDigits = currentCode?.Digits;
            if (string.IsNullOrWhiteSpace(codeDigits))
            {
                NknRuntimeDiagnostics.SetLastError("approve_missing_session_code");
                Log($"Approve ignored (msg_id={env.MessageId}, reason=no_session_code)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = DeriveSessionKey(helperKeyPair, helpeePubKey, codeDigits);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_key_derivation_failed");
                Log($"Approve key derivation failed (msg_id={env.MessageId}, ex={ex.GetType().Name})");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
            Approved?.Invoke(this, EventArgs.Empty);
            Log($"Approve dispatched (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
        }
        finally
        {
            helperKeyPair.Dispose();
        }
    }

    private void HandleReject(Envelope env)
    {
        ClearHelperJoinKeyPair();
        Rejected?.Invoke(this, EventArgs.Empty);
        Log($"Reject dispatched (msg_id={env.MessageId})");
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
        SessionTimeline.Record("SessionEndReceived");
        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        Log($"SessionEnd dispatched (msg_id={env.MessageId})");
        RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
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

    private async Task ApproveJoinRequestAsync(string joinRequestMessageId, CancellationToken ct)
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

            if (!TryGetHelpeeHostKeyPair(out var helpeeKeyPair) || helpeeKeyPair is null)
            {
                NknRuntimeDiagnostics.SetLastError("host_ecdh_not_ready");
                Log($"Approve failed (join_msg_id={joinRequestMessageId}, reason=no_host_key)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            var approvePayload = new ApprovePayload
            {
                helpeeEcdhPublicKey = Convert.ToBase64String(helpeeKeyPair.PublicKey),
            };

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(approvePayload);
            var envelope = CreateEnvelope(currentCode?.Digits ?? "000000", MsgType.Approve, payloadBytes, joinRequestMessageId);
            await SendEnvelopeAsync(pendingState.RemoteEndpoint, envelope, ct);

            var codeDigits = currentCode?.Digits;
            if (string.IsNullOrWhiteSpace(codeDigits))
            {
                NknRuntimeDiagnostics.SetLastError("approve_missing_session_code");
                Log($"Approve failed (join_msg_id={joinRequestMessageId}, reason=no_session_code)");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            byte[] sharedKey;
            try
            {
                sharedKey = DeriveSessionKey(helpeeKeyPair, pendingState.HelperEcdhPublicKey, codeDigits);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                NknRuntimeDiagnostics.SetLastError("approve_key_derivation_failed");
                Log($"Approve key derivation failed (join_msg_id={joinRequestMessageId}, ex={ex.GetType().Name})");
                Disconnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
            Approved?.Invoke(this, EventArgs.Empty);
            Log($"Approve sent (msg_id={envelope.MessageId}, reply_to={envelope.ReplyTo})");
        }
        finally
        {
            ClearPendingJoinRequest(joinRequestMessageId);
        }
    }

    private async Task RejectJoinRequestAsync(string joinRequestMessageId, CancellationToken ct)
    {
        PendingJoinRequestState? pending;
        if (!TryBeginPendingJoinDecision(joinRequestMessageId, out pending))
        {
            Log($"Reject ignored (join_msg_id={joinRequestMessageId}, reason=already_handled_or_missing)");
            return;
        }

        try
        {
            var envelope = CreateEnvelope(currentCode?.Digits ?? "000000", MsgType.Reject, Array.Empty<byte>(), joinRequestMessageId);
            await SendEnvelopeAsync(pending!.RemoteEndpoint, envelope, ct);
            Rejected?.Invoke(this, EventArgs.Empty);
            Log($"Reject sent (msg_id={envelope.MessageId}, reply_to={envelope.ReplyTo})");
        }
        finally
        {
            ClearPendingJoinRequest(joinRequestMessageId);
        }
    }

    private void StartPresenceLoop(SessionCode code, string topic, CancellationToken externalCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var previous = Interlocked.Exchange(ref presenceLoopCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        presenceLoopTask = Task.Run(() => PresenceLoopAsync(code, topic, cts.Token), CancellationToken.None);
    }

    private async Task PresenceLoopAsync(SessionCode code, string topic, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !disposed)
            {
                var presencePayload = new PresencePayload
                {
                    endpoint = string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address,
                    expiresAtMs = DateTimeOffset.UtcNow.Add(PresenceTtl).ToUnixTimeMilliseconds(),
                    identifier = identity.Identifier,
                };

                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(presencePayload);
                var envelope = CreateEnvelope(code.Digits, MsgType.Presence, payloadBytes, replyTo: null);
                await PublishEnvelopeAsync(envelope, ct);
                Log($"Presence published (msg_id={envelope.MessageId}, payload_len={payloadBytes.Length}, topic={topic})");

                await Task.Delay(PresenceInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/cancel path.
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"Presence loop failed ({ex.GetType().Name})");
        }
    }

    private async Task PublishEnvelopeAsync(Envelope envelope, CancellationToken ct)
    {
        var bytes = EnvelopeCodec.Serialize(envelope);

        try
        {
            NknRuntimeDiagnostics.IncrementMessagesSent();
            await client.PublishAsync(BuildPresenceTopic(envelope.Code), bytes, ct);
            Log($"Envelope published (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"Envelope publish failed (type={envelope.Type}, msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
            throw;
        }
    }

    private async Task SendEnvelopeAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        var bytes = EnvelopeCodec.Serialize(envelope);

        try
        {
            NknRuntimeDiagnostics.IncrementMessagesSent();
            await client.SendAsync(destination, bytes, ct);
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
                        Disconnected?.Invoke(this, EventArgs.Empty);
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

        var ackCode = string.IsNullOrWhiteSpace(code) ? currentCode?.Digits ?? "000000" : code;
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
        await StopPresenceLoopAsync();
        await BestEffortUnsubscribeCurrentTopicAsync();
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

    private async Task StopPresenceLoopAsync()
    {
        var cts = Interlocked.Exchange(ref presenceLoopCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (presenceLoopTask is not null)
            {
                await presenceLoopTask;
            }
        }
        catch
        {
            // Presence loop errors already recorded.
        }
        finally
        {
            cts.Dispose();
            presenceLoopTask = null;
        }
    }

    private async Task BestEffortUnsubscribeCurrentTopicAsync()
    {
        var topic = currentTopic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        try
        {
            await client.UnsubscribeAsync(topic);
            Log($"Unsubscribed topic (topic={topic})");
        }
        catch (Exception ex)
        {
            Log($"UnsubscribeAsync failed ({ex.GetType().Name}, topic={topic})");
        }
        finally
        {
            currentTopic = null;
        }
    }

    private void ResetSessionTracking()
    {
        remoteEndpoint = null;
        lastPeerAddress = null;
        helperJoinRequestMessageId = null;

        lock (gate)
        {
            pendingPresenceWait = null;
        }

        DisposeEphemeralKeyState();
    }

    private void DisposeEphemeralKeyState()
    {
        SessionEcdhKeyPair? helperKeyToDispose = null;
        SessionEcdhKeyPair? helpeeHostKeyToDispose = null;

        lock (gate)
        {
            helpeeHostKeyToDispose = helpeeHostEcdhKeyPair;
            helpeeHostEcdhKeyPair = null;

            helperKeyToDispose = helperJoinEcdhKeyPair;
            helperJoinEcdhKeyPair = null;

            pendingJoinRequest = null;
        }

        helperKeyToDispose?.Dispose();
        helpeeHostKeyToDispose?.Dispose();
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
        PendingJoinRequestState? previous = null;
        lock (gate)
        {
            previous = pendingJoinRequest;
            pendingJoinRequest = state;
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

    private static bool TryParsePresencePayload(byte[] payload, out PresenceAnnouncement presence)
    {
        presence = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PresencePayload>(payload);
            if (dto is null || string.IsNullOrWhiteSpace(dto.endpoint))
            {
                return false;
            }

            presence = new PresenceAnnouncement(
                dto.endpoint.Trim(),
                dto.expiresAtMs,
                string.IsNullOrWhiteSpace(dto.identifier) ? string.Empty : dto.identifier.Trim());
            return true;
        }
        catch
        {
            return false;
        }
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
            if (dto is null || string.IsNullOrWhiteSpace(dto.helpeeEcdhPublicKey))
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
        return HkdfSha256(ikm, salt, info, 32);
    }

    private static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int okmLen)
    {
        if (okmLen <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(okmLen));
        }

        byte[] prk;
        using (var extract = new HMACSHA256(salt))
        {
            prk = extract.ComputeHash(ikm);
        }

        try
        {
            var okm = new byte[okmLen];
            var previous = Array.Empty<byte>();
            var offset = 0;
            byte counter = 1;

            while (offset < okmLen)
            {
                using var expand = new HMACSHA256(prk);
                var blockInput = new byte[previous.Length + info.Length + 1];
                Buffer.BlockCopy(previous, 0, blockInput, 0, previous.Length);
                Buffer.BlockCopy(info, 0, blockInput, previous.Length, info.Length);
                blockInput[^1] = counter;

                previous = expand.ComputeHash(blockInput);
                var bytesToCopy = Math.Min(previous.Length, okmLen - offset);
                Buffer.BlockCopy(previous, 0, okm, offset, bytesToCopy);
                offset += bytesToCopy;
                counter++;
            }

            return okm;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
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

    private static string BuildPresenceTopic(SessionCode code) => "nlink.help." + code.Digits;

    private static string BuildPresenceTopic(string code) => "nlink.help." + code;

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

    private sealed class PresencePayload
    {
        public string? endpoint { get; set; }
        public long expiresAtMs { get; set; }
        public string? identifier { get; set; }
    }

    private sealed class JoinRequestPayload
    {
        public string? helperEndpoint { get; set; }
        public string? helperIdentifier { get; set; }
        public string? helperEcdhPublicKey { get; set; }
    }

    private sealed class ApprovePayload
    {
        public string? helpeeEcdhPublicKey { get; set; }
    }

    private sealed class SessionEndPayload
    {
        public string? reason { get; set; }
    }

    private sealed class PendingJoinRequestState
    {
        public PendingJoinRequestState(string joinRequestMessageId, string remoteEndpoint, byte[] helperEcdhPublicKey)
        {
            JoinRequestMessageId = joinRequestMessageId;
            RemoteEndpoint = remoteEndpoint;
            HelperEcdhPublicKey = helperEcdhPublicKey;
        }

        public string JoinRequestMessageId { get; }
        public string RemoteEndpoint { get; }
        public byte[] HelperEcdhPublicKey { get; }
        public bool DecisionSent { get; set; }
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

    private readonly record struct PresenceAnnouncement(string Endpoint, long ExpiresAtMs, string Identifier);
}
#pragma warning restore CS0067
