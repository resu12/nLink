param([string]$RepoRoot, [string]$SourcePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'GUI parse failed' }
$gui = $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure'}, $true)
if ($gui.Count -ne 1) { throw 'Expected the actual GUI recent-progress consumer' }
Invoke-Expression $gui[0].Extent.Text

# Fresh service-issued source/repair receipts, not live-capture replay. Only the
# local ordering/clock projection and subsequent physical observations are synthetic.
$sourceLines = @(Get-Content -LiteralPath $SourcePath)
$source = @(for ($i = 0; $i -lt $sourceLines.Count; $i++) {
    ConvertFrom-FileTransferLogLine -Line $sourceLines[$i] -FilePath $SourcePath -LineNumber ($i + 1) -Sequence ($i + 1)
})
$progress = @($source | Where-Object EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed')
if ($progress.Count -ne 1) { throw 'Expected one real repair progress receipt' }
$progress = $progress[0]
$authority = $progress.Fields.authority_id
$base = @(foreach ($name in @('filetransfer_fallback_frontier_repair_authority_granted',
    'filetransfer_control_plane_delivery_observed', 'filetransfer_fallback_frontier_repair_dispatched',
    'filetransfer_v6_frontier_request_sent', 'filetransfer_fallback_frontier_repair_contract_settled',
    'filetransfer_fallback_frontier_repair_progress_observed',
    'filetransfer_v6_fallback_frontier_repair_progress_state_forced', 'filetransfer_v6_receiver_state_sent')) {
    $selected = @($source | Where-Object {
        $_.EventName -ceq $name -and
        ($_.Fields['authority_id'] -ceq $authority -or $name -in @(
            'filetransfer_control_plane_delivery_observed', 'filetransfer_v6_receiver_state_sent')) -and
        $_.Fields.ContainsKey('continuity_source_fingerprint')
    })
    if ($selected.Count -ne 1) { throw "Expected one bound service receipt: $name ($($selected.Count))" }
    $selected[0]
})
if ($base[4].Fields.settlement_outcome -cne 'ProgressObserved' -or
    $base[6].Fields.decision_status -cnotin @('Accepted', 'Idempotent') -or
    $base[6].Fields.current_frontier_chunk_index -cne $base[7].Fields.contiguous_committed_chunk_index) {
    throw 'Service receipt chain is not progress-settled and published'
}
$origin = [DateTimeOffset]::Parse('2026-09-15T15:00:00Z')
$frontier = [long]$progress.Fields.current_frontier_chunk_index
$scope = @{}
foreach ($name in @('log_writer_id', 'session_id', 'transfer_id', 'direction', 'route', 'protocol_version',
    'live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation', 'transport_epoch',
    'bridge_recovery_generation', 'bridge_decision_generation', 'checkpoint_request_id', 'checkpoint_generation',
    'route_publication_generation', 'route_commit_generation')) {
    $scope[$name] = $progress.Fields[$name]
}
$scope.data_session_identity = $base[6].Fields.data_session_identity
$scope.data_session_generation = $base[6].Fields.data_session_generation
# Accepted checkpoints clear the lease but retain the leg's generation. The
# existing service fixture asserts that invariant; repair tuples omit this field.
$sourceHold = @($source | Where-Object EventName -eq 'filetransfer_v6_frontier_request_deferred')
if ($sourceHold.Count -ne 1 -or [long]$sourceHold[0].Fields.checkpoint_generation -le 0) {
    throw 'Expected the service-issued retained checkpoint generation'
}
$scope.checkpoint_generation = $sourceHold[0].Fields.checkpoint_generation
$physical = @{log_writer_id=$scope.log_writer_id;session_id=$scope.session_id;transfer_id=$scope.transfer_id}
$highest = [long]$base[7].Fields.durable_received_highest_chunk_index
$stateEpoch = [long]$base[7].Fields.epoch

function Add([string]$name, [int]$ms, [hashtable]$fields, [hashtable]$extra = @{}) {
    $f = $fields.Clone()
    foreach ($key in $extra.Keys) { $f[$key] = [string]$extra[$key] }
    $f.event = $name
    $f.log_writer_sequence = [string](10 * ($script:events.Count + 1))
    $f.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds($ms).UtcTicks
    $script:events.Add([pscustomobject]@{ EventName=$name; Fields=$f; Sequence=[long](10 * ($script:events.Count + 1));
        LineNumber=10 * ($script:events.Count + 1); FilePath='ordering-fixture'; TimestampUtc=$origin.AddMilliseconds($ms) })
}
function Fixture([bool]$rearm, [bool]$late) {
    $script:events = [Collections.Generic.List[object]]::new()
    $times = @(0, 1, 2, 3, 4, 100, 101, 102)
    for ($i = 0; $i -lt $base.Count; $i++) { Add $base[$i].EventName $times[$i] $base[$i].Fields }
    $wake = $scope.Clone(); $wake.frontier_chunk_index = [string]$frontier
    $wake.highest_received_chunk_index = [string]$highest; $wake.authority_effect = 'none'
    $recent = @{wake_generation='167'; committed_progress_utc=$origin.AddMilliseconds(100).ToString('o');
        wake_deadline_utc=$origin.AddMilliseconds(1100).ToString('o')}
    Add 'filetransfer_v6_frontier_recent_progress_grace_deferred' 140 $wake ($recent + @{decision_status='held';decision_reason='recent_progress_grace_current'})
    Add 'filetransfer_v6_receiver_state_deferred' 150 $physical @{route=$scope.route;protocol_version=$scope.protocol_version;
        next_chunk_index=$frontier;highest_received_chunk_index=$highest;reason='frontier_stalled'}
    if ($rearm) {
        $wake.highest_received_chunk_index = [string]($highest + 3); $recent.wake_generation = '168'
        Add 'filetransfer_v6_frontier_recent_progress_grace_deferred' 500 $wake ($recent + @{decision_status='rearmed';decision_reason='highest_received_advanced'})
        $wake.highest_received_chunk_index = [string]($highest + 6); $recent.wake_generation = '169'
        Add 'filetransfer_v6_frontier_recent_progress_grace_deferred' 900 $wake ($recent + @{decision_status='rearmed';decision_reason='highest_received_advanced'})
    }
    Add 'filetransfer_v6_frontier_request_deferred' 1105 $wake @{elapsed_ms='0';stall_grace_ms='750';wake_generation='258';
        reason='frontier_recent_progress_grace_elapsed';stall_started_utc=$origin.AddMilliseconds(1105).ToString('o');wake_deadline_utc=$origin.AddMilliseconds(1855).ToString('o')}
    Add 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' 1106 $wake ($recent + @{decision_status='rechecked';decision_reason='stall_grace_armed'})
    if ($rearm) {
        $old = $events[8].Fields.Clone()
        Add 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' 1107 $old @{decision_status='stale';decision_reason='wake_generation_replaced'}
    }
    Add 'filetransfer_v6_sparse_write_committed' 1400 $physical @{contiguous_chunks_committed='3';next_chunk_index=($frontier + 3);highest_received_chunk_index=($highest + 9)}
    Add 'filetransfer_v6_sparse_write_committed' 1600 $physical @{contiguous_chunks_committed='3';next_chunk_index=($frontier + 6);highest_received_chunk_index=($highest + 9)}
    Add 'filetransfer_v6_receiver_state_sent' $(if ($late) { 2102 } else { 1750 }) $physical @{epoch=($stateEpoch + 1);transport_epoch=$scope.transport_epoch;
        contiguous_committed_chunk_index=($frontier + 6);durable_received_highest_chunk_index=($highest + 9);transfer_paused='0'}
    Add 'unrelated_diagnostic' 2200 $physical @{note='outside the grace event catalog'}
    return $events.ToArray()
}
$cases = @('exact', 'rearm_only', 'late_only', 'live_ordering', 'session', 'transfer', 'runtime', 'publication',
    'leg', 'checkpoint', 'data', 'deadline', 'wrong_wake', 'missing_recheck', 'stale_only', 'cross_family_collision',
    'no_publication', 'chain_duplicate', 'chain_no_stale', 'chain_family_same', 'chain_earlier_root',
    'chain_missing_root', 'chain_missing_middle', 'chain_missing_final', 'chain_generation_gap',
    'chain_generation_reused', 'chain_highest_flat', 'chain_highest_regressed', 'chain_root_clock',
    'chain_clock_regressed', 'chain_at_deadline', 'chain_after_deadline', 'chain_latest_bad_root',
    'chain_conflict_session', 'chain_conflict_transfer', 'chain_conflict_event', 'chain_no_publication',
    'chain_terminal', 'chain_pause', 'chain_source_replaced', 'chain_stale_only',
    'chain_current_generation_reused', 'chain_latest_matching_root', 'chain_unrelated_transfer_conflict')
$scopeChanges = @('session_id', 'transfer_id', 'log_writer_id', 'direction', 'route', 'protocol_version',
    'live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation', 'transport_epoch',
    'bridge_recovery_generation', 'bridge_decision_generation', 'checkpoint_request_id', 'checkpoint_generation',
    'route_publication_generation', 'route_commit_generation', 'data_session_identity', 'data_session_generation',
    'frontier_chunk_index', 'committed_progress_utc', 'wake_deadline_utc', 'decision_status', 'decision_reason', 'authority_effect')
$cases += @($scopeChanges | ForEach-Object { "chain_scope_$_" })
function CopyEvent($event) { $copy = $event.PSObject.Copy(); $copy.Fields = $event.Fields.Clone(); return $copy }
function Boundary($result) {
    $records = @(foreach ($record in $result.CandidateRecords) { [ordered]@{
        writer=$record.Event.Fields.log_writer_id; sequence=$record.Event.Fields.log_writer_sequence;
        reason=$record.Reason; variant=$record.Variant; recovered=$record.Recovered } })
    ConvertTo-Json -InputObject ([ordered]@{raw=$result.RawCount;distinct=$result.DistinctCount;
        recovered=$result.RecoveredCount;unresolved=$result.UnresolvedCount;records=$records}) -Depth 8 -Compress
}
foreach ($case in $cases) {
    $items = @(Fixture ($case.StartsWith('chain_') -or $case -in @('rearm_only', 'live_ordering')) ($case -in @('late_only', 'live_ordering')))
    $recheck = @($items | Where-Object { $_.EventName -eq 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' -and $_.Fields.decision_status -eq 'rechecked' })[0]
    $arms = @($items | Where-Object EventName -eq 'filetransfer_v6_frontier_recent_progress_grace_deferred')
    $stall = @($items | Where-Object EventName -eq 'filetransfer_v6_frontier_request_deferred')[0]
    if ($case.StartsWith('chain_scope_')) { $arms[1].Fields[$case.Substring('chain_scope_'.Length)] = 'other' }
    switch ($case) {
        {$_ -in @('no_publication', 'chain_no_publication')} {
            $items = @($items | Where-Object { $_.EventName -ne 'filetransfer_v6_receiver_state_sent' -or $_.Sequence -lt 100 })
        }
        'chain_duplicate' { $items += (CopyEvent $arms[1]) }
        'chain_no_stale' { $items = @($items | Where-Object { $_.Fields['decision_status'] -cne 'stale' }) }
        'chain_family_same' { $stall.Fields.wake_generation = $recheck.Fields.wake_generation }
        'chain_earlier_root' {
            $copy = CopyEvent $arms[0]; $copy.Sequence = 85L; $copy.LineNumber = 85; $copy.Fields.log_writer_sequence = '85'
            $copy.Fields.wake_generation = '166'; $copy.Fields.highest_received_chunk_index = [string]($highest - 3)
            $copy.Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(130).UtcTicks; $items += $copy
        }
        'chain_missing_root' { $items = @($items | Where-Object { $_ -ne $arms[0] }) }
        'chain_missing_middle' { $items = @($items | Where-Object { $_ -ne $arms[1] }) }
        'chain_missing_final' { $items = @($items | Where-Object { $_ -ne $arms[2] }) }
        'chain_generation_gap' { $arms[1].Fields.wake_generation = '170' }
        'chain_generation_reused' { $arms[1].Fields.wake_generation = '167' }
        'chain_highest_flat' { $arms[1].Fields.highest_received_chunk_index = [string]$highest }
        'chain_highest_regressed' { $arms[1].Fields.highest_received_chunk_index = [string]($highest - 1) }
        'chain_root_clock' { $arms[0].Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(151).UtcTicks }
        'chain_clock_regressed' { $arms[2].Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(499).UtcTicks }
        'chain_at_deadline' { $arms[2].Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(1100).UtcTicks }
        'chain_after_deadline' { $arms[2].Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(1101).UtcTicks }
        'chain_latest_bad_root' {
            $copy = CopyEvent $arms[0]; $copy.Sequence = 95L; $copy.LineNumber = 95; $copy.Fields.log_writer_sequence = '95'
            $copy.Fields.runtime_generation = '999'; $copy.Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(145).UtcTicks
            $items += $copy
        }
        {$_ -in @('chain_conflict_session', 'chain_conflict_transfer', 'chain_conflict_event')} {
            $copy = CopyEvent $arms[1]; $copy.LineNumber++
            if ($case -eq 'chain_conflict_session') { $copy.Fields.session_id = 'other' }
            elseif ($case -eq 'chain_conflict_transfer') { $copy.Fields.transfer_id = 'other' }
            else { $copy.Fields.event = 'filetransfer_v6_receiver_state_sent'; $copy.EventName = $copy.Fields.event }
            $items += $copy
        }
        {$_ -in @('chain_terminal', 'chain_pause', 'chain_source_replaced')} {
            $copy = CopyEvent $arms[1]; $copy.Sequence = 115L; $copy.LineNumber = 115; $copy.Fields.log_writer_sequence = '115'
            $copy.Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(600).UtcTicks
            $copy.Fields.event = switch ($case) { 'chain_terminal' { 'file_transfer_inbound_terminal' }
                'chain_pause' { 'filetransfer_v6_receiver_state_sent' } 'chain_source_replaced' { 'filetransfer_data_session_recreated' } }
            $copy.EventName = $copy.Fields.event
            if ($case -eq 'chain_pause') { $copy.Fields.transfer_paused = '1' }
            $items += $copy
        }
        'chain_stale_only' { $recheck.Fields.decision_status = 'stale'; $recheck.Fields.decision_reason = 'wake_generation_replaced' }
        'chain_current_generation_reused' { $recheck.Fields.wake_generation = $arms[0].Fields.wake_generation }
        'chain_latest_matching_root' {
            $copy = CopyEvent $arms[0]; $copy.Sequence = 95L; $copy.LineNumber = 95; $copy.Fields.log_writer_sequence = '95'
            $copy.Fields.wake_generation = $recheck.Fields.wake_generation
            $copy.Fields.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds(145).UtcTicks
            $items += $copy
        }
        'chain_unrelated_transfer_conflict' {
            $copy = CopyEvent $arms[1]; $copy.Sequence = 115L; $copy.LineNumber = 115; $copy.Fields.log_writer_sequence = '115'
            $copy.Fields.session_id = 'other'; $copy.Fields.transfer_id = 'other'
            $conflict = CopyEvent $copy; $conflict.LineNumber++; $conflict.Fields.highest_received_chunk_index = '999'
            $items += $copy; $items += $conflict
        }
        'session' { $recheck.Fields.session_id = 'other' }
        'transfer' { $recheck.Fields.transfer_id = 'other' }
        'runtime' { $recheck.Fields.runtime_generation = '999' }
        'publication' { $recheck.Fields.route_publication_generation = '999' }
        'leg' { $recheck.Fields.leg_generation = '999' }
        'checkpoint' { $recheck.Fields.checkpoint_generation = '999' }
        'data' { $recheck.Fields.data_session_identity = 'other' }
        'deadline' { $recheck.Fields.wake_deadline_utc = $origin.AddMilliseconds(1101).ToString('o') }
        'wrong_wake' { $recheck.Fields.wake_generation = '999' }
        'missing_recheck' { $items = @($items | Where-Object { $_ -ne $recheck }) }
        'stale_only' { $recheck.Fields.decision_status = 'stale'; $recheck.Fields.decision_reason = 'wake_generation_replaced' }
        'cross_family_collision' {
            $items = @($items | Where-Object { $_ -ne $recheck })
            $stall = @($items | Where-Object EventName -eq 'filetransfer_v6_frontier_request_deferred')[0]
            Add 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1856 $stall.Fields @{
                wake_generation='167';decision_status='rechecked';decision_reason='stall_grace_armed'}
            $items += $events[$events.Count - 1]
        }
    }
    $items = @($items | Sort-Object Sequence, LineNumber)
    $expected = $case -in @('exact', 'rearm_only', 'chain_duplicate', 'chain_no_stale', 'chain_family_same', 'chain_earlier_root',
        'chain_unrelated_transfer_conflict')
    $plain = & $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation -Events $items
    $index = New-FileTransferFallbackFrontierGraceCorrelationIndex $items
    $indexed = Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $items -Index $index
    foreach ($result in @($plain, $indexed)) {
        if ($result.RawCount -ne 1 -or $result.DistinctCount -ne 1 -or
            $result.RecoveredCount -ne [int]$expected -or $result.UnresolvedCount -ne [int](-not $expected)) {
            throw "$case unexpected accounting: $($result.RawCount)/$($result.DistinctCount)/$($result.RecoveredCount)/$($result.UnresolvedCount)"
        }
    }
    if ((Boundary $plain) -cne (Boundary $indexed)) { throw "$case flat/indexed boundary differs" }
    $lines = @($items | ForEach-Object { $f = $_.Fields
        $parts = @($f.Keys | Sort-Object | ForEach-Object { "$_=$($f[$_])" })
        "[$($_.TimestampUtc.ToString('yyyy-MM-dd HH:mm:ss.fffffffZ'))] [INFO] [FileTransfer] $($parts -join '; '); gui_evidence_sequence=$($_.Sequence)"
    })
    $catalog = @(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
    $compactLines = @($lines | Where-Object { $_ -match '(?:^|[; ])event=([^;]+)' -and $Matches[1] -cin $catalog })
    if ($compactLines.Count -ge $lines.Count) { throw 'Full/compact inputs must differ' }
    foreach ($inputLines in @($lines, $compactLines)) {
        $guiResult = Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure -Lines $inputLines
        if ($guiResult.Unresolved -ne (-not $expected) -or
            $guiResult.FirstSequence -ne $(if ($expected) { [long]::MaxValue } else { 100L })) { throw "$case GUI boundary differs" }
        $parsed = @(for ($i = 0; $i -lt $inputLines.Count; $i++) {
            ConvertFrom-FileTransferLogLine -Line $inputLines[$i] -FilePath 'serialized.log' -LineNumber ($i + 1) -Sequence ($i + 1)
        })
        $serialized = Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $parsed -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $parsed)
        if ((Boundary $serialized) -cne (Boundary $indexed)) { throw "$case serialized boundary differs" }
    }
    $owned = [Collections.Generic.HashSet[object]]::new()
    [void]$owned.Add(@($items | Where-Object EventName -eq 'filetransfer_v6_receiver_state_deferred')[0])
    $key = Get-FileTransferFallbackFrontierGraceTransferKey -Event @($owned)[0]
    foreach ($frontiers in @(@($frontier), @($frontier, ($frontier + 3), ($frontier + 6)))) {
        $slice = @(Get-FileTransferFallbackFrontierGracePartitionSlice -Index $index -TransferKey $key -Frontiers $frontiers)
        $partition = & $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation -Events $slice -OwnedCandidateEvents $owned
        if ((Boundary $partition) -cne (Boundary $indexed)) { throw "$case partition boundary differs" }
    }
}
"recent-grace-ordering-ok: $($cases.Count) cases"
