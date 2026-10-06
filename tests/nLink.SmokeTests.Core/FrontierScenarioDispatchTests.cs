namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierScenarioDispatchTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ScenarioRunner_ForwardsPrivateAttemptWithoutChangingOrdinaryActions(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
            Invoke-Expression (Get-AttemptFunction $ast 'Invoke-Scenario')
            if(-not (Get-Command Invoke-Scenario).Parameters.ContainsKey('CanonicalAttempt')) {throw 'Private scenario forwarding missing'}
            $script:GuiSmokeScenarioDeadlineUtc=[DateTimeOffset]::MinValue
            $script:GuiSmokeScenarioDeadlineName='outer'
            $script:GuiSmokeTerminalPhaseGeneration=7L
            $script:ordinaryCalls=0; $script:ownedCalls=0; $script:deadlineCalls=0
            function Assert-PrivateDispatch {
                if($null -ne (Get-Variable CanonicalAttempt -ErrorAction SilentlyContinue) -or
                    $null -ne (Get-Variable original -ErrorAction SilentlyContinue)) {throw 'Runner capability leaked'}
            }
            function Assert-GuiSmokeScenarioDeadline {
                Assert-PrivateDispatch
                $script:deadlineCalls++
            }
            function Assert-Restored {
                if($script:GuiSmokeScenarioDeadlineUtc -ne [DateTimeOffset]::MinValue -or
                    $script:GuiSmokeScenarioDeadlineName -cne 'outer' -or
                    $script:GuiSmokeTerminalPhaseGeneration -ne 7L) {throw 'Runner deadline not restored'}
            }
            $ordinary={
                if($args.Count -ne 0 -or $script:GuiSmokeScenarioDeadlineName -cne 'ordinary' -or
                    $script:GuiSmokeScenarioDeadlineUtc -le [DateTimeOffset]::UtcNow -or
                    $script:GuiSmokeTerminalPhaseGeneration -ne 0L) {throw 'Ordinary action changed'}
                Assert-PrivateDispatch
                $script:ordinaryCalls++
            }
            Invoke-Scenario -Name ordinary -TimeoutSec 10 -Action $ordinary
            Assert-Restored
            Invoke-Scenario -Name ordinary -TimeoutSec 10 -CanonicalAttempt $null -Action $ordinary
            Assert-Restored
            $private:original=[object]::new()
            $script:expected=$original
            Invoke-Scenario -Name owned -TimeoutSec 10 -CanonicalAttempt $original -Action {
                param($CanonicalAttempt)
                Set-Variable -Name CanonicalAttempt -Option Private
                if($args.Count -ne 0 -or -not [object]::ReferenceEquals($CanonicalAttempt,$script:expected)) {throw 'Original attempt not forwarded'}
                Assert-PrivateDispatch
                $script:ownedCalls++
            }
            Assert-Restored
            $failure=''
            try {Invoke-Scenario -Name throws -TimeoutSec 10 -Action {throw 'original action failure'}}
            catch {$failure=$_.Exception.Message}
            Assert-Restored
            if($failure -cne 'original action failure' -or $script:ordinaryCalls -ne 2 -or
                $script:ownedCalls -ne 1 -or $script:deadlineCalls -ne 3) {throw 'Runner outcome changed'}
            'private-scenario-forwarding-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("private-scenario-forwarding-ok", result.Stdout);
    }
}
