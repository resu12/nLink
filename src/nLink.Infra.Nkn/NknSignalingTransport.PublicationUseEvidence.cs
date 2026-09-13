using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // Evidence only; no route/admission/effect decision reads this dictionary.
    private readonly Dictionary<FileTransferRouteHintKey, FileTransferPublicationCapture> fileTransferPublicationCaptures = new();

    private sealed record FileTransferPublicationCapture(
        FileTransferRoutePublicationAuthority Authority,
        FileTransferRouteHint RouteHint,
        FileTransferRoutePublicationEvidence Evidence);

    private bool TryGetFileTransferDataPathRouteAuthorityWithEvidence(
        string? sessionId, string? transferId, FileTransferDirection direction,
        out FileTransferRouteHint routeHint, out FileTransferRoutePublicationEvidence? publication)
    {
        publication = null;
        lock (gate)
        {
            // Reuse the existing authority lookup while holding its gate through
            // the diagnostic copy. Evidence never changes that lookup's result.
            if (!TryGetFileTransferDataPathRouteAuthority(sessionId, transferId, direction, out routeHint))
            {
                return false;
            }
            var key = CreateFileTransferRouteHintKey(sessionId!.Trim(), transferId!.Trim(), direction);
            if (fileTransferPublicationCaptures.TryGetValue(key, out var capture) &&
                fileTransferRoutePublicationAuthorities.TryGetValue(key, out var authority) &&
                ReferenceEquals(authority, capture.Authority) && capture.RouteHint == routeHint &&
                capture.Evidence.SessionId == FileTransferDataPathScopeEvidenceToken(sessionId.Trim()) &&
                capture.Evidence.TransferId == FileTransferDataPathScopeEvidenceToken(transferId.Trim()))
            {
                publication = capture.Evidence;
            }

            return true;
        }
    }

    private static string FormatFileTransferPublicationUseEvidenceSuffix(FileTransferDataPathEnvelopeEvidence evidence)
    {
        if (evidence.PublicationUse is not { } use || evidence.SecurityEvidence is not { } security ||
            use.Publication.SessionId != evidence.SessionId || use.Publication.TransferId != evidence.TransferId ||
            use.Publication.SessionId != FileTransferDataPathScopeEvidenceToken(security.SessionId) ||
            use.Publication.TransferId != FileTransferDataPathScopeEvidenceToken(security.TransferId))
        {
            return string.Empty;
        }

        return use.FormatLogSuffix();
    }
}
