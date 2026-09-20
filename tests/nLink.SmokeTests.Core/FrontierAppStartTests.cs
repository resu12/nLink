using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierAppStartTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "ready", "delayed", "forged-ready", "both-signals", "cancel-ready", "cancel-wait", "dispose-wait",
            "wrong-request", "wrong-run", "wrong-scenario", "wrong-parent", "wrong-app", "app-incarnation", "timeout", "parent-death",
            "selector-off", "selector-case", "bad-id", "collision-ready", "collision-failed", "dispose-before", "dispose-race", "core-ready", "core-delayed" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeAppReadiness_WaitsWithoutAuthorityAndPreservesExactAttachment(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-app-start-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppStart.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-start=" + scenario, result.Stdout);
        if (scenario == "parent-death")
        {
            var path = Path.Combine(directory, "app-result.json");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) { await Task.Delay(50); }
            Assert.True(File.Exists(path), "App did not finish after native GUI parent death");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(json.RootElement.GetProperty("Rejected").GetBoolean());
            Assert.True(json.RootElement.GetProperty("ParentDeathWithoutCancellation").GetBoolean());
        }
    }
}
