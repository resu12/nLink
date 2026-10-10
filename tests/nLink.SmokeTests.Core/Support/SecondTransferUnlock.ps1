param([string]$RepoRoot, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'GUI script does not parse.' }
foreach ($name in @('Unlock-TunaFromSessionHeader', 'Unlock-TunaPayers')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Missing production function: $name" }
    # Only the UI parameter type changes; production statements use stub controls.
    . ([scriptblock]::Create($definitions[0].Extent.Text.Replace('[System.Windows.Automation.AutomationElement]', '[object]')))
}
$onBranches = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -ceq '-not $prearmedActivation' -and
    $node.Extent.Text.Contains('Unlock-TunaPayers')
}, $true))
$preparations = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and $node.Body.Statements.Count -gt 0 -and
    $node.Body.Statements[0] -is [Management.Automation.Language.IfStatementAst] -and
    $node.Body.Statements[0].Clauses[0].Item1.Extent.Text -ceq '$secondTransferRequiresFreshDiagnosticReadiness'
}, $true))
if ($onBranches.Count -ne 1 -or $preparations.Count -ne 1) { throw 'Production on/preparation blocks are not unique.' }
$onAction = [scriptblock]::Create($onBranches[0].Extent.Text)
$body = $preparations[0].Body
$publication = @($body.Statements | Where-Object {
    $_.Extent.Text.StartsWith('$secondRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark')
})
if ($publication.Count -ne 1) { throw 'Second-transfer publication wait is missing.' }
# Execute the unchanged production prefix, including fresh readiness, send,
# accept and the publication wait. No production catch masks a failed assertion.
$prepare = [scriptblock]::Create($body.Extent.Text.Substring(1,
    $publication[0].Extent.EndOffset - $body.Extent.StartOffset - 1))

# Every external/UI operation is synthetic. No live ledger or authority evaluator.
function Write-Host {}
function Get-AppLogBookmark { return 1 }
function Submit-TunaWalletPasswordDialogIfPresent { return $false }
function Wait-ControlEnabledStateByAutomationId {
    param($Window,$AutomationId,$IsEnabled,$TimeoutMs)
    return [pscustomobject]@{ Kind='header'; Id=$Window.Id }
}
function Click-Element {
    param($Element)
    if ($Element.Kind -eq 'header') {
        $script:headerClicks++
        if ($script:on[$Element.Id]) { $script:on[$Element.Id] = $false }
        else { $script:pending[$Element.Id] = $true }
    }
    else { $script:transferClicks++ }
}
function Get-WalletPasswordDialogForProcess {
    param($ProcessId)
    if ($script:pending[$ProcessId]) { return [pscustomobject]@{ Id=$ProcessId } }
    return $null
}
function Get-AppLogLinesAfterBookmark { return 'event=tuna_runtime_locked; source=header; reason=header_switch_off' }
function Wait-Until {
    param($TimeoutMs,$PollMs,$OnTimeoutMessage,[scriptblock]$Condition)
    $result = & $Condition
    if ($null -eq $result) { throw $OnTimeoutMessage }
    return $result
}
function Submit-TunaWalletPasswordDialog {
    param($Process,$Dialog,$Password,$RoleLabel,$Reason)
    if ($script:failUnlock) { throw 'initial unlock failed' }
    $script:on[$Process.Id] = $true
    $script:pending[$Process.Id] = $false
    $script:passwordSubmissions++
    return $true
}
function Wait-AppLogContainsAnyAllAfterBookmark { return 'event=tuna_runtime_unlocked; source=header' }
function Wait-AndSubmitTunaWalletPasswordDialogIfPresent { throw 'Unexpected late-dialog path.' }
function Submit-TunaWalletPasswordDialogsForPayersIfPresent { $script:promptChecks++; return 0 }
function Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer {
    param($Bookmark,$TimeoutMs)
    if ($Bookmark -ne 7) { throw 'Diagnostic readiness lost the new-transfer bookmark.' }
    if (-not $script:readinessPresent) { throw 'diagnostic readiness missing' }
    return 'synthetic diagnostic readiness only'
}
function Wait-TunaGuiSecondTransferReadinessOrThrow { return [pscustomobject]@{Kind='send'} }
function Wait-TunaGuiFileTransferAcceptOrThrow { return [pscustomobject]@{Kind='accept'} }
function Wait-TunaGuiFileTunaV4PublicationAfterBookmark {
    param($Bookmark,$TimeoutMs,$Description,$ActionKind,$RequireZeroRouteCommitGeneration)
    $script:publicationWaits++
    if ($Bookmark -ne 7 -or $ActionKind -cne 'RoutePublication' -or -not $RequireZeroRouteCommitGeneration) {
        throw 'Required new-transfer publication scope was weakened.'
    }
    if ($script:missingPublication) { throw 'required second publication missing' }
    return 'synthetic second-transfer publication'
}

