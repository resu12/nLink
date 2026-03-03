using System;
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

    private int remoteScreenShareFrameGeneration;
    private int remoteScreenShareDecodeInFlight;
    private Task? remoteScreenShareDecodeTask;

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

        if (isDisposed() || !isTransportEnabled())
        {
            return;
        }

        // Only one decode may run at a time; newer frames are dropped here and a generation
        // mismatch disposes any stale bitmap before it can replace the current frame.
        if (Interlocked.Exchange(ref remoteScreenShareDecodeInFlight, 1) == 1)
        {
            return;
        }

        var generation = Volatile.Read(ref remoteScreenShareFrameGeneration);
        Task? decodeTask = null;
        decodeTask = Task.Run(async () =>
        {
            Bitmap? bitmap = null;
            try
            {
                bitmap = decodeFrame(e.EncodedFrameBytes);

                if (isDisposed() || generation != Volatile.Read(ref remoteScreenShareFrameGeneration))
                {
                    bitmap.Dispose();
                    bitmap = null;
                    return;
                }

                await UiThreadDispatch.RunAsync(() => SetRemoteScreenShareFrameCore(bitmap, generation));
                bitmap = null;
            }
            catch
            {
                bitmap?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref remoteScreenShareDecodeInFlight, 0);
                if (ReferenceEquals(remoteScreenShareDecodeTask, decodeTask))
                {
                    remoteScreenShareDecodeTask = null;
                }
            }
        });
        remoteScreenShareDecodeTask = decodeTask;
    }

    public void Clear()
    {
        Interlocked.Increment(ref remoteScreenShareFrameGeneration);
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            ClearRemoteScreenShareFrameCore();
            return;
        }

        _ = UiThreadDispatch.RunAsync(ClearRemoteScreenShareFrameCore);
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref remoteScreenShareFrameGeneration);

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
            catch
            {
            }
        }
    }

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
}
