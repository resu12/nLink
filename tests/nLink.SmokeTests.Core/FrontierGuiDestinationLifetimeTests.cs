namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierGuiDestinationLifetimeTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        (from host in new[] { "powershell", "pwsh" }
         from scenario in new[] { "valid", "cancel", "deadline", "reject", "dispose" }
         select new object[] { host, scenario })
        .Concat(new[] { new object[] { "pwsh", "slow" } })
        .Concat(from host in new[] { "powershell", "pwsh" }
                from stage in new[] { "header", "body", "commit" }
                from ending in new[] { "cancel", "deadline", "dispose" }
                select new object[] { host, "connected-" + stage + "-" + ending });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProductionGui_ArmsAndJoinsOriginalDestinationLifetime(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-gui-destination-lifetime-20260930",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = """
            param([string]$RepoRoot,[string]$Directory,[string]$Case)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch]
            if($null -eq $type.GetMethod('TryStartDestinationAdoption') -or
                $null -eq $type.GetMethod('TryCompleteDestinationAdoption')) {throw 'Production destination lifetime missing'}
            $env:NLINK_TEST_GUI_DESTINATION_CASE=$Case
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalSourceParentProbe.ps1') $RepoRoot $Directory 'valid'
            $result=Get-Content -LiteralPath (Join-Path $Directory 'destination-lifetime.json') -Raw | ConvertFrom-Json
            if(-not $result.Checked -or -not $result.Joined -or $result.Case -cne $Case) {throw 'Destination lifetime checks missing'}
            'gui-destination-lifetime='+$Case
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("gui-destination-lifetime=" + scenario, result.Stdout);
    }
}
