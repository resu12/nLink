using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.Core.ScreenShare;

namespace NLink.App.ViewModels;

public sealed class ScreenShareViewerViewModel : ViewModelBase, IDisposable
{
    private const int MaxDecodeIterationsPerPass = 2;

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
                var jpegBytes = TakePendingFrame();

                if (jpegBytes is null || disposed)
                {
                    return;
                }

                Bitmap? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => decodeFrame(jpegBytes)).ConfigureAwait(false);
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
                    }).ConfigureAwait(false);
                    bitmap = null;
                }
                catch
                {
                    bitmap?.Dispose();
                    Interlocked.Increment(ref decodeErrors);
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
            catch
            {
                // Best-effort cleanup only. Failed disposal must not break viewer updates.
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
        }
    }

    private byte[]? TakePendingFrame()
    {
        lock (gate)
        {
            var jpegBytes = pendingJpegBytes;
            pendingJpegBytes = null;
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
}
