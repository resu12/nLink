using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private const int TunaSidecarProtocolVersion = 1;
    private const int AccelerationNegotiationMaxRetryAttempts = 3;
    private static readonly TimeSpan AccelerationOfferLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AccelerationOfferAnswerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccelerationNegotiationRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HelperPaidOfferHelpeePriorityDelay = TimeSpan.FromSeconds(10);
    internal static TimeSpan? AccelerationOfferAnswerTimeoutOverrideForTests;
    internal static TimeSpan? HelperPaidOfferHelpeePriorityDelayOverrideForTests;
    private readonly object accelerationGate = new();
    private string? outboundAccelerationOfferNonce;
    private string? accelerationSessionId;
    private NknAccelerationLaneKind accelerationNegotiatedLanes;
    private int accelerationNegotiationScheduled;
    private int accelerationNegotiationRetryAttempts;
    private int helperPaidOfferPriorityDelayConsumed;
    private int remoteHelpeeAccelerationOfferObserved;
    private int transportAccelerationActivePublished;
    private string transportAccelerationStatusReason = "inactive";

    private readonly record struct AccelerationValidationResult(
        bool IsHardReject,
        string? Reason,
        NknAccelerationLaneKind AcceptedLanes)
    {
        public bool IsValid => Reason is null;

        public static AccelerationValidationResult Valid(NknAccelerationLaneKind acceptedLanes)
            => new(false, null, acceptedLanes);

        public static AccelerationValidationResult HardReject(string reason)
            => new(true, reason, NknAccelerationLaneKind.None);

        public static AccelerationValidationResult SoftReject(string reason)
            => new(false, reason, NknAccelerationLaneKind.None);
    }

    internal bool IsAccelerationAvailableForTests => IsAccelerationNegotiatedAndHealthy();

    internal bool HasAccelerationLaneForTests => accelerationLane is not null;

    internal bool AccelerationCanOfferListenerForTests
        => accelerationLane is INknTunaAccelerationSession tunaSession && tunaSession.CanOfferListener;

    internal NknAccelerationLaneKind AccelerationNegotiatedLanesForTests
    {
        get
        {
            lock (accelerationGate)
            {
                return accelerationNegotiatedLanes;
            }
        }
    }

    internal NknAccelerationLaneDiagnostics AccelerationDiagnosticsForTests
        => accelerationLane?.GetDiagnosticsSnapshot() ?? NknAccelerationLaneDiagnostics.Empty;

    internal void SetAccelerationAcceptedForTests(NknAccelerationLaneKind lanes, string? sessionId = null)
    {
        lock (accelerationGate)
        {
            accelerationSessionId = string.IsNullOrWhiteSpace(sessionId)
                ? currentSessionSecurityState.SessionId?.Value
                : sessionId.Trim();
            accelerationNegotiatedLanes = lanes;
        }

        NotifyTransportAccelerationStateChanged("test_accept");
    }

    private static INknAccelerationLane? CreateAccelerationLane(
        NknTunaAccelerationOptions options,
        INknTunaListenerSidecarSupervisor? listenerSupervisor = null)
    {
        if (options is null || !options.Enabled)
        {
            return null;
        }

        return new NknTunaAccelerationLane(options, listenerSupervisor);
    }

    private void OnAccelerationStateChanged(object? sender, AccelerationStateChangedEventArgs e)
    {
        var reason = SanitizeLogToken(e.Reason);
        var downSessionId = string.Empty;
        var downLanes = NknAccelerationLaneKind.None;
        var shouldNotifyRemoteDown = !e.IsAvailable &&
                                     ShouldNotifyRemoteAccelerationDown(e.Reason) &&
                                     TryCaptureAccelerationNegotiation(out downSessionId, out downLanes);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_state_changed; available={(e.IsAvailable ? 1 : 0)}; reason={reason}");
        if (!e.IsAvailable)
        {
            ResetAccelerationNegotiation($"sidecar_{e.Reason}");
            if (shouldNotifyRemoteDown)
            {
                ScheduleAccelerationDownNotification(downSessionId, downLanes, reason);
            }

            return;
        }

        NotifyTransportAccelerationStateChanged(e.Reason);
    }

    private void OnAccelerationMessageReceived(object? sender, NknIncomingMessage e)
    {
        if (disposed || e.Payload.Length == 0)
        {
            return;
        }

        var source = ResolveSyntheticAccelerationSource(e.Channel);
        if (string.IsNullOrWhiteSpace(source))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("tuna_source_unavailable");
            LocalOperationalLog.Warn("NKN.Tuna", $"event=tuna_accelerated_frame_rejected; reason=source_unavailable; channel={MapBridgeChannel(e.Channel)}");
            return;
        }

        OnClientMessageReceived(
            sender,
            new NknIncomingMessage(
                source,
                e.Payload,
                isTopic: false,
                topic: null,
                channel: e.Channel,
                bridgeIngressObservedUtcMs: e.BridgeIngressObservedUtcMs,
                bridgeMessageObservedUtcMs: e.BridgeMessageObservedUtcMs,
                binaryFrameDecodedUtcMs: e.BinaryFrameDecodedUtcMs,
                socketDataEventEmittedUtcMs: e.SocketDataEventEmittedUtcMs,
                wsReceiverWriteEnteredUtcMs: e.WsReceiverWriteEnteredUtcMs,
                wsMessageEmittedUtcMs: e.WsMessageEmittedUtcMs,
                sdkHandleMsgEnteredUtcMs: e.SdkHandleMsgEnteredUtcMs,
                clientMessageDispatchUtcMs: e.ClientMessageDispatchUtcMs,
                multiClientMessageDispatchUtcMs: e.MultiClientMessageDispatchUtcMs));
    }

    private string? ResolveSyntheticAccelerationSource(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => ResolveExpectedRemoteMediaPeerAddressForCurrentSession(),
            NknBridgeChannel.Bulk => ResolveExpectedRemoteBulkPeerAddressForCurrentSession(),
            _ => ResolveExpectedRemotePeerAddressForCurrentSession(),
        };

    private void ScheduleAccelerationNegotiationIfEligible(string reason)
    {
        if (disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            !IsSessionAccelerationEligible(out _))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref accelerationNegotiationScheduled, 1, 0) != 0)
        {
            return;
        }

        NotifyTransportAccelerationStateChanged($"negotiation_scheduled_{reason}");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await TrySendAccelerationOfferAsync(reason, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
                }
            },
            CancellationToken.None);
    }

    private async Task TrySendAccelerationOfferAsync(string reason, CancellationToken ct)
    {
        if (accelerationLane is not INknTunaAccelerationSession tunaSession ||
            string.IsNullOrWhiteSpace(remoteEndpoint) ||
            !IsSessionAccelerationEligible(out var eligibleLanes))
        {
            return;
        }

        if (!tunaSession.CanOfferListener)
        {
            return;
        }

        var localRole = ResolveLocalSessionRole();
        NotifyTransportAccelerationStateChanged("checking_payer_priority");
        if (await ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(localRole, reason, ct).ConfigureAwait(false))
        {
            return;
        }

        if (accelerationLane is not INknTunaAccelerationSession ||
            !IsSessionAccelerationEligible(out eligibleLanes))
        {
            return;
        }

        var preflightLanes = eligibleLanes & tunaSession.ConfiguredLanes;
        if (preflightLanes == NknAccelerationLaneKind.None)
        {
            NotifyTransportAccelerationStateChanged("no_eligible_lane");
            return;
        }

        NotifyTransportAccelerationStateChanged("listener_starting");
        if (!await tunaSession.EnsureListenerSidecarConnectedAsync(remoteEndpoint, ct).ConfigureAwait(false) ||
            string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress))
        {
            NotifyTransportAccelerationStateChanged("listener_sidecar_unavailable");
            ScheduleAccelerationNegotiationRetry("listener_sidecar_unavailable");
            return;
        }

        NotifyTransportAccelerationStateChanged("listener_ready");
        var offeredLanes = eligibleLanes & tunaSession.SupportedLanes;
        if (offeredLanes == NknAccelerationLaneKind.None)
        {
            NotifyTransportAccelerationStateChanged("no_supported_lane");
            ScheduleAccelerationNegotiationRetry("listener_sidecar_unavailable");
            return;
        }

        var sessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(sessionId) || !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var offer = new TransportAccelerationOfferPayload
        {
            SessionId = sessionId,
            SenderRole = localRole,
            TunaAddress = tunaSession.LocalTunaAddress,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(offeredLanes),
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.Add(AccelerationOfferLifetime).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationOffer,
            nonce,
            JsonSerializer.SerializeToUtf8Bytes(offer));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationOffer, payload, replyTo: null);
        lock (accelerationGate)
        {
            outboundAccelerationOfferNonce = nonce;
        }

        var queued = await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_{(queued ? "queued" : "rejected")}; reason={SanitizeLogToken(reason)}; session_id={sessionId}; lanes={string.Join(",", offer.SupportedLanes)}");
        if (!queued)
        {
            NotifyTransportAccelerationStateChanged("offer_queue_rejected");
            ScheduleAccelerationNegotiationRetry("offer_queue_rejected");
            return;
        }

        NotifyTransportAccelerationStateChanged("waiting_for_answer");
        ScheduleAccelerationOfferAnswerTimeout(nonce);
    }

    private void HandleTransportAccelerationOffer(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationOffer, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationOfferPayload>(securePayload.Plaintext, out var offer) ||
            offer is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_offer", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_offer", securePayload.Metadata, offer.Nonce, env.MessageId))
        {
            return;
        }

        _ = Task.Run(
            () => HandleTransportAccelerationOfferAsync(source, offer, env.MessageId, CancellationToken.None),
            CancellationToken.None);
    }

    private async Task HandleTransportAccelerationOfferAsync(
        string source,
        TransportAccelerationOfferPayload offer,
        string messageId,
        CancellationToken ct)
    {
        var validation = ValidateAccelerationOffer(source, offer);
        if (validation.IsHardReject)
        {
            RejectAccelerationEnvelope("transport_acceleration_offer", validation.Reason ?? "invalid", messageId);
            return;
        }

        ObserveRemoteOfferForPayerPriority(offer, validation);
        if (validation.IsValid && ShouldRejectRemoteHelperOfferForHelpeePriority(offer))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                "event=tuna_acceleration_offer_rejected; reason=helpee_payer_preferred; sender_role=helper");
            ScheduleAccelerationNegotiationIfEligible("helpee_payer_preferred");
            await SendAccelerationAnswerAsync(
                offer,
                accepted: false,
                lanes: NknAccelerationLaneKind.None,
                rejectReason: "helpee_payer_preferred",
                ct).ConfigureAwait(false);
            return;
        }

        var rejectReason = validation.Reason;
        if (validation.IsValid)
        {
            NotifyTransportAccelerationStateChanged("dialer_starting");
        }

        var accepted = validation.IsValid &&
                       accelerationLane is INknTunaAccelerationSession tunaSession &&
                       await tunaSession.StartDialerSidecarAsync(offer.TunaAddress, source, ct).ConfigureAwait(false);
        if (!accepted && rejectReason is null)
        {
            rejectReason = "sidecar_unavailable";
        }

        if (accepted)
        {
            NotifyTransportAccelerationStateChanged("dialer_ready");
            lock (accelerationGate)
            {
                outboundAccelerationOfferNonce = null;
                accelerationSessionId = offer.SessionId.Trim();
                accelerationNegotiatedLanes = validation.AcceptedLanes;
            }

            Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
            NotifyTransportAccelerationStateChanged("negotiated");
        }

        await SendAccelerationAnswerAsync(offer, accepted, accepted ? validation.AcceptedLanes : NknAccelerationLaneKind.None, rejectReason, ct).ConfigureAwait(false);
    }

    private async Task SendAccelerationAnswerAsync(
        TransportAccelerationOfferPayload offer,
        bool accepted,
        NknAccelerationLaneKind lanes,
        string? rejectReason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint) ||
            !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var answer = new TransportAccelerationAnswerPayload
        {
            SessionId = offer.SessionId,
            Accepted = accepted,
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            ExpiresAtUnixMs = Math.Min(offer.ExpiresAtUnixMs, DateTimeOffset.UtcNow.Add(AccelerationOfferLifetime).ToUnixTimeMilliseconds()),
            Nonce = offer.Nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
            RejectReason = accepted ? null : rejectReason,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationAnswer,
            offer.Nonce,
            JsonSerializer.SerializeToUtf8Bytes(answer));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationAnswer, payload, replyTo: null);
        await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_answer_sent; accepted={(accepted ? 1 : 0)}; reason={SanitizeLogToken(rejectReason)}; lanes={string.Join(",", answer.SupportedLanes)}");
    }

    private void HandleTransportAccelerationAnswer(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationAnswer, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationAnswerPayload>(securePayload.Plaintext, out var answer) ||
            answer is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_answer", securePayload.Metadata, answer.Nonce, env.MessageId))
        {
            return;
        }

        string? expectedNonce;
        lock (accelerationGate)
        {
            expectedNonce = outboundAccelerationOfferNonce;
        }

        if (string.IsNullOrWhiteSpace(expectedNonce) ||
            !string.Equals(expectedNonce, answer.Nonce, StringComparison.Ordinal))
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", "nonce_mismatch", env.MessageId);
            return;
        }

        var validation = ValidateAccelerationAnswer(source, answer, requireAcceptedLanes: answer.Accepted);
        if (validation.IsHardReject)
        {
            RejectAccelerationEnvelope("transport_acceleration_answer", validation.Reason ?? "invalid", env.MessageId);
            return;
        }

        if (!answer.Accepted || !validation.IsValid)
        {
            var effectiveRejectReason = !answer.Accepted
                ? answer.RejectReason ?? validation.Reason ?? "rejected"
                : validation.Reason ?? answer.RejectReason ?? "rejected";
            lock (accelerationGate)
            {
                outboundAccelerationOfferNonce = null;
            }

            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_answer_rejected; reason={SanitizeLogToken(effectiveRejectReason)}");
            NotifyTransportAccelerationStateChanged($"answer_rejected_{effectiveRejectReason}");
            if (ShouldRetryAccelerationNegotiation(effectiveRejectReason))
            {
                ScheduleAccelerationNegotiationRetry(effectiveRejectReason!);
            }

            return;
        }

        lock (accelerationGate)
        {
            outboundAccelerationOfferNonce = null;
            accelerationSessionId = answer.SessionId.Trim();
            accelerationNegotiatedLanes = validation.AcceptedLanes;
        }

        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_negotiated; session_id={answer.SessionId}; lanes={string.Join(",", answer.SupportedLanes)}");
        NotifyTransportAccelerationStateChanged("negotiated");
    }

    private void HandleTransportAccelerationDown(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.TransportAccelerationDown, out var securePayload))
        {
            return;
        }

        if (!TryDeserializeAccelerationPayload<TransportAccelerationDownPayload>(securePayload.Plaintext, out var down) ||
            down is null)
        {
            RejectAccelerationEnvelope("transport_acceleration_down", "payload_invalid", env.MessageId);
            return;
        }

        if (!TryValidateControlSecureMetadata("transport_acceleration_down", securePayload.Metadata, down.Nonce, env.MessageId))
        {
            return;
        }

        var rejectReason = ValidateAccelerationDown(source, down);
        if (rejectReason is not null)
        {
            RejectAccelerationEnvelope("transport_acceleration_down", rejectReason, env.MessageId);
            return;
        }

        if (IsAccelerationNegotiatedAndHealthy())
        {
            ResetAccelerationNegotiation($"remote_{down.Reason}");
            ScheduleAccelerationLaneStop($"remote_{down.Reason}");
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_remote_down; reason={SanitizeLogToken(down.Reason)}; lanes={string.Join(",", down.SupportedLanes)}");
    }

    private void ScheduleAccelerationNegotiationRetry(string reason)
    {
        if (disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsSessionAccelerationEligible(out _))
        {
            return;
        }

        var attempt = Interlocked.Increment(ref accelerationNegotiationRetryAttempts);
        if (attempt > AccelerationNegotiationMaxRetryAttempts)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_acceleration_retry_exhausted; reason={SanitizeLogToken(reason)}; attempts={attempt - 1}");
            return;
        }

        var delay = TimeSpan.FromMilliseconds(
            AccelerationNegotiationRetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_retry_scheduled; reason={SanitizeLogToken(reason)}; attempt={attempt}; delay_ms={(int)delay.TotalMilliseconds}");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                    ScheduleAccelerationNegotiationIfEligible($"retry_{reason}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_retry_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private void ScheduleAccelerationOfferAnswerTimeout(string nonce)
    {
        var timeout = AccelerationOfferAnswerTimeoutOverrideForTests ?? AccelerationOfferAnswerTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(timeout, CancellationToken.None).ConfigureAwait(false);
                    if (disposed ||
                        IsAccelerationNegotiatedAndHealthy() ||
                        !IsSessionAccelerationEligible(out _))
                    {
                        return;
                    }

                    lock (accelerationGate)
                    {
                        if (!string.Equals(outboundAccelerationOfferNonce, nonce, StringComparison.Ordinal))
                        {
                            return;
                        }

                        outboundAccelerationOfferNonce = null;
                    }

                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_answer_timeout; timeout_ms={(int)timeout.TotalMilliseconds}");
                    NotifyTransportAccelerationStateChanged("offer_answer_timeout");
                    ScheduleAccelerationNegotiationRetry("offer_answer_timeout");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_offer_answer_timeout_failed; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private static bool ShouldRetryAccelerationNegotiation(string? reason)
        => string.Equals(reason, "sidecar_unavailable", StringComparison.Ordinal) ||
           string.Equals(reason, "listener_sidecar_unavailable", StringComparison.Ordinal) ||
           string.Equals(reason, "offer_queue_rejected", StringComparison.Ordinal) ||
           string.Equals(reason, "offer_answer_timeout", StringComparison.Ordinal) ||
           string.Equals(reason, "session_not_eligible", StringComparison.Ordinal);

    private static bool ShouldNotifyRemoteAccelerationDown(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Trim() switch
        {
            "read_failed" or
            "write_failed" or
            "remote_closed" or
            "queue_overflow" or
            "status_timeout" or
            "invalid_status" or
            "status_parse_failed" => true,
            _ => false,
        };
    }

    private bool TryCaptureAccelerationNegotiation(out string sessionId, out NknAccelerationLaneKind lanes)
    {
        lock (accelerationGate)
        {
            sessionId = accelerationSessionId ?? string.Empty;
            lanes = accelerationNegotiatedLanes;
        }

        return lanes != NknAccelerationLaneKind.None &&
               !string.IsNullOrWhiteSpace(sessionId) &&
               string.Equals(sessionId, currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal);
    }

    private void ScheduleAccelerationDownNotification(string sessionId, NknAccelerationLaneKind lanes, string reason)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SendAccelerationDownAsync(sessionId, lanes, reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_down_notify_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private async Task SendAccelerationDownAsync(
        string sessionId,
        NknAccelerationLaneKind lanes,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !string.Equals(sessionId.Trim(), currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal) ||
            !TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var down = new TransportAccelerationDownPayload
        {
            SessionId = sessionId.Trim(),
            SupportedLanes = NknAccelerationLaneCodec.ToNames(lanes),
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = TunaSidecarProtocolVersion,
            Reason = reason,
        };
        var payload = CreateSecureControlPayload(
            MsgType.TransportAccelerationDown,
            nonce,
            JsonSerializer.SerializeToUtf8Bytes(down));
        var envelope = CreateEnvelope(envelopeCode, MsgType.TransportAccelerationDown, payload, replyTo: null);
        var queued = await QueueControlEnvelopeAsync(remoteEndpoint, envelope, ControlOutboundLane.High, ct).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_down_notify_{(queued ? "queued" : "rejected")}; reason={SanitizeLogToken(reason)}; lanes={string.Join(",", down.SupportedLanes)}");
    }

    private AccelerationValidationResult ValidateAccelerationOffer(
        string source,
        TransportAccelerationOfferPayload offer)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(offer.SessionId) ||
            !string.Equals(offer.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(offer.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (offer.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("unsupported_version");
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= offer.ExpiresAtUnixMs)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        if (string.IsNullOrWhiteSpace(offer.TunaAddress))
        {
            return AccelerationValidationResult.HardReject("missing_tuna_address");
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(offer.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes);
    }

    private AccelerationValidationResult ValidateAccelerationAnswer(
        string source,
        TransportAccelerationAnswerPayload answer,
        bool requireAcceptedLanes)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            return AccelerationValidationResult.HardReject("session_not_eligible");
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(answer.SessionId) ||
            !string.Equals(answer.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return AccelerationValidationResult.HardReject("session_id_mismatch");
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return AccelerationValidationResult.HardReject("source_identity_mismatch");
        }

        if (!IsAccelerationNonceValid(answer.Nonce))
        {
            return AccelerationValidationResult.HardReject("nonce_invalid");
        }

        if (answer.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return AccelerationValidationResult.HardReject("unsupported_version");
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= answer.ExpiresAtUnixMs)
        {
            return AccelerationValidationResult.HardReject("expired");
        }

        if (!requireAcceptedLanes)
        {
            return AccelerationValidationResult.Valid(NknAccelerationLaneKind.None);
        }

        var acceptedLanes = NknAccelerationLaneCodec.FromNames(answer.SupportedLanes) & capabilityLanes & ResolveConfiguredAccelerationLanes();
        return acceptedLanes == NknAccelerationLaneKind.None
            ? AccelerationValidationResult.SoftReject("unsupported_lane")
            : AccelerationValidationResult.Valid(acceptedLanes);
    }

    private string? ValidateAccelerationDown(string source, TransportAccelerationDownPayload down)
    {
        if (!IsSessionAccelerationEligible(out _))
        {
            return "session_not_eligible";
        }

        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        if (string.IsNullOrWhiteSpace(down.SessionId) ||
            !string.Equals(down.SessionId.Trim(), expectedSessionId, StringComparison.Ordinal))
        {
            return "session_id_mismatch";
        }

        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        if (!AddressMatchesForSessionPolicy(source, expectedSource))
        {
            return "source_identity_mismatch";
        }

        if (down.SidecarProtocolVersion != TunaSidecarProtocolVersion)
        {
            return "unsupported_version";
        }

        if (!IsAccelerationNonceValid(down.Nonce))
        {
            return "nonce_invalid";
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (down.SentAtUnixMs <= 0 ||
            down.SentAtUnixMs > nowMs + TimeSpan.FromSeconds(30).TotalMilliseconds ||
            nowMs - down.SentAtUnixMs > AccelerationOfferLifetime.TotalMilliseconds)
        {
            return "stale";
        }

        return null;
    }

    private bool IsSessionAccelerationEligible(out NknAccelerationLaneKind eligibleLanes)
    {
        if (!TryGetAccelerationSessionCapabilityLanes(out var capabilityLanes))
        {
            eligibleLanes = NknAccelerationLaneKind.None;
            return false;
        }

        eligibleLanes = capabilityLanes & ResolveConfiguredAccelerationLanes();
        return eligibleLanes != NknAccelerationLaneKind.None;
    }

    private bool TryGetAccelerationSessionCapabilityLanes(out NknAccelerationLaneKind eligibleLanes)
    {
        eligibleLanes = NknAccelerationLaneKind.None;
        var state = currentSessionSecurityState;
        var nowUtc = DateTimeOffset.UtcNow;
        if (!state.InviteValidated ||
            !state.HandshakeCompleted ||
            state.HandshakeState != SessionHandshakeState.Verified ||
            !state.IsApprovalActive(nowUtc) ||
            state.SessionId is null)
        {
            return false;
        }

        if (state.HasCapability(CapabilityGrant.FileTransfer, nowUtc))
        {
            eligibleLanes |= NknAccelerationLaneKind.File;
        }

        if (state.HasCapability(CapabilityGrant.ScreenShare, nowUtc))
        {
            eligibleLanes |= NknAccelerationLaneKind.Screen;
        }

        return eligibleLanes != NknAccelerationLaneKind.None;
    }

    private static bool IsAccelerationNonceValid(string? nonce)
    {
        var trimmed = string.IsNullOrWhiteSpace(nonce) ? string.Empty : nonce.Trim();
        return trimmed.Length is > 0 and <= 128;
    }

    private NknAccelerationLaneKind ResolveConfiguredAccelerationLanes()
        => tunaAccelerationOptions.Enabled
            ? tunaAccelerationOptions.Lanes
            : accelerationLane is null
                ? NknAccelerationLaneKind.None
                : NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;

    private bool IsAccelerationNegotiatedAndHealthy()
    {
        lock (accelerationGate)
        {
            return accelerationLane?.IsAvailable == true &&
                   accelerationNegotiatedLanes != NknAccelerationLaneKind.None &&
                   !string.IsNullOrWhiteSpace(accelerationSessionId) &&
                   string.Equals(accelerationSessionId, currentSessionSecurityState.SessionId?.Value, StringComparison.Ordinal);
        }
    }

    private async Task<bool> TrySendAcceleratedEnvelopeAsync(
        MsgType messageType,
        NknBridgeChannel channel,
        byte[] envelopeBytes,
        CancellationToken ct)
    {
        var lane = messageType switch
        {
            MsgType.ScreenShareFrame when channel == NknBridgeChannel.Media => NknAccelerationLaneKind.Screen,
            MsgType.FileTransferDataFrame when channel == NknBridgeChannel.Bulk => NknAccelerationLaneKind.File,
            _ => NknAccelerationLaneKind.None,
        };
        if (lane == NknAccelerationLaneKind.None ||
            accelerationLane is null ||
            !IsAccelerationNegotiatedAndHealthy())
        {
            return false;
        }

        lock (accelerationGate)
        {
            if ((accelerationNegotiatedLanes & lane) != lane)
            {
                return false;
            }
        }

        try
        {
            var sent = await accelerationLane.TrySendAsync(channel, envelopeBytes, ct).ConfigureAwait(false);
            if (sent)
            {
                LocalOperationalLog.Info(
                    "NKN.Tuna",
                    $"event=tuna_accelerated_envelope_sent; message_type={MapEnvelopeTypeForDiagnostics(messageType)}; channel={MapBridgeChannel(channel)}; payload_bytes={envelopeBytes.Length}");
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "NKN.Tuna",
                $"event=tuna_accelerated_envelope_send_failed; message_type={MapEnvelopeTypeForDiagnostics(messageType)}; channel={MapBridgeChannel(channel)}; error={ex.GetType().Name}");
        }

        return false;
    }

    public Task RequestAccelerationNegotiationAsync(string reason, CancellationToken ct)
    {
        if (ct.IsCancellationRequested ||
            disposed ||
            accelerationLane is not INknTunaAccelerationSession ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsSessionAccelerationEligible(out _))
        {
            return Task.CompletedTask;
        }

        lock (accelerationGate)
        {
            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce))
            {
                return Task.CompletedTask;
            }
        }

        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        ScheduleAccelerationNegotiationIfEligible(string.IsNullOrWhiteSpace(reason)
            ? "runtime_unlock"
            : reason.Trim());
        return Task.CompletedTask;
    }

    public async Task StopAccelerationAsync(string reason, CancellationToken ct)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "user_locked" : SanitizeLogToken(reason);
        var shouldNotifyRemoteDown = TryCaptureAccelerationNegotiation(out var downSessionId, out var downLanes);
        ResetAccelerationNegotiation(normalizedReason);
        if (accelerationLane is INknTunaAccelerationSession tunaSession)
        {
            try
            {
                await tunaSession.StopAsync(normalizedReason, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocalOperationalLog.Warn(
                    "NKN.Tuna",
                    $"event=tuna_acceleration_stop_failed; reason={normalizedReason}; error={ex.GetType().Name}");
            }
        }

        if (shouldNotifyRemoteDown)
        {
            await SendAccelerationDownAsync(downSessionId, downLanes, normalizedReason, ct).ConfigureAwait(false);
        }
    }

    private void ScheduleAccelerationLaneStop(string reason)
    {
        if (accelerationLane is not INknTunaAccelerationSession tunaSession)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await tunaSession.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "NKN.Tuna",
                        $"event=tuna_acceleration_stop_failed; reason={SanitizeLogToken(reason)}; error={ex.GetType().Name}");
                }
            },
            CancellationToken.None);
    }

    private void ResetAccelerationNegotiation(string reason)
    {
        lock (accelerationGate)
        {
            outboundAccelerationOfferNonce = null;
            accelerationSessionId = null;
            accelerationNegotiatedLanes = NknAccelerationLaneKind.None;
        }

        Interlocked.Exchange(ref accelerationNegotiationScheduled, 0);
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        Interlocked.Exchange(ref helperPaidOfferPriorityDelayConsumed, 0);
        Interlocked.Exchange(ref remoteHelpeeAccelerationOfferObserved, 0);
        LocalOperationalLog.Info("NKN.Tuna", $"event=tuna_acceleration_reset; reason={SanitizeLogToken(reason)}");
        NotifyTransportAccelerationStateChanged(reason);
    }

    private void NotifyTransportAccelerationStateChanged(string reason)
    {
        var active = IsAccelerationNegotiatedAndHealthy();
        var activeValue = active ? 1 : 0;
        var previousActiveValue = Interlocked.Exchange(ref transportAccelerationActivePublished, activeValue);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : SanitizeLogToken(reason);
        var reasonChanged = false;

        lock (accelerationGate)
        {
            if (!string.Equals(transportAccelerationStatusReason, normalizedReason, StringComparison.Ordinal))
            {
                transportAccelerationStatusReason = normalizedReason;
                reasonChanged = true;
            }
        }

        if (previousActiveValue == activeValue && !reasonChanged)
        {
            return;
        }

        TransportAccelerationStateChanged?.Invoke(
            this,
            new TransportAccelerationStateChangedEventArgs(active, normalizedReason));
    }

    private async Task<bool> ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(
        string localRole,
        string reason,
        CancellationToken ct)
    {
        if (!IsHelperSessionRole(localRole))
        {
            return false;
        }

        if (Volatile.Read(ref remoteHelpeeAccelerationOfferObserved) != 0 || IsAccelerationNegotiatedAndHealthy())
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_suppressed; reason=helpee_payer_priority; role=helper; trigger={SanitizeLogToken(reason)}");
            return true;
        }

        if (Interlocked.Exchange(ref helperPaidOfferPriorityDelayConsumed, 1) != 0)
        {
            return false;
        }

        var delay = HelperPaidOfferHelpeePriorityDelayOverrideForTests ?? HelperPaidOfferHelpeePriorityDelay;
        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "NKN.Tuna",
            $"event=tuna_acceleration_offer_deferred; reason=helpee_payer_priority; role=helper; delay_ms={(int)delay.TotalMilliseconds}; trigger={SanitizeLogToken(reason)}");
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }

        if (disposed ||
            Volatile.Read(ref remoteHelpeeAccelerationOfferObserved) != 0 ||
            IsAccelerationNegotiatedAndHealthy() ||
            !IsSessionAccelerationEligible(out _))
        {
            LocalOperationalLog.Info(
                "NKN.Tuna",
                $"event=tuna_acceleration_offer_suppressed; reason=helpee_payer_priority; role=helper; trigger={SanitizeLogToken(reason)}");
            return true;
        }

        return false;
    }

    private void ObserveRemoteOfferForPayerPriority(
        TransportAccelerationOfferPayload offer,
        AccelerationValidationResult validation)
    {
        if (validation.IsHardReject ||
            !validation.IsValid ||
            !IsHelperSessionRole(ResolveLocalSessionRole()) ||
            !IsHelpeeSessionRole(offer.SenderRole))
        {
            return;
        }

        Interlocked.Exchange(ref remoteHelpeeAccelerationOfferObserved, 1);
    }

    private bool ShouldRejectRemoteHelperOfferForHelpeePriority(TransportAccelerationOfferPayload offer)
    {
        if (!IsHelpeeSessionRole(ResolveLocalSessionRole()) ||
            !IsHelperSessionRole(offer.SenderRole) ||
            accelerationLane is not INknTunaAccelerationSession tunaSession ||
            !tunaSession.CanOfferListener ||
            string.IsNullOrWhiteSpace(tunaSession.LocalTunaAddress))
        {
            return false;
        }

        return true;
    }

    private string ResolveLocalSessionRole()
    {
        var local = LocalPeerAddress;
        if (currentSessionSecurityState.HelpeeAddress is PeerAddress helpee &&
            AddressesLikelySamePeer(local, helpee.Value))
        {
            return "helpee";
        }

        if (currentSessionSecurityState.HelperAddress is PeerAddress helper &&
            AddressesLikelySamePeer(local, helper.Value))
        {
            return "helper";
        }

        return "unknown";
    }

    private static bool IsHelperSessionRole(string? role)
        => string.Equals(role?.Trim(), "helper", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpeeSessionRole(string? role)
        => string.Equals(role?.Trim(), "helpee", StringComparison.OrdinalIgnoreCase);

    private static bool TryDeserializeAccelerationPayload<T>(byte[] payload, out T? value)
    {
        value = default;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RejectAccelerationEnvelope(string messageType, string reason, string messageId)
    {
        NknRuntimeDiagnostics.SetLastError($"{messageType}_{reason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{reason}");
        LocalOperationalLog.Warn(
            "NKN.Tuna",
            $"event=tuna_acceleration_message_rejected; message_type={messageType}; reason={reason}; msg_id={messageId}");
    }

    private static string SanitizeLogToken(string? value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
