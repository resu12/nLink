using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

public sealed class ScreenShareFrameSender : IAsyncDisposable
{
    private readonly Func<byte[], CancellationToken, Task> sendPayloadAsync;
    private readonly bool isTransportEnabled;
    private readonly object gate = new();
    private readonly CancellationTokenSource disposeCts = new();

    private PendingFrame? pendingFrame;
    private Task? sendLoopTask;
    private bool disposed;

    public ScreenShareFrameSender(Func<byte[], CancellationToken, Task> sendPayloadAsync, bool? isTransportEnabled = null)
    {
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.isTransportEnabled = isTransportEnabled ?? FeatureFlags.EnableScreenShareTransport;
    }

    public Task EnqueueFrameAsync(
        string sessionId,
        long frameId,
        int width,
        int height,
        string encoding,
        byte[] encodedFrameBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!isTransportEnabled)
        {
            return Task.CompletedTask;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(encodedFrameBytes);

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var frame = new PendingFrame(sessionId.Trim(), frameId, width, height, encoding.Trim(), encodedFrameBytes);
        lock (gate)
        {
            pendingFrame = frame;
            sendLoopTask ??= Task.Run(() => ProcessLoopAsync(), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposeCts.Cancel();

        Task? loopTask;
        lock (gate)
        {
            pendingFrame = null;
            loopTask = sendLoopTask;
            sendLoopTask = null;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        disposeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (true)
            {
                PendingFrame? frame;
                lock (gate)
                {
                    frame = pendingFrame;
                    pendingFrame = null;
                    if (frame is null)
                    {
                        sendLoopTask = null;
                        return;
                    }
                }

                await SendFrameAsync(frame, disposeCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (gate)
            {
                if (sendLoopTask?.IsCompleted != false)
                {
                    sendLoopTask = null;
                }
            }
        }
    }

    private async Task SendFrameAsync(PendingFrame frame, CancellationToken cancellationToken)
    {
        var chunkCount = (frame.EncodedFrameBytes.Length + ScreenSharePayloadCodec.MaxChunkRawBytes - 1) / ScreenSharePayloadCodec.MaxChunkRawBytes;
        if (chunkCount == 0)
        {
            return;
        }

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var offset = chunkIndex * ScreenSharePayloadCodec.MaxChunkRawBytes;
            var count = Math.Min(ScreenSharePayloadCodec.MaxChunkRawBytes, frame.EncodedFrameBytes.Length - offset);
            var chunkBytes = new byte[count];
            Buffer.BlockCopy(frame.EncodedFrameBytes, offset, chunkBytes, 0, count);

            var chunk = new ScreenShareFrameChunkV1
            {
                SessionId = frame.SessionId,
                FrameId = frame.FrameId,
                Width = frame.Width,
                Height = frame.Height,
                Encoding = frame.Encoding,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                DataBase64 = Convert.ToBase64String(chunkBytes),
            };

            var payloadBytes = ScreenSharePayloadCodec.Serialize(chunk);
            LogDebug($"Sending screenshare chunk frame={frame.FrameId} chunk={chunkIndex + 1}/{chunkCount} payload_len={payloadBytes.Length}");
            await sendPayloadAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareSender] {message}");
    }

    private sealed record PendingFrame(
        string SessionId,
        long FrameId,
        int Width,
        int Height,
        string Encoding,
        byte[] EncodedFrameBytes);
}
