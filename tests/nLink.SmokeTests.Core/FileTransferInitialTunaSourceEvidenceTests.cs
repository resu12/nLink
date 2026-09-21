namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialTunaSourceEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InitialTunaSource_CallbackSnapshotsPreserveInitialOwnerAndRetirement(bool terminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), "nlink-initial-tuna-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "actual-emitter.log");
            await File.WriteAllLinesAsync(path, FileTransferInitialRegularRouteEvidenceTests.Emit(
                FileTransferCoordinatorTests.CreateInitialTunaSourceCallbackSnapshotFixture(terminal)));
            var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core", "Support", "InitialTunaSourceCallbackEvidence.ps1"));
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, terminal.ToString()], host);
                Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            }
        }
        finally { TryDeleteDirectory(root); }
    }
}
