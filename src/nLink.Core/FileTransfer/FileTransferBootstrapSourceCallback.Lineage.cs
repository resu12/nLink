namespace NLink.Core.FileTransfer;

// Bounded source-supersession provenance. Only Core commit/publication paths advance it.
internal sealed record FileTransferBootstrapSourceCallbackLineage(
    FileTransferBootstrapSourceHoldCapture Capture, RoutePublicationToken Successor,
    RoutePublicationToken Publication, string Identity);

internal static partial class FileTransferCoordinator
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState RecordBootstrapSourceCallbackSuccessor(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionAuthority authority, in FileTransferRouteTransitionOperation committed,
        FileTransferBootstrapSourceHoldOperation? hold, FileTransferBootstrapExchange? exchange, FileTransferBootstrapAgreement? agreement)
    {
        if (hold is not { Capture: { } capture, Receipt: { } receipt } || receipt.Capture != capture ||
            exchange is not { Completed: true, Invalidated: false } ||
            exchange.SourceHoldCommand != capture.Command || exchange.Agreement != capture.Command.Agreement ||
            agreement != capture.Command.Agreement ||
            capture.Command.CoordinatorInstanceId != state.PersistentState.CoordinatorInstanceId ||
            committed.State != FileTransferRouteTransitionState.Committed || committed.RoutePublicationToken is not { } publication ||
            CreateRoutePublicationAuthority(authority.SourceRoutePublication) != capture.Command.Source.Publication ||
            CreateRoutePublicationAuthority(publication) == capture.Command.Source.Publication ||
            publication.LiveRouteEpoch <= capture.Command.Source.Source.LiveRouteEpoch || authority.TargetRoute != publication.Route ||
            committed.RouteCommitToken != state.CurrentRouteCommit || publication != state.CurrentRoutePublicationToken ||
            publication.RuntimeOwner != state.CurrentRuntimeOwner || !IsRoutePublicationTokenCurrent(state, publication, out _))
        { return state; }
        var lineage = new FileTransferBootstrapSourceCallbackLineage(capture, publication, publication, "");
        return WithBootstrapSourceCallbackLineage(state, lineage);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState AdvanceBootstrapSourceCallbackPublication(FileTransferCoordinatorState state, RoutePublicationToken publication)
    {
        if (state.PersistentState.BootstrapSourceCallbackLineage is not { } lineage ||
            !IsBootstrapSourceCallbackLineageBound(state, lineage) ||
            publication.SessionId != lineage.Publication.SessionId || publication.TransferId != lineage.Publication.TransferId ||
            publication.Direction != lineage.Publication.Direction || publication.TerminalGeneration != lineage.Publication.TerminalGeneration ||
            publication.LiveRouteEpoch < lineage.Publication.LiveRouteEpoch || publication.PublicationGeneration <= lineage.Publication.PublicationGeneration)
        { return state; }
        return WithBootstrapSourceCallbackLineage(state, lineage with { Publication = publication, Identity = "" });
    }

    private static bool IsBootstrapSourceCallbackLineageBound(FileTransferCoordinatorState state, FileTransferBootstrapSourceCallbackLineage lineage)
        => lineage.Identity == CalculateSnapshotFingerprint(lineage with { Identity = "" }) &&
            lineage.Capture.Command.CoordinatorInstanceId == state.PersistentState.CoordinatorInstanceId &&
            lineage.Capture.Command.Agreement == state.PersistentState.FileTransferRouteTransition.BootstrapAgreement &&
            lineage.Successor.SessionId == state.SessionId && lineage.Successor.TransferId == state.TransferId &&
            lineage.Successor.Direction == state.Direction && lineage.Successor.TerminalGeneration == state.TerminalGeneration;

    private static bool IsBootstrapSourceCallbackLineageCurrent(FileTransferCoordinatorState state, FileTransferBootstrapSourceCallbackLineage lineage)
        => IsBootstrapSourceCallbackLineageBound(state, lineage) && state.CurrentRoutePublicationToken == lineage.Publication &&
            IsRoutePublicationTokenCurrent(state, lineage.Publication, out _);

    private static FileTransferCoordinatorState WithBootstrapSourceCallbackLineage(FileTransferCoordinatorState state, FileTransferBootstrapSourceCallbackLineage lineage)
        => state with { PersistentState = state.PersistentState with
        { BootstrapSourceCallbackLineage = lineage with { Identity = CalculateSnapshotFingerprint(lineage) } } };
}
