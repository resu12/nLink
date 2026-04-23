using System;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal enum ScreenShareSenderOperatingState
{
    Normal = 0,
    Reduced = 1,
    CatchUp = 2,
}

internal enum ScreenShareSenderGuardState
{
    None = 0,
    RecoveryLocked = 1,
    PostAckGrace = 2,
    BootstrapGrace = 3,
    TransitionGrace = 4,
}

internal readonly record struct ScreenShareSenderPressureSnapshot(
    bool LocalCaptureAgePressure,
    bool RemoteHighFrameAgePressure,
    bool ImmediateReducedPressure,
    bool QueueOrLanePressure,
    bool BridgeHealthPressure,
    bool HelperProgressProofSatisfied,
    bool CurrentEpochRecoveryActive,
    bool EncodeBudgetPressure);

internal enum HelperRemoteSessionPhase
{
    NoVisibleBaseline = 0,
    Recovering = 1,
    VisibleStable = 2,
    Stalled = 3,
}

internal enum HelperRemoteRecoveryMechanism
{
    None = 0,
    WaitingForRecoveryKeyframe = 1,
    ReservedApply = 2,
    FollowerWindow = 3,
    RecoveryCorridor = 4,
    RunwayCleanup = 5,
}

internal readonly record struct HelperRemoteSessionSnapshot(
    long CurrentEpoch,
    HelperRemoteSessionPhase Phase,
    HelperRemoteRecoveryMechanism RecoveryMechanism,
    bool BaselineEstablished,
    bool SteadyVisibleProgressActive,
    long VisibleHeadFrameId,
    long AppliedHeadFrameId,
    long StableVisibleHeadFrameId,
    long VisibleRecoveryFloorFrameId,
    long ProvenHeadFrameId,
    long FramesAppliedSinceLastGap,
    bool CurrentEpochProgressProven,
    string CurrentEpochProgressProofSource,
    bool RecoveryActive,
    bool RecoveryCorridorActive,
    bool RunwayCleanupActive,
    bool PostRecoveryStabilizationActive);

internal enum ScreenShareLossClass
{
    CurrentEpochActionableLoss = 0,
    SameEpochRecoverySuppressed = 1,
    OlderEpochCleanup = 2,
    BenignStaleCleanup = 3,
}

internal enum ScreenShareOperationalTroubleDomain
{
    None = 0,
    Sender = 1,
    Helper = 2,
    Transport = 3,
}

internal readonly record struct ScreenShareOperationalHealthSnapshot(
    ScreenShareSenderOperatingState SenderOperatingState,
    ScreenShareSenderGuardState SenderGuardState,
    HelperRemoteSessionPhase HelperSessionPhase,
    HelperRemoteRecoveryMechanism HelperRecoveryMechanism,
    ScreenShareLossClass DominantLossClass,
    string DominantPressureBlocker,
    ScreenShareOperationalTroubleDomain DominantTroubleDomain,
    bool RecoveryActive,
    bool BaselineEstablished,
    bool SteadyVisibleProgressActive)
{
    public string ToLogMessage()
    {
        return
            $"event=screenshare_health_snapshot; sender_operating_state={ScreenShareConceptualModelFormatter.FormatSenderOperatingState(SenderOperatingState)}; " +
            $"sender_guard_state={ScreenShareConceptualModelFormatter.FormatSenderGuardState(SenderGuardState)}; " +
            $"helper_session_phase={ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(HelperSessionPhase)}; " +
            $"helper_recovery_mechanism={ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(HelperRecoveryMechanism)}; " +
            $"dominant_loss_class={ScreenShareConceptualModelFormatter.FormatLossClass(DominantLossClass)}; " +
            $"dominant_pressure_blocker={ScreenShareConceptualModelFormatter.FormatValue(DominantPressureBlocker, "none")}; " +
            $"dominant_trouble_domain={ScreenShareConceptualModelFormatter.FormatTroubleDomain(DominantTroubleDomain)}; " +
            $"recovery_active={(RecoveryActive ? 1 : 0)}; " +
            $"baseline_established={(BaselineEstablished ? 1 : 0)}; " +
            $"steady_visible_progress_active={(SteadyVisibleProgressActive ? 1 : 0)}";
    }
}

