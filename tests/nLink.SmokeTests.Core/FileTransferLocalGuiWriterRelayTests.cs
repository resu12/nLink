namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferLocalGuiWriterRelayTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> FrontierIsolationCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "wrapper-nonzero", "output-fails", "host-output-fails" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(FrontierIsolationCases))]
    public async Task GuiWriterRelay_ScalarCompletionDoesNotAcquireNativeCustody(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-route-relay-contract-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory,[string]$Scenario)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRelay.ps1') `
                -RepoRoot $RepoRoot -Directory $Directory -CaseSelection @($Scenario) -RequireFrontierIsolation
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-relay-isolation-checks=" + (scenario == "valid" ? 3 : 2), result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiWriterRelay_OutputFailureDoesNotReuseUnrelatedNativeExitCode(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-route-relay-contract-20260920", host + "-stale-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $global:LASTEXITCODE=37
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRelay.ps1') `
                -RepoRoot $RepoRoot -Directory $Directory -CaseSelection @('output-fails') -RequireFrontierIsolation
            if($global:LASTEXITCODE -ne 37) { throw 'Relay changed unrelated native exit state' }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-relay-isolation-checks=2", result.Stdout);
    }

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
