namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGenuineSubmissionTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "copied", "two_payloads", "both_positive", "missing_marker", "early_submit",
            "payload_fault", "catalog_cancel", "late_write", "repeat_submit", "after_submit_cancel", "after_submit_write", "after_complete_write" }
        select new object[] { host, "genuine_" + scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GenuineSubmission_OriginalProducerWriterAndNativeAppIssueExactFiles(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-genuine-submission-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, "files", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
