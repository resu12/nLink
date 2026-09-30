namespace NLink.SmokeTests;

public sealed class FileTransferAlternativeAcceptanceShadowTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "AlternativeAcceptanceShadow")]
    public async Task AlternativeAcceptanceShadow_DetachedDecisionsFailClosed(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "PublicationAlternativeAcceptanceControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("alternative-acceptance-controls=", result.Stdout, StringComparison.Ordinal);
    }
}
