namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryPayloadTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "two-payloads", "both-writers", "foreign-identities", "pre-take", "pre-mutation", "hash-mismatch",
            "partial-hash", "pin-close", "pin-swap", "budget", "record-budget", "repeat", "post-mutation", "source-dispose", "custody-dispose",
            "set-dispose", "worker-dispose", "inputs-dispose", "post-scope", "post-facts", "post-parent-pin", "parent-cancel" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryPayloads_BindOriginalNativeBytesToEvaluationPair(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-payloads-20260917", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-payloads-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-payloads=" + scenario, result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryPayloads_StrictDecodeEnforcesByteHashAndUtf8Bounds(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierPrimaryPayloadDecode.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-payload-decode=10", result.Stdout);
    }
}
