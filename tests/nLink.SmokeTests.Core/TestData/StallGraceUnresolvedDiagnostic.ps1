param([string]$RepoRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens = $null; $errors = $null
$guiPath = Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($guiPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'GUI source has parse errors' }
$definition = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'
}, $true))
if ($definition.Count -ne 1) { throw 'Expected one actual shared GUI consumer' }
Invoke-Expression $definition[0].Extent.Text

# Synthetic unit-test events and validated-boundary DTOs test the consumer's
# contract only. They are not producer evidence or a live cross-peer proof.
$start = [DateTimeOffset]::Parse('2026-09-09T06:27:00Z')
$repairA='fta2-repair_replay-222222222222222222222222'
$repairB='fta2-repair_replay-333333333333333333333333'
$range='frng-444444444444444444444444'
$routeAuthority='fta2-filetransfer_route_transition-555555555555555555555555'
$scope = @{
    log_writer_id='writer'; session_id='session'; transfer_id='transfer'; direction='inbound';
    continuity_coordinator_id='fte1-777777777777777777777777';
    route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'; runtime_generation='3';
    terminal_generation='0'; leg_generation='3'; transport_epoch='1'; bridge_recovery_generation='1';
    bridge_decision_generation='0'; checkpoint_request_id='(none)'; checkpoint_generation='1';
    route_publication_generation='3'; route_commit_generation='0'; data_session_identity='data'; data_session_generation='1'
}
function Event([string]$Stage, [string]$Name, [long]$Ms, [hashtable]$Extra) {
    $fields = $scope.Clone()
    foreach ($key in $Extra.Keys) { $fields[$key] = [string]$Extra[$key] }
    $fields.event = $Name; $fields.log_writer_sequence = [string]($Ms + 1)
    $fields.log_writer_attempt_utc_ticks = [string]$start.AddMilliseconds($Ms).UtcTicks
    [pscustomobject]@{ Stage=$Stage; EventName=$Name; Fields=$fields; Sequence=$Ms+1;
        LineNumber=$Ms+1; TimestampUtc=$start.AddMilliseconds($Ms); FilePath='unit-fixture.log' }
}
function Fixture {
    $wake = @{ frontier_chunk_index='3547'; highest_received_chunk_index='6418'; wake_generation='17';
        stall_started_utc=$start.AddMilliseconds(1000).ToString('O'); wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
        authority_effect='none'; stall_grace_ms='750'; elapsed_ms='0'; reason='chunk_batch_committed' }
    $repair = @{ authority_schema='2'; authority_id=$repairA; repair_generation='22'; repair_request_key="fallback-frontier:3547:1:$range";
        range_fingerprint=$range; primary_protocol_repair_request_id='v6-frontier:3547:18';
        primary_request_correlation_id='frq22'; predecessor_authority_id='fta2-repair_replay-111111111111111111111111'; frontier_chunk_index='3547';
        durable_received_highest_chunk_index='6418'; source_kind='ReceiverRequest'; target_transport='regular_nkn';
        range_count='1'; requested_chunk_count='1' }
    Event 'publication' 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
        target_route='post_tuna_fallback_v6'; target_protocol_version='6'; target_transport='regular_nkn';
        route_publication_action_kind='RoutePublication'; coordinator_decision_reason='accepted';
        fallback_leg_generation='3'; fallback_transport_epoch='1'; fallback_bridge_recovery_generation='1' }
    Event 'noise' 'unrelated_unit_test_note' 500 @{}
    Event 'hold' 'filetransfer_v6_frontier_request_deferred' 1000 $wake
    Event 'grant' 'filetransfer_fallback_frontier_repair_authority_granted' 1760 $repair
    $delivery = $repair.Clone(); $delivery.authority_id='control22'; $delivery.repair_request_id='v6-frontier:3547:18'
    $delivery.request_correlation_id='frq22'; $delivery.kind='frontier_request'
    $delivery.frame_type='filetransfer.frontier_request.v6'; $delivery.accepted_any='1'; $delivery.peer_visible_any='1'
    Event 'delivery' 'filetransfer_control_plane_delivery_observed' 1770 $delivery
    $dispatch=$repair.Clone(); $dispatch.dispatch_observed='1'
    Event 'dispatch' 'filetransfer_fallback_frontier_repair_dispatched' 1780 $dispatch
    $sent=$repair.Clone(); $sent.repair_request_id='v6-frontier:3547:18'; $sent.request_correlation_id='frq22'
    $sent.reason='frontier_stall_grace_elapsed'
    Event 'send' 'filetransfer_v6_frontier_request_sent' 1790 $sent
    $recheck=$wake.Clone(); $recheck.decision_status='dispatched'; $recheck.decision_reason='exact_receiver_request_dispatched'
    Event 'wake' 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1800 $recheck
    $settled=$repair.Clone(); $settled.settlement_outcome='SettledStalled'; $settled.dispatch_observed='1'
    $settled.decision_reason='post_tuna_fallback_v6_frontier_rescue_without_progress'
    $settled.successor_authority_id='(none)'; $settled.protocol_repair_request_id='v6-frontier:3547:18'; $settled.request_correlation_id='frq22'
    Event 'settlement' 'filetransfer_fallback_frontier_repair_contract_settled' 1950 $settled
}
function SupersessionFixture {
    $events=@(Fixture)
    $later=@($events | Where-Object {$_.Stage -ceq 'grant'})[0].Fields.Clone()
    $later.authority_id=$repairB; $later.repair_generation='32'; $later.predecessor_authority_id=$repairA
    $later.primary_protocol_repair_request_id='v6-frontier:3547:28'; $later.primary_request_correlation_id='frq32'
    $events+=Event 'later_grant' 'filetransfer_fallback_frontier_repair_authority_granted' 2200 $later
    $retirement=$later.Clone(); $retirement.settlement_outcome='StaleRejected'; $retirement.dispatch_observed='1'
    $retirement.successor_authority_id='(none)'; $retirement.protocol_repair_request_id=$later.primary_protocol_repair_request_id
    $retirement.request_correlation_id=$later.primary_request_correlation_id
    $retirement.decision_reason='tuna_reactivation_replaced_fallback_owner'
    $events+=Event 'retirement' 'filetransfer_fallback_frontier_repair_contract_settled' 2900 $retirement
    $target=@{route='file_tuna_v4';protocol_version='4';live_route_epoch='3';runtime_generation='4';
        terminal_generation='0';leg_generation='4';route_commit_generation='1';route_publication_generation='4';
        transport_epoch='2';bridge_recovery_generation='1'}
    $commit=@{authority_schema='2';authority_id=$routeAuthority;transition_generation='7';
        transition_kind='PostTunaFallbackV6ToTunaV4';decision_status='Committed';
        source_transport='regular_nkn';target_transport='tuna';source_data_session_generation='1';
        checkpoint_contiguous_frontier='3547';checkpoint_durable_highest='6418'}
    foreach($name in $target.Keys) { $commit['source_'+$name]=$scope[$name]; $commit['target_'+$name]=$target[$name] }
    $events+=Event 'commit' 'filetransfer_route_transition_committed' 3000 $commit
    $disposition=$commit.Clone(); $disposition.decision_status='Applied'; $disposition.decision_reason='filetransfer_route_transition_committed'
    foreach($name in $target.Keys) { $disposition['resulting_'+$name]=$target[$name] }
    $events+=Event 'disposition' 'filetransfer_route_transition_disposition_applied' 3100 $disposition
    $publication=$target.Clone(); $publication.transition_generation='7'; $publication.projection_source='coordinator_route_transition'
    $publication.reason='filetransfer_route_transition_committed'; $publication.route_publication_action_kind='RouteCommitPublication'
    $events+=Event 'target_publication' 'filetransfer_live_route_epoch_recovered' 3200 $publication
    $mapping=@{route='route';protocol_version='protocolVersion';live_route_epoch='liveRouteEpoch';
        runtime_generation='runtimeGeneration';terminal_generation='terminalGeneration';leg_generation='legGeneration';
        route_commit_generation='routeCommitGeneration';route_publication_generation='routePublicationGeneration';
        transport_epoch='transportEpoch';bridge_recovery_generation='bridgeRecoveryGeneration'}
    $sourceBoundary=@{dataSessionIdentity='projected-data';dataSessionGeneration=1;checkpointRequestId='(none)';checkpointGeneration=1}
    $targetBoundary=$sourceBoundary.Clone()
    foreach($name in $mapping.Keys) { $sourceBoundary[$mapping[$name]]=$scope[$name]; $targetBoundary[$mapping[$name]]=$target[$name] }
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $projected='fte1-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($routeAuthority))).Replace('-','').ToLowerInvariant().Substring(0,24)) }
    finally { $sha.Dispose() }
    $boundaries=@()
    foreach($stage in @('commit','disposition')) {
        $status=if($stage -ceq 'commit'){'Committed'}else{'Applied'}
        $route=[pscustomobject]@{dispositionKind='Committed';sourceTransport='RegularNkn';targetTransport='Tuna';
            sourceBoundary=[pscustomobject]$sourceBoundary;targetBoundary=[pscustomobject]$targetBoundary;
            durableCheckpointFingerprint='checkpoint-proof';proofBoundaryFingerprint='route-proof';
            receipts=@([pscustomobject]@{stage=$stage;receiptStatus=$status;receiptValidated=$true})}
        $operation=[pscustomobject]@{family='route_transition';operationRole='terminal';operationKind='PostTunaFallbackV6ToTunaV4';
            authorityId=$projected;operationGeneration=7;beforeState='Draining';afterState='Committed';stateApplied=($stage -ceq 'commit');
            receiptValidated=$true;receiptStatus=$status;routeTransition=$route}
        $boundaries+=[pscustomobject]@{verdict='pass';writer='writer';writerSequence=$(if($stage -ceq 'commit'){2951}else{3051});
            boundaryId='unit-'+$stage+'-boundary';coordinatorInstanceId=$scope.continuity_coordinator_id;sessionId='session';transferId='transfer';
            direction='inbound';decisionKind=$(if($stage -ceq 'commit'){'CommitFileTransferRouteTransition'}else{'ApplyFileTransferRouteTransitionDispositionReceipt'});
            decisionStatus='Committed';beforeStateGeneration=40;afterStateGeneration=41;beforeStateHash='before';afterStateHash='after';
            beforeBoundary=[pscustomobject]$sourceBoundary;afterBoundary=[pscustomobject]$targetBoundary;operations=@($operation)}
    }
    [pscustomobject]@{Events=$events;Snapshot=[pscustomobject]@{Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass';
        StreamComplete=$true;AuthoritativeFamilies=@('route_transition');Boundaries=$boundaries}}
}
function Lines([object[]]$Events) {
    $position=0
    foreach ($event in @($Events | Sort-Object Sequence,LineNumber)) {
        $position++
        '[2026-09-09 06:27:00Z] [INFO] [UnitFixture] ' +
            (@($event.Fields.Keys | Sort-Object | ForEach-Object { "$_=$($event.Fields[$_])" }) -join '; ') +
            "; gui_evidence_sequence=$position"
    }
}
function Parse([string[]]$Lines, [string]$Path) {
    for ($i=0; $i -lt $Lines.Count; $i++) {
        ConvertFrom-FileTransferLogLine -Line $Lines[$i] -FilePath $Path -LineNumber ($i+1) -Sequence ($i+1)
    }
}
function Quartet($Result) { @($Result.RawCount,$Result.DistinctCount,$Result.RecoveredCount,$Result.UnresolvedCount) -join '/' }
function DiagnosticJson($Value) { ConvertTo-Json -InputObject $Value -Depth 8 -Compress }
$outputs=[Collections.Generic.List[object]]::new()
function Check([string]$Case, [object[]]$Events, $SnapshotEvidence, [AllowNull()][string]$Kind, [bool]$Parity=$false) {
    $lines=@(Lines $Events)
    $names=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
    $compact=@($lines | Where-Object {
        $_ -match '(?:^|\] |; )event=([^;]+)' -and $Matches[1] -cin $names
    })
    if ($compact.Count -ge $lines.Count) { throw "$Case did not independently regenerate compact input" }
    $reference=$null
    $inputs=@(@{Name='full';Lines=$lines})
    if($Parity) { $inputs+=@(@{Name='compact';Lines=$compact}) }
    foreach ($input in $inputs) {
        $events=@(Parse $input.Lines ($input.Name+'.log'))
        $hold=@($events | Where-Object {$_.EventName -ceq 'filetransfer_v6_frontier_request_deferred'})[0]
        $sourceIndex=New-FileTransferStallGraceCurrentSourceIndex -Events $events -SnapshotEvidence $SnapshotEvidence
        $diagnostic=Get-FileTransferStallGraceUnresolvedDiagnostic -Index $sourceIndex -Candidate $hold
        if ([string]::IsNullOrWhiteSpace($Kind)) {
            if ($null -ne $diagnostic) { throw "$Case invented a diagnostic without the exact dispatch/stall chain" }
        } else {
            if ($null -eq $diagnostic -or $diagnostic.Kind -cne $Kind -or
                $diagnostic.ObligationDisposition -cne 'Unresolved' -or $diagnostic.WakeWriterSequence -ne 1801 -or
                $diagnostic.RepairAuthorityId -cne $repairA -or $diagnostic.RepairGeneration -ne 22 -or
                $diagnostic.SettlementWriterSequence -ne 1951) { throw "$Case wrong diagnostic: $(DiagnosticJson $diagnostic)" }
            if ($null -ne $diagnostic.PSObject.Properties['Recovered'] -or
                $null -ne $diagnostic.PSObject.Properties['RecoveryEvent']) { throw "$Case diagnostic contains recovery authority" }
            if ($Kind -ceq 'DispatchedStalled' -and ($diagnostic.SourceDisposition -cne 'Unproven' -or
                $diagnostic.SupersedingTransitionId -cne '(none)' -or $diagnostic.SupersedingTransitionGeneration -ne 0 -or
                $diagnostic.RetirementWriterSequence -ne 0 -or $diagnostic.PublicationWriterSequence -ne 0)) {
                throw "$Case upgraded an unproven source"
            }
            if ($Kind -ceq 'SourceSuperseded' -and ($diagnostic.SourceDisposition -cne 'Superseded' -or
                $diagnostic.SupersedingTransitionId -cne 'unit-commit-boundary' -or $diagnostic.SupersedingTransitionGeneration -ne 7 -or
                $diagnostic.RetirementWriterSequence -ne 2901 -or $diagnostic.PublicationWriterSequence -ne 3201)) {
                throw "$Case wrong source supersession: $(DiagnosticJson $diagnostic)"
            }
        }
        if(-not $Parity) {
            $reference=DiagnosticJson ([ordered]@{Case=$Case;Diagnostic=$diagnostic})
            break
        }
        $direct=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $events -SnapshotEvidence $SnapshotEvidence
        $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events -SnapshotEvidence $SnapshotEvidence
        $indexed=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index -SnapshotEvidence $SnapshotEvidence
        $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $input.Lines -IncludeEpisodeAudit -SnapshotEvidence $SnapshotEvidence
        $guiFast=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $input.Lines -SnapshotEvidence $SnapshotEvidence
        foreach ($result in @($direct,$indexed)) {
            if ((Quartet $result) -cne '1/1/0/1') { throw "$Case changed unresolved counts: $(Quartet $result)" }
            $record=$result.CandidateRecords[0]
            if ($record.Recovered -or (DiagnosticJson $record.UnresolvedDiagnostic) -cne (DiagnosticJson $diagnostic)) {
                throw "$Case retained diagnostic/recovery differs"
            }
            if($record.Reason -cne 'post_tuna_fallback_frontier_stall_grace_expired_without_exact_receiver_request_recheck' -or
                $record.Variant -cne 'settled_frontier_progress_successor_not_rechecked_after_stall_grace') {
                throw "$Case changed frozen unresolved taxonomy"
            }
        }
        if (-not $gui.Unresolved -or @($gui.EpisodeAudit).Count -ne 1 -or $gui.EpisodeAudit[0].Recovered -or
            (DiagnosticJson $gui.FirstUnresolvedDiagnostic) -cne (DiagnosticJson $diagnostic) -or
            (DiagnosticJson $guiFast.FirstUnresolvedDiagnostic) -cne (DiagnosticJson $diagnostic) -or
            (DiagnosticJson $gui.EpisodeAudit[0].UnresolvedDiagnostic) -cne (DiagnosticJson $diagnostic)) {
            throw "$Case GUI diagnostic/recovery differs"
        }
        $record=$indexed.CandidateRecords[0]
        $metadata=[ordered]@{Case=$Case;Diagnostic=$diagnostic;Quartet=(Quartet $indexed);Reason=$record.Reason;
            Variant=$record.Variant;WriterSequence=$record.Event.Fields.log_writer_sequence;
            RecoveryWriterSequence=$(if($null -eq $record.RecoveryEvent){''}else{$record.RecoveryEvent.Fields.log_writer_sequence})}
        $json=DiagnosticJson $metadata
        if ($null -ne $reference -and $json -cne $reference) { throw "$Case full/compact diagnostic or existing metadata changed" }
        $reference=$json
        if ($record.Event.LineNumber -ne $hold.LineNumber -or $record.Event.Sequence -ne $hold.Sequence) {
            throw "$Case changed candidate location"
        }
    }
    $outputs.Add(($reference | ConvertFrom-Json))
    "pass $Case"
}
Check 'dispatched_stalled' @(Fixture) $null 'DispatchedStalled' $true
foreach ($stage in @('grant','delivery','dispatch','send','wake','settlement')) {
    Check "missing_$stage" @((Fixture) | Where-Object {$_.Stage -cne $stage}) $null $null ($stage -ceq 'wake')
}
foreach ($mutation in @(
    @('delivery','accepted_any','0'),@('delivery','peer_visible_any','0'),@('delivery','request_correlation_id','other'),
    @('dispatch','dispatch_observed','0'),@('send','repair_request_id','other'),@('wake','wake_generation','18'),
    @('settlement','authority_id','other'),@('settlement','repair_generation','23'),
    @('settlement','settlement_outcome','ProgressObserved'))) {
    $events=@(Fixture); $row=@($events | Where-Object {$_.Stage -ceq $mutation[0]})[0]
    $row.Fields[$mutation[1]]=$mutation[2]
    Check ('wrong_'+$mutation[0]+'_'+$mutation[1]) $events $null $null
}
$events=@(Fixture); $duplicate=@($events | Where-Object {$_.Stage -ceq 'grant'})[0]
Check 'identical_duplicate' ($events+@($duplicate)) $null 'DispatchedStalled'
$events=@(Fixture); $conflict=Event 'conflict' 'filetransfer_fallback_frontier_repair_dispatched' 1780 @{dispatch_observed='0'}
Check 'conflicting_duplicate' ($events+@($conflict)) $null $null

