namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryEvaluationTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "actual-evaluations", "pre-eof", "wrong-ordinal", "equal-line", "compact-not-full", "origin-order",
            "record-cap", "null-context", "mutated-full", "mutated-compact", "wrong-array", "record-order", "repeat-full", "missing-view",
            "budget", "record-budget", "repeat-begin", "early-take", "wrong-context", "wrong-view", "repeat-take", "post-mutation",
            "source-close", "custody-dispose", "post-scope", "post-facts", "parent-cancel" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryEvaluations_BindExactNativeArraysViewsAndInvocations(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-evaluations-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-evaluations-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-evaluations=" + scenario, result.Stdout);
    }
}
