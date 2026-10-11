using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateRejectedProbeRetryCycleSnapshotFixture(string reason, bool retainPeerProof = false)
    {
        var snapshots = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (state, _, predecessor) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource(snapshotCapture: snapshots);
        var release = state.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairedRelease!.Value;
        if (retainPeerProof)
        {
            var proof = release.PeerProof.Proof with
            {
                AuthorityId = new FileTransferAuthorityId("fta2-fallback_tail_yield-fedcba9876543210fedcba98"),
                PredecessorAuthorityId = release.PeerProof.Proof.AuthorityId,
                YieldGeneration = release.PeerProof.Proof.YieldGeneration + 1,
            };
            var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(state,
                new FileTransferFallbackTailPeerProofReceipt(proof,
                    FileTransferCoordinator.GetEffectiveFallbackTailYieldScope(state.PersistentState.FallbackTailYield.CurrentOperation.Value),
                    "retry-cycle-successor-proof-before-local-supersession", 40_000));
            Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Accepted, retained.Status);
            snapshots.Add(retained.SnapshotEnvelope!);
            state = retained.State;
        }
        var superseded = FileTransferCoordinator.SupersedeFallbackTailPairedYieldAfterRejectedProbe(
            state, FileTransferFallbackTailRetryCycleSupersessionRole.Responder,
            predecessor, release, expectedInstallReceipt: null, reason);
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Accepted, superseded.Status);
        snapshots.Add(superseded.SnapshotEnvelope!);
        var leg = state.CurrentLeg!;
        var successor = FileTransferCoordinator.Apply(CoordinatorEvent(
            FileTransferCoordinatorEventKind.FallbackTailQuiescent,
            FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
            reason: reason, state: FileTransferLegState.Active, canSendData: true,
            committedChunk: Math.Max(0, leg.ProvenCommittedChunkIndex),
            highestObservedChunk: leg.ProvenHighestObservedChunkIndex,
            transportEpoch: leg.TransportEpochId, bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration), superseded.State);
        Assert.Null(successor.MutationRejectedReason);
        Assert.Equal(predecessor.AuthorityId, successor.FallbackTailYieldDecision!.Value.PredecessorAuthorityId);
        snapshots.Add(successor.SnapshotEnvelope!);
        return snapshots.ToArray();
    }
}
