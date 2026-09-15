using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "InboundPublicationTransition")]
    public async Task InboundPublicationTransition_RealServiceCommitBindsProjectionAndRejectsStaleDrain(bool advanceToFallback)
    {
        var transferId = "inbound_publication_transition_" + advanceToFallback;
        using var service = new SessionFileTransferService();
        using var dataSession = new InitialBootstrapRecordingDataSession("session_" + transferId, transferId);
        using var bridge = new InitialBootstrapCheckpointTransport(dataSession);
        object? originalExecution = null;
        FileTransferCoordinatorState? preparedState = null;
        var logStart = GetOperationalLogLength();
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession,
            afterPreparation: (current, execution) =>
            {
                originalExecution = execution;
                preparedState = CreateCoordinatorStateForTest(current);
                Assert.NotNull(preparedState.PersistentState.FileTransferRouteTransition.CurrentOperation);
                Assert.Empty(TransitionRows());
            });
        Assert.NotNull(originalExecution);
        var firstTuna = CreateCoordinatorStateForTest(context);
        AssertProjection(firstTuna, "regular_nkn_v4_fast", "file_tuna_v4", 1, Assert.Single(TransitionRows()));
        Assert.Equal(preparedState!.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value.Authority,
            firstTuna.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.Authority);
        Assert.NotNull(firstTuna.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.DispositionReceipt);
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));

        if (advanceToFallback)
        {
            var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
            await WaitUntilAsync(() => GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution") is null, 5000);
            var fallback = CreateCoordinatorStateForTest(context);
            Assert.Equal(2, TransitionRows().Length);
            AssertProjection(fallback, "file_tuna_v4", "post_tuna_fallback_v6", 2, TransitionRows()[1]);
        }

        var before = CreateCoordinatorStateForTest(context);
        var beforeFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(before);
        var beforeRows = TransitionRows();
        var beforeHints = bridge.ObservedRouteHints.Count;
        var beforeFrames = dataSession.SentFrames.Count;
        var beforeKeys = GetPrivateProperty<HashSet<string>>(context, "FileTransferRouteTransitionCompatibilityProjectionKeys").ToArray();
        var lateStart = GetOperationalLogLength();
        var recheck = typeof(SessionFileTransferService).GetMethod("RecheckInboundFileTransferRouteTransitionDrainAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Reuse the actual original execution, not a reconstructed callback DTO.
        for (var i = 0; i < 2; i++)
        {
            await Assert.IsAssignableFrom<Task>(recheck.Invoke(service, [context, originalExecution]));
        }
        var after = CreateCoordinatorStateForTest(context);
        Assert.Equal(beforeFingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(after));
        Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        Assert.Equal(before.PersistentState, after.PersistentState);
        Assert.Equal(beforeRows, TransitionRows());
        Assert.Equal(beforeHints, bridge.ObservedRouteHints.Count);
        Assert.Equal(beforeFrames, dataSession.SentFrames.Count);
        Assert.Equal(beforeKeys, GetPrivateProperty<HashSet<string>>(context, "FileTransferRouteTransitionCompatibilityProjectionKeys").ToArray());
        var lateLog = ReadOperationalLogTail(lateStart);
        Assert.DoesNotContain("event=filetransfer_route_transitioned;", lateLog, StringComparison.Ordinal);
        Assert.DoesNotContain("effect_kind=route_publication_forwarded;", lateLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_live_route_epoch_recovered;", lateLog, StringComparison.Ordinal);

        string[] TransitionRows() => ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("event=filetransfer_route_transitioned;", StringComparison.Ordinal) &&
            line.Contains("transfer_id=" + transferId + ";", StringComparison.Ordinal)).Select(line => line.TrimEnd('\r')).ToArray();

        static void AssertProjection(FileTransferCoordinatorState state, string previous, string route, int epoch, string line)
        {
            var operation = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
            var publication = Assert.IsType<RoutePublicationToken>(operation.DispositionCommand!.Value.RoutePublicationToken);
            Assert.Equal(FileTransferRouteTransitionState.Committed, operation.State);
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, operation.DispositionReceipt!.Value.Status);
            Assert.Equal(publication, state.CurrentRoutePublicationToken);
            Assert.Equal(publication.RuntimeOwner, state.CurrentRuntimeOwner);
            foreach (var field in new[]
            {
                "direction=inbound", "previous_route=" + previous, "new_route=" + route, "route=" + route,
                "protocol_version=" + publication.ProtocolVersion, "live_route_epoch=" + epoch,
                "runtime_generation=" + publication.RuntimeGeneration, "terminal_generation=" + publication.TerminalGeneration,
                "leg_generation=" + publication.LegGeneration, "route_publication_generation=" + publication.PublicationGeneration,
                "route_commit_generation=" + (publication.RouteCommitGeneration ?? 0),
                "projection_source=coordinator_route_transition",
                // Raw authority IDs are intentionally redacted by the logger.
                // Exact identity is asserted on the Coordinator objects above.
                "transition_authority_id=[redacted]",
                "transition_generation=" + operation.Authority.TransitionGeneration,
            }) { Assert.True((line + ";").Contains("; " + field + ";", StringComparison.Ordinal), "Missing " + field + " in: " + line); }
        }
    }
}
