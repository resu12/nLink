using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private sealed class FallbackListenerPreparationWork(
        FileTransferFallbackListenerPreparationPermit permit,
        FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, CancellationToken cancellation)
    {
        public readonly FileTransferFallbackListenerPreparationPermit Permit = permit;
        public readonly FileTransferSessionPreparationAttachment Attachment = attachment;
        public readonly FileTransferSessionPreparationSecurityEpoch Epoch = epoch;
        public readonly string Identity = string.Join("|", permit.Publication.SessionId,
            permit.Publication.TransferId, "fallback-listener-preparation", Guid.NewGuid().ToString("N"));
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public readonly TaskCompletionSource<NknTunaLaneLease?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private FallbackListenerPreparationWork? fallbackListenerPreparation;

    private void RequestFallbackListenerPreparation(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || disposed || string.IsNullOrWhiteSpace(remoteEndpoint) || IsAccelerationUserStoppedForCurrentSession() ||
            accelerationLane is not INknTunaAccelerationSession { CanOfferListener: true } ||
            !IsSessionAccelerationEligible(out var lanes) || (lanes & NknAccelerationLaneKind.File) == 0) return;
        var attachment = sessionPreparationLifecycle.Attachment;
        var epoch = sessionPreparationLifecycle.Current;
        if (attachment is null || epoch is null || !IsSessionPreparationInputLifetimeCurrent(attachment, epoch)) return;
        var permit = attachment.Authority.BeginFallbackListenerPreparation(attachment, epoch);
        if (permit is null) return;
        FallbackListenerPreparationWork work;
        lock (accelerationGate)
        {
            if (fallbackListenerPreparation is { } current && current.Permit == permit &&
                !current.Cancellation.IsCancellationRequested) return;
            RetireFallbackListenerPreparationUnsafe();
            work = fallbackListenerPreparation = new(permit, attachment, epoch, ct);
            var remaining = permit.ExpiresUtc - DateTimeOffset.UtcNow;
            work.Cancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        _ = PrepareFallbackListenerAsync(work);
    }

    private bool IsFallbackListenerPreparationCurrent(FallbackListenerPreparationWork work)
    {
        lock (accelerationGate)
            if (fallbackListenerPreparation != work || work.Cancellation.IsCancellationRequested || disposed) return false;
        return !IsAccelerationUserStoppedForCurrentSession() &&
            IsSessionPreparationInputLifetimeCurrent(work.Attachment, work.Epoch) &&
            work.Attachment.Authority.IsFallbackListenerPreparationCurrent(work.Attachment, work.Epoch, work.Permit);
    }

    private async Task PrepareFallbackListenerAsync(FallbackListenerPreparationWork work)
    {
        NknTunaLaneLease? lease = null;
        try
        {
            if (await ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(ResolveLocalSessionRole(),
                    "runtime_unlock", work.Cancellation.Token).ConfigureAwait(false) ||
                !IsFallbackListenerPreparationCurrent(work)) return;
            var lane = (INknTunaAccelerationSession)accelerationLane!;
            LogFallbackListenerPreparation(work, "Preparing", "coordinator_physical_preparation_permitted");
            lease = await lane.StartPreparationLaneAsync(new(work.Identity, remoteEndpoint!, LocalIsPayer: true,
                ListenerLifetimePolicy: NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation),
                work.Cancellation.Token).ConfigureAwait(false);
            if (!IsFallbackListenerPreparationCurrent(work))
            {
                if (lease is not null) lane.TryStopCurrentLaneLease(lease, "fallback_preparation_scope_retired");
                lease = null;
                LogFallbackListenerPreparation(work, "Rejected", "coordinator_preparation_scope_retired");
            }
            else LogFallbackListenerPreparation(work, lease is null ? "Unavailable" : "Ready",
                lease is null ? "physical_listener_unavailable" : "physical_listener_ready_no_dispatch_authority");
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFallbackListenerPreparation(work, "Unavailable", ex.GetType().Name);
        }
        finally
        {
            work.Completion.TrySetResult(lease);
            if (lease is null)
                lock (accelerationGate)
                    if (fallbackListenerPreparation == work) RetireFallbackListenerPreparationUnsafe();
        }
    }

    private async Task AwaitFallbackListenerPreparationAsync(FileTransferRoutePublicationAuthority source)
    {
        FallbackListenerPreparationWork? work;
        lock (accelerationGate) work = fallbackListenerPreparation;
        if (work is not null && FileTransferCoordinator.MatchesRoutePublicationAuthority(
                source, work.Permit.Publication) && IsFallbackListenerPreparationCurrent(work))
            await work.Completion.Task.ConfigureAwait(false);
    }

    private void RetireFallbackListenerPreparation()
    {
        lock (accelerationGate) RetireFallbackListenerPreparationUnsafe();
    }

    private void RetireFallbackListenerPreparationUnsafe()
    {
        var work = fallbackListenerPreparation;
        fallbackListenerPreparation = null;
        if (work is not null) _ = CancelFallbackListenerPreparationAsync(work);
    }

    private static async Task CancelFallbackListenerPreparationAsync(FallbackListenerPreparationWork work)
    {
        await work.Cancellation.CancelAsync().ConfigureAwait(false);
        await work.Completion.Task.ConfigureAwait(false);
        work.Cancellation.Dispose();
    }

    private static void LogFallbackListenerPreparation(FallbackListenerPreparationWork work, string status, string reason)
        => LocalOperationalLog.Info("NKN.Tuna",
            $"event=fallback_listener_preparation_decided; decision_status={status}; session_id={SanitizeLogToken(work.Permit.Publication.SessionId)}; transfer_id={SanitizeLogToken(work.Permit.Publication.TransferId)}; source_publication_generation={work.Permit.Publication.PublicationGeneration}; authority_effect=none; decision_reason={reason}");
}
