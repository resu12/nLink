using System.Diagnostics.CodeAnalysis;

namespace NLink.Core.FileTransfer;

internal readonly record struct FileTransferV4ChunkSegment(int ChunkIndex, int Offset, int Length);

// Diagnostic metadata only. No payload, span or authority is retained.
internal sealed record FileTransferV4ChunkProjection(
    string SessionId, string TransferId, int StartChunkIndex,
    IReadOnlyList<FileTransferV4ChunkSegment> Segments);

public static partial class FileTransferDataFrameCodec
{
    internal static bool TryProjectLegacyV4ChunkBatch(
        ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out FileTransferV4ChunkProjection? projection)
    {
        projection = null;
        if (payload.IsEmpty || payload.Length > FileTransferProtocol.MaxSerializedChunkBatchPayloadBytesV4)
        {
            return false;
        }

        var reader = new BinaryFrameReader(payload);
        if (!reader.TryReadUInt32(out var magic) || magic != BinaryMagic ||
            !reader.TryReadByte(out var version) || version != BinaryVersion ||
            !reader.TryReadByte(out var code) || code != V4ChunkBatchFrameCode ||
            !reader.TryReadString(out var sessionId) ||
            !reader.TryReadString(out var transferId) ||
            !FileTransferPayloadCodec.TryNormalizeRequiredEnvelope(
                FileTransferProtocol.Kind, FileTransferProtocol.ChunkBatchFrameTypeV4,
                FileTransferProtocol.ChunkBatchFrameTypeV4, sessionId, transferId,
                out var canonicalSessionId, out var canonicalTransferId) ||
            !reader.TryReadInt32(out var start) ||
            !reader.TryReadInt32(out var chunks) ||
            !reader.TryReadInt32(out var count) ||
            count <= 0 || count > FileTransferProtocol.MaxChunkBatchSegmentsV4 ||
            chunks != count || !IsValidV4ChunkRange(start, chunks))
        {
            return false;
        }

        var segments = new FileTransferV4ChunkSegment[count];
        var totalBytes = 0;
        for (var slot = 0; slot < count; slot++)
        {
            if (!reader.TryReadBytesView(out var bytes))
            {
                return false;
            }

            totalBytes += bytes.Length;
            if (totalBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4)
            {
                return false;
            }
            segments[slot] = new(start + slot, payload.Length - reader.RemainingLength - bytes.Length, bytes.Length);
        }
        if (!reader.IsFullyConsumed)
        {
            return false;
        }

        projection = new(canonicalSessionId, canonicalTransferId, start, Array.AsReadOnly(segments));
        return true;
    }
}
