using NLink.Core.SessionSecurity;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService : IFileTransferSessionPreparationAuthority
{
    private FileTransferSessionPreparationState sessionPreparation = new(Guid.NewGuid());
    private FileTransferSessionPreparationAttachment? preparationAttachment;
    private IFileTransferSignalingTransport? preparationTransport;
    private FileTransferSessionPreparationSecurityEpoch? preparationEpoch;
    private long preparationBlockedThroughGeneration;
    private PreparationAdmissionCapture? preparationAdmission;
    private FileTransferSessionPreparationExecution? preparationExecution;
    private FileTransferSessionPreparationAdmissionLease? preparationAdmissionLease;

    private sealed record PreparationAdmissionCapture(IFileTransferSignalingTransport Transport,
        FileTransferSessionPreparationAttachment Attachment, FileTransferSessionPreparationSecurityEpoch Epoch,
        FileTransferSessionPreparationAdmission Admission);

    private void AttachPreparationTransport(IFileTransferSignalingTransport source)
    {
        var attachment = (source as IFileTransferSessionPreparationTransport)?.SessionPreparationLifecycle.TryAttach(this);
        lock (gate)
        {
            transport = source;
            preparationTransport = source;
            preparationAttachment = attachment;
            preparationEpoch = null;
            preparationAdmission = null;
            preparationAdmissionLease?.Retire();
            preparationAdmissionLease = null;
            preparationBlockedThroughGeneration = attachment?.BaselineGeneration ?? 0;
            sessionPreparation = FileTransferCoordinator.AttachSessionPreparationTransport(sessionPreparation);
            preparationExecution?.Synchronize(sessionPreparation);
        }
    }

    private FileTransferSessionPreparationAttachment? DetachPreparationTransportLocked()
    {
        var old = preparationAttachment;
        preparationAttachment = null;
        preparationTransport = null;
        preparationEpoch = null;
        preparationAdmission = null;
        preparationAdmissionLease?.Retire();
        preparationAdmissionLease = null;
        sessionPreparation = FileTransferCoordinator.DetachSessionPreparationTransport(sessionPreparation);
        preparationExecution?.Synchronize(sessionPreparation);
        return old;
    }

    private void RevokePreparationScopeLocked()
    {
        preparationBlockedThroughGeneration = Math.Max(preparationBlockedThroughGeneration,
            preparationAttachment?.Lifecycle.Generation ?? 0);
        preparationAdmission = null;
        if (sessionPreparation.Scope is { } scope)
            sessionPreparation = FileTransferCoordinator.RevokeSessionPreparationScope(sessionPreparation, scope).State;
        preparationExecution?.Retire();
        preparationAdmissionLease?.Retire();
    }

    private void RevokePreparationForTransport(object? source)
    {
        lock (gate)
        {
            if (ReferenceEquals(source, transport)) RevokePreparationScopeLocked();
        }
    }

    private bool IsPreparationAttachmentCurrentLocked(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch)
        => !disposed && ReferenceEquals(preparationAttachment, attachment) && attachment.IsCurrent &&
            ReferenceEquals(transport, preparationTransport) &&
            ReferenceEquals(attachment.Lifecycle.Current, epoch) && epoch.IsCurrent &&
            epoch.Generation > attachment.BaselineGeneration && epoch.Security is not null;

    private bool EnsurePreparationScopeLocked(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch)
    {
        if (!IsPreparationAttachmentCurrentLocked(attachment, epoch)) return false;
        if (ReferenceEquals(preparationEpoch, epoch)) return sessionPreparation.Scope is not null;
        if (epoch.Generation <= preparationBlockedThroughGeneration ||
            outboundTransfer is { IsTerminal: false } || inboundTransfer is { IsTerminal: false }) return false;
        var decision = FileTransferCoordinator.RegisterSessionPreparationScope(sessionPreparation,
            sessionPreparation.AttachmentGeneration, checked(sessionPreparation.LastSecurityGeneration + 1),
            epoch.Id, epoch.Security!, DateTimeOffset.UtcNow);
        if (decision.Status != FileTransferSessionPreparationStatus.Accepted) return false;
        sessionPreparation = decision.State;
        preparationEpoch = epoch;
        preparationAdmission = null;
        return true;
    }

    FileTransferFallbackListenerPreparationPermit? IFileTransferSessionPreparationAuthority.BeginFallbackListenerPreparation(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch)
    {
        lock (gate)
        {
            FileTransferFallbackListenerPreparationPermit? result = null;
            epoch.TryUse(() =>
            {
                if (!IsPreparationAttachmentCurrentLocked(attachment, epoch) ||
                    epoch.Security?.ApprovalExpiresAt is not { } expires ||
                    !epoch.Security.HasCapability(CapabilityGrant.FileTransfer, DateTimeOffset.UtcNow)) return;
                var active = CaptureFallbackListenerPreparationStateLocked();
                if (active is not null && active.SessionId == epoch.SessionId)
                    result = FileTransferCoordinator.CreateFallbackListenerPreparationPermit(
                        active, epoch.Id, expires, DateTimeOffset.UtcNow);
            });
            return result;
        }
    }

    bool IFileTransferSessionPreparationAuthority.IsFallbackListenerPreparationCurrent(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferFallbackListenerPreparationPermit permit)
    {
        lock (gate)
        {
            var current = false;
            epoch.TryUse(() =>
            {
                if (!IsPreparationAttachmentCurrentLocked(attachment, epoch) ||
                    !epoch.Security!.HasCapability(CapabilityGrant.FileTransfer, DateTimeOffset.UtcNow)) return;
                var active = CaptureFallbackListenerPreparationStateLocked();
                current = active is not null && active.SessionId == epoch.SessionId &&
                    FileTransferCoordinator.IsFallbackListenerPreparationPermitCurrent(
                        active, permit, epoch.Id, DateTimeOffset.UtcNow);
            });
            return current;
        }
    }

    private FileTransferCoordinatorState? CaptureFallbackListenerPreparationStateLocked()
    {
        var outbound = outboundTransfer is { IsTerminal: false };
        var inbound = inboundTransfer is { IsTerminal: false };
        return outbound == inbound ? null : outbound
            ? CreateCoordinatorStateLocked(outboundTransfer!) : CreateCoordinatorStateLocked(inboundTransfer!);
    }

    FileTransferAccelerationRequestSelection IFileTransferSessionPreparationAuthority.SelectRequest(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch)
    {
        lock (gate)
        {
            var result = new FileTransferAccelerationRequestSelection(FileTransferAccelerationRequestPurpose.Unavailable,
                "preparation_service_binding_unavailable");
            epoch.TryUse(() =>
            {
                // A known real transfer can use its existing activation path even
                // when this attachment has no pre-first-admission provenance.
                if (disposed || !ReferenceEquals(preparationAttachment, attachment) || !attachment.IsCurrent ||
                    !ReferenceEquals(transport, preparationTransport) || !ReferenceEquals(attachment.Lifecycle.Current, epoch) ||
                    !epoch.IsCurrent || epoch.Security?.ApprovalExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow) return;
                var outbound = outboundTransfer is { IsTerminal: false };
                var inbound = inboundTransfer is { IsTerminal: false };
                if (outbound && inbound) return; // No ambiguous transfer owner selection.
                FileTransferCoordinatorState? active = outbound ? CreateCoordinatorStateLocked(outboundTransfer!)
                    : inbound ? CreateCoordinatorStateLocked(inboundTransfer!) : null;
                if (active is null && (!EnsurePreparationScopeLocked(attachment, epoch) || !IsPreparationResourceCurrentLocked())) return;
                result = FileTransferCoordinator.SelectSessionPreparationRequest(sessionPreparation, epoch.SessionId,
                    active, DateTimeOffset.UtcNow);
            });
            return result;
        }
    }

    FileTransferSessionPreparationDecision IFileTransferSessionPreparationAuthority.Begin(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        string nonce, long payerDecisionId, bool localIsPayer, DateTimeOffset deadlineUtc, bool listenerStartup)
    {
        lock (gate)
        {
            return DecidePreparationLocked(attachment, epoch, () =>
                EnsurePreparationScopeLocked(attachment, epoch)
                    ? FileTransferCoordinator.BeginSessionPreparation(sessionPreparation, sessionPreparation.Scope!,
                        nonce, payerDecisionId, localIsPayer, deadlineUtc, DateTimeOffset.UtcNow, listenerStartup)
                    : PreparationUnavailable());
        }
    }

    FileTransferSessionPreparationDecision IFileTransferSessionPreparationAuthority.BeginHandshake(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationPermit permit)
    {
        FileTransferSessionPreparationState captured;
        IFileTransferSessionPreparationLeaseProvider provider;
        lock (gate)
        {
            if (!IsPreparationAttachmentCurrentLocked(attachment, epoch) || !IsPreparationResourceCurrentLocked() ||
                !ReferenceEquals(sessionPreparation.Permit, permit)) return PreparationUnavailable();
            if (sessionPreparation.Phase == FileTransferSessionPreparationPhase.Pending)
                return DecidePreparationLocked(attachment, epoch, () =>
                    FileTransferCoordinator.BeginSessionPreparationHandshake(sessionPreparation, permit, null, DateTimeOffset.UtcNow));
            if (sessionPreparation.Phase != FileTransferSessionPreparationPhase.Starting ||
                CurrentPreparationExecutionLocked() is not { AllowsPreparation: true } execution ||
                !ReferenceEquals(execution.Permit, permit) ||
                transport is not IFileTransferSessionPreparationLeaseProvider currentProvider) return PreparationUnavailable();
            captured = sessionPreparation;
            provider = currentProvider;
        }
        var requestedUtc = DateTimeOffset.UtcNow;
        if (!provider.TryObserveSessionPreparationLease(permit.Scope, permit.Generation, out var lease) ||
            lease is null || lease.ObservedUtc < requestedUtc)
        {
            lock (gate) return PreparationUnavailable();
        }
        lock (gate)
        {
            return DecidePreparationLocked(attachment, epoch, () =>
                ReferenceEquals(captured, sessionPreparation) && IsPreparationResourceCurrentLocked()
                    ? FileTransferCoordinator.BeginSessionPreparationHandshake(sessionPreparation, permit, lease, DateTimeOffset.UtcNow)
                    : PreparationUnavailable());
        }
    }

    FileTransferSessionPreparationDecision IFileTransferSessionPreparationAuthority.Complete(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationPermit permit, FileTransferSessionPreparationProof proof)
    {
        lock (gate)
        {
            return DecidePreparationLocked(attachment, epoch, () => permit.Scope.SecurityIncarnation == epoch.Id &&
                (preparationExecution is not { } execution || !ReferenceEquals(execution.Permit, permit) || execution.AllowsPreparation)
                ? FileTransferCoordinator.CompleteSessionPreparation(sessionPreparation, permit, proof, DateTimeOffset.UtcNow)
                : PreparationUnavailable());
        }
    }

    bool IFileTransferSessionPreparationAuthority.IsCurrent(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit)
    {
        lock (gate)
        {
            var current = false;
            epoch.TryUse(() => current = IsPreparationAttachmentCurrentLocked(attachment, epoch) &&
                permit.Scope.SecurityIncarnation == epoch.Id &&
                (preparationExecution is not { } execution || !ReferenceEquals(execution.Permit, permit) || execution.AllowsPreparation) &&
                FileTransferCoordinator.IsSessionPreparationPermitCurrent(sessionPreparation, permit, DateTimeOffset.UtcNow));
            return current;
        }
    }

    FileTransferSessionPreparationDecision IFileTransferSessionPreparationAuthority.Retire(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationPermit permit)
    {
        lock (gate)
        {
            return DecidePreparationLocked(attachment, epoch, () => permit.Scope.SecurityIncarnation == epoch.Id
                ? FileTransferCoordinator.RetireSessionPreparationEpisode(sessionPreparation, permit)
                : PreparationUnavailable());
        }
    }

    FileTransferSessionPreparationExecution? IFileTransferSessionPreparationAuthority.BindExecution(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationPermit permit)
    {
        lock (gate)
        {
            FileTransferSessionPreparationExecution? result = null;
            epoch.TryUse(() =>
            {
                if (!IsPreparationAttachmentCurrentLocked(attachment, epoch) ||
                    permit.Scope.SecurityIncarnation != epoch.Id ||
                    !FileTransferCoordinator.IsSessionPreparationPermitCurrent(sessionPreparation, permit, DateTimeOffset.UtcNow)) return;
                if (preparationExecution is { } existing && ReferenceEquals(existing.Permit, permit))
                {
                    if (existing.AllowsPreparation) result = existing;
                    return;
                }
                // Completed synthetic evidence cannot manufacture an execution
                // owner after the fact; bind while the exact episode is pending.
                if (sessionPreparation.Phase is not (FileTransferSessionPreparationPhase.Starting or FileTransferSessionPreparationPhase.Pending)) return;
                var candidate = new FileTransferSessionPreparationExecution(permit);
                if (!attachment.BindExecution(candidate) || !epoch.BindExecution(candidate))
                {
                    candidate.Retire();
                    return;
                }
                preparationExecution?.Retire();
                preparationExecution = candidate;
                candidate.Synchronize(sessionPreparation);
                result = candidate.AllowsPreparation ? candidate : null;
            });
            return result;
        }
    }

    private FileTransferSessionPreparationDecision PreparationUnavailable()
        => new(sessionPreparation, FileTransferSessionPreparationStatus.Stale, "preparation_service_binding_unavailable");

    FileTransferSessionPreparationAdmissionLease? IFileTransferSessionPreparationAuthority.BindAdmission(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferSessionPreparationAdmission admission, FileTransferInitialTunaReadinessEvidence evidence)
    {
        lock (gate)
        {
            FileTransferSessionPreparationAdmissionLease? result = null;
            epoch.TryUse(() =>
            {
                if (preparationAdmission is not { } capture || !ReferenceEquals(capture.Attachment, attachment) ||
                    !ReferenceEquals(capture.Epoch, epoch) || !ReferenceEquals(capture.Admission, admission) ||
                    !IsPreparationCaptureCurrentLocked(capture) || !ReferenceEquals(sessionPreparation.ClaimedEvidence, evidence)) return;
                if (preparationAdmissionLease is { } existing && ReferenceEquals(existing.Admission, admission))
                {
                    if (existing.IsCurrent && ReferenceEquals(existing.Evidence, evidence)) result = existing;
                    return;
                }
                if (CurrentPreparationExecutionLocked() is not { } execution || !execution.IsAdmissionCurrent(admission)) return;
                var candidate = new FileTransferSessionPreparationAdmissionLease(execution, admission, evidence);
                if (!attachment.BindAdmission(candidate) || !epoch.BindAdmission(candidate))
                {
                    candidate.Retire();
                    return;
                }
                preparationAdmissionLease?.Retire();
                preparationAdmissionLease = result = candidate;
            });
            return result;
        }
    }

    FileTransferSessionPreparationObservation? IFileTransferSessionPreparationAuthority.Observe(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        FileTransferDirection direction, DateTimeOffset requestedUtc)
    {
        FileTransferSessionPreparationState captured;
        IFileTransferSessionPreparationLeaseProvider provider;
        lock (gate)
        {
            if (!IsPreparationAttachmentCurrentLocked(attachment, epoch) || !IsPreparationResourceCurrentLocked() ||
                sessionPreparation.Readiness is null ||
                transport is not IFileTransferSessionPreparationLeaseProvider currentProvider) return null;
            captured = sessionPreparation;
            provider = currentProvider;
        }
        if (!provider.TryObserveSessionPreparationLease(captured.Scope!, captured.Readiness!.Proof.SnapshotGeneration, out var lease) ||
            lease is null) return null;
        lock (gate)
        {
            FileTransferSessionPreparationObservation? result = null;
            epoch.TryUse(() =>
            {
                if (IsPreparationAttachmentCurrentLocked(attachment, epoch) && IsPreparationResourceCurrentLocked() &&
                    ReferenceEquals(captured, sessionPreparation))
                    result = FileTransferCoordinator.ObserveSessionPreparation(captured, captured.Scope!, direction,
                        lease, requestedUtc, DateTimeOffset.UtcNow);
            });
            return result;
        }
    }

    private FileTransferSessionPreparationDecision DecidePreparationLocked(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch,
        Func<FileTransferSessionPreparationDecision> decide)
    {
        var result = PreparationUnavailable();
        epoch.TryUse(() =>
        {
            if (!IsPreparationAttachmentCurrentLocked(attachment, epoch)) return;
            void Apply()
            {
                result = decide();
                sessionPreparation = result.State;
                preparationExecution?.Synchronize(sessionPreparation);
            }
            if (preparationExecution is { } execution) execution.ApplyStateTransition(Apply);
            else Apply();
        });
        return result;
    }

    private PreparationAdmissionCapture? ReservePreparationAdmissionLocked(
        IFileTransferSignalingTransport? source, string? sessionId, string transferId, FileTransferDirection direction)
    {
        if (!ReferenceEquals(source, transport) || source is null || preparationAttachment is not { } attachment)
            return null;
        var epoch = attachment.Lifecycle.Current;
        if (epoch is null || sessionId != epoch.SessionId || string.IsNullOrWhiteSpace(transferId)) return null;
        PreparationAdmissionCapture? result = null;
        epoch.TryUse(() =>
        {
            var registered = EnsurePreparationScopeLocked(attachment, epoch);
            // Even an admission before approval publication closes this installation's
            // slot. A later status notification cannot pretend there was no admission.
            preparationBlockedThroughGeneration = Math.Max(preparationBlockedThroughGeneration, epoch.Generation);
            if (!registered) return;
            var wasPreparation = sessionPreparation.Phase is FileTransferSessionPreparationPhase.Starting or FileTransferSessionPreparationPhase.Pending or FileTransferSessionPreparationPhase.Ready;
            var decision = FileTransferCoordinator.ReserveSessionPreparationAdmission(sessionPreparation,
                sessionPreparation.Scope!, transferId, direction, DateTimeOffset.UtcNow);
            sessionPreparation = decision.State;
            preparationExecution?.Synchronize(sessionPreparation);
            if (decision.Admission is not { } admission) return;
            if (wasPreparation) preparationAdmission = new(source, attachment, epoch, admission);
            result = preparationAdmission is { } capture && ReferenceEquals(capture.Admission, admission) ? capture : null;
        });
        return result;
    }

    private PreparationAdmissionCapture? ReservePreparationSend(string transferId)
    {
        lock (gate)
        {
            if (disposed || outboundTransfer is { IsTerminal: false }) return null;
            return ReservePreparationAdmissionLocked(transport,
                preparationAttachment?.Lifecycle.Current?.SessionId, transferId, FileTransferDirection.Outbound);
        }
    }

    private PreparationAdmissionCapture? ReservePreparationOffer(object? source, FileTransferOfferV2 message)
    {
        if (!IsNegotiableDataProtocolVersion(message.PreferredDataProtocolVersion) ||
            string.IsNullOrWhiteSpace(message.TransferId) || string.IsNullOrWhiteSpace(message.FileName) ||
            message.FileSizeBytes < 0 || !string.IsNullOrWhiteSpace(message.FileTransferRoute) &&
            !TryResolveRouteSelectionFromToken(message.FileTransferRoute, message.PreferredDataProtocolVersion!.Value, out _)) return null;
        lock (gate)
            return ReservePreparationAdmissionLocked(source as IFileTransferSignalingTransport,
                message.SessionId, message.TransferId, FileTransferDirection.Inbound);
    }

    private FileTransferInitialTunaReadinessEvidence? CapturePreparationAdmissionEvidence(PreparationAdmissionCapture? capture)
    {
        // No service gate while consulting the adapter. Only this exact reservation
        // can adopt the observation after the adapter returns.
        if (capture?.Admission.Readiness is not { } ready ||
            capture.Transport is not IFileTransferSessionPreparationLeaseProvider provider ||
            !provider.TryObserveSessionPreparationLease(capture.Admission.Scope, ready.Proof.SnapshotGeneration, out var lease) ||
            lease is null) return null;
        FileTransferInitialTunaReadinessEvidence? evidence;
        bool physicalHandoffRequired;
        lock (gate)
        {
            var decision = DecidePreparationLocked(capture.Attachment, capture.Epoch, () =>
                IsPreparationCaptureCurrentLocked(capture) && IsPreparationEvidenceResourceCurrentLocked(capture)
                    ? FileTransferCoordinator.ClaimSessionPreparationReadiness(sessionPreparation, capture.Admission, lease, DateTimeOffset.UtcNow)
                    : PreparationUnavailable());
            evidence = decision.Evidence;
            physicalHandoffRequired = CurrentPreparationExecutionLocked() is not null;
        }
        if (evidence is null || !physicalHandoffRequired) return evidence;
        if (capture.Transport is not IFileTransferSessionPreparationAdmissionTransport adopter ||
            adopter.RetainSessionPreparationForAdmission(capture.Admission, evidence) is not { } retained) return null;
        lock (gate)
            return ReferenceEquals(preparationAdmissionLease, retained)
                ? RecheckPreparationEvidenceLocked(capture, evidence) : null;
    }

    private bool IsPreparationCaptureCurrentLocked(PreparationAdmissionCapture capture)
        => ReferenceEquals(preparationAdmission, capture) && ReferenceEquals(transport, capture.Transport) &&
            IsPreparationAttachmentCurrentLocked(capture.Attachment, capture.Epoch) &&
            ReferenceEquals(sessionPreparation.Admission, capture.Admission) &&
            DateTimeOffset.UtcNow < capture.Admission.Scope.ApprovalExpiresUtc;

    private bool IsPreparationResourceCurrentLocked()
        => CurrentPreparationExecutionLocked()?.IsResourceCurrent ?? true;

    private bool IsPreparationEvidenceResourceCurrentLocked(PreparationAdmissionCapture capture)
        => preparationAdmissionLease is { } retained && ReferenceEquals(retained.Admission, capture.Admission)
            ? retained.IsRetained : CurrentPreparationExecutionLocked()?.IsAdmissionCurrent(capture.Admission) ?? true;

    private bool RequiresPreparationAdmissionLocked(PreparationAdmissionCapture? capture, FileTransferRouteSelection route)
        => capture is not null && route.Route == FileTransferRoute.FileTunaV4 &&
            preparationAdmissionLease is { } lease && ReferenceEquals(lease.Admission, capture.Admission);

    private bool AdoptPreparationAdmissionLocked(PreparationAdmissionCapture? capture,
        FileTransferCoordinatorState state, CancellationToken lifetime)
    {
        if (capture is null || preparationAdmissionLease is not { } lease || !ReferenceEquals(lease.Admission, capture.Admission)) return false;
        var adopted = false;
        capture.Epoch.TryUse(() => adopted = IsPreparationCaptureCurrentLocked(capture) && lease.Adopt(state, lifetime));
        return adopted;
    }

    private bool ActivatePreparationAdmission(PreparationAdmissionCapture? capture, bool required)
    {
        if (!required) return true;
        FileTransferSessionPreparationAdmissionLease? lease;
        lock (gate) lease = preparationAdmissionLease;
        return capture is not null && lease is not null && ReferenceEquals(lease.Admission, capture.Admission) &&
            lease.IsAdopted && capture.Transport is IFileTransferSessionPreparationAdmissionTransport adopter &&
                adopter.ActivateSessionPreparationAdmission(lease);
    }

    private void RetireUnadoptedPreparationAdmission(PreparationAdmissionCapture? capture)
    {
        lock (gate)
            if (preparationAdmissionLease is { } lease && ReferenceEquals(lease.Admission, capture?.Admission) && !lease.IsAdopted)
                lease.Retire();
    }

    private FileTransferSessionPreparationExecution? CurrentPreparationExecutionLocked()
        => preparationExecution is { } execution && execution.Permit.Scope == sessionPreparation.Scope &&
            execution.Permit.Generation == sessionPreparation.LastPreparationGeneration ? execution : null;

    private FileTransferInitialTunaReadinessEvidence? RecheckPreparationEvidenceLocked(
        PreparationAdmissionCapture capture, FileTransferInitialTunaReadinessEvidence? evidence)
        => evidence is not null && IsPreparationCaptureCurrentLocked(capture) && IsPreparationEvidenceResourceCurrentLocked(capture) &&
            ReferenceEquals(sessionPreparation.ClaimedEvidence, evidence) ? evidence : null;
}
