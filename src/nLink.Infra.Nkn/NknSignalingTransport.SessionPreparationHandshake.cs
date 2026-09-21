using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private async Task ProcessSessionPreparationMessageAsync(SessionPreparationInbound message)
    {
        try
        {
            if (message.Type == MsgType.SessionPreparationOffer)
            {
                if (!IsSessionPreparationInputLifetimeCurrent(message.Attachment, message.Epoch) ||
                    !TryAcquireSessionPreparationNegotiationSlot(out var slot)) return;
                // Authentication is only a request: the receiving service must
                // independently authorize this exact episode before any dial.
                try
                {
                    var decision = message.Attachment.Authority.Begin(message.Attachment, message.Epoch,
                        message.Nonce, message.PayerDecisionId, localIsPayer: false, message.DeadlineUtc);
                    if (decision.Permit is { } permit &&
                        await StartSessionPreparationHandshakeAsync(permit, message, CancellationToken.None).ConfigureAwait(false)) return;
                }
                finally
                {
                    lock (accelerationGate)
                        _ = TryReleaseAccelerationNegotiationSlotUnsafe(slot, AccelerationNegotiationSlotOwner.SessionPreparation);
                }
            }
            else
            {
                PreparationLaneWork? work;
                lock (preparationExecutionGate) work = preparationLaneWork;
                // A rejected answer may terminate its exact episode, but never
                // supply readiness. Other-episode/type replies are diagnostic only.
                if (work is not null && IsPreparationLaneWorkCurrent(work) &&
                    IsSessionPreparationReplyCurrent(message with { Accepted = true }, work.Execution.Permit))
                {
                    lock (work.Gate)
                    {
                        if (work.HandshakeStarted) work.Reply.TrySetResult(message);
                    }
                    return;
                }
            }
            LocalOperationalLog.Info("NKN.Tuna",
                $"event=session_preparation_message_rejected; message_type={MapSecureControlMessageType(message.Type)}; reason=execution_unavailable; authority_effect=none");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna",
                $"event=session_preparation_message_failed; error={ex.GetType().Name}; authority_effect=none");
        }
    }

    // Both public selection and authenticated offers use this isolated executor.
    // No ordinary negotiation state is installed.
    internal async Task<bool> StartSessionPreparationHandshakeAsync(FileTransferSessionPreparationPermit permit,
        SessionPreparationInbound? offer, CancellationToken ct)
    {
        var lease = await StartSessionPreparationLaneExecutionAsync(permit, offer, ct).ConfigureAwait(false);
        if (lease is null) return false;
        PreparationLaneWork? work;
        lock (preparationExecutionGate) work = preparationLaneWork;
        if (work is null || !ReferenceEquals(work.Execution.Permit, permit) || !IsPreparationLaneWorkCurrent(work)) return false;
        var start = false;
        lock (work.Gate)
        {
            if (!work.HandshakeStarted)
            {
                work.HandshakeStarted = true;
                start = true;
            }
        }
        if (start) _ = RunSessionPreparationHandshakeAsync(work, lease, ct);
        return await work.Handshake.Task.ConfigureAwait(false);
    }

    private async Task RunSessionPreparationHandshakeAsync(PreparationLaneWork work, NknTunaLaneLease lease, CancellationToken ct)
    {
        using var cancellation = ct.Register(work.Execution.Retire);
        var completed = false;
        try
        {
            var permit = work.Execution.Permit;
            if (!IsPreparationLeaseCurrent(work, lease)) return;
            if (permit.LocalIsPayer)
            {
                var address = work.Lane.LocalTunaAddress;
                if (string.IsNullOrWhiteSpace(address)) return;
                if (!await SendSessionPreparationAsync(work, MsgType.SessionPreparationOffer,
                    new TransportAccelerationOfferPayload
                    {
                        SessionId = permit.Scope.SessionId, Nonce = permit.Nonce, PayerDecisionId = permit.PayerDecisionId,
                        ExpiresAtUnixMs = permit.DeadlineUtc.ToUnixTimeMilliseconds(), SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SenderRole = ResolveLocalSessionRole(), Trigger = "session_preparation", TunaAddress = address,
                        SidecarProtocolVersion = TunaSidecarProtocolVersion, SupportedLanes = ["file"],
                    }).ConfigureAwait(false)) return;
            }
            else if (!await SendSessionPreparationAsync(work, MsgType.SessionPreparationAnswer,
                new TransportAccelerationAnswerPayload
                {
                    SessionId = permit.Scope.SessionId, Nonce = permit.Nonce, PayerDecisionId = permit.PayerDecisionId,
                    ExpiresAtUnixMs = permit.DeadlineUtc.ToUnixTimeMilliseconds(), Accepted = true,
                    SidecarProtocolVersion = TunaSidecarProtocolVersion, SupportedLanes = ["file"],
                }).ConfigureAwait(false)) return;

            var reply = await work.Reply.Task.WaitAsync(work.Execution.Cancellation).ConfigureAwait(false);
            if (!IsSessionPreparationReplyCurrent(reply, permit) || !IsPreparationLeaseCurrent(work, lease)) return;
            if (permit.LocalIsPayer && !await SendSessionPreparationAsync(work, MsgType.SessionPreparationAnswerAck,
                new TransportAccelerationAnswerAckPayload
                {
                    SessionId = permit.Scope.SessionId, Nonce = permit.Nonce, PayerDecisionId = permit.PayerDecisionId,
                    ExpiresAtUnixMs = permit.DeadlineUtc.ToUnixTimeMilliseconds(), SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Accepted = true, SidecarProtocolVersion = TunaSidecarProtocolVersion, SupportedLanes = ["file"],
                }).ConfigureAwait(false)) return;

            if (!IsPreparationLeaseCurrent(work, lease)) return;
            var runId = permit.LocalIsPayer ? lease.Listener?.ProcessRunId : lease.DialerOwner?.RunId;
            if (string.IsNullOrWhiteSpace(runId)) return;
            var proof = new FileTransferSessionPreparationProof(permit.Nonce, permit.PayerDecisionId,
                permit.Generation, permit.Generation, lease.ClientGeneration, runId,
                LeaseCurrent: true, ListenerReady: true, FileLaneNegotiated: true,
                AnswerReceived: permit.LocalIsPayer, AnswerAckDispatched: permit.LocalIsPayer,
                AnswerAckReceived: !permit.LocalIsPayer, DateTimeOffset.UtcNow);
            var decision = work.Attachment.Authority.Complete(work.Attachment, work.Epoch, permit, proof);
            completed = decision.Status is FileTransferSessionPreparationStatus.Accepted or FileTransferSessionPreparationStatus.Idempotent;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna",
                $"event=session_preparation_handshake_failed; error={ex.GetType().Name}; authority_effect=none");
        }
        finally
        {
            if (!completed) work.Execution.Retire();
            work.Handshake.TrySetResult(completed);
        }
    }

    private bool IsPreparationLeaseCurrent(PreparationLaneWork work, NknTunaLaneLease lease)
        => IsPreparationLaneWorkCurrent(work) && work.Lane.CaptureCurrentLaneLease() == lease &&
            work.Lane.IsAvailable && (work.Lane.SupportedLanes & NknAccelerationLaneKind.File) != 0;

    private async Task<bool> SendSessionPreparationAsync<T>(PreparationLaneWork work, MsgType type, T payload)
    {
        if (!IsPreparationLaneWorkCurrent(work)) return false;
        Envelope envelope;
        string? destination;
        lock (controlSecureStateGate)
        {
            if (!IsSessionPreparationInputLifetimeCurrent(work.Attachment, work.Epoch) ||
                !work.Execution.AllowsPreparation || string.IsNullOrWhiteSpace(currentEnvelopeCode)) return false;
            destination = ResolveExpectedRemotePeerAddressForCurrentSession();
            if (string.IsNullOrWhiteSpace(destination)) return false;
            envelope = CreateEnvelope(currentEnvelopeCode, type,
                CreateSecureControlPayload(type, work.Execution.Permit.Nonce, JsonSerializer.SerializeToUtf8Bytes(payload)), null);
        }
        // This queue completes on actual NKN send, not enqueue. Its token is
        // synchronously canceled by pending admission/key/lifecycle retirement.
        return await QueueControlEnvelopeAsync(destination, envelope, ControlOutboundLane.High,
            work.Execution.Cancellation).ConfigureAwait(false) && IsPreparationLaneWorkCurrent(work);
    }
}
