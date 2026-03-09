using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

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
        Assert.InRange(serialized.Length, 1, ScreenSharePayloadCodec.MaxSerializedFramePayloadBytes);
        Assert.InRange(
            NknBridgePayloadAccounting.MeasureSendCommandJsonlBytes("peer.test", serialized, "1"),
            1,
            24_000);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenSharePayloadCodec_Serialize_MaxChunkRawBytes_StaysWithinBudget_ForRealisticBase64Payload()
    {
        var payload = new byte[ScreenSharePayloadCodec.MaxChunkRawBytes];
        new Random(12345).NextBytes(payload);

        var serialized = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
        {
            SessionId = "screenshare-soak",
            FrameId = 2,
            Width = 960,
            Height = 540,
            TimestampUnixMilliseconds = 1_762_000_000_000,
            Encoding = "jpeg",
            ChunkIndex = 1,
            ChunkCount = 5,
            DataBase64 = Convert.ToBase64String(payload),
        });

        Assert.InRange(serialized.Length, 1, ScreenSharePayloadCodec.MaxSerializedFramePayloadBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenSharePayloadCodec_Serialize_OmitsOptionalKind_ForFrameChunks()
    {
        var payload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
        {
            SessionId = "stream-kind",
            FrameId = 5,
            Width = 1280,
            Height = 720,
            TimestampUnixMilliseconds = 123456789,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        });

        var json = System.Text.Encoding.UTF8.GetString(payload);
        Assert.DoesNotContain("\"kind\"", json, StringComparison.Ordinal);
        Assert.True(ScreenSharePayloadCodec.TryDeserialize(payload, out var chunk));
        Assert.Equal("screenshare", chunk.Kind);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenSharePayloadCodec_Serialize_OversizePayload_IncludesChunkDiagnostics()
    {
        var oversizedBase64 = new string('A', 40_000);
        var exception = Assert.Throws<InvalidOperationException>(() => ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
        {
            SessionId = "stream-oversize",
            FrameId = 17,
            Width = 1280,
            Height = 720,
            TimestampUnixMilliseconds = 123456789,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 2,
            DataBase64 = oversizedBase64,
        }));

        Assert.Contains("serialized_bytes=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("budget_bytes=18000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("raw_chunk_bytes=30000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("base64_bytes=40000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("frame_id=17", exception.Message, StringComparison.Ordinal);
        Assert.Contains("frame=1280x720", exception.Message, StringComparison.Ordinal);
        Assert.Contains("chunk=1/2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("encoding=jpeg", exception.Message, StringComparison.Ordinal);
    }
}
