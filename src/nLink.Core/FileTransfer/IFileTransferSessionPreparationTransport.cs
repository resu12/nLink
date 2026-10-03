using NLink.Core.SessionSecurity;

namespace NLink.Core.FileTransfer;

internal interface IFileTransferSessionPreparationTransport
{
    FileTransferSessionPreparationLifecycle SessionPreparationLifecycle { get; }
}

internal interface IFileTransferSessionPreparationLeaseProvider
{
    bool TryObserveSessionPreparationLease(FileTransferSessionPreparationScope scope,
        long snapshotGeneration, out FileTransferSessionPreparationLeaseObservation? observation);
}

internal interface IFileTransferSessionPreparationAdmissionTransport
{
    FileTransferSessionPreparationAdmissionLease? RetainSessionPreparationForAdmission(
        FileTransferSessionPreparationAdmission admission, FileTransferInitialTunaReadinessEvidence evidence);
    bool ActivateSessionPreparationAdmission(FileTransferSessionPreparationAdmissionLease lease);
}

internal interface IFileTransferSessionPreparationAuthority
{
    FileTransferFallbackListenerPreparationPermit? BeginFallbackListenerPreparation(
        FileTransferSessionPreparationAttachment attachment, FileTransferSessionPreparationSecurityEpoch epoch) => null;
    bool IsFallbackListenerPreparationCurrent(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferFallbackListenerPreparationPermit permit) => false;
    FileTransferAccelerationRequestSelection SelectRequest(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch)
        => new(FileTransferAccelerationRequestPurpose.Unavailable, "preparation_selection_unsupported");
    FileTransferSessionPreparationDecision Begin(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, string nonce, long payerDecisionId,
        bool localIsPayer, DateTimeOffset deadlineUtc, bool listenerStartup = false);
    FileTransferSessionPreparationDecision BeginHandshake(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit);
    FileTransferSessionPreparationDecision Complete(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit,
        FileTransferSessionPreparationProof proof);
    bool IsCurrent(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit);
    FileTransferSessionPreparationExecution? BindExecution(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit) => null;
    FileTransferSessionPreparationAdmissionLease? BindAdmission(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationAdmission admission,
        FileTransferInitialTunaReadinessEvidence evidence) => null;
    FileTransferSessionPreparationDecision Retire(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferSessionPreparationPermit permit);
    FileTransferSessionPreparationObservation? Observe(FileTransferSessionPreparationAttachment attachment,
        FileTransferSessionPreparationSecurityEpoch epoch, FileTransferDirection direction, DateTimeOffset requestedUtc);
}

// Lifetime evidence only. No route/runtime authority is created here and no
// security diagnostic IDs, fingerprints, or logs are consumed.
internal sealed class FileTransferSessionPreparationSecurityEpoch(long generation, string sessionId)
{
    private readonly object gate = new();
    private int retired;
    private SessionSecurityState? security;
    private FileTransferSessionPreparationExecution? execution;
    private FileTransferSessionPreparationAdmissionLease? admissionLease;
    public Guid Id { get; } = Guid.NewGuid();
    public long Generation { get; } = generation;
    public string SessionId { get; } = sessionId;
    public bool IsCurrent => Volatile.Read(ref retired) == 0;
    public SessionSecurityState? Security => Volatile.Read(ref security);
    internal void Retire()
    {
        lock (gate)
        {
            Interlocked.Exchange(ref retired, 1);
            execution?.Retire();
            admissionLease?.Retire();
        }
    }

    internal bool BindExecution(FileTransferSessionPreparationExecution candidate)
    {
        lock (gate)
        {
            if (!IsCurrent) return false;
            if (!ReferenceEquals(execution, candidate)) execution?.Retire();
            execution = candidate;
            return true;
        }
    }

    internal bool BindAdmission(FileTransferSessionPreparationAdmissionLease candidate)
    {
        lock (gate)
        {
            if (!IsCurrent) return false;
            if (admissionLease is not null && !ReferenceEquals(admissionLease, candidate)) return false;
            admissionLease = candidate;
            return true;
        }
    }

