using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using NLink.Core.Configuration;
using NLink.Infra.Nkn;

namespace NLink.Tests;

internal sealed class AttemptSingleSegmentProfile : IDisposable
{
    internal const string Variable = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
    private readonly string? previous;
    internal AttemptSingleSegmentProfile()
    {
#if !DEBUG
        throw new InvalidOperationException("offline_schedule_requires_debug");
#else
        if (!ReleaseOverridePolicy.UnsafeOverridesAllowed) throw new InvalidOperationException("offline_schedule_override_unavailable");
        previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, "1");
#endif
    }
    public void Dispose() => Environment.SetEnvironmentVariable(Variable, previous);
}

internal sealed record AttemptOfflineFrameHint(string Sender, OfflineFileFrameMetadata Frame)
{
    internal byte[] Encode()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        if (bytes.Length > 1024) throw new IOException("offline_schedule_descriptor_capacity");
        return bytes;
    }
    internal static AttemptOfflineFrameHint Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1024) throw new IOException("offline_schedule_descriptor_capacity");
        return JsonSerializer.Deserialize<AttemptOfflineFrameHint>(bytes) ?? throw new IOException("offline_schedule_descriptor_missing");
    }
    internal void Validate(string sender, string session, string transfer, byte[] bytes, bool canonicalTail = false)
    {
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        if (Sender != sender || Frame is null || Frame.SessionId != session || Frame.TransferId != transfer ||
            Frame.IsRepair || Frame.ChunkCount != 1 || Frame.StartChunkIndex < first || Frame.StartChunkIndex > first + 7 ||
            Frame.TransportEpoch != 0 || Frame.EnvelopeLength != bytes.Length ||
            Frame.EnvelopeSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new IOException("offline_schedule_descriptor_mismatch");
    }
}

// At most eight immutable ordinary descriptors, each consumed once. Repair and
// control frames never get a hint and follow the existing byte path unchanged.
internal sealed class AttemptOfflineFrameDescriptors(string sender, string session, string transfer,
    AttemptCanonicalTailProfile? canonicalTail = null)
{
    private readonly object gate = new();
    private readonly AttemptOfflineFrameHint?[] hints = new AttemptOfflineFrameHint?[8];
    private readonly bool[] consumed = new bool[8];
    private Exception? failure;

    internal void Observe(OfflineFileFrameMetadata frame)
    {
        lock (gate)
        {
            try
            {
                if (frame.IsRepair) return;
                var first = canonicalTail is null ? 0 : AttemptCanonicalTailProfile.FirstChunk;
                if (canonicalTail is not null && frame.StartChunkIndex < first)
                {
                    canonicalTail.Check();
                    if (frame.SessionId != session || frame.TransferId != transfer || frame.TransportEpoch != 0 ||
                        frame.StartChunkIndex < 0 || frame.ChunkCount is < 1 or > 3 ||
                        (long)frame.StartChunkIndex + frame.ChunkCount > first)
                        throw new IOException("canonical_tail_prefix_or_crossing_geometry");
                    return; // Original prefix bytes pass with no retained descriptor.
                }
                canonicalTail?.RequirePrefixConsumed();
                var hint = new AttemptOfflineFrameHint(sender, frame);
                _ = hint.Encode();
                var index = frame.StartChunkIndex - first;
                if (frame.SessionId != session || frame.TransferId != transfer || frame.ChunkCount != 1 ||
                    index is < 0 or > 7 || frame.TransportEpoch != 0 || hints[index] is not null ||
                    hints.Any(h => h?.Frame.EnvelopeSha256 == frame.EnvelopeSha256))
                    throw new IOException("offline_schedule_ordinary_geometry_or_duplicate");
                hints[index] = hint;
                Console.WriteLine("carrier-frontier-frame=described; chunk=" + frame.StartChunkIndex +
                    "; count=" + frame.ChunkCount + "; epoch=" + frame.TransportEpoch +
                    "; bytes=" + frame.EnvelopeLength + "; sha256=" + frame.EnvelopeSha256);
            }
            catch (Exception error) { failure ??= error; }
        }
    }
    internal byte[]? Consume(byte kind, NknBridgeChannel lane, byte[] bytes)
    {
        lock (gate)
        {
            Check();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var index = Array.FindIndex(hints, h => h?.Frame.EnvelopeSha256 == hash);
            if (index < 0) return null;
            if (consumed[index] || kind != 0 || lane != NknBridgeChannel.Bulk)
                throw new IOException("offline_schedule_duplicate_or_lane");
            canonicalTail?.RequirePrefixConsumed();
            hints[index]!.Validate(sender, session, transfer, bytes, canonicalTail is not null);
            consumed[index] = true;
            return hints[index]!.Encode();
        }
    }
    internal void Check() { lock (gate) { if (failure is not null) throw new IOException("offline_schedule_metadata_failed", failure); canonicalTail?.Check(); } }
    internal void VerifyComplete() { lock (gate) { Check(); if (consumed.Any(value => !value)) throw new IOException("offline_schedule_metadata_missing"); } }
}