$fixture=SupersessionFixture
Check 'source_superseded' $fixture.Events $fixture.Snapshot 'SourceSuperseded' $true
Check 'raw_route_without_validated_snapshot' $fixture.Events $null 'DispatchedStalled'
foreach($stage in @('later_grant','retirement','commit','disposition','target_publication')) {
    $fixture=SupersessionFixture
    Check "source_missing_$stage" @($fixture.Events | Where-Object {$_.Stage -cne $stage}) $fixture.Snapshot 'DispatchedStalled'
}
foreach($mutation in @(
    @('retirement','decision_reason','other'),@('retirement','repair_generation','22'),
    @('retirement','primary_request_correlation_id','other'),@('commit','authority_id','fta2-filetransfer_route_transition-666666666666666666666666'),
    @('commit','source_runtime_generation','99'),@('commit','checkpoint_contiguous_frontier','3548'),
    @('commit','source_data_session_generation','2'),@('disposition','authority_schema','1'),
    @('disposition','resulting_live_route_epoch','4'),@('target_publication','projection_source','route_cache'),
    @('target_publication','route_commit_generation','2'))) {
    $fixture=SupersessionFixture; $row=@($fixture.Events | Where-Object {$_.Stage -ceq $mutation[0]})[0]
    $row.Fields[$mutation[1]]=$mutation[2]
    Check ('source_wrong_'+$mutation[0]+'_'+$mutation[1]) $fixture.Events $fixture.Snapshot 'DispatchedStalled' ($mutation[1] -ceq 'source_runtime_generation')
}
foreach($mutation in @('mode','verdict','incomplete','family','missing_commit','missing_disposition','ambiguous_commit','unvalidated_receipt','wrong_source','wrong_coordinator')) {
    $fixture=SupersessionFixture; $snapshot=$fixture.Snapshot
    switch($mutation) {
        mode { $snapshot.Mode='Legacy' }
        verdict { $snapshot.ArtifactEvidenceVerdict='fail' }
        incomplete { $snapshot.StreamComplete=$false }
        family { $snapshot.AuthoritativeFamilies=@('route_runtime') }
        missing_commit { $snapshot.Boundaries=@($snapshot.Boundaries[1]) }
        missing_disposition { $snapshot.Boundaries=@($snapshot.Boundaries[0]) }
        ambiguous_commit { $snapshot.Boundaries+=@($snapshot.Boundaries[0]) }
        unvalidated_receipt { $snapshot.Boundaries[0].operations[0].routeTransition.receipts[0].receiptValidated=$false }
        wrong_source { $snapshot.Boundaries[0].operations[0].routeTransition.sourceBoundary.runtimeGeneration='99' }
        wrong_coordinator { $snapshot.Boundaries[1].coordinatorInstanceId='another' }
    }
    Check ('snapshot_'+$mutation) $fixture.Events $snapshot 'DispatchedStalled'
}
foreach($interval in @('before_settlement','after_settlement')) {
    $ms=if($interval -ceq 'before_settlement'){1900}else{2600}
    $expected=if($interval -ceq 'before_settlement'){$null}else{'DispatchedStalled'}
    foreach($poison in @('pause','terminal','data_session_replacement','scope_aba')) {
        $fixture=SupersessionFixture
        $extra=switch($poison) {
            pause {
                Event 'paused' 'filetransfer_v6_receiver_state_sent' $ms @{transfer_paused='1';contiguous_committed_chunk_index='3547'}
                Event 'resumed' 'filetransfer_v6_receiver_state_sent' ($ms+1) @{transfer_paused='0';contiguous_committed_chunk_index='3547'}
            }
            terminal { Event 'terminal' 'file_transfer_inbound_terminal' $ms @{} }
            data_session_replacement {
                Event 'removed' 'filetransfer_data_session_removed' $ms @{}
                Event 'recreated' 'filetransfer_data_session_recreated' ($ms+1) @{data_session_generation='2'}
            }
            scope_aba {
                Event 'scope_away' 'filetransfer_route_selected' $ms @{runtime_generation='99'}
                Event 'scope_back' 'filetransfer_route_selected' ($ms+1) @{}
            }
        }
        Check ($interval+'_'+$poison) ($fixture.Events+@($extra)) $fixture.Snapshot $expected
    }
}

# Guard ordinary report/classification wiring without generating a full report.
$guiText=[IO.File]::ReadAllText($guiPath)
$reportText=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1'))
foreach($required in @('FallbackFrontierStallDiagnostic = $null',
    '$result.FallbackFrontierStallDiagnostic = $fallbackFrontierStallGraceRecheckFastPath.FirstUnresolvedDiagnostic')) {
    if(-not $guiText.Contains($required)) { throw "GUI diagnostic propagation missing: $required" }
}
foreach($required in @('FallbackFrontierStallDiagnostic = $fallbackFrontierStallDiagnostic',
    'fallback_frontier_stall_diagnostic = if ($null -ne $recoveryClassification.PSObject.Properties[''FallbackFrontierStallDiagnostic''])',
    'fallback_frontier_stall_diagnostic={0}',
    'ConvertTo-Json -InputObject $recoveryClassification.FallbackFrontierStallDiagnostic -Depth 4 -Compress')) {
    if(-not $reportText.Contains($required)) { throw "Report diagnostic propagation missing: $required" }
}

'stall-unresolved-diagnostic-normalized='+(ConvertTo-Json -InputObject $outputs.ToArray() -Depth 16 -Compress)
'stall-unresolved-diagnostic-ok'
