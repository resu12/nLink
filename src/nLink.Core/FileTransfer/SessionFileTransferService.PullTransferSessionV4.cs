using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using NLink.Core.Configuration;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundV4SenderAsync(OutboundTransferContext context)
    {
        IFileTransferDataSession? dataSession = null;
        try
        {
            var currentTransport = GetTransportOrThrow();
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = context.ChunkSizeBytes,
                InitialPipelineDepth = V4SenderPumpDepth,
            };

            dataSession = await currentTransport
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
                context.PullCurrentPipelineDepth = V4SenderPumpDepth;
                context.RemoteNextExpectedChunkIndex = 0;
                context.RemoteGrantedUntilExclusive = 0;
                context.ChunksAcceptedForTransport = 0;
                context.BytesAcceptedForTransport = 0;
                context.V4LastStateEpoch = -1;
                context.V4TerminalReady = false;
                context.V4MixedScreenShareTransfer = context.V4MixedScreenShareTransfer || IsV4MixedScreenShareActive();
                context.V4SenderPumpLastWakeReason = "startup";
                context.V4SenderCreditExhaustedSinceUtc = null;
                context.PullV4SenderPumpRepairQueue.Clear();
                context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
                context.PullV4SenderPumpRepairRequests.Clear();
                context.PullSentChunkCache.Clear();
                context.PullSentChunkCacheBytes = 0;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_sender_started; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; pipeline_depth={V4SenderPumpDepth}; pending_bytes_limit={V4SenderPumpPendingBytes}");

            UpdateOutboundState(context, FileTransferTransferState.AwaitingStart, 0, 0, "Starting V4 file transfer.");
            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                reason: $"role={sessionOpen.SessionRole}; protocol_version={sessionOpen.ProtocolVersion}; chunk_size_bytes={sessionOpen.ChunkSizeBytes}; pipeline_depth={sessionOpen.InitialPipelineDepth}");

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);
            InitializeOutboundSenderRepairCachePolicy(context, stream.CanSeek);

            var manifest = new FileTransferManifestFrameV6
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
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_manifest_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size_bytes={context.FileSizeBytes}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}");
            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for V6 receiver state.");
            if (context.UserPaused)
            {
                await SendOutboundV4PauseControlAsync(context, "user_paused_initial").ConfigureAwait(false);
                await SendOutboundV4PauseStateAsync(context, "user_paused_initial").ConfigureAwait(false);
            }

            var senderPumpTask = RunOutboundV4SenderPumpAsync(context, stream, dataSession);
            Task<FileTransferDataFrame>? pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                pendingReceiveTask ??= dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();

                var completed = await Task.WhenAny(
                    pendingReceiveTask,
                    senderPumpTask,
                    Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
                if (completed == senderPumpTask)
                {
                    await senderPumpTask.ConfigureAwait(false);
                    return;
                }

                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        await StopOutboundV4SenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    if (TryGetOutboundV4PeerSilenceFailure(context, out var silenceStatus))
                    {
                        ForceLogOutboundV4SenderPumpSummary(context);
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Failed,
                            errorCode: DisconnectedErrorCode,
                            statusMessage: silenceStatus,
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        await StopOutboundV4SenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (!IsFrameForContext(context, frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "session_or_transfer_mismatch_v4");
                    continue;
                }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "protocol_not_v6");
                    continue;
                }

                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                    {
                        var now = DateTimeOffset.UtcNow;
                        context.PullV4LastPeerFrameReceivedUtc = now;
                        context.V6LastPeerLivenessUtc = now;
                    }
                }

                switch (frame)
                {
                    case FileTransferTransportEpochFrameV6 handoff:
                        ApplyOutboundV5HandoffFrame(context, handoff);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferFrontierRequestFrameV6 repairRequest:
                        ApplyOutboundV5RepairRequest(context, repairRequest);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferRepairProofFrameV6 repairProof:
                        ApplyOutboundV5RepairProof(context, repairProof);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferStateFrameV4 state:
                        ApplyOutboundV4State(context, state);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferCompleteFrameV4 complete:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=complete; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; file_size_bytes={complete.FileSizeBytes}");
                        break;
                    case FileTransferCancelFrameV4 cancel:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=cancel; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; cancel_reason={FormatProtocolLogValue(NormalizeReason(cancel.Reason) ?? CanceledReason)}");
                        break;
                    case FileTransferErrorFrameV4 error:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=error; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; error_code={NormalizeErrorCode(error.ErrorCode) ?? InvalidStateErrorCode}");
                        break;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame_v4");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
            await FailOutboundV4Async(
                context,
                dataSession,
                errorCode,
                ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: true).ConfigureAwait(false);
        }
    }

    private bool TryGetOutboundV4PeerSilenceFailure(
        OutboundTransferContext context,
        out string statusMessage)
    {
        statusMessage = "Receiver stopped responding.";
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.UserPaused ||
                context.PeerPaused ||
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
            {
                return false;
            }

            if (context.PullTransportPaused &&
                !IsTunaFallbackTransportPauseReason(context.PullTransportPauseReason))
            {
                return false;
            }

            var lastPeerFrameUtc = context.PullV4LastPeerFrameReceivedUtc ??
                context.PullTransportRebindStartedUtc ??
                context.PullTransportLastSafetyReplayUtc;
            if (lastPeerFrameUtc is null)
            {
                return false;
            }

            var postFallback = IsOutboundPostTunaRecoveryActiveLocked(context);
            var silence = DateTimeOffset.UtcNow - lastPeerFrameUtc.Value;
            var timeout = ResolveV4PeerSilenceTimeout(postFallback);
            if (silence < timeout)
            {
                return false;
            }

            var hasUnacknowledgedData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.PullV4SenderPumpRepairRequests.Count > 0;
            if (!hasUnacknowledgedData)
            {
                return false;
            }

            var reason = postFallback
                ? "post_tuna_fallback_peer_silence"
                : "peer_silence";
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_peer_feedback_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}");
            return true;
        }
    }

    private static TimeSpan ResolveV4PeerSilenceTimeout(bool postFallback)
        => V4PeerSilenceTimeoutOverrideForTests ??
           (postFallback ? PullV4PostFallbackPeerSilenceTimeout : PullV4PeerSilenceTimeout);

    private static bool IsOutboundPostTunaRecoveryActiveLocked(OutboundTransferContext context)
        => context.PullPostTunaRecoveryActive || context.PullTransportRebindGeneration > 0;

    private static bool IsInboundPostTunaRecoveryActiveLocked(InboundTransferContext context)
        => context.PullPostTunaRecoveryActive || context.PullTransportRebindGeneration > 0;

    private static int GetOutboundPostTunaRecoveryGenerationLocked(OutboundTransferContext context)
        => context.PullTransportRebindGeneration > 0
            ? context.PullTransportRebindGeneration
            : Math.Max(0, context.PullPostTunaRecoveryGeneration);

    private static int GetInboundPostTunaRecoveryGenerationLocked(InboundTransferContext context)
        => context.PullTransportRebindGeneration > 0
            ? context.PullTransportRebindGeneration
            : Math.Max(0, context.PullPostTunaRecoveryGeneration);

    private void MarkInboundV4PeerFrameReceived(InboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                var now = DateTimeOffset.UtcNow;
                context.PullV4LastPeerFrameReceivedUtc = now;
                context.V6LastPeerLivenessUtc = now;
            }
        }
    }

    private bool TryGetInboundV4PeerSilenceFailure(
        InboundTransferContext context,
        out string statusMessage)
    {
        statusMessage = "Sender stopped responding.";
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.UserPaused ||
                context.PeerPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6)
            {
                return false;
            }

            if (context.PullTransportPaused &&
                !IsTunaFallbackTransportPauseReason(context.PullTransportPauseReason))
            {
                return false;
            }

            var lastPeerFrameUtc = context.PullV4LastPeerFrameReceivedUtc ??
                context.PullLastProgressUtc ??
                context.LastUsefulBulkProgressUtc ??
                context.PullTransportRebindStartedUtc;
            if (lastPeerFrameUtc is null)
            {
                return false;
            }

            var postFallback = IsInboundPostTunaRecoveryActiveLocked(context);
            var silence = DateTimeOffset.UtcNow - lastPeerFrameUtc.Value;
            var timeout = ResolveV4PeerSilenceTimeout(postFallback);
            if (silence < timeout)
            {
                return false;
            }

            var reason = postFallback
                ? "post_tuna_fallback_peer_silence"
                : "peer_silence";
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_peer_receive_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}; missing_range_count={(context.NextChunkIndex <= context.PullHighestReceivedChunkIndex ? 1 : 0)}");
            return true;
        }
    }

    private async Task RunOutboundV4SenderPumpAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession)
    {
        while (true)
        {
            context.LifetimeCts.Token.ThrowIfCancellationRequested();

            PullV4QueuedRepairSend? repairSend = null;
            List<int>? chunkIndicesToSend = null;
            Task? waitForSignal = null;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return;
                }

                if (context.UserPaused || context.PeerPaused)
                {
                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                    waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                }
                else if (context.PullV4SenderPumpRepairQueue.Count > 0)
                {
                    while (context.PullV4SenderPumpRepairQueue.Count > 0 && repairSend is null)
                    {
                        var queuedRepair = context.PullV4SenderPumpRepairQueue.Dequeue();
                        foreach (var chunkIndex in queuedRepair.ChunkIndices)
                        {
                            context.PullV4SenderPumpRepairQueuedChunkIndices.Remove(chunkIndex);
                        }

                        repairSend = RevalidateQueuedV4RepairSendLocked(context, queuedRepair);
                        if (repairSend is null)
                        {
                            if (context.PullV4SenderPumpRepairRequests.TryGetValue(queuedRepair.RepairRequestKey, out var skippedRepairState))
                            {
                                skippedRepairState.Queued = false;
                                skippedRepairState.LastSentUtc = DateTimeOffset.UtcNow;
                            }

                            continue;
                        }

                        if (context.PullV4SenderPumpRepairRequests.TryGetValue(repairSend.RepairRequestKey, out var repairState))
                        {
                            repairState.Queued = false;
                            repairState.InFlight = true;
                        }
                    }
                }
                else if (!context.V4TerminalReady)
                {
                    if (IsV5TransportHandoffBlockingTail(context.V5TransportHandoff))
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (context.V5TransportHandoff is { State: not V5TransportHandoffState.Recovered } handoff &&
                            now - GetV5TransportHandoffActivityUtc(handoff) >= V5TransportHandoffWaitingTimeout &&
                            !HasOutboundV4RepairWorkInProgressLocked(context))
                        {
                            TrySetV5TransportHandoffState(
                                handoff,
                                FileTransferDirection.Outbound,
                                context.TransferId,
                                context.SessionId,
                                V5TransportHandoffState.WaitingForRegularNkn,
                                "proof_timeout",
                                context.RemoteNextExpectedChunkIndex,
                                Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                            context.StatusMessage = "Waiting for regular NKN";
                        }

                        context.PullSenderSendWaitCountRecent++;
                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderPumpLastWakeReason = "v6_handoff_tail_blocked";
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V5TransportHandoff!.EpochId}; state={FormatV5TransportHandoffState(context.V5TransportHandoff.State)}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; repair_queue_depth={context.PullV4SenderPumpRepairQueue.Count}");
                        MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, "v6_handoff_tail_blocked");
                        MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                        waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                    }
                    else
                    {
                    var startChunk = context.ChunksAcceptedForTransport;
                    var grantedUntilExclusive = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
                    if (grantedUntilExclusive > startChunk)
                    {
                        if (context.PullSenderFeedCreditWaitStartedUtc is not null)
                        {
                            context.PullSenderFeedCreditWaitMsRecent += (long)Math.Max(
                                0,
                                (DateTimeOffset.UtcNow - context.PullSenderFeedCreditWaitStartedUtc.Value).TotalMilliseconds);
                            context.PullSenderFeedCreditWaitStartedUtc = null;
                        }
                        context.V4SenderCreditExhaustedSinceUtc = null;

                        var maxNormalChunksThisPass = Math.Max(
                            1,
                            (int)Math.Ceiling(V4SenderPumpPendingBytes / (double)Math.Max(1, context.ChunkSizeBytes)));
                        var chunkCountThisPass = Math.Min(
                            grantedUntilExclusive - startChunk,
                            Math.Min(maxNormalChunksThisPass, V4NormalSendQuantumChunks));
                        chunkIndicesToSend = Enumerable.Range(startChunk, chunkCountThisPass).ToList();
                    }
                    else
                    {
                        context.PullSenderSendWaitCountRecent++;
                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                        MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                        waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                    }
                    }
                }
                else
                {
                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                    waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                }
            }

            if (repairSend is not null)
            {
                if (repairSend.ChunkIndices.Count > 0)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context))
                        {
                            context.V4SenderPumpLastRepairRequestKey = repairSend.RepairRequestKey;
                        }
                    }

                    await SendChunkIndicesV4Async(
                        context,
                        stream,
                        dataSession,
                        repairSend.ChunkIndices,
                        repairSend: true,
                        repairRequestKey: repairSend.RepairRequestKey,
                        protocolRepairRequestId: repairSend.ProtocolRepairRequestId,
                        protocolPriority: repairSend.ProtocolPriority,
                        protocolRecoveryMode: repairSend.ProtocolRecoveryMode,
                        repairDeliveryMode: repairSend.DeliveryMode,
                        repairDeliveryReason: repairSend.DeliveryEscalationReason).ConfigureAwait(false);
                }

                var sentUtc = DateTimeOffset.UtcNow;
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context))
                    {
                        if (context.PullV4SenderPumpRepairRequests.TryGetValue(repairSend.RepairRequestKey, out var repairState))
                        {
                            repairState.InFlight = false;
                            repairState.LastSentUtc = sentUtc;
                            repairState.SentCount++;
                            repairState.LastSentRemoteFrontierChunkIndex = repairSend.RemoteNextExpectedChunkIndex;
                        }
                    }
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; range_count={repairSend.RangeCount}; requested_chunk_count={repairSend.RequestedChunkCount}; sent_chunk_count={repairSend.ChunkIndices.Count}; sent_chunk_indices={FormatV4ChunkIndicesForLog(repairSend.ChunkIndices)}; transport_sent_chunk_count={repairSend.ChunkIndices.Count * V4RepairBatchSendAttempts}; repair_batch_send_attempt_count={V4RepairBatchSendAttempts}; repair_delivery_mode={FormatV4RepairDeliveryMode(repairSend.DeliveryMode)}; repair_delivery_escalation_reason={repairSend.DeliveryEscalationReason}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; frontier_tail_repair={(repairSend.FrontierTailRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair={repairSend.CreditExhaustedTimeMsAtDecision}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}; skipped_obsolete_count={repairSend.SkippedObsoleteCount}; skipped_future_count={repairSend.SkippedFutureCount}; skipped_out_of_bounds_count={repairSend.SkippedOutOfBoundsCount}; sent_unix_ms={sentUtc.ToUnixTimeMilliseconds()}; receiver_proof_status=pending");
                continue;
            }

            if (chunkIndicesToSend is not null)
            {
                await SendChunkIndicesV4Async(context, stream, dataSession, chunkIndicesToSend, repairSend: false).ConfigureAwait(false);
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
                            MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, "post_fallback_sender_wait");
                            MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                        }
                    }
                }
            }
        }
    }

    private void MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(
        OutboundTransferContext context,
        string reason)
    {
        var recoveryGeneration = GetOutboundPostTunaRecoveryGenerationLocked(context);
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            recoveryGeneration <= 0 ||
            (!context.PullPostTunaRecoveryActive && recoveryGeneration <= context.LastRecoveredV5TransportHandoffEpoch) ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullSourceCanSeek ||
            context.RemoteNextExpectedChunkIndex >= context.ChunkCount)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.PullTransportLastSafetyReplayGeneration == context.PullTransportRebindGeneration &&
            context.PullTransportLastSafetyReplayUtc is { } lastReplayUtc &&
            now - lastReplayUtc < PullTransportRebindSafetyReplayRearmCooldown)
        {
            return;
        }

        QueueOutboundV4TransportRebindSafetyReplayLocked(context, reason, allowRepeatForSameGeneration: true);
    }

    private void ApplyOutboundV4State(OutboundTransferContext context, FileTransferStateFrameV4 state)
    {
        SessionFileTransferSnapshot? snapshot = null;
        var shouldEnqueueRepairs = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var previousEpoch = context.V4LastStateEpoch;
            var previousRemoteNext = context.RemoteNextExpectedChunkIndex;
            var previousGrant = context.RemoteGrantedUntilExclusive;
            if (state.Epoch < previousEpoch)
            {
                var staleCommitted = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, context.ChunkCount);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={previousEpoch}; stale=1; applied=0; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; terminal_ready={(state.TerminalReady ? 1 : 0)}");
                if (state.MissingRanges.Count > 0)
                {
                    if (ShouldEnqueueRepairOnlyForRebindStateLocked(context, state, staleCommitted))
                    {
                        shouldEnqueueRepairs = true;
                        ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
                            context,
                            context.RemoteNextExpectedChunkIndex,
                            "stale_frontier_rebind");
                        QueueOutboundV4TransportRebindSafetyReplayLocked(context, "stale_frontier_rebind", allowRepeatForSameGeneration: true);
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_rebind_duplicate_state_repair_enqueued; transfer_id={context.TransferId}; session_id={context.SessionId}; state_kind=stale; epoch={state.Epoch}; previous_epoch={previousEpoch}; stale_contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; current_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; missing_range_count={state.MissingRanges.Count}");
                    }
                    else
                    {
                        var reason = staleCommitted == context.RemoteNextExpectedChunkIndex
                        ? "stale_epoch"
                        : "frontier_moved";
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; reason={reason}; stale_contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; current_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; missing_range_count={state.MissingRanges.Count}");
                    }
                }
            }
            else
            {
                var normalizedCommitted = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, context.ChunkCount);
                var frameCredit = Math.Clamp(state.CreditUntilChunkIndexExclusive, normalizedCommitted, context.ChunkCount);
                var normalizedCredit = frameCredit < context.RemoteGrantedUntilExclusive
                    ? Math.Max(context.ChunksAcceptedForTransport, frameCredit)
                    : Math.Max(context.RemoteGrantedUntilExclusive, frameCredit);
                if (state.Epoch == previousEpoch)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={previousEpoch}; duplicate=1; applied=0; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; terminal_ready={(state.TerminalReady ? 1 : 0)}");
                    if (ShouldEnqueueRepairOnlyForRebindStateLocked(context, state, normalizedCommitted))
                    {
                        ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
                            context,
                            context.RemoteNextExpectedChunkIndex,
                            "duplicate_frontier_rebind");
                        QueueOutboundV4TransportRebindSafetyReplayLocked(context, "duplicate_frontier_rebind", allowRepeatForSameGeneration: true);
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_rebind_duplicate_state_repair_enqueued; transfer_id={context.TransferId}; session_id={context.SessionId}; state_kind=duplicate; epoch={state.Epoch}; previous_epoch={previousEpoch}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; current_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; missing_range_count={state.MissingRanges.Count}");
                        EnqueueV4RepairsFromState(context, state);
                        return;
                    }
                    else
                    {
                        return;
                    }
                }

                var normalizedPauseReason = NormalizeReason(state.TransferPauseReason);
                var peerPauseChanged = state.TransferPaused != context.PeerPaused ||
                    !string.Equals(normalizedPauseReason, context.PeerPauseReason, StringComparison.Ordinal);

                context.V4LastStateEpoch = Math.Max(context.V4LastStateEpoch, state.Epoch);
                context.RemoteNextExpectedChunkIndex = Math.Max(context.RemoteNextExpectedChunkIndex, normalizedCommitted);
                context.RemoteGrantedUntilExclusive = normalizedCredit;
                context.ChunksTransferred = Math.Max(context.ChunksTransferred, context.RemoteNextExpectedChunkIndex);
                context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(context.FileSizeBytes, state.BytesCommitted));
                context.V4TerminalReady |= state.TerminalReady;
                UpdateOutboundV5TransportHandoffFromStateLocked(context, state);
                UpdateOutboundV4PostRebindFrontierOnlyRecoveryLocked(context, state);
                if (context.RemoteNextExpectedChunkIndex > previousRemoteNext)
                {
                    ClearObsoleteOutboundV4RepairWorkAfterFrontierAdvanceLocked(
                        context,
                        previousRemoteNext,
                        context.RemoteNextExpectedChunkIndex);
                }

                if (peerPauseChanged)
                {
                    context.PeerPaused = state.TransferPaused;
                    context.PeerPauseReason = normalizedPauseReason;
                    context.PeerPausedSinceUtc = state.TransferPaused ? DateTimeOffset.UtcNow : null;
                    if (!state.TransferPaused)
                    {
                        ResetOutboundV4AcceptedForPeerResumeLocked(context);
                    }
                }

                context.V4SenderPumpLastWakeReason = peerPauseChanged
                    ? state.TransferPaused
                        ? "peer_user_paused"
                        : "peer_user_resumed"
                    : state.TerminalReady
                        ? "state_terminal_ready"
                        : state.MissingRanges.Count > 0
                            ? "state_missing_ranges"
                            : normalizedCredit > previousGrant
                                ? "state_credit"
                                : normalizedCredit < previousGrant
                                    ? "state_credit_reduced"
                                    : "state_progress";
                context.PullV4LastGrantReceivedUtc = DateTimeOffset.UtcNow;
                TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
                foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
                {
                    context.SentAwaitingAck.Remove(chunkIndex);
                }

                if (!context.UserPaused && !context.PeerPaused)
                {
                    context.StatusMessage = context.V4TerminalReady
                        ? "Waiting for V6 receiver verification."
                        : "Receiver granted V4 transfer credit.";
                }
                else if (context.PeerPaused && !context.UserPaused)
                {
                    context.StatusMessage = "Peer paused transfer.";
                }

                LogOutboundV4StateReceivedLocked(
                    context,
                    state,
                    previousEpoch,
                    previousRemoteNext,
                    previousGrant,
                    applied: true,
                    stale: false,
                    duplicate: state.Epoch == previousEpoch);
                snapshot = CreateSnapshotLocked();
                shouldEnqueueRepairs = true;
            }
        }

        if (shouldEnqueueRepairs)
        {
            EnqueueV4RepairsFromState(context, state);
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private static void ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
        OutboundTransferContext context,
        int frontierChunkIndex,
        string reason)
    {
        var recoveryGeneration = GetOutboundPostTunaRecoveryGenerationLocked(context);
        if (recoveryGeneration <= 0 ||
            frontierChunkIndex < 0 ||
            frontierChunkIndex >= context.ChunkCount)
        {
            return;
        }

        if (context.PullTransportFrontierOnlyRepairActive &&
            context.PullTransportFrontierOnlyRepairStartChunkIndex == frontierChunkIndex)
        {
            return;
        }

        var previousStart = context.PullTransportFrontierOnlyRepairStartChunkIndex;
        context.PullTransportFrontierOnlyRepairActive = true;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = frontierChunkIndex;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_frontier_only_started; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; frontier_chunk_index={frontierChunkIndex}; previous_frontier_only_start_chunk_index={previousStart}; stable_advance_required_chunks={PullTransportRebindFrontierOnlyStableAdvanceChunks}");
    }

    private static void UpdateOutboundV4PostRebindFrontierOnlyRecoveryLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        var recoveryGeneration = GetOutboundPostTunaRecoveryGenerationLocked(context);
        if (!context.PullTransportFrontierOnlyRepairActive ||
            recoveryGeneration <= 0)
        {
            return;
        }

        var activeStart = Math.Max(0, context.PullTransportFrontierOnlyRepairStartChunkIndex);
        var advancedChunks = context.RemoteNextExpectedChunkIndex - activeStart;
        if (state.MissingRanges.Count > 0 ||
            advancedChunks < PullTransportRebindFrontierOnlyStableAdvanceChunks)
        {
            return;
        }

        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_frontier_only_recovered; transfer_id={context.TransferId}; session_id={context.SessionId}; rebind_generation={recoveryGeneration}; active_start_chunk_index={activeStart}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; advanced_chunks={advancedChunks}; missing_range_count={state.MissingRanges.Count}");
    }

    private static void UpdateOutboundV5TransportHandoffFromStateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        if (context.V5TransportHandoff is null)
        {
            return;
        }

        var handoff = context.V5TransportHandoff;
        if (handoff.EpochId <= context.LastRecoveredV5TransportHandoffEpoch &&
            !IsV5TransportHandoffBlockingTail(handoff))
        {
            return;
        }

        if (handoff.State == V5TransportHandoffState.Recovered)
        {
            CompleteOutboundV5TransportHandoffLocked(
                context,
                "recovered_state_observed",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
            return;
        }

        var v5State = state as FileTransferReceiverStateFrameV6;
        if (v5State is not null &&
            v5State.TransportEpoch > 0 &&
            v5State.TransportEpoch != handoff.EpochId)
        {
            if (!TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    v5State.TransportEpoch,
                    FileTransferProtocol.ReceiverStateFrameTypeV6,
                    "peer_state_epoch_conflict",
                    Math.Clamp(v5State.ContiguousCommittedChunkIndex, 0, context.ChunkCount),
                    v5State.DurableReceivedHighestChunkIndex,
                    requireFrontierEvidence: v5State.MissingRanges.Count > 0))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.ReceiverStateFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={v5State.TransportEpoch}; current_transport_epoch={handoff.EpochId}");
                return;
            }

            handoff = context.V5TransportHandoff!;
        }

        handoff.LastProofUtc = DateTimeOffset.UtcNow;
        var frontierMissing = state.MissingRanges.Any(range =>
            range.StartChunkIndex <= context.RemoteNextExpectedChunkIndex &&
            context.RemoteNextExpectedChunkIndex < range.StartChunkIndex + range.ChunkCount);
        if (context.RemoteNextExpectedChunkIndex <= handoff.StartingCommittedChunkIndex || frontierMissing)
        {
            TrySetV5TransportHandoffState(
                handoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V5TransportHandoffState.FrontierRepairOnly,
                frontierMissing ? "frontier_missing_state" : "state_proof",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
            return;
        }

        var frontierLagChunks = Math.Max(
            0,
            state.DurableReceivedHighestChunkIndex - context.RemoteNextExpectedChunkIndex + 1);
        if (state.MissingRanges.Count > 0 || frontierLagChunks > 0)
        {
            TrySetV5TransportHandoffState(
                handoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V5TransportHandoffState.BackfillRepair,
                state.MissingRanges.Count > 0
                    ? "frontier_proof_with_backfill"
                    : "frontier_proof_with_sparse_backfill",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
            return;
        }

        if (context.RemoteNextExpectedChunkIndex != handoff.LastObservedCommittedChunkIndex ||
            state.DurableReceivedHighestChunkIndex != handoff.LastObservedHighestChunkIndex)
        {
            handoff.DurableProgressSamples++;
            handoff.LastObservedCommittedChunkIndex = context.RemoteNextExpectedChunkIndex;
            handoff.LastObservedHighestChunkIndex = state.DurableReceivedHighestChunkIndex;
        }

        if (context.RemoteNextExpectedChunkIndex >= context.ChunkCount ||
            handoff.DurableProgressSamples >= 2)
        {
            CompleteOutboundV5TransportHandoffLocked(
                context,
                "durable_frontier_progress",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
        }
    }

    private static bool ShouldEnqueueRepairOnlyForRebindStateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        int normalizedCommitted)
    {
        if (!IsOutboundPostTunaRecoveryActiveLocked(context) ||
            state.MissingRanges.Count == 0 ||
            context.IsTerminal)
        {
            return false;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        if (normalizedCommitted != remoteFrontier)
        {
            return false;
        }

        foreach (var range in state.MissingRanges)
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, context.ChunkCount);
            var end = Math.Clamp(range.StartChunkIndex + range.ChunkCount, start, context.ChunkCount);
            if (start <= remoteFrontier && remoteFrontier < end)
            {
                return true;
            }

            if (start == remoteFrontier)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<FileTransferRangeV4> ClampV5HandoffFrontierRepairRangesForSend(
        IReadOnlyList<FileTransferRangeV4> ranges,
        int frontierChunkIndex,
        int chunkCount)
    {
        if (ranges.Count == 0 ||
            frontierChunkIndex < 0 ||
            frontierChunkIndex >= chunkCount)
        {
            return ranges;
        }

        foreach (var range in ranges)
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, chunkCount);
            var end = Math.Clamp(range.StartChunkIndex + range.ChunkCount, start, chunkCount);
            if (start <= frontierChunkIndex && frontierChunkIndex < end)
            {
                return
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = frontierChunkIndex,
                        ChunkCount = V4PostFallbackEmergencyFrontierRepairChunks,
                    },
                ];
            }
        }

        return ranges;
    }

    private static bool IsOutboundPostFallbackExactFrontierRepairRequiredLocked(
        OutboundTransferContext context,
        int frontierChunkIndex)
    {
        if (!IsOutboundPostTunaRecoveryActiveLocked(context) ||
            frontierChunkIndex < 0 ||
            frontierChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        return context.PullPostTunaRecoveryActive ||
               IsV5TransportHandoffBlockingTail(context.V5TransportHandoff) ||
               context.PullTransportFrontierOnlyRepairActive ||
               context.RemoteNextExpectedChunkIndex == frontierChunkIndex;
    }

    private static bool ShouldClampOutboundV5HandoffFrontierRepairRangeForSendLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        int frontierChunkIndex)
    {
        if (IsOutboundV5BackfillRepairRequestLocked(context, state))
        {
            return false;
        }

        return IsOutboundPostFallbackExactFrontierRepairRequiredLocked(context, frontierChunkIndex);
    }

    private static bool IsOutboundV5BackfillRepairRequestLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
        => state is FileTransferReceiverStateFrameV6 v5State &&
           string.Equals(v5State.Priority, "backfill", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(v5State.RecoveryMode, FormatV5TransportHandoffState(V5TransportHandoffState.BackfillRepair), StringComparison.OrdinalIgnoreCase) &&
           context.V5TransportHandoff?.State == V5TransportHandoffState.BackfillRepair;

    private static bool IsOutboundPeerV5TransportEpochUsableLocked(
        OutboundTransferContext context,
        long transportEpoch)
    {
        if (transportEpoch <= 0)
        {
            return false;
        }

        if (context.V5TransportHandoff is { } handoff &&
            handoff.EpochId == transportEpoch &&
            handoff.State != V5TransportHandoffState.Recovered)
        {
            return true;
        }

        return transportEpoch > context.LastRecoveredV5TransportHandoffEpoch;
    }

    private static bool TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
        OutboundTransferContext context,
        long peerTransportEpoch,
        string frameType,
        string reason,
        int peerCommittedChunkIndex,
        int peerHighestObservedChunkIndex,
        bool requireFrontierEvidence)
    {
        if (peerTransportEpoch <= 0 ||
            context.V5TransportHandoff is not { } current ||
            current.EpochId == peerTransportEpoch ||
            !IsV5TransportHandoffBlockingTail(current))
        {
            return false;
        }

        var clampedPeerCommitted = Math.Clamp(peerCommittedChunkIndex, 0, context.ChunkCount);
        var peerHighest = Math.Max(-1, peerHighestObservedChunkIndex);
        var currentFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var hasFrontierEvidence = !requireFrontierEvidence ||
                                  (clampedPeerCommitted <= currentFrontier &&
                                   peerHighest >= currentFrontier);
        if (!hasFrontierEvidence)
        {
            return false;
        }

        var previousEpoch = current.EpochId;
        var previousState = current.State;
        context.V5TransportHandoff = new TransportHandoffEpoch
        {
            EpochId = peerTransportEpoch,
            Kind = current.Kind,
            SourceTransport = current.SourceTransport,
            TargetTransport = current.TargetTransport,
            Direction = FileTransferDirection.Outbound,
            Reason = string.IsNullOrWhiteSpace(current.Reason) ? reason : current.Reason,
            StartedUtc = current.StartedUtc,
            TargetReadyUtc = current.TargetReadyUtc ?? DateTimeOffset.UtcNow,
            StartingCommittedChunkIndex = Math.Min(current.StartingCommittedChunkIndex, clampedPeerCommitted),
            StartingHighestObservedChunkIndex = Math.Max(current.StartingHighestObservedChunkIndex, peerHighest),
            LastProofUtc = DateTimeOffset.UtcNow,
            State = V5TransportHandoffState.FrontierRepairOnly,
            LastObservedCommittedChunkIndex = clampedPeerCommitted,
            LastObservedHighestChunkIndex = peerHighest,
            LastRepairRequestId = current.LastRepairRequestId,
            LastStateChangeLogUtc = DateTimeOffset.UtcNow,
        };
        context.PullTransportRebindGeneration = Math.Max(
            context.PullTransportRebindGeneration,
            (int)Math.Min(int.MaxValue, peerTransportEpoch));
        context.V4SenderPumpLastWakeReason = "v6_handoff_epoch_reconciled";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_conflict; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; action=adopt_peer_epoch; reason={FormatProtocolLogValue(reason)}; previous_transport_epoch={previousEpoch}; peer_transport_epoch={peerTransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV5TransportHandoffEpoch}; previous_state={FormatV5TransportHandoffState(previousState)}; state={FormatV5TransportHandoffState(context.V5TransportHandoff.State)}; committed_chunk={clampedPeerCommitted}; highest_observed_chunk={peerHighest}; target_transport={FormatFileTransferTransportKind(context.V5TransportHandoff.TargetTransport)}");
        return true;
    }

    private static bool TryReopenOutboundRecoveredV5TransportHandoffEpochLocked(
        OutboundTransferContext context,
        long peerTransportEpoch,
        string frameType,
        string reason,
        int peerCommittedChunkIndex,
        int peerHighestObservedChunkIndex,
        bool requireFrontierEvidence)
    {
        if (peerTransportEpoch <= 0 ||
            peerTransportEpoch > context.LastRecoveredV5TransportHandoffEpoch ||
            context.V5TransportHandoff is not null)
        {
            return false;
        }

        var clampedPeerCommitted = Math.Clamp(peerCommittedChunkIndex, 0, context.ChunkCount);
        var peerHighest = Math.Max(-1, peerHighestObservedChunkIndex);
        var currentFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var hasFrontierEvidence = !requireFrontierEvidence ||
                                  (clampedPeerCommitted <= currentFrontier &&
                                   peerHighest >= currentFrontier);
        if (!hasFrontierEvidence)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        context.V5TransportHandoff = new TransportHandoffEpoch
        {
            EpochId = peerTransportEpoch,
            Kind = FileTransferTransportHandoffKind.RegularNknRecovery,
            SourceTransport = FileTransferTransportKind.Unknown,
            TargetTransport = FileTransferTransportKind.RegularNkn,
            Direction = FileTransferDirection.Outbound,
            Reason = reason,
            StartedUtc = now,
            TargetReadyUtc = now,
            StartingCommittedChunkIndex = Math.Min(currentFrontier, clampedPeerCommitted),
            StartingHighestObservedChunkIndex = Math.Max(Math.Max(-1, context.ChunksAcceptedForTransport - 1), peerHighest),
            LastProofUtc = now,
            State = V5TransportHandoffState.FrontierRepairOnly,
            LastObservedCommittedChunkIndex = clampedPeerCommitted,
            LastObservedHighestChunkIndex = peerHighest,
            LastStateChangeLogUtc = now,
        };
        context.PullTransportRebindGeneration = Math.Max(
            context.PullTransportRebindGeneration,
            (int)Math.Min(int.MaxValue, peerTransportEpoch));
        context.V4SenderPumpLastWakeReason = "v6_handoff_reopened_recovered_epoch";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_reopened; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; reason={FormatProtocolLogValue(reason)}; peer_transport_epoch={peerTransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV5TransportHandoffEpoch}; state={FormatV5TransportHandoffState(context.V5TransportHandoff.State)}; committed_chunk={clampedPeerCommitted}; highest_observed_chunk={peerHighest}; current_frontier_chunk={currentFrontier}; target_transport={FormatFileTransferTransportKind(context.V5TransportHandoff.TargetTransport)}");
        return true;
    }

    private void ApplyOutboundV5HandoffFrame(OutboundTransferContext context, FileTransferTransportEpochFrameV6 handoff)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                handoff.TransportEpoch <= 0)
            {
                return;
            }

            if (!IsOutboundPeerV5TransportEpochUsableLocked(context, handoff.TransportEpoch) &&
                !TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    handoff.TransportEpoch,
                    FileTransferProtocol.TransportEpochFrameTypeV6,
                    "peer_handoff_epoch_conflict",
                    context.RemoteNextExpectedChunkIndex,
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    requireFrontierEvidence: false))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={handoff.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV5TransportHandoffEpoch}");
                return;
            }

            if (context.V5TransportHandoff is null)
            {
                context.V5TransportHandoff = new TransportHandoffEpoch
                {
                    EpochId = handoff.TransportEpoch,
                    Kind = FileTransferTransportHandoffKind.RegularNknRecovery,
                    SourceTransport = FileTransferTransportKind.Unknown,
                    TargetTransport = FileTransferTransportKind.RegularNkn,
                    Direction = FileTransferDirection.Outbound,
                    Reason = "peer_handoff",
                    StartedUtc = DateTimeOffset.UtcNow,
                    StartingCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
                    StartingHighestObservedChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
                    LastObservedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    State = V5TransportHandoffState.TransportProofPending,
                };
                LogV5TransportHandoffEpochStarted(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.V5TransportHandoff);
            }

            if (context.V5TransportHandoff.EpochId != handoff.TransportEpoch &&
                !TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    handoff.TransportEpoch,
                    FileTransferProtocol.TransportEpochFrameTypeV6,
                    "peer_handoff_epoch_conflict",
                    context.RemoteNextExpectedChunkIndex,
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    requireFrontierEvidence: false))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={handoff.TransportEpoch}; current_transport_epoch={context.V5TransportHandoff.EpochId}");
                return;
            }

            if (context.V5TransportHandoff.State == V5TransportHandoffState.Recovered)
            {
                return;
            }

            TrySetV5TransportHandoffState(
                context.V5TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V5TransportHandoffState.FrontierRepairOnly,
                "peer_handoff",
                context.RemoteNextExpectedChunkIndex,
                Math.Max(-1, context.ChunksAcceptedForTransport - 1));
        }
    }

    private void ApplyOutboundV5RepairRequest(OutboundTransferContext context, FileTransferFrontierRequestFrameV6 repairRequest)
    {
        if (repairRequest.MissingRanges.Count == 0)
        {
            return;
        }

        var first = repairRequest.MissingRanges[0];
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                repairRequest.TransportEpoch <= 0)
            {
                return;
            }

            if (!IsOutboundPeerV5TransportEpochUsableLocked(context, repairRequest.TransportEpoch) &&
                !TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    repairRequest.TransportEpoch,
                    FileTransferProtocol.FrontierRequestFrameTypeV6,
                    "peer_repair_request_epoch_conflict",
                    first.StartChunkIndex,
                    first.StartChunkIndex + first.ChunkCount - 1,
                    requireFrontierEvidence: true) &&
                !TryReopenOutboundRecoveredV5TransportHandoffEpochLocked(
                    context,
                    repairRequest.TransportEpoch,
                    FileTransferProtocol.FrontierRequestFrameTypeV6,
                    "peer_repair_request_after_recovered_epoch",
                    first.StartChunkIndex,
                    first.StartChunkIndex + first.ChunkCount - 1,
                    requireFrontierEvidence: true))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={repairRequest.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV5TransportHandoffEpoch}");
                return;
            }

            if (context.V5TransportHandoff is null)
            {
                context.PullTransportRebindGeneration = Math.Max(context.PullTransportRebindGeneration, (int)Math.Min(int.MaxValue, repairRequest.TransportEpoch));
                context.V5TransportHandoff = new TransportHandoffEpoch
                {
                    EpochId = repairRequest.TransportEpoch,
                    Kind = FileTransferTransportHandoffKind.RegularNknRecovery,
                    SourceTransport = FileTransferTransportKind.Unknown,
                    TargetTransport = FileTransferTransportKind.RegularNkn,
                    Direction = FileTransferDirection.Outbound,
                    Reason = "peer_repair_request",
                    StartedUtc = DateTimeOffset.UtcNow,
                    StartingCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
                    StartingHighestObservedChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
                    LastObservedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
                    State = V5TransportHandoffState.TransportProofPending,
                };
                LogV5TransportHandoffEpochStarted(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.V5TransportHandoff);
            }

            if (context.V5TransportHandoff.EpochId != repairRequest.TransportEpoch)
            {
                if (!TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                        context,
                        repairRequest.TransportEpoch,
                        FileTransferProtocol.FrontierRequestFrameTypeV6,
                        "peer_repair_request_epoch_conflict",
                        first.StartChunkIndex,
                        first.StartChunkIndex + first.ChunkCount - 1,
                        requireFrontierEvidence: true))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={repairRequest.TransportEpoch}; current_transport_epoch={context.V5TransportHandoff.EpochId}");
                    return;
                }
            }

            MarkV5TransportHandoffPeerActivity(context.V5TransportHandoff);
            var requestedState =
                string.Equals(repairRequest.Priority, "backfill", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(repairRequest.RecoveryMode, FormatV5TransportHandoffState(V5TransportHandoffState.BackfillRepair), StringComparison.OrdinalIgnoreCase)
                    ? V5TransportHandoffState.BackfillRepair
                    : V5TransportHandoffState.FrontierRepairOnly;
            TrySetV5TransportHandoffState(
                context.V5TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                requestedState,
                "repair_request",
                context.RemoteNextExpectedChunkIndex,
                Math.Max(-1, context.ChunksAcceptedForTransport - 1));
        }

        var syntheticState = new FileTransferReceiverStateFrameV6
        {
            SessionId = repairRequest.SessionId,
            TransferId = repairRequest.TransferId,
            Epoch = Math.Max(0, Environment.TickCount & 0x3fffffff),
            ContiguousCommittedChunkIndex = Math.Max(0, first.StartChunkIndex),
            DurableReceivedHighestChunkIndex = Math.Max(first.StartChunkIndex, first.StartChunkIndex + first.ChunkCount - 1),
            CreditUntilChunkIndexExclusive = Math.Max(first.StartChunkIndex + first.ChunkCount, first.StartChunkIndex + 1),
            MissingRanges = repairRequest.MissingRanges,
            BytesCommitted = 0,
            TransportEpoch = repairRequest.TransportEpoch,
            RepairRequestId = repairRequest.RepairRequestId,
            Priority = repairRequest.Priority,
            RecoveryMode = repairRequest.RecoveryMode,
        };
        EnqueueV4RepairsFromState(context, syntheticState);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_repair_requested; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={repairRequest.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequest.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(repairRequest.Priority ?? "(none)")}; first_start_chunk_index={first.StartChunkIndex}; requested_chunk_count={first.ChunkCount}; range_count={repairRequest.MissingRanges.Count}");
    }

    private void ApplyOutboundV5RepairProof(OutboundTransferContext context, FileTransferRepairProofFrameV6 repairProof)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                repairProof.TransportEpoch <= 0)
            {
                return;
            }

            if (!IsOutboundPeerV5TransportEpochUsableLocked(context, repairProof.TransportEpoch) &&
                !TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    repairProof.TransportEpoch,
                    FileTransferProtocol.RepairProofFrameTypeV6,
                    "peer_repair_proof_epoch_conflict",
                    repairProof.CommittedChunkIndex,
                    Math.Max(context.ChunksAcceptedForTransport - 1, repairProof.CommittedChunkIndex - 1),
                    requireFrontierEvidence: false))
            {
                return;
            }

            if (context.V5TransportHandoff is null)
            {
                return;
            }

            if (context.V5TransportHandoff.EpochId != repairProof.TransportEpoch &&
                !TryAdoptOutboundPeerV5TransportHandoffEpochLocked(
                    context,
                    repairProof.TransportEpoch,
                    FileTransferProtocol.RepairProofFrameTypeV6,
                    "peer_repair_proof_epoch_conflict",
                    repairProof.CommittedChunkIndex,
                    Math.Max(context.ChunksAcceptedForTransport - 1, repairProof.CommittedChunkIndex - 1),
                    requireFrontierEvidence: false))
            {
                return;
            }

            TrySetV5TransportHandoffState(
                context.V5TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                repairProof.CommittedChunkIndex > context.V5TransportHandoff.StartingCommittedChunkIndex
                    ? V5TransportHandoffState.BackfillRepair
                    : V5TransportHandoffState.FrontierRepairOnly,
                "repair_proof",
                repairProof.CommittedChunkIndex,
                Math.Max(context.ChunksAcceptedForTransport - 1, repairProof.CommittedChunkIndex - 1));
        }
    }

    private void LogOutboundV4StateReceivedLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        int previousEpoch,
        int previousRemoteNext,
        int previousGrant,
        bool applied,
        bool stale,
        bool duplicate)
    {
        var availableCreditChunks = Math.Max(0, context.RemoteGrantedUntilExclusive - context.ChunksAcceptedForTransport);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={previousEpoch}; applied={(applied ? 1 : 0)}; stale={(stale ? 1 : 0)}; duplicate={(duplicate ? 1 : 0)}; previous_contiguous_committed_chunk_index={previousRemoteNext}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; previous_credit_until_chunk_index_exclusive={previousGrant}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; effective_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; available_credit_chunks={availableCreditChunks}; available_credit_bytes={availableCreditChunks * (long)Math.Max(1, context.ChunkSizeBytes)}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; receiver_memory_pressure={(state.ReceiverMemoryPressure ? 1 : 0)}; receiver_disk_pressure={(state.ReceiverDiskPressure ? 1 : 0)}; terminal_ready={(state.TerminalReady ? 1 : 0)}");
    }

    private void ApplyOutboundV4PauseControl(OutboundTransferContext context, FileTransferPauseControlFrameV4 pauseControl)
    {
        SessionFileTransferSnapshot? snapshot = null;
        var receivedEventName = pauseControl is FileTransferPauseControlFrameV6
            ? "filetransfer_v6_pause_control_received"
            : "filetransfer_v4_pause_control_received";
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (pauseControl.Epoch < context.PeerV4LastPauseControlEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Outbound; epoch={pauseControl.Epoch}; previous_epoch={context.PeerV4LastPauseControlEpoch}; stale=1; applied=0; peer_paused={(pauseControl.Paused ? 1 : 0)}");
                return;
            }

            var normalizedReason = NormalizeReason(pauseControl.Reason);
            var changed = pauseControl.Paused != context.PeerPaused ||
                !string.Equals(normalizedReason, context.PeerPauseReason, StringComparison.Ordinal);
            if (pauseControl.Epoch == context.PeerV4LastPauseControlEpoch && !changed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Outbound; epoch={pauseControl.Epoch}; previous_epoch={context.PeerV4LastPauseControlEpoch}; duplicate=1; applied=0; peer_paused={(pauseControl.Paused ? 1 : 0)}");
                return;
            }

            var previousEpoch = context.PeerV4LastPauseControlEpoch;
            context.PeerV4LastPauseControlEpoch = Math.Max(context.PeerV4LastPauseControlEpoch, pauseControl.Epoch);
            context.PeerPaused = pauseControl.Paused;
            context.PeerPauseReason = normalizedReason;
            context.PeerPausedSinceUtc = pauseControl.Paused ? DateTimeOffset.UtcNow : null;
            if (pauseControl.Paused)
            {
                context.ResetV6SenderPipelineCancellation();
            }
            else
            {
                ResetOutboundV4AcceptedForPeerResumeLocked(context);
            }

            context.V4SenderPumpLastWakeReason = pauseControl.Paused
                ? "peer_pause_control_paused"
                : "peer_pause_control_resumed";
            if (!context.UserPaused)
            {
                context.StatusMessage = context.PeerPaused
                    ? "Peer paused transfer."
                    : GetOutboundResumeStatusMessage(context.State);
            }

            snapshot = CreateSnapshotLocked();
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Outbound; epoch={pauseControl.Epoch}; previous_epoch={previousEpoch}; stale=0; applied=1; peer_paused={(pauseControl.Paused ? 1 : 0)}; pause_reason={FormatProtocolLogValue(normalizedReason ?? "(none)")}");
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private void EnqueueV4RepairsFromState(OutboundTransferContext context, FileTransferStateFrameV4 state)
    {
        if (state.MissingRanges.Count == 0)
        {
            return;
        }

        var normalizedInputRanges = NormalizeV4MissingRangesForSend(state.MissingRanges, context.ChunkCount);
        var postRebindFrontierRepair = IsOutboundPostTunaRecoveryActiveLocked(context) &&
            normalizedInputRanges.Any(range =>
                range.StartChunkIndex <= state.ContiguousCommittedChunkIndex &&
                state.ContiguousCommittedChunkIndex < range.StartChunkIndex + range.ChunkCount);
        var normalizedRanges = SelectV4RepairRangesForSend(
            normalizedInputRanges,
            state.ContiguousCommittedChunkIndex,
            frontierExclusive: postRebindFrontierRepair);
        if (postRebindFrontierRepair &&
            ShouldClampOutboundV5HandoffFrontierRepairRangeForSendLocked(context, state, state.ContiguousCommittedChunkIndex))
        {
            normalizedRanges = ClampV5HandoffFrontierRepairRangesForSend(
                normalizedRanges,
                state.ContiguousCommittedChunkIndex,
                context.ChunkCount);
        }

        if (normalizedRanges.Count == 0)
        {
            return;
        }

        var requestedChunkIndices = new List<int>(FileTransferProtocol.MaxStateMissingChunksV4);
        foreach (var range in normalizedRanges)
        {
            for (var chunkIndex = range.StartChunkIndex;
                 chunkIndex < range.StartChunkIndex + range.ChunkCount &&
                 requestedChunkIndices.Count < FileTransferProtocol.MaxStateMissingChunksV4;
                 chunkIndex++)
            {
                requestedChunkIndices.Add(chunkIndex);
            }
        }

        var requestedChunkCount = normalizedRanges.Sum(static range => range.ChunkCount);
        var firstStart = normalizedRanges[0].StartChunkIndex;
        var lastEndExclusive = normalizedRanges[^1].StartChunkIndex + normalizedRanges[^1].ChunkCount;
        var emergencyCreditRepair = postRebindFrontierRepair &&
            normalizedRanges.Count == 1 &&
            firstStart == state.ContiguousCommittedChunkIndex &&
            requestedChunkCount <= V4PostFallbackEmergencyFrontierRepairChunks;
        var v5BackfillEmergencyCreditRepair = IsOutboundV5BackfillRepairRequestLocked(context, state) &&
            postRebindFrontierRepair &&
            firstStart == state.ContiguousCommittedChunkIndex &&
            lastEndExclusive > firstStart + V4PostFallbackEmergencyFrontierRepairChunks;
        var allowEmergencyCreditRepair = emergencyCreditRepair || v5BackfillEmergencyCreditRepair;
        var emergencyCreditEndExclusive = v5BackfillEmergencyCreditRepair
            ? Math.Min(context.ChunkCount, lastEndExclusive)
            : firstStart + V4PostFallbackEmergencyFrontierRepairChunks;
        var chunkIndices = FilterRepairChunkIndicesForSend(
            context,
            requestedChunkIndices,
            allowEmergencyCreditRepair,
            emergencyCreditEndExclusive,
            out var stats);
        var repairRequestKey = CreateV4RepairRequestKey(context.TransferId, firstStart, requestedChunkCount, state.ContiguousCommittedChunkIndex, state.DurableReceivedHighestChunkIndex, normalizedRanges);
        var protocolRepairRequestId = state is FileTransferReceiverStateFrameV6 v5StateForRepair ? v5StateForRepair.RepairRequestId : null;
        var protocolPriority = state is FileTransferReceiverStateFrameV6 v5StateForPriority ? v5StateForPriority.Priority : null;
        var protocolRecoveryMode = state is FileTransferReceiverStateFrameV6 v5StateForRecoveryMode ? v5StateForRecoveryMode.RecoveryMode : null;
        var frontierTailRepair = normalizedRanges.Count == 1 &&
            firstStart == state.ContiguousCommittedChunkIndex &&
            state.DurableReceivedHighestChunkIndex < state.ContiguousCommittedChunkIndex;
        var queuedRepair = new PullV4QueuedRepairSend(
            chunkIndices,
            normalizedRanges.Count,
            requestedChunkCount,
            firstStart,
            lastEndExclusive,
            stats.RemoteNextExpectedChunkIndex,
            stats.ChunksAcceptedForTransport,
            stats.SkippedObsoleteCount,
            stats.SkippedFutureCount,
            stats.SkippedOutOfBoundsCount,
            repairRequestKey,
            protocolRepairRequestId,
            protocolPriority,
            protocolRecoveryMode,
            frontierTailRepair,
            emergencyCreditRepair,
            FileTransferV4RepairDeliveryMode.BulkOnly,
            "first_send",
            -1);

        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_scheduled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count=0; scheduled_chunk_indices=(none); first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair=-1; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; sent_chunk_count=0; sent_chunk_indices=(none); repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=no_chunks; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; frontier_tail_repair={(queuedRepair.FrontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair=-1; remote_next_expected_chunk_index={queuedRepair.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={queuedRepair.ChunksAcceptedForTransport}; skipped_obsolete_count={queuedRepair.SkippedObsoleteCount}; skipped_future_count={queuedRepair.SkippedFutureCount}; skipped_out_of_bounds_count={queuedRepair.SkippedOutOfBoundsCount}; receiver_proof_status=not_applicable");
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (postRebindFrontierRepair)
            {
                ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
                    context,
                    state.ContiguousCommittedChunkIndex,
                    "missing_frontier_state");
                if (requestedChunkCount == V4PostFallbackEmergencyFrontierRepairChunks &&
                    IsOutboundPostFallbackExactFrontierRepairRequiredLocked(context, state.ContiguousCommittedChunkIndex))
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_v6_exact_frontier_repair_enqueued; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V5TransportHandoff?.EpochId ?? context.PullTransportRebindGeneration}; state={FormatV5TransportHandoffState(context.V5TransportHandoff?.State ?? V5TransportHandoffState.FrontierRepairOnly)}; frontier_chunk_index={state.ContiguousCommittedChunkIndex}; repair_request_key={repairRequestKey}; source_state_missing_range_count={state.MissingRanges.Count}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}");
                }
            }

            CleanupOutboundV4RepairRequestStateLocked(context, DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            var frontierRepair = firstStart == state.ContiguousCommittedChunkIndex;
            var senderRepairRepeatIntervalMs = ResolveV4SenderRepairRepeatIntervalMs(context, frontierRepair);
            if (!TryMarkOutboundV4RepairQueuedLocked(context, repairRequestKey, now, senderRepairRepeatIntervalMs, out var repairState, out var suppressionReason, out var lastSentAgeMs))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; reason={suppressionReason}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count={chunkIndices.Count}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; last_sent_age_ms={lastSentAgeMs}; repair_interval_ms={senderRepairRepeatIntervalMs}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}");
                return;
            }

            var deliveryDecision = ResolveV4RepairDeliveryDecisionLocked(context, repairState, stats.RemoteNextExpectedChunkIndex, frontierRepair, now);
            var deduped = new List<int>(chunkIndices.Count);
            foreach (var chunkIndex in chunkIndices)
            {
                if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
                {
                    deduped.Add(chunkIndex);
                }
            }

            if (deduped.Count == 0)
            {
                if (context.PullV4SenderPumpRepairRequests.TryGetValue(repairRequestKey, out var queuedState))
                {
                    queuedState.Queued = false;
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; reason=chunks_already_queued; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count=0; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; last_sent_age_ms=-1; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}");
                return;
            }

            if (allowEmergencyCreditRepair)
            {
                var emergencyEndExclusive = Math.Min(context.ChunkCount, deduped.Max() + 1);
                if (emergencyEndExclusive > context.ChunksAcceptedForTransport)
                {
                    var previousAccepted = context.ChunksAcceptedForTransport;
                    context.ChunksAcceptedForTransport = emergencyEndExclusive;
                    context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * Math.Max(1, context.ChunkSizeBytes));
                    var eventName = v5BackfillEmergencyCreditRepair
                        ? "filetransfer_v6_backfill_repair_credit_granted"
                        : "filetransfer_v4_emergency_frontier_credit_granted";
                    var reason = v5BackfillEmergencyCreditRepair
                        ? "post_fallback_backfill_repair"
                        : "post_fallback_missing_frontier";
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; previous_chunks_accepted_for_transport={previousAccepted}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; emergency_start_chunk_index={firstStart}; emergency_end_chunk_exclusive={emergencyEndExclusive}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; transport_epoch={context.V5TransportHandoff?.EpochId ?? 0}");
                }
            }

            context.PullV4SenderPumpRepairQueue.Enqueue(
                queuedRepair with
                {
                    ChunkIndices = deduped,
                    DeliveryMode = deliveryDecision.Mode,
                    DeliveryEscalationReason = deliveryDecision.Reason,
                    CreditExhaustedTimeMsAtDecision = deliveryDecision.CreditExhaustedTimeMs,
                });
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_scheduled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count={deduped.Count}; scheduled_chunk_indices={FormatV4ChunkIndicesForLog(deduped)}; repair_delivery_mode={FormatV4RepairDeliveryMode(deliveryDecision.Mode)}; repair_delivery_escalation_reason={deliveryDecision.Reason}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair={deliveryDecision.CreditExhaustedTimeMs}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
            context.SignalV4SenderPump();
        }
    }

    private static void CleanupOutboundV4RepairRequestStateLocked(OutboundTransferContext context, DateTimeOffset now)
    {
        foreach (var key in context.PullV4SenderPumpRepairRequests.Keys.ToArray())
        {
            var repairState = context.PullV4SenderPumpRepairRequests[key];
            if (repairState.Queued || repairState.InFlight)
            {
                continue;
            }

            if (repairState.LastSentUtc is null ||
                now - repairState.LastSentUtc.Value >= TimeSpan.FromMilliseconds(V4RepairRequestHistoryRetentionMs))
            {
                context.PullV4SenderPumpRepairRequests.Remove(key);
            }
        }
    }

    private static bool HasOutboundV4RepairWorkInProgressLocked(OutboundTransferContext context)
    {
        if (context.PullV4SenderPumpRepairQueue.Count > 0 ||
            context.PullV4SenderPumpRepairQueuedChunkIndices.Count > 0)
        {
            return true;
        }

        return context.PullV4SenderPumpRepairRequests.Values.Any(static state => state.Queued || state.InFlight);
    }

    private static bool TryMarkOutboundV4RepairQueuedLocked(
        OutboundTransferContext context,
        string repairRequestKey,
        DateTimeOffset now,
        int repairRepeatIntervalMs,
        out V4SenderRepairRequestState repairState,
        out string suppressionReason,
        out long lastSentAgeMs)
    {
        suppressionReason = "(none)";
        lastSentAgeMs = -1;
        if (!context.PullV4SenderPumpRepairRequests.TryGetValue(repairRequestKey, out var existingRepairState))
        {
            repairState = new V4SenderRepairRequestState { Queued = true };
            context.PullV4SenderPumpRepairRequests[repairRequestKey] = repairState;
            return true;
        }

        repairState = existingRepairState;
        if (repairState.Queued)
        {
            suppressionReason = "queued";
            repairState.SuppressedCount++;
            return false;
        }

        if (repairState.InFlight)
        {
            suppressionReason = "in_flight";
            repairState.SuppressedCount++;
            return false;
        }

        if (repairState.LastSentUtc is not null)
        {
            lastSentAgeMs = (long)Math.Max(0, (now - repairState.LastSentUtc.Value).TotalMilliseconds);
            if (lastSentAgeMs < repairRepeatIntervalMs)
            {
                suppressionReason = "recently_sent";
                repairState.SuppressedCount++;
                return false;
            }
        }

        repairState.Queued = true;
        return true;
    }

    private static (FileTransferV4RepairDeliveryMode Mode, string Reason, long CreditExhaustedTimeMs) ResolveV4RepairDeliveryDecisionLocked(
        OutboundTransferContext context,
        V4SenderRepairRequestState repairState,
        int queuedRemoteFrontierChunkIndex,
        bool frontierRepair,
        DateTimeOffset now)
    {
        var creditStallAgeMs = context.V4SenderCreditExhaustedSinceUtc is null
            ? 0
            : (long)Math.Max(0, (now - context.V4SenderCreditExhaustedSinceUtc.Value).TotalMilliseconds);
        if (repairState.SentCount == 0)
        {
            if (frontierRepair &&
                IsOutboundPostTunaRecoveryActiveLocked(context))
            {
                return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "post_fallback_frontier_emergency", creditStallAgeMs);
            }

            if (frontierRepair &&
                IsV4FileOnlyFastRepairEnabled(context) &&
                creditStallAgeMs >= V4FileOnlyFirstRepairCreditStallEscalationMs)
            {
                return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "first_send_credit_stall", creditStallAgeMs);
            }

            return (FileTransferV4RepairDeliveryMode.BulkOnly, "first_send", creditStallAgeMs);
        }

        if (creditStallAgeMs >= V4RepairRedundancyEscalationStallMs)
        {
            return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "credit_stall", creditStallAgeMs);
        }

        var currentRemoteFrontier = Math.Max(
            queuedRemoteFrontierChunkIndex,
            Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount));
        if (repairState.LastSentRemoteFrontierChunkIndex >= currentRemoteFrontier)
        {
            return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "frontier_not_advanced", creditStallAgeMs);
        }

        return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "retry", creditStallAgeMs);
    }

    private static string FormatV4RepairDeliveryMode(FileTransferV4RepairDeliveryMode mode)
        => mode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant
            ? "control_bulk_escalated"
            : "bulk_only";

    private static PullV4QueuedRepairSend? RevalidateQueuedV4RepairSendLocked(
        OutboundTransferContext context,
        PullV4QueuedRepairSend queuedRepair)
    {
        var filtered = new List<int>(queuedRepair.ChunkIndices.Count);
        var skippedObsolete = 0;
        var skippedFuture = 0;
        var skippedOutOfBounds = 0;
        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        foreach (var chunkIndex in queuedRepair.ChunkIndices)
        {
            if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
            {
                skippedOutOfBounds++;
                continue;
            }

            if (chunkIndex < remoteFrontier)
            {
                skippedObsolete++;
                continue;
            }

            if (chunkIndex >= acceptedUntil &&
                !(queuedRepair.EmergencyCreditRepair && chunkIndex == remoteFrontier))
            {
                skippedFuture++;
                continue;
            }

            filtered.Add(chunkIndex);
        }

        if (filtered.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_suppressed; direction=sender; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={queuedRepair.RepairRequestKey}; reason=obsolete_after_frontier_advance; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; scheduled_chunk_count=0; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; frontier_tail_repair={(queuedRepair.FrontierTailRepair ? 1 : 0)}; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_for_transport={acceptedUntil}; skipped_obsolete_count={queuedRepair.SkippedObsoleteCount + skippedObsolete}; skipped_future_count={queuedRepair.SkippedFutureCount + skippedFuture}; skipped_out_of_bounds_count={queuedRepair.SkippedOutOfBoundsCount + skippedOutOfBounds}");
            return null;
        }

        return queuedRepair with
        {
            ChunkIndices = filtered,
            RemoteNextExpectedChunkIndex = remoteFrontier,
            ChunksAcceptedForTransport = acceptedUntil,
            SkippedObsoleteCount = queuedRepair.SkippedObsoleteCount + skippedObsolete,
            SkippedFutureCount = queuedRepair.SkippedFutureCount + skippedFuture,
            SkippedOutOfBoundsCount = queuedRepair.SkippedOutOfBoundsCount + skippedOutOfBounds,
        };
    }

    private static void ClearObsoleteOutboundV4RepairWorkAfterFrontierAdvanceLocked(
        OutboundTransferContext context,
        int previousRemoteFrontier,
        int remoteFrontier)
    {
        if (remoteFrontier <= previousRemoteFrontier)
        {
            return;
        }

        var removedQueuedChunks = context.PullV4SenderPumpRepairQueuedChunkIndices.RemoveWhere(
            chunkIndex => chunkIndex < remoteFrontier);
        var removedRepairItems = 0;
        var removedRepairChunks = 0;
        var retained = new Queue<PullV4QueuedRepairSend>(context.PullV4SenderPumpRepairQueue.Count);
        while (context.PullV4SenderPumpRepairQueue.Count > 0)
        {
            var queuedRepair = context.PullV4SenderPumpRepairQueue.Dequeue();
            var filtered = queuedRepair.ChunkIndices
                .Where(chunkIndex => chunkIndex >= remoteFrontier)
                .ToList();
            var skippedObsolete = queuedRepair.ChunkIndices.Count - filtered.Count;
            if (filtered.Count == 0)
            {
                removedRepairItems++;
                removedRepairChunks += queuedRepair.ChunkIndices.Count;
                if (context.PullV4SenderPumpRepairRequests.TryGetValue(queuedRepair.RepairRequestKey, out var repairState))
                {
                    repairState.Queued = false;
                }

                continue;
            }

            removedRepairChunks += skippedObsolete;
            retained.Enqueue(queuedRepair with
            {
                ChunkIndices = filtered,
                RemoteNextExpectedChunkIndex = remoteFrontier,
                SkippedObsoleteCount = queuedRepair.SkippedObsoleteCount + skippedObsolete,
            });
        }

        while (retained.Count > 0)
        {
            context.PullV4SenderPumpRepairQueue.Enqueue(retained.Dequeue());
        }

        var removedHistoryCount = 0;
        foreach (var pair in context.PullV4SenderPumpRepairRequests.ToArray())
        {
            var repairState = pair.Value;
            if (repairState.Queued || repairState.InFlight)
            {
                continue;
            }

            if (repairState.LastSentRemoteFrontierChunkIndex >= 0 &&
                repairState.LastSentRemoteFrontierChunkIndex < remoteFrontier)
            {
                context.PullV4SenderPumpRepairRequests.Remove(pair.Key);
                removedHistoryCount++;
            }
        }

        if (removedQueuedChunks > 0 ||
            removedRepairItems > 0 ||
            removedRepairChunks > 0 ||
            removedHistoryCount > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_obsolete_after_frontier_advance; direction=sender; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_remote_next_expected_chunk_index={previousRemoteFrontier}; remote_next_expected_chunk_index={remoteFrontier}; removed_queued_chunk_index_count={removedQueuedChunks}; removed_repair_item_count={removedRepairItems}; removed_repair_chunk_count={removedRepairChunks}; removed_history_count={removedHistoryCount}");
        }
    }

    private async Task SendChunkIndicesV4Async(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        List<int> chunkIndices,
        bool repairSend,
        string repairRequestKey = "(none)",
        string? protocolRepairRequestId = null,
        string? protocolPriority = null,
        string? protocolRecoveryMode = null,
        FileTransferV4RepairDeliveryMode repairDeliveryMode = FileTransferV4RepairDeliveryMode.BulkOnly,
        string repairDeliveryReason = "normal")
    {
        if (chunkIndices.Count == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            var pending = new Queue<PendingV4TransportSend>();
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

                    throw new InvalidOperationException("File-transfer V4 sender transport send failed.", sendException);
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
                    context.PullSenderBatchFramesRecent++;
                    context.PullSenderChunkCountRecent += pendingSend.Prepared.ChunkCount;
                    if (repairSend)
                    {
                        context.PullSenderRepairSendCountRecent += pendingSend.Prepared.ChunkCount;
                    }

                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, sentUtc, force: false);
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_chunk_batch_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; mixed_screenshare={(IsV4MixedScreenShareActive() ? 1 : 0)}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}; repair_batch_send_attempt={pendingSend.SendAttempt}; repair_batch_send_attempt_count={pendingSend.SendAttemptCount}");
            }

            async Task<bool> ScheduleAsync(PreparedV4TransportSend prepared, int sendAttempt, int sendAttemptCount)
            {
                while (pending.Count >= V4SenderPumpDepth ||
                       (pending.Count > 0 && pendingRawBytes + prepared.RawBytes > V4SenderPumpPendingBytes))
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
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) ||
                        context.IsTerminal ||
                        context.UserPaused ||
                        context.PeerPaused)
                    {
                        return false;
                    }
                }

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
                pending.Enqueue(new PendingV4TransportSend(prepared, sendTask, scheduledUtc, sendAttempt, sendAttemptCount));
                pendingRawBytes += prepared.RawBytes;

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return false;
                    }

                    context.PullSenderPipelineConfiguredDepthRecent = V4SenderPumpDepth;
                    context.PullSenderPipelineEffectiveDepthRecent = V4SenderPumpDepth;
                    context.PullSenderPipelineScheduledFramesRecent++;
                    if (repairSend)
                    {
                        context.PullSenderV4RepairScheduledFramesRecent++;
                    }
                    else
                    {
                        context.PullSenderV4NormalScheduledFramesRecent++;
                    }

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

                return true;
            }

            for (var index = 0; index < chunkIndices.Count; index++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) ||
                        context.IsTerminal ||
                        context.UserPaused ||
                        context.PeerPaused)
                    {
                        break;
                    }
                }

                var batchPrepareStarted = Stopwatch.GetTimestamp();
                var preparedBatch = await TryPrepareChunkBatchV4Async(context, stream, chunkIndices, index, buffer, repairSend, repairRequestKey, protocolRepairRequestId, protocolPriority, protocolRecoveryMode, repairDeliveryMode).ConfigureAwait(false);
                var batchPrepareDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(batchPrepareStarted).TotalMilliseconds);
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

                var sendAttemptCount = repairSend ? V4RepairBatchSendAttempts : 1;
                for (var sendAttempt = 1; sendAttempt <= sendAttemptCount; sendAttempt++)
                {
                    if (!await ScheduleAsync(preparedBatch, sendAttempt, sendAttemptCount).ConfigureAwait(false))
                    {
                        index = chunkIndices.Count;
                        break;
                    }
                }

                index += preparedBatch.ChunkCount - 1;

                if (!repairSend)
                {
                    var preemptForRepair = false;
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context) &&
                            !context.IsTerminal &&
                            context.PullV4SenderPumpRepairQueue.Count > 0)
                        {
                            context.V4SenderPumpLastWakeReason = "repair_preempted_normal_pass";
                            preemptForRepair = true;
                        }
                    }

                    if (preemptForRepair)
                    {
                        break;
                    }
                }
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

    private async Task<PreparedV4TransportSend> TryPrepareChunkBatchV4Async(
        OutboundTransferContext context,
        Stream stream,
        IReadOnlyList<int> chunkIndices,
        int startListIndex,
        byte[] buffer,
        bool repairSend,
        string repairRequestKey,
        string? protocolRepairRequestId,
        string? protocolPriority,
        string? protocolRecoveryMode,
        FileTransferV4RepairDeliveryMode repairDeliveryMode)
    {
        var startChunkIndex = chunkIndices[startListIndex];
        var expectedChunkIndex = startChunkIndex;
        var totalRawBytes = 0;
        var maxBatchSegments = ResolveV4MaxBatchSegments(repairSend);
        List<byte[]> dataSegments = [];
        for (var index = startListIndex; index < chunkIndices.Count && dataSegments.Count < maxBatchSegments; index++)
        {
            var chunkIndex = chunkIndices[index];
            if (chunkIndex != expectedChunkIndex)
            {
                break;
            }

            var chunkBytes = await LoadChunkBytesForSendAsync(context, stream, chunkIndex, buffer, repairSend).ConfigureAwait(false);
            var candidateRawBytes = totalRawBytes + chunkBytes.Length;
            if (candidateRawBytes > FileTransferProtocol.MaxChunkBatchRawBytesV4 ||
                !CanSerializeChunkBatchV4(context.SessionId, context.TransferId, startChunkIndex, dataSegments, chunkBytes))
            {
                if (dataSegments.Count == 0)
                {
                    throw new InvalidOperationException("V4 chunk batch could not fit inside the transport payload budget.");
                }

                break;
            }

            dataSegments.Add(chunkBytes);
            totalRawBytes = candidateRawBytes;
            expectedChunkIndex++;
        }

        var batch = new FileTransferChunkBatchFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = startChunkIndex,
            ChunkCount = dataSegments.Count,
            DataSegments = dataSegments,
            BatchProfile = repairSend
                ? ResolveV4RepairBatchProfileName(maxBatchSegments)
                : ResolveV4BatchProfileName(maxBatchSegments),
            RepairDeliveryMode = repairSend
                ? repairDeliveryMode
                : FileTransferV4RepairDeliveryMode.BulkOnly,
            TransportEpoch = context.V5TransportHandoff?.EpochId ?? 0,
            BatchId = context.V5TransportHandoff is null
                ? null
                : $"v6:{context.V5TransportHandoff.EpochId}:{startChunkIndex}:{dataSegments.Count}",
            RepairRequestId = repairSend ? protocolRepairRequestId ?? repairRequestKey : null,
            Priority = repairSend
                ? protocolPriority ??
                  (IsV5TransportHandoffBlockingTail(context.V5TransportHandoff)
                      ? "frontier"
                      : "repair")
                : null,
            RecoveryMode = protocolRecoveryMode ??
                           (context.V5TransportHandoff is null
                               ? null
                               : FormatV5TransportHandoffState(context.V5TransportHandoff.State)),
        };

        _ = FileTransferDataFrameCodec.Serialize(batch);
        return new PreparedV4TransportSend(batch, startChunkIndex, dataSegments.Count, totalRawBytes);
    }

    private int ResolveV4MaxBatchSegments(bool repairSend)
    {
        if (!repairSend && IsV4MixedScreenShareActive())
        {
            return sessionScreenShareDegraded
                ? V4MixedScreenShareDegradedBatchSegments
                : V4MixedScreenShareNormalBatchSegments;
        }

        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(V4MaxBatchSegmentsEnvironmentVariableName, category: "filetransfer_tuning");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, V4MaxBatchSegmentsMin, V4MaxBatchSegmentsMax)
            : V4MaxBatchSegmentsDefault;
    }

    private static string ResolveV4BatchProfileName(int maxBatchSegments)
        => maxBatchSegments == V4MaxBatchSegmentsDefault
            ? "v4_default_21k"
            : $"v4_default_21k_{maxBatchSegments}x";

    private static string ResolveV4RepairBatchProfileName(int maxBatchSegments)
        => maxBatchSegments == V4MaxBatchSegmentsDefault
            ? "v4_repair_21k"
            : $"v4_repair_21k_{maxBatchSegments}x";

    private static bool CanSerializeChunkBatchV4(
        string sessionId,
        string transferId,
        int startChunkIndex,
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
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    ChunkCount = candidateSegments.Length,
                    DataSegments = candidateSegments,
                });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<FileTransferRangeV4> NormalizeV4MissingRangesForSend(IReadOnlyList<FileTransferRangeV4> ranges, int chunkCount)
    {
        if (ranges.Count == 0 || chunkCount <= 0)
        {
            return [];
        }

        var normalized = new List<FileTransferRangeV4>();
        var totalChunks = 0;
        foreach (var range in ranges
                     .Where(static range => range.ChunkCount > 0)
                     .OrderBy(static range => range.StartChunkIndex)
                     .ThenBy(static range => range.ChunkCount))
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, chunkCount);
            var endExclusive = Math.Clamp(range.StartChunkIndex + range.ChunkCount, 0, chunkCount);
            if (endExclusive <= start)
            {
                continue;
            }

            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                var previousEnd = previous.StartChunkIndex + previous.ChunkCount;
                if (start <= previousEnd)
                {
                    var mergedEnd = Math.Max(previousEnd, endExclusive);
                    normalized[^1] = previous with { ChunkCount = mergedEnd - previous.StartChunkIndex };
                    continue;
                }
            }

            var count = endExclusive - start;
            var remaining = FileTransferProtocol.MaxStateMissingChunksV4 - totalChunks;
            if (remaining <= 0 || normalized.Count >= FileTransferProtocol.MaxStateMissingRangesV4)
            {
                break;
            }

            if (count > remaining)
            {
                count = remaining;
            }

            normalized.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
            totalChunks += count;
        }

        return normalized;
    }

    private static IReadOnlyList<FileTransferRangeV4> SelectV4RepairRangesForSend(
        IReadOnlyList<FileTransferRangeV4> normalizedRanges,
        int remoteFrontier,
        bool frontierExclusive = false)
    {
        if (normalizedRanges.Count <= 1)
        {
            return normalizedRanges;
        }

        var selected = new List<FileTransferRangeV4>(normalizedRanges.Count);
        var selectedChunks = 0;
        foreach (var range in normalizedRanges)
        {
            var rangeEndExclusive = range.StartChunkIndex + range.ChunkCount;
            if (range.StartChunkIndex <= remoteFrontier && remoteFrontier < rangeEndExclusive)
            {
                var frontierCount = Math.Min(
                    rangeEndExclusive - remoteFrontier,
                    FileTransferProtocol.MaxStateMissingChunksV4);
                selected.Add(
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = remoteFrontier,
                        ChunkCount = frontierCount,
                    });
                selectedChunks += frontierCount;
                if (frontierExclusive)
                {
                    return selected;
                }

                break;
            }
        }

        foreach (var range in normalizedRanges)
        {
            if (selectedChunks >= FileTransferProtocol.MaxStateMissingChunksV4)
            {
                break;
            }

            var start = range.StartChunkIndex;
            var endExclusive = range.StartChunkIndex + range.ChunkCount;
            if (start <= remoteFrontier && remoteFrontier < endExclusive)
            {
                start = remoteFrontier + Math.Min(endExclusive - remoteFrontier, FileTransferProtocol.MaxStateMissingChunksV4);
            }

            if (endExclusive <= start)
            {
                continue;
            }

            var count = Math.Min(endExclusive - start, FileTransferProtocol.MaxStateMissingChunksV4 - selectedChunks);
            if (count <= 0)
            {
                continue;
            }

            selected.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
            selectedChunks += count;
        }

        return selected.Count == 0 ? normalizedRanges : selected;
    }

    private static string FormatV4ChunkIndicesForLog(IReadOnlyList<int> chunkIndices)
        => chunkIndices.Count == 0
            ? "(none)"
            : string.Join(",", chunkIndices.Take(FileTransferProtocol.MaxStateMissingChunksV4));

    private static string CreateV4RepairRequestKey(
        string transferId,
        int firstStart,
        int requestedChunkCount,
        int frontier,
        int highestReceived,
        IReadOnlyList<FileTransferRangeV4> ranges)
    {
        _ = transferId;
        _ = frontier;
        _ = highestReceived;
        var rangeText = string.Join(",", ranges.Select(static range => $"{range.StartChunkIndex}:{range.ChunkCount}"));
        return $"{Math.Max(0, firstStart)}:{Math.Max(0, requestedChunkCount)}:{rangeText}";
    }

    private void SignalOutboundV4SenderPump(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.SignalV4SenderPump();
            }
        }
    }

    private async Task StopOutboundV4SenderPumpAsync(OutboundTransferContext context, Task? senderPumpTask)
    {
        if (senderPumpTask is null)
        {
            return;
        }

        context.SignalV4SenderPump();
        try
        {
            await senderPumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private void ForceLogOutboundV4SenderPumpSummary(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context))
            {
                MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: true);
            }
        }
    }

    private void MaybeLogOutboundV4SenderPumpSummaryLocked(OutboundTransferContext context, DateTimeOffset now, bool force)
    {
        if (!force &&
            context.LastSenderThroughputLogUtc is not null &&
            now - context.LastSenderThroughputLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        if (!force &&
            context.PullSenderPipelineScheduledFramesRecent == 0 &&
            context.PullSenderPipelineCompletedFramesRecent == 0 &&
            context.PullSenderSendWaitCountRecent == 0)
        {
            return;
        }

        var availableCreditChunks = Math.Max(0, Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport);
        var creditExhaustedTimeMs = context.V4SenderCreditExhaustedSinceUtc is null || availableCreditChunks > 0 || context.V4TerminalReady
            ? 0
            : (long)Math.Max(0, (now - context.V4SenderCreditExhaustedSinceUtc.Value).TotalMilliseconds);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_sender_pump_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; sample_window_ms={PullControlChatterWindowMs}; configured_depth={V4SenderPumpDepth}; effective_depth={V4SenderPumpDepth}; in_flight_frames={context.PullSenderPipelineCurrentInFlightFrames}; in_flight_bytes={context.PullSenderPipelineCurrentInFlightBytes}; scheduled_frames={context.PullSenderPipelineScheduledFramesRecent}; normal_scheduled_frames={context.PullSenderV4NormalScheduledFramesRecent}; repair_scheduled_frames={context.PullSenderV4RepairScheduledFramesRecent}; completed_frames={context.PullSenderPipelineCompletedFramesRecent}; failed_frames={context.PullSenderPipelineFailedFramesRecent}; raw_bytes_sent={context.PullSenderRawBytesRecent}; batch_frames_sent={context.PullSenderBatchFramesRecent}; chunk_count_sent={context.PullSenderChunkCountRecent}; repair_send_count={context.PullSenderRepairSendCountRecent}; send_wait_count={context.PullSenderSendWaitCountRecent}; credit_exhausted_time_ms={creditExhaustedTimeMs}; available_credit_chunks={availableCreditChunks}; available_credit_bytes={availableCreditChunks * (long)Math.Max(1, context.ChunkSizeBytes)}; next_unsent_chunk_index={context.ChunksAcceptedForTransport}; credit_ceiling_chunk_index={context.RemoteGrantedUntilExclusive}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; terminal_ready={(context.V4TerminalReady ? 1 : 0)}; pump_wake_reason={context.V4SenderPumpLastWakeReason}; repair_request_key={context.V4SenderPumpLastRepairRequestKey}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count)}; sent_cache_chunk_count={context.PullSentChunkCache.Count}; sent_cache_bytes={context.PullSentChunkCacheBytes}");
        context.LastSenderThroughputLogUtc = now;
        context.PullSenderPipelineScheduledFramesRecent = 0;
        context.PullSenderV4NormalScheduledFramesRecent = 0;
        context.PullSenderV4RepairScheduledFramesRecent = 0;
        context.PullSenderPipelineCompletedFramesRecent = 0;
        context.PullSenderPipelineFailedFramesRecent = 0;
        context.PullSenderRawBytesRecent = 0;
        context.PullSenderBatchFramesRecent = 0;
        context.PullSenderChunkCountRecent = 0;
        context.PullSenderRepairSendCountRecent = 0;
        context.PullSenderSendWaitCountRecent = 0;
    }

    private async Task FailOutboundV4Async(
        OutboundTransferContext context,
        IFileTransferDataSession? dataSession,
        string errorCode,
        string statusMessage,
        bool notifyPeer)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_sender_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; error_code={errorCode}; reason={FormatProtocolLogValue(statusMessage)}");

        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: errorCode,
            statusMessage: statusMessage,
            notifyPeer: notifyPeer,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> SendOutboundV4PauseStateAsync(OutboundTransferContext context, string reason)
    {
        FileTransferStateFrameV4? state;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null)
            {
                return false;
            }

            state = CreateOutboundV4PauseStateLocked(context, reason);
            dataSession = context.DataSession;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LifecyclePrioritySendTimeoutMs));
            await dataSession.SendAsync(state, timeout.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_pause_state_sent; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; transfer_paused={(state.TransferPaused ? 1 : 0)}; pause_reason={FormatProtocolLogValue(state.TransferPauseReason ?? "(none)")}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}");
            return true;
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_pause_state_send_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; transfer_paused={(state.TransferPaused ? 1 : 0)}; error=timeout");
            return false;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_pause_state_send_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; transfer_paused={(state.TransferPaused ? 1 : 0)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private async Task<bool> SendOutboundV4PauseControlAsync(OutboundTransferContext context, string reason)
    {
        FileTransferPauseControlFrameV4? frame;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return false;
            }

            frame = CreateOutboundV4PauseControlLocked(context, reason);
        }

        return await SendV4PauseControlAsync(frame, reason, FileTransferDirection.Outbound, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> SendInboundV4PauseControlAsync(InboundTransferContext context, string reason)
    {
        FileTransferPauseControlFrameV4? frame;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal)
            {
                return false;
            }

            frame = CreateInboundV4PauseControlLocked(context, reason);
        }

        return await SendV4PauseControlAsync(frame, reason, FileTransferDirection.Inbound, CancellationToken.None).ConfigureAwait(false);
    }

    private void ScheduleOutboundV4PauseControlRetry(OutboundTransferContext context, bool paused, string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pause_control_retry_scheduled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt_count={PauseControlRetryDelaysMs.Length}");
        _ = Task.Run(
            () => RunOutboundV4PauseControlRetryLoopAsync(context, paused, reason),
            CancellationToken.None);
    }

    private void ScheduleInboundV4PauseControlRetry(InboundTransferContext context, bool paused, string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pause_control_retry_scheduled; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt_count={PauseControlRetryDelaysMs.Length}");
        _ = Task.Run(
            () => RunInboundV4PauseControlRetryLoopAsync(context, paused, reason),
            CancellationToken.None);
    }

    private async Task RunOutboundV4PauseControlRetryLoopAsync(OutboundTransferContext context, bool paused, string reason)
    {
        for (var index = 0; index < PauseControlRetryDelaysMs.Length; index++)
        {
            try
            {
                var delayMs = PauseControlRetryDelaysMs[index];
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                if (!ShouldContinueOutboundV4PauseControlRetry(context, paused))
                {
                    LogPauseControlRetryStopped(FileTransferDirection.Outbound, context.TransferId, context.SessionId, paused, reason, index + 1, "state_changed_or_terminal");
                    return;
                }

                if (await SendOutboundV4PauseControlAsync(context, reason).ConfigureAwait(false))
                {
                    LogPauseControlRetrySent(FileTransferDirection.Outbound, context.TransferId, context.SessionId, paused, reason, index + 1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPauseControlRetryStopped(FileTransferDirection.Outbound, context.TransferId, context.SessionId, paused, reason, index + 1, ex.GetType().Name);
                return;
            }
        }

        LogPauseControlRetryCompleted(FileTransferDirection.Outbound, context.TransferId, context.SessionId, paused, reason, PauseControlRetryDelaysMs.Length);
    }

    private async Task RunInboundV4PauseControlRetryLoopAsync(InboundTransferContext context, bool paused, string reason)
    {
        for (var index = 0; index < PauseControlRetryDelaysMs.Length; index++)
        {
            try
            {
                var delayMs = PauseControlRetryDelaysMs[index];
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                if (!ShouldContinueInboundV4PauseControlRetry(context, paused))
                {
                    LogPauseControlRetryStopped(FileTransferDirection.Inbound, context.TransferId, context.SessionId, paused, reason, index + 1, "state_changed_or_terminal");
                    return;
                }

                if (await SendInboundV4PauseControlAsync(context, reason).ConfigureAwait(false))
                {
                    LogPauseControlRetrySent(FileTransferDirection.Inbound, context.TransferId, context.SessionId, paused, reason, index + 1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPauseControlRetryStopped(FileTransferDirection.Inbound, context.TransferId, context.SessionId, paused, reason, index + 1, ex.GetType().Name);
                return;
            }
        }

        LogPauseControlRetryCompleted(FileTransferDirection.Inbound, context.TransferId, context.SessionId, paused, reason, PauseControlRetryDelaysMs.Length);
    }

    private bool ShouldContinueOutboundV4PauseControlRetry(OutboundTransferContext context, bool paused)
    {
        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.UserPaused == paused &&
                   !string.IsNullOrWhiteSpace(context.SessionId);
        }
    }

    private bool ShouldContinueInboundV4PauseControlRetry(InboundTransferContext context, bool paused)
    {
        lock (gate)
        {
            return ReferenceEquals(inboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.UserPaused == paused &&
                   !string.IsNullOrWhiteSpace(context.SessionId);
        }
    }

    private static void LogPauseControlRetrySent(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        bool paused,
        string reason,
        int attempt)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pause_control_retry_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt={attempt}");
    }

    private static void LogPauseControlRetryStopped(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        bool paused,
        string reason,
        int attempt,
        string stopReason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_pause_control_retry_stopped; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt={attempt}; stop_reason={FormatProtocolLogValue(stopReason)}");
    }

    private static void LogPauseControlRetryCompleted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        bool paused,
        string reason,
        int attemptCount)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pause_control_retry_completed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt_count={attemptCount}");
    }

    private async Task<bool> SendV4PauseControlAsync(
        FileTransferPauseControlFrameV4 frame,
        string reason,
        FileTransferDirection direction,
        CancellationToken ct)
    {
        var pause = new FileTransferPauseControlV6
        {
            SessionId = frame.SessionId,
            TransferId = frame.TransferId,
            Epoch = frame.Epoch,
            Paused = frame.Paused,
            Reason = frame.Reason,
            TransportEpoch = frame is FileTransferPauseControlFrameV6 v6 ? v6.TransportEpoch : 0,
            BatchId = frame is FileTransferPauseControlFrameV6 batch ? batch.BatchId : null,
            RepairRequestId = frame is FileTransferPauseControlFrameV6 repair ? repair.RepairRequestId : null,
            Priority = frame is FileTransferPauseControlFrameV6 priority ? priority.Priority : null,
            RecoveryMode = frame is FileTransferPauseControlFrameV6 recovery ? recovery.RecoveryMode : null,
        };
        var sent = await SendPauseControlAsync(pause, direction, reason, ct).ConfigureAwait(false);
        if (sent)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_pause_control_sent; transfer_id={frame.TransferId}; session_id={frame.SessionId}; direction={direction}; reason={reason}; epoch={frame.Epoch}; paused={(frame.Paused ? 1 : 0)}; pause_reason={FormatProtocolLogValue(frame.Reason ?? "(none)")}");
        }

        return sent;
    }

    private void ResetOutboundV4AcceptedForUserResumeLocked(OutboundTransferContext context)
        => ResetOutboundV4AcceptedAfterPauseLocked(context, "user_resumed");

    private void ResetOutboundV4AcceptedForPeerResumeLocked(OutboundTransferContext context)
        => ResetOutboundV4AcceptedAfterPauseLocked(context, "peer_resumed");

    private void ResetOutboundV4AcceptedAfterPauseLocked(OutboundTransferContext context, string reason)
    {
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !context.PullSourceCanSeek)
        {
            return;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedBefore = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        if (acceptedBefore <= remoteFrontier)
        {
            return;
        }

        var pendingRepairCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        context.ChunksAcceptedForTransport = remoteFrontier;
        context.BytesAcceptedForTransport = remoteFrontier >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)remoteFrontier * Math.Max(1, context.ChunkSizeBytes));
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repair in context.PullV4SenderPumpRepairRequests.Values)
        {
            repair.Queued = false;
            repair.InFlight = false;
        }

        foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex >= remoteFrontier).ToArray())
        {
            context.SentAwaitingAck.Remove(chunkIndex);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_sender_resume_rewind; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_before={acceptedBefore}; chunks_accepted_after={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; cleared_pending_repair_count={pendingRepairCount}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}");
    }

    private void QueueOutboundV4TransportRebindSafetyReplayLocked(
        OutboundTransferContext context,
        string reason,
        bool allowRepeatForSameGeneration = false)
    {
        var recoveryGeneration = GetOutboundPostTunaRecoveryGenerationLocked(context);
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullSourceCanSeek ||
            recoveryGeneration <= 0 ||
            (!context.PullPostTunaRecoveryActive && recoveryGeneration <= context.LastRecoveredV5TransportHandoffEpoch))
        {
            return;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var now = DateTimeOffset.UtcNow;
        if (context.PullTransportLastSafetyReplayGeneration == recoveryGeneration)
        {
            if (!allowRepeatForSameGeneration)
            {
                return;
            }

            var lastReplayAge = context.PullTransportLastSafetyReplayUtc is null
                ? TimeSpan.MaxValue
                : now - context.PullTransportLastSafetyReplayUtc.Value;
            if (context.PullTransportLastSafetyReplayFrontierChunkIndex == remoteFrontier &&
                lastReplayAge < PullTransportRebindSafetyReplayRearmCooldown)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; skip_reason=rearm_cooldown; remote_next_expected_chunk_index={remoteFrontier}; last_replay_age_ms={Math.Max(0, (long)lastReplayAge.TotalMilliseconds)}; rearm_cooldown_ms={Math.Max(0, (long)PullTransportRebindSafetyReplayRearmCooldown.TotalMilliseconds)}");
                return;
            }

            context.PullTransportSafetyReplayRearmCount++;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_rebind_safety_replay_rearmed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; rearm_count={context.PullTransportSafetyReplayRearmCount}; last_replay_age_ms={(context.PullTransportLastSafetyReplayUtc is null ? -1 : Math.Max(0, (long)lastReplayAge.TotalMilliseconds))}");
        }

        var grantedUntilExclusive = Math.Clamp(context.RemoteGrantedUntilExclusive, remoteFrontier, context.ChunkCount);
        var emergencyCreditRepair = false;
        var frontierOnlyReplay = context.PullTransportFrontierOnlyRepairActive &&
            remoteFrontier < context.ChunkCount;
        if (grantedUntilExclusive <= remoteFrontier)
        {
            emergencyCreditRepair = true;
            grantedUntilExclusive = Math.Min(context.ChunkCount, remoteFrontier + V4PostFallbackEmergencyFrontierRepairChunks);
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_emergency_frontier_replay; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; emergency_end_chunk_exclusive={grantedUntilExclusive}");
        }
        else if (frontierOnlyReplay)
        {
            var previousReplayEndExclusive = grantedUntilExclusive;
            grantedUntilExclusive = Math.Min(context.ChunkCount, remoteFrontier + V4PostFallbackEmergencyFrontierRepairChunks);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_frontier_only_replay; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; previous_replay_end_chunk_exclusive={previousReplayEndExclusive}; replay_end_chunk_exclusive={grantedUntilExclusive}; frontier_only_start_chunk_index={context.PullTransportFrontierOnlyRepairStartChunkIndex}");
        }
        else if (IsV5TransportHandoffBlockingTail(context.V5TransportHandoff))
        {
            var previousReplayEndExclusive = grantedUntilExclusive;
            grantedUntilExclusive = Math.Min(context.ChunkCount, remoteFrontier + V4PostFallbackEmergencyFrontierRepairChunks);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V5TransportHandoff!.EpochId}; state={FormatV5TransportHandoffState(context.V5TransportHandoff.State)}; remote_next_expected_chunk_index={remoteFrontier}; previous_replay_end_chunk_exclusive={previousReplayEndExclusive}; replay_end_chunk_exclusive={grantedUntilExclusive}");
        }

        var maxChunksByBytes = Math.Max(1, PullTransportRebindSafetyReplayMaxBytes / Math.Max(1, context.ChunkSizeBytes));
        var replayChunkCount = Math.Min(
            grantedUntilExclusive - remoteFrontier,
            Math.Min(PullTransportRebindSafetyReplayMaxChunks, maxChunksByBytes));
        if (replayChunkCount <= 0)
        {
            return;
        }

        var replayEndExclusive = remoteFrontier + replayChunkCount;
        var chunkIndices = new List<int>(replayChunkCount);
        for (var chunkIndex = remoteFrontier; chunkIndex < replayEndExclusive; chunkIndex++)
        {
            if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
            {
                chunkIndices.Add(chunkIndex);
            }
        }

        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; skip_reason=chunks_already_queued; remote_next_expected_chunk_index={remoteFrontier}; replay_end_chunk_exclusive={replayEndExclusive}");
            return;
        }

        context.PullTransportLastSafetyReplayGeneration = recoveryGeneration;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = remoteFrontier;
        context.PullTransportLastSafetyReplayUtc = now;
        var repairKey = $"transport_rebind_safety_replay:{recoveryGeneration}:{remoteFrontier}:{chunkIndices.Count}";
        context.PullV4SenderPumpRepairQueue.Enqueue(
            new PullV4QueuedRepairSend(
                chunkIndices,
                RangeCount: 1,
                RequestedChunkCount: replayChunkCount,
                FirstStartChunkIndex: remoteFrontier,
                LastEndChunkExclusive: replayEndExclusive,
                RemoteNextExpectedChunkIndex: remoteFrontier,
                ChunksAcceptedForTransport: replayEndExclusive,
                SkippedObsoleteCount: 0,
                SkippedFutureCount: replayChunkCount - chunkIndices.Count,
                SkippedOutOfBoundsCount: 0,
                RepairRequestKey: repairKey,
                ProtocolRepairRequestId: null,
                ProtocolPriority: null,
                ProtocolRecoveryMode: null,
                FrontierTailRepair: true,
                EmergencyCreditRepair: emergencyCreditRepair,
                DeliveryMode: FileTransferV4RepairDeliveryMode.ControlBulkRedundant,
                DeliveryEscalationReason: emergencyCreditRepair
                    ? "transport_rebind_emergency_frontier"
                    : frontierOnlyReplay
                        ? "transport_rebind_frontier_only"
                        : "transport_rebind_safety_replay",
                CreditExhaustedTimeMsAtDecision: -1));

        context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, replayEndExclusive);
        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * Math.Max(1, context.ChunkSizeBytes));
        context.V4SenderPumpLastWakeReason = "transport_rebind_safety_replay";
        context.SignalV4SenderPump();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_safety_replay_started; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={remoteFrontier}; replay_end_chunk_exclusive={replayEndExclusive}; requested_chunk_count={replayChunkCount}; scheduled_chunk_count={chunkIndices.Count}; replay_byte_cap={PullTransportRebindSafetyReplayMaxBytes}; replay_chunk_cap={PullTransportRebindSafetyReplayMaxChunks}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}");
    }

    private static FileTransferStateFrameV4 CreateOutboundV4PauseStateLocked(OutboundTransferContext context, string reason)
    {
        var committed = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var bytesCommitted = context.ChunkCount > 0 && committed >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, Math.Max(0L, (long)committed * Math.Max(1, context.ChunkSizeBytes)));
        context.V4PauseControlEpoch++;
        return new FileTransferReceiverStateFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            ContiguousCommittedChunkIndex = committed,
            DurableReceivedHighestChunkIndex = committed - 1,
            CreditUntilChunkIndexExclusive = committed,
            MissingRanges = [],
            BytesCommitted = bytesCommitted,
            ReceiverMemoryPressure = false,
            ReceiverDiskPressure = false,
            TerminalReady = false,
            TransferPaused = context.UserPaused,
            TransferPauseReason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
        };
    }

    private static FileTransferPauseControlFrameV4 CreateOutboundV4PauseControlLocked(OutboundTransferContext context, string reason)
    {
        context.V4PauseControlEpoch++;
        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = context.UserPaused,
            Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
        };
    }

    private static FileTransferPauseControlFrameV4 CreateInboundV4PauseControlLocked(InboundTransferContext context, string reason)
    {
        context.V4PauseControlEpoch++;
        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = context.UserPaused,
            Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
        };
    }

    private async Task RunInboundV4SparseReceiveLoopAsync(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_receiver_started; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; session_open_chunk_size_bytes={sessionOpen.ChunkSizeBytes}; session_open_pipeline_depth={sessionOpen.InitialPipelineDepth}");

        try
        {
            IFileTransferDataSession? dataSession;
            lock (gate)
            {
                dataSession = ReferenceEquals(inboundTransfer, context) && !context.IsTerminal
                    ? context.DataSession
                    : null;
            }

            if (dataSession is null)
            {
                return;
            }

            Task<FileTransferDataFrame>? pendingReceiveTask = null;
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                pendingReceiveTask ??= dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                var completed = await Task.WhenAny(
                    pendingReceiveTask,
                    Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (TryGetInboundV4PeerSilenceFailure(context, out var silenceStatus))
                    {
                        await TransitionInboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Failed,
                            errorCode: DisconnectedErrorCode,
                            statusMessage: silenceStatus,
                            sendError: false,
                            errorMessage: null,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    await MaybeSendInboundV4FrontierStallStateAsync(context).ConfigureAwait(false);
                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                if (!IsFrameForContext(context, frame))
                {
                    LogInboundV4FrameIgnored(context, frame, "session_or_transfer_mismatch");
                    continue;
                }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    LogInboundV4FrameIgnored(context, frame, "protocol_not_v6");
                    continue;
                }

                switch (frame)
                {
                    case FileTransferTransportEpochFrameV6 handoff:
                        MarkInboundV4PeerFrameReceived(context);
                        ApplyInboundV5HandoffFrame(context, handoff);
                        await SendInboundV5TransportHandoffAsync(context, "peer_handoff").ConfigureAwait(false);
                        break;
                    case FileTransferFrontierRequestFrameV6 repairRequest:
                        MarkInboundV4PeerFrameReceived(context);
                        LogInboundV4FrameIgnored(context, repairRequest, "unexpected_inbound_repair_request_v5");
                        break;
                    case FileTransferRepairProofFrameV6 repairProof:
                        MarkInboundV4PeerFrameReceived(context);
                        ApplyInboundV5RepairProof(context, repairProof);
                        break;
                    case FileTransferManifestFrameV4 manifest:
                        MarkInboundV4PeerFrameReceived(context);
                        if (!await InitializeInboundV4ManifestAsync(context, manifest).ConfigureAwait(false))
                        {
                            return;
                        }

                        StartInboundV4RepairScheduler(context);
                        if (context.UserPaused)
                        {
                            await SendInboundV4PauseControlAsync(context, "user_paused_manifest_received").ConfigureAwait(false);
                        }

                        await SendInboundV4StateAsync(context, "manifest_received", terminalReady: false).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV4 batch:
                        await HandleInboundV4ChunkBatchAsync(context, batch).ConfigureAwait(false);
                        break;
                    case FileTransferStateFrameV4 state:
                        MarkInboundV4PeerFrameReceived(context);
                        if (ApplyInboundV4PeerState(context, state))
                        {
                            await FlushInboundV4PausedProgressAsync(context, "peer_resumed").ConfigureAwait(false);
                        }
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogInboundV4FrameIgnored(context, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferCancelFrameV4 cancel:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=cancel; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; cancel_reason={FormatProtocolLogValue(NormalizeReason(cancel.Reason) ?? CanceledReason)}");
                        break;
                    case FileTransferErrorFrameV4 error:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=error; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; error_code={NormalizeErrorCode(error.ErrorCode) ?? InvalidStateErrorCode}");
                        break;
                    default:
                        LogInboundV4FrameIgnored(context, frame, "unexpected_inbound_frame_v4");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                ex.Message,
                "V4 receive loop failed.").ConfigureAwait(false);
        }
    }

    private void StartInboundV4RepairScheduler(InboundTransferContext context)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.PullManifestReceived &&
                context.ReceiverSparseWriteActive &&
                !context.V4ReceiverRepairSchedulerStarted)
            {
                context.V4ReceiverRepairSchedulerStarted = true;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_receiver_repair_scheduler_started; transfer_id={context.TransferId}; session_id={context.SessionId}; poll_interval_ms={PullSessionReceivePollDelayMs}");
        _ = RunInboundV4RepairSchedulerAsync(context);
    }

    private bool ApplyInboundV4PeerState(InboundTransferContext context, FileTransferStateFrameV4 state)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldFlushPausedProgress = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (state.Epoch < context.PeerV4LastStateEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_peer_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={context.PeerV4LastStateEpoch}; stale=1; applied=0; transfer_paused={(state.TransferPaused ? 1 : 0)}");
                return false;
            }

            var normalizedPauseReason = NormalizeReason(state.TransferPauseReason);
            var peerPauseChanged = state.TransferPaused != context.PeerPaused ||
                !string.Equals(normalizedPauseReason, context.PeerPauseReason, StringComparison.Ordinal);
            if (state.Epoch == context.PeerV4LastStateEpoch && !peerPauseChanged)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_peer_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={context.PeerV4LastStateEpoch}; duplicate=1; applied=0; transfer_paused={(state.TransferPaused ? 1 : 0)}");
                return false;
            }

            var previousEpoch = context.PeerV4LastStateEpoch;
            context.PeerV4LastStateEpoch = Math.Max(context.PeerV4LastStateEpoch, state.Epoch);
            context.PeerPaused = state.TransferPaused;
            context.PeerPauseReason = normalizedPauseReason;
            context.PeerPausedSinceUtc = state.TransferPaused ? DateTimeOffset.UtcNow : null;
            shouldFlushPausedProgress = peerPauseChanged && !context.PeerPaused && !context.UserPaused;
            if (!context.UserPaused)
            {
                context.StatusMessage = context.PeerPaused
                    ? "Peer paused transfer."
                    : GetInboundResumeStatusMessage(context.State);
            }

            snapshot = CreateSnapshotLocked();
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_peer_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={previousEpoch}; stale=0; applied=1; transfer_paused={(state.TransferPaused ? 1 : 0)}; pause_reason={FormatProtocolLogValue(normalizedPauseReason ?? "(none)")}");
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return shouldFlushPausedProgress;
    }

    private void ApplyInboundV5HandoffFrame(InboundTransferContext context, FileTransferTransportEpochFrameV6 handoff)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                handoff.TransportEpoch <= 0)
            {
                return;
            }

            if (handoff.TransportEpoch <= context.LastRecoveredV5TransportHandoffEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={handoff.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV5TransportHandoffEpoch}");
                return;
            }

            if (context.V5TransportHandoff is null)
            {
                context.PullTransportRebindGeneration = Math.Max(context.PullTransportRebindGeneration, (int)Math.Min(int.MaxValue, handoff.TransportEpoch));
                context.V5TransportHandoff = new TransportHandoffEpoch
                {
                    EpochId = handoff.TransportEpoch,
                    Reason = "peer_handoff",
                    StartedUtc = DateTimeOffset.UtcNow,
                    StartingCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
                    StartingHighestObservedChunkIndex = context.PullHighestReceivedChunkIndex,
                    LastObservedCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
                    LastObservedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
                    State = V5TransportHandoffState.NknProofPending,
                };
                LogV5TransportHandoffEpochStarted(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.V5TransportHandoff);
            }

            if (context.V5TransportHandoff.EpochId != handoff.TransportEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={handoff.TransportEpoch}; current_transport_epoch={context.V5TransportHandoff.EpochId}");
                return;
            }

            TrySetV5TransportHandoffState(
                context.V5TransportHandoff,
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                V5TransportHandoffState.FrontierRepairOnly,
                "peer_handoff",
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex);
            context.StatusMessage = "Repairing over regular NKN";
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }
    }

    private void ApplyInboundV5RepairProof(InboundTransferContext context, FileTransferRepairProofFrameV6 repairProof)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                repairProof.TransportEpoch <= context.LastRecoveredV5TransportHandoffEpoch ||
                context.V5TransportHandoff is null ||
                context.V5TransportHandoff.EpochId != repairProof.TransportEpoch)
            {
                return;
            }

            TrySetV5TransportHandoffState(
                context.V5TransportHandoff,
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                repairProof.CommittedChunkIndex > context.V5TransportHandoff.StartingCommittedChunkIndex
                    ? V5TransportHandoffState.BackfillRepair
                    : V5TransportHandoffState.FrontierRepairOnly,
                "repair_proof",
                repairProof.CommittedChunkIndex,
                context.PullHighestReceivedChunkIndex);
        }
    }

    private bool ApplyInboundV4PauseControl(InboundTransferContext context, FileTransferPauseControlFrameV4 pauseControl)
    {
        SessionFileTransferSnapshot? snapshot = null;
        var shouldFlushPausedProgress = false;
        var receivedEventName = pauseControl is FileTransferPauseControlFrameV6
            ? "filetransfer_v6_pause_control_received"
            : "filetransfer_v4_pause_control_received";
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (pauseControl.Epoch < context.PeerV4LastPauseControlEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Inbound; epoch={pauseControl.Epoch}; previous_epoch={context.PeerV4LastPauseControlEpoch}; stale=1; applied=0; peer_paused={(pauseControl.Paused ? 1 : 0)}");
                return false;
            }

            var normalizedReason = NormalizeReason(pauseControl.Reason);
            var changed = pauseControl.Paused != context.PeerPaused ||
                !string.Equals(normalizedReason, context.PeerPauseReason, StringComparison.Ordinal);
            if (pauseControl.Epoch == context.PeerV4LastPauseControlEpoch && !changed)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Inbound; epoch={pauseControl.Epoch}; previous_epoch={context.PeerV4LastPauseControlEpoch}; duplicate=1; applied=0; peer_paused={(pauseControl.Paused ? 1 : 0)}");
                return false;
            }

            var previousEpoch = context.PeerV4LastPauseControlEpoch;
            context.PeerV4LastPauseControlEpoch = Math.Max(context.PeerV4LastPauseControlEpoch, pauseControl.Epoch);
            context.PeerPaused = pauseControl.Paused;
            context.PeerPauseReason = normalizedReason;
            context.PeerPausedSinceUtc = pauseControl.Paused ? DateTimeOffset.UtcNow : null;
            shouldFlushPausedProgress = changed && !context.PeerPaused && !context.UserPaused;
            if (!context.UserPaused)
            {
                context.StatusMessage = context.PeerPaused
                    ? "Peer paused transfer."
                    : GetInboundResumeStatusMessage(context.State);
            }

            snapshot = CreateSnapshotLocked();
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event={receivedEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=Inbound; epoch={pauseControl.Epoch}; previous_epoch={previousEpoch}; stale=0; applied=1; peer_paused={(pauseControl.Paused ? 1 : 0)}; pause_reason={FormatProtocolLogValue(normalizedReason ?? "(none)")}");
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return shouldFlushPausedProgress;
    }

    private async Task RunInboundV4RepairSchedulerAsync(InboundTransferContext context)
    {
        try
        {
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);
                await MaybeSendInboundV4FrontierStallStateAsync(context).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                ex.Message,
                "V4 receiver repair scheduler failed.").ConfigureAwait(false);
        }
    }

    private async Task<bool> InitializeInboundV4ManifestAsync(InboundTransferContext context, FileTransferManifestFrameV4 manifest)
    {
        string? failureCode = null;
        string? failureMessage = null;
        FileTransferReceiveDestination? destination = null;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (context.PullManifestReceived)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Duplicate V4 manifest received.";
            }
            else if (!string.Equals(context.FileName, manifest.FileName, StringComparison.Ordinal) ||
                     context.FileSizeBytes != manifest.FileSizeBytes ||
                     (!string.IsNullOrWhiteSpace(context.Sha256Base64) &&
                      !string.Equals(context.Sha256Base64, manifest.Sha256Base64, StringComparison.Ordinal)))
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V4 manifest metadata did not match the original offer.";
            }
            else if (!TryCalculateExpectedChunkCount(manifest.FileSizeBytes, manifest.ChunkSizeBytes, out var expectedChunkCount) ||
                     manifest.ChunkCount != expectedChunkCount)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V4 manifest chunk metadata did not match the declared file size.";
            }
            else if (manifest.ChunkCount > FileTransferProtocol.MaxChunkCountV4)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V4 manifest chunk count exceeded the supported limit.";
            }
        }

        if (failureCode is null && (context.WriteStream is null || context.Hash is null))
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
                destination = await context.OpenWriteDestinationAsync!(context.CreateOffer(), linkedCts.Token).ConfigureAwait(false);
                ValidateV4SparseDestination(destination.Stream);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                destination?.Dispose();
                return false;
            }
            catch (InvalidOperationException ex)
            {
                destination?.Dispose();
                failureCode = V4SparseDestinationRequiredErrorCode;
                failureMessage = ex.Message;
            }
            catch (Exception ex)
            {
                destination?.Dispose();
                failureCode = StreamOpenFailedErrorCode;
                failureMessage = ex.Message;
            }
        }

        SessionFileTransferSnapshot? snapshot = null;
        bool streamCanRead = false;
        bool streamCanSeek = false;
        bool streamCanWrite = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                destination?.Dispose();
                return false;
            }

            if (failureCode is null && destination is not null)
            {
                context.WriteDestination = destination;
                context.WriteStream = destination.Stream;
                context.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                destination = null;
            }

            if (failureCode is null && (context.WriteStream is null || context.Hash is null))
            {
                failureCode = StreamOpenFailedErrorCode;
                failureMessage = "Could not open the V4 receive destination stream.";
            }

            if (failureCode is null)
            {
                streamCanRead = context.WriteStream!.CanRead;
                streamCanSeek = context.WriteStream.CanSeek;
                streamCanWrite = context.WriteStream.CanWrite;
                context.Sha256Base64 = manifest.Sha256Base64;
                context.MetadataAwaitingSinceUtc = null;
                context.ChunkCount = manifest.ChunkCount;
                context.ChunkSizeBytes = manifest.ChunkSizeBytes;
                context.NextChunkIndex = 0;
                context.BufferedBytes = 0;
                context.HighestBufferedChunkIndex = -1;
                context.PullHighestReceivedChunkIndex = -1;
                context.PendingChunks.Clear();
                context.ReceiverSparseWriteActive = true;
                context.ReceiverSparseChunksWritten = new BitArray(manifest.ChunkCount);
                context.ReceiverSparseChunksPendingWrite.Clear();
                context.ReceiverSparseBytesWritten = 0;
                context.PullReceiverSparseWriteBytesRecent = 0;
                context.PullReceiverSparseWriteBatchCountRecent = 0;
                context.PullReceiverSparseWriteDurationMsRecent = 0;
                context.PullReceiverSparseChunksWrittenRecent = 0;
                context.PullReceiverSparseContiguousChunksCommittedRecent = 0;
                context.ReceiverBufferPressureActive = false;
                context.ReceiverBufferPressureSinceUtc = null;
                context.BytesTransferred = 0;
                context.ChunksTransferred = 0;
                context.V4StateEpoch = 0;
                context.V4CreditUntilChunkIndexExclusive = ComputeV4CreditUntilExclusiveLocked(context);
                context.V4ReceiverRepairRequests.Clear();
                context.V4FrontierStallStartedUtc = null;
                context.V4FrontierStallChunkIndex = -1;
                context.V4FrontierStallLastSuppressedLogUtc = null;
                context.PullManifestReceived = true;
                context.State = FileTransferTransferState.Receiving;
                context.StatusMessage = context.UserPaused
                    ? "Transfer paused."
                    : context.PeerPaused
                        ? "Peer paused transfer."
                        : "Receiving V4 file data.";
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                snapshot = CreateSnapshotLocked();
            }
        }

        destination?.Dispose();

        if (failureCode is not null)
        {
            await FailInboundV4Async(
                context,
                failureCode,
                failureMessage ?? "V4 manifest was invalid.",
                failureMessage ?? "V4 manifest was invalid.").ConfigureAwait(false);
            return false;
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_manifest_received; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size_bytes={manifest.FileSizeBytes}; chunk_size_bytes={manifest.ChunkSizeBytes}; chunk_count={manifest.ChunkCount}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_sparse_mode_selected; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=seekable_readable_destination; stream_can_read={(streamCanRead ? 1 : 0)}; stream_can_seek={(streamCanSeek ? 1 : 0)}; stream_can_write={(streamCanWrite ? 1 : 0)}; file_size_bytes={manifest.FileSizeBytes}; chunk_count={manifest.ChunkCount}; chunk_size_bytes={manifest.ChunkSizeBytes}");
        LogTransferInfo(
            "start_received",
            FileTransferDirection.Inbound,
            manifest.TransferId,
            sessionId: manifest.SessionId,
            fileName: manifest.FileName,
            fileSizeBytes: manifest.FileSizeBytes,
            reason: $"protocol_version={FileTransferProtocol.ProtocolVersionV6}; chunk_count={manifest.ChunkCount}; chunk_size_bytes={manifest.ChunkSizeBytes}");
        return true;
    }

    private async Task HandleInboundV4ChunkBatchAsync(InboundTransferContext context, FileTransferChunkBatchFrameV4 batch)
    {
        if (!TryValidateInboundV4ChunkBatch(context, batch, out var chunks, out var failureMessage))
        {
            var invalidSegmentCount = batch.ChunkCount == batch.DataSegments.Count ? 0 : 1;
            var invalidSegmentLength = failureMessage?.Contains("segment length", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
            var normalizedFailureMessage = failureMessage ?? "invalid_chunk_batch";
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_frontier_repair_batch_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=invalid_chunk_batch; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start=-1; requested_missing_range_count=0; committed_frontier_before={context.NextChunkIndex}; committed_frontier_after={context.NextChunkIndex}; accepted_chunk_count=0; duplicate_or_stale_chunk_count=0; pending_write_chunk_count=0; invalid_segment_count={invalidSegmentCount}; invalid_segment_length={invalidSegmentLength}; message={FormatProtocolLogValue(normalizedFailureMessage)}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_receiver_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; error_code={InvalidStateErrorCode}; reason=invalid_chunk_batch; message={FormatProtocolLogValue(failureMessage)}");
            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                failureMessage ?? "V4 chunk batch was invalid.",
                failureMessage ?? "V4 chunk batch was invalid.").ConfigureAwait(false);
            return;
        }

        var acceptedChunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(chunks.Count);
        var observedRepairKeys = new HashSet<string>(StringComparer.Ordinal);
        var repairOverlapChunkCount = 0;
        var repairAcceptedChunkCount = 0;
        var repairDuplicateOrStaleChunkCount = 0;
        var repairStaleChunkCount = 0;
        var repairDuplicateChunkCount = 0;
        var repairPendingWriteChunkCount = 0;
        var repairRequestedRangeStart = -1;
        var repairRequestedRangeCount = 0;
        var repairFrontierBefore = 0;
        var repairFrontierChunkObserved = false;
        var repairFrontierChunkStatus = "not_in_batch";
        Stream? writeStream;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                !context.ReceiverSparseWriteActive ||
                context.ReceiverSparseChunksWritten is null ||
                context.WriteStream is null)
            {
                return;
            }

            if (context.UserPaused || context.PeerPaused)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_chunk_batch_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={(context.UserPaused ? "user_paused" : "peer_paused")}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
                return;
            }

            writeStream = context.WriteStream;
            repairFrontierBefore = context.NextChunkIndex;
            ClearFilledInboundV4RepairRequestsLocked(context, DateTimeOffset.UtcNow);
            foreach (var (chunkIndex, chunkBytes) in chunks)
            {
                var repairState = FindInboundV4RepairStateForChunkLocked(context, chunkIndex);
                var overlapsActiveRepair = repairState is not null;
                if (overlapsActiveRepair)
                {
                    if (observedRepairKeys.Add(repairState!.RepairRequestKey) && repairRequestedRangeStart < 0)
                    {
                        repairRequestedRangeStart = repairState.FirstStartChunkIndex;
                        repairRequestedRangeCount = repairState.RequestedChunkCount;
                    }

                    repairOverlapChunkCount++;
                }

                if (chunkIndex < context.NextChunkIndex)
                {
                    if (overlapsActiveRepair && chunkIndex == repairFrontierBefore)
                    {
                        repairFrontierChunkObserved = true;
                        repairFrontierChunkStatus = "stale_or_already_committed";
                    }

                    if (overlapsActiveRepair)
                    {
                        repairDuplicateOrStaleChunkCount++;
                        repairStaleChunkCount++;
                    }

                    continue;
                }

                var pendingWrite = context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex);
                var duplicateWritten = context.ReceiverSparseChunksWritten is not null &&
                    chunkIndex >= 0 &&
                    chunkIndex < context.ReceiverSparseChunksWritten.Length &&
                    context.ReceiverSparseChunksWritten[chunkIndex];
                if (pendingWrite || duplicateWritten)
                {
                    if (overlapsActiveRepair && chunkIndex == repairFrontierBefore)
                    {
                        repairFrontierChunkObserved = true;
                        repairFrontierChunkStatus = pendingWrite ? "pending_write" : "duplicate_written";
                    }

                    if (overlapsActiveRepair)
                    {
                        repairDuplicateOrStaleChunkCount++;
                        if (pendingWrite)
                        {
                            repairPendingWriteChunkCount++;
                        }
                        else
                        {
                            repairDuplicateChunkCount++;
                        }
                    }

                    continue;
                }

                context.ReceiverSparseChunksPendingWrite.Add(chunkIndex);
                context.BufferedBytes += chunkBytes.Length;
                context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                context.PullReceiverRawBytesRecent += chunkBytes.Length;
                acceptedChunks.Add((chunkIndex, chunkBytes));
                if (overlapsActiveRepair)
                {
                    repairAcceptedChunkCount++;
                    if (chunkIndex == repairFrontierBefore)
                    {
                        repairFrontierChunkObserved = true;
                        repairFrontierChunkStatus = "accepted_for_write";
                    }
                }
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_chunk_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunks.Count}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
        if (repairOverlapChunkCount > 0)
        {
            LogInboundV4FrontierRepairBatchReceived(
                context,
                batch,
                repairRequestedRangeStart,
                repairRequestedRangeCount,
                repairFrontierBefore,
                acceptedChunks.Count,
                repairDuplicateOrStaleChunkCount,
                repairStaleChunkCount,
                repairDuplicateChunkCount,
                repairPendingWriteChunkCount,
                repairOverlapChunkCount);
        }

        if (acceptedChunks.Count == 0)
        {
            if (repairOverlapChunkCount > 0)
            {
                LogInboundV4FrontierRepairBatchIgnored(
                    context,
                    batch,
                    SelectInboundV4RepairBatchIgnoreReason(repairStaleChunkCount, repairDuplicateChunkCount, repairPendingWriteChunkCount),
                    repairRequestedRangeStart,
                    repairRequestedRangeCount,
                    repairFrontierBefore,
                    repairDuplicateOrStaleChunkCount,
                    repairStaleChunkCount,
                    repairDuplicateChunkCount,
                    repairPendingWriteChunkCount);
                LogInboundV4RepairChunkObserved(
                    context,
                    observedRepairKeys,
                    batch,
                    repairOverlapChunkCount,
                    acceptedChunkCount: 0,
                    repairDuplicateOrStaleChunkCount,
                    repairFrontierBefore,
                    repairFrontierBefore);
                LogInboundV5FrontierRepairStillMissing(
                    context,
                    batch,
                    repairRequestedRangeStart,
                    repairRequestedRangeCount,
                    repairFrontierBefore,
                    repairFrontierBefore,
                    acceptedChunkCount: 0,
                    repairDuplicateOrStaleChunkCount,
                    repairPendingWriteChunkCount,
                    repairFrontierChunkObserved,
                    repairFrontierChunkStatus,
                    SelectInboundV4RepairBatchIgnoreReason(repairStaleChunkCount, repairDuplicateChunkCount, repairPendingWriteChunkCount));
            }

            await MaybeSendInboundV4FrontierStallStateAsync(context).ConfigureAwait(false);
            return;
        }

        MarkInboundV4PeerFrameReceived(context);

        var writeStopwatch = Stopwatch.StartNew();
        long sparseWriteBytes = 0;
        var writeGateEntered = false;
        try
        {
            await context.ReceiverSparseWriteGate.WaitAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            writeGateEntered = true;
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
            await FailInboundV4Async(
                context,
                StreamWriteFailedErrorCode,
                ex.Message,
                "Could not write a V4 sparse receiver chunk.").ConfigureAwait(false);
            return;
        }
        finally
        {
            if (writeGateEntered)
            {
                context.ReceiverSparseWriteGate.Release();
            }
        }
        writeStopwatch.Stop();

        int committedChunkCount;
        long committedByteCount;
        bool completed;
        SessionFileTransferSnapshot? snapshot;
        int nextChunkIndexAfterCommit;
        long bytesCommittedAfterCommit;
        int pendingChunkCountAfterCommit;
        long pendingBytesAfterCommit;
        int highestReceivedChunkIndexAfterCommit;
        int lateArrivalDistanceAfterCommit;
        FileTransferRepairProofFrameV6? repairProofFrame = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
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

            (committedChunkCount, committedByteCount) = CommitInboundV4ContiguousWrittenLocked(context);

            context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
            context.PullLastProgressUtc = DateTimeOffset.UtcNow;
            context.PullReceiverWriteBatchCountRecent++;
            context.PullReceiverWriteBatchBytesRecent += sparseWriteBytes;
            context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseWriteBatchCountRecent++;
            context.PullReceiverSparseWriteBytesRecent += sparseWriteBytes;
            context.PullReceiverSparseWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseChunksWrittenRecent += acceptedChunks.Count;
            context.PullReceiverSparseContiguousChunksCommittedRecent += committedChunkCount;
            context.PullReceiverContiguousBytesCommittedRecent += committedByteCount;
            context.V4CreditUntilChunkIndexExclusive = ComputeV4CreditUntilExclusiveLocked(context);
            if (repairOverlapChunkCount > 0)
            {
                UpdateInboundV4PostFallbackFrontierRepairWindowLocked(
                    context,
                    repairFrontierBefore,
                    context.NextChunkIndex,
                    batch.StartChunkIndex,
                    batch.ChunkCount);
            }

            if (context.V5TransportHandoff is { } handoff &&
                batch is FileTransferChunkBatchFrameV6 v5Batch &&
                (v5Batch.TransportEpoch == 0 || v5Batch.TransportEpoch == handoff.EpochId) &&
                (acceptedChunks.Count > 0 || repairOverlapChunkCount > 0))
            {
                var nextState = context.NextChunkIndex > handoff.StartingCommittedChunkIndex &&
                                context.NextChunkIndex > repairFrontierBefore
                    ? V5TransportHandoffState.BackfillRepair
                    : V5TransportHandoffState.FrontierRepairOnly;
                TrySetV5TransportHandoffState(
                    handoff,
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    nextState,
                    context.NextChunkIndex > repairFrontierBefore ? "frontier_repair_applied" : "chunk_batch_proof",
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex);
                handoff.LastProofUtc = DateTimeOffset.UtcNow;
                if (context.NextChunkIndex > repairFrontierBefore)
                {
                    repairProofFrame = new FileTransferRepairProofFrameV6
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        TransportEpoch = handoff.EpochId,
                        RepairRequestId = v5Batch.RepairRequestId ?? handoff.LastRepairRequestId,
                        AppliedChunkCount = acceptedChunks.Count,
                        CommittedChunkIndex = context.NextChunkIndex,
                        RecoveryMode = FormatV5TransportHandoffState(nextState),
                    };
                }
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            nextChunkIndexAfterCommit = context.NextChunkIndex;
            bytesCommittedAfterCommit = context.BytesTransferred;
            pendingChunkCountAfterCommit = GetReceiverPendingChunkCountLocked(context);
            pendingBytesAfterCommit = context.BufferedBytes;
            highestReceivedChunkIndexAfterCommit = context.PullHighestReceivedChunkIndex;
            lateArrivalDistanceAfterCommit = context.PullLateArrivalDistance;
            snapshot = CreateSnapshotLocked();
        }

        LogReceiverWriteBatchCommitted(
            context,
            new InboundWriteBatch(
                acceptedChunks.Select(static item => item.ChunkBytes).ToArray(),
                acceptedChunks.Count,
                sparseWriteBytes,
                nextChunkIndexAfterCommit,
                bytesCommittedAfterCommit,
                pendingChunkCountAfterCommit,
                pendingBytesAfterCommit,
                highestReceivedChunkIndexAfterCommit,
                lateArrivalDistanceAfterCommit,
                0),
            writeStopwatch.ElapsedMilliseconds);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_sparse_write_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; written_chunk_count={acceptedChunks.Count}; written_bytes={sparseWriteBytes}; contiguous_chunks_committed={committedChunkCount}; contiguous_bytes_committed={committedByteCount}; write_duration_ms={writeStopwatch.ElapsedMilliseconds}; next_chunk_index={nextChunkIndexAfterCommit}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_bytes={pendingBytesAfterCommit}");
        if (completed)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_receiver_completed_chunks; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_index={nextChunkIndexAfterCommit}; chunk_count={context.ChunkCount}; bytes_transferred={bytesCommittedAfterCommit}; file_size={context.FileSizeBytes}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_write_chunk_count={pendingChunkCountAfterCommit}; pending_bytes={pendingBytesAfterCommit}");
        }

        if (repairOverlapChunkCount > 0)
        {
            LogInboundV4FrontierRepairBatchApplied(
                context,
                batch,
                repairRequestedRangeStart,
                repairRequestedRangeCount,
                repairFrontierBefore,
                nextChunkIndexAfterCommit,
                repairAcceptedChunkCount,
                repairDuplicateOrStaleChunkCount,
                repairStaleChunkCount,
                repairDuplicateChunkCount,
                repairPendingWriteChunkCount,
                committedChunkCount,
                pendingChunkCountAfterCommit);
            if (nextChunkIndexAfterCommit > repairFrontierBefore)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_frontier_repair_frontier_advanced; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={repairRequestedRangeStart}; requested_missing_range_count={repairRequestedRangeCount}; committed_frontier_before={repairFrontierBefore}; committed_frontier_after={nextChunkIndexAfterCommit}; accepted_chunk_count={repairAcceptedChunkCount}; duplicate_or_stale_chunk_count={repairDuplicateOrStaleChunkCount}; pending_write_chunk_count={repairPendingWriteChunkCount}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}");
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_frontier_repair_still_missing; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={repairRequestedRangeStart}; requested_missing_range_count={repairRequestedRangeCount}; committed_frontier={nextChunkIndexAfterCommit}; accepted_chunk_count={repairAcceptedChunkCount}; duplicate_or_stale_chunk_count={repairDuplicateOrStaleChunkCount}; pending_write_chunk_count={repairPendingWriteChunkCount}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_chunk_count={pendingChunkCountAfterCommit}");
            }
            LogInboundV4RepairChunkObserved(
                context,
                observedRepairKeys,
                batch,
                repairOverlapChunkCount,
                repairAcceptedChunkCount,
                repairDuplicateOrStaleChunkCount,
                repairFrontierBefore,
                nextChunkIndexAfterCommit);
            if (nextChunkIndexAfterCommit > repairFrontierBefore)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_repair_applied; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={(repairProofFrame?.TransportEpoch ?? 0)}; repair_request_id={FormatProtocolLogValue(repairProofFrame?.RepairRequestId ?? "(none)")}; committed_frontier_before={repairFrontierBefore}; committed_frontier_after={nextChunkIndexAfterCommit}; accepted_chunk_count={repairAcceptedChunkCount}; requested_missing_range_start={repairRequestedRangeStart}; requested_missing_range_count={repairRequestedRangeCount}");
            }
            else
            {
                var finalFrontierStatus = repairFrontierChunkStatus == "accepted_for_write"
                    ? "accepted_but_not_committed"
                    : repairFrontierChunkStatus;
                LogInboundV5FrontierRepairStillMissing(
                    context,
                    batch,
                    repairRequestedRangeStart,
                    repairRequestedRangeCount,
                    repairFrontierBefore,
                    nextChunkIndexAfterCommit,
                    repairAcceptedChunkCount,
                    repairDuplicateOrStaleChunkCount,
                    repairPendingWriteChunkCount,
                    repairFrontierChunkObserved,
                    finalFrontierStatus,
                    "frontier_not_committed");
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (completed)
        {
            await SendInboundV4TerminalReadyStateBestEffortAsync(context).ConfigureAwait(false);
        }
        else
        {
            await SendInboundV4StateAsync(context, "chunk_batch_committed", terminalReady: false).ConfigureAwait(false);
            await SendInboundV5RepairRequestAsync(context, "chunk_batch_committed").ConfigureAwait(false);
        }

        if (repairProofFrame is not null)
        {
            await SendInboundV5RepairProofAsync(context, repairProofFrame).ConfigureAwait(false);
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
        }
    }

    private async Task FlushInboundV4PausedProgressAsync(InboundTransferContext context, string reason)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool completed;
        bool shouldSendState;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                context.ReceiverSparseChunksWritten is null ||
                context.UserPaused ||
                context.PeerPaused)
            {
                return;
            }

            var (committedChunkCount, committedByteCount) = CommitInboundV4ContiguousWrittenLocked(context);
            if (committedChunkCount > 0)
            {
                context.PullReceiverSparseContiguousChunksCommittedRecent += committedChunkCount;
                context.PullReceiverContiguousBytesCommittedRecent += committedByteCount;
                context.V4CreditUntilChunkIndexExclusive = ComputeV4CreditUntilExclusiveLocked(context);
                context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                snapshot = CreateSnapshotLocked();
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            shouldSendState = context.DataSession is not null;
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (!shouldSendState)
        {
            return;
        }

        if (completed)
        {
            await SendInboundV4TerminalReadyStateBestEffortAsync(context).ConfigureAwait(false);
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }

        await SendInboundV4StateAsync(context, reason, terminalReady: false).ConfigureAwait(false);
    }

    private async Task SendInboundV5RepairProofAsync(InboundTransferContext context, FileTransferRepairProofFrameV6 proof)
    {
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            dataSession = ReferenceEquals(inboundTransfer, context) && !context.IsTerminal
                ? context.DataSession
                : null;
        }

        if (dataSession is null)
        {
            return;
        }

        try
        {
            await dataSession.SendAsync(proof, context.LifetimeCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={proof.TransportEpoch}; repair_request_id={FormatProtocolLogValue(proof.RepairRequestId ?? "(none)")}; applied_chunk_count={proof.AppliedChunkCount}; committed_chunk={proof.CommittedChunkIndex}; recovery_mode={FormatProtocolLogValue(proof.RecoveryMode ?? "(none)")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={proof.TransportEpoch}; error={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private static (int ChunkCount, long ByteCount) CommitInboundV4ContiguousWrittenLocked(InboundTransferContext context)
    {
        if (context.UserPaused ||
            context.PeerPaused ||
            context.ReceiverSparseChunksWritten is null)
        {
            return (0, 0);
        }

        var committedChunkCount = 0;
        long committedByteCount = 0;
        while (context.NextChunkIndex < context.ChunkCount &&
               context.ReceiverSparseChunksWritten[context.NextChunkIndex])
        {
            var expectedChunkLength = GetExpectedInboundChunkLength(context, context.NextChunkIndex);
            context.ReceiverSparseChunksWritten[context.NextChunkIndex] = false;
            context.NextChunkIndex++;
            context.ChunksTransferred++;
            context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + expectedChunkLength);
            committedChunkCount++;
            committedByteCount += expectedChunkLength;
        }

        if (committedChunkCount > 0)
        {
            context.V4FrontierStallStartedUtc = null;
            context.V4FrontierStallChunkIndex = context.NextChunkIndex;
            context.V4FrontierStallLastSuppressedLogUtc = null;
        }

        return (committedChunkCount, committedByteCount);
    }

    private bool TryValidateInboundV4ChunkBatch(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        out IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks,
        out string? failureMessage)
    {
        chunks = [];
        failureMessage = null;
        int chunkCount;
        int chunkSizeBytes;
        long fileSizeBytes;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal || !context.PullManifestReceived)
            {
                failureMessage = "V4 chunk batch arrived before a valid manifest.";
                return false;
            }

            chunkCount = context.ChunkCount;
            chunkSizeBytes = context.ChunkSizeBytes;
            fileSizeBytes = context.FileSizeBytes;
        }

        if (batch.ChunkCount != batch.DataSegments.Count)
        {
            failureMessage = "V4 chunk batch count did not match the segment count.";
            return false;
        }

        if (batch.StartChunkIndex < 0 ||
            batch.ChunkCount <= 0 ||
            batch.StartChunkIndex + batch.ChunkCount > chunkCount)
        {
            failureMessage = "V4 chunk batch range was out of bounds.";
            return false;
        }

        var result = new List<(int ChunkIndex, byte[] ChunkBytes)>(batch.DataSegments.Count);
        for (var segmentIndex = 0; segmentIndex < batch.DataSegments.Count; segmentIndex++)
        {
            var chunkIndex = batch.StartChunkIndex + segmentIndex;
            var chunkBytes = batch.DataSegments[segmentIndex];
            var expectedChunkLength = GetExpectedChunkLength(fileSizeBytes, chunkSizeBytes, chunkCount, chunkIndex);
            if (chunkBytes.Length != expectedChunkLength)
            {
                failureMessage = "V4 chunk batch segment length did not match the manifest.";
                return false;
            }

            result.Add((chunkIndex, chunkBytes));
        }

        chunks = result;
        return true;
    }

    private async Task<bool> SendInboundV4StateAsync(
        InboundTransferContext context,
        string reason,
        bool terminalReady,
        bool requireMissingRange = false,
        bool forceMissingRange = false,
        bool forceSend = false)
    {
        FileTransferStateFrameV4? state;
        IFileTransferDataSession? dataSession;
        long frontierStallAgeMs;
        int frontierLagChunks;
        int frontierCreditCapChunkIndexExclusive;
        int sparseCreditTargetWithoutFrontierCap;
        int stateCreditWindowChunks;
        bool frontierWindowCreditCapped;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived)
            {
                return false;
            }

            context.V4MixedScreenShareTransfer = context.V4MixedScreenShareTransfer || IsV4MixedScreenShareActive();
            context.V4CreditUntilChunkIndexExclusive = ComputeV4CreditUntilExclusiveLocked(context);
            state = CreateInboundV4StateLocked(context, reason, terminalReady, forceMissingRange);
            if (requireMissingRange && state.MissingRanges.Count == 0)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (!forceSend &&
                ShouldSuppressInboundV4StateLocked(context, state, reason, terminalReady, requireMissingRange, now))
            {
                return false;
            }

            frontierStallAgeMs = context.V4FrontierStallStartedUtc is null
                ? 0
                : (long)Math.Max(0, (now - context.V4FrontierStallStartedUtc.Value).TotalMilliseconds);
            stateCreditWindowChunks = ComputeV4StateCreditWindowChunks(context);
            var stateSparseCreditBase = Math.Max(
                state.ContiguousCommittedChunkIndex,
                state.DurableReceivedHighestChunkIndex + 1);
            sparseCreditTargetWithoutFrontierCap = Math.Min(
                context.ChunkCount,
                stateSparseCreditBase + stateCreditWindowChunks);
            var rawFrontierCreditCapChunkIndexExclusive = Math.Min(
                context.ChunkCount,
                state.ContiguousCommittedChunkIndex + stateCreditWindowChunks);
            frontierCreditCapChunkIndexExclusive = IsV4MixedScreenShareActive()
                ? rawFrontierCreditCapChunkIndexExclusive
                : QuantizeV4CreditTarget(
                    rawFrontierCreditCapChunkIndexExclusive,
                    context.ChunkCount,
                    context.ChunkSizeBytes);
            frontierLagChunks = Math.Max(
                0,
                state.DurableReceivedHighestChunkIndex - state.ContiguousCommittedChunkIndex + 1);
            var postRebindFrontierRecoveryActive = IsInboundV4PostRebindFrontierRecoveryActive(context, frontierLagChunks);
            frontierWindowCreditCapped =
                ShouldClampInboundV4CreditForTransportRebind(context, state, frontierLagChunks) &&
                state.CreditUntilChunkIndexExclusive > frontierCreditCapChunkIndexExclusive;
            if (IsV5TransportHandoffBlockingTail(context.V5TransportHandoff) &&
                context.V5TransportHandoff!.State != V5TransportHandoffState.BackfillRepair)
            {
                var frontierOnlyCredit = Math.Clamp(
                    state.ContiguousCommittedChunkIndex + V4PostFallbackEmergencyFrontierRepairChunks,
                    state.ContiguousCommittedChunkIndex,
                    context.ChunkCount);
                if (state.CreditUntilChunkIndexExclusive > frontierOnlyCredit)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V5TransportHandoff.EpochId}; state={FormatV5TransportHandoffState(context.V5TransportHandoff.State)}; original_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; advertised_credit_until_chunk_index_exclusive={frontierOnlyCredit}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}");
                    state = state with { CreditUntilChunkIndexExclusive = frontierOnlyCredit };
                }
            }

            if (frontierWindowCreditCapped)
            {
                var advertisedCreditUntilChunkIndexExclusive = Math.Clamp(
                    frontierCreditCapChunkIndexExclusive,
                    state.ContiguousCommittedChunkIndex,
                    context.ChunkCount);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_frontier_credit_clamped; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; rebind_generation={context.PullTransportRebindGeneration}; post_rebind_frontier_recovery={(postRebindFrontierRecoveryActive ? 1 : 0)}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; original_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; advertised_credit_until_chunk_index_exclusive={advertisedCreditUntilChunkIndexExclusive}; frontier_lag_chunks={frontierLagChunks}; missing_range_count={state.MissingRanges.Count}");
                state = state with { CreditUntilChunkIndexExclusive = advertisedCreditUntilChunkIndexExclusive };
            }

            context.V4LastStateCreditUntilChunkIndexExclusive = state.CreditUntilChunkIndexExclusive;
            context.V4LastStateContiguousCommittedChunkIndex = state.ContiguousCommittedChunkIndex;
            context.V4LastStateDurableHighestChunkIndex = state.DurableReceivedHighestChunkIndex;
            context.V4LastStateSentUtc = now;
            dataSession = context.DataSession;
        }

        try
        {
            await dataSession.SendAsync(state, context.LifetimeCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_state_sent; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; mixed_screenshare={(IsV4MixedScreenShareActive() ? 1 : 0)}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; credit_window_chunks={stateCreditWindowChunks}; frontier_credit_cap_chunk_index_exclusive={frontierCreditCapChunkIndexExclusive}; sparse_credit_target_without_frontier_cap={sparseCreditTargetWithoutFrontierCap}; frontier_window_credit_capped={(frontierWindowCreditCapped ? 1 : 0)}; frontier_lag_chunks={frontierLagChunks}; missing_range_count={state.MissingRanges.Count}; frontier_stall_age_ms={frontierStallAgeMs}; bytes_committed={state.BytesCommitted}; receiver_memory_pressure={(state.ReceiverMemoryPressure ? 1 : 0)}; receiver_disk_pressure={(state.ReceiverDiskPressure ? 1 : 0)}; terminal_ready={(state.TerminalReady ? 1 : 0)}; transfer_paused={(state.TransferPaused ? 1 : 0)}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send V4 receiver state.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private static bool ShouldClampInboundV4CreditForTransportRebind(
        InboundTransferContext context,
        FileTransferStateFrameV4 state,
        int frontierLagChunks)
    {
        if (!IsInboundV4PostRebindFrontierRecoveryActive(context, frontierLagChunks))
        {
            return false;
        }

        var frontierChunkIndex = state.ContiguousCommittedChunkIndex;
        if (state.MissingRanges.Count == 0)
        {
            return true;
        }

        return state.MissingRanges.Any(range =>
            range.StartChunkIndex <= frontierChunkIndex &&
            range.StartChunkIndex + range.ChunkCount > frontierChunkIndex);
    }

    private static bool IsInboundV4PostRebindFrontierRecoveryActive(
        InboundTransferContext context,
        int frontierLagChunks)
        => IsInboundPostTunaRecoveryActiveLocked(context) &&
           (context.PullPostTunaRecoveryActive || !context.PullTransportRebindRecoveredLogged) &&
           frontierLagChunks > 0;

    private async Task SendInboundV4TerminalReadyStateBestEffortAsync(InboundTransferContext context)
    {
        var transferId = context.TransferId;
        var sessionId = context.SessionId;
        var sendTask = SendInboundV4StateAsync(context, "terminal_ready", terminalReady: true);
        try
        {
            if (sendTask.IsCompleted)
            {
                await sendTask.ConfigureAwait(false);
                return;
            }

            var completedTask = await Task.WhenAny(
                sendTask,
                Task.Delay(V4TerminalReadyStateBestEffortTimeoutMs, context.LifetimeCts.Token)).ConfigureAwait(false);
            if (completedTask == sendTask)
            {
                await sendTask.ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_terminal_ready_state_send_failed; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(ex.Message)}");
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_terminal_ready_state_send_deferred; transfer_id={transferId}; session_id={sessionId}; timeout_ms={V4TerminalReadyStateBestEffortTimeoutMs}");
        _ = ObserveInboundV4TerminalReadyStateSendAsync(sendTask, transferId, sessionId);
    }

    private static async Task ObserveInboundV4TerminalReadyStateSendAsync(Task<bool> sendTask, string transferId, string sessionId)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_terminal_ready_state_send_observe_failed; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private static bool ShouldSuppressInboundV4StateLocked(
        InboundTransferContext context,
        FileTransferStateFrameV4 state,
        string reason,
        bool terminalReady,
        bool requireMissingRange,
        DateTimeOffset now)
    {
        if (!string.Equals(reason, "chunk_batch_committed", StringComparison.Ordinal) ||
            terminalReady ||
            requireMissingRange ||
            state.MissingRanges.Count > 0 ||
            context.V4LastStateSentUtc is null)
        {
            return false;
        }

        var creditAdvance = state.CreditUntilChunkIndexExclusive - context.V4LastStateCreditUntilChunkIndexExclusive;
        var frontierAdvance = state.ContiguousCommittedChunkIndex - context.V4LastStateContiguousCommittedChunkIndex;
        var durableHighestAdvance = state.DurableReceivedHighestChunkIndex - context.V4LastStateDurableHighestChunkIndex;
        if (creditAdvance >= V4StateProgressCreditMinChunks ||
            frontierAdvance >= V4StateProgressCreditMinChunks)
        {
            return false;
        }

        var stateAgeMs = (long)Math.Max(0, (now - context.V4LastStateSentUtc.Value).TotalMilliseconds);
        if (stateAgeMs >= V4StateProgressMaxDelayMs &&
            (creditAdvance > 0 || frontierAdvance > 0 || durableHighestAdvance > 0))
        {
            return false;
        }

        return true;
    }

    private async Task MaybeSendInboundV4FrontierStallStateAsync(InboundTransferContext context)
    {
        var shouldSend = false;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.PullManifestReceived &&
                context.ReceiverSparseWriteActive &&
                ShouldSendInboundV4FrontierStallStateLocked(context, DateTimeOffset.UtcNow))
            {
                shouldSend = true;
            }
        }

        if (shouldSend)
        {
            await SendInboundV4StateAsync(
                context,
                "frontier_stall_repair_due",
                terminalReady: false,
                requireMissingRange: true).ConfigureAwait(false);
            await SendInboundV5RepairRequestAsync(context, "frontier_stall_repair_due").ConfigureAwait(false);
        }
    }

    private static bool ShouldSendInboundV4FrontierStallStateLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.UserPaused ||
            context.PeerPaused ||
            context.PullTransportPaused ||
            context.NextChunkIndex >= context.ChunkCount ||
            context.V4CreditUntilChunkIndexExclusive <= context.NextChunkIndex)
        {
            return false;
        }

        var frontierStallAgeMs = GetInboundV4FrontierStallAgeMsLocked(context, now);
        var repairRepeatIntervalMs = ResolveV4FrontierRepairRepeatIntervalMs(context);
        if (frontierStallAgeMs < repairRepeatIntervalMs)
        {
            return false;
        }

        var repairEndExclusive = Math.Min(
            context.ChunkCount,
            Math.Min(
                Math.Max(context.NextChunkIndex, context.V4CreditUntilChunkIndexExclusive),
                context.NextChunkIndex + V4RepairBurstMaxChunks));
        if (repairEndExclusive <= context.NextChunkIndex)
        {
            return false;
        }

        var previousFrontierRepair = FindRecentInboundV4FrontierTailRepairLocked(context, context.NextChunkIndex);
        return previousFrontierRepair?.LastRequestedUtc is null ||
            now - previousFrontierRepair.LastRequestedUtc.Value >= TimeSpan.FromMilliseconds(repairRepeatIntervalMs);
    }

    private async Task<bool> SendInboundV4CompleteAsync(InboundTransferContext context, string sessionId, string transferId, long fileSizeBytes, string sha256Base64, CancellationToken ct)
    {
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            dataSession = context.DataSession;
        }

        if (dataSession is null)
        {
            return false;
        }

        try
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_complete_send_started; transfer_id={transferId}; session_id={sessionId}; file_size_bytes={fileSizeBytes}");
            await dataSession.SendAsync(
                new FileTransferCompleteFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = fileSizeBytes,
                    Sha256Base64 = sha256Base64,
                },
                ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id={sessionId}; file_size_bytes={fileSizeBytes}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send V4 completion.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private async Task FailInboundV4Async(InboundTransferContext context, string errorCode, string statusMessage, string errorMessage)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_receiver_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; error_code={errorCode}; reason={FormatProtocolLogValue(statusMessage)}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: errorCode,
            statusMessage: statusMessage,
            sendError: true,
            errorMessage: errorMessage,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TrySendInboundV4ErrorAsync(InboundTransferContext context, string errorCode, string message)
    {
        IFileTransferDataSession? dataSession;
        string sessionId;
        string transferId;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            dataSession = context.DataSession;
            sessionId = context.SessionId;
            transferId = context.TransferId;
        }

        if (dataSession is null)
        {
            return;
        }

        try
        {
            await dataSession.SendAsync(
                new FileTransferErrorFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = message,
                },
                context.LifetimeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_error_send_failed; transfer_id={transferId}; session_id={sessionId}; error_code={errorCode}; reason={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private FileTransferStateFrameV4 CreateInboundV4StateLocked(
        InboundTransferContext context,
        string reason,
        bool terminalReady,
        bool forceMissingRange = false)
    {
        context.V4StateEpoch++;
        return new FileTransferReceiverStateFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4StateEpoch,
            ContiguousCommittedChunkIndex = context.NextChunkIndex,
            DurableReceivedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
            CreditUntilChunkIndexExclusive = context.V4CreditUntilChunkIndexExclusive,
            MissingRanges = BuildInboundV4MissingRangesLocked(context, forceMissingRange),
            BytesCommitted = context.BytesTransferred,
            ReceiverMemoryPressure = context.ReceiverBufferPressureActive,
            ReceiverDiskPressure = false,
            TerminalReady = terminalReady,
            TransferPaused = context.UserPaused,
            TransferPauseReason = context.UserPauseReason,
            TransportEpoch = context.V5TransportHandoff?.EpochId ?? 0,
            RepairRequestId = context.V5TransportHandoff?.LastRepairRequestId,
            Priority = context.V5TransportHandoff is null
                ? null
                : context.V5TransportHandoff.State == V5TransportHandoffState.BackfillRepair
                    ? "backfill"
                    : "frontier",
            RecoveryMode = context.V5TransportHandoff is null
                ? null
                : FormatV5TransportHandoffState(context.V5TransportHandoff.State),
        };
    }

    private IReadOnlyList<FileTransferRangeV4> BuildInboundV4MissingRangesLocked(
        InboundTransferContext context,
        bool forceFrontierRepair = false)
    {
        var written = context.ReceiverSparseChunksWritten;
        if (context.UserPaused ||
            context.PeerPaused ||
            written is null ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            context.V4FrontierStallStartedUtc = null;
            context.V4FrontierStallChunkIndex = -1;
            context.V4FrontierStallLastSuppressedLogUtc = null;
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        ClearFilledInboundV4RepairRequestsLocked(context, now);
        var frontierStallAgeMs = GetInboundV4FrontierStallAgeMsLocked(context, now);
        var frontierRepairRepeatIntervalMs = ResolveV4FrontierRepairRepeatIntervalMs(context);
        var frontierSuppressedLogIntervalMs = ResolveV4RepairSuppressedLogIntervalMs(context, frontierRepairRepeatIntervalMs);
        if (!forceFrontierRepair &&
            context.PullHighestReceivedChunkIndex >= context.NextChunkIndex &&
            frontierStallAgeMs < frontierRepairRepeatIntervalMs)
        {
            if (context.V4FrontierStallLastSuppressedLogUtc is null ||
                now - context.V4FrontierStallLastSuppressedLogUtc.Value >= TimeSpan.FromMilliseconds(frontierSuppressedLogIntervalMs))
            {
                context.V4FrontierStallLastSuppressedLogUtc = now;
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=stall_age_below_min; epoch={context.V4StateEpoch}; start_chunk_index={context.NextChunkIndex}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, frontierRepairRepeatIntervalMs - frontierStallAgeMs)}; repair_interval_ms={frontierRepairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
            }

            return [];
        }

        var previousFrontierRepair = FindRecentInboundV4RepairStateForRangeLocked(
            context,
            context.NextChunkIndex,
            Math.Min(context.ChunkCount, context.NextChunkIndex + 1));
        var sameFrontierRetry = previousFrontierRepair?.LastRequestedUtc is not null &&
            previousFrontierRepair.FirstStartChunkIndex == context.NextChunkIndex;
        var frontierRetryNarrowed = false;
        var originalFrontierRangeChunkCount = 0;
        var ranges = new List<FileTransferRangeV4>();
        var totalMissingChunks = 0;
        if (context.PullHighestReceivedChunkIndex >= context.NextChunkIndex)
        {
            var upperInclusive = Math.Min(context.PullHighestReceivedChunkIndex, context.ChunkCount - 1);
            var chunkIndex = context.NextChunkIndex;
            while (chunkIndex <= upperInclusive &&
                   ranges.Count < FileTransferProtocol.MaxStateMissingRangesV4 &&
                   totalMissingChunks < V4RepairBurstMaxChunks)
            {
                if (written[chunkIndex] || context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex))
                {
                    chunkIndex++;
                    continue;
                }

                var start = chunkIndex;
                var count = 0;
                var isFrontierRange = start == context.NextChunkIndex;
                var exactFrontierRepairRequired = isFrontierRange &&
                    IsInboundV5ExactFrontierRepairRequiredLocked(context);
                var maxRangeChunks = isFrontierRange && IsInboundPostTunaRecoveryActiveLocked(context)
                    ? exactFrontierRepairRequired
                        ? V4PostFallbackEmergencyFrontierRepairChunks
                        : ResolveV4PostFallbackFrontierRepairChunks(context)
                    : isFrontierRange && sameFrontierRetry
                    ? ResolveV4FrontierTailRetryChunks(context)
                    : isFrontierRange
                        ? ResolveV4InitialFrontierRepairChunks(context)
                        : V4RepairBurstMaxChunks;
                while (chunkIndex <= upperInclusive &&
                       !written[chunkIndex] &&
                       !context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex) &&
                       totalMissingChunks + count < V4RepairBurstMaxChunks &&
                       count < maxRangeChunks)
                {
                    count++;
                    chunkIndex++;
                }

                if (isFrontierRange)
                {
                    originalFrontierRangeChunkCount = count;
                    while (chunkIndex <= upperInclusive &&
                           !written[chunkIndex] &&
                           !context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex))
                    {
                        originalFrontierRangeChunkCount++;
                        chunkIndex++;
                    }

                    frontierRetryNarrowed = sameFrontierRetry && originalFrontierRangeChunkCount > count;
                }

                if (count > 0)
                {
                    ranges.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
                    totalMissingChunks += count;
                    if (isFrontierRange)
                    {
                        break;
                    }
                }
            }
        }

        var frontierTailRepair = false;
        V4ReceiverRepairRequestState? previousFrontierTailRepair = null;
        if (ranges.Count == 0)
        {
            var repairEndExclusive = Math.Min(
                context.ChunkCount,
                Math.Min(
                    Math.Max(context.NextChunkIndex, context.V4CreditUntilChunkIndexExclusive),
                    context.NextChunkIndex + V4RepairBurstMaxChunks));
            if (repairEndExclusive > context.NextChunkIndex)
            {
                if (forceFrontierRepair || frontierStallAgeMs >= frontierRepairRepeatIntervalMs)
                {
                    frontierTailRepair = true;
                    previousFrontierTailRepair = FindRecentInboundV4FrontierTailRepairLocked(context, context.NextChunkIndex);
                    var maxRepairCount = repairEndExclusive - context.NextChunkIndex;
                    var repairCount = IsInboundPostTunaRecoveryActiveLocked(context)
                        ? Math.Min(
                            IsInboundV5ExactFrontierRepairRequiredLocked(context)
                                ? V4PostFallbackEmergencyFrontierRepairChunks
                                : ResolveV4PostFallbackFrontierRepairChunks(context),
                            maxRepairCount)
                        : previousFrontierTailRepair is null
                        ? Math.Min(ResolveV4InitialFrontierRepairChunks(context), maxRepairCount)
                        : Math.Min(ResolveV4FrontierTailRetryChunks(context), maxRepairCount);
                    ranges.Add(new FileTransferRangeV4
                    {
                        StartChunkIndex = context.NextChunkIndex,
                        ChunkCount = repairCount,
                    });
                    totalMissingChunks = ranges[0].ChunkCount;
                    if (previousFrontierTailRepair is not null && repairCount < maxRepairCount)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_repair_retry_narrowed; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_repair_request_key={previousFrontierTailRepair.RepairRequestKey}; start_chunk_index={context.NextChunkIndex}; original_frontier_range_chunk_count={maxRepairCount}; narrowed_requested_chunk_count={repairCount}; retained_requested_chunk_count={repairCount}; retained_range_count=1; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    }
                }
                else if (context.V4FrontierStallLastSuppressedLogUtc is null ||
                         now - context.V4FrontierStallLastSuppressedLogUtc.Value >= TimeSpan.FromMilliseconds(frontierSuppressedLogIntervalMs))
                {
                    context.V4FrontierStallLastSuppressedLogUtc = now;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=stall_age_below_min; epoch={context.V4StateEpoch}; start_chunk_index={context.NextChunkIndex}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, frontierRepairRepeatIntervalMs - frontierStallAgeMs)}; repair_interval_ms={frontierRepairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }
            }
        }

        if (ranges.Count == 0)
        {
            return [];
        }

        if (!frontierTailRepair &&
            frontierRetryNarrowed)
        {
            var requestedChunkCountAfterNarrow = ranges.Sum(static range => range.ChunkCount);
            var previousRepairRequestKey = previousFrontierRepair?.RepairRequestKey ?? "(none)";
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_retry_narrowed; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_repair_request_key={previousRepairRequestKey}; start_chunk_index={ranges[0].StartChunkIndex}; original_frontier_range_chunk_count={originalFrontierRangeChunkCount}; narrowed_requested_chunk_count={ranges[0].ChunkCount}; retained_requested_chunk_count={requestedChunkCountAfterNarrow}; retained_range_count={ranges.Count}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
        }

        if (!frontierTailRepair && ranges.Count > 1)
        {
            var retainedRanges = new List<FileTransferRangeV4> { ranges[0] };
            foreach (var range in ranges.Skip(1))
            {
                var rangeEndExclusive = range.StartChunkIndex + range.ChunkCount;
                var recentRangeRepair = FindRecentInboundV4RepairStateForRangeLocked(
                    context,
                    range.StartChunkIndex,
                    rangeEndExclusive);
                if (recentRangeRepair?.LastRequestedUtc is not null)
                {
                    var retryInMs = V4RepairRepeatIntervalMs - (long)Math.Max(
                        0,
                        (now - recentRangeRepair.LastRequestedUtc.Value).TotalMilliseconds);
                    if (retryInMs > 0)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_repair_suppressed; direction=receiver; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={recentRangeRepair.RepairRequestKey}; reason=range_retry_interval; epoch={context.V4StateEpoch}; range_count=1; requested_chunk_count={range.ChunkCount}; first_start_chunk_index={range.StartChunkIndex}; last_end_chunk_exclusive={rangeEndExclusive}; retry_in_ms={retryInMs}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; frontier_tail_repair=0; frontier_stall_age_ms={frontierStallAgeMs}");
                        continue;
                    }
                }

                retainedRanges.Add(range);
            }

            ranges = retainedRanges;
        }

        var requestedChunkCount = ranges.Sum(static range => range.ChunkCount);
        var firstStart = ranges[0].StartChunkIndex;
        var lastEndExclusive = ranges[^1].StartChunkIndex + ranges[^1].ChunkCount;
        var repairRequestKey = CreateV4RepairRequestKey(
            context.TransferId,
            firstStart,
            requestedChunkCount,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex,
            ranges);

        var firstRangeEndExclusiveForRetry = ranges[0].StartChunkIndex + ranges[0].ChunkCount;
        var overlappingRepair = FindOverlappingInboundV4RepairStateLocked(
            context,
            firstStart,
            firstRangeEndExclusiveForRetry,
            repairRequestKey);

        if (!context.V4ReceiverRepairRequests.TryGetValue(repairRequestKey, out var repairState))
        {
            repairState = new V4ReceiverRepairRequestState
            {
                RepairRequestKey = repairRequestKey,
                FirstSeenUtc = overlappingRepair?.FirstSeenUtc ?? now,
                FirstStartChunkIndex = firstStart,
                LastEndChunkExclusive = lastEndExclusive,
                RequestedChunkCount = requestedChunkCount,
                Ranges = ranges
                    .Select(static range => new FileTransferRangeV4
                    {
                        StartChunkIndex = range.StartChunkIndex,
                        ChunkCount = range.ChunkCount,
                    })
                    .ToArray(),
                FrontierTailRepair = frontierTailRepair,
            };
            context.V4ReceiverRepairRequests[repairRequestKey] = repairState;
        }

        var lastRequestedUtc = repairState.LastRequestedUtc;
        if (overlappingRepair?.LastRequestedUtc is not null &&
            (lastRequestedUtc is null || overlappingRepair.LastRequestedUtc.Value > lastRequestedUtc.Value))
        {
            lastRequestedUtc = overlappingRepair.LastRequestedUtc;
        }

        if (frontierTailRepair &&
            previousFrontierTailRepair?.LastRequestedUtc is not null &&
            (lastRequestedUtc is null || previousFrontierTailRepair.LastRequestedUtc.Value > lastRequestedUtc.Value))
        {
            lastRequestedUtc = previousFrontierTailRepair.LastRequestedUtc;
        }

        var repairRepeatIntervalMs = ResolveV4RepairRepeatIntervalMs(context, firstStart, frontierTailRepair);
        var repairSuppressedLogIntervalMs = ResolveV4RepairSuppressedLogIntervalMs(context, repairRepeatIntervalMs);
        var due = forceFrontierRepair ||
            lastRequestedUtc is null ||
            now - lastRequestedUtc.Value >= TimeSpan.FromMilliseconds(repairRepeatIntervalMs);
        if (!due)
        {
            var retryInMs = repairRepeatIntervalMs - (long)Math.Max(0, (now - lastRequestedUtc!.Value).TotalMilliseconds);
            if (repairState.LastSuppressedLogUtc is null ||
                now - repairState.LastSuppressedLogUtc.Value >= TimeSpan.FromMilliseconds(repairSuppressedLogIntervalMs))
            {
                repairState.LastSuppressedLogUtc = now;
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_suppressed; direction=receiver; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; reason=retry_interval; epoch={context.V4StateEpoch}; attempt_count={repairState.AttemptCount}; range_count={ranges.Count}; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; retry_in_ms={Math.Max(0, retryInMs)}; repair_interval_ms={repairRepeatIntervalMs}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; frontier_tail_repair={(repairState.FrontierTailRepair ? 1 : 0)}; frontier_stall_age_ms={frontierStallAgeMs}; overlap_repair_request_key={overlappingRepair?.RepairRequestKey ?? "(none)"}");
                if (frontierTailRepair)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=retry_interval; epoch={context.V4StateEpoch}; repair_request_key={repairRequestKey}; start_chunk_index={firstStart}; requested_chunk_count={requestedChunkCount}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, retryInMs)}; repair_interval_ms={repairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }
            }

            return [];
        }

        repairState.LastRequestedUtc = now;
        repairState.AttemptCount++;
        repairState.Filled = false;
        var emergencyFrontierRepair =
            IsInboundPostTunaRecoveryActiveLocked(context) &&
            ranges.Count == 1 &&
            ranges[0].StartChunkIndex == context.NextChunkIndex &&
            ranges[0].ChunkCount == V4PostFallbackEmergencyFrontierRepairChunks;
        var postFallbackFrontierBackfillRepair =
            IsInboundPostTunaRecoveryActiveLocked(context) &&
            ranges.Count == 1 &&
            ranges[0].StartChunkIndex == context.NextChunkIndex &&
            ranges[0].ChunkCount > V4PostFallbackEmergencyFrontierRepairChunks;
        var logFrontierRepairLoop = !frontierTailRepair ||
            ShouldLogInboundV4RebindFrontierLoopLocked(context, now, repairRepeatIntervalMs);
        if (frontierTailRepair)
        {
            if (logFrontierRepairLoop)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; attempt_count={repairState.AttemptCount}; start_chunk_index={context.NextChunkIndex}; requested_chunk_count={ranges[0].ChunkCount}; frontier_stall_age_ms={frontierStallAgeMs}; repair_interval_ms={repairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; rebind_generation={context.PullTransportRebindGeneration}");
                if (emergencyFrontierRepair)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_v4_emergency_frontier_repair_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; rebind_generation={context.PullTransportRebindGeneration}; start_chunk_index={context.NextChunkIndex}; requested_chunk_count={ranges[0].ChunkCount}; frontier_stall_age_ms={frontierStallAgeMs}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }
                else if (postFallbackFrontierBackfillRepair)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_post_fallback_frontier_backfill_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; rebind_generation={context.PullTransportRebindGeneration}; start_chunk_index={context.NextChunkIndex}; requested_chunk_count={ranges[0].ChunkCount}; repair_window_chunks={ResolveV4PostFallbackFrontierRepairChunks(context)}; frontier_stall_age_ms={frontierStallAgeMs}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }
            }
        }
        else if (emergencyFrontierRepair)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_emergency_frontier_repair_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; rebind_generation={context.PullTransportRebindGeneration}; start_chunk_index={context.NextChunkIndex}; requested_chunk_count={ranges[0].ChunkCount}; frontier_stall_age_ms={frontierStallAgeMs}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
        }
        else if (postFallbackFrontierBackfillRepair)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_post_fallback_frontier_backfill_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; rebind_generation={context.PullTransportRebindGeneration}; start_chunk_index={context.NextChunkIndex}; requested_chunk_count={ranges[0].ChunkCount}; repair_window_chunks={ResolveV4PostFallbackFrontierRepairChunks(context)}; frontier_stall_age_ms={frontierStallAgeMs}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
        }

        if (logFrontierRepairLoop)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; epoch={context.V4StateEpoch}; attempt_count={repairState.AttemptCount}; range_count={ranges.Count}; requested_chunk_count={requestedChunkCount}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; first_seen_age_ms={(long)Math.Max(0, (now - repairState.FirstSeenUtc).TotalMilliseconds)}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; frontier_tail_repair={(repairState.FrontierTailRepair ? 1 : 0)}; frontier_stall_age_ms={frontierStallAgeMs}; repair_interval_ms={repairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; rebind_generation={context.PullTransportRebindGeneration}");
        }
        return ranges;
    }

    private static int ResolveV4InitialFrontierRepairChunks(InboundTransferContext context)
        => IsInboundPostTunaRecoveryActiveLocked(context)
            ? ResolveV4PostFallbackFrontierRepairChunks(context)
            : context.V4MixedScreenShareTransfer
            ? V4MixedInitialFrontierRepairChunks
            : IsV4FileOnlyFastRepairEnabled()
                ? V4FileOnlyInitialFrontierRepairChunks
                : V4KnownFrontierRepairChunks;

    private static int ResolveV4FrontierTailRetryChunks(InboundTransferContext context)
        => IsInboundPostTunaRecoveryActiveLocked(context)
            ? ResolveV4PostFallbackFrontierRepairChunks(context)
            : context.V4MixedScreenShareTransfer
            ? V4FrontierTailRetryChunks
            : IsV4FileOnlyFastRepairEnabled()
                ? V4FileOnlyFrontierTailRetryChunks
                : V4FrontierTailRetryChunks;

    private static int ResolveV4PostFallbackFrontierRepairChunks(InboundTransferContext context)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context))
        {
            return V4PostFallbackEmergencyFrontierRepairChunks;
        }

        if (IsInboundV5ExactFrontierRepairRequiredLocked(context))
        {
            return V4PostFallbackEmergencyFrontierRepairChunks;
        }

        var current = context.PullTransportRebindFrontierRepairWindowChunks;
        if (current <= 0)
        {
            current = V4PostFallbackEmergencyFrontierRepairChunks;
        }

        return Math.Clamp(
            current,
            V4PostFallbackEmergencyFrontierRepairChunks,
            V4PostFallbackFrontierBackfillStep3Chunks);
    }

    private static bool IsInboundV5ExactFrontierRepairRequiredLocked(InboundTransferContext context)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context) ||
            context.NextChunkIndex < 0 ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        if (context.V5TransportHandoff is { } handoff)
        {
            return handoff.State is V5TransportHandoffState.NknProofPending
                or V5TransportHandoffState.FrontierRepairOnly
                or V5TransportHandoffState.WaitingForRegularNkn;
        }

        return IsInboundPostFallbackFrontierGapMissingLocked(context);
    }

    private static bool IsInboundPostFallbackFrontierGapMissingLocked(InboundTransferContext context)
    {
        var written = context.ReceiverSparseChunksWritten;
        return written is not null &&
               context.PullHighestReceivedChunkIndex >= context.NextChunkIndex &&
               context.NextChunkIndex < written.Length &&
               !written[context.NextChunkIndex] &&
               !context.ReceiverSparseChunksPendingWrite.Contains(context.NextChunkIndex);
    }

    private static void UpdateInboundV4PostFallbackFrontierRepairWindowLocked(
        InboundTransferContext context,
        int frontierBefore,
        int frontierAfter,
        int batchStartChunkIndex,
        int batchChunkCount)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context) ||
            frontierAfter <= frontierBefore)
        {
            return;
        }

        var committedAdvance = Math.Max(0, frontierAfter - frontierBefore);
        if (committedAdvance <= 0)
        {
            return;
        }

        var previousWindow = ResolveV4PostFallbackFrontierRepairChunks(context);
        context.PullTransportRebindFrontierRepairCommittedChunks += committedAdvance;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = frontierAfter;

        var committedChunks = context.PullTransportRebindFrontierRepairCommittedChunks;
        var nextWindow = committedChunks >= V4PostFallbackFrontierBackfillStep3AfterCommittedChunks
            ? V4PostFallbackFrontierBackfillStep3Chunks
            : committedChunks >= V4PostFallbackFrontierBackfillStep2AfterCommittedChunks
                ? V4PostFallbackFrontierBackfillStep2Chunks
                : committedChunks >= V4PostFallbackFrontierBackfillStep1AfterCommittedChunks
                    ? V4PostFallbackFrontierBackfillStep1Chunks
                    : V4PostFallbackEmergencyFrontierRepairChunks;

        nextWindow = Math.Max(previousWindow, nextWindow);
        nextWindow = Math.Clamp(
            nextWindow,
            V4PostFallbackEmergencyFrontierRepairChunks,
            V4PostFallbackFrontierBackfillStep3Chunks);
        if (nextWindow <= previousWindow)
        {
            return;
        }

        context.PullTransportRebindFrontierRepairWindowChunks = nextWindow;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_post_fallback_frontier_backfill_window_changed; transfer_id={context.TransferId}; session_id={context.SessionId}; rebind_generation={context.PullTransportRebindGeneration}; previous_window_chunks={previousWindow}; repair_window_chunks={nextWindow}; committed_frontier_before={frontierBefore}; committed_frontier_after={frontierAfter}; committed_advance_chunks={committedAdvance}; total_repair_committed_chunks={committedChunks}; batch_start_chunk_index={batchStartChunkIndex}; batch_chunk_count={batchChunkCount}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
    }

    private static int ResolveV4FrontierRepairRepeatIntervalMs(InboundTransferContext context)
        => context.V4MixedScreenShareTransfer || !IsV4FileOnlyFastRepairEnabled()
            ? V4RepairRepeatIntervalMs
            : V4FileOnlyFrontierRepairRepeatIntervalMs;

    private static int ResolveV4RepairRepeatIntervalMs(
        InboundTransferContext context,
        int firstStartChunkIndex,
        bool frontierTailRepair)
        => frontierTailRepair || firstStartChunkIndex == context.NextChunkIndex
            ? ResolveV4FrontierRepairRepeatIntervalMs(context)
            : V4RepairRepeatIntervalMs;

    private static int ResolveV4RepairSuppressedLogIntervalMs(
        InboundTransferContext context,
        int repairRepeatIntervalMs)
        => IsInboundPostTunaRecoveryActiveLocked(context)
            ? Math.Max(repairRepeatIntervalMs, 5000)
            : repairRepeatIntervalMs;

    private static bool ShouldLogInboundV4RebindFrontierLoopLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        int repairRepeatIntervalMs)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context))
        {
            return true;
        }

        var minimumIntervalMs = Math.Max(repairRepeatIntervalMs, 5000);
        if (context.PullTransportRebindLastFrontierRepairLoopLogUtc is not null &&
            now - context.PullTransportRebindLastFrontierRepairLoopLogUtc.Value < TimeSpan.FromMilliseconds(minimumIntervalMs))
        {
            return false;
        }

        context.PullTransportRebindLastFrontierRepairLoopLogUtc = now;
        return true;
    }

    private static int ResolveV4SenderRepairRepeatIntervalMs(OutboundTransferContext context, bool frontierRepair)
        => frontierRepair && IsV4FileOnlyFastRepairEnabled(context)
            ? V4FileOnlyFrontierRepairRepeatIntervalMs
            : V4RepairRepeatIntervalMs;

    private static bool IsV4FileOnlyFastRepairEnabled(OutboundTransferContext context)
        => !context.V4MixedScreenShareTransfer && IsV4FileOnlyFastRepairEnabled();

    private static bool IsV4FileOnlyFastRepairEnabled()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(V4FileOnlyFastRepairEnvironmentVariableName, category: "filetransfer_tuning");
        return value is null ||
               (!string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase));
    }

    private static long GetInboundV4FrontierStallAgeMsLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.NextChunkIndex >= context.ChunkCount)
        {
            context.V4FrontierStallStartedUtc = null;
            context.V4FrontierStallChunkIndex = -1;
            context.V4FrontierStallLastSuppressedLogUtc = null;
            return 0;
        }

        if (context.V4FrontierStallStartedUtc is null ||
            context.V4FrontierStallChunkIndex != context.NextChunkIndex)
        {
            context.V4FrontierStallStartedUtc = now;
            context.V4FrontierStallChunkIndex = context.NextChunkIndex;
            context.V4FrontierStallLastSuppressedLogUtc = null;
            return 0;
        }

        return (long)Math.Max(0, (now - context.V4FrontierStallStartedUtc.Value).TotalMilliseconds);
    }

    private static void ClearFilledInboundV4RepairRequestsLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (context.V4ReceiverRepairRequests.Count == 0)
        {
            return;
        }

        foreach (var key in context.V4ReceiverRepairRequests.Keys.ToArray())
        {
            var repairState = context.V4ReceiverRepairRequests[key];
            var clearReason = string.Empty;
            if (IsInboundV4RepairStateObsoleteForFrontierLocked(context, repairState))
            {
                clearReason = "frontier_advanced";
                if (!repairState.Filled)
                {
                    repairState.Filled = true;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_repair_filled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; request_to_fill_ms={(repairState.LastRequestedUtc is null ? -1 : (long)Math.Max(0, (now - repairState.LastRequestedUtc.Value).TotalMilliseconds))}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    if (repairState.FrontierTailRepair)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_frontier_stall_missing_range_filled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; reason={clearReason}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; request_to_fill_ms={(repairState.LastRequestedUtc is null ? -1 : (long)Math.Max(0, (now - repairState.LastRequestedUtc.Value).TotalMilliseconds))}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    }
                }
            }
            else if (IsInboundV4RepairStateFilledLocked(context, repairState))
            {
                clearReason = "range_filled";
                if (!repairState.Filled)
                {
                    repairState.Filled = true;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_repair_filled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; request_to_fill_ms={(repairState.LastRequestedUtc is null ? -1 : (long)Math.Max(0, (now - repairState.LastRequestedUtc.Value).TotalMilliseconds))}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    if (repairState.FrontierTailRepair)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_frontier_stall_missing_range_filled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; reason={clearReason}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; request_to_fill_ms={(repairState.LastRequestedUtc is null ? -1 : (long)Math.Max(0, (now - repairState.LastRequestedUtc.Value).TotalMilliseconds))}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    }
                }
            }

            if (!string.IsNullOrEmpty(clearReason))
            {
                context.V4ReceiverRepairRequests.Remove(key);
                if (string.Equals(clearReason, "frontier_advanced", StringComparison.Ordinal))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_repair_obsolete_after_frontier_advance; direction=receiver; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_cleared; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairState.RepairRequestKey}; reason={clearReason}; first_start_chunk_index={repairState.FirstStartChunkIndex}; last_end_chunk_exclusive={repairState.LastEndChunkExclusive}; requested_chunk_count={repairState.RequestedChunkCount}; attempt_count={repairState.AttemptCount}; contiguous_committed_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
            }
        }
    }

    private static bool IsInboundV4RepairStateObsoleteForFrontierLocked(
        InboundTransferContext context,
        V4ReceiverRepairRequestState repairState)
        => (repairState.FrontierTailRepair && context.NextChunkIndex > repairState.FirstStartChunkIndex) ||
           (!repairState.FrontierTailRepair && context.NextChunkIndex >= repairState.LastEndChunkExclusive);

    private static bool IsInboundV4RepairRangeFilledLocked(InboundTransferContext context, int startChunkIndex, int endChunkExclusive)
    {
        if (context.ReceiverSparseChunksWritten is null)
        {
            return false;
        }

        var start = Math.Max(0, startChunkIndex);
        var end = Math.Min(context.ChunkCount, endChunkExclusive);
        if (end <= start)
        {
            return false;
        }

        for (var chunkIndex = start; chunkIndex < end; chunkIndex++)
        {
            if (chunkIndex < context.NextChunkIndex)
            {
                continue;
            }

            if (!context.ReceiverSparseChunksWritten[chunkIndex])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInboundV4RepairStateFilledLocked(InboundTransferContext context, V4ReceiverRepairRequestState repairState)
    {
        if (repairState.Ranges.Count == 0)
        {
            return false;
        }

        foreach (var range in repairState.Ranges)
        {
            if (!IsInboundV4RepairRangeFilledLocked(
                    context,
                    range.StartChunkIndex,
                    range.StartChunkIndex + range.ChunkCount))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InboundV4RepairStateContainsChunk(V4ReceiverRepairRequestState repairState, int chunkIndex)
        => repairState.Ranges.Any(range =>
            chunkIndex >= range.StartChunkIndex &&
            chunkIndex < range.StartChunkIndex + range.ChunkCount);

    private static bool InboundV4RepairStateOverlapsRange(
        V4ReceiverRepairRequestState repairState,
        int startChunkIndex,
        int endChunkExclusive)
        => repairState.Ranges.Any(range =>
            RangesOverlap(range.StartChunkIndex, range.StartChunkIndex + range.ChunkCount, startChunkIndex, endChunkExclusive));

    private static V4ReceiverRepairRequestState? FindInboundV4RepairStateForChunkLocked(InboundTransferContext context, int chunkIndex)
    {
        if (context.V4ReceiverRepairRequests.Count == 0)
        {
            return null;
        }

        return context.V4ReceiverRepairRequests.Values
            .Where(repairState => !repairState.Filled &&
                                  !IsInboundV4RepairStateObsoleteForFrontierLocked(context, repairState) &&
                                  InboundV4RepairStateContainsChunk(repairState, chunkIndex))
            .OrderByDescending(static repairState => repairState.FirstStartChunkIndex)
            .ThenByDescending(static repairState => repairState.LastRequestedUtc ?? repairState.FirstSeenUtc)
            .FirstOrDefault();
    }

    private static V4ReceiverRepairRequestState? FindOverlappingInboundV4RepairStateLocked(
        InboundTransferContext context,
        int startChunkIndex,
        int endChunkExclusive,
        string repairRequestKey)
    {
        if (context.V4ReceiverRepairRequests.Count == 0)
        {
            return null;
        }

        return context.V4ReceiverRepairRequests.Values
            .Where(repairState => !repairState.Filled &&
                                  !IsInboundV4RepairStateObsoleteForFrontierLocked(context, repairState) &&
                                  !string.Equals(repairState.RepairRequestKey, repairRequestKey, StringComparison.Ordinal) &&
                                  InboundV4RepairStateOverlapsRange(repairState, startChunkIndex, endChunkExclusive))
            .OrderByDescending(static repairState => repairState.LastRequestedUtc ?? repairState.FirstSeenUtc)
            .FirstOrDefault();
    }

    private static V4ReceiverRepairRequestState? FindRecentInboundV4RepairStateForRangeLocked(
        InboundTransferContext context,
        int startChunkIndex,
        int endChunkExclusive)
    {
        if (context.V4ReceiverRepairRequests.Count == 0)
        {
            return null;
        }

        return context.V4ReceiverRepairRequests.Values
            .Where(repairState => !repairState.Filled &&
                                  !IsInboundV4RepairStateObsoleteForFrontierLocked(context, repairState) &&
                                  repairState.LastRequestedUtc is not null &&
                                  InboundV4RepairStateOverlapsRange(repairState, startChunkIndex, endChunkExclusive))
            .OrderByDescending(static repairState => repairState.LastRequestedUtc)
            .FirstOrDefault();
    }

    private static bool RangesOverlap(int firstStart, int firstEndExclusive, int secondStart, int secondEndExclusive)
        => firstStart < secondEndExclusive && secondStart < firstEndExclusive;

    private static V4ReceiverRepairRequestState? FindRecentInboundV4FrontierTailRepairLocked(
        InboundTransferContext context,
        int frontierChunkIndex)
    {
        if (context.V4ReceiverRepairRequests.Count == 0)
        {
            return null;
        }

        return context.V4ReceiverRepairRequests.Values
            .Where(repairState => repairState.FrontierTailRepair &&
                                  !repairState.Filled &&
                                  !IsInboundV4RepairStateObsoleteForFrontierLocked(context, repairState) &&
                                  repairState.FirstStartChunkIndex == frontierChunkIndex &&
                                  repairState.LastRequestedUtc is not null)
            .OrderByDescending(static repairState => repairState.LastRequestedUtc)
            .FirstOrDefault();
    }

    private static string SelectInboundV4RepairBatchIgnoreReason(
        int staleChunkCount,
        int duplicateChunkCount,
        int pendingWriteChunkCount)
    {
        if (staleChunkCount > 0)
        {
            return "stale_chunk";
        }

        if (pendingWriteChunkCount > 0)
        {
            return "pending_write";
        }

        if (duplicateChunkCount > 0)
        {
            return "duplicate_chunk";
        }

        return "no_accepted_chunks";
    }

    private static void LogInboundV4FrontierRepairBatchReceived(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        int requestedRangeStart,
        int requestedRangeCount,
        int frontierBefore,
        int acceptedChunkCount,
        int duplicateOrStaleChunkCount,
        int staleChunkCount,
        int duplicateChunkCount,
        int pendingWriteChunkCount,
        int overlapChunkCount)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_frontier_repair_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={requestedRangeStart}; requested_missing_range_count={requestedRangeCount}; committed_frontier_before={frontierBefore}; accepted_chunk_count={acceptedChunkCount}; duplicate_or_stale_chunk_count={duplicateOrStaleChunkCount}; stale_chunk_count={staleChunkCount}; duplicate_chunk_count={duplicateChunkCount}; pending_write_chunk_count={pendingWriteChunkCount}; repair_overlap_chunk_count={overlapChunkCount}; invalid_segment_count=0; invalid_segment_length=0");

    private static void LogInboundV4FrontierRepairBatchIgnored(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        string reason,
        int requestedRangeStart,
        int requestedRangeCount,
        int frontierBefore,
        int duplicateOrStaleChunkCount,
        int staleChunkCount,
        int duplicateChunkCount,
        int pendingWriteChunkCount)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_frontier_repair_batch_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={requestedRangeStart}; requested_missing_range_count={requestedRangeCount}; committed_frontier_before={frontierBefore}; committed_frontier_after={frontierBefore}; accepted_chunk_count=0; duplicate_or_stale_chunk_count={duplicateOrStaleChunkCount}; stale_chunk_count={staleChunkCount}; duplicate_chunk_count={duplicateChunkCount}; pending_write_chunk_count={pendingWriteChunkCount}; invalid_segment_count=0; invalid_segment_length=0");

    private static void LogInboundV4FrontierRepairBatchApplied(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        int requestedRangeStart,
        int requestedRangeCount,
        int frontierBefore,
        int frontierAfter,
        int acceptedChunkCount,
        int duplicateOrStaleChunkCount,
        int staleChunkCount,
        int duplicateChunkCount,
        int pendingWriteChunkCount,
        int committedChunkCount,
        int pendingChunkCountAfterCommit)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_frontier_repair_batch_applied; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={requestedRangeStart}; requested_missing_range_count={requestedRangeCount}; committed_frontier_before={frontierBefore}; committed_frontier_after={frontierAfter}; accepted_chunk_count={acceptedChunkCount}; duplicate_or_stale_chunk_count={duplicateOrStaleChunkCount}; stale_chunk_count={staleChunkCount}; duplicate_chunk_count={duplicateChunkCount}; pending_write_chunk_count={pendingWriteChunkCount}; contiguous_chunks_committed={committedChunkCount}; pending_chunk_count={pendingChunkCountAfterCommit}; invalid_segment_count=0; invalid_segment_length=0");

    private static void LogInboundV4RepairChunkObserved(
        InboundTransferContext context,
        IReadOnlyCollection<string> observedRepairKeys,
        FileTransferChunkBatchFrameV4 batch,
        int overlapChunkCount,
        int acceptedChunkCount,
        int duplicateOrStaleChunkCount,
        int frontierBefore,
        int frontierAfter)
    {
        var firstKey = observedRepairKeys.Count > 0 ? observedRepairKeys.First() : "(none)";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_repair_chunk_observed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={firstKey}; matched_key_count={observedRepairKeys.Count}; overlap_chunk_count={overlapChunkCount}; accepted_chunk_count={acceptedChunkCount}; duplicate_or_stale_chunk_count={duplicateOrStaleChunkCount}; frontier_before={frontierBefore}; frontier_after={frontierAfter}; frontier_advanced={(frontierAfter > frontierBefore ? 1 : 0)}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}");
    }

    private static void LogInboundV5FrontierRepairStillMissing(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        int requestedRangeStart,
        int requestedRangeCount,
        int frontierBefore,
        int frontierAfter,
        int acceptedChunkCount,
        int duplicateOrStaleChunkCount,
        int pendingWriteChunkCount,
        bool frontierChunkObserved,
        string frontierChunkStatus,
        string reason)
    {
        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !IsInboundPostTunaRecoveryActiveLocked(context))
        {
            return;
        }

        var transportEpoch = batch is FileTransferChunkBatchFrameV6 v5Batch
            ? v5Batch.TransportEpoch
            : context.V5TransportHandoff?.EpochId ?? 0;
        var repairRequestId = batch is FileTransferChunkBatchFrameV6 v5RepairBatch
            ? v5RepairBatch.RepairRequestId
            : context.V5TransportHandoff?.LastRepairRequestId;
        var now = DateTimeOffset.UtcNow;
        var sameFrontier = context.LastV5FrontierRepairStillMissingChunkIndex == frontierBefore;
        if (sameFrontier &&
            context.LastV5FrontierRepairStillMissingLogUtc is { } lastLogUtc &&
            now - lastLogUtc < TimeSpan.FromSeconds(2))
        {
            context.SuppressedV5FrontierRepairStillMissingLogCount++;
            return;
        }

        var suppressedCount = context.SuppressedV5FrontierRepairStillMissingLogCount;
        context.LastV5FrontierRepairStillMissingLogUtc = now;
        context.LastV5FrontierRepairStillMissingChunkIndex = frontierBefore;
        context.SuppressedV5FrontierRepairStillMissingLogCount = 0;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_repair_still_missing; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; requested_missing_range_start={requestedRangeStart}; requested_missing_range_count={requestedRangeCount}; committed_frontier_before={frontierBefore}; committed_frontier_after={frontierAfter}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunkCount}; duplicate_or_stale_chunk_count={duplicateOrStaleChunkCount}; pending_write_chunk_count={pendingWriteChunkCount}; frontier_chunk_observed={(frontierChunkObserved ? 1 : 0)}; frontier_chunk_status={FormatProtocolLogValue(frontierChunkStatus)}; suppressed_count={suppressedCount}");
    }

    private int ComputeV4CreditUntilExclusiveLocked(InboundTransferContext context)
    {
        if (context.ChunkSizeBytes <= 0 || context.ChunkCount <= 0)
        {
            return 0;
        }

        if (context.UserPaused || context.PeerPaused)
        {
            return Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount);
        }

        var windowChunks = ComputeV4StateCreditWindowChunks(context);
        var creditBase = Math.Max(context.NextChunkIndex, context.PullHighestReceivedChunkIndex + 1);
        var rawTarget = Math.Min(context.ChunkCount, creditBase + windowChunks);
        if (IsV4MixedScreenShareActive())
        {
            return Math.Max(context.V4CreditUntilChunkIndexExclusive, rawTarget);
        }

        var quantumChunks = Math.Max(1, (int)Math.Ceiling(V4StateCreditGrantQuantumBytes / (double)context.ChunkSizeBytes));
        var target = rawTarget;
        if (rawTarget < context.ChunkCount)
        {
            var quantizedTarget = checked(((rawTarget + quantumChunks - 1) / quantumChunks) * quantumChunks);
            target = Math.Min(context.ChunkCount, quantizedTarget);
        }

        return Math.Max(context.V4CreditUntilChunkIndexExclusive, target);
    }

    private int ComputeV4StateCreditWindowChunks(InboundTransferContext context)
        => IsV4MixedScreenShareActive()
            ? ResolveV4StateCreditWindowChunksForCurrentMode()
            : Math.Max(1, (int)Math.Ceiling(V4FileOnlySparseCreditWindowBytes / (double)Math.Max(1, context.ChunkSizeBytes)));

    private int ResolveV4StateCreditWindowChunksForCurrentMode()
        => sessionScreenShareDegraded
            ? V4MixedScreenShareDegradedCreditWindowChunks
            : V4MixedScreenShareCreditWindowChunks;

    private static int QuantizeV4CreditTarget(int rawTarget, int chunkCount, int chunkSizeBytes)
    {
        if (rawTarget >= chunkCount)
        {
            return chunkCount;
        }

        var quantumChunks = Math.Max(1, (int)Math.Ceiling(V4StateCreditGrantQuantumBytes / (double)Math.Max(1, chunkSizeBytes)));
        return Math.Min(chunkCount, checked(((rawTarget + quantumChunks - 1) / quantumChunks) * quantumChunks));
    }

    private static void ValidateV4SparseDestination(Stream stream)
    {
        ValidateWritableStream(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("V4 sparse receive destination must be readable and seekable.");
        }
    }

    private static long GetExpectedChunkLength(long fileSizeBytes, int chunkSizeBytes, int chunkCount, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= chunkCount)
        {
            return -1;
        }

        if (chunkIndex == chunkCount - 1)
        {
            var consumedBeforeLast = (long)chunkIndex * chunkSizeBytes;
            return fileSizeBytes - consumedBeforeLast;
        }

        return chunkSizeBytes;
    }

    private static bool IsInboundV4ChunkPresentOrPendingLocked(InboundTransferContext context, int chunkIndex)
        => context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex) ||
           context.ReceiverSparseChunksWritten is not null &&
           chunkIndex >= 0 &&
           chunkIndex < context.ReceiverSparseChunksWritten.Length &&
           context.ReceiverSparseChunksWritten[chunkIndex];

    private static bool IsFrameForContext(InboundTransferContext context, FileTransferDataFrame frame)
        => string.Equals(frame.SessionId, context.SessionId, StringComparison.Ordinal) &&
           string.Equals(frame.TransferId, context.TransferId, StringComparison.Ordinal);

    private static bool IsFrameForContext(OutboundTransferContext context, FileTransferDataFrame frame)
        => string.Equals(frame.SessionId, context.SessionId, StringComparison.Ordinal) &&
           string.Equals(frame.TransferId, context.TransferId, StringComparison.Ordinal);

    private static void LogInboundV4FrameIgnored(InboundTransferContext context, FileTransferDataFrame frame, string reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_data_frame_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FormatProtocolLogValue(frame.Type)}; reason={reason}");
    }
}
