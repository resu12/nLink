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

        var snapshot = new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                epoch.Direction,
                epoch.EpochId,
                epoch.Kind,
                epoch.SourceTransport,
                epoch.TargetTransport,
                epoch.State,
                epoch.TerminalReason ?? epoch.Reason,
                IsV6TransportEpochUnresolved(epoch))
            {
                RecoveryBoundaryGeneration = epoch.RecoveryBoundaryGeneration,
            };
        try
        {
            observer.ObserveFileTransferV6TransportEpoch(snapshot);
        }
        finally
        {
            // Infra may re-enter Core only after this observer invocation has returned.
            snapshot.CompleteObservation();
        }
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

    private bool StartOutboundRuntimeUnlockPreCommitProbe(
        OutboundTransferContext context,
        RuntimeUnlockAdmissionToken admissionToken,
        string reason,
        out string rejectionReason,
        out long drainGeneration,
        RuntimeUnlockPendingPreCommitProbeRequest? pendingReplay = null,
        RuntimeUnlockRouteCommitSnapshot? acceptedSnapshot = null)
    {
        rejectionReason = "accepted";
        drainGeneration = 0;
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
            rejectionReason = admissionRejectionReason;
            return false;
        }

        if (!TryCaptureRuntimeOwnerLocked(
                context,
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                "runtime_unlock_precommit_probe",
                "start_probe",
                out var runtimeOwner))
        {
            rejectionReason = "runtime_owner_not_current";
            return false;
        }

        if (acceptedSnapshot is { LaunchGeneration: > 0 } launchSnapshot)
        {
            if (transport is not IRuntimeUnlockRouteCommitProofProvider ||
                !TryValidateRuntimeUnlockPreCommitProbeSnapshotForContext(
                    launchSnapshot,
                    context.SessionId,
                    context.TransferId,
                    out rejectionReason) ||
                string.IsNullOrWhiteSpace(launchSnapshot.TransferId) ||
                launchSnapshot.LaunchGeneration <= 0 ||
                !IsRuntimeUnlockPreCommitProbeSnapshotUsable(launchSnapshot))
            {
                rejectionReason = rejectionReason == "accepted"
                    ? transport is IRuntimeUnlockRouteCommitProofProvider
                        ? "launch_snapshot_not_usable"
                        : "proof_provider_unavailable"
                    : rejectionReason;
                return false;
            }

            if (context.DataSession is not IFileTransferRuntimeUnlockTunaProbeSender)
            {
                rejectionReason = "runtime_unlock_tuna_probe_sender_unavailable";
                return false;
            }
        }

        if (context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            context.DataSession is not null)
        {
            RuntimeUnlockRouteCommitSnapshot? transitionSnapshot =
                acceptedSnapshot;
            if (transitionSnapshot is null &&
                transport is IRuntimeUnlockRouteCommitProofProvider proofProvider &&
                proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                    context.SessionId,
                    context.TransferId,
                    out var currentSnapshot))
            {
                transitionSnapshot = currentSnapshot;
            }

            if (transitionSnapshot is not { } suppliedSnapshot ||
                !TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked(
                    context,
                    suppliedSnapshot,
                    "runtime_unlock_precommit_probe_launch",
                    out _))
            {
                rejectionReason = "route_commit_send_drain_not_staged";
                return false;
            }

            drainGeneration =
                context.FileTransferRouteTransitionExecution?.Authority
                    ?.TransitionGeneration ?? 0;
        }

        if (context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            context.DataSession is not null)
        {
            RuntimeUnlockRouteCommitSnapshot? transitionSnapshot =
                acceptedSnapshot;
            if (transitionSnapshot is null &&
                transport is IRuntimeUnlockRouteCommitProofProvider proofProvider &&
                proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                    context.SessionId,
                    context.TransferId,
                    out var currentSnapshot))
            {
                transitionSnapshot = currentSnapshot;
            }

            if (transitionSnapshot is not { } suppliedSnapshot ||
                !TryPrepareOutboundPostTunaFallbackToTunaRouteTransitionLocked(
                    context,
                    suppliedSnapshot,
                    "runtime_unlock_precommit_probe_launch",
                    out _) ||
                context.FileTransferRouteTransitionExecution?.Authority is not
                    {
                        Kind:
                            FileTransferRouteTransitionKind
                                .PostTunaFallbackV6ToTunaV4,
                        TransitionGeneration: > 0,
                    } transitionAuthority)
            {
                rejectionReason =
                    "post_fallback_route_commit_send_drain_not_staged";
                return false;
            }

            drainGeneration = transitionAuthority.TransitionGeneration;
        }

        var dataSession = context.DataSession;
        var dataSessionGeneration = context.DataSessionGeneration;
        var sessionId = context.SessionId;
        var transferId = context.TransferId;
        var lifetimeToken = context.LifetimeCts.Token;
        var launchContext = acceptedSnapshot is { LaunchGeneration: > 0 }
            ? context
            : null;
        Task RunProbeAsync()
            => RunRuntimeUnlockPreCommitProbeAsync(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                dataSession,
                runtimeOwner,
                admissionToken,
                pendingReplay,
                acceptedSnapshot,
                launchContext,
                dataSessionGeneration,
                lifetimeToken,
                reason);
        if (acceptedSnapshot is { LaunchGeneration: > 0 })
        {
            _ = Task.Run(RunProbeAsync, CancellationToken.None);
        }
        else
        {
            _ = RunProbeAsync();
        }
        return true;
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
            launchContext: null,
            acceptedDataSessionGeneration: context.DataSessionGeneration,
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
        OutboundTransferContext? launchContext,
        long acceptedDataSessionGeneration,
        CancellationToken ct,
        string reason)
    {
        lock (gate)
        {
            if (acceptedSnapshot is { } launchSnapshot &&
                launchContext is not null &&
                !IsRuntimeUnlockPreCommitProbeLaunchSourceCurrentLocked(
                    launchContext,
                    dataSession,
                    acceptedDataSessionGeneration,
                    launchSnapshot,
                    runtimeOwner,
                    admissionToken,
                    out var launchRejectionReason))
            {
                RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                    launchContext,
                    launchSnapshot,
                    "Invalidated",
                    launchRejectionReason,
                    launchContext.FileTransferRouteTransitionExecution?.Authority
                        ?.TransitionGeneration ?? 0);
                return;
            }

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
                if (acceptedSnapshot is { LaunchGeneration: > 0 } rejectedSnapshot &&
                    launchContext is not null)
                {
                    RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                        launchContext,
                        rejectedSnapshot,
                        "Invalidated",
                        "runtime_owner_authorization_rejected",
                        launchContext.FileTransferRouteTransitionExecution?.Authority
                            ?.TransitionGeneration ?? 0);
                }
                return;
            }
        }

        if (transport is not IRuntimeUnlockRouteCommitProofProvider proofProvider)
        {
            if (dataSession is not null &&
                HasEffectiveFallbackTailPairedReleaseForRuntimeUnlockPreCommit(
                    direction,
                    sessionId,
                    transferId,
                    dataSession,
                    acceptedDataSessionGeneration,
                    runtimeOwner,
                    admissionToken))
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
            if (dataSession is not null &&
                HasEffectiveFallbackTailPairedReleaseForRuntimeUnlockPreCommit(
                    direction,
                    sessionId,
                    transferId,
                    dataSession,
                    acceptedDataSessionGeneration,
                    runtimeOwner,
                    admissionToken))
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
            if (HasEffectiveFallbackTailPairedReleaseForRuntimeUnlockPreCommit(
                    direction,
                    sessionId,
                    transferId,
                    dataSession,
                    acceptedDataSessionGeneration,
                    runtimeOwner,
                    admissionToken))
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
            if (acceptedSnapshot is { } launchSnapshot &&
                launchContext is not null &&
                !IsRuntimeUnlockPreCommitProbeLaunchSourceCurrentLocked(
                    launchContext,
                    dataSession,
                    acceptedDataSessionGeneration,
                    launchSnapshot,
                    runtimeOwner,
                    admissionToken,
                    out var launchRejectionReason))
            {
                RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                    launchContext,
                    launchSnapshot,
                    "Invalidated",
                    launchRejectionReason,
                    launchContext.FileTransferRouteTransitionExecution?.Authority
                        ?.TransitionGeneration ?? 0);
                return;
            }

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
                if (acceptedSnapshot is { LaunchGeneration: > 0 } rejectedSnapshot &&
                    launchContext is not null)
                {
                    RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked(
                        launchContext,
                        rejectedSnapshot,
                        "Invalidated",
                        "runtime_unlock_probe_start_authorization_rejected",
                        launchContext.FileTransferRouteTransitionExecution?.Authority
                            ?.TransitionGeneration ?? 0);
                }
                return;
            }
        }

        if (direction == FileTransferDirection.Outbound && acceptedSnapshot is null)
        {
            lock (gate)
            {
                if (!IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) ||
                    outboundTransfer is not { IsTerminal: false } outbound ||
                    !outbound.RouteRuntime.UsesRegularNknV4FastRuntime ||
                    outbound.NegotiatedDataProtocolVersion !=
                        FileTransferProtocol.ProtocolVersionV4 ||
                    !TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked(
                        outbound,
                        snapshot,
                        "runtime_unlock_precommit_probe_started",
                        out _))
                {
                    return;
                }
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
        LogRuntimeUnlockPreCommitProbeStarted(
            direction,
            transferId,
            sessionId,
            frame,
            snapshot.LaunchGeneration);

        try
        {
            await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAsync(frame, ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={frame.TransactionGeneration}; offer_generation={frame.OfferGeneration}; tuna_path_lease_generation={frame.TunaPathLeaseGeneration}; launch_generation={snapshot.LaunchGeneration}; probe_id={FormatProtocolLogValue(probeId)}; target_transport=tuna; reason={FormatProtocolLogValue(reason)}");
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
                snapshot.LaunchGeneration,
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

            if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                    sessionId,
                    transferId,
                    transportEpoch: 0,
                    probeId,
                    FileTransferTransportKind.Tuna,
                    acked: false,
                    "precommit_probe_send_failed",
                    currentReceiveScope: default,
                    callbackTransactionGeneration:
                        frame.TransactionGeneration,
                    callbackOfferGeneration: frame.OfferGeneration,
                    callbackTunaPathLeaseGeneration:
                        frame.TunaPathLeaseGeneration,
                    expectedLocalTransactionGeneration:
                        frame.TransactionGeneration,
                    expectedLocalOfferGeneration: frame.OfferGeneration,
                    expectedLocalTunaPathLeaseGeneration:
                        frame.TunaPathLeaseGeneration,
                    expectedLocalTunaPathLeaseListenerRunId:
                        snapshot.TunaPathLeaseListenerRunId,
                    expectedLocalPathProbeState:
                        RuntimeUnlockPathProbeState.Started))
            {
                return;
            }
            LogRuntimeUnlockPreCommitProbeFailedForLaunch(
                direction,
                transferId,
                sessionId,
                frame.TransactionGeneration,
                frame.OfferGeneration,
                frame.TunaPathLeaseGeneration,
                probeId,
                ex.GetType().Name,
                snapshot.LaunchGeneration);
            MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                direction,
                sessionId,
                transferId,
                ex.GetType().Name);
        }
    }

    private bool
        HasEffectiveFallbackTailPairedReleaseForRuntimeUnlockPreCommit(
            FileTransferDirection direction,
            string sessionId,
            string transferId,
            IFileTransferDataSession dataSession,
            long dataSessionGeneration,
            RuntimeOwnerToken runtimeOwner,
            RuntimeUnlockAdmissionToken admissionToken)
    {
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                outboundTransfer is { IsTerminal: false } outbound &&
                string.Equals(
                    outbound.SessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    outbound.TransferId,
                    transferId,
                    StringComparison.Ordinal) &&
                ReferenceEquals(outbound.DataSession, dataSession) &&
                outbound.DataSessionGeneration == dataSessionGeneration &&
                outbound.CurrentRuntimeOwner == runtimeOwner)
            {
                var state = CreateCoordinatorStateLocked(outbound);
                return FileTransferCoordinator
                           .IsRuntimeUnlockAdmissionTokenCurrent(
                               state,
                               admissionToken,
                               out _) &&
                       FileTransferCoordinator
                           .HasEffectiveFallbackTailPairedReleaseForRouteTransition(
                               state);
            }

            if (direction == FileTransferDirection.Inbound &&
                inboundTransfer is { IsTerminal: false } inbound &&
                string.Equals(
                    inbound.SessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    inbound.TransferId,
                    transferId,
                    StringComparison.Ordinal) &&
                ReferenceEquals(inbound.DataSession, dataSession) &&
                inbound.DataSessionGeneration == dataSessionGeneration &&
                inbound.CurrentRuntimeOwner == runtimeOwner)
            {
                var state = CreateCoordinatorStateLocked(inbound);
                return FileTransferCoordinator
                           .IsRuntimeUnlockAdmissionTokenCurrent(
                               state,
                               admissionToken,
                               out _) &&
                       FileTransferCoordinator
                           .HasEffectiveFallbackTailPairedReleaseForRouteTransition(
                               state);
            }

            return false;
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
        long launchGeneration,
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
            snapshot.LaunchGeneration != launchGeneration ||
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

        if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                sessionId,
                transferId,
                transportEpoch: 0,
                probeId,
                FileTransferTransportKind.Tuna,
                acked: false,
                "precommit_probe_timeout",
                currentReceiveScope: default,
                callbackTransactionGeneration: transactionGeneration,
                callbackOfferGeneration: offerGeneration,
                callbackTunaPathLeaseGeneration: leaseGeneration,
                expectedLocalTransactionGeneration: transactionGeneration,
                expectedLocalOfferGeneration: offerGeneration,
                expectedLocalTunaPathLeaseGeneration: leaseGeneration,
                expectedLocalTunaPathLeaseListenerRunId:
                    snapshot.TunaPathLeaseListenerRunId,
                expectedLocalPathProbeState:
                    RuntimeUnlockPathProbeState.Started))
        {
            return;
        }
        LogRuntimeUnlockPreCommitProbeFailedForLaunch(
            direction,
            transferId,
            sessionId,
            transactionGeneration,
            offerGeneration,
            leaseGeneration,
            probeId,
            "timeout",
            launchGeneration);
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

    private bool IsRuntimeUnlockPreCommitProbeLaunchSourceCurrentLocked(
        OutboundTransferContext context,
        IFileTransferDataSession? dataSession,
        long dataSessionGeneration,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        RuntimeOwnerToken runtimeOwner,
        RuntimeUnlockAdmissionToken admissionToken,
        out string rejectionReason)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            rejectionReason = context.IsTerminal
                ? "terminal_context"
                : "transfer_context_replaced";
            return false;
        }

        if (dataSession is null ||
            !ReferenceEquals(context.DataSession, dataSession) ||
            context.DataSessionGeneration != dataSessionGeneration)
        {
            rejectionReason = "data_session_replaced";
            return false;
        }

        if (context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4)
        {
            if (context.FileTransferRouteTransitionExecution is not
                    {
                        Authority:
                        {
                            ProofBoundary:
                                FileTransferTunaActivationTransitionProofBoundary
                                    activation,
                        },
                    } execution ||
                !FileTransferRouteCommitProofMatchesSnapshot(
                    activation.AcceptedLaunchBoundary,
                    snapshot))
            {
                rejectionReason = "route_commit_send_drain_replaced";
                return false;
            }

            return IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(
                context,
                execution,
                out rejectionReason);
        }

        var coordinatorState = CreateCoordinatorStateLocked(context);
        if (!FileTransferCoordinator.IsRuntimeOwnerCurrent(
                coordinatorState,
                runtimeOwner,
                out rejectionReason))
        {
            return false;
        }

        return FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
            coordinatorState,
            admissionToken,
            out rejectionReason);
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
        _ = StartOutboundRuntimeUnlockPreCommitProbe(
            context,
            pending.AdmissionToken,
            reason,
            out _,
            out _,
            pending,
            acceptedSnapshot: null);
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

    private Task<bool> TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferDataFrame frame,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession)
        => TryHandleRuntimeUnlockPreCommitProbeDataFrameCoreAsync(
            sessionId,
            transferId,
            direction,
            frame,
            receivedTransportKind,
            dataSession,
            outboundSparseReceiveAuthority: null);

    private Task<bool> TryHandleOutboundSparseRuntimeUnlockPreCommitProbeDataFrameAsync(
        string sessionId,
        string transferId,
        FileTransferDataFrame frame,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession dataSession,
        OutboundTransferContext context,
        OutboundSparsePumpInstall install,
        RuntimeOwnerToken runtimeOwner,
        long dataSessionGeneration,
        long receiveSupervisorGeneration)
        => TryHandleRuntimeUnlockPreCommitProbeDataFrameCoreAsync(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            frame,
            receivedTransportKind,
            dataSession,
            new OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority(
                context,
                install,
                runtimeOwner,
                dataSession,
                dataSessionGeneration,
                receiveSupervisorGeneration));

    private async Task<bool> TryHandleRuntimeUnlockPreCommitProbeDataFrameCoreAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferDataFrame frame,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession,
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority? outboundSparseReceiveAuthority)
    {
        if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority)
        {
            bool receiveAuthorityCurrent;
            string receiveAuthorityRejection;
            lock (gate)
            {
                receiveAuthorityCurrent = IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                    requiredReceiveAuthority,
                    out receiveAuthorityRejection);
            }

            if (!receiveAuthorityCurrent)
            {
                LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                    requiredReceiveAuthority,
                    direction,
                    transferId,
                    sessionId,
                    frame,
                    receivedTransportKind,
                    "precommit_probe_receive",
                    receiveAuthorityRejection);
                return frame is FileTransferRuntimeUnlockPreCommitProbeFrame or
                    FileTransferRuntimeUnlockPreCommitProbeAckFrame;
            }
        }

        switch (frame)
        {
            case FileTransferRuntimeUnlockPreCommitProbeFrame probe:
                await HandleRuntimeUnlockPreCommitProbeAsync(
                        sessionId,
                        transferId,
                        direction,
                        probe,
                        receivedTransportKind,
                        dataSession,
                        outboundSparseReceiveAuthority)
                    .ConfigureAwait(false);
                return true;
            case FileTransferRuntimeUnlockPreCommitProbeAckFrame ack:
                await HandleRuntimeUnlockPreCommitProbeAckAsync(
                        sessionId,
                        transferId,
                        direction,
                        ack,
                        receivedTransportKind,
                        dataSession,
                        outboundSparseReceiveAuthority)
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
        IFileTransferDataSession? dataSession,
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority? outboundSparseReceiveAuthority)
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
        var probeReceivedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var accepted = true;
        var ackReason = RuntimeUnlockPreCommitProbeReceivedReason;
        RuntimeUnlockAdmissionToken? currentAdmissionToken = null;
        RuntimeUnlockPreCommitProbeAdmissionStatus admissionStatus;
        string admissionRejectionReason;
        bool receiveAuthorityRejectedBeforeAck = false;
        lock (gate)
        {
            if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority &&
                !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                    requiredReceiveAuthority,
                    out admissionRejectionReason))
            {
                admissionStatus = RuntimeUnlockPreCommitProbeAdmissionStatus.Rejected;
                receiveAuthorityRejectedBeforeAck = true;
            }
            else
            {
                admissionStatus = EnsureRuntimeUnlockPreCommitProbeAdmissionLocked(
                    direction,
                    sessionId,
                    transferId,
                    out admissionRejectionReason,
                    out currentAdmissionToken);
            }
        }

        if (receiveAuthorityRejectedBeforeAck && outboundSparseReceiveAuthority is { } rejectedReceiveAuthority)
        {
            LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                rejectedReceiveAuthority,
                direction,
                transferId,
                sessionId,
                probe,
                receivedTransportKind,
                "precommit_probe_before_ack",
                admissionRejectionReason);
            return;
        }
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
                    runtimeOwner: outboundSparseReceiveAuthority?.RuntimeOwner,
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
                if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority &&
                    !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                        requiredReceiveAuthority,
                        out var receiveAuthorityRejection))
                {
                    LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                        requiredReceiveAuthority,
                        direction,
                        transferId,
                        sessionId,
                        probe,
                        receivedTransportKind,
                        "precommit_probe_ack_authorization",
                        receiveAuthorityRejection);
                    return;
                }

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
                        runtimeOwner: outboundSparseReceiveAuthority?.RuntimeOwner,
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

        if (outboundSparseReceiveAuthority is { } sendReceiveAuthority &&
            !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                sendReceiveAuthority,
                out var sendReceiveAuthorityRejection))
        {
            LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                sendReceiveAuthority,
                direction,
                transferId,
                sessionId,
                probe,
                receivedTransportKind,
                "precommit_probe_before_ack_send",
                sendReceiveAuthorityRejection);
            return;
        }

        if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer ackAuthorizer)
        {
            ackAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(probe, receivedTransportKind);
        }

        if (outboundSparseReceiveAuthority is { } authorizedReceiveAuthority &&
            !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                authorizedReceiveAuthority,
                out var authorizedReceiveAuthorityRejection))
        {
            LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                authorizedReceiveAuthority,
                direction,
                transferId,
                sessionId,
                probe,
                receivedTransportKind,
                "precommit_probe_after_ack_authorizer",
                authorizedReceiveAuthorityRejection);
            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(ResolveV6TransportProbeAckSendTimeout());
            await tunaProbeSender.SendRuntimeUnlockTunaPreCommitProbeAckAsync(ack, timeoutCts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_precommit_probe_ack_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}");
            if (outboundSparseReceiveAuthority is { } completedSendReceiveAuthority &&
                !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                    completedSendReceiveAuthority,
                    out var completedSendReceiveAuthorityRejection))
            {
                LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                    completedSendReceiveAuthority,
                    direction,
                    transferId,
                    sessionId,
                    probe,
                    receivedTransportKind,
                    "precommit_probe_after_ack_send",
                    completedSendReceiveAuthorityRejection);
                return;
            }

            if (!accepted)
            {
                lock (gate)
                {
                    TrySupersedeInboundFallbackTailRetryCycleAfterRejectedProbeLocked(
                        direction,
                        sessionId,
                        transferId,
                        probe,
                        ackReason);
                }
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_rejected_for_fallback_survival; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; reason={FormatProtocolLogValue(ackReason)}");
                return;
            }

            if (transport is IRuntimeUnlockRouteCommitProofProvider proofProvider)
            {
                if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority)
                {
                    bool receiveAuthorityCurrent;
                    string receiveAuthorityRejection;
                    lock (gate)
                    {
                        receiveAuthorityCurrent = IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                            requiredReceiveAuthority,
                            out receiveAuthorityRejection);
                    }

                    if (!receiveAuthorityCurrent)
                    {
                        LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                            requiredReceiveAuthority,
                            direction,
                            transferId,
                            sessionId,
                            probe,
                            receivedTransportKind,
                            "precommit_probe_after_ack",
                            receiveAuthorityRejection);
                        return;
                    }
                }

                var peerAcceptanceHandled = false;
                RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt?
                    retainedPeerAcceptanceReceipt = null;
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus?
                    retainedPeerAcceptanceStatus = null;
                if (transport is IRuntimeUnlockPeerPreCommitAnswerAcceptanceObserver acceptanceObserver)
                {
                    if (!TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceReceiptLocked(
                            direction,
                            sessionId,
                            transferId,
                            probe,
                            receivedTransportKind,
                            dataSession,
                            currentAdmissionToken,
                            probeReceivedUtcMs,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            out var acceptanceReceipt,
                            out var acceptanceReceiveScope,
                            out var acceptanceReceiptReason))
                    {
                        LocalOperationalLog.Warn(
                            "FileTransferService",
                            $"event=runtime_unlock_peer_precommit_answer_acceptance_observed; status=Rejected; decision_status=Rejected; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; peer_transaction_generation={probe.TransactionGeneration}; peer_offer_generation={probe.OfferGeneration}; peer_tuna_path_lease_generation={probe.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(probe.ProbeId ?? "(none)")}; {FormatRuntimeUnlockPeerPreCommitAnswerAcceptanceScopeForLog(acceptanceReceiveScope)}; decision_reason={FormatProtocolLogValue(acceptanceReceiptReason)}");
                        return;
                    }

                    var acceptanceDecision =
                        acceptanceObserver.ObserveRuntimeUnlockPeerPreCommitAnswerAcceptance(acceptanceReceipt);
                    var acceptanceDecisionStatusRecognized =
                        acceptanceDecision.Status is
                            RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Accepted or
                            RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Idempotent or
                            RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Held or
                            RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Rejected or
                            RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.NotApplicable;
                    var acceptanceDecisionReceiptExact =
                        acceptanceDecision.Receipt == acceptanceReceipt;
                    var effectiveAcceptanceStatus =
                        acceptanceDecisionStatusRecognized &&
                        acceptanceDecisionReceiptExact
                            ? acceptanceDecision.Status ==
                                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus
                                    .NotApplicable
                                ? RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus
                                    .Rejected
                                : acceptanceDecision.Status
                            : RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus
                                .Rejected;
                    var effectiveAcceptanceReason =
                        !acceptanceDecisionReceiptExact
                            ? "runtime_unlock_peer_precommit_answer_acceptance_receipt_mutated"
                            : !acceptanceDecisionStatusRecognized
                                ? "runtime_unlock_peer_precommit_answer_acceptance_status_invalid"
                                : acceptanceDecision.Reason;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=runtime_unlock_peer_precommit_answer_acceptance_observed; status={effectiveAcceptanceStatus}; decision_status={effectiveAcceptanceStatus}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; peer_transaction_generation={probe.TransactionGeneration}; peer_offer_generation={probe.OfferGeneration}; peer_tuna_path_lease_generation={probe.TunaPathLeaseGeneration}; local_transaction_generation={acceptanceDecision.LocalTransactionGeneration}; local_offer_generation={acceptanceDecision.LocalOfferGeneration}; local_tuna_path_lease_generation={acceptanceDecision.LocalTunaPathLeaseGeneration}; pending_answer_ack_generation={acceptanceDecision.PendingAnswerAckGeneration}; launch_generation={acceptanceDecision.LaunchGeneration}; probe_id={FormatProtocolLogValue(probe.ProbeId ?? "(none)")}; probe_frame_fingerprint={FormatProtocolLogValue(acceptanceReceipt.ProbeFrameFingerprint)}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; {FormatRuntimeUnlockPeerPreCommitAnswerAcceptanceScopeForLog(acceptanceReceipt.ReceiveScope)}; decision_reason={FormatProtocolLogValue(effectiveAcceptanceReason)}");
                    if (effectiveAcceptanceStatus ==
                        RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Rejected)
                    {
                        return;
                    }

                    retainedPeerAcceptanceReceipt = acceptanceReceipt;
                    retainedPeerAcceptanceStatus = effectiveAcceptanceStatus;
                    peerAcceptanceHandled = effectiveAcceptanceStatus is
                        RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Accepted or
                        RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Idempotent;
                }

                if (!peerAcceptanceHandled &&
                    ShouldRecordReceivedRuntimeUnlockPreCommitProbeAsTransactionProbe(
                        proofProvider,
                        sessionId,
                        transferId,
                        ack.ProbeId))
                {
                    if (retainedPeerAcceptanceReceipt is { } peerReceipt)
                    {
                        if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                                sessionId,
                                transferId,
                                out var localProbeSnapshot) ||
                            localProbeSnapshot.TunaPathLeaseGeneration <= 0 ||
                            string.IsNullOrWhiteSpace(
                                localProbeSnapshot
                                    .TunaPathLeaseListenerRunId) ||
                            !Enum.TryParse<RuntimeUnlockPathProbeState>(
                                localProbeSnapshot.PathProbeState,
                                ignoreCase: true,
                                out var expectedLocalProbeState) ||
                            !proofProvider.NotifyRuntimeUnlockPathProbeStarted(
                                sessionId,
                                transferId,
                                transportEpoch: 0,
                                ack.ProbeId!,
                                FileTransferTransportKind.Tuna,
                                "precommit_probe_received",
                                peerReceipt.ReceiveScope,
                                peerReceipt.PeerTransactionGeneration,
                                peerReceipt.PeerOfferGeneration,
                                peerReceipt.PeerTunaPathLeaseGeneration,
                                localProbeSnapshot.TransactionGeneration,
                                localProbeSnapshot.OfferGeneration,
                                localProbeSnapshot.TunaPathLeaseGeneration,
                                localProbeSnapshot
                                    .TunaPathLeaseListenerRunId,
                                expectedLocalProbeState))
                        {
                            return;
                        }
                    }
                    else if (!proofProvider.NotifyRuntimeUnlockPathProbeStarted(
                                 sessionId,
                                 transferId,
                                 transportEpoch: 0,
                                 ack.ProbeId!,
                                 FileTransferTransportKind.Tuna,
                                 "precommit_probe_received"))
                    {
                        return;
                    }
                }
                else
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=runtime_unlock_precommit_peer_probe_tracked; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; reason=local_probe_already_started");
                }

                TrackRuntimeUnlockPeerPreCommitProbeLocked(
                    direction,
                    sessionId,
                    transferId,
                    ack,
                    retainedPeerAcceptanceReceipt,
                    retainedPeerAcceptanceStatus);

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

    private bool TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceReceiptLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession dataSession,
        RuntimeUnlockAdmissionToken? attemptedAdmissionToken,
        long receivedUtcMs,
        long probeAckSentUtcMs,
        out RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt receipt,
        out RuntimeUnlockPeerPreCommitAnswerAcceptanceScope receiveScope,
        out string reason)
    {
        receipt = null!;
        receiveScope = default;
        reason =
            "runtime_unlock_peer_precommit_answer_acceptance_scope_not_current";
        if (receivedTransportKind != FileTransferTransportKind.Tuna ||
            string.IsNullOrWhiteSpace(probe.ProbeId) ||
            attemptedAdmissionToken is not { IsDefault: false } attempted)
        {
            return false;
        }

        RuntimeUnlockAdmissionToken currentAdmission;
        long dataSessionGeneration;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                ReferenceEquals(outbound.DataSession, dataSession) &&
                outbound.RuntimeUnlockAdmissionToken is { IsDefault: false } outboundAdmission &&
                outboundAdmission == attempted)
            {
                var state = CreateCoordinatorStateLocked(outbound);
                if (!FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                        state,
                        outboundAdmission,
                        out _) ||
                    !TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                        state,
                        out receiveScope,
                        out reason))
                {
                    if (string.Equals(reason, "current", StringComparison.Ordinal))
                    {
                        reason =
                            "runtime_unlock_peer_precommit_answer_acceptance_scope_not_current";
                    }
                    return false;
                }

                currentAdmission = outboundAdmission;
                dataSessionGeneration = outbound.DataSessionGeneration;
            }
            else if (direction == FileTransferDirection.Inbound &&
                     IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                     inboundTransfer is { IsTerminal: false } inbound &&
                     ReferenceEquals(inbound.DataSession, dataSession) &&
                     inbound.RuntimeUnlockAdmissionToken is { IsDefault: false } inboundAdmission &&
                     inboundAdmission == attempted)
            {
                var state = CreateCoordinatorStateLocked(inbound);
                if (!FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(
                        state,
                        inboundAdmission,
                        out _) ||
                    !TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                        state,
                        out receiveScope,
                        out reason))
                {
                    if (string.Equals(reason, "current", StringComparison.Ordinal))
                    {
                        reason =
                            "runtime_unlock_peer_precommit_answer_acceptance_scope_not_current";
                    }
                    return false;
                }

                currentAdmission = inboundAdmission;
                dataSessionGeneration = inbound.DataSessionGeneration;
            }
            else
            {
                return false;
            }
        }

        string fingerprint;
        try
        {
            fingerprint = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        FileTransferDataFrameCodec.Serialize(probe)))
                .ToLowerInvariant();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            reason = "runtime_unlock_peer_precommit_frame_identity_invalid";
            return false;
        }

        receipt = new RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt(
            sessionId.Trim(),
            transferId.Trim(),
            direction,
            currentAdmission,
            probe.TransactionGeneration,
            probe.OfferGeneration,
            probe.TunaPathLeaseGeneration,
            probe.ProbeId.Trim(),
            fingerprint,
            receivedTransportKind,
            receivedUtcMs,
            probeAckSentUtcMs,
            dataSessionGeneration,
            receiveScope);
        reason = "runtime_unlock_peer_precommit_answer_acceptance_current";
        return true;
    }

    private static bool
        TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            FileTransferCoordinatorState state,
            out RuntimeUnlockPeerPreCommitAnswerAcceptanceScope scope)
        => TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            state,
            out scope,
            out _);

    private static bool
        TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            FileTransferCoordinatorState state,
            out RuntimeUnlockPeerPreCommitAnswerAcceptanceScope scope,
            out string reason)
    {
        scope = default;
        reason =
            "runtime_unlock_peer_precommit_answer_acceptance_scope_not_current";
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentRuntimeUnlockAdmission is not
                { IsDefault: false } admission ||
            state.CurrentLeg is not { } leg ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionGeneration <= 0)
        {
            return false;
        }

        var responderTransitionMode =
            state.Direction == FileTransferDirection.Inbound &&
            state.RouteSelection.Route ==
                FileTransferRoute.RegularNknV4Fast &&
            state.RouteSelection.ProtocolVersion ==
                FileTransferProtocol.ProtocolVersionV4
                ? RuntimeUnlockResponderPeerProbeTransitionMode
                    .InitialRegularV4
                : state.Direction == FileTransferDirection.Inbound &&
                  state.RouteSelection.Route ==
                      FileTransferRoute.PostTunaFallbackV6 &&
                  state.RouteSelection.ProtocolVersion ==
                      FileTransferProtocol.ProtocolVersionV6
                    ? RuntimeUnlockResponderPeerProbeTransitionMode
                        .PostFallbackV6Paired
                    : RuntimeUnlockResponderPeerProbeTransitionMode.None;
        if (responderTransitionMode ==
            RuntimeUnlockResponderPeerProbeTransitionMode.None)
        {
            reason =
                "runtime_unlock_peer_precommit_answer_acceptance_scope_mode_invalid";
            return false;
        }

        scope = new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            owner,
            publication,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId ?? "(none)",
            leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity,
            state.CurrentDataSessionGeneration,
            admission,
            responderTransitionMode);
        if (scope.IsDefault)
        {
            reason =
                "runtime_unlock_peer_precommit_answer_acceptance_scope_mode_invalid";
            return false;
        }

        reason = "runtime_unlock_peer_precommit_answer_acceptance_scope_current";
        return true;
    }

    private static string
        FormatRuntimeUnlockPeerPreCommitAnswerAcceptanceScopeForLog(
            RuntimeUnlockPeerPreCommitAnswerAcceptanceScope scope)
        =>
            $"responder_transition_mode={FormatRuntimeUnlockResponderPeerProbeTransitionMode(scope.ResponderTransitionMode)}; peer_runtime_owner_session_id={FormatProtocolLogValue(scope.RuntimeOwner.SessionId)}; peer_runtime_owner_transfer_id={FormatProtocolLogValue(scope.RuntimeOwner.TransferId)}; peer_runtime_owner_direction={scope.RuntimeOwner.Direction.ToString().ToLowerInvariant()}; peer_runtime_owner_route={scope.RuntimeOwner.Route.ToString().ToLowerInvariant()}; peer_runtime_owner_route_token={FormatProtocolLogValue(scope.RuntimeOwner.RouteToken)}; peer_runtime_owner_protocol_version={scope.RuntimeOwner.ProtocolVersion}; peer_runtime_owner_live_route_epoch={scope.RuntimeOwner.LiveRouteEpoch}; peer_runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; peer_runtime_owner_terminal_generation={scope.RuntimeOwner.TerminalGeneration}; peer_route_publication_session_id={FormatProtocolLogValue(scope.RoutePublication.SessionId)}; peer_route_publication_transfer_id={FormatProtocolLogValue(scope.RoutePublication.TransferId)}; peer_route_publication_direction={scope.RoutePublication.Direction.ToString().ToLowerInvariant()}; peer_route_publication_route={scope.RoutePublication.Route.ToString().ToLowerInvariant()}; peer_route_publication_route_token={FormatProtocolLogValue(scope.RoutePublication.RouteToken)}; peer_route_publication_protocol_version={scope.RoutePublication.ProtocolVersion}; peer_route_publication_live_route_epoch={scope.RoutePublication.LiveRouteEpoch}; peer_route_publication_runtime_generation={scope.RoutePublication.RuntimeGeneration}; peer_route_publication_terminal_generation={scope.RoutePublication.TerminalGeneration}; peer_route_publication_leg_generation={scope.RoutePublication.LegGeneration}; peer_route_publication_commit_generation={scope.RoutePublication.RouteCommitGeneration ?? 0}; peer_route_publication_generation={scope.RoutePublication.PublicationGeneration}; admission_generation={scope.Admission.AdmissionGeneration}; peer_admission_generation={scope.Admission.AdmissionGeneration}; peer_terminal_generation={scope.TerminalGeneration}; peer_leg_generation={scope.LegGeneration}; peer_transport_epoch={scope.TransportEpoch}; peer_bridge_recovery_generation={scope.BridgeRecoveryGeneration}; peer_checkpoint_request_id={FormatProtocolLogValue(scope.CheckpointRequestId)}; peer_checkpoint_generation={scope.CheckpointGeneration}; peer_data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity)}; peer_data_session_generation={scope.DataSessionGeneration}";

    private static string FormatRuntimeUnlockResponderPeerProbeTransitionMode(
        RuntimeUnlockResponderPeerProbeTransitionMode mode)
        => mode switch
        {
            RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 =>
                "initial_regular_v4",
            RuntimeUnlockResponderPeerProbeTransitionMode
                    .PostFallbackV6Paired =>
                "post_fallback_v6_paired",
            _ => "none",
        };

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
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt?
            answerAcceptanceReceipt,
        RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus?
            answerAcceptanceStatus)
    {
        if (string.IsNullOrWhiteSpace(ack.ProbeId) ||
            (answerAcceptanceReceipt is null) !=
                (answerAcceptanceStatus is null) ||
            (direction == FileTransferDirection.Inbound &&
             answerAcceptanceReceipt is null) ||
            answerAcceptanceStatus is { } retainedStatus &&
            retainedStatus is not (
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Accepted or
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Idempotent or
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Held))
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
            DateTimeOffset.UtcNow,
            answerAcceptanceReceipt,
            answerAcceptanceStatus);

        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                outbound.RuntimeUnlockPendingPeerPreCommitProbe =
                    MergeRuntimeUnlockPendingPeerPreCommitProbe(
                        outbound.RuntimeUnlockPendingPeerPreCommitProbe,
                        state);
                return;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                inbound.RuntimeUnlockPendingPeerPreCommitProbe =
                    MergeRuntimeUnlockPendingPeerPreCommitProbe(
                        inbound.RuntimeUnlockPendingPeerPreCommitProbe,
                        state);
            }
        }
    }

    private static RuntimeUnlockPeerPreCommitProbeState
        MergeRuntimeUnlockPendingPeerPreCommitProbe(
            RuntimeUnlockPeerPreCommitProbeState? current,
            RuntimeUnlockPeerPreCommitProbeState candidate)
    {
        if (current is null ||
            current.TransactionGeneration != candidate.TransactionGeneration ||
            current.OfferGeneration != candidate.OfferGeneration ||
            current.TunaPathLeaseGeneration !=
                candidate.TunaPathLeaseGeneration ||
            !string.Equals(
                current.ProbeId,
                candidate.ProbeId,
                StringComparison.Ordinal))
        {
            return candidate;
        }

        if (current.AnswerAcceptanceReceipt is null &&
            candidate.AnswerAcceptanceReceipt is not null)
        {
            return candidate;
        }

        return current;
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
        var currentReceiveScope =
            default(RuntimeUnlockPeerPreCommitAnswerAcceptanceScope);
        var currentReceiveScopeAvailable = false;
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
                currentReceiveScopeAvailable =
                    TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                        CreateCoordinatorStateLocked(inbound),
                        out currentReceiveScope);
            }
        }

        if (pending is null ||
            pending.TransactionGeneration != frame.TransactionGeneration ||
            pending.OfferGeneration != frame.OfferGeneration ||
            pending.TunaPathLeaseGeneration != frame.TunaPathLeaseGeneration ||
            !string.Equals(
                pending.ProbeId,
                frame.ProbeId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (pending.CompletedCommitConfirmation is { } completedConfirmation)
        {
            if (frame is not
                    FileTransferRuntimeUnlockPreCommitProbeAckFrame
                        candidateConfirmation ||
                candidateConfirmation != completedConfirmation)
            {
                return false;
            }
        }

        if (direction != FileTransferDirection.Inbound)
        {
            return true;
        }

        var retainedAcceptanceStatusCurrent =
            pending.AnswerAcceptanceStatus is
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Accepted or
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Idempotent or
                RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Held;
        return currentReceiveScopeAvailable &&
               pending.AnswerAcceptanceReceipt is { } receipt &&
               retainedAcceptanceStatusCurrent &&
               !receipt.ReceiveScope.IsDefault &&
               receipt.Direction == direction &&
               string.Equals(
                   receipt.SessionId,
                   sessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.TransferId,
                   transferId,
                   StringComparison.Ordinal) &&
               receipt.PeerTransactionGeneration ==
                   frame.TransactionGeneration &&
               receipt.PeerOfferGeneration == frame.OfferGeneration &&
               receipt.PeerTunaPathLeaseGeneration ==
                   frame.TunaPathLeaseGeneration &&
               string.Equals(
                   receipt.ProbeId,
                   frame.ProbeId,
                   StringComparison.Ordinal) &&
               receipt.ReceivedTransport == FileTransferTransportKind.Tuna &&
               receipt.ReceivedUtcMs > 0 &&
               receipt.ProbeAckSentUtcMs >= receipt.ReceivedUtcMs &&
               receipt.ProbeFrameFingerprint is { Length: 64 } &&
               receipt.AdmissionToken == currentReceiveScope.Admission &&
               receipt.ReceiveScope == currentReceiveScope &&
               receipt.DataSessionGeneration ==
                   currentReceiveScope.DataSessionGeneration;
    }

    private bool
        TryClassifyRuntimeUnlockCompletedPeerPreCommitProbeConfirmationLocked(
            FileTransferDirection direction,
            string sessionId,
            string transferId,
            FileTransferRuntimeUnlockPreCommitProbeAckFrame confirmation,
            IFileTransferDataSession? dataSession,
            out bool exactConfirmation,
            out bool receiveSourceAndScopeCurrent)
    {
        exactConfirmation = false;
        receiveSourceAndScopeCurrent = false;
        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.RuntimeUnlockPendingPeerPreCommitProbe is
                    {
                        CompletedCommitConfirmation: { } outboundCompleted,
                    } outboundPending &&
                outboundPending.TransactionGeneration ==
                    confirmation.TransactionGeneration &&
                outboundPending.OfferGeneration ==
                    confirmation.OfferGeneration &&
                outboundPending.TunaPathLeaseGeneration ==
                    confirmation.TunaPathLeaseGeneration &&
                string.Equals(
                    outboundPending.ProbeId,
                    confirmation.ProbeId,
                    StringComparison.Ordinal))
            {
                exactConfirmation = outboundCompleted == confirmation;
                receiveSourceAndScopeCurrent =
                    exactConfirmation &&
                    dataSession is not null &&
                    ReferenceEquals(outbound.DataSession, dataSession) &&
                    IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
                        direction,
                        sessionId,
                        transferId,
                        confirmation);
                return true;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound &&
                inbound.RuntimeUnlockPendingPeerPreCommitProbe is
                    {
                        CompletedCommitConfirmation: { } inboundCompleted,
                    } inboundPending &&
                inboundPending.TransactionGeneration ==
                    confirmation.TransactionGeneration &&
                inboundPending.OfferGeneration ==
                    confirmation.OfferGeneration &&
                inboundPending.TunaPathLeaseGeneration ==
                    confirmation.TunaPathLeaseGeneration &&
                string.Equals(
                    inboundPending.ProbeId,
                    confirmation.ProbeId,
                    StringComparison.Ordinal))
            {
                exactConfirmation = inboundCompleted == confirmation;
                receiveSourceAndScopeCurrent =
                    exactConfirmation &&
                    dataSession is not null &&
                    ReferenceEquals(inbound.DataSession, dataSession) &&
                    IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
                        direction,
                        sessionId,
                        transferId,
                        confirmation);
                return true;
            }

            return false;
        }
    }

    private void CompleteRuntimeUnlockPendingPeerPreCommitProbeLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame confirmation)
    {
        if (string.IsNullOrWhiteSpace(confirmation.ProbeId))
        {
            return;
        }

        lock (gate)
        {
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.RuntimeUnlockPendingPeerPreCommitProbe is { } outboundPending &&
                outboundPending.TransactionGeneration == confirmation.TransactionGeneration &&
                outboundPending.OfferGeneration == confirmation.OfferGeneration &&
                outboundPending.TunaPathLeaseGeneration == confirmation.TunaPathLeaseGeneration &&
                string.Equals(outboundPending.ProbeId, confirmation.ProbeId, StringComparison.Ordinal))
            {
                outbound.RuntimeUnlockPendingPeerPreCommitProbe =
                    outboundPending with
                    {
                        CompletedCommitConfirmation = confirmation,
                    };
                return;
            }

            if (direction == FileTransferDirection.Inbound &&
                IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                inboundTransfer is { IsTerminal: false } inbound &&
                inbound.RuntimeUnlockPendingPeerPreCommitProbe is { } inboundPending &&
                inboundPending.TransactionGeneration == confirmation.TransactionGeneration &&
                inboundPending.OfferGeneration == confirmation.OfferGeneration &&
                inboundPending.TunaPathLeaseGeneration == confirmation.TunaPathLeaseGeneration &&
                string.Equals(inboundPending.ProbeId, confirmation.ProbeId, StringComparison.Ordinal))
            {
                inbound.RuntimeUnlockPendingPeerPreCommitProbe =
                    inboundPending with
                    {
                        CompletedCommitConfirmation = confirmation,
                    };
            }
        }
    }

    private async Task HandleRuntimeUnlockPreCommitProbeAckAsync(
        string sessionId,
        string transferId,
        FileTransferDirection direction,
        FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
        FileTransferTransportKind receivedTransportKind,
        IFileTransferDataSession? dataSession,
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority? outboundSparseReceiveAuthority)
    {
        LogRuntimeUnlockPreCommitProbeAckReceiveDispatched(
            direction,
            transferId,
            sessionId,
            ack,
            receivedTransportKind,
            "core_handler_invoked");

        if (TryClassifyRuntimeUnlockCompletedPeerPreCommitProbeConfirmationLocked(
                direction,
                sessionId,
                transferId,
                ack,
                dataSession,
                out var exactCompletedConfirmation,
                out var completedConfirmationReceiveSourceAndScopeCurrent))
        {
            if (exactCompletedConfirmation &&
                completedConfirmationReceiveSourceAndScopeCurrent &&
                receivedTransportKind == FileTransferTransportKind.Tuna)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=runtime_unlock_precommit_probe_commit_confirmation_idempotent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; decision_status=Idempotent; decision_reason=runtime_unlock_precommit_probe_commit_confirmation_already_applied");
                return;
            }

            RuntimeUnlockAdmissionToken? latestAdmissionToken;
            lock (gate)
            {
                _ = TryGetCurrentRuntimeUnlockAdmissionTokenForDirectionLocked(
                    direction,
                    sessionId,
                    transferId,
                    out latestAdmissionToken);
            }

            var completedConfirmationRejectionReason =
                !exactCompletedConfirmation
                    ? "runtime_unlock_precommit_probe_commit_confirmation_semantic_mutation"
                    : receivedTransportKind != FileTransferTransportKind.Tuna
                        ? "not_tuna_transport"
                        : "runtime_unlock_precommit_probe_ack_receive_scope_not_current";
            LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                direction,
                transferId,
                sessionId,
                ack,
                attemptedAdmissionToken: null,
                currentAdmissionToken: latestAdmissionToken,
                decisionReason: completedConfirmationRejectionReason);
            LogRuntimeUnlockPreCommitProbeIgnored(
                direction,
                transferId,
                sessionId,
                ack,
                receivedTransportKind,
                completedConfirmationRejectionReason);
            return;
        }

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
        var isPeerCommitConfirmation =
            IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(ackReason);
        var commitReason = string.Equals(
            ackReason,
            RuntimeUnlockPreCommitProbeCommitConfirmedReason,
            StringComparison.Ordinal)
                ? RuntimeUnlockPreCommitProbeCommitConfirmedReason
                : string.Equals(
                    ackReason,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason,
                    StringComparison.Ordinal)
                    ? RuntimeUnlockPreCommitProbeCommitConfirmedReplyReason
                    : RuntimeUnlockPreCommitProbeAcceptedReason;
        if (isPeerCommitConfirmation &&
            !IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
                direction,
                sessionId,
                transferId,
                ack))
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

            const string peerTupleRejectionReason =
                "runtime_unlock_precommit_probe_ack_peer_tuple_not_current";
            LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                direction,
                transferId,
                sessionId,
                ack,
                attemptedAdmissionToken: null,
                currentAdmissionToken: latestAdmissionToken,
                decisionReason: peerTupleRejectionReason);
            LogRuntimeUnlockPreCommitProbeIgnored(
                direction,
                transferId,
                sessionId,
                ack,
                receivedTransportKind,
                peerTupleRejectionReason);
            return;
        }

        if (!proofProvider.TryGetRuntimeUnlockRouteCommitProof(
                sessionId,
                transferId,
                out var callbackSnapshot) ||
            (!isPeerCommitConfirmation &&
             (callbackSnapshot.TransactionGeneration !=
                  ack.TransactionGeneration ||
              callbackSnapshot.OfferGeneration != ack.OfferGeneration ||
              callbackSnapshot.TunaPathLeaseGeneration !=
                  ack.TunaPathLeaseGeneration)) ||
            (callbackSnapshot.TunaPathLeaseGeneration > 0 &&
             string.IsNullOrWhiteSpace(
                 callbackSnapshot.TunaPathLeaseListenerRunId)) ||
            !Enum.TryParse<RuntimeUnlockPathProbeState>(
                callbackSnapshot.PathProbeState,
                ignoreCase: true,
                out var expectedLocalPathProbeState))
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

            const string callbackTupleRejectionReason =
                "runtime_unlock_precommit_probe_ack_callback_tuple_not_current";
            LogRuntimeUnlockPreCommitProbeAckAuthorityRejected(
                direction,
                transferId,
                sessionId,
                ack,
                attemptedAdmissionToken: null,
                currentAdmissionToken: latestAdmissionToken,
                decisionReason: callbackTupleRejectionReason);
            LogRuntimeUnlockPreCommitProbeIgnored(
                direction,
                transferId,
                sessionId,
                ack,
                receivedTransportKind,
                callbackTupleRejectionReason);
            return;
        }

        var expectedTunaPathLeaseListenerRunId =
            callbackSnapshot.TunaPathLeaseListenerRunId;

        RuntimeUnlockAdmissionToken? currentAdmissionToken;
        bool authorized;
        string ackAuthorizationRejectionReason;
        long currentRouteTransitionLaunchGeneration = 0;
        RuntimeUnlockPeerPreCommitAnswerAcceptanceScope currentReceiveScope =
            default;
        lock (gate)
        {
            if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority &&
                !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                    requiredReceiveAuthority,
                    out ackAuthorizationRejectionReason))
            {
                authorized = false;
                currentAdmissionToken = null;
            }
            else
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
                        runtimeOwner: outboundSparseReceiveAuthority?.RuntimeOwner,
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

            FileTransferRouteTransitionOperation? currentTransition = null;
            if (direction == FileTransferDirection.Outbound &&
                IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                currentTransition = outbound.CoordinatorPersistentState
                    .FileTransferRouteTransition
                    .CurrentOperation;
            }
            else if (direction == FileTransferDirection.Inbound &&
                     IsInboundLifecycleMessageMatchLocked(sessionId, transferId) &&
                     inboundTransfer is { IsTerminal: false } inbound)
            {
                currentTransition = inbound.CoordinatorPersistentState
                    .FileTransferRouteTransition
                    .CurrentOperation;
            }

            if (currentTransition is
                    {
                        Authority.ProofBoundary:
                            FileTransferTunaActivationTransitionProofBoundary
                                activation,
                    } transition &&
                activation.PendingPreCommitProbe.TransactionGeneration ==
                    ack.TransactionGeneration &&
                activation.PendingPreCommitProbe.OfferGeneration ==
                    ack.OfferGeneration &&
                activation.PendingPreCommitProbe.TunaPathLeaseGeneration ==
                    ack.TunaPathLeaseGeneration)
            {
                currentRouteTransitionLaunchGeneration =
                    activation.LaunchGeneration;
            }

            if (authorized &&
                direction == FileTransferDirection.Inbound)
            {
                if (IsInboundLifecycleMessageMatchLocked(
                        sessionId,
                        transferId) &&
                    inboundTransfer is { IsTerminal: false } currentInbound &&
                    dataSession is not null &&
                    ReferenceEquals(currentInbound.DataSession, dataSession) &&
                    TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                        CreateCoordinatorStateLocked(currentInbound),
                        out var capturedReceiveScope) &&
                    (!isPeerCommitConfirmation ||
                     IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
                         direction,
                         sessionId,
                         transferId,
                         ack)))
                {
                    currentReceiveScope = capturedReceiveScope;
                }
                else
                {
                    authorized = false;
                    ackAuthorizationRejectionReason =
                        "runtime_unlock_precommit_probe_ack_receive_scope_not_current";
                }
            }
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

        if (outboundSparseReceiveAuthority is { } authorizedAckReceiveAuthority &&
            !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                authorizedAckReceiveAuthority,
                out var authorizedAckReceiveAuthorityRejection))
        {
            LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                authorizedAckReceiveAuthority,
                direction,
                transferId,
                sessionId,
                ack,
                receivedTransportKind,
                "precommit_probe_ack_after_authorization",
                authorizedAckReceiveAuthorityRejection);
            return;
        }

        if (!ack.Accepted)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=runtime_unlock_tuna_probe_ack_received; transport=tuna; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; accepted=0; reason={FormatProtocolLogValue(NormalizeReason(ack.Reason) ?? "precommit_probe_rejected")}");
            if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                    sessionId,
                    transferId,
                    transportEpoch: 0,
                    ack.ProbeId!,
                    FileTransferTransportKind.Tuna,
                    acked: false,
                    NormalizeReason(ack.Reason) ??
                        "precommit_probe_rejected",
                    currentReceiveScope,
                    ack.TransactionGeneration,
                    ack.OfferGeneration,
                    ack.TunaPathLeaseGeneration,
                    callbackSnapshot.TransactionGeneration,
                    callbackSnapshot.OfferGeneration,
                    callbackSnapshot.TunaPathLeaseGeneration,
                    expectedTunaPathLeaseListenerRunId,
                    expectedLocalPathProbeState))
            {
                return;
            }
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

        if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                sessionId,
                transferId,
                transportEpoch: 0,
                ack.ProbeId!,
                FileTransferTransportKind.Tuna,
                acked: true,
                commitReason,
                currentReceiveScope,
                ack.TransactionGeneration,
                ack.OfferGeneration,
                ack.TunaPathLeaseGeneration,
                callbackSnapshot.TransactionGeneration,
                callbackSnapshot.OfferGeneration,
                callbackSnapshot.TunaPathLeaseGeneration,
                expectedTunaPathLeaseListenerRunId,
                expectedLocalPathProbeState))
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
                "runtime_unlock_precommit_probe_ack_callback_tuple_replaced");
            return;
        }

        if (isPeerCommitConfirmation)
        {
            CompleteRuntimeUnlockPendingPeerPreCommitProbeLocked(
                direction,
                sessionId,
                transferId,
                ack);
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_acked; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={ack.TransactionGeneration}; offer_generation={ack.OfferGeneration}; launch_generation={currentRouteTransitionLaunchGeneration}; tuna_path_lease_generation={ack.TunaPathLeaseGeneration}; probe_id={FormatProtocolLogValue(ack.ProbeId ?? "(none)")}; probe_state=Acked; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}");
        if (!isPeerCommitConfirmation)
        {
            if (dataSession is not IFileTransferRuntimeUnlockTunaProbeSender)
            {
                if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                        sessionId,
                        transferId,
                        transportEpoch: 0,
                        ack.ProbeId!,
                        FileTransferTransportKind.Tuna,
                        acked: false,
                        "runtime_unlock_tuna_probe_sender_unavailable",
                        currentReceiveScope,
                        ack.TransactionGeneration,
                        ack.OfferGeneration,
                        ack.TunaPathLeaseGeneration,
                        callbackSnapshot.TransactionGeneration,
                        callbackSnapshot.OfferGeneration,
                        callbackSnapshot.TunaPathLeaseGeneration,
                        expectedTunaPathLeaseListenerRunId,
                        RuntimeUnlockPathProbeState.Acked))
                {
                    return;
                }
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
            if (outboundSparseReceiveAuthority is { } confirmationReceiveAuthority &&
                !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                    confirmationReceiveAuthority,
                    out var confirmationReceiveAuthorityRejection))
            {
                LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                    confirmationReceiveAuthority,
                    direction,
                    transferId,
                    sessionId,
                    ack,
                    receivedTransportKind,
                    "precommit_probe_before_confirmation_send",
                    confirmationReceiveAuthorityRejection);
                return;
            }

            if (dataSession is IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer confirmationAuthorizer)
            {
                confirmationAuthorizer.AuthorizeRuntimeUnlockPreCommitProbeAck(ack, receivedTransportKind);
            }

            if (outboundSparseReceiveAuthority is { } authorizedConfirmationReceiveAuthority &&
                !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
                    authorizedConfirmationReceiveAuthority,
                    out var authorizedConfirmationReceiveAuthorityRejection))
            {
                LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                    authorizedConfirmationReceiveAuthority,
                    direction,
                    transferId,
                    sessionId,
                    ack,
                    receivedTransportKind,
                    "precommit_probe_after_confirmation_authorizer",
                    authorizedConfirmationReceiveAuthorityRejection);
                return;
            }

            var confirmationSent = await SendRuntimeUnlockPreCommitProbeCommitConfirmationAsync(
                    direction,
                    sessionId,
                    transferId,
                    ack,
                    dataSession,
                    RuntimeUnlockPreCommitProbeCommitConfirmedReason)
                .ConfigureAwait(false);
            if (outboundSparseReceiveAuthority is { } requiredReceiveAuthority)
            {
                bool receiveAuthorityCurrent;
                string receiveAuthorityRejection;
                lock (gate)
                {
                    receiveAuthorityCurrent = IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                        requiredReceiveAuthority,
                        out receiveAuthorityRejection);
                }

                if (!receiveAuthorityCurrent)
                {
                    LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                        requiredReceiveAuthority,
                        direction,
                        transferId,
                        sessionId,
                        ack,
                        receivedTransportKind,
                        "precommit_probe_confirmation_after_send",
                        receiveAuthorityRejection);
                    return;
                }
            }

            if (!confirmationSent)
            {
                var confirmationFailureAuthorized = false;
                string? confirmationFailureReceiveRejection = null;
                lock (gate)
                {
                    if (outboundSparseReceiveAuthority is { } confirmationFailureReceiveAuthority &&
                        !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                            confirmationFailureReceiveAuthority,
                            out confirmationFailureReceiveRejection))
                    {
                        confirmationFailureAuthorized = false;
                    }
                    else
                    {
                        confirmationFailureAuthorized = TryAuthorizeRuntimeUnlockCallbackActionForDirectionLocked(
                            direction,
                            sessionId,
                            transferId,
                            runtimeOwner: outboundSparseReceiveAuthority?.RuntimeOwner,
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
                            out _);
                    }
                }

                if (confirmationFailureReceiveRejection is not null &&
                    outboundSparseReceiveAuthority is { } rejectedConfirmationFailureReceiveAuthority)
                {
                    LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                        rejectedConfirmationFailureReceiveAuthority,
                        direction,
                        transferId,
                        sessionId,
                        ack,
                        receivedTransportKind,
                        "precommit_probe_confirmation_failure",
                        confirmationFailureReceiveRejection);
                    return;
                }

                if (!confirmationFailureAuthorized)
                {
                    return;
                }

                if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                        sessionId,
                        transferId,
                        transportEpoch: 0,
                        ack.ProbeId!,
                        FileTransferTransportKind.Tuna,
                        acked: false,
                        "runtime_unlock_paired_commit_confirmation_send_failed",
                        currentReceiveScope,
                        ack.TransactionGeneration,
                        ack.OfferGeneration,
                        ack.TunaPathLeaseGeneration,
                        callbackSnapshot.TransactionGeneration,
                        callbackSnapshot.OfferGeneration,
                        callbackSnapshot.TunaPathLeaseGeneration,
                        expectedTunaPathLeaseListenerRunId,
                        RuntimeUnlockPathProbeState.Acked))
                {
                    return;
                }
                MarkRuntimeUnlockPreCommitProbeFailedFallbackResumedLocked(
                    direction,
                    sessionId,
                    transferId,
                    "runtime_unlock_paired_commit_confirmation_send_failed");
                return;
            }

            var initiatorCommitted = false;
            RouteCommitToken initiatorRouteCommitToken = default;
            string? initiatorCommitReceiveRejection = null;
            lock (gate)
            {
                if (outboundSparseReceiveAuthority is { } initiatorCommitReceiveAuthority &&
                    !IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                        initiatorCommitReceiveAuthority,
                        out initiatorCommitReceiveRejection))
                {
                    initiatorCommitted = false;
                }
                else
                {
                    initiatorCommitted = TryCommitRuntimeUnlockRouteAfterPreCommitProbeAck(
                        direction,
                        sessionId,
                        transferId,
                        ack.TransactionGeneration,
                        ack.OfferGeneration,
                        RuntimeUnlockPreCommitProbeCommitConfirmedReason,
                        out initiatorRouteCommitToken);
                }
            }

            if (initiatorCommitReceiveRejection is not null &&
                outboundSparseReceiveAuthority is { } rejectedInitiatorCommitReceiveAuthority)
            {
                LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
                    rejectedInitiatorCommitReceiveAuthority,
                    direction,
                    transferId,
                    sessionId,
                    ack,
                    receivedTransportKind,
                    "precommit_probe_confirmation_commit",
                    initiatorCommitReceiveRejection);
                return;
            }

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

    private void LogOutboundSparseRuntimeUnlockPreCommitProbeReceiveRejected(
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority receiveAuthority,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        FileTransferDataFrame frame,
        FileTransferTransportKind receivedTransportKind,
        string phase,
        string rejectionReason)
    {
        LogOutboundSparsePumpInstallRejected(
            receiveAuthority.Context,
            receiveAuthority.Install,
            phase,
            rejectionReason);
        if (frame is FileTransferRuntimeUnlockPreCommitProbeFrameBase probe)
        {
            LogRuntimeUnlockPreCommitProbeIgnored(
                direction,
                transferId,
                sessionId,
                probe,
                receivedTransportKind,
                $"stale_outbound_sparse_receive_{NormalizeReason(rejectionReason)}");
        }
    }

    private bool IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority receiveAuthority,
        out string rejectionReason)
    {
        if (!IsOutboundSparseReceiveSessionCurrentLocked(
                receiveAuthority.Context,
                receiveAuthority.DataSession,
                receiveAuthority.DataSessionGeneration,
                receiveAuthority.ReceiveSupervisorGeneration,
                out rejectionReason))
        {
            return false;
        }

        return IsOutboundSparseReceiveResultCurrentLocked(
            receiveAuthority.Context,
            receiveAuthority.Install,
            receiveAuthority.RuntimeOwner,
            receiveAuthority.DataSession,
            receiveAuthority.DataSessionGeneration,
            out rejectionReason);
    }

    private bool IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrent(
        OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority receiveAuthority,
        out string rejectionReason)
    {
        lock (gate)
        {
            return IsOutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthorityCurrentLocked(
                receiveAuthority,
                out rejectionReason);
        }
    }

    private sealed record OutboundSparseRuntimeUnlockPreCommitProbeReceiveAuthority(
        OutboundTransferContext Context,
        OutboundSparsePumpInstall Install,
        RuntimeOwnerToken RuntimeOwner,
        IFileTransferDataSession DataSession,
        long DataSessionGeneration,
        long ReceiveSupervisorGeneration);

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

        if (!proofProvider.NotifyRuntimeUnlockPathProbeResult(
                sessionId,
                transferId,
                transportEpoch: 0,
                probeId,
                FileTransferTransportKind.Tuna,
                acked: false,
                "runtime_unlock_paired_commit_timeout",
                currentReceiveScope: default,
                callbackTransactionGeneration: transactionGeneration,
                callbackOfferGeneration: offerGeneration,
                callbackTunaPathLeaseGeneration: leaseGeneration,
                expectedLocalTransactionGeneration: transactionGeneration,
                expectedLocalOfferGeneration: offerGeneration,
                expectedLocalTunaPathLeaseGeneration: leaseGeneration,
                expectedLocalTunaPathLeaseListenerRunId:
                    snapshot.TunaPathLeaseListenerRunId,
                expectedLocalPathProbeState:
                    RuntimeUnlockPathProbeState.Acked))
        {
            return;
        }
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
            IsRuntimeUnlockPreCommitProbeCommitConfirmationReason(
                ackFrame.Reason))
        {
            if (!ackFrame.Accepted)
            {
                reason =
                    "runtime_unlock_precommit_probe_commit_confirmation_semantic_mutation";
                return false;
            }

            if (IsRuntimeUnlockPendingPeerPreCommitProbeMatchLocked(
                    direction,
                    sessionId,
                    transferId,
                    frame))
            {
                return true;
            }

            reason =
                "runtime_unlock_precommit_probe_ack_peer_acceptance_not_current";
            return false;
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

    private static bool TryGetCommittedFileTransferRouteTransitionTokenLocked(
        OutboundTransferContext context,
        long transactionGeneration,
        long offerGeneration,
        out RouteCommitToken routeCommitToken)
    {
        routeCommitToken = default;
        if (context.RuntimeUnlockRouteCommitToken is not
                { IsDefault: false } token ||
            token.TransactionGeneration != transactionGeneration ||
            token.OfferGeneration != offerGeneration ||
            context.CurrentRoutePublicationToken is not
                { IsDefault: false } publication ||
            publication.RouteCommitGeneration != token.CommitGeneration ||
            context.CoordinatorPersistentState.FileTransferRouteTransition
                .LastTerminalOperation is not
                {
                    State: FileTransferRouteTransitionState.Committed,
                    Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                    DispositionCommand.RouteCommitToken: { } committedToken,
                    DispositionCommand.RoutePublicationToken: { } committedPublication,
                } ||
            committedToken != token ||
            committedPublication != publication)
        {
            return false;
        }

        routeCommitToken = token;
        return true;
    }

    private static bool TryGetCommittedFileTransferRouteTransitionTokenLocked(
        InboundTransferContext context,
        long transactionGeneration,
        long offerGeneration,
        out RouteCommitToken routeCommitToken)
    {
        routeCommitToken = default;
        if (context.RuntimeUnlockRouteCommitToken is not
                { IsDefault: false } token ||
            token.TransactionGeneration != transactionGeneration ||
            token.OfferGeneration != offerGeneration ||
            context.CurrentRoutePublicationToken is not
                { IsDefault: false } publication ||
            publication.RouteCommitGeneration != token.CommitGeneration ||
            context.CoordinatorPersistentState.FileTransferRouteTransition
                .LastTerminalOperation is not
                {
                    State: FileTransferRouteTransitionState.Committed,
                    Authority.TargetRoute: FileTransferRoute.FileTunaV4,
                    DispositionCommand.RouteCommitToken: { } committedToken,
                    DispositionCommand.RoutePublicationToken: { } committedPublication,
                } ||
            committedToken != token ||
            committedPublication != publication)
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
                    committed = TryGetCommittedFileTransferRouteTransitionTokenLocked(
                        outbound,
                        transactionGeneration,
                        offerGeneration,
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
                    committed = TryGetCommittedFileTransferRouteTransitionTokenLocked(
                        inbound,
                        transactionGeneration,
                        offerGeneration,
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
                if (FileTransferCoordinator
                    .IsFallbackTailRetryCycleSupersessionPending(
                        CreateCoordinatorStateLocked(outbound)))
                {
                    ResolveOutboundFallbackTailSurvivalLocked(
                        outbound,
                        string.Equals(
                            reason,
                            "runtime_unlock_tuna_probe_sidecar_closed",
                            StringComparison.Ordinal)
                            ? "sidecar_failed_precommit_probe_rollback"
                            : "pressure_rejected_precommit_probe_rollback");
                }
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
        FileTransferRuntimeUnlockPreCommitProbeFrame frame,
        long launchGeneration)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_started; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={frame.TransactionGeneration}; offer_generation={frame.OfferGeneration}; tuna_path_lease_generation={frame.TunaPathLeaseGeneration}; launch_generation={launchGeneration}; probe_id={FormatProtocolLogValue(frame.ProbeId ?? "(none)")}; target_route={FormatProtocolLogValue(frame.TargetRoute)}; target_protocol_version={frame.TargetProtocolVersion}; target_transport={FormatProtocolLogValue(frame.TargetTransport)}; handoff_kind={FormatProtocolLogValue(frame.HandoffKind)}");

    private void LogRuntimeUnlockPreCommitProbeFailed(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string reason)
        => LogRuntimeUnlockPreCommitProbeFailedCore(
            direction,
            transferId,
            sessionId,
            transactionGeneration,
            offerGeneration,
            leaseGeneration,
            probeId,
            reason,
            launchGeneration: 0);

    private void LogRuntimeUnlockPreCommitProbeFailedForLaunch(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string reason,
        long launchGeneration)
        => LogRuntimeUnlockPreCommitProbeFailedCore(
            direction,
            transferId,
            sessionId,
            transactionGeneration,
            offerGeneration,
            leaseGeneration,
            probeId,
            reason,
            launchGeneration);

    private void LogRuntimeUnlockPreCommitProbeFailedCore(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string reason,
        long launchGeneration)
    {
        InvalidateOutboundRegularV4RouteCommitSendDrainForProbeFailure(
            direction,
            transferId,
            sessionId,
            transactionGeneration,
            offerGeneration,
            leaseGeneration,
            probeId,
            reason);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=runtime_unlock_precommit_probe_failed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={FormatProtocolLogValue(transferId)}; session_id={FormatProtocolLogValue(sessionId)}; transaction_generation={transactionGeneration}; offer_generation={offerGeneration}; tuna_path_lease_generation={leaseGeneration}; launch_generation={launchGeneration}; probe_id={FormatProtocolLogValue(probeId)}; reason={FormatProtocolLogValue(reason)}");
    }

    private void InvalidateOutboundRegularV4RouteCommitSendDrainForProbeFailure(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        long transactionGeneration,
        long offerGeneration,
        long leaseGeneration,
        string probeId,
        string reason)
    {
        if (direction != FileTransferDirection.Outbound)
        {
            return;
        }

        lock (gate)
        {
            if (!IsOutboundLifecycleMessageMatchLocked(sessionId, transferId) ||
                outboundTransfer is not { } outbound ||
                outbound.FileTransferRouteTransitionExecution is not
                    {
                        Authority:
                        {
                            ProofBoundary:
                                FileTransferTunaActivationTransitionProofBoundary
                                    activation,
                        },
                    } execution)
            {
                return;
            }

            var proof = activation.AcceptedLaunchBoundary;
            if (proof.TransactionGeneration != transactionGeneration ||
                proof.OfferGeneration != offerGeneration ||
                proof.TunaPathLeaseGeneration != leaseGeneration)
            {
                return;
            }

            var retainedProbeId = NormalizeReason(proof.PathProbeId);
            var failedProbeId = NormalizeReason(probeId);
            if (!string.IsNullOrWhiteSpace(retainedProbeId) &&
                !string.Equals(retainedProbeId, failedProbeId, StringComparison.Ordinal))
            {
                return;
            }

            if (!InvalidateOutboundFileTransferRouteTransitionLocked(
                    outbound,
                    execution.Authority.Value,
                    terminalInvalidation: false,
                    $"precommit_probe_failed_{NormalizeReason(reason) ?? "unknown"}"))
            {
                return;
            }
            var retiredPending = FileTransferCoordinator.Apply(
                RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
                    outbound.RouteSelection,
                    outbound.CurrentTransferLeg,
                    reason,
                    activation.PendingPreCommitProbe),
                CreateCoordinatorStateLocked(outbound));
            if (retiredPending.RuntimeUnlockPendingPreCommitProbeDecision?.Accepted != true)
            {
                return;
            }

            ApplyCoordinatorDecisionToContextLocked(outbound, retiredPending);
            var tailState = CreateCoordinatorStateLocked(outbound);
            var tailOperation = FileTransferCoordinator
                .GetFallbackTailYieldSubstate(tailState)
                .CurrentOperation;
            var supersessionReason = string.Equals(
                    reason,
                    "inbound_fallback_tail_not_yielded",
                    StringComparison.Ordinal)
                ? "pressure_rejected_precommit_probe_rollback"
                : string.Equals(
                    reason,
                    "runtime_unlock_tuna_probe_sidecar_closed",
                    StringComparison.Ordinal)
                    ? "sidecar_failed_precommit_probe_rollback"
                    : null;
            if (supersessionReason is not null &&
                tailOperation is
                {
                    PairedRelease: { } pairedRelease,
                    PairingState:
                        FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                } &&
                tailState.PersistentState.FallbackTailYield
                    .LastConsumingInstallReceipt is { } installReceipt)
            {
                // Retire the exact settled install before releasing the consumed
                // pair. A rejected retirement leaves that pair unavailable for retry.
                if (supersessionReason == "sidecar_failed_precommit_probe_rollback")
                {
                    if (!TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(outbound,
                            FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason))
                    {
                        return;
                    }
                    tailState = CreateCoordinatorStateLocked(outbound);
                }
                var superseded = FileTransferCoordinator
                    .SupersedeFallbackTailPairedYieldAfterRejectedProbe(
                        tailState,
                        FileTransferFallbackTailRetryCycleSupersessionRole.Initiator,
                        tailOperation.Value.Decision,
                        pairedRelease,
                        installReceipt,
                        supersessionReason);
                if (superseded.Accepted)
                {
                    ApplyFallbackTailYieldTransitionToContextLocked(
                        outbound,
                        superseded);
                }
                LogFallbackTailRetryCycleSupersessionDecision(
                    FileTransferDirection.Outbound,
                    outbound.SessionId,
                    outbound.TransferId,
                    tailOperation.Value,
                    installReceipt,
                    execution.Authority.Value,
                    superseded.Status.ToString(),
                    superseded.Reason,
                    reason);
            }
        }
    }

    private void TrySupersedeInboundFallbackTailRetryCycleAfterRejectedProbeLocked(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        string reason)
    {
        if (direction != FileTransferDirection.Inbound ||
            !string.Equals(
                reason,
                "inbound_fallback_tail_not_yielded",
                StringComparison.Ordinal) ||
            !IsInboundLifecycleMessageMatchLocked(sessionId, transferId) ||
            inboundTransfer is not { IsTerminal: false } inbound ||
            !inbound.RouteRuntime.UsesPostTunaFallbackV6Runtime)
        {
            return;
        }

        var state = CreateCoordinatorStateLocked(inbound);
        var operation = FileTransferCoordinator
            .GetFallbackTailYieldSubstate(state)
            .CurrentOperation;
        if (operation is not
            {
                PairedRelease: { } pairedRelease,
                PairingState: FileTransferFallbackTailYieldPairingState.Paired,
            })
        {
            return;
        }

        var superseded = FileTransferCoordinator
            .SupersedeFallbackTailPairedYieldAfterRejectedProbe(
                state,
                FileTransferFallbackTailRetryCycleSupersessionRole.Responder,
                operation.Value.Decision,
                pairedRelease,
                expectedInstallReceipt: null,
                "pressure_rejected_precommit_probe_rollback");
        if (superseded.Accepted)
        {
            ApplyFallbackTailYieldTransitionToContextLocked(
                inbound,
                superseded);
        }
        LogFallbackTailRetryCycleSupersessionDecision(
            FileTransferDirection.Inbound,
            inbound.SessionId,
            inbound.TransferId,
            operation.Value,
            installReceipt: null,
            transitionAuthority: null,
            superseded.Status.ToString(),
            superseded.Reason,
            reason,
            probe.TransactionGeneration,
            probe.OfferGeneration,
            probe.TunaPathLeaseGeneration,
            probe.ProbeId);
    }

    private static void ApplyFallbackTailYieldTransitionToContextLocked(
        OutboundTransferContext context,
        FileTransferFallbackTailYieldTransitionDecision transition)
    {
        var previous = context.CoordinatorPersistentState.FallbackTailYield;
        var state = AdoptCoordinatorStateLocked(context, transition);
        LogFallbackTailYieldAggregateTransitions(
            previous,
            state.PersistentState.FallbackTailYield,
            transition.Reason);
    }

    private static void ApplyFallbackTailYieldTransitionToContextLocked(
        InboundTransferContext context,
        FileTransferFallbackTailYieldTransitionDecision transition)
    {
        var previous = context.CoordinatorPersistentState.FallbackTailYield;
        var state = AdoptCoordinatorStateLocked(context, transition);
        LogFallbackTailYieldAggregateTransitions(
            previous,
            state.PersistentState.FallbackTailYield,
            transition.Reason);
    }

    private static void LogFallbackTailRetryCycleSupersessionDecision(
        FileTransferDirection direction,
        string sessionId,
        string transferId,
        FileTransferFallbackTailYieldOperation operation,
        RuntimeUnlockRetryDispatchInstallReceipt? installReceipt,
        FileTransferRouteTransitionAuthority? transitionAuthority,
        string status,
        string decisionReason,
        string triggerReason,
        long transactionGeneration = 0,
        long offerGeneration = 0,
        long leaseGeneration = 0,
        string? probeId = null)
    {
        var scope = FileTransferCoordinator
            .GetEffectiveFallbackTailYieldScope(operation);
        var release = operation.PairedRelease;
        var peer = release?.PeerProof.Proof;
        var transition = transitionAuthority;
        var launch = (transition?.ProofBoundary as
            FileTransferTunaActivationTransitionProofBoundary)?
            .AcceptedLaunchBoundary;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_tail_retry_cycle_supersession_decided; authority_schema={FileTransferAuthoritySchema.Current}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; direction={direction.ToString().ToLowerInvariant()}; role={(direction == FileTransferDirection.Outbound ? "initiator" : "responder")}; trigger={(string.Equals(triggerReason, "runtime_unlock_tuna_probe_sidecar_closed", StringComparison.Ordinal) ? "sidecar_failed_precommit_probe_rollback" : "pressure_rejected_precommit_probe_rollback")}; authority_id={FormatProtocolLogValue(operation.Decision.AuthorityId.ToString())}; yield_generation={operation.Decision.YieldGeneration}; peer_authority_id={FormatProtocolLogValue(peer?.AuthorityId.ToString() ?? "(none)")}; peer_yield_generation={peer?.YieldGeneration ?? 0}; pairing_generation={release?.PairingGeneration ?? 0}; release_generation={release?.ReleaseGeneration ?? 0}; install_generation={installReceipt?.InstallGeneration ?? 0}; transaction_generation={launch?.TransactionGeneration ?? transactionGeneration}; offer_generation={launch?.OfferGeneration ?? offerGeneration}; tuna_path_lease_generation={launch?.TunaPathLeaseGeneration ?? leaseGeneration}; probe_id={FormatProtocolLogValue(launch?.PathProbeId ?? probeId ?? "(none)")}; transition_authority_id={FormatProtocolLogValue(transition?.AuthorityId.ToString() ?? "(none)")}; transition_generation={transition?.TransitionGeneration ?? 0}; route={FormatProtocolLogValue(scope.RouteToken)}; protocol_version={scope.ProtocolVersion}; live_route_epoch={scope.LiveRouteEpoch}; route_publication_generation={scope.RoutePublication?.PublicationGeneration ?? 0}; route_commit_generation={scope.RoutePublication?.RouteCommitGeneration ?? 0}; runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; terminal_generation={scope.TerminalGeneration}; leg_generation={scope.LegGeneration}; transport_epoch={scope.TransportEpoch}; bridge_recovery_generation={scope.BridgeRecoveryGeneration}; checkpoint_generation={scope.CheckpointGeneration}; checkpoint_request_id={FormatProtocolLogValue(scope.CheckpointRequestIdSnapshot)}; data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity ?? "(none)")}; data_session_generation={scope.DataSessionGeneration}; successor_generation={operation.Decision.YieldGeneration + 1}; status={FormatProtocolLogValue(status)}; reason={FormatProtocolLogValue(decisionReason)}; trigger_reason={FormatProtocolLogValue(triggerReason)}; authority_effect={(string.Equals(status, "Accepted", StringComparison.Ordinal) ? "retired_exact_predecessor" : "none")}");
    }

    private static bool FileTransferRouteCommitProofMatchesSnapshot(
        RuntimeUnlockRouteCommitProof proof,
        RuntimeUnlockRouteCommitSnapshot snapshot)
        => proof == RuntimeUnlockTransaction.CreateRouteCommitProof(
            snapshot,
            proof.AdmissionToken);

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

        var epochId = coordinatorAuthorized && context.CurrentTransferLeg is { TransportEpochId: > 0 } authorizedLeg
            ? authorizedLeg.TransportEpochId
            : Math.Max(
                context.LastRecoveredV6TransportEpoch + 1,
                Math.Max(1, context.PullTransportRebindGeneration));
        var senderLocalAvailabilityUntilExclusive = ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context);
        var epoch = new V6TransportEpoch
        {
            RoutePublicationToken = context.CurrentRoutePublicationToken,
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
            RoutePublicationToken = context.CurrentRoutePublicationToken,
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
            if (TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                    context,
                    epoch.EpochId,
                    "v6_transport_epoch_probe") is null &&
                !TryAuthorizeTransportEpochActionLocked(
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
                if (TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                        context,
                        epoch.EpochId,
                        "v6_transport_epoch_probe") is null &&
                    !TryAuthorizeTransportEpochActionLocked(
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
            if (TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                    context,
                    epoch.EpochId,
                    "v6_transport_epoch_probe") is null &&
                !TryAuthorizeTransportEpochActionLocked(
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
                if (TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                        context,
                        epoch.EpochId,
                        "v6_transport_epoch_probe") is null &&
                    !TryAuthorizeTransportEpochActionLocked(
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
        if (frame.ProbeId.StartsWith(
                "receive-recovery-proof:",
                StringComparison.Ordinal))
        {
            InboundTransferContext? inboundToPublish = null;
            lock (gate)
            {
                if (direction == FileTransferDirection.Inbound &&
                    inboundTransfer is { IsTerminal: false } inbound &&
                    ReferenceEquals(inboundTransfer, inbound) &&
                    string.Equals(inbound.SessionId, sessionId, StringComparison.Ordinal) &&
                    string.Equals(inbound.TransferId, transferId, StringComparison.Ordinal) &&
                    inbound.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
                    inbound.NegotiatedDataProtocolVersion ==
                        FileTransferProtocol.ProtocolVersionV6 &&
                    inbound.V6TransportEpoch?.EpochId == frame.TransportEpoch)
                {
                    inboundToPublish = inbound;
                }
            }

            if (inboundToPublish is not null)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_receive_recovery_proof_solicitation_peer_decided; decision_status=Accepted; decision_reason=current_post_tuna_fallback_receiver_state; authority_effect=none; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; direction=inbound; transport_epoch={frame.TransportEpoch}; probe_id={FormatProtocolLogValue(frame.ProbeId)}");
                _ = SendInboundV6ReceiverStateAsync(
                    inboundToPublish,
                    "receive_recovery_proof_solicitation",
                    forceSend: true);
            }
        }
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
        TransportEpochDecision? transportEpochDecision;
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
                    out transportEpochDecision,
                    targetTransportEpochId: message.TransportEpoch))
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
                out transportEpochDecision,
                targetTransportEpochId: message.TransportEpoch))
        {
            return;
        }

        if (transportEpochDecision is null)
        {
            return;
        }

        var senderLocalAvailabilityUntilExclusive = ResolveOutboundSenderLocalAvailabilityUntilExclusiveLocked(context);
        var epoch = new V6TransportEpoch
        {
            RoutePublicationToken = context.CurrentRoutePublicationToken,
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
        if (epoch.State == V6TransportEpochState.Recovered &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
            epoch.Kind is FileTransferTransportHandoffKind.TunaToNormalFallback or
                FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            epoch.RecoveryBoundaryGeneration = ++context.V6RecoveryBoundaryGeneration;
        }
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
        var reauthorizationRequest =
            AdoptOutboundFallbackLegTransportEpochLocked(
                context,
                epoch.EpochId,
                reason);
        if (TryStartOutboundFallbackTailTransportEpochReauthorizationRestartLocked(
                context,
                epoch.EpochId,
                "peer_transport_epoch_adopted",
                reauthorizationRequest))
        {
            ResolveOutboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
        }
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
        TransportEpochDecision? transportEpochDecision;
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
                        out transportEpochDecision,
                        targetTransportEpochId: message.TransportEpoch))
                {
                    return;
                }

                if (GetPendingFallbackTailYieldTransportEpochReauthorization(
                        context) is { } pendingReauthorization)
                {
                    var rebindDecision =
                        FileTransferCoordinator
                            .RebindFallbackTailYieldTransportEpochReauthorization(
                                CreateCoordinatorStateLocked(context),
                                pendingReauthorization,
                                message.TransportEpoch,
                                reason);
                    if (string.IsNullOrWhiteSpace(
                            rebindDecision.MutationRejectedReason))
                    {
                        ApplyCoordinatorDecisionToContextLocked(
                            context,
                            rebindDecision);
                    }
                    else
                    {
                        InvalidateFallbackTailYieldTransportEpochReauthorizationLocked(
                            context,
                            rebindDecision.MutationRejectedReason);
                        return;
                    }
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
                    out transportEpochDecision,
                    targetTransportEpochId: message.TransportEpoch))
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
                out transportEpochDecision,
                targetTransportEpochId: message.TransportEpoch))
        {
            return;
        }

        if (transportEpochDecision is null)
        {
            return;
        }

        if (!CanUseV6TransportEpochsLocked(context))
        {
            return;
        }

        var epoch = new V6TransportEpoch
        {
            RoutePublicationToken = context.CurrentRoutePublicationToken,
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
        if (epoch.State == V6TransportEpochState.Recovered &&
            epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
            epoch.Kind is FileTransferTransportHandoffKind.TunaToNormalFallback or
                FileTransferTransportHandoffKind.RegularNknRecovery)
        {
            epoch.RecoveryBoundaryGeneration = ++context.V6RecoveryBoundaryGeneration;
        }
        context.V6TransportEpoch = epoch;
        context.V6ReceiverTransportEpoch = epoch.EpochId;
        context.V6LastReceiverStateSentUtc = null;
        context.V6LastFrontierRequestSentUtc = null;
        var reauthorizationRequest =
            AdoptInboundFallbackLegTransportEpochLocked(
                context,
                epoch.EpochId,
                reason);
        if (TryStartInboundFallbackTailTransportEpochReauthorizationRestartLocked(
                context,
                epoch.EpochId,
                "peer_transport_epoch_adopted",
                reauthorizationRequest))
        {
            ResolveInboundFallbackTailSurvivalLocked(context, "transport_epoch_adopted");
        }
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

    private Task HandleIncomingFallbackTailYieldProofWithEvidenceAsync(FileTransferFallbackTailYieldFrameV6 message,
        FileTransferFallbackTailYieldEvidence? evidence)
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
            ApplyOutboundFallbackTailYieldProofWithEvidence(outbound, message, evidence);
        }

        if (inbound is not null)
        {
            ApplyInboundFallbackTailYieldProofWithEvidence(inbound, message, evidence);
        }

        if (outbound is null && inbound is null)
        {
            LogYieldReceiptAssociation(message, evidence, null, false, "peer_proof");
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

        var fallbackFrontierRuntime = IsFallbackFrontierRouteRuntime(context);
        var fallbackProofRejectionReason = "fallback_runtime_owner_or_leg_missing";
        if (fallbackFrontierRuntime &&
            (!IsStrictFallbackFrontierRuntime(context) ||
             !TryValidateFallbackFrontierRepairProofLocked(
                 context,
                 repairRequestId,
                 message.TransportEpoch,
                 out fallbackProofRejectionReason)))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_frontier_repair_proof_rejected; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(context.CurrentFallbackFrontierRepairContract?.Authority.AuthorityId.ToString() ?? "(none)")}; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={message.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; decision_reason={FormatProtocolLogValue(fallbackProofRejectionReason)}");
            return false;
        }

        if (!fallbackFrontierRuntime &&
            !TryAuthorizeRepairReplayLocked(
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
        string reason,
        FallbackFrontierRepairContract? fallbackFrontierContractHandoff = null)
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

        return CompleteOutboundV6TransportEpochLocked(
            context,
            reason,
            fallbackFrontierContractHandoff);
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
        TrySettleOutboundFallbackFrontierProgressLocked(
            context,
            previousRemoteFrontier,
            message.CommittedChunkIndex,
            "repair_proof_progress");
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

    private static bool IsRegularNknRecoveryForCurrentPostTunaFallbackLiveRoute(
        OutboundTransferContext context,
        V6TransportEpoch epoch)
        => epoch.Kind == FileTransferTransportHandoffKind.RegularNknRecovery &&
           epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
           context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           context.CurrentLiveRouteEpoch is
           {
               RouteSelection.Route: FileTransferRoute.PostTunaFallbackV6,
               RouteSelection.ProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
               HandoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
               TargetTransport: FileTransferTransportKind.RegularNkn,
           } liveRouteEpoch &&
           !string.Equals(liveRouteEpoch.State, "recovered", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(liveRouteEpoch.State, "terminal", StringComparison.OrdinalIgnoreCase) &&
           !context.IsTerminal;

    private static bool IsRegularNknRecoveryForCurrentPostTunaFallbackLiveRoute(
        InboundTransferContext context,
        V6TransportEpoch epoch)
        => epoch.Kind == FileTransferTransportHandoffKind.RegularNknRecovery &&
           epoch.TargetTransport == FileTransferTransportKind.RegularNkn &&
           context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           context.CurrentLiveRouteEpoch is
           {
               RouteSelection.Route: FileTransferRoute.PostTunaFallbackV6,
               RouteSelection.ProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
               HandoffKind: FileTransferTransportHandoffKind.TunaToNormalFallback,
               TargetTransport: FileTransferTransportKind.RegularNkn,
           } liveRouteEpoch &&
           !string.Equals(liveRouteEpoch.State, "recovered", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(liveRouteEpoch.State, "terminal", StringComparison.OrdinalIgnoreCase) &&
           !context.IsTerminal;

    private bool CompleteOutboundV6TransportEpochLocked(
        OutboundTransferContext context,
        string reason,
        FallbackFrontierRepairContract? fallbackFrontierContractHandoff = null)
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
        var preserveFallbackFrontierContractForHandoff =
            fallbackFrontierContractHandoff is not null &&
            ReferenceEquals(
                context.CurrentFallbackFrontierRepairContract,
                fallbackFrontierContractHandoff) &&
            !fallbackFrontierContractHandoff.Lifecycle.IsSettled;
        var fallbackTailYieldReauthorizationExpected =
            GetPendingFallbackTailYieldTransportEpochReauthorization(context) is
            {
                TargetTransportEpoch: var requiredTransportEpoch,
            } &&
            requiredTransportEpoch == epoch!.EpochId ||
            TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                epoch!.EpochId,
                reason) is
            {
                TargetTransportEpoch: var unstagedRequiredTransportEpoch,
            } &&
            unstagedRequiredTransportEpoch == epoch.EpochId;
        var fallbackTailYieldReauthorizationRequired =
            IsFallbackTailYieldTransportEpochReauthorizationRequiredLocked(
                context,
                epoch!.EpochId);
        if (fallbackTailYieldReauthorizationExpected)
        {
            if (!fallbackTailYieldReauthorizationRequired ||
                GetPendingFallbackTailYieldTransportEpochReauthorization(
                    context) is null ||
                !TryMarkOutboundFallbackTailLocalYieldFromCurrentLegLocked(
                    context,
                    "transport_epoch_reauthorization",
                    allowCurrentTransportEpochRecoveryHandoff: true))
            {
                return false;
            }
        }
        else
        {
            AcceptOutboundFallbackCheckpointAtTransportEpochCompletionLocked(
                context,
                epoch,
                observedHighestChunkIndex,
                reason);
        }

        if (!preserveFallbackFrontierContractForHandoff)
        {
            SettleAndClearFallbackFrontierRepairContractLocked(
                context,
                FallbackFrontierRepairSettlementOutcome.StaleRejected,
                $"transport_epoch_completed_{reason}");
        }
        epoch!.RecoveryBoundaryGeneration = ++context.V6RecoveryBoundaryGeneration;
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
        if ((epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
             epoch.TargetTransport == FileTransferTransportKind.RegularNkn) ||
            IsRegularNknRecoveryForCurrentPostTunaFallbackLiveRoute(context, epoch))
        {
            TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
                context,
                "transport_epoch_live_route_epoch_recovered",
                reason,
                routePublicationToken: epoch.RoutePublicationToken);
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
            $"event=filetransfer_v6_epoch_tail_unblocked; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={epoch!.EpochId}; recovery_boundary_generation={epoch.RecoveryBoundaryGeneration}; handoff_kind={FormatFileTransferTransportHandoffKind(epoch.Kind)}; source_transport={FormatFileTransferTransportKind(epoch.SourceTransport)}; target_transport={FormatFileTransferTransportKind(epoch.TargetTransport)}; observation_reason={FormatProtocolLogValue(epoch.Reason)}; reason={FormatProtocolLogValue(reason)}; committed_chunk={context.RemoteNextExpectedChunkIndex}; highest_observed_chunk={Math.Max(-1, context.ChunksAcceptedForTransport - 1)}; chunks_accepted_for_transport={context.ChunksAcceptedForTransport}; remote_credit_until_chunk_index_exclusive={context.RemoteGrantedUntilExclusive}; discarded_v4_repair_frame_count={discardedV4RepairFrameCount}; discarded_v4_repair_chunk_count={discardedV4RepairChunkCount}; discarded_v6_normal_request_count={discardedV6NormalRequestCount}; discarded_v6_priority_request_count={discardedV6PriorityRequestCount}");
        return true;
    }

    private bool CompleteInboundV6TransportEpochLocked(InboundTransferContext context, string reason)
    {
        var epoch = context.V6TransportEpoch;
        if (!IsV6TransportEpochUnresolved(epoch))
        {
            return false;
        }

        var fallbackTailYieldReauthorizationExpected =
            GetPendingFallbackTailYieldTransportEpochReauthorization(context) is
            {
                TargetTransportEpoch: var requiredTransportEpoch,
            } &&
            requiredTransportEpoch == epoch!.EpochId ||
            TryCreateFallbackTailYieldTransportEpochReauthorizationRequestLocked(
                context,
                epoch!.EpochId,
                reason) is
            {
                TargetTransportEpoch: var unstagedRequiredTransportEpoch,
            } &&
            unstagedRequiredTransportEpoch == epoch.EpochId;
        var fallbackTailYieldReauthorizationRequired =
            IsFallbackTailYieldTransportEpochReauthorizationRequiredLocked(
                context,
                epoch!.EpochId);
        if (fallbackTailYieldReauthorizationExpected)
        {
            if (!fallbackTailYieldReauthorizationRequired ||
                GetPendingFallbackTailYieldTransportEpochReauthorization(
                    context) is null ||
                !TryMarkInboundFallbackTailLocalYieldFromCurrentLegLocked(
                    context,
                    "transport_epoch_reauthorization",
                    allowCurrentTransportEpochRecoveryHandoff: true))
            {
                return false;
            }
        }
        else
        {
            AcceptInboundFallbackCheckpointAtTransportEpochCompletionLocked(
                context,
                epoch,
                reason);
        }

        SettleAndClearFallbackFrontierRepairContractLocked(
            context,
            FallbackFrontierRepairSettlementOutcome.StaleRejected,
            $"transport_epoch_completed_{reason}");
        epoch!.RecoveryBoundaryGeneration = ++context.V6RecoveryBoundaryGeneration;
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
        context.PullTransportRebindGeneration = 0;
        context.StatusMessage = GetInboundResumeStatusMessage(context.State);
        if ((epoch.Kind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
             epoch.TargetTransport == FileTransferTransportKind.RegularNkn) ||
            IsRegularNknRecoveryForCurrentPostTunaFallbackLiveRoute(context, epoch))
        {
            TryPublishLiveRouteEpochRecoveredIfAuthorizedLocked(
                context,
                "transport_epoch_live_route_epoch_recovered",
                reason,
                routePublicationToken: epoch.RoutePublicationToken);
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

    private void AcceptOutboundFallbackCheckpointAtTransportEpochCompletionLocked(
        OutboundTransferContext context,
        V6TransportEpoch epoch,
        int observedHighestChunkIndex,
        string reason)
    {
        if (epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            !IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            return;
        }

        var leg = context.CurrentTransferLeg!;
        var checkpointRequestId = leg.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(checkpointRequestId))
        {
            RememberRetiredFallbackCheckpointRequest(
                context.RetiredFallbackCheckpointRequestKeys,
                epoch.EpochId,
                checkpointRequestId);
        }

        var acceptedReason = $"transport_epoch_completed_{reason}";
        if (!TryAcceptOutboundFallbackCheckpointWithCoordinatorLocked(
                context,
                leg,
                epoch.EpochId,
                Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                observedHighestChunkIndex,
                checkpointRequestId,
                acceptedReason))
        {
            return;
        }

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

    private void AcceptInboundFallbackCheckpointAtTransportEpochCompletionLocked(
        InboundTransferContext context,
        V6TransportEpoch epoch,
        string reason)
    {
        if (epoch.Kind != FileTransferTransportHandoffKind.TunaToNormalFallback ||
            epoch.TargetTransport != FileTransferTransportKind.RegularNkn ||
            !IsCurrentPostTunaFallbackLegCheckpointPending(context.CurrentTransferLeg))
        {
            return;
        }

        var leg = context.CurrentTransferLeg!;
        var checkpointRequestId = leg.CheckpointRequestId;
        if (!string.IsNullOrWhiteSpace(checkpointRequestId))
        {
            RememberRetiredFallbackCheckpointRequest(
                context.RetiredFallbackCheckpointRequestKeys,
                epoch.EpochId,
                checkpointRequestId);
        }

        var acceptedReason = $"transport_epoch_completed_{reason}";
        if (!TryAcceptInboundFallbackCheckpointWithCoordinatorLocked(
                context,
                leg,
                epoch.EpochId,
                Math.Clamp(context.NextChunkIndex, 0, Math.Max(0, context.ChunkCount)),
                context.PullHighestReceivedChunkIndex,
                checkpointRequestId,
                acceptedReason))
        {
            return;
        }

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
        FallbackFrontierRepairContract? fallbackFrontierContract,
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
            var fallbackFrontierRuntime = IsFallbackFrontierRouteRuntime(context);
            var batchCoversPreviousFrontier =
                batch.StartChunkIndex <= previousCommittedChunkIndex &&
                previousCommittedChunkIndex < batch.StartChunkIndex + Math.Max(0, batch.ChunkCount);
            if (!fallbackFrontierRuntime &&
                string.IsNullOrWhiteSpace(repairRequestId) &&
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

            var fallbackProofRejectionReason = "fallback_runtime_owner_or_leg_missing";
            var repairProofAuthorized = fallbackFrontierRuntime
                ? fallbackFrontierContract is not null &&
                  IsStrictFallbackFrontierRuntime(context) &&
                  (fallbackFrontierContract.Lifecycle.IsSettled
                      ? TryValidateInboundFallbackFrontierProgressProofLocked(
                          context,
                          fallbackFrontierContract,
                          repairRequestId,
                          batch.TransportEpoch,
                          out fallbackProofRejectionReason)
                      : TryValidateFallbackFrontierRepairProofLocked(
                          context,
                          repairRequestId,
                          batch.TransportEpoch,
                          out fallbackProofRejectionReason))
                : TryAuthorizeRepairReplayLocked(
                    context,
                    context.CurrentRuntimeOwner,
                    inferredRepairRequestId ? RepairReplayKind.UnmatchedFrontierProof : RepairReplayKind.V6Repair,
                    "v6_chunk_repair_proof",
                    repairRequestId,
                    context.CurrentTransferLeg,
                    ResolveV6TransportEpochAuthorityId(context.CurrentTransferLeg, epoch),
                    context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                    null,
                    out _);
            if (!repairProofAuthorized)
            {
                if (fallbackFrontierRuntime)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_fallback_frontier_repair_proof_rejected; {FileTransferDiagnosticScopeEvidence.Format(context.SessionId, context.TransferId)}authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(context.CurrentFallbackFrontierRepairContract?.Authority.AuthorityId.ToString() ?? "(none)")}; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; transport_epoch={batch.TransportEpoch}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; decision_reason={FormatProtocolLogValue(fallbackProofRejectionReason)}");
                }

                return;
            }

            if (!TryAuthorizeTransportEpochActionLocked(
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
