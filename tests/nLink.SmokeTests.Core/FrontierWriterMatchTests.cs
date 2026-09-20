namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierWriterMatchTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied-binding", "copied-result", "copied-match", "foreign-result", "legacy-result",
            "mutable-result", "extra-writer", "substituted-contribution", "terminal-sequence", "repeat", "after-finish",
            "closed-output", "cancel", "complete", "dispose", "snapshot-bounds" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WriterMatch_JoinsExactOriginalSourceSnapshotAndNativeContributions(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-writer-match-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "matching-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
