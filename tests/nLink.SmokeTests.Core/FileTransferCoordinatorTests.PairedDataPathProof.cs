using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("wrong_writer", false)]
    [InlineData("changed_frame", false)]
    [InlineData("restarted_source", false)]
    [Trait("FileTransferCoordinatorSuite", "PairedDataPathProofFeasibility")]
    public void PairedDataPathProof_ExistingFrameIdentityLinksOnlyItsOriginalPublicationHistory(
        string control, bool expectedLink)
    {
        var sender = CreatePairedProofEndpoint(FileTransferDirection.Outbound, "paired-source-original");
        var receiver = CreatePairedProofEndpoint(FileTransferDirection.Inbound, "paired-receiver-original");
        var sourcePublication = sender.History.Single(envelope => envelope.Aggregate.Operations.Any(operation =>
            operation.OperationRole == "route_publication" && operation.BeforeState == "None" &&
            operation.RouteRuntime?.Boundary.Route == "regular_nkn_v4_fast"));
        var delivered = control == "changed_frame"
            ? sender.Frame with { SentUnixTimeMilliseconds = sender.Frame.SentUnixTimeMilliseconds + 1 }
            : sender.Frame;
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(FileTransferDataFrameCodec.Serialize(delivered), out var parsed));
        var receivedFrame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(parsed);
        var peerApplied = ApplyPairedProofFrame(receiver.State, receivedFrame);
        receiver.Capture(peerApplied.SnapshotEnvelope!);
        receiver.State = peerApplied.State;
        Assert.NotNull(peerApplied.PairedRelease);
        Assert.Equal(receiver.LocalProof.Scope.RoutePublication,
            peerApplied.PairedRelease!.Value.PeerProof.ObservationScope.RoutePublication);

        // Exchange the receiver's actual serialized proof in the opposite direction too.
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(FileTransferDataFrameCodec.Serialize(receiver.Frame), out parsed));
        var senderPaired = ApplyPairedProofFrame(sender.State, Assert.IsType<FileTransferFallbackTailYieldFrameV6>(parsed));
        sender.Capture(senderPaired.SnapshotEnvelope!);
        sender.State = senderPaired.State;
        Assert.NotNull(senderPaired.PairedRelease);

        // Do not let the route helper synthesize a replacement peer proof.
        var handoff = CreateCheckpointResponderCoexistencePairedHandoff(receiver.State, receiver.Capture, pairYield: false);
        var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(handoff.Request.ProofBoundary);
        Assert.Equal(peerApplied.PairedRelease, activation.FallbackTailPairedReleaseReceipt);
        var committed = CommitEmptyFileTransferRouteTransitionForTest(handoff.State, handoff.Request,
            handoff.FinalProof, invalidateRuntimeUnlockRetryLifecycle: false, capture: receiver.Capture);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.State.RouteSelection.Route);
        Assert.Equal(4, committed.State.CurrentRoutePublicationToken!.Value.ProtocolVersion);
        Assert.Equal(new[] { "regular_nkn_v4_fast", "file_tuna_v4", "post_tuna_fallback_v6", "file_tuna_v4" },
            receiver.History.SelectMany(envelope => envelope.Aggregate.Operations)
                .Where(operation => operation.OperationRole == "route_publication" && operation.BeforeState == "None")
                .Select(operation => operation.RouteRuntime!.Boundary.Route));

        var candidate = control == "restarted_source"
            ? CreatePairedProofEndpoint(FileTransferDirection.Outbound, "paired-source-restarted")
            : sender;
        if (control == "restarted_source")
        {
            Assert.Equal(sender.State.SessionId, candidate.State.SessionId);
            Assert.Equal(sender.State.TransferId, candidate.State.TransferId);
            Assert.Equal(sender.LocalProof.Decision.YieldGeneration, candidate.LocalProof.Decision.YieldGeneration);
            Assert.NotEqual(sender.LocalEnvelope.CoordinatorInstanceId, candidate.LocalEnvelope.CoordinatorInstanceId);
        }
        // Writer labels are fixture provenance, not new production authority.
        var candidateWriter = control == "wrong_writer" ? "unrelated-writer" : "source-writer";
        Assert.Equal(expectedLink, HasPairedProofSourceLink(candidate, candidateWriter, "source-writer",
            sourcePublication, peerApplied.SnapshotEnvelope!));
    }

    private sealed class PairedProofEndpoint
    {
        internal required FileTransferCoordinatorState State { get; set; }
        internal List<FileTransferCoordinatorTransitionEnvelope> History { get; } = [];
        internal FileTransferFallbackTailYieldFrameV6 Frame { get; set; } = null!;
        internal FileTransferFallbackTailLocalProofReceipt LocalProof { get; set; }
        internal FileTransferCoordinatorTransitionEnvelope LocalEnvelope { get; set; } = null!;

        internal void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            if (History.Count > 0)
            {
                Assert.Equal(History[^1].CoordinatorInstanceId, envelope.CoordinatorInstanceId);
                Assert.Equal(History[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
                Assert.Equal(History[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            }
            History.Add(envelope);
        }
    }

    private static PairedProofEndpoint CreatePairedProofEndpoint(FileTransferDirection direction, string instance)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var endpoint = new PairedProofEndpoint
        {
            State = EmptyState(regular, direction) with
            {
                CurrentDataSessionIdentity = "initial-responder-fallback-fixture-data-session",
                CurrentDataSessionGeneration = 17,
                PersistentState = new FileTransferCoordinatorPersistentState { CoordinatorInstanceId = instance },
            },
        };
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            regular, state: FileTransferLegState.Active, canSendData: true), endpoint.State);
        endpoint.Capture(started.SnapshotEnvelope!);
        endpoint.State = started.State;
        if (direction == FileTransferDirection.Inbound)
        {
            endpoint.State = ActivateInitialResponderTunaForFallbackFixture(endpoint.State, endpoint.Capture).State;
        }
        else
        {
            var setup = CreateRegularV4ToTunaRouteTransitionSetup(endpoint.State, endpoint.Capture);
            endpoint.State = CommitEmptyFileTransferRouteTransitionForTest(setup.State, setup.Request,
                setup.FinalProof, capture: endpoint.Capture).State;
        }
        endpoint.State = EnterCheckpointResponderFallback(endpoint.State, 1, endpoint.Capture);
        var yielded = MarkFallbackTailQuiescent(endpoint.State, endpoint.State.RouteSelection,
            endpoint.State.CurrentLeg!.TransportEpochId, committedChunk: 8, highestObservedChunk: 8);
        Assert.Null(yielded.MutationRejectedReason);
        endpoint.Capture(yielded.SnapshotEnvelope!);
        endpoint.State = yielded.State;
        var operation = endpoint.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        endpoint.Frame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(typeof(SessionFileTransferService)
            .GetMethod("CreateFallbackTailYieldFrame", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [operation, 8, 8]));
        var identity = PairedProofFrameToken("CreateFallbackTailYieldProofIdentity", endpoint.Frame);
        var fingerprint = PairedProofFrameToken("CreateFallbackTailYieldFrameFingerprint", endpoint.Frame);
        endpoint.LocalProof = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(endpoint.State,
            operation.Decision, identity, fingerprint, Math.Max(1, endpoint.Frame.SentUnixTimeMilliseconds));
        Assert.Equal(endpoint.State.CurrentRoutePublicationToken, endpoint.LocalProof.Scope.RoutePublication);
        Assert.True(endpoint.LocalProof.Scope.HasCompleteExecutionScope);
        var local = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(endpoint.State, endpoint.LocalProof);
        Assert.True(local.Accepted, local.Reason);
        endpoint.LocalEnvelope = local.SnapshotEnvelope!;
        endpoint.Capture(endpoint.LocalEnvelope);
        endpoint.State = local.State;
        return endpoint;
    }

    private static string PairedProofFrameToken(string method, FileTransferFallbackTailYieldFrameV6 frame)
        => Assert.IsType<string>(typeof(SessionFileTransferService)
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [frame]));

    private static FileTransferFallbackTailYieldTransitionDecision ApplyPairedProofFrame(
        FileTransferCoordinatorState state, FileTransferFallbackTailYieldFrameV6 frame)
    {
        var method = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplyFallbackTailPeerProofReceiptLocked" &&
                method.GetParameters()[0].ParameterType == typeof(FileTransferCoordinatorState));
        object?[] args = [state, frame, null, false, null];
        Assert.Equal(true, method.Invoke(null, args));
        var applied = Assert.IsType<FileTransferFallbackTailYieldTransitionDecision>(args[2]);
        Assert.True(applied.Accepted, applied.Reason);
        return applied;
    }

    // Experimental fixture-only join. This is not the Snapshot validator or a safety classifier.
    private static bool HasPairedProofSourceLink(PairedProofEndpoint source, string writer, string expectedWriter,
        FileTransferCoordinatorTransitionEnvelope oldPublication, FileTransferCoordinatorTransitionEnvelope peerEnvelope)
    {
        var local = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(source.LocalEnvelope);
        var peer = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(peerEnvelope);
        var localOperation = local.Aggregate.Operations.Single(operation => operation.OperationRole == "yield_current");
        var peerOperation = peer.Aggregate.Operations.Single(operation => operation.OperationRole == "yield_current");
        var localProof = localOperation.FallbackTailYield!;
        var peerProof = peerOperation.FallbackTailYield!;
        Assert.Contains(localProof.Receipts, receipt => receipt.Stage == "local_proof" && receipt.ReceiptValidated);
        Assert.Contains(peerProof.Receipts, receipt => receipt.Stage == "peer_proof" && receipt.ReceiptValidated);
        Assert.NotEqual("(none)", localProof.LocalProofIdentity);
        Assert.NotEqual("(none)", peerProof.PeerProofIdentity);
        return writer == expectedWriter && local.Direction == "outbound" && peer.Direction == "inbound" &&
            local.SessionId == peer.SessionId && local.TransferId == peer.TransferId &&
            source.History.Any(envelope => envelope.TransitionId == oldPublication.TransitionId &&
                envelope.CoordinatorInstanceId == oldPublication.CoordinatorInstanceId &&
                envelope.CalculatePayloadSha256() == oldPublication.CalculatePayloadSha256()) &&
            source.LocalProof.Decision.AuthorityId.ToString() == peerProof.PeerProofAuthorityId &&
            source.LocalProof.Decision.YieldGeneration == peerProof.PeerProofYieldGeneration &&
            localProof.LocalProofIdentity == peerProof.PeerProofIdentity &&
            localProof.LocalProofFingerprint == peerProof.PeerProofFingerprint;
    }
}
