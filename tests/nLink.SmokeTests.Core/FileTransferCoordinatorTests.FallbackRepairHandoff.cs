using System.Collections.Immutable;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairWriteScope_StartupEpochDoesNotGrantTailYieldAuthority(int liveRouteEpoch)
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var owner = source.CurrentRuntimeOwner!.Value with { LiveRouteEpoch = liveRouteEpoch };
        source = source with
        {
            CurrentLeg = MutateRepairHandoffLeg(source.CurrentLeg!, "none", liveRouteEpoch),
            CurrentLiveRouteEpoch = null,
            CurrentRuntimeOwner = owner,
            CurrentRoutePublicationToken = source.CurrentRoutePublicationToken!.Value with
            {
                RuntimeOwner = owner, LiveRouteEpoch = liveRouteEpoch,
            },
        };
        var scope = Assert.IsType<FileTransferFallbackTailYieldScope>(
            FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(source));
        Assert.Equal(liveRouteEpoch, scope.LiveRouteEpoch);
        Assert.Equal(liveRouteEpoch > 0, scope.HasCompleteExecutionScope);
        Assert.True(FileTransferCoordinator.IsInboundFallbackRepairWriteScopeCurrent(source, scope));
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairWriteScopeCurrent(source, scope with { LiveRouteEpoch = -1 }));
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairWriteScopeCurrent(
            source with { CurrentDataSessionGeneration = source.CurrentDataSessionGeneration + 1 }, scope));
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairWriteScopeCurrent(
            source with { CurrentRoutePublicationToken = null }, scope));
        Assert.Null(FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(
            source with { CurrentDataSessionGeneration = 0 }));
        Assert.Null(FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(
            source with { IsTerminal = true }));
        Assert.Null(FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(
            source with { Direction = FileTransferDirection.Outbound }));
        if (liveRouteEpoch == 0)
        {
            var receipt = peer with { ObservationScope = scope };
            Assert.True(receipt.IsDefault);
            var rejected = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, receipt);
            Assert.False(rejected.Accepted, rejected.Reason);
            Assert.Equal(source.CurrentRuntimeOwner, rejected.State.CurrentRuntimeOwner);
            Assert.Equal(source.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
            AssertRejectedRepairHandoff(source, FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, receipt));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateRepairHandoffEvidenceFixture(string terminal)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        if (terminal is "adopt" or "rollback")
        {
            var fixture = CreateReadyRepairHandoffTransition(envelopes.Add);
            if (terminal == "adopt")
            {
                CommitEmptyFileTransferRouteTransitionForTest(fixture.State, fixture.Request,
                    fixture.FinalProof, invalidateRuntimeUnlockRetryLifecycle: false, capture: envelopes.Add);
            }
            else
            {
                var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(fixture.State, fixture.Request);
                envelopes.Add(prepared.SnapshotEnvelope!);
                var invalidated = FileTransferCoordinator.InvalidateFileTransferRouteTransition(prepared.State,
                    prepared.Authority!.Value, terminalInvalidation: false, "precommit_probe_rejected");
                envelopes.Add(invalidated.SnapshotEnvelope!);
                var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(invalidated.State,
                    new(invalidated.DispositionCommand!.Value, FileTransferRouteTransitionDispositionApplyStatus.Applied,
                        "filetransfer_route_transition_rolled_back"));
                envelopes.Add(applied.SnapshotEnvelope!);
                var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(applied.State,
                    applied.State.PersistentState.FallbackTailYield.RepairHandoff!.Command, invalidated.DispositionCommand);
                Assert.True(restored.Accepted, restored.Reason);
                envelopes.Add(restored.SnapshotEnvelope!);
            }
        }
        else
        {
            var (source, peer) = CreateInboundRepairHandoffSource(envelopes.Add);
            var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
            envelopes.Add(prepared.SnapshotEnvelope!);
            var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
            var rejected = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture with { PauseInstance = 0 });
            envelopes.Add(rejected.SnapshotEnvelope!);
            var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(rejected.State, capture);
            envelopes.Add(captured.SnapshotEnvelope!);
            var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, CreateInboundRepairHandoffDrain(capture));
            envelopes.Add(drained.SnapshotEnvelope!);
            var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(drained.State, capture.Command);
            Assert.True(restored.Accepted, restored.Reason);
            envelopes.Add(restored.SnapshotEnvelope!);
        }
        return envelopes.ToArray();
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_ParkedWorkerDoesNotWaiveAnIssuedBridgeRecovery()
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, CreateInboundRepairHandoffDrain(capture));
        var recovery = FileTransferCoordinator.IssueReceiveRecoveryOperation(drained.State,
            CreateReceiveRecoveryObservationForTest(drained.State, "session_liveness_timeout_pending"),
            BridgeRecoveryKind.BridgeReceiveStallRecovery, drained.State.CurrentLeg!.CheckpointRequestId);
        Assert.Equal(FileTransferReceiveRecoveryTransitionStatus.Accepted, recovery.Status);
        Assert.NotNull(recovery.State.CurrentBridgeRecoveryExecutionCommand);
        Assert.False(FileTransferCoordinator.HasDrainedInboundFallbackRepairHandoff(recovery.State));
        Assert.False(RequestRuntimeUnlockAdmission(recovery.State, recovery.State.CurrentRuntimeOwner).RuntimeUnlockAdmissionDecision!.Value.Granted);
        AssertRejectedRepairHandoff(recovery.State, FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(recovery.State, capture.Command));
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_ExactTunaCommitAdoptsTheRetainedGapMap()
        => AssertRepairHandoffTunaCommit(CreateReadyRepairHandoffTransition());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRepairHandoffTunaCommit((FileTransferCoordinatorState State,
        FileTransferRouteTransitionPrepareRequest Request, RuntimeUnlockRouteCommitProof FinalProof) fixture)
    {
        var hold = fixture.State.PersistentState.FallbackTailYield.RepairHandoff!;
        var committed = CommitEmptyFileTransferRouteTransitionForTest(fixture.State, fixture.Request,
            fixture.FinalProof, invalidateRuntimeUnlockRetryLifecycle: false);
        var adopted = committed.State.PersistentState.FallbackTailYield.RepairHandoff!;
        Assert.Equal(FileTransferFallbackRepairHandoffState.Adopted, adopted.State);
        Assert.Equal(hold.DrainReceipt, adopted.DrainReceipt);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.State.RouteSelection.Route);
        Assert.Equal(4, committed.State.RouteSelection.ProtocolVersion);
        Assert.Equal(committed.State.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.DispositionCommand,
            adopted.TunaDisposition);
        Assert.NotEqual(fixture.State.CurrentRoutePublicationToken, committed.State.CurrentRoutePublicationToken);
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairHandoffCurrent(committed.State, hold.Command));
        AssertRejectedRepairHandoff(committed.State, FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(committed.State, hold.Command));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_RollbackRequiresExactAppliedDisposition(bool newerAdmission)
        => AssertRepairHandoffRollback(CreateReadyRepairHandoffTransition(), newerAdmission);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRepairHandoffRollback((FileTransferCoordinatorState State,
        FileTransferRouteTransitionPrepareRequest Request, RuntimeUnlockRouteCommitProof FinalProof) fixture, bool newerAdmission)
    {
        var hold = fixture.State.PersistentState.FallbackTailYield.RepairHandoff!;
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(fixture.State, fixture.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
        var invalidated = FileTransferCoordinator.InvalidateFileTransferRouteTransition(prepared.State,
            prepared.Authority!.Value, terminalInvalidation: false, "precommit_probe_rejected");
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Invalidated, invalidated.Status);
        var rollback = invalidated.DispositionCommand!.Value;
        AssertRejectedRepairHandoff(invalidated.State,
            FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(invalidated.State, hold.Command, rollback));
        var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(invalidated.State,
            new(rollback, FileTransferRouteTransitionDispositionApplyStatus.Applied, "filetransfer_route_transition_rolled_back"));
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Invalidated, applied.Status);
        var source = applied.State;
        if (newerAdmission)
        {
            source = source with { CurrentRuntimeUnlockAdmission = source.CurrentRuntimeUnlockAdmission!.Value with
                { AdmissionGeneration = source.CurrentRuntimeUnlockAdmission.Value.AdmissionGeneration + 1 } };
            AssertRejectedRepairHandoff(source, FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(source, hold.Command, rollback));
            return;
        }
        var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(source, hold.Command, rollback);
        Assert.True(restored.Accepted, restored.Reason);
        Assert.Equal(hold.DrainReceipt!.MissingRanges, restored.RestoreCommand!.MissingRanges);
        Assert.Equal(hold.DrainReceipt.Checkpoint, restored.RestoreCommand.Checkpoint);
        Assert.Equal(fixture.State.CurrentRuntimeOwner, restored.State.CurrentRuntimeOwner);
        Assert.Equal(fixture.State.CurrentRoutePublicationToken, restored.State.CurrentRoutePublicationToken);
        Assert.Null(restored.State.CurrentRuntimeUnlockAdmission);
        Assert.Null(restored.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, restored.State.RouteSelection.Route);
        Assert.Equal(6, restored.State.RouteSelection.ProtocolVersion);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferRouteTransitionPrepareRequest Request,
        RuntimeUnlockRouteCommitProof FinalProof) CreateReadyRepairHandoffTransition(Action<FileTransferCoordinatorTransitionEnvelope>? captureSnapshot = null)
    {
        var (source, peer) = CreateInboundRepairHandoffSource(captureSnapshot);
        if (captureSnapshot is null)
        {
            source = source with { RuntimeUnlockFallbackSurvivalState = source.RuntimeUnlockFallbackSurvivalState with
                { BridgeRecoveryPending = true, FallbackTailRepairPending = true } };
        }
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        captureSnapshot?.Invoke(prepared.SnapshotEnvelope!);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        captureSnapshot?.Invoke(captured.SnapshotEnvelope!);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, CreateInboundRepairHandoffDrain(capture));
        captureSnapshot?.Invoke(drained.SnapshotEnvelope!);
        var yielded = MarkFallbackTailQuiescent(drained.State, drained.State.RouteSelection,
            drained.State.CurrentLeg!.TransportEpochId, 12_106, 12_483);
        captureSnapshot?.Invoke(yielded.SnapshotEnvelope!);
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State,
            yielded.FallbackTailYieldDecision!.Value, "repair-handoff-transition-local", "repair-handoff-transition-frame", 50_001);
        var paired = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, local);
        captureSnapshot?.Invoke(paired.SnapshotEnvelope!);
        var admitted = RequestRuntimeUnlockAdmission(paired.State, paired.State.CurrentRuntimeOwner);
        captureSnapshot?.Invoke(admitted.SnapshotEnvelope!);
        var admission = admitted.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var pendingDecision = RecordPendingPreCommitProbe(admitted.State, admission);
        captureSnapshot?.Invoke(pendingDecision.SnapshotEnvelope!);
        var pending = pendingDecision.State.CurrentRuntimeUnlockPendingPreCommitProbe!.Value;
        return BindRepairHandoffResponderTransition(pendingDecision.State, paired.PairedRelease!.Value, admission, pending, capture.Checkpoint, captureSnapshot);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferRouteTransitionPrepareRequest Request,
        RuntimeUnlockRouteCommitProof FinalProof) BindRepairHandoffResponderTransition(FileTransferCoordinatorState state,
        FileTransferFallbackTailPairedReleaseReceipt release, RuntimeUnlockAdmissionToken admission,
        RuntimeUnlockPendingPreCommitProbeRequest pending, FileTransferRouteTransitionDurableCheckpoint checkpoint,
        Action<FileTransferCoordinatorTransitionEnvelope>? captureSnapshot = null)
    {
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(state, pending.TransactionGeneration,
            pending.OfferGeneration, "repair-handoff-responder-probe");
        var proof = WithCurrentTunaLease(RuntimeUnlockTransaction.CreateRouteCommitProof(transaction,
            "runtime_unlock_transaction_peer_proof", admission), pending.TunaPathLeaseGeneration, pending.TunaPathLeaseListenerRunId!);
        var scope = new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(state.CurrentRuntimeOwner!.Value,
            state.CurrentRoutePublicationToken!.Value, state.TerminalGeneration, state.CurrentLeg!.Generation,
            state.CurrentLeg.TransportEpochId, state.CurrentLeg.BridgeRecoveryGeneration,
            state.CurrentLeg.CheckpointRequestId ?? "(none)", state.CurrentLeg.CheckpointGeneration,
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration, admission,
            RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired);
        var observation = new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(pending.SessionId, pending.TransferId,
            pending.TransactionGeneration, pending.OfferGeneration, PendingAnswerAckGeneration: 5, PayerDecisionId: 7, LaunchGeneration: 31,
            Lanes: "file", AnswerAcceptanceSource: "SemanticAnswerAck", AckMessageId: "repair-handoff-answer-ack",
            AckSourceFingerprint: new string('b', 64), PeerProbeFrameFingerprint: new string('c', 64),
            pending.TunaPathLeaseGeneration, pending.TunaPathLeaseListenerRunId, proof.PathProbeId!, RuntimeUnlockPathProbeState.Acked,
            FileTransferTransportKind.Tuna, proof.PathProbeAckedUtcMs, FileTransferRoute.FileTunaV4, 4,
            FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, scope);
        var bound = FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(state, observation, admission, pending);
        Assert.Equal(RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Accepted, bound.Status);
        captureSnapshot?.Invoke(bound.SnapshotEnvelope!);
        return (bound.State, new(FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            state.CurrentRuntimeOwner.Value, state.CurrentRoutePublicationToken.Value, SourceRouteCommit: null,
            FileTransferRoute.PostTunaFallbackV6, 6, FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(admission, pending,
                proof with { Reason = "runtime_unlock_responder_peer_probe_acked" }, observation.LaunchGeneration,
                EffectiveRetryInstallReceipt: null, release, bound.Authority), checkpoint, FileTransferRoute.FileTunaV4, 4,
            FileTransferTransportKind.Tuna, FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null, "repair_handoff_transition"), proof);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_EofWithEarlierGapsRetainsSourceOwnershipUntilRuntimeHandoff()
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        Assert.True(prepared.Accepted, prepared.Reason);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var receipt = CreateInboundRepairHandoffDrain(capture);
        AssertRejectedRepairHandoff(prepared.State,
            FileTransferCoordinator.DrainInboundFallbackRepairHandoff(prepared.State, receipt));
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, receipt);
        Assert.True(drained.Accepted, drained.Reason);
        var operation = drained.State.PersistentState.FallbackTailYield.RepairHandoff!;
        Assert.Equal(FileTransferFallbackRepairHandoffState.Drained, operation.State);
        Assert.Equal(12_483, operation.DrainReceipt!.Checkpoint.DurableHighestChunkIndex);
        Assert.Equal(12_106, operation.DrainReceipt.Checkpoint.ContiguousCommittedChunkIndex);
        Assert.Equal(capture.MissingRanges, operation.DrainReceipt.MissingRanges);
        Assert.Equal(source.CurrentRuntimeOwner, drained.State.CurrentRuntimeOwner);
        Assert.Equal(source.CurrentRoutePublicationToken, drained.State.CurrentRoutePublicationToken);
        Assert.Equal(source.CurrentRepairReplayDecision, drained.State.CurrentRepairReplayDecision);
        Assert.Equal(source.CurrentLeg, drained.State.CurrentLeg);
        Assert.Null(drained.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Null(drained.State.CurrentRuntimeUnlockAdmission);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, drained.State.RouteSelection.Route);
        Assert.Equal(6, drained.State.RouteSelection.ProtocolVersion);
        Assert.Equal(peer, drained.State.PersistentState.FallbackTailYield.PendingPeerProofBeforeLocalYield);
        var admission = RequestRuntimeUnlockAdmission(drained.State, drained.State.CurrentRuntimeOwner);
        Assert.False(admission.RuntimeUnlockAdmissionDecision!.Value.Granted);
        var snapshot = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(drained.SnapshotEnvelope);
        var change = Assert.Single(snapshot.Aggregate.Operations,
            operation => operation.OperationRole == "repair_handoff");
        Assert.Equal("Captured", change.BeforeState);
        Assert.Equal("Drained", change.AfterState);
        Assert.True(change.StateApplied);
        Assert.True(change.ReceiptValidated);
        Assert.NotEqual(snapshot.Aggregate.BeforeStateFingerprint, snapshot.Aggregate.AfterStateFingerprint);
        Assert.Null(change.RouteRuntime);
        Assert.Equal(6, change.FallbackRepairHandoff!.SourceBoundary.ProtocolVersion);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_OnlyCapturedWritesMayAdvanceTheExactGapMap()
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        var receipt = CreateInboundRepairHandoffDrain(capture);
        var advanced = receipt with
        {
            Checkpoint = receipt.Checkpoint with { StateEpoch = 6, ContiguousCommittedChunkIndex = 12_290 },
            MissingRanges = [new(12_290, 1)],
        };
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, advanced);
        Assert.True(drained.Accepted, drained.Reason);
        Assert.Equal(advanced, drained.State.PersistentState.FallbackTailYield.RepairHandoff!.DrainReceipt);
        Assert.Null(drained.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Null(drained.State.CurrentRuntimeUnlockAdmission);
        AssertRejectedRepairHandoff(captured.State,
            FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State,
                receipt with { MissingRanges = [new(12_106, 2), new(12_295, 1)] }));
        var emptyCapture = capture with { InboundWrites = [] };
        var emptyCaptured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, emptyCapture);
        Assert.True(emptyCaptured.Accepted, emptyCaptured.Reason);
        AssertRejectedRepairHandoff(emptyCaptured.State,
            FileTransferCoordinator.DrainInboundFallbackRepairHandoff(emptyCaptured.State,
                advanced with { Capture = emptyCapture, SettledInboundWrites = [] }));
    }

    [Theory]
    [InlineData("missing-write")]
    [InlineData("extra-write")]
    [InlineData("missing-feedback")]
    [InlineData("wrong-pause")]
    [InlineData("wrong-checkpoint-generation")]
    [InlineData("wrong-data-session")]
    [InlineData("wrong-chunk-count")]
    [InlineData("frontier-regression")]
    [InlineData("map-loses-written-chunk")]
    [InlineData("noncanonical-map")]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_RejectsIncompleteOrChangedDrainReceipt(string mutation)
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        var receipt = CreateInboundRepairHandoffDrain(capture);
        receipt = mutation switch
        {
            "missing-write" => receipt with { SettledInboundWrites = [11] },
            "extra-write" => receipt with { SettledInboundWrites = [11, 12, 13] },
            "missing-feedback" => receipt with { SettledInboundFeedback = [] },
            "wrong-pause" => receipt with { Capture = capture with { PauseInstance = 2 } },
            "wrong-checkpoint-generation" => receipt with { Checkpoint = receipt.Checkpoint with { CheckpointGeneration = capture.Checkpoint.CheckpointGeneration + 1 } },
            "wrong-data-session" => receipt with { Checkpoint = receipt.Checkpoint with { DataSessionGeneration = capture.Checkpoint.DataSessionGeneration + 1 } },
            "wrong-chunk-count" => receipt with { Checkpoint = receipt.Checkpoint with { ChunkCount = capture.Checkpoint.ChunkCount + 1 } },
            "frontier-regression" => receipt with { Checkpoint = receipt.Checkpoint with { ContiguousCommittedChunkIndex = 12_105 }, MissingRanges = [new(12_105, 3), new(12_290, 1)] },
            "map-loses-written-chunk" => receipt with { MissingRanges = [new(12_106, 3), new(12_290, 1)] },
            "noncanonical-map" => receipt with { MissingRanges = [new(12_106, 1), new(12_107, 1), new(12_290, 1)] },
            _ => throw new ArgumentException(mutation),
        };
        AssertRejectedRepairHandoff(captured.State,
            FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, receipt));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("data-session")]
    [InlineData("coordinator-instance")]
    [InlineData("publication")]
    [InlineData("owner")]
    [InlineData("terminal-generation")]
    [InlineData("data-session-identity")]
    [InlineData("transport-epoch")]
    [InlineData("bridge-generation")]
    [InlineData("checkpoint-generation")]
    [InlineData("checkpoint-pending")]
    [InlineData("send-disabled")]
    [InlineData("leg-generation")]
    [InlineData("protocol")]
    [InlineData("route")]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_StaleSourceCannotDrainOrResumeFallback(string mutation)
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        var stale = mutation switch
        {
            "terminal" => captured.State with { IsTerminal = true },
            "data-session" => captured.State with { CurrentDataSessionGeneration = captured.State.CurrentDataSessionGeneration + 1 },
            "coordinator-instance" => captured.State with { PersistentState = captured.State.PersistentState with { CoordinatorInstanceId = "different-coordinator" } },
            "publication" => captured.State with { CurrentRoutePublicationToken = captured.State.CurrentRoutePublicationToken!.Value with { PublicationGeneration = captured.State.CurrentRoutePublicationToken.Value.PublicationGeneration + 1 } },
            "owner" => captured.State with { CurrentRuntimeOwner = captured.State.CurrentRuntimeOwner!.Value with { RuntimeGeneration = captured.State.CurrentRuntimeOwner.Value.RuntimeGeneration + 1 } },
            "terminal-generation" => captured.State with { TerminalGeneration = captured.State.TerminalGeneration + 1 },
            "data-session-identity" => captured.State with { CurrentDataSessionIdentity = "different-data-session" },
            "transport-epoch" or "bridge-generation" or "checkpoint-generation" or "checkpoint-pending" or
                "send-disabled" or "leg-generation" or "protocol" or "route" => captured.State with
                { CurrentLeg = MutateRepairHandoffLeg(captured.State.CurrentLeg!, mutation) },
            _ => throw new ArgumentException(mutation),
        };
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairHandoffCurrent(stale, capture.Command));
        AssertRejectedRepairHandoff(stale, FileTransferCoordinator.DrainInboundFallbackRepairHandoff(stale, CreateInboundRepairHandoffDrain(capture)));
        AssertRejectedRepairHandoff(stale, FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(stale, capture.Command));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_RestorationRetainsExactGapsAndCannotReplay(bool drainFirst)
    {
        var (source, peer) = CreateInboundRepairHandoffSource();
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        if (!drainFirst)
        {
            AssertRejectedRepairHandoff(captured.State,
                FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(captured.State, capture.Command));
        }
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State,
            CreateInboundRepairHandoffDrain(capture));
        Assert.True(drained.Accepted, drained.Reason);
        var current = drained.State;
        var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(current, capture.Command);
        Assert.True(restored.Accepted, restored.Reason);
        Assert.Equal(capture.Checkpoint, restored.RestoreCommand!.Checkpoint);
        Assert.Equal(capture.MissingRanges, restored.RestoreCommand.MissingRanges);
        Assert.Equal(source.CurrentRuntimeOwner, restored.State.CurrentRuntimeOwner);
        Assert.Equal(source.CurrentRoutePublicationToken, restored.State.CurrentRoutePublicationToken);
        Assert.Equal(FileTransferFallbackRepairHandoffState.Restored,
            restored.State.PersistentState.FallbackTailYield.RepairHandoff!.State);
        Assert.Null(restored.State.PersistentState.FallbackTailYield.PendingPeerProofBeforeLocalYield);
        Assert.Null(restored.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.False(FileTransferCoordinator.IsInboundFallbackRepairHandoffCurrent(restored.State, capture.Command));
        AssertRejectedRepairHandoff(restored.State,
            FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(restored.State, capture.Command));
        AssertRejectedRepairHandoff(restored.State,
            FileTransferCoordinator.DrainInboundFallbackRepairHandoff(restored.State, CreateInboundRepairHandoffDrain(capture)));
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_RestoreWaitsForNegotiationClosure()
        => AssertRepairHandoffRestoreWaitsForNegotiation(CreateInboundRepairHandoffSource());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRepairHandoffRestoreWaitsForNegotiation(
        (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Peer) fixture)
    {
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(fixture.State, fixture.Peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, CreateInboundRepairHandoffDrain(capture));
        Assert.True(drained.Accepted, drained.Reason);
        var yielded = MarkFallbackTailQuiescent(drained.State, drained.State.RouteSelection,
            drained.State.CurrentLeg!.TransportEpochId, 12_106, 12_483);
        Assert.Null(yielded.MutationRejectedReason);
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State,
            yielded.FallbackTailYieldDecision!.Value, "repair-handoff-local", "repair-handoff-local-frame", 50_001);
        var paired = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, local);
        Assert.NotNull(paired.PairedRelease);
        var admitted = RequestRuntimeUnlockAdmission(paired.State, paired.State.CurrentRuntimeOwner);
        Assert.True(admitted.RuntimeUnlockAdmissionDecision!.Value.Granted);
        AssertRejectedRepairHandoff(admitted.State,
            FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(admitted.State, capture.Command));
        Assert.Equal(FileTransferFallbackRepairHandoffState.Drained,
            admitted.State.PersistentState.FallbackTailYield.RepairHandoff!.State);
        Assert.Equal(capture.MissingRanges,
            admitted.State.PersistentState.FallbackTailYield.RepairHandoff.DrainReceipt!.MissingRanges);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairHandoff_RequiresAcceptedPeerEvidenceAndIdempotentCapture()
        => AssertRepairHandoffPeerEvidenceAndIdempotence(CreateInboundRepairHandoffSource());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRepairHandoffPeerEvidenceAndIdempotence(
        (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Peer) fixture)
    {
        var (source, peer) = fixture;
        var unobserved = source with { PersistentState = source.PersistentState with
        { FallbackTailYield = source.PersistentState.FallbackTailYield with { PendingPeerProofBeforeLocalYield = null } } };
        AssertRejectedRepairHandoff(unobserved, FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(unobserved, peer));
        AssertRejectedRepairHandoff(source, FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source,
            peer with { ObservationIdentity = "different-observation" }));
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, peer);
        var duplicate = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(prepared.State, peer);
        Assert.True(duplicate.Accepted, duplicate.Reason);
        Assert.Null(duplicate.HoldCommand);
        Assert.Equal(prepared.State.PersistentState, duplicate.State.PersistentState);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        var clonedCapture = capture with { InboundWrites = capture.InboundWrites.ToArray().ToImmutableArray(),
            MissingRanges = capture.MissingRanges.ToArray().ToImmutableArray() };
        var duplicateCapture = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(captured.State, clonedCapture);
        Assert.True(duplicateCapture.Accepted, duplicateCapture.Reason);
        Assert.Equal(captured.State.PersistentState, duplicateCapture.State.PersistentState);
        var receipt = CreateInboundRepairHandoffDrain(capture);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, receipt);
        var duplicateDrain = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(drained.State, receipt);
        Assert.True(duplicateDrain.Accepted, duplicateDrain.Reason);
        Assert.Equal(drained.State.PersistentState, duplicateDrain.State.PersistentState);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Peer)
        CreateInboundRepairHandoffSource(Action<FileTransferCoordinatorTransitionEnvelope>? captureSnapshot = null)
    {
        var source = ActiveFallbackStateWithoutCheckpoint(FileTransferDirection.Inbound) with
        {
            CurrentDataSessionIdentity = "repair-handoff-data-session", CurrentDataSessionGeneration = 1,
            CurrentFallbackCheckpointLease = null,
        };
        source = source with { PersistentState = source.PersistentState with { CoordinatorInstanceId = "repair-handoff-coordinator" } };
        source = EstablishInboundRepairHandoffCheckpoint(source, captureSnapshot);
        return ObserveInboundRepairHandoffPeer(source, captureSnapshot);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState EstablishInboundRepairHandoffCheckpoint(FileTransferCoordinatorState source,
        Action<FileTransferCoordinatorTransitionEnvelope>? captureSnapshot = null)
    {
        var epoch = source.CurrentLeg!.TransportEpochId;
        var requested = RequestFallbackCheckpoint(source, source.RouteSelection, "repair-handoff-checkpoint", epoch);
        Assert.Null(requested.MutationRejectedReason);
        captureSnapshot?.Invoke(requested.SnapshotEnvelope!);
        var accepted = AcceptFallbackCheckpoint(requested.State, source.RouteSelection, "repair-handoff-checkpoint", epoch, 12_106, 12_483);
        Assert.Null(accepted.MutationRejectedReason);
        captureSnapshot?.Invoke(accepted.SnapshotEnvelope!);
        return accepted.State;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Peer)
        ObserveInboundRepairHandoffPeer(FileTransferCoordinatorState source, Action<FileTransferCoordinatorTransitionEnvelope>? captureSnapshot = null)
    {
        var preview = MarkFallbackTailQuiescent(source, source.RouteSelection, source.CurrentLeg!.TransportEpochId, 12_106, 12_483);
        var proof = CreateCoordinatorFallbackTailPeerProof(preview.State, preview.FallbackTailYieldDecision!.Value) with
        { CommittedChunkIndex = 12_106, HighestObservedChunkIndex = 12_483 };
        var peer = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(source, proof, "repair-handoff-peer", 50_000);
        var accepted = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, peer);
        Assert.True(accepted.Accepted, accepted.Reason);
        captureSnapshot?.Invoke(accepted.SnapshotEnvelope!);
        return (accepted.State, peer);
    }

    private static FileTransferFallbackRepairHandoffCapture CreateInboundRepairHandoffCapture(
        FileTransferFallbackRepairHandoffCommand command)
        => new(command, 1, [11, 12], [21], new(command.Source.RuntimeOwner, command.Source.RoutePublication!.Value,
            command.Source.TerminalGeneration, command.Source.LegGeneration, command.Source.TransportEpoch,
            command.Source.BridgeRecoveryGeneration, command.Source.DataSessionGeneration, 5, 12_106, 12_483,
            12_484, command.Source.CheckpointGeneration), [new(12_106, 2), new(12_290, 1)]);

    private static FileTransferFallbackRepairHandoffDrainReceipt CreateInboundRepairHandoffDrain(
        FileTransferFallbackRepairHandoffCapture capture)
        => new(capture, capture.InboundWrites, capture.InboundFeedback, capture.Checkpoint, capture.MissingRanges);

    private static FileTransferLeg MutateRepairHandoffLeg(FileTransferLeg leg, string mutation, int? liveRouteEpoch = null)
        => new()
        {
            LegId = leg.LegId, Generation = leg.Generation + (mutation == "leg-generation" ? 1 : 0),
            RouteSelection = mutation == "route" ? FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4) : leg.RouteSelection,
            ProtocolVersion = mutation == "protocol" ? 4 : leg.ProtocolVersion, LiveRouteEpochId = liveRouteEpoch ?? leg.LiveRouteEpochId,
            TransportEpochId = leg.TransportEpochId + (mutation == "transport-epoch" ? 1 : 0), StartedUtc = leg.StartedUtc,
            State = leg.State, StartCommittedChunkIndex = leg.StartCommittedChunkIndex,
            ProvenCommittedChunkIndex = leg.ProvenCommittedChunkIndex, ProvenHighestObservedChunkIndex = leg.ProvenHighestObservedChunkIndex,
            CheckpointGeneration = leg.CheckpointGeneration + (mutation == "checkpoint-generation" ? 1 : 0),
            CheckpointRequestId = mutation == "checkpoint-pending" ? "new-checkpoint" : leg.CheckpointRequestId,
            BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration + (mutation == "bridge-generation" ? 1 : 0),
            CanSendData = mutation != "send-disabled" && leg.CanSendData,
        };

    private static void AssertRejectedRepairHandoff(FileTransferCoordinatorState before,
        FileTransferFallbackRepairHandoffDecision decision)
    {
        Assert.False(decision.Accepted, decision.Reason);
        Assert.Equal(before.PersistentState, decision.State.PersistentState);
        Assert.Equal(before.CurrentRuntimeOwner, decision.State.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, decision.State.CurrentRoutePublicationToken);
        Assert.Null(decision.HoldCommand);
        Assert.Null(decision.RestoreCommand);
        Assert.DoesNotContain(decision.SnapshotEnvelope!.Aggregate.Operations, operation => operation.StateApplied);
    }
}
