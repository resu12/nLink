namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private void OnRuntimeUnlockPostFallbackDialFailureObserved(object? sender,
        RuntimeUnlockPostFallbackDialFailureObservedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge ||
            args.Observation is not { IsDefault: false } observation)
        {
            return;
        }
        lock (gate)
        {
            if (!ReferenceEquals(transport, bridge) ||
                !bridge.IsRuntimeUnlockPostFallbackDialFailureCurrent(observation))
            {
                return;
            }
            var source = observation.SourceScope.PublicationAuthority;
            if (source.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { IsTerminal: false } outbound &&
                outbound.SessionId == source.SessionId && outbound.TransferId == source.TransferId)
            {
                var state = CreateCoordinatorStateLocked(outbound);
                var prepared = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(state, observation);
                if (!prepared.Accepted || !TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(
                        outbound, FileTransferCoordinator.PostFallbackDialFailureRetryRetirementReason, prepared))
                {
                    return;
                }
                var pair = FileTransferCoordinator.GetFallbackTailYieldSubstate(state).CurrentOperation!.Value;
                var superseded = FileTransferCoordinator.SupersedeFallbackTailPairedYieldAfterRejectedProbe(
                    CreateCoordinatorStateLocked(outbound), FileTransferFallbackTailRetryCycleSupersessionRole.Initiator,
                    pair.Decision, pair.PairedRelease!.Value, observation.InstallReceipt,
                    FileTransferCoordinator.PostFallbackDialFailureRetryRetirementReason);
                if (!superseded.Accepted) { return; }
                ApplyFallbackTailYieldTransitionToContextLocked(outbound, superseded);
                ClearOutboundRuntimeUnlockFallbackSeparationStateLocked(outbound);
                ResolveOutboundFallbackTailSurvivalLocked(outbound,
                    FileTransferCoordinator.PostFallbackDialFailureRetryRetirementReason);
                args.Accepted = true;
            }
            else if (source.Direction == FileTransferDirection.Inbound &&
                inboundTransfer is { IsTerminal: false } inbound &&
                inbound.SessionId == source.SessionId && inbound.TransferId == source.TransferId &&
                runtimeUnlockPostFallbackPairedRetryOfferBindingReceipt == observation.BindingReceipt)
            {
                var superseded = FileTransferCoordinator.SupersedeInboundFallbackPairAfterDialFailure(
                    CreateCoordinatorStateLocked(inbound), observation);
                if (!superseded.Accepted) { return; }
                ApplyFallbackTailYieldTransitionToContextLocked(inbound, superseded);
                runtimeUnlockPostFallbackPairedRetryOfferBindingReceipt = null;
                ClearInboundRuntimeUnlockFallbackSeparationStateLocked(inbound);
                ResolveInboundFallbackTailSurvivalLocked(inbound,
                    FileTransferCoordinator.PostFallbackDialFailureRetryRetirementReason);
                args.Accepted = true;
            }
        }
    }
}
