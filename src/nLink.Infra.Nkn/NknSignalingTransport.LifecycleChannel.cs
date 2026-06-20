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

    public async Task SendHelpRequestAsync(HelpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await client.ConnectAsync(ct).ConfigureAwait(false);
        var sourceAddress = string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new HelpRequestPayload
        {
            requestId = request.RequestId,
            helpeeAddress = request.HelpeeAddress.Value,
            helperAddress = request.HelperAddress.Value,
            inviteToken = request.InviteToken,
        });
        var envelope = CreateEnvelope(CreateAddressSessionContextCode(), MsgType.HelpRequest, payload, replyTo: null);
        await SendEnvelopeWithAckRetryAsync(request.HelperAddress.Value, envelope, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_sent; request_id={request.RequestId}; helper_address={request.HelperAddress.Value}; helpee_address={request.HelpeeAddress.Value}");
        Log($"SendHelpRequestAsync sent HelpRequest with Ack (msg_id={envelope.MessageId}, source={sourceAddress})");
    }

    public async Task SendHelpRequestDecisionAsync(HelpRequestDecisionMessage decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ThrowIfDisposed();

        await client.ConnectAsync(ct).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new HelpRequestDecisionPayload
        {
            requestId = decision.RequestId,
            helpeeAddress = decision.HelpeeAddress.Value,
            helperAddress = decision.HelperAddress.Value,
            accepted = decision.Accepted,
            reason = decision.Reason,
        });
        var envelope = CreateEnvelope(CreateAddressSessionContextCode(), MsgType.HelpRequestDecision, payload, replyTo: null);
        await SendEnvelopeWithAckRetryAsync(decision.HelpeeAddress.Value, envelope, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_decision_sent; request_id={decision.RequestId}; accepted={decision.Accepted}; helper_address={decision.HelperAddress.Value}; helpee_address={decision.HelpeeAddress.Value}; reason={decision.Reason ?? "(none)"}");
        Log($"SendHelpRequestDecisionAsync sent HelpRequestDecision with Ack (msg_id={envelope.MessageId}, accepted={decision.Accepted})");
    }

    public async Task SendHelpRequestCancellationAsync(HelpRequestMessage request, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await client.ConnectAsync(ct).ConfigureAwait(false);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "request_canceled" : reason.Trim();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new HelpRequestDecisionPayload
        {
            requestId = request.RequestId,
            helpeeAddress = request.HelpeeAddress.Value,
            helperAddress = request.HelperAddress.Value,
            accepted = false,
            reason = normalizedReason,
        });
        var envelope = CreateEnvelope(CreateAddressSessionContextCode(), MsgType.HelpRequestDecision, payload, replyTo: null);
        await SendEnvelopeWithAckRetryAsync(request.HelperAddress.Value, envelope, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_cancellation_sent; request_id={request.RequestId}; helper_address={request.HelperAddress.Value}; helpee_address={request.HelpeeAddress.Value}; reason={normalizedReason}");
        Log($"SendHelpRequestCancellationAsync sent HelpRequestDecision cancellation with Ack (msg_id={envelope.MessageId}, reason={normalizedReason})");
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

        var helperAddress = validation.Invite.BoundHelperAddress ??
                            new PeerAddress(identity.Address);
        if (InviteSecurityDiagnostics.RequiresBoundHelperForIssuedSecretInvites() &&
            validation.Invite.BoundHelperAddress is null)
        {
            UpdateSessionSecurityState(currentSessionSecurityState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_helper_required"));
            Log("JoinByInviteAsync rejected before join (reason=invite_helper_required)");
            throw new InvalidOperationException("Invite token must be bound to the verified helper identity.");
        }

        if (validation.Invite.BoundHelperAddress is not null &&
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
            var effectiveHelperAddress = outboundHandshake.InviteValidated
                ? outboundHandshake.HelperAddress
                : new PeerAddress(string.IsNullOrWhiteSpace(client.Address) ? identity.Address : client.Address);
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
                screenShareCursorOverlaySupported = LocalSupportsScreenShareCursorOverlay,
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

    public Task SendSessionEndAsync(CancellationToken ct)
        => SendSessionEndAsync("user_exit", ct);

    public async Task SendSessionEndAsync(string? reason, CancellationToken ct)
    {
        ThrowIfDisposed();

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode) ||
            string.IsNullOrWhiteSpace(remoteEndpoint) ||
            currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            return;
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "user_exit" : reason.Trim();
        var payload = CreateSecureLifecyclePayload(
            MsgType.SessionEnd,
            requestId: null,
            JsonSerializer.SerializeToUtf8Bytes(new SessionEndPayload
            {
                sessionId = sessionId.Value,
                reason = normalizedReason,
            }));

        var envelope = CreateEnvelope(envelopeCode, MsgType.SessionEnd, payload, replyTo: null);
        SessionTimeline.Record("SessionEndSent");
        var result = await SendLifecycleEnvelopeAsync(
                new LifecycleDeliveryOptions(
                    MessageType: MsgType.SessionEnd,
                    RequestId: null,
                    ControlDestination: remoteEndpoint,
                    BulkDestination: remoteBulkEndpoint,
                    LogCategory: "SessionSecurity",
                    LogEvent: "session_end_lifecycle_delivery",
                    AllowBulkDuplicate: true,
                    IgnoreCallerCancellation: true,
                    AcceptancePolicy: LifecycleDeliveryAcceptancePolicy.PeerVisibleRequired,
                    UseControlAckRetry: true,
                    PeerCopyAttempts: 2,
                    ThrowOnFailure: true),
                envelope,
                ct)
            .ConfigureAwait(false);

        var bulkSent = result.BulkCopy && string.Equals(result.BulkCopyLane, "bulk_copy", StringComparison.Ordinal);
        var bulkRetry = result.BulkCopy && !string.Equals(result.BulkCopyLane, "bulk_copy", StringComparison.Ordinal);
        var controlCopy = result.ControlCopy && string.Equals(result.ControlCopyLane, "control_copy", StringComparison.Ordinal);

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=session_end_redundant_sent; transport=nkn; session_id={SanitizeLogToken(sessionId.Value)}; reason={SanitizeLogToken(normalizedReason)}; control_ack={(result.ControlAck ? 1 : 0)}; control_copy={(controlCopy ? 1 : 0)}; bulk_copy={(result.BulkCopy ? 1 : 0)}; bulk_sent={(bulkSent ? 1 : 0)}; bulk_retry={(bulkRetry ? 1 : 0)}; delivered_any={(result.PeerVisibleAny ? 1 : 0)}; control_error={result.ControlAckErrorName}; control_copy_error={result.ControlCopyErrorName}; bulk_error={result.BulkCopyErrorName}; bulk_retry_error={(bulkRetry ? "(none)" : result.BulkCopyErrorName)}");
        Log($"SendSessionEndAsync sent SessionEnd with Ack or redundant copy (msg_id={envelope.MessageId}, control_ack={(result.ControlAck ? 1 : 0)}, control_copy={(result.ControlCopy ? 1 : 0)}, bulk_copy={(result.BulkCopy ? 1 : 0)})");
    }

    public async Task SendSessionHeartbeatAsync(SessionHeartbeatMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode) ||
            string.IsNullOrWhiteSpace(remoteEndpoint) ||
            currentSessionSecurityState.SessionId is not SessionId sessionId ||
            !string.Equals(message.SessionId, sessionId.Value, StringComparison.Ordinal))
        {
            var reason = !TryGetCurrentEnvelopeCode(out _)
                ? "missing_envelope_code"
                : string.IsNullOrWhiteSpace(remoteEndpoint)
                    ? "missing_remote_endpoint"
                    : currentSessionSecurityState.SessionId is not SessionId
                        ? "missing_session_id"
                        : "session_id_mismatch";
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=session_liveness_heartbeat_send_skipped; transport=nkn; reason={reason}; requested_session_id={SanitizeLogToken(message.SessionId)}; current_session_id={SanitizeLogToken(currentSessionSecurityState.SessionId?.Value ?? "(none)")}; generation={message.Generation}; sequence={message.Sequence}");
            return;
        }

        var heartbeatPayload = new SessionHeartbeatPayload
        {
            sessionId = sessionId.Value,
            generation = message.Generation,
            sequence = message.Sequence,
            sentUtcMs = message.SentUtcMs,
            role = string.IsNullOrWhiteSpace(message.Role) ? "unknown" : message.Role.Trim(),
        };
        var payload = CreateSecureLifecyclePayload(
            MsgType.SessionHeartbeat,
            requestId: null,
            JsonSerializer.SerializeToUtf8Bytes(heartbeatPayload));
        var envelope = CreateEnvelope(envelopeCode, MsgType.SessionHeartbeat, payload, replyTo: null);
        Task<SessionHeartbeatCopySendResult>? bulkCopyTask = null;

        Exception? ackError = null;
        try
        {
            await SendEnvelopeWithAckRetryAsync(
                    remoteEndpoint,
                    envelope,
                    ct,
                    afterPendingAckRegistered: () =>
                    {
                        if (!string.IsNullOrWhiteSpace(remoteBulkEndpoint))
                        {
                            bulkCopyTask = SendSessionHeartbeatCopyAsync(remoteBulkEndpoint, envelope, lane: "bulk", ct);
                        }
                    })
                .ConfigureAwait(false);
            RaiseSessionLivenessProof(
                sessionId.Value,
                message.Generation,
                message.Sequence,
                "heartbeat_ack",
                "control_ack");
        }
        catch (Exception ex)
        {
            ackError = ex;
        }

        var bulkCopyResult = bulkCopyTask is null
            ? new SessionHeartbeatCopySendResult("bulk", false, null)
            : await bulkCopyTask.ConfigureAwait(false);
        var deliveredAny = ackError is null || bulkCopyResult.Succeeded;
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=session_liveness_heartbeat_sent; transport=nkn; session_id={SanitizeLogToken(sessionId.Value)}; generation={message.Generation}; sequence={message.Sequence}; control_ack={(ackError is null ? 1 : 0)}; bulk_copy={(bulkCopyResult.Succeeded ? 1 : 0)}; delivered_any={(deliveredAny ? 1 : 0)}; control_error={ackError?.GetType().Name ?? "(none)"}; bulk_error={bulkCopyResult.Error?.GetType().Name ?? "(none)"}");

        if (!deliveredAny)
        {
            throw ackError ??
                  bulkCopyResult.Error ??
                  new InvalidOperationException("Session heartbeat send failed on all lanes.");
        }
    }

    private async Task<SessionHeartbeatCopySendResult> SendSessionHeartbeatCopyAsync(
        string destination,
        Envelope envelope,
        string lane,
        CancellationToken ct)
    {
        try
        {
            await SendBulkEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            return new SessionHeartbeatCopySendResult(lane, true, null);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=session_liveness_heartbeat_{lane}_failed; transport=nkn; error={ex.GetType().Name}");
            return new SessionHeartbeatCopySendResult(lane, false, ex);
        }
    }

    private async Task<SessionEndCopySendResult> SendSessionEndCopyAsync(
        string destination,
        Envelope envelope,
        bool useBulkLane,
        string lane,
        CancellationToken ct)
    {
        try
        {
            if (useBulkLane)
            {
                await SendBulkEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }
            else
            {
                await SendEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
            }

            return new SessionEndCopySendResult(lane, true, null);
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=session_end_redundant_{lane}_failed; transport=nkn; error={ex.GetType().Name}");
            return new SessionEndCopySendResult(lane, false, ex);
        }
    }

    private readonly record struct SessionEndCopySendResult(string Lane, bool Succeeded, Exception? Error);
    private readonly record struct SessionHeartbeatCopySendResult(string Lane, bool Succeeded, Exception? Error);

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

    internal void RouteLifecycleEnvelope(NknInboundEnvelopeContext inboundContext)
    {
        var source = inboundContext.Source;
        var env = inboundContext.Envelope;
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
            case MsgType.SessionHeartbeat:
                HandleSessionHeartbeat(inboundContext);
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
            case MsgType.HelpRequest:
                HandleHelpRequest(source, env);
                break;
            case MsgType.HelpRequestDecision:
                HandleHelpRequestDecision(source, env);
                break;
            default:
                throw new InvalidOperationException($"Lifecycle channel cannot route {env.Type}.");
        }
    }

    private void HandleHelpRequest(string source, Envelope env)
    {
        if (!TryParseHelpRequestPayload(env.Payload, out var request) ||
            string.IsNullOrWhiteSpace(request.requestId) ||
            string.IsNullOrWhiteSpace(request.helpeeAddress) ||
            string.IsNullOrWhiteSpace(request.helperAddress) ||
            string.IsNullOrWhiteSpace(request.inviteToken) ||
            !PeerAddress.TryParse(request.helpeeAddress, out var helpeeAddress) ||
            !PeerAddress.TryParse(request.helperAddress, out var helperAddress))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("help_request_invalid");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_rejected; reason=invalid_payload; msg_id={env.MessageId}; source={source ?? "(none)"}");
            return;
        }

        var localHelper = new PeerAddress(LocalPeerAddress);
        var admission = helpRequestAdmissionGuard.Admit(
            source,
            localHelper,
            helperAddress,
            helpeeAddress,
            request.requestId!,
            request.inviteToken!,
            DateTimeOffset.UtcNow);
        if (admission != HelpRequestAdmissionDecision.Accepted)
        {
            var reason = GetHelpRequestAdmissionRejectionReason(admission);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"help_request_{reason}");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_rejected; reason={reason}; msg_id={env.MessageId}; request_id={request.requestId}; source={source ?? "(none)"}; helper_address={request.helperAddress}; helpee_address={request.helpeeAddress}");

            if (!string.IsNullOrWhiteSpace(source))
            {
                SendAckFireAndForget(source, env.Code, env.MessageId);
            }

            return;
        }

        var validation = inviteTokenValidator.Validate(request.inviteToken, DateTimeOffset.UtcNow, InviteValidationMode.InspectOnly);
        if (!validation.IsSuccess || validation.Invite is null)
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("help_request_invite_invalid");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_rejected; reason=invite_invalid; msg_id={env.MessageId}; request_id={request.requestId}; source={source ?? "(none)"}; helper_address={request.helperAddress}; helpee_address={request.helpeeAddress}");
            return;
        }

        if (validation.Invite.BoundHelperAddress is not null &&
            !AddressesLikelySamePeer(validation.Invite.BoundHelperAddress.Value.Value, localHelper.Value))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("help_request_helper_mismatch");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_rejected; reason=helper_mismatch; msg_id={env.MessageId}; request_id={request.requestId}; source={source ?? "(none)"}; bound_helper={validation.Invite.BoundHelperAddress.Value.Value}; local_helper={localHelper.Value}; request_helper={request.helperAddress}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_received; request_id={request.requestId}; source={source ?? "(none)"}; helper_address={request.helperAddress}; helpee_address={request.helpeeAddress}");

        IncomingHelpRequest?.Invoke(
            this,
            new IncomingHelpRequestEventArgs(
                new HelpRequestMessage(
                    request.requestId!,
                    helpeeAddress,
                    helperAddress,
                    request.inviteToken!)));
    }

    private static string GetHelpRequestAdmissionRejectionReason(HelpRequestAdmissionDecision decision) =>
        decision switch
        {
            HelpRequestAdmissionDecision.DuplicateRecent => "duplicate_recent",
            HelpRequestAdmissionDecision.SourceThrottled => "source_throttled",
            HelpRequestAdmissionDecision.RequestChurnThrottled => "request_churn_throttled",
            _ => "unknown",
        };

    private void HandleHelpRequestDecision(string source, Envelope env)
    {
        if (!TryParseHelpRequestDecisionPayload(env.Payload, out var decision) ||
            string.IsNullOrWhiteSpace(decision.requestId) ||
            string.IsNullOrWhiteSpace(decision.helpeeAddress) ||
            string.IsNullOrWhiteSpace(decision.helperAddress) ||
            !PeerAddress.TryParse(decision.helpeeAddress, out var helpeeAddress) ||
            !PeerAddress.TryParse(decision.helperAddress, out var helperAddress))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("help_request_decision_invalid");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_decision_rejected; reason=invalid_payload; msg_id={env.MessageId}; source={source ?? "(none)"}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        CancelPendingDirectHelpRequestAcks(
            helperAddress.Value,
            "decision_received",
            MsgType.HelpRequest);

        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_decision_received; request_id={decision.requestId}; accepted={decision.accepted == true}; source={source ?? "(none)"}; helper_address={decision.helperAddress}; helpee_address={decision.helpeeAddress}; reason={decision.reason ?? "(none)"}");

        HelpRequestDecisionReceived?.Invoke(
            this,
            new HelpRequestDecisionEventArgs(
                new HelpRequestDecisionMessage(
                    decision.requestId!,
                    helpeeAddress,
                    helperAddress,
                    decision.accepted == true,
                    decision.reason)));
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

        remoteEndpoint = string.IsNullOrWhiteSpace(source)
            ? join.helperEndpoint
            : source;
        remoteMediaEndpoint = string.IsNullOrWhiteSpace(join.helperMediaEndpoint)
            ? join.helperEndpoint
            : join.helperMediaEndpoint;
        remoteBulkEndpoint = string.IsNullOrWhiteSpace(join.helperBulkEndpoint)
            ? join.helperEndpoint
            : join.helperBulkEndpoint;
        lastPeerAddress = string.IsNullOrWhiteSpace(source) ? join.helperEndpoint : source;
        remoteSupportsRemoteControl = join.remoteControlSupported == true;
        remoteSupportsScreenShareCursorOverlay = join.screenShareCursorOverlaySupported == true;
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

        var handshakeRemoteEndpoint = !string.IsNullOrWhiteSpace(source)
            ? source
            : join.helperEndpoint;
        ReplacePendingInboundHandshake(new PendingInboundHandshakeState(
            joinRequestMessageId: env.MessageId,
            remoteEndpoint: handshakeRemoteEndpoint,
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
        var verificationCode = CreateSessionVerificationCode(
            macKey,
            challenge.SessionId,
            pendingOutboundHandshake.HelperAddress,
            challenge.HelpeeAddress,
            helperKeyPair.PublicKey,
            helpeePubKey,
            challenge.ChallengeNonce,
            sessionContextCode);
        pendingOutboundHandshake = pendingOutboundHandshake.WithChallenge(
            challenge.ChallengeNonce,
            DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUtcMs),
            helpeePubKey);
        LogSessionVerificationCodeReady("Helper", challenge.SessionId, pendingOutboundHandshake.HelperAddress, verificationCode);
        UpdateSessionSecurityState(
            currentSessionSecurityState.WithHandshakeChallenge(
                challenge.SessionId,
                challenge.HelpeeAddress,
                pendingOutboundHandshake.HelperAddress,
                pendingOutboundHandshake.InviteValidated,
                DateTimeOffset.FromUnixTimeMilliseconds(challenge.ExpiresAtUtcMs))
            .WithVerificationCode(verificationCode));

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
        var verificationCode = CreateSessionVerificationCode(
            macKey,
            sessionId,
            pending.HelperAddress,
            helpeeAddress,
            pending.HelperEcdhPublicKey,
            helpeeKeyPair.PublicKey,
            pending.ChallengeNonce,
            pending.EnvelopeCode);

        ReplacePendingJoinRequest(new PendingJoinRequestState(
            pending.JoinRequestMessageId,
            pending.RemoteEndpoint,
            pending.HelperAddress,
            pending.HelperEcdhPublicKey,
            pending.EnvelopeCode,
            sessionId,
            approvalRequest));
        ClearPendingInboundHandshake(pending.JoinRequestMessageId);
        LogSessionVerificationCodeReady("Helpee", sessionId, pending.HelperAddress, verificationCode);
        UpdateSessionSecurityState(
            currentSessionSecurityState
                .WithHandshakeVerified(pending.HelperAddress)
                .WithVerificationCode(verificationCode));
        CancelPendingDirectHelpRequestAcks(
            pending.HelperAddress.Value,
            "incoming_join_request",
            MsgType.HelpRequest,
            MsgType.HelpRequestDecision);

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
            remoteSupportsScreenShareCursorOverlay = secureApprove.screenShareCursorOverlaySupported == true;
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

        RecordTunaFallbackNknControlReceived(MsgType.Chat, currentSessionSecurityState.SessionId?.Value, env.Payload.Length);
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

        RecordTunaFallbackNknControlReceived(MsgType.SessionEnd, currentSessionSecurityState.SessionId?.Value, env.Payload.Length);
        Log($"SessionEnd dispatched (msg_id={env.MessageId})");
        RemoteSessionEnded?.Invoke(this, EventArgs.Empty);
    }

    private void HandleSessionHeartbeat(NknInboundEnvelopeContext inboundContext)
    {
        var source = inboundContext.Source;
        var env = inboundContext.Envelope;
        if (!TryDecryptLifecyclePayload(
                source,
                env.MessageId,
                "session_heartbeat",
                env.Payload,
                TryGetControlSessionSharedKey(),
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "session_heartbeat",
                    SessionId: currentSessionSecurityState.SessionId,
                    SenderIdentity: TryResolveExpectedRemotePeerAddressForLifecycle()),
                inboundLifecycleReplayWindow,
                out var securePayload))
        {
            return;
        }

        if (!TryParseSessionHeartbeatPayload(securePayload.Plaintext, out var heartbeat))
        {
            NknRuntimeDiagnostics.SetLastError("session_heartbeat_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("session_heartbeat_payload_invalid");
            Log($"SessionHeartbeat payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateSessionHeartbeatMessageSession(
                heartbeat.sessionId,
                env.MessageId,
                source,
                inboundContext.Channel))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            SendAckFireAndForget(source, env.Code, env.MessageId);
        }

        var lane = inboundContext.Channel.ToString().ToLowerInvariant();
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=session_liveness_heartbeat_received; transport=nkn; session_id={SanitizeLogToken(heartbeat.sessionId)}; generation={heartbeat.generation.GetValueOrDefault()}; sequence={heartbeat.sequence.GetValueOrDefault()}; lane={lane}; source={source ?? "(none)"}; role={SanitizeLogToken(heartbeat.role ?? "unknown")}; msg_id={env.MessageId}");
        RaiseSessionLivenessProof(
            heartbeat.sessionId,
            heartbeat.generation.GetValueOrDefault(),
            heartbeat.sequence.GetValueOrDefault(),
            "heartbeat_received",
            lane);
    }

    private bool TryValidateSessionHeartbeatMessageSession(
        string? messageSessionId,
        string messageId,
        string? source,
        NknBridgeChannel channel)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedControlSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        var expectedBulkSource = ResolveExpectedRemoteBulkPeerAddressForCurrentSession();
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
        else if (!string.IsNullOrWhiteSpace(expectedControlSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (AddressMatchesForSessionPolicy(normalizedSource, expectedControlSource) ||
                 (channel == NknBridgeChannel.Bulk &&
                  !string.IsNullOrWhiteSpace(expectedBulkSource) &&
                  AddressMatchesForSessionPolicy(normalizedSource, expectedBulkSource)))
        {
            return true;
        }
        else
        {
            failureReason = "source_identity_mismatch";
        }

        NknRuntimeDiagnostics.SetLastError($"session_heartbeat_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"session_heartbeat_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=control_message_rejected; message_type=session_heartbeat; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; source={normalizedSource ?? "(none)"}; expected_source={expectedControlSource ?? "(none)"}; expected_bulk_source={expectedBulkSource ?? "(none)"}; channel={channel.ToString().ToLowerInvariant()}; msg_id={messageId}");
        Log($"SessionHeartbeat rejected (msg_id={messageId}, reason={failureReason})");
        return false;
    }

    private void HandleScreenShareFrame(NknInboundEnvelopeContext inboundContext)
    {
        if (!TryDecryptScreenSharePayload(inboundContext.Source, inboundContext.Envelope, MsgType.ScreenShareFrame, out var securePayload))
        {
            return;
        }

        var secureDecryptCompletedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out _)
            || fragments.Length == 0)
        {
            NknRuntimeDiagnostics.SetLastError("screenshare_frame_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("screenshare_frame_payload_invalid");
            Log($"ScreenShareFrame payload invalid (msg_id={inboundContext.Envelope.MessageId}, payload_len={inboundContext.Envelope.Payload.Length})");
            return;
        }

        var fragmentEnvelopeDeserializedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var chunk = fragments[0];
        var logStartupFrameDispatch = chunk.FrameId <= ScreenShareControlBootstrapMaxFrameId;
        if (inboundContext.Channel == NknBridgeChannel.Control)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_inbound_transport; channel=control; session_id={chunk.SessionId}; stream_epoch={chunk.StreamEpoch}; frame_id={chunk.FrameId}; fragment_index={chunk.FragmentIndex}; fragment_count={chunk.FragmentCount}; fragments_in_envelope={fragments.Length}; is_keyframe={(chunk.IsKeyFrame ? 1 : 0)}; source={inboundContext.Source}");
        }
        else if (inboundContext.Channel == NknBridgeChannel.Media && chunk.FrameId <= 2)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_inbound_transport; channel=media; session_id={chunk.SessionId}; stream_epoch={chunk.StreamEpoch}; frame_id={chunk.FrameId}; fragment_index={chunk.FragmentIndex}; fragment_count={chunk.FragmentCount}; fragments_in_envelope={fragments.Length}; is_keyframe={(chunk.IsKeyFrame ? 1 : 0)}; source={inboundContext.Source}");
        }

        if (!TryValidateScreenShareSecureMetadata("screenshare_frame", securePayload.Metadata, inboundContext.Envelope.MessageId) ||
            !TryValidateScreenShareMessageSession(
                "screenshare_frame",
                chunk.SessionId,
                inboundContext.Envelope.MessageId,
                requestId: null,
                inboundContext.Source) ||
            !TryValidateScreenShareSession("frame", chunk.SessionId) ||
            !IsScreenShareAuthorizedForDispatch("frame", chunk.SessionId))
        {
            return;
        }

        RecordTunaFallbackNknFrameReceived(
            MsgType.ScreenShareFrame,
            inboundContext.Channel,
            inboundContext.Envelope.Payload.Length,
            chunk.SessionId);

        if (ShouldIgnoreAcceleratedScreenShareFrameDuringFallback(chunk.SessionId))
        {
            LogAcceleratedScreenShareFrameIgnoredDuringFallback(chunk.SessionId, chunk.StreamEpoch, chunk.FrameId);
            return;
        }

        foreach (var fragment in fragments)
        {
            ScreenShareFrameLossAttributionRegistry.ObserveInboundReceivePath(
                fragment.SessionId,
                fragment.StreamEpoch,
                fragment.FrameId,
                fragment.IsKeyFrame,
                capturedTsUtcMs: fragment.CapturedTsUtcMs,
                envelopeSendUtcMs: inboundContext.Envelope.UnixTimeMs,
                socketDataEventEmittedUtcMs: inboundContext.SocketDataEventEmittedUtcMs,
                wsReceiverWriteEnteredUtcMs: inboundContext.WsReceiverWriteEnteredUtcMs,
                wsMessageEmittedUtcMs: inboundContext.WsMessageEmittedUtcMs,
                sdkHandleMsgEnteredUtcMs: inboundContext.SdkHandleMsgEnteredUtcMs,
                clientMessageDispatchUtcMs: inboundContext.ClientMessageDispatchUtcMs,
                multiClientMessageDispatchUtcMs: inboundContext.MultiClientMessageDispatchUtcMs,
                bridgeMessageObservedUtcMs: inboundContext.BridgeMessageObservedUtcMs,
                binaryFrameDecodedUtcMs: inboundContext.BinaryFrameDecodedUtcMs,
                bridgeIngressObservedUtcMs: inboundContext.BridgeIngressObservedUtcMs,
                envelopeParsedUtcMs: inboundContext.EnvelopeParsedUtcMs,
                secureDecryptCompletedUtcMs: secureDecryptCompletedUtcMs,
                fragmentEnvelopeDeserializedUtcMs: fragmentEnvelopeDeserializedUtcMs);

            if (logStartupFrameDispatch)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_frame_inbound_dispatch; stage=reassembler_enter; channel={inboundContext.Channel.ToString().ToLowerInvariant()}; session_id={fragment.SessionId}; stream_epoch={fragment.StreamEpoch}; frame_id={fragment.FrameId}; fragment_index={fragment.FragmentIndex}; fragment_count={fragment.FragmentCount}; is_keyframe={(fragment.IsKeyFrame ? 1 : 0)}; msg_id={inboundContext.Envelope.MessageId}");
            }

            secureScreenShareFrameReassembler.OnFragment(fragment);

            if (logStartupFrameDispatch)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_frame_inbound_dispatch; stage=reassembler_exit; channel={inboundContext.Channel.ToString().ToLowerInvariant()}; session_id={fragment.SessionId}; stream_epoch={fragment.StreamEpoch}; frame_id={fragment.FrameId}; fragment_index={fragment.FragmentIndex}; fragment_count={fragment.FragmentCount}; is_keyframe={(fragment.IsKeyFrame ? 1 : 0)}; msg_id={inboundContext.Envelope.MessageId}");
            }
        }

        if (logStartupFrameDispatch)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_frame_inbound_dispatch; stage=completed; channel={inboundContext.Channel.ToString().ToLowerInvariant()}; session_id={chunk.SessionId}; stream_epoch={chunk.StreamEpoch}; frame_id={chunk.FrameId}; fragment_count={chunk.FragmentCount}; is_keyframe={(chunk.IsKeyFrame ? 1 : 0)}; msg_id={inboundContext.Envelope.MessageId}");
        }
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
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=ack_ignored; reason=no_pending_wait; msg_id={env.MessageId}; reply_to={env.ReplyTo}; source={source ?? "(none)"}");
            Log($"Ack ignored (no pending wait, msg_id={env.MessageId}, reply_to={env.ReplyTo})");
            return;
        }

        if (!AddressesLikelySamePeer(source, pending.Destination))
        {
            NknRuntimeDiagnostics.IncrementAcksIgnoredSourceMismatch();
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("ack_source_mismatch");
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=ack_ignored; reason=source_mismatch; msg_id={env.MessageId}; reply_to={env.ReplyTo}; source={source ?? "(none)"}; expected_destination={pending.Destination}; pending_type={pending.Type}");
            Log($"Ack ignored (source mismatch, msg_id={env.MessageId}, reply_to={env.ReplyTo}, source_len={source?.Length ?? 0})");
            return;
        }

        if (pendingAcks.TryRemove(env.ReplyTo, out var removed))
        {
            removed.TryComplete(AckWaitOutcome.Acknowledged, reason: null);
        }

        RecordTunaFallbackNknControlReceived(MsgType.Ack, currentSessionSecurityState.SessionId?.Value, env.Payload.Length);
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
            CancelPendingDirectHelpRequestAcks(
                pendingState.HelperAddress.Value,
                "local_approve",
                MsgType.HelpRequest,
                MsgType.HelpRequestDecision);
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
                    screenShareCursorOverlaySupported = LocalSupportsScreenShareCursorOverlay,
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
        var logBootstrapGate = false;
        QueuedScreenShareEnvelopeMetadata? bootstrapMetadata = null;
        if (envelope.Type == MsgType.ScreenShareFrame)
        {
            bootstrapMetadata = TryCreateQueuedScreenShareEnvelopeMetadata(bytes, recoverySendRole: null, recoveryBurstToken: 0);
            logBootstrapGate = bootstrapMetadata is not null && bootstrapMetadata.FrameId <= ScreenShareControlBootstrapMaxFrameId;
        }

        try
        {
            if (!await outboundSendGate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                if (logBootstrapGate)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_control_bootstrap_gate_stage; stage=blocked; stream_epoch={bootstrapMetadata!.StreamEpoch}; frame_id={bootstrapMetadata.FrameId}; is_keyframe={(bootstrapMetadata.IsKeyFrame ? 1 : 0)}; msg_id={envelope.MessageId}; holder={outboundSendGateOwnerForDiagnostics ?? "(none)"}");
                }

                await outboundSendGate.WaitAsync(ct).ConfigureAwait(false);
            }

            outboundSendGateOwnerForDiagnostics = $"{envelope.Type}:{envelope.MessageId}";
            if (logBootstrapGate)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_control_bootstrap_gate_stage; stage=acquired; stream_epoch={bootstrapMetadata!.StreamEpoch}; frame_id={bootstrapMetadata.FrameId}; is_keyframe={(bootstrapMetadata.IsKeyFrame ? 1 : 0)}; msg_id={envelope.MessageId}");
            }

            try
            {
                NknRuntimeDiagnostics.IncrementMessagesSent();
                await client.SendAsync(destination, bytes, ct).ConfigureAwait(false);
                RecordTunaFallbackNknControlSent(envelope.Type);
            }
            finally
            {
                outboundSendGateOwnerForDiagnostics = null;
                outboundSendGate.Release();
                if (logBootstrapGate)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_control_bootstrap_gate_stage; stage=released; stream_epoch={bootstrapMetadata!.StreamEpoch}; frame_id={bootstrapMetadata.FrameId}; is_keyframe={(bootstrapMetadata.IsKeyFrame ? 1 : 0)}; msg_id={envelope.MessageId}");
                }
            }
            if (FileTransferDiagnosticLogPolicy.TraceEnabled)
            {
                Log($"Envelope sent (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
            }
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

    private async Task<bool> SendBulkEnvelopeAsync(
        string destination,
        Envelope envelope,
        byte[] bytes,
        CancellationToken ct,
        bool allowAcceleration = true,
        bool allowAccelerationDuringRegularNknFallback = false)
    {

        try
        {
            if (allowAcceleration &&
                envelope.Type == MsgType.FileTransferDataFrame &&
                await TrySendAcceleratedEnvelopeAsync(
                        envelope.Type,
                        NknBridgeChannel.Bulk,
                        bytes,
                        ct,
                        allowDuringRegularNknFallback: allowAccelerationDuringRegularNknFallback)
                    .ConfigureAwait(false))
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_accelerated_file_frame_sent; channel=bulk; payload_bytes={bytes.Length}");
                if (FileTransferDiagnosticLogPolicy.TraceEnabled)
                {
                    Log($"Bulk envelope sent via Tuna acceleration (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
                }

                return true;
            }

            NknRuntimeDiagnostics.IncrementMessagesSent();
            await client.SendBulkAsync(destination, bytes, ct).ConfigureAwait(false);
            if (envelope.Type == MsgType.FileTransferDataFrame)
            {
                RecordTunaFallbackNknFrameSent(envelope.Type, NknBridgeChannel.Bulk, bytes.Length);
            }
            if (FileTransferDiagnosticLogPolicy.TraceEnabled)
            {
                Log($"Bulk envelope sent (type={envelope.Type}, payload_len={envelope.Payload.Length}, msg_id={envelope.MessageId})");
            }

            return false;
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            Log($"Bulk envelope send failed (type={envelope.Type}, msg_id={envelope.MessageId}, ex={ex.GetType().Name})");
            throw;
        }
    }

    private async Task SendEnvelopeWithAckRetryAsync(
        string destination,
        Envelope envelope,
        CancellationToken ct,
        Action? afterPendingAckRegistered = null)
    {
        var ackWait = new TaskCompletionSource<AckWaitOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAck = new PendingAckWait(destination, envelope.Type, ackWait);
        if (!pendingAcks.TryAdd(envelope.MessageId, pendingAck))
        {
            throw new InvalidOperationException("pending_ack_exists");
        }

        try
        {
            afterPendingAckRegistered?.Invoke();
            for (var attempt = 0; attempt <= AckRetryDelays.Length; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                await SendEnvelopeAsync(destination, envelope, ct);

                try
                {
                    var outcome = await ackWait.Task.WaitAsync(AckWaitTimeout, ct);
                    if (outcome == AckWaitOutcome.Superseded)
                    {
                        Log($"Ack wait superseded (msg_id={envelope.MessageId}, type={envelope.Type}, reason={pendingAck.CompletionReason ?? "superseded"})");
                        return;
                    }

                    Log($"Ack received (msg_id={envelope.MessageId}, type={envelope.Type}, attempt={attempt + 1})");
                    return;
                }
                catch (TimeoutException)
                {
                    if (attempt == AckRetryDelays.Length)
                    {
                        NknRuntimeDiagnostics.SetLastError("ack_timeout");
                        LocalOperationalLog.Warn(
                            "DirectHelpRequest",
                            $"event=ack_timeout; type={envelope.Type}; destination={destination}; msg_id={envelope.MessageId}; attempts={attempt + 1}");
                        Log($"Ack timeout (msg_id={envelope.MessageId}, type={envelope.Type}, attempts={attempt + 1})");
                        if (ShouldDisconnectOnAckTimeout(envelope.Type))
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
        CompleteTunaFallbackProof("reset_session_tracking");
        ClearUnresolvedFileTransferV6TransportEpochs("reset_session_tracking");
        ClearPendingFileTransferV6Handoffs("reset_session_tracking");
        FlushAllControlOutboundQueues("reset_session_tracking");
        ClearScreenShareOutboundQueue("reset_session_tracking");
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
        remoteSupportsScreenShareCursorOverlay = false;
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
        ClearScreenShareOutboundQueue("clear_active_peer_session_tracking");
        secureScreenShareFrameReassembler.ClearAll();
        remoteEndpoint = null;
        remoteMediaEndpoint = null;
        remoteBulkEndpoint = null;
        currentEnvelopeCode = null;
        lastPeerAddress = null;
        helperJoinRequestMessageId = null;
        pendingOutboundHandshake = null;
        remoteSupportsRemoteControl = false;
        remoteSupportsScreenShareCursorOverlay = false;
        transportRemoteControlState = RemoteControlSessionState.Default;
        DisposeEphemeralKeyState(preserveHelpeeHostKeyPair);
    }

    private void CancelPendingAcks()
    {
        foreach (var pair in pendingAcks)
        {
            if (pendingAcks.TryRemove(pair.Key, out var pending))
            {
                pending.TryCancel();
            }
        }
    }

    private void CancelPendingDirectHelpRequestAcks(
        string? destination,
        string reason,
        params MsgType[] types)
    {
        if (types is null || types.Length == 0)
        {
            return;
        }

        foreach (var pair in pendingAcks.ToArray())
        {
            var pending = pair.Value;
            if (Array.IndexOf(types, pending.Type) < 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(destination) &&
                !AddressesLikelySamePeer(destination, pending.Destination))
            {
                continue;
            }

            if (pendingAcks.TryRemove(pair.Key, out var removed) &&
                removed.TryComplete(AckWaitOutcome.Superseded, reason))
            {
                LocalOperationalLog.Info(
                    "DirectHelpRequest",
                    $"event=ack_wait_canceled; type={removed.Type}; destination={removed.Destination}; reason={reason}; msg_id={pair.Key}");
            }
        }
    }

    private static bool ShouldDisconnectOnAckTimeout(MsgType type)
    {
        return type switch
        {
            MsgType.Chat => false,
            MsgType.SessionHeartbeat => false,
            MsgType.HelpRequest => false,
            MsgType.HelpRequestDecision => false,
            MsgType.TransportAccelerationPayerIntent => false,
            MsgType.TransportAccelerationOffer => false,
            MsgType.TransportAccelerationAnswer => false,
            MsgType.TransportAccelerationAnswerAck => false,
            MsgType.TransportAccelerationDown => false,
            _ => true,
        };
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

        nextState = PreserveActiveApprovedSessionIfStale(nextState);

        if (Equals(currentSessionSecurityState, nextState))
        {
            return;
        }

        if (!ShouldRetainCapabilitySecureState(nextState))
        {
            ResetControlSecureState();
        }

        currentSessionSecurityState = nextState;
        UpdateActiveApprovedSessionTracking(nextState);
        SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        if (accelerationLane is null)
        {
            return;
        }

        if (IsSessionAccelerationEligible(out _))
        {
            ScheduleAccelerationNegotiationIfEligible("session_security_state_ready");
        }
        else
        {
            ResetAccelerationNegotiation("session_security_state_not_eligible");
        }
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

        if (nextMatchesActive)
        {
            if (ShouldIgnoreLateHandshakeFailureForActiveApprovedSession(nextState))
            {
                LocalOperationalLog.Info(
                    "SessionSecurity",
                    $"event=late_handshake_failure_ignored; session_id={nextState.SessionId?.Value ?? "(none)"}; helper_identity={nextState.HelperAddress?.Value ?? "(none)"}; reason={nextState.HandshakeFailureReason ?? "(none)"}; active_session_id={activeSessionId.Value}; active_helper_identity={activeHelperAddress.Value}");
                return currentSessionSecurityState;
            }

            return nextState;
        }

        if (ShouldRetainCapabilitySecureState(nextState))
        {
            return nextState;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=stale_security_state_ignored; session_id={nextState.SessionId?.Value ?? "(none)"}; helper_identity={nextState.HelperAddress?.Value ?? "(none)"}; active_session_id={activeSessionId.Value}; active_helper_identity={activeHelperAddress.Value}");
        return currentSessionSecurityState;
    }

    private static bool ShouldIgnoreLateHandshakeFailureForActiveApprovedSession(SessionSecurityState nextState)
    {
        if (nextState.ApprovalGranted ||
            nextState.HandshakeState is not (SessionHandshakeState.Failed or SessionHandshakeState.Expired))
        {
            return false;
        }

        return string.Equals(nextState.HandshakeFailureReason, "invite_revoked", StringComparison.Ordinal) ||
               string.Equals(nextState.HandshakeFailureReason, "invite_binding_mismatch", StringComparison.Ordinal) ||
               string.Equals(nextState.HandshakeFailureReason, "invite_helper_required", StringComparison.Ordinal) ||
               string.Equals(nextState.HandshakeFailureReason, "invite_helper_mismatch", StringComparison.Ordinal) ||
               string.Equals(nextState.HandshakeFailureReason, "handshake_start_timeout", StringComparison.Ordinal);
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

    private static bool TryParseHelpRequestPayload(byte[] payload, out HelpRequestPayload parsed)
    {
        parsed = new HelpRequestPayload();
        try
        {
            var dto = JsonSerializer.Deserialize<HelpRequestPayload>(payload);
            if (dto is null)
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseHelpRequestDecisionPayload(byte[] payload, out HelpRequestDecisionPayload parsed)
    {
        parsed = new HelpRequestDecisionPayload();
        try
        {
            var dto = JsonSerializer.Deserialize<HelpRequestDecisionPayload>(payload);
            if (dto is null)
            {
                return false;
            }

            parsed = dto;
            return true;
        }
        catch (JsonException)
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

    private static SessionVerificationCode CreateSessionVerificationCode(
        byte[] sessionRootKey,
        SessionId sessionId,
        PeerAddress helperAddress,
        PeerAddress helpeeAddress,
        byte[] helperEcdhPublicKey,
        byte[] helpeeEcdhPublicKey,
        string challengeNonce,
        string sessionContextCode)
    {
        return SessionVerificationCodeDerivation.Derive(new SessionVerificationMaterial(
            sessionId,
            helperAddress,
            helpeeAddress,
            sessionRootKey,
            helperEcdhPublicKey,
            helpeeEcdhPublicKey,
            challengeNonce,
            sessionContextCode));
    }

    private static void LogSessionVerificationCodeReady(
        string role,
        SessionId sessionId,
        PeerAddress helperAddress,
        SessionVerificationCode verificationCode)
    {
        var emojiCount = verificationCode.EmojiSequence.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=session_verification_code_ready; role={role}; session_id={sessionId.Value}; helper_identity={helperAddress.Value}; source={verificationCode.Source}; code_length={emojiCount}; fallback_length={verificationCode.FallbackCode.Length}");
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
            var dto = JsonSerializer.Deserialize<RejectPayload>(payload);
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
            var dto = JsonSerializer.Deserialize<SessionEndPayload>(payload);
            if (dto is null || string.IsNullOrWhiteSpace(dto.sessionId))
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

    private static bool TryParseSessionHeartbeatPayload(byte[] payload, out SessionHeartbeatPayload parsed)
    {
        parsed = default!;

        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SessionHeartbeatPayload>(payload);
            if (dto is null ||
                string.IsNullOrWhiteSpace(dto.sessionId) ||
                dto.generation.GetValueOrDefault() <= 0 ||
                dto.sequence.GetValueOrDefault() <= 0 ||
                dto.sentUtcMs.GetValueOrDefault() <= 0)
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
        bool IsLowPriorityMouseMove,
        bool IsLowPriorityScreenShareCursorState);

    private sealed class JoinRequestPayload
    {
        public string? helperEndpoint { get; set; }
        public string? helperMediaEndpoint { get; set; }
        public string? helperBulkEndpoint { get; set; }
        public string? helperIdentifier { get; set; }
        public string? helperEcdhPublicKey { get; set; }
        public bool? remoteControlSupported { get; set; }
        public bool? screenShareCursorOverlaySupported { get; set; }
    }

    private sealed class HelpRequestPayload
    {
        public string? requestId { get; set; }
        public string? helpeeAddress { get; set; }
        public string? helperAddress { get; set; }
        public string? inviteToken { get; set; }
    }

    private sealed class HelpRequestDecisionPayload
    {
        public string? requestId { get; set; }
        public string? helpeeAddress { get; set; }
        public string? helperAddress { get; set; }
        public bool? accepted { get; set; }
        public string? reason { get; set; }
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
        public bool? screenShareCursorOverlaySupported { get; set; }
        public string? helpeeMediaEndpoint { get; set; }
        public string? helpeeBulkEndpoint { get; set; }
        public string? approvalDecisionBase64 { get; set; }
    }

    private sealed class SessionEndPayload
    {
        public string? sessionId { get; set; }
        public string? reason { get; set; }
    }

    private sealed class SessionHeartbeatPayload
    {
        public string? sessionId { get; set; }
        public long? generation { get; set; }
        public long? sequence { get; set; }
        public long? sentUtcMs { get; set; }
        public string? role { get; set; }
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

    private enum HelpRequestAdmissionDecision
    {
        Accepted = 0,
        DuplicateRecent = 1,
        SourceThrottled = 2,
        RequestChurnThrottled = 3,
    }

    private sealed class HelpRequestAdmissionGuard
    {
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(30);
        private const int MaxRequestsPerSourceWindow = 4;
        private const int MaxDistinctRequestIdsPerInviteWindow = 2;
        private const int MaxRecentDuplicateKeys = 2048;

        private readonly object gate = new();
        private readonly Dictionary<string, long> recentExactRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<long>> sourceRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, long>> requestIdsByInvite = new(StringComparer.Ordinal);

        public HelpRequestAdmissionDecision Admit(
            string? source,
            PeerAddress localHelperAddress,
            PeerAddress helperAddress,
            PeerAddress helpeeAddress,
            string requestId,
            string inviteToken,
            DateTimeOffset nowUtc)
        {
            var nowTicks = nowUtc.UtcTicks;
            var sourceIdentity = NormalizeSourceIdentity(source, helpeeAddress);
            var normalizedRequestId = requestId.Trim();
            var exactKey = string.Join(
                "|",
                sourceIdentity,
                helperAddress.Value,
                helpeeAddress.Value,
                normalizedRequestId);
            var sourceKey = string.Join("|", localHelperAddress.Value, sourceIdentity);
            var churnKey = string.Join("|", sourceKey, ComputeInviteTokenFingerprint(inviteToken));

            lock (gate)
            {
                PruneExactRequests(nowTicks);
                if (recentExactRequests.ContainsKey(exactKey))
                {
                    return HelpRequestAdmissionDecision.DuplicateRecent;
                }

                recentExactRequests[exactKey] = nowTicks;

                var sourceQueue = GetOrCreateSourceQueue(sourceKey);
                PruneQueue(sourceQueue, nowTicks, ThrottleWindow);
                if (sourceQueue.Count >= MaxRequestsPerSourceWindow)
                {
                    return HelpRequestAdmissionDecision.SourceThrottled;
                }

                sourceQueue.Enqueue(nowTicks);

                var requestIds = GetOrCreateRequestIds(churnKey);
                PruneRequestIds(requestIds, nowTicks);
                if (!requestIds.ContainsKey(normalizedRequestId) &&
                    requestIds.Count >= MaxDistinctRequestIdsPerInviteWindow)
                {
                    return HelpRequestAdmissionDecision.RequestChurnThrottled;
                }

                requestIds[normalizedRequestId] = nowTicks;
                return HelpRequestAdmissionDecision.Accepted;
            }
        }

        private Queue<long> GetOrCreateSourceQueue(string sourceKey)
        {
            if (!sourceRequests.TryGetValue(sourceKey, out var queue))
            {
                queue = new Queue<long>();
                sourceRequests[sourceKey] = queue;
            }

            return queue;
        }

        private Dictionary<string, long> GetOrCreateRequestIds(string churnKey)
        {
            if (!requestIdsByInvite.TryGetValue(churnKey, out var requestIds))
            {
                requestIds = new Dictionary<string, long>(StringComparer.Ordinal);
                requestIdsByInvite[churnKey] = requestIds;
            }

            return requestIds;
        }

        private void PruneExactRequests(long nowTicks)
        {
            var cutoffTicks = nowTicks - DuplicateWindow.Ticks;
            foreach (var pair in recentExactRequests.ToArray())
            {
                if (pair.Value < cutoffTicks)
                {
                    recentExactRequests.Remove(pair.Key);
                }
            }

            if (recentExactRequests.Count <= MaxRecentDuplicateKeys)
            {
                return;
            }

            foreach (var pair in recentExactRequests.OrderBy(static pair => pair.Value).Take(recentExactRequests.Count - MaxRecentDuplicateKeys).ToArray())
            {
                recentExactRequests.Remove(pair.Key);
            }
        }

        private void PruneRequestIds(Dictionary<string, long> requestIds, long nowTicks)
        {
            var cutoffTicks = nowTicks - ThrottleWindow.Ticks;
            foreach (var pair in requestIds.ToArray())
            {
                if (pair.Value < cutoffTicks)
                {
                    requestIds.Remove(pair.Key);
                }
            }
        }

        private static void PruneQueue(Queue<long> queue, long nowTicks, TimeSpan window)
        {
            var cutoffTicks = nowTicks - window.Ticks;
            while (queue.Count > 0 && queue.Peek() < cutoffTicks)
            {
                queue.Dequeue();
            }
        }

        private static string NormalizeSourceIdentity(string? source, PeerAddress helpeeAddress) =>
            string.IsNullOrWhiteSpace(source) ? helpeeAddress.Value : source.Trim();

        private static string ComputeInviteTokenFingerprint(string inviteToken)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(inviteToken.Trim()));
            return Convert.ToHexString(hash.AsSpan(0, 16));
        }
    }

    private sealed class PendingAckWait
    {
        private int completionState;

        public PendingAckWait(string destination, MsgType type, TaskCompletionSource<AckWaitOutcome> completion)
        {
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
            Type = type;
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        public string Destination { get; }
        public MsgType Type { get; }
        public TaskCompletionSource<AckWaitOutcome> Completion { get; }
        public string? CompletionReason { get; private set; }

        public bool TryComplete(AckWaitOutcome outcome, string? reason)
        {
            if (Interlocked.CompareExchange(ref completionState, (int)outcome, (int)AckWaitOutcome.Pending) != (int)AckWaitOutcome.Pending)
            {
                return false;
            }

            CompletionReason = reason;
            return Completion.TrySetResult(outcome);
        }

        public void TryCancel()
        {
            if (Interlocked.CompareExchange(ref completionState, (int)AckWaitOutcome.Canceled, (int)AckWaitOutcome.Pending) != (int)AckWaitOutcome.Pending)
            {
                return;
            }

            Completion.TrySetCanceled();
        }
    }

    private enum AckWaitOutcome
    {
        Pending = 0,
        Acknowledged = 1,
        Superseded = 2,
        Canceled = 3,
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
