namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferNativeWriterCaptureTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeWriterCapture_UsesOriginalWritesAndRejectsForgedOrUnboundedMetadata(string host)
        => await RunAsync(host, "NativeWriterCapture");

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    public async Task NativeWriterCapture_FinalizerPreservesPrimaryValidationAndOmission(string host, bool captureEntryMissing)
        => await RunAsync(host, "NativeWriterFinalizer", captureEntryMissing);

    private static async Task RunAsync(string host, string support, bool captureEntryMissing = false)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-native-writer-20260914", support + "-" + host + (captureEntryMissing ? "-missing-entry" : "") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, captureEntryMissing ? "missing-entry" : "current"], host);
        const string prefix = "native-writer-capture=";
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal),
            $"{host}/{support}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
