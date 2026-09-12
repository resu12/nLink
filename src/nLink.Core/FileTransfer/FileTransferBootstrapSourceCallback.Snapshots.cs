using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record FileTransferBootstrapCallbackSnapshot(
    string Stage, string CallbackFingerprint, long LeaseGeneration, string SourceFingerprint,
    string AgreementFingerprint, long ProviderGeneration, long SourcePauseInstance,
    string HoldCommandFingerprint, string CaptureFingerprint, ImmutableArray<string> CapturedCallbacks,
    string Disposition, string AllowedEffect);

internal static partial class FileTransferCoordinator
{
    private static FileTransferBootstrapCallbackSnapshot CreateBootstrapCallbackCaptureSnapshot(FileTransferBootstrapSourceHoldOperation hold)
        => new("Capture", "(none)", 0, CalculateSnapshotFingerprint(hold.Command.Source),
            CalculateSnapshotFingerprint(hold.Command.Agreement), 0, 0, CalculateSnapshotFingerprint(hold.Command),
            CalculateSnapshotFingerprint(hold.Capture), hold.Capture?.OutboundCallbacks.Select(CalculateSnapshotFingerprint).ToImmutableArray() ?? [],
            "None", "none");

    internal static FileTransferCoordinatorSnapshotResult CreateBootstrapSourceCallbackSnapshot(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceCallback callback, string stage,
        FileTransferBootstrapSourceCallbackDisposition disposition, string allowedEffect,
        FileTransferBootstrapSourceHoldCapture? retainedCapture = null)
    {
        var source = callback.Source.Source;
        var boundary = new FileTransferCoordinatorStateBoundarySnapshot(source.RouteToken, source.ProtocolVersion, source.LiveRouteEpoch,
            source.RoutePublicationGeneration, source.RouteCommitGeneration, source.RuntimeGeneration, source.TerminalGeneration,
            checked((int)source.LegGeneration), source.TransportEpoch, source.BridgeRecoveryGeneration, source.CheckpointGeneration,
            source.CheckpointRequestId, callback.DataSessionIdentity, callback.DataSessionGeneration);
        var fingerprint = CalculateSnapshotFingerprint(callback);
        var capture = retainedCapture ?? state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Capture;
        var captured = capture?.OutboundCallbacks.Contains(callback) == true && capture.Command.Source == callback.Source &&
            capture.Command.Agreement == callback.Agreement && capture.Command.CoordinatorInstanceId == callback.CoordinatorInstanceId;
        var payload = new FileTransferBootstrapCallbackSnapshot(stage, fingerprint, callback.LeaseGeneration,
            CalculateSnapshotFingerprint(callback.Source), CalculateSnapshotFingerprint(callback.Agreement), callback.ProviderGeneration,
            callback.PauseInstance, captured ? CalculateSnapshotFingerprint(capture!.Command) : "(none)",
            captured ? CalculateSnapshotFingerprint(capture) : "(none)", [], disposition.ToString(), allowedEffect);
        var descriptor = CreateBootstrapCorrelationDescriptor(state, "fallback_bootstrap_source_callback", "FallbackBootstrapSourceCallback",
            "fbcallback1:" + fingerprint, callback.LeaseGeneration, stage, fingerprint, boundary, null, []);
        descriptor = descriptor with { RouteTransition = descriptor.RouteTransition! with { BootstrapCallbacks = payload } };
        return CreateTransitionSnapshot(state, state, state.PersistentState.CoordinatorInstanceId!, state.PersistentState.SnapshotStateGeneration,
            "ObserveFallbackBootstrapSourceCallback", "Idempotent", "bootstrap_source_callback_" + stage.ToLowerInvariant(),
            [CreateOperationTransitionSnapshot(descriptor, stage == "Admitted" ? "None" : "Admitted", stage, false)]);
    }
}
