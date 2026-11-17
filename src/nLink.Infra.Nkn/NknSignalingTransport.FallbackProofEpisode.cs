using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // accelerationGate protects the projection and its pause-bound identity.
    private FileTransferFallbackProofEpisode? CaptureFallbackProofEpisodeUnsafe(FileTransferRouteTransitionAuthority authority)
    {
        if (authority.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
            CaptureFileFallbackProjectionUnsafe(authority) is not { } projection)
        {
            return null;
        }
        lock (fileTransferFallbackProofGate)
        {
            return IsPendingFallbackProofScopeCurrentUnsafe(authority) && fileTransferFallbackProofGeneration > 0
                ? new(authority, fileTransferFallbackProofGeneration, projection.Epoch) : null;
        }
    }

    // Caller holds fileTransferFallbackProofGate. A session-only pending episode may
    // be bound by the exact coordinator pause; any existing narrower scope must agree.
    private bool IsPendingFallbackProofScopeCurrentUnsafe(FileTransferRouteTransitionAuthority authority)
        => fileTransferFallbackProofPending &&
           (fileTransferFallbackProofLanes & NknAccelerationLaneKind.File) != 0 &&
           fileTransferFallbackProofSessionId == authority.SessionId &&
           (string.IsNullOrWhiteSpace(fileTransferFallbackProofTransferId) || fileTransferFallbackProofTransferId == authority.TransferId) &&
           (fileTransferFallbackProofDirection is null || fileTransferFallbackProofDirection == authority.Direction) &&
           authority.ProofBoundary is FileTransferFallbackTransitionProofBoundary fallback &&
           (fileTransferFallbackProofTransportEpoch <= 0 || fileTransferFallbackProofTransportEpoch == fallback.TargetTransportEpoch);
}
