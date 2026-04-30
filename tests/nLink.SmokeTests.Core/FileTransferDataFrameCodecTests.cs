using NLink.Core.FileTransfer;
using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferDataFrameCodecTests
{
    [Theory]
    [InlineData(null, "Current", true)]
    [InlineData("Current", "Current", true)]
    [InlineData("Packed3x20KiB", "Packed3x20KiB", true)]
    [InlineData("Packed3x21KiB", "Packed3x21KiB", true)]
    [InlineData("LargeSingle48KiB", "LargeSingle48KiB", true)]
    [InlineData("NotAProfile", "Current", false)]
    public void PayloadEfficiencyProfile_ParsesKnownProfiles(string? value, string expectedName, bool expectedResult)
    {
        var parsed = FileTransferPayloadEfficiencyProfile.TryParse(value, out var profile);

        Assert.Equal(expectedResult, parsed);
        Assert.Equal(expectedName, profile.Name);
    }

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

    [Fact]
    public void V3ChunkBatchFrame_AllowsPayloadEfficiencyPackedThreeChunkBatch()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV3
            {
                SessionId = "session_a",
                TransferId = "transfer_packed_v3",
                StartChunkIndex = 0,
                ChunkCount = 3,
                DataSegments =
                [
                    new byte[21 * 1024],
                    new byte[21 * 1024],
                    new byte[21 * 1024],
                ],
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var batch = Assert.IsType<FileTransferChunkBatchFrameV3>(frame);
        Assert.True(parsed);
        Assert.Equal(3, batch.DataSegments.Count);
        Assert.All(batch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV3);
    }

    [Fact]
    public void V3ChunkBatchFrame_RejectsOversizedPackedBatch()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV3
            {
                SessionId = "session_a",
                TransferId = "transfer_oversized_v3",
                StartChunkIndex = 0,
                ChunkCount = 3,
                DataSegments =
                [
                    new byte[22 * 1024],
                    new byte[22 * 1024],
                    new byte[22 * 1024],
                ],
            }));
    }

    [Fact]
    public void V3RepairRequestSetFrame_RoundTrips_AndNormalizesRanges()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairRequestSetFrameV3
            {
                SessionId = " session_a ",
                TransferId = " transfer_a ",
                Ranges =
                [
                    new FileTransferRepairRangeV3 { StartChunkIndex = 10, RequestedChunkCount = 2 },
                    new FileTransferRepairRangeV3 { StartChunkIndex = 12, RequestedChunkCount = 1 },
                    new FileTransferRepairRangeV3 { StartChunkIndex = 3, RequestedChunkCount = 1 },
                ],
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var repairSet = Assert.IsType<FileTransferRepairRequestSetFrameV3>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", repairSet.SessionId);
        Assert.Equal("transfer_a", repairSet.TransferId);
        Assert.Collection(
            repairSet.Ranges,
            range =>
            {
                Assert.Equal(3, range.StartChunkIndex);
                Assert.Equal(1, range.RequestedChunkCount);
            },
            range =>
            {
                Assert.Equal(10, range.StartChunkIndex);
                Assert.Equal(3, range.RequestedChunkCount);
            });
    }

    [Fact]
    public void V3RepairRequestSetFrame_RejectsInvalidRanges()
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new FileTransferRepairRequestSetFrameV3
            {
                SessionId = "session_a",
                TransferId = "transfer_a",
                Ranges =
                [
                    new FileTransferRepairRangeV3 { StartChunkIndex = -1, RequestedChunkCount = 1 },
                ],
            });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V4ManifestFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV4
            {
                SessionId = " session_a ",
                TransferId = " transfer_v4_manifest ",
                FileName = " v4.bin ",
                FileSizeBytes = 8192,
                ChunkSizeBytes = 2048,
                ChunkCount = 4,
                Sha256Base64 = hash,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var manifest = Assert.IsType<FileTransferManifestFrameV4>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", manifest.SessionId);
        Assert.Equal("transfer_v4_manifest", manifest.TransferId);
        Assert.Equal(FileTransferProtocol.ManifestFrameTypeV4, manifest.Type);
        Assert.Equal("v4.bin", manifest.FileName);
        Assert.Equal(hash, manifest.Sha256Base64);
    }

    [Fact]
    public void V4StateFrame_RoundTrips_AndNormalizesMissingRanges()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferStateFrameV4
            {
                SessionId = " session_a ",
                TransferId = " transfer_v4_state ",
                Epoch = 7,
                ContiguousCommittedChunkIndex = 11,
                DurableReceivedHighestChunkIndex = 80,
                CreditUntilChunkIndexExclusive = 120,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 30, ChunkCount = 2 },
                    new FileTransferRangeV4 { StartChunkIndex = 32, ChunkCount = 3 },
                    new FileTransferRangeV4 { StartChunkIndex = 18, ChunkCount = 1 },
                ],
                BytesCommitted = 44_032,
                ReceiverMemoryPressure = true,
                ReceiverDiskPressure = false,
                TerminalReady = true,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var state = Assert.IsType<FileTransferStateFrameV4>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", state.SessionId);
        Assert.Equal("transfer_v4_state", state.TransferId);
        Assert.Equal(7, state.Epoch);
        Assert.True(state.ReceiverMemoryPressure);
        Assert.False(state.ReceiverDiskPressure);
        Assert.True(state.TerminalReady);
        Assert.Collection(
            state.MissingRanges,
            range =>
            {
                Assert.Equal(18, range.StartChunkIndex);
                Assert.Equal(1, range.ChunkCount);
            },
            range =>
            {
                Assert.Equal(30, range.StartChunkIndex);
                Assert.Equal(5, range.ChunkCount);
            });
    }

    [Fact]
    public void V4ChunkBatchFrame_RoundTrips_WithinBudget()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_batch",
                StartChunkIndex = 4,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[] { 1, 2, 3 },
                    new byte[] { 4, 5 },
                ],
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var batch = Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
        Assert.True(parsed);
        Assert.Equal(FileTransferProtocol.ChunkBatchFrameTypeV4, batch.Type);
        Assert.Equal(4, batch.StartChunkIndex);
        Assert.Equal(2, batch.ChunkCount);
        Assert.Equal(2, batch.DataSegments.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, batch.DataSegments[0]);
        Assert.Equal(new byte[] { 4, 5 }, batch.DataSegments[1]);
        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4);
    }

    [Fact]
    public void V4ChunkBatchFrame_RejectsMismatchedBatchCount()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_mismatch_batch",
                StartChunkIndex = 4,
                ChunkCount = 16,
                DataSegments =
                [
                    new byte[] { 1, 2, 3 },
                    new byte[] { 4, 5 },
                ],
            }));
    }

    [Fact]
    public void V4CompleteCancelAndErrorFrames_RoundTrip()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var completePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCompleteFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_complete",
                FileSizeBytes = 4096,
                Sha256Base64 = hash,
            });
        var cancelPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCancelFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_cancel",
                Reason = "user_canceled",
            });
        var errorPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferErrorFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_error",
                ErrorCode = "runtime_unavailable",
                Message = "not ready",
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(completePayload, out var completeFrame));
        Assert.Equal(hash, Assert.IsType<FileTransferCompleteFrameV4>(completeFrame).Sha256Base64);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(cancelPayload, out var cancelFrame));
        Assert.Equal("user_canceled", Assert.IsType<FileTransferCancelFrameV4>(cancelFrame).Reason);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(errorPayload, out var errorFrame));
        var error = Assert.IsType<FileTransferErrorFrameV4>(errorFrame);
        Assert.Equal("runtime_unavailable", error.ErrorCode);
        Assert.Equal("not ready", error.Message);
    }

    [Fact]
    public void V4StateFrame_RejectsInvalidOrOversizedMissingRanges()
    {
        var invalidRangePayload = JsonSerializer.SerializeToUtf8Bytes(
            new FileTransferStateFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_bad_state",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 4,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = -1, ChunkCount = 1 },
                ],
                BytesCommitted = 0,
            });
        var tooManyChunksPayload = JsonSerializer.SerializeToUtf8Bytes(
            new FileTransferStateFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_bad_state_many",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 1000,
                CreditUntilChunkIndexExclusive = 1000,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = FileTransferProtocol.MaxStateMissingChunksV4 + 1 },
                ],
                BytesCommitted = 0,
            });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(invalidRangePayload, out _));
        Assert.False(FileTransferDataFrameCodec.TryDeserialize(tooManyChunksPayload, out _));
    }

    [Fact]
    public void V4ChunkBatchFrame_RejectsOversizedPackedBatch()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_oversized_batch",
                StartChunkIndex = 0,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[FileTransferProtocol.MaxChunkBatchRawBytesV4],
                    new byte[1],
                ],
            }));
    }
}
