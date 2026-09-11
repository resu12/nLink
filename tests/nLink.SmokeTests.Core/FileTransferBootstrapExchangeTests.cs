using System.Collections.Immutable;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CorrelatedBootstrap_QueueOnlyOrIncompleteReceiptCannotGrantDelivery(int variant)
    {
        var (sender, _) = CreateCorrelatedBootstrapPair();
        var issued = StartCorrelated(sender);
        var valid = CorrelatedReceipt(issued.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered);
        var invalid = variant switch
        {
            0 => valid with { Copies = [] },
            1 => valid with { SubmissionSealed = false },
            2 => valid with { Copies = valid.Copies.Where(copy => copy.FrameIdentity == issued.Command!.EpochFrameIdentity).ToImmutableArray() },
            3 => valid with { Copies = valid.Copies.Select(copy => copy with { Outcome = FileTransferBootstrapCopyOutcome.Pending }).ToImmutableArray() },
            4 => valid with { Copies = valid.Copies.Add(valid.Copies[0]) },
            _ => valid with { Copies = valid.Copies.Select(copy => copy with { Lane = "queue_acceptance" }).ToImmutableArray() },
        };
        var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(issued.State, invalid);
        Assert.False(rejected.Accepted);
        Assert.Same(issued.State, rejected.State);
        Assert.Null(rejected.Command);
        Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CorrelatedBootstrap_ReplacedScopeCannotAcceptLatePhysicalReceipt(int variant)
    {
        var (sender, _) = CreateCorrelatedBootstrapPair();
        var issued = StartCorrelated(sender);
        var state = issued.State;
        var replacedLeg = Assert.IsType<FileTransferLeg>(typeof(FileTransferCoordinator).GetMethod("CloneLeg",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [state.CurrentLeg!]));
        if (variant == 3) { replacedLeg.CheckpointGeneration++; }
        if (variant == 4) { replacedLeg.CheckpointRequestId = "replacement-checkpoint"; }
        var stale = variant switch
        {
            0 => state with { IsTerminal = true },
            1 => state with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 },
            2 => state with { CurrentDataSessionIdentity = "replacement-data-session" },
            3 or 4 => state with { CurrentLeg = replacedLeg },
            _ => state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "restarted-coordinator" } },
        };
        var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(stale,
            CorrelatedReceipt(issued.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered));
        Assert.False(rejected.Accepted);
        Assert.Same(stale, rejected.State);
        Assert.Null(rejected.Command);
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(stale, issued.Command!));
    }

    internal static FileTransferBootstrapAgreement CreateCorrelatedBootstrapAgreementFixture(string sessionId, string transferId, FileTransferDirection direction)
    {
        var pair = CreateCorrelatedBootstrapPair(sessionId, transferId);
        return (direction == FileTransferDirection.Outbound ? pair.Sender : pair.Receiver).PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelatedBootstrap_PrepareCannotBypassIncompleteExchangeWithFrameShapedEvidence(bool inbound)
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        var local = inbound ? response : solicit;
        var command = local.Command!.Bootstrap;
        var frame = inbound ? solicit.Command.Frame with { ReceiverChallengeId = response.Command!.Frame.ReceiverChallengeId }
            : response.Command!.Frame;
        var identity = PostTunaFallbackPeerProofFrameIdentity.Create(frame);
        var request = new FileTransferRouteTransitionPrepareRequest(FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            command.SourceRuntimeOwner, command.SourceRoutePublication, command.SourceRouteCommit,
            FileTransferRoute.FileTunaV4, 4, FileTransferTransportKind.Tuna,
            new FileTransferFallbackTransitionProofBoundary(command.SourceRouteCommit,
                new(local.State.SessionId, local.State.TransferId, local.State.Direction, command.BootstrapGeneration,
                    command.TargetTransportEpoch, "fallback_v6_proof_bootstrap_peer_frame", "coordinator_fallback_v6_proof_bootstrap_peer_frame", identity),
                identity, command.TargetTransportEpoch, command.SourceBridgeRecoveryGeneration + 1),
            command.Observation.DurableCheckpoint, FileTransferRoute.PostTunaFallbackV6, 6, FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback, null, "test_incomplete_correlation");
        var rejected = FileTransferCoordinator.PrepareFileTransferRouteTransition(local.State, request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Rejected, rejected.Status);
        Assert.Same(local.State, rejected.State);
        Assert.Null(rejected.PauseBindCommand);
        Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelatedBootstrap_PeerCountersAreIndependent_AndConflictsCannotRebind(bool inbound)
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        var state = inbound ? StartCorrelated(receiver).State : solicit.State;
        var frame = inbound ? solicit.Command.Frame : response.Command!.Frame;
        var id = inbound ? frame.SenderRequestId! : frame.ReceiverChallengeId!;
        var parts = id.Split(':');
        parts[2] = "371";
        var independent = string.Join(':', parts);
        frame = inbound ? frame with { SenderRequestId = independent } : frame with { ReceiverChallengeId = independent };
        var bound = ObserveCorrelated(state, frame);
        Assert.Equal(independent, bound.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.PeerCandidateId);
        Assert.Null(bound.State.PersistentState.FileTransferRouteTransition.BootstrapExchange.LastCompletedPeerId);
        Assert.Null(bound.Command);
        parts[3] = new string(parts[3][0] == 'a' ? 'b' : 'a', 64);
        var competing = inbound ? frame with { SenderRequestId = string.Join(':', parts) } : frame with { ReceiverChallengeId = string.Join(':', parts) };
        var rejected = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(bound.State, competing,
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
        Assert.False(rejected.Accepted);
        Assert.Same(bound.State, rejected.State);
        Assert.Null(rejected.Command);
        Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
    }

    [Fact]
    public void CorrelatedBootstrap_HeldAndCanceledPredecessorCannotIssueResponse_OrRestart()
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var announce = StartCorrelated(receiver);
        var solicit = StartCorrelated(sender);
        receiver = ObserveCorrelated(announce.State, solicit.Command!.Frame).State;
        var held = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(receiver,
            new(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Held, false, [], "held_before_submission"));
        Assert.True(held.Accepted);
        Assert.Null(held.Command);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(held.State, announce.Command!));
        var canceled = CorrelatedReceipt(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Failed) with
        {
            Copies = [new("control", announce.Command!.EpochFrameIdentity!, FileTransferBootstrapCopyOutcome.Canceled)],
        };
        var failed = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(held.State, canceled);
        Assert.True(failed.Accepted);
        Assert.True(failed.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
        Assert.Null(failed.Command);
        var late = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(failed.State,
            CorrelatedReceipt(announce.Command, FileTransferBootstrapPhaseReceiptStatus.Delivered));
        Assert.False(late.Accepted);
        Assert.Same(failed.State, late.State);
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(failed.State, announce.Command));
    }

    [Fact]
    public void CorrelatedBootstrap_RealHistoryCompletesSuccessor_AndRejectsPriorCycleFrames()
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var first = CompleteCorrelatedExchange(sender, receiver);
        sender = ReactivateCorrelatedFallback(CommitCorrelatedFallback(first.Sender)).State;
        receiver = ReactivateCorrelatedFallback(CommitCorrelatedFallback(first.Receiver)).State;
        Assert.Equal(3, sender.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(3, receiver.CurrentLiveRouteEpoch!.EpochId);
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        foreach (var (state, frame) in new[]
        {
            (solicit.State, first.Response),
            (response.State, first.Confirm),
            (response.State, first.Solicit),
        })
        {
            var rejected = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(state, frame,
                state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
            Assert.False(rejected.Accepted);
            Assert.Same(state, rejected.State);
            Assert.Null(rejected.Command);
        }
        Assert.Equal(first.Solicit.TransportEpoch, solicit.Command.Frame.TransportEpoch);
        Assert.NotEqual(first.Solicit.SenderRequestId, solicit.Command.Frame.SenderRequestId);
        sender = CompleteCorrelatedPhase(solicit.State, solicit.Command);
        receiver = CompleteCorrelatedPhase(response.State, response.Command!);
        var confirm = ObserveCorrelated(sender, response.Command!.Frame);
        sender = CompleteCorrelatedPhase(confirm.State, confirm.Command!);
        receiver = ObserveCorrelated(receiver, confirm.Command!.Frame).State;
        sender = CommitCorrelatedFallback(sender);
        receiver = CommitCorrelatedFallback(receiver);
        Assert.Equal(4, sender.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(4, receiver.CurrentLiveRouteEpoch!.EpochId);
    }

        private static (FileTransferCoordinatorState Sender, FileTransferCoordinatorState Receiver,
            FileTransferFallbackBootstrapFrameV6 Solicit, FileTransferFallbackBootstrapFrameV6 Response,
            FileTransferFallbackBootstrapFrameV6 Confirm) CompleteCorrelatedExchange(FileTransferCoordinatorState sender, FileTransferCoordinatorState receiver,
                Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
        {
            var solicit = StartCorrelated(sender);
            capture?.Invoke(solicit.SnapshotEnvelope!);
            var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
            capture?.Invoke(response.SnapshotEnvelope!);
            sender = CompleteCorrelatedPhase(solicit.State, solicit.Command, capture);
            receiver = CompleteCorrelatedPhase(response.State, response.Command!, capture);
            var confirm = ObserveCorrelated(sender, response.Command!.Frame);
            capture?.Invoke(confirm.SnapshotEnvelope!);
            sender = CompleteCorrelatedPhase(confirm.State, confirm.Command!, capture);
            var completed = ObserveCorrelated(receiver, confirm.Command!.Frame);
            capture?.Invoke(completed.SnapshotEnvelope!);
            receiver = completed.State;
            return (sender, receiver, solicit.Command.Frame, response.Command.Frame, confirm.Command.Frame);
        }

        private static FileTransferCoordinatorState CommitCorrelatedFallback(FileTransferCoordinatorState state,
            Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
        {
            var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange!;
            Assert.True(exchange.Completed);
            var command = exchange.Bootstrap;
            Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
                state, exchange.RetainedPeerFrame!, out var peer, out var reason), reason);
            var request = new FileTransferRouteTransitionPrepareRequest(
                FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
                command.SourceRuntimeOwner, command.SourceRoutePublication, command.SourceRouteCommit,
                FileTransferRoute.FileTunaV4, 4, FileTransferTransportKind.Tuna,
                new FileTransferFallbackTransitionProofBoundary(command.SourceRouteCommit,
                    peer.PeerProofEvidence, peer.PeerFrameIdentity, command.TargetTransportEpoch,
                    command.SourceBridgeRecoveryGeneration + 1), command.Observation.DurableCheckpoint,
                FileTransferRoute.PostTunaFallbackV6, 6, FileTransferTransportKind.RegularNkn,
                FileTransferTransportHandoffKind.TunaToNormalFallback, null, "test_correlated_fallback");
            var committed = CommitEmptyFileTransferRouteTransitionForTest(state, request, null, capture: capture);
            Assert.Equal(FileTransferRoute.PostTunaFallbackV6, committed.State.RouteSelection.Route);
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
                committed.State.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.DispositionReceipt!.Value.Status);
            Assert.Equal(exchange, committed.State.PersistentState.FileTransferRouteTransition.BootstrapExchange);
            Assert.NotNull(committed.State.CurrentFallbackCheckpointLease);
            return committed.State;
        }

        private static FileTransferCoordinatorDecision ReactivateCorrelatedFallback(FileTransferCoordinatorState state,
            Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
        {
            if (state.CurrentFallbackCheckpointLease is { } lease)
            {
                var accepted = AcceptFallbackCheckpoint(state,
                    FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
                    lease.CheckpointRequestId, state.CurrentLeg!.TransportEpochId, 4, 6);
                Assert.Null(accepted.MutationRejectedReason);
                capture?.Invoke(accepted.SnapshotEnvelope!);
                state = accepted.State;
            }
            var setup = state.Direction == FileTransferDirection.Inbound
                ? CreateCheckpointResponderCoexistencePairedHandoff(state, envelope => capture?.Invoke(envelope))
                : CreateFallbackV6ToTunaRouteTransitionSetup(state, envelope => capture?.Invoke(envelope));
            Assert.Equal(state.CurrentDataSessionIdentity, setup.State.CurrentDataSessionIdentity);
            Assert.Equal(state.CurrentDataSessionGeneration, setup.State.CurrentDataSessionGeneration);
            var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
            Assert.True(prepared.Status == FileTransferRouteTransitionDecisionStatus.Prepared,
                $"{state.Direction}: {prepared.Reason}");
            return CommitEmptyFileTransferRouteTransitionForTest(setup.State, setup.Request, setup.FinalProof, capture: capture);
        }

    internal static FileTransferCoordinatorDecision CreateCorrelatedOutboundServiceSuccessorHistory(FileTransferCoordinatorState source)
    {
        var pair = CreateCorrelatedBootstrapPair(source.SessionId, source.TransferId, source);
        var first = CompleteCorrelatedExchange(pair.Sender, pair.Receiver);
        return ReactivateCorrelatedFallback(CommitCorrelatedFallback(first.Sender));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelatedBootstrap_LegacyProofCannotBypassExchange(bool inbound)
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var issued = StartCorrelated(inbound ? receiver : sender);
        var state = CompleteCorrelatedPhase(issued.State, issued.Command!);
        FileTransferDataFrame legacy = inbound
            ? new FileTransferFrontierRequestFrameV6
            {
                SessionId = state.SessionId, TransferId = state.TransferId, TransportEpoch = 1,
                Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
                RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                RequestCorrelationId = FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(issued.Command!.Bootstrap.BootstrapGeneration),
            }
            : new FileTransferReceiverStateFrameV6
            {
                SessionId = state.SessionId, TransferId = state.TransferId, TransportEpoch = 1,
                Epoch = 3, ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 6,
                CreditUntilChunkIndexExclusive = 4, BytesCommitted = 4096, TransferPaused = true,
                TransferPauseReason = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
                Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
                RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            };
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            state, legacy, out _, out _));
        Assert.False(state.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelatedBootstrap_AnnounceWaitsForAllCopies_ThenCompletesBothSides(bool secondaryFails)
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var announce = StartCorrelated(receiver);
        receiver = announce.State;
        var solicit = StartCorrelated(sender);
        sender = CompleteCorrelatedPhase(solicit.State, solicit.Command!);
        var retained = ObserveCorrelated(receiver, solicit.Command!.Frame);
        Assert.Null(retained.Command);
        receiver = retained.State;
        var duplicate = ObserveCorrelated(receiver, solicit.Command.Frame);
        Assert.Same(receiver, duplicate.State);
        Assert.Null(duplicate.Command);

        var delivery = CorrelatedReceipt(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered, blockedBulk: true);
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(receiver, delivery);
        Assert.True(delivered.Accepted, delivered.Reason);
        Assert.Null(delivered.Command);
        Assert.False(delivered.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        var heldAfterSubmission = delivery with { Status = FileTransferBootstrapPhaseReceiptStatus.Held };
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(delivered.State, heldAfterSubmission).Accepted);

        var settled = CorrelatedReceipt(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Settled,
            failedBulk: secondaryFails);
        var response = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(delivered.State, settled);
        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(FileTransferBootstrapPhase.Response, response.Command!.Phase);
        Assert.Null(response.Command.EpochFrame);
        Assert.Equal(announce.Command!.Identity, response.Command.PredecessorIdentity);
        Assert.Same(response.State, FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(response.State, settled).State);
        var confirm = ObserveCorrelated(sender, response.Command.Frame);
        Assert.Equal(FileTransferBootstrapPhase.Confirm, confirm.Command!.Phase);
        Assert.Null(confirm.Command.EpochFrame);
        receiver = ObserveCorrelated(response.State, confirm.Command.Frame).State;
        Assert.False(receiver.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        receiver = CompleteCorrelatedPhase(receiver, response.Command);
        sender = CompleteCorrelatedPhase(confirm.State, confirm.Command);
        Assert.True(receiver.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        Assert.True(sender.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            sender, response.Command.Frame, out _, out var senderBoundaryReason), senderBoundaryReason);
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            receiver, confirm.Command.Frame, out _, out var receiverBoundaryReason), receiverBoundaryReason);
        Assert.Equal(FileTransferRoute.FileTunaV4, receiver.RouteSelection.Route);
        Assert.Equal(FileTransferRoute.FileTunaV4, sender.RouteSelection.Route);
    }

    [Fact]
    public void CorrelatedBootstrap_SolicitFirstElectsResponse_AndConfirmWaitsForSolicitSettlement()
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        Assert.Equal(FileTransferBootstrapPhase.Response, response.Command!.Phase);
        Assert.NotNull(response.Command.EpochFrame);
        var retained = ObserveCorrelated(solicit.State, response.Command.Frame);
        Assert.Null(retained.Command);
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(retained.State,
            CorrelatedReceipt(solicit.Command, FileTransferBootstrapPhaseReceiptStatus.Delivered, blockedBulk: true));
        Assert.Null(delivered.Command);
        var confirm = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(delivered.State,
            CorrelatedReceipt(solicit.Command, FileTransferBootstrapPhaseReceiptStatus.Settled));
        Assert.Equal(FileTransferBootstrapPhase.Confirm, confirm.Command!.Phase);
        Assert.Equal(FileTransferBootstrapPhase.Response,
            response.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Command.Phase);
        Assert.Null(StartCorrelated(response.State).Command);
    }

    [Fact]
    public void CorrelatedBootstrap_StaleScopeAndWrongEchoAreDiagnosticOnly()
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        sender = CompleteCorrelatedPhase(solicit.State, solicit.Command);
        foreach (var bad in new[]
        {
            response.Command!.Frame with { SenderRequestId = response.Command.Frame.ReceiverChallengeId },
            response.Command.Frame with { SenderRequestId = null },
            response.Command.Frame with { ReceiverChallengeId = solicit.Command.Frame.SenderRequestId },
            response.Command.Frame with { TransportEpoch = 2 },
        })
        {
            var rejected = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(sender, bad,
                sender.CurrentDataSessionIdentity!, sender.CurrentDataSessionGeneration);
            Assert.False(rejected.Accepted);
            Assert.Same(sender, rejected.State);
            Assert.Null(rejected.Command);
        }
        foreach (var stale in new[]
        {
            response.State with { IsTerminal = true },
            response.State with { CurrentDataSessionIdentity = "replaced" },
            response.State with { CurrentDataSessionGeneration = 18 },
            response.State with { CurrentRuntimeOwner = null },
            response.State with { PersistentState = response.State.PersistentState with { CoordinatorInstanceId = "restarted" } },
        })
        {
            var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(stale,
                CorrelatedReceipt(response.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered));
            Assert.False(rejected.Accepted);
            Assert.Same(stale, rejected.State);
            Assert.Null(rejected.Command);
        }
    }

    [Fact]
    public void CorrelatedBootstrap_SettlementBeforeDeliveryDoesNotUnlock_AndEpochOnlyIsNotDelivery()
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        sender = ObserveCorrelated(solicit.State, response.Command!.Frame).State;
        var receipt = CorrelatedReceipt(solicit.Command, FileTransferBootstrapPhaseReceiptStatus.Settled);
        var settled = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(sender, receipt);
        Assert.True(settled.Accepted);
        Assert.Null(settled.Command);
        var epochOnly = receipt with
        {
            Status = FileTransferBootstrapPhaseReceiptStatus.Delivered,
            Copies = receipt.Copies.Where(copy => copy.FrameIdentity == solicit.Command.EpochFrameIdentity).ToImmutableArray(),
        };
        var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(settled.State, epochOnly);
        Assert.False(rejected.Accepted);
        Assert.Same(settled.State, rejected.State);
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(settled.State,
            CorrelatedReceipt(solicit.Command, FileTransferBootstrapPhaseReceiptStatus.Delivered, blockedBulk: true));
        Assert.True(delivered.Accepted, delivered.Reason);
        Assert.Equal(FileTransferBootstrapPhase.Confirm, delivered.Command!.Phase);
    }

    internal static (FileTransferCoordinatorState State, FileTransferBootstrapPhaseCommand Command) CreateCorrelatedBootstrapTransportFixture(
        string sessionId, string transferId)
    {
        var (sender, _) = CreateCorrelatedBootstrapPair(sessionId, transferId);
        var issued = StartCorrelated(sender);
        return (issued.State, issued.Command!);
    }

    private static (FileTransferCoordinatorState Sender, FileTransferCoordinatorState Receiver) CreateCorrelatedBootstrapPair(
        string? sessionId = null, string? transferId = null, FileTransferCoordinatorState? senderSource = null,
        Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        FileTransferCoordinatorState Empty(FileTransferDirection direction) => EmptyState(regular, direction) with
        {
            SessionId = sessionId ?? "session_coordinator", TransferId = transferId ?? "transfer_coordinator",
            CurrentDataSessionIdentity = "bootstrap-data-session", CurrentDataSessionGeneration = 17,
            PersistentState = new() { CoordinatorInstanceId = "bootstrap-" + direction },
        };
        var sender = senderSource ?? Empty(FileTransferDirection.Outbound);
        var receiver = Empty(FileTransferDirection.Inbound);
        var offer = new FileTransferOfferV2
        {
            SessionId = sender.SessionId, TransferId = sender.TransferId, FileName = "file.bin", FileSizeBytes = 128 * 1024,
            PreferredDataProtocolVersion = 4, FileTransferRoute = regular.TelemetryToken,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(sender),
        };
        var accept = new FileTransferAcceptV1
        {
            SessionId = sender.SessionId, TransferId = sender.TransferId, AcceptedDataProtocolVersion = 4,
            FileTransferRoute = regular.TelemetryToken, FallbackBootstrapCorrelationVersion = 1,
            FallbackBootstrapIssuerId = FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(receiver),
        };
        sender = CaptureAgreement(FileTransferCoordinator.PinFallbackBootstrapOffer(sender, offer));
        sender = CaptureAgreement(FileTransferCoordinator.ApplyFallbackBootstrapAccept(sender, accept));
        receiver = CaptureAgreement(FileTransferCoordinator.PinFallbackBootstrapOffer(receiver, offer));
        receiver = CaptureAgreement(FileTransferCoordinator.ApplyFallbackBootstrapAccept(receiver, accept));
        receiver = CaptureAgreement(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(receiver, new()
        {
            SessionId = sender.SessionId, TransferId = sender.TransferId, ProtocolVersion = 4,
            FileTransferRoute = regular.TelemetryToken, SessionRole = FileTransferProtocol.SessionRoleSender,
            ChunkSizeBytes = 1024, InitialPipelineDepth = 1, FallbackBootstrapCorrelationVersion = 1,
            FallbackBootstrapIssuerId = offer.FallbackBootstrapIssuerId,
        }));
        return (Activate(sender), Activate(receiver));

        FileTransferCoordinatorState CaptureAgreement(FileTransferBootstrapAgreementDecision decision)
        {
            capture?.Invoke(decision.SnapshotEnvelope!);
            return decision.State;
        }

        FileTransferCoordinatorState Activate(FileTransferCoordinatorState state)
        {
            var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
                regular, state: FileTransferLegState.Active, canSendData: true), state);
            Assert.Null(started.MutationRejectedReason);
            capture?.Invoke(started.SnapshotEnvelope!);
            if (state.Direction == FileTransferDirection.Inbound)
            {
                var inbound = ActivateInitialResponderTunaForFallbackFixture(started.State, capture);
                Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(inbound.State, out var inboundReason), inboundReason);
                return inbound.State;
            }
            var setup = CreateRegularV4ToTunaRouteTransitionSetup(started.State, capture);
            var preparation = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
            Assert.True(preparation.Status == FileTransferRouteTransitionDecisionStatus.Prepared,
                $"{state.Direction}: {preparation.Reason}");
            var committed = CommitEmptyFileTransferRouteTransitionForTest(setup.State, setup.Request, setup.FinalProof, capture: capture);
            Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(committed.State, out var reason), reason);
            return committed.State;
        }
    }

    private static FileTransferBootstrapExchangeDecision StartCorrelated(FileTransferCoordinatorState state,
        FileTransferFallbackBootstrapRequestFrameV6? solicit = null)
    {
        var leg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(state.CurrentRuntimeOwner!.Value,
            state.CurrentRoutePublicationToken!.Value, state.TerminalGeneration, leg.Generation, leg.TransportEpochId,
            leg.BridgeRecoveryGeneration, state.CurrentDataSessionGeneration, 3, 4, 6, 128, 2);
        var decision = FileTransferCoordinator.StartFallbackBootstrapExchange(state,
            new(state.SessionId, state.TransferId, state.Direction, 1, 1, "off", "current_source", 10000, checkpoint),
            new()
            {
                SessionId = state.SessionId, TransferId = state.TransferId, Epoch = 3,
                ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 6,
                CreditUntilChunkIndexExclusive = 4, BytesCommitted = 4096, TransferPaused = true,
                TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode,
            }, solicit);
        Assert.True(decision.Accepted, decision.Reason);
        return decision;
    }

    private static FileTransferBootstrapExchangeDecision ObserveCorrelated(FileTransferCoordinatorState state,
        FileTransferFallbackBootstrapFrameV6 frame)
    {
        var decision = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(state, frame,
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
        Assert.True(decision.Accepted, decision.Reason);
        return decision;
    }

    private static FileTransferBootstrapPhaseReceipt CorrelatedReceipt(FileTransferBootstrapPhaseCommand command,
        FileTransferBootstrapPhaseReceiptStatus status, bool blockedBulk = false, bool failedBulk = false)
    {
        var copies = ImmutableArray.CreateBuilder<FileTransferBootstrapPhysicalCopy>();
        foreach (var frame in new[] { command.EpochFrameIdentity, command.FrameIdentity }.OfType<PostTunaFallbackPeerProofFrameIdentity>())
        {
            copies.Add(new("control", frame, FileTransferBootstrapCopyOutcome.Succeeded));
            copies.Add(new("bulk", frame, blockedBulk ? FileTransferBootstrapCopyOutcome.Pending : failedBulk
                ? FileTransferBootstrapCopyOutcome.Failed : FileTransferBootstrapCopyOutcome.Succeeded));
        }
        return new(command, status, true, copies.ToImmutable(), "test_exact_physical_receipt");
    }

    private static FileTransferCoordinatorState CompleteCorrelatedPhase(FileTransferCoordinatorState state,
        FileTransferBootstrapPhaseCommand command, Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
    {
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(state,
            CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Delivered));
        Assert.True(delivered.Accepted, delivered.Reason);
        capture?.Invoke(delivered.SnapshotEnvelope!);
        var settled = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(delivered.State,
            CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Settled));
        Assert.True(settled.Accepted, settled.Reason);
        capture?.Invoke(settled.SnapshotEnvelope!);
        return settled.State;
    }
}
