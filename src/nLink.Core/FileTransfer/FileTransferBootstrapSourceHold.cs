using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record FileTransferBootstrapSourceHoldCommand(
    string Identity, long Generation, string CoordinatorInstanceId, FileTransferBootstrapAgreement Agreement,
    FileTransferBootstrapLocalStopScope Source, string DataSessionIdentity, long DataSessionGeneration,
    FileTransferFallbackV6ProofBootstrapObservation Initiation, FileTransferFallbackBootstrapFrameV6? PeerWake);

internal sealed record FileTransferBootstrapSourceHoldCapture(
    FileTransferBootstrapSourceHoldCommand Command, long PauseInstance,
    ImmutableArray<long> OutboundPhysicalLeases, ImmutableArray<long> InboundWrites, ImmutableArray<long> InboundFeedback)
{
    public ImmutableArray<FileTransferBootstrapSourceCallback> OutboundCallbacks { get; init; } = [];
}

internal sealed record FileTransferBootstrapSourceHoldReceipt(
    FileTransferBootstrapSourceHoldCapture Capture,
    ImmutableArray<long> SettledOutboundPhysicalLeases, ImmutableArray<long> SettledInboundWrites,
    ImmutableArray<long> SettledInboundFeedback, FileTransferRouteTransitionDurableCheckpoint DurableCheckpoint);

internal sealed record FileTransferBootstrapSourceHoldOperation(
    FileTransferBootstrapSourceHoldCommand Command, FileTransferBootstrapSourceHoldCapture? Capture = null,
    FileTransferBootstrapSourceHoldReceipt? Receipt = null, bool Invalidated = false);

internal sealed record FileTransferBootstrapSourceHoldDecision(
    FileTransferCoordinatorState State, bool Accepted, string Reason,
    FileTransferBootstrapSourceHoldCommand? Command = null, FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null);

