using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareSenderAutoTuneEvaluatorTests
{
    [Fact]
    public void Evaluate_SustainedRemoteHighFrameAge_EntersCatchUpAfterTwoTicks()
    {
        var first = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs());
        Assert.False(first.ShouldEnterCatchUp);
        Assert.Equal(ScreenShareSenderFreshnessMode.Reduced, first.NextSenderMode);

        var second = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks: first.RemoteHighFrameAgeCatchUpEntryConsecutiveTicks));

        Assert.True(second.ShouldEnterCatchUp);
        Assert.True(second.HasRemoteHighFrameAgeCatchUpPressure);
        Assert.Equal(ScreenShareSenderFreshnessMode.CatchUp, second.NextSenderMode);
        Assert.Equal(ScreenShareSenderOperatingState.Reduced, second.CurrentOperatingState);
        Assert.Equal(ScreenShareSenderOperatingState.CatchUp, second.NextOperatingState);
        Assert.Equal(ScreenShareSenderGuardState.None, second.GuardState);
        Assert.Equal("helper_pressure", second.DominantPressureBlocker);
        Assert.True(second.PressureSnapshot.RemoteHighFrameAgePressure);
        Assert.Equal("remote_high_frame_age_escalation", second.NextSenderModeReason);
    }

    [Fact]
    public void Evaluate_RecoveryLock_AllowsReducedToCatchUpWithinSameTuningBand()
    {
        var primed = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            recoveryLockBlocker: true,
            currentSenderMode: ScreenShareSenderFreshnessMode.Reduced));

        var decision = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            recoveryLockBlocker: true,
            currentSenderMode: ScreenShareSenderFreshnessMode.Reduced,
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks: primed.RemoteHighFrameAgeCatchUpEntryConsecutiveTicks));

        Assert.True(decision.ShouldEnterCatchUp);
        Assert.True(decision.RecoveryLockAllowsSameTuningModeChange);
        Assert.Equal(ScreenShareSenderFreshnessMode.CatchUp, decision.NextSenderMode);
    }

    [Fact]
    public void Evaluate_RecoveryLock_StillBlocksReducedToNormalPromotion()
    {
        var decision = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            currentSenderMode: ScreenShareSenderFreshnessMode.Reduced,
            recoveryLockBlocker: true,
            helperPromotionHealthy: true,
            helperProgressProofSatisfied: true,
            reducedRecoveryLowPressureTicks: 2,
            captureToSendAgeMs: 120,
            remotePressureObservedFrameAgeMs: 120,
            lastEncodeTotalDurationMs: 10,
            remotePressureMode: ScreenShareRemotePressureMode.None,
            currentRemotePressureReason: string.Empty));

        Assert.False(decision.ShouldEnterCatchUp);
        Assert.False(decision.ShouldEnterReduced);
        Assert.False(decision.RecoveryLockAllowsSameTuningModeChange);
        Assert.Equal(ScreenShareSenderGuardState.RecoveryLocked, decision.GuardState);
        Assert.Equal("recovery_lock", decision.DominantPressureBlocker);
        Assert.Equal(ScreenShareSenderFreshnessMode.Reduced, decision.NextSenderMode);
    }

    [Fact]
    public void Evaluate_CatchUpWithExplicitRemoteHighFrameAgePressure_DoesNotExitToReduced()
    {
        var decision = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            currentSenderMode: ScreenShareSenderFreshnessMode.CatchUp,
            catchUpRecoveryLowPressureTicks: 2,
            remotePressureObservedFrameAgeMs: 300,
            captureToSendAgeMs: 120,
            remotePressureMode: ScreenShareRemotePressureMode.ReduceFps,
            currentRemotePressureReason: ScreenSharePressureProtocol.PressureReasonHighFrameAge));

        Assert.False(decision.ShouldEnterCatchUp);
        Assert.True(decision.CatchUpLowPressureTick);
        Assert.True(decision.CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure);
        Assert.Equal(0, decision.CatchUpRecoveryLowPressureTicks);
        Assert.Equal(ScreenShareSenderFreshnessMode.CatchUp, decision.NextSenderMode);
        Assert.Equal("remote_pressure", decision.NextSenderModeReason);
    }

    [Fact]
    public void Evaluate_CatchUpAfterRemoteHighFrameAgeClears_ExitsViaExistingLowPressurePath()
    {
        var decision = ScreenShareSenderAutoTuneEvaluator.Evaluate(CreateInputs(
            currentSenderMode: ScreenShareSenderFreshnessMode.CatchUp,
            catchUpRecoveryLowPressureTicks: 2,
            remoteHighFrameAgePressure: false,
            remotePressureObservedFrameAgeMs: 150,
            captureToSendAgeMs: 120,
            remotePressureMode: ScreenShareRemotePressureMode.None,
            currentRemotePressureReason: ScreenSharePressureProtocol.PressureReasonHealthy));

        Assert.False(decision.ShouldEnterCatchUp);
        Assert.False(decision.CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure);
        Assert.True(decision.CatchUpLowPressureTick);
        Assert.Equal(3, decision.CatchUpRecoveryLowPressureTicks);
        Assert.Equal(ScreenShareSenderFreshnessMode.Reduced, decision.NextSenderMode);
        Assert.Equal("recovered", decision.NextSenderModeReason);
    }

    private static ScreenShareSenderAutoTuneInputs CreateInputs(
        ScreenShareSenderFreshnessMode currentSenderMode = ScreenShareSenderFreshnessMode.Reduced,
        int captureToSendCatchUpPressureTicks = 0,
        int remoteObservedCatchUpPressureTicks = 0,
        int normalToReducedPressureTicks = 0,
        int remoteHighFrameAgeCatchUpEntryConsecutiveTicks = 0,
        int catchUpRecoveryLowPressureTicks = 0,
        int reducedRecoveryLowPressureTicks = 0,
        int reducedPromotionEncodeSoftSpikeConsecutiveCount = 0,
        bool suppressAgeOnlyPressureForGrace = false,
        bool remoteHighFrameAgePressure = true,
        bool helperProgressProofSatisfied = true,
        bool currentEpochRecoveryBurstActive = false,
        bool bootstrapModeGraceActive = false,
        bool postAckModeGraceActive = false,
        bool hasCatchUpExternalPressure = false,
        bool hasImmediateReducedPressure = false,
        bool hasLocalCatchUpPressure = false,
        bool hasLocalReducedPressure = false,
        long remotePressureObservedFrameAgeMs = 500,
        long captureToSendAgeMs = 120,
        int promotionCaptureToSendBudgetMs = 220,
        long lastEncodeTotalDurationMs = 32,
        int promotionEncodeBudgetMs = 80,
        int demotionEncodePressureMs = 160,
        bool transitionGraceActive = false,
        bool inStartupWarmup = false,
        bool helperPromotionHealthy = true,
        int laneQueueDepth = 0,
        long laneRecentDrops = 0,
        bool fileTransferDegradedHint = false,
        bool fileTransferCatchUpOnlyHintActive = false,
        bool hasLaneCongestion = false,
        bool hasSevereLaneCongestion = false,
        bool hasQueuePressure = false,
        long oldestQueuedAgeMs = 0,
        bool hasSevereHealthDegradation = false,
        bool hasActionableHealthDegradation = false,
        bool recoveryLockBlocker = false,
        bool recoveryLockSevereOverride = false,
        ScreenShareRemotePressureMode remotePressureMode = ScreenShareRemotePressureMode.ReduceFps,
        string currentRemotePressureReason = ScreenSharePressureProtocol.PressureReasonHighFrameAge)
        => new(
            currentSenderMode,
            captureToSendCatchUpPressureTicks,
            remoteObservedCatchUpPressureTicks,
            normalToReducedPressureTicks,
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks,
            catchUpRecoveryLowPressureTicks,
            reducedRecoveryLowPressureTicks,
            reducedPromotionEncodeSoftSpikeConsecutiveCount,
            suppressAgeOnlyPressureForGrace,
            remoteHighFrameAgePressure,
            helperProgressProofSatisfied,
            currentEpochRecoveryBurstActive,
            bootstrapModeGraceActive,
            postAckModeGraceActive,
            hasCatchUpExternalPressure,
            hasImmediateReducedPressure,
            hasLocalCatchUpPressure,
            hasLocalReducedPressure,
            remotePressureObservedFrameAgeMs,
            captureToSendAgeMs,
            promotionCaptureToSendBudgetMs,
            lastEncodeTotalDurationMs,
            promotionEncodeBudgetMs,
            demotionEncodePressureMs,
            transitionGraceActive,
            inStartupWarmup,
            helperPromotionHealthy,
            laneQueueDepth,
            laneRecentDrops,
            fileTransferDegradedHint,
            fileTransferCatchUpOnlyHintActive,
            hasLaneCongestion,
            hasSevereLaneCongestion,
            hasQueuePressure,
            oldestQueuedAgeMs,
            hasSevereHealthDegradation,
            hasActionableHealthDegradation,
            recoveryLockBlocker,
            recoveryLockSevereOverride,
            remotePressureMode,
            currentRemotePressureReason);
}
