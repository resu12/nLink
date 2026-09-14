namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalRetainedWriterBindingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task RetainedWriterBinding_RequiresAcceptedGuiAndExactSourceAndWriterBytes(string host)
        => await RunAsync(host, "LocalRetainedWriterBinding");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task RetainedWriterBinding_ActualParentsPreserveReportsReceiptsAndWatchdogs(string host)
        => await RunAsync(host, "LocalRetainedWriterParents");

    private static async Task RunAsync(string host, string support)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-retained-writer-20260914", support + "-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        if (support == "LocalRetainedWriterParents")
        {
            File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"), Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        }
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        const string prefix = "retained-writer-binding=";
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal), $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