    // Core-state decisions only: never call a transport, lane, or network operation
    // here. This serializes their adoption with key/approval retirement.
    internal bool TryUse(Action coreStateOnly)
    {
        lock (gate)
        {
            if (!IsCurrent) return false;
            coreStateOnly();
            return true;
        }
    }

    internal void ObserveSecurity(SessionSecurityState state)
    {
        lock (gate)
        {
            var prior = Security;
            if (!IsCurrent || state.SessionId?.Value != SessionId || !state.HandshakeCompleted ||
                state.HandshakeState != SessionHandshakeState.Verified ||
                !state.HasCapability(CapabilityGrant.FileTransfer, DateTimeOffset.UtcNow) ||
                state.HelpeeAddress is null || state.HelperAddress is null ||
                prior is not null && (prior.HelpeeAddress != state.HelpeeAddress ||
                    prior.HelperAddress != state.HelperAddress || prior.ApprovalExpiresAt != state.ApprovalExpiresAt))
            {
                Retire();
                return;
            }
            Volatile.Write(ref security, state);
        }
    }
}

internal sealed class FileTransferSessionPreparationAttachment(
    FileTransferSessionPreparationLifecycle lifecycle, IFileTransferSessionPreparationAuthority authority,
    long baselineGeneration) : IDisposable
{
    private readonly object gate = new();
    private int retired;
    private FileTransferSessionPreparationExecution? execution;
    private FileTransferSessionPreparationAdmissionLease? admissionLease;
    public FileTransferSessionPreparationLifecycle Lifecycle { get; } = lifecycle;
    public IFileTransferSessionPreparationAuthority Authority { get; } = authority;
    public long BaselineGeneration { get; } = baselineGeneration;
    public bool IsCurrent => Volatile.Read(ref retired) == 0;
    internal bool BindAdmission(FileTransferSessionPreparationAdmissionLease candidate)
    {
        lock (gate)
        {
            if (!IsCurrent) return false;
            if (admissionLease is not null && !ReferenceEquals(admissionLease, candidate)) admissionLease.Retire();
            admissionLease = candidate;
            return true;
        }
    }
    internal bool BindExecution(FileTransferSessionPreparationExecution candidate)
    {
        lock (gate)
        {
            if (!IsCurrent) return false;
            if (!ReferenceEquals(execution, candidate)) execution?.Retire();
            execution = candidate;
            return true;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            Interlocked.Exchange(ref retired, 1);
            execution?.Retire();
            admissionLease?.Retire();
        }
        Lifecycle.Release(this);
    }
}

internal sealed class FileTransferSessionPreparationLifecycle
{
    private readonly object gate = new();
    private long generation;
    private FileTransferSessionPreparationSecurityEpoch? current;
    private FileTransferSessionPreparationAttachment? attachment;
    public long Generation => Interlocked.Read(ref generation);
    public FileTransferSessionPreparationSecurityEpoch? Current => Volatile.Read(ref current);
    public FileTransferSessionPreparationAttachment? Attachment => Volatile.Read(ref attachment);

    public FileTransferSessionPreparationAttachment? TryAttach(IFileTransferSessionPreparationAuthority authority)
    {
        lock (gate)
        {
            if (attachment is not null) return null;
            var next = new FileTransferSessionPreparationAttachment(this, authority, generation);
            Volatile.Write(ref attachment, next);
            return next;
        }
    }

    internal void Release(FileTransferSessionPreparationAttachment expected)
    {
        lock (gate)
        {
            if (ReferenceEquals(attachment, expected)) Volatile.Write(ref attachment, null);
        }
    }

    // Call only at actual control-key installation. A null session means an
    // unbound install: invalidate prior preparation but do not make a new epoch.
    public void InstallKey(string? approvedSessionId)
    {
        lock (gate)
        {
            current?.Retire();
            var next = checked(++generation);
            Volatile.Write(ref current, string.IsNullOrWhiteSpace(approvedSessionId)
                ? null : new FileTransferSessionPreparationSecurityEpoch(next, approvedSessionId));
        }
    }

    public void ResetKey()
    {
        lock (gate)
        {
            current?.Retire();
            Volatile.Write(ref current, null);
        }
    }

    public void ObserveSecurity(SessionSecurityState state)
    {
        lock (gate) { current?.ObserveSecurity(state); }
    }
}
