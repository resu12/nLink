namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierRecoveryBufferTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "native", "no-markers", "foreign", "wrapped", "copied-source", "copied-recovery",
            "line-owner", "line-cancel", "line-budget", "line-stopped", "line-source", "line-reader",
            "line-end-owner", "line-end-cancel", "line-end-budget" }
            .Concat(from boundary in new[] { "before-read", "after-read", "buffer-complete", "eof", "before-return" }
                from mutation in new[] { "owner", "cancel", "budget", "reader" }
                select boundary + "-" + mutation)
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RecoveryBuffer_RejectsInvalidWorkBeforeFactsEscape(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "recovery-buffer-20260927", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "markers-buffered-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
        Assert.Contains("recovery-buffer=" + scenario, await File.ReadAllTextAsync(Path.Combine(directory, "recovery-buffer-probe.log")));
    }
}
