using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [Trait("FileTransferRuntimeSuite", "InboundPeerFirstFrontier")]
    public void InboundPeerFirstYield_WaitsForCurrentFrontierObligation(
        bool settledRepair, bool allowCurrentTransportEpochRecoveryHandoff)
    {
        const string transferId = "inbound_peer_first_frontier";
        using var service = new SessionFileTransferService();
        var type = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        type.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var leg = GetCurrentContextLeg(context);
        leg.State = FileTransferLegState.RecoveryActive;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        SetPrivateProperty(leg, "StartCommittedChunkIndex", 0);
        leg.ProvenCommittedChunkIndex = 1;
        leg.ProvenHighestObservedChunkIndex = 3;
        SetPrivateProperty(context, "CurrentFallbackCheckpointLease", null);
        SetPrivateProperty(context, "V6PostTunaFallbackProofReplayReceiverStatePending", false);
        SetPrivateProperty(context, "V6PostTunaFallbackProofReplayFrontierRequestPending", false);
        SetPrivateProperty(context, "ChunkCount", 8);
        SetPrivateProperty(context, "NextChunkIndex", settledRepair ? 0 : 1);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 3);

        var identity = type.GetMethod("CreateFallbackFrontierRepairIdentity",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [new List<FileTransferRangeV4> { new() { StartChunkIndex = settledRepair ? 0 : 1, ChunkCount = 1 } }, 8, 3]);
        object?[] issueArgs = [context, identity, FallbackFrontierRepairSourceKind.ReceiverRequest,
            "peer-first-frontier", null, "frontier", "regular_nkn_frontier_stall_control_bulk",
            "unit_peer_first_frontier", null, null, null];
        Assert.True(Assert.IsType<bool>(type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryIssueFallbackFrontierRepairContractLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType()).Invoke(service, issueArgs)),
            issueArgs[^1]?.ToString());
        var contract = issueArgs[^2]!;
        SetPrivateProperty(context, "CurrentFallbackFrontierRepairContract", contract);
        var settle = type.GetMethod("TrySettleFallbackFrontierRepairContract", BindingFlags.Static | BindingFlags.NonPublic)!;
        void SettleRepair() => Assert.True(Assert.IsType<bool>(settle.Invoke(null,
            [contract, Enum.Parse(settle.GetParameters()[1].ParameterType, "ProgressObserved"), "unit_frontier_progress", null])));
        if (settledRepair) SettleRepair();

        // Settled repair 0 must not hide the new sparse gap at 1. The other
        // case isolates an unsettled contract without a bitmap frontier gap.
        var written = new BitArray(8, !settledRepair);
        written[0] = true;
        written[3] = true;
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
        SetPrivateProperty(context, "NextChunkIndex", 1);
        var before = CreateCoordinatorStateForTest(context);
        var sessionId = GetPrivateProperty<string>(context, "SessionId");
        var peer = new FileTransferFallbackTailYieldFrameV6
        {
            AuthoritySchema = FileTransferAuthoritySchema.Current,
            SessionId = sessionId, TransferId = transferId, Direction = "outbound",
            TransportEpoch = leg.TransportEpochId, LiveRouteEpoch = leg.LiveRouteEpochId,
            FallbackLegGeneration = leg.Generation, BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            OwnerKey = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(sessionId, transferId,
                leg.RouteSelection.TelemetryToken, leg.ProtocolVersion, leg.LiveRouteEpochId,
                leg.Generation, leg.BridgeRecoveryGeneration, leg.TransportEpochId, "(none)"),
            PairKey = FileTransferCoordinator.BuildFallbackTailYieldPairKey(sessionId, transferId,
                leg.RouteSelection.TelemetryToken, leg.ProtocolVersion, leg.LiveRouteEpochId,
                leg.TransportEpochId, "(none)"),
            YieldGeneration = 1, AuthorityId = CreatePeerFallbackTailYieldAuthorityId(transferId),
            CommittedChunkIndex = 1, HighestObservedChunkIndex = 3, RecoveryMode = "fallback_tail_yield",
        };
        var applyPeer = type.GetMethod("ApplyInboundFallbackTailYieldProof", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var logStart = GetOperationalLogLength();
        applyPeer.Invoke(service, [context, peer]);

        var pending = GetCoordinatorFallbackTailYieldSubstate(context);
        Assert.NotNull(pending.PendingPeerProofBeforeLocalYield);
        Assert.Null(pending.CurrentOperation);
        Assert.False(Assert.IsType<bool>(type.GetMethod("TryMarkInboundFallbackTailLocalYieldFromCurrentLegLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [context, "transport_epoch_reauthorization", allowCurrentTransportEpochRecoveryHandoff, null])));
        var blocked = CreateCoordinatorStateForTest(context);
        Assert.Equal(before.CurrentRuntimeOwner, blocked.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, blocked.CurrentRoutePublicationToken);
        Assert.Equal(FileTransferLegState.RecoveryActive, blocked.CurrentLeg!.State);
        Assert.Equal(before.CurrentRuntimeUnlockAdmission, blocked.CurrentRuntimeUnlockAdmission);
        Assert.True(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
        Assert.Same(contract, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_tail_yield_blocked_by_frontier_obligation;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_tail_quiescent_yield_granted;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_tail_recovery_owner_retired_after_yield;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_tail_paired_yield_granted;", log, StringComparison.Ordinal);

        // Fresh current progress closes the gap; the original pending peer
        // proof must carry into a legitimate local yield without redelivery.
        if (!settledRepair) SettleRepair();
        written[1] = true;
        written[2] = true;
        SetPrivateProperty(context, "NextChunkIndex", 4);
        Assert.True(Assert.IsType<bool>(type.GetMethod("TryMarkInboundFallbackTailQuiescentLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, leg.TransportEpochId, "chunk_batch_committed"])));
        var issued = GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation!.Value;
        Assert.Equal(pending.PendingPeerProofBeforeLocalYield!.Value.Proof, issued.PeerProof!.Value.Proof);
        var paired = ApplyCurrentFallbackTailLocalProofForTest(context, "unit_peer_first_frontier_local_proof");
        Assert.NotNull(paired.PairedRelease);
        applyPeer.Invoke(service, [context, peer]);
        Assert.Equal(paired.PairedRelease, GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation!.Value.PairedRelease);
        Assert.Equal(paired.Decision.YieldGeneration, GetCoordinatorFallbackTailYieldSubstate(context).LastYieldGeneration);
    }
}
