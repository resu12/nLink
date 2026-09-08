using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void ReceiverBarrier_RecoveryDecisionOverlayKeepsExactRawSourceThroughPreparation()
        => CreateReceiverBarrierRecoveryDecisionOverlayFixture();

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateReceiverBarrierRecoveryDecisionOverlayFixture(bool completeLifecycle = false,
            Action<FileTransferReceiveRecoveryDecision>? observeRecoveryDecision = null)
    {
        var envelopes = CreateReceiverBarrierRoutePreparationFixture(
            completeLifecycle, (state, capture) => PrepareReceiverBarrierRecoveryDecisionOverlaySource(
                state, capture, observeRecoveryDecision));
        var handoff = Assert.Single(envelopes.Where(envelope =>
            envelope.DecisionKind == nameof(FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff)));
        Assert.Equal(0, handoff.Aggregate.BeforeBoundary.BridgeRecoveryGeneration);
        Assert.Equal(1, handoff.Aggregate.AfterBoundary.BridgeRecoveryGeneration);
        var recovery = Assert.Single(handoff.Aggregate.Operations.Where(operation =>
            operation.Family == "receive_recovery" && operation.OperationRole == "operation_current"));
        Assert.Equal("BridgeReceiveStallRecovery", recovery.OperationKind);
        Assert.Equal("None", recovery.BeforeState);
        Assert.Equal("Issued:None", recovery.AfterState);
        Assert.True(recovery.StateApplied);
        var adopted = Assert.Single(envelopes.Where(envelope =>
            envelope.DecisionKind == nameof(FileTransferCoordinator.ObserveReceiveRecoveryProof)));
        Assert.Contains(adopted.Aggregate.Operations, operation =>
            operation.AuthorityId == recovery.AuthorityId && operation.AfterState == "Completed:Adopted");

        var bind = Assert.Single(envelopes.Where(envelope =>
            envelope.DecisionKind == nameof(FileTransferCoordinator.BindInitialActivationReceiverBarrier)));
        var bound = Assert.Single(bind.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "receiver_barrier_current"));
        Assert.Equal("Bound", bound.AfterState);
        Assert.Equal(0, bound.ReceiverBarrier!.SourceBoundary.BridgeRecoveryGeneration);
        Assert.Equal(1, bound.ReceiverBarrier.EffectiveBoundary.BridgeRecoveryGeneration);
        Assert.Equal(bound.ReceiverBarrier.SourceBoundary with { BridgeRecoveryGeneration = 1 },
            bound.ReceiverBarrier.EffectiveBoundary);
        Assert.Equal(bind.Aggregate.AfterBoundary, bound.ReceiverBarrier.EffectiveBoundary);

        var preparation = Assert.Single(envelopes.Where(envelope =>
            envelope.DecisionKind == nameof(FileTransferCoordinator.PrepareFileTransferRouteTransition)));
        var invalidated = Assert.Single(preparation.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "receiver_barrier_terminal"));
        Assert.Equal("Invalidated", invalidated.AfterState);
        Assert.Equal(bound.AuthorityId, invalidated.AuthorityId);
        Assert.Equal(bound.ReceiverBarrier.SourceBoundary, invalidated.ReceiverBarrier!.SourceBoundary);
        Assert.Equal(bound.ReceiverBarrier.EffectiveBoundary, invalidated.ReceiverBarrier.EffectiveBoundary);
        var route = Assert.Single(preparation.Aggregate.Operations.Where(operation =>
            operation.Family == "route_transition" && operation.OperationRole == "current"));
        Assert.Equal(0, route.RouteTransition!.SourceBoundary.BridgeRecoveryGeneration);
        Assert.Equal(1, route.RouteTransition.SourceBoundary.CheckpointGeneration);
        Assert.Empty(route.RouteTransition.Receipts);
        Assert.False(route.ReceiptValidated);
        var ordered = envelopes.ToList();
        Assert.True(ordered.IndexOf(handoff) < ordered.IndexOf(adopted));
        Assert.True(ordered.IndexOf(adopted) < ordered.IndexOf(bind));
        Assert.True(ordered.IndexOf(bind) < ordered.IndexOf(preparation));
        return envelopes;
    }

    private static FileTransferCoordinatorState PrepareReceiverBarrierRecoveryDecisionOverlaySource(
        FileTransferCoordinatorState source, Action<FileTransferCoordinatorTransitionEnvelope> capture,
        Action<FileTransferReceiveRecoveryDecision>? observeRecoveryDecision)
    {
        var sourceFingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(source);
        var (repairState, repair) = AuthorizeRegularV4FrontierRepair(source, capture: capture);
        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            repairState, repair, RegularV4PressureAuthorityRequest("receiver-barrier-recovery-overlay"),
            "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, staged.Status);
        capture(staged.SnapshotEnvelope!);
        var binding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(staged.Binding);
        var installed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(staged.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(binding.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, "installed",
                binding.InstallGeneration, binding.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, installed.Status);
        capture(installed.SnapshotEnvelope!);
        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
            installed.State, CreateReceiveRecoveryObservationForTest(installed.State, "session_liveness_timeout_pending"));
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Prepared, prepared.Status);
        capture(prepared.SnapshotEnvelope!);
        var transition = Assert.IsType<RegularV4FrontierRepairPressureRecoveryHandoffTransition>(prepared.Transition);
        var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(prepared.State,
            transition, new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                transition.SettlementCommand, FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "current_authority_cleared", CurrentAuthorityCleared: true, RetainedPredecessorCleared: false));
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Committed, committed.Status);
        capture(committed.SnapshotEnvelope!);
        var authority = transition.ReceiveRecoveryOperation;
        var receipt = new FileTransferBridgeRecoveryExecutionReceipt(authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed, "hard", "bridge_recovery_completed");
        var started = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(committed.State,
            new FileTransferBridgeRecoveryExecutionStartResult(authority.ExecutionCommand,
                FileTransferBridgeRecoveryExecutionStartStatus.Started, "bridge_recovery_execution_started",
                Task.FromResult(receipt)), observedUtcMs: 99);
        Assert.True(started.Accepted, started.Reason);
        capture(started.SnapshotEnvelope!);
        observeRecoveryDecision?.Invoke(started);
        var completed = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(started.State, receipt, observedUtcMs: 100);
        Assert.True(completed.Accepted, completed.Reason);
        capture(completed.SnapshotEnvelope!);
        observeRecoveryDecision?.Invoke(completed);
        var adopted = FileTransferCoordinator.ObserveReceiveRecoveryProof(completed.State,
            new FileTransferReceiveRecoveryProof(authority, "receiver-barrier-recovery-proof", "receive_proof_observed", 101));
        Assert.True(adopted.Accepted, adopted.Reason);
        capture(adopted.SnapshotEnvelope!);
        observeRecoveryDecision?.Invoke(adopted);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Completed,
            adopted.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation!.Value.ExecutionState);
        Assert.Equal(FileTransferReceiveRecoveryClosureState.Adopted,
            adopted.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation!.Value.ClosureState);
        Assert.Equal(0, adopted.State.CurrentLeg!.BridgeRecoveryGeneration);
        Assert.Equal(1, adopted.State.CurrentBridgeRecoveryDecision!.Value.BridgeDecisionGeneration);
        Assert.Equal(source.CurrentRuntimeOwner, adopted.State.CurrentRuntimeOwner);
        Assert.Equal(source.CurrentRoutePublicationToken, adopted.State.CurrentRoutePublicationToken);
        Assert.Equal(sourceFingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(source));
        return adopted.State;
    }
}
