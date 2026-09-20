namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGuiLedgerEntryTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "default", "selector-invalid", "malformed-id", "wrong-scope", "missing-scope",
            "collision-ready", "existing-native", "existing-legacy", "budget", "postcreate-failure", "cancel-after-create",
            "replay", "wrong-path", "cleanup-failure", "invalid-output" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalGuiEntry_CreatesLedgerBeforeAppsWithoutPromotingDiagnostics(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-gui-ledger-entry-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLedgerEntry.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-ledger-entry=" + scenario, result.Stdout);
    }
}
