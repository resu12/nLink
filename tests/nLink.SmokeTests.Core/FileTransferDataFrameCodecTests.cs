using NLink.Core.FileTransfer;
using System.Buffers.Binary;
using System.Text;
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
    public void V5ManifestFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV5
            {
                SessionId = " session_a ",
                TransferId = " transfer_v5_manifest ",
                FileName = " v5.bin ",
                FileSizeBytes = 8192,
                ChunkSizeBytes = 2048,
                ChunkCount = 4,
                Sha256Base64 = hash,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var manifest = Assert.IsType<FileTransferManifestFrameV5>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", manifest.SessionId);
        Assert.Equal("transfer_v5_manifest", manifest.TransferId);
        Assert.Equal(FileTransferProtocol.ManifestFrameTypeV5, manifest.Type);
        Assert.Equal("v5.bin", manifest.FileName);
        Assert.Equal(hash, manifest.Sha256Base64);
    }

    [Theory]
    [InlineData(0, 1024, 1)]
    [InlineData(4096, 0, 1)]
    [InlineData(4096, FileTransferProtocol.MaxChunkRawBytes + 1, 1)]
    [InlineData(4096, 1024, 0)]
    [InlineData(4096, 1024, 3)]
    [InlineData(FileTransferProtocol.MaxChunkCountV4 + 1L, 1, FileTransferProtocol.MaxChunkCountV4 + 1)]
    public void V5ManifestFrame_RejectsInvalidChunkTuple(long fileSizeBytes, int chunkSizeBytes, int chunkCount)
    {
        var payload = BuildManifestFrame(fileSizeBytes, chunkSizeBytes, chunkCount);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V5ManifestFrame_RejectsInvalidChunkTupleOnSerialize()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_bad_manifest_serialize",
                FileName = "bad.bin",
                FileSizeBytes = 4096,
                ChunkSizeBytes = 1024,
                ChunkCount = 3,
                Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            }));
    }

    [Fact]
    public void V5StateFrame_RoundTrips_AndNormalizesMissingRanges()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferStateFrameV5
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

        var state = Assert.IsType<FileTransferStateFrameV5>(frame);
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
    public void V5StateFrame_RejectsTruncatedPayload()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferStateFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_legacy_state",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 4,
                BytesCommitted = 0,
            });
        var truncatedPayload = payload[..^3];

        var parsed = FileTransferDataFrameCodec.TryDeserialize(truncatedPayload, out var frame);

        Assert.False(parsed);
        Assert.Null(frame);
    }

    [Fact]
    public void V5ChunkBatchFrame_RoundTrips_WithinBudget()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_batch",
                StartChunkIndex = 4,
                ChunkCount = 2,
                TransportEpoch = 42,
                BatchId = " batch-a ",
                RepairRequestId = " repair-a ",
                Priority = " frontier ",
                RecoveryMode = " frontier_repair_only ",
                DataSegments =
                [
                    new byte[] { 1, 2, 3 },
                    new byte[] { 4, 5 },
                ],
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var batch = Assert.IsType<FileTransferChunkBatchFrameV5>(frame);
        Assert.True(parsed);
        Assert.Equal(FileTransferProtocol.ChunkBatchFrameTypeV5, batch.Type);
        Assert.Equal(4, batch.StartChunkIndex);
        Assert.Equal(2, batch.ChunkCount);
        Assert.Equal(42, batch.TransportEpoch);
        Assert.Equal("batch-a", batch.BatchId);
        Assert.Equal("repair-a", batch.RepairRequestId);
        Assert.Equal("frontier", batch.Priority);
        Assert.Equal("frontier_repair_only", batch.RecoveryMode);
        Assert.Equal(2, batch.DataSegments.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, batch.DataSegments[0]);
        Assert.Equal(new byte[] { 4, 5 }, batch.DataSegments[1]);
        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV5);
    }

    [Fact]
    public void V5RecoveryFrames_RoundTrip()
    {
        var handoffPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferHandoffFrameV5
            {
                SessionId = " session_a ",
                TransferId = " transfer_handoff ",
                TransportEpoch = 12,
                RecoveryMode = " nkn_proof_pending ",
            });
        var requestPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairRequestFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = " repair-1 ",
                Priority = " frontier ",
                RecoveryMode = " frontier_repair_only ",
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 1 },
                    new FileTransferRangeV4 { StartChunkIndex = 11, ChunkCount = 2 },
                ],
            });
        var proofPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairProofFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = " repair-1 ",
                AppliedChunkCount = 3,
                CommittedChunkIndex = 13,
                RecoveryMode = " backfill_repair ",
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(handoffPayload, out var handoffFrame));
        var handoff = Assert.IsType<FileTransferHandoffFrameV5>(handoffFrame);
        Assert.Equal("session_a", handoff.SessionId);
        Assert.Equal(12, handoff.TransportEpoch);
        Assert.Equal("nkn_proof_pending", handoff.RecoveryMode);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(requestPayload, out var requestFrame));
        var request = Assert.IsType<FileTransferRepairRequestFrameV5>(requestFrame);
        Assert.Equal(12, request.TransportEpoch);
        Assert.Equal("repair-1", request.RepairRequestId);
        Assert.Equal("frontier", request.Priority);
        var range = Assert.Single(request.MissingRanges);
        Assert.Equal(10, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(proofPayload, out var proofFrame));
        var proof = Assert.IsType<FileTransferRepairProofFrameV5>(proofFrame);
        Assert.Equal(12, proof.TransportEpoch);
        Assert.Equal("repair-1", proof.RepairRequestId);
        Assert.Equal(3, proof.AppliedChunkCount);
        Assert.Equal(13, proof.CommittedChunkIndex);
        Assert.Equal("backfill_repair", proof.RecoveryMode);
    }

    [Fact]
    public void V5RecoveryFrames_RejectMalformedPayloads()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairRequestFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = "repair-1",
                MissingRanges = [],
            }));

        var invalidProofPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairProofFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = "repair-1",
                AppliedChunkCount = 1,
                CommittedChunkIndex = 2,
            });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(invalidProofPayload[..^1], out _));
    }

    [Fact]
    public void V5ChunkBatchFrame_RejectsMismatchedBatchCount()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV5
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
    public void V5ChunkBatchFrame_RejectsSegmentCountAboveProtocolMaximum()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_too_many_segments",
                StartChunkIndex = 0,
                ChunkCount = FileTransferProtocol.MaxChunkBatchSegmentsV5 + 1,
                DataSegments = Enumerable
                    .Range(0, FileTransferProtocol.MaxChunkBatchSegmentsV5 + 1)
                    .Select(static _ => new byte[] { 1 })
                    .ToArray(),
            }));
    }

    [Theory]
    [InlineData(FileTransferProtocol.MaxChunkCountV4, 1)]
    [InlineData(FileTransferProtocol.MaxChunkCountV4 - 1, 2)]
    [InlineData(int.MaxValue, 1)]
    public void V5ChunkBatchFrame_RejectsOutOfProtocolChunkRanges(int startChunkIndex, int chunkCount)
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_range_overflow",
                StartChunkIndex = startChunkIndex,
                ChunkCount = chunkCount,
                DataSegments = Enumerable
                    .Range(0, chunkCount)
                    .Select(static _ => new byte[] { 1 })
                    .ToArray(),
            }));
    }

    [Fact]
    public void V5ChunkBatchFrame_RejectsUntrustedBinarySegmentCountBeforeReadingSegments()
    {
        var payload = BuildChunkBatchHeaderWithSegmentCount(FileTransferProtocol.MaxChunkBatchSegmentsV5 + 1);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V5StateFrame_RejectsOutOfProtocolMissingRange()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferStateFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_bad_missing_range",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 0,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = FileTransferProtocol.MaxChunkCountV4,
                        ChunkCount = 1,
                    },
                ],
                BytesCommitted = 0,
            }));
    }

    [Fact]
    public void V5CompleteCancelAndErrorFrames_RoundTrip()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var completePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCompleteFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_complete",
                FileSizeBytes = 4096,
                Sha256Base64 = hash,
            });
        var cancelPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCancelFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_cancel",
                Reason = "user_canceled",
            });
        var errorPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferErrorFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_error",
                ErrorCode = "runtime_unavailable",
                Message = "not ready",
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(completePayload, out var completeFrame));
        Assert.Equal(hash, Assert.IsType<FileTransferCompleteFrameV5>(completeFrame).Sha256Base64);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(cancelPayload, out var cancelFrame));
        Assert.Equal("user_canceled", Assert.IsType<FileTransferCancelFrameV5>(cancelFrame).Reason);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(errorPayload, out var errorFrame));
        var error = Assert.IsType<FileTransferErrorFrameV5>(errorFrame);
        Assert.Equal("runtime_unavailable", error.ErrorCode);
        Assert.Equal("not ready", error.Message);
    }

    [Fact]
    public void V5StateFrame_RejectsInvalidOrOversizedMissingRanges()
    {
        var invalidRangePayload = BuildStateFrameWithSingleMissingRange(-1, 1);
        var tooManyChunksPayload = BuildStateFrameWithSingleMissingRange(
            10,
            FileTransferProtocol.MaxStateMissingChunksV5 + 1);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(invalidRangePayload, out _));
        Assert.False(FileTransferDataFrameCodec.TryDeserialize(tooManyChunksPayload, out _));
    }

    [Fact]
    public void V5ChunkBatchFrame_RejectsOversizedPackedBatch()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV5
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_oversized_batch",
                StartChunkIndex = 0,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[FileTransferProtocol.MaxChunkBatchRawBytesV5],
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

    [Theory]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void V4BinaryFrameCodes_AreRejected(byte frameCode)
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(frameCode);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v4_binary_rejected");

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(buffer.ToArray(), out _));
    }

    [Theory]
    [InlineData(FileTransferProtocol.ManifestFrameTypeV4)]
    [InlineData(FileTransferProtocol.StateFrameTypeV4)]
    [InlineData(FileTransferProtocol.ChunkBatchFrameTypeV4)]
    [InlineData(FileTransferProtocol.SessionCompleteFrameTypeV4)]
    [InlineData(FileTransferProtocol.SessionCancelFrameTypeV4)]
    [InlineData(FileTransferProtocol.ErrorFrameTypeV4)]
    [InlineData(FileTransferProtocol.PauseControlFrameTypeV4)]
    public void V4JsonDataFrames_AreRejected(string frameType)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = FileTransferProtocol.Kind,
            Type = frameType,
            SessionId = "session_a",
            TransferId = "transfer_v4_json_rejected",
            FileName = "payload.bin",
            FileSizeBytes = 128,
            ChunkSizeBytes = 64,
            ChunkCount = 2,
            Sha256Base64 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]),
            Epoch = 1,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 0,
            CreditUntilChunkIndexExclusive = 1,
            MissingRanges = Array.Empty<FileTransferRangeV4>(),
            BytesCommitted = 0,
            StartChunkIndex = 0,
            DataSegments = new[] { new byte[] { 1 } },
            ErrorCode = "test",
            Paused = true,
        });

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    private static byte[] BuildChunkBatchHeaderWithSegmentCount(int segmentCount)
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(27);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v4_malicious_segment_count");
        WriteV5Metadata(buffer);
        WriteInt32(buffer, 0);
        WriteInt32(buffer, segmentCount);
        WriteInt32(buffer, segmentCount);
        return buffer.ToArray();
    }

    private static byte[] BuildManifestFrame(long fileSizeBytes, int chunkSizeBytes, int chunkCount)
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(25);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v4_bad_manifest_tuple");
        WriteV5Metadata(buffer);
        WriteString(buffer, "bad.bin");
        WriteInt64(buffer, fileSizeBytes);
        WriteInt32(buffer, chunkSizeBytes);
        WriteInt32(buffer, chunkCount);
        buffer.Write(new byte[FileTransferProtocol.Sha256LengthBytes]);
        return buffer.ToArray();
    }

    private static byte[] BuildStateFrameWithSingleMissingRange(int startChunkIndex, int chunkCount)
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(26);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v4_bad_state");
        WriteV5Metadata(buffer);
        WriteInt32(buffer, 1);
        WriteInt32(buffer, 0);
        WriteInt32(buffer, 1000);
        WriteInt32(buffer, 1000);
        WriteInt32(buffer, 1);
        WriteInt32(buffer, startChunkIndex);
        WriteInt32(buffer, chunkCount);
        WriteInt64(buffer, 0);
        WriteBool(buffer, false);
        WriteBool(buffer, false);
        WriteBool(buffer, false);
        WriteBool(buffer, false);
        buffer.WriteByte(0);
        return buffer.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteV5Metadata(Stream stream)
    {
        WriteInt64(stream, 0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBool(Stream stream, bool value)
        => stream.WriteByte(value ? (byte)1 : (byte)0);

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthBytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, checked((ushort)bytes.Length));
        stream.Write(lengthBytes);
        stream.Write(bytes);
    }
}
