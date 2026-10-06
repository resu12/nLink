namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierCanonicalRolesTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "unconsumed", "copied-attempt", "parent-cancelled",
            "helpee-report-failure", "helper-report-failure" }
        select new object[] { host, scenario };

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesCanonicalRoleChecksOnly(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch]
            foreach($name in @('TryCreateRoleRequest','StartRoleApp','MatchesRoleLaunch')) {
                if($null -eq $type.GetMethod($name)) {throw 'Production canonical role binding missing'}
            }
            if($null -ne $type.GetMethod('MatchesRoleLaunchForTest') -or
                $null -ne $type.GetMethod('TryHoldSourceForTest') -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('StartAttemptRoleAppForTest')) {throw 'Test-only role/source API leaked'}
            'canonical-roles-controls-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-roles-controls-ok", result.Stdout);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProductionRoles_PreserveOriginalLaunchesAndCleanup(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-canonical-roles-20260929",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCanonicalRolesParentProbe.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-roles=" + scenario, result.Stdout);
    }
}