internal static class ScreenShareConceptualModelFormatter
{
    public static string FormatSenderOperatingState(ScreenShareSenderOperatingState value)
        => value switch
        {
            ScreenShareSenderOperatingState.Reduced => "reduced",
            ScreenShareSenderOperatingState.CatchUp => "catch_up",
            _ => "normal",
        };

    public static string FormatSenderGuardState(ScreenShareSenderGuardState value)
        => value switch
        {
            ScreenShareSenderGuardState.RecoveryLocked => "recovery_locked",
            ScreenShareSenderGuardState.PostAckGrace => "post_ack_grace",
            ScreenShareSenderGuardState.BootstrapGrace => "bootstrap_grace",
            ScreenShareSenderGuardState.TransitionGrace => "transition_grace",
            _ => "none",
        };

    public static string FormatHelperSessionPhase(HelperRemoteSessionPhase value)
        => value switch
        {
            HelperRemoteSessionPhase.NoVisibleBaseline => "no_visible_baseline",
            HelperRemoteSessionPhase.Recovering => "recovering",
            HelperRemoteSessionPhase.Stalled => "stalled",
            _ => "visible_stable",
        };

    public static string FormatHelperRecoveryMechanism(HelperRemoteRecoveryMechanism value)
        => value switch
        {
            HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe => "waiting_for_recovery_keyframe",
            HelperRemoteRecoveryMechanism.ReservedApply => "reserved_apply",
            HelperRemoteRecoveryMechanism.FollowerWindow => "follower_window",
            HelperRemoteRecoveryMechanism.RecoveryCorridor => "recovery_corridor",
            HelperRemoteRecoveryMechanism.RunwayCleanup => "runway_cleanup",
            _ => "none",
        };

    public static string FormatLossClass(ScreenShareLossClass value)
        => value switch
        {
            ScreenShareLossClass.CurrentEpochActionableLoss => "current_epoch_actionable_loss",
            ScreenShareLossClass.SameEpochRecoverySuppressed => "same_epoch_recovery_suppressed",
            ScreenShareLossClass.OlderEpochCleanup => "older_epoch_cleanup",
            _ => "benign_stale_cleanup",
        };

    public static string FormatTroubleDomain(ScreenShareOperationalTroubleDomain value)
        => value switch
        {
            ScreenShareOperationalTroubleDomain.Sender => "sender",
            ScreenShareOperationalTroubleDomain.Helper => "helper",
            ScreenShareOperationalTroubleDomain.Transport => "transport",
            _ => "none",
        };

