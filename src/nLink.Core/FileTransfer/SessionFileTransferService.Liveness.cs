using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static TimeSpan CurrentV6HeartbeatInterval
        => V6HeartbeatIntervalOverrideForTests ?? TimeSpan.FromMilliseconds(V6HeartbeatIntervalMs);

    private static TimeSpan CurrentV6PeerLivenessTimeout
        => V6PeerLivenessTimeoutOverrideForTests ?? TimeSpan.FromMilliseconds(V6PeerLivenessTimeoutMs);

    private static TimeSpan CurrentV6SenderRequestFeedbackStallRecoveryDelay
        => V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests ?? TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoveryMs);

    private static TimeSpan CurrentV6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelay
        => V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests ??
           TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryMs);

    private static TimeSpan CurrentV6RegularNknSparseRuntimeStateRefreshCooldown
        => V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests ??
           TimeSpan.FromMilliseconds(V6RegularNknSparseRuntimeStateRefreshCooldownMs);

    private static TimeSpan CurrentV6SenderRequestFeedbackStallRecoveryCooldown
        => TimeSpan.FromMilliseconds(V6SenderRequestFeedbackStallRecoveryCooldownMs);

    private bool TryRequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
    {
        IFileTransferReceiveRecoveryController? controller;
        FileTransferBridgeRecoveryPolicy bridgeRecoveryPolicy;
        lock (gate)
        {
            controller = transport as IFileTransferReceiveRecoveryController;
            bridgeRecoveryPolicy = ResolveReceiveRecoveryPolicyForRequestLocked(request);
        }

        if (controller is null)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_unsupported; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}");
            return false;
        }

        try
        {
            controller.RequestFileTransferReceiveRecovery(request);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}");
            return true;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_transport_receive_recovery_request_failed; direction={request.Direction.ToString().ToLowerInvariant()}; transfer_id={request.TransferId}; session_id={request.SessionId}; reason={FormatProtocolLogValue(request.Reason)}; error={FormatProtocolLogValue(ex.GetType().Name)}; bridge_recovery_policy={FormatFileTransferBridgeRecoveryPolicy(bridgeRecoveryPolicy)}");
            return false;
        }
    }

    private static TimeSpan ResolveV6PeerLivenessTimeout(V6TransportEpoch? epoch)
    {
        var timeout = CurrentV6PeerLivenessTimeout;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return timeout;
        }

        var epochTimeout = ResolveV6TransportEpochProofTimeout() + CurrentV6HeartbeatInterval * 6;
        return epochTimeout > timeout ? epochTimeout : timeout;
    }

    private static TimeSpan ResolveV6FallbackRecoveryLivenessTimeout()
    {
        var timeout = CurrentV6PeerLivenessTimeout;
        var recoveryTimeout = ResolveV6TransportEpochProofTimeout() + CurrentV6HeartbeatInterval * 6;
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static TimeSpan ResolveV6RegularNknPeerLivenessRepairGrace(TimeSpan peerLivenessTimeout)
    {
        if (peerLivenessTimeout <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var ticks = peerLivenessTimeout.Ticks;
        var graceTicks = ticks > TimeSpan.MaxValue.Ticks / V6RegularNknPeerLivenessRepairGraceMultiplier
            ? TimeSpan.MaxValue.Ticks
            : ticks * V6RegularNknPeerLivenessRepairGraceMultiplier;
        return TimeSpan.FromTicks(graceTicks);
    }

    private static DateTimeOffset? ResolveV6PeerLivenessDeadlineBaseUtc(
        DateTimeOffset? lastPeerLivenessUtc,
        V6TransportEpoch? epoch,
        DateTimeOffset? epochLivenessDeferralUtc)
    {
        var baseUtc = lastPeerLivenessUtc;
        if (IsV6TransportEpochUnresolved(epoch) &&
            epoch is not null &&
            (baseUtc is null ||
             epoch.StartedUtc > baseUtc.Value))
        {
            baseUtc = epoch.StartedUtc;
        }

        if (epochLivenessDeferralUtc is not null &&
            (baseUtc is null ||
             epochLivenessDeferralUtc.Value > baseUtc.Value))
        {
            baseUtc = epochLivenessDeferralUtc.Value;
        }

        return baseUtc;
    }

    private static TimeSpan ResolveOutboundV6PeerLivenessTimeout(OutboundTransferContext context)
    {
        var timeout = ResolveV6PeerLivenessTimeout(context.V6TransportEpoch);
        if (!IsOutboundV6FallbackRecoveryLivenessExtensionActive(context))
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static TimeSpan ResolveInboundV6PeerLivenessTimeout(InboundTransferContext context)
    {
        var timeout = ResolveV6PeerLivenessTimeout(context.V6TransportEpoch);
        if (!IsInboundV6FallbackRecoveryLivenessExtensionActive(context))
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private static bool IsOutboundV6FallbackRecoveryLivenessExtensionActive(OutboundTransferContext context)
        => context.V6UseRegularNknRedundantData ||
           context.PullPostTunaRecoveryActive ||
           context.PullTransportPaused ||
           context.PullTransportResumeRequestPending;

    private static bool IsInboundV6FallbackRecoveryLivenessExtensionActive(InboundTransferContext context)
        => context.PullPostTunaRecoveryActive ||
           context.PullTransportPaused ||
           context.PullTransportResumeRequestPending;

    internal static TimeSpan ResolveV6PeerLivenessTimeoutForTests(bool unresolvedEpoch, bool fallbackRecoveryActive)
    {
        var timeout = unresolvedEpoch
            ? ResolveV6PeerLivenessTimeout(new V6TransportEpoch
            {
                EpochId = 1,
                Kind = FileTransferTransportHandoffKind.RegularNknRecovery,
                SourceTransport = FileTransferTransportKind.Tuna,
                TargetTransport = FileTransferTransportKind.RegularNkn,
                Reason = "test",
                StartingCommittedChunkIndex = 0,
                StartingHighestObservedChunkIndex = -1,
                State = V6TransportEpochState.TargetProofPending,
                StartedUtc = DateTimeOffset.UtcNow,
                LastStateChangeUtc = DateTimeOffset.UtcNow,
            })
            : CurrentV6PeerLivenessTimeout;

        if (!fallbackRecoveryActive)
        {
            return timeout;
        }

        var recoveryTimeout = ResolveV6FallbackRecoveryLivenessTimeout();
        return recoveryTimeout > timeout ? recoveryTimeout : timeout;
    }

    private void StartOutboundV6HeartbeatLoop(OutboundTransferContext context, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) &&
                !string.IsNullOrWhiteSpace(context.SessionId) &&
                !context.V6HeartbeatLoopStarted)
            {
                context.V6HeartbeatLoopStarted = true;
                context.V6LastPeerLivenessUtc ??= DateTimeOffset.UtcNow;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; interval_ms={(long)CurrentV6HeartbeatInterval.TotalMilliseconds}; timeout_ms={(long)CurrentV6PeerLivenessTimeout.TotalMilliseconds}");
        _ = Task.Run(() => RunOutboundV6HeartbeatLoopAsync(context), CancellationToken.None);
    }

    private void StartInboundV6HeartbeatLoop(InboundTransferContext context, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) &&
                !string.IsNullOrWhiteSpace(context.SessionId) &&
                !context.V6HeartbeatLoopStarted)
            {
                context.V6HeartbeatLoopStarted = true;
                context.V6LastPeerLivenessUtc ??= DateTimeOffset.UtcNow;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_started; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; interval_ms={(long)CurrentV6HeartbeatInterval.TotalMilliseconds}; timeout_ms={(long)CurrentV6PeerLivenessTimeout.TotalMilliseconds}");
        _ = Task.Run(() => RunInboundV6HeartbeatLoopAsync(context), CancellationToken.None);
    }

    private async Task RunOutboundV6HeartbeatLoopAsync(OutboundTransferContext context)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CurrentV6HeartbeatInterval, context.LifetimeCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                FileTransferHeartbeatV6? heartbeat = null;
                var timedOut = false;
                DateTimeOffset? lastPeerLivenessUtc = null;
                DateTimeOffset? livenessDeadlineBaseUtc = null;
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    peerLivenessTimeout = ResolveOutboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    livenessDeadlineBaseUtc = ResolveV6PeerLivenessDeadlineBaseUtc(
                        lastPeerLivenessUtc,
                        context.V6TransportEpoch,
                        context.V6EpochLivenessDeferralUtc);
                    timedOut = livenessDeadlineBaseUtc is not null &&
                        now - livenessDeadlineBaseUtc.Value >= peerLivenessTimeout;
                    if (!timedOut)
                    {
                        heartbeat = new FileTransferHeartbeatV6
                        {
                            SessionId = context.SessionId,
                            TransferId = context.TransferId,
                            TransportEpoch = context.V6TransportEpoch?.EpochId ?? 0,
                            Sequence = ++context.V6HeartbeatSequence,
                            SentUnixTimeMilliseconds = now.ToUnixTimeMilliseconds(),
                        };
                    }
                }

                if (timedOut)
                {
                    var terminalized = await TerminalizeOutboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    if (terminalized)
                    {
                        return;
                    }

                    continue;
                }

                if (heartbeat is not null)
                {
                    await SendHeartbeatAsync(heartbeat, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private async Task RunInboundV6HeartbeatLoopAsync(InboundTransferContext context)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CurrentV6HeartbeatInterval, context.LifetimeCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                FileTransferHeartbeatV6? heartbeat = null;
                var timedOut = false;
                DateTimeOffset? lastPeerLivenessUtc = null;
                DateTimeOffset? livenessDeadlineBaseUtc = null;
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    peerLivenessTimeout = ResolveInboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    livenessDeadlineBaseUtc = ResolveV6PeerLivenessDeadlineBaseUtc(
                        lastPeerLivenessUtc,
                        context.V6TransportEpoch,
                        context.V6EpochLivenessDeferralUtc);
                    timedOut = livenessDeadlineBaseUtc is not null &&
                        now - livenessDeadlineBaseUtc.Value >= peerLivenessTimeout;
                    if (!timedOut)
                    {
                        heartbeat = new FileTransferHeartbeatV6
                        {
                            SessionId = context.SessionId,
                            TransferId = context.TransferId,
                            TransportEpoch = context.V6TransportEpoch?.EpochId ?? 0,
                            Sequence = ++context.V6HeartbeatSequence,
                            SentUnixTimeMilliseconds = now.ToUnixTimeMilliseconds(),
                        };
                    }
                }

                if (timedOut)
                {
                    var terminalized = await TerminalizeInboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    if (terminalized)
                    {
                        return;
                    }

                    continue;
                }

                if (heartbeat is not null)
                {
                    await SendHeartbeatAsync(heartbeat, context.LifetimeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TerminalizeOutboundForPeerLivenessTimeoutAsync(OutboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
        if (TryDeferOutboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundEpochToProbe))
        {
            if (outboundEpochToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundEpochToProbe).ConfigureAwait(false);
                SignalOutboundV4SenderPump(outboundEpochToProbe);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundToSignal))
        {
            if (outboundToSignal is not null)
            {
                SignalOutboundV4SenderPump(outboundToSignal);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForRecovery(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var outboundToProbe))
        {
            TryRequestFileTransferReceiveRecovery(new FileTransferReceiveRecoveryRequest(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                "peer_liveness_stale_receive_recovery"));

            if (outboundToProbe is not null)
            {
                await AnnounceAndProbeOutboundV6TransportEpochAsync(outboundToProbe).ConfigureAwait(false);
                SignalOutboundV4SenderPump(outboundToProbe);
            }

            return false;
        }

        if (TryDeferOutboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var sparseRuntimeOutboundToSignal))
        {
            var primaryRegularNknBulkV6 = false;
            lock (gate)
            {
                primaryRegularNknBulkV6 =
                    ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal &&
                    IsPrimaryRegularNknBulkV6ContextLocked(context);
            }

            if (primaryRegularNknBulkV6)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_recovery_deferred_bridge_recovery; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=peer_liveness_stale; recovery_profile=regular_nkn_checkpoint_sync");
            }
            else
            {
                TryRequestFileTransferReceiveRecovery(new FileTransferReceiveRecoveryRequest(
                    context.SessionId,
                    context.TransferId,
                    FileTransferDirection.Outbound,
                    "v6_sparse_runtime_peer_liveness_recovery"));
            }

            if (sparseRuntimeOutboundToSignal is not null)
            {
                SignalOutboundV4SenderPump(sparseRuntimeOutboundToSignal);
            }

            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}");
        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Peer disconnected.",
            notifyPeer: true,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToSignal)
    {
        outboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int remoteFrontier;
        int highestAcceptedChunk;
        long bytesTransferred;
        long bytesAcceptedForTransport;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !ShouldUseV6RegularNknSparseRuntime(context) ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0 ||
                context.PullV4SenderPumpRepairRequests.Count > 0;
            if (!hasUnacknowledgedOutboundData)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
                context.V4SenderPumpLastWakeReason = IsPrimaryRegularNknBulkV6ContextLocked(context)
                    ? "primary_regular_nkn_bulk_v6_checkpoint_liveness_recovery"
                    : "v6_sparse_runtime_peer_liveness_recovery";

            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            bytesTransferred = context.BytesTransferred;
            bytesAcceptedForTransport = context.BytesAcceptedForTransport;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            outboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; deferral_count={deferralCount}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}; bytes_transferred={bytesTransferred}; bytes_accepted_for_transport={bytesAcceptedForTransport}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToProbe)
    {
        outboundToProbe = null;
        DateTimeOffset now;
        int deferralCount;
        long transportEpoch;
        string epochState;
        string handoffKind;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(context.V6TransportEpoch))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.V4SenderPumpLastWakeReason = "peer_liveness_epoch_waiting";
            context.StatusMessage = GetV6TransportEpochStatus(context.V6TransportEpoch!);

            transportEpoch = context.V6TransportEpoch!.EpochId;
            epochState = FormatV6TransportEpochState(context.V6TransportEpoch.State);
            handoffKind = FormatFileTransferTransportHandoffKind(context.V6TransportEpoch.Kind);
            deferralCount = context.V6EpochLivenessDeferralCount;
            outboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; handoff_kind={FormatProtocolLogValue(handoffKind)}; target_transport=regular_nkn; deferral_count={deferralCount}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToSignal)
    {
        outboundToSignal = null;
        DateTimeOffset now;
        int recentChunkSends;
        int deferralCount;
        int remoteFrontier;
        int highestAcceptedChunk;
        int inFlightSendCount;
        int priorityRequestCount;
        int normalRequestCount;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;
            if (lastPeerLivenessUtc is not null &&
                peerLivenessSilence >= repairGrace)
            {
                return false;
            }

            var activityWindow = ResolveV6FallbackRecoveryLivenessTimeout();
            recentChunkSends = context.LastChunkSentUtc.Count(pair => now - pair.Value <= activityWindow);
            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0;
            if (recentChunkSends <= 0 && !hasUnacknowledgedOutboundData)
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V4SenderPumpLastWakeReason = "regular_nkn_feedback_repair";

            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            remoteFrontier = context.RemoteNextExpectedChunkIndex;
            highestAcceptedChunk = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
            inFlightSendCount = context.V6ChunkSendsInFlight.Count(pair => pair.Key >= context.RemoteNextExpectedChunkIndex);
            priorityRequestCount = context.V6PriorityRequestedChunks.Count;
            normalRequestCount = context.V6NormalRequestedChunks.Count;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            outboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_regular_nkn_feedback_repair; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; recent_chunk_sends={recentChunkSends}; deferral_count={deferralCount}; remote_frontier_chunk_index={remoteFrontier}; highest_accepted_chunk_index={highestAcceptedChunk}; in_flight_send_count={inFlightSendCount}; priority_request_count={priorityRequestCount}; normal_request_count={normalRequestCount}");
        return true;
    }

    private bool TryDeferOutboundV6PeerLivenessTimeoutForRecovery(
        OutboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out OutboundTransferContext? outboundToProbe)
    {
        outboundToProbe = null;
        DateTimeOffset now;
        int recentChunkSends;
        int deferralCount;
        long transportEpoch;
        string epochState;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                context.V6PeerLivenessRecoveryDeferralCount > 0)
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var activityWindow = ResolveV6FallbackRecoveryLivenessTimeout();
            recentChunkSends = context.LastChunkSentUtc.Count(pair => now - pair.Value <= activityWindow);
            var hasUnacknowledgedOutboundData =
                context.ChunksAcceptedForTransport > context.RemoteNextExpectedChunkIndex ||
                context.BytesAcceptedForTransport > context.BytesTransferred ||
                context.SentAwaitingAck.Count > 0 ||
                context.V6ChunkSendsInFlight.Count > 0;
            if (recentChunkSends <= 0 && !hasUnacknowledgedOutboundData)
            {
                return false;
            }

            context.V6PeerLivenessRecoveryDeferralCount++;
            context.V6PeerLivenessRecoveryDeferredUtc = now;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.PullTransportRebindGeneration++;
            context.PullTransportRebindStartedUtc = now;
            context.PullTransportSafetyReplayRearmCount = 0;
            context.PullTransportFrontierOnlyRepairActive = false;
            context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
            context.V4SenderPumpLastWakeReason = "peer_liveness_recovery";
            StartOutboundV6TransportEpochLocked(
                context,
                "peer_liveness_stale_receive_recovery",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            transportEpoch = context.V6TransportEpoch?.EpochId ?? 0;
            epochState = context.V6TransportEpoch is null ? "none" : FormatV6TransportEpochState(context.V6TransportEpoch.State);
            deferralCount = context.V6PeerLivenessRecoveryDeferralCount;
            outboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_recovery; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; recent_chunk_sends={recentChunkSends}; deferral_count={deferralCount}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_recovery_waiting_for_receiver_requests; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={transportEpoch}; reason=peer_liveness_stale_receive_recovery");
        return true;
    }

    private async Task<bool> TerminalizeInboundForPeerLivenessTimeoutAsync(InboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
        if (TryDeferInboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToProbe))
        {
            if (inboundToProbe is not null)
            {
                await AnnounceAndProbeInboundV6TransportEpochAsync(inboundToProbe).ConfigureAwait(false);
            }

            return false;
        }

        if (TryDeferInboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToSignal))
        {
            if (inboundToSignal is not null)
            {
                var reason = "v6_sparse_runtime_peer_liveness_recovery";
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, inboundToSignal) &&
                        !inboundToSignal.IsTerminal &&
                        IsPrimaryRegularNknBulkV6ContextLocked(inboundToSignal))
                    {
                        reason = V6RegularNknCheckpointSyncRecoveryMode;
                    }
                }

                await SendInboundV4StateAsync(
                        inboundToSignal,
                        reason,
                        terminalReady: false,
                        forceSend: true)
                    .ConfigureAwait(false);
            }

            return false;
        }

        if (TryDeferInboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
                context,
                lastPeerLivenessUtc,
                peerLivenessTimeout,
                out var inboundToRefresh))
        {
            if (inboundToRefresh is not null)
            {
                await SendInboundV6ReceiverStateAsync(inboundToRefresh, "regular_nkn_peer_liveness_feedback_repair", forceSend: true).ConfigureAwait(false);
                await SendInboundV6FrontierRequestAsync(inboundToRefresh, "regular_nkn_peer_liveness_feedback_repair", forceSend: true).ConfigureAwait(false);
            }

            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Peer disconnected.",
            sendError: true,
            errorMessage: "Peer disconnected.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForSparseRuntimePrimaryRegularNkn(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToSignal)
    {
        inboundToSignal = null;
        DateTimeOffset now;
        int deferralCount;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        long bytesTransferred;
        int sparsePendingWriteCount;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !ShouldUseV6RegularNknSparseRuntime(context) ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NextChunkIndex >= context.ChunkCount ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;

            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            bytesTransferred = context.BytesTransferred;
            sparsePendingWriteCount = context.ReceiverSparseChunksPendingWrite.Count;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            inboundToSignal = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; deferral_count={deferralCount}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; bytes_transferred={bytesTransferred}; sparse_pending_write_count={sparsePendingWriteCount}");
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToProbe)
    {
        inboundToProbe = null;
        DateTimeOffset now;
        int deferralCount;
        long transportEpoch;
        string epochState;
        string handoffKind;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                !ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(context.V6TransportEpoch))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.PullTransportResumeRequestPending = true;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;
            context.StatusMessage = GetV6TransportEpochStatus(context.V6TransportEpoch!);

            transportEpoch = context.V6TransportEpoch!.EpochId;
            epochState = FormatV6TransportEpochState(context.V6TransportEpoch.State);
            handoffKind = FormatFileTransferTransportHandoffKind(context.V6TransportEpoch.Kind);
            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            inboundToProbe = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; transport_epoch={transportEpoch}; epoch_state={FormatProtocolLogValue(epochState)}; handoff_kind={FormatProtocolLogValue(handoffKind)}; target_transport=regular_nkn; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; deferral_count={deferralCount}");
        return true;
    }

    private bool TryDeferInboundV6PeerLivenessTimeoutForRegularNknFeedbackRepair(
        InboundTransferContext context,
        DateTimeOffset? lastPeerLivenessUtc,
        TimeSpan peerLivenessTimeout,
        out InboundTransferContext? inboundToRefresh)
    {
        inboundToRefresh = null;
        DateTimeOffset now;
        int deferralCount;
        int committedChunkIndex;
        int highestObservedChunkIndex;
        long bytesTransferred;
        long peerLivenessSilenceMs;
        long repairGraceMs;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsNegotiableDataProtocolVersion(context.NegotiatedDataProtocolVersion) ||
                context.UserPaused ||
                context.PeerPaused ||
                context.PullTransportPaused ||
                !context.PullSessionActive ||
                !context.PullManifestReceived ||
                context.NextChunkIndex >= context.ChunkCount ||
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) ||
                !IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(context))
            {
                return false;
            }

            now = DateTimeOffset.UtcNow;
            var repairGrace = ResolveV6RegularNknPeerLivenessRepairGrace(peerLivenessTimeout);
            var peerLivenessSilence = lastPeerLivenessUtc is null
                ? TimeSpan.Zero
                : now - lastPeerLivenessUtc.Value;
            if (lastPeerLivenessUtc is not null &&
                peerLivenessSilence >= repairGrace)
            {
                return false;
            }

            context.V6EpochLivenessDeferralCount++;
            context.V6EpochLivenessDeferralUtc = now;
            context.V6LastReceiverStateSentUtc = null;
            context.V6LastFrontierRequestSentUtc = null;

            deferralCount = context.V6EpochLivenessDeferralCount;
            committedChunkIndex = context.NextChunkIndex;
            highestObservedChunkIndex = context.PullHighestReceivedChunkIndex;
            bytesTransferred = context.BytesTransferred;
            peerLivenessSilenceMs = lastPeerLivenessUtc is null ? -1 : (long)Math.Max(0, peerLivenessSilence.TotalMilliseconds);
            repairGraceMs = (long)Math.Max(0, repairGrace.TotalMilliseconds);
            inboundToRefresh = context;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_v6_heartbeat_timeout_deferred_for_regular_nkn_feedback_repair; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; last_peer_liveness_utc={FormatProtocolLogValue(lastPeerLivenessUtc?.ToString("O"))}; timeout_ms={(long)peerLivenessTimeout.TotalMilliseconds}; peer_liveness_silence_ms={peerLivenessSilenceMs}; repair_grace_ms={repairGraceMs}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}; bytes_transferred={bytesTransferred}; deferral_count={deferralCount}");
        return true;
    }

    private static bool ShouldDeferV6PeerLivenessTimeoutForUnresolvedRegularNknEpoch(V6TransportEpoch? epoch)
        => IsV6TransportEpochUnresolved(epoch) &&
           epoch is { TargetTransport: FileTransferTransportKind.RegularNkn };

    private static bool IsOutboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(OutboundTransferContext context)
        => IsOutboundV6RegularNknPrimaryPathLocked(context) &&
           !context.PullPostTunaRecoveryActive &&
           context.LastRecoveredV6TransportTargetTransport != FileTransferTransportKind.Tuna &&
           context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.TunaToNormalFallback;

    private static bool IsInboundV6PrimaryRegularNknWithoutTunaRecoveryLocked(InboundTransferContext context)
        => context.V6TransportEpoch is not { TargetTransport: FileTransferTransportKind.Tuna } &&
           !context.PullPostTunaRecoveryActive &&
           context.LastRecoveredV6TransportTargetTransport != FileTransferTransportKind.Tuna &&
           context.LastRecoveredV6TransportEpochKind != FileTransferTransportHandoffKind.TunaToNormalFallback;

    private void TouchOutboundV6PeerLiveness(OutboundTransferContext context, string reason)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            context.V6LastPeerLivenessUtc = now;
            context.PullV4LastPeerFrameReceivedUtc = now;
            context.V6EpochLivenessDeferralCount = 0;
            context.V6EpochLivenessDeferralUtc = null;
            context.V6PeerLivenessRecoveryDeferralCount = 0;
            context.V6PeerLivenessRecoveryDeferredUtc = null;
        }
    }

    private void TouchOutboundV6PeerLivenessIfAuthoritative(OutboundTransferContext context, FileTransferReceivedDataFrame received, string reason)
    {
        if (IsAuthoritativeV6PeerLivenessFrame(received))
        {
            TouchOutboundV6PeerLiveness(context, reason);
        }
    }

    private void TouchInboundV6PeerLiveness(InboundTransferContext context, string reason)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            context.V6LastPeerLivenessUtc = now;
            context.PullV4LastPeerFrameReceivedUtc = now;
            context.V6EpochLivenessDeferralCount = 0;
            context.V6EpochLivenessDeferralUtc = null;
        }
    }

    private void TouchInboundV6PeerLivenessIfAuthoritative(InboundTransferContext context, FileTransferReceivedDataFrame received, string reason)
    {
        if (IsAuthoritativeV6PeerLivenessFrame(received))
        {
            TouchInboundV6PeerLiveness(context, reason);
        }
    }

    private static bool IsAuthoritativeV6PeerLivenessFrame(FileTransferReceivedDataFrame received)
        // Source/session/replay checks already ran before this point. Transport origin is still strict for
        // epoch proof, but peer liveness should survive redundant feedback being stamped as Tuna/unknown.
        => FileTransferProtocol.IsV6DataFrame(received.Frame) ||
           received.Frame is FileTransferStateFrameV4;
}
