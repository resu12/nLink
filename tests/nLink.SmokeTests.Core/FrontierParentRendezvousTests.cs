namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierParentRendezvousTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied", "delayed-parent", "repeat-gui", "gui-failure", "cancel-files", "missing-parent",
            "early-files", "early-pulled", "early-ack", "early-commit", "deadline-before", "parent-budget", "event-collision",
            "cancel-facts", "cancel-commit", "late-pull", "stale-files", "signal-failure", "gui-abandon", "repeat-waiting", "legacy-fallback" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ParentRendezvous_SchedulesOriginalOperationsWithoutGrantingAuthority(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-parent-rendezvous-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-rendezvous-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-parent-rendezvous=" + scenario, result.Stdout);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