$Context = [pscustomobject]@{
    HelpeeProc=[pscustomobject]@{Id=1}; HelpeeWindow=[pscustomobject]@{Id=1}
    HelperProc=[pscustomobject]@{Id=2}; HelperWindow=[pscustomobject]@{Id=2}
}
$WalletPassword = 'synthetic-not-a-wallet-password'
$RouteMode = 'live-reactivation-second-transfer'
$firstTransferTerminalBeforeLiveReactivationTransferId = 'first-transfer'
$senderWindow = $Context.HelpeeWindow
$receiverWindow = $Context.HelperWindow
$expectedOutboundRole = 'helpee'
$stepNumber = 2
$secondBookmark = 7
$StartupTimeoutMs = 1000
$results = New-Object 'System.Collections.Generic.List[object]'
foreach ($PayerMode in @('helpee','helper','both')) {
    $stepPayerMode = $PayerMode
    foreach ($prearmedActivation in @($false,$true)) {
        foreach ($mode in @('fresh-ready','fresh-not-ready','not-fresh-ready','not-fresh-not-ready','missing-publication','unlock-failure')) {
            $script:on = @{1=$false;2=$false}
            $script:pending = @{1=$false;2=$false}
            $script:headerClicks = 0
            $script:passwordSubmissions = 0
            $script:promptChecks = 0
            $script:transferClicks = 0
            $script:publicationWaits = 0
            $script:failUnlock = $mode -eq 'unlock-failure'
            $script:missingPublication = $mode -eq 'missing-publication'
            $script:readinessPresent = $mode -notin @('fresh-not-ready','not-fresh-not-ready')
            $secondTransferRequiresFreshDiagnosticReadiness = -not $mode.StartsWith('not-fresh-')
            [int[]]$expectedPayers = if ($PayerMode -eq 'both') { @(1,2) } elseif ($PayerMode -eq 'helper') { @(2) } else { @(1) }
            $ArtifactDir = Join-Path $OutputRoot "$PayerMode-$prearmedActivation-$mode"
            [void][IO.Directory]::CreateDirectory($ArtifactDir)
            $observedEvidenceLines = New-Object 'System.Collections.Generic.List[string]'
            # Old transfer success/readiness must never bypass the publication wait.
            $summary = [ordered]@{ completed=$true; integrityOk=$true }
            $fileTunaV4PublicationObserved = $true
            $diagnosticTunaReadinessObserved = $true
            $secondTransferProofOk = $false
            $secondRouteLine = ''
            $reachedPreparation = $false
            $caught = ''
            try {
                if ($prearmedActivation) {
                    # Seed the completed prearmed action with the real unlock helpers.
                    Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
                }
                . $onAction
                $reachedPreparation = $true
                . $prepare
            }
            catch { $caught = $_.Exception.Message }
            if ($script:failUnlock) {
                if ($caught -cne 'initial unlock failed' -or $reachedPreparation -or $script:publicationWaits -ne 0 -or
                    $script:transferClicks -ne 0 -or $secondTransferProofOk) { throw 'Initial on failure was hidden.' }
            }
            else {
                if ($script:headerClicks -ne $expectedPayers.Count -or $script:passwordSubmissions -ne $expectedPayers.Count -or
                    @($expectedPayers | Where-Object { -not $script:on[$_] }).Count -ne 0) {
                    throw "Second-transfer preparation repeated the on action: $PayerMode/$prearmedActivation/$mode clicks=$script:headerClicks"
                }
                $expectedError = if ($script:missingPublication) { 'required second publication missing' } else { '' }
                if ($caught -cne $expectedError -or $script:publicationWaits -ne 1 -or $script:transferClicks -ne 2 -or
                    $script:promptChecks -ne [int]$prearmedActivation -or $secondTransferProofOk) { throw "Preparation/proof gate changed: $caught" }
                if ($summary.secondTransferDiagnosticReadiness.observed -ne $script:readinessPresent -or
                    $summary.secondTransferDiagnosticReadiness.required -ne $secondTransferRequiresFreshDiagnosticReadiness) {
                    throw 'Diagnostic readiness handling changed.'
                }
                if (-not $script:readinessPresent -and ($summary.secondTransferDiagnosticReadiness.authority -cne 'diagnostic_only' -or
                    $summary.secondTransferDiagnosticReadiness.nextRequiredProof -cne 'second_transfer_file_tuna_v4_route_publication')) {
                    throw 'Diagnostic readiness was promoted to authority.'
                }
            }
            $results.Add([ordered]@{payer=$PayerMode;prearmed=$prearmedActivation;mode=$mode;headerClicks=$script:headerClicks;
                passwordSubmissions=$script:passwordSubmissions;publicationWaits=$script:publicationWaits;error=$caught})
        }
    }
}
$results.ToArray() | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputRoot 'cases.json') -Encoding utf8
Write-Output "second-transfer-unlock-ok cases=$($results.Count)"
