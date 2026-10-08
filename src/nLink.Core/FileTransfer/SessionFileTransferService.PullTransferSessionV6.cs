using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private async Task RunOutboundV6SenderAsync(OutboundTransferContext context)
    {
        IFileTransferDataSession? dataSession = null;
        RuntimeOwnerToken? startupFallbackOwner = null;
        FileTransferLeg? startupFallbackLeg = null;
        long startupFallbackTransportEpoch = 0;
        long startupDataSessionGeneration = 0;
        bool startupFallbackCheckpointRequired = false;
        RoutePublicationToken? runtimePublicationToken;
        bool IsStartupFallbackSourceCurrentLocked()
            => ReferenceEquals(outboundTransfer, context) && !context.IsTerminal &&
               !context.LifetimeCts.IsCancellationRequested &&
               ReferenceEquals(context.DataSession, dataSession) &&
               context.DataSessionGeneration == startupDataSessionGeneration &&
               ReferenceEquals(context.CurrentTransferLeg, startupFallbackLeg) &&
               context.CurrentTransferLeg?.TransportEpochId == startupFallbackTransportEpoch &&
               startupFallbackOwner is { } owner &&
               FileTransferCoordinator.IsRuntimeOwnerCurrent(CreateCoordinatorStateLocked(context), owner, out _);
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return;
            }

            runtimePublicationToken = context.CurrentRoutePublicationToken;
        }

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
            lock (gate)
            {
                if (!TryPrepareBootstrapSessionOpenLocked(context, ref sessionOpen)) { return; }
            }

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

                if (runtimePublicationToken is { IsDefault: false } capturedPublication &&
                    !TryValidateRoutePublicationForEmissionLocked(
                        context,
                        "v6_sender_start",
                        "runtime_start_data_session_opened",
                        routeCommitToken: null,
                        requestedToken: capturedPublication,
                        out _,
                        out _))
                {
                    dataSession.Dispose();
                    return;
                }

                if (!ReplaceOutboundDataSessionLocked(context, dataSession))
                {
                    dataSession.Dispose();
                    return;
                }
                context.PullSessionActive = true;
                context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
                context.RemoteNextExpectedChunkIndex = 0;
                context.RemoteGrantedUntilExclusive = 0;
                context.ChunksAcceptedForTransport = 0;
                context.BytesAcceptedForTransport = 0;
                context.V6LastReceiverStateEpoch = -1;
                context.V6LastReceiverFeedbackReceivedUtc = null;
                context.V6NextSequentialSourceChunkIndex = 0;
                SettleAndClearFallbackFrontierRepairContractLocked(
                    context,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    "v6_sender_startup_reset");
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
                if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                {
                    startupFallbackCheckpointRequired = true;
                    StartOutboundPostTunaRecoveryLocked(context, "post_tuna_fallback_v6_route");
                    // Startup can publish a successor recovery leg. Keep its diagnostic
                    // token, after validating the opening source above, not the old offer token.
                    runtimePublicationToken = context.CurrentRoutePublicationToken;
                    startupFallbackOwner = context.CurrentRuntimeOwner;
                    startupFallbackLeg = context.CurrentTransferLeg;
                    startupFallbackTransportEpoch = startupFallbackLeg?.TransportEpochId ?? 0;
                    startupDataSessionGeneration = context.DataSessionGeneration;
                }
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_sender_started; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={FileTransferProtocol.ProtocolVersionV6}; route={context.RouteSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; request_driven=1");
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                {
                    if (runtimePublicationToken is { IsDefault: false } publication)
                    {
                        TryPublishRuntimeStartedIfAuthorizedLocked(
                            context,
                            "sender",
                            "v6_sender_start",
                            "runtime_started",
                            routePublicationToken: publication);
                    }
                }
            }
            LogV6PostTunaFallbackSurvivalPolicyEnabled(context, FileTransferDirection.Outbound);

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
            if (startupFallbackCheckpointRequired)
            {
                lock (gate)
                {
                    if (!IsStartupFallbackSourceCurrentLocked())
                    {
                        return;
                    }
                }
            }
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
            Task? senderPumpTask = startupFallbackCheckpointRequired
                ? null
                : RunOutboundV6SenderPumpAsync(context, stream, dataSession);
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                pendingReceiveTask ??= dataSession.ReceiveWithMetadataAsync(context.LifetimeCts.Token).AsTask();

                var poll = Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token);
                var completed = senderPumpTask is null
                    ? await Task.WhenAny(pendingReceiveTask, poll).ConfigureAwait(false)
                    : await Task.WhenAny(pendingReceiveTask, senderPumpTask, poll).ConfigureAwait(false);
                if (senderPumpTask is not null && completed == senderPumpTask)
                {
                    await senderPumpTask.ConfigureAwait(false);
                    return;
                }

                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        await StopOutboundSparseSenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var received = await pendingReceiveTask.ConfigureAwait(false);
                var frame = received.Frame;
                pendingReceiveTask = null;
                if (startupFallbackCheckpointRequired)
                {
                    lock (gate)
                    {
                        if (!IsStartupFallbackSourceCurrentLocked()) { return; }
                    }
                }
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (!IsFrameForContext(context, frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "session_or_transfer_mismatch_v6");
                    continue;
                }

                if (await TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Outbound,
                        frame,
                        received.TransportKind,
                        dataSession).ConfigureAwait(false))
                {
                    continue;
                }

                if (TryHandleBootstrapCorrelationFrame(FileTransferDirection.Outbound, dataSession, received)) { continue; }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    SessionFileTransferSnapshot? legacyProofSnapshot = null;
                    if (frame is FileTransferStateFrameV4 legacyState)
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(outboundTransfer, context) &&
                                !context.IsTerminal &&
                                TryRecoverOutboundV6RegularNknEpochFromLegacyV4PeerStateLocked(
                                    context,
                                    legacyState,
                                    received.TransportKind,
                                    "regular_nkn_legacy_v4_state_proof"))
                            {
                                legacyProofSnapshot = CreateSnapshotLocked();
                            }
                        }
                    }

                    if (legacyProofSnapshot is not null)
                    {
                        RaiseTransferChanged(legacyProofSnapshot);
                        TouchOutboundV6PeerLiveness(context, "regular_nkn_legacy_v4_state_proof");
                        SignalOutboundSparseSenderPump(context);
                    }

                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "protocol_not_v6");
                    continue;
                }

                if (frame is not FileTransferFrontierRequestFrameV6 and not FileTransferReceiverStateFrameV6)
                {
                    TouchOutboundV6PeerLivenessIfAuthoritative(context, received, "v6_data_frame");
                }
                switch (frame)
                {
                    case FileTransferReceiverStateFrameV6 state:
                        if (startupFallbackCheckpointRequired)
                        {
                            lock (gate)
                            {
                                if (!IsStartupFallbackSourceCurrentLocked()) { return; }
                                if (state.TransportEpoch != startupFallbackTransportEpoch) { continue; }
                            }
                        }
                        if (await ApplyOutboundV6ReceiverState(context, state, received.TransportKind).ConfigureAwait(false))
                        {
                            TouchOutboundV6PeerLivenessIfAuthoritative(context, received, "v6_data_frame");
                        }
                        if (startupFallbackCheckpointRequired)
                        {
                            lock (gate)
                            {
                                if (!IsStartupFallbackSourceCurrentLocked()) { return; }
                                if (state.TransportEpoch != startupFallbackTransportEpoch) { continue; }
                                // Receipt of the manifest-derived state only schedules the exchange;
                                // the coordinator must still accept its exact checkpoint proof.
                                context.V6StartupCheckpointOwner = startupFallbackOwner;
                                QueueOutboundPostTunaFallbackCheckpointRequestLocked(context, "v6_startup_receiver_state");
                                startupFallbackCheckpointRequired = false;
                            }
                            // A rejected startup continuation must not leave an unowned data pump behind.
                            senderPumpTask = RunOutboundV6SenderPumpAsync(context, stream, dataSession);
                        }
                        SignalOutboundSparseSenderPump(context);
                        break;
                    case FileTransferFrontierRequestFrameV6 frontierRequest:
                        ApplyOutboundV6FrontierRequest(context, frontierRequest, received.TransportKind);
                        SignalOutboundSparseSenderPump(context);
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
                    case FileTransferFallbackTailYieldFrameV6 yield:
                        ApplyOutboundFallbackTailYieldProofWithEvidence(context, yield, received.YieldEvidence);
                        break;
                    case FileTransferRepairProofFrameV6:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "repair_proof_control_required");
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferCompleteFrameV4 complete:
                        if (await TryHandleOutboundLifecycleCompleteDataFrameAsync(context, complete).ConfigureAwait(false))
                        {
                            return;
                        }

                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_lifecycle_data_frame_ignored; kind=complete; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=metadata_mismatch; file_size_bytes={complete.FileSizeBytes}");
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
                        if (await TryHandleOutboundLifecycleErrorDataFrameAsync(context, error).ConfigureAwait(false))
                        {
                            return;
                        }

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

    private static void LogV6PostTunaFallbackSurvivalPolicyEnabled(
        OutboundTransferContext context,
        FileTransferDirection direction)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_survival_policy_enabled; direction={direction.ToString().ToLowerInvariant()}; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; frontier_chunk_index={Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount - 1))}");
    }

    private static void LogV6PostTunaFallbackSurvivalPolicyEnabled(
        InboundTransferContext context,
        FileTransferDirection direction)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_survival_policy_enabled; direction={direction.ToString().ToLowerInvariant()}; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; frontier_chunk_index={Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount - 1))}; destination_mode={FormatV6DestinationMode(context.V6DestinationMode)}");
    }

    private async Task<bool> ApplyOutboundV6ReceiverState(
        OutboundTransferContext context,
        FileTransferReceiverStateFrameV6 state,
        FileTransferTransportKind receivedTransportKind)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool completeFromFallbackTerminalReady = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            var startupCheckpointProof = context.V6StartupCheckpointOwner is { } startupOwner &&
                context.CurrentRuntimeOwner == startupOwner;
            if (startupCheckpointProof &&
                (!IsPostTunaFallbackCheckpointProofReceiverState(state) ||
                 !FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(context.CurrentTransferLeg,
                     context.RouteSelection.Route, context.NegotiatedDataProtocolVersion,
                     context.CurrentLiveRouteEpoch?.EpochId ?? 0, state.TransportEpoch, state.RepairRequestId, out _)))
            {
                LogPullDataFrameIgnored(context.TransferId, context.SessionId, state, "startup_checkpoint_proof_mismatch");
                return false;
            }

            if (!TryAcceptOutboundFallbackCheckpointLocked(context, state, "receiver_state"))
            {
                return false;
            }

            TryResumeOutboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
                context,
                "fallback_checkpoint_accepted");

            context.V6LastReceiverFeedbackReceivedUtc = DateTimeOffset.UtcNow;
            var receiverProgressChanged = UpdateOutboundReceiverAcknowledgedProgressFromV4StateLocked(context, state);
            var stateCarriesPostYieldRecoveryWork =
                state.MissingRanges.Count > 0 ||
                !string.IsNullOrWhiteSpace(state.RepairRequestId) ||
                !string.IsNullOrWhiteSpace(state.RecoveryMode) ||
                !string.IsNullOrWhiteSpace(state.Priority);
            var suppressPostYieldReceiverRecoveryWork =
                stateCarriesPostYieldRecoveryWork &&
                ShouldIgnoreOutboundPostYieldFallbackRecoveryFrameLocked(
                    context,
                    state.TransportEpoch,
                    FileTransferProtocol.ReceiverStateFrameTypeV6,
                    "peer_receiver_state_after_fallback_yield");
            var recoveredFromReceiverStateProof =
                !suppressPostYieldReceiverRecoveryWork &&
                TryRecoverOutboundV6RegularNknEpochFromPeerControlLocked(
                    context,
                    state.TransportEpoch,
                    receivedTransportKind,
                    "receiver_state_control_proof");
            if (recoveredFromReceiverStateProof)
            {
                TryResumeOutboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
                    context,
                    "fallback_handoff_recovered");
            }

            if (state.Epoch <= context.V6LastReceiverStateEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_ignored; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}transfer_id={context.TransferId}; session_id={context.SessionId}; reason={(state.Epoch == context.V6LastReceiverStateEpoch ? "duplicate_epoch" : "stale_epoch")}; epoch={state.Epoch}; previous_epoch={context.V6LastReceiverStateEpoch}; missing_range_count={state.MissingRanges.Count}");
                if (receiverProgressChanged || recoveredFromReceiverStateProof)
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
                if (receiverAdvancedFrontier)
                {
                    TrySettleOutboundFallbackFrontierProgressLocked(
                        context,
                        previousRemoteFrontier,
                        context.RemoteNextExpectedChunkIndex,
                        "receiver_state_progress");
                }

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
                    ClearOutboundV6PostTunaFallbackSenderSelfRepairLocked(context);
                    MaybeReleaseOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
                        context,
                        previousRemoteFrontier,
                        "receiver_state_progress");
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
                var stateProvesFrontierGap =
                    !suppressPostYieldReceiverRecoveryWork &&
                    OutboundV6ReceiverStateRequestsCurrentFrontierLocked(
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
                var strictFallbackFrontierStateEvidence =
                    stateRequestsFrontierGap && IsStrictFallbackFrontierRuntime(context);
                UpdateOutboundV6RegularNknDegradedProfileLocked(
                    context,
                    state,
                    previousRemoteFrontier,
                    receiverAdvancedFrontier,
                    stateRequestsFrontierGap);
                if (suppressPostYieldReceiverRecoveryWork)
                {
                    ClearOutboundV6NormalRequestedChunksLocked(
                        context,
                        state.TransportEpoch,
                        "post_yield_receiver_state_recovery_ignored");
                }
                else if (stateRequestsFrontierGap)
                {
                    EnterOutboundV6RegularNknFrontierPressureLocked(context, state);
                    if (strictFallbackFrontierStateEvidence)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_fallback_frontier_receiver_state_evidence_only; authority_schema={FileTransferAuthoritySchema.Current}; authority_id=(none); direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; terminal_generation={context.CurrentRuntimeOwner?.TerminalGeneration ?? 0}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; transport_epoch={state.TransportEpoch}; bridge_recovery_generation={context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)")}; repair_request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}; missing_range_count={state.MissingRanges.Count}; decision_reason=receiver_state_cannot_grant_fallback_frontier_authority");
                    }
                    else
                    {
                        PreemptOutboundV6NormalPipelineForReceiverStateFrontierLocked(
                            context,
                            state,
                            stateFrontierPriorityRanges);
                    }
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
                            startupCheckpointProof ? null : state.RepairRequestId,
                            PriorityName: null,
                            startupCheckpointProof ? null : state.RecoveryMode,
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

                if (!suppressPostYieldReceiverRecoveryWork &&
                    stateRequestsFrontierGap &&
                    !strictFallbackFrontierStateEvidence &&
                    context.RemoteNextExpectedChunkIndex < context.ChunkCount)
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

                context.V6SenderPumpLastWakeReason = suppressPostYieldReceiverRecoveryWork
                    ? "post_yield_receiver_state_recovery_ignored"
                    : strictFallbackFrontierStateEvidence
                    ? "fallback_frontier_receiver_state_evidence_only"
                    : state.MissingRanges.Count > 0
                    ? "receiver_state_request"
                    : state.TransferPaused
                        ? "peer_user_paused"
                        : "receiver_state_progress";
                context.StatusMessage = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
                    ? GetV6TransportEpochStatus(context.V6TransportEpoch!)
                    : state.TransferPaused
                        ? "Peer paused transfer."
                        : suppressPostYieldReceiverRecoveryWork
                        ? "Waiting for fallback tail settlement."
                        : state.MissingRanges.Count > 0
                        ? "Sending requested V6 file data."
                        : "Waiting for V6 receiver requests.";
                if (recoveredFromReceiverStateProof)
                {
                    context.V6SenderPumpLastWakeReason = "receiver_state_control_proof";
                }

                var fallbackTerminalProofConsumed = false;
                var fallbackTailYielded =
                    !suppressPostYieldReceiverRecoveryWork &&
                    TryMarkOutboundFallbackTailQuiescentOrTerminalLocked(
                        context,
                        state,
                        stateRequestsFrontierGap,
                        "receiver_state_progress",
                        out fallbackTerminalProofConsumed);
                if (fallbackTerminalProofConsumed)
                {
                    completeFromFallbackTerminalReady = true;
                    context.State = FileTransferTransferState.AwaitingCompletion;
                    context.StatusMessage = "Waiting for receiver verification.";
                }

                snapshot = CreateSnapshotLocked();

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_received; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; committed_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; diagnostic_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; transfer_paused={(state.TransferPaused ? 1 : 0)}");
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }

        if (completeFromFallbackTerminalReady)
        {
            await TransitionOutboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Completed,
                    errorCode: null,
                    statusMessage: "Transfer complete.",
                    notifyPeer: false,
                    cancelReason: null,
                    ct: CancellationToken.None)
                .ConfigureAwait(false);
        }
        return true;
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
        bool useAuthorizedFallbackPath;
        bool fallbackRuntimeWithoutAuthority;
        lock (gate)
        {
            useAuthorizedFallbackPath =
                ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                IsStrictFallbackFrontierRuntime(context);
            fallbackRuntimeWithoutAuthority =
                ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                IsFallbackFrontierRouteRuntime(context) &&
                !useAuthorizedFallbackPath;
        }

        if (useAuthorizedFallbackPath)
        {
            ApplyOutboundAuthorizedFallbackFrontierRequest(
                context,
                request,
                receivedTransportKind);
            return;
        }

        if (fallbackRuntimeWithoutAuthority)
        {
            LogAuthorizedFallbackFrontierReceiveRejected(
                context,
                request,
                receivedTransportKind,
                "fallback_runtime_owner_or_leg_missing");
            return;
        }

        var rawFirstStartChunkIndex = request.MissingRanges.Count > 0
            ? request.MissingRanges[0].StartChunkIndex
            : -1;
        var rawFirstChunkCount = request.MissingRanges.Count > 0
            ? request.MissingRanges[0].ChunkCount
            : 0;
        if (request.MissingRanges.Count == 0)
        {
            LogOutboundV6FrontierRequestReceiveDispatch(
                context,
                request,
                receivedTransportKind,
                "attempted",
                "empty_v6_frontier_request",
                rawFirstStartChunkIndex,
                rawFirstChunkCount,
                request.MissingRanges.Count);
            LogOutboundV6FrontierRequestReceiveDispatch(
                context,
                request,
                receivedTransportKind,
                "rejected",
                "empty_v6_frontier_request",
                rawFirstStartChunkIndex,
                rawFirstChunkCount,
                request.MissingRanges.Count);
            LogPullDataFrameIgnored(context.TransferId, context.SessionId, request, "empty_v6_frontier_request");
            return;
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            LogOutboundV6FrontierRequestReceiveDispatch(
                context,
                request,
                receivedTransportKind,
                "attempted",
                "core_receive",
                rawFirstStartChunkIndex,
                rawFirstChunkCount,
                request.MissingRanges.Count);
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    context.IsTerminal ? "transfer_terminal" : "stale_outbound_context",
                    rawFirstStartChunkIndex,
                    rawFirstChunkCount,
                    request.MissingRanges.Count);
                return;
            }

            var currentFallbackLeg = context.CurrentTransferLeg;
            if (IsCurrentPostTunaFallbackLegCheckpointPending(currentFallbackLeg))
            {
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    "checkpoint_pending_frontier_request",
                    rawFirstStartChunkIndex,
                    rawFirstChunkCount,
                    request.MissingRanges.Count);
                LogFileTransferFallbackStaleProofIgnored(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    currentFallbackLeg,
                    "checkpoint_pending_frontier_request",
                    request.TransportEpoch,
                    request.RepairRequestId,
                    context.FallbackStaleProofSuppressionKeys);
                return;
            }

            if (IsCurrentPostTunaFallbackLeg(currentFallbackLeg) &&
                currentFallbackLeg!.TransportEpochId > 0 &&
                request.TransportEpoch > 0 &&
                request.TransportEpoch != currentFallbackLeg.TransportEpochId)
            {
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    "frontier_transport_epoch_mismatch",
                    rawFirstStartChunkIndex,
                    rawFirstChunkCount,
                    request.MissingRanges.Count);
                LogFileTransferFallbackStaleProofIgnored(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    currentFallbackLeg,
                    "frontier_transport_epoch_mismatch",
                    request.TransportEpoch,
                    request.RepairRequestId,
                    context.FallbackStaleProofSuppressionKeys);
                return;
            }

            context.V6LastReceiverFeedbackReceivedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.RepairRequestId) &&
                !context.V6AppliedFrontierRequestIds.Add(request.RepairRequestId.Trim()))
            {
                var duplicateRange = request.MissingRanges[0];
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    "duplicate_request_id",
                    duplicateRange.StartChunkIndex,
                    duplicateRange.ChunkCount,
                    request.MissingRanges.Count);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_duplicate_ignored; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; first_start_chunk_index={duplicateRange.StartChunkIndex}; first_chunk_count={duplicateRange.ChunkCount}");
                return;
            }

            var normalizedRanges = NormalizeV6RequestRanges(request.MissingRanges, context.ChunkCount);
            if (normalizedRanges.Count == 0)
            {
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    "empty_v6_frontier_request",
                    rawFirstStartChunkIndex,
                    rawFirstChunkCount,
                    request.MissingRanges.Count);
                LogPullDataFrameIgnored(context.TransferId, context.SessionId, request, "empty_v6_frontier_request");
                return;
            }

            var first = normalizedRanges[0];
            var isBackfillRequest = string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase);
            if (!isBackfillRequest &&
                ShouldIgnoreOutboundV6FrontierRequestLocked(context, request, normalizedRanges, out var ignoreReason))
            {
                LogOutboundV6FrontierRequestReceiveDispatch(
                    context,
                    request,
                    receivedTransportKind,
                    "rejected",
                    ignoreReason,
                    first.StartChunkIndex,
                    first.ChunkCount,
                    normalizedRanges.Count);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_ignored_obsolete; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; reason={FormatProtocolLogValue(ignoreReason)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; first_start_chunk_index={first.StartChunkIndex}; first_chunk_count={first.ChunkCount}; range_count={normalizedRanges.Count}; previous_recovered_epoch={context.LastRecoveredV6TransportEpoch}; previous_recovered_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}");
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
            MaybeReleaseOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
                context,
                previousRemoteFrontier,
                "frontier_request_progress");
            snapshot = CreateSnapshotLocked();
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_advanced_remote_frontier; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; inferred_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
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

            LogOutboundV6FrontierRequestReceiveDispatch(
                context,
                request,
                receivedTransportKind,
                "dispatched",
                "queued_for_sender_repair",
                first.StartChunkIndex,
                first.ChunkCount,
                normalizedRanges.Count);
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
                $"event=filetransfer_v6_frontier_request_received; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; first_start_chunk_index={first.StartChunkIndex}; first_chunk_count={first.ChunkCount}; range_count={request.MissingRanges.Count}");
        }

        TouchOutboundV6PeerLiveness(context, "accepted_v6_frontier_request");
        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private static void LogOutboundV6FrontierRequestReceiveDispatch(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransportKind,
        string outcome,
        string decisionReason,
        int firstStartChunkIndex,
        int firstChunkCount,
        int rangeCount,
        OutboundSparsePumpInstall? receiveInstall = null)
    {
        var eventName = outcome switch
        {
            "attempted" => "filetransfer_v6_frontier_request_receive_dispatch_attempted",
            "dispatched" => "filetransfer_v6_frontier_request_receive_dispatched",
            "staged" => "filetransfer_v6_frontier_request_receive_dispatch_staged",
            "coalesced" => "filetransfer_v6_frontier_request_receive_dispatch_coalesced",
            _ => "filetransfer_v6_frontier_request_receive_dispatch_rejected",
        };
        var leg = context.CurrentTransferLeg;
        var authorityTuple = receiveInstall is null
            ? $"direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? leg?.LiveRouteEpochId ?? 0}; leg_generation={leg?.Generation ?? 0}"
            : FormatOutboundSparsePumpInstallTuple(receiveInstall);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; {FileTransferDiagnosticScopeEvidence.Format(receiveInstall is null ? context.SessionId : receiveInstall.RuntimeOwner.SessionId, receiveInstall is null ? context.TransferId : receiveInstall.RuntimeOwner.TransferId)}{authorityTuple}; transport_epoch={request.TransportEpoch}; current_transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId ?? "(none)")}; first_start_chunk_index={firstStartChunkIndex}; first_chunk_count={firstChunkCount}; range_count={rangeCount}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; decision_reason={FormatProtocolLogValue(decisionReason)}");
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
            $"event=filetransfer_v6_frontier_request_preempted_normal_pipeline; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; normal_request_count={normalRequestCount}; in_flight_send_count={inFlightSendCount}; requested_chunk_already_in_flight={(requestedChunkAlreadyInFlight ? 1 : 0)}; forced_in_flight_preemption={(forceInFlightPreemption ? 1 : 0)}; preempted_in_flight_count={preemptedInFlightCount}; sender_pipeline_generation={pipelineGeneration}");
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
            $"event=filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; receiver_state_epoch={state.Epoch}; transport_epoch={state.TransportEpoch}; normal_request_count={normalRequestCount}; in_flight_send_count={inFlightSendCount}; requested_chunk_already_in_flight={(requestedChunkAlreadyInFlight ? 1 : 0)}; sender_pipeline_generation={pipelineGeneration}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
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
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            $"transport_epoch_queue_clear_{reason}");
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

    private static int QueueOutboundV6RequestedRangesLocked(
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
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var acceptedChunkCount = 0;
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
                    acceptedChunkCount++;
                }
                else if (!context.V6PriorityRequestedChunks.Contains(chunkIndex))
                {
                    context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = metadata;
                    context.V6NormalRequestedChunks.Add(chunkIndex);
                    acceptedChunkCount++;
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
                $"event=filetransfer_v6_frontier_resend_gate_bypassed; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_key={FormatProtocolLogValue(metadata.RequestKey)}; transport_epoch={metadata.TransportEpoch}; repair_request_id={FormatProtocolLogValue(metadata.RepairRequestId ?? "(none)")}; recovery_mode={FormatProtocolLogValue(metadata.RecoveryMode ?? "(none)")}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; bypassed_chunk_count={bypassedPriorityResendGateChunks}; sent_awaiting_ack_count={context.SentAwaitingAck.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}; in_flight_send_count={context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex)}");
        }

        return acceptedChunkCount;
    }

    private static int ResolveOutboundV6NormalSendAheadLimitChunksLocked(OutboundTransferContext context)
    {
        if (IsOutboundV6TunaNormalSendAheadPathLocked(context))
        {
            return V6TunaNormalSendAheadLimitChunks;
        }

        if (IsOutboundV6PostTunaFallbackNormalSendAheadFreezeActiveLocked(context))
        {
            return V6PostTunaFallbackNormalSendAheadFreezeChunks;
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

        if (IsOutboundV6PostTunaFallbackNormalSendAheadFreezeActiveLocked(context))
        {
            return Math.Min(
                V6RegularNknNormalRefillLowWatermarkChunks,
                Math.Max(V6PostTunaFallbackFrontierRescueExactChunks, V6PostTunaFallbackNormalSendAheadFreezeChunks / 2));
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

        var existingFallbackContract = existingMetadata.FallbackFrontierRepairContract;
        var incomingFallbackContract = incomingMetadata.FallbackFrontierRepairContract;
        if (existingFallbackContract is not null || incomingFallbackContract is not null)
        {
            if (ReferenceEquals(existingFallbackContract, incomingFallbackContract))
            {
                return true;
            }

            return incomingFallbackContract is null;
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
            FileTransferFrontierRequestFrameV6? fallbackCheckpointResponseTimeoutRequest = null;
            IFileTransferDataSession? fallbackCheckpointResponseTimeoutDataSession = null;
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

                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
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
                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                }
                else if (IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
                {
                    if (context.DataSession is { } checkpointDataSession &&
                        TryCreateOutboundFallbackCheckpointResponseTimeoutReissueLocked(
                            context,
                            "v6_sender_checkpoint_pending",
                            out var checkpointReissueRequest) &&
                        checkpointReissueRequest is not null)
                    {
                        fallbackCheckpointResponseTimeoutRequest = checkpointReissueRequest;
                        fallbackCheckpointResponseTimeoutDataSession = checkpointDataSession;
                    }
                    else
                    {
                        context.PullSenderSendWaitCountRecent++;
                        logRequestWait = context.PullSenderFeedCreditWaitStartedUtc is null ||
                            !string.Equals(context.V6SenderPumpLastWakeReason, "post_tuna_fallback_checkpoint_pending", StringComparison.Ordinal);
                        if (logRequestWait)
                        {
                            LogFileTransferFallbackLegAuthoritySendBlocked(
                                context,
                                context.CurrentTransferLeg!,
                                "post_tuna_fallback_checkpoint_pending");
                        }

                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                        priorityRequestCount = context.V6PriorityRequestedChunks.Count;
                        normalRequestCount = context.V6NormalRequestedChunks.Count;
                        requestWaitReason = "post_tuna_fallback_checkpoint_pending";
                        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_pending";
                        waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                    }
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
                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                }

            }

            if (feedbackStaleRecoveryRequest is not null)
            {
                TryRequestFileTransferReceiveRecovery(feedbackStaleRecoveryRequest);
            }

            if (fallbackCheckpointResponseTimeoutRequest is not null &&
                fallbackCheckpointResponseTimeoutDataSession is not null)
            {
                QueueOutboundV4SparseRuntimeStateRefresh(
                    context,
                    fallbackCheckpointResponseTimeoutDataSession,
                    fallbackCheckpointResponseTimeoutRequest);
                continue;
            }

            if (feedbackStaleOutboundToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(feedbackStaleOutboundToProbe).ConfigureAwait(false);
                SignalOutboundSparseSenderPump(feedbackStaleOutboundToProbe);
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

        if (TrySuppressOutboundV6RecoveredRegularNknFeedbackRecoveryLocked(
                context,
                now,
                "sender_request_feedback_stalled"))
        {
            return false;
        }

        if (normalRequestCount == 0 &&
            transportBacklogChunks < V6SenderFeedbackStaleNormalBacklogChunks &&
            inFlightSendCount == 0)
        {
            return false;
        }

        if (!TryAuthorizeOutboundV6RegularNknRecoveryMutationLocked(
                context,
                "sender_request_feedback_stalled"))
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
                FileTransferTransportKind.RegularNkn,
                coordinatorAuthorized: true);
        }

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
        var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
        context.StatusMessage = context.V6TransportEpoch is null
            ? "Waiting for V6 receiver requests."
            : GetV6TransportEpochStatus(context.V6TransportEpoch);

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_sender_feedback_stale_normal_pipeline_paused; transfer_id={context.TransferId}; session_id={context.SessionId}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; normal_request_count={normalRequestCount}; priority_request_count={priorityRequestCount}; in_flight_send_count={inFlightSendCount}; transport_backlog_chunks={transportBacklogChunks}; cleared_normal_request_count={clearedNormalRequestCount}");

        recoveryRequest = AttachFallbackLegAuthority(
            context,
            new FileTransferReceiveRecoveryRequest(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "sender_request_feedback_stalled"),
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
            else if (TrySuppressOutboundV6RecoveredRegularNknFeedbackRecoveryLocked(
                         context,
                         now,
                         "sender_request_feedback_stalled"))
            {
                return;
            }
            else
            {
                if (!TryAuthorizeOutboundV6RegularNknRecoveryMutationLocked(
                        context,
                        "sender_request_feedback_stalled"))
                {
                    return;
                }

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
                        FileTransferTransportKind.RegularNkn,
                        coordinatorAuthorized: true);
                }

                transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
                epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
                remoteFrontier = context.RemoteNextExpectedChunkIndex;
                highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
                outboundToProbe = context;
                recoveryRequest = AttachFallbackLegAuthority(
                    context,
                    new FileTransferReceiveRecoveryRequest(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Outbound,
                        "sender_request_feedback_stalled"),
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
            SignalOutboundSparseSenderPump(outboundToProbe);
        }
    }

    private static bool TryAuthorizeOutboundV6RegularNknRecoveryMutationLocked(
        OutboundTransferContext context,
        string reason)
    {
        var targetTransport = NormalizeV6TargetTransport(
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        return TryAuthorizeTransportEpochActionLocked(
            context,
            FileTransferCoordinatorActionKind.TransportEpochAdoption,
            TransportEpochActionKind.Adopt,
            "v6_transport_epoch_start",
            reason,
            context.CurrentTransferLeg,
            null,
            context.CurrentTransferLeg?.BridgeRecoveryGeneration,
            null,
            context.RouteSelection.Route,
            context.NegotiatedDataProtocolVersion,
            targetTransport,
            out _);
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
        if (IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            return false;
        }

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
            if (metadata.FallbackFrontierRepairContract is null &&
                IsOutboundV6OrdinaryDataBlockedByFallbackLegLocked(context))
            {
                return false;
            }
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

    private static bool IsOutboundV6OrdinaryDataBlockedByFallbackLegLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           (!IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) ||
            !context.CurrentTransferLeg!.CanSendData ||
            context.CurrentRuntimeOwner is not { } owner ||
            !FileTransferCoordinator.IsRuntimeOwnerCurrent(CreateCoordinatorStateLocked(context), owner, out _));

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
            var fallbackContract = prepared.Metadata.FallbackFrontierRepairContract;
            if (!ReferenceEquals(outboundTransfer, context))
            {
                reason = "stale_outbound_context";
                TrySettleFallbackFrontierRepairContract(
                    fallbackContract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    reason);
            }
            else if (context.IsTerminal)
            {
                reason = "transfer_terminal";
                TrySettleFallbackFrontierRepairContract(
                    fallbackContract,
                    FallbackFrontierRepairSettlementOutcome.TerminalRejected,
                    reason);
            }
            else
            {
                currentPipelineGeneration = context.V6SenderPipelineGeneration;
                remoteFrontier = context.RemoteNextExpectedChunkIndex;
                var epoch = context.V6TransportEpoch;
                if (epoch is not null)
                {
                    currentTransportEpoch = epoch.EpochId;
                    currentEpochState = FormatV6TransportEpochState(epoch.State);
                }

                if (fallbackContract is not null &&
                    !TryValidateOutboundFallbackFrontierDispatchLocked(
                        context,
                        fallbackContract,
                        out var authorityRejectionReason))
                {
                    reason = authorityRejectionReason;
                    if (!fallbackContract.Lifecycle.IsSettled)
                    {
                        TrySettleFallbackFrontierRepairContract(
                            fallbackContract,
                            FallbackFrontierRepairSettlementOutcome.StaleRejected,
                            authorityRejectionReason);
                    }
                }
                else if (fallbackContract is null && IsOutboundV6OrdinaryDataBlockedByFallbackLegLocked(context))
                {
                    reason = "fallback_leg_data_permission_closed";
                }
                else if (prepared.SenderPipelineGeneration != currentPipelineGeneration)
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

                if (reason is not null &&
                    fallbackContract is not null &&
                    !fallbackContract.Lifecycle.IsSettled)
                {
                    var outcome = reason is "obsolete_remote_frontier" or "partially_obsolete_remote_frontier"
                        ? FallbackFrontierRepairSettlementOutcome.ProgressObserved
                        : reason.StartsWith("stale_", StringComparison.Ordinal)
                            ? FallbackFrontierRepairSettlementOutcome.StaleRejected
                            : fallbackContract.Lifecycle.DispatchObserved
                                ? FallbackFrontierRepairSettlementOutcome.SettledStalled
                                : FallbackFrontierRepairSettlementOutcome.MissingDispatch;
                    TrySettleFallbackFrontierRepairContract(
                        fallbackContract,
                        outcome,
                        $"prepared_batch_dropped_{reason}");
                }
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
                TrySettleFallbackFrontierRepairContract(
                    metadata.FallbackFrontierRepairContract,
                    context.IsTerminal
                        ? FallbackFrontierRepairSettlementOutcome.TerminalRejected
                        : FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    context.IsTerminal ? "prepare_after_terminal" : "prepare_for_stale_or_paused_context");
                return null;
            }

            if (metadata.FallbackFrontierRepairContract is { } fallbackContract &&
                !TryValidateOutboundFallbackFrontierDispatchLocked(
                    context,
                    fallbackContract,
                    out var authorityRejectionReason))
            {
                if (!fallbackContract.Lifecycle.IsSettled)
                {
                    TrySettleFallbackFrontierRepairContract(
                        fallbackContract,
                        FallbackFrontierRepairSettlementOutcome.StaleRejected,
                        authorityRejectionReason);
                }

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
            var fallbackDispatchAccepted = true;
            var fallbackFirstDispatchObserved = false;
            lock (gate)
            {
                var fallbackContract = prepared.Metadata.FallbackFrontierRepairContract;
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    fallbackDispatchAccepted = fallbackContract is null;
                    TrySettleFallbackFrontierRepairContract(
                        fallbackContract,
                        context.IsTerminal
                            ? FallbackFrontierRepairSettlementOutcome.TerminalRejected
                            : FallbackFrontierRepairSettlementOutcome.StaleRejected,
                        context.IsTerminal
                            ? "send_completion_after_terminal"
                            : "send_completion_for_stale_context");
                }
                else if (fallbackContract is not null &&
                         !TryValidateOutboundFallbackFrontierDispatchLocked(
                             context,
                             fallbackContract,
                             out var authorityRejectionReason))
                {
                    fallbackDispatchAccepted = false;
                    if (!fallbackContract.Lifecycle.IsSettled)
                    {
                        TrySettleFallbackFrontierRepairContract(
                            fallbackContract,
                            FallbackFrontierRepairSettlementOutcome.StaleRejected,
                            authorityRejectionReason);
                    }
                }
                else
                {
                    if (fallbackContract is not null)
                    {
                        fallbackFirstDispatchObserved =
                            TryMarkFallbackFrontierRepairDispatched(fallbackContract);
                    }

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

            if (!fallbackDispatchAccepted)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_fallback_frontier_repair_late_send_completion_ignored; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(prepared.Metadata.FallbackFrontierRepairContract?.Authority.AuthorityId.ToString() ?? "(none)")}; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}; decision_reason=authority_not_current_after_send");
                return;
            }

            if (prepared.Metadata.FallbackFrontierRepairContract is { } dispatchedContract)
            {
                var protocolRepairRequestId =
                    prepared.Metadata.RepairRequestId ??
                    dispatchedContract.Authority.ProtocolRepairRequestId;
                if (fallbackFirstDispatchObserved)
                {
                    LogFallbackFrontierRepairDispatched(
                        dispatchedContract,
                        protocolRepairRequestId,
                        "native_v6_send",
                        prepared.SegmentCount,
                        context.RemoteNextExpectedChunkIndex,
                        context.ChunksAcceptedForTransport);
                }
                else
                {
                    LogFallbackFrontierRepairRetryDispatched(
                        dispatchedContract,
                        protocolRepairRequestId,
                        "native_v6_send",
                        prepared.SegmentCount,
                        context.RemoteNextExpectedChunkIndex,
                        context.ChunksAcceptedForTransport);
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
                SignalOutboundSparseSenderPump(context);
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

            var canRequeueNormal = !prepared.Metadata.Priority &&
                !unresolvedEpoch &&
                string.Equals(context.V6CurrentNormalRequestKey, prepared.Metadata.RequestKey, StringComparison.Ordinal);
            var canRequeuePriority = prepared.Metadata.Priority;
            var requeueMetadata = prepared.Metadata;
            if ((canRequeuePriority || canRequeueNormal) &&
                prepared.Metadata.FallbackFrontierRepairContract is { } predecessorContract)
            {
                if (!TryIssueOutboundFallbackFrontierRetrySuccessorLocked(
                        context,
                        predecessorContract,
                        FallbackFrontierRepairSourceKind.SenderLivenessRescue,
                        "fallback_frontier_recoverable_send_failure_successor",
                        out var successorContract) ||
                    successorContract is null)
                {
                    return true;
                }

                requeueMetadata = prepared.Metadata with
                {
                    RequestKey = successorContract.Authority.CanonicalRepairKey,
                    TransportEpoch = successorContract.Authority.TransportEpoch,
                    RepairRequestId = successorContract.Authority.ProtocolRepairRequestId,
                    FallbackFrontierRepairContract = successorContract,
                };
            }

            context.PullSenderPipelineFailedFramesRecent++;
            context.V6SenderPumpLastWakeReason = pullTransportPaused
                ? "transport_paused"
                : "transport_send_deferred_for_recovery";

            if (canRequeuePriority || canRequeueNormal)
            {
                for (var offset = 0; offset < prepared.SegmentCount; offset++)
                {
                    var chunkIndex = prepared.StartChunkIndex + offset;
                    if (chunkIndex < context.RemoteNextExpectedChunkIndex || chunkIndex >= context.ChunkCount)
                    {
                        continue;
                    }

                    context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = requeueMetadata;
                    if (requeueMetadata.Priority)
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

        SignalOutboundSparseSenderPump(context);
        return true;
    }

    private static bool IsRecoverableV6DataSessionSendFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }

            if (current is ObjectDisposedException)
            {
                return true;
            }

            if (IsRecoverableV6DataSessionSendFailureMessage(current.Message))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (!ReferenceEquals(inner, current) &&
                        IsRecoverableV6DataSessionSendFailure(inner))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsRecoverableV6DataSessionSendFailureMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("Bridge disconnected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("NKN bridge is not running", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("NKN bridge process is not available", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Not connected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("client not ready", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("data session is not available", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("data session is unavailable", StringComparison.OrdinalIgnoreCase));
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

        var requeueMetadata = prepared.Metadata;
        FallbackFrontierRepairContract? retrySuccessor = null;
        if (prepared.Metadata.FallbackFrontierRepairContract is { } predecessorContract)
        {
            if (!TryIssueOutboundFallbackFrontierRetrySuccessorLocked(
                    context,
                    predecessorContract,
                    FallbackFrontierRepairSourceKind.SenderTimeoutRetry,
                    "fallback_frontier_send_timeout_successor",
                    out retrySuccessor) ||
                retrySuccessor is null)
            {
                reason = "fallback_frontier_successor_rejected";
                return 0;
            }

            requeueMetadata = prepared.Metadata with
            {
                RequestKey = retrySuccessor.Authority.CanonicalRepairKey,
                TransportEpoch = retrySuccessor.Authority.TransportEpoch,
                RepairRequestId = retrySuccessor.Authority.ProtocolRepairRequestId,
                FallbackFrontierRepairContract = retrySuccessor,
            };
        }

        var requeuedChunkCount = 0;
        for (var offset = 0; offset < prepared.SegmentCount; offset++)
        {
            var chunkIndex = prepared.StartChunkIndex + offset;
            if (chunkIndex < context.RemoteNextExpectedChunkIndex || chunkIndex >= context.ChunkCount)
            {
                continue;
            }

            if (IsOutboundV6ChunkBlockedByTransportEpochLocked(context, chunkIndex, requeueMetadata))
            {
                continue;
            }

            context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = requeueMetadata;
            if (requeueMetadata.Priority)
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
        if (requeuedChunkCount == 0 && retrySuccessor is not null)
        {
            TrySettleFallbackFrontierRepairContract(
                retrySuccessor,
                FallbackFrontierRepairSettlementOutcome.MissingDispatch,
                "fallback_frontier_timeout_successor_had_no_uncommitted_chunks");
        }

        reason = requeuedChunkCount > 0
            ? retrySuccessor is null ? "requeued" : "authorized_successor_requeued"
            : "no_uncommitted_chunks";
        return requeuedChunkCount;
    }

    private int RequeuePostTunaFallbackTimeoutFrontierRepairLocked(
        OutboundTransferContext context,
        PreparedV6ChunkBatch prepared,
        out string reason)
    {
        reason = "not_post_tuna_fallback";
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !IsStrictFallbackFrontierRuntime(context))
        {
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

        var frontierChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount - 1));
        if (frontierChunkIndex >= context.ChunkCount)
        {
            reason = "frontier_complete";
            return 0;
        }

        if (!TryGetOutboundFallbackFrontierPeerRepairLocked(context, out var fallbackContract, out var authorityRejectionReason) ||
            fallbackContract is null)
        {
            reason = $"fallback_frontier_authority_rejected_{authorityRejectionReason}";
            return 0;
        }

        var clearedInFlightCount = 0;
        for (var offset = 0; offset < prepared.SegmentCount; offset++)
        {
            if (context.V6ChunkSendsInFlight.Remove(prepared.StartChunkIndex + offset))
            {
                clearedInFlightCount++;
            }
        }

        if (context.V6ChunkSendsInFlight.Remove(frontierChunkIndex))
        {
            clearedInFlightCount++;
        }

        var metadata = new V6OutboundChunkRequestMetadata(
            fallbackContract.Authority.CanonicalRepairKey,
            Priority: true,
            TransportEpoch: fallbackContract.Authority.TransportEpoch,
            RepairRequestId: fallbackContract.Authority.ProtocolRepairRequestId,
            PriorityName: fallbackContract.Authority.Priority,
            RecoveryMode: fallbackContract.Authority.RecoveryMode,
            ForceRegularNknBulk: true,
            FallbackFrontierRepairContract: fallbackContract);

        context.V6RequestedChunkMetadataByChunkIndex[frontierChunkIndex] = metadata;
        context.V6NormalRequestedChunks.Remove(frontierChunkIndex);
        context.V6PriorityRequestedChunks.Add(frontierChunkIndex);
        context.V6PostTunaFallbackSenderSelfRepairChunkIndex = frontierChunkIndex;
        context.V6PostTunaFallbackSenderSelfRepairUtc = DateTimeOffset.UtcNow;
        ActivateOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(context, "send_timeout_exact_frontier_repair");
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_send_timeout_frontier_repair";
        reason = $"exact_frontier_requeued_cleared_in_flight_{clearedInFlightCount}";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued; {FormatFallbackFrontierRepairAuthorityTuple(fallbackContract.Authority)}; timed_out_start_chunk_index={prepared.StartChunkIndex}; timed_out_batch_chunk_count={prepared.SegmentCount}; requeued_chunk_count=1; cleared_in_flight_chunk_count={clearedInFlightCount}; repair_request_id={FormatProtocolLogValue(fallbackContract.Authority.ProtocolRepairRequestId)}");
        return 1;
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
        Task sendTask;
        Task physicalCompletionTask;
        OutboundPhysicalSendLease? physicalSendLease;
        lock (gate)
        {
            var repairSend =
                prepared.Metadata.Priority ||
                prepared.Metadata.FallbackFrontierRepairContract is not null;
            if (!TryRegisterOutboundPhysicalSendLeaseLocked(
                    context,
                    dataSession,
                    prepared.StartChunkIndex,
                    prepared.SegmentCount,
                    repairSend,
                    prepared.Metadata.RepairRequestId ?? prepared.Metadata.RequestKey,
                    out physicalSendLease,
                    out _))
            {
                return PreparedV6ChunkBatchSendOutcome.NotSent;
            }

            try
            {
                if (dataSession is IFileTransferPhysicalSendSubmissionSession physicalSubmissionSession)
                {
                    var submission = physicalSubmissionSession.SubmitPhysicalSend(
                        prepared.Batch,
                        authority: null,
                        sendCts.Token);
                    sendTask = submission.DeliveryTask;
                    physicalCompletionTask = submission.PhysicalCompletionTask;
                }
                else
                {
                    sendTask = dataSession.SendAsync(prepared.Batch, sendCts.Token);
                    physicalCompletionTask = sendTask;
                }
            }
            catch (Exception ex)
            {
                sendTask = Task.FromException(ex);
                physicalCompletionTask = sendTask;
            }

            if (physicalSendLease is not null)
            {
                physicalSendLease = BindOutboundPhysicalSendLeaseCompletionLocked(
                    context,
                    physicalSendLease,
                    physicalCompletionTask);
            }
        }

        if (physicalSendLease is not null)
        {
            _ = ObserveOutboundPhysicalSendLeaseAsync(
                context,
                physicalSendLease,
                physicalCompletionTask);
            _ = ObserveOutboundPhysicalSendFailureAsync(
                context,
                physicalSendLease,
                sendTask);
        }

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
        var exactFrontierRequeuedChunkCount = 0;
        var requeueReason = "not_attempted";
        var exactFrontierRequeueReason = "not_attempted";
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
                if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
                    prepared.Metadata.FallbackFrontierRepairContract is null)
                {
                    exactFrontierRequeuedChunkCount = RequeuePostTunaFallbackTimeoutFrontierRepairLocked(
                        context,
                        prepared,
                        out exactFrontierRequeueReason);
                }
            }
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_send_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; raw_bytes={prepared.RawBytes}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; timeout_ms={(long)timeout.TotalMilliseconds}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_send_timeout_requeue; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; requeued_chunk_count={requeuedChunkCount}; reason={FormatProtocolLogValue(requeueReason)}; request_key={FormatProtocolLogValue(prepared.Metadata.RequestKey)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; regular_nkn_primary={(prepared.UseRegularNknPrimaryDelivery ? 1 : 0)}; regular_nkn_redundant={(prepared.UseRegularNknRedundantDelivery ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
        if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_send_timeout_requeued; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; start_chunk_index={prepared.StartChunkIndex}; batch_chunk_count={prepared.SegmentCount}; requeued_chunk_count={requeuedChunkCount}; exact_frontier_requeued_chunk_count={exactFrontierRequeuedChunkCount}; reason={FormatProtocolLogValue(requeueReason)}; exact_frontier_reason={FormatProtocolLogValue(exactFrontierRequeueReason)}; priority={(prepared.Metadata.Priority ? 1 : 0)}; transport_epoch={prepared.Metadata.TransportEpoch}; repair_request_id={FormatProtocolLogValue(prepared.Metadata.RepairRequestId ?? "(none)")}");
        }

        _ = ObserveTimedOutV6ChunkBatchSendAsync(context.TransferId, context.SessionId, prepared, sendTask);
        return requeuedChunkCount > 0 || exactFrontierRequeuedChunkCount > 0
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
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                TryPublishRuntimeStartedIfAuthorizedLocked(
                    context,
                    "receiver",
                    "v6_receiver_start",
                    "runtime_started",
                    routePublicationToken: context.CurrentRoutePublicationToken);
            }
        }

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
            InboundBootstrapWriteIngress? writeIngress = null;
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                if (pendingReceiveTask is null)
                {
                    lock (gate) { writeIngress = CaptureInboundBootstrapWriteIngress(context, dataSession); }
                    pendingReceiveTask = dataSession.ReceiveWithMetadataAsync(context.LifetimeCts.Token).AsTask();
                }
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

                if (await TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Inbound,
                        frame,
                        received.TransportKind,
                        dataSession).ConfigureAwait(false))
                {
                    continue;
                }

                if (TryHandleBootstrapCorrelationFrame(FileTransferDirection.Inbound, dataSession, received)) { continue; }

                if (!FileTransferProtocol.IsV6DataFrame(frame))
                {
                    SessionFileTransferSnapshot? legacyProofSnapshot = null;
                    if (frame is FileTransferChunkBatchFrameV4 legacyBatch)
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(inboundTransfer, context) &&
                                !context.IsTerminal &&
                                TryRecoverInboundV6RegularNknEpochFromLegacyV4ChunkProofLocked(
                                    context,
                                    legacyBatch,
                                    received.TransportKind,
                                    "regular_nkn_legacy_v4_chunk_probe"))
                            {
                                legacyProofSnapshot = CreateSnapshotLocked();
                            }
                        }
                    }

                    if (legacyProofSnapshot is not null)
                    {
                        RaiseTransferChanged(legacyProofSnapshot);
                        TouchInboundV6PeerLiveness(context, "regular_nkn_legacy_v4_chunk_probe");
                        await SendInboundV6ReceiverStateAsync(
                            context,
                            "regular_nkn_legacy_v4_chunk_probe",
                            forceSend: true).ConfigureAwait(false);
                        await SendInboundV6FrontierRequestAsync(
                            context,
                            "regular_nkn_legacy_v4_chunk_probe",
                            forceSend: true).ConfigureAwait(false);
                    }

                    LogInboundV4FrameIgnored(context, frame, "protocol_not_v6");
                    continue;
                }

                if (frame is not FileTransferChunkBatchFrameV6 and not FileTransferFrontierRequestFrameV6)
                {
                    TouchInboundV6PeerLivenessIfAuthoritative(context, received, "v6_data_frame");
                }
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
                        await HandleInboundV6ChunkBatchFromReceiveAsync(context, batch, received.TransportKind, writeIngress).ConfigureAwait(false);
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
                    case FileTransferFallbackTailYieldFrameV6 yield:
                        ApplyInboundFallbackTailYieldProofWithEvidence(context, yield, received.YieldEvidence);
                        break;
                    case FileTransferFrontierRequestFrameV6 checkpoint when
                        context.RouteRuntime.UsesPostTunaFallbackV6Runtime && IsV6RegularNknStateRefreshRequest(checkpoint):
                    {
                        bool sendCheckpointState;
                        lock (gate)
                        {
                            sendCheckpointState = ReferenceEquals(inboundTransfer, context) && !context.IsTerminal &&
                                context.PullManifestReceived && ReferenceEquals(context.DataSession, dataSession) &&
                                context.CurrentTransferLeg?.TransportEpochId == checkpoint.TransportEpoch &&
                                MarkInboundFallbackCheckpointRequestedLocked(context, checkpoint, "v6_startup_checkpoint_received");
                        }
                        if (sendCheckpointState)
                        {
                            TouchInboundV6PeerLivenessIfAuthoritative(context, received, "v6_checkpoint_request");
                            await SendInboundV6ReceiverStateAsync(context, V6RegularNknStateRefreshRecoveryMode,
                                terminalReady: false, forceSend: true).ConfigureAwait(false);
                        }
                        break;
                    }
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
                        if (await TryHandleInboundLifecycleErrorDataFrameAsync(context, error).ConfigureAwait(false))
                        {
                            return;
                        }

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
                context.BootstrapPendingWriteOwners.Clear();
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
                ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
                ResetInboundV6FrontierStallGraceLocked(context);
                ResetInboundV6PostTunaFallbackFrontierRescueLocked(context);
                if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                {
                    StartInboundPostTunaRecoveryLocked(context, "post_tuna_fallback_v6_route");
                }

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
        LogV6PostTunaFallbackSurvivalPolicyEnabled(context, FileTransferDirection.Inbound);
        return true;
    }

    private Task HandleInboundV6ChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind receivedTransportKind)
    {
        InboundBootstrapWriteIngress? ingress;
        lock (gate) { ingress = CaptureInboundBootstrapWriteIngress(context, context.DataSession); }
        return HandleInboundV6ChunkBatchFromReceiveAsync(context, batch, receivedTransportKind, ingress);
    }

    private async Task HandleInboundV6ChunkBatchFromReceiveAsync(InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch, FileTransferTransportKind receivedTransportKind, InboundBootstrapWriteIngress? ingress)
    {
        InboundBootstrapWriteSource? source = null;
        lock (gate)
        {
            if ((context.RouteRuntime.UsesPostTunaFallbackV6Runtime && context.ReceiverSparseChunksWritten is not null ||
                 ingress?.FallbackRepairSource is not null) &&
                !TryCaptureInboundFallbackRepairWriteSourceLocked(context, ingress, out source)) { return; }
        }
        try { await HandleInboundV6ChunkBatchCoreAsync(context, batch, receivedTransportKind, source).ConfigureAwait(false); }
        finally { await SettleInboundTrackedWriteAsync(context, source).ConfigureAwait(false); }
    }

    private async Task HandleInboundV6ChunkBatchCoreAsync(
        InboundTransferContext context, FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind receivedTransportKind, InboundBootstrapWriteSource? writeSource)
    {
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks;
        string? failureMessage;
        bool valid;
        lock (gate)
        {
            if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
            valid = TryValidateInboundV4ChunkBatch(context, batch, out chunks, out failureMessage);
        }
        if (!valid)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; error_code={InvalidStateErrorCode}; reason=invalid_chunk_batch; message={FormatProtocolLogValue(failureMessage)}");
            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                failureMessage ?? "V6 chunk batch was invalid.",
                failureMessage ?? "V6 chunk batch was invalid.", writeSource).ConfigureAwait(false);
            return;
        }

        V6ReceiveDestinationMode mode;
        Stream? writeStream;
        FallbackFrontierRepairContract? fallbackFrontierContract = null;
        SessionFileTransferSnapshot? epochProofSnapshot = null;
        lock (gate)
        {
            if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
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

            if (!TryGetInboundFallbackFrontierBatchContractLocked(
                    context,
                    batch,
                    out fallbackFrontierContract,
                    out var fallbackRejectionReason))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_fallback_frontier_repair_batch_rejected; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(context.CurrentFallbackFrontierRepairContract?.Authority.AuthorityId.ToString() ?? "(none)")}; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={batch.TransportEpoch}; repair_request_id={FormatProtocolLogValue(batch.RepairRequestId ?? "(none)")}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; decision_reason={FormatProtocolLogValue(fallbackRejectionReason)}");
                return;
            }

            if (TryRecoverInboundV6RegularNknEpochFromChunkProofLocked(
                context,
                batch,
                receivedTransportKind,
                chunks.Count,
                "regular_nkn_chunk_probe"))
            {
                epochProofSnapshot = CreateSnapshotLocked();
            }

            mode = context.V6DestinationMode;
            writeStream = context.WriteStream;
        }

        if (epochProofSnapshot is not null)
        {
            RaiseTransferChanged(epochProofSnapshot);
        }

        lock (gate)
        {
            if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
            TouchInboundV6PeerLiveness(context,
                fallbackFrontierContract is null ? "accepted_v6_chunk_batch" : "authorized_fallback_frontier_batch");
        }

        if (mode == V6ReceiveDestinationMode.SparseSeekable)
        {
            await HandleInboundV6SparseChunkBatchWithWriteBindingAsync(
                context,
                batch,
                chunks,
                writeStream!,
                receivedTransportKind,
                fallbackFrontierContract, writeSource).ConfigureAwait(false);
            return;
        }

        await HandleInboundV6ContiguousChunkBatchAsync(
            context,
            batch,
            chunks,
            writeStream!,
            receivedTransportKind,
            fallbackFrontierContract).ConfigureAwait(false);
    }

    private Task HandleInboundV6SparseChunkBatchAsync(InboundTransferContext context, FileTransferChunkBatchFrameV6 batch,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks, Stream writeStream,
        FileTransferTransportKind receivedTransportKind, FallbackFrontierRepairContract? fallbackFrontierContract)
        => HandleInboundV6SparseChunkBatchWithWriteBindingAsync(context, batch, chunks, writeStream,
            receivedTransportKind, fallbackFrontierContract, null);

    private async Task HandleInboundV6SparseChunkBatchWithWriteBindingAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks,
        Stream writeStream,
        FileTransferTransportKind receivedTransportKind,
        FallbackFrontierRepairContract? fallbackFrontierContract,
        InboundBootstrapWriteSource? writeSource = null)
    {
        var acceptedChunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(chunks.Count);
        lock (gate)
        {
            if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
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
            if (writeSource is not null && acceptedChunks.Count != 0) { RegisterInboundBootstrapWriteLocked(context, writeSource, acceptedChunks); }
        }

        if (acceptedChunks.Count == 0)
        {
            await MaybeSendInboundV6RetryStateAsync(context).ConfigureAwait(false);
            return;
        }

        TryAdmitFrontierRepairObservation(context, fallbackFrontierContract, acceptedChunks);

        var writeStopwatch = Stopwatch.StartNew();
        long writtenBytes = 0;
        var writeGateEntered = false;
        try
        {
            await context.ReceiverSparseWriteGate.WaitAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            writeGateEntered = true;
            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                if (writeSource is not null)
                {
                    ValueTask physicalWrite;
                    lock (gate)
                    {
                        if (!IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource) ||
                            !OwnsInboundBootstrapWriteChunks(context, writeSource.Operation!)) { return; }
                        writeSource.Stream.Seek((long)chunkIndex * writeSource.ChunkSize, SeekOrigin.Begin);
                        physicalWrite = writeSource.Stream.WriteAsync(chunkBytes, context.LifetimeCts.Token);
                    }
                    await physicalWrite.ConfigureAwait(false);
                }
                else
                {
                    writeStream.Seek((long)chunkIndex * Math.Max(1, context.ChunkSizeBytes), SeekOrigin.Begin);
                    await writeStream.WriteAsync(chunkBytes, context.LifetimeCts.Token).ConfigureAwait(false);
                }
                writtenBytes += chunkBytes.Length;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailInboundV4Async(
                context,
                StreamWriteFailedErrorCode,
                ex.Message,
                "Could not write a V6 sparse receiver chunk.", writeSource).ConfigureAwait(false);
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
        if (writeSource is not null && BootstrapWriteBeforeCommitForTests is { } writeCommitBarrier)
        {
            await writeCommitBarrier(context.TransferId).ConfigureAwait(false);
        }
        int committedChunkCount;
        long committedByteCount;
        bool completed;
        SessionFileTransferSnapshot? snapshot = null;
        int nextChunkIndexAfterCommit;
        int highestReceivedChunkIndexAfterCommit;
        int previousCommittedChunkIndex;
        bool frontierStallGraceWakeRefreshed;
        var postTunaFallbackRepairProgressObserved = false;
        string? postTunaFallbackRepairProgressKey = null;
        int postTunaFallbackRepairProgressBefore = 0;
        int postTunaFallbackRepairProgressAfter = 0;
        int postTunaFallbackRepairProgressRequestedCount = 0;
        InboundFallbackFrontierProgressReceipt? fallbackFrontierProgressReceipt = null;
        InboundFallbackV6ProofBootstrapPendingWriteWake?
            fallbackV6ProofBootstrapObservedWake = null;
        InboundFallbackV6ProofBootstrapPendingWriteWake?
            fallbackV6ProofBootstrapRecheck = null;
        string? fallbackV6ProofBootstrapRecheckStatus = null;
        string? fallbackV6ProofBootstrapRecheckResult = null;
        string? recentProgressCommitEvidence = null;
        lock (gate)
        {
            if (writeSource is not null && (!IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource) ||
                !OwnsInboundBootstrapWriteChunks(context, writeSource.Operation!))) { return; }
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.ReceiverSparseChunksWritten is null)
            {
                return;
            }

            InboundV6FrontierRecentProgressGraceWake? recentProgressWake = null;
            previousCommittedChunkIndex = context.NextChunkIndex;
            foreach (var (chunkIndex, chunkBytes) in acceptedChunks)
            {
                if (!context.ReceiverSparseChunksPendingWrite.Remove(chunkIndex))
                {
                    continue;
                }

                // The physical write is complete. Validate the original wake
                // after removing pending presence, before committing the frontier.
                if (chunkIndex == previousCommittedChunkIndex &&
                    context.CurrentV6FrontierRecentProgressGraceWake is { } currentWake &&
                    IsInboundV6FrontierRecentProgressGraceWakeCurrentLocked(context, currentWake, out _))
                {
                    recentProgressWake = currentWake;
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
            context.FrontierPublicationObservation?.Commit(CreateCoordinatorStateLocked(context), context.DataSession,
                previousCommittedChunkIndex, context.NextChunkIndex, committedChunkCount,
                context.PullHighestReceivedChunkIndex, context.BytesTransferred);
            context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);
            var progressUtc = ReadPublicationBoundaryUtc(context);
            context.PullLastProgressUtc = progressUtc;
            context.FrontierBoundaryObservation?.Write(context, previousCommittedChunkIndex, committedChunkCount, "v6_sparse");
            if (committedChunkCount > 0)
            {
                if (recentProgressWake is { } wake)
                {
                    recentProgressCommitEvidence =
                        $"; commit_decision_utc={FormatProtocolLogValue(progressUtc.ToString("O"))}; commit_source_status=current; authority_effect=none; {FileTransferDiagnosticScopeEvidence.FormatDataSession(wake.DataSessionIdentity)}direction=inbound; route={FormatProtocolLogValue(wake.RouteToken)}; protocol_version={wake.ProtocolVersion}; live_route_epoch={wake.LiveRouteEpoch}; runtime_generation={wake.RuntimeGeneration}; terminal_generation={wake.TerminalGeneration}; leg_generation={wake.LegGeneration}; transport_epoch={wake.TransportEpoch}; bridge_recovery_generation={wake.BridgeRecoveryGeneration}; bridge_decision_generation={wake.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(wake.CheckpointRequestId)}; checkpoint_generation={wake.CheckpointGeneration}; route_publication_generation={wake.RoutePublicationGeneration}; route_commit_generation={wake.RouteCommitGeneration}; data_session_identity={FormatProtocolLogValue(wake.DataSessionIdentity)}; data_session_generation={wake.DataSessionGeneration}; frontier_chunk_index={wake.FrontierChunkIndex}; wake_generation={wake.WakeGeneration}; committed_progress_utc={FormatProtocolLogValue(wake.CommittedProgressUtc.ToString("O"))}; wake_deadline_utc={FormatProtocolLogValue(wake.DueUtc.ToString("O"))}";
                }
                context.PullLastCommittedProgressUtc = progressUtc;
                fallbackFrontierProgressReceipt = TrySettleInboundFallbackFrontierProgressLocked(
                    context,
                    fallbackFrontierContract,
                    batch.RepairRequestId,
                    batch.TransportEpoch,
                    receivedTransportKind,
                    previousCommittedChunkIndex,
                    context.NextChunkIndex,
                    "chunk_batch_committed");
                ResetInboundV6PostTunaFallbackFrontierRescueLocked(context);
                ClearInboundV6ReceiverStateDeferredDiagnosticLocked(context);
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
            if (committedChunkCount > 0)
            {
                var tailTransportEpoch = batch.TransportEpoch > 0
                    ? batch.TransportEpoch
                    : context.CurrentTransferLeg?.TransportEpochId ?? 0;
                var replayPending =
                    context.V6PostTunaFallbackProofReplayReceiverStatePending ||
                    context.V6PostTunaFallbackProofReplayFrontierRequestPending;
                postTunaFallbackRepairProgressObserved =
                    IsInboundV6PostTunaFallbackSurvivalPathLocked(context) &&
                    IsInboundPostTunaRecoveryActiveLocked(context) &&
                    (replayPending ||
                     context.PullPostTunaRecoveryActive ||
                     context.PullTransportRebindGeneration > 0) &&
                    context.NextChunkIndex > previousCommittedChunkIndex;
                if (postTunaFallbackRepairProgressObserved)
                {
                    postTunaFallbackRepairProgressKey =
                        batch.RepairRequestId ??
                        batch.BatchId ??
                        $"v6-fallback-progress:{batch.StartChunkIndex}:{batch.ChunkCount}";
                    postTunaFallbackRepairProgressBefore = previousCommittedChunkIndex;
                    postTunaFallbackRepairProgressAfter = context.NextChunkIndex;
                    postTunaFallbackRepairProgressRequestedCount = acceptedChunks.Count;
                }

                TryMarkInboundFallbackTailQuiescentLocked(
                    context,
                    tailTransportEpoch,
                    "chunk_batch_committed");
            }

            TryRecheckHeldInboundRuntimeUnlockResponderPeerProbeRouteTransitionAfterOwnerWorkClearedLocked(
                context,
                "receiver_sparse_pending_write_cleared");

            if (context.CurrentFallbackV6ProofBootstrapPendingWriteWake is not null &&
                context.ReceiverSparseChunksPendingWrite.Count == 0 &&
                context.PendingChunks.Count == 0)
            {
                fallbackV6ProofBootstrapRecheck =
                    TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked(
                        context,
                        out fallbackV6ProofBootstrapObservedWake,
                        out fallbackV6ProofBootstrapRecheckStatus,
                        out fallbackV6ProofBootstrapRecheckResult);
            }

            ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked(context, DateTimeOffset.UtcNow);
            frontierStallGraceWakeRefreshed = RefreshInboundV6FrontierStallGraceWakeForHighestProgressLocked(context);

            snapshot = CreateSnapshotLocked();
        }

        if (fallbackV6ProofBootstrapObservedWake is not null)
        {
            LogInboundFallbackV6ProofBootstrapPendingWriteRecheckDecision(
                fallbackV6ProofBootstrapObservedWake,
                fallbackV6ProofBootstrapRecheckStatus ?? "Stale",
                fallbackV6ProofBootstrapRecheckResult ?? "source_not_current");
        }

        if (fallbackV6ProofBootstrapRecheck is not null)
        {
            _ = ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync(
                fallbackV6ProofBootstrapRecheck);
        }

        SchedulePendingInboundV6FrontierRecentProgressGraceWake(context);
        if (frontierStallGraceWakeRefreshed)
        {
            SchedulePendingInboundV6FrontierStallGraceWake(context);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_chunk_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; mode=sparse_seekable; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunks.Count}; raw_bytes={chunks.Sum(static item => item.ChunkBytes.Length)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_sparse_write_committed; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}transfer_id={context.TransferId}; session_id={context.SessionId}; written_chunk_count={acceptedChunks.Count}; written_bytes={writtenBytes}; contiguous_chunks_committed={committedChunkCount}; contiguous_bytes_committed={committedByteCount}; write_duration_ms={writeStopwatch.ElapsedMilliseconds}; next_chunk_index={nextChunkIndexAfterCommit}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_bytes={context.BufferedBytes}{recentProgressCommitEvidence}");
        if (postTunaFallbackRepairProgressObserved &&
            !(IsStrictFallbackFrontierRuntime(context) &&
              context.CurrentFallbackFrontierRepairContract?.Lifecycle.SettlementOutcome ==
                  FallbackFrontierRepairSettlementOutcome.ProgressObserved))
        {
            LogFileTransferFallbackFrontierRepairProgressObserved(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                postTunaFallbackRepairProgressKey ?? "(none)",
                "chunk_batch_committed",
                postTunaFallbackRepairProgressBefore,
                postTunaFallbackRepairProgressAfter,
                postTunaFallbackRepairProgressRequestedCount);
        }

        if (snapshot is not null)
        {
            lock (gate)
            {
                if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
                RaiseTransferChanged(snapshot);
                MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
            }
        }

        if (committedChunkCount > 0)
        {
            await MaybeSendInboundV6RepairProofAsync(
                context,
                batch,
                fallbackFrontierContract,
                receivedTransportKind,
                acceptedChunks.Count,
                nextChunkIndexAfterCommit,
                previousCommittedChunkIndex, writeSource).ConfigureAwait(false);

            lock (gate)
            {
                if (writeSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, writeSource)) { return; }
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked(
                        context,
                        DateTimeOffset.UtcNow);
                }
            }
            SchedulePendingInboundV6FrontierRecentProgressGraceWake(context);
        }

        if (completed)
        {
            lock (gate)
            {
                // The captured write settles first; the coordinator then restores an empty-gap source for finalization.
                if (writeSource?.FallbackRepairSource is not null && HasInboundFallbackRepairHandoffLocked(context)) { return; }
            }
            await FinalizeInboundTransferWithWriteBindingAsync(context, context.LifetimeCts.Token, writeSource).ConfigureAwait(false);
            return;
        }

        if (committedChunkCount > 0)
        {
            var forceState = postTunaFallbackRepairProgressObserved ||
                             fallbackFrontierProgressReceipt is not null ||
                             ShouldForceInboundV6ReceiverStateAfterProgress(context, previousCommittedChunkIndex);
            await SendInboundV6ReceiverStateWithWriteBindingAsync(
                    context,
                    "chunk_batch_committed",
                    forceSend: forceState,
                    fallbackFrontierProgressReceipt: fallbackFrontierProgressReceipt,
                    requiredWriteSource: writeSource)
                .ConfigureAwait(false);
        }
        else
        {
            var forceState = ShouldForceInboundV6ReceiverStateDuringSparseFrontierStall(context);
            if (forceState)
            {
                await SendInboundV6ReceiverStateWithWriteBindingAsync(
                    context,
                    "chunk_batch_frontier_stalled",
                    forceSend: true, requiredWriteSource: writeSource).ConfigureAwait(false);
            }
            else
            {
                LogInboundV6ReceiverStateDeferredForFrontierStall(
                    context,
                    nextChunkIndexAfterCommit,
                    highestReceivedChunkIndexAfterCommit);
            }
        }

        await SendInboundV6FrontierRequestWithWriteBindingAsync(context, "chunk_batch_committed", forceSend: false,
            requiredWriteSource: writeSource).ConfigureAwait(false);
    }

    private async Task HandleInboundV6ContiguousChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks,
        Stream writeStream,
        FileTransferTransportKind receivedTransportKind,
        FallbackFrontierRepairContract? fallbackFrontierContract)
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
        InboundFallbackFrontierProgressReceipt? fallbackFrontierProgressReceipt = null;
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
                fallbackFrontierProgressReceipt = TrySettleInboundFallbackFrontierProgressLocked(
                    context,
                    fallbackFrontierContract,
                    batch.RepairRequestId,
                    batch.TransportEpoch,
                    receivedTransportKind,
                    previousCommittedChunkIndex,
                    context.NextChunkIndex,
                    "contiguous_chunk_batch_committed");
                ResetInboundV6PostTunaFallbackFrontierRescueLocked(context);
                ClearInboundV6ReceiverStateDeferredDiagnosticLocked(context);
            }

            context.PullReceiverWriteBatchCountRecent++;
            context.PullReceiverWriteBatchBytesRecent += writtenBytes;
            context.PullReceiverWriteDurationMsRecent += writeStopwatch.ElapsedMilliseconds;
            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            nextChunkIndexAfterCommit = context.NextChunkIndex;
            if (nextChunkIndexAfterCommit > previousCommittedChunkIndex)
            {
                var tailTransportEpoch = batch.TransportEpoch > 0
                    ? batch.TransportEpoch
                    : context.CurrentTransferLeg?.TransportEpochId ?? 0;
                TryMarkInboundFallbackTailQuiescentLocked(
                    context,
                    tailTransportEpoch,
                    "chunk_batch_committed");
            }

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
                fallbackFrontierContract,
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

        var forceState = fallbackFrontierProgressReceipt is not null ||
                         ShouldForceInboundV6ReceiverStateAfterProgress(context, previousCommittedChunkIndex);
        await SendInboundV6ReceiverStateAsync(
                context,
                "chunk_batch_committed",
                forceSend: forceState,
                fallbackFrontierProgressReceipt: fallbackFrontierProgressReceipt)
            .ConfigureAwait(false);
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

    private Task<bool> SendInboundV6ReceiverStateAsync(
        InboundTransferContext context, string reason, bool forceSend = false, bool terminalReady = false,
        InboundFallbackFrontierProgressReceipt? fallbackFrontierProgressReceipt = null)
        => SendInboundV6ReceiverStateWithFinalizationSourceAsync(context, reason, forceSend, terminalReady,
            fallbackFrontierProgressReceipt, null);

    private Task<bool> SendInboundV6ReceiverStateWithWriteBindingAsync(
        InboundTransferContext context, string reason, bool forceSend = false, bool terminalReady = false,
        InboundFallbackFrontierProgressReceipt? fallbackFrontierProgressReceipt = null,
        InboundBootstrapWriteSource? requiredWriteSource = null)
        => SendInboundV6ReceiverStateWithFinalizationSourceAsync(context, reason, forceSend, terminalReady,
            fallbackFrontierProgressReceipt, null, requiredWriteSource);

    private async Task<bool> SendInboundV6ReceiverStateWithFinalizationSourceAsync(
        InboundTransferContext context,
        string reason,
        bool forceSend = false,
        bool terminalReady = false,
        InboundFallbackFrontierProgressReceipt? fallbackFrontierProgressReceipt = null,
        InboundFinalizationSource? requiredFinalizationSource = null,
        InboundBootstrapWriteSource? requiredWriteSource = null)
    {
        FileTransferReceiverStateFrameV6? state;
        IFileTransferDataSession? dataSession;
        int requestWindowChunks;
        bool frontierStalled;
        InboundFallbackFrontierProgressReceipt? validatedFallbackFrontierProgressReceipt = null;
        InboundFallbackRepairFeedbackOperation? feedbackOperation = null;
        lock (gate)
        {
            if (HasInboundFallbackRepairHandoffLocked(context)) { return false; }
            if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
            if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return false; }
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived)
            {
                return false;
            }

            if (fallbackFrontierProgressReceipt is not null)
            {
                if (!TryValidateInboundFallbackFrontierProgressReceiptLocked(
                        context,
                        fallbackFrontierProgressReceipt,
                        out var progressReceiptRejectionReason))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_fallback_frontier_repair_progress_state_force_rejected; {FormatFallbackFrontierRepairAuthorityTuple(fallbackFrontierProgressReceipt.Authority)}; protocol_repair_request_id={FormatProtocolLogValue(fallbackFrontierProgressReceipt.ProtocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(fallbackFrontierProgressReceipt.RequestCorrelationId)}; range_fingerprint={FormatProtocolLogValue(fallbackFrontierProgressReceipt.RangeFingerprint)}; received_transport={FormatFileTransferTransportKind(fallbackFrontierProgressReceipt.ReceivedTransport)}; data_session_identity={FormatProtocolLogValue(fallbackFrontierProgressReceipt.DataSessionIdentity)}; data_session_generation={fallbackFrontierProgressReceipt.DataSessionGeneration}; previous_frontier_chunk_index={fallbackFrontierProgressReceipt.PreviousFrontierChunkIndex}; current_frontier_chunk_index={fallbackFrontierProgressReceipt.CurrentFrontierChunkIndex}; authority_effect=none; decision_status=Rejected; decision_reason={FormatProtocolLogValue(progressReceiptRejectionReason)}");
                    return false;
                }

                validatedFallbackFrontierProgressReceipt = fallbackFrontierProgressReceipt;
                forceSend = true;
            }

            if (TrySuppressInboundV6ControlSendForTunaActivationBarrierLocked(
                    context,
                    "receiver_state",
                    reason))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var currentPostTunaFallbackLeg =
                context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
                IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg);
            var postTunaFallbackCheckpoint =
                currentPostTunaFallbackLeg &&
                !string.IsNullOrWhiteSpace(context.CurrentTransferLeg?.CheckpointRequestId) &&
                (IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg) ||
                 string.Equals(reason, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal) &&
                 IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg));
            var receiverStateRetryInterval = TimeSpan.FromMilliseconds(V6ReceiverStateRetryIntervalMs);
            if (forceSend &&
                validatedFallbackFrontierProgressReceipt is null &&
                !terminalReady &&
                !postTunaFallbackCheckpoint &&
                ShouldCoalesceForcedInboundV6PostTunaFallbackControlLocked(
                    context,
                    now,
                    context.V6LastReceiverStateSentUtc,
                    context.V6LastReceiverStateCommittedChunkIndex,
                    receiverStateRetryInterval,
                    "receiver_state",
                    reason))
            {
                return false;
            }

            if (!forceSend &&
                context.V6LastReceiverStateSentUtc is { } lastSent &&
                now - lastSent < receiverStateRetryInterval)
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
            var receiverTransportEpoch = currentPostTunaFallbackLeg
                ? context.CurrentTransferLeg!.TransportEpochId
                : context.V6ReceiverTransportEpoch;
            var receiverRepairRequestId = postTunaFallbackCheckpoint
                ? context.CurrentTransferLeg!.CheckpointRequestId
                : null;
            var receiverPriority = postTunaFallbackCheckpoint
                ? context.CurrentTransferLeg!.CheckpointPriority ?? V6RegularNknStateRefreshPriority
                : ranges.Count > 0 && ranges[0].StartChunkIndex == context.NextChunkIndex
                    ? "frontier"
                    : null;
            var receiverRecoveryMode = postTunaFallbackCheckpoint
                ? V6RegularNknStateRefreshRecoveryMode
                : recoveryMode;
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
                TerminalReady = terminalReady,
                TransferPaused = context.UserPaused,
                TransferPauseReason = context.UserPauseReason,
                TransportEpoch = receiverTransportEpoch,
                RepairRequestId = receiverRepairRequestId,
                Priority = receiverPriority,
                RecoveryMode = receiverRecoveryMode,
            };
            context.V6LastReceiverStateSentUtc = now;
            context.V6LastReceiverStateCommittedChunkIndex = context.NextChunkIndex;
            context.V6LastReceiverStateHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            dataSession = context.DataSession;
            context.FrontierPublicationObservation?.Capture(CreateCoordinatorStateLocked(context), dataSession, state);
            context.FrontierBoundaryObservation?.Frame(context, state);
        }

        if (validatedFallbackFrontierProgressReceipt is { } progressReceipt)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; {FileTransferDiagnosticScopeEvidence.FormatDataSession(progressReceipt.DataSessionIdentity)}{FileTransferDiagnosticScopeEvidence.Format(progressReceipt.Authority.SessionId, progressReceipt.Authority.TransferId)}{FormatCheckpointRepairContinuity(GetCheckpointRepairContinuityStamp(progressReceipt))}{FormatFallbackFrontierRepairAuthorityTuple(progressReceipt.Authority)}; protocol_repair_request_id={FormatProtocolLogValue(progressReceipt.ProtocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(progressReceipt.RequestCorrelationId)}; range_fingerprint={FormatProtocolLogValue(progressReceipt.RangeFingerprint)}; received_transport={FormatFileTransferTransportKind(progressReceipt.ReceivedTransport)}; data_session_identity={FormatProtocolLogValue(progressReceipt.DataSessionIdentity)}; data_session_generation={progressReceipt.DataSessionGeneration}; previous_frontier_chunk_index={progressReceipt.PreviousFrontierChunkIndex}; current_frontier_chunk_index={progressReceipt.CurrentFrontierChunkIndex}; receiver_state_epoch={state.Epoch}; authority_effect=none; decision_status=Accepted; decision_reason=exact_fallback_frontier_repair_progress");
        }

        try
        {
            FileTransferControlPlaneDeliveryRequest? controlPlaneRequest = null;
            var peerVisibleControlPlaneProof = false;
            lock (gate)
            {
                if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return false; }
                if (ReferenceEquals(inboundTransfer, context) &&
                    !context.IsTerminal &&
                    ShouldUseFallbackControlPlaneForInboundFrame(context, state))
                {
                    var kind = string.Equals(state.RecoveryMode, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal)
                        ? FileTransferControlPlaneKind.FallbackCheckpointProof
                        : FileTransferControlPlaneKind.ReceiverState;
                    controlPlaneRequest = CreateInboundControlPlaneDeliveryRequestLocked(
                        context,
                        kind,
                        state,
                        reason);
                    controlPlaneRequest = BindInboundTrackedWriteControlRequest(context, controlPlaneRequest, requiredWriteSource);
                    if (!TryRegisterInboundFallbackRepairFeedbackLocked(context, dataSession, controlPlaneRequest, out feedbackOperation)) { return false; }
                    controlPlaneRequest = BindInboundFallbackRepairFeedbackRequest(context, controlPlaneRequest, feedbackOperation);
                    if (requiredFinalizationSource is not null)
                    {
                        var existingValidator = controlPlaneRequest.PreTransportDeliveryValidator;
                        controlPlaneRequest = controlPlaneRequest with { PreTransportDeliveryValidator = () =>
                        {
                            lock (gate)
                            {
                                return (existingValidator is null || existingValidator()) &&
                                    IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource);
                            }
                        } };
                    }
                }

                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked(
                        context,
                        DateTimeOffset.UtcNow);
                }
            }
            SchedulePendingInboundV6FrontierRecentProgressGraceWake(context);

            if (controlPlaneRequest is not null)
            {
                var delivery = await SendInboundFallbackControlPlaneOrDataSessionWithWriteBindingAsync(
                        context,
                        dataSession,
                        controlPlaneRequest,
                        context.LifetimeCts.Token, requiredWriteSource: requiredWriteSource,
                        requiredFeedbackOperation: feedbackOperation)
                    .ConfigureAwait(false);
                if (!delivery.Sent)
                {
                    lock (gate)
                    {
                        context.FrontierPublicationObservation?.Reject(state);
                        context.FrontierBoundaryObservation?.Reject(state);
                    }
                    return false;
                }

                peerVisibleControlPlaneProof = delivery.Result?.PeerVisibleAny == true;
                lock (gate)
                {
                    if (feedbackOperation is not null && !IsInboundFallbackRepairFeedbackCurrentLocked(context, feedbackOperation)) { return false; }
                    if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                    if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return false; }
                    if (delivery.Result is null)
                    {
                        context.FrontierPublicationObservation?.Unsupported(state);
                        context.FrontierBoundaryObservation?.Reject(state);
                    }
                    if (ReferenceEquals(inboundTransfer, context) &&
                        !context.IsTerminal)
                    {
                        TryConsumeInboundFallbackTailReceiverStateProofLocked(
                            context,
                            state,
                            controlPlaneRequest,
                            delivery.Result,
                            reason);
                    }
                }
            }
            else
            {
                Task sending;
                lock (gate)
                {
                    if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                    if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return false; }
                    sending = dataSession.SendAsync(state, context.LifetimeCts.Token);
                }
                await sending.ConfigureAwait(false);
                lock (gate)
                {
                    context.FrontierPublicationObservation?.Unsupported(state);
                    context.FrontierBoundaryObservation?.Reject(state);
                }
            }

            lock (gate)
            {
                if (feedbackOperation is not null && !IsInboundFallbackRepairFeedbackCurrentLocked(context, feedbackOperation)) { return false; }
                if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                if (requiredFinalizationSource is not null && !IsInboundFinalizationSourceCurrentLocked(context, requiredFinalizationSource)) { return false; }
            }
            if (string.Equals(state.RecoveryMode, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal))
            {
                if (controlPlaneRequest is not null && !peerVisibleControlPlaneProof)
                {
                    return true;
                }

                lock (gate)
                {
                    if (feedbackOperation is not null && !IsInboundFallbackRepairFeedbackCurrentLocked(context, feedbackOperation)) { return false; }
                    if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                    if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                    {
                        MarkInboundFallbackCheckpointAcceptedLocked(
                            context,
                            state.RepairRequestId,
                            state.TransportEpoch,
                            V6RegularNknStateRefreshRecoveryMode);
                    }
                }
            }

            var requestedChunkCount = 0;
            foreach (var range in state.MissingRanges)
            {
                requestedChunkCount += Math.Max(0, range.ChunkCount);
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_receiver_state_sent; {FileTransferDiagnosticScopeEvidence.Format(state.SessionId, state.TransferId)}{FormatCheckpointRepairContinuity(GetCheckpointRepairContinuityStamp(validatedFallbackFrontierProgressReceipt))}transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; requested_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; destination_mode={FormatV6DestinationMode(context.V6DestinationMode)}; transfer_paused={(state.TransferPaused ? 1 : 0)}; transport_epoch={state.TransportEpoch}; repair_request_id={FormatProtocolLogValue(state.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(state.Priority ?? "(none)")}; recovery_mode={FormatProtocolLogValue(state.RecoveryMode ?? "(none)")}");
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
        catch (Exception ex) when (!context.LifetimeCts.IsCancellationRequested)
        {
            lock (gate)
            {
                if (feedbackOperation is not null && !IsInboundFallbackRepairFeedbackCurrentLocked(context, feedbackOperation)) { return false; }
                if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                if (requiredFinalizationSource is not null)
                {
                    LocalOperationalLog.Warn("FileTransferService",
                        $"event=filetransfer_v6_terminal_ready_send_failed; transfer_id={context.TransferId}; reason={ex.GetType().Name}");
                    return false;
                }
                context.FrontierPublicationObservation?.Reject(state);
                context.FrontierBoundaryObservation?.Reject(state);
                if (TryDeferInboundV6ControlSendForTunaActivationBarrier(
                        context,
                        "receiver_state",
                        reason,
                        ex,
                        state,
                        request: null))
                {
                    return false;
                }

                if (TryDeferInboundV6ControlSendFailureForRecovery(context, "receiver_state", reason, ex))
                {
                    return false;
                }
            }

            if (requiredWriteSource is not null)
            {
                await FailInboundV4Async(context, InvalidStateErrorCode, ex.Message,
                    "Could not send V6 receiver state.", requiredWriteSource).ConfigureAwait(false);
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
        finally { await FinalizeInboundFallbackRepairFeedbackAsync(context, feedbackOperation).ConfigureAwait(false); }
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

            if (committedDelta > 0 &&
                context.NextChunkIndex > previousCommittedChunkIndex &&
                IsInboundV6PostTunaFallbackSurvivalPathLocked(context))
            {
                var coordinatorState = CreateCoordinatorStateLocked(context);
                if (FileTransferCoordinator.GetFallbackTailYieldSubstate(coordinatorState).CurrentOperation is
                        { LocalProof: not null } yielded &&
                    FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(
                        coordinatorState, yielded.Decision, out _))
                {
                    // The retired recovery worker cannot flush coalesced progress.
                    // Send it now through the existing coordinator-authorized control path.
                    return true;
                }
            }

            if (ShouldForceInboundV6PostTunaFallbackFrontierProgressStateLocked(
                    context,
                    previousCommittedChunkIndex,
                    committedDelta))
            {
                TryMarkInboundFallbackTailQuiescentLocked(
                    context,
                    context.CurrentTransferLeg?.TransportEpochId ?? context.V6ReceiverTransportEpoch,
                    "receiver_state_sparse_runtime");
                return true;
            }

            if (DateTimeOffset.UtcNow - context.V6LastReceiverStateSentUtc.Value >=
                TimeSpan.FromMilliseconds(V6ReceiverStateProgressMaxIntervalMs))
            {
                return true;
            }

            var shouldLogCoalesced = true;
            var suppressedCoalescedCount = 0;
            if (IsInboundV6PostTunaFallbackSurvivalPathLocked(context))
            {
                var leg = context.CurrentTransferLeg;
                var frontierGapMissing = IsInboundPostFallbackFrontierGapMissingLocked(context);
                var tuple = BuildFallbackChurnTuple(
                    context.SessionId,
                    context.TransferId,
                    context.RouteSelection.TelemetryToken,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    leg?.Generation ?? 0,
                    leg?.TransportEpochId ?? context.V6ReceiverTransportEpoch,
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex,
                    frontierGapMissing ? 1 : 0,
                    frontierGapMissing ? 1 : 0,
                    leg?.CheckpointRequestId);
                shouldLogCoalesced = ShouldLogInboundV6ReceiverStateCoalescedDiagnosticLocked(
                    context,
                    tuple,
                    DateTimeOffset.UtcNow,
                    out suppressedCoalescedCount);
            }

            if (shouldLogCoalesced)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_coalesced; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}transfer_id={context.TransferId}; session_id={context.SessionId}; previous_committed_chunk_index={previousCommittedChunkIndex}; current_committed_chunk_index={context.NextChunkIndex}; committed_delta={committedDelta}; last_sent_committed_chunk_index={context.V6LastReceiverStateCommittedChunkIndex}; suppressed_repeat_count={suppressedCoalescedCount}");
            }
            return false;
        }
    }

    private static bool ShouldForceInboundV6PostTunaFallbackFrontierProgressStateLocked(
        InboundTransferContext context,
        int previousCommittedChunkIndex,
        int committedDelta)
    {
        if (!IsInboundV6PostTunaFallbackSurvivalPathLocked(context) ||
            !IsInboundPostTunaRecoveryActiveLocked(context) ||
            context.V6DestinationMode != V6ReceiveDestinationMode.SparseSeekable ||
            committedDelta <= 0 ||
            context.NextChunkIndex <= previousCommittedChunkIndex ||
            context.NextChunkIndex <= context.V6LastReceiverStateCommittedChunkIndex)
        {
            return false;
        }

        var frontierGapMissing = IsInboundPostFallbackFrontierGapMissingLocked(context);
        var terminalFrontier = context.NextChunkIndex >= context.ChunkCount;
        var activeFrontierRecovery = context.PullPostTunaRecoveryActive || context.PullTransportRebindGeneration > 0;
        if (!terminalFrontier && !activeFrontierRecovery)
        {
            return false;
        }

        if (!terminalFrontier &&
            !frontierGapMissing &&
            context.PullTransportRebindGeneration <= 0)
        {
            return false;
        }

        var leg = context.CurrentTransferLeg;
        var tuple = BuildFallbackChurnTuple(
            context.SessionId,
            context.TransferId,
            context.RouteSelection.TelemetryToken,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            leg?.Generation ?? 0,
            leg?.TransportEpochId ?? context.V6ReceiverTransportEpoch,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex,
            frontierGapMissing ? 1 : 0,
            frontierGapMissing ? 1 : 0,
            leg?.CheckpointRequestId);
        if (ShouldLogInboundV6FrontierProgressForcedDiagnosticLocked(
                context,
                tuple,
                DateTimeOffset.UtcNow,
                out var suppressedCount))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; leg_generation={leg?.Generation ?? 0}; transport_epoch={leg?.TransportEpochId ?? context.V6ReceiverTransportEpoch}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; previous_committed_chunk_index={previousCommittedChunkIndex}; current_committed_chunk_index={context.NextChunkIndex}; committed_delta={committedDelta}; last_sent_committed_chunk_index={context.V6LastReceiverStateCommittedChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; frontier_gap_missing={(frontierGapMissing ? 1 : 0)}; terminal_frontier={(terminalFrontier ? 1 : 0)}; recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}; rebind_generation={context.PullTransportRebindGeneration}; frontier_repair_window_chunks={context.PullTransportRebindFrontierRepairWindowChunks}; frontier_repair_committed_chunks={context.PullTransportRebindFrontierRepairCommittedChunks}; suppressed_repeat_count={suppressedCount}");
        }
        return true;
    }

    private static bool ShouldLogInboundV6FrontierProgressForcedDiagnosticLocked(
        InboundTransferContext context,
        string tuple,
        DateTimeOffset now,
        out int suppressedCount)
    {
        if (string.Equals(context.V6LastFrontierProgressForcedDiagnosticTuple, tuple, StringComparison.Ordinal) &&
            context.V6LastFrontierProgressForcedDiagnosticUtc is { } lastLogged &&
            now - lastLogged < TimeSpan.FromMilliseconds(V6ReceiverStateDeferredDiagnosticCoalesceMs))
        {
            context.V6FrontierProgressForcedDiagnosticSuppressedCount++;
            suppressedCount = 0;
            return false;
        }

        suppressedCount = context.V6FrontierProgressForcedDiagnosticSuppressedCount;
        context.V6LastFrontierProgressForcedDiagnosticTuple = tuple;
        context.V6LastFrontierProgressForcedDiagnosticUtc = now;
        context.V6FrontierProgressForcedDiagnosticSuppressedCount = 0;
        return true;
    }

    private static bool ShouldLogInboundV6ReceiverStateCoalescedDiagnosticLocked(
        InboundTransferContext context,
        string tuple,
        DateTimeOffset now,
        out int suppressedCount)
    {
        if (string.Equals(context.V6LastReceiverStateCoalescedDiagnosticTuple, tuple, StringComparison.Ordinal) &&
            context.V6LastReceiverStateCoalescedDiagnosticUtc is { } lastLogged &&
            now - lastLogged < TimeSpan.FromMilliseconds(V6ReceiverStateDeferredDiagnosticCoalesceMs))
        {
            context.V6ReceiverStateCoalescedDiagnosticSuppressedCount++;
            suppressedCount = 0;
            return false;
        }

        suppressedCount = context.V6ReceiverStateCoalescedDiagnosticSuppressedCount;
        context.V6LastReceiverStateCoalescedDiagnosticTuple = tuple;
        context.V6LastReceiverStateCoalescedDiagnosticUtc = now;
        context.V6ReceiverStateCoalescedDiagnosticSuppressedCount = 0;
        return true;
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

            if (context.PullHighestReceivedChunkIndex > context.V6LastReceiverStateHighestReceivedChunkIndex)
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

            var shouldLogCoalesced = true;
            var suppressedCoalescedCount = 0;
            if (IsInboundV6PostTunaFallbackSurvivalPathLocked(context))
            {
                var leg = context.CurrentTransferLeg;
                var frontierGapMissing = IsInboundPostFallbackFrontierGapMissingLocked(context);
                var tuple = BuildFallbackChurnTuple(
                    context.SessionId,
                    context.TransferId,
                    context.RouteSelection.TelemetryToken,
                    context.NegotiatedDataProtocolVersion,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    leg?.Generation ?? 0,
                    leg?.TransportEpochId ?? context.V6ReceiverTransportEpoch,
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex,
                    frontierGapMissing ? 1 : 0,
                    frontierGapMissing ? 1 : 0,
                    leg?.CheckpointRequestId);
                shouldLogCoalesced = ShouldLogInboundV6ReceiverStateCoalescedDiagnosticLocked(
                    context,
                    tuple,
                    now,
                    out suppressedCoalescedCount);
            }

            if (shouldLogCoalesced)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_receiver_state_coalesced; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}transfer_id={context.TransferId}; session_id={context.SessionId}; reason=frontier_stalled_tail_window; current_committed_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; accept_window_end_chunk_index={context.V6SparseAcceptWindowEndExclusive}; tail_chunks_remaining={tailChunksRemaining}; elapsed_since_state_ms={(long)Math.Max(0, elapsedSinceState.TotalMilliseconds)}; suppressed_repeat_count={suppressedCoalescedCount}");
            }
            return false;
        }
    }

    private void LogInboundV6ReceiverStateDeferredForFrontierStall(
        InboundTransferContext context,
        int nextChunkIndex,
        int highestReceivedChunkIndex)
    {
        var now = DateTimeOffset.UtcNow;
        string transferId;
        string sessionId;
        string route;
        int protocolVersion;
        int liveRouteEpoch;
        int legGeneration;
        long transportEpoch;
        string checkpointRequestId;
        int suppressedCount;
        bool shouldLog;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            transferId = context.TransferId;
            sessionId = context.SessionId;
            route = context.RouteSelection.TelemetryToken;
            protocolVersion = context.NegotiatedDataProtocolVersion;
            liveRouteEpoch = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
            legGeneration = context.CurrentTransferLeg?.Generation ?? 0;
            transportEpoch = context.CurrentTransferLeg?.TransportEpochId ?? context.V6ReceiverTransportEpoch;
            checkpointRequestId = context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)";
            var frontierGapMissing = IsInboundPostFallbackFrontierGapMissingLocked(context);
            var tuple = BuildFallbackChurnTuple(
                sessionId,
                transferId,
                route,
                protocolVersion,
                liveRouteEpoch,
                legGeneration,
                transportEpoch,
                nextChunkIndex,
                highestReceivedChunkIndex,
                frontierGapMissing ? 1 : 0,
                frontierGapMissing ? 1 : 0,
                checkpointRequestId);

            shouldLog = !string.Equals(context.V6LastReceiverStateDeferredDiagnosticTuple, tuple, StringComparison.Ordinal) ||
                        context.V6LastReceiverStateDeferredDiagnosticUtc is not { } lastLogged ||
                        now - lastLogged >= TimeSpan.FromMilliseconds(V6ReceiverStateDeferredDiagnosticCoalesceMs);
            if (shouldLog)
            {
                suppressedCount = context.V6ReceiverStateDeferredDiagnosticSuppressedCount;
                context.V6LastReceiverStateDeferredDiagnosticTuple = tuple;
                context.V6LastReceiverStateDeferredDiagnosticUtc = now;
                context.V6ReceiverStateDeferredDiagnosticSuppressedCount = 0;
            }
            else
            {
                context.V6ReceiverStateDeferredDiagnosticSuppressedCount++;
                return;
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_receiver_state_deferred; {FileTransferDiagnosticScopeEvidence.Format(sessionId, transferId)}transfer_id={transferId}; session_id={sessionId}; route={route}; protocol_version={protocolVersion}; live_route_epoch={liveRouteEpoch}; leg_generation={legGeneration}; transport_epoch={transportEpoch}; checkpoint_request_id={FormatProtocolLogValue(checkpointRequestId)}; reason=frontier_stalled; next_chunk_index={nextChunkIndex}; highest_received_chunk_index={highestReceivedChunkIndex}; suppressed_repeat_count={suppressedCount}");
    }

    private static void ClearInboundV6ReceiverStateDeferredDiagnosticLocked(InboundTransferContext context)
    {
        context.V6LastReceiverStateDeferredDiagnosticTuple = null;
        context.V6LastReceiverStateDeferredDiagnosticUtc = null;
        context.V6ReceiverStateDeferredDiagnosticSuppressedCount = 0;
        context.V6LastReceiverStateCoalescedDiagnosticTuple = null;
        context.V6LastReceiverStateCoalescedDiagnosticUtc = null;
        context.V6ReceiverStateCoalescedDiagnosticSuppressedCount = 0;
        context.V6LastFrontierProgressForcedDiagnosticTuple = null;
        context.V6LastFrontierProgressForcedDiagnosticUtc = null;
        context.V6FrontierProgressForcedDiagnosticSuppressedCount = 0;
    }

    private Task<bool> SendInboundV6FrontierRequestAsync(InboundTransferContext context, string reason, bool forceSend = false)
        => SendInboundV6FrontierRequestWithWriteBindingAsync(context, reason, forceSend, null);

    private async Task<bool> SendInboundV6FrontierRequestWithWriteBindingAsync(
        InboundTransferContext context,
        string reason,
        bool forceSend = false,
        InboundBootstrapWriteSource? requiredWriteSource = null)
    {
        bool useAuthorizedFallbackPath;
        bool fallbackRuntimeWithoutAuthority;
        lock (gate)
        {
            if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
            useAuthorizedFallbackPath =
                ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                IsStrictFallbackFrontierRuntime(context);
            fallbackRuntimeWithoutAuthority =
                ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                IsFallbackFrontierRouteRuntime(context) &&
                !useAuthorizedFallbackPath;
        }

        if (useAuthorizedFallbackPath)
        {
            var sent = await SendInboundAuthorizedFallbackFrontierRequestAsync(context, reason, forceSend, requiredWriteSource)
                .ConfigureAwait(false);
            SchedulePendingInboundV6FrontierRecentProgressGraceWake(context);
            SchedulePendingInboundV6FrontierStallGraceWake(context);
            return sent;
        }

        if (fallbackRuntimeWithoutAuthority)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_frontier_repair_authority_rejected; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id=(none); direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; decision_reason=fallback_runtime_owner_or_leg_missing; source={FormatProtocolLogValue(reason)}");
            return false;
        }

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

            if (TrySuppressInboundV6ControlSendForTunaActivationBarrierLocked(
                    context,
                    "frontier_request",
                    reason))
            {
                return false;
            }

            var now = ReadPublicationBoundaryUtc(context);
            if (!ShouldSendInboundV6FrontierRequestLocked(context, now, unresolvedFrontierProofRequest, reason))
            {
                return false;
            }

            var frontierRequestRetryInterval = ResolveInboundV6FrontierRequestRetryIntervalLocked(context, unresolvedFrontierProofRequest);
            if (forceSend &&
                !unresolvedFrontierProofRequest &&
                ShouldCoalesceForcedInboundV6PostTunaFallbackControlLocked(
                    context,
                    now,
                    context.V6LastFrontierRequestSentUtc,
                    context.V6LastFrontierRequestChunkIndex,
                    frontierRequestRetryInterval,
                    "frontier_request",
                    reason))
            {
                return false;
            }

            if (!forceSend &&
                context.V6LastFrontierRequestSentUtc is { } lastSent &&
                now - lastSent < frontierRequestRetryInterval)
            {
                if (context.V6LastFrontierRequestChunkIndex != context.NextChunkIndex)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_frontier_request_coalesced; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; previous_frontier_chunk_index={context.V6LastFrontierRequestChunkIndex}; current_frontier_chunk_index={context.NextChunkIndex}; elapsed_ms={(long)(now - lastSent).TotalMilliseconds}; retry_interval_ms={(long)frontierRequestRetryInterval.TotalMilliseconds}; reason={FormatProtocolLogValue(reason)}");
                }

                return false;
            }

            var strictEpochFrontierProof = IsV6TransportEpochUnresolved(context.V6TransportEpoch);
            var frontierRequestChunks = strictEpochFrontierProof
                ? ResolveInboundV6EpochFrontierRequestChunksLocked(context)
                : ResolveInboundV6FrontierRequestChunksLocked(context);
            context.V6FrontierRequestSequence++;
            var repairRequestId = strictEpochFrontierProof
                ? $"v6-frontier:{context.V6TransportEpoch!.EpochId}:{context.NextChunkIndex}:{context.V6FrontierRequestSequence}"
                : $"v6-frontier:{context.NextChunkIndex}:{context.V6FrontierRequestSequence}";
            if (strictEpochFrontierProof)
            {
                context.V6TransportEpoch!.LastRepairRequestId = repairRequestId;
            }

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

            var frontierTransportEpoch = context.V6ReceiverTransportEpoch;

            request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = frontierTransportEpoch,
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
            FileTransferControlPlaneDeliveryRequest? controlPlaneRequest = null;
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) &&
                    !context.IsTerminal &&
                    ShouldUseFallbackControlPlaneForInboundFrame(context, request))
                {
                    controlPlaneRequest = EnsureControlPlaneDeliveryRequestCorrelation(CreateInboundControlPlaneDeliveryRequestLocked(
                        context,
                        FileTransferControlPlaneKind.FrontierRequest,
                        request,
                        reason));
                    if (controlPlaneRequest.Frame is FileTransferFrontierRequestFrameV6 correlatedRequest)
                    {
                        request = correlatedRequest;
                    }
                }
            }

            if (controlPlaneRequest is not null)
            {
                var delivery = await SendInboundFallbackControlPlaneOrDataSessionAsync(
                        context,
                        dataSession,
                        controlPlaneRequest,
                        context.LifetimeCts.Token)
                    .ConfigureAwait(false);
                if (!delivery.Sent)
                {
                    return false;
                }
            }
            else
            {
                await dataSession.SendAsync(request, context.LifetimeCts.Token).ConfigureAwait(false);
            }

            var totalRequestedChunks = request.MissingRanges.Sum(static range => range.ChunkCount);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_sent; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; recovery_mode={FormatProtocolLogValue(request.RecoveryMode ?? "(none)")}; start_chunk_index={request.MissingRanges[0].StartChunkIndex}; requested_chunk_count={request.MissingRanges[0].ChunkCount}; total_requested_chunk_count={totalRequestedChunks}; range_count={request.MissingRanges.Count}; post_tuna_fallback_survival=0");
            return true;
        }
        catch (Exception ex) when (!context.LifetimeCts.IsCancellationRequested)
        {
            if (TryDeferInboundV6ControlSendForTunaActivationBarrier(
                    context,
                    "frontier_request",
                    reason,
                    ex,
                    state: null,
                    request))
            {
                return false;
            }

            if (TryDeferInboundV6ControlSendFailureForRecovery(context, "frontier_request", reason, ex))
            {
                return false;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_failed; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private static bool ShouldCoalesceForcedInboundV6PostTunaFallbackControlLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        DateTimeOffset? lastSentUtc,
        int lastSentFrontierChunkIndex,
        TimeSpan retryInterval,
        string frameKind,
        string reason)
    {
        if (string.Equals(reason, "regular_nkn_legacy_v4_chunk_probe", StringComparison.Ordinal) ||
            string.Equals(reason, "regular_nkn_legacy_v4_state_proof", StringComparison.Ordinal) ||
            string.Equals(reason, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsInboundV6PostTunaFallbackSurvivalPathLocked(context) ||
            lastSentUtc is not { } lastSent ||
            lastSentFrontierChunkIndex != context.NextChunkIndex ||
            now - lastSent >= retryInterval)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_control_coalesced; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; frame_kind={FormatProtocolLogValue(frameKind)}; frontier_chunk_index={context.NextChunkIndex}; elapsed_ms={(long)Math.Max(0, (now - lastSent).TotalMilliseconds)}; retry_interval_ms={(long)retryInterval.TotalMilliseconds}; reason={FormatProtocolLogValue(reason)}");
        return true;
    }

    private static bool TrySuppressInboundV6ControlSendForTunaActivationBarrierLocked(
        InboundTransferContext context,
        string frameKind,
        string reason)
    {
        if (!context.PullTransportPaused ||
            !IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
        {
            return false;
        }

        context.TunaActivationBarrierStateSendSuppressedCount++;
        if (context.TunaActivationBarrierStateSendSuppressedCount is 1 ||
            context.TunaActivationBarrierStateSendSuppressedCount % 64 == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_control_send_suppressed_for_tuna_activation_barrier; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; frame_kind={FormatProtocolLogValue(frameKind)}; state_reason={FormatProtocolLogValue(reason)}; pause_reason={FormatProtocolLogValue(context.PullTransportPauseReason)}; suppressed_count={context.TunaActivationBarrierStateSendSuppressedCount}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}");
        }

        return true;
    }

    private bool TryDeferInboundV6ControlSendForTunaActivationBarrier(
        InboundTransferContext context,
        string frameKind,
        string reason,
        Exception ex,
        FileTransferReceiverStateFrameV6? state,
        FileTransferFrontierRequestFrameV6? request)
    {
        var explicitActivationUnavailable = IsTunaActivationNegotiationDataSessionUnavailableException(ex);
        string? pauseReason = null;
        var suppressedCount = 0;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                ((context.PullTransportPaused &&
                  IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason)) ||
                 explicitActivationUnavailable))
            {
                pauseReason = context.PullTransportPaused &&
                              IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason)
                    ? context.PullTransportPauseReason
                    : "tuna_activation_negotiating";
                context.TunaActivationBarrierStateSendSuppressedCount++;
                suppressedCount = context.TunaActivationBarrierStateSendSuppressedCount;
            }
        }

        if (pauseReason is null)
        {
            return false;
        }

        var eventName = string.Equals(frameKind, "frontier_request", StringComparison.Ordinal)
            ? "filetransfer_v6_frontier_request_deferred_for_tuna_activation_transport_pause"
            : "filetransfer_v6_receiver_state_deferred_for_tuna_activation_transport_pause";
        var transportEpoch = state?.TransportEpoch ?? request?.TransportEpoch ?? context.V6ReceiverTransportEpoch;
        var repairRequestId = state?.RepairRequestId ?? request?.RepairRequestId ?? "(none)";
        var recoveryMode = state?.RecoveryMode ?? request?.RecoveryMode ?? "(none)";
        var stateEpoch = state?.Epoch ?? 0;
        var committedChunkIndex = state?.ContiguousCommittedChunkIndex ?? context.NextChunkIndex;
        var highestReceivedChunkIndex = state?.DurableReceivedHighestChunkIndex ?? context.PullHighestReceivedChunkIndex;
        var missingRangeCount = state?.MissingRanges.Count ?? request?.MissingRanges.Count ?? 0;
        var requestedUntilChunkIndexExclusive = state?.CreditUntilChunkIndexExclusive ?? 0;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; frame_kind={FormatProtocolLogValue(frameKind)}; state_reason={FormatProtocolLogValue(reason)}; pause_reason={FormatProtocolLogValue(pauseReason)}; explicit_activation_unavailable={(explicitActivationUnavailable ? 1 : 0)}; suppressed_count={suppressedCount}; epoch={stateEpoch}; committed_chunk_index={committedChunkIndex}; highest_received_chunk_index={highestReceivedChunkIndex}; requested_until_chunk_index_exclusive={requestedUntilChunkIndexExclusive}; missing_range_count={missingRangeCount}; transport_epoch={transportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; recovery_mode={FormatProtocolLogValue(recoveryMode)}; bytes_transferred={context.BytesTransferred}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
        return true;
    }

    private bool TryDeferInboundV6ControlSendFailureForRecovery(
        InboundTransferContext context,
        string frameKind,
        string reason,
        Exception ex,
        bool requireRecoverableFailure = true)
    {
        if (requireRecoverableFailure &&
            !IsRecoverableV6DataSessionSendFailure(ex))
        {
            return false;
        }

        bool recoveryActive;
        bool postTunaFallbackSurvival;
        long transportEpoch;
        string recoveryMode;
        bool pullTransportPaused;
        bool resumeRequestPending;
        var replayGeneration = 0;
        var shouldScheduleReplay = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            var unresolvedEpoch = IsV6TransportEpochUnresolved(context.V6TransportEpoch);
            pullTransportPaused = context.PullTransportPaused;
            resumeRequestPending = context.PullTransportResumeRequestPending;
            postTunaFallbackSurvival = IsInboundV6PostTunaFallbackControlSendRecoveryPathLocked(context);
            recoveryActive =
                unresolvedEpoch ||
                pullTransportPaused ||
                resumeRequestPending ||
                postTunaFallbackSurvival;
            if (!recoveryActive)
            {
                return false;
            }

            transportEpoch = context.V6TransportEpoch?.EpochId ?? context.V6ReceiverTransportEpoch;
            recoveryMode = context.V6TransportEpoch is null
                ? postTunaFallbackSurvival
                    ? "post_tuna_fallback_survival"
                    : "(none)"
                : FormatV6TransportEpochState(context.V6TransportEpoch.State);
            if (postTunaFallbackSurvival)
            {
                shouldScheduleReplay = ArmInboundV6PostTunaFallbackProofReplayLocked(
                    context,
                    $"deferred_{frameKind}:{reason}",
                    out replayGeneration);
            }
        }

        var eventName = string.Equals(frameKind, "frontier_request", StringComparison.Ordinal)
            ? "filetransfer_v6_frontier_request_deferred_for_recovery"
            : "filetransfer_v6_receiver_state_deferred_for_recovery";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event={eventName}; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.RouteSelection.ProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; reason={FormatProtocolLogValue(reason)}; transport_epoch={transportEpoch}; recovery_mode={FormatProtocolLogValue(recoveryMode)}; frame_kind={FormatProtocolLogValue(frameKind)}; pull_transport_paused={(pullTransportPaused ? 1 : 0)}; resume_request_pending={(resumeRequestPending ? 1 : 0)}; post_tuna_fallback_survival={(postTunaFallbackSurvival ? 1 : 0)}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
        if (shouldScheduleReplay)
        {
            ScheduleInboundV6PostTunaFallbackProofReplay(context, replayGeneration, $"deferred_{frameKind}");
        }

        return true;
    }

    private bool ArmInboundV6PostTunaFallbackProofReplayLocked(
        InboundTransferContext context,
        string reason,
        out int replayGeneration)
    {
        replayGeneration = 0;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !IsInboundV6PostTunaFallbackControlSendRecoveryPathLocked(context) ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        var shouldStart = context.V6PostTunaFallbackProofReplayLoopGeneration <= 0;
        if (shouldStart)
        {
            context.V6PostTunaFallbackProofReplayGeneration++;
            context.V6PostTunaFallbackProofReplayLoopGeneration = context.V6PostTunaFallbackProofReplayGeneration;
            context.V6PostTunaFallbackProofReplayStartChunkIndex = context.NextChunkIndex;
            context.V6PostTunaFallbackProofReplayArmedUtc = DateTimeOffset.UtcNow;
        }

        replayGeneration = context.V6PostTunaFallbackProofReplayLoopGeneration;
        context.V6PostTunaFallbackProofReplayReceiverStatePending = true;
        context.V6PostTunaFallbackProofReplayFrontierRequestPending = true;
        context.V6PostTunaFallbackProofReplayTransportEpoch =
            context.V6TransportEpoch?.EpochId ?? context.V6ReceiverTransportEpoch;
        context.V6PostTunaFallbackProofReplayReason = NormalizeReason(reason) ?? "post_tuna_fallback_proof_replay";

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_proof_replay_armed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; replay_generation={replayGeneration}; transport_epoch={context.V6PostTunaFallbackProofReplayTransportEpoch}; start_chunk_index={context.V6PostTunaFallbackProofReplayStartChunkIndex}; current_chunk_index={context.NextChunkIndex}; reason={FormatProtocolLogValue(reason)}; loop_started={(shouldStart ? 1 : 0)}");
        return shouldStart;
    }

    private void ScheduleInboundV6PostTunaFallbackProofReplay(
        InboundTransferContext context,
        int replayGeneration,
        string reason)
    {
        if (replayGeneration <= 0)
        {
            return;
        }

        _ = RunInboundV6PostTunaFallbackProofReplayAsync(context, replayGeneration, reason);
    }

    private async Task RunInboundV6PostTunaFallbackProofReplayAsync(
        InboundTransferContext context,
        int replayGeneration,
        string reason)
    {
        var transferId = context.TransferId;
        var sessionId = context.SessionId;
        var retryIndex = 0;
        try
        {
            while (true)
            {
                var delayMs = retryIndex == 0
                    ? 250
                    : retryIndex - 1 < PullTransportRebindRetryDelaysMs.Length
                        ? PullTransportRebindRetryDelaysMs[retryIndex - 1]
                        : 5000;
                retryIndex++;
                await Task.Delay(delayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                bool sendReceiverState;
                bool sendFrontierRequest;
                int startChunkIndex;
                int currentChunkIndex;
                long transportEpoch;
                string replayReason;
                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) ||
                        context.IsTerminal ||
                        context.V6PostTunaFallbackProofReplayLoopGeneration != replayGeneration ||
                        !IsInboundV6PostTunaFallbackControlSendRecoveryPathLocked(context) ||
                        context.NextChunkIndex >= context.ChunkCount)
                    {
                        if (context.V6PostTunaFallbackProofReplayLoopGeneration == replayGeneration)
                        {
                            ClearInboundV6PostTunaFallbackProofReplayLocked(context);
                        }

                        return;
                    }

                    if (context.V6PostTunaFallbackProofReplayStartChunkIndex >= 0 &&
                        context.NextChunkIndex > context.V6PostTunaFallbackProofReplayStartChunkIndex)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v6_post_tuna_fallback_proof_replay_completed; direction=inbound; transfer_id={transferId}; session_id={sessionId}; replay_generation={replayGeneration}; reason=progress_observed; start_chunk_index={context.V6PostTunaFallbackProofReplayStartChunkIndex}; current_chunk_index={context.NextChunkIndex}");
                        ClearInboundV6PostTunaFallbackProofReplayLocked(context);
                        return;
                    }

                    sendReceiverState = context.V6PostTunaFallbackProofReplayReceiverStatePending;
                    sendFrontierRequest = context.V6PostTunaFallbackProofReplayFrontierRequestPending;
                    startChunkIndex = context.V6PostTunaFallbackProofReplayStartChunkIndex;
                    currentChunkIndex = context.NextChunkIndex;
                    transportEpoch = context.V6TransportEpoch?.EpochId ??
                                     context.V6PostTunaFallbackProofReplayTransportEpoch;
                    replayReason = context.V6PostTunaFallbackProofReplayReason ?? reason;
                }

                if (!sendReceiverState && !sendFrontierRequest)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(inboundTransfer, context) &&
                            context.V6PostTunaFallbackProofReplayLoopGeneration == replayGeneration)
                        {
                            context.V6PostTunaFallbackProofReplayReceiverStatePending = true;
                            context.V6PostTunaFallbackProofReplayFrontierRequestPending = true;
                            sendReceiverState = true;
                            sendFrontierRequest = true;
                        }
                    }
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_post_tuna_fallback_proof_replay; direction=inbound; transfer_id={transferId}; session_id={sessionId}; replay_generation={replayGeneration}; reason={FormatProtocolLogValue(replayReason)}; retry_delay_ms={delayMs}; transport_epoch={transportEpoch}; start_chunk_index={startChunkIndex}; current_chunk_index={currentChunkIndex}; receiver_state_pending={(sendReceiverState ? 1 : 0)}; frontier_request_pending={(sendFrontierRequest ? 1 : 0)}");

                var receiverStateSent = !sendReceiverState ||
                                        await SendInboundV6ReceiverStateAsync(
                                            context,
                                            "post_tuna_fallback_proof_replay",
                                            forceSend: true).ConfigureAwait(false);
                var frontierRequestSent = !sendFrontierRequest ||
                                          await SendInboundV6FrontierRequestAsync(
                                              context,
                                              "post_tuna_fallback_proof_replay",
                                              forceSend: true).ConfigureAwait(false);

                if (receiverStateSent || frontierRequestSent)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(inboundTransfer, context) &&
                            context.V6PostTunaFallbackProofReplayLoopGeneration == replayGeneration)
                        {
                            // Keep replaying until real data progress proves the peer consumed the
                            // checkpoint. A successful local send only means the bridge accepted it.
                            context.V6PostTunaFallbackProofReplayReceiverStatePending = true;
                            context.V6PostTunaFallbackProofReplayFrontierRequestPending = true;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) &&
                    context.V6PostTunaFallbackProofReplayLoopGeneration == replayGeneration &&
                    (context.IsTerminal || !IsInboundV6PostTunaFallbackControlSendRecoveryPathLocked(context)))
                {
                    ClearInboundV6PostTunaFallbackProofReplayLocked(context);
                }
            }
        }
    }

    private static void ClearInboundV6PostTunaFallbackProofReplayLocked(InboundTransferContext context)
    {
        context.V6PostTunaFallbackProofReplayLoopGeneration = 0;
        context.V6PostTunaFallbackProofReplayReceiverStatePending = false;
        context.V6PostTunaFallbackProofReplayFrontierRequestPending = false;
        context.V6PostTunaFallbackProofReplayStartChunkIndex = -1;
        context.V6PostTunaFallbackProofReplayTransportEpoch = 0;
        context.V6PostTunaFallbackProofReplayReason = null;
        context.V6PostTunaFallbackProofReplayArmedUtc = null;
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

        recoveryRequest = AttachFallbackLegAuthority(
            context,
            new FileTransferReceiveRecoveryRequest(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "sender_request_feedback_stalled"),
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

        recoveryRequest = AttachFallbackLegAuthority(
            context,
            new FileTransferReceiveRecoveryRequest(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "sender_request_feedback_stalled"),
            "sender_request_feedback_stalled");
        return true;
    }

    private bool TrySuppressOutboundV6RecoveredRegularNknFeedbackRecoveryLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string reason)
    {
        if (!ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportLiveRouteEpochId,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn,
                reason))
        {
            return false;
        }

        context.V6LastReceiveRecoveryRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.PullTransportResumeRequestPending = false;
        context.PullSenderFeedCreditWaitStartedUtc = null;
        context.V4SenderCreditExhaustedSinceUtc = null;
        context.V6SenderPumpLastWakeReason = "recovered_regular_nkn_epoch_restart_suppressed";
        TryQueueOutboundV6PostTunaFallbackSenderSelfRepairLocked(context, now, reason);
        return true;
    }

    private bool TryQueueOutboundV6PostTunaFallbackSenderSelfRepairLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        string reason)
    {
        if (!IsStrictFallbackFrontierRuntime(context) ||
            context.RemoteNextExpectedChunkIndex >= context.ChunkCount ||
            context.UserPaused ||
            context.PeerPaused ||
            context.PullTransportPaused ||
            IsV6TransportEpochUnresolved(context.V6TransportEpoch))
        {
            return false;
        }

        var frontierChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount - 1));
        if (!TryGetOutboundFallbackFrontierPeerRepairLocked(context, out var fallbackContract, out var authorityRejectionReason) ||
            fallbackContract is null)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_frontier_repair_authority_rejected; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id=(none); direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; transport_epoch={context.CurrentTransferLeg?.TransportEpochId ?? 0}; repair_request_id=(none); decision_reason={FormatProtocolLogValue(authorityRejectionReason)}; source=post_tuna_fallback_sender_frontier_rescue");
            return false;
        }

        context.V6PostTunaFallbackSenderSelfRepairSequence++;
        context.V6PostTunaFallbackSenderSelfRepairChunkIndex = frontierChunkIndex;
        context.V6PostTunaFallbackSenderSelfRepairUtc = now;
        var metadata = new V6OutboundChunkRequestMetadata(
            fallbackContract.Authority.CanonicalRepairKey,
            Priority: true,
            TransportEpoch: fallbackContract.Authority.TransportEpoch,
            RepairRequestId: fallbackContract.Authority.ProtocolRepairRequestId,
            PriorityName: fallbackContract.Authority.Priority,
            RecoveryMode: fallbackContract.Authority.RecoveryMode,
            FallbackFrontierRepairContract: fallbackContract);
        var queuedChunkCount = 0;
        var inFlightSkippedChunkCount = 0;
        var alreadyQueuedChunkCount = 0;
        foreach (var chunkIndex in fallbackContract.Identity.Ranges.SelectMany(
                     range => Enumerable.Range(range.StartChunkIndex, range.ChunkCount)))
        {
            if (chunkIndex < context.RemoteNextExpectedChunkIndex ||
                IsOutboundV6ChunkBlockedByTransportEpochLocked(context, chunkIndex, metadata))
            {
                continue;
            }

            if (context.V6ChunkSendsInFlight.ContainsKey(chunkIndex))
            {
                inFlightSkippedChunkCount++;
                continue;
            }

            if (context.V6PriorityRequestedChunks.Contains(chunkIndex))
            {
                alreadyQueuedChunkCount++;
            }

            context.V6RequestedChunkMetadataByChunkIndex[chunkIndex] = metadata;
            context.V6NormalRequestedChunks.Remove(chunkIndex);
            context.V6PriorityRequestedChunks.Add(chunkIndex);
            queuedChunkCount++;
        }

        if (queuedChunkCount <= 0)
        {
            return false;
        }

        ActivateOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(context, reason);
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_sender_frontier_rescue";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued; {FormatFallbackFrontierRepairAuthorityTuple(fallbackContract.Authority)}; reason={FormatProtocolLogValue(reason)}; repair_request_id={FormatProtocolLogValue(fallbackContract.Authority.ProtocolRepairRequestId)}; queued_chunk_count={queuedChunkCount}; already_queued_chunk_count={alreadyQueuedChunkCount}; in_flight_skipped_chunk_count={inFlightSkippedChunkCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}");
        return true;
    }

    private static void ClearOutboundV6PostTunaFallbackSenderSelfRepairLocked(OutboundTransferContext context)
    {
        context.V6PostTunaFallbackSenderSelfRepairChunkIndex = -1;
        context.V6PostTunaFallbackSenderSelfRepairUtc = null;
    }

    private static bool IsOutboundV6PostTunaFallbackNormalSendAheadFreezeActiveLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex >= 0 &&
           context.RemoteNextExpectedChunkIndex <= context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex;

    private static void ActivateOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RemoteNextExpectedChunkIndex >= context.ChunkCount)
        {
            return;
        }

        var frontierChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount - 1));
        if (context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex == frontierChunkIndex)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_already_active; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; reason={FormatProtocolLogValue(reason)}; frontier_chunk_index={frontierChunkIndex}; send_ahead_limit_chunks={V6PostTunaFallbackNormalSendAheadFreezeChunks}");
            return;
        }

        context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex = frontierChunkIndex;
        context.V6PostTunaFallbackNormalSendAheadFreezeUtc = DateTimeOffset.UtcNow;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; reason={FormatProtocolLogValue(reason)}; frontier_chunk_index={frontierChunkIndex}; send_ahead_limit_chunks={V6PostTunaFallbackNormalSendAheadFreezeChunks}");
    }

    private static void MaybeReleaseOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
        OutboundTransferContext context,
        int previousRemoteFrontier,
        string reason)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex < 0 ||
            context.RemoteNextExpectedChunkIndex <= context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex)
        {
            return;
        }

        var freezeFrontier = context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex;
        var startedUtc = context.V6PostTunaFallbackNormalSendAheadFreezeUtc;
        context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex = -1;
        context.V6PostTunaFallbackNormalSendAheadFreezeUtc = null;
        var activeMs = startedUtc is null
            ? 0
            : (long)Math.Max(0, (DateTimeOffset.UtcNow - startedUtc.Value).TotalMilliseconds);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_released; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; reason={FormatProtocolLogValue(reason)}; freeze_frontier_chunk_index={freezeFrontier}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; active_ms={activeMs}");
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
        if (IsInboundV6PostTunaFallbackSurvivalPathLocked(context))
        {
            return ResolveInboundV6PostTunaFallbackFrontierRescueChunksLocked(context);
        }

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

    private static bool IsInboundV6PostTunaFallbackSurvivalPathLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable &&
           context.V6TransportEpoch is not { TargetTransport: FileTransferTransportKind.Tuna };

    private static bool IsInboundV6PostTunaFallbackControlSendRecoveryPathLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.V6TransportEpoch is not { TargetTransport: FileTransferTransportKind.Tuna };

    private static int ResolveInboundV6PostTunaFallbackFrontierRescueChunksLocked(InboundTransferContext context)
    {
        if (context.V6PostTunaFallbackFrontierRescueChunkIndex != context.NextChunkIndex)
        {
            context.V6PostTunaFallbackFrontierRescueChunkIndex = context.NextChunkIndex;
            context.V6PostTunaFallbackFrontierRescueStep = 0;
            context.V6PostTunaFallbackFrontierRescueRequestCount = 0;
            context.V6PostTunaFallbackFrontierRescueStartedUtc = DateTimeOffset.UtcNow;
        }

        var configuredMaxChunks = context.V6PostTunaFallbackFrontierRescueStep switch
        {
            <= 0 => V6PostTunaFallbackFrontierRescueExactChunks,
            1 => V6PostTunaFallbackFrontierRescueStep1Chunks,
            2 => V6PostTunaFallbackFrontierRescueStep2Chunks,
            _ => V6PostTunaFallbackFrontierRescueStep3Chunks,
        };
        var remainingChunks = Math.Max(0, context.ChunkCount - context.NextChunkIndex);
        return Math.Max(1, Math.Min(configuredMaxChunks, remainingChunks));
    }

    private static int AdvanceInboundV6PostTunaFallbackFrontierRescueLocked(
        InboundTransferContext context,
        FallbackFrontierRepairContract? contract = null)
    {
        context.V6PostTunaFallbackFrontierRescueRequestCount++;
        if (context.V6PostTunaFallbackFrontierRescueRequestCount > 1 &&
            context.V6PostTunaFallbackFrontierRescueRequestCount % V6PostTunaFallbackFrontierRescueRequestsBeforeWiden == 0 &&
            context.V6PostTunaFallbackFrontierRescueStep < 3)
        {
            var previousStep = context.V6PostTunaFallbackFrontierRescueStep;
            context.V6PostTunaFallbackFrontierRescueStep++;
            var authority = contract?.Authority;
            var authoritySuffix = authority is { } issued
                ? $"; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(issued.AuthorityId.ToString())}; predecessor_authority_id={FormatProtocolLogValue(issued.PredecessorAuthorityId?.ToString() ?? "(none)")}; protocol_version={issued.ProtocolVersion}; live_route_epoch={issued.LiveRouteEpoch}; runtime_generation={issued.RuntimeGeneration}; terminal_generation={issued.TerminalGeneration}; leg_generation={issued.LegGeneration}; transport_epoch={issued.TransportEpoch}; bridge_recovery_generation={issued.BridgeRecoveryGeneration}; bridge_decision_generation={issued.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(issued.CheckpointRequestId)}; route_publication_generation={issued.RoutePublicationGeneration}; route_commit_generation={issued.RouteCommitGeneration ?? 0}; repair_generation={issued.DecisionGeneration}; repair_request_key={FormatProtocolLogValue(issued.CanonicalRepairKey)}; range_fingerprint={FormatProtocolLogValue(issued.RangeFingerprint)}; request_correlation_id={FormatProtocolLogValue(issued.RequestCorrelationId)}"
                : string.Empty;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}{authoritySuffix}; frontier_chunk_index={context.V6PostTunaFallbackFrontierRescueChunkIndex}; previous_rescue_step={previousStep}; rescue_step={context.V6PostTunaFallbackFrontierRescueStep}; rescue_request_count={context.V6PostTunaFallbackFrontierRescueRequestCount}");
        }

        return context.V6PostTunaFallbackFrontierRescueRequestCount;
    }

    private static void ResetInboundV6PostTunaFallbackFrontierRescueLocked(InboundTransferContext context)
    {
        context.V6PostTunaFallbackFrontierRescueChunkIndex = -1;
        context.V6PostTunaFallbackFrontierRescueStep = 0;
        context.V6PostTunaFallbackFrontierRescueRequestCount = 0;
        context.V6PostTunaFallbackFrontierRescueStartedUtc = null;
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
            ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
            ResetInboundV6FrontierStallGraceLocked(context);
            return true;
        }

        if (HasInboundV6SparseFrontierGapLocked(context) &&
            ShouldDeferInboundV6FrontierStallForRecentCommittedProgressLocked(context, now))
        {
            ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked(context, now);
            ResetInboundV6FrontierStallGraceLocked(context);
            return false;
        }

        if (!IsInboundV6FrontierStalledLocked(context, now))
        {
            ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
            ResetInboundV6FrontierStallGraceLocked(context);
            return false;
        }

        if (context.V6FrontierStallStartedUtc is null ||
            context.V6FrontierStallChunkIndex != context.NextChunkIndex)
        {
            var initialStallGrace = ResolveInboundV6FrontierRequestStallGraceLocked(
                context,
                unresolvedFrontierProofRequest);
            context.V6FrontierStallStartedUtc = now;
            context.V6FrontierStallChunkIndex = context.NextChunkIndex;
            context.V6FrontierStallLastDeferredLogUtc = now;
            ArmInboundV6FrontierStallGraceWakeLocked(context, now, initialStallGrace);
            LogInboundV6FrontierRequestDeferred(
                context,
                now,
                0,
                reason,
                initialStallGrace);
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
        context.CurrentV6FrontierStallGraceWake = null;
        context.V6FrontierStallGraceWakeScheduledGeneration = 0;
    }

    private static void ArmInboundV6FrontierStallGraceWakeLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        TimeSpan stallGrace)
    {
        if (!IsStrictFallbackFrontierRuntime(context) ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentRuntimeOwner is not { IsDefault: false } runtimeOwner ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            context.DataSessionGeneration <= 0)
        {
            return;
        }

        var wakeGeneration = context.LastV6FrontierStallGraceWakeGeneration < long.MaxValue
            ? context.LastV6FrontierStallGraceWakeGeneration + 1
            : long.MaxValue;
        context.LastV6FrontierStallGraceWakeGeneration = wakeGeneration;
        context.V6FrontierStallGraceWakeScheduledGeneration = 0;
        context.CurrentV6FrontierStallGraceWake = new InboundV6FrontierStallGraceWake(
            wakeGeneration,
            context.SessionId,
            context.TransferId,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex,
            now,
            now + stallGrace,
            context.RouteSelection.Route,
            context.RouteSelection.TelemetryToken,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentLiveRouteEpoch?.State,
            context.CurrentLiveRouteEpoch?.RouteSelection,
            context.CurrentLiveRouteEpoch?.RoutePublicationToken,
            context.CurrentLiveRouteEpoch?.HandoffKind,
            context.CurrentLiveRouteEpoch?.TargetTransport,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration ?? 0,
            publication,
            runtimeOwner.RuntimeGeneration,
            context.TerminalGeneration,
            runtimeOwner,
            leg.Generation,
            leg.LegId,
            leg.State,
            leg.CanSendData,
            leg.RouteSelection,
            leg.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0,
            leg.CheckpointRequestId ?? "(none)",
            leg.CheckpointGeneration,
            dataSession,
            CreateReceiveRecoveryDataSessionIdentity(
                context.TransferId,
                dataSession,
                context.DataSessionGeneration),
            context.DataSessionGeneration)
        {
            CheckpointContinuitySource = FileTransferCoordinator.CreateCheckpointContinuitySourceStamp(
                CreateCoordinatorStateLocked(context)),
        };
        context.FrontierPublicationObservation?.StallArm(CreateCoordinatorStateLocked(context), dataSession,
            wakeGeneration, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, now, now + stallGrace);
        context.FrontierBoundaryObservation?.Stall(context);
    }

    private bool RefreshInboundV6FrontierStallGraceWakeForHighestProgressLocked(InboundTransferContext context)
    {
        var current = context.CurrentV6FrontierStallGraceWake;
        if (current is null ||
            context.PullHighestReceivedChunkIndex <= current.HighestReceivedChunkIndex ||
            context.LastV6FrontierStallGraceWakeGeneration == long.MaxValue ||
            !IsInboundV6FrontierStallGraceWakeScopeCurrentLocked(context, current, true, out _))
        {
            return false;
        }

        // Accepted sparse writes may advance the tail without closing the gap.
        // Keep the exact owner and deadline; a stale timer cannot create this successor.
        var successor = current with
        {
            WakeGeneration = ++context.LastV6FrontierStallGraceWakeGeneration,
            HighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex,
        };
        context.CurrentV6FrontierStallGraceWake = successor;
        context.FrontierBoundaryObservation?.Successor(context, current, successor);
        context.V6FrontierStallGraceWakeScheduledGeneration = 0;
        LogInboundV6FrontierStallGraceRecheckDecision(successor, "rearmed", "highest_received_advanced");
        return true;
    }

    private void SchedulePendingInboundV6FrontierStallGraceWake(InboundTransferContext context)
    {
        InboundV6FrontierStallGraceWake? wake;
        lock (gate)
        {
            wake = context.CurrentV6FrontierStallGraceWake;
            if (wake is null ||
                context.V6FrontierStallGraceWakeScheduledGeneration == wake.WakeGeneration)
            {
                return;
            }

            context.V6FrontierStallGraceWakeScheduledGeneration = wake.WakeGeneration;
        }

        _ = RunInboundV6FrontierStallGraceWakeAsync(context, wake);
    }

    private async Task RunInboundV6FrontierStallGraceWakeAsync(
        InboundTransferContext context,
        InboundV6FrontierStallGraceWake wake)
    {
        try
        {
            var delay = wake.DueUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, context.LifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            return;
        }

        string decisionStatus;
        string decisionReason;
        lock (gate)
        {
            if (!IsInboundV6FrontierStallGraceWakeCurrentLocked(context, wake, out decisionReason))
            {
                decisionStatus = "stale";
            }
            else if (DateTimeOffset.UtcNow < wake.DueUtc)
            {
                context.V6FrontierStallGraceWakeScheduledGeneration = 0;
                decisionStatus = "rearmed";
                decisionReason = "stall_grace_deadline_pending";
            }
            else
            {
                decisionStatus = "scheduled";
                decisionReason = "stall_grace_elapsed";
            }
        }

        if (decisionStatus == "rearmed")
        {
            LogInboundV6FrontierStallGraceRecheckDecision(wake, decisionStatus, decisionReason);
            SchedulePendingInboundV6FrontierStallGraceWake(context);
            return;
        }

        if (decisionStatus != "scheduled")
        {
            LogInboundV6FrontierStallGraceRecheckDecision(wake, decisionStatus, decisionReason);
            return;
        }

        var sent = await SendInboundV6FrontierRequestAsync(
                context,
                "frontier_stall_grace_elapsed",
                forceSend: true)
            .ConfigureAwait(false);
        InboundV6FrontierStallGraceWake? rearmedWake = null;
        lock (gate)
        {
            if (ReferenceEquals(context.CurrentV6FrontierStallGraceWake, wake))
            {
                if (!sent &&
                    context.V6LastFrontierRequestSentUtc is { } lastSentUtc &&
                    context.V6LastFrontierRequestChunkIndex == wake.FrontierChunkIndex)
                {
                    var retryDueUtc = lastSentUtc + ResolveInboundV6FrontierRequestRetryIntervalLocked(
                        context,
                        unresolvedFrontierProofRequest: false);
                    if (retryDueUtc > DateTimeOffset.UtcNow)
                    {
                        var nextGeneration = context.LastV6FrontierStallGraceWakeGeneration < long.MaxValue
                            ? context.LastV6FrontierStallGraceWakeGeneration + 1
                            : long.MaxValue;
                        context.LastV6FrontierStallGraceWakeGeneration = nextGeneration;
                        rearmedWake = wake with
                        {
                            WakeGeneration = nextGeneration,
                            DueUtc = retryDueUtc,
                        };
                        context.CurrentV6FrontierStallGraceWake = rearmedWake;
                        context.V6FrontierStallGraceWakeScheduledGeneration = 0;
                    }
                }

                if (rearmedWake is null)
                {
                    context.CurrentV6FrontierStallGraceWake = null;
                    context.V6FrontierStallGraceWakeScheduledGeneration = 0;
                }
            }
        }

        if (rearmedWake is not null)
        {
            LogInboundV6FrontierStallGraceRecheckDecision(
                rearmedWake,
                "rearmed",
                "same_frontier_retry_interval_pending");
            SchedulePendingInboundV6FrontierStallGraceWake(context);
            return;
        }

        LogInboundV6FrontierStallGraceRecheckDecision(
            wake,
            sent ? "dispatched" : "coalesced",
            sent ? "exact_receiver_request_dispatched" : "exact_receiver_request_not_dispatched");
    }

    private bool IsInboundV6FrontierStallGraceWakeCurrentLocked(
        InboundTransferContext context,
        InboundV6FrontierStallGraceWake wake,
        out string reason)
        => IsInboundV6FrontierStallGraceWakeScopeCurrentLocked(context, wake, false, out reason);

    private bool IsInboundV6FrontierStallGraceWakeScopeCurrentLocked(
        InboundTransferContext context,
        InboundV6FrontierStallGraceWake wake,
        bool acceptedSparseWrite,
        out string reason)
    {
        if (!ReferenceEquals(context.CurrentV6FrontierStallGraceWake, wake) ||
            (context.V6FrontierStallGraceWakeScheduledGeneration != wake.WakeGeneration &&
                (!acceptedSparseWrite || context.V6FrontierStallGraceWakeScheduledGeneration != 0)))
        {
            reason = "wake_generation_replaced";
            return false;
        }

        if (context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            !ReferenceEquals(inboundTransfer, context) ||
            !IsStrictFallbackFrontierRuntime(context) ||
            !context.PullManifestReceived ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            reason = context.IsTerminal
                ? "transfer_terminal"
                : context.UserPaused || context.PeerPaused
                    ? "transfer_paused"
                    : "source_not_current";
            return false;
        }

        if (!string.Equals(context.SessionId, wake.SessionId, StringComparison.Ordinal) ||
            !string.Equals(context.TransferId, wake.TransferId, StringComparison.Ordinal) ||
            context.NextChunkIndex != wake.FrontierChunkIndex ||
            (acceptedSparseWrite
                ? context.PullHighestReceivedChunkIndex < wake.HighestReceivedChunkIndex
                : context.PullHighestReceivedChunkIndex != wake.HighestReceivedChunkIndex) ||
            context.V6FrontierStallStartedUtc != wake.StallStartedUtc ||
            context.V6FrontierStallChunkIndex != wake.FrontierChunkIndex ||
            !IsInboundV6FrontierStalledLocked(context, DateTimeOffset.UtcNow))
        {
            reason = "frontier_scope_changed";
            return false;
        }

        if (context.RouteSelection.Route != wake.Route ||
            !string.Equals(context.RouteSelection.TelemetryToken, wake.RouteToken, StringComparison.Ordinal) ||
            context.NegotiatedDataProtocolVersion != wake.ProtocolVersion ||
            !string.Equals(context.CurrentLiveRouteEpoch?.State, wake.RouteEpochState, StringComparison.Ordinal) ||
            context.CurrentLiveRouteEpoch?.RouteSelection != wake.RouteEpochSelection ||
            context.CurrentLiveRouteEpoch?.RoutePublicationToken != wake.RouteEpochPublication ||
            context.CurrentLiveRouteEpoch?.HandoffKind != wake.RouteEpochHandoffKind ||
            context.CurrentLiveRouteEpoch?.TargetTransport != wake.RouteEpochTargetTransport ||
            (context.CurrentLiveRouteEpoch?.EpochId ?? 0) != wake.LiveRouteEpoch ||
            context.CurrentRoutePublicationToken != wake.RoutePublication ||
            context.CurrentRoutePublicationToken?.PublicationGeneration != wake.RoutePublicationGeneration ||
            (context.CurrentRoutePublicationToken?.RouteCommitGeneration ?? 0) != wake.RouteCommitGeneration ||
            context.CurrentRuntimeOwner != wake.RuntimeOwner ||
            context.CurrentRuntimeOwner?.RuntimeGeneration != wake.RuntimeGeneration ||
            context.TerminalGeneration != wake.TerminalGeneration ||
            !string.Equals(context.CurrentTransferLeg?.LegId, wake.LegId, StringComparison.Ordinal) ||
            context.CurrentTransferLeg?.State != wake.LegState ||
            context.CurrentTransferLeg?.CanSendData != wake.LegCanSendData ||
            context.CurrentTransferLeg?.RouteSelection != wake.LegRouteSelection ||
            context.CurrentTransferLeg?.ProtocolVersion != wake.LegProtocolVersion ||
            context.CurrentTransferLeg?.LiveRouteEpochId != wake.LegLiveRouteEpoch ||
            context.CurrentTransferLeg?.Generation != wake.LegGeneration ||
            context.CurrentTransferLeg?.TransportEpochId != wake.TransportEpoch ||
            context.CurrentTransferLeg?.BridgeRecoveryGeneration != wake.BridgeRecoveryGeneration ||
            (context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0) != wake.BridgeDecisionGeneration ||
            !string.Equals(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)", wake.CheckpointRequestId, StringComparison.Ordinal) ||
            context.CurrentTransferLeg?.CheckpointGeneration != wake.CheckpointGeneration)
        {
            reason = "route_scope_changed";
            return false;
        }

        if (!ReferenceEquals(context.DataSession, wake.DataSession) ||
            context.DataSessionGeneration != wake.DataSessionGeneration ||
            !string.Equals(
                CreateReceiveRecoveryDataSessionIdentity(
                    context.TransferId,
                    context.DataSession,
                    context.DataSessionGeneration),
                wake.DataSessionIdentity,
                StringComparison.Ordinal))
        {
            reason = "data_session_changed";
            return false;
        }

        reason = "current";
        return true;
    }

    private static bool HasInboundV6SparseFrontierGapLocked(InboundTransferContext context)
        => context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable &&
           context.NextChunkIndex < context.ChunkCount &&
           context.PullHighestReceivedChunkIndex >= context.NextChunkIndex &&
           !IsInboundV6ChunkPresentOrPendingLocked(context, context.NextChunkIndex);

    private static void ResetInboundV6FrontierRecentProgressGraceWakeLocked(InboundTransferContext context)
    {
        context.CurrentV6FrontierRecentProgressGraceWake = null;
        context.V6FrontierRecentProgressGraceWakeScheduledGeneration = 0;
    }

    private static void ArmOrClearInboundV6FrontierRecentProgressGraceWakeLocked(
        InboundTransferContext context,
        DateTimeOffset now)
    {
        if (!IsStrictFallbackFrontierRuntime(context) ||
            !HasInboundV6SparseFrontierGapLocked(context) ||
            context.PullLastCommittedProgressUtc is not { } committedProgressUtc ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.CurrentRuntimeOwner is not { IsDefault: false } runtimeOwner ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession ||
            context.DataSessionGeneration <= 0)
        {
            ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
            return;
        }

        var current = context.CurrentV6FrontierRecentProgressGraceWake;
        if (current is not null &&
            current.FrontierChunkIndex == context.NextChunkIndex &&
            current.HighestReceivedChunkIndex == context.PullHighestReceivedChunkIndex &&
            current.CommittedProgressUtc == committedProgressUtc &&
            current.Route == context.RouteSelection.Route &&
            string.Equals(current.RouteToken, context.RouteSelection.TelemetryToken, StringComparison.Ordinal) &&
            current.ProtocolVersion == context.NegotiatedDataProtocolVersion &&
            current.LiveRouteEpoch == (context.CurrentLiveRouteEpoch?.EpochId ?? 0) &&
            current.RoutePublication == publication &&
            current.RuntimeOwner == runtimeOwner &&
            string.Equals(current.RouteEpochState, context.CurrentLiveRouteEpoch?.State, StringComparison.Ordinal) &&
            current.RouteEpochSelection == context.CurrentLiveRouteEpoch?.RouteSelection &&
            current.RouteEpochPublication == context.CurrentLiveRouteEpoch?.RoutePublicationToken &&
            current.RouteEpochHandoffKind == context.CurrentLiveRouteEpoch?.HandoffKind &&
            current.RouteEpochTargetTransport == context.CurrentLiveRouteEpoch?.TargetTransport &&
            string.Equals(current.LegId, leg.LegId, StringComparison.Ordinal) &&
            current.LegGeneration == leg.Generation &&
            current.LegState == leg.State &&
            current.LegCanSendData == leg.CanSendData &&
            current.LegRouteSelection == leg.RouteSelection &&
            current.LegProtocolVersion == leg.ProtocolVersion &&
            current.LegLiveRouteEpoch == leg.LiveRouteEpochId &&
            current.TransportEpoch == leg.TransportEpochId &&
            current.BridgeRecoveryGeneration == leg.BridgeRecoveryGeneration &&
            current.BridgeDecisionGeneration == (context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0) &&
            string.Equals(current.CheckpointRequestId, leg.CheckpointRequestId ?? "(none)", StringComparison.Ordinal) &&
            current.CheckpointGeneration == leg.CheckpointGeneration &&
            current.TerminalGeneration == context.TerminalGeneration &&
            ReferenceEquals(current.DataSession, dataSession) &&
            current.DataSessionGeneration == context.DataSessionGeneration)
        {
            return;
        }

        // An unchanged wake owns its due recheck. A concurrent sparse write or
        // state send must not retire it merely because the deadline elapsed.
        if (now >= committedProgressUtc + CurrentV6RegularNknFrontierRequestProgressGrace)
        {
            ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
            return;
        }

        var wakeGeneration = context.LastV6FrontierRecentProgressGraceWakeGeneration < long.MaxValue
            ? context.LastV6FrontierRecentProgressGraceWakeGeneration + 1
            : long.MaxValue;
        context.LastV6FrontierRecentProgressGraceWakeGeneration = wakeGeneration;
        context.V6FrontierRecentProgressGraceWakeScheduledGeneration = 0;
        var wake = new InboundV6FrontierRecentProgressGraceWake(
            wakeGeneration,
            context.SessionId,
            context.TransferId,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex,
            committedProgressUtc,
            committedProgressUtc + CurrentV6RegularNknFrontierRequestProgressGrace,
            context.RouteSelection.Route,
            context.RouteSelection.TelemetryToken,
            context.NegotiatedDataProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentLiveRouteEpoch?.State,
            context.CurrentLiveRouteEpoch?.RouteSelection,
            context.CurrentLiveRouteEpoch?.RoutePublicationToken,
            context.CurrentLiveRouteEpoch?.HandoffKind,
            context.CurrentLiveRouteEpoch?.TargetTransport,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration ?? 0,
            publication,
            runtimeOwner.RuntimeGeneration,
            context.TerminalGeneration,
            runtimeOwner,
            leg.Generation,
            leg.LegId,
            leg.State,
            leg.CanSendData,
            leg.RouteSelection,
            leg.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0,
            leg.CheckpointRequestId ?? "(none)",
            leg.CheckpointGeneration,
            dataSession,
            CreateReceiveRecoveryDataSessionIdentity(
                context.TransferId,
                dataSession,
                context.DataSessionGeneration),
            context.DataSessionGeneration);
        context.CurrentV6FrontierRecentProgressGraceWake = wake;
        context.FrontierPublicationObservation?.RecentArm(CreateCoordinatorStateLocked(context), dataSession,
            wakeGeneration, wake.FrontierChunkIndex, wake.HighestReceivedChunkIndex, committedProgressUtc, wake.DueUtc);
        context.FrontierBoundaryObservation?.Recent(context, wake);
        LogInboundV6FrontierRecentProgressGraceDeferred(
            wake,
            current is null ? "held" : "rearmed",
            current is null ? "recent_progress_grace_current" : "highest_received_advanced",
            now);
    }

    private void SchedulePendingInboundV6FrontierRecentProgressGraceWake(InboundTransferContext context)
    {
        InboundV6FrontierRecentProgressGraceWake? wake;
        lock (gate)
        {
            wake = context.CurrentV6FrontierRecentProgressGraceWake;
            if (wake is null ||
                context.V6FrontierRecentProgressGraceWakeScheduledGeneration == wake.WakeGeneration)
            {
                return;
            }

            context.V6FrontierRecentProgressGraceWakeScheduledGeneration = wake.WakeGeneration;
        }

        _ = RunInboundV6FrontierRecentProgressGraceWakeAsync(context, wake);
    }

    private async Task RunInboundV6FrontierRecentProgressGraceWakeAsync(
        InboundTransferContext context,
        InboundV6FrontierRecentProgressGraceWake wake)
    {
        try
        {
            var delay = wake.DueUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, context.LifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            return;
        }

        string decisionStatus;
        string decisionReason;
        lock (gate)
        {
            if (!IsInboundV6FrontierRecentProgressGraceWakeCurrentLocked(context, wake, out decisionReason))
            {
                decisionStatus = "stale";
            }
            else if (DateTimeOffset.UtcNow < wake.DueUtc)
            {
                context.V6FrontierRecentProgressGraceWakeScheduledGeneration = 0;
                decisionStatus = "rearmed";
                decisionReason = "recent_progress_deadline_pending";
            }
            else
            {
                context.CurrentV6FrontierRecentProgressGraceWake = null;
                context.V6FrontierRecentProgressGraceWakeScheduledGeneration = 0;
                decisionStatus = "rechecked";
                decisionReason = "recent_progress_grace_elapsed";
            }
        }

        if (decisionStatus == "rearmed")
        {
            LogInboundV6FrontierRecentProgressGraceRecheckDecision(wake, decisionStatus, decisionReason);
            SchedulePendingInboundV6FrontierRecentProgressGraceWake(context);
            return;
        }

        if (decisionStatus != "rechecked")
        {
            LogInboundV6FrontierRecentProgressGraceRecheckDecision(wake, decisionStatus, decisionReason);
            return;
        }

        var sent = await SendInboundV6FrontierRequestAsync(
                context,
                "frontier_recent_progress_grace_elapsed",
                forceSend: true)
            .ConfigureAwait(false);
        // Test-only inter-gate race: the original root diagnostic is already
        // emitted, but the recent recheck has not yet bound its current tip.
        context.FrontierBoundaryObservation?.AfterStallForTesting?.Invoke();
        bool stallGraceArmed;
        lock (gate)
        {
            stallGraceArmed =
                ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.V6FrontierStallChunkIndex == wake.FrontierChunkIndex &&
                context.CurrentV6FrontierStallGraceWake is not null;
            if (!sent && stallGraceArmed)
            {
                context.FrontierPublicationObservation?.RecentRechecked(wake.WakeGeneration,
                    context.CurrentV6FrontierStallGraceWake!.WakeGeneration);
                context.FrontierBoundaryObservation?.Rechecked(context, wake);
            }
        }

        LogInboundV6FrontierRecentProgressGraceRecheckDecision(
            wake,
            "rechecked",
            sent
                ? "exact_receiver_request_dispatched"
                : stallGraceArmed
                    ? "stall_grace_armed"
                    : "exact_receiver_request_not_dispatched");
    }

    private bool IsInboundV6FrontierRecentProgressGraceWakeCurrentLocked(
        InboundTransferContext context,
        InboundV6FrontierRecentProgressGraceWake wake,
        out string reason)
    {
        if (!ReferenceEquals(context.CurrentV6FrontierRecentProgressGraceWake, wake) ||
            context.V6FrontierRecentProgressGraceWakeScheduledGeneration != wake.WakeGeneration)
        {
            reason = "wake_generation_replaced";
            return false;
        }

        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            !context.PullManifestReceived ||
            !IsStrictFallbackFrontierRuntime(context) ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            reason = context.IsTerminal
                ? "transfer_terminal"
                : context.UserPaused || context.PeerPaused
                    ? "transfer_paused"
                    : "source_not_current";
            return false;
        }

        if (!string.Equals(context.SessionId, wake.SessionId, StringComparison.Ordinal) ||
            !string.Equals(context.TransferId, wake.TransferId, StringComparison.Ordinal) ||
            context.NextChunkIndex != wake.FrontierChunkIndex ||
            context.PullHighestReceivedChunkIndex != wake.HighestReceivedChunkIndex ||
            context.PullLastCommittedProgressUtc != wake.CommittedProgressUtc ||
            !HasInboundV6SparseFrontierGapLocked(context))
        {
            reason = context.NextChunkIndex > wake.FrontierChunkIndex
                ? "frontier_progressed"
                : "frontier_scope_changed";
            return false;
        }

        if (context.RouteSelection.Route != wake.Route ||
            !string.Equals(context.RouteSelection.TelemetryToken, wake.RouteToken, StringComparison.Ordinal) ||
            context.NegotiatedDataProtocolVersion != wake.ProtocolVersion ||
            !string.Equals(context.CurrentLiveRouteEpoch?.State, wake.RouteEpochState, StringComparison.Ordinal) ||
            context.CurrentLiveRouteEpoch?.RouteSelection != wake.RouteEpochSelection ||
            context.CurrentLiveRouteEpoch?.RoutePublicationToken != wake.RouteEpochPublication ||
            context.CurrentLiveRouteEpoch?.HandoffKind != wake.RouteEpochHandoffKind ||
            context.CurrentLiveRouteEpoch?.TargetTransport != wake.RouteEpochTargetTransport ||
            (context.CurrentLiveRouteEpoch?.EpochId ?? 0) != wake.LiveRouteEpoch ||
            context.CurrentRoutePublicationToken != wake.RoutePublication ||
            context.CurrentRoutePublicationToken?.PublicationGeneration != wake.RoutePublicationGeneration ||
            (context.CurrentRoutePublicationToken?.RouteCommitGeneration ?? 0) != wake.RouteCommitGeneration ||
            context.CurrentRuntimeOwner != wake.RuntimeOwner ||
            context.CurrentRuntimeOwner?.RuntimeGeneration != wake.RuntimeGeneration ||
            context.TerminalGeneration != wake.TerminalGeneration ||
            !string.Equals(context.CurrentTransferLeg?.LegId, wake.LegId, StringComparison.Ordinal) ||
            context.CurrentTransferLeg?.Generation != wake.LegGeneration ||
            context.CurrentTransferLeg?.State != wake.LegState ||
            context.CurrentTransferLeg?.CanSendData != wake.LegCanSendData ||
            context.CurrentTransferLeg?.RouteSelection != wake.LegRouteSelection ||
            context.CurrentTransferLeg?.ProtocolVersion != wake.LegProtocolVersion ||
            context.CurrentTransferLeg?.LiveRouteEpochId != wake.LegLiveRouteEpoch ||
            context.CurrentTransferLeg?.TransportEpochId != wake.TransportEpoch ||
            context.CurrentTransferLeg?.BridgeRecoveryGeneration != wake.BridgeRecoveryGeneration ||
            (context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0) != wake.BridgeDecisionGeneration ||
            !string.Equals(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)", wake.CheckpointRequestId, StringComparison.Ordinal) ||
            context.CurrentTransferLeg?.CheckpointGeneration != wake.CheckpointGeneration)
        {
            reason = "route_scope_changed";
            return false;
        }

        if (!ReferenceEquals(context.DataSession, wake.DataSession) ||
            context.DataSessionGeneration != wake.DataSessionGeneration ||
            !string.Equals(
                CreateReceiveRecoveryDataSessionIdentity(
                    context.TransferId,
                    context.DataSession,
                    context.DataSessionGeneration),
                wake.DataSessionIdentity,
                StringComparison.Ordinal))
        {
            reason = "data_session_changed";
            return false;
        }

        reason = "current";
        return true;
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
        => !unresolvedFrontierProofRequest && IsStrictFallbackFrontierRuntime(context)
            ? V6PostTunaFallbackFrontierRequestStallGraceOverrideForTests ??
              TimeSpan.FromMilliseconds(V6FrontierRequestStallGraceMs)
            : !unresolvedFrontierProofRequest &&
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
    {
        var wake = context.CurrentV6FrontierStallGraceWake;
        var dataSessionIdentity = CreateReceiveRecoveryDataSessionIdentity(context.TransferId, context.DataSession, context.DataSessionGeneration);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_deferred; {FileTransferDiagnosticScopeEvidence.FormatDataSession(dataSessionIdentity)}{FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}{FormatCheckpointContinuitySource(wake?.CheckpointContinuitySource)}authority_effect=none; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; terminal_generation={context.TerminalGeneration}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; transport_epoch={context.CurrentTransferLeg?.TransportEpochId ?? 0}; bridge_recovery_generation={context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0}; bridge_decision_generation={context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)")}; checkpoint_generation={context.CurrentTransferLeg?.CheckpointGeneration ?? 0}; route_publication_generation={context.CurrentRoutePublicationToken?.PublicationGeneration ?? 0}; route_commit_generation={context.CurrentRoutePublicationToken?.RouteCommitGeneration ?? 0}; data_session_identity={FormatProtocolLogValue(dataSessionIdentity)}; data_session_generation={context.DataSessionGeneration}; frontier_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; stall_started_utc={FormatProtocolLogValue(context.V6FrontierStallStartedUtc?.ToString("O") ?? "(none)")}; wake_generation={wake?.WakeGeneration ?? 0}; wake_deadline_utc={FormatProtocolLogValue(wake?.DueUtc.ToString("O") ?? "(none)")}; elapsed_ms={elapsedMs}; stall_grace_ms={(long)stallGrace.TotalMilliseconds}; reason={FormatProtocolLogValue(reason)}; utc={FormatProtocolLogValue(now.ToString("O"))}{context.CoordinatorSnapshotEmitter.GetRoutePublicationEvidence(context.CurrentRoutePublicationToken)?.FormatLogSuffix()}");
    }

    private static void LogInboundV6FrontierStallGraceRecheckDecision(
        InboundV6FrontierStallGraceWake wake,
        string status,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_stall_grace_recheck_decided; {FileTransferDiagnosticScopeEvidence.FormatDataSession(wake.DataSessionIdentity)}{FileTransferDiagnosticScopeEvidence.Format(wake.SessionId, wake.TransferId)}{FormatCheckpointContinuitySource(wake.CheckpointContinuitySource)}authority_effect=none; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}; direction=inbound; transfer_id={FormatProtocolLogValue(wake.TransferId)}; session_id={FormatProtocolLogValue(wake.SessionId)}; route={FormatProtocolLogValue(wake.RouteToken)}; protocol_version={wake.ProtocolVersion}; live_route_epoch={wake.LiveRouteEpoch}; runtime_generation={wake.RuntimeGeneration}; terminal_generation={wake.TerminalGeneration}; leg_generation={wake.LegGeneration}; transport_epoch={wake.TransportEpoch}; bridge_recovery_generation={wake.BridgeRecoveryGeneration}; bridge_decision_generation={wake.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(wake.CheckpointRequestId)}; checkpoint_generation={wake.CheckpointGeneration}; route_publication_generation={wake.RoutePublicationGeneration}; route_commit_generation={wake.RouteCommitGeneration}; data_session_identity={FormatProtocolLogValue(wake.DataSessionIdentity)}; data_session_generation={wake.DataSessionGeneration}; frontier_chunk_index={wake.FrontierChunkIndex}; highest_received_chunk_index={wake.HighestReceivedChunkIndex}; stall_started_utc={FormatProtocolLogValue(wake.StallStartedUtc.ToString("O"))}; wake_generation={wake.WakeGeneration}; wake_deadline_utc={FormatProtocolLogValue(wake.DueUtc.ToString("O"))}");

    private static void LogInboundV6FrontierRecentProgressGraceDeferred(
        InboundV6FrontierRecentProgressGraceWake wake,
        string status,
        string reason,
        DateTimeOffset decisionUtc)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_recent_progress_grace_deferred; arm_decision_utc={FormatProtocolLogValue(decisionUtc.ToString("O"))}; " +
            $"{FileTransferDiagnosticScopeEvidence.FormatDataSession(wake.DataSessionIdentity)}{FileTransferDiagnosticScopeEvidence.Format(wake.SessionId, wake.TransferId)}authority_effect=none; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}; direction=inbound; transfer_id={FormatProtocolLogValue(wake.TransferId)}; session_id={FormatProtocolLogValue(wake.SessionId)}; route={FormatProtocolLogValue(wake.RouteToken)}; protocol_version={wake.ProtocolVersion}; live_route_epoch={wake.LiveRouteEpoch}; runtime_generation={wake.RuntimeGeneration}; terminal_generation={wake.TerminalGeneration}; leg_generation={wake.LegGeneration}; transport_epoch={wake.TransportEpoch}; bridge_recovery_generation={wake.BridgeRecoveryGeneration}; bridge_decision_generation={wake.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(wake.CheckpointRequestId)}; checkpoint_generation={wake.CheckpointGeneration}; route_publication_generation={wake.RoutePublicationGeneration}; route_commit_generation={wake.RouteCommitGeneration}; data_session_identity={FormatProtocolLogValue(wake.DataSessionIdentity)}; data_session_generation={wake.DataSessionGeneration}; frontier_chunk_index={wake.FrontierChunkIndex}; highest_received_chunk_index={wake.HighestReceivedChunkIndex}; committed_progress_utc={FormatProtocolLogValue(wake.CommittedProgressUtc.ToString("O"))}; wake_generation={wake.WakeGeneration}; wake_deadline_utc={FormatProtocolLogValue(wake.DueUtc.ToString("O"))}");

    private static void LogInboundV6FrontierRecentProgressGraceRecheckDecision(
        InboundV6FrontierRecentProgressGraceWake wake,
        string status,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_recent_progress_grace_recheck_decided; {FileTransferDiagnosticScopeEvidence.FormatDataSession(wake.DataSessionIdentity)}{FileTransferDiagnosticScopeEvidence.Format(wake.SessionId, wake.TransferId)}authority_effect=none; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}; direction=inbound; transfer_id={FormatProtocolLogValue(wake.TransferId)}; session_id={FormatProtocolLogValue(wake.SessionId)}; route={FormatProtocolLogValue(wake.RouteToken)}; protocol_version={wake.ProtocolVersion}; live_route_epoch={wake.LiveRouteEpoch}; runtime_generation={wake.RuntimeGeneration}; terminal_generation={wake.TerminalGeneration}; leg_generation={wake.LegGeneration}; transport_epoch={wake.TransportEpoch}; bridge_recovery_generation={wake.BridgeRecoveryGeneration}; bridge_decision_generation={wake.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(wake.CheckpointRequestId)}; checkpoint_generation={wake.CheckpointGeneration}; route_publication_generation={wake.RoutePublicationGeneration}; route_commit_generation={wake.RouteCommitGeneration}; data_session_identity={FormatProtocolLogValue(wake.DataSessionIdentity)}; data_session_generation={wake.DataSessionGeneration}; frontier_chunk_index={wake.FrontierChunkIndex}; highest_received_chunk_index={wake.HighestReceivedChunkIndex}; committed_progress_utc={FormatProtocolLogValue(wake.CommittedProgressUtc.ToString("O"))}; wake_generation={wake.WakeGeneration}; wake_deadline_utc={FormatProtocolLogValue(wake.DueUtc.ToString("O"))}");

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