    public static string FormatValue(string? value, string defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}

internal static class ScreenShareLossTaxonomyMapper
{
    public static ScreenShareLossClass ClassifySession(
        ScreenShareFrameLossSessionSnapshot snapshot,
        long softStaleCleanupCount = 0,
        long staleSupersededRecoverySuppressedCount = 0,
        long postRecoveryStaleDropBypassCount = 0)
    {
        var currentEpochActionableLoss =
            Math.Max(0, snapshot.ReassemblerLossCount) +
            Math.Max(0, snapshot.DecodeFailedLossCount) +
            Math.Max(0, snapshot.UnattributedLossCount) +
            Math.Max(0, snapshot.LateFragmentAfterAppliedHeadCount) +
            Math.Max(0, snapshot.LateFragmentAfterVisibleRecoveryCount);
        if (currentEpochActionableLoss > 0)
        {
            return ScreenShareLossClass.CurrentEpochActionableLoss;
        }

        var sameEpochRecoverySuppressed =
            Math.Max(0, snapshot.WaitingForRecoveryKeyframeRejectCount) +
            Math.Max(0, snapshot.RecoveryRunwayOverflowRejectCount) +
            Math.Max(0, snapshot.SuppressedEmitDuringRecoveryWaitCount) +
            Math.Max(0, snapshot.BlockedByReservedRecoveryFrameRejectCount) +
            Math.Max(0, snapshot.DeferredPostRecoveryCandidateReplaceCount) +
            Math.Max(0, snapshot.PreCandidateGapTailRejectedCount) +
            Math.Max(0, snapshot.FutureTailQuarantinedDuringGapCount) +
            Math.Max(0, snapshot.FutureTailQuarantinedAfterGapCount) +
            Math.Max(0, snapshot.RecoveryKeyframeSupersededOrReplacedCount);
        if (sameEpochRecoverySuppressed > 0)
        {
            return ScreenShareLossClass.SameEpochRecoverySuppressed;
        }

        if (snapshot.OlderEpochCleanupAfterEpochAdvanceCount > 0)
        {
            return ScreenShareLossClass.OlderEpochCleanup;
        }

        var benignStaleCleanup =
            Math.Max(0, snapshot.SupersededRecoveryTailCleanupCount) +
            Math.Max(0, snapshot.LateFragmentAfterSuccessfulRecoveryCount) +
            Math.Max(0, snapshot.LateFragmentAfterStableVisibleHeadCount) +
            Math.Max(0, softStaleCleanupCount) +
            Math.Max(0, staleSupersededRecoverySuppressedCount) +
            Math.Max(0, postRecoveryStaleDropBypassCount);
        return benignStaleCleanup > 0
            ? ScreenShareLossClass.BenignStaleCleanup
            : ScreenShareLossClass.BenignStaleCleanup;
    }

    public static ScreenShareLossClass ClassifyEpoch(ScreenShareEpochDiagnosticsSnapshot epochDiagnostics)
    {
        var currentEpochActionableLoss =
            Math.Max(0, epochDiagnostics.FragmentGapBeforeAssemblyCount) +
            Math.Max(0, epochDiagnostics.LateFragmentAfterAppliedHeadCount) +
            Math.Max(0, epochDiagnostics.LateFragmentAfterVisibleRecoveryCount);
        if (currentEpochActionableLoss > 0)
        {
            return ScreenShareLossClass.CurrentEpochActionableLoss;
        }

        var sameEpochRecoverySuppressed =
            Math.Max(0, epochDiagnostics.SuppressedEmitDuringRecoveryWaitCount) +
            Math.Max(0, epochDiagnostics.FutureTailPrunedWhileGapActiveCount) +
            Math.Max(0, epochDiagnostics.RecoveryKeyframeSupersededOrReplacedCount);
        if (sameEpochRecoverySuppressed > 0)
        {
            return ScreenShareLossClass.SameEpochRecoverySuppressed;
        }

        if (epochDiagnostics.OlderEpochCleanupAfterEpochAdvanceCount > 0)
        {
            return ScreenShareLossClass.OlderEpochCleanup;
        }

        return ScreenShareLossClass.BenignStaleCleanup;
    }
}

internal static class ScreenShareOperationalHealthSnapshotBuilder
{
    public static ScreenShareOperationalHealthSnapshot Build(
        ScreenShareMetrics senderMetrics,
        ScreenShareMetrics viewerMetrics)
    {
        var senderOperatingState = ParseSenderOperatingState(senderMetrics.SenderOperatingState, senderMetrics.FreshnessMode);
        var senderGuardState = ParseSenderGuardState(senderMetrics.SenderGuardState);
        var helperRecoveryMechanism = ResolveHelperRecoveryMechanism(viewerMetrics);
        var helperSessionPhase = ResolveHelperSessionPhase(viewerMetrics, helperRecoveryMechanism);
        var dominantLossClass = ResolveLossClass(viewerMetrics);
        var dominantPressureBlocker = ScreenShareConceptualModelFormatter.FormatValue(senderMetrics.DominantPressureBlocker, "none");
        var dominantTroubleDomain = DetermineTroubleDomain(
            senderOperatingState,
            senderGuardState,
            helperSessionPhase,
            dominantLossClass,
            dominantPressureBlocker);

        return new ScreenShareOperationalHealthSnapshot(
            senderOperatingState,
            senderGuardState,
            helperSessionPhase,
            helperRecoveryMechanism,
            dominantLossClass,
            dominantPressureBlocker,
            dominantTroubleDomain,
            senderMetrics.RecoveryActive || viewerMetrics.RecoveryActive,
            viewerMetrics.BaselineEstablished,
            viewerMetrics.SteadyVisibleProgressActive);
    }

