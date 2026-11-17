# Negative-only local decoded-row/reader faults. Original native strings,
# index events, payload, physical origins and evaluated views remain untouched.
$script:dsFault=$mode.Substring('data-session-'.Length)
$script:dsRoot=$root
$script:dsProgress=@($events | Where-Object {$_.EventName -ceq 'filetransfer_fallback_frontier_repair_progress_observed' -and
    $_.Fields.current_frontier_chunk_index -ceq [string]$root.FrontierChunkIndex -and $_.Sequence -lt $failure.Event.Sequence})[-1].Fields
$script:dsParser=(Get-Command ConvertFrom-GuiSmokeLogLedgerStructuredFields).ScriptBlock
$script:dsReader=(Get-Command Read-FileTransferNativeFrontierDataSession).ScriptBlock
$script:watchdogFaultApplied=0
$script:watchdogExpected=switch($script:dsFault) {
    'wake-token' {'native_frontier_data_session'}
    'wake-generation' {'native_frontier_wake_scope'}
    'missing-both' {'native_frontier_data_session_unavailable'}
    'opaque-duplicate' {'native_frontier_duplicate_data_session'}
    'opaque-conflict' {'native_frontier_data_session_conflict'}
    {$_ -cin @('terminal','pause')} {'native_frontier_watchdog_invalidated'}
    default {'native_frontier_watchdog_scope_changed'}
}
function ConvertFrom-GuiSmokeLogLedgerStructuredFields {
    param([string]$Line)
    $f=& $script:dsParser -Line $Line
    $arm=$f['event'] -ceq 'filetransfer_v6_frontier_recent_progress_grace_deferred' -and
        $f['frontier_chunk_index'] -ceq [string]$script:dsRoot.FrontierChunkIndex
    $slot=$f['event'] -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and
        $f['wake_generation'] -ceq [string]$script:dsRoot.WakeGeneration -and $f['decision_status'] -ceq 'stale'
    $fault=$script:dsFault; $changed=$false
    if($arm -and $fault -cin @('wake-token','wake-generation') -and $script:watchdogFaultApplied -eq 0) {
        if($fault -ceq 'wake-token') {$f.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign:1'}
        else {$f.data_session_generation='999999'}
        $changed=$true
    } elseif($slot) {
        switch($fault) {
            {$_ -cin @('watchdog-token','unknown-token')} {
                $f.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign:1'
                if($fault -ceq 'unknown-token') {$f.event='filetransfer_v6_frontier_future_event'}
                $changed=$true
            }
            'watchdog-generation' {$f.data_session_generation='999999'; $changed=$true}
            'terminal' {
                $f.event='filetransfer_data_session_recreated'
                $f.Remove('data_session_identity'); $f.Remove('frontier_scope_data_session_identity'); $changed=$true
            }
            'pause' {
                $f.transfer_paused='1'; $f.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign:1'; $changed=$true
            }
        }
    }
    if($changed) {$script:watchdogFaultApplied++}
    return $f
}
function Read-FileTransferNativeFrontierDataSession {
    param([string]$Line,$Fields)
    $fault=$script:dsFault; $fields=$Fields.Clone()
    $slot=$fields['event'] -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and
        $fields['wake_generation'] -ceq [string]$script:dsRoot.WakeGeneration -and $fields['decision_status'] -ceq 'stale'
    if($slot -and $fault -ceq 'missing-both') {
        $Line=$Line -creplace ';\s*(?:data_session_identity|frontier_scope_data_session_identity)=[^;]*',''
        $fields.Remove('data_session_identity'); $fields.Remove('frontier_scope_data_session_identity')
        $script:watchdogFaultApplied++
    } elseif($fields['event'] -ceq 'filetransfer_fallback_frontier_repair_authority_granted' -and
        $fields['authority_id'] -ceq $script:dsProgress.authority_id -and $fields['repair_generation'] -ceq $script:dsProgress.repair_generation -and
        $fault -cin @('opaque-duplicate','opaque-conflict') -and $script:watchdogFaultApplied -eq 0) {
        $Line+='; data_session_identity=other:1'
        if($fault -ceq 'opaque-duplicate') {$Line+='; DATA_SESSION_IDENTITY=other:1'}
        else {$Line+='; frontier_scope_data_session_identity='+(ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign:1')}
        $fields=& $script:dsParser -Line $Line
        $script:watchdogFaultApplied++
    }
    & $script:dsReader -Line $Line -Fields $fields
}
