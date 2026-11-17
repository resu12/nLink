namespace NLink.Core.FileTransfer;

// Read-only references into already-issued Coordinator evidence. This is not
// an install receipt, release receipt, or authorization for a physical effect.
internal sealed record FileTransferActivationRetryBindingSnapshot(
    string RetryAuthorityId,
    int RetryAuthorityGeneration,
    long InstallGeneration,
    string InstallReceiptFingerprint,
    string PairedReleaseAuthorityId,
    int PairedReleaseGeneration);

internal static partial class FileTransferCoordinator
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferActivationRetryBindingSnapshot?
        CreateActivationRetryBindingSnapshot(FileTransferRouteTransitionKind? kind, FileTransferRouteTransitionProofBoundary? proofBoundary)
    {
        if (kind != FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 ||
            proofBoundary is not FileTransferTunaActivationTransitionProofBoundary
              { EffectiveRetryInstallReceipt: { } install,
                FallbackTailPairedReleaseReceipt: { } release })
        {
            return null;
        }

        return new(
            install.Authority.AuthorityId.ToString(),
            install.Authority.AuthorityGeneration,
            install.InstallGeneration,
            CalculateSnapshotFingerprint(install),
            release.ReleaseAuthorityId.ToString(),
            release.ReleaseGeneration);
    }
}
