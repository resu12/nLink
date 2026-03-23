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
        if ((frame is FileTransferChunkDataFrameV2 || frame is FileTransferChunkBatchFrameV2) &&
            payload.Length > FileTransferProtocol.MaxSerializedChunkPayloadBytes)
        {
            LocalOperationalLog.Warn(
                "FileTransferPayload",
                $"event=serialize_chunk_data_frame_budget_exceeded; session_id={frame.SessionId}; transfer_id={frame.TransferId}; payload_bytes={payload.Length}");
            throw new InvalidOperationException(
                $"Serialized file-transfer chunk data frame exceeded safe budget of {FileTransferProtocol.MaxSerializedChunkPayloadBytes} bytes.");
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
                    if (totalChunkBytesV3 > FileTransferProtocol.MaxChunkRawBytes)
                    {
                        throw new InvalidOperationException($"Chunk batch payload exceeded {FileTransferProtocol.MaxChunkRawBytes} bytes.");
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
                    if (totalChunkBytesV3 > FileTransferProtocol.MaxChunkRawBytes)
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
            default:
                return false;
        }
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
            FileTransferChunkBatchFrameV3 => 15,
            FileTransferChunkBatchFrameV2 => 7,
            FileTransferAckProgressFrameV2 => 4,
            FileTransferCancelFrameV2 => 5,
            FileTransferCompleteFrameV2 => 6,
            FileTransferManifestFrameV3 => 11,
            FileTransferGrantWindowFrameV3 => 12,
            FileTransferAckProgressFrameV3 => 13,
            FileTransferRepairRequestFrameV3 => 16,
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
