using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    internal Func<Task>? SessionPreparationBeforeRequestPermitForTests;
    private FileTransferAccelerationRequestSelection SelectSessionPreparationRequest()
    {
        var attachment = sessionPreparationLifecycle.Attachment;
        var epoch = sessionPreparationLifecycle.Current;
        // Existing transfer integrations may carry Coordinator-issued publication
        // authority without implementing the new preparation attachment. Preserve
        // that path and its subsequent source-current guards, never infer it from
        // a data-session/route-cache entry or from preparation being unavailable.
        if (attachment is null && epoch is { IsCurrent: true } &&
            TryCaptureCurrentRuntimeUnlockFileTransferOwnerAuthority(epoch.SessionId, out _, out _))
            return new(FileTransferAccelerationRequestPurpose.TransferActivation, "existing_coordinator_transfer_publication");
        return attachment is not null && epoch is not null && IsSessionPreparationRequestBindingCurrent(attachment, epoch)
            ? attachment.Authority.SelectRequest(attachment, epoch)
            : new(FileTransferAccelerationRequestPurpose.Unavailable, "preparation_binding_unavailable");
    }

    private async Task RequestSessionPreparationAsync(CancellationToken ct)
    {
        long slot = 0;
        FileTransferSessionPreparationPermit? ownedPermit = null;
        FileTransferSessionPreparationAttachment? attachment = null;
        FileTransferSessionPreparationSecurityEpoch? epoch = null;
        var started = false;
        try
        {
            if (accelerationLane is not INknTunaAccelerationSession { CanOfferListener: true } || ct.IsCancellationRequested) return;
            attachment = sessionPreparationLifecycle.Attachment;
            epoch = sessionPreparationLifecycle.Current;
            if (attachment is null || epoch is null || !IsSessionPreparationInputLifetimeCurrent(attachment, epoch)) return;
            // Reuse the existing bounded helper/helpee payer preference. Incoming
            // preparation may win while this waits; Core is consulted again below.
            if (await ShouldSuppressLocalPaidOfferForHelpeePriorityAsync(ResolveLocalSessionRole(), "runtime_unlock", ct).ConfigureAwait(false)) return;
            if (SessionPreparationBeforeRequestPermitForTests is { } beforePermit) await beforePermit().ConfigureAwait(false);
            if (!IsSessionPreparationInputLifetimeCurrent(attachment, epoch)) return;
            var selected = attachment.Authority.SelectRequest(attachment, epoch);
            if (selected.Purpose != FileTransferAccelerationRequestPurpose.SessionPreparation ||
                selected.Permit is { LocalIsPayer: false }) return;
            if (!TryAcquireSessionPreparationNegotiationSlot(out slot)) return;
            if (ct.IsCancellationRequested || IsAccelerationUserStoppedForCurrentSession() ||
                CaptureRuntimeUnlockPostFallbackSchedulingBoundary(epoch.SessionId).Kind != RuntimeUnlockPostFallbackSchedulingBoundaryKind.NoFallback)
                return;
            var existing = selected.Permit;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            if (deadline > epoch.Security!.ApprovalExpiresAt!.Value) deadline = epoch.Security.ApprovalExpiresAt.Value;
            deadline = DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds());
            var decision = attachment.Authority.Begin(attachment, epoch, existing?.Nonce ?? Guid.NewGuid().ToString("N"),
                existing?.PayerDecisionId ?? ResolvePayerDecisionIdForNegotiation("runtime_unlock"), true,
                existing?.DeadlineUtc ?? deadline);
            if (decision.Permit is not { LocalIsPayer: true } permit) return;
            ownedPermit = permit;
            started = await StartSessionPreparationHandshakeAsync(permit, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=session_preparation_request_failed; error={ex.GetType().Name}; authority_effect=none");
        }
        finally
        {
            if (!started && ownedPermit is not null && attachment is not null && epoch is not null)
                attachment.Authority.Retire(attachment, epoch, ownedPermit);
            lock (accelerationGate)
                _ = TryReleaseAccelerationNegotiationSlotUnsafe(slot, AccelerationNegotiationSlotOwner.SessionPreparation);
        }
    }

    private bool TryAcquireSessionPreparationNegotiationSlot(out long slot)
    {
        lock (accelerationGate)
        {
            slot = 0;
            if (!string.IsNullOrWhiteSpace(outboundAccelerationOfferNonce) ||
                !string.IsNullOrWhiteSpace(pendingAccelerationAnswerAckNonce) ||
                GetFileTransferRouteTransitionPauseAuthorityUnsafe() is not null ||
                !TryAcquireGenericAccelerationNegotiationSlot(out slot)) return false;
            accelerationNegotiationSlotOwner = AccelerationNegotiationSlotOwner.SessionPreparation;
            return true;
        }
    }
}
