param([string]$RepoRoot, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'wrapper parse failed' }
$writer = @($ast.FindAll({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Write-TunaGuiDirectEvidenceManifest'
}, $true))
if ($writer.Count -ne 1) { throw 'expected one direct manifest writer' }
Invoke-Expression $writer[0].Extent.Text
$reducer = @($writer[0].FindAll({ param($n)
    $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$retainedAnalysisStatus'
}, $true))
if ($reducer.Count -ne 1) { throw 'expected one status reducer' }

# Only the writer is loaded: no wrapper entry point, analysis or GUI process runs.
# A deterministic git stub permits a clean-source positive proof control.
function git {
    if ($args -contains 'rev-parse') { return ('a' * 40) }
    if ($args -contains 'status') { if ($sourceIsDirty) { ' M synthetic' }; return }
    throw 'unexpected git invocation'
}
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'synthetic-acceptance'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'synthetic-scenario'
$RouteMode = 'live-regular-activation-cycle'
$PayloadSize = '256mb'; $Direction = 'helper-to-helpee'; $PayerMode = 'helper'
$Fault = 'none'; $LiveToggleSequence = 'off-on-off'

function Write-Json($Path, $Value) {
    $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Get-FixturePins($Directory) {
    @(Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'filetransfer-tuna-gui-evidence-manifest.json' |
        Sort-Object Name | ForEach-Object { '{0}:{1}' -f $_.Name, (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
}
function Check-Manifest {
    param(
        [string]$Name, [int]$ExitCode, [bool]$Available, [string]$ExpectedStatus,
        [string]$Owner = 'RouteAcceptance', [string]$Receipt = 'absent', [string]$Corruption = '',
        [bool]$ExpectedReceiptValid = $false, [bool]$ExpectedProof = $false, [bool]$ExpectedFinalized = $true
    )
    $RetainedAnalysisOwner = $Owner
    $sourceIsDirty = $Corruption -eq 'source'
    $dir = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $capture = Join-Path $dir 'filetransfer-gui-log-ledger-slice.log'
    $inputPath = Join-Path $dir 'filetransfer-retained-log-slice.log'
    'synthetic capture, not runtime evidence' | Set-Content -LiteralPath $capture -Encoding UTF8
    'synthetic analysis input, not runtime evidence' | Set-Content -LiteralPath $inputPath -Encoding UTF8
    $captureSha = Get-FileTransferEvidenceSha256 $capture
    $inputSha = Get-FileTransferEvidenceSha256 $inputPath
    $state = [ordered]@{
        timedOut = $false; lastCompletedStage = 'gui_child_exited'; captureSha256 = $captureSha
        acceptanceRunId = $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenarioRunId = $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
    }
    $ledger = [ordered]@{
        retainedSlice = @{ sha256 = $captureSha }; analyzerInput = @{ captureSha256 = $inputSha }
        cursorIntegrityVerdict = 'pass'; writerEvidenceVersion = 3; writerIntegrityVerdict = 'pass'
        writerChronologyInversionCount = 0; writerChronologyCollisionCount = 0; writerChronologyVerdict = 'pass'
        acceptanceRunId = $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenarioRunId = $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
    }
    if ($Corruption -eq 'identity') { $ledger.scenarioRunId = 'wrong-scenario' }
    if ($Corruption -eq 'capture') { $ledger.retainedSlice.sha256 = 'wrong-hash' }
    if ($Corruption -eq 'input') { $ledger.analyzerInput.captureSha256 = 'wrong-hash' }
    if ($Corruption -eq 'cursor') { $ledger.cursorIntegrityVerdict = 'fail' }
    if ($Corruption -eq 'writer') { $ledger.writerIntegrityVerdict = 'fail' }
    Write-Json (Join-Path $dir 'filetransfer-gui-evidence-finalization-state.json') $state
    if ($Corruption -ne 'missing-ledger') { Write-Json (Join-Path $dir 'filetransfer-gui-log-ledger.json') $ledger }
    Write-Json (Join-Path $dir 'filetransfer-tuna-gui-summary.json') @{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'; routeMode = $RouteMode
        completed = $true; integrityOk = ($Corruption -ne 'summary')
    }
    $receiptPath = Join-Path $dir 'filetransfer-retained-analysis-attempt-receipt.json'
    if ($Receipt -eq 'malformed') { '{broken' | Set-Content -LiteralPath $receiptPath -Encoding UTF8 }
    elseif ($Receipt -ne 'absent') {
        foreach ($file in @('filetransfer-retained-analysis.stdout.txt', 'filetransfer-retained-analysis.stderr.txt',
                'filetransfer-retained-analysis-diagnostics.json', 'filetransfer-retained-analysis-stage-diagnostics.json',
                'filetransfer-retained-compact-summary.json', 'filetransfer-coordinator-snapshot-compact-summary.json',
                'filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt', 'stability-gates-summary.txt')) {
            '{}' | Set-Content -LiteralPath (Join-Path $dir $file) -Encoding UTF8
        }
        Write-Json (Join-Path $dir 'filetransfer-coordinator-snapshot-full-summary.json') @{
            Mode = 'synthetic'; ContractVersion = 1; CatalogVersion = 'synthetic'; CatalogHash = 'catalog'; BoundaryHash = 'boundary'
        }
        Write-Json (Join-Path $dir 'filetransfer-coordinator-snapshot-parity-summary.json') @{ Verdict = 'pass' }
        $receiptObject = Write-FileTransferRetainedAnalysisAttemptReceipt -ArtifactDir $dir -Owner $Owner -AttemptId $Name `
            -AcceptanceRunId $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ScenarioRunId $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID `
            -LogPath $inputPath -InputSha256Before $inputSha -InputLineCountBefore 1 -LiveRouteProofMode 'RegularActivationCycle' `
            -CompactRequested $true -ProcessId 0 -StartedUtc '2026-01-01T00:00:00Z' -EndedUtc '2026-01-01T00:00:01Z' `
            -TimedOut ($Receipt -eq 'timed_out') -ExitCodeAvailable $true -ExitCode $(if ($Receipt -eq 'failed') { 1 } else { 0 }) `
            -CompletedProofArtifacts $true
        if ($Receipt -eq 'in_progress') { $receiptObject.status = 'in_progress' }
        if ($Receipt -eq 'wrong-identity') { $receiptObject.scenarioRunId = 'wrong-scenario' }
        if ($Receipt -eq 'wrong-hash') { $receiptObject.analyzerInputSha256After = 'wrong-hash' }
        Write-Json $receiptPath $receiptObject
    }
    $pins = Get-FixturePins $dir
    $previous = $null
    foreach ($write in 1..2) {
        Write-TunaGuiDirectEvidenceManifest -RepoRoot $RepoRoot -ArtifactDir $dir -GuiExitCode $ExitCode -GuiExitCodeAvailable $Available
        $manifest = Get-Content -LiteralPath (Join-Path $dir 'filetransfer-tuna-gui-evidence-manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.retainedAnalysisStatus -cne $ExpectedStatus) {
            throw "${Name}: expected $ExpectedStatus, got $($manifest.retainedAnalysisStatus)"
        }
        if ($manifest.retainedAnalysisCompleted -ne ($ExpectedStatus -eq 'completed') -or
            $manifest.retainedAnalysisReceiptValid -ne $ExpectedReceiptValid -or $manifest.currentRuntimeProof -ne $ExpectedProof -or
            $manifest.artifactFinalized -ne $ExpectedFinalized) { throw "${Name}: completion/receipt/proof/finalization changed" }
        if ($manifest.guiExitCode -ne $ExitCode -or $manifest.guiExitCodeAvailable -ne $Available -or
            $manifest.captureSha256 -ne $captureSha -or $manifest.analyzerInputSha256 -ne $inputSha -or
            $manifest.acceptanceRunId -ne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
            $manifest.scenarioRunId -ne $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or
            $manifest.sourceCommit -ne ('a' * 40) -or $manifest.sourceDirty -ne $sourceIsDirty) { throw "${Name}: provenance changed" }
        if (@($manifest.retainedAnalysisReceiptPaths).Count -ne 1 -or $manifest.retainedAnalysisReceiptPaths[0] -ne $receiptPath -or
            $manifest.retainedAnalysisReceiptSha256 -ne (Get-FileTransferEvidenceSha256 $receiptPath)) { throw "${Name}: receipt binding changed" }
        if ($Receipt -eq 'absent' -and ($manifest.PSObject.Properties.Name -contains 'analysisAttempts' -or
                ($manifest.PSObject.Properties.Name -contains 'retainedAnalysisExitCodeAvailable' -and $manifest.retainedAnalysisExitCodeAvailable))) {
            throw "${Name}: invented analysis execution evidence"
        }
        if ((Get-FixturePins $dir) -cne $pins) { throw "${Name}: writer created or mutated fixture/receipt evidence" }
        $semantic = $manifest | Select-Object * -ExcludeProperty generatedAtUtc | ConvertTo-Json -Depth 16 -Compress
        if ($null -ne $previous -and $semantic -cne $previous) { throw "${Name}: repeated write changed semantics" }
        $previous = $semantic
    }
    Write-Output "case-ok: $Name"
}

Check-Manifest 'failed' 1 $true 'not_started_due_to_gui_failure'
Check-Manifest 'available-124' 124 $true 'not_started_due_to_gui_failure'
Check-Manifest 'available-minus-one' -1 $true 'not_started_due_to_gui_failure'
Check-Manifest 'successful-pending' 0 $true 'delegated_pending'
Check-Manifest 'unavailable-default' -1 $false 'delegated_pending'
Check-Manifest 'unavailable-nonzero' 1 $false 'delegated_pending'
Check-Manifest 'unavailable-124' 124 $false 'delegated_pending'
Check-Manifest 'wrapper-failed' 1 $true 'not_started' -Owner Wrapper
Check-Manifest 'wrapper-success' 0 $true 'not_started' -Owner Wrapper
Check-Manifest 'wrapper-unknown' -1 $false 'not_started' -Owner Wrapper
Check-Manifest 'receipt-completed-success' 0 $true 'completed' -Receipt completed -ExpectedReceiptValid $true -ExpectedProof $true
Check-Manifest 'receipt-completed-failed-gui' 1 $true 'completed' -Receipt completed -ExpectedReceiptValid $true
Check-Manifest 'receipt-completed-unknown-gui' -1 $false 'completed' -Receipt completed -ExpectedReceiptValid $true
foreach ($status in @('failed', 'timed_out', 'in_progress')) {
    Check-Manifest "receipt-$status" 1 $true $status -Receipt $status
}
Check-Manifest 'receipt-malformed' 1 $true 'failed' -Receipt malformed
foreach ($invalid in @('wrong-identity', 'wrong-hash')) {
    Check-Manifest "receipt-$invalid-success-gui" 0 $true 'completed' -Receipt $invalid
    Check-Manifest "receipt-$invalid-failed-gui" 1 $true 'completed' -Receipt $invalid
}
foreach ($invalid in @('capture', 'input', 'missing-ledger')) {
    Check-Manifest "absent-$invalid" 1 $true 'not_started_due_to_gui_failure' -Corruption $invalid -ExpectedFinalized $false
    Check-Manifest "completed-$invalid" 0 $true 'completed' -Receipt completed -Corruption $invalid -ExpectedReceiptValid $true -ExpectedFinalized $false
}
foreach ($invalid in @('identity', 'cursor', 'writer', 'summary', 'source')) {
    Check-Manifest "completed-$invalid" 0 $true 'completed' -Receipt completed -Corruption $invalid -ExpectedReceiptValid $true
}
# Exercise mixed reducer inputs without widening the single-directory parent path.
foreach ($case in @(
        @{ States = @('not_started_due_to_gui_failure'); Expected = 'not_started_due_to_gui_failure' },
        @{ States = @('not_started_due_to_gui_failure', 'failed'); Expected = 'failed' },
        @{ States = @('not_started_due_to_gui_failure', 'failed', 'timed_out'); Expected = 'timed_out' },
        @{ States = @('completed', 'not_started'); Expected = 'in_progress' },
        @{ States = @('completed', 'completed'); Expected = 'completed' })) {
    $analysisStatuses = $case.States
    Invoke-Expression $reducer[0].Extent.Text
    if ($retainedAnalysisStatus -cne $case.Expected) { throw "status reducer lost $($case.Expected)" }
}
Write-Output 'failed-gui-analysis-status-ok'
