namespace NLink.SmokeTests;

public sealed class FileTransferPublicationEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task PublicationEvidence_OnlyExactIndependentPeerBindingsPreserveSourceAdmission(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/PublicationEvidence.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("publication-evidence-ok", result.Stdout, StringComparison.Ordinal);
    }
}
