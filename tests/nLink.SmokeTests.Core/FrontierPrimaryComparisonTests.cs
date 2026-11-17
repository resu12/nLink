namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierPrimaryComparisonTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task OriginalPrimaryComparisons_RejectEveryByteMutationAndRetirement(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierPrimaryComparison.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-primary-comparisons-ok", result.Stdout);
    }
}
