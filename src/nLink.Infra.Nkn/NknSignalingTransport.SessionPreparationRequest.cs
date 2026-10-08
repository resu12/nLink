using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    internal Func<Task>? SessionPreparationBeforeRequestPermitForTests;
    internal Func<Task>? SessionPreparationBeforeTransferActivationOfferForTests;
    private SessionPreparationActivationHandoff? sessionPreparationActivationHandoff;

    private sealed class SessionPreparationActivationHandoff(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationPermit permit, CancellationToken cancellation)
    {
        public readonly FileTransferSessionPreparationAttachment Attachment = attachment;
        public readonly FileTransferSessionPreparationSecurityEpoch Epoch = epoch;
        public readonly FileTransferSessionPreparationPermit Permit = permit;
        public readonly CancellationToken Cancellation = cancellation;
        public readonly TaskCompletionSource<bool> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileTransferRoutePublicationAuthority? Publication;
        public CancellationToken? TransferLifetime;
        public Task Activation = Task.CompletedTask;
        public int Scheduled;
    }
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
        SessionPreparationActivationHandoff? handoff = null;
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
            var deadline = DateTimeOffset.UtcNow + FileTransferCoordinator.SessionPreparationListenerStartupLifetime;
            if (deadline > epoch.Security!.ApprovalExpiresAt!.Value) deadline = epoch.Security.ApprovalExpiresAt.Value;
            deadline = DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds());
            var decision = attachment.Authority.Begin(attachment, epoch, existing?.Nonce ?? Guid.NewGuid().ToString("N"),
                existing?.PayerDecisionId ?? ResolvePayerDecisionIdForNegotiation("runtime_unlock"), true,
                existing?.DeadlineUtc ?? deadline, listenerStartup: true);
            if (decision.Permit is not { LocalIsPayer: true } permit) return;
            ownedPermit = permit;
            handoff = new(attachment, epoch, permit, ct);
            Volatile.Write(ref sessionPreparationActivationHandoff, handoff);
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
            handoff?.Completed.TrySetResult(started);
        }
    }

    private void ContinueSessionPreparationAfterAdmission(FileTransferRoutePublicationAuthority publication)
    {
        var handoff = Volatile.Read(ref sessionPreparationActivationHandoff);
        if (handoff is null || publication.RouteToken != FileTransferRouteResolver.RegularNknV4FastToken ||
            publication.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4) return;
        PreparationLaneWork? work;
        lock (preparationExecutionGate) work = preparationLaneWork;
        // Only the exact Core admission that retired unfinished preparation can
        // carry its request forward. A later transfer cannot borrow this intent.
        if (work is null || !ReferenceEquals(work.Execution.Permit, handoff.Permit) ||
            work.Execution.Admission is not { Readiness: null } admission || admission.Scope != handoff.Permit.Scope ||
            admission.TransferId != publication.TransferId || admission.Direction != publication.Direction ||
            admission.Scope.SessionId != publication.SessionId ||
            handoff.Attachment.Authority.BindAdmissionActivationLifetime(handoff.Attachment, handoff.Epoch,
                admission, publication) is not { } transferLifetime ||
            Interlocked.CompareExchange(ref handoff.Scheduled, 1, 0) != 0) return;
        handoff.TransferLifetime = transferLifetime;
        handoff.Publication = publication;
        handoff.Activation = Task.Run(() => ContinueSessionPreparationAfterAdmissionAsync(handoff, publication),
            CancellationToken.None);
    }

    private async Task ContinueSessionPreparationAfterAdmissionAsync(
        SessionPreparationActivationHandoff handoff, FileTransferRoutePublicationAuthority publication)
    {
        try
        {
            // The canceled startup must release its negotiation slot first.
            if (await handoff.Completed.Task.ConfigureAwait(false) || !IsSessionPreparationActivationHandoffLifetimeCurrent(handoff) ||
                handoff.Attachment.Authority.SelectRequest(handoff.Attachment, handoff.Epoch).Purpose !=
                    FileTransferAccelerationRequestPurpose.TransferActivation ||
                !TryCaptureCurrentRuntimeUnlockFileTransferOwnerAuthority(publication.SessionId, out var current, out _) ||
                current != publication) return;
            if (!TryCaptureRuntimeUnlockInitialActivationReceiveRecoveryExpectedScope(publication, out _, out var reason))
            {
                // Admission can precede receiver acceptance. Keep the exact
                // request pending until authenticated regular-route traffic
                // wakes a recheck; that traffic grants no activation authority.
                if (reason == "runtime_unlock_initial_activation_data_session_not_current")
                    Interlocked.CompareExchange(ref handoff.Scheduled, 0, 1);
                return;
            }
            LocalOperationalLog.Info("NKN.Tuna",
                "event=session_preparation_transfer_activation_requested; reason=pending_preparation_admitted; authority_effect=none");
            // This continuation is not another user unlock: never clear a stop
            // that races dispatch, and retain the exact publication into preflight.
            ScheduleAccelerationNegotiationIfEligible("runtime_unlock", runtimeUnlockActivation: true,
                preparationHandoff: handoff);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna",
                $"event=session_preparation_transfer_activation_failed; error={ex.GetType().Name}; authority_effect=none");
        }
    }

    private bool IsSessionPreparationActivationHandoffLifetimeCurrent(SessionPreparationActivationHandoff handoff)
        => !handoff.Cancellation.IsCancellationRequested &&
            handoff.TransferLifetime is { IsCancellationRequested: false } &&
            ReferenceEquals(Volatile.Read(ref sessionPreparationActivationHandoff), handoff) &&
            IsSessionPreparationInputLifetimeCurrent(handoff.Attachment, handoff.Epoch) &&
            !IsAccelerationUserStoppedForCurrentSession() && !IsAccelerationPeerUserStoppedForFallbackSession(handoff.Epoch.SessionId);

    private void RecheckSessionPreparationAdmissionFromRegularFrame(FileTransferDataFrame frame)
    {
        var handoff = Volatile.Read(ref sessionPreparationActivationHandoff);
        if (handoff?.Publication is { } publication && Volatile.Read(ref handoff.Scheduled) == 0 &&
            frame.SessionId == publication.SessionId && frame.TransferId == publication.TransferId &&
            frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6 or
                FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6)
            ContinueSessionPreparationAfterAdmission(publication);
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
