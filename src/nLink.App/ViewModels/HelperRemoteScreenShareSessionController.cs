using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLink.App.Services.ScreenCapture;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.ViewModels;

internal readonly record struct HelperRemoteVisibleApplyProgress(
    long VisibleHeadFrameId,
    long StableVisibleHeadFrameId,
    long FramesAppliedSinceLastGap,
    long AppliedHeadFrameId);

internal readonly record struct HelperRemoteCurrentEpochProgressProof(
    bool Active,
    string Source,
    long ProofFrameId,
    long FramesAppliedSinceLastGap);

internal readonly record struct DeferredHelperRemoteFrameCandidate(
    string SessionId,
    string Encoding,
    byte[] EncodedFrameBytes,
    long CapturedTsUtcMs,
    long StreamEpoch,
    long FrameId,
    bool IsKeyFrame,
    long ArrivalSequence);

internal readonly record struct HelperRemoteRecoveryActivationResult(
    bool NewlyActive,
    int PurgedDeferredCandidateCount,
    long RecoveryExpectedNextFrameId,
    long RecoveryReceivedFrameId,
    long LastCleanFrameId);

internal readonly record struct HelperRemoteReservedApplyClearResult(
    bool Cleared,
    long HoldMs);

internal readonly record struct HelperRemoteRecoveryProgressCorridorAbortResult(
    bool Aborted,
    long StreamEpoch,
    long RecoveryFrameId,
    long LastContiguousFrameId,
    int ContiguousFollowerApplyCount,
    string Reason,
    long HoldMs);

internal readonly record struct HelperRemoteRecoveryProgressCorridorApplyResult(
    bool Applied,
    bool Succeeded,
    HelperRemoteRecoveryProgressCorridorAbortResult Abort,
    long StreamEpoch,
    long RecoveryFrameId,
    long LastContiguousFrameId,
    int ContiguousFollowerApplyCount,
    long HoldMs);

internal readonly record struct HelperRemotePendingRecoveryRunwayAbortResult(
    bool Matched,
    long ExpectedNextFrameId,
    long ReceivedFrameId,
    long RecoveryFrameId,
    long HoldMs);

internal readonly record struct HelperRemoteDeferredCandidateReleaseResult(
    bool HasCandidateToEnqueue,
    DeferredHelperRemoteFrameCandidate CandidateToEnqueue,
    DeferredHelperRemoteFrameCandidate[] RejectedCandidates,
    HelperRemoteRecoveryProgressCorridorAbortResult CorridorAbort);

internal sealed class HelperRemoteRecoveryState
{
    public long VisibleHeadStreamEpoch { get; set; }

    public long VisibleHeadFrameId { get; set; } = -1;

    public long NeedMoreInputBurstEpoch { get; set; }

    public int NeedMoreInputBurstCount { get; set; }

    public bool RecoveryActive { get; set; }

    public long RecoveryStreamEpoch { get; set; }

    public string RecoveryReason { get; set; } = string.Empty;

    public long TrackedFrameEpoch { get; set; }

    public long LastSeenFrameId { get; set; } = -1;

    public long LastCleanFrameId { get; set; } = -1;

    public bool HasCleanKeyframeForEpoch { get; set; }

    public long RecoveryExpectedNextFrameId { get; set; } = -1;

    public long RecoveryReceivedFrameId { get; set; } = -1;

    public string SessionId { get; set; } = string.Empty;
}

internal sealed class HelperRemoteFollowerState
{
    public bool ReservedApplyActive { get; set; }

    public long ReservedApplyStreamEpoch { get; set; } = -1;

    public long ReservedApplyFrameId { get; set; } = -1;

    public DateTimeOffset ReservedApplyPendingSinceUtc { get; set; }

    public bool StartupKeyframePendingVisibleApplyActive { get; set; }

    public long StartupKeyframePendingVisibleApplyStreamEpoch { get; set; } = -1;

    public long StartupKeyframePendingVisibleApplyFrameId { get; set; } = -1;

    public DateTimeOffset StartupKeyframePendingVisibleApplyPendingSinceUtc { get; set; }

    public object DeferredFollowerGate { get; } = new();

    public SortedDictionary<long, DeferredHelperRemoteFrameCandidate> DeferredPostRecoveryCandidates { get; } = new();

    public long DeferredPostRecoveryCandidateSequence { get; set; }

    public long PostRecoveryStabilizationEpoch { get; set; }

    public int PostRecoveryReservedAppliesRemaining { get; set; }

    public bool RecoveryProgressCorridorActive { get; set; }

    public long RecoveryProgressCorridorEpoch { get; set; }

    public long RecoveryProgressCorridorRecoveryFrameId { get; set; } = -1;

    public long RecoveryProgressCorridorLastFrameId { get; set; } = -1;

    public int RecoveryProgressCorridorAppliedCount { get; set; }

    public DateTimeOffset RecoveryProgressCorridorStartedUtc { get; set; }

    public DateTimeOffset RecoveryProgressCorridorLastVisibleApplyUtc { get; set; }

    public bool ExpiredRecoveryRunwayActive { get; set; }

    public long ExpiredRecoveryRunwayEpoch { get; set; }

    public long ExpiredRecoveryRunwayLastContiguousFrameId { get; set; } = -1;

    public long ExpiredRecoveryRunwayMaximumFrameId { get; set; } = -1;

    public DateTimeOffset ExpiredRecoveryRunwayStartedUtc { get; set; }

    public bool PendingRecoveryRunwayAbortActive { get; set; }

    public long PendingRecoveryRunwayAbortEpoch { get; set; }

    public long PendingRecoveryRunwayAbortExpectedNextFrameId { get; set; } = -1;

    public long PendingRecoveryRunwayAbortReceivedFrameId { get; set; } = -1;

    public string PendingRecoveryRunwayAbortReason { get; set; } = string.Empty;

    public DateTimeOffset PendingRecoveryRunwayAbortSetUtc { get; set; }
}

internal sealed class HelperRemoteVisibleProgressState
{
    public long PostRecoveryVisibleGenerationResetCount { get; set; }

    public long PostRecoveryPurgedPreRecoveryFollowerCount { get; set; }

    public long PostRecoveryStaleDropBypassCount { get; set; }

    public long RecoveryFollowerWindowBufferedCount { get; set; }

    public long RecoveryFollowerWindowAppliedCount { get; set; }

    public long RecoveryFollowerWindowTrimmedCount { get; set; }

    public long RecoveryProgressCorridorCount { get; set; }

    public long RecoveryProgressCorridorSuccessCount { get; set; }

    public long RecoveryProgressCorridorAbortCount { get; set; }

    public long RecoveryProgressCorridorAppliedCount { get; set; }

    public long StaleSupersededRecoverySuppressedCount { get; set; }

