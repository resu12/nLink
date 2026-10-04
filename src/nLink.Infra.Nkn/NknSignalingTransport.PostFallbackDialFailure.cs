using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private event EventHandler<RuntimeUnlockPostFallbackDialFailureObservedEventArgs>?
        runtimeUnlockPostFallbackDialFailureObserved;

    event EventHandler<RuntimeUnlockPostFallbackDialFailureObservedEventArgs>?
        IRuntimeUnlockRetryDispatchAuthorityBridge.RuntimeUnlockPostFallbackDialFailureObserved
    {
        add => runtimeUnlockPostFallbackDialFailureObserved += value;
        remove => runtimeUnlockPostFallbackDialFailureObserved -= value;
    }

    bool IRuntimeUnlockRetryDispatchAuthorityBridge.IsRuntimeUnlockPostFallbackDialFailureCurrent(
        RuntimeUnlockPostFallbackDialFailureObservation observation)
    {
        if (observation.IsDefault || !TryCaptureRuntimeUnlockRetryNegotiationSourceScope(
                observation.SourceScope.PublicationAuthority, out var source, out var data) ||
            data is null || data.IsDisposed || source.TransportEpoch != observation.SourceScope.TransportEpoch ||
            source.BridgeRecoveryGeneration != observation.SourceScope.BridgeRecoveryGeneration ||
            (source.CheckpointRequestId ?? "(none)") != observation.SourceScope.CheckpointRequestId ||
            source.DataSessionIdentity != observation.SourceScope.DataSessionIdentity ||
            source.DataSessionGeneration != observation.SourceScope.DataSessionGeneration)
        {
            return false;
        }
        lock (accelerationGate)
        {
            if (disposed) { return false; }
            if (observation.InstallReceipt is { } installed)
            {
                return runtimeUnlockRetryDispatchInstallShadow.LastCommittedReceipt == installed &&
                    runtimeUnlockTransactionState == observation.FailedTransaction &&
                    runtimeUnlockTransactionState.OfferGeneration == observation.OfferGeneration &&
                    runtimeUnlockTunaPathLeaseState.PayerDecisionId == observation.PayerDecisionId &&
                    runtimeUnlockTunaPathLeaseState.State == RuntimeUnlockTunaPathLeaseState.Failed &&
                    runtimeUnlockTunaPathLeaseState.TransactionGeneration == runtimeUnlockTransactionState.TransactionGeneration &&
                    runtimeUnlockTunaPathLeaseState.OfferGeneration == observation.OfferGeneration &&
                    runtimeUnlockRecoveryRetryState?.DispatchContract is { } contract &&
                    contract.Authority == installed.Authority && contract.OfferNonce == observation.OfferNonce &&
                    contract.OfferGeneration == observation.OfferGeneration &&
                    contract.Lifecycle.SettlementOutcome == RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved;
            }
            return inboundRuntimeUnlockDialPending == 0 &&
                inboundRuntimeUnlockDialTerminalReason == "runtime_unlock_tuna_dial_failed" &&
                inboundRuntimeUnlockDialAttemptGeneration == observation.OfferGeneration &&
                inboundRuntimeUnlockDialSessionId == source.SourcePublicationAuthority.SessionId &&
                inboundRuntimeUnlockDialNonce == observation.OfferNonce &&
                inboundRuntimeUnlockDialPayerDecisionId == observation.PayerDecisionId;
        }
    }

    private bool PublishPostFallbackDialFailure(RuntimeUnlockPostFallbackDialFailureObservation observation)
    {
        var args = new RuntimeUnlockPostFallbackDialFailureObservedEventArgs(observation);
        runtimeUnlockPostFallbackDialFailureObserved?.Invoke(this, args);
        LocalOperationalLog.Info("NKN.Tuna",
            $"event=runtime_unlock_post_fallback_dial_failure_decided; direction={observation.SourceScope.PublicationAuthority.Direction.ToString().ToLowerInvariant()}; offer_generation={observation.OfferGeneration}; install_generation={observation.InstallReceipt?.InstallGeneration ?? 0}; accepted={(args.Accepted ? 1 : 0)}; reason=runtime_unlock_tuna_dial_failed; authority_effect={(args.Accepted ? "coordinator_recovery_applied" : "none")}");
        return args.Accepted;
    }

    private bool TryRecoverPostFallbackDialFailure(RuntimeUnlockRetryDispatchInstallReceipt installed,
        string nonce, long offerGeneration, long payerDecisionId)
    {
        if (!TryCaptureRuntimeUnlockRetryNegotiationSourceScope(installed.Request.SourcePublicationAuthority,
                out var source, out _)) { return false; }
        RuntimeUnlockTransactionSnapshot failed;
        lock (accelerationGate) { failed = runtimeUnlockTransactionState; }
        return PublishPostFallbackDialFailure(new(
            new(source.SourcePublicationAuthority, source.TransportEpoch, source.BridgeRecoveryGeneration,
                source.CheckpointRequestId ?? "(none)", source.DataSessionIdentity, source.DataSessionGeneration),
            offerGeneration, nonce, payerDecisionId, installed, FailedTransaction: failed));
    }
}
