namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPrimaryWorkerTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied-inputs", "unbound", "self-worker", "preopened", "prepare-budget", "repeat-prepare", "missing-offer",
            "cancel-before", "file-binding", "file-state", "file-tail", "facts-body", "facts-binding", "facts-length", "facts-padding",
            "native-handle", "partial-pull", "wrong-request", "wrong-run", "wrong-path", "worker-incarnation", "parent-incarnation",
            "post-scope", "post-facts", "post-parent-pin", "parent-cancel", "worker-dispose", "inputs-dispose", "copied-worker",
            "copied-worker-inputs", "attach-replay", "worker-budget", "post-worker-pin", "parent-read-after-offer", "existing-publication", "native-topology" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryWorker_RequiresExactNativeRequestAndRetainedParentFiles(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-worker-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-worker=", result.Stdout);
    }
}
