using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.Tests;

// One scheduling fault, not a receiver-state evaluator. Integration must supply
// native selection/currentness and the earliest original hold/publication bound.
internal sealed class AttemptOfflinePublicationDelay : IOfflinePublicationDelay, IAsyncDisposable
{
    private readonly object gate = new();
    private string source, peer;
    private readonly long releaseAt;
    private readonly int retainedBytes;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration cancellation;
    private readonly Task release;
    private readonly TaskCompletionSource[] finished = Enumerable.Range(0, 3)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
    private readonly bool[] started = new bool[3], succeeded = new bool[3];
    private readonly AttemptPipeWriteObservation?[] writes = new AttemptPipeWriteObservation?[3];
    private FileTransferReceiverStateFrameV6? expected;
    private OfflinePublicationMetadata? metadata;
    private string? controlHash, bulkHash, failure;
    private bool bound, disposed, verified;
    private readonly Lazy<Task> cleanup;

    internal AttemptOfflinePublicationDelay(FileTransferReceiverStateFrameV6 expected, string source, string peer,
        long releaseAt, long expiresAt, int retainedBytes, CancellationToken ct, bool canonicalTail = false)
    {
        if (canonicalTail) AttemptCanonicalTailProfile.RequireSupported();
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        if (expected.MissingRanges.Count > 8 || expected.ContiguousCommittedChunkIndex != first + 4 ||
            expected.DurableReceivedHighestChunkIndex != first + 7 || expected.TerminalReady || expected.TransferPaused ||
            (canonicalTail && expected.BytesCommitted != AttemptCanonicalTailProfile.CommittedAfterWrite) ||
            retainedBytes < 0 || JsonSerializer.SerializeToUtf8Bytes(expected).Length > 4096 ||
            Encoding.UTF8.GetByteCount(source) + Encoding.UTF8.GetByteCount(peer) > 4096 ||
            releaseAt <= Stopwatch.GetTimestamp() ||
            releaseAt + Stopwatch.Frequency / 10 >= expiresAt)
            throw new InvalidOperationException("publication_arm_unavailable");
        this.expected = expected with { MissingRanges = expected.MissingRanges.Select(r => r with { }).ToImmutableArray() };
        this.source = source; this.peer = peer; this.releaseAt = releaseAt; this.retainedBytes = retainedBytes;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Lifetime = lifetime.Token;
        cancellation = Lifetime.Register(() => { lock (gate) { if (!verified) failure ??= "publication_cancelled_or_expired"; } });
        lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, (expiresAt - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency)));
        release = ReleaseAsync();
        cleanup = new(CleanupAsync);
    }

    public CancellationToken Lifetime { get; }
    internal int Pending { get { lock (gate) return Enumerable.Range(0, 3).Count(i => started[i] && !finished[i].Task.IsCompleted); } }
    internal string? Failure { get { lock (gate) return failure; } }

    internal OfflinePublicationDelayRegistration Registration()
    {
        lock (gate) { Check(); return new(expected!.SessionId, expected.TransferId, Bind, Fail); }
    }

    internal IOfflinePublicationDelay Bind(OfflinePublicationMetadata item)
    {
        try
        {
            lock (gate)
            {
                Check();
                if (bound || item.State is not FileTransferReceiverStateFrameV6 state || expected is null ||
                    state with { MissingRanges = expected.MissingRanges } != expected ||
                    !state.MissingRanges.SequenceEqual(expected.MissingRanges) || item.Source != source ||
                    item.ControlDestination != peer || string.IsNullOrWhiteSpace(item.BulkDestination) ||
                    item.TransportEpoch != state.TransportEpoch || item.Route != FileTransferRouteResolver.PostTunaFallbackV6Token ||
                    item.Protocol != 6 || !item.PeerVisibleRequired || item.IgnoreCallerCancellation ||
                    item.PeerCopyAttempts != 1 || item.CopyTimeout != TimeSpan.FromMilliseconds(750) || item.Bulk is null)
                    throw new InvalidOperationException("publication_shape_or_identity");
                foreach (var descriptor in new[] { item.Control, item.Bulk })
                    if (descriptor.Length is < 1 or > 16384 || descriptor.Sha256.Length != 64 || descriptor.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
                        string.IsNullOrWhiteSpace(descriptor.MessageId) || JsonSerializer.SerializeToUtf8Bytes(descriptor).Length > 1024)
                        throw new InvalidOperationException("publication_descriptor_capacity");
                // Include derived state fields (base-type JSON omits them). Reserve metadata,
                // source serialization, two encrypted originals and three serialized copy buffers.
                if (JsonSerializer.SerializeToUtf8Bytes(item).Length > 8192 || JsonSerializer.SerializeToUtf8Bytes(state).Length > 4096 ||
                    (long)retainedBytes + 65536 + 3L * item.Control.Length + 2L * item.Bulk.Length > 1048576 ||
                    item.Control.MessageId == item.Bulk.MessageId || item.Control.Sha256 == item.Bulk.Sha256)
                    throw new InvalidOperationException("publication_aggregate_capacity_or_identity");
                metadata = item; controlHash = item.Control.Sha256; bulkHash = item.Bulk.Sha256; bound = true;
                return this;
            }
        }
        catch { Fail("publication_registration_failed"); throw; }
    }

    public async Task BeforeCopyAsync(string role, long? timeoutStarted, CancellationToken ct)
    {
        try
        {
            lock (gate)
            {
                Check();
                var index = Role(role);
                if (!bound || started[index] || (index == 0) != (timeoutStarted is null) ||
                    (timeoutStarted is { } start && releaseAt + Stopwatch.Frequency / 10 >= start + Stopwatch.Frequency * 3 / 4))
                    throw new InvalidOperationException("publication_copy_shape_or_budget");
                started[index] = true;
            }
            await release.WaitAsync(ct);
            lock (gate) Check();
            ct.ThrowIfCancellationRequested();
        }
        catch { Fail("publication_copy_wait_failed"); throw; }
    }

    public void CopyFinished(string role, bool success)
    {
        if (role is not ("control_queue" or "control_copy" or "bulk_copy")) { Fail("publication_unknown_role"); return; }
        bool failed;
        lock (gate)
        {
            var index = Role(role);
            failed = !started[index] || finished[index].Task.IsCompleted || !success;
            succeeded[index] = success && !failed;
            finished[index].TrySetResult();
        }
        if (failed) Fail("publication_copy_failed");
    }

    internal void ObserveWrite(AttemptPipeWriteObservation write)
    {
        try
        {
            lock (gate)
            {
                if (write.Sha256 != controlHash && write.Sha256 != bulkHash) return; // unrelated traffic
                Check();
                var control = write.Sha256 == controlHash;
                var descriptor = control ? metadata!.Control : metadata!.Bulk!;
                if (write.Source != source || write.Peer != peer || write.Kind != 0 || write.Length != descriptor.Length ||
                    write.Lane != (control ? NknBridgeChannel.Control : NknBridgeChannel.Bulk) || write.Timestamp < releaseAt ||
                    !release.IsCompletedSuccessfully)
                    throw new InvalidOperationException("publication_write_identity_or_early");
                if (!write.Completed)
                {
                    if (writes.Any(w => w?.WriteId == write.WriteId)) throw new InvalidOperationException("publication_duplicate_write");
                    var slot = control ? Array.FindIndex(writes, 0, 2, w => w is null) : (writes[2] is null ? 2 : -1);
                    if (slot < 0) throw new InvalidOperationException("publication_extra_copy");
                    writes[slot] = write;
                }
                else
                {
                    var slot = Array.FindIndex(writes, w => w?.WriteId == write.WriteId);
                    if (slot < 0 || writes[slot]!.Completed || writes[slot]!.Timestamp > write.Timestamp ||
                        writes[slot]! with { Completed = true, Timestamp = write.Timestamp } != write)
                        throw new InvalidOperationException("publication_completion_without_write");
                    writes[slot] = write;
                }
            }
        }
        catch { Fail("publication_write_observer_failed"); throw; }
    }

    internal void VerifyComplete()
    {
        try
        {
            lock (gate)
            {
                Check();
                if (!succeeded.All(value => value) || !finished.All(value => value.Task.IsCompleted) ||
                    writes.Any(value => value is not { Completed: true }))
                    throw new InvalidOperationException("publication_physical_completion_missing");
                verified = true;
            }
        }
        catch { Fail("publication_verification_failed"); throw; }
    }

    internal void Fail(string reason)
    {
        lock (gate) { failure ??= reason; if (disposed) return; }
        try { lifetime.Cancel(); } catch (ObjectDisposedException) when (disposed) { }
    }

    private void Check()
    {
        if (disposed || failure is not null || Lifetime.IsCancellationRequested)
            throw new InvalidOperationException(failure ?? "publication_retired");
    }
    private static int Role(string role) => role switch
    { "control_queue" => 0, "control_copy" => 1, "bulk_copy" => 2, _ => throw new InvalidOperationException("publication_unknown_role") };

    private async Task ReleaseAsync()
    {
        while (releaseAt > Stopwatch.GetTimestamp())
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 1000d * (releaseAt - Stopwatch.GetTimestamp()) / Stopwatch.Frequency)), Lifetime);
        Lifetime.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync() => new(cleanup.Value);

    private async Task CleanupAsync()
    {
        Task[] pending;
        lock (gate)
        {
            disposed = true;
            pending = Enumerable.Range(0, 3).Where(i => started[i]).Select(i => finished[i].Task).ToArray();
        }
        lifetime.Cancel();
        await Task.WhenAll(pending);
        try { await release; } catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        cancellation.Dispose(); lifetime.Dispose();
        lock (gate) { expected = null; metadata = null; source = peer = string.Empty; Array.Clear(writes); }
    }
}
