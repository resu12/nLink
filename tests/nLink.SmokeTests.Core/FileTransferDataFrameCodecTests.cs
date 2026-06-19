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
    public void V6ManifestFrame_RoundTrips_AndNormalizesEnvelope()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV6
            {
                SessionId = " session_a ",
                TransferId = " transfer_v6_manifest ",
                FileName = " v6.bin ",
                FileSizeBytes = 8192,
                ChunkSizeBytes = 2048,
                ChunkCount = 4,
                Sha256Base64 = hash,
            });

        var parsed = FileTransferDataFrameCodec.TryDeserialize(payload, out var frame);

        var manifest = Assert.IsType<FileTransferManifestFrameV6>(frame);
        Assert.True(parsed);
        Assert.Equal("session_a", manifest.SessionId);
        Assert.Equal("transfer_v6_manifest", manifest.TransferId);
        Assert.Equal(FileTransferProtocol.ManifestFrameTypeV6, manifest.Type);
        Assert.Equal("v6.bin", manifest.FileName);
        Assert.Equal(hash, manifest.Sha256Base64);
    }

    [Theory]
    [InlineData(0, 1024, 1)]
    [InlineData(4096, 0, 1)]
    [InlineData(4096, FileTransferProtocol.MaxChunkRawBytes + 1, 1)]
    [InlineData(4096, 1024, 0)]
    [InlineData(4096, 1024, 3)]
    [InlineData(FileTransferProtocol.MaxChunkCountV4 + 1L, 1, FileTransferProtocol.MaxChunkCountV4 + 1)]
    public void V6ManifestFrame_RejectsInvalidChunkTuple(long fileSizeBytes, int chunkSizeBytes, int chunkCount)
    {
        var payload = BuildManifestFrame(fileSizeBytes, chunkSizeBytes, chunkCount);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V6ManifestFrame_RejectsInvalidChunkTupleOnSerialize()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferManifestFrameV6
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
    public void V6ReceiverStateFrame_RoundTrips_AndNormalizesMissingRanges()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferReceiverStateFrameV6
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

        var state = Assert.IsType<FileTransferReceiverStateFrameV6>(frame);
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
    public void V6ReceiverStateFrame_RejectsTruncatedPayload()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferReceiverStateFrameV6
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
    public void V6ChunkBatchFrame_RoundTrips_WithinBudget()
    {
        var payload = FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV6
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

        var batch = Assert.IsType<FileTransferChunkBatchFrameV6>(frame);
        Assert.True(parsed);
        Assert.Equal(FileTransferProtocol.ChunkBatchFrameTypeV6, batch.Type);
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
        Assert.InRange(payload.Length, 1, FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV6);
    }

    [Fact]
    public void LegacyV4BinaryFrames_RequireExplicitCodecPath()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var manifest = new FileTransferManifestFrameV4
        {
            SessionId = " session_a ",
            TransferId = " transfer_v4_manifest ",
            FileName = " v4.bin ",
            FileSizeBytes = 8192,
            ChunkSizeBytes = 2048,
            ChunkCount = 4,
            Sha256Base64 = hash,
        };
        var state = new FileTransferStateFrameV4
        {
            SessionId = "session_a",
            TransferId = "transfer_v4_state",
            Epoch = 3,
            ContiguousCommittedChunkIndex = 2,
            DurableReceivedHighestChunkIndex = 5,
            CreditUntilChunkIndexExclusive = 64,
            MissingRanges =
            [
                new FileTransferRangeV4 { StartChunkIndex = 7, ChunkCount = 2 },
            ],
            BytesCommitted = 4096,
        };
        var batch = new FileTransferChunkBatchFrameV4
        {
            SessionId = "session_a",
            TransferId = "transfer_v4_batch",
            StartChunkIndex = 2,
            ChunkCount = 2,
            DataSegments =
            [
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6 },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(manifest));

        var manifestPayload = FileTransferDataFrameCodec.SerializeLegacyV4(manifest);
        var statePayload = FileTransferDataFrameCodec.SerializeLegacyV4(state);
        var batchPayload = FileTransferDataFrameCodec.SerializeLegacyV4(batch);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(manifestPayload, out _));
        Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(manifestPayload, out var decodedManifestFrame));
        Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(statePayload, out var decodedStateFrame));
        Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(batchPayload, out var decodedBatchFrame));

        var decodedManifest = Assert.IsType<FileTransferManifestFrameV4>(decodedManifestFrame);
        var decodedState = Assert.IsType<FileTransferStateFrameV4>(decodedStateFrame);
        var decodedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(decodedBatchFrame);
        Assert.Equal(FileTransferProtocol.ManifestFrameTypeV4, decodedManifest.Type);
        Assert.Equal("session_a", decodedManifest.SessionId);
        Assert.Equal(64, decodedState.CreditUntilChunkIndexExclusive);
        Assert.Equal(2, decodedBatch.DataSegments.Count);
        Assert.Equal(new byte[] { 4, 5, 6 }, decodedBatch.DataSegments[1]);
    }

    [Fact]
    public void V6RecoveryFrames_RoundTrip()
    {
        var handoffPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferTransportEpochFrameV6
            {
                SessionId = " session_a ",
                TransferId = " transfer_handoff ",
                TransportEpoch = 12,
                RecoveryMode = " nkn_proof_pending ",
            });
        var requestPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferFrontierRequestFrameV6
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
            new FileTransferRepairProofFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = " repair-1 ",
                AppliedChunkCount = 3,
                CommittedChunkIndex = 13,
                RecoveryMode = " backfill_repair ",
            });
        var normalRequestPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 0,
                RepairRequestId = " normal-frontier-1 ",
                Priority = " frontier ",
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 42, ChunkCount = 1 },
                ],
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(handoffPayload, out var handoffFrame));
        var handoff = Assert.IsType<FileTransferTransportEpochFrameV6>(handoffFrame);
        Assert.Equal("session_a", handoff.SessionId);
        Assert.Equal(12, handoff.TransportEpoch);
        Assert.Equal("nkn_proof_pending", handoff.RecoveryMode);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(requestPayload, out var requestFrame));
        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(requestFrame);
        Assert.Equal(12, request.TransportEpoch);
        Assert.Equal("repair-1", request.RepairRequestId);
        Assert.Equal("frontier", request.Priority);
        var range = Assert.Single(request.MissingRanges);
        Assert.Equal(10, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(proofPayload, out var proofFrame));
        var proof = Assert.IsType<FileTransferRepairProofFrameV6>(proofFrame);
        Assert.Equal(12, proof.TransportEpoch);
        Assert.Equal("repair-1", proof.RepairRequestId);
        Assert.Equal(3, proof.AppliedChunkCount);
        Assert.Equal(13, proof.CommittedChunkIndex);
        Assert.Equal("backfill_repair", proof.RecoveryMode);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(normalRequestPayload, out var normalRequestFrame));
        var normalRequest = Assert.IsType<FileTransferFrontierRequestFrameV6>(normalRequestFrame);
        Assert.Equal(0, normalRequest.TransportEpoch);
        Assert.Equal("normal-frontier-1", normalRequest.RepairRequestId);
    }

    [Fact]
    public void V6TransportProbeAndHeartbeatFrames_RoundTrip()
    {
        var probePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferTransportProbeFrameV6
            {
                SessionId = " session_a ",
                TransferId = " transfer_probe ",
                TransportEpoch = 12,
                ProbeId = " probe-1 ",
                TargetTransport = " tuna ",
            });
        var heartbeatPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferHeartbeatFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_probe",
                TransportEpoch = 12,
                Sequence = 7,
                SentUnixTimeMilliseconds = 1_717_171_717_000,
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(probePayload, out var probeFrame));
        var probe = Assert.IsType<FileTransferTransportProbeFrameV6>(probeFrame);
        Assert.Equal(FileTransferProtocol.TransportProbeFrameTypeV6, probe.Type);
        Assert.Equal("session_a", probe.SessionId);
        Assert.Equal(12, probe.TransportEpoch);
        Assert.Equal("probe-1", probe.ProbeId);
        Assert.Equal("tuna", probe.TargetTransport);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(heartbeatPayload, out var heartbeatFrame));
        var heartbeat = Assert.IsType<FileTransferHeartbeatFrameV6>(heartbeatFrame);
        Assert.Equal(FileTransferProtocol.HeartbeatFrameTypeV6, heartbeat.Type);
        Assert.Equal(12, heartbeat.TransportEpoch);
        Assert.Equal(7, heartbeat.Sequence);
        Assert.Equal(1_717_171_717_000, heartbeat.SentUnixTimeMilliseconds);
    }

    [Fact]
    public void RuntimeUnlockPreCommitProbeFrames_RoundTrip()
    {
        var probePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRuntimeUnlockPreCommitProbeFrame
            {
                SessionId = " session_a ",
                TransferId = " transfer_probe ",
                TransactionGeneration = 3,
                OfferGeneration = 9,
                TunaPathLeaseGeneration = 5,
                ProbeId = " probe-1 ",
                TargetRoute = " file_tuna_v4 ",
                TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                TargetTransport = " tuna ",
                HandoffKind = " normal_to_tuna_activation ",
                SentUnixTimeMilliseconds = 1_717_171_717_000,
            });
        var ackPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRuntimeUnlockPreCommitProbeAckFrame
            {
                SessionId = "session_a",
                TransferId = "transfer_probe",
                TransactionGeneration = 3,
                OfferGeneration = 9,
                TunaPathLeaseGeneration = 5,
                ProbeId = "probe-1",
                TargetRoute = "file_tuna_v4",
                TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                TargetTransport = "tuna",
                HandoffKind = "normal_to_tuna_activation",
                SentUnixTimeMilliseconds = 1_717_171_717_111,
                Accepted = true,
                Reason = " precommit_probe_received ",
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(probePayload, out var probeFrame));
        var probe = Assert.IsType<FileTransferRuntimeUnlockPreCommitProbeFrame>(probeFrame);
        Assert.Equal(FileTransferProtocol.RuntimeUnlockPreCommitProbeFrameType, probe.Type);
        Assert.Equal("session_a", probe.SessionId);
        Assert.Equal("transfer_probe", probe.TransferId);
        Assert.Equal(3, probe.TransactionGeneration);
        Assert.Equal(9, probe.OfferGeneration);
        Assert.Equal(5, probe.TunaPathLeaseGeneration);
        Assert.Equal("probe-1", probe.ProbeId);
        Assert.Equal("file_tuna_v4", probe.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, probe.TargetProtocolVersion);
        Assert.Equal("tuna", probe.TargetTransport);
        Assert.Equal("normal_to_tuna_activation", probe.HandoffKind);

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(ackPayload, out var ackFrame));
        var ack = Assert.IsType<FileTransferRuntimeUnlockPreCommitProbeAckFrame>(ackFrame);
        Assert.Equal(FileTransferProtocol.RuntimeUnlockPreCommitProbeAckFrameType, ack.Type);
        Assert.Equal("probe-1", ack.ProbeId);
        Assert.True(ack.Accepted);
        Assert.Equal("precommit_probe_received", ack.Reason);
    }

    [Fact]
    public void V6RecoveryFrames_RejectMalformedPayloads()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_handoff",
                TransportEpoch = 12,
                RepairRequestId = "repair-1",
                MissingRanges = [],
            }));

        var invalidProofPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferRepairProofFrameV6
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
    public void V6ChunkBatchFrame_RejectsMismatchedBatchCount()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV6
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
    public void V6ChunkBatchFrame_RejectsSegmentCountAboveProtocolMaximum()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_too_many_segments",
                StartChunkIndex = 0,
                ChunkCount = FileTransferProtocol.MaxChunkBatchSegmentsV6 + 1,
                DataSegments = Enumerable
                    .Range(0, FileTransferProtocol.MaxChunkBatchSegmentsV6 + 1)
                    .Select(static _ => new byte[] { 1 })
                    .ToArray(),
            }));
    }

    [Theory]
    [InlineData(FileTransferProtocol.MaxChunkCountV4, 1)]
    [InlineData(FileTransferProtocol.MaxChunkCountV4 - 1, 2)]
    [InlineData(int.MaxValue, 1)]
    public void V6ChunkBatchFrame_RejectsOutOfProtocolChunkRanges(int startChunkIndex, int chunkCount)
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV6
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
    public void V6ChunkBatchFrame_RejectsUntrustedBinarySegmentCountBeforeReadingSegments()
    {
        var payload = BuildChunkBatchHeaderWithSegmentCount(FileTransferProtocol.MaxChunkBatchSegmentsV6 + 1);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(payload, out _));
    }

    [Fact]
    public void V6ReceiverStateFrame_RejectsOutOfProtocolMissingRange()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferReceiverStateFrameV6
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
    public void V6CompleteCancelErrorAndPauseFrames_RoundTrip()
    {
        var hash = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var completePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCompleteFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_complete",
                FileSizeBytes = 4096,
                Sha256Base64 = hash,
            });
        var cancelPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferCancelFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_cancel",
                Reason = "user_canceled",
            });
        var errorPayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferErrorFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_error",
                ErrorCode = "runtime_unavailable",
                Message = "not ready",
            });
        var pausePayload = FileTransferDataFrameCodec.Serialize(
            new FileTransferPauseControlFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_pause",
                Epoch = 3,
                Paused = true,
                Reason = " user_pause ",
            });

        Assert.True(FileTransferDataFrameCodec.TryDeserialize(completePayload, out var completeFrame));
        Assert.Equal(hash, Assert.IsType<FileTransferCompleteFrameV6>(completeFrame).Sha256Base64);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(cancelPayload, out var cancelFrame));
        Assert.Equal("user_canceled", Assert.IsType<FileTransferCancelFrameV6>(cancelFrame).Reason);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(errorPayload, out var errorFrame));
        var error = Assert.IsType<FileTransferErrorFrameV6>(errorFrame);
        Assert.Equal("runtime_unavailable", error.ErrorCode);
        Assert.Equal("not ready", error.Message);
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(pausePayload, out var pauseFrame));
        var pause = Assert.IsType<FileTransferPauseControlFrameV6>(pauseFrame);
        Assert.Equal(FileTransferProtocol.PauseControlFrameTypeV6, pause.Type);
        Assert.Equal(3, pause.Epoch);
        Assert.True(pause.Paused);
        Assert.Equal("user_pause", pause.Reason);
    }

    [Fact]
    public void V6ReceiverStateFrame_RejectsInvalidOrOversizedMissingRanges()
    {
        var invalidRangePayload = BuildStateFrameWithSingleMissingRange(-1, 1);
        var tooManyChunksPayload = BuildStateFrameWithSingleMissingRange(
            10,
            FileTransferProtocol.MaxStateMissingChunksV6 + 1);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(invalidRangePayload, out _));
        Assert.False(FileTransferDataFrameCodec.TryDeserialize(tooManyChunksPayload, out _));
    }

    [Fact]
    public void V6ChunkBatchFrame_RejectsOversizedPackedBatch()
    {
        Assert.Throws<InvalidOperationException>(() => FileTransferDataFrameCodec.Serialize(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = "session_a",
                TransferId = "transfer_v4_oversized_batch",
                StartChunkIndex = 0,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[FileTransferProtocol.MaxChunkBatchRawBytesV6],
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

    [Fact]
    public void UnknownV6BinaryFrameCode_IsRejected()
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(52);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_unknown_v6_binary");
        WriteV6Metadata(buffer);

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(buffer.ToArray(), out _));
    }

    [Fact]
    public void V6BinaryFrame_RejectsMalformedMetadata()
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(43);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_bad_v6_metadata");
        WriteInt64(buffer, 1);
        buffer.WriteByte(1);
        WriteUInt16(buffer, 8);
        buffer.WriteByte((byte)'x');

        Assert.False(FileTransferDataFrameCodec.TryDeserialize(buffer.ToArray(), out _));
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
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    public void ObsoleteV5BinaryFrameCodes_AreRejected(byte frameCode)
    {
        using var buffer = new MemoryStream();
        WriteUInt32(buffer, 0x3246544E);
        buffer.WriteByte(1);
        buffer.WriteByte(frameCode);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_obsolete_v5_binary_rejected");
        WriteV6Metadata(buffer);

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
        buffer.WriteByte(42);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v6_malicious_segment_count");
        WriteV6Metadata(buffer);
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
        buffer.WriteByte(40);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v6_bad_manifest_tuple");
        WriteV6Metadata(buffer);
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
        buffer.WriteByte(41);
        WriteString(buffer, "session_a");
        WriteString(buffer, "transfer_v6_bad_state");
        WriteV6Metadata(buffer);
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

    private static void WriteV6Metadata(Stream stream)
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

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
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
