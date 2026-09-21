namespace NLink.Core.FileTransfer;

// Core-issued ownership of an already prepared resource for one real admission.
// Pending retention is not route authority; adoption additionally requires the
// actual Coordinator publication and follows that transfer's lifetime.
internal sealed class FileTransferSessionPreparationAdmissionLease
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer expiry;
    private CancellationTokenRegistration transferRetirement;
    private long expiresTicks;
    private RoutePublicationToken? publication;
    private int retained;
    private int adopted;

    internal FileTransferSessionPreparationAdmissionLease(FileTransferSessionPreparationExecution execution,
        FileTransferSessionPreparationAdmission admission, FileTransferInitialTunaReadinessEvidence evidence)
    {
        Execution = execution;
        Admission = admission;
        Evidence = evidence;
        expiresTicks = admission.Readiness!.ExpiresUtc.UtcTicks;
        expiry = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ScheduleExpiry();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public FileTransferSessionPreparationExecution Execution { get; }
    public FileTransferSessionPreparationAdmission Admission { get; }
    public FileTransferInitialTunaReadinessEvidence Evidence { get; }
    public CancellationToken Cancellation => cancellation.Token;
    public bool IsCurrent => !cancellation.IsCancellationRequested && DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref expiresTicks);
    public bool IsRetained => Volatile.Read(ref retained) != 0 && IsCurrent;
    public bool HasPublication => Volatile.Read(ref adopted) != 0;
    public bool IsAdopted => HasPublication && IsCurrent;

    // A physical observation only. It cannot produce a route publication.
    public bool ReportRetained()
    {
        lock (gate)
        {
            if (!IsCurrent || !Execution.IsAdmissionCurrent(Admission)) return false;
            Volatile.Write(ref retained, 1);
            return true;
        }
    }

    internal bool Adopt(FileTransferCoordinatorState state, CancellationToken transferLifetime)
    {
        lock (gate)
        {
            if (!IsRetained || transferLifetime.IsCancellationRequested ||
                state.CurrentRoutePublicationToken is not { } token ||
                !FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, token, out _) ||
                token.SessionId != Admission.Scope.SessionId || token.TransferId != Admission.TransferId ||
                token.Direction != Admission.Direction || token.Route != FileTransferRoute.FileTunaV4 || token.ProtocolVersion != 4)
                return false;
            if (publication is { } existing) return existing == token && IsAdopted;
            publication = token;
            Interlocked.Exchange(ref expiresTicks, Admission.Scope.ApprovalExpiresUtc.UtcTicks);
            transferRetirement = transferLifetime.Register(Retire);
            // Register may synchronously observe cancellation that won after
            // the initial check. Do not reschedule an already retired timer.
            if (!IsCurrent)
            {
                transferRetirement.Unregister();
                return false;
            }
            Volatile.Write(ref adopted, 1);
            ScheduleExpiry();
            // Prepared cleanup still carries its old physical receipt. The
            // adapter must rotate that receipt before reporting retention.
            Execution.Retire();
            return IsAdopted;
        }
    }

    public void Retire()
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return;
            expiry.Dispose();
            transferRetirement.Unregister();
            _ = cancellation.CancelAsync();
        }
    }

    private void Expire() { lock (gate) { if (!cancellation.IsCancellationRequested) ScheduleExpiry(); } }
    private void ScheduleExpiry()
    {
        var remaining = new DateTimeOffset(Interlocked.Read(ref expiresTicks), TimeSpan.Zero) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) Retire();
        else expiry.Change(remaining, Timeout.InfiniteTimeSpan);
    }
}
