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
        Action<FileTransferCoordinatorTransitionEnvelope>? capture = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var initialState = EmptyState(regular, direction) with { SessionId = sessionId, TransferId = transferId };
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
        return publications;
    }
}
