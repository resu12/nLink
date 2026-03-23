using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundSendLoopAsync(OutboundTransferContext context)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
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

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);

            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Sending file metadata.");

            var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
            try
            {
                while (true)
                {
                    context.LifetimeCts.Token.ThrowIfCancellationRequested();

                    FileTransferChunkV1? repairChunk = null;
                    string? repairLogEvent = null;
                    int repairChunkIndex = -1;
                    int repairRangeStartChunkIndex = -1;
                    int repairRangeEndChunkExclusive = -1;
                    int pendingRepairBatchCount = 0;
                    TimeSpan repairWaitDelay = TimeSpan.Zero;
                    bool repairModeActive = false;
                    int nextChunkIndexToRead;
                    int remoteGrantedUntilExclusive;
                    int remoteNextExpectedChunkIndex;
                    int effectiveSendLimitExclusive;
                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        repairModeActive = TryPrepareRepairChunkLocked(
                            context,
                            GetEffectiveSendLimitExclusiveLocked(context),
                            out repairChunk,
                            out repairWaitDelay,
                            out repairLogEvent,
                            out repairChunkIndex,
                            out repairRangeStartChunkIndex,
                            out repairRangeEndChunkExclusive,
                            out pendingRepairBatchCount);
                        nextChunkIndexToRead = context.NextChunkIndexToRead;
                        remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
                        remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
                        effectiveSendLimitExclusive = GetEffectiveSendLimitExclusiveLocked(context);
                    }

                    if (repairLogEvent is not null)
                    {
                        LogRepairChunkEvent(
                            repairLogEvent,
                            context.TransferId,
                            context.SessionId,
                            repairChunkIndex,
                            repairRangeStartChunkIndex,
                            repairRangeEndChunkExclusive,
                            remoteNextExpectedChunkIndex,
                            remoteGrantedUntilExclusive,
                            context.CurrentRepairBatchSize,
                            pendingRepairBatchCount);
                    }

                    if (repairChunk is not null)
                    {
                        await currentTransport.SendFileTransferChunkAsync(repairChunk, context.LifetimeCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (repairModeActive)
                    {
                        LogRepairModeBatchWait(
                            context.TransferId,
                            context.SessionId,
                            repairRangeStartChunkIndex,
                            repairRangeEndChunkExclusive,
                            remoteNextExpectedChunkIndex,
                            remoteGrantedUntilExclusive,
                            pendingRepairBatchCount);
                        await WaitForOutboundRepairActivityAsync(context, repairWaitDelay).ConfigureAwait(false);
                        continue;
                    }

                    if (context.RepairOnlyModeActive &&
                        context.RemotePressureMode != FileTransferPressureMode.CatchUpOnly &&
                        nextChunkIndexToRead > remoteNextExpectedChunkIndex)
                    {
                        await WaitForOutboundControlActivityAsync(context, "repair_only").ConfigureAwait(false);
                        continue;
                    }

                    if (nextChunkIndexToRead >= context.ChunkCount)
                    {
                        UpdateOutboundState(
                            context,
                            FileTransferTransferState.AwaitingCompletion,
                            context.BytesTransferred,
                            context.ChunksTransferred,
                            "Waiting for receiver verification.");
                        await WaitForOutboundCompletionSignalAsync(context).ConfigureAwait(false);
                        continue;
                    }

                    if (nextChunkIndexToRead >= effectiveSendLimitExclusive)
                    {
                        await WaitForOutboundControlActivityAsync(context, "window").ConfigureAwait(false);
                        continue;
                    }

                    var fileOffset = (long)nextChunkIndexToRead * context.ChunkSizeBytes;
                    if (stream.CanSeek && stream.Position != fileOffset)
                    {
                        stream.Seek(fileOffset, SeekOrigin.Begin);
                    }

                    var remaining = context.FileSizeBytes - fileOffset;
                    var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, targetReadSize), context.LifetimeCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Failed,
                            errorCode: FileSizeMismatchErrorCode,
                            statusMessage: "Source stream did not match the declared file size.",
                            notifyPeer: true,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    var chunkBytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);
                    var chunkMessage = new FileTransferChunkV1
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        ChunkIndex = nextChunkIndexToRead,
                        ChunkCount = context.ChunkCount,
                        DataBase64 = Convert.ToBase64String(chunkBytes),
                    };

                    SessionFileTransferSnapshot? snapshot = null;
                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        context.NextChunkIndexToRead = nextChunkIndexToRead + 1;
                        context.SentChunkCache[nextChunkIndexToRead] = chunkMessage;
                        context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, context.NextChunkIndexToRead);
                        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                            ? context.FileSizeBytes
                            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                        snapshot = CreateSnapshotLocked();
                    }

                    if (snapshot is not null)
                    {
                        RaiseTransferChanged(snapshot);
                    }

                    await currentTransport.SendFileTransferChunkAsync(chunkMessage, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            // Local cancel path already transitioned the state.
        }
        catch (Exception ex)
        {
            var errorCode =
                ex is InvalidOperationException invalidOperationException &&
                invalidOperationException.Message.Contains("payload exceeded safe budget", StringComparison.OrdinalIgnoreCase)
                    ? PayloadBudgetExceededErrorCode
                    : ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ex.Message,
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private int GetLocalSendAheadClampChunksLocked(OutboundTransferContext context)
    {
        if (context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly)
        {
            return sessionScreenShareActive
                ? LocalSendAheadClampDegradedWhileScreenshareChunks
                : LocalSendAheadClampDegradedChunks;
        }

        if (context.RepairModeActive)
        {
            return sessionScreenShareActive
                ? LocalSendAheadClampDegradedWhileScreenshareChunks
                : LocalSendAheadClampDegradedChunks;
        }

        return LocalSendAheadClampChunks;
    }

    private int GetEffectiveSendLimitExclusiveLocked(OutboundTransferContext context)
    {
        var localClampExclusive = context.RemoteNextExpectedChunkIndex + GetLocalSendAheadClampChunksLocked(context);
        return Math.Min(context.RemoteGrantedUntilExclusive, localClampExclusive);
    }

    private static bool ShouldLogSendAheadClampLocked(OutboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.LastSendAheadClampLogUtc is not null &&
            now - context.LastSendAheadClampLogUtc.Value < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
        {
            return false;
        }

        context.LastSendAheadClampLogUtc = now;
        return true;
    }

    private void MaybeLogSendAheadClamp(
        OutboundTransferContext context,
        int nextChunkIndexToRead,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int effectiveSendLimitExclusive)
    {
        if (effectiveSendLimitExclusive >= remoteGrantedUntilExclusive)
        {
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || !ShouldLogSendAheadClampLocked(context))
            {
                return;
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_send_ahead_clamped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}");
    }

    private bool IsTransferDegradedLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            (outboundTransfer.PullSessionDegraded ||
             outboundTransfer.RepairModeActive ||
             outboundTransfer.RemotePressureMode == FileTransferPressureMode.CatchUpOnly)) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            (inboundTransfer.PullSessionDegraded ||
             inboundTransfer.BulkFallbackModeActive ||
             inboundTransfer.DegradedRepairModeActive ||
             inboundTransfer.LocalPressureMode == FileTransferPressureMode.CatchUpOnly));

    private bool IsCatchUpOnlyPressureActiveLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            outboundTransfer.RemotePressureMode == FileTransferPressureMode.CatchUpOnly) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            inboundTransfer.LocalPressureMode == FileTransferPressureMode.CatchUpOnly);

    private async Task WaitForOutboundControlActivityAsync(OutboundTransferContext context, string reason)
    {
        Task signalTask;
        DateTimeOffset deadlineUtc;
        int nextChunkIndexToRead = 0;
        int remoteNextExpectedChunkIndex = 0;
        int remoteGrantedUntilExclusive = 0;
        int effectiveSendLimitExclusive = 0;
        bool repairOnlyModeActive = false;
        DateTimeOffset? lastWindowUpdateUtc = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (string.Equals(reason, "window", StringComparison.Ordinal) &&
                (context.NextChunkIndexToRead < GetEffectiveSendLimitExclusiveLocked(context) || context.PendingRepairChunkIndices.Count > 0))
            {
                return;
            }

            if (string.Equals(reason, "completion", StringComparison.Ordinal) &&
                (context.RemoteNextExpectedChunkIndex >= context.ChunkCount || context.PendingRepairChunkIndices.Count > 0))
            {
                return;
            }

            if (string.Equals(reason, "repair_only", StringComparison.Ordinal) &&
                (!context.RepairOnlyModeActive || context.NextChunkIndexToRead <= context.RemoteNextExpectedChunkIndex))
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            deadlineUtc = context.LastWindowUpdateUtc + OutboundWindowTimeout;
            nextChunkIndexToRead = context.NextChunkIndexToRead;
            remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
            remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
            effectiveSendLimitExclusive = GetEffectiveSendLimitExclusiveLocked(context);
            repairOnlyModeActive = context.RepairOnlyModeActive;
            lastWindowUpdateUtc = context.LastWindowUpdateUtc;
        }

        if (string.Equals(reason, "window", StringComparison.Ordinal))
        {
            MaybeLogSendAheadClamp(context, nextChunkIndexToRead, remoteNextExpectedChunkIndex, remoteGrantedUntilExclusive, effectiveSendLimitExclusive);
            if (context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly &&
                effectiveSendLimitExclusive < remoteGrantedUntilExclusive)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=sequential_send_blocked_by_pressure; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; pressure_mode={FormatPressureMode(context.RemotePressureMode)}; pressure_revision={context.RemotePressureRevision}; last_window_update_utc={lastWindowUpdateUtc:O}");
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=window_waiting_for_credit; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; last_window_update_utc={lastWindowUpdateUtc:O}");
            }
        }
        else if (string.Equals(reason, "repair_only", StringComparison.Ordinal) && repairOnlyModeActive)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=repair_only_waiting_for_catchup; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; last_window_update_utc={lastWindowUpdateUtc:O}");
        }

        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: WindowTimeoutErrorCode,
                statusMessage: "Receiver window update was not received in time.",
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(signalTask, Task.Delay(remaining, context.LifetimeCts.Token)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
            return;
        }

        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: WindowTimeoutErrorCode,
            statusMessage: "Receiver window update was not received in time.",
            notifyPeer: true,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WaitForOutboundCompletionSignalAsync(OutboundTransferContext context)
    {
        Task signalTask;
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.PendingRepairChunkIndices.Count > 0 ||
                context.RepairModeActive)
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            cancellationToken = context.LifetimeCts.Token;
        }

        var completed = await Task.WhenAny(
            signalTask,
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
        }
    }

    private async Task WaitForOutboundRepairActivityAsync(OutboundTransferContext context, TimeSpan delay)
    {
        Task signalTask;
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !context.RepairModeActive)
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            cancellationToken = context.LifetimeCts.Token;
        }

        if (delay <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        var completed = await Task.WhenAny(
            signalTask,
            Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
        }
    }

    private static bool TryPrepareRepairChunkLocked(
        OutboundTransferContext context,
        int effectiveSendLimitExclusive,
        out FileTransferChunkV1? message,
        out TimeSpan waitDelay,
        out string? logEvent,
        out int chunkIndex,
        out int repairRangeStartChunkIndex,
        out int repairRangeEndChunkExclusive,
        out int pendingBatchCount)
    {
        message = null;
        waitDelay = TimeSpan.Zero;
        logEvent = null;
        chunkIndex = -1;
        repairRangeStartChunkIndex = context.RepairRangeStartChunkIndex ?? -1;
        repairRangeEndChunkExclusive = context.RepairRangeEndChunkExclusive ?? -1;
        pendingBatchCount = context.PendingRepairChunkIndices.Count;

        if (!context.RepairModeActive ||
            context.RepairRangeStartChunkIndex is null ||
            context.RepairRangeEndChunkExclusive is null)
        {
            return false;
        }

        if (context.RemoteNextExpectedChunkIndex >= context.RepairRangeEndChunkExclusive.Value)
        {
            PromoteDeferredRepairRangeOrClearLocked(context);
            return false;
        }

        if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out var unavailableChunkIndex))
        {
            context.LastRepairSendUtc = DateTimeOffset.UtcNow;
            context.LastRepairChunkSentIndex = chunkIndex;
            logEvent = context.RepairSendCycle == 0 ? "repair_chunk_sent" : "repair_chunk_resent";
            pendingBatchCount = context.PendingRepairChunkIndices.Count;
            return true;
        }

        if (unavailableChunkIndex >= 0)
        {
            context.LastRepairSendUtc = DateTimeOffset.UtcNow;
            context.LastRepairChunkSentIndex = unavailableChunkIndex;
            logEvent = "repair_chunk_unavailable";
            chunkIndex = unavailableChunkIndex;
            waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
            pendingBatchCount = context.PendingRepairChunkIndices.Count;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.PendingRepairChunkIndices.Count == 0 &&
            !context.RepairBatchInFlight)
        {
            EnqueueNextRepairBatchLocked(context, effectiveSendLimitExclusive);
            if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out unavailableChunkIndex))
            {
                context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                context.LastRepairChunkSentIndex = chunkIndex;
                logEvent = context.RepairSendCycle == 0 ? "repair_chunk_sent" : "repair_chunk_resent";
                pendingBatchCount = context.PendingRepairChunkIndices.Count;
                return true;
            }

            if (unavailableChunkIndex >= 0)
            {
                context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                context.LastRepairChunkSentIndex = unavailableChunkIndex;
                logEvent = "repair_chunk_unavailable";
                chunkIndex = unavailableChunkIndex;
                waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
                pendingBatchCount = context.PendingRepairChunkIndices.Count;
                return true;
            }
        }

        if (context.RepairBatchInFlight)
        {
            var resendReferenceUtc = context.LastRepairSendUtc ?? now;
            if (context.LastRepairEvidenceUtc is not null &&
                context.LastRepairEvidenceUtc.Value > resendReferenceUtc)
            {
                resendReferenceUtc = context.LastRepairEvidenceUtc.Value;
            }

            var resendDueUtc = resendReferenceUtc.AddMilliseconds(GetRepairAckTimeoutMsLocked(context));
            if (context.LastRepairSendUtc is not null && now >= resendDueUtc)
            {
                context.RepairSendCycle++;
                ReleaseRepairBatchLocked(context);
                ClearPendingRepairQueueLocked(context);
                EnqueueNextRepairBatchLocked(context, effectiveSendLimitExclusive);
                if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out unavailableChunkIndex))
                {
                    context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                    context.LastRepairChunkSentIndex = chunkIndex;
                    logEvent = "repair_chunk_resent";
                    pendingBatchCount = context.PendingRepairChunkIndices.Count;
                    return true;
                }

                if (unavailableChunkIndex >= 0)
                {
                    context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                    context.LastRepairChunkSentIndex = unavailableChunkIndex;
                    logEvent = "repair_chunk_unavailable";
                    chunkIndex = unavailableChunkIndex;
                    waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
                    pendingBatchCount = context.PendingRepairChunkIndices.Count;
                    return true;
                }
            }

            waitDelay = resendDueUtc - DateTimeOffset.UtcNow;
            if (waitDelay < TimeSpan.Zero)
            {
                waitDelay = TimeSpan.Zero;
            }
        }

        pendingBatchCount = context.PendingRepairChunkIndices.Count;
        return true;
    }

    private static bool TryDequeueRepairChunkLocked(
        OutboundTransferContext context,
        int effectiveSendLimitExclusive,
        out FileTransferChunkV1? message,
        out int chunkIndex,
        out int unavailableChunkIndex)
    {
        chunkIndex = -1;
        unavailableChunkIndex = -1;
        while (context.PendingRepairChunkIndices.Count > 0)
        {
            chunkIndex = context.PendingRepairChunkIndices.Dequeue();
            context.PendingRepairChunkIndicesSet.Remove(chunkIndex);
            if (chunkIndex >= effectiveSendLimitExclusive)
            {
                context.PendingRepairChunkIndices.Enqueue(chunkIndex);
                context.PendingRepairChunkIndicesSet.Add(chunkIndex);
                message = null;
                unavailableChunkIndex = -1;
                return false;
            }

            if (chunkIndex < context.RemoteNextExpectedChunkIndex)
            {
                LogRepairChunkEvent(
                    "repair_chunk_skipped_obsolete",
                    context.TransferId,
                    context.SessionId,
                    chunkIndex,
                    context.RepairRangeStartChunkIndex ?? -1,
                    context.RepairRangeEndChunkExclusive ?? -1,
                    context.RemoteNextExpectedChunkIndex,
                    context.RemoteGrantedUntilExclusive,
                    context.CurrentRepairBatchSize,
                    context.PendingRepairChunkIndices.Count);
                continue;
            }

            if (context.SentChunkCache.TryGetValue(chunkIndex, out var cachedMessage))
            {
                message = cachedMessage;
                return true;
            }

            unavailableChunkIndex = chunkIndex;
            message = null;
            return false;
        }

        message = null;
        return false;
    }

    private static void ClearPendingRepairQueueLocked(OutboundTransferContext context)
    {
        context.PendingRepairChunkIndices.Clear();
        context.PendingRepairChunkIndicesSet.Clear();
    }

    private static void EnqueueRepairRangeLocked(OutboundTransferContext context, int rangeStartChunkIndex, int rangeEndChunkExclusive)
    {
        ClearPendingRepairQueueLocked(context);

        var start = Math.Max(rangeStartChunkIndex, context.RemoteNextExpectedChunkIndex);
        var end = Math.Min(rangeEndChunkExclusive, context.NextChunkIndexToRead);
        for (var chunkIndex = start; chunkIndex < end; chunkIndex++)
        {
            if (!context.PendingRepairChunkIndicesSet.Add(chunkIndex))
            {
                continue;
            }

            context.PendingRepairChunkIndices.Enqueue(chunkIndex);
        }
    }

    private static void EnqueueNextRepairBatchLocked(OutboundTransferContext context, int effectiveSendLimitExclusive)
    {
        if (context.RepairRangeStartChunkIndex is null || context.RepairRangeEndChunkExclusive is null)
        {
            ClearPendingRepairQueueLocked(context);
            ReleaseRepairBatchLocked(context);
            return;
        }

        var start = Math.Max(context.RepairRangeStartChunkIndex.Value, context.RemoteNextExpectedChunkIndex);
        var end = Math.Min(context.RepairRangeEndChunkExclusive.Value, Math.Min(start + context.CurrentRepairBatchSize, effectiveSendLimitExclusive));
        if (end <= start)
        {
            ClearPendingRepairQueueLocked(context);
            ReleaseRepairBatchLocked(context);
            return;
        }

        EnqueueRepairRangeLocked(context, start, end);
        context.RepairBatchInFlight = context.PendingRepairChunkIndices.Count > 0;
        context.OutstandingRepairBatchStartChunkIndex = start;
        context.OutstandingRepairBatchEndChunkExclusive = end;
    }

    private static void ReleaseRepairBatchLocked(OutboundTransferContext context)
    {
        context.RepairBatchInFlight = false;
        context.OutstandingRepairBatchStartChunkIndex = null;
        context.OutstandingRepairBatchEndChunkExclusive = null;
    }

    private void UpdateOutboundPressureDerivedStateLocked(OutboundTransferContext context)
    {
        var batchSize =
            context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly && sessionScreenShareActive
                ? 1
                : RepairBatchSize;
        if (context.RepairSingleChunkModeActive)
        {
            batchSize = 1;
        }

        context.CurrentRepairBatchSize = batchSize;
    }

    private static int GetRepairAckTimeoutMsLocked(OutboundTransferContext context)
        => context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly
            ? 1000
            : RepairAckTimeoutMs;

    private static void ClearRepairModeLocked(OutboundTransferContext context)
    {
        context.RepairModeActive = false;
        context.RepairRangeStartChunkIndex = null;
        context.RepairRangeEndChunkExclusive = null;
        context.DeferredRepairRangeStartChunkIndex = null;
        context.DeferredRepairRangeEndChunkExclusive = null;
        context.LastRepairSendUtc = null;
        context.LastRepairAckObservedUtc = null;
        context.LastRepairEvidenceUtc = null;
        context.LastRepairRangeRequestedUtc = null;
        context.LastRepairChunkSentIndex = null;
        context.RepairSendCycle = 0;
        context.RepairSingleChunkModeActive = false;
        ReleaseRepairBatchLocked(context);
        ClearPendingRepairQueueLocked(context);
    }

    private static void EnterRepairOnlyModeLocked(OutboundTransferContext context)
    {
        if (context.RepairOnlyModeActive)
        {
            return;
        }

        context.RepairOnlyModeActive = true;
        LogRepairOnlyModeEvent(
            "repair_only_mode_entered",
            context.TransferId,
            context.SessionId,
            context.NextChunkIndexToRead,
            context.RemoteNextExpectedChunkIndex,
            context.RemoteGrantedUntilExclusive);
    }

    private static void TryExitRepairOnlyModeLocked(OutboundTransferContext context)
    {
        if (!context.RepairOnlyModeActive)
        {
            return;
        }

        if (context.RepairModeActive ||
            context.PendingRepairChunkIndices.Count > 0 ||
            context.RemoteNextExpectedChunkIndex < context.NextChunkIndexToRead)
        {
            return;
        }

        context.RepairOnlyModeActive = false;
        LogRepairOnlyModeEvent(
            "repair_only_mode_exited",
            context.TransferId,
            context.SessionId,
            context.NextChunkIndexToRead,
            context.RemoteNextExpectedChunkIndex,
            context.RemoteGrantedUntilExclusive);
    }

    private static void PromoteDeferredRepairRangeOrClearLocked(OutboundTransferContext context)
    {
        if (context.DeferredRepairRangeStartChunkIndex is not null &&
            context.DeferredRepairRangeEndChunkExclusive is not null &&
            context.DeferredRepairRangeEndChunkExclusive.Value > context.RemoteNextExpectedChunkIndex)
        {
            ActivateRepairRangeLocked(
                context,
                context.DeferredRepairRangeStartChunkIndex.Value,
                context.DeferredRepairRangeEndChunkExclusive.Value);
            context.DeferredRepairRangeStartChunkIndex = null;
            context.DeferredRepairRangeEndChunkExclusive = null;
            return;
        }

        ClearRepairModeLocked(context);
    }

    private static void ActivateRepairRangeLocked(OutboundTransferContext context, int rangeStartChunkIndex, int rangeEndChunkExclusive)
    {
        context.RepairModeActive = true;
        context.RepairRangeStartChunkIndex = rangeStartChunkIndex;
        context.RepairRangeEndChunkExclusive = rangeEndChunkExclusive;
        context.LastRepairSendUtc = null;
        context.LastRepairAckObservedUtc = null;
        context.LastRepairEvidenceUtc = DateTimeOffset.UtcNow;
        context.LastRepairRangeRequestedUtc = DateTimeOffset.UtcNow;
        context.LastRepairChunkSentIndex = null;
        context.RepairSendCycle = 0;
        context.RepairSingleChunkModeActive = true;
        ReleaseRepairBatchLocked(context);
        EnqueueNextRepairBatchLocked(context, context.RemoteGrantedUntilExclusive);
    }

    private static void PruneSentChunkCache(OutboundTransferContext context, int nextExpectedChunkIndex)
    {
        if (context.SentChunkCache.Count == 0)
        {
            return;
        }

        List<int>? staleKeys = null;
        foreach (var key in context.SentChunkCache.Keys)
        {
            if (key >= nextExpectedChunkIndex)
            {
                continue;
            }

            staleKeys ??= [];
            staleKeys.Add(key);
        }

        if (staleKeys is null)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            context.SentChunkCache.Remove(key);
            context.PendingRepairChunkIndicesSet.Remove(key);
        }
    }

    private static void UpdateOutboundAcknowledgedProgressLocked(OutboundTransferContext context)
    {
        var acknowledgedChunks = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acknowledgedBytes = acknowledgedChunks >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)acknowledgedChunks * context.ChunkSizeBytes);
        var awaitingCompletion =
            context.NextChunkIndexToRead >= context.ChunkCount &&
            context.PendingRepairChunkIndices.Count == 0 &&
            !context.RepairModeActive;

        context.BytesTransferred = acknowledgedBytes;
        context.ChunksTransferred = acknowledgedChunks;
        context.State = awaitingCompletion || acknowledgedChunks >= context.ChunkCount
            ? FileTransferTransferState.AwaitingCompletion
            : FileTransferTransferState.Sending;
        context.StatusMessage = awaitingCompletion || acknowledgedChunks >= context.ChunkCount
            ? "Waiting for receiver verification."
            : "Sending file data.";
    }

    private async Task TransitionOutboundToTerminalAsync(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool notifyPeer,
        string? cancelReason,
        CancellationToken ct)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldNotifyPeer;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            if (terminalState == FileTransferTransferState.Completed)
            {
                context.BytesTransferred = context.FileSizeBytes;
                context.ChunksTransferred = context.ChunkCount;
                context.BytesAcceptedForTransport = context.FileSizeBytes;
                context.ChunksAcceptedForTransport = context.ChunkCount;
            }
            snapshot = CreateSnapshotLocked();
            shouldNotifyPeer = notifyPeer;
        }

        RaiseTransferChanged(snapshot);
        context.DisposeResources();
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount);

        if (!shouldNotifyPeer)
        {
            return;
        }

        if (terminalState == FileTransferTransferState.Canceled)
        {
            await SendCancelAsync(context.SessionId, context.TransferId, cancelReason, ct).ConfigureAwait(false);
            return;
        }

        if (terminalState == FileTransferTransferState.Failed)
        {
            await SendErrorAsync(context.SessionId, context.TransferId, context.ErrorCode ?? InvalidStateErrorCode, context.StatusMessage, ct).ConfigureAwait(false);
        }
    }

    private async Task TransitionInboundToTerminalAsync(
        InboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool sendError,
        string? errorMessage,
        string? cancelReason,
        CancellationToken ct)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldSendError;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            context.AcceptInProgress = false;
            snapshot = CreateSnapshotLocked();
            shouldSendError = sendError;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: statusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath);

        if (terminalState == FileTransferTransferState.Declined)
        {
            await SendDeclineAsync(sessionId, transferId, cancelReason ?? DeclinedReason, ct).ConfigureAwait(false);
            return;
        }

        if (terminalState == FileTransferTransferState.Canceled)
        {
            await SendCancelAsync(sessionId, transferId, cancelReason ?? CanceledReason, ct).ConfigureAwait(false);
            return;
        }

        if (shouldSendError)
        {
            await SendErrorAsync(sessionId, transferId, normalizedErrorCode ?? InvalidStateErrorCode, errorMessage ?? statusMessage, ct).ConfigureAwait(false);
        }
    }

    private void UpdateOutboundState(
        OutboundTransferContext context,
        FileTransferTransferState state,
        long bytesTransferred,
        int chunksTransferred,
        string statusMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.State = state;
            context.BytesTransferred = bytesTransferred;
            context.ChunksTransferred = chunksTransferred;
            context.StatusMessage = statusMessage;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private void SetInboundAcceptInProgress(InboundTransferContext context, bool acceptInProgress)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.AcceptInProgress = acceptInProgress;
            }
        }
    }

    private async Task SendDeclineAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferDeclineAsync(
                new FileTransferDeclineV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"decline send failed: {ex.Message}");
        }
    }

    private async Task SendCancelAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferCancelAsync(
                new FileTransferCancelV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"cancel send failed: {ex.Message}");
        }
    }

    private async Task SendErrorAsync(string sessionId, string transferId, string errorCode, string? message, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferErrorAsync(
                new FileTransferErrorV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"error send failed: {ex.Message}");
        }
    }

    private void FailOutboundLocally(OutboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            snapshot = CreateSnapshotLocked();
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount);
    }

    private void FailInboundLocally(InboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            context.AcceptInProgress = false;
            snapshot = CreateSnapshotLocked();
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: failureMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath);
    }

    private void DetachTransportCore(bool markActiveTransfersFailed, string failureCode, string failureMessage)
    {
        IFileTransferSignalingTransport? previousTransport;
        ISignalingTransport? previousLifecycle;
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;

        lock (gate)
        {
            previousTransport = transport;
            previousLifecycle = transportLifecycle;
            outbound = markActiveTransfersFailed && outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = markActiveTransfersFailed && inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
            transport = null;
            transportLifecycle = null;
        }

        if (previousTransport is not null)
        {
            previousTransport.FileTransferOfferReceived -= OnFileTransferOfferReceived;
            previousTransport.FileTransferAcceptReceived -= OnFileTransferAcceptReceived;
            previousTransport.FileTransferDeclineReceived -= OnFileTransferDeclineReceived;
            previousTransport.FileTransferSessionOpenReceived -= OnFileTransferSessionOpenReceived;
            previousTransport.FileTransferStartReceived -= OnFileTransferStartReceived;
            previousTransport.FileTransferChunkReceived -= OnFileTransferChunkReceived;
            previousTransport.FileTransferWindowUpdateReceived -= OnFileTransferWindowUpdateReceived;
            previousTransport.FileTransferMissingRangeReceived -= OnFileTransferMissingRangeReceived;
            previousTransport.FileTransferPressureStateReceived -= OnFileTransferPressureStateReceived;
            previousTransport.FileTransferCancelReceived -= OnFileTransferCancelReceived;
            previousTransport.FileTransferErrorReceived -= OnFileTransferErrorReceived;
            previousTransport.FileTransferCompleteReceived -= OnFileTransferCompleteReceived;
        }

        if (previousLifecycle is not null)
        {
            previousLifecycle.Rejected -= OnTransportRejectedOrDisconnected;
            previousLifecycle.Disconnected -= OnTransportRejectedOrDisconnected;
        }

        if (outbound is not null)
        {
            FailOutboundLocally(outbound, failureCode, failureMessage);
        }

        if (inbound is not null)
        {
            FailInboundLocally(inbound, failureCode, failureMessage);
        }
    }

    private IFileTransferSignalingTransport GetTransportOrThrow()
        => transport ?? throw new InvalidOperationException("No file-transfer transport is attached.");

    private SessionFileTransferSnapshot CreateSnapshot()
    {
        lock (gate)
        {
            return CreateSnapshotLocked();
        }
    }

    private SessionFileTransferSnapshot CreateSnapshotLocked()
        => new(
            Outbound: outboundTransfer?.ToSnapshot(),
            Inbound: inboundTransfer?.ToSnapshot());

    private FileTransferTransferSnapshot? CaptureCurrentOutboundSnapshot()
    {
        lock (gate)
        {
            return outboundTransfer?.ToSnapshot();
        }
    }

    private FileTransferTransferSnapshot? CaptureCurrentInboundSnapshot()
    {
        lock (gate)
        {
            return inboundTransfer?.ToSnapshot();
        }
    }

    private void RaiseTransferChanged(SessionFileTransferSnapshot snapshot)
    {
        try
        {
            TransferChanged?.Invoke(this, new SessionFileTransferSnapshotChangedEventArgs(snapshot));
        }
        catch
        {
        }
    }

    private static FileTransferSendDescriptor NormalizeSendDescriptor(FileTransferSendDescriptor descriptor, Func<string> transferIdFactory)
    {
        var normalizedFileName = NormalizeRequiredBounded(descriptor.FileName, FileTransferProtocol.MaxFileNameLength, nameof(descriptor.FileName));
        if (descriptor.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.FileSizeBytes), "File size must be positive.");
        }

        if (descriptor.ChunkSizeBytes is int chunkSizeBytes &&
            (chunkSizeBytes <= 0 || chunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.ChunkSizeBytes), $"Chunk size must be between 1 and {FileTransferProtocol.MaxChunkRawBytes} bytes.");
        }

        var transferId = string.IsNullOrWhiteSpace(descriptor.TransferId)
            ? NormalizeTransferId(transferIdFactory())
            : NormalizeTransferId(descriptor.TransferId);

        return descriptor with
        {
            FileName = normalizedFileName,
            TransferId = transferId,
            ChunkSizeBytes = descriptor.ChunkSizeBytes,
        };
    }

    private static string NormalizeTransferId(string? transferId)
    {
        return NormalizeRequiredBounded(transferId, FileTransferProtocol.MaxTransferIdLength, nameof(transferId));
    }

    private Task HandleIncomingWindowUpdateAsync(FileTransferWindowUpdateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        SessionFileTransferSnapshot? snapshot = null;
        string? repairAckLogEvent = null;
        int repairAckChunkIndex = -1;
        int repairRangeStartChunkIndex = -1;
        int repairRangeEndChunkExclusive = -1;
        int remoteGrantedUntilExclusive = 0;
        int remoteNextExpectedChunkIndex = 0;

        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (context.ChunkCount > 0 &&
                (message.NextExpectedChunkIndex < 0 ||
                 message.NextExpectedChunkIndex > context.ChunkCount ||
                 message.GrantedUntilChunkIndexExclusive < message.NextExpectedChunkIndex ||
                 message.GrantedUntilChunkIndexExclusive > context.ChunkCount))
            {
                return Task.CompletedTask;
            }

            if (message.NextExpectedChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                var previousNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
                context.RemoteNextExpectedChunkIndex = message.NextExpectedChunkIndex;
                PruneSentChunkCache(context, message.NextExpectedChunkIndex);
                if (context.RepairModeActive &&
                    context.RepairRangeStartChunkIndex is not null &&
                    context.RepairRangeEndChunkExclusive is not null &&
                    message.NextExpectedChunkIndex > previousNextExpectedChunkIndex)
                {
                    context.LastRepairAckObservedUtc = DateTimeOffset.UtcNow;
                    context.LastRepairEvidenceUtc = context.LastRepairAckObservedUtc;
                    context.LastRepairChunkSentIndex = null;
                    context.RepairSingleChunkModeActive = false;
                    repairAckLogEvent = "repair_chunk_acknowledged";
                    repairAckChunkIndex = Math.Min(
                        message.NextExpectedChunkIndex - 1,
                        context.RepairRangeEndChunkExclusive.Value - 1);
                    repairRangeStartChunkIndex = context.RepairRangeStartChunkIndex.Value;
                    repairRangeEndChunkExclusive = context.RepairRangeEndChunkExclusive.Value;
                    if (message.NextExpectedChunkIndex >= context.RepairRangeEndChunkExclusive.Value)
                    {
                        PromoteDeferredRepairRangeOrClearLocked(context);
                    }
                    else
                    {
                        context.RepairRangeStartChunkIndex = Math.Max(
                            message.NextExpectedChunkIndex,
                            context.RepairRangeStartChunkIndex.Value);
                        ReleaseRepairBatchLocked(context);
                        context.LastRepairSendUtc = null;
                        context.RepairSendCycle = 0;
                        ClearPendingRepairQueueLocked(context);
                    }

                    UpdateOutboundPressureDerivedStateLocked(context);
                }
            }

            if (message.GrantedUntilChunkIndexExclusive > context.RemoteGrantedUntilExclusive)
            {
                context.RemoteGrantedUntilExclusive = message.GrantedUntilChunkIndexExclusive;
            }

            context.LastWindowUpdateUtc = DateTimeOffset.UtcNow;
            TryExitRepairOnlyModeLocked(context);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
            remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
            snapshot = CreateSnapshotLocked();
        }

        LogWindowUpdateReceived(message);
        if (repairAckLogEvent is not null)
        {
            LogRepairChunkEvent(
                repairAckLogEvent,
                message.TransferId,
                message.SessionId,
                repairAckChunkIndex,
                repairRangeStartChunkIndex,
                repairRangeEndChunkExclusive,
                remoteNextExpectedChunkIndex,
                remoteGrantedUntilExclusive,
                context!.CurrentRepairBatchSize,
                pendingBatchCount: 0);
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context!, FileTransferDirection.Outbound);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingMissingRangeAsync(FileTransferMissingRangeV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            var context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var start = Math.Max(message.StartChunkIndex, context.RemoteNextExpectedChunkIndex);
            var end = Math.Min(message.EndChunkIndexExclusive, context.NextChunkIndexToRead);
            if (end > start)
            {
                var now = DateTimeOffset.UtcNow;
                EnterRepairOnlyModeLocked(context);
                var hasActiveRange =
                    context.RepairModeActive &&
                    context.RepairRangeStartChunkIndex is not null &&
                    context.RepairRangeEndChunkExclusive is not null &&
                    context.RemoteNextExpectedChunkIndex < context.RepairRangeEndChunkExclusive.Value;
                var activeRangeStart = context.RepairRangeStartChunkIndex ?? -1;
                var activeRangeEnd = context.RepairRangeEndChunkExclusive ?? -1;
                var repeatsActiveRange =
                    hasActiveRange &&
                    start == activeRangeStart &&
                    end == activeRangeEnd;
                var evolvedActiveRange =
                    hasActiveRange &&
                    start >= activeRangeStart &&
                    end <= activeRangeEnd &&
                    (start > activeRangeStart || end < activeRangeEnd);
                var canReplaceActiveRange =
                    !hasActiveRange ||
                    start <= activeRangeStart;
                if (repeatsActiveRange)
                {
                    context.LastRepairRangeRequestedUtc = now;
                    context.RepairSingleChunkModeActive = true;
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (evolvedActiveRange)
                {
                    context.RepairRangeStartChunkIndex = start;
                    context.RepairRangeEndChunkExclusive = end;
                    context.LastRepairRangeRequestedUtc = now;
                    context.LastRepairEvidenceUtc = now;
                    context.LastRepairSendUtc = null;
                    context.LastRepairChunkSentIndex = null;
                    context.RepairSendCycle = 0;
                    context.RepairSingleChunkModeActive = true;
                    ReleaseRepairBatchLocked(context);
                    ClearPendingRepairQueueLocked(context);
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (canReplaceActiveRange)
                {
                    ActivateRepairRangeLocked(context, start, end);
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (context.DeferredRepairRangeStartChunkIndex is null ||
                         start <= context.DeferredRepairRangeStartChunkIndex.Value)
                {
                    context.DeferredRepairRangeStartChunkIndex = start;
                    context.DeferredRepairRangeEndChunkExclusive = end;
                }
                else if (context.DeferredRepairRangeEndChunkExclusive is not null)
                {
                    context.DeferredRepairRangeEndChunkExclusive = Math.Max(
                        context.DeferredRepairRangeEndChunkExclusive.Value,
                        end);
                }
            }

            LogMissingRangeReceived(
                message,
                context.RemoteNextExpectedChunkIndex,
                context.NextChunkIndexToRead - 1);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return Task.CompletedTask;
    }

}
