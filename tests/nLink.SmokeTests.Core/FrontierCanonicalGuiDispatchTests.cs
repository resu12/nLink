namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierCanonicalGuiDispatchTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "enabled", "disabled", "wrong-size", "second-attempt",
            "wrapper-environment", "gui-environment", "wrong-host", "gui-nonzero",
            "copied-ticket", "unconsumed-ticket", "retired-ticket", "parent-cancelled" }
        select new object[] { host, scenario };

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesSharedDispatchOnly(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch' -as [type]
            if($null -eq $type -or $null -eq [NLink.Tools.LocalGuiWriterRequest+Relay].GetMethod('TryForwardCanonicalAttempt') -or
                $null -eq [NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner].GetMethod('TryOpenCanonicalAttemptDispatch') -or
                $null -eq $type.GetMethod('TryTakeScenarioOwner')) {
                throw 'Production GUI attempt dispatch missing'
            }
            if($null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('TryBindAttemptForTest') -or
                $null -ne $type.GetMethod('MatchesRoleLaunchForTest') -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+Relay].GetMethod('TryForwardAttemptForTest')) {throw 'Test-only API leaked'}
            'canonical-gui-controls-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-gui-controls-ok", result.Stdout);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProductionChain_ConsumesOnlyOriginalAttemptWithoutLivePromotion(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-attempt-gui-dispatch-20260929",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCanonicalGuiParentProbe.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-gui-dispatch=" + scenario, result.Stdout);
    }
}
