using System.Collections;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private int ResolveOutboundInitialPipelineDepth(OutboundTransferContext? context = null)
        => V4SenderPumpDepth;

    private int ResolveOutboundPipelineDepth(OutboundTransferContext? context = null)
        => V4SenderPumpDepth;

    private int ResolveInboundMaximumPipelineDepthLocked(InboundTransferContext context)
        => V4SenderPumpDepth;

    private int ResolveInboundMinimumPipelineDepthLocked(InboundTransferContext context)
        => V4SenderPumpDepth;

    private Task<bool> MaybeSendTransportRebindStateAsync(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6
            ? SendInboundV6TransportRebindStateAsync(context)
            : SendInboundV4StateAsync(
                context,
                "transport_rebind",
                terminalReady: false,
                requireMissingRange: false,
                forceMissingRange: true,
                forceSend: true);

    private async Task<bool> SendInboundV6TransportRebindStateAsync(InboundTransferContext context)
    {
        var stateSent = await SendInboundV6ReceiverStateAsync(
            context,
            "transport_rebind",
            forceSend: true).ConfigureAwait(false);
        await SendInboundV6FrontierRequestAsync(
            context,
            "transport_rebind",
            forceSend: true).ConfigureAwait(false);
        return stateSent;
    }

    private async Task<bool> SendInboundV6TransportHandoffAsync(InboundTransferContext context, string reason)
    {
        await SendInboundV6HandoffFrameAsync(context, reason).ConfigureAwait(false);
        var stateSent = ShouldUsePostTunaFallbackV6FeedbackEnvelope(context)
            ? await SendInboundV6ReceiverStateAsync(
                context,
                reason,
                forceSend: true).ConfigureAwait(false)
            : await SendInboundV4StateAsync(
                context,
                reason,
                terminalReady: false,
                requireMissingRange: false,
                forceMissingRange: true,
                forceSend: true).ConfigureAwait(false);
        await SendInboundV6RepairRequestAsync(context, reason).ConfigureAwait(false);
        return stateSent;
    }

    private async Task<bool> SendInboundV6HandoffFrameAsync(InboundTransferContext context, string reason)
    {
        FileTransferTransportEpochFrameV6? frame;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.V6TransportHandoff is null)
            {
                return false;
            }

            frame = new FileTransferTransportEpochFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = context.V6TransportHandoff.EpochId,
                RecoveryMode = FormatV6TransportHandoffState(context.V6TransportHandoff.State),
            };
            dataSession = context.DataSession;
        }

        try
        {
            await dataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_handoff_frame_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={frame.TransportEpoch}; reason={FormatProtocolLogValue(reason)}; recovery_mode={FormatProtocolLogValue(frame.RecoveryMode ?? "(none)")}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_handoff_frame_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private async Task<bool> SendInboundV6RepairRequestAsync(InboundTransferContext context, string reason,
        InboundBootstrapWriteSource? requiredWriteSource = null)
    {
        FileTransferFrontierRequestFrameV6? frame;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (HasBootstrapSourceHoldLocked(context)) { return false; }
            if (requiredWriteSource is not null && !IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                context.V6TransportHandoff is null ||
                context.NextChunkIndex >= context.ChunkCount)
            {
                return false;
            }

            var ranges = CreateInboundV6HandoffRepairRangesLocked(context);
            if (ranges.Count == 0)
            {
                return false;
            }

            var repairRequestId = $"v6:{context.V6TransportHandoff.EpochId}:{context.NextChunkIndex}:{context.V4StateEpoch + 1}";
            context.V6TransportHandoff.LastRepairRequestId = repairRequestId;
            frame = new FileTransferFrontierRequestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = context.V6TransportHandoff.EpochId,
                RepairRequestId = repairRequestId,
                MissingRanges = ranges,
                Priority = context.V6TransportHandoff.State == V6TransportHandoffState.BackfillRepair
                    ? "backfill"
                    : "frontier",
                RecoveryMode = FormatV6TransportHandoffState(context.V6TransportHandoff.State),
            };
            dataSession = context.DataSession;
        }

        try
        {
            Task sending;
            if (requiredWriteSource is not null)
            {
                lock (gate)
                {
                    if (!IsInboundBootstrapWriteSourceCurrentLocked(context, requiredWriteSource)) { return false; }
                    sending = dataSession.SendAsync(frame, context.LifetimeCts.Token);
                }
            }
            else { sending = dataSession.SendAsync(frame, context.LifetimeCts.Token); }
            await sending.ConfigureAwait(false);
            var firstRange = frame.MissingRanges[0];
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_repair_requested; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={frame.TransportEpoch}; repair_request_id={FormatProtocolLogValue(frame.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(frame.Priority ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; first_start_chunk_index={firstRange.StartChunkIndex}; requested_chunk_count={firstRange.ChunkCount}; range_count={frame.MissingRanges.Count}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_repair_request_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private static IReadOnlyList<FileTransferRangeV4> CreateInboundV6HandoffRepairRangesLocked(InboundTransferContext context)
    {
        if (context.V6TransportHandoff is null ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return [];
        }

        var frontier = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount);
        var window = IsInboundV6ExactFrontierRepairRequiredLocked(context)
            ? V4PostFallbackEmergencyFrontierRepairChunks
            : context.V6TransportHandoff.State == V6TransportHandoffState.BackfillRepair
            ? ResolveV4PostFallbackFrontierRepairChunks(context)
            : V4PostFallbackEmergencyFrontierRepairChunks;
        var chunkCount = Math.Clamp(
            window,
            V4PostFallbackEmergencyFrontierRepairChunks,
            Math.Max(1, context.ChunkCount - frontier));
        return
        [
            new FileTransferRangeV4
            {
                StartChunkIndex = frontier,
                ChunkCount = chunkCount,
            },
        ];
    }

    private void ScheduleInboundTransportRebindRetries(InboundTransferContext context, string reason, int generation)
    {
        if (context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            context.IsTerminal)
        {
            return;
        }

        if (!TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RepairReplay,
                "inbound_transport_rebind_retry",
                "schedule_retry",
                out var runtimeOwner))
        {
            return;
        }

        _ = RunInboundTransportRebindRetriesAsync(context, reason, generation, runtimeOwner);
    }

    private async Task RunInboundTransportRebindRetriesAsync(
        InboundTransferContext context,
        string reason,
        int generation,
        RuntimeOwnerToken runtimeOwner)
    {
        var transferId = context.TransferId;
        var sessionId = context.SessionId;
        var retryIndex = 0;
        while (true)
        {
            var delayMs = retryIndex < PullTransportRebindRetryDelaysMs.Length
                ? PullTransportRebindRetryDelaysMs[retryIndex++]
                : 5000;
            try
            {
                await Task.Delay(delayMs, context.LifetimeCts.Token).ConfigureAwait(false);
                bool shouldRetry;
                bool progressObserved;
                bool recovered;
                bool frontierStalled;
                int nextChunkIndex;
                int highestReceivedChunkIndex;
                long bytesTransferred;
                int stableProgressSamples;
                lock (gate)
                {
                    if (!TryAuthorizeRuntimeOwnerActionLocked(
                            context,
                            runtimeOwner,
                            FileTransferCoordinatorActionKind.RepairReplay,
                            "inbound_transport_rebind_retry",
                            "retry_state_or_repair",
                            out _))
                    {
                        return;
                    }

                    var active =
                        ReferenceEquals(inboundTransfer, context) &&
                        !context.IsTerminal &&
                        context.PullSessionActive &&
                        context.PullManifestReceived &&
                        !context.UserPaused &&
                        !context.PeerPaused &&
                        !context.PullTransportPaused &&
                        context.PullTransportRebindGeneration == generation;
                    progressObserved =
                        context.NextChunkIndex > context.PullTransportRebindStartedNextChunkIndex ||
                        context.BytesTransferred > context.PullTransportRebindStartedBytesTransferred;
                    frontierStalled =
                        context.NextChunkIndex < context.ChunkCount &&
                        context.NextChunkIndex <= context.PullHighestReceivedChunkIndex;
                    if (active && progressObserved && !frontierStalled)
                    {
                        if (context.NextChunkIndex != context.PullTransportRebindLastObservedNextChunkIndex ||
                            context.PullHighestReceivedChunkIndex != context.PullTransportRebindLastObservedHighestReceivedChunkIndex)
                        {
                            context.PullTransportRebindStableProgressSamples++;
                            context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
                            context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                        }
                    }
                    else if (active)
                    {
                        context.PullTransportRebindStableProgressSamples = 0;
                        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
                        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                    }

                    recovered =
                        active &&
                        progressObserved &&
                        (context.NextChunkIndex >= context.ChunkCount ||
                         (!frontierStalled && context.PullTransportRebindStableProgressSamples >= 2));
                    if (active &&
                        context.V6TransportHandoff is { State: not V6TransportHandoffState.Recovered } handoff &&
                        DateTimeOffset.UtcNow - GetV6TransportHandoffActivityUtc(handoff) >= V6TransportHandoffWaitingTimeout &&
                        !progressObserved)
                    {
                        TrySetV6TransportHandoffState(
                            handoff,
                            FileTransferDirection.Inbound,
                            context.TransferId,
                            context.SessionId,
                            V6TransportHandoffState.WaitingForTargetTransport,
                            "proof_timeout",
                            context.NextChunkIndex,
                            context.PullHighestReceivedChunkIndex);
                        context.StatusMessage = GetV6TransportHandoffWaitingStatus(handoff);
                    }

                    if (recovered && context.V6TransportHandoff is not null)
                    {
                        CompleteInboundV6TransportHandoffLocked(
                            context,
                            "durable_frontier_progress",
                            context.NextChunkIndex,
                            context.PullHighestReceivedChunkIndex);
                    }
                    shouldRetry =
                        active &&
                        !recovered;
                    nextChunkIndex = context.NextChunkIndex;
                    highestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                    bytesTransferred = context.BytesTransferred;
                    stableProgressSamples = context.PullTransportRebindStableProgressSamples;
                }

                if (!shouldRetry)
                {
                    if (recovered)
                    {
                        LogInboundTransportRebindRecovered(context, reason, generation, nextChunkIndex, highestReceivedChunkIndex, bytesTransferred);
                    }

                    return;
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={(frontierStalled ? "filetransfer_rebind_recovery_still_stalled" : "filetransfer_rebind_recovery_pending")}; direction=inbound; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; retry_delay_ms={delayMs}; progress_observed={(progressObserved ? 1 : 0)}; stable_progress_samples={stableProgressSamples}; committed_chunk={nextChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; bytes_transferred={bytesTransferred}");
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event={(frontierStalled ? "filetransfer_v6_rebind_recovery_still_stalled" : "filetransfer_v6_rebind_recovery_pending")}; direction=inbound; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; retry_delay_ms={delayMs}; progress_observed={(progressObserved ? 1 : 0)}; stable_progress_samples={stableProgressSamples}; committed_chunk={nextChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; bytes_transferred={bytesTransferred}");
                bool sent;
                bool repairRequestSent;
                if (context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6)
                {
                    sent = await SendInboundV6ReceiverStateAsync(
                        context,
                        "transport_rebind_retry",
                        forceSend: true).ConfigureAwait(false);
                    repairRequestSent = await SendInboundV6FrontierRequestAsync(
                        context,
                        "transport_rebind_retry",
                        forceSend: true).ConfigureAwait(false);
                }
                else
                {
                    sent = await SendInboundV4StateAsync(
                        context,
                        "transport_rebind_retry",
                        terminalReady: false,
                        requireMissingRange: false,
                        forceMissingRange: true,
                        forceSend: true).ConfigureAwait(false);
                    repairRequestSent = await SendInboundV6RepairRequestAsync(context, "transport_rebind_retry").ConfigureAwait(false);
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_state_forced; direction=inbound; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; retry_delay_ms={delayMs}; state_sent={(sent ? 1 : 0)}; repair_request_sent={(repairRequestSent ? 1 : 0)}; committed_chunk={nextChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}");
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_retry_failed; direction=inbound; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; error={FormatProtocolLogValue(ex.Message)}");
                return;
            }
        }
    }

    private void LogInboundTransportRebindRecovered(
        InboundTransferContext context,
        string reason,
        int generation,
        int nextChunkIndex,
        int highestReceivedChunkIndex,
        long bytesTransferred)
    {
        bool shouldLog;
        long elapsedMs;
        lock (gate)
        {
            if (generation <= 0 ||
                !ReferenceEquals(inboundTransfer, context) ||
                context.PullTransportRebindGeneration != generation ||
                context.PullTransportRebindRecoveredLogged)
            {
                return;
            }

            context.PullTransportRebindRecoveredLogged = true;
            elapsedMs = context.PullTransportRebindStartedUtc is null
                ? 0
                : Math.Max(0, (long)(DateTimeOffset.UtcNow - context.PullTransportRebindStartedUtc.Value).TotalMilliseconds);
            shouldLog = true;
        }

        if (!shouldLog)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_progress_observed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; elapsed_ms={elapsedMs}; committed_chunk={nextChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; bytes_transferred={bytesTransferred}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_recovered; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; rebind_generation={generation}; elapsed_ms={elapsedMs}");
    }

    private static string FormatV6TransportHandoffState(V6TransportHandoffState state)
        => state switch
        {
            V6TransportHandoffState.TransportProofPending => "transport_proof_pending",
            V6TransportHandoffState.FrontierRepairOnly => "frontier_repair_only",
            V6TransportHandoffState.BackfillRepair => "backfill_repair",
            V6TransportHandoffState.Recovered => "recovered",
            V6TransportHandoffState.WaitingForTargetTransport => "waiting_for_target_transport",
            _ => "none",
        };

    private static string FormatFileTransferTransportKind(FileTransferTransportKind transport)
        => transport switch
        {
            FileTransferTransportKind.RegularNkn => "regular_nkn",
            FileTransferTransportKind.Tuna => "tuna",
            _ => "unknown",
        };

    private static string FormatFileTransferTransportHandoffKind(FileTransferTransportHandoffKind kind)
        => kind switch
        {
            FileTransferTransportHandoffKind.NormalToTunaActivation => "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.TunaToNormalFallback => "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaRestart => "tuna_restart",
            FileTransferTransportHandoffKind.RegularNknRecovery => "regular_nkn_recovery",
            _ => "none",
        };

    private static string GetV6TransportHandoffSwitchingStatus(TransportHandoffEpoch handoff)
        => handoff.TargetTransport == FileTransferTransportKind.Tuna
            ? "Switching to Tuna"
            : "Switching to regular NKN";

    private static string GetV6TransportHandoffRepairStatus(TransportHandoffEpoch handoff)
        => handoff.TargetTransport == FileTransferTransportKind.Tuna
            ? "Switching to Tuna"
            : "Repairing over regular NKN";

    private static string GetV6TransportHandoffWaitingStatus(TransportHandoffEpoch handoff)
        => handoff.TargetTransport == FileTransferTransportKind.Tuna
            ? "Switching to Tuna"
            : "Waiting for regular NKN";

    private static FileTransferTransportKind NormalizeTargetTransport(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (targetTransport != FileTransferTransportKind.Unknown)
        {
            return targetTransport;
        }

        return handoffKind switch
        {
            FileTransferTransportHandoffKind.NormalToTunaActivation or
                FileTransferTransportHandoffKind.TunaRestart => FileTransferTransportKind.Tuna,
            _ => FileTransferTransportKind.RegularNkn,
        };
    }

    private static FileTransferTransportKind ResolveSourceTransportForHandoff(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => handoffKind switch
        {
            FileTransferTransportHandoffKind.NormalToTunaActivation => FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback => FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.TunaRestart => FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.RegularNknRecovery => FileTransferTransportKind.RegularNkn,
            _ => targetTransport == FileTransferTransportKind.Tuna
                ? FileTransferTransportKind.RegularNkn
                : FileTransferTransportKind.Unknown,
        };

    private static FileTransferTransportHandoffKind NormalizeHandoffKind(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (handoffKind != FileTransferTransportHandoffKind.None)
        {
            return handoffKind;
        }

        return targetTransport == FileTransferTransportKind.Tuna
            ? FileTransferTransportHandoffKind.NormalToTunaActivation
            : FileTransferTransportHandoffKind.RegularNknRecovery;
    }

    private static bool IsV6TransportHandoffBlockingTail(TransportHandoffEpoch? handoff)
        => handoff is not null &&
           handoff.State is V6TransportHandoffState.TransportProofPending
               or V6TransportHandoffState.FrontierRepairOnly
               or V6TransportHandoffState.BackfillRepair
               or V6TransportHandoffState.WaitingForTargetTransport;

    private static void StartOutboundV6TransportHandoffLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedTarget = NormalizeTargetTransport(handoffKind, targetTransport);
        var normalizedSource = ResolveSourceTransportForHandoff(handoffKind, normalizedTarget);
        var normalizedKind = NormalizeHandoffKind(handoffKind, normalizedTarget);
        if (TryReuseActiveV6TransportHandoffLocked(
                context.V6TransportHandoff,
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                normalizedKind,
                normalizedSource,
                normalizedTarget,
                reason,
                context.RemoteNextExpectedChunkIndex,
                Math.Max(-1, context.ChunksAcceptedForTransport - 1)))
        {
            context.PullTransportRebindGeneration = (int)Math.Min(int.MaxValue, context.V6TransportHandoff!.EpochId);
            context.StatusMessage = GetV6TransportHandoffSwitchingStatus(context.V6TransportHandoff);
            return;
        }

        var epochId = Math.Max(
            Math.Max(1, context.PullTransportRebindGeneration),
            (int)Math.Min(int.MaxValue, context.LastRecoveredV6TransportHandoffEpoch + 1));
        context.PullTransportRebindGeneration = epochId;
        context.V6TransportHandoff = new TransportHandoffEpoch
        {
            EpochId = epochId,
            Kind = normalizedKind,
            SourceTransport = normalizedSource,
            TargetTransport = normalizedTarget,
            Direction = FileTransferDirection.Outbound,
            Reason = string.IsNullOrWhiteSpace(reason) ? "transport_rebind" : reason,
            StartedUtc = DateTimeOffset.UtcNow,
            TargetReadyUtc = DateTimeOffset.UtcNow,
            StartingCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            State = V6TransportHandoffState.TransportProofPending,
        };
        context.StatusMessage = GetV6TransportHandoffSwitchingStatus(context.V6TransportHandoff);
        LogV6TransportHandoffEpochStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.V6TransportHandoff);
    }

    private static void StartInboundV6TransportHandoffLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedTarget = NormalizeTargetTransport(handoffKind, targetTransport);
        var normalizedSource = ResolveSourceTransportForHandoff(handoffKind, normalizedTarget);
        var normalizedKind = NormalizeHandoffKind(handoffKind, normalizedTarget);
        if (TryReuseActiveV6TransportHandoffLocked(
                context.V6TransportHandoff,
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                normalizedKind,
                normalizedSource,
                normalizedTarget,
                reason,
                context.NextChunkIndex,
                context.PullHighestReceivedChunkIndex))
        {
            context.PullTransportRebindGeneration = (int)Math.Min(int.MaxValue, context.V6TransportHandoff!.EpochId);
            context.StatusMessage = GetV6TransportHandoffSwitchingStatus(context.V6TransportHandoff);
            return;
        }

        var epochId = Math.Max(
            Math.Max(1, context.PullTransportRebindGeneration),
            (int)Math.Min(int.MaxValue, context.LastRecoveredV6TransportHandoffEpoch + 1));
        context.PullTransportRebindGeneration = epochId;
        context.V6TransportHandoff = new TransportHandoffEpoch
        {
            EpochId = epochId,
            Kind = normalizedKind,
            SourceTransport = normalizedSource,
            TargetTransport = normalizedTarget,
            Direction = FileTransferDirection.Inbound,
            Reason = string.IsNullOrWhiteSpace(reason) ? "transport_rebind" : reason,
            StartedUtc = DateTimeOffset.UtcNow,
            TargetReadyUtc = DateTimeOffset.UtcNow,
            StartingCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = context.PullHighestReceivedChunkIndex,
            LastObservedCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
            State = V6TransportHandoffState.TransportProofPending,
        };
        context.StatusMessage = GetV6TransportHandoffSwitchingStatus(context.V6TransportHandoff);
        LogV6TransportHandoffEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.V6TransportHandoff);
    }

    private static bool TryReuseActiveV6TransportHandoffLocked(
        TransportHandoffEpoch? handoff,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind sourceTransport,
        FileTransferTransportKind targetTransport,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        if (!IsV6TransportHandoffBlockingTail(handoff) ||
            handoff!.TargetTransport != targetTransport)
        {
            return false;
        }

        handoff.TargetReadyUtc ??= DateTimeOffset.UtcNow;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_reused; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; existing_handoff_kind={FormatFileTransferTransportHandoffKind(handoff.Kind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; source_transport={FormatFileTransferTransportKind(sourceTransport)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; reason={FormatProtocolLogValue(reason)}; state={FormatV6TransportHandoffState(handoff.State)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        return true;
    }

    private static bool TrySetV6TransportHandoffState(
        TransportHandoffEpoch? handoff,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        V6TransportHandoffState nextState,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        if (handoff is null ||
            handoff.State == nextState)
        {
            return false;
        }

        if (handoff.State == V6TransportHandoffState.Recovered &&
            nextState != V6TransportHandoffState.Recovered)
        {
            return false;
        }

        var previousState = handoff.State;
        handoff.State = nextState;
        var now = DateTimeOffset.UtcNow;
        handoff.LastStateChangeLogUtc = now;
        if (nextState is V6TransportHandoffState.FrontierRepairOnly
            or V6TransportHandoffState.BackfillRepair
            or V6TransportHandoffState.Recovered)
        {
            handoff.LastProofUtc = now;
        }
        else if (nextState != V6TransportHandoffState.TransportProofPending)
        {
            handoff.LastProofUtc ??= now;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_state_changed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(handoff.Kind)}; source_transport={FormatFileTransferTransportKind(handoff.SourceTransport)}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; previous_state={FormatV6TransportHandoffState(previousState)}; state={FormatV6TransportHandoffState(nextState)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        if (nextState == V6TransportHandoffState.WaitingForTargetTransport)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_handoff_waiting_for_target_transport; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(handoff.Kind)}; source_transport={FormatFileTransferTransportKind(handoff.SourceTransport)}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        }
        else if (nextState == V6TransportHandoffState.Recovered)
        {
            var elapsedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - handoff.StartedUtc).TotalMilliseconds);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_handoff_recovered; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(handoff.Kind)}; source_transport={FormatFileTransferTransportKind(handoff.SourceTransport)}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; elapsed_ms={elapsedMs}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; starting_committed_chunk={handoff.StartingCommittedChunkIndex}");
            if (handoff.TargetTransport == FileTransferTransportKind.RegularNkn)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=tuna_disable_handoff_nkn_ready; session_id={sessionId}; reason={FormatProtocolLogValue(handoff.Reason)}; proof=v6_frontier_recovered; lanes=file");
            }
        }

        return true;
    }

    private static DateTimeOffset GetV6TransportHandoffActivityUtc(TransportHandoffEpoch handoff)
        => handoff.LastProofUtc ??
           handoff.TargetReadyUtc ??
           handoff.LastStateChangeLogUtc ??
           handoff.StartedUtc;

    private static void MarkV6TransportHandoffPeerActivity(TransportHandoffEpoch? handoff)
    {
        if (handoff is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        handoff.TargetReadyUtc ??= now;
        handoff.LastProofUtc = now;
    }

    private bool CompleteOutboundV6TransportHandoffLocked(
        OutboundTransferContext context,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        var handoff = context.V6TransportHandoff;
        if (handoff is null)
        {
            return false;
        }

        TrySetV6TransportHandoffState(
            handoff,
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            V6TransportHandoffState.Recovered,
            reason,
            committedChunkIndex,
            highestObservedChunkIndex);

        context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
            context.LastRecoveredV6TransportHandoffEpoch,
            handoff.EpochId);
        TrySettleOutboundFallbackFrontierProgressLocked(
            context,
            context.RemoteNextExpectedChunkIndex,
            committedChunkIndex,
            $"transport_handoff_completed_{reason}");
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            $"transport_handoff_completed_{reason}");
        var discardedRepairFrameCount = context.PullV4SenderPumpRepairQueue.Count;
        var discardedRepairChunkCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.InFlight = false;
        }

        context.V6TransportHandoff = null;
        if (!context.PullPostTunaRecoveryActive)
        {
            context.PullTransportRebindGeneration = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        }
        context.V4SenderCreditExhaustedSinceUtc = null;
        context.PullSenderFeedCreditWaitStartedUtc = null;
        context.SparseSenderPumpLastWakeReason = "v6_handoff_recovered";
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SignalSparseSenderPump();
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_tail_unblocked; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={handoff.EpochId}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; discarded_repair_frame_count={discardedRepairFrameCount}; discarded_repair_chunk_count={discardedRepairChunkCount}");
        return true;
    }

    private static bool CompleteInboundV6TransportHandoffLocked(
        InboundTransferContext context,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        var handoff = context.V6TransportHandoff;
        if (handoff is null)
        {
            return false;
        }

        TrySetV6TransportHandoffState(
            handoff,
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            V6TransportHandoffState.Recovered,
            reason,
            committedChunkIndex,
            highestObservedChunkIndex);

        context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
            context.LastRecoveredV6TransportHandoffEpoch,
            handoff.EpochId);
        context.V6TransportHandoff = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_tail_unblocked; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={handoff.EpochId}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}");
        return true;
    }

    private static void LogV6TransportHandoffEpochStarted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        TransportHandoffEpoch handoff)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_epoch_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(handoff.Kind)}; source_transport={FormatFileTransferTransportKind(handoff.SourceTransport)}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; reason={FormatProtocolLogValue(handoff.Reason)}; state={FormatV6TransportHandoffState(handoff.State)}; starting_committed_chunk={handoff.StartingCommittedChunkIndex}; starting_highest_observed_chunk={handoff.StartingHighestObservedChunkIndex}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_handoff_target_ready; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; reason={FormatProtocolLogValue(handoff.Reason)}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_transport_proof_pending; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={handoff.EpochId}; target_transport={FormatFileTransferTransportKind(handoff.TargetTransport)}; reason={FormatProtocolLogValue(handoff.Reason)}");
    }

    private bool TryPauseOutboundTransportLocked(OutboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (HasBootstrapSourceHoldLocked(context)) { InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused"); }
        if (context.IsTerminal)
        {
            return false;
        }

        if (IsTunaActivationNegotiationTransportPauseReason(reason) &&
            ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_tuna_activation_pause_suppressed; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}");
            return false;
        }

        if (IsTunaActivationNegotiationTransportPauseReason(reason) &&
            !ClearOutboundV4RepairWorkForTunaActivationPauseLocked(context, reason))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_tuna_activation_pause_rejected; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (context.PullTransportPaused)
        {
            if (!IsTunaActivationNegotiationTransportPauseReason(reason) ||
                IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
            {
                return false;
            }

            context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
            context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
            context.PullTransportPauseReason = reason;
            context.PullTransportLastPauseReason = reason;
            context.PullTransportResumeRequestPending = false;
            context.SparseSenderPumpLastWakeReason = "tuna_activation_barrier";
            InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused");
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportLastPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused");
        return true;
    }

    private bool TryPauseInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (HasInboundFallbackRepairHandoffLocked(context)) { return false; }
        if (HasBootstrapSourceHoldLocked(context)) { InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused"); }
        if (context.IsTerminal)
        {
            return false;
        }

        if (IsTunaActivationNegotiationTransportPauseReason(reason) &&
            ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_post_tuna_fallback_tuna_activation_pause_suppressed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; recovery_active={(context.PullPostTunaRecoveryActive ? 1 : 0)}");
            return false;
        }

        if (IsTunaActivationNegotiationTransportPauseReason(reason) &&
            (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                 context,
                 $"tuna_activation_pause_{reason}") ||
             !SettleAndClearRegularV4FrontierRepairContractLocked(
                 context,
                 FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
                 $"tuna_activation_pause_{reason}")))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_tuna_activation_pause_rejected; direction=inbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (context.PullTransportPaused)
        {
            if (!IsTunaActivationNegotiationTransportPauseReason(reason) ||
                IsTunaActivationNegotiationTransportPauseReason(context.PullTransportPauseReason))
            {
                return false;
            }

            context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
            context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
            context.PullTransportPauseReason = reason;
            context.PullTransportResumeRequestPending = false;
            InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused");
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        InvalidatePendingBootstrapCorrelationLocked(CreateCoordinatorStateLocked(context), "bootstrap_transport_paused");
        return true;
    }

    private bool TryStartOutboundV6TransportEpochWhileUnavailableLocked(
        OutboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !requiresResumeRequest)
        {
            return false;
        }

        if (handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
        }

        if (!CanUseV6TransportEpochsLocked(context))
        {
            return false;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (TryHoldOutboundPostTunaRecoveryEpochRestartLocked(
                context,
                reason,
                requiresResumeRequest,
                handoffKind,
                targetTransport))
        {
            return false;
        }
        if (targetTransport == FileTransferTransportKind.RegularNkn &&
            IsPrimaryRegularNknBulkV6ContextLocked(context))
        {
            context.PullTransportResumeRequestPending = true;
            context.SparseSenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind_waiting_for_available";
            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_bulk_v6_rebind_deferred_until_available; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; target_transport=regular_nkn; rebind_generation={context.PullTransportRebindGeneration}");
            return false;
        }

        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current) &&
            current.Kind == handoffKind &&
            current.TargetTransport == targetTransport)
        {
            LogV6TransportEpochReused(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, reason);
            return true;
        }

        if (TrySuppressOutboundRecoveredV6RegularNknEpochRestartPauseLocked(
                context,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportLiveRouteEpochId,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        context.PullTransportResumeRequestPending = true;
        context.PullTransportRebindGeneration++;
        context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.SparseSenderPumpLastWakeReason = "transport_handoff_unavailable";
        StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
        if (IsTunaFallbackTransportPauseReason(reason))
        {
            StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);
        }

        LogOutboundV6TransportEpochWaitingForRequests(context, reason);
        return IsV6TransportEpochUnresolved(context.V6TransportEpoch);
    }

    private bool TryStartInboundV6TransportEpochWhileUnavailableLocked(
        InboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !requiresResumeRequest)
        {
            return false;
        }

        if (handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
        }

        if (!CanUseV6TransportEpochsLocked(context))
        {
            return false;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (TryHoldInboundPostTunaRecoveryEpochRestartLocked(
                context,
                reason,
                requiresResumeRequest,
                handoffKind,
                targetTransport))
        {
            return false;
        }
        if (targetTransport == FileTransferTransportKind.RegularNkn &&
            IsPrimaryRegularNknBulkV6ContextLocked(context))
        {
            context.PullTransportResumeRequestPending = true;
            LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_primary_regular_nkn_bulk_v6_rebind_deferred_until_available; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; target_transport=regular_nkn; rebind_generation={context.PullTransportRebindGeneration}");
            return false;
        }

        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current) &&
            current.Kind == handoffKind &&
            current.TargetTransport == targetTransport)
        {
            context.V6ReceiverTransportEpoch = current.EpochId;
            LogV6TransportEpochReused(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, reason);
            return true;
        }

        if (TrySuppressInboundRecoveredV6RegularNknEpochRestartPauseLocked(
                context,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportLiveRouteEpochId,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        context.PullTransportResumeRequestPending = true;
        context.PullTimeoutOldestChunkIndex = null;
        context.PullTimeoutStreak = 0;
        context.PullFirstChunkTimeoutCount = 0;
        context.PullRecoverySinceUtc = null;
        context.PullTransportRebindGeneration++;
        context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
        context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        StartInboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
        if (IsTunaFallbackTransportPauseReason(reason))
        {
            StartInboundPostTunaRecoveryLocked(context, reason);
        }

        return IsV6TransportEpochUnresolved(context.V6TransportEpoch);
    }

    private bool TryHoldOutboundPostTunaRecoveryEpochRestartLocked(
        OutboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var state = CreateCoordinatorStateLocked(context);
        var authority = FileTransferCoordinator
            .GetCurrentReceiveRecoveryOperationAuthority(state);
        if (authority is null)
        {
            return false;
        }

        return DecidePostTunaRecoveryEpochRestartLocked(
            state,
            authority.Value,
            reason,
            requiresResumeRequest,
            handoffKind,
            targetTransport,
            (status, decisionReason, command, exactAuthority) =>
                RecordReceiveRecoveryAttemptSnapshotLocked(
                    context,
                    state,
                    nameof(FileTransferCoordinator.ClassifyPostTunaReceiveRecoveryEpochRestart),
                    status,
                    decisionReason,
                    command,
                    exactAuthority));
    }

    private bool TryHoldInboundPostTunaRecoveryEpochRestartLocked(
        InboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var state = CreateCoordinatorStateLocked(context);
        var authority = FileTransferCoordinator
            .GetCurrentReceiveRecoveryOperationAuthority(state);
        if (authority is null)
        {
            return false;
        }

        return DecidePostTunaRecoveryEpochRestartLocked(
            state,
            authority.Value,
            reason,
            requiresResumeRequest,
            handoffKind,
            targetTransport,
            (status, decisionReason, command, exactAuthority) =>
                RecordReceiveRecoveryAttemptSnapshotLocked(
                    context,
                    state,
                    nameof(FileTransferCoordinator.ClassifyPostTunaReceiveRecoveryEpochRestart),
                    status,
                    decisionReason,
                    command,
                    exactAuthority));
    }

    private static bool DecidePostTunaRecoveryEpochRestartLocked(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryOperationAuthority authority,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        Action<string, string, object,
            FileTransferReceiveRecoveryOperationAuthority> recordSnapshot)
    {
        var observedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var restartObservation =
            new FileTransferReceiveRecoveryPostTunaEpochRestartObservation(
                authority,
                IsAvailable: false,
                requiresResumeRequest,
                handoffKind,
                targetTransport,
                reason,
                observedUtcMs);
        var decision = FileTransferCoordinator
            .ClassifyPostTunaReceiveRecoveryEpochRestart(
                state,
                restartObservation);
        recordSnapshot(
            decision.Disposition.ToString(),
            decision.Reason,
            restartObservation,
            authority);
        var operation = decision.Operation;
        var observation = authority.Observation;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_post_tuna_epoch_restart_decided; disposition={decision.Disposition}; receive_recovery_operation_authority_id={FormatProtocolLogValue(authority.AuthorityId.Value)}; receive_recovery_operation_generation={authority.OperationGeneration}; execution_state={operation?.ExecutionState.ToString() ?? "(none)"}; proof_identity={FormatProtocolLogValue(operation?.ReceiveProof?.ProofIdentity)}; immutable_deadline_utc_ms={decision.ImmutableDeadlineUtcMs}; is_available=0; requires_resume_request={(requiresResumeRequest ? 1 : 0)}; availability_reason={FormatProtocolLogValue(reason)}; handoff_kind={FormatProtocolLogValue(handoffKind.ToString())}; target_transport={FormatProtocolLogValue(targetTransport.ToString())}; session_id={FormatProtocolLogValue(observation.SessionId)}; transfer_id={FormatProtocolLogValue(observation.TransferId)}; direction={observation.Direction.ToString().ToLowerInvariant()}; route={FormatProtocolLogValue(observation.RouteToken)}; protocol_version={observation.ProtocolVersion}; live_route_epoch={observation.LiveRouteEpoch}; route_publication_generation={observation.RoutePublication.PublicationGeneration}; runtime_generation={observation.RuntimeOwner.RuntimeGeneration}; terminal_generation={observation.TerminalGeneration}; leg_generation={observation.LegGeneration}; transport_epoch={observation.TransportEpoch}; bridge_recovery_generation={observation.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(observation.CheckpointRequestId)}; data_session_identity={FormatProtocolLogValue(observation.DataSessionIdentity)}; data_session_generation={observation.DataSessionGeneration}; proposed_runtime_generation={observation.RuntimeOwner.RuntimeGeneration + 1}; proposed_route_publication_generation={observation.RoutePublication.PublicationGeneration + 1}; proposed_leg_generation={observation.LegGeneration + 1}; outcome={(decision.SuppressesRestart ? "preserved" : "rejected")}; reason={FormatProtocolLogValue(decision.Reason)}");
        return decision.SuppressesRestart;
    }

    private bool TrySuppressOutboundRecoveredV6RegularNknEpochRestartPauseLocked(
        OutboundTransferContext context,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string reason)
    {
        if (context.IsTerminal ||
            !CanUseV6TransportEpochsLocked(context) ||
            handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
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
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        ClearOutboundRecoveredV6RegularNknEpochSuppressedPauseLocked(context, reason, handoffKind, targetTransport);
        return true;
    }

    private bool TrySuppressInboundRecoveredV6RegularNknEpochRestartPauseLocked(
        InboundTransferContext context,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string reason)
    {
        if (context.IsTerminal ||
            !CanUseV6TransportEpochsLocked(context) ||
            handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (!ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportLiveRouteEpochId,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        ClearInboundRecoveredV6RegularNknEpochSuppressedPauseLocked(context, reason, handoffKind, targetTransport);
        return true;
    }

    private void ClearOutboundRecoveredV6RegularNknEpochSuppressedPauseLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var wasPaused = context.PullTransportPaused;
        var previousPauseReason = context.PullTransportPauseReason;
        context.PullTransportLastResumeReason = reason;
        context.PullTransportLastResumeUtc = DateTimeOffset.UtcNow;
        context.PullTransportLastPauseReason = previousPauseReason ?? context.PullTransportLastPauseReason;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.PullSenderFeedCreditWaitStartedUtc = null;
        context.V4SenderCreditExhaustedSinceUtc = null;
        context.V6SenderPumpLastWakeReason = "recovered_regular_nkn_epoch_restart_suppressed";
        context.SparseSenderPumpLastWakeReason = "recovered_regular_nkn_epoch_restart_suppressed";
        context.SignalSparseSenderPump();
        TryPublishRecoveredRegularNknLiveRouteEpochAfterSuppressedRestartLocked(
            context,
            reason);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_pause_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteRuntime.TelemetryToken}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; reason={FormatProtocolLogValue(reason)}; was_paused={(wasPaused ? 1 : 0)}");
        PublishRecoveredV6TransportEpochSnapshotAfterSuppressedRestart(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.LastRecoveredV6TransportEpoch,
            context.LastRecoveredV6TransportEpochKind,
            context.LastRecoveredV6TransportTargetTransport,
            ++context.V6RecoveryBoundaryGeneration,
            reason,
            handoffKind,
            targetTransport);
    }

    private void ClearInboundRecoveredV6RegularNknEpochSuppressedPauseLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var wasPaused = context.PullTransportPaused;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportRebindStartedUtc = null;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        context.PullTimeoutOldestChunkIndex = null;
        context.PullTimeoutStreak = 0;
        context.PullFirstChunkTimeoutCount = 0;
        context.PullRecoverySinceUtc = null;
        TryPublishRecoveredRegularNknLiveRouteEpochAfterSuppressedRestartLocked(
            context,
            reason);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_pause_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteRuntime.TelemetryToken}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; reason={FormatProtocolLogValue(reason)}; was_paused={(wasPaused ? 1 : 0)}");
        PublishRecoveredV6TransportEpochSnapshotAfterSuppressedRestart(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.LastRecoveredV6TransportEpoch,
            context.LastRecoveredV6TransportEpochKind,
            context.LastRecoveredV6TransportTargetTransport,
            ++context.V6RecoveryBoundaryGeneration,
            reason,
            handoffKind,
            targetTransport);
    }

    private void PublishRecoveredV6TransportEpochSnapshotAfterSuppressedRestart(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long recoveredTransportEpoch,
        FileTransferTransportHandoffKind recoveredHandoffKind,
        FileTransferTransportKind recoveredTargetTransport,
        long recoveryBoundaryGeneration,
        string reason,
        FileTransferTransportHandoffKind requestedHandoffKind,
        FileTransferTransportKind requestedTargetTransport)
    {
        if (transport is not IFileTransferV6TransportEpochObserver observer ||
            recoveredTransportEpoch <= 0 ||
            recoveredTargetTransport != FileTransferTransportKind.RegularNkn ||
            recoveredHandoffKind is not FileTransferTransportHandoffKind.TunaToNormalFallback and
                not FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            return;
        }

        var sourceTransport = recoveredHandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback
            ? FileTransferTransportKind.Tuna
            : FileTransferTransportKind.Unknown;
        var snapshot = new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                direction,
                recoveredTransportEpoch,
                recoveredHandoffKind,
                sourceTransport,
                FileTransferTransportKind.RegularNkn,
                V6TransportEpochState.Recovered,
                reason,
                IsUnresolved: false)
            {
                RecoveryBoundaryGeneration = recoveryBoundaryGeneration,
            };
        try
        {
            observer.ObserveFileTransferV6TransportEpoch(snapshot);
        }
        finally
        {
            snapshot.CompleteObservation();
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_snapshot_published; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; recovered_transport_epoch={recoveredTransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(recoveredHandoffKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedHandoffKind)}; requested_target_transport={FormatFileTransferTransportKind(requestedTargetTransport)}; reason={FormatProtocolLogValue(reason)}");
    }

    private bool TryPublishRecoveredRegularNknLiveRouteEpochAfterSuppressedRestartLocked(
        OutboundTransferContext context,
        string reason)
    {
        var epoch = context.CurrentLiveRouteEpoch;
        if (epoch is null ||
            !epoch.RouteSelection.RuntimeDescriptor.UsesPostTunaFallbackV6Runtime ||
            epoch.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            string.Equals(epoch.State, "recovered", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(epoch.State, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
            context,
            "suppressed_restart_live_route_epoch_recovered",
            reason,
            routePublicationToken: epoch.RoutePublicationToken);
    }

    private bool TryPublishRecoveredRegularNknLiveRouteEpochAfterSuppressedRestartLocked(
        InboundTransferContext context,
        string reason)
    {
        var epoch = context.CurrentLiveRouteEpoch;
        if (epoch is null ||
            !epoch.RouteSelection.RuntimeDescriptor.UsesPostTunaFallbackV6Runtime ||
            epoch.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            string.Equals(epoch.State, "recovered", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(epoch.State, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
            context,
            "suppressed_restart_live_route_epoch_recovered",
            reason,
            routePublicationToken: epoch.RoutePublicationToken);
    }

    private static void LogOutboundV6TransportEpochWaitingForRequests(
        OutboundTransferContext context,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_recovery_waiting_for_receiver_requests; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={context.V6TransportEpoch?.EpochId ?? 0}; reason={FormatProtocolLogValue(reason)}");

    private bool TryResumeOutboundTransportLocked(
        OutboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (HasBootstrapSourceHoldLocked(context)) { return false; }
        if (context.IsTerminal)
        {
            return false;
        }

        if (!context.PullTransportPaused)
        {
            if (!requiresResumeRequest)
            {
                return false;
            }

            if (handoffKind == FileTransferTransportHandoffKind.None)
            {
                return false;
            }

            if (ShouldPromoteRegularNknV4ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                context.SparseSenderPumpLastWakeReason = "live_route_tuna_activated";
                TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
                return context.RouteRuntime.UsesFileTunaV4Runtime;
            }

            if (!CanUseV6TransportEpochsLocked(context))
            {
                return false;
            }

            targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
            if (targetTransport == FileTransferTransportKind.RegularNkn &&
                IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                context.PullTransportRebindGeneration++;
                context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
                context.PullTransportSafetyReplayRearmCount = 0;
                context.PullTransportFrontierOnlyRepairActive = false;
                context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
                context.V6RegularNknLastCheckpointSyncRequestedUtc = null;
                context.SparseSenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind";
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportLiveRouteEpochId,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return false;
            }

            if (ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                context.SparseSenderPumpLastWakeReason = "live_route_tuna_reactivated";
                return TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.SparseSenderPumpLastWakeReason = "transport_handoff";
            StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            if (IsTunaFallbackTransportPauseReason(reason))
            {
                StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);
            }

            LogOutboundV6TransportEpochWaitingForRequests(context, reason);
            return true;
        }

        var resumedPauseReason = context.PullTransportPauseReason;
        if (!ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context) &&
            ShouldDeferTunaActivationReadyResumeUntilRouteHandoff(
                resumedPauseReason,
                reason,
                handoffKind))
        {
            context.PullTransportResumeRequestPending = false;
            context.SparseSenderPumpLastWakeReason = "tuna_activation_handoff_pending";
            LogTunaActivationReadyResumeDeferred(FileTransferDirection.Outbound, context.TransferId, context.SessionId, reason);
            return false;
        }

        if (requiresResumeRequest &&
            ShouldPromoteRegularNknV4ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            context.PullTransportResumeRequestPending = false;
            context.SparseSenderPumpLastWakeReason = "live_route_tuna_activated";
            TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                context,
                reason,
                handoffKind,
                targetTransport);
            return context.RouteRuntime.UsesFileTunaV4Runtime;
        }

        if (requiresResumeRequest &&
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            !CanUseV6TransportEpochsLocked(context))
        {
            return false;
        }

        context.PullTransportLastResumeReason = reason;
        context.PullTransportLastResumeUtc = DateTimeOffset.UtcNow;
        context.PullTransportLastPauseReason = resumedPauseReason ?? context.PullTransportLastPauseReason;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        if (requiresResumeRequest)
        {
            if (!CanUseV6TransportEpochsLocked(context))
            {
                context.PullTransportResumeRequestPending = false;
                context.SparseSenderPumpLastWakeReason = "transport_resumed";
                return true;
            }

            targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
            if (targetTransport == FileTransferTransportKind.RegularNkn &&
                IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                context.PullTransportRebindGeneration++;
                context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
                context.PullTransportSafetyReplayRearmCount = 0;
                context.PullTransportFrontierOnlyRepairActive = false;
                context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
                context.V6RegularNknLastCheckpointSyncRequestedUtc = null;
                context.SparseSenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind";
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteRuntime,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportLiveRouteEpochId,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return true;
            }

            if (ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                context.SparseSenderPumpLastWakeReason = "live_route_tuna_reactivated";
                return TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.SparseSenderPumpLastWakeReason = "transport_rebind";
            StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            if (IsTunaFallbackTransportPauseReason(reason))
            {
                StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);
            }

            LogOutboundV6TransportEpochWaitingForRequests(context, reason);
        }
        else
        {
            context.SparseSenderPumpLastWakeReason = "transport_resumed";
        }

        return true;
    }

    private bool TryResumeInboundTransportLocked(
        InboundTransferContext context,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (HasBootstrapSourceHoldLocked(context)) { return false; }
        if (HasInboundFallbackRepairHandoffLocked(context))
        {
            return requiresResumeRequest && ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                context.RouteRuntime, context.NegotiatedDataProtocolVersion, handoffKind, targetTransport) &&
                TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(context, reason, handoffKind, targetTransport);
        }
        if (context.IsTerminal)
        {
            return false;
        }

        if (!context.PullTransportPaused)
        {
            if (!requiresResumeRequest)
            {
                return false;
            }

            if (handoffKind == FileTransferTransportHandoffKind.None)
            {
                return false;
            }

            if (ShouldPromoteRegularNknV4ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                return TryPromoteInboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            if (!CanUseV6TransportEpochsLocked(context))
            {
                return false;
            }

            targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
            if (targetTransport == FileTransferTransportKind.RegularNkn &&
                IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                context.PullTransportRebindGeneration++;
                context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
                context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
                context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
                context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                context.PullTransportRebindRecoveredLogged = false;
                context.PullTransportRebindStableProgressSamples = 0;
                context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
                context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
                context.PullTransportRebindFrontierRepairCommittedChunks = 0;
                context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
                context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
                context.V6RegularNknLastCheckpointSyncRequestId = null;
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.RouteRuntime,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportLiveRouteEpochId,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return false;
            }

            if (ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                return TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            context.PullTimeoutOldestChunkIndex = null;
            context.PullTimeoutStreak = 0;
            context.PullFirstChunkTimeoutCount = 0;
            context.PullRecoverySinceUtc = null;
            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
            context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
            context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            context.PullTransportRebindRecoveredLogged = false;
            context.PullTransportRebindStableProgressSamples = 0;
            context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
            context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
            context.PullTransportRebindFrontierRepairCommittedChunks = 0;
            context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
            context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
            StartInboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            if (IsTunaFallbackTransportPauseReason(reason))
            {
                StartInboundPostTunaRecoveryLocked(context, reason);
            }

            return true;
        }

        var resumedPauseReason = context.PullTransportPauseReason;
        if (!ShouldSuppressTunaActivationPauseForPostTunaFallbackLocked(context) &&
            ShouldDeferTunaActivationReadyResumeUntilRouteHandoff(
                resumedPauseReason,
                reason,
                handoffKind))
        {
            context.PullTransportResumeRequestPending = false;
            LogTunaActivationReadyResumeDeferred(FileTransferDirection.Inbound, context.TransferId, context.SessionId, reason);
            return false;
        }

        if (requiresResumeRequest &&
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            !CanUseV6TransportEpochsLocked(context))
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
        if (requiresResumeRequest)
        {
            if (ShouldPromoteRegularNknV4ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                return TryPromoteInboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            if (!CanUseV6TransportEpochsLocked(context))
            {
                context.PullTransportResumeRequestPending = false;
                return true;
            }

            targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
            if (targetTransport == FileTransferTransportKind.RegularNkn &&
                IsPrimaryRegularNknBulkV6ContextLocked(context))
            {
                context.PullTransportRebindGeneration++;
                context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
                context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
                context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
                context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                context.PullTransportRebindRecoveredLogged = false;
                context.PullTransportRebindStableProgressSamples = 0;
                context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
                context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
                context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
                context.PullTransportRebindFrontierRepairCommittedChunks = 0;
                context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
                context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
                context.V6RegularNknLastCheckpointSyncRequestId = null;
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteRuntime,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportLiveRouteEpochId,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return true;
            }

            if (ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                return TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
            context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
            context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            context.PullTransportRebindRecoveredLogged = false;
            context.PullTransportRebindStableProgressSamples = 0;
            context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
            context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
            context.PullTransportRebindFrontierRepairCommittedChunks = 0;
            context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
            context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
            StartInboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            if (IsTunaFallbackTransportPauseReason(reason))
            {
                StartInboundPostTunaRecoveryLocked(context, reason);
            }
        }

        return true;
    }

    private static bool CanUseV6TransportEpochsLocked(OutboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6;

    private static bool CanUseV6TransportEpochsLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6;

    private static bool ShouldPromotePostTunaFallbackV6ToFileTunaV4(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int negotiatedProtocolVersion,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => FileTransferCoordinator.CanTransitionToRoute(
            routeRuntime,
            negotiatedProtocolVersion,
            FileTransferRoute.FileTunaV4,
            handoffKind,
            targetTransport);

    private static bool ShouldPromoteRegularNknV4ToFileTunaV4(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int negotiatedProtocolVersion,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => FileTransferCoordinator.CanTransitionToRoute(
            routeRuntime,
            negotiatedProtocolVersion,
            FileTransferRoute.FileTunaV4,
            handoffKind,
            targetTransport);

    private static bool IsFallbackSurvivalProofPending(OutboundTransferContext context)
        => !RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_pending", "proof_pending_check").Granted;

    private static bool IsFallbackSurvivalProofPending(InboundTransferContext context)
        => !RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_pending", "proof_pending_check").Granted;

    private static string FormatRuntimeUnlockFallbackAdmissionRejection(
        FileTransferDirection direction,
        RuntimeUnlockAdmissionDecision admission)
    {
        if (admission.Granted)
        {
            return "none";
        }

        if (admission.Status == RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner)
        {
            return direction == FileTransferDirection.Inbound
                ? "inbound_fallback_tail_not_yielded"
                : "outbound_fallback_tail_not_yielded";
        }

        return admission.Reason;
    }

    private static RuntimeUnlockFallbackSurvivalState BuildRuntimeUnlockFallbackSurvivalStateLocked(
        OutboundTransferContext context)
    {
        var fallbackTailRepairPending =
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
            (context.PullTransportFrontierOnlyRepairActive ||
             IsV6TransportHandoffBlockingTail(context.V6TransportHandoff));

        return new RuntimeUnlockFallbackSurvivalState(
            FallbackActive: context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6,
            FallbackCurrent: IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg),
            FallbackTerminalized: context.CurrentTransferLeg?.State == FileTransferLegState.Terminal,
            CheckpointDeliveryRecoveryPending: context.V6RegularNknFallbackCheckpointDeliveryRecoveryPending,
            StateRefreshSendInFlight: context.V6RegularNknStateRefreshSendInFlight != 0,
            DeferredStateRefreshPending: context.V6RegularNknDeferredStateRefreshRequest is not null,
            ProofReplayPending: false,
            ControlPlanePressurePending: false,
            BridgeRecoveryPending: context.PullPostTunaRecoveryActive,
            FallbackTailRepairPending: fallbackTailRepairPending,
            PairedFallbackYieldGranted: false);
    }

    private static RuntimeUnlockFallbackSurvivalState BuildRuntimeUnlockFallbackSurvivalStateLocked(
        InboundTransferContext context)
    {
        var proofReplayPending =
            context.V6PostTunaFallbackProofReplayReceiverStatePending ||
            context.V6PostTunaFallbackProofReplayFrontierRequestPending;
        var fallbackTailRepairPending =
            context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
            IsV6TransportHandoffBlockingTail(context.V6TransportHandoff);

        return new RuntimeUnlockFallbackSurvivalState(
            FallbackActive: context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6,
            FallbackCurrent: IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg),
            FallbackTerminalized: context.CurrentTransferLeg?.State == FileTransferLegState.Terminal,
            CheckpointDeliveryRecoveryPending: false,
            StateRefreshSendInFlight: false,
            DeferredStateRefreshPending: false,
            ProofReplayPending: proofReplayPending,
            ControlPlanePressurePending: !string.IsNullOrWhiteSpace(context.PostTunaFallbackControlPlanePressureTuple),
            BridgeRecoveryPending: context.PullPostTunaRecoveryActive,
            FallbackTailRepairPending: fallbackTailRepairPending,
            PairedFallbackYieldGranted: false);
    }

    private static RuntimeUnlockAdmissionDecision RequestRuntimeUnlockAdmissionLocked(
        OutboundTransferContext context,
        string source,
        string reason,
        bool transactionFailed = false)
    {
        var survivalState = BuildRuntimeUnlockFallbackSurvivalStateLocked(context);
        var survivalDecision = FileTransferCoordinator.Apply(
            RuntimeUnlockCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                survivalState: survivalState),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, survivalDecision);

        var admissionDecision = FileTransferCoordinator.Apply(
            RuntimeUnlockCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockAdmission,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                admissionRequest: new RuntimeUnlockAdmissionRequest(
                    source,
                    reason,
                    context.CurrentRuntimeOwner,
                    transactionFailed)),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, admissionDecision);

        var admission = admissionDecision.RuntimeUnlockAdmissionDecision ??
            new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
                "runtime_unlock_admission_missing");
        _ = TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            CreateCoordinatorStateLocked(context),
            out var admissionScope);
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission,
            admissionScope);
        return admission;
    }

    private static RuntimeUnlockAdmissionDecision RequestRuntimeUnlockAdmissionLocked(
        InboundTransferContext context,
        string source,
        string reason,
        bool transactionFailed = false)
    {
        var survivalState = BuildRuntimeUnlockFallbackSurvivalStateLocked(context);
        var survivalDecision = FileTransferCoordinator.Apply(
            RuntimeUnlockCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                survivalState: survivalState),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, survivalDecision);

        var admissionDecision = FileTransferCoordinator.Apply(
            RuntimeUnlockCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockAdmission,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                admissionRequest: new RuntimeUnlockAdmissionRequest(
                    source,
                    reason,
                    context.CurrentRuntimeOwner,
                    transactionFailed)),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, admissionDecision);

        var admission = admissionDecision.RuntimeUnlockAdmissionDecision ??
            new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
                "runtime_unlock_admission_missing");
        _ = TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            CreateCoordinatorStateLocked(context),
            out var admissionScope);
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission,
            admissionScope);
        UpdateInboundPostFallbackPairedAdmissionRecheckLocked(
            context,
            source,
            reason,
            admission);
        if (admission.Granted)
        {
            AttachInitialReceiverBarrierAdmissionLocked(context);
        }
        return admission;
    }

    private static void UpdateInboundPostFallbackPairedAdmissionRecheckLocked(
        InboundTransferContext context,
        string source,
        string reason,
        RuntimeUnlockAdmissionDecision admission)
    {
        var pending = context.RuntimeUnlockPostFallbackPairedAdmissionRecheck;
        if (admission.Granted)
        {
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
            return;
        }

        if (context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            context.DataSession is null)
        {
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
            return;
        }

        if (admission.Status == RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner &&
            string.Equals(
                admission.Reason,
                "paired_fallback_tail_yield_missing",
                StringComparison.Ordinal))
        {
            if (GetCurrentFallbackTailYieldOperation(context) is not
                {
                    Decision.IsDefault: false,
                    Scope.HasCompleteExecutionScope: true,
                } operation ||
                operation.PairingState == FileTransferFallbackTailYieldPairingState.Paired ||
                !string.Equals(
                    operation.Scope.DataSessionIdentity,
                    CreateReceiveRecoveryDataSessionIdentity(
                        context.TransferId,
                        context.DataSession,
                        context.DataSessionGeneration),
                    StringComparison.Ordinal) ||
                operation.Scope.DataSessionGeneration != context.DataSessionGeneration)
            {
                ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
                return;
            }

            if (pending is null ||
                pending.YieldDecision != operation.Decision ||
                pending.SourceScope != operation.Scope ||
                !ReferenceEquals(pending.DataSession, context.DataSession) ||
                !string.Equals(pending.RequestSource, source, StringComparison.Ordinal) ||
                !string.Equals(pending.RequestReason, reason, StringComparison.Ordinal))
            {
                pending = new RuntimeUnlockPostFallbackPairedAdmissionRecheck
                {
                    RecheckGeneration = Math.Max(
                        1,
                        context.LastRuntimeUnlockPostFallbackPairedAdmissionRecheckGeneration + 1),
                    RequestSource = source,
                    RequestReason = reason,
                    YieldDecision = operation.Decision,
                    SourceScope = operation.Scope,
                    DataSession = context.DataSession,
                    LastEvaluatedSurvivalState = admission.FallbackSurvivalState,
                    LastDecisionReason = admission.Reason,
                };
                context.LastRuntimeUnlockPostFallbackPairedAdmissionRecheckGeneration =
                    pending.RecheckGeneration;
                context.RuntimeUnlockPostFallbackPairedAdmissionRecheck = pending;
            }
            else
            {
                pending.LastEvaluatedSurvivalState = admission.FallbackSurvivalState;
                pending.LastDecisionReason = admission.Reason;
            }

            MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                context,
                reason,
                FormatRuntimeUnlockFallbackAdmissionRejection(
                    FileTransferDirection.Inbound,
                    admission));
            return;
        }

        if (pending is not null &&
            admission.Status == RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner)
        {
            pending.LastEvaluatedSurvivalState = admission.FallbackSurvivalState;
            pending.LastDecisionReason = admission.Reason;
            MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                context,
                pending.RequestReason,
                FormatRuntimeUnlockFallbackAdmissionRejection(
                    FileTransferDirection.Inbound,
                    admission));
            return;
        }

        ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
    }

    private static void EnsureInboundPostFallbackPairedAdmissionRecheckLocked(
        InboundTransferContext context,
        FileTransferFallbackTailYieldOperation operation,
        FileTransferFallbackTailPairedReleaseReceipt pairedRelease,
        FileTransferFallbackTailYieldScope scope)
    {
        if (context.RuntimeUnlockPostFallbackPairedAdmissionRecheck is not null ||
            HasCurrentRuntimeUnlockAdmissionLocked(context) ||
            context.DataSession is null)
        {
            return;
        }

        var pending = new RuntimeUnlockPostFallbackPairedAdmissionRecheck
        {
            RecheckGeneration = Math.Max(
                1,
                context.LastRuntimeUnlockPostFallbackPairedAdmissionRecheckGeneration + 1),
            RequestSource = "fallback_tail_pairing_committed",
            RequestReason = "fallback_quiescent",
            YieldDecision = operation.Decision,
            SourceScope = scope,
            DataSession = context.DataSession,
            PairedRelease = pairedRelease,
            LastEvaluatedSurvivalState =
                BuildRuntimeUnlockFallbackSurvivalStateLocked(context) with
                {
                    PairedFallbackYieldGranted = true,
                },
            LastDecisionReason = "paired_fallback_tail_yield_missing",
        };
        if (!TryGetCurrentInboundPostFallbackPairedAdmissionScopeLocked(
                context,
                pending,
                out var currentOperation,
                out var currentPairedRelease,
                out _) ||
            currentOperation != operation ||
            currentPairedRelease != pairedRelease)
        {
            return;
        }

        context.LastRuntimeUnlockPostFallbackPairedAdmissionRecheckGeneration =
            pending.RecheckGeneration;
        context.RuntimeUnlockPostFallbackPairedAdmissionRecheck = pending;
    }

    private static void ClearInboundPostFallbackPairedAdmissionRecheckLocked(
        InboundTransferContext context,
        RuntimeUnlockPostFallbackPairedAdmissionRecheck? pending)
    {
        if (pending is null ||
            !ReferenceEquals(
                context.RuntimeUnlockPostFallbackPairedAdmissionRecheck,
                pending))
        {
            return;
        }

        context.RuntimeUnlockPostFallbackPairedAdmissionRecheck = null;
        if (context.RuntimeUnlockWaitingForFallbackSurvival &&
            string.Equals(
                context.RuntimeUnlockWaitingForFallbackSurvivalReason,
                pending.RequestReason,
                StringComparison.Ordinal))
        {
            context.RuntimeUnlockWaitingForFallbackSurvival = false;
            context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
            context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        }
    }

    private static void TryRecheckInboundPostFallbackPairedAdmissionLocked(
        InboundTransferContext context,
        string trigger)
    {
        var pending = context.RuntimeUnlockPostFallbackPairedAdmissionRecheck;
        if (pending is null)
        {
            return;
        }

        if (!TryGetCurrentInboundPostFallbackPairedAdmissionScopeLocked(
                context,
                pending,
                out _,
                out var pairedRelease,
                out var staleReason))
        {
            LogInboundPostFallbackPairedAdmissionRecheckDecision(
                context,
                pending,
                pending.PairedRelease,
                "Stale",
                trigger,
                staleReason,
                admissionGeneration: 0);
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
            return;
        }

        if (pending.PairedRelease is { } priorRelease && priorRelease != pairedRelease)
        {
            LogInboundPostFallbackPairedAdmissionRecheckDecision(
                context,
                pending,
                priorRelease,
                "Stale",
                trigger,
                "paired_release_replaced",
                admissionGeneration: 0);
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
            return;
        }

        pending.PairedRelease = pairedRelease;
        if (HasCurrentRuntimeUnlockAdmissionLocked(context))
        {
            var admissionGeneration = context.RuntimeUnlockAdmissionToken?.AdmissionGeneration ?? 0;
            LogInboundPostFallbackPairedAdmissionRecheckDecision(
                context,
                pending,
                pairedRelease,
                "Idempotent",
                trigger,
                "paired_admission_already_current",
                admissionGeneration);
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
            return;
        }

        var currentSurvival = BuildRuntimeUnlockFallbackSurvivalStateLocked(context) with
        {
            PairedFallbackYieldGranted = true,
        };
        if (pending.LastEvaluatedSurvivalState == currentSurvival &&
            !string.Equals(
                pending.LastDecisionReason,
                "paired_fallback_tail_yield_missing",
                StringComparison.Ordinal))
        {
            LogInboundPostFallbackPairedAdmissionRecheckDecision(
                context,
                pending,
                pairedRelease,
                "Held",
                trigger,
                pending.LastDecisionReason,
                admissionGeneration: 0);
            return;
        }

        var admission = RequestRuntimeUnlockAdmissionLocked(
            context,
            "paired_fallback_tail_yield_admission_recheck",
            pending.RequestReason);
        var status = admission.Granted
            ? "Granted"
            : admission.Status == RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner
                ? "Held"
                : "Stale";
        LogInboundPostFallbackPairedAdmissionRecheckDecision(
            context,
            pending,
            pairedRelease,
            status,
            trigger,
            admission.Reason,
            admission.AdmissionToken?.AdmissionGeneration ?? 0);

        if (admission.Granted)
        {
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
        }
        else if (admission.Status != RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner)
        {
            ClearInboundPostFallbackPairedAdmissionRecheckLocked(context, pending);
        }
    }

    private static bool TryGetCurrentInboundPostFallbackPairedAdmissionScopeLocked(
        InboundTransferContext context,
        RuntimeUnlockPostFallbackPairedAdmissionRecheck pending,
        out FileTransferFallbackTailYieldOperation operation,
        out FileTransferFallbackTailPairedReleaseReceipt pairedRelease,
        out string reason)
    {
        operation = default;
        pairedRelease = default;
        reason = "paired_admission_source_not_current";
        if (context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            context.CurrentTransferLeg is not { } leg ||
            context.CurrentRuntimeOwner is not { } owner ||
            context.CurrentRoutePublicationToken is not { } publication ||
            context.DataSession is null ||
            !ReferenceEquals(context.DataSession, pending.DataSession))
        {
            return false;
        }

        if (GetCurrentFallbackTailYieldOperation(context) is not
            {
                PairingState: FileTransferFallbackTailYieldPairingState.Paired,
                PairedRelease: { IsDefault: false } currentPairedRelease,
            } currentOperation ||
            currentOperation.Decision != pending.YieldDecision ||
            FileTransferCoordinator.GetEffectiveFallbackTailYieldScope(
                currentOperation) != pending.SourceScope)
        {
            reason = "paired_admission_pairing_not_current";
            return false;
        }

        var scope = pending.SourceScope;
        if (scope.RuntimeOwner != owner ||
            scope.RoutePublication != publication ||
            scope.TerminalGeneration != context.TerminalGeneration ||
            scope.LegGeneration != leg.Generation ||
            scope.TransportEpoch != leg.TransportEpochId ||
            scope.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            scope.CheckpointGeneration != leg.CheckpointGeneration ||
            !string.Equals(
                scope.CheckpointRequestIdSnapshot,
                leg.CheckpointRequestId ?? "(none)",
                StringComparison.Ordinal) ||
            !string.Equals(
                scope.DataSessionIdentity,
                CreateReceiveRecoveryDataSessionIdentity(
                    context.TransferId,
                    context.DataSession,
                    context.DataSessionGeneration),
                StringComparison.Ordinal) ||
            scope.DataSessionGeneration != context.DataSessionGeneration)
        {
            reason = "paired_admission_scope_mutated";
            return false;
        }

        operation = currentOperation;
        pairedRelease = currentPairedRelease;
        reason = "paired_admission_scope_current";
        return true;
    }

    private static void LogInboundPostFallbackPairedAdmissionRecheckDecision(
        InboundTransferContext context,
        RuntimeUnlockPostFallbackPairedAdmissionRecheck pending,
        FileTransferFallbackTailPairedReleaseReceipt? pairedRelease,
        string status,
        string trigger,
        string reason,
        int admissionGeneration)
    {
        var scope = pending.SourceScope;
        var release = pairedRelease.GetValueOrDefault();
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_post_fallback_paired_admission_recheck_decided; decision_status={FormatProtocolLogValue(status)}; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; pending_recheck_generation={pending.RecheckGeneration}; request_source={FormatProtocolLogValue(pending.RequestSource)}; request_reason={FormatProtocolLogValue(pending.RequestReason)}; admission_generation={admissionGeneration}; authority_id={FormatProtocolLogValue(pending.YieldDecision.AuthorityId.ToString())}; yield_generation={pending.YieldDecision.YieldGeneration}; owner_key={FormatProtocolLogValue(scope.OwnerKey)}; pair_key={FormatProtocolLogValue(scope.PairKey)}; release_authority_id={FormatProtocolLogValue(release.IsDefault ? "(none)" : release.ReleaseAuthorityId.ToString())}; release_generation={(release.IsDefault ? 0 : release.ReleaseGeneration)}; pairing_generation={(release.IsDefault ? 0 : release.PairingGeneration)}; local_proof_identity={FormatProtocolLogValue(release.IsDefault ? "(none)" : release.LocalProof.ProofIdentity)}; peer_observation_identity={FormatProtocolLogValue(release.IsDefault ? "(none)" : release.PeerProof.ObservationIdentity)}; route={FormatProtocolLogValue(scope.RouteToken)}; protocol_version={scope.ProtocolVersion}; live_route_epoch={scope.LiveRouteEpoch}; runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; terminal_generation={scope.TerminalGeneration}; leg_generation={scope.LegGeneration}; route_publication_generation={scope.RoutePublication?.PublicationGeneration ?? 0}; route_commit_generation={scope.RoutePublication?.RouteCommitGeneration ?? 0}; transport_epoch={scope.TransportEpoch}; bridge_recovery_generation={scope.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(scope.CheckpointRequestIdSnapshot)}; checkpoint_generation={scope.CheckpointGeneration}; data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity ?? "(none)")}; data_session_generation={scope.DataSessionGeneration}; trigger={FormatProtocolLogValue(trigger)}; authority_effect=none; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static FileTransferCoordinatorEvent RuntimeUnlockCoordinatorEvent(
        FileTransferCoordinatorEventKind kind,
        FileTransferRouteSelection routeSelection,
        FileTransferLeg? leg,
        string reason,
        RuntimeUnlockAdmissionRequest? admissionRequest = null,
        RuntimeUnlockFallbackSurvivalState? survivalState = null)
        => new(
            kind,
            routeSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            reason,
            leg?.State ?? FileTransferLegState.Active,
            leg?.CanSendData ?? true,
            leg?.ProvenCommittedChunkIndex ?? 0,
            leg?.ProvenHighestObservedChunkIndex ?? -1,
            leg?.TransportEpochId ?? 0,
            leg?.BridgeRecoveryGeneration ?? 0,
            RuntimeUnlockAdmissionRequest: admissionRequest,
            RuntimeUnlockFallbackSurvivalState: survivalState);

    private static bool IsFallbackSurvivalOwnerPendingAfterAcceptedProof(OutboundTransferContext context)
        => !RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_after_proof", "accepted_proof_check").Granted;

    private static bool IsFallbackSurvivalOwnerPendingAfterAcceptedProof(InboundTransferContext context)
        => !RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_after_proof", "accepted_proof_check").Granted;

    private bool TryHandleOutboundRuntimeUnlockPreCommitProbeLaunchSnapshotLocked(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason)
    {
        var snapshotContextValid = TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
            snapshot,
            context.SessionId,
            context.TransferId,
            out var snapshotRejectionReason);
        if (snapshot.LaunchGeneration <= 0 ||
            !ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            !string.Equals(dataSession.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(dataSession.TransferId, context.TransferId, StringComparison.Ordinal) ||
            !snapshotContextValid ||
            string.IsNullOrWhiteSpace(snapshot.TransferId) ||
            !IsRuntimeUnlockRegularV4RouteCommitLaunchSnapshot(snapshot, context))
        {
            RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                context,
                snapshot,
                context.IsTerminal ? "Invalidated" : "Rejected",
                snapshot.LaunchGeneration <= 0
                    ? "launch_generation_missing"
                    : snapshotContextValid
                        ? "launch_snapshot_not_current"
                        : snapshotRejectionReason,
                drainGeneration: 0);
            return false;
        }

        if (context.LastRuntimeUnlockPreCommitProbeLaunchDisposition is { } previous &&
            snapshot.LaunchGeneration <= previous.LaunchGeneration)
        {
            if (snapshot.LaunchGeneration == previous.LaunchGeneration &&
                previous.Snapshot == snapshot)
            {
                LogRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
                    context,
                    snapshot,
                    "Idempotent",
                    "duplicate_launch_handoff",
                    previous.DrainGeneration);
                return true;
            }

            LogRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
                context,
                snapshot,
                snapshot.LaunchGeneration == previous.LaunchGeneration
                    ? "Rejected"
                    : "Invalidated",
                snapshot.LaunchGeneration == previous.LaunchGeneration
                    ? "launch_generation_tuple_mutation"
                    : "launch_generation_stale",
                drainGeneration: 0);
            return false;
        }

        return TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            "runtime_unlock_probe_missing",
            snapshot);
    }

    private bool TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason)
        => TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            rejectionReason,
            null);

    private bool TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackWithSnapshotLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason,
        RuntimeUnlockRouteCommitSnapshot? snapshot)
        => TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            rejectionReason,
            snapshot);

    private bool TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason,
        RuntimeUnlockRouteCommitSnapshot? snapshot)
    {
        var admission = TryReuseRuntimeUnlockRegularV4RouteCommitAdmissionLocked(
                context,
                snapshot,
                "precommit_probe_start",
                reason,
                out var existingAdmission)
            ? existingAdmission
            : RequestRuntimeUnlockAdmissionLocked(context, "precommit_probe_start", reason);
        if (!admission.Granted)
        {
            if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
            {
                MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                    context,
                    reason,
                    FormatRuntimeUnlockFallbackAdmissionRejection(FileTransferDirection.Outbound, admission));
            }

            NotifyRuntimeUnlockPreCommitProbeLaunchHeldByCoordinatorDenial(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                admission,
                snapshot,
                "precommit_probe_start",
                reason);
            if (snapshot is { LaunchGeneration: > 0 })
            {
                RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                    context,
                    snapshot,
                    "Held",
                    admission.Reason,
                    drainGeneration: 0);
            }
            return true;
        }

        var launchSnapshot = snapshot is { LaunchGeneration: > 0 };
        if (!launchSnapshot)
        {
            GrantOutboundRuntimeUnlockActivationWindowLocked(context, admission, reason);
        }

        var started = StartOutboundRuntimeUnlockPreCommitProbe(
            context,
            admission.AdmissionToken!.Value,
            reason,
            out var startRejectionReason,
            out var drainGeneration,
            pendingReplay: null,
            acceptedSnapshot: snapshot);
        if (!started)
        {
            if (snapshot is { LaunchGeneration: > 0 })
            {
                RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                    context,
                    snapshot,
                    context.IsTerminal ? "Invalidated" : "Rejected",
                    startRejectionReason,
                    drainGeneration);
            }

            return false;
        }

        if (launchSnapshot)
        {
            GrantOutboundRuntimeUnlockActivationWindowLocked(context, admission, reason);
        }
        if (snapshot is { LaunchGeneration: > 0 })
        {
            RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                context,
                snapshot,
                "Accepted",
                "route_commit_send_drain_staged",
                drainGeneration);
        }

        return true;
    }

    private bool TryReuseRuntimeUnlockRegularV4RouteCommitAdmissionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot? snapshot,
        string source,
        string reason,
        out RuntimeUnlockAdmissionDecision admission)
    {
        admission = default;
        if (snapshot is not { } currentSnapshot)
        {
            return false;
        }

        RuntimeUnlockAdmissionToken token;
        {
            var coordinatorState = CreateCoordinatorStateLocked(context);
            if (context.RuntimeUnlockAdmissionToken is not
                    { IsDefault: false } currentAdmission ||
                context.RuntimeUnlockPendingPreCommitProbe is not
                    { IsDefault: false } currentPending ||
                currentPending.AdmissionToken != currentAdmission ||
                !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                    coordinatorState,
                    currentAdmission,
                    out _) ||
                !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                    coordinatorState,
                    currentPending,
                    out _) ||
                !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                    currentSnapshot,
                    currentPending,
                    out _))
            {
                return false;
            }

            token = currentAdmission;
        }

        admission = new RuntimeUnlockAdmissionDecision(
            RuntimeUnlockAdmissionStatus.Granted,
            token.Reason,
            token,
            token.Route,
            token.ProtocolVersion,
            token.LiveRouteEpoch,
            token.RuntimeGeneration,
            token.TerminalGeneration,
            token.LegGeneration,
            token.FallbackSurvivalState);
        _ = TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            CreateCoordinatorStateLocked(context),
            out var admissionScope);
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission,
            admissionScope);
        return true;
    }

    private void RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string status,
        string decisionReason,
        long drainGeneration)
    {
        var normalizedReason = NormalizeReason(decisionReason) ?? "unknown";
        var previous = context.LastRuntimeUnlockPreCommitProbeLaunchDisposition;
        var mayReplacePrevious = previous is null ||
                                 snapshot.LaunchGeneration > previous.LaunchGeneration ||
                                 (snapshot.LaunchGeneration == previous.LaunchGeneration &&
                                  previous.Snapshot == snapshot &&
                                  string.Equals(status, "Invalidated", StringComparison.Ordinal) &&
                                  !string.Equals(previous.Status, "Invalidated", StringComparison.Ordinal));
        if (mayReplacePrevious)
        {
            context.LastRuntimeUnlockPreCommitProbeLaunchDisposition =
                new RuntimeUnlockPreCommitProbeLaunchDisposition(
                    snapshot.LaunchGeneration,
                    snapshot,
                    status,
                    normalizedReason,
                    Math.Max(0, drainGeneration));
        }

        LogRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
            context,
            snapshot,
            status,
            decisionReason,
            drainGeneration);
    }

    private static void LogRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string status,
        string decisionReason,
        long drainGeneration)
    {
        var owner = context.CurrentRuntimeOwner;
        var publication = context.CurrentRoutePublicationToken;
        var leg = context.CurrentTransferLeg;
        var admission = context.RuntimeUnlockAdmissionToken;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; transaction_generation={snapshot.TransactionGeneration}; offer_generation={snapshot.OfferGeneration}; tuna_path_lease_generation={snapshot.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(snapshot.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={snapshot.LaunchGeneration}; status={FormatProtocolLogValue(status)}; drain_generation={Math.Max(0, drainGeneration)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={owner?.RuntimeGeneration ?? 0}; terminal_generation={context.TerminalGeneration}; leg_generation={leg?.Generation ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; bridge_decision_generation={context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0}; route_publication_generation={publication?.PublicationGeneration ?? 0}; route_commit_generation={publication?.RouteCommitGeneration ?? 0}; data_session_generation={context.DataSessionGeneration}; admission_generation={admission?.AdmissionGeneration ?? 0}; target_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(snapshot.TargetRoute))}; target_protocol_version={snapshot.ProtocolVersion}; target_transport={FormatFileTransferTransportKind(snapshot.TargetTransport)}; handoff_kind={FormatFileTransferTransportHandoffKind(snapshot.HandoffKind)}; decision_reason={FormatProtocolLogValue(decisionReason)}");
    }

    private static void LogRuntimeUnlockPreCommitProbeLaunchHandoffWithoutContext(
        IFileTransferDataSession dataSession,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string status,
        string decisionReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=outbound; session_id={FormatProtocolLogValue(snapshot.SessionId)}; transfer_id={FormatProtocolLogValue(snapshot.TransferId ?? dataSession.TransferId)}; transaction_generation={snapshot.TransactionGeneration}; offer_generation={snapshot.OfferGeneration}; tuna_path_lease_generation={snapshot.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(snapshot.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={snapshot.LaunchGeneration}; status={FormatProtocolLogValue(status)}; drain_generation=0; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=0; route_commit_generation=0; data_session_generation=0; admission_generation=0; target_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(snapshot.TargetRoute))}; target_protocol_version={snapshot.ProtocolVersion}; target_transport={FormatFileTransferTransportKind(snapshot.TargetTransport)}; handoff_kind={FormatFileTransferTransportHandoffKind(snapshot.HandoffKind)}; decision_reason={FormatProtocolLogValue(decisionReason)}");

    private bool TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
        InboundTransferContext context,
        string reason,
        string rejectionReason)
        => TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            rejectionReason,
            null);

    private bool TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackWithSnapshotLocked(
        InboundTransferContext context,
        string reason,
        string rejectionReason,
        RuntimeUnlockRouteCommitSnapshot? snapshot)
        => TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            rejectionReason,
            snapshot);

    private bool TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
        InboundTransferContext context,
        string reason,
        string rejectionReason,
        RuntimeUnlockRouteCommitSnapshot? snapshot)
    {
        var admission = RequestRuntimeUnlockAdmissionLocked(context, "precommit_probe_responder", reason);
        if (!admission.Granted)
        {
            if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
            {
                MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                    context,
                    reason,
                    FormatRuntimeUnlockFallbackAdmissionRejection(FileTransferDirection.Inbound, admission));
            }

            NotifyRuntimeUnlockPreCommitProbeLaunchHeldByCoordinatorDenial(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                admission,
                snapshot,
                "precommit_probe_responder",
                reason);
            return true;
        }

        if (snapshot is { LaunchGeneration: > 0 } launchSnapshot &&
            context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            context.NegotiatedDataProtocolVersion ==
                FileTransferProtocol.ProtocolVersionV4 &&
            !TryPrepareInboundRegularV4ToTunaRouteTransitionLocked(
                context,
                launchSnapshot,
                reason,
                out _))
        {
            LogRuntimeUnlockRouteCommitDeferredProbeMissing(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                reason,
                launchSnapshot,
                "route_transition_not_prepared");
            return true;
        }

        GrantInboundRuntimeUnlockActivationWindowLocked(context, admission, reason);
        LogRuntimeUnlockPreCommitResponderWaitingForPeerProbe(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason,
            rejectionReason);
        return true;
    }

    private bool
        TryHandleInboundRuntimeUnlockResponderPeerProbeRouteTransitionHandoffLocked(
            InboundTransferContext context,
            IFileTransferDataSession dataSession,
            RuntimeUnlockRouteCommitSnapshot snapshot,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            string reason)
        => observation.ResponderTransitionMode switch
        {
            RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 =>
                TryHandleInboundRuntimeUnlockInitialResponderPeerProbeRouteTransitionHandoffLocked(
                    context,
                    dataSession,
                    snapshot,
                    observation,
                    reason),
            RuntimeUnlockResponderPeerProbeTransitionMode
                    .PostFallbackV6Paired =>
                TryHandleInboundRuntimeUnlockPostFallbackResponderPeerProbeRouteTransitionHandoffLocked(
                    context,
                    dataSession,
                    snapshot,
                    observation,
                    reason),
            _ => RejectInboundRuntimeUnlockResponderPeerProbeTransitionMode(
                context,
                observation),
        };

    private static bool RejectInboundRuntimeUnlockResponderPeerProbeTransitionMode(
        InboundTransferContext context,
        RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation)
    {
        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
            context,
            observation,
            null,
            null,
            "Rejected",
            "runtime_unlock_responder_peer_probe_transition_mode_invalid");
        return false;
    }

    private bool
        TryHandleInboundRuntimeUnlockInitialResponderPeerProbeRouteTransitionHandoffLocked(
            InboundTransferContext context,
            IFileTransferDataSession dataSession,
            RuntimeUnlockRouteCommitSnapshot snapshot,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            string reason)
    {
        var rejectionReason =
            "runtime_unlock_initial_responder_peer_probe_handoff_not_current";
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            snapshot.ResponderPeerProbeRouteTransitionObservation != observation ||
            !TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
                snapshot,
                context.SessionId,
                context.TransferId,
                out rejectionReason) ||
            !IsRuntimeUnlockSamePathProbeAcked(snapshot) ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV4)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                context.IsTerminal ? "terminal_context" : rejectionReason);
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                state,
                out var currentReceiveScope) ||
            currentReceiveScope != observation.PeerReceiveScope)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                "runtime_unlock_initial_responder_peer_probe_receive_scope_mismatch");
            return false;
        }

        var capturedAdmission = observation.PeerReceiveScope.Admission;
        string? snapshotRejectionReason = null;
        if (capturedAdmission.IsDefault ||
            context.RuntimeUnlockAdmissionToken is not
                { IsDefault: false } currentAdmission ||
            currentAdmission != capturedAdmission ||
            state.CurrentRuntimeUnlockAdmission != capturedAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                capturedAdmission,
                out rejectionReason) ||
            !RuntimeUnlockResponderPeerProbeSnapshotMatchesObservation(
                snapshot,
                observation,
                out snapshotRejectionReason) ||
            !FileTransferCoordinator
                .TryPrevalidateRuntimeUnlockInitialResponderPeerProbeRouteTransitionObservation(
                    state,
                    observation,
                    capturedAdmission,
                    out rejectionReason))
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                snapshotRejectionReason ??
                (string.Equals(rejectionReason, "current", StringComparison.Ordinal)
                    ? "runtime_unlock_initial_responder_peer_probe_handoff_not_current"
                    : rejectionReason));
            return false;
        }

        if (!TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out _,
                out _))
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                "runtime_unlock_initial_responder_peer_probe_precommit_authority_missing");
            return false;
        }

        var admission = RuntimeUnlockAdmissionDecisionFromToken(
            context,
            capturedAdmission,
            reason);
        if (!context.RuntimeUnlockActivationWindowGranted)
        {
            GrantInboundRuntimeUnlockActivationWindowLocked(
                context,
                admission,
                reason);
        }

        if (context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Held",
                "runtime_unlock_initial_responder_peer_probe_owner_work_pending");
            return true;
        }

        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
            context,
            observation,
            null,
            null,
            "Accepted",
            "runtime_unlock_initial_responder_peer_probe_handoff_current");
        return TryPrepareInboundRegularV4ToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }

    private bool
        TryHandleInboundRuntimeUnlockPostFallbackResponderPeerProbeRouteTransitionHandoffLocked(
            InboundTransferContext context,
            IFileTransferDataSession dataSession,
            RuntimeUnlockRouteCommitSnapshot snapshot,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            string reason)
    {
        var snapshotRejectionReason =
            "responder_peer_probe_handoff_not_current";
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            snapshot.ResponderPeerProbeRouteTransitionObservation != observation ||
            !TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
                snapshot,
                context.SessionId,
                context.TransferId,
                out snapshotRejectionReason) ||
            !IsRuntimeUnlockSamePathProbeAcked(snapshot) ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                context.IsTerminal
                    ? "terminal_context"
                    : snapshotRejectionReason);
            return false;
        }

        var admissionState = CreateCoordinatorStateLocked(context);
        if (!TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                admissionState,
                out var currentReceiveScope) ||
            currentReceiveScope != observation.PeerReceiveScope)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                "runtime_unlock_responder_peer_probe_receive_scope_mismatch");
            return false;
        }

        var capturedAdmission = observation.PeerReceiveScope.Admission;
        var admissionRejectionReason =
            "runtime_unlock_responder_peer_probe_receive_scope_mismatch";
        if (capturedAdmission.IsDefault ||
            context.RuntimeUnlockAdmissionToken is not
                { IsDefault: false } currentAdmission ||
            currentAdmission != capturedAdmission ||
            admissionState.CurrentRuntimeUnlockAdmission != capturedAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                admissionState,
                capturedAdmission,
                out admissionRejectionReason))
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                capturedAdmission.IsDefault ||
                context.RuntimeUnlockAdmissionToken != capturedAdmission ||
                admissionState.CurrentRuntimeUnlockAdmission != capturedAdmission
                    ? "runtime_unlock_responder_peer_probe_receive_scope_mismatch"
                    : admissionRejectionReason);
            return false;
        }

        var handoffPreflightRejectionReason =
            "runtime_unlock_responder_peer_probe_handoff_not_current";
        if (!RuntimeUnlockResponderPeerProbeSnapshotMatchesObservation(
                snapshot,
                observation,
                out var snapshotObservationRejectionReason) ||
            !FileTransferCoordinator
                .TryPrevalidateRuntimeUnlockResponderPeerProbeRouteTransitionObservation(
                    admissionState,
                    observation,
                    capturedAdmission,
                    out _,
                    out handoffPreflightRejectionReason))
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                snapshotObservationRejectionReason ??
                handoffPreflightRejectionReason);
            return false;
        }

        var admission = RuntimeUnlockAdmissionDecisionFromToken(
            context,
            capturedAdmission,
            reason);
        if (!context.RuntimeUnlockActivationWindowGranted)
        {
            GrantInboundRuntimeUnlockActivationWindowLocked(
                context,
                admission,
                reason);
        }

        if (!TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var admissionToken,
                out var pendingPreCommit))
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                null,
                null,
                "Rejected",
                "runtime_unlock_responder_peer_probe_precommit_authority_missing");
            return false;
        }

        var binding = FileTransferCoordinator
            .BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(
                CreateCoordinatorStateLocked(context),
                observation,
                admissionToken,
                pendingPreCommit);
        var bindingState = AdoptCoordinatorStateLocked(context, binding);
        if (!binding.Accepted || binding.Authority is not { } authority)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                binding.Authority,
                null,
                binding.Status.ToString(),
                binding.Reason,
                snapshotAttemptAlreadyRecorded: true);
            return binding.Status ==
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Held;
        }

        if (context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0)
        {
            var held = FileTransferCoordinator
                .HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork(
                    bindingState,
                    authority,
                    "runtime_unlock_responder_peer_probe_handoff_owner_work_pending");
            _ = AdoptCoordinatorStateLocked(context, held);
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                held.Authority,
                null,
                held.Status.ToString(),
                held.Reason,
                snapshotAttemptAlreadyRecorded: true);
            return held.Status ==
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Held;
        }

        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
            context,
            observation,
            authority,
            null,
            binding.Status.ToString(),
            binding.Reason,
            snapshotAttemptAlreadyRecorded: true);
        return TryPrepareInboundPostTunaFallbackToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }

    private static bool
        RuntimeUnlockResponderPeerProbeSnapshotMatchesObservation(
            RuntimeUnlockRouteCommitSnapshot snapshot,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            out string? rejectionReason)
    {
        rejectionReason = null;
        if (!string.Equals(
                snapshot.SessionId,
                observation.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.TransferId,
                observation.TransferId,
                StringComparison.Ordinal) ||
            snapshot.TransactionGeneration !=
                observation.TransactionGeneration ||
            snapshot.OfferGeneration != observation.OfferGeneration ||
            snapshot.LaunchGeneration != observation.LaunchGeneration ||
            !snapshot.PeerVisibleProof ||
            !snapshot.PeerReceived ||
            !snapshot.AnswerReceived ||
            !snapshot.TunaPathLeaseRequired ||
            !snapshot.TunaPathLeaseCurrent ||
            snapshot.TunaPathLeaseGeneration !=
                observation.TunaPathLeaseGeneration ||
            !string.Equals(
                snapshot.TunaPathLeaseListenerRunId,
                observation.TunaPathLeaseListenerRunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.PathProbeId,
                observation.ProbeId,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.PathProbeState,
                RuntimeUnlockPathProbeState.Acked.ToString(),
                StringComparison.Ordinal) ||
            snapshot.PathProbeTransport != observation.ProbeTransport ||
            snapshot.PathProbeAckedUtcMs != observation.ProbeAckedUtcMs ||
            !string.IsNullOrWhiteSpace(snapshot.PathProbeFailureReason) ||
            !string.Equals(
                snapshot.TunaPathLeaseState,
                RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(
                snapshot.TunaPathLeaseFailureReason) ||
            snapshot.TargetRoute != observation.TargetRoute ||
            snapshot.ProtocolVersion != observation.TargetProtocolVersion ||
            snapshot.HandoffKind != observation.HandoffKind ||
            snapshot.TargetTransport != observation.TargetTransport)
        {
            rejectionReason =
                "runtime_unlock_responder_peer_probe_snapshot_observation_mismatch";
            return false;
        }

        return true;
    }

    private bool
        TryRecheckHeldInboundRuntimeUnlockResponderPeerProbeRouteTransitionAfterOwnerWorkClearedLocked(
            InboundTransferContext context,
            string reason)
    {
        if (TryQueueInboundFileTransferRouteTransitionDrainAfterOwnerWorkClearedLocked(
                context,
                reason))
        {
            return true;
        }

        if (ReferenceEquals(inboundTransfer, context) &&
            !context.IsTerminal &&
            context.ReceiverSparseChunksPendingWrite.Count == 0 &&
            context.PendingChunks.Count == 0 &&
            context.DataSession is { } initialDataSession &&
            transport is IRuntimeUnlockRouteCommitProofProvider
                initialProofProvider &&
            initialProofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var initialSnapshot) &&
            initialSnapshot.ResponderPeerProbeRouteTransitionObservation is
            {
                ResponderTransitionMode:
                    RuntimeUnlockResponderPeerProbeTransitionMode
                        .InitialRegularV4,
            } initialObservation)
        {
            return TryHandleInboundRuntimeUnlockInitialResponderPeerProbeRouteTransitionHandoffLocked(
                context,
                initialDataSession,
                initialSnapshot,
                initialObservation,
                reason);
        }

        var handoff = context.CoordinatorPersistentState
            .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0 ||
            handoff.LastStatus !=
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Held ||
            handoff.CurrentAuthority is not
                { IsDefault: false } authority ||
            context.DataSession is null ||
            transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot) ||
            snapshot.ResponderPeerProbeRouteTransitionObservation is not
                { } observation ||
            observation != authority.Observation)
        {
            return false;
        }

        var binding = FileTransferCoordinator
            .BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(
                CreateCoordinatorStateLocked(context),
                observation,
                authority.RuntimeUnlockAdmission,
                authority.PendingPreCommitProbe);
        _ = AdoptCoordinatorStateLocked(context, binding);
        if (!binding.Accepted || binding.Authority is not { } currentAuthority)
        {
            LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
                context,
                observation,
                binding.Authority,
                null,
                binding.Status.ToString(),
                binding.Reason,
                snapshotAttemptAlreadyRecorded: true);
            return false;
        }

        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
            context,
            observation,
            currentAuthority,
            null,
            binding.Status.ToString(),
            binding.Reason,
            snapshotAttemptAlreadyRecorded: true);
        return TryPrepareInboundPostTunaFallbackToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }

    private static void
        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision(
            InboundTransferContext context,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority?
                responderAuthority,
            FileTransferRouteTransitionAuthority? transitionAuthority,
            string status,
            string decisionReason,
            bool snapshotAttemptAlreadyRecorded = false)
    {
        if (!snapshotAttemptAlreadyRecorded)
        {
            RecordResponderProbeHandoffAttemptSnapshotLocked(
                context,
                observation,
                status,
                decisionReason);
        }
        var owner = context.CurrentRuntimeOwner;
        var publication = context.CurrentRoutePublicationToken;
        var leg = context.CurrentTransferLeg;
        var pair = GetCurrentFallbackTailYieldOperation(context);
        var release = pair?.PairedRelease;
        var scope = pair is { } operation
            ? operation.Scope
            : default;
        var responderTransitionMode =
            FormatRuntimeUnlockResponderPeerProbeTransitionMode(
                observation.ResponderTransitionMode);
        var activationAuthorityMode =
            observation.ResponderTransitionMode ==
                RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4
                ? "initial_responder"
                : responderAuthority is null
                    ? "none"
                    : "responder";
        LogInboundRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
            context,
            observation,
            transitionAuthority,
            status,
            decisionReason);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; status={FormatProtocolLogValue(status)}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(decisionReason)}; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; transaction_generation={observation.TransactionGeneration}; offer_generation={observation.OfferGeneration}; pending_answer_ack_generation={observation.PendingAnswerAckGeneration}; payer_decision_id={observation.PayerDecisionId}; launch_generation={observation.LaunchGeneration}; responder_transition_mode={responderTransitionMode}; tuna_path_lease_generation={observation.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(observation.TunaPathLeaseListenerRunId ?? "(none)")}; lanes={FormatProtocolLogValue(observation.Lanes)}; answer_acceptance_source={FormatProtocolLogValue(observation.AnswerAcceptanceSource)}; ack_message_id={FormatProtocolLogValue(observation.AckMessageId ?? "(none)")}; ack_source_fingerprint={FormatProtocolLogValue(observation.AckSourceFingerprint ?? "(none)")}; peer_probe_frame_fingerprint={FormatProtocolLogValue(observation.PeerProbeFrameFingerprint ?? "(none)")}; probe_id={FormatProtocolLogValue(observation.ProbeId)}; probe_state={observation.ProbeState.ToString().ToLowerInvariant()}; probe_transport={FormatFileTransferTransportKind(observation.ProbeTransport)}; probe_acked_utc_ms={observation.ProbeAckedUtcMs}; fallback_yield_authority_id={FormatProtocolLogValue(release?.ReleaseAuthorityId.ToString() ?? "(none)")}; fallback_yield_generation={release?.ReleaseGeneration ?? 0}; pairing_generation={release?.PairingGeneration ?? 0}; pair_key={FormatProtocolLogValue(scope.PairKey ?? "(none)")}; pair_state={FormatProtocolLogValue(pair?.PairingState.ToString() ?? "None")}; local_yield_authority_id={FormatProtocolLogValue(release?.LocalProof.Decision.AuthorityId.ToString() ?? "(none)")}; local_yield_generation={release?.LocalProof.Decision.YieldGeneration ?? 0}; local_proof_identity={FormatProtocolLogValue(release?.LocalProof.ProofIdentity ?? "(none)")}; local_frame_fingerprint={FormatProtocolLogValue(release?.LocalProof.FrameFingerprint ?? "(none)")}; peer_yield_authority_id={FormatProtocolLogValue(release?.PeerProof.Proof.AuthorityId.ToString() ?? "(none)")}; peer_yield_generation={release?.PeerProof.Proof.YieldGeneration ?? 0}; peer_observation_identity={FormatProtocolLogValue(release?.PeerProof.ObservationIdentity ?? "(none)")}; peer_frame_fingerprint={FormatProtocolLogValue(release?.PeerProof.Proof.FrameFingerprint ?? "(none)")}; peer_owner_key={FormatProtocolLogValue(release?.PeerProof.Proof.OwnerKey ?? "(none)")}; peer_pair_key={FormatProtocolLogValue(release?.PeerProof.Proof.PairKey ?? "(none)")}; responder_handoff_authority_id={FormatProtocolLogValue(responderAuthority?.AuthorityId.ToString() ?? "(none)")}; responder_handoff_generation={responderAuthority?.AuthorityGeneration ?? 0}; admission_generation={context.RuntimeUnlockAdmissionToken?.AdmissionGeneration ?? 0}; pending_precommit_generation={context.RuntimeUnlockPendingPreCommitProbe?.PendingGeneration ?? 0}; transition_authority_id={FormatProtocolLogValue(transitionAuthority?.AuthorityId.ToString() ?? "(none)")}; transition_generation={transitionAuthority?.TransitionGeneration ?? 0}; activation_authority_mode={FormatProtocolLogValue(activationAuthorityMode)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={owner?.RuntimeGeneration ?? 0}; route_publication_generation={publication?.PublicationGeneration ?? 0}; route_commit_generation={publication?.RouteCommitGeneration ?? 0}; terminal_generation={context.TerminalGeneration}; leg_generation={leg?.Generation ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(scope.CheckpointRequestIdSnapshot ?? "(none)")}; checkpoint_generation={scope.CheckpointGeneration}; data_session_identity={FormatProtocolLogValue(CreateReceiveRecoveryDataSessionIdentity(context.TransferId, context.DataSession, context.DataSessionGeneration))}; data_session_generation={context.DataSessionGeneration}; target_route={FormatProtocolLogValue(FileTransferRouteResolver.FileTunaV4Token)}; target_protocol_version={FileTransferProtocol.ProtocolVersionV4}; target_transport={FormatFileTransferTransportKind(FileTransferTransportKind.Tuna)}");
    }

    private static void
        LogRuntimeUnlockResponderPeerProbeRouteTransitionHandoffWithoutContext(
            IFileTransferDataSession dataSession,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            string status,
            string decisionReason)
    {
        LogInboundRuntimeUnlockPreCommitProbeLaunchHandoffWithoutContext(
            dataSession,
            observation,
            status,
            decisionReason);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; status={FormatProtocolLogValue(status)}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(decisionReason)}; direction=inbound; session_id={FormatProtocolLogValue(observation.SessionId)}; transfer_id={FormatProtocolLogValue(observation.TransferId ?? dataSession.TransferId)}; transaction_generation={observation.TransactionGeneration}; offer_generation={observation.OfferGeneration}; pending_answer_ack_generation={observation.PendingAnswerAckGeneration}; payer_decision_id={observation.PayerDecisionId}; launch_generation={observation.LaunchGeneration}; responder_transition_mode={FormatRuntimeUnlockResponderPeerProbeTransitionMode(observation.ResponderTransitionMode)}; tuna_path_lease_generation={observation.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(observation.TunaPathLeaseListenerRunId ?? "(none)")}; lanes={FormatProtocolLogValue(observation.Lanes)}; answer_acceptance_source={FormatProtocolLogValue(observation.AnswerAcceptanceSource)}; ack_message_id={FormatProtocolLogValue(observation.AckMessageId ?? "(none)")}; ack_source_fingerprint={FormatProtocolLogValue(observation.AckSourceFingerprint ?? "(none)")}; peer_probe_frame_fingerprint={FormatProtocolLogValue(observation.PeerProbeFrameFingerprint ?? "(none)")}; probe_id={FormatProtocolLogValue(observation.ProbeId)}; probe_state={observation.ProbeState.ToString().ToLowerInvariant()}; probe_transport={FormatFileTransferTransportKind(observation.ProbeTransport)}; probe_acked_utc_ms={observation.ProbeAckedUtcMs}; fallback_yield_authority_id=(none); fallback_yield_generation=0; pairing_generation=0; pair_key=(none); pair_state=None; responder_handoff_authority_id=(none); responder_handoff_generation=0; admission_generation=0; pending_precommit_generation=0; transition_authority_id=(none); transition_generation=0; activation_authority_mode=none; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; route_publication_generation=0; route_commit_generation=0; terminal_generation=0; leg_generation=0; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; data_session_identity=(none); data_session_generation=0; target_route={FormatProtocolLogValue(FileTransferRouteResolver.FileTunaV4Token)}; target_protocol_version={FileTransferProtocol.ProtocolVersionV4}; target_transport={FormatFileTransferTransportKind(FileTransferTransportKind.Tuna)}");
    }

    private static void
        LogInboundRuntimeUnlockPreCommitProbeLaunchHandoffDecision(
            InboundTransferContext context,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            FileTransferRouteTransitionAuthority? transitionAuthority,
            string status,
            string decisionReason)
    {
        var owner = context.CurrentRuntimeOwner;
        var publication = context.CurrentRoutePublicationToken;
        var leg = context.CurrentTransferLeg;
        var admission = context.RuntimeUnlockAdmissionToken;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; transaction_generation={observation.TransactionGeneration}; offer_generation={observation.OfferGeneration}; responder_transition_mode={FormatRuntimeUnlockResponderPeerProbeTransitionMode(observation.ResponderTransitionMode)}; tuna_path_lease_generation={observation.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(observation.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={observation.LaunchGeneration}; status={FormatProtocolLogValue(status)}; drain_generation={transitionAuthority?.TransitionGeneration ?? 0}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={owner?.RuntimeGeneration ?? 0}; terminal_generation={context.TerminalGeneration}; leg_generation={leg?.Generation ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; bridge_decision_generation={context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0}; route_publication_generation={publication?.PublicationGeneration ?? 0}; route_commit_generation={publication?.RouteCommitGeneration ?? 0}; data_session_generation={context.DataSessionGeneration}; admission_generation={admission?.AdmissionGeneration ?? 0}; target_route={FormatProtocolLogValue(FileTransferRouteResolver.FileTunaV4Token)}; target_protocol_version={FileTransferProtocol.ProtocolVersionV4}; target_transport={FormatFileTransferTransportKind(FileTransferTransportKind.Tuna)}; handoff_kind={FormatFileTransferTransportHandoffKind(FileTransferTransportHandoffKind.NormalToTunaActivation)}; decision_reason={FormatProtocolLogValue(decisionReason)}");
    }

    private static void
        LogInboundRuntimeUnlockPreCommitProbeLaunchHandoffWithoutContext(
            IFileTransferDataSession dataSession,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
            string status,
            string decisionReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=inbound; session_id={FormatProtocolLogValue(observation.SessionId)}; transfer_id={FormatProtocolLogValue(observation.TransferId ?? dataSession.TransferId)}; transaction_generation={observation.TransactionGeneration}; offer_generation={observation.OfferGeneration}; tuna_path_lease_generation={observation.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(observation.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={observation.LaunchGeneration}; status={FormatProtocolLogValue(status)}; drain_generation=0; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=0; route_commit_generation=0; data_session_generation=0; admission_generation=0; target_route={FormatProtocolLogValue(FileTransferRouteResolver.FileTunaV4Token)}; target_protocol_version={FileTransferProtocol.ProtocolVersionV4}; target_transport={FormatFileTransferTransportKind(FileTransferTransportKind.Tuna)}; handoff_kind={FormatFileTransferTransportHandoffKind(FileTransferTransportHandoffKind.NormalToTunaActivation)}; decision_reason={FormatProtocolLogValue(decisionReason)}");

    private bool EnsureOutboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(
        OutboundTransferContext context,
        string reason)
    {
        RuntimeUnlockRouteCommitSnapshot? snapshot = null;
        if (transport is IRuntimeUnlockRouteCommitProofProvider proofProvider &&
            proofProvider.TryGetRuntimeUnlockRouteCommitProof(context.SessionId, context.TransferId, out var currentSnapshot))
        {
            snapshot = currentSnapshot;
            if (IsRuntimeUnlockSamePathProbeAcked(currentSnapshot))
            {
                return true;
            }
        }

        var deferredAction = snapshot is { } current && IsRuntimeUnlockPreCommitProbeStarted(current)
            ? "probe_already_started"
            : "start_probe";
        LogRuntimeUnlockRouteCommitDeferredProbeMissing(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            reason,
            snapshot,
            deferredAction);

        if (!string.Equals(deferredAction, "probe_already_started", StringComparison.Ordinal))
        {
            TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackWithSnapshotLocked(
                context,
                reason,
                "runtime_unlock_probe_missing",
                snapshot);
        }

        return false;
    }

    private bool EnsureInboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(
        InboundTransferContext context,
        string reason)
    {
        RuntimeUnlockRouteCommitSnapshot? snapshot = null;
        if (transport is IRuntimeUnlockRouteCommitProofProvider proofProvider &&
            proofProvider.TryGetRuntimeUnlockRouteCommitProof(context.SessionId, context.TransferId, out var currentSnapshot))
        {
            snapshot = currentSnapshot;
            if (IsRuntimeUnlockSamePathProbeAcked(currentSnapshot))
            {
                return true;
            }
        }

        var deferredAction = snapshot is { } current && IsRuntimeUnlockPreCommitProbeStarted(current)
            ? "probe_already_started"
            : "wait_for_peer_probe";
        LogRuntimeUnlockRouteCommitDeferredProbeMissing(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            reason,
            snapshot,
            deferredAction);

        if (!string.Equals(deferredAction, "probe_already_started", StringComparison.Ordinal))
        {
            TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackWithSnapshotLocked(
                context,
                reason,
                "runtime_unlock_probe_missing",
                snapshot);
        }

        return false;
    }

    private void NotifyRuntimeUnlockPreCommitProbeLaunchHeldByCoordinatorDenial(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeUnlockAdmissionDecision admission,
        RuntimeUnlockRouteCommitSnapshot? snapshot,
        string source,
        string reason)
    {
        if (transport is not IRuntimeUnlockPreCommitProbeLaunchObserver observer ||
            snapshot is null ||
            snapshot.TransactionGeneration <= 0 ||
            snapshot.OfferGeneration <= 0)
        {
            return;
        }

        observer.NotifyRuntimeUnlockPreCommitProbeLaunchHeld(
            new RuntimeUnlockPreCommitProbeLaunchHoldNotification(
                sessionId,
                transferId,
                direction,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                snapshot.TunaPathLeaseGeneration,
                source,
                reason,
                admission.Reason,
                admission.CurrentLiveRouteEpoch,
                admission.CurrentRuntimeGeneration,
                admission.CurrentTerminalGeneration,
                admission.CurrentLegGeneration));
    }

    private static bool IsRuntimeUnlockSamePathProbeAcked(RuntimeUnlockRouteCommitSnapshot snapshot)
        => snapshot.TargetRoute == FileTransferRoute.FileTunaV4 &&
           snapshot.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
           snapshot.TargetTransport == FileTransferTransportKind.Tuna &&
           string.Equals(snapshot.PathProbeState, RuntimeUnlockPathProbeState.Acked.ToString(), StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(snapshot.PathProbeId) &&
           snapshot.PathProbeTransport == FileTransferTransportKind.Tuna;

    private static bool IsRuntimeUnlockPreCommitProbeStarted(RuntimeUnlockRouteCommitSnapshot snapshot)
        => string.Equals(snapshot.PathProbeState, RuntimeUnlockPathProbeState.Started.ToString(), StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(snapshot.PathProbeId);

    private static void LogRuntimeUnlockRouteCommitDeferredProbeMissing(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string reason,
        RuntimeUnlockRouteCommitSnapshot? snapshot,
        string deferredAction)
    {
        var targetRoute = snapshot is null
            ? "(none)"
            : FormatRuntimeUnlockCallbackRouteToken(snapshot.TargetRoute);
        var targetTransport = snapshot is null
            ? "(none)"
            : FormatFileTransferTransportKind(snapshot.TargetTransport);
        var handoffKind = snapshot is null
            ? "(none)"
            : FormatFileTransferTransportHandoffKind(snapshot.HandoffKind);
        var pathProbeTransport = snapshot is null
            ? "(none)"
            : FormatFileTransferTransportKind(snapshot.PathProbeTransport);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_route_commit_deferred_probe_missing; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; deferred_action={FormatProtocolLogValue(deferredAction)}; transaction_generation={snapshot?.TransactionGeneration ?? 0}; offer_generation={snapshot?.OfferGeneration ?? 0}; peer_visible_proof={(snapshot?.PeerVisibleProof == true ? 1 : 0)}; answer_received={(snapshot?.AnswerReceived == true ? 1 : 0)}; tuna_path_lease_generation={snapshot?.TunaPathLeaseGeneration ?? 0}; tuna_path_lease_current={(snapshot?.TunaPathLeaseCurrent == true ? 1 : 0)}; tuna_path_lease_state={FormatProtocolLogValue(snapshot?.TunaPathLeaseState ?? "(none)")}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(snapshot?.TunaPathLeaseListenerRunId ?? "(none)")}; path_probe_state={FormatProtocolLogValue(snapshot?.PathProbeState ?? "(none)")}; path_probe_id={FormatProtocolLogValue(snapshot?.PathProbeId ?? "(none)")}; path_probe_transport={FormatProtocolLogValue(pathProbeTransport)}; path_probe_acked_utc_ms={snapshot?.PathProbeAckedUtcMs ?? 0}; target_route={FormatProtocolLogValue(targetRoute)}; target_protocol_version={snapshot?.ProtocolVersion ?? 0}; target_transport={FormatProtocolLogValue(targetTransport)}; handoff_kind={FormatProtocolLogValue(handoffKind)}");
    }

    private void TryResumeOutboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (GetCurrentFallbackTailYieldOperation(context)?.PairedRelease is
            not { } pairedRelease)
        {
            return;
        }

        TryQueueFallbackPairedTailYieldRuntimeUnlockRetryDispatchLocked(
            context,
            pairedRelease,
            reason);
    }

    private void TryResumeInboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
        InboundTransferContext context,
        string reason)
    {
        if (FileTransferCoordinator
            .IsFallbackTailRetryCycleSupersessionPending(
                CreateCoordinatorStateLocked(context)))
        {
            ResolveInboundFallbackTailSurvivalLocked(
                context,
                "pressure_rejected_precommit_probe_rollback");
        }

        TryRecheckInboundPostFallbackPairedAdmissionLocked(context, reason);

        if (GetCurrentFallbackTailYieldOperation(context)?.PairedRelease is
            not { } pairedRelease)
        {
            return;
        }

        TryQueueFallbackPairedTailYieldRuntimeUnlockRetryDispatchLocked(
            context,
            pairedRelease,
            reason);
    }

    private static void MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = true;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = reason;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc ??= DateTimeOffset.UtcNow;
        context.RuntimeUnlockActivationWindowGranted = false;
        context.RuntimeUnlockActivationWindowReason = null;
        context.RuntimeUnlockActivationWindowGrantedUtc = null;
        LogRuntimeUnlockWaitingForFallbackSurvival(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason,
            rejectionReason);
    }

    private static void MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
        InboundTransferContext context,
        string reason,
        string rejectionReason)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = true;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = reason;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc ??= DateTimeOffset.UtcNow;
        context.RuntimeUnlockActivationWindowGranted = false;
        context.RuntimeUnlockActivationWindowReason = null;
        context.RuntimeUnlockActivationWindowGrantedUtc = null;
        LogRuntimeUnlockWaitingForFallbackSurvival(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason,
            rejectionReason);
    }

    private static void GrantOutboundRuntimeUnlockActivationWindowLocked(
        OutboundTransferContext context,
        RuntimeUnlockAdmissionDecision admission,
        string reason)
    {
        if (!admission.Granted || admission.AdmissionToken is null)
        {
            return;
        }

        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = true;
        context.RuntimeUnlockActivationWindowReason = reason;
        context.RuntimeUnlockActivationWindowGrantedUtc = DateTimeOffset.UtcNow;
        context.RuntimeUnlockAdmissionToken = admission.AdmissionToken;
        if (IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg))
        {
            context.CurrentTransferLeg!.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
        }

        LogRuntimeUnlockActivationWindowGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason);
    }

    private static void GrantInboundRuntimeUnlockActivationWindowLocked(
        InboundTransferContext context,
        RuntimeUnlockAdmissionDecision admission,
        string reason)
    {
        if (!admission.Granted || admission.AdmissionToken is null)
        {
            return;
        }

        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = true;
        context.RuntimeUnlockActivationWindowReason = reason;
        context.RuntimeUnlockActivationWindowGrantedUtc = DateTimeOffset.UtcNow;
        context.RuntimeUnlockAdmissionToken = admission.AdmissionToken;
        if (IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg))
        {
            context.CurrentTransferLeg!.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
        }

        LogRuntimeUnlockActivationWindowGranted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason);
    }

    private static void ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(OutboundTransferContext context)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = false;
        context.RuntimeUnlockActivationWindowReason = null;
        context.RuntimeUnlockActivationWindowGrantedUtc = null;
        context.RuntimeUnlockAdmissionToken = null;
        context.RuntimeUnlockPendingPreCommitProbe = null;
    }

    private static void ClearInboundRuntimeUnlockFallbackSeparationStateLocked(InboundTransferContext context)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = false;
        context.RuntimeUnlockActivationWindowReason = null;
        context.RuntimeUnlockActivationWindowGrantedUtc = null;
        context.RuntimeUnlockAdmissionToken = null;
        context.RuntimeUnlockPendingPreCommitProbe = null;
        context.RuntimeUnlockPostFallbackPairedAdmissionRecheck = null;
    }

    private static void LogRuntimeUnlockWaitingForFallbackSurvival(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string rejectionReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_waiting_for_fallback_survival; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; state={FormatProtocolLogValue(leg is null ? "none" : FormatFileTransferLegState(leg.State))}; can_send_data={(leg?.CanSendData == true ? 1 : 0)}");

    private static void LogRuntimeUnlockFallbackSurvivalResumeHeld(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string rejectionReason,
        RuntimeUnlockAdmissionDecision admission)
        => LogRuntimeUnlockFallbackSurvivalResumeOutcome(
            "filetransfer_runtime_unlock_fallback_survival_resume_held",
            direction,
            transferId,
            sessionId,
            leg,
            reason,
            rejectionReason,
            admission);

    private static void LogRuntimeUnlockFallbackSurvivalResumeRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string rejectionReason,
        RuntimeUnlockAdmissionDecision admission)
        => LogRuntimeUnlockFallbackSurvivalResumeOutcome(
            "filetransfer_runtime_unlock_fallback_survival_resume_rejected",
            direction,
            transferId,
            sessionId,
            leg,
            reason,
            rejectionReason,
            admission);

    private static void LogRuntimeUnlockFallbackSurvivalResumeOutcome(
        string eventName,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string rejectionReason,
        RuntimeUnlockAdmissionDecision admission)
    {
        var survival = admission.FallbackSurvivalState;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; admission_generation={admission.AdmissionToken?.AdmissionGeneration ?? 0}; admission_reason={FormatProtocolLogValue(admission.Reason)}; route={FormatProtocolLogValue(FormatRuntimeUnlockAdmissionRouteToken(admission.CurrentRoute))}; protocol_version={admission.CurrentProtocolVersion}; live_route_epoch={admission.CurrentLiveRouteEpoch}; runtime_generation={admission.CurrentRuntimeGeneration}; terminal_generation={admission.CurrentTerminalGeneration}; leg_generation={admission.CurrentLegGeneration}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; fallback_active={(survival.FallbackActive ? 1 : 0)}; fallback_current={(survival.FallbackCurrent ? 1 : 0)}; fallback_terminalized={(survival.FallbackTerminalized ? 1 : 0)}; checkpoint_delivery_recovery_pending={(survival.CheckpointDeliveryRecoveryPending ? 1 : 0)}; state_refresh_send_in_flight={(survival.StateRefreshSendInFlight ? 1 : 0)}; deferred_state_refresh_pending={(survival.DeferredStateRefreshPending ? 1 : 0)}; proof_replay_pending={(survival.ProofReplayPending ? 1 : 0)}; control_plane_pressure_pending={(survival.ControlPlanePressurePending ? 1 : 0)}; bridge_recovery_pending={(survival.BridgeRecoveryPending ? 1 : 0)}; fallback_tail_repair_pending={(survival.FallbackTailRepairPending ? 1 : 0)}; paired_fallback_yield_granted={(survival.PairedFallbackYieldGranted ? 1 : 0)}");
    }

    private static void LogRuntimeUnlockAdmissionDecision(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string source,
        string reason,
        RuntimeUnlockAdmissionDecision admission,
        RuntimeUnlockPeerPreCommitAnswerAcceptanceScope currentScope = default)
    {
        var eventName = admission.Granted
            ? "filetransfer_runtime_unlock_admission_granted"
            : "filetransfer_runtime_unlock_admission_denied";
        var survival = admission.FallbackSurvivalState;
        var tokenGeneration = admission.AdmissionToken?.AdmissionGeneration ?? 0;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}; admission_generation={tokenGeneration}; admission_reason={FormatProtocolLogValue(admission.Reason)}; route={FormatProtocolLogValue(FormatRuntimeUnlockAdmissionRouteToken(admission.CurrentRoute))}; protocol_version={admission.CurrentProtocolVersion}; live_route_epoch={admission.CurrentLiveRouteEpoch}; runtime_generation={admission.CurrentRuntimeGeneration}; terminal_generation={admission.CurrentTerminalGeneration}; leg_generation={admission.CurrentLegGeneration}; runtime_owner_session_id={FormatProtocolLogValue(currentScope.RuntimeOwner.SessionId ?? "(none)")}; runtime_owner_transfer_id={FormatProtocolLogValue(currentScope.RuntimeOwner.TransferId ?? "(none)")}; runtime_owner_direction={currentScope.RuntimeOwner.Direction.ToString().ToLowerInvariant()}; runtime_owner_route={currentScope.RuntimeOwner.Route.ToString().ToLowerInvariant()}; runtime_owner_route_token={FormatProtocolLogValue(currentScope.RuntimeOwner.RouteToken ?? "(none)")}; runtime_owner_protocol_version={currentScope.RuntimeOwner.ProtocolVersion}; runtime_owner_live_route_epoch={currentScope.RuntimeOwner.LiveRouteEpoch}; runtime_owner_runtime_generation={currentScope.RuntimeOwner.RuntimeGeneration}; runtime_owner_terminal_generation={currentScope.RuntimeOwner.TerminalGeneration}; route_publication_session_id={FormatProtocolLogValue(currentScope.RoutePublication.SessionId ?? "(none)")}; route_publication_transfer_id={FormatProtocolLogValue(currentScope.RoutePublication.TransferId ?? "(none)")}; route_publication_direction={currentScope.RoutePublication.Direction.ToString().ToLowerInvariant()}; route_publication_route={currentScope.RoutePublication.Route.ToString().ToLowerInvariant()}; route_publication_route_token={FormatProtocolLogValue(currentScope.RoutePublication.RouteToken ?? "(none)")}; route_publication_protocol_version={currentScope.RoutePublication.ProtocolVersion}; route_publication_live_route_epoch={currentScope.RoutePublication.LiveRouteEpoch}; route_publication_runtime_generation={currentScope.RoutePublication.RuntimeGeneration}; route_publication_terminal_generation={currentScope.RoutePublication.TerminalGeneration}; route_publication_leg_generation={currentScope.RoutePublication.LegGeneration}; route_publication_commit_generation={currentScope.RoutePublication.RouteCommitGeneration ?? 0}; route_publication_generation={currentScope.RoutePublication.PublicationGeneration}; transport_epoch={currentScope.TransportEpoch}; bridge_recovery_generation={currentScope.BridgeRecoveryGeneration}; checkpoint_request_id={FormatProtocolLogValue(currentScope.CheckpointRequestId ?? "(none)")}; checkpoint_generation={currentScope.CheckpointGeneration}; data_session_identity={FormatProtocolLogValue(currentScope.DataSessionIdentity ?? "(none)")}; data_session_generation={currentScope.DataSessionGeneration}; fallback_active={(survival.FallbackActive ? 1 : 0)}; fallback_current={(survival.FallbackCurrent ? 1 : 0)}; fallback_terminalized={(survival.FallbackTerminalized ? 1 : 0)}; checkpoint_delivery_recovery_pending={(survival.CheckpointDeliveryRecoveryPending ? 1 : 0)}; state_refresh_send_in_flight={(survival.StateRefreshSendInFlight ? 1 : 0)}; deferred_state_refresh_pending={(survival.DeferredStateRefreshPending ? 1 : 0)}; proof_replay_pending={(survival.ProofReplayPending ? 1 : 0)}; control_plane_pressure_pending={(survival.ControlPlanePressurePending ? 1 : 0)}; bridge_recovery_pending={(survival.BridgeRecoveryPending ? 1 : 0)}; fallback_tail_repair_pending={(survival.FallbackTailRepairPending ? 1 : 0)}; paired_fallback_yield_granted={(survival.PairedFallbackYieldGranted ? 1 : 0)}");
    }

    private static string FormatRuntimeUnlockAdmissionRouteToken(FileTransferRoute route)
        => FileTransferRouteResolver.Resolve(route).TelemetryToken;

    private static void LogRuntimeUnlockAdmissionTokenRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string source,
        string reason,
        string rejectionReason,
        RuntimeUnlockAdmissionToken token)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_admission_denied; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}; admission_generation={token.AdmissionGeneration}; admission_reason={FormatProtocolLogValue(token.Reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; route={FormatProtocolLogValue(token.RouteToken)}; protocol_version={token.ProtocolVersion}; live_route_epoch={token.LiveRouteEpoch}; runtime_generation={token.RuntimeGeneration}; terminal_generation={token.TerminalGeneration}; leg_generation={token.LegGeneration}");

    private static void LogRuntimeUnlockActivationWindowGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_activation_window_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; state={FormatProtocolLogValue(leg is null ? "none" : FormatFileTransferLegState(leg.State))}; can_send_data={(leg?.CanSendData == true ? 1 : 0)}");

    private static void LogRuntimeUnlockPreCommitResponderWaitingForPeerProbe(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason,
        string rejectionReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_responder_waiting_for_peer_probe; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; state={FormatProtocolLogValue(leg is null ? "none" : FormatFileTransferLegState(leg.State))}; can_send_data={(leg?.CanSendData == true ? 1 : 0)}");

    private bool TryAuthorizeRuntimeUnlockRoutePublicationLocked(
        OutboundTransferContext context,
        RouteCommitToken routeCommitToken,
        string reason,
        out RoutePublicationToken routePublicationToken,
        out string rejectionReason)
    {
        if (!TryAuthorizeRoutePublicationLocked(
                context,
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                "runtime_unlock_route_publication",
                reason,
                routeCommitToken,
                out routePublicationToken,
                out rejectionReason))
        {
            LogRuntimeUnlockCallbackRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                "runtime_unlock_route_publication",
                reason,
                rejectionReason,
                context.CurrentRuntimeOwner,
                routeCommitToken.RuntimeOwner,
                routeCommitToken.RuntimeUnlockAdmission,
                routeCommitToken,
                routeCommitToken.TransactionGeneration,
                routeCommitToken.OfferGeneration,
                routeCommitToken.TunaPathLeaseGeneration,
                routeCommitToken.TunaPathLeaseListenerRunId,
                routeCommitToken.SamePathTunaProbeAckId,
                true,
                routeCommitToken.SamePathTunaProbeAckId,
                routeCommitToken.CommitGeneration,
                routeCommitToken.TargetRoute,
                routeCommitToken.TargetProtocolVersion,
                routeCommitToken.TargetTransport,
                context.RouteSelection.Route,
                context.RouteSelection.ProtocolVersion,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
                context.TerminalGeneration,
                context.CurrentTransferLeg?.Generation ?? 0);
            return false;
        }

        return true;
    }

    private bool TryAuthorizeRuntimeUnlockRoutePublicationLocked(
        InboundTransferContext context,
        RouteCommitToken routeCommitToken,
        string reason,
        out RoutePublicationToken routePublicationToken,
        out string rejectionReason)
    {
        if (!TryAuthorizeRoutePublicationLocked(
                context,
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                "runtime_unlock_route_publication",
                reason,
                routeCommitToken,
                out routePublicationToken,
                out rejectionReason))
        {
            LogRuntimeUnlockCallbackRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                "runtime_unlock_route_publication",
                reason,
                rejectionReason,
                context.CurrentRuntimeOwner,
                routeCommitToken.RuntimeOwner,
                routeCommitToken.RuntimeUnlockAdmission,
                routeCommitToken,
                routeCommitToken.TransactionGeneration,
                routeCommitToken.OfferGeneration,
                routeCommitToken.TunaPathLeaseGeneration,
                routeCommitToken.TunaPathLeaseListenerRunId,
                routeCommitToken.SamePathTunaProbeAckId,
                true,
                routeCommitToken.SamePathTunaProbeAckId,
                routeCommitToken.CommitGeneration,
                routeCommitToken.TargetRoute,
                routeCommitToken.TargetProtocolVersion,
                routeCommitToken.TargetTransport,
                context.RouteSelection.Route,
                context.RouteSelection.ProtocolVersion,
                context.CurrentLiveRouteEpoch?.EpochId ?? 0,
                context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
                context.TerminalGeneration,
                context.CurrentTransferLeg?.Generation ?? 0);
            return false;
        }

        return true;
    }

    private void PublishRuntimeUnlockRouteCommitResultIfAuthorizedLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        RouteCommitToken routeCommitToken,
        string reason,
        long completedSendDrainGeneration = 0)
    {
        if (ShouldPublishRuntimeUnlockRouteCommitResult(reason))
        {
            if (transport is IRuntimeUnlockRouteCommitProofProvider proofProvider)
            {
                proofProvider.NotifyRuntimeUnlockRouteCommitResult(
                    sessionId,
                    transferId,
                    direction,
                    routeCommitToken.TransactionGeneration,
                    routeCommitToken.OfferGeneration,
                    accepted: true,
                    reason);
            }
        }
        else
        {
            LocalOperationalLog.Info(
                "FileTransfer",
                $"event=filetransfer_runtime_unlock_route_commit_publication_deferred; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={routeCommitToken.TransactionGeneration}; offer_generation={routeCommitToken.OfferGeneration}; route_commit_generation={routeCommitToken.CommitGeneration}; reason={FormatProtocolLogValue(reason)}");
        }

        LogRuntimeUnlockRouteCommitAccepted(
            direction,
            transferId,
            sessionId,
            routeCommitToken.TransactionGeneration,
            routeCommitToken.OfferGeneration,
            reason,
            routeCommitToken.CommitGeneration,
            completedSendDrainGeneration);
    }

    private static bool ShouldPublishRuntimeUnlockRouteCommitResult(string reason)
    {
        var normalized = NormalizeReason(reason);
        return !string.Equals(normalized, RuntimeUnlockPreCommitProbeAcceptedReason, StringComparison.Ordinal) &&
               !string.Equals(normalized, RuntimeUnlockPreCommitProbeCommitConfirmedReason, StringComparison.Ordinal) &&
               !string.Equals(normalized, RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason, StringComparison.Ordinal);
    }

    private static void LogRuntimeUnlockRouteCommitAccepted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        string reason,
        int routeCommitGeneration = 0,
        long completedSendDrainGeneration = 0)
        => LocalOperationalLog.Info(
            "FileTransfer",
            $"event=filetransfer_runtime_unlock_route_commit_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; route_commit_generation={routeCommitGeneration}; completed_send_drain_generation={completedSendDrainGeneration}; reason={FormatProtocolLogValue(reason)}");

    private static void LogRuntimeUnlockRouteCommitRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string reason,
        string rejectionReason)
        => LocalOperationalLog.Warn(
            "FileTransfer",
            $"event=filetransfer_runtime_unlock_route_commit_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; rejection_reason={FormatProtocolLogValue(rejectionReason)}");

    private bool TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromoteRegularNknV4ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!EnsureOutboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(
                context,
                reason))
        {
            return true;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot))
        {
            return false;
        }

        return TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }
    private bool TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        out RuntimeUnlockAdmissionToken admission,
        out RuntimeUnlockPendingPreCommitProbeRequest pendingPreCommitProbe)
    {
        admission = default;
        pendingPreCommitProbe = default;
        var state = CreateCoordinatorStateLocked(context);
        if (context.RuntimeUnlockAdmissionToken is not { IsDefault: false } currentAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                currentAdmission,
                out _))
        {
            return false;
        }

        RuntimeUnlockPendingPreCommitProbeRequest currentPending;
        if (context.RuntimeUnlockPendingPreCommitProbe is { } existingPending)
        {
            if (existingPending.AdmissionToken != currentAdmission ||
                !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                    state,
                    existingPending,
                    out _) ||
                !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                    snapshot,
                    existingPending,
                    out _))
            {
                return false;
            }

            currentPending = existingPending;
        }
        else
        {
            var validCreationSnapshot =
                context.RouteRuntime.UsesPostTunaFallbackV6Runtime
                    ? IsRuntimeUnlockPostFallbackPreCommitAuthorityCreationSnapshot(
                        snapshot,
                        context)
                    : IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
                        snapshot,
                        context);
            if (!validCreationSnapshot)
            {
                return false;
            }

            if (!TryRecordRuntimeUnlockPendingPreCommitProbeLocked(
                    FileTransferDirection.Outbound,
                    context.SessionId,
                    context.TransferId,
                    currentAdmission.RuntimeOwner,
                    currentAdmission,
                    snapshot,
                    "runtime_unlock_route_commit_send_drain"))
            {
                return false;
            }

            state = CreateCoordinatorStateLocked(context);
            if (context.RuntimeUnlockAdmissionToken != currentAdmission ||
                context.RuntimeUnlockPendingPreCommitProbe is not { } acceptedPending ||
                acceptedPending.AdmissionToken != currentAdmission ||
                !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                    state,
                    currentAdmission,
                    out _) ||
                !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                    state,
                    acceptedPending,
                    out _) ||
                !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(snapshot, acceptedPending, out _))
            {
                return false;
            }

            currentPending = acceptedPending;
        }

        admission = currentAdmission;
        pendingPreCommitProbe = currentPending;
        return true;
    }

    private bool TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
        InboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        out RuntimeUnlockAdmissionToken admission,
        out RuntimeUnlockPendingPreCommitProbeRequest pendingPreCommitProbe)
    {
        admission = default;
        pendingPreCommitProbe = default;
        var state = CreateCoordinatorStateLocked(context);
        if (context.RuntimeUnlockAdmissionToken is not
                { IsDefault: false } currentAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                currentAdmission,
                out _))
        {
            return false;
        }

        RuntimeUnlockPendingPreCommitProbeRequest currentPending;
        if (context.RuntimeUnlockPendingPreCommitProbe is { } existingPending)
        {
            if (existingPending.AdmissionToken != currentAdmission ||
                !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                    state,
                    existingPending,
                    out _) ||
                !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                    snapshot,
                    existingPending,
                    out _))
            {
                return false;
            }

            currentPending = existingPending;
        }
        else
        {
            if (!IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
                    snapshot,
                    context.SessionId,
                    context.TransferId))
            {
                return false;
            }

            if (!TryRecordRuntimeUnlockPendingPreCommitProbeLocked(
                    FileTransferDirection.Inbound,
                    context.SessionId,
                    context.TransferId,
                    currentAdmission.RuntimeOwner,
                    currentAdmission,
                    snapshot,
                    "regular_v4_to_tuna_transition"))
            {
                return false;
            }

            state = CreateCoordinatorStateLocked(context);
            if (context.RuntimeUnlockAdmissionToken != currentAdmission ||
                context.RuntimeUnlockPendingPreCommitProbe is not { } acceptedPending ||
                acceptedPending.AdmissionToken != currentAdmission ||
                !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                    state,
                    currentAdmission,
                    out _) ||
                !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                    state,
                    acceptedPending,
                    out _) ||
                !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                    snapshot,
                    acceptedPending,
                    out _))
            {
                return false;
            }

            currentPending = acceptedPending;
        }

        admission = currentAdmission;
        pendingPreCommitProbe = currentPending;
        return true;
    }

    private static bool IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string sessionId,
        string transferId)
        => string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal) &&
           string.Equals(snapshot.TransferId, transferId, StringComparison.Ordinal) &&
           snapshot.LaunchGeneration > 0 &&
           snapshot.TransactionGeneration > 0 &&
           snapshot.OfferGeneration > 0 &&
           snapshot.PeerVisibleProof &&
           snapshot.PeerReceived &&
           snapshot.AnswerReceived &&
           snapshot.TunaPathLeaseRequired &&
           snapshot.TunaPathLeaseGeneration > 0 &&
           snapshot.TunaPathLeaseCurrent &&
           snapshot.TargetRoute == FileTransferRoute.FileTunaV4 &&
           snapshot.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           snapshot.HandoffKind ==
                FileTransferTransportHandoffKind.NormalToTunaActivation &&
           snapshot.TargetTransport == FileTransferTransportKind.Tuna;

    private static bool IsRuntimeUnlockPostFallbackPreCommitAuthorityCreationSnapshot(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        OutboundTransferContext context)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion !=
                FileTransferProtocol.ProtocolVersionV6 ||
            !IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
                snapshot,
                context.SessionId,
                context.TransferId) ||
            !string.Equals(
                snapshot.TransactionState,
                RuntimeUnlockTransactionState.AnswerReceived.ToString(),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                snapshot.TunaPathLeaseState,
                RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.TunaPathLeaseListenerRunId) ||
            !string.IsNullOrWhiteSpace(snapshot.PathProbeFailureReason))
        {
            return false;
        }

        return
            (string.Equals(
                snapshot.PathProbeState,
                RuntimeUnlockPathProbeState.None.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
             string.IsNullOrWhiteSpace(snapshot.PathProbeId)) ||
            (string.Equals(
                snapshot.PathProbeState,
                RuntimeUnlockPathProbeState.Started.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(snapshot.PathProbeId));
    }

    private static bool IsRuntimeUnlockRegularV4RouteCommitBoundarySnapshot(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        OutboundTransferContext context)
        => string.Equals(snapshot.SessionId, context.SessionId, StringComparison.Ordinal) &&
           string.Equals(snapshot.TransferId, context.TransferId, StringComparison.Ordinal) &&
           snapshot.TransactionGeneration > 0 &&
           snapshot.OfferGeneration > 0 &&
           snapshot.TargetRoute == FileTransferRoute.FileTunaV4 &&
           snapshot.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
           snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
           snapshot.TargetTransport == FileTransferTransportKind.Tuna;

    private static bool IsRuntimeUnlockRegularV4RouteCommitLaunchSnapshot(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        OutboundTransferContext context)
        => snapshot.LaunchGeneration > 0 &&
           IsRuntimeUnlockRegularV4RouteCommitBoundarySnapshot(snapshot, context) &&
           snapshot.PeerVisibleProof &&
           snapshot.PeerReceived &&
           snapshot.AnswerReceived &&
           snapshot.TunaPathLeaseRequired &&
           snapshot.TunaPathLeaseGeneration > 0 &&
           snapshot.TunaPathLeaseCurrent &&
           string.Equals(
               snapshot.TunaPathLeaseState,
               RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
               StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(snapshot.TunaPathLeaseListenerRunId);

    private static bool IsSameRuntimeUnlockRegularV4RouteCommitSnapshotBoundary(
        RuntimeUnlockRouteCommitSnapshot left,
        RuntimeUnlockRouteCommitSnapshot right)
        => string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
           string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal) &&
           left.TransactionGeneration == right.TransactionGeneration &&
           left.OfferGeneration == right.OfferGeneration &&
           left.LaunchGeneration == right.LaunchGeneration &&
           left.TunaPathLeaseGeneration == right.TunaPathLeaseGeneration &&
           string.Equals(
               left.TunaPathLeaseListenerRunId,
               right.TunaPathLeaseListenerRunId,
               StringComparison.Ordinal) &&
           left.TargetRoute == right.TargetRoute &&
           left.ProtocolVersion == right.ProtocolVersion &&
           left.HandoffKind == right.HandoffKind &&
           left.TargetTransport == right.TargetTransport &&
           left.PeerVisibleProof == right.PeerVisibleProof &&
           left.PeerReceived == right.PeerReceived &&
           left.AnswerReceived == right.AnswerReceived &&
           left.TunaPathLeaseRequired == right.TunaPathLeaseRequired &&
           string.Equals(left.TunaPathLeaseState, right.TunaPathLeaseState, StringComparison.Ordinal) &&
           left.TunaPathLeaseCurrent == right.TunaPathLeaseCurrent &&
           string.Equals(left.TunaPathLeaseFailureReason, right.TunaPathLeaseFailureReason, StringComparison.Ordinal);

    private static bool IsSameRuntimeUnlockRegularV4RouteCommitLaunchBindingBoundary(
        RuntimeUnlockRouteCommitSnapshot pending,
        RuntimeUnlockRouteCommitSnapshot launch)
        => pending.LaunchGeneration <= 0 &&
           launch.LaunchGeneration > 0 &&
           string.Equals(pending.SessionId, launch.SessionId, StringComparison.Ordinal) &&
           string.Equals(pending.TransferId, launch.TransferId, StringComparison.Ordinal) &&
           pending.TransactionGeneration == launch.TransactionGeneration &&
           pending.OfferGeneration == launch.OfferGeneration &&
           pending.TunaPathLeaseGeneration == launch.TunaPathLeaseGeneration &&
           string.Equals(
               pending.TunaPathLeaseListenerRunId,
               launch.TunaPathLeaseListenerRunId,
               StringComparison.Ordinal) &&
           pending.TargetRoute == launch.TargetRoute &&
           pending.ProtocolVersion == launch.ProtocolVersion &&
           pending.HandoffKind == launch.HandoffKind &&
           pending.TargetTransport == launch.TargetTransport &&
           pending.PeerVisibleProof == launch.PeerVisibleProof &&
           pending.PeerReceived == launch.PeerReceived &&
           pending.AnswerReceived == launch.AnswerReceived &&
           pending.TunaPathLeaseRequired == launch.TunaPathLeaseRequired &&
           string.Equals(
               pending.TunaPathLeaseState,
               launch.TunaPathLeaseState,
               StringComparison.Ordinal) &&
           pending.TunaPathLeaseCurrent == launch.TunaPathLeaseCurrent &&
           string.Equals(
               pending.TunaPathLeaseFailureReason,
               launch.TunaPathLeaseFailureReason,
               StringComparison.Ordinal) &&
           ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(pending.PathProbeState) == 0 &&
           ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(launch.PathProbeState) == 0 &&
           string.IsNullOrWhiteSpace(pending.PathProbeId) &&
           string.IsNullOrWhiteSpace(launch.PathProbeId) &&
           pending.PathProbeTransport == launch.PathProbeTransport &&
           pending.PathProbeAckedUtcMs == launch.PathProbeAckedUtcMs &&
           string.Equals(
               pending.PathProbeFailureReason,
               launch.PathProbeFailureReason,
               StringComparison.Ordinal);

    private static bool IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        OutboundTransferContext context)
        => IsRuntimeUnlockRegularV4RouteCommitBoundarySnapshot(snapshot, context) &&
           ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(snapshot.PathProbeState) == 0 &&
           string.IsNullOrWhiteSpace(snapshot.PathProbeId);

    private static bool IsAllowedRuntimeUnlockRegularV4RouteCommitProbeProgression(
        RuntimeUnlockRouteCommitSnapshot previous,
        RuntimeUnlockRouteCommitSnapshot current)
    {
        if (!IsSameRuntimeUnlockRegularV4RouteCommitSnapshotBoundary(previous, current))
        {
            return false;
        }

        var previousRank = ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(
            previous.PathProbeState);
        var currentRank = ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(
            current.PathProbeState);
        if (previousRank < 0 || currentRank < previousRank)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(previous.PathProbeId) &&
            !string.Equals(previous.PathProbeId, current.PathProbeId, StringComparison.Ordinal))
        {
            return false;
        }

        return currentRank == 0 || !string.IsNullOrWhiteSpace(current.PathProbeId);
    }

    private static int ResolveRuntimeUnlockRegularV4RouteCommitProbeStateRank(string? state)
    {
        var normalized = NormalizeReason(state);
        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(normalized, "started", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(normalized, "acked", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return -1;
    }

    private bool TryPromoteInboundRegularNknV4ToFileTunaV4Locked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromoteRegularNknV4ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!EnsureInboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(
                context,
                reason))
        {
            return true;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot))
        {
            return false;
        }

        return TryPrepareInboundRegularV4ToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }
    private static void ResetOutboundRegularNknV4StateForFileTunaV4Locked(OutboundTransferContext context)
    {
        ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.PullTransportRebindGeneration = 0;
        context.PullTransportRebindStartedUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.V6TransportEpoch = null;
        context.V6TransportEpochReplayLoopEpochId = 0;
        context.V6TransportHandoff = null;
        context.PullSenderFeedCreditWaitStartedUtc = null;
        context.V4SenderCreditExhaustedSinceUtc = null;
    }

    private static void ResetInboundRegularNknV4StateForFileTunaV4Locked(InboundTransferContext context)
    {
        ClearInboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.PullTransportRebindGeneration = 0;
        context.PullTransportRebindStartedUtc = null;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        context.V6TransportEpoch = null;
        context.V6TransportEpochReplayLoopEpochId = 0;
        context.V6ReceiverTransportEpoch = 0;
        context.V6TransportHandoff = null;
        context.PullTimeoutOldestChunkIndex = null;
        context.PullTimeoutStreak = 0;
        context.PullFirstChunkTimeoutCount = 0;
        context.PullRecoverySinceUtc = null;
    }

    private bool TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!EnsureOutboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot))
        {
            return false;
        }

        return TryPrepareOutboundPostTunaFallbackToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }

    private bool TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromotePostTunaFallbackV6ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!EnsureInboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot))
        {
            return false;
        }

        return TryPrepareInboundPostTunaFallbackToTunaRouteTransitionLocked(
            context,
            snapshot,
            reason,
            out _);
    }

    private void ResetOutboundPostTunaFallbackStateForFileTunaV4Locked(OutboundTransferContext context)
    {
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            "tuna_reactivation_replaced_fallback_owner");
        ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(context);
        var supersededFallbackLeg = IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg)
            ? context.CurrentTransferLeg
            : null;
        var previousRemoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var previousAccepted = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        var previousGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
        var fallbackProvenCommitted = supersededFallbackLeg is null
            ? -1
            : Math.Clamp(supersededFallbackLeg.ProvenCommittedChunkIndex, 0, context.ChunkCount);
        var fallbackProvenHighestObserved = supersededFallbackLeg?.ProvenHighestObservedChunkIndex ?? -1;
        RetireOutboundFallbackCheckpointExchangeLocked(context, null, "live_route_tuna_reactivated");
        context.V6TransportEpoch = null;
        context.V6TransportEpochReplayLoopEpochId = 0;
        context.V6TransportHandoff = null;
        context.V6PendingEpochRepairRequestIds.Clear();
        context.V6PriorityRequestedChunks.Clear();
        context.V6NormalRequestedChunks.Clear();
        context.V6RequestedChunkMetadataByChunkIndex.Clear();
        context.V6AppliedFrontierRequestIds.Clear();
        context.V6CurrentNormalRequestKey = null;
        context.V6ChunkSendsInFlight.Clear();
        context.SentAwaitingAck.Clear();
        context.PullPostTunaRecoveryActive = false;
        context.PullPostTunaRecoveryGeneration = 0;
        context.PullPostTunaRecoveryFrontierChunkIndex = -1;
        context.PullPostTunaRecoveryStartedUtc = null;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportRebindStartedUtc = null;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.PullFileTunaV4PostTunaReactivationGeneration = Math.Max(
            1,
            context.PullFileTunaV4PostTunaReactivationGeneration + 1);
        context.PullFileTunaV4PostTunaReactivationStartedUtc = DateTimeOffset.UtcNow;
        context.PullFileTunaV4PostTunaLastFrontierReplayUtc = null;
        context.PullFileTunaV4PostTunaLastFrontierReplayChunkIndex = -1;
        context.PullFileTunaV4PostTunaLastFrontierReplayEndChunkIndex = -1;
        context.PullFileTunaV4PostTunaFrontierReplayCount = 0;
        context.V6RegularNknStateRefreshSendInFlight = 0;
        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
        context.V6RegularNknStateRefreshActiveRequestId = null;
        context.V6RegularNknStateRefreshActivePriority = null;
        context.V6RegularNknStateRefreshActiveStartedUtc = null;
        context.V6RegularNknStateRefreshActiveReusedPendingCheckpoint = false;
        context.PostTunaFallbackLiveCheckpointStallCommittedChunkIndex = -1;
        context.PostTunaFallbackLiveCheckpointStallHighestObservedChunkIndex = -1;
        context.PostTunaFallbackLiveCheckpointStallCount = 0;
        context.V6RegularNknCheckpointSyncSendInFlight = 0;
        ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
        ClearOutboundV6PostTunaFallbackSenderSelfRepairLocked(context);
        context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex = -1;
        context.V6PostTunaFallbackNormalSendAheadFreezeUtc = null;
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.QueuedUtc = null;
            repairState.InFlight = false;
        }

        if (fallbackProvenCommitted > context.RemoteNextExpectedChunkIndex)
        {
            var advancedFrontier = fallbackProvenCommitted;
            context.RemoteNextExpectedChunkIndex = advancedFrontier;
            context.RemoteGrantedUntilExclusive = Math.Max(context.RemoteGrantedUntilExclusive, advancedFrontier);
            context.ChunksTransferred = Math.Max(context.ChunksTransferred, advancedFrontier);
            var receiverBytes = advancedFrontier >= context.ChunkCount
                ? context.FileSizeBytes
                : Math.Min(context.FileSizeBytes, (long)advancedFrontier * Math.Max(1, context.ChunkSizeBytes));
            context.BytesTransferred = Math.Max(context.BytesTransferred, receiverBytes);
            context.BytesAcknowledgedByReceiver = Math.Max(context.BytesAcknowledgedByReceiver, receiverBytes);
            foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < advancedFrontier).ToArray())
            {
                context.SentAwaitingAck.Remove(chunkIndex);
            }

            TrimSenderRepairCacheLocked(context, advancedFrontier);
            ClearObsoleteOutboundV4RepairWorkAfterFrontierAdvanceLocked(
                context,
                previousRemoteFrontier,
                advancedFrontier);
        }

        ResetOutboundV4AcceptedAfterPauseLocked(context, "live_route_tuna_reactivated");
        LogFileTransferFallbackLegAuthoritySupersededByRouteHint(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            supersededFallbackLeg,
            context.RouteSelection,
            "live_route_tuna_reactivated");
        if (previousAccepted > context.ChunksAcceptedForTransport ||
            previousRemoteFrontier != context.RemoteNextExpectedChunkIndex ||
            previousGrantedUntilExclusive != context.RemoteGrantedUntilExclusive)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_tuna_reactivation_v4_frontier_reseeded; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; target_route={context.RouteSelection.TelemetryToken}; target_protocol_version={context.NegotiatedDataProtocolVersion}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; previous_chunks_accepted_for_transport={previousAccepted}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; previous_remote_credit_until_chunk_index_exclusive={previousGrantedUntilExclusive}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; fallback_proven_committed_chunk={fallbackProvenCommitted}; fallback_proven_highest_observed_chunk={fallbackProvenHighestObserved}; reason=live_route_tuna_reactivated");
        }

        context.SignalSparseSenderPump();
    }

    private static void ResetInboundPostTunaFallbackStateForFileTunaV4Locked(InboundTransferContext context)
    {
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            "tuna_reactivation_replaced_fallback_owner");
        ClearInboundRuntimeUnlockFallbackSeparationStateLocked(context);
        var supersededFallbackLeg = IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg)
            ? context.CurrentTransferLeg
            : null;
        context.V6TransportEpoch = null;
        context.V6TransportEpochReplayLoopEpochId = 0;
        context.V6ReceiverTransportEpoch = 0;
        context.V6TransportHandoff = null;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.V6LastFrontierRequestChunkIndex = -1;
        context.V6LastFrontierRequestId = null;
        context.PullPostTunaRecoveryActive = false;
        context.PullPostTunaRecoveryGeneration = 0;
        context.PullPostTunaRecoveryFrontierChunkIndex = -1;
        context.PullPostTunaRecoveryStartedUtc = null;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.PullTransportRebindStartedUtc = null;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        ClearInboundV6PostTunaFallbackProofReplayLocked(context);
        ResetInboundV6PostTunaFallbackFrontierRescueLocked(context);
        LogFileTransferFallbackLegAuthoritySupersededByRouteHint(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            supersededFallbackLeg,
            context.RouteSelection,
            "live_route_tuna_reactivated");
    }

    private static bool IsFileTunaV4PostTunaRecoveryContext(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int negotiatedProtocolVersion,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedReason = NormalizeReason(reason);
        var provedTunaFallbackResume =
            string.Equals(normalizedReason, "transport_recovered", StringComparison.Ordinal) &&
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            targetTransport == FileTransferTransportKind.RegularNkn;

        return routeRuntime.UsesFileTunaV4Runtime &&
            negotiatedProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            routeRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
            (provedTunaFallbackResume || IsTunaFallbackTransportPauseReason(reason));
    }

    private bool TryStartOutboundFileTunaV4PostTunaRecoveryLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (!IsFileTunaV4PostTunaRecoveryContext(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport) ||
            context.IsTerminal)
        {
            return false;
        }

        var keepPausedUntilFileTunaV4Repair =
            context.PullTransportPaused &&
            IsTunaFallbackTransportPauseReason(reason) &&
            !string.Equals(NormalizeReason(reason), "transport_recovered", StringComparison.Ordinal);
        if (keepPausedUntilFileTunaV4Repair)
        {
            context.PullTransportPauseReason ??= reason;
            context.PullTransportResumeRequestPending = true;
        }
        else
        {
            context.PullTransportPaused = false;
            context.PullTransportPausedSinceUtc = null;
            context.PullTransportGraceDeadlineUtc = null;
            context.PullTransportPauseReason = null;
            context.PullTransportResumeRequestPending = false;
        }

        context.PullTransportRebindGeneration = Math.Max(1, context.PullTransportRebindGeneration + 1);
        context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.SparseSenderPumpLastWakeReason = "file_tuna_v4_post_tuna_rebind";
        ResetOutboundV4AcceptedAfterPauseLocked(context, "file_tuna_v4_post_tuna_recovery");
        StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);
        return true;
    }

    private bool TryStartInboundFileTunaV4PostTunaRecoveryLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (!IsFileTunaV4PostTunaRecoveryContext(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport) ||
            context.IsTerminal)
        {
            return false;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = Math.Max(1, context.PullTransportRebindGeneration + 1);
        context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportRebindStartedBytesTransferred = context.BytesTransferred;
        context.PullTransportRebindStartedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindStartedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
        context.PullTransportRebindLastFrontierRepairLoopLogUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        StartInboundPostTunaRecoveryLocked(context, reason);
        return true;
    }

    private static bool ShouldSuppressRecoveredV6RegularNknEpochRestart(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRouteRuntimeDescriptor routeRuntime,
        long lastRecoveredEpoch,
        int lastRecoveredLiveRouteEpochId,
        FileTransferTransportHandoffKind lastRecoveredKind,
        FileTransferTransportKind lastRecoveredTarget,
        int currentLiveRouteEpochId,
        FileTransferTransportHandoffKind requestedKind,
        FileTransferTransportKind requestedTarget,
        string reason)
    {
        if (lastRecoveredEpoch <= 0 ||
            lastRecoveredTarget != FileTransferTransportKind.RegularNkn ||
            requestedTarget != FileTransferTransportKind.RegularNkn)
        {
            return false;
        }

        if (requestedKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            routeRuntime.UsesPostTunaFallbackV6Runtime &&
            currentLiveRouteEpochId > 0 &&
            lastRecoveredLiveRouteEpochId > 0 &&
            currentLiveRouteEpochId > lastRecoveredLiveRouteEpochId)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_recovered_restart_allowed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(routeRuntime.TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_live_route_epoch={lastRecoveredLiveRouteEpochId}; current_live_route_epoch={currentLiveRouteEpochId}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}; allowance=new_post_tuna_live_route_epoch");
            return false;
        }

        if (requestedKind == FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            if (routeRuntime.UsesPostTunaFallbackV6Runtime)
            {
                if (lastRecoveredKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason))
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_epoch_recovered_restart_allowed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(routeRuntime.TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}; allowance=post_tuna_bridge_recovery_epoch_refresh");
                    return false;
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_epoch_recovered_restart_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(routeRuntime.TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
                return true;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_recovered_restart_allowed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(routeRuntime.TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(routeRuntime.TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
        return true;
    }

    private static bool IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(string reason)
        => string.Equals(reason, "transport_recovered_unproven", StringComparison.Ordinal) ||
           string.Equals(reason, "receive_stall_recovery", StringComparison.Ordinal) ||
           string.Equals(reason, "bridge_receive_stall", StringComparison.Ordinal) ||
           string.Equals(reason, "bulk_receive_stalled", StringComparison.Ordinal);

    private void StartOutboundPostTunaRecoveryLocked(
        OutboundTransferContext context,
        string reason)
        => StartOutboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            forceNewGeneration: false,
            capturedReauthorizationRequest: null);

    private void StartOutboundPostTunaRecoveryWithReauthorizationLocked(
        OutboundTransferContext context,
        string reason,
        FallbackTailYieldTransportEpochReauthorizationRequest capturedReauthorizationRequest)
        => StartOutboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            forceNewGeneration: false,
            capturedReauthorizationRequest);

    private void StartOutboundPostTunaRecoveryCoreLocked(
        OutboundTransferContext context,
        string reason,
        bool forceNewGeneration,
        FallbackTailYieldTransportEpochReauthorizationRequest?
            capturedReauthorizationRequest = null)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        if (!forceNewGeneration &&
            capturedReauthorizationRequest is null &&
            string.Equals(
                reason,
                "transport_recovered_unproven",
                StringComparison.Ordinal) &&
            TryHoldOutboundPostTunaRecoveryEpochRestartLocked(
                context,
                reason,
                requiresResumeRequest: true,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn))
        {
            return;
        }

        if (!forceNewGeneration &&
            capturedReauthorizationRequest is null &&
            IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason) &&
            TrySuppressPostYieldFallbackRecoveryRestartLocked(
                context,
                "post_tuna_fallback_recovery_restart",
                reason))
        {
            return;
        }

        if (forceNewGeneration)
        {
            var currentGeneration = Math.Max(
                context.PullTransportRebindGeneration,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0);
            context.PullTransportRebindGeneration = Math.Max(1, currentGeneration + 1);
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        }

        var generation = Math.Max(1, context.PullTransportRebindGeneration);
        var frontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount - 1);
        var currentFallbackLegMatchesLiveEpoch =
            context.CurrentTransferLeg is { } currentLeg &&
            IsCurrentPostTunaFallbackLeg(currentLeg) &&
            currentLeg.LiveRouteEpochId == (context.CurrentLiveRouteEpoch?.EpochId ?? 0);
        var hasNewerAuthorizedTransportEpoch =
            context.CurrentTransportEpochDecision is
            {
                Accepted: true,
                ActionKind: TransportEpochActionKind.Adopt,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn,
            } transportEpochDecision &&
            context.CurrentTransferLeg is { } transportEpochLeg &&
            transportEpochDecision.TargetTransportEpoch >
                transportEpochLeg.TransportEpochId;
        if (!forceNewGeneration &&
            context.PullPostTunaRecoveryActive &&
            IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason) &&
            currentFallbackLegMatchesLiveEpoch &&
            context.CurrentTransferLeg is
                { State: FileTransferLegState.RecoveryActive } &&
            !hasNewerAuthorizedTransportEpoch)
        {
            return;
        }

        if (!forceNewGeneration &&
            context.PullPostTunaRecoveryActive &&
            currentFallbackLegMatchesLiveEpoch &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier &&
            capturedReauthorizationRequest is null &&
            !hasNewerAuthorizedTransportEpoch)
        {
            return;
        }

        var transportEpoch = ResolveOutboundFallbackCurrentTransportEpochLocked(context, generation);
        var pendingFallbackTailYieldTransportEpochReauthorization =
            GetPendingFallbackTailYieldTransportEpochReauthorization(context);
        if (!forceNewGeneration &&
            capturedReauthorizationRequest is null &&
            pendingFallbackTailYieldTransportEpochReauthorization is
                { } exactPendingReauthorization &&
            exactPendingReauthorization.TargetTransportEpoch == transportEpoch &&
            FileTransferCoordinator
                .IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    CreateCoordinatorStateLocked(context),
                    exactPendingReauthorization,
                    out _))
        {
            return;
        }

        FallbackTailYieldTransportEpochReauthorizationRequest?
            checkpointBoundReauthorizationRequest = null;
        if (pendingFallbackTailYieldTransportEpochReauthorization is { } pendingReauthorization &&
            !TryCaptureExpectedOutboundFallbackCheckpointReauthorizationRestartLocked(
                context,
                pendingReauthorization,
                transportEpoch,
                reason,
                out checkpointBoundReauthorizationRequest))
        {
            return;
        }

        var fallbackTailYieldTransportEpochReauthorizationRequest =
            capturedReauthorizationRequest ??
            checkpointBoundReauthorizationRequest ??
            TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                transportEpoch,
                reason);
        if (pendingFallbackTailYieldTransportEpochReauthorization is not null &&
            fallbackTailYieldTransportEpochReauthorizationRequest is null)
        {
            return;
        }

        if (pendingFallbackTailYieldTransportEpochReauthorization is null)
        {
            RetireOutboundFallbackCheckpointExchangeLocked(context, null, reason);
        }

        var leg = StartOutboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.CheckpointPending,
            canSendData: false,
            transportEpochOverride: transportEpoch,
            bridgeRecoveryGenerationOverride: generation,
            fallbackTailYieldTransportEpochReauthorizationRequest:
                fallbackTailYieldTransportEpochReauthorizationRequest);
        if (leg is null)
        {
            return;
        }

        InitializeOutboundPostTunaRecoveryForCurrentCoordinatorLegLocked(
            context,
            reason,
            generation,
            frontier);
    }

    private static void InitializeOutboundPostTunaRecoveryForCurrentCoordinatorLegLocked(
        OutboundTransferContext context,
        string reason,
        int? recoveryGeneration = null,
        int? recoveryFrontier = null)
    {
        var generation = recoveryGeneration ?? Math.Max(1, context.PullTransportRebindGeneration);
        var frontier = recoveryFrontier ?? Math.Clamp(
            context.RemoteNextExpectedChunkIndex,
            0,
            Math.Max(0, context.ChunkCount - 1));
        ResetOutboundPostTunaFallbackLegOwnedStateLocked(context, reason, generation, frontier);
        context.PullPostTunaRecoveryActive = true;
        context.PullPostTunaRecoveryGeneration = generation;
        context.PullPostTunaRecoveryFrontierChunkIndex = frontier;
        context.PullPostTunaRecoveryStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportFrontierOnlyRepairActive = true;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = frontier;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_recovery_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; recovery_generation={generation}; frontier_chunk_index={frontier}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}");
    }

    private static bool
        TryCaptureExpectedOutboundFallbackCheckpointReauthorizationRestartLocked(
        OutboundTransferContext context,
        FallbackTailYieldTransportEpochReauthorization pendingReauthorization,
        long targetTransportEpoch,
        string reason,
        out FallbackTailYieldTransportEpochReauthorizationRequest?
            reauthorizationRequest)
    {
        reauthorizationRequest = null;
        if (targetTransportEpoch <= pendingReauthorization.TargetTransportEpoch)
        {
            return true;
        }

        if (context.CurrentTransferLeg is not { } leg ||
            string.IsNullOrWhiteSpace(leg.CheckpointRequestId))
        {
            return true;
        }

        var state = CreateCoordinatorStateLocked(context);
        var currentCheckpointSnapshot =
            FileTransferCoordinator.NormalizeFallbackTailYieldCheckpointRequestId(
                state.CurrentFallbackCheckpointLease);
        if (state.PersistentState.FallbackTailYield.PendingReauthorization !=
                pendingReauthorization ||
            state.PersistentState.FallbackTailYield.CurrentOperation is not
                null ||
            !string.Equals(
                currentCheckpointSnapshot,
                leg.CheckpointRequestId,
                StringComparison.Ordinal) ||
            !string.Equals(
                pendingReauthorization.TargetCheckpointRequestIdSnapshot,
                currentCheckpointSnapshot,
                StringComparison.Ordinal) ||
            pendingReauthorization.TargetCheckpointGeneration !=
                leg.CheckpointGeneration ||
            pendingReauthorization.TargetCheckpointLease !=
                state.CurrentFallbackCheckpointLease ||
            !FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
                state,
                pendingReauthorization,
                allowTransportEpochDecisionReplacement: true,
                out _))
        {
            return false;
        }

        reauthorizationRequest =
            TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                targetTransportEpoch,
                reason);
        return reauthorizationRequest is not null;
    }

    private void StartOutboundPostTunaRecoveryWithSafetyReplayLocked(OutboundTransferContext context, string reason)
    {
        var previousLeg = context.CurrentTransferLeg;
        StartOutboundPostTunaRecoveryLocked(context, reason);
        if (ReferenceEquals(previousLeg, context.CurrentTransferLeg) &&
            (IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(context, context.CurrentTransferLeg) ||
             HasCoordinatorPairedFallbackTailRelease(context)))
        {
            return;
        }

        if (ReferenceEquals(previousLeg, context.CurrentTransferLeg) &&
            context.CurrentTransferLeg is { State: FileTransferLegState.RecoveryActive })
        {
            return;
        }

        if (context.CurrentLiveRouteEpoch is { EpochId: > 0 })
        {
            QueueOutboundPostTunaFallbackCheckpointRequestLocked(context, reason);
        }
    }

    private void QueueOutboundPostTunaFallbackCheckpointRequestLocked(OutboundTransferContext context, string reason)
    {
        IFileTransferDataSession? dataSession = null;
        FileTransferFrontierRequestFrameV6? request = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
                context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
                context.CurrentTransferLeg is not { } leg ||
                !IsCurrentPostTunaFallbackLeg(leg))
            {
                return;
            }

            if (IsCurrentPostTunaFallbackLegCheckpointPending(leg))
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_fallback_checkpoint_request_skipped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; checkpoint_request_id={FormatProtocolLogValue(leg.CheckpointRequestId ?? "(none)")}; skip_reason=checkpoint_already_pending");
                return;
            }

            if (ShouldSuppressOutboundFallbackCheckpointRequestForDeliveryRecoveryLocked(
                    context,
                    $"fallback_checkpoint:{reason}",
                    out var suppressionReason,
                    out var deliveryRecoveryAgeMs,
                    out var deliveryRecoveryRemainingMs))
            {
                context.PullTransportResumeRequestPending = true;
                context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_delivery_recovery_pending";
                context.V6SenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_delivery_recovery_pending";
                LogOutboundFallbackCheckpointRequestSuppressedForDeliveryRecovery(
                    context,
                    $"fallback_checkpoint:{reason}",
                    suppressionReason,
                    deliveryRecoveryAgeMs,
                    deliveryRecoveryRemainingMs);
                return;
            }

            dataSession = context.DataSession;
            if (dataSession is null)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_fallback_checkpoint_request_deferred; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg.LegId)}; leg_generation={leg.Generation}; cause=data_session_missing");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var transportEpoch = ResolveOutboundV6SparseRuntimeStateRefreshTransportEpoch(context);
            var epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
            var committed = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount));
            request = CreateOutboundV4SparseRuntimeStateRefreshRequestLocked(
                context,
                now,
                TimeSpan.Zero,
                transportEpoch,
                epochState,
                0,
                0,
                committed,
                0,
                0,
                $"fallback_leg_checkpoint:{reason}",
                V6RegularNknStateRefreshPriority);
            context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_requested";
            context.V6SenderPumpLastWakeReason = "post_tuna_fallback_checkpoint_requested";
        }

        if (dataSession is not null && request is not null)
        {
            QueueOutboundV4SparseRuntimeStateRefresh(context, dataSession, request);
        }
    }

    private void StartInboundPostTunaRecoveryLocked(
        InboundTransferContext context,
        string reason)
        => StartInboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            capturedReauthorizationRequest: null);

    private void StartInboundPostTunaRecoveryWithReauthorizationLocked(
        InboundTransferContext context,
        string reason,
        FallbackTailYieldTransportEpochReauthorizationRequest capturedReauthorizationRequest)
        => StartInboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            capturedReauthorizationRequest);

    private void StartInboundPostTunaRecoveryCoreLocked(
        InboundTransferContext context,
        string reason,
        FallbackTailYieldTransportEpochReauthorizationRequest?
            capturedReauthorizationRequest)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        if (capturedReauthorizationRequest is null &&
            string.Equals(
                reason,
                "transport_recovered_unproven",
                StringComparison.Ordinal) &&
            TryHoldInboundPostTunaRecoveryEpochRestartLocked(
                context,
                reason,
                requiresResumeRequest: true,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn))
        {
            return;
        }

        if (capturedReauthorizationRequest is null &&
            IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason) &&
            TrySuppressPostYieldFallbackRecoveryRestartLocked(
                context,
                "post_tuna_fallback_recovery_restart",
                reason))
        {
            return;
        }

        var generation = Math.Max(1, context.PullTransportRebindGeneration);
        var frontier = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount - 1);
        var currentFallbackLegMatchesLiveEpoch =
            context.CurrentTransferLeg is { } currentLeg &&
            IsCurrentPostTunaFallbackLeg(currentLeg) &&
            currentLeg.LiveRouteEpochId == (context.CurrentLiveRouteEpoch?.EpochId ?? 0);
        var hasNewerAuthorizedTransportEpoch =
            context.CurrentTransportEpochDecision is
            {
                Accepted: true,
                ActionKind: TransportEpochActionKind.Adopt,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn,
            } transportEpochDecision &&
            context.CurrentTransferLeg is { } transportEpochLeg &&
            transportEpochDecision.TargetTransportEpoch >
                transportEpochLeg.TransportEpochId;
        if (context.PullPostTunaRecoveryActive &&
            currentFallbackLegMatchesLiveEpoch &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier &&
            capturedReauthorizationRequest is null &&
            !hasNewerAuthorizedTransportEpoch)
        {
            return;
        }

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? generation;
        var pendingFallbackTailYieldTransportEpochReauthorization =
            GetPendingFallbackTailYieldTransportEpochReauthorization(context);
        if (capturedReauthorizationRequest is null &&
            pendingFallbackTailYieldTransportEpochReauthorization is
                { } exactPendingReauthorization &&
            exactPendingReauthorization.TargetTransportEpoch == transportEpoch &&
            FileTransferCoordinator
                .IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    CreateCoordinatorStateLocked(context),
                    exactPendingReauthorization,
                    out _))
        {
            return;
        }

        var previousInboundLeg = context.CurrentTransferLeg;
        var retiredCheckpointRequestId = IsCurrentPostTunaFallbackLeg(previousInboundLeg)
            ? previousInboundLeg!.CheckpointRequestId
            : null;
        if (!string.IsNullOrWhiteSpace(retiredCheckpointRequestId))
        {
            if (!TryAuthorizeRuntimeOwnerLegActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.FallbackCheckpointRetire,
                    "post_tuna_recovery_restart",
                    "retire_fallback_checkpoint",
                    previousInboundLeg,
                    retiredCheckpointRequestId,
                    previousInboundLeg!.TransportEpochId,
                    previousInboundLeg.BridgeRecoveryGeneration,
                    previousInboundLeg.CheckpointGeneration,
                    null,
                    context.CurrentFallbackCheckpointLease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    out _))
            {
                return;
            }

            if (!TryApplyInboundCoordinatorLegMutationLocked(
                    context,
                    new FileTransferCoordinatorEvent(
                        FileTransferCoordinatorEventKind.FallbackCheckpointRetired,
                        context.RouteSelection,
                        FileTransferTransportHandoffKind.None,
                        FileTransferTransportKind.Unknown,
                        reason,
                        FileTransferLegState.RecoveryActive,
                        CanSendData: true,
                        Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                        context.PullHighestReceivedChunkIndex,
                        previousInboundLeg!.TransportEpochId,
                        previousInboundLeg.BridgeRecoveryGeneration,
                        CheckpointRequestId: retiredCheckpointRequestId,
                        FallbackCheckpointLease: context.CurrentFallbackCheckpointLease),
                    FileTransferCoordinatorActionKind.FallbackCheckpointRetire,
                    "retire_fallback_checkpoint",
                    previousInboundLeg,
                    retiredCheckpointRequestId,
                    previousInboundLeg.TransportEpochId,
                    out _))
            {
                return;
            }

            LogFileTransferFallbackCheckpointRetired(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                previousInboundLeg!,
                retiredCheckpointRequestId,
                reason);
        }

        var fallbackTailYieldTransportEpochReauthorizationRequest =
            capturedReauthorizationRequest ??
            TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                transportEpoch,
                reason);
        if (pendingFallbackTailYieldTransportEpochReauthorization is not null &&
            fallbackTailYieldTransportEpochReauthorizationRequest is null)
        {
            return;
        }

        var leg = StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.CheckpointPending,
            canSendData: false,
            transportEpochOverride: transportEpoch,
            bridgeRecoveryGenerationOverride: generation,
            fallbackTailYieldTransportEpochReauthorizationRequest:
                fallbackTailYieldTransportEpochReauthorizationRequest);
        if (leg is null)
        {
            return;
        }

        ResetInboundPostTunaFallbackLegOwnedStateLocked(context, reason, generation, frontier);
        context.PullPostTunaRecoveryActive = true;
        context.PullPostTunaRecoveryGeneration = generation;
        context.PullPostTunaRecoveryFrontierChunkIndex = frontier;
        context.PullPostTunaRecoveryStartedUtc = DateTimeOffset.UtcNow;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_post_tuna_recovery_started; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; recovery_generation={generation}; frontier_chunk_index={frontier}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; credit_until_chunk_index_exclusive={context.V4CreditUntilChunkIndexExclusive}");
    }

    private static void ResetOutboundPostTunaFallbackLegOwnedStateLocked(
        OutboundTransferContext context,
        string reason,
        int generation,
        int frontier)
    {
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            "fallback_leg_owner_replaced");
        var clearedPriorityRequestCount = context.V6PriorityRequestedChunks.Count;
        var clearedNormalRequestCount = context.V6NormalRequestedChunks.Count;
        var clearedMetadataCount = context.V6RequestedChunkMetadataByChunkIndex.Count;
        var clearedInFlightCount = context.V6ChunkSendsInFlight.Count;
        var clearedAwaitingAckCount = context.SentAwaitingAck.Count;
        var clearedRepairQueueCount = context.PullV4SenderPumpRepairQueue.Count;
        var clearedRepairQueuedChunkCount = context.PullV4SenderPumpRepairQueuedChunkIndices.Count;

        context.V6PriorityRequestedChunks.Clear();
        context.V6NormalRequestedChunks.Clear();
        context.V6RequestedChunkMetadataByChunkIndex.Clear();
        context.V6AppliedFrontierRequestIds.Clear();
        context.V6PendingEpochRepairRequestIds.Clear();
        ClearOutboundFallbackCheckpointRepairAuthorityLocked(context);
        context.V6CurrentNormalRequestKey = null;
        context.V6ChunkSendsInFlight.Clear();
        context.SentAwaitingAck.Clear();
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        var senderPipelineGeneration = context.ResetV6SenderPipelineCancellation();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.InFlight = false;
        }

        var committed = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount));
        context.ChunksAcceptedForTransport = committed;
        context.BytesAcceptedForTransport = committed >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)committed * Math.Max(1, context.ChunkSizeBytes));
        context.RemoteGrantedUntilExclusive = committed;
        context.V6RegularNknStateRefreshSendInFlight = 0;
        context.V6RegularNknStateRefreshActiveSendGeneration = 0;
        context.V6RegularNknStateRefreshActiveRequestId = null;
        context.V6RegularNknStateRefreshActivePriority = null;
        context.V6RegularNknStateRefreshActiveStartedUtc = null;
        context.V6RegularNknStateRefreshActiveReusedPendingCheckpoint = false;
        context.V6RegularNknCheckpointSyncSendInFlight = 0;
        ClearOutboundV6RegularNknDeferredStateRefreshLocked(context);
        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(
            context,
            "post_tuna_fallback_leg_started");
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        context.V6PostTunaFallbackNormalSendAheadFreezeChunkIndex = -1;
        context.V6PostTunaFallbackNormalSendAheadFreezeUtc = null;
        context.SparseSenderPumpLastWakeReason = "post_tuna_fallback_leg_started";
        context.V6SenderPumpLastWakeReason = "post_tuna_fallback_leg_started";

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_recovery_generation_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; recovery_generation={generation}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={context.V6TransportEpoch?.EpochId ?? generation}; frontier_chunk_index={frontier}; committed_chunk={committed}; cleared_priority_request_count={clearedPriorityRequestCount}; cleared_normal_request_count={clearedNormalRequestCount}; cleared_metadata_count={clearedMetadataCount}; cleared_in_flight_count={clearedInFlightCount}; cleared_awaiting_ack_count={clearedAwaitingAckCount}; cleared_repair_queue_count={clearedRepairQueueCount}; cleared_repair_queued_chunk_count={clearedRepairQueuedChunkCount}; sender_pipeline_generation={senderPipelineGeneration}; old_credit_discarded=1");
    }

    private static void ResetInboundPostTunaFallbackLegOwnedStateLocked(
        InboundTransferContext context,
        string reason,
        int generation,
        int frontier)
    {
        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            "fallback_leg_owner_replaced");
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.V6LastFrontierRequestChunkIndex = -1;
        context.V6LastFrontierRequestId = null;
        context.V6PostTunaFallbackFrontierRescueChunkIndex = -1;
        context.V6PostTunaFallbackFrontierRescueStep = 0;
        context.V6PostTunaFallbackFrontierRescueRequestCount = 0;
        context.V6PostTunaFallbackFrontierRescueStartedUtc = null;
        context.PullTransportRebindFrontierRepairCommittedChunks = 0;
        context.PullTransportRebindFrontierRepairWindowChunks = V4PostFallbackEmergencyFrontierRepairChunks;
        context.PullTransportRebindFrontierRepairLastCommittedChunkIndex = -1;
        context.PullTransportRebindRecoveredLogged = false;
        context.PullTransportRebindStableProgressSamples = 0;
        context.PullTransportRebindLastObservedNextChunkIndex = context.NextChunkIndex;
        context.PullTransportRebindLastObservedHighestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_fallback_leg_recovery_generation_started; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; recovery_generation={generation}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={context.V6TransportEpoch?.EpochId ?? generation}; frontier_chunk_index={frontier}; committed_chunk={context.NextChunkIndex}; highest_received_chunk_index={context.PullHighestReceivedChunkIndex}; old_frontier_discarded=1");
    }

    private static void EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(
        InboundTransferContext context,
        string reason)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            context.NegotiatedDataProtocolVersion < FileTransferProtocol.ProtocolVersionV6 ||
            context.V6DestinationMode != V6ReceiveDestinationMode.Unknown)
        {
            return;
        }

        var stream = context.WriteStream;
        var streamCanRead = stream?.CanRead == true;
        var streamCanWrite = stream?.CanWrite == true;
        var streamCanSeek = stream?.CanSeek == true;
        var sparseCapable = context.ReceiverSparseWriteActive || (streamCanRead && streamCanSeek);
        context.V6DestinationMode = sparseCapable
            ? V6ReceiveDestinationMode.SparseSeekable
            : V6ReceiveDestinationMode.ContiguousOnly;

        if (context.V6DestinationMode == V6ReceiveDestinationMode.SparseSeekable)
        {
            context.ReceiverSparseWriteActive = true;
            if (context.ReceiverSparseChunksWritten is null && context.ChunkCount > 0)
            {
                context.ReceiverSparseChunksWritten = new BitArray(context.ChunkCount);
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_destination_mode_selected; transfer_id={context.TransferId}; session_id={context.SessionId}; mode={FormatV6DestinationMode(context.V6DestinationMode)}; can_read={(streamCanRead ? 1 : 0)}; can_write={(streamCanWrite ? 1 : 0)}; can_seek={(streamCanSeek ? 1 : 0)}; reason={FormatProtocolLogValue(reason)}; source=live_post_tuna_fallback");
    }

    private async Task<bool> HandlePausedOutboundTransportAsync(OutboundTransferContext context)
    {
        DateTimeOffset? graceDeadlineUtc;
        string reason;
        var routeTransitionPauseRejected = false;
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
            if (string.Equals(
                    reason,
                    "filetransfer_route_transition_draining",
                    StringComparison.Ordinal))
            {
                var held =
                    TryGetOutboundFileTransferRouteTransitionPauseOwnershipLocked(
                        context,
                        out var ownership,
                        out var authority,
                        out var rejectionReason);
                LogOutboundFileTransferRouteTransitionTransportPauseDecisionLocked(
                    context,
                    authority,
                    ownership,
                    held ? "Held" : "Rejected",
                    held
                        ? "coordinator_route_transition_pause_owned"
                        : rejectionReason);
                if (held)
                {
                    return false;
                }

                routeTransitionPauseRejected = true;
            }
        }

        if (!routeTransitionPauseRejected &&
            graceDeadlineUtc is not null &&
            DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        if (IsTunaFallbackTransportPauseReason(reason))
        {
            SessionFileTransferSnapshot? snapshot;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || !context.PullTransportPaused)
                {
                    return true;
                }

                context.PullTransportGraceDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                context.StatusMessage = "Waiting for network recovery.";
                snapshot = CreateSnapshotLocked();
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=tuna_disable_handoff_nkn_pending; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}");
            RaiseTransferChanged(snapshot);
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
        var routeTransitionPauseRejected = false;
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

            if (HasInboundFallbackRepairHandoffLocked(context) && context.FallbackRepairHandoffExecution is { } repairHold &&
                IsInboundFallbackRepairHandoffExecutionCurrentLocked(context, repairHold))
            {
                if (context.PullTransportGraceDeadlineUtc is { } deadline && DateTimeOffset.UtcNow >= deadline)
                {
                    _ = TryRestoreInboundFallbackRepairHandoffLocked(context);
                }
                return false;
            }

            graceDeadlineUtc = context.PullTransportGraceDeadlineUtc;
            reason = context.PullTransportPauseReason ?? "transport_disconnected";
            if (string.Equals(
                    reason,
                    "filetransfer_route_transition_draining",
                    StringComparison.Ordinal))
            {
                var held =
                    TryGetInboundFileTransferRouteTransitionPauseOwnershipLocked(
                        context,
                        out var ownership,
                        out var authority,
                        out var rejectionReason);
                LogInboundFileTransferRouteTransitionTransportPauseDecisionLocked(
                    context,
                    authority,
                    ownership,
                    held ? "Held" : "Rejected",
                    held
                        ? "coordinator_route_transition_pause_owned"
                        : rejectionReason);
                if (held)
                {
                    return false;
                }

                routeTransitionPauseRejected = true;
            }
        }

        if (!routeTransitionPauseRejected &&
            graceDeadlineUtc is not null &&
            DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        if (IsTunaFallbackTransportPauseReason(reason))
        {
            SessionFileTransferSnapshot? snapshot;
            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal || !context.PullTransportPaused)
                {
                    return true;
                }

                context.PullTransportGraceDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                context.StatusMessage = "Waiting for network recovery.";
                snapshot = CreateSnapshotLocked();
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=tuna_disable_handoff_nkn_pending; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}");
            RaiseTransferChanged(snapshot);
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

    private static bool IsTunaFallbackTransportPauseReason(string? reason)
    {
        var normalized = NormalizeReason(reason);
        return normalized is "header_switch_off" or
            "remote_header_switch_off" or
            "runtime_disabled" or
            "wallet_unlinked" or
            "cap_reached" or
            "listener_exit" or
            "listener_exited" or
            "sidecar_exit" or
            "sidecar_exited" or
            "ipc_disconnect" or
            "send_failure" or
            "remote_closed" or
            "remote_remote_closed" or
            "tuna_fallback_to_nkn" or
            "transport_disconnected" or
            "transport_recovered_unproven" or
            "transport_probe_unproven" or
            "receive_stall_recovery" or
            "sender_request_feedback_stalled" or
            "peer_liveness_stale_receive_recovery" or
            "core_filetransfer_request" ||
            normalized?.StartsWith("header_switch_off_", StringComparison.OrdinalIgnoreCase) == true ||
            normalized?.StartsWith("remote_header_switch_off_", StringComparison.OrdinalIgnoreCase) == true ||
            normalized?.StartsWith("helper_switch_off_", StringComparison.OrdinalIgnoreCase) == true ||
            normalized?.Contains("tuna", StringComparison.OrdinalIgnoreCase) == true ||
            normalized?.Contains("sidecar", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsTunaActivationNegotiationTransportPauseReason(string? reason)
    {
        var normalized = NormalizeReason(reason);
        return normalized is "tuna_activation_negotiating" or
            "activation_negotiation_pending" or
            "runtime_unlock_cutthrough_offer_send_prepare" or
            "runtime_unlock_offer_observed_waiting_for_answer" or
            "runtime_unlock_listener_starting";
    }

    private static bool IsTunaActivationNegotiatedTransportReadyReason(string? reason)
        => string.Equals(
            NormalizeReason(reason),
            "tuna_activation_negotiated_transport_ready",
            StringComparison.Ordinal);

    private static bool ShouldDeferTunaActivationReadyResumeUntilRouteHandoff(
        string? pauseReason,
        string reason,
        FileTransferTransportHandoffKind handoffKind)
        => IsTunaActivationNegotiationTransportPauseReason(pauseReason) &&
           (IsTunaActivationNegotiatedTransportReadyReason(reason) ||
            string.Equals(
                NormalizeReason(reason),
                "runtime_unlock_precommit_probe_launch",
                StringComparison.Ordinal)) &&
           handoffKind == FileTransferTransportHandoffKind.None;

    private static void LogTunaActivationReadyResumeDeferred(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_tuna_activation_ready_resume_deferred_until_route_handoff; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; pause_reason=tuna_activation_negotiating");

    private bool ReplaceOutboundDataSessionLocked(OutboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return true;
        }

        if (context.DataSession is null && context.DataSessionGeneration == 0 &&
            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            context.CurrentLiveRouteEpoch is null && context.LastLiveRouteEpochId == 0)
        {
            return AttachInitialFallbackDataSessionLocked(context, session);
        }

        if (context.CoordinatorPersistentState.FileTransferRouteTransition
                .CurrentOperation is { } activeRouteTransition)
        {
            InvalidateOutboundFileTransferRouteTransitionLocked(
                context,
                activeRouteTransition.Authority,
                terminalInvalidation: false,
                "data_session_replaced");

            if (context.CoordinatorPersistentState.FileTransferRouteTransition
                    .CurrentOperation is not null)
            {
                return false;
            }
        }

        if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                context,
                "retry_dispatch_data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=runtime_unlock_retry_scope_invalidation_failed");
            return false;
        }

        if (!TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=receive_recovery_operation_invalidation_failed");
            return false;
        }

        if (context.DataSession is not null)
        {
            UnsubscribeDataSessionEvents(context.DataSession);
            context.DataSession.Dispose();
        }

        InvalidateFallbackTailYieldTransportEpochReauthorizationLocked(
            context,
            "data_session_replaced");
        context.DataSession = session;
        context.DataSessionGeneration = Math.Max(1, context.DataSessionGeneration + 1);
        context.PendingOutboundSparsePumpInstall = null;
        context.InstalledOutboundSparsePump = null;
        context.PendingOutboundSparseFrontierDispatchProofs.Clear();
        SubscribeDataSessionEvents(session);
        TryReplayOutboundRuntimeUnlockPendingPreCommitProbeLocked(context, "data_session_attached");
        RetryOutboundFallbackTailLocalYieldProofIfPendingLocked(context, "data_session_attached");
        return true;
    }

    private bool ReplaceInboundDataSessionLocked(InboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return true;
        }

        if (context.DataSession is null && context.DataSessionGeneration == 0 &&
            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            context.CurrentLiveRouteEpoch is null && context.LastLiveRouteEpochId == 0)
        {
            return AttachInitialFallbackDataSessionLocked(context, session);
        }

        if (context.CoordinatorPersistentState.FileTransferRouteTransition
                .CurrentOperation is { } activeRouteTransition)
        {
            InvalidateInboundFileTransferRouteTransitionLocked(
                context,
                activeRouteTransition.Authority,
                terminalInvalidation: false,
                "data_session_replaced");
            if (context.CoordinatorPersistentState.FileTransferRouteTransition
                    .CurrentOperation is not null)
            {
                return false;
            }
        }

        if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                context,
                "retry_dispatch_data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=runtime_unlock_retry_scope_invalidation_failed");
            return false;
        }

        if (!TryInvalidateReceiveRecoveryOperationForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=receive_recovery_operation_invalidation_failed");
            return false;
        }

        InvalidateCheckpointRepairContinuityLocked(context, "data_session_replaced");
        if (context.DataSession is not null)
        {
            UnsubscribeDataSessionEvents(context.DataSession);
            context.DataSession.Dispose();
        }

        InvalidateFallbackTailYieldTransportEpochReauthorizationLocked(
            context,
            "data_session_replaced");
        context.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        context.DataSession = session;
        var previousDataSessionGeneration = context.DataSessionGeneration;
        context.DataSessionGeneration = Math.Max(1, context.DataSessionGeneration + 1);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_session_recreated; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; terminal_generation={context.CurrentRuntimeOwner?.TerminalGeneration ?? 0}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; transport_epoch={context.CurrentTransferLeg?.TransportEpochId ?? 0}; bridge_recovery_generation={context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0}; bridge_decision_generation={context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0}; route_publication_generation={context.CurrentRoutePublicationToken?.PublicationGeneration ?? 0}; route_commit_generation={context.CurrentRoutePublicationToken?.RouteCommitGeneration ?? 0}; previous_data_session_generation={previousDataSessionGeneration}; data_session_generation={context.DataSessionGeneration}; decision_reason=data_session_replaced");
        ReconcileInboundRegularV4FrontierRepairProjectionLocked(
            context,
            "data_session_replaced");
        SubscribeDataSessionEvents(session);
        TryReplayInboundRuntimeUnlockPendingPreCommitProbeLocked(context, "data_session_attached");
        RetryInboundFallbackTailLocalYieldProofIfPendingLocked(context, "data_session_attached");
        return true;
    }

    private void SubscribeDataSessionEvents(IFileTransferDataSession session)
    {
        session.AvailabilityChanged += OnDataSessionAvailabilityChanged;
        if (session is IFileTransferRuntimeUnlockAuthorityFrameReceiver authorityFrameReceiver)
        {
            authorityFrameReceiver.RuntimeUnlockAuthorityFrameReceived += OnRuntimeUnlockAuthorityFrameReceived;
        }
    }

    private void UnsubscribeDataSessionEvents(IFileTransferDataSession session)
    {
        session.AvailabilityChanged -= OnDataSessionAvailabilityChanged;
        if (session is IFileTransferRuntimeUnlockAuthorityFrameReceiver authorityFrameReceiver)
        {
            authorityFrameReceiver.RuntimeUnlockAuthorityFrameReceived -= OnRuntimeUnlockAuthorityFrameReceived;
        }
    }

    private static void TrimRecentEvents(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek() > TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            events.Dequeue();
        }
    }

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

    private static int GetReceiverPendingChunkCountLocked(InboundTransferContext context)
        => context.PendingChunks.Count + context.ReceiverSparseChunksPendingWrite.Count;

    private static void LogReceiverWriteBatchCommitted(
        InboundTransferContext context,
        InboundWriteBatch batch,
        long writeDurationMs)
    {
        if (!FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receiver_write_batch_committed; transfer_id={context.TransferId}; session_id={context.SessionId}; batch_chunk_count={batch.ChunkCount}; batch_bytes={batch.ByteCount}; write_duration_ms={writeDurationMs}; pending_chunk_count={batch.PendingChunkCountAfterDequeue}; pending_bytes={batch.PendingBytesAfterDequeue}; next_chunk_index={batch.NextChunkIndexAfterDequeue}; highest_received_chunk_index={batch.HighestReceivedChunkIndex}; late_arrival_distance={batch.LateArrivalDistance}; granted_window_bytes={batch.GrantedWindowBytes}");
    }

    private static void LogPullDataFrameReceived(string transferId, string sessionId, FileTransferDataFrame frame)
    {
        LogPullBinaryFrameReceived(transferId, sessionId, frame);
        if (!FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}");
    }

    private static void LogPullDataFrameIgnored(string transferId, string sessionId, FileTransferDataFrame frame, string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; reason={reason}");
    }

    private static string GetFrameChunkIndex(FileTransferDataFrame frame)
        => frame switch
        {
            FileTransferChunkBatchFrameV4 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };

    private static void LogPullBinaryFrameSent(string transferId, string sessionId, FileTransferDataFrame frame, int payloadBytes)
    {
        if (!FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            return;
        }

        var serializedPayloadBytes = FileTransferProtocol.IsV4DataFrame(frame)
            ? FileTransferDataFrameCodec.SerializeLegacyV4(frame).Length
            : FileTransferDataFrameCodec.Serialize(frame).Length;
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        var batchChunkCount = frame is FileTransferChunkBatchFrameV4 ? GetFrameChunkCount(frame) : 0;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; payload_bytes={serializedPayloadBytes}; serialized_payload_bytes={serializedPayloadBytes}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}; batch_chunk_count={batchChunkCount}");
    }

    private static void LogPullBinaryFrameReceived(string transferId, string sessionId, FileTransferDataFrame frame)
    {
        if (!FileTransferDiagnosticLogPolicy.TraceEnabled)
        {
            return;
        }

        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

    private static void LogPullProfileClampForScreenshare(string transferId, string sessionId, string state, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_screenshare_profile; transfer_id={transferId}; session_id={sessionId}; state={state}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private static void LogPullProfileRecoveredAfterScreenshare(string transferId, string sessionId, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_screenshare_profile_recovered; transfer_id={transferId}; session_id={sessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private static void LogPullPipelineChanged(string transferId, string sessionId, FileTransferDirection direction, int pipelineDepth, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pipeline_changed; transfer_id={transferId}; session_id={sessionId}; direction={direction}; pipeline_depth={pipelineDepth}; degraded={(degraded ? 1 : 0)}");
    }

    private static void LogPullChunkProfile(
        string transferId,
        string sessionId,
        int chunkSizeBytes,
        int pipelineDepth,
        bool screenshareActive,
        bool screenshareDegraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v4_chunk_profile; transfer_id={transferId}; session_id={sessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}; screenshare_active={(screenshareActive ? 1 : 0)}; screenshare_degraded={(screenshareDegraded ? 1 : 0)}");
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
}
