using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSecondTransferFailureTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SecondTransferFailure_PreservesOriginalErrorAndPersistsFailedPhase(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(FindRepoRoot(), "artifacts", "second-transfer-failure-reporting-20260923",
            $"{host}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = FileTransferCoordinatorTests.CreateInitialRegularPublicationSnapshotFixture(
            FileTransferDirection.Outbound, publicationCount: 1, canceled: false);
        var fixturePath = Path.Combine(root, "fresh-snapshot.log");
        // Fresh Core decisions and production emitter; no historical/live replay.
        await File.WriteAllLinesAsync(fixturePath, FileTransferInitialRegularRouteEvidenceTests.Emit(fixture));
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "SecondTransferFailureReporting.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), fixturePath, root], host);
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("second-transfer-failure-reporting-ok cases=14", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("accept-failure-reporting-ok cases=7", result.Stdout, StringComparison.Ordinal);
    }
}
