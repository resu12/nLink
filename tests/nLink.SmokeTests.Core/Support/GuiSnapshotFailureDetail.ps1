param([string]$RepoRoot, [string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/GuiDataPathDiagnosticCompanion.ps1')
$script:originalSnapshotEvaluator = ${function:Get-FileTransferCoordinatorSnapshotEvaluation}
$script:originalFamilyPredicate = ${function:Test-FileTransferCoordinatorSnapshotFamilyAuthoritative}
$tokens = $null; $errors = $null
$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'GUI parse failed' }
foreach ($name in @('Write-GuiSmokeAtomicJson',
    'New-TunaGuiFileTransferFailureClassificationResult',
    'Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification',
    'New-TunaGuiFailureClassificationEnrichmentReceipt',
    'Invoke-TunaGuiFailureClassificationRequest')) {
    $definition = $guiAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}
$scenario = $guiAst.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Run-ScenarioFileTransferTunaHandoffFallback'
}, $true)
$summaryAssignment = $scenario.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -ceq '$newFailureSummary'
}, $true)
Invoke-Expression $summaryAssignment.Extent.Text

# Unit seam: the original primary still calls its full and compact evaluators.
# Distinct full detail detects accidental export from the wrong selected view.
$script:case = 'located'
$script:evaluatorCalls = 0
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param($Lines, $SnapshotRecords)
    $script:evaluatorCalls++
    $boundary = [pscustomobject]@{
        boundaryId=('a' * 64); boundaryHash=('b' * 64); variant='(none)';
        rawCount=1; distinctCount=1; recoveredCount=0; unresolvedCount=1;
        writer='writer-fixture'; writerSequence=42L;
        decisionReason='snapshot_payload_hash_mismatch';
        eventName='filetransfer_coordinator_transition_snapshot'; evidenceLineIndex=1L
    }
    switch ($script:case) {
        'artifact' { $boundary.eventName='(artifact)'; $boundary.evidenceLineIndex=-1L }
        'missing' { $boundary.PSObject.Properties.Remove('decisionReason') }
        'null_boundary' { $boundary=$null }
        'long_reason' { $boundary.decisionReason='x' * 257 }
        'long_event' { $boundary.eventName='x' * 129 }
        'long_id' { $boundary.boundaryId='a' * 129 }
        'long_hash' { $boundary.boundaryHash='b' * 129 }
        'unsafe_reason' { $boundary.decisionReason="snapshot_failure`nraw payload" }
        'unsafe_event' { $boundary.eventName='event; payload=private' }
        'string_index' { $boundary.evidenceLineIndex='1' }
        'fraction_index' { $boundary.evidenceLineIndex=1.5 }
        'negative_index' { $boundary.evidenceLineIndex=-2L }
        'outside_index' { $boundary.evidenceLineIndex=2L }
        'throwing_reason' {
            $boundary.PSObject.Properties.Remove('decisionReason')
            $boundary | Add-Member ScriptProperty decisionReason { throw 'diagnostic getter failed' }
        }
        'limit' {
            $boundary.decisionReason='x' * 256; $boundary.eventName='e' * 128
            $boundary.boundaryId='a' * 128; $boundary.boundaryHash='b' * 128
        }
    }
    if ($script:case -in @('located','artifact') -and $null -ne $Lines) {
        $boundary.decisionReason='full_view_not_selected'
    }
    return [pscustomobject]@{
        Mode='SnapshotV1Authoritative'; StreamComplete=$true;
        ArtifactEvidenceVerdict='fail'; TransitionCount=1L;
        FirstUnresolvedBoundary=$boundary
    }
}
function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative { return $false }
function Get-AppLogLinesAfterBookmark { throw 'unexpected live source read' }
function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'unexpected process output read' }

$inputPath = Join-Path $ArtifactRoot 'input.log'
[IO.File]::WriteAllLines($inputPath, [string[]]@('unrelated record',
    'event=filetransfer_coordinator_transition_snapshot; fixture=1'))
