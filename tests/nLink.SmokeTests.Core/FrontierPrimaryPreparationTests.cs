namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryPreparationTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from owner in new[] { "Wrapper", "RouteAcceptance" }
        from scenario in new[] { "valid", "foreign-arguments", "already-hashed", "disposed-parent", "pin-failure", "budget", "replay", "copied-result", "independent-lifetime", "worker-reports", "legacy-isolation" }
        select new object[] { host, owner, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryPreparation_BindsOriginalSourcePassesAndOwnsRequest(string host, string owner, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-preparation-20260919", host + "-" + owner + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-prepare-" + owner + "-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-preparation=" + owner + "/" + scenario, result.Stdout);
        if (scenario is "worker-reports" or "legacy-isolation") { Assert.Contains("frontier-terminal-persistence=valid", result.Stdout); }
        else { Assert.DoesNotContain("frontier-primary-worker=", result.Stdout); }
    }
}
