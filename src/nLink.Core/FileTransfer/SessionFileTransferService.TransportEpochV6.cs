using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private const string RuntimeUnlockPreCommitProbeAcceptedReason = "precommit_probe_ack";
    private const string RuntimeUnlockPreCommitProbeReceivedReason = "precommit_probe_received";
    private const string RuntimeUnlockPreCommitProbeCommitConfirmedReason = "precommit_probe_commit_confirmed";
    private const string RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason = "precommit_probe_commit_confirmed_reply";
    private const string RuntimeUnlockPreCommitProbeCommitConfirmedReplySentReason = "precommit_probe_commit_confirmed_reply_sent";

    private static TimeSpan ResolveV6TransportEpochProofTimeout()
        => V6TransportEpochProofTimeoutOverrideForTests ?? V6TransportEpochProofTimeout;

    private static TimeSpan ResolveV6TransportProbeAckSendTimeout()
        => V6TransportProbeAckSendTimeoutOverrideForTests ?? V6TransportProbeAckSendTimeout;

    private static bool IsV6TransportEpochUnresolved(V6TransportEpoch? epoch)
        => epoch is not null &&
           epoch.State is not V6TransportEpochState.Recovered and not V6TransportEpochState.Terminal;

    private static bool IsRuntimeUnlockTunaPathProbeEpoch(V6TransportEpoch? epoch)
        => epoch is
        {
            Kind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TargetTransport: FileTransferTransportKind.Tuna,
        };

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
            FileTransferTransportHandoffKind.RegularNknRecovery => FileTransferTransportKind.RegularNkn,
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
        FileTransferLeg? currentLeg,
        V6TransportEpoch current,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        if (IsCurrentPostTunaFallbackLeg(currentLeg) &&
            currentLeg!.TransportEpochId != current.EpochId)
        {
            return false;
        }

        var normalizedKind = NormalizeV6TransportHandoffKind(handoffKind);
        if (current.TargetTransport != targetTransport)
        {
            return false;
        }

        if (current.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            normalizedKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
            targetTransport == FileTransferTransportKind.RegularNkn)
        {
            return true;
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

    private void NotifyRuntimeUnlockPathProbeStarted(
        string sessionId,
        string transferId,
        V6TransportEpoch epoch,
        string reason)
    {
        if (!IsRuntimeUnlockTunaPathProbeEpoch(epoch) ||
            transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            string.IsNullOrWhiteSpace(epoch.ProbeId))
        {
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    epoch.Direction,
                    sessionId,
                    transferId,
                    runtimeOwner: null,
                    actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                    source: "runtime_unlock_transport_epoch_probe",
                    attemptedAction: reason,
                    admissionToken: null,
                    routeCommitToken: null,
                    transactionGeneration: null,
                    offerGeneration: null,
                    tunaPathLeaseGeneration: null,
                    tunaPathLeaseListenerRunId: null,
                    probeId: epoch.ProbeId,
                    probeAcked: false,
                    samePathTunaProbeAckId: null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _))
            {
                return;
            }
        }

        proofProvider.NotifyRuntimeUnlockPathProbeStarted(
            sessionId,
            transferId,
            epoch.EpochId,
            epoch.ProbeId,
            epoch.TargetTransport,
            reason);
    }

    private void NotifyRuntimeUnlockPathProbeResult(
        string sessionId,
        string transferId,
        V6TransportEpoch epoch,
        bool acked,
        string reason)
    {
        if (!IsRuntimeUnlockTunaPathProbeEpoch(epoch) ||
            transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            string.IsNullOrWhiteSpace(epoch.ProbeId))
        {
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    epoch.Direction,
                    sessionId,
                    transferId,
                    runtimeOwner: null,
                    actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                    source: "runtime_unlock_transport_epoch_probe",
                    attemptedAction: reason,
                    admissionToken: null,
                    routeCommitToken: null,
                    transactionGeneration: null,
                    offerGeneration: null,
                    tunaPathLeaseGeneration: null,
                    tunaPathLeaseListenerRunId: null,
                    probeId: epoch.ProbeId,
                    probeAcked: acked,
                    samePathTunaProbeAckId: acked ? epoch.ProbeId : null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _))
            {
                return;
            }
        }

        proofProvider.NotifyRuntimeUnlockPathProbeResult(
            sessionId,
            transferId,
            epoch.EpochId,
            epoch.ProbeId,
            epoch.TargetTransport,
            acked,
            reason);
    }

    private void StartOutboundRuntimeUnlockPreCommitProbe(
        OutboundTransferContext context,
        RuntimeUnlockAdmissionToken admissionToken,
        string reason,
        RuntimeUnlockPendingPreCommitProbeRequest? pendingReplay = null,
        RuntimeUnlockRouteCommitSnapshot? acceptedSnapshot = null)
    {
        if (!FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                admissionToken,
                out var admissionRejectionReason))
        {
            LogRuntimeUnlockAdmissionTokenRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                "runtime_unlock_precommit_probe",
                reason,
                admissionRejectionReason,
                admissionToken);
            return;
        }

        if (!TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                "runtime_unlock_precommit_probe",
                "start_probe",
                out var runtimeOwner))
        {
            return;
        }

        _ = RunRuntimeUnlockPreCommitProbeAsync(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            context.DataSession,
            runtimeOwner,
            admissionToken,
            pendingReplay,
            acceptedSnapshot,
            context.LifetimeCts.Token,
            reason);
    }

    private void StartInboundRuntimeUnlockPreCommitProbe(
        InboundTransferContext context,
        RuntimeUnlockAdmissionToken admissionToken,
        string reason,
        RuntimeUnlockPendingPreCommitProbeRequest? pendingReplay = null,
        RuntimeUnlockRouteCommitSnapshot? acceptedSnapshot = null)
    {
        if (!FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                admissionToken,
                out var admissionRejectionReason))
        {
            LogRuntimeUnlockAdmissionTokenRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                "runtime_unlock_precommit_probe",
                reason,
                admissionRejectionReason,
                admissionToken);
            return;
        }

        if (!TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                "runtime_unlock_precommit_probe",
                "start_probe",
                out var runtimeOwner))
        {
            return;
        }

        _ = RunRuntimeUnlockPreCommitProbeAsync(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            context.DataSession,
            runtimeOwner,
            admissionToken,
            pendingReplay,
            acceptedSnapshot,
            context.LifetimeCts.Token,
            reason);
    }

    private async Task RunRuntimeUnlockPreCommitProbeAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        IFileTransferDataSession? dataSession,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        RuntimeUnlockPendingPreCommitProbeRequest? pendingReplay,
        RuntimeUnlockRouteCommitSnapshot? acceptedSnapshot,
        CancellationToken ct,
        string reason)
    {
        lock (gate)
        {
            if (!TryAuthorizeRuntimeOwnerActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner,
                    FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                    "runtime_unlock_precommit_probe",
                    "start_probe",
                    out _))
            {
                return;
            }
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            if (dataSession is not null && IsFallbackTailQuiescentRuntimeUnlockResumeReason(reason))
            {
                LogRuntimeUnlockPreCommitProbeWaitingForTransportProof(
                    direction,
                    transferId,
                    sessionId,
                    reason);
                LogRuntimeUnlockPreCommitProbePendingHeld(
                    direction,
                    transferId,
                    sessionId,
                    runtimeOwner,
                    admissionToken,
                    reason,
                    "proof_provider_unavailable");
                return;
            }

            if (dataSession is null)
            {
                LogRuntimeUnlockPreCommitProbePendingRejected(
                    direction,
                    transferId,
                    sessionId,
                    pendingReplay,
                    runtimeOwner,
                    admissionToken,
                    transactionGeneration: 0,
                    offerGeneration: 0,
                    leaseGeneration: 0,
                    listenerRunId: null,
                    "proof_provider_unavailable");
                return;
            }

            LogRuntimeUnlockPreCommitProbeFailed(
                direction,
                transferId,
                sessionId,
                transactionGeneration: 0,
                offerGeneration: 0,
                leaseGeneration: 0,
                probeId: "(none)",
                "proof_provider_unavailable");
            return;
        }

        RuntimeUnlockRouteCommitSnapshot snapshot;
        if (acceptedSnapshot is { } suppliedSnapshot)
        {
            snapshot = suppliedSnapshot;
        }
        else if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out snapshot))
        {
            if (dataSession is not null && IsFallbackTailQuiescentRuntimeUnlockResumeReason(reason))
            {
                LogRuntimeUnlockPreCommitProbeWaitingForTransportProof(
                    direction,
                    transferId,
                    sessionId,
                    reason);
                LogRuntimeUnlockPreCommitProbePendingHeld(
                    direction,
                    transferId,
                    sessionId,
                    runtimeOwner,
                    admissionToken,
                    reason,
                    "transport_proof_missing");
                return;
            }

            if (dataSession is null)
            {
                LogRuntimeUnlockPreCommitProbePendingRejected(
                    direction,
                    transferId,
                    sessionId,
                    pendingReplay,
                    runtimeOwner,
                    admissionToken,
                    transactionGeneration: 0,
                    offerGeneration: 0,
                    leaseGeneration: 0,
                    listenerRunId: null,
                    "proof_provider_unavailable");
                return;
            }

            LogRuntimeUnlockPreCommitProbeFailed(
                direction,
                transferId,
                sessionId,
                transactionGeneration: 0,
                offerGeneration: 0,
                leaseGeneration: 0,
                probeId: "(none)",
                "proof_provider_unavailable");
            return;
        }

        if (!TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
                snapshot,
                sessionId,
                transferId,
                out var snapshotRejectionReason))
        {
            LogRuntimeUnlockPreCommitProbePendingRejected(
                direction,
                transferId,
                sessionId,
                pendingReplay,
                runtimeOwner,
                admissionToken,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                snapshot.TunaPathLeaseGeneration,
                snapshot.TunaPathLeaseListenerRunId,
                snapshotRejectionReason);
            return;
        }

        if (string.Equals(snapshot.PathProbeState, RuntimeUnlockPathProbeState.Started.ToString(), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(snapshot.PathProbeId))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_suppressed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={snapshot.TransactionGeneration}; offer_generation={snapshot.OfferGeneration}; probe_id={FormatProtocolLogValue(snapshot.PathProbeId)}; reason=probe_already_started");
            return;
        }

        if (!IsRuntimeUnlockPreCommitProbeSnapshotUsable(snapshot))
        {
            if (dataSession is null)
            {
                LogRuntimeUnlockPreCommitProbePendingRejected(
                    direction,
                    transferId,
                    sessionId,
                    pendingReplay,
                    runtimeOwner,
                    admissionToken,
                    snapshot.TransactionGeneration,
                    snapshot.OfferGeneration,
                    snapshot.TunaPathLeaseGeneration,
                    snapshot.TunaPathLeaseListenerRunId,
                    "snapshot_not_usable");
                return;
            }

            LogRuntimeUnlockPreCommitProbeWaitingForTransportProof(
                direction,
                transferId,
                sessionId,
                "snapshot_not_usable");
            if (IsFallbackTailQuiescentRuntimeUnlockResumeReason(reason))
            {
                LogRuntimeUnlockPreCommitProbePendingRejected(
                    direction,
                    transferId,
                    sessionId,
                    pendingReplay,
                    runtimeOwner,
                    admissionToken,
                    snapshot.TransactionGeneration,
                    snapshot.OfferGeneration,
                    snapshot.TunaPathLeaseGeneration,
                    snapshot.TunaPathLeaseListenerRunId,
                    "snapshot_not_usable");
            }
            return;
        }

        if (dataSession is null)
        {
            lock (gate)
            {
                TryRecordRuntimeUnlockPendingPreCommitProbeLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner,
                    admissionToken,
                    snapshot,
                    reason);
            }

            return;
        }

        if (dataSession is not IFileTransferRuntimeUnlockTunaProbeSender tunaProbeSender)
        {
            LogRuntimeUnlockPreCommitProbeFailed(
                direction,
                transferId,
                sessionId,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                snapshot.TunaPathLeaseGeneration,
                snapshot.PathProbeId ?? "(none)",
                "runtime_unlock_tuna_probe_sender_unavailable");
            return;
        }

        if (pendingReplay is { } expectedPending &&
            !RuntimeUnlockPreCommitProbeSnapshotMatchesPending(snapshot, expectedPending, out var pendingMismatchReason))
        {
            LogRuntimeUnlockPreCommitProbePendingRejected(
                direction,
                transferId,
                sessionId,
                expectedPending,
                runtimeOwner,
                admissionToken,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                snapshot.TunaPathLeaseGeneration,
                snapshot.TunaPathLeaseListenerRunId,
                pendingMismatchReason);
            return;
        }

        var probeId = $"runtime-unlock-precommit:{snapshot.TransactionGeneration}:{snapshot.OfferGeneration}:{Guid.NewGuid():N}";
        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner,
                    FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                    "runtime_unlock_precommit_probe",
                    "start_probe",
                    admissionToken,
                    routeCommitToken: null,
                    transactionGeneration: snapshot.TransactionGeneration,
                    offerGeneration: snapshot.OfferGeneration,
                    tunaPathLeaseGeneration: snapshot.TunaPathLeaseGeneration,
                    tunaPathLeaseListenerRunId: snapshot.TunaPathLeaseListenerRunId,
                    probeId: probeId,
                    probeAcked: false,
                    samePathTunaProbeAckId: null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _))
            {
                return;
            }
        }

        var frame = new FileTransferRuntimeUnlockPreCommitProbeFrame
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransactionGeneration = snapshot.TransactionGeneration,
            OfferGeneration = snapshot.OfferGeneration,
            TunaPathLeaseGeneration = snapshot.TunaPathLeaseGeneration,
            ProbeId = probeId,
            TargetRoute = FileTransferRouteResolver.FileTunaV4Token,
            TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            TargetTransport = FormatFileTransferTransportKind(FileTransferTransportKind.Tuna),
            HandoffKind = FormatFileTransferTransportHandoffKind(FileTransferTransportHandoffKind.NormalToTunaActivation),
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        proofProvider.NotifyRuntimeUnlockPathProbeStarted(
            sessionId,
            transferId,
            transportEpoch: 0,
            probeId,
            FileTransferTransportKind.Tuna,
            "precommit_probe_sent");
        LogRuntimeUnlockPreCommitProbeStarted(direction, transferId, sessionId, frame);

        try
        {
            await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAsync(frame, ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={frame.TransactionGeneration}; offer_generation={frame.OfferGeneration}; tuna_path_lease_generation={frame.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(probeId)}; target_transport=tuna; reason={FormatProtocolLogValue(reason)}");
            _ = RunRuntimeUnlockPreCommitProbeTimeoutAsync(
                proofProvider,
                sessionId,
                transferId,
                direction,
                runtimeOwner,
                admissionToken,
                frame.TransactionGeneration,
                frame.OfferGeneration,
                frame.TunaPathLeaseGeneration,
                probeId,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (gate)
            {
                if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                        direction,
                        sessionId,
                        transferId,
                        runtimeOwner,
                        FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                        "runtime_unlock_precommit_probe",
                        "probe_send_failed",
                        admissionToken,
                        routeCommitToken: null,
                        transactionGeneration: frame.TransactionGeneration,
                        offerGeneration: frame.OfferGeneration,
                        tunaPathLeaseGeneration: frame.TunaPathLeaseGeneration,
                        tunaPathLeaseListenerRunId: snapshot.TunaPathLeaseListenerRunId,
                        probeId: probeId,
                        probeAcked: false,
                        samePathTunaProbeAckId: null,
                        routeCommitGeneration: null,
                        targetRoute: FileTransferRoute.FileTunaV4,
                        targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                        targetTransport: FileTransferTransportKind.Tuna,
                        out _))
                {
                    return;
                }
            }

            proofProvider.NotifyRuntimeUnlockPathProbeResult(
                sessionId,
                transferId,
                transportEpoch: 0,
                probeId,
                FileTransferTransportKind.Tuna,
                acked: false,
                "precommit_probe_send_failed");
            LogRuntimeUnlockPreCommitProbeFailed(
                direction,
                transferId,
                sessionId,
                frame.TransactionGeneration,
                frame.OfferGeneration,
                frame.TunaPathLeaseGeneration,
                probeId,
                ex.GetType().Name);
            MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                direction,
                sessionId,
                transferId,
                ex.GetType().Name);
        }
    }

    private async Task RunRuntimeUnlockPreCommitProbeTimeoutAsync(
        IRuntimeUnlockRouteCommitProofProvider proofProvider,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(ResolveV6TransportEpochProofTimeout(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeOwnerActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner,
                    FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                    "runtime_unlock_precommit_probe_timeout",
                    "fail_probe",
                    out _))
            {
                return;
            }
        }

        if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out var snapshot) ||
            snapshot.TransactionGeneration != transactionGeneration ||
            snapshot.OfferGeneration != offerGeneration ||
            snapshot.TunaPathLeaseGeneration != leaseGeneration ||
            !string.Equals(snapshot.PathProbeId, probeId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.PathProbeState, RuntimeUnlockPathProbeState.Started.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner,
                    FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                    "runtime_unlock_precommit_probe_timeout",
                    "probe_timeout",
                    admissionToken,
                    routeCommitToken: null,
                    transactionGeneration: transactionGeneration,
                    offerGeneration: offerGeneration,
                    tunaPathLeaseGeneration: leaseGeneration,
                    tunaPathLeaseListenerRunId: snapshot.TunaPathLeaseListenerRunId,
                    probeId: probeId,
                    probeAcked: false,
                    samePathTunaProbeAckId: null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _))
            {
                return;
            }
        }

        proofProvider.NotifyRuntimeUnlockPathProbeResult(
            sessionId,
            transferId,
            transportEpoch: 0,
            probeId,
            FileTransferTransportKind.Tuna,
            acked: false,
            "precommit_probe_timeout");
        LogRuntimeUnlockPreCommitProbeFailed(
            direction,
            transferId,
            sessionId,
            transactionGeneration,
            offerGeneration,
            leaseGeneration,
            probeId,
            "timeout");
        MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
            direction,
            sessionId,
            transferId,
            "timeout");
    }

    private static bool IsRuntimeUnlockPreCommitProbeSnapshotUsable(RuntimeUnlockRouteCommitSnapshot snapshot)
    {
        if (!snapshot.PeerVisibleProof)
        {
            return false;
        }

        if (!snapshot.TunaPathLeaseRequired)
        {
            return snapshot.PeerReceived && snapshot.AnswerReceived;
        }

        return snapshot.PeerReceived &&
               snapshot.AnswerReceived &&
               snapshot.TunaPathLeaseCurrent &&
               snapshot.TunaPathLeaseGeneration > 0 &&
               string.Equals(snapshot.TunaPathLeaseState, RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string sessionId,
        string transferId,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (!string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(snapshot.TransferId) &&
             !string.Equals(snapshot.TransferId, transferId, StringComparison.Ordinal)))
        {
            rejectionReason = "session_or_transfer_mismatch";
            return false;
        }

        if (snapshot.TargetRoute != FileTransferRoute.FileTunaV4 ||
            snapshot.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            snapshot.TargetTransport != FileTransferTransportKind.Tuna ||
            snapshot.HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        return true;
    }

    private static bool RuntimeUnlockPreCommitProbeSnapshotMatchesPending(
        RuntimeUnlockRouteCommitSnapshot snapshot,
        RuntimeUnlockPendingPreCommitProbeRequest pending,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (snapshot.TransactionGeneration != pending.TransactionGeneration)
        {
            rejectionReason = "runtime_unlock_transaction_generation_mismatch";
            return false;
        }

        if (snapshot.OfferGeneration != pending.OfferGeneration)
        {
            rejectionReason = "runtime_unlock_offer_generation_mismatch";
            return false;
        }

        if (snapshot.TunaPathLeaseGeneration != pending.TunaPathLeaseGeneration)
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_generation_mismatch";
            return false;
        }

        if (!string.Equals(snapshot.TunaPathLeaseListenerRunId, pending.TunaPathLeaseListenerRunId, StringComparison.Ordinal))
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_listener_mismatch";
            return false;
        }

        if (snapshot.TargetRoute != pending.TargetRoute ||
            snapshot.ProtocolVersion != pending.TargetProtocolVersion ||
            snapshot.TargetTransport != pending.TargetTransport ||
            snapshot.HandoffKind != pending.HandoffKind)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        return true;
    }

    private bool TryRecordRuntimeUnlockPendingPreCommitProbeLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason)
    {
        if (direction == FileTransferDirection.Outbound &&
            IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            outboundTransfer is { IsTerminal: false } outbound)
        {
            var pending = CreateRuntimeUnlockPendingPreCommitProbeRequest(
                outbound,
                runtimeOwner,
                admissionToken,
                snapshot,
                reason);
            var decision = FileTransferCoordinator.Apply(
                RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending,
                    outbound.RouteSelection,
                    outbound.CurrentTransferLeg,
                    reason,
                    pending),
                CreateCoordinatorStateLocked(outbound));
            ApplyCoordinatorDecisionToContextLocked(outbound, decision);
            return LogRuntimeUnlockPendingPreCommitProbeDecision(
                direction,
                transferId,
                sessionId,
                "runtime_unlock_precommit_probe",
                reason,
                decision.RuntimeUnlockPendingPreCommitProbeDecision,
                pending);
        }

        if (direction == FileTransferDirection.Inbound &&
            IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            inboundTransfer is { IsTerminal: false } inbound)
        {
            var pending = CreateRuntimeUnlockPendingPreCommitProbeRequest(
                inbound,
                runtimeOwner,
                admissionToken,
                snapshot,
                reason);
            var decision = FileTransferCoordinator.Apply(
                RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending,
                    inbound.RouteSelection,
                    inbound.CurrentTransferLeg,
                    reason,
                    pending),
                CreateCoordinatorStateLocked(inbound));
            ApplyCoordinatorDecisionToContextLocked(inbound, decision);
            return LogRuntimeUnlockPendingPreCommitProbeDecision(
                direction,
                transferId,
                sessionId,
                "runtime_unlock_precommit_probe",
                reason,
                decision.RuntimeUnlockPendingPreCommitProbeDecision,
                pending);
        }

        LogRuntimeUnlockPreCommitProbePendingRejected(
            direction,
            transferId,
            sessionId,
            pending: null,
            runtimeOwner,
            admissionToken,
            snapshot.TransactionGeneration,
            snapshot.OfferGeneration,
            snapshot.TunaPathLeaseGeneration,
            snapshot.TunaPathLeaseListenerRunId,
            "transfer_not_current");
        return false;
    }

    private static RuntimeUnlockPendingPreCommitProbeRequest CreateRuntimeUnlockPendingPreCommitProbeRequest(
        OutboundTransferContext context,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason)
        => new(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            runtimeOwner,
            admissionToken,
            context.RouteSelection.Route,
            context.RouteSelection.TelemetryToken,
            context.RouteSelection.ProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            runtimeOwner.RuntimeGeneration,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            snapshot.TransactionGeneration,
            snapshot.OfferGeneration,
            snapshot.TunaPathLeaseGeneration,
            snapshot.TunaPathLeaseListenerRunId,
            snapshot.TargetRoute,
            snapshot.ProtocolVersion,
            snapshot.TargetTransport,
            snapshot.HandoffKind,
            PendingGeneration: 0,
            NormalizeReason(reason) ?? "runtime_unlock_precommit_probe");

    private static RuntimeUnlockPendingPreCommitProbeRequest CreateRuntimeUnlockPendingPreCommitProbeRequest(
        InboundTransferContext context,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string reason)
        => new(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            runtimeOwner,
            admissionToken,
            context.RouteSelection.Route,
            context.RouteSelection.TelemetryToken,
            context.RouteSelection.ProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            runtimeOwner.RuntimeGeneration,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            snapshot.TransactionGeneration,
            snapshot.OfferGeneration,
            snapshot.TunaPathLeaseGeneration,
            snapshot.TunaPathLeaseListenerRunId,
            snapshot.TargetRoute,
            snapshot.ProtocolVersion,
            snapshot.TargetTransport,
            snapshot.HandoffKind,
            PendingGeneration: 0,
            NormalizeReason(reason) ?? "runtime_unlock_precommit_probe");

    private static FileTransferCoordinatorEvent RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
        FileTransferCoordinatorEventKind kind,
        FileTransferRouteSelection routeSelection,
        FileTransferLeg? leg,
        string reason,
        RuntimeUnlockPendingPreCommitProbeRequest pending)
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
            RuntimeUnlockPendingPreCommitProbeRequest: pending);

    private void TryReplayOutboundRuntimeUnlockPendingPreCommitProbeLocked(
        OutboundTransferContext context,
        string reason)
    {
        if (context.DataSession is null ||
            context.RuntimeUnlockPendingPreCommitProbe is not { } pending)
        {
            return;
        }

        var state = CreateCoordinatorStateLocked(context);
        var decision = FileTransferCoordinator.AuthorizeAction(
            state,
            RuntimeUnlockPendingPreCommitProbeReplayAction(pending, reason));
        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        if (!decision.Accepted)
        {
            LogRuntimeUnlockPreCommitProbePendingRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                pending,
                pending.RuntimeOwner,
                pending.AdmissionToken,
                pending.TransactionGeneration,
                pending.OfferGeneration,
                pending.TunaPathLeaseGeneration,
                pending.TunaPathLeaseListenerRunId,
                decision.RejectionReason);
            return;
        }

        var replayed = FileTransferCoordinator.Apply(
            RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeReplayed,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                pending),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, replayed);
        if (replayed.RuntimeUnlockPendingPreCommitProbeDecision?.Accepted != true)
        {
            LogRuntimeUnlockPendingPreCommitProbeDecision(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                "runtime_unlock_precommit_probe_replay",
                reason,
                replayed.RuntimeUnlockPendingPreCommitProbeDecision,
                pending);
            return;
        }

        var retired = FileTransferCoordinator.Apply(
            RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                pending),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, retired);
        LogRuntimeUnlockPreCommitProbeReplayed(FileTransferDirection.Outbound, context.TransferId, context.SessionId, pending, reason);
        StartOutboundRuntimeUnlockPreCommitProbe(context, pending.AdmissionToken, reason, pending);
    }

    private void TryReplayInboundRuntimeUnlockPendingPreCommitProbeLocked(
        InboundTransferContext context,
        string reason)
    {
        if (context.DataSession is null ||
            context.RuntimeUnlockPendingPreCommitProbe is not { } pending)
        {
            return;
        }

        var state = CreateCoordinatorStateLocked(context);
        var decision = FileTransferCoordinator.AuthorizeAction(
            state,
            RuntimeUnlockPendingPreCommitProbeReplayAction(pending, reason));
        ApplyCoordinatorActionDecisionToContextLocked(context, decision);
        if (!decision.Accepted)
        {
            LogRuntimeUnlockPreCommitProbePendingRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                pending,
                pending.RuntimeOwner,
                pending.AdmissionToken,
                pending.TransactionGeneration,
                pending.OfferGeneration,
                pending.TunaPathLeaseGeneration,
                pending.TunaPathLeaseListenerRunId,
                decision.RejectionReason);
            return;
        }

        var replayed = FileTransferCoordinator.Apply(
            RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeReplayed,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                pending),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, replayed);
        if (replayed.RuntimeUnlockPendingPreCommitProbeDecision?.Accepted != true)
        {
            LogRuntimeUnlockPendingPreCommitProbeDecision(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                "runtime_unlock_precommit_probe_replay",
                reason,
                replayed.RuntimeUnlockPendingPreCommitProbeDecision,
                pending);
            return;
        }

        var retired = FileTransferCoordinator.Apply(
            RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
                context.RouteSelection,
                context.CurrentTransferLeg,
                reason,
                pending),
            CreateCoordinatorStateLocked(context));
        ApplyCoordinatorDecisionToContextLocked(context, retired);
        LogRuntimeUnlockPreCommitProbeReplayed(FileTransferDirection.Inbound, context.TransferId, context.SessionId, pending, reason);
        StartInboundRuntimeUnlockPreCommitProbe(context, pending.AdmissionToken, reason, pending);
    }

    private static FileTransferCoordinatorActionRequest RuntimeUnlockPendingPreCommitProbeReplayAction(
        RuntimeUnlockPendingPreCommitProbeRequest pending,
        string reason)
        => new(
            FileTransferCoordinatorActionKind.RuntimeUnlockPendingPreCommitProbeReplay,
            "runtime_unlock_precommit_probe_pending",
            reason,
            RuntimeOwner: pending.RuntimeOwner,
            Route: pending.Route,
            ProtocolVersion: pending.ProtocolVersion,
            LiveRouteEpoch: pending.LiveRouteEpoch,
            LegGeneration: pending.LegGeneration,
            RuntimeUnlockAdmissionToken: pending.AdmissionToken,
            RuntimeUnlockTransactionGeneration: pending.TransactionGeneration,
            RuntimeUnlockOfferGeneration: pending.OfferGeneration,
            RuntimeUnlockTunaPathLeaseGeneration: pending.TunaPathLeaseGeneration,
            RuntimeUnlockTunaPathLeaseListenerRunId: pending.TunaPathLeaseListenerRunId,
            TargetRoute: pending.TargetRoute,
            TargetProtocolVersion: pending.TargetProtocolVersion,
            TargetTransport: pending.TargetTransport,
            RuntimeGeneration: pending.RuntimeGeneration,
            TerminalGeneration: pending.TerminalGeneration,
            RuntimeUnlockPendingPreCommitProbeRequest: pending);

    private async Task<bool> TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferDataFrame frame,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession)
    {
        switch (frame)
        {
            case FileTransferRuntimeUnlockPreCommitProbeFrame probe:
                await HandleRuntimeUnlockPreCommitProbeAsync(
                        sessionId,
                        transferId,
                        direction,
                        probe,
                        receivedTransportKind,
                        dataSession)
                    .ConfigureAwait(false);
                return true;
            case FileTransferRuntimeUnlockPreCommitProbeAckFrame ack:
                await HandleRuntimeUnlockPreCommitProbeAckAsync(
                        sessionId,
                        transferId,
                        direction,
                        ack,
                        receivedTransportKind,
                        dataSession)
                    .ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    private async Task HandleRuntimeUnlockPreCommitProbeAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession)
    {
        if (!TryValidateRuntimeUnlockPreCommitProbeFrame(
                sessionId,
                transferId,
                direction,
                probe,
                receivedTransportKind,
                expectAck: false,
                out var reason))
        {
            LogRuntimeUnlockPreCommitProbeIgnored(direction, transferId, sessionId, probe, receivedTransportKind, reason);
            return;
        }

        if (dataSession is null)
        {
            LogRuntimeUnlockPreCommitProbeIgnored(direction, transferId, sessionId, probe, receivedTransportKind, "data_session_unavailable");
            return;
        }

        if (dataSession is not IFileTransferRuntimeUnlockTunaProbeSender tunaProbeSender)
        {
            LogRuntimeUnlockPreCommitProbeIgnored(direction, transferId, sessionId, probe, receivedTransportKind, "runtime_unlock_tuna_probe_sender_unavailable");
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_tuna_probe_received; transport=tuna; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={probe.TransactionGeneration}; offer_generation={probe.OfferGeneration}; tuna_path_lease_generation={probe.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(probe.ProbeId ?? "(none)")}; target_transport={FormatProtocolLogValue(probe.TargetTransport)}");

        var accepted = true;
        var ackReason = RuntimeUnlockPreCommitProbeReceivedReason;
        RuntimeUnlockAdmissionToken? currentAdmissionToken = null;
        var admissionStatus = EnsureRuntimeUnlockPreCommitProbeAdmissionLocked(
            direction,
            sessionId,
            transferId,
            out var admissionRejectionReason,
            out currentAdmissionToken);
        if (admissionStatus == RuntimeUnlockPreCommitProbeAdmissionStatus.Rejected)
        {
            accepted = false;
            ackReason = admissionRejectionReason;
        }
        else if (admissionStatus != RuntimeUnlockPreCommitProbeAdmissionStatus.Granted ||
                 currentAdmissionToken is null)
        {
            var authorityRejectionReason = string.IsNullOrWhiteSpace(admissionRejectionReason)
                ? "runtime_unlock_admission_missing"
                : admissionRejectionReason;
            lock (gate)
            {
                _ = TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner: null,
                    actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                    source: "runtime_unlock_precommit_probe_received",
                    attemptedAction: "probe_received",
                    admissionToken: null,
                    routeCommitToken: null,
                    transactionGeneration: probe.TransactionGeneration,
                    offerGeneration: probe.OfferGeneration,
                    tunaPathLeaseGeneration: probe.TunaPathLeaseGeneration,
                    tunaPathLeaseListenerRunId: null,
                    probeId: probe.ProbeId,
                    probeAcked: true,
                    samePathTunaProbeAckId: probe.ProbeId,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _);

                LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                    direction,
                    transferId,
                    sessionId,
                    probe,
                    attemptedAdmissionToken: null,
                    currentAdmissionToken: null,
                    authorityRejectionReason);
            }

            return;
        }

        var ack = new FileTransferRuntimeUnlockPreCommitProbeAckFrame
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransactionGeneration = probe.TransactionGeneration,
            OfferGeneration = probe.OfferGeneration,
            TunaPathLeaseGeneration = probe.TunaPathLeaseGeneration,
            ProbeId = probe.ProbeId,
            TargetRoute = probe.TargetRoute,
            TargetProtocolVersion = probe.TargetProtocolVersion,
            TargetTransport = probe.TargetTransport,
            HandoffKind = probe.HandoffKind,
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Accepted = accepted,
            Reason = ackReason,
        };

        if (accepted)
        {
            lock (gate)
            {
                var attemptedAdmissionToken = currentAdmissionToken;
                if (!TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
                        direction,
                        sessionId,
                        transferId,
                        out var refreshedAdmissionToken) ||
                    refreshedAdmissionToken is null)
                {
                    LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                        direction,
                        transferId,
                        sessionId,
                        probe,
                        attemptedAdmissionToken,
                        currentAdmissionToken: null,
                        "runtime_unlock_admission_missing");
                    return;
                }

                if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                        direction,
                        sessionId,
                        transferId,
                        runtimeOwner: null,
                        actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                        source: "runtime_unlock_precommit_probe_received",
                        attemptedAction: "probe_received",
                        admissionToken: refreshedAdmissionToken,
                        routeCommitToken: null,
                        transactionGeneration: probe.TransactionGeneration,
                        offerGeneration: probe.OfferGeneration,
                        tunaPathLeaseGeneration: probe.TunaPathLeaseGeneration,
                        tunaPathLeaseListenerRunId: null,
                        probeId: probe.ProbeId,
                        probeAcked: true,
                        samePathTunaProbeAckId: probe.ProbeId,
                        routeCommitGeneration: null,
                        targetRoute: FileTransferRoute.FileTunaV4,
                        targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                        targetTransport: FileTransferTransportKind.Tuna,
                        out var authorityRejectionReason))
                {
                    LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                        direction,
                        transferId,
                        sessionId,
                        probe,
                        attemptedAdmissionToken,
                        refreshedAdmissionToken,
                        authorityRejectionReason);
                    return;
                }
            }
        }

        if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer ackAuthorizer)
        {
            ackAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(probe, receivedTransportKind);
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(ResolveV6TransportProbeAckSendTimeout());
            await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAckAsync(ack, timeoutCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_ack_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}");
            if (!accepted)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_rejected_for_fallback_survival; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; reason={FormatProtocolLogValue(ackReason)}");
                return;
            }

            if (transport is IRuntimeUnlockRouteCommitProofProvider proofProvider)
            {
                TrackRuntimeUnlockPeerPreCommitProbeLocked(direction, sessionId, transferId, ack);
                if (ShouldRecordReceivedRuntimeUnlockPreCommitProbeAsTransactionProbe(
                        proofProvider,
                        sessionId,
                        transferId,
                        ack.ProbeId))
                {
                    proofProvider.NotifyRuntimeUnlockPathProbeStarted(
                        sessionId,
                        transferId,
                        transportEpoch: 0,
                        ack.ProbeId!,
                        FileTransferTransportKind.Tuna,
                        "precommit_probe_received");
                }
                else
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=runtime_unlock_precommit_peer_probe_tracked; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason=local_probe_already_started");
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_ack_waiting_for_commit_confirmation; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; reason=precommit_probe_ack_sent");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
            $"event=runtime_unlock_precommit_probe_ack_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; error={FormatProtocolLogValue(ex.GetType().Name)}");
        }
    }

    private bool HasCurrentRuntimeUnlockPreCommitProbeAdmissionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId)
    {
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                return HasCurrentRuntimeUnlockAdmissionLocked(outbound);
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                return HasCurrentRuntimeUnlockAdmissionLocked(inbound);
            }
        }

        return false;
    }

    private bool TryGetCurrentRuntimeUnlockPreCommitProbeAdmissionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        out RuntimeUnlockAdmissionToken token)
    {
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.RuntimeUnlockAdmissionToken is { } outboundToken &&
                FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                    CreateCoordinatorStateLocked(outbound),
                    outboundToken,
                    out _))
            {
                token = outboundToken;
                return true;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound &&
                inbound.RuntimeUnlockAdmissionToken is { } inboundToken &&
                FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                    CreateCoordinatorStateLocked(inbound),
                    inboundToken,
                    out _))
            {
                token = inboundToken;
                return true;
            }
        }

        token = default;
        return false;
    }

    private bool TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        out RuntimeUnlockAdmissionToken? token)
    {
        if (direction == FileTransferDirection.Outbound &&
            IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            outboundTransfer is { IsTerminal: false } outbound &&
            TryGetCurrentRuntimeUnlockAdmissionTokenLocked(outbound, out token))
        {
            return true;
        }

        if (direction == FileTransferDirection.Inbound &&
            IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
            inboundTransfer is { IsTerminal: false } inbound &&
            TryGetCurrentRuntimeUnlockAdmissionTokenLocked(inbound, out token))
        {
            return true;
        }

        token = null;
        return false;
    }

    private static void LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase probe,
        RuntimeUnlockAdmissionToken? attemptedAdmissionToken,
        RuntimeUnlockAdmissionToken? currentAdmissionToken,
        string decisionReason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_ack_authority_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={probe.TransactionGeneration}; offer_generation={probe.OfferGeneration}; tuna_path_lease_generation={probe.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(probe.ProbeId ?? "(none)")}; attempted_admission_generation={attemptedAdmissionToken?.AdmissionGeneration ?? 0}; current_admission_generation={currentAdmissionToken?.AdmissionGeneration ?? 0}; attempted_runtime_generation={attemptedAdmissionToken?.RuntimeGeneration ?? 0}; current_runtime_generation={currentAdmissionToken?.RuntimeGeneration ?? 0}; attempted_terminal_generation={attemptedAdmissionToken?.TerminalGeneration ?? 0}; current_terminal_generation={currentAdmissionToken?.TerminalGeneration ?? 0}; attempted_leg_generation={attemptedAdmissionToken?.LegGeneration ?? 0}; current_leg_generation={currentAdmissionToken?.LegGeneration ?? 0}; target_route={FormatProtocolLogValue(probe.TargetRoute)}; target_protocol_version={probe.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(probe.TargetTransport)}; decision_reason={FormatProtocolLogValue(decisionReason)}");

    private static void LogRuntimeUnlockPreCommitProbeAckReceiveDispatched(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        FileTransferTransportKind receivedTransportKind,
        string decisionReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_ack_receive_dispatched; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; frame_type={FormatProtocolLogValue(FileTransferProtocol.RuntimeUnlockPreCommitProbeAckFrameType)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; actual_transport={FormatFileTransferTransportKind(receivedTransportKind)}; delivery_transport={FormatFileTransferTransportKind(receivedTransportKind)}; target_route={FormatProtocolLogValue(ack.TargetRoute)}; target_protocol_version={ack.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(ack.TargetTransport)}; decision_reason={FormatProtocolLogValue(decisionReason)}");

    private enum RuntimeUnlockPreCommitProbeAdmissionStatus
    {
        Granted,
        Rejected,
        Unavailable,
    }

    private RuntimeUnlockPreCommitProbeAdmissionStatus EnsureRuntimeUnlockPreCommitProbeAdmissionLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        out string rejectionReason,
        out RuntimeUnlockAdmissionToken? admissionToken)
    {
        rejectionReason = "none";
        admissionToken = null;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (TryGetCurrentRuntimeUnlockAdmissionTokenLocked(outbound, out admissionToken))
                {
                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Granted;
                }

                var admission = RequestRuntimeUnlockAdmissionLocked(
                    outbound,
                    "precommit_probe_received",
                    "precommit_probe_received");
                if (admission.Granted)
                {
                    if (admission.AdmissionToken is not { } grantedToken)
                    {
                        rejectionReason = "runtime_unlock_admission_missing";
                        return RuntimeUnlockPreCommitProbeAdmissionStatus.Unavailable;
                    }

                    ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(outbound);
                    outbound.RuntimeUnlockAdmissionToken = grantedToken;
                    outbound.LastRuntimeUnlockAdmissionGeneration = Math.Max(
                        outbound.LastRuntimeUnlockAdmissionGeneration,
                        grantedToken.AdmissionGeneration);
                    admissionToken = grantedToken;
                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Granted;
                }

                if (!admission.Granted)
                {
                    rejectionReason = FormatRuntimeUnlockFallbackAdmissionRejection(
                        FileTransferDirection.Outbound,
                        admission);
                    if (outbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                    {
                        MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                            outbound,
                            "precommit_probe_received",
                            rejectionReason);
                    }

                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Rejected;
                }
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                if (TryGetCurrentRuntimeUnlockAdmissionTokenLocked(inbound, out admissionToken))
                {
                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Granted;
                }

                var admission = RequestRuntimeUnlockAdmissionLocked(
                    inbound,
                    "precommit_probe_received",
                    "precommit_probe_received");
                if (admission.Granted)
                {
                    if (admission.AdmissionToken is not { } grantedToken)
                    {
                        rejectionReason = "runtime_unlock_admission_missing";
                        return RuntimeUnlockPreCommitProbeAdmissionStatus.Unavailable;
                    }

                    ClearInboundRuntimeUnlockFallbackSeparationStateLocked(inbound);
                    inbound.RuntimeUnlockAdmissionToken = grantedToken;
                    inbound.LastRuntimeUnlockAdmissionGeneration = Math.Max(
                        inbound.LastRuntimeUnlockAdmissionGeneration,
                        grantedToken.AdmissionGeneration);
                    admissionToken = grantedToken;
                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Granted;
                }

                if (!admission.Granted)
                {
                    rejectionReason = FormatRuntimeUnlockFallbackAdmissionRejection(
                        FileTransferDirection.Inbound,
                        admission);
                    if (inbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                    {
                        MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                            inbound,
                            "precommit_probe_received",
                            rejectionReason);
                    }

                    return RuntimeUnlockPreCommitProbeAdmissionStatus.Rejected;
                }
            }
        }

        return RuntimeUnlockPreCommitProbeAdmissionStatus.Unavailable;
    }

    private static bool TryGetCurrentRuntimeUnlockAdmissionTokenLocked(
        OutboundTransferContext context,
        out RuntimeUnlockAdmissionToken? token)
    {
        if (context.RuntimeUnlockAdmissionToken is { } currentToken &&
            FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                currentToken,
                out _))
        {
            token = currentToken;
            return true;
        }

        token = null;
        return false;
    }

    private static bool TryGetCurrentRuntimeUnlockAdmissionTokenLocked(
        InboundTransferContext context,
        out RuntimeUnlockAdmissionToken? token)
    {
        if (context.RuntimeUnlockAdmissionToken is { } currentToken &&
            FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                currentToken,
                out _))
        {
            token = currentToken;
            return true;
        }

        token = null;
        return false;
    }

    private static bool HasCurrentRuntimeUnlockAdmissionLocked(OutboundTransferContext context)
        => context.RuntimeUnlockAdmissionToken is { } token &&
           FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
               CreateCoordinatorStateLocked(context),
               token,
               out _);

    private static bool HasCurrentRuntimeUnlockAdmissionLocked(InboundTransferContext context)
        => context.RuntimeUnlockAdmissionToken is { } token &&
           FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
               CreateCoordinatorStateLocked(context),
               token,
               out _);

    private static bool TryGetCurrentRuntimeUnlockAdmissionLocked(
        OutboundTransferContext context,
        string reason,
        out RuntimeUnlockAdmissionDecision admission)
    {
        if (context.RuntimeUnlockAdmissionToken is { } token &&
            FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                token,
                out _))
        {
            admission = RuntimeUnlockAdmissionDecisionFromToken(context, token, reason);
            return true;
        }

        admission = new RuntimeUnlockAdmissionDecision(
            RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
            "runtime_unlock_admission_missing");
        return false;
    }

    private static bool TryGetCurrentRuntimeUnlockAdmissionLocked(
        InboundTransferContext context,
        string reason,
        out RuntimeUnlockAdmissionDecision admission)
    {
        if (context.RuntimeUnlockAdmissionToken is { } token &&
            FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                CreateCoordinatorStateLocked(context),
                token,
                out _))
        {
            admission = RuntimeUnlockAdmissionDecisionFromToken(context, token, reason);
            return true;
        }

        admission = new RuntimeUnlockAdmissionDecision(
            RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
            "runtime_unlock_admission_missing");
        return false;
    }

    private static RuntimeUnlockAdmissionDecision RuntimeUnlockAdmissionDecisionFromToken(
        OutboundTransferContext context,
        RuntimeUnlockAdmissionToken token,
        string reason)
        => new(
            RuntimeUnlockAdmissionStatus.Granted,
            NormalizeReason(reason) ?? token.Reason,
            token,
            context.RouteSelection.Route,
            context.RouteSelection.ProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            context.RuntimeUnlockFallbackSurvivalState);

    private static RuntimeUnlockAdmissionDecision RuntimeUnlockAdmissionDecisionFromToken(
        InboundTransferContext context,
        RuntimeUnlockAdmissionToken token,
        string reason)
        => new(
            RuntimeUnlockAdmissionStatus.Granted,
            NormalizeReason(reason) ?? token.Reason,
            token,
            context.RouteSelection.Route,
            context.RouteSelection.ProtocolVersion,
            context.CurrentLiveRouteEpoch?.EpochId ?? 0,
            context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            context.TerminalGeneration,
            context.CurrentTransferLeg?.Generation ?? 0,
            context.RuntimeUnlockFallbackSurvivalState);

    private static bool IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(string? reason)
    {
        var normalized = NormalizeReason(reason);
        return string.Equals(normalized, RuntimeUnlockPreCommitProbeCommitConfirmedReason, StringComparison.Ordinal) ||
               string.Equals(normalized, RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason, StringComparison.Ordinal);
    }

    private bool ShouldRecordReceivedRuntimeUnlockPreCommitProbeAsTransactionProbe(
        IRuntimeUnlockRouteCommitProofProvider proofProvider,
        string sessionId,
        string transferId,
        string? probeId)
    {
        if (string.IsNullOrWhiteSpace(probeId) ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out var snapshot))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(snapshot.PathProbeId) ||
            string.Equals(snapshot.PathProbeState, RuntimeUnlockPathProbeState.None.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(snapshot.PathProbeId, probeId, StringComparison.Ordinal);
    }

    private void TrackRuntimeUnlockPeerPreCommitProbeLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack)
    {
        if (string.IsNullOrWhiteSpace(ack.ProbeId))
        {
            return;
        }

        var state = new RuntimeUnlockPeerPreCommitProbeState(
            sessionId,
            transferId,
            ack.TransactionGeneration,
            ack.OfferGeneration,
            ack.TunaPathLeaseGeneration,
            ack.ProbeId,
            DateTimeOffset.UtcNow);

        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                outbound.RuntimeUnlockPendingPeerPreCommitProbe = state;
                return;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                inbound.RuntimeUnlockPendingPeerPreCommitProbe = state;
            }
        }
    }

    private bool IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame)
    {
        if (string.IsNullOrWhiteSpace(frame.ProbeId))
        {
            return false;
        }

        RuntimeUnlockPeerPreCommitProbeState? pending = null;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                pending = outbound.RuntimeUnlockPendingPeerPreCommitProbe;
            }
            else if (direction == FileTransferDirection.Inbound &&
                     IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                     inboundTransfer is { IsTerminal: false } inbound)
            {
                pending = inbound.RuntimeUnlockPendingPeerPreCommitProbe;
            }
        }

        return pending is not null &&
               pending.TransactionGeneration == frame.TransactionGeneration &&
               pending.OfferGeneration == frame.OfferGeneration &&
               pending.TunaPathLeaseGeneration == frame.TunaPathLeaseGeneration &&
               string.Equals(pending.ProbeId, frame.ProbeId, StringComparison.Ordinal);
    }

    private void ClearRuntimeUnlockPendingPeerPreCommitProbeLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame)
    {
        if (string.IsNullOrWhiteSpace(frame.ProbeId))
        {
            return;
        }

        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.RuntimeUnlockPendingPeerPreCommitProbe is { } outboundPending &&
                outboundPending.TransactionGeneration == frame.TransactionGeneration &&
                outboundPending.OfferGeneration == frame.OfferGeneration &&
                outboundPending.TunaPathLeaseGeneration == frame.TunaPathLeaseGeneration &&
                string.Equals(outboundPending.ProbeId, frame.ProbeId, StringComparison.Ordinal))
            {
                outbound.RuntimeUnlockPendingPeerPreCommitProbe = null;
                return;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound &&
                inbound.RuntimeUnlockPendingPeerPreCommitProbe is { } inboundPending &&
                inboundPending.TransactionGeneration == frame.TransactionGeneration &&
                inboundPending.OfferGeneration == frame.OfferGeneration &&
                inboundPending.TunaPathLeaseGeneration == frame.TunaPathLeaseGeneration &&
                string.Equals(inboundPending.ProbeId, frame.ProbeId, StringComparison.Ordinal))
            {
                inbound.RuntimeUnlockPendingPeerPreCommitProbe = null;
            }
        }
    }

    private async Task HandleRuntimeUnlockPreCommitProbeAckAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession)
    {
        LogRuntimeUnlockPreCommitProbeAckReceiveDispatched(
            direction,
            transferId,
            sessionId,
            ack,
            receivedTransportKind,
            "core_handler_invoked");

        if (!TryValidateRuntimeUnlockPreCommitProbeFrame(
                sessionId,
                transferId,
                direction,
                ack,
                receivedTransportKind,
                expectAck: true,
                out var reason))
        {
            RuntimeUnlockAdmissionToken? latestAdmissionToken;
            lock (gate)
            {
                _ = TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    out latestAdmissionToken);
            }

            LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                direction,
                transferId,
                sessionId,
                ack,
                attemptedAdmissionToken: null,
                currentAdmissionToken: latestAdmissionToken,
                reason);
            LogRuntimeUnlockPreCommitProbeIgnored(direction, transferId, sessionId, ack, receivedTransportKind, reason);
            return;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            LogRuntimeUnlockPreCommitProbeIgnored(direction, transferId, sessionId, ack, receivedTransportKind, "proof_provider_unavailable");
            return;
        }

        var ackReason = NormalizeReason(ack.Reason);
        var isPeerCommitConfirmation = IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(ackReason);
        var commitReason = string.Equals(ackReason, RuntimeUnlockPreCommitProbeCommitConfirmedReason, StringComparison.Ordinal)
            ? RuntimeUnlockPreCommitProbeCommitConfirmedReason
            : string.Equals(ackReason, RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason, StringComparison.Ordinal)
                ? RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason
                : RuntimeUnlockPreCommitProbeAcceptedReason;

        RuntimeUnlockAdmissionToken? currentAdmissionToken;
        bool authorized;
        string ackAuthorizationRejectionReason;
        lock (gate)
        {
            _ = TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
                direction,
                sessionId,
                transferId,
                out currentAdmissionToken);

            authorized = TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner: null,
                    actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                    source: "runtime_unlock_precommit_probe_ack",
                    attemptedAction: ack.Accepted ? "probe_ack" : "probe_rejected",
                    admissionToken: currentAdmissionToken,
                    routeCommitToken: null,
                    transactionGeneration: ack.TransactionGeneration,
                    offerGeneration: ack.OfferGeneration,
                    tunaPathLeaseGeneration: ack.TunaPathLeaseGeneration,
                    tunaPathLeaseListenerRunId: null,
                    probeId: ack.ProbeId,
                    probeAcked: ack.Accepted,
                    samePathTunaProbeAckId: ack.Accepted ? ack.ProbeId : null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out ackAuthorizationRejectionReason);
        }

        if (!authorized)
        {
            RuntimeUnlockAdmissionToken? latestAdmissionToken;
            lock (gate)
            {
                _ = TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    out latestAdmissionToken);
            }

            LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                direction,
                transferId,
                sessionId,
                ack,
                currentAdmissionToken,
                latestAdmissionToken,
                ackAuthorizationRejectionReason);
            return;
        }

        if (!ack.Accepted)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_tuna_probe_ack_received; transport=tuna; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; accepted=0; reason={FormatProtocolLogValue(NormalizeReason(ack.Reason) ?? "precommit_probe_rejected")}");
            proofProvider.NotifyRuntimeUnlockPathProbeResult(
                sessionId,
                transferId,
                transportEpoch: 0,
                ack.ProbeId!,
                FileTransferTransportKind.Tuna,
                acked: false,
                NormalizeReason(ack.Reason) ?? "precommit_probe_rejected");
            LogRuntimeUnlockPreCommitProbeFailed(
                direction,
                transferId,
                sessionId,
                ack.TransactionGeneration,
                ack.OfferGeneration,
                ack.TunaPathLeaseGeneration,
                ack.ProbeId ?? "(none)",
                NormalizeReason(ack.Reason) ?? "precommit_probe_rejected");
            MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                direction,
                sessionId,
                transferId,
                NormalizeReason(ack.Reason) ?? "precommit_probe_rejected");
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_tuna_probe_ack_received; transport=tuna; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; accepted=1; reason={FormatProtocolLogValue(commitReason)}");

        if (isPeerCommitConfirmation)
        {
            ClearRuntimeUnlockPendingPeerPreCommitProbeLocked(direction, sessionId, transferId, ack);
        }

        proofProvider.NotifyRuntimeUnlockPathProbeResult(
            sessionId,
            transferId,
            transportEpoch: 0,
            ack.ProbeId!,
            FileTransferTransportKind.Tuna,
            acked: true,
            commitReason);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_acked; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}");
        if (!isPeerCommitConfirmation)
        {
            if (dataSession is not IFileTransferRuntimeUnlockTunaProbeSender)
            {
                proofProvider.NotifyRuntimeUnlockPathProbeResult(
                    sessionId,
                    transferId,
                    transportEpoch: 0,
                    ack.ProbeId!,
                    FileTransferTransportKind.Tuna,
                    acked: false,
                    "runtime_unlock_tuna_probe_sender_unavailable");
                LogRuntimeUnlockPreCommitProbeFailed(
                    direction,
                    transferId,
                    sessionId,
                    ack.TransactionGeneration,
                    ack.OfferGeneration,
                    ack.TunaPathLeaseGeneration,
                    ack.ProbeId ?? "(none)",
                    "runtime_unlock_tuna_probe_sender_unavailable");
                MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                    direction,
                    sessionId,
                    transferId,
                    "runtime_unlock_tuna_probe_sender_unavailable");
                return;
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_paired_commit_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(RuntimeUnlockPreCommitProbeCommitConfirmedReason)}");
            if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer confirmationAuthorizer)
            {
                confirmationAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(ack, receivedTransportKind);
            }

            var confirmationSent = await SendRuntimeUnlockPreCommitProbeCommitConfirmationAsync(
                    direction,
                    sessionId,
                    transferId,
                    ack,
                    dataSession,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReason)
                .ConfigureAwait(false);
            if (!confirmationSent)
            {
                lock (gate)
                {
                    if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                            direction,
                            sessionId,
                            transferId,
                            runtimeOwner: null,
                            actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback,
                            source: "runtime_unlock_paired_commit_confirmation",
                            attemptedAction: "paired_commit_confirmation_send_failed",
                            admissionToken: null,
                            routeCommitToken: null,
                            transactionGeneration: ack.TransactionGeneration,
                            offerGeneration: ack.OfferGeneration,
                            tunaPathLeaseGeneration: ack.TunaPathLeaseGeneration,
                            tunaPathLeaseListenerRunId: null,
                            probeId: ack.ProbeId,
                            probeAcked: false,
                            samePathTunaProbeAckId: null,
                            routeCommitGeneration: null,
                            targetRoute: FileTransferRoute.FileTunaV4,
                            targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                            targetTransport: FileTransferTransportKind.Tuna,
                            out _))
                    {
                        return;
                    }
                }

                proofProvider.NotifyRuntimeUnlockPathProbeResult(
                    sessionId,
                    transferId,
                    transportEpoch: 0,
                    ack.ProbeId!,
                    FileTransferTransportKind.Tuna,
                    acked: false,
                    "runtime_unlock_paired_commit_confirmation_send_failed");
                MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                    direction,
                    sessionId,
                    transferId,
                    "runtime_unlock_paired_commit_confirmation_send_failed");
                return;
            }

            var initiatorCommitted = TryCommitRuntimeUnlockRouteAfterPreCommitProbeAck(
                direction,
                sessionId,
                transferId,
                ack.TransactionGeneration,
                ack.OfferGeneration,
                RuntimeUnlockPreCommitProbeCommitConfirmedReason,
                out var initiatorRouteCommitToken);
            if (initiatorCommitted)
            {
                NotifyRuntimeUnlockPairedRouteCommitConfirmed(
                    direction,
                    sessionId,
                    transferId,
                    ack.TransactionGeneration,
                    ack.OfferGeneration,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReason,
                    initiatorRouteCommitToken);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_paired_commit_confirmed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(RuntimeUnlockPreCommitProbeCommitConfirmedReason)}");
            }
            else
            {
                _ = RunRuntimeUnlockPairedCommitTimeoutAsync(
                    proofProvider,
                    sessionId,
                    transferId,
                    direction,
                    ack.TransactionGeneration,
                    ack.OfferGeneration,
                    ack.TunaPathLeaseGeneration,
                    ack.ProbeId!,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason);
            }
            return;
        }

        var committed = TryCommitRuntimeUnlockRouteAfterPreCommitProbeAck(
            direction,
            sessionId,
            transferId,
            ack.TransactionGeneration,
            ack.OfferGeneration,
            commitReason,
            out var routeCommitToken);
        if (committed &&
            string.Equals(ackReason, RuntimeUnlockPreCommitProbeCommitConfirmedReason, StringComparison.Ordinal) &&
            dataSession is not null)
        {
            if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer replyAuthorizer)
            {
                replyAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(ack, receivedTransportKind);
            }

            var replySent = await SendRuntimeUnlockPreCommitProbeCommitConfirmationAsync(
                    direction,
                    sessionId,
                    transferId,
                    ack,
                    dataSession,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason)
                .ConfigureAwait(false);
            if (replySent)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_paired_commit_confirmed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; reason={FormatProtocolLogValue(RuntimeUnlockPreCommitProbeCommitConfirmedReplySentReason)}");
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_paired_commit_confirmed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(RuntimeUnlockPreCommitProbeCommitConfirmedReplySentReason)}");
            }
            else
            {
                lock (gate)
                {
                    if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                            direction,
                            sessionId,
                            transferId,
                            routeCommitToken.RuntimeOwner,
                            actionKind: FileTransferCoordinatorActionKind.RouteCommitPublication,
                            source: "runtime_unlock_paired_route_commit",
                            attemptedAction: "paired_commit_reply_send_failed",
                            admissionToken: null,
                            routeCommitToken: routeCommitToken,
                            transactionGeneration: ack.TransactionGeneration,
                            offerGeneration: ack.OfferGeneration,
                            tunaPathLeaseGeneration: ack.TunaPathLeaseGeneration,
                            tunaPathLeaseListenerRunId: routeCommitToken.TunaPathLeaseListenerRunId,
                            probeId: ack.ProbeId,
                            probeAcked: false,
                            samePathTunaProbeAckId: routeCommitToken.SamePathTunaProbeAckId,
                            routeCommitGeneration: routeCommitToken.CommitGeneration,
                            targetRoute: routeCommitToken.TargetRoute,
                            targetProtocolVersion: routeCommitToken.TargetProtocolVersion,
                            targetTransport: routeCommitToken.TargetTransport,
                            out _))
                    {
                        return;
                    }
                }

                proofProvider.NotifyRuntimeUnlockRouteCommitResult(
                    sessionId,
                    transferId,
                    direction,
                    ack.TransactionGeneration,
                    ack.OfferGeneration,
                    accepted: false,
                    "runtime_unlock_paired_commit_reply_send_failed");
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=runtime_unlock_paired_commit_aborted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason=reply_send_failed");
            }
        }
        else if (committed &&
                 string.Equals(ackReason, RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason, StringComparison.Ordinal))
        {
            NotifyRuntimeUnlockPairedRouteCommitConfirmed(
                direction,
                sessionId,
                transferId,
                ack.TransactionGeneration,
                ack.OfferGeneration,
                RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason,
                routeCommitToken);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_paired_commit_confirmed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason)}");
        }
    }

    private async Task<bool> SendRuntimeUnlockPreCommitProbeCommitConfirmationAsync(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        IFileTransferDataSession dataSession,
        string reason)
    {
        if (dataSession is not IFileTransferRuntimeUnlockTunaProbeSender tunaProbeSender)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_commit_confirmation_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; error=runtime_unlock_tuna_probe_sender_unavailable");
            return false;
        }

        var confirmation = new FileTransferRuntimeUnlockPreCommitProbeAckFrame
        {
            SessionId = ack.SessionId,
            TransferId = ack.TransferId,
            TransactionGeneration = ack.TransactionGeneration,
            OfferGeneration = ack.OfferGeneration,
            TunaPathLeaseGeneration = ack.TunaPathLeaseGeneration,
            ProbeId = ack.ProbeId,
            TargetRoute = ack.TargetRoute,
            TargetProtocolVersion = ack.TargetProtocolVersion,
            TargetTransport = ack.TargetTransport,
            HandoffKind = ack.HandoffKind,
            SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Accepted = true,
            Reason = reason,
        };

        try
        {
            using var timeoutCts = new CancellationTokenSource(ResolveV6TransportProbeAckSendTimeout());
            if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer authorizer)
            {
                authorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(confirmation, FileTransferTransportKind.Tuna);
            }

            await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAckAsync(confirmation, timeoutCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_commit_confirmation_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={confirmation.TransactionGeneration}; offer_generation={confirmation.OfferGeneration}; tuna_path_lease_generation={confirmation.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(confirmation.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}");
            if (IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(reason))
            {
                if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer duplicateAuthorizer)
                {
                    duplicateAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(confirmation, FileTransferTransportKind.Tuna);
                }

                await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAckAsync(confirmation, timeoutCts.Token).ConfigureAwait(false);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_commit_confirmation_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={confirmation.TransactionGeneration}; offer_generation={confirmation.OfferGeneration}; tuna_path_lease_generation={confirmation.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(confirmation.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; duplicate=1");
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_commit_confirmation_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={confirmation.TransactionGeneration}; offer_generation={confirmation.OfferGeneration}; tuna_path_lease_generation={confirmation.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(confirmation.ProbeId ?? "(none)")}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.GetType().Name)}");
            return false;
        }
    }

    private async Task RunRuntimeUnlockPairedCommitTimeoutAsync(
        IRuntimeUnlockRouteCommitProofProvider proofProvider,
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string expectedReplyReason)
    {
        try
        {
            await Task.Delay(ResolveV6TransportEpochProofTimeout()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out var snapshot) ||
            snapshot.TransactionGeneration != transactionGeneration ||
            snapshot.OfferGeneration != offerGeneration ||
            snapshot.TunaPathLeaseGeneration != leaseGeneration ||
            !string.Equals(snapshot.PathProbeId, probeId, StringComparison.Ordinal) ||
            string.Equals(snapshot.TransactionState, RuntimeUnlockTransactionState.Committed.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    runtimeOwner: null,
                    actionKind: FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback,
                    source: "runtime_unlock_paired_commit_timeout",
                    attemptedAction: "paired_commit_timeout",
                    admissionToken: null,
                    routeCommitToken: null,
                    transactionGeneration: transactionGeneration,
                    offerGeneration: offerGeneration,
                    tunaPathLeaseGeneration: leaseGeneration,
                    tunaPathLeaseListenerRunId: snapshot.TunaPathLeaseListenerRunId,
                    probeId: probeId,
                    probeAcked: false,
                    samePathTunaProbeAckId: null,
                    routeCommitGeneration: null,
                    targetRoute: FileTransferRoute.FileTunaV4,
                    targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                    targetTransport: FileTransferTransportKind.Tuna,
                    out _))
            {
                return;
            }
        }

        proofProvider.NotifyRuntimeUnlockPathProbeResult(
            sessionId,
            transferId,
            transportEpoch: 0,
            probeId,
            FileTransferTransportKind.Tuna,
            acked: false,
            "runtime_unlock_paired_commit_timeout");
        MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
            direction,
            sessionId,
            transferId,
            "runtime_unlock_paired_commit_timeout");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=runtime_unlock_paired_commit_aborted; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; tuna_path_lease_generation={leaseGeneration}; probe_id={FormatProtocolLogValue(probeId)}; expected_reply_reason={FormatProtocolLogValue(expectedReplyReason)}; reason=timeout");
    }

    private void NotifyRuntimeUnlockPairedRouteCommitConfirmed(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        long transactionGeneration,
        long offerGeneration,
        string reason,
        RouteCommitToken routeCommitToken)
    {
        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            return;
        }

        if (routeCommitToken.IsDefault ||
            routeCommitToken.TransactionGeneration != transactionGeneration ||
            routeCommitToken.OfferGeneration != offerGeneration)
        {
            LogRuntimeUnlockRouteCommitRejected(
                direction,
                transferId,
                sessionId,
                reason,
                routeCommitToken.IsDefault ? "route_commit_token_missing" : "transaction_or_offer_mismatch");
            return;
        }

        lock (gate)
        {
            if (!TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    routeCommitToken.RuntimeOwner,
                    FileTransferCoordinatorActionKind.RouteCommitPublication,
                    "runtime_unlock_paired_route_commit",
                    reason,
                    admissionToken: null,
                    routeCommitToken: routeCommitToken,
                    transactionGeneration: transactionGeneration,
                    offerGeneration: offerGeneration,
                    tunaPathLeaseGeneration: routeCommitToken.TunaPathLeaseGeneration,
                    tunaPathLeaseListenerRunId: routeCommitToken.TunaPathLeaseListenerRunId,
                    probeId: routeCommitToken.SamePathTunaProbeAckId,
                    probeAcked: true,
                    samePathTunaProbeAckId: routeCommitToken.SamePathTunaProbeAckId,
                    routeCommitGeneration: routeCommitToken.CommitGeneration,
                    targetRoute: routeCommitToken.TargetRoute,
                    targetProtocolVersion: routeCommitToken.TargetProtocolVersion,
                    targetTransport: routeCommitToken.TargetTransport,
                    out _))
            {
                return;
            }
        }

        proofProvider.NotifyRuntimeUnlockRouteCommitResult(
            sessionId,
            transferId,
            direction,
            transactionGeneration,
            offerGeneration,
            accepted: true,
            reason);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_paired_commit_confirmed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; reason={FormatProtocolLogValue(reason)}");
    }

    private bool TryValidateRuntimeUnlockPreCommitProbeFrame(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame,
        FileTransferTransportKind receivedTransportKind,
        bool expectAck,
        out string reason)
    {
        reason = "none";
        if (receivedTransportKind != FileTransferTransportKind.Tuna)
        {
            reason = "not_tuna_transport";
            return false;
        }

        if (string.IsNullOrWhiteSpace(frame.ProbeId) ||
            frame.TransactionGeneration <= 0 ||
            frame.OfferGeneration <= 0 ||
            frame.TunaPathLeaseGeneration < 0 ||
            !string.Equals(frame.TargetRoute, FileTransferRouteResolver.FileTunaV4Token, StringComparison.Ordinal) ||
            frame.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            !string.Equals(frame.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.HandoffKind, "normal_to_tuna_activation", StringComparison.OrdinalIgnoreCase))
        {
            reason = "metadata_invalid";
            return false;
        }

        if (!expectAck)
        {
            _ = direction;
            return true;
        }

        if (frame is FileTransferRuntimeUnlockPreCommitProbeAckFrame ackFrame &&
            IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(ackFrame.Reason) &&
            IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(direction, sessionId, transferId, frame))
        {
            return true;
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider ||
            !proofProvider.TryGetRuntimeUnlockRouteCommitProof(sessionId, transferId, out var snapshot))
        {
            reason = "transaction_proof_missing";
            return false;
        }

        if (!IsRuntimeUnlockPreCommitProbeSnapshotUsable(snapshot) ||
            snapshot.TransactionGeneration != frame.TransactionGeneration ||
            snapshot.OfferGeneration != frame.OfferGeneration ||
            snapshot.TunaPathLeaseGeneration != frame.TunaPathLeaseGeneration)
        {
            reason = "transaction_or_lease_mismatch";
            return false;
        }

        if (!string.Equals(snapshot.PathProbeId, frame.ProbeId, StringComparison.Ordinal))
        {
            reason = "probe_id_mismatch";
            return false;
        }

        _ = direction;
        return true;
    }

    private bool TryAuthorizeRuntimeUnlockRouteCommitTokenLocked(
        OutboundTransferContext context,
        long transactionGeneration,
        long offerGeneration,
        string reason,
        out RouteCommitToken routeCommitToken)
    {
        routeCommitToken = default;
        if (context.RuntimeUnlockRouteCommitToken is not { } token || token.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                reason,
                "route_commit_token_missing");
            return false;
        }

        if (token.TransactionGeneration != transactionGeneration ||
            token.OfferGeneration != offerGeneration)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Outbound,
                context.TransferId,
                context.SessionId,
                reason,
                "transaction_or_offer_mismatch");
            return false;
        }

        if (!TryAuthorizeRuntimeUnlockRoutePublicationLocked(context, token, reason, out _, out _))
        {
            return false;
        }

        routeCommitToken = token;
        return true;
    }

    private bool TryAuthorizeRuntimeUnlockRouteCommitTokenLocked(
        InboundTransferContext context,
        long transactionGeneration,
        long offerGeneration,
        string reason,
        out RouteCommitToken routeCommitToken)
    {
        routeCommitToken = default;
        if (context.RuntimeUnlockRouteCommitToken is not { } token || token.IsDefault)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                reason,
                "route_commit_token_missing");
            return false;
        }

        if (token.TransactionGeneration != transactionGeneration ||
            token.OfferGeneration != offerGeneration)
        {
            LogRuntimeUnlockRouteCommitRejected(
                FileTransferDirection.Inbound,
                context.TransferId,
                context.SessionId,
                reason,
                "transaction_or_offer_mismatch");
            return false;
        }

        if (!TryAuthorizeRuntimeUnlockRoutePublicationLocked(context, token, reason, out _, out _))
        {
            return false;
        }

        routeCommitToken = token;
        return true;
    }

    private bool TryCommitRuntimeUnlockRouteAfterPreCommitProbeAck(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        long transactionGeneration,
        long offerGeneration,
        string reason,
        out RouteCommitToken routeCommitToken)
    {
        SessionFileTransferSnapshot? snapshot = null;
        var committed = false;
        routeCommitToken = default;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (outbound.RouteRuntime.UsesRegularNknV4FastRuntime)
                {
                    if (TryGetCurrentRuntimeUnlockAdmissionLocked(outbound, reason, out var admission))
                    {
                        if (!outbound.RuntimeUnlockActivationWindowGranted)
                        {
                            GrantOutboundRuntimeUnlockActivationWindowLocked(outbound, admission, reason);
                        }

                        TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                            outbound,
                            reason,
                            FileTransferTransportHandoffKind.NormalToTunaActivation,
                            FileTransferTransportKind.Tuna);
                    }
                }
                else if (outbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                {
                    if (!TryGetCurrentRuntimeUnlockAdmissionLocked(outbound, reason, out var admission))
                    {
                        MarkOutboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                            outbound,
                            reason,
                            "runtime_unlock_admission_missing");
                    }
                    else
                    {
                        if (!outbound.RuntimeUnlockActivationWindowGranted)
                        {
                            GrantOutboundRuntimeUnlockActivationWindowLocked(outbound, admission, reason);
                        }

                        TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked(
                            outbound,
                            reason,
                            FileTransferTransportHandoffKind.NormalToTunaActivation,
                            FileTransferTransportKind.Tuna);
                    }
                }

                if (outbound.RouteRuntime.UsesFileTunaV4Runtime)
                {
                    committed = TryAuthorizeRuntimeUnlockRouteCommitTokenLocked(
                        outbound,
                        transactionGeneration,
                        offerGeneration,
                        reason,
                        out routeCommitToken);
                    if (committed)
                    {
                        snapshot = CreateSnapshotLocked();
                    }
                }
            }
            else if (direction == FileTransferDirection.Inbound &&
                     IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                     inboundTransfer is { IsTerminal: false } inbound)
            {
                if (inbound.RouteRuntime.UsesRegularNknV4FastRuntime)
                {
                    if (TryGetCurrentRuntimeUnlockAdmissionLocked(inbound, reason, out var admission))
                    {
                        if (!inbound.RuntimeUnlockActivationWindowGranted)
                        {
                            GrantInboundRuntimeUnlockActivationWindowLocked(inbound, admission, reason);
                        }

                        TryPromoteInboundRegularNknV4ToFileTunaV4Locked(
                            inbound,
                            reason,
                            FileTransferTransportHandoffKind.NormalToTunaActivation,
                            FileTransferTransportKind.Tuna);
                    }
                }
                else if (inbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
                {
                    if (!TryGetCurrentRuntimeUnlockAdmissionLocked(inbound, reason, out var admission))
                    {
                        MarkInboundRuntimeUnlockWaitingForFallbackSurvivalLocked(
                            inbound,
                            reason,
                            "runtime_unlock_admission_missing");
                    }
                    else
                    {
                        if (!inbound.RuntimeUnlockActivationWindowGranted)
                        {
                            GrantInboundRuntimeUnlockActivationWindowLocked(inbound, admission, reason);
                        }

                        TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(
                            inbound,
                            reason,
                            FileTransferTransportHandoffKind.NormalToTunaActivation,
                            FileTransferTransportKind.Tuna);
                    }
                }

                if (inbound.RouteRuntime.UsesFileTunaV4Runtime)
                {
                    committed = TryAuthorizeRuntimeUnlockRouteCommitTokenLocked(
                        inbound,
                        transactionGeneration,
                        offerGeneration,
                        reason,
                        out routeCommitToken);
                    if (committed)
                    {
                        snapshot = CreateSnapshotLocked();
                    }
                }
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return committed;
    }

    private void MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        string reason)
    {
        FileTransferLeg? leg = null;
        var resumed = false;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
            {
                leg = outbound.CurrentTransferLeg;
                resumed = outbound.RuntimeUnlockActivationWindowGranted ||
                    outbound.RuntimeUnlockWaitingForFallbackSurvival;
                ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(outbound);
            }
            else if (direction == FileTransferDirection.Inbound &&
                     IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                     inboundTransfer is { IsTerminal: false } inbound &&
                     inbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
            {
                leg = inbound.CurrentTransferLeg;
                resumed = inbound.RuntimeUnlockActivationWindowGranted ||
                    inbound.RuntimeUnlockWaitingForFallbackSurvival;
                ClearInboundRuntimeUnlockFallbackSeparationStateLocked(inbound);
            }
        }

        if (resumed)
        {
            LogRuntimeUnlockProbeFailedFallbackResumed(
                direction,
                transferId,
                sessionId,
                leg,
                reason);
        }
    }

    private static void LogRuntimeUnlockPreCommitProbeStarted(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRuntimeUnlockPreCommitProbeFrame frame)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={frame.TransactionGeneration}; offer_generation={frame.OfferGeneration}; tuna_path_lease_generation={frame.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(frame.ProbeId ?? "(none)")}; target_route={FormatProtocolLogValue(frame.TargetRoute)}; target_protocol_version={frame.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(frame.TargetTransport)}; handoff_kind={FormatProtocolLogValue(frame.HandoffKind)}");

    private static void LogRuntimeUnlockPreCommitProbeFailed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string reason)
        => LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; tuna_path_lease_generation={leaseGeneration}; probe_id={FormatProtocolLogValue(probeId)}; reason={FormatProtocolLogValue(reason)}");

    private static void LogRuntimeUnlockPreCommitProbeWaitingForTransportProof(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation");

    private static void LogRuntimeUnlockPreCommitProbePendingHeld(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        string reason,
        string holdReason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_pending_held; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source=runtime_unlock_precommit_probe_pending; reason={FormatProtocolLogValue(reason)}; hold_reason={FormatProtocolLogValue(holdReason)}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch={runtimeOwner.LiveRouteEpoch}; runtime_generation={runtimeOwner.RuntimeGeneration}; terminal_generation={runtimeOwner.TerminalGeneration}; leg_generation={admissionToken.LegGeneration}; admission_generation={admissionToken.AdmissionGeneration}; pending_probe_generation=0; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; tuna_path_lease_listener_run_id=(none); target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation");

    private static bool LogRuntimeUnlockPendingPreCommitProbeDecision(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string source,
        string reason,
        RuntimeUnlockPendingPreCommitProbeDecision? decision,
        RuntimeUnlockPendingPreCommitProbeRequest attempted)
    {
        if (decision is { Accepted: true, PendingRequest: { } accepted })
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_runtime_unlock_precommit_probe_pending; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source={FormatProtocolLogValue(source)}; reason={FormatProtocolLogValue(reason)}{FormatRuntimeUnlockPendingPreCommitProbeFields(accepted)}; rejection_reason=accepted");
            return true;
        }

        LogRuntimeUnlockPreCommitProbePendingRejected(
            direction,
            transferId,
            sessionId,
            attempted,
            attempted.RuntimeOwner,
            attempted.AdmissionToken,
            attempted.TransactionGeneration,
            attempted.OfferGeneration,
            attempted.TunaPathLeaseGeneration,
            attempted.TunaPathLeaseListenerRunId,
            decision?.RejectionReason ?? "runtime_unlock_pending_precommit_probe_rejected");
        return false;
    }

    private static void LogRuntimeUnlockPreCommitProbeReplayed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeUnlockPendingPreCommitProbeRequest pending,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_replayed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source=runtime_unlock_precommit_probe_pending; reason={FormatProtocolLogValue(reason)}{FormatRuntimeUnlockPendingPreCommitProbeFields(pending)}; rejection_reason=accepted");

    private static void LogRuntimeUnlockPreCommitProbePendingRejected(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeUnlockPendingPreCommitProbeRequest? pending,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string? listenerRunId,
        string rejectionReason)
    {
        var fields = pending is { } request
            ? FormatRuntimeUnlockPendingPreCommitProbeFields(request)
            : $" runtime_generation={runtimeOwner.RuntimeGeneration}; terminal_generation={runtimeOwner.TerminalGeneration}; live_route_epoch={runtimeOwner.LiveRouteEpoch}; leg_generation={admissionToken.LegGeneration}; admission_generation={admissionToken.AdmissionGeneration}; pending_probe_generation=0; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; tuna_path_lease_generation={leaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(listenerRunId ?? "(none)")}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation";
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_precommit_probe_pending_rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; source=runtime_unlock_precommit_probe_pending; reason={FormatProtocolLogValue(rejectionReason)}{fields}; rejection_reason={FormatProtocolLogValue(rejectionReason)}");
    }

    private static string FormatRuntimeUnlockPendingPreCommitProbeFields(RuntimeUnlockPendingPreCommitProbeRequest pending)
        => $" route={FormatProtocolLogValue(pending.RouteToken)}; protocol_version={pending.ProtocolVersion}; live_route_epoch={pending.LiveRouteEpoch}; runtime_generation={pending.RuntimeGeneration}; terminal_generation={pending.TerminalGeneration}; leg_generation={pending.LegGeneration}; admission_generation={pending.AdmissionToken.AdmissionGeneration}; pending_probe_generation={pending.PendingGeneration}; transaction_generation={pending.TransactionGeneration}; offer_generation={pending.OfferGeneration}; tuna_path_lease_generation={pending.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(pending.TunaPathLeaseListenerRunId ?? "(none)")}; target_route={FormatProtocolLogValue(FormatRuntimeUnlockCallbackRouteToken(pending.TargetRoute))}; target_protocol_version={pending.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(FormatFileTransferTransportKind(pending.TargetTransport))}; handoff_kind={FormatProtocolLogValue(FormatFileTransferTransportHandoffKind(pending.HandoffKind))}";

    private static void LogRuntimeUnlockProbeFailedFallbackResumed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferLeg? leg,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_runtime_unlock_probe_failed_fallback_resumed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; reason={FormatProtocolLogValue(reason)}; leg_id={FormatProtocolLogValue(leg?.LegId ?? "(none)")}; leg_generation={leg?.Generation ?? 0}; route={FormatProtocolLogValue(leg?.RouteSelection.TelemetryToken ?? "(none)")}; protocol_version={leg?.ProtocolVersion ?? 0}; live_route_epoch={leg?.LiveRouteEpochId ?? 0}; transport_epoch={leg?.TransportEpochId ?? 0}; bridge_recovery_generation={leg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(leg?.CheckpointRequestId ?? "(none)")}; state={FormatProtocolLogValue(leg is null ? "none" : FormatFileTransferLegState(leg.State))}; can_send_data={(leg?.CanSendData == true ? 1 : 0)}");

    private static void LogRuntimeUnlockPreCommitProbeIgnored(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferRuntimeUnlockPreCommitProbeFrameBase frame,
        FileTransferTransportKind receivedTransportKind,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_ignored; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; frame_type={FormatProtocolLogValue(frame.Type)}; transaction_generation={frame.TransactionGeneration}; offer_generation={frame.OfferGeneration}; tuna_path_lease_generation={frame.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(frame.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; reason={FormatProtocolLogValue(reason)}");

    private bool StartOutboundV6TransportEpochLocked(
        OutboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        bool coordinatorAuthorized = false)
    {
        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (!coordinatorAuthorized &&
            !TryAuthorizeTransportEpochActionLocked(
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
                out _))
        {
            return false;
        }

        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (ShouldReuseCurrentV6TransportEpoch(context.CurrentTransferLeg, current, handoffKind, targetTransport))
            {
                AdoptOutboundFallbackLegTransportEpochLocked(context, current.EpochId, reason);
                ResolveOutboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
                LogV6TransportEpochReused(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, reason);
                return true;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, "superseded");
        }

        var epochId = Math.Max(context.LastRecoveredV6TransportEpoch + 1, Math.Max(1, context.PullTransportRebindGeneration));
        var senderLocalAvailabilityUntilExclusive = ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context);
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
            StartingHighestObservedChunkIndex = Math.Max(-1, senderLocalAvailabilityUntilExclusive - 1),
            LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = Math.Max(-1, senderLocalAvailabilityUntilExclusive - 1),
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
        ClearOutboundV6RequestQueuesForTransportEpochLocked(
            context,
            epoch.EpochId,
            "transport_epoch_started");
        AdoptOutboundFallbackLegTransportEpochLocked(context, epoch.EpochId, reason);
        ResolveOutboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
        context.StatusMessage = GetV6TransportEpochStatus(epoch);
        LogV6TransportEpochStarted(context.TransferId, context.SessionId, epoch);
        PublishV6TransportEpochSnapshot(context.SessionId, context.TransferId, epoch);
        return true;
    }

    private void StartInboundV6TransportEpochLocked(
        InboundTransferContext context,
        string reason,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
    {
        targetTransport = NormalizeV6TargetTransport(handoffKind, targetTransport);
        if (!TryAuthorizeTransportEpochActionLocked(
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
                out _))
        {
            return;
        }

        if (context.V6TransportEpoch is { } current &&
            IsV6TransportEpochUnresolved(current))
        {
            if (ShouldReuseCurrentV6TransportEpoch(context.CurrentTransferLeg, current, handoffKind, targetTransport))
            {
                LogV6TransportEpochReused(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, reason);
                AdoptInboundFallbackLegTransportEpochLocked(context, current.EpochId, reason);
                ResolveInboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
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
        AdoptInboundFallbackLegTransportEpochLocked(context, epoch.EpochId, reason);
        ResolveInboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
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
                $"event=filetransfer_v6_epoch_recovered; direction={epoch.Direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; elapsed_ms={elapsedMs}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
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
            if (!TryAuthorizeTransportEpochActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    TransportEpochActionKind.Adopt,
                    "v6_transport_epoch_probe",
                    "announce_probe",
                    context.CurrentTransferLeg,
                    ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    context.RouteSelection.Route,
                    context.NegotiatedDataProtocolVersion,
                    epoch.TargetTransport,
                    out _))
            {
                return;
            }

            epoch.LastAnnouncedUtc = DateTimeOffset.UtcNow;
        }

        await SendV6TransportEpochControlAsync(context.SessionId, context.TransferId, epoch).ConfigureAwait(false);
        NotifyRuntimeUnlockPathProbeStarted(context.SessionId, context.TransferId, epoch, "transport_probe_sent");
        await SendV6TransportProbeFrameAsync(context.SessionId, context.TransferId, epoch, dataSession, context.LifetimeCts.Token).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                ReferenceEquals(context.V6TransportEpoch, epoch) &&
                IsV6TransportEpochUnresolved(epoch) &&
                epoch.State == V6TransportEpochState.TargetProofPending)
            {
                if (!TryAuthorizeTransportEpochActionLocked(
                        context,
                        FileTransferCoordinatorActionKind.TransportEpochAdoption,
                        TransportEpochActionKind.Adopt,
                        "v6_transport_epoch_probe",
                        "probe_sent",
                        context.CurrentTransferLeg,
                        ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                        context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                        null,
                        context.RouteSelection.Route,
                        context.NegotiatedDataProtocolVersion,
                        epoch.TargetTransport,
                        out _))
                {
                    return;
                }

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
        ScheduleOutboundV6TransportEpochReplay(context, epoch.EpochId, "announce_probe");
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
            if (!TryAuthorizeTransportEpochActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    TransportEpochActionKind.Adopt,
                    "v6_transport_epoch_probe",
                    "announce_probe",
                    context.CurrentTransferLeg,
                    ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    context.RouteSelection.Route,
                    context.NegotiatedDataProtocolVersion,
                    epoch.TargetTransport,
                    out _))
            {
                return;
            }

            epoch.LastAnnouncedUtc = DateTimeOffset.UtcNow;
        }

        await SendV6TransportEpochControlAsync(context.SessionId, context.TransferId, epoch).ConfigureAwait(false);
        NotifyRuntimeUnlockPathProbeStarted(context.SessionId, context.TransferId, epoch, "transport_probe_sent");
        await SendV6TransportProbeFrameAsync(context.SessionId, context.TransferId, epoch, dataSession, context.LifetimeCts.Token).ConfigureAwait(false);
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                ReferenceEquals(context.V6TransportEpoch, epoch) &&
                IsV6TransportEpochUnresolved(epoch) &&
                epoch.State == V6TransportEpochState.TargetProofPending)
            {
                if (!TryAuthorizeTransportEpochActionLocked(
                        context,
                        FileTransferCoordinatorActionKind.TransportEpochAdoption,
                        TransportEpochActionKind.Adopt,
                        "v6_transport_epoch_probe",
                        "probe_sent",
                        context.CurrentTransferLeg,
                        ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                        context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                        null,
                        context.RouteSelection.Route,
                        context.NegotiatedDataProtocolVersion,
                        epoch.TargetTransport,
                        out _))
                {
                    return;
                }

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
        ScheduleInboundV6TransportEpochReplay(context, epoch.EpochId, "announce_probe");
    }

    private void ScheduleOutboundV6TransportEpochReplay(OutboundTransferContext context, long epochId, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context) &&
                !context.IsTerminal &&
                context.V6TransportEpoch is { } epoch &&
                epoch.EpochId == epochId &&
                IsV6TransportEpochUnresolved(epoch) &&
                context.V6TransportEpochReplayLoopEpochId != epochId)
            {
                context.V6TransportEpochReplayLoopEpochId = epochId;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            _ = RunOutboundV6TransportEpochReplayAsync(context, epochId, reason);
        }
    }

    private void ScheduleInboundV6TransportEpochReplay(InboundTransferContext context, long epochId, string reason)
    {
        var shouldStart = false;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.V6TransportEpoch is { } epoch &&
                epoch.EpochId == epochId &&
                IsV6TransportEpochUnresolved(epoch) &&
                context.V6TransportEpochReplayLoopEpochId != epochId)
            {
                context.V6TransportEpochReplayLoopEpochId = epochId;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            _ = RunInboundV6TransportEpochReplayAsync(context, epochId, reason);
        }
    }

    private async Task RunOutboundV6TransportEpochReplayAsync(OutboundTransferContext context, long epochId, string reason)
    {
        var transferId = context.TransferId;
        var sessionId = context.SessionId;
        var retryIndex = 0;
        try
        {
            while (true)
            {
                var delayMs = retryIndex < PullTransportRebindRetryDelaysMs.Length
                    ? PullTransportRebindRetryDelaysMs[retryIndex++]
                    : 5000;
                await Task.Delay(delayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                V6TransportEpochState state;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) ||
                        context.IsTerminal ||
                        context.V6TransportEpoch is not { } epoch ||
                        epoch.EpochId != epochId ||
                        !IsV6TransportEpochUnresolved(epoch))
                    {
                        if (context.V6TransportEpochReplayLoopEpochId == epochId)
                        {
                            context.V6TransportEpochReplayLoopEpochId = 0;
                        }

                        return;
                    }

                    state = epoch.State;
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_epoch_replay; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epochId}; reason={FormatProtocolLogValue(reason)}; retry_delay_ms={delayMs}; state={FormatV6TransportEpochState(state)}");
                await AnnounceAndProbeOutboundV6TransportEpochAsync(context).ConfigureAwait(false);
                SignalOutboundSparseSenderPump(context);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    context.V6TransportEpochReplayLoopEpochId == epochId)
                {
                    context.V6TransportEpochReplayLoopEpochId = 0;
                }
            }
        }
    }

    private async Task RunInboundV6TransportEpochReplayAsync(InboundTransferContext context, long epochId, string reason)
    {
        var transferId = context.TransferId;
        var sessionId = context.SessionId;
        var retryIndex = 0;
        try
        {
            while (true)
            {
                var delayMs = retryIndex < PullTransportRebindRetryDelaysMs.Length
                    ? PullTransportRebindRetryDelaysMs[retryIndex++]
                    : 5000;
                await Task.Delay(delayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                V6TransportEpochState state;
                lock (gate)
                {
                    if (!ReferenceEquals(inboundTransfer, context) ||
                        context.IsTerminal ||
                        context.V6TransportEpoch is not { } epoch ||
                        epoch.EpochId != epochId ||
                        !IsV6TransportEpochUnresolved(epoch))
                    {
                        if (context.V6TransportEpochReplayLoopEpochId == epochId)
                        {
                            context.V6TransportEpochReplayLoopEpochId = 0;
                        }

                        return;
                    }

                    state = epoch.State;
                }

                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_epoch_replay; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transport_epoch={epochId}; reason={FormatProtocolLogValue(reason)}; retry_delay_ms={delayMs}; state={FormatV6TransportEpochState(state)}");
                await AnnounceAndProbeInboundV6TransportEpochAsync(context).ConfigureAwait(false);
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
                    context.V6TransportEpochReplayLoopEpochId == epochId)
                {
                    context.V6TransportEpochReplayLoopEpochId = 0;
                }
            }
        }
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

    private Task HandleReceivedV6TransportProbeFrameAsync(
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
            return Task.CompletedTask;
        }

        _ = SendV6TransportProbeAckAsync(sessionId, transferId, direction, frame, targetTransport);
        return Task.CompletedTask;
    }

    private async Task SendV6TransportProbeAckAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferTransportProbeFrameV6 frame,
        FileTransferTransportKind targetTransport)
    {
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
            using var timeoutCts = new CancellationTokenSource();
            var timeout = ResolveV6TransportProbeAckSendTimeout();
            var sendTask = currentTransport.SendFileTransferTransportProbeAsync(ack, timeoutCts.Token);
            var completedTask = await Task.WhenAny(sendTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, sendTask))
            {
                timeoutCts.Cancel();
                _ = sendTask.ContinueWith(
                    static task =>
                    {
                        _ = task.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_v6_transport_probe_ack_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; error=timeout; timeout_ms={(long)timeout.TotalMilliseconds}");
                return;
            }

            await sendTask.ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_ack_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; probe_id={FormatProtocolLogValue(frame.ProbeId)}; target_transport={FormatFileTransferTransportKind(targetTransport)}");
        }
        catch (OperationCanceledException)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_transport_probe_ack_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; transport_epoch={frame.TransportEpoch}; error=canceled");
        }
        catch (Exception ex)
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
                if (outbound.V6TransportEpoch?.EpochId == message.TransportEpoch &&
                    previousEpochId != message.TransportEpoch)
                {
                    outboundToProbe = outbound;
                }
            }

            if (IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                var previousEpochId = inbound.V6TransportEpoch?.EpochId;
                AdoptInboundV6TransportEpochLocked(inbound, message);
                if (inbound.V6TransportEpoch?.EpochId == message.TransportEpoch &&
                    previousEpochId != message.TransportEpoch)
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
        var reason = NormalizeReason(message.Reason) ?? "peer_transport_epoch";
        var normalizedTarget = NormalizeV6TargetTransport(kind, target);
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

            if (!TryAuthorizeTransportEpochActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    TransportEpochActionKind.Adopt,
                    "peer_transport_epoch",
                    reason,
                    context.CurrentTransferLeg,
                    null,
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    context.RouteSelection.Route,
                    context.NegotiatedDataProtocolVersion,
                    normalizedTarget,
                    out _))
            {
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Outbound, context.TransferId, context.SessionId, current, "superseded_by_peer_epoch");
        }
        else if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.Adopt,
                "peer_transport_epoch",
                reason,
                context.CurrentTransferLeg,
                null,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                normalizedTarget,
                out _))
        {
            return;
        }

        var senderLocalAvailabilityUntilExclusive = ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context);
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
            StartingHighestObservedChunkIndex = Math.Max(-1, senderLocalAvailabilityUntilExclusive - 1),
            LastObservedCommittedChunkIndex = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount),
            LastObservedHighestChunkIndex = Math.Max(-1, senderLocalAvailabilityUntilExclusive - 1),
            ProbeId = $"v6-probe:{message.TransportEpoch}:{Guid.NewGuid():N}",
        };
        context.V6TransportEpoch = epoch;
        context.V6PendingEpochRepairRequestIds.Clear();
        context.V6SenderPumpLastWakeReason = "peer_transport_epoch";
        context.V6UseRegularNknRedundantData = false;
        context.V6TunaRedundantDataEpochId = 0;
        context.V6TunaRedundantDataSatisfiedEpochId = 0;
        context.V6TunaRedundantDataProbeStartedUtc = null;
        context.V6TunaRedundantDataProbeStartedBytes = 0;
        context.V6RegularNknRedundantDataEpochId = 0;
        context.V6RegularNknRedundantDataDisabledEpochId = 0;
        context.V6RegularNknRedundantDataBatchCount = 0;
        ClearOutboundV6RequestQueuesForTransportEpochLocked(
            context,
            epoch.EpochId,
            "peer_transport_epoch_adopted");
        AdoptOutboundFallbackLegTransportEpochLocked(context, epoch.EpochId, reason);
        ResolveOutboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
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
        var reason = NormalizeReason(message.Reason) ?? "peer_transport_epoch";
        var normalizedTarget = NormalizeV6TargetTransport(kind, target);
        var adoptionTargetRoute = CanUseV6TransportEpochsLocked(context)
            ? context.RouteSelection.Route
            : FileTransferRoute.PostTunaFallbackV6;
        var adoptionTargetProtocol = adoptionTargetRoute == FileTransferRoute.PostTunaFallbackV6
            ? FileTransferProtocol.ProtocolVersionV6
            : context.NegotiatedDataProtocolVersion;
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
                if (!TryAuthorizeTransportEpochActionLocked(
                        context,
                        FileTransferCoordinatorActionKind.TransportEpochAdoption,
                        TransportEpochActionKind.Adopt,
                        "peer_transport_epoch",
                        reason,
                        context.CurrentTransferLeg,
                        ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, current),
                        context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                        null,
                        adoptionTargetRoute,
                        adoptionTargetProtocol,
                        normalizedTarget,
                        out _))
                {
                    return;
                }

                context.V6ReceiverTransportEpoch = current.EpochId;
                return;
            }

            if (!TryAuthorizeTransportEpochActionLocked(
                    context,
                    FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    TransportEpochActionKind.Adopt,
                    "peer_transport_epoch",
                    reason,
                    context.CurrentTransferLeg,
                    null,
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    adoptionTargetRoute,
                    adoptionTargetProtocol,
                    normalizedTarget,
                    out _))
            {
                return;
            }

            TerminalizeV6TransportEpochLocked(FileTransferDirection.Inbound, context.TransferId, context.SessionId, current, "superseded_by_peer_epoch");
        }
        else if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.Adopt,
                "peer_transport_epoch",
                reason,
                context.CurrentTransferLeg,
                null,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                adoptionTargetRoute,
                adoptionTargetProtocol,
                normalizedTarget,
                out _))
        {
            return;
        }

        if (!CanUseV6TransportEpochsLocked(context) &&
            TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                TransportEpochActionKind.PromotePeerEpoch,
                "peer_transport_epoch",
                "peer_epoch_promotion",
                context.CurrentTransferLeg,
                null,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                FileTransferRoute.PostTunaFallbackV6,
                FileTransferProtocol.ProtocolVersionV6,
                normalizedTarget,
                out _) &&
            (TryPromoteInboundFileTunaV4FallbackToPostTunaV6Locked(context, reason, kind, target) ||
             TryPromoteInboundRegularNknV4FallbackToPostTunaV6Locked(context, reason, kind, target)))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; route={FormatProtocolLogValue(context.RouteSelection.TelemetryToken)}; protocol_version={context.NegotiatedDataProtocolVersion}; handoff_kind={FormatFileTransferTransportHandoffKind(kind)}; target_transport={FormatFileTransferTransportKind(NormalizeV6TargetTransport(kind, target))}; reason={FormatProtocolLogValue(reason)}");
        }

        var epoch = new V6TransportEpoch
        {
            EpochId = message.TransportEpoch,
            Kind = kind,
            SourceTransport = ParseFileTransferTransportKind(message.SourceTransport),
            TargetTransport = target == FileTransferTransportKind.Unknown ? FileTransferTransportKind.RegularNkn : target,
            Direction = FileTransferDirection.Inbound,
            Reason = reason,
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
        AdoptInboundFallbackLegTransportEpochLocked(context, epoch.EpochId, reason);
        ResolveInboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
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

    private static long? ResolveV6TransportEpochAuthorityId(FileTransferLeg? leg, V6TransportEpoch epoch)
        => IsCurrentPostTunaFallbackLeg(leg)
            ? epoch.EpochId
            : leg?.TransportEpochId == epoch.EpochId
                ? epoch.EpochId
                : null;

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
            SignalOutboundSparseSenderPump(outboundRecovered);
        }

        if (inboundRecovered is not null)
        {
            TouchInboundV6PeerLiveness(inboundRecovered, "transport_probe_ack");
            var replayGeneration = 0;
            var shouldScheduleReplay = false;
            lock (gate)
            {
                shouldScheduleReplay = ArmInboundV6PostTunaFallbackProofReplayLocked(
                    inboundRecovered,
                    "transport_probe_ack",
                    out replayGeneration);
            }

            if (shouldScheduleReplay)
            {
                ScheduleInboundV6PostTunaFallbackProofReplay(
                    inboundRecovered,
                    replayGeneration,
                    "transport_probe_ack");
            }

            _ = SendInboundV6ReceiverStateAsync(inboundRecovered, "transport_probe_ack", forceSend: true);
            _ = SendInboundV6FrontierRequestAsync(inboundRecovered, "transport_probe_ack", forceSend: true);
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

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "transport_probe_ack",
                "recover_transport_epoch",
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        NotifyRuntimeUnlockPathProbeResult(
            context.SessionId,
            context.TransferId,
            epoch,
            acked: true,
            "transport_probe_ack");
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

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "transport_probe_ack",
                "recover_transport_epoch",
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        NotifyRuntimeUnlockPathProbeResult(
            context.SessionId,
            context.TransferId,
            epoch,
            acked: true,
            "transport_probe_ack");
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
            SignalOutboundSparseSenderPump(outboundRecovered);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingFallbackTailYieldProofAsync(FileTransferFallbackTailYieldFrameV6 message)
    {
        OutboundTransferContext? outbound = null;
        InboundTransferContext? inbound = null;
        lock (gate)
        {
            if (IsOutboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId))
            {
                outbound = outboundTransfer;
            }

            if (IsInboundLifecycleMessageMatchLocked(message.SessionId, message.TransferId))
            {
                inbound = inboundTransfer;
            }
        }

        if (outbound is not null)
        {
            ApplyOutboundFallbackTailYieldProof(outbound, message);
        }

        if (inbound is not null)
        {
            ApplyInboundFallbackTailYieldProof(inbound, message);
        }

        return Task.CompletedTask;
    }

    private bool TryRecoverOutboundV6TransportEpochFromRepairProofLocked(OutboundTransferContext context, FileTransferRepairProofV6 message)
    {
        var epoch = context.V6TransportEpoch;
        var repairRequestId = message.RepairRequestId?.Trim();
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != message.TransportEpoch ||
            string.IsNullOrWhiteSpace(repairRequestId))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(message.RepairRequestId ?? "(none)")}; current_transport_epoch={epoch?.EpochId ?? 0}; last_repair_request_id={FormatProtocolLogValue(epoch?.LastRepairRequestId ?? "(none)")}; reason=repair_request_mismatch");
            return false;
        }

        var knownRepairRequest =
            string.Equals(epoch.LastRepairRequestId, repairRequestId, StringComparison.Ordinal) ||
            context.V6PendingEpochRepairRequestIds.Contains(repairRequestId);
        var inferredFrontierProof = IsRecoverableUnmatchedV6FrontierRepairProof(context, epoch, message, repairRequestId);
        if (!knownRepairRequest && !inferredFrontierProof)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_ignored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(message.RepairRequestId ?? "(none)")}; current_transport_epoch={epoch.EpochId}; last_repair_request_id={FormatProtocolLogValue(epoch.LastRepairRequestId ?? "(none)")}; reason=repair_request_mismatch");
            return false;
        }

        if (!TryAuthorizeRepairReplayLocked(
                context,
                context.CurrentRuntimeOwner,
                inferredFrontierProof ? RepairReplayKind.UnmatchedFrontierProof : RepairReplayKind.V6Repair,
                "v6_repair_proof",
                repairRequestId,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                out _))
        {
            return false;
        }

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                inferredFrontierProof
                    ? FileTransferCoordinatorActionKind.UnmatchedFrontierProof
                    : FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v6_repair_proof",
                repairRequestId,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        if (!knownRepairRequest)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_repair_proof_unmatched_frontier_accepted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; committed_chunk={message.CommittedChunkIndex}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
        }

        AdvanceOutboundV6RemoteFrontierFromRepairProofLocked(context, message);
        return CompleteOutboundV6TransportEpochLocked(context, "frontier_repair_proof");
    }

    private bool TryRecoverOutboundV6RegularNknEpochFromPeerControlLocked(
        OutboundTransferContext context,
        long transportEpoch,
        FileTransferTransportKind receivedTransportKind,
        string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != transportEpoch ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            receivedTransportKind != FileTransferTransportKind.RegularNkn ||
            (epoch.Kind != FileTransferTransportHandoffKind.RegularNknRecovery &&
             epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback))
        {
            return false;
        }

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v6_peer_control",
                reason,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        return CompleteOutboundV6TransportEpochLocked(context, reason);
    }

    private bool TryRecoverOutboundV6RegularNknEpochFromLegacyV4PeerStateLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state,
        FileTransferTransportKind receivedTransportKind,
        string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.TargetTransport != FileTransferTransportKind.RegularNkn ||
            receivedTransportKind != FileTransferTransportKind.RegularNkn ||
            state.Epoch <= 0 ||
            (epoch.Kind != FileTransferTransportHandoffKind.RegularNknRecovery &&
             epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback))
        {
            return false;
        }

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v4_legacy_state_proof",
                reason,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_legacy_v4_state_proof_accepted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; state_epoch={state.Epoch}; committed_chunk={state.ContiguousCommittedChunkIndex}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
        return CompleteOutboundV6TransportEpochLocked(context, reason);
    }

    private bool TryRecoverInboundV6RegularNknEpochFromChunkProofLocked(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        FileTransferTransportKind receivedTransportKind,
        int observedChunkCount,
        string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.EpochId != batch.TransportEpoch ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            receivedTransportKind != FileTransferTransportKind.RegularNkn ||
            observedChunkCount <= 0 ||
            (epoch.Kind != FileTransferTransportHandoffKind.RegularNknRecovery &&
             epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback))
        {
            return false;
        }

        if (string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
            batch.StartChunkIndex <= context.NextChunkIndex &&
            context.NextChunkIndex < batch.StartChunkIndex + Math.Max(0, batch.ChunkCount))
        {
            return false;
        }

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v6_chunk_proof",
                reason,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_chunk_probe_accepted; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={batch.TransportEpoch}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; observed_chunk_count={observedChunkCount}; committed_chunk={context.NextChunkIndex}; highest_observed_chunk={context.PullHighestReceivedChunkIndex}");
        return CompleteInboundV6TransportEpochLocked(context, reason);
    }

    private bool TryRecoverInboundV6RegularNknEpochFromLegacyV4ChunkProofLocked(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch,
        FileTransferTransportKind receivedTransportKind,
        string reason)
    {
        var epoch = context.V6TransportEpoch;
        var observedChunkCount = Math.Min(Math.Max(0, batch.ChunkCount), batch.DataSegments.Count);
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.TargetTransport != FileTransferTransportKind.RegularNkn ||
            receivedTransportKind != FileTransferTransportKind.RegularNkn ||
            observedChunkCount <= 0 ||
            (epoch.Kind != FileTransferTransportHandoffKind.RegularNknRecovery &&
             epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback))
        {
            return false;
        }

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v4_legacy_chunk_proof",
                reason,
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_regular_nkn_legacy_v4_chunk_probe_accepted; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; start_chunk_index={batch.StartChunkIndex}; batch_chunk_count={batch.ChunkCount}; observed_chunk_count={observedChunkCount}; committed_chunk={context.NextChunkIndex}; highest_observed_chunk={context.PullHighestReceivedChunkIndex}");
        return CompleteInboundV6TransportEpochLocked(context, reason);
    }

    private static bool IsRecoverableUnmatchedV6FrontierRepairProof(
        OutboundTransferContext context,
        V6TransportEpoch epoch,
        FileTransferRepairProofV6 message,
        string repairRequestId)
    {
        if (message.AppliedChunkCount <= 0 ||
            message.CommittedChunkIndex <= context.RemoteNextExpectedChunkIndex)
        {
            return false;
        }

        var epochPrefix = $"v6-frontier:{epoch.EpochId}:";
        var stateEpochPrefix = $"v6-state-frontier:{epoch.EpochId}:";
        var rebindSafetyReplayPrefix = $"transport_rebind_safety_replay:{epoch.EpochId}:";
        return repairRequestId.StartsWith(epochPrefix, StringComparison.Ordinal) ||
               repairRequestId.StartsWith(stateEpochPrefix, StringComparison.Ordinal) ||
               repairRequestId.StartsWith(rebindSafetyReplayPrefix, StringComparison.Ordinal) ||
               repairRequestId.StartsWith(V6RegularNknCheckpointSyncRequestPrefix, StringComparison.Ordinal);
    }

    private void AdvanceOutboundV6RemoteFrontierFromRepairProofLocked(
        OutboundTransferContext context,
        FileTransferRepairProofV6 message)
    {
        if (message.CommittedChunkIndex <= context.RemoteNextExpectedChunkIndex)
        {
            return;
        }

        var previousRemoteFrontier = context.RemoteNextExpectedChunkIndex;
        context.RemoteNextExpectedChunkIndex = Math.Clamp(message.CommittedChunkIndex, 0, context.ChunkCount);
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
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_repair_proof_advanced_remote_frontier; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(message.RepairRequestId ?? "(none)")}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; committed_chunk={context.RemoteNextExpectedChunkIndex}");
    }

    private bool TryCompleteOutboundV6TransportEpochWhenPeerCaughtUpToAcceptedTailLocked(
        OutboundTransferContext context,
        FileTransferStateFrameV4 state)
    {
        if (state is not FileTransferReceiverStateFrameV6 v6State)
        {
            return false;
        }

        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch) ||
            epoch!.Kind != FileTransferTransportHandoffKind.NormalToTunaActivation ||
            epoch.TargetTransport != FileTransferTransportKind.Tuna ||
            v6State.TransportEpoch != epoch.EpochId)
        {
            return false;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acceptedUntil = ResolveOutboundDiagnosticV6EpochSenderTailExclusiveLocked(context, epoch);
        if (acceptedUntil <= 0 ||
            remoteFrontier < acceptedUntil ||
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

        if (!TryAuthorizeTransportEpochActionLocked(
                context,
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                TransportEpochActionKind.RecoverLiveRoute,
                "v6_peer_caught_up",
                "frontier_caught_up_to_accepted_tail",
                context.CurrentTransferLeg,
                ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                context.RouteSelection.Route,
                context.NegotiatedDataProtocolVersion,
                epoch.TargetTransport,
                out _))
        {
            return false;
        }

        if (!CompleteOutboundV6TransportEpochLocked(context, "frontier_caught_up_to_accepted_tail"))
        {
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_tail_caught_up_to_accepted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; remote_next_expected_chunk_index={remoteFrontier}; chunks_accepted_for_transport={acceptedUntil}; durable_received_highest_chunk_index={state.DurableReceivedHighestChunkIndex}; missing_range_count={state.MissingRanges.Count}");
        return true;
    }

    private static int ResolveOutboundDiagnosticV6EpochSenderTailExclusiveLocked(
        OutboundTransferContext context,
        V6TransportEpoch epoch)
    {
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        if (!context.RouteRuntime.UsesDiagnosticRegularNknV6Runtime ||
            epoch.Kind != FileTransferTransportHandoffKind.NormalToTunaActivation ||
            epoch.TargetTransport != FileTransferTransportKind.Tuna)
        {
            return acceptedUntil;
        }

        return Math.Clamp(
            Math.Max(
                Math.Max(acceptedUntil, epoch.StartingHighestObservedChunkIndex + 1),
                ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context)),
            0,
            context.ChunkCount);
    }

    private static int ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(OutboundTransferContext context)
    {
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        var highestChunkIndex = acceptedUntil - 1;
        highestChunkIndex = Math.Max(highestChunkIndex, MaxChunkIndexOrMinusOne(context.SentAwaitingAck.Keys));
        highestChunkIndex = Math.Max(highestChunkIndex, MaxChunkIndexOrMinusOne(context.V6ChunkSendsInFlight.Keys));
        highestChunkIndex = Math.Max(highestChunkIndex, MaxChunkIndexOrMinusOne(context.LastChunkSentUtc.Keys));
        highestChunkIndex = Math.Max(highestChunkIndex, MaxChunkIndexOrMinusOne(context.PullSentChunkCache.Keys));
        return Math.Clamp(highestChunkIndex + 1, 0, context.ChunkCount);
    }

    private static int ResolveOutboundRepairAcceptedUntilExclusiveLocked(OutboundTransferContext context)
    {
        var acceptedUntil = Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            context.CurrentTransferLeg is { CanSendData: true } leg &&
            IsCurrentPostTunaFallbackLeg(leg) &&
            leg.State == FileTransferLegState.RecoveryActive &&
            leg.ProvenHighestObservedChunkIndex >= acceptedUntil)
        {
            var senderAvailableUntil = context.PullSourceCanSeek
                ? context.ChunkCount
                : ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context);
            var provenObservedUntil = Math.Clamp(leg.ProvenHighestObservedChunkIndex + 1, 0, context.ChunkCount);
            var fallbackAuthorityUntil = Math.Min(senderAvailableUntil, provenObservedUntil);
            if (fallbackAuthorityUntil > acceptedUntil)
            {
                return Math.Clamp(fallbackAuthorityUntil, 0, context.ChunkCount);
            }
        }

        if (!context.RouteRuntime.UsesDiagnosticRegularNknV6Runtime)
        {
            return acceptedUntil;
        }

        if (context.V6TransportEpoch is { } epoch &&
            IsV6TransportEpochUnresolved(epoch) &&
            epoch.Kind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
            epoch.TargetTransport == FileTransferTransportKind.Tuna)
        {
            acceptedUntil = Math.Max(acceptedUntil, epoch.StartingHighestObservedChunkIndex + 1);
        }

        return Math.Clamp(
            Math.Max(acceptedUntil, ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context)),
            0,
            context.ChunkCount);
    }

    private static int MaxChunkIndexOrMinusOne(IEnumerable<int> chunkIndices)
    {
        var max = -1;
        foreach (var chunkIndex in chunkIndices)
        {
            if (chunkIndex > max)
            {
                max = chunkIndex;
            }
        }

        return max;
    }

    private static bool ShouldRestoreOutboundDiagnosticV6EpochSenderTailLocked(
        OutboundTransferContext context,
        V6TransportEpoch epoch,
        string reason)
        => context.RouteRuntime.UsesDiagnosticRegularNknV6Runtime &&
           epoch.Kind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
           epoch.TargetTransport == FileTransferTransportKind.Tuna &&
           (string.Equals(reason, "frontier_caught_up_to_accepted_tail", StringComparison.Ordinal) ||
            string.Equals(reason, "frontier_repair_proof", StringComparison.Ordinal));

    private bool CompleteOutboundV6TransportEpochLocked(OutboundTransferContext context, string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return false;
        }

        var restoreDiagnosticSenderTail = ShouldRestoreOutboundDiagnosticV6EpochSenderTailLocked(context, epoch!, reason);
        var diagnosticSenderTailExclusive = restoreDiagnosticSenderTail
            ? ResolveOutboundDiagnosticV6EpochSenderTailExclusiveLocked(context, epoch!)
            : Math.Clamp(context.ChunksAcceptedForTransport, 0, context.ChunkCount);
        var observedHighestChunkIndex = Math.Max(-1, context.ChunksAcceptedForTransport - 1);
        if (restoreDiagnosticSenderTail)
        {
            observedHighestChunkIndex = Math.Max(observedHighestChunkIndex, diagnosticSenderTailExclusive - 1);
        }

        var discardedV4RepairFrameCount = context.PullV4SenderPumpRepairQueue.Count;
        var discardedV4RepairChunkCount = context.PullV4SenderPumpRepairQueue.Sum(static repair => repair.ChunkIndices.Count);
        var discardedV6NormalRequestCount = context.V6NormalRequestedChunks.Count;
        var discardedV6PriorityRequestCount = context.V6PriorityRequestedChunks.Count;
        TrySetV6TransportEpochStateLocked(
            epoch,
            context.TransferId,
            context.SessionId,
            V6TransportEpochState.Recovered,
            reason,
            context.RemoteNextExpectedChunkIndex,
            observedHighestChunkIndex);
        context.LastRecoveredV6TransportEpoch = Math.Max(context.LastRecoveredV6TransportEpoch, epoch!.EpochId);
        context.LastRecoveredV6TransportLiveRouteEpochId = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
        context.LastRecoveredV6TransportEpochKind = epoch.Kind;
        context.LastRecoveredV6TransportTargetTransport = epoch.TargetTransport;
        context.V6TransportEpoch = null;
        context.V6PendingEpochRepairRequestIds.Clear();
        context.PullV4SenderPumpRepairQueue.Clear();
        context.PullV4SenderPumpRepairQueuedChunkIndices.Clear();
        foreach (var repairState in context.PullV4SenderPumpRepairRequests.Values)
        {
            repairState.Queued = false;
            repairState.QueuedUtc = null;
            repairState.InFlight = false;
        }

        context.V4SenderCreditExhaustedSinceUtc = null;
        context.PullSenderFeedCreditWaitStartedUtc = null;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        ClearOutboundFallbackCheckpointDeliveryRecoveryPendingLocked(
            context,
            $"transport_epoch_completed_{reason}");
        if (epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
            IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            var leg = context.CurrentTransferLeg!;
            var checkpointRequestId = leg.CheckpointRequestId;
            if (!string.IsNullOrWhiteSpace(checkpointRequestId))
            {
                RememberRetiredFallbackCheckpointRequest(
                    context.RetiredFallbackCheckpointRequestKeys,
                    leg,
                    epoch.EpochId,
                    checkpointRequestId);
            }

            var acceptedReason = $"transport_epoch_completed_{reason}";
            if (TryAcceptOutboundFallbackCheckpointWithCoordinatorLocked(
                    context,
                    leg,
                    epoch.EpochId,
                    Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    observedHighestChunkIndex,
                    checkpointRequestId,
                    acceptedReason))
            {
                ClearOutboundFallbackCheckpointRetryStateLocked(
                    context,
                    checkpointRequestId,
                    acceptedReason);
                LogFileTransferFallbackCheckpointAccepted(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    checkpointRequestId ?? "(none)",
                    acceptedReason);
                LogFileTransferFallbackLegAuthorityCheckpointAccepted(
                    FileTransferDirection.Outbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    checkpointRequestId,
                    acceptedReason);
            }
        }

        context.PullTransportRebindGeneration = 0;
        context.PullTransportLastSafetyReplayGeneration = 0;
        context.PullTransportLastSafetyReplayFrontierChunkIndex = -1;
        context.PullTransportLastSafetyReplayEndChunkIndex = -1;
        context.PullTransportLastSafetyReplayUtc = null;
        context.PullTransportSafetyReplayRearmCount = 0;
        context.PullTransportFrontierOnlyRepairActive = false;
        context.PullTransportFrontierOnlyRepairStartChunkIndex = -1;
        TryRetireOutboundV6TransportHandoffAfterEpochRecoveryLocked(
            context,
            epoch!,
            reason,
            context.RemoteNextExpectedChunkIndex,
            observedHighestChunkIndex);
        if (restoreDiagnosticSenderTail &&
            diagnosticSenderTailExclusive > context.ChunksAcceptedForTransport)
        {
            var previousAccepted = context.ChunksAcceptedForTransport;
            context.ChunksAcceptedForTransport = diagnosticSenderTailExclusive;
            context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                ? context.FileSizeBytes
                : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * Math.Max(1, context.ChunkSizeBytes));
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_epoch_sender_tail_restored; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; previous_chunks_accepted_for_transport={previousAccepted}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; epoch_starting_highest_observed_chunk={epoch.StartingHighestObservedChunkIndex}");
        }

        context.V6SenderPumpLastWakeReason = "transport_epoch_recovered";
        context.SparseSenderPumpLastWakeReason = "transport_epoch_recovered";
        context.StatusMessage = GetOutboundResumeStatusMessage(context.State);
        context.SignalSparseSenderPump();
        if (epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
                context,
                "transport_epoch_live_route_epoch_recovered",
                reason);
        }
        else if (epoch.Kind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                 epoch.TargetTransport == FileTransferTransportKind.Tuna)
        {
            if (!TryPromoteOutboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    epoch.Kind,
                    epoch.TargetTransport))
            {
                TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    epoch.Kind,
                    epoch.TargetTransport);
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_epoch_tail_unblocked; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch!.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={context.RemoteNextExpectedChunkIndex}; highest_observed_chunk={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; discarded_v4_repair_frame_count={discardedV4RepairFrameCount}; discarded_v4_repair_chunk_count={discardedV4RepairChunkCount}; discarded_v6_normal_request_count={discardedV6NormalRequestCount}; discarded_v6_priority_request_count={discardedV6PriorityRequestCount}");
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
        context.LastRecoveredV6TransportLiveRouteEpochId = context.CurrentLiveRouteEpoch?.EpochId ?? 0;
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
        TryRetireInboundV6TransportHandoffAfterEpochRecoveryLocked(
            context,
            epoch!,
            reason,
            context.NextChunkIndex,
            context.PullHighestReceivedChunkIndex);
        if (epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
            IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            var leg = context.CurrentTransferLeg!;
            var checkpointRequestId = leg.CheckpointRequestId;
            if (!string.IsNullOrWhiteSpace(checkpointRequestId))
            {
                RememberRetiredFallbackCheckpointRequest(
                    context.RetiredFallbackCheckpointRequestKeys,
                    leg,
                    epoch.EpochId,
                    checkpointRequestId);
            }

            var acceptedReason = $"transport_epoch_completed_{reason}";
            if (TryAcceptInboundFallbackCheckpointWithCoordinatorLocked(
                    context,
                    leg,
                    epoch.EpochId,
                    Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                    context.PullHighestReceivedChunkIndex,
                    checkpointRequestId,
                    acceptedReason))
            {
                LogFileTransferFallbackCheckpointAccepted(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    checkpointRequestId ?? "(none)",
                    acceptedReason);
                LogFileTransferFallbackLegAuthorityCheckpointAccepted(
                    FileTransferDirection.Inbound,
                    context.TransferId,
                    context.SessionId,
                    leg,
                    checkpointRequestId,
                    acceptedReason);
            }
        }

        context.PullTransportRebindGeneration = 0;
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);
        if (epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn)
        {
            TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
                context,
                "transport_epoch_live_route_epoch_recovered",
                reason);
        }
        else if (epoch.Kind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                 epoch.TargetTransport == FileTransferTransportKind.Tuna)
        {
            if (!TryPromoteInboundRegularNknV4ToFileTunaV4Locked(
                    context,
                    reason,
                    epoch.Kind,
                    epoch.TargetTransport))
            {
                TryPromoteInboundPostTunaFallbackV6ToFileTunaV4Locked(
                    context,
                    reason,
                    epoch.Kind,
                    epoch.TargetTransport);
            }
        }

        return true;
    }

    private static bool TryRetireOutboundV6TransportHandoffAfterEpochRecoveryLocked(
        OutboundTransferContext context,
        V6TransportEpoch epoch,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        var handoff = context.V6TransportHandoff;
        if (!IsMatchingV6TransportHandoffForRecoveredEpoch(handoff, epoch))
        {
            return false;
        }

        TrySetV6TransportHandoffState(
            handoff,
            FileTransferDirection.Outbound,
            context.TransferId,
            context.SessionId,
            V6TransportHandoffState.Recovered,
            $"transport_epoch_completed_{reason}",
            committedChunkIndex,
            highestObservedChunkIndex);
        context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
            context.LastRecoveredV6TransportHandoffEpoch,
            handoff!.EpochId);
        context.V6TransportHandoff = null;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_legacy_handoff_retired_by_transport_epoch; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        return true;
    }

    private static bool TryRetireInboundV6TransportHandoffAfterEpochRecoveryLocked(
        InboundTransferContext context,
        V6TransportEpoch epoch,
        string reason,
        int committedChunkIndex,
        int highestObservedChunkIndex)
    {
        var handoff = context.V6TransportHandoff;
        if (!IsMatchingV6TransportHandoffForRecoveredEpoch(handoff, epoch))
        {
            return false;
        }

        TrySetV6TransportHandoffState(
            handoff,
            FileTransferDirection.Inbound,
            context.TransferId,
            context.SessionId,
            V6TransportHandoffState.Recovered,
            $"transport_epoch_completed_{reason}",
            committedChunkIndex,
            highestObservedChunkIndex);
        context.LastRecoveredV6TransportHandoffEpoch = Math.Max(
            context.LastRecoveredV6TransportHandoffEpoch,
            handoff!.EpochId);
        context.V6TransportHandoff = null;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_legacy_handoff_retired_by_transport_epoch; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch.EpochId}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={committedChunkIndex}; highest_observed_chunk={highestObservedChunkIndex}");
        return true;
    }

    private static bool IsMatchingV6TransportHandoffForRecoveredEpoch(
        TransportHandoffEpoch? handoff,
        V6TransportEpoch epoch)
        => handoff is not null &&
           handoff.EpochId == epoch.EpochId &&
           handoff.Kind == epoch.Kind &&
           handoff.TargetTransport == epoch.TargetTransport;

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
                    if (!TryAuthorizeTransportEpochActionLocked(
                            context,
                            FileTransferCoordinatorActionKind.TransportEpochAdoption,
                            TransportEpochActionKind.Adopt,
                            "v6_transport_epoch_timeout",
                            "proof_timeout",
                            context.CurrentTransferLeg,
                            ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                            context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                            null,
                            context.RouteSelection.Route,
                            context.NegotiatedDataProtocolVersion,
                            epoch.TargetTransport,
                            out _))
                    {
                        return;
                    }

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
                    if (!TryAuthorizeTransportEpochActionLocked(
                            context,
                            FileTransferCoordinatorActionKind.TransportEpochAdoption,
                            TransportEpochActionKind.Adopt,
                            "v6_transport_epoch_timeout",
                            "proof_timeout",
                            context.CurrentTransferLeg,
                            ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                            context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                            null,
                            context.RouteSelection.Route,
                            context.NegotiatedDataProtocolVersion,
                            epoch.TargetTransport,
                            out _))
                    {
                        return;
                    }

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
            if (!metadata.Priority ||
                !string.Equals(metadata.PriorityName, "frontier", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (chunkIndex == context.RemoteNextExpectedChunkIndex)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(metadata.RepairRequestId);
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
        bool inferredRepairRequestId = false;
        lock (gate)
        {
            var epoch = context.V6TransportEpoch;
            var repairRequestId = batch.RepairRequestId?.Trim();
            var batchCoversPreviousFrontier =
                batch.StartChunkIndex <= previousCommittedChunkIndex &&
                previousCommittedChunkIndex < batch.StartChunkIndex + Math.Max(0, batch.ChunkCount);
            if (string.IsNullOrWhiteSpace(repairRequestId) &&
                batchCoversPreviousFrontier &&
                !string.IsNullOrWhiteSpace(context.V6LastFrontierRequestId))
            {
                repairRequestId = context.V6LastFrontierRequestId;
                inferredRepairRequestId = true;
            }

            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                !IsV6TransportEpochUnresolved(epoch) ||
                batch.TransportEpoch != epoch!.EpochId ||
                receivedTransportKind == FileTransferTransportKind.Unknown ||
                receivedTransportKind != epoch.TargetTransport ||
                string.IsNullOrWhiteSpace(repairRequestId) ||
                !batchCoversPreviousFrontier ||
                committedChunkIndex <= previousCommittedChunkIndex)
            {
                return;
            }

            if (!TryAuthorizeRepairReplayLocked(
                    context,
                    context.CurrentRuntimeOwner,
                    inferredRepairRequestId ? RepairReplayKind.UnmatchedFrontierProof : RepairReplayKind.V6Repair,
                    "v6_chunk_repair_proof",
                    repairRequestId,
                    context.CurrentTransferLeg,
                    ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    out _) ||
                !TryAuthorizeTransportEpochActionLocked(
                    context,
                    inferredRepairRequestId
                        ? FileTransferCoordinatorActionKind.UnmatchedFrontierProof
                        : FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    TransportEpochActionKind.RecoverLiveRoute,
                    "v6_chunk_repair_proof",
                    repairRequestId,
                    context.CurrentTransferLeg,
                    ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    context.RouteSelection.Route,
                    context.NegotiatedDataProtocolVersion,
                    epoch.TargetTransport,
                    out _))
            {
                return;
            }

            epoch.LastRepairRequestId = repairRequestId;
            proof = new FileTransferRepairProofV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = epoch.EpochId,
                RepairRequestId = repairRequestId,
                AppliedChunkCount = appliedChunkCount,
                CommittedChunkIndex = committedChunkIndex,
                RecoveryMode = FormatV6TransportEpochState(epoch.State),
            };
            if (inferredRepairRequestId)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_repair_proof_inferred; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={proof.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; batch_start_chunk_index={batch.StartChunkIndex}; previous_committed_chunk_index={previousCommittedChunkIndex}; committed_chunk={committedChunkIndex}");
            }

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