$requestPath = Join-Path $ArtifactRoot 'request.json'
$responsePath = Join-Path $ArtifactRoot 'response.json'
Write-GuiSmokeAtomicJson -Path $requestPath -Value ([ordered]@{
    errorMessage='original fallback projection wait failed';
    routeMode='live-regular-activation-cycle'; evidencePaths=@($inputPath); responsePath=$responsePath
})
$direction='outbound'; $payerMode='helpee'; $faultMode='switch-off'
$routeMode='live-regular-activation-cycle'; $payloadSize=268435456
try { throw 'original fallback projection wait failed' } catch { $scenarioFailure=$_ }
$caseCount=0
foreach ($case in @('located','artifact','limit','missing','null_boundary',
    'long_reason','long_event','long_id','long_hash','unsafe_reason','unsafe_event',
    'string_index','fraction_index','negative_index','outside_index','throwing_reason')) {
    $script:case=$case; $script:evaluatorCalls=0
    Invoke-TunaGuiFailureClassificationRequest -RequestPath $requestPath
    $response = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
    $classification=$response.classification
    if ($script:evaluatorCalls -ne 2 -or
        $classification.FailureClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
        $classification.Reason -cne 'coordinator_snapshot_evidence_invalid' -or
        $classification.Phase -cne 'evidence_finalization' -or
        $classification.ClassificationSource -cne 'coordinator_snapshot_evidence' -or
        -not $classification.ClassificationComplete -or $response.diagnosticEligibility.eligible) {
        throw "Primary classification changed: $case"
    }
    if ($case -ne 'null_boundary' -and ($classification.CoordinatorWriterSequence -ne 42 -or
        $classification.CoordinatorRawCount -ne 1 -or $classification.CoordinatorUnresolvedCount -ne 1)) {
        throw "Primary boundary fields changed: $case"
    }
    $receipt=New-TunaGuiFailureClassificationEnrichmentReceipt -Classification $classification -Status completed -Reason '(none)'
    $summary=& $newFailureSummary $classification 'completed' $receipt
    if ($summary.completed -or $summary.integrityOk -or $summary.tunaActive -or $summary.routeSelected -or
        $summary.failureReason -cne $classification.Reason -or $summary.error -cne $scenarioFailure.Exception.Message) {
        throw "Summary promoted diagnostic evidence: $case"
    }
    $detail = $classification.PSObject.Properties['CoordinatorSnapshotEvidenceDiagnostic']
    if ($case -in @('located','artifact','limit')) {
        if ($null -eq $detail -or $null -eq $detail.Value) { throw "Snapshot detail missing: $case" }
        $diagnostic=$detail.Value
        if ($diagnostic.authorityEffect -cne 'none' -or $diagnostic.schemaVersion -ne 1 -or
            $diagnostic.boundaryId -cne $classification.CoordinatorBoundaryId -or
            $diagnostic.boundaryHash -cne $classification.CoordinatorBoundaryHash -or
            $diagnostic.evidenceLineIndexDomain -cne 'immutable_sorted_unique_paths_concatenated_zero_based' -or
            @($diagnostic.PSObject.Properties).Count -ne 8) { throw "Invalid diagnostic shape: $case" }
        if ($case -ne 'limit' -and $diagnostic.decisionReason -cne 'snapshot_payload_hash_mismatch') {
            throw 'Selected compact reason was not preserved'
        }
        if (($case -eq 'artifact' -and ($diagnostic.evidenceLineIndex -ne -1 -or $diagnostic.eventName -cne '(artifact)')) -or
            ($case -eq 'located' -and $diagnostic.evidenceLineIndex -ne 1)) { throw 'Input index changed' }
        $json=$diagnostic | ConvertTo-Json -Depth 4 -Compress
        if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 2048 -or
            ($summary.nonAuthoritySnapshotEvidenceDiagnostic | ConvertTo-Json -Depth 4 -Compress) -cne $json) {
            throw 'Summary lost bounded diagnostic detail'
        }
    }
    elseif (($null -ne $detail -and $null -ne $detail.Value) -or
        $summary.Contains('nonAuthoritySnapshotEvidenceDiagnostic')) { throw "Unsafe detail was retained: $case" }
    $caseCount++
}
$base=New-TunaGuiFileTransferFailureClassificationResult -Phase progress_failure -Reason original_failure -RouteMode $routeMode -ClassificationSource explicit_error
$baseReceipt=New-TunaGuiFailureClassificationEnrichmentReceipt -Classification $base -Status not_requested -Reason '(none)'
$baseSummary=& $newFailureSummary $base 'completed' $baseReceipt
if ($baseSummary.Contains('nonAuthoritySnapshotEvidenceDiagnostic')) { throw 'Unrelated failure gained Snapshot detail' }

# The live-input branch selects the original full view and uses a different
# index domain. This unit control performs no GUI or network work.
function Get-AppLogLinesAfterBookmark { return @('unrelated record', 'event=filetransfer_coordinator_transition_snapshot; fixture=1') }
function Read-GuiSmokeProcessOutputLinesSnapshot { return @() }
$script:case='located'; $script:evaluatorCalls=0
$live=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage fixture -RouteMode $routeMode -RequireCompletedClassification
if ($script:evaluatorCalls -ne 2 -or
    $live.CoordinatorSnapshotEvidenceDiagnostic.decisionReason -cne 'full_view_not_selected' -or
    $live.CoordinatorSnapshotEvidenceDiagnostic.evidenceLineIndexDomain -cne 'app_log_then_process_output_zero_based') {
    throw 'Live-input diagnostic selected the wrong original view or index domain'
}

# Real evaluator integration on a new malformed offline input, not the failed
# live artifact. The original two evaluations alone must report the known first
# missing field; the wrapper only counts calls and never stores private views.
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param($Lines, $SnapshotRecords)
    $script:evaluatorCalls++
    & $script:originalSnapshotEvaluator @PSBoundParameters
}
function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative {
    param($Evaluation, $Family)
    & $script:originalFamilyPredicate @PSBoundParameters
}
[IO.File]::WriteAllLines($inputPath, [string[]]@('unrelated record',
    '[2026-01-01 00:00:00Z] [INFO] [FileTransferService] event=filetransfer_coordinator_transition_snapshot; log_writer_id=writer-fixture; log_writer_sequence=42;'))
$script:evaluatorCalls=0
Invoke-TunaGuiFailureClassificationRequest -RequestPath $requestPath
$actual=(Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json).classification
if ($script:evaluatorCalls -ne 2 -or
    $actual.FailureClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
    $actual.CoordinatorSnapshotEvidenceDiagnostic.decisionReason -cne 'snapshot_instance_missing' -or
    $actual.CoordinatorSnapshotEvidenceDiagnostic.eventName -cne 'filetransfer_coordinator_transition_snapshot' -or
    $actual.CoordinatorSnapshotEvidenceDiagnostic.evidenceLineIndex -ne 1 -or
    $actual.CoordinatorWriterSequence -ne 42) {
    throw "Real evaluator detail mismatch: $($actual | ConvertTo-Json -Depth 5 -Compress)"
}
Write-Output "gui-snapshot-failure-detail-ok cases=$caseCount; original_gui_evaluations_per_case=2"
Write-Output 'gui-snapshot-failure-detail-real-evaluator-ok'
