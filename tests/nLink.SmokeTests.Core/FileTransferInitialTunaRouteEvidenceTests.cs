using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialTunaRouteEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false, 1)]
    [InlineData(FileTransferDirection.Outbound, false, 1)]
    [InlineData(FileTransferDirection.Inbound, true, 1)]
    [InlineData(FileTransferDirection.Outbound, true, 1)]
    [InlineData(FileTransferDirection.Inbound, false, 2)]
    [InlineData(FileTransferDirection.Outbound, false, 2)]
    public async Task PreparedInitialTuna_RealOwnerAndPublicationPassWithExactNegativeControls(
        FileTransferDirection direction, bool independentGenerations, int publicationCount)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreatePreparedInitialTunaPublicationSnapshotFixture(
            direction, independentGenerations, publicationCount);
        var root = Path.Combine(Path.GetTempPath(), "nlink-initial-tuna-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "actual-emitter.log");
            await File.WriteAllLinesAsync(path, FileTransferInitialRegularRouteEvidenceTests.Emit(envelopes));
            var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "InitialTunaRouteSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, publicationCount.ToString()]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(root); }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorTransitionEnvelope[] CreatePreparedInitialTunaPublicationSnapshotFixture(
        FileTransferDirection direction, bool independentGenerations, int publicationCount)
    {
        var preparation = CreateSessionPreparationFixture(localIsPayer: direction == FileTransferDirection.Outbound, ready: true);
        var reserved = FileTransferCoordinator.ReserveSessionPreparationAdmission(preparation.State, preparation.Permit.Scope,
            "transfer_coordinator", direction, preparation.Now);
        var claimed = FileTransferCoordinator.ClaimSessionPreparationReadiness(reserved.State, reserved.Admission!,
            preparation.Lease, preparation.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, claimed.Status);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var empty = EmptyState(tuna, direction);
        empty = empty with
        {
            PersistentState = empty.PersistentState with { CoordinatorInstanceId = $"initial-prepared-tuna-{direction}" },
            LastRuntimeOwnerGeneration = independentGenerations ? 6 : 0,
            LastTransferLegGeneration = independentGenerations ? 12 : 0,
            CurrentDataSessionIdentity = "initial-prepared-tuna-session",
            CurrentDataSessionGeneration = 1,
        };
        var start = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            tuna, targetTransport: FileTransferTransportKind.Tuna, initialTunaReadinessEvidence: claimed.Evidence), empty);
        Assert.True(start.InitialTunaRouteSelectionDecision?.Accepted);
        var owner = start.State.CurrentRuntimeOwner!.Value;
        var leg = start.State.CurrentLeg!;
        Assert.Equal(0, owner.LiveRouteEpoch);
        Assert.Equal(independentGenerations ? 7 : 1, owner.RuntimeGeneration);
        Assert.Equal(independentGenerations ? 13 : 1, leg.Generation);
        FileTransferCoordinatorActionRequest Request(FileTransferCoordinatorActionKind kind) => new(kind,
            Source: "initial_prepared_tuna_evidence", Reason: "initial_publication",
            RuntimeOwner: owner, Route: tuna.Route, ProtocolVersion: 4,
            LiveRouteEpoch: leg.LiveRouteEpochId, LegGeneration: leg.Generation,
            RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: 0);
        var startLeg = FileTransferCoordinator.AuthorizeAction(start.State, Request(FileTransferCoordinatorActionKind.StartTransferLeg));
        Assert.True(startLeg.Accepted, startLeg.RejectionReason);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope> { start.SnapshotEnvelope!, startLeg.SnapshotEnvelope! };
        var state = startLeg.State;
        for (var index = 0; index < publicationCount; index++)
        {
            var publication = FileTransferCoordinator.AuthorizeAction(state, Request(FileTransferCoordinatorActionKind.RoutePublication));
            Assert.True(publication.Accepted, publication.RejectionReason);
            var token = Assert.IsType<RoutePublicationToken>(publication.RoutePublicationToken);
            Assert.Equal(index + 1, token.PublicationGeneration);
            Assert.Null(token.RouteCommitGeneration);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(publication.State, token, out _));
            envelopes.Add(publication.SnapshotEnvelope!);
            state = publication.State;
        }
        return envelopes.ToArray();
    }
}
