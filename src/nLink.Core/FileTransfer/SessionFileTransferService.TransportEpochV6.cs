using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static TimeSpan ResolveV6TransportEpochProofTimeout()
        => V6TransportEpochProofTimeoutOverrideForTests ?? V6TransportEpochProofTimeout;

    private static bool IsV6TransportEpochUnresolved(V6TransportEpoch? epoch)
        => epoch is not null &&
           epoch.State is not V6TransportEpochState.Recovered and not V6TransportEpochState.Terminal;

    private static string FormatV6TransportEpochState(V6TransportEpochState state)
        => state switch
        {
            V6TransportEpochState.EpochStarting => "epoch_starting",
            V6TransportEpochState.TargetProofPending => "target_proof_pending",
            V6TransportEpochState.FrontierRepairOnly => "frontier_repair_only",
            V6TransportEpochState.BackfillRepair => "backfill_repair",
            V6TransportEpochState.Recovered => "recovered",
            V6TransportEpochState.WaitingForTargetTransport => "waiting_for_target_transport",
            V6TransportEpochState.Terminal => "terminal",
            _ => "none",
        };

    private static V6TransportEpochState ParseV6TransportEpochState(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "epoch_starting" => V6TransportEpochState.EpochStarting,
            "target_proof_pending" or "transport_proof_pending" or "nkn_proof_pending" => V6TransportEpochState.TargetProofPending,
            "frontier_repair_only" => V6TransportEpochState.FrontierRepairOnly,
            "backfill_repair" => V6TransportEpochState.BackfillRepair,
            "recovered" => V6TransportEpochState.Recovered,
            "waiting_for_target_transport" or "waiting_for_regular_nkn" => V6TransportEpochState.WaitingForTargetTransport,
            "terminal" => V6TransportEpochState.Terminal,
            _ => V6TransportEpochState.TargetProofPending,
        };

    private static FileTransferTransportKind ParseFileTransferTransportKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "regular_nkn" or "nkn" => FileTransferTransportKind.RegularNkn,
            "tuna" => FileTransferTransportKind.Tuna,
            _ => FileTransferTransportKind.Unknown,
        };

    private static FileTransferTransportHandoffKind ParseFileTransferTransportHandoffKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "normal_to_tuna_activation" => FileTransferTransportHandoffKind.NormalToTunaActivation,
            "tuna_to_normal_fallback" => FileTransferTransportHandoffKind.TunaToNormalFallback,
            "tuna_restart" => FileTransferTransportHandoffKind.TunaRestart,
            "regular_nkn_recovery" => FileTransferTransportHandoffKind.RegularNknRecovery,
            _ => FileTransferTransportHandoffKind.RegularNknRecovery,
        };

    private static FileTransferTransportKind ResolveV6EpochSourceTransport(
        FileTransferTransportHandoffKind kind,
        FileTransferTransportKind targetTransport)
        => kind switch
        {
            FileTransferTransportHandoffKind.NormalToTunaActivation => FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback => FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.TunaRestart => FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.RegularNknRecovery when targetTransport == FileTransferTransportKind.Tuna => FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.RegularNknRecovery => FileTransferTransportKind.Tuna,
            _ => targetTransport == FileTransferTransportKind.Tuna
                ? FileTransferTransportKind.RegularNkn
                : FileTransferTransportKind.Tuna,
        };

    private static string GetV6TransportEpochStatus(V6TransportEpoch epoch)
    {
        if (epoch.TargetTransport == FileTransferTransportKind.Tuna)
        {
            return "Switching to Tuna";
        }

        if (epoch.State == V6TransportEpochState.WaitingForTargetTransport)
        {
            return "Waiting for regular NKN";
        }

        if (epoch.State is V6TransportEpochState.FrontierRepairOnly or V6TransportEpochState.BackfillRepair)
        {
            return "Repairing over regular NKN";
        }

        return "Switching to regular NKN";
    }

    private static FileTransferTransportKind NormalizeV6TargetTransport(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => targetTransport != FileTransferTransportKind.Unknown
            ? targetTransport
            : handoffKind switch
            {
                FileTransferTransportHandoffKind.NormalToTunaActivation => FileTransferTransportKind.Tuna,
                FileTransferTransportHandoffKind.TunaRestart => FileTransferTransportKind.Tuna,
                _ => FileTransferTransportKind.RegularNkn,
            };

    private static FileTransferTransportHandoffKind NormalizeV6TransportHandoffKind(FileTransferTransportHandoffKind handoffKind)
        => handoffKind == FileTransferTransportHandoffKind.None
            ? FileTransferTransportHandoffKind.RegularNknRecovery
            : handoffKind;

    private static bool ShouldReuseCurrentV6TransportEpoch(
        V6TransportEpoch current,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        var normalizedKind = NormalizeV6TransportHandoffKind(handoffKind);
        if (current.TargetTransport != targetTransport)
        {
            return false;
        }

        return current.Kind == normalizedKind;
    }

    private void PublishV6TransportEpochSnapshot(string sessionId, string transferId, V6TransportEpoch epoch)
    {
        if (transport is not IFileTransferV6TransportEpochObserver observer)
        {
            return;
        }

        observer.ObserveFileTransferV6TransportEpoch(
            new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                epoch.Direction,
                epoch.EpochId,
                epoch.Kind,
                epoch.SourceTransport,
                epoch.TargetTransport,
                epoch.State,
                epoch.TerminalReason ?? epoch.Reason,
                IsV6TransportEpochUnresolved(epoch)));
    }

    private void StartOutboundV6TransportEpochLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (ShouldReuseCurrentV6TransportEpoch(current, handoffKind, targetTransport))
            {
                LogV6TransportEpochReused(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, reason);
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, "superseded");
        }

        var epochId = Math.Max(context.LastRecoveredV6TransportEpoch + 1, Math.Max(1, context.PullTransportRebindGeneration));
        var epoch = new V6TransportEpoch
        {
            EpochId = epochId,
            Kind = NormalizeV6TransportHandoffKind(handoffKind),
            SourceTransport = ResolveV6EpochSourceTransport(handoffKind, targetTransport),
            TargetTransport = targetTransport,
            Direction = FileTransferDirection.Outbound,
            Reason = NormalizeReason(reason) ?? "transport_epoch",
            State = V6TransportEpochState.TargetProofPending,
            StartingCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            ProbeId = $"v6-probe:{epochId}:{Guid.NewGuid():N}",
        };
        context.V6TransportEpoch = epoch;
        context.V6PendingEpochRepairRequestIds.Clear();
        context.V6SenderPumpLastWakeReason = "transport_epoch";
        context.V6UseRegularNknRedundantData = false;
        context.V6TunaRedundantDataEpochId = 0;
        context.V6TunaRedundantDataSatisfiedEpochId = 0;
        context.V6TunaRedundantDataProbeStartedUtc = null;
        context.V6TunaRedundantDataProbeStartedBytes = 0;
        context.V6RegularNknRedundantDataEpochId = 0;
        context.V6RegularNknRedundantDataDisabledEpochId = 0;
        context.V6RegularNknRedundantDataBatchCount = 0;
        context.StatusMessage = GetV6TransportEpochStatus(epoch);
        LogV6TransportEpochStarted(context.TransferId, context.SessionId, epoch);
        PublishV6TransportEpochSnapshot(context.SessionId, context.TransferId, epoch);
    }

    private void StartInboundV6TransportEpochLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (ShouldReuseCurrentV6TransportEpoch(current, handoffKind, targetTransport))
            {
                LogV6TransportEpochReused(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, reason);
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, "superseded");
        }

        var epochId = Math.Max(context.LastRecoveredV6TransportEpoch + 1, Math.Max(1, context.PullTransportRebindGeneration));
        var epoch = new V6TransportEpoch
        {
            EpochId = epochId,
            Kind = NormalizeV6TransportHandoffKind(handoffKind),
            SourceTransport = ResolveV6EpochSourceTransport(handoffKind, targetTransport),
            TargetTransport = targetTransport,
            Direction = FileTransferDirection.Inbound,
            Reason = NormalizeReason(reason) ?? "transport_epoch",
            State = V6TransportEpochState.TargetProofPending,
            StartingCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = context.PullHighestReceivedChunkIndex,
            LastObservedCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
            ProbeId = $"v6-probe:{epochId}:{Guid.NewGuid():N}",
        };
        context.V6TransportEpoch = epoch;
        context.V6ReceiverTransportEpoch = epoch.EpochId;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.StatusMessage = GetV6TransportEpochStatus(epoch);
        LogV6TransportEpochStarted(context.TransferId, context.SessionId, epoch);
        PublishV6TransportEpochSnapshot(context.SessionId, context.TransferId, epoch);
    }

    private static void LogV6TransportEpochStarted(string transferId, string sessionId, V6TransportEpoch epoch)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_started; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(epoch.Reason)}; state={FormatV6TransportEpochState(epoch.State)}; starting_committed_chunk={epoch.StartingCommittedChunkIndex}; starting_highest_observed_chunk={epoch.StartingHighestObservedChunkIndex}");

    private static void LogV6TransportEpochReused(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        V6TransportEpoch epoch,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_reused; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; state={FormatV6TransportEpochState(epoch.State)}");

    private bool TrySetV6TransportEpochStateLocked(
        V6TransportEpoch? epoch,
        string transferId,
        string sessionId,
        V6TransportEpochState nextState,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        if (epoch is null ||
            epoch.State == nextState ||
            epoch.State == V6TransportEpochState.Terminal ||
            epoch.State == V6TransportEpochState.Recovered && nextState != V6TransportEpochState.Recovered)
        {
            return false;
        }

        var previousState = epoch.State;
        epoch.State = nextState;
        epoch.LastStateChangeUtc = DateTimeOffset.UtcNow;
        epoch.LastObservedCommittedChunkIndex = committedChunkIndex;
        epoch.LastObservedHighestChunkIndex = highestObservedChunkIndex;
        if (nextState is V6TransportEpochState.FrontierRepairOnly or V6TransportEpochState.BackfillRepair or V6TransportEpochState.Recovered)
        {
            epoch.LastProofUtc = DateTimeOffset.UtcNow;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_state_changed; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; previous_state={FormatV6TransportEpochState(previousState)}; state={FormatV6TransportEpochState(nextState)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        if (nextState == V6TransportEpochState.WaitingForTargetTransport)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_waiting; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        }
        else if (nextState == V6TransportEpochState.Recovered)
        {
            var elapsedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - epoch.StartedUtc).TotalMilliseconds);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_recovered; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; elapsed_ms={elapsedMs}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        }

        PublishV6TransportEpochSnapshot(sessionId, transferId, epoch);
        return true;
    }

    private void TerminalizeV6TransportEpochLocked(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        V6TransportEpoch epoch,
        string reason)
    {
        if (epoch.State == V6TransportEpochState.Terminal)
        {
            return;
        }

        epoch.State = V6TransportEpochState.Terminal;
        epoch.TerminalReason = NormalizeReason(reason) ?? "terminal";
        epoch.TerminalUtc = DateTimeOffset.UtcNow;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_terminal; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; reason={FormatProtocolLogValue(epoch.TerminalReason)}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}");
        PublishV6TransportEpochSnapshot(sessionId, transferId, epoch);
    }

    private static FileTransferTransportEpochV6 CreateV6TransportEpochControl(
        string sessionId,
        string transferId,
        V6TransportEpoch epoch)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransportEpoch = epoch.EpochId,
            State = FormatV6TransportEpochState(epoch.State),
            HandoffKind = FormatFileTransferTransportHandoffKind(epoch.Kind),
            SourceTransport = FormatFileTransferTransportKind(epoch.SourceTransport),
            TargetTransport = FormatFileTransferTransportKind(epoch.TargetTransport),
            Reason = epoch.Reason,
            RecoveryMode = FormatV6TransportEpochState(epoch.State),
        };

    private async Task AnnounceAndProbeOutboundV6TransportEpochAsync(OutboundTransferContext context)
    {
        V6TransportEpoch? epoch;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || context.V6TransportEpoch is null)
            {
                return;
            }

            epoch = context.V6TransportEpoch;
            dataSession = context.DataSession;
            epoch.LastAnnouncedUtc = DateTimeOffset.UtcNow;
        }

        await SendV6TransportEpochControlAsync(context.SessionId, context.TransferId, epoch).ConfigureAwait(false);
        await SendV6TransportProbeFrameAsync(context.SessionId, context.TransferId, epoch, dataSession, context.LifetimeCts.Token).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                ReferenceEquals(context.V6TransportEpoch, epoch) &&
                IsV6TransportEpochUnresolved(epoch) &&
                epoch.State == V6TransportEpochState.TargetProofPending)
            {
                var changed = TrySetV6TransportEpochStateLocked(
                    epoch,
                    context.TransferId,
                    context.SessionId,
                    V6TransportEpochState.FrontierRepairOnly,
                    "probe_sent",
                    context.RemoteNextExpectedChunkIndex,
                    Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                if (changed)
                {
                    context.StatusMessage = GetV6TransportEpochStatus(epoch);
                }
            }
        }

        ScheduleOutboundV6TransportEpochProofTimeout(context, epoch.EpochId);
    }

    private async Task AnnounceAndProbeInboundV6TransportEpochAsync(InboundTransferContext context)
    {
        V6TransportEpoch? epoch;
        IFileTransferDataSession? dataSession;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal || context.V6TransportEpoch is null)
            {
                return;
            }

            epoch = context.V6TransportEpoch;
            dataSession = context.DataSession;
            epoch.LastAnnouncedUtc = DateTimeOffset.UtcNow;
        }

        await SendV6TransportEpochControlAsync(context.SessionId, context.TransferId, epoch).ConfigureAwait(false);
        await SendV6TransportProbeFrameAsync(context.SessionId, context.TransferId, epoch, dataSession, context.LifetimeCts.Token).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                ReferenceEquals(context.V6TransportEpoch, epoch) &&
                IsV6TransportEpochUnresolved(epoch) &&
                epoch.State == V6TransportEpochState.TargetProofPending)
            {
                var changed = TrySetV6TransportEpochStateLocked(
                    epoch,
                    context.TransferId,
                    context.SessionId,
                    V6TransportEpochState.FrontierRepairOnly,
                    "probe_sent",
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex);
                if (changed)
                {
                    context.StatusMessage = GetV6TransportEpochStatus(epoch);
                }
                context.V6ReceiverTransportEpoch = epoch.EpochId;
            }
        }

        await SendInboundV6ReceiverStateAsync(context, "transport_epoch", forceSend: true).ConfigureAwait(false);
        await SendInboundV6FrontierRequestAsync(context, "transport_epoch", forceSend: true).ConfigureAwait(false);
        ScheduleInboundV6TransportEpochProofTimeout(context, epoch.EpochId);
    }

    private async Task SendV6TransportEpochControlAsync(string sessionId, string transferId, V6TransportEpoch epoch)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferTransportEpochAsync(
                    CreateV6TransportEpochControl(sessionId, transferId, epoch),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_announce_failed; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; error={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private async Task SendV6TransportProbeFrameAsync(
        string sessionId,
        string transferId,
        V6TransportEpoch epoch,
        IFileTransferDataSession? dataSession,
        CancellationToken ct)
    {
        if (dataSession is null ||
            string.IsNullOrWhiteSpace(epoch.ProbeId))
        {
            return;
        }

        var frame = new FileTransferTransportProbeFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransportEpoch = epoch.EpochId,
            ProbeId = epoch.ProbeId,
            TargetTransport = FormatFileTransferTransportKind(epoch.TargetTransport),
        };

        try
        {
            await dataSession.SendAsync(frame, ct).ConfigureAwait(false);
            epoch.LastProbeSentUtc = DateTimeOffset.UtcNow;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_sent; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; probe_id={FormatProtocolLogValue(epoch.ProbeId)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_failed; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; error={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private async Task HandleReceivedV6TransportProbeFrameAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferTransportProbeFrameV6 frame,
        FileTransferTransportKind receivedTransportKind)
    {
        var targetTransport = ParseFileTransferTransportKind(frame.TargetTransport);
        if (receivedTransportKind == FileTransferTransportKind.Unknown ||
            targetTransport == FileTransferTransportKind.Unknown ||
            receivedTransportKind != targetTransport ||
            frame.TransportEpoch <= 0 ||
            string.IsNullOrWhiteSpace(frame.ProbeId))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; probe_id={FormatProtocolLogValue(frame.ProbeId ?? "(none)")}; target_transport={FormatFileTransferTransportKind(targetTransport)}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; reason=transport_proof_mismatch");
            return;
        }

        try
        {
            var currentTransport = GetTransportOrThrow();
            var ack = new FileTransferTransportProbeV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frame.TransportEpoch,
                ProbeId = frame.ProbeId,
                TargetTransport = frame.TargetTransport,
            };
            await currentTransport.SendFileTransferTransportProbeAsync(ack, CancellationToken.None).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_ack_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; probe_id={FormatProtocolLogValue(frame.ProbeId)}; target_transport={FormatFileTransferTransportKind(targetTransport)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_ack_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; error={FormatProtocolLogValue(ex.Message)}");
        }
    }

    private Task HandleIncomingTransportEpochAsync(FileTransferTransportEpochV6 message)
    {
        OutboundTransferContext? outboundToProbe = null;
        InboundTransferContext? inboundToProbe = null;
        lock (gate)
        {
            if (IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                var previousEpochId = outbound.V6TransportEpoch?.EpochId;
                AdoptOutboundV6TransportEpochLocked(outbound, message);
                if (previousEpochId != message.TransportEpoch)
                {
                    outboundToProbe = outbound;
                }
            }

            if (IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                var previousEpochId = inbound.V6TransportEpoch?.EpochId;
                AdoptInboundV6TransportEpochLocked(inbound, message);
                if (previousEpochId != message.TransportEpoch)
                {
                    inboundToProbe = inbound;
                }
            }
        }

        if (outboundToProbe is not null)
        {
            TouchOutboundV6PeerLiveness(outboundToProbe, "transport_epoch");
            _ = AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToProbe);
        }

        if (inboundToProbe is not null)
        {
            TouchInboundV6PeerLiveness(inboundToProbe, "transport_epoch");
            _ = AnnounceAndProbeInboundV6TransportEpochAsync(inboundToProbe);
        }

        return Task.CompletedTask;
    }

    private void AdoptOutboundV6TransportEpochLocked(OutboundTransferContext context, FileTransferTransportEpochV6 message)
    {
        if (message.TransportEpoch <= context.LastRecoveredV6TransportEpoch)
        {
            return;
        }

        var target = ParseFileTransferTransportKind(message.TargetTransport);
        var kind = ParseFileTransferTransportHandoffKind(message.HandoffKind);
        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (message.TransportEpoch < current.EpochId)
            {
                LogStalePeerV6TransportEpochIgnored(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, message);
                return;
            }

            if (current.EpochId == message.TransportEpoch)
            {
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, "superseded_by_peer_epoch");
        }

        var epoch = new V6TransportEpoch
        {
            EpochId = message.TransportEpoch,
            Kind = kind,
            SourceTransport = ParseFileTransferTransportKind(message.SourceTransport),
            TargetTransport = target == FileTransferTransportKind.Unknown ? FileTransferTransportKind.RegularNkn : target,
            Direction = FileTransferDirection.Outbound,
            Reason = NormalizeReason(message.Reason) ?? "peer_transport_epoch",
            State = ParseV6TransportEpochState(message.State),
            StartingCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1),
            ProbeId = $"v6-probe:{message.TransportEpoch}:{Guid.NewGuid():N}",
        };
        context.V6TransportEpoch = epoch;
        context.StatusMessage = GetV6TransportEpochStatus(epoch);
        LogV6TransportEpochStarted(context.TransferId, context.SessionId, epoch);
        PublishV6TransportEpochSnapshot(context.SessionId, context.TransferId, epoch);
    }

    private void AdoptInboundV6TransportEpochLocked(InboundTransferContext context, FileTransferTransportEpochV6 message)
    {
        if (message.TransportEpoch <= context.LastRecoveredV6TransportEpoch)
        {
            return;
        }

        var target = ParseFileTransferTransportKind(message.TargetTransport);
        var kind = ParseFileTransferTransportHandoffKind(message.HandoffKind);
        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (message.TransportEpoch < current.EpochId)
            {
                LogStalePeerV6TransportEpochIgnored(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, message);
                return;
            }

            if (current.EpochId == message.TransportEpoch)
            {
                context.V6ReceiverTransportEpoch = current.EpochId;
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, "superseded_by_peer_epoch");
        }

        var epoch = new V6TransportEpoch
        {
            EpochId = message.TransportEpoch,
            Kind = kind,
            SourceTransport = ParseFileTransferTransportKind(message.SourceTransport),
            TargetTransport = target == FileTransferTransportKind.Unknown ? FileTransferTransportKind.RegularNkn : target,
            Direction = FileTransferDirection.Inbound,
            Reason = NormalizeReason(message.Reason) ?? "peer_transport_epoch",
            State = ParseV6TransportEpochState(message.State),
            StartingCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            StartingHighestObservedChunkIndex = context.PullHighestReceivedChunkIndex,
            LastObservedCommittedChunkIndex = Math.Clamp(context.NextChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = context.PullHighestReceivedChunkIndex,
            ProbeId = $"v6-probe:{message.TransportEpoch}:{Guid.NewGuid():N}",
        };
        context.V6TransportEpoch = epoch;
        context.V6ReceiverTransportEpoch = epoch.EpochId;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        context.StatusMessage = GetV6TransportEpochStatus(epoch);
        LogV6TransportEpochStarted(context.TransferId, context.SessionId, epoch);
        PublishV6TransportEpochSnapshot(context.SessionId, context.TransferId, epoch);
    }

    private static void LogStalePeerV6TransportEpochIgnored(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        V6TransportEpoch current,
        FileTransferTransportEpochV6 message)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_peer_epoch_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason=stale_peer_epoch; current_transport_epoch={current.EpochId}; peer_transport_epoch={message.TransportEpoch}; current_handoff_kind={FormatFileTransferTransportHandoffKind(current.Kind)}; peer_handoff_kind={FormatProtocolLogValue(message.HandoffKind)}; current_target_transport={FormatFileTransferTransportKind(current.TargetTransport)}; peer_target_transport={FormatProtocolLogValue(message.TargetTransport)}");

    private Task HandleIncomingTransportProbeAckAsync(FileTransferTransportProbeV6 message)
    {
        OutboundTransferContext? outboundRecovered = null;
        InboundTransferContext? inboundRecovered = null;
        lock (gate)
        {
            if (IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                TryRecoverOutboundV6TransportEpochFromProbeAckLocked(outbound, message))
            {
                outboundRecovered = outbound;
            }

            if (IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                inboundTransfer is { IsTerminal: false } inbound &&
                TryRecoverInboundV6TransportEpochFromProbeAckLocked(inbound, message))
            {
                inboundRecovered = inbound;
            }
        }

        if (outboundRecovered is not null)
        {
            TouchOutboundV6PeerLiveness(outboundRecovered, "transport_probe_ack");
            SignalOutboundV4SenderPump(outboundRecovered);
        }

        if (inboundRecovered is not null)
        {
            TouchInboundV6PeerLiveness(inboundRecovered, "transport_probe_ack");
            _ = SendInboundV6ReceiverStateAsync(inboundRecovered, "transport_probe_ack", forceSend: true);
        }

        return Task.CompletedTask;
    }

    private bool TryRecoverOutboundV6TransportEpochFromProbeAckLocked(OutboundTransferContext context, FileTransferTransportProbeV6 message)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != message.TransportEpoch ||
            string.IsNullOrWhiteSpace(epoch.ProbeId) ||
            !string.Equals(epoch.ProbeId, message.ProbeId, StringComparison.Ordinal) ||
            ParseFileTransferTransportKind(message.TargetTransport) != epoch.TargetTransport)
        {
            return false;
        }

        return CompleteOutboundV6TransportEpochLocked(context, "transport_probe_ack");
    }

    private bool TryRecoverInboundV6TransportEpochFromProbeAckLocked(InboundTransferContext context, FileTransferTransportProbeV6 message)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != message.TransportEpoch ||
            string.IsNullOrWhiteSpace(epoch.ProbeId) ||
            !string.Equals(epoch.ProbeId, message.ProbeId, StringComparison.Ordinal) ||
            ParseFileTransferTransportKind(message.TargetTransport) != epoch.TargetTransport)
        {
            return false;
        }

        return CompleteInboundV6TransportEpochLocked(context, "transport_probe_ack");
    }

    private Task HandleIncomingRepairProofAsync(FileTransferRepairProofV6 message)
    {
        OutboundTransferContext? outboundRecovered = null;
        lock (gate)
        {
            if (IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                TryRecoverOutboundV6TransportEpochFromRepairProofLocked(outbound, message))
            {
                outboundRecovered = outbound;
            }
        }

        if (outboundRecovered is not null)
        {
            TouchOutboundV6PeerLiveness(outboundRecovered, "repair_proof");
            SignalOutboundV4SenderPump(outboundRecovered);
        }

        return Task.CompletedTask;
    }

    private bool TryRecoverOutboundV6TransportEpochFromRepairProofLocked(OutboundTransferContext context, FileTransferRepairProofV6 message)
    {
        var epoch = context.V6TransportEpoch;
        var repairRequestId = message.RepairRequestId?.Trim();
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != message.TransportEpoch ||
            string.IsNullOrWhiteSpace(repairRequestId) ||
            (!string.Equals(epoch.LastRepairRequestId, repairRequestId, StringComparison.Ordinal) &&
             !context.V6PendingEpochRepairRequestIds.Contains(repairRequestId)))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(message.RepairRequestId ?? "(none)")}; current_transport_epoch={epoch?.EpochId ?? 0}; last_repair_request_id={FormatProtocolLogValue(epoch?.LastRepairRequestId ?? "(none)")}; reason=repair_request_mismatch");
            return false;
        }

        return CompleteOutboundV6TransportEpochLocked(context, "frontier_repair_proof");
    }

    private bool CompleteOutboundV6TransportEpochLocked(OutboundTransferContext context, string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return false;
        }

        TrySetV6TransportEpochStateLocked(
            epoch,
            context.TransferId,
            context.SessionId,
            V6TransportEpochState.Recovered,
            reason,
            context.RemoteNextExpectedChunkIndex,
            Math.Max(-1, context.ChunksAcceptedForTransport - 1));
        context.LastRecoveredV6TransportEpoch = Math.Max(context.LastRecoveredV6TransportEpoch, epoch!.EpochId);
        context.LastRecoveredV6TransportEpochKind = epoch.Kind;
        context.LastRecoveredV6TransportTargetTransport = epoch.TargetTransport;
        context.V6TransportEpoch = null;
        context.V6PendingEpochRepairRequestIds.Clear();
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.V6SenderPumpLastWakeReason = "transport_epoch_recovered";
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        return true;
    }

    private bool CompleteInboundV6TransportEpochLocked(InboundTransferContext context, string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return false;
        }

        TrySetV6TransportEpochStateLocked(
            epoch,
            context.TransferId,
            context.SessionId,
            V6TransportEpochState.Recovered,
            reason,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex);
        context.LastRecoveredV6TransportEpoch = Math.Max(context.LastRecoveredV6TransportEpoch, epoch!.EpochId);
        context.LastRecoveredV6TransportEpochKind = epoch.Kind;
        context.LastRecoveredV6TransportTargetTransport = epoch.TargetTransport;
        // Keep stamping receiver requests with the recovered epoch until a new epoch or
        // transfer terminalization. The peer sender may still be proving its symmetric
        // epoch and will reject epoch-0 frontier requests while unresolved.
        context.V6ReceiverTransportEpoch = epoch.EpochId;
        context.V6TransportEpoch = null;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        context.PullTransportRebindGeneration = 0;
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);
        return true;
    }

    private void ScheduleOutboundV6TransportEpochProofTimeout(OutboundTransferContext context, long epochId)
        => _ = RunOutboundV6TransportEpochProofTimeoutAsync(context, epochId);

    private async Task RunOutboundV6TransportEpochProofTimeoutAsync(OutboundTransferContext context, long epochId)
    {
        try
        {
            await Task.Delay(ResolveV6TransportEpochProofTimeout(), context.LifetimeCts.Token).ConfigureAwait(false);
            bool waiting = false;
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    context.V6TransportEpoch is { } epoch &&
                    epoch.EpochId == epochId &&
                    IsV6TransportEpochUnresolved(epoch))
                {
                    waiting = TrySetV6TransportEpochStateLocked(
                        epoch,
                        context.TransferId,
                        context.SessionId,
                        V6TransportEpochState.WaitingForTargetTransport,
                        "proof_timeout",
                        context.RemoteNextExpectedChunkIndex,
                        Math.Max(-1, context.ChunksAcceptedForTransport - 1));
                    context.StatusMessage = GetV6TransportEpochStatus(epoch);
                }
            }

            if (waiting)
            {
                _ = AnnounceAndProbeOutboundV6TransportEpochAsync(context);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private void ScheduleInboundV6TransportEpochProofTimeout(InboundTransferContext context, long epochId)
        => _ = RunInboundV6TransportEpochProofTimeoutAsync(context, epochId);

    private async Task RunInboundV6TransportEpochProofTimeoutAsync(InboundTransferContext context, long epochId)
    {
        try
        {
            await Task.Delay(ResolveV6TransportEpochProofTimeout(), context.LifetimeCts.Token).ConfigureAwait(false);
            bool waiting = false;
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) &&
                    context.V6TransportEpoch is { } epoch &&
                    epoch.EpochId == epochId &&
                    IsV6TransportEpochUnresolved(epoch))
                {
                    waiting = TrySetV6TransportEpochStateLocked(
                        epoch,
                        context.TransferId,
                        context.SessionId,
                        V6TransportEpochState.WaitingForTargetTransport,
                        "proof_timeout",
                        context.NextChunkIndex,
                        context.PullHighestReceivedChunkIndex);
                    context.StatusMessage = GetV6TransportEpochStatus(epoch);
                }
            }

            if (waiting)
            {
                _ = AnnounceAndProbeInboundV6TransportEpochAsync(context);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private bool IsOutboundV6ChunkBlockedByTransportEpochLocked(
        OutboundTransferContext context,
        int chunkIndex,
        V6OutboundChunkRequestMetadata metadata)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return false;
        }

        if (metadata.TransportEpoch != epoch!.EpochId)
        {
            return true;
        }

        if (epoch.State == V6TransportEpochState.TargetProofPending)
        {
            return true;
        }

        if (epoch.State is V6TransportEpochState.FrontierRepairOnly or V6TransportEpochState.WaitingForTargetTransport)
        {
            return !metadata.Priority ||
                   chunkIndex != context.RemoteNextExpectedChunkIndex ||
                   !string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase);
        }

        if (epoch.State == V6TransportEpochState.BackfillRepair)
        {
            return !metadata.Priority;
        }

        return false;
    }

    private async Task MaybeSendInboundV6RepairProofAsync(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind receivedTransportKind,
        int appliedChunkCount,
        int committedChunkIndex,
        int previousCommittedChunkIndex)
    {
        FileTransferRepairProofV6? proof = null;
        lock (gate)
        {
            var epoch = context.V6TransportEpoch;
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsV6TransportEpochUnresolved(epoch) ||
                batch.TransportEpoch != epoch!.EpochId ||
                receivedTransportKind == FileTransferTransportKind.Unknown ||
                receivedTransportKind != epoch.TargetTransport ||
                string.IsNullOrWhiteSpace(batch.RepairRequestId) ||
                batch.StartChunkIndex != previousCommittedChunkIndex ||
                committedChunkIndex <= previousCommittedChunkIndex)
            {
                return;
            }

            epoch.LastRepairRequestId = batch.RepairRequestId;
            proof = new FileTransferRepairProofV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = epoch.EpochId,
                RepairRequestId = batch.RepairRequestId,
                AppliedChunkCount = appliedChunkCount,
                CommittedChunkIndex = committedChunkIndex,
                RecoveryMode = FormatV6TransportEpochState(epoch.State),
            };
            CompleteInboundV6TransportEpochLocked(context, "frontier_chunk_proof");
        }

        if (proof is null)
        {
            return;
        }

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferRepairProofAsync(proof, CancellationToken.None).ConfigureAwait(false);
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
}
