using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport : IFileTransferSessionPreparationLeaseProvider,
    IFileTransferSessionPreparationAdmissionTransport
{
    private PreparationAdmissionWork? preparationAdmissionWork;
    private PreparationAdmissionWork? accelerationPreparationAdmission;
    internal Action? SessionPreparationBeforeRetentionForTests;
    internal Action? SessionPreparationAfterRetentionForTests;
    internal Action? SessionPreparationBeforeActivationForTests;
    internal Action? SessionPreparationAfterActivationForTests;

    private sealed class PreparationAdmissionWork(PreparationLaneWork preparation,
        FileTransferSessionPreparationAdmissionLease authority)
    {
        public readonly object Gate = new();
        public readonly PreparationLaneWork Preparation = preparation;
        public readonly FileTransferSessionPreparationAdmissionLease Authority = authority;
        public readonly string Identity = $"admission:{authority.Id:N}";
        public NknTunaLaneLease? Lease;
        public bool Activated;
        public CancellationTokenRegistration Retirement;
    }

    bool IFileTransferSessionPreparationLeaseProvider.TryObserveSessionPreparationLease(
        FileTransferSessionPreparationScope scope, long snapshotGeneration,
        out FileTransferSessionPreparationLeaseObservation? observation)
    {
        observation = null;
        PreparationLaneWork? work;
        lock (preparationExecutionGate) work = preparationLaneWork;
        if (work is null || work.Execution.Permit.Scope != scope || work.Execution.Permit.Generation != snapshotGeneration ||
            !work.Execution.IsResourceCurrent || !IsSessionPreparationInputLifetimeCurrent(work.Attachment, work.Epoch)) return false;
        NknTunaLaneLease? lease;
        lock (work.Gate) lease = work.Lease;
        if (lease is null || work.Lane.CaptureCurrentLaneLease() != lease || !work.Lane.IsAvailable ||
            (work.Lane.SupportedLanes & NknAccelerationLaneKind.File) == 0) return false;
        var run = work.Execution.Permit.LocalIsPayer ? lease.Listener?.ProcessRunId : lease.DialerOwner?.RunId;
        if (string.IsNullOrWhiteSpace(run) || !work.Execution.IsResourceCurrent ||
            !IsSessionPreparationInputLifetimeCurrent(work.Attachment, work.Epoch)) return false;
        observation = new(scope, snapshotGeneration, lease.ClientGeneration, run, true, true, true, DateTimeOffset.UtcNow);
        return true;
    }

    FileTransferSessionPreparationAdmissionLease? IFileTransferSessionPreparationAdmissionTransport.RetainSessionPreparationForAdmission(
        FileTransferSessionPreparationAdmission admission, FileTransferInitialTunaReadinessEvidence evidence)
    {
        PreparationLaneWork? preparation;
        lock (preparationExecutionGate) preparation = preparationLaneWork;
        if (preparation is null || !IsSessionPreparationInputLifetimeCurrent(preparation.Attachment, preparation.Epoch)) return null;
        var authority = preparation.Attachment.Authority.BindAdmission(preparation.Attachment, preparation.Epoch, admission, evidence);
        if (authority is null || !ReferenceEquals(authority.Execution, preparation.Execution)) return null;
        PreparationAdmissionWork work;
        lock (preparationExecutionGate)
        {
            if (preparationAdmissionWork is { } existing && ReferenceEquals(existing.Authority, authority))
                return existing.Authority.IsRetained ? authority : null;
            work = new(preparation, authority);
            preparationAdmissionWork = work;
        }
        work.Retirement = authority.Cancellation.Register(() => CleanupPreparationAdmission(work));
        NknTunaLaneLease? retained = null;
        var accepted = false;
        try
        {
            SessionPreparationBeforeRetentionForTests?.Invoke();
            NknTunaLaneLease? original;
            lock (preparation.Gate) original = preparation.Lease;
            if (!authority.IsCurrent || !preparation.Execution.IsAdmissionCurrent(admission) || original is null ||
                !preparation.Lane.TryTransferPreparationLaneLease(original, work.Identity, activate: false, out retained) || retained is null) return null;
            SessionPreparationAfterRetentionForTests?.Invoke();
            lock (work.Gate)
            {
                if (!authority.IsCurrent || preparation.Lane.CaptureCurrentLaneLease() != retained || !authority.ReportRetained()) return null;
                work.Lease = retained;
                accepted = true;
            }
            return authority;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=session_preparation_retention_failed; error={ex.GetType().Name}; authority_effect=none");
            return null;
        }
        finally
        {
            if (!accepted)
            {
                if (retained is not null) StopPreparationLaneLease(preparation, retained);
                authority.Retire();
                preparation.Execution.Retire();
            }
        }
    }

    bool IFileTransferSessionPreparationAdmissionTransport.ActivateSessionPreparationAdmission(
        FileTransferSessionPreparationAdmissionLease authority)
    {
        PreparationAdmissionWork? work;
        lock (preparationExecutionGate) work = preparationAdmissionWork;
        if (work is null || !ReferenceEquals(work.Authority, authority) || !authority.IsAdopted ||
            !IsSessionPreparationInputLifetimeCurrent(work.Preparation.Attachment, work.Preparation.Epoch)) return false;
        NknTunaLaneLease? activated = null;
        var accepted = false;
        try
        {
            SessionPreparationBeforeActivationForTests?.Invoke();
            lock (work.Gate)
            {
                if (work.Activated)
                {
                    accepted = IsPreparationAdmissionResourceCurrent(work);
                    return accepted;
                }
                if (work.Lease is not { } retained || !authority.IsAdopted ||
                    !work.Preparation.Lane.TryTransferPreparationLaneLease(retained, work.Identity, activate: true, out activated) ||
                    activated is null || !authority.IsAdopted) return false;
                work.Lease = activated;
                work.Activated = true;
            }
            SessionPreparationAfterActivationForTests?.Invoke();
            lock (accelerationGate)
            {
                if (!IsPreparationAdmissionResourceCurrent(work)) return false;
                accelerationSessionId = authority.Admission.Scope.SessionId;
                accelerationNegotiatedLanes = NknAccelerationLaneKind.File;
                accelerationPreparationAdmission = work;
                accepted = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn("NKN.Tuna", $"event=session_preparation_adoption_failed; error={ex.GetType().Name}; authority_effect=none");
            return false;
        }
        finally
        {
            if (!accepted)
            {
                if (activated is not null) StopPreparationLaneLease(work.Preparation, activated);
                authority.Retire();
            }
        }
    }

    private static bool IsPreparationAdmissionResourceCurrent(PreparationAdmissionWork work)
        => work.Authority.IsAdopted && Volatile.Read(ref work.Lease) is { } lease &&
            work.Preparation.Lane.CaptureCurrentLaneLease() == lease;

    private static void CleanupPreparationAdmission(PreparationAdmissionWork work)
    {
        NknTunaLaneLease? retired;
        lock (work.Gate) { retired = work.Lease; work.Lease = null; }
        if (retired is not null) StopPreparationLaneLease(work.Preparation, retired);
        work.Retirement.Unregister();
    }
}
