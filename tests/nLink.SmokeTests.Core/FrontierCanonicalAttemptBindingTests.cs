namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierCanonicalAttemptBindingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_BindsOnlyOriginalCanonicalArguments(string host)
        => await RunProbe(host, "controls", "FrontierCanonicalAttemptBindingProbe.ps1");

    [Theory]
    [InlineData("powershell", "enabled")]
    [InlineData("pwsh", "enabled")]
    [InlineData("powershell", "disabled")]
    [InlineData("pwsh", "disabled")]
    [InlineData("powershell", "wrong-size")]
    [InlineData("pwsh", "wrong-size")]
    [InlineData("powershell", "second-attempt")]
    [InlineData("pwsh", "second-attempt")]
    public async Task ActualParentLaunch_BindsOptInArgumentsWithoutGuiPromotion(string host, string scenario)
        => await RunProbe(host, scenario, "FrontierCanonicalAttemptLaunchProbe.ps1");

    private async Task RunProbe(string host, string scenario, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-attempt-binding-20260929",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", name));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-attempt-binding=" + scenario, result.Stdout);
    }
}

