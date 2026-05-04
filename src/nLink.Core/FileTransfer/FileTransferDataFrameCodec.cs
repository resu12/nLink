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

        return TryDeserializeBinary(payload, out frame);
    }

    private static byte[] SerializeBinary(FileTransferDataFrame frame)
    {
        using var buffer = new MemoryStream();
        buffer.Write(BitConverter.GetBytes(BinaryMagic));
        buffer.WriteByte(BinaryVersion);
        buffer.WriteByte(GetFrameCode(frame));
        WriteString(buffer, frame.SessionId);
        WriteString(buffer, frame.TransferId);

        switch (frame)
        {
            case FileTransferManifestFrameV4 manifest:
                if (!IsValidV4ManifestTuple(manifest.FileSizeBytes, manifest.ChunkSizeBytes, manifest.ChunkCount))
                {
                    throw new InvalidOperationException("V4 manifest chunk tuple was invalid.");
                }

                WriteString(buffer, manifest.FileName);
                WriteInt64(buffer, manifest.FileSizeBytes);
                WriteInt32(buffer, manifest.ChunkSizeBytes);
                WriteInt32(buffer, manifest.ChunkCount);
                WriteHash(buffer, manifest.Sha256Base64);
                break;
            case FileTransferStateFrameV4 state:
                if (!TryNormalizeV4MissingRanges(state.MissingRanges, allowEmpty: true, out var normalizedMissingRanges))
                {
                    throw new InvalidOperationException("V4 state missing ranges payload was invalid.");
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

                if (batch.DataSegments.Count > FileTransferProtocol.MaxChunkBatchSegmentsV4)
                {
                    throw new InvalidOperationException($"V4 chunk batch segment count exceeded {FileTransferProtocol.MaxChunkBatchSegmentsV4}.");
                }

                if (batch.ChunkCount != batch.DataSegments.Count)
                {
                    throw new InvalidOperationException("V4 chunk batch count must match the number of data segments.");
                }

                var totalChunkBytes = 0;
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

                    if (totalChunkBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4)
                    {
                        throw new InvalidOperationException($"V4 chunk batch payload exceeded {FileTransferProtocol.MaxChunkBatchRawBytesV4} bytes.");
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
            default:
                throw new InvalidOperationException($"Unsupported file-transfer data frame type '{frame.GetType().Name}'.");
        }

        return buffer.ToArray();
    }

    private static bool TryDeserializeBinary(ReadOnlySpan<byte> payload, out FileTransferDataFrame? frame)
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
            case 18:
                if (!reader.TryReadString(out var fileName) ||
                    !reader.TryReadInt64(out var fileSizeBytes) ||
                    !reader.TryReadInt32(out var chunkSizeBytes) ||
                    !reader.TryReadInt32(out var chunkCount) ||
                    !reader.TryReadHash(out var sha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferManifestFrameV4
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
            case 19:
                if (!reader.TryReadInt32(out var epoch) ||
                    !reader.TryReadInt32(out var contiguousCommitted) ||
                    !reader.TryReadInt32(out var durableHighest) ||
                    !reader.TryReadInt32(out var creditUntil) ||
                    !reader.TryReadInt32(out var missingRangeCount) ||
                    missingRangeCount < 0 ||
                    missingRangeCount > FileTransferProtocol.MaxStateMissingRangesV4)
                {
                    return false;
                }

                var missingRanges = new FileTransferRangeV4[missingRangeCount];
                for (var rangeIndex = 0; rangeIndex < missingRangeCount; rangeIndex++)
                {
                    if (!reader.TryReadInt32(out var rangeStartChunkIndex) ||
                        !reader.TryReadInt32(out var rangeChunkCount))
                    {
                        return false;
                    }

                    missingRanges[rangeIndex] = new FileTransferRangeV4
                    {
                        StartChunkIndex = rangeStartChunkIndex,
                        ChunkCount = rangeChunkCount,
                    };
                }

                if (!reader.TryReadInt64(out var bytesCommitted) ||
                    !reader.TryReadBool(out var receiverMemoryPressure) ||
                    !reader.TryReadBool(out var receiverDiskPressure) ||
                    !reader.TryReadBool(out var terminalReady))
                {
                    return false;
                }

                var transferPaused = false;
                string? transferPauseReason = null;
                if (!reader.IsFullyConsumed &&
                    (!reader.TryReadBool(out transferPaused) ||
                     !reader.TryReadOptionalString(out transferPauseReason)))
                {
                    return false;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferStateFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = epoch,
                    ContiguousCommittedChunkIndex = contiguousCommitted,
                    DurableReceivedHighestChunkIndex = durableHighest,
                    CreditUntilChunkIndexExclusive = creditUntil,
                    MissingRanges = missingRanges,
                    BytesCommitted = bytesCommitted,
                    ReceiverMemoryPressure = receiverMemoryPressure,
                    ReceiverDiskPressure = receiverDiskPressure,
                    TerminalReady = terminalReady,
                    TransferPaused = transferPaused,
                    TransferPauseReason = transferPauseReason,
                };
                break;
            case 20:
                if (!reader.TryReadInt32(out var startChunkIndex) ||
                    !reader.TryReadInt32(out var batchChunkCount) ||
                    !reader.TryReadInt32(out var batchSegmentCount) ||
                    batchSegmentCount <= 0 ||
                    batchSegmentCount > FileTransferProtocol.MaxChunkBatchSegmentsV4 ||
                    batchChunkCount != batchSegmentCount)
                {
                    return false;
                }

                var segments = new byte[batchSegmentCount][];
                for (var segmentIndex = 0; segmentIndex < batchSegmentCount; segmentIndex++)
                {
                    if (!reader.TryReadBytes(out var segmentBytes))
                    {
                        return false;
                    }

                    segments[segmentIndex] = segmentBytes;
                }

                if (!reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    ChunkCount = batchChunkCount,
                    DataSegments = segments,
                };
                break;
            case 21:
                if (!reader.TryReadInt64(out var completeFileSizeBytes) ||
                    !reader.TryReadHash(out var completeSha256Base64) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCompleteFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = completeFileSizeBytes,
                    Sha256Base64 = completeSha256Base64,
                };
                break;
            case 22:
                if (!reader.TryReadOptionalString(out var reason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferCancelFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = reason,
                };
                break;
            case 23:
                if (!reader.TryReadString(out var errorCode) ||
                    !reader.TryReadOptionalString(out var errorMessage) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferErrorFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = errorMessage,
                };
                break;
            case 24:
                if (!reader.TryReadInt32(out var pauseControlEpoch) ||
                    !reader.TryReadBool(out var paused) ||
                    !reader.TryReadOptionalString(out var pauseControlReason) ||
                    !reader.IsFullyConsumed)
                {
                    return false;
                }

                frame = new FileTransferPauseControlFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = pauseControlEpoch,
                    Paused = paused,
                    Reason = pauseControlReason,
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
                FileTransferPayloadCodec.TryNormalizeFileName(manifest.FileName, out var fileName) &&
                IsValidV4ManifestTuple(manifest.FileSizeBytes, manifest.ChunkSizeBytes, manifest.ChunkCount) &&
                FileTransferPayloadCodec.TryNormalizeSha256(manifest.Sha256Base64, out var hash):
                normalized = manifest with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ManifestFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = fileName,
                    Sha256Base64 = hash,
                };
                return true;
            case FileTransferStateFrameV4 state when
                state.Epoch >= 0 &&
                state.ContiguousCommittedChunkIndex >= 0 &&
                state.DurableReceivedHighestChunkIndex >= -1 &&
                state.CreditUntilChunkIndexExclusive >= state.ContiguousCommittedChunkIndex &&
                state.BytesCommitted >= 0 &&
                TryNormalizeV4MissingRanges(state.MissingRanges, allowEmpty: true, out var missingRanges) &&
                FileTransferPayloadCodec.TryNormalizeOptional(state.TransferPauseReason, FileTransferProtocol.MaxReasonLength, out var transferPauseReason):
                normalized = state with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.StateFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    MissingRanges = missingRanges,
                    TransferPauseReason = transferPauseReason,
                };
                return true;
            case FileTransferChunkBatchFrameV4 batch when
                batch.StartChunkIndex >= 0 &&
                batch.ChunkCount > 0 &&
                batch.DataSegments.Count > 0 &&
                batch.DataSegments.Count <= FileTransferProtocol.MaxChunkBatchSegmentsV4 &&
                batch.ChunkCount == batch.DataSegments.Count:
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
                    if (segment.Length > FileTransferProtocol.MaxChunkRawBytes ||
                        totalChunkBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4)
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
                    DataSegments = normalizedSegments,
                };
                return true;
            case FileTransferCompleteFrameV4 complete when
                complete.FileSizeBytes >= 0 &&
                FileTransferPayloadCodec.TryNormalizeSha256(complete.Sha256Base64, out var completeHash):
                normalized = complete with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCompleteFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Sha256Base64 = completeHash,
                };
                return true;
            case FileTransferCancelFrameV4 cancel when
                FileTransferPayloadCodec.TryNormalizeOptional(cancel.Reason, FileTransferProtocol.MaxReasonLength, out var cancelReason):
                normalized = cancel with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.SessionCancelFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = cancelReason,
                };
                return true;
            case FileTransferErrorFrameV4 error when
                FileTransferPayloadCodec.TryNormalizeOptional(error.ErrorCode, FileTransferProtocol.MaxErrorCodeLength, out var errorCode) &&
                errorCode is not null &&
                FileTransferPayloadCodec.TryNormalizeOptional(error.Message, FileTransferProtocol.MaxErrorMessageLength, out var errorMessage):
                normalized = error with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.ErrorFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = errorMessage,
                };
                return true;
            case FileTransferPauseControlFrameV4 pauseControl when
                pauseControl.Epoch >= 0 &&
                FileTransferPayloadCodec.TryNormalizeOptional(pauseControl.Reason, FileTransferProtocol.MaxReasonLength, out var pauseControlReason):
                normalized = pauseControl with
                {
                    Kind = FileTransferProtocol.Kind,
                    Type = FileTransferProtocol.PauseControlFrameTypeV4,
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = pauseControlReason,
                };
                return true;
            default:
                return false;
        }
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

    private static byte GetFrameCode(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferManifestFrameV4 => 18,
            FileTransferStateFrameV4 => 19,
            FileTransferChunkBatchFrameV4 => 20,
            FileTransferCompleteFrameV4 => 21,
            FileTransferCancelFrameV4 => 22,
            FileTransferErrorFrameV4 => 23,
            FileTransferPauseControlFrameV4 => 24,
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
