namespace NLink.SmokeTests;

public sealed class FileTransferAccountingAvailabilityTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "Imports")]
    [InlineData("pwsh", "Imports")]
    [InlineData("powershell", "Summary")]
    [InlineData("pwsh", "Summary")]
    [InlineData("powershell", "Overrides")]
    [InlineData("pwsh", "Overrides")]
    [InlineData("powershell", "Compatibility")]
    [InlineData("pwsh", "Compatibility")]
    public async Task AccountingAvailability_ReportOnlyContract(string host, string lane)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var outputRoot = Path.Combine(Path.GetTempPath(), $"nlink-accounting-availability-{Guid.NewGuid():N}");
        try
        {
            const string script = """
                param([string]$RepoRoot, [string]$OutputRoot, [string]$Lane)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/AccountingAvailability.ps1') -RepoRoot $RepoRoot -OutputRoot $OutputRoot -Lane $Lane
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), outputRoot, lane], host, initializeHostModulePath: true);
            Assert.True(result.ExitCode == 0, $"{host}/{lane}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains($"accounting-availability-{lane}-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(outputRoot);
        }
    }
}