    public static ScreenShareOperationalHealthSnapshot BuildFromSenderSummary(
        ScreenShareSenderOperatingState senderOperatingState,
        ScreenShareSenderGuardState senderGuardState,
        string dominantPressureBlocker,
        bool recoveryActive,
        bool helperSteadyVisibleProgressActive,
        bool helperFactHealthyActive,
        long helperVisibleHeadFrameId,
        long helperStableVisibleHeadFrameId,
        long helperVisibleRecoveryFloorFrameId,
        long helperRecoveryKeyframeApplyCount)
    {
        _ = helperRecoveryKeyframeApplyCount;
        var baselineEstablished =
            helperVisibleHeadFrameId >= 0 ||
            helperStableVisibleHeadFrameId >= 0 ||
            helperVisibleRecoveryFloorFrameId >= 0 ||
            helperFactHealthyActive ||
            helperSteadyVisibleProgressActive;
        var steadyVisibleProgressActive = helperSteadyVisibleProgressActive || helperFactHealthyActive;
        var helperRecoveryMechanism =
            recoveryActive
                ? baselineEstablished
                    ? HelperRemoteRecoveryMechanism.RecoveryCorridor
                    : HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe
                : HelperRemoteRecoveryMechanism.None;
        var helperSessionPhase =
            !baselineEstablished && helperRecoveryMechanism == HelperRemoteRecoveryMechanism.None
                ? HelperRemoteSessionPhase.NoVisibleBaseline
                : helperRecoveryMechanism != HelperRemoteRecoveryMechanism.None
                    ? HelperRemoteSessionPhase.Recovering
                    : HelperRemoteSessionPhase.VisibleStable;
        var dominantLossClass = ScreenShareLossClass.BenignStaleCleanup;
        var dominantTroubleDomain = DetermineTroubleDomain(
            senderOperatingState,
            senderGuardState,
            helperSessionPhase,
            dominantLossClass,
            ScreenShareConceptualModelFormatter.FormatValue(dominantPressureBlocker, "none"));

        return new ScreenShareOperationalHealthSnapshot(
            SenderOperatingState: senderOperatingState,
            SenderGuardState: senderGuardState,
            HelperSessionPhase: helperSessionPhase,
            HelperRecoveryMechanism: helperRecoveryMechanism,
            DominantLossClass: dominantLossClass,
            DominantPressureBlocker: ScreenShareConceptualModelFormatter.FormatValue(dominantPressureBlocker, "none"),
            DominantTroubleDomain: dominantTroubleDomain,
            RecoveryActive: recoveryActive,
            BaselineEstablished: baselineEstablished,
            SteadyVisibleProgressActive: steadyVisibleProgressActive);
    }

    private static ScreenShareOperationalTroubleDomain DetermineTroubleDomain(
        ScreenShareSenderOperatingState senderOperatingState,
        ScreenShareSenderGuardState senderGuardState,
        HelperRemoteSessionPhase helperSessionPhase,
        ScreenShareLossClass dominantLossClass,
        string dominantPressureBlocker)
    {
        if (helperSessionPhase == HelperRemoteSessionPhase.Recovering ||
            helperSessionPhase == HelperRemoteSessionPhase.Stalled ||
            dominantLossClass == ScreenShareLossClass.CurrentEpochActionableLoss)
        {
            return ScreenShareOperationalTroubleDomain.Helper;
        }

        if (string.Equals(dominantPressureBlocker, "bridge_health", StringComparison.Ordinal) ||
            string.Equals(dominantPressureBlocker, "queue_evict", StringComparison.Ordinal) ||
            string.Equals(dominantPressureBlocker, "rate_gate", StringComparison.Ordinal))
        {
            return ScreenShareOperationalTroubleDomain.Transport;
        }

        if (senderGuardState != ScreenShareSenderGuardState.None ||
            senderOperatingState != ScreenShareSenderOperatingState.Normal)
        {
            return ScreenShareOperationalTroubleDomain.Sender;
        }

        return ScreenShareOperationalTroubleDomain.None;
    }

