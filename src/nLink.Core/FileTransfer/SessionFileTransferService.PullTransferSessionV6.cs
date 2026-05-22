using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundV6SenderAsync(OutboundTransferContext context)
    {
        IFileTransferDataSession? dataSession = null;
        try
        {
            var currentTransport = GetTransportOrThrow();
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = context.RouteSelection.ProtocolVersion,
                FileTransferRoute = context.RouteSelection.TelemetryToken,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = context.ChunkSizeBytes,
                InitialPipelineDepth = ResolveOutboundInitialPipelineDepth(context),
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
                context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
                context.RemoteNextExpectedChunkIndex = 0;
                context.RemoteGrantedUntilExclusive = 0;
                context.ChunksAcceptedForTransport = 0;
                context.BytesAcceptedForTransport = 0;
                context.V6LastReceiverStateEpoch = -1;
                context.V6LastReceiverFeedbackReceivedUtc = null;
                context.V6NextSequentialSourceChunkIndex = 0;
                context.V6PriorityRequestedChunks.Clear();
                context.V6NormalRequestedChunks.Clear();
                context.V6RequestedChunkMetadataByChunkIndex.Clear();
                context.V6AppliedFrontierRequestIds.Clear();
                context.V6CurrentNormalRequestKey = null;
                ClearOutboundV6RegularNknDegradedProfileLocked(context);
                context.V6RegularNknInferredFrontierObservedChunkIndex = -1;
                context.V6RegularNknInferredFrontierObservedUtc = null;
                context.V6LastInferredRegularNknFrontierRepairChunkIndex = -1;
                context.V6LastInferredRegularNknFrontierRepairReceiverStateEpoch = -1;
                context.V6LastInferredRegularNknFrontierRepairUtc = null;
                context.V6LastInferredRegularNknFrontierRepairRequestId = null;
                context.V6LastInferredRegularNknFrontierRepairSuppressedLogUtc = null;
                context.V6LastInferredRegularNknFrontierRepairSuppressedReason = null;
                context.V6UseRegularNknRedundantData = false;
                context.V6TunaRedundantDataEpochId = 0;
                context.V6TunaRedundantDataSatisfiedEpochId = 0;
                context.V6TunaRedundantDataProbeStartedUtc = null;
                context.V6TunaRedundantDataProbeStartedBytes = 0;
                context.V6SenderPumpLastWakeReason = "startup";
                context.PullSentChunkCache.Clear();
                context.PullSentChunkCacheBytes = 0;
                context.PullSenderFeedCreditWaitStartedUtc = null;
                context.V4SenderCreditExhaustedSinceUtc = null;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_sender_started; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; route={context.RouteSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; request_driven=1");
            LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Outbound, "sender", context.RouteSelection);

            UpdateOutboundState(context, FileTransferTransferState.AwaitingStart, 0, 0, "Starting V6 file transfer.");
            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                reason: FormatFileTransferSessionOpenReason(sessionOpen.SessionRole, sessionOpen.ProtocolVersion, sessionOpen.ChunkSizeBytes, sessionOpen.InitialPipelineDepth, context.RouteSelection),
                routeSelection: context.RouteSelection);

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
                $"event=filetransfer_v6_manifest_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size_bytes={context.FileSizeBytes}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}");
            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for V6 receiver requests.");
            if (context.UserPaused)
            {
                await SendOutboundV4PauseControlAsync(context, "user_paused_initial").ConfigureAwait(false);
            }

            Task<FileTransferReceivedDataFrame>? pendingReceiveTask = dataSession.ReceiveWithMetadataAsync(context.LifetimeCts.Token).AsTask();
            var senderPumpTask = RunOutboundV6SenderPumpAsync(context, stream, dataSession);
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                pendingReceiveTask ??= dataSession.ReceiveWithMetadataAsync(context.LifetimeCts.Token).AsTask();

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

                    continue;
                }

                var received = await pendingReceiveTask.ConfigureAwait(false);
                var frame = received.Frame;
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (!IsFrameForContext(context, frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "session_or_transfer_mismatch_v6");
                    continue;
                }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "protocol_not_v6");
                    continue;
                }

                TouchOutboundV6PeerLivenessIfAuthoritative(context, received, "v6_data_frame");
                switch (frame)
                {
                    case FileTransferReceiverStateFrameV6 state:
                        ApplyOutboundV6ReceiverState(context, state, received.TransportKind);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferFrontierRequestFrameV6 frontierRequest:
                        ApplyOutboundV6FrontierRequest(context, frontierRequest, received.TransportKind);
                        SignalOutboundV4SenderPump(context);
                        break;
                    case FileTransferTransportEpochFrameV6:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "transport_epoch_control_required");
                        break;
                    case FileTransferTransportProbeFrameV6 probe:
                        await HandleReceivedV6TransportProbeFrameAsync(
                            context.SessionId,
                            context.TransferId,
                            FileTransferDirection.Outbound,
                            probe,
                            received.TransportKind).ConfigureAwait(false);
                        break;
                    case FileTransferRepairProofFrameV6:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "repair_proof_control_required");
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferCompleteFrameV4 complete:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=complete; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; file_size_bytes={complete.FileSizeBytes}");
                        break;
                    case FileTransferCancelFrameV4 cancel:
                    {
                        var reason = NormalizeReason(cancel.Reason);
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_priority_received; kind=cancel; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=outbound; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=redundant_data_frame");
                        await TransitionOutboundToTerminalAsync(
                                context,
                                FileTransferTransferState.Canceled,
                                errorCode: FileTransferResultCodes.CanceledRemote,
                                statusMessage: reason ?? "Transfer canceled by peer.",
                                notifyPeer: false,
                                cancelReason: null,
                                ct: CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                    case FileTransferErrorFrameV4 error:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=error; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; error_code={NormalizeErrorCode(error.ErrorCode) ?? InvalidStateErrorCode}");
                        break;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame_v6");
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
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void ApplyOutboundV6ReceiverState(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        FileTransferTransportKind receivedTransportKind)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.V6LastReceiverFeedbackReceivedUtc = DateTimeOffset.UtcNow;
            var receiverProgressChanged = UpdateOutboundReceiverAcknowledgedProgressFromV4StateLocked(context, state);
            if (state.Epoch <= context.V6LastReceiverStateEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={(state.Epoch == context.V6LastReceiverStateEpoch ? "duplicate_epoch" : "stale_epoch")}; epoch={state.Epoch}; previous_epoch={context.V6LastReceiverStateEpoch}; missing_range_count={state.MissingRanges.Count}");
                if (receiverProgressChanged)
                {
                    snapshot = CreateSnapshotLocked();
                }
            }
            else
            {
                var previousRemoteFrontier = context.RemoteNextExpectedChunkIndex;
                var committed = Math.Clamp(state.ContiguousCommittedChunkIndex, 0, context.ChunkCount);
                context.V6LastReceiverStateEpoch = state.Epoch;
                context.RemoteNextExpectedChunkIndex = Math.Max(context.RemoteNextExpectedChunkIndex, committed);
                var receiverAdvancedFrontier = context.RemoteNextExpectedChunkIndex > previousRemoteFrontier;
                context.RemoteGrantedUntilExclusive = Math.Clamp(state.CreditUntilChunkIndexExclusive, context.RemoteNextExpectedChunkIndex, context.ChunkCount);
                context.ChunksTransferred = Math.Max(context.ChunksTransferred, context.RemoteNextExpectedChunkIndex);
                context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(context.FileSizeBytes, state.BytesCommitted));
                context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred);
                context.PeerPaused = state.TransferPaused;
                context.PeerPauseReason = NormalizeReason(state.TransferPauseReason);
                context.PeerPausedSinceUtc = state.TransferPaused ? DateTimeOffset.UtcNow : null;
                if (receiverAdvancedFrontier)
                {
                    ClearOutboundV6RegularNknInferredFrontierObservationLocked(context);
                }

                MaybeEnableOutboundV6RegularNknRedundantDataLocked(context, state.BytesCommitted);
                foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
                {
                    context.SentAwaitingAck.Remove(chunkIndex);
                }

                foreach (var chunkIndex in context.V6ChunkSendsInFlight.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
                {
                    context.V6ChunkSendsInFlight.Remove(chunkIndex);
                }

                PruneOutboundV6RequestedChunksBeforeLocked(context, context.RemoteNextExpectedChunkIndex);
                TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
                var stateProvesFrontierGap = OutboundV6ReceiverStateRequestsCurrentFrontierLocked(
                    context,
                    state,
                    receiverAdvancedFrontier);
                if (!stateProvesFrontierGap)
                {
                    ClearOutboundV6RegularNknInferredFrontierObservationLocked(context);
                }

                IReadOnlyList<FileTransferRangeV4> stateFrontierPriorityRanges = stateProvesFrontierGap
                    ? BuildOutboundV6StateFrontierPriorityRangesLocked(context, state)
                    : [];
                var stateRequestsFrontierGap = stateFrontierPriorityRanges.Count > 0;
                UpdateOutboundV6RegularNknDegradedProfileLocked(
                    context,
                    state,
                    previousRemoteFrontier,
                    receiverAdvancedFrontier,
                    stateRequestsFrontierGap);
                if (stateRequestsFrontierGap)
                {
                    EnterOutboundV6RegularNknFrontierPressureLocked(context, state);
                    PreemptOutboundV6NormalPipelineForReceiverStateFrontierLocked(context, state, stateFrontierPriorityRanges);
                }
                else if (ShouldAcceptOutboundV6NormalReceiverStateRequestsLocked(context, state.TransportEpoch))
                {
                    var normalRegularNknProgressClearsPressure =
                        ShouldClearOutboundV6RegularNknFrontierPressureOnProgressLocked(
                            context,
                            state,
                            receiverAdvancedFrontier);
                    MaybeClearOutboundV6RegularNknFrontierPressureLocked(
                        context,
                        state,
                        "receiver_state_progress",
                        forceClear: normalRegularNknProgressClearsPressure);
                    ReplaceOutboundV6NormalRequestedRangesLocked(
                        context,
                        state.MissingRanges,
                        new V6OutboundChunkRequestMetadata(
                            $"state:{state.Epoch}",
                            Priority: false,
                            state.TransportEpoch,
                            state.RepairRequestId,
                            PriorityName: null,
                            state.RecoveryMode,
                            RequiresExplicitFrontierRequest: stateProvesFrontierGap,
                            AllowNormalRefillBypass: !receiverAdvancedFrontier),
                        obsoleteBeforeChunkIndex: context.RemoteNextExpectedChunkIndex);
                }
                else
                {
                    ClearOutboundV6NormalRequestedChunksLocked(
                        context,
                        state.TransportEpoch,
                        "transport_epoch_unresolved");
                }

                if (state.MissingRanges.Count == 0)
                {
                    MaybeClearOutboundV6RegularNknFrontierPressureLocked(context, state, "receiver_state_empty_window");
                }

                if (stateRequestsFrontierGap && context.RemoteNextExpectedChunkIndex < context.ChunkCount)
                {
                    var forceRegularNknBulk =
                        !IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, state.TransportEpoch) &&
                        ShouldForceOutboundV6PeerRequestedPriorityOverRegularNkn(
                        context,
                        state.TransportEpoch,
                        state.RecoveryMode,
                        receivedTransportKind);
                    var frontierRepairRequestId = ResolveOutboundV6StateFrontierRepairRequestIdLocked(
                        context,
                        state,
                        context.RemoteNextExpectedChunkIndex);
                    var inferredRegularNknRepair = IsCurrentOutboundV6RegularNknInferredFrontierRepairLocked(
                        context,
                        state,
                        context.RemoteNextExpectedChunkIndex);
                    var frontierRecoveryMode = inferredRegularNknRepair
                        ? "regular_nkn_inferred_frontier_stall"
                        : state.RecoveryMode;
                    QueueOutboundV6RequestedRangesLocked(
                        context,
                        stateFrontierPriorityRanges,
                        new V6OutboundChunkRequestMetadata(
                            inferredRegularNknRepair
                                ? $"state-frontier-inferred:{context.RemoteNextExpectedChunkIndex}"
                                : $"state-frontier:{state.Epoch}:{context.RemoteNextExpectedChunkIndex}",
                            Priority: true,
                            state.TransportEpoch,
                            frontierRepairRequestId,
                            state.Priority ?? "frontier",
                            frontierRecoveryMode,
                            forceRegularNknBulk),
                        obsoleteBeforeChunkIndex: context.RemoteNextExpectedChunkIndex);
                }

                context.V6SenderPumpLastWakeReason = state.MissingRanges.Count > 0
                    ? "receiver_state_request"
                    : state.TransferPaused
                        ? "peer_user_paused"
                        : "receiver_state_progress";
                context.StatusMessage = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
                    ? GetV6TransportEpochStatus(context.V6TransportEpoch!)
                    : state.TransferPaused
                        ? "Peer paused transfer."
                        : state.MissingRanges.Count > 0
                        ? "Sending requested V6 file data."
                        : "Waiting for V6 receiver requests.";
                snapshot = CreateSnapshotLocked();

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; committed_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; diagnostic_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; transfer_paused={(state.TransferPaused ? 1 : 0)}");
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private static IReadOnlyList<FileTransferRangeV4> BuildOutboundV6StateFrontierPriorityRangesLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state)
    {
        var frontierChunkIndex = context.RemoteNextExpectedChunkIndex;
        var now = DateTimeOffset.UtcNow;
        var regularNknFrontierStall = IsV6RegularNknFrontierStallMode(state.RecoveryMode);
        if (frontierChunkIndex >= context.ChunkCount ||
            (!regularNknFrontierStall && state.DurableReceivedHighestChunkIndex < frontierChunkIndex))
        {
            return [];
        }

        var maxPriorityChunks = ResolveOutboundV6StateFrontierPriorityChunksLocked(context, state);
        var ranges = NormalizeV6RequestRanges(state.MissingRanges, context.ChunkCount);
        foreach (var range in ranges)
        {
            var rangeEndExclusive = range.StartChunkIndex + range.ChunkCount;
            if (rangeEndExclusive <= frontierChunkIndex)
            {
                continue;
            }

            if (range.StartChunkIndex > frontierChunkIndex)
            {
                return [];
            }

            var count = Math.Min(
                context.ChunkCount - frontierChunkIndex,
                rangeEndExclusive - frontierChunkIndex);
            if (maxPriorityChunks <= 0)
            {
                maxPriorityChunks = ResolveOutboundV6RegularNknInferredStateFrontierPriorityChunksLocked(
                    context,
                    state,
                    frontierChunkIndex,
                    count,
                    now);
            }

            count = Math.Min(maxPriorityChunks, count);
            if (count <= 0)
            {
                return [];
            }

            return
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = frontierChunkIndex,
                    ChunkCount = count,
                },
            ];
        }

        return [];
    }

    private static bool OutboundV6ReceiverStateRequestsCurrentFrontierLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        bool receiverAdvancedFrontier)
    {
        var frontierChunkIndex = context.RemoteNextExpectedChunkIndex;
        var regularNknFrontierStall = IsV6RegularNknFrontierStallMode(state.RecoveryMode);
        if (ShouldClearOutboundV6RegularNknFrontierPressureOnProgressLocked(
                context,
                state,
                receiverAdvancedFrontier))
        {
            return false;
        }

        if (frontierChunkIndex >= context.ChunkCount ||
            (!regularNknFrontierStall && state.DurableReceivedHighestChunkIndex < frontierChunkIndex))
        {
            return false;
        }

        foreach (var range in NormalizeV6RequestRanges(state.MissingRanges, context.ChunkCount))
        {
            var rangeEndExclusive = range.StartChunkIndex + range.ChunkCount;
            if (range.StartChunkIndex <= frontierChunkIndex &&
                frontierChunkIndex < rangeEndExclusive)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldClearOutboundV6RegularNknFrontierPressureOnProgressLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        bool receiverAdvancedFrontier)
        => receiverAdvancedFrontier &&
           !IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
           ((state.TransportEpoch <= 0 &&
             IsOutboundV6RegularNknPrimaryPathLocked(context)) ||
            IsRecoveredOutboundV6RegularNknEpoch(context, state.TransportEpoch) ||
            IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, state.TransportEpoch));

    private static int ResolveOutboundV6StateFrontierPriorityChunksLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state)
    {
        var epoch = context.V6TransportEpoch;
        if (IsV6TransportEpochUnresolved(epoch) &&
            state.TransportEpoch == epoch!.EpochId)
        {
            return ResolveOutboundV6EpochFrontierPriorityChunksLocked(context);
        }

        if (IsRecoveredOutboundV6RegularNknEpoch(context, state.TransportEpoch))
        {
            return V6RecoveredRegularNknFrontierPriorityBurstChunks;
        }

        if (IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, state.TransportEpoch))
        {
            return V6RecoveredRegularNknFrontierPriorityBurstChunks;
        }

        if (IsV6RegularNknFrontierStallMode(state.RecoveryMode))
        {
            return V6RegularNknFrontierRepairBurstChunks;
        }

        return 0;
    }

    private static int ResolveOutboundV6EpochFrontierPriorityChunksLocked(OutboundTransferContext context)
        => context.V6TransportEpoch is
        {
            TargetTransport: FileTransferTransportKind.RegularNkn,
            Kind: FileTransferTransportHandoffKind.RegularNknRecovery,
        }
            ? V6RegularNknFrontierRepairBurstChunks
            : V6EpochFrontierRequestChunks;

    private static int ResolveOutboundV6RegularNknInferredStateFrontierPriorityChunksLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        int frontierChunkIndex,
        int availableFrontierChunks,
        DateTimeOffset now)
    {
        if (state.TransportEpoch > 0 ||
            context.V6TransportEpoch is not null ||
            IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return 0;
        }

        if (context.UserPaused || context.PeerPaused || state.TransferPaused)
        {
            LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
                context,
                state,
                frontierChunkIndex,
                "paused",
                0,
                now);
            return 0;
        }

        if (context.V6RegularNknInferredFrontierObservedChunkIndex != frontierChunkIndex ||
            context.V6RegularNknInferredFrontierObservedUtc is null)
        {
            context.V6RegularNknInferredFrontierObservedChunkIndex = frontierChunkIndex;
            context.V6RegularNknInferredFrontierObservedUtc = now;
            LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
                context,
                state,
                frontierChunkIndex,
                "stall_observation_started",
                0,
                now);
            return 0;
        }

        var stallThreshold = CurrentV6RegularNknInferredFrontierRepairStall;
        var cooldown = CurrentV6RegularNknInferredFrontierRepairCooldown;
        var elapsedMs = (long)Math.Max(0, (now - context.V6RegularNknInferredFrontierObservedUtc.Value).TotalMilliseconds);
        if (elapsedMs < stallThreshold.TotalMilliseconds)
        {
            LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
                context,
                state,
                frontierChunkIndex,
                "stall_grace",
                elapsedMs,
                now);
            return 0;
        }

        if (context.V6ChunkSendsInFlight.ContainsKey(frontierChunkIndex) ||
            context.V6PriorityRequestedChunks.Contains(frontierChunkIndex))
        {
            LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
                context,
                state,
                frontierChunkIndex,
                "frontier_priority_pending",
                elapsedMs,
                now);
            return 0;
        }

        if (context.V6LastInferredRegularNknFrontierRepairChunkIndex == frontierChunkIndex &&
            context.V6LastInferredRegularNknFrontierRepairUtc is { } lastRepairUtc &&
            now - lastRepairUtc < cooldown)
        {
            LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
                context,
                state,
                frontierChunkIndex,
                "cooldown",
                elapsedMs,
                now);
            return 0;
        }

        var chunkCount = Math.Min(
            Math.Min(V6RegularNknInferredFrontierRepairBurstChunks, availableFrontierChunks),
            context.ChunkCount - frontierChunkIndex);
        if (chunkCount <= 0)
        {
            return 0;
        }

        var repairRequestId = $"v6-inferred-frontier:{frontierChunkIndex}";
        context.V6LastInferredRegularNknFrontierRepairChunkIndex = frontierChunkIndex;
        context.V6LastInferredRegularNknFrontierRepairReceiverStateEpoch = state.Epoch;
        context.V6LastInferredRegularNknFrontierRepairUtc = now;
        context.V6LastInferredRegularNknFrontierRepairRequestId = repairRequestId;

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_state_frontier_repair_request_inferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={state.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; receiver_state_epoch={state.Epoch}; frontier_chunk_index={frontierChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; inferred_chunk_count={chunkCount}; stall_elapsed_ms={elapsedMs}; stall_threshold_ms={(long)stallThreshold.TotalMilliseconds}; cooldown_ms={(long)cooldown.TotalMilliseconds}");

        return chunkCount;
    }

    private static void LogOutboundV6RegularNknInferredStateFrontierRepairDeferredLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        int frontierChunkIndex,
        string reason,
        long elapsedMs,
        DateTimeOffset now)
    {
        if (string.Equals(context.V6LastInferredRegularNknFrontierRepairSuppressedReason, reason, StringComparison.Ordinal) &&
            context.V6LastInferredRegularNknFrontierRepairSuppressedLogUtc is { } lastLogUtc &&
            now - lastLogUtc < TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoverySuppressedLogIntervalMs))
        {
            return;
        }

        context.V6LastInferredRegularNknFrontierRepairSuppressedReason = reason;
        context.V6LastInferredRegularNknFrontierRepairSuppressedLogUtc = now;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_state_frontier_repair_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; receiver_state_epoch={state.Epoch}; frontier_chunk_index={frontierChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; stall_elapsed_ms={elapsedMs}; stall_threshold_ms={(long)CurrentV6RegularNknInferredFrontierRepairStall.TotalMilliseconds}; cooldown_ms={(long)CurrentV6RegularNknInferredFrontierRepairCooldown.TotalMilliseconds}");
    }

    private static TimeSpan CurrentV6RegularNknInferredFrontierRepairStall =>
        V6RegularNknInferredFrontierRepairStallOverrideForTests ??
        TimeSpan.FromMilliseconds(V6RegularNknInferredFrontierRepairStallMs);

    private static TimeSpan CurrentV6RegularNknInferredFrontierRepairCooldown =>
        V6RegularNknInferredFrontierRepairCooldownOverrideForTests ??
        TimeSpan.FromMilliseconds(V6RegularNknInferredFrontierRepairCooldownMs);

    private static bool IsCurrentOutboundV6RegularNknInferredFrontierRepairLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        int frontierChunkIndex)
        => context.V6LastInferredRegularNknFrontierRepairChunkIndex == frontierChunkIndex &&
           context.V6LastInferredRegularNknFrontierRepairReceiverStateEpoch == state.Epoch &&
           !string.IsNullOrWhiteSpace(context.V6LastInferredRegularNknFrontierRepairRequestId);

    private static void ClearOutboundV6RegularNknInferredFrontierObservationLocked(OutboundTransferContext context)
    {
        context.V6RegularNknInferredFrontierObservedChunkIndex = -1;
        context.V6RegularNknInferredFrontierObservedUtc = null;
        context.V6LastInferredRegularNknFrontierRepairSuppressedLogUtc = null;
        context.V6LastInferredRegularNknFrontierRepairSuppressedReason = null;
    }

    private string? ResolveOutboundV6StateFrontierRepairRequestIdLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        int frontierChunkIndex)
    {
        var repairRequestId = state.RepairRequestId?.Trim();
        var epoch = context.V6TransportEpoch;
        if (string.IsNullOrWhiteSpace(repairRequestId) &&
            IsV6TransportEpochUnresolved(epoch) &&
            state.TransportEpoch == epoch!.EpochId &&
            frontierChunkIndex < context.ChunkCount)
        {
            repairRequestId = $"v6-state-frontier:{state.TransportEpoch}:{state.Epoch}:{frontierChunkIndex}";
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_state_frontier_repair_request_inferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={state.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; receiver_state_epoch={state.Epoch}; frontier_chunk_index={frontierChunkIndex}; recovery_mode={FormatProtocolLogValue(state.RecoveryMode ?? "(none)")}");
        }
        else if (string.IsNullOrWhiteSpace(repairRequestId) &&
            state.TransportEpoch <= 0 &&
            IsCurrentOutboundV6RegularNknInferredFrontierRepairLocked(context, state, frontierChunkIndex))
        {
            repairRequestId = context.V6LastInferredRegularNknFrontierRepairRequestId;
        }

        if (!string.IsNullOrWhiteSpace(repairRequestId) &&
            IsV6TransportEpochUnresolved(epoch) &&
            state.TransportEpoch == epoch!.EpochId)
        {
            epoch.LastRepairRequestId = repairRequestId;
            context.V6PendingEpochRepairRequestIds.Add(repairRequestId);
        }

        return repairRequestId;
    }

    private static void MaybeEnableOutboundV6RegularNknRedundantDataLocked(
        OutboundTransferContext context,
        long committedBytes)
    {
        var recoveredRegularNknEpochId = ResolveRecoveredOutboundV6RegularNknEpochId(context);
        if (recoveredRegularNknEpochId > 0)
        {
            if (context.V6RegularNknRedundantDataDisabledEpochId == recoveredRegularNknEpochId)
            {
                context.V6UseRegularNknRedundantData = false;
                context.V6RegularNknRedundantDataEpochId = 0;
                context.V6RegularNknRedundantDataBatchCount = 0;
                return;
            }

            if (context.V6RegularNknRedundantDataEpochId != recoveredRegularNknEpochId ||
                !context.V6UseRegularNknRedundantData)
            {
                context.V6RegularNknRedundantDataEpochId = recoveredRegularNknEpochId;
                context.V6RegularNknRedundantDataBatchCount = 0;
                context.V6TunaRedundantDataEpochId = 0;
                context.V6TunaRedundantDataSatisfiedEpochId = 0;
                context.V6TunaRedundantDataProbeStartedUtc = null;
                context.V6TunaRedundantDataProbeStartedBytes = 0;
                context.V6UseRegularNknRedundantData = true;
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v6_regular_nkn_redundant_data_enabled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={recoveredRegularNknEpochId}; reason=regular_nkn_recovered_after_tuna_fallback; committed_bytes={committedBytes}; regular_nkn_pipeline_depth={V6RegularNknFallbackSenderPipelineDepth}");
            }

            return;
        }

        var recoveredTunaEpochId = ResolveRecoveredOutboundV6TunaActivationEpochId(context);
        if (recoveredTunaEpochId <= 0)
        {
            context.V6TunaRedundantDataEpochId = 0;
            context.V6TunaRedundantDataSatisfiedEpochId = 0;
            context.V6TunaRedundantDataProbeStartedUtc = null;
            context.V6TunaRedundantDataProbeStartedBytes = 0;
            context.V6RegularNknRedundantDataEpochId = 0;
            context.V6RegularNknRedundantDataBatchCount = 0;
            context.V6UseRegularNknRedundantData = false;
            return;
        }

        if (context.V6TunaRedundantDataSatisfiedEpochId == recoveredTunaEpochId)
        {
            context.V6UseRegularNknRedundantData = false;
            context.V6RegularNknRedundantDataEpochId = 0;
            context.V6RegularNknRedundantDataBatchCount = 0;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.V6TunaRedundantDataEpochId != recoveredTunaEpochId ||
            context.V6TunaRedundantDataProbeStartedUtc is null)
        {
            context.V6TunaRedundantDataEpochId = recoveredTunaEpochId;
            context.V6RegularNknRedundantDataEpochId = 0;
            context.V6RegularNknRedundantDataBatchCount = 0;
            context.V6TunaRedundantDataProbeStartedUtc = now;
            context.V6TunaRedundantDataProbeStartedBytes = Math.Max(0, committedBytes);
            context.V6UseRegularNknRedundantData = false;
            return;
        }

        var probeDelay = ResolveV6TunaRedundantDataProbeDelay();
        if (now - context.V6TunaRedundantDataProbeStartedUtc.Value < probeDelay)
        {
            return;
        }

        var committedAfterProof = Math.Max(0, committedBytes - context.V6TunaRedundantDataProbeStartedBytes);
        var minimumExpectedBytes = ResolveV6TunaRedundantDataMinimumBytesAfterProof();
        if (context.V6UseRegularNknRedundantData)
        {
            if (committedAfterProof >= minimumExpectedBytes)
            {
                context.V6UseRegularNknRedundantData = false;
                context.V6TunaRedundantDataSatisfiedEpochId = recoveredTunaEpochId;
                context.V6RegularNknRedundantDataBatchCount = 0;
                context.V6TunaRedundantDataProbeStartedUtc = now;
                context.V6TunaRedundantDataProbeStartedBytes = Math.Max(0, committedBytes);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_tuna_regular_nkn_supplement_disabled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={recoveredTunaEpochId}; reason=committed_progress_restored; committed_after_supplement_bytes={committedAfterProof}; minimum_expected_bytes={minimumExpectedBytes}");
                return;
            }

            context.V6TunaRedundantDataProbeStartedUtc = now;
            context.V6TunaRedundantDataProbeStartedBytes = Math.Max(0, committedBytes);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_tuna_regular_nkn_supplement_retained; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={recoveredTunaEpochId}; reason=committed_progress_still_below_target; committed_after_supplement_bytes={committedAfterProof}; minimum_expected_bytes={minimumExpectedBytes}");
            return;
        }

        if (committedAfterProof >= minimumExpectedBytes)
        {
            context.V6TunaRedundantDataSatisfiedEpochId = recoveredTunaEpochId;
            context.V6TunaRedundantDataProbeStartedUtc = now;
            context.V6TunaRedundantDataProbeStartedBytes = Math.Max(0, committedBytes);
            return;
        }

        context.V6UseRegularNknRedundantData = true;
        context.V6TunaRedundantDataProbeStartedUtc = now;
        context.V6TunaRedundantDataProbeStartedBytes = Math.Max(0, committedBytes);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_tuna_regular_nkn_supplement_enabled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={recoveredTunaEpochId}; reason=tuna_committed_progress_below_target; probe_delay_ms={(long)probeDelay.TotalMilliseconds}; committed_after_proof_bytes={committedAfterProof}; minimum_expected_bytes={minimumExpectedBytes}; regular_nkn_pipeline_depth={V6RegularNknRedundantSenderPipelineDepth}");
    }

    private static long ResolveRecoveredOutboundV6RegularNknEpochId(OutboundTransferContext context)
    {
        if (context.V6TransportEpoch is { } current)
        {
            return current is
            {
                TargetTransport: FileTransferTransportKind.RegularNkn,
                State: V6TransportEpochState.Recovered,
                Kind: FileTransferTransportHandoffKind.TunaToNormalFallback,
            }
                ? current.EpochId
                : 0;
        }

        return context.LastRecoveredV6TransportEpoch > 0 &&
               context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.RegularNkn &&
               context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.TunaToNormalFallback
            ? context.LastRecoveredV6TransportEpoch
            : 0;
    }

    private static bool IsRecoveredOutboundV6RegularNknEpoch(
        OutboundTransferContext context,
        long transportEpoch)
        => transportEpoch > 0 &&
           context.LastRecoveredV6TransportEpoch == transportEpoch &&
           context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.RegularNkn &&
           context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.TunaToNormalFallback;

    private static bool IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(
        OutboundTransferContext context,
        long transportEpoch)
    {
        if (transportEpoch <= 0)
        {
            return false;
        }

        if (context.V6TransportEpoch is
            {
                TargetTransport: FileTransferTransportKind.RegularNkn,
                State: V6TransportEpochState.Recovered,
                Kind: FileTransferTransportHandoffKind.RegularNknRecovery,
            } current)
        {
            return current.EpochId == transportEpoch;
        }

        return context.LastRecoveredV6TransportEpoch == transportEpoch &&
               context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.RegularNkn &&
               context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.RegularNknRecovery;
    }

    private static bool IsOutboundV6RegularNknFallbackPrimaryDelivery(OutboundTransferContext context)
    {
        if (!context.V6UseRegularNknRedundantData)
        {
            return false;
        }

        var epochId = context.V6RegularNknRedundantDataEpochId > 0
            ? context.V6RegularNknRedundantDataEpochId
            : ResolveRecoveredOutboundV6RegularNknEpochId(context);
        return IsRecoveredOutboundV6RegularNknEpoch(context, epochId);
    }

    private static bool ShouldForceOutboundV6PriorityChunkOverRegularNkn(
        OutboundTransferContext context,
        V6OutboundChunkRequestMetadata metadata)
    {
        if (!metadata.Priority)
        {
            return false;
        }

        if (context.V6TransportEpoch is { } epoch &&
            epoch.EpochId == metadata.TransportEpoch &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            return true;
        }

        if (metadata.ForceRegularNknBulk)
        {
            return true;
        }

        return IsRecoveredOutboundV6RegularNknEpoch(context, metadata.TransportEpoch);
    }

    private static bool ShouldForceOutboundV6PeerRequestedPriorityOverRegularNkn(
        OutboundTransferContext context,
        long transportEpoch,
        string? recoveryMode,
        FileTransferTransportKind receivedTransportKind)
    {
        if (ShouldForceOutboundV6TunaFrontierRescueOverRegularNkn(
            context,
            transportEpoch,
            recoveryMode,
            receivedTransportKind))
        {
            return true;
        }

        if (transportEpoch <= 0)
        {
            return receivedTransportKind == FileTransferTransportKind.RegularNkn &&
                   IsV6RegularNknFrontierControlBulkEscalationMode(recoveryMode);
        }

        if (context.V6TransportEpoch is { } current &&
            current.EpochId == transportEpoch)
        {
            return current.TargetTransport == FileTransferTransportKind.RegularNkn;
        }

        if (IsRecoveredOutboundV6RegularNknEpoch(context, transportEpoch) ||
            IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, transportEpoch))
        {
            return true;
        }

        var recoveredTunaEpochId = ResolveRecoveredOutboundV6TunaActivationEpochId(context);
        return recoveredTunaEpochId > 0 &&
               transportEpoch > recoveredTunaEpochId &&
               IsV6RegularNknRecoveryMode(recoveryMode);
    }

    private static bool ShouldForceOutboundV6TunaFrontierRescueOverRegularNkn(
        OutboundTransferContext context,
        long transportEpoch,
        string? recoveryMode,
        FileTransferTransportKind receivedTransportKind)
    {
        if (context.RouteSelection.Route != FileTransferRoute.FileTunaV6 ||
            transportEpoch <= 0 ||
            receivedTransportKind != FileTransferTransportKind.RegularNkn ||
            !IsV6RegularNknFrontierControlBulkEscalationMode(recoveryMode) ||
            !IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return false;
        }

        if (context.V6TransportEpoch is { } current &&
            current.EpochId == transportEpoch &&
            current.TargetTransport == FileTransferTransportKind.Tuna &&
            current.State == V6TransportEpochState.Recovered)
        {
            return true;
        }

        return transportEpoch == ResolveRecoveredOutboundV6TunaActivationEpochId(context);
    }

    private static bool IsV6RegularNknRecoveryMode(string? recoveryMode)
    {
        var normalized = NormalizeReason(recoveryMode);
        return normalized is "target_proof_pending"
            or "frontier_repair_only"
            or "backfill_repair"
            or "waiting_for_target_transport"
            or "regular_nkn_frontier_stall"
            or "regular_nkn_frontier_stall_control_bulk"
            or "regular_nkn_inferred_frontier_stall";
    }

    private static bool IsV6RegularNknFrontierControlBulkEscalationMode(string? recoveryMode)
        => string.Equals(
            NormalizeReason(recoveryMode),
            "regular_nkn_frontier_stall_control_bulk",
            StringComparison.Ordinal);

    private static bool IsV6RegularNknFrontierStallMode(string? recoveryMode)
    {
        var normalized = NormalizeReason(recoveryMode);
        return normalized is "regular_nkn_frontier_stall" or "regular_nkn_frontier_stall_control_bulk";
    }

    private static bool ShouldIgnoreOutboundV6FrontierRequestLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        IReadOnlyList<FileTransferRangeV4> normalizedRanges,
        out string reason)
    {
        if (normalizedRanges.All(range => range.StartChunkIndex + range.ChunkCount <= context.RemoteNextExpectedChunkIndex))
        {
            reason = "already_committed";
            return true;
        }

        if (request.TransportEpoch >= 0 &&
            request.TransportEpoch < context.LastRecoveredV6TransportEpoch &&
            context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.RegularNkn &&
            context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
            IsV6RegularNknRecoveryMode(request.RecoveryMode))
        {
            reason = "stale_recovered_regular_nkn_epoch";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool TryClaimOutboundV6RegularNknRedundantNormalBatchLocked(
        OutboundTransferContext context,
        V6OutboundChunkRequestMetadata metadata)
    {
        if (metadata.Priority ||
            !context.V6UseRegularNknRedundantData)
        {
            return false;
        }

        if (context.V6TunaRedundantDataEpochId > 0)
        {
            return true;
        }

        var epochId = context.V6RegularNknRedundantDataEpochId > 0
            ? context.V6RegularNknRedundantDataEpochId
            : ResolveRecoveredOutboundV6RegularNknEpochId(context);
        if (epochId <= 0 ||
            context.V6RegularNknRedundantDataDisabledEpochId == epochId)
        {
            context.V6UseRegularNknRedundantData = false;
            context.V6RegularNknRedundantDataEpochId = 0;
            context.V6RegularNknRedundantDataBatchCount = 0;
            return false;
        }

        var isFallbackPrimaryDelivery = IsRecoveredOutboundV6RegularNknEpoch(context, epochId);
        if (!isFallbackPrimaryDelivery &&
            context.V6RegularNknRedundantDataBatchCount >= V6RegularNknRedundantNormalBatchLimit)
        {
            DisableOutboundV6RegularNknRedundantDataLocked(
                context,
                epochId,
                "normal_batch_limit",
                clearNormalRequests: false);
            return false;
        }

        context.V6RegularNknRedundantDataEpochId = epochId;
        context.V6RegularNknRedundantDataBatchCount++;
        return true;
    }

    private static void DisableOutboundV6RegularNknRedundantDataLocked(
        OutboundTransferContext context,
        long epochId,
        string reason,
        bool clearNormalRequests)
    {
        if (epochId <= 0)
        {
            epochId = context.V6RegularNknRedundantDataEpochId > 0
                ? context.V6RegularNknRedundantDataEpochId
                : ResolveRecoveredOutboundV6RegularNknEpochId(context);
        }

        if (epochId <= 0 && !context.V6UseRegularNknRedundantData)
        {
            return;
        }

        context.V6UseRegularNknRedundantData = false;
        if (epochId > 0)
        {
            context.V6RegularNknRedundantDataDisabledEpochId = epochId;
        }

        context.V6RegularNknRedundantDataEpochId = 0;
        context.V6RegularNknRedundantDataBatchCount = 0;
        if (clearNormalRequests)
        {
            foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
            {
                context.V6NormalRequestedChunks.Remove(chunkIndex);
                if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
                {
                    context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
                }
            }
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_redundant_data_disabled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epochId}; reason={FormatProtocolLogValue(reason)}; normal_requests_cleared={(clearNormalRequests ? 1 : 0)}");
    }

    private static long ResolveRecoveredOutboundV6TunaActivationEpochId(OutboundTransferContext context)
    {
        if (context.V6TransportEpoch is { } current)
        {
            if (current is
                {
                    Kind: FileTransferTransportHandoffKind.NormalToTunaActivation,
                    TargetTransport: FileTransferTransportKind.Tuna,
                    State: V6TransportEpochState.Recovered,
                })
            {
                return current.EpochId;
            }

            return 0;
        }

        return context.LastRecoveredV6TransportEpoch > 0 &&
               context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
               context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.Tuna
            ? context.LastRecoveredV6TransportEpoch
            : 0;
    }

    private static TimeSpan ResolveV6TunaRedundantDataProbeDelay()
        => V6TunaRedundantDataProbeDelayOverrideForTests ?? TimeSpan.FromMilliseconds(V6TunaRedundantDataProbeDelayMs);

    private static long ResolveV6TunaRedundantDataMinimumBytesAfterProof()
        => V6TunaRedundantDataMinimumBytesAfterProofOverrideForTests ?? V6TunaRedundantDataMinimumBytesAfterProof;

    private void ApplyOutboundV6FrontierRequest(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransportKind)
    {
        if (request.MissingRanges.Count == 0)
        {
            LogPullDataFrameIgnored(context.TransferId, context.SessionId, request, "empty_v6_frontier_request");
            return;
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.V6LastReceiverFeedbackReceivedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.RepairRequestId) &&
                !context.V6AppliedFrontierRequestIds.Add(request.RepairRequestId.Trim()))
            {
                var duplicateRange = request.MissingRanges[0];
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; first_start_chunk_index={duplicateRange.StartChunkIndex}; first_chunk_count={duplicateRange.ChunkCount}");
                return;
            }

            var normalizedRanges = NormalizeV6RequestRanges(request.MissingRanges, context.ChunkCount);
            if (normalizedRanges.Count == 0)
            {
                LogPullDataFrameIgnored(context.TransferId, context.SessionId, request, "empty_v6_frontier_request");
                return;
            }

            var first = normalizedRanges[0];
            var isBackfillRequest = string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase);
            if (!isBackfillRequest &&
                ShouldIgnoreOutboundV6FrontierRequestLocked(context, request, normalizedRanges, out var ignoreReason))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_ignored_obsolete; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; reason={FormatProtocolLogValue(ignoreReason)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; first_start_chunk_index={first.StartChunkIndex}; first_chunk_count={first.ChunkCount}; range_count={normalizedRanges.Count}; previous_recovered_epoch={context.LastRecoveredV6TransportEpoch}; previous_recovered_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}");
                return;
            }

            if (!isBackfillRequest)
            {
                PreemptOutboundV6NormalPipelineForFrontierRequestLocked(context, request, normalizedRanges);
            }

            if (!isBackfillRequest &&
                first.StartChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                var previousRemoteFrontier = context.RemoteNextExpectedChunkIndex;
                context.RemoteNextExpectedChunkIndex = Math.Clamp(first.StartChunkIndex, 0, context.ChunkCount);
                context.RemoteGrantedUntilExclusive = Math.Max(context.RemoteGrantedUntilExclusive, context.RemoteNextExpectedChunkIndex);
                context.ChunksTransferred = Math.Max(context.ChunksTransferred, context.RemoteNextExpectedChunkIndex);
                context.BytesTransferred = Math.Max(
                    context.BytesTransferred,
                    context.RemoteNextExpectedChunkIndex >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.RemoteNextExpectedChunkIndex * context.ChunkSizeBytes));
                context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred);

                foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
                {
                    context.SentAwaitingAck.Remove(chunkIndex);
                }

                foreach (var chunkIndex in context.V6ChunkSendsInFlight.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
                {
                    context.V6ChunkSendsInFlight.Remove(chunkIndex);
                }

                PruneOutboundV6RequestedChunksBeforeLocked(context, context.RemoteNextExpectedChunkIndex);
                TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
                snapshot = CreateSnapshotLocked();
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_advanced_remote_frontier; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; inferred_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
            }

            if (!isBackfillRequest)
            {
                _ = TryRecoverOutboundV6RegularNknEpochFromPeerControlLocked(
                    context,
                    request.TransportEpoch,
                    receivedTransportKind,
                    "frontier_request_control_proof");
            }

            var forceRegularNknBulk = ShouldForceOutboundV6PeerRequestedPriorityOverRegularNkn(
                context,
                request.TransportEpoch,
                request.RecoveryMode,
                receivedTransportKind);
            QueueOutboundV6RequestedRangesLocked(
                context,
                normalizedRanges,
                new V6OutboundChunkRequestMetadata(
                    string.IsNullOrWhiteSpace(request.RepairRequestId)
                        ? $"frontier:{request.TransportEpoch}:{first.StartChunkIndex}"
                        : request.RepairRequestId,
                    Priority: true,
                    request.TransportEpoch,
                    request.RepairRequestId,
                    request.Priority ?? "frontier",
                    request.RecoveryMode,
                    forceRegularNknBulk),
                obsoleteBeforeChunkIndex: context.RemoteNextExpectedChunkIndex);
            if (IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.V6TransportEpoch!.EpochId == request.TransportEpoch &&
                !string.IsNullOrWhiteSpace(request.RepairRequestId))
            {
                context.V6TransportEpoch.LastRepairRequestId = request.RepairRequestId;
                context.V6PendingEpochRepairRequestIds.Add(request.RepairRequestId.Trim());
                var changed = TrySetV6TransportEpochStateLocked(
                    context.V6TransportEpoch,
                    context.TransferId,
                    context.SessionId,
                    string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase)
                        ? V6TransportEpochState.BackfillRepair
                        : V6TransportEpochState.FrontierRepairOnly,
                    "frontier_request",
                    context.RemoteNextExpectedChunkIndex,
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                if (changed)
                {
                    context.StatusMessage = GetV6TransportEpochStatus(context.V6TransportEpoch);
                }
            }

            context.V6SenderPumpLastWakeReason = "frontier_request";

            if (forceRegularNknBulk &&
                (context.V6TransportEpoch is null ||
                 context.V6TransportEpoch.EpochId != request.TransportEpoch))
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v6_regular_nkn_priority_force_inferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; recovery_mode={FormatProtocolLogValue(request.RecoveryMode ?? "(none)")}; previous_recovered_epoch={context.LastRecoveredV6TransportEpoch}");
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_received; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; first_start_chunk_index={first.StartChunkIndex}; first_chunk_count={first.ChunkCount}; range_count={request.MissingRanges.Count}");
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private static void PreemptOutboundV6NormalPipelineForFrontierRequestLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        IReadOnlyList<FileTransferRangeV4> normalizedRanges)
    {
        var normalRequestCount = context.V6NormalRequestedChunks.Count;
        var inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
        if (normalRequestCount == 0 && inFlightSendCount == 0)
        {
            return;
        }

        var requestedChunkAlreadyInFlight = false;
        var forceInFlightPreemption = ShouldForceOutboundV6FrontierInFlightPreemptionLocked(context, request);
        var preemptedInFlightCount = 0;
        foreach (var range in normalizedRanges)
        {
            var endExclusive = range.StartChunkIndex + range.ChunkCount;
            for (var chunkIndex = range.StartChunkIndex; chunkIndex < endExclusive; chunkIndex++)
            {
                if (context.V6ChunkSendsInFlight.ContainsKey(chunkIndex))
                {
                    requestedChunkAlreadyInFlight = true;
                    if (forceInFlightPreemption &&
                        context.V6ChunkSendsInFlight.Remove(chunkIndex))
                    {
                        preemptedInFlightCount++;
                    }

                    if (forceInFlightPreemption)
                    {
                        continue;
                    }

                    break;
                }
            }

            if (requestedChunkAlreadyInFlight && !forceInFlightPreemption)
            {
                break;
            }
        }

        foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
            }
        }

        context.V6CurrentNormalRequestKey = null;
        var pipelineGeneration = requestedChunkAlreadyInFlight && !forceInFlightPreemption
            ? context.V6SenderPipelineGeneration
            : context.ResetV6SenderPipelineCancellation();
        context.V6SenderPumpLastWakeReason = "frontier_request_preempted_normal_pipeline";

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_preempted_normal_pipeline; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; normal_request_count={normalRequestCount}; in_flight_send_count={inFlightSendCount}; requested_chunk_already_in_flight={(requestedChunkAlreadyInFlight ? 1 : 0)}; forced_in_flight_preemption={(forceInFlightPreemption ? 1 : 0)}; preempted_in_flight_count={preemptedInFlightCount}; sender_pipeline_generation={pipelineGeneration}");
    }

    private static bool ShouldForceOutboundV6FrontierInFlightPreemptionLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request)
    {
        if (string.IsNullOrWhiteSpace(request.RepairRequestId) ||
            !string.Equals(request.Priority, "frontier", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var recoveryMode = NormalizeReason(request.RecoveryMode);
        if (recoveryMode is "regular_nkn_frontier_stall" or "regular_nkn_frontier_stall_control_bulk")
        {
            return true;
        }

        if (request.TransportEpoch <= 0)
        {
            return false;
        }

        if (context.V6TransportEpoch is { } epoch &&
            epoch.EpochId == request.TransportEpoch &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            return true;
        }

        return IsRecoveredOutboundV6RegularNknEpoch(context, request.TransportEpoch) ||
               IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, request.TransportEpoch);
    }

    private static void PreemptOutboundV6NormalPipelineForReceiverStateFrontierLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        IReadOnlyList<FileTransferRangeV4> frontierRanges)
    {
        var normalRequestCount = context.V6NormalRequestedChunks.Count;
        var inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
        if (normalRequestCount == 0 && inFlightSendCount == 0)
        {
            return;
        }

        var requestedChunkAlreadyInFlight = false;
        foreach (var range in frontierRanges)
        {
            var endExclusive = range.StartChunkIndex + range.ChunkCount;
            for (var chunkIndex = range.StartChunkIndex; chunkIndex < endExclusive; chunkIndex++)
            {
                if (context.V6ChunkSendsInFlight.ContainsKey(chunkIndex))
                {
                    requestedChunkAlreadyInFlight = true;
                    break;
                }
            }

            if (requestedChunkAlreadyInFlight)
            {
                break;
            }
        }

        foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
            }
        }

        context.V6CurrentNormalRequestKey = null;
        var pipelineGeneration = requestedChunkAlreadyInFlight
            ? context.V6SenderPipelineGeneration
            : context.ResetV6SenderPipelineCancellation();
        context.V6SenderPumpLastWakeReason = "receiver_state_frontier_preempted_normal_pipeline";

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; transport_epoch={state.TransportEpoch}; normal_request_count={normalRequestCount}; in_flight_send_count={inFlightSendCount}; requested_chunk_already_in_flight={(requestedChunkAlreadyInFlight ? 1 : 0)}; sender_pipeline_generation={pipelineGeneration}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
    }

    private static void PruneOutboundV6RequestedChunksBeforeLocked(OutboundTransferContext context, int frontierChunkIndex)
    {
        foreach (var chunkIndex in context.V6PriorityRequestedChunks.Where(chunkIndex => chunkIndex < frontierChunkIndex).ToArray())
        {
            context.V6PriorityRequestedChunks.Remove(chunkIndex);
            context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
        }

        foreach (var chunkIndex in context.V6NormalRequestedChunks.Where(chunkIndex => chunkIndex < frontierChunkIndex).ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
        }
    }

    private static bool ShouldAcceptOutboundV6NormalReceiverStateRequestsLocked(
        OutboundTransferContext context,
        long requestTransportEpoch)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return true;
        }

        if (requestTransportEpoch != epoch!.EpochId)
        {
            return false;
        }

        return false;
    }

    private static void ClearOutboundV6RequestQueuesForTransportEpochLocked(
        OutboundTransferContext context,
        long transportEpoch,
        string reason)
    {
        var pipelineGeneration = context.ResetV6SenderPipelineCancellation();
        var normalRequestCount = context.V6NormalRequestedChunks.Count;
        var priorityRequestCount = context.V6PriorityRequestedChunks.Count;
        var metadataCount = context.V6RequestedChunkMetadataByChunkIndex.Count;
        var sentAwaitingAckCount = context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
        var inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
        context.V6NormalRequestedChunks.Clear();
        context.V6PriorityRequestedChunks.Clear();
        context.V6RequestedChunkMetadataByChunkIndex.Clear();
        context.V6CurrentNormalRequestKey = null;
        foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex >= context.RemoteNextExpectedChunkIndex).ToArray())
        {
            context.SentAwaitingAck.Remove(chunkIndex);
        }

        foreach (var chunkIndex in context.V6ChunkSendsInFlight.Keys.Where(chunkIndex => chunkIndex >= context.RemoteNextExpectedChunkIndex).ToArray())
        {
            context.V6ChunkSendsInFlight.Remove(chunkIndex);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_request_queues_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; reason={FormatProtocolLogValue(reason)}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}; metadata_count={metadataCount}; sent_awaiting_ack_count={sentAwaitingAckCount}; in_flight_send_count={inFlightSendCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; sender_pipeline_generation={pipelineGeneration}");
    }

    private static void ClearOutboundV6NormalRequestedChunksLocked(
        OutboundTransferContext context,
        long transportEpoch,
        string reason)
    {
        var normalRequestCount = context.V6NormalRequestedChunks.Count;
        if (normalRequestCount > 0)
        {
            foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
            {
                context.V6NormalRequestedChunks.Remove(chunkIndex);
                if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
                {
                    context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
                }
            }
        }

        context.V6CurrentNormalRequestKey = null;
        if (normalRequestCount == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_normal_requests_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; reason={FormatProtocolLogValue(reason)}; normal_request_count={normalRequestCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
    }

    private static void ReplaceOutboundV6NormalRequestedRangesLocked(
        OutboundTransferContext context,
        IReadOnlyList<FileTransferRangeV4> ranges,
        V6OutboundChunkRequestMetadata metadata,
        int obsoleteBeforeChunkIndex)
    {
        context.V6CurrentNormalRequestKey = metadata.RequestKey;
        foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
            }
        }

        QueueOutboundV6RequestedRangesLocked(context, ranges, metadata, obsoleteBeforeChunkIndex);
    }

    private static void QueueOutboundV6RequestedRangesLocked(
        OutboundTransferContext context,
        IReadOnlyList<FileTransferRangeV4> ranges,
        V6OutboundChunkRequestMetadata metadata,
        int obsoleteBeforeChunkIndex)
    {
        if (!metadata.Priority &&
            IsV6TransportEpochUnresolved(context.V6TransportEpoch))
        {
            ClearOutboundV6NormalRequestedChunksLocked(
                context,
                metadata.TransportEpoch,
                "transport_epoch_unresolved");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var normalSendAheadEndExclusive = metadata.Priority
            ? context.ChunkCount
            : Math.Min(
                context.ChunkCount,
                context.RemoteNextExpectedChunkIndex + ResolveOutboundV6NormalSendAheadLimitChunksLocked(context));
        var normalPendingAheadCount = metadata.Priority
            ? 0
            : CountOutboundV6NormalPendingAheadChunksLocked(context);
        var normalRefillLowWatermark = metadata.Priority
            ? int.MaxValue
            : ResolveOutboundV6NormalRefillLowWatermarkChunksLocked(context);
        var deferNormalRefill = !metadata.Priority &&
            normalPendingAheadCount >= normalRefillLowWatermark;
        var nearFrontierResendBypassEndExclusive = metadata.Priority
            ? context.RemoteNextExpectedChunkIndex
            : Math.Min(
                context.ChunkCount,
                context.RemoteNextExpectedChunkIndex + V6RegularNknNearFrontierNormalResendBypassChunks);
        var suppressedNormalTailChunks = 0;
        var deferredNormalRefillChunks = 0;
        var bypassedNormalRefillChunks = 0;
        var bypassedPriorityResendGateChunks = 0;
        foreach (var range in NormalizeV6RequestRanges(ranges, context.ChunkCount))
        {
            var endExclusive = range.StartChunkIndex + range.ChunkCount;
            for (var chunkIndex = range.StartChunkIndex; chunkIndex < endExclusive; chunkIndex++)
            {
                if (chunkIndex < obsoleteBeforeChunkIndex)
                {
                    continue;
                }

                if (!metadata.Priority && chunkIndex >= normalSendAheadEndExclusive)
                {
                    suppressedNormalTailChunks++;
                    continue;
                }

                var bypassNormalRefillDeferral = deferNormalRefill &&
                    ShouldBypassOutboundV6NormalRefillDeferralForNearFrontierResendLocked(
                        context,
                        chunkIndex,
                        metadata,
                        now,
                        nearFrontierResendBypassEndExclusive);
                if (deferNormalRefill && !bypassNormalRefillDeferral)
                {
                    deferredNormalRefillChunks++;
                    continue;
                }

                if (bypassNormalRefillDeferral)
                {
                    bypassedNormalRefillChunks++;
                }

                if (context.V6ChunkSendsInFlight.ContainsKey(chunkIndex))
                {
                    context.PullResendSuppressedCountRecent++;
                    continue;
                }

                var chunkSentAwaitingAck = context.SentAwaitingAck.ContainsKey(chunkIndex);
                var bypassPriorityResendGate = chunkSentAwaitingAck &&
                    ShouldBypassOutboundV6ExplicitFrontierResendGateLocked(context, metadata);
                if (chunkSentAwaitingAck &&
                    !bypassPriorityResendGate &&
                    ShouldSuppressOutboundV6RequestedChunkResendLocked(context, chunkIndex, metadata, now))
                {
                    context.PullResendSuppressedCountRecent++;
                    continue;
                }

                if (bypassPriorityResendGate)
                {
                    bypassedPriorityResendGateChunks++;
                }

                if (metadata.Priority)
                {
                    if (!ShouldPreserveOutboundV6ExistingPriorityMetadataLocked(context, chunkIndex, metadata))
                    {
                        context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = metadata;
                    }

                    context.V6NormalRequestedChunks.Remove(chunkIndex);
                    context.V6PriorityRequestedChunks.Add(chunkIndex);
                }
                else if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
                {
                    context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = metadata;
                    context.V6NormalRequestedChunks.Add(chunkIndex);
                }
            }
        }

        if (suppressedNormalTailChunks > 0 &&
            !string.Equals(context.V6SenderPumpLastWakeReason, "normal_send_ahead_limited", StringComparison.Ordinal))
        {
            context.V6SenderPumpLastWakeReason = "normal_send_ahead_limited";
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_normal_send_ahead_limited; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; send_ahead_end_exclusive={normalSendAheadEndExclusive}; suppressed_chunk_count={suppressedNormalTailChunks}; sent_awaiting_ack_count={context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}; in_flight_send_count={context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}");
        }

        if (deferredNormalRefillChunks > 0 &&
            !string.Equals(context.V6SenderPumpLastWakeReason, "normal_refill_deferred", StringComparison.Ordinal))
        {
            context.V6SenderPumpLastWakeReason = "normal_refill_deferred";
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_normal_refill_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; pending_ahead_chunk_count={normalPendingAheadCount}; refill_low_watermark_chunks={normalRefillLowWatermark}; deferred_chunk_count={deferredNormalRefillChunks}; sent_awaiting_ack_count={context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}; in_flight_send_count={context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}");
        }

        if (bypassedNormalRefillChunks > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_normal_refill_near_frontier_resend_bypassed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; bypass_end_exclusive={nearFrontierResendBypassEndExclusive}; bypassed_chunk_count={bypassedNormalRefillChunks}; pending_ahead_chunk_count={normalPendingAheadCount}; refill_low_watermark_chunks={normalRefillLowWatermark}; sent_awaiting_ack_count={context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}; in_flight_send_count={context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}");
        }

        if (bypassedPriorityResendGateChunks > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_resend_gate_bypassed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; transport_epoch={metadata.TransportEpoch}; repair_request_id={FormatProtocolLogValue(metadata.RepairRequestId ?? "(none)")}; recovery_mode={FormatProtocolLogValue(metadata.RecoveryMode ?? "(none)")}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; bypassed_chunk_count={bypassedPriorityResendGateChunks}; sent_awaiting_ack_count={context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}; in_flight_send_count={context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}");
        }
    }

    private static int ResolveOutboundV6NormalSendAheadLimitChunksLocked(OutboundTransferContext context)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return V6TunaNormalSendAheadLimitChunks;
        }

        if (IsOutboundV6RegularNknFrontierPressureActiveLocked(context))
        {
            return V6RegularNknFrontierPressureNormalSendAheadLimitChunks;
        }

        if (IsOutboundV6RegularNknDegradedProfileActiveLocked(context))
        {
            return V6RegularNknDegradedNormalSendAheadLimitChunks;
        }

        return V6RegularNknNormalSendAheadLimitChunks;
    }

    private static int ResolveOutboundV6NormalRefillLowWatermarkChunksLocked(OutboundTransferContext context)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return V6TunaNormalSendAheadLimitChunks;
        }

        if (IsOutboundV6RegularNknFrontierPressureActiveLocked(context))
        {
            return V6RegularNknFrontierPressureNormalRefillLowWatermarkChunks;
        }

        if (IsOutboundV6RegularNknDegradedProfileActiveLocked(context))
        {
            return V6RegularNknDegradedNormalRefillLowWatermarkChunks;
        }

        return V6RegularNknNormalRefillLowWatermarkChunks;
    }

    private static bool IsOutboundV6TunaNormalSendAheadPathLocked(OutboundTransferContext context)
    {
        if (context.V6TransportEpoch is
            {
                Kind: FileTransferTransportHandoffKind.NormalToTunaActivation,
                TargetTransport: FileTransferTransportKind.Tuna,
                State: V6TransportEpochState.Recovered,
            })
        {
            return true;
        }

        return context.LastRecoveredV6TransportEpoch > 0 &&
               context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
               context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.Tuna;
    }

    private static bool IsOutboundV6RegularNknPrimaryPathLocked(OutboundTransferContext context)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return false;
        }

        return context.V6TransportEpoch is null ||
               context.V6TransportEpoch.TargetTransport == FileTransferTransportKind.RegularNkn;
    }

    private static bool IsOutboundV6RegularNknFrontierPressureActiveLocked(OutboundTransferContext context)
        => context.V6RegularNknFrontierPressureUntilChunkIndex > context.RemoteNextExpectedChunkIndex;

    private static bool IsOutboundV6RegularNknDegradedProfileActiveLocked(OutboundTransferContext context)
        => context.V6RegularNknDegradedUntilChunkIndex > context.RemoteNextExpectedChunkIndex &&
           !IsOutboundV6TunaNormalSendAheadPathLocked(context);

    private static void UpdateOutboundV6RegularNknDegradedProfileLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        int previousRemoteFrontier,
        bool receiverAdvancedFrontier,
        bool stateRequestsFrontierGap)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            MaybeClearOutboundV6RegularNknDegradedProfileLocked(context, state, "tuna_path_active");
            return;
        }

        if (context.UserPaused || context.PeerPaused || state.TransferPaused)
        {
            MaybeClearOutboundV6RegularNknDegradedProfileLocked(context, state, "paused");
            return;
        }

        if (receiverAdvancedFrontier)
        {
            MaybeClearOutboundV6RegularNknDegradedProfileLocked(context, state, "receiver_state_progress");
            return;
        }

        if (state.MissingRanges.Count == 0)
        {
            MaybeClearOutboundV6RegularNknDegradedProfileLocked(context, state, "receiver_state_empty_window");
            return;
        }

        if (IsV6TransportEpochUnresolved(context.V6TransportEpoch))
        {
            MaybeClearOutboundV6RegularNknDegradedProfileLocked(context, state, "transport_epoch_unresolved");
            return;
        }

        var noProgressReceiverState =
            state.ContiguousCommittedChunkIndex <= previousRemoteFrontier &&
            context.RemoteNextExpectedChunkIndex <= previousRemoteFrontier;
        if (!noProgressReceiverState)
        {
            context.V6RegularNknDegradedNoProgressReceiverStateCount = 0;
            context.V6RegularNknDegradedObservedChunkIndex = -1;
            context.V6RegularNknDegradedObservedUtc = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.V6RegularNknDegradedObservedChunkIndex != context.RemoteNextExpectedChunkIndex ||
            context.V6RegularNknDegradedObservedUtc is null)
        {
            context.V6RegularNknDegradedObservedChunkIndex = context.RemoteNextExpectedChunkIndex;
            context.V6RegularNknDegradedObservedUtc = now;
            context.V6RegularNknDegradedNoProgressReceiverStateCount = 0;
        }

        var recoveredFallbackEpoch = ResolveRecoveredOutboundV6RegularNknEpochId(context);
        var recoveredFallbackRegularNkn = recoveredFallbackEpoch > 0 &&
            (state.TransportEpoch <= 0 || state.TransportEpoch == recoveredFallbackEpoch);
        var observedElapsedMs = context.V6RegularNknDegradedObservedUtc is { } observedUtc
            ? (long)Math.Max(0, (now - observedUtc).TotalMilliseconds)
            : 0;
        context.V6RegularNknDegradedNoProgressReceiverStateCount = recoveredFallbackRegularNkn
            ? Math.Max(
                context.V6RegularNknDegradedNoProgressReceiverStateCount,
                V6RegularNknDegradedNoProgressReceiverStateThreshold)
            : Math.Min(
                context.V6RegularNknDegradedNoProgressReceiverStateCount + 1,
                V6RegularNknDegradedNoProgressReceiverStateThreshold);

        var reason = recoveredFallbackRegularNkn
            ? "tuna_fallback_regular_nkn_recovery"
            : stateRequestsFrontierGap
                ? "receiver_state_frontier_gap_no_progress"
                : "receiver_state_no_progress";
        var sustainedRegularNknStall = observedElapsedMs >= V6RegularNknDegradedNoProgressGraceMs;
        if (!recoveredFallbackRegularNkn &&
            (!sustainedRegularNknStall ||
             context.V6RegularNknDegradedNoProgressReceiverStateCount < V6RegularNknDegradedNoProgressReceiverStateThreshold))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_regular_nkn_degraded_profile_observed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; reason={FormatProtocolLogValue(reason)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; no_progress_receiver_state_count={context.V6RegularNknDegradedNoProgressReceiverStateCount}; threshold={V6RegularNknDegradedNoProgressReceiverStateThreshold}; observed_elapsed_ms={observedElapsedMs}; grace_ms={V6RegularNknDegradedNoProgressGraceMs}");
            return;
        }

        EnterOutboundV6RegularNknDegradedProfileLocked(context, state, reason);
    }

    private static void EnterOutboundV6RegularNknDegradedProfileLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return;
        }

        var previousUntil = context.V6RegularNknDegradedUntilChunkIndex;
        var previousReason = context.V6RegularNknDegradedReason;
        var degradedStart = context.RemoteNextExpectedChunkIndex;
        var degradedUntil = Math.Min(context.ChunkCount, degradedStart + V6RegularNknDegradedReleaseAdvanceChunks);
        if (degradedUntil <= degradedStart)
        {
            degradedUntil = Math.Min(context.ChunkCount, degradedStart + V6RegularNknDegradedNormalSendAheadLimitChunks);
        }

        context.V6RegularNknDegradedStartChunkIndex = degradedStart;
        context.V6RegularNknDegradedUntilChunkIndex = Math.Max(previousUntil, degradedUntil);
        context.V6RegularNknDegradedEnteredUtc ??= DateTimeOffset.UtcNow;
        context.V6RegularNknDegradedReason = reason;

        if (previousUntil > degradedStart &&
            previousUntil >= context.V6RegularNknDegradedUntilChunkIndex &&
            string.Equals(previousReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_degraded_profile_entered; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; reason={FormatProtocolLogValue(reason)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; no_progress_receiver_state_count={context.V6RegularNknDegradedNoProgressReceiverStateCount}; degraded_until_chunk_index={context.V6RegularNknDegradedUntilChunkIndex}; send_ahead_limit_chunks={V6RegularNknDegradedNormalSendAheadLimitChunks}; refill_low_watermark_chunks={V6RegularNknDegradedNormalRefillLowWatermarkChunks}");
    }

    private static void MaybeClearOutboundV6RegularNknDegradedProfileLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason)
    {
        if (!IsOutboundV6RegularNknDegradedProfileActiveLocked(context))
        {
            ClearOutboundV6RegularNknDegradedProfileLocked(context);
            return;
        }

        var degradedStart = context.V6RegularNknDegradedStartChunkIndex;
        var degradedUntil = context.V6RegularNknDegradedUntilChunkIndex;
        var degradedReason = context.V6RegularNknDegradedReason;
        var enteredUtc = context.V6RegularNknDegradedEnteredUtc;
        var activeMs = enteredUtc is { } entered
            ? (long)Math.Max(0, (DateTimeOffset.UtcNow - entered).TotalMilliseconds)
            : 0;

        ClearOutboundV6RegularNknDegradedProfileLocked(context);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_degraded_profile_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; reason={FormatProtocolLogValue(reason)}; degraded_reason={FormatProtocolLogValue(degradedReason ?? "(none)")}; degraded_start_chunk_index={degradedStart}; degraded_until_chunk_index={degradedUntil}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; active_ms={activeMs}");
    }

    private static void ClearOutboundV6RegularNknDegradedProfileLocked(OutboundTransferContext context)
    {
        context.V6RegularNknDegradedNoProgressReceiverStateCount = 0;
        context.V6RegularNknDegradedObservedChunkIndex = -1;
        context.V6RegularNknDegradedObservedUtc = null;
        context.V6RegularNknDegradedStartChunkIndex = -1;
        context.V6RegularNknDegradedUntilChunkIndex = -1;
        context.V6RegularNknDegradedEnteredUtc = null;
        context.V6RegularNknDegradedReason = null;
    }

    private static void EnterOutboundV6RegularNknFrontierPressureLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return;
        }

        var previousUntil = context.V6RegularNknFrontierPressureUntilChunkIndex;
        var pressureStart = context.RemoteNextExpectedChunkIndex;
        var pressureUntil = Math.Min(
            context.ChunkCount,
            Math.Max(
                pressureStart + V6RegularNknFrontierPressureReleaseAdvanceChunks,
                state.DurableReceivedHighestChunkIndex + 1));
        if (pressureUntil <= pressureStart)
        {
            pressureUntil = Math.Min(context.ChunkCount, pressureStart + V6RegularNknFrontierPressureReleaseAdvanceChunks);
        }

        context.V6RegularNknFrontierPressureStartChunkIndex = pressureStart;
        context.V6RegularNknFrontierPressureUntilChunkIndex = Math.Max(previousUntil, pressureUntil);
        context.V6RegularNknFrontierPressureEnteredUtc ??= DateTimeOffset.UtcNow;

        if (previousUntil > pressureStart)
        {
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_frontier_pressure_entered; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; pressure_until_chunk_index={context.V6RegularNknFrontierPressureUntilChunkIndex}; send_ahead_limit_chunks={V6RegularNknFrontierPressureNormalSendAheadLimitChunks}; refill_low_watermark_chunks={V6RegularNknFrontierPressureNormalRefillLowWatermarkChunks}");
    }

    private static void MaybeClearOutboundV6RegularNknFrontierPressureLocked(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        string reason,
        bool forceClear = false)
    {
        if (!IsOutboundV6RegularNknFrontierPressureActiveLocked(context))
        {
            context.V6RegularNknFrontierPressureStartChunkIndex = -1;
            context.V6RegularNknFrontierPressureUntilChunkIndex = -1;
            context.V6RegularNknFrontierPressureEnteredUtc = null;
            return;
        }

        if (!forceClear &&
            context.RemoteNextExpectedChunkIndex < context.V6RegularNknFrontierPressureUntilChunkIndex &&
            state.MissingRanges.Count > 0)
        {
            return;
        }

        var pressureStart = context.V6RegularNknFrontierPressureStartChunkIndex;
        var pressureUntil = context.V6RegularNknFrontierPressureUntilChunkIndex;
        var enteredUtc = context.V6RegularNknFrontierPressureEnteredUtc;
        var activeMs = enteredUtc is { } entered
            ? (long)Math.Max(0, (DateTimeOffset.UtcNow - entered).TotalMilliseconds)
            : 0;

        context.V6RegularNknFrontierPressureStartChunkIndex = -1;
        context.V6RegularNknFrontierPressureUntilChunkIndex = -1;
        context.V6RegularNknFrontierPressureEnteredUtc = null;

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_frontier_pressure_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; reason={FormatProtocolLogValue(reason)}; pressure_start_chunk_index={pressureStart}; pressure_until_chunk_index={pressureUntil}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; active_ms={activeMs}");
    }

    private static int CountOutboundV6NormalPendingAheadChunksLocked(OutboundTransferContext context)
    {
        var frontier = context.RemoteNextExpectedChunkIndex;
        return context.SentAwaitingAck.Keys
            .Where(chunkIndex => chunkIndex >= frontier)
            .Concat(context.V6ChunkSendsInFlight.Keys.Where(chunkIndex => chunkIndex >= frontier))
            .Concat(context.V6NormalRequestedChunks.Where(chunkIndex => chunkIndex >= frontier))
            .Distinct()
            .Count();
    }

    private static bool ShouldPreserveOutboundV6ExistingPriorityMetadataLocked(
        OutboundTransferContext context,
        int chunkIndex,
        V6OutboundChunkRequestMetadata incomingMetadata)
    {
        if (!incomingMetadata.Priority ||
            !context.V6PriorityRequestedChunks.Contains(chunkIndex) ||
            !context.V6RequestedChunkMetadataByChunkIndex.TryGetValue(chunkIndex, out var existingMetadata) ||
            !existingMetadata.Priority)
        {
            return false;
        }

        if (!string.Equals(existingMetadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(incomingMetadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase) ||
            existingMetadata.TransportEpoch != incomingMetadata.TransportEpoch)
        {
            return false;
        }

        if (!IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
            incomingMetadata.TransportEpoch > 0 &&
            !existingMetadata.ForceRegularNknBulk &&
            incomingMetadata.ForceRegularNknBulk)
        {
            return true;
        }

        if (!IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
            incomingMetadata.TransportEpoch > 0 &&
            existingMetadata.ForceRegularNknBulk &&
            !incomingMetadata.ForceRegularNknBulk)
        {
            return false;
        }

        return existingMetadata.ForceRegularNknBulk || !incomingMetadata.ForceRegularNknBulk;
    }

    private static bool ShouldSuppressOutboundV6RequestedChunkResendLocked(
        OutboundTransferContext context,
        int chunkIndex,
        V6OutboundChunkRequestMetadata metadata,
        DateTimeOffset now)
    {
        if (!context.LastChunkSentUtc.TryGetValue(chunkIndex, out var lastSentUtc))
        {
            return false;
        }

        if (!metadata.Priority)
        {
            if (metadata.RequiresExplicitFrontierRequest)
            {
                return true;
            }

            return now - lastSentUtc < ResolveOutboundV6NormalReceiverStateResendGateLocked(context);
        }

        var minimumIntervalMs = V6NormalReceiverStateResendGateMs;
        minimumIntervalMs = string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase)
            ? metadata.TransportEpoch <= 0
                ? V6RecoveredFrontierResendGateMs
                : V6EpochFrontierResendGateMs
            : RepairResendIntervalMs;

        return now - lastSentUtc < TimeSpan.FromMilliseconds(minimumIntervalMs);
    }

    private static bool ShouldBypassOutboundV6ExplicitFrontierResendGateLocked(
        OutboundTransferContext context,
        V6OutboundChunkRequestMetadata metadata)
    {
        if (!metadata.Priority ||
            string.IsNullOrWhiteSpace(metadata.RepairRequestId) ||
            !string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var recoveryMode = NormalizeReason(metadata.RecoveryMode);
        if (metadata.TransportEpoch <= 0)
        {
            return string.IsNullOrWhiteSpace(recoveryMode) ||
                   recoveryMode is "regular_nkn_frontier_stall" or "regular_nkn_frontier_stall_control_bulk";
        }

        if (context.V6TransportEpoch is { } epoch &&
            epoch.EpochId == metadata.TransportEpoch &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            return true;
        }

        if (IsRecoveredOutboundV6RegularNknEpoch(context, metadata.TransportEpoch) ||
            IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, metadata.TransportEpoch))
        {
            return true;
        }

        return recoveryMode is "regular_nkn_frontier_stall" or "regular_nkn_frontier_stall_control_bulk";
    }

    private static bool ShouldBypassOutboundV6NormalRefillDeferralForNearFrontierResendLocked(
        OutboundTransferContext context,
        int chunkIndex,
        V6OutboundChunkRequestMetadata metadata,
        DateTimeOffset now,
        int nearFrontierResendBypassEndExclusive)
    {
        if (metadata.Priority ||
            metadata.RequiresExplicitFrontierRequest ||
            !metadata.AllowNormalRefillBypass ||
            chunkIndex < context.RemoteNextExpectedChunkIndex ||
            chunkIndex >= nearFrontierResendBypassEndExclusive ||
            !context.SentAwaitingAck.ContainsKey(chunkIndex) ||
            !context.LastChunkSentUtc.TryGetValue(chunkIndex, out var lastSentUtc))
        {
            return false;
        }

        return now - lastSentUtc >= ResolveOutboundV6NormalReceiverStateResendGateLocked(context);
    }

    private static TimeSpan ResolveOutboundV6NormalReceiverStateResendGateLocked(OutboundTransferContext context)
    {
        if (V6RegularNknNormalReceiverStateResendGateOverrideForTests is { } overrideValue)
        {
            return overrideValue;
        }

        return TimeSpan.FromMilliseconds(IsOutboundV6TunaNormalSendAheadPathLocked(context)
            ? V6NormalReceiverStateResendGateMs
            : V6RegularNknNormalReceiverStateResendGateMs);
    }

    private static bool ShouldBypassOutboundV6PipelineDepthForEpochProofLocked(
        OutboundTransferContext context,
        int inFlightSendCount,
        int pipelineDepth)
    {
        if (inFlightSendCount >= pipelineDepth + V6EpochPriorityPipelineBypassDepth)
        {
            return false;
        }

        var epoch = context.V6TransportEpoch;
        if ((!IsV6TransportEpochUnresolved(epoch) ||
             epoch!.TargetTransport != FileTransferTransportKind.RegularNkn) &&
            !context.V6UseRegularNknRedundantData)
        {
            return false;
        }

        foreach (var chunkIndex in context.V6PriorityRequestedChunks)
        {
            if (chunkIndex < context.RemoteNextExpectedChunkIndex ||
                chunkIndex >= context.ChunkCount ||
                !context.V6RequestedChunkMetadataByChunkIndex.TryGetValue(chunkIndex, out var metadata) ||
                !metadata.Priority ||
                !string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsV6TransportEpochUnresolved(epoch))
            {
                if (metadata.TransportEpoch != epoch!.EpochId)
                {
                    continue;
                }

                return true;
            }

            if (context.V6UseRegularNknRedundantData &&
                IsRecoveredOutboundV6RegularNknEpoch(context, metadata.TransportEpoch))
            {
                return true;
            }

        }

        return false;
    }

    private async Task RunOutboundV6SenderPumpAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession)
    {
        var inFlightSends = new List<Task>();
        while (true)
        {
            context.LifetimeCts.Token.ThrowIfCancellationRequested();
            for (var index = inFlightSends.Count - 1; index >= 0; index--)
            {
                if (inFlightSends[index].IsCompleted)
                {
                    await inFlightSends[index].ConfigureAwait(false);
                    inFlightSends.RemoveAt(index);
                }
            }

            var pipelineDepth = Math.Max(1, context.PullCurrentPipelineDepth > 0
                ? context.PullCurrentPipelineDepth
                : ResolveOutboundPipelineDepth(context));
            if (!IsV4MixedScreenShareActive())
            {
                pipelineDepth = Math.Max(pipelineDepth, V6FileOnlySenderPipelineDepth);
            }

            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal)
                {
                    if (context.V6UseRegularNknRedundantData)
                    {
                        pipelineDepth = IsOutboundV6RegularNknFallbackPrimaryDelivery(context)
                            ? Math.Max(pipelineDepth, V6RegularNknFallbackSenderPipelineDepth)
                            : Math.Min(pipelineDepth, V6RegularNknRedundantSenderPipelineDepth);
                    }
                    else if (IsOutboundV6RegularNknPrimaryPathLocked(context))
                    {
                        pipelineDepth = Math.Min(pipelineDepth, V6RegularNknSenderPipelineDepth);
                    }
                }
            }

            var allowPriorityBypass = false;
            if (inFlightSends.Count >= pipelineDepth)
            {
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                    {
                        allowPriorityBypass = ShouldBypassOutboundV6PipelineDepthForEpochProofLocked(
                            context,
                            inFlightSends.Count,
                            pipelineDepth);
                    }
                }
            }

            if (inFlightSends.Count >= pipelineDepth && !allowPriorityBypass)
            {
                await Task.WhenAny(inFlightSends).ConfigureAwait(false);
                continue;
            }

            List<int>? chunkIndicesToSend = null;
            V6OutboundChunkRequestMetadata metadata = default;
            Task? waitForSignal = null;
            FileTransferReceiveRecoveryRequest? feedbackStaleRecoveryRequest = null;
            OutboundTransferContext? feedbackStaleOutboundToProbe = null;
            bool logRequestWait = false;
            string requestWaitReason = string.Empty;
            int priorityRequestCount = 0;
            int normalRequestCount = 0;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return;
                }

                if (context.UserPaused || context.PeerPaused || context.PullTransportPaused)
                {
                    if (!context.UserPaused &&
                        !context.PeerPaused &&
                        context.PullTransportPaused &&
                        ShouldAllowOutboundV6FrontierProofWhileTransportPausedLocked(context) &&
                        TryDequeueOutboundV6ChunkRunLocked(context, stream.CanSeek, out chunkIndicesToSend, out metadata))
                    {
                        context.PullSenderFeedCreditWaitStartedUtc = null;
                        context.V4SenderCreditExhaustedSinceUtc = null;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v6_transport_paused_frontier_proof_allowed; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={metadata.TransportEpoch}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; chunk_count={chunkIndicesToSend.Count}; reason={FormatProtocolLogValue(context.PullTransportPauseReason ?? "transport_paused")}");
                    }
                    else if (context.PullTransportPaused)
                    {
                        context.PullSenderSendWaitCountRecent++;
                        logRequestWait = context.PullSenderFeedCreditWaitStartedUtc is null ||
                            !string.Equals(context.V6SenderPumpLastWakeReason, "transport_paused", StringComparison.Ordinal);
                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                        priorityRequestCount = context.V6PriorityRequestedChunks.Count;
                        normalRequestCount = context.V6NormalRequestedChunks.Count;
                        requestWaitReason = string.IsNullOrWhiteSpace(context.PullTransportPauseReason)
                            ? "transport_paused"
                            : $"transport_paused:{context.PullTransportPauseReason}";
                        context.V6SenderPumpLastWakeReason = "transport_paused";
                    }

                    waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                }
                else if (TryStartOutboundV6ReceiveRecoveryForFeedbackStallLocked(
                             context,
                             out feedbackStaleRecoveryRequest,
                             out feedbackStaleOutboundToProbe))
                {
                    context.PullSenderSendWaitCountRecent++;
                    logRequestWait = true;
                    context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                    context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                    priorityRequestCount = context.V6PriorityRequestedChunks.Count;
                    normalRequestCount = context.V6NormalRequestedChunks.Count;
                    requestWaitReason = "receiver_feedback_stalled";
                    waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                }
                else if (TryDequeueOutboundV6ChunkRunLocked(context, stream.CanSeek, out chunkIndicesToSend, out metadata))
                {
                    context.PullSenderFeedCreditWaitStartedUtc = null;
                    context.V4SenderCreditExhaustedSinceUtc = null;
                }
                else
                {
                    context.PullSenderSendWaitCountRecent++;
                    logRequestWait = context.PullSenderFeedCreditWaitStartedUtc is null;
                    context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                    context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                    priorityRequestCount = context.V6PriorityRequestedChunks.Count;
                    normalRequestCount = context.V6NormalRequestedChunks.Count;
                    requestWaitReason = priorityRequestCount == 0 && normalRequestCount == 0
                        ? "no_active_requests"
                        : context.V6SenderPumpLastWakeReason;
                    waitForSignal = context.ResetAndGetV4SenderPumpSignalTask();
                }

            }

            if (feedbackStaleRecoveryRequest is not null)
            {
                TryRequestFileTransferReceiveRecovery(feedbackStaleRecoveryRequest);
            }

            if (feedbackStaleOutboundToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(feedbackStaleOutboundToProbe).ConfigureAwait(false);
                SignalOutboundV4SenderPump(feedbackStaleOutboundToProbe);
            }

            if (chunkIndicesToSend is { Count: > 0 })
            {
                var prepared = await PrepareChunkBatchV6Async(context, stream, chunkIndicesToSend, metadata).ConfigureAwait(false);
                if (prepared is not null)
                {
                    inFlightSends.Add(SendPreparedChunkBatchV6Async(context, dataSession, prepared));
                }

                continue;
            }

            if (waitForSignal is not null)
            {
                if (logRequestWait)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_sender_waiting_for_requests; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(requestWaitReason)}; priority_request_count={priorityRequestCount}; normal_request_count={normalRequestCount}");
                }

                await MaybeRequestOutboundV6ReceiveRecoveryForRequestStarvationAsync(context, requestWaitReason).ConfigureAwait(false);
                await Task.WhenAny(waitForSignal, Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
            }
        }
    }

    private bool TryStartOutboundV6ReceiveRecoveryForFeedbackStallLocked(
        OutboundTransferContext context,
        out FileTransferReceiveRecoveryRequest? recoveryRequest,
        out OutboundTransferContext? outboundToProbe)
    {
        recoveryRequest = null;
        outboundToProbe = null;

        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
            context.UserPaused ||
            context.PeerPaused ||
            context.PullTransportPaused)
        {
            return false;
        }

        var epoch = context.V6TransportEpoch;
        if (IsV6TransportEpochUnresolved(epoch) &&
            epoch is not { TargetTransport: FileTransferTransportKind.RegularNkn })
        {
            return false;
        }

        var lastFeedbackUtc = context.V6LastReceiverFeedbackReceivedUtc;
        if (lastFeedbackUtc is null ||
            context.V6LastReceiverStateEpoch < 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var feedbackSilence = now - lastFeedbackUtc.Value;
        if (feedbackSilence < CurrentV6SenderRequestFeedbackStallRecoveryDelay)
        {
            return false;
        }

        if (context.V6LastReceiveRecoveryRequestedUtc is not null &&
            now - context.V6LastReceiveRecoveryRequestedUtc.Value < CurrentV6SenderRequestFeedbackStallRecoveryCooldown)
        {
            return false;
        }

        var normalRequestCount = context.V6NormalRequestedChunks.Count;
        var priorityRequestCount = context.V6PriorityRequestedChunks.Count;
        var inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
        var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
        if (ShouldSuppressOutboundV6ReceiveRecoveryForOutstandingBacklogLocked(
                context,
                now,
                transportBacklogChunks,
                inFlightSendCount,
                normalRequestCount,
                priorityRequestCount,
                out var suppressionReason,
                out var recentChunkSendCount))
        {
            LogOutboundV6FeedbackStallRecoverySuppressedLocked(
                context,
                now,
                suppressionReason,
                feedbackSilence,
                transportBacklogChunks,
                inFlightSendCount,
                recentChunkSendCount,
                normalRequestCount,
                priorityRequestCount);
            return false;
        }

        if (TryPrepareOutboundV6PrimaryRegularNknReceiveRecoveryWithoutEpochLocked(
                context,
                now,
                feedbackSilence,
                transportBacklogChunks,
                inFlightSendCount,
                recentChunkSendCount,
                normalRequestCount,
                priorityRequestCount,
                out recoveryRequest))
        {
            return true;
        }

        if (TryPrepareOutboundV6PrimaryRegularNknStaleNormalPipelineRecoveryWithoutEpochLocked(
                context,
                now,
                feedbackSilence,
                transportBacklogChunks,
                inFlightSendCount,
                recentChunkSendCount,
                normalRequestCount,
                priorityRequestCount,
                out recoveryRequest))
        {
            return true;
        }

        if (IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
        {
            var primaryRegularRecentChunkSendCount = context.LastChunkSentUtc.Count(pair =>
                now - pair.Value <= CurrentV6SenderRequestFeedbackStallRecoveryDelay);
            LogOutboundV6FeedbackStallRecoverySuppressedLocked(
                context,
                now,
                "primary_regular_nkn_protocol_repair_only",
                feedbackSilence,
                transportBacklogChunks,
                inFlightSendCount,
                primaryRegularRecentChunkSendCount,
                normalRequestCount,
                priorityRequestCount);
            context.V6SenderPumpLastWakeReason = "regular_nkn_feedback_repair";
            return false;
        }

        if (normalRequestCount == 0 &&
            transportBacklogChunks < V6SenderFeedbackStaleNormalBacklogChunks &&
            inFlightSendCount == 0)
        {
            return false;
        }

        var clearedNormalRequestCount = 0;
        foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
            }

            clearedNormalRequestCount++;
        }

        context.V6CurrentNormalRequestKey = null;
        context.ResetV6SenderPipelineCancellation();
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc ??= now;
        context.PullTransportPauseReason = "sender_request_feedback_stalled";
        context.PullTransportResumeRequestPending = true;
        context.PullTransportRebindGeneration++;
        context.PullTransportRebindStartedUtc = now;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.V6LastReceiveRecoveryRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.V6SenderPumpLastWakeReason = "sender_request_feedback_stalled";

        if (!IsV6TransportEpochUnresolved(epoch))
        {
            StartOutboundV6TransportEpochLocked(
                context,
                "sender_request_feedback_stalled",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);
        }

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
        var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
        context.StatusMessage = context.V6TransportEpoch is null
            ? "Waiting for V6 receiver requests."
            : GetV6TransportEpochStatus(context.V6TransportEpoch);

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_sender_feedback_stale_normal_pipeline_paused; transfer_id={context.TransferId}; session_id={context.SessionId}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}; in_flight_send_count={inFlightSendCount}; transport_backlog_chunks={transportBacklogChunks}; cleared_normal_request_count={clearedNormalRequestCount}");

        recoveryRequest = new FileTransferReceiveRecoveryRequest(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            "sender_request_feedback_stalled");
        outboundToProbe = context;
        return true;
    }

    private async Task MaybeRequestOutboundV6ReceiveRecoveryForRequestStarvationAsync(OutboundTransferContext context, string waitReason)
    {
        FileTransferReceiveRecoveryRequest? recoveryRequest = null;
        OutboundTransferContext? outboundToProbe = null;
        long transportEpoch = 0;
        string epochState = "none";
        int remoteFrontier = 0;
        int highestAcceptedChunk = -1;
        DateTimeOffset? waitStartedUtc = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                !string.Equals(waitReason, "no_active_requests", StringComparison.Ordinal))
            {
                return;
            }

            if (context.V6PriorityRequestedChunks.Count != 0 ||
                context.V6NormalRequestedChunks.Count != 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            waitStartedUtc = context.PullSenderFeedCreditWaitStartedUtc;
            if (waitStartedUtc is null ||
                now - waitStartedUtc.Value < CurrentV6SenderRequestFeedbackStallRecoveryDelay)
            {
                return;
            }

            if (context.V6LastReceiveRecoveryRequestedUtc is not null &&
                now - context.V6LastReceiveRecoveryRequestedUtc.Value < CurrentV6SenderRequestFeedbackStallRecoveryCooldown)
            {
                return;
            }

            var hasTransferActivity =
                context.V6LastReceiverStateEpoch >= 0 &&
                (context.ChunksAcceptedForTransport > 0 ||
                 context.BytesAcceptedForTransport > 0 ||
                 context.RemoteNextExpectedChunkIndex > 0 ||
                 context.SentAwaitingAck.Count > 0 ||
                 context.V6ChunkSendsInFlight.Count > 0);
            if (!hasTransferActivity)
            {
                return;
            }

            var epoch = context.V6TransportEpoch;
            if (IsV6TransportEpochUnresolved(epoch) &&
                epoch is not { TargetTransport: FileTransferTransportKind.RegularNkn })
            {
                return;
            }

            var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
            var inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
            var feedbackSilence = context.V6LastReceiverFeedbackReceivedUtc is { } lastFeedbackUtc
                ? now - lastFeedbackUtc
                : now - waitStartedUtc.Value;
            if (ShouldSuppressOutboundV6ReceiveRecoveryForOutstandingBacklogLocked(
                    context,
                    now,
                    transportBacklogChunks,
                    inFlightSendCount,
                    normalRequestCount: 0,
                    priorityRequestCount: 0,
                    out var suppressionReason,
                    out var recentChunkSendCount))
            {
                LogOutboundV6FeedbackStallRecoverySuppressedLocked(
                    context,
                    now,
                    suppressionReason,
                    feedbackSilence,
                    transportBacklogChunks,
                    inFlightSendCount,
                    recentChunkSendCount,
                    normalRequestCount: 0,
                    priorityRequestCount: 0);
                return;
            }

            if (TryPrepareOutboundV6PrimaryRegularNknReceiveRecoveryWithoutEpochLocked(
                    context,
                    now,
                    feedbackSilence,
                    transportBacklogChunks,
                    inFlightSendCount,
                    recentChunkSendCount,
                    normalRequestCount: 0,
                    priorityRequestCount: 0,
                    out recoveryRequest))
            {
                transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
                epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
                remoteFrontier = context.RemoteNextExpectedChunkIndex;
                highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            }
            else if (IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                var primaryRegularRecentChunkSendCount = context.LastChunkSentUtc.Count(pair =>
                    now - pair.Value <= CurrentV6SenderRequestFeedbackStallRecoveryDelay);
                LogOutboundV6FeedbackStallRecoverySuppressedLocked(
                    context,
                    now,
                    "primary_regular_nkn_protocol_repair_only",
                    feedbackSilence,
                    transportBacklogChunks,
                    inFlightSendCount,
                    primaryRegularRecentChunkSendCount,
                    normalRequestCount: 0,
                    priorityRequestCount: 0);
                context.V6SenderPumpLastWakeReason = "regular_nkn_feedback_repair";
                return;
            }
            else
            {
                context.V6LastReceiveRecoveryRequestedUtc = now;
                context.V6EpochLivenessDeferralCount++;
                context.V6EpochLivenessDeferralUtc = now;
                context.PullTransportResumeRequestPending = true;
                context.PullTransportRebindGeneration++;
                context.PullTransportRebindStartedUtc = now;
                context.PullTransportSafetyReplayRearmCount = 0;
                context.PullTransportFrontierOnlyRepairActive = false;
                context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
                context.V6SenderPumpLastWakeReason = "sender_request_feedback_stalled";

                if (!IsV6TransportEpochUnresolved(epoch))
                {
                    StartOutboundV6TransportEpochLocked(
                        context,
                        "sender_request_feedback_stalled",
                        FileTransferTransportHandoffKind.RegularNknRecovery,
                        FileTransferTransportKind.RegularNkn);
                }

                transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
                epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
                remoteFrontier = context.RemoteNextExpectedChunkIndex;
                highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
                outboundToProbe = context;
                recoveryRequest = new FileTransferReceiveRecoveryRequest(
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Outbound,
                    "sender_request_feedback_stalled");
            }

        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_sender_request_feedback_stalled_recovery_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; wait_started_utc={FormatProtocolLogValue(waitStartedUtc?.ToString("O"))}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}");

        if (recoveryRequest is not null)
        {
            TryRequestFileTransferReceiveRecovery(recoveryRequest);
        }

        if (outboundToProbe is not null)
        {
            await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToProbe).ConfigureAwait(false);
            SignalOutboundV4SenderPump(outboundToProbe);
        }
    }

    private bool ShouldAllowOutboundV6FrontierProofWhileTransportPausedLocked(OutboundTransferContext context)
    {
        if (!context.PullTransportPaused ||
            context.V6PriorityRequestedChunks.Count == 0)
        {
            return false;
        }

        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.TargetTransport != FileTransferTransportKind.RegularNkn ||
            epoch.State is not (V6TransportEpochState.FrontierRepairOnly
                or V6TransportEpochState.BackfillRepair
                or V6TransportEpochState.WaitingForTargetTransport))
        {
            return false;
        }

        foreach (var chunkIndex in context.V6PriorityRequestedChunks)
        {
            if (chunkIndex < context.RemoteNextExpectedChunkIndex ||
                chunkIndex >= context.ChunkCount ||
                !context.V6RequestedChunkMetadataByChunkIndex.TryGetValue(chunkIndex, out var metadata) ||
                !metadata.Priority ||
                !string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase) ||
                metadata.TransportEpoch != epoch.EpochId)
            {
                continue;
            }

            return !IsOutboundV6ChunkBlockedByTransportEpochLocked(context, chunkIndex, metadata);
        }

        return false;
    }

    private bool TryDequeueOutboundV6ChunkRunLocked(
        OutboundTransferContext context,
        bool sourceCanSeek,
        out List<int> chunkIndices,
        out V6OutboundChunkRequestMetadata metadata)
    {
        chunkIndices = [];
        metadata = default;
        var source = context.V6PriorityRequestedChunks.Count > 0
            ? context.V6PriorityRequestedChunks
            : context.V6NormalRequestedChunks;
        var priority = ReferenceEquals(source, context.V6PriorityRequestedChunks);
        while (source.Count > 0 && chunkIndices.Count == 0)
        {
            var first = source.Min;
            if (first < context.RemoteNextExpectedChunkIndex || first >= context.ChunkCount)
            {
                source.Remove(first);
                context.V6RequestedChunkMetadataByChunkIndex.Remove(first);
                continue;
            }

            if (!sourceCanSeek && first > context.V6NextSequentialSourceChunkIndex)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_request_waiting_for_sequential_source; transfer_id={context.TransferId}; session_id={context.SessionId}; requested_chunk_index={first}; next_sequential_source_chunk_index={context.V6NextSequentialSourceChunkIndex}; priority={(priority ? 1 : 0)}");
                return false;
            }

            if (!sourceCanSeek && first < context.V6NextSequentialSourceChunkIndex &&
                !context.PullSentChunkCache.ContainsKey(first))
            {
                source.Remove(first);
                context.V6RequestedChunkMetadataByChunkIndex.Remove(first);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_request_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=non_seekable_chunk_not_cached; requested_chunk_index={first}; next_sequential_source_chunk_index={context.V6NextSequentialSourceChunkIndex}");
                continue;
            }

            metadata = context.V6RequestedChunkMetadataByChunkIndex.TryGetValue(first, out var stored)
                ? stored
                : new V6OutboundChunkRequestMetadata($"implicit:{first}", priority, 0, null, priority ? "frontier" : null, null);
            if (TryDropOutboundV6StaleEpochRequestLocked(context, source, first, metadata))
            {
                continue;
            }

            if (IsOutboundV6ChunkBlockedByTransportEpochLocked(context, first, metadata))
            {
                if (context.V6SenderPumpLastWakeReason != "transport_epoch_blocked")
                {
                    LogOutboundV6EpochRequestBlocked(context, first, metadata);
                }

                context.V6SenderPumpLastWakeReason = "transport_epoch_blocked";
                return false;
            }

            var expected = first;
            var maxSegments = Math.Min(
                ResolveV6MaxBatchSegments(repairSend: priority),
                Math.Max(1, FileTransferProtocol.MaxChunkBatchRawBytesV6 / Math.Max(1, context.ChunkSizeBytes)));
            while (source.Remove(expected))
            {
                var expectedMetadata = context.V6RequestedChunkMetadataByChunkIndex.TryGetValue(expected, out var storedExpected)
                    ? storedExpected
                    : metadata;
                if (TryDropOutboundV6StaleEpochRequestLocked(context, source, expected, expectedMetadata))
                {
                    if (chunkIndices.Count == 0)
                    {
                        break;
                    }

                    continue;
                }

                if (!expectedMetadata.Equals(metadata) ||
                    IsOutboundV6ChunkBlockedByTransportEpochLocked(context, expected, expectedMetadata))
                {
                    if (context.V6SenderPumpLastWakeReason != "transport_epoch_blocked")
                    {
                        LogOutboundV6EpochRequestBlocked(context, expected, expectedMetadata);
                    }

                    context.V6SenderPumpLastWakeReason = "transport_epoch_blocked";
                    source.Add(expected);
                    break;
                }

                context.V6RequestedChunkMetadataByChunkIndex.Remove(expected);
                if (expected < context.RemoteNextExpectedChunkIndex || expected >= context.ChunkCount)
                {
                    expected++;
                    continue;
                }

                if (!sourceCanSeek && expected != context.V6NextSequentialSourceChunkIndex + chunkIndices.Count &&
                    !context.PullSentChunkCache.ContainsKey(expected))
                {
                    source.Add(expected);
                    break;
                }

                chunkIndices.Add(expected);
                if (chunkIndices.Count >= maxSegments)
                {
                    break;
                }

                expected++;
                if (!source.Contains(expected))
                {
                    break;
                }
            }
        }

        return chunkIndices.Count > 0;
    }

    private static bool TryDropOutboundV6StaleEpochRequestLocked(
        OutboundTransferContext context,
        SortedSet<int> source,
        int chunkIndex,
        V6OutboundChunkRequestMetadata metadata)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            metadata.TransportEpoch == epoch!.EpochId)
        {
            return false;
        }

        source.Remove(chunkIndex);
        if (!context.V6PriorityRequestedChunks.Contains(chunkIndex) &&
            !context.V6NormalRequestedChunks.Contains(chunkIndex))
        {
            context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
        }

        if (context.V6SenderPumpLastWakeReason != "transport_epoch_stale_request_dropped")
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_stale_request_dropped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; requested_chunk_index={chunkIndex}; request_id={FormatProtocolLogValue(metadata.RequestKey)}; request_transport_epoch={metadata.TransportEpoch}; current_transport_epoch={epoch.EpochId}; current_epoch_state={FormatV6TransportEpochState(epoch.State)}");
        }

        context.V6SenderPumpLastWakeReason = "transport_epoch_stale_request_dropped";
        return true;
    }

    private int ResolveV6MaxBatchSegments(bool repairSend)
    {
        if (repairSend || IsV4MixedScreenShareActive())
        {
            return ResolveV4MaxBatchSegments(repairSend);
        }

        return FileTransferProtocol.MaxChunkBatchSegmentsV6;
    }

    private static TimeSpan ResolveV6SenderTransportSendTimeout(PreparedV6ChunkBatch prepared)
    {
        if (V6SenderTransportSendTimeoutOverrideForTests is { } timeout)
        {
            return timeout;
        }

        if (prepared.UseRegularNknRedundantDelivery)
        {
            return TimeSpan.FromMilliseconds(V6RegularNknRedundantTransportSendTimeoutMs);
        }

        if (IsPreparedV6RegularNknPrimaryDelivery(prepared))
        {
            return TimeSpan.FromMilliseconds(V6RegularNknTransportSendTimeoutMs);
        }

        return TimeSpan.FromMilliseconds(V6SenderTransportSendTimeoutMs);
    }

    private static bool IsPreparedV6RegularNknPrimaryDelivery(PreparedV6ChunkBatch prepared)
        => prepared.UseRegularNknPrimaryDelivery;

    private enum PreparedV6ChunkBatchSendOutcome
    {
        Sent,
        NotSent,
        NotSentWakeSenderPump,
    }

    private static void LogOutboundV6EpochRequestBlocked(
        OutboundTransferContext context,
        int chunkIndex,
        V6OutboundChunkRequestMetadata metadata)
    {
        var epoch = context.V6TransportEpoch;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_request_blocked; transfer_id={context.TransferId}; session_id={context.SessionId}; requested_chunk_index={chunkIndex}; request_id={FormatProtocolLogValue(metadata.RequestKey)}; priority={(metadata.Priority ? 1 : 0)}; request_transport_epoch={metadata.TransportEpoch}; current_transport_epoch={epoch?.EpochId ?? 0}; current_epoch_state={FormatProtocolLogValue(epoch is null ? "none" : FormatV6TransportEpochState(epoch.State))}; target_transport={FormatProtocolLogValue(epoch is null ? "none" : FormatFileTransferTransportKind(epoch.TargetTransport))}");
    }

    private sealed record PreparedV6ChunkBatch(
        FileTransferChunkBatchFrameV6 Batch,
        int RawBytes,
        int StartChunkIndex,
        int SegmentCount,
        bool UseRegularNknRedundantDelivery,
        bool UseRegularNknFallbackPrimaryDelivery,
        bool UseRegularNknPrimaryDelivery,
        long SenderPipelineGeneration,
        V6OutboundChunkRequestMetadata Metadata);

    private bool ShouldDropPreparedV6ChunkBatchBeforeSend(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared)
    {
        string? reason = null;
        long currentPipelineGeneration = 0;
        long currentTransportEpoch = 0;
        string currentEpochState = "none";
        int remoteFrontier = 0;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            currentPipelineGeneration = context.V6SenderPipelineGeneration;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            var epoch = context.V6TransportEpoch;
            if (epoch is not null)
            {
                currentTransportEpoch = epoch.EpochId;
                currentEpochState = FormatV6TransportEpochState(epoch.State);
            }

            if (prepared.SenderPipelineGeneration != currentPipelineGeneration)
            {
                reason = "stale_sender_pipeline_generation";
            }
            else if (context.UserPaused || context.PeerPaused)
            {
                reason = "paused";
            }
            else if (!prepared.Metadata.Priority &&
                     !string.Equals(context.V6CurrentNormalRequestKey, prepared.Metadata.RequestKey, StringComparison.Ordinal))
            {
                reason = "stale_request_key";
            }
            else if (prepared.StartChunkIndex + prepared.SegmentCount <= remoteFrontier)
            {
                reason = "obsolete_remote_frontier";
            }
            else if (prepared.StartChunkIndex < remoteFrontier)
            {
                reason = "partially_obsolete_remote_frontier";
            }
            else if (IsV6TransportEpochUnresolved(epoch) &&
                     prepared.Metadata.TransportEpoch != epoch!.EpochId)
            {
                reason = "stale_transport_epoch";
            }
            else if (IsOutboundV6ChunkBlockedByTransportEpochLocked(context, prepared.StartChunkIndex, prepared.Metadata))
            {
                reason = "transport_epoch_blocked";
            }
        }

        if (reason is null)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_stale_prepared_batch_dropped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; request_transport_epoch={prepared.Metadata.TransportEpoch}; current_transport_epoch={currentTransportEpoch}; current_epoch_state={FormatProtocolLogValue(currentEpochState)}; sender_pipeline_generation={prepared.SenderPipelineGeneration}; current_sender_pipeline_generation={currentPipelineGeneration}; remote_frontier_chunk_index={remoteFrontier}");
        return true;
    }

    private bool IsPreparedV6ChunkBatchStale(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            if (prepared.SenderPipelineGeneration != context.V6SenderPipelineGeneration)
            {
                return true;
            }

            var epoch = context.V6TransportEpoch;
            return IsV6TransportEpochUnresolved(epoch) &&
                   prepared.Metadata.TransportEpoch != epoch!.EpochId;
        }
    }

    private async Task<PreparedV6ChunkBatch?> PrepareChunkBatchV6Async(
        OutboundTransferContext context,
        Stream stream,
        IReadOnlyList<int> chunkIndices,
        V6OutboundChunkRequestMetadata metadata)
    {
        if (chunkIndices.Count == 0)
        {
            return null;
        }

        List<int> activeChunkIndices;
        bool useRegularNknRedundantDelivery;
        bool useRegularNknFallbackPrimaryDelivery;
        bool useRegularNknPrimaryDelivery;
        bool forceRegularNknBulkDelivery;
        long senderPipelineGeneration;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.UserPaused ||
                context.PeerPaused)
            {
                return null;
            }

            if (!metadata.Priority &&
                !string.Equals(context.V6CurrentNormalRequestKey, metadata.RequestKey, StringComparison.Ordinal))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_stale_request_batch_dropped; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; current_request_key={FormatProtocolLogValue(context.V6CurrentNormalRequestKey ?? "(none)")}; requested_chunk_count={chunkIndices.Count}");
                return null;
            }

            var currentFrontier = context.RemoteNextExpectedChunkIndex;
            activeChunkIndices = chunkIndices
                .Where(chunkIndex => chunkIndex >= currentFrontier && chunkIndex < context.ChunkCount)
                .ToList();
            for (var i = 0; i < activeChunkIndices.Count; i++)
            {
                if (!IsOutboundV6ChunkBlockedByTransportEpochLocked(context, activeChunkIndices[i], metadata))
                {
                    continue;
                }

                LogOutboundV6EpochRequestBlocked(context, activeChunkIndices[i], metadata);
                activeChunkIndices = activeChunkIndices.Take(i).ToList();
                break;
            }

            if (activeChunkIndices.Count == 0)
            {
                return null;
            }

            var useRegularNknNormalRedundantDelivery =
                TryClaimOutboundV6RegularNknRedundantNormalBatchLocked(context, metadata);
            var forceRegularNknPriorityDelivery =
                ShouldForceOutboundV6PriorityChunkOverRegularNkn(context, metadata);
            useRegularNknFallbackPrimaryDelivery =
                useRegularNknNormalRedundantDelivery &&
                IsRecoveredOutboundV6RegularNknEpoch(context, metadata.TransportEpoch);
            useRegularNknPrimaryDelivery =
                metadata.TransportEpoch <= 0 ||
                useRegularNknFallbackPrimaryDelivery ||
                IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, metadata.TransportEpoch);
            useRegularNknRedundantDelivery = useRegularNknNormalRedundantDelivery ||
                                             forceRegularNknPriorityDelivery;
            forceRegularNknBulkDelivery = useRegularNknNormalRedundantDelivery ||
                                          forceRegularNknPriorityDelivery;
            senderPipelineGeneration = context.V6SenderPipelineGeneration;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        var dataSegments = new List<byte[]>(activeChunkIndices.Count);
        var rawBytes = 0;
        try
        {
            foreach (var chunkIndex in activeChunkIndices)
            {
                var repairRead = chunkIndex < context.V6NextSequentialSourceChunkIndex;
                var chunkBytes = await LoadChunkBytesForSendAsync(context, stream, chunkIndex, buffer, repairRead).ConfigureAwait(false);
                var candidateRawBytes = rawBytes + chunkBytes.Length;
                if (candidateRawBytes > FileTransferProtocol.MaxChunkBatchRawBytesV6 ||
                    !CanSerializeChunkBatchV4(context.SessionId, context.TransferId, activeChunkIndices[0], dataSegments, chunkBytes))
                {
                    if (dataSegments.Count == 0)
                    {
                        throw new InvalidOperationException("V6 chunk batch could not fit inside the transport payload budget.");
                    }

                    break;
                }

                dataSegments.Add(chunkBytes);
                rawBytes = candidateRawBytes;
                if (!stream.CanSeek)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context) &&
                            !context.IsTerminal &&
                            chunkIndex == context.V6NextSequentialSourceChunkIndex)
                        {
                            context.V6NextSequentialSourceChunkIndex++;
                        }
                    }
                }
            }

            var startChunkIndex = activeChunkIndices[0];
            var batchProfile = metadata.Priority
                ? "v6_frontier_repair"
                : useRegularNknFallbackPrimaryDelivery
                    ? "v6_request_window_regular_nkn_fallback"
                : useRegularNknRedundantDelivery
                    ? "v6_request_window_regular_nkn_redundant"
                    : "v6_request_window";
            var batch = new FileTransferChunkBatchFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                StartChunkIndex = startChunkIndex,
                ChunkCount = dataSegments.Count,
                DataSegments = dataSegments,
                BatchProfile = batchProfile,
                RepairDeliveryMode = forceRegularNknBulkDelivery && metadata.Priority
                    ? FileTransferV4RepairDeliveryMode.ControlBulkRedundant
                    : FileTransferV4RepairDeliveryMode.BulkOnly,
                ForceRegularNknBulk = forceRegularNknBulkDelivery,
                TransportEpoch = metadata.TransportEpoch,
                BatchId = $"v6:{metadata.RequestKey}:{startChunkIndex}:{dataSegments.Count}",
                RepairRequestId = metadata.RepairRequestId,
                Priority = metadata.PriorityName ?? (metadata.Priority ? "frontier" : null),
                RecoveryMode = metadata.RecoveryMode,
            };

            _ = FileTransferDataFrameCodec.Serialize(batch);
            var inFlightUtc = DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal &&
                    senderPipelineGeneration == context.V6SenderPipelineGeneration)
                {
                    foreach (var chunkIndex in activeChunkIndices)
                    {
                        context.V6ChunkSendsInFlight[chunkIndex] = inFlightUtc;
                    }
                }
            }

            return new PreparedV6ChunkBatch(
                batch,
                rawBytes,
                startChunkIndex,
                dataSegments.Count,
                useRegularNknRedundantDelivery,
                useRegularNknFallbackPrimaryDelivery,
                useRegularNknPrimaryDelivery,
                senderPipelineGeneration,
                metadata);
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendPreparedChunkBatchV6Async(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        PreparedV6ChunkBatch prepared)
    {
        var wakeSenderPumpAfterClear = false;
        try
        {
            if (ShouldDropPreparedV6ChunkBatchBeforeSend(context, prepared))
            {
                return;
            }

            var sendOutcome = await SendPreparedChunkBatchV6WithTimeoutAsync(context, dataSession, prepared).ConfigureAwait(false);
            if (sendOutcome != PreparedV6ChunkBatchSendOutcome.Sent)
            {
                wakeSenderPumpAfterClear = sendOutcome == PreparedV6ChunkBatchSendOutcome.NotSentWakeSenderPump;
                return;
            }

            LogPullBinaryFrameSent(context.TransferId, context.SessionId, prepared.Batch, prepared.RawBytes);

            var sentUtc = DateTimeOffset.UtcNow;
            SessionFileTransferSnapshot? snapshot = null;
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    for (var offset = 0; offset < prepared.SegmentCount; offset++)
                    {
                        var chunkIndex = prepared.StartChunkIndex + offset;
                        context.SentAwaitingAck[chunkIndex] = sentUtc;
                        context.LastChunkSentUtc[chunkIndex] = sentUtc;
                        context.V6ChunkSendsInFlight.Remove(chunkIndex);
                        context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                    }

                    var endExclusive = prepared.StartChunkIndex + prepared.SegmentCount;
                    context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, endExclusive);
                    context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                    context.PullUsefulPayloadBytesRecent += prepared.RawBytes;
                    context.PullSenderRawBytesRecent += prepared.RawBytes;
                    context.PullSenderBatchFramesRecent++;
                    context.PullSenderChunkCountRecent += prepared.SegmentCount;
                    context.PullSenderPipelineCompletedFramesRecent++;
                    if (prepared.Metadata.Priority)
                    {
                        context.PullSenderRepairSendCountRecent += prepared.SegmentCount;
                    }

                    TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                    snapshot = CreateSnapshotLocked();
                }
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_chunk_batch_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; raw_bytes={prepared.RawBytes}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");

            if (snapshot is not null)
            {
                RaiseTransferChanged(snapshot);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (IsPreparedV6ChunkBatchStale(context, prepared))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_stale_prepared_batch_canceled; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; request_transport_epoch={prepared.Metadata.TransportEpoch}; sender_pipeline_generation={prepared.SenderPipelineGeneration}");
        }
        catch (Exception ex)
        {
            if (TryHandleRecoverableOutboundV6ChunkBatchSendFailure(context, prepared, ex))
            {
                return;
            }

            var errorCode = ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ClearPreparedV6ChunkBatchInFlightMarkers(context, prepared);
            if (wakeSenderPumpAfterClear)
            {
                SignalOutboundV4SenderPump(context);
            }
        }
    }

    private bool TryHandleRecoverableOutboundV6ChunkBatchSendFailure(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared,
        Exception ex)
    {
        if (!IsRecoverableV6DataSessionSendFailure(ex))
        {
            return false;
        }

        var shouldDefer = false;
        var pullTransportPaused = false;
        var resumeRequestPending = false;
        var postTunaRecoveryActive = false;
        var unresolvedEpoch = false;
        long currentEpoch = 0;
        var epochState = "none";
        var handoffKind = FileTransferTransportHandoffKind.None;
        var targetTransport = FileTransferTransportKind.Unknown;
        var requeuedChunkCount = 0;
        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            pullTransportPaused = context.PullTransportPaused;
            resumeRequestPending = context.PullTransportResumeRequestPending;
            postTunaRecoveryActive = context.PullPostTunaRecoveryActive;
            unresolvedEpoch = IsV6TransportEpochUnresolved(context.V6TransportEpoch);
            if (!pullTransportPaused && !resumeRequestPending && !postTunaRecoveryActive && !unresolvedEpoch)
            {
                return false;
            }

            var epoch = context.V6TransportEpoch;
            currentEpoch = epoch?.EpochId ?? 0;
            epochState = epoch is null ? "none" : FormatV6TransportEpochState(epoch.State);
            handoffKind = epoch?.Kind ?? FileTransferTransportHandoffKind.None;
            targetTransport = epoch?.TargetTransport ?? FileTransferTransportKind.Unknown;
            shouldDefer = true;

            context.PullSenderPipelineFailedFramesRecent++;
            context.V6SenderPumpLastWakeReason = pullTransportPaused
                ? "transport_paused"
                : "transport_send_deferred_for_recovery";

            var canRequeueNormal = !prepared.Metadata.Priority &&
                !unresolvedEpoch &&
                string.Equals(context.V6CurrentNormalRequestKey, prepared.Metadata.RequestKey, StringComparison.Ordinal);
            var canRequeuePriority = prepared.Metadata.Priority;
            if (canRequeuePriority || canRequeueNormal)
            {
                for (var offset = 0; offset < prepared.SegmentCount; offset++)
                {
                    var chunkIndex = prepared.StartChunkIndex + offset;
                    if (chunkIndex < context.RemoteNextExpectedChunkIndex || chunkIndex >= context.ChunkCount)
                    {
                        continue;
                    }

                    context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = prepared.Metadata;
                    if (prepared.Metadata.Priority)
                    {
                        context.V6NormalRequestedChunks.Remove(chunkIndex);
                        context.V6PriorityRequestedChunks.Add(chunkIndex);
                    }
                    else if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
                    {
                        context.V6NormalRequestedChunks.Add(chunkIndex);
                    }

                    requeuedChunkCount++;
                }
            }

            snapshot = CreateSnapshotLocked();
        }

        if (!shouldDefer)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_send_deferred_for_recovery; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; raw_bytes={prepared.RawBytes}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; current_transport_epoch={currentEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; pull_transport_paused={(pullTransportPaused ? 1 : 0)}; resume_request_pending={(resumeRequestPending ? 1 : 0)}; post_tuna_recovery_active={(postTunaRecoveryActive ? 1 : 0)}; unresolved_epoch={(unresolvedEpoch ? 1 : 0)}; requeued_chunk_count={requeuedChunkCount}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        SignalOutboundV4SenderPump(context);
        return true;
    }

    private static bool IsRecoverableV6DataSessionSendFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException)
            {
                return true;
            }

            if (current is InvalidOperationException invalidOperation)
            {
                var message = invalidOperation.Message;
                if (message.Contains("Bridge disconnected", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("NKN bridge is not running", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("NKN bridge process is not available", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Not connected", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("client not ready", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("data session is not available", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ClearPreparedV6ChunkBatchInFlightMarkers(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context))
            {
                return;
            }

            for (var offset = 0; offset < prepared.SegmentCount; offset++)
            {
                context.V6ChunkSendsInFlight.Remove(prepared.StartChunkIndex + offset);
            }
        }
    }

    private int RequeueTimedOutPreparedV6ChunkBatchLocked(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared,
        out string reason)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            reason = "terminal";
            return 0;
        }

        if (!IsPreparedV6RegularNknPrimaryDelivery(prepared))
        {
            reason = "not_regular_nkn_primary";
            return 0;
        }

        if (prepared.UseRegularNknRedundantDelivery)
        {
            reason = "redundant_delivery";
            return 0;
        }

        if (context.UserPaused || context.PeerPaused || context.PullTransportPaused)
        {
            reason = "paused";
            return 0;
        }

        if (prepared.SenderPipelineGeneration != context.V6SenderPipelineGeneration)
        {
            reason = "stale_sender_pipeline_generation";
            return 0;
        }

        if (IsV6TransportEpochUnresolved(context.V6TransportEpoch))
        {
            reason = "unresolved_transport_epoch";
            return 0;
        }

        if (!IsOutboundV6RegularNknPrimaryPathLocked(context) &&
            !IsRecoveredOutboundV6RegularNknEpoch(context, prepared.Metadata.TransportEpoch) &&
            !IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(context, prepared.Metadata.TransportEpoch))
        {
            reason = "not_current_regular_nkn_path";
            return 0;
        }

        if (!prepared.Metadata.Priority &&
            !string.Equals(context.V6CurrentNormalRequestKey, prepared.Metadata.RequestKey, StringComparison.Ordinal))
        {
            reason = "stale_request_key";
            return 0;
        }

        var requeuedChunkCount = 0;
        for (var offset = 0; offset < prepared.SegmentCount; offset++)
        {
            var chunkIndex = prepared.StartChunkIndex + offset;
            if (chunkIndex < context.RemoteNextExpectedChunkIndex || chunkIndex >= context.ChunkCount)
            {
                continue;
            }

            if (IsOutboundV6ChunkBlockedByTransportEpochLocked(context, chunkIndex, prepared.Metadata))
            {
                continue;
            }

            context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = prepared.Metadata;
            if (prepared.Metadata.Priority)
            {
                context.V6NormalRequestedChunks.Remove(chunkIndex);
                context.V6PriorityRequestedChunks.Add(chunkIndex);
            }
            else if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6NormalRequestedChunks.Add(chunkIndex);
            }

            requeuedChunkCount++;
        }

        context.V6SenderPumpLastWakeReason = requeuedChunkCount > 0
            ? "regular_nkn_send_timeout_requeued"
            : "regular_nkn_send_timeout_no_requeue";
        reason = requeuedChunkCount > 0 ? "requeued" : "no_uncommitted_chunks";
        return requeuedChunkCount;
    }

    private async Task<PreparedV6ChunkBatchSendOutcome> SendPreparedChunkBatchV6WithTimeoutAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        PreparedV6ChunkBatch prepared)
    {
        var timeout = ResolveV6SenderTransportSendTimeout(prepared);
        CancellationToken senderPipelineToken;
        lock (gate)
        {
            senderPipelineToken = ReferenceEquals(outboundTransfer, context)
                ? context.V6SenderPipelineCts.Token
                : new CancellationToken(canceled: true);
        }

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token, senderPipelineToken);
        Task sendTask = dataSession.SendAsync(prepared.Batch, sendCts.Token);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            await sendTask.ConfigureAwait(false);
            return PreparedV6ChunkBatchSendOutcome.Sent;
        }

        var timeoutTask = Task.Delay(timeout, sendCts.Token);
        var completed = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);
        if (completed == sendTask)
        {
            await sendTask.ConfigureAwait(false);
            return PreparedV6ChunkBatchSendOutcome.Sent;
        }

        if (senderPipelineToken.IsCancellationRequested &&
            IsPreparedV6ChunkBatchStale(context, prepared))
        {
            sendCts.Cancel();
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; raw_bytes={prepared.RawBytes}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; sender_pipeline_generation={prepared.SenderPipelineGeneration}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
            _ = ObserveTimedOutV6ChunkBatchSendAsync(context.TransferId, context.SessionId, prepared, sendTask);
            return PreparedV6ChunkBatchSendOutcome.NotSent;
        }

        if (context.LifetimeCts.IsCancellationRequested)
        {
            throw new OperationCanceledException(context.LifetimeCts.Token);
        }

        sendCts.Cancel();
        var requeuedChunkCount = 0;
        var requeueReason = "not_attempted";
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.PullSenderPipelineFailedFramesRecent++;
                if (prepared.UseRegularNknRedundantDelivery &&
                    !prepared.UseRegularNknFallbackPrimaryDelivery &&
                    !prepared.Metadata.Priority)
                {
                    DisableOutboundV6RegularNknRedundantDataLocked(
                        context,
                        prepared.Metadata.TransportEpoch,
                        "send_timeout",
                        clearNormalRequests: true);
                    context.V6SenderPumpLastWakeReason = "regular_nkn_redundant_timeout";
                }

                requeuedChunkCount = RequeueTimedOutPreparedV6ChunkBatchLocked(
                    context,
                    prepared,
                    out requeueReason);
            }
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_send_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; raw_bytes={prepared.RawBytes}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; timeout_ms={(long)timeout.TotalMilliseconds}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_send_timeout_requeue; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; requeued_chunk_count={requeuedChunkCount}; reason={FormatProtocolLogValue(requeueReason)}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; regular_nkn_primary={(prepared.UseRegularNknPrimaryDelivery ? 1 : 0)}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
        _ = ObserveTimedOutV6ChunkBatchSendAsync(context.TransferId, context.SessionId, prepared, sendTask);
        return requeuedChunkCount > 0
            ? PreparedV6ChunkBatchSendOutcome.NotSentWakeSenderPump
            : PreparedV6ChunkBatchSendOutcome.NotSent;
    }

    private static async Task ObserveTimedOutV6ChunkBatchSendAsync(
        string transferId,
        string sessionId,
        PreparedV6ChunkBatch prepared,
        Task sendTask)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_chunk_batch_send_late_completed; transfer_id={transferId}; session_id={sessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_chunk_batch_send_late_canceled; transfer_id={transferId}; session_id={sessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_chunk_batch_send_late_failed; transfer_id={transferId}; session_id={sessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; error={FormatProtocolLogValue(ex.GetType().Name)}");
        }
    }

    private async Task RunInboundV6ReceiverAsync(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_receiver_started; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; route={context.RouteSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; session_open_chunk_size_bytes={sessionOpen.ChunkSizeBytes}; session_open_pipeline_depth={sessionOpen.InitialPipelineDepth}; request_driven=1");
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Inbound, "receiver", context.RouteSelection);

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

            Task<FileTransferReceivedDataFrame>? pendingReceiveTask = null;
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                pendingReceiveTask ??= dataSession.ReceiveWithMetadataAsync(context.LifetimeCts.Token).AsTask();
                var completed = await Task.WhenAny(
                    pendingReceiveTask,
                    Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token)).ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    await MaybeSendInboundV6RetryStateAsync(context).ConfigureAwait(false);
                    continue;
                }

                var received = await pendingReceiveTask.ConfigureAwait(false);
                var frame = received.Frame;
                pendingReceiveTask = null;
                if (!IsFrameForContext(context, frame))
                {
                    LogInboundV4FrameIgnored(context, frame, "session_or_transfer_mismatch_v6");
                    continue;
                }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    LogInboundV4FrameIgnored(context, frame, "protocol_not_v6");
                    continue;
                }

                TouchInboundV6PeerLivenessIfAuthoritative(context, received, "v6_data_frame");
                switch (frame)
                {
                    case FileTransferManifestFrameV6 manifest:
                        if (!await InitializeInboundV6ManifestAsync(context, manifest).ConfigureAwait(false))
                        {
                            return;
                        }

                        if (context.UserPaused)
                        {
                            await SendInboundV4PauseControlAsync(context, "user_paused_manifest_received").ConfigureAwait(false);
                        }

                        await SendInboundV6ReceiverStateAsync(context, "manifest_received", forceSend: true).ConfigureAwait(false);
                        await SendInboundV6FrontierRequestAsync(context, "manifest_received", forceSend: true).ConfigureAwait(false);
                        if (manifest.ChunkCount == 0)
                        {
                            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                            return;
                        }

                        break;
                    case FileTransferChunkBatchFrameV6 batch:
                        await HandleInboundV6ChunkBatchAsync(context, batch, received.TransportKind).ConfigureAwait(false);
                        break;
                    case FileTransferStateFrameV4 state:
                        if (ApplyInboundV4PeerState(context, state))
                        {
                            await FlushInboundV6PausedProgressAsync(context, "peer_resumed").ConfigureAwait(false);
                        }

                        break;
                    case FileTransferTransportEpochFrameV6:
                        LogInboundV4FrameIgnored(context, frame, "transport_epoch_control_required");
                        break;
                    case FileTransferTransportProbeFrameV6 probe:
                        await HandleReceivedV6TransportProbeFrameAsync(
                            context.SessionId,
                            context.TransferId,
                            FileTransferDirection.Inbound,
                            probe,
                            received.TransportKind).ConfigureAwait(false);
                        break;
                    case FileTransferFrontierRequestFrameV6:
                    case FileTransferRepairProofFrameV6:
                        LogInboundV4FrameIgnored(context, frame, "transport_epoch_control_required");
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogInboundV4FrameIgnored(context, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferCancelFrameV4 cancel:
                    {
                        var reason = NormalizeReason(cancel.Reason);
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_priority_received; kind=cancel; transfer_id={context.TransferId}; session_id={context.SessionId}; direction=inbound; reason={FormatProtocolLogValue(reason ?? CanceledReason)}; path=redundant_data_frame");
                        await TransitionInboundToTerminalAsync(
                                context,
                                FileTransferTransferState.Canceled,
                                errorCode: FileTransferResultCodes.CanceledRemote,
                                statusMessage: reason ?? "Transfer canceled by peer.",
                                sendError: false,
                                errorMessage: null,
                                cancelReason: null,
                                ct: CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                    case FileTransferErrorFrameV4 error:
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=error; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=phase2_control_required; error_code={NormalizeErrorCode(error.ErrorCode) ?? InvalidStateErrorCode}");
                        break;
                    default:
                        LogInboundV4FrameIgnored(context, frame, "unexpected_inbound_frame_v6");
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
                "V6 receive loop failed.").ConfigureAwait(false);
        }
    }

    private async Task<bool> InitializeInboundV6ManifestAsync(InboundTransferContext context, FileTransferManifestFrameV6 manifest)
    {
        string? failureCode = null;
        string? failureMessage = null;
        FileTransferReceiveDestination? destination = null;
        V6ReceiveDestinationMode destinationMode = V6ReceiveDestinationMode.Unknown;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (context.PullManifestReceived)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Duplicate V6 manifest received.";
            }
            else if (manifest.ChunkSizeBytes <= 0 ||
                     manifest.ChunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes ||
                     manifest.FileSizeBytes < 0 ||
                     manifest.ChunkCount < 0 ||
                     string.IsNullOrWhiteSpace(manifest.Sha256Base64))
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V6 manifest was invalid.";
            }
            else if (!string.Equals(context.FileName, manifest.FileName, StringComparison.Ordinal) ||
                     context.FileSizeBytes != manifest.FileSizeBytes ||
                     (!string.IsNullOrWhiteSpace(context.Sha256Base64) &&
                      !string.Equals(context.Sha256Base64, manifest.Sha256Base64, StringComparison.Ordinal)))
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V6 manifest metadata did not match the original offer.";
            }
            else if (!TryCalculateExpectedChunkCount(manifest.FileSizeBytes, manifest.ChunkSizeBytes, out var expectedChunkCount) ||
                     manifest.ChunkCount != expectedChunkCount)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V6 manifest chunk metadata did not match the declared file size.";
            }
            else if (manifest.ChunkCount > FileTransferProtocol.MaxChunkCountV6)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "V6 manifest chunk count exceeded the supported limit.";
            }
        }

        if (failureCode is null && (context.WriteStream is null || context.Hash is null))
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
                destination = await context.OpenWriteDestinationAsync!(context.CreateOffer(), linkedCts.Token).ConfigureAwait(false);
                ValidateWritableStream(destination.Stream);
                destinationMode = destination.Stream.CanRead && destination.Stream.CanSeek
                    ? V6ReceiveDestinationMode.SparseSeekable
                    : V6ReceiveDestinationMode.ContiguousOnly;
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                destination?.Dispose();
                return false;
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
                context.V6DestinationMode = destinationMode;
                destination = null;
            }

            if (failureCode is null && (context.WriteStream is null || context.Hash is null))
            {
                failureCode = StreamOpenFailedErrorCode;
                failureMessage = "Could not open the V6 receive destination stream.";
            }

            if (failureCode is null)
            {
                streamCanRead = context.WriteStream!.CanRead;
                streamCanSeek = context.WriteStream.CanSeek;
                streamCanWrite = context.WriteStream.CanWrite;
                if (context.V6DestinationMode == V6ReceiveDestinationMode.Unknown)
                {
                    context.V6DestinationMode = streamCanRead && streamCanSeek
                        ? V6ReceiveDestinationMode.SparseSeekable
                        : V6ReceiveDestinationMode.ContiguousOnly;
                }

                context.Sha256Base64 = manifest.Sha256Base64;
                context.MetadataAwaitingSinceUtc = null;
                context.ChunkCount = manifest.ChunkCount;
                context.ChunkSizeBytes = manifest.ChunkSizeBytes;
                context.NextChunkIndex = 0;
                context.BufferedBytes = 0;
                context.HighestBufferedChunkIndex = -1;
                context.PullHighestReceivedChunkIndex = -1;
                context.V6SparseAcceptWindowEndExclusive = 0;
                context.PendingChunks.Clear();
                context.ReceiverSparseWriteActive = context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable;
                context.ReceiverSparseChunksWritten = context.ReceiverSparseWriteActive
                    ? new BitArray(manifest.ChunkCount)
                    : null;
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
                context.V6ReceiverStateEpoch = 0;
                context.V6FrontierRequestSequence = 0;
                context.V6LastReceiverStateSentUtc = null;
                context.V6LastFrontierRequestSentUtc = null;
                context.V6LastFrontierRequestChunkIndex = -1;
                context.V6LastFrontierRequestId = null;
                ResetInboundV6FrontierStallGraceLocked(context);
                context.PullManifestReceived = true;
                context.State = FileTransferTransferState.Receiving;
                context.StatusMessage = context.UserPaused
                    ? "Transfer paused."
                    : context.PeerPaused
                        ? "Peer paused transfer."
                        : "Receiving V6 file data.";
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                context.PullLastCommittedProgressUtc = null;
                snapshot = CreateSnapshotLocked();
            }
        }

        destination?.Dispose();

        if (failureCode is not null)
        {
            await FailInboundV4Async(
                context,
                failureCode,
                failureMessage ?? "V6 manifest was invalid.",
                failureMessage ?? "V6 manifest was invalid.").ConfigureAwait(false);
            return false;
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_manifest_received; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size_bytes={manifest.FileSizeBytes}; chunk_size_bytes={manifest.ChunkSizeBytes}; chunk_count={manifest.ChunkCount}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_destination_mode_selected; transfer_id={context.TransferId}; session_id={context.SessionId}; mode={FormatV6DestinationMode(context.V6DestinationMode)}; can_read={(streamCanRead ? 1 : 0)}; can_write={(streamCanWrite ? 1 : 0)}; can_seek={(streamCanSeek ? 1 : 0)}");
        return true;
    }

    private async Task HandleInboundV6ChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind receivedTransportKind)
    {
        if (!TryValidateInboundV4ChunkBatch(context, batch, out var chunks, out var failureMessage))
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; error_code={InvalidStateErrorCode}; reason=invalid_chunk_batch; message={FormatProtocolLogValue(failureMessage)}");
            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                failureMessage ?? "V6 chunk batch was invalid.",
                failureMessage ?? "V6 chunk batch was invalid.").ConfigureAwait(false);
            return;
        }

        V6ReceiveDestinationMode mode;
        Stream? writeStream;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                context.WriteStream is null)
            {
                return;
            }

            if (context.UserPaused || context.PeerPaused)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_chunk_batch_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={(context.UserPaused ? "user_paused" : "peer_paused")}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
                return;
            }

            mode = context.V6DestinationMode;
            writeStream = context.WriteStream;
        }

        if (mode == V6ReceiveDestinationMode.SparseSeekable)
        {
            await HandleInboundV6SparseChunkBatchAsync(context, batch, chunks, writeStream!, receivedTransportKind).ConfigureAwait(false);
            return;
        }

        await HandleInboundV6ContiguousChunkBatchAsync(context, batch, chunks, writeStream!, receivedTransportKind).ConfigureAwait(false);
    }

    private async Task HandleInboundV6SparseChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks,
        Stream writeStream,
        FileTransferTransportKind receivedTransportKind)
    {
        var acceptedChunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(chunks.Count);
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.ReceiverSparseChunksWritten is null)
            {
                return;
            }

            foreach (var (chunkIndex, chunkBytes) in chunks)
            {
                if (!IsInboundV6ChunkRequestedLocked(context, chunkIndex))
                {
                    var reason = chunkIndex < context.NextChunkIndex
                        ? "behind_committed_frontier"
                        : chunkIndex >= GetInboundV6AcceptWindowEndExclusiveLocked(context)
                            ? "ahead_of_accept_window"
                            : "outside_request_window";
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_unsolicited_chunk_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; mode=sparse_seekable; reason={reason}; chunk_index={chunkIndex}; committed_frontier_chunk_index={context.NextChunkIndex}; request_window_end_chunk_index={GetInboundV6RequestWindowEndExclusiveLocked(context)}; accept_window_end_chunk_index={GetInboundV6AcceptWindowEndExclusiveLocked(context)}");
                    continue;
                }

                if (chunkIndex < context.NextChunkIndex ||
                    context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex) ||
                    context.ReceiverSparseChunksWritten[chunkIndex])
                {
                    continue;
                }

                context.ReceiverSparseChunksPendingWrite.Add(chunkIndex);
                context.BufferedBytes += chunkBytes.Length;
                context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                context.PullReceiverRawBytesRecent += chunkBytes.Length;
                acceptedChunks.Add((chunkIndex, chunkBytes));
            }
        }

        if (acceptedChunks.Count == 0)
        {
            await MaybeSendInboundV6RetryStateAsync(context).ConfigureAwait(false);
            return;
        }

        var writeStopwatch = Stopwatch.StartNew();
        long writtenBytes = 0;
        var writeGateEntered = false;
        try
        {
            await context.ReceiverSparseWriteGate.WaitAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            writeGateEntered = true;
            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                writeStream.Seek((long)chunkIndex * Math.Max(1, context.ChunkSizeBytes), SeekOrigin.Begin);
                await writeStream.WriteAsync(chunkBytes, context.LifetimeCts.Token).ConfigureAwait(false);
                writtenBytes += chunkBytes.Length;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailInboundV4Async(
                context,
                StreamWriteFailedErrorCode,
                ex.Message,
                "Could not write a V6 sparse receiver chunk.").ConfigureAwait(false);
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
        SessionFileTransferSnapshot? snapshot = null;
        int nextChunkIndexAfterCommit;
        int highestReceivedChunkIndexAfterCommit;
        int previousCommittedChunkIndex;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.ReceiverSparseChunksWritten is null)
            {
                return;
            }

            previousCommittedChunkIndex = context.NextChunkIndex;
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
            var progressUtc = DateTimeOffset.UtcNow;
            context.PullLastProgressUtc = progressUtc;
            if (committedChunkCount > 0)
            {
                context.PullLastCommittedProgressUtc = progressUtc;
            }

            context.PullReceiverWriteBatchCountRecent++;
            context.PullReceiverWriteBatchBytesRecent += writtenBytes;
            context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseWriteBatchCountRecent++;
            context.PullReceiverSparseWriteBytesRecent += writtenBytes;
            context.PullReceiverSparseWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            context.PullReceiverSparseChunksWrittenRecent += acceptedChunks.Count;
            context.PullReceiverSparseContiguousChunksCommittedRecent += committedChunkCount;
            context.PullReceiverContiguousBytesCommittedRecent += committedByteCount;
            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            nextChunkIndexAfterCommit = context.NextChunkIndex;
            highestReceivedChunkIndexAfterCommit = context.PullHighestReceivedChunkIndex;
            snapshot = CreateSnapshotLocked();
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; mode=sparse_seekable; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunks.Count}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_sparse_write_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; written_chunk_count={acceptedChunks.Count}; written_bytes={writtenBytes}; contiguous_chunks_committed={committedChunkCount}; contiguous_bytes_committed={committedByteCount}; write_duration_ms={writeStopwatch.ElapsedMilliseconds}; next_chunk_index={nextChunkIndexAfterCommit}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_bytes={context.BufferedBytes}");

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (committedChunkCount > 0)
        {
            await MaybeSendInboundV6RepairProofAsync(
                context,
                batch,
                receivedTransportKind,
                acceptedChunks.Count,
                nextChunkIndexAfterCommit,
                previousCommittedChunkIndex).ConfigureAwait(false);
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }

        if (committedChunkCount > 0)
        {
            var forceState = ShouldForceInboundV6ReceiverStateAfterProgress(context, previousCommittedChunkIndex);
            await SendInboundV6ReceiverStateAsync(context, "chunk_batch_committed", forceSend: forceState).ConfigureAwait(false);
        }
        else
        {
            var forceState = ShouldForceInboundV6ReceiverStateDuringSparseFrontierStall(context);
            if (forceState)
            {
                await SendInboundV6ReceiverStateAsync(
                    context,
                    "chunk_batch_frontier_stalled",
                    forceSend: true).ConfigureAwait(false);
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_deferred; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=frontier_stalled; next_chunk_index={nextChunkIndexAfterCommit}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}");
            }
        }

        await SendInboundV6FrontierRequestAsync(context, "chunk_batch_committed", forceSend: false).ConfigureAwait(false);
    }

    private async Task HandleInboundV6ContiguousChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks,
        Stream writeStream,
        FileTransferTransportKind receivedTransportKind)
    {
        var acceptedChunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(chunks.Count);
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.Hash is null)
            {
                return;
            }

            var expectedChunkIndex = context.NextChunkIndex;
            foreach (var (chunkIndex, chunkBytes) in chunks.OrderBy(static item => item.ChunkIndex))
            {
                if (chunkIndex < expectedChunkIndex)
                {
                    continue;
                }

                if (chunkIndex != expectedChunkIndex)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_unsolicited_chunk_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; mode=contiguous_only; chunk_index={chunkIndex}; expected_chunk_index={expectedChunkIndex}");
                    continue;
                }

                acceptedChunks.Add((chunkIndex, chunkBytes));
                expectedChunkIndex++;
            }
        }

        if (acceptedChunks.Count == 0)
        {
            await MaybeSendInboundV6RetryStateAsync(context).ConfigureAwait(false);
            return;
        }

        var writeStopwatch = Stopwatch.StartNew();
        long writtenBytes = 0;
        try
        {
            foreach (var (_, chunkBytes) in acceptedChunks)
            {
                await writeStream.WriteAsync(chunkBytes, context.LifetimeCts.Token).ConfigureAwait(false);
                writtenBytes += chunkBytes.Length;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailInboundV4Async(
                context,
                StreamWriteFailedErrorCode,
                ex.Message,
                "Could not write a V6 contiguous receiver chunk.").ConfigureAwait(false);
            return;
        }

        writeStopwatch.Stop();
        bool completed;
        SessionFileTransferSnapshot? snapshot = null;
        int nextChunkIndexAfterCommit;
        int previousCommittedChunkIndex;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.Hash is null)
            {
                return;
            }

            previousCommittedChunkIndex = context.NextChunkIndex;
            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                if (chunkIndex != context.NextChunkIndex)
                {
                    continue;
                }

                context.Hash.AppendData(chunkBytes);
                context.NextChunkIndex++;
                context.ChunksTransferred++;
                context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + chunkBytes.Length);
                context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                context.HighestBufferedChunkIndex = Math.Max(context.HighestBufferedChunkIndex, chunkIndex);
                context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                context.PullReceiverRawBytesRecent += chunkBytes.Length;
                context.PullReceiverContiguousBytesCommittedRecent += chunkBytes.Length;
            }

            context.BufferedBytes = 0;
            context.PullLateArrivalDistance = 0;
            var progressUtc = DateTimeOffset.UtcNow;
            context.PullLastProgressUtc = progressUtc;
            if (context.NextChunkIndex > previousCommittedChunkIndex)
            {
                context.PullLastCommittedProgressUtc = progressUtc;
            }

            context.PullReceiverWriteBatchCountRecent++;
            context.PullReceiverWriteBatchBytesRecent += writtenBytes;
            context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            nextChunkIndexAfterCommit = context.NextChunkIndex;
            snapshot = CreateSnapshotLocked();
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; mode=contiguous_only; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunks.Count}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_contiguous_write_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; written_chunk_count={acceptedChunks.Count}; written_bytes={writtenBytes}; write_duration_ms={writeStopwatch.ElapsedMilliseconds}; next_chunk_index={nextChunkIndexAfterCommit}");

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (nextChunkIndexAfterCommit > previousCommittedChunkIndex)
        {
            await MaybeSendInboundV6RepairProofAsync(
                context,
                batch,
                receivedTransportKind,
                acceptedChunks.Count,
                nextChunkIndexAfterCommit,
                previousCommittedChunkIndex).ConfigureAwait(false);
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }

        var forceState = ShouldForceInboundV6ReceiverStateAfterProgress(context, previousCommittedChunkIndex);
        await SendInboundV6ReceiverStateAsync(context, "chunk_batch_committed", forceSend: forceState).ConfigureAwait(false);
        await SendInboundV6FrontierRequestAsync(context, "chunk_batch_committed", forceSend: false).ConfigureAwait(false);
    }

    private async Task MaybeSendInboundV6RetryStateAsync(InboundTransferContext context)
    {
        bool shouldSendState;
        bool shouldSendFrontierRequest;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                context.UserPaused ||
                context.PeerPaused ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var unresolvedFrontierProofRequest =
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.V6TransportEpoch!.State is V6TransportEpochState.TargetProofPending
                    or V6TransportEpochState.FrontierRepairOnly
                    or V6TransportEpochState.WaitingForTargetTransport;
            var frontierRequestRetryInterval = ResolveInboundV6FrontierRequestRetryIntervalLocked(context, unresolvedFrontierProofRequest);
            shouldSendState = context.V6LastReceiverStateSentUtc is null ||
                              now - context.V6LastReceiverStateSentUtc.Value >= TimeSpan.FromMilliseconds(V6ReceiverStateRetryIntervalMs);
            shouldSendFrontierRequest = context.V6LastFrontierRequestSentUtc is null ||
                                        now - context.V6LastFrontierRequestSentUtc.Value >= frontierRequestRetryInterval;
        }

        if (shouldSendState)
        {
            await SendInboundV6ReceiverStateAsync(context, "retry", forceSend: true).ConfigureAwait(false);
        }

        if (shouldSendFrontierRequest)
        {
            await SendInboundV6FrontierRequestAsync(context, "retry", forceSend: true).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendInboundV6ReceiverStateAsync(
        InboundTransferContext context,
        string reason,
        bool forceSend = false)
    {
        FileTransferReceiverStateFrameV6? state;
        IFileTransferDataSession? dataSession;
        int requestWindowChunks;
        bool frontierStalled;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (!forceSend &&
                context.V6LastReceiverStateSentUtc is { } lastSent &&
                now - lastSent < TimeSpan.FromMilliseconds(V6ReceiverStateRetryIntervalMs))
            {
                return false;
            }

            requestWindowChunks = ResolveInboundV6RequestWindowChunksLocked(context);
            frontierStalled = IsInboundV6FrontierStalledLocked(context, now);
            var ranges = BuildInboundV6RequestRangesLocked(context);
            var recoveryMode = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
                ? FormatV6TransportEpochState(context.V6TransportEpoch!.State)
                : frontierStalled && IsInboundV6RegularNknFrontierProgressGracePathLocked(context)
                    ? IsInboundV6RegularNknFrontierControlBulkEscalatedLocked(context, now)
                        ? "regular_nkn_frontier_stall_control_bulk"
                        : "regular_nkn_frontier_stall"
                    : null;
            context.V6ReceiverStateEpoch++;
            var requestedEndExclusive = ranges.Count == 0
                ? context.NextChunkIndex
                : ranges.Max(static range => range.StartChunkIndex + range.ChunkCount);
            if (context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable)
            {
                context.V6SparseAcceptWindowEndExclusive = Math.Max(
                    context.V6SparseAcceptWindowEndExclusive,
                    requestedEndExclusive);
            }

            state = new FileTransferReceiverStateFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V6ReceiverStateEpoch,
                ContiguousCommittedChunkIndex = context.NextChunkIndex,
                DurableReceivedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
                CreditUntilChunkIndexExclusive = Math.Clamp(requestedEndExclusive, context.NextChunkIndex, context.ChunkCount),
                MissingRanges = ranges,
                BytesCommitted = context.BytesTransferred,
                ReceiverMemoryPressure = context.ReceiverBufferPressureActive,
                ReceiverDiskPressure = false,
                TerminalReady = false,
                TransferPaused = context.UserPaused,
                TransferPauseReason = context.UserPauseReason,
                TransportEpoch = context.V6ReceiverTransportEpoch,
                Priority = ranges.Count > 0 && ranges[0].StartChunkIndex == context.NextChunkIndex
                    ? "frontier"
                    : null,
                RecoveryMode = recoveryMode,
            };
            context.V6LastReceiverStateSentUtc = now;
            context.V6LastReceiverStateCommittedChunkIndex = context.NextChunkIndex;
            dataSession = context.DataSession;
        }

        try
        {
            await dataSession.SendAsync(state, context.LifetimeCts.Token).ConfigureAwait(false);
            var requestedChunkCount = 0;
            foreach (var range in state.MissingRanges)
            {
                requestedChunkCount += Math.Max(0, range.ChunkCount);
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_state_sent; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; requested_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; destination_mode={FormatV6DestinationMode(context.V6DestinationMode)}; transfer_paused={(state.TransferPaused ? 1 : 0)}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_request_window_sent; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; requested_chunk_count={requestedChunkCount}; requested_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; request_window_chunks={requestWindowChunks}; frontier_stalled={(frontierStalled ? 1 : 0)}; transport_epoch={state.TransportEpoch}; recovery_mode={FormatProtocolLogValue(state.RecoveryMode)}");
            if (frontierStalled && state.MissingRanges.Count > 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_sparse_frontier_window_clamped; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; requested_chunk_count={requestedChunkCount}; requested_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; request_window_chunks={requestWindowChunks}");
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bool recoveryActive;
            long transportEpoch;
            string recoveryMode;
            lock (gate)
            {
                recoveryActive =
                    ReferenceEquals(inboundTransfer, context) &&
                    !context.IsTerminal &&
                    (IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                     context.PullTransportPaused ||
                     context.PullTransportResumeRequestPending);
                transportEpoch = context.V6TransportEpoch?.EpochId ?? context.V6ReceiverTransportEpoch;
                recoveryMode = context.V6TransportEpoch is null
                    ? "(none)"
                    : FormatV6TransportEpochState(context.V6TransportEpoch.State);
            }

            if (recoveryActive)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_deferred_for_recovery; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; transport_epoch={transportEpoch}; recovery_mode={FormatProtocolLogValue(recoveryMode)}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
                return false;
            }

            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send V6 receiver state.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private bool ShouldForceInboundV6ReceiverStateAfterProgress(
        InboundTransferContext context,
        int previousCommittedChunkIndex)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (IsV6TransportEpochUnresolved(context.V6TransportEpoch))
            {
                return true;
            }

            if (context.V6LastReceiverStateSentUtc is null ||
                context.V6LastReceiverStateCommittedChunkIndex < 0)
            {
                return true;
            }

            var committedDelta = Math.Max(0, context.NextChunkIndex - context.V6LastReceiverStateCommittedChunkIndex);
            if (committedDelta >= V6ReceiverStateProgressMinCommittedChunks)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow - context.V6LastReceiverStateSentUtc.Value >=
                TimeSpan.FromMilliseconds(V6ReceiverStateProgressMaxIntervalMs))
            {
                return true;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_state_coalesced; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_committed_chunk_index={previousCommittedChunkIndex}; current_committed_chunk_index={context.NextChunkIndex}; committed_delta={committedDelta}; last_sent_committed_chunk_index={context.V6LastReceiverStateCommittedChunkIndex}");
            return false;
        }
    }

    private bool ShouldForceInboundV6ReceiverStateDuringSparseFrontierStall(InboundTransferContext context)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.UserPaused ||
                context.PeerPaused ||
                context.V6DestinationMode != V6ReceiveDestinationMode.SparseSeekable ||
                !IsInboundV6FrontierStalledLocked(context))
            {
                return false;
            }

            if (IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                context.V6LastReceiverStateSentUtc is null)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            var elapsedSinceState = now - context.V6LastReceiverStateSentUtc.Value;
            if (elapsedSinceState >= TimeSpan.FromMilliseconds(V6ReceiverStateProgressMaxIntervalMs))
            {
                return true;
            }

            var tailChunksRemaining = context.V6SparseAcceptWindowEndExclusive - (context.PullHighestReceivedChunkIndex + 1);
            var lowWatermarkChunks = Math.Max(V6FrontierRequestChunks, V6SparseSeekableRequestBudgetChunks / 4);
            if (tailChunksRemaining <= lowWatermarkChunks &&
                elapsedSinceState >= TimeSpan.FromMilliseconds(V6NormalReceiverStateResendGateMs / 4))
            {
                return true;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_state_coalesced; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=frontier_stalled_tail_window; current_committed_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; accept_window_end_chunk_index={context.V6SparseAcceptWindowEndExclusive}; tail_chunks_remaining={tailChunksRemaining}; elapsed_since_state_ms={(long)Math.Max(0, elapsedSinceState.TotalMilliseconds)}");
            return false;
        }
    }

    private async Task<bool> SendInboundV6FrontierRequestAsync(
        InboundTransferContext context,
        string reason,
        bool forceSend = false)
    {
        FileTransferFrontierRequestFrameV6? request;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            var unresolvedFrontierProofRequest =
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.V6TransportEpoch!.State is V6TransportEpochState.TargetProofPending
                    or V6TransportEpochState.FrontierRepairOnly
                    or V6TransportEpochState.WaitingForTargetTransport;

            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived ||
                context.UserPaused ||
                context.PeerPaused ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (!ShouldSendInboundV6FrontierRequestLocked(context, now, unresolvedFrontierProofRequest, reason))
            {
                return false;
            }

            var frontierRequestRetryInterval = ResolveInboundV6FrontierRequestRetryIntervalLocked(context, unresolvedFrontierProofRequest);
            if (!forceSend &&
                context.V6LastFrontierRequestSentUtc is { } lastSent &&
                now - lastSent < frontierRequestRetryInterval)
            {
                if (context.V6LastFrontierRequestChunkIndex != context.NextChunkIndex)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_frontier_request_coalesced; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_frontier_chunk_index={context.V6LastFrontierRequestChunkIndex}; current_frontier_chunk_index={context.NextChunkIndex}; elapsed_ms={(long)(now - lastSent).TotalMilliseconds}; retry_interval_ms={(long)frontierRequestRetryInterval.TotalMilliseconds}; reason={FormatProtocolLogValue(reason)}");
                }

                return false;
            }

            context.V6FrontierRequestSequence++;
            var repairRequestId = $"v6-frontier:{context.NextChunkIndex}:{context.V6FrontierRequestSequence}";
            if (IsV6TransportEpochUnresolved(context.V6TransportEpoch))
            {
                repairRequestId = $"v6-frontier:{context.V6TransportEpoch!.EpochId}:{context.NextChunkIndex}:{context.V6FrontierRequestSequence}";
                context.V6TransportEpoch.LastRepairRequestId = repairRequestId;
            }

            var strictEpochFrontierProof = IsV6TransportEpochUnresolved(context.V6TransportEpoch);
            var frontierRequestChunks = strictEpochFrontierProof
                ? ResolveInboundV6EpochFrontierRequestChunksLocked(context)
                : ResolveInboundV6FrontierRequestChunksLocked(context);
            var missingRanges = strictEpochFrontierProof
                ?
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = context.NextChunkIndex,
                        ChunkCount = Math.Min(frontierRequestChunks, context.ChunkCount - context.NextChunkIndex),
                    },
                ]
                : BuildInboundV6FrontierRequestRangesLocked(context, frontierRequestChunks);
            var recoveryMode = strictEpochFrontierProof
                ? FormatV6TransportEpochState(context.V6TransportEpoch!.State)
                : IsInboundV6RegularNknFrontierControlBulkEscalatedLocked(context, now)
                    ? "regular_nkn_frontier_stall_control_bulk"
                    : "regular_nkn_frontier_stall";

            request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = context.V6ReceiverTransportEpoch,
                RepairRequestId = repairRequestId,
                MissingRanges = missingRanges,
                Priority = "frontier",
                RecoveryMode = recoveryMode,
            };
            context.V6LastFrontierRequestSentUtc = now;
            context.V6LastFrontierRequestChunkIndex = context.NextChunkIndex;
            context.V6LastFrontierRequestId = repairRequestId;
            dataSession = context.DataSession;
        }

        try
        {
            await dataSession.SendAsync(request, context.LifetimeCts.Token).ConfigureAwait(false);
            var totalRequestedChunks = request.MissingRanges.Sum(static range => range.ChunkCount);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; recovery_mode={FormatProtocolLogValue(request.RecoveryMode ?? "(none)")}; start_chunk_index={request.MissingRanges[0].StartChunkIndex}; requested_chunk_count={request.MissingRanges[0].ChunkCount}; total_requested_chunk_count={totalRequestedChunks}; range_count={request.MissingRanges.Count}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private static bool ShouldSuppressOutboundV6ReceiveRecoveryForOutstandingBacklogLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        int transportBacklogChunks,
        int inFlightSendCount,
        int normalRequestCount,
        int priorityRequestCount,
        out string reason,
        out int recentChunkSendCount)
    {
        reason = string.Empty;
        var recentWindow = CurrentV6SenderRequestFeedbackStallRecoveryDelay;
        recentChunkSendCount = context.LastChunkSentUtc.Count(pair => now - pair.Value <= recentWindow);
        if (transportBacklogChunks >= V6SenderFeedbackStaleNormalBacklogChunks)
        {
            var regularNknIdleBacklog =
                IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) &&
                normalRequestCount == 0 &&
                priorityRequestCount == 0 &&
                inFlightSendCount == 0 &&
                recentChunkSendCount == 0 &&
                !IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.RegularNknRecovery;
            if (regularNknIdleBacklog)
            {
                return false;
            }

            if (IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) &&
                recentChunkSendCount == 0 &&
                (normalRequestCount > 0 || inFlightSendCount > 0) &&
                priorityRequestCount == 0 &&
                !IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.RegularNknRecovery)
            {
                return false;
            }

            reason = "outstanding_transport_backlog";
            return true;
        }

        if (transportBacklogChunks > 0 &&
            (recentChunkSendCount > 0 || inFlightSendCount > 0))
        {
            if (recentChunkSendCount == 0 &&
                inFlightSendCount > 0 &&
                priorityRequestCount == 0 &&
                IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) &&
                !IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.RegularNknRecovery)
            {
                return false;
            }

            reason = recentChunkSendCount > 0 ? "recent_chunk_sends" : "in_flight_sends";
            return true;
        }

        return false;
    }

    private static bool TryPrepareOutboundV6PrimaryRegularNknReceiveRecoveryWithoutEpochLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int inFlightSendCount,
        int recentChunkSendCount,
        int normalRequestCount,
        int priorityRequestCount,
        out FileTransferReceiveRecoveryRequest? recoveryRequest)
    {
        recoveryRequest = null;
        if (!IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) ||
            IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
            context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.RegularNknRecovery ||
            transportBacklogChunks <= 0 ||
            inFlightSendCount != 0 ||
            recentChunkSendCount != 0 ||
            normalRequestCount != 0 ||
            priorityRequestCount != 0)
        {
            return false;
        }

        context.V6LastReceiveRecoveryRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.V6SenderPumpLastWakeReason = "regular_nkn_receive_recovery";

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
        var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_sender_feedback_stale_regular_nkn_receive_recovery_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; in_flight_send_count={inFlightSendCount}; recent_chunk_send_count={recentChunkSendCount}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}; epoch_started=0");

        recoveryRequest = new FileTransferReceiveRecoveryRequest(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            "sender_request_feedback_stalled");
        return true;
    }

    private static bool TryPrepareOutboundV6PrimaryRegularNknStaleNormalPipelineRecoveryWithoutEpochLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int inFlightSendCount,
        int recentChunkSendCount,
        int normalRequestCount,
        int priorityRequestCount,
        out FileTransferReceiveRecoveryRequest? recoveryRequest)
    {
        recoveryRequest = null;
        if (!IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) ||
            IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
            context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.RegularNknRecovery ||
            transportBacklogChunks <= 0 ||
            recentChunkSendCount != 0 ||
            priorityRequestCount != 0 ||
            (normalRequestCount == 0 && inFlightSendCount == 0))
        {
            return false;
        }

        var clearedNormalRequestCount = 0;
        foreach (var chunkIndex in context.V6NormalRequestedChunks.ToArray())
        {
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                context.V6RequestedChunkMetadataByChunkIndex.Remove(chunkIndex);
            }

            clearedNormalRequestCount++;
        }

        var clearedInFlightCount = 0;
        foreach (var chunkIndex in context.V6ChunkSendsInFlight.Keys.Where(chunkIndex => chunkIndex >= context.RemoteNextExpectedChunkIndex).ToArray())
        {
            context.V6ChunkSendsInFlight.Remove(chunkIndex);
            clearedInFlightCount++;
        }

        context.V6CurrentNormalRequestKey = null;
        var pipelineGeneration = context.ResetV6SenderPipelineCancellation();
        context.V6LastReceiveRecoveryRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.V6SenderPumpLastWakeReason = "regular_nkn_stale_normal_pipeline_cleared";

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
        var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_sender_feedback_stale_regular_nkn_normal_pipeline_cleared; transfer_id={context.TransferId}; session_id={context.SessionId}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}; in_flight_send_count={inFlightSendCount}; recent_chunk_send_count={recentChunkSendCount}; cleared_normal_request_count={clearedNormalRequestCount}; cleared_in_flight_chunk_count={clearedInFlightCount}; sender_pipeline_generation={pipelineGeneration}; epoch_started=0");

        recoveryRequest = new FileTransferReceiveRecoveryRequest(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            "sender_request_feedback_stalled");
        return true;
    }

    private static void LogOutboundV6FeedbackStallRecoverySuppressedLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string reason,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int inFlightSendCount,
        int recentChunkSendCount,
        int normalRequestCount,
        int priorityRequestCount)
    {
        if (context.V6LastFeedbackStallRecoverySuppressedUtc is { } lastLogged &&
            now - lastLogged < TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoverySuppressedLogIntervalMs))
        {
            return;
        }

        context.V6LastFeedbackStallRecoverySuppressedUtc = now;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_sender_feedback_stale_recovery_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; in_flight_send_count={inFlightSendCount}; recent_chunk_send_count={recentChunkSendCount}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}");
    }

    private static IReadOnlyList<FileTransferRangeV4> BuildInboundV6FrontierRequestRangesLocked(
        InboundTransferContext context,
        int requestedChunkLimit)
    {
        var maxChunks = Math.Min(requestedChunkLimit, context.ChunkCount - context.NextChunkIndex);
        if (maxChunks <= 0)
        {
            return [];
        }

        if (context.V6DestinationMode != V6ReceiveDestinationMode.SparseSeekable ||
            !IsInboundV6RegularNknFrontierProgressGracePathLocked(context))
        {
            return
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = context.NextChunkIndex,
                    ChunkCount = Math.Max(1, maxChunks),
                },
            ];
        }

        var nearFrontierEndExclusive = context.NextChunkIndex + maxChunks;
        var observedEndExclusive = context.PullHighestReceivedChunkIndex >= context.NextChunkIndex
            ? context.PullHighestReceivedChunkIndex + 1
            : nearFrontierEndExclusive;
        var scanEndExclusive = Math.Min(
            context.ChunkCount,
            Math.Min(
                context.NextChunkIndex + V6RegularNknFrontierRepairScanHorizonChunks,
                Math.Max(nearFrontierEndExclusive, observedEndExclusive)));
        if (scanEndExclusive <= context.NextChunkIndex)
        {
            scanEndExclusive = Math.Min(context.ChunkCount, context.NextChunkIndex + Math.Max(1, maxChunks));
        }

        var ranges = new List<FileTransferRangeV4>();
        var requestedChunks = 0;
        var chunkIndex = context.NextChunkIndex;
        while (chunkIndex < scanEndExclusive &&
               ranges.Count < FileTransferProtocol.MaxStateMissingRangesV6 &&
               requestedChunks < maxChunks)
        {
            if (IsInboundV6ChunkPresentOrPendingLocked(context, chunkIndex))
            {
                chunkIndex++;
                continue;
            }

            var start = chunkIndex;
            var count = 0;
            while (chunkIndex < scanEndExclusive &&
                   !IsInboundV6ChunkPresentOrPendingLocked(context, chunkIndex) &&
                   requestedChunks + count < maxChunks)
            {
                count++;
                chunkIndex++;
            }

            if (count > 0)
            {
                ranges.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
                requestedChunks += count;
            }
        }

        return ranges.Count > 0
            ? ranges
            :
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = context.NextChunkIndex,
                    ChunkCount = Math.Max(1, maxChunks),
                },
            ];
    }

    private static int ResolveInboundV6FrontierRequestChunksLocked(InboundTransferContext context)
    {
        var configuredMaxChunks = IsInboundV6RegularNknFrontierProgressGracePathLocked(context)
            ? V6RegularNknFrontierRepairBurstChunks
            : V6FrontierRequestChunks;
        var maxChunks = Math.Min(configuredMaxChunks, context.ChunkCount - context.NextChunkIndex);
        if (maxChunks <= 1 || context.V6DestinationMode != V6ReceiveDestinationMode.SparseSeekable)
        {
            return Math.Max(1, maxChunks);
        }

        if (IsInboundV6RegularNknFrontierProgressGracePathLocked(context))
        {
            return Math.Max(1, maxChunks);
        }

        var chunkCount = 0;
        for (var chunkIndex = context.NextChunkIndex;
             chunkIndex < context.NextChunkIndex + maxChunks;
             chunkIndex++)
        {
            if (IsInboundV6ChunkPresentOrPendingLocked(context, chunkIndex))
            {
                break;
            }

            chunkCount++;
        }

        return Math.Max(1, chunkCount);
    }

    private static int ResolveInboundV6EpochFrontierRequestChunksLocked(InboundTransferContext context)
    {
        var remainingChunks = context.ChunkCount - context.NextChunkIndex;
        var maxChunks = Math.Min(V6EpochFrontierRequestChunks, remainingChunks);
        if (maxChunks <= 0)
        {
            return 1;
        }

        if (context.V6TransportEpoch is
            {
                TargetTransport: FileTransferTransportKind.RegularNkn,
                Kind: FileTransferTransportHandoffKind.RegularNknRecovery,
            } &&
            context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable &&
            !IsInboundV6ChunkPresentOrPendingLocked(context, context.NextChunkIndex))
        {
            return Math.Max(1, Math.Min(V6RegularNknFrontierRepairBurstChunks, remainingChunks));
        }

        return Math.Max(1, maxChunks);
    }

    private static TimeSpan CurrentV6RegularNknFrontierRequestProgressGrace =>
        V6RegularNknFrontierRequestProgressGraceOverrideForTests ??
        TimeSpan.FromMilliseconds(V6RegularNknFrontierRequestProgressGraceMs);

    private static bool ShouldSendInboundV6FrontierRequestLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        bool unresolvedFrontierProofRequest,
        string reason)
    {
        if (unresolvedFrontierProofRequest)
        {
            ResetInboundV6FrontierStallGraceLocked(context);
            return true;
        }

        if (!IsInboundV6FrontierStalledLocked(context, now))
        {
            ResetInboundV6FrontierStallGraceLocked(context);
            return false;
        }

        if (context.V6FrontierStallStartedUtc is null ||
            context.V6FrontierStallChunkIndex != context.NextChunkIndex)
        {
            context.V6FrontierStallStartedUtc = now;
            context.V6FrontierStallChunkIndex = context.NextChunkIndex;
            context.V6FrontierStallLastDeferredLogUtc = now;
            LogInboundV6FrontierRequestDeferred(
                context,
                now,
                0,
                reason,
                ResolveInboundV6FrontierRequestStallGraceLocked(context, unresolvedFrontierProofRequest));
            return false;
        }

        var elapsedMs = (long)(now - context.V6FrontierStallStartedUtc.Value).TotalMilliseconds;
        var stallGrace = ResolveInboundV6FrontierRequestStallGraceLocked(context, unresolvedFrontierProofRequest);
        if (elapsedMs < stallGrace.TotalMilliseconds)
        {
            if (context.V6FrontierStallLastDeferredLogUtc is null ||
                now - context.V6FrontierStallLastDeferredLogUtc.Value >= TimeSpan.FromSeconds(5))
            {
                context.V6FrontierStallLastDeferredLogUtc = now;
                LogInboundV6FrontierRequestDeferred(context, now, elapsedMs, reason, stallGrace);
            }

            return false;
        }

        return true;
    }

    private static void ResetInboundV6FrontierStallGraceLocked(InboundTransferContext context)
    {
        context.V6FrontierStallStartedUtc = null;
        context.V6FrontierStallChunkIndex = -1;
        context.V6FrontierStallLastDeferredLogUtc = null;
    }

    private static bool IsInboundV6RegularNknFrontierControlBulkEscalatedLocked(
        InboundTransferContext context,
        DateTimeOffset now)
    {
        if (context.V6FrontierStallStartedUtc is not { } started ||
            context.V6FrontierStallChunkIndex != context.NextChunkIndex)
        {
            return false;
        }

        return now - started >= ResolveInboundV6RegularNknFrontierControlBulkEscalationLocked(context);
    }

    private static TimeSpan ResolveInboundV6FrontierRequestRetryIntervalLocked(
        InboundTransferContext context,
        bool unresolvedFrontierProofRequest)
        => !unresolvedFrontierProofRequest &&
           IsInboundV6RegularNknFrontierProgressGracePathLocked(context)
            ? TimeSpan.FromMilliseconds(V6RegularNknFrontierRequestRetryIntervalMs)
            : TimeSpan.FromMilliseconds(V6FrontierRequestRetryIntervalMs);

    private static TimeSpan ResolveInboundV6FrontierRequestStallGraceLocked(
        InboundTransferContext context,
        bool unresolvedFrontierProofRequest)
        => !unresolvedFrontierProofRequest &&
           IsInboundV6RegularNknFrontierProgressGracePathLocked(context)
            ? TimeSpan.FromMilliseconds(V6RegularNknFrontierRequestStallGraceMs)
            : TimeSpan.FromMilliseconds(V6FrontierRequestStallGraceMs);

    private static TimeSpan ResolveInboundV6RegularNknFrontierControlBulkEscalationLocked(InboundTransferContext context)
        => IsInboundV6RegularNknFrontierProgressGracePathLocked(context)
            ? TimeSpan.FromMilliseconds(V6RegularNknFrontierControlBulkEscalationMs)
            : TimeSpan.FromMilliseconds(1000);

    private static void LogInboundV6FrontierRequestDeferred(
        InboundTransferContext context,
        DateTimeOffset now,
        long elapsedMs,
        string reason,
        TimeSpan stallGrace)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_deferred; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frontier_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; elapsed_ms={elapsedMs}; stall_grace_ms={(long)stallGrace.TotalMilliseconds}; reason={FormatProtocolLogValue(reason)}; utc={FormatProtocolLogValue(now.ToString("O"))}");

    private async Task FlushInboundV6PausedProgressAsync(InboundTransferContext context, string reason)
    {
        bool completed;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                context.UserPaused ||
                context.PeerPaused)
            {
                return;
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }

        await SendInboundV6ReceiverStateAsync(context, reason, forceSend: true).ConfigureAwait(false);
        await SendInboundV6FrontierRequestAsync(context, reason, forceSend: true).ConfigureAwait(false);
    }

    private Task<bool> SendInboundPauseProgressStateAsync(InboundTransferContext context, string reason)
        => ShouldUseInboundSparseCreditProgressState(context)
            ? SendInboundV4StateAsync(context, reason, terminalReady: false, forceSend: true)
            : SendInboundV6ReceiverStateAsync(context, reason, forceSend: true);

    private async Task FlushInboundPausedProgressAsync(InboundTransferContext context, string reason)
    {
        if (!ShouldUseInboundSparseCreditProgressState(context))
        {
            await FlushInboundV6PausedProgressAsync(context, reason).ConfigureAwait(false);
            return;
        }

        bool completed;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !context.PullManifestReceived ||
                context.UserPaused ||
                context.PeerPaused)
            {
                return;
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }

        await SendInboundV4StateAsync(context, reason, terminalReady: false, forceSend: true).ConfigureAwait(false);
    }

    private bool ShouldUseInboundSparseCreditProgressState(InboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(inboundTransfer, context) &&
                   !context.IsTerminal &&
                   (context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 ||
                    (context.ReceiverSparseWriteActive &&
                     ShouldAdvertiseInboundV4SparseWrittenProgressLocked(context)));
        }
    }

    private IReadOnlyList<FileTransferRangeV4> BuildInboundV6RequestRangesLocked(InboundTransferContext context)
    {
        if (context.UserPaused ||
            context.PeerPaused ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return [];
        }

        var maxChunks = Math.Min(ResolveInboundV6RequestWindowChunksLocked(context), context.ChunkCount - context.NextChunkIndex);
        if (maxChunks <= 0)
        {
            return [];
        }

        if (IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
            context.V6TransportEpoch!.State is V6TransportEpochState.TargetProofPending
                or V6TransportEpochState.FrontierRepairOnly
                or V6TransportEpochState.WaitingForTargetTransport)
        {
            var frontierChunks = ResolveInboundV6EpochFrontierRequestChunksLocked(context);
            return
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = context.NextChunkIndex,
                    ChunkCount = Math.Min(frontierChunks, context.ChunkCount - context.NextChunkIndex),
                },
            ];
        }

        if (IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
            context.V6TransportEpoch!.State is not V6TransportEpochState.BackfillRepair)
        {
            return [];
        }

        if (context.V6DestinationMode == V6ReceiveDestinationMode.ContiguousOnly)
        {
            var frontierChunks = Math.Min(V6EpochFrontierRequestChunks, context.ChunkCount - context.NextChunkIndex);
            return
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = context.NextChunkIndex,
                    ChunkCount = frontierChunks,
                },
            ];
        }

        var ranges = new List<FileTransferRangeV4>();
        var requestedChunks = 0;
        var maxRequestedChunks = Math.Min(FileTransferProtocol.MaxStateMissingChunksV6, maxChunks);
        var scanEndExclusive = Math.Min(context.ChunkCount, context.NextChunkIndex + maxChunks);
        if (context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable)
        {
            var frontierStalled = IsInboundV6FrontierStalledLocked(context);
            var rollingTailStart = Math.Max(context.NextChunkIndex, context.PullHighestReceivedChunkIndex + 1);
            var rollingTailEndExclusive = rollingTailStart + maxChunks;
            var maxAheadEndExclusive = context.NextChunkIndex +
                (frontierStalled
                    ? V6SparseSeekableFrontierStalledRollingAheadChunks
                    : V6SparseSeekableRollingAheadChunks);
            scanEndExclusive = Math.Min(
                context.ChunkCount,
                Math.Min(
                    Math.Max(scanEndExclusive, rollingTailEndExclusive),
                    maxAheadEndExclusive));
            var requestBudgetChunks = frontierStalled
                ? Math.Min(maxChunks, V6SparseSeekableFrontierStalledRequestBudgetChunks)
                : Math.Max(maxChunks, V6SparseSeekableRequestBudgetChunks);
            maxRequestedChunks = Math.Min(
                FileTransferProtocol.MaxStateMissingChunksV6,
                requestBudgetChunks);
        }

        var chunkIndex = context.NextChunkIndex;
        while (chunkIndex < scanEndExclusive &&
               ranges.Count < FileTransferProtocol.MaxStateMissingRangesV6 &&
               requestedChunks < maxRequestedChunks)
        {
            if (IsInboundV6ChunkPresentOrPendingLocked(context, chunkIndex))
            {
                chunkIndex++;
                continue;
            }

            var start = chunkIndex;
            var count = 0;
            while (chunkIndex < scanEndExclusive &&
                   !IsInboundV6ChunkPresentOrPendingLocked(context, chunkIndex) &&
                   requestedChunks + count < maxRequestedChunks)
            {
                count++;
                chunkIndex++;
            }

            if (count > 0)
            {
                ranges.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
                requestedChunks += count;
            }
        }

        return ranges;
    }

    private static int GetInboundV6RequestWindowEndExclusiveLocked(InboundTransferContext context)
        => Math.Min(context.ChunkCount, context.NextChunkIndex + ResolveInboundV6RequestWindowChunksLocked(context));

    private static int GetInboundV6AcceptWindowEndExclusiveLocked(InboundTransferContext context)
        => context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable
            ? Math.Min(
                context.ChunkCount,
                Math.Max(
                    GetInboundV6RequestWindowEndExclusiveLocked(context),
                    context.V6SparseAcceptWindowEndExclusive))
            : GetInboundV6RequestWindowEndExclusiveLocked(context);

    private static bool IsInboundV6ChunkPresentOrPendingLocked(InboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < context.NextChunkIndex)
        {
            return true;
        }

        if (context.V6DestinationMode == V6ReceiveDestinationMode.ContiguousOnly)
        {
            return false;
        }

        return IsInboundV4ChunkPresentOrPendingLocked(context, chunkIndex);
    }

    private static bool IsInboundV6FrontierStalledLocked(
        InboundTransferContext context,
        DateTimeOffset? nowOverride = null)
    {
        if (context.V6DestinationMode != V6ReceiveDestinationMode.SparseSeekable ||
            context.NextChunkIndex >= context.ChunkCount ||
            IsInboundV6ChunkPresentOrPendingLocked(context, context.NextChunkIndex))
        {
            return false;
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        if (context.PullHighestReceivedChunkIndex >= context.NextChunkIndex)
        {
            if (ShouldDeferInboundV6FrontierStallForRecentCommittedProgressLocked(context, now))
            {
                return false;
            }

            return true;
        }

        // A true sparse gap is handled above. If the receiver has only sent a
        // normal request and no data has arrived yet, give regular NKN a short
        // resend window before promoting the frontier to repair mode.
        var lastProgressUtc = context.PullLastProgressUtc ?? context.V6LastReceiverStateSentUtc;
        var resendGateMs = V6NormalReceiverStateResendGateMs;
        return lastProgressUtc is not null &&
               context.V6LastReceiverStateSentUtc is not null &&
               context.V6LastReceiverStateCommittedChunkIndex == context.NextChunkIndex &&
               now - lastProgressUtc.Value >=
               TimeSpan.FromMilliseconds(resendGateMs);
    }

    private static bool ShouldDeferInboundV6FrontierStallForRecentCommittedProgressLocked(
        InboundTransferContext context,
        DateTimeOffset now)
    {
        if (!IsInboundV6RegularNknFrontierProgressGracePathLocked(context) ||
            context.PullLastCommittedProgressUtc is not { } lastCommittedProgressUtc)
        {
            return false;
        }

        return now - lastCommittedProgressUtc < CurrentV6RegularNknFrontierRequestProgressGrace;
    }

    private static bool IsInboundV6RegularNknFrontierProgressGracePathLocked(InboundTransferContext context)
    {
        if (IsV6TransportEpochUnresolved(context.V6TransportEpoch))
        {
            return false;
        }

        if (context.V6TransportEpoch is { TargetTransport: FileTransferTransportKind.Tuna })
        {
            return false;
        }

        return context.LastRecoveredV6TransportEpoch == 0 ||
               context.LastRecoveredV6TransportTargetTransport != FileTransferTransportKind.Tuna;
    }

    private static bool IsInboundV6ChunkRequestedLocked(InboundTransferContext context, int chunkIndex)
    {
        if (chunkIndex < context.NextChunkIndex || chunkIndex >= context.ChunkCount)
        {
            return false;
        }

        if (context.V6DestinationMode == V6ReceiveDestinationMode.ContiguousOnly)
        {
            return chunkIndex == context.NextChunkIndex;
        }

        return chunkIndex < GetInboundV6AcceptWindowEndExclusiveLocked(context);
    }

    private static int ResolveInboundV6RequestWindowChunksLocked(InboundTransferContext context)
    {
        var windowChunks = V6ReceiverRequestWindowChunks;
        if (IsInboundV6RegularNknFrontierProgressGracePathLocked(context))
        {
            windowChunks = Math.Min(windowChunks, V6RegularNknReceiverRequestWindowChunks);
        }

        var recoveredRegularNkn = IsRecoveredInboundV6TunaFallbackRegularNknEpoch(context);
        var frontierStalled = IsInboundV6FrontierStalledLocked(context);
        if (frontierStalled)
        {
            var frontierWindowChunks = recoveredRegularNkn
                ? V6RecoveredRegularNknFrontierStalledReceiverRequestWindowChunks
                : V6FrontierStalledReceiverRequestWindowChunks;
            windowChunks = Math.Min(windowChunks, frontierWindowChunks);
        }
        else if (recoveredRegularNkn)
        {
            windowChunks = Math.Min(windowChunks, V6RecoveredRegularNknReceiverRequestWindowChunks);
        }

        return Math.Max(V6FrontierRequestChunks, windowChunks);
    }

    private static bool IsRecoveredInboundV6TunaFallbackRegularNknEpoch(InboundTransferContext context)
    {
        if (context.V6TransportEpoch is { } current)
        {
            return current is
            {
                TargetTransport: FileTransferTransportKind.RegularNkn,
                State: V6TransportEpochState.Recovered,
                Kind: FileTransferTransportHandoffKind.TunaToNormalFallback,
            };
        }

        return context.LastRecoveredV6TransportEpoch > 0 &&
               context.LastRecoveredV6TransportTargetTransport == FileTransferTransportKind.RegularNkn &&
               context.LastRecoveredV6TransportEpochKind == FileTransferTransportHandoffKind.TunaToNormalFallback;
    }

    private static IReadOnlyList<FileTransferRangeV4> NormalizeV6RequestRanges(
        IReadOnlyList<FileTransferRangeV4> ranges,
        int chunkCount)
    {
        if (ranges.Count == 0 || chunkCount <= 0)
        {
            return [];
        }

        var result = new List<FileTransferRangeV4>();
        var requestedChunks = 0;
        foreach (var range in ranges)
        {
            if (range.ChunkCount <= 0)
            {
                continue;
            }

            var start = Math.Clamp(range.StartChunkIndex, 0, chunkCount);
            var endExclusive = Math.Clamp(range.StartChunkIndex + range.ChunkCount, start, chunkCount);
            var count = Math.Min(endExclusive - start, FileTransferProtocol.MaxStateMissingChunksV6 - requestedChunks);
            if (count <= 0)
            {
                break;
            }

            result.Add(new FileTransferRangeV4 { StartChunkIndex = start, ChunkCount = count });
            requestedChunks += count;
            if (result.Count >= FileTransferProtocol.MaxStateMissingRangesV6 ||
                requestedChunks >= FileTransferProtocol.MaxStateMissingChunksV6)
            {
                break;
            }
        }

        return result;
    }

    private static string FormatV6DestinationMode(V6ReceiveDestinationMode mode)
        => mode switch
        {
            V6ReceiveDestinationMode.SparseSeekable => "sparse_seekable",
            V6ReceiveDestinationMode.ContiguousOnly => "contiguous_only",
            _ => "unknown",
        };
}
