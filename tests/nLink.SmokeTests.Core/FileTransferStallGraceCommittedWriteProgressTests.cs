namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceCommittedWriteProgressTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task StallGrace_CommittedWriteProgress_RequiresExactCurrentSourceWitness(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceCommittedWriteProgress.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("committed-write-progress-ok", result.Stdout, StringComparison.Ordinal);
    }
}
