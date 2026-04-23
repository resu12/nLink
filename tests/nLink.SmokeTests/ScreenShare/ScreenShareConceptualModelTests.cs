using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareConceptualModelTests
{
    [Fact]
    public void LossTaxonomyMapper_ClassifiesOlderEpochCleanupSeparately()
    {
        var classification = ScreenShareLossTaxonomyMapper.ClassifySession(
            ScreenShareFrameLossSessionSnapshot.Empty with
            {
                OlderEpochCleanupAfterEpochAdvanceCount = 9,
            });

        Assert.Equal(ScreenShareLossClass.OlderEpochCleanup, classification);
    }

    [Fact]
    public void LossTaxonomyMapper_ClassifiesSameEpochRecoverySuppressedBeforeCleanup()
    {
        var classification = ScreenShareLossTaxonomyMapper.ClassifySession(
            ScreenShareFrameLossSessionSnapshot.Empty with
            {
                SuppressedEmitDuringRecoveryWaitCount = 2,
                OlderEpochCleanupAfterEpochAdvanceCount = 5,
            });

        Assert.Equal(ScreenShareLossClass.SameEpochRecoverySuppressed, classification);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_PrefersHelperDomainWhenRecoveryActive()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.Build(
            new ScreenShareMetrics(
                SenderOperatingState: "catch_up",
                SenderGuardState: "none",
                DominantPressureBlocker: "helper_pressure",
                RecoveryActive: true),
            new ScreenShareMetrics(
                HelperSessionPhase: "recovering",
                HelperRecoveryMechanism: "waiting_for_recovery_keyframe",
                DominantLossClass: "current_epoch_actionable_loss",
                BaselineEstablished: false,
                SteadyVisibleProgressActive: false,
                RecoveryActive: true));

        Assert.Equal(ScreenShareOperationalTroubleDomain.Helper, snapshot.DominantTroubleDomain);
        Assert.Equal(ScreenShareSenderOperatingState.CatchUp, snapshot.SenderOperatingState);
        Assert.Equal(HelperRemoteSessionPhase.Recovering, snapshot.HelperSessionPhase);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_UsesSenderDomainWhenSenderIsGuardedWithoutHelperTrouble()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.Build(
            new ScreenShareMetrics(
                SenderOperatingState: "reduced",
                SenderGuardState: "recovery_locked",
                DominantPressureBlocker: "recovery_lock"),
            new ScreenShareMetrics(
                HelperSessionPhase: "visible_stable",
                HelperRecoveryMechanism: "none",
                DominantLossClass: "benign_stale_cleanup",
                BaselineEstablished: true,
                SteadyVisibleProgressActive: true));

        Assert.Equal(ScreenShareOperationalTroubleDomain.Sender, snapshot.DominantTroubleDomain);
        Assert.Equal(ScreenShareSenderGuardState.RecoveryLocked, snapshot.SenderGuardState);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_InfersVisibleStableWhenBaselineExistsButPhaseFieldIsDefault()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.Build(
            new ScreenShareMetrics(),
            new ScreenShareMetrics(
                HelperSessionPhase: "no_visible_baseline",
                HelperRecoveryMechanism: "none",
                DominantLossClass: "benign_stale_cleanup",
                BaselineEstablished: true,
                SteadyVisibleProgressActive: true,
                VisibleHeadFrameId: 96,
                StableVisibleHeadFrameId: 97));

        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.HelperSessionPhase);
        Assert.Equal(ScreenShareOperationalTroubleDomain.None, snapshot.DominantTroubleDomain);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_InfersRecoveringCorridorWhenRecoverySignalsExistWithoutExplicitFields()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.Build(
            new ScreenShareMetrics(),
            new ScreenShareMetrics(
                HelperSessionPhase: "no_visible_baseline",
                HelperRecoveryMechanism: "none",
                DominantLossClass: "benign_stale_cleanup",
                BaselineEstablished: false,
                RecoveryActive: true,
                RecoveryWindowActive: true,
                RecoveryProgressCorridorCount: 1));

        Assert.Equal(HelperRemoteSessionPhase.Recovering, snapshot.HelperSessionPhase);
        Assert.Equal(HelperRemoteRecoveryMechanism.RecoveryCorridor, snapshot.HelperRecoveryMechanism);
        Assert.Equal(ScreenShareOperationalTroubleDomain.Helper, snapshot.DominantTroubleDomain);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_BuildFromSenderSummary_UsesRemoteHelperProofForVisibleStable()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.BuildFromSenderSummary(
            ScreenShareSenderOperatingState.Reduced,
            ScreenShareSenderGuardState.None,
            dominantPressureBlocker: "helper_pressure",
            recoveryActive: false,
            helperSteadyVisibleProgressActive: true,
            helperFactHealthyActive: true,
            helperVisibleHeadFrameId: 81,
            helperStableVisibleHeadFrameId: 80,
            helperVisibleRecoveryFloorFrameId: 75,
            helperRecoveryKeyframeApplyCount: 0);

        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.HelperSessionPhase);
        Assert.Equal(HelperRemoteRecoveryMechanism.None, snapshot.HelperRecoveryMechanism);
        Assert.True(snapshot.BaselineEstablished);
        Assert.True(snapshot.SteadyVisibleProgressActive);
    }

    [Fact]
    public void OperationalHealthSnapshotBuilder_BuildFromSenderSummary_DoesNotStayRecoveringAfterRecoveryProofSettles()
    {
        var snapshot = ScreenShareOperationalHealthSnapshotBuilder.BuildFromSenderSummary(
            ScreenShareSenderOperatingState.Normal,
            ScreenShareSenderGuardState.None,
            dominantPressureBlocker: "none",
            recoveryActive: false,
            helperSteadyVisibleProgressActive: true,
            helperFactHealthyActive: true,
            helperVisibleHeadFrameId: 118,
            helperStableVisibleHeadFrameId: 117,
            helperVisibleRecoveryFloorFrameId: 111,
            helperRecoveryKeyframeApplyCount: 2);

        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.HelperSessionPhase);
        Assert.Equal(HelperRemoteRecoveryMechanism.None, snapshot.HelperRecoveryMechanism);
        Assert.Equal(ScreenShareOperationalTroubleDomain.None, snapshot.DominantTroubleDomain);
    }
}
