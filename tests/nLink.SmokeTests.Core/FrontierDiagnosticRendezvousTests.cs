namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierDiagnosticRendezvousTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from owner in new[] { "Wrapper", "RouteAcceptance" }
        from scenario in new[] { "valid", "parent-first", "offer-first", "copied", "repeat-wait", "forged-offer", "forged-adoption",
            "request-cancel", "cancel-before-commit", "offer-timeout", "adoption-timeout", "worker-exit", "parent-exit",
            "collision-offered", "collision-adopted", "payload-tamper", "mutation-before-commit", "parent-budget", "worker-budget" }
        where owner == "Wrapper" || scenario == "valid"
        select new object[] { host, owner, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DiagnosticRendezvous_OrdersNativeAdoptionAndCommitWithoutReplacingProof(string host, string owner, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-diagnostic-rendezvous-20260919", host + "-" + owner + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-prepare-" + owner + "-start-rendezvous-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-diagnostic-rendezvous=" + owner + "/" + scenario, result.Stdout);
        if (scenario is "valid" or "parent-first" or "offer-first" or "copied" or "repeat-wait")
            Assert.Contains("frontier-terminal-persistence=valid", result.Stdout);
        if (scenario == "parent-exit")
        {
            var path = Path.Combine(directory, "diagnostic-after-parent-exit.json");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) { await Task.Delay(50); }
            Assert.True(File.Exists(path), "Worker did not finish diagnostic wait after its actual parent exited");
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.GetProperty("Committed").GetBoolean());
            Assert.Equal(2, json.RootElement.GetProperty("Evaluations").GetInt32());
            Assert.True(json.RootElement.GetProperty("Unchanged").GetBoolean());
            Assert.InRange(json.RootElement.GetProperty("Milliseconds").GetInt64(), 200, 10000);
        }
    }
}
