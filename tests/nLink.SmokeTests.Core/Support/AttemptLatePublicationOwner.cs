using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.Tests;

// Immutable scheduling evidence only. In particular this object never receives
// a service, receiver context, route token, validator or result-producing callback.
internal sealed record AttemptLatePublicationWindow(Stamp Stall, long ChunkRelease, long CopyRelease, long Expires, int RetainedBytes)
{
    internal static AttemptLatePublicationWindow Create(Stamp stall, long deadlineUtc,
        AttemptOfflineFrameSchedule.HoldBudget hold, long now)
    {
        var f = Stopwatch.Frequency;
        if (stall.Frequency != f || stall.ProcessId != Environment.ProcessId || stall.ProcessCreationFileTime <= 0 ||
            stall.ClockInstanceId == Guid.Empty || stall.ObservationId == Guid.Empty || stall.CaptureSequence <= 0 ||
            stall.CounterBefore <= 0 || stall.CounterAfter < stall.CounterBefore || stall.CounterAfter - stall.CounterBefore >= f / 20 ||
            deadlineUtc != checked(stall.UtcTicks + TimeSpan.TicksPerMillisecond * 750) ||
            hold.Count != 2 || hold.Bytes is <= 0 or > 1048576 || hold.CapturedCounter > now || now < stall.CounterAfter)
            throw new IOException("late_publication_clock_or_hold_shape");
        // TickCount64 may be quantized. Both the translation bracket and the
        // additional 100 ms hold reserve are conservative; the hold's own timer
        // still enforces its original deadline.
        var holdEnd = checked(hold.CapturedCounter + (hold.OldestExpiresAtTick - hold.CapturedTick) * f / 1000);
        var chunk = checked(stall.CounterAfter + f / 4);
        var copies = checked(stall.CounterAfter + f * 4 / 5);
        var expires = Math.Min(checked(stall.CounterBefore + f * 5 / 4), holdEnd - f / 10);
        if (now >= chunk || copies + f / 5 >= holdEnd || copies + f / 10 >= expires)
        {
            // Failure-only scheduling diagnostics survive the original nested
            // exception log. No fresh clock read, authority or owner is created.
            throw new IOException("late_publication_original_budget_unavailable; budget_json=" + JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Stall = stall,
                DeadlineUtcTicks = deadlineUtc,
                Hold = hold,
                NowCounter = now,
                HoldEndCounter = holdEnd,
                ChunkReleaseCounter = chunk,
                CopyReleaseCounter = copies,
                ExpiresCounter = expires,
                ArmMarginCounter = chunk - now,
                HoldReserveMarginCounter = holdEnd - (copies + f / 5),
                ExpiryMarginCounter = expires - (copies + f / 10),
                LateArm = now >= chunk,
                HoldReserveUnavailable = copies + f / 5 >= holdEnd,
                ExpiryAllowanceUnavailable = copies + f / 10 >= expires
            }));
        }
        return new(stall, chunk, copies, expires, hold.Bytes);
    }

    internal void CheckWrite(Stamp write)
    {
        var s = Stall; var f = s.Frequency;
        if (write.ProcessId != s.ProcessId || write.ProcessCreationFileTime != s.ProcessCreationFileTime ||
            write.ClockInstanceId != s.ClockInstanceId || write.ObservationId != s.ObservationId || write.Frequency != f ||
            write.CaptureSequence <= s.CaptureSequence || write.CounterBefore < ChunkRelease || write.CounterAfter < write.CounterBefore ||
            write.UtcTicks < s.UtcTicks || write.UtcTicks >= s.UtcTicks + TimeSpan.TicksPerMillisecond * 750 ||
            (write.CounterAfter - s.CounterBefore + 2) * 1000m >= 750m * f ||
            CopyRelease + f / 10 >= write.CounterBefore + f)
            throw new IOException("late_publication_original_write_unavailable");
    }
}

internal sealed record AttemptLatePublicationIdentity(string Session, string Transfer, string Source, string Peer,
    long TransportEpoch, long LiveRouteEpoch, long LegGeneration, long BridgeGeneration);

