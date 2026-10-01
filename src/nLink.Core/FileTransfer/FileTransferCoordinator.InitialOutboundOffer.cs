namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    private readonly record struct InitialStartRouteSelection(
        FileTransferRouteSelection Route, InitialTunaRouteSelectionDecision? Decision,
        string? RejectionReason = null);

    // Selection, first pin and leg start stay in one synchronous coordinator call.
    // Only the existing pin/start envelopes leave this method; no reusable admission
    // token or second readiness evaluation crosses the service boundary.
    internal static (FileTransferBootstrapAgreementDecision? Offer, FileTransferCoordinatorDecision Start)
        ApplyInitialOutboundOffer(FileTransferCoordinatorEvent start, FileTransferCoordinatorState state,
            FileTransferOfferV2 draft, bool pinAgreement)
    {
        if (start.Kind != FileTransferCoordinatorEventKind.StartTransfer ||
            state.Direction != FileTransferDirection.Outbound || state.IsTerminal ||
            state.CurrentRuntimeOwner is not null || state.CurrentLeg is not null ||
            state.CurrentLiveRouteEpoch is not null || state.LastRuntimeOwnerGeneration != 0 ||
            state.LastTransferLegGeneration != 0 ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not null ||
            !IsBootstrapNegotiationScopeCurrent(state, draft.SessionId, draft.TransferId))
            return (null, FinalizeApplySnapshot(start, state,
                MutationRejected(state, start.Reason, "initial_offer_source_not_fresh")));

        var selection = SelectInitialStartRoute(start, state);
        if (selection.RejectionReason is { } rejection)
            return (null, FinalizeApplySnapshot(start, state,
                MutationRejected(state, start.Reason, rejection,
                    initialTunaRouteSelectionDecision: selection.Decision)));

        var offer = draft with
        {
            FileTransferRoute = selection.Route.TelemetryToken,
            PreferredDataProtocolVersion = selection.Route.ProtocolVersion,
        };
        FileTransferBootstrapAgreementDecision? pinned = null;
        if (pinAgreement)
        {
            pinned = PinFallbackBootstrapOffer(state, offer);
            if (!pinned.Accepted)
                return (pinned, FinalizeApplySnapshot(start, pinned.State,
                    MutationRejected(pinned.State, start.Reason, pinned.Reason)));
            state = pinned.State;
        }

        return (pinned, FinalizeApplySnapshot(start, state, ApplyCore(start, state, selection)));
    }

    private static InitialStartRouteSelection SelectInitialStartRoute(
        FileTransferCoordinatorEvent coordinatorEvent, FileTransferCoordinatorState state)
    {
        var routeSelection = coordinatorEvent.RouteSelection;
        InitialTunaRouteSelectionDecision? initialTunaDecision = null;
        if (routeSelection.Route == FileTransferRoute.FileTunaV4)
        {
            if (!CanStartInitialFileTunaRoute(
                    state,
                    coordinatorEvent,
                    routeSelection,
                    out var initialTunaRejectionReason))
            {
                if (coordinatorEvent.AllowInitialRouteFallback &&
                    coordinatorEvent.InitialRouteFallbackSelection is { } fallbackSelection)
                {
                    if (!IsValidInitialTunaFallbackSelection(fallbackSelection))
                    {
                        var fallbackRejected = new InitialTunaRouteSelectionDecision(
                            Accepted: false,
                            Reason: "initial_tuna_fallback_route_invalid",
                            RequestedRoute: routeSelection.Route,
                            RequestedProtocolVersion: routeSelection.ProtocolVersion,
                            RequestedTransport: coordinatorEvent.TargetTransport,
                            SelectedRoute: state.RouteSelection.Route,
                            SelectedProtocolVersion: state.RouteSelection.ProtocolVersion,
                            FallbackApplied: false,
                            Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                        return new(state.RouteSelection, fallbackRejected, "initial_tuna_fallback_route_invalid");
                    }

                    initialTunaDecision = new InitialTunaRouteSelectionDecision(
                        Accepted: false,
                        Reason: initialTunaRejectionReason,
                        RequestedRoute: routeSelection.Route,
                        RequestedProtocolVersion: routeSelection.ProtocolVersion,
                        RequestedTransport: coordinatorEvent.TargetTransport,
                        SelectedRoute: fallbackSelection.Route,
                        SelectedProtocolVersion: fallbackSelection.ProtocolVersion,
                        FallbackApplied: true,
                        Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                    routeSelection = fallbackSelection;
                }
                else
                {
                    var rejected = new InitialTunaRouteSelectionDecision(
                        Accepted: false,
                        Reason: initialTunaRejectionReason,
                        RequestedRoute: routeSelection.Route,
                        RequestedProtocolVersion: routeSelection.ProtocolVersion,
                        RequestedTransport: coordinatorEvent.TargetTransport,
                        SelectedRoute: state.RouteSelection.Route,
                        SelectedProtocolVersion: state.RouteSelection.ProtocolVersion,
                        FallbackApplied: false,
                        Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                    return new(state.RouteSelection, rejected, initialTunaRejectionReason);
                }
            }
            else
            {
                initialTunaDecision = new InitialTunaRouteSelectionDecision(
                    Accepted: true,
                    Reason: "accepted",
                    RequestedRoute: routeSelection.Route,
                    RequestedProtocolVersion: routeSelection.ProtocolVersion,
                    RequestedTransport: coordinatorEvent.TargetTransport,
                    SelectedRoute: routeSelection.Route,
                    SelectedProtocolVersion: routeSelection.ProtocolVersion,
                    FallbackApplied: false,
                    Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
            }
        }
        return new(routeSelection, initialTunaDecision);
    }
}
