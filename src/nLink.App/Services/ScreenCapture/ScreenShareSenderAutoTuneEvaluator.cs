using System;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareSenderAutoTuneInputs(
    ScreenShareSenderFreshnessMode CurrentSenderMode,
    int CaptureToSendCatchUpPressureTicks,
    int RemoteObservedCatchUpPressureTicks,
    int NormalToReducedPressureTicks,
    int RemoteHighFrameAgeCatchUpEntryConsecutiveTicks,
    int CatchUpRecoveryLowPressureTicks,
    int ReducedRecoveryLowPressureTicks,
    int ReducedPromotionEncodeSoftSpikeConsecutiveCount,
    bool SuppressAgeOnlyPressureForGrace,
    bool RemoteHighFrameAgePressure,
    bool HelperProgressProofSatisfied,
    bool CurrentEpochRecoveryBurstActive,
    bool BootstrapModeGraceActive,
    bool PostAckModeGraceActive,
    bool HasCatchUpExternalPressure,
    bool HasImmediateReducedPressure,
    bool HasLocalCatchUpPressure,
    bool HasLocalReducedPressure,
    long RemotePressureObservedFrameAgeMs,
    long CaptureToSendAgeMs,
    int PromotionCaptureToSendBudgetMs,
    long LastEncodeTotalDurationMs,
    int PromotionEncodeBudgetMs,
    int DemotionEncodePressureMs,
    bool TransitionGraceActive,
    bool InStartupWarmup,
    bool HelperPromotionHealthy,
    int LaneQueueDepth,
    long LaneRecentDrops,
    bool FileTransferDegradedHint,
    bool FileTransferCatchUpOnlyHintActive,
    bool HasLaneCongestion,
    bool HasSevereLaneCongestion,
    bool HasQueuePressure,
    long OldestQueuedAgeMs,
    bool HasSevereHealthDegradation,
    bool HasActionableHealthDegradation,
    bool RecoveryLockBlocker,
    bool RecoveryLockSevereOverride,
    ScreenShareRemotePressureMode RemotePressureMode,
    string CurrentRemotePressureReason);

internal readonly record struct ScreenShareSenderAutoTuneDecision(
    int CaptureToSendCatchUpPressureTicks,
    int RemoteObservedCatchUpPressureTicks,
    int NormalToReducedPressureTicks,
    int RemoteHighFrameAgeCatchUpEntryConsecutiveTicks,
    int CatchUpRecoveryLowPressureTicks,
    int ReducedRecoveryLowPressureTicks,
    int ReducedPromotionEncodeSoftSpikeConsecutiveCount,
    bool HasRemoteHighFrameAgeCatchUpPressure,
    bool ShouldEnterCatchUp,
    bool ShouldEnterReduced,
    bool CatchUpLowPressureTick,
    bool ReducedLowPressureTick,
    bool EncodeBudgetBlocker,
    bool EncodeSoftPromotionOverrun,
    bool EncodeSoftSpikeResetSuppressed,
    bool CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure,
    string RemoteHighFrameAgeCatchUpSuppressionReason,
    ScreenShareSenderFreshnessMode NextSenderMode,
    bool RecoveryLockAllowsSameTuningModeChange,
    string NextSenderModeReason,
    ScreenShareSenderOperatingState CurrentOperatingState,
    ScreenShareSenderOperatingState NextOperatingState,
    ScreenShareSenderGuardState GuardState,
    string DominantPressureBlocker,
    ScreenShareSenderPressureSnapshot PressureSnapshot);

internal static class ScreenShareSenderAutoTuneEvaluator
{
    private const int CatchUpModeRemoteAgeThresholdMs = 1200;
    private const int RemoteHighFrameAgeCatchUpEntryThresholdMs = 400;
    private const int CatchUpEntryConsecutiveTicks = 2;
    private const int NormalToReducedEntryConsecutiveTicks = 2;
    private const int CatchUpRecoveryConsecutiveTicks = 3;
    private const int ReducedRecoveryConsecutiveTicks = 3;
    private const int ReducedPromotionEncodeSoftSpikeResetConsecutiveEvaluations = 2;
    private const int CatchUpRecoveryCaptureToSendThresholdMs = 450;
    private const int CatchUpRecoveryRemoteAgeThresholdMs = 900;

