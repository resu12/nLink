using System.Text;

namespace NLink.SmokeTests;

public sealed class FileTransferRecentProgressGraceOrderingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "RecentProgressGraceOrdering")]
    public async Task RecentProgressGrace_RearmChainPreservesExactScopeAndPublicationDeadline(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var lines = await SessionFileTransferV4SenderTests.CreateCheckpointRepairContinuityEvidenceFixtureAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-recent-grace-ordering", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var sourcePath = Path.Combine(tempRoot, "service-receipts.log");
            await File.WriteAllLinesAsync(sourcePath, lines, new UTF8Encoding(false));
            var script = """
                param([string]$RepoRoot, [string]$SourcePath)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/RecentProgressGraceOrdering.ps1') -RepoRoot $RepoRoot -SourcePath $SourcePath
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), sourcePath], host);
            Assert.True(result.ExitCode == 0,
                $"{host} ordering probe failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("recent-grace-ordering-ok:", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
