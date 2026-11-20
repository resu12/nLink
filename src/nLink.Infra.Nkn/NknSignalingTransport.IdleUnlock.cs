using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private const string DuplexSessionUnlockTrigger = "duplex_session_after_user_unlock";

    // Retained user intent, never file-route or runtime-owner authority.
    private sealed record IdleSessionUnlockRequest(
        FileTransferSessionPreparationAttachment Attachment,
        FileTransferSessionPreparationSecurityEpoch Epoch,
        FileTransferAccelerationRequestSelection Selection,
        long RequestedAtUnixMs, CancellationToken Cancellation, long StopGeneration)
    {
        public string Trigger => Selection.Purpose == FileTransferAccelerationRequestPurpose.DuplexSessionReestablishment
            ? DuplexSessionUnlockTrigger : "session_ready_after_user_unlock";
        public bool FromPeer { get; init; }
    }

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

    private void SchedulePreferredHelpeeNegotiation(string reason, string? peerTrigger, string sessionId, long sentAtUnixMs)
    {
        if (peerTrigger != DuplexSessionUnlockTrigger)
        {
            ScheduleAccelerationNegotiationIfEligible(reason);
            return;
        }
        var stopGeneration = Volatile.Read(ref accelerationExplicitStopGeneration);
        var selection = SelectSessionPreparationRequest();
        if (selection.Purpose == FileTransferAccelerationRequestPurpose.DuplexSessionReestablishment)
            RequestIdleSessionReestablishment(selection, CancellationToken.None, stopGeneration, sentAtUnixMs, sessionId);
    }

    private void RequestIdleSessionReestablishment(FileTransferAccelerationRequestSelection expected, CancellationToken ct, long stopGeneration, long requestedAtUnixMs, string? peerSession = null)
    {
        var attachment = sessionPreparationLifecycle.Attachment;
        var epoch = sessionPreparationLifecycle.Current;
        if (attachment is null || epoch is null || (peerSession is not null && peerSession != epoch.SessionId) ||
            !IsSessionPreparationRequestBindingCurrent(attachment, epoch)) return;
        var selection = attachment.Authority.SelectRequest(attachment, epoch);
        if (selection != expected) return;
        var request = new IdleSessionUnlockRequest(attachment, epoch, selection,
            requestedAtUnixMs, ct, stopGeneration) { FromPeer = peerSession is not null };
        IdleSessionUnlockBeforePublicationForTests?.Invoke();
        lock (accelerationGate)
        {
            if (accelerationExplicitStopGeneration != stopGeneration || ct.IsCancellationRequested) return;
            if (peerSession is not null &&
                (IsAccelerationUserStoppedForCurrentSession() || IsAccelerationPeerUserStoppedForFallbackSession(peerSession) ||
                 (idleSessionUnlock is { } prior && prior.Selection == selection && IsIdleSessionUnlockLifetimeCurrent(prior) &&
                  (prior.RequestedAtUnixMs >= requestedAtUnixMs || outboundAccelerationOfferIdleUnlock?.Request == prior ||
                   acceptedIdleSessionAnswer?.Request == prior)))) return;
            idleSessionUnlock = request;
            idleSessionUnlockWaiting = true;
        }
        NotifyTransportAccelerationStateChanged("renegotiating_after_user_unlock");
        if (peerSession is null) Interlocked.Exchange(ref accelerationNegotiationRetryAttempts, 0);
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
            if (!request.FromPeer)
            {
                ClearAccelerationUserStoppedForCurrentSession();
                ClearAccelerationPeerUserStoppedForCurrentSession();
            }
            idleSessionUnlockWaiting = false;
        }
        ScheduleAccelerationNegotiationIfEligible(request.Trigger, idleUnlock: request);
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
