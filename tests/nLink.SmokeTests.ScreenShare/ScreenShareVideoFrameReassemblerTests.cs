using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareVideoFrameReassemblerTests : ScreenShareViewerViewModelTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public ScreenShareVideoFrameReassemblerTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_RequestsSingleKeyframeForRealGap_AndCapsFutureNonKeyTail()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-supersede",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-supersede", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 2, fragmentIndex: 0, isKeyFrame: false));
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 3, fragmentIndex: 0, isKeyFrame: false));
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 4, fragmentIndex: 0, isKeyFrame: false));

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-supersede");
        Assert.InRange(keyframeRequests.Count, 0, 1);
        if (keyframeRequests.Count == 1)
        {
            Assert.Equal("frame_gap_reassembler", keyframeRequests[0].Reason);
        }
        Assert.Equal(0, snapshot.GapNonKeyPrunedCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(3, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal("future_tail_pruned_while_gap_active", snapshot.DominantReassemblerRootCause);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal("future_tail_pruned_while_gap_active", epochDiagnostics.DominantReassemblerRootCause);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "gap_detected", StringComparison.Ordinal));
        Assert.Contains(epochDiagnostics.TopLossBursts, static burst => string.Equals(burst.RootCause, "future_tail_pruned_while_gap_active", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 0);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_AllowsCurrentBurstBudgetWithoutDroppingFrames()
    {
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-burst-headroom",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        for (var frameId = 0; frameId < ScreenShareVideoFrameReassembler.MaxInFlightAssembliesPerSession; frameId++)
        {
            reassembler.OnFragment(CreatePartialFragment("viewer-burst-headroom", streamEpoch: 1, frameId: frameId, fragmentIndex: 0, isKeyFrame: false));
        }

        Assert.Empty(keyframeRequests);
        Assert.Equal(0, reassembler.GetMetricsSnapshot().FramesDropped);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_DropsLateMissingHeadWhileGapIsActive()
    {
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-ordered-ready",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 2, isKeyFrame: false);

        Assert.Equal(new long[] { 0 }, readyFrameIds);
        Assert.Single(keyframeRequests);

        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        Assert.Equal(new long[] { 0 }, readyFrameIds);
        Assert.Single(keyframeRequests);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-ordered-ready");
        Assert.Equal(2, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_DropsStartupFollowers_UntilMissingHeadArrives()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-startup-reorder",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 3, isKeyFrame: false);

        Assert.Empty(readyFrameIds);
        Assert.Single(keyframeRequests);

        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 0, isKeyFrame: true);

        Assert.Equal(new long[] { 0 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-startup-reorder");
        Assert.Equal(3, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_WaitsBeforeResyncingToBufferedRecoveryKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 14, 15, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-resync",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 4, isKeyFrame: true);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);
        Assert.Single(keyframeRequests);
        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-keyframe-resync");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.False(snapshot.GapActive);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_AllowsOrdinaryFramesAfterRecoveryKeyframeWithoutRunwayTrim()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-gap-quarantine",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 6, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 7, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-gap-quarantine");
        var epochSnapshot = Assert.Single(snapshot.EpochSnapshots);
        Assert.Equal(0, snapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(1, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(0, epochSnapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, epochSnapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(1, epochSnapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(new long[] { 0, 4, 5, 6, 7 }, readyFrameIds);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 5);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 6);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 7);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "recovery_keyframe_buffered", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_SimplifiedRecovery_AllowsOrdinaryTailAfterRecoveryKeyframeEmits()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-only-tail-drop",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-keyframe-only-tail-drop");
        Assert.Equal(0, snapshot.RecoveryRunwayOverflowRejectCount);
        Assert.Equal(new long[] { 0, 4, 5 }, readyFrameIds);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 5);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_SimplifiedRecovery_EmitsRecoveryOwnerWithoutProtectedFollowers()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 20, 8, 10, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrames = new List<(long FrameId, ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass)>();
        reassembler.FrameReady += (_, e) => readyFrames.Add((e.FrameId, e.RecoveryDeliveryClass));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-only-emits",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        Assert.Contains(readyFrames, static frame => frame.FrameId == 4 && frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.RecoveryOwner);
        Assert.Contains(readyFrames, static frame => frame.FrameId == 5 && frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal);
        Assert.DoesNotContain(readyFrames, static frame => frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.ProtectedFollower);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_ResyncPreservesContiguousFollowersBehindRecoveryKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-recovery-followers",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 6, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4, 5, 6 }, readyFrameIds);
        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-recovery-followers");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal(4, snapshot.FramesEmitted);
        Assert.Equal(0, snapshot.RunwayFollowersEmittedWithinActionableWindowCount);
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_NonContiguousFollowerStartsNewGapAndStillWaitsForKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 10, 10, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-noncontiguous-followers",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 6, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-noncontiguous-followers");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal(0, snapshot.RecoveryRunwayOverflowRejectCount);
        Assert.Equal(0, snapshot.StaleRunwayWindowAbortCount);
        Assert.Equal(0, snapshot.LateSameEpochAfterHeadAdvancedDropCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 5 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 6 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_Attribution_TracksOneShotResyncPurge()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 14, 15, 5, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-attribution",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 3, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 5, isKeyFrame: true);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-attribution");
        Assert.Equal(2, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(0, snapshot.ReadyFrameSkippedReplacedLossCount);
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal("future_tail_pruned_while_gap_active", snapshot.DominantReassemblerRootCause);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal("future_tail_pruned_while_gap_active", epochDiagnostics.DominantReassemblerRootCause);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "resync_triggered", StringComparison.Ordinal));
        Assert.Contains(epochDiagnostics.TopLossBursts, static burst => string.Equals(burst.RootCause, "future_tail_pruned_while_gap_active", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_NewerSameEpochRecoveryCandidate_ReplacesOlderBufferedOwner()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var sessionId = "viewer-superseded-recovery-tail-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler();
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 1, frameId: 4, fragmentIndex: 0, isKeyFrame: true));
        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 7, isKeyFrame: true);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.Equal(new long[] { 0, 7 }, readyFrameIds);
        Assert.Equal(7L, snapshot.WinningRecoveryFrameId);
        Assert.Equal(7L, epochDiagnostics.WinningRecoveryFrameId);
        Assert.True(snapshot.RecoveryOwnerReplacedCount >= 1);
        Assert.True(epochDiagnostics.RecoveryOwnerReplacedCount >= 1);
        Assert.True(snapshot.RecoveryKeyframeSupersededOrReplacedCount >= 1);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "gap_recovery_keyframe_replaced", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_recovery_owner_replaced", logText, StringComparison.Ordinal);
        Assert.Contains("stream_epoch=1", logText, StringComparison.Ordinal);
        Assert.Contains("previous_recovery_owner_frame_id=4", logText, StringComparison.Ordinal);
        Assert.Contains("new_recovery_owner_frame_id=7", logText, StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_Investigation_DistinguishesGapTailSuppressionRecoveryOwnerSuppressionAndOrderedHeadCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        var preCandidateSessionId = "viewer-investigation-pre-candidate-" + Guid.NewGuid().ToString("N");
        var preCandidateReassembler = new ScreenShareVideoFrameReassembler();
        preCandidateReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = preCandidateSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(preCandidateReassembler, preCandidateSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(preCandidateReassembler, preCandidateSessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        var preCandidateSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(preCandidateSessionId);
        Assert.Contains(
            preCandidateSnapshot.RecentLosses,
            static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));

        var suppressedRecoveryOwnerSessionId = "viewer-investigation-owner-suppressed-" + Guid.NewGuid().ToString("N");
        var suppressedRecoveryOwnerReassembler = new ScreenShareVideoFrameReassembler();
        suppressedRecoveryOwnerReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = suppressedRecoveryOwnerSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(suppressedRecoveryOwnerReassembler, suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        suppressedRecoveryOwnerReassembler.OnFragment(CreatePartialFragment(suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 7, fragmentIndex: 0, isKeyFrame: true));
        CompleteFrame(suppressedRecoveryOwnerReassembler, suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 4, isKeyFrame: true);
        var suppressedRecoveryOwnerSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(suppressedRecoveryOwnerSessionId);
        Assert.Contains(
            suppressedRecoveryOwnerSnapshot.RecentLosses,
            static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "same_epoch_recovery_owner_suppressed", StringComparison.Ordinal));

        var orderedHeadCleanupSessionId = "viewer-investigation-ordered-head-" + Guid.NewGuid().ToString("N");
        var orderedHeadCleanupReassembler = new ScreenShareVideoFrameReassembler();
        orderedHeadCleanupReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = orderedHeadCleanupSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 1, isKeyFrame: false);
        var orderedHeadCleanupSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(orderedHeadCleanupSessionId);
        Assert.Contains(
            orderedHeadCleanupSnapshot.RecentLosses,
            static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "late_fragment_after_ordered_head", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_OlderEpochLateBurstAfterEpochAdvance_IsClassifiedAsNonLossCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 22, 12, 38, 34, TimeSpan.Zero);
        var sessionId = "viewer-investigation-older-epoch-late-burst-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<(long StreamEpoch, long FrameId)>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add((e.StreamEpoch, e.FrameId));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 5,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 5, frameId: 114, fragmentIndex: 0, isKeyFrame: true));
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 116, isKeyFrame: true);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 6,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(reassembler, sessionId, streamEpoch: 6, frameId: 0, isKeyFrame: true);

        var snapshotBeforeOlderEpochCleanup = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFiveBeforeOlderEpochCleanup = snapshotBeforeOlderEpochCleanup.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        for (var frameId = 117L; frameId <= 124L; frameId++)
        {
            CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: frameId, isKeyFrame: false);
        }

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFive = snapshot.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        Assert.Equal(
            new (long StreamEpoch, long FrameId)[] { (5, 0), (5, 116), (6, 0) },
            readyFrameIds);
        Assert.True(epochFive.RecoveryOwnerReplacedCount >= 1);
        Assert.True(snapshot.OlderEpochCleanupAfterEpochAdvanceCount >= 8);
        Assert.True(epochFive.OlderEpochCleanupAfterEpochAdvanceCount >= 8);
        Assert.Equal(snapshotBeforeOlderEpochCleanup.ReassemblerLossCount, snapshot.ReassemblerLossCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(epochFiveBeforeOlderEpochCleanup.LateFragmentAfterHeadAdvancedCount, epochFive.LateFragmentAfterHeadAdvancedCount);
        Assert.DoesNotContain(
            epochFive.TopLossBursts,
            static burst =>
                string.Equals(burst.RootCause, "late_fragment_after_head_advanced", StringComparison.Ordinal) &&
                burst.ExpectedNextFrameId == 1 &&
                burst.ReceivedFrameIdStart == 117 &&
                burst.ReceivedFrameIdEnd == 124);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance", logText, StringComparison.Ordinal);
        Assert.Contains("stream_epoch=5", logText, StringComparison.Ordinal);
        Assert.Contains("session_current_stream_epoch=6", logText, StringComparison.Ordinal);
        Assert.Contains("source=incoming_fragment", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=screenshare_reassembler_actionable_late_fragment; session_id=(redacted); stream_epoch=5; session_current_stream_epoch=6; frame_id=117",
            logText,
            StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_EpochAdvancePurge_IsClassifiedAsNonLossCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var sessionId = "viewer-investigation-epoch-advance-purge-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler();

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 5,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 5, frameId: 10, fragmentIndex: 0, isKeyFrame: true));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 6,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFive = snapshot.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        Assert.Equal(0, snapshot.ReassemblerLossCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochFive.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(1, snapshot.OlderEpochCleanupAfterEpochAdvanceCount);
        Assert.Equal(1, epochFive.OlderEpochCleanupAfterEpochAdvanceCount);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance", logText, StringComparison.Ordinal);
        Assert.Contains("session_current_stream_epoch=6", logText, StringComparison.Ordinal);
        Assert.Contains("frame_id=10", logText, StringComparison.Ordinal);
        Assert.Contains("source=epoch_advance_purge", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=screenshare_reassembler_actionable_late_fragment; session_id=(redacted); stream_epoch=5; session_current_stream_epoch=6; frame_id=10",
            logText,
            StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_PostResyncOlderTail_IsCountedAsSupersededCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 18, 11, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-post-resync-superseded-tail",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 4, isKeyFrame: true);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 3, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-post-resync-superseded-tail");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(snapshot.SupersededRecoveryTailCleanupCount >= 1);
        Assert.True(epochDiagnostics.SupersededRecoveryTailCleanupCount >= 1);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_LateFragmentBehindOrderedHead_IsBenignCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-ordered-head-cleanup",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 2, isKeyFrame: false);

        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-ordered-head-cleanup");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(snapshot.OrderedEmitHeadFrameId >= 2);
        Assert.True(epochDiagnostics.OrderedEmitHeadFrameId >= 2);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "late_fragment_after_ordered_head", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_TrimPurgesBufferedFramesBehindAppliedHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-proven-head-trim",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        reassembler.OnFragment(CreatePartialFragment("viewer-proven-head-trim", streamEpoch: 1, frameId: 0, fragmentIndex: 0, isKeyFrame: true));
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-proven-head-trim",
            streamEpoch: 1,
            frameId: 4,
            isKeyFrame: false);

        reassembler.OnFragment(CreatePartialFragment("viewer-proven-head-trim", streamEpoch: 1, frameId: 6, fragmentIndex: 0, isKeyFrame: true));

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-proven-head-trim");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(
            snapshot.AssemblyEvictedLossCount + snapshot.ReassemblerStaleSupersededLossCount >= 1,
            $"Expected the stale buffered frame to be dropped from tracked state, but saw assembly_evicted={snapshot.AssemblyEvictedLossCount} and stale_superseded={snapshot.ReassemblerStaleSupersededLossCount}.");
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(
            snapshot.RecentLosses,
            static loss => loss.FrameId == 0 && string.Equals(loss.Reason, "late_fragment_after_applied_head", StringComparison.Ordinal));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_OlderTailBehindRecoveryOwner_IsBenignCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 20, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-recovery-floor-suppression",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4, 5 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-recovery-floor-suppression");
        Assert.Equal(0, snapshot.SuppressedEmitDuringRecoveryWaitCount);
        Assert.Contains(
            snapshot.RecentLosses,
            static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal));
    }

}
