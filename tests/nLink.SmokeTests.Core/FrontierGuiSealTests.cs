namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGuiSealTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "both_positive", "two_payloads", "copied", "rotated", "mutated",
            "early", "missing-output", "missing-completion", "nonzero", "held-stdout", "held-stderr",
            "capture-fault", "cancel-after", "budget", "repeat", "writer-open" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FinalSeal_OriginalGuiJoinsRealWriterFilesExitAndOutputBeforeSourceVerification(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-final-source-seal-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
