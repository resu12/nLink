using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferBootstrapLocalStopScope BootstrapStopScopeForTests(FileTransferCoordinatorState state)
    {
        var publication = state.CurrentRoutePublicationToken!.Value;
        var commit = state.CurrentRouteCommit!.Value;
        var leg = state.CurrentLeg!;
        return new(FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state),
            FileTransferCoordinator.CreateRoutePublicationAuthority(publication)!,
            new(commit.RouteToken, 4, commit.LiveRouteEpoch, publication.RuntimeGeneration, publication.PublicationGeneration,
                commit.CommitGeneration, state.TerminalGeneration, leg.Generation, leg.TransportEpochId,
                leg.BridgeRecoveryGeneration, leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootstrapInitiation_CoreValidatesOriginAndScopeBeforeCurrentExchangeIdempotence(bool inbound)
    {
        var (state, stop, observation, localState, issued) = CreateBootstrapInitiationOriginFixture(inbound);
        AssertBootstrapInitiationRejectedOrigins(inbound, state, stop, observation, localState, issued);
        AssertBootstrapInitiationTerminalAndLegacyRejection(state, observation, localState);
    }

    [Theory]
    [InlineData(false, "sidecar_tuna_write_failed")]
    [InlineData(true, "sidecar_tuna_write_failed")]
    [InlineData(false, "sidecar_tuna_stream_eof")]
    [InlineData(true, "sidecar_tuna_stream_eof")]
    public void BootstrapInitiation_TransportFailureRejectsMissingOriginAndWrongSource(bool inbound, string reason)
    {
        var (state, stop, observation, localState, issued) = CreateBootstrapInitiationOriginFixture(inbound, reason);
        AssertBootstrapInitiationRejectedOrigins(inbound, state, stop, observation, localState, issued);
        AssertBootstrapInitiationTerminalAndLegacyRejection(state, observation, localState);
        foreach (var invalid in new[] {
            observation with { LocalStop = stop with { FailureOrigin = null } },
            observation with { LocalStop = stop with { FailureOrigin = stop.FailureOrigin! with { ClientGeneration = 0 } } },
            observation with { LocalStop = stop with { FailureOrigin = stop.FailureOrigin! with { ProcessRunId = "" } } },
            observation with { InitiationKind = FileTransferBootstrapInitiationKind.LocalRuntimeDisable } })
        {
            var rejected = FileTransferCoordinator.StartFallbackBootstrapExchange(issued.State, invalid, localState);
            Assert.False(rejected.Accepted);
            Assert.Equal(issued.State.PersistentState.FileTransferRouteTransition, rejected.State.PersistentState.FileTransferRouteTransition);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferBootstrapLocalStopObservation Stop,
        FileTransferFallbackV6ProofBootstrapObservation Observation, FileTransferFallbackBootstrapStateFrameV6? LocalState,
        FileTransferBootstrapExchangeDecision Issued) CreateBootstrapInitiationOriginFixture(bool inbound, string? failureReason = null)
    {
        var pair = CreateCorrelatedBootstrapPair();
        var state = inbound ? pair.Receiver : pair.Sender;
        var template = StartCorrelated(state).Command!;
        var stop = new FileTransferBootstrapLocalStopObservation(BootstrapStopScopeForTests(state), state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration)
            { FailureOrigin = failureReason is null ? null : new(Guid.NewGuid(), 1, "exact-failed-run", 1) };
        var observation = template.Bootstrap.Observation with
        {
            Source = failureReason ?? "runtime_disabled", ProofKind = failureReason is null ? "local_runtime_disabled" : "local_transport_failure",
            InitiationKind = failureReason is null ? FileTransferBootstrapInitiationKind.LocalRuntimeDisable : FileTransferBootstrapInitiationKind.LocalTransportFailure,
            LocalStop = stop,
        };
        var localState = template.Frame as FileTransferFallbackBootstrapStateFrameV6;
        state = ApplyEmptyBootstrapSourceHoldForTests(state, observation);
        var issued = FileTransferCoordinator.StartFallbackBootstrapExchange(state, observation, localState);
        Assert.True(issued.Accepted, issued.Reason);
        Assert.NotNull(issued.Command);
        Assert.Equal(inbound ? FileTransferBootstrapPhase.Announce : FileTransferBootstrapPhase.Solicit, issued.Command.Phase);
        var duplicate = FileTransferCoordinator.StartFallbackBootstrapExchange(issued.State, observation, localState);
        Assert.True(duplicate.Accepted, duplicate.Reason);
        Assert.Null(duplicate.Command);
        Assert.Equal(issued.State.PersistentState.FileTransferRouteTransition, duplicate.State.PersistentState.FileTransferRouteTransition);
        return (state, stop, observation, localState, issued);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertBootstrapInitiationRejectedOrigins(bool inbound, FileTransferCoordinatorState state,
        FileTransferBootstrapLocalStopObservation stop, FileTransferFallbackV6ProofBootstrapObservation observation,
        FileTransferFallbackBootstrapStateFrameV6? localState, FileTransferBootstrapExchangeDecision issued)
    {
        var invalid = new[]
        {
            observation with { InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence },
            observation with { InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence, LocalStop = null },
            observation with { InitiationKind = (FileTransferBootstrapInitiationKind)42 },
            observation with { LocalStop = null },
            observation with { Source = "remote_runtime_disabled" },
            observation with { ProofKind = "file_transfer_v6_state_frame_received" },
            observation with { TransferId = "other-transfer" },
            observation with { SessionId = "other-session" },
            observation with { Direction = inbound ? FileTransferDirection.Outbound : FileTransferDirection.Inbound },
            observation with { LocalStop = stop with { DataSessionIdentity = "replacement" } },
            observation with { LocalStop = stop with { DataSessionGeneration = stop.DataSessionGeneration + 1 } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { LocalIssuer = new string('a', 64) } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Publication = stop.Scope.Publication with { RoutePublicationGeneration = 99 } } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { RouteCommitGeneration = 99 } } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { LegGeneration = 99 } } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { CheckpointGeneration = 99 } } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { TransportEpoch = 99 } } } },
            observation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { BridgeRecoveryGeneration = 99 } } } },
        };
        foreach (var current in new[] { state, issued.State, issued.State with { IsTerminal = true } })
        {
            foreach (var candidate in invalid)
            {
                var rejected = FileTransferCoordinator.StartFallbackBootstrapExchange(current, candidate, localState);
                Assert.False(rejected.Accepted);
                Assert.Null(rejected.Command);
                Assert.Equal(current.PersistentState.FileTransferRouteTransition, rejected.State.PersistentState.FileTransferRouteTransition);
                Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertBootstrapInitiationTerminalAndLegacyRejection(FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation, FileTransferFallbackBootstrapStateFrameV6? localState)
    {
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(state with { IsTerminal = true }, observation, localState).Accepted);
        var legacy = state with { PersistentState = state.PersistentState with
            { FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with { BootstrapAgreement = null } } };
        Assert.False(FileTransferCoordinator.StartFallbackBootstrapExchange(legacy, observation, localState).Accepted);
        Assert.Equal(FileTransferFallbackV6ProofBootstrapDecisionStatus.Rejected,
            FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(legacy, observation).Status);
    }
}
