namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierRelayTransferTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "guards", "wrapper-nonzero", "no-take", "handoff-cancel", "request-dispose", "registration-dispose", "body-mismatch", "analysis", "analysis-changed", "map-collision", "event-collision", "transfer-tamper", "pull-budget" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeRelayTransfer_PreservesHeldEvidenceAndRequiresOriginalSuccessfulTake(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-relay-transfer-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierRelayTransfer.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-relay-transfer=" + scenario, result.Stdout);
    }
}
