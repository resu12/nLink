using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareMotionIntegrityGuardTests
{
    [Fact]
    public void Evaluate_HighSampledMotion_EntersBurstAndForcesKeyFrame()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var first = CreateNv12Frame(160, 90, 24);
        var second = CreateNv12Frame(160, 90, 224);
        var third = CreateNv12Frame(160, 90, 24);

        Assert.False(guard.Evaluate(first, 160, 90, 0, enabled: true, externalKeyFrameRequested: false).ShouldForceKeyFrame);
        Assert.False(guard.Evaluate(second, 160, 90, 125, enabled: true, externalKeyFrameRequested: false).ShouldForceKeyFrame);
        var decision = guard.Evaluate(third, 160, 90, 250, enabled: true, externalKeyFrameRequested: false);

        Assert.True(decision.ShouldForceKeyFrame);
        Assert.True(decision.Snapshot.Active);
        Assert.Equal(1, decision.Snapshot.BurstEnterCount);
        Assert.Equal(1, decision.Snapshot.ForcedKeyFrameCount);
        Assert.True(decision.Snapshot.SampledMotionRatio > ScreenShareMotionIntegrityGuard.MotionBurstThresholdRatio);
    }

    [Fact]
    public void Evaluate_ScrollBandMotion_EntersBurstAndForcesKeyFrameImmediately()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var first = CreateNv12Frame(160, 90, 80);
        var scroll = CreateNv12Frame(160, 90, 80);
        FillLumaRegion(scroll, 160, x: 0, y: 20, width: 160, height: 30, value: 230);

        guard.Evaluate(first, 160, 90, 0, enabled: true, externalKeyFrameRequested: false);
        var decision = guard.Evaluate(scroll, 160, 90, 125, enabled: true, externalKeyFrameRequested: false);

        Assert.True(decision.ShouldForceKeyFrame);
        Assert.True(decision.Snapshot.Active);
        Assert.Equal(1, decision.Snapshot.BurstEnterCount);
        Assert.Equal(1, decision.Snapshot.ForcedKeyFrameCount);
        Assert.True(decision.Snapshot.ScrollMotionActiveBandCount >= ScreenShareMotionIntegrityGuard.StrongScrollMotionMinimumActiveBands);
        Assert.True(decision.Snapshot.ScrollMotionPeakBandRatio >= ScreenShareMotionIntegrityGuard.ScrollBandMotionThresholdRatio);
        Assert.Equal(1, decision.Snapshot.ScrollTriggerCount);
        Assert.Equal(1, decision.Snapshot.HighMotionFrameCount);
        Assert.Equal("strong_scroll_motion", decision.Snapshot.LastTriggerKind);
        Assert.Equal("motion_keyframe_due", decision.Snapshot.LastReason);
    }

    [Fact]
    public void Evaluate_LowMotionAndCursorSizedMotion_DoNotEnterBurst()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var first = CreateNv12Frame(160, 90, 80);
        var second = CreateNv12Frame(160, 90, 82);
        var cursorMove = CreateNv12Frame(160, 90, 82);
        FillLumaRegion(cursorMove, 160, x: 72, y: 40, width: 8, height: 8, value: 230);

        guard.Evaluate(first, 160, 90, 0, enabled: true, externalKeyFrameRequested: false);
        var lowMotionDecision = guard.Evaluate(second, 160, 90, 125, enabled: true, externalKeyFrameRequested: false);
        var cursorDecision = guard.Evaluate(cursorMove, 160, 90, 250, enabled: true, externalKeyFrameRequested: false);

        Assert.False(lowMotionDecision.ShouldForceKeyFrame);
        Assert.False(lowMotionDecision.Snapshot.Active);
        Assert.False(cursorDecision.ShouldForceKeyFrame);
        Assert.False(cursorDecision.Snapshot.Active);
        Assert.True(cursorDecision.Snapshot.SampledMotionRatio < ScreenShareMotionIntegrityGuard.MotionBurstThresholdRatio);
    }

    [Fact]
    public void Evaluate_MotionBurst_RespectsKeyFrameCapAndQuietExit()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var dark = CreateNv12Frame(160, 90, 16);
        var bright = CreateNv12Frame(160, 90, 240);

        guard.Evaluate(dark, 160, 90, 0, enabled: true, externalKeyFrameRequested: false);
        guard.Evaluate(bright, 160, 90, 125, enabled: true, externalKeyFrameRequested: false);
        var firstForce = guard.Evaluate(dark, 160, 90, 250, enabled: true, externalKeyFrameRequested: false);
        var capped = guard.Evaluate(bright, 160, 90, 375, enabled: true, externalKeyFrameRequested: false);
        var secondForce = guard.Evaluate(dark, 160, 90, 500, enabled: true, externalKeyFrameRequested: false);

        Assert.True(firstForce.ShouldForceKeyFrame);
        Assert.False(capped.ShouldForceKeyFrame);
        Assert.True(secondForce.ShouldForceKeyFrame);
        Assert.Equal(2, secondForce.Snapshot.ForcedKeyFrameCount);

        var quietBeforeExit = guard.Evaluate(dark, 160, 90, 875, enabled: true, externalKeyFrameRequested: false);
        var quietAfterExit = guard.Evaluate(dark, 160, 90, 1001, enabled: true, externalKeyFrameRequested: false);

        Assert.True(quietBeforeExit.Snapshot.Active);
        Assert.False(quietAfterExit.Snapshot.Active);
        Assert.Equal(1, quietAfterExit.Snapshot.BurstExitCount);
    }

    [Fact]
    public void Evaluate_ExternalKeyFrameRequest_TakesPrecedenceOverMotionForce()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var dark = CreateNv12Frame(160, 90, 16);
        var bright = CreateNv12Frame(160, 90, 240);

        guard.Evaluate(dark, 160, 90, 0, enabled: true, externalKeyFrameRequested: false);
        guard.Evaluate(bright, 160, 90, 125, enabled: true, externalKeyFrameRequested: false);
        var decision = guard.Evaluate(dark, 160, 90, 250, enabled: true, externalKeyFrameRequested: true);

        Assert.False(decision.ShouldForceKeyFrame);
        Assert.True(decision.Snapshot.Active);
        Assert.Equal(0, decision.Snapshot.ForcedKeyFrameCount);
        Assert.Equal("external_keyframe_precedence", decision.Snapshot.LastReason);
    }

    [Fact]
    public void Evaluate_DisabledPath_DoesNotForceForReducedOrPreviewCallers()
    {
        var guard = new ScreenShareMotionIntegrityGuard();
        var dark = CreateNv12Frame(160, 90, 16);
        var bright = CreateNv12Frame(160, 90, 240);

        guard.Evaluate(dark, 160, 90, 0, enabled: false, externalKeyFrameRequested: false);
        guard.Evaluate(bright, 160, 90, 250, enabled: false, externalKeyFrameRequested: false);
        var decision = guard.Evaluate(dark, 160, 90, 500, enabled: false, externalKeyFrameRequested: false);

        Assert.False(decision.ShouldForceKeyFrame);
        Assert.False(decision.Snapshot.Active);
        Assert.Equal(0, decision.Snapshot.BurstEnterCount);
        Assert.Equal("disabled", decision.Snapshot.LastReason);
    }

    private static byte[] CreateNv12Frame(int width, int height, byte luma)
    {
        var frame = new byte[width * height * 3 / 2];
        Array.Fill(frame, luma, 0, width * height);
        Array.Fill(frame, (byte)128, width * height, frame.Length - width * height);
        return frame;
    }

    private static void FillLumaRegion(
        byte[] frame,
        int stride,
        int x,
        int y,
        int width,
        int height,
        byte value)
    {
        for (var row = y; row < y + height; row++)
        {
            var offset = row * stride + x;
            Array.Fill(frame, value, offset, width);
        }
    }
}

