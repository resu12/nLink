namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierLedgerOriginTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> CreationCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "existing", "copied-owner", "copied-origin", "repeat", "after-request",
            "closed-handle", "position-change", "over-bound", "budget", "cancel", "complete", "dispose" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(CreationCases))]
    public async Task LedgerOrigin_OwnsActualCreationBeforeLaunch(string host, string scenario)
        => await RunAsync(host, scenario, "ledger-origin");

    public static IEnumerable<object[]> SealCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "wrong-source", "shared-budget", "closed-after-seal", "copied-binding", "after-launch" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(SealCases))]
    public async Task LedgerOrigin_JoinsOriginalCreationToAdoptedWritersAndFinalizer(string host, string scenario)
        => await RunAsync(host, "ledger-" + scenario, "seal");

    private async Task RunAsync(string host, string scenario, string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-ledger-origin-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
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
