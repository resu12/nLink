namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class NativeParserBudgetTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "cursor-local-budget")]
    [InlineData("pwsh", "cursor-local-budget")]
    [InlineData("powershell", "cursor-cumulative-budget")]
    [InlineData("pwsh", "cursor-cumulative-budget")]
    [InlineData("powershell", "cursor-cancel-budget")]
    [InlineData("pwsh", "cursor-cancel-budget")]
    public async Task OwnedCursorExtendsOnlyLocalAllowanceAndRetainsRejectionChecks(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-parser-budget-20261001", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "buffered-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GenericAndGenericCursorKeepFiveSecondLimit(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/BufferedLedgerLoader.ps1') $RepoRoot
            [NLink.Tests.BufferedLedgerProbe]::CheckGenericCursorBudget()
            'generic-parser-budget=5000'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("generic-parser-budget=5000", result.Stdout);
    }
}
