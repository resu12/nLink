using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // One optional sender scope. Never consulted by runtime or route admission.
    private FileTransferContentEvidenceSlot? fileTransferContentEvidenceSlot;

    private sealed record FileTransferContentEvidenceSlot(
        TransportFileTransferDataSession Owner, FileTransferSecurityContext Context,
        FileTransferContentEvidenceScope Scope);

    private readonly record struct FileTransferContentTerminalCapture(TransportFileTransferDataSession? Owner);

    private FileTransferContentEvidence? CaptureFileTransferContentEvidence(
        byte[] serialized, TransportFileTransferDataSession? originatingSession,
        FileTransferDataPathSendAuthority? authority, FileTransferPublicationUseEvidence? publicationUse,
        FileTransferSecurityEvidence? securityEvidence)
    {
        if (!FileTransferDiagnosticLogPolicy.TraceEnabled || originatingSession is null ||
            authority is not { Direction: FileTransferDirection.Outbound, ProtocolVersion: 4, TargetProtocolVersion: 4 } ||
            publicationUse?.Kind != FileTransferPublicationUseKind.V4PhysicalSubmission ||
            securityEvidence?.Context is not { } context ||
            !ReferenceEquals(FileTransferPublicationUseEvidence.GetSend(authority), publicationUse) ||
            !((authority.RouteToken == FileTransferRouteResolver.RegularNknV4FastToken &&
               authority.TargetRouteToken == FileTransferRouteResolver.RegularNknV4FastToken &&
               authority.TargetTransport == FileTransferTransportKind.RegularNkn) || IsFileTunaV4TargetTuna(authority)) ||
            authority.SessionId != originatingSession.SessionId || authority.TransferId != originatingSession.TransferId ||
            authority.SessionId != securityEvidence.SessionId || authority.TransferId != securityEvidence.TransferId ||
            publicationUse.Publication.SessionId != FileTransferDataPathScopeEvidenceToken(authority.SessionId) ||
            publicationUse.Publication.TransferId != FileTransferDataPathScopeEvidenceToken(authority.TransferId) ||
            !FileTransferDataFrameCodec.TryProjectLegacyV4ChunkBatch(serialized, out var projection) ||
            projection.SessionId != authority.SessionId || projection.TransferId != authority.TransferId)
        {
            return null;
        }

        lock (controlSecureStateGate)
        {
            if (!ReferenceEquals(context, fileTransferSecurityContext) || context.Retired ||
                SecurityContextStatus(context, authority.SessionId) != "captured" || originatingSession.IsDisposed ||
                originatingSession.ContentEvidenceRetired || fileTransferTerminalTombstones.ContainsKey(authority.TransferId))
            {
                return null;
            }
            if (fileTransferContentEvidenceSlot is { } occupied &&
                (!ReferenceEquals(occupied.Owner, originatingSession) || !ReferenceEquals(occupied.Context, context)))
            {
                return null;
            }
            try
            {
                fileTransferContentEvidenceSlot ??= new(originatingSession, context,
                    new FileTransferContentEvidenceScope(authority.SessionId, authority.TransferId));
                return fileTransferContentEvidenceSlot.Scope.TryCapture(serialized, out var evidence) ? evidence : null;
            }
            catch (CryptographicException)
            {
                return null; // Optional diagnostics must not change a send outcome.
            }
        }
    }

    private void RetireFileTransferContentEvidenceLocked(TransportFileTransferDataSession? owner = null)
    {
        if (owner is not null) { owner.ContentEvidenceRetired = true; }
        if (fileTransferContentEvidenceSlot is not { } slot ||
            (owner is not null && !ReferenceEquals(slot.Owner, owner))) { return; }
        slot.Scope.Dispose();
        fileTransferContentEvidenceSlot = null;
    }

    private FileTransferContentTerminalCapture CaptureTerminalContentOwner(string transferId)
    {
        lock (controlSecureStateGate)
        {
            return new(fileTransferContentEvidenceSlot?.Owner.TransferId == transferId
                ? fileTransferContentEvidenceSlot.Owner : null);
        }
    }

    private static string FormatFileTransferContentEvidenceSuffix(FileTransferContentEvidence? evidence,
        string transferId, string frameType, int rawPayloadBytes, int batchChunkCount, FileTransferDataFrame? frame,
        FileTransferDataPathEnvelopeEvidence envelope)
        => evidence is not null && frame is FileTransferChunkBatchFrameV4 batch &&
           frameType == FileTransferProtocol.ChunkBatchFrameTypeV4 && batch.Type == frameType &&
           evidence.SessionId == frame.SessionId && evidence.TransferId == transferId && frame.TransferId == transferId &&
           envelope.SessionId == FileTransferDataPathScopeEvidenceToken(evidence.SessionId) &&
           envelope.TransferId == FileTransferDataPathScopeEvidenceToken(evidence.TransferId) &&
           batch.StartChunkIndex == evidence.Segments[0].ChunkIndex && batch.ChunkCount == evidence.Segments.Count &&
           batch.DataSegments.Count == evidence.Segments.Count && batchChunkCount == evidence.Segments.Count &&
           rawPayloadBytes == evidence.Segments.Sum(segment => segment.Bytes)
            ? evidence.FormatLogSuffix() : string.Empty;
}