    public long SoftStaleCleanupCount { get; set; }

    public long PreCandidateGapTailEmittedToViewerCount { get; set; }

    public long RecoveryKeyframePendingVisibleApplyCount { get; set; }

    public long StartupCorridorBufferedFollowerCount { get; set; }

    public long StartupCorridorReleaseCount { get; set; }

    public long StartupCorridorAbortCount { get; set; }

    public string StartupCorridorAbortReason { get; set; } = "none";

    public long ProtectedRecoveryDeliveryCount { get; set; }

    public long RecoveryRunwayContiguousFollowerBufferCount { get; set; }

    public long RecoveryRunwayContiguousFollowerApplyCount { get; set; }

    public long LastReservedApplyHoldMs { get; set; }

    public long LastRecoveryProgressCorridorHoldMs { get; set; }

    public long LastRecoveryRunwayAbortHoldMs { get; set; }

    public string LastRecoveryProgressCorridorAbortReason { get; set; } = "none";
}

internal sealed class HelperRemoteScreenShareSessionController
{
    private const int HelperRemoteNeedMoreInputStallThreshold = 2;
    private readonly IHelperRemoteScreenShareSessionContext context;

    public HelperRemoteScreenShareSessionController(IHelperRemoteScreenShareSessionContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
    }

    internal HelperRemoteRecoveryState RecoveryState { get; } = new();

    internal HelperRemoteFollowerState FollowerState { get; } = new();

    internal HelperRemoteVisibleProgressState VisibleProgressState { get; } = new();

    public string SessionId => RecoveryState.SessionId;

    public void SetSessionId(string? sessionId)
    {
        RecoveryState.SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : sessionId.Trim();
    }

