using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed class TransportScreenShareCoordinator : IAsyncDisposable
{
    internal const int MaxTransportFramesPerSecond = 2;

    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync;
    private readonly IScreenShareClock clock;
    private readonly object gate = new();

    private IScreenCaptureSource? captureSource;
    private ScreenShareFrameSendPipeline? sendPipeline;
    private string sessionId = string.Empty;
    private bool disposed;

    public TransportScreenShareCoordinator(
        Func<IScreenCaptureSource> captureSourceFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync,
        IScreenShareClock? clock = null)
    {
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.clock = clock ?? SystemScreenShareClock.Instance;
    }

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return captureSource is not null && sendPipeline is not null;
            }
        }
    }

    public async Task StartAsync(string nextSessionId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextSessionId);
        ct.ThrowIfCancellationRequested();

        var normalizedSessionId = nextSessionId.Trim();
        lock (gate)
        {
            if (captureSource is not null &&
                sendPipeline is not null &&
                string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                LogDebug("StartAsync ignored because screenshare is already active for the current session.");
                return;
            }
        }

        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);

        var nextCaptureSource = captureSourceFactory();
        if (!nextCaptureSource.IsSupported)
        {
            if (nextCaptureSource is IAsyncDisposable unsupportedAsyncDisposable)
            {
                await unsupportedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        var nextPipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: async (chunk, sendCt) =>
            {
                var payload = ScreenSharePayloadCodec.Serialize(chunk);
                await sendPayloadAsync(payload, sendCt).ConfigureAwait(false);
            },
            clock: clock,
            maxFramesPerSecond: MaxTransportFramesPerSecond);

        lock (gate)
        {
            captureSource = nextCaptureSource;
            sendPipeline = nextPipeline;
            sessionId = normalizedSessionId;
            nextCaptureSource.FrameArrived += OnFrameArrived;
        }

        try
        {
            await nextCaptureSource.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(captureSource, nextCaptureSource))
                {
                    captureSource = null;
                }

                if (ReferenceEquals(sendPipeline, nextPipeline))
                {
                    sendPipeline = null;
                }

                if (string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    sessionId = string.Empty;
                }

                nextCaptureSource.FrameArrived -= OnFrameArrived;
            }

            await nextPipeline.DisposeAsync().ConfigureAwait(false);
            if (nextCaptureSource is IAsyncDisposable failedAsyncDisposable)
            {
                await failedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task HandleDisconnectedAsync()
    {
        return StopAsync(sendStopMessage: false, reason: "disconnected", CancellationToken.None);
    }

    public async Task StopAsync(bool sendStopMessage, string? reason, CancellationToken ct)
    {
        IScreenCaptureSource? oldCaptureSource;
        ScreenShareFrameSendPipeline? oldPipeline;
        string oldSessionId;

        lock (gate)
        {
            oldCaptureSource = captureSource;
            oldPipeline = sendPipeline;
            oldSessionId = sessionId;
            captureSource = null;
            sendPipeline = null;
            sessionId = string.Empty;

            if (oldCaptureSource is not null)
            {
                oldCaptureSource.FrameArrived -= OnFrameArrived;
            }
        }

        if (oldCaptureSource is null && oldPipeline is null && string.IsNullOrWhiteSpace(oldSessionId))
        {
            LogDebug("StopAsync ignored because screenshare is already inactive.");
            return;
        }

        if (oldCaptureSource is not null)
        {
            try
            {
                await oldCaptureSource.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            if (oldCaptureSource is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        if (oldPipeline is not null)
        {
            await oldPipeline.DisposeAsync().ConfigureAwait(false);
        }

        if (sendStopMessage && !string.IsNullOrWhiteSpace(oldSessionId))
        {
            var stop = new ScreenShareStopMessageV1
            {
                SessionId = oldSessionId,
                Reason = reason,
            };

            await sendPayloadAsync(ScreenSharePayloadCodec.SerializeStop(stop), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnFrameArrived(object? sender, ScreenCaptureFrameEventArgs e)
    {
        ScreenShareFrameSendPipeline? currentPipeline;
        string currentSessionId;

        lock (gate)
        {
            currentPipeline = sendPipeline;
            currentSessionId = sessionId;
        }

        if (currentPipeline is null || string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        _ = TryEnqueueFrameAsync(currentPipeline, currentSessionId, e);
    }

    private async Task TryEnqueueFrameAsync(
        ScreenShareFrameSendPipeline currentPipeline,
        string currentSessionId,
        ScreenCaptureFrameEventArgs e)
    {
        try
        {
            await currentPipeline.EnqueueFrameAsync(
                currentSessionId,
                e.Width,
                e.Height,
                e.Encoding,
                e.EncodedFrameData,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already disposed.");
        }
        catch (InvalidOperationException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already completed.");
        }
        catch (OperationCanceledException)
        {
            LogDebug("Frame enqueue canceled during shutdown.");
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareTransport] {message}");
    }
}
