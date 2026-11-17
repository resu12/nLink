using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BootstrapSourceHold_CoreRequiresExactCaptureAndReceiptBeforeFreezingCheckpoint(bool inbound)
    {
        var pair = CreateCorrelatedBootstrapPair();
        var state = inbound ? pair.Receiver : pair.Sender;
        var template = StartCorrelated(state).Command!;
        var observation = template.Bootstrap.Observation;
        var frame = template.Frame as FileTransferFallbackBootstrapStateFrameV6;
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(state, observation, frame).Accepted);
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, null);
        Assert.True(prepared.Accepted, prepared.Reason);
        Assert.Null(prepared.State.PersistentState.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
        Assert.Null(prepared.State.PersistentState.FileTransferRouteTransition.BootstrapExchange);
        var duplicate = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(prepared.State, observation, null);
        Assert.True(duplicate.Accepted, duplicate.Reason);
        Assert.Null(duplicate.Command);
        var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 1, inbound ? [] : [11], inbound ? [12] : [], inbound ? [13] : []);
        var receipt = new FileTransferBootstrapSourceHoldReceipt(capture, capture.OutboundPhysicalLeases, capture.InboundWrites,
            capture.InboundFeedback, observation.DurableCheckpoint);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(prepared.State, receipt).Accepted);
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        Assert.True(applied.Accepted, applied.Reason);
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(applied.State, observation, frame).Accepted);
        var missing = inbound ? receipt with { SettledInboundWrites = [] } : receipt with { SettledOutboundPhysicalLeases = [] };
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State, missing).Accepted);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 }, receipt).Accepted);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State with { IsTerminal = true }, receipt).Accepted);
        var finalCheckpoint = observation.DurableCheckpoint with { StateEpoch = observation.DurableCheckpoint.StateEpoch + 1 };
        receipt = receipt with { DurableCheckpoint = finalCheckpoint };
        var drained = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State, receipt);
        Assert.True(drained.Accepted, drained.Reason);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(drained.State,
            receipt with { DurableCheckpoint = finalCheckpoint with { StateEpoch = finalCheckpoint.StateEpoch + 1 } }).Accepted);
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(drained.State, observation, frame).Accepted);
        var issued = FileTransferCoordinator.StartFallbackBootstrapExchange(drained.State, observation with { DurableCheckpoint = finalCheckpoint }, frame);
        Assert.True(issued.Accepted, issued.Reason);
        Assert.Equal(finalCheckpoint, issued.Command!.Bootstrap.Observation.DurableCheckpoint);
        var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(issued.State, "new_pause");
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(invalidated.State, issued.Command));
        Assert.False(FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(invalidated.State, observation, null).Accepted);
    }
}
