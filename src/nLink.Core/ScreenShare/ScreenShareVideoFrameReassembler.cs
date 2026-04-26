using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.Core.ScreenShare;

public sealed class ScreenShareVideoFrameReassembler
{
    public const int MaxInFlightAssembliesPerSession = 8;
    public const int MaxReadyFramesPerSession = 12;
    public const int MaxFutureNonKeyFramesWhileGapActive = 0;
    public const int MaxBufferedRecoveryKeyframesWhileGapActive = 1;
    public const int MaxRecoveryFollowerFramesWhileGapActive = 0;
    public const int MaxInFlightFramesPerSession = MaxInFlightAssembliesPerSession;
    public const int MaxFragmentCount = 128;
    public const int MaxAssembledFrameBytes = 2_000_000;

    private static readonly TimeSpan RecoveryKeyframeRequestMinimumInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, SessionAssemblyState> sessions = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> nowProvider;
    private long framesCompleted;
    private long framesDropped;
    private long framesSuperseded;
    private long framesRejectedOversize;
    private long assembliesExpired;

    public ScreenShareVideoFrameReassembler()
        : this(nowProvider: null)
    {
    }

    internal ScreenShareVideoFrameReassembler(Func<DateTimeOffset>? nowProvider)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<ScreenShareVideoFrameReadyEventArgs>? FrameReady;
    public event EventHandler<ScreenShareVideoKeyframeRequestV1>? KeyframeRequested;

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesDropped: Interlocked.Read(ref framesDropped),
            FramesCompleted: Interlocked.Read(ref framesCompleted),
            FramesSuperseded: Interlocked.Read(ref framesSuperseded),
            FramesRejectedOversize: Interlocked.Read(ref framesRejectedOversize),
            FreshnessMode: "latest_only");
    }

    public long AssembliesExpired => Interlocked.Read(ref assembliesExpired);

    public void OnStreamConfig(ScreenShareVideoStreamConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sessionId = config.SessionId.Trim();
        if (string.IsNullOrWhiteSpace(sessionId) || config.StreamEpoch <= 0)
        {
            return;
        }

        if (!sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionAssemblyState();
            sessions.Add(sessionId, session);
        }

        if (config.StreamEpoch < session.CurrentStreamEpoch)
        {
            return;
        }

        if (config.StreamEpoch > session.CurrentStreamEpoch)
        {
            var previousStreamEpoch = session.CurrentStreamEpoch;
            foreach (var staleFrameId in session.InFlightFrames.Keys.ToArray())
            {
                ScreenShareFrameLossAttributionRegistry.ObserveOlderEpochCleanupAfterEpochAdvance(
                    sessionId,
                    previousStreamEpoch,
                    staleFrameId,
                    config.StreamEpoch,
                    "epoch_advance_purge");
            }

            foreach (var staleFrameId in session.ReadyFrames.Keys.ToArray())
            {
                ScreenShareFrameLossAttributionRegistry.ObserveOlderEpochCleanupAfterEpochAdvance(
                    sessionId,
                    previousStreamEpoch,
                    staleFrameId,
                    config.StreamEpoch,
                    "epoch_advance_purge");
            }

            session.InFlightFrames.Clear();
            session.ReadyFrames.Clear();
            session.LastEmittedFrameId = -1;
            session.SupersededRecoveryTailFloorFrameId = -1;
            session.WinningRecoveryOwnerActive = false;
            session.WinningRecoveryFrameId = -1;
            session.OrderedEmitHeadFrameId = -1;
            session.ExpiredRecoveryRunwayOwnerFrameId = -1;
            ResetRecoveryRunway(session);
            ResetGapState(session);
        }

        session.CurrentStreamEpoch = config.StreamEpoch;
        session.CurrentStreamConfig = config;
        session.EmitConfigOnNextCompletedFrame = true;
        UpdateGapStateSnapshot(sessionId, session);
    }

    public void OnFragment(ScreenShareVideoFragmentV1 fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        if (!TryValidateFragment(fragment, out var oversizeRejected))
        {
            if (oversizeRejected)
            {
                Interlocked.Increment(ref framesRejectedOversize);
                ObserveAssemblyEvictedWithCause(fragment.SessionId, session: null, fragment.StreamEpoch, fragment.FrameId, "fragment_oversize");
            }

            return;
        }

        var sessionId = fragment.SessionId.Trim();
        ScreenShareFrameLossAttributionRegistry.ObserveFragmentSeen(sessionId, fragment.StreamEpoch, fragment.FrameId, fragment.IsKeyFrame);
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionAssemblyState();
            sessions.Add(sessionId, session);
        }

        if (session.CurrentStreamConfig is null || fragment.StreamEpoch > session.CurrentStreamEpoch)
        {
            RequestKeyframe(sessionId, fragment.StreamEpoch, "stream_config_missing");
            return;
        }

        if (fragment.StreamEpoch < session.CurrentStreamEpoch)
        {
            Interlocked.Increment(ref framesDropped);
            ScreenShareFrameLossAttributionRegistry.ObserveOlderEpochCleanupAfterEpochAdvance(
                sessionId,
                fragment.StreamEpoch,
                fragment.FrameId,
                session.CurrentStreamEpoch,
                "incoming_fragment");
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        if (TryDropFragmentBehindProvenHeadFloor(sessionId, session, fragment))
        {
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        if (fragment.FrameId <= session.LastEmittedFrameId)
        {
            Interlocked.Increment(ref framesDropped);
            var reason = ResolveLateFragmentReason(sessionId, session, fragment.StreamEpoch, fragment.FrameId);
            ScreenShareFrameLossAttributionRegistry.ObserveReassemblerStaleSuperseded(
                sessionId,
                fragment.StreamEpoch,
                fragment.FrameId,
                supersededByFrameId: session.LastEmittedFrameId,
                fragment.IsKeyFrame,
                reason: reason);
            ObserveReassemblerRootCause(
                sessionId,
                session,
                fragment.StreamEpoch,
                fragment.FrameId,
                reason);
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        HandleFragmentCore(sessionId, session, fragment);
    }

    public void ClearSession(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sessions.Remove(sessionId.Trim());
        }
    }

    public void ClearAll() => sessions.Clear();

    private void HandleFragmentCore(string sessionId, SessionAssemblyState session, ScreenShareVideoFragmentV1 fragment)
    {
        var expectedNextFrameId = GetExpectedNextFrameId(session);
        if (fragment.FrameId > expectedNextFrameId &&
            !session.InFlightFrames.ContainsKey(expectedNextFrameId) &&
            !session.ReadyFrames.ContainsKey(expectedNextFrameId))
        {
            EnterGapStateIfNeeded(sessionId, session, expectedNextFrameId, fragment.FrameId);
        }

        if (session.GapActive && fragment.FrameId >= session.GapExpectedFrameId)
        {
            if (fragment.IsKeyFrame)
            {
                if (!TryHandleIncomingRecoveryKeyframe(sessionId, session, fragment))
                {
                    UpdateGapStateSnapshot(sessionId, session);
                    return;
                }
            }
            else if (!session.InFlightFrames.ContainsKey(fragment.FrameId) &&
                     !session.ReadyFrames.ContainsKey(fragment.FrameId) &&
                     !TryPrepareForIncomingFutureNonKey(sessionId, session, fragment))
            {
                UpdateGapStateSnapshot(sessionId, session);
                return;
            }
        }

        if (!session.InFlightFrames.TryGetValue(fragment.FrameId, out var assembly))
        {
            assembly = new AssemblyState(
                fragment.SessionId.Trim(),
                fragment.StreamEpoch,
                fragment.FrameId,
                fragment.Width,
                fragment.Height,
                fragment.CapturedTsUtcMs,
                fragment.Encoding.Trim(),
                fragment.IsKeyFrame,
                fragment.FragmentCount);
            session.InFlightFrames.Add(fragment.FrameId, assembly);
        if (session.GapActive &&
            fragment.IsKeyFrame &&
            fragment.FrameId > session.GapExpectedFrameId)
        {
            TrackBufferedRecoveryKeyframe(sessionId, session, fragment.StreamEpoch, fragment.FrameId);
        }
        }
        else if (!assembly.Matches(fragment))
        {
            session.InFlightFrames.Remove(fragment.FrameId);
            Interlocked.Increment(ref framesDropped);
            Interlocked.Increment(ref assembliesExpired);
            ObserveAssemblyEvictedWithCause(sessionId, session, fragment.StreamEpoch, fragment.FrameId, "assembly_mismatch", fragment.IsKeyFrame);
            RequestKeyframe(sessionId, fragment.StreamEpoch, "assembly_mismatch");
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        if (assembly.FragmentBytes[fragment.FragmentIndex] is not null)
        {
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        var fragmentAcceptedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        assembly.FragmentBytes[fragment.FragmentIndex] = fragment.Data;
        assembly.ReceivedFragmentCount++;
        assembly.TotalBytes += fragment.Data.Length;
        ScreenShareFrameLossAttributionRegistry.ObserveAcceptedFragment(
            sessionId,
            fragment.StreamEpoch,
            fragment.FrameId,
            fragment.IsKeyFrame,
            fragmentAcceptedUtcMs);

        if (assembly.TotalBytes > MaxAssembledFrameBytes)
        {
            session.InFlightFrames.Remove(fragment.FrameId);
            Interlocked.Increment(ref framesDropped);
            Interlocked.Increment(ref framesRejectedOversize);
            ObserveAssemblyEvictedWithCause(sessionId, session, fragment.StreamEpoch, fragment.FrameId, "assembly_oversize", fragment.IsKeyFrame);
            RequestKeyframe(sessionId, fragment.StreamEpoch, "assembly_oversize");
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        TrimBufferedState(sessionId, session);

        if (assembly.ReceivedFragmentCount != assembly.FragmentCount)
        {
            UpdateGapStateSnapshot(sessionId, session);
            return;
        }

        var frameBytes = new byte[assembly.TotalBytes];
        var offset = 0;
        for (var i = 0; i < assembly.FragmentCount; i++)
        {
            var bytes = assembly.FragmentBytes[i];
            if (bytes is null)
            {
                session.InFlightFrames.Remove(fragment.FrameId);
                Interlocked.Increment(ref framesDropped);
                Interlocked.Increment(ref assembliesExpired);
                ObserveAssemblyEvictedWithCause(sessionId, session, fragment.StreamEpoch, fragment.FrameId, "assembly_incomplete", fragment.IsKeyFrame);
                RequestKeyframe(sessionId, fragment.StreamEpoch, "assembly_incomplete");
                UpdateGapStateSnapshot(sessionId, session);
                return;
            }

            Buffer.BlockCopy(bytes, 0, frameBytes, offset, bytes.Length);
            offset += bytes.Length;
        }

        session.InFlightFrames.Remove(fragment.FrameId);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameAssembled(sessionId, assembly.StreamEpoch, assembly.FrameId, assembly.IsKeyFrame);
        session.ReadyFrames[assembly.FrameId] = new ReadyFrameState(
            assembly.SessionId,
            assembly.StreamEpoch,
            assembly.FrameId,
            assembly.Width,
            assembly.Height,
            assembly.CapturedTsUtcMs,
            assembly.Encoding,
            assembly.IsKeyFrame,
            frameBytes);
        if (session.GapActive &&
            assembly.IsKeyFrame &&
            assembly.FrameId > session.GapExpectedFrameId)
        {
            TrackBufferedRecoveryKeyframe(sessionId, session, assembly.StreamEpoch, assembly.FrameId);
        }

        var frameReadyObservedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ScreenShareFrameLossAttributionRegistry.ObserveFrameReady(
            sessionId,
            assembly.StreamEpoch,
            assembly.FrameId,
            assembly.IsKeyFrame,
            assembly.CapturedTsUtcMs,
            frameReadyObservedUtcMs);
        TrimBufferedState(sessionId, session);
        EmitBufferedReadyFrames(sessionId, session);
        UpdateGapStateSnapshot(sessionId, session);
    }

    private void RequestKeyframe(string sessionId, long streamEpoch, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return;
        }

        if (sessions.TryGetValue(sessionId, out var session) &&
            session.LastKeyframeRequestStreamEpoch == streamEpoch &&
            session.LastKeyframeRequestUtc != default &&
            nowProvider() - session.LastKeyframeRequestUtc < RecoveryKeyframeRequestMinimumInterval)
        {
            return;
        }

        if (sessions.TryGetValue(sessionId, out session))
        {
            session.LastKeyframeRequestStreamEpoch = streamEpoch;
            session.LastKeyframeRequestUtc = nowProvider();
        }

        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
            sessionId,
            streamEpoch,
            "keyframe_requested");

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_video_keyframe_requested; session_id={sessionId}; stream_epoch={streamEpoch}; reason={reason}");
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_keyframe_requested; role=receiver; stream_epoch={streamEpoch}; reason={reason}; recovery_active=1; current_epoch_need_more_input_count=unavailable");
        KeyframeRequested?.Invoke(
            this,
            new ScreenShareVideoKeyframeRequestV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                Reason = reason,
            });
    }

    private static bool TryValidateFragment(ScreenShareVideoFragmentV1 fragment, out bool oversizeRejected)
    {
        oversizeRejected = false;

        if (fragment.StreamEpoch <= 0 ||
            fragment.FrameId < 0 ||
            fragment.Width <= 0 ||
            fragment.Height <= 0 ||
            fragment.CapturedTsUtcMs < 0 ||
            string.IsNullOrWhiteSpace(fragment.SessionId) ||
            !string.Equals(fragment.Encoding, "h264", StringComparison.OrdinalIgnoreCase) ||
            fragment.FragmentCount <= 0 ||
            fragment.FragmentIndex < 0 ||
            fragment.FragmentIndex >= fragment.FragmentCount)
        {
            return false;
        }

        if (fragment.FragmentCount > MaxFragmentCount)
        {
            oversizeRejected = true;
            return false;
        }

        var minimumPossibleFrameBytes =
            ((long)fragment.FragmentCount - 1) * ScreenShareVideoPayloadCodec.MaxFragmentRawBytes + 1;
        if (minimumPossibleFrameBytes > MaxAssembledFrameBytes)
        {
            oversizeRejected = true;
            return false;
        }

        if (fragment.Data.Length == 0 || fragment.Data.Length > ScreenShareVideoPayloadCodec.MaxFragmentRawBytes)
        {
            oversizeRejected = fragment.Data.Length > ScreenShareVideoPayloadCodec.MaxFragmentRawBytes;
            return false;
        }

        return true;
    }

    private void EmitBufferedReadyFrames(string sessionId, SessionAssemblyState session)
    {
        while (TryDequeueNextReadyFrame(sessionId, session, out var readyFrame))
        {
            var streamConfig = session.EmitConfigOnNextCompletedFrame ? session.CurrentStreamConfig : null;
            session.EmitConfigOnNextCompletedFrame = false;
            session.LastEmittedFrameId = readyFrame.FrameId;
            session.OrderedEmitHeadFrameId = Math.Max(session.OrderedEmitHeadFrameId, readyFrame.FrameId);
            ObserveRecoveryRunwayFrameEmitted(sessionId, session, readyFrame.FrameId);
            Interlocked.Increment(ref framesCompleted);
            var frameEmittedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ScreenShareFrameLossAttributionRegistry.ObserveFrameEmitted(
                sessionId,
                readyFrame.StreamEpoch,
                readyFrame.FrameId,
                readyFrame.IsKeyFrame,
                frameEmittedUtcMs);

            FrameReady?.Invoke(
                this,
                new ScreenShareVideoFrameReadyEventArgs(
                    sessionId,
                    readyFrame.StreamEpoch,
                    readyFrame.FrameId,
                    readyFrame.Width,
                    readyFrame.Height,
                    readyFrame.CapturedTsUtcMs,
                    readyFrame.Encoding,
                    readyFrame.IsKeyFrame,
                    readyFrame.FrameBytes,
                    streamConfig,
                    readyFrame.RecoveryDeliveryClass,
                    frameEmittedUtcMs));

            if (!session.GapActive)
            {
                MaybeEnterGapStateFromBufferedFrames(sessionId, session);
            }
        }
    }

    private bool TryDequeueNextReadyFrame(string sessionId, SessionAssemblyState session, out ReadyFrameState readyFrame)
    {
        while (true)
        {
            var expectedNextFrameId = GetExpectedNextFrameId(session);
            if (session.GapActive &&
                session.BufferedRecoveryKeyframeFrameId >= 0 &&
                expectedNextFrameId >= 0 &&
                expectedNextFrameId < session.BufferedRecoveryKeyframeFrameId &&
                session.ReadyFrames.ContainsKey(expectedNextFrameId))
            {
                DropBufferedFrameById(
                    sessionId,
                    session,
                    expectedNextFrameId,
                    "suppressed_emit_during_recovery_wait",
                    session.BufferedRecoveryKeyframeFrameId);
                UpdateGapStateSnapshot(sessionId, session);
                continue;
            }

            if (session.ReadyFrames.Remove(expectedNextFrameId, out var expectedReadyFrame))
            {
                if (session.GapActive && session.GapExpectedFrameId == expectedNextFrameId)
                {
                    ResetGapState(session);
                }

                readyFrame = expectedReadyFrame;
                return true;
            }

            if (!session.GapActive ||
                session.BufferedRecoveryKeyframeFrameId < 0 ||
                !session.ReadyFrames.TryGetValue(session.BufferedRecoveryKeyframeFrameId, out var recoveryKeyframe) ||
                !ShouldEmitRecoveryKeyframe(session))
            {
                readyFrame = default!;
                return false;
            }

            session.ReadyFrames.Remove(recoveryKeyframe.FrameId);
            PurgeFramesForResync(sessionId, session, recoveryKeyframe.FrameId);
            session.SupersededRecoveryTailFloorFrameId = Math.Max(
                session.SupersededRecoveryTailFloorFrameId,
                recoveryKeyframe.FrameId);
            session.WinningRecoveryOwnerActive = true;
            session.WinningRecoveryFrameId = Math.Max(session.WinningRecoveryFrameId, recoveryKeyframe.FrameId);
            session.OrderedEmitHeadFrameId = Math.Max(session.OrderedEmitHeadFrameId, recoveryKeyframe.FrameId);
            ScreenShareFrameLossAttributionRegistry.ObserveRecoveryOwner(
                sessionId,
                recoveryKeyframe.StreamEpoch,
                session.WinningRecoveryFrameId,
                session.OrderedEmitHeadFrameId,
                replaced: false);
            ScreenShareFrameLossAttributionRegistry.ObserveRecoveryKeyframeResync(
                sessionId,
                recoveryKeyframe.StreamEpoch,
                recoveryKeyframe.FrameId);
            ResetRecoveryRunway(session);
            ResetGapState(session);
            recoveryKeyframe.RecoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.RecoveryOwner;
            readyFrame = recoveryKeyframe;
            return true;
        }
    }

    private void EnterGapStateIfNeeded(string sessionId, SessionAssemblyState session, long expectedNextFrameId, long receivedFrameId = -1)
    {
        if (session.GapActive)
        {
            return;
        }

        session.GapActive = true;
        session.GapExpectedFrameId = Math.Max(0, expectedNextFrameId);
        session.GapDetectedUtc = nowProvider();
        session.BufferedRecoveryKeyframeFrameId = FindNewestBufferedRecoveryKeyframeFrameId(session);
        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
            sessionId,
            session.CurrentStreamEpoch,
            "gap_detected",
            expectedNextFrameId,
            receivedFrameId);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_receiver_continuity_lost; role=receiver; stream_epoch={session.CurrentStreamEpoch}; reason=frame_gap_reassembler; recovery_active=1; current_epoch_need_more_input_count=unavailable");
        RequestKeyframe(sessionId, session.CurrentStreamEpoch, "frame_gap_reassembler");
        UpdateGapStateSnapshot(sessionId, session);
    }

    private void MaybeEnterGapStateFromBufferedFrames(string sessionId, SessionAssemblyState session)
    {
        if (session.GapActive)
        {
            return;
        }

        var expectedNextFrameId = GetExpectedNextFrameId(session);
        if (session.InFlightFrames.ContainsKey(expectedNextFrameId) || session.ReadyFrames.ContainsKey(expectedNextFrameId))
        {
            return;
        }

        if (session.InFlightFrames.Keys.Any(frameId => frameId > expectedNextFrameId) ||
            session.ReadyFrames.Keys.Any(frameId => frameId > expectedNextFrameId))
        {
            EnterGapStateIfNeeded(sessionId, session, expectedNextFrameId);
        }
    }

    private bool TryPrepareForIncomingFutureNonKey(string sessionId, SessionAssemblyState session, ScreenShareVideoFragmentV1 fragment)
    {
        if (!session.GapActive || fragment.FrameId < session.GapExpectedFrameId)
        {
            return true;
        }

        if (session.BufferedRecoveryKeyframeFrameId >= 0)
        {
            DropIncomingFutureNonKey(
                sessionId,
                fragment,
                fragment.FrameId <= session.BufferedRecoveryKeyframeFrameId
                    ? "superseded_recovery_tail_cleanup"
                    : "recovery_keyframe_buffered_tail_rejected");
            return false;
        }

        DropIncomingFutureNonKey(sessionId, fragment, "pre_candidate_gap_tail_rejected");
        return false;
    }

    private bool TryDropExpiredRecoveryRunwayTail(string sessionId, SessionAssemblyState session, ScreenShareVideoFragmentV1 fragment)
    {
        _ = sessionId;
        _ = session;
        _ = fragment;
        return false;
    }

    private bool TryHandleIncomingRecoveryKeyframe(string sessionId, SessionAssemblyState session, ScreenShareVideoFragmentV1 fragment)
    {
        if (!session.GapActive || fragment.FrameId <= session.GapExpectedFrameId)
        {
            return true;
        }

        var bufferedRecoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
        if (bufferedRecoveryKeyframeFrameId < 0 ||
            bufferedRecoveryKeyframeFrameId == fragment.FrameId ||
            fragment.FrameId > bufferedRecoveryKeyframeFrameId)
        {
            return true;
        }

        Interlocked.Increment(ref framesDropped);
        ObserveAssemblyEvictedWithCause(
            sessionId,
            session,
            fragment.StreamEpoch,
            fragment.FrameId,
            "same_epoch_recovery_owner_suppressed",
            fragment.IsKeyFrame,
            bufferedRecoveryKeyframeFrameId);
        return false;
    }

    private void DropIncomingFutureNonKey(string sessionId, ScreenShareVideoFragmentV1 fragment, string reason)
    {
        Interlocked.Increment(ref framesDropped);
        ObserveAssemblyEvictedWithCause(
            sessionId,
            session: null,
            fragment.StreamEpoch,
            fragment.FrameId,
            string.IsNullOrWhiteSpace(reason) ? "gap_non_key_pruned" : reason.Trim(),
            fragment.IsKeyFrame);
    }

    private void TrimBufferedState(string sessionId, SessionAssemblyState session)
    {
        while (true)
        {
            var trimmedAny = false;

            while (true)
            {
                var staleFrameId = FindHighestBufferedFrameIdAtOrBelowProvenHeadFloor(sessionId, session);
                if (staleFrameId < 0)
                {
                    break;
                }

                var reason = ResolveLateFragmentReason(sessionId, session, session.CurrentStreamEpoch, staleFrameId);
                DropBufferedFrameById(
                    sessionId,
                    session,
                    staleFrameId,
                    reason,
                    GetProvenHeadFloorFrameId(sessionId, session, session.CurrentStreamEpoch));
                trimmedAny = true;
            }

            if (session.ExpiredRecoveryRunwayOwnerFrameId >= 0)
            {
                while (true)
                {
                    var expiredRunwayFrameId = FindFarthestExpiredRecoveryRunwayFrameId(session);
                    if (expiredRunwayFrameId < 0)
                    {
                        break;
                    }

                    DropBufferedFrameById(
                        sessionId,
                        session,
                        expiredRunwayFrameId,
                        "runway_candidate_expired_after_head_advance",
                        session.ExpiredRecoveryRunwayOwnerFrameId);
                    trimmedAny = true;
                }
            }

            if (session.GapActive)
            {
                if (session.BufferedRecoveryKeyframeFrameId >= 0)
                {
                    while (true)
                    {
                        var preRecoveryTailFrameId = FindFarthestFutureNonKeyFrameIdAtOrBeforeRecoveryKeyframe(session);
                        if (preRecoveryTailFrameId < 0)
                        {
                            break;
                        }

                        DropBufferedFrameById(
                            sessionId,
                            session,
                            preRecoveryTailFrameId,
                            "superseded_recovery_tail_cleanup",
                            session.BufferedRecoveryKeyframeFrameId);
                        trimmedAny = true;
                    }

                    while (true)
                    {
                        var bufferedRecoveryFollowerFrameId = FindFarthestBufferedRecoveryFollowerFrameId(session);
                        if (bufferedRecoveryFollowerFrameId < 0)
                        {
                            break;
                        }

                        DropBufferedFrameById(
                            sessionId,
                            session,
                            bufferedRecoveryFollowerFrameId,
                            "recovery_keyframe_buffered_tail_rejected",
                            session.BufferedRecoveryKeyframeFrameId);
                        trimmedAny = true;
                    }
                }

                while (session.BufferedRecoveryKeyframeFrameId < 0)
                {
                    var farthestFutureNonKeyFrameId = FindFarthestFutureNonKeyFrameIdBeyondGapHead(session);
                    if (farthestFutureNonKeyFrameId < 0)
                    {
                        break;
                    }

                    DropBufferedFrameById(sessionId, session, farthestFutureNonKeyFrameId, "pre_candidate_gap_tail_rejected");
                    trimmedAny = true;
                }
            }

            while (session.ReadyFrames.Count > MaxReadyFramesPerSession)
            {
                var readyFrameIdToDrop = FindFarthestFutureReadyNonKeyFrameId(session);
                if (readyFrameIdToDrop < 0)
                {
                    break;
                }

                DropBufferedFrameById(sessionId, session, readyFrameIdToDrop, session.GapActive ? "gap_non_key_pruned" : "buffer_budget_pruned");
                trimmedAny = true;
            }

            while (session.InFlightFrames.Count > MaxInFlightAssembliesPerSession)
            {
                var assemblyFrameIdToDrop = FindFarthestFutureInFlightNonKeyFrameId(session);
                if (assemblyFrameIdToDrop < 0)
                {
                    break;
                }

                DropBufferedFrameById(sessionId, session, assemblyFrameIdToDrop, session.GapActive ? "gap_non_key_pruned" : "buffer_budget_pruned");
                trimmedAny = true;
            }

            if (!trimmedAny)
            {
                break;
            }
        }

        AssertBounds(session.InFlightFrames.Count, session.ReadyFrames.Count);
    }

    private long FindFarthestFutureNonKeyFrameId(SessionAssemblyState session)
    {
        return Math.Max(FindFarthestFutureReadyNonKeyFrameId(session), FindFarthestFutureInFlightNonKeyFrameId(session));
    }

    private long FindFarthestFutureNonKeyFrameIdAtOrBeforeRecoveryKeyframe(SessionAssemblyState session)
    {
        if (session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        return Math.Max(
            FindFarthestFutureReadyNonKeyFrameIdAtOrBeforeRecoveryKeyframe(session),
            FindFarthestFutureInFlightNonKeyFrameIdAtOrBeforeRecoveryKeyframe(session));
    }

    private long FindFarthestFutureReadyNonKeyFrameId(SessionAssemblyState session)
    {
        var floorFrameId = session.GapActive ? session.GapExpectedFrameId : GetExpectedNextFrameId(session) - 1;
        var expectedNextFrameId = GetExpectedNextFrameId(session);
        foreach (var pair in session.ReadyFrames.Reverse())
        {
            if (pair.Key <= floorFrameId || pair.Key == expectedNextFrameId)
            {
                continue;
            }

            if (!pair.Value.IsKeyFrame)
            {
                return pair.Key;
            }
        }

        return -1;
    }

    private long FindFarthestFutureReadyNonKeyFrameIdAtOrBeforeRecoveryKeyframe(SessionAssemblyState session)
    {
        if (session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        var floorFrameId = session.GapActive ? session.GapExpectedFrameId : GetExpectedNextFrameId(session) - 1;
        var expectedNextFrameId = GetExpectedNextFrameId(session);
        foreach (var pair in session.ReadyFrames.Reverse())
        {
            if (pair.Key <= floorFrameId ||
                pair.Key == expectedNextFrameId ||
                pair.Key > session.BufferedRecoveryKeyframeFrameId)
            {
                continue;
            }

            if (!pair.Value.IsKeyFrame)
            {
                return pair.Key;
            }
        }

        return -1;
    }

    private long FindFarthestFutureInFlightNonKeyFrameId(SessionAssemblyState session)
    {
        var floorFrameId = session.GapActive ? session.GapExpectedFrameId : GetExpectedNextFrameId(session) - 1;
        var expectedNextFrameId = GetExpectedNextFrameId(session);
        foreach (var pair in session.InFlightFrames.Reverse())
        {
            if (pair.Key <= floorFrameId || pair.Key == expectedNextFrameId)
            {
                continue;
            }

            if (!pair.Value.IsKeyFrame)
            {
                return pair.Key;
            }
        }

        return -1;
    }

    private long FindFarthestFutureInFlightNonKeyFrameIdAtOrBeforeRecoveryKeyframe(SessionAssemblyState session)
    {
        if (session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        var floorFrameId = session.GapActive ? session.GapExpectedFrameId : GetExpectedNextFrameId(session) - 1;
        var expectedNextFrameId = GetExpectedNextFrameId(session);
        foreach (var pair in session.InFlightFrames.Reverse())
        {
            if (pair.Key <= floorFrameId ||
                pair.Key == expectedNextFrameId ||
                pair.Key > session.BufferedRecoveryKeyframeFrameId)
            {
                continue;
            }

            if (!pair.Value.IsKeyFrame)
            {
                return pair.Key;
            }
        }

        return -1;
    }

    private static int CountBufferedRecoveryFollowerFrames(SessionAssemblyState session)
    {
        if (!session.GapActive || session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return 0;
        }

        var recoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
        var readyCount = session.ReadyFrames.Values.Count(frame => frame.FrameId > recoveryKeyframeFrameId && !frame.IsKeyFrame);
        var inFlightCount = session.InFlightFrames.Values.Count(frame => frame.FrameId > recoveryKeyframeFrameId && !frame.IsKeyFrame);
        return readyCount + inFlightCount;
    }

    private static long GetNextAllowedRecoveryFollowerFrameId(SessionAssemblyState session)
    {
        if (!session.GapActive || session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        var nextFollowerFrameId = session.BufferedRecoveryKeyframeFrameId + 1;
        for (var bufferedCount = 0; bufferedCount < MaxRecoveryFollowerFramesWhileGapActive; bufferedCount++)
        {
            if (session.ReadyFrames.ContainsKey(nextFollowerFrameId) ||
                session.InFlightFrames.ContainsKey(nextFollowerFrameId))
            {
                nextFollowerFrameId++;
                continue;
            }

            return nextFollowerFrameId;
        }

        return -1;
    }

    private static long FindFarthestBufferedRecoveryFollowerFrameId(SessionAssemblyState session)
    {
        if (!session.GapActive || session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        var recoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
        var readyFollowerFrameId = session.ReadyFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(frameId => frameId > recoveryKeyframeFrameId)
            .DefaultIfEmpty(-1)
            .First();
        var inFlightFollowerFrameId = session.InFlightFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(frameId => frameId > recoveryKeyframeFrameId)
            .DefaultIfEmpty(-1)
            .First();
        return Math.Max(readyFollowerFrameId, inFlightFollowerFrameId);
    }

    private static long FindFarthestBufferedRecoveryFollowerFrameIdOutsideRunway(SessionAssemblyState session)
    {
        if (!session.GapActive || session.BufferedRecoveryKeyframeFrameId < 0)
        {
            return -1;
        }

        var framesToKeep = GetRecoveryFollowerFramesToKeep(session, session.BufferedRecoveryKeyframeFrameId);
        var recoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
        foreach (var pair in session.ReadyFrames.Reverse())
        {
            if (pair.Value.IsKeyFrame ||
                pair.Key <= recoveryKeyframeFrameId ||
                framesToKeep.Contains(pair.Key))
            {
                continue;
            }

            return pair.Key;
        }

        foreach (var pair in session.InFlightFrames.Reverse())
        {
            if (pair.Value.IsKeyFrame ||
                pair.Key <= recoveryKeyframeFrameId ||
                framesToKeep.Contains(pair.Key))
            {
                continue;
            }

            return pair.Key;
        }

        return -1;
    }

    private static long FindFarthestFutureNonKeyFrameIdBeyondGapHead(SessionAssemblyState session)
    {
        if (!session.GapActive || session.BufferedRecoveryKeyframeFrameId >= 0)
        {
            return -1;
        }

        var readyFrameId = session.ReadyFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(frameId => frameId > session.GapExpectedFrameId)
            .DefaultIfEmpty(-1)
            .First();
        var inFlightFrameId = session.InFlightFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(frameId => frameId > session.GapExpectedFrameId)
            .DefaultIfEmpty(-1)
            .First();
        return Math.Max(readyFrameId, inFlightFrameId);
    }

    private bool DropBufferedFrameById(string sessionId, SessionAssemblyState session, long frameId, string reason, long relatedFrameId = -1)
    {
        if (session.ReadyFrames.Remove(frameId, out var readyFrame))
        {
            Interlocked.Increment(ref framesDropped);
            ScreenShareFrameLossAttributionRegistry.ObserveReadyFrameSkippedReplaced(
                sessionId,
                readyFrame.StreamEpoch,
                frameId,
                relatedFrameId,
                readyFrame.IsKeyFrame,
                reason);
            ObserveReassemblerRootCause(
                sessionId,
                session,
                readyFrame.StreamEpoch,
                frameId,
                reason,
                relatedFrameId);
            if (session.BufferedRecoveryKeyframeFrameId == frameId)
            {
                session.BufferedRecoveryKeyframeFrameId = FindNewestBufferedRecoveryKeyframeFrameId(session);
            }

            return true;
        }

        if (!session.InFlightFrames.Remove(frameId, out var assembly))
        {
            return false;
        }

        Interlocked.Increment(ref framesDropped);
        Interlocked.Increment(ref assembliesExpired);
        ObserveAssemblyEvictedWithCause(sessionId, session, assembly.StreamEpoch, frameId, reason, assembly.IsKeyFrame, relatedFrameId);
        if (session.BufferedRecoveryKeyframeFrameId == frameId)
        {
            session.BufferedRecoveryKeyframeFrameId = FindNewestBufferedRecoveryKeyframeFrameId(session);
        }

        return true;
    }

    private bool ShouldEmitRecoveryKeyframe(SessionAssemblyState session)
    {
        return session.GapActive && session.BufferedRecoveryKeyframeFrameId >= 0;
    }

    private void PurgeFramesForResync(string sessionId, SessionAssemblyState session, long recoveryKeyframeFrameId)
    {
        var staleInFlightFrameIds = session.InFlightFrames.Keys
            .Where(frameId => frameId != recoveryKeyframeFrameId)
            .ToArray();
        foreach (var staleFrameId in staleInFlightFrameIds)
        {
            session.InFlightFrames.Remove(staleFrameId);
            Interlocked.Increment(ref framesDropped);
            Interlocked.Increment(ref assembliesExpired);
            var reason = staleFrameId < recoveryKeyframeFrameId
                ? "superseded_recovery_tail_cleanup"
                : "resync_purge";
            ObserveAssemblyEvictedWithCause(sessionId, session, session.CurrentStreamEpoch, staleFrameId, reason, relatedFrameId: recoveryKeyframeFrameId);
        }

        var staleReadyFrameIds = session.ReadyFrames.Keys
            .Where(frameId => frameId != recoveryKeyframeFrameId)
            .ToArray();
        foreach (var staleFrameId in staleReadyFrameIds)
        {
            session.ReadyFrames.Remove(staleFrameId);
            Interlocked.Increment(ref framesDropped);
            var reason = staleFrameId < recoveryKeyframeFrameId
                ? "superseded_recovery_tail_cleanup"
                : "resync_purge";
            ScreenShareFrameLossAttributionRegistry.ObserveReadyFrameSkippedReplaced(
                sessionId,
                session.CurrentStreamEpoch,
                staleFrameId,
                recoveryKeyframeFrameId,
                reason: reason);
            ObserveReassemblerRootCause(
                sessionId,
                session,
                session.CurrentStreamEpoch,
                staleFrameId,
                reason,
                recoveryKeyframeFrameId);
        }
    }

    private static HashSet<long> GetRecoveryFollowerFramesToKeep(SessionAssemblyState session, long recoveryKeyframeFrameId)
    {
        _ = session;
        _ = recoveryKeyframeFrameId;
        return [];
    }

    private void TrackBufferedRecoveryKeyframe(string sessionId, SessionAssemblyState session, long streamEpoch, long frameId)
    {
        if (!session.GapActive || frameId <= session.GapExpectedFrameId)
        {
            return;
        }

        if (frameId <= session.BufferedRecoveryKeyframeFrameId)
        {
            return;
        }

        var previousBufferedRecoveryKeyframeFrameId = session.BufferedRecoveryKeyframeFrameId;
        var replaced = false;
        if (session.BufferedRecoveryKeyframeFrameId >= 0)
        {
            DropBufferedFrameById(
                sessionId,
                session,
                session.BufferedRecoveryKeyframeFrameId,
                "gap_recovery_keyframe_replaced",
                frameId);
            replaced = true;
        }

        session.BufferedRecoveryKeyframeFrameId = frameId;
        session.WinningRecoveryOwnerActive = true;
        session.WinningRecoveryFrameId = frameId;
        session.OrderedEmitHeadFrameId = Math.Max(session.OrderedEmitHeadFrameId, frameId);
        DropSupersededRecoveryTailBeforeFrame(sessionId, session, frameId);
        ScreenShareFrameLossAttributionRegistry.ObserveRecoveryOwner(
            sessionId,
            streamEpoch,
            session.WinningRecoveryFrameId,
            session.OrderedEmitHeadFrameId,
            replaced: replaced);
        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(
            sessionId,
            streamEpoch,
            replaced ? "recovery_owner_replaced" : "recovery_owner_buffered",
            frameId,
            previousBufferedRecoveryKeyframeFrameId);
        LogRecoveryOwnerTransition(
            sessionId,
            session,
            streamEpoch,
            previousBufferedRecoveryKeyframeFrameId,
            frameId,
            replaced);
    }

    private static void StartRecoveryRunway(SessionAssemblyState session, long recoveryKeyframeFrameId)
    {
        _ = recoveryKeyframeFrameId;
        ResetRecoveryRunway(session);
    }

    private static void ObserveRecoveryRunwayFrameEmitted(string sessionId, SessionAssemblyState session, long emittedFrameId)
    {
        if (!session.RecoveryRunwayActive ||
            session.RecoveryRunwayExpectedNextFrameId < 0 ||
            session.RecoveryRunwayRemainingFollowers <= 0)
        {
            return;
        }

        if (emittedFrameId != session.RecoveryRunwayExpectedNextFrameId)
        {
            return;
        }

        ScreenShareFrameLossAttributionRegistry.ObserveRunwayFollowerEmittedWithinActionableWindow(
            sessionId,
            session.CurrentStreamEpoch,
            emittedFrameId);
        session.RecoveryRunwayExpectedNextFrameId++;
        session.RecoveryRunwayRemainingFollowers--;
        if (session.RecoveryRunwayRemainingFollowers <= 0)
        {
            ResetRecoveryRunway(session);
        }
    }

    private static void ResetRecoveryRunway(SessionAssemblyState session)
    {
        session.RecoveryRunwayActive = false;
        session.RecoveryRunwayExpectedNextFrameId = -1;
        session.RecoveryRunwayRemainingFollowers = 0;
    }

    private void ExpireRecoveryRunwayWindow(string sessionId, SessionAssemblyState session, long blockedByFrameId)
    {
        if (!session.WinningRecoveryOwnerActive || session.WinningRecoveryFrameId < 0)
        {
            ResetRecoveryRunway(session);
            return;
        }

        if (session.ExpiredRecoveryRunwayOwnerFrameId >= session.WinningRecoveryFrameId)
        {
            ResetRecoveryRunway(session);
            return;
        }

        session.ExpiredRecoveryRunwayOwnerFrameId = session.WinningRecoveryFrameId;
        ScreenShareFrameLossAttributionRegistry.ObserveStaleRunwayWindowAbort(
            sessionId,
            session.CurrentStreamEpoch,
            session.WinningRecoveryFrameId,
            blockedByFrameId);
        TrimBufferedState(sessionId, session);
        ResetRecoveryRunway(session);
    }

    private void ResetGapState(SessionAssemblyState session)
    {
        session.GapActive = false;
        session.GapExpectedFrameId = -1;
        session.GapDetectedUtc = default;
        session.BufferedRecoveryKeyframeFrameId = -1;
    }

    private void UpdateGapStateSnapshot(string sessionId, SessionAssemblyState session)
    {
        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerGapState(
            sessionId,
            session.CurrentStreamEpoch,
            session.GapActive,
            session.GapExpectedFrameId,
            session.BufferedRecoveryKeyframeFrameId,
            CountFutureNonKeyFrames(session));
    }

    private void LogRecoveryOwnerTransition(
        string sessionId,
        SessionAssemblyState session,
        long streamEpoch,
        long previousRecoveryOwnerFrameId,
        long newRecoveryOwnerFrameId,
        bool replaced)
    {
        var orderedEmitHeadFrameId = Math.Max(
            session.OrderedEmitHeadFrameId,
            ScreenShareFrameLossAttributionRegistry.GetOrderedEmitHeadFrameId(sessionId, streamEpoch));
        var appliedHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetAppliedHeadFrameId(sessionId, streamEpoch);
        var stableVisibleHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetStableVisibleHeadFrameId(sessionId, streamEpoch);
        var visibleRecoveryFloorFrameId = ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(sessionId, streamEpoch);
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event={(replaced ? "screenshare_reassembler_recovery_owner_replaced" : "screenshare_reassembler_recovery_owner_buffered")}; session_id={sessionId}; stream_epoch={streamEpoch}; session_current_stream_epoch={FormatFrameIdForLog(session.CurrentStreamEpoch)}; previous_recovery_owner_frame_id={FormatFrameIdForLog(previousRecoveryOwnerFrameId)}; new_recovery_owner_frame_id={FormatFrameIdForLog(newRecoveryOwnerFrameId)}; gap_active={(session.GapActive ? 1 : 0)}; gap_expected_frame_id={FormatFrameIdForLog(session.GapExpectedFrameId)}; ordered_emit_head_frame_id={FormatFrameIdForLog(orderedEmitHeadFrameId)}; applied_head_frame_id={FormatFrameIdForLog(appliedHeadFrameId)}; stable_visible_head_frame_id={FormatFrameIdForLog(stableVisibleHeadFrameId)}; visible_recovery_floor_frame_id={FormatFrameIdForLog(visibleRecoveryFloorFrameId)}; replacement_after_ordered_head_advanced={(previousRecoveryOwnerFrameId >= 0 && orderedEmitHeadFrameId >= 0 && previousRecoveryOwnerFrameId <= orderedEmitHeadFrameId ? 1 : 0)}; replacement_after_applied_head_advanced={(previousRecoveryOwnerFrameId >= 0 && appliedHeadFrameId >= 0 && previousRecoveryOwnerFrameId <= appliedHeadFrameId ? 1 : 0)}");
    }

    private static string FormatFrameIdForLog(long frameId)
        => frameId >= 0 ? frameId.ToString() : "(none)";

    private static int CountFutureNonKeyFrames(SessionAssemblyState session)
    {
        if (!session.GapActive)
        {
            return 0;
        }

        var floorFrameId = session.GapExpectedFrameId;
        var readyCount = session.ReadyFrames.Values.Count(frame => frame.FrameId > floorFrameId && !frame.IsKeyFrame);
        var inFlightCount = session.InFlightFrames.Values.Count(frame => frame.FrameId > floorFrameId && !frame.IsKeyFrame);
        return readyCount + inFlightCount;
    }

    private static long GetMaxAllowedPreRecoveryFutureNonKeyFrameId(SessionAssemblyState session)
    {
        return session.GapExpectedFrameId + MaxFutureNonKeyFramesWhileGapActive;
    }

    private static long FindNewestBufferedRecoveryKeyframeFrameId(SessionAssemblyState session)
    {
        var readyRecoveryKeyframe = session.ReadyFrames.Values
            .Where(frame => frame.IsKeyFrame)
            .Select(frame => frame.FrameId)
            .DefaultIfEmpty(-1)
            .Max();
        var inFlightRecoveryKeyframe = session.InFlightFrames.Values
            .Where(frame => frame.IsKeyFrame)
            .Select(frame => frame.FrameId)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(readyRecoveryKeyframe, inFlightRecoveryKeyframe);
    }

    private static long GetExpectedNextFrameId(SessionAssemblyState session)
    {
        return session.LastEmittedFrameId + 1;
    }

    private void DropSupersededRecoveryTailBeforeFrame(string sessionId, SessionAssemblyState session, long frameId)
    {
        if (!session.GapActive || frameId <= session.GapExpectedFrameId)
        {
            return;
        }

        while (true)
        {
            var supersededFrameId = FindFarthestFutureNonKeyFrameIdBeforeFrame(session, frameId);
            if (supersededFrameId < 0)
            {
                break;
            }

            DropBufferedFrameById(
                sessionId,
                session,
                supersededFrameId,
                "superseded_recovery_tail_cleanup",
                frameId);
        }
    }

    private static long FindFarthestFutureNonKeyFrameIdBeforeFrame(SessionAssemblyState session, long frameId)
    {
        if (!session.GapActive || frameId <= session.GapExpectedFrameId)
        {
            return -1;
        }

        var readyFrameId = session.ReadyFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(candidateFrameId => candidateFrameId > session.GapExpectedFrameId && candidateFrameId < frameId)
            .DefaultIfEmpty(-1)
            .First();
        var inFlightFrameId = session.InFlightFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(candidateFrameId => candidateFrameId > session.GapExpectedFrameId && candidateFrameId < frameId)
            .DefaultIfEmpty(-1)
            .First();
        return Math.Max(readyFrameId, inFlightFrameId);
    }

    private static long FindFarthestExpiredRecoveryRunwayFrameId(SessionAssemblyState session)
    {
        if (session.ExpiredRecoveryRunwayOwnerFrameId < 0)
        {
            return -1;
        }

        var readyFrameId = session.ReadyFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(candidateFrameId => candidateFrameId > session.ExpiredRecoveryRunwayOwnerFrameId)
            .DefaultIfEmpty(-1)
            .First();
        var inFlightFrameId = session.InFlightFrames.Reverse()
            .Where(static pair => !pair.Value.IsKeyFrame)
            .Select(static pair => pair.Key)
            .Where(candidateFrameId => candidateFrameId > session.ExpiredRecoveryRunwayOwnerFrameId)
            .DefaultIfEmpty(-1)
            .First();
        return Math.Max(readyFrameId, inFlightFrameId);
    }

    private bool TryDropFragmentBehindProvenHeadFloor(string sessionId, SessionAssemblyState session, ScreenShareVideoFragmentV1 fragment)
    {
        var provenHeadFloorFrameId = GetProvenHeadFloorFrameId(sessionId, session, fragment.StreamEpoch);
        if (provenHeadFloorFrameId < 0 || fragment.FrameId > provenHeadFloorFrameId)
        {
            return false;
        }

        Interlocked.Increment(ref framesDropped);
        var reason = ResolveLateFragmentReason(sessionId, session, fragment.StreamEpoch, fragment.FrameId);
        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerStaleSuperseded(
            sessionId,
            fragment.StreamEpoch,
            fragment.FrameId,
            supersededByFrameId: provenHeadFloorFrameId,
            fragment.IsKeyFrame,
            reason: reason);
        ObserveReassemblerRootCause(
            sessionId,
            session,
            fragment.StreamEpoch,
            fragment.FrameId,
            reason,
            provenHeadFloorFrameId);
        return true;
    }

    private static long GetProvenHeadFloorFrameId(string sessionId, SessionAssemblyState session, long streamEpoch)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || streamEpoch <= 0)
        {
            return -1;
        }

        // Use only already-proven progress. The local ordered-emit tracker also advances for
        // buffered recovery owners, so LastEmittedFrameId is the safe local floor here.
        var appliedHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetAppliedHeadFrameId(sessionId, streamEpoch);
        var stableVisibleHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetStableVisibleHeadFrameId(sessionId, streamEpoch);
        var visibleRecoveryFloorFrameId = ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(sessionId, streamEpoch);
        return Math.Max(
            Math.Max(session.LastEmittedFrameId, appliedHeadFrameId),
            Math.Max(stableVisibleHeadFrameId, visibleRecoveryFloorFrameId));
    }

    private static long FindHighestBufferedFrameIdAtOrBelowProvenHeadFloor(string sessionId, SessionAssemblyState session)
    {
        var provenHeadFloorFrameId = GetProvenHeadFloorFrameId(sessionId, session, session.CurrentStreamEpoch);
        if (provenHeadFloorFrameId < 0)
        {
            return -1;
        }

        var readyFrameId = session.ReadyFrames.Reverse()
            .Select(static pair => pair.Key)
            .Where(frameId => frameId <= provenHeadFloorFrameId)
            .DefaultIfEmpty(-1)
            .First();
        var inFlightFrameId = session.InFlightFrames.Reverse()
            .Select(static pair => pair.Key)
            .Where(frameId => frameId <= provenHeadFloorFrameId)
            .DefaultIfEmpty(-1)
            .First();
        return Math.Max(readyFrameId, inFlightFrameId);
    }

    private static string ResolveLateFragmentReason(string sessionId, SessionAssemblyState session, long streamEpoch, long frameId)
    {
        if (session.SupersededRecoveryTailFloorFrameId >= 0 &&
            frameId < session.SupersededRecoveryTailFloorFrameId)
        {
            return "superseded_recovery_tail_cleanup";
        }

        if (session.WinningRecoveryOwnerActive &&
            session.WinningRecoveryFrameId >= 0 &&
            frameId < session.WinningRecoveryFrameId)
        {
            return "superseded_recovery_tail_cleanup";
        }

        var orderedEmitHeadFrameId = Math.Max(
            session.OrderedEmitHeadFrameId,
            ScreenShareFrameLossAttributionRegistry.GetOrderedEmitHeadFrameId(sessionId, streamEpoch));
        if (orderedEmitHeadFrameId >= 0 && frameId <= orderedEmitHeadFrameId)
        {
            return "late_fragment_after_ordered_head";
        }

        var appliedHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetAppliedHeadFrameId(sessionId, streamEpoch);
        if (appliedHeadFrameId >= 0 && frameId <= appliedHeadFrameId)
        {
            return "late_fragment_after_applied_head";
        }

        var stableVisibleHeadFrameId = ScreenShareFrameLossAttributionRegistry.GetStableVisibleHeadFrameId(sessionId, streamEpoch);
        if (stableVisibleHeadFrameId >= 0 && frameId <= stableVisibleHeadFrameId)
        {
            return "late_fragment_after_stable_visible_head";
        }

        var visibleRecoveryFloorFrameId = ScreenShareFrameLossAttributionRegistry.GetVisibleRecoveryFloorFrameId(sessionId, streamEpoch);
        return visibleRecoveryFloorFrameId >= 0 && frameId <= visibleRecoveryFloorFrameId
            ? "late_fragment_after_visible_recovery"
            : "late_fragment_after_head_advanced";
    }

    private void ObserveAssemblyEvictedWithCause(
        string sessionId,
        SessionAssemblyState? session,
        long streamEpoch,
        long frameId,
        string reason,
        bool isKeyFrame = false,
        long relatedFrameId = -1)
    {
        ScreenShareFrameLossAttributionRegistry.ObserveAssemblyEvicted(
            sessionId,
            streamEpoch > 0 ? streamEpoch : 0,
            frameId,
            reason,
            isKeyFrame);
        ObserveReassemblerRootCause(sessionId, session, streamEpoch, frameId, reason, relatedFrameId);
    }

    private void ObserveReassemblerRootCause(
        string sessionId,
        SessionAssemblyState? session,
        long streamEpoch,
        long frameId,
        string reason,
        long relatedFrameId = -1)
    {
        var rootCause = MapReassemblerRootCause(reason);
        if (rootCause == ScreenShareReassemblerRootCauseBucket.None || streamEpoch <= 0 || frameId < 0)
        {
            return;
        }

        var expectedNextFrameId = session is null ? -1 : GetExpectedNextFrameId(session);
        var futureNonKeyBufferedCount = session is null ? 0 : CountFutureNonKeyFrames(session);
        var bufferedRecoveryKeyframeFrameId = session?.BufferedRecoveryKeyframeFrameId ?? -1;
        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            sessionId,
            streamEpoch,
            frameId,
            rootCause,
            expectedNextFrameId,
            relatedFrameId >= 0 ? relatedFrameId : frameId,
            futureNonKeyBufferedCount,
            bufferedRecoveryKeyframeFrameId,
            reason,
            session?.CurrentStreamEpoch ?? -1,
            session?.GapActive ?? false,
            session?.GapExpectedFrameId ?? -1,
            session?.WinningRecoveryFrameId ?? -1,
            session?.OrderedEmitHeadFrameId ?? -1);
    }

    private static ScreenShareReassemblerRootCauseBucket MapReassemblerRootCause(string reason)
    {
        return reason switch
        {
            "stale_frame_superseded" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "superseded_recovery_tail_cleanup" => ScreenShareReassemblerRootCauseBucket.None,
            "late_fragment_after_ordered_head" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "late_fragment_after_applied_head" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "late_fragment_after_stable_visible_head" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "late_fragment_after_head_advanced" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "late_fragment_after_visible_recovery" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "stream_epoch_advanced" => ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            "gap_non_key_pruned" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "future_tail_quarantined_during_gap" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "future_tail_quarantined_after_gap" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "pre_candidate_gap_tail_rejected" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "recovery_keyframe_buffered_tail_rejected" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "recovery_follower_window_trimmed" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "recovery_runway_overflow" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "suppressed_emit_during_recovery_wait" => ScreenShareReassemblerRootCauseBucket.FutureTailPrunedWhileGapActive,
            "buffer_budget_pruned" => ScreenShareReassemblerRootCauseBucket.ProtectedHeadMissingBudgetPressure,
            "gap_recovery_keyframe_replaced" => ScreenShareReassemblerRootCauseBucket.RecoveryKeyframeSupersededOrReplaced,
            "same_epoch_recovery_owner_suppressed" => ScreenShareReassemblerRootCauseBucket.RecoveryKeyframeSupersededOrReplaced,
            "resync_purge" => ScreenShareReassemblerRootCauseBucket.OrderedEmitBlockedThenResynced,
            "fragment_oversize" => ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly,
            "assembly_mismatch" => ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly,
            "assembly_oversize" => ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly,
            "assembly_incomplete" => ScreenShareReassemblerRootCauseBucket.FragmentGapBeforeAssembly,
            _ => ScreenShareReassemblerRootCauseBucket.None,
        };
    }

    private sealed class SessionAssemblyState
    {
        public long CurrentStreamEpoch { get; set; }
        public long LastEmittedFrameId { get; set; } = -1;
        public long SupersededRecoveryTailFloorFrameId { get; set; } = -1;
        public bool WinningRecoveryOwnerActive { get; set; }
        public long WinningRecoveryFrameId { get; set; } = -1;
        public long OrderedEmitHeadFrameId { get; set; } = -1;
        public ScreenShareVideoStreamConfigV1? CurrentStreamConfig { get; set; }
        public bool EmitConfigOnNextCompletedFrame { get; set; }
        public SortedDictionary<long, AssemblyState> InFlightFrames { get; } = new();
        public SortedDictionary<long, ReadyFrameState> ReadyFrames { get; } = new();
        public long LastKeyframeRequestStreamEpoch { get; set; }
        public DateTimeOffset LastKeyframeRequestUtc { get; set; }
        public bool GapActive { get; set; }
        public long GapExpectedFrameId { get; set; } = -1;
        public DateTimeOffset GapDetectedUtc { get; set; }
        public long BufferedRecoveryKeyframeFrameId { get; set; } = -1;
        public bool RecoveryRunwayActive { get; set; }
        public long RecoveryRunwayExpectedNextFrameId { get; set; } = -1;
        public int RecoveryRunwayRemainingFollowers { get; set; }
        public long ExpiredRecoveryRunwayOwnerFrameId { get; set; } = -1;
    }

    private sealed class AssemblyState
    {
        public AssemblyState(string sessionId, long streamEpoch, long frameId, int width, int height, long capturedTsUtcMs, string encoding, bool isKeyFrame, int fragmentCount)
        {
            SessionId = sessionId;
            StreamEpoch = streamEpoch;
            FrameId = frameId;
            Width = width;
            Height = height;
            CapturedTsUtcMs = capturedTsUtcMs;
            Encoding = encoding;
            IsKeyFrame = isKeyFrame;
            FragmentCount = fragmentCount;
            FragmentBytes = new byte[fragmentCount][];
        }

        public string SessionId { get; }
        public long StreamEpoch { get; }
        public long FrameId { get; }
        public int Width { get; }
        public int Height { get; }
        public long CapturedTsUtcMs { get; }
        public string Encoding { get; }
        public bool IsKeyFrame { get; }
        public int FragmentCount { get; }
        public byte[][] FragmentBytes { get; }
        public int ReceivedFragmentCount { get; set; }
        public int TotalBytes { get; set; }

        public bool Matches(ScreenShareVideoFragmentV1 fragment)
        {
            return string.Equals(SessionId, fragment.SessionId, StringComparison.Ordinal) &&
                   StreamEpoch == fragment.StreamEpoch &&
                   FrameId == fragment.FrameId &&
                   Width == fragment.Width &&
                   Height == fragment.Height &&
                   CapturedTsUtcMs == fragment.CapturedTsUtcMs &&
                   string.Equals(Encoding, fragment.Encoding, StringComparison.Ordinal) &&
                   IsKeyFrame == fragment.IsKeyFrame &&
                   FragmentCount == fragment.FragmentCount;
        }
    }

    private sealed class ReadyFrameState
    {
        public ReadyFrameState(string sessionId, long streamEpoch, long frameId, int width, int height, long capturedTsUtcMs, string encoding, bool isKeyFrame, byte[] frameBytes, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal)
        {
            SessionId = sessionId;
            StreamEpoch = streamEpoch;
            FrameId = frameId;
            Width = width;
            Height = height;
            CapturedTsUtcMs = capturedTsUtcMs;
            Encoding = encoding;
            IsKeyFrame = isKeyFrame;
            FrameBytes = frameBytes;
            RecoveryDeliveryClass = recoveryDeliveryClass;
        }

        public string SessionId { get; }
        public long StreamEpoch { get; }
        public long FrameId { get; }
        public int Width { get; }
        public int Height { get; }
        public long CapturedTsUtcMs { get; }
        public string Encoding { get; }
        public bool IsKeyFrame { get; }
        public byte[] FrameBytes { get; }
        public ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass { get; set; }
    }

    [Conditional("DEBUG")]
    private static void AssertBounds(int inFlightCount, int readyCount)
    {
        if (inFlightCount > MaxInFlightAssembliesPerSession)
        {
            throw new InvalidOperationException($"Screenshare video receiver exceeded max of {MaxInFlightAssembliesPerSession} in-flight assemblies.");
        }

        if (readyCount > MaxReadyFramesPerSession)
        {
            throw new InvalidOperationException($"Screenshare video receiver exceeded max of {MaxReadyFramesPerSession} ready frames.");
        }
    }
}
