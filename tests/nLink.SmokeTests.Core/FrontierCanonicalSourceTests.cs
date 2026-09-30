namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierCanonicalSourceTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "size", "open-writer", "cancel", "closed", "dispose", "default" }
        select new object[] { host, scenario };

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesProtectedSourceWithoutTestAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource' -as [type]
            if($null -eq $type -or $null -eq [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch].GetMethod('TryHoldSource')) {
                throw 'Production protected source missing'
            }
            if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
                $null -ne $type.GetMethod('TryBindClosedWritersForTest') -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch].GetMethod('TryHoldSourceForTest')) {
                throw 'Source API is mutable or exposes test custody'
            }
            'canonical-source-controls-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-source-controls-ok", result.Stdout);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProductionSource_UsesOriginalPreSendHashAndRetiresCustody(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-canonical-source-20260929",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCanonicalSourceParentProbe.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-source=" + scenario, result.Stdout);
    }
}
