namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierFinalizerHandoffTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "positive", "parent-cancel", "missing-parent", "parent-budget", "signal-failure",
            "stale-files", "early-commit", "gui-failure", "stage-failure" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RealFinalizer_SelectsOneCompletionAndPreservesEarlierFailureFallback(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-finalizer-handoff-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCleanupAdoption.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario == "positive" ? "positive" : "writer-only",
            "persistence-handoff-" + scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-finalizer-handoff=" + scenario, result.Stdout);
    }
}
