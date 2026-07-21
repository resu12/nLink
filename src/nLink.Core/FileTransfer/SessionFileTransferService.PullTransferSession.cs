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

    private async Task<bool> SendInboundV6RepairRequestAsync(InboundTransferContext context, string reason)
    {
        FileTransferFrontierRequestFrameV6? frame;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
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
            await dataSession.SendAsync(frame, context.LifetimeCts.Token).ConfigureAwait(false);
            var firstRange = frame.MissingRanges[0];
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_repair_requested; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={frame.TransportEpoch}; repair_request_id={FormatProtocolLogValue(frame.RepairRequestId ?? "(none)")}; priority={FormatProtocolLogValue(frame.Priority ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; first_start_chunk_index={firstRange.StartChunkIndex}; requested_chunk_count={firstRange.ChunkCount}; range_count={frame.MissingRanges.Count}");
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
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportLastPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryPauseInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
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
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
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

        if (handoffKind == FileTransferTransportHandoffKind.None &&
            !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!CanUseV6TransportEpochsLocked(context))
        {
            if (!TryPromoteOutboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
            {
                return false;
            }

            handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
            targetTransport = FileTransferTransportKind.RegularNkn;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
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

        if (handoffKind == FileTransferTransportHandoffKind.None &&
            !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport) &&
            !ShouldPromoteRegularNknV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        if (!CanUseV6TransportEpochsLocked(context))
        {
            if (!TryPromoteInboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport) &&
                !TryPromoteInboundRegularNknV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
            {
                return false;
            }

            handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
            targetTransport = FileTransferTransportKind.RegularNkn;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
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

            if (handoffKind == FileTransferTransportHandoffKind.None &&
                !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    reason,
                    handoffKind,
                    targetTransport) &&
                !ShouldPromoteRegularNknV4FallbackToPostTunaV6(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    reason,
                    handoffKind,
                    targetTransport))
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
                if (!TryPromoteOutboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
                {
                    return false;
                }

                handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
                targetTransport = FileTransferTransportKind.RegularNkn;
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
                if (!TryPromoteOutboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
                {
                    context.PullTransportResumeRequestPending = false;
                    context.SparseSenderPumpLastWakeReason = "transport_resumed";
                    return true;
                }

                handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
                targetTransport = FileTransferTransportKind.RegularNkn;
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

            if (handoffKind == FileTransferTransportHandoffKind.None &&
                !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    reason,
                    handoffKind,
                    targetTransport))
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
                if (!TryPromoteInboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport) &&
                    !TryPromoteInboundRegularNknV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
                {
                    return false;
                }

                handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
                targetTransport = FileTransferTransportKind.RegularNkn;
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
                if (!TryPromoteInboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport) &&
                    !TryPromoteInboundRegularNknV4FallbackToPostTunaV6Locked(context, reason, handoffKind, targetTransport))
                {
                    context.PullTransportResumeRequestPending = false;
                    return true;
                }

                handoffKind = FileTransferTransportHandoffKind.TunaToNormalFallback;
                targetTransport = FileTransferTransportKind.RegularNkn;
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

    private static bool ShouldPromoteFileTunaV4FallbackToPostTunaV6(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int negotiatedProtocolVersion,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedReason = NormalizeReason(reason);
        if (string.IsNullOrWhiteSpace(normalizedReason) ||
            normalizedReason.Contains("activation", StringComparison.OrdinalIgnoreCase) ||
            IsTunaActivationNegotiationTransportPauseReason(normalizedReason))
        {
            return false;
        }

        return routeRuntime.UsesFileTunaV4Runtime &&
            IsTunaFallbackTransportPauseReason(normalizedReason) &&
            FileTransferCoordinator.CanTransitionToRoute(
                routeRuntime,
                negotiatedProtocolVersion,
                FileTransferRoute.PostTunaFallbackV6,
                handoffKind,
                targetTransport);
    }

    private static bool ShouldPromoteRegularNknV4FallbackToPostTunaV6(
        FileTransferRouteRuntimeDescriptor routeRuntime,
        int negotiatedProtocolVersion,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason is not ("peer_post_tuna_fallback_v6_proof" or
            "current_post_tuna_fallback_live_route_frame"))
        {
            // A post-Tuna fallback leg is valid only after this transfer has
            // committed a file_tuna_v4 runtime leg or the peer has sent an
            // explicit current V6 fallback proof. Treat fallback-shaped
            // availability events during plain regular V4 as activation/setup
            // noise, otherwise stale sidecar state can poison a regular
            // transfer into fallback V6 before file metadata or Tuna proof
            // exists.
            return false;
        }

        return routeRuntime.UsesRegularNknV4FastRuntime &&
            negotiatedProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            targetTransport == FileTransferTransportKind.RegularNkn &&
            FileTransferCoordinator.CanTransitionToRoute(
                routeRuntime,
                negotiatedProtocolVersion,
                FileTransferRoute.PostTunaFallbackV6,
                handoffKind,
                targetTransport);
    }

    private bool TryPromoteOutboundFileTunaV4FallbackToPostTunaV6Locked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        // A proof-backed fallback handoff outranks the abandoned Tuna commit.  All
        // entry points, including late/duplicate precommit ACKs, pass through this
        // method, so keep the precedence check here rather than only in the send-
        // drain callback.
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is
                { } adoptionContinuation)
        {
            TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
                context,
                adoptionContinuation,
                out _);
            return true;
        }

        if (context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff is { } fallbackIntent)
        {
            TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
                context,
                fallbackIntent,
                "tuna_route_commit_suppressed_by_pending_fallback_handoff",
                out _);
            return true;
        }

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: handoffKind == FileTransferTransportHandoffKind.None
                ? FileTransferTransportHandoffKind.TunaToNormalFallback
                : handoffKind,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            routeSelection.HandoffKind,
            FileTransferTransportKind.RegularNkn,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        context.StatusMessage = "Switching to regular NKN fallback.";
        StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "fallback_route_transition", reason, out var routePublicationToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "fallback_route_selected", reason, routePublicationToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "fallback_route_hint", reason, "service_live_route_transition", routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "fallback_live_route_epoch_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "sender", "fallback_runtime_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        return true;
    }

    private bool TryPromoteInboundFileTunaV4FallbackToPostTunaV6Locked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromoteFileTunaV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: handoffKind == FileTransferTransportHandoffKind.None
                ? FileTransferTransportHandoffKind.TunaToNormalFallback
                : handoffKind,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            routeSelection.HandoffKind,
            FileTransferTransportKind.RegularNkn,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        context.StatusMessage = "Switching to regular NKN fallback.";
        EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(context, reason);
        StartInboundPostTunaRecoveryLocked(context, reason);

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "fallback_route_transition", reason, out var routePublicationToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "fallback_route_selected", reason, routePublicationToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "fallback_route_hint", reason, "service_live_route_transition", routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "fallback_live_route_epoch_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "receiver", "fallback_runtime_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        return true;
    }

    private bool TryPromoteInboundRegularNknV4FallbackToPostTunaV6Locked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (context.IsTerminal ||
            !ShouldPromoteRegularNknV4FallbackToPostTunaV6(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                reason,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: handoffKind == FileTransferTransportHandoffKind.None
                ? FileTransferTransportHandoffKind.TunaToNormalFallback
                : handoffKind,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            routeSelection.HandoffKind,
            FileTransferTransportKind.RegularNkn,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        context.StatusMessage = "Switching to regular NKN fallback.";
        EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(context, reason);
        StartInboundPostTunaRecoveryLocked(context, reason);

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "fallback_route_transition", reason, out var routePublicationToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "fallback_route_selected", reason, routePublicationToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "fallback_route_hint", reason, "service_live_route_transition", routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "fallback_live_route_epoch_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "receiver", "fallback_runtime_started", reason, routePublicationToken: routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        return true;
    }

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
            PairedFallbackYieldGranted: HasPairedFallbackTailYieldForCurrentOwner(context));
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
            PairedFallbackYieldGranted: HasPairedFallbackTailYieldForCurrentOwner(context));
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
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission);
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
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission);
        return admission;
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

        if (!TryBindRuntimeUnlockRegularV4RouteCommitDrainToLaunchSnapshotLocked(
                context,
                snapshot,
                out var bindingRejectionReason))
        {
            RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                context,
                snapshot,
                context.IsTerminal ? "Invalidated" : "Rejected",
                bindingRejectionReason,
                context.PendingRuntimeUnlockRegularV4RouteCommitDrain?.DrainGeneration ?? 0);
            return false;
        }

        return TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackCoreLocked(
            context,
            reason,
            "runtime_unlock_probe_missing",
            snapshot);
    }

    private bool TryBindRuntimeUnlockRegularV4RouteCommitDrainToLaunchSnapshotLocked(
        OutboundTransferContext context,
        RuntimeUnlockRouteCommitSnapshot launchSnapshot,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (context.PendingRuntimeUnlockRegularV4RouteCommitDrain is not { } drain)
        {
            return true;
        }

        if (drain.RouteCommitSnapshot.LaunchGeneration == launchSnapshot.LaunchGeneration)
        {
            return IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
                context,
                drain,
                launchSnapshot,
                out rejectionReason);
        }

        if (drain.RouteCommitSnapshot.LaunchGeneration > 0 ||
            launchSnapshot.LaunchGeneration <= 0)
        {
            rejectionReason = "launch_generation_tuple_mutation";
            return false;
        }

        if (!IsRuntimeUnlockRegularV4RouteCommitDrainSourceScopeCurrentForSendRegistrationLocked(
                context,
                drain))
        {
            rejectionReason = "source_scope_replaced";
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.IsRuntimeOwnerCurrent(
                state,
                drain.SourceRuntimeOwner,
                out rejectionReason) ||
            !FileTransferCoordinator.IsRoutePublicationTokenCurrent(
                state,
                drain.SourceRoutePublication,
                out rejectionReason) ||
            context.RuntimeUnlockAdmissionToken != drain.RuntimeUnlockAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                drain.RuntimeUnlockAdmission,
                out rejectionReason) ||
            context.RuntimeUnlockPendingPreCommitProbe != drain.PendingPreCommitProbe ||
            !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                drain.PendingPreCommitProbe,
                out rejectionReason) ||
            !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                launchSnapshot,
                drain.PendingPreCommitProbe,
                out rejectionReason))
        {
            return false;
        }

        if (!IsSameRuntimeUnlockRegularV4RouteCommitLaunchBindingBoundary(
                drain.RouteCommitSnapshot,
                launchSnapshot))
        {
            rejectionReason = "launch_generation_tuple_mutation";
            return false;
        }

        context.PendingRuntimeUnlockRegularV4RouteCommitDrain = drain with
        {
            RouteCommitSnapshot = launchSnapshot,
        };
        return true;
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
        if (snapshot is not { } currentSnapshot ||
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain is not { } drain ||
            context.RuntimeUnlockAdmissionToken != drain.RuntimeUnlockAdmission ||
            context.RuntimeUnlockPendingPreCommitProbe != drain.PendingPreCommitProbe ||
            !IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
                context,
                drain,
                currentSnapshot,
                out _))
        {
            return false;
        }

        var token = drain.RuntimeUnlockAdmission;
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
        LogRuntimeUnlockAdmissionDecision(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            source,
            reason,
            admission);
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
        if (!IsFallbackTailQuiescentRuntimeUnlockResumeReason(reason))
        {
            return;
        }

        var admission = RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_resume", reason);
        if (context.IsTerminal)
        {
            LogRuntimeUnlockFallbackSurvivalResumeRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                "terminal_context",
                admission);
            return;
        }

        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            LogRuntimeUnlockFallbackSurvivalResumeRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                "route_not_post_tuna_fallback",
                admission);
            return;
        }

        if (!admission.Granted)
        {
            var rejectionReason = FormatRuntimeUnlockFallbackAdmissionRejection(
                FileTransferDirection.Outbound,
                admission);
            MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                context,
                reason,
                rejectionReason);
            LogRuntimeUnlockFallbackSurvivalResumeHeld(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                rejectionReason,
                admission);
            return;
        }

        GrantOutboundRuntimeUnlockActivationWindowLocked(context, admission, reason);
        _ = StartOutboundRuntimeUnlockPreCommitProbe(
            context,
            admission.AdmissionToken!.Value,
            reason,
            out _,
            out _,
            pendingReplay: null,
            acceptedSnapshot: null);
    }

    private void TryResumeInboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
        InboundTransferContext context,
        string reason)
    {
        if (!IsFallbackTailQuiescentRuntimeUnlockResumeReason(reason))
        {
            return;
        }

        var admission = RequestRuntimeUnlockAdmissionLocked(context, "fallback_survival_resume", reason);
        if (context.IsTerminal)
        {
            LogRuntimeUnlockFallbackSurvivalResumeRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                "terminal_context",
                admission);
            return;
        }

        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            LogRuntimeUnlockFallbackSurvivalResumeRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                "route_not_post_tuna_fallback",
                admission);
            return;
        }

        if (!admission.Granted)
        {
            var rejectionReason = FormatRuntimeUnlockFallbackAdmissionRejection(
                FileTransferDirection.Inbound,
                admission);
            MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                context,
                reason,
                rejectionReason);
            LogRuntimeUnlockFallbackSurvivalResumeHeld(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                context.CurrentTransferLeg,
                reason,
                rejectionReason,
                admission);
            return;
        }

        GrantInboundRuntimeUnlockActivationWindowLocked(context, admission, reason);
        LogRuntimeUnlockPreCommitResponderWaitingForPeerProbe(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason,
            "fallback_tail_quiescent");
    }

    private static bool IsFallbackTailQuiescentRuntimeUnlockResumeReason(string reason)
        => string.Equals(
            NormalizeReason(reason),
            "fallback_tail_quiescent",
            StringComparison.OrdinalIgnoreCase);

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
        RuntimeUnlockAdmissionDecision admission)
    {
        var eventName = admission.Granted
            ? "filetransfer_runtime_unlock_admission_granted"
            : "filetransfer_runtime_unlock_admission_denied";
        var survival = admission.FallbackSurvivalState;
        var tokenGeneration = admission.AdmissionToken?.AdmissionGeneration ?? 0;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}; admission_generation={tokenGeneration}; admission_reason={FormatProtocolLogValue(admission.Reason)}; route={FormatProtocolLogValue(FormatRuntimeUnlockAdmissionRouteToken(admission.CurrentRoute))}; protocol_version={admission.CurrentProtocolVersion}; live_route_epoch={admission.CurrentLiveRouteEpoch}; runtime_generation={admission.CurrentRuntimeGeneration}; terminal_generation={admission.CurrentTerminalGeneration}; leg_generation={admission.CurrentLegGeneration}; fallback_active={(survival.FallbackActive ? 1 : 0)}; fallback_current={(survival.FallbackCurrent ? 1 : 0)}; fallback_terminalized={(survival.FallbackTerminalized ? 1 : 0)}; checkpoint_delivery_recovery_pending={(survival.CheckpointDeliveryRecoveryPending ? 1 : 0)}; state_refresh_send_in_flight={(survival.StateRefreshSendInFlight ? 1 : 0)}; deferred_state_refresh_pending={(survival.DeferredStateRefreshPending ? 1 : 0)}; proof_replay_pending={(survival.ProofReplayPending ? 1 : 0)}; control_plane_pressure_pending={(survival.ControlPlanePressurePending ? 1 : 0)}; bridge_recovery_pending={(survival.BridgeRecoveryPending ? 1 : 0)}; fallback_tail_repair_pending={(survival.FallbackTailRepairPending ? 1 : 0)}; paired_fallback_yield_granted={(survival.PairedFallbackYieldGranted ? 1 : 0)}");
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

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out _,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out rejectionReason,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason,
        out RouteCommitToken routeCommitToken)
    {
        var retiredLeg = context.CurrentTransferLeg;
        var retiredOwner = context.CurrentRuntimeOwner;
        if (context.RuntimeUnlockAdmissionToken is null)
        {
            var admission = RequestRuntimeUnlockAdmissionLocked(
                context,
                "runtime_unlock_route_commit",
                reason);
            if (!admission.Granted)
            {
                rejectionReason = admission.Reason;
                routeCommitToken = default;
                LogRuntimeUnlockRouteCommitRejected(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    reason,
                    rejectionReason);
                return false;
            }
        }

        var completedSendDrainGeneration =
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain is { } pendingDrain &&
            context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration ==
                pendingDrain.DrainGeneration
                ? pendingDrain.DrainGeneration
                : 0;
        var accepted = TryAcceptRuntimeUnlockRouteCommitLocked(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            CreateCoordinatorStateLocked(context),
            context.RuntimeUnlockAdmissionToken,
            routeSelection,
            Math.Clamp(context.ChunksTransferred, 0, Math.Max(0, context.ChunkCount)),
            Math.Max(-1, context.ChunksTransferred - 1),
            reason,
            () => TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                $"runtime_unlock_route_commit_{reason}"),
            () => CreateCoordinatorStateLocked(context),
            out rejectionReason,
            out routeCommitToken,
            out var decision);
        if (!accepted || decision is null)
        {
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        LogTransferLegFrozenLocked(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            decision.FrozenLeg,
            reason);
        if (decision.StartedLeg is { } leg)
        {
            LogFileTransferLegStarted(FileTransferDirection.Outbound, context.TransferId, context.SessionId, leg, reason);
        }

        if (retiredOwner is { } owner &&
            context.CurrentRuntimeOwner is { } currentOwner &&
            owner.RuntimeGeneration != currentOwner.RuntimeGeneration)
        {
            RetireOutboundFallbackControlPlaneExchangeLocked(
                context,
                retiredLeg,
                context.CurrentTransferLeg?.Generation ?? 0,
                context.CurrentTransferLeg?.CheckpointRequestId,
                reason);
            RetireRuntimeOwnedWorkersLocked(context, owner, reason);
        }

        if (!TryAuthorizeRuntimeUnlockRoutePublicationLocked(context, routeCommitToken, reason, out _, out rejectionReason))
        {
            return false;
        }

        PublishRuntimeUnlockRouteCommitResultIfAuthorizedLocked(
            FileTransferDirection.Outbound,
            context.SessionId,
            context.TransferId,
            routeCommitToken,
            reason,
            completedSendDrainGeneration);
        return true;
    }

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out _,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out rejectionReason,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason,
        out RouteCommitToken routeCommitToken)
    {
        var retiredLeg = context.CurrentTransferLeg;
        var retiredOwner = context.CurrentRuntimeOwner;
        if (context.RuntimeUnlockAdmissionToken is null)
        {
            var admission = RequestRuntimeUnlockAdmissionLocked(
                context,
                "runtime_unlock_route_commit",
                reason);
            if (!admission.Granted)
            {
                rejectionReason = admission.Reason;
                routeCommitToken = default;
                LogRuntimeUnlockRouteCommitRejected(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    reason,
                    rejectionReason);
                return false;
            }
        }

        var accepted = TryAcceptRuntimeUnlockRouteCommitLocked(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            CreateCoordinatorStateLocked(context),
            context.RuntimeUnlockAdmissionToken,
            routeSelection,
            Math.Max(0, context.NextChunkIndex),
            context.PullHighestReceivedChunkIndex,
            reason,
            () => TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                $"runtime_unlock_route_commit_{reason}"),
            () => CreateCoordinatorStateLocked(context),
            out rejectionReason,
            out routeCommitToken,
            out var decision);
        if (!accepted || decision is null)
        {
            return false;
        }

        ApplyCoordinatorDecisionToContextLocked(context, decision);
        LogTransferLegFrozenLocked(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            decision.FrozenLeg,
            reason);
        if (decision.StartedLeg is { } leg)
        {
            LogFileTransferLegStarted(FileTransferDirection.Inbound, context.TransferId, context.SessionId, leg, reason);
        }

        if (retiredOwner is { } owner &&
            context.CurrentRuntimeOwner is { } currentOwner &&
            owner.RuntimeGeneration != currentOwner.RuntimeGeneration)
        {
            RetireInboundFallbackControlPlaneExchangeLocked(
                context,
                retiredLeg,
                context.CurrentTransferLeg?.Generation ?? 0,
                context.CurrentTransferLeg?.CheckpointRequestId,
                reason);
            RetireRuntimeOwnedWorkersLocked(context, owner, reason);
        }

        if (!TryAuthorizeRuntimeUnlockRoutePublicationLocked(context, routeCommitToken, reason, out _, out rejectionReason))
        {
            return false;
        }

        PublishRuntimeUnlockRouteCommitResultIfAuthorizedLocked(
            FileTransferDirection.Inbound,
            context.SessionId,
            context.TransferId,
            routeCommitToken,
            reason);
        return true;
    }

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferCoordinatorState coordinatorState,
        RuntimeUnlockAdmissionToken? admissionToken,
        FileTransferRouteSelection routeSelection,
        int committedChunkIndex,
        int highestObservedChunkIndex,
        string reason,
        Func<bool> retireRegularV4Pressure,
        Func<FileTransferCoordinatorState> getCurrentCoordinatorState,
        out string rejectionReason,
        out RouteCommitToken routeCommitToken,
        out FileTransferCoordinatorDecision? acceptedDecision)
    {
        routeCommitToken = default;
        acceptedDecision = null;
        rejectionReason = "none";
        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            rejectionReason = "transaction_proof_provider_missing";
            LogRuntimeUnlockRouteCommitRejected(
                direction,
                transferId,
                sessionId,
                reason,
                rejectionReason);
            return false;
        }

        if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out var snapshot))
        {
            rejectionReason = "transaction_proof_missing";
            LogRuntimeUnlockRouteCommitRejected(
                direction,
                transferId,
                sessionId,
                reason,
                rejectionReason);
            return false;
        }

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(snapshot, admissionToken);
        var coordinatorEvent = new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                routeSelection,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                reason,
                FileTransferLegState.Active,
                CanSendData: true,
                committedChunkIndex,
                highestObservedChunkIndex,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0,
                RuntimeUnlockCommitProof: proof);
        var decision = FileTransferCoordinator.Apply(coordinatorEvent, coordinatorState);
        if (string.Equals(
                decision.RuntimeUnlockCommitRejectedReason,
                "regular_v4_frontier_repair_pressure_retirement_required",
                StringComparison.Ordinal))
        {
            if (!retireRegularV4Pressure())
            {
                rejectionReason = "regular_v4_frontier_repair_pressure_retirement_failed";
                LogRuntimeUnlockRouteCommitRejected(
                    direction,
                    transferId,
                    sessionId,
                    reason,
                    rejectionReason);
                return false;
            }

            decision = FileTransferCoordinator.Apply(
                coordinatorEvent,
                getCurrentCoordinatorState());
        }

        if (decision.RuntimeUnlockCommitAccepted &&
            decision.RouteCommitToken is { } token &&
            !token.IsDefault)
        {
            routeCommitToken = token;
            acceptedDecision = decision;
            return true;
        }

        rejectionReason = decision.RuntimeUnlockCommitRejectedReason ??
            (decision.RuntimeUnlockCommitAccepted ? "route_commit_token_missing" : "transaction_commit_rejected");
        LogRuntimeUnlockRouteCommitRejected(
            direction,
            transferId,
            sessionId,
            reason,
            rejectionReason);
        return false;
    }

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
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is
                { } adoptionContinuation)
        {
            TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
                context,
                adoptionContinuation,
                out _);
            return true;
        }

        if (context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff is { } fallbackIntent)
        {
            TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
                context,
                fallbackIntent,
                reason,
                out _);
            return true;
        }

        if (context.IsTerminal ||
            !ShouldPromoteRegularNknV4ToFileTunaV4(
                context.RouteRuntime,
                context.NegotiatedDataProtocolVersion,
                handoffKind,
                targetTransport))
        {
            return false;
        }

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        RuntimeUnlockRegularV4RouteCommitDrain? routeCommitDrain = null;
        if (context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            !TryStageRuntimeUnlockRegularV4RouteCommitSendDrainLocked(
                context,
                reason,
                out routeCommitDrain))
        {
            return false;
        }

        if (!EnsureOutboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (routeCommitDrain is not null &&
            !TryCompleteRuntimeUnlockRegularV4RouteCommitSendDrainLocked(
                context,
                routeCommitDrain,
                reason,
                out _))
        {
            return true;
        }

        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out _, out var routeCommitToken))
        {
            if (routeCommitDrain is not null)
            {
                InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
                    context,
                    routeCommitDrain,
                    "coordinator_route_commit_rejected");
            }

            return false;
        }

        if (routeCommitDrain is not null &&
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain == routeCommitDrain)
        {
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain = null;
            context.RuntimeUnlockRegularV4RouteCommitDrainRecheckQueued = false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch || routeCommitToken.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                reason,
                routeCommitToken.IsDefault ? "route_commit_token_missing" : "live_route_epoch_missing");
            return false;
        }

        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetOutboundV4AcceptedAfterPauseLocked(context, "live_route_tuna_activated");
        ResetOutboundRegularNknV4StateForFileTunaV4Locked(context);
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SparseSenderPumpLastWakeReason = "live_route_tuna_activated";

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_route_transition", reason, out var routePublicationToken, routeCommitToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "runtime_unlock_route_selected", reason, routePublicationToken, routeCommitToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "runtime_unlock_route_hint", reason, "service_live_route_transition", routeCommitToken: routeCommitToken, routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_live_route_epoch_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "sender", "runtime_unlock_runtime_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        if (!TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(context, "runtime_unlock_live_route_epoch_recovered", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }
        return true;
    }

    private bool TryStageRuntimeUnlockRegularV4RouteCommitSendDrainLocked(
        OutboundTransferContext context,
        string reason,
        out RuntimeUnlockRegularV4RouteCommitDrain drain)
    {
        drain = null!;
        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var snapshot))
        {
            return false;
        }

        return TryStageRuntimeUnlockRegularV4RouteCommitSendDrainFromSnapshotLocked(
            context,
            reason,
            snapshot,
            out drain);
    }

    private bool TryStageRuntimeUnlockRegularV4RouteCommitSendDrainFromSnapshotLocked(
        OutboundTransferContext context,
        string reason,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        out RuntimeUnlockRegularV4RouteCommitDrain drain)
    {
        drain = null!;
        if (!IsRuntimeUnlockRegularV4RouteCommitBoundarySnapshot(snapshot, context))
        {
            return false;
        }

        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } runtimeOwner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } routePublication ||
            context.CurrentTransferLeg is not { } leg ||
            context.DataSession is not { } dataSession)
        {
            return false;
        }

        var coordinatorState = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.IsRuntimeOwnerCurrent(
                coordinatorState,
                runtimeOwner,
                out _) ||
            !FileTransferCoordinator.IsRoutePublicationTokenCurrent(
                coordinatorState,
                routePublication,
                out _))
        {
            return false;
        }

        if (!TryCaptureRuntimeUnlockPreCommitAuthoritiesLocked(
                context,
                snapshot,
                out var runtimeUnlockAdmission,
                out var pendingPreCommitProbe))
        {
            return false;
        }

        if (!context.PullTransportPaused &&
            TryPauseOutboundTransportLocked(
                context,
                "tuna_activation_negotiating",
                requiresResumeRequest: true))
        {
            LogTransportPaused(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                "tuna_activation_negotiating");
            context.SignalSparseSenderPump();
        }

        var candidate = new RuntimeUnlockRegularV4RouteCommitDrain(
            Math.Max(1, context.LastRuntimeUnlockRegularV4RouteCommitDrainGeneration + 1),
            runtimeOwner,
            routePublication,
            context.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            dataSession,
            context.DataSessionGeneration,
            runtimeUnlockAdmission,
            pendingPreCommitProbe,
            context.LastOutboundRegularV4PhysicalSendLeaseGeneration,
            0,
            snapshot,
            NormalizeReason(reason) ?? "unknown");
        candidate = candidate with
        {
            CapturedSendCount = CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
                context,
                candidate),
        };
        if (context.PendingRuntimeUnlockRegularV4RouteCommitDrain is { } pending)
        {
            if (IsSameRuntimeUnlockRegularV4RouteCommitDrainBoundary(pending, candidate) &&
                IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
                    context,
                    pending,
                    snapshot,
                    out _) &&
                IsAllowedRuntimeUnlockRegularV4RouteCommitProbeProgression(
                    pending.RouteCommitSnapshot,
                    snapshot))
            {
                var normalizedReason = NormalizeReason(reason) ?? pending.Reason;
                drain = pending.RouteCommitSnapshot == snapshot &&
                        string.Equals(pending.Reason, normalizedReason, StringComparison.Ordinal)
                    ? pending
                    : pending with
                    {
                        RouteCommitSnapshot = snapshot,
                        Reason = normalizedReason,
                    };
                context.PendingRuntimeUnlockRegularV4RouteCommitDrain = drain;
                return true;
            }

            InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
                context,
                pending,
                "route_commit_boundary_replaced");
        }

        context.LastRuntimeUnlockRegularV4RouteCommitDrainGeneration = candidate.DrainGeneration;
        context.PendingRuntimeUnlockRegularV4RouteCommitDrain = candidate;
        context.RuntimeUnlockRegularV4RouteCommitDrainRecheckQueued = false;
        drain = candidate;
        LogRuntimeUnlockRegularV4RouteCommitSendDrain(
            "runtime_unlock_regular_v4_route_commit_send_drain_staged",
            context,
            candidate,
            CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(context, candidate),
            "route_commit_boundary_staged");
        return true;
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
            if (!IsRuntimeUnlockRegularV4PreCommitAuthorityCreationSnapshot(
                    snapshot,
                    context))
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

    private bool TryCompleteRuntimeUnlockRegularV4RouteCommitSendDrainLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        string reason,
        out string rejectionReason)
    {
        if (context.PendingRuntimeUnlockRegularV4RouteCommitDrain != drain)
        {
            rejectionReason = "route_commit_send_drain_replaced";
            return false;
        }

        if (!IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentLocked(
                context,
                drain,
                out rejectionReason))
        {
            InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
                context,
                drain,
                rejectionReason);
            return false;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                context.SessionId,
                context.TransferId,
                out var currentSnapshot) ||
            !IsRuntimeUnlockSamePathProbeAcked(currentSnapshot) ||
            !IsAllowedRuntimeUnlockRegularV4RouteCommitProbeProgression(
                drain.RouteCommitSnapshot,
                currentSnapshot))
        {
            rejectionReason = "route_commit_probe_not_acked_for_send_drain";
            return false;
        }

        var activeSendCount = CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
            context,
            drain);
        if (activeSendCount > 0)
        {
            rejectionReason = "regular_v4_physical_sends_in_flight";
            LogRuntimeUnlockRegularV4RouteCommitSendDrain(
                "runtime_unlock_regular_v4_route_commit_send_drain_deferred",
                context,
                drain,
                activeSendCount,
                rejectionReason);
            return false;
        }

        rejectionReason = "none";
        if (context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration !=
            drain.DrainGeneration)
        {
            context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration =
                drain.DrainGeneration;
            LogRuntimeUnlockRegularV4RouteCommitSendDrain(
                "runtime_unlock_regular_v4_route_commit_send_drain_completed",
                context,
                drain,
                activeSendCount,
                reason);
        }

        return true;
    }

    private bool IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        out string rejectionReason)
        => IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentCoreLocked(
            context,
            drain,
            out rejectionReason,
            suppliedSnapshot: null);

    private bool IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        RuntimeUnlockRouteCommitSnapshot suppliedSnapshot,
        out string rejectionReason)
        => IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentCoreLocked(
            context,
            drain,
            out rejectionReason,
            suppliedSnapshot);

    private bool IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentCoreLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        out string rejectionReason,
        RuntimeUnlockRouteCommitSnapshot? suppliedSnapshot = null)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            rejectionReason = context.IsTerminal ? "terminal_context" : "transfer_context_replaced";
            return false;
        }

        if (!context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.CurrentRuntimeOwner != drain.SourceRuntimeOwner ||
            context.CurrentRoutePublicationToken != drain.SourceRoutePublication ||
            context.TerminalGeneration != drain.SourceTerminalGeneration ||
            context.CurrentTransferLeg is not { } leg ||
            leg.Generation != drain.SourceLegGeneration ||
            leg.TransportEpochId != drain.SourceTransportEpoch ||
            leg.BridgeRecoveryGeneration != drain.SourceBridgeRecoveryGeneration ||
            !ReferenceEquals(context.DataSession, drain.SourceDataSession) ||
            context.DataSessionGeneration != drain.SourceDataSessionGeneration)
        {
            rejectionReason = "source_scope_replaced";
            return false;
        }

        var state = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.IsRuntimeOwnerCurrent(
                state,
                drain.SourceRuntimeOwner,
                out rejectionReason) ||
            !FileTransferCoordinator.IsRoutePublicationTokenCurrent(
                state,
                drain.SourceRoutePublication,
                out rejectionReason) ||
            context.RuntimeUnlockAdmissionToken != drain.RuntimeUnlockAdmission ||
            !FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                state,
                drain.RuntimeUnlockAdmission,
                out rejectionReason) ||
            context.RuntimeUnlockPendingPreCommitProbe != drain.PendingPreCommitProbe ||
            !FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
                state,
                drain.PendingPreCommitProbe,
                out rejectionReason))
        {
            return false;
        }

        RuntimeUnlockRouteCommitSnapshot currentSnapshot;
        if (suppliedSnapshot is { } acceptedSnapshot)
        {
            currentSnapshot = acceptedSnapshot;
        }
        else if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
                 !proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                     context.SessionId,
                     context.TransferId,
                     out currentSnapshot))
        {
            rejectionReason = "route_commit_transaction_replaced";
            return false;
        }

        if (!RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
                currentSnapshot,
                drain.PendingPreCommitProbe,
                out rejectionReason))
        {
            return false;
        }

        if (!IsAllowedRuntimeUnlockRegularV4RouteCommitProbeProgression(
                drain.RouteCommitSnapshot,
                currentSnapshot))
        {
            rejectionReason = "route_commit_transaction_replaced";
            return false;
        }

        rejectionReason = "none";
        return true;
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

    private static bool IsSameRuntimeUnlockRegularV4RouteCommitDrainBoundary(
        RuntimeUnlockRegularV4RouteCommitDrain left,
        RuntimeUnlockRegularV4RouteCommitDrain right)
        => left.SourceRuntimeOwner == right.SourceRuntimeOwner &&
           left.SourceRoutePublication == right.SourceRoutePublication &&
           left.SourceTerminalGeneration == right.SourceTerminalGeneration &&
           left.SourceLegGeneration == right.SourceLegGeneration &&
           left.SourceTransportEpoch == right.SourceTransportEpoch &&
           left.SourceBridgeRecoveryGeneration == right.SourceBridgeRecoveryGeneration &&
           ReferenceEquals(left.SourceDataSession, right.SourceDataSession) &&
           left.SourceDataSessionGeneration == right.SourceDataSessionGeneration &&
           left.RuntimeUnlockAdmission == right.RuntimeUnlockAdmission &&
           left.PendingPreCommitProbe == right.PendingPreCommitProbe &&
           IsSameRuntimeUnlockRegularV4RouteCommitSnapshotBoundary(
               left.RouteCommitSnapshot,
               right.RouteCommitSnapshot);

    private static int CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain)
        => context.ActiveOutboundRegularV4PhysicalSendLeases.Values.Count(lease =>
            IsOutboundRegularV4PhysicalSendLeaseForDrain(lease, drain));

    private static bool IsOutboundRegularV4PhysicalSendLeaseForDrain(
        OutboundRegularV4PhysicalSendLease lease,
        RuntimeUnlockRegularV4RouteCommitDrain drain)
        => lease.LeaseGeneration > 0 &&
           lease.RuntimeOwner == drain.SourceRuntimeOwner &&
           lease.RoutePublication == drain.SourceRoutePublication &&
           lease.TerminalGeneration == drain.SourceTerminalGeneration &&
           lease.LegGeneration == drain.SourceLegGeneration &&
           lease.TransportEpoch == drain.SourceTransportEpoch &&
           lease.BridgeRecoveryGeneration == drain.SourceBridgeRecoveryGeneration &&
           ReferenceEquals(lease.DataSession, drain.SourceDataSession) &&
           lease.DataSessionGeneration == drain.SourceDataSessionGeneration &&
           lease.LeaseGeneration <= drain.CapturedThroughSendLeaseGeneration;

    private static void InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        string reason)
    {
        if (context.PendingRuntimeUnlockRegularV4RouteCommitDrain != drain)
        {
            return;
        }

        var activeSendCount = CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
            context,
            drain);
        InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
            context,
            drain,
            reason);
        context.PendingRuntimeUnlockRegularV4RouteCommitDrain = null;
        context.RuntimeUnlockRegularV4RouteCommitDrainRecheckQueued = false;
        LogRuntimeUnlockRegularV4RouteCommitSendDrain(
            "runtime_unlock_regular_v4_route_commit_send_drain_invalidated",
            context,
            drain,
            activeSendCount,
            reason);
    }

    private static void InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        string reason)
    {
        var pending = context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff;
        var continuation = context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation;
        if ((pending is null || pending.RouteCommitDrain != drain) &&
            (continuation is null || continuation.Intent.RouteCommitDrain != drain))
        {
            return;
        }

        var exactCoordinatorCommitInProgress =
            string.Equals(reason, "coordinator_source_scope_replaced", StringComparison.Ordinal) &&
            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration == drain.DrainGeneration;
        if (exactCoordinatorCommitInProgress)
        {
            return;
        }

        if (continuation is not null && continuation.Intent.RouteCommitDrain == drain)
        {
            TryDiscardRuntimeUnlockUncommittedTunaFallbackAdoptionStage(
                continuation.AdoptionBridge,
                continuation.StageReceipt.Stage,
                reason);
            context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation = null;
        }

        context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = null;
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackRetainedPeerProof is { } retained &&
            retained.HandoffGeneration ==
                (pending?.HandoffGeneration ?? continuation?.Intent.HandoffGeneration))
        {
            context.PendingRuntimeUnlockUncommittedTunaFallbackRetainedPeerProof = null;
        }

        var invalidatedIntent = pending ?? continuation?.Intent;
        if (invalidatedIntent is null)
        {
            return;
        }

        LogRuntimeUnlockUncommittedTunaFallbackHandoff(
            "runtime_unlock_uncommitted_tuna_fallback_handoff_invalidated",
            context,
            invalidatedIntent,
            "Invalidated",
            reason);
    }

    private static void LogRuntimeUnlockRegularV4RouteCommitSendDrain(
        string eventName,
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain,
        int activeSendCount,
        string reason)
    {
        var snapshot = drain.RouteCommitSnapshot;
        var matchingRetiredSendCount = Math.Max(
            0,
            drain.CapturedSendCount - activeSendCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; drain_generation={drain.DrainGeneration}; source_route={FormatProtocolLogValue(drain.SourceRuntimeOwner.RouteToken)}; source_protocol_version={drain.SourceRuntimeOwner.ProtocolVersion}; source_live_route_epoch={drain.SourceRuntimeOwner.LiveRouteEpoch}; source_runtime_generation={drain.SourceRuntimeOwner.RuntimeGeneration}; source_terminal_generation={drain.SourceTerminalGeneration}; source_leg_generation={drain.SourceLegGeneration}; source_transport_epoch={drain.SourceTransportEpoch}; source_bridge_recovery_generation={drain.SourceBridgeRecoveryGeneration}; source_route_publication_generation={drain.SourceRoutePublication.PublicationGeneration}; source_route_commit_generation={drain.SourceRoutePublication.RouteCommitGeneration ?? 0}; source_data_session_generation={drain.SourceDataSessionGeneration}; admission_generation={drain.RuntimeUnlockAdmission.AdmissionGeneration}; pending_precommit_generation={drain.PendingPreCommitProbe.PendingGeneration}; transaction_generation={snapshot.TransactionGeneration}; offer_generation={snapshot.OfferGeneration}; tuna_path_lease_generation={snapshot.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(snapshot.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={snapshot.LaunchGeneration}; probe_id={FormatProtocolLogValue(snapshot.PathProbeId ?? "(none)")}; probe_state={FormatProtocolLogValue(snapshot.PathProbeState)}; target_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(snapshot.TargetRoute))}; target_protocol_version={snapshot.ProtocolVersion}; target_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(snapshot.TargetTransport))}; handoff_kind={FormatProtocolLogValue(FormatFileTransferTransportHandoffKind(snapshot.HandoffKind))}; captured_through_send_lease_generation={drain.CapturedThroughSendLeaseGeneration}; captured_send_count={drain.CapturedSendCount}; matching_retired_send_count={matchingRetiredSendCount}; active_send_count={activeSendCount}; registered_send_count={context.OutboundRegularV4PhysicalSendRegisteredCount}; retired_send_count={context.OutboundRegularV4PhysicalSendRetiredCount}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private bool TryRetainRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
        OutboundTransferContext context,
        IFileTransferDataSession dataSession,
        PostTunaFallbackPeerProofEvidence evidence,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot,
        RuntimeUnlockRouteCommitSnapshot? expectedPreCommitSnapshot = null)
    {
        transferSnapshot = null;
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is { } continuation &&
            ReferenceEquals(continuation.Intent.SourceDataSession, dataSession) &&
            continuation.Intent.PeerProofEvidence == evidence)
        {
            TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
                context,
                continuation,
                out transferSnapshot);
            return true;
        }

        if (!evidence.IsComplete ||
            !string.Equals(evidence.SessionId, context.SessionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.TransferId, context.TransferId, StringComparison.Ordinal) ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain is not { } drain ||
            context.RuntimeUnlockRouteCommitToken is not null ||
            (expectedPreCommitSnapshot is not null &&
             expectedPreCommitSnapshot != drain.RouteCommitSnapshot) ||
            !IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
                context,
                drain,
                drain.RouteCommitSnapshot,
                out _))
        {
            return false;
        }

        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is
                { } replacedContinuation &&
            ReferenceEquals(replacedContinuation.Intent.SourceDataSession, dataSession))
        {
            if (replacedContinuation.Intent.PeerProofEvidence is not { } currentEvidence ||
                !currentEvidence.CanBeReplacedBy(evidence))
            {
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    replacedContinuation.Intent,
                    "Rejected",
                    "peer_proof_replacement_stale");
                return true;
            }

            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                replacedContinuation.Intent.RouteCommitDrain,
                "peer_proof_replaced");
        }

        var pending = context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff;
        if (pending is not null &&
            (!IsSameRuntimeUnlockRegularV4RouteCommitDrainBoundary(pending.RouteCommitDrain, drain) ||
             pending.RuntimeUnlockAdmission != drain.RuntimeUnlockAdmission ||
             pending.PendingPreCommitProbe != drain.PendingPreCommitProbe ||
             !ReferenceEquals(pending.SourceDataSession, dataSession) ||
             pending.SourceDataSessionGeneration != context.DataSessionGeneration))
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                pending.RouteCommitDrain,
                "handoff_scope_replaced");
            pending = null;
        }

        RuntimeUnlockUncommittedTunaFallbackHandoffIntent intent;
        if (pending is null)
        {
            var generation = Math.Max(
                1,
                context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration + 1);
            context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration = generation;
            intent = new RuntimeUnlockUncommittedTunaFallbackHandoffIntent(
                generation,
                drain.SourceRuntimeOwner,
                drain.SourceRoutePublication,
                drain.RuntimeUnlockAdmission,
                drain.PendingPreCommitProbe,
                drain,
                dataSession,
                context.DataSessionGeneration,
                evidence,
                PeerProofFrame: null,
                ProofTransportEpoch: evidence.TransportEpoch,
                NormalizeReason(reason) ?? "transport_recovered");
            context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = intent;
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_retained",
                context,
                intent,
                "Retained",
                "proof_scoped_transport_recovered");
        }
        else
        {
            var peerProofEvidence = evidence;
            if (pending.PeerProofEvidence is { } retainedEvidence &&
                retainedEvidence != peerProofEvidence)
            {
                if (!retainedEvidence.CanBeReplacedBy(peerProofEvidence))
                {
                    LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                        "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                        context,
                        pending,
                        "Rejected",
                        "peer_proof_replacement_stale");
                    return true;
                }

                InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                    context,
                    pending.RouteCommitDrain,
                    "peer_proof_replaced");
                pending = null;
            }

            if (pending is not null &&
                pending.PeerProofFrame is not null &&
                pending.ProofTransportEpoch != peerProofEvidence.TransportEpoch)
            {
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    pending,
                    "Rejected",
                    "availability_peer_proof_transport_epoch_mismatch");
                return true;
            }

            if (pending is not null &&
                pending.PeerProofFrame is { } retainedPeerProofFrame &&
                !IsRuntimeUnlockUncommittedTunaFallbackEvidenceForPeerFrame(
                    evidence,
                    retainedPeerProofFrame))
            {
                if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is null &&
                    pending.PeerProofEvidence is null &&
                    pending.ProofTransportEpoch == peerProofEvidence.TransportEpoch)
                {
                    pending = pending with
                    {
                        PeerProofEvidence = peerProofEvidence,
                    };
                    context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = pending;
                    LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                        "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                        context,
                        pending,
                        "Held",
                        "peer_proof_exact_frame_pending");
                    return true;
                }

                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    pending,
                    "Rejected",
                    "availability_peer_proof_frame_identity_mismatch");
                return true;
            }

            if (pending is null)
            {
                var replacementGeneration = Math.Max(
                    1,
                    context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration + 1);
                context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration =
                    replacementGeneration;
                intent = new RuntimeUnlockUncommittedTunaFallbackHandoffIntent(
                    replacementGeneration,
                    drain.SourceRuntimeOwner,
                    drain.SourceRoutePublication,
                    drain.RuntimeUnlockAdmission,
                    drain.PendingPreCommitProbe,
                    drain,
                    dataSession,
                    context.DataSessionGeneration,
                    peerProofEvidence,
                    PeerProofFrame: null,
                    ProofTransportEpoch: peerProofEvidence.TransportEpoch,
                    NormalizeReason(reason) ?? "transport_recovered");
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_retained",
                    context,
                    intent,
                    "Retained",
                    "peer_proof_replacement_retained");
            }
            else
            {
                intent = pending.PeerProofEvidence is not null
                    ? pending
                    : pending with
                    {
                        PeerProofEvidence = peerProofEvidence,
                        ProofTransportEpoch = peerProofEvidence.TransportEpoch,
                    };
            }

            context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = intent;
        }

        TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
            context,
            intent,
            reason,
            out transferSnapshot);
        return true;
    }

    private bool TryRetainRuntimeUnlockUncommittedTunaFallbackPeerProofLocked(
        OutboundTransferContext context,
        FileTransferDataFrame frame,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is { } continuation &&
            continuation.Intent.PeerProofFrame is { } continuationFrame &&
            IsSameRuntimeUnlockUncommittedTunaFallbackPeerProofFrame(continuationFrame, frame))
        {
            TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
                context,
                continuation,
                out transferSnapshot);
            return true;
        }

        if (!IsPeerPostTunaFallbackV6RuntimeProofFrame(frame) ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesRegularNknV4FastRuntime ||
            context.NegotiatedDataProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain is not { } drain ||
            context.RuntimeUnlockRouteCommitToken is not null ||
            context.DataSession is not { } dataSession ||
            !IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
                context,
                drain,
                drain.RouteCommitSnapshot,
                out _))
        {
            return false;
        }

        var proofTransportEpoch = ResolveV6RuntimeProofTransportEpoch(frame);
        if (proofTransportEpoch <= 0)
        {
            return false;
        }

        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is
                { } replacedContinuation &&
            replacedContinuation.Intent.PeerProofFrame is not null)
        {
            if (proofTransportEpoch <= replacedContinuation.Intent.ProofTransportEpoch)
            {
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    replacedContinuation.Intent,
                    "Rejected",
                    "peer_proof_frame_replacement_stale");
                return true;
            }

            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                replacedContinuation.Intent.RouteCommitDrain,
                "peer_proof_frame_replaced");
        }

        var pending = context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff;
        if (pending is not null &&
            (!IsSameRuntimeUnlockRegularV4RouteCommitDrainBoundary(pending.RouteCommitDrain, drain) ||
             pending.RuntimeUnlockAdmission != drain.RuntimeUnlockAdmission ||
             pending.PendingPreCommitProbe != drain.PendingPreCommitProbe ||
             !ReferenceEquals(pending.SourceDataSession, dataSession) ||
             pending.SourceDataSessionGeneration != context.DataSessionGeneration))
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                pending.RouteCommitDrain,
                "handoff_scope_replaced");
            pending = null;
        }

        RuntimeUnlockUncommittedTunaFallbackHandoffIntent intent;
        if (pending is null)
        {
            var generation = Math.Max(
                1,
                context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration + 1);
            context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration = generation;
            intent = new RuntimeUnlockUncommittedTunaFallbackHandoffIntent(
                generation,
                drain.SourceRuntimeOwner,
                drain.SourceRoutePublication,
                drain.RuntimeUnlockAdmission,
                drain.PendingPreCommitProbe,
                drain,
                dataSession,
                context.DataSessionGeneration,
                PeerProofEvidence: null,
                frame,
                proofTransportEpoch,
                NormalizeReason(reason) ?? "peer_post_tuna_fallback_v6_proof");
            context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = intent;
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_retained",
                context,
                intent,
                "Retained",
                "peer_proof_before_availability");
        }
        else
        {
            if (pending.PeerProofEvidence is { } availabilityEvidence &&
                availabilityEvidence.TransportEpoch != proofTransportEpoch)
            {
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    pending,
                    "Rejected",
                    "peer_proof_transport_epoch_mismatch");
                return true;
            }


            if (pending.PeerProofEvidence is { } exactAvailabilityEvidence &&
                !IsRuntimeUnlockUncommittedTunaFallbackEvidenceForPeerFrame(
                    exactAvailabilityEvidence,
                    frame))
            {
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                    context,
                    pending,
                    "Rejected",
                    "peer_proof_frame_identity_mismatch");
                return true;
            }

            if (pending.PeerProofFrame is { } retainedFrame &&
                !IsSameRuntimeUnlockUncommittedTunaFallbackPeerProofFrame(
                    retainedFrame,
                    frame))
            {
                var isExactAvailabilityBoundReplacement =
                    context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation is null &&
                    proofTransportEpoch == pending.ProofTransportEpoch &&
                    pending.PeerProofEvidence is { } retainedAvailability &&
                    IsRuntimeUnlockUncommittedTunaFallbackEvidenceForPeerFrame(
                        retainedAvailability,
                        frame);
                if (isExactAvailabilityBoundReplacement)
                {
                    pending = pending with
                    {
                        PeerProofFrame = frame,
                        ProofTransportEpoch = proofTransportEpoch,
                    };
                    context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = pending;
                }
                else if (proofTransportEpoch <= pending.ProofTransportEpoch)
                {
                    LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                        "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                        context,
                        pending,
                        "Rejected",
                        "peer_proof_frame_replacement_stale");
                    return true;
                }
                else
                {
                    InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                        context,
                        pending.RouteCommitDrain,
                        "peer_proof_frame_replaced");
                    pending = null;
                }
            }

            if (pending is null)
            {
                var replacementGeneration = Math.Max(
                    1,
                    context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration + 1);
                context.LastRuntimeUnlockUncommittedTunaFallbackHandoffGeneration =
                    replacementGeneration;
                intent = new RuntimeUnlockUncommittedTunaFallbackHandoffIntent(
                    replacementGeneration,
                    drain.SourceRuntimeOwner,
                    drain.SourceRoutePublication,
                    drain.RuntimeUnlockAdmission,
                    drain.PendingPreCommitProbe,
                    drain,
                    dataSession,
                    context.DataSessionGeneration,
                    PeerProofEvidence: null,
                    frame,
                    proofTransportEpoch,
                    NormalizeReason(reason) ?? "peer_post_tuna_fallback_v6_proof");
                LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                    "runtime_unlock_uncommitted_tuna_fallback_handoff_retained",
                    context,
                    intent,
                    "Retained",
                    "peer_proof_frame_replacement_retained");
            }
            else
            {
                intent = pending.PeerProofFrame is not null
                    ? pending
                    : pending with
                {
                    PeerProofFrame = frame,
                    ProofTransportEpoch = proofTransportEpoch,
                };
            }

            context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = intent;
        }

        LogRuntimeUnlockUncommittedTunaFallbackHandoff(
            "runtime_unlock_uncommitted_tuna_fallback_peer_proof_observed",
            context,
            intent,
            "Observed",
            reason);
        TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
            context,
            intent,
            reason,
            out transferSnapshot);
        return true;
    }

    private bool TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
        OutboundTransferContext context,
        RuntimeUnlockUncommittedTunaFallbackHandoffIntent intent,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot,
        bool allowStaleAdoptionStageRestage = true)
    {
        transferSnapshot = null;
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff != intent)
        {
            return false;
        }

        if (!IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrentLocked(
                context,
                intent,
                out var currentnessReason))
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                intent.RouteCommitDrain,
                currentnessReason);
            return false;
        }

        if (intent.PeerProofEvidence is null || intent.PeerProofFrame is null)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Held",
                intent.PeerProofEvidence is null
                    ? "proof_scoped_availability_pending"
                    : "peer_v6_proof_frame_pending");
            return false;
        }

        if (intent.PeerProofEvidence.TransportEpoch != intent.ProofTransportEpoch ||
            ResolveV6RuntimeProofTransportEpoch(intent.PeerProofFrame) != intent.ProofTransportEpoch ||
            !IsRuntimeUnlockUncommittedTunaFallbackEvidenceForPeerFrame(
                intent.PeerProofEvidence,
                intent.PeerProofFrame) ||
            !string.Equals(
                intent.PeerProofFrame.SessionId,
                intent.PeerProofEvidence.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.PeerProofFrame.TransferId,
                intent.PeerProofEvidence.TransferId,
                StringComparison.Ordinal))
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                "peer_proof_availability_tuple_mismatch");
            return false;
        }

        var activeSendCount = CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
            context,
            intent.RouteCommitDrain);
        if (activeSendCount > 0)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Held",
                "regular_v4_physical_sends_in_flight");
            return false;
        }

        if (!TryCompleteRuntimeUnlockRegularV4RouteCommitSendDrainLocked(
                context,
                intent.RouteCommitDrain,
                "uncommitted_tuna_fallback_handoff",
                out var drainRejectionReason))
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Held",
                drainRejectionReason);
            return false;
        }

        var coordinatorHandoff = new RuntimeUnlockUncommittedTunaFallbackHandoff(
            intent.SourceRuntimeOwner,
            intent.SourceRoutePublication,
            intent.RuntimeUnlockAdmission,
            intent.PendingPreCommitProbe,
            intent.RouteCommitDrain.RouteCommitSnapshot,
            intent.RouteCommitDrain.SourceTerminalGeneration,
            intent.RouteCommitDrain.SourceLegGeneration,
            intent.RouteCommitDrain.SourceTransportEpoch,
            intent.RouteCommitDrain.SourceBridgeRecoveryGeneration,
            intent.SourceDataSessionGeneration,
            intent.RouteCommitDrain.DrainGeneration,
            context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration,
            intent.PeerProofEvidence!,
            intent.ProofTransportEpoch,
            intent.HandoffGeneration,
            NormalizeReason(reason) ?? intent.Reason);
        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        var bridgeRecoveryGeneration = Math.Max(
            1,
            intent.RouteCommitDrain.SourceBridgeRecoveryGeneration + 1);
        var coordinatorEvent = new FileTransferCoordinatorEvent(
            FileTransferCoordinatorEventKind.TunaLost,
            routeSelection,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn,
            "uncommitted_tuna_fallback_handoff",
            FileTransferLegState.CheckpointPending,
            CanSendData: false,
            Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
            Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            intent.ProofTransportEpoch,
            bridgeRecoveryGeneration,
            RuntimeUnlockUncommittedTunaFallbackHandoff: coordinatorHandoff);
        var coordinatorState = CreateCoordinatorStateLocked(context);
        var prepared = FileTransferCoordinator.PrepareRuntimeUnlockUncommittedTunaFallbackHandoff(
            coordinatorState,
            coordinatorEvent);
        if (prepared.Status !=
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Prepared ||
            prepared.Transition is not { } transition)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                prepared.Reason);
            return false;
        }

        RegularV4FrontierRepairContract? pressureContract = null;
        if (transition.PressureBinding is { } pressureBinding &&
            !TryGetExactRegularV4PressureRecoveryHandoffContract(
                context.CurrentRegularV4FrontierRepairContract,
                pressureBinding,
                out pressureContract,
                out var pressureContractRejectionReason))
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                pressureContractRejectionReason);
            return false;
        }

        if (prepared.RouteDecision is not { } decision ||
            decision.StartedLeg is null ||
            decision.StartedLiveRouteEpoch is null ||
            !string.IsNullOrWhiteSpace(decision.MutationRejectedReason) ||
            decision.State.CurrentRoutePublicationToken is not
                { IsDefault: false } routePublicationToken ||
            decision.State.CurrentRuntimeOwner is not
                { IsDefault: false } committedRuntimeOwner ||
            decision.State.CurrentLeg is not { } committedLeg ||
            committedLeg.Generation != routePublicationToken.LegGeneration ||
            committedLeg.TransportEpochId != intent.ProofTransportEpoch ||
            committedLeg.BridgeRecoveryGeneration != bridgeRecoveryGeneration)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                "uncommitted_tuna_fallback_coordinator_projection_rejected");
            return false;
        }

        if (transport is not IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge adoptionBridge)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                "uncommitted_tuna_fallback_adoption_bridge_missing");
            return false;
        }

        var adoptionStage = new RuntimeUnlockUncommittedTunaFallbackAdoptionStage(
            intent.SourceDataSession,
            intent.SourceDataSessionGeneration,
            intent.SourceRuntimeOwner,
            intent.SourceRoutePublication,
            intent.RuntimeUnlockAdmission,
            intent.PendingPreCommitProbe,
            intent.RouteCommitDrain.RouteCommitSnapshot,
            intent.RouteCommitDrain.SourceLegGeneration,
            intent.RouteCommitDrain.SourceTransportEpoch,
            intent.RouteCommitDrain.SourceBridgeRecoveryGeneration,
            intent.HandoffGeneration,
            intent.RouteCommitDrain.DrainGeneration,
            context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration,
            intent.ProofTransportEpoch,
            committedLeg.BridgeRecoveryGeneration,
            intent.PeerProofEvidence!,
            transition.PressureSettlementCommand,
            committedRuntimeOwner,
            routePublicationToken);
        RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt adoptionStageReceipt;
        try
        {
            adoptionStageReceipt = adoptionBridge.StageRuntimeUnlockUncommittedTunaFallbackAdoption(
                adoptionStage);
        }
        catch (Exception ex)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                $"uncommitted_tuna_fallback_adoption_stage_failed_{NormalizeReason(ex.GetType().Name)}");
            return false;
        }

        if (adoptionStageReceipt.Stage != adoptionStage ||
            adoptionStageReceipt.Status is not (
                RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Staged or
                RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Idempotent) ||
            adoptionStageReceipt.StageGeneration <= 0)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Rejected",
                $"uncommitted_tuna_fallback_adoption_stage_{NormalizeReason(adoptionStageReceipt.Reason)}");
            return false;
        }

        var adoptionCommand = new RuntimeUnlockUncommittedTunaFallbackAdoptionCommand(
            intent.SourceDataSession,
            intent.SourceDataSessionGeneration,
            adoptionStageReceipt.StageGeneration,
            committedRuntimeOwner,
            routePublicationToken,
            committedLeg.TransportEpochId,
            committedLeg.BridgeRecoveryGeneration,
            intent.HandoffGeneration,
            intent.RouteCommitDrain.DrainGeneration,
            intent.PeerProofEvidence!,
            transition.PressureSettlementCommand,
            adoptionStageReceipt.AvailabilityGeneration,
            adoptionStageReceipt.ActivationPaused,
            adoptionStageReceipt.AvailabilityReason);

        var continuation = new RuntimeUnlockUncommittedTunaFallbackAdoptionContinuation(
            intent,
            transition,
            decision,
            previousRouteSelection,
            routeSelection,
            routeInput,
            committedRuntimeOwner,
            routePublicationToken,
            committedLeg,
            adoptionStageReceipt,
            adoptionCommand,
            pressureContract,
            adoptionBridge);
        context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation = continuation;
        return TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
            context,
            continuation,
            out transferSnapshot,
            allowStaleAdoptionStageRestage);
    }

    private bool TryCommitRuntimeUnlockUncommittedTunaFallbackAdoptionContinuationLocked(
        OutboundTransferContext context,
        RuntimeUnlockUncommittedTunaFallbackAdoptionContinuation continuation,
        out SessionFileTransferSnapshot? transferSnapshot,
        bool allowStaleAdoptionStageRestage = true)
    {
        transferSnapshot = null;
        var continuationCurrentnessReason = "uncommitted_tuna_fallback_adoption_continuation_stale";
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation != continuation ||
            !IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrentLocked(
                context,
                continuation.Intent,
                out continuationCurrentnessReason) ||
            !IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                out continuationCurrentnessReason))
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                continuationCurrentnessReason);
            return false;
        }

        var revalidatedPreparation =
            FileTransferCoordinator.PrepareRuntimeUnlockUncommittedTunaFallbackHandoff(
                CreateCoordinatorStateLocked(context),
                continuation.Transition.CoordinatorEvent);
        if (revalidatedPreparation.Status !=
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Prepared ||
            revalidatedPreparation.Transition != continuation.Transition ||
            revalidatedPreparation.RouteDecision is not { } revalidatedRouteDecision ||
            revalidatedRouteDecision.State.CurrentRuntimeOwner != continuation.RuntimeOwner ||
            revalidatedRouteDecision.State.CurrentRoutePublicationToken != continuation.RoutePublication ||
            revalidatedRouteDecision.State.CurrentLeg is not { } revalidatedLeg ||
            revalidatedLeg.Generation != continuation.Leg.Generation ||
            revalidatedLeg.TransportEpochId != continuation.Leg.TransportEpochId ||
            revalidatedLeg.BridgeRecoveryGeneration != continuation.Leg.BridgeRecoveryGeneration ||
            revalidatedLeg.State != FileTransferLegState.CheckpointPending ||
            revalidatedLeg.CanSendData)
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                string.IsNullOrWhiteSpace(revalidatedPreparation.Reason)
                    ? "uncommitted_tuna_fallback_revalidation_failed"
                    : revalidatedPreparation.Reason);
            return false;
        }

        if (transport is not IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge adoptionBridge)
        {
            const string invalidationReason = "coordinator_transport_adoption_unavailable";
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Invalidated",
                invalidationReason);
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                invalidationReason);
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        var adoptionCommand = continuation.AdoptionCommand;
        RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt preparationReceipt;
        try
        {
            preparationReceipt = adoptionBridge.PrepareRuntimeUnlockUncommittedTunaFallbackAdoption(
                adoptionCommand);
        }
        catch (Exception ex)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Held",
                $"coordinator_transport_adoption_prepare_failed_{NormalizeReason(ex.GetType().Name)}");
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        if (preparationReceipt.Command != adoptionCommand ||
            preparationReceipt.Status is not (
                RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Prepared or
                RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Idempotent))
        {
            if (allowStaleAdoptionStageRestage &&
                preparationReceipt.Command == adoptionCommand &&
                preparationReceipt.Status ==
                    RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Rejected &&
                IsRuntimeUnlockUncommittedTunaFallbackAdoptionStageCurrentnessRejection(
                    preparationReceipt.Reason))
            {
                return TryRestageRuntimeUnlockUncommittedTunaFallbackAdoptionLocked(
                    context,
                    continuation,
                    "coordinator_transport_adoption_prepare_stage_stale",
                    out transferSnapshot);
            }

            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Held",
                $"coordinator_transport_adoption_prepare_{NormalizeReason(preparationReceipt.Reason)}");
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        var intent = continuation.Intent;
        var transition = continuation.Transition;
        var committed = FileTransferCoordinator.CommitRuntimeUnlockUncommittedTunaFallbackHandoff(
            CreateCoordinatorStateLocked(context),
            transition,
            transition.Handoff,
            continuation.StageReceipt,
            preparationReceipt);
        if (committed.Status !=
                RuntimeUnlockUncommittedTunaFallbackHandoffTransitionStatus.Committed ||
            committed.RouteDecision is not { } decision ||
            decision.State.CurrentRuntimeOwner != continuation.RuntimeOwner ||
            decision.State.CurrentRoutePublicationToken != continuation.RoutePublication ||
            decision.State.CurrentLeg is not { } finalLeg ||
            finalLeg.Generation != continuation.Leg.Generation ||
            finalLeg.TransportEpochId != continuation.Leg.TransportEpochId ||
            finalLeg.BridgeRecoveryGeneration != continuation.Leg.BridgeRecoveryGeneration)
        {
            var invalidationReason =
                $"coordinator_post_adoption_commit_{NormalizeReason(committed.Reason)}";
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Invalidated",
                invalidationReason);
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                invalidationReason);
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt adoptionReceipt;
        try
        {
            adoptionReceipt = adoptionBridge.CommitRuntimeUnlockUncommittedTunaFallbackAdoption(
                adoptionCommand);
        }
        catch (Exception ex)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Held",
                $"coordinator_transport_adoption_finalize_failed_{NormalizeReason(ex.GetType().Name)}");
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        if (adoptionReceipt.Command != adoptionCommand ||
            adoptionReceipt.Status is not (
                RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Applied or
                RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Idempotent) ||
            adoptionReceipt.PressureSettlementReceipt != preparationReceipt.PressureSettlementReceipt)
        {
            if (allowStaleAdoptionStageRestage &&
                adoptionReceipt.Command == adoptionCommand &&
                adoptionReceipt.Status == RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected &&
                IsRuntimeUnlockUncommittedTunaFallbackAdoptionStageCurrentnessRejection(
                    adoptionReceipt.Reason))
            {
                return TryRestageRuntimeUnlockUncommittedTunaFallbackAdoptionLocked(
                    context,
                    continuation,
                    "coordinator_transport_adoption_finalize_stage_stale",
                    out transferSnapshot);
            }

            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                continuation.Intent,
                "Held",
                $"coordinator_transport_adoption_finalize_{NormalizeReason(adoptionReceipt.Reason)}");
            transferSnapshot = CreateSnapshotLocked();
            return false;
        }

        var previousRouteSelection = continuation.PreviousRouteSelection;
        var routeSelection = continuation.RouteSelection;
        var routeInput = continuation.RouteInput;
        var committedRuntimeOwner = decision.State.CurrentRuntimeOwner!.Value;
        var routePublicationToken = decision.State.CurrentRoutePublicationToken!.Value;
        var committedLeg = finalLeg;
        var pressureContract = continuation.PressureContract;
        ApplyCoordinatorDecisionToContextLocked(
            context,
            decision,
            forwardRoutePublication: false);
        if (context.CurrentRuntimeOwner != committedRuntimeOwner ||
            context.CurrentRoutePublicationToken != routePublicationToken ||
            context.CurrentTransferLeg != committedLeg)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Committed",
                "coordinator_transport_adoption_apply_mismatch");
        }

        try
        {
            context.RoutePublicationAuthorityForwarder?.Invoke(
                routePublicationToken,
                decision.Reason);
        }
        catch (Exception ex)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Committed",
                $"coordinator_route_publication_forward_failed_{NormalizeReason(ex.GetType().Name)}");
        }
        if (pressureContract is not null)
        {
            FinalizeRuntimeUnlockUncommittedTunaFallbackPressureContractLocked(
                context,
                pressureContract,
                transition);
        }
        ApplyFileTransferRuntimeProfileSelectionLocked(
            context,
            FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection));
        var routeTransitionPublished = TryPublishRouteTransitionedWithExistingAuthorityLocked(
            context,
            previousRouteSelection,
            "fallback_route_transition",
            "uncommitted_tuna_fallback_handoff",
            routePublicationToken,
            intent.RouteCommitDrain.DrainGeneration);
        var routeSelectionPublished = TryPublishRouteSelectedIfAuthorizedLocked(
            context,
            routeInput,
            "fallback_route_selected",
            "uncommitted_tuna_fallback_handoff",
            routePublicationToken);
        var routeHintPublished = TryPublishRouteHintIfAuthorizedLocked(
            context,
            "fallback_route_hint",
            "uncommitted_tuna_fallback_handoff",
            "service_live_route_transition",
            routePublicationToken: routePublicationToken);
        var liveRouteEpochPublished = TryPublishLiveRouteEpochStartedIfAuthorizedLocked(
            context,
            previousRouteSelection,
            "fallback_live_route_epoch_started",
            "uncommitted_tuna_fallback_handoff",
            routePublicationToken: routePublicationToken);
        if (!routeTransitionPublished ||
            !routeSelectionPublished ||
            !routeHintPublished ||
            !liveRouteEpochPublished)
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Committed",
                "coordinator_route_publication_failed");
        }

        StartOutboundV6TransportEpochLocked(
            context,
            "uncommitted_tuna_fallback_handoff",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn,
            coordinatorAuthorized: true);
        InitializeOutboundPostTunaRecoveryForCurrentCoordinatorLegLocked(
            context,
            "uncommitted_tuna_fallback_handoff");
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(
                context,
                "sender",
                "fallback_runtime_started",
                "uncommitted_tuna_fallback_handoff",
                routePublicationToken: routePublicationToken))
        {
            LogRuntimeUnlockUncommittedTunaFallbackHandoff(
                "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
                context,
                intent,
                "Committed",
                "coordinator_runtime_start_publication_failed");
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = true;
        context.PullTransportRebindGeneration = committedLeg.BridgeRecoveryGeneration;
        context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
        context.StatusMessage = "Switching to regular NKN fallback.";
        context.PendingRuntimeUnlockUncommittedTunaFallbackRetainedPeerProof =
            new RuntimeUnlockUncommittedTunaFallbackRetainedPeerProof(
                intent.HandoffGeneration,
                intent.PeerProofFrame!,
                intent.PeerProofEvidence!.FrameIdentity!,
                intent.ProofTransportEpoch,
                intent.SourceDataSession,
                intent.SourceDataSessionGeneration,
                committedRuntimeOwner,
                routePublicationToken);
        context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation = null;
        context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff = null;
        LogRuntimeUnlockUncommittedTunaFallbackHandoff(
            "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
            context,
            intent,
            "Accepted",
            "coordinator_post_tuna_fallback_v6_authorized");
        LogRuntimeUnlockUncommittedTunaFallbackHandoff(
            "runtime_unlock_uncommitted_tuna_fallback_handoff_committed",
            context,
            intent,
            "Committed",
            "coordinator_post_tuna_fallback_v6_committed");
        transferSnapshot = CreateSnapshotLocked();
        QueueOutboundPostTunaFallbackCheckpointRequestLocked(
            context,
            "uncommitted_tuna_fallback_handoff");
        return true;
    }

    private bool TryRestageRuntimeUnlockUncommittedTunaFallbackAdoptionLocked(
        OutboundTransferContext context,
        RuntimeUnlockUncommittedTunaFallbackAdoptionContinuation continuation,
        string reason,
        out SessionFileTransferSnapshot? transferSnapshot)
    {
        transferSnapshot = null;
        var currentnessReason = "handoff_scope_replaced";
        if (context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation != continuation ||
            context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff != continuation.Intent ||
            !IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrentLocked(
                context,
                continuation.Intent,
                out currentnessReason))
        {
            InvalidateRuntimeUnlockUncommittedTunaFallbackHandoffForDrainLocked(
                context,
                continuation.Intent.RouteCommitDrain,
                currentnessReason);
            return false;
        }

        TryDiscardRuntimeUnlockUncommittedTunaFallbackAdoptionStage(
            continuation.AdoptionBridge,
            continuation.StageReceipt.Stage,
            reason);
        context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation = null;
        LogRuntimeUnlockUncommittedTunaFallbackHandoff(
            "runtime_unlock_uncommitted_tuna_fallback_handoff_decided",
            context,
            continuation.Intent,
            "Held",
            reason);
        return TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
            context,
            continuation.Intent,
            reason,
            out transferSnapshot,
            allowStaleAdoptionStageRestage: false);
    }

    private static bool IsRuntimeUnlockUncommittedTunaFallbackAdoptionStageCurrentnessRejection(
        string? reason)
        => string.Equals(
            NormalizeReason(reason),
            "uncommitted_tuna_fallback_adoption_stage_mismatch",
            StringComparison.Ordinal);

    private static void TryDiscardRuntimeUnlockUncommittedTunaFallbackAdoptionStage(
        IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge adoptionBridge,
        RuntimeUnlockUncommittedTunaFallbackAdoptionStage stage,
        string reason)
    {
        try
        {
            adoptionBridge.DiscardRuntimeUnlockUncommittedTunaFallbackAdoption(stage, reason);
        }
        catch
        {
            // Diagnostic-only cleanup after Core has rejected this handoff.
        }
    }

    private void FinalizeRuntimeUnlockUncommittedTunaFallbackPressureContractLocked(
        OutboundTransferContext context,
        RegularV4FrontierRepairContract contract,
        RuntimeUnlockUncommittedTunaFallbackHandoffTransition transition)
    {
        if (transition.PressureBinding is not { } binding ||
            transition.PressureSettlement is not { } settlement ||
            !ReferenceEquals(context.CurrentRegularV4FrontierRepairContract, contract) ||
            contract.Authority != binding.Authority ||
            contract.Lifecycle.IsSettled ||
            !contract.Lifecycle.TrySettle(
                settlement.Outcome,
                settlement.DecisionReason,
                settlement.SuccessorAuthorityId))
        {
            return;
        }

        context.CurrentRegularV4FrontierRepairContract = null;
        if (context.PullRegularNknV4PeerSilenceMissingDispatchSettlementContract?.Authority.AuthorityId ==
            binding.Authority.AuthorityId)
        {
            context.PullRegularNknV4PeerSilenceMissingDispatchSettlementContract = null;
        }
        context.PendingRegularV4FrontierRepairPressureInstallRetry = null;
        ClearPendingRegularV4FrontierRepairPressureInstall(contract);

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(contract.Authority.AuthorityId.ToString())}; successor_authority_id={FormatProtocolLogValue(settlement.SuccessorAuthorityId?.ToString() ?? "(none)")}; repair_request_key={FormatProtocolLogValue(contract.Authority.RepairKey)}; settlement_outcome={FormatProtocolLogValue(settlement.Outcome.ToString())}; dispatch_observed={(contract.Lifecycle.DispatchObserved ? 1 : 0)}; decision_reason={FormatProtocolLogValue(settlement.DecisionReason)}");
    }

    private static bool IsSameRuntimeUnlockUncommittedTunaFallbackPeerProofFrame(
        FileTransferDataFrame left,
        FileTransferDataFrame right)
        => PostTunaFallbackPeerProofFrameIdentity.TryCreate(left, out var leftIdentity) &&
           leftIdentity.Matches(right);

    private static bool IsRuntimeUnlockUncommittedTunaFallbackEvidenceForPeerFrame(
        PostTunaFallbackPeerProofEvidence evidence,
        FileTransferDataFrame frame)
    {
        if (!evidence.IsComplete ||
            evidence.FrameIdentity is not { } frameIdentity ||
            !frameIdentity.Matches(frame) ||
            !string.Equals(evidence.SessionId, frame.SessionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.TransferId, frame.TransferId, StringComparison.Ordinal) ||
            evidence.TransportEpoch != ResolveV6RuntimeProofTransportEpoch(frame))
        {
            return false;
        }

        return evidence.ProofKind switch
        {
            "file_transfer_v6_state_frame_received" => frame is FileTransferReceiverStateFrameV6,
            "file_transfer_v6_frontier_request_frame_received" => frame is FileTransferFrontierRequestFrameV6,
            // A recovered V6 epoch is itself the exact coordinator-bound peer proof.
            // The retained frame is then only the first current-epoch input to replay.
            "filetransfer_v6_epoch_recovered" => IsPeerPostTunaFallbackV6RuntimeProofFrame(frame),
            _ => false,
        };
    }

    private bool IsRuntimeUnlockUncommittedTunaFallbackHandoffCurrentLocked(
        OutboundTransferContext context,
        RuntimeUnlockUncommittedTunaFallbackHandoffIntent intent,
        out string rejectionReason)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            rejectionReason = context.IsTerminal ? "terminal_context" : "transfer_context_replaced";
            return false;
        }

        if (context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff != intent ||
            context.PendingRuntimeUnlockRegularV4RouteCommitDrain != intent.RouteCommitDrain ||
            context.CurrentRuntimeOwner != intent.SourceRuntimeOwner ||
            context.CurrentRoutePublicationToken != intent.SourceRoutePublication ||
            context.RuntimeUnlockRouteCommitToken is not null ||
            context.RuntimeUnlockAdmissionToken != intent.RuntimeUnlockAdmission ||
            context.RuntimeUnlockPendingPreCommitProbe != intent.PendingPreCommitProbe ||
            !ReferenceEquals(context.DataSession, intent.SourceDataSession) ||
            context.DataSessionGeneration != intent.SourceDataSessionGeneration)
        {
            rejectionReason = "handoff_scope_replaced";
            return false;
        }

        return IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentForSnapshotLocked(
            context,
            intent.RouteCommitDrain,
            intent.RouteCommitDrain.RouteCommitSnapshot,
            out rejectionReason);
    }

    private static void LogRuntimeUnlockUncommittedTunaFallbackHandoff(
        string eventName,
        OutboundTransferContext context,
        RuntimeUnlockUncommittedTunaFallbackHandoffIntent intent,
        string status,
        string reason)
    {
        var drain = intent.RouteCommitDrain;
        var admission = intent.RuntimeUnlockAdmission;
        var pendingPreCommit = intent.PendingPreCommitProbe;
        var snapshot = drain.RouteCommitSnapshot;
        var proof = intent.PeerProofEvidence;
        var continuation = context.PendingRuntimeUnlockUncommittedTunaFallbackAdoptionContinuation;
        var targetOwner = context.CurrentRuntimeOwner is
            { Route: FileTransferRoute.PostTunaFallbackV6 } owner
            ? owner
            : default;
        var targetPublication = context.CurrentRoutePublicationToken is
            { Route: FileTransferRoute.PostTunaFallbackV6 } publication
            ? publication
            : default;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; handoff_generation={intent.HandoffGeneration}; decision_status={FormatProtocolLogValue(status)}; source_route={FormatProtocolLogValue(intent.SourceRuntimeOwner.RouteToken)}; current_route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; source_protocol_version={intent.SourceRuntimeOwner.ProtocolVersion}; current_protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={intent.SourceRuntimeOwner.LiveRouteEpoch}; runtime_generation={intent.SourceRuntimeOwner.RuntimeGeneration}; terminal_generation={drain.SourceTerminalGeneration}; leg_generation={drain.SourceLegGeneration}; transport_epoch={drain.SourceTransportEpoch}; bridge_recovery_generation={drain.SourceBridgeRecoveryGeneration}; route_publication_generation={intent.SourceRoutePublication.PublicationGeneration}; route_commit_generation={intent.SourceRoutePublication.RouteCommitGeneration ?? 0}; data_session_generation={intent.SourceDataSessionGeneration}; admission_generation={admission.AdmissionGeneration}; pending_precommit_generation={pendingPreCommit.PendingGeneration}; transaction_generation={pendingPreCommit.TransactionGeneration}; offer_generation={pendingPreCommit.OfferGeneration}; tuna_path_lease_generation={pendingPreCommit.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(pendingPreCommit.TunaPathLeaseListenerRunId ?? "(none)")}; launch_generation={snapshot.LaunchGeneration}; probe_id={FormatProtocolLogValue(snapshot.PathProbeId ?? "(none)")}; probe_state={FormatProtocolLogValue(snapshot.PathProbeState)}; drain_generation={drain.DrainGeneration}; completed_drain_generation={context.LastCompletedRuntimeUnlockRegularV4RouteCommitDrainGeneration}; proof_direction={FormatProtocolLogValue(proof?.Direction.ToString() ?? "(none)")}; proof_generation={proof?.ProofGeneration ?? 0}; proof_transport_epoch={intent.ProofTransportEpoch}; proof_kind={FormatProtocolLogValue(proof?.ProofKind ?? "(none)")}; proof_reason={FormatProtocolLogValue(proof?.Reason ?? "(none)")}; handoff_kind=tuna_to_normal_fallback; target_route=post_tuna_fallback_v6; target_protocol_version={FileTransferProtocol.ProtocolVersionV6}; target_transport=regular_nkn; target_runtime_generation={targetOwner.RuntimeGeneration}; target_leg_generation={targetPublication.LegGeneration}; target_route_publication_generation={targetPublication.PublicationGeneration}; adoption_stage_generation={continuation?.StageReceipt.StageGeneration ?? 0}; adoption_stage_status={FormatProtocolLogValue(continuation?.StageReceipt.Status.ToString() ?? "(none)")}; availability_proof_present={(intent.PeerProofEvidence is null ? 0 : 1)}; peer_proof_present={(intent.PeerProofFrame is null ? 0 : 1)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private async Task RecheckRuntimeUnlockRegularV4RouteCommitAfterSendDrainAsync(
        OutboundTransferContext context,
        RuntimeUnlockRegularV4RouteCommitDrain drain)
    {
        await Task.Yield();
        SessionFileTransferSnapshot? transferSnapshot = null;
        RouteCommitToken routeCommitToken = default;
        var fallbackHandoffCompleted = false;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.PendingRuntimeUnlockRegularV4RouteCommitDrain != drain)
            {
                return;
            }

            context.RuntimeUnlockRegularV4RouteCommitDrainRecheckQueued = false;
            if (!IsRuntimeUnlockRegularV4RouteCommitDrainSourceCurrentLocked(
                    context,
                    drain,
                    out var rejectionReason))
            {
                InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
                    context,
                    drain,
                    rejectionReason);
                return;
            }

            if (CountActiveOutboundRegularV4PhysicalSendLeasesForDrainLocked(
                    context,
                    drain) > 0)
            {
                return;
            }

            if (context.PendingRuntimeUnlockUncommittedTunaFallbackHandoff is { } fallbackIntent)
            {
                fallbackHandoffCompleted =
                    TryCompleteRuntimeUnlockUncommittedTunaFallbackHandoffLocked(
                        context,
                        fallbackIntent,
                        "regular_v4_send_drain_retired",
                        out transferSnapshot);
                if (!fallbackHandoffCompleted)
                {
                    return;
                }
            }

            if (!fallbackHandoffCompleted &&
                (!TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    drain.Reason,
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna) ||
                context.RuntimeUnlockRouteCommitToken is not { IsDefault: false } committedToken ||
                committedToken.TransactionGeneration != drain.RouteCommitSnapshot.TransactionGeneration ||
                committedToken.OfferGeneration != drain.RouteCommitSnapshot.OfferGeneration))
            {
                return;
            }

            if (!fallbackHandoffCompleted)
            {
                routeCommitToken = context.RuntimeUnlockRouteCommitToken!.Value;
                transferSnapshot = CreateSnapshotLocked();
            }
        }

        if (transferSnapshot is not null)
        {
            RaiseTransferChanged(transferSnapshot);
        }

        if (fallbackHandoffCompleted)
        {
            return;
        }

        LogTransportResumed(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            drain.Reason,
            requiresResumeRequest: true);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_rebind_skipped_for_tuna_activation; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(drain.Reason)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}");
        var snapshot = drain.RouteCommitSnapshot;
        NotifyRuntimeUnlockPairedRouteCommitConfirmed(
            FileTransferDirection.Outbound,
            context.SessionId,
            context.TransferId,
            snapshot.TransactionGeneration,
            snapshot.OfferGeneration,
            drain.Reason,
            routeCommitToken);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_paired_commit_confirmed; direction=outbound; transfer_id={FormatProtocolLogValue(context.TransferId)}; session_id={FormatProtocolLogValue(context.SessionId)}; transaction_generation={snapshot.TransactionGeneration}; offer_generation={snapshot.OfferGeneration}; tuna_path_lease_generation={snapshot.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(snapshot.PathProbeId ?? "(none)")}; reason={FormatProtocolLogValue(drain.Reason)}");
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

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        if (!EnsureInboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out _, out var routeCommitToken))
        {
            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch || routeCommitToken.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                reason,
                routeCommitToken.IsDefault ? "route_commit_token_missing" : "live_route_epoch_missing");
            return false;
        }

        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetInboundRegularNknV4StateForFileTunaV4Locked(context);
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_route_transition", reason, out var routePublicationToken, routeCommitToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "runtime_unlock_route_selected", reason, routePublicationToken, routeCommitToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "runtime_unlock_route_hint", reason, "service_live_route_transition", routeCommitToken: routeCommitToken, routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_live_route_epoch_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "receiver", "runtime_unlock_runtime_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        if (!TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(context, "runtime_unlock_live_route_epoch_recovered", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }
        return true;
    }

    private static void ResetOutboundRegularNknV4StateForFileTunaV4Locked(OutboundTransferContext context)
    {
        ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
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
        context.SignalSparseSenderPump();
    }

    private static void ResetInboundRegularNknV4StateForFileTunaV4Locked(InboundTransferContext context)
    {
        ClearInboundRuntimeUnlockFallbackSeparationStateLocked(context);
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

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        if (!EnsureOutboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out _, out var routeCommitToken))
        {
            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch || routeCommitToken.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                reason,
                routeCommitToken.IsDefault ? "route_commit_token_missing" : "live_route_epoch_missing");
            return false;
        }

        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetOutboundPostTunaFallbackStateForFileTunaV4Locked(context);
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SparseSenderPumpLastWakeReason = "live_route_tuna_reactivated";

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_route_transition", reason, out var routePublicationToken, routeCommitToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "runtime_unlock_route_selected", reason, routePublicationToken, routeCommitToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "runtime_unlock_route_hint", reason, "service_live_route_transition", routeCommitToken: routeCommitToken, routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_live_route_epoch_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "sender", "runtime_unlock_runtime_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        if (!TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(context, "runtime_unlock_live_route_epoch_recovered", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }
        return true;
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

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        if (!EnsureInboundRuntimeUnlockPreCommitProbeReadyForRouteCommitLocked(context, reason))
        {
            return true;
        }

        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out _, out var routeCommitToken))
        {
            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        if (context.CurrentLiveRouteEpoch is not { } liveRouteEpoch || routeCommitToken.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                reason,
                routeCommitToken.IsDefault ? "route_commit_token_missing" : "live_route_epoch_missing");
            return false;
        }

        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetInboundPostTunaFallbackStateForFileTunaV4Locked(context);
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);

        if (!TryPublishRouteTransitionedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_route_transition", reason, out var routePublicationToken, routeCommitToken) ||
            !TryPublishRouteSelectedIfAuthorizedLocked(context, routeInput, "runtime_unlock_route_selected", reason, routePublicationToken, routeCommitToken) ||
            !TryPublishRouteHintIfAuthorizedLocked(context, "runtime_unlock_route_hint", reason, "service_live_route_transition", routeCommitToken: routeCommitToken, routePublicationToken: routePublicationToken) ||
            !TryPublishLiveRouteEpochStartedIfAuthorizedLocked(context, previousRouteSelection, "runtime_unlock_live_route_epoch_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        if (!TryPublishRuntimeStartedIfAuthorizedLocked(context, "receiver", "runtime_unlock_runtime_started", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }

        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        if (!TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(context, "runtime_unlock_live_route_epoch_recovered", reason, routeCommitToken, routePublicationToken))
        {
            return false;
        }
        return true;
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

    private void StartOutboundPostTunaRecoveryLocked(OutboundTransferContext context, string reason)
        => StartOutboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            forceNewGeneration: false);

    private void StartOutboundPostTunaRecoveryCoreLocked(
        OutboundTransferContext context,
        string reason,
        bool forceNewGeneration)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        if (!forceNewGeneration &&
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
        if (!forceNewGeneration &&
            context.PullPostTunaRecoveryActive &&
            IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason) &&
            currentFallbackLegMatchesLiveEpoch &&
            context.CurrentTransferLeg is { State: FileTransferLegState.RecoveryActive })
        {
            return;
        }

        if (!forceNewGeneration &&
            context.PullPostTunaRecoveryActive &&
            currentFallbackLegMatchesLiveEpoch &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier &&
            context.PendingFallbackTailYieldTransportEpochReauthorizationRequest is null)
        {
            return;
        }

        var transportEpoch = ResolveOutboundFallbackCurrentTransportEpochLocked(context, generation);
        var pendingFallbackTailYieldTransportEpochReauthorizationRequest =
            context.PendingFallbackTailYieldTransportEpochReauthorizationRequest;
        var pendingFallbackTailYieldTransportEpochReauthorization =
            context.PendingFallbackTailYieldTransportEpochReauthorization;
        if (pendingFallbackTailYieldTransportEpochReauthorization is { } pendingReauthorization &&
            !TryRetireExpectedOutboundFallbackCheckpointBeforeReauthorizationRestartLocked(
                context,
                pendingReauthorization,
                transportEpoch,
                reason))
        {
            return;
        }

        var fallbackTailYieldTransportEpochReauthorizationRequest =
            ResolveFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                transportEpoch,
                reason);
        if ((pendingFallbackTailYieldTransportEpochReauthorization is not null ||
             pendingFallbackTailYieldTransportEpochReauthorizationRequest is not null) &&
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

    private static bool TryRetireExpectedOutboundFallbackCheckpointBeforeReauthorizationRestartLocked(
        OutboundTransferContext context,
        FallbackTailYieldTransportEpochReauthorization pendingReauthorization,
        long targetTransportEpoch,
        string reason)
    {
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
        if (state.PendingFallbackTailYieldTransportEpochReauthorization != pendingReauthorization ||
            state.LastFallbackTailYieldDecision is not null ||
            context.FallbackTailYieldTransportEpochReauthorizationDataSessionGeneration !=
            context.DataSessionGeneration ||
            !string.Equals(
                pendingReauthorization.TargetCheckpointRequestIdSnapshot,
                "(none)",
                StringComparison.Ordinal) ||
            !string.Equals(
                currentCheckpointSnapshot,
                leg.CheckpointRequestId,
                StringComparison.Ordinal) ||
            !FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
                state,
                pendingReauthorization with
                {
                    TargetCheckpointRequestIdSnapshot = currentCheckpointSnapshot,
                },
                out _))
        {
            return false;
        }

        if (!RetireOutboundFallbackCheckpointExchangeLocked(
                context,
                leg.CheckpointRequestId,
                reason))
        {
            return false;
        }

        return context.PendingFallbackTailYieldTransportEpochReauthorization == pendingReauthorization &&
               FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                   CreateCoordinatorStateLocked(context),
                   pendingReauthorization,
                   out _);
    }

    private void StartOutboundPostTunaRecoveryWithSafetyReplayLocked(OutboundTransferContext context, string reason)
    {
        var previousLeg = context.CurrentTransferLeg;
        StartOutboundPostTunaRecoveryLocked(context, reason);
        if (ReferenceEquals(previousLeg, context.CurrentTransferLeg) &&
            (IsPostYieldFallbackTailOwnerRetiredAndYieldedLocked(context, context.CurrentTransferLeg) ||
             HasPairedFallbackTailYieldForCurrentOwner(context)))
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

    private void StartInboundPostTunaRecoveryLocked(InboundTransferContext context, string reason)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        if (IsPostTunaFallbackBridgeRecoveryEpochRefreshReason(reason) &&
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
        if (context.PullPostTunaRecoveryActive &&
            currentFallbackLegMatchesLiveEpoch &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier &&
            context.PendingFallbackTailYieldTransportEpochReauthorizationRequest is null)
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

        var transportEpoch = context.V6TransportEpoch?.EpochId ?? generation;
        var pendingFallbackTailYieldTransportEpochReauthorizationRequest =
            context.PendingFallbackTailYieldTransportEpochReauthorizationRequest;
        var pendingFallbackTailYieldTransportEpochReauthorization =
            context.PendingFallbackTailYieldTransportEpochReauthorization;
        var fallbackTailYieldTransportEpochReauthorizationRequest =
            ResolveFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                transportEpoch,
                reason);
        if ((pendingFallbackTailYieldTransportEpochReauthorization is not null ||
             pendingFallbackTailYieldTransportEpochReauthorizationRequest is not null) &&
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

        if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=outbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
            return false;
        }

        if (context.DataSession is not null)
        {
            UnsubscribeDataSessionEvents(context.DataSession);
            context.DataSession.Dispose();
        }

        if (context.PendingRuntimeUnlockRegularV4RouteCommitDrain is { } pendingDrain)
        {
            InvalidateRuntimeUnlockRegularV4RouteCommitDrainLocked(
                context,
                pendingDrain,
                "data_session_replaced");
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

        if (!TryRetireRegularV4FrontierRepairPressureForScopeReplacementLocked(
                context,
                "data_session_replaced"))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_data_session_replacement_rejected; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; decision_reason=regular_v4_frontier_repair_pressure_retirement_failed");
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
        var previousDataSessionGeneration = context.DataSessionGeneration;
        context.DataSessionGeneration = Math.Max(1, context.DataSessionGeneration + 1);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_session_recreated; direction=inbound; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; terminal_generation={context.CurrentRuntimeOwner?.TerminalGeneration ?? 0}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; transport_epoch={context.CurrentTransferLeg?.TransportEpochId ?? 0}; bridge_recovery_generation={context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0}; bridge_decision_generation={context.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0}; route_publication_generation={context.CurrentRoutePublicationToken?.PublicationGeneration ?? 0}; route_commit_generation={context.CurrentRoutePublicationToken?.RouteCommitGeneration ?? 0}; previous_data_session_generation={previousDataSessionGeneration}; data_session_generation={context.DataSessionGeneration}; decision_reason=data_session_replaced");
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
