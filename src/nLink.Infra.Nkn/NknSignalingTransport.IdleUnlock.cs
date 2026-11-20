using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // Retained user intent, never file-route or runtime-owner authority.
    private sealed record IdleSessionUnlockRequest(
        FileTransferSessionPreparationAttachment Attachment,
        FileTransferSessionPreparationSecurityEpoch Epoch,
        FileTransferAccelerationRequestSelection Selection,
        long RequestedAtUnixMs, CancellationToken Cancellation, long StopGeneration);

    private sealed record IdleSessionOfferBinding(
        IdleSessionUnlockRequest Request, string Nonce, long PayerDecisionId, long Generation);

    private IdleSessionUnlockRequest? idleSessionUnlock;
    private IdleSessionOfferBinding? outboundAccelerationOfferIdleUnlock;
    private IdleSessionOfferBinding? retiredAccelerationOfferIdleUnlock;
    private IdleSessionOfferBinding? acceptedIdleSessionAnswer;
    private bool idleSessionUnlockWaiting;
    private int accelerationLaneStopDrains;
    private long accelerationExplicitStopGeneration;

    internal Action? IdleSessionUnlockBeforePublicationForTests { get; set; }
    internal Func<Task>? IdleSessionUnlockBeforeRetryForTests { get; set; }
    internal Task? IdleSessionUnlockRetryForTests { get; private set; }
    internal Action? IdleSessionBeforeAnswerCommitForTests { get; set; }
    internal Func<Task>? IdleSessionBeforeAnswerAckForTests { get; set; }
    internal Task? IdleSessionAnswerAckForTests { get; private set; }
    internal Task[]? IdleSessionAnswerAckSendsForTests { get; private set; }

    // No Core callback here: safe to recheck while holding accelerationGate.
    private bool IsIdleSessionUnlockLifetimeCurrent(IdleSessionUnlockRequest request)
        => ReferenceEquals(Volatile.Read(ref idleSessionUnlock), request) &&
           Volatile.Read(ref accelerationExplicitStopGeneration) == request.StopGeneration &&
           !request.Cancellation.IsCancellationRequested &&
           DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - request.RequestedAtUnixMs < AccelerationOfferLifetime.TotalMilliseconds &&
           IsSessionPreparationRequestBindingCurrent(request.Attachment, request.Epoch);

    private bool IsIdleSessionUnlockBindingCurrent(IdleSessionUnlockRequest request)
        => IsIdleSessionUnlockLifetimeCurrent(request) &&
           request.Attachment.Authority.SelectRequest(request.Attachment, request.Epoch) == request.Selection;

    private bool IsIdleSessionUnlockCurrent(IdleSessionUnlockRequest request)
        => IsIdleSessionUnlockBindingCurrent(request) &&
           !IsAccelerationUserStoppedForCurrentSession() &&
           !IsAccelerationPeerUserStoppedForFallbackSession(request.Epoch.SessionId);

    private void RequestIdleSessionReestablishment(CancellationToken ct, long stopGeneration, long requestedAtUnixMs)
    {
        var attachment = sessionPreparationLifecycle.Attachment;
        var epoch = sessionPreparationLifecycle.Current;
        if (attachment is null || epoch is null || !IsSessionPreparationRequestBindingCurrent(attachment, epoch)) return;
        var selection = attachment.Authority.SelectRequest(attachment, epoch);
        if (selection.Purpose != FileTransferAccelerationRequestPurpose.IdleSessionReestablishment) return;
        var request = new IdleSessionUnlockRequest(attachment, epoch, selection,
            requestedAtUnixMs, ct, stopGeneration);
        IdleSessionUnlockBeforePublicationForTests?.Invoke();
        lock (accelerationGate)
        {
            if (accelerationExplicitStopGeneration != stopGeneration || ct.IsCancellationRequested) return;
            idleSessionUnlock = request;
            idleSessionUnlockWaiting = true;
        }
        NotifyTransportAccelerationStateChanged("renegotiating_after_user_unlock");
        Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
        RecheckPendingIdleSessionUnlock();
    }

    private void RecheckPendingIdleSessionUnlock()
    {
        var request = Volatile.Read(ref idleSessionUnlock);
        if (request is null || !IsIdleSessionUnlockBindingCurrent(request)) return;
        lock (accelerationGate)
        {
            if (!ReferenceEquals(idleSessionUnlock, request) || !idleSessionUnlockWaiting ||
                accelerationLaneStopDrains != 0 || accelerationNegotiationScheduled != 0) return;
            // Reapply this later user intent after the preceding stop finishes.
            // Any newer explicit stop atomically invalidates it when that stop begins.
            ClearAccelerationUserStoppedForCurrentSession();
            ClearAccelerationPeerUserStoppedForCurrentSession();
            idleSessionUnlockWaiting = false;
        }
        ScheduleAccelerationNegotiationIfEligible("session_ready_after_user_unlock", idleUnlock: request);
    }

    private IDisposable BeginAccelerationLaneStopDrain(bool invalidateIdleUnlock = false)
    {
        lock (accelerationGate)
        {
            accelerationLaneStopDrains++;
            if (invalidateIdleUnlock)
            {
                accelerationExplicitStopGeneration++;
                idleSessionUnlock = null;
                idleSessionUnlockWaiting = false;
            }
        }
        return new AccelerationLaneStopDrain(this);
    }

    private IdleSessionUnlockRequest? CaptureOutboundIdleSessionUnlock(string nonce)
    {
        lock (accelerationGate)
            return outboundAccelerationOfferNonce == nonce ? outboundAccelerationOfferIdleUnlock?.Request : null;
    }

    private bool IsIdleSessionOfferCurrentLocked(IdleSessionOfferBinding binding, bool retired)
        => ReferenceEquals(retired ? retiredAccelerationOfferIdleUnlock : outboundAccelerationOfferIdleUnlock, binding) &&
           (retired ? retiredAccelerationOfferNonce : outboundAccelerationOfferNonce) == binding.Nonce &&
           (retired ? retiredAccelerationOfferPayerDecisionId : outboundAccelerationOfferPayerDecisionId) == binding.PayerDecisionId &&
           (retired ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < retiredAccelerationOfferExpiresUtcMs
               : outboundAccelerationOfferGeneration == binding.Generation) &&
           binding.Request.Selection.IdleWrite?.IsCurrent == true &&
           IsIdleSessionUnlockLifetimeCurrent(binding.Request) &&
           !IsAccelerationUserStoppedForCurrentSession() &&
           !IsAccelerationPeerUserStoppedForFallbackSession(binding.Request.Epoch.SessionId);

    private bool IsIdleSessionAcceptedAnswerCurrentLocked(IdleSessionOfferBinding binding)
        => ReferenceEquals(acceptedIdleSessionAnswer, binding) &&
           accelerationSessionId == binding.Request.Epoch.SessionId &&
           IsIdleSessionUnlockLifetimeCurrent(binding.Request) &&
           !IsAccelerationUserStoppedForCurrentSession() &&
           !IsAccelerationPeerUserStoppedForFallbackSession(binding.Request.Epoch.SessionId);

    private bool IsIdleSessionAcceptedAnswerCurrent(IdleSessionOfferBinding binding)
    {
        // Acceptance consumed the idle selection. A newly admitted transfer must
        // not prevent completion of this handshake; it still needs its own Core authority.
        lock (accelerationGate) return IsIdleSessionAcceptedAnswerCurrentLocked(binding);
    }

    private sealed class AccelerationLaneStopDrain(NknSignalingTransport owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.accelerationGate) owner.accelerationLaneStopDrains--;
            owner.RecheckPendingIdleSessionUnlock();
        }
    }
}