    public static ScreenShareSenderAutoTuneDecision Evaluate(ScreenShareSenderAutoTuneInputs input)
    {
        var currentOperatingState = MapOperatingState(input.CurrentSenderMode);
        var guardState = ResolveGuardState(input);
        var captureToSendCatchUpPressureTicks =
            input.SuppressAgeOnlyPressureForGrace
                ? 0
                : input.HasLocalCatchUpPressure
                    ? input.CaptureToSendCatchUpPressureTicks + 1
                    : 0;
        var remoteObservedCatchUpPressureTicks =
            input.SuppressAgeOnlyPressureForGrace
                ? 0
                : input.RemotePressureObservedFrameAgeMs >= CatchUpModeRemoteAgeThresholdMs
                    ? input.RemoteObservedCatchUpPressureTicks + 1
                    : 0;
        var normalToReducedPressureTicks =
            input.SuppressAgeOnlyPressureForGrace
                ? 0
                : input.HasLocalReducedPressure
                    ? input.NormalToReducedPressureTicks + 1
                    : 0;
        var explicitRemoteHighFrameAgePressureActive =
            input.RemotePressureMode == ScreenShareRemotePressureMode.ReduceFps &&
            string.Equals(input.CurrentRemotePressureReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal);

        var remoteHighFrameAgeCatchUpEligible =
            !input.SuppressAgeOnlyPressureForGrace &&
            explicitRemoteHighFrameAgePressureActive &&
            input.HelperProgressProofSatisfied &&
            !input.CurrentEpochRecoveryBurstActive &&
            input.RemotePressureObservedFrameAgeMs >= RemoteHighFrameAgeCatchUpEntryThresholdMs;
        var remoteHighFrameAgeCatchUpEntryConsecutiveTicks =
            remoteHighFrameAgeCatchUpEligible
                ? input.RemoteHighFrameAgeCatchUpEntryConsecutiveTicks + 1
                : 0;
        var hasRemoteHighFrameAgeCatchUpPressure =
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks >= CatchUpEntryConsecutiveTicks;

        var remoteHighFrameAgeCatchUpSuppressionReason = string.Empty;
        if (input.RemoteHighFrameAgePressure &&
            input.CurrentSenderMode != ScreenShareSenderFreshnessMode.CatchUp &&
            !hasRemoteHighFrameAgeCatchUpPressure)
        {
            remoteHighFrameAgeCatchUpSuppressionReason =
                input.BootstrapModeGraceActive
                    ? "bootstrap_grace"
                    : input.PostAckModeGraceActive
                        ? "post_ack_grace"
                        : input.CurrentEpochRecoveryBurstActive
                            ? "current_epoch_recovery_burst"
                            : !input.HelperProgressProofSatisfied
                                ? "missing_helper_evidence"
                                : "under_threshold";
        }

        var shouldEnterCatchUp =
            input.HasCatchUpExternalPressure ||
            captureToSendCatchUpPressureTicks >= CatchUpEntryConsecutiveTicks ||
            remoteObservedCatchUpPressureTicks >= CatchUpEntryConsecutiveTicks ||
            hasRemoteHighFrameAgeCatchUpPressure;
        var shouldEnterReduced =
            input.HasImmediateReducedPressure ||
            normalToReducedPressureTicks >= NormalToReducedEntryConsecutiveTicks;

        if (input.BootstrapModeGraceActive &&
            !input.HasCatchUpExternalPressure &&
            (captureToSendCatchUpPressureTicks > 0 ||
             remoteObservedCatchUpPressureTicks > 0 ||
             input.RemoteHighFrameAgePressure))
        {
            shouldEnterCatchUp = false;
            shouldEnterReduced = input.HasImmediateReducedPressure;
        }

        var catchUpLowPressureTick =
            input.CaptureToSendAgeMs >= 0 &&
            input.CaptureToSendAgeMs <= CatchUpRecoveryCaptureToSendThresholdMs &&
            input.RemotePressureObservedFrameAgeMs <= CatchUpRecoveryRemoteAgeThresholdMs &&
            !input.HasCatchUpExternalPressure;
        var reducedLowPressureTickWithoutEncode =
            !input.TransitionGraceActive &&
            !input.InStartupWarmup &&
            input.CaptureToSendAgeMs >= 0 &&
            input.CaptureToSendAgeMs <= input.PromotionCaptureToSendBudgetMs &&
            input.HelperPromotionHealthy &&
            input.LaneQueueDepth == 0 &&
            input.LaneRecentDrops == 0 &&
            !input.FileTransferDegradedHint &&
            !input.FileTransferCatchUpOnlyHintActive &&
            !input.HasLaneCongestion &&
            !input.HasQueuePressure;
        var encodeSampleMissing = input.LastEncodeTotalDurationMs < 0;
        var encodeSeverePromotionOverrun = input.LastEncodeTotalDurationMs > input.DemotionEncodePressureMs;
        var encodeSoftPromotionOverrun =
            input.LastEncodeTotalDurationMs >= 0 &&
            input.LastEncodeTotalDurationMs > input.PromotionEncodeBudgetMs &&
            !encodeSeverePromotionOverrun;
        var encodeBudgetBlocker = encodeSampleMissing || encodeSeverePromotionOverrun || encodeSoftPromotionOverrun;
        var encodeSoftSpikeResetSuppressed = false;
        var reducedPromotionEncodeSoftSpikeConsecutiveCount = 0;
        if (input.CurrentSenderMode == ScreenShareSenderFreshnessMode.Reduced &&
            reducedLowPressureTickWithoutEncode &&
            encodeSoftPromotionOverrun)
        {
            reducedPromotionEncodeSoftSpikeConsecutiveCount =
                input.ReducedPromotionEncodeSoftSpikeConsecutiveCount + 1;
            if (reducedPromotionEncodeSoftSpikeConsecutiveCount < ReducedPromotionEncodeSoftSpikeResetConsecutiveEvaluations)
            {
                encodeBudgetBlocker = false;
                encodeSoftSpikeResetSuppressed = true;
            }
        }
        var reducedLowPressureTick =
            reducedLowPressureTickWithoutEncode &&
            input.LastEncodeTotalDurationMs >= 0 &&
            !encodeBudgetBlocker;
        var pressureSnapshot = new ScreenShareSenderPressureSnapshot(
            LocalCaptureAgePressure: input.HasLocalCatchUpPressure || input.HasLocalReducedPressure,
            RemoteHighFrameAgePressure: explicitRemoteHighFrameAgePressureActive,
            ImmediateReducedPressure: input.HasImmediateReducedPressure,
            QueueOrLanePressure:
                input.HasQueuePressure ||
                input.HasLaneCongestion ||
                input.HasSevereLaneCongestion ||
                input.LaneQueueDepth > 0 ||
                input.LaneRecentDrops > 0 ||
                input.OldestQueuedAgeMs > 0,
            BridgeHealthPressure: input.HasActionableHealthDegradation || input.HasSevereHealthDegradation,
            HelperProgressProofSatisfied: input.HelperProgressProofSatisfied,
            CurrentEpochRecoveryActive: input.CurrentEpochRecoveryBurstActive,
            EncodeBudgetPressure: encodeBudgetBlocker);

        int catchUpRecoveryLowPressureTicks;
        int reducedRecoveryLowPressureTicks;
        ScreenShareSenderFreshnessMode nextSenderMode;
        var catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure = false;

        if (input.CurrentSenderMode == ScreenShareSenderFreshnessMode.CatchUp)
        {
            if (shouldEnterCatchUp)
            {
                catchUpRecoveryLowPressureTicks = 0;
                nextSenderMode = ScreenShareSenderFreshnessMode.CatchUp;
            }
            else if (explicitRemoteHighFrameAgePressureActive)
            {
                catchUpRecoveryLowPressureTicks = 0;
                nextSenderMode = ScreenShareSenderFreshnessMode.CatchUp;
                catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure = true;
            }
            else
            {
                catchUpRecoveryLowPressureTicks =
                    catchUpLowPressureTick
                        ? input.CatchUpRecoveryLowPressureTicks + 1
                        : 0;
                nextSenderMode =
                    catchUpRecoveryLowPressureTicks >= CatchUpRecoveryConsecutiveTicks
                        ? ScreenShareSenderFreshnessMode.Reduced
                        : ScreenShareSenderFreshnessMode.CatchUp;
            }

            reducedRecoveryLowPressureTicks = 0;
        }
        else
        {
            catchUpRecoveryLowPressureTicks = 0;
            if (shouldEnterCatchUp)
            {
                reducedRecoveryLowPressureTicks = 0;
                nextSenderMode = ScreenShareSenderFreshnessMode.CatchUp;
            }
            else if (input.CurrentSenderMode == ScreenShareSenderFreshnessMode.Normal)
            {
                reducedRecoveryLowPressureTicks = 0;
                nextSenderMode =
                    shouldEnterReduced
                        ? ScreenShareSenderFreshnessMode.Reduced
                        : ScreenShareSenderFreshnessMode.Normal;
            }
            else if (shouldEnterReduced)
            {
                reducedRecoveryLowPressureTicks = 0;
                nextSenderMode = ScreenShareSenderFreshnessMode.Reduced;
            }
            else if (input.CurrentSenderMode == ScreenShareSenderFreshnessMode.Reduced)
            {
                reducedRecoveryLowPressureTicks =
                    reducedLowPressureTick
                        ? input.ReducedRecoveryLowPressureTicks + 1
                        : 0;
                nextSenderMode =
                    reducedRecoveryLowPressureTicks >= ReducedRecoveryConsecutiveTicks
                        ? ScreenShareSenderFreshnessMode.Normal
                        : ScreenShareSenderFreshnessMode.Reduced;
            }
            else
            {
                reducedRecoveryLowPressureTicks = 0;
                nextSenderMode = ScreenShareSenderFreshnessMode.Normal;
            }
        }

        var recoveryLockAllowsSameTuningModeChange =
            input.RecoveryLockBlocker &&
            !input.RecoveryLockSevereOverride &&
            nextSenderMode != input.CurrentSenderMode &&
            CanRecoveryLockAllowModeTransition(input.CurrentSenderMode, nextSenderMode);

        if (input.RecoveryLockBlocker &&
            !recoveryLockAllowsSameTuningModeChange &&
            !input.RecoveryLockSevereOverride &&
            nextSenderMode != input.CurrentSenderMode)
        {
            nextSenderMode = input.CurrentSenderMode;
        }

        var nextSenderModeReason = ResolveSenderFreshnessReason(
            input.FileTransferDegradedHint,
            input.FileTransferCatchUpOnlyHintActive,
            input.HasSevereHealthDegradation,
            input.HasActionableHealthDegradation,
            input.HasSevereLaneCongestion,
            hasLaneCongestion: input.HasLaneCongestion,
            oldestQueuedAgeMs: input.OldestQueuedAgeMs,
            hasQueuePressure: input.HasQueuePressure,
            hasSenderPressure: input.HasLocalReducedPressure,
            hasCatchUpPressure: shouldEnterCatchUp,
            hasRemoteHighFrameAgeCatchUpPressure: hasRemoteHighFrameAgeCatchUpPressure,
            remotePressureMode: input.RemotePressureMode);
        var nextOperatingState = MapOperatingState(nextSenderMode);
        var dominantPressureBlocker = ResolveDominantPressureBlocker(
            guardState,
            pressureSnapshot,
            input.FileTransferDegradedHint,
            input.FileTransferCatchUpOnlyHintActive);

        return new ScreenShareSenderAutoTuneDecision(
            captureToSendCatchUpPressureTicks,
            remoteObservedCatchUpPressureTicks,
            normalToReducedPressureTicks,
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks,
            catchUpRecoveryLowPressureTicks,
            reducedRecoveryLowPressureTicks,
            reducedPromotionEncodeSoftSpikeConsecutiveCount,
            hasRemoteHighFrameAgeCatchUpPressure,
            shouldEnterCatchUp,
            shouldEnterReduced,
            catchUpLowPressureTick,
            reducedLowPressureTick,
            encodeBudgetBlocker,
            encodeSoftPromotionOverrun,
            encodeSoftSpikeResetSuppressed,
            catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure,
            remoteHighFrameAgeCatchUpSuppressionReason,
            nextSenderMode,
            recoveryLockAllowsSameTuningModeChange,
            nextSenderModeReason,
            currentOperatingState,
            nextOperatingState,
            guardState,
            dominantPressureBlocker,
            pressureSnapshot);
    }

