using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const long V6RegularNknActiveProbeSyntheticEpochBase = 4_000_000_000L;
    private const int V6RegularNknActiveProbeMinimumWatchSamples = 4;
    private const long V6RegularNknActiveProbeHighRttMs = 1500;
    private static readonly TimeSpan V6RegularNknActiveProbeCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan V6RegularNknActiveProbeTimeout = TimeSpan.FromSeconds(3);

    private static TimeSpan CurrentV6RegularNknActiveProbeCooldown =>
        V6RegularNknActiveProbeCooldownOverrideForTests ?? V6RegularNknActiveProbeCooldown;

    private static TimeSpan CurrentV6RegularNknActiveProbeTimeout =>
        V6RegularNknActiveProbeTimeoutOverrideForTests ?? V6RegularNknActiveProbeTimeout;

    private static int CurrentV6RegularNknActiveProbeMinimumWatchSamples =>
        V6RegularNknActiveProbeMinimumWatchSamplesOverrideForTests ?? V6RegularNknActiveProbeMinimumWatchSamples;

    private static bool IsV6RegularNknActiveProbeEnabled()
        => IsFileTransferEnvFlagEnabled(V6RegularNknActiveProbeEnvironmentVariableName);

    private static bool IsFileTransferEnvFlagEnabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
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

    private static void ResetOutboundV6RegularNknActiveProbeLocked(OutboundTransferContext context)
        => context.V6RegularNknActiveProbe.Reset();

    private void MaybeScheduleOutboundV6RegularNknActiveProbeLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string scoutClassification,
        string scoutRecommendation,
        string scoutReason,
        long receiverFeedbackAgeMs,
        long committedProgressGapMs)
    {
        var suppressionReason = string.Empty;
        if (!IsV6RegularNknActiveProbeEnabled() ||
            TryGetOutboundV6RegularNknPassiveScoutSuppressionReasonLocked(context, terminal: false, out suppressionReason))
        {
            if (IsV6RegularNknActiveProbeEnabled())
            {
                context.V6RegularNknActiveProbe.ConsecutiveWatchOrWorseSamples = 0;
                context.V6RegularNknActiveProbe.LastSuppressionReason = suppressionReason;
            }

            return;
        }

        var state = context.V6RegularNknActiveProbe;
        state.EverEnabled = true;
        var severity = ResolveOutboundV6RegularNknPassiveScoutSeverity(scoutClassification);
        var watchSeverity = ResolveOutboundV6RegularNknPassiveScoutSeverity("watch");
        if (severity < watchSeverity)
        {
            state.ConsecutiveWatchOrWorseSamples = 0;
            return;
        }

        state.ConsecutiveWatchOrWorseSamples++;
        UpdateOutboundV6RegularNknActiveProbeWorstScoutClassification(state, scoutClassification);

        var minimumSamples = Math.Max(1, CurrentV6RegularNknActiveProbeMinimumWatchSamples);
        if (!string.Equals(scoutClassification, "stalled", StringComparison.Ordinal) &&
            state.ConsecutiveWatchOrWorseSamples < minimumSamples)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.InFlightProbeId))
        {
            return;
        }

        var cooldown = CurrentV6RegularNknActiveProbeCooldown;
        if (state.LastProbeStartedUtc is { } lastProbeStartedUtc &&
            now - lastProbeStartedUtc < cooldown)
        {
            return;
        }

        if (context.DataSession is not { IsAvailable: true } dataSession)
        {
            state.LastSuppressionReason = "data_session_unavailable";
            return;
        }

        var probeOrdinal = ++state.NextProbeOrdinal;
        var syntheticEpoch = V6RegularNknActiveProbeSyntheticEpochBase + probeOrdinal;
        var probeId = $"v6-rnkn-probe:{probeOrdinal}:{Guid.NewGuid():N}";
        state.InFlightProbeId = probeId;
        state.InFlightSyntheticEpoch = syntheticEpoch;
        state.InFlightSentUtc = now;
        state.InFlightScoutClassification = scoutClassification;
        state.InFlightScoutRecommendation = scoutRecommendation;
        state.InFlightScoutReason = scoutReason;
        state.InFlightReceiverFeedbackAgeMs = receiverFeedbackAgeMs;
        state.InFlightCommittedProgressGapMs = committedProgressGapMs;
        state.LastProbeStartedUtc = now;
        state.ProbeCount++;
        state.Started = true;
        state.StartedUtc ??= now;
        state.FinalDryRunRecommendation = "keep_current_path";

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_active_probe_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; dry_run=1; probe_id={FormatProtocolLogValue(probeId)}; synthetic_transport_epoch={syntheticEpoch}; target_transport=regular_nkn; scout_classification={FormatProtocolLogValue(scoutClassification)}; scout_recommendation={FormatProtocolLogValue(scoutRecommendation)}; scout_reason={FormatProtocolLogValue(scoutReason)}; consecutive_watch_samples={state.ConsecutiveWatchOrWorseSamples}; receiver_feedback_age_ms={receiverFeedbackAgeMs}; committed_progress_gap_ms={committedProgressGapMs}; cooldown_ms={(long)cooldown.TotalMilliseconds}; timeout_ms={(long)CurrentV6RegularNknActiveProbeTimeout.TotalMilliseconds}");

        _ = Task.Run(() => SendOutboundV6RegularNknActiveProbeAsync(context, dataSession, probeId, syntheticEpoch));
    }

    private async Task SendOutboundV6RegularNknActiveProbeAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        string probeId,
        long syntheticEpoch)
    {
        try
        {
            var frame = new FileTransferTransportProbeFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = syntheticEpoch,
                ProbeId = probeId,
                TargetTransport = FormatFileTransferTransportKind(FileTransferTransportKind.RegularNkn),
            };
            await dataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
            _ = Task.Run(() => CompleteOutboundV6RegularNknActiveProbeTimeoutAsync(context, probeId, syntheticEpoch));
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal &&
                    string.Equals(context.V6RegularNknActiveProbe.InFlightProbeId, probeId, StringComparison.Ordinal) &&
                    context.V6RegularNknActiveProbe.InFlightSyntheticEpoch == syntheticEpoch)
                {
                    CompleteOutboundV6RegularNknActiveProbeLocked(
                        context,
                        outcome: "send_failed",
                        now: DateTimeOffset.UtcNow,
                        rttMs: -1,
                        error: ex.Message);
                }
            }
        }
    }

    private async Task CompleteOutboundV6RegularNknActiveProbeTimeoutAsync(
        OutboundTransferContext context,
        string probeId,
        long syntheticEpoch)
    {
        try
        {
            await Task.Delay(CurrentV6RegularNknActiveProbeTimeout, context.LifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                string.Equals(context.V6RegularNknActiveProbe.InFlightProbeId, probeId, StringComparison.Ordinal) &&
                context.V6RegularNknActiveProbe.InFlightSyntheticEpoch == syntheticEpoch)
            {
                CompleteOutboundV6RegularNknActiveProbeLocked(
                    context,
                    outcome: "timeout",
                    now: DateTimeOffset.UtcNow,
                    rttMs: -1,
                    error: null);
            }
        }
    }

    private bool TryCompleteOutboundV6RegularNknActiveProbeAckLocked(
        OutboundTransferContext context,
        FileTransferTransportProbeV6 message,
        DateTimeOffset now)
    {
        var state = context.V6RegularNknActiveProbe;
        if (string.IsNullOrWhiteSpace(state.InFlightProbeId) ||
            !string.Equals(state.InFlightProbeId, message.ProbeId, StringComparison.Ordinal) ||
            state.InFlightSyntheticEpoch != message.TransportEpoch ||
            ParseFileTransferTransportKind(message.TargetTransport) != FileTransferTransportKind.RegularNkn)
        {
            return false;
        }

        var rttMs = state.InFlightSentUtc is { } sentUtc
            ? (long)Math.Max(0, (now - sentUtc).TotalMilliseconds)
            : 0;
        CompleteOutboundV6RegularNknActiveProbeLocked(
            context,
            outcome: "ack",
            now,
            rttMs,
            error: null);
        return true;
    }

    private static void CompleteOutboundV6RegularNknActiveProbeLocked(
        OutboundTransferContext context,
        string outcome,
        DateTimeOffset now,
        long rttMs,
        string? error)
    {
        var state = context.V6RegularNknActiveProbe;
        var probeId = state.InFlightProbeId ?? "(none)";
        var syntheticEpoch = state.InFlightSyntheticEpoch;
        var scoutClassification = string.IsNullOrWhiteSpace(state.InFlightScoutClassification)
            ? "none"
            : state.InFlightScoutClassification;
        var scoutReason = string.IsNullOrWhiteSpace(state.InFlightScoutReason)
            ? "none"
            : state.InFlightScoutReason;
        var receiverFeedbackAgeMs = state.InFlightReceiverFeedbackAgeMs;
        var committedProgressGapMs = state.InFlightCommittedProgressGapMs;
        var elapsedMs = state.InFlightSentUtc is { } sentUtc
            ? (long)Math.Max(0, (now - sentUtc).TotalMilliseconds)
            : rttMs;
        var dryRunRecommendation = ResolveOutboundV6RegularNknActiveProbeDryRunRecommendation(
            outcome,
            rttMs,
            scoutClassification,
            scoutReason,
            receiverFeedbackAgeMs,
            committedProgressGapMs);

        if (string.Equals(outcome, "ack", StringComparison.Ordinal))
        {
            state.SuccessCount++;
            state.RttSamplesMs.Add(rttMs);
        }
        else if (string.Equals(outcome, "timeout", StringComparison.Ordinal))
        {
            state.TimeoutCount++;
        }
        else if (string.Equals(outcome, "send_failed", StringComparison.Ordinal))
        {
            state.SendFailedCount++;
        }

        ObserveOutboundV6RegularNknActiveProbeDryRunRecommendation(state, dryRunRecommendation);
        state.FinalDryRunRecommendation = dryRunRecommendation;
        state.LastOutcome = outcome;
        state.InFlightProbeId = null;
        state.InFlightSyntheticEpoch = 0;
        state.InFlightSentUtc = null;
        state.InFlightScoutClassification = "none";
        state.InFlightScoutRecommendation = "none";
        state.InFlightScoutReason = "none";
        state.InFlightReceiverFeedbackAgeMs = -1;
        state.InFlightCommittedProgressGapMs = 0;

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_active_probe_result; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; dry_run=1; probe_id={FormatProtocolLogValue(probeId)}; synthetic_transport_epoch={syntheticEpoch}; outcome={FormatProtocolLogValue(outcome)}; rtt_ms={rttMs}; elapsed_ms={elapsedMs}; scout_classification={FormatProtocolLogValue(scoutClassification)}; scout_reason={FormatProtocolLogValue(scoutReason)}; receiver_feedback_age_ms={receiverFeedbackAgeMs}; committed_progress_gap_ms={committedProgressGapMs}; error={FormatProtocolLogValue(error ?? "(none)")}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_active_probe_dry_run_decision; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; dry_run=1; probe_id={FormatProtocolLogValue(probeId)}; synthetic_transport_epoch={syntheticEpoch}; outcome={FormatProtocolLogValue(outcome)}; dry_run_recommendation={FormatProtocolLogValue(dryRunRecommendation)}; rtt_ms={rttMs}; scout_classification={FormatProtocolLogValue(scoutClassification)}; scout_reason={FormatProtocolLogValue(scoutReason)}; success_count={state.SuccessCount}; timeout_count={state.TimeoutCount}; send_failed_count={state.SendFailedCount}; worst_dry_run_recommendation={FormatProtocolLogValue(state.WorstDryRunRecommendation)}; non_keep_dry_run_recommendation_count={state.NonKeepDryRunRecommendationCount}; keep_current_path_count={state.KeepCurrentPathCount}; would_try_round_robin_probe_count={state.WouldTryRoundRobinProbeCount}; would_try_fresh_bulk_client_probe_count={state.WouldTryFreshBulkClientProbeCount}; would_pause_bulk_until_feedback_count={state.WouldPauseBulkUntilFeedbackCount}");
    }

    private static string ResolveOutboundV6RegularNknActiveProbeDryRunRecommendation(
        string outcome,
        long rttMs,
        string scoutClassification,
        string scoutReason,
        long receiverFeedbackAgeMs,
        long committedProgressGapMs)
    {
        if (string.Equals(outcome, "timeout", StringComparison.Ordinal) ||
            string.Equals(outcome, "send_failed", StringComparison.Ordinal))
        {
            return "would_try_fresh_bulk_client_probe";
        }

        if (string.Equals(scoutClassification, "stalled", StringComparison.Ordinal))
        {
            return "would_pause_bulk_until_feedback";
        }

        if (string.Equals(scoutClassification, "degraded", StringComparison.Ordinal))
        {
            return "would_try_round_robin_probe";
        }

        if (rttMs >= V6RegularNknActiveProbeHighRttMs ||
            receiverFeedbackAgeMs < 0 ||
            receiverFeedbackAgeMs >= (long)CurrentV6RegularNknPassiveScoutWatchFeedbackStale.TotalMilliseconds ||
            committedProgressGapMs >= (long)CurrentV6RegularNknPassiveScoutDegradedNoProgress.TotalMilliseconds ||
            string.Equals(scoutReason, "receiver_feedback_stale", StringComparison.Ordinal))
        {
            return "would_try_round_robin_probe";
        }

        return "keep_current_path";
    }

    private static void MaybeLogOutboundV6RegularNknActiveProbeSummaryLocked(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string terminalReason)
    {
        if (!IsV6RegularNknActiveProbeEnabled())
        {
            return;
        }

        var state = context.V6RegularNknActiveProbe;
        if (!state.Started || state.SummaryLogged)
        {
            return;
        }

        state.SummaryLogged = true;
        var durationMs = state.StartedUtc is { } startedUtc
            ? (long)Math.Max(0, (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds)
            : 0;
        var p50 = ComputeV6RegularNknActiveProbePercentile(state.RttSamplesMs, 0.50);
        var p95 = ComputeV6RegularNknActiveProbePercentile(state.RttSamplesMs, 0.95);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_active_probe_summary; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; terminal_state={terminalState}; terminal_reason={FormatProtocolLogValue(terminalReason)}; probe_count={state.ProbeCount}; success_count={state.SuccessCount}; timeout_count={state.TimeoutCount}; send_failed_count={state.SendFailedCount}; rtt_p50_ms={p50}; rtt_p95_ms={p95}; worst_scout_classification={FormatProtocolLogValue(state.WorstScoutClassification)}; final_dry_run_recommendation={FormatProtocolLogValue(state.FinalDryRunRecommendation)}; worst_dry_run_recommendation={FormatProtocolLogValue(state.WorstDryRunRecommendation)}; non_keep_dry_run_recommendation_count={state.NonKeepDryRunRecommendationCount}; keep_current_path_count={state.KeepCurrentPathCount}; would_try_round_robin_probe_count={state.WouldTryRoundRobinProbeCount}; would_try_fresh_bulk_client_probe_count={state.WouldTryFreshBulkClientProbeCount}; would_pause_bulk_until_feedback_count={state.WouldPauseBulkUntilFeedbackCount}; last_outcome={FormatProtocolLogValue(state.LastOutcome)}; suppression_reason={FormatProtocolLogValue(state.LastSuppressionReason ?? "(none)")}; duration_ms={durationMs}");
    }

    private static long ComputeV6RegularNknActiveProbePercentile(IReadOnlyCollection<long> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return -1;
        }

        var ordered = samples.OrderBy(static value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        index = Math.Clamp(index, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void UpdateOutboundV6RegularNknActiveProbeWorstScoutClassification(
        V6RegularNknActiveProbeState state,
        string classification)
    {
        var severity = ResolveOutboundV6RegularNknPassiveScoutSeverity(classification);
        if (severity <= state.WorstScoutClassificationSeverity)
        {
            return;
        }

        state.WorstScoutClassificationSeverity = severity;
        state.WorstScoutClassification = classification;
    }

    private static void ObserveOutboundV6RegularNknActiveProbeDryRunRecommendation(
        V6RegularNknActiveProbeState state,
        string recommendation)
    {
        if (string.Equals(recommendation, "keep_current_path", StringComparison.Ordinal))
        {
            state.KeepCurrentPathCount++;
        }
        else if (string.Equals(recommendation, "would_try_round_robin_probe", StringComparison.Ordinal))
        {
            state.WouldTryRoundRobinProbeCount++;
            state.NonKeepDryRunRecommendationCount++;
        }
        else if (string.Equals(recommendation, "would_try_fresh_bulk_client_probe", StringComparison.Ordinal))
        {
            state.WouldTryFreshBulkClientProbeCount++;
            state.NonKeepDryRunRecommendationCount++;
        }
        else if (string.Equals(recommendation, "would_pause_bulk_until_feedback", StringComparison.Ordinal))
        {
            state.WouldPauseBulkUntilFeedbackCount++;
            state.NonKeepDryRunRecommendationCount++;
        }

        var severity = ResolveOutboundV6RegularNknActiveProbeRecommendationSeverity(recommendation);
        if (severity <= state.WorstDryRunRecommendationSeverity)
        {
            return;
        }

        state.WorstDryRunRecommendationSeverity = severity;
        state.WorstDryRunRecommendation = recommendation;
    }

    private static int ResolveOutboundV6RegularNknActiveProbeRecommendationSeverity(string recommendation)
        => recommendation switch
        {
            "would_pause_bulk_until_feedback" => 4,
            "would_try_fresh_bulk_client_probe" => 3,
            "would_try_round_robin_probe" => 2,
            "keep_current_path" => 1,
            _ => 0,
        };

    private sealed class V6RegularNknActiveProbeState
    {
        public bool EverEnabled { get; set; }

        public bool Started { get; set; }

        public bool SummaryLogged { get; set; }

        public DateTimeOffset? StartedUtc { get; set; }

        public DateTimeOffset? LastProbeStartedUtc { get; set; }

        public int ConsecutiveWatchOrWorseSamples { get; set; }

        public int NextProbeOrdinal { get; set; }

        public int ProbeCount { get; set; }

        public int SuccessCount { get; set; }

        public int TimeoutCount { get; set; }

        public int SendFailedCount { get; set; }

        public string? InFlightProbeId { get; set; }

        public long InFlightSyntheticEpoch { get; set; }

        public DateTimeOffset? InFlightSentUtc { get; set; }

        public string InFlightScoutClassification { get; set; } = "none";

        public string InFlightScoutRecommendation { get; set; } = "none";

        public string InFlightScoutReason { get; set; } = "none";

        public long InFlightReceiverFeedbackAgeMs { get; set; } = -1;

        public long InFlightCommittedProgressGapMs { get; set; }

        public string WorstScoutClassification { get; set; } = "none";

        public int WorstScoutClassificationSeverity { get; set; }

        public string FinalDryRunRecommendation { get; set; } = "none";

        public string WorstDryRunRecommendation { get; set; } = "none";

        public int WorstDryRunRecommendationSeverity { get; set; }

        public int NonKeepDryRunRecommendationCount { get; set; }

        public int KeepCurrentPathCount { get; set; }

        public int WouldTryRoundRobinProbeCount { get; set; }

        public int WouldTryFreshBulkClientProbeCount { get; set; }

        public int WouldPauseBulkUntilFeedbackCount { get; set; }

        public string LastOutcome { get; set; } = "none";

        public string? LastSuppressionReason { get; set; }

        public List<long> RttSamplesMs { get; } = [];

        public void Reset()
        {
            EverEnabled = false;
            Started = false;
            SummaryLogged = false;
            StartedUtc = null;
            LastProbeStartedUtc = null;
            ConsecutiveWatchOrWorseSamples = 0;
            NextProbeOrdinal = 0;
            ProbeCount = 0;
            SuccessCount = 0;
            TimeoutCount = 0;
            SendFailedCount = 0;
            InFlightProbeId = null;
            InFlightSyntheticEpoch = 0;
            InFlightSentUtc = null;
            InFlightScoutClassification = "none";
            InFlightScoutRecommendation = "none";
            InFlightScoutReason = "none";
            InFlightReceiverFeedbackAgeMs = -1;
            InFlightCommittedProgressGapMs = 0;
            WorstScoutClassification = "none";
            WorstScoutClassificationSeverity = 0;
            FinalDryRunRecommendation = "none";
            WorstDryRunRecommendation = "none";
            WorstDryRunRecommendationSeverity = 0;
            NonKeepDryRunRecommendationCount = 0;
            KeepCurrentPathCount = 0;
            WouldTryRoundRobinProbeCount = 0;
            WouldTryFreshBulkClientProbeCount = 0;
            WouldPauseBulkUntilFeedbackCount = 0;
            LastOutcome = "none";
            LastSuppressionReason = null;
            RttSamplesMs.Clear();
        }
    }
}
