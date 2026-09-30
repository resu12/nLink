namespace NLink.SmokeTests;

public sealed class FileTransferEfficiencyPublicationLookupTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "Semantic")]
    [InlineData("pwsh", "Semantic")]
    [InlineData("powershell", "Scaling")]
    [InlineData("pwsh", "Scaling")]
    [Trait("RetainedFallbackSuite", "EfficiencyPublicationLookup")]
    public async Task FrontierProgressClosures_PreservePublicationSemanticsWithBoundedSearch(string host, string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot, [string]$Mode)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/EfficiencyPublicationLookup.ps1') -RepoRoot $RepoRoot -Mode $Mode
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), mode], host);
        Assert.True(result.ExitCode == 0,
            $"{host}/{mode} failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("efficiency-publication-lookup-ok:", result.Stdout, StringComparison.Ordinal);
    }
}
