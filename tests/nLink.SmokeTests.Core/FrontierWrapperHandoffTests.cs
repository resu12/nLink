namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierWrapperHandoffTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "positive", "gui-failure", "stage-failure", "output-failure", "state-failure",
            "assigned-output", "incompatible-output", "coerced-output", "assigned-completion", "incompatible-completion" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualWrapper_PreservesOriginalOwnershipUntilTheLaterPrimary(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-wrapper-handoff-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierWrapperHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-wrapper-handoff=" + scenario, result.Stdout);
        if (scenario is "valid" or "positive")
        {
            Assert.Contains("frontier-prepared-source-guards=11", result.Stdout);
            Assert.Contains("frontier-retained-parent=Wrapper/prepared", result.Stdout);
        }
    }
}
