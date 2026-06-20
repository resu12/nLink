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
            ClearOutboundV4RepairWorkForTunaActivationPauseLocked(context, reason);
            return true;
        }

        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = DateTimeOffset.UtcNow;
        context.PullTransportGraceDeadlineUtc = context.PullTransportPausedSinceUtc.Value.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportLastPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        if (IsTunaActivationNegotiationTransportPauseReason(reason))
        {
            ClearOutboundV4RepairWorkForTunaActivationPauseLocked(context, reason);
        }
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
        LogRecoveredRegularNknLiveRouteEpochAfterSuppressedRestart(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentLiveRouteEpoch,
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
        LogRecoveredRegularNknLiveRouteEpochAfterSuppressedRestart(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            context.CurrentLiveRouteEpoch,
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
        observer.ObserveFileTransferV6TransportEpoch(
            new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                direction,
                recoveredTransportEpoch,
                recoveredHandoffKind,
                sourceTransport,
                FileTransferTransportKind.RegularNkn,
                V6TransportEpochState.Recovered,
                reason,
                IsUnresolved: false));
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_recovered_restart_snapshot_published; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; recovered_transport_epoch={recoveredTransportEpoch}; recovered_handoff_kind={FormatFileTransferTransportHandoffKind(recoveredHandoffKind)}; requested_handoff_kind={FormatFileTransferTransportHandoffKind(requestedHandoffKind)}; requested_target_transport={FormatFileTransferTransportKind(requestedTargetTransport)}; reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogRecoveredRegularNknLiveRouteEpochAfterSuppressedRestart(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        LiveRouteEpoch? epoch,
        string reason)
    {
        if (epoch is null ||
            !epoch.RouteSelection.RuntimeDescriptor.UsesPostTunaFallbackV6Runtime ||
            epoch.HandoffKind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            string.Equals(epoch.State, "recovered", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(epoch.State, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LogLiveRouteEpochRecovered(
            direction,
            transferId,
            sessionId,
            epoch,
            reason);
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
                return TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
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
        if (ShouldDeferTunaActivationReadyResumeUntilRouteHandoff(
                resumedPauseReason,
                reason,
                handoffKind))
        {
            context.PullTransportResumeRequestPending = false;
            context.SparseSenderPumpLastWakeReason = "tuna_activation_handoff_pending";
            LogTunaActivationReadyResumeDeferred(FileTransferDirection.Outbound, context.TransferId, context.SessionId, reason);
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
            if (ShouldPromoteRegularNknV4ToFileTunaV4(
                    context.RouteRuntime,
                    context.NegotiatedDataProtocolVersion,
                    handoffKind,
                    targetTransport))
            {
                context.PullTransportResumeRequestPending = false;
                context.SparseSenderPumpLastWakeReason = "live_route_tuna_activated";
                return TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    handoffKind,
                    targetTransport);
            }

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
        if (ShouldDeferTunaActivationReadyResumeUntilRouteHandoff(
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

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Outbound, "sender", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        StartOutboundPostTunaRecoveryWithSafetyReplayLocked(context, reason);
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

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Inbound, "receiver", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(context, reason);
        StartInboundPostTunaRecoveryLocked(context, reason);
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

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV6Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Inbound, "receiver", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked(context, reason);
        StartInboundPostTunaRecoveryLocked(context, reason);
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

    private static bool ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(string? rejectionReason)
        => string.Equals(rejectionReason, "runtime_unlock_probe_missing", StringComparison.Ordinal) ||
           string.Equals(rejectionReason, "transaction_not_commit_ready", StringComparison.Ordinal);

    private static bool IsFallbackSurvivalProofPending(FileTransferLeg? leg)
        => IsCurrentPostTunaFallbackLeg(leg) &&
           (!leg!.CanSendData ||
            leg.State is FileTransferLegState.CheckpointPending or FileTransferLegState.BridgeRestartPending ||
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId));

    private static bool IsFallbackSurvivalProofPending(OutboundTransferContext context)
        => IsFallbackSurvivalProofPending(context.CurrentTransferLeg) ||
           context.V6RegularNknFallbackCheckpointDeliveryRecoveryPending ||
           context.V6RegularNknStateRefreshSendInFlight != 0 ||
           context.V6RegularNknDeferredStateRefreshRequest is not null;

    private static bool IsFallbackSurvivalProofPending(InboundTransferContext context)
        => IsFallbackSurvivalProofPending(context.CurrentTransferLeg) ||
           context.V6PostTunaFallbackProofReplayReceiverStatePending ||
           context.V6PostTunaFallbackProofReplayFrontierRequestPending;

    private bool TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            StartOutboundRuntimeUnlockPreCommitProbe(context, reason);
            return true;
        }

        if (!context.RuntimeUnlockActivationWindowGranted &&
            IsFallbackSurvivalProofPending(context))
        {
            MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(context, reason, rejectionReason);
            return true;
        }

        GrantOutboundRuntimeUnlockActivationWindowLocked(context, reason);
        StartOutboundRuntimeUnlockPreCommitProbe(context, reason);
        return true;
    }

    private bool TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
        InboundTransferContext context,
        string reason,
        string rejectionReason)
    {
        if (!context.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            StartInboundRuntimeUnlockPreCommitProbe(context, reason);
            return true;
        }

        if (!context.RuntimeUnlockActivationWindowGranted &&
            IsFallbackSurvivalProofPending(context))
        {
            MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(context, reason, rejectionReason);
            return true;
        }

        GrantInboundRuntimeUnlockActivationWindowLocked(context, reason);
        StartInboundRuntimeUnlockPreCommitProbe(context, reason);
        return true;
    }

    private void TryResumeOutboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (!context.RuntimeUnlockWaitingForFallbackSurvival ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            IsFallbackSurvivalProofPending(context))
        {
            return;
        }

        GrantOutboundRuntimeUnlockActivationWindowLocked(context, reason);
        StartOutboundRuntimeUnlockPreCommitProbe(context, reason);
    }

    private void TryResumeInboundRuntimeUnlockAfterFallbackSurvivalProofLocked(
        InboundTransferContext context,
        string reason)
    {
        if (!context.RuntimeUnlockWaitingForFallbackSurvival ||
            context.IsTerminal ||
            !context.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            IsFallbackSurvivalProofPending(context))
        {
            return;
        }

        GrantInboundRuntimeUnlockActivationWindowLocked(context, reason);
        StartInboundRuntimeUnlockPreCommitProbe(context, reason);
    }

    private static void MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
        OutboundTransferContext context,
        string reason,
        string rejectionReason)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = true;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = reason;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc ??= DateTimeOffset.UtcNow;
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
        string reason)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = true;
        context.RuntimeUnlockActivationWindowReason = reason;
        context.RuntimeUnlockActivationWindowGrantedUtc = DateTimeOffset.UtcNow;
        LogRuntimeUnlockActivationWindowGranted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            context.CurrentTransferLeg,
            reason);
    }

    private static void GrantInboundRuntimeUnlockActivationWindowLocked(
        InboundTransferContext context,
        string reason)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = true;
        context.RuntimeUnlockActivationWindowReason = reason;
        context.RuntimeUnlockActivationWindowGrantedUtc = DateTimeOffset.UtcNow;
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
    }

    private static void ClearInboundRuntimeUnlockFallbackSeparationStateLocked(InboundTransferContext context)
    {
        context.RuntimeUnlockWaitingForFallbackSurvival = false;
        context.RuntimeUnlockWaitingForFallbackSurvivalReason = null;
        context.RuntimeUnlockWaitingForFallbackSurvivalUtc = null;
        context.RuntimeUnlockActivationWindowGranted = false;
        context.RuntimeUnlockActivationWindowReason = null;
        context.RuntimeUnlockActivationWindowGrantedUtc = null;
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

    private static void LogRuntimeUnlockActivationWindowGranted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_activation_window_granted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; state={FormatProtocolLogValue(leg is null ? "none" : FormatFileTransferLegState(leg.State))}; can_send_data={(leg?.CanSendData == true ? 1 : 0)}");

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        OutboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            CreateCoordinatorStateLocked(context),
            routeSelection,
            Math.Clamp(context.ChunksTransferred, 0, Math.Max(0, context.ChunkCount)),
            Math.Max(-1, context.ChunksTransferred - 1),
            reason,
            out rejectionReason);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context,
            routeSelection,
            reason,
            out _);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        InboundTransferContext context,
        FileTransferRouteSelection routeSelection,
        string reason,
        out string rejectionReason)
        => TryAcceptRuntimeUnlockRouteCommitLocked(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            CreateCoordinatorStateLocked(context),
            routeSelection,
            Math.Max(0, context.NextChunkIndex),
            context.PullHighestReceivedChunkIndex,
            reason,
            out rejectionReason);

    private bool TryAcceptRuntimeUnlockRouteCommitLocked(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferCoordinatorState coordinatorState,
        FileTransferRouteSelection routeSelection,
        int committedChunkIndex,
        int highestObservedChunkIndex,
        string reason,
        out string rejectionReason)
    {
        rejectionReason = "none";
        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            return true;
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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(snapshot);
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
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
                RuntimeUnlockCommitProof: proof),
            coordinatorState);

        if (decision.RuntimeUnlockCommitAccepted)
        {
            proofProvider.NotifyRuntimeUnlockRouteCommitResult(
                sessionId,
                transferId,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                accepted: true,
                reason);
            LogRuntimeUnlockRouteCommitAccepted(
                direction,
                transferId,
                sessionId,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                reason);
            return true;
        }

        rejectionReason = decision.RuntimeUnlockCommitRejectedReason ?? "transaction_commit_rejected";
        if (!ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(rejectionReason))
        {
            proofProvider.NotifyRuntimeUnlockRouteCommitResult(
                sessionId,
                transferId,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                accepted: false,
                rejectionReason);
        }

        LogRuntimeUnlockRouteCommitRejected(
            direction,
            transferId,
            sessionId,
            reason,
            rejectionReason);
        return false;
    }

    private static void LogRuntimeUnlockRouteCommitAccepted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransfer",
            $"event=filetransfer_runtime_unlock_route_commit_accepted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; reason={FormatProtocolLogValue(reason)}");

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

        var previousRouteSelection = context.RouteSelection;
        var routeInput = new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransportProfileKind: ResolveTransportProfileKind(transport));
        var routeSelection = FileTransferRouteResolver.Resolve(routeInput);
        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out var rejectionReason))
        {
            if (ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(rejectionReason))
            {
                return TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
                    context,
                    reason,
                    rejectionReason);
            }

            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetOutboundV4AcceptedAfterPauseLocked(context, "live_route_tuna_activated");
        ResetOutboundRegularNknV4StateForFileTunaV4Locked(context);
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SparseSenderPumpLastWakeReason = "live_route_tuna_activated";

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Outbound, "sender", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        LogLiveRouteEpochRecovered(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            reason);
        return true;
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
        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out var rejectionReason))
        {
            if (ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(rejectionReason))
            {
                return TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
                    context,
                    reason,
                    rejectionReason);
            }

            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetInboundRegularNknV4StateForFileTunaV4Locked(context);
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Inbound, "receiver", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        LogLiveRouteEpochRecovered(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            reason);
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
        StartOutboundTransferLegLocked(
            context,
            context.RouteSelection,
            "live_route_tuna_activated",
            FileTransferLegState.Active,
            canSendData: true);
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
        StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            "live_route_tuna_activated",
            FileTransferLegState.Active,
            canSendData: true);
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
        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out var rejectionReason))
        {
            if (ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(rejectionReason))
            {
                return TryStartOutboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
                    context,
                    reason,
                    rejectionReason);
            }

            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetOutboundPostTunaFallbackStateForFileTunaV4Locked(context);
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SparseSenderPumpLastWakeReason = "live_route_tuna_reactivated";

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Outbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Outbound, "sender", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Outbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        LogLiveRouteEpochRecovered(
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            reason);
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
        if (!TryAcceptRuntimeUnlockRouteCommitLocked(context, routeSelection, reason, out var rejectionReason))
        {
            if (ShouldStartRuntimeUnlockTunaPathProbeAfterCommitRejection(rejectionReason))
            {
                return TryStartInboundRuntimeUnlockPreCommitProbeOrWaitForFallbackLocked(
                    context,
                    reason,
                    rejectionReason);
            }

            return false;
        }

        var runtimeSelection = FileTransferRuntimeProfileSelection.FromRouteSelection(routeSelection);
        var liveRouteEpoch = StartLiveRouteEpoch(
            context.LastLiveRouteEpochId,
            routeSelection,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            reason);
        context.LastLiveRouteEpochId = liveRouteEpoch.EpochId;
        context.CurrentLiveRouteEpoch = liveRouteEpoch;
        context.RouteSelection = routeSelection;
        context.NegotiatedDataProtocolVersion = routeSelection.ProtocolVersion;
        ApplyFileTransferRuntimeProfileSelectionLocked(context, runtimeSelection);
        ResetInboundPostTunaFallbackStateForFileTunaV4Locked(context);
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);

        LogFileTransferRouteTransitioned(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            previousRouteSelection,
            routeSelection,
            reason);
        LogFileTransferRouteSelected(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            routeInput,
            liveRouteEpoch.EpochId);
        NotifyFileTransferRouteHintObserver(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            routeSelection,
            liveRouteEpoch.EpochId,
            "service_live_route_transition");
        LogLiveRouteEpochStarted(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            previousRouteSelection);
        LogV4Negotiated(context.TransferId, context.SessionId, FileTransferDirection.Inbound, routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferRuntimeStarted(context.TransferId, context.SessionId, FileTransferDirection.Inbound, "receiver", routeSelection, liveRouteEpoch.EpochId);
        LogFileTransferBridgeRecoveryPolicySelected(
            context.TransferId,
            context.SessionId,
            FileTransferDirection.Inbound,
            runtimeSelection,
            routeSelection,
            liveRouteEpoch.EpochId);
        LogLiveRouteEpochRecovered(
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            liveRouteEpoch,
            reason);
        return true;
    }

    private void ResetOutboundPostTunaFallbackStateForFileTunaV4Locked(OutboundTransferContext context)
    {
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

        StartOutboundTransferLegLocked(
            context,
            context.RouteSelection,
            "live_route_tuna_reactivated",
            FileTransferLegState.Active,
            canSendData: true);
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

        StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            "live_route_tuna_reactivated",
            FileTransferLegState.Active,
            canSendData: true);
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

    private static bool TryStartInboundFileTunaV4PostTunaRecoveryLocked(
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

    private static void StartOutboundPostTunaRecoveryLocked(OutboundTransferContext context, string reason)
        => StartOutboundPostTunaRecoveryCoreLocked(
            context,
            reason,
            forceNewGeneration: false);

    private static void StartOutboundPostTunaRecoveryCoreLocked(
        OutboundTransferContext context,
        string reason,
        bool forceNewGeneration)
    {
        if (context.ChunkCount <= 0)
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
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier)
        {
            return;
        }

        RetireOutboundFallbackCheckpointExchangeLocked(context, null, reason);

        var leg = StartOutboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.CheckpointPending,
            canSendData: false);
        leg.BridgeRecoveryGeneration = generation;
        leg.TransportEpochId = ResolveOutboundFallbackCurrentTransportEpochLocked(context, generation);
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

    private void StartOutboundPostTunaRecoveryWithSafetyReplayLocked(OutboundTransferContext context, string reason)
    {
        var previousLeg = context.CurrentTransferLeg;
        StartOutboundPostTunaRecoveryLocked(context, reason);
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

    private static void StartInboundPostTunaRecoveryLocked(InboundTransferContext context, string reason)
    {
        if (context.ChunkCount <= 0)
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
            context.PullPostTunaRecoveryFrontierChunkIndex == frontier)
        {
            return;
        }

        var previousInboundLeg = context.CurrentTransferLeg;
        var retiredCheckpointRequestId = IsCurrentPostTunaFallbackLeg(previousInboundLeg)
            ? previousInboundLeg!.CheckpointRequestId
            : null;
        if (!string.IsNullOrWhiteSpace(retiredCheckpointRequestId))
        {
            FileTransferCoordinator.RetireFallbackCheckpointRequest(previousInboundLeg!);
            LogFileTransferFallbackCheckpointRetired(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                previousInboundLeg!,
                retiredCheckpointRequestId,
                reason);
        }

        var leg = StartInboundTransferLegLocked(
            context,
            context.RouteSelection,
            reason,
            FileTransferLegState.CheckpointPending,
            canSendData: false);
        leg.BridgeRecoveryGeneration = generation;
        leg.TransportEpochId = context.V6TransportEpoch?.EpochId ?? generation;
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
            $"event=filetransfer_fallback_leg_recovery_generation_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={FormatProtocolLogValue(reason)}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; recovery_generation={generation}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; transport_epoch={context.V6TransportEpoch?.EpochId ?? generation}; frontier_chunk_index={frontier}; committed_chunk={committed}; cleared_priority_request_count={clearedPriorityRequestCount}; cleared_normal_request_count={clearedNormalRequestCount}; cleared_metadata_count={clearedMetadataCount}; cleared_in_flight_count={clearedInFlightCount}; cleared_awaiting_ack_count={clearedAwaitingAckCount}; cleared_repair_queue_count={clearedRepairQueueCount}; cleared_repair_queued_chunk_count={clearedRepairQueuedChunkCount}; old_credit_discarded=1");
    }

    private static void ResetInboundPostTunaFallbackLegOwnedStateLocked(
        InboundTransferContext context,
        string reason,
        int generation,
        int frontier)
    {
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
           IsTunaActivationNegotiatedTransportReadyReason(reason) &&
           handoffKind == FileTransferTransportHandoffKind.None;

    private static void LogTunaActivationReadyResumeDeferred(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_tuna_activation_ready_resume_deferred_until_route_handoff; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={FormatProtocolLogValue(reason)}; pause_reason=tuna_activation_negotiating");

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
