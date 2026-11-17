param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog=Get-FileTransferCoordinatorFallbackRepairHandoffCatalog
$parent=Get-FileTransferCoordinatorEarlyCheckpointProofCatalog
if ($parent.Hash -cne 'a5589d2ced4470105f2643a73921f2f9918e97804043581b9b10ef2c367c5be4' -or
    $catalog.ParentCatalogHash -cne $parent.Hash) { throw 'Frozen parent catalog changed' }
$lines=[IO.File]::ReadAllLines($InputPath)
$envelopes=@(foreach ($line in $lines) {
    if ((Get-FileTransferCoordinatorSnapshotEventName $line) -ceq 'filetransfer_coordinator_transition_snapshot') {
        (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line 'snapshot_payload')).Value
    }
})
$entries=@(for ($i=0; $i -lt $envelopes.Count; $i++) {
    $e=$envelopes[$i]
    [pscustomobject]@{SessionId=$e.sessionId;TransferId=$e.transferId;Direction=$e.direction;Boundary=[pscustomobject]@{
        coordinatorInstanceId=$e.coordinatorInstanceId;afterStateGeneration=$e.aggregate.afterStateGeneration;
        evidenceLineIndex=$i;operations=$e.aggregate.operations}}
})
$handoffs=@(foreach ($e in $envelopes) {
    foreach ($o in @($e.aggregate.operations | Where-Object { Test-FileTransferFallbackRepairHandoffMarker $_ })) {
        if (-not (Test-FileTransferFallbackRepairHandoffOperation $e $o $catalog)) { throw "Exact operation rejected: $($e.decisionKind):$($o.beforeState)->$($o.afterState)" }
        if (Test-FileTransferFallbackRepairHandoffOperation $e $o $parent) { throw 'Frozen parent accepted new handoff' }
        $effect=[pscustomobject]@{ReceiptIdentity=$o.receiptIdentity;ReceiptGeneration=$o.receiptGeneration;
            ReceiptStatus=$o.receiptStatus;ReceiptFingerprint=$o.receiptFingerprint}
        if ((Test-FileTransferCoordinatorSnapshotEffectScope $o $effect) -or
            (Test-FileTransferCoordinatorSnapshotEffectReceipt $o $effect)) { throw 'Handoff evidence granted physical authority' }
        [pscustomobject]@{Envelope=$e;Operation=$o}
    }
})
if ($handoffs.Count -lt 4) { throw 'Fixture lacks lifecycle' }
$errors=@(Get-FileTransferFallbackRepairHandoffHistoryErrors $entries)
if ($errors.Count) { throw "Exact lifecycle rejected: $errors" }
function Clone($value) {
    $copy=ConvertFrom-Json (ConvertTo-Json $value -Depth 64 -Compress)
    foreach ($item in $copy) { $item }
}
$drained=@($handoffs | Where-Object { $_.Operation.afterState -ceq 'Drained' -and $_.Operation.beforeState -ceq 'Captured' })[0]
foreach ($case in @('source','pause','settled-write','settled-feedback','gaps','checkpoint','receipt','before-state','untyped','tuna-v6','direction','extra-field')) {
    $e=Clone $drained.Envelope; $o=@($e.aggregate.operations | Where-Object operationRole -ceq 'repair_handoff')[0]; $p=$o.fallbackRepairHandoff
    switch ($case) {
        source { $p.sourceBoundary.dataSessionGeneration++ }
        pause { $p.pauseInstance=0 }
        settled-write { $p.settledWrites=@(11) }
        settled-feedback { $p.settledFeedback=@(22) }
        gaps { $p.remainingGaps[0].startChunkIndex++ }
        checkpoint { $p.drainCheckpoint.checkpointGeneration++ }
        receipt { $p.receipts[2].receiptValidated=$false }
        before-state { $o.beforeState='Prepared' }
        untyped { $o.fallbackRepairHandoff=$null }
        tuna-v6 { $p.sourceBoundary.route='file_tuna_v6' }
        direction { $e.direction='outbound' }
        extra-field { $p | Add-Member -NotePropertyName unlocked -NotePropertyValue $true }
    }
    if (Test-FileTransferFallbackRepairHandoffOperation $e $o $catalog) { throw "$case mutation accepted" }
}
foreach ($case in @('capture-changed','drain-changed','missing-capture','missing-source')) {
    $copy=@(Clone $entries)
    $target=@($copy | Where-Object { @($_.Boundary.operations | Where-Object { $_.operationRole -ceq 'repair_handoff' -and $_.afterState -ceq 'Drained' -and $_.beforeState -ceq 'Captured' }).Count })[0]
    $o=@($target.Boundary.operations | Where-Object operationRole -ceq 'repair_handoff')[0]
    switch ($case) {
        capture-changed { $o.fallbackRepairHandoff.pauseInstance++ }
        drain-changed {
            $end=@($copy | Where-Object { @($_.Boundary.operations | Where-Object { $_.operationRole -ceq 'repair_handoff' -and $_.afterState -cin @('Adopted','Restored') }).Count })[0]
            @($end.Boundary.operations | Where-Object operationRole -ceq 'repair_handoff')[0].fallbackRepairHandoff.drainCheckpoint.stateEpoch++
        }
        missing-capture { $copy=@($copy | Where-Object { @($_.Boundary.operations | Where-Object { $_.operationRole -ceq 'repair_handoff' -and $_.afterState -ceq 'Captured' -and $_.beforeState -ceq 'Prepared' }).Count -eq 0 }) }
        missing-source { $copy=@($copy | Where-Object { @($_.Boundary.operations | Where-Object { $_.operationRole -ceq 'repair_handoff' -and $_.afterState -ceq 'Prepared' -and $_.beforeState -ceq 'None' }).Count -eq 0 }) }
    }
    if (@(Get-FileTransferFallbackRepairHandoffHistoryErrors $copy).Count -eq 0) { throw "$case history accepted" }
}
$terminal=@($handoffs | Where-Object { $_.Operation.afterState -cin @('Adopted','Restored') -and $_.Operation.beforeState -ceq 'Drained' })[0]
$e=Clone $terminal.Envelope; $o=@($e.aggregate.operations | Where-Object operationRole -ceq 'repair_handoff')[0]
if ($o.afterState -ceq 'Restored') {
    $o.fallbackRepairHandoff.restoredGaps=@()
    if (Test-FileTransferFallbackRepairHandoffOperation $e $o $catalog) { throw 'Restore lost gaps accepted' }
} else {
    $e.aggregate.afterBoundary.route='file_tuna_v6'; $e.aggregate.afterBoundary.protocolVersion=6
    if (Test-FileTransferFallbackRepairHandoffOperation $e $o $catalog) { throw 'Tuna v6 adoption accepted' }
}
if ($terminal.Operation.fallbackRepairHandoff.dispositionKind -cne '(none)') {
    $copy=@(Clone $entries)
    foreach ($entry in $copy) { foreach ($op in @($entry.Boundary.operations | Where-Object operationRole -ceq 'repair_handoff')) {
        if ($op.afterState -cin @('Adopted','Restored')) { $op.fallbackRepairHandoff.transitionAuthorityId='fte1-000000000000000000000000' }
    }}
    if (@(Get-FileTransferFallbackRepairHandoffHistoryErrors $copy).Count -eq 0) { throw 'Unbound disposition accepted' }
}
$full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$records=@(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i];LineIndex=$i} })
$compact=Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
if ($terminal.Operation.afterState -ceq 'Restored' -and $terminal.Operation.fallbackRepairHandoff.dispositionKind -ceq '(none)') {
    if ($full.ArtifactEvidenceVerdict -cne 'pass') { throw "Closed-source restoration invalid: $($full.ArtifactEvidenceFailureReason)" }
} elseif ($full.ArtifactEvidenceVerdict -cne 'fail' -or
    @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -cnotmatch '^snapshot_responder_probe_handoff_required_effect_missing:' }).Count -ne 0) {
    throw "Modeled transition failed outside absent physical effects: $($full.ArtifactEvidenceFailureReason)"
}
if ($full.ArtifactEvidenceFailureReason -cmatch 'snapshot_fallback_repair_handoff' -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) {
    throw "Full/compact handoff proof invalid: $($full.ArtifactEvidenceFailureReason)"
}
Write-Output "fallback-repair-handoff-evidence-ok; overall=$($full.ArtifactEvidenceVerdict); unresolved=$($full.ArtifactEvidenceFailureReason)"
