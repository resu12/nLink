using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static TimeSpan CurrentV6HeartbeatInterval
        => V6HeartbeatIntervalOverrideForTests ?? TimeSpan.FromMilliseconds(V6HeartbeatIntervalMs);

    private static TimeSpan CurrentV6PeerLivenessTimeout
        => V6PeerLivenessTimeoutOverrideForTests ?? TimeSpan.FromMilliseconds(V6PeerLivenessTimeoutMs);

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

    private static bool ShouldDeferV6PeerLivenessTimeout(V6TransportEpoch? epoch)
        => epoch is not null &&
           epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
           epoch.State == V6TransportEpochState.WaitingForTargetTransport;

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
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    peerLivenessTimeout = ResolveOutboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    timedOut = lastPeerLivenessUtc is not null &&
                        now - lastPeerLivenessUtc.Value >= peerLivenessTimeout &&
                        !ShouldDeferV6PeerLivenessTimeout(context.V6TransportEpoch);
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
                    await TerminalizeOutboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    return;
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
                TimeSpan peerLivenessTimeout;
                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    peerLivenessTimeout = ResolveInboundV6PeerLivenessTimeout(context);
                    lastPeerLivenessUtc = context.V6LastPeerLivenessUtc;
                    timedOut = lastPeerLivenessUtc is not null &&
                        now - lastPeerLivenessUtc.Value >= peerLivenessTimeout &&
                        !ShouldDeferV6PeerLivenessTimeout(context.V6TransportEpoch);
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
                    await TerminalizeInboundForPeerLivenessTimeoutAsync(context, lastPeerLivenessUtc, peerLivenessTimeout).ConfigureAwait(false);
                    return;
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

    private async Task TerminalizeOutboundForPeerLivenessTimeoutAsync(OutboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
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
    }

    private async Task TerminalizeInboundForPeerLivenessTimeoutAsync(InboundTransferContext context, DateTimeOffset? lastPeerLivenessUtc, TimeSpan peerLivenessTimeout)
    {
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
    }

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
        }
    }
}
