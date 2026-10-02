using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

// Optional sender diagnostic primitive; never grants transport or route authority.
internal sealed class FileTransferContentEvidenceScope(string sessionId, string transferId) : IDisposable
{
    private readonly object gate = new();
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly FileTransferContentScopeMarker marker = new();

    internal bool TryCapture(ReadOnlySpan<byte> serialized,
        [NotNullWhen(true)] out FileTransferContentEvidence? evidence)
    {
        evidence = null;
        if (marker.Retired ||
            !FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(serialized, out var projection) ||
            projection.SessionId != sessionId || projection.TransferId != transferId)
        {
            return false;
        }

        lock (gate)
        {
            if (marker.Retired) { return false; }
            try
            {
                using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
                var sessionBytes = Encoding.UTF8.GetBytes(sessionId);
                var transferBytes = Encoding.UTF8.GetBytes(transferId);
                var commitments = new FileTransferChunkContentCommitment[projection.Segments.Count];
                for (var slot = 0; slot < commitments.Length; slot++)
                {
                    var segment = projection.Segments[slot];
                    hash.AppendData("nLink/filetransfer/content/v4-chunk/v1\0"u8);
                    AppendNumber(hash, sessionBytes.Length);
                    hash.AppendData(sessionBytes);
                    AppendNumber(hash, transferBytes.Length);
                    hash.AppendData(transferBytes);
                    AppendNumber(hash, segment.ChunkIndex);
                    AppendNumber(hash, segment.Length);
                    hash.AppendData(serialized.Slice(segment.Offset, segment.Length));
                    commitments[slot] = new(segment.ChunkIndex, segment.Length,
                        "ftcc1-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                }
                evidence = new(marker, projection.SessionId, projection.TransferId, Array.AsReadOnly(commitments));
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static void AppendNumber(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        hash.AppendData(bytes);
    }

    public void Dispose()
    {
        lock (gate)
        {
            marker.Retired = true;
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}

internal sealed class FileTransferContentScopeMarker
{
    internal string ScopeId { get; } = "ftcs1-" + Guid.NewGuid().ToString("N");
    internal volatile bool Retired;
}

internal readonly record struct FileTransferChunkContentCommitment(int ChunkIndex, int Bytes, string Commitment);

internal sealed record FileTransferContentEvidence(
    FileTransferContentScopeMarker Scope, string SessionId, string TransferId,
    IReadOnlyList<FileTransferChunkContentCommitment> Segments)
{
    internal const int MaximumSuffixBytes = 768;

    internal string FormatLogSuffix()
    {
        var retired = Scope.Retired;
        var suffix = new StringBuilder("; content_evidence_profile=sender_chunk_hmac_v1; content_scope_id=")
            .Append(Scope.ScopeId).Append("; content_evidence_state=").Append(retired ? "retired" : "captured")
            .Append("; content_segment_count=").Append((retired ? 0 : Segments.Count).ToString(CultureInfo.InvariantCulture));
        if (!retired)
        {
            for (var slot = 0; slot < Segments.Count; slot++)
            {
                var segment = Segments[slot];
                suffix.Append(FormattableString.Invariant(
                    $"; content_segment_{slot}_index={segment.ChunkIndex}; content_segment_{slot}_bytes={segment.Bytes}; content_segment_{slot}_commitment={segment.Commitment}"));
            }
        }
        return suffix.Length <= MaximumSuffixBytes ? suffix.ToString() : string.Empty;
    }
}