    public void EnsureSessionId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            SetSessionId(sessionId);
        }
    }

    public bool IsReservedApplyRequest(EncodedFrameDecodeRequest request)
    {
        return FollowerState.ReservedApplyActive &&
               FollowerState.ReservedApplyStreamEpoch == request.StreamEpoch &&
               FollowerState.ReservedApplyFrameId == request.FrameId;
    }

    public bool IsReservedApplyFrame(long streamEpoch, long frameId)
    {
        return FollowerState.ReservedApplyActive &&
               FollowerState.ReservedApplyStreamEpoch == streamEpoch &&
               FollowerState.ReservedApplyFrameId == frameId;
    }

    public bool IsStartupKeyframePendingVisibleApplyRequest(EncodedFrameDecodeRequest request)
    {
        return FollowerState.StartupKeyframePendingVisibleApplyActive &&
               FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch == request.StreamEpoch &&
               FollowerState.StartupKeyframePendingVisibleApplyFrameId == request.FrameId;
    }

    public void SetReservedApplyPending(
        long streamEpoch,
        long frameId,
        bool startupKeyframePendingVisibleApply,
        DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 || frameId < 0)
        {
            return;
        }

        FollowerState.ReservedApplyActive = true;
        FollowerState.ReservedApplyStreamEpoch = streamEpoch;
        FollowerState.ReservedApplyFrameId = frameId;
        FollowerState.ReservedApplyPendingSinceUtc = nowUtc;
        if (startupKeyframePendingVisibleApply)
        {
            FollowerState.StartupKeyframePendingVisibleApplyActive = true;
            FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch = streamEpoch;
            FollowerState.StartupKeyframePendingVisibleApplyFrameId = frameId;
            FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc = nowUtc;
        }
    }

    public HelperRemoteReservedApplyClearResult ClearReservedApplyIfMatch(EncodedFrameDecodeRequest request)
    {
        if (!IsReservedApplyRequest(request))
        {
            return default;
        }

        var holdMs = 0L;
        if (FollowerState.ReservedApplyPendingSinceUtc != default)
        {
            holdMs = Math.Max(
                holdMs,
                Math.Max(0L, (long)(DateTimeOffset.UtcNow - FollowerState.ReservedApplyPendingSinceUtc).TotalMilliseconds));
        }

        FollowerState.ReservedApplyActive = false;
        FollowerState.ReservedApplyStreamEpoch = -1;
        FollowerState.ReservedApplyFrameId = -1;
        FollowerState.ReservedApplyPendingSinceUtc = default;
        if (IsStartupKeyframePendingVisibleApplyRequest(request))
        {
            if (FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc != default)
            {
                holdMs = Math.Max(
                    holdMs,
                    Math.Max(
                        0L,
                        (long)(DateTimeOffset.UtcNow - FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc)
                            .TotalMilliseconds));
            }

            FollowerState.StartupKeyframePendingVisibleApplyActive = false;
            FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch = -1;
            FollowerState.StartupKeyframePendingVisibleApplyFrameId = -1;
            FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc = default;
        }

        VisibleProgressState.LastReservedApplyHoldMs = holdMs;
        return new HelperRemoteReservedApplyClearResult(true, holdMs);
    }

    public void ClearReservedApplyThroughEpoch(long streamEpoch)
    {
        if (!FollowerState.ReservedApplyActive || streamEpoch < FollowerState.ReservedApplyStreamEpoch)
        {
            return;
        }

        if (FollowerState.ReservedApplyPendingSinceUtc != default)
        {
            VisibleProgressState.LastReservedApplyHoldMs = Math.Max(
                0L,
                (long)(DateTimeOffset.UtcNow - FollowerState.ReservedApplyPendingSinceUtc).TotalMilliseconds);
        }

        FollowerState.ReservedApplyActive = false;
        FollowerState.ReservedApplyStreamEpoch = -1;
        FollowerState.ReservedApplyFrameId = -1;
        FollowerState.ReservedApplyPendingSinceUtc = default;
        if (FollowerState.StartupKeyframePendingVisibleApplyActive &&
            streamEpoch >= FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch)
        {
            FollowerState.StartupKeyframePendingVisibleApplyActive = false;
            FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch = -1;
            FollowerState.StartupKeyframePendingVisibleApplyFrameId = -1;
            FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc = default;
        }
    }

    public int GetDeferredPostRecoveryCandidateCount()
    {
        lock (FollowerState.DeferredFollowerGate)
        {
            return FollowerState.DeferredPostRecoveryCandidates.Count;
        }
    }

    public DeferredHelperRemoteFrameCandidate[] ClearDeferredPostRecoveryCandidates()
    {
        lock (FollowerState.DeferredFollowerGate)
        {
            if (FollowerState.DeferredPostRecoveryCandidates.Count == 0)
            {
                return Array.Empty<DeferredHelperRemoteFrameCandidate>();
            }

            var candidates = FollowerState.DeferredPostRecoveryCandidates.Values.ToArray();
            FollowerState.DeferredPostRecoveryCandidates.Clear();
            return candidates;
        }
    }

    public HelperRemoteDeferredCandidateReleaseResult ReleaseDeferredPostRecoveryCandidateIfMatch(
        long streamEpoch,
        long previousVisibleFrameId)
    {
        lock (FollowerState.DeferredFollowerGate)
        {
            if (FollowerState.DeferredPostRecoveryCandidates.Count == 0)
            {
                return default;
            }

            var rejectedCandidates = new List<DeferredHelperRemoteFrameCandidate>();
            while (FollowerState.DeferredPostRecoveryCandidates.Count > 0)
            {
                var candidatePair = FollowerState.DeferredPostRecoveryCandidates.First();
                var candidate = candidatePair.Value;
                FollowerState.DeferredPostRecoveryCandidates.Remove(candidatePair.Key);

                if (candidate.StreamEpoch != streamEpoch)
                {
                    rejectedCandidates.Add(candidate);
                    continue;
                }

                if (previousVisibleFrameId >= 0 && candidate.FrameId != previousVisibleFrameId + 1)
                {
                    rejectedCandidates.Add(candidate);
                    while (FollowerState.DeferredPostRecoveryCandidates.Count > 0)
                    {
                        var stalePair = FollowerState.DeferredPostRecoveryCandidates.First();
                        rejectedCandidates.Add(stalePair.Value);
                        FollowerState.DeferredPostRecoveryCandidates.Remove(stalePair.Key);
                    }

                    ClearReservedApplyThroughEpoch(streamEpoch);
                    ResetPostRecoveryStabilization();
                    var corridorAbort = AbortRecoveryProgressCorridor("non_contiguous_follower", DateTimeOffset.UtcNow);
                    return new HelperRemoteDeferredCandidateReleaseResult(
                        HasCandidateToEnqueue: false,
                        CandidateToEnqueue: default,
                        RejectedCandidates: rejectedCandidates.ToArray(),
                        CorridorAbort: corridorAbort);
                }

                return new HelperRemoteDeferredCandidateReleaseResult(
                    HasCandidateToEnqueue: true,
                    CandidateToEnqueue: candidate,
                    RejectedCandidates: rejectedCandidates.ToArray(),
                    CorridorAbort: default);
            }

            return new HelperRemoteDeferredCandidateReleaseResult(
                HasCandidateToEnqueue: false,
                CandidateToEnqueue: default,
                RejectedCandidates: rejectedCandidates.ToArray(),
                CorridorAbort: default);
        }
    }

    public DeferredHelperRemoteFrameCandidate[] PurgeDeferredPostRecoveryCandidateIfStale(EncodedFrameDecodeRequest recoveryRequest)
    {
        lock (FollowerState.DeferredFollowerGate)
        {
            if (FollowerState.DeferredPostRecoveryCandidates.Count == 0)
            {
                return Array.Empty<DeferredHelperRemoteFrameCandidate>();
            }

            var staleFrameIds = FollowerState.DeferredPostRecoveryCandidates
                .Where(pair =>
                    pair.Value.StreamEpoch == recoveryRequest.StreamEpoch &&
                    pair.Value.FrameId >= 0 &&
                    recoveryRequest.FrameId >= 0 &&
                    pair.Value.FrameId <= recoveryRequest.FrameId)
                .Select(static pair => pair.Key)
                .ToArray();
            if (staleFrameIds.Length == 0)
            {
                return Array.Empty<DeferredHelperRemoteFrameCandidate>();
            }

            var purgedCandidates = new DeferredHelperRemoteFrameCandidate[staleFrameIds.Length];
            for (var i = 0; i < staleFrameIds.Length; i++)
            {
                var staleFrameId = staleFrameIds[i];
                purgedCandidates[i] = FollowerState.DeferredPostRecoveryCandidates[staleFrameId];
                FollowerState.DeferredPostRecoveryCandidates.Remove(staleFrameId);
            }

            return purgedCandidates;
        }
    }

    public void ClearPendingRecoveryRunwayAbort()
    {
        FollowerState.PendingRecoveryRunwayAbortActive = false;
        FollowerState.PendingRecoveryRunwayAbortEpoch = 0;
        FollowerState.PendingRecoveryRunwayAbortExpectedNextFrameId = -1;
        FollowerState.PendingRecoveryRunwayAbortReceivedFrameId = -1;
        FollowerState.PendingRecoveryRunwayAbortReason = string.Empty;
        FollowerState.PendingRecoveryRunwayAbortSetUtc = default;
    }

    public void SetPendingRecoveryRunwayAbort(
        long streamEpoch,
        long expectedNextFrameId,
        long receivedFrameId,
        string reason,
        DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0)
        {
            return;
        }

        FollowerState.PendingRecoveryRunwayAbortActive = true;
        FollowerState.PendingRecoveryRunwayAbortEpoch = streamEpoch;
        FollowerState.PendingRecoveryRunwayAbortExpectedNextFrameId = expectedNextFrameId;
        FollowerState.PendingRecoveryRunwayAbortReceivedFrameId = receivedFrameId;
        FollowerState.PendingRecoveryRunwayAbortReason = string.IsNullOrWhiteSpace(reason)
            ? "unknown"
            : reason.Trim();
        FollowerState.PendingRecoveryRunwayAbortSetUtc = nowUtc;
    }

    public HelperRemotePendingRecoveryRunwayAbortResult ConsumePendingRecoveryRunwayAbortForCorridorStart(
        long streamEpoch,
        DateTimeOffset nowUtc)
    {
        if (!FollowerState.PendingRecoveryRunwayAbortActive ||
            FollowerState.PendingRecoveryRunwayAbortEpoch != streamEpoch)
        {
            return default;
        }

        var result = new HelperRemotePendingRecoveryRunwayAbortResult(
            Matched: true,
            ExpectedNextFrameId: FollowerState.PendingRecoveryRunwayAbortExpectedNextFrameId,
            ReceivedFrameId: FollowerState.PendingRecoveryRunwayAbortReceivedFrameId,
            RecoveryFrameId: FollowerState.RecoveryProgressCorridorRecoveryFrameId,
            HoldMs: ComputeDurationMs(FollowerState.PendingRecoveryRunwayAbortSetUtc, nowUtc));
        VisibleProgressState.LastRecoveryRunwayAbortHoldMs = result.HoldMs;
        ClearPendingRecoveryRunwayAbort();
        return result;
    }

    public void ResetRecoveryProgressCorridor()
    {
        FollowerState.RecoveryProgressCorridorActive = false;
        FollowerState.RecoveryProgressCorridorEpoch = 0;
        FollowerState.RecoveryProgressCorridorRecoveryFrameId = -1;
        FollowerState.RecoveryProgressCorridorLastFrameId = -1;
        FollowerState.RecoveryProgressCorridorAppliedCount = 0;
        FollowerState.RecoveryProgressCorridorStartedUtc = default;
        FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc = default;
    }

    public void StartRecoveryProgressCorridor(long streamEpoch, long frameId, DateTimeOffset nowUtc)
    {
        if (streamEpoch <= 0 || frameId < 0)
        {
            return;
        }

        FollowerState.ExpiredRecoveryRunwayActive = false;
        FollowerState.ExpiredRecoveryRunwayEpoch = 0;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = default;
        FollowerState.RecoveryProgressCorridorActive = true;
        FollowerState.RecoveryProgressCorridorEpoch = streamEpoch;
        FollowerState.RecoveryProgressCorridorRecoveryFrameId = frameId;
        FollowerState.RecoveryProgressCorridorLastFrameId = frameId;
        FollowerState.RecoveryProgressCorridorAppliedCount = 1;
        FollowerState.RecoveryProgressCorridorStartedUtc = nowUtc;
        FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc = nowUtc;
        ClearPendingRecoveryRunwayAbort();
    }

    public HelperRemoteRecoveryProgressCorridorAbortResult AbortRecoveryProgressCorridor(string reason, DateTimeOffset nowUtc)
    {
        if (!FollowerState.RecoveryProgressCorridorActive)
        {
            return default;
        }

        var abortReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        var result = new HelperRemoteRecoveryProgressCorridorAbortResult(
            Aborted: true,
            StreamEpoch: FollowerState.RecoveryProgressCorridorEpoch,
            RecoveryFrameId: FollowerState.RecoveryProgressCorridorRecoveryFrameId,
            LastContiguousFrameId: FollowerState.RecoveryProgressCorridorLastFrameId,
            ContiguousFollowerApplyCount: Math.Max(0, FollowerState.RecoveryProgressCorridorAppliedCount - 1),
            Reason: abortReason,
            HoldMs: ComputeDurationMs(FollowerState.RecoveryProgressCorridorStartedUtc, nowUtc));
        VisibleProgressState.LastRecoveryProgressCorridorAbortReason = abortReason;
        VisibleProgressState.LastRecoveryProgressCorridorHoldMs = result.HoldMs;
        ResetRecoveryProgressCorridor();
        return result;
    }

    public HelperRemoteRecoveryProgressCorridorApplyResult ObserveRecoveryProgressCorridorApply(
        long streamEpoch,
        long frameId,
        DateTimeOffset nowUtc,
        int requiredContiguousFollowerApplyCount)
    {
        if (!FollowerState.RecoveryProgressCorridorActive)
        {
            return default;
        }

        if (streamEpoch != FollowerState.RecoveryProgressCorridorEpoch)
        {
            return new HelperRemoteRecoveryProgressCorridorApplyResult(
                Applied: false,
                Succeeded: false,
                Abort: AbortRecoveryProgressCorridor("epoch_changed", nowUtc),
                StreamEpoch: streamEpoch,
                RecoveryFrameId: -1,
                LastContiguousFrameId: -1,
                ContiguousFollowerApplyCount: 0,
                HoldMs: 0);
        }

        if (frameId < 0 || frameId <= FollowerState.RecoveryProgressCorridorLastFrameId)
        {
            return default;
        }

        if (frameId != FollowerState.RecoveryProgressCorridorLastFrameId + 1)
        {
            return new HelperRemoteRecoveryProgressCorridorApplyResult(
                Applied: false,
                Succeeded: false,
                Abort: AbortRecoveryProgressCorridor("non_contiguous_apply", nowUtc),
                StreamEpoch: streamEpoch,
                RecoveryFrameId: -1,
                LastContiguousFrameId: -1,
                ContiguousFollowerApplyCount: 0,
                HoldMs: 0);
        }

        FollowerState.RecoveryProgressCorridorLastFrameId = frameId;
        FollowerState.RecoveryProgressCorridorAppliedCount++;
        FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc = nowUtc;
        var result = new HelperRemoteRecoveryProgressCorridorApplyResult(
            Applied: true,
            Succeeded: FollowerState.RecoveryProgressCorridorAppliedCount >= requiredContiguousFollowerApplyCount + 1,
            Abort: default,
            StreamEpoch: streamEpoch,
            RecoveryFrameId: FollowerState.RecoveryProgressCorridorRecoveryFrameId,
            LastContiguousFrameId: FollowerState.RecoveryProgressCorridorLastFrameId,
            ContiguousFollowerApplyCount: Math.Max(0, FollowerState.RecoveryProgressCorridorAppliedCount - 1),
            HoldMs: ComputeDurationMs(FollowerState.RecoveryProgressCorridorStartedUtc, nowUtc));
        VisibleProgressState.LastRecoveryProgressCorridorHoldMs = result.HoldMs;
        if (result.Succeeded)
        {
            ResetRecoveryProgressCorridor();
        }

        return result;
    }

    public HelperRemoteRecoveryProgressCorridorAbortResult EnsureRecoveryProgressCorridorNotStalled(
        long streamEpoch,
        DateTimeOffset nowUtc,
        TimeSpan stallTimeout,
        int requiredContiguousFollowerApplyCount)
    {
        if (!FollowerState.RecoveryProgressCorridorActive ||
            FollowerState.RecoveryProgressCorridorEpoch != streamEpoch ||
            FollowerState.RecoveryProgressCorridorAppliedCount >= requiredContiguousFollowerApplyCount ||
            FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc == default)
        {
            return default;
        }

        if (nowUtc - FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc <= stallTimeout)
        {
            return default;
        }

        return AbortRecoveryProgressCorridor("timeout", nowUtc);
    }

    public void RecordDecodedVisibleFrame(EncodedFrameDecodeRequest request)
    {
        RecoveryState.NeedMoreInputBurstEpoch = request.StreamEpoch;
        RecoveryState.NeedMoreInputBurstCount = 0;
        RecoveryState.TrackedFrameEpoch = request.StreamEpoch;
        if (request.FrameId >= 0)
        {
            RecoveryState.LastSeenFrameId = request.FrameId;
            RecoveryState.LastCleanFrameId = request.FrameId;
            RecoveryState.VisibleHeadStreamEpoch = request.StreamEpoch;
            RecoveryState.VisibleHeadFrameId = request.FrameId;
        }

        if (request.IsKeyFrame)
        {
            RecoveryState.HasCleanKeyframeForEpoch = true;
        }
    }

    public void CompleteRecoveryAfterVisibleResync()
    {
        RecoveryState.RecoveryActive = false;
        RecoveryState.RecoveryReason = string.Empty;
        RecoveryState.RecoveryExpectedNextFrameId = -1;
        RecoveryState.RecoveryReceivedFrameId = -1;
        FollowerState.PostRecoveryStabilizationEpoch = 0;
        FollowerState.PostRecoveryReservedAppliesRemaining = 0;
        FollowerState.ExpiredRecoveryRunwayActive = false;
        FollowerState.ExpiredRecoveryRunwayEpoch = 0;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = default;
        ClearPendingRecoveryRunwayAbort();
        ResetRecoveryProgressCorridor();
    }

    public void ResetPostRecoveryStabilization()
    {
        FollowerState.PostRecoveryStabilizationEpoch = 0;
        FollowerState.PostRecoveryReservedAppliesRemaining = 0;
    }

    public void ExpireRecoveryRunwayWindow(long streamEpoch, long recoveryFrameId, DateTimeOffset nowUtc)
    {
        FollowerState.ExpiredRecoveryRunwayActive = streamEpoch > 0;
        FollowerState.ExpiredRecoveryRunwayEpoch = streamEpoch;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = FollowerState.RecoveryProgressCorridorLastFrameId;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = recoveryFrameId >= 0
            ? recoveryFrameId + 2
            : -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = nowUtc;
    }

    public int RecordNeedMoreInput(long streamEpoch)
    {
        if (RecoveryState.NeedMoreInputBurstEpoch != streamEpoch)
        {
            RecoveryState.NeedMoreInputBurstEpoch = streamEpoch;
            RecoveryState.NeedMoreInputBurstCount = 0;
        }
        else
        {
            RecoveryState.NeedMoreInputBurstCount++;
        }

        return RecoveryState.NeedMoreInputBurstCount;
    }

    public void ResetState()
    {
        RecoveryState.VisibleHeadStreamEpoch = 0;
        RecoveryState.VisibleHeadFrameId = -1;
        RecoveryState.NeedMoreInputBurstEpoch = 0;
        RecoveryState.NeedMoreInputBurstCount = 0;
        RecoveryState.RecoveryActive = false;
        RecoveryState.RecoveryStreamEpoch = 0;
        RecoveryState.RecoveryReason = string.Empty;
        RecoveryState.TrackedFrameEpoch = 0;
        RecoveryState.LastSeenFrameId = -1;
        RecoveryState.LastCleanFrameId = -1;
        RecoveryState.HasCleanKeyframeForEpoch = false;
        RecoveryState.RecoveryExpectedNextFrameId = -1;
        RecoveryState.RecoveryReceivedFrameId = -1;
        RecoveryState.SessionId = string.Empty;

        FollowerState.ReservedApplyActive = false;
        FollowerState.ReservedApplyStreamEpoch = -1;
        FollowerState.ReservedApplyFrameId = -1;
        FollowerState.ReservedApplyPendingSinceUtc = default;
        FollowerState.StartupKeyframePendingVisibleApplyActive = false;
        FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch = -1;
        FollowerState.StartupKeyframePendingVisibleApplyFrameId = -1;
        FollowerState.StartupKeyframePendingVisibleApplyPendingSinceUtc = default;
        FollowerState.ExpiredRecoveryRunwayActive = false;
        FollowerState.ExpiredRecoveryRunwayEpoch = 0;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = default;
        FollowerState.DeferredPostRecoveryCandidateSequence = 0;
        FollowerState.PostRecoveryStabilizationEpoch = 0;
        FollowerState.PostRecoveryReservedAppliesRemaining = 0;
        FollowerState.RecoveryProgressCorridorLastVisibleApplyUtc = default;
        FollowerState.PendingRecoveryRunwayAbortSetUtc = default;
        ClearPendingRecoveryRunwayAbort();
        ResetRecoveryProgressCorridor();
        lock (FollowerState.DeferredFollowerGate)
        {
            FollowerState.DeferredPostRecoveryCandidates.Clear();
        }

        VisibleProgressState.LastReservedApplyHoldMs = 0;
        VisibleProgressState.LastRecoveryProgressCorridorHoldMs = 0;
        VisibleProgressState.LastRecoveryRunwayAbortHoldMs = 0;
        VisibleProgressState.LastRecoveryProgressCorridorAbortReason = "none";
    }

    public ScreenShareViewerFrameGapObservation? ObserveFrameGapContinuityLoss(long streamEpoch, long frameId, bool isKeyFrame)
    {
        if (streamEpoch <= 0 || frameId < 0)
        {
            return null;
        }

        if (RecoveryState.TrackedFrameEpoch != streamEpoch)
        {
            ResetFrameTracking(streamEpoch);
            if (!isKeyFrame)
            {
                RecoveryState.RecoveryReceivedFrameId = frameId;
                RecoveryState.LastSeenFrameId = frameId;
                return new ScreenShareViewerFrameGapObservation(
                    ExpectedNextFrameId: 0,
                    ReceivedFrameId: frameId,
                    LastCleanFrameId: RecoveryState.LastCleanFrameId);
            }

            RecoveryState.LastSeenFrameId = frameId;
            return null;
        }

        if (RecoveryState.RecoveryActive && RecoveryState.RecoveryStreamEpoch == streamEpoch)
        {
            RecoveryState.RecoveryReceivedFrameId = frameId;
            return null;
        }

        var expectedNextFrameId = RecoveryState.LastSeenFrameId >= 0
            ? RecoveryState.LastSeenFrameId + 1
            : 0;

        if (!RecoveryState.HasCleanKeyframeForEpoch && !isKeyFrame)
        {
            if (FollowerState.StartupKeyframePendingVisibleApplyActive &&
                FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch == streamEpoch &&
                frameId > FollowerState.StartupKeyframePendingVisibleApplyFrameId)
            {
                RecoveryState.RecoveryReceivedFrameId = frameId;
                RecoveryState.LastSeenFrameId = Math.Max(RecoveryState.LastSeenFrameId, frameId);
                return null;
            }

            RecoveryState.RecoveryReceivedFrameId = frameId;
            RecoveryState.LastSeenFrameId = frameId;
            return new ScreenShareViewerFrameGapObservation(
                ExpectedNextFrameId: expectedNextFrameId,
                ReceivedFrameId: frameId,
                LastCleanFrameId: RecoveryState.LastCleanFrameId);
        }

        if (!isKeyFrame &&
            RecoveryState.LastSeenFrameId >= 0 &&
            frameId > expectedNextFrameId)
        {
            RecoveryState.RecoveryReceivedFrameId = frameId;
            RecoveryState.LastSeenFrameId = frameId;
            return new ScreenShareViewerFrameGapObservation(
                ExpectedNextFrameId: expectedNextFrameId,
                ReceivedFrameId: frameId,
                LastCleanFrameId: RecoveryState.LastCleanFrameId);
        }

        RecoveryState.LastSeenFrameId = Math.Max(RecoveryState.LastSeenFrameId, frameId);
        return null;
    }

    public void ResetFrameTracking(long streamEpoch)
    {
        RecoveryState.TrackedFrameEpoch = Math.Max(0, streamEpoch);
        RecoveryState.LastSeenFrameId = -1;
        RecoveryState.LastCleanFrameId = -1;
        RecoveryState.VisibleHeadStreamEpoch = Math.Max(0, streamEpoch);
        RecoveryState.VisibleHeadFrameId = -1;
        RecoveryState.HasCleanKeyframeForEpoch = false;
        RecoveryState.RecoveryExpectedNextFrameId = -1;
        RecoveryState.RecoveryReceivedFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayActive = false;
        FollowerState.ExpiredRecoveryRunwayEpoch = 0;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = default;
        ClearPendingRecoveryRunwayAbort();
        ClearReservedApplyThroughEpoch(streamEpoch);
        lock (FollowerState.DeferredFollowerGate)
        {
            var staleFrameIds = FollowerState.DeferredPostRecoveryCandidates
                .Where(pair => pair.Value.StreamEpoch != streamEpoch)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleFrameId in staleFrameIds)
            {
                FollowerState.DeferredPostRecoveryCandidates.Remove(staleFrameId);
            }
        }
    }

    public HelperRemoteRecoveryActivationResult ActivateRecovery(
        string reason,
        long streamEpoch,
        long expectedNextFrameId = -1,
        long receivedFrameId = -1,
        long lastCleanFrameId = -1)
    {
        var newlyActive =
            !RecoveryState.RecoveryActive ||
            RecoveryState.RecoveryStreamEpoch != streamEpoch ||
            !string.Equals(RecoveryState.RecoveryReason, reason, StringComparison.Ordinal);

        RecoveryState.RecoveryActive = true;
        RecoveryState.RecoveryStreamEpoch = streamEpoch;
        RecoveryState.RecoveryReason = reason;
        RecoveryState.RecoveryExpectedNextFrameId = expectedNextFrameId >= 0
            ? expectedNextFrameId
            : (newlyActive ? -1 : RecoveryState.RecoveryExpectedNextFrameId);
        RecoveryState.RecoveryReceivedFrameId = receivedFrameId >= 0
            ? receivedFrameId
            : (newlyActive ? -1 : RecoveryState.RecoveryReceivedFrameId);
        if (lastCleanFrameId >= 0)
        {
            RecoveryState.LastCleanFrameId = lastCleanFrameId;
        }

        RecoveryState.NeedMoreInputBurstEpoch = streamEpoch;
        RecoveryState.NeedMoreInputBurstCount = 0;
        FollowerState.PostRecoveryStabilizationEpoch = 0;
        FollowerState.PostRecoveryReservedAppliesRemaining = 0;
        FollowerState.ExpiredRecoveryRunwayActive = false;
        FollowerState.ExpiredRecoveryRunwayEpoch = 0;
        FollowerState.ExpiredRecoveryRunwayLastContiguousFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayMaximumFrameId = -1;
        FollowerState.ExpiredRecoveryRunwayStartedUtc = default;
        ClearPendingRecoveryRunwayAbort();
        ResetRecoveryProgressCorridor();
        var purgedDeferredCandidateCount = 0;
        lock (FollowerState.DeferredFollowerGate)
        {
            var staleFrameIds = FollowerState.DeferredPostRecoveryCandidates
                .Where(pair => pair.Value.StreamEpoch != streamEpoch || newlyActive)
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleFrameId in staleFrameIds)
            {
                purgedDeferredCandidateCount++;
                FollowerState.DeferredPostRecoveryCandidates.Remove(staleFrameId);
            }
        }

        return new HelperRemoteRecoveryActivationResult(
            newlyActive,
            purgedDeferredCandidateCount,
            RecoveryState.RecoveryExpectedNextFrameId,
            RecoveryState.RecoveryReceivedFrameId,
            RecoveryState.LastCleanFrameId);
    }

    public string? TryRejectFrameBeforeDecode(
        string sessionId,
        string encoding,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
    {
        if (!context.IsHelperRemoteH264(encoding))
        {
            return null;
        }

        var rejectionReason = context.ResolveHelperRemotePreDecodeRejectionReason(
            sessionId,
            streamEpoch,
            frameId,
            isKeyFrame,
            recoveryDeliveryClass);
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            return null;
        }

        if (string.Equals(rejectionReason, "waiting_for_recovery_keyframe", StringComparison.Ordinal) &&
            ShouldTreatCurrentEpochAsProofStable(sessionId, streamEpoch, frameId))
        {
            return null;
        }

        if (string.Equals(rejectionReason, "waiting_for_recovery_keyframe", StringComparison.Ordinal))
        {
            context.IncrementFramesDroppedWaitingForRecoveryKeyframe();
            context.IncrementPreCandidateGapTailEmittedToViewerCount();
        }

        if (string.Equals(rejectionReason, "waiting_for_recovery_keyframe", StringComparison.Ordinal) &&
            string.Equals(RecoveryState.RecoveryReason, "frame_gap", StringComparison.Ordinal) &&
            streamEpoch == RecoveryState.RecoveryStreamEpoch)
        {
            context.IncrementFramesDroppedForFrameGap();
        }

        context.ObserveViewerRejectedBeforeEnqueue(
            sessionId,
            encoding,
            streamEpoch,
            frameId,
            isKeyFrame,
            rejectionReason);
        return rejectionReason;
    }

    private bool ShouldTreatCurrentEpochAsProofStable(string? sessionId, long streamEpoch, long frameId)
    {
        if (streamEpoch <= 0 || frameId < 0)
        {
            return false;
        }

        var effectiveSessionId = context.GetEffectiveHelperRemoteSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = SessionId;
        }

        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            return false;
        }

        var helperSnapshot = BuildSessionSnapshot(ScreenShareFrameLossAttributionRegistry.GetSnapshot(effectiveSessionId));
        if (helperSnapshot.CurrentEpoch != streamEpoch ||
            helperSnapshot.Phase != HelperRemoteSessionPhase.VisibleStable ||
            !helperSnapshot.SteadyVisibleProgressActive ||
            helperSnapshot.RecoveryActive ||
            helperSnapshot.RecoveryCorridorActive ||
            helperSnapshot.RunwayCleanupActive ||
            helperSnapshot.PostRecoveryStabilizationActive)
        {
            return false;
        }

        var provenHeadFrameId = Math.Max(
            Math.Max(helperSnapshot.VisibleHeadFrameId, helperSnapshot.AppliedHeadFrameId),
            Math.Max(helperSnapshot.StableVisibleHeadFrameId, helperSnapshot.ProvenHeadFrameId));
        if (provenHeadFrameId < 0)
        {
            provenHeadFrameId = RecoveryState.LastCleanFrameId;
        }

        return provenHeadFrameId >= 0 && frameId <= provenHeadFrameId + 1;
    }

    public void OnFrameAppliedVisible(EncodedFrameDecodeRequest request)
    {
        if (!context.IsHelperRemoteH264(request.Encoding))
        {
            return;
        }

        ClearReservedApplyIfMatch(request);

        if (FollowerState.PostRecoveryStabilizationEpoch == request.StreamEpoch &&
            FollowerState.PostRecoveryReservedAppliesRemaining > 0 &&
            !request.IsKeyFrame)
        {
            FollowerState.PostRecoveryReservedAppliesRemaining--;
        }

        if (FollowerState.PostRecoveryStabilizationEpoch == request.StreamEpoch &&
            FollowerState.PostRecoveryReservedAppliesRemaining <= 0 &&
            GetDeferredPostRecoveryCandidateCount() == 0)
        {
            FollowerState.PostRecoveryReservedAppliesRemaining = 0;
            FollowerState.PostRecoveryStabilizationEpoch = 0;
        }
    }

    public HelperRemoteVisibleApplyProgress BuildVisibleApplyProgress(EncodedFrameDecodeRequest request)
    {
        if (!context.IsHelperRemoteH264(request.Encoding))
        {
            return new HelperRemoteVisibleApplyProgress(-1L, -1L, 0L, -1L);
        }

        var effectiveSessionId = context.GetEffectiveHelperRemoteSessionId(request.SessionId);

        ScreenShareEpochDiagnosticsSnapshot? epochDiagnostics = null;
        if (!string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(effectiveSessionId);
            epochDiagnostics = snapshot.EpochDiagnostics.FirstOrDefault(epoch => epoch.StreamEpoch == request.StreamEpoch);
        }

        var visibleHeadFrameId =
            RecoveryState.VisibleHeadStreamEpoch == request.StreamEpoch
                ? RecoveryState.VisibleHeadFrameId
                : request.FrameId;

        return new HelperRemoteVisibleApplyProgress(
            visibleHeadFrameId,
            epochDiagnostics?.StableVisibleHeadFrameId ?? -1L,
            epochDiagnostics?.FramesAppliedSinceLastGap ?? 0L,
            epochDiagnostics?.AppliedHeadFrameId ?? -1L);
    }

    public bool ShouldForceRecoveryOnce(long streamEpoch, long frameId)
    {
        if (context.ForcedHelperRemoteRecoveryTriggered ||
            context.ForcedHelperRemoteRecoveryAfterApplies < 0 ||
            streamEpoch <= 0 ||
            !string.Equals(context.LogRole, "helper_remote", StringComparison.Ordinal))
        {
            return false;
        }

        if (context.FramesApplied < context.ForcedHelperRemoteRecoveryAfterApplies)
        {
            return false;
        }

        context.ForcedHelperRemoteRecoveryTriggered = true;
        context.LogScreenShareInfo(
            $"event=screenshare_forced_helper_remote_recovery_triggered; role={context.LogRole}; stream_epoch={streamEpoch}; frame_id={FormatFrameIdForLog(frameId)}; after_applies={context.ForcedHelperRemoteRecoveryAfterApplies.ToString(CultureInfo.InvariantCulture)}; last_clean_frame_id={FormatFrameIdForLog(RecoveryState.LastCleanFrameId)}");
        return true;
    }

    public HelperRemoteSessionSnapshot BuildSessionSnapshot(ScreenShareFrameLossSessionSnapshot attributionSnapshot)
    {
        var currentEpoch = ResolveCurrentEpoch(attributionSnapshot);
        var currentEpochDiagnostics = currentEpoch > 0
            ? attributionSnapshot.EpochDiagnostics.FirstOrDefault(epoch => epoch.StreamEpoch == currentEpoch)
            : null;
        var recoveryMechanism = ResolveRecoveryMechanism();
        var visibleHeadFrameId = Math.Max(
            currentEpochDiagnostics?.VisibleHeadFrameId ?? -1L,
            RecoveryState.VisibleHeadFrameId >= 0 &&
            (RecoveryState.VisibleHeadStreamEpoch <= 0 || RecoveryState.VisibleHeadStreamEpoch == currentEpoch)
                ? RecoveryState.VisibleHeadFrameId
                : -1L);
        var appliedHeadFrameId = currentEpochDiagnostics?.AppliedHeadFrameId ?? -1L;
        var stableVisibleHeadFrameId = currentEpochDiagnostics?.StableVisibleHeadFrameId ?? -1L;
        var visibleRecoveryFloorFrameId = currentEpochDiagnostics?.VisibleRecoveryFloorFrameId ?? -1L;
        var framesAppliedSinceLastGap = Math.Max(0L, currentEpochDiagnostics?.FramesAppliedSinceLastGap ?? 0L);
        var recoveryActive =
            RecoveryState.RecoveryActive &&
            (RecoveryState.RecoveryStreamEpoch <= 0 || RecoveryState.RecoveryStreamEpoch == currentEpoch);
        var recoveryCorridorActive =
            FollowerState.RecoveryProgressCorridorActive &&
            (FollowerState.RecoveryProgressCorridorEpoch <= 0 || FollowerState.RecoveryProgressCorridorEpoch == currentEpoch);
        var runwayCleanupActive =
            (FollowerState.ExpiredRecoveryRunwayActive &&
             (FollowerState.ExpiredRecoveryRunwayEpoch <= 0 || FollowerState.ExpiredRecoveryRunwayEpoch == currentEpoch)) ||
            (FollowerState.PendingRecoveryRunwayAbortActive &&
             (FollowerState.PendingRecoveryRunwayAbortEpoch <= 0 || FollowerState.PendingRecoveryRunwayAbortEpoch == currentEpoch));
        var postRecoveryStabilizationActive =
            FollowerState.PostRecoveryStabilizationEpoch > 0 &&
            FollowerState.PostRecoveryStabilizationEpoch == currentEpoch &&
            (FollowerState.PostRecoveryReservedAppliesRemaining > 0 || GetDeferredPostRecoveryCandidateCount() > 0);
        var currentEpochProgressProof = ComputeCurrentEpochProgressProof(
            visibleHeadFrameId,
            appliedHeadFrameId,
            stableVisibleHeadFrameId,
            visibleRecoveryFloorFrameId,
            framesAppliedSinceLastGap);
        var baselineEstablished =
            visibleHeadFrameId >= 0 ||
            appliedHeadFrameId >= 0 ||
            stableVisibleHeadFrameId >= 0 ||
            currentEpochProgressProof.Active;
        var realRecoveryActive =
            recoveryActive ||
            recoveryCorridorActive ||
            runwayCleanupActive ||
            recoveryMechanism != HelperRemoteRecoveryMechanism.None;
        var steadyVisibleProgressActive =
            baselineEstablished &&
            currentEpochProgressProof.Active &&
            !realRecoveryActive;
        var stalled =
            baselineEstablished &&
            RecoveryState.NeedMoreInputBurstCount >= HelperRemoteNeedMoreInputStallThreshold &&
            !currentEpochProgressProof.Active &&
            !realRecoveryActive;
        var phase =
            !baselineEstablished && !realRecoveryActive
                ? HelperRemoteSessionPhase.NoVisibleBaseline
                : realRecoveryActive
                    ? HelperRemoteSessionPhase.Recovering
                    : stalled
                    ? HelperRemoteSessionPhase.Stalled
                    : HelperRemoteSessionPhase.VisibleStable;

        return new HelperRemoteSessionSnapshot(
            currentEpoch,
            phase,
            recoveryMechanism,
            baselineEstablished,
            steadyVisibleProgressActive,
            visibleHeadFrameId,
            appliedHeadFrameId,
            stableVisibleHeadFrameId,
            visibleRecoveryFloorFrameId,
            currentEpochProgressProof.ProofFrameId,
            currentEpochProgressProof.FramesAppliedSinceLastGap,
            currentEpochProgressProof.Active,
            currentEpochProgressProof.Source,
            recoveryActive,
            recoveryCorridorActive,
            runwayCleanupActive,
            postRecoveryStabilizationActive);
    }

    private HelperRemoteRecoveryMechanism ResolveRecoveryMechanism()
    {
        if (FollowerState.RecoveryProgressCorridorActive)
        {
            return HelperRemoteRecoveryMechanism.RecoveryCorridor;
        }

        if (FollowerState.ExpiredRecoveryRunwayActive || FollowerState.PendingRecoveryRunwayAbortActive)
        {
            return HelperRemoteRecoveryMechanism.RunwayCleanup;
        }

        if (FollowerState.ReservedApplyActive || FollowerState.StartupKeyframePendingVisibleApplyActive)
        {
            return HelperRemoteRecoveryMechanism.ReservedApply;
        }

        if (FollowerState.PostRecoveryStabilizationEpoch > 0 || FollowerState.DeferredPostRecoveryCandidates.Count > 0)
        {
            return HelperRemoteRecoveryMechanism.FollowerWindow;
        }

        if (RecoveryState.RecoveryActive)
        {
            return HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe;
        }

        return HelperRemoteRecoveryMechanism.None;
    }

    private static string FormatFrameIdForLog(long frameId)
    {
        return frameId >= 0
            ? frameId.ToString(CultureInfo.InvariantCulture)
            : "(none)";
    }

    private static long ComputeDurationMs(DateTimeOffset startedUtc, DateTimeOffset nowUtc)
    {
        if (startedUtc == default || nowUtc < startedUtc)
        {
            return 0;
        }

        return Math.Max(0L, (long)(nowUtc - startedUtc).TotalMilliseconds);
    }

    private long ResolveCurrentEpoch(ScreenShareFrameLossSessionSnapshot attributionSnapshot)
    {
        var currentEpoch = 0L;
        foreach (var epochDiagnostics in attributionSnapshot.EpochDiagnostics)
        {
            if (epochDiagnostics.StreamEpoch > currentEpoch)
            {
                currentEpoch = epochDiagnostics.StreamEpoch;
            }
        }

        currentEpoch = Math.Max(
            currentEpoch,
            RecoveryState.VisibleHeadStreamEpoch > 0
                ? RecoveryState.VisibleHeadStreamEpoch
                : RecoveryState.VisibleHeadFrameId >= 0
                    ? RecoveryState.TrackedFrameEpoch
                    : 0L);
        currentEpoch = Math.Max(currentEpoch, RecoveryState.RecoveryStreamEpoch);
        currentEpoch = Math.Max(currentEpoch, RecoveryState.TrackedFrameEpoch);
        currentEpoch = Math.Max(
            currentEpoch,
            FollowerState.ReservedApplyActive ? FollowerState.ReservedApplyStreamEpoch : 0L);
        currentEpoch = Math.Max(
            currentEpoch,
            FollowerState.StartupKeyframePendingVisibleApplyActive
                ? FollowerState.StartupKeyframePendingVisibleApplyStreamEpoch
                : 0L);
        currentEpoch = Math.Max(currentEpoch, FollowerState.PostRecoveryStabilizationEpoch);
        currentEpoch = Math.Max(
            currentEpoch,
            FollowerState.RecoveryProgressCorridorActive ? FollowerState.RecoveryProgressCorridorEpoch : 0L);
        currentEpoch = Math.Max(
            currentEpoch,
            FollowerState.ExpiredRecoveryRunwayActive ? FollowerState.ExpiredRecoveryRunwayEpoch : 0L);
        currentEpoch = Math.Max(
            currentEpoch,
            FollowerState.PendingRecoveryRunwayAbortActive ? FollowerState.PendingRecoveryRunwayAbortEpoch : 0L);
        return currentEpoch;
    }

    private static HelperRemoteCurrentEpochProgressProof ComputeCurrentEpochProgressProof(
        long visibleHeadFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId,
        long visibleRecoveryFloorFrameId,
        long framesAppliedSinceLastGap)
    {
        var normalizedFramesAppliedSinceLastGap = Math.Max(0L, framesAppliedSinceLastGap);
        var provenHeadFrameId = Math.Max(
            Math.Max(visibleHeadFrameId, appliedHeadFrameId),
            stableVisibleHeadFrameId);
        if (visibleRecoveryFloorFrameId >= 0 && provenHeadFrameId >= visibleRecoveryFloorFrameId)
        {
            return new HelperRemoteCurrentEpochProgressProof(
                true,
                "recovery_floor_plus_head",
                provenHeadFrameId,
                Math.Max(normalizedFramesAppliedSinceLastGap, provenHeadFrameId - visibleRecoveryFloorFrameId + 1));
        }

        if (stableVisibleHeadFrameId >= 0)
        {
            return new HelperRemoteCurrentEpochProgressProof(
                true,
                "stable_visible_head",
                stableVisibleHeadFrameId,
                Math.Max(1L, normalizedFramesAppliedSinceLastGap));
        }

        if (appliedHeadFrameId >= 0)
        {
            return new HelperRemoteCurrentEpochProgressProof(
                true,
                "applied_head",
                appliedHeadFrameId,
                Math.Max(1L, normalizedFramesAppliedSinceLastGap));
        }

        if (visibleHeadFrameId >= 0)
        {
            return new HelperRemoteCurrentEpochProgressProof(
                true,
                "visible_head",
                visibleHeadFrameId,
                Math.Max(1L, normalizedFramesAppliedSinceLastGap));
        }

        return new HelperRemoteCurrentEpochProgressProof(
            false,
            "none",
            -1L,
            normalizedFramesAppliedSinceLastGap);
    }
}
