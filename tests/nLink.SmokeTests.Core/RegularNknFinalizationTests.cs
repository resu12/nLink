namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RegularNknFinalization")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class RegularNknFinalizationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("success")]
    [InlineData("scenario-failure")]
    [InlineData("drain-failure")]
    [InlineData("wrong-writer")]
    [InlineData("wrong-state")]
    [InlineData("hard-timeout")]
    [InlineData("finalizer-failure")]
    [InlineData("both-failures")]
    public Task RegularNkn_OriginalProducerAndWrapperFinalizeExactAttempt(string mode) => RunContractAsync(mode);

    [Fact]
    public Task RegularNkn_ChildTimeoutCannotBeClearedByNormalExit() => RunContractAsync("child-timeout");

    [Theory]
    [InlineData("filetransfer-gui-evidence-finalization-state.json")]
    [InlineData("filetransfer-gui-evidence-finalization-timeout.json")]
    [InlineData("filetransfer-gui-scenario-deadline-request.json")]
    [InlineData("filetransfer-gui-log-ledger.json")]
    [InlineData("filetransfer-gui-log-ledger-slice.log")]
    public Task RegularNkn_RefusesArtifactReuseBeforeClearingEvidence(string artifactName) =>
        RunContractAsync("reuse:" + artifactName);

    private async Task RunContractAsync(string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "regular-finalization-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellFileAsync(root,
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "RegularNknFinalization.ps1"),
            ["-RepoRoot", root, "-Directory", directory, "-Mode", mode],
            new Dictionary<string, string>
            {
                ["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "Modules")
            });
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("regular-finalization-ok", result.Stdout, StringComparison.Ordinal);
    }
}
