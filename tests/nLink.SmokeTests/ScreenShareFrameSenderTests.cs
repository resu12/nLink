using System.Diagnostics;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameSenderTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSender_ChunksFramePayload_AsUtf8JsonBytes()
    {
        var sentPayloads = new List<byte[]>();
        var expectedChunkCount = 3;
        var allChunksSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sender = new ScreenShareFrameSender(
            sendPayloadAsync: (payload, _) =>
            {
                sentPayloads.Add(payload);
                if (sentPayloads.Count == expectedChunkCount)
                {
                    allChunksSent.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            isTransportEnabled: true);

        var jpegBytes = Enumerable.Range(0, ScreenSharePayloadCodec.MaxChunkRawBytes * 2 + 17)
            .Select(i => (byte)(i % 251))
            .ToArray();

        await sender.EnqueueFrameAsync("session-a", 7, 1280, 720, "jpeg", jpegBytes, CancellationToken.None);
        await allChunksSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(expectedChunkCount, sentPayloads.Count);

        for (var i = 0; i < sentPayloads.Count; i++)
        {
            Assert.True(ScreenSharePayloadCodec.TryDeserialize(sentPayloads[i], out var chunk));
            Assert.Equal("session-a", chunk.SessionId);
            Assert.Equal(7, chunk.FrameId);
            Assert.Equal(1280, chunk.Width);
            Assert.Equal(720, chunk.Height);
            Assert.Equal("jpeg", chunk.Encoding);
            Assert.Equal(i, chunk.ChunkIndex);
            Assert.Equal(expectedChunkCount, chunk.ChunkCount);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSender_WhenBusy_KeepsOnlyNewestPendingFrame()
    {
        var sentFrameIds = new List<long>();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalFramesSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        await using var sender = new ScreenShareFrameSender(
            sendPayloadAsync: async (payload, _) =>
            {
                Assert.True(ScreenSharePayloadCodec.TryDeserialize(payload, out var chunk));
                lock (sentFrameIds)
                {
                    sentFrameIds.Add(chunk.FrameId);
                    if (sentFrameIds.Count == 2)
                    {
                        finalFramesSent.TrySetResult(true);
                    }
                }

                if (Interlocked.Increment(ref startedCount) == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstSend.Task;
                }
            },
            isTransportEnabled: true);

        await sender.EnqueueFrameAsync("session-b", 1, 800, 600, "jpeg", new byte[] { 1, 2, 3 }, CancellationToken.None);
        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await sender.EnqueueFrameAsync("session-b", 2, 800, 600, "jpeg", new byte[] { 4, 5, 6 }, CancellationToken.None);
        await sender.EnqueueFrameAsync("session-b", 3, 800, 600, "jpeg", new byte[] { 7, 8, 9 }, CancellationToken.None);

        releaseFirstSend.TrySetResult(true);
        await finalFramesSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new long[] { 1, 3 }, sentFrameIds);
    }

    [Fact]
    public async Task ScreenShareFrameSender_DebugLogs_EmitChunkSendEntries()
    {
        using var writer = new StringWriter();
        using var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);

        try
        {
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var sender = new ScreenShareFrameSender(
                sendPayloadAsync: (_, _) =>
                {
                    sent.TrySetResult(true);
                    return Task.CompletedTask;
                },
                isTransportEnabled: true);

            await sender.EnqueueFrameAsync("session-log", 99, 640, 360, "jpeg", new byte[] { 10, 11, 12, 13 }, CancellationToken.None);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            listener.Flush();

            var logs = writer.ToString();
#if DEBUG
            Assert.Contains("[ScreenShareSender] Sending screenshare chunk", logs);
#else
            Assert.True(string.IsNullOrEmpty(logs) || !logs.Contains("[ScreenShareSender]"));
#endif
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}
