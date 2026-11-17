param([string]$RepoRoot, [switch]$Baseline, [string[]]$Cases = @())
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'GUI parse failed' }
$definition = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'}, $true))
if ($definition.Count -ne 1) { throw 'Expected actual GUI consumer' }
Invoke-Expression $definition[0].Extent.Text
$origin = [DateTimeOffset]::Parse('2026-09-08T12:45:22.5911854+00:00')
$oldCheckpoint = 'v6-regular-nkn-state-refresh:route-transition:2:1'
$scope = @{ log_writer_id='writer'; session_id='session'; transfer_id='transfer'; direction='inbound';
    route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'; runtime_generation='3';
    terminal_generation='0'; leg_generation='3'; transport_epoch='1'; bridge_recovery_generation='1';
    bridge_decision_generation='0'; checkpoint_request_id=$oldCheckpoint; checkpoint_generation='1';
    route_publication_generation='3'; route_commit_generation='0'; data_session_identity='data'; data_session_generation='1' }
function Event([string]$Name, [long]$Sequence, [long]$Ms, [hashtable]$Fields, [hashtable]$Extra = @{}) {
    $f = $Fields.Clone(); foreach ($key in $Extra.Keys) { $f[$key] = [string]$Extra[$key] }
    $f.event = $Name; $f.log_writer_sequence = [string]$Sequence
    $f.log_writer_attempt_utc_ticks = [string]$origin.AddMilliseconds($Ms).UtcTicks
    [pscustomobject]@{ EventName=$Name; Fields=$f; Sequence=$Sequence; LineNumber=[int]$Sequence;
        TimestampUtc=$origin.AddMilliseconds($Ms); FilePath='fixture.log' }
}
function Fixture([bool]$SameCheckpoint = $false, [bool]$KeepOldCheckpoint = $false) {
    $hold = $scope.Clone(); if ($SameCheckpoint -and -not $KeepOldCheckpoint) { $hold.checkpoint_request_id = '(none)' }
    Event 'filetransfer_route_hint_authorized_publication_accepted' 3236 0 $hold @{
        route_publication_action_kind='RoutePublication'; coordinator_decision_reason='accepted';
        target_route='post_tuna_fallback_v6'; target_protocol_version='6'; target_transport='regular_nkn';
        fallback_leg_generation='3'; fallback_transport_epoch='1'; fallback_bridge_recovery_generation='1' }
    $wake = @{ authority_effect='none'; frontier_chunk_index='1302'; highest_received_chunk_index='1574';
        stall_started_utc=$origin.AddMilliseconds(1000).ToString('O'); wake_deadline_utc=$origin.AddMilliseconds(1750).ToString('O');
        wake_generation='1'; elapsed_ms='0'; stall_grace_ms='750'; reason='chunk_batch_committed' }
    Event 'filetransfer_v6_frontier_request_deferred' 3294 1000 $hold $wake
    # Service acceptance is diagnostic, not a checkpoint-acceptance-to-repair authority binding.
    # The real 3297 snapshot is intentionally not synthesized: its absent active lease generation 0
    # is a different domain from the retained leg counter 1 below. No snapshot operation links 3456.
    if (-not $SameCheckpoint) {
        Event 'filetransfer_fallback_checkpoint_accepted' 3299 1010 $scope @{
            reason='regular_nkn_state_refresh'; proven_committed_chunk='1302'; proven_highest_observed_chunk='1574'; state='Active' }
    }
    Event 'unrelated_liveness_diagnostic' 3400 1500 $hold @{}
    $repair = $hold.Clone(); if (-not $KeepOldCheckpoint) { $repair.checkpoint_request_id = '(none)' }
    foreach ($key in @{
        authority_schema='2'; authority_id='fta2-repair_replay-successor'; predecessor_authority_id='(none)';
        repair_generation='3'; repair_request_key='fallback-frontier:1302:1:range:1302'; range_fingerprint='range:1302';
        frontier_chunk_index='1302'; durable_received_highest_chunk_index='1574'; source_kind='ReceiverRequest';
        target_transport='regular_nkn'; range_count='1'; requested_chunk_count='1';
        primary_protocol_repair_request_id='v6-frontier:1302:1'; primary_request_correlation_id='frq-successor'
    }.GetEnumerator()) { $repair[$key.Key] = $key.Value }
    Event 'filetransfer_fallback_frontier_repair_authority_granted' 3456 1800 $repair @{}
    $stale = $wake.Clone(); $stale.decision_status='stale'; $stale.decision_reason='frontier_scope_changed'
    Event 'filetransfer_v6_frontier_stall_grace_recheck_decided' 3470 1820 $hold $stale
    $request = @{ repair_request_id='v6-frontier:1302:1'; request_correlation_id='frq-successor'; reason='chunk_batch_committed' }
    Event 'filetransfer_control_plane_delivery_observed' 3472 1830 $repair ($request + @{
        kind='frontier_request'; peer_visible_any='1'; accepted_any='1'; control_plane_generation='2';
        first_start_chunk_index='1302'; first_chunk_count='1' })
    Event 'filetransfer_fallback_frontier_repair_dispatched' 3478 1840 $repair @{ dispatch_observed='1' }
    Event 'filetransfer_v6_frontier_request_sent' 3479 1850 $repair $request
    Event 'filetransfer_fallback_frontier_repair_contract_settled' 3554 2100 $repair @{
        settlement_outcome='ProgressObserved'; dispatch_observed='1' }
    Event 'filetransfer_fallback_frontier_repair_progress_observed' 3555 2110 $repair @{
        previous_frontier_chunk_index='1302'; current_frontier_chunk_index='1303' }
    Event 'filetransfer_v6_fallback_frontier_repair_progress_state_forced' 3560 2120 $repair @{
        current_frontier_chunk_index='1303'; receiver_state_epoch='3'; decision_status='Accepted' }
    Event 'filetransfer_v6_receiver_state_sent' 3574 2130 $repair @{
        epoch='3'; contiguous_committed_chunk_index='1303'; durable_received_highest_chunk_index='1574'; transfer_paused='0' }
    # Late old-checkpoint state remains evidence and cannot provide the new request's authority or progress.
    Event 'filetransfer_v6_receiver_state_sent' 3577 2140 $hold @{
        repair_request_id=$oldCheckpoint; epoch='1'; contiguous_committed_chunk_index='1302';
        durable_received_highest_chunk_index='1574'; transfer_paused='0'; reason='regular_nkn_state_refresh' }
}
function At([object[]]$Events, [string]$Sequence) { @($Events | Where-Object { $_.Fields.log_writer_sequence -ceq $Sequence })[0] }
function Copy-CheckpointEvent($Event) { $copy=$Event.PSObject.Copy(); $copy.Fields=$Event.Fields.Clone(); return $copy }
function Lines([object[]]$Events) {
    foreach ($event in ($Events | Sort-Object Sequence, LineNumber)) {
        "[$($event.TimestampUtc.ToString('yyyy-MM-dd HH:mm:ss.fffffffZ'))] [INFO] [FileTransfer] " +
            (@($event.Fields.Keys | Sort-Object | ForEach-Object { "$_=$($event.Fields[$_])" }) -join '; ') +
            "; gui_evidence_sequence=$($event.Sequence)"
    }
}
function Parse([string[]]$Lines, [string]$Path) {
    for ($i=0; $i -lt $Lines.Count; $i++) {
        ConvertFrom-FileTransferLogLine -Line $Lines[$i] -FilePath $Path -LineNumber ($i+1) -Sequence ($i+1)
    }
}
function Quartet($Result) { @($Result.RawCount, $Result.DistinctCount, $Result.RecoveredCount, $Result.UnresolvedCount) -join '/' }
function Metadata($Record) {
    $names = @('log_writer_id','log_writer_sequence','session_id','transfer_id','direction','route','protocol_version',
        'live_route_epoch','runtime_generation','terminal_generation','leg_generation','transport_epoch','bridge_recovery_generation',
        'bridge_decision_generation','checkpoint_request_id','checkpoint_generation','route_publication_generation','route_commit_generation',
        'data_session_identity','data_session_generation','frontier_chunk_index','highest_received_chunk_index',
        'stall_started_utc','wake_generation','wake_deadline_utc')
    $value = [ordered]@{}; foreach ($name in $names) { $value[$name]=[string]$Record.Event.Fields[$name] }
    $value.recovered=[bool]$Record.Recovered
    $value.recoveryWriter=if ($null -ne $Record.RecoveryEvent) { [string]$Record.RecoveryEvent.Fields.log_writer_id } else { '' }
    $value.recoverySequence=if ($null -ne $Record.RecoveryEvent) { [string]$Record.RecoveryEvent.Fields.log_writer_sequence } else { '' }
    ConvertTo-Json -InputObject $value -Depth 4 -Compress
}
function Retained([object[]]$Events) {
    Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $Events -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $Events)
}
function Check([object[]]$Events, [string]$Label, [bool]$Recovered, [bool]$BaselineOnly = $false) {
    $candidate = At $Events '3294'
    $admission = Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $Events) -Candidate $candidate
    if (-not $admission.Admitted -or $admission.Episode.AdmissionKind -cne 'CurrentPublishedSource' -or
        $null -ne $admission.Episode.HistoricalProgress) { throw "$Label did not independently admit the published-source hold" }
    if (@($Events | Where-Object { $_.EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed' -and $_.Sequence -lt 3294 }).Count) {
        throw 'Fixture invented predecessor repair progress'
    }
    $fullLines = @(Lines $Events)
    $catalog = @(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
    $compactLines = @($fullLines | Where-Object { $_ -match '(?:^|; )event=([^;]+)' -and $Matches[1] -cin $catalog })
    if ($compactLines.Count -ge $fullLines.Count) { throw 'Compact fixture was not independently regenerated from full input' }
    $full = Retained @(Parse $fullLines 'full.log')
    $compact = Retained @(Parse $compactLines 'regenerated-compact.log')
    $direct = & $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $Events
    $guiFull = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $fullLines -IncludeEpisodeAudit
    $guiCompact = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $compactLines -IncludeEpisodeAudit
    $expected = if ($Recovered -or $BaselineOnly) { '1/1/1/0' } else { '1/1/0/1' }
    foreach ($result in @($direct,$full,$compact)) {
        if ((Quartet $result) -cne $expected) { throw "$Label retained expected $expected, actual $(Quartet $result)" }
        if ($Recovered -and $result.CandidateRecords[0].RecoveryEvent.Fields.log_writer_sequence -cne '3574') { throw "$Label selected wrong recovery" }
        if ($result.CandidateRecords[0].Reason -cne 'post_tuna_fallback_frontier_stall_grace_expired_without_exact_receiver_request_recheck' -or
            $result.CandidateRecords[0].Variant -cne 'settled_frontier_progress_successor_not_rechecked_after_stall_grace') { throw "$Label changed taxonomy" }
    }
    foreach ($result in @($guiFull,$guiCompact)) {
        if (@($result.EpisodeAudit).Count -ne 1 -or $result.EpisodeAudit[0].Excluded -or
            $result.EpisodeAudit[0].Recovered -ne $Recovered -or $result.Unresolved -eq $Recovered) { throw "$Label GUI recovery/exclusion differs" }
        if (-not $Recovered -and $result.FirstSequence -ne 3294) { throw "$Label GUI selected wrong frozen hold" }
    }
    if ($BaselineOnly) { 'baseline retained=1/1/1/0 gui=1/1/0/1'; return }
    $boundary = Metadata $full.CandidateRecords[0]
    foreach ($record in @($direct.CandidateRecords[0],$compact.CandidateRecords[0],$guiFull.EpisodeAudit[0],$guiCompact.EpisodeAudit[0])) {
        if ((Metadata $record) -cne $boundary) { throw "$Label full/compact/GUI normalized metadata differs" }
    }
    "pass $Label"
}
if ($Baseline) { Check @(Fixture) 'checkpoint_retired' $false $true; return }
$mutations = @(
    @('acceptance_wrong_id','3299','checkpoint_request_id','wrong-checkpoint'),
    @('acceptance_wrong_reason','3299','reason','rejected_checkpoint'),
    @('acceptance_wrong_transfer','3299','transfer_id','other'),
    @('acceptance_wrong_scope','3299','leg_generation','4'),
    @('wrong_authority','3478','authority_id','fta2-repair_replay-other'),
    @('wrong_request','3479','repair_request_id','v6-frontier:other'),
    @('wrong_correlation','3472','request_correlation_id','frq-other'),
    @('wrong_generation','3456','repair_generation','4'),
    @('wrong_primary','3456','primary_protocol_repair_request_id','v6-frontier:other'),
    @('wrong_request_key','3478','repair_request_key','other'),
    @('wrong_fingerprint','3555','range_fingerprint','other'),
    @('wrong_source','3456','source_kind','Other'),
    @('wrong_grant_direction','3456','direction','outbound'),
    @('wrong_grant_route','3456','route','file_tuna_v4'),
    @('wrong_grant_protocol','3456','protocol_version','4'),
    @('wrong_scope','3456','runtime_generation','4'),
    @('wrong_data','3456','data_session_identity','other'),
    @('wrong_data_generation','3560','data_session_generation','2'),
    @('wrong_forced_epoch','3560','receiver_state_epoch','4'),
    @('rejected_forced_state','3560','decision_status','Rejected'),
    @('wrong_state_epoch','3574','epoch','4'),
    @('paused_state','3574','transfer_paused','1'),
    @('rejected_settlement','3554','settlement_outcome','StaleRejected'),
    @('rejected_delivery','3472','accepted_any','0'),
    @('stale_wake_cannot_dispatch','3470','decision_status','dispatched'),
    @('wrong_old_wake','3470','wake_generation','2'),
    @('swapped_checkpoint_counter','3456','checkpoint_generation','0'),
    @('candidate_old_checkpoint_counter_zero','3294','checkpoint_generation','0'),
    @('malformed_checkpoint_counter','3456','checkpoint_generation','invalid'),
    @('wrong_successor_checkpoint','3456','checkpoint_request_id','different-checkpoint')
)
$allCases = @('checkpoint_retired','missing_acceptance','missing_grant','missing_forced_state','missing_settlement','missing_authority',
    'duplicate','conflicting_acceptance','conflicting_grant','conflicting_checkpoint_grant','conflicting_checkpoint_grant_first',
    'replacement','terminal','pause','aba','same_checkpoint','same_old_checkpoint',
    'unrelated_checkpoint_grant','foreign_checkpoint_grant','late_checkpoint_grant','unrelated_publication_repair') + @($mutations | ForEach-Object { $_[0] })
foreach ($case in $allCases) {
    if ($Cases.Count -gt 0 -and $case -cnotin $Cases) { continue }
    $same = $case -in @('same_checkpoint','same_old_checkpoint','unrelated_checkpoint_grant',
        'foreign_checkpoint_grant','late_checkpoint_grant','unrelated_publication_repair')
    $keepOld = $same -and $case -cne 'same_checkpoint'
    $events = @(Fixture $same $keepOld)
    if ($keepOld -and ((At $events '3294').Fields.checkpoint_request_id -cne $oldCheckpoint -or
        (At $events '3456').Fields.checkpoint_request_id -cne $oldCheckpoint)) { throw "$case lost the live unchanged checkpoint ID" }
    $mutation = @($mutations | Where-Object { $_[0] -ceq $case })
    if ($mutation.Count -eq 1) { (At $events $mutation[0][1]).Fields[$mutation[0][2]]=$mutation[0][3] }
    switch ($case) {
        'missing_acceptance' { $events=@($events | Where-Object { $_.Fields.log_writer_sequence -cne '3299' }) }
        'missing_grant' { $events=@($events | Where-Object { $_.Fields.log_writer_sequence -cne '3456' }) }
        'missing_forced_state' { $events=@($events | Where-Object { $_.Fields.log_writer_sequence -cne '3560' }) }
        'missing_settlement' { $events=@($events | Where-Object { $_.Fields.log_writer_sequence -cne '3554' }) }
        'missing_authority' {
            foreach ($event in $events) {
                if ($event.Fields['authority_id'] -ceq 'fta2-repair_replay-successor') { $event.Fields.authority_id='(none)' }
            }
        }
        'duplicate' { $events+=@((Copy-CheckpointEvent (At $events '3299')),(Copy-CheckpointEvent (At $events '3472')),(Copy-CheckpointEvent (At $events '3555'))) }
        'conflicting_acceptance' { $copy=Copy-CheckpointEvent (At $events '3299');$copy.Fields.checkpoint_request_id='other';$events+=@($copy) }
        'conflicting_grant' { $copy=Copy-CheckpointEvent (At $events '3456');$copy.Fields.checkpoint_generation='0';$events+=@($copy) }
        { $_ -in @('conflicting_checkpoint_grant','conflicting_checkpoint_grant_first') } {
            $copy=Copy-CheckpointEvent (At $events '3456');$copy.Fields.checkpoint_request_id=$oldCheckpoint
            if ($case -eq 'conflicting_checkpoint_grant_first') { $events=@($copy)+$events } else { $events+=@($copy) }
        }
        { $_ -in @('replacement','terminal','pause','aba') } {
            $name=switch ($case) { 'replacement' {'filetransfer_data_session_recreated'} 'terminal' {'file_transfer_inbound_terminal'} 'pause' {'filetransfer_v6_receiver_state_sent'} 'aba' {'filetransfer_route_selected'} }
            $extra=Event $name 3500 1900 $scope @{ data_session_identity='replacement'; transfer_paused='1'; route_publication_generation='4' }
            $events+=@($extra)
            if ($case -eq 'aba') { $events+=@(Event 'filetransfer_route_selected' 3501 1910 $scope @{}) }
        }
        { $_ -in @('unrelated_checkpoint_grant','foreign_checkpoint_grant','late_checkpoint_grant','unrelated_publication_repair') } {
            $copy=Copy-CheckpointEvent (At $events '3456');$copy.Fields.checkpoint_request_id='other-checkpoint';$copy.Fields.authority_id='fta2-repair_replay-unrelated'
            $copy.Fields.log_writer_sequence='3457';$copy.Sequence=3457;$copy.LineNumber=3457
            if ($case -eq 'unrelated_checkpoint_grant') { $copy.Fields.frontier_chunk_index='999' }
            if ($case -eq 'foreign_checkpoint_grant') { $copy.Fields.transfer_id='foreign' }
            if ($case -eq 'late_checkpoint_grant') { $copy.Sequence=3600;$copy.LineNumber=3600;$copy.Fields.log_writer_sequence='3600';$copy.TimestampUtc=$origin.AddMilliseconds(2200);$copy.Fields.log_writer_attempt_utc_ticks=[string]$copy.TimestampUtc.UtcTicks }
            if ($case -eq 'unrelated_publication_repair') {
                $copy.Fields.route_publication_generation='4'
                $copy.TimestampUtc=$origin.AddMilliseconds(1801);$copy.Fields.log_writer_attempt_utc_ticks=[string]$copy.TimestampUtc.UtcTicks
                $progress=Copy-CheckpointEvent (At $events '3555');$progress.Fields.route_publication_generation='4'
                $progress.Fields.checkpoint_request_id=$copy.Fields.checkpoint_request_id;$progress.Fields.authority_id=$copy.Fields.authority_id
                $progress.Fields.log_writer_sequence='3458';$progress.Sequence=3458;$progress.LineNumber=3458
                $progress.TimestampUtc=$origin.AddMilliseconds(1802);$progress.Fields.log_writer_attempt_utc_ticks=[string]$progress.TimestampUtc.UtcTicks
                $events+=@($progress)
            }
            $events+=@($copy)
        }
    }
    Check $events $case $same
}
# Published-source admission is inbound-only; no outbound repair predecessor is fabricated.
$outbound=@(Fixture);foreach($event in $outbound){$event.Fields.direction='outbound'}
$admission=Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $outbound) -Candidate (At $outbound '3294')
if ($admission.Admitted) { throw 'Outbound diagnostic became an inbound stall obligation' }
'checkpoint-scope-ok'
