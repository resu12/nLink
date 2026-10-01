using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialSelectionRouteEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSelection_ExactOwnerlessFallbackPreservesPublicationGuards(bool correlated)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(FindRepoRoot(), "artifacts", "initial-selection-evidence-20261001",
            $"fixture-{correlated}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "actual-emitter.log");
        // Real Coordinator decisions and production projection; forwarding is
        // modeled here, not live delivery. Retained service logs are checked separately.
        await File.WriteAllLinesAsync(path, FileTransferInitialRegularRouteEvidenceTests.Emit(
            FileTransferCoordinatorTests.CreateInitialSelectionPublicationFixture(correlated)));
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "InitialSelectionRouteEvidence.ps1"));
        string? first = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, correlated.ToString()], host);
            await File.WriteAllTextAsync(Path.Combine(root, host + ".txt"), result.Stdout + Environment.NewLine + result.Stderr);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{')));
            if (first is not null) { Assert.Equal(first, normalized); }
            first = normalized;
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialSelectionPublicationFixture(bool correlated)
    {
        var source = InitialOfferState();
        var initial = FileTransferCoordinator.ApplyInitialOutboundOffer(InitialOfferStart(source, false),
            source, InitialOfferDraft(source, correlated), correlated);
        Assert.Equal("initial_tuna_readiness_missing", initial.Start.InitialTunaRouteSelectionDecision!.Value.Reason);
        Assert.True(initial.Start.InitialTunaRouteSelectionDecision.Value.FallbackApplied);
        var state = initial.Start.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, owner.Route);
        var published = FileTransferCoordinator.AuthorizeAction(state,
            new(FileTransferCoordinatorActionKind.RoutePublication, Source: "initial_selection_fixture", Reason: "new_transfer",
                RuntimeOwner: owner, Route: owner.Route, ProtocolVersion: 4, LiveRouteEpoch: 0,
                LegGeneration: state.CurrentLeg!.Generation, RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: 0));
        Assert.True(published.Accepted, published.RejectionReason);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        if (initial.Offer is { } pin) { envelopes.Add(pin.SnapshotEnvelope!); }
        envelopes.Add(initial.Start.SnapshotEnvelope!);
        envelopes.Add(published.SnapshotEnvelope!);
        return envelopes.ToArray();
    }
}
