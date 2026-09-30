using System.Collections;
using System.Reflection;
using System.Collections.Immutable;
using System.Diagnostics;
using NLink.Infra.Nkn;
using NLink.Tools;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;
using NLink.Core.FileTransfer;
using NLink.SmokeTests;
using TimingBoundary = NLink.Tests.AttemptDeferredWriteTiming.Boundary;

namespace NLink.Tests;

// Scheduling evidence only. This helper reads the original context under its
// gate; it never invokes an authority evaluator or an observation callback.
internal static class AttemptOfflineLatePublicationProgress
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    internal static async Task RunAsync(SessionFileTransferService service, object context, AttemptNknPipeClient client, NknSignalingTransport transport, LocalFrontierAppRequest.AppLease lease, CancellationToken ct,
        bool canonicalTail = false, AttemptCanonicalPrefixScope? prefixScope = null, AttemptCanonicalPrefixHandoff? prefixHandoff = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("late_publication_native_windows_only");
        var gate = Field(service, "gate")!;
        var schedule = client.InboundSchedule!;
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        object? originalObservation = null, originalRoot = null;
        AttemptLatePublicationOwner? owner = null;
        string? sparseShapeRejection = null;
        var deferredTiming = canonicalTail ? new AttemptDeferredWriteTiming() : null;
        using var process = Process.GetCurrentProcess();
        var creation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        var stage = "ordinary-frontier-zero-highest-seven";
        try
        {
            if (schedule.IsCanonicalTail != canonicalTail) throw new IOException("late_publication_schedule_profile_mismatch");
            if (canonicalTail)
            {
                if (prefixScope is null || prefixHandoff is null) throw new IOException("canonical_prefix_binding_missing");
                stage = "canonical-committed-prefix";
                Console.WriteLine("carrier-frontier-late-publication-wait=" + stage);
                await WaitForCommittedPrefixAsync(prefixScope.Ready, () =>
                {
                    client.CheckReader();
                    prefixHandoff.Check();
                    Read(() => true);
                    if (schedule.AnyOrdinarySeen) throw new IOException("canonical_prefix_early_tail");
                }, prefixHandoff.PublishAsync, ct);
                stage = "canonical-prefix-before-first-suffix";
                Console.WriteLine("carrier-frontier-late-publication-wait=" + stage);
                await WaitForCanonicalSuffixAsync(schedule, () =>
                {
                    client.CheckReader();
                    Read(() => { RequireUnboundObservation(Observation()); return true; });
                }, ct);
                stage = "ordinary-frontier-tail-highest-final";
            }
            await Wait(() => schedule.AllOrdinarySeen && Read(() =>
            {
                var observation = canonicalTail ? Observation() : null;
                if (canonicalTail) RequireUnboundObservation(observation);
                return ReadSparseShape(canonicalTail, context, observation, out sparseShapeRejection);
            }));
            if (canonicalTail) schedule.CaptureTailReadyTiming();
            stage = canonicalTail ? "exact-tail-repair-and-recent-grace" : "exact-repair-zero-and-recent-grace";
            await Wait(() => Read(() =>
            {
                var observation = Observation();
                if (observation is null || Field(observation, "recent") is null) return false;
                var next = (int)Property(context, "NextChunkIndex")!;
                if (next != first + 1 || Field(observation, "capturedRepair") is null)
                    throw new IOException("offline_schedule_repair_shape");
                var entries = ((IEnumerable)Field(observation, "entries")!).Cast<object>();
                var receipt = Property(entries.Single(entry => (string)Property(entry, "Kind")! == "RepairProgress"), "Binding")!;
                var identity = Property(Property(receipt, "Contract")!, "Identity")!;
                var range = ((IEnumerable)Property(identity, "Ranges")!).Cast<FileTransferRangeV4>().Single();
                RequireRepairShape(canonicalTail, range, (int)Property(receipt, "PreviousFrontierChunkIndex")!,
                    (int)Property(receipt, "CurrentFrontierChunkIndex")!);
                return (int)Property(context, "V6LastReceiverStateCommittedChunkIndex")! == first + 1 &&
                    (int)Property(context, "V6LastReceiverStateHighestReceivedChunkIndex")! == first + 7;
            }));
            deferredTiming?.Capture(TimingBoundary.CursorStart);
            var cursor = CoreSmokeTestsBase.GetOperationalLogLength();
            deferredTiming?.Capture(TimingBoundary.CursorEnd);
            Read(() =>
            {
                var recent = Field(Observation()!, "recent")!;
                var now = DateTimeOffset.UtcNow;
                var due = (DateTimeOffset)Property(recent, "DueUtc")!;
                deferredTiming?.Grace(false, now, due);
                if (now >= due)
                    throw new IOException("offline_schedule_recent_grace_elapsed");
                return true;
            });
            deferredTiming?.Capture(TimingBoundary.ReleaseStart);
            schedule.Release(2);
            deferredTiming?.Capture(TimingBoundary.ReleaseEnd);
            stage = "fresh-sparse-write-deferred";
            await Wait(() => ReadDeferredEvent(canonicalTail, () => CoreSmokeTestsBase.ReadOperationalLogTail(cursor), deferredTiming));
            deferredTiming?.Capture(TimingBoundary.RecheckRequested);
            Read(() =>
            {
                RequireDeferredWithinRecentGrace(DateTimeOffset.UtcNow,
                    (DateTimeOffset)Property(Field(Observation()!, "recent")!, "DueUtc")!, deferredTiming);
                return true;
            }, traceDeferredRecheck: true);
            stage = "original-recent-recheck";
            await Wait(() => Read(() => Field(Observation()!, "recheckedTip") is not null));
            stage = "native-arm-and-original-hold-budget";
            var facts = Read(() =>
            {
                var observation = Observation()!;
                originalRoot = Field(observation, "root") ?? throw new IOException("late_publication_root_missing");
                var stamp = (Stamp)Field(observation, "stallClock")!;
                if (stamp.ProcessCreationFileTime != creation ||
                    stamp.ObservationId != (Guid)Field(observation, "observationId")! ||
                    stamp.ClockInstanceId != (Guid)Field(observation, "clockId")! ||
                    !ReferenceEquals(Field(observation, "tip"), originalRoot) ||
                    !ReferenceEquals(Field(observation, "recheckedTip"), originalRoot) ||
                    ((IEnumerable)Field(observation, "entries")!).Cast<object>().Any(e => (string)Property(e, "Kind")! == "StallSuccessor") ||
                    (int)Property(context, "NextChunkIndex")! != first + 1 ||
                    (int)Property(context, "PullHighestReceivedChunkIndex")! != first + 7 ||
                    (int)Property(originalRoot, "ProtocolVersion")! != 6)
                    throw new IOException("late_publication_native_arm_shape");
                var identity = new AttemptLatePublicationIdentity(
                    (string)Property(originalRoot, "SessionId")!, (string)Property(originalRoot, "TransferId")!,
                    client.Address, client.PeerAddress, (long)Property(originalRoot, "TransportEpoch")!,
                    Convert.ToInt64(Property(originalRoot, "LiveRouteEpoch")),
                    Convert.ToInt64(Property(originalRoot, "LegGeneration")),
                    Convert.ToInt64(Property(originalRoot, "BridgeRecoveryGeneration")));
                return (Stamp: stamp, Deadline: ((DateTimeOffset)Property(originalRoot, "DueUtc")!).UtcTicks, Identity: identity);
            });
            var window = AttemptLatePublicationWindow.Create(facts.Stamp, facts.Deadline, schedule.ReadBudget(), Stopwatch.GetTimestamp());
            owner = new(window, facts.Identity, ct, canonicalTail);
            if (transport.OfflinePublicationDelayForTests is not null || client.WriteObserver is not null)
                throw new IOException("late_publication_hook_already_owned");
            transport.OfflinePublicationDelayForTests = owner.Registration();
            client.WriteObserver = owner.ObserveWrite;
            stage = canonicalTail ? "fixed-tail-plus-one-release" : "fixed-chunk-one-release";
            await Wait(() => Stopwatch.GetTimestamp() >= window.ChunkRelease);
            // Never silently slide the chosen schedule to a later episode.
            if (Stopwatch.GetTimestamp() >= window.Stall.CounterBefore + Stopwatch.Frequency * 3 / 4)
                throw new IOException("late_publication_chunk_release_late");
            schedule.Release(1);
            stage = "actual-write-and-selected-frame";
            await Wait(() => Read(() =>
            {
                var observation = Observation()!;
                if (Field(observation, "firstWrite") is not { } write ||
                    Field(observation, "frozenFrame") is not FileTransferReceiverStateFrameV6 frame) return false;
                RequireWriteShape(canonicalTail, (int)Property(write, "Before")!, (int)Property(write, "After")!,
                    (int)Property(write, "Count")!, (int)Property(write, "Highest")!,
                    (int)Property(context, "NextChunkIndex")!, frame);
                if (!ReferenceEquals(Field(observation, "writeTip"), originalRoot))
                    throw new IOException("late_publication_first_write_shape");
                var frozen = frame with
                {
                    MissingRanges = ((IEnumerable)Field(observation, "missingRanges")!).Cast<FileTransferRangeV4>()
                        .Select(r => r with { }).ToImmutableArray()
                };
                return owner.CheckNativeWrite((Stamp)Property(write, "Clock")!, frozen);
            }));
            stage = "original-late-accepted-closure-and-all-copies";
            await Wait(() => Read(() => (bool)Property(Observation()!, "IsClosed")!) && owner.CopiesFinished);
            Read(() =>
            {
                var observation = Observation()!;
                if ((string)Property(observation, "Status")! != "PublishedWithinObservationBound" ||
                    Property(observation, "Timing") is null ||
                    !((IEnumerable)Field(observation, "entries")!).Cast<object>().Any(e => (string)Property(e, "Kind")! == "AcceptedCompletion"))
                    throw new IOException("late_publication_original_late_receipt_missing");
                return true;
            });
            owner.VerifyComplete();
            Console.WriteLine("carrier-frontier-late-publication=closed-within-bound; physical-copies=3; root=unchanged");
            // All selected tasks are finished before either hook is retired.
            transport.OfflinePublicationDelayForTests = null;
            client.WriteObserver = null;
            schedule.Release(4);
            if (schedule.PendingBytes != 0) throw new IOException("offline_schedule_retained_frames");
        }
        catch (Exception error)
        {
            WriteSparseShapeRejection(ref sparseShapeRejection, Console.Out);
            owner?.Fail("late_publication_profile_unavailable");
            schedule.Dispose();
            throw new IOException("offline_schedule_unavailable; stage=" + stage, error);
        }

        finally
        {
            // Do not retire someone else's pre-existing hook on a failed preflight.
            if (owner is not null)
            {
                owner.Fail("late_publication_profile_cleanup");
                await owner.DisposeAsync();
                if (transport.OfflinePublicationDelayForTests?.Bind?.Target == owner) transport.OfflinePublicationDelayForTests = null;
                if (client.WriteObserver?.Target == owner) client.WriteObserver = null;
            }
            schedule.WriteHoldTimings(Console.Out);
            deferredTiming?.Write(Console.Out);
        }

        T Read<T>(Func<T> read, bool traceDeferredRecheck = false)
        {
            lock (gate)
            {
                if (traceDeferredRecheck) deferredTiming?.Capture(TimingBoundary.RecheckGateEntered);
                if (!ReferenceEquals(Field(service, "inboundTransfer"), context)) throw new IOException("offline_schedule_context_retired");
                prefixScope?.Check();
                if (canonicalTail) AttemptCanonicalTailProfile.RequireGeometry((long)Property(context, "FileSizeBytes")!,
                    (int)Property(context, "ChunkSizeBytes")!, (int)Property(context, "ChunkCount")!);
                var observation = Observation();
                if (!OperatingSystem.IsWindows() || !lease.IsCurrent || !client.IsCurrent) throw new IOException("late_publication_native_process_retired");
                if (originalRoot is not null && observation is not null)
                {
                    if (!ReferenceEquals(Field(observation, "root"), originalRoot) ||
                        !ReferenceEquals(Field(observation, "tip"), originalRoot) ||
                        !ReferenceEquals(Field(observation, "recheckedTip"), originalRoot) ||
                        ((IEnumerable)Field(observation, "entries")!).Cast<object>().Any(e => (string)Property(e, "Kind")! == "StallSuccessor"))
                        throw new IOException("late_publication_root_changed");
                    // The original value-only projection performs no evaluator,
                    // callback or mutation. It stays inside this reader/gate.
                    var projection = observation.GetType().GetMethod("Source", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context]);
                    if (!Field(observation, "source")!.Equals(projection)) throw new IOException("late_publication_source_changed");
                }
                owner?.Check();
                return read();
            }
        }
        object? Observation()
        {
            var observation = Property(context, "FrontierBoundaryObservation");
            if (originalObservation is not null && !ReferenceEquals(originalObservation, observation))
                throw new IOException("offline_schedule_observation_replaced");
            if (observation is null) return null;
            originalObservation ??= observation;
            if ((bool)Property(observation, "IsClosed")! && Property(observation, "Status") is not
                "PublishedWithinObservationBound")
                throw new IOException("offline_schedule_negative_close; status=" + Property(observation, "Status"));
            return observation;
        }
        async Task Wait(Func<bool> ready)
        {
            Console.WriteLine("carrier-frontier-late-publication-wait=" + stage);
            using var edge = CancellationTokenSource.CreateLinkedTokenSource(ct);
            edge.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                client.CheckReader();
                Read(() => true);
                if (ready()) return;
                await Task.Delay(1, edge.Token);
            }
        }
    }

    // The caller passes the already-running carrier token. There is deliberately
    // no new deadline here; per-frame expiry still belongs to the scheduler.
    internal static async Task WaitForCommittedPrefixAsync(Func<bool> ready, Action check, Func<Task> publish, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            check();
            if (ready())
            {
                ct.ThrowIfCancellationRequested();
                await publish(); // Outside the snapshot's service gate; no suffix dependency.
                return;
            }
            await Task.Delay(1, ct);
        }
    }

    internal static async Task WaitForCanonicalSuffixAsync(AttemptOfflineFrameSchedule schedule, Action check, CancellationToken ct)
    {
        if (!schedule.IsCanonicalTail) throw new IOException("late_publication_schedule_profile_mismatch");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            check();
            ct.ThrowIfCancellationRequested();
            if (schedule.AnyOrdinarySeen) return;
            await Task.Delay(1, ct);
        }
    }

    internal static void RequireUnboundObservation(object? observation)
    {
        if (observation is not null && ((bool)Property(observation, "IsClosed")! ||
            Field(observation, "source") is not null || Field(observation, "capturedRepair") is not null))
            throw new IOException("canonical_tail_prior_frontier_episode");
    }

    internal static bool MatchesSparseShape(bool canonicalTail, int next, int highest)
    {
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        // Physical tail receipt can precede application commit of its contiguous prefix.
        if (canonicalTail && next >= 0 && next < first && highest == next - 1)
            return false;
        if (next != first) throw new IOException("offline_schedule_frontier_advanced_before_sparse_shape");
        return highest == first + 7;
    }

    internal static bool ReadSparseShape(bool canonicalTail, object context, object? observation, out string? rejection)
    {
        rejection = null;
        var next = (int)Property(context, "NextChunkIndex")!;
        var highest = (int)Property(context, "PullHighestReceivedChunkIndex")!;
        if (canonicalTail && next >= 0 && next < AttemptCanonicalTailProfile.FirstChunk &&
            highest >= next && highest < AttemptCanonicalTailProfile.ChunkCount)
        {
            // Receipt can precede commit. Wait only for this exact unwritten
            // prefix frontier; this is not tail readiness or route authority.
            try
            {
                if (Property(context, "ReceiverSparseChunksWritten") is BitArray written &&
                    written.Length == AttemptCanonicalTailProfile.ChunkCount && !written[next] &&
                    Property(context, "ReceiverSparseChunksPendingWrite") is HashSet<int> pending && pending.Contains(next))
                    return false;
            }
            catch (Exception) { /* Unavailable evidence retains the original predicate and rejection. */ }
        }
        try { return MatchesSparseShape(canonicalTail, next, highest); }
        catch (IOException) when (canonicalTail)
        {
            // Called under the original service gate. Retain only bounded scalar
            // evidence; output belongs to the outer catch, after the gate exits.
            var prefix = FormattableString.Invariant($"carrier-frontier-sparse-shape-rejected; next={next}; highest={highest}; ");
            rejection = prefix + "snapshot=unavailable";
            try
            {
                var pending = (HashSet<int>)Property(context, "ReceiverSparseChunksPendingWrite")!;
                var written = (BitArray?)Property(context, "ReceiverSparseChunksWritten");
                var nextWritten = written is not null && next >= 0 && next < written.Length ? (written[next] ? 1 : 0) : -1;
                var status = observation is null ? "none" : (string)Property(observation, "Status")!;
                status = new string(status.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
                var closed = observation is null ? -1 : ((bool)Property(observation, "IsClosed")! ? 1 : 0);
                var bound = observation is not null && Field(observation, "source") is not null;
                var repaired = observation is not null && Field(observation, "capturedRepair") is not null;
                rejection = prefix + FormattableString.Invariant($"snapshot=available; pending_count={pending.Count}; next_pending={(pending.Contains(next) ? 1 : 0)}; written_length={written?.Length ?? -1}; next_written={nextWritten}; observation_present={(observation is null ? 0 : 1)}; observation_closed={closed}; observation_status={status}; source_bound={(bound ? 1 : 0)}; repair_bound={(repaired ? 1 : 0)}");
            }
            catch (Exception) { /* A diagnostic read must not replace the original rejection. */ }
            throw;
        }
    }

    internal static void WriteSparseShapeRejection(ref string? rejection, TextWriter output)
    {
        var line = rejection;
        rejection = null;
        if (line is null) return;
        try { output.WriteLine(line); }
        catch (Exception) { /* Diagnostic output is single-attempt, never a retry or a new failure. */ }
    }

    internal static void RequireRepairShape(bool canonicalTail, FileTransferRangeV4 range, int before, int after)
    {
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        if (range.StartChunkIndex != first || range.ChunkCount != 1 || before != first || after != first + 1)
            throw new IOException("offline_schedule_repair_range");
    }

    internal static bool MatchesDeferredEvent(bool canonicalTail, string line)
    {
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        return line.Contains("event=filetransfer_v6_receiver_state_deferred;", StringComparison.Ordinal) &&
            line.Contains("reason=frontier_stalled;", StringComparison.Ordinal) &&
            line.Contains($"next_chunk_index={first + 1}; highest_received_chunk_index={first + 7};", StringComparison.Ordinal);
    }

    internal static bool ReadDeferredEvent(bool canonicalTail, Func<string> read, AttemptDeferredWriteTiming? timing)
    {
        timing?.BeginRead();
        try
        {
            var tail = read();
            timing?.ReadCompleted(tail.Length);
            return tail.Split('\n').Any(line =>
            {
                if (!MatchesDeferredEvent(canonicalTail, line)) return false;
                timing?.Match(line);
                return true;
            });
        }
        finally { timing?.EndPoll(); }
    }

    internal static void RequireDeferredWithinRecentGrace(DateTimeOffset now, DateTimeOffset due, AttemptDeferredWriteTiming? timing)
    {
        timing?.Grace(true, now, due);
        if (now >= due) throw new IOException("offline_schedule_deferred_outside_recent_grace");
    }

    internal static void RequireWriteShape(bool canonicalTail, int before, int after, int count, int highest, int next,
        FileTransferReceiverStateFrameV6 frame)
    {
        var first = canonicalTail ? AttemptCanonicalTailProfile.FirstChunk : 0;
        if (before != first + 1 || after != first + 4 || count != 3 || highest != first + 7 || next != first + 4)
            throw new IOException("late_publication_first_write_shape");
        if (canonicalTail && (frame.ContiguousCommittedChunkIndex != first + 4 ||
            frame.DurableReceivedHighestChunkIndex != first + 7 || frame.BytesCommitted != AttemptCanonicalTailProfile.CommittedAfterWrite ||
            frame.TerminalReady || frame.TransferPaused))
            throw new IOException("canonical_tail_nonterminal_frame_shape");
    }
}
