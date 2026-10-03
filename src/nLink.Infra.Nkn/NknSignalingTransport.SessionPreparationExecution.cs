using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private readonly object preparationExecutionGate = new();
    private PreparationLaneWork? preparationLaneWork;
    internal Func<Task>? SessionPreparationBeforeLaneStartForTests;

    private sealed class PreparationLaneWork(
        FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationExecution execution,
        INknTunaAccelerationSession lane, string? remoteTunaAddress)
    {
        public readonly object Gate = new();
        public readonly FileTransferSessionPreparationAttachment Attachment = attachment;
        public readonly FileTransferSessionPreparationSecurityEpoch Epoch = epoch;
        public readonly FileTransferSessionPreparationExecution Execution = execution;
        public readonly INknTunaAccelerationSession Lane = lane;
        public readonly string? RemoteTunaAddress = remoteTunaAddress;
        public readonly TaskCompletionSource<NknTunaLaneLease?> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NknTunaLaneLease? Lease;
        public CancellationTokenRegistration Revocation;
        public readonly TaskCompletionSource<SessionPreparationInbound> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HandshakeStarted;
        public FileTransferSessionPreparationHandshake? HandshakePermit;
        public string Identity => $"preparation:{Execution.Permit.Scope.SecurityIncarnation:N}:{Execution.Permit.Generation}:{Execution.Permit.Nonce}";
    }

    // Physical executor only. Readiness requires the separate three-leg handshake.
    internal Task<NknTunaLaneLease?> StartSessionPreparationLaneExecutionAsync(
        FileTransferSessionPreparationPermit permit, SessionPreparationInbound? offer, CancellationToken ct)
    {
        var attachment = sessionPreparationLifecycle.Attachment;
        var epoch = sessionPreparationLifecycle.Current;
        if (ct.IsCancellationRequested || attachment is null || epoch is null ||
            accelerationLane is not INknTunaAccelerationSession lane ||
            !IsSessionPreparationInputLifetimeCurrent(attachment, epoch) ||
            permit.LocalIsPayer && offer is not null ||
            !permit.LocalIsPayer && (offer is null || offer.Type != MsgType.SessionPreparationOffer ||
                !ReferenceEquals(offer.Attachment, attachment) || !ReferenceEquals(offer.Epoch, epoch) ||
                offer.Nonce != permit.Nonce || offer.PayerDecisionId != permit.PayerDecisionId ||
                offer.DeadlineUtc != permit.DeadlineUtc || string.IsNullOrWhiteSpace(offer.TunaAddress)))
            return Task.FromResult<NknTunaLaneLease?>(null);

        // No Core callback while holding either adapter/lane lock. This binding
        // is retired synchronously at the Core admission/key/lifecycle boundary.
        var execution = attachment.Authority.BindExecution(attachment, epoch, permit);
        if (execution is null) return Task.FromResult<NknTunaLaneLease?>(null);
        PreparationLaneWork work;
        lock (preparationExecutionGate)
        {
            if (preparationLaneWork is { } existing && !existing.Execution.Cancellation.IsCancellationRequested)
                return ReferenceEquals(existing.Execution, execution) && existing.RemoteTunaAddress == offer?.TunaAddress
                    ? existing.Started.Task : Task.FromResult<NknTunaLaneLease?>(null);
            work = new(attachment, epoch, execution, lane, offer?.TunaAddress);
            preparationLaneWork = work;
        }

        work.Revocation = execution.Cancellation.Register(() => CleanupPreparationLaneWork(work));
        _ = RunPreparationLaneWorkAsync(work, ct);
        return work.Started.Task;
    }

    private async Task RunPreparationLaneWorkAsync(PreparationLaneWork work, CancellationToken ct)
    {
        using var requestCancellation = ct.Register(work.Execution.Retire);
        NknTunaLaneLease? started = null;
        var retainedByPreparation = false;
        try
        {
            var permit = work.Execution.Permit;
            if (SessionPreparationBeforeLaneStartForTests is { } beforeStart)
                await beforeStart().ConfigureAwait(false);
            if (!IsPreparationLaneWorkCurrent(work)) return;
            var remotePeer = ResolveExpectedRemotePeerAddressForCurrentSession();
            if (string.IsNullOrWhiteSpace(remotePeer)) return;
            var request = new NknTunaLanePreparationRequest(
                work.Identity,
                remotePeer, permit.LocalIsPayer, work.RemoteTunaAddress);
            started = await work.Lane.StartPreparationLaneAsync(request, work.Execution.Cancellation).ConfigureAwait(false);
            if (started is null || !IsPreparationLaneWorkCurrent(work) || work.Lane.CaptureCurrentLaneLease() != started) return;
            lock (work.Gate)
            {
                if (!work.Execution.AllowsPreparation || work.Execution.Cancellation.IsCancellationRequested) return;
                work.Lease = started;
                retainedByPreparation = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=session_preparation_lane_start_failed; error={ex.GetType().Name}; authority_effect=none");
        }
        finally
        {
            if (!retainedByPreparation)
            {
                if (started is not null) StopPreparationLaneLease(work, started);
                work.Execution.Retire();
                work.Attachment.Authority.Retire(work.Attachment, work.Epoch, work.Execution.Permit);
            }
            work.Started.TrySetResult(retainedByPreparation && work.Execution.AllowsPreparation ? started : null);
        }
    }

    private bool IsPreparationLaneWorkCurrent(PreparationLaneWork work)
        => work.Execution.AllowsPreparation &&
            IsSessionPreparationInputLifetimeCurrent(work.Attachment, work.Epoch) &&
            work.Attachment.Authority.IsCurrent(work.Attachment, work.Epoch, work.Execution.Permit);

    private static void CleanupPreparationLaneWork(PreparationLaneWork work)
    {
        NknTunaLaneLease? retired;
        lock (work.Gate)
        {
            retired = work.Lease;
            work.Lease = null;
        }
        if (retired is not null) StopPreparationLaneLease(work, retired);
        // This asynchronous cancellation callback is never invoked by a Core
        // gate owner. A stale retirement cannot alter a successor Core episode.
        work.Attachment.Authority.Retire(work.Attachment, work.Epoch, work.Execution.Permit);
        work.Revocation.Unregister();
    }

    private static void StopPreparationLaneLease(PreparationLaneWork work, NknTunaLaneLease lease)
    {
        try { work.Lane.TryStopCurrentLaneLease(lease, "preparation_execution_retired"); }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=session_preparation_exact_cleanup_failed; error={ex.GetType().Name}; authority_effect=none");
        }
    }

    private void RetireSessionPreparationExecution()
    {
        PreparationLaneWork? work;
        PreparationAdmissionWork? admission;
        lock (preparationExecutionGate) { work = preparationLaneWork; admission = preparationAdmissionWork; }
        work?.Execution.Retire();
        admission?.Authority.Retire();
    }

    private void HandleSessionPreparationLaneState(AccelerationStateChangedEventArgs e)
    {
        lock (accelerationGate)
            if (!e.IsAvailable && fallbackListenerPreparation?.Identity == e.SessionPreparationExecutionIdentity)
                RetireFallbackListenerPreparationUnsafe();
        PreparationLaneWork? work;
        lock (preparationExecutionGate) work = preparationLaneWork;
        // The event carries its original purpose/owner. Even a late predecessor
        // event must never fall through to ordinary recovery/activation logic.
        if (!e.IsAvailable && work?.Identity == e.SessionPreparationExecutionIdentity)
            work.Execution.Retire();
        PreparationAdmissionWork? admission;
        lock (preparationExecutionGate) admission = preparationAdmissionWork;
        if (!e.IsAvailable && admission?.Identity == e.SessionPreparationExecutionIdentity && !admission.Activated)
            admission.Authority.Retire();
        LocalOperationalLog.Info("NKN.Tuna",
            $"event=session_preparation_lane_state; available={(e.IsAvailable ? 1 : 0)}; authority_effect=none");
    }
}
