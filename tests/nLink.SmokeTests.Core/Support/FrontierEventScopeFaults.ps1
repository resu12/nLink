# Negative-only decoded-row injection: no native source, payload, original
# event or evaluation is changed; these cases must never produce a witness.
$script:scopeFault=$mode.Substring('scope-fault-'.Length)
$script:scopeFailureLine=$failure.Event.RawLine
$script:scopeRoot=$root
$script:scopeProgress=@($events | Where-Object {$_.EventName -ceq 'filetransfer_fallback_frontier_repair_progress_observed' -and
    $_.Fields.current_frontier_chunk_index -ceq [string]$script:scopeRoot.FrontierChunkIndex -and
    $_.Sequence -lt $failure.Event.Sequence})[-1].Fields
$script:scopeParser=(Get-Command ConvertFrom-GuiSmokeLogLedgerStructuredFields).ScriptBlock
$script:watchdogFaultApplied=0
$script:watchdogExpected=if($script:scopeFault.StartsWith('foreign-',[StringComparison]::Ordinal)) {'native_frontier_receipt_ambiguity'}
    elseif($script:scopeFault -ceq 'conflict') {'native_frontier_scope_conflict'}
    elseif($script:scopeFault.StartsWith('unscoped-',[StringComparison]::Ordinal)) {'native_frontier_scope_invalid'}
    else {'native_frontier_watchdog_invalidated'}
function ConvertFrom-GuiSmokeLogLedgerStructuredFields {
    param([string]$Line)
    $f=& $script:scopeParser -Line $Line
    $fault=$script:scopeFault
    $selected=$Line -ceq $script:scopeFailureLine
    $slot=$f.event -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and
        $f.wake_generation -ceq [string]$script:scopeRoot.WakeGeneration -and $f.decision_status -ceq 'stale'
    $link=$f.event -ceq 'filetransfer_fallback_frontier_repair_authority_granted' -and
        $f['authority_id'] -ceq $script:scopeProgress.authority_id -and $f['repair_generation'] -ceq $script:scopeProgress.repair_generation
    if($selected -and ($fault.StartsWith('foreign-',[StringComparison]::Ordinal) -or $fault -ceq 'conflict')) {
        if($fault -cne 'conflict') {$f.session_id='[redacted]';$f.transfer_id='[redacted]'}
        if($fault -cin @('foreign-session','foreign-both','conflict')) {$f.frontier_scope_session_id=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign-session'}
        if($fault -cin @('foreign-transfer','foreign-both')) {$f.frontier_scope_transfer_id=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign-transfer'}
        $script:watchdogFaultApplied++
    } elseif(($slot -and $fault -cne 'unscoped-link' -and -not $fault.StartsWith('foreign-',[StringComparison]::Ordinal) -and $fault -cne 'conflict') -or
        ($link -and $fault -ceq 'unscoped-link' -and $script:watchdogFaultApplied -eq 0)) {
        $name=$fault -creplace '^unscoped-',''
        if($name.StartsWith('terminal-',[StringComparison]::Ordinal)) {$f.event=$name.Substring('terminal-'.Length)}
        elseif($name.StartsWith('pause-',[StringComparison]::Ordinal)) {$f.event=$name.Substring('pause-'.Length);$f.transfer_paused='1'}
        elseif($name -ceq 'competing') {$f.event='filetransfer_v6_frontier_request_deferred'}
        elseif($name -ceq 'future') {$f.event='filetransfer_v6_frontier_future_event'}
        elseif($name -ceq 'link') {$f.event='future_preprogress_repair_link'}
        if($fault.StartsWith('unscoped-',[StringComparison]::Ordinal)) {
            $f.Remove('frontier_scope_session_id'); $f.Remove('frontier_scope_transfer_id')
            $f.session_id='[redacted]';$f.transfer_id='[redacted]'
        }
        $script:watchdogFaultApplied++
    }
    return $f
}
