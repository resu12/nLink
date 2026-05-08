using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private bool IsTransferDegradedLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            outboundTransfer.PullSessionDegraded) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            (inboundTransfer.PullSessionDegraded ||
             inboundTransfer.BulkFallbackModeActive ||
             inboundTransfer.DegradedRepairModeActive));

    private bool IsCatchUpOnlyPressureActiveLocked()
        => false;

    private void UpdateOutboundPressureDerivedStateLocked(OutboundTransferContext context)
    {
        context.CurrentRepairBatchSize = RepairBatchSize;
    }

    private static void UpdateOutboundAcknowledgedProgressLocked(OutboundTransferContext context)
    {
        var acknowledgedChunks = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acknowledgedBytes = acknowledgedChunks >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)acknowledgedChunks * context.ChunkSizeBytes);

        context.BytesTransferred = acknowledgedBytes;
        context.ChunksTransferred = acknowledgedChunks;
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
        IFileTransferDataSession? dataSessionForCancel = null;
        string sessionId;
        string transferId;
        string? normalizedCancelReason;

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
            context.UserPaused = false;
            context.UserPauseReason = null;
            context.UserPausedSinceUtc = null;
            context.PeerPaused = false;
            context.PeerPauseReason = null;
            context.PeerPausedSinceUtc = null;
            if (terminalState == FileTransferTransferState.Completed)
            {
                context.BytesTransferred = context.FileSizeBytes;
                context.ChunksTransferred = context.ChunkCount;
                context.BytesAcceptedForTransport = context.FileSizeBytes;
                context.ChunksAcceptedForTransport = context.ChunkCount;
            }

            snapshot = CreateSnapshotLocked();
            shouldNotifyPeer = notifyPeer;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedCancelReason = NormalizeReason(cancelReason) ?? CanceledReason;
            if (terminalState == FileTransferTransferState.Canceled && shouldNotifyPeer)
            {
                dataSessionForCancel = context.DetachDataSession();
            }
        }

        RaiseTransferChanged(snapshot);
        try
        {
            if (shouldNotifyPeer)
            {
                if (terminalState == FileTransferTransferState.Canceled)
                {
                    await SendCancelAsync(sessionId, transferId, normalizedCancelReason, CancellationToken.None).ConfigureAwait(false);
                }
                else if (terminalState == FileTransferTransferState.Failed)
                {
                    await SendErrorAsync(sessionId, transferId, context.ErrorCode ?? InvalidStateErrorCode, context.StatusMessage, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (dataSessionForCancel is not null)
            {
                await TrySendV4CancelFrameAsync(
                        dataSessionForCancel,
                        sessionId,
                        transferId,
                        normalizedCancelReason,
                        FileTransferDirection.Outbound,
                        "outbound_terminal",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                StartV4CancelDataFrameRetryLoop(
                    dataSessionForCancel,
                    sessionId,
                    transferId,
                    normalizedCancelReason,
                    FileTransferDirection.Outbound,
                    "outbound_terminal");
            }
            else
            {
                dataSessionForCancel?.Dispose();
            }
        }
        finally
        {
            context.DisposeResources();
        }

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
        IFileTransferDataSession? dataSessionForCancel = null;
        string? normalizedCancelReason;

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
            context.UserPaused = false;
            context.UserPauseReason = null;
            context.UserPausedSinceUtc = null;
            context.PeerPaused = false;
            context.PeerPauseReason = null;
            context.PeerPausedSinceUtc = null;
            snapshot = CreateSnapshotLocked();
            shouldSendError = sendError;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
            normalizedCancelReason = NormalizeReason(cancelReason) ?? CanceledReason;
            if (terminalState == FileTransferTransferState.Canceled)
            {
                dataSessionForCancel = context.DetachDataSession();
            }
        }

        if (terminalState == FileTransferTransferState.Completed &&
            context.PullTransportRebindGeneration > 0)
        {
            LogInboundTransportRebindRecovered(
                context,
                "terminal_completed",
                context.PullTransportRebindGeneration,
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex,
                context.BytesTransferred);
        }

        RaiseTransferChanged(snapshot);
        try
        {
            if (terminalState == FileTransferTransferState.Declined)
            {
                await SendDeclineAsync(sessionId, transferId, cancelReason ?? DeclinedReason, ct).ConfigureAwait(false);
            }
            else if (terminalState == FileTransferTransferState.Canceled)
            {
                await SendCancelAsync(sessionId, transferId, normalizedCancelReason, CancellationToken.None).ConfigureAwait(false);
            }
            else if (shouldSendError)
            {
                await SendErrorAsync(sessionId, transferId, normalizedErrorCode ?? InvalidStateErrorCode, errorMessage ?? statusMessage, CancellationToken.None).ConfigureAwait(false);
            }

            if (dataSessionForCancel is not null)
            {
                await TrySendV4CancelFrameAsync(
                        dataSessionForCancel,
                        sessionId,
                        transferId,
                        normalizedCancelReason,
                        FileTransferDirection.Inbound,
                        "inbound_terminal",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                StartV4CancelDataFrameRetryLoop(
                    dataSessionForCancel,
                    sessionId,
                    transferId,
                    normalizedCancelReason,
                    FileTransferDirection.Inbound,
                    "inbound_terminal");
            }
        }
        finally
        {
            context.DisposeResources();
        }

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
            context.StatusMessage = context.UserPaused
                ? "Transfer paused."
                : context.PeerPaused
                    ? "Peer paused transfer."
                    : statusMessage;
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
        var normalizedReason = NormalizeReason(reason) ?? CanceledReason;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            await TrySendCancelControlOnceAsync(sessionId, transferId, normalizedReason, attempt: 1, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(normalizedReason)}; path=control; error=timeout");
        }

        _ = Task.Run(
            () => RunCancelControlRetryLoopAsync(sessionId, transferId, normalizedReason),
            CancellationToken.None);
    }

    private async Task RunCancelControlRetryLoopAsync(string sessionId, string transferId, string? reason)
    {
        for (var index = 0; index < CancelRetryDelaysMs.Length; index++)
        {
            try
            {
                await Task.Delay(CancelRetryDelaysMs[index]).ConfigureAwait(false);
                await TrySendCancelControlOnceAsync(
                        sessionId,
                        transferId,
                        reason,
                        attempt: index + 2,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"cancel retry loop failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task TrySendCancelControlOnceAsync(
        string sessionId,
        string transferId,
        string? reason,
        int attempt,
        CancellationToken ct)
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
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_cancel_control_sent; transfer_id={transferId}; session_id={sessionId}; attempt={attempt}");
            if (attempt == 1)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_lifecycle_priority_sent; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=control; attempt={attempt}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"cancel send failed (attempt={attempt}): {ex.Message}");
            if (attempt == 1)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=control; error={ex.GetType().Name}");
            }
        }
    }

    private async Task TrySendV4CancelFrameAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        string source,
        CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(CancelDataFrameBestEffortTimeoutMs));
            await dataSession.SendAsync(
                    new FileTransferCancelFrameV5
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        Reason = NormalizeReason(reason),
                    },
                    timeout.Token)
                .ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_cancel_frame_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=cancel; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=redundant_data_frame; direction={direction.ToString().ToLowerInvariant()}; source={source}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error=timeout");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_cancel_frame_send_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; error={FormatProtocolLogValue(ex.Message)}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=cancel; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; source={source}; path=redundant_data_frame; error={ex.GetType().Name}");
        }
    }

    private void StartV4CancelDataFrameRetryLoop(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        string source)
    {
        _ = Task.Run(
            () => RunV4CancelDataFrameRetryLoopAsync(dataSession, sessionId, transferId, reason, direction, source),
            CancellationToken.None);
    }

    private async Task RunV4CancelDataFrameRetryLoopAsync(
        IFileTransferDataSession dataSession,
        string sessionId,
        string transferId,
        string? reason,
        FileTransferDirection direction,
        string source)
    {
        try
        {
            for (var index = 0; index < CancelDataFrameRetryDelaysMs.Length; index++)
            {
                await Task.Delay(CancelDataFrameRetryDelaysMs[index]).ConfigureAwait(false);
                await TrySendV4CancelFrameAsync(
                        dataSession,
                        sessionId,
                        transferId,
                        reason,
                        direction,
                        $"{source}_retry_{index + 2}",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                dataSession.Dispose();
            }
            catch
            {
            }
        }
    }

    private async Task SendErrorAsync(string sessionId, string transferId, string errorCode, string? message, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferErrorAsync(
                new FileTransferErrorV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                },
                timeout.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_sent; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error_code={NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error=timeout");
        }
        catch (Exception ex)
        {
            Warn($"error send failed: {ex.Message}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_lifecycle_priority_send_failed; kind=error; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(NormalizeReason(message) ?? "(none)")}; path=control; error={ex.GetType().Name}");
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
}
