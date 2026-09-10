using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    // Reuse the real Coordinator route/receipt helpers. This supplies authority
    // to a transport fixture, not a synthetic accepted route or a live run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<RoutePublicationToken> CreateDataPathIngressRouteCycle(
        string sessionId, string transferId, FileTransferDirection direction)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, regular,
                state: FileTransferLegState.Active, canSendData: true),
            EmptyState(regular, direction) with { SessionId = sessionId, TransferId = transferId }).State;
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
            state = published.State;
        }

        var publications = new List<RoutePublicationToken> { state.CurrentRoutePublicationToken!.Value };
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        state = direction == FileTransferDirection.Inbound
            ? ActivateInitialResponderTunaForFallbackFixture(state).State
            : ActivateTuna(state, tuna, committedChunk: 4).State;
        publications.Add(state.CurrentRoutePublicationToken!.Value);

        state = EnterCheckpointResponderFallback(state, 1, _ => { });
        publications.Add(state.CurrentRoutePublicationToken!.Value);
        if (direction == FileTransferDirection.Inbound)
        {
            var paired = CreateCheckpointResponderCoexistencePairedHandoff(state, _ => { });
            state = CommitEmptyFileTransferRouteTransitionForTest(paired.State, paired.Request,
                paired.FinalProof, invalidateRuntimeUnlockRetryLifecycle: false).State;
        }
        else
        {
            state = ActivateTuna(state, tuna, committedChunk: 8).State;
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
