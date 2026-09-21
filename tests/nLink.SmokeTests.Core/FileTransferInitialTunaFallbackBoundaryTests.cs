using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackBoundary")]
    public void InitialTunaFallback_CurrentSourceRequiresDrainedHoldBeforeExchange(
        FileTransferDirection direction, bool independentGenerations)
        => CreateInitialFallbackExchangeFixture(direction, independentGenerations);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferBootstrapExchangeDecision CreateInitialFallbackExchangeFixture(
        FileTransferDirection direction, bool independentGenerations,
        List<FileTransferCoordinatorTransitionEnvelope>? envelopes = null, bool pairedIssuers = false)
    {
        var state = CreateInitialTunaBootstrapSourceFixture(direction, independentGenerations, envelopes, pairedIssuers);
        var source = Assert.IsType<FileTransferBootstrapLocalStopScope>(
            FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state));
        var leg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(state.CurrentRuntimeOwner!.Value,
            state.CurrentRoutePublicationToken!.Value, state.TerminalGeneration, leg.Generation, leg.TransportEpochId,
            leg.BridgeRecoveryGeneration, state.CurrentDataSessionGeneration, 1, 0, -1, 1, 1);
        var observation = new FileTransferFallbackV6ProofBootstrapObservation(state.SessionId, state.TransferId, direction,
            1, 1, "runtime_disabled", "local_runtime_disabled", 10000, checkpoint)
        {
            InitiationKind = FileTransferBootstrapInitiationKind.LocalRuntimeDisable,
            LocalStop = new(source, state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration),
        };
        Assert.False(observation.IsDefault);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, source));
        Assert.Null(state.CurrentRouteCommit);
        Assert.Null(state.CurrentLiveRouteEpoch);
        Assert.True(FileTransferCoordinator.CanStartFallbackBootstrapExchange(state));

        var hold = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, null);
        Assert.True(hold.Accepted, hold.Reason);
        Assert.NotNull(hold.Command);
        envelopes?.Add(hold.SnapshotEnvelope!);
        Assert.Equal(state.CurrentRoutePublicationToken, hold.State.CurrentRoutePublicationToken);

        // Going straight to exchange issuance cannot bypass the absent hold receipt.
        var exchange = FileTransferCoordinator.StartFallbackBootstrapExchange(state, observation, null);
        Assert.False(exchange.Accepted);
        Assert.Equal("bootstrap_source_hold_receipt_required", exchange.Reason);
        Assert.Null(exchange.Command);
        Assert.Equal(state.PersistentState.FileTransferRouteTransition, exchange.State.PersistentState.FileTransferRouteTransition);
        Assert.Equal(state.CurrentRoutePublicationToken, exchange.State.CurrentRoutePublicationToken);
        Assert.All(exchange.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));

        var capture = new FileTransferBootstrapSourceHoldCapture(hold.Command!, 1, [], [], []);
        var captured = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(hold.State, capture);
        Assert.True(captured.Accepted, captured.Reason);
        envelopes?.Add(captured.SnapshotEnvelope!);
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(captured.State, observation, null).Accepted);
        var drained = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(captured.State,
            new(capture, [], [], [], checkpoint));
        Assert.True(drained.Accepted, drained.Reason);
        envelopes?.Add(drained.SnapshotEnvelope!);
        var issued = FileTransferCoordinator.StartFallbackBootstrapExchange(drained.State, observation,
            new() { SessionId = state.SessionId, TransferId = state.TransferId, Epoch = 1,
                ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1, CreditUntilChunkIndexExclusive = 0,
                TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode });
        Assert.True(issued.Accepted, issued.Reason);
        Assert.NotNull(issued.Command);
        envelopes?.Add(issued.SnapshotEnvelope!);
        Assert.False(issued.Command.Bootstrap.IsDefault);
        Assert.Null(issued.Command.Bootstrap.SourceRouteCommit);
        Assert.Equal(FileTransferCoordinator.CaptureInitialTunaFallbackSource(state), issued.Command.Bootstrap.InitialSource);
        Assert.Null(issued.State.CurrentRouteCommit);
        Assert.Null(issued.State.CurrentLiveRouteEpoch);
        Assert.Equal(state.CurrentRoutePublicationToken, issued.State.CurrentRoutePublicationToken);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(issued.State, issued.Command));
        var duplicate = FileTransferCoordinator.StartFallbackBootstrapExchange(issued.State, observation, null);
        Assert.True(duplicate.Accepted, duplicate.Reason);
        Assert.Null(duplicate.Command);
        return issued;
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackBoundary")]
    public void InitialTunaFallback_RejectsSourceHistoryChangesAndLateReceipts(FileTransferDirection direction)
    {
        var issued = CreateInitialFallbackExchangeFixture(direction, true);
        AssertInitialFallbackSourceChangesRejected(issued);
        AssertInitialFallbackHistoryRejected(CreateInitialTunaBootstrapSourceFixture(direction, true), issued);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackSourceChangesRejected(FileTransferBootstrapExchangeDecision issued)
    {
        var state = issued.State;
        var command = issued.Command!;
        var source = command.Bootstrap.InitialSource!;
        var publication = state.CurrentRoutePublicationToken!.Value;
        foreach (var invalid in new[]
        {
            state with { IsTerminal = true }, state with { TerminalGeneration = 1 },
            state with { SessionId = "other" }, state with { TransferId = "other" },
            state with { CurrentDataSessionIdentity = "replacement" },
            state with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 },
            state with { CurrentRuntimeOwner = null }, state with { CurrentLeg = null },
            state with { CurrentRoutePublicationToken = null }, state with { CurrentRouteCommit = default(RouteCommitToken) },
            state with { LastRouteCommitGeneration = 1 }, state with { LastLiveRouteEpochId = 1 },
            state with { CurrentRoutePublicationToken = publication with { PublicationGeneration = 2 }, LastRoutePublicationGeneration = 2 },
            state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "replacement" } },
            state with { PersistentState = state.PersistentState with { FileTransferRouteTransition =
                state.PersistentState.FileTransferRouteTransition with { LastTransitionGeneration = 1 } } },
        })
        {
            Assert.NotEqual(source, FileTransferCoordinator.CaptureInitialTunaFallbackSource(invalid));
            Assert.False(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(invalid, command));
            var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(invalid,
                CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Delivered));
            Assert.False(rejected.Accepted);
            Assert.Null(rejected.Command);
            Assert.Equal(invalid.PersistentState.FileTransferRouteTransition, rejected.State.PersistentState.FileTransferRouteTransition);
            Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        }
        foreach (var altered in new[]
        {
            command.Bootstrap with { InitialSource = null },
            command.Bootstrap with { SourceRouteCommit = default(RouteCommitToken) },
            command.Bootstrap with { InitialSource = source with { Publication = publication with { PublicationGeneration = 2 } } },
            command.Bootstrap with { InitialSource = source with { DataSessionGeneration = source.DataSessionGeneration + 1 } },
            command.Bootstrap with { SourceLiveRouteEpoch = 1 },
        })
        {
            Assert.True(altered.IsDefault);
            Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(state,
                CorrelatedReceipt(command with { Bootstrap = altered }, FileTransferBootstrapPhaseReceiptStatus.Delivered)).Accepted);
        }

    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackHistoryRejected(FileTransferCoordinatorState fresh,
        FileTransferBootstrapExchangeDecision issued)
    {
        // No initial-source history may be erased to issue a new first exchange.
        var command = issued.Command!;
        var state = issued.State;
        var transition = fresh.PersistentState.FileTransferRouteTransition;
        foreach (var history in new[]
        {
            transition with { LastFallbackV6ProofBootstrapGeneration = 1 },
            transition with { PendingFallbackV6ProofBootstrap = command.Bootstrap },
            transition with { LastFallbackV6ProofBootstrapReceipt = new(command.Bootstrap,
                FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered, command.FrameIdentity, "prior") },
            transition with { BootstrapExchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange },
            transition with { LastTransitionGeneration = 1 },
        })
        {
            var invalid = fresh with { PersistentState = fresh.PersistentState with { FileTransferRouteTransition = history } };
            Assert.False(FileTransferCoordinator.CanStartFallbackBootstrapExchange(invalid));
            Assert.False(FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(invalid, command.Bootstrap.Observation, null).Accepted);
        }
    }

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialFallbackExchangeSnapshotFixture(FileTransferDirection direction)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var issued = CreateInitialFallbackExchangeFixture(direction, true, envelopes);
        var state = CompleteCorrelatedPhase(issued.State, issued.Command!, envelopes.Add);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(state,
            state.PersistentState.FileTransferRouteTransition.BootstrapExchange!));
        var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapExchange(state, "test_retirement");
        envelopes.Add(invalidated.SnapshotEnvelope!);
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(invalidated.State, issued.Command!));
        return envelopes.ToArray();
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackBoundary")]
    public void InitialTunaFallback_PeerExchangeCompletesWithoutPublishingFallback()
    {
        var sender = CreateInitialFallbackExchangeFixture(FileTransferDirection.Outbound, true, pairedIssuers: true);
        var receiver = CreateInitialFallbackExchangeFixture(FileTransferDirection.Inbound, false, pairedIssuers: true);
        CompleteInitialFallbackPeerExchange(sender, receiver);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState Sender, FileTransferCoordinatorState Receiver) CompleteInitialFallbackPeerExchange(FileTransferBootstrapExchangeDecision solicit,
        FileTransferBootstrapExchangeDecision announce, Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
    {
        var sender = CompleteCorrelatedPhase(solicit.State, solicit.Command!, capture);
        var retained = ObserveCorrelated(announce.State, solicit.Command!.Frame);
        capture?.Invoke(retained.SnapshotEnvelope!);
        Assert.Null(retained.Command);
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(retained.State,
            CorrelatedReceipt(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered, blockedBulk: true));
        Assert.True(delivered.Accepted, delivered.Reason);
        Assert.Null(delivered.Command);
        capture?.Invoke(delivered.SnapshotEnvelope!);
        var response = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(delivered.State,
            CorrelatedReceipt(announce.Command!, FileTransferBootstrapPhaseReceiptStatus.Settled));
        Assert.True(response.Accepted, response.Reason);
        Assert.Equal(FileTransferBootstrapPhase.Response, response.Command!.Phase);
        capture?.Invoke(response.SnapshotEnvelope!);
        var confirm = ObserveCorrelated(sender, response.Command.Frame);
        capture?.Invoke(confirm.SnapshotEnvelope!);
        Assert.Equal(FileTransferBootstrapPhase.Confirm, confirm.Command!.Phase);
        var observed = ObserveCorrelated(response.State, confirm.Command.Frame);
        capture?.Invoke(observed.SnapshotEnvelope!);
        var receiver = observed.State;
        Assert.False(receiver.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        receiver = CompleteCorrelatedPhase(receiver, response.Command, capture);
        sender = CompleteCorrelatedPhase(confirm.State, confirm.Command, capture);
        foreach (var state in new[] { sender, receiver })
        {
            var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange!;
            Assert.True(exchange.Completed);
            Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
                state, exchange.RetainedPeerFrame!, out var boundary, out var reason), reason);
            Assert.NotNull(boundary.Command.InitialSource);
            Assert.Null(boundary.Command.SourceRouteCommit);
            Assert.Equal(FileTransferRoute.FileTunaV4, state.RouteSelection.Route);
            Assert.Equal(0, state.CurrentRoutePublicationToken!.Value.LiveRouteEpoch);
            Assert.Null(state.CurrentRouteCommit);
            // Transition integration is a separate gate; a null commit alone remains invalid.
            Assert.True(new FileTransferFallbackTransitionProofBoundary(null, boundary.PeerProofEvidence,
                boundary.PeerFrameIdentity, exchange.Bootstrap.TargetTransportEpoch,
                exchange.Bootstrap.SourceBridgeRecoveryGeneration + 1).IsDefault);
        }
        return (sender, receiver);
    }
}
