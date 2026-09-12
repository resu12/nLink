using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferCoordinatorSuite", "ReceiveRecoveryOperation")]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void FileTransferReceiveRecoveryOperation_CompletedPressureSuccessorUsesRealSnapshotFinalizers(
        FileTransferDirection direction,
        bool predecessorProofObserved)
        => CreateCompletedRecoveryPressureSuccessorSnapshotFixture(
            direction, predecessorProofObserved);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateCompletedRecoveryPressureSuccessorSnapshotFixture(
            FileTransferDirection direction,
            bool predecessorProofObserved,
            Action<FileTransferReceiveRecoveryDecision>? observeDecision = null,
            bool terminalizeUnprovenSuccessor = false)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        void Capture(FileTransferCoordinatorTransitionEnvelope? envelope)
        {
            var actual = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(envelope);
            if (envelopes.Count > 0)
            {
                Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration,
                    actual.Aggregate.BeforeStateGeneration);
                Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint,
                    actual.Aggregate.BeforeStateFingerprint);
            }
            envelopes.Add(actual);
        }
        void CaptureRecovery(FileTransferReceiveRecoveryDecision decision)
        {
            Capture(decision.SnapshotEnvelope);
            observeDecision?.Invoke(decision);
        }

        var fixture = CreateReceiveRecoveryOperationFixture(
            direction,
            coordinatorInstanceId: $"completed-pressure-successor-{direction}-{predecessorProofObserved}",
            captureInitial: Capture);
        var (state, predecessor, predecessorReceipt) = CompletePressureSuccessorPredecessor(
            fixture, predecessorProofObserved, envelopes, CaptureRecovery);
        return CompletePressureSuccessorHandoffAndExecution(
            state, predecessor, predecessorReceipt, predecessorProofObserved,
            terminalizeUnprovenSuccessor, envelopes, Capture, CaptureRecovery);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CompletePressureSuccessorHandoffAndExecution(
        FileTransferCoordinatorState state, FileTransferReceiveRecoveryOperation predecessor,
        FileTransferBridgeRecoveryExecutionReceipt predecessorReceipt, bool predecessorProofObserved,
        bool terminalizeUnprovenSuccessor, List<FileTransferCoordinatorTransitionEnvelope> envelopes,
        Action<FileTransferCoordinatorTransitionEnvelope?> Capture,
        Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        var predecessorState = state;
        var (prepared, transition, installedBinding) = PrepareCompletedPressureSuccessorHandoff(
            state, predecessor, Capture);
        var (committed, appliedReceipt) = CommitCompletedPressureSuccessorHandoff(
            prepared, transition, installedBinding, predecessor);
        var (successor, duplicate) = AssertCompletedPressureSuccessorHandoff(
            committed, transition, appliedReceipt, predecessor, predecessorProofObserved, envelopes, Capture);
        return CompletePressureSuccessorExecution(
            predecessorReceipt, successor, duplicate, terminalizeUnprovenSuccessor,
            predecessor, predecessorState, envelopes, Capture, CaptureRecovery);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferReceiveRecoveryOperation Predecessor,
        FileTransferBridgeRecoveryExecutionReceipt PredecessorReceipt) CompletePressureSuccessorPredecessor(
            (FileTransferCoordinatorState State, FileTransferReceiveRecoveryDecision Decision) fixture,
            bool predecessorProofObserved, List<FileTransferCoordinatorTransitionEnvelope> envelopes,
            Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        CaptureRecovery(fixture.Decision);
        Assert.Equal(0, envelopes[0].Aggregate.BeforeStateGeneration);
        Assert.Equal(1, envelopes[0].Aggregate.AfterStateGeneration);
        Assert.Equal(2, fixture.Decision.SnapshotEnvelope!.Aggregate.BeforeStateGeneration);
        Assert.Equal(1, fixture.State.CurrentRuntimeOwner!.Value.RuntimeGeneration);
        Assert.Equal(1, fixture.State.CurrentLeg!.Generation);
        Assert.Null(fixture.State.CurrentLiveRouteEpoch);
        Assert.Equal(0, fixture.State.CurrentLeg.LiveRouteEpochId);
        var predecessorAuthority = Assert.IsType<FileTransferReceiveRecoveryOperationAuthority>(
            fixture.Decision.Authority);
        var predecessorReceipt = new FileTransferBridgeRecoveryExecutionReceipt(
            predecessorAuthority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "hard",
            "bridge_recovery_completed");
        var started = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(
            fixture.Decision.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                predecessorAuthority.ExecutionCommand,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                Task.FromResult(predecessorReceipt)),
            observedUtcMs: 99);
        Assert.True(started.Accepted, started.Reason);
        CaptureRecovery(started);
        var completed = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
            started.State, predecessorReceipt, observedUtcMs: 100);
        Assert.True(completed.Accepted, completed.Reason);
        CaptureRecovery(completed);
        var state = completed.State;
        if (predecessorProofObserved)
        {
            var proof = FileTransferCoordinator.ObserveReceiveRecoveryProof(
                state,
                new FileTransferReceiveRecoveryProof(
                    predecessorAuthority,
                    "completed-pressure-predecessor-proof",
                    "receive_proof_observed",
                    101));
            Assert.True(proof.Accepted, proof.Reason);
            CaptureRecovery(proof);
            state = proof.State;
        }
        var predecessor = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            state.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Completed, predecessor.ExecutionState);
        Assert.Equal(predecessorProofObserved
            ? FileTransferReceiveRecoveryClosureState.Adopted
            : FileTransferReceiveRecoveryClosureState.Retained, predecessor.ClosureState);
        return (state, predecessor, predecessorReceipt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (RegularV4FrontierRepairPressureRecoveryHandoffDecision Prepared,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition Transition,
        RegularV4FrontierRepairPressureBinding InstalledBinding) PrepareCompletedPressureSuccessorHandoff(
            FileTransferCoordinatorState state, FileTransferReceiveRecoveryOperation predecessor,
            Action<FileTransferCoordinatorTransitionEnvelope?> Capture)
    {
        var (installed, installedBinding) = InstallCompletedPressureSuccessorBinding(state, predecessor, Capture);
        return PrepareInstalledPressureSuccessorHandoff(installed, installedBinding, predecessor, Capture);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (RegularV4FrontierRepairPressureBindingDecision Installed,
        RegularV4FrontierRepairPressureBinding InstalledBinding) InstallCompletedPressureSuccessorBinding(
            FileTransferCoordinatorState state, FileTransferReceiveRecoveryOperation predecessor,
            Action<FileTransferCoordinatorTransitionEnvelope?> Capture)
    {
        var (repairState, repairDecision) = AuthorizeRegularV4FrontierRepair(state, capture: Capture);
        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            repairState,
            repairDecision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:completed-successor-snapshot"),
            "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, staged.Status);
        Capture(staged.SnapshotEnvelope);
        var binding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(staged.Binding);
        var installed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            staged.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                binding.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed",
                binding.InstallGeneration,
                binding.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, installed.Status);
        Capture(installed.SnapshotEnvelope);
        var installedBinding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(
            PressureState(installed.State).InstalledBinding);
        Assert.Equal(predecessor,
            installed.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        return (installed, installedBinding);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (RegularV4FrontierRepairPressureRecoveryHandoffDecision Prepared,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition Transition,
        RegularV4FrontierRepairPressureBinding InstalledBinding) PrepareInstalledPressureSuccessorHandoff(
            RegularV4FrontierRepairPressureBindingDecision installed,
            RegularV4FrontierRepairPressureBinding installedBinding, FileTransferReceiveRecoveryOperation predecessor,
            Action<FileTransferCoordinatorTransitionEnvelope?> Capture)
    {
        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
            installed.State,
            CreateReceiveRecoveryHealthEvaluationObservation(
                installed.State, observationGeneration: 2, evidenceGeneration: 1,
                observedUtcMs: 102));
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Prepared, prepared.Status);
        Capture(prepared.SnapshotEnvelope);
        var transition = Assert.IsType<RegularV4FrontierRepairPressureRecoveryHandoffTransition>(
            prepared.Transition);
        Assert.Equal(predecessor.Authority.AuthorityId,
            transition.ReceiveRecoveryOperation.PredecessorAuthorityId);
        Assert.Equal(predecessor.Authority.OperationGeneration,
            transition.ReceiveRecoveryOperation.PredecessorOperationGeneration);
        return (prepared, transition, installedBinding);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (RegularV4FrontierRepairPressureRecoveryHandoffDecision Committed,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt AppliedReceipt)
        CommitCompletedPressureSuccessorHandoff(
            RegularV4FrontierRepairPressureRecoveryHandoffDecision prepared,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
            RegularV4FrontierRepairPressureBinding installedBinding,
            FileTransferReceiveRecoveryOperation predecessor)
    {
        var rejectedReceipt = AssertRejectedPressureSuccessorHandoff(prepared, transition, installedBinding, predecessor);
        var appliedReceipt = AssertMismatchedPressureSuccessorHandoff(
            prepared, transition, installedBinding, predecessor, rejectedReceipt);
        var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            prepared.State, transition, appliedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Committed, committed.Status);
        return (committed, appliedReceipt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt AssertRejectedPressureSuccessorHandoff(
        RegularV4FrontierRepairPressureRecoveryHandoffDecision prepared,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        RegularV4FrontierRepairPressureBinding installedBinding, FileTransferReceiveRecoveryOperation predecessor)
    {
        // Rejected branches must not enter the clean evidence stream returned below.
        var rejectedReceipt = new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
            transition.SettlementCommand,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "pressure_apply_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);
        var rejected = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            prepared.State, transition, rejectedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected, rejected.Status);
        Assert.Equal(installedBinding, PressureState(rejected.State).InstalledBinding);
        Assert.Equal(predecessor, rejected.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        Assert.Null(rejected.State.PersistentState.FileTransferReceiveRecovery.LastTerminalOperation);
        return rejectedReceipt;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt AssertMismatchedPressureSuccessorHandoff(
        RegularV4FrontierRepairPressureRecoveryHandoffDecision prepared,
        RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
        RegularV4FrontierRepairPressureBinding installedBinding, FileTransferReceiveRecoveryOperation predecessor,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt rejectedReceipt)
    {
        var appliedReceipt = rejectedReceipt with
        {
            Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            Reason = "current_authority_cleared",
            CurrentAuthorityCleared = true,
        };
        var mismatched = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            prepared.State,
            transition,
            appliedReceipt with
            {
                Command = transition.SettlementCommand with
                {
                    InstallGeneration = transition.SettlementCommand.InstallGeneration + 1,
                },
            });
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected, mismatched.Status);
        Assert.Equal(installedBinding, PressureState(mismatched.State).InstalledBinding);
        Assert.Equal(predecessor, mismatched.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        return appliedReceipt;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferReceiveRecoveryOperation Successor,
        RegularV4FrontierRepairPressureRecoveryHandoffDecision Duplicate) AssertCompletedPressureSuccessorHandoff(
            RegularV4FrontierRepairPressureRecoveryHandoffDecision committed,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition transition,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt appliedReceipt,
            FileTransferReceiveRecoveryOperation predecessor, bool predecessorProofObserved,
            List<FileTransferCoordinatorTransitionEnvelope> envelopes,
            Action<FileTransferCoordinatorTransitionEnvelope?> Capture)
    {
        Capture(committed.SnapshotEnvelope);
        var handoffEnvelope = envelopes[^1];
        Assert.Equal(handoffEnvelope.Aggregate.BeforeStateGeneration + 1,
            handoffEnvelope.Aggregate.AfterStateGeneration);
        Assert.Equal(["receive_recovery", "regular_v4_frontier_repair_pressure"],
            handoffEnvelope.Aggregate.ChangedFamilies);
        Assert.Contains(handoffEnvelope.Aggregate.Operations, operation => operation is
        {
            Family: "receive_recovery",
            OperationRole: "operation_current",
            BeforeState: "None",
            AfterState: "Issued:None",
            StateApplied: true,
        } && operation.AuthorityId == transition.ReceiveRecoveryOperation.AuthorityId.ToString());
        Assert.Contains(handoffEnvelope.Aggregate.Operations, operation => operation is
        {
            Family: "regular_v4_frontier_repair_pressure",
            OperationRole: "recovery_handoff_terminal",
            StateApplied: true,
        });
        Assert.Null(PressureState(committed.State).InstalledBinding);
        Assert.Null(committed.State.CurrentRepairReplayDecision);
        var successor = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            committed.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        Assert.Equal(transition.ReceiveRecoveryOperation, successor.Authority);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Issued, successor.ExecutionState);
        var terminal = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            committed.State.PersistentState.FileTransferReceiveRecovery.LastTerminalOperation);
        Assert.Equal(predecessor.Authority, terminal.Authority);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Invalidated, terminal.ExecutionState);
        Assert.Equal(FileTransferReceiveRecoveryClosureState.None, terminal.ClosureState);
        Assert.Equal(predecessor.ExecutionReceipt, terminal.ExecutionReceipt);
        Assert.Equal(predecessor.ReceiveProof, terminal.ReceiveProof);
        Assert.Equal(predecessor.RetiredRecoveryLivenessLease, terminal.RetiredRecoveryLivenessLease);
        Assert.Equal(predecessorProofObserved
            ? "completed_proven_unreserved_receive_recovery_superseded_by_newer_health_observation"
            : "completed_unproven_receive_recovery_superseded_by_newer_health_observation",
            terminal.InvalidationReason);

        var duplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            committed.State, transition, appliedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Idempotent, duplicate.Status);
        Capture(duplicate.SnapshotEnvelope);
        Assert.Equal(committed.State.PersistentState.FileTransferReceiveRecovery,
            duplicate.State.PersistentState.FileTransferReceiveRecovery);
        return (successor, duplicate);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CompletePressureSuccessorExecution(
        FileTransferBridgeRecoveryExecutionReceipt predecessorReceipt, FileTransferReceiveRecoveryOperation successor,
        RegularV4FrontierRepairPressureRecoveryHandoffDecision duplicate, bool terminalizeUnprovenSuccessor,
        FileTransferReceiveRecoveryOperation predecessor, FileTransferCoordinatorState predecessorState,
        List<FileTransferCoordinatorTransitionEnvelope> envelopes,
        Action<FileTransferCoordinatorTransitionEnvelope?> Capture,
        Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        var successorReceipt = predecessorReceipt with { Command = successor.Authority.ExecutionCommand };
        var successorCompleted = ExecutePressureSuccessorRecovery(successor, duplicate, successorReceipt, CaptureRecovery);
        return AssertPressureSuccessorRecoveryCompletion(
            successor, successorReceipt, successorCompleted, terminalizeUnprovenSuccessor,
            predecessor, predecessorState, envelopes, Capture, CaptureRecovery);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferReceiveRecoveryDecision ExecutePressureSuccessorRecovery(
        FileTransferReceiveRecoveryOperation successor, RegularV4FrontierRepairPressureRecoveryHandoffDecision duplicate,
        FileTransferBridgeRecoveryExecutionReceipt successorReceipt,
        Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        var successorStarted = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(
            duplicate.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                successor.Authority.ExecutionCommand,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                Task.FromResult(successorReceipt)),
            observedUtcMs: 103);
        Assert.True(successorStarted.Accepted, successorStarted.Reason);
        CaptureRecovery(successorStarted);
        var successorCompleted = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
            successorStarted.State, successorReceipt, observedUtcMs: 104);
        Assert.True(successorCompleted.Accepted, successorCompleted.Reason);
        CaptureRecovery(successorCompleted);
        return successorCompleted;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> AssertPressureSuccessorRecoveryCompletion(
        FileTransferReceiveRecoveryOperation successor, FileTransferBridgeRecoveryExecutionReceipt successorReceipt,
        FileTransferReceiveRecoveryDecision successorCompleted, bool terminalizeUnprovenSuccessor,
        FileTransferReceiveRecoveryOperation predecessor, FileTransferCoordinatorState predecessorState,
        List<FileTransferCoordinatorTransitionEnvelope> envelopes,
        Action<FileTransferCoordinatorTransitionEnvelope?> Capture,
        Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        if (terminalizeUnprovenSuccessor)
        {
            return AssertUnprovenPressureSuccessorTerminalized(successor, successorReceipt, successorCompleted, envelopes, Capture);
        }

        return AssertProvenPressureSuccessorCompleted(successor, successorCompleted, predecessor, predecessorState, envelopes, CaptureRecovery);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> AssertUnprovenPressureSuccessorTerminalized(
        FileTransferReceiveRecoveryOperation successor, FileTransferBridgeRecoveryExecutionReceipt successorReceipt,
        FileTransferReceiveRecoveryDecision successorCompleted, List<FileTransferCoordinatorTransitionEnvelope> envelopes,
        Action<FileTransferCoordinatorTransitionEnvelope?> Capture)
    {
        var terminalized = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                successorCompleted.State.RouteSelection,
                reason: "completed"),
            successorCompleted.State);
        Assert.Null(terminalized.MutationRejectedReason);
        Assert.True(terminalized.State.IsTerminal);
        Capture(terminalized.SnapshotEnvelope);
        var terminalRecovery = terminalized.State.PersistentState.FileTransferReceiveRecovery;
        Assert.Null(terminalRecovery.CurrentOperation);
        var unprovenTerminal = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            terminalRecovery.LastTerminalOperation);
        Assert.Equal(successor.Authority, unprovenTerminal.Authority);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Invalidated,
            unprovenTerminal.ExecutionState);
        Assert.Equal("terminal_state", unprovenTerminal.InvalidationReason);
        Assert.Equal(successorReceipt, unprovenTerminal.ExecutionReceipt);
        Assert.Null(unprovenTerminal.ReceiveProof);
        return envelopes;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> AssertProvenPressureSuccessorCompleted(
        FileTransferReceiveRecoveryOperation successor, FileTransferReceiveRecoveryDecision successorCompleted,
        FileTransferReceiveRecoveryOperation predecessor, FileTransferCoordinatorState predecessorState,
        List<FileTransferCoordinatorTransitionEnvelope> envelopes, Action<FileTransferReceiveRecoveryDecision> CaptureRecovery)
    {
        var successorProof = new FileTransferReceiveRecoveryProof(
            successor.Authority, "completed-pressure-successor-proof", "receive_proof_observed", 105);
        var successorProved = FileTransferCoordinator.ObserveReceiveRecoveryProof(
            successorCompleted.State, successorProof);
        Assert.True(successorProved.Accepted, successorProved.Reason);
        CaptureRecovery(successorProved);
        var closed = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            successorProved.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        Assert.Equal(successor.Authority, closed.Authority);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Completed, closed.ExecutionState);
        Assert.Equal(FileTransferReceiveRecoveryClosureState.Adopted, closed.ClosureState);
        Assert.Equal(successorProof, closed.ReceiveProof);
        var duplicateProof = FileTransferCoordinator.ObserveReceiveRecoveryProof(successorProved.State, successorProof);
        Assert.Equal(FileTransferReceiveRecoveryTransitionStatus.Idempotent, duplicateProof.Status);
        CaptureRecovery(duplicateProof);
        Assert.Equal(closed, duplicateProof.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        Assert.Equal(predecessor, predecessorState.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        return envelopes;
    }
}
