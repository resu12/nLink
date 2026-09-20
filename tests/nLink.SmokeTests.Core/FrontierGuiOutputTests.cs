namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGuiOutputTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "empty", "copied", "missing-capture", "no-stdout", "no-stderr",
            "already-started", "repeat-start", "after-seal", "capture-fault", "nonzero", "early-eof",
            "held-stdout", "held-stderr", "cancel-after", "owner-disposed", "start-failure", "sink-open-failure" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalOutput_RequiresOwnedPipesAndSuccessfulCaptureThroughBothEofs(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-output-ownership-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, "output", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
