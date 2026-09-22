namespace NLink.SmokeTests;

public sealed class FileTransferFailedGuiAnalysisStatusTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DirectManifest_KnownFailedGuiExitDoesNotImplyPendingAnalysis(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var outputRoot = Path.Combine(Path.GetTempPath(), $"nlink-failed-gui-status-{Guid.NewGuid():N}");
        try
        {
            const string script = """
                param([string]$RepoRoot, [string]$OutputRoot)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/FailedGuiAnalysisStatus.ps1') -RepoRoot $RepoRoot -OutputRoot $OutputRoot
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), outputRoot], host, initializeHostModulePath: true);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("failed-gui-analysis-status-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(outputRoot);
        }
    }
}
