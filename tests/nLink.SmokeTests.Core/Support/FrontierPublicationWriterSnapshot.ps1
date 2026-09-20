param([string]$RepoRoot,[string]$InputPath,[string]$CapturePath)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$lines=[IO.File]::ReadAllLines($InputPath)
$capture=[IO.File]::ReadAllText($CapturePath) | ConvertFrom-Json
if($capture.Format -cne 'offline_frontier_publication_validation_v3') { throw 'Unsupported private capture' }
$catalog=Get-FileTransferCoordinatorEvidenceCatalog
$names=@($catalog.SnapshotAuthorityV1)+@($catalog.GenerationStampedEffects)
$compact=[Collections.Generic.List[string]]::new(); $origins=[Collections.Generic.List[int]]::new()
for($i=0;$i -lt $lines.Length;$i++) {
    if($names -ccontains (Get-FileTransferCoordinatorSnapshotEventName -Line $lines[$i])) {
        $compact.Add($lines[$i]); $origins.Add($i)
    }
}
# These are the original fresh full/compact evaluations, not a replay of any
# saved live primary. Everything below consumes their existing results only.
$fullView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$compactView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compact.ToArray()
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Additional authority evaluation forbidden' }
foreach($view in @($fullView,$compactView)) {
    if($view.Mode -cne 'SnapshotV1Authoritative' -or $view.ArtifactEvidenceVerdict -cne 'pass' -or
        -not $view.StreamComplete) { throw "Fresh source view failed: $($view.ArtifactEvidenceFailureReason)" }
}
$parity=Compare-FileTransferCoordinatorSnapshotEvaluations -Full $fullView -Compact $compactView
if($parity.Verdict -cne 'pass') { throw 'Full/compact authority parity failed' }
$beforeFull=$fullView | ConvertTo-Json -Depth 100 -Compress
$beforeCompact=$compactView | ConvertTo-Json -Depth 100 -Compress
$binding=$capture.WriterBinding
$sourceLine=[int]$capture.WriterBindingLocation.LedgerLine-1
if($sourceLine -lt 0 -or $sourceLine -ge $lines.Length -or
    $lines[$sourceLine] -cne $capture.WriterBindingLocation.RawLine) { throw 'Exact manifest origin lost' }
$manifest=ConvertFrom-GuiSmokeLogLedgerStructuredFields $lines[$sourceLine]
if($manifest.event -cne 'filetransfer_frontier_publication_receipt_closed' -or
    $manifest.frontier_receipt_hash -cne $binding.ReceiptHash -or $manifest.frontier_completion_hash -cne $binding.CompletionHash -or
    $manifest.frontier_receipt_clock -cne 'offline_injected_v1') { throw 'Manifest binding lost' }
$locations=[Collections.Generic.List[int]]::new()
foreach($kind in @('full','compact')) {
    $view=$fullView; $inputLines=$lines
    if($kind -ceq 'compact') { $view=$compactView; $inputLines=$compact.ToArray() }
    $matches=@($view.Boundaries | Where-Object {
        $_.coordinatorInstanceId -ceq $binding.CoordinatorId -and $_.boundaryId -ceq $binding.CompletionId
    })
    if($matches.Count -ne 1) { throw 'Completion boundary is not unique' }
    $boundary=$matches[0]; $index=[int]$boundary.evidenceLineIndex
    if($boundary.verdict -cne 'pass' -or $boundary.decisionKind -cne 'FallbackControlPlaneSendCompleted' -or
        $boundary.decisionStatus -cnotin @('Accepted','Idempotent') -or $index -lt 0 -or $index -ge $inputLines.Length -or
        ('ftfr1-'+$boundary.boundaryHash) -cne $binding.CompletionHash) { throw 'Completion authority reference differs' }
    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $inputLines[$index]
    if($fields.snapshot_sha256 -cne $boundary.boundaryHash -or $fields.transition_id -cne $binding.CompletionId -or
        $fields.log_writer_id -cne $manifest.log_writer_id -or
        [long]$fields.log_writer_sequence -ge [long]$manifest.log_writer_sequence) { throw 'Completion writer origin differs' }
    $physical=$index; if($kind -ceq 'compact') { $physical=$origins[$index] }
    if($physical -ge $sourceLine -or $lines[$physical] -cne $inputLines[$index]) { throw 'Completion physical order differs' }
    $locations.Add($physical)
}
if($locations[0] -ne $locations[1] -or $beforeFull -cne ($fullView | ConvertTo-Json -Depth 100 -Compress) -or
    $beforeCompact -cne ($compactView | ConvertTo-Json -Depth 100 -Compress)) { throw 'Existing authority views changed' }
# This is compatibility/prerequisite evidence, NOT a primary-owned witness
# capability, new correctness disposition, or a new payload authority evaluator.
'frontier-writer-snapshot='+(@{FullCompactParity='pass';OriginalEvaluations=2;AdditionalEvaluations=0
    ManifestLine=$sourceLine+1;CompletionLine=$locations[0]+1;LiveProofEligible=$false;PrimaryWitnessBound=$false} | ConvertTo-Json -Compress)
