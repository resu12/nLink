using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const long V6RegularNknPassiveScoutTargetBytesPerSecond = 1_500_000;
    private const double V6RegularNknPassiveScoutHighFramesPerMib = 35.0;
    private static readonly TimeSpan V6RegularNknPassiveScoutSampleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan V6RegularNknPassiveScoutRecommendationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan V6RegularNknPassiveScoutWatchFeedbackStale = TimeSpan.FromMilliseconds(3500);
    private static readonly TimeSpan V6RegularNknPassiveScoutDegradedNoProgress = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan V6RegularNknPassiveScoutStalledNoProgress = TimeSpan.FromSeconds(20);

    private static TimeSpan CurrentV6RegularNknPassiveScoutSampleInterval =>
        V6RegularNknPassiveScoutSampleIntervalOverrideForTests ?? V6RegularNknPassiveScoutSampleInterval;

    private static TimeSpan CurrentV6RegularNknPassiveScoutRecommendationInterval =>
        V6RegularNknPassiveScoutRecommendationIntervalOverrideForTests ?? V6RegularNknPassiveScoutRecommendationInterval;

    private static TimeSpan CurrentV6RegularNknPassiveScoutWatchFeedbackStale =>
        V6RegularNknPassiveScoutWatchFeedbackStaleOverrideForTests ?? V6RegularNknPassiveScoutWatchFeedbackStale;

    private static TimeSpan CurrentV6RegularNknPassiveScoutDegradedNoProgress =>
        V6RegularNknPassiveScoutDegradedNoProgressOverrideForTests ?? V6RegularNknPassiveScoutDegradedNoProgress;

    private static TimeSpan CurrentV6RegularNknPassiveScoutStalledNoProgress =>
        V6RegularNknPassiveScoutStalledNoProgressOverrideForTests ?? V6RegularNknPassiveScoutStalledNoProgress;

    private static bool IsV6RegularNknPassiveScoutEnabled()
    {
        var value = Environment.GetEnvironmentVariable(V6RegularNknPassiveScoutEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetOutboundV6RegularNknPassiveScoutLocked(OutboundTransferContext context)
        => context.V6RegularNknPassiveScout.Reset();

    private static void MaybeStartOutboundV6RegularNknPassiveScoutLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string trigger)
    {
        if (!IsV6RegularNknPassiveScoutEnabled())
        {
            return;
        }

        var scout = context.V6RegularNknPassiveScout;
        if (scout.Started)
        {
            return;
        }

        scout.Started = true;
        scout.StartedUtc = now;
        scout.LastSampleUtc = now;
        scout.LastCommittedProgressUtc = now;
        scout.LastObservedCommittedChunkIndex = context.RemoteNextExpectedChunkIndex;
        scout.LastSampleCommittedChunkIndex = context.RemoteNextExpectedChunkIndex;
        scout.LastSampleCommittedBytes = ResolveOutboundCommittedBytes(context);
        scout.LastSampleRawBytesSent = context.PullSenderRawBytesRecent;
        scout.LastSampleBatchFramesSent = context.PullSenderBatchFramesRecent;
        scout.WorstClassification = "none";
        scout.FinalRecommendation = "none";

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_passive_scout_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; trigger={FormatProtocolLogValue(trigger)}; target_goodput_bytes_per_second={V6RegularNknPassiveScoutTargetBytesPerSecond}; sample_interval_ms={(long)CurrentV6RegularNknPassiveScoutSampleInterval.TotalMilliseconds}; watch_feedback_stale_ms={(long)CurrentV6RegularNknPassiveScoutWatchFeedbackStale.TotalMilliseconds}; degraded_no_progress_ms={(long)CurrentV6RegularNknPassiveScoutDegradedNoProgress.TotalMilliseconds}; stalled_no_progress_ms={(long)CurrentV6RegularNknPassiveScoutStalledNoProgress.TotalMilliseconds}; high_frames_per_mib={V6RegularNknPassiveScoutHighFramesPerMib.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static void ObserveOutboundV6RegularNknPassiveScoutReceiverStateLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        DateTimeOffset now)
    {
        if (!IsV6RegularNknPassiveScoutEnabled())
        {
            return;
        }

        var scout = context.V6RegularNknPassiveScout;
        scout.LastReceiverStateUtc = now;
        scout.LastReceiverStateEpoch = state.Epoch;
        scout.LastDurableReceivedHighestChunkIndex = state.DurableReceivedHighestChunkIndex;
        scout.LastMissingRangeCount = state.MissingRanges.Count;
        scout.LastTransferPaused = state.TransferPaused;
        scout.LastReceiverTransportEpoch = state.TransportEpoch;
    }

    private static void ObserveOutboundV6RegularNknPassiveScoutFrontierRequestLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        DateTimeOffset now)
    {
        if (!IsV6RegularNknPassiveScoutEnabled())
        {
            return;
        }

        var scout = context.V6RegularNknPassiveScout;
        scout.LastFrontierRequestUtc = now;
        scout.FrontierRequestReceivedCount++;
        scout.LastReceiverTransportEpoch = request.TransportEpoch;
    }

    private static void ObserveOutboundV6RegularNknPassiveScoutDegradedProfileEnteredLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (!IsV6RegularNknPassiveScoutEnabled())
        {
            return;
        }

        var scout = context.V6RegularNknPassiveScout;
        scout.DegradedProfileEntryCount++;
        scout.LastDegradedProfileReason = reason;
    }

    private static void MaybeSampleOutboundV6RegularNknPassiveScoutLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string trigger)
    {
        if (!IsV6RegularNknPassiveScoutEnabled() ||
            TryGetOutboundV6RegularNknPassiveScoutSuppressionReasonLocked(context, terminal: false, out _))
        {
            return;
        }

        MaybeStartOutboundV6RegularNknPassiveScoutLocked(context, now, trigger);

        var scout = context.V6RegularNknPassiveScout;
        var interval = CurrentV6RegularNknPassiveScoutSampleInterval;
        if (scout.LastSampleUtc is { } lastSampleUtc && now - lastSampleUtc < interval)
        {
            return;
        }

        var previousSampleUtc = scout.LastSampleUtc ?? scout.StartedUtc;
        var sampleWindowMs = Math.Max(1, (long)(now - previousSampleUtc).TotalMilliseconds);
        var committedChunkIndex = context.RemoteNextExpectedChunkIndex;
        var committedBytes = ResolveOutboundCommittedBytes(context);
        if (committedChunkIndex > scout.LastObservedCommittedChunkIndex)
        {
            scout.LastObservedCommittedChunkIndex = committedChunkIndex;
            scout.LastCommittedProgressUtc = now;
        }

        var committedBytesDelta = Math.Max(0, committedBytes - scout.LastSampleCommittedBytes);
        var rawBytesDelta = Math.Max(0, context.PullSenderRawBytesRecent - scout.LastSampleRawBytesSent);
        var batchFramesDelta = Math.Max(0, context.PullSenderBatchFramesRecent - scout.LastSampleBatchFramesSent);
        var committedBytesPerSecond = sampleWindowMs > 0
            ? committedBytesDelta * 1000.0 / sampleWindowMs
            : 0.0;
        var framesPerMib = rawBytesDelta > 0
            ? batchFramesDelta / (rawBytesDelta / (1024.0 * 1024.0))
            : 0.0;
        var receiverFeedbackAgeMs = context.V6LastReceiverFeedbackReceivedUtc is { } feedbackUtc
            ? (long)Math.Max(0, (now - feedbackUtc).TotalMilliseconds)
            : -1;
        var committedProgressGapMs = scout.LastCommittedProgressUtc is { } progressUtc
            ? (long)Math.Max(0, (now - progressUtc).TotalMilliseconds)
            : 0;
        var degradedActive = IsOutboundV6RegularNknDegradedProfileActiveLocked(context);
        var highFramesPerMib = framesPerMib >= V6RegularNknPassiveScoutHighFramesPerMib && batchFramesDelta > 0;
        var senderStillEmittingBulk = rawBytesDelta > 0 || batchFramesDelta > 0 || context.V6ChunkSendsInFlight.Count > 0;
        var classification = ClassifyOutboundV6RegularNknPassiveScoutWindow(
            committedBytesDelta,
            committedBytesPerSecond,
            receiverFeedbackAgeMs,
            committedProgressGapMs,
            degradedActive,
            highFramesPerMib,
            senderStillEmittingBulk);
        var recommendation = RecommendOutboundV6RegularNknPassiveScoutAction(
            classification,
            receiverFeedbackAgeMs,
            highFramesPerMib,
            committedProgressGapMs);
        var reason = ResolveOutboundV6RegularNknPassiveScoutReason(
            classification,
            receiverFeedbackAgeMs,
            highFramesPerMib,
            committedProgressGapMs,
            degradedActive,
            committedBytesPerSecond);

        scout.SampleCount++;
        scout.LastSampleUtc = now;
        scout.LastSampleCommittedChunkIndex = committedChunkIndex;
        scout.LastSampleCommittedBytes = committedBytes;
        scout.LastSampleRawBytesSent = context.PullSenderRawBytesRecent;
        scout.LastSampleBatchFramesSent = context.PullSenderBatchFramesRecent;
        scout.LastClassification = classification;
        scout.FinalRecommendation = recommendation;
        scout.MaxCommittedProgressGapMs = Math.Max(scout.MaxCommittedProgressGapMs, committedProgressGapMs);
        if (string.Equals(classification, "degraded", StringComparison.Ordinal))
        {
            scout.DegradedWindowCount++;
        }

        if (highFramesPerMib)
        {
            scout.HighFramesPerMibWindowCount++;
        }

        UpdateOutboundV6RegularNknPassiveScoutWorstClassification(scout, classification);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_passive_scout_sample; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; trigger={FormatProtocolLogValue(trigger)}; classification={classification}; recommendation={recommendation}; reason={FormatProtocolLogValue(reason)}; sample_window_ms={sampleWindowMs}; committed_bytes_delta={committedBytesDelta}; committed_bytes_per_second={committedBytesPerSecond.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; raw_bytes_sent_delta={rawBytesDelta}; batch_frames_sent_delta={batchFramesDelta}; frames_per_mib={framesPerMib.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; receiver_feedback_age_ms={receiverFeedbackAgeMs}; committed_progress_gap_ms={committedProgressGapMs}; remote_frontier_chunk_index={committedChunkIndex}; durable_received_highest_chunk_index={scout.LastDurableReceivedHighestChunkIndex}; missing_range_count={scout.LastMissingRangeCount}; frontier_request_received_count={scout.FrontierRequestReceivedCount}; degraded_profile_active={(degradedActive ? 1 : 0)}; degraded_profile_entry_count={scout.DegradedProfileEntryCount}; degraded_profile_reason={FormatProtocolLogValue(scout.LastDegradedProfileReason ?? "(none)")}; high_frames_per_mib={(highFramesPerMib ? 1 : 0)}; sender_bulk_active={(senderStillEmittingBulk ? 1 : 0)}; receiver_state_epoch={scout.LastReceiverStateEpoch}; receiver_transport_epoch={scout.LastReceiverTransportEpoch}");

        if (!string.Equals(recommendation, "none", StringComparison.Ordinal))
        {
            MaybeLogOutboundV6RegularNknPassiveScoutRecommendationLocked(
                context,
                scout,
                now,
                classification,
                recommendation,
                reason);
        }
    }

    private static bool TryGetOutboundV6RegularNknPassiveScoutSuppressionReasonLocked(
        OutboundTransferContext context,
        bool terminal,
        out string reason)
    {
        reason = string.Empty;
        if (!terminal && context.IsTerminal)
        {
            reason = "terminal";
            return true;
        }

        if (context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            reason = "protocol_not_v6";
            return true;
        }

        if (!terminal && context.State is not (FileTransferTransferState.Sending or FileTransferTransferState.AwaitingCompletion))
        {
            reason = "not_sending";
            return true;
        }

        if (context.UserPaused || context.PeerPaused)
        {
            reason = "paused";
            return true;
        }

        if (context.PullTransportPaused)
        {
            reason = "transport_paused";
            return true;
        }

        if (context.V6TransportEpoch is { } epoch)
        {
            if (IsV6TransportEpochUnresolved(epoch))
            {
                reason = "transport_epoch_unresolved";
                return true;
            }

            if (epoch.TargetTransport == FileTransferTransportKind.Tuna)
            {
                reason = "tuna_epoch_active";
                return true;
            }
        }

        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            reason = "tuna_path_active";
            return true;
        }

        return false;
    }

    private static string ClassifyOutboundV6RegularNknPassiveScoutWindow(
        long committedBytesDelta,
        double committedBytesPerSecond,
        long receiverFeedbackAgeMs,
        long committedProgressGapMs,
        bool degradedActive,
        bool highFramesPerMib,
        bool senderStillEmittingBulk)
    {
        if (committedProgressGapMs >= (long)CurrentV6RegularNknPassiveScoutStalledNoProgress.TotalMilliseconds)
        {
            return "stalled";
        }

        if (degradedActive ||
            highFramesPerMib ||
            (senderStillEmittingBulk &&
             committedBytesDelta <= 0 &&
             committedProgressGapMs >= (long)CurrentV6RegularNknPassiveScoutDegradedNoProgress.TotalMilliseconds))
        {
            return "degraded";
        }

        if (receiverFeedbackAgeMs < 0 ||
            receiverFeedbackAgeMs >= (long)CurrentV6RegularNknPassiveScoutWatchFeedbackStale.TotalMilliseconds ||
            (committedBytesDelta > 0 && committedBytesPerSecond < V6RegularNknPassiveScoutTargetBytesPerSecond) ||
            (senderStillEmittingBulk && committedBytesDelta <= 0))
        {
            return "watch";
        }

        return "healthy";
    }

    private static string RecommendOutboundV6RegularNknPassiveScoutAction(
        string classification,
        long receiverFeedbackAgeMs,
        bool highFramesPerMib,
        long committedProgressGapMs)
    {
        if (string.Equals(classification, "healthy", StringComparison.Ordinal))
        {
            return "none";
        }

        if (string.Equals(classification, "stalled", StringComparison.Ordinal) ||
            (committedProgressGapMs >= (long)CurrentV6RegularNknPassiveScoutDegradedNoProgress.TotalMilliseconds &&
             !highFramesPerMib))
        {
            return "would_probe_fresh_bulk_client";
        }

        if (highFramesPerMib)
        {
            return "would_probe_round_robin";
        }

        if (receiverFeedbackAgeMs < 0 ||
            receiverFeedbackAgeMs >= (long)CurrentV6RegularNknPassiveScoutWatchFeedbackStale.TotalMilliseconds)
        {
            return "would_probe_same_topology";
        }

        return "would_probe_round_robin";
    }

    private static string ResolveOutboundV6RegularNknPassiveScoutReason(
        string classification,
        long receiverFeedbackAgeMs,
        bool highFramesPerMib,
        long committedProgressGapMs,
        bool degradedActive,
        double committedBytesPerSecond)
    {
        if (string.Equals(classification, "healthy", StringComparison.Ordinal))
        {
            return "committed_frontier_advancing";
        }

        if (string.Equals(classification, "stalled", StringComparison.Ordinal))
        {
            return "committed_progress_stalled";
        }

        if (degradedActive)
        {
            return "degraded_profile_active";
        }

        if (highFramesPerMib)
        {
            return "high_frames_per_mib";
        }

        if (receiverFeedbackAgeMs < 0 ||
            receiverFeedbackAgeMs >= (long)CurrentV6RegularNknPassiveScoutWatchFeedbackStale.TotalMilliseconds)
        {
            return "receiver_feedback_stale";
        }

        if (committedProgressGapMs >= (long)CurrentV6RegularNknPassiveScoutDegradedNoProgress.TotalMilliseconds)
        {
            return "committed_progress_gap";
        }

        if (committedBytesPerSecond < V6RegularNknPassiveScoutTargetBytesPerSecond)
        {
            return "below_target_goodput";
        }

        return "sender_bulk_not_committed";
    }

    private static void MaybeLogOutboundV6RegularNknPassiveScoutRecommendationLocked(
        OutboundTransferContext context,
        V6RegularNknPassiveScoutState scout,
        DateTimeOffset now,
        string classification,
        string recommendation,
        string reason)
    {
        var recommendationChanged = !string.Equals(scout.LastRecommendation, recommendation, StringComparison.Ordinal) ||
                                    !string.Equals(scout.LastRecommendationReason, reason, StringComparison.Ordinal);
        var intervalElapsed = scout.LastRecommendationUtc is null ||
                              now - scout.LastRecommendationUtc.Value >= CurrentV6RegularNknPassiveScoutRecommendationInterval;
        if (!recommendationChanged && !intervalElapsed)
        {
            return;
        }

        scout.LastRecommendationUtc = now;
        scout.LastRecommendation = recommendation;
        scout.LastRecommendationReason = reason;
        scout.RecommendationCount++;

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_passive_scout_recommendation; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; classification={classification}; recommendation={recommendation}; reason={FormatProtocolLogValue(reason)}; sample_count={scout.SampleCount}; worst_classification={scout.WorstClassification}; max_committed_progress_gap_ms={scout.MaxCommittedProgressGapMs}; degraded_profile_entry_count={scout.DegradedProfileEntryCount}; frontier_request_received_count={scout.FrontierRequestReceivedCount}");
    }

    private static void MaybeLogOutboundV6RegularNknPassiveScoutSummaryLocked(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string terminalReason)
    {
        if (!IsV6RegularNknPassiveScoutEnabled())
        {
            return;
        }

        var scout = context.V6RegularNknPassiveScout;
        if (!scout.Started || scout.SummaryLogged)
        {
            return;
        }

        scout.SummaryLogged = true;
        var durationMs = scout.StartedUtc is { } startedUtc
            ? (long)Math.Max(0, (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds)
            : 0;

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_passive_scout_summary; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; terminal_state={terminalState}; terminal_reason={FormatProtocolLogValue(terminalReason)}; sample_count={scout.SampleCount}; worst_classification={scout.WorstClassification}; recommendation_count={scout.RecommendationCount}; max_committed_progress_gap_ms={scout.MaxCommittedProgressGapMs}; degraded_window_count={scout.DegradedWindowCount}; high_frames_per_mib_window_count={scout.HighFramesPerMibWindowCount}; final_recommendation={scout.FinalRecommendation}; degraded_profile_entry_count={scout.DegradedProfileEntryCount}; frontier_request_received_count={scout.FrontierRequestReceivedCount}; duration_ms={durationMs}");
    }

    private static long ResolveOutboundCommittedBytes(OutboundTransferContext context)
        => context.RemoteNextExpectedChunkIndex >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)context.RemoteNextExpectedChunkIndex * context.ChunkSizeBytes);

    private static void UpdateOutboundV6RegularNknPassiveScoutWorstClassification(
        V6RegularNknPassiveScoutState scout,
        string classification)
    {
        var severity = ResolveOutboundV6RegularNknPassiveScoutSeverity(classification);
        if (severity <= scout.WorstClassificationSeverity)
        {
            return;
        }

        scout.WorstClassificationSeverity = severity;
        scout.WorstClassification = classification;
    }

    private static int ResolveOutboundV6RegularNknPassiveScoutSeverity(string classification)
        => classification switch
        {
            "healthy" => 1,
            "watch" => 2,
            "degraded" => 3,
            "stalled" => 4,
            _ => 0,
        };

    private sealed class V6RegularNknPassiveScoutState
    {
        public bool Started { get; set; }

        public bool SummaryLogged { get; set; }

        public DateTimeOffset StartedUtc { get; set; }

        public DateTimeOffset? LastSampleUtc { get; set; }

        public DateTimeOffset? LastCommittedProgressUtc { get; set; }

        public DateTimeOffset? LastReceiverStateUtc { get; set; }

        public DateTimeOffset? LastFrontierRequestUtc { get; set; }

        public int LastObservedCommittedChunkIndex { get; set; }

        public int LastSampleCommittedChunkIndex { get; set; }

        public long LastSampleCommittedBytes { get; set; }

        public long LastSampleRawBytesSent { get; set; }

        public int LastSampleBatchFramesSent { get; set; }

        public int LastReceiverStateEpoch { get; set; } = -1;

        public int LastDurableReceivedHighestChunkIndex { get; set; } = -1;

        public int LastMissingRangeCount { get; set; }

        public bool LastTransferPaused { get; set; }

        public long LastReceiverTransportEpoch { get; set; }

        public int FrontierRequestReceivedCount { get; set; }

        public int DegradedProfileEntryCount { get; set; }

        public string? LastDegradedProfileReason { get; set; }

        public int SampleCount { get; set; }

        public int RecommendationCount { get; set; }

        public int DegradedWindowCount { get; set; }

        public int HighFramesPerMibWindowCount { get; set; }

        public long MaxCommittedProgressGapMs { get; set; }

        public string LastClassification { get; set; } = "none";

        public string WorstClassification { get; set; } = "none";

        public int WorstClassificationSeverity { get; set; }

        public string FinalRecommendation { get; set; } = "none";

        public string LastRecommendation { get; set; } = "none";

        public string LastRecommendationReason { get; set; } = "none";

        public DateTimeOffset? LastRecommendationUtc { get; set; }

        public void Reset()
        {
            Started = false;
            SummaryLogged = false;
            StartedUtc = default;
            LastSampleUtc = null;
            LastCommittedProgressUtc = null;
            LastReceiverStateUtc = null;
            LastFrontierRequestUtc = null;
            LastObservedCommittedChunkIndex = 0;
            LastSampleCommittedChunkIndex = 0;
            LastSampleCommittedBytes = 0;
            LastSampleRawBytesSent = 0;
            LastSampleBatchFramesSent = 0;
            LastReceiverStateEpoch = -1;
            LastDurableReceivedHighestChunkIndex = -1;
            LastMissingRangeCount = 0;
            LastTransferPaused = false;
            LastReceiverTransportEpoch = 0;
            FrontierRequestReceivedCount = 0;
            DegradedProfileEntryCount = 0;
            LastDegradedProfileReason = null;
            SampleCount = 0;
            RecommendationCount = 0;
            DegradedWindowCount = 0;
            HighFramesPerMibWindowCount = 0;
            MaxCommittedProgressGapMs = 0;
            LastClassification = "none";
            WorstClassification = "none";
            WorstClassificationSeverity = 0;
            FinalRecommendation = "none";
            LastRecommendation = "none";
            LastRecommendationReason = "none";
            LastRecommendationUtc = null;
        }
    }
}
