namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPrimaryReportsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryReports_PreserveTextWhenOptionalCaptureFails(string host)
        => await RunAsync(host, "FrontierPrimaryReports", "frontier-primary-reports=14");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryReports_ActualParentsPreserveReportsReceiptsAndWatchdogs(string host)
        => await RunAsync(host, "LocalRetainedWriterParents", "retained-writer-binding=");

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("powershell", "persist-valid")]
    [InlineData("pwsh", "persist-valid")]
    [InlineData("powershell", "launch-bind-failure")]
    [InlineData("pwsh", "launch-bind-failure")]
    [InlineData("powershell", "launch-capture-open-failure")]
    [InlineData("pwsh", "launch-capture-open-failure")]
    [InlineData("powershell", "launch-guards")]
    [InlineData("pwsh", "launch-guards")]
    [InlineData("powershell", "launch-start-failure")]
    [InlineData("pwsh", "launch-start-failure")]
    public async Task PrimaryReports_OriginalLaunchRetainsTerminalAndPersistenceBinding(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-reports-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-join-pipes-terminal-reports-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(scenario.StartsWith("launch-", StringComparison.Ordinal) ?
            "frontier-primary-report-launch=" + scenario[7..] : "frontier-terminal-receipt=" + scenario, result.Stdout);
    }

    private static async Task RunAsync(string host, string support, string expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-primary-reports-20260919", host + "-" + support + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        if (support == "LocalRetainedWriterParents")
            File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"), Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        var path = Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1");
        var script = "param([string]$Root,[string]$Output)\n& '" + path.Replace("'", "''") + "' -RepoRoot $Root -Directory $Output" +
            (support == "LocalRetainedWriterParents" ? " -FrontierReports" : "");
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(expected, result.Stdout);
    }
}