internal static partial class FileTransferCoordinator
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static FileTransferBootstrapSourceHoldDecision PrepareFallbackBootstrapSourceHold(
        FileTransferCoordinatorState state, FileTransferFallbackV6ProofBootstrapObservation initiation,
        FileTransferFallbackBootstrapFrameV6? peerWake)
    {
        var decision = PrepareFallbackBootstrapSourceHoldCore(state, initiation, peerWake);
        return FinalizeBootstrapSourceHoldSnapshot(state, decision, nameof(PrepareFallbackBootstrapSourceHold), new { initiation, peerWake });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapSourceHoldDecision PrepareFallbackBootstrapSourceHoldCore(
        FileTransferCoordinatorState state, FileTransferFallbackV6ProofBootstrapObservation initiation,
        FileTransferFallbackBootstrapFrameV6? peerWake)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        if (!IsFallbackBootstrapInitiationCurrent(state, initiation) || !CanStartFallbackBootstrapExchange(state) ||
            state.CurrentRoutePublicationToken is null || state.CurrentRouteCommit is null ||
            state.CurrentRuntimeOwner is null || state.CurrentLeg is null ||
            state.CurrentDataSessionIdentity is not { Length: > 0 } sessionIdentity || state.CurrentDataSessionGeneration <= 0 ||
            transition.BootstrapAgreement is not { IsEnabled: true } agreement ||
            initiation.InitiationKind == FileTransferBootstrapInitiationKind.PeerEvidence &&
                (peerWake is null || !IsFallbackBootstrapWakeFrameCurrent(state, peerWake)))
        {
            return new(state, false, "bootstrap_source_hold_initiation_rejected");
        }
        var source = CaptureFallbackBootstrapSourceScope(state);
        if (source is null) { return new(state, false, "bootstrap_source_hold_source_stale"); }
        if (transition.BootstrapSourceHold is { } prior && prior.Command.Source == source)
        {
            var same = IsFallbackBootstrapSourceHoldCurrent(state, prior.Command) &&
                prior.Command.Initiation.InitiationKind == initiation.InitiationKind &&
                prior.Command.Initiation.LocalStop == initiation.LocalStop && prior.Command.PeerWake == peerWake;
            return new(state, same, same ? "bootstrap_source_hold_already_prepared" : "bootstrap_source_hold_conflict");
        }
        var generation = checked((transition.BootstrapSourceHold?.Command.Generation ?? 0) + 1);
        var identity = "fbchold1:" + CalculateSnapshotFingerprint(new { source, sessionIdentity, generation, agreement, initiation, peerWake });
        var command = new FileTransferBootstrapSourceHoldCommand(identity, generation, state.PersistentState.CoordinatorInstanceId!,
            agreement, source, sessionIdentity, state.CurrentDataSessionGeneration, initiation, peerWake);
        return new(WithHold(state, new(command)), true, "bootstrap_source_hold_prepared", command);
    }

    internal static bool IsFallbackBootstrapSourceHoldCurrent(FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldCommand command)
        => state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is { Invalidated: false } operation &&
            operation.Command == command && state.PersistentState.CoordinatorInstanceId == command.CoordinatorInstanceId &&
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement == command.Agreement &&
            state.CurrentDataSessionIdentity == command.DataSessionIdentity && state.CurrentDataSessionGeneration == command.DataSessionGeneration &&
            IsFallbackBootstrapLocalStopScopeCurrent(state, command.Source);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static FileTransferBootstrapSourceHoldDecision ApplyFallbackBootstrapSourceHoldCapture(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldCapture capture)
    {
        var result = ApplyFallbackBootstrapSourceHoldCaptureCore(state, capture);
        return FinalizeBootstrapSourceHoldSnapshot(state, result, nameof(ApplyFallbackBootstrapSourceHoldCapture), capture);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapSourceHoldDecision ApplyFallbackBootstrapSourceHoldCaptureCore(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldCapture capture)
    {
        var operation = state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold;
        var valid = IsFallbackBootstrapSourceHoldCurrent(state, capture.Command) && capture.PauseInstance > 0 &&
            AreHoldIdsValid(capture.OutboundPhysicalLeases) && AreHoldIdsValid(capture.InboundWrites) && AreHoldIdsValid(capture.InboundFeedback) &&
            !capture.OutboundCallbacks.IsDefault &&
            capture.OutboundCallbacks.Select(callback => callback.LeaseGeneration).Distinct().Count() == capture.OutboundCallbacks.Length &&
            capture.OutboundCallbacks.All(callback => IsFallbackBootstrapSourceCallbackSourceCurrent(state, callback) &&
                callback.Source == capture.Command.Source) &&
            (state.Direction == FileTransferDirection.Outbound
                ? capture.InboundWrites.IsEmpty && capture.InboundFeedback.IsEmpty : capture.OutboundPhysicalLeases.IsEmpty && capture.OutboundCallbacks.IsEmpty) &&
            (operation!.Capture is null || operation.Capture == capture);
        var result = new FileTransferBootstrapSourceHoldDecision(valid && operation!.Capture is null
            ? WithHold(state, operation with { Capture = capture }) : state, valid,
            valid ? "bootstrap_source_hold_capture_applied" : "bootstrap_source_hold_capture_rejected");
        return result;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static FileTransferBootstrapSourceHoldDecision ApplyFallbackBootstrapSourceHoldReceipt(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldReceipt receipt)
    {
        var result = ApplyFallbackBootstrapSourceHoldReceiptCore(state, receipt);
        return FinalizeBootstrapSourceHoldSnapshot(state, result, nameof(ApplyFallbackBootstrapSourceHoldReceipt), receipt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapSourceHoldDecision ApplyFallbackBootstrapSourceHoldReceiptCore(
        FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldReceipt receipt)
    {
        var operation = state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold;
        var valid = IsFallbackBootstrapSourceHoldCurrent(state, receipt.Capture.Command) && operation!.Capture == receipt.Capture &&
            !receipt.DurableCheckpoint.IsDefault && IsBootstrapHoldCheckpointSourceCurrent(state, receipt.DurableCheckpoint) &&
            AreHoldIdsValid(receipt.SettledOutboundPhysicalLeases) && AreHoldIdsValid(receipt.SettledInboundWrites) && AreHoldIdsValid(receipt.SettledInboundFeedback) &&
            receipt.Capture.OutboundPhysicalLeases.SequenceEqual(receipt.SettledOutboundPhysicalLeases) &&
            receipt.Capture.InboundWrites.SequenceEqual(receipt.SettledInboundWrites) &&
            receipt.Capture.InboundFeedback.SequenceEqual(receipt.SettledInboundFeedback) &&
            (operation.Receipt is null || operation.Receipt == receipt);
        var result = new FileTransferBootstrapSourceHoldDecision(valid && operation!.Receipt is null
            ? WithHold(state, operation with { Receipt = receipt }) : state, valid,
            valid ? "bootstrap_source_hold_drained" : "bootstrap_source_hold_receipt_rejected");
        return result;
    }

    internal static FileTransferBootstrapSourceHoldDecision InvalidateFallbackBootstrapSourceHold(FileTransferCoordinatorState state, string reason)
    {
        var operation = state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold;
        var result = new FileTransferBootstrapSourceHoldDecision(operation is { Invalidated: false }
            ? WithHold(state, operation with { Invalidated = true }) : state, operation is not null, reason);
        return FinalizeBootstrapSourceHoldSnapshot(state, result, nameof(InvalidateFallbackBootstrapSourceHold), reason);
    }

    private static bool AreHoldIdsValid(ImmutableArray<long> ids)
        => !ids.IsDefault && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Length;

    private static bool IsBootstrapHoldCheckpointSourceCurrent(FileTransferCoordinatorState state, FileTransferRouteTransitionDurableCheckpoint checkpoint)
        => checkpoint.SourceRuntimeOwner == state.CurrentRuntimeOwner && checkpoint.SourceRoutePublication == state.CurrentRoutePublicationToken &&
            checkpoint.SourceTerminalGeneration == state.TerminalGeneration && checkpoint.SourceLegGeneration == state.CurrentLeg?.Generation &&
            checkpoint.SourceTransportEpoch == state.CurrentLeg?.TransportEpochId && checkpoint.SourceBridgeRecoveryGeneration == state.CurrentLeg?.BridgeRecoveryGeneration &&
            checkpoint.DataSessionGeneration == state.CurrentDataSessionGeneration;

    private static FileTransferCoordinatorState WithHold(FileTransferCoordinatorState state, FileTransferBootstrapSourceHoldOperation operation)
        => state with { PersistentState = state.PersistentState with
        { FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with { BootstrapSourceHold = operation } } };

    private static FileTransferBootstrapSourceHoldDecision FinalizeBootstrapSourceHoldSnapshot(
        FileTransferCoordinatorState before, FileTransferBootstrapSourceHoldDecision decision, string kind, object attempt)
    {
        if (string.IsNullOrWhiteSpace(before.PersistentState.CoordinatorInstanceId)) { return decision; }
        var snapshot = CreateBootstrapCorrelationSnapshot(before, decision.State, decision.Accepted, kind, decision.Reason, attempt);
        return decision with { State = snapshot.State, SnapshotEnvelope = snapshot.Envelope };
    }
}
