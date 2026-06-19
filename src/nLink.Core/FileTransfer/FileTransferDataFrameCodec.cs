using System.Buffers.Binary;
using System.IO;
using System.Text;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public static class FileTransferDataFrameCodec
{
    private const uint BinaryMagic = 0x3246544E; // "NFT2"
    private const byte BinaryVersion = 1;

    public static byte[] Serialize(FileTransferDataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (FileTransferProtocol.IsV4DataFrame(frame))
        {
            throw new InvalidOperationException("Legacy V4 binary data frames are not enabled for the default V6 codec path.");
        }

        var payload = SerializeBinary(frame);
        var maxSerializedPayloadBytes = frame is FileTransferChunkBatchFrameV6
            ? FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV6
            : FileTransferProtocol.MaxSerializedChunkPayloadBytes;
        if (frame is FileTransferChunkBatchFrameV6 && payload.Length > maxSerializedPayloadBytes)
        {
            LocalOperationalLog.Warn(
                "FileTransferPayload",
                $"event=serialize_chunk_data_frame_budget_exceeded; session_id={frame.SessionId}; transfer_id={frame.TransferId}; payload_bytes={payload.Length}; budget_bytes={maxSerializedPayloadBytes}");
            throw new InvalidOperationException(
                $"Serialized file-transfer chunk data frame exceeded safe budget of {maxSerializedPayloadBytes} bytes.");
        }

        return payload;
    }

    public static byte[] SerializeLegacyV4(FileTransferDataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!FileTransferProtocol.IsV4DataFrame(frame))
        {
            throw new InvalidOperationException($"Expected a legacy V4 file-transfer data frame, got '{frame.GetType().Name}'.");
        }

        var payload = SerializeBinary(frame);
        var maxSerializedPayloadBytes = frame is FileTransferChunkBatchFrameV4
            ? FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4
            : FileTransferProtocol.MaxSerializedChunkPayloadBytes;
        if (frame is FileTransferChunkBatchFrameV4 && payload.Length > maxSerializedPayloadBytes)
        {
            LocalOperationalLog.Warn(
                "FileTransferPayload",
                $"event=serialize_chunk_data_frame_budget_exceeded; session_id={frame.SessionId}; transfer_id={frame.TransferId}; payload_bytes={payload.Length}; budget_bytes={maxSerializedPayloadBytes}");
            throw new InvalidOperationException(
                $"Serialized file-transfer chunk data frame exceeded safe budget of {maxSerializedPayloadBytes} bytes.");
        }

        return payload;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out FileTransferDataFrame? frame)
    {
        frame = null;
        if (payload.Length == 0)
        {
            return false;
        }

        return TryDeserializeBinary(payload, allowLegacyV4: false, out frame);
    }

    public static bool TryDeserializeLegacyV4(ReadOnlySpan<byte> payload, out FileTransferDataFrame? frame)
    {
        frame = null;
        if (payload.Length == 0)
        {
            return false;
        }

        return TryDeserializeBinary(payload, allowLegacyV4: true, out frame) &&
               FileTransferProtocol.IsV4DataFrame(frame);
    }

    private static byte[] SerializeBinary(FileTransferDataFrame frame)
    {
        using var buffer = new MemoryStream();
        buffer.Write(BitConverter.GetBytes(BinaryMagic));
        buffer.WriteByte(BinaryVersion);
        buffer.WriteByte(GetFrameCode(frame));
        WriteString(buffer, frame.SessionId);
        WriteString(buffer, frame.TransferId);
        if (!FileTransferProtocol.IsV4DataFrame(frame))
        {
            WriteTransportMetadata(buffer, frame);
        }

        switch (frame)
        {
            case FileTransferManifestFrameV4 manifest:
                if (!IsValidV4ManifestTuple(manifest.FileSizeBytes, manifest.ChunkSizeBytes, manifest.ChunkCount))
                {
                    throw new InvalidOperationException("Manifest chunk tuple was invalid.");
                }

                WriteString(buffer, manifest.FileName);
                WriteInt64(buffer, manifest.FileSizeBytes);
                WriteInt32(buffer, manifest.ChunkSizeBytes);
                WriteInt32(buffer, manifest.ChunkCount);
                WriteHash(buffer, manifest.Sha256Base64);
                break;
            case FileTransferStateFrameV4 state:
                if (!TryNormalizeV4MissingRanges(
                        state.MissingRanges,
                        allowEmpty: true,
                        out var normalizedMissingRanges,
                        maxRangeCount: frame is FileTransferReceiverStateFrameV6
                            ? FileTransferProtocol.MaxStateMissingRangesV6
                            : FileTransferProtocol.MaxStateMissingRangesV4,
                        maxChunkCount: frame is FileTransferReceiverStateFrameV6
                            ? FileTransferProtocol.MaxChunkCountV6
                            : FileTransferProtocol.MaxChunkCountV4,
                        maxTotalChunks: frame is FileTransferReceiverStateFrameV6
                            ? FileTransferProtocol.MaxStateMissingChunksV6
                            : FileTransferProtocol.MaxStateMissingChunksV4))
                {
                    throw new InvalidOperationException("Receiver state missing ranges payload was invalid.");
                }

                WriteInt32(buffer, state.Epoch);
                WriteInt32(buffer, state.ContiguousCommittedChunkIndex);
                WriteInt32(buffer, state.DurableReceivedHighestChunkIndex);
                WriteInt32(buffer, state.CreditUntilChunkIndexExclusive);
                WriteInt32(buffer, normalizedMissingRanges.Count);
                foreach (var range in normalizedMissingRanges)
                {
                    WriteInt32(buffer, range.StartChunkIndex);
                    WriteInt32(buffer, range.ChunkCount);
                }

                WriteInt64(buffer, state.BytesCommitted);
                WriteBool(buffer, state.ReceiverMemoryPressure);
                WriteBool(buffer, state.ReceiverDiskPressure);
                WriteBool(buffer, state.TerminalReady);
                WriteBool(buffer, state.TransferPaused);
                WriteOptionalString(buffer, state.TransferPauseReason);
                break;
            case FileTransferChunkBatchFrameV4 batch:
                if (batch.DataSegments.Count == 0)
                {
                    throw new InvalidOperationException("Chunk batch payload may not be empty.");
                }

                var maxChunkBatchSegments = frame is FileTransferChunkBatchFrameV6
                    ? FileTransferProtocol.MaxChunkBatchSegmentsV6
                    : FileTransferProtocol.MaxChunkBatchSegmentsV4;
                if (batch.DataSegments.Count > maxChunkBatchSegments)
                {
                    throw new InvalidOperationException($"Chunk batch segment count exceeded {maxChunkBatchSegments}.");
                }

                if (batch.ChunkCount != batch.DataSegments.Count)
                {
                    throw new InvalidOperationException("V4 chunk batch count must match the number of data segments.");
                }

                if (!IsValidV4ChunkRange(batch.StartChunkIndex, batch.ChunkCount))
                {
                    throw new InvalidOperationException("Chunk batch range was outside protocol bounds.");
                }

                var totalChunkBytes = 0L;
                WriteInt32(buffer, batch.StartChunkIndex);
                WriteInt32(buffer, batch.ChunkCount);
                WriteInt32(buffer, batch.DataSegments.Count);
                foreach (var segmentBytes in batch.DataSegments)
                {
                    if (segmentBytes.Length == 0)
                    {
                        throw new InvalidOperationException("Chunk batch segment payload may not be empty.");
                    }

                    totalChunkBytes += segmentBytes.Length;
                    if (segmentBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch segment payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                    }

                    var maxChunkBatchRawBytes = frame is FileTransferChunkBatchFrameV6
                        ? FileTransferProtocol.MaxChunkBatchRawBytesV6
                        : FileTransferProtocol.MaxChunkBatchRawBytesV4;
                    if (totalChunkBytes > maxChunkBatchRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch payload exceeded {maxChunkBatchRawBytes} bytes.");
                    }

                    WriteBytes(buffer, segmentBytes);
                }
                break;
            case FileTransferCompleteFrameV4 complete:
                WriteInt64(buffer, complete.FileSizeBytes);
                WriteHash(buffer, complete.Sha256Base64);
                break;
            case FileTransferCancelFrameV4 cancel:
                WriteOptionalString(buffer, cancel.Reason);
                break;
            case FileTransferErrorFrameV4 error:
                WriteString(buffer, error.ErrorCode);
                WriteOptionalString(buffer, error.Message);
                break;
            case FileTransferPauseControlFrameV4 pauseControl:
                WriteInt32(buffer, pauseControl.Epoch);
                WriteBool(buffer, pauseControl.Paused);
                WriteOptionalString(buffer, pauseControl.Reason);
                break;
            case FileTransferTransportEpochFrameV6:
                break;
            case FileTransferTransportProbeFrameV6 probe:
                WriteOptionalString(buffer, probe.ProbeId);
                WriteOptionalString(buffer, probe.TargetTransport);
                break;
            case FileTransferRuntimeUnlockPreCommitProbeFrame probe:
                WriteRuntimeUnlockPreCommitProbeFields(buffer, probe);
                break;
            case FileTransferRuntimeUnlockPreCommitProbeAckFrame ack:
                WriteRuntimeUnlockPreCommitProbeFields(buffer, ack);
                WriteBool(buffer, ack.Accepted);
                WriteOptionalString(buffer, ack.Reason);
                break;
            case FileTransferFrontierRequestFrameV6 frontierRequest:
                if (!TryNormalizeV4MissingRanges(
                        frontierRequest.MissingRanges,
                        allowEmpty: false,
                        out var normalizedFrontierRanges,
                        maxRangeCount: FileTransferProtocol.MaxStateMissingRangesV6,
                        maxChunkCount: FileTransferProtocol.MaxChunkCountV6,
                        maxTotalChunks: FileTransferProtocol.MaxStateMissingChunksV6))
                {
                    throw new InvalidOperationException("V6 frontier request missing ranges payload was invalid.");
                }

                WriteInt32(buffer, normalizedFrontierRanges.Count);
                foreach (var range in normalizedFrontierRanges)
                {
                    WriteInt32(buffer, range.StartChunkIndex);
                    WriteInt32(buffer, range.ChunkCount);
                }
                break;
            case FileTransferRepairProofFrameV6 repairProof:
                WriteInt32(buffer, repairProof.AppliedChunkCount);
                WriteInt32(buffer, repairProof.CommittedChunkIndex);
                break;
            case FileTransferHeartbeatFrameV6 heartbeat:
                WriteInt64(buffer, heartbeat.Sequence);
                WriteInt64(buffer, heartbeat.SentUnixTimeMilliseconds);
                break;
            default:
                throw new InvalidOperationException($"Unsupported file-transfer data frame type '{frame.GetType().Name}'.");
        }

        return buffer.ToArray();
    }

    private static bool TryDeserializeBinary(ReadOnlySpan<byte> payload, bool allowLegacyV4, out FileTransferDataFrame? frame)
    {
        frame = null;
        var reader = new BinaryFrameReader(payload);
        if (!reader.TryReadUInt32(out var magic) ||
            magic != BinaryMagic ||
            !reader.TryReadByte(out var version) ||
            version != BinaryVersion ||
            !reader.TryReadByte(out var frameCode) ||
            !reader.TryReadString(out var sessionId) ||
            !reader.TryReadString(out var transferId))
        {
            return false;
        }

        var legacyV4FrameCode = IsLegacyV4FrameCode(frameCode);
        if (legacyV4FrameCode && !allowLegacyV4)
        {
            return false;
        }

        var metadata = default(TransportMetadata);
        if (!legacyV4FrameCode &&
            !TryReadTransportMetadata(ref reader, out metadata))
        {
            return false;
        }

        switch (frameCode)
        {
            case 18:
                if (!reader.TryReadString(out var v4FileName) ||
                    !reader.TryReadInt64(out var v4FileSizeBytes) ||
                    !reader.TryReadInt32(out var v4ChunkSizeBytes) ||
                    !reader.TryReadInt32(out var v4ChunkCount) ||
                    !reader.TryReadHash(out var v4Sha256Base64) ||
                    !reader.IsFullyConsumed ||
                    !IsValidV4ManifestTuple(v4FileSizeBytes, v4ChunkSizeBytes, v4ChunkCount))
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = v4FileName,
                    FileSizeBytes = v4FileSizeBytes,
                    ChunkSizeBytes = v4ChunkSizeBytes,
                    ChunkCount = v4ChunkCount,
                    Sha256Base64 = v4Sha256Base64,
                };
                break;
            case 19:
                if (!reader.TryReadInt32(out var v4Epoch) ||
                    !reader.TryReadInt32(out var v4ContiguousCommitted) ||
                    !reader.TryReadInt32(out var v4DurableHighest) ||
                    !reader.TryReadInt32(out var v4CreditUntil) ||
                    !reader.TryReadInt32(out var v4MissingRangeCount) ||
                    v4MissingRangeCount < 0 ||
                    v4MissingRangeCount > FileTransferProtocol.MaxStateMissingRangesV4)
                {
                    return false;
                }

                var v4MissingRanges = new FileTransferRangeV4[v4MissingRangeCount];
                for (var rangeIndex = 0; rangeIndex < v4MissingRangeCount; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeChunkCount))
                    {
                        return false;
                    }

                    v4MissingRanges[rangeIndex] = new FileTransferRangeV4
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        ChunkCount = rangeChunkCount,
                    };
                }

                if (!reader.TryReadInt64(out var v4BytesCommitted) ||
                    !reader.TryReadBool(out var v4ReceiverMemoryPressure) ||
                    !reader.TryReadBool(out var v4ReceiverDiskPressure) ||
                    !reader.TryReadBool(out var v4TerminalReady))
                {
                    return false;
                }

                var v4TransferPaused = false;
                string? v4TransferPauseReason = null;
                if (!reader.IsFullyConsumed &&
                    (!reader.TryReadBool(out v4TransferPaused) ||
                     !reader.TryReadOptionalString(out v4TransferPauseReason)))
                {
                    return false;
                }

                if (!reader.IsFullyConsumed ||
                    !TryNormalizeV4MissingRanges(v4MissingRanges, allowEmpty: true, out var normalizedV4MissingRanges))
                {
                    return false;
                }

                frame = new FileTransferStateFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = v4Epoch,
                    ContiguousCommittedChunkIndex = v4ContiguousCommitted,
                    DurableReceivedHighestChunkIndex = v4DurableHighest,
                    CreditUntilChunkIndexExclusive = v4CreditUntil,
                    MissingRanges = normalizedV4MissingRanges,
                    BytesCommitted = v4BytesCommitted,
                    ReceiverMemoryPressure = v4ReceiverMemoryPressure,
                    ReceiverDiskPressure = v4ReceiverDiskPressure,
                    TerminalReady = v4TerminalReady,
                    TransferPaused = v4TransferPaused,
                    TransferPauseReason = v4TransferPauseReason,
                };
                break;
            case 20:
                if (!reader.TryReadInt32(out var v4StartChunkIndex) ||
                    !reader.TryReadInt32(out var v4BatchChunkCount) ||
                    !reader.TryReadInt32(out var v4BatchSegmentCount) ||
                    v4BatchSegmentCount <= 0 ||
                    v4BatchSegmentCount > FileTransferProtocol.MaxChunkBatchSegmentsV4 ||
                    v4BatchChunkCount != v4BatchSegmentCount ||
                    !IsValidV4ChunkRange(v4StartChunkIndex, v4BatchChunkCount))
                {
                    return false;
                }

                var v4Segments = new byte[v4BatchSegmentCount][];
                var v4TotalChunkBytes = 0L;
                for (var segmentIndex = 0; segmentIndex < v4BatchSegmentCount; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes) ||
                        segmentBytes.Length == 0 ||
                        segmentBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        return false;
                    }

                    v4TotalChunkBytes += segmentBytes.Length;
                    if (v4TotalChunkBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4)
                    {
                        return false;
                    }

                    v4Segments[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = v4StartChunkIndex,
                    ChunkCount = v4BatchChunkCount,
                    DataSegments = v4Segments,
                };
                break;
            case 21:
                if (!reader.TryReadInt64(out var v4CompleteFileSizeBytes) ||
                    !reader.TryReadHash(out var v4CompleteSha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCompleteFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = v4CompleteFileSizeBytes,
                    Sha256Base64 = v4CompleteSha256Base64,
                };
                break;
            case 22:
                if (!reader.TryReadOptionalString(out var v4CancelReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCancelFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = v4CancelReason,
                };
                break;
            case 23:
                if (!reader.TryReadString(out var v4ErrorCode) ||
                    !reader.TryReadOptionalString(out var v4ErrorMessage) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferErrorFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = v4ErrorCode,
                    Message = v4ErrorMessage,
                };
                break;
            case 24:
                if (!reader.TryReadInt32(out var v4PauseControlEpoch) ||
                    !reader.TryReadBool(out var v4Paused) ||
                    !reader.TryReadOptionalString(out var v4PauseControlReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferPauseControlFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = v4PauseControlEpoch,
                    Paused = v4Paused,
                    Reason = v4PauseControlReason,
                };
                break;
            case 40:
                if (!reader.TryReadString(out var v6FileName) ||
                    !reader.TryReadInt64(out var v6FileSizeBytes) ||
                    !reader.TryReadInt32(out var v6ChunkSizeBytes) ||
                    !reader.TryReadInt32(out var v6ChunkCount) ||
                    !reader.TryReadHash(out var v6Sha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = v6FileName,
                    FileSizeBytes = v6FileSizeBytes,
                    ChunkSizeBytes = v6ChunkSizeBytes,
                    ChunkCount = v6ChunkCount,
                    Sha256Base64 = v6Sha256Base64,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 41:
                if (!reader.TryReadInt32(out var v6StateEpoch) ||
                    !reader.TryReadInt32(out var v6ContiguousCommitted) ||
                    !reader.TryReadInt32(out var v6DurableHighest) ||
                    !reader.TryReadInt32(out var v6CreditUntil) ||
                    !reader.TryReadInt32(out var v6MissingRangeCount) ||
                    v6MissingRangeCount < 0 ||
                    v6MissingRangeCount > FileTransferProtocol.MaxStateMissingRangesV6)
                {
                    return false;
                }

                var v6MissingRanges = new FileTransferRangeV4[v6MissingRangeCount];
                for (var rangeIndex = 0; rangeIndex < v6MissingRangeCount; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeChunkCount))
                    {
                        return false;
                    }

                    v6MissingRanges[rangeIndex] = new FileTransferRangeV4
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        ChunkCount = rangeChunkCount,
                    };
                }

                if (!reader.TryReadInt64(out var v6BytesCommitted) ||
                    !reader.TryReadBool(out var v6ReceiverMemoryPressure) ||
                    !reader.TryReadBool(out var v6ReceiverDiskPressure) ||
                    !reader.TryReadBool(out var v6TerminalReady) ||
                    !reader.TryReadBool(out var v6TransferPaused) ||
                    !reader.TryReadOptionalString(out var v6TransferPauseReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = v6StateEpoch,
                    ContiguousCommittedChunkIndex = v6ContiguousCommitted,
                    DurableReceivedHighestChunkIndex = v6DurableHighest,
                    CreditUntilChunkIndexExclusive = v6CreditUntil,
                    MissingRanges = v6MissingRanges,
                    BytesCommitted = v6BytesCommitted,
                    ReceiverMemoryPressure = v6ReceiverMemoryPressure,
                    ReceiverDiskPressure = v6ReceiverDiskPressure,
                    TerminalReady = v6TerminalReady,
                    TransferPaused = v6TransferPaused,
                    TransferPauseReason = v6TransferPauseReason,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 42:
                if (!reader.TryReadInt32(out var v6StartChunkIndex) ||
                    !reader.TryReadInt32(out var v6BatchChunkCount) ||
                    !reader.TryReadInt32(out var v6BatchSegmentCount) ||
                    v6BatchSegmentCount <= 0 ||
                    v6BatchSegmentCount > FileTransferProtocol.MaxChunkBatchSegmentsV6 ||
                    v6BatchChunkCount != v6BatchSegmentCount)
                {
                    return false;
                }

                var v6Segments = new byte[v6BatchSegmentCount][];
                for (var segmentIndex = 0; segmentIndex < v6BatchSegmentCount; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes))
                    {
                        return false;
                    }

                    v6Segments[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = v6StartChunkIndex,
                    ChunkCount = v6BatchChunkCount,
                    DataSegments = v6Segments,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 43:
                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferTransportEpochFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 44:
                if (!reader.TryReadOptionalString(out var probeId) ||
                    !reader.TryReadOptionalString(out var targetTransport) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferTransportProbeFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    ProbeId = probeId,
                    TargetTransport = targetTransport,
                };
                break;
            case 52:
                if (!TryReadRuntimeUnlockPreCommitProbeFields(ref reader, out var preCommitProbeFields) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRuntimeUnlockPreCommitProbeFrame
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                    TransactionGeneration = preCommitProbeFields.TransactionGeneration,
                    OfferGeneration = preCommitProbeFields.OfferGeneration,
                    TunaPathLeaseGeneration = preCommitProbeFields.TunaPathLeaseGeneration,
                    ProbeId = preCommitProbeFields.ProbeId,
                    TargetRoute = preCommitProbeFields.TargetRoute,
                    TargetProtocolVersion = preCommitProbeFields.TargetProtocolVersion,
                    TargetTransport = preCommitProbeFields.TargetTransport,
                    HandoffKind = preCommitProbeFields.HandoffKind,
                    SentUnixTimeMilliseconds = preCommitProbeFields.SentUnixTimeMilliseconds,
                };
                break;
            case 53:
                if (!TryReadRuntimeUnlockPreCommitProbeFields(ref reader, out var preCommitAckFields) ||
                    !reader.TryReadBool(out var preCommitAccepted) ||
                    !reader.TryReadOptionalString(out var preCommitAckReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRuntimeUnlockPreCommitProbeAckFrame
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                    TransactionGeneration = preCommitAckFields.TransactionGeneration,
                    OfferGeneration = preCommitAckFields.OfferGeneration,
                    TunaPathLeaseGeneration = preCommitAckFields.TunaPathLeaseGeneration,
                    ProbeId = preCommitAckFields.ProbeId,
                    TargetRoute = preCommitAckFields.TargetRoute,
                    TargetProtocolVersion = preCommitAckFields.TargetProtocolVersion,
                    TargetTransport = preCommitAckFields.TargetTransport,
                    HandoffKind = preCommitAckFields.HandoffKind,
                    SentUnixTimeMilliseconds = preCommitAckFields.SentUnixTimeMilliseconds,
                    Accepted = preCommitAccepted,
                    Reason = preCommitAckReason,
                };
                break;
            case 45:
                if (!reader.TryReadInt32(out var frontierRangeCount) ||
                    frontierRangeCount <= 0 ||
                    frontierRangeCount > FileTransferProtocol.MaxStateMissingRangesV6)
                {
                    return false;
                }

                var frontierRanges = new FileTransferRangeV4[frontierRangeCount];
                for (var rangeIndex = 0; rangeIndex < frontierRangeCount; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeChunkCount))
                    {
                        return false;
                    }

                    frontierRanges[rangeIndex] = new FileTransferRangeV4
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        ChunkCount = rangeChunkCount,
                    };
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferFrontierRequestFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    RepairRequestId = metadata.RepairRequestId,
                    MissingRanges = frontierRanges,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 46:
                if (!reader.TryReadInt32(out var v6AppliedChunkCount) ||
                    !reader.TryReadInt32(out var v6CommittedChunkIndex) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRepairProofFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    RepairRequestId = metadata.RepairRequestId,
                    AppliedChunkCount = v6AppliedChunkCount,
                    CommittedChunkIndex = v6CommittedChunkIndex,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 47:
                if (!reader.TryReadInt64(out var v6CompleteFileSizeBytes) ||
                    !reader.TryReadHash(out var v6CompleteSha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCompleteFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = v6CompleteFileSizeBytes,
                    Sha256Base64 = v6CompleteSha256Base64,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 48:
                if (!reader.TryReadOptionalString(out var v6CancelReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCancelFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = v6CancelReason,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 49:
                if (!reader.TryReadString(out var v6ErrorCode) ||
                    !reader.TryReadOptionalString(out var v6ErrorMessage) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferErrorFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = v6ErrorCode,
                    Message = v6ErrorMessage,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 50:
                if (!reader.TryReadInt32(out var v6PauseControlEpoch) ||
                    !reader.TryReadBool(out var v6Paused) ||
                    !reader.TryReadOptionalString(out var v6PauseControlReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferPauseControlFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = v6PauseControlEpoch,
                    Paused = v6Paused,
                    Reason = v6PauseControlReason,
                    TransportEpoch = metadata.TransportEpoch,
                    BatchId = metadata.BatchId,
                    RepairRequestId = metadata.RepairRequestId,
                    Priority = metadata.Priority,
                    RecoveryMode = metadata.RecoveryMode,
                };
                break;
            case 51:
                if (!reader.TryReadInt64(out var sequence) ||
                    !reader.TryReadInt64(out var sentUnixTimeMilliseconds) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferHeartbeatFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = metadata.TransportEpoch,
                    Sequence = sequence,
                    SentUnixTimeMilliseconds = sentUnixTimeMilliseconds,
                };
                break;
            default:
                return false;
        }

        return frame is not null && TryNormalizeFrame(frame, out frame);
    }

    private static bool TryNormalizeFrame(FileTransferDataFrame frame, out FileTransferDataFrame? normalized)
    {
        normalized = null;
        if (!FileTransferPayloadCodec.TryNormalizeRequiredEnvelope(
                frame.Kind,
                frame.Type,
                frame.Type,
                frame.SessionId,
                frame.TransferId,
                out var sessionId,
                out var transferId))
        {
            return false;
        }

        switch (frame)
        {
            case FileTransferManifestFrameV4 manifest when
                FileTransferProtocol.IsV4DataFrame(manifest) &&
                FileTransferPayloadCodec.TryNormalizeFileName(manifest.FileName, out var v4NormalizedFileName) &&
                IsValidV4ManifestTuple(manifest.FileSizeBytes, manifest.ChunkSizeBytes, manifest.ChunkCount) &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifest.Sha256Base64, out var v4NormalizedHash):
                normalized = manifest with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = v4NormalizedFileName,
                    Sha256Base64 = v4NormalizedHash,
                };
                return true;
            case FileTransferStateFrameV4 state when
                FileTransferProtocol.IsV4DataFrame(state) &&
                state.Epoch >= 0 &&
                state.ContiguousCommittedChunkIndex >= 0 &&
                state.ContiguousCommittedChunkIndex <= FileTransferProtocol.MaxChunkCountV4 &&
                state.DurableReceivedHighestChunkIndex >= -1 &&
                state.DurableReceivedHighestChunkIndex <= FileTransferProtocol.MaxChunkCountV4 &&
                state.CreditUntilChunkIndexExclusive >= state.ContiguousCommittedChunkIndex &&
                state.CreditUntilChunkIndexExclusive <= FileTransferProtocol.MaxChunkCountV4 &&
                state.BytesCommitted >= 0 &&
                TryNormalizeV4MissingRanges(state.MissingRanges, allowEmpty: true, out var v4MissingRanges) &&
                FileTransferPayloadCodec.TryNormalizeOptional(state.TransferPauseReason, FileTransferProtocol.MaxReasonLength, out var v4TransferPauseReason):
                normalized = state with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.StateFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    MissingRanges = v4MissingRanges,
                    TransferPauseReason = v4TransferPauseReason,
                };
                return true;
            case FileTransferChunkBatchFrameV4 batch when
                FileTransferProtocol.IsV4DataFrame(batch) &&
                batch.StartChunkIndex >= 0 &&
                batch.ChunkCount > 0 &&
                IsValidV4ChunkRange(batch.StartChunkIndex, batch.ChunkCount) &&
                batch.DataSegments.Count > 0 &&
                batch.DataSegments.Count <= FileTransferProtocol.MaxChunkBatchSegmentsV4 &&
                batch.ChunkCount == batch.DataSegments.Count:
                var normalizedV4Segments = new byte[batch.DataSegments.Count][];
                var v4TotalChunkBytes = 0L;
                for (var segmentIndex = 0; segmentIndex < batch.DataSegments.Count; segmentIndex++)
                {
                    var segment = batch.DataSegments[segmentIndex];
                    if (segment.Length == 0)
                    {
                        return false;
                    }

                    normalizedV4Segments[segmentIndex] = segment.ToArray();
                    v4TotalChunkBytes += segment.Length;
                    if (segment.Length > FileTransferProtocol.MaxChunkRawBytes ||
                        v4TotalChunkBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4)
                    {
                        return false;
                    }
                }

                normalized = batch with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkBatchFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    DataSegments = normalizedV4Segments,
                };
                return true;
            case FileTransferCompleteFrameV4 complete when
                FileTransferProtocol.IsV4DataFrame(complete) &&
                complete.FileSizeBytes >= 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(complete.Sha256Base64, out var v4CompleteHash):
                normalized = complete with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCompleteFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Sha256Base64 = v4CompleteHash,
                };
                return true;
            case FileTransferCancelFrameV4 cancel when
                FileTransferProtocol.IsV4DataFrame(cancel) &&
                FileTransferPayloadCodec.TryNormalizeOptional(cancel.Reason, FileTransferProtocol.MaxReasonLength, out var v4CancelReason):
                normalized = cancel with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCancelFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = v4CancelReason,
                };
                return true;
            case FileTransferErrorFrameV4 error when
                FileTransferProtocol.IsV4DataFrame(error) &&
                FileTransferPayloadCodec.TryNormalizeOptional(error.ErrorCode, FileTransferProtocol.MaxErrorCodeLength, out var v4ErrorCode) &&
                v4ErrorCode is not null &&
                FileTransferPayloadCodec.TryNormalizeOptional(error.Message, FileTransferProtocol.MaxErrorMessageLength, out var v4ErrorMessage):
                normalized = error with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ErrorFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = v4ErrorCode,
                    Message = v4ErrorMessage,
                };
                return true;
            case FileTransferPauseControlFrameV4 pauseControl when
                FileTransferProtocol.IsV4DataFrame(pauseControl) &&
                pauseControl.Epoch >= 0 &&
                FileTransferPayloadCodec.TryNormalizeOptional(pauseControl.Reason, FileTransferProtocol.MaxReasonLength, out var v4PauseControlReason):
                normalized = pauseControl with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.PauseControlFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = v4PauseControlReason,
                };
                return true;
            case FileTransferManifestFrameV6 manifest when
                FileTransferPayloadCodec.TryNormalizeFileName(manifest.FileName, out var fileName) &&
                IsValidV4ManifestTuple(manifest.FileSizeBytes, manifest.ChunkSizeBytes, manifest.ChunkCount) &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifest.Sha256Base64, out var hash):
                normalized = manifest with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileName,
                    Sha256Base64 = hash,
                    TransportEpoch = manifest.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(manifest.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(manifest.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(manifest.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(manifest.RecoveryMode),
                };
                return true;
            case FileTransferReceiverStateFrameV6 state when
                state.Epoch >= 0 &&
                state.ContiguousCommittedChunkIndex >= 0 &&
                state.ContiguousCommittedChunkIndex <= FileTransferProtocol.MaxChunkCountV6 &&
                state.DurableReceivedHighestChunkIndex >= -1 &&
                state.DurableReceivedHighestChunkIndex <= FileTransferProtocol.MaxChunkCountV6 &&
                state.CreditUntilChunkIndexExclusive >= state.ContiguousCommittedChunkIndex &&
                state.CreditUntilChunkIndexExclusive <= FileTransferProtocol.MaxChunkCountV6 &&
                state.BytesCommitted >= 0 &&
                TryNormalizeV4MissingRanges(
                    state.MissingRanges,
                    allowEmpty: true,
                    out var missingRanges,
                    maxRangeCount: FileTransferProtocol.MaxStateMissingRangesV6,
                    maxChunkCount: FileTransferProtocol.MaxChunkCountV6,
                    maxTotalChunks: FileTransferProtocol.MaxStateMissingChunksV6) &&
                FileTransferPayloadCodec.TryNormalizeOptional(state.TransferPauseReason, FileTransferProtocol.MaxReasonLength, out var transferPauseReason):
                normalized = state with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ReceiverStateFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    MissingRanges = missingRanges,
                    TransferPauseReason = transferPauseReason,
                    TransportEpoch = state.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(state.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(state.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(state.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(state.RecoveryMode),
                };
                return true;
            case FileTransferChunkBatchFrameV6 batch when
                batch.StartChunkIndex >= 0 &&
                batch.ChunkCount > 0 &&
                IsValidV4ChunkRange(batch.StartChunkIndex, batch.ChunkCount) &&
                batch.DataSegments.Count > 0 &&
                batch.DataSegments.Count <= FileTransferProtocol.MaxChunkBatchSegmentsV6 &&
                batch.ChunkCount == batch.DataSegments.Count:
                var normalizedV6Segments = new byte[batch.DataSegments.Count][];
                var totalV6ChunkBytes = 0L;
                for (var segmentIndex = 0; segmentIndex < batch.DataSegments.Count; segmentIndex++)
                {
                    var segment = batch.DataSegments[segmentIndex];
                    if (segment.Length == 0)
                    {
                        return false;
                    }

                    normalizedV6Segments[segmentIndex] = segment.ToArray();
                    totalV6ChunkBytes += segment.Length;
                    if (segment.Length > FileTransferProtocol.MaxChunkRawBytes ||
                        totalV6ChunkBytes > FileTransferProtocol.MaxChunkBatchRawBytesV6)
                    {
                        return false;
                    }
                }

                normalized = batch with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkBatchFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    DataSegments = normalizedV6Segments,
                    TransportEpoch = batch.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(batch.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(batch.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(batch.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(batch.RecoveryMode),
                };
                return true;
            case FileTransferTransportEpochFrameV6 epoch when epoch.TransportEpoch > 0:
                normalized = epoch with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.TransportEpochFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    RecoveryMode = NormalizeTransportMetadataToken(epoch.RecoveryMode),
                };
                return true;
            case FileTransferTransportProbeFrameV6 probe when
                probe.TransportEpoch > 0 &&
                FileTransferPayloadCodec.TryNormalizeOptional(probe.ProbeId, FileTransferProtocol.MaxReasonLength, out var probeId) &&
                FileTransferPayloadCodec.TryNormalizeOptional(probe.TargetTransport, FileTransferProtocol.MaxReasonLength, out var targetTransport):
                normalized = probe with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.TransportProbeFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProbeId = probeId,
                    TargetTransport = targetTransport,
                };
                return true;
            case FileTransferFrontierRequestFrameV6 frontierRequest when
                frontierRequest.TransportEpoch >= 0 &&
                !string.IsNullOrWhiteSpace(frontierRequest.RepairRequestId) &&
                TryNormalizeV4MissingRanges(
                    frontierRequest.MissingRanges,
                    allowEmpty: false,
                    out var frontierRanges,
                    maxRangeCount: FileTransferProtocol.MaxStateMissingRangesV6,
                    maxChunkCount: FileTransferProtocol.MaxChunkCountV6,
                    maxTotalChunks: FileTransferProtocol.MaxStateMissingChunksV6):
                normalized = frontierRequest with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.FrontierRequestFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    RepairRequestId = NormalizeTransportMetadataToken(frontierRequest.RepairRequestId),
                    MissingRanges = frontierRanges,
                    Priority = NormalizeTransportMetadataToken(frontierRequest.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(frontierRequest.RecoveryMode),
                };
                return true;
            case FileTransferRepairProofFrameV6 repairProof when
                repairProof.TransportEpoch > 0 &&
                repairProof.AppliedChunkCount >= 0 &&
                repairProof.CommittedChunkIndex >= 0 &&
                repairProof.CommittedChunkIndex <= FileTransferProtocol.MaxChunkCountV6:
                normalized = repairProof with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.RepairProofFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    RepairRequestId = NormalizeTransportMetadataToken(repairProof.RepairRequestId),
                    RecoveryMode = NormalizeTransportMetadataToken(repairProof.RecoveryMode),
                };
                return true;
            case FileTransferCompleteFrameV6 complete when
                complete.FileSizeBytes >= 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(complete.Sha256Base64, out var completeHash):
                normalized = complete with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCompleteFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Sha256Base64 = completeHash,
                    TransportEpoch = complete.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(complete.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(complete.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(complete.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(complete.RecoveryMode),
                };
                return true;
            case FileTransferCancelFrameV6 cancel when
                FileTransferPayloadCodec.TryNormalizeOptional(cancel.Reason, FileTransferProtocol.MaxReasonLength, out var cancelReason):
                normalized = cancel with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCancelFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = cancelReason,
                    TransportEpoch = cancel.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(cancel.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(cancel.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(cancel.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(cancel.RecoveryMode),
                };
                return true;
            case FileTransferErrorFrameV6 error when
                FileTransferPayloadCodec.TryNormalizeOptional(error.ErrorCode, FileTransferProtocol.MaxErrorCodeLength, out var errorCode) &&
                errorCode is not null &&
                FileTransferPayloadCodec.TryNormalizeOptional(error.Message, FileTransferProtocol.MaxErrorMessageLength, out var errorMessage):
                normalized = error with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ErrorFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = errorMessage,
                    TransportEpoch = error.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(error.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(error.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(error.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(error.RecoveryMode),
                };
                return true;
            case FileTransferPauseControlFrameV6 pauseControl when
                pauseControl.Epoch >= 0 &&
                FileTransferPayloadCodec.TryNormalizeOptional(pauseControl.Reason, FileTransferProtocol.MaxReasonLength, out var pauseControlReason):
                normalized = pauseControl with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.PauseControlFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = pauseControlReason,
                    TransportEpoch = pauseControl.TransportEpoch,
                    BatchId = NormalizeTransportMetadataToken(pauseControl.BatchId),
                    RepairRequestId = NormalizeTransportMetadataToken(pauseControl.RepairRequestId),
                    Priority = NormalizeTransportMetadataToken(pauseControl.Priority),
                    RecoveryMode = NormalizeTransportMetadataToken(pauseControl.RecoveryMode),
                };
                return true;
            case FileTransferHeartbeatFrameV6 heartbeat when
                heartbeat.TransportEpoch >= 0 &&
                heartbeat.Sequence >= 0 &&
                heartbeat.SentUnixTimeMilliseconds >= 0:
                normalized = heartbeat with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.HeartbeatFrameTypeV6,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferRuntimeUnlockPreCommitProbeFrame probe when
                TryNormalizeRuntimeUnlockPreCommitProbe(probe, sessionId, transferId, out var normalizedProbe):
                normalized = normalizedProbe;
                return true;
            case FileTransferRuntimeUnlockPreCommitProbeAckFrame ack when
                TryNormalizeRuntimeUnlockPreCommitProbeAck(ack, sessionId, transferId, out var normalizedAck):
                normalized = normalizedAck;
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeRuntimeUnlockPreCommitProbe(
        FileTransferRuntimeUnlockPreCommitProbeFrame frame,
        string sessionId,
        string transferId,
        out FileTransferRuntimeUnlockPreCommitProbeFrame normalized)
    {
        normalized = frame;
        if (!TryNormalizeRuntimeUnlockPreCommitProbeFields(frame, out var fields))
        {
            return false;
        }

        normalized = frame with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.RuntimeUnlockPreCommitProbeFrameType,
            SessionId = sessionId,
            TransferId = transferId,
            ProbeId = fields.ProbeId,
            TargetRoute = fields.TargetRoute,
            TargetTransport = fields.TargetTransport,
            HandoffKind = fields.HandoffKind,
            BatchId = NormalizeTransportMetadataToken(frame.BatchId),
            RepairRequestId = NormalizeTransportMetadataToken(frame.RepairRequestId),
            Priority = NormalizeTransportMetadataToken(frame.Priority),
            RecoveryMode = NormalizeTransportMetadataToken(frame.RecoveryMode),
        };
        return true;
    }

    private static bool TryNormalizeRuntimeUnlockPreCommitProbeAck(
        FileTransferRuntimeUnlockPreCommitProbeAckFrame frame,
        string sessionId,
        string transferId,
        out FileTransferRuntimeUnlockPreCommitProbeAckFrame normalized)
    {
        normalized = frame;
        if (!TryNormalizeRuntimeUnlockPreCommitProbeFields(frame, out var fields) ||
            !FileTransferPayloadCodec.TryNormalizeOptional(frame.Reason, FileTransferProtocol.MaxReasonLength, out var reason))
        {
            return false;
        }

        normalized = frame with
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.RuntimeUnlockPreCommitProbeAckFrameType,
            SessionId = sessionId,
            TransferId = transferId,
            ProbeId = fields.ProbeId,
            TargetRoute = fields.TargetRoute,
            TargetTransport = fields.TargetTransport,
            HandoffKind = fields.HandoffKind,
            Reason = reason,
            BatchId = NormalizeTransportMetadataToken(frame.BatchId),
            RepairRequestId = NormalizeTransportMetadataToken(frame.RepairRequestId),
            Priority = NormalizeTransportMetadataToken(frame.Priority),
            RecoveryMode = NormalizeTransportMetadataToken(frame.RecoveryMode),
        };
        return true;
    }

    private static bool TryNormalizeRuntimeUnlockPreCommitProbeFields(
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame,
        out (string ProbeId, string TargetRoute, string TargetTransport, string HandoffKind) fields)
    {
        fields = default;
        if (frame.TransactionGeneration <= 0 ||
            frame.OfferGeneration <= 0 ||
            frame.TunaPathLeaseGeneration <= 0 ||
            frame.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            frame.SentUnixTimeMilliseconds < 0 ||
            !FileTransferPayloadCodec.TryNormalizeOptional(frame.ProbeId, FileTransferProtocol.MaxReasonLength, out var probeId) ||
            string.IsNullOrWhiteSpace(probeId) ||
            !FileTransferPayloadCodec.TryNormalizeOptional(frame.TargetRoute, FileTransferProtocol.MaxReasonLength, out var targetRoute) ||
            string.IsNullOrWhiteSpace(targetRoute) ||
            !string.Equals(targetRoute, FileTransferRouteResolver.FileTunaV4Token, StringComparison.Ordinal) ||
            !FileTransferPayloadCodec.TryNormalizeOptional(frame.TargetTransport, FileTransferProtocol.MaxReasonLength, out var targetTransport) ||
            string.IsNullOrWhiteSpace(targetTransport) ||
            !string.Equals(targetTransport, "tuna", StringComparison.OrdinalIgnoreCase) ||
            !FileTransferPayloadCodec.TryNormalizeOptional(frame.HandoffKind, FileTransferProtocol.MaxReasonLength, out var handoffKind) ||
            string.IsNullOrWhiteSpace(handoffKind) ||
            !string.Equals(handoffKind, "normal_to_tuna_activation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fields = (probeId, targetRoute, targetTransport.ToLowerInvariant(), handoffKind.ToLowerInvariant());
        return true;
    }

    private static void WriteTransportMetadata(Stream stream, FileTransferDataFrame frame)
    {
        var (transportEpoch, batchId, repairRequestId, priority, recoveryMode) = frame switch
        {
            IFileTransferTransportMetadataFrame metadata => (metadata.TransportEpoch, metadata.BatchId, metadata.RepairRequestId, metadata.Priority, metadata.RecoveryMode),
            FileTransferTransportEpochFrameV6 metadata => (metadata.TransportEpoch, null, null, null, metadata.RecoveryMode),
            FileTransferTransportProbeFrameV6 metadata => (metadata.TransportEpoch, null, null, null, null),
            FileTransferFrontierRequestFrameV6 metadata => (metadata.TransportEpoch, null, metadata.RepairRequestId, metadata.Priority, metadata.RecoveryMode),
            FileTransferRepairProofFrameV6 metadata => (metadata.TransportEpoch, null, metadata.RepairRequestId, null, metadata.RecoveryMode),
            FileTransferHeartbeatFrameV6 metadata => (metadata.TransportEpoch, null, null, null, null),
            _ => throw new InvalidOperationException($"Unsupported metadata frame type '{frame.GetType().Name}'."),
        };

        WriteInt64(stream, transportEpoch);
        WriteOptionalString(stream, batchId);
        WriteOptionalString(stream, repairRequestId);
        WriteOptionalString(stream, priority);
        WriteOptionalString(stream, recoveryMode);
    }

    private static void WriteRuntimeUnlockPreCommitProbeFields(
        Stream stream,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame)
    {
        WriteInt64(stream, frame.TransactionGeneration);
        WriteInt64(stream, frame.OfferGeneration);
        WriteInt64(stream, frame.TunaPathLeaseGeneration);
        WriteOptionalString(stream, frame.ProbeId);
        WriteString(stream, frame.TargetRoute);
        WriteInt32(stream, frame.TargetProtocolVersion);
        WriteString(stream, frame.TargetTransport);
        WriteString(stream, frame.HandoffKind);
        WriteInt64(stream, frame.SentUnixTimeMilliseconds);
    }

    private static bool TryReadTransportMetadata(ref BinaryFrameReader reader, out TransportMetadata metadata)
    {
        metadata = default;
        if (!reader.TryReadInt64(out var transportEpoch) ||
            !reader.TryReadOptionalString(out var batchId) ||
            !reader.TryReadOptionalString(out var repairRequestId) ||
            !reader.TryReadOptionalString(out var priority) ||
            !reader.TryReadOptionalString(out var recoveryMode))
        {
            return false;
        }

        metadata = new TransportMetadata(
            transportEpoch,
            NormalizeTransportMetadataToken(batchId),
            NormalizeTransportMetadataToken(repairRequestId),
            NormalizeTransportMetadataToken(priority),
            NormalizeTransportMetadataToken(recoveryMode));
        return true;
    }

    private static bool TryReadRuntimeUnlockPreCommitProbeFields(
        ref BinaryFrameReader reader,
        out RuntimeUnlockPreCommitProbeFields fields)
    {
        fields = default;
        if (!reader.TryReadInt64(out var transactionGeneration) ||
            !reader.TryReadInt64(out var offerGeneration) ||
            !reader.TryReadInt64(out var tunaPathLeaseGeneration) ||
            !reader.TryReadOptionalString(out var probeId) ||
            !reader.TryReadString(out var targetRoute) ||
            !reader.TryReadInt32(out var targetProtocolVersion) ||
            !reader.TryReadString(out var targetTransport) ||
            !reader.TryReadString(out var handoffKind) ||
            !reader.TryReadInt64(out var sentUnixTimeMilliseconds))
        {
            return false;
        }

        fields = new RuntimeUnlockPreCommitProbeFields(
            transactionGeneration,
            offerGeneration,
            tunaPathLeaseGeneration,
            probeId,
            targetRoute,
            targetProtocolVersion,
            targetTransport,
            handoffKind,
            sentUnixTimeMilliseconds);
        return true;
    }

    private static string? NormalizeTransportMetadataToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Length > FileTransferProtocol.MaxReasonLength
                ? value.Trim()[..FileTransferProtocol.MaxReasonLength]
                : value.Trim();

    private static bool IsLegacyV4FrameCode(byte frameCode)
        => frameCode is >= 18 and <= 24;

    private readonly record struct TransportMetadata(
        long TransportEpoch,
        string? BatchId,
        string? RepairRequestId,
        string? Priority,
        string? RecoveryMode);

    private readonly record struct RuntimeUnlockPreCommitProbeFields(
        long TransactionGeneration,
        long OfferGeneration,
        long TunaPathLeaseGeneration,
        string? ProbeId,
        string TargetRoute,
        int TargetProtocolVersion,
        string TargetTransport,
        string HandoffKind,
        long SentUnixTimeMilliseconds);

    private static bool IsValidV4ChunkRange(int startChunkIndex, int chunkCount)
    {
        if (startChunkIndex < 0 || chunkCount <= 0)
        {
            return false;
        }

        var endExclusive = (long)startChunkIndex + chunkCount;
        return endExclusive <= FileTransferProtocol.MaxChunkCountV4;
    }

    private static bool IsValidV4ManifestTuple(long fileSizeBytes, int chunkSizeBytes, int chunkCount)
    {
        if (fileSizeBytes <= 0 ||
            chunkSizeBytes <= 0 ||
            chunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes ||
            chunkCount <= 0 ||
            chunkCount > FileTransferProtocol.MaxChunkCountV4)
        {
            return false;
        }

        try
        {
            var expectedChunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes));
            return expectedChunkCount == chunkCount;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryNormalizeV4MissingRanges(
        IReadOnlyList<FileTransferRangeV4>? ranges,
        bool allowEmpty,
        out IReadOnlyList<FileTransferRangeV4> normalized,
        int? maxRangeCount = null,
        int? maxChunkCount = null,
        int? maxTotalChunks = null)
    {
        normalized = [];
        var effectiveMaxRangeCount = maxRangeCount ?? FileTransferProtocol.MaxStateMissingRangesV4;
        var effectiveMaxChunkCount = maxChunkCount ?? FileTransferProtocol.MaxChunkCountV4;
        var effectiveMaxTotalChunks = maxTotalChunks ?? FileTransferProtocol.MaxStateMissingChunksV4;
        if (ranges is null || ranges.Count == 0)
        {
            return allowEmpty;
        }

        if (ranges.Count > effectiveMaxRangeCount)
        {
            return false;
        }

        var sorted = new List<(int Start, int EndExclusive)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.StartChunkIndex < 0 || range.ChunkCount <= 0)
            {
                return false;
            }

            var endExclusive = (long)range.StartChunkIndex + range.ChunkCount;
            if (endExclusive > effectiveMaxChunkCount)
            {
                return false;
            }

            sorted.Add((range.StartChunkIndex, (int)endExclusive));
        }

        sorted.Sort(static (left, right) => left.Start == right.Start
            ? left.EndExclusive.CompareTo(right.EndExclusive)
            : left.Start.CompareTo(right.Start));

        var merged = new List<FileTransferRangeV4>(sorted.Count);
        var currentStart = sorted[0].Start;
        var currentEnd = sorted[0].EndExclusive;
        for (var index = 1; index < sorted.Count; index++)
        {
            var candidate = sorted[index];
            if (candidate.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, candidate.EndExclusive);
                continue;
            }

            merged.Add(new FileTransferRangeV4
            {
                StartChunkIndex = currentStart,
                ChunkCount = currentEnd - currentStart,
            });
            currentStart = candidate.Start;
            currentEnd = candidate.EndExclusive;
        }

        merged.Add(new FileTransferRangeV4
        {
            StartChunkIndex = currentStart,
            ChunkCount = currentEnd - currentStart,
        });

        if (merged.Count > effectiveMaxRangeCount)
        {
            return false;
        }

        var totalChunks = 0L;
        foreach (var range in merged)
        {
            totalChunks += range.ChunkCount;
            if (totalChunks > effectiveMaxTotalChunks)
            {
                return false;
            }
        }

        normalized = merged;
        return allowEmpty || merged.Count > 0;
    }

    private static byte GetFrameCode(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferManifestFrameV4 and not FileTransferManifestFrameV6 => 18,
            FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6 => 19,
            FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6 => 20,
            FileTransferCompleteFrameV4 and not FileTransferCompleteFrameV6 => 21,
            FileTransferCancelFrameV4 and not FileTransferCancelFrameV6 => 22,
            FileTransferErrorFrameV4 and not FileTransferErrorFrameV6 => 23,
            FileTransferPauseControlFrameV4 and not FileTransferPauseControlFrameV6 => 24,
            FileTransferManifestFrameV6 => 40,
            FileTransferReceiverStateFrameV6 => 41,
            FileTransferChunkBatchFrameV6 => 42,
            FileTransferTransportEpochFrameV6 => 43,
            FileTransferTransportProbeFrameV6 => 44,
            FileTransferFrontierRequestFrameV6 => 45,
            FileTransferRepairProofFrameV6 => 46,
            FileTransferCompleteFrameV6 => 47,
            FileTransferCancelFrameV6 => 48,
            FileTransferErrorFrameV6 => 49,
            FileTransferPauseControlFrameV6 => 50,
            FileTransferHeartbeatFrameV6 => 51,
            FileTransferRuntimeUnlockPreCommitProbeFrame => 52,
            FileTransferRuntimeUnlockPreCommitProbeAckFrame => 53,
            _ => throw new InvalidOperationException($"Unsupported file-transfer data frame type '{frame.GetType().Name}'."),
        };

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("String payload exceeded binary frame budget.");
        }

        Span<byte> lengthBuffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBuffer, (ushort)bytes.Length);
        stream.Write(lengthBuffer);
        stream.Write(bytes);
    }

    private static void WriteOptionalString(Stream stream, string? value)
    {
        stream.WriteByte(string.IsNullOrWhiteSpace(value) ? (byte)0 : (byte)1);
        if (!string.IsNullOrWhiteSpace(value))
        {
            WriteString(stream, value.Trim());
        }
    }

    private static void WriteBool(Stream stream, bool value)
        => stream.WriteByte(value ? (byte)1 : (byte)0);

    private static void WriteBytes(Stream stream, byte[] bytes)
    {
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteHash(Stream stream, string sha256Base64)
    {
        var bytes = Convert.FromBase64String(sha256Base64);
        if (bytes.Length != FileTransferProtocol.Sha256LengthBytes)
        {
            throw new InvalidOperationException("SHA-256 payload was not 32 bytes.");
        }

        stream.Write(bytes);
    }

    private ref struct BinaryFrameReader
    {
        private ReadOnlySpan<byte> remaining;

        public BinaryFrameReader(ReadOnlySpan<byte> payload)
        {
            remaining = payload;
        }

        public bool IsFullyConsumed => remaining.IsEmpty;

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (remaining.Length < 1)
            {
                return false;
            }

            value = remaining[0];
            remaining = remaining[1..];
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (remaining.Length < sizeof(uint))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(remaining[..sizeof(uint)]);
            remaining = remaining[sizeof(uint)..];
            return true;
        }

        public bool TryReadBool(out bool value)
        {
            value = false;
            if (!TryReadByte(out var raw) || raw > 1)
            {
                return false;
            }

            value = raw == 1;
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            value = 0;
            if (remaining.Length < sizeof(int))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(remaining[..sizeof(int)]);
            remaining = remaining[sizeof(int)..];
            return true;
        }

        public bool TryReadInt64(out long value)
        {
            value = 0;
            if (remaining.Length < sizeof(long))
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(remaining[..sizeof(long)]);
            remaining = remaining[sizeof(long)..];
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (remaining.Length < sizeof(ushort))
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(remaining[..sizeof(ushort)]);
            remaining = remaining[sizeof(ushort)..];
            if (remaining.Length < length)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(remaining[..length]);
            remaining = remaining[length..];
            return true;
        }

        public bool TryReadOptionalString(out string? value)
        {
            value = null;
            if (!TryReadByte(out var hasValue))
            {
                return false;
            }

            if (hasValue == 0)
            {
                return true;
            }

            if (hasValue != 1 || !TryReadString(out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        public bool TryReadBytes(out byte[] value)
        {
            value = [];
            if (!TryReadInt32(out var length) ||
                length <= 0 ||
                length > FileTransferProtocol.MaxChunkRawBytes ||
                remaining.Length < length)
            {
                return false;
            }

            value = remaining[..length].ToArray();
            remaining = remaining[length..];
            return true;
        }

        public bool TryReadHash(out string value)
        {
            value = string.Empty;
            if (remaining.Length < FileTransferProtocol.Sha256LengthBytes)
            {
                return false;
            }

            value = Convert.ToBase64String(remaining[..FileTransferProtocol.Sha256LengthBytes]);
            remaining = remaining[FileTransferProtocol.Sha256LengthBytes..];
            return true;
        }
    }
}
