namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierRetainedParentTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> PollCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "parent-first", "copied", "repeat-wait", "forged-offer", "request-cancel", "payload-tamper", "parent-budget" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(PollCases))]
    public async Task NativeOfferPolling_PreservesOneShotIdentityPacketAndBudgets(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-retained-parent-integration-20260919", host + "-poll-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-start-rendezvous-poll-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-diagnostic-rendezvous=Wrapper/" + scenario, result.Stdout);
        if (scenario is "parent-first" or "copied" or "repeat-wait")
            Assert.Contains("frontier-terminal-persistence=valid", result.Stdout);
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from owner in new[] { "Wrapper", "RouteAcceptance" }
        from scenario in new[] { "valid", "delayed-offer", "diagnostic-rejected", "report-failure", "timeout", "persistence-collision", "receipt-write-failure" }
        select new object[] { host, owner, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeParents_PreserveWatchdogsReportsAndExactTerminalReceipt(string host, string owner, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-retained-parent-integration-20260919", host + "-" + owner + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-parent-" + owner + "-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-retained-parent=" + owner + "/" + scenario, result.Stdout);
    }
}
