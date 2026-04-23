using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class HelperRemoteScreenShareSessionControllerTests
{
    [Fact]
    public void TryRejectFrameBeforeDecode_WaitingForRecoveryKeyframe_IncrementsGapCounters()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext
        {
            PreDecodeRejectionReason = "waiting_for_recovery_keyframe",
        };
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.RecoveryState.RecoveryActive = true;
        controller.RecoveryState.RecoveryReason = "frame_gap";
        controller.RecoveryState.RecoveryStreamEpoch = 5;

        var rejectionReason = controller.TryRejectFrameBeforeDecode(
            sessionId: "session-a",
            encoding: "h264",
            streamEpoch: 5,
            frameId: 11,
            isKeyFrame: false,
            recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.Normal);

        Assert.Equal("waiting_for_recovery_keyframe", rejectionReason);
        Assert.Equal(1, context.FramesDroppedWaitingForRecoveryKeyframeCount);
        Assert.Equal(1, context.PreCandidateGapTailEmittedCount);
        Assert.Equal(1, context.FramesDroppedForFrameGapCount);
        Assert.Equal("waiting_for_recovery_keyframe", context.LastObservedViewerRejectionReason);
    }

    [Fact]
    public void OnFrameAppliedVisible_ClearsReservedApply_AndCompletesPostRecoveryWindow()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.FollowerState.ReservedApplyActive = true;
        controller.FollowerState.ReservedApplyStreamEpoch = 8;
        controller.FollowerState.ReservedApplyFrameId = 22;
        controller.FollowerState.PostRecoveryStabilizationEpoch = 8;
        controller.FollowerState.PostRecoveryReservedAppliesRemaining = 1;

        controller.OnFrameAppliedVisible(new EncodedFrameDecodeRequest(
            Encoding: "h264",
            EncodedFrameBytes: new byte[] { 1 },
            IsKeyFrame: false,
            StreamEpoch: 8,
            FrameId: 22,
            SessionId: "session-b",
            RequiresReservedApply: false,
            BypassesAgeBudget: false,
            RecoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.Normal));

        Assert.False(controller.FollowerState.ReservedApplyActive);
        Assert.Equal(0, controller.FollowerState.PostRecoveryReservedAppliesRemaining);
        Assert.Equal(0, controller.FollowerState.PostRecoveryStabilizationEpoch);
    }

    [Fact]
    public void ClearReservedApplyIfMatch_RecordsReservedApplyHoldDuration()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.SetReservedApplyPending(
            streamEpoch: 8,
            frameId: 22,
            startupKeyframePendingVisibleApply: true,
            nowUtc: DateTimeOffset.UtcNow.AddMilliseconds(-75));

        var result = controller.ClearReservedApplyIfMatch(new EncodedFrameDecodeRequest(
            Encoding: "h264",
            EncodedFrameBytes: new byte[] { 1 },
            IsKeyFrame: true,
            StreamEpoch: 8,
            FrameId: 22,
            SessionId: "session-b",
            RequiresReservedApply: false,
            BypassesAgeBudget: false,
            RecoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner));

        Assert.True(result.Cleared);
        Assert.True(result.HoldMs > 0);
        Assert.Equal(result.HoldMs, controller.VisibleProgressState.LastReservedApplyHoldMs);
        Assert.False(controller.FollowerState.ReservedApplyActive);
        Assert.False(controller.FollowerState.StartupKeyframePendingVisibleApplyActive);
    }

    [Fact]
    public void ObserveRecoveryProgressCorridorApply_RecordsHoldDuration_AndResetsOnSuccess()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.StartRecoveryProgressCorridor(
            streamEpoch: 9,
            frameId: 100,
            nowUtc: DateTimeOffset.UtcNow.AddMilliseconds(-90));

        var result = controller.ObserveRecoveryProgressCorridorApply(
            streamEpoch: 9,
            frameId: 101,
            nowUtc: DateTimeOffset.UtcNow,
            requiredContiguousFollowerApplyCount: 1);

        Assert.True(result.Applied);
        Assert.True(result.Succeeded);
        Assert.True(result.HoldMs > 0);
        Assert.Equal(result.HoldMs, controller.VisibleProgressState.LastRecoveryProgressCorridorHoldMs);
        Assert.False(controller.FollowerState.RecoveryProgressCorridorActive);
    }

    [Fact]
    public void BuildVisibleApplyProgress_UsesStableVisibleHeadSnapshot()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        for (var frameId = 10; frameId <= 13; frameId++)
        {
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
                "session-c",
                streamEpoch: 4,
                frameId: frameId,
                isKeyFrame: frameId == 10);
        }

        var context = new FakeHelperRemoteScreenShareSessionContext
        {
            EffectiveSessionId = "session-c",
        };
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.RecoveryState.VisibleHeadStreamEpoch = 4;
        controller.RecoveryState.VisibleHeadFrameId = 13;

        var progress = controller.BuildVisibleApplyProgress(new EncodedFrameDecodeRequest(
            Encoding: "h264",
            EncodedFrameBytes: new byte[] { 1 },
            IsKeyFrame: false,
            StreamEpoch: 4,
            FrameId: 13,
            SessionId: "session-c",
            RequiresReservedApply: false,
            BypassesAgeBudget: false,
            RecoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.Normal));

        Assert.Equal(13, progress.VisibleHeadFrameId);
        Assert.Equal(13, progress.StableVisibleHeadFrameId);
        Assert.Equal(4, progress.FramesAppliedSinceLastGap);
        Assert.Equal(13, progress.AppliedHeadFrameId);
    }

    [Fact]
    public void ShouldForceRecoveryOnce_TriggersOnlyAfterThreshold()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext
        {
            FramesApplied = 5,
            ForcedHelperRemoteRecoveryAfterApplies = 5,
        };
        var controller = new HelperRemoteScreenShareSessionController(context);

        Assert.True(controller.ShouldForceRecoveryOnce(streamEpoch: 7, frameId: 31));
        Assert.False(controller.ShouldForceRecoveryOnce(streamEpoch: 7, frameId: 32));
        Assert.True(context.ForcedHelperRemoteRecoveryTriggered);
        Assert.Contains("screenshare_forced_helper_remote_recovery_triggered", context.LastScreenShareInfoLog);
    }

    [Fact]
    public void BuildSessionSnapshot_ReportsRecoveringReservedApplyState()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.FollowerState.ReservedApplyActive = true;
        controller.RecoveryState.RecoveryActive = true;

        var snapshot = controller.BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot.Empty with
        {
            StableVisibleHeadFrameId = -1,
            VisibleRecoveryFloorFrameId = -1,
        });

        Assert.Equal(HelperRemoteSessionPhase.Recovering, snapshot.Phase);
        Assert.Equal(HelperRemoteRecoveryMechanism.ReservedApply, snapshot.RecoveryMechanism);
        Assert.False(snapshot.BaselineEstablished);
        Assert.False(snapshot.SteadyVisibleProgressActive);
    }

    [Fact]
    public void BuildSessionSnapshot_ReportsVisibleStableWhenBaselineExistsWithoutRecovery()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.RecoveryState.VisibleHeadFrameId = 42;

        var snapshot = controller.BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot.Empty with
        {
            StableVisibleHeadFrameId = 42,
            VisibleRecoveryFloorFrameId = 40,
        });

        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.Phase);
        Assert.Equal(HelperRemoteRecoveryMechanism.None, snapshot.RecoveryMechanism);
        Assert.True(snapshot.BaselineEstablished);
        Assert.True(snapshot.SteadyVisibleProgressActive);
    }

    [Fact]
    public void BuildSessionSnapshot_CurrentEpochAppliedHeadProof_ReportsVisibleStable()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);

        var snapshot = controller.BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot.Empty with
        {
            EpochDiagnostics = new[]
            {
                CreateEpochDiagnosticsSnapshot(
                    streamEpoch: 5,
                    visibleHeadFrameId: -1,
                    appliedHeadFrameId: 20,
                    stableVisibleHeadFrameId: 20,
                    framesAppliedSinceLastGap: 3),
            },
        });

        Assert.Equal(5, snapshot.CurrentEpoch);
        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.Phase);
        Assert.Equal(HelperRemoteRecoveryMechanism.None, snapshot.RecoveryMechanism);
        Assert.True(snapshot.BaselineEstablished);
        Assert.True(snapshot.SteadyVisibleProgressActive);
        Assert.True(snapshot.CurrentEpochProgressProven);
        Assert.Equal("stable_visible_head", snapshot.CurrentEpochProgressProofSource);
        Assert.Equal(20, snapshot.ProvenHeadFrameId);
        Assert.Equal(3, snapshot.FramesAppliedSinceLastGap);
    }

    [Fact]
    public void BuildSessionSnapshot_ActiveRecoveryCorridor_ReportsRecovering()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.StartRecoveryProgressCorridor(
            streamEpoch: 9,
            frameId: 100,
            nowUtc: DateTimeOffset.UtcNow.AddMilliseconds(-90));

        var snapshot = controller.BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot.Empty with
        {
            EpochDiagnostics = new[]
            {
                CreateEpochDiagnosticsSnapshot(
                    streamEpoch: 9,
                    visibleHeadFrameId: 101,
                    appliedHeadFrameId: 101,
                    stableVisibleHeadFrameId: 101,
                    framesAppliedSinceLastGap: 2),
            },
        });

        Assert.Equal(9, snapshot.CurrentEpoch);
        Assert.Equal(HelperRemoteSessionPhase.Recovering, snapshot.Phase);
        Assert.Equal(HelperRemoteRecoveryMechanism.RecoveryCorridor, snapshot.RecoveryMechanism);
        Assert.True(snapshot.CurrentEpochProgressProven);
        Assert.True(snapshot.RecoveryCorridorActive);
        Assert.False(snapshot.SteadyVisibleProgressActive);
    }

    [Fact]
    public void BuildSessionSnapshot_OlderEpochVisibleHead_DoesNotSuppressCurrentEpochStableClassification()
    {
        var context = new FakeHelperRemoteScreenShareSessionContext();
        var controller = new HelperRemoteScreenShareSessionController(context);
        controller.RecoveryState.VisibleHeadStreamEpoch = 4;
        controller.RecoveryState.VisibleHeadFrameId = 99;

        var snapshot = controller.BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot.Empty with
        {
            EpochDiagnostics = new[]
            {
                CreateEpochDiagnosticsSnapshot(
                    streamEpoch: 5,
                    visibleHeadFrameId: -1,
                    appliedHeadFrameId: 21,
                    stableVisibleHeadFrameId: 21,
                    framesAppliedSinceLastGap: 4),
            },
        });

        Assert.Equal(5, snapshot.CurrentEpoch);
        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, snapshot.Phase);
        Assert.Equal(-1, snapshot.VisibleHeadFrameId);
        Assert.Equal(21, snapshot.AppliedHeadFrameId);
        Assert.Equal(21, snapshot.StableVisibleHeadFrameId);
        Assert.Equal(21, snapshot.ProvenHeadFrameId);
        Assert.True(snapshot.CurrentEpochProgressProven);
        Assert.True(snapshot.SteadyVisibleProgressActive);
    }

    private static ScreenShareEpochDiagnosticsSnapshot CreateEpochDiagnosticsSnapshot(
        long streamEpoch,
        long visibleHeadFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        long visibleRecoveryFloorFrameId = -1)
    {
        return new ScreenShareEpochDiagnosticsSnapshot(
            StreamEpoch: streamEpoch,
            LastAppliedFrameId: appliedHeadFrameId,
            VisibleHeadFrameId: visibleHeadFrameId,
            AppliedHeadFrameId: appliedHeadFrameId,
            OrderedEmitHeadFrameId: -1,
            WinningRecoveryFrameId: -1,
            GapCount: 0,
            RecoveryKeyframeApplyCount: 0,
            ResyncCount: 0,
            FramesAppliedSinceLastGap: framesAppliedSinceLastGap,
            TimeToFirstApplyMs: 0,
            TimeFromGapToKeyframeRequestMs: 0,
            TimeFromGapToRecoveryKeyframeAppliedMs: 0,
            TimeInRecoveryLockMs: 0,
            RecoveryCandidatePresentCount: 0,
            VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            StableVisibleHeadFrameId: stableVisibleHeadFrameId,
            FragmentGapBeforeAssemblyCount: 0,
            LateFragmentAfterHeadAdvancedCount: 0,
            LateFragmentAfterAppliedHeadCount: 0,
            LateFragmentAfterOrderedHeadCount: 0,
            SupersededRecoveryTailCleanupCount: 0,
            RecoveryOwnerReplacedCount: 0,
            OlderEpochCleanupAfterEpochAdvanceCount: 0,
            LateFragmentAfterStableVisibleHeadCount: 0,
            LateFragmentAfterVisibleRecoveryCount: 0,
            LateFragmentAfterSuccessfulRecoveryCount: 0,
            SuppressedEmitDuringRecoveryWaitCount: 0,
            FutureTailPrunedWhileGapActiveCount: 0,
            ProtectedHeadMissingBudgetPressureCount: 0,
            RecoveryKeyframeSupersededOrReplacedCount: 0,
            OrderedEmitBlockedThenResyncedCount: 0,
            DominantReassemblerRootCause: "none",
            TimelineEvents: Array.Empty<ScreenShareEpochContinuityEventSnapshot>(),
            TopLossBursts: Array.Empty<ScreenShareReassemblerLossBurstSnapshot>());
    }

    private sealed class FakeHelperRemoteScreenShareSessionContext : IHelperRemoteScreenShareSessionContext
    {
        public string? PreDecodeRejectionReason { get; set; }

        public int FramesDroppedWaitingForRecoveryKeyframeCount { get; private set; }

        public int PreCandidateGapTailEmittedCount { get; private set; }

        public int FramesDroppedForFrameGapCount { get; private set; }

        public string LastObservedViewerRejectionReason { get; private set; } = string.Empty;

        public string EffectiveSessionId { get; set; } = string.Empty;

        public long FramesApplied { get; set; }

        public long ForcedHelperRemoteRecoveryAfterApplies { get; set; } = -1;

        public bool ForcedHelperRemoteRecoveryTriggered { get; set; }

        public string LogRole { get; set; } = "helper_remote";

        public string LastScreenShareInfoLog { get; private set; } = string.Empty;

        long IHelperRemoteScreenShareSessionContext.FramesApplied => FramesApplied;

        long IHelperRemoteScreenShareSessionContext.ForcedHelperRemoteRecoveryAfterApplies => ForcedHelperRemoteRecoveryAfterApplies;

        bool IHelperRemoteScreenShareSessionContext.ForcedHelperRemoteRecoveryTriggered
        {
            get => ForcedHelperRemoteRecoveryTriggered;
            set => ForcedHelperRemoteRecoveryTriggered = value;
        }

        string IHelperRemoteScreenShareSessionContext.LogRole => LogRole;

        public bool IsHelperRemoteH264(string encoding)
        {
            return string.Equals(encoding, "h264", StringComparison.Ordinal);
        }

        public string? ResolveHelperRemotePreDecodeRejectionReason(string? sessionId, long streamEpoch, long frameId, bool isKeyFrame, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
        {
            _ = sessionId;
            _ = streamEpoch;
            _ = frameId;
            _ = isKeyFrame;
            _ = recoveryDeliveryClass;
            return PreDecodeRejectionReason;
        }

        public void IncrementFramesDroppedWaitingForRecoveryKeyframe()
        {
            FramesDroppedWaitingForRecoveryKeyframeCount++;
        }

        public void IncrementPreCandidateGapTailEmittedToViewerCount()
        {
            PreCandidateGapTailEmittedCount++;
        }

        public void IncrementFramesDroppedForFrameGap()
        {
            FramesDroppedForFrameGapCount++;
        }

        public void ObserveViewerRejectedBeforeEnqueue(string sessionId, string encoding, long streamEpoch, long frameId, bool isKeyFrame, string reason)
        {
            _ = sessionId;
            _ = encoding;
            _ = streamEpoch;
            _ = frameId;
            _ = isKeyFrame;
            LastObservedViewerRejectionReason = reason;
        }

        public string GetEffectiveHelperRemoteSessionId(string? sessionId)
        {
            return string.IsNullOrWhiteSpace(sessionId) ? EffectiveSessionId : sessionId;
        }

        public void LogScreenShareInfo(string message)
        {
            LastScreenShareInfoLog = message;
        }
    }
}
