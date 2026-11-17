using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.SmokeTests;

namespace NLink.Tests;

// Scheduling evidence only. This helper reads the original context under its
// gate; it never invokes an authority evaluator or an observation callback.
internal static class AttemptOfflineFrontierProgress
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    internal static async Task RunAsync(SessionFileTransferService service, object context, AttemptNknPipeClient client, CancellationToken ct)
    {
        var gate = Field(service, "gate")!;
        var schedule = client.InboundSchedule!;
        object? originalObservation = null;
        var stage = "ordinary-frontier-zero-highest-seven";
        try
        {
            await Wait(() => schedule.AllOrdinarySeen && Read(() =>
            {
                var next = (int)Property(context, "NextChunkIndex")!;
                if (next != 0) throw new IOException("offline_schedule_frontier_advanced_before_sparse_shape");
                return (int)Property(context, "PullHighestReceivedChunkIndex")! == 7;
            }));
            stage = "exact-repair-zero-and-recent-grace";
            await Wait(() => Read(() =>
            {
                var observation = Observation();
                if (observation is null || Field(observation, "recent") is null) return false;
                var next = (int)Property(context, "NextChunkIndex")!;
                if (next != 1 || Field(observation, "capturedRepair") is null)
                    throw new IOException("offline_schedule_repair_shape");
                var entries = ((IEnumerable)Field(observation, "entries")!).Cast<object>();
                var receipt = Property(entries.Single(entry => (string)Property(entry, "Kind")! == "RepairProgress"), "Binding")!;
                var identity = Property(Property(receipt, "Contract")!, "Identity")!;
                var range = ((IEnumerable)Property(identity, "Ranges")!).Cast<FileTransferRangeV4>().Single();
                if (range.StartChunkIndex != 0 || range.ChunkCount != 1 ||
                    (int)Property(receipt, "PreviousFrontierChunkIndex")! != 0 ||
                    (int)Property(receipt, "CurrentFrontierChunkIndex")! != 1)
                    throw new IOException("offline_schedule_repair_range");
                return (int)Property(context, "V6LastReceiverStateCommittedChunkIndex")! == 1 &&
                    (int)Property(context, "V6LastReceiverStateHighestReceivedChunkIndex")! == 7;
            }));
            var cursor = CoreSmokeTestsBase.GetOperationalLogLength();
            Read(() =>
            {
                var recent = Field(Observation()!, "recent")!;
                if (DateTimeOffset.UtcNow >= (DateTimeOffset)Property(recent, "DueUtc")!)
                    throw new IOException("offline_schedule_recent_grace_elapsed");
                return true;
            });
            schedule.Release(2);
            stage = "fresh-sparse-write-deferred";
            await Wait(() => CoreSmokeTestsBase.ReadOperationalLogTail(cursor).Split('\n').Any(line =>
                line.Contains("event=filetransfer_v6_receiver_state_deferred;", StringComparison.Ordinal) &&
                line.Contains("reason=frontier_stalled;", StringComparison.Ordinal) &&
                line.Contains("next_chunk_index=1; highest_received_chunk_index=7;", StringComparison.Ordinal)));
            Read(() =>
            {
                if (DateTimeOffset.UtcNow >= (DateTimeOffset)Property(Field(Observation()!, "recent")!, "DueUtc")!)
                    throw new IOException("offline_schedule_deferred_outside_recent_grace");
                return true;
            });
            stage = "original-recent-recheck";
            await Wait(() => Read(() => Field(Observation()!, "recheckedTip") is not null));
            var due = Read(() => (DateTimeOffset)Property(Field(Observation()!, "root")!, "DueUtc")!);
            if (DateTimeOffset.UtcNow >= due) throw new IOException("offline_schedule_original_deadline_elapsed");
            schedule.Release(1);
            stage = "original-write-and-accepted-publication";
            await Wait(() => Read(() =>
            {
                var observation = Observation()!;
                if (!(bool)Property(observation, "IsClosed")!) return false;
                var write = Field(observation, "firstWrite")!;
                var clock = Property(write, "Clock")!;
                if ((int)Property(write, "Before")! != 1 || (int)Property(write, "After")! != 4 ||
                    (long)Property(clock, "UtcTicks")! >= due.UtcTicks || (int)Property(context, "NextChunkIndex")! != 4)
                    throw new IOException("offline_schedule_first_write_shape_or_deadline");
                Console.WriteLine("carrier-frontier-schedule=closed-positive; status=" + Property(observation, "Status"));
                return true;
            }));
            schedule.Release(4);
            if (schedule.PendingBytes != 0) throw new IOException("offline_schedule_retained_frames");
        }
        catch (Exception error)
        {
            schedule.Dispose();
            throw new IOException("offline_schedule_unavailable; stage=" + stage, error);
        }

        T Read<T>(Func<T> read)
        {
            lock (gate)
            {
                if (!ReferenceEquals(Field(service, "inboundTransfer"), context)) throw new IOException("offline_schedule_context_retired");
                _ = Observation();
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
                ("PublishedBeforeOriginalDeadline" or "PublishedWithinObservationBound"))
                throw new IOException("offline_schedule_negative_close; status=" + Property(observation, "Status"));
            return observation;
        }
        async Task Wait(Func<bool> ready)
        {
            Console.WriteLine("carrier-frontier-schedule-wait=" + stage);
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
}
