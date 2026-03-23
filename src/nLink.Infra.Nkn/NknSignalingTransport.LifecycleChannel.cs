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

public sealed partial class NknSignalingTransport
{
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
                helperMediaEndpoint = client.MediaAddress,
                helperBulkEndpoint = client.BulkAddress,
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

        var envelope = CreateEnvelope(envelopeCode, MsgType.Chat, CreateSecureChatPayload(payload.ToArray()), replyTo: null);
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

    internal void RouteLifecycleEnvelope(string source, Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.JoinRequest:
                HandleJoinRequest(source, env);
                break;
            case MsgType.Approve:
                HandleApprove(source, env);
                break;
            case MsgType.Reject:
                HandleReject(source, env);
                break;
            case MsgType.Chat:
                HandleChat(source, env);
                break;
            case MsgType.Ack:
                HandleAck(source, env);
                break;
            case MsgType.SessionEnd:
                HandleSessionEnd(source, env);
                break;
            case MsgType.SessionHandshakeStart:
                HandleSessionHandshakeStart(source, env);
                break;
            case MsgType.SessionHandshakeChallenge:
                HandleSessionHandshakeChallenge(source, env);
                break;
            case MsgType.SessionHandshakeResponse:
                HandleSessionHandshakeResponse(source, env);
                break;
            case MsgType.SessionHandshakeResult:
                HandleSessionHandshakeResult(source, env);
                break;
            default:
                throw new InvalidOperationException($"Lifecycle channel cannot route {env.Type}.");
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
        remoteMediaEndpoint = string.IsNullOrWhiteSpace(join.helperMediaEndpoint)
            ? join.helperEndpoint
            : join.helperMediaEndpoint;
        remoteBulkEndpoint = string.IsNullOrWhiteSpace(join.helperBulkEndpoint)
            ? join.helperEndpoint
            : join.helperBulkEndpoint;
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
            remoteMediaEndpoint = string.IsNullOrWhiteSpace(secureApprove.helpeeMediaEndpoint)
                ? remoteEndpoint
                : secureApprove.helpeeMediaEndpoint;
            remoteBulkEndpoint = string.IsNullOrWhiteSpace(secureApprove.helpeeBulkEndpoint)
                ? remoteEndpoint
                : secureApprove.helpeeBulkEndpoint;
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

        if (!TryDecryptChatPayload(source, env, out var securePayload))
        {
            return;
        }

        ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(securePayload.Plaintext));
        Log($"Chat dispatched (msg_id={env.MessageId}, payload_len={securePayload.Plaintext.Length})");
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
            !TryValidateScreenShareMessageSession(
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
            !TryValidateScreenShareMessageSession(
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
                    helpeeMediaEndpoint = client.MediaAddress,
                    helpeeBulkEndpoint = client.BulkAddress,
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
        await SendEnvelopeAsync(destination, envelope, bytes, ct).ConfigureAwait(false);
    }

    private async Task SendEnvelopeAsync(string destination, Envelope envelope, byte[] bytes, CancellationToken ct)
    {

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

    private async Task SendBulkEnvelopeAsync(string destination, Envelope envelope, CancellationToken ct)
    {
        var bytes = EnvelopeCodec.Serialize(envelope);
        await SendBulkEnvelopeAsync(destination, envelope, bytes, ct).ConfigureAwait(false);
    }

    private async Task SendBulkEnvelopeAsync(string destination, Envelope envelope, byte[] bytes, CancellationToken ct)
    {

        try
        {
            NknRuntimeDiagnostics.IncrementMessagesSent();
            await client.SendBulkAsync(destination, bytes, ct).ConfigureAwait(false);
            Log($"Bulk envelope sent (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"Bulk envelope send failed (type={envelope.Type}, msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
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
        remoteMediaEndpoint = null;
        remoteBulkEndpoint = null;
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
        remoteMediaEndpoint = null;
        remoteBulkEndpoint = null;
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
            allowedSourceAddress = string.IsNullOrWhiteSpace(remoteMediaEndpoint)
                ? helpeeAddress.Value
                : remoteMediaEndpoint;
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

        return AddressesLikelySamePeer(source, client.Address) ||
               AddressesLikelySamePeer(source, client.MediaAddress) ||
               AddressesLikelySamePeer(source, client.BulkAddress);
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
        public string? helperMediaEndpoint { get; set; }
        public string? helperBulkEndpoint { get; set; }
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
        public string? helpeeMediaEndpoint { get; set; }
        public string? helpeeBulkEndpoint { get; set; }
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
}
