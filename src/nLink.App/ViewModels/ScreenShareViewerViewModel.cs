using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.Core.ScreenShare;
using System.Diagnostics;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.ViewModels;

public sealed class ScreenShareViewerViewModel : ViewModelBase, IDisposable
{
    private const int MaxDecodeIterationsPerPass = 2;
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<byte[], Bitmap> decodeFrame;
    private readonly Func<Action, Task> postToUiAsync;
    private readonly object gate = new();

    private Bitmap? currentFrame;
    private bool isActive;
    private string statusText = string.Empty;
    private byte[]? pendingJpegBytes;
    private int decodeInFlight;
    private int generation;
    private long framesDecoded;
    private long decodeErrors;
    private long framesCoalesced;
    private bool disposed;
#if DEBUG
    private long pendingJpegBytesReceivedUtcTicks;
    private readonly DebugLatencyWindow decodeDurationLatency = new();
    private readonly DebugLatencyWindow endToEndLatency = new();
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public ScreenShareViewerViewModel(
        Func<byte[], Bitmap>? decodeFrame = null,
        Func<Action, Task>? postToUiAsync = null)
    {
        this.decodeFrame = decodeFrame ?? DecodeFrame;
        this.postToUiAsync = postToUiAsync ?? PostToUiAsync;
    }

    public IImage? CurrentFrame
    {
        get => currentFrame;
        private set => SetProperty(ref currentFrame, value as Bitmap);
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    internal bool IsIdleForDiagnostics
    {
        get
        {
            lock (gate)
            {
                return Volatile.Read(ref decodeInFlight) == 0 && pendingJpegBytes is null;
            }
        }
    }

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesDecoded: Interlocked.Read(ref framesDecoded),
            DecodeErrors: Interlocked.Read(ref decodeErrors),
            FramesCoalesced: Interlocked.Read(ref framesCoalesced));
    }

    public void OnJpegFrame(byte[] jpegBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(jpegBytes);
        if (jpegBytes.Length == 0)
        {
            throw new ArgumentException("JPEG bytes must not be empty.", nameof(jpegBytes));
        }

        var copy = new byte[jpegBytes.Length];
        Buffer.BlockCopy(jpegBytes, 0, copy, 0, jpegBytes.Length);
        ReplacePendingFrame(copy);

        IsActive = true;
        StatusText = "Live";
#if DEBUG
        StartSnapshotTimer();
#endif

        if (Interlocked.Exchange(ref decodeInFlight, 1) == 0)
        {
            _ = Task.Run(ProcessDecodeLoopAsync);
        }
    }

    public void Clear()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            pendingJpegBytes = null;
        }

        IsActive = false;
        StatusText = string.Empty;
#if DEBUG
        StopSnapshotTimer();
#endif
        ReplaceCurrentFrame(null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessDecodeLoopAsync()
    {
        try
        {
            var iteration = 0;
            while (iteration < MaxDecodeIterationsPerPass)
            {
                var generationSnapshot = Volatile.Read(ref generation);
                var jpegBytes = TakePendingFrame(out var receivedUtcTicks);

                if (jpegBytes is null || disposed)
                {
                    return;
                }

                Bitmap? bitmap = null;
                var decodeStartTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    bitmap = await Task.Run(() => decodeFrame(jpegBytes)).ConfigureAwait(false);
#if DEBUG
                    decodeDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(decodeStartTimestamp, Stopwatch.GetTimestamp()));
#endif
                    if (disposed || generationSnapshot != Volatile.Read(ref generation))
                    {
                        bitmap.Dispose();
                        bitmap = null;
                        return;
                    }

                    var nextBitmap = bitmap;
                    await postToUiAsync(() =>
                    {
                        if (disposed || generationSnapshot != Volatile.Read(ref generation))
                        {
                            nextBitmap.Dispose();
                            return;
                        }

                        ReplaceCurrentFrame(nextBitmap);
                        Interlocked.Increment(ref framesDecoded);
#if DEBUG
                        endToEndLatency.RecordTimeSpanTicks(DateTime.UtcNow.Ticks - receivedUtcTicks);
#endif
                    }).ConfigureAwait(false);
                    bitmap = null;
                }
                catch (Exception ex)
                {
#if DEBUG
                    decodeDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(decodeStartTimestamp, Stopwatch.GetTimestamp()));
#endif
                    bitmap?.Dispose();
                    Interlocked.Increment(ref decodeErrors);
                    LogDebug($"Viewer frame decode/apply failed: {ex.GetType().Name}: {ex.Message}");
                    await postToUiAsync(() =>
                    {
                        if (!disposed && generationSnapshot == Volatile.Read(ref generation))
                        {
                            StatusText = "Invalid frame received";
                        }
                    }).ConfigureAwait(false);
                }

                iteration++;
            }
        }
        finally
        {
            Interlocked.Exchange(ref decodeInFlight, 0);

            lock (gate)
            {
                if (!disposed && pendingJpegBytes is not null && Interlocked.Exchange(ref decodeInFlight, 1) == 0)
                {
                    _ = Task.Run(ProcessDecodeLoopAsync);
                }
            }
        }
    }

    private void ReplaceCurrentFrame(Bitmap? nextFrame)
    {
        var previous = currentFrame;
        CurrentFrame = nextFrame;
        if (previous is not null)
        {
            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                LogDebug($"Viewer previous-frame disposal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ReplacePendingFrame(byte[] jpegBytes)
    {
        lock (gate)
        {
            if (pendingJpegBytes is not null)
            {
                Interlocked.Increment(ref framesCoalesced);
            }

            pendingJpegBytes = jpegBytes;
#if DEBUG
            pendingJpegBytesReceivedUtcTicks = DateTime.UtcNow.Ticks;
#endif
        }
    }

    private byte[]? TakePendingFrame(out long receivedUtcTicks)
    {
        lock (gate)
        {
            var jpegBytes = pendingJpegBytes;
            pendingJpegBytes = null;
#if DEBUG
            receivedUtcTicks = pendingJpegBytesReceivedUtcTicks;
            pendingJpegBytesReceivedUtcTicks = 0;
#else
            receivedUtcTicks = 0;
#endif
            return jpegBytes;
        }
    }

    private static Task PostToUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, DispatcherPriority.Background);
        return completion.Task;
    }

    private static Bitmap DecodeFrame(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes, writable: false);
        return new Bitmap(stream);
    }

#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((ScreenShareViewerViewModel)state!).OnSnapshotTimerTick(),
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
            if (!IsActive)
            {
                return;
            }

            var metrics = GetMetricsSnapshot();
            var decodeSummary = decodeDurationLatency.SnapshotAndReset();
            var endToEndSummary = endToEndLatency.SnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} decoded={metrics.FramesDecoded} errors={metrics.DecodeErrors} inFlight={Volatile.Read(ref decodeInFlight)} " +
                $"decode={FormatLatency(decodeSummary)} e2e={FormatLatency(endToEndSummary)}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Viewer snapshot failed: {ex.GetType().Name}: {ex.Message}");
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
        Trace.WriteLine($"[ScreenShareViewer] {message}");
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
