using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    // Reuse the real Coordinator route/receipt helpers. This supplies authority
    // to a transport fixture, not a synthetic accepted route or a live run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<RoutePublicationToken> CreateDataPathIngressRouteCycle(
        string sessionId, string transferId, FileTransferDirection direction,
        Action<FileTransferCoordinatorTransitionEnvelope>? capture = null,
        bool initializeDataSession = false,
        Action<FileTransferFallbackTailYieldFrameV6>? captureFallbackYield = null,
        Action<FileTransferCoordinatorState>? captureFallbackState = null,
        Action<FileTransferCoordinatorState>? captureSecondTunaState = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var initialState = EmptyState(regular, direction) with { SessionId = sessionId, TransferId = transferId };
        if (initializeDataSession)
        {
            // Match the existing transition fixture checkpoint before authoring
            // any Snapshot boundary, rather than repairing emitted history.
            initialState = initialState with
            {
                CurrentDataSessionIdentity = "initial-responder-fallback-fixture-data-session",
                CurrentDataSessionGeneration = 17,
            };
        }
        if (capture is not null)
        {
            initialState = initialState with
            {
                PersistentState = new FileTransferCoordinatorPersistentState { CoordinatorInstanceId = Guid.NewGuid().ToString("N") },
            };
        }
        var started = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, regular,
                state: FileTransferLegState.Active, canSendData: true),
            initialState);
        capture?.Invoke(started.SnapshotEnvelope!);
        var state = started.State;
        if (state.CurrentRoutePublicationToken is null)
        {
            var owner = state.CurrentRuntimeOwner!.Value;
            var published = FileTransferCoordinator.AuthorizeAction(state,
                new FileTransferCoordinatorActionRequest(FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "data_path_ingress_fixture", Reason: "new_transfer", RuntimeOwner: owner,
                    Route: regular.Route, ProtocolVersion: regular.ProtocolVersion,
                    LiveRouteEpoch: state.CurrentLeg!.LiveRouteEpochId,
                    LegGeneration: state.CurrentLeg.Generation, RuntimeGeneration: owner.RuntimeGeneration,
                    TerminalGeneration: state.TerminalGeneration, TargetRoute: regular.Route,
                    TargetProtocolVersion: regular.ProtocolVersion, TargetTransport: FileTransferTransportKind.RegularNkn));
            Assert.True(published.Accepted, published.RejectionReason);
            capture?.Invoke(published.SnapshotEnvelope!);
            state = published.State;
        }

        var publications = new List<RoutePublicationToken> { state.CurrentRoutePublicationToken!.Value };
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var activated = direction == FileTransferDirection.Inbound
            ? ActivateInitialResponderTunaForFallbackFixture(state, capture)
            : ActivateTuna(state, tuna, committedChunk: 4, capture);
        state = activated.State;
        publications.Add(state.CurrentRoutePublicationToken!.Value);

        state = EnterCheckpointResponderFallback(state, 1, envelope => capture?.Invoke(envelope));
        publications.Add(state.CurrentRoutePublicationToken!.Value);
        if (captureFallbackYield is not null)
        {
            Assert.Equal(FileTransferDirection.Outbound, direction);
            var yielded = MarkFallbackTailQuiescent(state, state.RouteSelection,
                state.CurrentLeg!.TransportEpochId, committedChunk: 100, highestObservedChunk: 99);
            Assert.Null(yielded.MutationRejectedReason);
            capture?.Invoke(yielded.SnapshotEnvelope!);
            state = yielded.State;
            var operation = state.PersistentState.FallbackTailYield.CurrentOperation!.Value;
            var frame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(typeof(SessionFileTransferService)
                .GetMethod("CreateFallbackTailYieldFrame", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [operation, 100, 99]));
            var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(state, operation.Decision,
                PairedProofFrameToken("CreateFallbackTailYieldProofIdentity", frame),
                PairedProofFrameToken("CreateFallbackTailYieldFrameFingerprint", frame), Math.Max(1, frame.SentUnixTimeMilliseconds));
            var applied = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(state, local);
            Assert.True(applied.Accepted, applied.Reason);
            capture?.Invoke(applied.SnapshotEnvelope!);
            captureFallbackState?.Invoke(applied.State);
            captureFallbackYield(frame);
            return publications;
        }
        if (direction == FileTransferDirection.Inbound)
        {
            var paired = CreateCheckpointResponderCoexistencePairedHandoff(state, envelope => capture?.Invoke(envelope));
            var committed = CommitEmptyFileTransferRouteTransitionForTest(paired.State, paired.Request,
                paired.FinalProof, invalidateRuntimeUnlockRetryLifecycle: false, capture: capture);
            state = committed.State;
        }
        else
        {
            activated = ActivateTuna(state, tuna, committedChunk: 8, capture);
            state = activated.State;
        }
        publications.Add(state.CurrentRoutePublicationToken!.Value);
        Assert.Equal(new[] { "regular_nkn_v4_fast", "file_tuna_v4", "post_tuna_fallback_v6", "file_tuna_v4" },
            publications.Select(publication => publication.RouteToken));
        Assert.All(publications, publication =>
        {
            Assert.Equal(sessionId, publication.SessionId);
            Assert.Equal(transferId, publication.TransferId);
            Assert.Equal(direction, publication.Direction);
        });
        captureSecondTunaState?.Invoke(state);
        return publications;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorState EnterIngressFinalFallback(FileTransferCoordinatorState state,
        Action<FileTransferCoordinatorTransitionEnvelope> capture, long transportEpoch = 2)
    {
        Assert.Equal("file_tuna_v4", state.CurrentRoutePublicationToken!.Value.RouteToken);
        Assert.Equal(3, state.CurrentRoutePublicationToken.Value.LiveRouteEpoch);
        var fallback = EnterCheckpointResponderFallback(state, transportEpoch, capture);
        var publication = fallback.CurrentRoutePublicationToken!.Value;
        Assert.Equal("post_tuna_fallback_v6", publication.RouteToken);
        Assert.Equal(6, publication.ProtocolVersion);
        Assert.Equal(4, publication.LiveRouteEpoch);
        Assert.Equal(5, publication.PublicationGeneration);
        return fallback;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static RoutePublicationToken CompleteIngressSecondTunaFromPeerYield(FileTransferCoordinatorState state,
        FileTransferFallbackTailYieldFrameV6 peerFrame, Action<FileTransferCoordinatorTransitionEnvelope> capture,
        Action<FileTransferCoordinatorState>? captureCommitted = null)
    {
        var paired = ApplyPairedProofFrame(state, peerFrame);
        capture(paired.SnapshotEnvelope!);
        var release = paired.PairedRelease!.Value;
        Assert.Equal(peerFrame.AuthorityId, release.PeerProof.Proof.AuthorityId.ToString());
        Assert.True(FileTransferCoordinator.TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
            paired.State, release, "runtime_unlock_offer_send_not_observed", "post_tuna_fallback_tail_unblocked",
            out var request, out var reason), reason);
        var yield = paired.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision;
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(paired.State, request,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, yield.AuthorityId, yield.YieldGeneration, yield.YieldKind.ToString());
        Assert.True(granted.Granted);
        capture(granted.SnapshotEnvelope!);
        var installed = StageAndInstallPersistentRetryLifecycle(granted.State, granted, request,
            stagedUtcMs: 3_000_000, capture: capture);
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup(installed.State, capture);
        Assert.Equal(release, Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(setup.Request.ProofBoundary)
            .FallbackTailPairedReleaseReceipt);
        var committed = CommitEmptyFileTransferRouteTransitionForTest(setup.State, setup.Request, setup.FinalProof, capture: capture);
        var publication = committed.State.CurrentRoutePublicationToken!.Value;
        Assert.Equal(FileTransferRoute.FileTunaV4, publication.Route);
        Assert.Equal(3, publication.LiveRouteEpoch);
        Assert.Equal(4, publication.ProtocolVersion);
        captureCommitted?.Invoke(committed.State);
        return publication;
    }
}
