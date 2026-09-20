namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierAppReportsTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "default", "empty", "unicode-framing", "large", "collision-stdout", "collision-stderr",
            "bind-failure", "budget-before", "owner-disposed", "cancel-after", "native-write-failure", "native-seal-failure",
            "ordinary-write-failure", "setup-failure", "start-failure", "guards", "replay", "cross-api", "nonzero", "early-eof", "held-pipe", "two-apps",
            "native-ready", "ready-collision-ready", "ready-collision-failed" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalAppReports_PreserveSingleLaunchAndLiveLinesAcrossOptionalFailures(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-app-reports-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppReports.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-reports=" + scenario, result.Stdout);
    }
}
