namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierParentCommitTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "path-binding", "copied-ack", "copied-custody", "copied-binding", "copied-request", "repeat-prepare",
            "parent-budget", "gui-budget", "missing-prepare", "cancel-before", "failed-completion", "gui-failure", "missing-commit",
            "repeat-take", "post-pin", "post-body", "commit-binding", "commit-state", "legacy-body", "legacy-scope", "request-dispose",
            "output-gate", "state-gate", "dispose-custody", "post-accept-mutation", "post-accept-pin", "repeat-gui", "request-signal", "joined-mismatch",
            "post-length", "post-digest", "post-tail", "late-parent-budget" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ParentCommit_RequiresOriginalCompletionAndSuccessfulExit(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-parent-commit-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
        if (scenario == "path-binding") { Assert.Contains("frontier-parent-path-binding=13", result.Stdout); }
    }
}
