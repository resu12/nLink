using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_ExactInitialActivation_AdmitsOnceInEitherArrivalOrder(
        bool evaluationFirst,
        bool pendingWrite)
    {
        const string transferId = "initial_peer_first_bootstrap_arrival_order";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(
            service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var pendingWrites = GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        if (pendingWrite)
        {
            pendingWrites.Add(120);
        }

        if (!evaluationFirst)
        {
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
        }

        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        if (pendingWrite)
        {
            AssertInitialPeerFirstBootstrapHeldForPendingWrite(context, dataSession);
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        }

        if (evaluationFirst)
        {
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        }

        if (pendingWrite)
        {
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
            pendingWrites.Clear();
            object?[] takeArguments = [context, null, null, null];
            var wake = typeof(SessionFileTransferService).GetMethod(
                    "TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, takeArguments);
            Assert.NotNull(wake);
            Assert.True(GetPrivateProperty<bool>(wake, "RecheckClaimed"));
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                    "ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [wake]));
        }

        await WaitUntilAsync(
            () => GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState")
                .FileTransferRouteTransition.LastTerminalOperation is
                {
                    Authority.Kind: FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
                    DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    RetainedFrameApplyReceipt.Status: FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                } && GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null,
            timeoutMs: 5000);
        var receipt = AssertInitialPeerFirstBootstrapCompleted(context, dataSession, frame);

        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation with { ObservationGeneration = 12 });
        AssertInitialPeerFirstBootstrapDuplicate(context, dataSession, receipt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialPeerFirstBootstrapHeldForPendingWrite(
        object context,
        InitialBootstrapRecordingDataSession dataSession)
    {
        Assert.NotNull(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        var held = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        Assert.Null(held.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
        Assert.Equal(0, held.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferFallbackV6ProofBootstrapReceipt AssertInitialPeerFirstBootstrapCompleted(
        object context,
        InitialBootstrapRecordingDataSession dataSession,
        FileTransferFrontierRequestFrameV6 frame)
    {
        var completed = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        var receipt = Assert.IsType<FileTransferFallbackV6ProofBootstrapReceipt>(
            completed.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered, receipt.Status);
        Assert.Equal(1, receipt.Command.BootstrapGeneration);
        Assert.Equal(1, receipt.Command.Observation.FallbackEpoch);
        Assert.Equal(1, receipt.Command.TargetTransportEpoch);
        Assert.Equal(1, receipt.Command.SourceLiveRouteEpoch);
        Assert.Equal(
            PostTunaFallbackPeerProofFrameIdentity.Create(frame),
            completed.FileTransferRouteTransition.LastTerminalOperation!.Value
                .RetainedFrameApplyReceipt!.Value.Command.FrameIdentity);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        var fallbackLeg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        Assert.True(fallbackLeg.CanSendData);
        Assert.Equal(FileTransferLegState.RecoveryActive, fallbackLeg.State);
        Assert.True(fallbackLeg.CheckpointGeneration > 0);
        Assert.Null(fallbackLeg.CheckpointRequestId);
        var checkpointState = Assert.Single(dataSession.SentFrames
            .OfType<FileTransferReceiverStateFrameV6>()
            .Where(state => state.RecoveryMode == "regular_nkn_state_refresh"));
        Assert.Equal(
            $"v6-regular-nkn-state-refresh:route-transition:{completed.FileTransferRouteTransition.LastTerminalOperation!.Value.Authority.TransitionGeneration}:1",
            checkpointState.RepairRequestId);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        return receipt;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialPeerFirstBootstrapDuplicate(
        object context,
        InitialBootstrapRecordingDataSession dataSession,
        FileTransferFallbackV6ProofBootstrapReceipt receipt)
    {
        var duplicate = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        Assert.Equal(receipt, duplicate.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(1, duplicate.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Single(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
        Assert.Single(dataSession.SentFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Where(FileTransferProtocol.IsFallbackV6ProofBootstrapReceiverState));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InboundBootstrapCheckpoint_DeliveryMustCompleteWithPeerVisibleProof(bool peerVisible)
    {
        const string transferId = "inbound_bootstrap_checkpoint_delivery";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession)
        {
            ReleaseCheckpoint = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PeerVisible = peerVisible,
        };
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        var processing = ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        try
        {
            await bridge.CheckpointCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = CreateCoordinatorStateForTest(context);
            var lease = Assert.IsType<FallbackCheckpointLease>(pending.CurrentFallbackCheckpointLease);
            Assert.Equal(FallbackCheckpointLeaseState.Pending, lease.State);
            Assert.Equal(FileTransferLegState.CheckpointPending, pending.CurrentLeg!.State);
            Assert.False(pending.CurrentLeg.CanSendData);
            Assert.Null(pending.PersistentState.FallbackTailYield.CurrentOperation);
            var request = Assert.Single(bridge.Requests);
            Assert.Equal(FileTransferControlPlaneKind.FallbackCheckpointProof, request.Kind);
            Assert.Equal(lease.CheckpointRequestId, request.CheckpointRequestId);
            Assert.Equal(lease.CheckpointGeneration, pending.CurrentLeg.CheckpointGeneration);
            Assert.Equal(lease.LegGeneration, request.TransferLegGeneration);
            Assert.Equal(lease.TransportEpoch, request.TransportEpoch);
            Assert.Equal(lease.BridgeRecoveryGeneration, request.BridgeRecoveryGeneration);
            Assert.Equal(lease.RuntimeOwner, pending.CurrentRuntimeOwner);
            Assert.True(request.PreTransportDeliveryValidator!());

            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
            {
                var execution = GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution");
                return peerVisible ? execution is null : execution is not null &&
                    GetPrivatePropertyValue(execution, "LastFrameReceipt") is not null;
            }, 5000);
            var completed = CreateCoordinatorStateForTest(context);
            Assert.Equal(peerVisible, completed.CurrentLeg!.CanSendData);
            Assert.Equal(peerVisible ? FileTransferLegState.RecoveryActive : FileTransferLegState.CheckpointPending,
                completed.CurrentLeg.State);
            Assert.Equal(lease.LeaseGeneration, completed.LastFallbackCheckpointLeaseGeneration);
            Assert.Null(completed.PersistentState.FallbackTailYield.CurrentOperation);
            if (!peerVisible)
            {
                Assert.Equal(lease.CheckpointRequestId, completed.CurrentLeg.CheckpointRequestId);
            }
            Assert.False(pending.CurrentLeg.CanSendData);
            Assert.Equal(FallbackCheckpointLeaseState.Pending, lease.State);
        }
        finally
        {
            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("data_session")]
    [InlineData("data_session_generation")]
    [InlineData("data_session_aba")]
    [InlineData("replacement")]
    [InlineData("terminal")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InboundBootstrapCheckpoint_StaleDeliveryCannotAcceptOrYield(string mutation)
    {
        var transferId = $"inbound_bootstrap_checkpoint_{mutation}";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var replacementSession = new InitialBootstrapRecordingDataSession(dataSession.SessionId, transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession)
        {
            ReleaseCheckpoint = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        var processing = ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        try
        {
            await bridge.CheckpointCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = CreateCoordinatorStateForTest(context);
            var request = Assert.Single(bridge.Requests);
            switch (mutation)
            {
                case "data_session":
                case "data_session_aba": SetPrivateProperty(context, "DataSession", replacementSession); break;
                case "data_session_generation": SetPrivateProperty(context, "DataSessionGeneration", 2L); break;
                case "replacement":
                    typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(service, null);
                    break;
                case "terminal": SetPrivateProperty(context, "State", FileTransferTransferState.Canceled); break;
            }

            var validatorTarget = request.PreTransportDeliveryValidator!.Target!;
            var contract = validatorTarget.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(field => field.FieldType.Name == "FallbackControlPlaneDeliveryContract")
                .GetValue(validatorTarget);
            var completion = new FileTransferControlPlaneDeliveryResult(
                request.Kind, transferId, dataSession.SessionId, "late-checkpoint-proof",
                true, false, true, false, true, true, "", "", "", "");
            Assert.False(Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod("TryCompleteInboundFallbackControlPlaneSendLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, request, contract, completion])));
            if (mutation == "data_session_aba")
            {
                SetPrivateProperty(context, "DataSession", dataSession);
            }
            Assert.False(request.PreTransportDeliveryValidator!());
            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            await bridge.CheckpointReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A consumed stale delivery validator must never become valid again.
            Assert.False(request.PreTransportDeliveryValidator!());
            var current = CreateCoordinatorStateForTest(context);
            Assert.False(current.CurrentLeg!.CanSendData);
            Assert.Equal(pending.CurrentFallbackCheckpointLease, current.CurrentFallbackCheckpointLease);
            Assert.Null(current.PersistentState.FallbackTailYield.CurrentOperation);
        }
        finally
        {
            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing_receipt")]
    [InlineData("held_receipt")]
    [InlineData("frame_identity")]
    [InlineData("proof_generation")]
    [InlineData("bootstrap_generation")]
    [InlineData("data_session_generation")]
    [InlineData("publication")]
    [InlineData("target_transport")]
    [InlineData("target_epoch")]
    [InlineData("terminal")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public async Task InboundBootstrapCheckpoint_CommitRequiresExactLocalBootstrap(string mutation)
    {
        var transferId = $"inbound_bootstrap_commit_{mutation}";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var source = CreateCoordinatorStateForTest(context);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null, 5000);
        var completed = CreateCoordinatorStateForTest(context);
        var (input, authority, terminal) = CreateInboundBootstrapCheckpointInput(source, completed, mutation);
        AssertInboundBootstrapCheckpointCommit(source, input, authority, terminal, mutation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState Input, FileTransferRouteTransitionAuthority Authority,
        FileTransferRouteTransitionOperation Terminal) CreateInboundBootstrapCheckpointInput(
            FileTransferCoordinatorState source, FileTransferCoordinatorState completed, string mutation)
    {
        var terminal = completed.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        var bootstrap = completed.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt!;
        var authority = terminal.Authority;
        var fallback = Assert.IsType<FileTransferFallbackTransitionProofBoundary>(authority.ProofBoundary);
        authority = mutation switch
        {
            "frame_identity" => authority with { ProofBoundary = fallback with { RetainedFrameIdentity = fallback.RetainedFrameIdentity with { Sha256 = new string('a', 64) } } },
            "proof_generation" => authority with { ProofBoundary = fallback with { PeerProofEvidence = fallback.PeerProofEvidence with { ProofGeneration = 99 } } },
            "target_epoch" => authority with { ProofBoundary = fallback with { TargetTransportEpoch = 99 } },
            "target_transport" => authority with { TargetTransport = FileTransferTransportKind.Tuna },
            "publication" => authority with { SourceRoutePublication = authority.SourceRoutePublication with { PublicationGeneration = 99 } },
            _ => authority,
        };
        var input = source with
        {
            CurrentDataSessionGeneration = mutation == "data_session_generation" ? 99 : source.CurrentDataSessionGeneration,
            IsTerminal = mutation == "terminal",
            PersistentState = source.PersistentState with
            {
                FileTransferRouteTransition = source.PersistentState.FileTransferRouteTransition with
                {
                    CurrentOperation = terminal with { State = FileTransferRouteTransitionState.Draining, Authority = authority },
                    LastFallbackV6ProofBootstrapGeneration = bootstrap.Command.BootstrapGeneration,
                    LastFallbackV6ProofBootstrapReceipt = mutation switch
                    {
                        "missing_receipt" => null,
                        "held_receipt" => bootstrap with { Status = FileTransferFallbackV6ProofBootstrapReceiptStatus.Held },
                        "bootstrap_generation" => bootstrap with { Command = bootstrap.Command with { BootstrapGeneration = 99 } },
                        _ => bootstrap,
                    },
                },
            },
        };
        return (input, authority, terminal);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInboundBootstrapCheckpointCommit(FileTransferCoordinatorState source,
        FileTransferCoordinatorState input, FileTransferRouteTransitionAuthority authority,
        FileTransferRouteTransitionOperation terminal, string mutation)
    {
        var valid = Assert.IsType<bool>(typeof(FileTransferCoordinator)
            .GetMethod("IsInboundFallbackBootstrapCheckpointBoundaryCurrent", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [input, authority]));
        Assert.Equal(mutation == "none", valid);
        if (mutation != "none")
        {
            var rejected = FileTransferCoordinator.CommitFileTransferRouteTransition(input, authority, terminal.DrainReceipt!.Value);
            Assert.Equal(FileTransferRouteTransitionDecisionStatus.Rejected, rejected.Status);
            Assert.Null(rejected.State.CurrentFallbackCheckpointLease);
            Assert.Equal(input.PersistentState.SnapshotStateGeneration, rejected.State.PersistentState.SnapshotStateGeneration);
            return;
        }

        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(input, authority, terminal.DrainReceipt!.Value);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Committed, committed.Status);
        var lease = Assert.IsType<FallbackCheckpointLease>(committed.RouteDecision!.FallbackCheckpointLease);
        Assert.Equal(lease, committed.State.CurrentFallbackCheckpointLease);
        Assert.False(committed.State.CurrentLeg!.CanSendData);
        Assert.Equal(FileTransferLegState.CheckpointPending, committed.State.CurrentLeg.State);
        Assert.Equal(input.PersistentState.SnapshotStateGeneration + 1, committed.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(FileTransferRoute.FileTunaV4, source.CurrentLeg!.RouteSelection.Route);
        Assert.Null(source.CurrentFallbackCheckpointLease);
        var duplicate = FileTransferCoordinator.CommitFileTransferRouteTransition(committed.State, authority, terminal.DrainReceipt.Value);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal(committed.State.CurrentFallbackCheckpointLease, duplicate.State.CurrentFallbackCheckpointLease);
        Assert.Equal(committed.State.PersistentState.SnapshotStateGeneration, duplicate.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(committed.State.PersistentState.SnapshotStateFingerprint, duplicate.State.PersistentState.SnapshotStateFingerprint);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InboundBootstrapCheckpoint_ExactPeerYieldPairsOnlyAfterAcceptance()
    {
        const string transferId = "inbound_bootstrap_checkpoint_pair";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession)
        {
            ReleaseCheckpoint = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        var processing = ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        try
        {
            await bridge.CheckpointCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = CreateCoordinatorStateForTest(context);
            var leg = pending.CurrentLeg!;
            var proof = new FileTransferFallbackTailYieldFrameV6
            {
                AuthoritySchema = FileTransferAuthoritySchema.Current,
                SessionId = dataSession.SessionId,
                TransferId = transferId,
                TransportEpoch = leg.TransportEpochId,
                OwnerKey = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
                    dataSession.SessionId, transferId, leg.RouteSelection.TelemetryToken,
                    leg.ProtocolVersion, leg.LiveRouteEpochId, leg.Generation + 1,
                    leg.BridgeRecoveryGeneration, leg.TransportEpochId, "(none)"),
                PairKey = FileTransferCoordinator.BuildFallbackTailYieldPairKey(
                    dataSession.SessionId, transferId, leg.RouteSelection.TelemetryToken,
                    leg.ProtocolVersion, leg.LiveRouteEpochId, leg.TransportEpochId, "(none)"),
                Direction = "outbound",
                YieldGeneration = 1,
                AuthorityId = CreatePeerFallbackTailYieldAuthorityId(transferId),
                LiveRouteEpoch = leg.LiveRouteEpochId,
                FallbackLegGeneration = leg.Generation + 1,
                BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
                CommittedChunkIndex = 100,
                HighestObservedChunkIndex = 99,
                RecoveryMode = "fallback_tail_yield",
            };
            var applyPeerProof = typeof(SessionFileTransferService).GetMethod(
                "ApplyInboundFallbackTailYieldProof", BindingFlags.Instance | BindingFlags.NonPublic)!;
            applyPeerProof.Invoke(service, [context, proof]);
            Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation);
            Assert.False(CreateCoordinatorStateForTest(context).CurrentLeg!.CanSendData);
            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null, 5000);
            Assert.True(CreateCoordinatorStateForTest(context).CurrentLeg!.CanSendData);

            applyPeerProof.Invoke(service, [context, proof]);
            await WaitUntilAsync(() => GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation?.PairedRelease is not null, 5000);
            var paired = GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation!.Value;
            Assert.NotNull(paired.LocalProof);
            Assert.NotNull(paired.PeerProof);
            Assert.Equal(proof.AuthorityId, paired.PeerProof.Value.Proof.AuthorityId.ToString());
            applyPeerProof.Invoke(service, [context, proof]);
            Assert.Equal(paired.Decision.AuthorityId,
                GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation!.Value.Decision.AuthorityId);
            Assert.Single(dataSession.SentFrames.OfType<FileTransferFallbackTailYieldFrameV6>());
        }
        finally
        {
            bridge.ReleaseCheckpoint.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("missing_disposition")]
    [InlineData("wrong_fallback_epoch")]
    [InlineData("wrong_bootstrap_generation")]
    [InlineData("wrong_transport_epoch")]
    [InlineData("wrong_source_scope")]
    [InlineData("wrong_data_session")]
    [InlineData("wrong_data_session_generation")]
    [InlineData("bootstrap_history")]
    [InlineData("recovered_history")]
    [InlineData("terminal")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_NoncurrentInitialTrigger_HasNoAuthorityEffects(string mismatch)
    {
        var transferId = $"initial_peer_first_bootstrap_{mismatch}";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (_, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var persistent = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        switch (mismatch)
        {
            case "missing_disposition":
                SetPrivateProperty(context, "CoordinatorPersistentState", persistent with
                {
                    FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                    {
                        LastTerminalOperation = persistent.FileTransferRouteTransition.LastTerminalOperation!.Value with
                        {
                            DispositionReceipt = null,
                        },
                    },
                });
                break;
            case "wrong_fallback_epoch":
                evaluation = evaluation with { FallbackEpoch = 2 };
                break;
            case "wrong_bootstrap_generation":
                evaluation = evaluation with { TriggerBootstrapGeneration = 2 };
                break;
            case "wrong_transport_epoch":
                evaluation = evaluation with { TriggerTransportEpoch = 2 };
                break;
            case "wrong_source_scope":
                evaluation = evaluation with
                {
                    TriggerSourceScope = evaluation.TriggerSourceScope! with { RoutePublicationGeneration = 99 },
                };
                break;
            case "wrong_data_session":
                evaluation = evaluation with
                {
                    TriggerDataSession = new InitialBootstrapRecordingDataSession(dataSession.SessionId, transferId),
                };
                break;
            case "wrong_data_session_generation":
                evaluation = evaluation with { TriggerDataSessionGeneration = 2 };
                break;
            case "bootstrap_history":
                SetPrivateProperty(context, "CoordinatorPersistentState", persistent with
                {
                    FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                    {
                        LastFallbackV6ProofBootstrapGeneration = 1,
                    },
                });
                break;
            case "recovered_history":
                SetPrivateProperty(context, "LastRecoveredV6TransportLiveRouteEpochId", 1);
                break;
            case "terminal":
                SetPrivateProperty(context, "State", FileTransferTransferState.Canceled);
                SetPrivateProperty(context, "TerminalGeneration", 1);
                break;
        }

        GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite").Add(120);
        var before = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        var ownerBefore = GetPrivatePropertyValue(context, "CurrentRuntimeOwner");
        var publicationBefore = GetPrivatePropertyValue(context, "CurrentRoutePublicationToken");
        var progressBefore = GetPrivateProperty<int>(context, "NextChunkIndex");
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        Assert.Equal(before, GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState"));
        Assert.Equal(ownerBefore, GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        Assert.Equal(publicationBefore, GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
        Assert.Equal(progressBefore, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_DeferredWakeReplacementOrTerminalization_CannotIssue(bool terminalize)
    {
        const string transferId = "initial_peer_first_bootstrap_stale_wake";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var pendingWrites = GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        pendingWrites.Add(120);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        Assert.NotNull(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));

        if (terminalize)
        {
            SetPrivateProperty(context, "State", FileTransferTransferState.Canceled);
            SetPrivateProperty(context, "TerminalGeneration", 1);
        }
        else
        {
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                    "ReplaceInboundDataSessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, new InitialBootstrapRecordingDataSession(dataSession.SessionId, transferId)])));
        }

        pendingWrites.Clear();
        object?[] takeArguments = [context, null, null, null];
        Assert.Null(typeof(SessionFileTransferService).GetMethod(
                "TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, takeArguments));
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        var final = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        Assert.Null(final.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
        Assert.Null(final.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(0, final.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
    }

    [Theory]
    [InlineData("missing_disposition")]
    [InlineData("authority_session")]
    [InlineData("authority_transfer")]
    [InlineData("authority_direction")]
    [InlineData("current_publication")]
    [InlineData("current_commit")]
    [InlineData("data_session_generation")]
    [InlineData("terminal_generation")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_InitialActivationPredicate_IsReadOnlyAndRejectsChangedAuthority(string mismatch)
    {
        var transferId = $"initial_bootstrap_predicate_{mismatch}";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var state = CreateCoordinatorStateForTest(context);
        var before = state.PersistentState;
        Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out _));
        Assert.Equal(before, state.PersistentState);
        var terminal = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        var changedTerminal = mismatch switch
        {
            "missing_disposition" => terminal with { DispositionReceipt = null },
            "authority_session" => terminal with { Authority = terminal.Authority with { SessionId = "stale_session" } },
            "authority_transfer" => terminal with { Authority = terminal.Authority with { TransferId = "stale_transfer" } },
            "authority_direction" => terminal with { Authority = terminal.Authority with { Direction = FileTransferDirection.Outbound } },
            _ => terminal,
        };
        var changed = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
                {
                    LastTerminalOperation = changedTerminal,
                },
            },
        };
        changed = mismatch switch
        {
            "current_publication" => changed with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { PublicationGeneration = 99 } },
            "current_commit" => changed with { CurrentRouteCommit = state.CurrentRouteCommit!.Value with { CommitGeneration = 99 } },
            "data_session_generation" => changed with { CurrentDataSessionGeneration = 2 },
            "terminal_generation" => changed with { TerminalGeneration = state.TerminalGeneration + 1 },
            _ => changed,
        };
        var changedBefore = changed.PersistentState;
        Assert.False(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(changed, out _));
        Assert.Equal(changedBefore, changed.PersistentState);
        Assert.Equal(before.SnapshotStateGeneration, state.PersistentState.SnapshotStateGeneration);
        Assert.Equal(before.SnapshotStateFingerprint, state.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(before, GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState"));
    }

    private static async Task<object> CreateCommittedInitialInboundTunaForBootstrapTestAsync(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        InitialBootstrapRecordingDataSession dataSession,
        bool receiverBarrier = false,
        Action<object>? beforeActivation = null,
        Action<object, object>? afterPreparation = null)
    {
        await Task.Yield();
        const string reason = "test_initial_peer_first_bootstrap";
        service.AttachTransport(bridge);
        var context = CreateRegularNknV4InboundContext(dataSession.TransferId,
            chunkCount: 256, nextChunkIndex: 100,
            durableHighestChunkIndex: 99, creditUntilChunkIndexExclusive: 128,
            sessionId: dataSession.SessionId);
        beforeActivation?.Invoke(context);
        if (!receiverBarrier)
        {
            SeedInitialRegularV4RuntimeScopeForTest(context, reason);
            SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, reason);
        }
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        SetPrivateProperty(context, "V4StateEpoch", 1);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99);
        var written = new BitArray(256);
        for (var index = 0; index < 100; index++)
        {
            written[index] = true;
        }
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
        StartCoordinatorSnapshotStreamForTest(context);
        typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        if (receiverBarrier)
        {
            PublishInitialReceiverBarrierRegularRouteForTest(service, bridge, context, reason);
        }
        bridge.SeedRuntimeUnlockRouteCommitProofForTests(dataSession.TransferId, reason,
            transactionGeneration: 3, offerGeneration: 5, leaseGeneration: 7, launchGeneration: 9);
        if (receiverBarrier)
        {
            BindReceiverBarrierBeforeInitialRoutePreparationForTest(service, bridge, context, dataSession);
        }
        var staged = StageInboundFileTransferRouteTransitionForGapTest(service, context,
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(bridge.RuntimeUnlockRouteCommitSnapshot), reason);
        Assert.True(staged.Accepted, staged.Reason);
        var execution = GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution");
        Assert.NotNull(execution);
        afterPreparation?.Invoke(context, execution!);
        await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "RecheckInboundFileTransferRouteTransitionDrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, execution]));
        AssertCommittedInitialInboundTunaForBootstrap(context);
        return context;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCommittedInitialInboundTunaForBootstrap(object context)
    {
        var state = CreateCoordinatorStateForTest(context);
        var terminal = Assert.IsType<FileTransferRouteTransitionOperation>(
            state.PersistentState.FileTransferRouteTransition.LastTerminalOperation);
        Assert.Equal(FileTransferRouteTransitionKind.RegularV4ToTunaV4, terminal.Authority.Kind);
        Assert.Equal(FileTransferRouteTransitionState.Committed, terminal.State);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, terminal.DispositionReceipt!.Value.Status);
        Assert.NotNull(terminal.DrainReceipt);
        Assert.NotNull(terminal.RouteCommitToken);
        Assert.NotNull(terminal.RoutePublicationToken);
        Assert.Equal(1, state.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(0, state.CurrentLeg!.TransportEpochId);
        Assert.Null(state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(0, state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out var currentReason), currentReason);
    }

    internal static async Task<string> CreateInboundBootstrapCheckpointEvidenceFixtureAsync(bool receiverBarrier = false)
    {
        const string transferId = "inbound_bootstrap_checkpoint_evidence";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession, receiverBarrier);
        var identity = GetPrivateProperty<string>(context, "CoordinatorInstanceId");
        var evidenceIdentity = "fte1-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null, 5000);
        Assert.True(CreateCoordinatorStateForTest(context).CurrentLeg!.CanSendData);
        service.Dispose();
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal) &&
            line.Contains($"coordinator_instance_id={evidenceIdentity};", StringComparison.Ordinal)), 5000);
        return string.Join('\n', ReadOperationalLogTail(logStart).Split('\n')
            .Where(line => line.Contains($"coordinator_instance_id={evidenceIdentity};", StringComparison.Ordinal)));
    }

    private static (FileTransferFrontierRequestFrameV6 Frame, FileTransferFallbackV6ProofBootstrapEvaluation Evaluation)
        CreateInitialPeerFirstBootstrapTrigger(object context, InitialBootstrapRecordingDataSession dataSession, long fallbackEpoch = 1)
    {
        var state = CreateCoordinatorStateForTest(context);
        var transportEpoch = state.CurrentLeg!.TransportEpochId + 1;
        var frame = new FileTransferFrontierRequestFrameV6
        {
            SessionId = dataSession.SessionId,
            TransferId = dataSession.TransferId,
            TransportEpoch = transportEpoch,
            RequestCorrelationId = FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(fallbackEpoch),
            MissingRanges = [],
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        return (frame, new FileTransferFallbackV6ProofBootstrapEvaluation(
            dataSession.SessionId, FileTransferDirection.Inbound,
            ObservationGeneration: 10 + fallbackEpoch, FallbackEpoch: fallbackEpoch,
            Source: "unit_test_initial_peer_first_bootstrap",
            ProofKind: "file_transfer_v6_frontier_request_frame_received",
            ObservedUtcMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TriggerTransferId: dataSession.TransferId,
            TriggerTransportEpoch: transportEpoch,
            TriggerFrameFingerprint: PostTunaFallbackPeerProofFrameIdentity.Create(frame).Sha256,
            TriggerDataSession: dataSession, TriggerDataSessionGeneration: dataSession.Generation,
            TriggerSourceScope: CreateFallbackV6ProofBootstrapTriggerSourceScope(state),
            TriggerBootstrapGeneration: fallbackEpoch));
    }

    private static Task ProcessInitialPeerFirstBootstrapEvaluationAsync(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
        => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "ProcessFallbackV6ProofBootstrapEvaluationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [bridge, evaluation]));

    private static void AssertInitialPeerFirstBootstrapFrameReceived(
        SessionFileTransferService service, object context, FileTransferFrontierRequestFrameV6 frame)
    {
        object?[] arguments = [context, frame, null];
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                "TryPromoteInboundFileTunaV4FallbackFromPeerV6Proof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments)));
    }

    private class InitialBootstrapRecordingDataSession(string sessionId, string transferId) :
        IFileTransferDataSession, IFileTransferDataSessionGenerationProvider, IFileTransferPhysicalSendSubmissionSession
    {
        private readonly RecordingDataSession recording = new(sessionId, transferId);

        public string SessionId => recording.SessionId;
        public string TransferId => recording.TransferId;
        public long Generation { get; set; } = 7;
        public bool IsAvailable => true;
        public IReadOnlyList<FileTransferDataFrame> SentFrames => recording.SentFrames;
        public Func<FileTransferDataFrame, CancellationToken, Task>? BeforeSendAsyncForTest { get; set; }
        public Func<CancellationToken, ValueTask<FileTransferReceivedDataFrame>>? ReceiveWithMetadataForTest { get; init; }
        public Func<FileTransferDataFrame, CancellationToken, FileTransferPhysicalSendSubmission>? PhysicalSubmissionForTest { get; set; }

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => recording.ReceiveAsync(ct);

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => ReceiveWithMetadataForTest is { } receive ? receive(ct) : recording.ReceiveWithMetadataAsync(ct);

        public async Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            if (BeforeSendAsyncForTest is { } beforeSend) { await beforeSend(frame, ct); }
            await recording.SendAsync(frame, ct);
        }

        public FileTransferPhysicalSendSubmission SubmitPhysicalSend(FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority? authority, CancellationToken ct)
        {
            if (PhysicalSubmissionForTest is { } submit) { return submit(frame, ct); }
            // This recording fixture has exactly one physical task; it does not model NKN redundant copies.
            var task = SendAsync(frame, ct);
            return new(task, task);
        }

        public void Dispose() => recording.Dispose();
    }

    private class InitialBootstrapCheckpointTransport(InitialBootstrapRecordingDataSession dataSession) :
        LoopbackFileTransferTransport(dataSession.SessionId), IFileTransferControlPlaneDeliveryTransport
    {
        public List<FileTransferControlPlaneDeliveryRequest> Requests { get; } = [];
        public TaskCompletionSource CheckpointCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckpointReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ReleaseCheckpoint { get; set; }
        public bool PeerVisible { get; set; } = true;
        public IFileTransferControlPlaneDeliveryTransport? YieldTransportForTest { get; init; }

        public async Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
        {
            if (request.Kind == FileTransferControlPlaneKind.FallbackTailYield && YieldTransportForTest is { } transport)
            {
                Requests.Add(request);
                // Forward the original Service request and return the original
                // adapter result. Recording the frame is not another wire send.
                var result = await transport.SendFileTransferControlPlaneFrameAsync(request, ct);
                if (result.PeerVisibleAny) { await dataSession.SendAsync(request.Frame, ct); }
                return result;
            }
            var accepted = request.PreTransportDeliveryValidator?.Invoke() == true;
            Requests.Add(request);
            if (request.Kind == FileTransferControlPlaneKind.FallbackCheckpointProof)
            {
                CheckpointCaptured.TrySetResult();
                if (ReleaseCheckpoint is { } release)
                {
                    await release.Task.WaitAsync(ct);
                }
            }

            if (accepted)
            {
                await dataSession.SendAsync(request.Frame, ct);
            }

            if (request.Kind == FileTransferControlPlaneKind.FallbackCheckpointProof)
            {
                CheckpointReturned.TrySetResult();
            }
            return new FileTransferControlPlaneDeliveryResult(
                request.Kind, request.Frame.TransferId, request.Frame.SessionId,
                "bootstrap-checkpoint-test", ControlQueue: accepted,
                ControlAck: false, ControlCopy: accepted && PeerVisible,
                BulkCopy: false, PeerVisibleAny: accepted && PeerVisible,
                AcceptedAny: accepted, ControlQueueErrorName: string.Empty,
                ControlAckErrorName: string.Empty, ControlCopyErrorName: string.Empty,
                BulkCopyErrorName: string.Empty);
        }
    }
}
