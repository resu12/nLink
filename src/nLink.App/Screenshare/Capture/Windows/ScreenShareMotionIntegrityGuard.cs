using System;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareMotionIntegrityDecision(
    bool ShouldForceKeyFrame,
    ScreenShareMotionIntegrityGuardSnapshot Snapshot);

internal readonly record struct ScreenShareMotionIntegrityGuardSnapshot(
    bool Active = false,
    double SampledMotionRatio = 0,
    double PeakSampledMotionRatio = 0,
    int ScrollMotionActiveBandCount = 0,
    double ScrollMotionPeakBandRatio = 0,
    long HighMotionFrameCount = 0,
    long ScrollTriggerCount = 0,
    long BurstEnterCount = 0,
    long BurstExitCount = 0,
    long ForcedKeyFrameCount = 0,
    string LastTriggerKind = "none",
    string LastReason = "none");

internal sealed class ScreenShareMotionIntegrityGuard
{
    private const int SampleColumns = 32;
    private const int SampleRows = 18;
    private const int SampleCount = SampleColumns * SampleRows;
    private const byte LumaDeltaThreshold = 20;

    internal const double MotionBurstThresholdRatio = 0.20d;
    internal const double StrongDistributedMotionThresholdRatio = 0.25d;
    internal const double ScrollBandMotionThresholdRatio = 0.12d;
    internal const double FullFrameBandRatioExclusion = 0.95d;
    internal const int ScrollMotionMinimumActiveBands = 4;
    internal const int StrongScrollMotionMinimumActiveBands = 6;
    internal const int ScrollMotionMaximumActiveBands = SampleRows - 2;
    internal const int ConsecutiveHighMotionFramesRequired = 2;
    internal const int QuietExitMs = 500;
    internal const int ForcedKeyFrameMinimumIntervalMs = 250;
    internal const int ForcedKeyFrameFrameInterval = 2;

    private byte[]? previousSamples;
    private bool hasPreviousSamples;
    private bool active;
    private int consecutiveHighMotionFrames;
    private int framesSinceLastForcedKeyFrame = ForcedKeyFrameFrameInterval;
    private long lastHighMotionUtcMs = -1;
    private long lastForcedKeyFrameUtcMs = -1;
    private long burstEnterCount;
    private long burstExitCount;
    private long forcedKeyFrameCount;
    private long highMotionFrameCount;
    private long scrollTriggerCount;
    private double sampledMotionRatio;
    private double peakSampledMotionRatio;
    private int scrollMotionActiveBandCount;
    private double scrollMotionPeakBandRatio;
    private string lastTriggerKind = "none";
    private string lastReason = "none";

    public ScreenShareMotionIntegrityDecision Evaluate(
        ReadOnlySpan<byte> nv12Bytes,
        int width,
        int height,
        long nowUtcMs,
        bool enabled,
        bool externalKeyFrameRequested)
    {
        if (!enabled)
        {
            ResetTransientState("disabled");
            return new ScreenShareMotionIntegrityDecision(false, GetSnapshot());
        }

        if (width <= 0 || height <= 0 || nv12Bytes.Length < width * height)
        {
            ResetTransientState("invalid_nv12");
            return new ScreenShareMotionIntegrityDecision(false, GetSnapshot());
        }

        var motionSample = SampleMotion(nv12Bytes, width, height);
        sampledMotionRatio = motionSample.GlobalRatio;
        peakSampledMotionRatio = Math.Max(peakSampledMotionRatio, sampledMotionRatio);
        scrollMotionActiveBandCount = motionSample.ActiveBandCount;
        scrollMotionPeakBandRatio = motionSample.PeakBandRatio;

        var globalHighMotion =
            motionSample.HasPreviousSample &&
            sampledMotionRatio > MotionBurstThresholdRatio;
        var scrollMotion =
            motionSample.HasPreviousSample &&
            motionSample.ActiveBandCount >= ScrollMotionMinimumActiveBands &&
            motionSample.ActiveBandCount <= ScrollMotionMaximumActiveBands &&
            motionSample.PeakBandRatio >= ScrollBandMotionThresholdRatio;
        var strongDistributedMotion =
            motionSample.HasPreviousSample &&
            sampledMotionRatio >= StrongDistributedMotionThresholdRatio &&
            motionSample.PeakBandRatio < FullFrameBandRatioExclusion;
        var strongScrollMotion =
            strongDistributedMotion ||
            (scrollMotion &&
             motionSample.ActiveBandCount >= StrongScrollMotionMinimumActiveBands);
        var highMotion = globalHighMotion || scrollMotion;
        if (highMotion)
        {
            highMotionFrameCount++;
            if (scrollMotion || strongDistributedMotion)
            {
                scrollTriggerCount++;
            }

            consecutiveHighMotionFrames++;
            lastHighMotionUtcMs = nowUtcMs;
            lastTriggerKind = strongScrollMotion
                ? (strongDistributedMotion ? "strong_distributed_motion" : "strong_scroll_motion")
                : scrollMotion
                    ? "moderate_scroll_motion"
                    : "global_high_motion";
        }
        else
        {
            consecutiveHighMotionFrames = 0;
        }

        if (!active &&
            (strongScrollMotion ||
             consecutiveHighMotionFrames >= ConsecutiveHighMotionFramesRequired))
        {
            active = true;
            burstEnterCount++;
            framesSinceLastForcedKeyFrame = ForcedKeyFrameFrameInterval;
            lastReason = strongScrollMotion ? "strong_scroll_motion_burst" : "high_motion_burst";
        }

        if (active &&
            !highMotion &&
            lastHighMotionUtcMs >= 0 &&
            nowUtcMs - lastHighMotionUtcMs >= QuietExitMs)
        {
            active = false;
            burstExitCount++;
            framesSinceLastForcedKeyFrame = ForcedKeyFrameFrameInterval;
            lastReason = "quiet_exit";
        }

        if (!active)
        {
            return new ScreenShareMotionIntegrityDecision(false, GetSnapshot());
        }

        framesSinceLastForcedKeyFrame++;
        if (externalKeyFrameRequested)
        {
            lastReason = "external_keyframe_precedence";
            return new ScreenShareMotionIntegrityDecision(false, GetSnapshot());
        }

        var intervalElapsed = lastForcedKeyFrameUtcMs < 0 ||
                              nowUtcMs - lastForcedKeyFrameUtcMs >= ForcedKeyFrameMinimumIntervalMs;
        var frameIntervalElapsed = framesSinceLastForcedKeyFrame >= ForcedKeyFrameFrameInterval;
        if (intervalElapsed && frameIntervalElapsed)
        {
            forcedKeyFrameCount++;
            framesSinceLastForcedKeyFrame = 0;
            lastForcedKeyFrameUtcMs = nowUtcMs;
            lastReason = "motion_keyframe_due";
            return new ScreenShareMotionIntegrityDecision(true, GetSnapshot());
        }

        lastReason = "motion_keyframe_cap_wait_interval_ms=" +
                     Math.Max(0, ForcedKeyFrameMinimumIntervalMs - (nowUtcMs - lastForcedKeyFrameUtcMs));
        return new ScreenShareMotionIntegrityDecision(false, GetSnapshot());
    }

