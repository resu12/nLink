namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiProcessCleanupTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiProcessCleanup_ReportsOutcomesWithoutChangingActionsOrSharedWait(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), $"nlink-cleanup-detail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "GuiProcessCleanup.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), root], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("gui-process-cleanup-diagnostic-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
