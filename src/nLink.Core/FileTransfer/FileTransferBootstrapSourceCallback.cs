namespace NLink.Core.FileTransfer;

internal sealed record FileTransferBootstrapSourceCallback(
    long LeaseGeneration, string CoordinatorInstanceId, FileTransferBootstrapAgreement Agreement,
    FileTransferBootstrapLocalStopScope Source, string DataSessionIdentity, long DataSessionGeneration,
    long ProviderGeneration, bool TransportPaused, long PauseInstance, string Identity);

internal enum FileTransferBootstrapSourceCallbackDisposition { Current, Held, Stale }

// A permanent diagnostic-only classification of this exact callback, not successor runtime authority.
internal sealed record FileTransferBootstrapSourceCallbackRetirement(
    FileTransferBootstrapSourceCallback Callback, FileTransferBootstrapSourceHoldCapture Capture,
    RoutePublicationToken Successor, string Identity);

internal static partial class FileTransferCoordinator
{
    internal static FileTransferBootstrapSourceCallbackRetirement? IssueFallbackBootstrapSourceCallbackRetirement(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceCallback callback, FileTransferBootstrapSourceHoldCapture? capture)
    {
        if (capture is null || !capture.OutboundCallbacks.Contains(callback) ||
            capture.Command.Source != callback.Source || capture.Command.Agreement != callback.Agreement ||
            capture.Command.CoordinatorInstanceId != callback.CoordinatorInstanceId ||
            state.PersistentState.CoordinatorInstanceId != callback.CoordinatorInstanceId ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement != callback.Agreement ||
            callback.Identity != CalculateSnapshotFingerprint(callback with { Identity = "" }) ||
            state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is not { Capture: { } accepted, Receipt: { } receipt } ||
            accepted != capture || receipt.Capture != capture ||
            state.PersistentState.FileTransferRouteTransition.BootstrapExchange is not { Completed: true, Invalidated: false } exchange ||
            exchange.SourceHoldCommand != capture.Command || exchange.Agreement != callback.Agreement ||
            !HasCommittedBootstrapSourceCallbackSuccessor(state, callback.Source) ||
            state.PersistentState.BootstrapSourceCallbackLineage is not { } lineage || lineage.Capture != capture)
        { return null; }
        var retirement = new FileTransferBootstrapSourceCallbackRetirement(callback, capture, lineage.Successor, "");
        return retirement with { Identity = CalculateSnapshotFingerprint(retirement) };
    }

    internal static bool IsFallbackBootstrapSourceCallbackRetired(FileTransferCoordinatorState state,
        FileTransferBootstrapSourceCallback callback, FileTransferBootstrapSourceHoldCapture? capture,
        FileTransferBootstrapSourceCallbackRetirement? retirement)
        => retirement is not null && retirement.Callback == callback && retirement.Capture == capture &&
            retirement.Identity == CalculateSnapshotFingerprint(retirement with { Identity = "" }) &&
            state.PersistentState.CoordinatorInstanceId == callback.CoordinatorInstanceId &&
            state.SessionId == retirement.Successor.SessionId && state.TransferId == retirement.Successor.TransferId &&
            state.Direction == retirement.Successor.Direction &&
            state.PersistentState.BootstrapSourceCallbackLineage is { } lineage && IsBootstrapSourceCallbackLineageCurrent(state, lineage) &&
            (lineage.Successor.LiveRouteEpoch > retirement.Successor.LiveRouteEpoch || lineage.Successor == retirement.Successor) &&
            retirement.Successor.LiveRouteEpoch > callback.Source.Source.LiveRouteEpoch;

    internal static bool HasCommittedBootstrapSourceCallbackSuccessor(FileTransferCoordinatorState state,
        FileTransferBootstrapLocalStopScope source)
        => state.PersistentState.BootstrapSourceCallbackLineage is { } lineage && IsBootstrapSourceCallbackLineageCurrent(state, lineage) &&
            lineage.Capture.Command.Source == source && lineage.Successor.LiveRouteEpoch > source.Source.LiveRouteEpoch;

    internal static FileTransferBootstrapSourceCallback? IssueFallbackBootstrapSourceCallback(
        FileTransferCoordinatorState state, long leaseGeneration, long providerGeneration, bool paused, long pauseInstance)
    {
        if (state.Direction != FileTransferDirection.Outbound || leaseGeneration <= 0 || providerGeneration <= 0 ||
            CaptureFallbackBootstrapSourceScope(state) is not { } source ||
            state.CurrentDataSessionIdentity is not { Length: > 0 } session || state.CurrentDataSessionGeneration <= 0 ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { IsEnabled: true } agreement)
        { return null; }
        if (state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Command.Source == source) { return null; }
        var callback = new FileTransferBootstrapSourceCallback(leaseGeneration, state.PersistentState.CoordinatorInstanceId!, agreement, source, session,
            state.CurrentDataSessionGeneration, providerGeneration, paused, pauseInstance, "");
        return callback with { Identity = CalculateSnapshotFingerprint(callback) };
    }

    internal static bool IsFallbackBootstrapSourceCallbackSourceCurrent(FileTransferCoordinatorState state,
        FileTransferBootstrapSourceCallback callback)
        => callback.LeaseGeneration > 0 && callback.ProviderGeneration > 0 &&
            callback.Identity == CalculateSnapshotFingerprint(callback with { Identity = "" }) &&
            state.Direction == FileTransferDirection.Outbound &&
            state.PersistentState.CoordinatorInstanceId == callback.CoordinatorInstanceId &&
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement == callback.Agreement &&
            state.CurrentDataSessionIdentity == callback.DataSessionIdentity &&
            state.CurrentDataSessionGeneration == callback.DataSessionGeneration &&
            IsFallbackBootstrapLocalStopScopeCurrent(state, callback.Source);

    internal static FileTransferBootstrapSourceCallbackDisposition ResolveFallbackBootstrapSourceCallback(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceCallback callback,
        long providerGeneration, bool paused, long pauseInstance)
    {
        if (!IsFallbackBootstrapSourceCallbackSourceCurrent(state, callback) || providerGeneration != callback.ProviderGeneration)
        { return FileTransferBootstrapSourceCallbackDisposition.Stale; }
        if (state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is { } hold && hold.Command.Source == callback.Source)
        {
            return IsFallbackBootstrapSourceHoldCurrent(state, hold.Command) && hold.Capture is { } capture &&
                capture.OutboundCallbacks.Contains(callback) && paused && pauseInstance == capture.PauseInstance
                ? FileTransferBootstrapSourceCallbackDisposition.Held : FileTransferBootstrapSourceCallbackDisposition.Stale;
        }
        return paused == callback.TransportPaused && pauseInstance == callback.PauseInstance
            ? FileTransferBootstrapSourceCallbackDisposition.Current : FileTransferBootstrapSourceCallbackDisposition.Stale;
    }
}