internal sealed class AttemptLatePublicationOwner : IOfflinePublicationDelay, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly AttemptLatePublicationWindow window;
    private readonly bool canonicalTail;
    private AttemptLatePublicationIdentity? identity;
    private readonly CancellationTokenSource lifetime;
    private AttemptOfflinePublicationDelay? delay;
    private FileTransferReceiverStateFrameV6? selected;
    private string? failure;
    private bool nativeChecked, disposed;
    private readonly Lazy<Task> cleanup;

    internal AttemptLatePublicationOwner(AttemptLatePublicationWindow window, AttemptLatePublicationIdentity identity, CancellationToken ct,
        bool canonicalTail = false)
    {
        if (canonicalTail) AttemptCanonicalTailProfile.RequireSupported();
        this.window = window; this.identity = identity; this.canonicalTail = canonicalTail;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, (window.Expires - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency)));
        Lifetime = lifetime.Token;
        cleanup = new(CleanupAsync);
    }
    public CancellationToken Lifetime { get; }
    internal OfflinePublicationDelayRegistration Registration()
    {
        lock (gate) { Check(); return new(identity!.Session, identity.Transfer, Bind, Fail); }
    }
    private IOfflinePublicationDelay Bind(OfflinePublicationMetadata item)
    {
        try
        {
            lock (gate)
            {
                Check();
                var identity = this.identity!;
                if (delay is not null || item.State is not FileTransferReceiverStateFrameV6 state ||
                    state.SessionId != identity.Session || state.TransferId != identity.Transfer || item.Source != identity.Source ||
                    item.TransportEpoch != identity.TransportEpoch || item.LiveRouteEpoch != identity.LiveRouteEpoch ||
                    item.LegGeneration != identity.LegGeneration || item.BridgeRecoveryGeneration != identity.BridgeGeneration ||
                    item.Direction != FileTransferDirection.Inbound)
                    throw new IOException("late_publication_source_or_competing_state");
                selected = state with { MissingRanges = state.MissingRanges.Select(r => r with { }).ToImmutableArray() };
                delay = new(selected, identity.Source, identity.Peer, window.CopyRelease, window.Expires, window.RetainedBytes, Lifetime, canonicalTail);
                delay.Bind(item);
                return this;
            }
        }
        catch { Fail("late_publication_bind_failed"); throw; }
    }
    // Called only by the separate service-gated reader, using frozen native
    // frame/write facts. No hook may read or retain that reader's context.
    internal bool CheckNativeWrite(Stamp write, FileTransferReceiverStateFrameV6 frame)
    {
        lock (gate)
        {
            Check();
            window.CheckWrite(write);
            if (selected is null) return false;
            if (Stopwatch.GetTimestamp() >= window.CopyRelease ||
                frame with { MissingRanges = selected.MissingRanges } != selected ||
                !frame.MissingRanges.SequenceEqual(selected.MissingRanges))
                throw new IOException("late_publication_native_frame_changed_or_late");
            nativeChecked = true;
            return true;
        }
    }
    public async Task BeforeCopyAsync(string role, long? timeoutStarted, CancellationToken ct)
    {
        try
        {
            await delay!.BeforeCopyAsync(role, timeoutStarted, ct);
            lock (gate)
            {
                Check();
                if (!nativeChecked) throw new IOException("late_publication_native_write_not_checked");
            }
        }
        catch { Fail("late_publication_copy_failed"); throw; }
    }
    public void CopyFinished(string role, bool succeeded) => delay!.CopyFinished(role, succeeded);
    internal void ObserveWrite(AttemptPipeWriteObservation write) { lock (gate) { delay?.ObserveWrite(write); } }
    internal bool CopiesFinished { get { lock (gate) { Check(); return delay is not null && delay.Pending == 0; } } }
    internal void VerifyComplete() { lock (gate) { Check(); if (!nativeChecked || delay is null) throw new IOException("late_publication_no_native_write"); delay.VerifyComplete(); } }
    internal void Check()
    {
        lock (gate)
            if (disposed || Lifetime.IsCancellationRequested || failure is not null || delay?.Failure is not null)
                throw new IOException(failure ?? delay?.Failure ?? "late_publication_retired");
    }
    internal void Fail(string reason)
    {
        lock (gate) { failure ??= reason; if (disposed) return; }
        try { lifetime.Cancel(); } catch (ObjectDisposedException) when (disposed) { }
    }
    public ValueTask DisposeAsync() => new(cleanup.Value);
    private async Task CleanupAsync()
    {
        AttemptOfflinePublicationDelay? owned;
        lock (gate) { disposed = true; owned = delay; }
        lifetime.Cancel();
        if (owned is not null) await owned.DisposeAsync();
        // Keep only the retired primitive's counters: a late failed role must
        // still report its actual failure, never dereference cleared metadata.
        lock (gate) { selected = null; identity = null; }
        lifetime.Dispose();
    }
}
