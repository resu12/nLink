using System.Runtime.CompilerServices;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    internal const string PostFallbackDialFailureRetryRetirementReason =
        "retry_dispatch_post_fallback_dial_failure";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsPostFallbackDialFailureCurrent(FileTransferCoordinatorState state,
        RuntimeUnlockPostFallbackDialFailureObservation observation, bool checkBudget)
    {
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var source = observation.SourceScope;
        if (observation.IsDefault || state.IsTerminal ||
            state.Direction != source.PublicationAuthority.Direction ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            state.CurrentRoutePublicationToken is not { } publication ||
            !MatchesRoutePublicationAuthority(source.PublicationAuthority, publication) ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe is not null ||
            state.PersistentState.FileTransferRouteTransition.CurrentOperation is not null ||
            state.CurrentDataSessionIdentity != source.DataSessionIdentity ||
            state.CurrentDataSessionGeneration != source.DataSessionGeneration ||
            GetFallbackTailYieldSubstate(state).CurrentOperation is not { PairedRelease: { } release } pair ||
            !IsFallbackTailYieldScopeCurrent(state, GetEffectiveFallbackTailYieldScope(pair), out _) ||
            GetEffectiveFallbackTailYieldScope(pair).TransportEpoch != source.TransportEpoch ||
            GetEffectiveFallbackTailYieldScope(pair).BridgeRecoveryGeneration != source.BridgeRecoveryGeneration ||
            GetEffectiveFallbackTailYieldScope(pair).CheckpointRequestIdSnapshot != source.CheckpointRequestId ||
            checkBudget && lifecycle.PendingScopeInvalidationCommand is not null ||
            checkBudget && lifecycle.LastPostFallbackDialFailure?.SourceScope.PublicationAuthority ==
                source.PublicationAuthority)
        {
            return false;
        }

        if (state.Direction == FileTransferDirection.Inbound)
        {
            return observation.BindingReceipt is { } binding &&
                binding.Request.PublicationAuthority == source.PublicationAuthority &&
                binding.Request.PayerDecisionId == observation.PayerDecisionId &&
                binding.PairedRelease == release && binding.PairedScope == GetEffectiveFallbackTailYieldScope(pair) &&
                pair.PairingState == FileTransferFallbackTailYieldPairingState.Paired &&
                GetFallbackTailYieldSubstate(state).CurrentReservation is null;
        }

        var tail = GetFallbackTailYieldSubstate(state);
        return lifecycle.PendingAttempt is null && lifecycle.InstalledAttempt is null &&
            lifecycle.RetainedPredecessor is null && lifecycle.BoundedClosure is null &&
            lifecycle.LastTerminalAttempt is
            { State: RuntimeUnlockRetryLifecycleAttemptState.Settled,
              Settlement.Outcome: RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
              DispatchObserved: true, InstallReceipt: { } install } settled &&
            install == observation.InstallReceipt && settled.OfferGeneration == observation.OfferGeneration &&
            settled.OfferNonce == observation.OfferNonce &&
            tail.LastConsumingInstallReceipt == install &&
            tail.LastConsumedReservation is { } consumed && consumed.Release == release &&
            consumed.RetryAuthority == settled.Authority &&
            pair.PairingState == FileTransferFallbackTailYieldPairingState.ReleaseConsumed &&
            observation.FailedTransaction is
            { State: RuntimeUnlockTransactionState.Failed, PeerReceived: true,
              AnswerReceived: false, PathProbeState: RuntimeUnlockPathProbeState.None,
              RouteCommitPending: false, RouteCommitted: false,
              FailureReason: "runtime_unlock_tuna_dial_failed_before_answer" } failed &&
            failed.SessionId == state.SessionId &&
            (failed.TransferId is null || failed.TransferId == state.TransferId) &&
            failed.TransactionGeneration > 0 &&
            failed.OfferGeneration == settled.OfferGeneration;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsPostFallbackDialFailureRetryRetirementCurrent(FileTransferCoordinatorState state)
        => GetRuntimeUnlockRetryLifecycle(state).LastPostFallbackDialFailure is { } observation &&
            IsPostFallbackDialFailureCurrent(state, observation, checkBudget: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static RuntimeUnlockRetryLifecycleScopeInvalidationDecision
        PreparePostFallbackDialFailureRetirement(FileTransferCoordinatorState state,
            RuntimeUnlockPostFallbackDialFailureObservation observation)
    {
        if (state.Direction != FileTransferDirection.Outbound ||
            !IsPostFallbackDialFailureCurrent(state, observation, checkBudget: true))
        {
            return new(RuntimeUnlockRetryLifecycleTransitionStatus.Rejected,
                "post_fallback_dial_failure_not_current") { State = state };
        }
        var observed = ApplyRuntimeUnlockRetryLifecycle(state, GetRuntimeUnlockRetryLifecycle(state) with
        {
            LastPostFallbackDialFailure = observation,
        });
        return PrepareRuntimeUnlockRetryLifecycleScopeInvalidationCore(observed,
            PostFallbackDialFailureRetryRetirementReason, state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferFallbackTailYieldTransitionDecision
        SupersedeInboundFallbackPairAfterDialFailure(FileTransferCoordinatorState state,
            RuntimeUnlockPostFallbackDialFailureObservation observation)
    {
        if (state.Direction != FileTransferDirection.Inbound ||
            !IsPostFallbackDialFailureCurrent(state, observation, checkBudget: true))
        {
            return FallbackTailYieldTransitionRejected(state, "post_fallback_dial_failure_not_current");
        }
        var observed = ApplyRuntimeUnlockRetryLifecycle(state, GetRuntimeUnlockRetryLifecycle(state) with
        {
            LastPostFallbackDialFailure = observation,
        });
        var pair = GetFallbackTailYieldSubstate(observed).CurrentOperation!.Value;
        return FinalizeFallbackTailYieldSnapshot(state,
            SupersedeFallbackTailPairedYieldAfterRejectedProbeCore(observed,
                FileTransferFallbackTailRetryCycleSupersessionRole.Responder, pair.Decision,
                pair.PairedRelease!.Value, null, PostFallbackDialFailureRetryRetirementReason),
            nameof(SupersedeInboundFallbackPairAfterDialFailure), observation, pair.Decision.AuthorityId);
    }
}