    internal static ScreenShareSenderOperatingState MapOperatingState(ScreenShareSenderFreshnessMode mode)
        => mode switch
        {
            ScreenShareSenderFreshnessMode.Reduced => ScreenShareSenderOperatingState.Reduced,
            ScreenShareSenderFreshnessMode.CatchUp => ScreenShareSenderOperatingState.CatchUp,
            _ => ScreenShareSenderOperatingState.Normal,
        };

    internal static ScreenShareSenderGuardState ResolveGuardState(ScreenShareSenderAutoTuneInputs input)
    {
        if (input.RecoveryLockBlocker)
        {
            return ScreenShareSenderGuardState.RecoveryLocked;
        }

        if (input.PostAckModeGraceActive)
        {
            return ScreenShareSenderGuardState.PostAckGrace;
        }

        if (input.BootstrapModeGraceActive)
        {
            return ScreenShareSenderGuardState.BootstrapGrace;
        }

        if (input.TransitionGraceActive)
        {
            return ScreenShareSenderGuardState.TransitionGrace;
        }

        return ScreenShareSenderGuardState.None;
    }

    private static string ResolveDominantPressureBlocker(
        ScreenShareSenderGuardState guardState,
        ScreenShareSenderPressureSnapshot pressureSnapshot,
        bool fileTransferDegradedHint,
        bool fileTransferCatchUpOnlyHint)
    {
        if (guardState == ScreenShareSenderGuardState.RecoveryLocked)
        {
            return "recovery_lock";
        }

        if (guardState == ScreenShareSenderGuardState.PostAckGrace)
        {
            return "post_ack_grace";
        }

        if (guardState == ScreenShareSenderGuardState.BootstrapGrace)
        {
            return "bootstrap_grace";
        }

        if (guardState == ScreenShareSenderGuardState.TransitionGrace)
        {
            return "transition_grace";
        }

        if (pressureSnapshot.RemoteHighFrameAgePressure || pressureSnapshot.ImmediateReducedPressure)
        {
            return "helper_pressure";
        }

        if (fileTransferCatchUpOnlyHint)
        {
            return "file_transfer_pressure";
        }

        if (fileTransferDegradedHint)
        {
            return "file_transfer";
        }

        if (pressureSnapshot.BridgeHealthPressure)
        {
            return "bridge_health";
        }

        if (pressureSnapshot.QueueOrLanePressure)
        {
            return "queue_evict";
        }

        if (pressureSnapshot.EncodeBudgetPressure)
        {
            return "encode_budget";
        }

        if (pressureSnapshot.LocalCaptureAgePressure)
        {
            return "capture_age";
        }

        return "none";
    }

