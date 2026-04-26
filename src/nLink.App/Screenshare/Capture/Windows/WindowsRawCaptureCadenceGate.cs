using System;
using System.Threading;

namespace NLink.App.Services.ScreenCapture;

internal sealed class WindowsRawCaptureCadenceGate
{
    private static readonly TimeSpan ReadbackFpsSampleMinInterval = TimeSpan.FromMilliseconds(500);

    private int targetFramesPerSecond;
    private int forceNextFrame;
    private long lastAcceptedReadbackUtcMs;
    private long frameArrivedCount;
    private long framesSkippedBeforeReadback;
    private long framesReadbackCount;
    private long urgentBypassCount;
    private long lastReadbackDurationMs = -1;
    private long readbackDurationTotalMs;
    private long readbackFpsSampleUtcMs;
    private long readbackFpsSampleCount;
    private double readbackFps;
    private int outputWidth;
    private int outputHeight;
    private int gpuScaleEnabled;
    private string gpuScaleFallbackReason = string.Empty;

    public void SetCadence(int targetFps)
    {
        Volatile.Write(ref targetFramesPerSecond, Math.Max(0, targetFps));
    }

    public void ForceNext()
    {
        Volatile.Write(ref forceNextFrame, 1);
    }

    public void SetOutputDiagnostics(int width, int height, bool gpuScaleEnabledValue, string fallbackReason)
    {
        Volatile.Write(ref outputWidth, Math.Max(0, width));
        Volatile.Write(ref outputHeight, Math.Max(0, height));
        Volatile.Write(ref gpuScaleEnabled, gpuScaleEnabledValue ? 1 : 0);
        Volatile.Write(
            ref gpuScaleFallbackReason,
            string.IsNullOrWhiteSpace(fallbackReason) ? "(none)" : fallbackReason.Trim());
    }

    public void RecordFrameArrived()
    {
        Interlocked.Increment(ref frameArrivedCount);
    }

    public void RecordSkippedBeforeReadback()
    {
        Interlocked.Increment(ref framesSkippedBeforeReadback);
    }

    public bool ShouldSkipBeforeReadback(DateTimeOffset nowUtc, bool hasDeliveredFrame)
    {
        var targetFps = Volatile.Read(ref targetFramesPerSecond);
        if (targetFps <= 0 || !hasDeliveredFrame)
        {
            MarkAccepted(nowUtc);
            return false;
        }

        if (Interlocked.Exchange(ref forceNextFrame, 0) == 1)
        {
            Interlocked.Increment(ref urgentBypassCount);
            MarkAccepted(nowUtc);
            return false;
        }

        var lastAcceptedUtcMs = Interlocked.Read(ref lastAcceptedReadbackUtcMs);
        if (lastAcceptedUtcMs <= 0)
        {
            MarkAccepted(nowUtc);
            return false;
        }

        var minIntervalMs = 1000d / Math.Max(1, targetFps);
        var elapsedMs = nowUtc.ToUnixTimeMilliseconds() - lastAcceptedUtcMs;
        if (elapsedMs >= minIntervalMs)
        {
            MarkAccepted(nowUtc);
            return false;
        }

        RecordSkippedBeforeReadback();
        return true;
    }

    public void RecordReadback(TimeSpan duration, DateTimeOffset nowUtc)
    {
        var durationMs = Math.Max(0, (long)duration.TotalMilliseconds);
        Interlocked.Exchange(ref lastReadbackDurationMs, durationMs);
        Interlocked.Add(ref readbackDurationTotalMs, durationMs);

        var count = Interlocked.Increment(ref framesReadbackCount);
        UpdateReadbackFps(nowUtc.ToUnixTimeMilliseconds(), count);
    }

    public WindowsRawCaptureRuntimeMetrics GetSnapshot()
    {
        var readbackCount = Interlocked.Read(ref framesReadbackCount);
        var durationTotalMs = Interlocked.Read(ref readbackDurationTotalMs);
        var averageDurationMs = readbackCount > 0
            ? durationTotalMs / (double)readbackCount
            : -1;

        return new WindowsRawCaptureRuntimeMetrics(
            FrameArrivedCount: Interlocked.Read(ref frameArrivedCount),
            FramesSkippedBeforeReadback: Interlocked.Read(ref framesSkippedBeforeReadback),
            FramesReadbackCount: readbackCount,
            ReadbackFps: Volatile.Read(ref readbackFps),
            LastReadbackDurationMs: Interlocked.Read(ref lastReadbackDurationMs),
            AverageReadbackDurationMs: averageDurationMs,
            CadenceTargetFps: Volatile.Read(ref targetFramesPerSecond),
            UrgentBypassCount: Interlocked.Read(ref urgentBypassCount),
            OutputWidth: Volatile.Read(ref outputWidth),
            OutputHeight: Volatile.Read(ref outputHeight),
            GpuScaleEnabled: Volatile.Read(ref gpuScaleEnabled) == 1,
            GpuScaleFallbackReason: Volatile.Read(ref gpuScaleFallbackReason) ?? string.Empty);
    }

    private void MarkAccepted(DateTimeOffset nowUtc)
    {
        Interlocked.Exchange(ref lastAcceptedReadbackUtcMs, nowUtc.ToUnixTimeMilliseconds());
    }

    private void UpdateReadbackFps(long nowUtcMs, long readbackCount)
    {
        var lastSampleUtcMs = Interlocked.Read(ref readbackFpsSampleUtcMs);
        if (lastSampleUtcMs <= 0 ||
            readbackCount < Interlocked.Read(ref readbackFpsSampleCount))
        {
            Interlocked.Exchange(ref readbackFpsSampleUtcMs, nowUtcMs);
            Interlocked.Exchange(ref readbackFpsSampleCount, readbackCount);
            return;
        }

        var elapsedMs = nowUtcMs - lastSampleUtcMs;
        if (elapsedMs < ReadbackFpsSampleMinInterval.TotalMilliseconds)
        {
            return;
        }

        var readbackDelta = Math.Max(0, readbackCount - Interlocked.Read(ref readbackFpsSampleCount));
        Volatile.Write(ref readbackFps, readbackDelta * 1000d / elapsedMs);
        Interlocked.Exchange(ref readbackFpsSampleUtcMs, nowUtcMs);
        Interlocked.Exchange(ref readbackFpsSampleCount, readbackCount);
    }
}
