using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundPullSendLoopAsync(OutboundTransferContext context)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            var initialPipelineDepth = ResolveOutboundInitialPipelineDepth();
            var startMessage = new FileTransferStartV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                Sha256Base64 = context.Sha256Base64!,
                ChunkCount = context.ChunkCount,
                ChunkSizeBytes = context.ChunkSizeBytes,
            };
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV2,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = context.ChunkSizeBytes,
                InitialPipelineDepth = initialPipelineDepth,
            };

            var dataSession = await currentTransport
                .OpenFileTransferDataSessionAsync(context.SessionId, context.TransferId, context.LifetimeCts.Token)
                .ConfigureAwait(false);

            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    dataSession.Dispose();
                    return;
                }

                ReplaceOutboundDataSessionLocked(context, dataSession);
                context.PullSessionActive = true;
                context.PullCurrentPipelineDepth = initialPipelineDepth;
                context.RequestedButUnsent.Clear();
                context.GrantedOutstandingChunks.Clear();
                context.PullSentChunkCache.Clear();
            }

            UpdateOutboundState(context, FileTransferTransferState.AwaitingStart, 0, 0, "Starting file transfer.");
            await currentTransport.SendFileTransferStartAsync(startMessage, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "start_sent",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                reason: $"chunk_count={context.ChunkCount}; chunk_size_bytes={context.ChunkSizeBytes}");

            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                reason: $"role={sessionOpen.SessionRole}; chunk_size_bytes={sessionOpen.ChunkSizeBytes}; pipeline_depth={sessionOpen.InitialPipelineDepth}");
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Outbound, initialPipelineDepth, degraded: sessionScreenShareDegraded);

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);

            await dataSession.SendAsync(
                    new FileTransferManifestFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        FileName = context.FileName,
                        FileSizeBytes = context.FileSizeBytes,
                        ChunkSizeBytes = context.ChunkSizeBytes,
                        ChunkCount = context.ChunkCount,
                        Sha256Base64 = context.Sha256Base64!,
                    },
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);
            LogPullBinaryFrameSent(
                context.TransferId,
                context.SessionId,
                new FileTransferManifestFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    FileName = context.FileName,
                    FileSizeBytes = context.FileSizeBytes,
                    ChunkSizeBytes = context.ChunkSizeBytes,
                    ChunkCount = context.ChunkCount,
                    Sha256Base64 = context.Sha256Base64!,
                },
                payloadBytes: 0);

            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for receiver requests.");

            Task<FileTransferDataFrameV2>? pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferRequestChunksFrameV2 request:
                        await SendRequestedChunksAsync(context, stream, dataSession, request).ConfigureAwait(false);
                        break;
                    case FileTransferAckProgressFrameV2 ack:
                        ApplyOutboundAckProgress(context, ack);
                        await SendPendingRequestedChunksAsync(context, stream, dataSession).ConfigureAwait(false);
                        break;
                    case FileTransferCancelFrameV2 cancel:
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Canceled,
                            errorCode: CanceledReason,
                            statusMessage: cancel.Reason ?? "Transfer canceled by receiver.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    case FileTransferCompleteFrameV2:
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Completed,
                            errorCode: null,
                            statusMessage: "Transfer complete.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode),
                statusMessage: ex.Message,
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunInboundPullReceiveLoopAsync(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
    {
        try
        {
            var dataSession = context.DataSession ?? await GetTransportOrThrow()
                .OpenFileTransferDataSessionAsync(sessionOpen.SessionId, sessionOpen.TransferId, context.LifetimeCts.Token)
                .ConfigureAwait(false);

            if (!ReferenceEquals(context.DataSession, dataSession))
            {
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                    {
                        ReplaceInboundDataSessionLocked(context, dataSession);
                    }
                }
            }

            FileTransferManifestFrameV2? manifest = null;
            Task<FileTransferDataFrameV2>? pendingReceiveTask = null;
            while (manifest is null)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (frame is FileTransferManifestFrameV2 receivedManifest)
                {
                    manifest = receivedManifest;
                }
                else if (frame is FileTransferCancelFrameV2 cancel)
                {
                    await TransitionInboundToTerminalAsync(
                        context,
                        FileTransferTransferState.Canceled,
                        errorCode: CanceledReason,
                        statusMessage: cancel.Reason ?? "Transfer canceled by sender.",
                        sendError: false,
                        errorMessage: null,
                        cancelReason: null,
                        ct: CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                else
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "waiting_for_manifest");
                }
            }

            await InitializeInboundPullManifestAsync(context, manifest).ConfigureAwait(false);
            await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: false).ConfigureAwait(false);

            pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }
                else if (pendingReceiveTask.IsCompleted)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_receive_loop_overlap_detected; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=completed_receive_task_reused");
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    await MaybeHandlePullRequestTimeoutAsync(context).ConfigureAwait(false);
                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferChunkDataFrameV2 chunk:
                        await HandleInboundPullChunkAsync(context, chunk).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV2 batch:
                        await HandleInboundPullChunkBatchAsync(context, batch).ConfigureAwait(false);
                        break;
                    case FileTransferCancelFrameV2 cancel:
                        await TransitionInboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Canceled,
                            errorCode: CanceledReason,
                            statusMessage: cancel.Reason ?? "Transfer canceled by sender.",
                            sendError: false,
                            errorMessage: null,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_inbound_frame");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: ex.Message,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private int ResolveOutboundInitialPipelineDepth(OutboundTransferContext? context = null)
        => ResolveOutboundPipelineDepth(context);

    private int ResolveOutboundPipelineDepth(OutboundTransferContext? context = null)
    {
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

    private static void TrimRecentEvents(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek() > TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            events.Dequeue();
        }
    }

    private void MaybeLogPullControlChatterWindow(InboundTransferContext context, string transferId, string sessionId, DateTimeOffset now)
    {
        TrimRecentEvents(context.RecentPullAckSentUtc, now);
        TrimRecentEvents(context.RecentPullRequestSentUtc, now);
        TrimRecentEvents(context.RecentPullChunkSentUtc, now);
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
            var usefulPayloadBytesPerSecond = (long)Math.Round(context.PullUsefulPayloadBytesRecent / (PullControlChatterWindowMs / 1000D));
            var controlFramesPerMiB = context.PullUsefulPayloadBytesRecent <= 0
                ? 0D
                : controlFrameCount / (context.PullUsefulPayloadBytesRecent / 1048576D);
            var grantedWindowBytes = Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * Math.Max(1, context.ChunkSizeBytes);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v3_throughput_summary; transfer_id={transferId}; session_id={sessionId}; useful_payload_bytes_per_second={usefulPayloadBytesPerSecond}; control_frames_per_mib={controlFramesPerMiB:F2}; granted_window_bytes={grantedWindowBytes}; chunk_size_bytes={context.ChunkSizeBytes}; profile={ResolveInboundV3ProfileName(context)}");
        }
        context.PullDuplicateRequestIgnoredCountRecent = 0;
        context.PullResendSuppressedCountRecent = 0;
        context.PullUsefulPayloadBytesRecent = 0;
    }

    private async Task SendRequestedChunksAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        FileTransferRequestChunksFrameV2 request)
    {
        List<int> chunkIndicesToSend;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                if (request.RequestedChunkCount == 1)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_retry_abandoned_terminal; transfer_id={request.TransferId}; session_id={request.SessionId}; chunk_index={request.StartChunkIndex}");
                }
                return;
            }

            context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
            var startChunkIndex = Math.Max(0, request.StartChunkIndex);
            var maxChunkIndexExclusive = Math.Min(context.ChunkCount, startChunkIndex + Math.Max(1, request.RequestedChunkCount));
            for (var chunkIndex = startChunkIndex; chunkIndex < maxChunkIndexExclusive; chunkIndex++)
            {
                var isExplicitRetryRequest =
                    request.RequestedChunkCount == 1 &&
                    context.GrantedOutstandingChunks.Contains(chunkIndex) &&
                    context.LastChunkSentUtc.ContainsKey(chunkIndex);

                if (chunkIndex < context.ChunksTransferred)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_request_ignored_obsolete; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; next_expected_chunk={context.ChunksTransferred}");
                    continue;
                }

                if (isExplicitRetryRequest)
                {
                    var resendGateMs = GetPullSessionRetryResendGateMsForOutbound(context);
                    var lastSentUtc = context.LastChunkSentUtc[chunkIndex];
                    var millisecondsSinceLastSend = Math.Max(0, (int)(now - lastSentUtc).TotalMilliseconds);
                    var resendCountSinceAck = context.ChunkResendCountSinceAck.TryGetValue(chunkIndex, out var resendCount)
                        ? resendCount
                        : 0;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_retry_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; milliseconds_since_last_send={millisecondsSinceLastSend}; resend_gate_ms={resendGateMs}; resend_count_since_ack={resendCountSinceAck}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");

                    if (now - lastSentUtc < TimeSpan.FromMilliseconds(resendGateMs))
                    {
                        context.PullResendSuppressedCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_retry_gate_blocked; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; milliseconds_since_last_send={millisecondsSinceLastSend}; resend_gate_ms={resendGateMs}; resend_count_since_ack={resendCountSinceAck}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");
                        continue;
                    }

                    context.RequestedButUnsent.Add(chunkIndex);
                    context.GrantedOutstandingChunks.Add(chunkIndex);
                    continue;
                }

                if (context.GrantedOutstandingChunks.Contains(chunkIndex))
                {
                    if (context.SentAwaitingAck.TryGetValue(chunkIndex, out var sentAtUtc) &&
                        now - sentAtUtc < TimeSpan.FromMilliseconds(GetPullSessionRequestTimeoutMsForOutbound(context)))
                    {
                        context.PullDuplicateRequestIgnoredCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_request_duplicate_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}");
                        continue;
                    }

                    if (context.RequestedButUnsent.Contains(chunkIndex))
                    {
                        context.PullResendSuppressedCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_resend_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}");
                        continue;
                    }
                }

                context.RequestedButUnsent.Add(chunkIndex);
                context.GrantedOutstandingChunks.Add(chunkIndex);
            }
            chunkIndicesToSend = GetSendableRequestedChunksLocked(context);
        }

        if (chunkIndicesToSend.Count == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_grant_drain; transfer_id={context.TransferId}; session_id={context.SessionId}; sendable_chunk_count={chunkIndicesToSend.Count}; first_chunk_index={chunkIndicesToSend[0]}; last_chunk_index={chunkIndicesToSend[^1]}; sender_sendability_source=grant_only");

        await SendQueuedChunkIndicesAsync(context, stream, dataSession, chunkIndicesToSend).ConfigureAwait(false);
    }

    private List<int> GetSendableRequestedChunksLocked(OutboundTransferContext context)
        => context.RequestedButUnsent.ToList();

    private async Task SendPendingRequestedChunksAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession)
    {
        List<int> chunkIndicesToSend;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
            chunkIndicesToSend = GetSendableRequestedChunksLocked(context);
        }

        if (chunkIndicesToSend.Count == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_grant_drain_after_ack; transfer_id={context.TransferId}; session_id={context.SessionId}; sendable_chunk_count={chunkIndicesToSend.Count}; first_chunk_index={chunkIndicesToSend[0]}; last_chunk_index={chunkIndicesToSend[^1]}; sender_sendability_source=grant_only");

        await SendQueuedChunkIndicesAsync(context, stream, dataSession, chunkIndicesToSend).ConfigureAwait(false);
    }

    private async Task SendQueuedChunkIndicesAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        List<int> chunkIndicesToSend)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            for (var chunkListIndex = 0; chunkListIndex < chunkIndicesToSend.Count; chunkListIndex++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                var chunkIndex = chunkIndicesToSend[chunkListIndex];
                if (TryBuildBundledChunkFrame(
                        context,
                        stream,
                        buffer,
                        chunkIndicesToSend,
                        chunkListIndex,
                        out var bundledFrame,
                        out var bundledChunkIndexes))
                {
                    await dataSession.SendAsync(bundledFrame, context.LifetimeCts.Token).ConfigureAwait(false);
                    LogPullBinaryFrameSent(
                        context.TransferId,
                        context.SessionId,
                        bundledFrame,
                        bundledFrame.DataSegments.Sum(static segment => segment.Length));

                    foreach (var bundledChunkIndex in bundledChunkIndexes)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={bundledChunkIndex}; chunk_bytes={bundledFrame.DataSegments[bundledChunkIndex - bundledFrame.StartChunkIndex].Length}");
                    }

                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        var sentUtc = DateTimeOffset.UtcNow;
                        foreach (var bundledChunkIndex in bundledChunkIndexes)
                        {
                            context.RequestedButUnsent.Remove(bundledChunkIndex);
                            context.SentAwaitingAck[bundledChunkIndex] = sentUtc;
                            context.LastChunkSentUtc[bundledChunkIndex] = sentUtc;
                            context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, bundledChunkIndex + 1);
                        }

                        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                            ? context.FileSizeBytes
                            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                        context.StatusMessage = "Streaming requested chunks.";
                        foreach (var _ in bundledChunkIndexes)
                        {
                            context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                        }
                        TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                        context.PullUsefulPayloadBytesRecent += bundledFrame.DataSegments.Sum(static segment => segment.Length);
                    }

                    chunkListIndex += bundledChunkIndexes.Count - 1;
                    continue;
                }

                byte[]? cachedChunkBytes = null;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    context.PullSentChunkCache.TryGetValue(chunkIndex, out cachedChunkBytes);
                }

                byte[] chunkBytes;
                if (cachedChunkBytes is null)
                {
                    var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
                    if (stream.CanSeek && stream.Position != fileOffset)
                    {
                        stream.Seek(fileOffset, SeekOrigin.Begin);
                    }

                    var remaining = context.FileSizeBytes - fileOffset;
                    var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, targetReadSize), context.LifetimeCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new InvalidOperationException("Source stream did not match the declared file size.");
                    }

                    chunkBytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);

                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        context.PullSentChunkCache[chunkIndex] = chunkBytes;
                    }
                }
                else
                {
                    chunkBytes = cachedChunkBytes;
                }

                var frameToSend = CreatePullChunkDataFrame(
                    context.NegotiatedDataProtocolVersion,
                    context.SessionId,
                    context.TransferId,
                    chunkIndex,
                    context.ChunkCount,
                    chunkBytes);

                await dataSession.SendAsync(
                        frameToSend,
                        context.LifetimeCts.Token)
                    .ConfigureAwait(false);
                LogPullBinaryFrameSent(context.TransferId, context.SessionId, frameToSend, frameToSend.Data.Length);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_chunk_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}");

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    context.RequestedButUnsent.Remove(chunkIndex);
                    var sentUtc = DateTimeOffset.UtcNow;
                    var isRetrySend = context.LastChunkSentUtc.ContainsKey(chunkIndex);
                    context.SentAwaitingAck[chunkIndex] = sentUtc;
                    context.LastChunkSentUtc[chunkIndex] = sentUtc;
                    if (isRetrySend)
                    {
                        context.LastChunkResentUtc[chunkIndex] = sentUtc;
                        context.ChunkResendCountSinceAck[chunkIndex] = context.ChunkResendCountSinceAck.TryGetValue(chunkIndex, out var resendCount)
                            ? resendCount + 1
                            : 1;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_retry_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; resend_count_since_ack={context.ChunkResendCountSinceAck[chunkIndex]}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");
                    }
                    context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, chunkIndex + 1);
                    context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                    context.StatusMessage = "Streaming requested chunks.";
                    context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                    context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                }
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryBuildBundledChunkFrame(
        OutboundTransferContext context,
        Stream stream,
        byte[] buffer,
        IReadOnlyList<int> chunkIndicesToSend,
        int chunkListIndex,
        out FileTransferChunkBatchFrameV2 bundledFrame,
        out List<int> bundledChunkIndexes)
    {
        bundledFrame = null!;
        bundledChunkIndexes = [];
        var bundledChunkFrameCount = ResolveHealthyBundledChunkFrameCount(context.ChunkSizeBytes);

        if (!CanBundleOutboundChunkFrames(context) ||
            bundledChunkFrameCount <= 1 ||
            chunkListIndex + bundledChunkFrameCount - 1 >= chunkIndicesToSend.Count)
        {
            return false;
        }

        var firstChunkIndex = chunkIndicesToSend[chunkListIndex];
        lock (gate)
        {
            for (var segmentOffset = 0; segmentOffset < bundledChunkFrameCount; segmentOffset++)
            {
                var chunkIndex = chunkIndicesToSend[chunkListIndex + segmentOffset];
                if (chunkIndex != firstChunkIndex + segmentOffset ||
                    context.LastChunkSentUtc.ContainsKey(chunkIndex))
                {
                    return false;
                }
            }
        }

        var segments = new byte[bundledChunkFrameCount][];
        var totalBytes = 0;
        for (var segmentOffset = 0; segmentOffset < bundledChunkFrameCount; segmentOffset++)
        {
            var chunkIndex = chunkIndicesToSend[chunkListIndex + segmentOffset];
            var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
            if (stream.CanSeek && stream.Position != fileOffset)
            {
                stream.Seek(fileOffset, SeekOrigin.Begin);
            }

            var remaining = context.FileSizeBytes - fileOffset;
            var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
            var read = stream.Read(buffer, 0, targetReadSize);
            if (read <= 0)
            {
                throw new InvalidOperationException("Source stream did not match the declared file size.");
            }

            var chunkBytes = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);
            totalBytes += read;
            if (totalBytes > PullHealthyBundledRawBytesCap)
            {
                return false;
            }

            segments[segmentOffset] = chunkBytes;
        }

        var candidateFrame = new FileTransferChunkBatchFrameV2
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = firstChunkIndex,
            ChunkCount = context.ChunkCount,
            DataSegments = segments,
        };

        _ = FileTransferDataFrameCodec.Serialize(candidateFrame);
        bundledFrame = candidateFrame;
        bundledChunkIndexes = Enumerable.Range(firstChunkIndex, bundledChunkFrameCount).ToList();
        return true;
    }

    private bool CanBundleOutboundChunkFrames(OutboundTransferContext context)
        => !sessionScreenShareActive &&
           !sessionScreenShareDegraded &&
           !context.PullSessionDegraded &&
           context.ChunkSizeBytes == PullHealthyDefaultChunkSizeBytes;

    private void ApplyOutboundAckProgress(OutboundTransferContext context, FileTransferAckProgressFrameV2 ack)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.ChunksTransferred = Math.Max(context.ChunksTransferred, Math.Min(ack.NextExpectedChunkIndex, context.ChunkCount));
            context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(ack.BytesCommitted, context.FileSizeBytes));
            context.RequestedButUnsent.RemoveWhere(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex);
            context.GrantedOutstandingChunks.RemoveWhere(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex);
            foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.SentAwaitingAck.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.LastChunkSentUtc.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.LastChunkSentUtc.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.LastChunkResentUtc.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.LastChunkResentUtc.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.ChunkResendCountSinceAck.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.ChunkResendCountSinceAck.Remove(chunkIndex);
            }
            TrimOutboundPullSentChunkCache(context, ack.NextExpectedChunkIndex);
            context.StatusMessage = context.ChunksTransferred >= context.ChunkCount
                ? "Waiting for receiver verification."
                : "Receiver is acknowledging requested chunks.";
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private async Task InitializeInboundPullManifestAsync(InboundTransferContext context, FileTransferManifestFrameV2 manifest)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (!string.Equals(context.FileName, manifest.FileName, StringComparison.Ordinal) ||
                context.FileSizeBytes != manifest.FileSizeBytes ||
                !string.Equals(context.Sha256Base64, manifest.Sha256Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Manifest metadata did not match the original offer.");
            }

            context.ChunkCount = manifest.ChunkCount;
            context.ChunkSizeBytes = manifest.ChunkSizeBytes;
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

        LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
    }

    private async Task<bool> MaybeSendNextChunkRequestAsync(InboundTransferContext context, bool forceResendOldestOutstanding)
    {
        FileTransferRequestChunksFrameV2? request = null;
        int? blockedOldestOutstandingChunk = null;
        int blockedRequestedUntilExclusive = 0;
        int batchExtensionCount = 0;
        bool retryingOldestOutstanding = false;
        int retryAttemptCount = 0;
        int previousPipelineDepth = 0;
        int updatedPipelineDepth = 0;
        bool pipelineDepthChanged = false;
        DateTimeOffset requestSentUtc = default;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived ||
                context.ChunkCount <= 0)
            {
                return false;
            }

            pipelineDepthChanged = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: true, out previousPipelineDepth, out updatedPipelineDepth);
            var outstandingCount = context.OutstandingChunkRequests.Count;
            if (forceResendOldestOutstanding && context.OutstandingChunkRequests.Count > 0)
            {
                var oldest = context.OutstandingChunkRequests.Keys.Min();
                request = new FileTransferRequestChunksFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    StartChunkIndex = oldest,
                    RequestedChunkCount = 1,
                    PipelineDepth = context.PullCurrentPipelineDepth,
                };
                context.OutstandingChunkRequests[oldest] = DateTimeOffset.UtcNow;
                context.RequestedChunks.Add(oldest);
                context.ChunkAttemptCounts[oldest] = context.ChunkAttemptCounts.TryGetValue(oldest, out var attempts)
                    ? attempts + 1
                    : 1;
                context.PullLastRequestSentUtc = DateTimeOffset.UtcNow;
                retryingOldestOutstanding = true;
                retryAttemptCount = context.ChunkAttemptCounts[oldest];
            }
            else
            {
                if (outstandingCount > 0)
                {
                    var oldestOutstanding = context.OutstandingChunkRequests.Keys.Min();
                    var lowWatermark = ResolveInboundRequestLowWatermarkLocked(context);
                    var desiredOutstanding = context.PullCurrentPipelineDepth;
                    var canExtendBatch =
                        !context.PullGapFocusActive &&
                        !sessionScreenShareDegraded &&
                        oldestOutstanding == context.NextChunkIndex &&
                        context.PendingChunks.Count == 0 &&
                        outstandingCount <= lowWatermark &&
                        context.PullRequestedFrontierExclusive < context.ChunkCount;

                    if (canExtendBatch)
                    {
                        var missingTailCount = desiredOutstanding - outstandingCount;
                        var requestCount = Math.Min(missingTailCount, context.ChunkCount - context.PullRequestedFrontierExclusive);
                        if (requestCount > 0)
                        {
                            var startChunkIndex = context.PullRequestedFrontierExclusive;
                            request = new FileTransferRequestChunksFrameV2
                            {
                                SessionId = context.SessionId,
                                TransferId = context.TransferId,
                                StartChunkIndex = startChunkIndex,
                                RequestedChunkCount = requestCount,
                                PipelineDepth = context.PullCurrentPipelineDepth,
                            };
                            requestSentUtc = DateTimeOffset.UtcNow;
                            for (var chunkIndex = startChunkIndex; chunkIndex < startChunkIndex + requestCount; chunkIndex++)
                            {
                                context.OutstandingChunkRequests[chunkIndex] = requestSentUtc;
                                context.RequestedChunks.Add(chunkIndex);
                                context.ChunkAttemptCounts[chunkIndex] = 1;
                            }

                            context.PullRequestedFrontierExclusive = startChunkIndex + requestCount;
                            context.PullLastRequestSentUtc = requestSentUtc;
                            batchExtensionCount = requestCount;
                        }
                    }

                    if (request is null)
                    {
                        blockedOldestOutstandingChunk = oldestOutstanding;
                        blockedRequestedUntilExclusive = context.PullRequestedFrontierExclusive;
                    }
                }
                else
                {
                    var desiredOutstanding = context.PullCurrentPipelineDepth;
                    var requestCount = desiredOutstanding;
                    if (requestCount <= 0)
                    {
                        return false;
                    }

                    var startChunkIndex = context.NextChunkIndex;
                    if (startChunkIndex >= context.ChunkCount)
                    {
                        return false;
                    }

                    requestCount = Math.Min(requestCount, context.ChunkCount - startChunkIndex);
                    if (requestCount <= 0)
                    {
                        return false;
                    }

                    request = new FileTransferRequestChunksFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        StartChunkIndex = startChunkIndex,
                        RequestedChunkCount = requestCount,
                        PipelineDepth = context.PullCurrentPipelineDepth,
                    };
                    requestSentUtc = DateTimeOffset.UtcNow;
                    for (var chunkIndex = startChunkIndex; chunkIndex < startChunkIndex + requestCount; chunkIndex++)
                    {
                        context.OutstandingChunkRequests[chunkIndex] = requestSentUtc;
                        context.RequestedChunks.Add(chunkIndex);
                        context.ChunkAttemptCounts[chunkIndex] = 1;
                    }

                    context.PullRequestedFrontierExclusive = startChunkIndex + requestCount;
                    context.PullLastRequestSentUtc = requestSentUtc;
                }
            }
        }

        if (blockedOldestOutstandingChunk is not null)
        {
            if (blockedOldestOutstandingChunk.Value == context.NextChunkIndex)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_request_window_blocked_by_oldest_gap; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; oldest_outstanding_chunk={blockedOldestOutstandingChunk.Value}; requested_until_exclusive={blockedRequestedUntilExclusive}");
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_refill_skipped_above_low_watermark; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; oldest_outstanding_chunk={blockedOldestOutstandingChunk.Value}; requested_until_exclusive={blockedRequestedUntilExclusive}");
            }
            return false;
        }

        if (pipelineDepthChanged)
        {
            if (updatedPipelineDepth > previousPipelineDepth)
            {
                LogPullProfileStepUp(context.TransferId, context.SessionId, previousPipelineDepth, updatedPipelineDepth, context.ChunkSizeBytes, context.PullSessionDegraded || sessionScreenShareDegraded);
            }
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
        }

        if (request is null || context.DataSession is null)
        {
            return false;
        }

        await context.DataSession.SendAsync(request, context.LifetimeCts.Token).ConfigureAwait(false);
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, request, payloadBytes: 0);
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.RecentPullRequestSentUtc.Enqueue(requestSentUtc == default ? DateTimeOffset.UtcNow : requestSentUtc);
                MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, DateTimeOffset.UtcNow);
            }
        }

        if (retryingOldestOutstanding)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_retry_oldest_chunk; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={request.StartChunkIndex}; attempt_count={retryAttemptCount}; pipeline_depth={request.PipelineDepth}");
        }
        else if (batchExtensionCount > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_grant_window_refilled; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_batch_extended; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_refill; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        }
        else
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_grant_window_opened; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_request_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        return true;
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

    private async Task MaybeHandlePullRequestTimeoutAsync(InboundTransferContext context)
    {
        if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
        {
            return;
        }

        bool shouldResend = false;
        bool degradedChanged = false;
        bool failStalledFirstChunk = false;
        bool pipelineDepthChanged = false;
        bool gapFocusChanged = false;
        int oldestOutstandingChunkIndex = -1;
        int timeoutStreak = 0;
        int outstandingCount = 0;
        int pipelineDepth = 0;
        int highestReceivedChunkIndex = -1;
        int lateArrivalDistance = 0;
        bool screenshareDegraded = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.OutstandingChunkRequests.Count == 0)
            {
                return;
            }

            oldestOutstandingChunkIndex = context.OutstandingChunkRequests.Keys.Min();
            var oldest = context.OutstandingChunkRequests[oldestOutstandingChunkIndex];
            var timeoutMs = GetPullSessionRequestTimeoutMs(context);
            if (DateTimeOffset.UtcNow - oldest < TimeSpan.FromMilliseconds(timeoutMs))
            {
                TryRecoverInboundPullSessionLocked(context);
                return;
            }

            shouldResend = true;
            if (context.PullTimeoutOldestChunkIndex == oldestOutstandingChunkIndex)
            {
                context.PullTimeoutStreak++;
            }
            else
            {
                context.PullTimeoutOldestChunkIndex = oldestOutstandingChunkIndex;
                context.PullTimeoutStreak = 1;
            }

            timeoutStreak = context.PullTimeoutStreak;
            outstandingCount = context.OutstandingChunkRequests.Count;
            pipelineDepth = context.PullCurrentPipelineDepth;
            screenshareDegraded = sessionScreenShareDegraded;
            highestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            lateArrivalDistance = context.PullLateArrivalDistance;

            if (outstandingCount > PullTimeoutOutstandingStepDownThreshold)
            {
                pipelineDepthChanged = TryStepDownInboundPipelineLocked(context, "oldest_chunk_timeout", outstandingCount, out _, out pipelineDepth);
            }

            var shouldEnterDegraded = sessionScreenShareDegraded || context.PullTimeoutStreak >= PullSessionDegradedEntryTimeoutStreakThreshold;
            degradedChanged = shouldEnterDegraded && !context.PullSessionDegraded;
            if (shouldEnterDegraded)
            {
                context.PullSessionDegraded = true;
                context.PullDegradedSinceUtc ??= DateTimeOffset.UtcNow;
            }

            pipelineDepthChanged = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: false, out _, out pipelineDepth) || pipelineDepthChanged;

            context.PullRecoverySinceUtc = null;
            if (!context.PullGapFocusActive &&
                (context.PullLateArrivalDistance >= PullGapFocusBufferedThreshold ||
                 context.PendingChunks.Count >= PullGapFocusBufferedThreshold))
            {
                context.PullGapFocusActive = true;
                gapFocusChanged = true;
            }
            if (context.NextChunkIndex == 0 && oldestOutstandingChunkIndex == 0)
            {
                context.PullFirstChunkTimeoutCount++;
                failStalledFirstChunk = context.PullFirstChunkTimeoutCount >= PullSessionFirstChunkStallTimeouts;
            }
            else
            {
                context.PullFirstChunkTimeoutCount = 0;
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_request_timeout_detected; transfer_id={context.TransferId}; session_id={context.SessionId}; oldest_chunk={oldestOutstandingChunkIndex}; timeout_streak={timeoutStreak}; outstanding_count={outstandingCount}; pipeline_depth={pipelineDepth}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");

        LogPullReorderPressure(
            context.TransferId,
            context.SessionId,
            context.NextChunkIndex,
            highestReceivedChunkIndex,
            lateArrivalDistance,
            outstandingCount,
            pipelineDepth,
            context.ChunkSizeBytes);

        if (degradedChanged)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_degraded_entered; transfer_id={context.TransferId}; session_id={context.SessionId}");
        }

        if (gapFocusChanged)
        {
            LogGapFocusChanged(context.TransferId, context.SessionId, active: true, context.NextChunkIndex, highestReceivedChunkIndex, lateArrivalDistance);
        }

        if (pipelineDepthChanged)
        {
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, pipelineDepth, degraded: context.PullSessionDegraded || screenshareDegraded);
        }

        if (failStalledFirstChunk)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_pull_session_stalled_at_first_chunk; transfer_id={context.TransferId}; session_id={context.SessionId}");
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: PullSessionStalledErrorCode,
                statusMessage: PullSessionStalledErrorCode,
                sendError: true,
                errorMessage: PullSessionStalledErrorCode,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (shouldResend)
        {
            await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: true).ConfigureAwait(false);
        }
    }

    private bool ShouldSendPullAckLocked(InboundTransferContext context, int contiguousChunkCount, bool completed, bool sentRequestImmediately)
    {
        if (context.PullAckDebtChunks <= 0 || contiguousChunkCount <= 0)
        {
            return false;
        }

        if (completed)
        {
            return true;
        }

        if (context.PullAckDebtChunks >= ResolveInboundAckThresholdLocked(context))
        {
            return true;
        }

        if (context.OutstandingChunkRequests.Count == 0)
        {
            return true;
        }

        if (context.PullLastAckSentUtc is null)
        {
            return false;
        }

        if (sentRequestImmediately)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - context.PullLastAckSentUtc.Value >= TimeSpan.FromMilliseconds(ResolveInboundAckCoalesceDelayMsLocked(context));
    }

    private async Task HandleInboundPullChunkAsync(InboundTransferContext context, FileTransferChunkDataFrameV2 chunk)
    {
        byte[] chunkBytes;
        if (chunk.Data.Length == 0 || chunk.Data.Length > FileTransferProtocol.MaxChunkRawBytes)
        {
            throw new InvalidOperationException("Chunk payload exceeded the V2 raw payload budget.");
        }
        chunkBytes = chunk.Data;

        await HandleInboundPullChunksAsync(
            context,
            [(chunk.ChunkIndex, chunkBytes)]).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunkBatchAsync(InboundTransferContext context, FileTransferChunkBatchFrameV2 batch)
    {
        var chunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(batch.DataSegments.Count);
        for (var segmentOffset = 0; segmentOffset < batch.DataSegments.Count; segmentOffset++)
        {
            var chunkBytes = batch.DataSegments[segmentOffset];
            if (chunkBytes.Length == 0 || chunkBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
            {
                throw new InvalidOperationException("Chunk batch payload exceeded the V2 raw payload budget.");
            }

            chunks.Add((batch.StartChunkIndex + segmentOffset, chunkBytes));
        }

        await HandleInboundPullChunksAsync(context, chunks).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunksAsync(InboundTransferContext context, IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        var isV3 = context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3;
        List<byte[]> contiguousChunkBytes = [];
        int ackDebtChunks = 0;
        long ackDebtBytes = 0;
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
                    context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                    var now = DateTimeOffset.UtcNow;
                    context.RecentPullChunkSentUtc.Enqueue(now);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, now);
                    context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_received; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}");
                }
            }

            while (context.PendingChunks.Remove(context.NextChunkIndex, out var contiguous))
            {
                contiguousChunkBytes.Add(contiguous);
                context.OutstandingChunkRequests.Remove(context.NextChunkIndex);
                context.RequestedChunks.Remove(context.NextChunkIndex);
                context.ChunkAttemptCounts.Remove(context.NextChunkIndex);
                context.NextChunkIndex++;
                context.ChunksTransferred++;
                context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + contiguous.Length);
            }

            context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);

            if (contiguousChunkBytes.Count > 0)
            {
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
                context.PullFirstChunkTimeoutCount = 0;
                context.PullTimeoutOldestChunkIndex = null;
                context.PullTimeoutStreak = 0;
                context.PullCommittedFrontier = context.NextChunkIndex;
                if (context.PullGapFocusActive && contiguousChunkBytes.Count >= 2)
                {
                    context.PullGapFocusActive = false;
                    LogGapFocusChanged(context.TransferId, context.SessionId, active: false, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullLateArrivalDistance);
                }
                ackDebtChunks = contiguousChunkBytes.Count;
                ackDebtBytes = contiguousChunkBytes.Sum(static bytes => (long)bytes.Length);
                context.PullAckDebtChunks += ackDebtChunks;
                context.PullAckDebtBytes += ackDebtBytes;
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

        foreach (var bytes in contiguousChunkBytes)
        {
            await context.WriteStream!.WriteAsync(bytes, context.LifetimeCts.Token).ConfigureAwait(false);
            context.Hash!.AppendData(bytes);
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

        if (contiguousChunkBytes.Count > 0)
        {
            LogPullBatchCommit(context.TransferId, context.SessionId, contiguousChunkBytes.Count, context.NextChunkIndex, context.BytesTransferred);
        }

        var sentRequestImmediately = false;
        if (!completed)
        {
            if (isV3)
            {
                await SendInboundGrantWindowV3Async(context, forceGrant: false).ConfigureAwait(false);
            }
            else
            {
                sentRequestImmediately = await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: false).ConfigureAwait(false);
            }
        }

        if (isV3)
        {
            if (context.DataSession is not null && contiguousChunkBytes.Count > 0)
            {
                await SendInboundGrantWindowV3Async(context, forceGrant: completed).ConfigureAwait(false);
            }
        }
        else if (context.DataSession is not null &&
            ShouldSendPullAckLocked(context, ackDebtChunks, completed, sentRequestImmediately))
        {
            await context.DataSession.SendAsync(
                    new FileTransferAckProgressFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        NextExpectedChunkIndex = context.NextChunkIndex,
                        BytesCommitted = context.BytesTransferred,
                    },
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_progress_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_batch_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; contiguous_chunk_count={context.PullAckDebtChunks}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_debt_flushed; transfer_id={context.TransferId}; session_id={context.SessionId}; ack_debt_chunks={context.PullAckDebtChunks}; ack_debt_bytes={context.PullAckDebtBytes}");
            LogPullBinaryFrameSent(
                context.TransferId,
                context.SessionId,
                new FileTransferAckProgressFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    BytesCommitted = context.BytesTransferred,
                },
                payloadBytes: 0);

            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    var now = DateTimeOffset.UtcNow;
                    context.PullLastAckSentUtc = now;
                    context.PullLastAckSentChunkIndex = context.NextChunkIndex;
                    context.PullAckDebtChunks = 0;
                    context.PullAckDebtBytes = 0;
                    context.RecentPullAckSentUtc.Enqueue(now);
                    MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
                }
            }
        }
        else if (contiguousChunkBytes.Count > 0 && sentRequestImmediately)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_suppressed_coalesced; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
        }
        else if (contiguousChunkBytes.Count > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_flush_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; ack_debt_chunks={context.PullAckDebtChunks}; outstanding_count={context.OutstandingChunkRequests.Count}");
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
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
            context.PullSentChunkCache.Remove(obsoleteChunkIndex);
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
            _ => "(none)",
        };

    private static void LogPullBinaryFrameSent(string transferId, string sessionId, FileTransferDataFrameV2 frame, int payloadBytes)
    {
        var serializedPayloadBytes = FileTransferDataFrameCodec.Serialize(frame).Length;
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; payload_bytes={serializedPayloadBytes}; serialized_payload_bytes={serializedPayloadBytes}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

    private static void LogPullBinaryFrameReceived(string transferId, string sessionId, FileTransferDataFrameV2 frame)
    {
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

}
