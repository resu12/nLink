namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierEvidencePersistenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task EvidencePersistence_BoundedNativeWriteAndRename(string host)
        => await RunAsync(host, "primitive", true);

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "empty-semantic", "early", "before-copy", "missing-copy", "active-work", "copied-binding", "copied-evidence",
            "mutable-result", "collision", "directory", "closed-output", "closed-copy", "closed-evidence", "repeat", "cancel", "complete", "budget", "copy-dispose", "over-bound" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EvidencePersistence_RequiresOriginalCopyAndPersistence(string host, string scenario)
        => await RunAsync(host, scenario, false);

    private async Task RunAsync(string host, string scenario, bool primitive)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-evidence-persistence-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support",
            primitive ? "FrontierEvidencePersistence.ps1" : "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, primitive ? [root, directory] :
            [root, directory, "cursor-persist-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(primitive ? "frontier-evidence-persistence=" : "frontier-gui-launch=", result.Stdout);
    }
}
