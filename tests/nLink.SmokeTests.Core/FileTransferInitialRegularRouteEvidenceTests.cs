using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialRegularRouteEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    public Task InitialRegularRoute_RealSnapshotsUseIndependentConsumers(FileTransferDirection direction, bool canceled)
        => AssertConsumersAsync(direction, canceled, publicationCount: 1);

    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    public Task SameOwnerRegularRepublication_RealSnapshotsUseIndependentConsumers(FileTransferDirection direction, bool canceled)
        => AssertConsumersAsync(direction, canceled, publicationCount: 2);

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    public Task InitialRegularRoute_RejectedDecisionsAndIndependentGenerationDomainsPreserveExactProof(FileTransferDirection direction)
        => AssertConsumersAsync(direction, canceled: false, publicationCount: 1, independentGenerations: true);

    private static async Task AssertConsumersAsync(FileTransferDirection direction, bool canceled, int publicationCount,
        bool independentGenerations = false)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateInitialRegularPublicationSnapshotFixture(
            direction, publicationCount, canceled, independentGenerations);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-initial-regular-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var path = Path.Combine(tempRoot, "actual-emitter.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes));
            var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "InitialRegularRouteSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script,
                [FindRepoRoot(), path, tempRoot, publicationCount.ToString(), canceled.ToString()]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    internal static string[] Emit(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        var emitterType = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(emitterType, nonPublic: true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        // Replace only the asynchronous sink. The production emitter projects
        // real Coordinator snapshots and binds effects to their exact receipts.
        emitterType.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(true, emitterType.GetMethod("Start", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));
        foreach (var envelope in envelopes)
        {
            emitterType.GetMethod("Enqueue", flags)!.Invoke(emitter, [envelope]);
            if (envelope.DecisionKind != nameof(FileTransferCoordinatorActionKind.RoutePublication) ||
                envelope.DecisionStatus != "Accepted") { continue; }
            var publication = Assert.Single(envelope.Aggregate.Operations, operation =>
                operation.Family == "route_runtime" && operation.OperationRole == "route_publication" &&
                operation.AfterState == "Published");
            emitterType.GetMethod("EnqueueAuthorityEffect", flags)!.Invoke(emitter,
                [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                    envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                    publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                    publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                    publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
        }
        // Stream completion is evidence finalization, not transfer completion.
        emitterType.GetMethod("Complete", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)emitterType.GetField("pending", flags)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=initial-regular-writer; log_writer_sequence={index + 1}")).ToArray();
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialRegularPublicationSnapshotFixture(
        FileTransferDirection direction, int publicationCount, bool canceled, bool independentGenerations = false)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var empty = EmptyState(regular, direction);
        empty = empty with
        {
            PersistentState = empty.PersistentState with { CoordinatorInstanceId = $"initial-regular-{direction}" },
            LastRuntimeOwnerGeneration = independentGenerations ? 6 : 0,
            LastTransferLegGeneration = independentGenerations ? 12 : 0,
            CurrentDataSessionIdentity = "initial-regular-data-session",
            CurrentDataSessionGeneration = 1,
        };
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        FileTransferCoordinatorState RejectPublication(FileTransferCoordinatorState current)
        {
            var rejected = FileTransferCoordinator.AuthorizeAction(current,
                new FileTransferCoordinatorActionRequest(FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "initial_regular_evidence", Reason: "rejected_publication",
                    RuntimeOwner: default(RuntimeOwnerToken), Route: regular.Route, ProtocolVersion: regular.ProtocolVersion));
            Assert.False(rejected.Accepted);
            Assert.Equal(current.CurrentRuntimeOwner, rejected.State.CurrentRuntimeOwner);
            Assert.Equal(current.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
            envelopes.Add(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(rejected.SnapshotEnvelope));
            return rejected.State;
        }
        if (independentGenerations) { empty = RejectPublication(empty); }
        var started = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, regular), empty);
        var state = started.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        Assert.Null(state.CurrentLiveRouteEpoch);
        Assert.Equal(0, owner.LiveRouteEpoch);
        envelopes.Add(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(started.SnapshotEnvelope));
        if (independentGenerations)
        {
            Assert.Equal(7, owner.RuntimeGeneration);
            Assert.Equal(13, leg.Generation);
            state = RejectPublication(state);
        }
        for (var index = 0; index < publicationCount; index++)
        {
            var publication = FileTransferCoordinator.AuthorizeAction(state,
                new FileTransferCoordinatorActionRequest(FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "initial_regular_evidence", Reason: "route_publication",
                    RuntimeOwner: owner, Route: regular.Route, ProtocolVersion: regular.ProtocolVersion,
                    LiveRouteEpoch: leg.LiveRouteEpochId, LegGeneration: leg.Generation,
                    RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: state.TerminalGeneration));
            Assert.True(publication.Accepted, publication.RejectionReason);
            var token = Assert.IsType<RoutePublicationToken>(publication.RoutePublicationToken);
            Assert.Equal(index + 1, token.PublicationGeneration);
            Assert.Equal(owner, publication.State.CurrentRuntimeOwner);
            Assert.Equal(leg.Generation, publication.State.CurrentLeg!.Generation);
            Assert.Null(token.RouteCommitGeneration);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(publication.State, token, out _));
            envelopes.Add(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(publication.SnapshotEnvelope));
            state = publication.State;
        }
        if (canceled)
        {
            var terminal = FileTransferCoordinator.Apply(
                CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular,
                    reason: FileTransferResultCodes.CanceledLocal), state);
            Assert.True(terminal.State.IsTerminal);
            envelopes.Add(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(terminal.SnapshotEnvelope));
        }
        else { Assert.False(state.IsTerminal); }
        return envelopes.ToArray();
    }
}
