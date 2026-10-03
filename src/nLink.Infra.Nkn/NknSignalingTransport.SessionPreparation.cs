using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport : IFileTransferSessionPreparationTransport
{
    private readonly FileTransferSessionPreparationLifecycle sessionPreparationLifecycle = new();

    FileTransferSessionPreparationLifecycle IFileTransferSessionPreparationTransport.SessionPreparationLifecycle
        => sessionPreparationLifecycle;

    private static bool IsSessionPreparationMessageType(MsgType type)
        => type is MsgType.SessionPreparationOffer or MsgType.SessionPreparationAnswer or
            MsgType.SessionPreparationAnswerAck;

    private void HandleSessionPreparationEnvelope(string source, Envelope envelope)
    {
        if (!TryReadSessionPreparationEnvelope(source, envelope, out var message)) return;
        _ = ProcessSessionPreparationMessageAsync(message!);
    }

    // A successfully read message is authenticated input, NOT a preparation
    // permit or readiness proof. Freeze its purpose and local security lifetime
    // before any future queued continuation can inspect it.
    internal bool TryReadSessionPreparationEnvelope(string source, Envelope envelope,
        out SessionPreparationInbound? message)
    {
        message = null;
        var epoch = sessionPreparationLifecycle.Current;
        var attachment = sessionPreparationLifecycle.Attachment;
        if (!IsSessionPreparationMessageType(envelope.Type) || epoch is null || attachment is null ||
            !IsSessionPreparationInputLifetimeCurrent(attachment, epoch) ||
            !AddressMatchesForSessionPolicy(source, ResolveExpectedRemotePeerAddressForCurrentSession())) return false;

        SessionSecureEnvelopePayload secure;
        lock (controlSecureStateGate)
        {
            // Key installation/reset uses this same gate. An old-key message
            // must not update the successor key's replay window. The decrypt
            // helper makes no liveness/Core callback for preparation types.
            if (!IsSessionPreparationInputLifetimeCurrent(attachment, epoch) ||
                !TryDecryptControlPayload(source, envelope, envelope.Type, out secure)) return false;
        }

        string sessionId, nonce;
        long payerDecisionId, expiresMs;
        long? sentMs = null;
        int protocol;
        string[] lanes;
        string? senderRole = null, tunaAddress = null;
        var accepted = true;
        switch (envelope.Type)
        {
            case MsgType.SessionPreparationOffer:
                if (!TryDeserializeAccelerationPayload<TransportAccelerationOfferPayload>(secure.Plaintext, out var offer) ||
                    offer is null || offer.Trigger != "session_preparation" ||
                    string.IsNullOrWhiteSpace(offer.TunaAddress) ||
                    !(IsHelperSessionRole(ResolveLocalSessionRole()) && IsHelpeeSessionRole(offer.SenderRole) ||
                      IsHelpeeSessionRole(ResolveLocalSessionRole()) && IsHelperSessionRole(offer.SenderRole))) return false;
                (sessionId, nonce, payerDecisionId, expiresMs, protocol, lanes) =
                    (offer.SessionId, offer.Nonce, offer.PayerDecisionId, offer.ExpiresAtUnixMs,
                        offer.SidecarProtocolVersion, offer.SupportedLanes);
                (sentMs, senderRole, tunaAddress) = (offer.SentAtUnixMs, offer.SenderRole, offer.TunaAddress);
                break;
            case MsgType.SessionPreparationAnswer:
                if (!TryDeserializeAccelerationPayload<TransportAccelerationAnswerPayload>(secure.Plaintext, out var answer) ||
                    answer is null) return false;
                (sessionId, nonce, payerDecisionId, expiresMs, protocol, lanes, accepted) =
                    (answer.SessionId, answer.Nonce, answer.PayerDecisionId, answer.ExpiresAtUnixMs,
                        answer.SidecarProtocolVersion, answer.SupportedLanes, answer.Accepted);
                break;
            case MsgType.SessionPreparationAnswerAck:
                if (!TryDeserializeAccelerationPayload<TransportAccelerationAnswerAckPayload>(secure.Plaintext, out var ack) ||
                    ack is null || !ack.Accepted) return false;
                (sessionId, nonce, payerDecisionId, expiresMs, protocol, lanes, sentMs) =
                    (ack.SessionId, ack.Nonce, ack.PayerDecisionId, ack.ExpiresAtUnixMs,
                        ack.SidecarProtocolVersion, ack.SupportedLanes, ack.SentAtUnixMs);
                break;
            default: return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sessionId != epoch.SessionId || !IsAccelerationNonceValid(nonce) ||
            nonce != secure.Metadata.RequestId || payerDecisionId <= 0 || protocol != TunaSidecarProtocolVersion ||
            lanes is not ["file"] || expiresMs <= nowMs || expiresMs > nowMs + 60_000 ||
            expiresMs > epoch.Security!.ApprovalExpiresAt!.Value.ToUnixTimeMilliseconds() ||
            sentMs is { } sent && (sent <= 0 || sent > nowMs + 30_000 || sent >= expiresMs || expiresMs - sent > 60_000) ||
            !IsSessionPreparationInputLifetimeCurrent(attachment, epoch)) return false;

        message = new(envelope.Type, attachment, epoch, nonce, payerDecisionId,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresMs), accepted, senderRole, tunaAddress);
        return true;
    }

    private bool IsSessionPreparationInputLifetimeCurrent(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch)
        => IsSessionPreparationRequestBindingCurrent(attachment, epoch) && epoch.Generation > attachment.BaselineGeneration;

    private bool IsSessionPreparationRequestBindingCurrent(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch)
        => !disposed && ReferenceEquals(sessionPreparationLifecycle.Attachment, attachment) && attachment.IsCurrent &&
            ReferenceEquals(sessionPreparationLifecycle.Current, epoch) && epoch.IsCurrent &&
            epoch.Security is { } security &&
            security.HasCapability(CapabilityGrant.FileTransfer, DateTimeOffset.UtcNow);

    internal bool IsSessionPreparationReplyCurrent(SessionPreparationInbound message,
        FileTransferSessionPreparationPermit permit)
    {
        // No Core callback under accelerationGate. This is a current-state
        // decision only; lane/network effects still require their own fence.
        PreparationLaneWork? work;
        lock (preparationExecutionGate) work = preparationLaneWork;
        DateTimeOffset deadline;
        if (work is not null && ReferenceEquals(work.Execution.Permit, permit))
        {
            lock (work.Gate)
            {
                if (work.HandshakePermit is not { } handshake) return false;
                deadline = handshake.DeadlineUtc;
            }
        }
        else deadline = permit.DeadlineUtc;
        return message.Type == (permit.LocalIsPayer
                ? MsgType.SessionPreparationAnswer : MsgType.SessionPreparationAnswerAck) &&
            message.Accepted && message.Nonce == permit.Nonce && message.PayerDecisionId == permit.PayerDecisionId &&
            message.DeadlineUtc.ToUnixTimeMilliseconds() == deadline.ToUnixTimeMilliseconds() &&
            message.Epoch.Id == permit.Scope.SecurityIncarnation && message.Epoch.SessionId == permit.Scope.SessionId &&
            IsSessionPreparationInputLifetimeCurrent(message.Attachment, message.Epoch) &&
            message.Attachment.Authority.IsCurrent(message.Attachment, message.Epoch, permit);
    }
}

internal sealed record SessionPreparationInbound(MsgType Type,
    FileTransferSessionPreparationAttachment Attachment, FileTransferSessionPreparationSecurityEpoch Epoch,
    string Nonce, long PayerDecisionId, DateTimeOffset DeadlineUtc, bool Accepted,
    string? SenderRole, string? TunaAddress);
