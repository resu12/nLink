param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$parseErrors = $null
$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'GUI script does not parse.' }
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification', 'Test-TunaGuiCompletedFailureClassificationResult')) {
    $definitions = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Get-AppLogLinesAfterBookmark { throw 'Live ledger must not be read.' }
function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'Process output must not be read.' }

# Prove the nullable contract using a fresh authoritative passing fixture.
$nullable = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 `
    -ErrorMessage 'original second-transfer failure' -RouteMode 'live-reactivation-second-transfer' `
    -ImmutableEvidencePaths @($FixturePath)
if ($null -ne $nullable) { throw 'Passing snapshot fixture no longer exercises the nullable classifier result.' }
$complete = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 `
    -ErrorMessage 'original second-transfer failure' -RouteMode 'live-reactivation-second-transfer' `
    -ImmutableEvidencePaths @($FixturePath) -RequireCompletedClassification
if ($complete.Phase -cne 'evidence_harness' -or
    $complete.Reason -cne 'authoritative_snapshot_pass_harness_failure_unclassified' -or
    $complete.FailureClass -cne 'filetransfer_gui_failure_classification_unresolved') {
    throw 'Completed classification must remain fail-closed without inventing a runtime failure.'
}
Write-Output 'fresh-snapshot-nullable-contract-reproduced'

# Execute the actual production catch/finally, isolating only the classifier
# outcomes and file-copy side effect. No GUI or route-authority work is invoked.
$catches = @($guiAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.CatchClauseAst] -and
        $node.Body.Extent.Text.Contains('$secondSw.IsRunning')
}, $true))
if ($catches.Count -ne 1) { throw 'Second-transfer catch is not unique.' }
$exercise = [scriptblock]::Create('try { throw $originalFailure } ' + $catches[0].Extent.Text +
    ' finally ' + $catches[0].Parent.Finally.Extent.Text)
