using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    public void InitialFallbackDataSession_BindsOnceWithoutReplacingAuthority(FileTransferDirection direction, bool independentGenerations)
    {
        var state = CreateInitialFallbackDataSessionState(direction, independentGenerations);
        var request = InitialFallbackDataSessionRequestForTest(state);
        var result = FileTransferCoordinator.BindInitialFallbackDataSession(state, request);
        Assert.NotNull(result.Binding);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(result.SnapshotEnvelope);
        Assert.Equal("BindInitialFallbackDataSession", envelope.DecisionKind);
        Assert.Equal("Accepted", envelope.DecisionStatus);
        Assert.Equal(state.PersistentState.SnapshotStateGeneration + 1, result.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(state.PersistentState.SnapshotStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
        Assert.NotEqual(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
        Assert.Equal(envelope.Aggregate.BeforeBoundary with { DataSessionIdentity = request.DataSessionIdentity, DataSessionGeneration = 1 },
            envelope.Aggregate.AfterBoundary);
        Assert.Equal(state with { CurrentDataSessionIdentity = request.DataSessionIdentity, CurrentDataSessionGeneration = 1,
            PersistentState = result.State.PersistentState }, result.State);
        Assert.Equal(state.PersistentState with { SnapshotStateGeneration = result.State.PersistentState.SnapshotStateGeneration,
            SnapshotStateFingerprint = result.State.PersistentState.SnapshotStateFingerprint }, result.State.PersistentState);
        var operation = Assert.Single(envelope.Aggregate.Operations);
        Assert.True(operation.StateApplied);
        Assert.True(operation.ReceiptValidated);
        Assert.Equal("initial_data_session_binding", operation.OperationRole);
        Assert.Equal(envelope.Aggregate.BeforeBoundary, operation.InitialDataSessionBinding!.SourceBoundary);
        Assert.Equal(envelope.Aggregate.AfterBoundary, operation.InitialDataSessionBinding.EffectiveBoundary);
        Assert.Equal(state.CurrentRuntimeOwner, result.State.CurrentRuntimeOwner);
        Assert.Equal(state.CurrentRoutePublicationToken, result.State.CurrentRoutePublicationToken);
        Assert.Same(state.CurrentLeg, result.State.CurrentLeg);
        AssertRejected(result.State, request); // No duplicate or later replacement exemption.
        AssertRejected(result.State, request with { DataSessionGeneration = 2, DataSessionIdentity = state.TransferId + ":2" });
    }

    [Theory]
    [InlineData(FileTransferDirection.Inbound)]
    [InlineData(FileTransferDirection.Outbound)]
    public void InitialFallbackDataSession_RejectsForeignStaleAndNoninitialScopesWithoutMutation(FileTransferDirection direction)
    {
        for (var variant = 0; variant < 35; variant++)
        {
            var state = CreateInitialFallbackDataSessionState(direction);
            var request = InitialFallbackDataSessionRequestForTest(state);
            switch (variant)
            {
                case 0: request = request with { SessionId = "foreign" }; break;
                case 1: request = request with { TransferId = "foreign" }; break;
                case 2: request = request with { Direction = direction == FileTransferDirection.Inbound ? FileTransferDirection.Outbound : FileTransferDirection.Inbound }; break;
                case 3: request = request with { RuntimeOwner = default }; break;
                case 4: request = request with { Publication = default }; break;
                case 5: request = request with { RuntimeOwner = request.RuntimeOwner with { RuntimeGeneration = 99 } }; break;
                case 6: request = request with { Publication = request.Publication with { PublicationGeneration = 99 } }; break;
                case 7: request = request with { Publication = request.Publication with { Reason = "forged" } }; break;
                case 8: request = request with { DataSessionIdentity = "foreign:1" }; break;
                case 9: request = request with { DataSessionGeneration = 2 }; break;
                case 10: state = state with { IsTerminal = true }; break;
                case 11: state = state with { TerminalGeneration = 1 }; break;
                case 12: state = state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast) }; break;
                case 13: state = state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4) }; break;
                case 14: state = state with { LastLiveRouteEpochId = 1 }; break;
                case 15: state = state with { CurrentDataSessionIdentity = "existing", CurrentDataSessionGeneration = 1 }; break;
                case 16: state = state with { CurrentRoutePublicationToken = null }; break;
                case 17: state = state with { LastRoutePublicationGeneration = 2 }; break;
                case 18: state = state with { CurrentLeg = null }; break;
                case 19: state.CurrentLeg!.State = FileTransferLegState.CheckpointPending; break;
                case 20: state.CurrentLeg!.CanSendData = false; break;
                case 21: state.CurrentLeg!.CheckpointRequestId = "checkpoint"; break;
                case 22: state = state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = null } }; break;
                case 23: request = request with { DataSessionIdentity = "(none)" }; break;
                case 24: state = state with { CurrentRouteCommit = default(RouteCommitToken) }; break;
                case 25: state = state with { CurrentFallbackCheckpointLease = default(FallbackCheckpointLease) }; break;
                case 26: state = state with { CurrentFallbackControlPlaneToken = default(FallbackControlPlaneToken) }; break;
                case 27: state = state with { CurrentFallbackControlPlanePressureToken = default(FallbackControlPlanePressureToken) }; break;
                case 28: state = state with { CurrentRepairReplayDecision = default(RepairReplayDecision) }; break;
                case 29: state = state with { CurrentTransportEpochDecision = default(TransportEpochDecision) }; break;
                case 30: state = state with { CurrentRuntimeUnlockAdmission = default(RuntimeUnlockAdmissionToken) }; break;
                case 31: state = state with { CurrentRuntimeUnlockPendingPreCommitProbe = default(RuntimeUnlockPendingPreCommitProbeRequest) }; break;
                case 32: state = state with { CurrentRuntimeOwner = null }; break;
                case 33: state = state with { CurrentDataSessionGeneration = 1 }; break;
                case 34: request = request with { CoordinatorInstanceId = "foreign" }; break;
            }
            AssertRejected(state, request);
        }
    }

    private static void AssertRejected(FileTransferCoordinatorState state, InitialFallbackDataSessionRequest request)
    {
        var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(state);
        var result = FileTransferCoordinator.BindInitialFallbackDataSession(state, request);
        Assert.Null(result.Binding);
        Assert.Null(result.SnapshotEnvelope);
        Assert.Same(state, result.State);
        Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(state));
    }

    private static InitialFallbackDataSessionRequest InitialFallbackDataSessionRequestForTest(FileTransferCoordinatorState state)
        => new(state.PersistentState.CoordinatorInstanceId!, state.SessionId, state.TransferId, state.Direction, state.CurrentRuntimeOwner!.Value,
            state.CurrentRoutePublicationToken!.Value, state.TransferId + ":1", 1);

    private static FileTransferCoordinatorState CreateInitialFallbackDataSessionState(FileTransferDirection direction, bool independentGenerations = false)
    {
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var empty = EmptyState(route, direction) with
        {
            PersistentState = new() { CoordinatorInstanceId = "initial-binding-" + direction },
            LastRuntimeOwnerGeneration = independentGenerations ? 6 : 0,
            LastTransferLegGeneration = independentGenerations ? 12 : 0,
        };
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, route), empty).State;
        var publication = FileTransferCoordinator.AuthorizeAction(started, new(FileTransferCoordinatorActionKind.RoutePublication,
            Source: "initial_binding_test", Reason: "initial_publication", RuntimeOwner: started.CurrentRuntimeOwner,
            Route: route.Route, ProtocolVersion: 6, LiveRouteEpoch: 0, LegGeneration: started.CurrentLeg!.Generation,
            RuntimeGeneration: started.CurrentRuntimeOwner!.Value.RuntimeGeneration, TerminalGeneration: 0));
        Assert.True(publication.Accepted, publication.RejectionReason);
        return publication.State;
    }
}
