using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.App.Threading;
using NLink.Infra.Nkn;

namespace NLink.App.Services.ScreenCapture;

internal sealed class HelperScreenShareCoordinator
{
    private readonly Func<bool> isDisposed;
    private readonly Func<bool> isTransportEnabled;
    private readonly Func<Bitmap?> getRemoteFrame;
    private readonly Action<Bitmap?> setRemoteFrame;
    private readonly Func<byte[], Bitmap> decodeFrame;
    private readonly object gate = new();

    private int remoteScreenShareFrameGeneration;
    private int remoteScreenShareDecodeInFlight;
    private int maxRemoteScreenShareDecodeTasksActive;
    private int stopped;
    private Task? remoteScreenShareDecodeTask;
    private byte[]? pendingEncodedFrameBytes;
    private long framesDecoded;

    public HelperScreenShareCoordinator(
        Func<bool> isDisposed,
        Func<bool> isTransportEnabled,
        Func<Bitmap?> getRemoteFrame,
        Action<Bitmap?> setRemoteFrame,
        Func<byte[], Bitmap>? decodeFrame = null)
    {
        this.isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        this.isTransportEnabled = isTransportEnabled ?? throw new ArgumentNullException(nameof(isTransportEnabled));
        this.getRemoteFrame = getRemoteFrame ?? throw new ArgumentNullException(nameof(getRemoteFrame));
        this.setRemoteFrame = setRemoteFrame ?? throw new ArgumentNullException(nameof(setRemoteFrame));
        this.decodeFrame = decodeFrame ?? DecodeFrame;
    }

    public void OnFrameCompleted(ScreenShareFrameCompletedEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        if (isDisposed() || Volatile.Read(ref stopped) == 1 || !isTransportEnabled())
        {
            return;
        }

        ReplacePendingFrame(e.EncodedFrameBytes);
        StartDecodeLoopIfNeeded();
    }

    public void Clear()
    {
        Interlocked.Increment(ref remoteScreenShareFrameGeneration);
        ReplacePendingFrame(null);

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            ClearRemoteScreenShareFrameCore();
            return;
        }

        _ = UiThreadDispatch.RunAsync(ClearRemoteScreenShareFrameCore);
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref stopped, 1);
        Interlocked.Increment(ref remoteScreenShareFrameGeneration);
        ReplacePendingFrame(null);

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            ClearRemoteScreenShareFrameCore();
        }
        else
        {
            await UiThreadDispatch.RunAsync(ClearRemoteScreenShareFrameCore).ConfigureAwait(false);
        }

        var decodeTask = remoteScreenShareDecodeTask;
        if (decodeTask is not null &&
            !(Application.Current is not null && Dispatcher.UIThread.CheckAccess()))
        {
            try
            {
                await decodeTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Remote frame decode task completion failed during stop: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal long FramesDecoded => Interlocked.Read(ref framesDecoded);

    internal int DecodeTasksActive => Volatile.Read(ref remoteScreenShareDecodeInFlight);

    internal int MaxDecodeTasksActive => Volatile.Read(ref maxRemoteScreenShareDecodeTasksActive);

    private void SetRemoteScreenShareFrameCore(Bitmap? nextFrame, int generation)
    {
        if (generation != Volatile.Read(ref remoteScreenShareFrameGeneration))
        {
            nextFrame?.Dispose();
            return;
        }

        var previous = getRemoteFrame();
        setRemoteFrame(nextFrame);
        previous?.Dispose();
    }

    private void ClearRemoteScreenShareFrameCore()
    {
        var previous = getRemoteFrame();
        setRemoteFrame(null);
        previous?.Dispose();
    }

    private static Bitmap DecodeFrame(byte[] encodedFrameData)
    {
        using var stream = new MemoryStream(encodedFrameData, writable: false);
        return new Bitmap(stream);
    }

    private void StartDecodeLoopIfNeeded()
    {
        if (Interlocked.Exchange(ref remoteScreenShareDecodeInFlight, 1) != 0)
        {
            return;
        }

        RecordDecodeTaskActivated();
        StartDecodeLoopCore();
    }

    private void StartDecodeLoopCore()
    {
        Task? decodeTask = null;
        decodeTask = Task.Run(async () =>
        {
            try
            {
                await ProcessPendingDecodeLoopAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref remoteScreenShareDecodeInFlight, 0);
                if (ReferenceEquals(remoteScreenShareDecodeTask, decodeTask))
                {
                    remoteScreenShareDecodeTask = null;
                }

                var restart = false;
                lock (gate)
                {
                    if (!isDisposed() &&
                        Volatile.Read(ref stopped) == 0 &&
                        pendingEncodedFrameBytes is not null &&
                        Interlocked.Exchange(ref remoteScreenShareDecodeInFlight, 1) == 0)
                    {
                        RecordDecodeTaskActivated();
                        restart = true;
                    }
                }

                if (restart)
                {
                    StartDecodeLoopCore();
                }
            }
        });

        remoteScreenShareDecodeTask = decodeTask;
    }

    private async Task ProcessPendingDecodeLoopAsync()
    {
        while (true)
        {
            var generation = Volatile.Read(ref remoteScreenShareFrameGeneration);
            var encodedFrameBytes = TakePendingFrame();
            if (encodedFrameBytes is null || isDisposed() || Volatile.Read(ref stopped) == 1)
            {
                return;
            }

            Bitmap? bitmap = null;
            try
            {
                bitmap = decodeFrame(encodedFrameBytes);

                if (isDisposed() ||
                    Volatile.Read(ref stopped) == 1 ||
                    generation != Volatile.Read(ref remoteScreenShareFrameGeneration))
                {
                    bitmap.Dispose();
                    bitmap = null;
                    return;
                }

                await UiThreadDispatch.RunAsync(() => SetRemoteScreenShareFrameCore(bitmap, generation));
                Interlocked.Increment(ref framesDecoded);
                bitmap = null;
            }
            catch (Exception ex)
            {
                bitmap?.Dispose();
                LogDebug($"Remote frame decode/apply failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ReplacePendingFrame(byte[]? encodedFrameBytes)
    {
        lock (gate)
        {
            pendingEncodedFrameBytes = encodedFrameBytes;
        }
    }

    private byte[]? TakePendingFrame()
    {
        lock (gate)
        {
            var encodedFrameBytes = pendingEncodedFrameBytes;
            pendingEncodedFrameBytes = null;
            return encodedFrameBytes;
        }
    }

    private void RecordDecodeTaskActivated()
    {
        while (true)
        {
            var currentMax = Volatile.Read(ref maxRemoteScreenShareDecodeTasksActive);
            if (currentMax >= 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxRemoteScreenShareDecodeTasksActive, 1, currentMax) == currentMax)
            {
                return;
            }
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareRemote] {message}");
    }
}
