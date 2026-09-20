namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierCursorMatchTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CursorProcessor_CapturesExactLfIndicesAndImmutablePrefix(string host)
        => await RunAsync(host, "primitive", true);

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "prefix", "empty", "wrong-bookmark", "missing-final", "generic-reader", "changed-index",
            "missing-writer", "missing-recovery", "after-finish", "repeat-join", "copied-result", "copied-match", "copied-binding",
            "mutable-result", "closed-output", "cancel", "complete", "repeat-process", "missing-initial", "budget" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CursorMatch_RequiresOriginalObservationsAndExactProcessor(string host, string scenario)
        => await RunAsync(host, scenario, false);

    private async Task RunAsync(string host, string scenario, bool primitive)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-cursor-match-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support",
            primitive ? "FrontierCursorProcessor.ps1" : "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, primitive ? [root, directory] :
            [root, directory, "cursor-join-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(primitive ? "frontier-cursor-processor=" : "frontier-gui-launch=", result.Stdout);
    }
}
