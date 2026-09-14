param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/PublicationUseEvidence.ps1')
$source = Join-Path $Directory 'nlink.log'
$lines = [IO.File]::ReadAllLines($source)
$hostDirectory = Join-Path $Directory ('ps' + $PSVersionTable.PSVersion.Major + '-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $hostDirectory -Force)
$writerPath = Join-Path $hostDirectory 'writer.log'
$ledger = [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::Process($source,$lines.Length,0,
    (Join-Path $hostDirectory 'retained.log'),$writerPath,$RunId,$ScenarioId,[string[]]@())
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete) {
    throw "Generated full Snapshot invalid: $($full.ArtifactEvidenceFailureReason)"
}
$names = Get-GuiSmokeSemanticEvidenceEventNames
$compactLines = [string[]]@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -in $names })
$compactPath = Join-Path $hostDirectory 'compact.log'
[IO.File]::WriteAllLines($compactPath,$compactLines,[Text.UTF8Encoding]::new($false))
$catalog=Get-FileTransferCoordinatorEvidenceCatalog
$snapshotCompactLines=[string[]]@($lines | Where-Object {
    $event=Get-FileTransferCoordinatorSnapshotEventName $_
    $event -cin $catalog.SnapshotAuthorityV1 -or $event -cin $catalog.GenerationStampedEffects
})
$snapshotCompactPath=Join-Path $hostDirectory 'snapshot-compact.log'
[IO.File]::WriteAllLines($snapshotCompactPath,$snapshotCompactLines,[Text.UTF8Encoding]::new($false))
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines ([IO.File]::ReadAllLines($snapshotCompactPath))
$parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
if ($parity.Verdict -cne 'pass') { throw "Generated Snapshot parity failed: $($parity.FailureReason)" }
function Index($Snapshot,[string]$Path,[string]$SnapshotPath) {
    New-FileTransferPublicationUseIndex -SnapshotEvidence $Snapshot -InputPath $Path `
        -InputSha256 (Get-FileTransferEvidenceSha256 $Path) `
        -WriterPath $writerPath -WriterEvidence $ledger -AcceptanceRunId $RunId -ScenarioRunId $ScenarioId `
        -SnapshotInputPath $SnapshotPath -SnapshotInputSha256 (Get-FileTransferEvidenceSha256 $SnapshotPath)
}
function Results($Index) {
    if ($Index.Reason) { throw "Input binding failed: $($Index.Reason)" }
    @(for ($i=0; $i -lt $Index.Lines.Length; $i++) {
        if ($Index.Lines[$i] -notmatch '; publication_use_kind=') { continue }
        Resolve-FileTransferPublicationUseEvidence $Index $Index.Path ($i+1) $Index.Lines[$i]
    })
}
$fullIndex = Index $full $source $source
$compactIndex = Index $compact $compactPath $snapshotCompactPath
function ExistingReferences($Index) {
    @(foreach ($line in $Index.Lines) {
        if ($line -notmatch '; publication_use_kind=') { continue }
        $event = [pscustomobject]@{Fields=(ConvertFrom-GuiSmokeLogLedgerStructuredFields $line)}
        Resolve-FileTransferPublicationEvidence $Index.Publications $event
    }) | ConvertTo-Json -Depth 5 -Compress
}
$existingBefore = ExistingReferences $fullIndex
$results = Results $fullIndex
$compactResults = Results $compactIndex
if ($results.Count -eq 0 -or @($results | Where-Object Status -CNE 'matched_local_reference').Count -ne 0) {
    throw ('Generated local references did not match: ' + ($results | ConvertTo-Json -Depth 5 -Compress))
}
function Semantic($Rows) {
    @($Rows | ForEach-Object { $_.Reference | Select-Object * -ExcludeProperty SourcePath,SourceLine } |
        Sort-Object Writer,WriterSequence) | ConvertTo-Json -Depth 5 -Compress
}
if ((Semantic $results) -cne (Semantic $compactResults)) { throw 'Consumer full/compact mismatch' }
if ((@($results.Reference.UseKind | Sort-Object -Unique) -join '|') -cne
    'receive_dispatch|v4_physical_submission|v4_pre_pump_request') { throw 'Missing generated use kinds' }
$candidateLine = [long]$results[0].Reference.SourceLine
$candidate = $fullIndex.Lines[$candidateLine-1]
if ((Resolve-FileTransferPublicationUseEvidence $fullIndex ($source+'.wrong') $candidateLine $candidate).Status -cne 'unproven' -or
    (Resolve-FileTransferPublicationUseEvidence $fullIndex $source ($candidateLine+1) $candidate).Status -cne 'unproven' -or
    (Resolve-FileTransferPublicationUseEvidence $fullIndex $source $candidateLine ($candidate+'; duplicate=1')).Status -cne 'unproven') {
    throw 'Foreign input record admitted'
}
$negativeCount = 3
function RejectLine([string]$Line,[string]$Label) {
    # Isolated predicate controls derived from the generated record. These
    # altered in-memory indexes are not new validated-input/provenance proof.
    $copy = $fullIndex.Clone(); $copy.Lines = $fullIndex.Lines.Clone()
    $copy.Lines[$candidateLine-1] = $Line
    $result = Resolve-FileTransferPublicationUseEvidence $copy $copy.Path $candidateLine $Line
    if ($result.Status -cne 'unproven' -or $null -ne $result.Reference) { throw "Negative admitted: $Label" }
    $script:negativeCount++
}
function Field([string]$Line,[string]$Name,[string]$Value) {
    $pattern = '; ' + [regex]::Escape($Name) + '=[^;\r\n]*'
    if ($Line -match $pattern) { return [regex]::Replace($Line,$pattern,('; '+$Name+'='+$Value)) }
    return $Line + '; ' + $Name + '=' + $Value
}
$fields = ConvertFrom-GuiSmokeLogLedgerStructuredFields $candidate
foreach ($name in @('publication_evidence_version','publication_evidence_coordinator_id','publication_evidence_session_id',
    'publication_evidence_transfer_id','publication_evidence_boundary_id','publication_evidence_state_generation',
    'publication_evidence_observed_state_generation','publication_evidence_operation_key','publication_evidence_token_fingerprint',
    'security_context_status','security_transport_id','security_installation_id','security_establishment_fingerprint',
    'security_payload_fingerprint','security_installation_generation','security_sequence','data_path_envelope_fingerprint',
    'data_path_session_id','data_path_transfer_id','security_session_id','security_transfer_id','publication_use_kind',
    'route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation',
    'route_publication_generation','route_commit_generation','direction')) {
    RejectLine (Field $candidate $name 'wrong') "bad:$name"
    RejectLine ([regex]::Replace($candidate,('; '+[regex]::Escape($name)+'=[^;\r\n]*'),'')) "missing:$name"
    RejectLine ($candidate+'; '+$name+'='+$fields[$name]) "duplicate:$name"
}
foreach ($name in @('publication_evidence_state_generation','publication_evidence_observed_state_generation',
    'security_installation_generation','security_sequence','runtime_generation','leg_generation','route_publication_generation')) {
    foreach ($value in @('0','-1','01','+1','1.0','9223372036854775808')) { RejectLine (Field $candidate $name $value) "$name/$value" }
}
foreach ($scope in @('session','transfer')) {
    foreach ($value in @('','foreign','prefix_[redacted]')) { RejectLine (Field $candidate ($scope+'_id') $value) "$scope/$value" }
}
foreach ($value in @('retired','unbound','missing')) { RejectLine (Field $candidate 'security_context_status' $value) "status/$value" }
RejectLine (Field $candidate 'data_session_generation' '999') 'wrong data session'
$observations = @($fullIndex.Publications.States["$($fields.publication_evidence_coordinator_id)|$($fields.publication_evidence_observed_state_generation)"])
$overlays = @($observations.afterBoundary.dataSessionGeneration | Sort-Object -Unique)
if ($overlays.Count -lt 2) { throw 'Generated fixture did not exercise idempotent data-session overlay changes' }
foreach ($overlay in $overlays) {
    RejectLine (Field $candidate 'data_session_generation' ([string]$overlay)) 'matching only one data-session overlay is insufficient'
}
RejectLine (Field $candidate 'data_session_identity' '[redacted]') 'unavailable data-session identity'
RejectLine (Field $candidate 'continuity_coordinator_id' ('fte1-'+('a'*24))) 'conflicting Coordinator'
RejectLine (Field (Field $candidate 'route' 'file_tuna_v6') 'protocol_version' '6') 'invalid Tuna6'
RejectLine (Field $candidate 'publication_evidence_observed_state_generation' '999999') 'unknown observed state'
# Equal-valued publication tuples from another real local history cannot stand
# in for the captured source Coordinator/boundary. Do not rewrite the fixture.
$peer = @($results.Reference | Where-Object CoordinatorId -CNE $fields.publication_evidence_coordinator_id)[0]
RejectLine (Field $candidate 'publication_evidence_coordinator_id' $peer.CoordinatorId) 'foreign Coordinator with equal route values'
# Only pinned unavailable forms bypass the independent raw-scope agreement.
$redacted = Field (Field $candidate 'session_id' 'sess_[redacted]') 'transfer_id' '[redacted]'
$copy=$fullIndex.Clone(); $copy.Lines=$fullIndex.Lines.Clone(); $copy.Lines[$candidateLine-1]=$redacted
if ((Resolve-FileTransferPublicationUseEvidence $copy $copy.Path $candidateLine $redacted).Status -cne 'matched_local_reference') {
    throw 'Pinned partially redacted scope did not resolve'
}
RejectLine (Field $redacted 'security_session_id' ('fte1-'+('a'*24))) 'equal placeholders cannot repair projected scope'

$reference=$results[0].Reference
foreach ($change in @('duplicate-authored','duplicate-observed','wrong-receipt','wrong-owner','wrong-direction',
    'wrong-scope','stale-observation','conflicting-state-hash')) {
    $snapshot = $full | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
    $authored = @($snapshot.Boundaries | Where-Object boundaryId -CEQ $reference.BoundaryId)[0]
    $observed = @($snapshot.Boundaries | Where-Object {
        $_.coordinatorInstanceId -ceq $reference.CoordinatorId -and $_.afterStateGeneration -eq $reference.ObservedStateGeneration })
    $op = @($authored.operations | Where-Object operationKey -CEQ $reference.OperationKey)[0]
    switch ($change) {
        'duplicate-authored' { $snapshot.Boundaries += @($authored) }
        'duplicate-observed' { $snapshot.Boundaries += @($observed[0]) }
        'wrong-receipt' { $op.receiptValidated=$false }
        'wrong-owner' { $observed[0].writer='foreign' }
        'wrong-direction' { $observed[0].direction='inbound' }
        'wrong-scope' { $observed[0].sessionId='fte1-'+('a'*24) }
        'stale-observation' { $observed[0].afterBoundary.routePublicationGeneration=999 }
        'conflicting-state-hash' { $observed[0].afterStateHash='fte1-'+('a'*24) }
    }
    $copy=$fullIndex.Clone(); $copy.Publications=New-FileTransferPublicationEvidenceIndex $snapshot
    if ((Resolve-FileTransferPublicationUseEvidence $copy $copy.Path $candidateLine $candidate).Status -cne 'unproven') {
        throw "Snapshot control admitted: $change"
    }
    $negativeCount++
}
$inputHash=Get-FileTransferEvidenceSha256 $source
foreach ($change in @('wrong-run','wrong-scenario','wrong-hash','missing-owner','writer-gap','wrong-writer-hash',
    'writer-duplicate','writer-identity','writer-inversion','writer-collision','writer-batch',
    'failed-snapshot','incomplete-snapshot','shadow-only','wrong-snapshot-location')) {
    $snapshot=$full | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
    $writer=$ledger | ConvertTo-Json -Depth 8 -Compress | ConvertFrom-Json
    $run=$RunId; $scenario=$ScenarioId; $hash=$inputHash
    switch ($change) {
        'wrong-run' { $run='other' }; 'wrong-scenario' { $scenario='other' }; 'wrong-hash' { $hash='a'*64 }
        'missing-owner' { $writer.Writers=@() }; 'writer-gap' { $writer.WriterGapCount=1 }
        'wrong-writer-hash' { $writer.WriterSha256='a'*64 }
        'writer-duplicate' { $writer.WriterDuplicateCount=1 }
        'writer-identity' { $writer.WriterIdentityMismatchCount=1 }
        'writer-inversion' { $writer.ChronologyInversionCount=1 }
        'writer-collision' { $writer.ChronologyCollisionCount=1 }
        'writer-batch' { $writer.ChronologyBatchInvalid=$true }
        'failed-snapshot' { $snapshot.ArtifactEvidenceVerdict='fail' }
        'incomplete-snapshot' { $snapshot.StreamComplete=$false }
        'shadow-only' { $snapshot.AuthoritativeFamilies=@() }
        'wrong-snapshot-location' { $snapshot.Boundaries[0].evidenceLineIndex=0 }
    }
    $copy=New-FileTransferPublicationUseIndex $snapshot $source $hash $writerPath $writer $run $scenario $source $inputHash
    if (-not $copy.Reason) { throw "Input control admitted: $change" }
    $negativeCount++
}
# A fresh hash does not make changed content part of the original writer log.
$changedLines=$lines.Clone(); $changedLines[$candidateLine-1]=Field $candidate 'security_sequence' '999'
$changedPath=Join-Path $hostDirectory 'changed-input.log'
[IO.File]::WriteAllLines($changedPath,$changedLines,[Text.UTF8Encoding]::new($false))
$changedIndex=New-FileTransferPublicationUseIndex $full $changedPath (Get-FileTransferEvidenceSha256 $changedPath) $writerPath $ledger $RunId $ScenarioId $source $inputHash
if($changedIndex.Reason -cne 'input_membership_invalid') { throw 'Rehashed foreign content bypassed original writer membership' }
$negativeCount++
$wrongSnapshotInput=New-FileTransferPublicationUseIndex $compact $source $inputHash $writerPath $ledger $RunId $ScenarioId $source $inputHash
if($wrongSnapshotInput.Reason -cne 'snapshot_input_binding_invalid') { throw 'Reduced Snapshot offsets silently remapped to full input' }
$negativeCount++
$wrongSnapshotHash=New-FileTransferPublicationUseIndex $compact $source $inputHash $writerPath $ledger $RunId $ScenarioId $snapshotCompactPath ('a'*64)
if(-not $wrongSnapshotHash.Reason) { throw 'Wrong Snapshot input hash admitted' }
$negativeCount++
$originalOperation=@($full.Boundaries | Where-Object boundaryId -CEQ $reference.BoundaryId)[0].operations |
    Where-Object operationKey -CEQ $reference.OperationKey
$originalOperation.receiptValidated=$false
if((Resolve-FileTransferPublicationUseEvidence $fullIndex $source $candidateLine $candidate).Status -cne 'matched_local_reference') {
    throw 'Consumer index aliases subsequent caller Snapshot mutation'
}
$originalOperation.receiptValidated=$true
$detached = Resolve-FileTransferPublicationUseEvidence $fullIndex $source $candidateLine $candidate
$detached.Reference.CoordinatorId = 'caller-change'
$detached.Reference.OperationKey = 'caller-change'
$again = Resolve-FileTransferPublicationUseEvidence $fullIndex $source $candidateLine $candidate
if (($again.Reference | ConvertTo-Json -Compress) -cne ($results[0].Reference | ConvertTo-Json -Compress)) {
    throw 'Consumer result aliases its validated input'
}
if ($existingBefore -cne (ExistingReferences $fullIndex) -or $existingBefore -cne (ExistingReferences $compactIndex)) {
    throw 'Existing resolver outcomes changed or lost full/compact parity'
}
# Exercise the unchanged existing matcher independently of the new consumer.
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
function Proof([string[]]$InputLines) {
    $events=@(for($i=0;$i -lt $InputLines.Length;$i++) {
        $event=ConvertFrom-FileTransferLogLine $InputLines[$i] $source ($i+1) ($i+1)
        if($null -ne $event){$event}
    })
    $proof=Get-FileTransferFileTunaV4DataPathMismatchProof -Events $events
    $proof | Select-Object RawCount,DistinctCount,RecoveredCount,UnresolvedCount,EffectiveCount | ConvertTo-Json -Compress
}
$before=Proof $lines
$ablated=[string[]]@($lines | ForEach-Object { $_ -replace '; publication_evidence_[a-z_]+=[^;\r\n]*','' -replace '; publication_use_kind=[^;\r\n]*','' })
if ($before -cne (Proof $ablated)) { throw 'Existing recovery changed under diagnostic field ablation' }
if ($inputHash -cne (Get-FileTransferEvidenceSha256 $source)) { throw 'Original generated input changed' }
'publication-use-consumer=' + ([ordered]@{Records=(Semantic $results);NegativeControls=$negativeCount;Recovery=$before} | ConvertTo-Json -Compress)
