namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryOutputTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied", "repeat-start", "repeat-join", "join-dispose", "parent-cancel", "stdout-pin", "after-pin",
            "scope", "transport-dispose", "held-stdout", "held-stderr", "capture-fault", "no-stdout", "no-stderr", "start-failure", "nonzero-exit" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryOutput_JoinsOnlyOriginalCapturedEofAndTransport(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-output-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-join-pipes-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-output=" + scenario, result.Stdout);
    }
}