    public static string ResolveSenderFreshnessReason(
        bool fileTransferDegradedHint,
        bool fileTransferCatchUpOnlyHint,
        bool hasSevereHealthDegradation,
        bool hasActionableHealthDegradation,
        bool hasSevereLaneCongestion,
        bool hasLaneCongestion,
        long oldestQueuedAgeMs,
        bool hasQueuePressure,
        bool hasSenderPressure,
        bool hasCatchUpPressure,
        bool hasRemoteHighFrameAgeCatchUpPressure,
        ScreenShareRemotePressureMode remotePressureMode)
    {
        if (hasRemoteHighFrameAgeCatchUpPressure)
        {
            return "remote_high_frame_age_escalation";
        }

        if (remotePressureMode == ScreenShareRemotePressureMode.CatchUpOnly ||
            remotePressureMode == ScreenShareRemotePressureMode.ReduceFps)
        {
            return "remote_pressure";
        }

        if (fileTransferCatchUpOnlyHint)
        {
            return "file_transfer_pressure";
        }

        if (fileTransferDegradedHint)
        {
            return "file_transfer";
        }

        if (hasSevereHealthDegradation && hasActionableHealthDegradation)
        {
            return "bridge_health";
        }

        if (hasSevereLaneCongestion || hasLaneCongestion)
        {
            return oldestQueuedAgeMs > 0 ? "bridge_queue" : "lane_congestion";
        }

        if (hasQueuePressure)
        {
            return "queue_pressure";
        }

        if (hasCatchUpPressure)
        {
            return "catch_up_pressure";
        }

        if (hasSenderPressure)
        {
            return "capture_age";
        }

        return "recovered";
    }

    public static ScreenShareTransportTuningLevel ResolveSenderTuningLevel(ScreenShareSenderFreshnessMode mode)
        => mode switch
        {
            ScreenShareSenderFreshnessMode.Reduced => ScreenShareTransportTuningLevel.BandwidthReduced,
            ScreenShareSenderFreshnessMode.CatchUp => ScreenShareTransportTuningLevel.BandwidthReduced,
            _ => ScreenShareTransportTuningLevel.Normal,
        };

    public static bool CanRecoveryLockAllowModeTransition(
        ScreenShareSenderFreshnessMode currentMode,
        ScreenShareSenderFreshnessMode nextMode)
    {
        if (currentMode == nextMode)
        {
            return false;
        }

        if (ResolveSenderTuningLevel(currentMode) != ResolveSenderTuningLevel(nextMode))
        {
            return false;
        }

        return (currentMode == ScreenShareSenderFreshnessMode.Reduced ||
                currentMode == ScreenShareSenderFreshnessMode.CatchUp) &&
               (nextMode == ScreenShareSenderFreshnessMode.Reduced ||
                nextMode == ScreenShareSenderFreshnessMode.CatchUp);
    }
}
