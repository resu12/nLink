using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameChunkerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(ScreenSharePayloadCodec.MaxChunkRawBytes)]
    [InlineData(ScreenSharePayloadCodec.MaxChunkRawBytes + 1)]
    [InlineData((ScreenSharePayloadCodec.MaxChunkRawBytes * 2) + 17)]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameChunker_RoundTrips_MultiplePayloadSizes(int payloadSize)
    {
        var payload = Enumerable.Range(0, payloadSize)
            .Select(i => (byte)(i % 251))
            .ToArray();

        var chunks = ScreenShareFrameChunker.ChunkFrame(
            sessionId: "stream-a",
            frameId: 7,
            width: 1280,
            height: 720,
            encoding: "jpeg",
            timestampUnixMilliseconds: 123456789,
            encodedFrameBytes: payload);

        var expectedChunkCount = (payload.Length + ScreenSharePayloadCodec.MaxChunkRawBytes - 1) / ScreenSharePayloadCodec.MaxChunkRawBytes;
        Assert.Equal(expectedChunkCount, chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal("stream-a", chunk.SessionId);
            Assert.Equal(7, chunk.FrameId);
            Assert.Equal(1280, chunk.Width);
            Assert.Equal(720, chunk.Height);
            Assert.Equal(123456789, chunk.TimestampUnixMilliseconds);
            Assert.Equal("jpeg", chunk.Encoding);
            Assert.Equal(i, chunk.ChunkIndex);
            Assert.Equal(expectedChunkCount, chunk.ChunkCount);
        }

        var reconstructed = chunks
            .OrderBy(c => c.ChunkIndex)
            .SelectMany(c => Convert.FromBase64String(c.DataBase64))
            .ToArray();

        Assert.Equal(payload, reconstructed);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameChunker_SerializedChunkPayload_StaysWithinSafeTransportBudget()
    {
        var payload = Enumerable.Range(0, ScreenSharePayloadCodec.MaxChunkRawBytes)
            .Select(i => (byte)(i % 251))
            .ToArray();

        var chunks = ScreenShareFrameChunker.ChunkFrame(
            sessionId: "stream-budget",
            frameId: 11,
            width: 1280,
            height: 720,
            encoding: "jpeg",
            timestampUnixMilliseconds: 123456789,
            encodedFrameBytes: payload);

        Assert.Single(chunks);

        var serialized = ScreenSharePayloadCodec.Serialize(chunks[0]);
        Assert.InRange(serialized.Length, 1, 16_000);
    }
}
