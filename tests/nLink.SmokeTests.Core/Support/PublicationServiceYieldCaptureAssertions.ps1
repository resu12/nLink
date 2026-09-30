param([string[]]$Lines,[string]$OutputDirectory,[string]$SenderWriter,[string]$ReceiverWriter)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Post-run fixture assertions only. No primary input, recovery consumer or
# authority evaluator is created or modified by these diagnostic checks.
function YField([string]$Line,[string]$Name) {
    $matches=[regex]::Matches($Line,'(?:^|;\s*)'+[regex]::Escape($Name)+'=([^;]*)')
    if($matches.Count -ne 1) { throw "Missing or ambiguous yield field: $Name" }
    $matches[0].Groups[1].Value.Trim()
}
$rows=@($Lines | Where-Object { $_.Contains('event=filetransfer_fallback_tail_yield_evidence;') })
$sends=@($rows | Where-Object { (YField $_ 'yield_evidence_role') -ceq 'send_copy' })
$receives=@($rows | Where-Object { (YField $_ 'yield_evidence_role') -ceq 'receive_dispatch' })
$associations=@($rows | Where-Object { (YField $_ 'yield_evidence_role') -ceq 'receipt_association' })
$single=$env:NLINK_PUBLICATION_YIELD_CASE -cin @('control_only','bulk_only')
$peerCopies=if($single) {1} else {2}
if($rows.Count -ne (6+4*$peerCopies) -or $sends.Count -ne 4 -or $receives.Count -ne (2*$peerCopies) -or $associations.Count -ne (2+2*$peerCopies)) {
    throw 'Service yield capture topology changed'
}
if(@($sends | ForEach-Object { YField $_ 'yield_evidence_id' } | Select-Object -Unique).Count -ne 2 -or
    @($receives | ForEach-Object { YField $_ 'yield_evidence_id' } | Select-Object -Unique).Count -ne (2*$peerCopies)) {
    throw 'Independent diagnostic references collapsed'
}
foreach($row in $rows) {
    if((YField $row 'yield_evidence_version') -cne '1' -or (YField $row 'authority_effect') -cne 'none' -or
        (YField $row 'yield_evidence_id') -cnotmatch '^ftye1-[0-9a-f]{32}$' -or
        (YField $row 'yield_proof_identity') -cnotmatch '^fte1-[0-9a-f]{24}$' -or
        (YField $row 'yield_frame_fingerprint') -cnotmatch '^fte1-[0-9a-f]{24}$') { throw 'Diagnostic shape/authority changed' }
}
foreach($writer in @($SenderWriter,$ReceiverWriter)) {
    $local=@($associations | Where-Object { (YField $_ 'log_writer_id') -ceq $writer -and (YField $_ 'yield_receipt_stage') -ceq 'local_proof' })
    $peer=@($associations | Where-Object { (YField $_ 'log_writer_id') -ceq $writer -and (YField $_ 'yield_receipt_stage') -ceq 'peer_proof' })
    if($local.Count -ne 1 -or $peer.Count -ne $peerCopies -or (YField $local[0] 'yield_receipt_decision') -cne 'Accepted' -or
        @($peer | Where-Object { (YField $_ 'yield_receipt_decision') -ceq 'Accepted' }).Count -ne 1 -or
        @($peer | Where-Object { (YField $_ 'yield_receipt_decision') -ceq 'Idempotent' }).Count -ne ($peerCopies-1)) { throw 'Service receipt lifecycle changed' }
    $copies=@($sends | Where-Object { (YField $_ 'log_writer_id') -ceq $writer })
    if($copies.Count -ne 2 -or ((@($copies | ForEach-Object { YField $_ 'yield_evidence_copy' } | Sort-Object) -join '|') -cne 'bulk|control')) {
        throw 'Expected original control and bulk slots'
    }
    foreach($field in @('data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence')) {
        if((YField $copies[0] $field) -ceq (YField $copies[1] $field)) { throw "Copy slots reused $field" }
    }
    foreach($copy in $copies) {
        foreach($field in @('yield_evidence_id','yield_proof_identity','yield_frame_fingerprint')) {
            if((YField $copy $field) -cne (YField $local[0] $field)) { throw "Original local completion reference changed: $field" }
        }
        $slot=YField $copy 'yield_evidence_copy'
        $sent=-not $single -or $env:NLINK_PUBLICATION_YIELD_CASE -ceq ($slot+'_only')
        if((YField $copy 'yield_evidence_status') -cne 'bound' -or
            (YField $copy ($slot+'_copy')) -cne $(if($sent) {'1'} else {'0'}) -or
            [long](YField $copy 'log_writer_sequence') -ge [long](YField $local[0] 'log_writer_sequence')) { throw 'Local receipt preceded original physical completion' }
        $received=@($receives | Where-Object { (YField $_ 'data_path_envelope_fingerprint') -ceq (YField $copy 'data_path_envelope_fingerprint') })
        if(-not $sent) {
            if($received.Count -ne 0) { throw 'Unavailable copy was delivered' }
            continue
        }
        if($received.Count -ne 1 -or (YField $received[0] 'log_writer_id') -ceq $writer -or
            (YField $received[0] 'yield_evidence_status') -cne 'bound' -or
            (YField $received[0] 'security_installation_id') -ceq (YField $copy 'security_installation_id')) { throw 'Original authenticated peer dispatch missing' }
        foreach($field in @('yield_proof_identity','yield_frame_fingerprint','yield_evidence_copy','security_payload_fingerprint',
            'security_sequence','security_establishment_fingerprint','data_path_session_id','data_path_transfer_id')) {
            if((YField $received[0] $field) -cne (YField $copy $field)) { throw "Per-copy authentication changed: $field" }
        }
    }
}
foreach($association in $associations) {
    $accepted=(YField $association 'yield_receipt_decision') -ceq 'Accepted'
    if($accepted -and ((YField $association 'receipt_identity') -cne (YField $association 'yield_proof_identity') -or
        (YField $association 'receipt_status') -cne 'Observed')) { throw 'Accepted receipt is not the captured canonical proof' }
    if((YField $association 'yield_evidence_status') -cne $(if($accepted) {'bound'} else {'diagnostic_only'}) -or
        (YField $association 'yield_receipt_state_changed') -cne $(if($accepted) {'1'} else {'0'}) -or
        (YField $association 'receipt_stage') -cne $(if($accepted) {YField $association 'yield_receipt_stage'} else {'attempted_decision'})) {
        throw 'Duplicate invocation borrowed an accepted association'
    }
    if((YField $association 'yield_receipt_stage') -ceq 'peer_proof') {
        $ingress=@($receives | Where-Object { (YField $_ 'yield_evidence_id') -ceq (YField $association 'yield_evidence_id') })
        if($ingress.Count -ne 1 -or (YField $ingress[0] 'log_writer_id') -cne (YField $association 'log_writer_id') -or
            [long](YField $ingress[0] 'log_writer_sequence') -ge [long](YField $association 'log_writer_sequence')) { throw 'Original ingress handoff missing' }
        foreach($field in @('yield_proof_identity','yield_frame_fingerprint')) {
            if((YField $ingress[0] $field) -cne (YField $association $field)) { throw 'Receipt substituted its authenticated proof' }
        }
    }
    foreach($view in @('full','compact')) {
        $summary=[IO.File]::ReadAllText((Join-Path $OutputDirectory ('filetransfer-coordinator-snapshot-'+$view+'-summary.json'))) | ConvertFrom-Json
        $boundary=@($summary.Boundaries | Where-Object { $_.boundaryId -ceq (YField $association 'transition_id') -and
            $_.coordinatorInstanceId -ceq (YField $association 'coordinator_instance_id') -and $_.writer -ceq (YField $association 'log_writer_id') })
        if($boundary.Count -ne 1 -or $boundary[0].afterStateGeneration -ne [long](YField $association 'coordinator_state_generation') -or
            $boundary[0].decisionStatus -cne (YField $association 'yield_receipt_decision')) { throw "Original $view receipt boundary changed" }
        $operation=@($boundary[0].operations | Where-Object { $_.operationKey -ceq (YField $association 'operation_key') -and
            $_.authorityId -ceq (YField $association 'operation_authority_id') -and $_.operationGeneration -eq [long](YField $association 'operation_generation') })
        if($operation.Count -ne 1 -or $null -eq $operation[0].fallbackTailYield) { throw 'Original receipt operation missing' }
        $receipt=@($operation[0].fallbackTailYield.receipts | Where-Object { $_.stage -ceq (YField $association 'receipt_stage') -and
            $_.receiptIdentity -ceq (YField $association 'receipt_identity') -and $_.receiptGeneration -eq [long](YField $association 'receipt_generation') -and
            $_.receiptStatus -ceq (YField $association 'receipt_status') -and $_.receiptFingerprint -ceq (YField $association 'receipt_fingerprint') })
        if($receipt.Count -ne 1 -or ($accepted -and -not $receipt[0].receiptValidated)) { throw 'Association is not its exact original Coordinator receipt' }
    }
}
