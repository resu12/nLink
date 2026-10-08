using System.Text;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferRecentProgressGraceOrderingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "RecentProgressCommitClock")]
    public async Task RecentProgressCommit_ActualCaptureRequiresExactWakeScopeAndUnchangedDeadline(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var lines = await SessionFileTransferV4SenderTests.CreateRecentProgressCommitClockEvidenceFixtureAsync(delayLog: true);
        var directory = Path.Combine(Path.GetTempPath(), "nlink-recent-commit-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "service.log");
            await File.WriteAllLinesAsync(sourcePath, lines, new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot, [string]$SourcePath)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/RecentProgressCommitClock.ps1') -RepoRoot $RepoRoot -SourcePath $SourcePath
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), sourcePath], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("recent-progress-commit-clock-ok:", result.Stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "RecentProgressGraceOrdering")]
    public Task RecentProgressGrace_RearmChainPreservesExactScopeAndPublicationDeadline(string host)
        => RunOrderingAsync(host, watchdogFirst: false);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "RecentProgressGraceOrdering")]
    public Task RecentProgressGrace_WatchdogFirstRequiresExactCurrentStallAndTimelyPublication(string host)
        => RunOrderingAsync(host, watchdogFirst: true);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "RecentProgressGraceOrdering")]
    public Task RecentProgressGrace_ArmDecisionDeadlineIsIndependentOfLogDelivery(string host)
        => RunOrderingAsync(host, watchdogFirst: false, decisionClock: true);

    private async Task RunOrderingAsync(string host, bool watchdogFirst, bool decisionClock = false)
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
                param([string]$RepoRoot, [string]$SourcePath, [string]$WatchdogFirst, [string]$DecisionClock)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/RecentProgressGraceOrdering.ps1') -RepoRoot $RepoRoot -SourcePath $SourcePath -WatchdogOnly:($WatchdogFirst -eq 'true') -DecisionClockOnly:($DecisionClock -eq 'true')
                """;
            var result = await RunPowerShellScriptTextAsync(script,
                [FindRepoRoot(), sourcePath, watchdogFirst ? "true" : "false", decisionClock ? "true" : "false"], host);
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