public sealed class ScreenShareMotionIdrProofTrackerTests
{
    [Fact]
    public void ObserveDisplayableOutput_IdrAfterMotionRequest_ConfirmsProof()
    {
        var tracker = new ScreenShareMotionIdrProofTracker();

        tracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs: 1_000);
        tracker.ObserveDisplayableOutput(isIdr: true, motionGuardActive: true, normalTransportMode: true, nowUtcMs: 1_050);

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(1, snapshot.RequestedCount);
        Assert.Equal(1, snapshot.ConfirmedCount);
        Assert.Equal(0, snapshot.MissedCount);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(0, snapshot.ConsecutiveMissCount);
        Assert.Equal(1, snapshot.ActiveMotionIdrFrameRatio);
        Assert.Equal("none", snapshot.LastMissReason);
        Assert.False(snapshot.EncoderRebuildPending);
    }

    [Fact]
    public void ObserveDisplayableOutput_NonIdrAfterMotionRequest_TracksMissAndSchedulesRebuild()
    {
        var tracker = new ScreenShareMotionIdrProofTracker();

        tracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs: 1_000);
        tracker.ObserveDisplayableOutput(isIdr: false, motionGuardActive: true, normalTransportMode: true, nowUtcMs: 1_050);
        tracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs: 1_125);
        tracker.ObserveDisplayableOutput(isIdr: false, motionGuardActive: true, normalTransportMode: true, nowUtcMs: 1_175);

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(2, snapshot.RequestedCount);
        Assert.Equal(0, snapshot.ConfirmedCount);
        Assert.Equal(2, snapshot.MissedCount);
        Assert.Equal(2, snapshot.PendingCount);
        Assert.Equal(2, snapshot.ConsecutiveMissCount);
        Assert.Equal(1, snapshot.BurstMissCount);
        Assert.Equal(0, snapshot.ActiveMotionIdrFrameRatio);
        Assert.Equal("next_displayable_output_was_not_idr", snapshot.LastMissReason);
        Assert.True(snapshot.EncoderRebuildPending);
        Assert.Equal("forced_idr_miss_threshold", snapshot.LastRebuildReason);
    }

    [Fact]
    public void TryConsumeEncoderRebuild_ConsumesOnceAndRateLimitsFollowUpMisses()
    {
        var tracker = new ScreenShareMotionIdrProofTracker();

        MissMotionForcedIdr(tracker);
        MissMotionForcedIdr(tracker);
        Assert.True(tracker.TryConsumeEncoderRebuild(nowUtcMs: 1_000, normalTransportMode: true));

        var consumed = tracker.GetSnapshot();
        Assert.Equal(1, consumed.EncoderRebuildCount);
        Assert.Equal(0, consumed.ConsecutiveMissCount);
        Assert.False(consumed.EncoderRebuildPending);
        Assert.Equal("encoder_rebuild_due_to_forced_idr_miss", consumed.LastRebuildReason);

        MissMotionForcedIdr(tracker);
        MissMotionForcedIdr(tracker);
        Assert.False(tracker.TryConsumeEncoderRebuild(nowUtcMs: 1_500, normalTransportMode: true));

        var rateLimited = tracker.GetSnapshot();
        Assert.True(rateLimited.EncoderRebuildPending);
        Assert.Equal(1, rateLimited.EncoderRebuildSuppressedCount);
        Assert.Equal("rate_limited", rateLimited.LastRebuildReason);

        Assert.True(tracker.TryConsumeEncoderRebuild(nowUtcMs: 2_000, normalTransportMode: true));
        var released = tracker.GetSnapshot();
        Assert.Equal(2, released.EncoderRebuildCount);
        Assert.False(released.EncoderRebuildPending);
    }

    [Fact]
    public void TryConsumeEncoderRebuild_DoesNotRunOutsideNormalTransportMode()
    {
        var tracker = new ScreenShareMotionIdrProofTracker();

        MissMotionForcedIdr(tracker);
        MissMotionForcedIdr(tracker);

        Assert.False(tracker.TryConsumeEncoderRebuild(nowUtcMs: 1_000, normalTransportMode: false));
        Assert.True(tracker.GetSnapshot().EncoderRebuildPending);
        Assert.Equal(0, tracker.GetSnapshot().EncoderRebuildCount);
    }

    [Fact]
    public void TryConsumeEncoderRebuild_TimesOutPendingForcedIdrProof()
    {
        var tracker = new ScreenShareMotionIdrProofTracker();

        tracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs: 1_000);
        tracker.ObserveDisplayableOutput(isIdr: false, motionGuardActive: true, normalTransportMode: true, nowUtcMs: 1_050);

        Assert.False(tracker.TryConsumeEncoderRebuild(nowUtcMs: 1_400, normalTransportMode: true));
        Assert.True(tracker.TryConsumeEncoderRebuild(nowUtcMs: 1_500, normalTransportMode: true));

        var snapshot = tracker.GetSnapshot();
        Assert.Equal("encoder_rebuild_due_to_forced_idr_miss", snapshot.LastRebuildReason);
        Assert.Equal("forced_idr_timeout", snapshot.LastMissReason);
    }

    private static void MissMotionForcedIdr(ScreenShareMotionIdrProofTracker tracker)
    {
        tracker.ObserveMotionForcedKeyFrameRequested(nowUtcMs: 1_000);
        tracker.ObserveDisplayableOutput(isIdr: false, motionGuardActive: true, normalTransportMode: true, nowUtcMs: 1_050);
    }
}
