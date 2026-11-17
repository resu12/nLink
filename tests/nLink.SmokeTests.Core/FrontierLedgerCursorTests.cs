namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierLedgerCursorTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> CursorCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "chunk", "exact-lines", "over-lines", "shrink", "position", "closed", "budget",
            "copied-origin", "copied-observation", "repeat-initial", "no-app", "two-apps", "exited-app", "cancel", "dispose", "complete" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(CursorCases))]
    public async Task LedgerCursor_IndexesOriginalIncrementalBytes(string host, string scenario)
        => await RunAsync(host, scenario, "ledger-cursor");

    public static IEnumerable<object[]> FinalCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "empty-initial", "missing-initial", "repeat-final", "before-work", "source-started",
            "prefix-rewrite", "partial-tail", "late-sync", "copied-binding", "copied-final", "closed-origin", "after-finish", "budget-final" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(FinalCases))]
    public async Task LedgerCursor_FinalObservationMatchesOriginalSealedSource(string host, string scenario)
        => await RunAsync(host, "cursor-" + scenario, "seal");

    private async Task RunAsync(string host, string scenario, string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-ledger-cursor-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, mode, typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
