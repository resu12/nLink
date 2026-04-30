using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private int ResolveOutboundInitialPipelineDepth(OutboundTransferContext? context = null)
        => ResolveOutboundPipelineDepth(context);

    private int ResolveV3SenderTransportPipelineDepth(OutboundTransferContext context, out int configuredDepth)
    {
        configuredDepth = ResolveConfiguredV3SenderTransportPipelineDepth(context);
        if (context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3 ||
            sessionScreenShareActive ||
            sessionScreenShareDegraded)
        {
            return V3SendPipelineMinDepth;
        }

        return configuredDepth;
    }

    private int ResolveConfiguredV3SenderTransportPipelineDepth(OutboundTransferContext context)
    {
        var value = Environment.GetEnvironmentVariable(V3SendPipelineDepthEnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, V3SendPipelineMinDepth, V3SendPipelineMaxDepth);
        }

        return UsesConservativeNknStartup(transport, context.NegotiatedDataProtocolVersion)
            ? V3SendPipelineDefaultNknFileOnlyDepth
            : V3SendPipelineDefaultOtherDepth;
    }

    private long ResolveV3SenderTransportPipelinePendingBytesLimit(OutboundTransferContext context)
    {
        var value = Environment.GetEnvironmentVariable(V3SendPipelinePendingBytesEnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            long.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, V3SendPipelinePendingBytesMinLimit, V3SendPipelinePendingBytesMaxLimit);
        }

        return UsesConservativeNknStartup(transport, context.NegotiatedDataProtocolVersion) &&
               !sessionScreenShareActive &&
               !sessionScreenShareDegraded
            ? V3SendPipelinePendingBytesDefaultNknFileOnlyLimit
            : V3SendPipelinePendingBytesDefaultOtherLimit;
    }

    private bool ShouldUseAsyncOutboundV3SenderPump(OutboundTransferContext context)
    {
        if (context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3 ||
            sessionScreenShareActive ||
            sessionScreenShareDegraded ||
            !UsesConservativeNknStartup(transport, context.NegotiatedDataProtocolVersion))
        {
            return false;
        }

        var value = Environment.GetEnvironmentVariable(V3AsyncSenderPumpEnvironmentVariableName);
        return !string.Equals(value?.Trim(), "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsV3CreditKeepaliveGrantEnabled()
    {
        var value = Environment.GetEnvironmentVariable(V3CreditKeepaliveGrantsEnvironmentVariableName);
        return string.Equals(value?.Trim(), "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value?.Trim(), "on", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldUseInboundV3ReceiverFeedbackPumpLocked(InboundTransferContext context)
    {
        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return false;
        }

        var value = Environment.GetEnvironmentVariable(V3ReceiverFeedbackPumpEnvironmentVariableName);
        return !string.Equals(value?.Trim(), "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
    }

    private int ResolveOutboundPipelineDepth(OutboundTransferContext? context = null)
    {
        if (context is not null &&
            UsesConservativeNknStartup(transport, context.NegotiatedDataProtocolVersion))
        {
            return PullV3ConservativeStartupInitialPipelineDepth;
        }

        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        return sessionScreenShareActive
            ? PullScreensharePipelineDepth
            : ResolveHealthyPipelineDepth(context?.ChunkSizeBytes ?? PullHealthyDefaultChunkSizeBytes);
    }

    private int ResolveInboundMaximumPipelineDepthLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        if (sessionScreenShareActive)
        {
            return PullScreensharePipelineDepth;
        }

        return context.PullSessionDegraded
            ? PullDegradedPipelineDepth
            : ResolveHealthyPipelineDepth(context.ChunkSizeBytes);
    }

    private int ResolveInboundMinimumPipelineDepthLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        return sessionScreenShareActive
            ? PullScreensharePipelineDepth
            : PullDegradedPipelineDepth;
    }

    private int ResolveInboundRequestLowWatermarkLocked(InboundTransferContext context)
    {
        var pipelineDepth = context.PullCurrentPipelineDepth > 0
            ? context.PullCurrentPipelineDepth
            : ResolveInboundMaximumPipelineDepthLocked(context);
        if (pipelineDepth <= PullDegradedScreensharePipelineDepth)
        {
            return PullDegradedScreenshareLowWatermarkChunks;
        }

        if (sessionScreenShareActive)
        {
            return PullScreenshareLowWatermarkChunks;
        }

        if (pipelineDepth <= PullDegradedPipelineDepth)
        {
            return PullDegradedLowWatermarkChunks;
        }

        return ResolveHealthyLowWatermarkChunks(context.ChunkSizeBytes, pipelineDepth);
    }

    private int ResolveInboundAckThresholdLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return 1;
        }

        if (sessionScreenShareActive || context.PullSessionDegraded)
        {
            return 2;
        }

        return 8;
    }

    private int ResolveInboundAckCoalesceDelayMsLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullSessionScreenshareAckCoalesceDelayMs;
        }

        if (sessionScreenShareActive)
        {
            return PullSessionScreenshareAckCoalesceDelayMs;
        }

        return context.PullSessionDegraded
            ? PullSessionDegradedAckCoalesceDelayMs
            : PullSessionHealthyAckCoalesceDelayMs;
    }

    private static int GetPullSessionRequestTimeoutMs(InboundTransferContext context)
        => context.PullSessionDegraded
            ? PullSessionDegradedRequestTimeoutMs
            : PullSessionHealthyRequestTimeoutMs;

    private static int GetPullSessionRequestTimeoutMsForOutbound(OutboundTransferContext context)
        => context.PullSessionDegraded
            ? PullSessionDegradedRequestTimeoutMs
            : PullSessionHealthyRequestTimeoutMs;

    private int GetPullSessionRetryResendGateMsForOutbound(OutboundTransferContext context)
        => context.PullSessionDegraded || sessionScreenShareActive || sessionScreenShareDegraded
            ? PullSessionDegradedRetryResendGateMs
            : PullSessionHealthyRetryResendGateMs;

    private int ResolveInboundPipelineDepthForLogging(InboundTransferContext context)
        => ResolveInboundMaximumPipelineDepthLocked(context);

    private static int ResolveHealthyPipelineDepth(int chunkSizeBytes)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        var depthByBudget = (int)Math.Ceiling((double)PullHealthyTargetInFlightBytes / normalizedChunkSize);
        return Math.Clamp(depthByBudget, PullHealthyMinimumPipelineDepth, PullHealthyMaximumPipelineDepthCap);
    }

    private static int ResolveHealthyLowWatermarkChunks(int chunkSizeBytes, int pipelineDepth)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        var chunksByBudget = PullHealthyLowWatermarkBytes / normalizedChunkSize;
        return Math.Clamp(chunksByBudget, PullHealthyLowWatermarkChunks, Math.Max(PullHealthyLowWatermarkChunks, pipelineDepth - 1));
    }

    private static int NextLowerPipelineDepth(int currentDepth, int minimumDepth)
    {
        if (currentDepth > PullHealthyPipelineDepth)
        {
            return Math.Max(PullHealthyPipelineDepth, minimumDepth);
        }

        if (currentDepth > PullDegradedPipelineDepth)
        {
            return Math.Max(PullDegradedPipelineDepth, minimumDepth);
        }

        if (currentDepth > PullScreensharePipelineDepth)
        {
            return Math.Max(PullScreensharePipelineDepth, minimumDepth);
        }

        if (currentDepth > PullDegradedScreensharePipelineDepth)
        {
            return Math.Max(PullDegradedScreensharePipelineDepth, minimumDepth);
        }

        return minimumDepth;
    }

    private static int NextHigherPipelineDepth(int currentDepth, int maximumDepth)
    {
        if (currentDepth < PullScreensharePipelineDepth)
        {
            return Math.Min(PullScreensharePipelineDepth, maximumDepth);
        }

        if (currentDepth < PullDegradedPipelineDepth)
        {
            return Math.Min(PullDegradedPipelineDepth, maximumDepth);
        }

        if (currentDepth < PullHealthyPipelineDepth)
        {
            return Math.Min(PullHealthyPipelineDepth, maximumDepth);
        }

        return maximumDepth;
    }

    private static int ResolveHealthyBundledChunkFrameCount(int chunkSizeBytes)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        return Math.Max(1, PullHealthyBundledRawBytesCap / normalizedChunkSize);
    }

    private bool RefreshInboundPullPipelineDepthLocked(InboundTransferContext context, bool allowRecoveryIncrease, out int previousDepth, out int updatedDepth)
    {
        previousDepth = context.PullCurrentPipelineDepth;
        var maximumDepth = ResolveInboundMaximumPipelineDepthLocked(context);
        var minimumDepth = ResolveInboundMinimumPipelineDepthLocked(context);

        if (previousDepth <= 0)
        {
            updatedDepth = maximumDepth;
        }
        else if (previousDepth > maximumDepth)
        {
            updatedDepth = maximumDepth;
        }
        else if (previousDepth < minimumDepth)
        {
            updatedDepth = minimumDepth;
        }
        else if (!allowRecoveryIncrease ||
                 previousDepth >= maximumDepth ||
                 context.PullLateArrivalDistance > 1)
        {
            updatedDepth = previousDepth;
        }
        else if (context.PullRecoverySinceUtc is null ||
                 DateTimeOffset.UtcNow - context.PullRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PullSessionRecoveryHoldMs))
        {
            updatedDepth = previousDepth;
        }
        else
        {
            updatedDepth = NextHigherPipelineDepth(previousDepth, maximumDepth);
            if (updatedDepth > previousDepth)
            {
                context.PullRecoverySinceUtc = DateTimeOffset.UtcNow;
                context.PullCurrentPipelineStep = updatedDepth;
            }
        }

        context.PullCurrentPipelineDepth = updatedDepth;
        return previousDepth != updatedDepth;
    }

    private static void LogPullPipelineChanged(string transferId, string sessionId, FileTransferDirection direction, int pipelineDepth, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pipeline_changed; direction={direction}; transfer_id={transferId}; session_id={sessionId}; pipeline_depth={pipelineDepth}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullPipelineRecoveryStep(string transferId, string sessionId, int previousDepth, int updatedDepth, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pipeline_recovery_step; transfer_id={transferId}; session_id={sessionId}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullProfileStepDown(string transferId, string sessionId, string reason, int previousDepth, int updatedDepth, int chunkSizeBytes, int outstandingCount, int lateArrivalDistance)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_step_down; transfer_id={transferId}; session_id={sessionId}; reason={reason}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; chunk_size_bytes={chunkSizeBytes}; outstanding_count={outstandingCount}; late_arrival_distance={lateArrivalDistance}");
    }

    private static void LogPullProfileStepUp(string transferId, string sessionId, int previousDepth, int updatedDepth, int chunkSizeBytes, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_step_up; transfer_id={transferId}; session_id={sessionId}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; chunk_size_bytes={chunkSizeBytes}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullReorderPressure(string transferId, string sessionId, int nextExpectedChunkIndex, int highestReceivedChunkIndex, int lateArrivalDistance, int outstandingCount, int pipelineDepth, int chunkSizeBytes)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_reorder_pressure; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; late_arrival_distance={lateArrivalDistance}; outstanding_count={outstandingCount}; pipeline_depth={pipelineDepth}; chunk_size_bytes={chunkSizeBytes}");
    }

    private static void LogGapFocusChanged(string transferId, string sessionId, bool active, int nextExpectedChunkIndex, int highestReceivedChunkIndex, int lateArrivalDistance)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_gap_focus_{(active ? "entered" : "exited")}; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; late_arrival_distance={lateArrivalDistance}");
    }

    private static void LogPullProfileClampForScreenshare(string transferId, string sessionId, string reason, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_clamped_for_screenshare; transfer_id={transferId}; session_id={sessionId}; reason={reason}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private static void LogPullProfileRecoveredAfterScreenshare(string transferId, string sessionId, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_recovered_after_screenshare; transfer_id={transferId}; session_id={sessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private bool TryStepDownInboundPipelineLocked(InboundTransferContext context, string reason, int outstandingCount, out int previousDepth, out int updatedDepth)
    {
        previousDepth = context.PullCurrentPipelineDepth > 0
            ? context.PullCurrentPipelineDepth
            : ResolveInboundMaximumPipelineDepthLocked(context);
        updatedDepth = previousDepth;
        var minimumDepth = ResolveInboundMinimumPipelineDepthLocked(context);
        var now = DateTimeOffset.UtcNow;
        if (previousDepth <= minimumDepth)
        {
            return false;
        }

        if (context.PullLastProfileAdjustmentUtc is not null &&
            now - context.PullLastProfileAdjustmentUtc.Value < TimeSpan.FromMilliseconds(PullProfileAdjustmentCooldownMs))
        {
            return false;
        }

        updatedDepth = NextLowerPipelineDepth(previousDepth, minimumDepth);
        if (updatedDepth == previousDepth)
        {
            return false;
        }

        context.PullCurrentPipelineDepth = updatedDepth;
        context.PullCurrentPipelineStep = updatedDepth;
        context.PullRecoverySinceUtc = null;
        context.PullLastProfileAdjustmentUtc = now;
        ClearPendingInboundReorderStepDownLocked(context);
        LogPullProfileStepDown(
            context.TransferId,
            context.SessionId,
            reason,
            previousDepth,
            updatedDepth,
            context.ChunkSizeBytes,
            outstandingCount,
            context.PullLateArrivalDistance);
        return true;
    }

    private static void ClearPendingInboundReorderStepDownLocked(InboundTransferContext context)
    {
        context.PullReorderPressureSinceUtc = null;
        context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
    }

    private bool ShouldDelayHealthyReorderStepDownLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (sessionScreenShareActive ||
            sessionScreenShareDegraded ||
            context.PullSessionDegraded ||
            context.PullCurrentPipelineDepth < PullHealthyPipelineDepth)
        {
            ClearPendingInboundReorderStepDownLocked(context);
            return false;
        }

        if (context.PullReorderPressureSinceUtc is null)
        {
            context.PullReorderPressureSinceUtc = now;
            context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
            return true;
        }

        if (context.NextChunkIndex > context.PullReorderPressureFrontierChunkIndex)
        {
            context.PullReorderPressureSinceUtc = now;
            context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
            return true;
        }

        return now - context.PullReorderPressureSinceUtc.Value < TimeSpan.FromMilliseconds(PullHealthyReorderStepDownHoldMs);
    }

    private static void LogPullChunkProfile(string transferId, string? sessionId, int chunkSizeBytes, int pipelineDepth, bool screenshareActive, bool screenshareDegraded)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "(none)" : sessionId.Trim();
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_chunk_profile; transfer_id={transferId}; session_id={normalizedSessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}; screenshare_active={(screenshareActive ? "yes" : "no")}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_selected; transfer_id={transferId}; session_id={normalizedSessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}; screenshare_active={(screenshareActive ? "yes" : "no")}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");
    }

    private static void LogPullBatchCommit(string transferId, string sessionId, int contiguousChunkCount, int nextExpectedChunkIndex, long bytesCommitted)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_batch_commit; transfer_id={transferId}; session_id={sessionId}; contiguous_chunk_count={contiguousChunkCount}; next_expected_chunk={nextExpectedChunkIndex}; bytes_committed={bytesCommitted}");
    }

    private static void LogReceiverBufferPressureEntered(InboundTransferContext context, string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_receiver_buffer_pressure_entered; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; soft_limit_bytes={ReceiverBufferSoftLimitBytes}; severe_limit_bytes={ReceiverBufferSevereLimitBytes}; emergency_limit_bytes={ReceiverBufferEmergencyLimitBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}");
    }

    private static void LogReceiverBufferPressureExited(InboundTransferContext context, long durationMs)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receiver_buffer_pressure_exited; transfer_id={context.TransferId}; session_id={context.SessionId}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; duration_ms={durationMs}; exit_limit_bytes={ReceiverBufferExitLimitBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}");
    }

    private static void LogReceiverWriteBatchCommitted(InboundTransferContext context, InboundWriteBatch batch, long writeDurationMs)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receiver_write_batch_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_chunk_count={batch.ChunkCount}; batch_bytes={batch.ByteCount}; write_duration_ms={writeDurationMs}; pending_chunk_count={batch.PendingChunkCountAfterDequeue}; pending_bytes={batch.PendingBytesAfterDequeue}; next_chunk_index={batch.NextChunkIndexAfterDequeue}; highest_received_chunk_index={batch.HighestReceivedChunkIndex}; late_arrival_distance={batch.LateArrivalDistance}; granted_window_bytes={batch.GrantedWindowBytes}");
    }

    private static long GetInboundV3GrantedWindowBytesLocked(InboundTransferContext context)
        => Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * (long)Math.Max(1, context.ChunkSizeBytes);

    private static int GetExpectedInboundChunkLength(InboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= context.ChunkCount || context.ChunkSizeBytes <= 0)
        {
            return 0;
        }

        var offset = (long)chunkIndex * context.ChunkSizeBytes;
        var remaining = Math.Max(0, context.FileSizeBytes - offset);
        return (int)Math.Min(context.ChunkSizeBytes, remaining);
    }

    private static bool IsInboundV3ChunkPresentOrPendingLocked(InboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < context.NextChunkIndex)
        {
            return true;
        }

        if (!context.ReceiverSparseWriteActive)
        {
            return context.PendingChunks.ContainsKey(chunkIndex);
        }

        return context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex) ||
               context.ReceiverSparseChunksWritten is not null &&
               chunkIndex >= 0 &&
               chunkIndex < context.ReceiverSparseChunksWritten.Length &&
               context.ReceiverSparseChunksWritten[chunkIndex];
    }

    private static int GetReceiverPendingChunkCountLocked(InboundTransferContext context)
        => context.ReceiverSparseWriteActive
            ? context.ReceiverSparseChunksPendingWrite.Count
            : context.PendingChunks.Count;

    private static bool HasAnyInboundV3ChunkPresentOrPendingAheadLocked(InboundTransferContext context)
    {
        if (!context.ReceiverSparseWriteActive)
        {
            return context.PendingChunks.Count > 0;
        }

        if (context.ReceiverSparseChunksPendingWrite.Count > 0)
        {
            return true;
        }

        if (context.ReceiverSparseChunksWritten is null)
        {
            return false;
        }

        for (var chunkIndex = context.NextChunkIndex; chunkIndex < context.ReceiverSparseChunksWritten.Length; chunkIndex++)
        {
            if (context.ReceiverSparseChunksWritten[chunkIndex])
            {
                return true;
            }
        }

        return false;
    }

    private static int GetSparseWrittenAheadChunkCountLocked(InboundTransferContext context)
    {
        if (!context.ReceiverSparseWriteActive || context.ReceiverSparseChunksWritten is null)
        {
            return 0;
        }

        var count = 0;
        for (var chunkIndex = context.NextChunkIndex; chunkIndex < context.ReceiverSparseChunksWritten.Length; chunkIndex++)
        {
            if (context.ReceiverSparseChunksWritten[chunkIndex])
            {
                count++;
            }
        }

        return count;
    }

    private static long GetSparseWrittenAheadBytesLocked(InboundTransferContext context)
    {
        if (!context.ReceiverSparseWriteActive || context.ReceiverSparseChunksWritten is null)
        {
            return 0;
        }

        var bytes = 0L;
        for (var chunkIndex = context.NextChunkIndex; chunkIndex < context.ReceiverSparseChunksWritten.Length; chunkIndex++)
        {
            if (context.ReceiverSparseChunksWritten[chunkIndex])
            {
                bytes += GetExpectedInboundChunkLength(context, chunkIndex);
            }
        }

        return bytes;
    }

    private static int GetSparseGapCountLocked(InboundTransferContext context)
    {
        if (!context.ReceiverSparseWriteActive || context.ReceiverSparseChunksWritten is null)
        {
            return 0;
        }

        var gaps = 0;
        var inGap = false;
        var sawWrittenAhead = false;
        for (var chunkIndex = context.NextChunkIndex; chunkIndex <= context.PullHighestReceivedChunkIndex && chunkIndex < context.ReceiverSparseChunksWritten.Length; chunkIndex++)
        {
            if (context.ReceiverSparseChunksWritten[chunkIndex])
            {
                sawWrittenAhead = true;
                inGap = false;
                continue;
            }

            if (!inGap && sawWrittenAhead)
            {
                gaps++;
                inGap = true;
            }
        }

        return gaps;
    }

    private static long GetOutboundV3SentCacheBytesLocked(OutboundTransferContext context)
        => context.PullSentChunkCacheBytes;

    private static long GetWindowBytesPerSecond(long bytes)
        => (long)Math.Round(bytes / (PullControlChatterWindowMs / 1000D));

    private static long GetRecentPercentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values
            .Where(static value => value >= 0)
            .OrderBy(static value => value)
            .ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling((percentile / 100D) * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static void TrimRecentEvents(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek() > TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            events.Dequeue();
        }
    }

    private static long UpdateInboundGapStallTrackingLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.PullHighestReceivedChunkIndex <= context.NextChunkIndex)
        {
            context.PullV3GapStallSinceUtc = null;
            context.PullV3GapStallStartChunkIndex = -1;
            return 0;
        }

        if (context.PullV3GapStallSinceUtc is null ||
            context.PullV3GapStallStartChunkIndex != context.NextChunkIndex)
        {
            context.PullV3GapStallSinceUtc = now;
            context.PullV3GapStallStartChunkIndex = context.NextChunkIndex;
            return 0;
        }

        return (long)Math.Max(0, (now - context.PullV3GapStallSinceUtc.Value).TotalMilliseconds);
    }

    private void MaybeLogOutboundV3SenderThroughputWindowLocked(OutboundTransferContext context, DateTimeOffset now, bool force = false)
    {
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV3)
        {
            return;
        }

        if (!force &&
            context.LastSenderThroughputLogUtc is not null &&
            now - context.LastSenderThroughputLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        context.LastSenderThroughputLogUtc = now;
        var rawBytesPerSecond = GetWindowBytesPerSecond(context.PullSenderRawBytesRecent);
        var remoteNextExpectedChunkIndex = Math.Max(0, context.ChunksTransferred);
        var remoteGrantedUntilChunkIndexExclusive = Math.Max(remoteNextExpectedChunkIndex, Math.Min(context.ChunkCount, context.PullV3GrantedUntilExclusive));
        var remoteGrantedWindowBytes = Math.Max(0, remoteGrantedUntilChunkIndexExclusive - remoteNextExpectedChunkIndex) * (long)Math.Max(1, context.ChunkSizeBytes);
        var sentCacheBytes = GetOutboundV3SentCacheBytesLocked(context);
        var sourceCanSeek = context.PullSourceCanSeek ? 1 : 0;
        var cacheHardLimitBytes = GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek);
        var pendingBytesLimit = ResolveV3SenderTransportPipelinePendingBytesLimit(context);
        var interScheduleGapP95Ms = GetRecentPercentile(context.PullSenderFeedInterScheduleGapMsRecent, 95D);
        var interScheduleGapMaxMs = context.PullSenderFeedInterScheduleGapMsRecent.Count == 0
            ? 0
            : context.PullSenderFeedInterScheduleGapMsRecent.Max();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_sender_throughput_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; sample_window_ms={PullControlChatterWindowMs}; raw_bytes_sent={context.PullSenderRawBytesRecent}; raw_bytes_per_second={rawBytesPerSecond}; chunk_frames_sent={context.PullSenderChunkFramesRecent}; batch_frames_sent={context.PullSenderBatchFramesRecent}; chunk_count_sent={context.PullSenderChunkCountRecent}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_next_expected_chunk_index={remoteNextExpectedChunkIndex}; remote_granted_until_chunk_index_exclusive={remoteGrantedUntilChunkIndexExclusive}; remote_granted_window_bytes={remoteGrantedWindowBytes}; sent_cache_chunk_count={context.PullSentChunkCache.Count}; sent_cache_bytes={sentCacheBytes}; source_can_seek={sourceCanSeek}; cache_hard_limit_bytes={cacheHardLimitBytes}; cache_hit_count={context.PullSenderCacheHitCountRecent}; cache_miss_count={context.PullSenderCacheMissCountRecent}; source_reread_count={context.PullSenderSourceRereadCountRecent}; cache_eviction_count={context.PullSenderCacheEvictionCountRecent}; repair_chunk_skipped_count={context.PullSenderRepairChunkSkippedCountRecent}; send_wait_count={context.PullSenderSendWaitCountRecent}; repair_send_count={context.PullSenderRepairSendCountRecent}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_sender_pipeline_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; sample_window_ms={PullControlChatterWindowMs}; configured_depth={context.PullSenderPipelineConfiguredDepthRecent}; effective_depth={context.PullSenderPipelineEffectiveDepthRecent}; in_flight_frames={context.PullSenderPipelineCurrentInFlightFrames}; in_flight_bytes={context.PullSenderPipelineCurrentInFlightBytes}; in_flight_frames_max={context.PullSenderPipelineMaxInFlightFramesRecent}; in_flight_bytes_max={context.PullSenderPipelineMaxInFlightBytesRecent}; scheduled_frames={context.PullSenderPipelineScheduledFramesRecent}; completed_frames={context.PullSenderPipelineCompletedFramesRecent}; failed_frames={context.PullSenderPipelineFailedFramesRecent}; fifo_wait_ms={context.PullSenderPipelineFifoWaitMsRecent}; fifo_wait_max_ms={context.PullSenderPipelineMaxFifoWaitMsRecent}; accepted_progress_lag_bytes_max={context.PullSenderPipelineMaxAcceptedProgressLagBytesRecent}; pending_bytes_limit={pendingBytesLimit}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_sender_feed_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; sample_window_ms={PullControlChatterWindowMs}; chunk_frames_prepared={context.PullSenderFeedChunkFramesPreparedRecent}; batch_frames_prepared={context.PullSenderFeedBatchFramesPreparedRecent}; chunk_count_prepared={context.PullSenderFeedChunkCountPreparedRecent}; raw_bytes_prepared={context.PullSenderFeedRawBytesPreparedRecent}; read_duration_ms={context.PullSenderFeedReadDurationMsRecent}; batch_prepare_duration_ms={context.PullSenderFeedBatchPrepareDurationMsRecent}; send_async_schedule_duration_ms={context.PullSenderFeedScheduleDurationMsRecent}; inter_schedule_gap_p95_ms={interScheduleGapP95Ms}; inter_schedule_gap_max_ms={interScheduleGapMaxMs}; credit_wait_duration_ms={context.PullSenderFeedCreditWaitMsRecent}; pipeline_slot_wait_duration_ms={context.PullSenderFeedPipelineSlotWaitMsRecent}; effective_depth={context.PullSenderPipelineEffectiveDepthRecent}; pending_bytes={context.PullSenderPipelineCurrentInFlightBytes}; pending_bytes_limit={pendingBytesLimit}; source_read_error_count={context.PullSenderFeedSourceReadErrorCountRecent}");

        context.PullSenderRawBytesRecent = 0;
        context.PullSenderChunkFramesRecent = 0;
        context.PullSenderBatchFramesRecent = 0;
        context.PullSenderChunkCountRecent = 0;
        context.PullSenderSendWaitCountRecent = 0;
        context.PullSenderRepairSendCountRecent = 0;
        context.PullSenderCacheHitCountRecent = 0;
        context.PullSenderCacheMissCountRecent = 0;
        context.PullSenderSourceRereadCountRecent = 0;
        context.PullSenderCacheEvictionCountRecent = 0;
        context.PullSenderRepairChunkSkippedCountRecent = 0;
        context.PullSenderPipelineScheduledFramesRecent = 0;
        context.PullSenderPipelineCompletedFramesRecent = 0;
        context.PullSenderPipelineFailedFramesRecent = 0;
        context.PullSenderPipelineFifoWaitMsRecent = 0;
        context.PullSenderPipelineMaxFifoWaitMsRecent = 0;
        context.PullSenderPipelineMaxInFlightFramesRecent = context.PullSenderPipelineCurrentInFlightFrames;
        context.PullSenderPipelineMaxInFlightBytesRecent = context.PullSenderPipelineCurrentInFlightBytes;
        context.PullSenderPipelineMaxAcceptedProgressLagBytesRecent = 0;
        context.PullSenderFeedChunkFramesPreparedRecent = 0;
        context.PullSenderFeedBatchFramesPreparedRecent = 0;
        context.PullSenderFeedChunkCountPreparedRecent = 0;
        context.PullSenderFeedRawBytesPreparedRecent = 0;
        context.PullSenderFeedReadDurationMsRecent = 0;
        context.PullSenderFeedBatchPrepareDurationMsRecent = 0;
        context.PullSenderFeedScheduleDurationMsRecent = 0;
        context.PullSenderFeedCreditWaitMsRecent = 0;
        context.PullSenderFeedPipelineSlotWaitMsRecent = 0;
        context.PullSenderFeedSourceReadErrorCountRecent = 0;
        context.PullSenderFeedInterScheduleGapMsRecent.Clear();
    }

    private void MaybeLogPullControlChatterWindow(InboundTransferContext context, string transferId, string sessionId, DateTimeOffset now)
    {
        TrimRecentEvents(context.RecentPullAckSentUtc, now);
        TrimRecentEvents(context.RecentPullRequestSentUtc, now);
        TrimRecentEvents(context.RecentPullChunkSentUtc, now);
        var oldestGapAgeMs = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3
            ? UpdateInboundGapStallTrackingLocked(context, now)
            : 0L;
        if (context.LastPullControlChatterLogUtc is not null &&
            now - context.LastPullControlChatterLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        context.LastPullControlChatterLogUtc = now;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_control_chatter_window; transfer_id={transferId}; session_id={sessionId}; ack_count_recent={context.RecentPullAckSentUtc.Count}; request_count_recent={context.RecentPullRequestSentUtc.Count}; chunk_sent_count_recent={context.RecentPullChunkSentUtc.Count}; duplicate_request_ignored_count_recent={context.PullDuplicateRequestIgnoredCountRecent}; resend_suppressed_count_recent={context.PullResendSuppressedCountRecent}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_useful_payload_window; transfer_id={transferId}; session_id={sessionId}; useful_payload_bytes_recent={context.PullUsefulPayloadBytesRecent}; ack_count_recent={context.RecentPullAckSentUtc.Count}; request_count_recent={context.RecentPullRequestSentUtc.Count}; chunk_sent_count_recent={context.RecentPullChunkSentUtc.Count}; duplicate_request_ignored_count_recent={context.PullDuplicateRequestIgnoredCountRecent}; resend_suppressed_count_recent={context.PullResendSuppressedCountRecent}");
        if (context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3)
        {
            var controlFrameCount = context.RecentPullAckSentUtc.Count + context.RecentPullRequestSentUtc.Count;
            var usefulPayloadBytesPerSecond = GetWindowBytesPerSecond(context.PullUsefulPayloadBytesRecent);
            var controlFramesPerMiB = context.PullUsefulPayloadBytesRecent <= 0
                ? 0D
                : controlFrameCount / (context.PullUsefulPayloadBytesRecent / 1048576D);
            var grantedWindowBytes = Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * Math.Max(1, context.ChunkSizeBytes);
            var conservativeStartupDurationMs = GetConservativeStartupDurationMs(context, DateTimeOffset.UtcNow);
            var bytesBeforeStartupExit = GetConservativeStartupBytesBeforeExit(context);
            var startupProbeWindowBytes = context.PullV3ConservativeStartupProbeActive ? PullV3ConservativeStartupProbeTargetInFlightBytes : 0;
            var firstRepairOrTimeoutBeforeStartupExit = context.PullV3FirstRepairOrTimeoutBeforeStartupExit ? 1 : 0;
            var rawBytesReceivedPerSecond = GetWindowBytesPerSecond(context.PullReceiverRawBytesRecent);
            var contiguousBytesCommittedPerSecond = GetWindowBytesPerSecond(context.PullReceiverContiguousBytesCommittedRecent);
            var sparseWriteBytesPerSecond = GetWindowBytesPerSecond(context.PullReceiverSparseWriteBytesRecent);
            var sparseWrittenAheadBytes = GetSparseWrittenAheadBytesLocked(context);
            var sparseGapCount = GetSparseGapCountLocked(context);
            var sparseMode = context.ReceiverSparseWriteActive ? 1 : 0;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v3_throughput_summary; transfer_id={transferId}; session_id={sessionId}; useful_payload_bytes_per_second={usefulPayloadBytesPerSecond}; control_frames_per_mib={controlFramesPerMiB:F2}; granted_window_bytes={grantedWindowBytes}; chunk_size_bytes={context.ChunkSizeBytes}; profile={ResolveInboundV3ProfileName(context)}; conservative_startup_duration_ms={conservativeStartupDurationMs}; bytes_before_startup_exit={bytesBeforeStartupExit}; startup_exit_reason={GetConservativeStartupExitReason(context)}; startup_probe_window_bytes={startupProbeWindowBytes}; first_repair_or_timeout_before_startup_exit={firstRepairOrTimeoutBeforeStartupExit}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v3_receiver_throughput_summary; transfer_id={transferId}; session_id={sessionId}; sample_window_ms={PullControlChatterWindowMs}; raw_bytes_received={context.PullReceiverRawBytesRecent}; raw_bytes_received_per_second={rawBytesReceivedPerSecond}; contiguous_bytes_committed={context.PullReceiverContiguousBytesCommittedRecent}; contiguous_bytes_committed_per_second={contiguousBytesCommittedPerSecond}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; oldest_gap_age_ms={oldestGapAgeMs}; granted_until_chunk_index_exclusive={context.PullV3GrantedUntilExclusive}; granted_window_bytes={grantedWindowBytes}; write_batch_count={context.PullReceiverWriteBatchCountRecent}; write_batch_bytes={context.PullReceiverWriteBatchBytesRecent}; write_duration_ms={context.PullReceiverWriteDurationMsRecent}; sparse_mode={sparseMode}; sparse_write_bytes_per_second={sparseWriteBytesPerSecond}; sparse_written_ahead_bytes={sparseWrittenAheadBytes}; sparse_gap_count={sparseGapCount}");
            if (oldestGapAgeMs >= PullControlChatterWindowMs)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v3_gap_stall_summary; transfer_id={transferId}; session_id={sessionId}; sample_window_ms={PullControlChatterWindowMs}; gap_start_chunk_index={context.PullV3GapStallStartChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; stall_duration_ms={oldestGapAgeMs}; pending_bytes={context.BufferedBytes}; granted_window_bytes={grantedWindowBytes}; sparse_mode={sparseMode}; sparse_written_ahead_bytes={sparseWrittenAheadBytes}; sparse_gap_count={sparseGapCount}");
            }

            if (context.ReceiverSparseWriteActive && context.PullReceiverSparseWriteBatchCountRecent > 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_receiver_sparse_write_summary; transfer_id={transferId}; session_id={sessionId}; written_chunk_count={context.PullReceiverSparseChunksWrittenRecent}; written_bytes={context.PullReceiverSparseWriteBytesRecent}; sparse_write_bytes_per_second={sparseWriteBytesPerSecond}; write_duration_ms={context.PullReceiverSparseWriteDurationMsRecent}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; queued_memory_bytes={context.BufferedBytes}; sparse_written_ahead_chunks={GetSparseWrittenAheadChunkCountLocked(context)}; sparse_written_ahead_bytes={sparseWrittenAheadBytes}; sparse_gap_count={sparseGapCount}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={grantedWindowBytes}");
                if (context.PullReceiverSparseContiguousChunksCommittedRecent > 0)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_receiver_sparse_commit_summary; transfer_id={transferId}; session_id={sessionId}; contiguous_chunks_committed={context.PullReceiverSparseContiguousChunksCommittedRecent}; contiguous_bytes_committed={context.PullReceiverContiguousBytesCommittedRecent}; next_chunk_index={context.NextChunkIndex}; bytes_committed={context.BytesTransferred}; sparse_written_ahead_chunks={GetSparseWrittenAheadChunkCountLocked(context)}; sparse_written_ahead_bytes={sparseWrittenAheadBytes}; sparse_gap_count={sparseGapCount}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; queued_memory_bytes={context.BufferedBytes}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={grantedWindowBytes}");
                }
            }
        }
        context.PullDuplicateRequestIgnoredCountRecent = 0;
        context.PullResendSuppressedCountRecent = 0;
        context.PullUsefulPayloadBytesRecent = 0;
        context.PullReceiverRawBytesRecent = 0;
        context.PullReceiverContiguousBytesCommittedRecent = 0;
        context.PullReceiverWriteBatchCountRecent = 0;
        context.PullReceiverWriteBatchBytesRecent = 0;
        context.PullReceiverWriteDurationMsRecent = 0;
        context.PullReceiverSparseWriteBytesRecent = 0;
        context.PullReceiverSparseWriteBatchCountRecent = 0;
        context.PullReceiverSparseWriteDurationMsRecent = 0;
        context.PullReceiverSparseChunksWrittenRecent = 0;
        context.PullReceiverSparseContiguousChunksCommittedRecent = 0;
    }

    private Task InitializeInboundPullManifestAsync(InboundTransferContext context, FileTransferManifestFrameV2 manifest)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool sparseModeSelected = false;
        bool streamCanRead = false;
        bool streamCanSeek = false;
        bool streamCanWrite = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return Task.CompletedTask;
            }

            if (!string.Equals(context.FileName, manifest.FileName, StringComparison.Ordinal) ||
                context.FileSizeBytes != manifest.FileSizeBytes ||
                !string.Equals(context.Sha256Base64, manifest.Sha256Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Manifest metadata did not match the original offer.");
            }

            context.ChunkCount = manifest.ChunkCount;
            context.ChunkSizeBytes = manifest.ChunkSizeBytes;
            streamCanRead = context.WriteStream?.CanRead == true;
            streamCanSeek = context.WriteStream?.CanSeek == true;
            streamCanWrite = context.WriteStream?.CanWrite == true;
            sparseModeSelected =
                context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3 &&
                streamCanWrite &&
                streamCanSeek &&
                streamCanRead &&
                manifest.ChunkCount > 0;
            context.ReceiverSparseWriteActive = sparseModeSelected;
            context.ReceiverSparseChunksWritten = sparseModeSelected
                ? new System.Collections.BitArray(manifest.ChunkCount)
                : null;
            context.ReceiverSparseChunksPendingWrite.Clear();
            context.ReceiverSparseBytesWritten = 0;
            context.PullReceiverSparseWriteBytesRecent = 0;
            context.PullReceiverSparseWriteBatchCountRecent = 0;
            context.PullReceiverSparseWriteDurationMsRecent = 0;
            context.PullReceiverSparseChunksWrittenRecent = 0;
            context.PullReceiverSparseContiguousChunksCommittedRecent = 0;
            context.PullV3LastSparseCreditEligibleUtc = null;
            context.PullV3LastSparseCreditBaseChunkIndex = 0;
            context.State = FileTransferTransferState.Receiving;
            context.StatusMessage = "Receiving requested chunks.";
            context.PullManifestReceived = true;
            RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: false, out _, out _);
            context.PullCurrentPipelineStep = context.PullCurrentPipelineDepth;
            context.PullCurrentChunkSizeStep = context.ChunkSizeBytes;
            context.PullLastProgressUtc = DateTimeOffset.UtcNow;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        if (sparseModeSelected)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_receiver_sparse_mode_selected; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=seekable_readable_destination; stream_can_read={(streamCanRead ? 1 : 0)}; stream_can_seek={(streamCanSeek ? 1 : 0)}; stream_can_write={(streamCanWrite ? 1 : 0)}; file_size_bytes={context.FileSizeBytes}; chunk_count={context.ChunkCount}; chunk_size_bytes={context.ChunkSizeBytes}");
        }

        LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
        return Task.CompletedTask;
    }

    private Task<bool> MaybeSendNextChunkRequestAsync(InboundTransferContext context, bool forceResendOldestOutstanding)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived ||
                context.ChunkCount <= 0)
            {
                return Task.FromResult(false);
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_legacy_request_suppressed_in_v3; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; requested_until_exclusive={context.PullRequestedFrontierExclusive}; force_resend_oldest_outstanding={forceResendOldestOutstanding}; reason=legacy_request_path_invoked");
            return Task.FromResult(false);
        }
    }

    private bool TryPauseOutboundTransportLocked(OutboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullSessionActive || context.DataSession is null)
        {
            return false;
        }

        if (context.PullTransportPaused)
        {
            context.PullTransportResumeRequestPending |= requiresResumeRequest;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = now;
        context.PullTransportGraceDeadlineUtc = now.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryPauseInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullSessionActive || context.DataSession is null)
        {
            return false;
        }

        if (context.PullTransportPaused)
        {
            context.PullTransportResumeRequestPending |= requiresResumeRequest;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = now;
        context.PullTransportGraceDeadlineUtc = now.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryResumeOutboundTransportLocked(OutboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullTransportPaused)
        {
            return false;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryResumeInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullTransportPaused)
        {
            return false;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        context.PullTimeoutOldestChunkIndex = null;
        context.PullTimeoutStreak = 0;
        context.PullFirstChunkTimeoutCount = 0;
        context.PullRecoverySinceUtc = null;
        return true;
    }

    private async Task<bool> HandlePausedOutboundTransportAsync(OutboundTransferContext context)
    {
        DateTimeOffset? graceDeadlineUtc;
        string reason;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            if (!context.PullTransportPaused)
            {
                return false;
            }

            graceDeadlineUtc = context.PullTransportGraceDeadlineUtc;
            reason = context.PullTransportPauseReason ?? "transport_disconnected";
        }

        if (graceDeadlineUtc is not null && DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_transport_grace_exhausted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}");
        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Transport disconnected.",
            notifyPeer: false,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandlePausedInboundTransportAsync(InboundTransferContext context)
    {
        DateTimeOffset? graceDeadlineUtc;
        string reason;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            if (!context.PullTransportPaused)
            {
                return false;
            }

            graceDeadlineUtc = context.PullTransportGraceDeadlineUtc;
            reason = context.PullTransportPauseReason ?? "transport_disconnected";
        }

        if (graceDeadlineUtc is not null && DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_transport_grace_exhausted; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Transport disconnected.",
            sendError: true,
            errorMessage: "Transport disconnected.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private void ReplaceOutboundDataSessionLocked(OutboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return;
        }

        if (context.DataSession is not null)
        {
            context.DataSession.AvailabilityChanged -= OnDataSessionAvailabilityChanged;
            context.DataSession.Dispose();
        }

        context.DataSession = session;
        session.AvailabilityChanged += OnDataSessionAvailabilityChanged;
    }

    private void ReplaceInboundDataSessionLocked(InboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return;
        }

        if (context.DataSession is not null)
        {
            context.DataSession.AvailabilityChanged -= OnDataSessionAvailabilityChanged;
            context.DataSession.Dispose();
        }

        context.DataSession = session;
        session.AvailabilityChanged += OnDataSessionAvailabilityChanged;
    }

    private async Task HandleInboundPullChunksAsync(InboundTransferContext context, IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        var isV3 = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3;
        if (isV3 && context.ReceiverSparseWriteActive)
        {
            await HandleInboundSparsePullChunksAsync(context, chunks).ConfigureAwait(false);
            return;
        }

        var shouldSendGrantAfterBuffering = false;
        string? failureCode = null;
        string? failureMessage = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.WriteStream is null ||
                context.Hash is null)
            {
                return;
            }

            foreach (var (chunkIndex, chunkBytes) in chunks)
            {
                context.OutstandingChunkRequests.Remove(chunkIndex);
                context.RequestedChunks.Remove(chunkIndex);
                if (chunkIndex < context.NextChunkIndex)
                {
                    continue;
                }

                if (!context.PendingChunks.ContainsKey(chunkIndex))
                {
                    context.PendingChunks[chunkIndex] = chunkBytes;
                    context.BufferedBytes += chunkBytes.Length;
                    context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                    var now = DateTimeOffset.UtcNow;
                    context.RecentPullChunkSentUtc.Enqueue(now);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, now);
                    context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                    context.PullReceiverRawBytesRecent += chunkBytes.Length;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_received; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}");
                }
            }

            UpdateReceiverBufferPressureLocked(context, DateTimeOffset.UtcNow);
            if (context.BufferedBytes >= ReceiverBufferEmergencyLimitBytes)
            {
                failureCode = ReceiverBufferExhaustedErrorCode;
                failureMessage = "Receiver buffer exceeded the emergency safety limit.";
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receiver_buffer_exhausted; transfer_id={context.TransferId}; session_id={context.SessionId}; pending_chunk_count={context.PendingChunks.Count}; pending_bytes={context.BufferedBytes}; emergency_limit_bytes={ReceiverBufferEmergencyLimitBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}");
            }

            shouldSendGrantAfterBuffering = isV3 && failureCode is null;
        }

        if (failureCode is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: failureMessage,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        while (true)
        {
            InboundWriteBatch? batch = null;
            bool completed = false;
            bool shouldLogStartupCompleted = false;
            long startupCompletedBytesReceived = 0;
            int startupCompletedNextExpectedChunk = 0;
            int startupCompletedHighestBufferedChunk = -1;
            SessionFileTransferSnapshot? snapshot = null;
            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) ||
                    context.IsTerminal ||
                    context.WriteStream is null ||
                    context.Hash is null)
                {
                    return;
                }

                batch = TryDequeueInboundWriteBatchLocked(context);

                context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
                UpdateReceiverBufferPressureLocked(context, DateTimeOffset.UtcNow);

                if (batch is not null && batch.ChunkCount > 0)
                {
                    context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                    context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
                    context.PullFirstChunkTimeoutCount = 0;
                    context.PullTimeoutOldestChunkIndex = null;
                    context.PullTimeoutStreak = 0;
                    context.PullCommittedFrontier = context.NextChunkIndex;
                    if (context.PullGapFocusActive && batch.ChunkCount >= 2)
                    {
                        context.PullGapFocusActive = false;
                        LogGapFocusChanged(context.TransferId, context.SessionId, active: false, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullLateArrivalDistance);
                    }

                    context.PullAckDebtChunks += batch.ChunkCount;
                    context.PullAckDebtBytes += batch.ByteCount;
                    if (!context.StartupPhaseCompleted && context.NextChunkIndex > 0)
                    {
                        context.StartupPhaseCompleted = true;
                        context.LastForcedWindowUpdateSentUtc = null;
                        shouldLogStartupCompleted = true;
                        startupCompletedBytesReceived = context.BytesTransferred;
                        startupCompletedNextExpectedChunk = context.NextChunkIndex;
                        startupCompletedHighestBufferedChunk = GetCurrentHighestBufferedChunkIndexLocked(context);
                    }
                }

                if (context.PullLateArrivalDistance < PullLateArrivalDistanceThreshold)
                {
                    ClearPendingInboundReorderStepDownLocked(context);
                }

                if (context.PullLateArrivalDistance >= PullLateArrivalDistanceThreshold)
                {
                    var outstandingCount = context.OutstandingChunkRequests.Count;
                    var now = DateTimeOffset.UtcNow;
                    if (!ShouldDelayHealthyReorderStepDownLocked(context, now) &&
                        TryStepDownInboundPipelineLocked(context, "late_arrival_distance", outstandingCount, out _, out _))
                    {
                        LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
                    }
                }

                if (context.PullLateArrivalDistance > 0)
                {
                    LogPullReorderPressure(
                        context.TransferId,
                        context.SessionId,
                        context.NextChunkIndex,
                        context.PullHighestReceivedChunkIndex,
                        context.PullLateArrivalDistance,
                        context.OutstandingChunkRequests.Count,
                        context.PullCurrentPipelineDepth,
                        context.ChunkSizeBytes);
                }

                completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
                snapshot = CreateSnapshotLocked();
            }

            if (batch is null || batch.ChunkCount == 0)
            {
                if (snapshot is not null)
                {
                    RaiseTransferChanged(snapshot);
                }

                if (shouldSendGrantAfterBuffering)
                {
                    await SendInboundGrantWindowV3Async(context, forceGrant: false).ConfigureAwait(false);
                }

                return;
            }

            var writeStopwatch = Stopwatch.StartNew();
            foreach (var bytes in batch.Chunks)
            {
                await context.WriteStream!.WriteAsync(bytes, context.LifetimeCts.Token).ConfigureAwait(false);
                context.Hash!.AppendData(bytes);
            }
            writeStopwatch.Stop();
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullReceiverContiguousBytesCommittedRecent += batch.ByteCount;
                    context.PullReceiverWriteBatchCountRecent++;
                    context.PullReceiverWriteBatchBytesRecent += batch.ByteCount;
                    context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
                    MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, DateTimeOffset.UtcNow);
                }
            }
            LogReceiverWriteBatchCommitted(context, batch, writeStopwatch.ElapsedMilliseconds);

            if (snapshot is not null)
            {
                RaiseTransferChanged(snapshot);
                MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
            }

            if (shouldLogStartupCompleted)
            {
                LogWindowStartupCompleted(
                    context.TransferId,
                    context.SessionId,
                    startupCompletedNextExpectedChunk,
                    startupCompletedHighestBufferedChunk,
                    startupCompletedBytesReceived);
            }

            LogPullBatchCommit(context.TransferId, context.SessionId, batch.ChunkCount, batch.NextChunkIndexAfterDequeue, batch.BytesCommittedAfterDequeue);

            if (isV3)
            {
                await SendInboundGrantWindowV3Async(context, forceGrant: completed).ConfigureAwait(false);
            }

            if (completed)
            {
                await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                return;
            }

            if (!HasContiguousInboundChunkReady(context))
            {
                return;
            }
        }
    }

    private async Task HandleInboundSparsePullChunksAsync(InboundTransferContext context, IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        var acceptedChunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(chunks.Count);
        Stream? writeStream = null;
        bool shouldSendGrantAfterBuffering = false;
        string? failureCode = null;
        string? failureMessage = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.ReceiverSparseWriteActive ||
                context.WriteStream is null ||
                context.Hash is null ||
                context.ReceiverSparseChunksWritten is null)
            {
                return;
            }

            writeStream = context.WriteStream;
            foreach (var (chunkIndex, chunkBytes) in chunks)
            {
                context.OutstandingChunkRequests.Remove(chunkIndex);
                context.RequestedChunks.Remove(chunkIndex);
                if (chunkIndex < context.NextChunkIndex)
                {
                    continue;
                }

                if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, new FileTransferChunkDataFrameV3
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        ChunkIndex = chunkIndex,
                        ChunkCount = context.ChunkCount,
                        Data = chunkBytes,
                    }, "sparse_chunk_out_of_range");
                    continue;
                }

                var expectedChunkLength = GetExpectedInboundChunkLength(context, chunkIndex);
                if (chunkBytes.Length != expectedChunkLength)
                {
                    failureCode = FileSizeMismatchErrorCode;
                    failureMessage = "Received chunk length did not match the declared file size.";
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_chunk_rejected; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=sparse_chunk_size_mismatch; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}; expected_chunk_bytes={expectedChunkLength}; chunk_count={context.ChunkCount}");
                    break;
                }

                if (IsInboundV3ChunkPresentOrPendingLocked(context, chunkIndex))
                {
                    continue;
                }

                context.ReceiverSparseChunksPendingWrite.Add(chunkIndex);
                context.BufferedBytes += chunkBytes.Length;
                context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                var now = DateTimeOffset.UtcNow;
                context.RecentPullChunkSentUtc.Enqueue(now);
                TrimRecentEvents(context.RecentPullChunkSentUtc, now);
                context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                context.PullReceiverRawBytesRecent += chunkBytes.Length;
                acceptedChunks.Add((chunkIndex, chunkBytes));
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_chunk_received; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}");
            }

            UpdateReceiverBufferPressureLocked(context, DateTimeOffset.UtcNow);
            if (failureCode is null && context.BufferedBytes >= ReceiverBufferEmergencyLimitBytes)
            {
                failureCode = ReceiverBufferExhaustedErrorCode;
                failureMessage = "Receiver buffer exceeded the emergency safety limit.";
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receiver_buffer_exhausted; transfer_id={context.TransferId}; session_id={context.SessionId}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; emergency_limit_bytes={ReceiverBufferEmergencyLimitBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}");
            }

            shouldSendGrantAfterBuffering = failureCode is null;
        }

        if (failureCode is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: failureMessage,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (acceptedChunks.Count == 0)
        {
            if (shouldSendGrantAfterBuffering)
            {
                await SendInboundGrantWindowV3Async(context, forceGrant: false).ConfigureAwait(false);
            }

            return;
        }

        var writeStopwatch = Stopwatch.StartNew();
        long sparseWriteBytes = 0;
        try
        {
            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                var offset = (long)chunkIndex * Math.Max(1, context.ChunkSizeBytes);
                writeStream!.Seek(offset, SeekOrigin.Begin);
                await writeStream.WriteAsync(chunkBytes, context.LifetimeCts.Token).ConfigureAwait(false);
                sparseWriteBytes += chunkBytes.Length;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not write a sparse receiver chunk.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }
        writeStopwatch.Stop();

        bool completed;
        bool shouldLogStartupCompleted = false;
        long startupCompletedBytesReceived = 0;
        int startupCompletedNextExpectedChunk = 0;
        int startupCompletedHighestBufferedChunk = -1;
        int committedChunkCount = 0;
        long committedByteCount = 0;
        int nextChunkIndexAfterCommit = 0;
        long bytesCommittedAfterCommit = 0;
        SessionFileTransferSnapshot? snapshot;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.WriteStream is null ||
                context.Hash is null ||
                context.ReceiverSparseChunksWritten is null)
            {
                return;
            }

            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                if (!context.ReceiverSparseChunksPendingWrite.Remove(chunkIndex))
                {
                    continue;
                }

                context.BufferedBytes = Math.Max(0, context.BufferedBytes - chunkBytes.Length);
                if (!context.ReceiverSparseChunksWritten[chunkIndex])
                {
                    context.ReceiverSparseChunksWritten[chunkIndex] = true;
                    context.ReceiverSparseBytesWritten += chunkBytes.Length;
                    context.HighestBufferedChunkIndex = Math.Max(context.HighestBufferedChunkIndex, chunkIndex);
                }
            }

            while (context.NextChunkIndex < context.ChunkCount &&
                   context.ReceiverSparseChunksWritten[context.NextChunkIndex])
            {
                var expectedChunkLength = GetExpectedInboundChunkLength(context, context.NextChunkIndex);
                context.ReceiverSparseChunksWritten[context.NextChunkIndex] = false;
                context.OutstandingChunkRequests.Remove(context.NextChunkIndex);
                context.RequestedChunks.Remove(context.NextChunkIndex);
                context.ChunkAttemptCounts.Remove(context.NextChunkIndex);
                context.NextChunkIndex++;
                context.ChunksTransferred++;
                context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + expectedChunkLength);
                committedChunkCount++;
                committedByteCount += expectedChunkLength;
            }

            context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
            UpdateReceiverBufferPressureLocked(context, DateTimeOffset.UtcNow);

            if (committedChunkCount > 0)
            {
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
                context.PullFirstChunkTimeoutCount = 0;
                context.PullTimeoutOldestChunkIndex = null;
                context.PullTimeoutStreak = 0;
                context.PullCommittedFrontier = context.NextChunkIndex;
                if (context.PullGapFocusActive && committedChunkCount >= 2)
                {
                    context.PullGapFocusActive = false;
                    LogGapFocusChanged(context.TransferId, context.SessionId, active: false, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullLateArrivalDistance);
                }

                context.PullAckDebtChunks += committedChunkCount;
                context.PullAckDebtBytes += committedByteCount;
                context.PullReceiverContiguousBytesCommittedRecent += committedByteCount;
                if (context.PullV3LastProactiveFrontierRepairStartChunkIndex >= 0 &&
                    context.NextChunkIndex > context.PullV3LastProactiveFrontierRepairStartChunkIndex &&
                    context.PullV3LastProactiveFrontierRepairSentUtc is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    var repairedStartChunkIndex = context.PullV3LastProactiveFrontierRepairStartChunkIndex;
                    var requestedChunkCount = context.PullV3LastProactiveFrontierRepairRequestedChunkCount;
                    var requestToFillMs = (long)Math.Max(0, (now - context.PullV3LastProactiveFrontierRepairSentUtc.Value).TotalMilliseconds);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_frontier_gap_repair_filled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={context.PullV3LastProactiveFrontierRepairRequestKey ?? CreateRepairRequestKey(repairedStartChunkIndex, requestedChunkCount)}; start_chunk_index={repairedStartChunkIndex}; requested_chunk_count={requestedChunkCount}; request_to_fill_ms={requestToFillMs}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; committed_chunk_count={committedChunkCount}; sparse_written_ahead_bytes={GetSparseWrittenAheadBytesLocked(context)}; same_frontier_unfilled_ms={GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now)}");
                    ResetStaleProactiveFrontierRepairStateLocked(context, now);
                }

                if (!context.StartupPhaseCompleted && context.NextChunkIndex > 0)
                {
                    context.StartupPhaseCompleted = true;
                    context.LastForcedWindowUpdateSentUtc = null;
                    shouldLogStartupCompleted = true;
                    startupCompletedBytesReceived = context.BytesTransferred;
                    startupCompletedNextExpectedChunk = context.NextChunkIndex;
                    startupCompletedHighestBufferedChunk = GetCurrentHighestBufferedChunkIndexLocked(context);
                }
            }

            context.PullReceiverWriteBatchCountRecent++;
            context.PullReceiverWriteBatchBytesRecent += sparseWriteBytes;
            context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseWriteBatchCountRecent++;
            context.PullReceiverSparseWriteBytesRecent += sparseWriteBytes;
            context.PullReceiverSparseWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseChunksWrittenRecent += acceptedChunks.Count;
            context.PullReceiverSparseContiguousChunksCommittedRecent += committedChunkCount;

            if (context.PullLateArrivalDistance < PullLateArrivalDistanceThreshold)
            {
                ClearPendingInboundReorderStepDownLocked(context);
            }

            if (context.PullLateArrivalDistance >= PullLateArrivalDistanceThreshold)
            {
                var outstandingCount = context.OutstandingChunkRequests.Count;
                var now = DateTimeOffset.UtcNow;
                if (!ShouldDelayHealthyReorderStepDownLocked(context, now) &&
                    TryStepDownInboundPipelineLocked(context, "late_arrival_distance", outstandingCount, out _, out _))
                {
                    LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
                }
            }

            if (context.PullLateArrivalDistance > 0)
            {
                LogPullReorderPressure(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex,
                    context.PullLateArrivalDistance,
                    context.OutstandingChunkRequests.Count,
                    context.PullCurrentPipelineDepth,
                    context.ChunkSizeBytes);
            }

            nextChunkIndexAfterCommit = context.NextChunkIndex;
            bytesCommittedAfterCommit = context.BytesTransferred;
            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            if (completed)
            {
                context.LastPullControlChatterLogUtc = null;
            }

            MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, DateTimeOffset.UtcNow);
            snapshot = CreateSnapshotLocked();
        }

        if (committedChunkCount > 0)
        {
            LogPullBatchCommit(context.TransferId, context.SessionId, committedChunkCount, nextChunkIndexAfterCommit, bytesCommittedAfterCommit);
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (shouldLogStartupCompleted)
        {
            LogWindowStartupCompleted(
                context.TransferId,
                context.SessionId,
                startupCompletedNextExpectedChunk,
                startupCompletedHighestBufferedChunk,
                startupCompletedBytesReceived);
        }

        if (shouldSendGrantAfterBuffering)
        {
            await SendInboundGrantWindowV3Async(context, forceGrant: completed).ConfigureAwait(false);
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
        }
    }

    private sealed record InboundWriteBatch(
        IReadOnlyList<byte[]> Chunks,
        int ChunkCount,
        long ByteCount,
        int NextChunkIndexAfterDequeue,
        long BytesCommittedAfterDequeue,
        int PendingChunkCountAfterDequeue,
        long PendingBytesAfterDequeue,
        int HighestReceivedChunkIndex,
        int LateArrivalDistance,
        long GrantedWindowBytes);

    private bool HasContiguousInboundChunkReady(InboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(inboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.PendingChunks.ContainsKey(context.NextChunkIndex);
        }
    }

    private static InboundWriteBatch? TryDequeueInboundWriteBatchLocked(InboundTransferContext context)
    {
        if (!context.PendingChunks.TryGetValue(context.NextChunkIndex, out _))
        {
            return null;
        }

        List<byte[]> chunks = [];
        long byteCount = 0;
        while (chunks.Count < ReceiverWriteBatchMaxChunks &&
               context.PendingChunks.TryGetValue(context.NextChunkIndex, out var contiguous))
        {
            if (chunks.Count > 0 && byteCount + contiguous.Length > ReceiverWriteBatchMaxBytes)
            {
                break;
            }

            if (!context.PendingChunks.Remove(context.NextChunkIndex))
            {
                break;
            }

            context.BufferedBytes = Math.Max(0, context.BufferedBytes - contiguous.Length);
            chunks.Add(contiguous);
            byteCount += contiguous.Length;
            context.OutstandingChunkRequests.Remove(context.NextChunkIndex);
            context.RequestedChunks.Remove(context.NextChunkIndex);
            context.ChunkAttemptCounts.Remove(context.NextChunkIndex);
            context.NextChunkIndex++;
            context.ChunksTransferred++;
            context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + contiguous.Length);
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        var lateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
        return new InboundWriteBatch(
            chunks,
            chunks.Count,
            byteCount,
            context.NextChunkIndex,
            context.BytesTransferred,
            context.PendingChunks.Count,
            context.BufferedBytes,
            context.PullHighestReceivedChunkIndex,
            lateArrivalDistance,
            GetInboundV3GrantedWindowBytesLocked(context));
    }

    private static void UpdateReceiverBufferPressureLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (!context.ReceiverBufferPressureActive && context.BufferedBytes >= ReceiverBufferSoftLimitBytes)
        {
            context.ReceiverBufferPressureActive = true;
            context.ReceiverBufferPressureSinceUtc = now;
            var reason = context.BufferedBytes >= ReceiverBufferSevereLimitBytes ? "severe_limit" : "soft_limit";
            LogReceiverBufferPressureEntered(context, reason);
            return;
        }

        if (context.ReceiverBufferPressureActive && context.BufferedBytes <= ReceiverBufferExitLimitBytes)
        {
            var durationMs = context.ReceiverBufferPressureSinceUtc is null
                ? 0L
                : (long)Math.Max(0, (now - context.ReceiverBufferPressureSinceUtc.Value).TotalMilliseconds);
            context.ReceiverBufferPressureActive = false;
            context.ReceiverBufferPressureSinceUtc = null;
            LogReceiverBufferPressureExited(context, durationMs);
        }
    }

    private void TryRecoverInboundPullSessionLocked(InboundTransferContext context)
    {
        var maximumDepth = ResolveInboundMaximumPipelineDepthLocked(context);
        if (sessionScreenShareDegraded ||
            context.PullCurrentPipelineDepth >= maximumDepth ||
            context.PullLateArrivalDistance > 1 ||
            context.PullGapFocusActive ||
            context.PullTimeoutStreak > 0 ||
            context.PullHighestReceivedChunkIndex - context.NextChunkIndex >= 3)
        {
            context.PullRecoverySinceUtc = null;
            return;
        }

        context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - context.PullRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PullSessionRecoveryHoldMs))
        {
            return;
        }

        var changed = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: true, out var previousDepth, out var updatedPipelineDepth);
        if (changed)
        {
            LogPullPipelineRecoveryStep(context.TransferId, context.SessionId, previousDepth, updatedPipelineDepth, degraded: context.PullSessionDegraded || sessionScreenShareDegraded);
            LogPullProfileStepUp(context.TransferId, context.SessionId, previousDepth, updatedPipelineDepth, context.ChunkSizeBytes, context.PullSessionDegraded || sessionScreenShareDegraded);
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, degraded: context.PullSessionDegraded || sessionScreenShareDegraded);
        }

        if (context.PullSessionDegraded && !sessionScreenShareActive && updatedPipelineDepth >= PullDegradedPipelineDepth)
        {
            context.PullSessionDegraded = false;
            context.PullDegradedSinceUtc = null;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_degraded_exited; transfer_id={context.TransferId}; session_id={context.SessionId}");
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, degraded: false);
        }
    }

    private static void TrimOutboundPullSentChunkCache(OutboundTransferContext context, int nextExpectedChunkIndex)
    {
        if (context.PullSentChunkCache.Count == 0)
        {
            return;
        }

        foreach (var obsoleteChunkIndex in context.PullSentChunkCache.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            if (context.PullSentChunkCache.Remove(obsoleteChunkIndex, out var removedBytes))
            {
                context.PullSentChunkCacheBytes -= removedBytes.Length;
            }
        }

        if (context.PullSentChunkCacheBytes < 0)
        {
            context.PullSentChunkCacheBytes = 0;
        }
    }

    private static void LogPullDataFrameReceived(string transferId, string sessionId, FileTransferDataFrameV2 frame)
    {
        LogPullBinaryFrameReceived(transferId, sessionId, frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}");
    }

    private static void LogPullDataFrameIgnored(string transferId, string sessionId, FileTransferDataFrameV2 frame, string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; reason={reason}");
    }

    private static string GetFrameChunkIndex(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.ChunkIndex.ToString(),
            FileTransferChunkBatchFrameV2 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            FileTransferRepairRequestSetFrameV3 repairSet when repairSet.Ranges.Count > 0 => string.Join(",", repairSet.Ranges.Select(static range => $"{range.StartChunkIndex}-{range.StartChunkIndex + range.RequestedChunkCount - 1}")),
            _ => "(none)",
        };

    private static void LogPullBinaryFrameSent(string transferId, string sessionId, FileTransferDataFrameV2 frame, int payloadBytes)
    {
        var serializedPayloadBytes = FileTransferDataFrameCodec.Serialize(frame).Length;
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        var batchChunkCount = frame is FileTransferChunkDataFrameV2 or FileTransferChunkBatchFrameV2
            ? GetFrameChunkCount(frame)
            : 0;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; payload_bytes={serializedPayloadBytes}; serialized_payload_bytes={serializedPayloadBytes}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}; batch_chunk_count={batchChunkCount}");
    }

    private static void LogPullBinaryFrameReceived(string transferId, string sessionId, FileTransferDataFrameV2 frame)
    {
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

}
