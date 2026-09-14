namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalGuiWriterRequestTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiWriterRequest_CapabilityBoundsAndOneShotPublication(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-gui-writer-20260914", "bounds-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalGuiWriterBounds.ps1"));
        foreach (var mode in new[] { "valid", "64-writers", "65-writers", "empty-writers", "duplicate-writer", "metadata", "writer-length",
            "negative-bytes", "negative-lines", "hash", "identity", "bookmark", "source-relative", "disposed", "cancelled", "ledger-length" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [root, directory, mode], host);
            const string prefix = "gui-writer-request=";
            Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal), $"{host}/{mode}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".json"), line[prefix.Length..]);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiWriterRequest_ActualParentAndFinalizerBindCompletionWithoutChangingPrimary(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-gui-writer-20260914", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalGuiWriterRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        const string prefix = "gui-writer-request=";
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal), $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
