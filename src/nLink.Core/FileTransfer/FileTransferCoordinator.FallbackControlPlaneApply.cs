using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    internal static (FileTransferCoordinatorActionDecision Authorization,
        FileTransferCoordinatorDecision? Application) AuthorizeAndApplyFallbackControlPlane(
        FileTransferCoordinatorState authorizationState,
        FileTransferCoordinatorActionRequest request,
        Func<FallbackControlPlaneToken, FileTransferCoordinatorEvent> createApplyEvent,
        Func<FileTransferCoordinatorState> captureApplyState)
    {
        var eventKind = request.Kind switch
        {
            FileTransferCoordinatorActionKind.ControlPlaneSend =>
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
            FileTransferCoordinatorActionKind.ControlPlaneSendCompletion =>
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var authorization = AuthorizeActionCore(authorizationState, request);
        if (!CanOmitUnconsumedFallbackAuthorizationSnapshot(authorizationState, request, authorization))
        {
            authorization = FinalizeActionSnapshot(authorizationState, request, authorization);
        }
        if (!authorization.Accepted || authorization.FallbackControlPlaneToken is not { } token)
        {
            return (authorization, null);
        }

        // Preserve the old order: authorize, construct the event, capture fresh
        // state, then Apply. Never apply the unused authorization's state.
        var applyEvent = createApplyEvent(token);
        if (applyEvent.Kind != eventKind || applyEvent.FallbackControlPlaneToken != token)
        {
            throw new ArgumentException("Fallback apply event must use the authorized kind and exact token.", nameof(createApplyEvent));
        }
        return (authorization, Apply(applyEvent, captureApplyState()));
    }

    private static bool CanOmitUnconsumedFallbackAuthorizationSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorActionRequest request,
        FileTransferCoordinatorActionDecision decision)
    {
        if (!decision.Accepted || decision.FallbackControlPlaneToken is not { } token ||
            string.IsNullOrWhiteSpace(before.PersistentState.CoordinatorInstanceId))
        {
            return false;
        }
        var expected = request.Kind == FileTransferCoordinatorActionKind.ControlPlaneSend
            ? before with
            {
                CurrentFallbackControlPlaneToken = token,
                LastFallbackControlPlaneTokenGeneration = token.TokenGeneration,
            }
            : before;
        if (decision.State != expected)
        {
            return false;
        }

        // Scope retirement/reconciliation must keep the full snapshot finalizer,
        // including its hidden-mutation and rejection checks. No cross-call cache.
        var reconciled = decision.State;
        ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot> attempted = default;
        ReconcileTransitionSnapshotState(before, ref reconciled, ref attempted,
            suppliedOperationsAreComplete: false);
        return ReferenceEquals(reconciled, decision.State);
    }
}
