using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundPullSendLoopV3Async(OutboundTransferContext context)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            var initialPipelineDepth = ResolveOutboundInitialPipelineDepth(context);
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
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
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
                context.PullV3GrantedUntilExclusive = 0;
                context.PullSentChunkCache.Clear();
            }

            UpdateOutboundState(context, FileTransferTransferState.AwaitingStart, 0, 0, "Starting file transfer.");
            await currentTransport.SendFileTransferStartAsync(startMessage, context.LifetimeCts.Token).ConfigureAwait(false);
            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                reason: $"role={sessionOpen.SessionRole}; protocol_version={sessionOpen.ProtocolVersion}; chunk_size_bytes={sessionOpen.ChunkSizeBytes}; pipeline_depth={sessionOpen.InitialPipelineDepth}");

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);
            InitializeOutboundV3SenderCachePolicy(context, stream.CanSeek);

            var manifest = new FileTransferManifestFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                ChunkSizeBytes = context.ChunkSizeBytes,
                ChunkCount = context.ChunkCount,
                Sha256Base64 = context.Sha256Base64!,
            };

            await dataSession.SendAsync(manifest, context.LifetimeCts.Token).ConfigureAwait(false);
            LogPullBinaryFrameSent(context.TransferId, context.SessionId, manifest, payloadBytes: 0);
            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for receiver credit.");

            var useAsyncSenderPump = ShouldUseAsyncOutboundV3SenderPump(context);
            var senderPumpTask = useAsyncSenderPump
                ? RunOutboundV3SenderPumpAsync(context, stream, dataSession)
                : null;

            Task<FileTransferDataFrameV2>? pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var delayTask = Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token);
                var completed = senderPumpTask is not null
                    ? await Task.WhenAny(pendingReceiveTask, senderPumpTask, delayTask).ConfigureAwait(false)
                    : await Task.WhenAny(pendingReceiveTask, delayTask).ConfigureAwait(false);
                if (completed == senderPumpTask)
                {
                    await senderPumpTask!.ConfigureAwait(false);
                    return;
                }

                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        await StopOutboundV3SenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferGrantWindowFrameV3 grant:
                        ApplyOutboundV3Grant(context, grant, useAsyncSenderPump);
                        if (useAsyncSenderPump)
                        {
                            SignalOutboundV3SenderPump(context);
                        }
                        else
                        {
                            await SendGrantedChunksV3Async(context, stream, dataSession).ConfigureAwait(false);
                        }

                        break;
                    case FileTransferAckProgressFrameV3 ack:
                        ApplyOutboundV3Ack(context, ack, useAsyncSenderPump);
                        if (useAsyncSenderPump)
                        {
                            SignalOutboundV3SenderPump(context);
                        }
                        else
                        {
                            await SendGrantedChunksV3Async(context, stream, dataSession).ConfigureAwait(false);
                        }

                        break;
                    case FileTransferRepairRequestFrameV3 repair:
                        if (useAsyncSenderPump)
                        {
                            EnqueueRequestedChunksV3ForPump(context, repair);
                            SignalOutboundV3SenderPump(context);
                        }
                        else
                        {
                            await ResendRequestedChunksV3Async(context, stream, dataSession, repair).ConfigureAwait(false);
                        }

                        break;
                    case FileTransferRepairRequestSetFrameV3 repairSet:
                        if (useAsyncSenderPump)
                        {
                            EnqueueRequestedChunkSetV3ForPump(context, repairSet);
                            SignalOutboundV3SenderPump(context);
                        }
                        else
                        {
                            await ResendRequestedChunkSetV3Async(context, stream, dataSession, repairSet).ConfigureAwait(false);
                        }

                        break;
                    case FileTransferCancelFrameV2 cancel:
                        ForceLogOutboundV3SenderThroughputWindow(context);
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Canceled,
                            errorCode: CanceledReason,
                            statusMessage: cancel.Reason ?? "Transfer canceled by receiver.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        await StopOutboundV3SenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    case FileTransferCompleteFrameV2:
                        ForceLogOutboundV3SenderThroughputWindow(context);
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Completed,
                            errorCode: null,
                            statusMessage: "Transfer complete.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        await StopOutboundV3SenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame_v3");
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

    private async Task RunInboundPullReceiveLoopV3Async(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
    {
        Task? receiverFeedbackPumpTask = null;
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

            FileTransferManifestFrameV3? manifest = null;
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
                if (frame is FileTransferManifestFrameV3 receivedManifest)
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
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "waiting_for_manifest_v3");
                }
            }

            await InitializeInboundPullManifestV3Async(context, manifest).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullV3ReceiverFeedbackPumpEnabled = ShouldUseInboundV3ReceiverFeedbackPumpLocked(context);
                    if (context.PullV3ReceiverFeedbackPumpEnabled)
                    {
                        receiverFeedbackPumpTask = RunInboundV3ReceiverFeedbackPumpAsync(context);
                    }
                }
            }

            await SendInboundGrantWindowV3Async(context, forceGrant: true).ConfigureAwait(false);

            pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var delayTask = Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token);
                var completed = receiverFeedbackPumpTask is null
                    ? await Task.WhenAny(pendingReceiveTask, delayTask).ConfigureAwait(false)
                    : await Task.WhenAny(pendingReceiveTask, receiverFeedbackPumpTask, delayTask).ConfigureAwait(false);
                if (completed == receiverFeedbackPumpTask)
                {
                    await receiverFeedbackPumpTask!.ConfigureAwait(false);
                    return;
                }

                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    await MaybeHandlePullV3RepairSchedulerAsync(context).ConfigureAwait(false);
                    await MaybeSendInboundV3CreditKeepaliveGrantAsync(context).ConfigureAwait(false);
                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferChunkDataFrameV3 chunk:
                        await HandleInboundPullChunkV3Async(context, chunk).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV3 batch:
                        await HandleInboundPullChunkBatchV3Async(context, batch).ConfigureAwait(false);
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
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_inbound_frame_v3");
                        break;
                }

                await MaybeHandlePullV3RepairSchedulerAsync(context).ConfigureAwait(false);
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
        finally
        {
            await StopInboundV3ReceiverFeedbackPumpAsync(context, receiverFeedbackPumpTask).ConfigureAwait(false);
        }
    }

    private async Task InitializeInboundPullManifestV3Async(InboundTransferContext context, FileTransferManifestFrameV3 manifest)
    {
        await InitializeInboundPullManifestAsync(
            context,
            new FileTransferManifestFrameV2
            {
                SessionId = manifest.SessionId,
                TransferId = manifest.TransferId,
                FileName = manifest.FileName,
                FileSizeBytes = manifest.FileSizeBytes,
                ChunkSizeBytes = manifest.ChunkSizeBytes,
                ChunkCount = manifest.ChunkCount,
                Sha256Base64 = manifest.Sha256Base64,
            }).ConfigureAwait(false);

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.TransportProfileKind = ResolveTransportProfileKind(transport);
            context.PullV3ConservativeStartupActive =
                !sessionScreenShareActive &&
                !sessionScreenShareDegraded &&
                context.TransportProfileKind == FileTransferTransportProfileKind.ConservativeNknStartup;
            context.PullV3ConservativeStartupDegradedActive = false;
            context.PullV3ConservativeStartupProbeActive = false;
            context.PullV3ConservativeStartupStartedUtc = context.PullV3ConservativeStartupActive ? DateTimeOffset.UtcNow : null;
            context.PullV3ConservativeStartupExitedUtc = null;
            context.PullV3ConservativeStartupExitReason = null;
            context.PullV3ConservativeStartupExitBytes = 0;
            context.PullV3FirstRepairOrTimeoutBeforeStartupExit = false;
            context.PullV3ExpandedWindowActive = false;
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            context.PullV3LimitedWindowActive = false;
            context.PullV3LastGrantTargetWindowBytes = 0;
            context.PullV3LastReorderPolicyDecision = null;
            context.PullV3LastReorderPolicyDecisionLogUtc = null;
            context.PullV3LastGrantWindowSummaryLogUtc = null;
        }
    }

    private static FileTransferChunkDataFrameV2 CreatePullChunkDataFrame(
        int protocolVersion,
        string sessionId,
        string transferId,
        int chunkIndex,
        int chunkCount,
        byte[] chunkBytes)
        => protocolVersion == FileTransferProtocol.ProtocolVersionV3
            ? new FileTransferChunkDataFrameV3
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = chunkBytes,
            }
            : new FileTransferChunkDataFrameV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = chunkBytes,
            };

    private void InitializeOutboundV3SenderCachePolicy(OutboundTransferContext context, bool sourceCanSeek)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.PullSourceCanSeek = sourceCanSeek;
            context.PullSentChunkCache.Clear();
            context.PullSentChunkCacheBytes = 0;
            context.PullSenderCachePressureActive = false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_policy; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(sourceCanSeek ? 1 : 0)}; seekable_target_bytes={SenderRepairCacheSeekableTargetBytes}; seekable_hard_limit_bytes={SenderRepairCacheSeekableHardLimitBytes}; non_seekable_hard_limit_bytes={SenderRepairCacheNonSeekableHardLimitBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(sourceCanSeek)}");
    }

    private static int GetExpectedOutboundChunkLength(OutboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= context.ChunkCount || context.ChunkSizeBytes <= 0)
        {
            return 0;
        }

        var offset = (long)chunkIndex * context.ChunkSizeBytes;
        var remaining = Math.Max(0, context.FileSizeBytes - offset);
        return (int)Math.Min(context.ChunkSizeBytes, remaining);
    }

    private static long GetSenderRepairCacheHardLimitBytes(bool sourceCanSeek)
        => sourceCanSeek ? SenderRepairCacheSeekableHardLimitBytes : SenderRepairCacheNonSeekableHardLimitBytes;

    private static bool TryGetCachedChunkLocked(OutboundTransferContext context, int chunkIndex, out byte[] chunkBytes)
        => context.PullSentChunkCache.TryGetValue(chunkIndex, out chunkBytes!);

    private void StoreSentChunkInCacheLocked(OutboundTransferContext context, int chunkIndex, byte[] chunkBytes)
    {
        if (context.PullSentChunkCache.TryGetValue(chunkIndex, out var existing))
        {
            context.PullSentChunkCacheBytes -= existing.Length;
        }

        context.PullSentChunkCache[chunkIndex] = chunkBytes;
        context.PullSentChunkCacheBytes += chunkBytes.Length;
        TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
        EnforceSenderRepairCacheLimitLocked(context, chunkIndex);
    }

    private void EnforceSenderRepairCacheLimitLocked(OutboundTransferContext context, int protectedChunkIndex)
    {
        var hardLimitBytes = GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek);
        if (context.PullSentChunkCacheBytes <= hardLimitBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
            return;
        }

        if (!context.PullSourceCanSeek)
        {
            LogSenderRepairCacheFailureLocked(context, protectedChunkIndex, SenderCacheExhaustedErrorCode, "non_seekable_cache_limit");
            throw new SenderCacheException(SenderCacheExhaustedErrorCode, "Sender repair cache exceeded the non-seekable source limit.");
        }

        MaybeLogSenderRepairCachePressureEnterLocked(context, "seekable_cache_hard_limit");
        var evicted = 0;
        foreach (var chunkIndex in context.PullSentChunkCache.Keys
                     .OrderBy(chunkIndex => context.LastChunkSentUtc.TryGetValue(chunkIndex, out var sentUtc) ? sentUtc : DateTimeOffset.MinValue)
                     .ThenBy(static chunkIndex => chunkIndex)
                     .ToArray())
        {
            if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
            {
                break;
            }

            if (chunkIndex == protectedChunkIndex)
            {
                continue;
            }

            if (context.PullSentChunkCache.Remove(chunkIndex, out var removedBytes))
            {
                context.PullSentChunkCacheBytes -= removedBytes.Length;
                evicted++;
            }
        }

        if (evicted > 0)
        {
            context.PullSenderCacheEvictionCountRecent += evicted;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_sender_repair_cache_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={hardLimitBytes}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; cache_eviction_count={evicted}; reason=evicted_to_target");
        }

        if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
        }
    }

    private void TrimSenderRepairCacheLocked(OutboundTransferContext context, int nextExpectedChunkIndex)
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

        if (context.PullSentChunkCacheBytes <= SenderRepairCacheSeekableTargetBytes)
        {
            MaybeLogSenderRepairCachePressureExitLocked(context);
        }
    }

    private void MaybeLogSenderRepairCachePressureEnterLocked(OutboundTransferContext context, string reason)
    {
        if (context.PullSenderCachePressureActive)
        {
            return;
        }

        context.PullSenderCachePressureActive = true;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_pressure_entered; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek)}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
    }

    private void MaybeLogSenderRepairCachePressureExitLocked(OutboundTransferContext context)
    {
        if (!context.PullSenderCachePressureActive)
        {
            return;
        }

        context.PullSenderCachePressureActive = false;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_cache_pressure_exited; transfer_id={context.TransferId}; session_id={context.SessionId}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_target_bytes={SenderRepairCacheSeekableTargetBytes}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
    }

    private void LogSenderRepairCacheFailureLocked(OutboundTransferContext context, int chunkIndex, string errorCode, string reason)
    {
        var eventName = string.Equals(errorCode, SenderCacheExhaustedErrorCode, StringComparison.Ordinal)
            ? "filetransfer_sender_cache_exhausted"
            : "filetransfer_sender_repair_unavailable";
        LocalOperationalLog.Error(
            "FileTransferService",
            $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; chunk_index={chunkIndex}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}; cache_chunk_count={context.PullSentChunkCache.Count}; cache_bytes={context.PullSentChunkCacheBytes}; cache_hard_limit_bytes={GetSenderRepairCacheHardLimitBytes(context.PullSourceCanSeek)}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; error_code={errorCode}");
    }

    private List<int> FilterRepairChunkIndicesForSend(
        OutboundTransferContext context,
        IEnumerable<int> requestedChunkIndices,
        out RepairChunkFilterStats stats)
    {
        var valid = new List<int>();
        var skippedObsolete = 0;
        var skippedFuture = 0;
        var skippedOutOfBounds = 0;
        var accepted = 0;
        var remoteNextExpected = 0;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                stats = new RepairChunkFilterStats(0, 0, 0, 0, 0);
                return valid;
            }

            accepted = context.ChunksAcceptedForTransport;
            remoteNextExpected = context.RemoteNextExpectedChunkIndex;
            foreach (var chunkIndex in requestedChunkIndices)
            {
                if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
                {
                    skippedOutOfBounds++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "out_of_bounds");
                    continue;
                }

                if (chunkIndex < remoteNextExpected)
                {
                    skippedObsolete++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "obsolete");
                    continue;
                }

                if (chunkIndex >= accepted)
                {
                    skippedFuture++;
                    LogSenderRepairChunkSkippedLocked(context, chunkIndex, "not_yet_sent");
                    continue;
                }

                valid.Add(chunkIndex);
            }
        }

        stats = new RepairChunkFilterStats(remoteNextExpected, accepted, skippedObsolete, skippedFuture, skippedOutOfBounds);
        return valid;
    }

    private void LogSenderRepairChunkSkippedLocked(OutboundTransferContext context, int chunkIndex, string reason)
    {
        context.PullSenderRepairChunkSkippedCountRecent++;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_repair_chunk_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; chunk_index={chunkIndex}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; chunk_count={context.ChunkCount}");
    }

    private async Task SendGrantedChunksV3Async(
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

            var startChunk = context.ChunksAcceptedForTransport;
            var grantedUntilExclusive = Math.Min(context.PullV3GrantedUntilExclusive, context.ChunkCount);
            if (grantedUntilExclusive <= startChunk)
            {
                context.PullSenderSendWaitCountRecent++;
                context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                MaybeLogOutboundV3SenderThroughputWindowLocked(context, DateTimeOffset.UtcNow);
                return;
            }

            if (context.PullSenderFeedCreditWaitStartedUtc is not null)
            {
                context.PullSenderFeedCreditWaitMsRecent += (long)Math.Max(
                    0,
                    (DateTimeOffset.UtcNow - context.PullSenderFeedCreditWaitStartedUtc.Value).TotalMilliseconds);
                context.PullSenderFeedCreditWaitStartedUtc = null;
            }

            chunkIndicesToSend = Enumerable.Range(startChunk, grantedUntilExclusive - startChunk).ToList();
        }

        await SendChunkIndicesV3Async(context, stream, dataSession, chunkIndicesToSend, repairSend: false).ConfigureAwait(false);
    }

    private void SignalOutboundV3SenderPump(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.SignalV3SenderPump();
            }
        }
    }

    private void ForceLogOutboundV3SenderThroughputWindow(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                MaybeLogOutboundV3SenderThroughputWindowLocked(context, DateTimeOffset.UtcNow, force: true);
            }
        }
    }

    private static async Task StopOutboundV3SenderPumpAsync(OutboundTransferContext context, Task? senderPumpTask)
    {
        if (senderPumpTask is null)
        {
            return;
        }

        context.SignalV3SenderPump();
        await senderPumpTask.ConfigureAwait(false);
    }

    private static async Task StopInboundV3ReceiverFeedbackPumpAsync(InboundTransferContext context, Task? receiverFeedbackPumpTask)
    {
        if (receiverFeedbackPumpTask is null)
        {
            return;
        }

        context.SignalV3ReceiverFeedbackPump();
        try
        {
            await receiverFeedbackPumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private async Task RunInboundV3ReceiverFeedbackPumpAsync(InboundTransferContext context)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_feedback_pump_started; transfer_id={context.TransferId}; session_id={context.SessionId}; queue_limit={V3ReceiverFeedbackPumpQueueLimit}; mode=pump");

        while (true)
        {
            context.LifetimeCts.Token.ThrowIfCancellationRequested();

            InboundV3ReceiverFeedbackWork? work = null;
            IFileTransferDataSession? dataSession = null;
            Task? waitForSignal = null;
            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context))
                {
                    return;
                }

                if (context.PullV3ReceiverFeedbackQueue.Count > 0)
                {
                    work = context.PullV3ReceiverFeedbackQueue[0];
                    context.PullV3ReceiverFeedbackQueue.RemoveAt(0);
                    dataSession = context.DataSession;
                }
                else
                {
                    MaybeLogInboundV3ReceiverFeedbackSummaryLocked(context, DateTimeOffset.UtcNow, force: context.IsTerminal);
                    if (context.IsTerminal)
                    {
                        return;
                    }

                    waitForSignal = context.ResetAndGetV3ReceiverFeedbackPumpSignalTask();
                }
            }

            if (work is null)
            {
                if (waitForSignal is not null)
                {
                    var completed = await Task.WhenAny(waitForSignal, Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
                    if (completed != waitForSignal)
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                            {
                                MaybeLogInboundV3ReceiverFeedbackSummaryLocked(context, DateTimeOffset.UtcNow);
                            }
                        }
                    }
                }

                continue;
            }

            if (dataSession is null)
            {
                work.Completion?.TrySetException(new InvalidOperationException("Receiver feedback data session is not available."));
                await FailInboundV3ReceiverFeedbackPumpAsync(context, work, "no_data_session", null).ConfigureAwait(false);
                return;
            }

            var sendStopwatch = Stopwatch.StartNew();
            var sendStartedUtc = DateTimeOffset.UtcNow;
            try
            {
                await dataSession.SendAsync(work.Frame, context.LifetimeCts.Token).ConfigureAwait(false);
                sendStopwatch.Stop();
                var enqueueAgeMs = (long)Math.Max(0, (sendStartedUtc - work.EnqueuedUtc).TotalMilliseconds);
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, context))
                    {
                        context.PullV3ReceiverFeedbackSentRecent++;
                        context.PullV3ReceiverFeedbackMaxEnqueueAgeMsRecent = Math.Max(context.PullV3ReceiverFeedbackMaxEnqueueAgeMsRecent, enqueueAgeMs);
                        context.PullV3ReceiverFeedbackMaxSendDurationMsRecent = Math.Max(context.PullV3ReceiverFeedbackMaxSendDurationMsRecent, sendStopwatch.ElapsedMilliseconds);
                    }
                }

                LogPullBinaryFrameSent(context.TransferId, context.SessionId, work.Frame, payloadBytes: 0);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v3_receiver_feedback_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={work.Frame.Type}; reason={work.Reason}; mode=pump; send_duration_ms={sendStopwatch.ElapsedMilliseconds}; enqueue_to_send_age_ms={enqueueAgeMs}; queue_depth={GetInboundV3ReceiverFeedbackQueueDepth(context)}");
                work.Completion?.TrySetResult(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sendStopwatch.Stop();
                work.Completion?.TrySetException(ex);
                await FailInboundV3ReceiverFeedbackPumpAsync(context, work, "send_failed", ex).ConfigureAwait(false);
                return;
            }
        }
    }

    private int GetInboundV3ReceiverFeedbackQueueDepth(InboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(inboundTransfer, context)
                ? context.PullV3ReceiverFeedbackQueue.Count
                : 0;
        }
    }

    private async Task FailInboundV3ReceiverFeedbackPumpAsync(
        InboundTransferContext context,
        InboundV3ReceiverFeedbackWork work,
        string reason,
        Exception? error)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context))
            {
                context.PullV3ReceiverFeedbackFailedRecent++;
            }
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_feedback_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={work.Frame.Type}; reason={reason}; mode=pump; queue_depth={GetInboundV3ReceiverFeedbackQueueDepth(context)}; error={error?.GetType().Name ?? "(none)"}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: InvalidStateErrorCode,
            statusMessage: error?.Message ?? "Receiver feedback pump failed.",
            sendError: true,
            errorMessage: "Receiver feedback pump failed.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> SendOrQueueInboundV3ReceiverFeedbackAsync(
        InboundTransferContext context,
        FileTransferDataFrameV2 frame,
        string reason,
        bool waitForSend = false)
    {
        IFileTransferDataSession? directDataSession = null;
        TaskCompletionSource<bool>? completion = null;
        var queued = false;
        var queueExhausted = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null)
            {
                return false;
            }

            if (context.PullV3ReceiverFeedbackPumpEnabled)
            {
                completion = waitForSend
                    ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                    : null;
                queueExhausted = !TryEnqueueInboundV3ReceiverFeedbackLocked(
                    context,
                    new InboundV3ReceiverFeedbackWork(frame, DateTimeOffset.UtcNow, reason, completion),
                    out queued);
                if (!queueExhausted)
                {
                    context.SignalV3ReceiverFeedbackPump();
                }
            }
            else
            {
                directDataSession = context.DataSession;
            }
        }

        if (queueExhausted)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v3_receiver_feedback_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frame.Type}; reason=queue_exhausted; mode=pump; queue_depth={V3ReceiverFeedbackPumpQueueLimit}; error_code={ReceiverFeedbackQueueExhaustedErrorCode}");
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: ReceiverFeedbackQueueExhaustedErrorCode,
                statusMessage: "Receiver feedback queue exceeded the safety limit.",
                sendError: true,
                errorMessage: "Receiver feedback queue exceeded the safety limit.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (queued)
        {
            if (completion is not null)
            {
                await completion.Task.ConfigureAwait(false);
            }

            return true;
        }

        if (directDataSession is null)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        await directDataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
        stopwatch.Stop();
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, frame, payloadBytes: 0);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_feedback_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frame.Type}; reason={reason}; mode=direct; send_duration_ms={stopwatch.ElapsedMilliseconds}; enqueue_to_send_age_ms=0; queue_depth=0");
        return true;
    }

    private bool TryEnqueueInboundV3ReceiverFeedbackLocked(
        InboundTransferContext context,
        InboundV3ReceiverFeedbackWork work,
        out bool queued)
    {
        queued = false;
        var coalescible = IsInboundV3ReceiverFeedbackCoalescible(work.Frame);
        if (coalescible && context.PullV3ReceiverFeedbackQueue.Count > 0)
        {
            var lastIndex = context.PullV3ReceiverFeedbackQueue.Count - 1;
            var previous = context.PullV3ReceiverFeedbackQueue[lastIndex];
            if (IsInboundV3ReceiverFeedbackCoalescible(previous.Frame))
            {
                previous.Completion?.TrySetResult(true);
                context.PullV3ReceiverFeedbackQueue[lastIndex] = work;
                context.PullV3ReceiverFeedbackCoalescedRecent++;
                context.PullV3ReceiverFeedbackEnqueuedRecent++;
                context.PullV3ReceiverFeedbackMaxQueueDepthRecent = Math.Max(
                    context.PullV3ReceiverFeedbackMaxQueueDepthRecent,
                    context.PullV3ReceiverFeedbackQueue.Count);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v3_receiver_feedback_coalesced; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_frame_type={previous.Frame.Type}; frame_type={work.Frame.Type}; reason={work.Reason}; mode=pump; queue_depth={context.PullV3ReceiverFeedbackQueue.Count}; coalesced_count={context.PullV3ReceiverFeedbackCoalescedRecent}");
                queued = true;
                return true;
            }
        }

        if (context.PullV3ReceiverFeedbackQueue.Count >= V3ReceiverFeedbackPumpQueueLimit)
        {
            return false;
        }

        context.PullV3ReceiverFeedbackQueue.Add(work);
        context.PullV3ReceiverFeedbackEnqueuedRecent++;
        context.PullV3ReceiverFeedbackMaxQueueDepthRecent = Math.Max(
            context.PullV3ReceiverFeedbackMaxQueueDepthRecent,
            context.PullV3ReceiverFeedbackQueue.Count);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_feedback_enqueued; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={work.Frame.Type}; reason={work.Reason}; mode=pump; queue_depth={context.PullV3ReceiverFeedbackQueue.Count}; queue_limit={V3ReceiverFeedbackPumpQueueLimit}");
        queued = true;
        return true;
    }

    private static bool IsInboundV3ReceiverFeedbackCoalescible(FileTransferDataFrameV2 frame)
        => frame is FileTransferGrantWindowFrameV3 or FileTransferAckProgressFrameV3;

    private void MaybeLogInboundV3ReceiverFeedbackSummaryLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        bool force = false)
    {
        if (!force &&
            context.PullV3ReceiverFeedbackLastSummaryUtc is not null &&
            now - context.PullV3ReceiverFeedbackLastSummaryUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        if (!force &&
            context.PullV3ReceiverFeedbackEnqueuedRecent == 0 &&
            context.PullV3ReceiverFeedbackSentRecent == 0 &&
            context.PullV3ReceiverFeedbackCoalescedRecent == 0 &&
            context.PullV3ReceiverFeedbackFailedRecent == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_feedback_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; mode={(context.PullV3ReceiverFeedbackPumpEnabled ? "pump" : "direct")}; queue_depth={context.PullV3ReceiverFeedbackQueue.Count}; queue_limit={V3ReceiverFeedbackPumpQueueLimit}; enqueued={context.PullV3ReceiverFeedbackEnqueuedRecent}; sent={context.PullV3ReceiverFeedbackSentRecent}; coalesced={context.PullV3ReceiverFeedbackCoalescedRecent}; failed={context.PullV3ReceiverFeedbackFailedRecent}; max_queue_depth={context.PullV3ReceiverFeedbackMaxQueueDepthRecent}; max_enqueue_to_send_age_ms={context.PullV3ReceiverFeedbackMaxEnqueueAgeMsRecent}; max_send_duration_ms={context.PullV3ReceiverFeedbackMaxSendDurationMsRecent}");
        context.PullV3ReceiverFeedbackLastSummaryUtc = now;
        context.PullV3ReceiverFeedbackEnqueuedRecent = 0;
        context.PullV3ReceiverFeedbackSentRecent = 0;
        context.PullV3ReceiverFeedbackCoalescedRecent = 0;
        context.PullV3ReceiverFeedbackFailedRecent = 0;
        context.PullV3ReceiverFeedbackMaxQueueDepthRecent = context.PullV3ReceiverFeedbackQueue.Count;
        context.PullV3ReceiverFeedbackMaxEnqueueAgeMsRecent = 0;
        context.PullV3ReceiverFeedbackMaxSendDurationMsRecent = 0;
    }

    private async Task RunOutboundV3SenderPumpAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession)
    {
        while (true)
        {
            context.LifetimeCts.Token.ThrowIfCancellationRequested();

            PullV3QueuedRepairSend? repairSend = null;
            List<int>? chunkIndicesToSend = null;
            Task? waitForSignal = null;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return;
                }

                if (context.PullV3SenderPumpRepairQueue.Count > 0)
                {
                    repairSend = context.PullV3SenderPumpRepairQueue.Dequeue();
                    foreach (var chunkIndex in repairSend.ChunkIndices)
                    {
                        context.PullV3SenderPumpRepairQueuedChunkIndices.Remove(chunkIndex);
                    }
                }
                else
                {
                    var startChunk = context.ChunksAcceptedForTransport;
                    var grantedUntilExclusive = Math.Min(context.PullV3GrantedUntilExclusive, context.ChunkCount);
                    if (grantedUntilExclusive > startChunk)
                    {
                        if (context.PullSenderFeedCreditWaitStartedUtc is not null)
                        {
                            context.PullSenderFeedCreditWaitMsRecent += (long)Math.Max(
                                0,
                                (DateTimeOffset.UtcNow - context.PullSenderFeedCreditWaitStartedUtc.Value).TotalMilliseconds);
                            context.PullSenderFeedCreditWaitStartedUtc = null;
                        }

                        var maxNormalChunksThisPass = Math.Max(
                            1,
                            (int)Math.Ceiling(
                                ResolveV3SenderTransportPipelinePendingBytesLimit(context) /
                                (double)Math.Max(1, context.ChunkSizeBytes)));
                        var chunkCountThisPass = Math.Min(grantedUntilExclusive - startChunk, maxNormalChunksThisPass);
                        chunkIndicesToSend = Enumerable.Range(startChunk, chunkCountThisPass).ToList();
                    }
                    else
                    {
                        context.PullSenderSendWaitCountRecent++;
                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        MaybeLogOutboundV3CreditStallLocked(context, "waiting_for_credit", DateTimeOffset.UtcNow);
                        MaybeLogOutboundV3SenderThroughputWindowLocked(context, DateTimeOffset.UtcNow);
                        waitForSignal = context.ResetAndGetV3SenderPumpSignalTask();
                    }
                }
            }

            if (repairSend is not null)
            {
                if (repairSend.ChunkIndices.Count > 0)
                {
                    await SendChunkIndicesV3Async(context, stream, dataSession, repairSend.ChunkIndices, repairSend: true).ConfigureAwait(false);
                }

                if (repairSend.LogRepairSetSent)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_repair_set_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; range_count={repairSend.RangeCount}; requested_chunk_count={repairSend.RequestedChunkCount}; sent_chunk_count={repairSend.ChunkIndices.Count}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}; skipped_obsolete_count={repairSend.SkippedObsoleteCount}; skipped_future_count={repairSend.SkippedFutureCount}; skipped_out_of_bounds_count={repairSend.SkippedOutOfBoundsCount}");
                }

                if (repairSend.LogFrontierRepairSent)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_frontier_gap_repair_sender_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; range_count={repairSend.RangeCount}; requested_chunk_count={repairSend.RequestedChunkCount}; sent_chunk_count={repairSend.ChunkIndices.Count}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}; skipped_obsolete_count={repairSend.SkippedObsoleteCount}; skipped_future_count={repairSend.SkippedFutureCount}; skipped_out_of_bounds_count={repairSend.SkippedOutOfBoundsCount}");
                }

                continue;
            }

            if (chunkIndicesToSend is not null)
            {
                await SendChunkIndicesV3Async(context, stream, dataSession, chunkIndicesToSend, repairSend: false).ConfigureAwait(false);
                continue;
            }

            if (waitForSignal is not null)
            {
                var completed = await Task.WhenAny(waitForSignal, Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
                if (completed != waitForSignal)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                        {
                            MaybeLogOutboundV3CreditStallLocked(context, "waiting_for_credit", DateTimeOffset.UtcNow);
                        }
                    }
                }
            }
        }
    }

    private void MaybeLogOutboundV3CreditStallLocked(OutboundTransferContext context, string waitReason, DateTimeOffset now)
    {
        if (context.PullV3LastCreditStallLogUtc is not null &&
            now - context.PullV3LastCreditStallLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        context.PullV3LastCreditStallLogUtc = now;
        var creditWaitActiveMs = context.PullSenderFeedCreditWaitStartedUtc is null
            ? 0L
            : (long)Math.Max(0, (now - context.PullSenderFeedCreditWaitStartedUtc.Value).TotalMilliseconds);
        var lastGrantAgeMs = context.PullV3LastGrantReceivedUtc is null
            ? -1L
            : (long)Math.Max(0, (now - context.PullV3LastGrantReceivedUtc.Value).TotalMilliseconds);
        var remoteGrantedUntilChunkIndexExclusive = Math.Max(
            context.ChunksTransferred,
            Math.Min(context.ChunkCount, context.PullV3GrantedUntilExclusive));
        var availableCreditChunks = Math.Max(0, remoteGrantedUntilChunkIndexExclusive - context.ChunksAcceptedForTransport);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_sender_credit_stall_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; wait_reason={waitReason}; credit_wait_active_ms={creditWaitActiveMs}; accepted_chunk_index={context.ChunksAcceptedForTransport}; remote_next_expected_chunk_index={context.ChunksTransferred}; remote_granted_until_chunk_index_exclusive={remoteGrantedUntilChunkIndexExclusive}; available_credit_chunks={availableCreditChunks}; available_credit_bytes={availableCreditChunks * (long)Math.Max(1, context.ChunkSizeBytes)}; last_grant_age_ms={lastGrantAgeMs}; in_flight_frames={context.PullSenderPipelineCurrentInFlightFrames}; in_flight_bytes={context.PullSenderPipelineCurrentInFlightBytes}; pending_repair_count={context.PullV3SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count)}");
    }

    private void EnqueueRequestedChunksV3ForPump(
        OutboundTransferContext context,
        FileTransferRepairRequestFrameV3 repair)
    {
        var startChunkIndex = Math.Max(0, repair.StartChunkIndex);
        var requestedChunkCount = Math.Max(1, repair.RequestedChunkCount);
        var endChunkExclusive = Math.Min(context.ChunkCount, startChunkIndex + requestedChunkCount);
        if (endChunkExclusive <= startChunkIndex)
        {
            return;
        }

        var chunkIndices = FilterRepairChunkIndicesForSend(
            context,
            Enumerable.Range(startChunkIndex, endChunkExclusive - startChunkIndex),
            out var stats);
        var repairRequestKey = CreateRepairRequestKey(startChunkIndex, requestedChunkCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_frontier_gap_repair_sender_received; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count=1; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={endChunkExclusive}; scheduled_chunk_count={chunkIndices.Count}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
        EnqueueRepairSendForPump(
            context,
            chunkIndices,
            new PullV3QueuedRepairSend(
                chunkIndices,
                RangeCount: 1,
                RequestedChunkCount: requestedChunkCount,
                FirstStartChunkIndex: startChunkIndex,
                LastEndChunkExclusive: endChunkExclusive,
                stats.RemoteNextExpectedChunkIndex,
                stats.ChunksAcceptedForTransport,
                stats.SkippedObsoleteCount,
                stats.SkippedFutureCount,
                stats.SkippedOutOfBoundsCount,
                LogRepairSetSent: false,
                RepairRequestKey: repairRequestKey,
                LogFrontierRepairSent: true));
    }

    private void EnqueueRequestedChunkSetV3ForPump(
        OutboundTransferContext context,
        FileTransferRepairRequestSetFrameV3 repairSet)
    {
        var normalizedRanges = NormalizeRepairRangesForSend(repairSet.Ranges, context.ChunkCount);
        if (normalizedRanges.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_repair_set_received; transfer_id={context.TransferId}; session_id={context.SessionId}; range_count=0; requested_chunk_count=0; skipped_obsolete_count=0; reason=empty");
            return;
        }

        var requestedChunkIndices = new List<int>(FileTransferProtocol.MaxRepairSetChunksV3);
        foreach (var range in normalizedRanges)
        {
            var endExclusive = Math.Min(context.ChunkCount, range.StartChunkIndex + range.RequestedChunkCount);
            for (var chunkIndex = range.StartChunkIndex; chunkIndex < endExclusive && requestedChunkIndices.Count < FileTransferProtocol.MaxRepairSetChunksV3; chunkIndex++)
            {
                requestedChunkIndices.Add(chunkIndex);
            }
        }

        var chunkIndices = FilterRepairChunkIndicesForSend(context, requestedChunkIndices, out var repairFilterStats);
        var requestedChunkCount = normalizedRanges.Sum(static range => range.RequestedChunkCount);
        var firstStart = normalizedRanges[0].StartChunkIndex;
        var lastEndExclusive = normalizedRanges[^1].StartChunkIndex + normalizedRanges[^1].RequestedChunkCount;
        var repairRequestKey = CreateRepairRangesFingerprint(normalizedRanges);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_repair_set_received; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; remote_next_expected_chunk_index={repairFilterStats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairFilterStats.ChunksAcceptedForTransport}; skipped_obsolete_count={repairFilterStats.SkippedObsoleteCount}; skipped_future_count={repairFilterStats.SkippedFutureCount}; skipped_out_of_bounds_count={repairFilterStats.SkippedOutOfBoundsCount}");

        EnqueueRepairSendForPump(
            context,
            chunkIndices,
            new PullV3QueuedRepairSend(
                chunkIndices,
                normalizedRanges.Count,
                requestedChunkCount,
                firstStart,
                lastEndExclusive,
                repairFilterStats.RemoteNextExpectedChunkIndex,
                repairFilterStats.ChunksAcceptedForTransport,
                repairFilterStats.SkippedObsoleteCount,
                repairFilterStats.SkippedFutureCount,
                repairFilterStats.SkippedOutOfBoundsCount,
                LogRepairSetSent: true,
                RepairRequestKey: repairRequestKey,
                LogFrontierRepairSent: false));
    }

    private void EnqueueRepairSendForPump(
        OutboundTransferContext context,
        IReadOnlyCollection<int> chunkIndices,
        PullV3QueuedRepairSend queuedRepair)
    {
        if (chunkIndices.Count == 0)
        {
            if (queuedRepair.LogRepairSetSent)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_repair_set_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={queuedRepair.RepairRequestKey}; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; sent_chunk_count=0; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; remote_next_expected_chunk_index={queuedRepair.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={queuedRepair.ChunksAcceptedForTransport}; skipped_obsolete_count={queuedRepair.SkippedObsoleteCount}; skipped_future_count={queuedRepair.SkippedFutureCount}; skipped_out_of_bounds_count={queuedRepair.SkippedOutOfBoundsCount}");
            }

            if (queuedRepair.LogFrontierRepairSent)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_frontier_gap_repair_sender_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={queuedRepair.RepairRequestKey}; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; sent_chunk_count=0; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; remote_next_expected_chunk_index={queuedRepair.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={queuedRepair.ChunksAcceptedForTransport}; skipped_obsolete_count={queuedRepair.SkippedObsoleteCount}; skipped_future_count={queuedRepair.SkippedFutureCount}; skipped_out_of_bounds_count={queuedRepair.SkippedOutOfBoundsCount}");
            }

            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var deduped = new List<int>(chunkIndices.Count);
            foreach (var chunkIndex in chunkIndices)
            {
                if (context.PullV3SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
                {
                    deduped.Add(chunkIndex);
                }
            }

            if (deduped.Count == 0)
            {
                return;
            }

            context.PullV3SenderPumpRepairQueue.Enqueue(queuedRepair with { ChunkIndices = deduped });
            if (queuedRepair.LogFrontierRepairSent)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_frontier_gap_repair_sender_scheduled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={queuedRepair.RepairRequestKey}; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; scheduled_chunk_count={deduped.Count}; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; queue_depth={context.PullV3SenderPumpRepairQueue.Count}; remote_next_expected_chunk_index={queuedRepair.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={queuedRepair.ChunksAcceptedForTransport}");
            }
        }
    }

    private async Task ResendRequestedChunksV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        FileTransferRepairRequestFrameV3 repair)
    {
        var startChunkIndex = Math.Max(0, repair.StartChunkIndex);
        var requestedChunkCount = Math.Max(1, repair.RequestedChunkCount);
        var endChunkExclusive = Math.Min(context.ChunkCount, startChunkIndex + requestedChunkCount);
        if (endChunkExclusive <= startChunkIndex)
        {
            return;
        }

        var chunkIndices = FilterRepairChunkIndicesForSend(
            context,
            Enumerable.Range(startChunkIndex, endChunkExclusive - startChunkIndex),
            out var stats);
        var repairRequestKey = CreateRepairRequestKey(startChunkIndex, requestedChunkCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_frontier_gap_repair_sender_received; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count=1; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={endChunkExclusive}; scheduled_chunk_count={chunkIndices.Count}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_frontier_gap_repair_sender_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count=1; requested_chunk_count={requestedChunkCount}; sent_chunk_count=0; first_start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={endChunkExclusive}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
            return;
        }

        await SendChunkIndicesV3Async(
            context,
            stream,
            dataSession,
            chunkIndices,
            repairSend: true).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_frontier_gap_repair_sender_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count=1; requested_chunk_count={requestedChunkCount}; sent_chunk_count={chunkIndices.Count}; first_start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={endChunkExclusive}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
    }

    private async Task ResendRequestedChunkSetV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        FileTransferRepairRequestSetFrameV3 repairSet)
    {
        var normalizedRanges = NormalizeRepairRangesForSend(repairSet.Ranges, context.ChunkCount);
        if (normalizedRanges.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_repair_set_received; transfer_id={context.TransferId}; session_id={context.SessionId}; range_count=0; requested_chunk_count=0; skipped_obsolete_count=0; reason=empty");
            return;
        }

        var requestedChunkIndices = new List<int>(FileTransferProtocol.MaxRepairSetChunksV3);
        foreach (var range in normalizedRanges)
        {
            var endExclusive = Math.Min(context.ChunkCount, range.StartChunkIndex + range.RequestedChunkCount);
            for (var chunkIndex = range.StartChunkIndex; chunkIndex < endExclusive && requestedChunkIndices.Count < FileTransferProtocol.MaxRepairSetChunksV3; chunkIndex++)
            {
                requestedChunkIndices.Add(chunkIndex);
            }
        }

        var chunkIndices = FilterRepairChunkIndicesForSend(context, requestedChunkIndices, out var repairFilterStats);

        var requestedChunkCount = normalizedRanges.Sum(static range => range.RequestedChunkCount);
        var firstStart = normalizedRanges[0].StartChunkIndex;
        var lastEndExclusive = normalizedRanges[^1].StartChunkIndex + normalizedRanges[^1].RequestedChunkCount;
        var repairRequestKey = CreateRepairRangesFingerprint(normalizedRanges);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_repair_set_received; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; remote_next_expected_chunk_index={repairFilterStats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairFilterStats.ChunksAcceptedForTransport}; skipped_obsolete_count={repairFilterStats.SkippedObsoleteCount}; skipped_future_count={repairFilterStats.SkippedFutureCount}; skipped_out_of_bounds_count={repairFilterStats.SkippedOutOfBoundsCount}");

        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_repair_set_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; sent_chunk_count=0; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; remote_next_expected_chunk_index={repairFilterStats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairFilterStats.ChunksAcceptedForTransport}; skipped_obsolete_count={repairFilterStats.SkippedObsoleteCount}; skipped_future_count={repairFilterStats.SkippedFutureCount}; skipped_out_of_bounds_count={repairFilterStats.SkippedOutOfBoundsCount}");
            return;
        }

        await SendChunkIndicesV3Async(context, stream, dataSession, chunkIndices, repairSend: true).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_repair_set_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; sent_chunk_count={chunkIndices.Count}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; remote_next_expected_chunk_index={repairFilterStats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairFilterStats.ChunksAcceptedForTransport}; skipped_obsolete_count={repairFilterStats.SkippedObsoleteCount}; skipped_future_count={repairFilterStats.SkippedFutureCount}; skipped_out_of_bounds_count={repairFilterStats.SkippedOutOfBoundsCount}");
    }

    private async Task SendChunkIndicesV3Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        List<int> chunkIndices,
        bool repairSend)
    {
        if (chunkIndices.Count == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            var effectiveDepth = ResolveV3SenderTransportPipelineDepth(context, out var configuredDepth);
            var pendingBytesLimit = ResolveV3SenderTransportPipelinePendingBytesLimit(context);
            var pending = new Queue<PendingV3TransportSend>();
            long pendingRawBytes = 0;

            async Task RetireNextAsync()
            {
                var pendingSend = pending.Dequeue();
                pendingRawBytes = Math.Max(0, pendingRawBytes - pendingSend.Prepared.RawBytes);
                Exception? sendException = null;
                try
                {
                    await pendingSend.SendTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    sendException = ex;
                }

                var sentUtc = DateTimeOffset.UtcNow;
                var fifoWaitMs = (long)Math.Max(0, (sentUtc - pendingSend.ScheduledUtc).TotalMilliseconds);
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context))
                    {
                        context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                        context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                        if (sendException is null)
                        {
                            context.PullSenderPipelineCompletedFramesRecent++;
                            context.PullSenderPipelineFifoWaitMsRecent += fifoWaitMs;
                            context.PullSenderPipelineMaxFifoWaitMsRecent = Math.Max(context.PullSenderPipelineMaxFifoWaitMsRecent, fifoWaitMs);
                        }
                        else
                        {
                            context.PullSenderPipelineFailedFramesRecent++;
                        }
                    }
                }

                if (sendException is not null)
                {
                    if (sendException is OperationCanceledException)
                    {
                        throw new OperationCanceledException(context.LifetimeCts.Token);
                    }

                    throw new InvalidOperationException("File-transfer V3 sender transport send failed.", sendException);
                }

                LogPullBinaryFrameSent(
                    context.TransferId,
                    context.SessionId,
                    pendingSend.Prepared.Frame,
                    pendingSend.Prepared.RawBytes);

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    var startChunkIndex = pendingSend.Prepared.StartChunkIndex;
                    for (var chunkOffset = 0; chunkOffset < pendingSend.Prepared.ChunkCount; chunkOffset++)
                    {
                        var chunkIndex = startChunkIndex + chunkOffset;
                        context.SentAwaitingAck[chunkIndex] = sentUtc;
                        context.LastChunkSentUtc[chunkIndex] = sentUtc;
                        if (!repairSend)
                        {
                            context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, chunkIndex + 1);
                        }

                        context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                    }

                    if (!repairSend)
                    {
                        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                            ? context.FileSizeBytes
                            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                    }

                    TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                    context.PullUsefulPayloadBytesRecent += pendingSend.Prepared.RawBytes;
                    context.PullSenderRawBytesRecent += pendingSend.Prepared.RawBytes;
                    if (pendingSend.Prepared.IsBatch)
                    {
                        context.PullSenderBatchFramesRecent++;
                    }
                    else
                    {
                        context.PullSenderChunkFramesRecent++;
                    }

                    context.PullSenderChunkCountRecent += pendingSend.Prepared.ChunkCount;
                    if (repairSend)
                    {
                        context.PullSenderRepairSendCountRecent += pendingSend.Prepared.ChunkCount;
                    }

                    MaybeLogOutboundV3SenderThroughputWindowLocked(context, sentUtc);
                }
            }

            async Task ScheduleAsync(PreparedV3TransportSend prepared)
            {
                while (pending.Count >= effectiveDepth ||
                       (pending.Count > 0 && pendingRawBytes + prepared.RawBytes > pendingBytesLimit))
                {
                    var slotWaitStarted = Stopwatch.GetTimestamp();
                    await RetireNextAsync().ConfigureAwait(false);
                    var slotWaitMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(slotWaitStarted).TotalMilliseconds);
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                        {
                            context.PullSenderFeedPipelineSlotWaitMsRecent += slotWaitMs;
                        }
                    }
                }

                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                var scheduleStarted = Stopwatch.GetTimestamp();
                Task sendTask;
                try
                {
                    sendTask = dataSession.SendAsync(prepared.Frame, context.LifetimeCts.Token);
                }
                catch (Exception ex)
                {
                    sendTask = Task.FromException(ex);
                }

                var scheduledUtc = DateTimeOffset.UtcNow;
                var scheduleDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds);
                pending.Enqueue(new PendingV3TransportSend(prepared, sendTask, scheduledUtc));
                pendingRawBytes += prepared.RawBytes;

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    context.PullSenderPipelineConfiguredDepthRecent = configuredDepth;
                    context.PullSenderPipelineEffectiveDepthRecent = effectiveDepth;
                    context.PullSenderPipelineScheduledFramesRecent++;
                    context.PullSenderFeedScheduleDurationMsRecent += scheduleDurationMs;
                    if (context.PullSenderFeedLastScheduleUtc is not null)
                    {
                        context.PullSenderFeedInterScheduleGapMsRecent.Add((long)Math.Max(
                            0,
                            (scheduledUtc - context.PullSenderFeedLastScheduleUtc.Value).TotalMilliseconds));
                    }

                    context.PullSenderFeedLastScheduleUtc = scheduledUtc;
                    context.PullSenderPipelineCurrentInFlightFrames++;
                    context.PullSenderPipelineCurrentInFlightBytes += prepared.RawBytes;
                    context.PullSenderPipelineMaxInFlightFramesRecent = Math.Max(
                        context.PullSenderPipelineMaxInFlightFramesRecent,
                        context.PullSenderPipelineCurrentInFlightFrames);
                    context.PullSenderPipelineMaxInFlightBytesRecent = Math.Max(
                        context.PullSenderPipelineMaxInFlightBytesRecent,
                        context.PullSenderPipelineCurrentInFlightBytes);
                    if (!repairSend)
                    {
                        var scheduledEndExclusive = prepared.StartChunkIndex + prepared.ChunkCount;
                        var acceptedProgressLagBytes = Math.Max(0, scheduledEndExclusive - context.ChunksAcceptedForTransport) *
                            (long)Math.Max(1, context.ChunkSizeBytes);
                        context.PullSenderPipelineMaxAcceptedProgressLagBytesRecent = Math.Max(
                            context.PullSenderPipelineMaxAcceptedProgressLagBytesRecent,
                            acceptedProgressLagBytes);
                    }
                }
            }

            for (var index = 0; index < chunkIndices.Count; index++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                var batchPrepareStarted = Stopwatch.GetTimestamp();
                var preparedBatch = await TryPrepareChunkBatchV3Async(context, stream, chunkIndices, index, buffer, repairSend).ConfigureAwait(false);
                var batchPrepareDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(batchPrepareStarted).TotalMilliseconds);
                if (preparedBatch is not null)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                        {
                            context.PullSenderFeedBatchFramesPreparedRecent++;
                            context.PullSenderFeedChunkCountPreparedRecent += preparedBatch.ChunkCount;
                            context.PullSenderFeedRawBytesPreparedRecent += preparedBatch.RawBytes;
                            context.PullSenderFeedBatchPrepareDurationMsRecent += batchPrepareDurationMs;
                        }
                    }

                    await ScheduleAsync(preparedBatch).ConfigureAwait(false);
                    index += preparedBatch.ChunkCount - 1;
                    continue;
                }

                var chunkIndex = chunkIndices[index];
                var chunkBytes = await LoadChunkBytesForV3SendAsync(context, stream, chunkIndex, buffer, repairSend).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                    {
                        context.PullSenderFeedChunkFramesPreparedRecent++;
                        context.PullSenderFeedChunkCountPreparedRecent++;
                        context.PullSenderFeedRawBytesPreparedRecent += chunkBytes.Length;
                    }
                }

                var frame = new FileTransferChunkDataFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = context.ChunkCount,
                    Data = chunkBytes,
                };

                await ScheduleAsync(new PreparedV3TransportSend(frame, chunkIndex, 1, chunkBytes.Length, IsBatch: false)).ConfigureAwait(false);
            }

            while (pending.Count > 0)
            {
                await RetireNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<PreparedV3TransportSend?> TryPrepareChunkBatchV3Async(
        OutboundTransferContext context,
        Stream stream,
        IReadOnlyList<int> chunkIndices,
        int startListIndex,
        byte[] buffer,
        bool repairSend)
    {
        if (startListIndex + 1 >= chunkIndices.Count)
        {
            return null;
        }

        var payloadProfile = context.PayloadEfficiencyProfile;
        var maxBatchChunkCount = Math.Clamp(payloadProfile.MaxBatchChunkCount, 1, PullV3BatchMaxChunks);
        if (maxBatchChunkCount < 2)
        {
            return null;
        }

        var targetBatchRawBytes = Math.Clamp(
            payloadProfile.TargetBatchRawBytes,
            1,
            FileTransferProtocol.MaxChunkBatchRawBytesV3);
        var startChunkIndex = chunkIndices[startListIndex];
        var expectedChunkIndex = startChunkIndex;
        var totalRawBytes = 0;
        List<byte[]> dataSegments = [];
        for (var index = startListIndex; index < chunkIndices.Count && dataSegments.Count < maxBatchChunkCount; index++)
        {
            var chunkIndex = chunkIndices[index];
            if (chunkIndex != expectedChunkIndex)
            {
                break;
            }

            var chunkBytes = await LoadChunkBytesForV3SendAsync(context, stream, chunkIndex, buffer, repairSend).ConfigureAwait(false);
            var candidateRawBytes = totalRawBytes + chunkBytes.Length;
            if (candidateRawBytes > targetBatchRawBytes)
            {
                break;
            }

            if (!CanSerializeChunkBatchV3(
                    context.SessionId,
                    context.TransferId,
                    startChunkIndex,
                    context.ChunkCount,
                    dataSegments,
                    chunkBytes))
            {
                break;
            }

            dataSegments.Add(chunkBytes);
            totalRawBytes = candidateRawBytes;
            expectedChunkIndex++;
        }

        if (dataSegments.Count < 2)
        {
            return null;
        }

        var batch = new FileTransferChunkBatchFrameV3
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = startChunkIndex,
            ChunkCount = context.ChunkCount,
            DataSegments = dataSegments,
            BatchProfile = payloadProfile.Name,
        };

        _ = FileTransferDataFrameCodec.Serialize(batch);
        return new PreparedV3TransportSend(batch, startChunkIndex, dataSegments.Count, totalRawBytes, IsBatch: true);
    }

    private static bool CanSerializeChunkBatchV3(
        string sessionId,
        string transferId,
        int startChunkIndex,
        int chunkCount,
        IReadOnlyList<byte[]> existingSegments,
        byte[] candidateSegment)
    {
        var candidateSegments = new byte[existingSegments.Count + 1][];
        for (var index = 0; index < existingSegments.Count; index++)
        {
            candidateSegments[index] = existingSegments[index];
        }

        candidateSegments[^1] = candidateSegment;
        try
        {
            _ = FileTransferDataFrameCodec.Serialize(
                new FileTransferChunkBatchFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    ChunkCount = chunkCount,
                    DataSegments = candidateSegments,
                });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<byte[]> LoadChunkBytesForV3SendAsync(
        OutboundTransferContext context,
        Stream stream,
        int chunkIndex,
        byte[] buffer,
        bool repairSend)
    {
        if (repairSend)
        {
            return await TryLoadRepairChunkAsync(context, stream, chunkIndex, buffer).ConfigureAwait(false);
        }

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                TryGetCachedChunkLocked(context, chunkIndex, out var cachedBytes))
            {
                return cachedBytes;
            }
        }

        var chunkBytes = await ReadChunkExactAsync(context, stream, chunkIndex, buffer, seekBeforeRead: stream.CanSeek).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                StoreSentChunkInCacheLocked(context, chunkIndex, chunkBytes);
            }
        }

        return chunkBytes;
    }

    private async Task<byte[]> TryLoadRepairChunkAsync(OutboundTransferContext context, Stream stream, int chunkIndex, byte[] buffer)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                throw new OperationCanceledException(context.LifetimeCts.Token);
            }

            if (TryGetCachedChunkLocked(context, chunkIndex, out var cachedBytes))
            {
                context.PullSenderCacheHitCountRecent++;
                return cachedBytes;
            }

            context.PullSenderCacheMissCountRecent++;
            if (!context.PullSourceCanSeek)
            {
                LogSenderRepairCacheFailureLocked(context, chunkIndex, SenderRepairUnavailableErrorCode, "non_seekable_cache_miss");
                throw new SenderCacheException(SenderRepairUnavailableErrorCode, "Requested repair chunk is no longer available from the non-seekable source cache.");
            }
        }

        var chunkBytes = await ReadChunkExactAsync(context, stream, chunkIndex, buffer, seekBeforeRead: true).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.PullSenderSourceRereadCountRecent++;
                StoreSentChunkInCacheLocked(context, chunkIndex, chunkBytes);
            }
        }

        return chunkBytes;
    }

    private async Task<byte[]> ReadChunkExactAsync(
        OutboundTransferContext context,
        Stream stream,
        int chunkIndex,
        byte[] buffer,
        bool seekBeforeRead)
    {
        var readStarted = Stopwatch.GetTimestamp();
        var expectedLength = GetExpectedOutboundChunkLength(context, chunkIndex);
        try
        {
            if (expectedLength <= 0)
            {
                throw new InvalidOperationException("Requested chunk is outside the declared file size.");
            }

            var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
            if (seekBeforeRead)
            {
                if (!stream.CanSeek)
                {
                    throw new InvalidOperationException("Source stream must be seekable for random chunk reads.");
                }

                if (stream.Position != fileOffset)
                {
                    stream.Seek(fileOffset, SeekOrigin.Begin);
                }
            }

            var totalRead = 0;
            while (totalRead < expectedLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, expectedLength - totalRead), context.LifetimeCts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new InvalidOperationException("Source stream did not match the declared file size.");
                }

                totalRead += read;
            }

            var chunkBytes = new byte[expectedLength];
            Buffer.BlockCopy(buffer, 0, chunkBytes, 0, expectedLength);
            return chunkBytes;
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullSenderFeedSourceReadErrorCountRecent++;
                }
            }

            throw;
        }
        finally
        {
            var readDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    context.PullSenderFeedReadDurationMsRecent += readDurationMs;
                }
            }
        }
    }

    private void ApplyOutboundV3Grant(OutboundTransferContext context, FileTransferGrantWindowFrameV3 grant, bool asyncSenderPumpEnabled)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var previousGrantedUntilExclusive = context.PullV3GrantedUntilExclusive;
            var previousAcceptedForTransport = context.ChunksAcceptedForTransport;
            ApplyOutboundV3ProgressLocked(context, grant.NextExpectedChunkIndex, grant.BytesCommitted);
            context.PullV3GrantedUntilExclusive = Math.Max(
                grant.NextExpectedChunkIndex,
                Math.Min(context.ChunkCount, grant.GrantedUntilChunkIndexExclusive));
            context.PullV3LastGrantReceivedUtc = now;
            context.StatusMessage = "Receiver granted more transfer credit.";
            LogOutboundV3GrantApplySummaryLocked(
                context,
                FileTransferProtocol.GrantWindowFrameTypeV3,
                previousGrantedUntilExclusive,
                previousAcceptedForTransport,
                asyncSenderPumpEnabled,
                now);
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private void ApplyOutboundV3Ack(OutboundTransferContext context, FileTransferAckProgressFrameV3 ack, bool asyncSenderPumpEnabled)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var previousGrantedUntilExclusive = context.PullV3GrantedUntilExclusive;
            var previousAcceptedForTransport = context.ChunksAcceptedForTransport;
            ApplyOutboundV3ProgressLocked(context, ack.NextExpectedChunkIndex, ack.BytesCommitted);
            context.StatusMessage = context.ChunksTransferred >= context.ChunkCount
                ? "Waiting for receiver verification."
                : "Receiver acknowledged streamed chunks.";
            LogOutboundV3GrantApplySummaryLocked(
                context,
                FileTransferProtocol.AckProgressFrameTypeV3,
                previousGrantedUntilExclusive,
                previousAcceptedForTransport,
                asyncSenderPumpEnabled,
                now);
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private void LogOutboundV3GrantApplySummaryLocked(
        OutboundTransferContext context,
        string frameType,
        int previousGrantedUntilExclusive,
        int previousAcceptedForTransport,
        bool asyncSenderPumpEnabled,
        DateTimeOffset now)
    {
        var newGrantedUntilExclusive = Math.Max(context.ChunksTransferred, Math.Min(context.ChunkCount, context.PullV3GrantedUntilExclusive));
        var availableCreditChunksBefore = Math.Max(0, Math.Min(context.ChunkCount, previousGrantedUntilExclusive) - previousAcceptedForTransport);
        var availableCreditChunksAfter = Math.Max(0, newGrantedUntilExclusive - context.ChunksAcceptedForTransport);
        var creditWaitActiveMs = context.PullSenderFeedCreditWaitStartedUtc is null
            ? 0L
            : (long)Math.Max(0, (now - context.PullSenderFeedCreditWaitStartedUtc.Value).TotalMilliseconds);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_sender_grant_apply_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; async_sender_pump={(asyncSenderPumpEnabled ? 1 : 0)}; previous_granted_until_chunk_index_exclusive={previousGrantedUntilExclusive}; new_granted_until_chunk_index_exclusive={newGrantedUntilExclusive}; previous_accepted_chunk_index={previousAcceptedForTransport}; accepted_chunk_index={context.ChunksAcceptedForTransport}; remote_next_expected_chunk_index={context.ChunksTransferred}; available_credit_chunks_before={availableCreditChunksBefore}; available_credit_chunks_after={availableCreditChunksAfter}; available_credit_bytes_after={availableCreditChunksAfter * (long)Math.Max(1, context.ChunkSizeBytes)}; credit_wait_active_ms={creditWaitActiveMs}; send_pump_signaled={(asyncSenderPumpEnabled ? 1 : 0)}; chunks_schedulable={availableCreditChunksAfter}; in_flight_frames={context.PullSenderPipelineCurrentInFlightFrames}; in_flight_bytes={context.PullSenderPipelineCurrentInFlightBytes}");
    }

    private static void ApplyOutboundV3ProgressLocked(OutboundTransferContext context, int nextExpectedChunkIndex, long bytesCommitted)
    {
        context.ChunksTransferred = Math.Max(context.ChunksTransferred, Math.Min(nextExpectedChunkIndex, context.ChunkCount));
        context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(bytesCommitted, context.FileSizeBytes));
        foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            context.SentAwaitingAck.Remove(chunkIndex);
        }

        foreach (var chunkIndex in context.LastChunkSentUtc.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            context.LastChunkSentUtc.Remove(chunkIndex);
        }

        TrimOutboundPullSentChunkCache(context, nextExpectedChunkIndex);
    }

    private async Task HandleInboundPullChunkV3Async(InboundTransferContext context, FileTransferChunkDataFrameV3 chunk)
    {
        if (chunk.Data.Length == 0 || chunk.Data.Length > FileTransferProtocol.MaxChunkRawBytes)
        {
            throw new InvalidOperationException("Chunk payload exceeded the V3 raw payload budget.");
        }

        await HandleInboundPullChunksAsync(context, [(chunk.ChunkIndex, chunk.Data)]).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunkBatchV3Async(InboundTransferContext context, FileTransferChunkBatchFrameV3 batch)
    {
        var chunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(batch.DataSegments.Count);
        for (var index = 0; index < batch.DataSegments.Count; index++)
        {
            var segment = batch.DataSegments[index];
            if (segment.Length == 0 || segment.Length > FileTransferProtocol.MaxChunkRawBytes)
            {
                throw new InvalidOperationException("Chunk batch payload exceeded the V3 raw payload budget.");
            }

            chunks.Add((batch.StartChunkIndex + index, segment));
        }

        await HandleInboundPullChunksAsync(context, chunks).ConfigureAwait(false);
    }

    private async Task MaybeHandlePullV3RepairSchedulerAsync(InboundTransferContext context)
    {
        if (await MaybeHandlePullV3ProactiveFrontierRepairAsync(context).ConfigureAwait(false))
        {
            return;
        }

        await MaybeHandlePullV3RepairTimeoutAsync(context).ConfigureAwait(false);
    }

    private async Task MaybeSendInboundV3CreditKeepaliveGrantAsync(InboundTransferContext context)
    {
        if (!IsV3CreditKeepaliveGrantEnabled())
        {
            return;
        }

        FileTransferGrantWindowFrameV3? frame = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0 ||
                context.NextChunkIndex >= context.ChunkCount ||
                !IsFileOnlySparseReorderPolicyCandidateLocked(context) ||
                context.PullV3GrantedUntilExclusive <= context.NextChunkIndex)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (context.PullV3LastGrantSentUtc is not null &&
                now - context.PullV3LastGrantSentUtc.Value < TimeSpan.FromMilliseconds(V3CreditKeepaliveGrantIntervalMs))
            {
                return;
            }

            context.PullV3LastGrantSentUtc = now;
            context.RecentPullAckSentUtc.Enqueue(now);
            frame = new FileTransferGrantWindowFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                NextExpectedChunkIndex = context.NextChunkIndex,
                GrantedUntilChunkIndexExclusive = context.PullV3GrantedUntilExclusive,
                BytesCommitted = context.BytesTransferred,
            };

            LogInboundV3GrantWindowSummaryLocked(
                context,
                "credit_keepalive",
                ResolveInboundV3TargetWindowChunksLocked(context) * (long)Math.Max(1, context.ChunkSizeBytes),
                context.PullV3GrantedUntilExclusive,
                Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex),
                Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex),
                Math.Max(1, (int)Math.Ceiling(Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * (ResolveFileOnlySparseGrantLowWatermarkPercent() / 100D))),
                context.NextChunkIndex,
                "credit_keepalive",
                0,
                context.NextChunkIndex,
                "contiguous_frontier",
                "(none)",
                0,
                ResolveSparseCreditTopupBytes(IsSparseCreditDominanceModeEnabled()),
                context.NextChunkIndex,
                "credit_keepalive",
                IsSparseCreditDominanceModeEnabled() ? "Dominant" : "Current",
                sparseCreditHoldActive: false,
                sparseCreditEligible: true,
                proactiveRepairPressureState: "(none)",
                now);
            MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
        }

        await SendOrQueueInboundV3ReceiverFeedbackAsync(context, frame, "credit_keepalive").ConfigureAwait(false);
    }

    private async Task<bool> MaybeHandlePullV3ProactiveFrontierRepairAsync(InboundTransferContext context)
    {
        PullV3RepairRequestSelection? selection = null;
        PullV3ProactiveFrontierRepairSkip? skip = null;
        var gapStallAgeMs = 0L;
        var proactiveRepairPressureState = "(none)";
        var proactiveRepairAgeMs = 0L;
        var sameFrontierUnfilledMs = 0L;
        var grantPolicyAfterRepair = "(none)";
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0 ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return false;
        }

        var now = DateTimeOffset.UtcNow;
        ResetStaleProactiveFrontierRepairStateLocked(context, now);
        selection = BuildPullV3ProactiveFrontierRepairSelectionLocked(context, now, out skip, out gapStallAgeMs);
        if (selection is null && skip is null)
        {
                return false;
            }

            if (selection?.Frame is not null)
            {
                context.PullLastProgressUtc = now;
                context.PullV3LastRepairRequestSentUtc = now;
                context.PullV3LastProactiveFrontierRepairSentUtc = now;
                context.RecentPullRepairRequestSentUtc.Enqueue(now);
                var recentProactiveRepair = IsRecentProactiveFrontierRepairLocked(context, now);
                var repeatedProactiveRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
                    context,
                    recentProactiveRepair,
                    gapStallAgeMs,
                    now);
                proactiveRepairPressureState = ResolveProactiveFrontierRepairPressureStateLocked(
                    context,
                    recentProactiveRepair,
                    repeatedProactiveRepair,
                    gapStallAgeMs,
                    now);
                proactiveRepairAgeMs = GetProactiveFrontierRepairAgeMsLocked(context, now);
                sameFrontierUnfilledMs = GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now);
                grantPolicyAfterRepair = ResolveGrantPolicyAfterRepairLocked(context, ResolveFileOnlySparseReorderPolicyDecisionLocked(
                    context,
                    true,
                    recentProactiveRepair,
                    repeatedProactiveRepair,
                    proactiveRepairPressureState,
                    gapStallAgeMs),
                    gapStallAgeMs,
                    ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
                        context,
                        true,
                        recentProactiveRepair,
                        proactiveRepairPressureState));
            }
        }

        if (skip is not null)
        {
            if (skip.ShouldLog)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_frontier_gap_repair_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={skip.RepairRequestKey}; attempt_count={skip.AttemptCount}; reason={skip.Reason}; start_chunk_index={skip.StartChunkIndex}; requested_chunk_count={skip.RequestedChunkCount}; gap_stall_age_ms={skip.GapStallAgeMs}; late_arrival_distance={skip.LateArrivalDistance}; highest_received_chunk_index={skip.HighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={skip.GrantedUntilChunkIndexExclusive}; granted_window_bytes={skip.GrantedWindowBytes}; min_gap_ms={skip.MinGapMs}; repeat_ms={skip.RepeatMs}; max_repair_chunks={skip.MaxRepairChunks}; proactive_repair_pressure_state={skip.ProactiveRepairPressureState}; proactive_repair_age_ms={skip.ProactiveRepairAgeMs}; same_frontier_unfilled_ms={skip.SameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={skip.GrantPolicyAfterRepair}");
                if (string.Equals(skip.Reason, "duplicate_recent", StringComparison.Ordinal))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_frontier_gap_repair_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={skip.RepairRequestKey}; attempt_count={skip.AttemptCount}; reason={skip.Reason}; start_chunk_index={skip.StartChunkIndex}; requested_chunk_count={skip.RequestedChunkCount}; gap_stall_age_ms={skip.GapStallAgeMs}; late_arrival_distance={skip.LateArrivalDistance}; highest_received_chunk_index={skip.HighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={skip.GrantedUntilChunkIndexExclusive}; granted_window_bytes={skip.GrantedWindowBytes}; proactive_repair_pressure_state={skip.ProactiveRepairPressureState}; proactive_repair_age_ms={skip.ProactiveRepairAgeMs}; same_frontier_unfilled_ms={skip.SameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={skip.GrantPolicyAfterRepair}");
                }
            }

            return skip.ConsumesScheduler;
        }

        var repairSelection = selection;
        var repairFrame = repairSelection?.Frame;
        if (repairSelection is null || repairFrame is null)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_frontier_gap_repair_eligible; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSelection.RepairRequestKey}; attempt_count={context.PullV3ConsecutiveProactiveFrontierRepairCount}; range_count={repairSelection.RangeCount}; start_chunk_index={repairSelection.FirstStartChunkIndex}; requested_chunk_count={repairSelection.RequestedChunkCount}; last_end_chunk_exclusive={repairSelection.LastEndChunkExclusive}; gap_stall_age_ms={gapStallAgeMs}; late_arrival_distance={repairSelection.LateArrivalDistance}; highest_received_chunk_index={repairSelection.HighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={repairSelection.GrantedUntilChunkIndexExclusive}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}; min_gap_ms={ResolveProactiveFrontierRepairMinGapMs()}; repeat_ms={ResolveProactiveFrontierRepairRepeatMs()}; max_repair_chunks={ResolveProactiveFrontierRepairChunkCount()}; proactive_repair_pressure_state={proactiveRepairPressureState}; proactive_repair_age_ms={proactiveRepairAgeMs}; same_frontier_unfilled_ms={sameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={grantPolicyAfterRepair}");
        if (!await SendOrQueueInboundV3ReceiverFeedbackAsync(context, repairFrame, "proactive_frontier_gap_repair").ConfigureAwait(false))
        {
            return true;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_frontier_gap_repair_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSelection.RepairRequestKey}; attempt_count={context.PullV3ConsecutiveProactiveFrontierRepairCount}; range_count={repairSelection.RangeCount}; start_chunk_index={repairSelection.FirstStartChunkIndex}; requested_chunk_count={repairSelection.RequestedChunkCount}; last_end_chunk_exclusive={repairSelection.LastEndChunkExclusive}; gap_stall_age_ms={gapStallAgeMs}; late_arrival_distance={repairSelection.LateArrivalDistance}; highest_received_chunk_index={repairSelection.HighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={repairSelection.GrantedUntilChunkIndexExclusive}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}; reason=proactive_frontier_gap; min_gap_ms={ResolveProactiveFrontierRepairMinGapMs()}; repeat_ms={ResolveProactiveFrontierRepairRepeatMs()}; max_repair_chunks={ResolveProactiveFrontierRepairChunkCount()}; proactive_repair_pressure_state={proactiveRepairPressureState}; proactive_repair_age_ms={proactiveRepairAgeMs}; same_frontier_unfilled_ms={sameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={grantPolicyAfterRepair}");

        await SendInboundGrantWindowV3Async(context, forceGrant: true).ConfigureAwait(false);
        return true;
    }

    private async Task MaybeHandlePullV3RepairTimeoutAsync(InboundTransferContext context)
    {
        PullV3RepairRequestSelection? selection = null;
        string? suppressionReason = null;
        int suppressionRangeCount = 0;
        int suppressionRequestedChunkCount = 0;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0 ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
            var lastProgressUtc = context.PullLastProgressUtc ?? context.MetadataAwaitingSinceUtc ?? now;
            if (now - lastProgressUtc < TimeSpan.FromMilliseconds(GetPullSessionRequestTimeoutMs(context)))
            {
                return;
            }

            selection = BuildPullV3RepairRequestSelectionLocked(context, now, out suppressionReason, out suppressionRangeCount, out suppressionRequestedChunkCount);
            if (selection is null)
            {
                return;
            }

            if (selection.Frame is not null)
            {
                if (context.PullV3ConservativeStartupActive)
                {
                    context.PullV3FirstRepairOrTimeoutBeforeStartupExit = true;
                }

                context.PullLastProgressUtc = now;
                context.PullV3LastRepairRequestSentUtc = now;
                context.RecentPullRepairRequestSentUtc.Enqueue(now);
            }
        }

        if (selection.Frame is null)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_repair_request_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={suppressionReason ?? "unknown"}; range_count={suppressionRangeCount}; requested_chunk_count={suppressionRequestedChunkCount}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={context.PullV3GrantedUntilExclusive}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}");
            return;
        }

        if (!await SendOrQueueInboundV3ReceiverFeedbackAsync(context, selection.Frame, "repair_timeout").ConfigureAwait(false))
        {
            return;
        }

        if (selection.Frame is FileTransferRepairRequestSetFrameV3)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_repair_set_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; range_count={selection.RangeCount}; requested_chunk_count={selection.RequestedChunkCount}; first_start_chunk_index={selection.FirstStartChunkIndex}; last_end_chunk_exclusive={selection.LastEndChunkExclusive}; next_chunk_index={selection.NextChunkIndex}; highest_received_chunk_index={selection.HighestReceivedChunkIndex}; granted_until_chunk_index_exclusive={selection.GrantedUntilChunkIndexExclusive}; pending_chunk_count={selection.PendingChunkCount}; pending_bytes={selection.PendingBytes}; late_arrival_distance={selection.LateArrivalDistance}; reason=timeout");
        }

        await SendInboundGrantWindowV3Async(context, forceGrant: true).ConfigureAwait(false);
    }

    private PullV3RepairRequestSelection? BuildPullV3ProactiveFrontierRepairSelectionLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        out PullV3ProactiveFrontierRepairSkip? skip,
        out long gapStallAgeMs)
    {
        skip = null;
        gapStallAgeMs = 0;

        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return null;
        }

        ResetStaleProactiveFrontierRepairStateLocked(context, now);
        gapStallAgeMs = GetInboundV3CurrentGapStallAgeMsLocked(context, now);
        if (context.PullHighestReceivedChunkIndex < context.NextChunkIndex + PullV3ProactiveFrontierRepairMinLateDistance ||
            IsInboundV3ChunkPresentOrPendingLocked(context, context.NextChunkIndex))
        {
            return null;
        }

        var minGapMs = ResolveProactiveFrontierRepairMinGapMs();
        var repeatMs = ResolveProactiveFrontierRepairRepeatMs();
        var maxRepairChunks = ResolveProactiveFrontierRepairChunkCount();
        if (!IsV3ProactiveGapRepairEnabled())
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "disabled", gapStallAgeMs, 0, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        if (context.ReceiverBufferPressureActive)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "receiver_buffer_pressure", gapStallAgeMs, 0, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        if (context.PullSessionDegraded)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "session_degraded", gapStallAgeMs, 0, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        if (context.PullTimeoutStreak > 0)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "timeout", gapStallAgeMs, 0, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        if (gapStallAgeMs < minGapMs)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "gap_age_below_min", gapStallAgeMs, 0, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        var ranges = BuildProactiveFrontierRepairRangesLocked(context, maxRepairChunks);
        var requestedChunkCount = ranges.Sum(static range => range.RequestedChunkCount);

        if (requestedChunkCount <= 0)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "empty_missing_range", gapStallAgeMs, requestedChunkCount, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: false);
            return null;
        }

        var fingerprint = CreateRepairRangesFingerprint(ranges);
        var duplicateRecent =
            context.PullV3LastProactiveFrontierRepairStartChunkIndex == context.NextChunkIndex &&
            string.Equals(context.PullV3LastProactiveFrontierRepairFingerprint, fingerprint, StringComparison.Ordinal) &&
            context.PullV3LastProactiveFrontierRepairSentUtc is not null &&
            now - context.PullV3LastProactiveFrontierRepairSentUtc.Value < TimeSpan.FromMilliseconds(repeatMs);
        if (duplicateRecent)
        {
            skip = CreateProactiveFrontierRepairSkipLocked(context, now, "duplicate_recent", gapStallAgeMs, requestedChunkCount, minGapMs, repeatMs, maxRepairChunks, consumesScheduler: true);
            return null;
        }

        context.PullV3ConsecutiveProactiveFrontierRepairCount =
            context.PullV3LastProactiveFrontierRepairStartChunkIndex == context.NextChunkIndex
                ? Math.Max(1, context.PullV3ConsecutiveProactiveFrontierRepairCount + 1)
                : 1;
        context.PullV3LastProactiveFrontierRepairStartChunkIndex = context.NextChunkIndex;
        context.PullV3LastProactiveFrontierRepairRequestedChunkCount = requestedChunkCount;
        context.PullV3LastProactiveFrontierRepairHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullV3LastProactiveFrontierRepairFingerprint = fingerprint;
        context.PullV3LastProactiveFrontierRepairRequestKey = fingerprint;

        FileTransferDataFrameV2 repairFrame;
        if (ranges.Count == 1)
        {
            var onlyRange = ranges[0];
            repairFrame = new FileTransferRepairRequestFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                StartChunkIndex = onlyRange.StartChunkIndex,
                RequestedChunkCount = onlyRange.RequestedChunkCount,
            };
        }
        else
        {
            repairFrame = new FileTransferRepairRequestSetFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Ranges = ranges,
            };
        }

        var firstStart = ranges[0].StartChunkIndex;
        var lastEndExclusive = ranges[^1].StartChunkIndex + ranges[^1].RequestedChunkCount;
        return new PullV3RepairRequestSelection(
            repairFrame,
            ranges.Count,
            requestedChunkCount,
            firstStart,
            lastEndExclusive,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex,
            context.PullV3GrantedUntilExclusive,
            GetReceiverPendingChunkCountLocked(context),
            context.BufferedBytes,
            context.PullLateArrivalDistance,
            fingerprint);
    }

    private PullV3ProactiveFrontierRepairSkip? CreateProactiveFrontierRepairSkipLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        string reason,
        long gapStallAgeMs,
        int requestedChunkCount,
        int minGapMs,
        int repeatMs,
        int maxRepairChunks,
        bool consumesScheduler)
    {
        if (string.Equals(context.PullV3LastProactiveFrontierRepairSkipReason, reason, StringComparison.Ordinal) &&
            context.PullV3LastProactiveFrontierRepairSkipStartChunkIndex == context.NextChunkIndex &&
            context.PullV3LastProactiveFrontierRepairSkipLogUtc is not null &&
            now - context.PullV3LastProactiveFrontierRepairSkipLogUtc.Value < TimeSpan.FromMilliseconds(PullV3PressureStateSuppressionMs))
        {
            var repeatedProactiveRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
                context,
                IsRecentProactiveFrontierRepairLocked(context, now),
                gapStallAgeMs,
                now);
            var pressureState = ResolveProactiveFrontierRepairPressureStateLocked(
                context,
                IsRecentProactiveFrontierRepairLocked(context, now),
                repeatedProactiveRepair,
                gapStallAgeMs,
                now);
            return new PullV3ProactiveFrontierRepairSkip(
                reason,
                context.NextChunkIndex,
                requestedChunkCount,
                CreateRepairRequestKey(context.NextChunkIndex, requestedChunkCount),
                context.PullV3ConsecutiveProactiveFrontierRepairCount,
                gapStallAgeMs,
                context.PullLateArrivalDistance,
                context.PullHighestReceivedChunkIndex,
                context.PullV3GrantedUntilExclusive,
                GetInboundV3GrantedWindowBytesLocked(context),
                minGapMs,
                repeatMs,
                maxRepairChunks,
                pressureState,
                GetProactiveFrontierRepairAgeMsLocked(context, now),
                GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now),
                ResolveGrantPolicyAfterRepairLocked(context, ResolveFileOnlySparseReorderPolicyDecisionLocked(
                    context,
                    context.PullV3LastRepairRequestSentUtc is not null && now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs),
                    IsRecentProactiveFrontierRepairLocked(context, now),
                    repeatedProactiveRepair,
                    pressureState,
                    gapStallAgeMs),
                    gapStallAgeMs,
                    ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
                        context,
                        context.PullV3LastRepairRequestSentUtc is not null && now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs),
                        IsRecentProactiveFrontierRepairLocked(context, now),
                        pressureState)),
                consumesScheduler,
                ShouldLog: false);
        }

        context.PullV3LastProactiveFrontierRepairSkipReason = reason;
        context.PullV3LastProactiveFrontierRepairSkipStartChunkIndex = context.NextChunkIndex;
        context.PullV3LastProactiveFrontierRepairSkipLogUtc = now;
        var recentProactiveRepair = IsRecentProactiveFrontierRepairLocked(context, now);
        var repeatedRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(context, recentProactiveRepair, gapStallAgeMs, now);
        var proactiveRepairPressureState = ResolveProactiveFrontierRepairPressureStateLocked(
            context,
            recentProactiveRepair,
            repeatedRepair,
            gapStallAgeMs,
            now);
        return new PullV3ProactiveFrontierRepairSkip(
            reason,
            context.NextChunkIndex,
            requestedChunkCount,
            CreateRepairRequestKey(context.NextChunkIndex, requestedChunkCount),
            context.PullV3ConsecutiveProactiveFrontierRepairCount,
            gapStallAgeMs,
            context.PullLateArrivalDistance,
            context.PullHighestReceivedChunkIndex,
            context.PullV3GrantedUntilExclusive,
            GetInboundV3GrantedWindowBytesLocked(context),
            minGapMs,
            repeatMs,
            maxRepairChunks,
            proactiveRepairPressureState,
            GetProactiveFrontierRepairAgeMsLocked(context, now),
            GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now),
            ResolveGrantPolicyAfterRepairLocked(context, ResolveFileOnlySparseReorderPolicyDecisionLocked(
                context,
                context.PullV3LastRepairRequestSentUtc is not null && now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs),
                recentProactiveRepair,
                repeatedRepair,
                proactiveRepairPressureState,
                gapStallAgeMs),
                gapStallAgeMs,
                ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
                    context,
                    context.PullV3LastRepairRequestSentUtc is not null && now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs),
                    recentProactiveRepair,
                    proactiveRepairPressureState)),
            consumesScheduler,
            ShouldLog: true);
    }

    private PullV3RepairRequestSelection? BuildPullV3RepairRequestSelectionLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        out string? suppressionReason,
        out int suppressionRangeCount,
        out int suppressionRequestedChunkCount)
    {
        suppressionReason = null;
        suppressionRangeCount = 0;
        suppressionRequestedChunkCount = 0;

        var ranges = BuildMissingRepairRangesLocked(context);
        if (ranges.Count == 0)
        {
            suppressionReason = "empty";
            return new PullV3RepairRequestSelection(null, 0, 0, context.NextChunkIndex, context.NextChunkIndex, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullV3GrantedUntilExclusive, GetReceiverPendingChunkCountLocked(context), context.BufferedBytes, context.PullLateArrivalDistance, CreateRepairRequestKey(context.NextChunkIndex, 0));
        }

        var requestedChunkCount = ranges.Sum(static range => range.RequestedChunkCount);
        suppressionRangeCount = ranges.Count;
        suppressionRequestedChunkCount = requestedChunkCount;

        if (ranges.Count > 1)
        {
            var fingerprint = string.Join(",", ranges.Select(static range => $"{range.StartChunkIndex}:{range.RequestedChunkCount}"));
            var duplicateRecent =
                string.Equals(context.PullV3LastRepairRequestFingerprint, fingerprint, StringComparison.Ordinal) &&
                context.PullV3LastRepairRequestFingerprintUtc is not null &&
                now - context.PullV3LastRepairRequestFingerprintUtc.Value < TimeSpan.FromMilliseconds(PullV3RepairSetRepeatMinIntervalMs) &&
                context.PullV3LastRepairRequestNextChunkIndex == context.NextChunkIndex &&
                context.PullV3LastRepairRequestHighestReceivedChunkIndex == context.PullHighestReceivedChunkIndex;
            if (duplicateRecent)
            {
                suppressionReason = "duplicate_recent";
                return new PullV3RepairRequestSelection(null, ranges.Count, requestedChunkCount, ranges[0].StartChunkIndex, ranges[^1].StartChunkIndex + ranges[^1].RequestedChunkCount, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullV3GrantedUntilExclusive, GetReceiverPendingChunkCountLocked(context), context.BufferedBytes, context.PullLateArrivalDistance, fingerprint);
            }

            context.PullV3LastRepairRequestFingerprint = fingerprint;
            context.PullV3LastRepairRequestFingerprintUtc = now;
            context.PullV3LastRepairRequestNextChunkIndex = context.NextChunkIndex;
            context.PullV3LastRepairRequestHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            var repairSet = new FileTransferRepairRequestSetFrameV3
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Ranges = ranges,
            };
            return new PullV3RepairRequestSelection(repairSet, ranges.Count, requestedChunkCount, ranges[0].StartChunkIndex, ranges[^1].StartChunkIndex + ranges[^1].RequestedChunkCount, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullV3GrantedUntilExclusive, GetReceiverPendingChunkCountLocked(context), context.BufferedBytes, context.PullLateArrivalDistance, fingerprint);
        }

        var onlyRange = ranges[0];
        var repair = new FileTransferRepairRequestFrameV3
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = onlyRange.StartChunkIndex,
            RequestedChunkCount = Math.Min(onlyRange.RequestedChunkCount, Math.Max(1, context.ChunkCount - onlyRange.StartChunkIndex)),
        };
        return new PullV3RepairRequestSelection(repair, 1, repair.RequestedChunkCount, repair.StartChunkIndex, repair.StartChunkIndex + repair.RequestedChunkCount, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullV3GrantedUntilExclusive, GetReceiverPendingChunkCountLocked(context), context.BufferedBytes, context.PullLateArrivalDistance, CreateRepairRequestKey(repair.StartChunkIndex, repair.RequestedChunkCount));
    }

    private static List<FileTransferRepairRangeV3> BuildMissingRepairRangesLocked(InboundTransferContext context)
    {
        if (context.PullHighestReceivedChunkIndex < context.NextChunkIndex && !HasAnyInboundV3ChunkPresentOrPendingAheadLocked(context))
        {
            return
            [
                new FileTransferRepairRangeV3
                {
                    StartChunkIndex = context.NextChunkIndex,
                    RequestedChunkCount = Math.Min(PullV3RepairRequestChunkCount, context.ChunkCount - context.NextChunkIndex),
                },
            ];
        }

        var horizon = Math.Min(
            context.ChunkCount,
            Math.Min(
                Math.Max(context.PullV3GrantedUntilExclusive, context.PullHighestReceivedChunkIndex + 1),
                context.NextChunkIndex + PullV3RepairSetScanHorizonChunks));
        if (horizon <= context.NextChunkIndex)
        {
            return [];
        }

        var ranges = new List<FileTransferRepairRangeV3>(FileTransferProtocol.MaxRepairSetRangesV3);
        var remainingChunks = FileTransferProtocol.MaxRepairSetChunksV3;
        int? currentStart = null;
        var currentCount = 0;

        void FlushCurrent()
        {
            if (currentStart is null || currentCount <= 0 || remainingChunks <= 0 || ranges.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
            {
                currentStart = null;
                currentCount = 0;
                return;
            }

            var requestedCount = Math.Min(currentCount, remainingChunks);
            ranges.Add(new FileTransferRepairRangeV3
            {
                StartChunkIndex = currentStart.Value,
                RequestedChunkCount = requestedCount,
            });
            remainingChunks -= requestedCount;
            currentStart = null;
            currentCount = 0;
        }

        for (var chunkIndex = context.NextChunkIndex; chunkIndex < horizon; chunkIndex++)
        {
            if (IsInboundV3ChunkPresentOrPendingLocked(context, chunkIndex))
            {
                FlushCurrent();
                if (remainingChunks <= 0 || ranges.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
                {
                    break;
                }

                continue;
            }

            currentStart ??= chunkIndex;
            currentCount++;
        }

        FlushCurrent();
        return ranges;
    }

    private static List<FileTransferRepairRangeV3> BuildProactiveFrontierRepairRangesLocked(
        InboundTransferContext context,
        int maxRepairChunks)
    {
        var cappedRepairChunks = Math.Clamp(maxRepairChunks, PullV3ProactiveFrontierRepairChunkCountMin, PullV3ProactiveFrontierRepairChunkCountMax);
        var horizon = Math.Min(
            context.ChunkCount,
            Math.Min(
                Math.Max(context.PullV3GrantedUntilExclusive, context.PullHighestReceivedChunkIndex + 1),
                context.NextChunkIndex + PullV3RepairSetScanHorizonChunks));
        if (horizon <= context.NextChunkIndex)
        {
            return [];
        }

        var ranges = new List<FileTransferRepairRangeV3>(FileTransferProtocol.MaxRepairSetRangesV3);
        var remainingChunks = Math.Min(cappedRepairChunks, FileTransferProtocol.MaxRepairSetChunksV3);
        int? currentStart = null;
        var currentCount = 0;

        void FlushCurrent()
        {
            if (currentStart is null || currentCount <= 0 || remainingChunks <= 0 || ranges.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
            {
                currentStart = null;
                currentCount = 0;
                return;
            }

            var requestedCount = Math.Min(currentCount, remainingChunks);
            ranges.Add(new FileTransferRepairRangeV3
            {
                StartChunkIndex = currentStart.Value,
                RequestedChunkCount = requestedCount,
            });
            remainingChunks -= requestedCount;
            currentStart = null;
            currentCount = 0;
        }

        for (var chunkIndex = context.NextChunkIndex; chunkIndex < horizon; chunkIndex++)
        {
            if (IsInboundV3ChunkPresentOrPendingLocked(context, chunkIndex))
            {
                FlushCurrent();
                if (remainingChunks <= 0 || ranges.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
                {
                    break;
                }

                continue;
            }

            currentStart ??= chunkIndex;
            currentCount++;
        }

        FlushCurrent();
        return ranges;
    }

    private static string CreateRepairRequestKey(int startChunkIndex, int requestedChunkCount)
        => $"{Math.Max(0, startChunkIndex)}:{Math.Max(0, requestedChunkCount)}";

    private static string CreateRepairRangesFingerprint(IReadOnlyList<FileTransferRepairRangeV3> ranges)
        => ranges.Count == 0
            ? "(none)"
            : string.Join(",", ranges.Select(static range => CreateRepairRequestKey(range.StartChunkIndex, range.RequestedChunkCount)));

    private static List<FileTransferRepairRangeV3> NormalizeRepairRangesForSend(
        IReadOnlyList<FileTransferRepairRangeV3> ranges,
        int chunkCount)
    {
        if (ranges.Count == 0 || chunkCount <= 0)
        {
            return [];
        }

        var sorted = ranges
            .Where(static range => range.StartChunkIndex >= 0 && range.RequestedChunkCount > 0)
            .Select(range => (Start: range.StartChunkIndex, EndExclusive: (int)Math.Min(chunkCount, (long)range.StartChunkIndex + range.RequestedChunkCount)))
            .Where(static range => range.EndExclusive > range.Start)
            .OrderBy(static range => range.Start)
            .ThenBy(static range => range.EndExclusive)
            .ToList();
        if (sorted.Count == 0)
        {
            return [];
        }

        var normalized = new List<FileTransferRepairRangeV3>(Math.Min(sorted.Count, FileTransferProtocol.MaxRepairSetRangesV3));
        var remainingChunks = FileTransferProtocol.MaxRepairSetChunksV3;
        var currentStart = sorted[0].Start;
        var currentEndExclusive = sorted[0].EndExclusive;
        for (var index = 1; index < sorted.Count; index++)
        {
            var candidate = sorted[index];
            if (candidate.Start <= currentEndExclusive)
            {
                currentEndExclusive = Math.Max(currentEndExclusive, candidate.EndExclusive);
                continue;
            }

            AddNormalizedRange(normalized, currentStart, currentEndExclusive, ref remainingChunks);
            if (remainingChunks <= 0 || normalized.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
            {
                return normalized;
            }

            currentStart = candidate.Start;
            currentEndExclusive = candidate.EndExclusive;
        }

        AddNormalizedRange(normalized, currentStart, currentEndExclusive, ref remainingChunks);
        return normalized;
    }

    private static void AddNormalizedRange(List<FileTransferRepairRangeV3> ranges, int startChunkIndex, int endChunkExclusive, ref int remainingChunks)
    {
        if (remainingChunks <= 0 || ranges.Count >= FileTransferProtocol.MaxRepairSetRangesV3)
        {
            return;
        }

        var requestedChunkCount = Math.Min(endChunkExclusive - startChunkIndex, remainingChunks);
        if (requestedChunkCount <= 0)
        {
            return;
        }

        ranges.Add(new FileTransferRepairRangeV3
        {
            StartChunkIndex = startChunkIndex,
            RequestedChunkCount = requestedChunkCount,
        });
        remainingChunks -= requestedChunkCount;
    }

    private async Task SendInboundGrantWindowV3Async(InboundTransferContext context, bool forceGrant)
    {
        FileTransferDataFrameV2? frame = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.ChunkCount <= 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _ = UpdateInboundV3WindowProfileLocked(context, now);
            var previousGrantedUntilExclusive = context.PullV3GrantedUntilExclusive;
            var gapStallAgeMs = GetInboundV3CurrentGapStallAgeMsLocked(context, now);
            var recentRepair = context.PullV3LastRepairRequestSentUtc is not null &&
                               now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs);
            var recentProactiveFrontierRepair = IsRecentProactiveFrontierRepairLocked(context, now);
            var repeatedProactiveFrontierRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
                context,
                recentProactiveFrontierRepair,
                gapStallAgeMs,
                now);
            var proactiveRepairPressureState = ResolveProactiveFrontierRepairPressureStateLocked(
                context,
                recentProactiveFrontierRepair,
                repeatedProactiveFrontierRepair,
                gapStallAgeMs,
                now);
            var recentRepairForCadence = ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
                context,
                recentRepair,
                recentProactiveFrontierRepair,
                proactiveRepairPressureState);
            var fileOnlySparseGrantCadence =
                IsFileOnlySparseReorderPolicyCandidateLocked(context) &&
                !context.ReceiverBufferPressureActive &&
                context.PullTimeoutStreak == 0 &&
                !recentRepairForCadence;
            var sparseCreditAccountingEnabled = IsSparseCreditAccountingSparseBaseEnabled();
            var sparseCreditDominanceEnabled = IsSparseCreditDominanceModeEnabled();
            var sparseAheadLimitBytes = ResolveSparseAheadGrantMaxBytes(sparseCreditDominanceEnabled);
            var dominantSparseCredit = ResolveInboundV3DominantSparseCreditBaseLocked(
                context,
                fileOnlySparseGrantCadence,
                sparseCreditAccountingEnabled,
                sparseCreditDominanceEnabled,
                sparseAheadLimitBytes,
                gapStallAgeMs,
                now);
            int grantBaseChunkIndex;
            string grantBaseReason;
            long sparseAheadBytes;
            int creditBaseChunkIndex;
            string creditBaseReason;
            string sparseCreditBlockReason;
            if (sparseCreditDominanceEnabled && sparseCreditAccountingEnabled)
            {
                grantBaseChunkIndex = dominantSparseCredit.BaseChunkIndex;
                grantBaseReason = dominantSparseCredit.BaseReason;
                sparseAheadBytes = dominantSparseCredit.SparseAheadBytes;
                creditBaseChunkIndex = dominantSparseCredit.UseSparseBase
                    ? dominantSparseCredit.BaseChunkIndex
                    : context.NextChunkIndex;
                creditBaseReason = dominantSparseCredit.UseSparseBase ? "sparse_base" : "contiguous_frontier";
                sparseCreditBlockReason = dominantSparseCredit.BlockReason;
            }
            else
            {
                grantBaseChunkIndex = ResolveInboundV3GrantBaseChunkIndexLocked(
                    context,
                    fileOnlySparseGrantCadence,
                    gapStallAgeMs,
                    sparseAheadLimitBytes,
                    out grantBaseReason,
                    out sparseAheadBytes);
                creditBaseChunkIndex = ResolveInboundV3CreditBaseChunkIndexLocked(
                    context,
                    sparseCreditAccountingEnabled,
                    grantBaseChunkIndex,
                    grantBaseReason,
                    fileOnlySparseGrantCadence,
                    out creditBaseReason,
                    out sparseCreditBlockReason);
            }
            var targetWindowChunks = ResolveInboundV3TargetWindowChunksLocked(context);
            var targetWindowBytes = targetWindowChunks * (long)Math.Max(1, context.ChunkSizeBytes);
            var targetGrantedUntilExclusive = Math.Min(context.ChunkCount, grantBaseChunkIndex + targetWindowChunks);
            targetGrantedUntilExclusive = ApplyReceiverBufferGrantClampLocked(context, targetGrantedUntilExclusive);
            targetGrantedUntilExclusive = Math.Max(previousGrantedUntilExclusive, targetGrantedUntilExclusive);
            var currentCredit = Math.Max(0, context.PullV3GrantedUntilExclusive - creditBaseChunkIndex);
            var desiredCredit = Math.Max(0, targetGrantedUntilExclusive - creditBaseChunkIndex);
            var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
            var sparseCreditTopupBytes = ResolveSparseCreditTopupBytes(sparseCreditDominanceEnabled);
            var sparseCreditAdvanceBytes = string.Equals(creditBaseReason, "sparse_base", StringComparison.Ordinal)
                ? Math.Max(0, creditBaseChunkIndex - context.PullV3LastGrantCreditBaseChunkIndex) * (long)chunkSizeBytes
                : 0L;
            var shouldGrantForSparseCreditTopup =
                sparseCreditTopupBytes > 0 &&
                sparseCreditAdvanceBytes >= sparseCreditTopupBytes &&
                targetGrantedUntilExclusive > previousGrantedUntilExclusive;
            var shouldClampGrant = targetGrantedUntilExclusive < previousGrantedUntilExclusive;
            var targetWindowChanged = targetWindowBytes != context.PullV3LastGrantTargetWindowBytes && desiredCredit > currentCredit;
            var grantLowWatermarkCredit = fileOnlySparseGrantCadence
                ? Math.Max(1, (int)Math.Ceiling(desiredCredit * (ResolveFileOnlySparseGrantLowWatermarkPercent() / 100D)))
                : Math.Max(1, desiredCredit / PullV3GrantLowWatermarkDivisor);
            var shouldGrant = forceGrant || shouldClampGrant || targetWindowChanged || shouldGrantForSparseCreditTopup || currentCredit <= grantLowWatermarkCredit;
            var ackCoalesceDelayMs = fileOnlySparseGrantCadence
                ? ResolveFileOnlySparseGrantCoalesceMs()
                : PullV3HealthyAckCoalesceDelayMs;
            var ackDebtReady = context.PullAckDebtBytes >= PullV3HealthyAckThresholdBytes;
            var ackCoalesceOpen = context.PullLastAckSentUtc is null ||
                                  DateTimeOffset.UtcNow - context.PullLastAckSentUtc.Value >= TimeSpan.FromMilliseconds(ackCoalesceDelayMs);
            var shouldAckOnly =
                !shouldGrant &&
                ackDebtReady &&
                ackCoalesceOpen;

            var lowWatermarkReached = currentCredit <= grantLowWatermarkCredit;
            var ackCoalesceBlocked = !shouldGrant && ackDebtReady && !ackCoalesceOpen;
            var sameGrantTarget = targetGrantedUntilExclusive <= previousGrantedUntilExclusive;

            LogInboundV3GrantDecisionSummaryLocked(
                context,
                shouldGrant,
                shouldAckOnly,
                forceGrant,
                shouldClampGrant,
                targetWindowChanged,
                shouldGrantForSparseCreditTopup,
                lowWatermarkReached,
                ackCoalesceBlocked,
                sameGrantTarget,
                targetWindowBytes,
                targetGrantedUntilExclusive,
                currentCredit,
                desiredCredit,
                grantLowWatermarkCredit,
                grantBaseChunkIndex,
                grantBaseReason,
                creditBaseChunkIndex,
                creditBaseReason,
                sparseCreditBlockReason,
                sparseCreditAdvanceBytes,
                sparseCreditTopupBytes,
                now);

            if (!shouldGrant && !shouldAckOnly)
            {
                MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
                return;
            }

            if (shouldGrant)
            {
                context.PullV3GrantedUntilExclusive = targetGrantedUntilExclusive;
                context.PullV3LastGrantSentUtc = DateTimeOffset.UtcNow;
                context.PullV3LastGrantTargetWindowBytes = targetWindowBytes;
                context.PullV3LastGrantCreditBaseChunkIndex = creditBaseChunkIndex;
                frame = new FileTransferGrantWindowFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    GrantedUntilChunkIndexExclusive = context.PullV3GrantedUntilExclusive,
                    BytesCommitted = context.BytesTransferred,
                };
            }
            else
            {
                frame = new FileTransferAckProgressFrameV3
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    BytesCommitted = context.BytesTransferred,
                };
            }

            context.PullLastAckSentUtc = now;
            context.PullLastAckSentChunkIndex = context.NextChunkIndex;
            context.PullAckDebtChunks = 0;
            context.PullAckDebtBytes = 0;
            context.RecentPullAckSentUtc.Enqueue(now);
            LogInboundV3GrantWindowSummaryLocked(
                context,
                shouldGrant
                    ? forceGrant
                        ? "force"
                        : shouldClampGrant
                            ? "clamp"
                            : targetWindowChanged
                                ? "target_changed"
                                : shouldGrantForSparseCreditTopup
                                    ? "sparse_credit_topup"
                                : "low_watermark"
                    : "ack_only",
                targetWindowBytes,
                targetGrantedUntilExclusive,
                currentCredit,
                desiredCredit,
                grantLowWatermarkCredit,
                grantBaseChunkIndex,
                grantBaseReason,
                sparseAheadBytes,
                creditBaseChunkIndex,
                creditBaseReason,
                sparseCreditBlockReason,
                sparseCreditAdvanceBytes,
                sparseCreditTopupBytes,
                grantBaseChunkIndex,
                grantBaseReason,
                sparseCreditDominanceEnabled ? "Dominant" : "Current",
                dominantSparseCredit.HoldActive,
                dominantSparseCredit.Eligible,
                proactiveRepairPressureState,
                now);
            MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
        }

        await SendOrQueueInboundV3ReceiverFeedbackAsync(
            context,
            frame,
            frame is FileTransferGrantWindowFrameV3 ? "grant_window" : "ack_progress").ConfigureAwait(false);
    }

    private int ApplyReceiverBufferGrantClampLocked(InboundTransferContext context, int targetGrantedUntilExclusive)
    {
        if (!context.ReceiverBufferPressureActive && context.BufferedBytes < ReceiverBufferSoftLimitBytes)
        {
            return targetGrantedUntilExclusive;
        }

        var previousTarget = targetGrantedUntilExclusive;
        int clampedTarget;
        string reason;
        if (context.BufferedBytes >= ReceiverBufferSevereLimitBytes)
        {
            clampedTarget = context.NextChunkIndex;
            reason = "severe_limit";
        }
        else
        {
            var limitedWindowChunks = Math.Max(1, (int)Math.Ceiling((double)PullV3HealthyLimitedTargetInFlightBytes / Math.Max(1, context.ChunkSizeBytes)));
            clampedTarget = Math.Min(targetGrantedUntilExclusive, Math.Min(context.ChunkCount, context.NextChunkIndex + limitedWindowChunks));
            reason = "soft_limit";
        }

        if (clampedTarget < previousTarget)
        {
            var now = DateTimeOffset.UtcNow;
            if (context.LastReceiverGrantClampLogUtc is null ||
                now - context.LastReceiverGrantClampLogUtc.Value >= TimeSpan.FromMilliseconds(PullV3PressureStateSuppressionMs))
            {
                context.LastReceiverGrantClampLogUtc = now;
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_receiver_grant_clamped_for_buffer; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}; previous_target_granted_until_exclusive={previousTarget}; clamped_target_granted_until_exclusive={clampedTarget}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}; soft_limit_bytes={ReceiverBufferSoftLimitBytes}; severe_limit_bytes={ReceiverBufferSevereLimitBytes}");
            }
        }

        return clampedTarget;
    }

    private int ResolveInboundV3GrantBaseChunkIndexLocked(
        InboundTransferContext context,
        bool fileOnlySparseGrantCadence,
        long gapStallAgeMs,
        int sparseAheadLimitBytes,
        out string grantBaseReason,
        out long sparseAheadBytes)
    {
        grantBaseReason = "contiguous_frontier";
        sparseAheadBytes = 0;
        if (!fileOnlySparseGrantCadence)
        {
            return context.NextChunkIndex;
        }

        if (gapStallAgeMs >= ResolveSparseAheadGrantGapStallLimitMs())
        {
            grantBaseReason = "gap_stall";
            return context.NextChunkIndex;
        }

        if (sparseAheadLimitBytes <= 0)
        {
            grantBaseReason = "sparse_ahead_disabled";
            return context.NextChunkIndex;
        }

        var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
        var sparseAheadLimitChunks = Math.Max(1, (int)Math.Ceiling((double)sparseAheadLimitBytes / chunkSizeBytes));
        var durableSparseFrontier = Math.Max(context.NextChunkIndex, Math.Min(context.ChunkCount, context.PullHighestReceivedChunkIndex + 1));
        var sparseAheadBase = Math.Min(durableSparseFrontier, Math.Min(context.ChunkCount, context.NextChunkIndex + sparseAheadLimitChunks));
        if (sparseAheadBase <= context.NextChunkIndex)
        {
            return context.NextChunkIndex;
        }

        grantBaseReason = "sparse_ahead";
        sparseAheadBytes = (long)(sparseAheadBase - context.NextChunkIndex) * chunkSizeBytes;
        return sparseAheadBase;
    }

    private InboundV3SparseCreditBaseResolution ResolveInboundV3DominantSparseCreditBaseLocked(
        InboundTransferContext context,
        bool fileOnlySparseGrantCadence,
        bool sparseCreditAccountingEnabled,
        bool sparseCreditDominanceEnabled,
        int sparseAheadLimitBytes,
        long gapStallAgeMs,
        DateTimeOffset now)
    {
        var blockReason = ResolveSparseCreditDominanceBlockReasonLocked(
            context,
            sparseCreditAccountingEnabled,
            sparseCreditDominanceEnabled,
            fileOnlySparseGrantCadence,
            gapStallAgeMs,
            sparseAheadLimitBytes);
        var eligible = string.Equals(blockReason, "(none)", StringComparison.Ordinal);
        if (!eligible)
        {
            return new InboundV3SparseCreditBaseResolution(
                context.NextChunkIndex,
                "contiguous_frontier",
                0,
                false,
                false,
                false,
                blockReason);
        }

        var durableSparseBase = ResolveDurableSparseWrittenCreditBaseLocked(context, sparseAheadLimitBytes);
        var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
        if (durableSparseBase > context.NextChunkIndex)
        {
            context.PullV3LastSparseCreditEligibleUtc = now;
            context.PullV3LastSparseCreditBaseChunkIndex = durableSparseBase;
            return new InboundV3SparseCreditBaseResolution(
                durableSparseBase,
                "sparse_ahead",
                (long)(durableSparseBase - context.NextChunkIndex) * chunkSizeBytes,
                true,
                false,
                true,
                "(none)");
        }

        var holdMs = ResolveSparseCreditHoldMs();
        var holdActive =
            holdMs > 0 &&
            context.PullV3LastSparseCreditEligibleUtc is not null &&
            now - context.PullV3LastSparseCreditEligibleUtc.Value <= TimeSpan.FromMilliseconds(holdMs) &&
            context.PullV3LastSparseCreditBaseChunkIndex > context.NextChunkIndex;
        if (holdActive)
        {
            var heldBase = Math.Min(context.ChunkCount, context.PullV3LastSparseCreditBaseChunkIndex);
            return new InboundV3SparseCreditBaseResolution(
                heldBase,
                "sparse_ahead",
                (long)(heldBase - context.NextChunkIndex) * chunkSizeBytes,
                true,
                true,
                true,
                "(none)");
        }

        return new InboundV3SparseCreditBaseResolution(
            context.NextChunkIndex,
            "contiguous_frontier",
            0,
            false,
            false,
            true,
            "no_sparse_ahead");
    }

    private string ResolveSparseCreditDominanceBlockReasonLocked(
        InboundTransferContext context,
        bool sparseCreditAccountingEnabled,
        bool sparseCreditDominanceEnabled,
        bool fileOnlySparseGrantCadence,
        long gapStallAgeMs,
        int sparseAheadLimitBytes)
    {
        if (!sparseCreditDominanceEnabled)
        {
            return "mode_current";
        }

        if (!sparseCreditAccountingEnabled)
        {
            return "accounting_disabled";
        }

        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return "not_file_only_sparse";
        }

        if (context.ReceiverBufferPressureActive)
        {
            return "receiver_buffer_pressure";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (!fileOnlySparseGrantCadence)
        {
            return "repair_pressure";
        }

        if (gapStallAgeMs >= ResolveSparseAheadGrantGapStallLimitMs())
        {
            return "gap_stall";
        }

        if (sparseAheadLimitBytes <= 0)
        {
            return "sparse_ahead_disabled";
        }

        return "(none)";
    }

    private static int ResolveDurableSparseWrittenCreditBaseLocked(InboundTransferContext context, int sparseAheadLimitBytes)
    {
        if (!context.ReceiverSparseWriteActive ||
            context.ReceiverSparseChunksWritten is null ||
            sparseAheadLimitBytes <= 0)
        {
            return context.NextChunkIndex;
        }

        var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
        var sparseAheadLimitChunks = Math.Max(1, (int)Math.Ceiling((double)sparseAheadLimitBytes / chunkSizeBytes));
        var endExclusive = Math.Min(
            context.ChunkCount,
            Math.Min(context.ReceiverSparseChunksWritten.Length, context.NextChunkIndex + sparseAheadLimitChunks));
        var highestSparseWrittenChunkIndex = -1;
        for (var chunkIndex = context.NextChunkIndex; chunkIndex < endExclusive; chunkIndex++)
        {
            if (context.ReceiverSparseChunksWritten[chunkIndex])
            {
                highestSparseWrittenChunkIndex = chunkIndex;
            }
        }

        return highestSparseWrittenChunkIndex >= context.NextChunkIndex
            ? highestSparseWrittenChunkIndex + 1
            : context.NextChunkIndex;
    }

    private int ResolveInboundV3CreditBaseChunkIndexLocked(
        InboundTransferContext context,
        bool sparseCreditAccountingEnabled,
        int grantBaseChunkIndex,
        string grantBaseReason,
        bool fileOnlySparseGrantCadence,
        out string creditBaseReason,
        out string sparseCreditBlockReason)
    {
        creditBaseReason = "contiguous_frontier";
        sparseCreditBlockReason = ResolveSparseCreditBlockReasonLocked(
            context,
            sparseCreditAccountingEnabled,
            fileOnlySparseGrantCadence,
            grantBaseReason);
        if (!sparseCreditAccountingEnabled ||
            !fileOnlySparseGrantCadence ||
            !string.Equals(grantBaseReason, "sparse_ahead", StringComparison.Ordinal) ||
            grantBaseChunkIndex <= context.NextChunkIndex)
        {
            return context.NextChunkIndex;
        }

        creditBaseReason = "sparse_base";
        sparseCreditBlockReason = "(none)";
        return grantBaseChunkIndex;
    }

    private string ResolveSparseCreditBlockReasonLocked(
        InboundTransferContext context,
        bool sparseCreditAccountingEnabled,
        bool fileOnlySparseGrantCadence,
        string grantBaseReason)
    {
        if (!sparseCreditAccountingEnabled)
        {
            return "accounting_disabled";
        }

        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return "not_file_only_sparse";
        }

        if (context.ReceiverBufferPressureActive)
        {
            return "receiver_buffer_pressure";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (!fileOnlySparseGrantCadence)
        {
            return "repair_pressure";
        }

        return grantBaseReason switch
        {
            "gap_stall" => "gap_stall",
            "sparse_ahead_disabled" => "sparse_ahead_disabled",
            "sparse_ahead" => "(none)",
            _ => "no_sparse_ahead",
        };
    }

    private int ResolveInboundV3TargetWindowChunksLocked(InboundTransferContext context)
    {
        var fixedFileOnlyWindowBytes = ResolveFixedFileOnlyWindowBytes();
        var fixedFileOnlyWindowActive = fixedFileOnlyWindowBytes > 0 && IsFileOnlySparseReorderPolicyCandidateLocked(context);
        var healthyMaximumTargetBytes = fixedFileOnlyWindowActive
            ? fixedFileOnlyWindowBytes
            : IsFileOnlySparseReorderPolicyCandidateLocked(context)
            ? ResolveFileOnlySparseTargetWindowBytes()
            : PullV3HealthyMaximumTargetInFlightBytes;
        var targetBytes = context.PullV3ConservativeStartupActive
            ? context.PullV3ConservativeStartupDegradedActive
                ? PullV3ConservativeStartupDegradedTargetInFlightBytes
                : context.PullV3ConservativeStartupProbeActive
                    ? PullV3ConservativeStartupProbeTargetInFlightBytes
                : PullV3ConservativeStartupTargetInFlightBytes
            : sessionScreenShareDegraded || context.PullSessionDegraded
            ? PullV3DegradedTargetInFlightBytes
            : sessionScreenShareActive
                ? PullV3ScreenshareTargetInFlightBytes
                : context.PullV3LimitedWindowActive
                    ? PullV3HealthyLimitedTargetInFlightBytes
                : context.PullV3FileOnlySoftLimitedWindowActive
                    ? ResolveFileOnlySparseSoftLimitBytes()
                : context.PullV3ExpandedWindowActive
                    ? healthyMaximumTargetBytes
                    : PullV3HealthyTargetInFlightBytes;
        if (fixedFileOnlyWindowActive &&
            !context.PullSessionDegraded &&
            !context.ReceiverBufferPressureActive &&
            context.PullTimeoutStreak == 0 &&
            !context.PullV3LimitedWindowActive)
        {
            targetBytes = fixedFileOnlyWindowBytes;
        }

        targetBytes = Math.Min(targetBytes, healthyMaximumTargetBytes);
        return Math.Max(4, (int)Math.Ceiling((double)targetBytes / Math.Max(1, context.ChunkSizeBytes)));
    }

    private static int ResolveFixedFileOnlyWindowBytes()
        => ResolveIntegerEnvironmentVariable(
            V3FixedFileOnlyWindowBytesEnvironmentVariableName,
            PullV3FixedFileOnlyWindowBytesDefault,
            PullV3FixedFileOnlyWindowBytesMin,
            PullV3FixedFileOnlyWindowBytesMax);

    private static int ResolveFileOnlySparseTargetWindowBytes()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlyTargetWindowBytesEnvironmentVariableName,
            PullV3FileOnlySparseTargetInFlightBytesDefault,
            PullV3FileOnlySparseTargetInFlightBytesMin,
            PullV3FileOnlySparseTargetInFlightBytesMax);

    private static int ResolveFileOnlySparseGrantLowWatermarkPercent()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlyGrantLowWatermarkPercentEnvironmentVariableName,
            PullV3FileOnlySparseGrantLowWatermarkPercentDefault,
            PullV3FileOnlySparseGrantLowWatermarkPercentMin,
            PullV3FileOnlySparseGrantLowWatermarkPercentMax);

    private static int ResolveFileOnlySparseGrantCoalesceMs()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlyGrantCoalesceMsEnvironmentVariableName,
            PullV3FileOnlySparseGrantCoalesceMsDefault,
            PullV3FileOnlySparseGrantCoalesceMsMin,
            PullV3FileOnlySparseGrantCoalesceMsMax);

    private static int ResolveSparseAheadGrantMaxBytes(bool sparseCreditDominanceEnabled)
        => ResolveIntegerEnvironmentVariable(
            V3SparseAheadGrantMaxBytesEnvironmentVariableName,
            sparseCreditDominanceEnabled
                ? PullV3SparseAheadGrantMaxBytesDominantDefault
                : PullV3SparseAheadGrantMaxBytesCurrentDefault,
            0,
            PullV3SparseAheadGrantMaxBytesMax);

    private static bool IsSparseCreditDominanceModeEnabled()
    {
        var value = Environment.GetEnvironmentVariable(V3SparseCreditModeEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "Current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(normalized, "Dominant", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSparseCreditAccountingSparseBaseEnabled()
    {
        var value = Environment.GetEnvironmentVariable(V3SparseCreditAccountingEnvironmentVariableName);
        return !string.Equals(value?.Trim(), "ContiguousFrontier", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveSparseCreditTopupBytes(bool sparseCreditDominanceEnabled)
        => ResolveIntegerEnvironmentVariable(
            V3SparseCreditTopupBytesEnvironmentVariableName,
            sparseCreditDominanceEnabled
                ? PullV3SparseCreditTopupBytesDominantDefault
                : PullV3SparseCreditTopupBytesCurrentDefault,
            PullV3SparseCreditTopupBytesMin,
            PullV3SparseCreditTopupBytesMax);

    private static int ResolveSparseCreditHoldMs()
        => ResolveIntegerEnvironmentVariable(
            V3SparseCreditHoldMsEnvironmentVariableName,
            PullV3SparseCreditHoldMsDefault,
            PullV3SparseCreditHoldMsMin,
            PullV3SparseCreditHoldMsMax);

    private static int ResolveFileOnlySparseSoftLimitBytes()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlySoftLimitBytesEnvironmentVariableName,
            PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesDefault,
            PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesMin,
            PullV3HealthyFileOnlySoftLimitedTargetInFlightBytesMax);

    private static int ResolveFileOnlySparseSoftLimitedReorderThreshold()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlySoftLimitReorderThresholdEnvironmentVariableName,
            PullV3FileOnlySparseSoftLimitedReorderThresholdDefault,
            PullV3FileOnlySparseSoftLimitedReorderThresholdMin,
            PullV3FileOnlySparseSoftLimitedReorderThresholdMax);

    private static int ResolveFileOnlySparseSoftGapStallMs()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlySoftGapStallMsEnvironmentVariableName,
            PullV3FileOnlySparseSoftGapStallMsDefault,
            PullV3FileOnlySparseSoftGapStallMsMin,
            PullV3FileOnlySparseSoftGapStallMsMax);

    private static int ResolveFileOnlySparseSoftRecoveryMs()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlySoftRecoveryMsEnvironmentVariableName,
            PullV3FileOnlySparseSoftLimitedRecoveryHoldMsDefault,
            PullV3FileOnlySparseSoftLimitedRecoveryHoldMsMin,
            PullV3FileOnlySparseSoftLimitedRecoveryHoldMsMax);

    private static int ResolveFileOnlySparseLimitedRecoveryMs()
        => ResolveIntegerEnvironmentVariable(
            V3FileOnlyLimitedRecoveryMsEnvironmentVariableName,
            PullV3FileOnlySparseLimitedRecoveryHoldMsDefault,
            PullV3FileOnlySparseLimitedRecoveryHoldMsMin,
            PullV3FileOnlySparseLimitedRecoveryHoldMsMax);

    private static int ResolveSparseAheadGrantGapStallLimitMs()
        => ResolveIntegerEnvironmentVariable(
            V3SparseAheadGapStallLimitMsEnvironmentVariableName,
            PullV3SparseAheadGrantGapStallLimitMsDefault,
            PullV3SparseAheadGrantGapStallLimitMsMin,
            PullV3SparseAheadGrantGapStallLimitMsMax);

    private static int ResolveIntegerEnvironmentVariable(string name, int defaultValue, int minimumValue, int maximumValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, minimumValue, maximumValue);
        }

        return defaultValue;
    }

    private string UpdateInboundV3WindowProfileLocked(InboundTransferContext context, DateTimeOffset now)
    {
        var previousProfile = ResolveInboundV3ProfileName(context);
        var transitionReason = "unchanged";

        string Complete(string reason)
        {
            var updatedProfile = ResolveInboundV3ProfileName(context);
            LogInboundV3ProfileChanged(context, previousProfile, updatedProfile, reason);
            return updatedProfile;
        }

        TrimRecentEvents(context.RecentPullRepairRequestSentUtc, now);
        ResetStaleProactiveFrontierRepairStateLocked(context, now);

        if (sessionScreenShareActive || sessionScreenShareDegraded)
        {
            ExitConservativeStartupLocked(context, now, "screenshare_pressure");
            context.PullV3ExpandedWindowActive = false;
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            context.PullV3LimitedWindowActive = false;
            context.PullV3CleanSinceUtc = null;
            context.PullV3AdverseSinceUtc ??= now;
            return Complete("screenshare_pressure");
        }

        var recentRepair = context.PullV3LastRepairRequestSentUtc is not null &&
                           now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs);
        var recentProactiveFrontierRepair = IsRecentProactiveFrontierRepairLocked(context, now);
        var gapStallAgeMs = GetInboundV3CurrentGapStallAgeMsLocked(context, now);
        var repeatedProactiveFrontierRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
            context,
            recentProactiveFrontierRepair,
            gapStallAgeMs,
            now);
        var proactiveRepairPressureState = ResolveProactiveFrontierRepairPressureStateLocked(
            context,
            recentProactiveFrontierRepair,
            repeatedProactiveFrontierRepair,
            gapStallAgeMs,
            now);
        var recentRepairPressure = ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
            context,
            recentRepair,
            recentProactiveFrontierRepair,
            proactiveRepairPressureState);
        var fileOnlyReorderDecision = ResolveFileOnlySparseReorderPolicyDecisionLocked(
            context,
            recentRepair,
            recentProactiveFrontierRepair,
            repeatedProactiveFrontierRepair,
            proactiveRepairPressureState,
            gapStallAgeMs);
        var fileOnlySparseReorderCandidate = IsFileOnlySparseReorderPolicyCandidateLocked(context);
        var fixedFileOnlyWindowActive = ResolveFixedFileOnlyWindowBytes() > 0 && fileOnlySparseReorderCandidate;
        if (fixedFileOnlyWindowActive &&
            fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.SoftLimit)
        {
            fileOnlyReorderDecision = InboundV3ReorderPolicyDecision.Tolerate;
            transitionReason = "fixed_file_only_window";
        }

        var genericLimitedRecoveryCleanEligible =
            context.PullV3LimitedWindowActive &&
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded &&
            !context.PullSessionDegraded &&
            context.PullTimeoutStreak == 0 &&
            !recentRepairPressure &&
            context.PullLateArrivalDistance < PullV3HighReorderDistanceThreshold;
        var fileOnlySparseHardLimitedPressureActive = HasFileOnlySparseHardLimitedPressureLocked(
            context,
            gapStallAgeMs,
            recentRepairPressure,
            out var fileOnlySparseLimitedRecoveryBlockReason);
        var fileOnlySparseLimitedRecoveryCleanEligible =
            context.PullV3LimitedWindowActive &&
            fileOnlySparseReorderCandidate &&
            !fileOnlySparseHardLimitedPressureActive;
        var startupReorderAdverse = fileOnlySparseReorderCandidate
            ? fileOnlyReorderDecision is InboundV3ReorderPolicyDecision.SoftLimit or InboundV3ReorderPolicyDecision.Limit
            : context.PullLateArrivalDistance >= PullV3HighReorderDistanceThreshold;
        var startupAdverse =
            context.ReceiverBufferPressureActive ||
            context.PullTimeoutStreak > 0 ||
            startupReorderAdverse ||
            recentRepairPressure;
        var startupHealthyEligible =
            context.PullV3ConservativeStartupActive &&
            !context.PullSessionDegraded &&
            !context.ReceiverBufferPressureActive &&
            !recentRepairPressure &&
            context.PullTimeoutStreak == 0 &&
            context.PullLateArrivalDistance < PullLateArrivalDistanceThreshold &&
            context.BytesTransferred >= PullV3ConservativeStartupStepUpProgressBytesThreshold;

        if (context.PullV3ConservativeStartupActive &&
            fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.Tolerate)
        {
            ExitConservativeStartupLocked(context, now, "file_only_reorder_tolerated");
            context.PullV3CleanSinceUtc = now;
            context.PullLastProfileAdjustmentUtc = now;
            transitionReason = "file_only_reorder_tolerated";
        }
        else if (context.PullV3ConservativeStartupActive &&
                 fileOnlyReorderDecision is InboundV3ReorderPolicyDecision.SoftLimit or InboundV3ReorderPolicyDecision.Limit)
        {
            ExitConservativeStartupLocked(
                context,
                now,
                ResolveFileOnlySparseReorderPolicyReason(context, fileOnlyReorderDecision, gapStallAgeMs, recentRepairPressure, recentProactiveFrontierRepair, repeatedProactiveFrontierRepair, proactiveRepairPressureState));
            context.PullV3CleanSinceUtc = null;
            context.PullV3AdverseSinceUtc ??= now;
            transitionReason = ResolveFileOnlySparseReorderPolicyReason(context, fileOnlyReorderDecision, gapStallAgeMs, recentRepairPressure, recentProactiveFrontierRepair, repeatedProactiveFrontierRepair, proactiveRepairPressureState);
        }
        else if (context.PullV3ConservativeStartupActive)
        {
            if (startupAdverse)
            {
                transitionReason = context.PullV3ConservativeStartupDegradedActive
                    ? transitionReason
                    : "startup_adverse";
                context.PullV3ConservativeStartupDegradedActive = true;
                context.PullV3ConservativeStartupProbeActive = false;
                context.PullV3FirstRepairOrTimeoutBeforeStartupExit =
                    context.PullV3FirstRepairOrTimeoutBeforeStartupExit ||
                    recentRepair ||
                    context.PullTimeoutStreak > 0;
                context.PullV3AdverseSinceUtc ??= now;
                context.PullV3CleanSinceUtc = null;
            }
            else
            {
                context.PullV3AdverseSinceUtc = null;
                if (!context.PullV3ConservativeStartupDegradedActive &&
                    !context.PullV3ConservativeStartupProbeActive &&
                    context.BytesTransferred >= PullV3ConservativeStartupProbeProgressBytesThreshold &&
                    !context.ReceiverBufferPressureActive &&
                    !recentRepairPressure &&
                    context.PullTimeoutStreak == 0 &&
                    context.PullLateArrivalDistance < PullLateArrivalDistanceThreshold)
                {
                    context.PullV3CleanSinceUtc ??= now;
                    if (now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3ConservativeStartupProbeHoldMs))
                    {
                        context.PullV3ConservativeStartupProbeActive = true;
                        context.PullV3CleanSinceUtc = now;
                        context.PullLastProfileAdjustmentUtc = now;
                        transitionReason = "startup_probe";
                    }
                }
                else if (startupHealthyEligible)
                {
                    context.PullV3CleanSinceUtc ??= now;
                    if (now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3ConservativeStartupStepUpHoldMs))
                    {
                        ExitConservativeStartupLocked(context, now, "startup_fast_clean");
                        context.PullV3CleanSinceUtc = now;
                        context.PullLastProfileAdjustmentUtc = now;
                        transitionReason = "startup_fast_clean";
                    }
                }
                else if (!context.PullV3ConservativeStartupProbeActive)
                {
                    context.PullV3CleanSinceUtc = null;
                }
            }

            if (context.PullV3ConservativeStartupActive)
            {
                return Complete(transitionReason);
            }
        }

        LogInboundV3ReorderPolicyDecisionLocked(
            context,
            fileOnlyReorderDecision,
            gapStallAgeMs,
            recentRepair,
            recentRepairPressure,
            repeatedProactiveFrontierRepair,
            proactiveRepairPressureState,
            now);

        if (fixedFileOnlyWindowActive &&
            fileOnlyReorderDecision != InboundV3ReorderPolicyDecision.Limit)
        {
            ExitConservativeStartupLocked(context, now, "fixed_file_only_window");
            context.PullV3ExpandedWindowActive = true;
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            context.PullV3LimitedWindowActive = false;
            context.PullV3CleanSinceUtc = now;
            context.PullV3AdverseSinceUtc = null;
            context.PullLastProfileAdjustmentUtc = now;
            return Complete("fixed_file_only_window");
        }

        if (fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.Limit &&
            fileOnlySparseReorderCandidate &&
            !fileOnlySparseHardLimitedPressureActive)
        {
            fileOnlyReorderDecision = InboundV3ReorderPolicyDecision.Tolerate;
            transitionReason = "file_only_limited_pressure_cleared";
        }

        if (fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.Limit)
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            if (!context.PullV3LimitedWindowActive ||
                context.PullLastProfileAdjustmentUtc is null ||
                now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs))
            {
                context.PullV3LimitedWindowActive = true;
                context.PullLastProfileAdjustmentUtc = now;
                transitionReason = ResolveFileOnlySparseReorderPolicyReason(context, fileOnlyReorderDecision, gapStallAgeMs, recentRepairPressure, recentProactiveFrontierRepair, repeatedProactiveFrontierRepair, proactiveRepairPressureState);
            }

            context.PullV3CleanSinceUtc = null;
            context.PullV3AdverseSinceUtc ??= now;
            return Complete(transitionReason);
        }

        if (fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.SoftLimit)
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullV3LimitedWindowActive = false;
            if (!context.PullV3FileOnlySoftLimitedWindowActive ||
                context.PullLastProfileAdjustmentUtc is null ||
                now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs))
            {
                context.PullV3FileOnlySoftLimitedWindowActive = true;
                context.PullLastProfileAdjustmentUtc = now;
                transitionReason = ResolveFileOnlySparseReorderPolicyReason(context, fileOnlyReorderDecision, gapStallAgeMs, recentRepairPressure, recentProactiveFrontierRepair, repeatedProactiveFrontierRepair, proactiveRepairPressureState);
            }

            context.PullV3CleanSinceUtc = null;
            context.PullV3AdverseSinceUtc ??= now;
            return Complete(transitionReason);
        }

        if (context.PullV3FileOnlySoftLimitedWindowActive &&
            fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.Conservative)
        {
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            context.PullLastProfileAdjustmentUtc = now;
            transitionReason = "file_only_reorder_policy_conservative";
        }
        else if (context.PullV3FileOnlySoftLimitedWindowActive)
        {
            var softRecoveryEligible =
                IsFileOnlySparseReorderPolicyCandidateLocked(context) &&
                context.PullTimeoutStreak == 0 &&
                !recentRepairPressure &&
                !context.ReceiverBufferPressureActive &&
                gapStallAgeMs < ResolveProactiveFrontierRepairMinGapMs();
            if (softRecoveryEligible)
            {
                context.PullV3CleanSinceUtc ??= now;
                if (now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(ResolveFileOnlySparseSoftRecoveryMs()))
                {
                    context.PullV3FileOnlySoftLimitedWindowActive = false;
                    context.PullLastProfileAdjustmentUtc = now;
                    transitionReason = "file_only_soft_limited_recovered";
                }
            }
            else
            {
                context.PullV3CleanSinceUtc = null;
            }

            if (context.PullV3FileOnlySoftLimitedWindowActive)
            {
                return Complete(transitionReason);
            }
        }

        var tolerateFileOnlySparseReorder = fileOnlyReorderDecision == InboundV3ReorderPolicyDecision.Tolerate;
        var severeReorder = context.PullLateArrivalDistance >= PullV3FileOnlySparseToleratedReorderThreshold &&
                            !tolerateFileOnlySparseReorder;
        var reorderLimited =
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded &&
            !context.PullSessionDegraded &&
            context.PullTimeoutStreak == 0 &&
            !context.ReceiverBufferPressureActive &&
            !recentRepairPressure &&
            !tolerateFileOnlySparseReorder &&
            context.PullLateArrivalDistance >= PullV3LimitedReorderDistanceThreshold;
        var healthyEligible =
            !sessionScreenShareActive &&
            !sessionScreenShareDegraded &&
            !context.PullSessionDegraded &&
            !context.PullV3LimitedWindowActive &&
            !context.PullV3FileOnlySoftLimitedWindowActive &&
            !context.ReceiverBufferPressureActive &&
            context.BytesTransferred >= PullV3StepUpProgressBytesThreshold &&
            context.PullTimeoutStreak == 0 &&
            !recentRepairPressure &&
            context.PullLateArrivalDistance < PullV3HighReorderDistanceThreshold;
        var adverse =
            sessionScreenShareActive ||
            sessionScreenShareDegraded ||
            context.PullSessionDegraded ||
            context.ReceiverBufferPressureActive ||
            context.PullTimeoutStreak >= 2 ||
            recentRepairPressure ||
            (!tolerateFileOnlySparseReorder && context.PullLateArrivalDistance >= PullV3HighReorderDistanceThreshold);

        if (healthyEligible ||
            genericLimitedRecoveryCleanEligible ||
            fileOnlySparseLimitedRecoveryCleanEligible)
        {
            context.PullV3CleanSinceUtc ??= now;
        }
        else
        {
            context.PullV3CleanSinceUtc = null;
        }

        if (adverse)
        {
            context.PullV3AdverseSinceUtc ??= now;
        }
        else
        {
            context.PullV3AdverseSinceUtc = null;
        }

        if (!context.PullV3LimitedWindowActive &&
            reorderLimited &&
            (severeReorder ||
             (context.PullV3AdverseSinceUtc is not null &&
              now - context.PullV3AdverseSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3LimitedStepDownHoldMs) &&
              (context.PullLastProfileAdjustmentUtc is null ||
               now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))))
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullV3FileOnlySoftLimitedWindowActive = false;
            context.PullV3LimitedWindowActive = true;
            context.PullLastProfileAdjustmentUtc = now;
            transitionReason = severeReorder ? "severe_reorder" : "high_reorder";
        }
        else if (context.PullV3LimitedWindowActive)
        {
            var limitedRecoveryEligible =
                genericLimitedRecoveryCleanEligible ||
                fileOnlySparseLimitedRecoveryCleanEligible;
            var limitedRecoveryHoldMs = fileOnlySparseLimitedRecoveryCleanEligible
                ? ResolveFileOnlySparseLimitedRecoveryMs()
                : PullV3LimitedRecoveryHoldMs;
            var profileCooldownSatisfied =
                fileOnlySparseLimitedRecoveryCleanEligible ||
                context.PullLastProfileAdjustmentUtc is null ||
                now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs);
            if (limitedRecoveryEligible &&
                context.PullV3CleanSinceUtc is not null &&
                now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(limitedRecoveryHoldMs) &&
                profileCooldownSatisfied)
            {
                context.PullV3LimitedWindowActive = false;
                context.PullV3FileOnlySoftLimitedWindowActive = false;
                context.PullV3ExpandedWindowActive = fileOnlySparseLimitedRecoveryCleanEligible || context.PullV3ExpandedWindowActive;
                context.PullV3AdverseSinceUtc = null;
                context.PullLastProfileAdjustmentUtc = now;
                transitionReason = fileOnlySparseLimitedRecoveryCleanEligible
                    ? "file_only_sparse_limited_recovered"
                    : "limited_recovered";
            }
        }

        if (!context.PullV3ExpandedWindowActive &&
            healthyEligible &&
            context.PullV3CleanSinceUtc is not null &&
            now - context.PullV3CleanSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3HealthyStepUpHoldMs) &&
            (context.PullLastProfileAdjustmentUtc is null ||
             now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
        {
            context.PullV3ExpandedWindowActive = true;
            context.PullLastProfileAdjustmentUtc = now;
            transitionReason = "healthy_expanded";
        }
        else if (context.PullV3ExpandedWindowActive &&
                 adverse &&
                 context.PullV3AdverseSinceUtc is not null &&
                 now - context.PullV3AdverseSinceUtc.Value >= TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs) &&
                 (context.PullLastProfileAdjustmentUtc is null ||
                  now - context.PullLastProfileAdjustmentUtc.Value >= TimeSpan.FromMilliseconds(PullV3ProfileAdjustmentCooldownMs)))
        {
            context.PullV3ExpandedWindowActive = false;
            context.PullLastProfileAdjustmentUtc = now;
            transitionReason = context.PullLateArrivalDistance >= PullV3HighReorderDistanceThreshold
                ? "high_reorder"
                : "adverse_pressure";
        }

        return Complete(transitionReason);
    }

    private bool IsFileOnlySparseReorderPolicyCandidateLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV3 &&
           context.ReceiverSparseWriteActive &&
           context.TransportProfileKind == FileTransferTransportProfileKind.ConservativeNknStartup &&
           !sessionScreenShareActive &&
           !sessionScreenShareDegraded &&
           !context.PullSessionDegraded &&
           IsFileOnlySparseReorderPolicySparseTolerant();

    private static bool IsFileOnlySparseReorderPolicySparseTolerant()
    {
        var value = Environment.GetEnvironmentVariable(V3FileOnlyReorderPolicyEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "Conservative", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(normalized, "SparseTolerant", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsV3ProactiveGapRepairEnabled()
    {
        var value = Environment.GetEnvironmentVariable(V3ProactiveGapRepairEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return !string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static V3ProactiveRepairPressureMode ResolveV3ProactiveRepairPressureMode()
    {
        var value = Environment.GetEnvironmentVariable(V3ProactiveRepairPressureModeEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return V3ProactiveRepairPressureMode.PhaseNCompatible;
        }

        var normalized = value.Trim();
        if (string.Equals(normalized, "BenignUntilProven", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase))
        {
            return V3ProactiveRepairPressureMode.BenignUntilProven;
        }

        if (string.Equals(normalized, "ImmediatePressure", StringComparison.OrdinalIgnoreCase))
        {
            return V3ProactiveRepairPressureMode.ImmediatePressure;
        }

        if (string.Equals(normalized, "PhaseNCompatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
        {
            return V3ProactiveRepairPressureMode.PhaseNCompatible;
        }

        return V3ProactiveRepairPressureMode.PhaseNCompatible;
    }

    private static int ResolveProactiveRepairGraceMs()
        => ResolveIntegerEnvironmentVariable(
            V3ProactiveRepairGraceMsEnvironmentVariableName,
            PullV3ProactiveRepairGraceMsDefault,
            PullV3ProactiveRepairGraceMsMin,
            PullV3ProactiveRepairGraceMsMax);

    private static int ResolveProactiveFrontierRepairMinGapMs()
        => ResolveIntegerEnvironmentVariable(
            V3FrontierRepairMinGapMsEnvironmentVariableName,
            PullV3ProactiveFrontierRepairMinGapAgeMsDefault,
            PullV3ProactiveFrontierRepairMinGapAgeMsMin,
            PullV3ProactiveFrontierRepairMinGapAgeMsMax);

    private static int ResolveProactiveFrontierRepairRepeatMs()
        => ResolveIntegerEnvironmentVariable(
            V3FrontierRepairRepeatMsEnvironmentVariableName,
            PullV3ProactiveFrontierRepairRepeatMinIntervalMsDefault,
            PullV3ProactiveFrontierRepairRepeatMinIntervalMsMin,
            PullV3ProactiveFrontierRepairRepeatMinIntervalMsMax);

    private static int ResolveProactiveFrontierRepairChunkCount()
        => ResolveIntegerEnvironmentVariable(
            V3FrontierRepairChunksEnvironmentVariableName,
            PullV3ProactiveFrontierRepairChunkCountDefault,
            PullV3ProactiveFrontierRepairChunkCountMin,
            PullV3ProactiveFrontierRepairChunkCountMax);

    private static long GetInboundV3CurrentGapStallAgeMsLocked(InboundTransferContext context, DateTimeOffset now)
        => UpdateInboundGapStallTrackingLocked(context, now);

    private static bool IsRecentProactiveFrontierRepairLocked(InboundTransferContext context, DateTimeOffset now)
        => context.PullV3LastProactiveFrontierRepairSentUtc is not null &&
           now - context.PullV3LastProactiveFrontierRepairSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs);

    private static void ResetStaleProactiveFrontierRepairStateLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.PullV3LastProactiveFrontierRepairSentUtc is null ||
            context.PullV3LastProactiveFrontierRepairStartChunkIndex < 0)
        {
            return;
        }

        var repairedStartChunkIndex = context.PullV3LastProactiveFrontierRepairStartChunkIndex;
        var resetReason = context.NextChunkIndex > repairedStartChunkIndex
            ? "frontier_advanced"
            : context.NextChunkIndex == repairedStartChunkIndex &&
              IsInboundV3ChunkPresentOrPendingLocked(context, repairedStartChunkIndex)
                ? "frontier_chunk_present"
                : null;
        if (resetReason is null)
        {
            return;
        }

        var repairAgeMs = GetProactiveFrontierRepairAgeMsLocked(context, now);
        var sameFrontierUnfilledMs = GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_proactive_frontier_repair_state_reset; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={context.PullV3LastProactiveFrontierRepairRequestKey ?? CreateRepairRequestKey(repairedStartChunkIndex, context.PullV3LastProactiveFrontierRepairRequestedChunkCount)}; reason={resetReason}; start_chunk_index={repairedStartChunkIndex}; requested_chunk_count={context.PullV3LastProactiveFrontierRepairRequestedChunkCount}; next_chunk_index={context.NextChunkIndex}; highest_received_at_repair={context.PullV3LastProactiveFrontierRepairHighestReceivedChunkIndex}; current_highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; proactive_repair_age_ms={repairAgeMs}; same_frontier_unfilled_ms={sameFrontierUnfilledMs}");

        if (context.PullV3LastRepairRequestSentUtc == context.PullV3LastProactiveFrontierRepairSentUtc)
        {
            context.PullV3LastRepairRequestSentUtc = null;
        }

        context.PullV3LastProactiveFrontierRepairSentUtc = null;
        context.PullV3LastProactiveFrontierRepairStartChunkIndex = -1;
        context.PullV3LastProactiveFrontierRepairRequestedChunkCount = 0;
        context.PullV3LastProactiveFrontierRepairHighestReceivedChunkIndex = -1;
        context.PullV3LastProactiveFrontierRepairRequestKey = null;
        context.PullV3LastProactiveFrontierRepairFingerprint = null;
        context.PullV3ConsecutiveProactiveFrontierRepairCount = 0;
        context.PullV3LastProactiveFrontierRepairSkipLogUtc = null;
        context.PullV3LastProactiveFrontierRepairSkipReason = null;
        context.PullV3LastProactiveFrontierRepairSkipStartChunkIndex = -1;
    }

    private static long GetProactiveFrontierRepairAgeMsLocked(InboundTransferContext context, DateTimeOffset now)
        => context.PullV3LastProactiveFrontierRepairSentUtc is null
            ? 0
            : (long)Math.Max(0, (now - context.PullV3LastProactiveFrontierRepairSentUtc.Value).TotalMilliseconds);

    private static long GetSameFrontierUnfilledProactiveRepairAgeMsLocked(InboundTransferContext context, DateTimeOffset now)
        => context.PullV3LastProactiveFrontierRepairStartChunkIndex == context.NextChunkIndex &&
           context.PullV3LastProactiveFrontierRepairSentUtc is not null &&
           !IsInboundV3ChunkPresentOrPendingLocked(context, context.NextChunkIndex)
            ? GetProactiveFrontierRepairAgeMsLocked(context, now)
            : 0;

    private static bool IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
        InboundTransferContext context,
        bool recentProactiveFrontierRepair,
        long gapStallAgeMs,
        DateTimeOffset now)
    {
        if (!recentProactiveFrontierRepair ||
            context.PullV3LastProactiveFrontierRepairStartChunkIndex != context.NextChunkIndex ||
            IsInboundV3ChunkPresentOrPendingLocked(context, context.NextChunkIndex))
        {
            return false;
        }

        if (gapStallAgeMs >= PullV3FileOnlySparseLimitedGapStallMs)
        {
            return true;
        }

        return ResolveV3ProactiveRepairPressureMode() switch
        {
            V3ProactiveRepairPressureMode.BenignUntilProven =>
                GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now) >= ResolveProactiveRepairGraceMs(),
            V3ProactiveRepairPressureMode.ImmediatePressure =>
                context.PullV3ConsecutiveProactiveFrontierRepairCount > 1,
            _ => false,
        };
    }

    private string ResolveProactiveFrontierRepairPressureStateLocked(
        InboundTransferContext context,
        bool recentProactiveFrontierRepair,
        bool repeatedProactiveFrontierRepair,
        long gapStallAgeMs,
        DateTimeOffset now)
    {
        if (!recentProactiveFrontierRepair)
        {
            return "(none)";
        }

        var pressureMode = ResolveV3ProactiveRepairPressureMode();
        if (pressureMode == V3ProactiveRepairPressureMode.ImmediatePressure)
        {
            return "immediate_pressure";
        }

        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return "not_file_only_sparse";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (context.ReceiverBufferPressureActive)
        {
            return "receiver_buffer_pressure";
        }

        if (gapStallAgeMs >= PullV3FileOnlySparseLimitedGapStallMs)
        {
            return "hard_gap_stall";
        }

        var sameFrontierUnfilledMs = GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now);
        if (pressureMode == V3ProactiveRepairPressureMode.PhaseNCompatible)
        {
            return "(none)";
        }

        if (sameFrontierUnfilledMs >= ResolveProactiveRepairGraceMs())
        {
            return repeatedProactiveFrontierRepair ? "repeated_unfilled" : "grace_expired";
        }

        return "benign_grace";
    }

    private static bool IsProactiveFrontierRepairPressureState(string state)
        => !string.Equals(state, "(none)", StringComparison.Ordinal) &&
           !string.Equals(state, "benign_grace", StringComparison.Ordinal);

    private string ResolveFileOnlySparseLimitedRecoveryBlockReasonLocked(
        InboundTransferContext context,
        long gapStallAgeMs,
        bool recentRepairPressure)
    {
        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return "not_file_only_sparse";
        }

        if (sessionScreenShareActive || sessionScreenShareDegraded)
        {
            return "screenshare_pressure";
        }

        if (context.PullSessionDegraded)
        {
            return "session_degraded";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (context.ReceiverBufferPressureActive)
        {
            return "receiver_buffer_pressure";
        }

        if (recentRepairPressure)
        {
            return "repair_pressure";
        }

        if (gapStallAgeMs >= PullV3FileOnlySparseLimitedGapStallMs)
        {
            return "old_frontier_gap";
        }

        return "(none)";
    }

    private bool HasFileOnlySparseHardLimitedPressureLocked(
        InboundTransferContext context,
        long gapStallAgeMs,
        bool recentRepairPressure,
        out string blockReason)
    {
        blockReason = ResolveFileOnlySparseLimitedRecoveryBlockReasonLocked(context, gapStallAgeMs, recentRepairPressure);
        return !string.Equals(blockReason, "(none)", StringComparison.Ordinal);
    }

    private string ResolveLimitedRecoveryBlockReasonLocked(InboundTransferContext context, DateTimeOffset now)
    {
        var gapStallAgeMs = GetInboundV3CurrentGapStallAgeMsLocked(context, now);
        var recentRepair = context.PullV3LastRepairRequestSentUtc is not null &&
                           now - context.PullV3LastRepairRequestSentUtc.Value < TimeSpan.FromMilliseconds(PullV3AdverseStepDownHoldMs);
        var recentProactiveFrontierRepair = IsRecentProactiveFrontierRepairLocked(context, now);
        var repeatedProactiveFrontierRepair = IsRepeatedOrUnfilledProactiveFrontierRepairLocked(
            context,
            recentProactiveFrontierRepair,
            gapStallAgeMs,
            now);
        var proactiveRepairPressureState = ResolveProactiveFrontierRepairPressureStateLocked(
            context,
            recentProactiveFrontierRepair,
            repeatedProactiveFrontierRepair,
            gapStallAgeMs,
            now);
        var recentRepairPressure = ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
            context,
            recentRepair,
            recentProactiveFrontierRepair,
            proactiveRepairPressureState);

        if (IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return ResolveFileOnlySparseLimitedRecoveryBlockReasonLocked(context, gapStallAgeMs, recentRepairPressure);
        }

        if (sessionScreenShareActive || sessionScreenShareDegraded)
        {
            return "screenshare_pressure";
        }

        if (context.PullSessionDegraded)
        {
            return "session_degraded";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (recentRepairPressure)
        {
            return "repair_pressure";
        }

        if (context.PullLateArrivalDistance >= PullV3HighReorderDistanceThreshold)
        {
            return "high_reorder";
        }

        return "(none)";
    }

    private bool ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
        InboundTransferContext context,
        bool recentRepair,
        bool recentProactiveFrontierRepair,
        string proactiveRepairPressureState)
    {
        if (!recentRepair)
        {
            return false;
        }

        return !recentProactiveFrontierRepair ||
               IsProactiveFrontierRepairPressureState(proactiveRepairPressureState);
    }

    private InboundV3ReorderPolicyDecision ResolveFileOnlySparseReorderPolicyDecisionLocked(
        InboundTransferContext context,
        bool recentRepair,
        bool recentProactiveFrontierRepair,
        bool repeatedProactiveFrontierRepair,
        string proactiveRepairPressureState,
        long gapStallAgeMs)
    {
        if (!IsFileOnlySparseReorderPolicyCandidateLocked(context))
        {
            return InboundV3ReorderPolicyDecision.Conservative;
        }

        if (context.ReceiverBufferPressureActive ||
            context.PullTimeoutStreak > 0 ||
            gapStallAgeMs >= PullV3FileOnlySparseLimitedGapStallMs)
        {
            return InboundV3ReorderPolicyDecision.Limit;
        }

        if (recentRepair &&
            ShouldTreatRecentRepairAsFileOnlySparsePressureLocked(
                context,
                recentRepair,
                recentProactiveFrontierRepair,
                proactiveRepairPressureState))
        {
            return InboundV3ReorderPolicyDecision.Limit;
        }

        if (context.PullLateArrivalDistance >= PullV3FileOnlySparseToleratedReorderThreshold ||
            context.PullLateArrivalDistance >= ResolveFileOnlySparseSoftLimitedReorderThreshold() ||
            gapStallAgeMs >= ResolveFileOnlySparseSoftGapStallMs())
        {
            return InboundV3ReorderPolicyDecision.Tolerate;
        }

        return InboundV3ReorderPolicyDecision.Normal;
    }

    private static string ResolveFileOnlySparseReorderPolicyReason(
        InboundTransferContext context,
        InboundV3ReorderPolicyDecision decision,
        long gapStallAgeMs,
        bool recentRepair,
        bool recentProactiveFrontierRepair,
        bool repeatedProactiveFrontierRepair,
        string proactiveRepairPressureState)
    {
        if (context.ReceiverBufferPressureActive)
        {
            return "receiver_buffer_pressure";
        }

        if (context.PullTimeoutStreak > 0)
        {
            return "timeout";
        }

        if (recentRepair)
        {
            if (recentProactiveFrontierRepair &&
                IsProactiveFrontierRepairPressureState(proactiveRepairPressureState))
            {
                return proactiveRepairPressureState switch
                {
                    "immediate_pressure" => "proactive_gap_immediate_pressure",
                    "hard_gap_stall" => "proactive_gap_hard_stall",
                    "grace_expired" => "proactive_gap_grace_expired",
                    "repeated_unfilled" => "proactive_gap_repeated",
                    _ => "proactive_gap_repair_pressure",
                };
            }

            if (recentProactiveFrontierRepair)
            {
                return repeatedProactiveFrontierRepair
                    ? "proactive_gap_repeated_in_grace"
                    : "proactive_gap_benign";
            }

            return "repair";
        }

        if (gapStallAgeMs >= PullV3FileOnlySparseLimitedGapStallMs)
        {
            return "file_only_gap_stall_limited";
        }

        if (gapStallAgeMs >= ResolveFileOnlySparseSoftGapStallMs())
        {
            return "file_only_gap_stall_soft_limited";
        }

        return decision == InboundV3ReorderPolicyDecision.SoftLimit
            ? "file_only_reorder_soft_limited"
            : "file_only_reorder_limited";
    }

    private void LogInboundV3ReorderPolicyDecisionLocked(
        InboundTransferContext context,
        InboundV3ReorderPolicyDecision decision,
        long gapStallAgeMs,
        bool recentRepair,
        bool recentRepairPressure,
        bool repeatedProactiveFrontierRepair,
        string proactiveRepairPressureState,
        DateTimeOffset now)
    {
        if (decision == InboundV3ReorderPolicyDecision.Normal)
        {
            return;
        }

        var decisionName = FormatReorderPolicyDecision(decision);
        if (string.Equals(context.PullV3LastReorderPolicyDecision, decisionName, StringComparison.Ordinal) &&
            context.PullV3LastReorderPolicyDecisionLogUtc is not null &&
            now - context.PullV3LastReorderPolicyDecisionLogUtc.Value < TimeSpan.FromMilliseconds(PullV3PressureStateSuppressionMs))
        {
            return;
        }

        context.PullV3LastReorderPolicyDecision = decisionName;
        context.PullV3LastReorderPolicyDecisionLogUtc = now;
        var targetWindowBytes = ResolveInboundV3TargetWindowChunksLocked(context) * (long)Math.Max(1, context.ChunkSizeBytes);
        var proactiveRepairAgeMs = GetProactiveFrontierRepairAgeMsLocked(context, now);
        var sameFrontierUnfilledMs = GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now);
        var grantPolicyAfterRepair = ResolveGrantPolicyAfterRepairLocked(context, decision, gapStallAgeMs, recentRepairPressure);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_reorder_policy_decision; transfer_id={context.TransferId}; session_id={context.SessionId}; policy={(IsFileOnlySparseReorderPolicySparseTolerant() ? "SparseTolerant" : "Conservative")}; decision={decisionName}; sparse_mode={(context.ReceiverSparseWriteActive ? 1 : 0)}; transport_profile={context.TransportProfileKind}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; pull_session_degraded={(context.PullSessionDegraded ? 1 : 0)}; receiver_buffer_pressure={(context.ReceiverBufferPressureActive ? 1 : 0)}; repair_recent={(recentRepair ? 1 : 0)}; repair_pressure={(recentRepairPressure ? 1 : 0)}; repeated_proactive_repair={(repeatedProactiveFrontierRepair ? 1 : 0)}; proactive_repair_pressure_state={proactiveRepairPressureState}; proactive_repair_age_ms={proactiveRepairAgeMs}; same_frontier_unfilled_ms={sameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={grantPolicyAfterRepair}; timeout_streak={context.PullTimeoutStreak}; late_arrival_distance={context.PullLateArrivalDistance}; soft_reorder_threshold={ResolveFileOnlySparseSoftLimitedReorderThreshold()}; soft_gap_stall_ms={ResolveFileOnlySparseSoftGapStallMs()}; sparse_ahead_gap_stall_limit_ms={ResolveSparseAheadGrantGapStallLimitMs()}; gap_stall_age_ms={gapStallAgeMs}; current_profile={ResolveInboundV3ProfileName(context)}; target_window_bytes={targetWindowBytes}; soft_limit_target_bytes={ResolveFileOnlySparseSoftLimitBytes()}; granted_window_bytes={GetInboundV3GrantedWindowBytesLocked(context)}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}");
    }

    private static string FormatReorderPolicyDecision(InboundV3ReorderPolicyDecision decision)
        => decision switch
        {
            InboundV3ReorderPolicyDecision.Normal => "normal",
            InboundV3ReorderPolicyDecision.Tolerate => "tolerated",
            InboundV3ReorderPolicyDecision.SoftLimit => "soft_limited",
            InboundV3ReorderPolicyDecision.Limit => "limited",
            _ => "conservative",
        };

    private string ResolveGrantPolicyAfterRepairLocked(
        InboundTransferContext context,
        InboundV3ReorderPolicyDecision decision,
        long gapStallAgeMs = -1,
        bool recentRepairPressure = false)
    {
        if (decision == InboundV3ReorderPolicyDecision.Limit)
        {
            return "healthy_limited";
        }

        if (decision == InboundV3ReorderPolicyDecision.SoftLimit)
        {
            return "healthy_file_only_soft_limited";
        }

        if (context.PullV3LimitedWindowActive &&
            gapStallAgeMs >= 0 &&
            IsFileOnlySparseReorderPolicyCandidateLocked(context) &&
            !HasFileOnlySparseHardLimitedPressureLocked(context, gapStallAgeMs, recentRepairPressure, out _))
        {
            return context.PullV3ExpandedWindowActive ? "healthy_expanded" : "healthy";
        }

        return ResolveInboundV3ProfileName(context);
    }

    private void LogInboundV3GrantDecisionSummaryLocked(
        InboundTransferContext context,
        bool shouldGrant,
        bool shouldAckOnly,
        bool forceGrant,
        bool shouldClampGrant,
        bool targetWindowChanged,
        bool sparseCreditTopup,
        bool lowWatermarkReached,
        bool ackCoalesceBlocked,
        bool sameGrantTarget,
        long targetWindowBytes,
        int targetGrantedUntilExclusive,
        int currentCreditChunks,
        int desiredCreditChunks,
        int lowWatermarkCreditChunks,
        int grantBaseChunkIndex,
        string grantBaseReason,
        int creditBaseChunkIndex,
        string creditBaseReason,
        string sparseCreditBlockReason,
        long sparseCreditAdvanceBytes,
        int sparseCreditTopupBytes,
        DateTimeOffset now)
    {
        if (!shouldGrant &&
            !shouldAckOnly &&
            !ackCoalesceBlocked &&
            context.PullV3LastGrantWindowSummaryLogUtc is not null &&
            now - context.PullV3LastGrantWindowSummaryLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_receiver_grant_decision_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; should_grant={(shouldGrant ? 1 : 0)}; should_ack_only={(shouldAckOnly ? 1 : 0)}; force_grant={(forceGrant ? 1 : 0)}; clamp_grant={(shouldClampGrant ? 1 : 0)}; target_window_changed={(targetWindowChanged ? 1 : 0)}; sparse_credit_topup={(sparseCreditTopup ? 1 : 0)}; low_watermark_reached={(lowWatermarkReached ? 1 : 0)}; ack_coalesce_blocked={(ackCoalesceBlocked ? 1 : 0)}; same_grant_target={(sameGrantTarget ? 1 : 0)}; target_window_bytes={targetWindowBytes}; current_credit_chunks={currentCreditChunks}; desired_credit_chunks={desiredCreditChunks}; low_watermark_credit_chunks={lowWatermarkCreditChunks}; credit_remaining_bytes={currentCreditChunks * (long)chunkSizeBytes}; credit_desired_bytes={desiredCreditChunks * (long)chunkSizeBytes}; granted_until_chunk_index_exclusive={context.PullV3GrantedUntilExclusive}; target_granted_until_chunk_index_exclusive={targetGrantedUntilExclusive}; grant_base_chunk_index={grantBaseChunkIndex}; grant_base_reason={grantBaseReason}; credit_base_chunk_index={creditBaseChunkIndex}; credit_base_reason={creditBaseReason}; sparse_credit_advance_bytes={sparseCreditAdvanceBytes}; sparse_credit_topup_bytes={sparseCreditTopupBytes}; sparse_credit_block_reason={sparseCreditBlockReason}; ack_debt_bytes={context.PullAckDebtBytes}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}");
    }

    private void LogInboundV3GrantWindowSummaryLocked(
        InboundTransferContext context,
        string reason,
        long targetWindowBytes,
        int targetGrantedUntilExclusive,
        int currentCreditChunks,
        int desiredCreditChunks,
        int lowWatermarkCreditChunks,
        int grantBaseChunkIndex,
        string grantBaseReason,
        long sparseAheadBytes,
        int creditBaseChunkIndex,
        string creditBaseReason,
        string sparseCreditBlockReason,
        long sparseCreditAdvanceBytes,
        int sparseCreditTopupBytes,
        int targetBaseChunkIndex,
        string targetBaseReason,
        string sparseCreditMode,
        bool sparseCreditHoldActive,
        bool sparseCreditEligible,
        string proactiveRepairPressureState,
        DateTimeOffset now)
    {
        if (context.PullV3LastGrantWindowSummaryLogUtc is not null &&
            now - context.PullV3LastGrantWindowSummaryLogUtc.Value < TimeSpan.FromMilliseconds(ResolveFileOnlySparseGrantCoalesceMs()) &&
            !string.Equals(reason, "force", StringComparison.Ordinal) &&
            !string.Equals(reason, "clamp", StringComparison.Ordinal) &&
            !string.Equals(reason, "target_changed", StringComparison.Ordinal) &&
            !string.Equals(reason, "credit_keepalive", StringComparison.Ordinal))
        {
            return;
        }

        context.PullV3LastGrantWindowSummaryLogUtc = now;
        var chunkSizeBytes = Math.Max(1, context.ChunkSizeBytes);
        var effectiveGrantedWindowBytes = Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * (long)chunkSizeBytes;
        var creditRemainingBytes = currentCreditChunks * (long)chunkSizeBytes;
        var creditDesiredBytes = desiredCreditChunks * (long)chunkSizeBytes;
        var proactiveRepairAgeMs = GetProactiveFrontierRepairAgeMsLocked(context, now);
        var sameFrontierUnfilledMs = GetSameFrontierUnfilledProactiveRepairAgeMsLocked(context, now);
        var fixedFileOnlyWindowBytes = ResolveFixedFileOnlyWindowBytes();
        var fixedFileOnlyWindowActive = fixedFileOnlyWindowBytes > 0 && IsFileOnlySparseReorderPolicyCandidateLocked(context);
        var limitedRecoveryBlockReason = context.PullV3LimitedWindowActive
            ? ResolveLimitedRecoveryBlockReasonLocked(context, now)
            : "(none)";
        var limitedRecoveryCleanMs =
            context.PullV3LimitedWindowActive &&
            string.Equals(limitedRecoveryBlockReason, "(none)", StringComparison.Ordinal) &&
            context.PullV3CleanSinceUtc is not null
                ? (long)Math.Max(0, (now - context.PullV3CleanSinceUtc.Value).TotalMilliseconds)
                : 0L;
        var limitedRecoveryHoldMs = IsFileOnlySparseReorderPolicyCandidateLocked(context)
            ? ResolveFileOnlySparseLimitedRecoveryMs()
            : PullV3LimitedRecoveryHoldMs;
        if (context.PullV3LimitedWindowActive &&
            IsFileOnlySparseReorderPolicyCandidateLocked(context) &&
            string.Equals(limitedRecoveryBlockReason, "(none)", StringComparison.Ordinal) &&
            limitedRecoveryCleanMs >= limitedRecoveryHoldMs)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v3_limited_state_invariant_violation; transfer_id={context.TransferId}; session_id={context.SessionId}; profile={ResolveInboundV3ProfileName(context)}; limited_recovery_clean_ms={limitedRecoveryCleanMs}; limited_recovery_hold_ms={limitedRecoveryHoldMs}; limited_recovery_block_reason={limitedRecoveryBlockReason}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}");
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_grant_window_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; file_only_sparse_cadence={(IsFileOnlySparseReorderPolicyCandidateLocked(context) ? 1 : 0)}; profile={ResolveInboundV3ProfileName(context)}; target_window_bytes={targetWindowBytes}; effective_granted_window_bytes={effectiveGrantedWindowBytes}; current_credit_chunks={currentCreditChunks}; desired_credit_chunks={desiredCreditChunks}; low_watermark_credit_chunks={lowWatermarkCreditChunks}; credit_remaining_chunks={currentCreditChunks}; credit_desired_chunks={desiredCreditChunks}; credit_remaining_bytes={creditRemainingBytes}; credit_desired_bytes={creditDesiredBytes}; granted_until_chunk_index_exclusive={context.PullV3GrantedUntilExclusive}; target_granted_until_chunk_index_exclusive={targetGrantedUntilExclusive}; target_base_chunk_index={targetBaseChunkIndex}; target_base_reason={targetBaseReason}; grant_base_chunk_index={grantBaseChunkIndex}; grant_base_reason={grantBaseReason}; sparse_ahead_bytes={sparseAheadBytes}; credit_base_chunk_index={creditBaseChunkIndex}; credit_base_reason={creditBaseReason}; sparse_credit_mode={sparseCreditMode}; sparse_credit_hold_active={(sparseCreditHoldActive ? 1 : 0)}; sparse_credit_eligible={(sparseCreditEligible ? 1 : 0)}; sparse_credit_advance_bytes={sparseCreditAdvanceBytes}; sparse_credit_topup_bytes={sparseCreditTopupBytes}; sparse_credit_block_reason={sparseCreditBlockReason}; proactive_repair_pressure_state={proactiveRepairPressureState}; proactive_repair_age_ms={proactiveRepairAgeMs}; same_frontier_unfilled_ms={sameFrontierUnfilledMs}; proactive_repair_grace_ms={ResolveProactiveRepairGraceMs()}; grant_policy_after_repair={ResolveInboundV3ProfileName(context)}; limited_recovery_clean_ms={limitedRecoveryCleanMs}; limited_recovery_hold_ms={limitedRecoveryHoldMs}; limited_recovery_block_reason={limitedRecoveryBlockReason}; fixed_file_only_window_active={(fixedFileOnlyWindowActive ? 1 : 0)}; fixed_file_only_window_bytes={(fixedFileOnlyWindowActive ? fixedFileOnlyWindowBytes : 0)}; soft_reorder_threshold={ResolveFileOnlySparseSoftLimitedReorderThreshold()}; soft_gap_stall_ms={ResolveFileOnlySparseSoftGapStallMs()}; sparse_ahead_gap_stall_limit_ms={ResolveSparseAheadGrantGapStallLimitMs()}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; late_arrival_distance={context.PullLateArrivalDistance}; pending_chunk_count={GetReceiverPendingChunkCountLocked(context)}; pending_bytes={context.BufferedBytes}");
    }

    private void LogInboundV3ProfileChanged(InboundTransferContext context, string previousProfile, string updatedProfile, string reason)
    {
        if (string.Equals(previousProfile, updatedProfile, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var targetWindowBytes = ResolveInboundV3TargetWindowChunksLocked(context) * Math.Max(1, context.ChunkSizeBytes);
        var grantedWindowBytes = Math.Max(0, context.PullV3GrantedUntilExclusive - context.NextChunkIndex) * Math.Max(1, context.ChunkSizeBytes);
        var conservativeStartupDurationMs = GetConservativeStartupDurationMs(context, now);
        var bytesBeforeStartupExit = GetConservativeStartupBytesBeforeExit(context);
        var startupProbeWindowBytes = context.PullV3ConservativeStartupProbeActive ? PullV3ConservativeStartupProbeTargetInFlightBytes : 0;
        var firstRepairOrTimeoutBeforeStartupExit = context.PullV3FirstRepairOrTimeoutBeforeStartupExit ? 1 : 0;
        var limitedRecoveryBlockReason = string.Equals(previousProfile, "healthy_limited", StringComparison.Ordinal)
            ? ResolveLimitedRecoveryBlockReasonLocked(context, now)
            : "(none)";
        var limitedRecoveryCleanMs =
            string.Equals(previousProfile, "healthy_limited", StringComparison.Ordinal) &&
            string.Equals(limitedRecoveryBlockReason, "(none)", StringComparison.Ordinal) &&
            context.PullV3CleanSinceUtc is not null
                ? (long)Math.Max(0, (now - context.PullV3CleanSinceUtc.Value).TotalMilliseconds)
                : 0L;
        var limitedRecoveryHoldMs = IsFileOnlySparseReorderPolicyCandidateLocked(context)
            ? ResolveFileOnlySparseLimitedRecoveryMs()
            : PullV3LimitedRecoveryHoldMs;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v3_profile_changed; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_profile={previousProfile}; updated_profile={updatedProfile}; reason={reason}; target_window_bytes={targetWindowBytes}; granted_window_bytes={grantedWindowBytes}; late_arrival_distance={context.PullLateArrivalDistance}; timeout_streak={context.PullTimeoutStreak}; limited_recovery_clean_ms={limitedRecoveryCleanMs}; limited_recovery_hold_ms={limitedRecoveryHoldMs}; limited_recovery_block_reason={limitedRecoveryBlockReason}; conservative_startup_duration_ms={conservativeStartupDurationMs}; bytes_before_startup_exit={bytesBeforeStartupExit}; startup_exit_reason={GetConservativeStartupExitReason(context)}; startup_probe_window_bytes={startupProbeWindowBytes}; first_repair_or_timeout_before_startup_exit={firstRepairOrTimeoutBeforeStartupExit}");
    }

    private string ResolveInboundV3ProfileName(InboundTransferContext context)
    {
        if (context.PullV3ConservativeStartupActive)
        {
            return context.PullV3ConservativeStartupDegradedActive
                ? "nkn_conservative_startup_degraded"
                : context.PullV3ConservativeStartupProbeActive
                    ? "nkn_conservative_startup_probe"
                : "nkn_conservative_startup";
        }

        if (sessionScreenShareDegraded || context.PullSessionDegraded)
        {
            return "degraded";
        }

        if (sessionScreenShareActive)
        {
            return "balanced_screenshare";
        }

        if (context.PullV3LimitedWindowActive)
        {
            return "healthy_limited";
        }

        if (context.PullV3FileOnlySoftLimitedWindowActive)
        {
            return "healthy_file_only_soft_limited";
        }

        return context.PullV3ExpandedWindowActive ? "healthy_expanded" : "healthy";
    }

    private static long GetConservativeStartupDurationMs(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.PullV3ConservativeStartupStartedUtc is null)
        {
            return 0;
        }

        var end = context.PullV3ConservativeStartupExitedUtc ?? now;
        return (long)Math.Max(0, (end - context.PullV3ConservativeStartupStartedUtc.Value).TotalMilliseconds);
    }

    private static long GetConservativeStartupBytesBeforeExit(InboundTransferContext context)
        => context.PullV3ConservativeStartupExitedUtc is null
            ? context.BytesTransferred
            : context.PullV3ConservativeStartupExitBytes;

    private static string GetConservativeStartupExitReason(InboundTransferContext context)
        => string.IsNullOrWhiteSpace(context.PullV3ConservativeStartupExitReason)
            ? "(none)"
            : context.PullV3ConservativeStartupExitReason!;

    private static void ExitConservativeStartupLocked(InboundTransferContext context, DateTimeOffset now, string reason)
    {
        if (!context.PullV3ConservativeStartupActive)
        {
            return;
        }

        context.PullV3ConservativeStartupActive = false;
        context.PullV3ConservativeStartupDegradedActive = false;
        context.PullV3ConservativeStartupProbeActive = false;
        context.PullV3ConservativeStartupExitedUtc ??= now;
        context.PullV3ConservativeStartupExitReason ??= reason;
        context.PullV3ConservativeStartupExitBytes = Math.Max(context.PullV3ConservativeStartupExitBytes, context.BytesTransferred);
    }

    private enum V3ProactiveRepairPressureMode
    {
        PhaseNCompatible,
        BenignUntilProven,
        ImmediatePressure,
    }

    private sealed record PullV3RepairRequestSelection(
        FileTransferDataFrameV2? Frame,
        int RangeCount,
        int RequestedChunkCount,
        int FirstStartChunkIndex,
        int LastEndChunkExclusive,
        int NextChunkIndex,
        int HighestReceivedChunkIndex,
        int GrantedUntilChunkIndexExclusive,
        int PendingChunkCount,
        long PendingBytes,
        int LateArrivalDistance,
        string RepairRequestKey);

    private sealed record PullV3ProactiveFrontierRepairSkip(
        string Reason,
        int StartChunkIndex,
        int RequestedChunkCount,
        string RepairRequestKey,
        int AttemptCount,
        long GapStallAgeMs,
        int LateArrivalDistance,
        int HighestReceivedChunkIndex,
        int GrantedUntilChunkIndexExclusive,
        long GrantedWindowBytes,
        int MinGapMs,
        int RepeatMs,
        int MaxRepairChunks,
        string ProactiveRepairPressureState,
        long ProactiveRepairAgeMs,
        long SameFrontierUnfilledMs,
        string GrantPolicyAfterRepair,
        bool ConsumesScheduler,
        bool ShouldLog);

    private readonly record struct InboundV3SparseCreditBaseResolution(
        int BaseChunkIndex,
        string BaseReason,
        long SparseAheadBytes,
        bool UseSparseBase,
        bool HoldActive,
        bool Eligible,
        string BlockReason);

    private readonly record struct RepairChunkFilterStats(
        int RemoteNextExpectedChunkIndex,
        int ChunksAcceptedForTransport,
        int SkippedObsoleteCount,
        int SkippedFutureCount,
        int SkippedOutOfBoundsCount);
}
