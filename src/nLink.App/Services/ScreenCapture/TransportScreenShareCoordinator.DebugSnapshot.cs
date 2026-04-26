using System;
using System.Diagnostics;
using System.Threading;
using NLink.Core.ScreenShare;

#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnSnapshotTimerTick(),
            this,
            SnapshotInterval,
            SnapshotInterval);
    }

    private void StopSnapshotTimer()
    {
        Interlocked.Exchange(ref snapshotTickInFlight, 0);
        var timer = Interlocked.Exchange(ref snapshotTimer, null);
        timer?.Dispose();
    }

    private void OnSnapshotTimerTick()
    {
        if (Interlocked.Exchange(ref snapshotTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            ScreenShareFrameSendPipeline? currentPipeline;
            lock (gate)
            {
                currentPipeline = sendPipeline;
                if (captureSource is null || currentPipeline is null)
                {
                    return;
                }
            }

            var metrics = GetMetricsSnapshot();
            var latency = currentPipeline.GetDebugLatencySnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} queued={metrics.FramesQueued} dropped={metrics.FramesDropped} " +
                $"drop_rate={metrics.FramesDroppedByRateGate} drop_evict={metrics.FramesDroppedByQueueEvict} deferred={metrics.FramesDeferredToSendSlot} replaced={metrics.FramesReplacedBeforeSendSlot} slot_empty={metrics.SendSlotEmptyCount} sent={metrics.ChunksSent} " +
                $"raw_bytes={metrics.RawFrameBytesSent} serialized_bytes={metrics.SerializedChunkBytesSent} bridge_bytes={metrics.BridgeBytesSent} " +
                $"display_info={metrics.DisplayInfoSendCount} avg_c2e={metrics.AverageCaptureToEnqueueMs:F1}ms " +
                $"avg_q2s={metrics.AverageEnqueueToSendMs:F1}ms avg_c2s={metrics.AverageCaptureToSendMs:F1}ms " +
                $"c2e={FormatLatency(latency.CaptureToEnqueue)} q2s={FormatLatency(latency.EnqueueToSend)} " +
                $"send={FormatLatency(latency.SendDuration)} e2e={FormatLatency(latency.EndToEnd)}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Transport snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref snapshotTickInFlight, 0);
        }
    }
#endif

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareTransport] {message}");
    }

#if DEBUG
    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif
}
