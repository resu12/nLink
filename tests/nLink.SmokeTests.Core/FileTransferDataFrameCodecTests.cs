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
    [InlineData("bad", "Current", false)]
    public void PayloadEfficiencyProfile_ParsesKnownProfiles(string? value, string expectedName, bool expectedResult)
    {
        var result = FileTransferPayloadEfficiencyProfile.TryParse(value, out var profile);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedName, profile.Name);
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
                TransferPaused = true,
                TransferPauseReason = " user_pause ",
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
        Assert.True(state.TransferPaused);
        Assert.Equal("user_pause", state.TransferPauseReason);
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
    public void V4StateFrame_DecodesLegacyPayloadWithoutPeerPauseFields()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferStateFrameV4
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_legacy_state",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 4,
                BytesCommitted = 0,
            });
        var legacyPayload = payload[..^2];

        var parsed = FileTransferDataFrameCodec.TryDeserialize(legacyPayload, out var frame);

        var state = Assert.IsType<FileTransferStateFrameV4>(frame);
        Assert.True(parsed);
        Assert.False(state.TransferPaused);
        Assert.Null(state.TransferPauseReason);
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

    [Fact]
    public void LegacyFrameTypes_AreNotDecoded()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = FileTransferProtocol.Kind,
            Type = "chunk.legacy",
            SessionId = "session_a",
            TransferId = "transfer_legacy",
        });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }
}
