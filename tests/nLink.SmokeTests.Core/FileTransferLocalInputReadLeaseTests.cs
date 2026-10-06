namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferLocalInputReadLeaseTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalInputReadLease_ReadOnlyIdentityAndVerificationFailClosed(string host)
        => await RunAsync(host, "LocalInputReadLease", "local-input-read-lease=", false);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalInputReadLease_RetainedParentsPreservePrimaryReadersAndReceipts(string host)
        => await RunAsync(host, "LocalCarrierHandoff", "local-carrier-handoff=", true);

    private static async Task RunAsync(string host, string support, string prefix, bool lifecycle)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-read-inputs-20260914", support + "-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        if (lifecycle)
        {
            File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"),
                Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        }
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "read-lease"], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
