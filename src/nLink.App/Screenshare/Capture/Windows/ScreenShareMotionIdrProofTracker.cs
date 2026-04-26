using System;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareMotionIdrProofSnapshot(
    long RequestedCount = 0,
    long ConfirmedCount = 0,
    long MissedCount = 0,
    long PendingCount = 0,
    long ConsecutiveMissCount = 0,
    long BurstMissCount = 0,
    double ActiveMotionIdrFrameRatio = 0,
    string LastMissReason = "none",
    long EncoderRebuildCount = 0,
    long EncoderRebuildSuppressedCount = 0,
    bool EncoderRebuildPending = false,
    string LastRebuildReason = "none");

internal sealed class ScreenShareMotionIdrProofTracker
{
    internal const int ConsecutiveMissesBeforeRebuild = 2;
    internal const int RebuildMinimumIntervalMs = 1000;
    internal const int PendingRequestTimeoutMs = 500;

    private long requestedCount;
    private long confirmedCount;
    private long missedCount;
    private long pendingCount;
    private long consecutiveMissCount;
    private long burstMissCount;
    private long activeMotionDisplayableFrames;
    private long activeMotionIdrFrames;
    private long encoderRebuildCount;
    private long encoderRebuildSuppressedCount;
    private long pendingRequestFirstUtcMs = -1;
    private long pendingBurstRequestCount;
    private bool pendingBurstMissRecorded;
    private bool encoderRebuildPending;
    private long lastEncoderRebuildUtcMs = -1;
    private string lastMissReason = "none";
    private string lastRebuildReason = "none";

    public void ObserveMotionForcedKeyFrameRequested(long nowUtcMs)
    {
        requestedCount++;
        pendingCount++;
        pendingBurstRequestCount++;
        if (pendingRequestFirstUtcMs < 0)
        {
            pendingRequestFirstUtcMs = nowUtcMs;
        }
    }

    public void ObserveDisplayableOutput(bool isIdr, bool motionGuardActive, bool normalTransportMode, long nowUtcMs)
    {
        if (motionGuardActive)
        {
            activeMotionDisplayableFrames++;
            if (isIdr)
            {
                activeMotionIdrFrames++;
            }
        }

        if (pendingCount <= 0)
        {
            return;
        }

        if (isIdr)
        {
            confirmedCount += pendingCount;
            ClearPendingProof();
            consecutiveMissCount = 0;
            lastMissReason = "none";
            return;
        }

        missedCount++;
        consecutiveMissCount++;
        lastMissReason = "next_displayable_output_was_not_idr";
        if (pendingBurstRequestCount >= ConsecutiveMissesBeforeRebuild && !pendingBurstMissRecorded)
        {
            burstMissCount++;
            pendingBurstMissRecorded = true;
        }

        if (normalTransportMode &&
            (consecutiveMissCount >= ConsecutiveMissesBeforeRebuild ||
             pendingBurstRequestCount >= ConsecutiveMissesBeforeRebuild))
        {
            encoderRebuildPending = true;
            lastRebuildReason = "forced_idr_miss_threshold";
        }

        ScheduleRebuildIfPendingProofTimedOut(nowUtcMs, normalTransportMode);
    }

    public bool TryConsumeEncoderRebuild(long nowUtcMs, bool normalTransportMode)
    {
        ScheduleRebuildIfPendingProofTimedOut(nowUtcMs, normalTransportMode);
        if (!encoderRebuildPending || !normalTransportMode)
        {
            return false;
        }

        if (lastEncoderRebuildUtcMs >= 0 &&
            nowUtcMs - lastEncoderRebuildUtcMs < RebuildMinimumIntervalMs)
        {
            encoderRebuildSuppressedCount++;
            lastRebuildReason = "rate_limited";
            return false;
        }

        encoderRebuildPending = false;
        lastEncoderRebuildUtcMs = nowUtcMs;
        encoderRebuildCount++;
        consecutiveMissCount = 0;
        lastRebuildReason = "encoder_rebuild_due_to_forced_idr_miss";
        return true;
    }

    public ScreenShareMotionIdrProofSnapshot GetSnapshot()
        => new(
            RequestedCount: requestedCount,
            ConfirmedCount: confirmedCount,
            MissedCount: missedCount,
            PendingCount: pendingCount,
            ConsecutiveMissCount: consecutiveMissCount,
            BurstMissCount: burstMissCount,
            ActiveMotionIdrFrameRatio: activeMotionDisplayableFrames > 0
                ? activeMotionIdrFrames / (double)activeMotionDisplayableFrames
                : 0d,
            LastMissReason: string.IsNullOrWhiteSpace(lastMissReason) ? "none" : lastMissReason,
            EncoderRebuildCount: encoderRebuildCount,
            EncoderRebuildSuppressedCount: encoderRebuildSuppressedCount,
            EncoderRebuildPending: encoderRebuildPending,
            LastRebuildReason: string.IsNullOrWhiteSpace(lastRebuildReason) ? "none" : lastRebuildReason);

    public void Reset(string reason = "reset")
    {
        requestedCount = 0;
        confirmedCount = 0;
        missedCount = 0;
        pendingCount = 0;
        consecutiveMissCount = 0;
        burstMissCount = 0;
        activeMotionDisplayableFrames = 0;
        activeMotionIdrFrames = 0;
        encoderRebuildCount = 0;
        encoderRebuildSuppressedCount = 0;
        pendingRequestFirstUtcMs = -1;
        pendingBurstRequestCount = 0;
        pendingBurstMissRecorded = false;
        encoderRebuildPending = false;
        lastEncoderRebuildUtcMs = -1;
        lastMissReason = "none";
        lastRebuildReason = string.IsNullOrWhiteSpace(reason) ? "reset" : reason;
    }

    private void ScheduleRebuildIfPendingProofTimedOut(long nowUtcMs, bool normalTransportMode)
    {
        if (!normalTransportMode ||
            pendingCount <= 0 ||
            pendingRequestFirstUtcMs < 0 ||
            nowUtcMs - pendingRequestFirstUtcMs < PendingRequestTimeoutMs)
        {
            return;
        }

        encoderRebuildPending = true;
        lastMissReason = "forced_idr_timeout";
        lastRebuildReason = "forced_idr_timeout";
    }

    private void ClearPendingProof()
    {
        pendingCount = 0;
        pendingRequestFirstUtcMs = -1;
        pendingBurstRequestCount = 0;
        pendingBurstMissRecorded = false;
    }
}
