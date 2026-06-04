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
    private static bool ShouldUseV6SparseCreditEnvelope(OutboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesV6FeedbackEnvelope;

    private static bool ShouldUseV6SparseCreditEnvelope(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesV6FeedbackEnvelope;

    private static bool ShouldAcceptSparseCreditRuntimeDataFrame(
        OutboundTransferContext context,
        FileTransferDataFrame frame)
        => ShouldUseV6SparseCreditEnvelope(context)
            ? FileTransferProtocol.IsV6DataFrame(frame)
            : FileTransferProtocol.IsV4DataFrame(frame) ||
              ShouldAcceptLiveRouteV6ProbeFrame(context, frame);

    private static bool ShouldAcceptSparseCreditRuntimeDataFrame(
        InboundTransferContext context,
        FileTransferDataFrame frame)
        => ShouldUseV6SparseCreditEnvelope(context)
            ? FileTransferProtocol.IsV6DataFrame(frame)
            : FileTransferProtocol.IsV4DataFrame(frame) ||
              ShouldAcceptLiveRouteV6ProbeFrame(context, frame);

    private static bool ShouldAcceptLiveRouteV6ProbeFrame(OutboundTransferContext context, FileTransferDataFrame frame)
        => frame is FileTransferTransportProbeFrameV6 &&
           context.CurrentLiveRouteEpoch is
           {
               HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
               TargetTransport: FileTransferTransportKind.Tuna,
           };

    private static bool ShouldAcceptLiveRouteV6ProbeFrame(InboundTransferContext context, FileTransferDataFrame frame)
        => frame is FileTransferTransportProbeFrameV6 &&
           context.CurrentLiveRouteEpoch is
           {
               HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
               TargetTransport: FileTransferTransportKind.Tuna,
           };

    private bool ShouldBoundOutboundV4TransportSendForV6RegularNknSparseRuntime(OutboundTransferContext context)
    {
        if (!ShouldUseV6RegularNknSparseRuntime(context))
        {
            return false;
        }

        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);
        }
    }

    private bool ShouldBoundOutboundV4TransportSendForFileTunaV4PostTunaRecovery(OutboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   IsOutboundFileTunaV4PostTunaRecoveryActiveLocked(context);
        }
    }

    private bool ShouldBoundOutboundV4TransportSendForPostTunaFallbackV6LiveSparseRecovery(OutboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context);
        }
    }

    private bool ShouldBoundOutboundV4TransportSendForRegularNknV4FastRuntime(OutboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.RouteRuntime.UsesRegularNknV4FastRuntime &&
                   context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                   context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4;
        }
    }

    private bool ShouldBoundOutboundV4TransportSend(OutboundTransferContext context)
        => ShouldBoundOutboundV4TransportSendForV6RegularNknSparseRuntime(context) ||
           ShouldBoundOutboundV4TransportSendForFileTunaV4PostTunaRecovery(context) ||
           ShouldBoundOutboundV4TransportSendForPostTunaFallbackV6LiveSparseRecovery(context) ||
           ShouldBoundOutboundV4TransportSendForRegularNknV4FastRuntime(context);

    private static TimeSpan CurrentV6RegularNknSparseRuntimeV4TransportSendTimeout =>
        V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests ??
        TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeV4TransportSendTimeoutMs);

    private FileTransferManifestFrameV4 CreateOutboundV4ManifestFrame(OutboundTransferContext context)
    {
        if (ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferManifestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                ChunkSizeBytes = context.ChunkSizeBytes,
                ChunkCount = context.ChunkCount,
                Sha256Base64 = context.Sha256Base64!,
            };
        }

        return new FileTransferManifestFrameV4
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            FileName = context.FileName,
            FileSizeBytes = context.FileSizeBytes,
            ChunkSizeBytes = context.ChunkSizeBytes,
            ChunkCount = context.ChunkCount,
            Sha256Base64 = context.Sha256Base64!,
        };
    }

    private async Task RunOutboundSparseCreditSenderAsync(
        OutboundTransferContext context,
        FileTransferSparseCreditRuntimeKind runtimeKind)
    {
        IFileTransferDataSession? dataSession = null;
        var isPrimaryRegularNknBulkV6 = IsPrimaryRegularNknBulkV6Runtime(runtimeKind);
        var useV6Envelope = context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6;
        if (isPrimaryRegularNknBulkV6)
        {
            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Opening, "sender_start");
        }

        try
        {
            var currentTransport = GetTransportOrThrow();
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = context.NegotiatedDataProtocolVersion,
                FileTransferRoute = context.RouteSelection.TelemetryToken,
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
                context.SparseSenderPumpLastWakeReason = "startup";
                context.V4SenderCreditExhaustedSinceUtc = null;
                context.PullV4SenderPumpRepairQueue.Clear();
                context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
                context.PullV4SenderPumpRepairRequests.Clear();
                context.PullSentChunkCache.Clear();
                context.PullSentChunkCacheBytes = 0;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event={(useV6Envelope ? "filetransfer_v6_sender_started" : "filetransfer_v4_sender_started")}; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={context.NegotiatedDataProtocolVersion}; route={context.RouteSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}; pipeline_depth={V4SenderPumpDepth}; pending_bytes_limit={V4SenderPumpPendingBytes}");
            LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Outbound, "sender", context.RouteSelection);

            UpdateOutboundState(
                context,
                FileTransferTransferState.AwaitingStart,
                0,
                0,
                isPrimaryRegularNknBulkV6 ? "Starting regular NKN bulk V6 transfer." : "Starting regular NKN V4 transfer.");
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.ManifestExchange, "session_open");
            }

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

            var manifest = CreateOutboundV4ManifestFrame(context);

            if (!await SendOutboundV4PrePumpFrameWithTunaActivationPauseRetryAsync(
                    context,
                    dataSession,
                    manifest,
                    frameKind: "manifest",
                    payloadBytes: 0).ConfigureAwait(false))
            {
                return;
            }

            LogPullBinaryFrameSent(context.TransferId, context.SessionId, manifest, payloadBytes: 0);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_manifest_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; file_size_bytes={context.FileSizeBytes}; chunk_size_bytes={context.ChunkSizeBytes}; chunk_count={context.ChunkCount}");
            UpdateOutboundState(
                context,
                FileTransferTransferState.Sending,
                0,
                0,
                useV6Envelope ? "Waiting for V6 receiver state." : "Waiting for V4 receiver state.");
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.AwaitingReceiverState, "manifest_sent");
            }

            if (context.UserPaused)
            {
                await SendOutboundV4PauseControlAsync(context, "user_paused_initial").ConfigureAwait(false);
                await SendOutboundV4PauseStateAsync(context, "user_paused_initial").ConfigureAwait(false);
            }

            var senderPumpTask = RunOutboundV4SenderPumpAsync(context, stream, dataSession);
            Task<FileTransferReceivedDataFrame>? pendingReceiveTask = null;
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
                    if (isPrimaryRegularNknBulkV6)
                    {
                        LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Finalizing, "sender_pump_completed");
                    }

                    try
                    {
                        await senderPumpTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (TryDeferOutboundPostTunaFallbackDataSessionCancellation(
                               context,
                               "sender_pump",
                               ex))
                    {
                        senderPumpTask = RunOutboundV4SenderPumpAsync(context, stream, dataSession);
                        pendingReceiveTask = null;
                        continue;
                    }
                    catch (Exception ex) when (TryDeferOutboundPostTunaFallbackTransportSendFailure(
                               context,
                               "sender_pump",
                               ex))
                    {
                        senderPumpTask = RunOutboundV4SenderPumpAsync(context, stream, dataSession);
                        pendingReceiveTask = null;
                        continue;
                    }

                    if (isPrimaryRegularNknBulkV6)
                    {
                        FileTransferTransferState? terminalState;
                        lock (gate)
                        {
                            terminalState = ReferenceEquals(outboundTransfer, context)
                                ? context.State
                                : null;
                        }

                        if (terminalState == FileTransferTransferState.Completed)
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Completed, "sender_pump_completed");
                        }
                        else if (terminalState == FileTransferTransferState.Failed)
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Failed, "sender_pump_completed");
                        }
                    }

                    return;
                }

                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        await StopOutboundSparseSenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    if (TryGetOutboundV4PeerSilenceFailure(context, out var silenceStatus, out var feedbackStateRefreshRequest))
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
                        await StopOutboundSparseSenderPumpAsync(context, senderPumpTask).ConfigureAwait(false);
                        return;
                    }

                    if (feedbackStateRefreshRequest is not null)
                    {
                        if (isPrimaryRegularNknBulkV6 &&
                            IsPrimaryRegularNknBulkV6CheckpointSyncRequest(feedbackStateRefreshRequest))
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.CheckpointSyncRequested, "feedback_stale");
                            QueueOutboundPrimaryRegularNknBulkV6CheckpointSync(
                                context,
                                dataSession,
                                feedbackStateRefreshRequest);
                        }
                        else
                        {
                            if (isPrimaryRegularNknBulkV6)
                            {
                                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.StateRefreshRequested, "feedback_stale");
                            }

                            QueueOutboundV4SparseRuntimeStateRefresh(
                                context,
                                dataSession,
                                feedbackStateRefreshRequest);
                        }
                    }

                    continue;
                }

                FileTransferReceivedDataFrame received;
                try
                {
                    received = await pendingReceiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (TryDeferOutboundPostTunaFallbackDataSessionCancellation(
                           context,
                           "receive_loop",
                           ex))
                {
                    pendingReceiveTask = null;
                    await Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (TryDeferOutboundPostTunaFallbackTransportSendFailure(
                           context,
                           "receive_loop",
                           ex))
                {
                    pendingReceiveTask = null;
                    await Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);
                    continue;
                }

                var frame = received.Frame;
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (!IsFrameForContext(context, frame))
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "session_or_transfer_mismatch_v4");
                    continue;
                }

                if (!ShouldAcceptSparseCreditRuntimeDataFrame(context, frame))
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

                    LogPullDataFrameIgnored(
                        context.TransferId,
                        context.SessionId,
                        frame,
                        useV6Envelope ? "protocol_not_v6" : "protocol_not_v4");
                    continue;
                }

                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
                    {
                        var now = DateTimeOffset.UtcNow;
                        context.PullV4LastPeerFrameReceivedUtc = now;
                        context.PullV4PeerSilenceDeferralUtc = null;
                        context.PullV4PeerSilenceDeferralCount = 0;
                        context.V6LastPeerLivenessUtc = now;
                        if (frame is FileTransferReceiverStateFrameV6)
                        {
                            context.V6RegularNknStateRefreshFailureCount = 0;
                            if (isPrimaryRegularNknBulkV6)
                            {
                                context.V6RegularNknCheckpointSyncFailureCount = 0;
                            }
                        }
                    }
                }

                switch (frame)
                {
                    case FileTransferTransportEpochFrameV6 handoff:
                        ApplyOutboundV6HandoffFrame(context, handoff);
                        SignalOutboundSparseSenderPump(context);
                        break;
                    case FileTransferFrontierRequestFrameV6 repairRequest:
                        ApplyOutboundV6RepairRequest(context, repairRequest);
                        SignalOutboundSparseSenderPump(context);
                        break;
                    case FileTransferRepairProofFrameV6 repairProof:
                        ApplyOutboundV6RepairProof(context, repairProof);
                        SignalOutboundSparseSenderPump(context);
                        break;
                    case FileTransferTransportProbeFrameV6 probe:
                        await HandleReceivedV6TransportProbeFrameAsync(
                            context.SessionId,
                            context.TransferId,
                            FileTransferDirection.Outbound,
                            probe,
                            received.TransportKind).ConfigureAwait(false);
                        break;
                    case FileTransferPauseControlFrameV4 pauseControl:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, pauseControl, "lifecycle_data_frame_ignored_phase2");
                        break;
                    case FileTransferStateFrameV4 state:
                        ApplyOutboundV4State(context, state);
                        if (isPrimaryRegularNknBulkV6 &&
                            state is FileTransferReceiverStateFrameV6)
                        {
                            if (state is FileTransferReceiverStateFrameV6 receiverState &&
                                string.Equals(receiverState.RecoveryMode, V6RegularNknCheckpointSyncRecoveryMode, StringComparison.Ordinal))
                            {
                                LogPrimaryRegularNknBulkV6CheckpointReceived(context, receiverState);
                            }

                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.CreditGranted, "receiver_state");
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.SendingBulk, "receiver_state");
                        }

                        SignalOutboundSparseSenderPump(context);
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
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame_v4");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Cancelled, "lifetime_cancelled");
            }
        }
        catch (Exception ex)
        {
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Failed, "exception");
            }

            if (TryDeferOutboundPostTunaFallbackTransportSendFailure(context, "outbound_loop", ex))
            {
                return;
            }

            var errorCode = ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
            await FailOutboundV4Async(
                context,
                dataSession,
                errorCode,
                ClassifyOutboundFailureStatusMessage(ex, errorCode),
                notifyPeer: true).ConfigureAwait(false);
        }
    }

    private bool TryDeferOutboundPostTunaFallbackDataSessionCancellation(
        OutboundTransferContext context,
        string source,
        OperationCanceledException ex)
    {
        SessionFileTransferSnapshot? snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.LifetimeCts.IsCancellationRequested ||
                !IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context))
            {
                return false;
            }

            if (!context.PullTransportPaused)
            {
                context.PullTransportPaused = true;
                context.PullTransportPausedSinceUtc = now;
                context.PullTransportPauseReason = "post_tuna_fallback_bridge_restart";
                context.PullTransportLastPauseReason = context.PullTransportPauseReason;
                context.PullTransportResumeRequestPending = true;
            }

            context.PullTransportGraceDeadlineUtc = now.AddSeconds(5);
            context.StatusMessage = "Waiting for network recovery.";
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_bridge_restart_cancelled_send";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_bridge_restart_cancelled_send";
            snapshot = CreateSnapshotLocked();
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_fallback_data_session_cancellation_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; source={FormatProtocolLogValue(source)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}; rebind_generation={context.PullTransportRebindGeneration}; transport_paused={(context.PullTransportPaused ? 1 : 0)}; error={FormatProtocolLogValue(ex.GetType().Name)}");
        RaiseTransferChanged(snapshot);
        SignalOutboundSparseSenderPump(context);
        return true;
    }

    private bool TryDeferOutboundPostTunaFallbackTransportSendFailure(
        OutboundTransferContext context,
        string source,
        Exception ex)
    {
        if (!IsRecoverablePostTunaFallbackTransportSendException(ex))
        {
            return false;
        }

        SessionFileTransferSnapshot? snapshot;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.LifetimeCts.IsCancellationRequested ||
                !IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context))
            {
                return false;
            }

            if (!context.PullTransportPaused)
            {
                context.PullTransportPaused = true;
                context.PullTransportPausedSinceUtc = now;
                context.PullTransportPauseReason = "post_tuna_fallback_bridge_restart";
                context.PullTransportLastPauseReason = context.PullTransportPauseReason;
                context.PullTransportResumeRequestPending = true;
            }

            context.PullTransportGraceDeadlineUtc = now.AddSeconds(5);
            context.StatusMessage = "Waiting for network recovery.";
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_bridge_restart_send_failure";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_bridge_restart_send_failure";
            snapshot = CreateSnapshotLocked();
        }

        TryRequestFileTransferReceiveRecovery(AttachFallbackLegAuthority(
            context,
            new FileTransferReceiveRecoveryRequest(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_bridge_restart_send_failure"),
            "post_tuna_fallback_bridge_restart_send_failure"));

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_fallback_transport_send_failure_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; source={FormatProtocolLogValue(source)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; reason=post_tuna_fallback_bridge_restart_send_failure; recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}; rebind_generation={context.PullTransportRebindGeneration}; transport_paused={(context.PullTransportPaused ? 1 : 0)}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
        RaiseTransferChanged(snapshot);
        SignalOutboundSparseSenderPump(context);
        return true;
    }

    private static bool IsRecoverablePostTunaFallbackTransportSendException(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return true;
        }

        return IsTransportDisconnected(ex);
    }

    private bool TryGetOutboundV4PeerSilenceFailure(
        OutboundTransferContext context,
        out string statusMessage,
        out FileTransferFrontierRequestFrameV6? feedbackStateRefreshRequest)
    {
        statusMessage = "Receiver stopped responding.";
        feedbackStateRefreshRequest = null;
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
            var hasUnacknowledgedData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.PullV4SenderPumpRepairRequests.Count > 0;
            if (!hasUnacknowledgedData)
            {
                return false;
            }

            var isPrimaryRegularNknBulkV6 =
                !postFallback &&
                IsPrimaryRegularNknBulkV6ContextLocked(context);
            var isDiagnosticV6SparseRuntimePrimaryRegularNkn =
                !postFallback &&
                !isPrimaryRegularNknBulkV6 &&
                ShouldUseV6RegularNknSparseRuntime(context) &&
                IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);
            var isPostTunaFallbackSparseRuntime =
                postFallback &&
                ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context);
            if (isPrimaryRegularNknBulkV6)
            {
                TryPrepareOutboundPrimaryRegularNknBulkV6CheckpointSyncLocked(
                    context,
                    silence,
                    out feedbackStateRefreshRequest);
            }
            else if (isDiagnosticV6SparseRuntimePrimaryRegularNkn ||
                     isPostTunaFallbackSparseRuntime)
            {
                TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked(
                    context,
                    silence,
                    out feedbackStateRefreshRequest);
            }

            if (silence < timeout)
            {
                return false;
            }

            var reason = postFallback
                ? "post_tuna_fallback_peer_silence"
                : "peer_silence";
            if (isPrimaryRegularNknBulkV6)
            {
                MaybeLogOutboundPrimaryRegularNknBulkV6PeerSilenceDeferralLocked(context, reason, silence, timeout);
                return false;
            }

            if (isDiagnosticV6SparseRuntimePrimaryRegularNkn)
            {
                MaybeLogOutboundV4SparseRuntimePeerSilenceDeferralLocked(context, reason, silence, timeout);
                return false;
            }

            if (isPostTunaFallbackSparseRuntime)
            {
                MaybeLogOutboundPostTunaFallbackPeerSilenceDeferralLocked(context, reason, silence, timeout);
                MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, reason);
                return false;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_peer_feedback_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}");
            return true;
        }
    }

    private static void TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked(
        OutboundTransferContext context,
        TimeSpan feedbackSilence,
        out FileTransferFrontierRequestFrameV6? stateRefreshRequest)
    {
        stateRefreshRequest = null;
        var now = DateTimeOffset.UtcNow;
        if (feedbackSilence < CurrentV6SenderRequestFeedbackStallRecoveryDelay ||
            context.V6RegularNknLastStateRefreshRequestedUtc is { } lastRefresh &&
            now - lastRefresh < CurrentV6RegularNknSparseRuntimeStateRefreshCooldown)
        {
            return;
        }

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
        var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
        var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
        var creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
        var availableCreditChunks = Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport);
        var inFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames);
        var queuedRepairChunkCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        var staleCreditRecoveryDelay = CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay;
        if (ShouldForcePostTunaFallbackStaleInflightRecoveryLocked(
                context,
                feedbackSilence,
                transportBacklogChunks,
                availableCreditChunks,
                inFlightFrames))
        {
            var staleInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames);
            var staleInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes);
            context.PullSenderPipelineCurrentInFlightFrames = 0;
            context.PullSenderPipelineCurrentInFlightBytes = 0;
            context.PullSenderPipelineFailedFramesRecent += staleInFlightFrames;
            context.PullSenderPipelineFailedFramesTotal += staleInFlightFrames;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_stale_inflight_recovery";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_stale_inflight_recovery";

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_stale_inflight_repair_recovery_forced; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=sender_pipeline_in_flight_stale; deferral_count={context.V6EpochLivenessDeferralCount}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; stale_credit_recovery_delay_ms={(long)staleCreditRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; stale_in_flight_frames={staleInFlightFrames}; stale_in_flight_bytes={staleInFlightBytes}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; state_refresh_failure_count={context.V6RegularNknStateRefreshFailureCount}; rebind_generation={context.PullTransportRebindGeneration}");

            stateRefreshRequest = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
                context,
                now,
                feedbackSilence,
                transportEpoch,
                epochState,
                transportBacklogChunks,
                availableCreditChunks,
                creditCeiling,
                0,
                queuedRepairChunkCount,
                "feedback_stalled_with_stale_inflight",
                V6RegularNknStateRefreshStaleInflightPriority);
            return;
        }

        if (ShouldForcePostTunaFallbackStaleCreditRecoveryLocked(
                context,
                feedbackSilence,
                transportBacklogChunks,
                availableCreditChunks))
        {
            var previousCreditCeiling = creditCeiling;
            var previousRemoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
            var clampedGrant = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunksAcceptedForTransport);
            context.RemoteGrantedUntilExclusive = clampedGrant;
            creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_stale_credit_recovery";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_stale_credit_recovery";
            ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
                context,
                Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount - 1)),
                "post_tuna_fallback_stale_credit");

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_stale_credit_recovery_forced; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=receiver_credit_stale; deferral_count={context.V6EpochLivenessDeferralCount}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; stale_credit_recovery_delay_ms={(long)staleCreditRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; previous_available_credit_chunks={availableCreditChunks}; available_credit_chunks={Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport)}; previous_credit_ceiling_chunk_index={previousCreditCeiling}; credit_ceiling_chunk_index={creditCeiling}; previous_remote_credit_until_chunk_index_exclusive={previousRemoteGrantedUntilExclusive}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; state_refresh_failure_count={context.V6RegularNknStateRefreshFailureCount}; rebind_generation={context.PullTransportRebindGeneration}");

            stateRefreshRequest = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
                context,
                now,
                feedbackSilence,
                transportEpoch,
                epochState,
                transportBacklogChunks,
                availableCreditChunks,
                previousCreditCeiling,
                inFlightFrames,
                queuedRepairChunkCount,
                "feedback_stalled_with_stale_credit",
                V6RegularNknStateRefreshStaleCreditPriority);
            return;
        }

        if (ShouldForcePostTunaFallbackTailReconciliationLocked(
                context,
                feedbackSilence,
                transportBacklogChunks,
                availableCreditChunks,
                inFlightFrames,
                queuedRepairChunkCount))
        {
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_tail_reconciliation";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_tail_reconciliation";

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_fallback_tail_zero_credit_breaker; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=tail_reconciliation_zero_credit; deferral_count={context.V6EpochLivenessDeferralCount}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; stale_credit_recovery_delay_ms={(long)staleCreditRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; in_flight_frames={inFlightFrames}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; state_refresh_failure_count={context.V6RegularNknStateRefreshFailureCount}; rebind_generation={context.PullTransportRebindGeneration}");
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_fallback_tail_stale_frontier_retired; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentTransferLeg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={transportEpoch}; retired_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; reason=tail_reconciliation_zero_credit");

            stateRefreshRequest = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
                context,
                now,
                feedbackSilence,
                transportEpoch,
                epochState,
                transportBacklogChunks,
                availableCreditChunks,
                creditCeiling,
                inFlightFrames,
                queuedRepairChunkCount,
                "post_tuna_fallback_tail_reconciliation",
                V6RegularNknStateRefreshTailReconciliationPriority);
            return;
        }

        if (availableCreditChunks > 0 || inFlightFrames > 0)
        {
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6SenderPumpLastWakeReason = "v4_sparse_runtime_feedback_stalled_deferred";
            if (context.V6LastFeedbackStallRecoverySuppressedUtc is null ||
                now - context.V6LastFeedbackStallRecoverySuppressedUtc.Value >= TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoverySuppressedLogIntervalMs))
            {
                context.V6LastFeedbackStallRecoverySuppressedUtc = now;
                var reason = availableCreditChunks > 0 ? "normal_credit_available" : "sender_pipeline_in_flight";
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_deferred; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; deferral_count={context.V6EpochLivenessDeferralCount}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; recovery_delay_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryDelay.TotalMilliseconds}; stale_credit_recovery_delay_ms={(long)staleCreditRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; in_flight_frames={inFlightFrames}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; rebind_generation={context.PullTransportRebindGeneration}");
            }

            if (feedbackSilence >= staleCreditRecoveryDelay)
            {
                stateRefreshRequest = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
                    context,
                    now,
                    feedbackSilence,
                    transportEpoch,
                    epochState,
                    transportBacklogChunks,
                    availableCreditChunks,
                    creditCeiling,
                    inFlightFrames,
                    queuedRepairChunkCount,
                    availableCreditChunks > 0 ? "feedback_stalled_with_credit" : "feedback_stalled_with_inflight");
            }

            return;
        }

        stateRefreshRequest = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
            context,
            now,
            feedbackSilence,
            transportEpoch,
            epochState,
            transportBacklogChunks,
            availableCreditChunks,
            creditCeiling,
            inFlightFrames,
            queuedRepairChunkCount,
            "feedback_stalled_no_credit");
    }

    private static bool ShouldForcePostTunaFallbackStaleInflightRecoveryLocked(
        OutboundTransferContext context,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks,
        int inFlightFrames)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullPostTunaRecoveryActive &&
           transportBacklogChunks > 0 &&
           availableCreditChunks <= 0 &&
           inFlightFrames > 0 &&
           feedbackSilence >= CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay &&
           context.V6RegularNknStateRefreshFailureCount >= V6PostTunaFallbackStaleInflightRecoveryMinStateRefreshFailures;

    private static bool ShouldForcePostTunaFallbackStaleCreditRecoveryLocked(
        OutboundTransferContext context,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullPostTunaRecoveryActive &&
           transportBacklogChunks > 0 &&
           availableCreditChunks > 0 &&
           feedbackSilence >= CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay &&
           context.V6RegularNknStateRefreshFailureCount >= V6PostTunaFallbackStaleCreditRecoveryMinStateRefreshFailures;

    private static bool ShouldForcePostTunaFallbackTailReconciliationLocked(
        OutboundTransferContext context,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks,
        int inFlightFrames,
        int queuedRepairChunkCount)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullPostTunaRecoveryActive &&
           availableCreditChunks <= 0 &&
           (transportBacklogChunks > 0 ||
            inFlightFrames > 0 ||
            queuedRepairChunkCount > 0 ||
            context.PullV4SenderPumpRepairRequests.Count > 0) &&
           feedbackSilence >= CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay &&
           context.V6RegularNknStateRefreshFailureCount >= Math.Min(
               V6PostTunaFallbackStaleInflightRecoveryMinStateRefreshFailures,
               V6PostTunaFallbackStaleCreditRecoveryMinStateRefreshFailures);

    private static FileTransferFrontierRequestFrameV6 CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        TimeSpan feedbackSilence,
        long transportEpoch,
        string epochState,
        int transportBacklogChunks,
        int availableCreditChunks,
        int creditCeiling,
        int inFlightFrames,
        int queuedRepairChunkCount,
        string refreshReason,
        string priority = V6RegularNknStateRefreshPriority)
    {
        context.V6RegularNknLastStateRefreshRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.V6SenderPumpLastWakeReason = "v4_sparse_runtime_state_refresh_requested";
        var sequence = ++context.V6RegularNknStateRefreshSequence;
        var refreshHintChunkIndex = context.ChunkCount > 0
            ? Math.Min(Math.Max(0, context.RemoteNextExpectedChunkIndex), context.ChunkCount - 1)
            : 0;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(refreshReason)}; request_sequence={sequence}; priority={FormatProtocolLogValue(priority)}; feedback_silence_ms={(long)feedbackSilence.TotalMilliseconds}; refresh_cooldown_ms={(long)CurrentV6RegularNknSparseRuntimeStateRefreshCooldown.TotalMilliseconds}; stale_credit_recovery_delay_ms={(long)CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; refresh_hint_chunk_index={refreshHintChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; in_flight_frames={inFlightFrames}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; rebind_generation={context.PullTransportRebindGeneration}");
        if (string.Equals(priority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal))
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_fallback_tail_reconciliation_requested; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_sequence={sequence}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; live_route_epoch={context.CurrentTransferLeg?.LiveRouteEpochId ?? context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={transportEpoch}; checkpoint_request_id=v6-regular-nkn-state-refresh:{sequence}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; refresh_hint_chunk_index={refreshHintChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; in_flight_frames={inFlightFrames}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}; queued_repair_chunk_count={queuedRepairChunkCount}; reason={FormatProtocolLogValue(refreshReason)}");
        }

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            TransportEpoch = transportEpoch,
            RepairRequestId = $"v6-regular-nkn-state-refresh:{sequence}",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = refreshHintChunkIndex,
                    ChunkCount = 1,
                },
            ],
            Priority = priority,
            RecoveryMode = V6RegularNknStateRefreshRecoveryMode,
        };
        return request;
    }

    private static void TryPrepareOutboundPrimaryRegularNknBulkV6CheckpointSyncLocked(
        OutboundTransferContext context,
        TimeSpan feedbackSilence,
        out FileTransferFrontierRequestFrameV6? checkpointSyncRequest)
    {
        checkpointSyncRequest = null;
        var now = DateTimeOffset.UtcNow;
        if (feedbackSilence < CurrentV6SenderRequestFeedbackStallRecoveryDelay ||
            context.V6RegularNknLastCheckpointSyncRequestedUtc is { } lastRefresh &&
            now - lastRefresh < CurrentV6RegularNknSparseRuntimeStateRefreshCooldown)
        {
            return;
        }

        var creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
        var availableCreditChunks = Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport);
        var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
        var inFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames);
        if (availableCreditChunks > 0 || inFlightFrames > 0)
        {
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6SenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_feedback_stalled_deferred";
            if (context.V6LastFeedbackStallRecoverySuppressedUtc is null ||
                now - context.V6LastFeedbackStallRecoverySuppressedUtc.Value >= TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoverySuppressedLogIntervalMs))
            {
                context.V6LastFeedbackStallRecoverySuppressedUtc = now;
                var reason = availableCreditChunks > 0 ? "checkpoint_credit_available" : "checkpoint_sender_pipeline_in_flight";
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_recovery_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; deferral_count={context.V6EpochLivenessDeferralCount}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; stale_credit_recovery_delay_ms={(long)CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay.TotalMilliseconds}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; in_flight_frames={inFlightFrames}; rebind_generation={context.PullTransportRebindGeneration}");
            }

            if (feedbackSilence < CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay)
            {
                return;
            }
        }

        checkpointSyncRequest = CreateOutboundPrimaryRegularNknBulkV6CheckpointSyncRequestLocked(
            context,
            now,
            feedbackSilence,
            transportBacklogChunks,
            availableCreditChunks,
            creditCeiling,
            inFlightFrames,
            availableCreditChunks > 0
                ? "feedback_stalled_with_credit"
                : inFlightFrames > 0
                    ? "feedback_stalled_with_inflight"
                    : "feedback_stalled_no_credit");
    }

    private static FileTransferFrontierRequestFrameV6 CreateOutboundPrimaryRegularNknBulkV6CheckpointSyncRequestLocked(
        OutboundTransferContext context,
        DateTimeOffset now,
        TimeSpan feedbackSilence,
        int transportBacklogChunks,
        int availableCreditChunks,
        int creditCeiling,
        int inFlightFrames,
        string reason)
    {
        context.V6RegularNknLastCheckpointSyncRequestedUtc = now;
        context.V6EpochLivenessDeferralCount++;
        context.V6EpochLivenessDeferralUtc = now;
        context.V6SenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_checkpoint_sync_requested";
        var sequence = ++context.V6RegularNknCheckpointSyncSequence;
        var checkpointHintChunkIndex = context.ChunkCount > 0
            ? Math.Min(Math.Max(0, context.RemoteNextExpectedChunkIndex), context.ChunkCount - 1)
            : 0;
        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            TransportEpoch = context.PullTransportRebindGeneration,
            RepairRequestId = $"{V6RegularNknCheckpointSyncRequestPrefix}{sequence}",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = checkpointHintChunkIndex,
                    ChunkCount = 1,
                },
            ],
            Priority = V6RegularNknCheckpointSyncPriority,
            RecoveryMode = V6RegularNknCheckpointSyncRecoveryMode,
        };
        LogPrimaryRegularNknBulkV6CheckpointRequestPrepared(
            context,
            request,
            reason,
            feedbackSilence,
            transportBacklogChunks,
            availableCreditChunks,
            inFlightFrames);
        return request;
    }

    private void QueueOutboundPrimaryRegularNknBulkV6CheckpointSync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferFrontierRequestFrameV6 request)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                return;
            }

            if (context.V6RegularNknCheckpointSyncSendInFlight > 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_skipped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; reason=send_in_flight; in_flight={context.V6RegularNknCheckpointSyncSendInFlight}");
                return;
            }

            context.V6RegularNknCheckpointSyncSendInFlight++;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_queued; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; timeout_ms={V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs}");
        _ = SendOutboundPrimaryRegularNknBulkV6CheckpointSyncAsync(context, dataSession, request);
    }

    private async Task SendOutboundPrimaryRegularNknBulkV6CheckpointSyncAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferFrontierRequestFrameV6 request)
    {
        var signalSenderPumpAfterSend = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs));
            await dataSession.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            LogPullBinaryFrameSent(context.TransferId, context.SessionId, request, payloadBytes: 0);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; transport_epoch={request.TransportEpoch}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
        }
        catch (OperationCanceledException) when (!context.LifetimeCts.IsCancellationRequested)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; timeout_ms={V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs}");
            RequestOutboundPrimaryRegularNknBulkV6CheckpointReceiveRecovery(
                context,
                request,
                "checkpoint_sync_send_timeout");
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; reason=lifetime_cancelled");
        }
        catch (Exception ex)
        {
            var terminal = false;
            lock (gate)
            {
                terminal = !ReferenceEquals(outboundTransfer, context) || context.IsTerminal;
            }

            var eventName = terminal
                ? "filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_suppressed"
                : "filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed";
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event={eventName}; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; reason={FormatProtocolLogValue(ex.Message)}; terminal={(terminal ? 1 : 0)}");
            if (!terminal)
            {
                RequestOutboundPrimaryRegularNknBulkV6CheckpointReceiveRecovery(
                    context,
                    request,
                    "checkpoint_sync_send_failed");
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    context.V6RegularNknCheckpointSyncSendInFlight > 0)
                {
                    context.V6RegularNknCheckpointSyncSendInFlight--;
                    signalSenderPumpAfterSend =
                        context.V6RegularNknCheckpointSyncSendInFlight == 0 &&
                        string.Equals(
                            context.SparseSenderPumpLastWakeReason,
                            "primary_regular_nkn_bulk_v6_checkpoint_recovery_deferred",
                            StringComparison.Ordinal);
                }
            }

            if (signalSenderPumpAfterSend)
            {
                SignalOutboundSparseSenderPump(context);
            }
        }
    }

    private void RequestOutboundPrimaryRegularNknBulkV6CheckpointReceiveRecovery(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        FileTransferReceiveRecoveryRequest? recoveryRequest = null;
        var signalSenderPump = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (context.V6LastReceiveRecoveryRequestedUtc is { } lastRecovery &&
                now - lastRecovery < CurrentV6SenderRequestFeedbackStallRecoveryCooldown)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; suppression_reason=recovery_cooldown; cooldown_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryCooldown.TotalMilliseconds}; last_recovery_age_ms={(long)Math.Max(0, (now - lastRecovery).TotalMilliseconds)}; failure_count={context.V6RegularNknCheckpointSyncFailureCount}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
                return;
            }

            context.V6RegularNknCheckpointSyncFailureCount++;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;

            var feedbackSilence = context.PullV4LastPeerFrameReceivedUtc is { } lastPeerFrame
                ? now - lastPeerFrame
                : TimeSpan.Zero;
            var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
            var creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
            var availableCreditChunks = Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport);
            if (context.BridgeRecoveryPolicy == FileTransferBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery &&
                context.V6RegularNknCheckpointSyncFailureCount < V6RegularNknCheckpointSyncFailuresBeforeBridgeRecovery)
            {
                context.SparseSenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_checkpoint_recovery_deferred";
                signalSenderPump = true;
                LogPrimaryRegularNknBulkV6FrontierFeedbackFailedRecoverable(
                    context,
                    request,
                    reason,
                    "defer_bridge_recovery",
                    context.V6RegularNknCheckpointSyncFailureCount,
                    feedbackSilence,
                    transportBacklogChunks,
                    availableCreditChunks);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; suppression_reason=quiet_policy_first_failure; failure_count={context.V6RegularNknCheckpointSyncFailureCount}; required_failure_count={V6RegularNknCheckpointSyncFailuresBeforeBridgeRecovery}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
            }
            else
            {
                context.V6LastReceiveRecoveryRequestedUtc = now;
                context.SparseSenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_checkpoint_receive_recovery";
                LogPrimaryRegularNknBulkV6FrontierFeedbackFailedRecoverable(
                    context,
                    request,
                    reason,
                    "request_bridge_recovery",
                    context.V6RegularNknCheckpointSyncFailureCount,
                    feedbackSilence,
                    transportBacklogChunks,
                    availableCreditChunks);
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; failure_count={context.V6RegularNknCheckpointSyncFailureCount}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");

                recoveryRequest = AttachFallbackLegAuthority(
                    context,
                    new FileTransferReceiveRecoveryRequest(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Outbound,
                        "primary_regular_nkn_bulk_v6_checkpoint_sync_failed"),
                    "primary_regular_nkn_bulk_v6_checkpoint_sync_failed",
                    request.RepairRequestId,
                    request.TransportEpoch);
            }
        }

        if (signalSenderPump)
        {
            SignalOutboundSparseSenderPump(context);
        }

        if (recoveryRequest is not null)
        {
            TryRequestFileTransferReceiveRecovery(recoveryRequest);
        }
    }

    private static bool IsV6RegularNknStateRefreshRequest(FileTransferFrontierRequestFrameV6 request)
        => string.Equals(request.RecoveryMode, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal) ||
           (request.RepairRequestId?.StartsWith("v6-regular-nkn-state-refresh:", StringComparison.Ordinal) ?? false);

    private void QueueOutboundV4SparseRuntimeStateRefresh(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferFrontierRequestFrameV6 request)
    {
        FileTransferReceiveRecoveryRequest? recoveryRequest = null;
        long sendGeneration;
        var deferredUntilResume = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return;
            }

            if (context.V6RegularNknStateRefreshSendInFlight > 0)
            {
                if (TryRetireOutboundPostTunaFallbackStaleStateRefreshSendLocked(
                        context,
                        request,
                        DateTimeOffset.UtcNow))
                {
                    recoveryRequest = AttachFallbackLegAuthority(
                        context,
                        new FileTransferReceiveRecoveryRequest(
                            context.SessionId,
                            context.TransferId,
                            FileTransferDirection.Outbound,
                            "post_tuna_fallback_stale_state_refresh_send_retired"),
                        "post_tuna_fallback_stale_state_refresh_send_retired",
                        request.RepairRequestId,
                        request.TransportEpoch);
                    DeferOutboundPostTunaFallbackStateRefreshAfterRecoveryLocked(
                        context,
                        request,
                        "post_tuna_fallback_stale_state_refresh_send_retired");
                    deferredUntilResume = true;
                }
                else
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_regular_nkn_state_refresh_send_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; reason=send_in_flight; in_flight={context.V6RegularNknStateRefreshSendInFlight}; active_generation={context.V6RegularNknStateRefreshActiveSendGeneration}; active_request_id={FormatProtocolLogValue(context.V6RegularNknStateRefreshActiveRequestId ?? "(none)")}");
                    return;
                }
            }

            if (deferredUntilResume)
            {
                sendGeneration = 0;
            }
            else
            {
                sendGeneration = ++context.V6RegularNknStateRefreshSendGeneration;
                context.V6RegularNknStateRefreshSendInFlight = 1;
                context.V6RegularNknStateRefreshActiveSendGeneration = sendGeneration;
                context.V6RegularNknStateRefreshActiveRequestId = request.RepairRequestId;
                context.V6RegularNknStateRefreshActivePriority = request.Priority;
                context.V6RegularNknStateRefreshActiveStartedUtc = DateTimeOffset.UtcNow;
                MarkOutboundFallbackCheckpointRequestedLocked(
                    context,
                    request,
                    "state_refresh_send_queued");
            }
        }

        if (deferredUntilResume)
        {
            if (recoveryRequest is not null)
            {
                TryRequestFileTransferReceiveRecovery(recoveryRequest);
            }

            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_state_refresh_send_queued; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; priority={FormatProtocolLogValue(request.Priority)}; timeout_ms={V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs}");
        _ = SendOutboundV4SparseRuntimeStateRefreshAsync(context, dataSession, request, sendGeneration);
        if (recoveryRequest is not null)
        {
            TryRequestFileTransferReceiveRecovery(recoveryRequest);
        }
    }

    private static void DeferOutboundPostTunaFallbackStateRefreshAfterRecoveryLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var replacementCount = context.V6RegularNknDeferredStateRefreshRequest is null
            ? context.V6RegularNknDeferredStateRefreshReplaceCount
            : context.V6RegularNknDeferredStateRefreshReplaceCount + 1;
        context.V6RegularNknDeferredStateRefreshRequest = request;
        context.V6RegularNknDeferredStateRefreshReason = reason;
        context.V6RegularNknDeferredStateRefreshCreatedUtc = now;
        context.V6RegularNknDeferredStateRefreshReplaceCount = replacementCount;
        context.PullTransportResumeRequestPending = true;
        context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_deferred_until_resume";
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_deferred_until_resume";
        MarkOutboundFallbackCheckpointRequestedLocked(context, request, reason);

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_fallback_state_refresh_deferred_until_resume; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; priority={FormatProtocolLogValue(request.Priority)}; deferred_replace_count={replacementCount}; failure_count={context.V6RegularNknStateRefreshFailureCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex)}; available_credit_chunks={Math.Max(0, Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport)}; rebind_generation={context.PullTransportRebindGeneration}");
    }

    private void QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume(
        OutboundTransferContext context,
        string resumeReason)
    {
        IFileTransferDataSession? dataSession = null;
        FileTransferFrontierRequestFrameV6? request = null;
        string deferredReason = "(none)";
        DateTimeOffset? deferredUtc = null;
        int replaceCount = 0;
        var discardReason = string.Empty;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return;
            }

            if (context.V6RegularNknDeferredStateRefreshRequest is null)
            {
                return;
            }

            if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
                !context.PullPostTunaRecoveryActive)
            {
                discardReason = "fallback_no_longer_active";
            }
            else if (context.DataSession is null)
            {
                discardReason = "data_session_missing";
            }
            else if (!context.DataSession.IsAvailable)
            {
                return;
            }
            else if (context.V6RegularNknStateRefreshSendInFlight > 0)
            {
                return;
            }

            if (!string.IsNullOrEmpty(discardReason))
            {
                request = context.V6RegularNknDeferredStateRefreshRequest;
                deferredReason = context.V6RegularNknDeferredStateRefreshReason ?? "(none)";
                deferredUtc = context.V6RegularNknDeferredStateRefreshCreatedUtc;
                replaceCount = context.V6RegularNknDeferredStateRefreshReplaceCount;
                ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
            }
            else
            {
                dataSession = context.DataSession;
                request = context.V6RegularNknDeferredStateRefreshRequest;
                deferredReason = context.V6RegularNknDeferredStateRefreshReason ?? "(none)";
                deferredUtc = context.V6RegularNknDeferredStateRefreshCreatedUtc;
                replaceCount = context.V6RegularNknDeferredStateRefreshReplaceCount;
                ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
                context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_deferred_state_refresh_replayed";
                context.V6SenderPumpLastWakeReason = "post_tuna_fallback_deferred_state_refresh_replayed";
            }
        }

        if (!string.IsNullOrEmpty(discardReason))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_state_refresh_deferred_discarded; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(discardReason)}; resume_reason={FormatProtocolLogValue(resumeReason)}; deferred_reason={FormatProtocolLogValue(deferredReason)}; request_id={FormatProtocolLogValue(request?.RepairRequestId ?? "(none)")}; deferred_age_ms={FormatNullableDurationMs(deferredUtc, DateTimeOffset.UtcNow)}; deferred_replace_count={replaceCount}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}");
            return;
        }

        if (dataSession is null ||
            request is null)
        {
            return;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_fallback_state_refresh_deferred_replayed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(resumeReason)}; deferred_reason={FormatProtocolLogValue(deferredReason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; priority={FormatProtocolLogValue(request.Priority)}; deferred_age_ms={FormatNullableDurationMs(deferredUtc, DateTimeOffset.UtcNow)}; deferred_replace_count={replaceCount}; rebind_generation={context.PullTransportRebindGeneration}");

        QueueOutboundV4SparseRuntimeStateRefresh(context, dataSession, request);
    }

    private static string FormatNullableDurationMs(DateTimeOffset? sinceUtc, DateTimeOffset nowUtc)
        => sinceUtc is { } since
            ? ((long)Math.Max(0, (nowUtc - since).TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)
            : "-1";

    private static void ClearOutboundV6RegularNknDeferredStateRefreshLocked(OutboundTransferContext context)
    {
        context.V6RegularNknDeferredStateRefreshRequest = null;
        context.V6RegularNknDeferredStateRefreshReason = null;
        context.V6RegularNknDeferredStateRefreshCreatedUtc = null;
        context.V6RegularNknDeferredStateRefreshReplaceCount = 0;
    }

    private async Task SendOutboundV4SparseRuntimeStateRefreshAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferFrontierRequestFrameV6 request,
        long sendGeneration)
    {
        var retiredSendObserved = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs));
            await dataSession.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (TryObserveRetiredOutboundV4SparseRuntimeStateRefreshSend(
                    context,
                    request,
                    sendGeneration,
                    "sent"))
            {
                retiredSendObserved = true;
                return;
            }

            LogPullBinaryFrameSent(context.TransferId, context.SessionId, request, payloadBytes: 0);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_regular_nkn_state_refresh_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; transport_epoch={request.TransportEpoch}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
        }
        catch (OperationCanceledException) when (!context.LifetimeCts.IsCancellationRequested)
        {
            if (TryObserveRetiredOutboundV4SparseRuntimeStateRefreshSend(
                    context,
                    request,
                    sendGeneration,
                    "timeout"))
            {
                retiredSendObserved = true;
                return;
            }

            if (TryDeferOutboundPostTunaFallbackStateRefreshForTunaActivation(
                    context,
                    request,
                    "post_tuna_fallback_state_refresh_send_timeout",
                    null))
            {
                return;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_regular_nkn_state_refresh_send_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; timeout_ms={V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs}");
            RequestOutboundPostTunaFallbackStateRefreshReceiveRecovery(
                context,
                request,
                "post_tuna_fallback_state_refresh_send_timeout");
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (TryObserveRetiredOutboundV4SparseRuntimeStateRefreshSend(
                    context,
                    request,
                    sendGeneration,
                    "failed"))
            {
                retiredSendObserved = true;
                return;
            }

            if (TryDeferOutboundPostTunaFallbackStateRefreshForTunaActivation(
                    context,
                    request,
                    "post_tuna_fallback_state_refresh_send_failed",
                    ex))
            {
                return;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_regular_nkn_state_refresh_send_failed; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; reason={FormatProtocolLogValue(ex.Message)}");
            RequestOutboundPostTunaFallbackStateRefreshReceiveRecovery(
                context,
                request,
                "post_tuna_fallback_state_refresh_send_failed");
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    context.V6RegularNknStateRefreshActiveSendGeneration == sendGeneration &&
                    context.V6RegularNknStateRefreshSendInFlight > 0)
                {
                    context.V6RegularNknStateRefreshSendInFlight--;
                    if (context.V6RegularNknStateRefreshSendInFlight <= 0)
                    {
                        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
                        context.V6RegularNknStateRefreshActiveRequestId = null;
                        context.V6RegularNknStateRefreshActivePriority = null;
                        context.V6RegularNknStateRefreshActiveStartedUtc = null;
                    }
                }
                else if (!retiredSendObserved &&
                         ReferenceEquals(outboundTransfer, context) &&
                         context.V6RegularNknStateRefreshRetiredSendGeneration == sendGeneration)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_regular_nkn_state_refresh_retired_send_observed; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; outcome=finally; retired_request_id={FormatProtocolLogValue(context.V6RegularNknStateRefreshRetiredRequestId ?? "(none)")}; active_generation={context.V6RegularNknStateRefreshActiveSendGeneration}; active_request_id={FormatProtocolLogValue(context.V6RegularNknStateRefreshActiveRequestId ?? "(none)")}");
                }
            }
        }
    }

    private static bool TryRetireOutboundPostTunaFallbackStaleStateRefreshSendLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        DateTimeOffset now)
    {
        var staleInflightRequest = IsPostTunaFallbackStaleInflightStateRefreshRequest(request);
        var staleCreditRequest = IsPostTunaFallbackStaleCreditStateRefreshRequest(request);
        var tailReconciliationRequest = IsPostTunaFallbackTailReconciliationStateRefresh(request);
        if ((!staleInflightRequest && !staleCreditRequest && !tailReconciliationRequest) ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullPostTunaRecoveryActive ||
            context.V6RegularNknStateRefreshSendInFlight <= 0 ||
            context.V6RegularNknStateRefreshFailureCount < Math.Min(
                V6PostTunaFallbackStaleInflightRecoveryMinStateRefreshFailures,
                V6PostTunaFallbackStaleCreditRecoveryMinStateRefreshFailures))
        {
            return false;
        }

        var activeStartedUtc = context.V6RegularNknStateRefreshActiveStartedUtc;
        var activeAgeMs = activeStartedUtc is { } started
            ? (long)Math.Max(0, (now - started).TotalMilliseconds)
            : -1;
        var activeGeneration = context.V6RegularNknStateRefreshActiveSendGeneration;
        var activeRequestId = context.V6RegularNknStateRefreshActiveRequestId ?? "(none)";
        var activePriority = context.V6RegularNknStateRefreshActivePriority ?? "(none)";
        var activeTimedOut = activeAgeMs < 0 ||
            activeAgeMs >= V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs;
        var activeWasStaleInflight = string.Equals(
            context.V6RegularNknStateRefreshActivePriority,
            V6RegularNknStateRefreshStaleInflightPriority,
            StringComparison.Ordinal);
        var retireReason = activeTimedOut
            ? "state_refresh_send_timeout_elapsed"
            : activeWasStaleInflight
                ? "stale_inflight_state_refresh_superseded"
                : tailReconciliationRequest
                    ? "tail_reconciliation_forced"
                : staleCreditRequest
                    ? "stale_credit_recovery_forced"
                    : "stale_inflight_recovery_forced";

        context.V6RegularNknStateRefreshRetiredSendGeneration = activeGeneration;
        context.V6RegularNknStateRefreshRetiredRequestId = context.V6RegularNknStateRefreshActiveRequestId;
        context.V6RegularNknStateRefreshRetiredSendCount++;
        context.V6RegularNknStateRefreshSendInFlight = 0;
        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
        context.V6RegularNknStateRefreshActiveRequestId = null;
        context.V6RegularNknStateRefreshActivePriority = null;
        context.V6RegularNknStateRefreshActiveStartedUtc = null;
        context.PullTransportResumeRequestPending = true;
        context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_stale_state_refresh_retired";
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_stale_state_refresh_retired";

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(retireReason)}; retired_generation={activeGeneration}; retired_request_id={FormatProtocolLogValue(activeRequestId)}; retired_priority={FormatProtocolLogValue(activePriority)}; replacement_request_id={FormatProtocolLogValue(request.RepairRequestId)}; replacement_priority={FormatProtocolLogValue(request.Priority)}; active_age_ms={activeAgeMs}; timeout_ms={V6RegularNknSparseRuntimeStateRefreshSendTimeoutMs}; retired_send_count={context.V6RegularNknStateRefreshRetiredSendCount}; failure_count={context.V6RegularNknStateRefreshFailureCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex)}; available_credit_chunks={Math.Max(0, Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport)}; rebind_generation={context.PullTransportRebindGeneration}");
        return true;
    }

    private bool TryObserveRetiredOutboundV4SparseRuntimeStateRefreshSend(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        long sendGeneration,
        string outcome)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                sendGeneration <= 0)
            {
                return false;
            }

            var staleGeneration =
                context.V6RegularNknStateRefreshRetiredSendGeneration >= sendGeneration ||
                sendGeneration < context.V6RegularNknStateRefreshSendGeneration ||
                context.V6RegularNknStateRefreshActiveSendGeneration > 0 &&
                sendGeneration < context.V6RegularNknStateRefreshActiveSendGeneration;
            if (!staleGeneration)
            {
                return false;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_regular_nkn_state_refresh_retired_send_observed; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; generation={sendGeneration}; outcome={FormatProtocolLogValue(outcome)}; retired_request_id={FormatProtocolLogValue(context.V6RegularNknStateRefreshRetiredRequestId ?? "(none)")}; active_generation={context.V6RegularNknStateRefreshActiveSendGeneration}; active_request_id={FormatProtocolLogValue(context.V6RegularNknStateRefreshActiveRequestId ?? "(none)")}");
            return true;
        }
    }

    private bool TryDeferOutboundPostTunaFallbackStateRefreshForTunaActivation(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason,
        Exception? ex)
    {
        var signalSenderPump = false;
        var suppressActivationPause = false;
        var shouldDefer = false;
        var pauseReason = "tuna_activation_negotiating";
        long feedbackSilenceMs = 0;
        var transportBacklogChunks = 0;
        var availableCreditChunks = 0;
        var creditCeiling = 0;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context))
            {
                return false;
            }

            shouldDefer =
                ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(context) ||
                IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason) ||
                IsTunaActivationNegotiationTransportPauseReason(context.PullTransportLastPauseReason) ||
                IsTunaActivationNegotiationDataSessionUnavailableException(ex);
            if (!shouldDefer)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            pauseReason = context.PullTransportPauseReason ??
                context.PullTransportLastPauseReason ??
                pauseReason;
            feedbackSilenceMs = context.PullV4LastPeerFrameReceivedUtc is { } lastPeerFrame
                ? (long)Math.Max(0, (now - lastPeerFrame).TotalMilliseconds)
                : 0;
            transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
            creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
            availableCreditChunks = Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport);
            if (ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context))
            {
                suppressActivationPause = true;
                if (IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
                {
                    context.PullTransportPaused = false;
                    context.PullTransportPausedSinceUtc = null;
                    context.PullTransportGraceDeadlineUtc = null;
                    context.PullTransportPauseReason = null;
                    context.PullTransportResumeRequestPending = false;
                }

                context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_tuna_activation_pause_suppressed";
                context.V6SenderPumpLastWakeReason = "post_tuna_fallback_tuna_activation_pause_suppressed";
                signalSenderPump = true;
            }

            if (!suppressActivationPause)
            {
                context.V6EpochLivenessDeferralCount++;
                context.V6EpochLivenessDeferralUtc = now;
                context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_deferred_for_tuna_activation";
                context.V6SenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_deferred_for_tuna_activation";
                MarkOutboundV4SenderPumpTransportPausedLocked(context);
                signalSenderPump = true;
            }
        }

        if (suppressActivationPause)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_state_refresh_tuna_activation_pause_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; pause_reason={FormatProtocolLogValue(pauseReason)}; error={FormatProtocolLogValue(ex?.GetType().Name ?? "OperationCanceledException")}; message={FormatProtocolLogValue(ex?.Message ?? "send canceled or timed out during tuna activation")}; feedback_silence_ms={feedbackSilenceMs}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
        }
        else
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_state_refresh_deferred_for_tuna_activation; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; pause_reason={FormatProtocolLogValue(pauseReason)}; error={FormatProtocolLogValue(ex?.GetType().Name ?? "OperationCanceledException")}; message={FormatProtocolLogValue(ex?.Message ?? "send canceled or timed out during tuna activation")}; feedback_silence_ms={feedbackSilenceMs}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
        }

        if (signalSenderPump)
        {
            SignalOutboundSparseSenderPump(context);
        }

        return !suppressActivationPause;
    }

    private void RequestOutboundPostTunaFallbackStateRefreshReceiveRecovery(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        string reason)
    {
        FileTransferReceiveRecoveryRequest? recoveryRequest = null;
        var signalSenderPump = false;
        var staleInflightEscalation = IsPostTunaFallbackStaleInflightStateRefreshRequest(request);
        var staleCreditEscalation = IsPostTunaFallbackStaleCreditStateRefreshRequest(request);
        var tailReconciliationEscalation = IsPostTunaFallbackTailReconciliationStateRefresh(request);
        var stateRefreshSendTimeout = string.Equals(
            reason,
            "post_tuna_fallback_state_refresh_send_timeout",
            StringComparison.Ordinal);
        var bypassRecoveryCooldown = staleInflightEscalation || staleCreditEscalation || tailReconciliationEscalation || stateRefreshSendTimeout;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var feedbackSilence = context.PullV4LastPeerFrameReceivedUtc is { } lastPeerFrame
                ? now - lastPeerFrame
                : TimeSpan.Zero;
            var transportBacklogChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
            var creditCeiling = Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount);
            var availableCreditChunks = Math.Max(0, creditCeiling - context.ChunksAcceptedForTransport);

            context.V6RegularNknStateRefreshFailureCount++;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_receive_recovery";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_state_refresh_receive_recovery";
            if (stateRefreshSendTimeout ||
                string.Equals(reason, "post_tuna_fallback_state_refresh_send_failed", StringComparison.Ordinal))
            {
                TryRetireOutboundFallbackCheckpointRequestLocked(context, request.RepairRequestId, reason);
            }

            MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, reason);
            signalSenderPump = true;

            if (!bypassRecoveryCooldown &&
                context.V6LastReceiveRecoveryRequestedUtc is { } lastRecovery &&
                now - lastRecovery < CurrentV6SenderRequestFeedbackStallRecoveryCooldown)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; suppression_reason=recovery_cooldown; cooldown_ms={(long)CurrentV6SenderRequestFeedbackStallRecoveryCooldown.TotalMilliseconds}; last_recovery_age_ms={(long)Math.Max(0, (now - lastRecovery).TotalMilliseconds)}; failure_count={context.V6RegularNknStateRefreshFailureCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
                recoveryRequest = null;
            }
            else if (!bypassRecoveryCooldown &&
                     context.PullV4LastPeerFrameReceivedUtc.HasValue &&
                     feedbackSilence < CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay &&
                     transportBacklogChunks > 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; suppression_reason=fresh_peer_progress; stale_credit_recovery_delay_ms={(long)CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay.TotalMilliseconds}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; failure_count={context.V6RegularNknStateRefreshFailureCount}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");
                recoveryRequest = null;
            }
            else
            {
                context.V6LastReceiveRecoveryRequestedUtc = now;
                var recoveryReason = staleInflightEscalation
                    ? "post_tuna_fallback_stale_inflight_repair_failed"
                    : staleCreditEscalation
                        ? "post_tuna_fallback_stale_credit_repair_failed"
                    : tailReconciliationEscalation
                        ? "post_tuna_fallback_tail_reconciliation_failed"
                    : "post_tuna_fallback_state_refresh_failed";
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; request_id={FormatProtocolLogValue(request.RepairRequestId)}; stale_inflight_recovery={(staleInflightEscalation ? 1 : 0)}; stale_credit_recovery={(staleCreditEscalation ? 1 : 0)}; tail_reconciliation={(tailReconciliationEscalation ? 1 : 0)}; state_refresh_send_timeout={(stateRefreshSendTimeout ? 1 : 0)}; recovery_reason={FormatProtocolLogValue(recoveryReason)}; failure_count={context.V6RegularNknStateRefreshFailureCount}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; highest_accepted_chunk_index={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; transport_backlog_chunks={transportBacklogChunks}; available_credit_chunks={availableCreditChunks}; credit_ceiling_chunk_index={creditCeiling}; rebind_generation={context.PullTransportRebindGeneration}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(context.BridgeRecoveryPolicy)}");

                recoveryRequest = AttachFallbackLegAuthority(
                    context,
                    new FileTransferReceiveRecoveryRequest(
                        context.SessionId,
                        context.TransferId,
                        FileTransferDirection.Outbound,
                        recoveryReason),
                    recoveryReason,
                    request.RepairRequestId,
                    request.TransportEpoch);
            }
        }

        if (signalSenderPump)
        {
            SignalOutboundSparseSenderPump(context);
        }

        if (recoveryRequest is not null)
        {
            TryRequestFileTransferReceiveRecovery(recoveryRequest);
        }
    }

    private static bool IsPostTunaFallbackStaleInflightStateRefreshRequest(FileTransferFrontierRequestFrameV6 request)
        => string.Equals(request.Priority, V6RegularNknStateRefreshStaleInflightPriority, StringComparison.Ordinal);

    private static bool IsPostTunaFallbackStaleCreditStateRefreshRequest(FileTransferFrontierRequestFrameV6 request)
        => string.Equals(request.Priority, V6RegularNknStateRefreshStaleCreditPriority, StringComparison.Ordinal);

    private static bool IsPostTunaFallbackTailReconciliationStateRefresh(FileTransferFrontierRequestFrameV6 request)
        => string.Equals(request.Priority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal);

    private static bool IsPostTunaFallbackTailReconciliationStateRefresh(FileTransferReceiverStateFrameV6 state)
        => string.Equals(state.Priority, V6RegularNknStateRefreshTailReconciliationPriority, StringComparison.Ordinal);

    private static void MaybeLogOutboundV4SparseRuntimePeerSilenceDeferralLocked(
        OutboundTransferContext context,
        string reason,
        TimeSpan silence,
        TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.PullV4PeerSilenceDeferralUtc is { } lastLog &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            context.PullV4PeerSilenceDeferralCount++;
            return;
        }

        context.PullV4PeerSilenceDeferralUtc = now;
        context.PullV4PeerSilenceDeferralCount++;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_peer_feedback_timeout_deferred_for_v6_regular_nkn_sparse_runtime; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; deferral_count={context.PullV4PeerSilenceDeferralCount}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}");
    }

    private static void MaybeLogOutboundPrimaryRegularNknBulkV6PeerSilenceDeferralLocked(
        OutboundTransferContext context,
        string reason,
        TimeSpan silence,
        TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.PullV4PeerSilenceDeferralUtc is { } lastLog &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            context.PullV4PeerSilenceDeferralCount++;
            return;
        }

        context.PullV4PeerSilenceDeferralUtc = now;
        context.PullV4PeerSilenceDeferralCount++;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_bulk_v6_peer_feedback_timeout_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; deferral_count={context.PullV4PeerSilenceDeferralCount}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; rebind_generation={context.PullTransportRebindGeneration}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}");
    }

    private static void MaybeLogOutboundPostTunaFallbackPeerSilenceDeferralLocked(
        OutboundTransferContext context,
        string reason,
        TimeSpan silence,
        TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.PullV4PeerSilenceDeferralUtc is { } lastLog &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            context.PullV4PeerSilenceDeferralCount++;
            return;
        }

        context.PullV4PeerSilenceDeferralUtc = now;
        context.PullV4PeerSilenceDeferralCount++;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_post_tuna_fallback_peer_feedback_timeout_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; deferral_count={context.PullV4PeerSilenceDeferralCount}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; rebind_generation={context.PullTransportRebindGeneration}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairRequests.Count}");
    }

    private static TimeSpan ResolveV4PeerSilenceTimeout(bool postFallback)
        => V4PeerSilenceTimeoutOverrideForTests ??
           (postFallback ? PullV4PostFallbackPeerSilenceTimeout : PullV4PeerSilenceTimeout);

    private static bool IsOutboundPostTunaRecoveryActiveLocked(OutboundTransferContext context)
        => context.PullPostTunaRecoveryActive ||
           context.PullTransportRebindGeneration > 0 ||
           IsRecoveredV6TunaFallbackEpochLocked(context.LastRecoveredV6TransportEpochKind, context.LastRecoveredV6TransportTargetTransport);

    private static bool IsInboundPostTunaRecoveryActiveLocked(InboundTransferContext context)
        => context.PullPostTunaRecoveryActive ||
           context.PullTransportRebindGeneration > 0 ||
           IsRecoveredV6TunaFallbackEpochLocked(context.LastRecoveredV6TransportEpochKind, context.LastRecoveredV6TransportTargetTransport);

    private static bool IsOutboundFileTunaV4PostTunaRecoveryActiveLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           IsOutboundPostTunaRecoveryActiveLocked(context);

    private static bool IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6 &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsOutboundPostTunaRecoveryActiveLocked(context);

    private static bool IsInboundFileTunaV4PostTunaRecoveryActiveLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           IsInboundPostTunaRecoveryActiveLocked(context);

    private static int GetOutboundPostTunaRecoveryGenerationLocked(OutboundTransferContext context)
        => context.PullTransportRebindGeneration > 0
            ? context.PullTransportRebindGeneration
            : context.PullPostTunaRecoveryGeneration > 0
                ? context.PullPostTunaRecoveryGeneration
                : IsRecoveredV6TunaFallbackEpochLocked(context.LastRecoveredV6TransportEpochKind, context.LastRecoveredV6TransportTargetTransport)
                    ? (int)Math.Min(int.MaxValue, Math.Max(0, context.LastRecoveredV6TransportEpoch))
                    : 0;

    private static int GetInboundPostTunaRecoveryGenerationLocked(InboundTransferContext context)
        => context.PullTransportRebindGeneration > 0
            ? context.PullTransportRebindGeneration
            : context.PullPostTunaRecoveryGeneration > 0
                ? context.PullPostTunaRecoveryGeneration
                : IsRecoveredV6TunaFallbackEpochLocked(context.LastRecoveredV6TransportEpochKind, context.LastRecoveredV6TransportTargetTransport)
                    ? (int)Math.Min(int.MaxValue, Math.Max(0, context.LastRecoveredV6TransportEpoch))
                    : 0;

    private static bool IsRecoveredV6TunaFallbackEpochLocked(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
           targetTransport == FileTransferTransportKind.RegularNkn;

    private static bool IsDedicatedV6TransportEpochKind(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => handoffKind is FileTransferTransportHandoffKind.NormalToTunaActivation
               or FileTransferTransportHandoffKind.TunaToNormalFallback
               or FileTransferTransportHandoffKind.TunaRestart ||
           targetTransport == FileTransferTransportKind.Tuna;

    private static bool ShouldSuppressLegacyV6HandoffForDedicatedV6EpochLocked(
        OutboundTransferContext context,
        long transportEpoch)
    {
        if (transportEpoch <= 0)
        {
            return false;
        }

        if (context.V6TransportEpoch is { } current &&
            current.EpochId == transportEpoch &&
            IsDedicatedV6TransportEpochKind(current.Kind, current.TargetTransport))
        {
            return true;
        }

        return context.LastRecoveredV6TransportEpoch == transportEpoch &&
               IsDedicatedV6TransportEpochKind(
                   context.LastRecoveredV6TransportEpochKind,
                   context.LastRecoveredV6TransportTargetTransport);
    }

    private static void LogLegacyV6HandoffSuppressedForDedicatedV6Epoch(
        OutboundTransferContext context,
        long transportEpoch,
        string frameType,
        string reason)
    {
        var handoffKind = context.V6TransportEpoch is { EpochId: var currentEpoch } current && currentEpoch == transportEpoch
            ? current.Kind
            : context.LastRecoveredV6TransportEpochKind;
        var targetTransport = context.V6TransportEpoch is { EpochId: var currentEpochForTarget } currentForTarget && currentEpochForTarget == transportEpoch
            ? currentForTarget.TargetTransport
            : context.LastRecoveredV6TransportTargetTransport;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_legacy_handoff_suppressed_for_epoch; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; transport_epoch={transportEpoch}; reason={FormatProtocolLogValue(reason)}; handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
    }

    private void MarkInboundV4PeerFrameReceived(InboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                var now = DateTimeOffset.UtcNow;
                context.PullV4LastPeerFrameReceivedUtc = now;
                context.PullV4PeerSilenceDeferralUtc = null;
                context.PullV4PeerSilenceDeferralCount = 0;
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
            if (postFallback &&
                ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context))
            {
                MaybeLogInboundPostTunaFallbackPeerSilenceDeferralLocked(context, reason, silence, timeout);
                return false;
            }

            if (!postFallback &&
                ShouldUseV6RegularNknSparseRuntime(context) &&
                IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                MaybeLogInboundV4SparseRuntimePeerSilenceDeferralLocked(context, reason, silence, timeout);
                return false;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v4_peer_receive_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}; missing_range_count={(context.NextChunkIndex <= context.PullHighestReceivedChunkIndex ? 1 : 0)}");
            return true;
        }
    }

    private static void MaybeLogInboundV4SparseRuntimePeerSilenceDeferralLocked(
        InboundTransferContext context,
        string reason,
        TimeSpan silence,
        TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.PullV4PeerSilenceDeferralUtc is { } lastLog &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            context.PullV4PeerSilenceDeferralCount++;
            return;
        }

        context.PullV4PeerSilenceDeferralUtc = now;
        context.PullV4PeerSilenceDeferralCount++;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_peer_receive_timeout_deferred_for_v6_regular_nkn_sparse_runtime; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; deferral_count={context.PullV4PeerSilenceDeferralCount}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}; missing_range_count={(context.NextChunkIndex <= context.PullHighestReceivedChunkIndex ? 1 : 0)}");
    }

    private static void MaybeLogInboundPostTunaFallbackPeerSilenceDeferralLocked(
        InboundTransferContext context,
        string reason,
        TimeSpan silence,
        TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.PullV4PeerSilenceDeferralUtc is { } lastLog &&
            now - lastLog < TimeSpan.FromSeconds(10))
        {
            context.PullV4PeerSilenceDeferralCount++;
            return;
        }

        context.PullV4PeerSilenceDeferralUtc = now;
        context.PullV4PeerSilenceDeferralCount++;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v4_post_tuna_fallback_peer_receive_timeout_deferred; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; deferral_count={context.PullV4PeerSilenceDeferralCount}; silence_ms={(long)Math.Max(0, silence.TotalMilliseconds)}; timeout_ms={(long)timeout.TotalMilliseconds}; rebind_generation={context.PullTransportRebindGeneration}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}; missing_range_count={(context.NextChunkIndex <= context.PullHighestReceivedChunkIndex ? 1 : 0)}");
    }

    private static bool ShouldUsePostTunaFallbackV6SparseRuntimeLocked(OutboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           (context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteRuntime.UsesV6SparsePump);

    private static bool ShouldUsePostTunaFallbackV6SparseRuntimeLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           (context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteRuntime.UsesV6SparsePump);

    private static bool ShouldUsePostTunaFallbackV6FeedbackEnvelope(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteRuntime.UsesV6FeedbackEnvelope;

    private static bool ShouldPauseOutboundV4SenderPumpForV6RegularNknSparseRuntimeLocked(OutboundTransferContext context)
        => context.PullTransportPaused &&
           ShouldUseV6RegularNknSparseRuntime(context) &&
           IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

    private static bool ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(OutboundTransferContext context)
        => context.PullTransportPaused &&
           IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason) &&
           !ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context);

    private static bool ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(OutboundTransferContext context)
        => context.PullTransportPaused &&
           context.RouteRuntime.UsesRegularNknV4FastRuntime &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           IsRegularNknV4RecoveryTransportPauseReason(context.PullTransportPauseReason);

    private static bool IsRegularNknV4RecoveryTransportPauseReason(string? reason)
    {
        var normalized = NormalizeReason(reason);
        return normalized is "receive_stall_recovery" or
            "transport_recovered_unproven" or
            "transport_probe_unproven";
    }

    private static bool ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullPostTunaRecoveryActive;

    private static bool ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.PullPostTunaRecoveryActive;

    private static bool ShouldPauseOutboundV4SenderPumpForFileTunaV4PostTunaRecoveryLocked(OutboundTransferContext context)
        => context.PullTransportPaused &&
           IsOutboundFileTunaV4PostTunaRecoveryActiveLocked(context) &&
           IsTunaFallbackTransportPauseReason(context.PullTransportPauseReason);

    private static bool IsFileTunaV4PostTunaRecoveryActiveLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
           context.PullPostTunaRecoveryActive;

    private static bool ShouldBlockOutboundV4NormalSendAheadForFileTunaV4PostTunaRecoveryLocked(OutboundTransferContext context)
        => IsFileTunaV4PostTunaRecoveryActiveLocked(context) &&
           context.PullTransportFrontierOnlyRepairActive;

    private static bool ShouldPauseOutboundV4SenderPumpForTransportPauseLocked(OutboundTransferContext context)
        => ShouldPauseOutboundV4SenderPumpForV6RegularNknSparseRuntimeLocked(context) ||
           ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(context) ||
           ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context) ||
           ShouldPauseOutboundV4SenderPumpForFileTunaV4PostTunaRecoveryLocked(context);

    private static bool ShouldAllowOutboundV4RepairWhileTransportPausedLocked(OutboundTransferContext context)
    {
        if (!context.PullTransportPaused ||
            context.PullV4SenderPumpRepairQueue.Count == 0)
        {
            return false;
        }

        if (ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context))
        {
            return true;
        }

        if (!IsTunaFallbackRepairDrainTransportPauseReason(context.PullTransportPauseReason))
        {
            return false;
        }

        return ShouldUseV6RegularNknSparseRuntime(context) ||
            ShouldPauseOutboundV4SenderPumpForFileTunaV4PostTunaRecoveryLocked(context);
    }

    private static bool ShouldAllowOutboundV4RepairSendWhileTransportPausedLocked(
        OutboundTransferContext context,
        bool repairSend)
        => repairSend &&
           context.PullTransportPaused &&
           (ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context) ||
            (IsTunaFallbackRepairDrainTransportPauseReason(context.PullTransportPauseReason) &&
             (ShouldUseV6RegularNknSparseRuntime(context) ||
              ShouldPauseOutboundV4SenderPumpForFileTunaV4PostTunaRecoveryLocked(context))));

    private static bool ShouldBlockOutboundV4TransportSendForTransportPauseLocked(
        OutboundTransferContext context,
        bool repairSend)
        => ShouldPauseOutboundV4SenderPumpForTransportPauseLocked(context) &&
           !ShouldAllowOutboundV4RepairSendWhileTransportPausedLocked(context, repairSend);

    private static bool ShouldInterruptOutboundV4TransportSendOnPumpSignal(OutboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime ||
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
           ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(context) ||
           ShouldUseV6RegularNknSparseRuntime(context);

    private static bool IsTunaActivationNegotiationDataSessionUnavailableException(Exception? ex)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is InvalidOperationException &&
            ex.Message.Contains("File-transfer data session is unavailable", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("tuna_activation_negotiating", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ex is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsTunaActivationNegotiationDataSessionUnavailableException);
        }

        return IsTunaActivationNegotiationDataSessionUnavailableException(ex.InnerException);
    }

    private static bool IsRecentRecoveredTunaActivationCancellationLocked(
        OutboundTransferContext context,
        bool dataSessionAvailable,
        Exception ex)
    {
        if (ex is not OperationCanceledException ||
            !dataSessionAvailable ||
            !IsTunaActivationNegotiationTransportPauseReason(context.PullTransportLastPauseReason) ||
            context.PullTransportLastResumeUtc is not { } lastResumeUtc)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - lastResumeUtc;
        return age >= TimeSpan.Zero &&
            age <= TimeSpan.FromMilliseconds(TunaActivationSendCancellationRecoveryWindowMs);
    }

    private bool TryDeferOutboundV4SendForTunaActivationPauseLocked(
        OutboundTransferContext context,
        bool dataSessionAvailable,
        Exception sendException,
        out string pauseReason,
        out int rebindGeneration,
        out bool activationPauseStarted,
        out bool activationPauseAlreadyRecovered)
    {
        pauseReason = context.PullTransportPauseReason ?? "tuna_activation_negotiating";
        rebindGeneration = context.PullTransportRebindGeneration;
        activationPauseStarted = false;
        activationPauseAlreadyRecovered = false;

        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal)
        {
            return false;
        }

        var alreadyPausedForActivation = ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(context);
        var explicitActivationUnavailable = IsTunaActivationNegotiationDataSessionUnavailableException(sendException);
        var recentRecoveredActivationCancellation = IsRecentRecoveredTunaActivationCancellationLocked(
            context,
            dataSessionAvailable,
            sendException);
        if (!alreadyPausedForActivation && !explicitActivationUnavailable && !recentRecoveredActivationCancellation)
        {
            return false;
        }

        if (!alreadyPausedForActivation &&
            dataSessionAvailable &&
            (explicitActivationUnavailable || recentRecoveredActivationCancellation))
        {
            activationPauseAlreadyRecovered = true;
            pauseReason = "tuna_activation_negotiating";
            context.SparseSenderPumpLastWakeReason = "tuna_activation_send_canceled_after_resume";
            return true;
        }

        if (!alreadyPausedForActivation)
        {
            activationPauseStarted = TryPauseOutboundTransportLocked(
                context,
                "tuna_activation_negotiating",
                requiresResumeRequest: false);
        }

        pauseReason = context.PullTransportPauseReason ?? pauseReason;
        rebindGeneration = context.PullTransportRebindGeneration;
        MarkOutboundV4SenderPumpTransportPausedLocked(context);
        return true;
    }

    private async Task<bool> SendOutboundV4PrePumpFrameWithTunaActivationPauseRetryAsync(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        FileTransferDataFrame frame,
        string frameKind,
        int payloadBytes)
    {
        var loggedDeferred = false;
        while (true)
        {
            context.LifetimeCts.Token.ThrowIfCancellationRequested();
            Task? waitForSignal = null;
            string pauseReason = "tuna_activation_negotiating";

            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) ||
                    context.IsTerminal)
                {
                    return false;
                }

                if (ShouldPauseOutboundV4SenderPumpForTunaActivationNegotiationLocked(context))
                {
                    pauseReason = context.PullTransportPauseReason ?? pauseReason;
                    MarkOutboundV4SenderPumpTransportPausedLocked(context);
                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                }
            }

            if (waitForSignal is not null)
            {
                if (!loggedDeferred)
                {
                    loggedDeferred = true;
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_v4_pre_pump_send_deferred_for_tuna_activation_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_kind={FormatProtocolLogValue(frameKind)}; frame_type={frame.Type}; payload_bytes={payloadBytes}; reason={FormatProtocolLogValue(pauseReason)}; rebind_generation={context.PullTransportRebindGeneration}; error=none; message=already_paused");
                }

                await WaitForOutboundV4TransportPauseSignalAsync(context, waitForSignal).ConfigureAwait(false);
                continue;
            }

            try
            {
                await dataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                bool deferred;
                bool activationPauseStarted;
                var rebindGeneration = 0;
                lock (gate)
                {
                    deferred = TryDeferOutboundV4SendForTunaActivationPauseLocked(
                        context,
                        dataSession.IsAvailable,
                        ex,
                        out pauseReason,
                        out rebindGeneration,
                        out activationPauseStarted,
                        out var activationPauseAlreadyRecovered);
                    if (deferred)
                    {
                        waitForSignal = activationPauseAlreadyRecovered
                            ? null
                            : context.ResetAndGetSparseSenderPumpSignalTask();
                    }
                }

                if (!deferred)
                {
                    throw;
                }

                if (activationPauseStarted)
                {
                    LogTransportPaused(FileTransferDirection.Outbound, context.TransferId, context.SessionId, pauseReason);
                    ScheduleOutboundV4TransportPauseControlRetry(context, paused: true, pauseReason);
                }

                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v4_pre_pump_send_deferred_for_tuna_activation_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_kind={FormatProtocolLogValue(frameKind)}; frame_type={frame.Type}; payload_bytes={payloadBytes}; reason={FormatProtocolLogValue(pauseReason)}; rebind_generation={rebindGeneration}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
                loggedDeferred = true;

                if (waitForSignal is not null)
                {
                    await WaitForOutboundV4TransportPauseSignalAsync(context, waitForSignal).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task WaitForOutboundV4TransportPauseSignalAsync(
        OutboundTransferContext context,
        Task waitForSignal)
        => await Task.WhenAny(
                waitForSignal,
                Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
            .ConfigureAwait(false);

    private static bool IsTunaFallbackRepairDrainTransportPauseReason(string? pauseReason)
    {
        var reason = NormalizeReason(pauseReason);
        return IsTunaFallbackTransportPauseReason(reason) ||
            reason is "remote_closed" or
            "remote_remote_closed" or
            "sidecar_remote_closed" or
            "tuna_fallback_to_nkn" or
            "transport_disconnected" ||
            reason?.Contains("sidecar", StringComparison.OrdinalIgnoreCase) == true ||
            reason?.Contains("tuna", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void MarkOutboundV4SenderPumpTransportPausedLocked(OutboundTransferContext context)
    {
        var eventName = ShouldPauseOutboundV4SenderPumpForV6RegularNknSparseRuntimeLocked(context)
            ? "filetransfer_v4_sender_pump_transport_paused_for_v6_regular_nkn_sparse_runtime"
            : ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context)
                ? "filetransfer_v4_sender_pump_transport_paused_for_regular_v4_receive_stall_recovery"
            : "filetransfer_v4_sender_pump_transport_paused";
        MarkOutboundV4SenderPumpTransportPausedLocked(context, eventName);
    }

    private static string ResolveOutboundV4PendingSendsAbandonedForTransportPauseEventName(OutboundTransferContext context)
        => ShouldPauseOutboundV4SenderPumpForV6RegularNknSparseRuntimeLocked(context)
            ? "filetransfer_v4_pending_transport_sends_abandoned_for_v6_regular_nkn_sparse_runtime"
            : ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context)
                ? "filetransfer_v4_pending_transport_sends_abandoned_for_regular_v4_receive_stall_recovery"
                : "filetransfer_v4_pending_transport_sends_abandoned_for_transport_pause";

    private void MarkOutboundV4SenderPumpTransportPausedLocked(OutboundTransferContext context, string eventName)
    {
        context.PullSenderSendWaitCountRecent++;
        context.PullSenderSendWaitCountTotal++;
        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
        var shouldLog = !string.Equals(context.SparseSenderPumpLastWakeReason, "transport_paused", StringComparison.Ordinal);
        context.SparseSenderPumpLastWakeReason = "transport_paused";
        MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
        if (shouldLog)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(context.PullTransportPauseReason ?? "transport_paused")}; rebind_generation={context.PullTransportRebindGeneration}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; bytes_transferred={context.BytesTransferred}; bytes_accepted_for_transport={context.BytesAcceptedForTransport}; pending_repair_count={context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count)}");
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
            bool completeFromTerminalReady = false;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return;
                }

                var transportPaused = ShouldPauseOutboundV4SenderPumpForTransportPauseLocked(context);
                var allowPausedRepair = transportPaused &&
                    ShouldAllowOutboundV4RepairWhileTransportPausedLocked(context);
                if (ShouldCompleteOutboundV4FromTerminalReadyStateLocked(context))
                {
                    completeFromTerminalReady = true;
                    context.State = FileTransferTransferState.AwaitingCompletion;
                    context.StatusMessage = "Receiver verified transfer.";
                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: true);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_terminal_ready_completion_inferred; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={context.NegotiatedDataProtocolVersion}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunk_count={context.ChunkCount}; bytes_acknowledged_by_receiver={context.BytesAcknowledgedByReceiver}; file_size_bytes={context.FileSizeBytes}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
                }
                else if (context.UserPaused || context.PeerPaused || (transportPaused && !allowPausedRepair))
                {
                    if (transportPaused)
                    {
                        MarkOutboundV4SenderPumpTransportPausedLocked(context);
                    }
                    else
                    {
                        MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                    }

                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
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
                    if (ShouldBlockOutboundV4NormalSendAheadForFileTunaV4PostTunaRecoveryLocked(context))
                    {
                        MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, "file_tuna_v4_post_tuna_frontier_repair_wait");
                        if (context.PullV4SenderPumpRepairQueue.Count == 0)
                        {
                            context.PullSenderSendWaitCountRecent++;
                            context.PullSenderSendWaitCountTotal++;
                            context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                            context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                            context.SparseSenderPumpLastWakeReason = "file_tuna_v4_post_tuna_frontier_repair_wait";
                            MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                            waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                        }
                    }
                    else if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (context.V6TransportHandoff is { State: not V6TransportHandoffState.Recovered } handoff &&
                            now - GetV6TransportHandoffActivityUtc(handoff) >= V6TransportHandoffWaitingTimeout &&
                            !HasOutboundV4RepairWorkInProgressLocked(context))
                        {
                            TrySetV6TransportHandoffState(
                                handoff,
                                FileTransferDirection.Outbound,
                                context.TransferId,
                                context.SessionId,
                                V6TransportHandoffState.WaitingForRegularNkn,
                                "proof_timeout",
                                context.RemoteNextExpectedChunkIndex,
                                Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                            context.StatusMessage = "Waiting for regular NKN";
                        }

                        context.PullSenderSendWaitCountRecent++;
                        context.PullSenderSendWaitCountTotal++;
                        context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                        context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                        context.SparseSenderPumpLastWakeReason = "v6_handoff_tail_blocked";
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V6TransportHandoff!.EpochId}; state={FormatV6TransportHandoffState(context.V6TransportHandoff.State)}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; repair_queue_depth={context.PullV4SenderPumpRepairQueue.Count}");
                        MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, "v6_handoff_tail_blocked");
                        MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                        waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
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
                            MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(
                                context,
                                "regular_v4_sender_wait");
                            if (context.PullV4SenderPumpRepairQueue.Count > 0)
                            {
                                continue;
                            }

                            context.PullSenderSendWaitCountRecent++;
                            context.PullSenderSendWaitCountTotal++;
                            context.PullSenderFeedCreditWaitStartedUtc ??= DateTimeOffset.UtcNow;
                            context.V4SenderCreditExhaustedSinceUtc ??= DateTimeOffset.UtcNow;
                            MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                            waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                        }
                    }
                }
                else
                {
                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                    waitForSignal = context.ResetAndGetSparseSenderPumpSignalTask();
                }
            }

            if (completeFromTerminalReady)
            {
                await TransitionOutboundToTerminalAsync(
                    context,
                    FileTransferTransferState.Completed,
                    errorCode: null,
                    statusMessage: "Transfer complete.",
                    notifyPeer: false,
                    cancelReason: null,
                    ct: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (repairSend is not null)
            {
                var repairDelivered = true;
                if (repairSend.ChunkIndices.Count > 0)
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context))
                        {
                            context.V4SenderPumpLastRepairRequestKey = repairSend.RepairRequestKey;
                        }
                    }

                    repairDelivered = await SendChunkIndicesV4Async(
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
                            if (repairDelivered)
                            {
                                repairState.LastSentUtc = sentUtc;
                                repairState.SentCount++;
                                repairState.LastSentRemoteFrontierChunkIndex = repairSend.RemoteNextExpectedChunkIndex;
                            }
                        }
                    }
                }

                if (!repairDelivered)
                {
                    var requeuedRegularV4PeerSilenceRepair = false;
                    var requeuedRegularV4PeerSilenceRepairChunkCount = 0;
                    if (string.Equals(repairSend.DeliveryEscalationReason, "regular_v4_peer_silence_safety_replay", StringComparison.Ordinal))
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(outboundTransfer, context) &&
                                !context.IsTerminal)
                            {
                                requeuedRegularV4PeerSilenceRepair =
                                    TryRequeueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(
                                        context,
                                        repairSend,
                                        out requeuedRegularV4PeerSilenceRepairChunkCount);
                            }
                        }
                    }

                    var deferredEventName = IsFileTunaV4PostTunaRecoveryActiveLocked(context)
                        ? "filetransfer_v4_repair_send_deferred_for_file_tuna_v4_post_tuna_recovery"
                        : string.Equals(repairSend.DeliveryEscalationReason, "regular_v4_peer_silence_safety_replay", StringComparison.Ordinal)
                            ? "filetransfer_v4_repair_send_deferred_for_regular_v4_peer_silence_repair"
                            : "filetransfer_v4_repair_send_deferred_for_v6_regular_nkn_sparse_runtime";
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event={deferredEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(repairSend.ProtocolRepairRequestId ?? "(none)")}; range_count={repairSend.RangeCount}; requested_chunk_count={repairSend.RequestedChunkCount}; scheduled_chunk_count={repairSend.ChunkIndices.Count}; repair_delivery_mode={FormatV4RepairDeliveryMode(repairSend.DeliveryMode)}; repair_delivery_escalation_reason={repairSend.DeliveryEscalationReason}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}");
                    if (requeuedRegularV4PeerSilenceRepair)
                    {
                        LocalOperationalLog.Warn(
                            "FileTransferService",
                            $"event=filetransfer_regular_v4_peer_silence_safety_replay_requeued_after_send_timeout; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; requeued_chunk_count={requeuedRegularV4PeerSilenceRepairChunkCount}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}");
                    }

                    continue;
                }

                var fileTunaV4PostTunaRepairProved = false;
                var fileTunaV4PostTunaRebindGeneration = 0;
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context) &&
                        !context.IsTerminal &&
                        ShouldPauseOutboundV4SenderPumpForFileTunaV4PostTunaRecoveryLocked(context))
                    {
                        context.PullTransportPaused = false;
                        context.PullTransportPausedSinceUtc = null;
                        context.PullTransportGraceDeadlineUtc = null;
                        context.PullTransportPauseReason = null;
                        context.PullTransportResumeRequestPending = false;
                        context.SparseSenderPumpLastWakeReason = "file_tuna_v4_post_tuna_repair_proved";
                        fileTunaV4PostTunaRepairProved = true;
                        fileTunaV4PostTunaRebindGeneration = context.PullTransportRebindGeneration;
                    }
                }

                if (fileTunaV4PostTunaRepairProved)
                {
                    LogTransportResumed(
                        FileTransferDirection.Outbound,
                        context.TransferId,
                        context.SessionId,
                        "file_tuna_v4_post_tuna_repair_proved",
                        requiresResumeRequest: false);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_file_tuna_v4_post_tuna_recovery_cleanup_completed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; proof=file_transfer_v4_repair_sent; rebind_generation={fileTunaV4PostTunaRebindGeneration}; repair_request_key={repairSend.RepairRequestKey}; sent_chunk_count={repairSend.ChunkIndices.Count}");
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairSend.RepairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(repairSend.ProtocolRepairRequestId ?? "(none)")}; range_count={repairSend.RangeCount}; requested_chunk_count={repairSend.RequestedChunkCount}; sent_chunk_count={repairSend.ChunkIndices.Count}; sent_chunk_indices={FormatV4ChunkIndicesForLog(repairSend.ChunkIndices)}; transport_sent_chunk_count={repairSend.ChunkIndices.Count * V4RepairBatchSendAttempts}; repair_batch_send_attempt_count={V4RepairBatchSendAttempts}; repair_delivery_mode={FormatV4RepairDeliveryMode(repairSend.DeliveryMode)}; repair_delivery_escalation_reason={repairSend.DeliveryEscalationReason}; first_start_chunk_index={repairSend.FirstStartChunkIndex}; last_end_chunk_exclusive={repairSend.LastEndChunkExclusive}; frontier_tail_repair={(repairSend.FrontierTailRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair={repairSend.CreditExhaustedTimeMsAtDecision}; remote_next_expected_chunk_index={repairSend.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={repairSend.ChunksAcceptedForTransport}; skipped_obsolete_count={repairSend.SkippedObsoleteCount}; skipped_future_count={repairSend.SkippedFutureCount}; skipped_out_of_bounds_count={repairSend.SkippedOutOfBoundsCount}; sent_unix_ms={sentUtc.ToUnixTimeMilliseconds()}; receiver_proof_status=pending");
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
                            var transportPaused = ShouldPauseOutboundV4SenderPumpForTransportPauseLocked(context);
                            var allowPausedRepair = transportPaused &&
                                ShouldAllowOutboundV4RepairWhileTransportPausedLocked(context);
                            if (!transportPaused || allowPausedRepair)
                            {
                                MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(context, "post_fallback_sender_wait");
                                MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(context, "regular_v4_sender_wait");
                            }

                            MaybeLogOutboundV4SenderPumpSummaryLocked(context, DateTimeOffset.UtcNow, force: false);
                        }
                    }
                }
            }
        }
    }

    private static bool ShouldCompleteOutboundV4FromTerminalReadyStateLocked(OutboundTransferContext context)
        => ShouldUseV6SparseCreditEnvelope(context) &&
           context.V4TerminalReady &&
           context.RemoteNextExpectedChunkIndex >= context.ChunkCount &&
           context.ChunksAcceptedForTransport >= context.ChunkCount &&
           Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred) >= context.FileSizeBytes &&
           context.PullSenderPipelineCurrentInFlightFrames <= 0;

    private void MaybeQueueOutboundV4StalledRebindSafetyReplayLocked(
        OutboundTransferContext context,
        string reason)
    {
        var recoveryGeneration = GetOutboundPostTunaRecoveryGenerationLocked(context);
        var allowFileTunaV4PostTunaReplay =
            context.RouteRuntime.UsesFileTunaV4Runtime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            recoveryGeneration <= 0 ||
            (!context.PullPostTunaRecoveryActive && recoveryGeneration <= context.LastRecoveredV6TransportHandoffEpoch) ||
            (!allowFileTunaV4PostTunaReplay &&
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6) ||
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

    private bool MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.RouteRuntime.FrameFamily != FileTransferFrameFamily.V4 ||
            !context.PullSourceCanSeek ||
            context.RemoteNextExpectedChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        var frontierLagChunks = Math.Max(0, acceptedUntil - remoteFrontier);
        if (acceptedUntil <= remoteFrontier ||
            frontierLagChunks < PullV4RegularNknControlFeedbackPressureMinFrontierLagChunks)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var lastPeerFrameUtc = context.PullV4LastPeerFrameReceivedUtc ?? context.PullV4LastGrantReceivedUtc;
        var minSilence = TimeSpan.FromMilliseconds(PullControlChatterWindowMs * 2L);
        var feedbackSilence = lastPeerFrameUtc is null ? TimeSpan.MaxValue : now - lastPeerFrameUtc.Value;
        if (feedbackSilence < minSilence)
        {
            return false;
        }

        if (context.PullRegularNknV4LastPeerSilenceSafetyReplayUtc is { } lastReplayUtc &&
            now - lastReplayUtc < PullTransportRebindSafetyReplayRearmCooldown)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_regular_v4_peer_silence_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; skip_reason=rearm_cooldown; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_for_transport={acceptedUntil}; frontier_lag_chunks={frontierLagChunks}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; rearm_cooldown_ms={(long)PullTransportRebindSafetyReplayRearmCooldown.TotalMilliseconds}");
            return false;
        }

        var replayStartChunkIndex = remoteFrontier;
        var wrappedToFrontier = false;
        if (context.PullRegularNknV4LastPeerSilenceSafetyReplayFrontierChunkIndex == remoteFrontier &&
            context.PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex > remoteFrontier)
        {
            if (context.PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex < acceptedUntil)
            {
                replayStartChunkIndex = context.PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex;
            }
            else
            {
                wrappedToFrontier = true;
            }
        }

        var maxChunksByBytes = Math.Max(1, PullTransportRebindSafetyReplayMaxBytes / Math.Max(1, context.ChunkSizeBytes));
        var replayChunkCount = Math.Min(
            acceptedUntil - replayStartChunkIndex,
            Math.Min(PullTransportRebindSafetyReplayMaxChunks, maxChunksByBytes));
        if (replayChunkCount <= 0)
        {
            return false;
        }

        var replayEndExclusive = replayStartChunkIndex + replayChunkCount;
        var chunkIndices = new List<int>(replayChunkCount);
        for (var chunkIndex = replayStartChunkIndex; chunkIndex < replayEndExclusive; chunkIndex++)
        {
            if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
            {
                chunkIndices.Add(chunkIndex);
            }
        }

        context.PullRegularNknV4LastPeerSilenceSafetyReplayUtc = now;
        context.PullRegularNknV4LastPeerSilenceSafetyReplayFrontierChunkIndex = remoteFrontier;
        context.PullRegularNknV4LastPeerSilenceSafetyReplayEndChunkIndex = replayEndExclusive;
        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_regular_v4_peer_silence_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; skip_reason=chunks_already_queued; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; replay_end_chunk_exclusive={replayEndExclusive}; frontier_lag_chunks={frontierLagChunks}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}");
            return false;
        }

        var replayOrdinal = ++context.PullRegularNknV4PeerSilenceSafetyReplayCount;
        var repairKey = $"regular_v4_peer_silence_safety_replay:{remoteFrontier}:{replayStartChunkIndex}:{chunkIndices.Count}:{replayOrdinal}";
        context.PullV4SenderPumpRepairQueue.Enqueue(
            new PullV4QueuedRepairSend(
                chunkIndices,
                RangeCount: 1,
                RequestedChunkCount: replayChunkCount,
                FirstStartChunkIndex: replayStartChunkIndex,
                LastEndChunkExclusive: replayEndExclusive,
                RemoteNextExpectedChunkIndex: remoteFrontier,
                ChunksAcceptedForTransport: acceptedUntil,
                SkippedObsoleteCount: 0,
                SkippedFutureCount: replayChunkCount - chunkIndices.Count,
                SkippedOutOfBoundsCount: 0,
                RepairRequestKey: repairKey,
                ProtocolRepairRequestId: null,
                ProtocolPriority: null,
                ProtocolRecoveryMode: null,
                FrontierTailRepair: replayStartChunkIndex == remoteFrontier,
                EmergencyCreditRepair: false,
                DeliveryMode: FileTransferV4RepairDeliveryMode.ControlBulkRedundant,
                DeliveryEscalationReason: "regular_v4_peer_silence_safety_replay",
                CreditExhaustedTimeMsAtDecision: -1));

        context.SparseSenderPumpLastWakeReason = "regular_v4_peer_silence_safety_replay";
        context.SignalSparseSenderPump();

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_regular_v4_peer_silence_safety_replay_started; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; replay_ordinal={replayOrdinal}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; replay_end_chunk_exclusive={replayEndExclusive}; requested_chunk_count={replayChunkCount}; scheduled_chunk_count={chunkIndices.Count}; frontier_lag_chunks={frontierLagChunks}; feedback_silence_ms={(long)Math.Max(0, feedbackSilence.TotalMilliseconds)}; replay_byte_cap={PullTransportRebindSafetyReplayMaxBytes}; replay_chunk_cap={PullTransportRebindSafetyReplayMaxChunks}; wrapped_to_frontier={(wrappedToFrontier ? 1 : 0)}");
        return true;
    }

    private static bool TryRequeueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(
        OutboundTransferContext context,
        PullV4QueuedRepairSend repairSend,
        out int requeuedChunkCount)
    {
        requeuedChunkCount = 0;
        if (context.IsTerminal ||
            context.UserPaused ||
            context.PeerPaused ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.RouteRuntime.FrameFamily != FileTransferFrameFamily.V4 ||
            !string.Equals(repairSend.DeliveryEscalationReason, "regular_v4_peer_silence_safety_replay", StringComparison.Ordinal))
        {
            return false;
        }

        var revalidatedRepair = RevalidateQueuedV4RepairSendLocked(context, repairSend);
        if (revalidatedRepair is null)
        {
            return false;
        }

        var requeued = new List<int>(repairSend.ChunkIndices.Count);
        foreach (var chunkIndex in revalidatedRepair.ChunkIndices)
        {
            if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
            {
                requeued.Add(chunkIndex);
            }
        }

        if (requeued.Count == 0)
        {
            return false;
        }

        var firstStartChunkIndex = requeued.Min();
        var lastEndChunkExclusive = requeued.Max() + 1;
        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        var replayOrdinal = ++context.PullRegularNknV4PeerSilenceSafetyReplayCount;
        var repairKey = $"regular_v4_peer_silence_safety_replay:{remoteFrontier}:{firstStartChunkIndex}:{requeued.Count}:{replayOrdinal}";
        context.PullV4SenderPumpRepairQueue.Enqueue(
            revalidatedRepair with
            {
                ChunkIndices = requeued,
                RequestedChunkCount = requeued.Count,
                FirstStartChunkIndex = firstStartChunkIndex,
                LastEndChunkExclusive = lastEndChunkExclusive,
                RemoteNextExpectedChunkIndex = remoteFrontier,
                ChunksAcceptedForTransport = acceptedUntil,
                RepairRequestKey = repairKey,
                FrontierTailRepair = firstStartChunkIndex == remoteFrontier,
            });
        context.SparseSenderPumpLastWakeReason = "regular_v4_peer_silence_safety_replay_retry";
        context.SignalSparseSenderPump();
        requeuedChunkCount = requeued.Count;
        return true;
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

            var currentPostTunaFallbackReceiverState = false;
            if (state is FileTransferReceiverStateFrameV6 receiverState)
            {
                if (!TryAcceptOutboundFallbackCheckpointLocked(context, receiverState, "receiver_state_sparse_runtime"))
                {
                    return;
                }

                currentPostTunaFallbackReceiverState =
                    context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
                    IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg);
                if (currentPostTunaFallbackReceiverState &&
                    state.Epoch < context.V4LastStateEpoch)
                {
                    var previousEpochFloor = context.V4LastStateEpoch;
                    context.V4LastStateEpoch = Math.Max(-1, state.Epoch - 1);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_fallback_state_epoch_floor_reset; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; previous_epoch={previousEpochFloor}; state_epoch={state.Epoch}; new_previous_epoch={context.V4LastStateEpoch}; transport_epoch={receiverState.TransportEpoch}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; reason=current_fallback_checkpoint");
                }
            }

            var previousEpoch = context.V4LastStateEpoch;
            var previousRemoteNext = context.RemoteNextExpectedChunkIndex;
            var previousGrant = context.RemoteGrantedUntilExclusive;
            var receiverProgressChanged = UpdateOutboundReceiverAcknowledgedProgressFromV4StateLocked(context, state);
            context.PullV4StateReceivedCountTotal++;
            if (state.Epoch < previousEpoch)
            {
                context.PullV4StateStaleCountTotal++;
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

                if (receiverProgressChanged)
                {
                    snapshot = CreateSnapshotLocked();
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
                    context.PullV4StateDuplicateCountTotal++;
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
                    }

                    if (receiverProgressChanged)
                    {
                        snapshot = CreateSnapshotLocked();
                    }
                }
                else
                {
                    context.PullV4StateAppliedCountTotal++;
                    var normalizedPauseReason = NormalizeReason(state.TransferPauseReason);
                    var peerPauseChanged = state.TransferPaused != context.PeerPaused ||
                        !string.Equals(normalizedPauseReason, context.PeerPauseReason, StringComparison.Ordinal);

                    context.V4LastStateEpoch = Math.Max(context.V4LastStateEpoch, state.Epoch);
                    context.RemoteNextExpectedChunkIndex = Math.Max(context.RemoteNextExpectedChunkIndex, normalizedCommitted);
                    context.RemoteGrantedUntilExclusive = normalizedCredit;
                    context.ChunksTransferred = Math.Max(context.ChunksTransferred, context.RemoteNextExpectedChunkIndex);
                    context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(context.FileSizeBytes, state.BytesCommitted));
                    context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred);
                    context.V4TerminalReady |= state.TerminalReady;
                    UpdateOutboundV6TransportHandoffFromStateLocked(context, state);
                    var suppressRepairsForAcceptedTail =
                        TryCompleteOutboundV6TransportEpochWhenPeerCaughtUpToAcceptedTailLocked(context, state);
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

                    context.SparseSenderPumpLastWakeReason = peerPauseChanged
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

                    MaybeObserveRegularV4ControlFeedbackPressureFromStateLocked(context, state);
                    LogOutboundV4StateReceivedLocked(
                        context,
                        state,
                        previousEpoch,
                        previousRemoteNext,
                        previousGrant,
                        applied: true,
                        stale: false,
                        duplicate: false);
                    snapshot = CreateSnapshotLocked();
                    shouldEnqueueRepairs = !suppressRepairsForAcceptedTail;
                }
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

    private void MaybeObserveRegularV4ControlFeedbackPressureFromStateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        if (state.MissingRanges.Count <= 0 &&
            context.PullV4SenderPumpRepairRequests.Count <= 0)
        {
            return;
        }

        var pendingRepairCount = 0;
        foreach (var range in state.MissingRanges)
        {
            pendingRepairCount += Math.Max(0, range.ChunkCount);
        }

        var availableCreditChunks = Math.Max(
            0,
            Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport);
        var creditExhaustedTimeMs = context.V4SenderCreditExhaustedSinceUtc is null || availableCreditChunks > 0 || context.V4TerminalReady
            ? 0
            : (long)Math.Max(0, (DateTimeOffset.UtcNow - context.V4SenderCreditExhaustedSinceUtc.Value).TotalMilliseconds);
        MaybeObserveRegularV4ControlFeedbackPressure(
            context,
            creditExhaustedTimeMs,
            availableCreditChunks,
            pendingRepairCount);
    }

    private static bool UpdateOutboundReceiverAcknowledgedProgressFromV4StateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        var receiverBytes = Math.Clamp(state.BytesCommitted, 0L, Math.Max(0L, context.FileSizeBytes));
        var previousVisibleBytes = Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred);
        if (receiverBytes <= previousVisibleBytes)
        {
            return false;
        }

        context.BytesAcknowledgedByReceiver = receiverBytes;
        context.BytesTransferred = Math.Max(context.BytesTransferred, receiverBytes);
        return true;
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

        if (context.PullTransportFrontierOnlyRepairActive)
        {
            var activeStart = context.PullTransportFrontierOnlyRepairStartChunkIndex;
            if (activeStart >= 0 && frontierChunkIndex >= activeStart)
            {
                return;
            }
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
        if (advancedChunks < PullTransportRebindFrontierOnlyStableAdvanceChunks)
        {
            return;
        }

        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_frontier_only_recovered; transfer_id={context.TransferId}; session_id={context.SessionId}; rebind_generation={recoveryGeneration}; active_start_chunk_index={activeStart}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; advanced_chunks={advancedChunks}; missing_range_count={state.MissingRanges.Count}");
    }

    private static void UpdateOutboundV6TransportHandoffFromStateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        if (context.V6TransportHandoff is null)
        {
            return;
        }

        var handoff = context.V6TransportHandoff;
        if (handoff.EpochId <= context.LastRecoveredV6TransportHandoffEpoch &&
            !IsV6TransportHandoffBlockingTail(handoff))
        {
            return;
        }

        if (handoff.State == V6TransportHandoffState.Recovered)
        {
            CompleteOutboundV6TransportHandoffLocked(
                context,
                "recovered_state_observed",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
            return;
        }

        var v6State = state as FileTransferReceiverStateFrameV6;
        if (v6State is not null &&
            v6State.TransportEpoch > 0 &&
            v6State.TransportEpoch != handoff.EpochId)
        {
            if (!TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
                    context,
                    v6State.TransportEpoch,
                    FileTransferProtocol.ReceiverStateFrameTypeV6,
                    "peer_state_epoch_conflict",
                    Math.Clamp(v6State.ContiguousCommittedChunkIndex, 0, context.ChunkCount),
                    v6State.DurableReceivedHighestChunkIndex,
                    requireFrontierEvidence: v6State.MissingRanges.Count > 0))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.ReceiverStateFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={v6State.TransportEpoch}; current_transport_epoch={handoff.EpochId}");
                return;
            }

            handoff = context.V6TransportHandoff!;
        }

        handoff.LastProofUtc = DateTimeOffset.UtcNow;
        if (TryCompleteOutboundV6HandoffWhenPeerCaughtUpToAcceptedTailLocked(context, state, handoff))
        {
            return;
        }

        var frontierMissing = state.MissingRanges.Any(range =>
            range.StartChunkIndex <= context.RemoteNextExpectedChunkIndex &&
            context.RemoteNextExpectedChunkIndex < range.StartChunkIndex + range.ChunkCount);
        if (context.RemoteNextExpectedChunkIndex <= handoff.StartingCommittedChunkIndex || frontierMissing)
        {
            TrySetV6TransportHandoffState(
                handoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V6TransportHandoffState.FrontierRepairOnly,
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
            TrySetV6TransportHandoffState(
                handoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V6TransportHandoffState.BackfillRepair,
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
            CompleteOutboundV6TransportHandoffLocked(
                context,
                "durable_frontier_progress",
                context.RemoteNextExpectedChunkIndex,
                state.DurableReceivedHighestChunkIndex);
        }
    }

    private static bool TryCompleteOutboundV6HandoffWhenPeerCaughtUpToAcceptedTailLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        TransportHandoffEpoch handoff)
    {
        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        if (remoteFrontier < acceptedUntil ||
            state.DurableReceivedHighestChunkIndex >= acceptedUntil)
        {
            return false;
        }

        foreach (var range in state.MissingRanges)
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, context.ChunkCount);
            var end = Math.Clamp(range.StartChunkIndex + range.ChunkCount, start, context.ChunkCount);
            if (start < acceptedUntil && end > start)
            {
                return false;
            }
        }

        CompleteOutboundV6TransportHandoffLocked(
            context,
            "frontier_caught_up_to_accepted_tail",
            remoteFrontier,
            state.DurableReceivedHighestChunkIndex);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_tail_caught_up_to_accepted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={handoff.EpochId}; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_for_transport={acceptedUntil}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}");
        return true;
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

    private static IReadOnlyList<FileTransferRangeV4> ClampV6HandoffFrontierRepairRangesForSend(
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

        return IsV6TransportHandoffBlockingTail(context.V6TransportHandoff) ||
               context.PullTransportFrontierOnlyRepairActive;
    }

    private static bool ShouldClampOutboundV6HandoffFrontierRepairRangeForSendLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        int frontierChunkIndex)
    {
        if (IsOutboundV6BackfillRepairRequestLocked(context, state))
        {
            return false;
        }

        if (ShouldPreserveOutboundPostFallbackSparseFrontierBackfillRangeLocked(context, state, frontierChunkIndex))
        {
            return false;
        }

        return IsOutboundPostFallbackExactFrontierRepairRequiredLocked(context, frontierChunkIndex);
    }

    private static bool ShouldPreserveOutboundPostFallbackSparseFrontierBackfillRangeLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        int frontierChunkIndex)
    {
        if (!IsOutboundPostTunaRecoveryActiveLocked(context) ||
            frontierChunkIndex < 0 ||
            frontierChunkIndex >= context.ChunkCount ||
            state.DurableReceivedHighestChunkIndex <= frontierChunkIndex)
        {
            return false;
        }

        foreach (var range in state.MissingRanges)
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, context.ChunkCount);
            var end = Math.Clamp(range.StartChunkIndex + range.ChunkCount, start, context.ChunkCount);
            if (start <= frontierChunkIndex &&
                frontierChunkIndex < end &&
                end - frontierChunkIndex > V4PostFallbackEmergencyFrontierRepairChunks)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOutboundV6BackfillRepairRequestLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
        => state is FileTransferReceiverStateFrameV6 v6State &&
           string.Equals(v6State.Priority, "backfill", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(v6State.RecoveryMode, FormatV6TransportHandoffState(V6TransportHandoffState.BackfillRepair), StringComparison.OrdinalIgnoreCase) &&
           context.V6TransportHandoff?.State == V6TransportHandoffState.BackfillRepair;

    private static bool IsOutboundPeerV6TransportEpochUsableLocked(
        OutboundTransferContext context,
        long transportEpoch)
    {
        if (transportEpoch <= 0)
        {
            return false;
        }

        if (context.V6TransportHandoff is { } handoff &&
            handoff.EpochId == transportEpoch &&
            handoff.State != V6TransportHandoffState.Recovered)
        {
            return true;
        }

        return transportEpoch > context.LastRecoveredV6TransportHandoffEpoch;
    }

    private static bool TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
        OutboundTransferContext context,
        long peerTransportEpoch,
        string frameType,
        string reason,
        int peerCommittedChunkIndex,
        int peerHighestObservedChunkIndex,
        bool requireFrontierEvidence)
    {
        if (peerTransportEpoch <= 0 ||
            context.V6TransportHandoff is not { } current ||
            current.EpochId == peerTransportEpoch ||
            !IsV6TransportHandoffBlockingTail(current))
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
        context.V6TransportHandoff = new TransportHandoffEpoch
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
            State = V6TransportHandoffState.FrontierRepairOnly,
            LastObservedCommittedChunkIndex = clampedPeerCommitted,
            LastObservedHighestChunkIndex = peerHighest,
            LastRepairRequestId = current.LastRepairRequestId,
            LastStateChangeLogUtc = DateTimeOffset.UtcNow,
        };
        context.PullTransportRebindGeneration = Math.Max(
            context.PullTransportRebindGeneration,
            (int)Math.Min(int.MaxValue, peerTransportEpoch));
        context.SparseSenderPumpLastWakeReason = "v6_handoff_epoch_reconciled";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_conflict; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; action=adopt_peer_epoch; reason={FormatProtocolLogValue(reason)}; previous_transport_epoch={previousEpoch}; peer_transport_epoch={peerTransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV6TransportHandoffEpoch}; previous_state={FormatV6TransportHandoffState(previousState)}; state={FormatV6TransportHandoffState(context.V6TransportHandoff.State)}; committed_chunk={clampedPeerCommitted}; highest_observed_chunk={peerHighest}; target_transport={FormatFileTransferTransportKind(context.V6TransportHandoff.TargetTransport)}");
        return true;
    }

    private static bool TryReopenOutboundRecoveredV6TransportHandoffEpochLocked(
        OutboundTransferContext context,
        long peerTransportEpoch,
        string frameType,
        string reason,
        int peerCommittedChunkIndex,
        int peerHighestObservedChunkIndex,
        bool requireFrontierEvidence)
    {
        if (peerTransportEpoch <= 0 ||
            peerTransportEpoch > context.LastRecoveredV6TransportHandoffEpoch ||
            context.V6TransportHandoff is not null)
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
        context.V6TransportHandoff = new TransportHandoffEpoch
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
            State = V6TransportHandoffState.FrontierRepairOnly,
            LastObservedCommittedChunkIndex = clampedPeerCommitted,
            LastObservedHighestChunkIndex = peerHighest,
            LastStateChangeLogUtc = now,
        };
        context.PullTransportRebindGeneration = Math.Max(
            context.PullTransportRebindGeneration,
            (int)Math.Min(int.MaxValue, peerTransportEpoch));
        context.SparseSenderPumpLastWakeReason = "v6_handoff_reopened_recovered_epoch";
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_reopened; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={frameType}; reason={FormatProtocolLogValue(reason)}; peer_transport_epoch={peerTransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV6TransportHandoffEpoch}; state={FormatV6TransportHandoffState(context.V6TransportHandoff.State)}; committed_chunk={clampedPeerCommitted}; highest_observed_chunk={peerHighest}; current_frontier_chunk={currentFrontier}; target_transport={FormatFileTransferTransportKind(context.V6TransportHandoff.TargetTransport)}");
        return true;
    }

    private void ApplyOutboundV6HandoffFrame(OutboundTransferContext context, FileTransferTransportEpochFrameV6 handoff)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                handoff.TransportEpoch <= 0)
            {
                return;
            }

            if (!IsOutboundPeerV6TransportEpochUsableLocked(context, handoff.TransportEpoch) &&
                !TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
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
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={handoff.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV6TransportHandoffEpoch}");
                return;
            }

            if (context.V6TransportHandoff is null)
            {
                context.V6TransportHandoff = new TransportHandoffEpoch
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
                    State = V6TransportHandoffState.TransportProofPending,
                };
                LogV6TransportHandoffEpochStarted(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.V6TransportHandoff);
            }

            if (context.V6TransportHandoff.EpochId != handoff.TransportEpoch &&
                !TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
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
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={handoff.TransportEpoch}; current_transport_epoch={context.V6TransportHandoff.EpochId}");
                return;
            }

            if (context.V6TransportHandoff.State == V6TransportHandoffState.Recovered)
            {
                return;
            }

            TrySetV6TransportHandoffState(
                context.V6TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                V6TransportHandoffState.FrontierRepairOnly,
                "peer_handoff",
                context.RemoteNextExpectedChunkIndex,
                Math.Max(-1, context.ChunksAcceptedForTransport - 1));
        }
    }

    private void ApplyOutboundV6RepairRequest(OutboundTransferContext context, FileTransferFrontierRequestFrameV6 repairRequest)
    {
        if (repairRequest.MissingRanges.Count == 0)
        {
            return;
        }

        var first = repairRequest.MissingRanges[0];
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return;
            }

            var allowEpochlessPostTunaFallbackFrontier =
                IsEpochlessOutboundPostTunaFallbackV6FrontierRequestLocked(context, repairRequest, first);
            if (repairRequest.TransportEpoch <= 0 && !allowEpochlessPostTunaFallbackFrontier)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; reason=missing_transport_epoch; frame_transport_epoch={repairRequest.TransportEpoch}; route={context.RouteSelection.TelemetryToken}; recovery_mode={FormatProtocolLogValue(repairRequest.RecoveryMode ?? "(none)")}; first_start_chunk_index={first.StartChunkIndex}; requested_chunk_count={first.ChunkCount}");
                return;
            }

            var isBackfillRequest = string.Equals(repairRequest.Priority, "backfill", StringComparison.OrdinalIgnoreCase);
            if (allowEpochlessPostTunaFallbackFrontier)
            {
                ActivateOutboundV4PostRebindFrontierOnlyRepairLocked(
                    context,
                    Math.Clamp(first.StartChunkIndex, 0, context.ChunkCount),
                    "epochless_post_tuna_frontier_request");
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_epochless_post_tuna_fallback_frontier_request_accepted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; repair_request_id={FormatProtocolLogValue(repairRequest.RepairRequestId ?? "(none)")}; recovery_mode={FormatProtocolLogValue(repairRequest.RecoveryMode ?? "(none)")}; first_start_chunk_index={first.StartChunkIndex}; requested_chunk_count={first.ChunkCount}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; rebind_generation={GetOutboundPostTunaRecoveryGenerationLocked(context)}");
            }
            else
            {
                var suppressLegacyHandoff = ShouldSuppressLegacyV6HandoffForDedicatedV6EpochLocked(
                    context,
                    repairRequest.TransportEpoch);
                if (!suppressLegacyHandoff &&
                    !IsOutboundPeerV6TransportEpochUsableLocked(context, repairRequest.TransportEpoch) &&
                    !TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
                        context,
                        repairRequest.TransportEpoch,
                        FileTransferProtocol.FrontierRequestFrameTypeV6,
                        "peer_repair_request_epoch_conflict",
                        first.StartChunkIndex,
                        first.StartChunkIndex + first.ChunkCount - 1,
                        requireFrontierEvidence: true) &&
                    !TryReopenOutboundRecoveredV6TransportHandoffEpochLocked(
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
                        $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={repairRequest.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV6TransportHandoffEpoch}");
                    return;
                }

                if (suppressLegacyHandoff)
                {
                    LogLegacyV6HandoffSuppressedForDedicatedV6Epoch(
                        context,
                        repairRequest.TransportEpoch,
                        FileTransferProtocol.FrontierRequestFrameTypeV6,
                        "dedicated_v6_transport_epoch");
                }
                else if (context.V6TransportHandoff is null)
                {
                    context.PullTransportRebindGeneration = Math.Max(context.PullTransportRebindGeneration, (int)Math.Min(int.MaxValue, repairRequest.TransportEpoch));
                    context.V6TransportHandoff = new TransportHandoffEpoch
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
                        State = V6TransportHandoffState.TransportProofPending,
                    };
                    LogV6TransportHandoffEpochStarted(
                        FileTransferDirection.Outbound,
                        context.TransferId,
                        context.SessionId,
                        context.V6TransportHandoff);
                }

                if (!suppressLegacyHandoff &&
                    context.V6TransportHandoff is { } legacyHandoff &&
                    legacyHandoff.EpochId != repairRequest.TransportEpoch)
                {
                    if (!TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
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
                            $"event=filetransfer_v6_recovery_frame_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={repairRequest.TransportEpoch}; current_transport_epoch={context.V6TransportHandoff.EpochId}");
                        return;
                    }
                }

                if (!suppressLegacyHandoff)
                {
                    MarkV6TransportHandoffPeerActivity(context.V6TransportHandoff);
                    var requestedState =
                        isBackfillRequest ||
                        string.Equals(repairRequest.RecoveryMode, FormatV6TransportHandoffState(V6TransportHandoffState.BackfillRepair), StringComparison.OrdinalIgnoreCase)
                            ? V6TransportHandoffState.BackfillRepair
                            : V6TransportHandoffState.FrontierRepairOnly;
                    TrySetV6TransportHandoffState(
                        context.V6TransportHandoff,
                        FileTransferDirection.Outbound,
                        context.TransferId,
                        context.SessionId,
                        requestedState,
                        "repair_request",
                        context.RemoteNextExpectedChunkIndex,
                        Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                }
            }

            if (!isBackfillRequest)
            {
                AdvanceOutboundV4SparseRemoteFrontierFromV6FrontierRequestLocked(
                    context,
                    repairRequest,
                    first);
            }
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

    private static bool IsEpochlessOutboundPostTunaFallbackV6FrontierRequestLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferRangeV4 firstRange)
    {
        if (request.TransportEpoch > 0 ||
            !IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context) ||
            firstRange.StartChunkIndex < 0 ||
            firstRange.StartChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        if (string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RecoveryMode))
        {
            return true;
        }

        return IsV6RegularNknFrontierStallMode(request.RecoveryMode) ||
               string.Equals(
                   request.RecoveryMode,
                   FormatV6TransportHandoffState(V6TransportHandoffState.FrontierRepairOnly),
                   StringComparison.OrdinalIgnoreCase);
    }

    private void AdvanceOutboundV4SparseRemoteFrontierFromV6FrontierRequestLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferRangeV4 firstRange)
    {
        var inferredRemoteFrontier = Math.Clamp(firstRange.StartChunkIndex, 0, context.ChunkCount);
        if (inferredRemoteFrontier <= context.RemoteNextExpectedChunkIndex)
        {
            return;
        }

        var previousRemoteFrontier = context.RemoteNextExpectedChunkIndex;
        context.RemoteNextExpectedChunkIndex = inferredRemoteFrontier;
        context.RemoteGrantedUntilExclusive = Math.Max(context.RemoteGrantedUntilExclusive, context.RemoteNextExpectedChunkIndex);
        context.ChunksTransferred = Math.Max(context.ChunksTransferred, context.RemoteNextExpectedChunkIndex);
        context.BytesTransferred = Math.Max(
            context.BytesTransferred,
            context.RemoteNextExpectedChunkIndex >= context.ChunkCount
                ? context.FileSizeBytes
                : Math.Min(context.FileSizeBytes, (long)context.RemoteNextExpectedChunkIndex * Math.Max(1, context.ChunkSizeBytes)));
        context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, context.BytesTransferred);

        foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex).ToArray())
        {
            context.SentAwaitingAck.Remove(chunkIndex);
        }

        TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
        ClearObsoleteOutboundV4RepairWorkAfterFrontierAdvanceLocked(
            context,
            previousRemoteFrontier,
            context.RemoteNextExpectedChunkIndex);
        MaybeReleaseOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
            context,
            previousRemoteFrontier,
            "frontier_request_progress");

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_advanced_remote_frontier; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={request.TransportEpoch}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; inferred_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
    }

    private void ApplyOutboundV6RepairProof(OutboundTransferContext context, FileTransferRepairProofFrameV6 repairProof)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                repairProof.TransportEpoch <= 0)
            {
                return;
            }

            if (!IsOutboundPeerV6TransportEpochUsableLocked(context, repairProof.TransportEpoch) &&
                !TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
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

            if (context.V6TransportHandoff is null)
            {
                return;
            }

            if (context.V6TransportHandoff.EpochId != repairProof.TransportEpoch &&
                !TryAdoptOutboundPeerV6TransportHandoffEpochLocked(
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

            TrySetV6TransportHandoffState(
                context.V6TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                repairProof.CommittedChunkIndex > context.V6TransportHandoff.StartingCommittedChunkIndex
                    ? V6TransportHandoffState.BackfillRepair
                    : V6TransportHandoffState.FrontierRepairOnly,
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
        var metadataState = state as FileTransferReceiverStateFrameV6;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_state_received; transfer_id={context.TransferId}; session_id={context.SessionId}; epoch={state.Epoch}; previous_epoch={previousEpoch}; applied={(applied ? 1 : 0)}; stale={(stale ? 1 : 0)}; duplicate={(duplicate ? 1 : 0)}; repair_request_id={FormatProtocolLogValue(metadataState?.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(metadataState?.Priority ?? "(none)")}; recovery_mode={FormatProtocolLogValue(metadataState?.RecoveryMode ?? "(none)")}; previous_contiguous_committed_chunk_index={previousRemoteNext}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; previous_credit_until_chunk_index_exclusive={previousGrant}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; effective_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; available_credit_chunks={availableCreditChunks}; available_credit_bytes={availableCreditChunks * (long)Math.Max(1, context.ChunkSizeBytes)}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; receiver_memory_pressure={(state.ReceiverMemoryPressure ? 1 : 0)}; receiver_disk_pressure={(state.ReceiverDiskPressure ? 1 : 0)}; terminal_ready={(state.TerminalReady ? 1 : 0)}");
        if (IsPrimaryRegularNknFrontierRepairTransactionId(metadataState?.RepairRequestId))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_received; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(metadataState?.RepairRequestId)}; epoch={state.Epoch}; applied={(applied ? 1 : 0)}; stale={(stale ? 1 : 0)}; duplicate={(duplicate ? 1 : 0)}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; requested_missing_range_start={(state.MissingRanges.Count == 0 ? -1 : state.MissingRanges[0].StartChunkIndex)}; requested_missing_range_count={(state.MissingRanges.Count == 0 ? 0 : state.MissingRanges[0].ChunkCount)}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}");
        }
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

            context.SparseSenderPumpLastWakeReason = pauseControl.Paused
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

        var fileTunaV4PostTunaRecovery = IsOutboundFileTunaV4PostTunaRecoveryActiveLocked(context);
        var maxRepairChunks = fileTunaV4PostTunaRecovery
            ? V4PostFallbackEmergencyFrontierRepairChunks
            : ResolveV4RepairRequestMaxChunksForSend(context, state);
        var normalizedInputRanges = NormalizeV4MissingRangesForSend(state.MissingRanges, context.ChunkCount, maxRepairChunks);
        var postRebindFrontierRepair = IsOutboundPostTunaRecoveryActiveLocked(context) &&
            normalizedInputRanges.Any(range =>
                range.StartChunkIndex <= state.ContiguousCommittedChunkIndex &&
                state.ContiguousCommittedChunkIndex < range.StartChunkIndex + range.ChunkCount);
        var normalizedRanges = SelectV4RepairRangesForSend(
            normalizedInputRanges,
            state.ContiguousCommittedChunkIndex,
            maxRepairChunks,
            frontierExclusive: postRebindFrontierRepair);
        if (fileTunaV4PostTunaRecovery &&
            postRebindFrontierRepair &&
            normalizedRanges.Count > 0)
        {
            normalizedRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = state.ContiguousCommittedChunkIndex,
                    ChunkCount = V4PostFallbackEmergencyFrontierRepairChunks,
                },
            ];
        }
        else if (postRebindFrontierRepair &&
            ShouldClampOutboundV6HandoffFrontierRepairRangeForSendLocked(context, state, state.ContiguousCommittedChunkIndex))
        {
            normalizedRanges = ClampV6HandoffFrontierRepairRangesForSend(
                normalizedRanges,
                state.ContiguousCommittedChunkIndex,
                context.ChunkCount);
        }

        if (normalizedRanges.Count == 0)
        {
            return;
        }

        var requestedChunkIndices = new List<int>(maxRepairChunks);
        foreach (var range in normalizedRanges)
        {
            for (var chunkIndex = range.StartChunkIndex;
                 chunkIndex < range.StartChunkIndex + range.ChunkCount &&
                 requestedChunkIndices.Count < maxRepairChunks;
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
        var v6BackfillEmergencyCreditRepair = IsOutboundV6BackfillRepairRequestLocked(context, state) &&
            postRebindFrontierRepair &&
            firstStart == state.ContiguousCommittedChunkIndex &&
            lastEndExclusive > firstStart + V4PostFallbackEmergencyFrontierRepairChunks;
        var allowEmergencyCreditRepair = emergencyCreditRepair || v6BackfillEmergencyCreditRepair;
        var emergencyCreditEndExclusive = v6BackfillEmergencyCreditRepair
            ? Math.Min(context.ChunkCount, lastEndExclusive)
            : firstStart + V4PostFallbackEmergencyFrontierRepairChunks;
        var chunkIndices = FilterRepairChunkIndicesForSend(
            context,
            requestedChunkIndices,
            allowEmergencyCreditRepair,
            emergencyCreditEndExclusive,
            out var stats);
        var protocolRepairRequestId = state is FileTransferReceiverStateFrameV6 v6StateForRepair ? v6StateForRepair.RepairRequestId : null;
        var protocolPriority = state is FileTransferReceiverStateFrameV6 v6StateForPriority ? v6StateForPriority.Priority : null;
        var protocolRecoveryMode = state is FileTransferReceiverStateFrameV6 v6StateForRecoveryMode ? v6StateForRecoveryMode.RecoveryMode : null;
        var rangeRepairRequestKey = CreateV4RepairRequestKey(
            context.TransferId,
            firstStart,
            requestedChunkCount,
            state.ContiguousCommittedChunkIndex,
            state.DurableReceivedHighestChunkIndex,
            normalizedRanges);
        var repairRequestKey = ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context) &&
                               IsPrimaryRegularNknFrontierRepairTransactionId(protocolRepairRequestId)
            ? protocolRepairRequestId!
            : rangeRepairRequestKey;
        var frontierRepair = firstStart == state.ContiguousCommittedChunkIndex;
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
                $"event=filetransfer_v4_repair_scheduled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId ?? "(none)")}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count=0; scheduled_chunk_indices=(none); first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair=-1; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_repair_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId ?? "(none)")}; range_count={queuedRepair.RangeCount}; requested_chunk_count={queuedRepair.RequestedChunkCount}; sent_chunk_count=0; sent_chunk_indices=(none); repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=no_chunks; first_start_chunk_index={queuedRepair.FirstStartChunkIndex}; last_end_chunk_exclusive={queuedRepair.LastEndChunkExclusive}; frontier_tail_repair={(queuedRepair.FrontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair=-1; remote_next_expected_chunk_index={queuedRepair.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={queuedRepair.ChunksAcceptedForTransport}; skipped_obsolete_count={queuedRepair.SkippedObsoleteCount}; skipped_future_count={queuedRepair.SkippedFutureCount}; skipped_out_of_bounds_count={queuedRepair.SkippedOutOfBoundsCount}; receiver_proof_status=not_applicable");
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (state is FileTransferReceiverStateFrameV6 v6StateForEpochRepair &&
                v6StateForEpochRepair.TransportEpoch > 0)
            {
                MarkOutboundV6EpochRepairRequestPendingLocked(
                    context,
                    v6StateForEpochRepair.TransportEpoch,
                    protocolRepairRequestId ?? repairRequestKey,
                    "v4_sparse_receiver_state");
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
                        $"event=filetransfer_v6_exact_frontier_repair_enqueued; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V6TransportHandoff?.EpochId ?? context.PullTransportRebindGeneration}; state={FormatV6TransportHandoffState(context.V6TransportHandoff?.State ?? V6TransportHandoffState.FrontierRepairOnly)}; frontier_chunk_index={state.ContiguousCommittedChunkIndex}; repair_request_key={repairRequestKey}; source_state_missing_range_count={state.MissingRanges.Count}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}");
                }
            }
            CleanupOutboundV4RepairRequestStateLocked(context, DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            var senderRepairRepeatIntervalMs = ResolveV4SenderRepairRepeatIntervalMs(context, frontierRepair);
            if (!TryMarkOutboundV4RepairQueuedLocked(context, repairRequestKey, now, senderRepairRepeatIntervalMs, out var repairState, out var suppressionReason, out var lastSentAgeMs))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_repair_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId ?? "(none)")}; reason={suppressionReason}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count={chunkIndices.Count}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; last_sent_age_ms={lastSentAgeMs}; repair_interval_ms={senderRepairRepeatIntervalMs}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}");
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
                    $"event=filetransfer_v4_repair_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId ?? "(none)")}; reason=chunks_already_queued; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count=0; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; last_sent_age_ms=-1; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}");
                return;
            }

            var availableCreditChunks = Math.Max(
                0,
                Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport);
            MaybeObserveRegularV4ControlFeedbackPressure(
                context,
                deliveryDecision.CreditExhaustedTimeMs,
                availableCreditChunks,
                deduped.Count);

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
                    var eventName = v6BackfillEmergencyCreditRepair
                        ? "filetransfer_v6_backfill_repair_credit_granted"
                        : "filetransfer_v4_emergency_frontier_credit_granted";
                    var reason = v6BackfillEmergencyCreditRepair
                        ? "post_fallback_backfill_repair"
                        : "post_fallback_missing_frontier";
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; rebind_generation={context.PullTransportRebindGeneration}; previous_chunks_accepted_for_transport={previousAccepted}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; emergency_start_chunk_index={firstStart}; emergency_end_chunk_exclusive={emergencyEndExclusive}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; transport_epoch={context.V6TransportHandoff?.EpochId ?? 0}");
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
                $"event=filetransfer_v4_repair_scheduled; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId ?? "(none)")}; epoch={state.Epoch}; range_count={normalizedRanges.Count}; requested_chunk_count={requestedChunkCount}; scheduled_chunk_count={deduped.Count}; scheduled_chunk_indices={FormatV4ChunkIndicesForLog(deduped)}; repair_delivery_mode={FormatV4RepairDeliveryMode(deliveryDecision.Mode)}; repair_delivery_escalation_reason={deliveryDecision.Reason}; first_start_chunk_index={firstStart}; last_end_chunk_exclusive={lastEndExclusive}; frontier_tail_repair={(frontierTailRepair ? 1 : 0)}; post_rebind_frontier_exclusive={(postRebindFrontierRepair ? 1 : 0)}; credit_exhausted_time_ms_at_repair={deliveryDecision.CreditExhaustedTimeMs}; remote_next_expected_chunk_index={stats.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={stats.ChunksAcceptedForTransport}; skipped_obsolete_count={stats.SkippedObsoleteCount}; skipped_future_count={stats.SkippedFutureCount}; skipped_out_of_bounds_count={stats.SkippedOutOfBoundsCount}");
            context.SignalSparseSenderPump();
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
                ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context))
            {
                return (FileTransferV4RepairDeliveryMode.ControlBulkRedundant, "primary_regular_nkn_frontier_first_send", creditStallAgeMs);
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

    private async Task<bool> SendChunkIndicesV4Async(
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
            return true;
        }

        var isRegularV4PeerSilenceSafetyRepair =
            repairSend &&
            string.Equals(repairDeliveryReason, "regular_v4_peer_silence_safety_replay", StringComparison.Ordinal);
        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            var pending = new Queue<PendingV4TransportSend>();
            long pendingRawBytes = 0;
            var stopForTransportPause = false;
            var stopForLiveRouteChange = false;
            var completedWithoutTransportPause = true;
            var transportStopReason = "transport_paused";
            var liveRouteChangeStopReason = "live_route_changed";

            void MarkStopForTransportPauseLocked()
            {
                stopForTransportPause = true;
                completedWithoutTransportPause = false;
                transportStopReason = context.PullTransportPauseReason ?? transportStopReason;
                MarkOutboundV4SenderPumpTransportPausedLocked(context);
            }

            void AbandonPendingForTransportPause()
            {
                if (pending.Count == 0)
                {
                    return;
                }

                var abandonedFrameCount = 0;
                long abandonedBytes = 0;
                while (pending.Count > 0)
                {
                    var abandoned = pending.Dequeue();
                    abandonedFrameCount++;
                    abandonedBytes += abandoned.Prepared.RawBytes;
                    abandoned.SendCts?.Cancel();
                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                        abandoned.SendTask,
                        abandoned.SendCts,
                        context.TransferId,
                        context.SessionId,
                        abandoned.Prepared.StartChunkIndex,
                        abandoned.Prepared.ChunkCount);
                }

                pendingRawBytes = 0;
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context))
                    {
                        context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - abandonedFrameCount);
                        context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - abandonedBytes);
                    }
                }

                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event={ResolveOutboundV4PendingSendsAbandonedForTransportPauseEventName(context)}; transfer_id={context.TransferId}; session_id={context.SessionId}; abandoned_frame_count={abandonedFrameCount}; abandoned_raw_bytes={abandonedBytes}; reason={FormatProtocolLogValue(context.PullTransportPauseReason ?? transportStopReason)}");
            }

            void AbandonPendingForLiveRouteChange()
            {
                if (pending.Count == 0)
                {
                    return;
                }

                var abandonedFrameCount = 0;
                long abandonedBytes = 0;
                while (pending.Count > 0)
                {
                    var abandoned = pending.Dequeue();
                    abandonedFrameCount++;
                    abandonedBytes += abandoned.Prepared.RawBytes;
                    abandoned.SendCts?.Cancel();
                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                        abandoned.SendTask,
                        abandoned.SendCts,
                        context.TransferId,
                        context.SessionId,
                        abandoned.Prepared.StartChunkIndex,
                        abandoned.Prepared.ChunkCount);
                }

                pendingRawBytes = 0;
                lock (gate)
                {
                    if (ReferenceEquals(outboundTransfer, context))
                    {
                        context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - abandonedFrameCount);
                        context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - abandonedBytes);
                    }
                }

                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v4_pending_transport_sends_abandoned_for_live_route_change; transfer_id={context.TransferId}; session_id={context.SessionId}; abandoned_frame_count={abandonedFrameCount}; abandoned_raw_bytes={abandonedBytes}; reason={FormatProtocolLogValue(liveRouteChangeStopReason)}; current_route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}");
            }

            async Task RetireNextAsync()
            {
                var pendingSend = pending.Dequeue();
                pendingRawBytes = Math.Max(0, pendingRawBytes - pendingSend.Prepared.RawBytes);
                Exception? sendException = null;
                var timedOutForBoundTransport = false;
                var boundSendForTransport =
                    ShouldBoundOutboundV4TransportSend(context) ||
                    isRegularV4PeerSilenceSafetyRepair;
                var abandonedForTransportPause = false;
                var abandonedForLiveRouteChange = false;
                lock (gate)
                {
                    var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                    if (ReferenceEquals(outboundTransfer, context) &&
                        !context.IsTerminal &&
                        blockForTransportPause)
                    {
                        MarkStopForTransportPauseLocked();
                        abandonedForTransportPause = true;
                    }
                }

                if (abandonedForTransportPause)
                {
                    pendingSend.SendCts?.Cancel();
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context))
                        {
                            context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                            context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                        }
                    }

                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                        pendingSend.SendTask,
                        pendingSend.SendCts,
                        context.TransferId,
                        context.SessionId,
                        pendingSend.Prepared.StartChunkIndex,
                        pendingSend.Prepared.ChunkCount);

                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_v4_transport_send_abandoned_for_transport_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(transportStopReason)}; repair_request_key={repairRequestKey}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}");
                    return;
                }

                try
                {
                    if (boundSendForTransport &&
                        !pendingSend.SendTask.IsCompleted)
                    {
                        var sendTimeout = CurrentV6RegularNknSparseRuntimeV4TransportSendTimeout;
                        var timeoutStarted = Stopwatch.GetTimestamp();
                        while (!pendingSend.SendTask.IsCompleted)
                        {
                            var elapsed = Stopwatch.GetElapsedTime(timeoutStarted);
                            var remaining = sendTimeout - elapsed;
                            if (remaining <= TimeSpan.Zero)
                            {
                                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                                timedOutForBoundTransport = true;
                                break;
                            }

                            var delay = remaining < TimeSpan.FromMilliseconds(PullSessionReceivePollDelayMs)
                                ? remaining
                                : TimeSpan.FromMilliseconds(PullSessionReceivePollDelayMs);
                            var completed = await Task.WhenAny(
                                    pendingSend.SendTask,
                                    Task.Delay(delay, context.LifetimeCts.Token))
                                .ConfigureAwait(false);
                            if (completed == pendingSend.SendTask)
                            {
                                break;
                            }

                            if (!pendingSend.ScheduledRegularNknV4FastRuntime)
                            {
                                continue;
                            }

                            lock (gate)
                            {
                                var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                                if (ReferenceEquals(outboundTransfer, context) &&
                                    !context.IsTerminal &&
                                    blockForTransportPause)
                                {
                                    MarkStopForTransportPauseLocked();
                                    abandonedForTransportPause = true;
                                }
                                else if (ReferenceEquals(outboundTransfer, context) &&
                                         !context.IsTerminal &&
                                         !context.RouteRuntime.UsesRegularNknV4FastRuntime)
                                {
                                    stopForLiveRouteChange = true;
                                    completedWithoutTransportPause = false;
                                    liveRouteChangeStopReason = "regular_v4_send_superseded_by_live_route";
                                    context.SparseSenderPumpLastWakeReason = "live_route_changed";
                                    abandonedForLiveRouteChange = true;
                                }
                            }

                            if (abandonedForTransportPause)
                            {
                                pendingSend.SendCts?.Cancel();
                                lock (gate)
                                {
                                    if (ReferenceEquals(outboundTransfer, context))
                                    {
                                        context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                                        context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                                    }
                                }

                                _ = ObserveAbandonedOutboundV4TransportSendAsync(
                                    pendingSend.SendTask,
                                    pendingSend.SendCts,
                                    context.TransferId,
                                    context.SessionId,
                                    pendingSend.Prepared.StartChunkIndex,
                                    pendingSend.Prepared.ChunkCount);

                                LocalOperationalLog.Warn(
                                    "FileTransferService",
                                    $"event=filetransfer_v4_transport_send_abandoned_for_transport_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(transportStopReason)}; repair_request_key={repairRequestKey}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}");
                                return;
                            }

                            if (abandonedForLiveRouteChange)
                            {
                                pendingSend.SendCts?.Cancel();
                                lock (gate)
                                {
                                    if (ReferenceEquals(outboundTransfer, context))
                                    {
                                        context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                                        context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                                    }
                                }

                                _ = ObserveAbandonedOutboundV4TransportSendAsync(
                                    pendingSend.SendTask,
                                    pendingSend.SendCts,
                                    context.TransferId,
                                    context.SessionId,
                                    pendingSend.Prepared.StartChunkIndex,
                                    pendingSend.Prepared.ChunkCount);

                                LocalOperationalLog.Warn(
                                    "FileTransferService",
                                    $"event=filetransfer_v4_transport_send_abandoned_for_live_route_change; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(liveRouteChangeStopReason)}; current_route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; repair_request_key={repairRequestKey}");
                                return;
                            }
                        }
                    }

                    if (!timedOutForBoundTransport)
                    {
                        if (ShouldInterruptOutboundV4TransportSendOnPumpSignal(context) &&
                            !pendingSend.ScheduledRegularNknV4FastRuntime &&
                            !pendingSend.SendTask.IsCompleted)
                        {
                            while (!pendingSend.SendTask.IsCompleted)
                            {
                                var completed = await Task.WhenAny(
                                        pendingSend.SendTask,
                                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                                    .ConfigureAwait(false);
                                if (completed == pendingSend.SendTask)
                                {
                                    break;
                                }

                                lock (gate)
                                {
                                    var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                                    if (ReferenceEquals(outboundTransfer, context) &&
                                        !context.IsTerminal &&
                                        blockForTransportPause)
                                    {
                                        MarkStopForTransportPauseLocked();
                                        abandonedForTransportPause = true;
                                    }
                                }

                                if (abandonedForTransportPause)
                                {
                                    pendingSend.SendCts?.Cancel();
                                    lock (gate)
                                    {
                                        if (ReferenceEquals(outboundTransfer, context))
                                        {
                                            context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                                            context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                                        }
                                    }

                                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                                        pendingSend.SendTask,
                                        pendingSend.SendCts,
                                        context.TransferId,
                                        context.SessionId,
                                        pendingSend.Prepared.StartChunkIndex,
                                        pendingSend.Prepared.ChunkCount);

                                    LocalOperationalLog.Warn(
                                        "FileTransferService",
                                        $"event=filetransfer_v4_transport_send_abandoned_for_transport_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(transportStopReason)}; repair_request_key={repairRequestKey}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}");
                                    return;
                                }
                            }
                        }
                        else if (pendingSend.ScheduledRegularNknV4FastRuntime &&
                            !pendingSend.SendTask.IsCompleted)
                        {
                            while (!pendingSend.SendTask.IsCompleted)
                            {
                                var completed = await Task.WhenAny(
                                        pendingSend.SendTask,
                                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                                    .ConfigureAwait(false);
                                if (completed == pendingSend.SendTask)
                                {
                                    break;
                                }

                                lock (gate)
                                {
                                    var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                                    if (ReferenceEquals(outboundTransfer, context) &&
                                        !context.IsTerminal &&
                                        blockForTransportPause)
                                    {
                                        MarkStopForTransportPauseLocked();
                                        abandonedForTransportPause = true;
                                    }
                                    else if (ReferenceEquals(outboundTransfer, context) &&
                                             !context.IsTerminal &&
                                             !context.RouteRuntime.UsesRegularNknV4FastRuntime)
                                    {
                                        stopForLiveRouteChange = true;
                                        completedWithoutTransportPause = false;
                                        liveRouteChangeStopReason = "regular_v4_send_superseded_by_live_route";
                                        context.SparseSenderPumpLastWakeReason = "live_route_changed";
                                        abandonedForLiveRouteChange = true;
                                    }
                                }

                                if (abandonedForTransportPause)
                                {
                                    pendingSend.SendCts?.Cancel();
                                    lock (gate)
                                    {
                                        if (ReferenceEquals(outboundTransfer, context))
                                        {
                                            context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                                            context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                                        }
                                    }

                                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                                        pendingSend.SendTask,
                                        pendingSend.SendCts,
                                        context.TransferId,
                                        context.SessionId,
                                        pendingSend.Prepared.StartChunkIndex,
                                        pendingSend.Prepared.ChunkCount);

                                    LocalOperationalLog.Warn(
                                        "FileTransferService",
                                        $"event=filetransfer_v4_transport_send_abandoned_for_transport_pause; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(transportStopReason)}; repair_request_key={repairRequestKey}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}");
                                    return;
                                }

                                if (abandonedForLiveRouteChange)
                                {
                                    pendingSend.SendCts?.Cancel();
                                    lock (gate)
                                    {
                                        if (ReferenceEquals(outboundTransfer, context))
                                        {
                                            context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                                            context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                                        }
                                    }

                                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                                        pendingSend.SendTask,
                                        pendingSend.SendCts,
                                        context.TransferId,
                                        context.SessionId,
                                        pendingSend.Prepared.StartChunkIndex,
                                        pendingSend.Prepared.ChunkCount);

                                    LocalOperationalLog.Warn(
                                        "FileTransferService",
                                        $"event=filetransfer_v4_transport_send_abandoned_for_live_route_change; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(liveRouteChangeStopReason)}; current_route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; repair_request_key={repairRequestKey}");
                                    return;
                                }
                            }
                        }

                        await pendingSend.SendTask.ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    sendException = ex;
                }

                var sentUtc = DateTimeOffset.UtcNow;
                if (timedOutForBoundTransport)
                {
                    stopForTransportPause = true;
                    completedWithoutTransportPause = false;
                    transportStopReason = repairSend
                        ? "repair_transport_send_timeout"
                        : "normal_transport_send_timeout";
                    pendingSend.SendCts?.Cancel();
                    lock (gate)
                    {
                        if (ReferenceEquals(outboundTransfer, context))
                        {
                            context.PullSenderPipelineCurrentInFlightFrames = Math.Max(0, context.PullSenderPipelineCurrentInFlightFrames - 1);
                            context.PullSenderPipelineCurrentInFlightBytes = Math.Max(0, context.PullSenderPipelineCurrentInFlightBytes - pendingSend.Prepared.RawBytes);
                            context.PullSenderPipelineFailedFramesRecent++;
                            context.PullSenderPipelineFailedFramesTotal++;
                            context.SparseSenderPumpLastWakeReason = "transport_send_timeout";
                        }
                    }

                    _ = ObserveAbandonedOutboundV4TransportSendAsync(
                        pendingSend.SendTask,
                        pendingSend.SendCts,
                        context.TransferId,
                        context.SessionId,
                        pendingSend.Prepared.StartChunkIndex,
                        pendingSend.Prepared.ChunkCount);

                    var timeoutEventName = IsFileTunaV4PostTunaRecoveryActiveLocked(context)
                        ? "filetransfer_v4_transport_send_timeout_deferred_for_file_tuna_v4_post_tuna_recovery"
                        : isRegularV4PeerSilenceSafetyRepair
                            ? "filetransfer_v4_transport_send_timeout_deferred_for_regular_v4_peer_silence_repair"
                            : context.RouteRuntime.UsesRegularNknV4FastRuntime &&
                              context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                              context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4
                            ? "filetransfer_v4_transport_send_timeout_deferred_for_regular_nkn_v4_fast_runtime"
                            : ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context)
                            ? "filetransfer_v4_transport_send_timeout_deferred_for_regular_v4_receive_stall_recovery"
                            : IsOutboundPostTunaFallbackV6LiveSparseRecoveryActiveLocked(context)
                            ? "filetransfer_v4_transport_send_timeout_deferred_for_post_tuna_fallback_v6_live_sparse_recovery"
                            : "filetransfer_v4_transport_send_timeout_deferred_for_v6_regular_nkn_sparse_runtime";
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event={timeoutEventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; timeout_ms={(long)CurrentV6RegularNknSparseRuntimeV4TransportSendTimeout.TotalMilliseconds}; repair_request_key={repairRequestKey}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}");
                    return;
                }

                pendingSend.SendCts?.Dispose();

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
                            context.PullSenderPipelineFailedFramesTotal++;
                        }
                    }
                }

                if (sendException is not null)
                {
                    var deferredForTransportPause = false;
                    var activationPauseStarted = false;
                    var activationPauseAlreadyRecovered = false;
                    string pauseReason = "transport_paused";
                    int rebindGeneration = 0;
                    lock (gate)
                    {
                        var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                        if (ReferenceEquals(outboundTransfer, context) &&
                            !context.IsTerminal &&
                            blockForTransportPause)
                        {
                            deferredForTransportPause = true;
                            pauseReason = context.PullTransportPauseReason ?? pauseReason;
                            rebindGeneration = context.PullTransportRebindGeneration;
                            MarkStopForTransportPauseLocked();
                        }
                        else if (TryDeferOutboundV4SendForTunaActivationPauseLocked(
                                     context,
                                     dataSession.IsAvailable,
                                     sendException,
                                     out pauseReason,
                                     out rebindGeneration,
                                     out activationPauseStarted,
                                     out activationPauseAlreadyRecovered))
                        {
                            deferredForTransportPause = true;
                            stopForTransportPause = true;
                            completedWithoutTransportPause = false;
                            transportStopReason = pauseReason;
                        }
                    }

                    if (deferredForTransportPause)
                    {
                        completedWithoutTransportPause = false;
                        if (activationPauseStarted)
                        {
                            LogTransportPaused(FileTransferDirection.Outbound, context.TransferId, context.SessionId, pauseReason);
                            ScheduleOutboundV4TransportPauseControlRetry(context, paused: true, pauseReason);
                        }

                        var eventName = activationPauseAlreadyRecovered
                            ? "filetransfer_v4_transport_send_failure_deferred_for_recovered_tuna_activation_pause"
                            : ShouldPauseOutboundV4SenderPumpForV6RegularNknSparseRuntimeLocked(context)
                            ? "filetransfer_v4_transport_send_failure_deferred_for_v6_regular_nkn_sparse_runtime"
                            : ShouldPauseOutboundV4SenderPumpForRegularNknV4ReceiveStallRecoveryLocked(context)
                            ? "filetransfer_v4_transport_send_failure_deferred_for_regular_v4_receive_stall_recovery"
                            : "filetransfer_v4_transport_send_failure_deferred_for_transport_pause";
                        LocalOperationalLog.Warn(
                            "FileTransferService",
                            $"event={eventName}; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; send_attempt={pendingSend.SendAttempt}; send_attempt_count={pendingSend.SendAttemptCount}; reason={FormatProtocolLogValue(pauseReason)}; rebind_generation={rebindGeneration}; error={FormatProtocolLogValue(sendException.GetType().Name)}; message={FormatProtocolLogValue(sendException.Message)}");
                        return;
                    }

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
                    context.PullSenderRawBytesTotal += pendingSend.Prepared.RawBytes;
                    context.PullSenderBatchFramesRecent++;
                    context.PullSenderBatchFramesTotal++;
                    context.PullSenderChunkCountRecent += pendingSend.Prepared.ChunkCount;
                    context.PullSenderChunkCountTotal += pendingSend.Prepared.ChunkCount;
                    if (repairSend)
                    {
                        context.PullSenderRepairRawBytesTotal += pendingSend.Prepared.RawBytes;
                        context.PullSenderRepairBatchFramesTotal++;
                        context.PullSenderRepairChunkCountTotal += pendingSend.Prepared.ChunkCount;
                        context.PullSenderRepairSendCountRecent += pendingSend.Prepared.ChunkCount;
                    }
                    else
                    {
                        context.PullSenderNormalRawBytesTotal += pendingSend.Prepared.RawBytes;
                        context.PullSenderNormalBatchFramesTotal++;
                        context.PullSenderNormalChunkCountTotal += pendingSend.Prepared.ChunkCount;
                    }

                    MaybeLogOutboundV4SenderPumpSummaryLocked(context, sentUtc, force: false);
                }

                if (repairSend || FileTransferDiagnosticLogPolicy.TraceEnabled)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_chunk_batch_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; start_chunk_index={pendingSend.Prepared.StartChunkIndex}; batch_chunk_count={pendingSend.Prepared.ChunkCount}; raw_bytes={pendingSend.Prepared.RawBytes}; repair_send={(repairSend ? 1 : 0)}; mixed_screenshare={(IsV4MixedScreenShareActive() ? 1 : 0)}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; repair_delivery_mode={(repairSend ? FormatV4RepairDeliveryMode(repairDeliveryMode) : "none")}; repair_delivery_escalation_reason={(repairSend ? repairDeliveryReason : "none")}; repair_batch_send_attempt={pendingSend.SendAttempt}; repair_batch_send_attempt_count={pendingSend.SendAttemptCount}");
                }
            }

            async Task<bool> ScheduleAsync(PreparedV4TransportSend prepared, int sendAttempt, int sendAttemptCount)
            {
                if (stopForTransportPause || stopForLiveRouteChange)
                {
                    return false;
                }

                while (pending.Count >= V4SenderPumpDepth ||
                       (pending.Count > 0 && pendingRawBytes + prepared.RawBytes > V4SenderPumpPendingBytes))
                {
                    var slotWaitStarted = Stopwatch.GetTimestamp();
                    await RetireNextAsync().ConfigureAwait(false);
                    if (stopForTransportPause || stopForLiveRouteChange)
                    {
                        return false;
                    }

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
                    var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                    if (!ReferenceEquals(outboundTransfer, context) ||
                        context.IsTerminal ||
                        context.UserPaused ||
                        context.PeerPaused ||
                        blockForTransportPause)
                    {
                        if (blockForTransportPause)
                        {
                            MarkStopForTransportPauseLocked();
                        }

                        return false;
                    }
                }

                var scheduleStarted = Stopwatch.GetTimestamp();
                Task sendTask;
                CancellationTokenSource? sendCts = null;
                try
                {
                    var sendToken = context.LifetimeCts.Token;
                    if (ShouldBoundOutboundV4TransportSend(context) ||
                        isRegularV4PeerSilenceSafetyRepair)
                    {
                        sendCts = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeCts.Token);
                        sendToken = sendCts.Token;
                    }

                    sendTask = dataSession.SendAsync(prepared.Frame, sendToken);
                }
                catch (Exception ex)
                {
                    sendCts?.Dispose();
                    sendCts = null;
                    sendTask = Task.FromException(ex);
                }

                var scheduledUtc = DateTimeOffset.UtcNow;
                var scheduleDurationMs = (long)Math.Max(0, Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds);
                var scheduledRegularNknV4FastRuntime =
                    context.RouteRuntime.UsesRegularNknV4FastRuntime &&
                    context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                    context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4;
                pending.Enqueue(new PendingV4TransportSend(prepared, sendTask, sendCts, scheduledUtc, sendAttempt, sendAttemptCount, scheduledRegularNknV4FastRuntime));
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

            var primaryRegularNknFrontierAnchorBarrier =
                repairSend &&
                string.Equals(repairDeliveryReason, "primary_regular_nkn_frontier_first_send", StringComparison.Ordinal) &&
                ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context);
            var primaryRegularNknFrontierAnchorBarrierUsed = false;
            var maxRepairBatchSegments = ResolveV4MaxBatchSegments(repairSend: true);

            for (var index = 0; index < chunkIndices.Count; index++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    var blockForTransportPause = ShouldBlockOutboundV4TransportSendForTransportPauseLocked(context, repairSend);
                    var blockNormalSendAheadForFileTunaV4PostTunaRecovery =
                        !repairSend &&
                        ReferenceEquals(outboundTransfer, context) &&
                        !context.IsTerminal &&
                        ShouldBlockOutboundV4NormalSendAheadForFileTunaV4PostTunaRecoveryLocked(context);
                    if (!ReferenceEquals(outboundTransfer, context) ||
                        context.IsTerminal ||
                        context.UserPaused ||
                        context.PeerPaused ||
                        blockForTransportPause ||
                        blockNormalSendAheadForFileTunaV4PostTunaRecovery)
                    {
                        if (blockForTransportPause)
                        {
                            MarkStopForTransportPauseLocked();
                        }
                        else if (blockNormalSendAheadForFileTunaV4PostTunaRecovery)
                        {
                            context.SparseSenderPumpLastWakeReason = "file_tuna_v4_post_tuna_frontier_repair";
                            LocalOperationalLog.Info(
                                "FileTransferService",
                                $"event=filetransfer_v4_normal_send_blocked_for_file_tuna_v4_post_tuna_frontier_repair; transfer_id={context.TransferId}; session_id={context.SessionId}; rebind_generation={context.PullTransportRebindGeneration}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; repair_queue_depth={context.PullV4SenderPumpRepairQueue.Count}");
                        }

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
                        completedWithoutTransportPause = false;
                        index = chunkIndices.Count;
                        break;
                    }
                }

                if (primaryRegularNknFrontierAnchorBarrier &&
                    !primaryRegularNknFrontierAnchorBarrierUsed &&
                    preparedBatch.StartChunkIndex == chunkIndices[0] &&
                    preparedBatch.ChunkCount <= maxRepairBatchSegments &&
                    index + preparedBatch.ChunkCount < chunkIndices.Count)
                {
                    primaryRegularNknFrontierAnchorBarrierUsed = true;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_primary_regular_nkn_bulk_v6_frontier_repair_anchor_barrier; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={repairRequestKey}; anchor_start_chunk_index={preparedBatch.StartChunkIndex}; anchor_chunk_count={preparedBatch.ChunkCount}; remaining_chunk_count={chunkIndices.Count - preparedBatch.ChunkCount}; repair_delivery_mode={FormatV4RepairDeliveryMode(repairDeliveryMode)}; repair_delivery_escalation_reason={repairDeliveryReason}");
                    while (pending.Count > 0)
                    {
                        await RetireNextAsync().ConfigureAwait(false);
                        if (stopForTransportPause || stopForLiveRouteChange)
                        {
                            completedWithoutTransportPause = false;
                            index = chunkIndices.Count;
                            break;
                        }
                    }
                }

                index += preparedBatch.ChunkCount - 1;

            }

            while (pending.Count > 0)
            {
                if (stopForTransportPause)
                {
                    completedWithoutTransportPause = false;
                    AbandonPendingForTransportPause();
                    break;
                }

                if (stopForLiveRouteChange)
                {
                    completedWithoutTransportPause = false;
                    AbandonPendingForLiveRouteChange();
                    break;
                }

                await RetireNextAsync().ConfigureAwait(false);
            }

            return completedWithoutTransportPause &&
                !stopForTransportPause &&
                !stopForLiveRouteChange;
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ObserveAbandonedOutboundV4TransportSendAsync(
        Task sendTask,
        CancellationTokenSource? sendCts,
        string transferId,
        string sessionId,
        int startChunkIndex,
        int chunkCount)
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
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_abandoned_transport_send_observed; transfer_id={transferId}; session_id={sessionId}; start_chunk_index={startChunkIndex}; batch_chunk_count={chunkCount}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
        }
        finally
        {
            sendCts?.Dispose();
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
                !CanSerializeChunkBatchV4(
                    context.SessionId,
                    context.TransferId,
                    startChunkIndex,
                    dataSegments,
                    chunkBytes,
                    ShouldUseV6SparseCreditEnvelope(context)))
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

        var unresolvedV6Epoch = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
            ? context.V6TransportEpoch
            : null;
        var v6TransportEpoch = unresolvedV6Epoch?.EpochId ?? context.V6TransportHandoff?.EpochId ?? 0;
        var v6RecoveryMode = protocolRecoveryMode ??
                             (unresolvedV6Epoch is not null
                                 ? FormatV6TransportEpochState(unresolvedV6Epoch.State)
                                 : context.V6TransportHandoff is null
                                     ? null
                                     : FormatV6TransportHandoffState(context.V6TransportHandoff.State));
        var v6Priority = repairSend
            ? protocolPriority ??
              (unresolvedV6Epoch is not null
                  ? unresolvedV6Epoch.State == V6TransportEpochState.BackfillRepair
                      ? "backfill"
                      : "frontier"
                  : IsV6TransportHandoffBlockingTail(context.V6TransportHandoff)
                      ? "frontier"
                      : "repair")
            : null;

        FileTransferChunkBatchFrameV4 batch;
        if (ShouldUseV6SparseCreditEnvelope(context))
        {
            batch = new FileTransferChunkBatchFrameV6
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
                TransportEpoch = v6TransportEpoch,
                BatchId = v6TransportEpoch <= 0
                    ? null
                    : $"v6:{v6TransportEpoch}:{startChunkIndex}:{dataSegments.Count}",
                RepairRequestId = repairSend ? protocolRepairRequestId ?? repairRequestKey : null,
                Priority = v6Priority,
                RecoveryMode = v6RecoveryMode,
            };
            _ = FileTransferDataFrameCodec.Serialize(batch);
        }
        else
        {
            batch = new FileTransferChunkBatchFrameV4
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
                ForceRegularNknBulk = ShouldForceRegularNknBulkForV4Route(context),
            };
            _ = FileTransferDataFrameCodec.SerializeLegacyV4(batch);
        }
        return new PreparedV4TransportSend(batch, startChunkIndex, dataSegments.Count, totalRawBytes);
    }

    private static bool ShouldForceRegularNknBulkForV4Route(OutboundTransferContext context)
        => context.RouteRuntime.UsesRegularNknV4FastRuntime &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4;

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
        byte[] candidateSegment,
        bool useV6Envelope = true)
    {
        var candidateSegments = new byte[existingSegments.Count + 1][];
        for (var index = 0; index < existingSegments.Count; index++)
        {
            candidateSegments[index] = existingSegments[index];
        }

        candidateSegments[^1] = candidateSegment;
        try
        {
            FileTransferChunkBatchFrameV4 candidateBatch = useV6Envelope
                ? new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    ChunkCount = candidateSegments.Length,
                    DataSegments = candidateSegments,
                }
                : new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunkIndex,
                    ChunkCount = candidateSegments.Length,
                    DataSegments = candidateSegments,
                };
            _ = useV6Envelope
                ? FileTransferDataFrameCodec.Serialize(candidateBatch)
                : FileTransferDataFrameCodec.SerializeLegacyV4(candidateBatch);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<FileTransferRangeV4> NormalizeV4MissingRangesForSend(
        IReadOnlyList<FileTransferRangeV4> ranges,
        int chunkCount,
        int maxMissingChunks = FileTransferProtocol.MaxStateMissingChunksV4)
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
            var remaining = maxMissingChunks - totalChunks;
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
        int maxMissingChunks = FileTransferProtocol.MaxStateMissingChunksV4,
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
                    maxMissingChunks);
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
            if (selectedChunks >= maxMissingChunks)
            {
                break;
            }

            var start = range.StartChunkIndex;
            var endExclusive = range.StartChunkIndex + range.ChunkCount;
            if (start <= remoteFrontier && remoteFrontier < endExclusive)
            {
                start = remoteFrontier + Math.Min(endExclusive - remoteFrontier, maxMissingChunks);
            }

            if (endExclusive <= start)
            {
                continue;
            }

            var count = Math.Min(endExclusive - start, maxMissingChunks - selectedChunks);
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

    private static int ResolveV4RepairRequestMaxChunksForSend(OutboundTransferContext context, FileTransferStateFrameV4 state)
        => state is FileTransferReceiverStateFrameV6 &&
           ShouldUseV6RegularNknSparseRuntime(context) &&
           IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context)
            ? V6RegularNknSparseRuntimeRepairBurstMaxChunks
            : FileTransferProtocol.MaxStateMissingChunksV4;

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

    private void SignalOutboundSparseSenderPump(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) && !context.IsTerminal)
            {
                context.SignalSparseSenderPump();
            }
        }
    }

    private async Task StopOutboundSparseSenderPumpAsync(OutboundTransferContext context, Task? senderPumpTask)
    {
        if (senderPumpTask is null)
        {
            return;
        }

        context.SignalSparseSenderPump();
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
            MaybeObserveIdleRegularV4ControlFeedbackPressureLocked(context, now);
            return;
        }

        var availableCreditChunks = Math.Max(0, Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport);
        var creditExhaustedTimeMs = context.V4SenderCreditExhaustedSinceUtc is null || availableCreditChunks > 0 || context.V4TerminalReady
            ? 0
            : (long)Math.Max(0, (now - context.V4SenderCreditExhaustedSinceUtc.Value).TotalMilliseconds);
        var pendingRepairCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        _ = MaybeObserveRegularV4ControlFeedbackPressure(
            context,
            creditExhaustedTimeMs,
            availableCreditChunks,
            pendingRepairCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_sender_pump_summary; transfer_id={context.TransferId}; session_id={context.SessionId}; sample_window_ms={PullControlChatterWindowMs}; configured_depth={V4SenderPumpDepth}; effective_depth={V4SenderPumpDepth}; in_flight_frames={context.PullSenderPipelineCurrentInFlightFrames}; in_flight_bytes={context.PullSenderPipelineCurrentInFlightBytes}; scheduled_frames={context.PullSenderPipelineScheduledFramesRecent}; normal_scheduled_frames={context.PullSenderV4NormalScheduledFramesRecent}; repair_scheduled_frames={context.PullSenderV4RepairScheduledFramesRecent}; completed_frames={context.PullSenderPipelineCompletedFramesRecent}; failed_frames={context.PullSenderPipelineFailedFramesRecent}; raw_bytes_sent={context.PullSenderRawBytesRecent}; batch_frames_sent={context.PullSenderBatchFramesRecent}; chunk_count_sent={context.PullSenderChunkCountRecent}; repair_send_count={context.PullSenderRepairSendCountRecent}; send_wait_count={context.PullSenderSendWaitCountRecent}; raw_bytes_sent_total={context.PullSenderRawBytesTotal}; normal_raw_bytes_sent_total={context.PullSenderNormalRawBytesTotal}; repair_raw_bytes_sent_total={context.PullSenderRepairRawBytesTotal}; batch_frames_sent_total={context.PullSenderBatchFramesTotal}; normal_batch_frames_sent_total={context.PullSenderNormalBatchFramesTotal}; repair_batch_frames_sent_total={context.PullSenderRepairBatchFramesTotal}; chunk_count_sent_total={context.PullSenderChunkCountTotal}; normal_chunk_count_sent_total={context.PullSenderNormalChunkCountTotal}; repair_chunk_count_sent_total={context.PullSenderRepairChunkCountTotal}; send_wait_count_total={context.PullSenderSendWaitCountTotal}; failed_frames_total={context.PullSenderPipelineFailedFramesTotal}; credit_exhausted_time_ms={creditExhaustedTimeMs}; available_credit_chunks={availableCreditChunks}; available_credit_bytes={availableCreditChunks * (long)Math.Max(1, context.ChunkSizeBytes)}; next_unsent_chunk_index={context.ChunksAcceptedForTransport}; credit_ceiling_chunk_index={context.RemoteGrantedUntilExclusive}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; terminal_ready={(context.V4TerminalReady ? 1 : 0)}; pump_wake_reason={context.SparseSenderPumpLastWakeReason}; repair_request_key={context.V4SenderPumpLastRepairRequestKey}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; pending_repair_count={pendingRepairCount}; sent_cache_chunk_count={context.PullSentChunkCache.Count}; sent_cache_bytes={context.PullSentChunkCacheBytes}");
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

    private void MaybeObserveIdleRegularV4ControlFeedbackPressureLocked(OutboundTransferContext context, DateTimeOffset now)
    {
        if (context.PullV4LastRegularNknControlFeedbackPressureObservedUtc is { } lastObserved &&
            now - lastObserved < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        var availableCreditChunks = Math.Max(
            0,
            Math.Min(context.RemoteGrantedUntilExclusive, context.ChunkCount) - context.ChunksAcceptedForTransport);
        var creditExhaustedTimeMs = context.V4SenderCreditExhaustedSinceUtc is null || availableCreditChunks > 0 || context.V4TerminalReady
            ? 0
            : (long)Math.Max(0, (now - context.V4SenderCreditExhaustedSinceUtc.Value).TotalMilliseconds);
        var pendingRepairCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        _ = MaybeObserveRegularV4ControlFeedbackPressure(
            context,
            creditExhaustedTimeMs,
            availableCreditChunks,
            pendingRepairCount);
    }

    private bool MaybeObserveRegularV4ControlFeedbackPressure(
        OutboundTransferContext context,
        long creditExhaustedTimeMs,
        int availableCreditChunks,
        int pendingRepairCount)
    {
        var now = DateTimeOffset.UtcNow;
        var frontierLagChunks = Math.Max(0, context.ChunksAcceptedForTransport - context.RemoteNextExpectedChunkIndex);
        var activeRepairRequestCount = context.PullV4SenderPumpRepairRequests.Count;
        var effectivePendingRepairCount = Math.Max(pendingRepairCount, activeRepairRequestCount);
        var lastGrantSilenceMs = context.PullV4LastGrantReceivedUtc is null
            ? 0
            : (long)Math.Max(0, (now - context.PullV4LastGrantReceivedUtc.Value).TotalMilliseconds);
        var creditFrontierPressure = availableCreditChunks <= 0 &&
            creditExhaustedTimeMs >= PullV4RegularNknControlFeedbackPressureMinCreditExhaustedMs &&
            (frontierLagChunks >= PullV4RegularNknControlFeedbackPressureMinFrontierLagChunks ||
             effectivePendingRepairCount > 0);
        var remoteFrontierPressure = availableCreditChunks > 0 &&
            frontierLagChunks >= PullV4RegularNknControlFeedbackPressureMinFrontierLagChunks &&
            (effectivePendingRepairCount > 0 ||
             lastGrantSilenceMs >= PullControlChatterWindowMs * 2L);

        if (!context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.V4TerminalReady ||
            (!creditFrontierPressure && !remoteFrontierPressure))
        {
            return false;
        }

        IFileTransferRegularV4ControlFeedbackPressureObserver? observer;
        lock (gate)
        {
            observer = transport as IFileTransferRegularV4ControlFeedbackPressureObserver;
        }

        if (observer is null)
        {
            return false;
        }

        var pressureReason = remoteFrontierPressure && !creditFrontierPressure
            ? "regular_v4_sender_remote_frontier_pressure"
            : "regular_v4_credit_frontier_pressure";
        var replayQueued = MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked(context, pressureReason);
        if (replayQueued)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_regular_v4_feedback_pressure_replay_armed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(pressureReason)}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; frontier_lag_chunks={frontierLagChunks}; available_credit_chunks={availableCreditChunks}; pending_repair_count={effectivePendingRepairCount}");
        }

        observer.ObserveRegularV4ControlFeedbackPressure(new FileTransferRegularV4ControlFeedbackPressure(
            context.SessionId,
            context.TransferId,
            creditExhaustedTimeMs,
            frontierLagChunks,
            effectivePendingRepairCount,
            pressureReason));
        context.PullV4LastRegularNknControlFeedbackPressureObservedUtc = now;
        return true;
    }

    private void MaybeObserveRegularV4ReceiverFrontierRepairPressure(
        InboundTransferContext context,
        int frontierLagChunks,
        int pendingRepairCount)
    {
        if (!context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.IsTerminal ||
            pendingRepairCount <= 0)
        {
            return;
        }

        IFileTransferRegularV4ControlFeedbackPressureObserver? observer;
        lock (gate)
        {
            observer = transport as IFileTransferRegularV4ControlFeedbackPressureObserver;
        }

        if (observer is null)
        {
            return;
        }

        observer.ObserveRegularV4ControlFeedbackPressure(new FileTransferRegularV4ControlFeedbackPressure(
            context.SessionId,
            context.TransferId,
            CreditExhaustedTimeMs: 0,
            Math.Max(0, frontierLagChunks),
            pendingRepairCount,
            "regular_v4_receiver_frontier_repair_due"));
    }

    private static bool ShouldLogV4EfficiencySummary(OutboundTransferContext context)
        => context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 ||
           context.PullSenderRawBytesTotal > 0 ||
           context.PullSenderBatchFramesTotal > 0;

    private static bool ShouldLogV4EfficiencySummary(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 ||
           context.PullReceiverRawBatchBytesTotal > 0 ||
           context.PullReceiverAcceptedRawBytesTotal > 0;

    private static long ComputeEfficiencyPermille(long numerator, long denominator)
        => denominator <= 0
            ? -1
            : (numerator * 1000L + denominator / 2L) / denominator;

    private static void LogV4EfficiencySummary(
        OutboundTransferContext context,
        FileTransferTransferState terminalState)
    {
        if (!ShouldLogV4EfficiencySummary(context))
        {
            return;
        }

        var fileSizeBytes = Math.Max(0L, context.FileSizeBytes);
        var rawToFilePermille = ComputeEfficiencyPermille(context.PullSenderRawBytesTotal, fileSizeBytes);
        var normalToFilePermille = ComputeEfficiencyPermille(context.PullSenderNormalRawBytesTotal, fileSizeBytes);
        var repairToFilePermille = ComputeEfficiencyPermille(context.PullSenderRepairRawBytesTotal, fileSizeBytes);
        var repairToRawPermille = ComputeEfficiencyPermille(context.PullSenderRepairRawBytesTotal, context.PullSenderRawBytesTotal);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_efficiency_summary; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; terminal_state={terminalState.ToString().ToLowerInvariant()}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; file_size_bytes={fileSizeBytes}; bytes_acknowledged_by_receiver={context.BytesAcknowledgedByReceiver}; bytes_transferred={context.BytesTransferred}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; raw_bytes_sent_total={context.PullSenderRawBytesTotal}; normal_raw_bytes_sent_total={context.PullSenderNormalRawBytesTotal}; repair_raw_bytes_sent_total={context.PullSenderRepairRawBytesTotal}; raw_to_file_permille={rawToFilePermille}; normal_to_file_permille={normalToFilePermille}; repair_to_file_permille={repairToFilePermille}; repair_to_raw_permille={repairToRawPermille}; batch_frames_sent_total={context.PullSenderBatchFramesTotal}; normal_batch_frames_sent_total={context.PullSenderNormalBatchFramesTotal}; repair_batch_frames_sent_total={context.PullSenderRepairBatchFramesTotal}; chunk_count_sent_total={context.PullSenderChunkCountTotal}; normal_chunk_count_sent_total={context.PullSenderNormalChunkCountTotal}; repair_chunk_count_sent_total={context.PullSenderRepairChunkCountTotal}; send_wait_count_total={context.PullSenderSendWaitCountTotal}; failed_frames_total={context.PullSenderPipelineFailedFramesTotal}; receiver_state_received_total={context.PullV4StateReceivedCountTotal}; receiver_state_applied_total={context.PullV4StateAppliedCountTotal}; receiver_state_duplicate_total={context.PullV4StateDuplicateCountTotal}; receiver_state_stale_total={context.PullV4StateStaleCountTotal}; pending_repair_count={context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count)}; sent_cache_chunk_count={context.PullSentChunkCache.Count}; sent_cache_bytes={context.PullSentChunkCacheBytes}");
    }

    private static void LogV4EfficiencySummary(
        InboundTransferContext context,
        FileTransferTransferState terminalState)
    {
        if (!ShouldLogV4EfficiencySummary(context))
        {
            return;
        }

        var fileSizeBytes = Math.Max(0L, context.FileSizeBytes);
        var rawToFilePermille = ComputeEfficiencyPermille(context.PullReceiverRawBatchBytesTotal, fileSizeBytes);
        var acceptedToFilePermille = ComputeEfficiencyPermille(context.PullReceiverAcceptedRawBytesTotal, fileSizeBytes);
        var duplicateToRawPermille = ComputeEfficiencyPermille(context.PullReceiverDuplicateOrStaleRawBytesTotal, context.PullReceiverRawBatchBytesTotal);
        var acceptedToRawPermille = ComputeEfficiencyPermille(context.PullReceiverAcceptedRawBytesTotal, context.PullReceiverRawBatchBytesTotal);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_efficiency_summary; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; terminal_state={terminalState.ToString().ToLowerInvariant()}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; file_size_bytes={fileSizeBytes}; bytes_transferred={context.BytesTransferred}; sparse_bytes_written={context.ReceiverSparseBytesWritten}; next_chunk_index={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; raw_batch_bytes_received_total={context.PullReceiverRawBatchBytesTotal}; raw_batch_frames_received_total={context.PullReceiverRawBatchFramesTotal}; accepted_raw_bytes_received_total={context.PullReceiverAcceptedRawBytesTotal}; duplicate_or_stale_raw_bytes_received_total={context.PullReceiverDuplicateOrStaleRawBytesTotal}; raw_to_file_permille={rawToFilePermille}; accepted_to_file_permille={acceptedToFilePermille}; duplicate_or_stale_to_raw_permille={duplicateToRawPermille}; accepted_to_raw_permille={acceptedToRawPermille}; chunk_count_received_total={context.PullReceiverChunkCountTotal}; accepted_chunk_count_total={context.PullReceiverAcceptedChunkCountTotal}; duplicate_or_stale_chunk_count_total={context.PullReceiverDuplicateOrStaleChunkCountTotal}; repair_overlap_chunk_count_total={context.PullReceiverRepairOverlapChunkCountTotal}; repair_accepted_chunk_count_total={context.PullReceiverRepairAcceptedChunkCountTotal}; repair_duplicate_or_stale_chunk_count_total={context.PullReceiverRepairDuplicateOrStaleChunkCountTotal}; repair_duplicate_or_stale_raw_bytes_total={context.PullReceiverRepairDuplicateOrStaleRawBytesTotal}; sparse_write_batch_count_total={context.PullReceiverSparseWriteBatchCountTotal}; sparse_write_duration_ms_total={context.PullReceiverSparseWriteDurationMsTotal}; receiver_state_sent_total={context.PullV4StateSentCountTotal}; repair_request_count_total={context.PullV4RepairRequestCountTotal}; repair_requested_chunk_count_total={context.PullV4RepairRequestedChunkCountTotal}; repair_suppressed_count_total={context.PullV4RepairSuppressedCountTotal}; frontier_tail_repair_request_count_total={context.PullV4FrontierTailRepairRequestCountTotal}; frontier_stall_suppressed_count_total={context.V4FrontierStallSuppressedCountTotal}; pending_repair_request_count={context.V4ReceiverRepairRequests.Count}; pending_write_chunk_count={context.ReceiverSparseChunksPendingWrite.Count}; pending_bytes={context.BufferedBytes}");
    }

    private async Task FailOutboundV4Async(
        OutboundTransferContext context,
        IFileTransferDataSession? dataSession,
        string errorCode,
        string statusMessage,
        bool notifyPeer)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }
        }

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

    private async Task<bool> SendOutboundV4TransportPauseControlAsync(
        OutboundTransferContext context,
        bool paused,
        string reason)
    {
        FileTransferPauseControlFrameV4? frame;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal)
            {
                return false;
            }

            frame = CreateOutboundV4TransportPauseControlLocked(context, paused, reason);
        }

        return await SendV4PauseControlAsync(frame, reason, FileTransferDirection.Outbound, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> SendInboundV4TransportPauseControlAsync(
        InboundTransferContext context,
        bool paused,
        string reason)
    {
        FileTransferPauseControlFrameV4? frame;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal)
            {
                return false;
            }

            frame = CreateInboundV4TransportPauseControlLocked(context, paused, reason);
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

    private void ScheduleOutboundV4TransportPauseControlRetry(
        OutboundTransferContext context,
        bool paused,
        string reason,
        bool localTunaActivationBarrier = false)
    {
        if (ShouldSuppressRegularNknV4TunaActivationTransportPauseControl(
                context.RouteRuntime,
                reason,
                localTunaActivationBarrier))
        {
            LogTunaActivationTransportPauseControlSuppressed(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                paused,
                reason);
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_pause_control_retry_scheduled; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt_count={PauseControlRetryDelaysMs.Length}");
        _ = Task.Run(
            () => RunOutboundV4TransportPauseControlRetryLoopAsync(context, paused, reason),
            CancellationToken.None);
    }

    private void ScheduleInboundV4TransportPauseControlRetry(
        InboundTransferContext context,
        bool paused,
        string reason,
        bool localTunaActivationBarrier = false)
    {
        if (ShouldSuppressRegularNknV4TunaActivationTransportPauseControl(
                context.RouteRuntime,
                reason,
                localTunaActivationBarrier))
        {
            LogTunaActivationTransportPauseControlSuppressed(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                paused,
                reason);
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_pause_control_retry_scheduled; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; attempt_count={PauseControlRetryDelaysMs.Length}");
        _ = Task.Run(
            () => RunInboundV4TransportPauseControlRetryLoopAsync(context, paused, reason),
            CancellationToken.None);
    }

    private static bool ShouldSuppressRegularNknV4TunaActivationTransportPauseControl(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        string reason,
        bool localTunaActivationBarrier)
        => routeRuntime.UsesRegularNknV4FastRuntime &&
           (localTunaActivationBarrier ||
            IsTunaActivationNegotiationTransportPauseReason(reason));

    private static void LogTunaActivationTransportPauseControlSuppressed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int protocolVersion,
        bool paused,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_tuna_activation_transport_pause_control_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; paused={(paused ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; route={routeRuntime.TelemetryToken}; protocol_version={protocolVersion}; scope=local_regular_v4_activation_barrier");

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

    private async Task RunOutboundV4TransportPauseControlRetryLoopAsync(OutboundTransferContext context, bool paused, string reason)
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

                if (!ShouldContinueOutboundV4TransportPauseControlRetry(context, paused))
                {
                    LogPauseControlRetryStopped(FileTransferDirection.Outbound, context.TransferId, context.SessionId, paused, reason, index + 1, "state_changed_or_terminal");
                    return;
                }

                if (await SendOutboundV4TransportPauseControlAsync(context, paused, reason).ConfigureAwait(false))
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

    private async Task RunInboundV4TransportPauseControlRetryLoopAsync(InboundTransferContext context, bool paused, string reason)
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

                if (!ShouldContinueInboundV4TransportPauseControlRetry(context, paused))
                {
                    LogPauseControlRetryStopped(FileTransferDirection.Inbound, context.TransferId, context.SessionId, paused, reason, index + 1, "state_changed_or_terminal");
                    return;
                }

                if (await SendInboundV4TransportPauseControlAsync(context, paused, reason).ConfigureAwait(false))
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

    private bool ShouldContinueOutboundV4TransportPauseControlRetry(OutboundTransferContext context, bool paused)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                string.IsNullOrWhiteSpace(context.SessionId))
            {
                return false;
            }

            return paused
                ? context.PullTransportPaused &&
                  IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason)
                : !context.PullTransportPaused;
        }
    }

    private bool ShouldContinueInboundV4TransportPauseControlRetry(InboundTransferContext context, bool paused)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                string.IsNullOrWhiteSpace(context.SessionId))
            {
                return false;
            }

            return paused
                ? context.PullTransportPaused &&
                  IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason)
                : !context.PullTransportPaused;
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

        var unresolvedV6Epoch = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
            ? context.V6TransportEpoch
            : null;
        if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff) ||
            unresolvedV6Epoch is not null ||
            context.PullPostTunaRecoveryActive)
        {
            var transportEpoch = unresolvedV6Epoch?.EpochId ??
                                 context.V6TransportHandoff?.EpochId ??
                                 context.PullTransportRebindGeneration;
            var handoffState = unresolvedV6Epoch is not null
                ? FormatV6TransportEpochState(unresolvedV6Epoch.State)
                : FormatV6TransportHandoffState(context.V6TransportHandoff?.State ?? V6TransportHandoffState.FrontierRepairOnly);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_sender_resume_rewind_suppressed_for_v6_handoff; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; transport_epoch={transportEpoch}; handoff_state={handoffState}; handoff_kind={FormatFileTransferTransportHandoffKind(unresolvedV6Epoch?.Kind ?? context.V6TransportHandoff?.Kind ?? FileTransferTransportHandoffKind.None)}; target_transport={FormatFileTransferTransportKind(unresolvedV6Epoch?.TargetTransport ?? context.V6TransportHandoff?.TargetTransport ?? FileTransferTransportKind.Unknown)}; post_tuna_recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}; source_can_seek={(context.PullSourceCanSeek ? 1 : 0)}");
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
        var allowFileTunaV4PostTunaReplay =
            context.RouteRuntime.UsesFileTunaV4Runtime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V4;
        var allowPostTunaFallbackV6Repair =
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
            context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            (!allowFileTunaV4PostTunaReplay &&
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6) ||
            !context.PullSourceCanSeek ||
            recoveryGeneration <= 0 ||
            (!context.PullPostTunaRecoveryActive && recoveryGeneration <= context.LastRecoveredV6TransportHandoffEpoch))
        {
            return;
        }

        if (IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            if (!IsCurrentPostTunaFallbackTailReconciliationCheckpointPending(context.CurrentTransferLeg))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; skip_reason=fallback_checkpoint_pending; checkpoint_request_id={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)")}");
                return;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_safety_replay_allowed_for_tail_reconciliation; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)")}; checkpoint_priority={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointPriority ?? "(none)")}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}");
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

        var replayStartChunkIndex = remoteFrontier;
        var postTunaFallbackPeerSilenceSweep = allowPostTunaFallbackV6Repair &&
            !IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
            context.PullTransportFrontierOnlyRepairActive &&
            remoteFrontier < context.ChunkCount &&
            (string.Equals(reason, "post_fallback_sender_wait", StringComparison.Ordinal) ||
             string.Equals(reason, "post_tuna_fallback_peer_silence", StringComparison.Ordinal) ||
             string.Equals(reason, "post_tuna_fallback_state_refresh_send_timeout", StringComparison.Ordinal) ||
             string.Equals(reason, "post_tuna_fallback_state_refresh_send_failed", StringComparison.Ordinal));
        var fallbackFrontierSweep = (allowFileTunaV4PostTunaReplay || postTunaFallbackPeerSilenceSweep) &&
            context.PullTransportFrontierOnlyRepairActive &&
            remoteFrontier < context.ChunkCount;
        var postTunaFallbackSweepWrappedToFrontier = false;
        if (fallbackFrontierSweep &&
            context.PullTransportLastSafetyReplayGeneration == recoveryGeneration &&
            context.PullTransportLastSafetyReplayFrontierChunkIndex == remoteFrontier &&
            context.PullTransportLastSafetyReplayEndChunkIndex > remoteFrontier)
        {
            var sweepLimitExclusive = Math.Clamp(
                Math.Max(context.ChunksAcceptedForTransport, context.RemoteGrantedUntilExclusive),
                remoteFrontier,
                context.ChunkCount);
            if (context.PullTransportLastSafetyReplayEndChunkIndex < sweepLimitExclusive)
            {
                replayStartChunkIndex = context.PullTransportLastSafetyReplayEndChunkIndex;
            }
            else if (postTunaFallbackPeerSilenceSweep)
            {
                replayStartChunkIndex = remoteFrontier;
                postTunaFallbackSweepWrappedToFrontier = true;
            }
        }

        var grantedUntilExclusive = Math.Clamp(context.RemoteGrantedUntilExclusive, replayStartChunkIndex, context.ChunkCount);
        var emergencyCreditRepair = false;
        var frontierOnlyReplay = context.PullTransportFrontierOnlyRepairActive &&
            remoteFrontier < context.ChunkCount;
        if (grantedUntilExclusive <= replayStartChunkIndex)
        {
            emergencyCreditRepair = true;
            grantedUntilExclusive = Math.Min(
                context.ChunkCount,
                replayStartChunkIndex + (fallbackFrontierSweep ? PullTransportRebindSafetyReplayMaxChunks : V4PostFallbackEmergencyFrontierRepairChunks));
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_emergency_frontier_replay; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; emergency_end_chunk_exclusive={grantedUntilExclusive}");
        }
        else if (frontierOnlyReplay)
        {
            var previousReplayEndExclusive = grantedUntilExclusive;
            grantedUntilExclusive = Math.Min(
                context.ChunkCount,
                replayStartChunkIndex + (fallbackFrontierSweep ? PullTransportRebindSafetyReplayMaxChunks : V4PostFallbackEmergencyFrontierRepairChunks));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_frontier_only_replay; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; previous_replay_end_chunk_exclusive={previousReplayEndExclusive}; replay_end_chunk_exclusive={grantedUntilExclusive}; frontier_only_start_chunk_index={context.PullTransportFrontierOnlyRepairStartChunkIndex}; file_tuna_v4_frontier_sweep={(allowFileTunaV4PostTunaReplay && fallbackFrontierSweep ? 1 : 0)}; post_tuna_v6_frontier_sweep={(allowPostTunaFallbackV6Repair && fallbackFrontierSweep ? 1 : 0)}; post_tuna_v6_frontier_sweep_wrapped={(postTunaFallbackSweepWrappedToFrontier ? 1 : 0)}");
        }
        else if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff))
        {
            var previousReplayEndExclusive = grantedUntilExclusive;
            grantedUntilExclusive = Math.Min(context.ChunkCount, replayStartChunkIndex + V4PostFallbackEmergencyFrontierRepairChunks);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V6TransportHandoff!.EpochId}; state={FormatV6TransportHandoffState(context.V6TransportHandoff.State)}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; previous_replay_end_chunk_exclusive={previousReplayEndExclusive}; replay_end_chunk_exclusive={grantedUntilExclusive}");
        }

        var maxChunksByBytes = Math.Max(1, PullTransportRebindSafetyReplayMaxBytes / Math.Max(1, context.ChunkSizeBytes));
        var replayChunkCount = Math.Min(
            grantedUntilExclusive - replayStartChunkIndex,
            Math.Min(PullTransportRebindSafetyReplayMaxChunks, maxChunksByBytes));
        if (replayChunkCount <= 0)
        {
            return;
        }

        var replayEndExclusive = replayStartChunkIndex + replayChunkCount;
        var chunkIndices = new List<int>(replayChunkCount);
        for (var chunkIndex = replayStartChunkIndex; chunkIndex < replayEndExclusive; chunkIndex++)
        {
            if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
            {
                chunkIndices.Add(chunkIndex);
            }
        }

        if (chunkIndices.Count == 0)
        {
            if (TryClearStalePostTunaFallbackTransportReplayLocked(
                    context,
                    reason,
                    recoveryGeneration,
                    remoteFrontier,
                    replayStartChunkIndex,
                    replayEndExclusive,
                    out var staleReplayClearedChunkCount,
                    out var staleReplayClearedItemCount))
            {
                for (var chunkIndex = replayStartChunkIndex; chunkIndex < replayEndExclusive; chunkIndex++)
                {
                    if (context.PullV4SenderPumpRepairQueuedChunkIndices.Add(chunkIndex))
                    {
                        chunkIndices.Add(chunkIndex);
                    }
                }

                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_post_tuna_fallback_stale_rebind_replay_reset; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; replay_end_chunk_exclusive={replayEndExclusive}; cleared_repair_item_count={staleReplayClearedItemCount}; cleared_repair_chunk_count={staleReplayClearedChunkCount}; rescheduled_chunk_count={chunkIndices.Count}");
            }

        }

        if (chunkIndices.Count == 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_transport_rebind_safety_replay_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; skip_reason=chunks_already_queued; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; replay_end_chunk_exclusive={replayEndExclusive}");
            return;
        }

        context.PullTransportLastSafetyReplayGeneration = recoveryGeneration;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = remoteFrontier;
        context.PullTransportLastSafetyReplayEndChunkIndex = replayEndExclusive;
        context.PullTransportLastSafetyReplayUtc = now;
        var repairKey = $"transport_rebind_safety_replay:{recoveryGeneration}:{remoteFrontier}:{replayStartChunkIndex}:{chunkIndices.Count}";
        MarkOutboundV6EpochRepairRequestPendingLocked(
            context,
            recoveryGeneration,
            repairKey,
            "transport_rebind_safety_replay");
        context.PullV4SenderPumpRepairQueue.Enqueue(
            new PullV4QueuedRepairSend(
                chunkIndices,
                RangeCount: 1,
                RequestedChunkCount: replayChunkCount,
                FirstStartChunkIndex: replayStartChunkIndex,
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
        context.SparseSenderPumpLastWakeReason = "transport_rebind_safety_replay";
        context.SignalSparseSenderPump();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_safety_replay_started; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={recoveryGeneration}; remote_next_expected_chunk_index={remoteFrontier}; replay_start_chunk_index={replayStartChunkIndex}; replay_end_chunk_exclusive={replayEndExclusive}; requested_chunk_count={replayChunkCount}; scheduled_chunk_count={chunkIndices.Count}; replay_byte_cap={PullTransportRebindSafetyReplayMaxBytes}; replay_chunk_cap={PullTransportRebindSafetyReplayMaxChunks}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; file_tuna_v4_frontier_sweep={(allowFileTunaV4PostTunaReplay && fallbackFrontierSweep ? 1 : 0)}; post_tuna_v6_frontier_sweep={(allowPostTunaFallbackV6Repair && fallbackFrontierSweep ? 1 : 0)}; post_tuna_v6_frontier_sweep_wrapped={(postTunaFallbackSweepWrappedToFrontier ? 1 : 0)}");
    }

    private static bool TryClearStalePostTunaFallbackTransportReplayLocked(
        OutboundTransferContext context,
        string reason,
        long recoveryGeneration,
        int remoteFrontier,
        int replayStartChunkIndex,
        int replayEndExclusive,
        out int clearedChunkCount,
        out int clearedItemCount)
    {
        clearedChunkCount = 0;
        clearedItemCount = 0;
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            !context.PullPostTunaRecoveryActive ||
            !context.PullTransportFrontierOnlyRepairActive ||
            recoveryGeneration <= 0 ||
            remoteFrontier < 0 ||
            remoteFrontier >= context.ChunkCount ||
            replayStartChunkIndex < remoteFrontier ||
            replayEndExclusive <= replayStartChunkIndex ||
            context.PullTransportLastSafetyReplayGeneration != recoveryGeneration ||
            context.PullTransportLastSafetyReplayFrontierChunkIndex != remoteFrontier)
        {
            return false;
        }

        if (!IsPostTunaFallbackPeerSilenceReplayReason(reason))
        {
            return false;
        }

        var retained = new Queue<PullV4QueuedRepairSend>(context.PullV4SenderPumpRepairQueue.Count);
        while (context.PullV4SenderPumpRepairQueue.Count > 0)
        {
            var queuedRepair = context.PullV4SenderPumpRepairQueue.Dequeue();
            if (!queuedRepair.RepairRequestKey.StartsWith("transport_rebind_safety_replay:", StringComparison.Ordinal))
            {
                retained.Enqueue(queuedRepair);
                continue;
            }

            var overlapsReplayRange = queuedRepair.ChunkIndices.Any(chunkIndex =>
                chunkIndex >= replayStartChunkIndex &&
                chunkIndex < replayEndExclusive);
            if (!overlapsReplayRange)
            {
                retained.Enqueue(queuedRepair);
                continue;
            }

            clearedItemCount++;
            clearedChunkCount += queuedRepair.ChunkIndices.Count;
            foreach (var chunkIndex in queuedRepair.ChunkIndices)
            {
                context.PullV4SenderPumpRepairQueuedChunkIndices.Remove(chunkIndex);
            }

            if (context.PullV4SenderPumpRepairRequests.TryGetValue(queuedRepair.RepairRequestKey, out var repairState))
            {
                repairState.Queued = false;
                repairState.InFlight = false;
                repairState.SuppressedCount++;
            }
        }

        while (retained.Count > 0)
        {
            context.PullV4SenderPumpRepairQueue.Enqueue(retained.Dequeue());
        }

        clearedChunkCount += context.PullV4SenderPumpRepairQueuedChunkIndices.RemoveWhere(
            chunkIndex => chunkIndex >= replayStartChunkIndex && chunkIndex < replayEndExclusive);

        return clearedChunkCount > 0 || clearedItemCount > 0;
    }

    private static bool IsPostTunaFallbackPeerSilenceReplayReason(string reason)
        => string.Equals(reason, "post_fallback_sender_wait", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_peer_silence", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_state_refresh_send_timeout", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_state_refresh_send_failed", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_stale_inflight_repair_stalled", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_stale_inflight_repair_failed", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_stale_credit_repair_stalled", StringComparison.Ordinal) ||
           string.Equals(reason, "post_tuna_fallback_stale_credit_repair_failed", StringComparison.Ordinal);

    private static void MarkOutboundV6EpochRepairRequestPendingLocked(
        OutboundTransferContext context,
        long transportEpoch,
        string? repairRequestId,
        string source)
    {
        if (transportEpoch <= 0 ||
            string.IsNullOrWhiteSpace(repairRequestId) ||
            !IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
            context.V6TransportEpoch!.EpochId != transportEpoch)
        {
            return;
        }

        var trimmed = repairRequestId.Trim();
        var previousLastRepairRequestId = context.V6TransportEpoch.LastRepairRequestId;
        context.V6TransportEpoch.LastRepairRequestId = trimmed;
        var added = context.V6PendingEpochRepairRequestIds.Add(trimmed);
        if (added ||
            !string.Equals(previousLastRepairRequestId, trimmed, StringComparison.Ordinal))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_repair_request_registered; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; repair_request_id={FormatProtocolLogValue(trimmed)}; source={FormatProtocolLogValue(source)}; previous_last_repair_request_id={FormatProtocolLogValue(previousLastRepairRequestId ?? "(none)")}");
        }
    }

    private static FileTransferStateFrameV4 CreateOutboundV4PauseStateLocked(OutboundTransferContext context, string reason)
    {
        var committed = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        var bytesCommitted = context.ChunkCount > 0 && committed >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, Math.Max(0L, (long)committed * Math.Max(1, context.ChunkSizeBytes)));
        context.V4PauseControlEpoch++;
        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferStateFrameV4
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
        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferPauseControlFrameV4
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V4PauseControlEpoch,
                Paused = context.UserPaused,
                Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
            };
        }

        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = context.UserPaused,
            Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
        };
    }

    private static FileTransferPauseControlFrameV4 CreateOutboundV4TransportPauseControlLocked(
        OutboundTransferContext context,
        bool paused,
        string reason)
    {
        context.V4PauseControlEpoch++;
        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferPauseControlFrameV4
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V4PauseControlEpoch,
                Paused = paused,
                Reason = reason,
            };
        }

        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = paused,
            Reason = reason,
        };
    }

    private static FileTransferPauseControlFrameV4 CreateInboundV4PauseControlLocked(InboundTransferContext context, string reason)
    {
        context.V4PauseControlEpoch++;
        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferPauseControlFrameV4
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V4PauseControlEpoch,
                Paused = context.UserPaused,
                Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
            };
        }

        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = context.UserPaused,
            Reason = context.UserPauseReason ?? (!context.UserPaused ? reason : null),
        };
    }

    private static FileTransferPauseControlFrameV4 CreateInboundV4TransportPauseControlLocked(
        InboundTransferContext context,
        bool paused,
        string reason)
    {
        context.V4PauseControlEpoch++;
        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferPauseControlFrameV4
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V4PauseControlEpoch,
                Paused = paused,
                Reason = reason,
            };
        }

        return new FileTransferPauseControlFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4PauseControlEpoch,
            Paused = paused,
            Reason = reason,
        };
    }

    private async Task RunInboundSparseCreditReceiveLoopAsync(
        InboundTransferContext context,
        FileTransferSessionOpenV2 sessionOpen,
        FileTransferSparseCreditRuntimeKind runtimeKind)
    {
        var isPrimaryRegularNknBulkV6 = IsPrimaryRegularNknBulkV6Runtime(runtimeKind);
        var useV6Envelope = context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6;
        if (isPrimaryRegularNknBulkV6)
        {
            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Opening, "receiver_start");
            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.AwaitingManifest, "session_open");
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={(useV6Envelope ? "filetransfer_v6_receiver_started" : "filetransfer_v4_receiver_started")}; transfer_id={context.TransferId}; session_id={context.SessionId}; protocol_version={context.NegotiatedDataProtocolVersion}; route={context.RouteSelection.TelemetryToken}; runtime_profile={FormatFileTransferRouteRuntimeProfile(context.RouteSelection.RuntimeProfile)}; frame_family={FormatFileTransferFrameFamily(context.RouteSelection.FrameFamily)}; bridge_recovery_policy={FormatFileTransferRouteBridgeRecoveryPolicy(context.RouteSelection.BridgeRecoveryPolicy)}; session_open_chunk_size_bytes={sessionOpen.ChunkSizeBytes}; session_open_pipeline_depth={sessionOpen.InitialPipelineDepth}");
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

                    await MaybeSendInboundSparseCreditFrontierStallStateAsync(context).ConfigureAwait(false);
                    continue;
                }

                var received = await pendingReceiveTask.ConfigureAwait(false);
                var frame = received.Frame;
                pendingReceiveTask = null;
                if (!IsFrameForContext(context, frame))
                {
                    LogInboundV4FrameIgnored(context, frame, "session_or_transfer_mismatch");
                    continue;
                }

                if (!ShouldAcceptSparseCreditRuntimeDataFrame(context, frame))
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

                    if (frame is FileTransferChunkBatchFrameV4 legacyPostTunaBatch and not FileTransferChunkBatchFrameV6 &&
                        ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context))
                    {
                        await HandleInboundV4ChunkBatchAsync(context, legacyPostTunaBatch, received.TransportKind).ConfigureAwait(false);
                        continue;
                    }

                    LogInboundV4FrameIgnored(context, frame, useV6Envelope ? "protocol_not_v6" : "protocol_not_v4");
                    continue;
                }

                switch (frame)
                {
                    case FileTransferTransportEpochFrameV6 handoff:
                        MarkInboundV4PeerFrameReceived(context);
                        ApplyInboundV6HandoffFrame(context, handoff);
                        await SendInboundV6TransportHandoffAsync(context, "peer_handoff").ConfigureAwait(false);
                        break;
                    case FileTransferFrontierRequestFrameV6 repairRequest:
                        MarkInboundV4PeerFrameReceived(context);
                        if (isPrimaryRegularNknBulkV6 &&
                            IsPrimaryRegularNknBulkV6CheckpointSyncRequest(repairRequest))
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.CheckpointSyncRequested, "checkpoint_sync_received");
                            lock (gate)
                            {
                                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                                {
                                    context.V6RegularNknLastCheckpointSyncRequestId = repairRequest.RepairRequestId;
                                }
                            }

                            LocalOperationalLog.Info(
                                "FileTransferService",
                                $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_received; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(repairRequest.RepairRequestId)}; transport_epoch={repairRequest.TransportEpoch}; recovery_mode={FormatProtocolLogValue(repairRequest.RecoveryMode)}; rebind_generation={context.PullTransportRebindGeneration}");
                            var sent = await SendInboundSparseCreditStateAsync(
                                context,
                                V6RegularNknCheckpointSyncRecoveryMode,
                                terminalReady: false,
                                forceSend: true).ConfigureAwait(false);
                            LocalOperationalLog.Info(
                                "FileTransferService",
                                $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_response_queued; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(repairRequest.RepairRequestId)}; sent={(sent ? 1 : 0)}; rebind_generation={context.PullTransportRebindGeneration}");
                        }
                        else if (IsV6RegularNknStateRefreshRequest(repairRequest) &&
                            (ShouldUseV6RegularNknSparseRuntime(context) ||
                             IsInboundPostTunaRecoveryActiveLocked(context) &&
                             ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context)))
                        {
                            if (isPrimaryRegularNknBulkV6)
                            {
                                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.StateRefreshRequested, "state_refresh_received");
                            }

                            LocalOperationalLog.Info(
                                "FileTransferService",
                                $"event=filetransfer_v6_regular_nkn_state_refresh_received; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(repairRequest.RepairRequestId)}; transport_epoch={repairRequest.TransportEpoch}; recovery_mode={FormatProtocolLogValue(repairRequest.RecoveryMode)}");
                            lock (gate)
                            {
                                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                                {
                                    MarkInboundFallbackCheckpointRequestedLocked(context, repairRequest, "state_refresh_received");
                                }
                            }

                            var sent = await SendInboundSparseCreditStateAsync(
                                context,
                                V6RegularNknStateRefreshRecoveryMode,
                                terminalReady: false,
                                forceSend: true).ConfigureAwait(false);
                            LocalOperationalLog.Info(
                                "FileTransferService",
                                $"event=filetransfer_v6_regular_nkn_state_refresh_state_resent; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(repairRequest.RepairRequestId)}; sent={(sent ? 1 : 0)}");
                        }
                        else
                        {
                            LogInboundV4FrameIgnored(context, repairRequest, "unexpected_inbound_repair_request_v6");
                        }
                        break;
                    case FileTransferRepairProofFrameV6 repairProof:
                        MarkInboundV4PeerFrameReceived(context);
                        ApplyInboundV6RepairProof(context, repairProof);
                        break;
                    case FileTransferTransportProbeFrameV6 probe:
                        MarkInboundV4PeerFrameReceived(context);
                        await HandleReceivedV6TransportProbeFrameAsync(
                            context.SessionId,
                            context.TransferId,
                            FileTransferDirection.Inbound,
                            probe,
                            received.TransportKind).ConfigureAwait(false);
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

                        await SendInboundSparseCreditStateAsync(context, "manifest_received", terminalReady: false, forceSend: ShouldUsePostTunaFallbackV6FeedbackEnvelope(context)).ConfigureAwait(false);
                        if (ShouldUsePostTunaFallbackV6FeedbackEnvelope(context))
                        {
                            await SendInboundV6FrontierRequestAsync(context, "manifest_received", forceSend: true).ConfigureAwait(false);
                        }
                        if (isPrimaryRegularNknBulkV6)
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.CreditGranted, "manifest_received");
                        }

                        break;
                    case FileTransferChunkBatchFrameV6 batch when ShouldUsePostTunaFallbackV6SparseRuntimeLocked(context):
                        MarkInboundV4PeerFrameReceived(context);
                        await HandleInboundV6ChunkBatchAsync(context, batch, received.TransportKind).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV4 batch:
                        if (isPrimaryRegularNknBulkV6)
                        {
                            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.ReceivingBulk, "chunk_batch_received");
                        }

                        await HandleInboundV4ChunkBatchAsync(context, batch, received.TransportKind).ConfigureAwait(false);
                        if (isPrimaryRegularNknBulkV6)
                        {
                            var completedAfterBatch = false;
                            lock (gate)
                            {
                                completedAfterBatch = ReferenceEquals(inboundTransfer, context) &&
                                                      context.IsTerminal &&
                                                      context.State == FileTransferTransferState.Completed;
                            }

                            if (completedAfterBatch)
                            {
                                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Completed, "chunk_batch_completed");
                            }
                        }

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
                        LogInboundV4FrameIgnored(context, frame, "unexpected_inbound_frame_v4");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Cancelled, "lifetime_cancelled");
            }
        }
        catch (Exception ex)
        {
            if (isPrimaryRegularNknBulkV6)
            {
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Failed, "exception");
            }

            await FailInboundV4Async(
                context,
                InvalidStateErrorCode,
                ex.Message,
                isPrimaryRegularNknBulkV6 ? "Regular NKN bulk V6 receive loop failed." : "V4 receive loop failed.").ConfigureAwait(false);
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

    private void ApplyInboundV6HandoffFrame(InboundTransferContext context, FileTransferTransportEpochFrameV6 handoff)
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

            if (handoff.TransportEpoch <= context.LastRecoveredV6TransportHandoffEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=recovered_epoch; frame_transport_epoch={handoff.TransportEpoch}; last_recovered_transport_epoch={context.LastRecoveredV6TransportHandoffEpoch}");
                return;
            }

            if (context.V6TransportHandoff is null)
            {
                context.PullTransportRebindGeneration = Math.Max(context.PullTransportRebindGeneration, (int)Math.Min(int.MaxValue, handoff.TransportEpoch));
                context.V6TransportHandoff = new TransportHandoffEpoch
                {
                    EpochId = handoff.TransportEpoch,
                    Reason = "peer_handoff",
                    StartedUtc = DateTimeOffset.UtcNow,
                    StartingCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
                    StartingHighestObservedChunkIndex = context.PullHighestReceivedChunkIndex,
                    LastObservedCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
                    LastObservedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
                    State = V6TransportHandoffState.NknProofPending,
                };
                LogV6TransportHandoffEpochStarted(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.V6TransportHandoff);
            }

            if (context.V6TransportHandoff.EpochId != handoff.TransportEpoch)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_recovery_frame_ignored; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}; reason=stale_or_mismatched_epoch; frame_transport_epoch={handoff.TransportEpoch}; current_transport_epoch={context.V6TransportHandoff.EpochId}");
                return;
            }

            TrySetV6TransportHandoffState(
                context.V6TransportHandoff,
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                V6TransportHandoffState.FrontierRepairOnly,
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

    private void ApplyInboundV6RepairProof(InboundTransferContext context, FileTransferRepairProofFrameV6 repairProof)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                repairProof.TransportEpoch <= context.LastRecoveredV6TransportHandoffEpoch ||
                context.V6TransportHandoff is null ||
                context.V6TransportHandoff.EpochId != repairProof.TransportEpoch)
            {
                return;
            }

            TrySetV6TransportHandoffState(
                context.V6TransportHandoff,
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                repairProof.CommittedChunkIndex > context.V6TransportHandoff.StartingCommittedChunkIndex
                    ? V6TransportHandoffState.BackfillRepair
                    : V6TransportHandoffState.FrontierRepairOnly,
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
                await MaybeSendInboundSparseCreditFrontierStallStateAsync(context).ConfigureAwait(false);
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
            reason: $"protocol_version={FileTransferProtocol.ProtocolVersionV4}; chunk_count={manifest.ChunkCount}; chunk_size_bytes={manifest.ChunkSizeBytes}");
        return true;
    }

    private async Task HandleInboundV4ChunkBatchAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        FileTransferTransportKind receivedTransportKind)
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
        var batchRawBytes = chunks.Sum(static item => item.ChunkBytes.Length);
        var acceptedRawBytes = 0L;
        var duplicateOrStaleChunkCount = 0;
        var duplicateOrStaleRawBytes = 0L;
        var repairDuplicateOrStaleRawBytes = 0L;
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
                    $"event=filetransfer_v4_chunk_batch_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={(context.UserPaused ? "user_paused" : "peer_paused")}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; raw_bytes={batchRawBytes}");
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
                    duplicateOrStaleChunkCount++;
                    duplicateOrStaleRawBytes += chunkBytes.Length;
                    if (overlapsActiveRepair && chunkIndex == repairFrontierBefore)
                    {
                        repairFrontierChunkObserved = true;
                        repairFrontierChunkStatus = "stale_or_already_committed";
                    }

                    if (overlapsActiveRepair)
                    {
                        repairDuplicateOrStaleChunkCount++;
                        repairDuplicateOrStaleRawBytes += chunkBytes.Length;
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
                    duplicateOrStaleChunkCount++;
                    duplicateOrStaleRawBytes += chunkBytes.Length;
                    if (overlapsActiveRepair && chunkIndex == repairFrontierBefore)
                    {
                        repairFrontierChunkObserved = true;
                        repairFrontierChunkStatus = pendingWrite ? "pending_write" : "duplicate_written";
                    }

                    if (overlapsActiveRepair)
                    {
                        repairDuplicateOrStaleChunkCount++;
                        repairDuplicateOrStaleRawBytes += chunkBytes.Length;
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
                acceptedRawBytes += chunkBytes.Length;
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

            context.PullReceiverRawBatchBytesTotal += batchRawBytes;
            context.PullReceiverRawBatchFramesTotal++;
            context.PullReceiverChunkCountTotal += chunks.Count;
            context.PullReceiverAcceptedRawBytesTotal += acceptedRawBytes;
            context.PullReceiverAcceptedChunkCountTotal += acceptedChunks.Count;
            context.PullReceiverDuplicateOrStaleRawBytesTotal += duplicateOrStaleRawBytes;
            context.PullReceiverDuplicateOrStaleChunkCountTotal += duplicateOrStaleChunkCount;
            context.PullReceiverRepairOverlapChunkCountTotal += repairOverlapChunkCount;
            context.PullReceiverRepairAcceptedChunkCountTotal += repairAcceptedChunkCount;
            context.PullReceiverRepairDuplicateOrStaleChunkCountTotal += repairDuplicateOrStaleChunkCount;
            context.PullReceiverRepairDuplicateOrStaleRawBytesTotal += repairDuplicateOrStaleRawBytes;
        }

        if (repairOverlapChunkCount > 0 || acceptedChunks.Count == 0 || FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_chunk_batch_received; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; accepted_chunk_count={acceptedChunks.Count}; raw_bytes={batchRawBytes}");
        }
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
                LogInboundV6FrontierRepairStillMissing(
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

            await MaybeSendInboundSparseCreditFrontierStallStateAsync(context).ConfigureAwait(false);
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
        string? primaryRegularNknFrontierRepairTransactionObservedId = null;
        bool primaryRegularNknFrontierRepairTransactionMatchedActive = false;
        int primaryRegularNknFrontierRepairTransactionObservedCount = 0;
        long primaryRegularNknFrontierRepairTransactionRequestToObserveMs = -1;
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
            context.PullReceiverSparseWriteBatchCountTotal++;
            context.PullReceiverSparseWriteDurationMsTotal += writeStopwatch.ElapsedMilliseconds;
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

            if (context.V6TransportHandoff is { } handoff &&
                batch is FileTransferChunkBatchFrameV6 handoffBatch &&
                (handoffBatch.TransportEpoch == 0 || handoffBatch.TransportEpoch == handoff.EpochId) &&
                (acceptedChunks.Count > 0 || repairOverlapChunkCount > 0))
            {
                var nextState = context.NextChunkIndex > handoff.StartingCommittedChunkIndex &&
                                context.NextChunkIndex > repairFrontierBefore
                    ? V6TransportHandoffState.BackfillRepair
                    : V6TransportHandoffState.FrontierRepairOnly;
                TrySetV6TransportHandoffState(
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
                        RepairRequestId = handoffBatch.RepairRequestId ?? handoff.LastRepairRequestId,
                        AppliedChunkCount = acceptedChunks.Count,
                        CommittedChunkIndex = context.NextChunkIndex,
                        RecoveryMode = FormatV6TransportHandoffState(nextState),
                    };
                }
            }

            if (batch is FileTransferChunkBatchFrameV6 transactionBatch &&
                ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context) &&
                IsPrimaryRegularNknFrontierRepairTransactionId(transactionBatch.RepairRequestId))
            {
                var now = DateTimeOffset.UtcNow;
                primaryRegularNknFrontierRepairTransactionObservedId = transactionBatch.RepairRequestId;
                primaryRegularNknFrontierRepairTransactionMatchedActive = string.Equals(
                    transactionBatch.RepairRequestId,
                    context.V6RegularNknFrontierRepairTransactionId,
                    StringComparison.Ordinal);
                if (primaryRegularNknFrontierRepairTransactionMatchedActive)
                {
                    context.V6RegularNknFrontierRepairTransactionLastObservedUtc = now;
                    context.V6RegularNknFrontierRepairTransactionObservedCount++;
                    primaryRegularNknFrontierRepairTransactionObservedCount = context.V6RegularNknFrontierRepairTransactionObservedCount;
                    primaryRegularNknFrontierRepairTransactionRequestToObserveMs =
                        context.V6RegularNknFrontierRepairTransactionStartedUtc is null
                            ? -1
                            : (long)Math.Max(0, (now - context.V6RegularNknFrontierRepairTransactionStartedUtc.Value).TotalMilliseconds);
                }
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            nextChunkIndexAfterCommit = context.NextChunkIndex;
            bytesCommittedAfterCommit = context.BytesTransferred;
            pendingChunkCountAfterCommit = GetReceiverPendingChunkCountLocked(context);
            pendingBytesAfterCommit = context.BufferedBytes;
            highestReceivedChunkIndexAfterCommit = context.PullHighestReceivedChunkIndex;
            lateArrivalDistanceAfterCommit = context.PullLateArrivalDistance;
            ClearInboundPrimaryRegularNknFrontierRepairTransactionAfterProgressLocked(
                context,
                repairFrontierBefore,
                nextChunkIndexAfterCommit);
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
        if (FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_sparse_write_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; written_chunk_count={acceptedChunks.Count}; written_bytes={sparseWriteBytes}; contiguous_chunks_committed={committedChunkCount}; contiguous_bytes_committed={committedByteCount}; write_duration_ms={writeStopwatch.ElapsedMilliseconds}; next_chunk_index={nextChunkIndexAfterCommit}; highest_received_chunk_index={highestReceivedChunkIndexAfterCommit}; pending_bytes={pendingBytesAfterCommit}");
        }
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
                LogInboundV6FrontierRepairStillMissing(
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

        if (primaryRegularNknFrontierRepairTransactionObservedId is not null)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_observed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(primaryRegularNknFrontierRepairTransactionObservedId)}; matched_active={(primaryRegularNknFrontierRepairTransactionMatchedActive ? 1 : 0)}; observed_count={primaryRegularNknFrontierRepairTransactionObservedCount}; request_to_observe_ms={primaryRegularNknFrontierRepairTransactionRequestToObserveMs}; batch_start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; requested_missing_range_start={repairRequestedRangeStart}; requested_missing_range_count={repairRequestedRangeCount}; committed_frontier_before={repairFrontierBefore}; committed_frontier_after={nextChunkIndexAfterCommit}; frontier_advanced={(nextChunkIndexAfterCommit > repairFrontierBefore ? 1 : 0)}; accepted_chunk_count={repairAcceptedChunkCount}; duplicate_or_stale_chunk_count={repairDuplicateOrStaleChunkCount}; pending_write_chunk_count={repairPendingWriteChunkCount}; frontier_chunk_observed={(repairFrontierChunkObserved ? 1 : 0)}; frontier_chunk_status={FormatProtocolLogValue(repairFrontierChunkStatus)}");
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (batch is FileTransferChunkBatchFrameV6 v6Batch && committedChunkCount > 0)
        {
            await MaybeSendInboundV6RepairProofAsync(
                context,
                v6Batch,
                receivedTransportKind,
                acceptedChunks.Count,
                nextChunkIndexAfterCommit,
                repairFrontierBefore).ConfigureAwait(false);
        }

        if (completed)
        {
            await SendInboundV4TerminalReadyStateBestEffortAsync(context).ConfigureAwait(false);
        }
        else
        {
            await SendInboundSparseCreditStateAsync(context, "chunk_batch_committed", terminalReady: false).ConfigureAwait(false);
            if (ShouldUsePostTunaFallbackV6FeedbackEnvelope(context))
            {
                await SendInboundV6FrontierRequestAsync(context, "chunk_batch_committed", forceSend: false).ConfigureAwait(false);
            }
            else
            {
                await SendInboundV6RepairRequestAsync(context, "chunk_batch_committed").ConfigureAwait(false);
            }
        }

        if (repairProofFrame is not null)
        {
            await SendInboundV6RepairProofAsync(context, repairProofFrame).ConfigureAwait(false);
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

        await SendInboundSparseCreditStateAsync(context, reason, terminalReady: false).ConfigureAwait(false);
    }

    private async Task SendInboundV6RepairProofAsync(InboundTransferContext context, FileTransferRepairProofFrameV6 proof)
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

    private Task<bool> SendInboundSparseCreditStateAsync(
        InboundTransferContext context,
        string reason,
        bool terminalReady,
        bool requireMissingRange = false,
        bool forceMissingRange = false,
        bool forceSend = false)
        => ShouldUsePostTunaFallbackV6FeedbackEnvelope(context)
            ? SendInboundV6ReceiverStateAsync(context, reason, forceSend || requireMissingRange || forceMissingRange, terminalReady)
            : SendInboundV4StateAsync(context, reason, terminalReady, requireMissingRange, forceMissingRange, forceSend);

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

            if (context.PullTransportPaused &&
                IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
            {
                context.TunaActivationBarrierStateSendSuppressedCount++;
                if (context.TunaActivationBarrierStateSendSuppressedCount is 1 ||
                    context.TunaActivationBarrierStateSendSuppressedCount % 64 == 0)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_state_send_suppressed_for_tuna_activation_barrier; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; state_reason={FormatProtocolLogValue(reason)}; pause_reason={FormatProtocolLogValue(context.PullTransportPauseReason)}; suppressed_count={context.TunaActivationBarrierStateSendSuppressedCount}; committed_chunk={context.NextChunkIndex}; highest_received_chunk={context.PullHighestReceivedChunkIndex}; bytes_transferred={context.BytesTransferred}");
                }

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
            if (IsV6TransportHandoffBlockingTail(context.V6TransportHandoff) &&
                context.V6TransportHandoff!.State != V6TransportHandoffState.BackfillRepair)
            {
                var frontierOnlyCredit = Math.Clamp(
                    state.ContiguousCommittedChunkIndex + V4PostFallbackEmergencyFrontierRepairChunks,
                    state.ContiguousCommittedChunkIndex,
                    context.ChunkCount);
                if (state.CreditUntilChunkIndexExclusive > frontierOnlyCredit)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V6TransportHandoff.EpochId}; state={FormatV6TransportHandoffState(context.V6TransportHandoff.State)}; original_credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; advertised_credit_until_chunk_index_exclusive={frontierOnlyCredit}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}");
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
            context.V4LastStateBytesCommitted = state.BytesCommitted;
            context.V4LastStateSentUtc = now;
            dataSession = context.DataSession;
        }

        try
        {
            await dataSession.SendAsync(state, context.LifetimeCts.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context))
                {
                    context.PullV4StateSentCountTotal++;
                }
            }

            var metadataState = state as FileTransferReceiverStateFrameV6;
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    MarkInboundFallbackCheckpointAcceptedLocked(
                        context,
                        metadataState?.RepairRequestId,
                        metadataState?.TransportEpoch ?? 0,
                        reason);
                }
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_state_sent; transfer_id={state.TransferId}; session_id={state.SessionId}; reason={reason}; epoch={state.Epoch}; repair_request_id={FormatProtocolLogValue(metadataState?.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(metadataState?.Priority ?? "(none)")}; recovery_mode={FormatProtocolLogValue(metadataState?.RecoveryMode ?? "(none)")}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; mixed_screenshare={(IsV4MixedScreenShareActive() ? 1 : 0)}; screen_share_active={(sessionScreenShareActive ? 1 : 0)}; screen_share_degraded={(sessionScreenShareDegraded ? 1 : 0)}; screen_share_observed={(sessionScreenShareObserved ? 1 : 0)}; credit_window_chunks={stateCreditWindowChunks}; frontier_credit_cap_chunk_index_exclusive={frontierCreditCapChunkIndexExclusive}; sparse_credit_target_without_frontier_cap={sparseCreditTargetWithoutFrontierCap}; frontier_window_credit_capped={(frontierWindowCreditCapped ? 1 : 0)}; frontier_lag_chunks={frontierLagChunks}; missing_range_count={state.MissingRanges.Count}; frontier_stall_age_ms={frontierStallAgeMs}; bytes_committed={state.BytesCommitted}; receiver_memory_pressure={(state.ReceiverMemoryPressure ? 1 : 0)}; receiver_disk_pressure={(state.ReceiverDiskPressure ? 1 : 0)}; terminal_ready={(state.TerminalReady ? 1 : 0)}; transfer_paused={(state.TransferPaused ? 1 : 0)}");
            if (IsPrimaryRegularNknFrontierRepairTransactionId(metadataState?.RepairRequestId) &&
                state.MissingRanges.Count > 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_state_sent; direction=inbound; transfer_id={state.TransferId}; session_id={state.SessionId}; request_id={FormatProtocolLogValue(metadataState?.RepairRequestId)}; epoch={state.Epoch}; start_chunk_index={state.MissingRanges[0].StartChunkIndex}; requested_chunk_count={state.MissingRanges[0].ChunkCount}; committed_frontier_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; frontier_stall_age_ms={frontierStallAgeMs}");
            }
            LogPrimaryRegularNknBulkV6CheckpointSent(context, state, reason, sent: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string? pauseReason = null;
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) &&
                    !context.IsTerminal &&
                    context.PullTransportPaused &&
                    IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
                {
                    pauseReason = context.PullTransportPauseReason;
                }
            }

            if (pauseReason is not null)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v4_state_send_deferred_for_tuna_activation_transport_pause; direction=inbound; transfer_id={state.TransferId}; session_id={state.SessionId}; state_reason={FormatProtocolLogValue(reason)}; pause_reason={FormatProtocolLogValue(pauseReason)}; epoch={state.Epoch}; contiguous_committed_chunk_index={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; credit_until_chunk_index_exclusive={state.CreditUntilChunkIndexExclusive}; missing_range_count={state.MissingRanges.Count}; bytes_committed={state.BytesCommitted}; error={FormatProtocolLogValue(ex.GetType().Name)}; message={FormatProtocolLogValue(ex.Message)}");
                return false;
            }

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
        var sendTask = SendInboundSparseCreditStateAsync(context, "terminal_ready", terminalReady: true);
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
        var bytesCommittedAdvance = state.BytesCommitted - context.V4LastStateBytesCommitted;
        var bytesCommittedCanDriveStateCadence = ShouldUseInboundV4BytesCommittedForStateCadenceLocked(context);
        var progressMinChunks = ResolveInboundV4StateProgressMinChunks(context);
        var progressMinBytes = progressMinChunks * (long)Math.Max(1, context.ChunkSizeBytes);
        if (creditAdvance >= progressMinChunks ||
            frontierAdvance >= progressMinChunks ||
            (bytesCommittedCanDriveStateCadence && bytesCommittedAdvance >= progressMinBytes))
        {
            return false;
        }

        var stateAgeMs = (long)Math.Max(0, (now - context.V4LastStateSentUtc.Value).TotalMilliseconds);
        if (stateAgeMs >= V4StateProgressMaxDelayMs &&
            (creditAdvance > 0 ||
             frontierAdvance > 0 ||
             durableHighestAdvance > 0 ||
             (bytesCommittedCanDriveStateCadence && bytesCommittedAdvance > 0)))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldUseInboundV4BytesCommittedForStateCadenceLocked(InboundTransferContext context)
        => ShouldAdvertiseInboundV4SparseWrittenProgressLocked(context);

    private static int ResolveInboundV4StateProgressMinChunks(InboundTransferContext context)
        => ShouldAdvertiseInboundV4SparseWrittenProgressLocked(context)
            ? V6ReceiverStateProgressMinCommittedChunks
            : V4StateProgressCreditMinChunks;

    private async Task MaybeSendInboundSparseCreditFrontierStallStateAsync(InboundTransferContext context)
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
            await SendInboundSparseCreditStateAsync(
                context,
                "frontier_stall_repair_due",
                terminalReady: false,
                requireMissingRange: true).ConfigureAwait(false);
            if (ShouldUsePostTunaFallbackV6FeedbackEnvelope(context))
            {
                await SendInboundV6FrontierRequestAsync(context, "frontier_stall_repair_due", forceSend: true).ConfigureAwait(false);
            }
            else
            {
                await SendInboundV6RepairRequestAsync(context, "frontier_stall_repair_due").ConfigureAwait(false);
            }
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

    private async Task<bool> SendInboundV4CompleteAsync(
        InboundTransferContext context,
        string sessionId,
        string transferId,
        long fileSizeBytes,
        string sha256Base64,
        CancellationToken ct,
        bool failOnSendFailure = true)
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
            FileTransferCompleteFrameV4 completeFrame = ShouldUseV6SparseCreditEnvelope(context)
                ? new FileTransferCompleteFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = fileSizeBytes,
                    Sha256Base64 = sha256Base64,
                }
                : new FileTransferCompleteFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = fileSizeBytes,
                    Sha256Base64 = sha256Base64,
                };
            await dataSession.SendAsync(completeFrame, ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id={sessionId}; file_size_bytes={fileSizeBytes}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !failOnSendFailure)
        {
            if (!failOnSendFailure)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v4_complete_data_frame_echo_failed; transfer_id={transferId}; session_id={sessionId}; file_size_bytes={fileSizeBytes}; error={ex.GetType().Name}");
                return false;
            }

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
            FileTransferErrorFrameV4 errorFrame = ShouldUseV6SparseCreditEnvelope(context)
                ? new FileTransferErrorFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = message,
                }
                : new FileTransferErrorFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = errorCode,
                    Message = message,
                };
            await dataSession.SendAsync(errorFrame, context.LifetimeCts.Token).ConfigureAwait(false);
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
        var missingRanges = BuildInboundV4MissingRangesLocked(context, forceMissingRange);

        var primaryRegularNknCheckpoint =
            context.RouteRuntime.UsesV6SparsePump &&
            string.Equals(reason, V6RegularNknCheckpointSyncRecoveryMode, StringComparison.Ordinal);
        if (primaryRegularNknCheckpoint)
        {
            context.V6RegularNknCheckpointSequence++;
        }

        var postTunaFallbackCheckpoint =
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            string.Equals(reason, V6RegularNknStateRefreshRecoveryMode, StringComparison.Ordinal) &&
            IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg);
        var primaryRegularNknFrontierRepairRequestId = primaryRegularNknCheckpoint
            ? null
            : postTunaFallbackCheckpoint
                ? null
            : EnsureInboundPrimaryRegularNknFrontierRepairTransactionLocked(context, missingRanges);

        var unresolvedV6Epoch = IsV6TransportEpochUnresolved(context.V6TransportEpoch)
            ? context.V6TransportEpoch
            : null;
        var receiverTransportEpoch = primaryRegularNknCheckpoint
            ? context.PullTransportRebindGeneration
            : postTunaFallbackCheckpoint
                ? context.CurrentTransferLeg!.TransportEpochId
            : unresolvedV6Epoch?.EpochId ?? context.V6TransportHandoff?.EpochId ?? 0;
        var receiverRepairRequestId = primaryRegularNknCheckpoint
            ? context.V6RegularNknLastCheckpointSyncRequestId ?? $"checkpoint:{context.V6RegularNknCheckpointSequence}"
            : postTunaFallbackCheckpoint
                ? context.CurrentTransferLeg!.CheckpointRequestId ?? unresolvedV6Epoch?.LastRepairRequestId ?? context.V6TransportHandoff?.LastRepairRequestId
            : primaryRegularNknFrontierRepairRequestId ?? unresolvedV6Epoch?.LastRepairRequestId ?? context.V6TransportHandoff?.LastRepairRequestId;
        var receiverPriority = primaryRegularNknCheckpoint
            ? V6RegularNknCheckpointSyncPriority
            : postTunaFallbackCheckpoint
                ? context.CurrentTransferLeg!.CheckpointPriority ?? V6RegularNknStateRefreshPriority
            : primaryRegularNknFrontierRepairRequestId is not null
                ? V6RegularNknFrontierRepairTransactionPriority
            : unresolvedV6Epoch is not null
                ? unresolvedV6Epoch.State == V6TransportEpochState.BackfillRepair
                    ? "backfill"
                    : "frontier"
                : context.V6TransportHandoff is null
                    ? null
                    : context.V6TransportHandoff.State == V6TransportHandoffState.BackfillRepair
                        ? "backfill"
                        : "frontier";
        var receiverRecoveryMode = primaryRegularNknCheckpoint
            ? V6RegularNknCheckpointSyncRecoveryMode
            : postTunaFallbackCheckpoint
                ? V6RegularNknStateRefreshRecoveryMode
            : primaryRegularNknFrontierRepairRequestId is not null
                ? V6RegularNknFrontierRepairTransactionRecoveryMode
            : unresolvedV6Epoch is not null
                ? FormatV6TransportEpochState(unresolvedV6Epoch.State)
                : context.V6TransportHandoff is null
                    ? null
                    : FormatV6TransportHandoffState(context.V6TransportHandoff.State);
        var bytesCommitted = ResolveInboundV4StateBytesCommittedLocked(context);

        if (!ShouldUseV6SparseCreditEnvelope(context))
        {
            return new FileTransferStateFrameV4
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                Epoch = context.V4StateEpoch,
                ContiguousCommittedChunkIndex = context.NextChunkIndex,
                DurableReceivedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
                CreditUntilChunkIndexExclusive = context.V4CreditUntilChunkIndexExclusive,
                MissingRanges = missingRanges,
                BytesCommitted = bytesCommitted,
                ReceiverMemoryPressure = context.ReceiverBufferPressureActive,
                ReceiverDiskPressure = false,
                TerminalReady = terminalReady,
                TransferPaused = context.UserPaused,
                TransferPauseReason = context.UserPauseReason,
            };
        }

        return new FileTransferReceiverStateFrameV6
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Epoch = context.V4StateEpoch,
            ContiguousCommittedChunkIndex = context.NextChunkIndex,
            DurableReceivedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
            CreditUntilChunkIndexExclusive = context.V4CreditUntilChunkIndexExclusive,
            MissingRanges = missingRanges,
            BytesCommitted = bytesCommitted,
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
    }

    private static long ResolveInboundV4StateBytesCommittedLocked(InboundTransferContext context)
    {
        var bytesCommitted = context.BytesTransferred;
        if (ShouldAdvertiseInboundV4SparseWrittenProgressLocked(context))
        {
            bytesCommitted = Math.Max(bytesCommitted, context.ReceiverSparseBytesWritten);
        }

        return Math.Clamp(bytesCommitted, 0L, Math.Max(0L, context.FileSizeBytes));
    }

    private static bool ShouldAdvertiseInboundV4SparseWrittenProgressLocked(InboundTransferContext context)
        => context.ReceiverSparseWriteActive &&
           (context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 ||
            context.RouteRuntime.UsesV6SparsePump) &&
           context.V6TransportHandoff is null &&
           !context.PullPostTunaRecoveryActive;

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
            context.V4FrontierStallSuppressedCountTotal++;
            if (context.V4FrontierStallLastSuppressedLogUtc is null ||
                now - context.V4FrontierStallLastSuppressedLogUtc.Value >= TimeSpan.FromMilliseconds(frontierSuppressedLogIntervalMs))
            {
                context.V4FrontierStallLastSuppressedLogUtc = now;
                if (FileTransferDiagnosticLogPolicy.TraceEnabled)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=stall_age_below_min; epoch={context.V4StateEpoch}; start_chunk_index={context.NextChunkIndex}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, frontierRepairRepeatIntervalMs - frontierStallAgeMs)}; repair_interval_ms={frontierRepairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                }
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
        var repairBurstMaxChunks = ResolveV4RepairBurstMaxChunks(context);
        var ranges = new List<FileTransferRangeV4>();
        var totalMissingChunks = 0;
        if (context.PullHighestReceivedChunkIndex >= context.NextChunkIndex)
        {
            var upperInclusive = Math.Min(context.PullHighestReceivedChunkIndex, context.ChunkCount - 1);
            var chunkIndex = context.NextChunkIndex;
            while (chunkIndex <= upperInclusive &&
                   ranges.Count < FileTransferProtocol.MaxStateMissingRangesV4 &&
                   totalMissingChunks < repairBurstMaxChunks)
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
                    IsInboundV6ExactFrontierRepairRequiredLocked(context);
                var maxRangeChunks = isFrontierRange && IsInboundPostTunaRecoveryActiveLocked(context)
                    ? exactFrontierRepairRequired
                        ? V4PostFallbackEmergencyFrontierRepairChunks
                        : ResolveV4PostFallbackFrontierRepairChunks(context)
                    : isFrontierRange && sameFrontierRetry
                    ? ResolveV4FrontierTailRetryChunks(context)
                    : isFrontierRange
                        ? ResolveV4InitialFrontierRepairChunks(context)
                        : repairBurstMaxChunks;

                while (chunkIndex <= upperInclusive &&
                       !written[chunkIndex] &&
                       !context.ReceiverSparseChunksPendingWrite.Contains(chunkIndex) &&
                       totalMissingChunks + count < repairBurstMaxChunks &&
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
                    context.NextChunkIndex + repairBurstMaxChunks));
            if (repairEndExclusive > context.NextChunkIndex)
            {
                if (forceFrontierRepair || frontierStallAgeMs >= frontierRepairRepeatIntervalMs)
                {
                    frontierTailRepair = true;
                    previousFrontierTailRepair = FindRecentInboundV4FrontierTailRepairLocked(context, context.NextChunkIndex);
                    var maxRepairCount = repairEndExclusive - context.NextChunkIndex;
                    var repairCount = IsInboundPostTunaRecoveryActiveLocked(context)
                        ? Math.Min(
                            IsInboundV6ExactFrontierRepairRequiredLocked(context)
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
                    context.V4FrontierStallSuppressedCountTotal++;
                    context.V4FrontierStallLastSuppressedLogUtc = now;
                    if (FileTransferDiagnosticLogPolicy.TraceEnabled)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=stall_age_below_min; epoch={context.V4StateEpoch}; start_chunk_index={context.NextChunkIndex}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, frontierRepairRepeatIntervalMs - frontierStallAgeMs)}; repair_interval_ms={frontierRepairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    }
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
            context.PullV4RepairSuppressedCountTotal++;
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
                    context.V4FrontierStallSuppressedCountTotal++;
                    if (FileTransferDiagnosticLogPolicy.TraceEnabled)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_v4_frontier_stall_missing_range_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=retry_interval; epoch={context.V4StateEpoch}; repair_request_key={repairRequestKey}; start_chunk_index={firstStart}; requested_chunk_count={requestedChunkCount}; frontier_stall_age_ms={frontierStallAgeMs}; retry_in_ms={Math.Max(0, retryInMs)}; repair_interval_ms={repairRepeatIntervalMs}; initial_frontier_repair_chunks={ResolveV4InitialFrontierRepairChunks(context)}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}");
                    }
                }
            }

            return [];
        }

        repairState.LastRequestedUtc = now;
        repairState.AttemptCount++;
        repairState.Filled = false;
        context.PullV4RepairRequestCountTotal++;
        context.PullV4RepairRequestedChunkCountTotal += requestedChunkCount;
        if (frontierTailRepair)
        {
            context.PullV4FrontierTailRepairRequestCountTotal++;
        }

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
            MaybeObserveRegularV4ReceiverFrontierRepairPressure(
                context,
                Math.Max(0, Math.Max(context.PullHighestReceivedChunkIndex + 1, context.V4CreditUntilChunkIndexExclusive) - context.NextChunkIndex),
                requestedChunkCount);

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
            : ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context)
            ? V4FileOnlyInitialFrontierRepairChunks
            : context.V4MixedScreenShareTransfer
            ? V4MixedInitialFrontierRepairChunks
            : IsV4FileOnlyFastRepairEnabled()
                ? V4FileOnlyInitialFrontierRepairChunks
                : V4KnownFrontierRepairChunks;

    private static int ResolveV4FrontierTailRetryChunks(InboundTransferContext context)
        => IsInboundPostTunaRecoveryActiveLocked(context)
            ? ResolveV4PostFallbackFrontierRepairChunks(context)
            : ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context)
            ? V4FileOnlyFrontierTailRetryChunks
            : context.V4MixedScreenShareTransfer
            ? V4FrontierTailRetryChunks
            : IsV4FileOnlyFastRepairEnabled()
                ? V4FileOnlyFrontierTailRetryChunks
                : V4FrontierTailRetryChunks;

    private static bool ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(InboundTransferContext context)
        => ShouldUseV6RegularNknSparseRuntime(context) &&
           IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

    private static bool ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(OutboundTransferContext context)
        => ShouldUseV6RegularNknSparseRuntime(context) &&
           IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

    private static bool IsPrimaryRegularNknFrontierRepairTransactionId(string? repairRequestId)
        => repairRequestId?.StartsWith(V6RegularNknFrontierRepairTransactionRequestPrefix, StringComparison.Ordinal) == true;

    private static string? EnsureInboundPrimaryRegularNknFrontierRepairTransactionLocked(
        InboundTransferContext context,
        IReadOnlyList<FileTransferRangeV4> missingRanges)
    {
        if (!ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context) ||
            missingRanges.Count == 0 ||
            missingRanges[0].StartChunkIndex != context.NextChunkIndex ||
            missingRanges[0].ChunkCount <= 0)
        {
            if (context.V6RegularNknFrontierRepairTransactionId is not null &&
                context.NextChunkIndex > context.V6RegularNknFrontierRepairTransactionStartChunkIndex)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; reason=frontier_advanced; committed_frontier_chunk_index={context.NextChunkIndex}; previous_start_chunk_index={context.V6RegularNknFrontierRepairTransactionStartChunkIndex}; previous_chunk_count={context.V6RegularNknFrontierRepairTransactionChunkCount}; observed_count={context.V6RegularNknFrontierRepairTransactionObservedCount}");
                ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(context);
            }
            else if (context.V6RegularNknFrontierRepairTransactionId is not null &&
                     missingRanges.Count == 0)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_retained; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; reason=no_missing_ranges_without_frontier_advance; committed_frontier_chunk_index={context.NextChunkIndex}; active_start_chunk_index={context.V6RegularNknFrontierRepairTransactionStartChunkIndex}; active_chunk_count={context.V6RegularNknFrontierRepairTransactionChunkCount}; observed_count={context.V6RegularNknFrontierRepairTransactionObservedCount}");
            }

            return null;
        }

        var first = missingRanges[0];
        if (context.V6RegularNknFrontierRepairTransactionId is not null &&
            context.V6RegularNknFrontierRepairTransactionStartChunkIndex == first.StartChunkIndex &&
            context.V6RegularNknFrontierRepairTransactionChunkCount == first.ChunkCount)
        {
            return context.V6RegularNknFrontierRepairTransactionId;
        }

        if (context.V6RegularNknFrontierRepairTransactionId is not null &&
            context.NextChunkIndex <= context.V6RegularNknFrontierRepairTransactionStartChunkIndex)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; reason=frontier_retargeted; committed_frontier_chunk_index={context.NextChunkIndex}; previous_start_chunk_index={context.V6RegularNknFrontierRepairTransactionStartChunkIndex}; previous_chunk_count={context.V6RegularNknFrontierRepairTransactionChunkCount}; observed_count={context.V6RegularNknFrontierRepairTransactionObservedCount}; new_start_chunk_index={first.StartChunkIndex}; new_requested_chunk_count={first.ChunkCount}");
            ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(context);
        }
        else if (context.V6RegularNknFrontierRepairTransactionId is not null &&
                 context.NextChunkIndex > context.V6RegularNknFrontierRepairTransactionStartChunkIndex)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; reason=frontier_advanced; committed_frontier_chunk_index={context.NextChunkIndex}; previous_start_chunk_index={context.V6RegularNknFrontierRepairTransactionStartChunkIndex}; previous_chunk_count={context.V6RegularNknFrontierRepairTransactionChunkCount}; observed_count={context.V6RegularNknFrontierRepairTransactionObservedCount}");
            ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(context);
        }

        if (context.V6RegularNknFrontierRepairTransactionId is not null)
        {
            return context.V6RegularNknFrontierRepairTransactionId;
        }

        context.V6RegularNknFrontierRepairTransactionSequence++;
        context.V6RegularNknFrontierRepairTransactionId =
            $"{V6RegularNknFrontierRepairTransactionRequestPrefix}{first.StartChunkIndex}:{first.ChunkCount}:{context.V6RegularNknFrontierRepairTransactionSequence}";
        context.V6RegularNknFrontierRepairTransactionStartChunkIndex = first.StartChunkIndex;
        context.V6RegularNknFrontierRepairTransactionChunkCount = first.ChunkCount;
        context.V6RegularNknFrontierRepairTransactionStartedUtc = DateTimeOffset.UtcNow;
        context.V6RegularNknFrontierRepairTransactionLastObservedUtc = null;
        context.V6RegularNknFrontierRepairTransactionObservedCount = 0;

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_started; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; sequence={context.V6RegularNknFrontierRepairTransactionSequence}; start_chunk_index={first.StartChunkIndex}; requested_chunk_count={first.ChunkCount}; committed_frontier_chunk_index={context.NextChunkIndex}; durable_received_highest_chunk_index={context.PullHighestReceivedChunkIndex}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}");
        return context.V6RegularNknFrontierRepairTransactionId;
    }

    private static void ClearInboundPrimaryRegularNknFrontierRepairTransactionAfterProgressLocked(
        InboundTransferContext context,
        int frontierBefore,
        int frontierAfter)
    {
        if (!ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context) ||
            context.V6RegularNknFrontierRepairTransactionId is null ||
            frontierAfter <= frontierBefore ||
            frontierAfter <= context.V6RegularNknFrontierRepairTransactionStartChunkIndex)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_primary_regular_nkn_frontier_repair_transaction_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; request_id={FormatProtocolLogValue(context.V6RegularNknFrontierRepairTransactionId)}; reason=frontier_advanced; committed_frontier_before={frontierBefore}; committed_frontier_after={frontierAfter}; start_chunk_index={context.V6RegularNknFrontierRepairTransactionStartChunkIndex}; requested_chunk_count={context.V6RegularNknFrontierRepairTransactionChunkCount}; observed_count={context.V6RegularNknFrontierRepairTransactionObservedCount}");
        ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(context);
    }

    private static void ClearInboundPrimaryRegularNknFrontierRepairTransactionLocked(InboundTransferContext context)
    {
        context.V6RegularNknFrontierRepairTransactionId = null;
        context.V6RegularNknFrontierRepairTransactionStartChunkIndex = -1;
        context.V6RegularNknFrontierRepairTransactionChunkCount = 0;
        context.V6RegularNknFrontierRepairTransactionStartedUtc = null;
        context.V6RegularNknFrontierRepairTransactionLastObservedUtc = null;
        context.V6RegularNknFrontierRepairTransactionObservedCount = 0;
    }

    private static int ResolveV4RepairBurstMaxChunks(InboundTransferContext context)
        => ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context)
            ? V6RegularNknSparseRuntimeRepairBurstMaxChunks
            : V4RepairBurstMaxChunks;

    private static int ResolveV4PostFallbackFrontierRepairChunks(InboundTransferContext context)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context))
        {
            return V4PostFallbackEmergencyFrontierRepairChunks;
        }

        if (IsInboundV6ExactFrontierRepairRequiredLocked(context))
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

    private static bool IsInboundV6ExactFrontierRepairRequiredLocked(InboundTransferContext context)
    {
        if (!IsInboundPostTunaRecoveryActiveLocked(context) ||
            context.NextChunkIndex < 0 ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        if (!IsInboundPostFallbackFrontierGapMissingLocked(context) ||
            context.PullTransportRebindFrontierRepairWindowChunks > V4PostFallbackEmergencyFrontierRepairChunks ||
            context.PullTransportRebindFrontierRepairCommittedChunks >= PullTransportRebindFrontierOnlyStableAdvanceChunks)
        {
            return false;
        }

        if (context.V6TransportHandoff is { } handoff)
        {
            return handoff.State is V6TransportHandoffState.NknProofPending
                or V6TransportHandoffState.FrontierRepairOnly
                or V6TransportHandoffState.WaitingForRegularNkn;
        }

        return true;
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
        => ShouldUseSparseRuntimeFrontierRepairCadenceLocked(context)
            ? V6RegularNknSparseRuntimeFrontierRepairRepeatIntervalMs
            : ShouldUseRegularNknV4FileOnlyFrontierRepairCadenceLocked(context)
            ? V4RegularNknFileOnlyFrontierRepairRepeatIntervalMs
            : context.V4MixedScreenShareTransfer || !IsV4FileOnlyFastRepairEnabled()
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
        => frontierRepair && ShouldUsePrimaryRegularNknBulkV6RepairProfileLocked(context)
            ? V6RegularNknSparseRuntimeSenderFrontierRepairRepeatIntervalMs
            : frontierRepair && ShouldUseSparseRuntimeFrontierRepairCadenceLocked(context)
            ? V6RegularNknSparseRuntimeFrontierRepairRepeatIntervalMs
            : frontierRepair && ShouldUseRegularNknV4FileOnlyFrontierRepairCadenceLocked(context)
            ? V4RegularNknFileOnlyFrontierRepairRepeatIntervalMs
            : frontierRepair && IsV4FileOnlyFastRepairEnabled(context)
            ? V4FileOnlyFrontierRepairRepeatIntervalMs
            : V4RepairRepeatIntervalMs;

    private static bool ShouldUseSparseRuntimeFrontierRepairCadenceLocked(InboundTransferContext context)
        => ShouldUseV6RegularNknSparseRuntime(context) &&
           IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) &&
           !IsInboundPostTunaRecoveryActiveLocked(context);

    private static bool ShouldUseSparseRuntimeFrontierRepairCadenceLocked(OutboundTransferContext context)
        => ShouldUseV6RegularNknSparseRuntime(context) &&
           IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context) &&
           !IsOutboundPostTunaRecoveryActiveLocked(context);

    private static bool ShouldUseRegularNknV4FileOnlyFrontierRepairCadenceLocked(InboundTransferContext context)
        => !IsInboundPostTunaRecoveryActiveLocked(context) &&
           !ShouldUseSparseRuntimeFrontierRepairCadenceLocked(context) &&
           !context.V4MixedScreenShareTransfer &&
           IsV4FileOnlyFastRepairEnabled();

    private static bool ShouldUseRegularNknV4FileOnlyFrontierRepairCadenceLocked(OutboundTransferContext context)
        => !IsOutboundPostTunaRecoveryActiveLocked(context) &&
           !ShouldUseSparseRuntimeFrontierRepairCadenceLocked(context) &&
           IsV4FileOnlyFastRepairEnabled(context);

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

    private static void LogInboundV6FrontierRepairStillMissing(
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

        var transportEpoch = batch is FileTransferChunkBatchFrameV6 v6Batch
            ? v6Batch.TransportEpoch
            : context.V6TransportHandoff?.EpochId ?? 0;
        var repairRequestId = batch is FileTransferChunkBatchFrameV6 v6RepairBatch
            ? v6RepairBatch.RepairRequestId
            : context.V6TransportHandoff?.LastRepairRequestId;
        var now = DateTimeOffset.UtcNow;
        var sameFrontier = context.LastV6FrontierRepairStillMissingChunkIndex == frontierBefore;
        if (sameFrontier &&
            context.LastV6FrontierRepairStillMissingLogUtc is { } lastLogUtc &&
            now - lastLogUtc < TimeSpan.FromSeconds(2))
        {
            context.SuppressedV6FrontierRepairStillMissingLogCount++;
            return;
        }

        var suppressedCount = context.SuppressedV6FrontierRepairStillMissingLogCount;
        context.LastV6FrontierRepairStillMissingLogUtc = now;
        context.LastV6FrontierRepairStillMissingChunkIndex = frontierBefore;
        context.SuppressedV6FrontierRepairStillMissingLogCount = 0;
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
    {
        if (IsV4MixedScreenShareActive())
        {
            return ResolveV4StateCreditWindowChunksForCurrentMode();
        }

        var windowBytes = ShouldUseV6RegularNknBulkSparseCreditWindowLocked(context)
            ? V6RegularNknBulkSparseCreditWindowBytes
            : V4FileOnlySparseCreditWindowBytes;
        return Math.Max(1, (int)Math.Ceiling(windowBytes / (double)Math.Max(1, context.ChunkSizeBytes)));
    }

    private static bool ShouldUseV6RegularNknBulkSparseCreditWindowLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesV6SparsePump &&
           context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context);

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
