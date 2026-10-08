namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PendingPreparation")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPendingPreparationGuiTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PendingPreparationGui_IsOptInAndRequiresTheHandoffAndNoFaultTunaDelivery(string host)
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
            foreach ($name in @('New-Phase4RouteAcceptanceScenario', 'Get-Phase5RouteAcceptanceScenarios')) {
                $definition = $acceptance.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                Invoke-Expression $definition.Extent.Text
            }
            $baseline = [pscustomobject]@{Scenarios=@{}}
            $defaults = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline)
            if ($defaults.Count -ne 6 -or @($defaults | Where-Object RouteMode -eq 'pending-preparation').Count) { throw 'default matrix expanded' }
            $scenarios = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline -IncludePendingPreparation)
            $scenario = @($scenarios | Where-Object Name -eq 'pending-preparation-tuna-v4-64mb')
            if ($scenario.Count -ne 1) { throw 'target scenario missing or duplicated' }
            $scenario = $scenario[0]
            if ($scenario.RouteMode -cne 'pending-preparation' -or $scenario.Fault -cne 'none' -or
                $scenario.PayerMode -cne 'helpee' -or $scenario.PayloadBytes -ne 67108864 -or
                ($scenario.ExpectedRouteChanges -join ',') -cne 'regular_nkn_v4_fast,file_tuna_v4' -or
                $null -ne $scenario.Baseline) { throw 'target contract changed' }
            $prepared = $defaults | Where-Object Name -eq 'active-tuna-v4-64mb'
            if ($prepared.RouteMode -cne 'preactivated' -or ($prepared.ExpectedRouteChanges -join ',') -cne 'file_tuna_v4') { throw 'prepared contract changed' }
            if ($wrapper.ParamBlock.Extent.Text -notmatch '"pending-preparation"') { throw 'wrapper cannot select mode' }
            $cycle = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true)
            $body = $cycle.Body.Extent.Text
            if ($body.IndexOf('Write-DeterministicFileTransferPayload') -gt $body.IndexOf('$sendButton = Wait-ControlEnabledStateByAutomationId') -or
                $body.IndexOf('$sendButton = Wait-ControlEnabledStateByAutomationId') -gt $body.IndexOf("if (`$RouteMode -eq 'pending-preparation')")) { throw 'preparation window wasted before Send' }
            $pendingBranches = @($cycle.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst]}, $true) |
                ForEach-Object { $_.Clauses } | Where-Object {
                    $_.Item1.Extent.Text -ceq "`$RouteMode -eq 'pending-preparation'" -and
                    $_.Item2.Extent.Text -match 'Unlock-TunaPayers|Wait-AppLogContainsAllAfterBookmark|-not \$pendingPreparationHandoffObserved'
                })
            if ($pendingBranches.Count -ne 3) { throw 'unexpected pending preparation branches' }
            $pendingBodies = @($pendingBranches | ForEach-Object {
                $text = $_.Item2.Extent.Text
                $text.Substring(1, $text.Length - 2)
            })
            $Context = 'context'; $PayerMode = 'helpee'; $FaultMode = 'none'; $WalletPassword = 'fixture'
            $script:unlockCount = 0
            function Unlock-TunaPayers { param($Context, $PayerMode, $Password) $script:unlockCount++ }
            function Wait-GuiPairedPreparationReadiness { throw 'pending scenario waited for prepared route' }
            $observedEvidenceLines = [Collections.Generic.List[string]]::new()
            $bookmark = 17; $sw = [Diagnostics.Stopwatch]::StartNew()
            $pendingPreparationHandoffObserved = $false
            function Wait-AppLogContainsAllAfterBookmark {
                param($Bookmark, $Needles, $TimeoutMs, $Description)
                if ($Bookmark -eq 17 -and ($Needles -join ',') -ceq 'event=tuna_sidecar_process_started,role=listener') {
                    if ($script:unlockCount -ne 1) { throw 'listener start waited before unlock' }
                    return 'listener-start'
                }
                if ($Bookmark -ne 17 -or ($Needles -join ',') -cne 'event=session_preparation_transfer_activation_requested,reason=pending_preparation_admitted,authority_effect=none') { throw 'handoff witness weakened' }
                return 'handoff'
            }
            function Wait-TunaGuiFileTunaV4PublicationAfterBookmark { throw 'prepared-only publication selector used for initial handoff' }
            function Wait-TunaGuiLiveRouteEpochEvidence {
                param($Bookmark, $Route, $ProtocolVersion, $HandoffKind, $TargetTransport, $EventName, [switch]$RequireAuthoritativeProjection, $TimeoutMs, $Description)
                if ($Bookmark -ne 17 -or $Route -cne 'file_tuna_v4' -or $ProtocolVersion -ne 4 -or
                    $HandoffKind -cne 'normal_to_tuna_activation' -or $TargetTransport -cne 'tuna' -or
                    $EventName -cne 'filetransfer_live_route_epoch_started' -or -not $RequireAuthoritativeProjection) { throw 'initial handoff witness weakened' }
                return 'coordinator-publication'
            }
            Invoke-Expression $pendingBodies[0]
            if ($script:unlockCount -ne 1) { throw 'unlock missing' }
            Invoke-Expression $pendingBodies[1]
            if (-not $pendingPreparationHandoffObserved -or -not $fileTunaV4PublicationObserved -or $observedEvidenceLines.Count -ne 3) { throw 'startup, handoff or publication not retained' }
            $evidence = [pscustomobject]@{ fileTunaV4PublicationObserved=$true; peerVisibleTunaPayloadObserved=$true
                missingTunaDataPathProof=$false; fallbackEpochStarted=$false; fallbackEpochRecovered=$false; fallbackEpochWaiting=$false }
            Invoke-Expression $pendingBodies[2]
            foreach ($field in $evidence.PSObject.Properties.Name) {
                $old = $evidence.$field; $evidence.$field = -not $old
                $rejected = $false
                try { Invoke-Expression $pendingBodies[2] } catch { $rejected = $true }
                if (-not $rejected) { throw ('invalid evidence accepted: ' + $field) }
                $evidence.$field = $old
            }
            $pendingPreparationHandoffObserved = $false
            $rejected = $false
            try { Invoke-Expression $pendingBodies[2] } catch { $rejected = $true }
            if (-not $rejected) { throw 'missing admission handoff accepted' }
            'pending-preparation-gate-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal("pending-preparation-gate-ok", result.Stdout.Trim());
    }
}
