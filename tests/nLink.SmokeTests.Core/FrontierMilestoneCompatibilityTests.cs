namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierMilestoneCompatibilityTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "absent", "ignored", "duplicate", "append", "reorder", "encoding-only", "receipt-failure", "ledger-missing",
            "hash-failure", "merge-write-failure", "replaced-reference" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualMerge_PreservesPostMergePrimaryInputAndNativeOriginsOnlyWhenExact(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-milestone-compatibility-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierWrapperHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "merge-" + scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-milestone-compatibility=" + scenario, result.Stdout);
    }
}
