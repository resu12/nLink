using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameAssemblerTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameAssembler_ReassemblesCompleteFrame()
    {
        var assembler = new ScreenShareFrameAssembler();
        ScreenShareFrameCompletedEventArgs? completed = null;
        assembler.FrameCompleted += (_, frame) => completed = frame;

        var frameBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        assembler.OnChunk(BuildChunk(frameId: 10, chunkIndex: 0, chunkCount: 2, bytes: frameBytes[..4]));
        assembler.OnChunk(BuildChunk(frameId: 10, chunkIndex: 1, chunkCount: 2, bytes: frameBytes[4..]));

        Assert.NotNull(completed);
        Assert.Equal(10, completed!.FrameId);
        Assert.Equal(1280, completed.Width);
        Assert.Equal(720, completed.Height);
        Assert.Equal("jpeg", completed.Encoding);
        Assert.Equal(frameBytes, completed.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameAssembler_NewerFrame_PreemptsOlderIncompleteFrame()
    {
        var assembler = new ScreenShareFrameAssembler();
        var completedFrames = new List<ScreenShareFrameCompletedEventArgs>();
        assembler.FrameCompleted += (_, frame) => completedFrames.Add(frame);

        assembler.OnChunk(BuildChunk(frameId: 1, chunkIndex: 0, chunkCount: 2, bytes: new byte[] { 1, 2 }));
        assembler.OnChunk(BuildChunk(frameId: 2, chunkIndex: 0, chunkCount: 1, bytes: new byte[] { 9, 8, 7 }));
        assembler.OnChunk(BuildChunk(frameId: 1, chunkIndex: 1, chunkCount: 2, bytes: new byte[] { 3, 4 }));

        var frame = Assert.Single(completedFrames);
        Assert.Equal(2, frame.FrameId);
        Assert.Equal(new byte[] { 9, 8, 7 }, frame.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameAssembler_IgnoresInvalidOrOutOfRangeChunks()
    {
        var assembler = new ScreenShareFrameAssembler();
        ScreenShareFrameCompletedEventArgs? completed = null;
        assembler.FrameCompleted += (_, frame) => completed = frame;

        assembler.OnChunk(BuildChunk(frameId: 5, chunkIndex: 2, chunkCount: 2, bytes: new byte[] { 1 }));
        assembler.OnChunk(BuildChunk(frameId: 5, chunkIndex: 0, chunkCount: 2, bytes: new byte[] { 2, 3 }, dataBase64: "%%%"));
        assembler.OnChunk(BuildChunk(frameId: 5, chunkIndex: 0, chunkCount: 2, bytes: new byte[] { 4, 5 }));
        assembler.OnChunk(BuildChunk(frameId: 5, chunkIndex: 1, chunkCount: 3, bytes: new byte[] { 6, 7 }));
        Assert.Null(completed);

        assembler.OnChunk(BuildChunk(frameId: 5, chunkIndex: 1, chunkCount: 2, bytes: new byte[] { 6, 7 }));

        Assert.NotNull(completed);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, completed!.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameAssembler_BacklogPressure_CompletesLatestFrameOnly()
    {
        var assembler = new ScreenShareFrameAssembler();
        var completedFrames = new List<ScreenShareFrameCompletedEventArgs>();
        assembler.FrameCompleted += (_, frame) => completedFrames.Add(frame);

        const int totalFrames = 200;
        for (var frameId = 1; frameId <= totalFrames; frameId++)
        {
            assembler.OnChunk(BuildChunk(
                frameId: frameId,
                chunkIndex: 0,
                chunkCount: 2,
                bytes: new byte[] { (byte)(frameId & 0xFF) }));

            if (frameId > 1)
            {
                // Simulate stale tail chunks arriving after a newer frame has already started.
                assembler.OnChunk(BuildChunk(
                    frameId: frameId - 1,
                    chunkIndex: 1,
                    chunkCount: 2,
                    bytes: new byte[] { 0x7F }));
            }
        }

        assembler.OnChunk(BuildChunk(
            frameId: totalFrames,
            chunkIndex: 1,
            chunkCount: 2,
            bytes: new byte[] { 0x55 }));

        var completed = Assert.Single(completedFrames);
        Assert.Equal(totalFrames, completed.FrameId);
        Assert.Equal(new byte[] { (byte)(totalFrames & 0xFF), 0x55 }, completed.EncodedFrameBytes);

        var metrics = assembler.GetMetricsSnapshot();
        Assert.Equal(1, metrics.FramesCompleted);
        Assert.True(metrics.AssembliesResetNewerFrame >= totalFrames - 2);
        Assert.True(metrics.ChunksDroppedOlderFrame >= totalFrames - 2);
        Assert.Equal(0, metrics.FramesTooLargeDropped);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameAssembler_DropsCompletedFrame_WhenFrameAgeExceedsCutoff()
    {
        var assembler = new ScreenShareFrameAssembler();
        ScreenShareFrameCompletedEventArgs? completed = null;
        assembler.FrameCompleted += (_, frame) => completed = frame;

        var staleTimestampUnixMilliseconds = DateTimeOffset.UtcNow.AddMilliseconds(-2500).ToUnixTimeMilliseconds();
        assembler.OnChunk(BuildChunk(frameId: 99, chunkIndex: 0, chunkCount: 2, bytes: new byte[] { 1, 2 }, timestampUnixMilliseconds: staleTimestampUnixMilliseconds));
        assembler.OnChunk(BuildChunk(frameId: 99, chunkIndex: 1, chunkCount: 2, bytes: new byte[] { 3, 4 }, timestampUnixMilliseconds: staleTimestampUnixMilliseconds));

        Assert.Null(completed);
        var metrics = assembler.GetMetricsSnapshot();
        Assert.Equal(1, metrics.FramesDroppedStaleAge);
        Assert.Equal("degraded", metrics.FreshnessMode);
    }

    private static ScreenShareFrameChunkV1 BuildChunk(long frameId, int chunkIndex, int chunkCount, byte[] bytes, string? dataBase64 = null, long timestampUnixMilliseconds = 0)
    {
        return new ScreenShareFrameChunkV1
        {
            SessionId = "session-test",
            FrameId = frameId,
            Width = 1280,
            Height = 720,
            Encoding = "jpeg",
            TimestampUnixMilliseconds = timestampUnixMilliseconds,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
            DataBase64 = dataBase64 ?? Convert.ToBase64String(bytes),
        };
    }
}
