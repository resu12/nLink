namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierTerminalReceiptTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalOutput_MatchesOriginalHostEncoding(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-terminal-receipt-20260917", host + "-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierTerminalEncoding.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-terminal-encoding=7", result.Stdout);
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "proof-incomplete", "copied", "stdout-changed", "stderr-changed", "catalog-changed", "source-mismatch",
            "pid-mismatch", "route-mismatch", "no-hook", "write-fails", "dictionary-mutation", "return-copy", "return-mutation", "repeat-take",
            "repeat-begin", "parent-cancel", "pin-after-take", "budget", "preexisting", "forged-custody", "return-property", "source-reader", "foreign-source" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TerminalReceipt_BindsOriginalWriterAndExactReturnedReceipt(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-terminal-receipt-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-join-pipes-terminal-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-terminal-receipt=" + scenario, result.Stdout);
    }
}
