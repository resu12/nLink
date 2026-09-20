namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierDiagnosticOutputTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DiagnosticOutput_EnforcesWireAndTextBounds(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierDiagnosticOutputBounds.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-diagnostic-output-bounds=verified", result.Stdout);
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied-capabilities", "missing-receiver", "repeat-offer", "repeat-adopt", "repeat-commit",
            "repeat-take", "preexit-take", "mutation-before-commit", "header-length", "payload-tamper", "packet-mismatch", "secret-tamper",
            "state-only", "worker-failure", "no-commit", "escrow-pin", "parent-budget", "worker-budget", "parent-cancel", "cancel-after-take",
            "copy-budget", "repeat-prepare", "no-offer", "pin-after-take" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DiagnosticOutput_AdoptsBeforeExitAndConsumesExactTransportOnce(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-diagnostic-output-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-join-output-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-diagnostic-output=" + scenario, result.Stdout);
    }
}