    private static ScreenShareSenderOperatingState ParseSenderOperatingState(string? senderOperatingState, string? freshnessMode)
    {
        var value = string.IsNullOrWhiteSpace(senderOperatingState) ? freshnessMode : senderOperatingState;
        return value?.Trim() switch
        {
            "reduced" => ScreenShareSenderOperatingState.Reduced,
            "catch_up" => ScreenShareSenderOperatingState.CatchUp,
            _ => ScreenShareSenderOperatingState.Normal,
        };
    }

    private static ScreenShareSenderGuardState ParseSenderGuardState(string? senderGuardState)
        => senderGuardState?.Trim() switch
        {
            "recovery_locked" => ScreenShareSenderGuardState.RecoveryLocked,
            "post_ack_grace" => ScreenShareSenderGuardState.PostAckGrace,
            "bootstrap_grace" => ScreenShareSenderGuardState.BootstrapGrace,
            "transition_grace" => ScreenShareSenderGuardState.TransitionGrace,
            _ => ScreenShareSenderGuardState.None,
        };

    private static HelperRemoteSessionPhase ParseHelperSessionPhase(string? helperSessionPhase)
        => helperSessionPhase?.Trim() switch
        {
            "recovering" => HelperRemoteSessionPhase.Recovering,
            "visible_stable" => HelperRemoteSessionPhase.VisibleStable,
            "stalled" => HelperRemoteSessionPhase.Stalled,
            _ => HelperRemoteSessionPhase.NoVisibleBaseline,
        };

    private static HelperRemoteRecoveryMechanism ParseHelperRecoveryMechanism(string? helperRecoveryMechanism)
        => helperRecoveryMechanism?.Trim() switch
        {
            "waiting_for_recovery_keyframe" => HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe,
            "reserved_apply" => HelperRemoteRecoveryMechanism.ReservedApply,
            "follower_window" => HelperRemoteRecoveryMechanism.FollowerWindow,
            "recovery_corridor" => HelperRemoteRecoveryMechanism.RecoveryCorridor,
            "runway_cleanup" => HelperRemoteRecoveryMechanism.RunwayCleanup,
            _ => HelperRemoteRecoveryMechanism.None,
        };

    private static ScreenShareLossClass ParseLossClass(string? dominantLossClass)
        => dominantLossClass?.Trim() switch
        {
            "current_epoch_actionable_loss" => ScreenShareLossClass.CurrentEpochActionableLoss,
            "same_epoch_recovery_suppressed" => ScreenShareLossClass.SameEpochRecoverySuppressed,
            "older_epoch_cleanup" => ScreenShareLossClass.OlderEpochCleanup,
            _ => ScreenShareLossClass.BenignStaleCleanup,
        };

