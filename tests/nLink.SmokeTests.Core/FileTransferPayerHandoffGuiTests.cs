namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PayerHandoff")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPayerHandoffGuiTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PayerHandoffGui_MatrixRequiresHelperThenHelpeeWithPayloadBeforeBothFaults(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function Read-ScriptAst([string]$Name) {
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot ('tools/' + $Name)), [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
                return $ast
            }
            $gui = Read-ScriptAst 'GuiSmoke-Windows.ps1'
            $wrapper = Read-ScriptAst 'Run-FileTransferTunaGuiSmoke.ps1'
            $acceptance = Read-ScriptAst 'Run-FileTransferRouteAcceptance.ps1'
            foreach ($name in @('New-Phase4RouteAcceptanceScenario', 'Get-Phase5RouteAcceptanceScenarios', 'Test-Phase5ScenarioRequiresIsolation')) {
                $definition = $acceptance.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                Invoke-Expression $definition.Extent.Text
            }
            $scenarios = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest ([pscustomobject]@{Scenarios=@{}}))
            $scenario = @($scenarios | Where-Object Name -eq 'helper-to-helpee-payer-handoff-128mb')
            if ($scenarios.Count -ne 7 -or $scenario.Count -ne 1) { throw 'handoff matrix row missing or duplicated' }
            $scenario = $scenario[0]
            if ($scenario.PayerMode -cne 'helper-to-helpee' -or $scenario.RouteMode -cne 'live-multi-toggle' -or
                $scenario.LiveToggleSequence -cne 'off,on,off' -or $scenario.Fault -cne 'switch-off' -or
                $scenario.LiveProofMode -cne 'MultiToggle' -or $scenario.PayloadBytes -ne 134217728 -or
                ($scenario.ExpectedRouteChanges -join ',') -cne 'file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6' -or
                $null -ne $scenario.Baseline) { throw 'handoff contract changed' }
            if (-not (Test-Phase5ScenarioRequiresIsolation -Scenario $scenario)) { throw 'handoff isolation preflight missing' }
            foreach ($name in @('Get-TunaGuiLiveTogglePayer', 'Get-TunaGuiFaultTarget', 'Unlock-TunaPayers')) {
                $definition = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                Invoke-Expression $definition.Extent.Text
            }
            $roles = @(0..2 | ForEach-Object { Get-TunaGuiLiveTogglePayer -PayerMode $scenario.PayerMode -StepIndex $_ })
            if (($roles -join ',') -cne 'helper,helpee,helpee') { throw 'toggle role sequence changed' }
            foreach ($mode in @('helpee', 'helper', 'both')) {
                if ((Get-TunaGuiLiveTogglePayer -PayerMode $mode -StepIndex 2) -cne $mode) { throw 'existing payer changed' }
            }
            if ((Get-TunaGuiFaultTarget -PayerMode $roles[0]) -cne 'helper' -or
                (Get-TunaGuiFaultTarget -PayerMode $roles[2]) -cne 'helpee') { throw 'fault targets changed' }
            $script:unlocks = [Collections.Generic.List[string]]::new()
            function Unlock-TunaFromSessionHeader { param($Process, $Window, $Password, $RoleLabel, $WaitForRuntimeEvidence) $script:unlocks.Add($RoleLabel) }
            $context = [pscustomobject]@{HelpeeProc=1; HelpeeWindow=2; HelperProc=3; HelperWindow=4}
            Unlock-TunaPayers -Context $context -PayerMode $scenario.PayerMode -Password 'fixture'
            Unlock-TunaPayers -Context $context -PayerMode $roles[1] -Password 'fixture'
            if (($script:unlocks -join ',') -cne 'helper,helpee') { throw 'wrong endpoint unlocked' }
            $cycle = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true)
            $body = $cycle.Extent.Text
            foreach ($required in @(
                'Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $stepPayerMode',
                'Unlock-TunaPayers -Context $Context -PayerMode $nextPayerMode',
                'Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $stepPayerMode',
                "`$RouteMode -eq 'live-multi-toggle' -and `$PayerMode -ne 'helper-to-helpee'",
                'Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $progressBookmark -MinimumTotalPayloadBytes 1048576L',
                'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark')) {
                if (-not $body.Contains($required)) { throw ('handoff action or payload gate missing: ' + $required) }
            }
            if ($wrapper.ParamBlock.Extent.Text -notmatch '"helper-to-helpee"') { throw 'wrapper cannot select handoff' }
            $guard = @($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.Contains('Helper-to-helpee payer handoff requires')}, $true))
            if ($guard.Count -ne 1) { throw 'wrapper handoff guard missing' }
            $PayerMode = 'helper-to-helpee'; $RouteMode = 'live-multi-toggle'; $Fault = 'switch-off'; $LiveToggleSequence = 'off,on,off'
            Invoke-Expression $guard[0].Extent.Text
            $RouteMode = 'preactivated'; $rejected = $false
            try { Invoke-Expression $guard[0].Extent.Text } catch { $rejected = $true }
            if (-not $rejected) { throw 'unsupported handoff mode admitted' }
            'PASS'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("PASS", result.Stdout, StringComparison.Ordinal);
    }
}

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PayerHandoff")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPayerHandoffAcceptanceTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    public async Task PayerHandoffGui_FakeCampaignChecksTheNewRouteCycleWithoutLiveRuntimeClaims()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var artifacts = Path.Combine(root, "artifacts", "payer-handoff-20261009", "fake", Guid.NewGuid().ToString("N"));
        var result = await RunPowerShellFileAsync(root, Path.Combine(root, "tools", "Run-FileTransferRouteAcceptance.ps1"),
            ["-MatrixMode", "phase5-analyzer-gui-acceptance", "-ScenarioName", "helper-to-helpee-payer-handoff-128mb",
             "-ArtifactRoot", artifacts, "-TimeoutSeconds", "30", "-ProgressTimeoutSeconds", "30",
             "-GoodputOnlyRerunLimit", "0", "-SetupOnlyRerunLimit", "0"], BuildFakeRouteAcceptanceEnvironment("phase5-pass"));
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var summary = ReadArtifactReport(Path.Combine(artifacts, "phase5-pass"), "phase5-analyzer-gui-acceptance-summary.txt");
        Assert.Equal("PASS", summary["correctness_verdict"]);
        Assert.Equal("1", summary["run_count"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6",
            summary["helper-to-helpee-payer-handoff-128mb.route_sequence"]);
        Assert.Equal("pass", summary["helper-to-helpee-payer-handoff-128mb.live_route_epoch_proof_verdict"]);
    }
}