function Copy-FileTransferLiveLogSlice { $script:copyCount++ }
function Get-TunaGuiFileTransferSetupFailureClassification {
    param($Bookmark, $ErrorMessage, $RouteMode, [switch]$RequireCompletedClassification)
    $script:classificationCalls++
    $script:completionRequested = $RequireCompletedClassification.IsPresent
    if ($ErrorMessage -notin @('original second-transfer failure', 'original accept timeout')) { throw 'Original error was changed before classification.' }
    switch ($script:mode) {
        'null' { return $null }
        'complete' { return $complete }
        'explicit' {
            return New-TunaGuiFileTransferFailureClassificationResult -Phase 'measured_route' `
                -Reason 'expected_tuna_v4_missing' -RouteMode $RouteMode -ClassificationSource 'explicit_error'
        }
        'partial' { return [pscustomobject]@{ Reason = 'incomplete response' } }
        'incomplete' {
            return New-TunaGuiFileTransferFailureClassificationResult -Phase 'not_final' -Reason 'not_final' `
                -RouteMode $RouteMode -ClassificationSource 'partial' -ClassificationComplete $false
        }
        'empty_phase' {
            $invalid = New-TunaGuiFileTransferFailureClassificationResult -Phase 'temporary' -Reason 'invalid' `
                -RouteMode $RouteMode -ClassificationSource 'invalid'
            $invalid.Phase = ''
            return $invalid
        }
        'throw' { throw 'classifier exploded' }
    }
}
$caseCount = 0
foreach ($script:mode in @('null', 'complete', 'explicit', 'partial', 'incomplete', 'empty_phase', 'throw')) {
    foreach ($existingPhase in @($false, $true)) {
        $ArtifactDir = Join-Path $OutputRoot ("$script:mode-$existingPhase")
        [void][IO.Directory]::CreateDirectory($ArtifactDir)
        $summary = [ordered]@{ completed = $true; integrityOk = $true; firstTransferMarker = 'preserve' }
        $secondTransferPhase = if ($existingPhase) {
            [ordered]@{ name = 'existing'; transferId = 'transfer-2'; route = 'regular_nkn_v4_fast';
                protocolVersion = 4; payloadBytes = 16777216; completed = $true; integrityOk = $true; marker = 'preserve' }
        } else { $null }
        $secondSw = [Diagnostics.Stopwatch]::StartNew()
        if ($existingPhase) { $secondSw.Stop() }
        $secondBookmark = 7
        $secondPayloadBytes = 16777216
        $secondExpectedHash = 'fixture-hash'
        $secondRouteLine = ''
        $RouteMode = 'live-reactivation-second-transfer'
        $secondTransferProofOk = $false
        $script:copyCount = 0
        $script:classificationCalls = 0
        $script:completionRequested = $false
        $originalFailure = [Management.Automation.ErrorRecord]::new(
            [InvalidOperationException]::new('original second-transfer failure'), 'original_failure_id',
            [Management.Automation.ErrorCategory]::InvalidOperation, 'transfer-2')
        $caught = $null
        try { . $exercise } catch { $caught = $_ }
        if ($null -eq $caught -or $caught.Exception.Message -cne $originalFailure.Exception.Message -or
            $caught.FullyQualifiedErrorId -cne $originalFailure.FullyQualifiedErrorId) {
            throw "Original error was masked for $script:mode/$existingPhase : $caught"
        }
        $persisted = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Raw | ConvertFrom-Json
        $phase = $persisted.secondTransfer
        $adopted = $script:mode -in @('complete', 'explicit')
        $expectedPhase = if ($script:mode -eq 'complete') { $complete.Phase }
            elseif ($script:mode -eq 'explicit') { 'measured_route' } else { 'harness_failure_classification' }
        $expectedReason = if ($script:mode -eq 'complete') { $complete.Reason }
            elseif ($script:mode -eq 'explicit') { 'expected_tuna_v4_missing' } else { 'evidence_enrichment_pending' }
        $expectedSource = if ($script:mode -eq 'complete') { $complete.ClassificationSource }
            elseif ($script:mode -eq 'explicit') { 'explicit_error' } else { 'classification_pending' }
        if ($phase.error -cne $originalFailure.Exception.Message -or $phase.setupFailurePhase -cne $expectedPhase -or
            $phase.setupFailureReason -cne $expectedReason -or $phase.classificationComplete -ne $adopted -or
            $phase.classificationSource -cne $expectedSource -or
            $phase.classificationError -cne $(if ($script:mode -eq 'throw') { 'classifier exploded' } else { '(none)' })) {
            throw "Failure phase/classification mismatch for $script:mode/$existingPhase"
        }
        if (-not $persisted.completed -or -not $persisted.integrityOk -or $persisted.firstTransferMarker -cne 'preserve' -or
            $secondTransferProofOk -or $secondSw.IsRunning -or $script:copyCount -ne 1 -or
            $script:classificationCalls -ne 1 -or -not $script:completionRequested) { throw 'Failure handling changed proof or cleanup.' }
        if ($existingPhase) {
            if ($phase.marker -cne 'preserve' -or $phase.route -cne 'regular_nkn_v4_fast' -or
                -not $phase.completed -or -not $phase.integrityOk) { throw 'Existing measured observations were replaced.' }
        }
        elseif ($phase.completed -or $phase.integrityOk -or $phase.route -cne '(unknown)' -or
            $phase.protocolVersion -ne 0 -or $phase.payloadBytes -ne 16777216 -or
            $phase.expectedSha256 -cne 'fixture-hash' -or $phase.receivedSha256 -cne '(none)') {
            throw 'Missing transfer acquired success or route evidence.'
        }
        $caseCount++
    }
}
Write-Output "second-transfer-failure-reporting-ok cases=$caseCount"

$acceptFunction = @($guiAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Wait-TunaGuiFileTransferAcceptOrThrow'
}, $true))[0]
$acceptCatch = @($acceptFunction.FindAll({ param($node)
    $node -is [Management.Automation.Language.CatchClauseAst]
}, $true))[0]
$acceptExercise = [scriptblock]::Create('try { throw $originalFailure } ' + $acceptCatch.Extent.Text)
$acceptCases = 0
foreach ($script:mode in @('null', 'complete', 'explicit', 'partial', 'incomplete', 'empty_phase', 'throw')) {
    $script:classificationCalls = 0
    $script:completionRequested = $false
    $Bookmark = 7
    $originalFailure = [Management.Automation.ErrorRecord]::new(
        [TimeoutException]::new('original accept timeout'), 'original_accept_timeout',
        [Management.Automation.ErrorCategory]::OperationTimeout, 'accept-button')
    $caught = $null
    try { . $acceptExercise } catch { $caught = $_ }
    if ($null -eq $caught -or $script:classificationCalls -ne 1 -or -not $script:completionRequested) {
        throw 'Accept failure did not request a complete classification exactly once.'
    }
    if ($script:mode -in @('complete', 'explicit')) {
        if (-not $caught.Exception.Message.Contains('original_error=original accept timeout') -or
            -not $caught.Exception.Message.Contains('Tuna GUI measured file-transfer accept did not become enabled:')) {
            throw 'Complete accept classification lost the original timeout or existing context.'
        }
    }
    elseif ($caught.Exception.Message -cne $originalFailure.Exception.Message -or
        $caught.FullyQualifiedErrorId -cne $originalFailure.FullyQualifiedErrorId) {
        throw "Original accept failure was masked for $script:mode : $caught"
    }
    $acceptCases++
}
Write-Output "accept-failure-reporting-ok cases=$acceptCases"
