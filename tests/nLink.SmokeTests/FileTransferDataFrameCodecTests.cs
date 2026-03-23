using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed class FileTransferDataFrameCodecTests
{
    [Fact]
    public void ManifestFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                FileName = " sample.bin ",
                FileSizeBytes = 4096,
                ChunkSizeBytes = 1024,
                ChunkCount = 4,
                Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var manifest = Assert.IsType<FileTransferManifestFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", manifest.SessionId);
        Assert.Equal("transfer_a", manifest.TransferId);
        Assert.Equal("sample.bin", manifest.FileName);
    }

    [Fact]
    public void RequestChunksFrame_RoundTrips()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRequestChunksFrameV2
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                StartChunkIndex = 7,
                RequestedChunkCount = 2,
                PipelineDepth = 4,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var request = Assert.IsType<FileTransferRequestChunksFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal(7, request.StartChunkIndex);
        Assert.Equal(2, request.RequestedChunkCount);
    }

    [Fact]
    public void ChunkDataFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkDataFrameV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                ChunkIndex = 3,
                ChunkCount = 8,
                DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var chunk = Assert.IsType<FileTransferChunkDataFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", chunk.SessionId);
        Assert.Equal("transfer_a", chunk.TransferId);
        Assert.Equal(3, chunk.ChunkIndex);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunk.Data);
        Assert.NotEqual((byte)'{', payload[0]);
    }

    [Fact]
    public void AckProgressFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferAckProgressFrameV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                NextExpectedChunkIndex = 5,
                BytesCommitted = 4096,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var ack = Assert.IsType<FileTransferAckProgressFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", ack.SessionId);
        Assert.Equal("transfer_a", ack.TransferId);
        Assert.Equal(5, ack.NextExpectedChunkIndex);
    }

    [Fact]
    public void ChunkBatchFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV2
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                StartChunkIndex = 3,
                ChunkCount = 8,
                DataBase64Segments =
                [
                    Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
                    Convert.ToBase64String(new byte[] { 5, 6, 7, 8 }),
                ],
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var chunkBatch = Assert.IsType<FileTransferChunkBatchFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", chunkBatch.SessionId);
        Assert.Equal("transfer_a", chunkBatch.TransferId);
        Assert.Equal(3, chunkBatch.StartChunkIndex);
        Assert.Equal(2, chunkBatch.DataSegments.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunkBatch.DataSegments[0]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, chunkBatch.DataSegments[1]);
        Assert.NotEqual((byte)'{', payload[0]);
    }

    [Fact]
    public void CancelFrame_RoundTrips()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCancelFrameV2
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                Reason = "user_canceled",
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var cancel = Assert.IsType<FileTransferCancelFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal("user_canceled", cancel.Reason);
    }

    [Fact]
    public void CompleteFrame_RoundTrips()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCompleteFrameV2
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                FileSizeBytes = 4096,
                Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var complete = Assert.IsType<FileTransferCompleteFrameV2>(frame);
        Assert.True(parsed);
        Assert.Equal(4096, complete.FileSizeBytes);
    }

    [Fact]
    public void ChunkDataFrame_RejectsOversizedPayload()
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new FileTransferChunkDataFrameV2
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[FileTransferProtocol.MaxChunkRawBytes + 1]),
            });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V3GrantWindowFrame_RoundTrips()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferGrantWindowFrameV3
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                NextExpectedChunkIndex = 5,
                GrantedUntilChunkIndexExclusive = 29,
                BytesCommitted = 4096,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var grant = Assert.IsType<FileTransferGrantWindowFrameV3>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", grant.SessionId);
        Assert.Equal("transfer_a", grant.TransferId);
        Assert.Equal(29, grant.GrantedUntilChunkIndexExclusive);
    }

    [Fact]
    public void V3ChunkDataFrame_RoundTrips()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkDataFrameV3
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                ChunkIndex = 2,
                ChunkCount = 8,
                Data = new byte[] { 1, 2, 3, 4 },
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var chunk = Assert.IsType<FileTransferChunkDataFrameV3>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", chunk.SessionId);
        Assert.Equal("transfer_a", chunk.TransferId);
        Assert.Equal(2, chunk.ChunkIndex);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunk.Data);
    }
}
