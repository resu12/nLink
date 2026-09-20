namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierScenarioLaunchTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "default", "cancel-before", "cancel-between", "cursor-failure",
            "request-overflow", "copied-owner", "helpee-window-failure", "helper-window-failure",
            "helpee-report-failure", "helper-report-failure" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeScenarioLaunch_PreservesOriginalRequestsCursorAndReports(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-scenario-launch-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierScenarioLaunch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-scenario-launch=" + scenario, result.Stdout);
    }
}
