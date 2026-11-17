using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BootstrapPeerAdmission_RejectsUnboundObservationBeforeHoldOrExchange(bool inbound)
    {
        var pair = CreateCorrelatedBootstrapPair();
        var state = inbound ? pair.Receiver : pair.Sender;
        var wake = StartCorrelated(inbound ? pair.Sender : pair.Receiver).Command!.Frame;
        var local = StartCorrelated(state).Command!;
        var observation = local.Bootstrap.Observation with
        {
            Source = "correlated_bootstrap_peer_frame",
            ProofKind = inbound ? "file_transfer_v6_frontier_request_frame_received" : "file_transfer_v6_state_frame_received",
            InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence, LocalStop = null,
        };
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, wake);
        Assert.True(prepared.Accepted, prepared.Reason);
        var drained = ApplyEmptyBootstrapSourceHoldForTests(state, observation, wake);
        var issued = FileTransferCoordinator.StartFallbackBootstrapExchange(drained, observation,
            local.Frame as FileTransferFallbackBootstrapStateFrameV6, wake as FileTransferFallbackBootstrapRequestFrameV6);
        Assert.True(issued.Accepted, issued.Reason);
        var invalid = new[]
        {
            observation with { Source = "tuna_unavailable_before_send", ProofKind = "file_transfer_v4_state_frame_sent" },
            observation with { Source = "tuna_unavailable_before_send" },
            observation with { ProofKind = "file_transfer_v4_state_frame_sent" },
            observation with { ProofKind = inbound ? "file_transfer_v6_state_frame_received" : "file_transfer_v6_frontier_request_frame_received" },
            observation with { SessionId = "other-session" },
            observation with { TransferId = "other-transfer" },
            observation with { Direction = inbound ? FileTransferDirection.Outbound : FileTransferDirection.Inbound },
            observation with { ObservationGeneration = 0 },
            observation with { LocalStop = local.Bootstrap.Observation.LocalStop },
            observation with { DurableCheckpoint = observation.DurableCheckpoint with { DataSessionGeneration = 999 } },
            observation with { DurableCheckpoint = observation.DurableCheckpoint with { SourceTransportEpoch = 999 } },
            observation with { DurableCheckpoint = observation.DurableCheckpoint with { SourceTerminalGeneration = 999 } },
            observation with { DurableCheckpoint = observation.DurableCheckpoint with
                { SourceRuntimeOwner = observation.DurableCheckpoint.SourceRuntimeOwner with { RuntimeGeneration = 999 } } },
            observation with { DurableCheckpoint = observation.DurableCheckpoint with
                { SourceRoutePublication = observation.DurableCheckpoint.SourceRoutePublication with { PublicationGeneration = 999 } } },
        };
        foreach (var current in new[] { state, prepared.State, issued.State })
        foreach (var candidate in invalid)
        {
            var rejected = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(current, candidate, wake);
            Assert.False(rejected.Accepted);
            Assert.Same(current, rejected.State);
            Assert.Null(rejected.Command);
            Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
            var exchange = FileTransferCoordinator.StartFallbackBootstrapExchange(current, candidate,
                local.Frame as FileTransferFallbackBootstrapStateFrameV6, wake as FileTransferFallbackBootstrapRequestFrameV6);
            Assert.False(exchange.Accepted);
            Assert.Same(current, exchange.State);
            Assert.Null(exchange.Command);
        }
    }
}
