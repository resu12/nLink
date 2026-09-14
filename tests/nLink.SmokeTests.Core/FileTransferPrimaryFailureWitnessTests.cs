namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPrimaryFailureWitnessTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryFailureWitness_UsesExactPrimarySelectionAndPreservesReports(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-failure-witness-20260914", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "PrimaryFailureWitness.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("primary-failure-witness=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith("primary-failure-witness=", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line["primary-failure-witness=".Length..]);
    }
}
