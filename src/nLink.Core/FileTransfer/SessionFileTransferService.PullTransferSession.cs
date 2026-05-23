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
        var stateSent = await SendInboundV4StateAsync(
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

        _ = RunInboundTransportRebindRetriesAsync(context, reason, generation);
    }

    private async Task RunInboundTransportRebindRetriesAsync(InboundTransferContext context, string reason, int generation)
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

    private static bool CompleteOutboundV6TransportHandoffLocked(
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
        context.V4SenderPumpLastWakeReason = "v6_handoff_recovered";
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SignalV4SenderPump();
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
            context.V4SenderPumpLastWakeReason = "tuna_activation_barrier";
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryPauseInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (context.IsTerminal)
        {
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
            !CanUseV6TransportEpochsLocked(context) ||
            !requiresResumeRequest ||
            handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
        }

        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (targetTransport == FileTransferTransportKind.RegularNkn &&
            IsPrimaryRegularNknBulkV6ContextLocked(context))
        {
            context.PullTransportResumeRequestPending = true;
            context.V4SenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind_waiting_for_available";
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
                context.RouteSelection.Route,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
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
        context.V4SenderPumpLastWakeReason = "transport_handoff_unavailable";
        StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
        if (IsTunaFallbackTransportPauseReason(reason))
        {
            StartOutboundPostTunaRecoveryLocked(context, reason);
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
            !CanUseV6TransportEpochsLocked(context) ||
            !requiresResumeRequest ||
            handoffKind == FileTransferTransportHandoffKind.None)
        {
            return false;
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
                context.RouteSelection.Route,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
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
                context.RouteSelection.Route,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
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
                context.RouteSelection.Route,
                context.LastRecoveredV6TransportEpoch,
                context.LastRecoveredV6TransportEpochKind,
                context.LastRecoveredV6TransportTargetTransport,
                handoffKind,
                targetTransport,
                reason))
        {
            return false;
        }

        ClearInboundRecoveredV6RegularNknEpochSuppressedPauseLocked(context, reason, handoffKind, targetTransport);
        return true;
    }

    private static void ClearOutboundRecoveredV6RegularNknEpochSuppressedPauseLocked(
        OutboundTransferContext context,
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
        context.V4SenderPumpLastWakeReason = "recovered_regular_nkn_epoch_restart_suppressed";
        context.SignalV4SenderPump();

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_pause_cleared; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={FileTransferRouteResolver.Resolve(context.RouteSelection.Route).TelemetryToken}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; reason={FormatProtocolLogValue(reason)}; was_paused={(wasPaused ? 1 : 0)}");
    }

    private static void ClearInboundRecoveredV6RegularNknEpochSuppressedPauseLocked(
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

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_pause_cleared; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={FileTransferRouteResolver.Resolve(context.RouteSelection.Route).TelemetryToken}; recovered_transport_epoch={context.LastRecoveredV6TransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(context.LastRecoveredV6TransportEpochKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(handoffKind)}; target_transport={FormatFileTransferTransportKind(targetTransport)}; reason={FormatProtocolLogValue(reason)}; was_paused={(wasPaused ? 1 : 0)}");
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
            if (!requiresResumeRequest ||
                handoffKind == FileTransferTransportHandoffKind.None)
            {
                return false;
            }

            if (!CanUseV6TransportEpochsLocked(context))
            {
                return TryStartOutboundLiveV4TunaFallbackRecoveryLocked(context, reason, handoffKind, targetTransport);
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
                context.V4SenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind";
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteSelection.Route,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return false;
            }

            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.V4SenderPumpLastWakeReason = "transport_handoff";
            StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            LogOutboundV6TransportEpochWaitingForRequests(context, reason);
            return true;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        if (requiresResumeRequest)
        {
            if (!CanUseV6TransportEpochsLocked(context))
            {
                if (!TryStartOutboundLiveV4TunaFallbackRecoveryLocked(context, reason, handoffKind, targetTransport))
                {
                    context.PullTransportResumeRequestPending = false;
                    context.V4SenderPumpLastWakeReason = "transport_resumed";
                }

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
                context.V4SenderPumpLastWakeReason = "primary_regular_nkn_bulk_v6_rebind";
                LogPrimaryRegularNknBulkV6State(context, PrimaryRegularNknBulkV6State.Rebinding, reason);
                return true;
            }

            if (ShouldSuppressRecoveredV6RegularNknEpochRestart(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    context.RouteSelection.Route,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return true;
            }

            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = DateTimeOffset.UtcNow;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.V4SenderPumpLastWakeReason = "transport_rebind";
            StartOutboundV6TransportEpochLocked(context, reason, handoffKind, targetTransport);
            if (IsTunaFallbackTransportPauseReason(reason))
            {
                StartOutboundPostTunaRecoveryLocked(context, reason);
            }

            LogOutboundV6TransportEpochWaitingForRequests(context, reason);
        }
        else
        {
            context.V4SenderPumpLastWakeReason = "transport_resumed";
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
            if (!requiresResumeRequest ||
                handoffKind == FileTransferTransportHandoffKind.None)
            {
                return false;
            }

            if (!CanUseV6TransportEpochsLocked(context))
            {
                return TryStartInboundLiveV4TunaFallbackRecoveryLocked(context, reason, handoffKind, targetTransport);
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
                    context.RouteSelection.Route,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return false;
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
            return true;
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
            if (!CanUseV6TransportEpochsLocked(context))
            {
                if (!TryStartInboundLiveV4TunaFallbackRecoveryLocked(context, reason, handoffKind, targetTransport))
                {
                    context.PullTransportResumeRequestPending = false;
                }

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
                    context.RouteSelection.Route,
                    context.LastRecoveredV6TransportEpoch,
                    context.LastRecoveredV6TransportEpochKind,
                    context.LastRecoveredV6TransportTargetTransport,
                    handoffKind,
                    targetTransport,
                    reason))
            {
                return true;
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
           context.RouteSelection.FrameFamily == FileTransferFrameFamily.V6;

    private static bool CanUseV6TransportEpochsLocked(InboundTransferContext context)
        => context.NegotiatedDataProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteSelection.FrameFamily == FileTransferFrameFamily.V6;

    private static bool IsLiveV4TunaFallbackRecoveryContext(
        FileTransferRoute route,
        int negotiatedProtocolVersion,
        FileTransferFrameFamily frameFamily,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedReason = NormalizeReason(reason);
        var provedTunaFallbackResume =
            string.Equals(normalizedReason, "transport_recovered", StringComparison.Ordinal) &&
            handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            targetTransport == FileTransferTransportKind.RegularNkn;

        return route == FileTransferRoute.FileTunaV4 &&
            negotiatedProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            frameFamily == FileTransferFrameFamily.V4 &&
            (provedTunaFallbackResume || IsTunaFallbackTransportPauseReason(reason));
    }

    private bool TryStartOutboundLiveV4TunaFallbackRecoveryLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (!IsLiveV4TunaFallbackRecoveryContext(
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                context.RouteSelection.FrameFamily,
                reason,
                handoffKind,
                targetTransport) ||
            context.IsTerminal)
        {
            return false;
        }

        var keepPausedUntilLiveV4Repair =
            context.PullTransportPaused &&
            IsTunaFallbackTransportPauseReason(reason) &&
            !string.Equals(NormalizeReason(reason), "transport_recovered", StringComparison.Ordinal);
        if (keepPausedUntilLiveV4Repair)
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
        context.V4SenderPumpLastWakeReason = "live_v4_tuna_fallback_rebind";
        ResetOutboundV4AcceptedAfterPauseLocked(context, "live_v4_tuna_fallback");
        StartOutboundPostTunaRecoveryLocked(context, reason);
        QueueOutboundV4TransportRebindSafetyReplayLocked(
            context,
            "live_v4_tuna_fallback",
            allowRepeatForSameGeneration: true);
        return true;
    }

    private static bool TryStartInboundLiveV4TunaFallbackRecoveryLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (!IsLiveV4TunaFallbackRecoveryContext(
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                context.RouteSelection.FrameFamily,
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
        FileTransferRoute route,
        long lastRecoveredEpoch,
        FileTransferTransportHandoffKind lastRecoveredKind,
        FileTransferTransportKind lastRecoveredTarget,
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

        if (requestedKind == FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            if (route == FileTransferRoute.PostTunaFallbackV6)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_epoch_recovered_restart_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(route).TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
                return true;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_recovered_restart_allowed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(route).TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; route={FormatProtocolLogValue(FileTransferRouteResolver.Resolve(route).TelemetryToken)}; recovered_transport_epoch={lastRecoveredEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(lastRecoveredKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedKind)}; target_transport={FormatFileTransferTransportKind(requestedTarget)}; reason={FormatProtocolLogValue(reason)}");
        return true;
    }

    private static void StartOutboundPostTunaRecoveryLocked(OutboundTransferContext context, string reason)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        var generation = Math.Max(1, context.PullTransportRebindGeneration);
        var frontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount - 1);
        if (context.PullPostTunaRecoveryActive &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier)
        {
            return;
        }

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

    private static void StartInboundPostTunaRecoveryLocked(InboundTransferContext context, string reason)
    {
        if (context.ChunkCount <= 0)
        {
            return;
        }

        var generation = Math.Max(1, context.PullTransportRebindGeneration);
        var frontier = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount - 1);
        if (context.PullPostTunaRecoveryActive &&
            context.PullPostTunaRecoveryGeneration == generation &&
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier)
        {
            return;
        }

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
            normalized?.Contains("tuna", StringComparison.OrdinalIgnoreCase) == true ||
            normalized?.Contains("sidecar", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsTunaActivationNegotiationTransportPauseReason(string? reason)
        => string.Equals(
            NormalizeReason(reason),
            "tuna_activation_negotiating",
            StringComparison.Ordinal);

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
