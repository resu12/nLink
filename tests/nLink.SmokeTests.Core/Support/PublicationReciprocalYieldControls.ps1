param([string]$OutputDirectory,$Candidate,$Reciprocal)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Detached query controls only: these reads never provide primary custody.
. (Join-Path $PSScriptRoot 'PublicationServiceYieldMatcher.ps1')
. (Join-Path $PSScriptRoot '../../../tools/GuiSmokeStructuredFields.ps1')
. (Join-Path $PSScriptRoot '../../../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Detached controls cannot evaluate authority' }
function RClone($Value) { ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
$lines=[IO.File]::ReadAllLines((Join-Path $OutputDirectory 'filetransfer-retained-log-slice.log'))
$records=@(for($i=0;$i -lt $lines.Length;$i++) {
    $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $lines[$i]
    if($f.event -ceq 'filetransfer_fallback_tail_yield_evidence') { [pscustomobject]@{Position=$i;Fields=$f} }
})
$cases=@('missing_local','missing_copy','missing_ingress','foreign_writer','foreign_coordinator','retired_security','foreign_installation',
    'foreign_transport','foreign_generation','borrowed_idempotent','failed_selected_slot','wrong_local_receipt','wrong_peer_receipt',
    'wrong_source_episode','source_history_gap','source_invalidated','missing_release','invalid_release','wrong_remote_generation','lost_local_receipt')
foreach($view in @('full','compact')) {
    $boundaries=(Get-Content -Raw (Join-Path $OutputDirectory ('filetransfer-coordinator-snapshot-'+$view+'-summary.json')) | ConvertFrom-Json).Boundaries
    $forward=Get-ServiceYieldReceiptCandidate $records $boundaries $Candidate.Old $Candidate.Current
    $reverse=Get-ServiceYieldReciprocalCandidate $records $boundaries $Candidate.Old $Candidate.Current $forward
    YRequire ($reverse.ProofIdentity -ceq $Reciprocal.ProofIdentity) 'reciprocal_detached_baseline'
    foreach($case in $cases) {
        $rs=@(foreach($r in $records) { [pscustomobject]@{Position=$r.Position;Fields=$r.Fields.Clone()} })
        $bs=RClone $boundaries
        $local=YOne @($rs | Where-Object Position -eq $Reciprocal.LocalPosition) 'control_local'
        $copy=YOne @($rs | Where-Object Position -eq $Reciprocal.CopyPosition) 'control_copy'
        $ingress=YOne @($rs | Where-Object Position -eq $Reciprocal.IngressPosition) 'control_ingress'
        $peer=YOne @($rs | Where-Object Position -eq $Reciprocal.PeerPosition) 'control_peer'
        $pair=YOne @($bs | Where-Object boundaryId -CEQ $Reciprocal.PeerBoundary) 'control_pair'
        $op=YOne @($pair.operations | Where-Object operationRole -CEQ 'yield_current') 'control_operation'
        switch($case) {
            'missing_local' { $rs=@($rs | Where-Object Position -ne $local.Position) }
            'missing_copy' { $rs=@($rs | Where-Object Position -ne $copy.Position) }
            'missing_ingress' { $rs=@($rs | Where-Object Position -ne $ingress.Position) }
            'foreign_writer' { $peer.Fields.log_writer_id='foreign' }
            'foreign_coordinator' { $peer.Fields.coordinator_instance_id='fte1-'+('0'*24) }
            'retired_security' { $ingress.Fields.security_context_status='retired' }
            'foreign_installation' { $ingress.Fields.security_installation_id='ftsi1-'+('0'*32) }
            'foreign_transport' { $ingress.Fields.security_transport_id='ftst1-'+('0'*32) }
            'foreign_generation' { $ingress.Fields.security_installation_generation='999' }
            'borrowed_idempotent' { $peer.Fields.yield_receipt_decision='Idempotent'; $peer.Fields.yield_receipt_state_changed='0'; $peer.Fields.yield_evidence_status='diagnostic_only' }
            'failed_selected_slot' { $copy.Fields.control_copy='0'; $copy.Fields.control_ack='0'; $copy.Fields.bulk_copy='0' }
            'wrong_local_receipt' { $local.Fields.receipt_fingerprint='fte1-'+('0'*24) }
            'wrong_peer_receipt' { $peer.Fields.receipt_fingerprint='fte1-'+('0'*24) }
            'wrong_source_episode' { $peer.Fields.operation_key='foreign' }
            'source_history_gap' { $pair.beforeStateGeneration++ }
            'source_invalidated' { $op.fallbackTailYield.invalidationReason='reset' }
            'missing_release' { $op.fallbackTailYield.receipts=@($op.fallbackTailYield.receipts | Where-Object stage -CNE 'paired_release') }
            'invalid_release' { ($op.fallbackTailYield.receipts | Where-Object stage -CEQ 'paired_release').receiptValidated=$false }
            'wrong_remote_generation' { $op.fallbackTailYield.peerProofYieldGeneration++ }
            'lost_local_receipt' { $op.fallbackTailYield.receipts=@($op.fallbackTailYield.receipts | Where-Object stage -CNE 'local_proof') }
        }
        # These changes isolate the reverse edge; the original forward query
        # must still succeed, so a forward failure cannot satisfy the control.
        $f=Get-ServiceYieldReceiptCandidate $rs $bs $Candidate.Old $Candidate.Current
        $reason=''; try { $null=Get-ServiceYieldReciprocalCandidate $rs $bs $Candidate.Old $Candidate.Current $f } catch { $reason=$_.Exception.Message }
        YRequire (-not [string]::IsNullOrWhiteSpace($reason)) ('reciprocal_negative_admitted:'+ $case)
    }
}
[pscustomobject]@{NegativeControls=$cases;Views=2;AuthorityEvaluations=0;ForwardPreserved=$true}
