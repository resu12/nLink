namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferTerminalRetentionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public Task TerminalRetention_PreservesExactDiagnosticRecordsWithoutAuthority(string host)
        => RunAsync(host, "TerminalDiagnosticRetention");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public Task TerminalIntegrity_StatusInferenceCannotSupplyFileIntegrity(string host)
        => RunAsync(host, "TerminalIntegrityInference");

    private static async Task RunAsync(string host, string support)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "terminal-retention-fix-20260925",
            support + "-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support + ".ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), result.Stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("terminal-evidence=pass", result.Stdout, StringComparison.Ordinal);
    }
}
