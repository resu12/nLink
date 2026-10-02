namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class NativePollingContractReviewTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task StandalonePollingReviewPreservesInterruptionCounterexamples(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var output = Path.Combine(root, "artifacts", "native-polling-contract-20261001", "polling-" + host + ".json");
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/NativePollingContractObservation.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, output], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-polling-contract=diagnostic-only", result.Stdout);
    }
}
