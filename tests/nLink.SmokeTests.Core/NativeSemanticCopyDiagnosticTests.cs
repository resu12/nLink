namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class NativeSemanticCopyDiagnosticTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "profile-valid")]
    [InlineData("pwsh", "profile-valid")]
    [InlineData("powershell", "profile-budget")]
    [InlineData("pwsh", "profile-budget")]
    public async Task OriginalCopyDiagnosticsPreserveSuccessAndCumulativeExpiry(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-semantic-copy-rca-20261001", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-copy-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }
}
