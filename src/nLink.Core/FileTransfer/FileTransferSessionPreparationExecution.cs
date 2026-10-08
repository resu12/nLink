namespace NLink.Core.FileTransfer;

// Issued by the service only after a Coordinator permit decision. This is a
// revocable physical-work lifetime, not file-route/runtime authority.
internal sealed class FileTransferSessionPreparationExecution
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer expiry;
    private DateTimeOffset expiresUtc;
    private FileTransferSessionPreparationReadiness? readiness;
    private FileTransferSessionPreparationAdmission? admission;

    internal FileTransferSessionPreparationExecution(FileTransferSessionPreparationPermit permit)
    {
        Permit = permit;
        expiresUtc = permit.DeadlineUtc;
        expiry = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public FileTransferSessionPreparationPermit Permit { get; }
    public CancellationToken Cancellation => cancellation.Token;
    public FileTransferSessionPreparationAdmission? Admission
    {
        get { lock (gate) return admission; }
    }
    public bool AllowsPreparation
    {
        get { lock (gate) return IsResourceCurrentUnsafe() && admission is null; }
    }
    public bool IsResourceCurrent
    {
        get { lock (gate) return IsResourceCurrentUnsafe(); }
    }
    public bool IsAdmissionCurrent(FileTransferSessionPreparationAdmission expected)
    {
        lock (gate) return IsResourceCurrentUnsafe() && ReferenceEquals(admission, expected);
    }

    internal void Synchronize(FileTransferSessionPreparationState state)
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return;
            if (ReferenceEquals(state.Permit, Permit) && state.Phase is
                FileTransferSessionPreparationPhase.Starting or FileTransferSessionPreparationPhase.Pending or FileTransferSessionPreparationPhase.Ready)
            {
                readiness = state.Readiness;
                expiresUtc = readiness?.ExpiresUtc ?? state.Handshake?.DeadlineUtc ?? Permit.DeadlineUtc;
            }
            else if (state.Phase == FileTransferSessionPreparationPhase.AdmissionReserved &&
                state.Admission is { } reserved)
            {
                admission = reserved;
                if (readiness is null || !ReferenceEquals(reserved.Readiness, readiness))
                {
                    RetireUnsafe();
                    return;
                }
                // Admission closes all preparation callbacks, but preserves
                // completed resources for this exact Core reservation to claim.
            }
            else
            {
                RetireUnsafe();
                return;
            }
            ScheduleExpiryUnsafe();
        }
    }

    internal void Retire() { lock (gate) RetireUnsafe(); }

    // Core-state transitions only. Completion and deadline retirement must
    // linearize together; this never runs transport or cleanup callbacks.
    internal void ApplyStateTransition(Action coreStateOnly)
    {
        lock (gate) coreStateOnly();
    }

    private bool IsResourceCurrentUnsafe()
        => !cancellation.IsCancellationRequested && DateTimeOffset.UtcNow < expiresUtc;

    private void Expire()
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return;
            // An already queued pending-phase timer cannot retire later ready
            // evidence before the Coordinator's current readiness deadline.
            ScheduleExpiryUnsafe();
        }
    }

    private void ScheduleExpiryUnsafe()
    {
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) RetireUnsafe();
        else expiry.Change(remaining, Timeout.InfiniteTimeSpan);
    }

    private void RetireUnsafe()
    {
        if (cancellation.IsCancellationRequested) return;
        expiry.Dispose();
        // CancelAsync changes IsCancellationRequested synchronously but runs
        // registered lane cleanup asynchronously, never on the Core gate owner.
        // Adapter cleanup registrations must contain their own failure handling.
        _ = cancellation.CancelAsync();
    }
}
