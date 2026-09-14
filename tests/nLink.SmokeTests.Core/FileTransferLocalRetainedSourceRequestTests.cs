namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalRetainedSourceRequestTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalRetainedSourceRequest_BindsExactWorkerAndFailsClosed(string host)
        => await RunAsync(host, "primitive");

    [Fact]
    public async Task LocalRetainedSourceRequest_ActualPrimaryPreservesReportsAndEvaluationCount()
        => await RunAsync("powershell", "primary");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalRetainedSourceRequest_ActualParentsPreserveReceiptsAndDeadlines(string host)
        => await RunAsync(host, "parents");

    private static async Task RunAsync(string host, string suite)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-source-request-20260914", suite + "-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        if (suite == "parents")
        {
            File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"),
                Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        }
        var support = suite == "parents" ? "LocalCarrierHandoff" : "LocalRetainedSourceRequest";
        var prefix = suite == "parents" ? "local-carrier-handoff=" : "local-retained-source-request=";
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, suite == "parents" ? "source-request" : suite], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal),
            $"{host}/{suite}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
