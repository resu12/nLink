namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPrimaryStartTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from owner in new[] { "Wrapper", "RouteAcceptance" }
        from scenario in new[] { "auto", "auto-terminal", "delayed", "forged-ready", "both-signals", "cancel", "timeout", "wrong-parent", "wrong-request",
            "capture-failure", "receiver-failure", "bind-failure", "handoff-failure", "collision-ready", "collision-failed", "guards", "start-failure", "replay", "legacy-selector", "cancelled-ready", "parent-exit" }
        where owner == "Wrapper" || scenario is "auto" or "auto-terminal"
        select new object[] { host, owner, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryStart_WaitsForNativeHandoffWithoutReplacingAttachmentAuthority(string host, string owner, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-start-20260919", host + "-" + owner + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-prepare-" + owner + "-start-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-start=" + owner + "/" + scenario, result.Stdout);
        if (scenario == "auto-terminal") { Assert.Contains("frontier-terminal-persistence=valid", result.Stdout); }
        if (scenario == "parent-exit")
        {
            var path = Path.Combine(directory, "worker-after-parent-exit.json");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) { await Task.Delay(50); }
            Assert.True(File.Exists(path), "Readiness worker did not finish after its actual parent exited");
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.GetProperty("Attached").GetBoolean());
            Assert.True(json.RootElement.GetProperty("RetryRejected").GetBoolean());
            Assert.InRange(json.RootElement.GetProperty("Milliseconds").GetInt64(), 200, 10000);
        }
    }
}