// Receive-side faults happen after an actual complete pipe write/read. The
// reader never waits for a held frame, and releases use the same delivery path.
internal sealed class AttemptOfflineFrameSchedule : IDisposable
{
    internal sealed record HoldBudget(long CapturedCounter, long OldestExpiresAtTick, long CapturedTick, int Bytes, int Count);
    private sealed record Held(byte[] Bytes, Action<byte[]> Deliver, long Started, int DescriptorBytes);
    private readonly record struct HoldTiming(string Boundary, int ChunkIndex, long TickMilliseconds, long UtcTicks,
        long? StartedTick, long? ExpiresAtTick, int HeldCount, int HeldBytes, bool Failed, bool Disposed);
    private readonly object gate = new();
    private readonly Dictionary<int, Held> held = [];
    private readonly HoldTiming?[] holdTimings = new HoldTiming?[5];
    private bool holdTimingsWritten;
    private readonly bool[] seen = new bool[8];
    private readonly string sender, session, transfer;
    private readonly bool canonicalTail;
    private readonly int holdMilliseconds;
    private readonly Timer expiry;
    private readonly CancellationTokenRegistration cancellation;
    private Exception? failure;
    private bool disposed;
    private int releaseIndex;
    internal AttemptOfflineFrameSchedule(string sender, string session, string transfer, CancellationToken ct,
        bool latePublication = false, bool canonicalTail = false)
    {
#if !DEBUG
        if (latePublication) throw new InvalidOperationException("offline_schedule_requires_debug");
#endif
        if (canonicalTail)
        {
            AttemptCanonicalTailProfile.RequireSupported();
            if (!latePublication) throw new InvalidOperationException("canonical_tail_requires_late_publication");
        }
        this.sender = sender; this.session = session; this.transfer = transfer;
        this.canonicalTail = canonicalTail;
        holdMilliseconds = latePublication ? 4000 : 3000;
        Console.WriteLine("carrier-frontier-hold-policy=" + (latePublication ? "late-publication-fixed-4s-v1" : "frontier-fixed-3s-v1") +
            "; max_hold_ms=" + holdMilliseconds);
        expiry = new Timer(_ => { lock (gate) { Expire(Environment.TickCount64); } }, null, 10, 10);
        cancellation = ct.Register(() => { lock (gate) { Fail(new OperationCanceledException(ct)); } });
    }
    internal int PendingBytes { get { lock (gate) { return held.Values.Sum(h => h.Bytes.Length + h.DescriptorBytes); } } }
    // A conservative translation point, not a new hold clock. Reads and releases
    // never renew Held.Started or the profile's fixed per-frame expiry.
    internal HoldBudget ReadBudget()
    {
        lock (gate)
        {
            Check();
            if (held.Count == 0) throw new IOException("offline_schedule_no_remaining_hold");
            var counter = Stopwatch.GetTimestamp();
            var tick = Environment.TickCount64;
            return new(counter, held.Values.Min(h => h.Started) + holdMilliseconds, tick, PendingBytes, held.Count);
        }
    }
    internal bool AllOrdinarySeen { get { lock (gate) { Check(); return seen.All(value => value); } } }
    internal bool AnyOrdinarySeen { get { lock (gate) { Check(); return seen.Any(value => value); } } }
    internal bool IsCanonicalTail => canonicalTail;
    internal void Receive(AttemptOfflineFrameHint hint, byte[] bytes, Action<byte[]> deliver)
    {
        lock (gate)
        {
            try
            {
                Check(); hint.Validate(sender, session, transfer, bytes, canonicalTail);
                if (bytes.Length is < 1 or > 1048576) throw new IOException("offline_schedule_frame_capacity");
                var descriptorBytes = hint.Encode().Length;
                var index = hint.Frame.StartChunkIndex - (canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0);
                if (seen[index]) throw new IOException("offline_schedule_duplicate_ordinary");
                seen[index] = true;
                Console.WriteLine("carrier-frontier-frame=" + (index == 0 ? "drop" : index is 1 or 2 or 4 ? "hold" : "pass") +
                    "; chunk=" + hint.Frame.StartChunkIndex + "; bytes=" + bytes.Length + "; sha256=" + hint.Frame.EnvelopeSha256);
                if (index == 0) return;
                if (index is 1 or 2 or 4)
                {
                    if (held.Count >= 3 || PendingBytes + bytes.Length + descriptorBytes > 1048576)
                        throw new IOException("offline_schedule_held_capacity");
                    held.Add(index, new((byte[])bytes.Clone(), deliver, Environment.TickCount64, descriptorBytes));
                    CaptureHoldTiming(index == 1 ? 0 : index == 2 ? 1 : 2, "held", Environment.TickCount64,
                        hint.Frame.StartChunkIndex, held[index].Started);
                }
                else deliver(bytes);
            }
            catch (Exception error) { Fail(error); throw; }
        }
    }
    internal void Release(int index)
    {
        lock (gate)
        {
            try
            {
                Check();
                ReadOnlySpan<int> order = [2, 1, 4];
                if (releaseIndex >= order.Length || index != order[releaseIndex] || !held.Remove(index, out var frame))
                    throw new IOException("offline_schedule_release_order");
                releaseIndex++;
                frame.Deliver(frame.Bytes);
            }
            catch (Exception error) { Fail(error); throw; }
        }
    }
    internal void Check()
    {
        lock (gate)
        {
            Expire(Environment.TickCount64);
            if (failure is not null) throw new IOException("offline_schedule_failed", failure);
            if (disposed) throw new ObjectDisposedException(nameof(AttemptOfflineFrameSchedule));
        }
    }
    private void Expire(long now)
    {
        if (held.Values.Any(frame => now - frame.Started >= holdMilliseconds))
        {
            CaptureHoldTiming(4, "expired", now);
            Fail(new TimeoutException("offline_schedule_hold_expired"));
        }
    }
    // Called after the service-gated readiness read returns. This is a passive
    // scheduler snapshot, not another readiness/expiry check or an authority edge.
    internal void CaptureTailReadyTiming()
    {
        lock (gate) { CaptureHoldTiming(3, "exact-tail-ready-observed", Environment.TickCount64); }
    }
    private void CaptureHoldTiming(int slot, string boundary, long now, int chunk = -1, long? started = null)
    {
        try
        {
            if (!canonicalTail || holdTimingsWritten || holdTimings[slot] is not null) return;
            started ??= held.Count == 0 ? null : held.Values.Min(frame => frame.Started);
            holdTimings[slot] = new(boundary, chunk, now, DateTimeOffset.UtcNow.UtcTicks,
                started, started + holdMilliseconds, held.Count,
                held.Values.Sum(frame => frame.Bytes.Length + frame.DescriptorBytes), failure is not null, disposed);
        }
        catch (Exception) { /* Timing evidence must not alter the original scheduling/failure path. */ }
    }
    internal void WriteHoldTimings(TextWriter output)
    {
        try
        {
            HoldTiming?[] snapshot;
            lock (gate)
            {
                if (!canonicalTail || holdTimingsWritten) return;
                holdTimingsWritten = true;
                snapshot = (HoldTiming?[])holdTimings.Clone();
                Array.Clear(holdTimings);
            }
            // At most five scalar records, consumed before any formatting or I/O.
            // No Check/Expire here, including after failure or disposal.
            foreach (var timing in snapshot)
                if (timing is { } value)
                    output.WriteLine("carrier-canonical-hold-timing=" + JsonSerializer.Serialize(value));
        }
        catch (Exception) { /* One output attempt only; never replace the original exception. */ }
    }
    private void Fail(Exception error) { failure ??= error; held.Clear(); }
    public void Dispose()
    {
        lock (gate) { disposed = true; held.Clear(); expiry.Dispose(); }
        cancellation.Dispose();
    }
}