    public void Reset(string reason = "reset")
    {
        previousSamples = null;
        hasPreviousSamples = false;
        active = false;
        consecutiveHighMotionFrames = 0;
        framesSinceLastForcedKeyFrame = ForcedKeyFrameFrameInterval;
        lastHighMotionUtcMs = -1;
        lastForcedKeyFrameUtcMs = -1;
        burstEnterCount = 0;
        burstExitCount = 0;
        forcedKeyFrameCount = 0;
        highMotionFrameCount = 0;
        scrollTriggerCount = 0;
        sampledMotionRatio = 0;
        peakSampledMotionRatio = 0;
        scrollMotionActiveBandCount = 0;
        scrollMotionPeakBandRatio = 0;
        lastTriggerKind = "none";
        lastReason = string.IsNullOrWhiteSpace(reason) ? "reset" : reason;
    }

    public ScreenShareMotionIntegrityGuardSnapshot GetSnapshot()
        => new(
            Active: active,
            SampledMotionRatio: sampledMotionRatio,
            PeakSampledMotionRatio: peakSampledMotionRatio,
            ScrollMotionActiveBandCount: scrollMotionActiveBandCount,
            ScrollMotionPeakBandRatio: scrollMotionPeakBandRatio,
            HighMotionFrameCount: highMotionFrameCount,
            ScrollTriggerCount: scrollTriggerCount,
            BurstEnterCount: burstEnterCount,
            BurstExitCount: burstExitCount,
            ForcedKeyFrameCount: forcedKeyFrameCount,
            LastTriggerKind: string.IsNullOrWhiteSpace(lastTriggerKind) ? "none" : lastTriggerKind,
            LastReason: string.IsNullOrWhiteSpace(lastReason) ? "none" : lastReason);

    private void ResetTransientState(string reason)
    {
        previousSamples = null;
        hasPreviousSamples = false;
        active = false;
        consecutiveHighMotionFrames = 0;
        framesSinceLastForcedKeyFrame = ForcedKeyFrameFrameInterval;
        lastHighMotionUtcMs = -1;
        lastForcedKeyFrameUtcMs = -1;
        sampledMotionRatio = 0;
        scrollMotionActiveBandCount = 0;
        scrollMotionPeakBandRatio = 0;
        lastReason = reason;
    }

    private MotionSample SampleMotion(ReadOnlySpan<byte> nv12Bytes, int width, int height)
    {
        var currentSamples = new byte[SampleCount];
        var previous = previousSamples;
        var changed = 0;
        var sampleIndex = 0;
        var activeBandCount = 0;
        var peakBandRatio = 0d;
        for (var row = 0; row < SampleRows; row++)
        {
            var y = Math.Min(height - 1, ((row * 2 + 1) * height) / (SampleRows * 2));
            var rowOffset = y * width;
            var changedInBand = 0;
            for (var column = 0; column < SampleColumns; column++)
            {
                var x = Math.Min(width - 1, ((column * 2 + 1) * width) / (SampleColumns * 2));
                var value = nv12Bytes[rowOffset + x];
                currentSamples[sampleIndex] = value;
                if (hasPreviousSamples &&
                    previous is not null &&
                    Math.Abs(value - previous[sampleIndex]) >= LumaDeltaThreshold)
                {
                    changed++;
                    changedInBand++;
                }

                sampleIndex++;
            }

            if (hasPreviousSamples)
            {
                var bandRatio = changedInBand / (double)SampleColumns;
                peakBandRatio = Math.Max(peakBandRatio, bandRatio);
                if (bandRatio >= ScrollBandMotionThresholdRatio)
                {
                    activeBandCount++;
                }
            }
        }

        previousSamples = currentSamples;
        if (!hasPreviousSamples)
        {
            hasPreviousSamples = true;
            return new MotionSample(false, 0d, 0, 0d);
        }

        return new MotionSample(true, changed / (double)SampleCount, activeBandCount, peakBandRatio);
    }

    private readonly record struct MotionSample(
        bool HasPreviousSample,
        double GlobalRatio,
        int ActiveBandCount,
        double PeakBandRatio);
}
