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
        var expectedChunkCount = 3;
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: expectedChunkCount);

        await using var sender = new ScreenShareFrameSender(
            sendPayloadAsync: probe.SendPayloadAsync,
            isTransportEnabled: true);

        var jpegBytes = Enumerable.Range(0, ScreenSharePayloadCodec.MaxChunkRawBytes * 2 + 17)
            .Select(i => (byte)(i % 251))
            .ToArray();

        await sender.EnqueueFrameAsync("session-a", 7, 1280, 720, "jpeg", jpegBytes, CancellationToken.None);
        await probe.WaitForPayloadCountAsync(expectedChunkCount, TimeSpan.FromSeconds(2));

        var sentPayloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(expectedChunkCount, probe.PayloadsSent);
        Assert.Equal(expectedChunkCount, sentPayloads.Length);

        for (var i = 0; i < sentPayloads.Length; i++)
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
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);

        await using var sender = new ScreenShareFrameSender(
            sendPayloadAsync: probe.SendPayloadAsync,
            isTransportEnabled: true);

        await sender.EnqueueFrameAsync("session-b", 1, 800, 600, "jpeg", new byte[] { 1, 2, 3 }, CancellationToken.None);
        await probe.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(2));

        await sender.EnqueueFrameAsync("session-b", 2, 800, 600, "jpeg", new byte[] { 4, 5, 6 }, CancellationToken.None);
        await sender.EnqueueFrameAsync("session-b", 3, 800, 600, "jpeg", new byte[] { 7, 8, 9 }, CancellationToken.None);

        probe.ReleaseBlockedSends();
        await probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2));

        var sentFrameIds = probe.GetRecentPayloadsSnapshot()
            .Select(payload =>
            {
                Assert.True(ScreenSharePayloadCodec.TryDeserialize(payload, out var chunk));
                return chunk.FrameId;
            })
            .ToArray();

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
