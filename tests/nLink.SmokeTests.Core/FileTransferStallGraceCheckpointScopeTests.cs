namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceCheckpointScopeTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task StallGrace_CheckpointRetirementWithoutExactSuccessorBinding_RemainsUnresolvedInAllConsumers(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceCheckpointScope.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass checkpoint_retired", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass same_old_checkpoint", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass unrelated_checkpoint_grant", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass unrelated_publication_repair", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass conflicting_checkpoint_grant_first", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("checkpoint-scope-ok", result.Stdout, StringComparison.Ordinal);
    }
}
