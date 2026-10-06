namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferLivePublicationInputTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LivePublicationInputs_NativeMetadataMatchesOriginalParser(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalLiveMembershipParser.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("live-membership-parser=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LivePublicationInputs_BoundSourceBytesAndShareOnlyActualNativeArrays(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-live-inputs-20260914", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalLiveInputCapture.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("local-live-input-capture=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout);
    }
}