    private static HelperRemoteRecoveryMechanism ResolveHelperRecoveryMechanism(ScreenShareMetrics viewerMetrics)
    {
        var parsed = ParseHelperRecoveryMechanism(viewerMetrics.HelperRecoveryMechanism);
        if (parsed != HelperRemoteRecoveryMechanism.None)
        {
            return parsed;
        }

        if (viewerMetrics.RecoveryProgressCorridorCount > 0 || viewerMetrics.RecoveryWindowActive)
        {
            return HelperRemoteRecoveryMechanism.RecoveryCorridor;
        }

        if (viewerMetrics.RecoveryKeyframePendingVisibleApplyCount > 0)
        {
            return HelperRemoteRecoveryMechanism.ReservedApply;
        }

        if (viewerMetrics.RecoveryFollowerWindowBufferedCount > 0 ||
            viewerMetrics.RecoveryFollowerWindowAppliedCount > 0 ||
            viewerMetrics.RecoveryFollowerWindowTrimmedCount > 0)
        {
            return HelperRemoteRecoveryMechanism.FollowerWindow;
        }

        if (viewerMetrics.RecoveryRunwayContiguousFollowerBufferCount > 0 ||
            viewerMetrics.RecoveryRunwayContiguousFollowerApplyCount > 0 ||
            viewerMetrics.RecoveryRunwayAbortCount > 0)
        {
            return HelperRemoteRecoveryMechanism.RunwayCleanup;
        }

        if (viewerMetrics.RecoveryActive ||
            viewerMetrics.WaitingForRecoveryKeyframeRejectCount > 0 ||
            viewerMetrics.RecoveryWaitRejectBeforeRunwayCount > 0 ||
            viewerMetrics.SuppressedEmitDuringRecoveryWaitCount > 0)
        {
            return HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe;
        }

        return HelperRemoteRecoveryMechanism.None;
    }

    private static HelperRemoteSessionPhase ResolveHelperSessionPhase(
        ScreenShareMetrics viewerMetrics,
        HelperRemoteRecoveryMechanism helperRecoveryMechanism)
    {
        var parsed = ParseHelperSessionPhase(viewerMetrics.HelperSessionPhase);
        if (parsed == HelperRemoteSessionPhase.Recovering || parsed == HelperRemoteSessionPhase.Stalled)
        {
            return parsed;
        }

        if (viewerMetrics.RecoveryActive ||
            helperRecoveryMechanism != HelperRemoteRecoveryMechanism.None)
        {
            return HelperRemoteSessionPhase.Recovering;
        }

        if (parsed == HelperRemoteSessionPhase.VisibleStable)
        {
            return parsed;
        }

        if (viewerMetrics.BaselineEstablished ||
            viewerMetrics.VisibleHeadFrameId >= 0 ||
            viewerMetrics.StableVisibleHeadFrameId >= 0)
        {
            return HelperRemoteSessionPhase.VisibleStable;
        }

        return HelperRemoteSessionPhase.NoVisibleBaseline;
    }

    private static ScreenShareLossClass ResolveLossClass(ScreenShareMetrics viewerMetrics)
    {
        var parsed = ParseLossClass(viewerMetrics.DominantLossClass);
        if (parsed != ScreenShareLossClass.BenignStaleCleanup)
        {
            return parsed;
        }

        var currentEpochActionableLoss =
            Math.Max(0, viewerMetrics.ReassemblerLossCount) +
            Math.Max(0, viewerMetrics.LateFragmentAfterAppliedHeadCount) +
            Math.Max(0, viewerMetrics.LateFragmentAfterVisibleRecoveryCount) +
            Math.Max(0, viewerMetrics.UnattributedLossCount);
        if (currentEpochActionableLoss > 0)
        {
            return ScreenShareLossClass.CurrentEpochActionableLoss;
        }

        var sameEpochRecoverySuppressed =
            Math.Max(0, viewerMetrics.WaitingForRecoveryKeyframeRejectCount) +
            Math.Max(0, viewerMetrics.RecoveryWaitRejectBeforeRunwayCount) +
            Math.Max(0, viewerMetrics.RecoveryRunwayOverflowRejectCount) +
            Math.Max(0, viewerMetrics.SuppressedEmitDuringRecoveryWaitCount) +
            Math.Max(0, viewerMetrics.BlockedByReservedRecoveryFrameRejectCount) +
            Math.Max(0, viewerMetrics.DeferredPostRecoveryCandidateReplaceCount) +
            Math.Max(0, viewerMetrics.PreCandidateGapTailRejectedCount) +
            Math.Max(0, viewerMetrics.FutureTailQuarantinedDuringGapCount) +
            Math.Max(0, viewerMetrics.FutureTailQuarantinedAfterGapCount);
        if (sameEpochRecoverySuppressed > 0)
        {
            return ScreenShareLossClass.SameEpochRecoverySuppressed;
        }

        return parsed;
    }
}
