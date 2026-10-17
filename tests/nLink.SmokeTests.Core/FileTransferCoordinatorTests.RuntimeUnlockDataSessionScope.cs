using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
        CreateProviderLocalInitialActivationHoldForTests()
    {
        var fixture = CreateNonRegularV4ReceiveRecoveryOperationFixture();
        var state = fixture.Decision.State with { CurrentDataSessionProviderGeneration = 2 };
        var request = CreateInitialActivationReceiveRecoveryHoldRequest(state, 74);
        request = request with { ExpectedScope = request.ExpectedScope with
        {
            DataSessionIdentity = $"{state.TransferId}:2", DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        } };
        return FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(state, request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockDataSessionScope")]
    public void RuntimeUnlockDataSessionScope_ProviderObservationUsesExactLocalRecovery(bool recoveryActive)
    {
        var fixture = CreateNonRegularV4ReceiveRecoveryOperationFixture();
        var state = fixture.Decision.State with { CurrentDataSessionProviderGeneration = 2 };
        var request = CreateInitialActivationReceiveRecoveryHoldRequest(state, 71);
        if (!recoveryActive)
        {
            state = state with { PersistentState = state.PersistentState with
            { FileTransferReceiveRecovery = new FileTransferReceiveRecoverySubstate() } };
            request = request with { ExpectedScope = request.ExpectedScope with { ExpectedExecutionCommand = null } };
        }
        request = request with { ExpectedScope = request.ExpectedScope with
        {
            DataSessionIdentity = $"{state.TransferId}:2",
            DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        } };
        Assert.Equal(1, state.CurrentDataSessionGeneration);
        var result = FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(state, request);
        Assert.Equal(recoveryActive ? RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held :
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released, result.Decision.Status);
        Assert.Equal(request, result.Decision.Request);
        Assert.Equal(1, result.State.CurrentDataSessionGeneration);
        Assert.Equal(2, result.State.CurrentDataSessionProviderGeneration);
        Assert.Equal(state.CurrentRoutePublicationToken, result.State.CurrentRoutePublicationToken);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(result.State).InstalledAttempt);
        Assert.Null(result.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        if (recoveryActive)
        {
            Assert.Equal(1, result.Decision.ReceiveRecoveryOperation!.Value.Authority.Observation.DataSessionGeneration);
            Assert.Equal(request, result.Decision.DataSessionBinding!.Request);
            Assert.Equal(1, result.Decision.DataSessionBinding.TransferLocalGeneration);
            Assert.Equal(2, result.Decision.DataSessionBinding.Request.ExpectedScope.DataSessionGeneration);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationHoldBinding")]
    public void RuntimeUnlockDataSessionScope_ExplicitCancellationRetainsOnlyExactOldBinding(bool peerCancellation)
    {
        var held = CreateProviderLocalInitialActivationHoldForTests();
        var replaced = held.State with { CurrentDataSessionProviderGeneration = 3 };
        var request = new RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationRequest(
            held.Decision.Request,
            peerCancellation ? RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind.PeerCancellation :
                RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind.UserCancellation,
            "explicit_cancel");
        var cancelled = FileTransferCoordinator.InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold(replaced, request);
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Invalidated, cancelled.Decision.Status);
        Assert.Equal(held.Decision.DataSessionBinding, cancelled.Decision.DataSessionBinding);
        Assert.Equal(held.Decision.DataSessionBinding,
            cancelled.State.PersistentState.FileTransferReceiveRecovery.LastInitialActivationHold!.DataSessionBinding);
        Assert.Equal(3, cancelled.State.CurrentDataSessionProviderGeneration);
        Assert.Equal(replaced.CurrentRoutePublicationToken, cancelled.State.CurrentRoutePublicationToken);
        Assert.Equal(replaced.PersistentState.FileTransferReceiveRecovery.CurrentOperation,
            cancelled.State.PersistentState.FileTransferReceiveRecovery.CurrentOperation);
        var duplicate = FileTransferCoordinator.InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold(cancelled.State, request);
        Assert.Equal(cancelled.Decision, duplicate.Decision);
        Assert.Equal(cancelled.State, duplicate.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationHoldBinding")]
    public void RuntimeUnlockDataSessionScope_ExactSuccessorGetsNewLocalOperationBinding()
    {
        var completed = CreateCompletedUnprovenReceiveRecoveryOperation(receiptObservedUtcMs: 100);
        var state = completed.State with { CurrentDataSessionProviderGeneration = 2 };
        var request = CreateInitialActivationReceiveRecoveryHoldRequest(state, 75);
        request = request with { ExpectedScope = request.ExpectedScope with
        {
            DataSessionIdentity = $"{state.TransferId}:2", DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        } };
        var held = FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(state, request);
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held, held.Decision.Status);
        var observation = CreateReceiveRecoveryHealthEvaluationObservation(
            held.State, observationGeneration: 2, evidenceGeneration: 1, observedUtcMs: 101);
        var successor = FileTransferCoordinator.IssueReceiveRecoveryOperation(
            held.State, observation, BridgeRecoveryKind.BridgeReceiveStallRecovery, held.State.CurrentLeg?.CheckpointRequestId);
        Assert.True(successor.Accepted, successor.Reason);
        var rebound = successor.InitialActivationHoldDecision!.Value;
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held, rebound.Status);
        Assert.Equal(held.Decision.HoldGeneration, rebound.HoldGeneration);
        Assert.Equal(completed.Operation.Authority, rebound.PreviousReceiveRecoveryAuthority);
        Assert.Equal(request, rebound.DataSessionBinding!.Request);
        Assert.Equal(1, rebound.DataSessionBinding.TransferLocalGeneration);
        Assert.Equal(2, rebound.DataSessionBinding.Request.ExpectedScope.DataSessionGeneration);
        Assert.Equal(rebound.ReceiveRecoveryOperation!.Value.Authority.AuthorityId,
            rebound.DataSessionBinding.ReceiveRecoveryAuthorityId);
        Assert.Equal(rebound.DataSessionBinding,
            successor.State.PersistentState.FileTransferReceiveRecovery.CurrentInitialActivationHold!.DataSessionBinding);
        Assert.NotEqual(held.Decision.DataSessionBinding!.ReceiveRecoveryAuthorityId,
            rebound.DataSessionBinding.ReceiveRecoveryAuthorityId);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(successor.State).InstalledAttempt);
        Assert.Equal(state.CurrentRoutePublicationToken, successor.State.CurrentRoutePublicationToken);
    }

    [Theory]
    [InlineData("provider_replaced")]
    [InlineData("provider_aliases_local")]
    [InlineData("local_replaced")]
    [InlineData("identity")]
    [InlineData("terminal")]
    [InlineData("publication")]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockDataSessionScope")]
    public void RuntimeUnlockDataSessionScope_RejectsStaleProviderWithoutAuthority(string change)
    {
        var fixture = CreateNonRegularV4ReceiveRecoveryOperationFixture();
        var state = fixture.Decision.State with { CurrentDataSessionProviderGeneration = 2 };
        var request = CreateInitialActivationReceiveRecoveryHoldRequest(state, 72);
        request = request with { ExpectedScope = request.ExpectedScope with
        {
            DataSessionIdentity = $"{state.TransferId}:2", DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        } };
        if (change == "provider_replaced") state = state with { CurrentDataSessionProviderGeneration = 3 };
        if (change == "provider_aliases_local") request = request with { ExpectedScope = request.ExpectedScope with
            { DataSessionIdentity = $"{state.TransferId}:1", DataSessionGeneration = 1 } };
        if (change == "local_replaced") state = state with { CurrentDataSessionGeneration = 2, CurrentDataSessionIdentity = "replacement" };
        if (change == "identity") request = request with { ExpectedScope = request.ExpectedScope with { DataSessionIdentity = "other-transfer:2" } };
        if (change == "terminal") state = state with { IsTerminal = true };
        if (change == "publication") request = request with { ExpectedScope = request.ExpectedScope with
            { SourcePublicationAuthority = request.ExpectedScope.SourcePublicationAuthority! with { RoutePublicationGeneration = 999 } } };
        var result = FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(state, request);
        Assert.True(result.Decision.Status is RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected or
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Invalidated, result.Decision.Reason);
        Assert.Equal(state.CurrentRoutePublicationToken, result.State.CurrentRoutePublicationToken);
        Assert.Null(result.State.PersistentState.FileTransferReceiveRecovery.CurrentInitialActivationHold);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(result.State).InstalledAttempt);
        Assert.Null(result.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.Null(result.Decision.DataSessionBinding);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockDataSessionScope")]
    public void RuntimeUnlockDataSessionScope_ColdDialerBindingRetainsBothLifetimes()
    {
        var (original, retry, _) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var state = original with { CurrentDataSessionProviderGeneration = 2 };
        var request = ColdDialerRequest(state, retry) with
        {
            DataSessionIdentity = $"{state.TransferId}:2", DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        };
        var before = FileTransferCoordinator.CalculateSnapshotFingerprint(state);
        var bound = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, null);
        Assert.True(bound.Accepted, bound.Reason);
        Assert.Equal(2, bound.Receipt.Request.DataSessionGeneration);
        Assert.Equal(state.CurrentDataSessionGeneration, bound.Receipt.PairedScope.DataSessionGeneration);
        Assert.Equal(before, FileTransferCoordinator.CalculateSnapshotFingerprint(state));
        Assert.False(FileTransferCoordinator.BindOutboundFallbackDialerPreparation(
            state with { CurrentDataSessionProviderGeneration = 3 }, request, bound.Receipt).Accepted);
        Assert.False(FileTransferCoordinator.BindOutboundFallbackDialerPreparation(
            state with { IsTerminal = true }, request, bound.Receipt).Accepted);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt);
        Assert.Null(state.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockDataSessionScope")]
    public void RuntimeUnlockDataSessionScope_HeldRecoveryRevalidatesProviderBeforeRelease(
        bool nonRegular, bool providerReplaced)
    {
        var fixture = nonRegular ? CreateNonRegularV4ReceiveRecoveryOperationFixture() :
            CreateReceiveRecoveryOperationFixture();
        var state = fixture.Decision.State with { CurrentDataSessionProviderGeneration = 2 };
        var authority = Assert.IsType<FileTransferReceiveRecoveryOperationAuthority>(fixture.Decision.Authority);
        var request = CreateInitialActivationReceiveRecoveryHoldRequest(state, 73);
        request = request with { ExpectedScope = request.ExpectedScope with
        {
            DataSessionIdentity = $"{state.TransferId}:2", DataSessionGeneration = 2,
            DataSessionGenerationKind = RuntimeUnlockDataSessionGenerationKind.Provider,
        } };
        var held = FileTransferCoordinator.EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold(state, request);
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held, held.Decision.Status);
        Assert.NotNull(held.Decision.DataSessionBinding);
        var proof = FileTransferCoordinator.ObserveReceiveRecoveryProof(held.State,
            new FileTransferReceiveRecoveryProof(authority, "provider-local-proof", "receive_proof_observed", 2));
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held, proof.InitialActivationHoldDecision?.Status);
        var receipt = new FileTransferBridgeRecoveryExecutionReceipt(authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed, "soft", "bridge_recovery_execution_completed");
        var started = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(proof.State,
            new FileTransferBridgeRecoveryExecutionStartResult(authority.ExecutionCommand,
                FileTransferBridgeRecoveryExecutionStartStatus.Started, "bridge_recovery_execution_started",
                Task.FromResult(receipt)));
        var completed = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
            started.State with { CurrentDataSessionProviderGeneration = providerReplaced ? 3 : 2 }, receipt, 3);

        Assert.Equal(providerReplaced ? RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Invalidated :
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Released, completed.InitialActivationHoldDecision?.Status);
        Assert.Null(completed.State.PersistentState.FileTransferReceiveRecovery.CurrentInitialActivationHold);
        Assert.Equal(state.CurrentRoutePublicationToken, completed.State.CurrentRoutePublicationToken);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(completed.State).InstalledAttempt);
        Assert.Null(completed.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.Equal(held.Decision.DataSessionBinding, completed.InitialActivationHoldDecision?.DataSessionBinding);
        if (providerReplaced)
            Assert.Null(completed.InitialActivationHoldDecision?.EnvelopeRefreshCommand);
    }
}
