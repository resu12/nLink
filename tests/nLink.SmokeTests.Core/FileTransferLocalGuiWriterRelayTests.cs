namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalGuiWriterRelayTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiWriterRelay_LegacyChildResolvesHashWithoutChangingParentEnvironment(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-relay-module-path-20260915", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRelay.ps1') `
                -RepoRoot $RepoRoot -Directory $Directory -CaseSelection @('baseline','valid','wrapper-nonzero') -RequireHashCmdlet
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host,
            initializeHostModulePath: true);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("gui-writer-relay=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiWriterRelay_ActualLaunchPreservesScopeIdentityAndFailures(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-gui-relay-20260914", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalGuiWriterRelay.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        const string prefix = "gui-writer-relay=";
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal), $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
