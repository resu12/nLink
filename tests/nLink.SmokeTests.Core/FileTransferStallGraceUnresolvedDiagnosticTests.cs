namespace NLink.SmokeTests;

public sealed class FileTransferStallGraceUnresolvedDiagnosticTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task StallGrace_UnresolvedDiagnosticNeverRecoversAndHasCrossHostConsumerParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceUnresolvedDiagnostic.ps1') -RepoRoot $RepoRoot
            """;
        string? previous = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
            Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            Assert.Contains("stall-unresolved-diagnostic-ok", result.Stdout, StringComparison.Ordinal);
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("stall-unresolved-diagnostic-normalized=", StringComparison.Ordinal)));
            if (previous is not null) { Assert.Equal(previous, normalized); }
            previous = normalized;
        }
    }
}
