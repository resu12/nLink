# Negative-only decoded-row fault injection. Original native files, arrays,
# index events, receipt and primary evaluations are not edited or reevaluated.
$script:watchdogFault=$mode.Substring('watchdog-'.Length)
$script:watchdogRoot=$root
$script:watchdogRootFields=$stalls[0].Fields
$script:watchdogRecent=@($receipt.Lifecycle | Where-Object Kind -ceq 'RecentArmed')[-1].Binding
$script:watchdogRecheckFields=@($events | Where-Object {$_.EventName -ceq 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' -and
    $_.Fields.wake_generation -ceq [string]$script:watchdogRecent.WakeGeneration -and $_.Fields.decision_status -ceq 'rechecked'})[0].Fields
$script:watchdogParser=(Get-Command ConvertFrom-GuiSmokeLogLedgerStructuredFields).ScriptBlock
$script:watchdogFaultApplied=0
$script:watchdogExpected=switch($script:watchdogFault) {
    'unknown-root' {'native_frontier_stall'}
    {$_ -in @('missing-root','duplicate-root','conflicting-root')} {'native_frontier_unique_filetransfer_v6_frontier_request_deferred'}
    'reordered-root' {'native_frontier_recheck'}
    {$_ -in @('missing-clock','malformed-clock','overflow-clock')} {'native_frontier_watchdog_number'}
    {$_ -in @('root-clock-before','root-clock-after','recheck-at-deadline','recheck-after-deadline','recheck-progress','recheck-deadline')} {'native_frontier_watchdog_interval'}
    {$_ -in @('stale-recheck','wrong-recheck-generation','missing-recheck')} {'native_frontier_unique_filetransfer_v6_frontier_recent_progress_grace_recheck_decided'}
    'competing-root' {'native_frontier_watchdog_competing_root'}
    {$_ -in @('terminal','pause')} {'native_frontier_watchdog_invalidated'}
    'scope' {'native_frontier_watchdog_scope_changed'}
    'duplicate-fields' {'native_frontier_duplicate_field'}
    default {'native_frontier_watchdog_fields'}
}
function ConvertFrom-GuiSmokeLogLedgerStructuredFields {
    param([string]$Line)
    $f=& $script:watchdogParser -Line $Line
    $root=$script:watchdogRoot; $recent=$script:watchdogRecent; $fault=$script:watchdogFault
    $isRoot=$f.event -ceq 'filetransfer_v6_frontier_request_deferred' -and $f.wake_generation -ceq [string]$root.WakeGeneration -and $f.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex
    $isRecheck=$f.event -ceq 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' -and $f.wake_generation -ceq [string]$recent.WakeGeneration -and $f.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex
    $isSlot=$f.event -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and $f.wake_generation -ceq [string]$root.WakeGeneration -and $f.decision_status -ceq 'stale'
    $changed=$false
    if($isRoot) {
        $changed=$true
        switch($fault) {
            'unknown-root' {$f.reason='other'}
            'missing-root' {$f.event='not_a_stall'}
            'reordered-root' {$f=$script:watchdogRecheckFields.Clone()}
            'root-highest' {$f.highest_received_chunk_index=[string]($root.HighestReceivedChunkIndex+1)}
            'direction' {$f.direction='outbound'}
            'authority' {$f.authority_effect='route_committed'}
            'missing-elapsed' {$f.Remove('elapsed_ms')}
            'grace' {$f.stall_grace_ms='751'}
            'missing-clock' {$f.Remove('log_writer_attempt_utc_ticks')}
            'malformed-clock' {$f.log_writer_attempt_utc_ticks='bad'}
            'overflow-clock' {$f.log_writer_attempt_utc_ticks='9223372036854775808'}
            'root-clock-before' {$f.log_writer_attempt_utc_ticks=[string]($root.StartedUtcTicks-1)}
            'root-clock-after' {$f.log_writer_attempt_utc_ticks=[string]$root.DeadlineUtcTicks}
            'duplicate-fields' {$f['__nlink_duplicate_writer_evidence_field']='1'}
            default {$changed=$false}
        }
    } elseif($isRecheck) {
        $changed=$true
        switch($fault) {
            'recheck-highest' {$f.highest_received_chunk_index=[string]($recent.HighestReceivedChunkIndex+1)}
            'recheck-at-deadline' {$f.log_writer_attempt_utc_ticks=[string]$root.DeadlineUtcTicks}
            'recheck-after-deadline' {$f.log_writer_attempt_utc_ticks=[string]($root.DeadlineUtcTicks+1)}
            'recheck-progress' {$f.committed_progress_utc=[DateTimeOffset]::new([long]$recent.ProgressUtcTicks+1,[TimeSpan]::Zero).ToString('o')}
            'recheck-deadline' {$f.wake_deadline_utc=[DateTimeOffset]::new([long]$recent.DeadlineUtcTicks+1,[TimeSpan]::Zero).ToString('o')}
            'stale-recheck' {$f.decision_status='stale'}
            'wrong-recheck-generation' {$f.wake_generation='999999'}
            'missing-recheck' {$f.event='not_a_recheck'}
            'reordered-root' {$f=$script:watchdogRootFields.Clone()}
            default {$changed=$false}
        }
    } elseif($isSlot) {
        $changed=$true
        switch($fault) {
            'duplicate-root' {$f=$script:watchdogRootFields.Clone()}
            'conflicting-root' {$f=$script:watchdogRootFields.Clone(); $f.highest_received_chunk_index=[string]($root.HighestReceivedChunkIndex+1); $f.reason='other'}
            'competing-root' {
                foreach($key in $script:watchdogRootFields.Keys) {if(-not $key.StartsWith('log_writer_',[StringComparison]::Ordinal)) {$f[$key]=$script:watchdogRootFields[$key]}}
                $f.wake_generation='999999'; $f.reason='other'; $f.checkpoint_request_id='foreign'
            }
            'terminal' {$f.event='file_transfer_inbound_terminal'}
            'pause' {$f.transfer_paused='1'}
            'scope' {$f.data_session_generation='999999'}
            default {$changed=$false}
        }
    }
    if($changed) {$script:watchdogFaultApplied++}
    return $f
}
