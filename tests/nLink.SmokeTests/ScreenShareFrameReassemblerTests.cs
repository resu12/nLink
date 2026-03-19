using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameReassemblerTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_ReassemblesOutOfOrderChunks()
    {
        var reassembler = new ScreenShareFrameReassembler();
        ScreenShareFrameReadyEventArgs? completed = null;
        reassembler.FrameReady += (_, frame) => completed = frame;

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        reassembler.OnChunk(BuildChunk("session-a", 10, 1, 2, payload[4..], timestampUnixMilliseconds: 123));
        reassembler.OnChunk(BuildChunk("session-a", 10, 0, 2, payload[..4], timestampUnixMilliseconds: 123));

        Assert.NotNull(completed);
        Assert.Equal("session-a", completed!.SessionId);
        Assert.Equal(10, completed.FrameId);
        Assert.Equal(1280, completed.Width);
        Assert.Equal(720, completed.Height);
        Assert.Equal(123, completed.TimestampUnixMilliseconds);
        Assert.Equal("jpeg", completed.Encoding);
        Assert.Equal(payload, completed.EncodedFrameBytes);
        Assert.Equal(1, reassembler.GetMetricsSnapshot().FramesCompleted);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_ReassemblesInterleavedFramesAcrossSessions()
    {
        var reassembler = new ScreenShareFrameReassembler();
        var completed = new List<ScreenShareFrameReadyEventArgs>();
        reassembler.FrameReady += (_, frame) => completed.Add(frame);

        reassembler.OnChunk(BuildChunk("session-a", 1, 0, 2, new byte[] { 1, 2 }, timestampUnixMilliseconds: 10));
        reassembler.OnChunk(BuildChunk("session-b", 7, 0, 2, new byte[] { 9, 8 }, timestampUnixMilliseconds: 20));
        reassembler.OnChunk(BuildChunk("session-a", 1, 1, 2, new byte[] { 3, 4 }, timestampUnixMilliseconds: 10));
        reassembler.OnChunk(BuildChunk("session-b", 7, 1, 2, new byte[] { 7, 6 }, timestampUnixMilliseconds: 20));

        Assert.Equal(2, completed.Count);
        Assert.Contains(completed, frame => frame.SessionId == "session-a" && frame.FrameId == 1 && frame.EncodedFrameBytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains(completed, frame => frame.SessionId == "session-b" && frame.FrameId == 7 && frame.EncodedFrameBytes.SequenceEqual(new byte[] { 9, 8, 7, 6 }));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_PreservesAlternatingFrameDimensions()
    {
        var reassembler = new ScreenShareFrameReassembler();
        var completed = new List<ScreenShareFrameReadyEventArgs>();
        reassembler.FrameReady += (_, frame) => completed.Add(frame);

        reassembler.OnChunk(BuildChunk("session-a", 11, 0, 2, new byte[] { 1, 2 }, 100, width: 1280, height: 720));
        reassembler.OnChunk(BuildChunk("session-a", 11, 1, 2, new byte[] { 3, 4 }, 100, width: 1280, height: 720));
        reassembler.OnChunk(BuildChunk("session-a", 12, 0, 2, new byte[] { 5, 6 }, 101, width: 640, height: 360));
        reassembler.OnChunk(BuildChunk("session-a", 12, 1, 2, new byte[] { 7, 8 }, 101, width: 640, height: 360));

        Assert.Equal(2, completed.Count);
        Assert.Contains(completed, frame =>
            frame.FrameId == 11 &&
            frame.Width == 1280 &&
            frame.Height == 720 &&
            frame.EncodedFrameBytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains(completed, frame =>
            frame.FrameId == 12 &&
            frame.Width == 640 &&
            frame.Height == 360 &&
            frame.EncodedFrameBytes.SequenceEqual(new byte[] { 5, 6, 7, 8 }));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_DropsOldestIncompleteFrame_WhenNewerFramesExceedCapacity()
    {
        var reassembler = new ScreenShareFrameReassembler();
        var completed = new List<ScreenShareFrameReadyEventArgs>();
        reassembler.FrameReady += (_, frame) => completed.Add(frame);

        reassembler.OnChunk(BuildChunk("session-a", 1, 0, 2, new byte[] { 1 }, timestampUnixMilliseconds: 1));
        reassembler.OnChunk(BuildChunk("session-a", 2, 0, 2, new byte[] { 2 }, timestampUnixMilliseconds: 2));
        reassembler.OnChunk(BuildChunk("session-a", 3, 0, 1, new byte[] { 3, 4 }, timestampUnixMilliseconds: 3));
        reassembler.OnChunk(BuildChunk("session-a", 1, 1, 2, new byte[] { 9 }, timestampUnixMilliseconds: 1));
        reassembler.OnChunk(BuildChunk("session-a", 2, 1, 2, new byte[] { 5 }, timestampUnixMilliseconds: 2));

        var frame = Assert.Single(completed);
        Assert.DoesNotContain(completed, frame => frame.FrameId == 1);
        Assert.DoesNotContain(completed, frame => frame.FrameId == 2);
        Assert.Equal(3, frame.FrameId);
        Assert.Equal(new byte[] { 3, 4 }, frame.EncodedFrameBytes);
        Assert.True(reassembler.GetMetricsSnapshot().FramesDropped >= 2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_RejectsInvalidChunkIndices_AndMalformedBase64()
    {
        var reassembler = new ScreenShareFrameReassembler();
        var completed = new List<ScreenShareFrameReadyEventArgs>();
        reassembler.FrameReady += (_, frame) => completed.Add(frame);

        reassembler.OnChunk(BuildChunk("session-a", 5, 2, 2, new byte[] { 1 }, timestampUnixMilliseconds: 1));
        reassembler.OnChunk(BuildChunk("session-a", 5, 0, 2, new byte[] { 2 }, timestampUnixMilliseconds: 1, dataBase64: "%%%"));
        reassembler.OnChunk(BuildChunk("session-a", 5, 0, 2, new byte[] { 3, 4 }, timestampUnixMilliseconds: 1));
        reassembler.OnChunk(BuildChunk("session-a", 5, 1, 3, new byte[] { 5, 6 }, timestampUnixMilliseconds: 1));
        reassembler.OnChunk(BuildChunk("session-a", 5, 1, 2, new byte[] { 5, 6 }, timestampUnixMilliseconds: 1));

        Assert.Empty(completed);

        reassembler.OnChunk(BuildChunk("session-a", 6, 0, 2, new byte[] { 7, 8 }, timestampUnixMilliseconds: 2));
        reassembler.OnChunk(BuildChunk("session-a", 6, 1, 2, new byte[] { 9, 10 }, timestampUnixMilliseconds: 2));

        var frame = Assert.Single(completed);
        Assert.Equal(6, frame.FrameId);
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, frame.EncodedFrameBytes);
        Assert.True(reassembler.GetMetricsSnapshot().FramesDropped >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_RejectsOversizedFrame()
    {
        var reassembler = new ScreenShareFrameReassembler();
        ScreenShareFrameReadyEventArgs? completed = null;
        reassembler.FrameReady += (_, frame) => completed = frame;

        for (var i = 0; i < 65; i++)
        {
            reassembler.OnChunk(BuildChunk(
                sessionId: "session-a",
                frameId: 8,
                chunkIndex: i,
                chunkCount: 65,
                bytes: CreateBytes(ScreenSharePayloadCodec.MaxChunkRawBytes, (byte)(i % 251)),
                timestampUnixMilliseconds: 50));
        }

        Assert.Null(completed);
        var metrics = reassembler.GetMetricsSnapshot();
        Assert.True(metrics.FramesRejectedOversize >= 1);
        Assert.True(metrics.FramesDropped >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_RejectsFrameImmediately_WhenDeclaredChunkBudgetExceedsLimit()
    {
        var reassembler = new ScreenShareFrameReassembler();
        ScreenShareFrameReadyEventArgs? completed = null;
        reassembler.FrameReady += (_, frame) => completed = frame;

        reassembler.OnChunk(BuildChunk(
            sessionId: "session-budget",
            frameId: 12,
            chunkIndex: 0,
            chunkCount: 65,
            bytes: new byte[] { 1, 2, 3 },
            timestampUnixMilliseconds: 77));

        Assert.Null(completed);
        var metrics = reassembler.GetMetricsSnapshot();
        Assert.True(metrics.FramesRejectedOversize >= 1);
        Assert.True(metrics.FramesDropped >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_DropsCompletedFrame_WhenFrameAgeExceedsCutoff()
    {
        var reassembler = new ScreenShareFrameReassembler();
        ScreenShareFrameReadyEventArgs? completed = null;
        reassembler.FrameReady += (_, frame) => completed = frame;

        var staleTimestampUnixMilliseconds = DateTimeOffset.UtcNow.AddMilliseconds(-2500).ToUnixTimeMilliseconds();
        reassembler.OnChunk(BuildChunk("session-stale", 21, 0, 2, new byte[] { 1, 2 }, staleTimestampUnixMilliseconds));
        reassembler.OnChunk(BuildChunk("session-stale", 21, 1, 2, new byte[] { 3, 4 }, staleTimestampUnixMilliseconds));

        Assert.Null(completed);
        var metrics = reassembler.GetMetricsSnapshot();
        Assert.Equal(1, metrics.FramesDroppedStaleAge);
        Assert.Equal("degraded", metrics.FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameReassembler_DegradedFreshness_KeepsNewestInFlightFrame()
    {
        var reassembler = new ScreenShareFrameReassembler();
        var completed = new List<ScreenShareFrameReadyEventArgs>();
        reassembler.FrameReady += (_, frame) => completed.Add(frame);

        var degradedTimestampUnixMilliseconds = DateTimeOffset.UtcNow.AddMilliseconds(-1700).ToUnixTimeMilliseconds();
        reassembler.OnChunk(BuildChunk("session-freshness", 1, 0, 1, new byte[] { 1 }, degradedTimestampUnixMilliseconds));
        reassembler.OnChunk(BuildChunk("session-freshness", 2, 0, 2, new byte[] { 2 }, degradedTimestampUnixMilliseconds));
        reassembler.OnChunk(BuildChunk("session-freshness", 3, 0, 2, new byte[] { 3 }, degradedTimestampUnixMilliseconds));
        reassembler.OnChunk(BuildChunk("session-freshness", 2, 1, 2, new byte[] { 9 }, degradedTimestampUnixMilliseconds));
        reassembler.OnChunk(BuildChunk("session-freshness", 3, 1, 2, new byte[] { 4 }, degradedTimestampUnixMilliseconds));

        Assert.Equal(2, completed.Count);
        Assert.DoesNotContain(completed, frame => frame.FrameId == 2);
        Assert.Contains(completed, frame => frame.FrameId == 3);
        Assert.Equal("degraded", reassembler.GetMetricsSnapshot().FreshnessMode);
    }

    private static ScreenShareFrameChunkV1 BuildChunk(
        string sessionId,
        long frameId,
        int chunkIndex,
        int chunkCount,
        byte[] bytes,
        long timestampUnixMilliseconds,
        int width = 1280,
        int height = 720,
        string? dataBase64 = null)
    {
        return new ScreenShareFrameChunkV1
        {
            SessionId = sessionId,
            FrameId = frameId,
            Width = width,
            Height = height,
            TimestampUnixMilliseconds = timestampUnixMilliseconds,
            Encoding = "jpeg",
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
            DataBase64 = dataBase64 ?? Convert.ToBase64String(bytes),
        };
    }

    private static byte[] CreateBytes(int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }
}
