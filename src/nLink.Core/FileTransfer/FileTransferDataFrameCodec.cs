using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public static class FileTransferDataFrameCodec
{
    private const uint BinaryMagic = 0x3246544E; // "NFT2"
    private const byte BinaryVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static byte[] Serialize(FileTransferDataFrameV2 frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = SerializeBinary(frame);
        var maxSerializedPayloadBytes = frame switch
        {
            FileTransferChunkBatchFrameV4 => FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4,
            FileTransferChunkBatchFrameV3 => FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV3,
            _ => FileTransferProtocol.MaxSerializedChunkPayloadBytes,
        };
        if ((frame is FileTransferChunkDataFrameV2 || frame is FileTransferChunkBatchFrameV2) &&
            payload.Length > maxSerializedPayloadBytes)
        {
            LocalOperationalLog.Warn(
                "FileTransferPayload",
                $"event=serialize_chunk_data_frame_budget_exceeded; session_id={frame.SessionId}; transfer_id={frame.TransferId}; payload_bytes={payload.Length}; budget_bytes={maxSerializedPayloadBytes}");
            throw new InvalidOperationException(
                $"Serialized file-transfer chunk data frame exceeded safe budget of {maxSerializedPayloadBytes} bytes.");
        }

        return payload;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out FileTransferDataFrameV2? frame)
    {
        frame = null;
        if (payload.Length == 0)
        {
            return false;
        }

        if (LooksLikeJson(payload))
        {
            return TryDeserializeJson(payload, out frame);
        }

        return TryDeserializeBinary(payload, out frame);
    }

    private static byte[] SerializeBinary(FileTransferDataFrameV2 frame)
    {
        using var buffer = new MemoryStream();
        buffer.Write(BitConverter.GetBytes(BinaryMagic));
        buffer.WriteByte(BinaryVersion);
        buffer.WriteByte(GetFrameCode(frame));
        WriteString(buffer, frame.SessionId);
        WriteString(buffer, frame.TransferId);

        switch (frame)
        {
            case FileTransferManifestFrameV2 manifest:
                WriteString(buffer, manifest.FileName);
                WriteInt64(buffer, manifest.FileSizeBytes);
                WriteInt32(buffer, manifest.ChunkSizeBytes);
                WriteInt32(buffer, manifest.ChunkCount);
                WriteHash(buffer, manifest.Sha256Base64);
                break;
            case FileTransferRequestChunksFrameV2 request:
                WriteInt32(buffer, request.StartChunkIndex);
                WriteInt32(buffer, request.RequestedChunkCount);
                WriteInt32(buffer, request.PipelineDepth);
                break;
            case FileTransferChunkDataFrameV3 chunkV3:
                var chunkBytesV3 = chunkV3.Data;
                if (chunkBytesV3.Length == 0 || chunkBytesV3.Length > FileTransferProtocol.MaxChunkRawBytes)
                {
                    throw new InvalidOperationException($"Chunk payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                }

                WriteInt32(buffer, chunkV3.ChunkIndex);
                WriteInt32(buffer, chunkV3.ChunkCount);
                WriteBytes(buffer, chunkBytesV3);
                break;
            case FileTransferChunkDataFrameV2 chunk:
                var chunkBytes = chunk.Data;
                if (chunkBytes.Length == 0 || chunkBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
                {
                    throw new InvalidOperationException($"Chunk payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                }

                WriteInt32(buffer, chunk.ChunkIndex);
                WriteInt32(buffer, chunk.ChunkCount);
                WriteBytes(buffer, chunkBytes);
                break;
            case FileTransferChunkBatchFrameV4 chunkBatchV4:
                if (chunkBatchV4.DataSegments.Count == 0)
                {
                    throw new InvalidOperationException("Chunk batch payload may not be empty.");
                }

                if (chunkBatchV4.ChunkCount != chunkBatchV4.DataSegments.Count)
                {
                    throw new InvalidOperationException("V4 chunk batch count must match the number of data segments.");
                }

                var totalChunkBytesV4 = 0;
                WriteInt32(buffer, chunkBatchV4.StartChunkIndex);
                WriteInt32(buffer, chunkBatchV4.ChunkCount);
                WriteInt32(buffer, chunkBatchV4.DataSegments.Count);
                foreach (var segmentBytes in chunkBatchV4.DataSegments)
                {
                    if (segmentBytes.Length == 0)
                    {
                        throw new InvalidOperationException("Chunk batch segment payload may not be empty.");
                    }

                    totalChunkBytesV4 += segmentBytes.Length;
                    if (segmentBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch segment payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                    }

                    if (totalChunkBytesV4 > FileTransferProtocol.MaxChunkBatchRawBytesV4)
                    {
                        throw new InvalidOperationException($"V4 chunk batch payload exceeded {FileTransferProtocol.MaxChunkBatchRawBytesV4} bytes.");
                    }

                    WriteBytes(buffer, segmentBytes);
                }
                break;
            case FileTransferChunkBatchFrameV3 chunkBatchV3:
                if (chunkBatchV3.DataSegments.Count == 0)
                {
                    throw new InvalidOperationException("Chunk batch payload may not be empty.");
                }

                var totalChunkBytesV3 = 0;
                WriteInt32(buffer, chunkBatchV3.StartChunkIndex);
                WriteInt32(buffer, chunkBatchV3.ChunkCount);
                WriteInt32(buffer, chunkBatchV3.DataSegments.Count);
                foreach (var segmentBytes in chunkBatchV3.DataSegments)
                {
                    if (segmentBytes.Length == 0)
                    {
                        throw new InvalidOperationException("Chunk batch segment payload may not be empty.");
                    }

                    totalChunkBytesV3 += segmentBytes.Length;
                    if (segmentBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch segment payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                    }

                    if (totalChunkBytesV3 > FileTransferProtocol.MaxChunkBatchRawBytesV3)
                    {
                        throw new InvalidOperationException($"V3 chunk batch payload exceeded {FileTransferProtocol.MaxChunkBatchRawBytesV3} bytes.");
                    }

                    WriteBytes(buffer, segmentBytes);
                }
                break;
            case FileTransferChunkBatchFrameV2 chunkBatch:
                if (chunkBatch.DataSegments.Count == 0)
                {
                    throw new InvalidOperationException("Chunk batch payload may not be empty.");
                }

                var totalChunkBytes = 0;
                WriteInt32(buffer, chunkBatch.StartChunkIndex);
                WriteInt32(buffer, chunkBatch.ChunkCount);
                WriteInt32(buffer, chunkBatch.DataSegments.Count);
                foreach (var segmentBytes in chunkBatch.DataSegments)
                {
                    if (segmentBytes.Length == 0)
                    {
                        throw new InvalidOperationException("Chunk batch segment payload may not be empty.");
                    }

                    totalChunkBytes += segmentBytes.Length;
                    if (totalChunkBytes > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
                    }

                    WriteBytes(buffer, segmentBytes);
                }
                break;
            case FileTransferAckProgressFrameV2 ack:
                WriteInt32(buffer, ack.NextExpectedChunkIndex);
                WriteInt64(buffer, ack.BytesCommitted);
                break;
            case FileTransferCancelFrameV2 cancel:
                WriteOptionalString(buffer, cancel.Reason);
                break;
            case FileTransferCompleteFrameV2 complete:
                WriteInt64(buffer, complete.FileSizeBytes);
                WriteHash(buffer, complete.Sha256Base64);
                break;
            case FileTransferManifestFrameV3 manifestV3:
                WriteString(buffer, manifestV3.FileName);
                WriteInt64(buffer, manifestV3.FileSizeBytes);
                WriteInt32(buffer, manifestV3.ChunkSizeBytes);
                WriteInt32(buffer, manifestV3.ChunkCount);
                WriteHash(buffer, manifestV3.Sha256Base64);
                break;
            case FileTransferGrantWindowFrameV3 grantV3:
                WriteInt32(buffer, grantV3.NextExpectedChunkIndex);
                WriteInt32(buffer, grantV3.GrantedUntilChunkIndexExclusive);
                WriteInt64(buffer, grantV3.BytesCommitted);
                break;
            case FileTransferAckProgressFrameV3 ackV3:
                WriteInt32(buffer, ackV3.NextExpectedChunkIndex);
                WriteInt64(buffer, ackV3.BytesCommitted);
                break;
            case FileTransferRepairRequestFrameV3 repairV3:
                WriteInt32(buffer, repairV3.StartChunkIndex);
                WriteInt32(buffer, repairV3.RequestedChunkCount);
                break;
            case FileTransferRepairRequestSetFrameV3 repairSetV3:
                if (!TryNormalizeRepairRanges(repairSetV3.Ranges, out var normalizedRanges))
                {
                    throw new InvalidOperationException("Repair request set payload was invalid.");
                }

                WriteInt32(buffer, normalizedRanges.Count);
                foreach (var range in normalizedRanges)
                {
                    WriteInt32(buffer, range.StartChunkIndex);
                    WriteInt32(buffer, range.RequestedChunkCount);
                }
                break;
            case FileTransferManifestFrameV4 manifestV4:
                WriteString(buffer, manifestV4.FileName);
                WriteInt64(buffer, manifestV4.FileSizeBytes);
                WriteInt32(buffer, manifestV4.ChunkSizeBytes);
                WriteInt32(buffer, manifestV4.ChunkCount);
                WriteHash(buffer, manifestV4.Sha256Base64);
                break;
            case FileTransferStateFrameV4 stateV4:
                if (!TryNormalizeV4MissingRanges(stateV4.MissingRanges, allowEmpty: true, out var normalizedMissingRanges))
                {
                    throw new InvalidOperationException("V4 state missing ranges payload was invalid.");
                }

                WriteInt32(buffer, stateV4.Epoch);
                WriteInt32(buffer, stateV4.ContiguousCommittedChunkIndex);
                WriteInt32(buffer, stateV4.DurableReceivedHighestChunkIndex);
                WriteInt32(buffer, stateV4.CreditUntilChunkIndexExclusive);
                WriteInt32(buffer, normalizedMissingRanges.Count);
                foreach (var range in normalizedMissingRanges)
                {
                    WriteInt32(buffer, range.StartChunkIndex);
                    WriteInt32(buffer, range.ChunkCount);
                }

                WriteInt64(buffer, stateV4.BytesCommitted);
                WriteBool(buffer, stateV4.ReceiverMemoryPressure);
                WriteBool(buffer, stateV4.ReceiverDiskPressure);
                WriteBool(buffer, stateV4.TerminalReady);
                break;
            case FileTransferCompleteFrameV4 completeV4:
                WriteInt64(buffer, completeV4.FileSizeBytes);
                WriteHash(buffer, completeV4.Sha256Base64);
                break;
            case FileTransferCancelFrameV4 cancelV4:
                WriteOptionalString(buffer, cancelV4.Reason);
                break;
            case FileTransferErrorFrameV4 errorV4:
                WriteString(buffer, errorV4.ErrorCode);
                WriteOptionalString(buffer, errorV4.Message);
                break;
            default:
                throw new InvalidOperationException($"Unsupported file-transfer data frame type '{frame.GetType().Name}'.");
        }

        return buffer.ToArray();
    }

    private static bool TryDeserializeBinary(ReadOnlySpan<byte> payload, out FileTransferDataFrameV2? frame)
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

        switch (frameCode)
        {
            case 1:
                if (!reader.TryReadString(out var fileName) ||
                    !reader.TryReadInt64(out var fileSizeBytes) ||
                    !reader.TryReadInt32(out var chunkSizeBytes) ||
                    !reader.TryReadInt32(out var chunkCount) ||
                    !reader.TryReadHash(out var sha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    ChunkSizeBytes = chunkSizeBytes,
                    ChunkCount = chunkCount,
                    Sha256Base64 = sha256Base64,
                };
                break;
            case 2:
                if (!reader.TryReadInt32(out var startChunkIndex) ||
                    !reader.TryReadInt32(out var requestedChunkCount) ||
                    !reader.TryReadInt32(out var pipelineDepth) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRequestChunksFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    RequestedChunkCount = requestedChunkCount,
                    PipelineDepth = pipelineDepth,
                };
                break;
            case 3:
                if (!reader.TryReadInt32(out var chunkIndex) ||
                    !reader.TryReadInt32(out var chunkCountValue) ||
                    !reader.TryReadBytes(out var chunkBytes) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkDataFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = chunkCountValue,
                    Data = chunkBytes,
                };
                break;
            case 7:
                if (!reader.TryReadInt32(out var startChunkIndexValue) ||
                    !reader.TryReadInt32(out var batchChunkCountValue) ||
                    !reader.TryReadInt32(out var batchSegmentCount) ||
                    batchSegmentCount <= 0)
                {
                    return false;
                }

                var chunkSegments = new byte[batchSegmentCount][];
                for (var segmentIndex = 0; segmentIndex < batchSegmentCount; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes))
                    {
                        return false;
                    }

                    chunkSegments[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndexValue,
                    ChunkCount = batchChunkCountValue,
                    DataSegments = chunkSegments,
                };
                break;
            case 4:
                if (!reader.TryReadInt32(out var nextExpectedChunkIndex) ||
                    !reader.TryReadInt64(out var bytesCommitted) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferAckProgressFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    NextExpectedChunkIndex = nextExpectedChunkIndex,
                    BytesCommitted = bytesCommitted,
                };
                break;
            case 5:
                if (!reader.TryReadOptionalString(out var reason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCancelFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = reason,
                };
                break;
            case 6:
                if (!reader.TryReadInt64(out var completeFileSizeBytes) ||
                    !reader.TryReadHash(out var completeSha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCompleteFrameV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = completeFileSizeBytes,
                    Sha256Base64 = completeSha256Base64,
                };
                break;
            case 11:
                if (!reader.TryReadString(out var fileNameV3) ||
                    !reader.TryReadInt64(out var fileSizeBytesV3) ||
                    !reader.TryReadInt32(out var chunkSizeBytesV3) ||
                    !reader.TryReadInt32(out var chunkCountV3) ||
                    !reader.TryReadHash(out var sha256Base64V3) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileNameV3,
                    FileSizeBytes = fileSizeBytesV3,
                    ChunkSizeBytes = chunkSizeBytesV3,
                    ChunkCount = chunkCountV3,
                    Sha256Base64 = sha256Base64V3,
                };
                break;
            case 12:
                if (!reader.TryReadInt32(out var nextExpectedChunkIndexV3) ||
                    !reader.TryReadInt32(out var grantedUntilExclusiveV3) ||
                    !reader.TryReadInt64(out var bytesCommittedV3) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferGrantWindowFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    NextExpectedChunkIndex = nextExpectedChunkIndexV3,
                    GrantedUntilChunkIndexExclusive = grantedUntilExclusiveV3,
                    BytesCommitted = bytesCommittedV3,
                };
                break;
            case 13:
                if (!reader.TryReadInt32(out var ackNextExpectedV3) ||
                    !reader.TryReadInt64(out var ackBytesCommittedV3) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferAckProgressFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    NextExpectedChunkIndex = ackNextExpectedV3,
                    BytesCommitted = ackBytesCommittedV3,
                };
                break;
            case 14:
                if (!reader.TryReadInt32(out var chunkIndexV3) ||
                    !reader.TryReadInt32(out var chunkCountValueV3) ||
                    !reader.TryReadBytes(out var chunkBytesV3Data) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkDataFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ChunkIndex = chunkIndexV3,
                    ChunkCount = chunkCountValueV3,
                    Data = chunkBytesV3Data,
                };
                break;
            case 15:
                if (!reader.TryReadInt32(out var startChunkIndexValueV3) ||
                    !reader.TryReadInt32(out var batchChunkCountValueV3) ||
                    !reader.TryReadInt32(out var batchSegmentCountV3) ||
                    batchSegmentCountV3 <= 0)
                {
                    return false;
                }

                var chunkSegmentsV3 = new byte[batchSegmentCountV3][];
                for (var segmentIndex = 0; segmentIndex < batchSegmentCountV3; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes))
                    {
                        return false;
                    }

                    chunkSegmentsV3[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndexValueV3,
                    ChunkCount = batchChunkCountValueV3,
                    DataSegments = chunkSegmentsV3,
                };
                break;
            case 16:
                if (!reader.TryReadInt32(out var repairStartChunkIndex) ||
                    !reader.TryReadInt32(out var repairRequestedChunkCount) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRepairRequestFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = repairStartChunkIndex,
                    RequestedChunkCount = repairRequestedChunkCount,
                };
                break;
            case 17:
                if (!reader.TryReadInt32(out var repairRangeCount) ||
                    repairRangeCount <= 0 ||
                    repairRangeCount > FileTransferProtocol.MaxRepairSetRangesV3)
                {
                    return false;
                }

                var repairRanges = new FileTransferRepairRangeV3[repairRangeCount];
                for (var rangeIndex = 0; rangeIndex < repairRangeCount; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeRequestedChunkCount))
                    {
                        return false;
                    }

                    repairRanges[rangeIndex] = new FileTransferRepairRangeV3
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        RequestedChunkCount = rangeRequestedChunkCount,
                    };
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferRepairRequestSetFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Ranges = repairRanges,
                };
                break;
            case 18:
                if (!reader.TryReadString(out var fileNameV4) ||
                    !reader.TryReadInt64(out var fileSizeBytesV4) ||
                    !reader.TryReadInt32(out var chunkSizeBytesV4) ||
                    !reader.TryReadInt32(out var chunkCountV4) ||
                    !reader.TryReadHash(out var sha256Base64V4) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileNameV4,
                    FileSizeBytes = fileSizeBytesV4,
                    ChunkSizeBytes = chunkSizeBytesV4,
                    ChunkCount = chunkCountV4,
                    Sha256Base64 = sha256Base64V4,
                };
                break;
            case 19:
                if (!reader.TryReadInt32(out var stateEpochV4) ||
                    !reader.TryReadInt32(out var stateContiguousCommittedV4) ||
                    !reader.TryReadInt32(out var stateDurableHighestV4) ||
                    !reader.TryReadInt32(out var stateCreditUntilV4) ||
                    !reader.TryReadInt32(out var stateMissingRangeCountV4) ||
                    stateMissingRangeCountV4 < 0 ||
                    stateMissingRangeCountV4 > FileTransferProtocol.MaxStateMissingRangesV4)
                {
                    return false;
                }

                var missingRangesV4 = new FileTransferRangeV4[stateMissingRangeCountV4];
                for (var rangeIndex = 0; rangeIndex < stateMissingRangeCountV4; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeChunkCount))
                    {
                        return false;
                    }

                    missingRangesV4[rangeIndex] = new FileTransferRangeV4
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        ChunkCount = rangeChunkCount,
                    };
                }

                if (!reader.TryReadInt64(out var stateBytesCommittedV4) ||
                    !reader.TryReadBool(out var receiverMemoryPressureV4) ||
                    !reader.TryReadBool(out var receiverDiskPressureV4) ||
                    !reader.TryReadBool(out var terminalReadyV4) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferStateFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = stateEpochV4,
                    ContiguousCommittedChunkIndex = stateContiguousCommittedV4,
                    DurableReceivedHighestChunkIndex = stateDurableHighestV4,
                    CreditUntilChunkIndexExclusive = stateCreditUntilV4,
                    MissingRanges = missingRangesV4,
                    BytesCommitted = stateBytesCommittedV4,
                    ReceiverMemoryPressure = receiverMemoryPressureV4,
                    ReceiverDiskPressure = receiverDiskPressureV4,
                    TerminalReady = terminalReadyV4,
                };
                break;
            case 20:
                if (!reader.TryReadInt32(out var startChunkIndexValueV4) ||
                    !reader.TryReadInt32(out var batchChunkCountValueV4) ||
                    !reader.TryReadInt32(out var batchSegmentCountV4) ||
                    batchSegmentCountV4 <= 0 ||
                    batchChunkCountValueV4 != batchSegmentCountV4)
                {
                    return false;
                }

                var chunkSegmentsV4 = new byte[batchSegmentCountV4][];
                for (var segmentIndex = 0; segmentIndex < batchSegmentCountV4; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes))
                    {
                        return false;
                    }

                    chunkSegmentsV4[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndexValueV4,
                    ChunkCount = batchChunkCountValueV4,
                    DataSegments = chunkSegmentsV4,
                };
                break;
            case 21:
                if (!reader.TryReadInt64(out var completeFileSizeBytesV4) ||
                    !reader.TryReadHash(out var completeSha256Base64V4) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCompleteFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = completeFileSizeBytesV4,
                    Sha256Base64 = completeSha256Base64V4,
                };
                break;
            case 22:
                if (!reader.TryReadOptionalString(out var cancelReasonV4) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCancelFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = cancelReasonV4,
                };
                break;
            case 23:
                if (!reader.TryReadString(out var errorCodeV4) ||
                    !reader.TryReadOptionalString(out var errorMessageV4) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferErrorFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCodeV4,
                    Message = errorMessageV4,
                };
                break;
            default:
                return false;
        }

        return frame is not null && TryNormalizeFrame(frame, out frame);
    }

    private static bool TryDeserializeJson(ReadOnlySpan<byte> utf8Json, out FileTransferDataFrameV2? frame)
    {
        frame = null;

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            if (!document.RootElement.TryGetProperty(nameof(FileTransferDataFrameV2.Type), out var typeElement))
            {
                return false;
            }

            var type = typeElement.GetString();
            frame = type switch
            {
                FileTransferProtocol.ManifestFrameTypeV2 => JsonSerializer.Deserialize<FileTransferManifestFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.RequestChunksFrameTypeV2 => JsonSerializer.Deserialize<FileTransferRequestChunksFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.ChunkDataFrameTypeV2 => JsonSerializer.Deserialize<FileTransferChunkDataFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.ChunkBatchFrameTypeV2 => JsonSerializer.Deserialize<FileTransferChunkBatchFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.AckProgressFrameTypeV2 => JsonSerializer.Deserialize<FileTransferAckProgressFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.SessionCancelFrameTypeV2 => JsonSerializer.Deserialize<FileTransferCancelFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.SessionCompleteFrameTypeV2 => JsonSerializer.Deserialize<FileTransferCompleteFrameV2>(utf8Json, JsonOptions),
                FileTransferProtocol.ManifestFrameTypeV3 => JsonSerializer.Deserialize<FileTransferManifestFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.GrantWindowFrameTypeV3 => JsonSerializer.Deserialize<FileTransferGrantWindowFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.AckProgressFrameTypeV3 => JsonSerializer.Deserialize<FileTransferAckProgressFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.ChunkDataFrameTypeV3 => JsonSerializer.Deserialize<FileTransferChunkDataFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.ChunkBatchFrameTypeV3 => JsonSerializer.Deserialize<FileTransferChunkBatchFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.RepairRequestFrameTypeV3 => JsonSerializer.Deserialize<FileTransferRepairRequestFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.RepairRequestSetFrameTypeV3 => JsonSerializer.Deserialize<FileTransferRepairRequestSetFrameV3>(utf8Json, JsonOptions),
                FileTransferProtocol.ManifestFrameTypeV4 => JsonSerializer.Deserialize<FileTransferManifestFrameV4>(utf8Json, JsonOptions),
                FileTransferProtocol.StateFrameTypeV4 => JsonSerializer.Deserialize<FileTransferStateFrameV4>(utf8Json, JsonOptions),
                FileTransferProtocol.ChunkBatchFrameTypeV4 => JsonSerializer.Deserialize<FileTransferChunkBatchFrameV4>(utf8Json, JsonOptions),
                FileTransferProtocol.SessionCompleteFrameTypeV4 => JsonSerializer.Deserialize<FileTransferCompleteFrameV4>(utf8Json, JsonOptions),
                FileTransferProtocol.SessionCancelFrameTypeV4 => JsonSerializer.Deserialize<FileTransferCancelFrameV4>(utf8Json, JsonOptions),
                FileTransferProtocol.ErrorFrameTypeV4 => JsonSerializer.Deserialize<FileTransferErrorFrameV4>(utf8Json, JsonOptions),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return false;
        }

        return frame is not null && TryNormalizeFrame(frame, out frame);
    }

    private static bool TryNormalizeFrame(FileTransferDataFrameV2 frame, out FileTransferDataFrameV2? normalized)
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
            case FileTransferManifestFrameV2 manifest when
                FileTransferPayloadCodec.TryNormalizeFileName(manifest.FileName, out var fileName) &&
                manifest.FileSizeBytes > 0 &&
                manifest.ChunkSizeBytes > 0 &&
                manifest.ChunkSizeBytes <= FileTransferProtocol.MaxChunkRawBytes &&
                manifest.ChunkCount > 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifest.Sha256Base64, out var hash):
                normalized = manifest with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileName,
                    Sha256Base64 = hash,
                };
                return true;
            case FileTransferRequestChunksFrameV2 request when
                request.StartChunkIndex >= 0 &&
                request.RequestedChunkCount > 0 &&
                request.PipelineDepth > 0:
                normalized = request with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.RequestChunksFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferChunkDataFrameV3 chunkV3 when
                chunkV3.ChunkIndex >= 0 &&
                chunkV3.ChunkCount > 0 &&
                chunkV3.ChunkIndex < chunkV3.ChunkCount &&
                chunkV3.Data.Length > 0 &&
                chunkV3.Data.Length <= FileTransferProtocol.MaxChunkRawBytes:
                normalized = chunkV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkDataFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Data = chunkV3.Data.ToArray(),
                };
                return true;
            case FileTransferChunkDataFrameV2 chunk when
                chunk.ChunkIndex >= 0 &&
                chunk.ChunkCount > 0 &&
                chunk.ChunkIndex < chunk.ChunkCount &&
                chunk.Data.Length > 0 &&
                chunk.Data.Length <= FileTransferProtocol.MaxChunkRawBytes:
                normalized = chunk with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkDataFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Data = chunk.Data.ToArray(),
                };
                return true;
            case FileTransferChunkBatchFrameV4 batchV4 when
                batchV4.StartChunkIndex >= 0 &&
                batchV4.ChunkCount > 0 &&
                batchV4.DataSegments.Count > 0 &&
                batchV4.ChunkCount == batchV4.DataSegments.Count:
                var normalizedSegmentsV4 = new byte[batchV4.DataSegments.Count][];
                var totalChunkBytesV4 = 0;
                for (var segmentIndex = 0; segmentIndex < batchV4.DataSegments.Count; segmentIndex++)
                {
                    var segment = batchV4.DataSegments[segmentIndex];
                    if (segment.Length == 0)
                    {
                        return false;
                    }

                    normalizedSegmentsV4[segmentIndex] = segment.ToArray();
                    totalChunkBytesV4 += segment.Length;
                    if (segment.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        return false;
                    }

                    if (totalChunkBytesV4 > FileTransferProtocol.MaxChunkBatchRawBytesV4)
                    {
                        return false;
                    }
                }

                normalized = batchV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkBatchFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    DataSegments = normalizedSegmentsV4,
                };
                return true;
            case FileTransferChunkBatchFrameV3 batchV3 when
                batchV3.StartChunkIndex >= 0 &&
                batchV3.ChunkCount > 0 &&
                batchV3.DataSegments.Count > 0 &&
                batchV3.StartChunkIndex + batchV3.DataSegments.Count - 1 < batchV3.ChunkCount:
                var normalizedSegmentsV3 = new byte[batchV3.DataSegments.Count][];
                var totalChunkBytesV3 = 0;
                for (var segmentIndex = 0; segmentIndex < batchV3.DataSegments.Count; segmentIndex++)
                {
                    var segment = batchV3.DataSegments[segmentIndex];
                    if (segment.Length == 0)
                    {
                        return false;
                    }

                    normalizedSegmentsV3[segmentIndex] = segment.ToArray();
                    totalChunkBytesV3 += segment.Length;
                    if (segment.Length > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        return false;
                    }

                    if (totalChunkBytesV3 > FileTransferProtocol.MaxChunkBatchRawBytesV3)
                    {
                        return false;
                    }
                }

                normalized = batchV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkBatchFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                    DataSegments = normalizedSegmentsV3,
                };
                return true;
            case FileTransferChunkBatchFrameV2 batch when
                batch.StartChunkIndex >= 0 &&
                batch.ChunkCount > 0 &&
                batch.DataSegments.Count > 0 &&
                batch.StartChunkIndex + batch.DataSegments.Count - 1 < batch.ChunkCount:
                var normalizedSegments = new byte[batch.DataSegments.Count][];
                var totalChunkBytes = 0;
                for (var segmentIndex = 0; segmentIndex < batch.DataSegments.Count; segmentIndex++)
                {
                    var segment = batch.DataSegments[segmentIndex];
                    if (segment.Length == 0)
                    {
                        return false;
                    }

                    normalizedSegments[segmentIndex] = segment.ToArray();
                    totalChunkBytes += segment.Length;
                    if (totalChunkBytes > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        return false;
                    }
                }

                normalized = batch with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ChunkBatchFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                    DataSegments = normalizedSegments,
                };
                return true;
            case FileTransferAckProgressFrameV2 ack when
                ack.NextExpectedChunkIndex >= 0 &&
                ack.BytesCommitted >= 0:
                normalized = ack with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.AckProgressFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferCancelFrameV2 cancel when
                FileTransferPayloadCodec.TryNormalizeOptional(cancel.Reason, FileTransferProtocol.MaxReasonLength, out var reason):
                normalized = cancel with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCancelFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = reason,
                };
                return true;
            case FileTransferCompleteFrameV2 complete when
                complete.FileSizeBytes >= 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(complete.Sha256Base64, out var completeHash):
                normalized = complete with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCompleteFrameTypeV2,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Sha256Base64 = completeHash,
                };
                return true;
            case FileTransferManifestFrameV3 manifestV3 when
                FileTransferPayloadCodec.TryNormalizeFileName(manifestV3.FileName, out var fileNameV3) &&
                manifestV3.FileSizeBytes > 0 &&
                manifestV3.ChunkSizeBytes > 0 &&
                manifestV3.ChunkSizeBytes <= FileTransferProtocol.MaxChunkRawBytes &&
                manifestV3.ChunkCount > 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifestV3.Sha256Base64, out var hashV3):
                normalized = manifestV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileNameV3,
                    Sha256Base64 = hashV3,
                };
                return true;
            case FileTransferGrantWindowFrameV3 grantV3 when
                grantV3.NextExpectedChunkIndex >= 0 &&
                grantV3.GrantedUntilChunkIndexExclusive >= grantV3.NextExpectedChunkIndex &&
                grantV3.BytesCommitted >= 0:
                normalized = grantV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.GrantWindowFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferAckProgressFrameV3 ackV3 when
                ackV3.NextExpectedChunkIndex >= 0 &&
                ackV3.BytesCommitted >= 0:
                normalized = ackV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.AckProgressFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferRepairRequestFrameV3 repairV3 when
                repairV3.StartChunkIndex >= 0 &&
                repairV3.RequestedChunkCount > 0:
                normalized = repairV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.RepairRequestFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                };
                return true;
            case FileTransferRepairRequestSetFrameV3 repairSetV3 when
                TryNormalizeRepairRanges(repairSetV3.Ranges, out var ranges):
                normalized = repairSetV3 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.RepairRequestSetFrameTypeV3,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Ranges = ranges,
                };
                return true;
            case FileTransferManifestFrameV4 manifestV4 when
                FileTransferPayloadCodec.TryNormalizeFileName(manifestV4.FileName, out var fileNameV4) &&
                manifestV4.FileSizeBytes > 0 &&
                manifestV4.ChunkSizeBytes > 0 &&
                manifestV4.ChunkSizeBytes <= FileTransferProtocol.MaxChunkRawBytes &&
                manifestV4.ChunkCount > 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifestV4.Sha256Base64, out var hashV4):
                normalized = manifestV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileNameV4,
                    Sha256Base64 = hashV4,
                };
                return true;
            case FileTransferStateFrameV4 stateV4 when
                stateV4.Epoch >= 0 &&
                stateV4.ContiguousCommittedChunkIndex >= 0 &&
                stateV4.DurableReceivedHighestChunkIndex >= -1 &&
                stateV4.CreditUntilChunkIndexExclusive >= stateV4.ContiguousCommittedChunkIndex &&
                stateV4.BytesCommitted >= 0 &&
                TryNormalizeV4MissingRanges(stateV4.MissingRanges, allowEmpty: true, out var missingRangesV4):
                normalized = stateV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.StateFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    MissingRanges = missingRangesV4,
                };
                return true;
            case FileTransferCompleteFrameV4 completeV4 when
                completeV4.FileSizeBytes >= 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(completeV4.Sha256Base64, out var completeHashV4):
                normalized = completeV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCompleteFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Sha256Base64 = completeHashV4,
                };
                return true;
            case FileTransferCancelFrameV4 cancelV4 when
                FileTransferPayloadCodec.TryNormalizeOptional(cancelV4.Reason, FileTransferProtocol.MaxReasonLength, out var cancelReasonV4):
                normalized = cancelV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCancelFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = cancelReasonV4,
                };
                return true;
            case FileTransferErrorFrameV4 errorV4 when
                FileTransferPayloadCodec.TryNormalizeOptional(errorV4.ErrorCode, FileTransferProtocol.MaxErrorCodeLength, out var errorCodeV4) &&
                errorCodeV4 is not null &&
                FileTransferPayloadCodec.TryNormalizeOptional(errorV4.Message, FileTransferProtocol.MaxErrorMessageLength, out var errorMessageV4):
                normalized = errorV4 with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ErrorFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCodeV4,
                    Message = errorMessageV4,
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeRepairRanges(
        IReadOnlyList<FileTransferRepairRangeV3>? ranges,
        out IReadOnlyList<FileTransferRepairRangeV3> normalized)
    {
        normalized = [];
        if (ranges is null || ranges.Count == 0)
        {
            return false;
        }

        var sorted = new List<(int Start, int EndExclusive)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.StartChunkIndex < 0 || range.RequestedChunkCount <= 0)
            {
                return false;
            }

            var endExclusive = (long)range.StartChunkIndex + range.RequestedChunkCount;
            if (endExclusive > int.MaxValue)
            {
                return false;
            }

            sorted.Add((range.StartChunkIndex, (int)endExclusive));
        }

        sorted.Sort(static (left, right) => left.Start == right.Start
            ? left.EndExclusive.CompareTo(right.EndExclusive)
            : left.Start.CompareTo(right.Start));

        var merged = new List<FileTransferRepairRangeV3>(Math.Min(sorted.Count, FileTransferProtocol.MaxRepairSetRangesV3));
        var remainingChunks = FileTransferProtocol.MaxRepairSetChunksV3;
        var currentStart = sorted[0].Start;
        var currentEnd = sorted[0].EndExclusive;

        void AddCurrent()
        {
            if (remainingChunks <= 0 || merged.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
            {
                return;
            }

            var requestedChunkCount = Math.Min(currentEnd - currentStart, remainingChunks);
            if (requestedChunkCount <= 0)
            {
                return;
            }

            merged.Add(new FileTransferRepairRangeV3
            {
                StartChunkIndex = currentStart,
                RequestedChunkCount = requestedChunkCount,
            });
            remainingChunks -= requestedChunkCount;
        }

        for (var index = 1; index < sorted.Count; index++)
        {
            var candidate = sorted[index];
            if (candidate.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, candidate.EndExclusive);
                continue;
            }

            AddCurrent();
            if (remainingChunks <= 0 || merged.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
            {
                break;
            }

            currentStart = candidate.Start;
            currentEnd = candidate.EndExclusive;
        }

        AddCurrent();
        if (merged.Count == 0)
        {
            return false;
        }

        normalized = merged;
        return true;
    }

    private static bool TryNormalizeV4MissingRanges(
        IReadOnlyList<FileTransferRangeV4>? ranges,
        bool allowEmpty,
        out IReadOnlyList<FileTransferRangeV4> normalized)
    {
        normalized = [];
        if (ranges is null || ranges.Count == 0)
        {
            return allowEmpty;
        }

        if (ranges.Count > FileTransferProtocol.MaxStateMissingRangesV4)
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
            if (endExclusive > int.MaxValue)
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

        if (merged.Count > FileTransferProtocol.MaxStateMissingRangesV4)
        {
            return false;
        }

        var totalChunks = 0L;
        foreach (var range in merged)
        {
            totalChunks += range.ChunkCount;
            if (totalChunks > FileTransferProtocol.MaxStateMissingChunksV4)
            {
                return false;
            }
        }

        normalized = merged;
        return allowEmpty || merged.Count > 0;
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload)
        {
            if (!char.IsWhiteSpace((char)value))
            {
                return value == (byte)'{';
            }
        }

        return false;
    }

    private static byte GetFrameCode(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferManifestFrameV2 => 1,
            FileTransferRequestChunksFrameV2 => 2,
            FileTransferChunkDataFrameV3 => 14,
            FileTransferChunkDataFrameV2 => 3,
            FileTransferChunkBatchFrameV4 => 20,
            FileTransferChunkBatchFrameV3 => 15,
            FileTransferChunkBatchFrameV2 => 7,
            FileTransferAckProgressFrameV2 => 4,
            FileTransferCancelFrameV2 => 5,
            FileTransferCompleteFrameV2 => 6,
            FileTransferManifestFrameV3 => 11,
            FileTransferGrantWindowFrameV3 => 12,
            FileTransferAckProgressFrameV3 => 13,
            FileTransferRepairRequestFrameV3 => 16,
            FileTransferRepairRequestSetFrameV3 => 17,
            FileTransferManifestFrameV4 => 18,
            FileTransferStateFrameV4 => 19,
            FileTransferCompleteFrameV4 => 21,
            FileTransferCancelFrameV4 => 22,
            FileTransferErrorFrameV4 => 23,
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
