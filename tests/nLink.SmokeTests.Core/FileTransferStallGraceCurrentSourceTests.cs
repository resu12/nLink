namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceCurrentSourceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task StallGrace_HistoricalProgressAndCurrentRequest_UseExactCurrentSourceInActualGuiAndRetained()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = """
            param([string]$RepoRoot)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceCurrentSource.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root]);
        Assert.True(result.ExitCode == 0, $"Current source failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("current-source-ok", result.Stdout, StringComparison.Ordinal);
    }
}
