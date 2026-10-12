param([string]$RepoRoot)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'GUI parse failed'}
$definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
. ([scriptblock]::Create($definition.Extent.Text))
function Id([string]$Value){'fte1-'+(Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($Value))).Substring(0,24)}
# Synthetic validated-boundary DTOs exercise the consumer contract only; they
# are not producer, snapshot-validator or live-network proof.
function Fixture {
    param([switch]$Sanitized)
    $dataIdentity=if($Sanitized){'0123456789abcdef0123456789abcdef:1'}else{'data'}
    $start=[DateTimeOffset]::Parse('2026-09-06T04:32:00Z')
    $scope=@{log_writer_id='writer';session_id='session';transfer_id='transfer';direction='inbound';
        route='post_tuna_fallback_v6';protocol_version='6';live_route_epoch='1';runtime_generation='1';
        terminal_generation='0';leg_generation='1';transport_epoch='3';bridge_recovery_generation='1';bridge_decision_generation='0';
        checkpoint_request_id='(none)';checkpoint_generation='0';route_publication_generation='1';route_commit_generation='0';
        data_session_identity='data';data_session_generation='1';publication_evidence_version='1';
        publication_evidence_coordinator_id=(Id 'receiver');publication_evidence_session_id=(Id 'session');
        publication_evidence_transfer_id=(Id 'transfer');publication_evidence_boundary_id=(Id 'source');
        publication_evidence_state_generation='2';publication_evidence_observed_state_generation='2';
        publication_evidence_operation_key=('route_runtime|route_publication|'+(Id 'publication:1')+'|1|RoutePublication');
        publication_evidence_token_fingerprint=(Id 'receiver-token')}
    if($Sanitized){$scope.data_session_identity='[redacted]:1';$scope.frontier_scope_data_session_identity=Id $dataIdentity}
    function Event([string]$Name,[long]$Ms,[hashtable]$Extra){
        $f=$scope.Clone();foreach($key in $Extra.Keys){$f[$key]=[string]$Extra[$key]}
        $f.event=$Name;$f.log_writer_sequence=[string]($Ms+1);$f.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds($Ms).UtcTicks
        [pscustomobject]@{EventName=$Name;Fields=$f;Sequence=$Ms+1;LineNumber=$Ms+1;TimestampUtc=$start.AddMilliseconds($Ms);FilePath='fixture.log'}
    }
    $source=Event 'filetransfer_route_hint_authorized_publication_accepted' 10 @{route_publication_action_kind='RoutePublication';
        coordinator_decision_reason='accepted';target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';
        fallback_leg_generation='1';fallback_transport_epoch='3';fallback_bridge_recovery_generation='1'}
    $peer=Event 'filetransfer_route_selected' 100 @{direction='outbound';route='file_tuna_v4';protocol_version='4';
        live_route_epoch='2';runtime_generation='2';leg_generation='2';route_publication_generation='2';route_commit_generation='1';
        publication_evidence_coordinator_id=(Id 'sender');publication_evidence_boundary_id=(Id 'peer');
        publication_evidence_token_fingerprint=(Id 'sender-token');
        publication_evidence_operation_key=('route_runtime|route_publication|'+(Id 'publication:2')+'|2|RouteCommitPublication')}
    $hold=Event 'filetransfer_v6_frontier_request_deferred' 1000 @{authority_effect='none';frontier_chunk_index='100';highest_received_chunk_index='255';
        stall_started_utc=$start.AddMilliseconds(1000).ToString('O');wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
        wake_generation='1';elapsed_ms='0';stall_grace_ms='750';reason='chunk_batch_committed';publication_evidence_observed_state_generation='3'}
    function Boundary($Event,[string]$Id,[long]$Generation){
        $f=$Event.Fields
        $mapping=@{route='route';protocol_version='protocolVersion';live_route_epoch='liveRouteEpoch';runtime_generation='runtimeGeneration';
            terminal_generation='terminalGeneration';leg_generation='legGeneration';route_publication_generation='routePublicationGeneration';route_commit_generation='routeCommitGeneration'}
        $b=@{dataSessionIdentity=(Id $dataIdentity);dataSessionGeneration=1}
        foreach($name in $mapping.Keys){$b[$mapping[$name]]=$f[$name]}
        $identity=Id ('publication:'+$f.route_publication_generation)
        $operation=[pscustomobject]@{operationKey=$f.publication_evidence_operation_key;family='route_runtime';operationRole='route_publication';
            operationKind=$(if($f.direction -ceq 'inbound'){'RoutePublication'}else{'RouteCommitPublication'});
            operationGeneration=$f.route_publication_generation;authorityId=$identity;afterState='Published';receiptValidated=$true;
            receiptStatus='Accepted';receiptFingerprint=$f.publication_evidence_token_fingerprint;receiptIdentity=$identity;receiptGeneration=$f.route_publication_generation;
            routeRuntime=[pscustomobject]@{role='route_publication';boundary=[pscustomobject]$b;tokenFingerprint=$f.publication_evidence_token_fingerprint}}
        [pscustomobject]@{verdict='pass';boundaryId=(Id $Id);coordinatorInstanceId=$f.publication_evidence_coordinator_id;
            writer='writer';sessionId=(Id 'session');transferId=(Id 'transfer');direction=$f.direction;afterStateGeneration=$Generation;
            afterBoundary=[pscustomobject]$b;operations=@($operation)}
    }
    [pscustomobject]@{Events=@($source,$peer,$hold);Snapshot=[pscustomobject]@{Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass';
        StreamComplete=$true;AuthoritativeFamilies=@('route_runtime');Boundaries=@((Boundary $source 'source' 2),(Boundary $peer 'peer' 2),(Boundary $hold 'observed' 3))}}
}
function Admit($Fixture){
    $index=New-FileTransferStallGracePublishedSourceIndex -Events $Fixture.Events -SnapshotEvidence $Fixture.Snapshot
    Get-FileTransferStallGracePublishedSourceAdmission -Index $index -Candidate $Fixture.Events[2]
}
function Reject($Fixture,[string]$Label){$result=Admit $Fixture;if($null -ne $result -and $result.Admitted){throw "Rejected ownership admitted: $Label"}}
$good=Fixture
if(-not (Admit $good).Admitted){throw 'Exact independent peer publication invalidated current source'}
$sanitized=Fixture -Sanitized
if(-not (Admit $sanitized).Admitted){throw 'Sanitized data-session token did not bind to the exact current snapshot'}
foreach($which in @(0,1,2)){
    foreach($token in @('', '(none)', 'fte1-invalid', (Id 'foreign-data'))){
        $bad=Fixture -Sanitized;$bad.Events[$which].Fields.frontier_scope_data_session_identity=$token;Reject $bad "sanitized $which token $token"
    }
    $bad=Fixture -Sanitized;$bad.Events[$which].Fields.Remove('frontier_scope_data_session_identity');Reject $bad "sanitized $which missing token"
    foreach($raw in @('', '(none)', '[redacted]:0', 'prefix[redacted]:1', '[redacted]:1:extra', 'foreign-data')){
        $bad=Fixture -Sanitized;$bad.Events[$which].Fields.data_session_identity=$raw;Reject $bad "sanitized $which conflicting raw $raw"
    }
    $bad=Fixture -Sanitized;$bad.Events[$which].Fields.data_session_generation='2';Reject $bad "sanitized $which generation"
    $bad=Fixture -Sanitized;$bad.Snapshot.Boundaries[$which].writer='foreign-writer';Reject $bad "sanitized $which snapshot writer"
    $bad=Fixture -Sanitized;$bad.Snapshot.Boundaries[$which].afterBoundary.dataSessionIdentity=Id 'foreign-data';Reject $bad "sanitized $which snapshot identity"
    $bad=Fixture;$bad.Events[$which].Fields.frontier_scope_data_session_identity=Id 'foreign-data';Reject $bad "raw $which contradictory token"
}
$bad=Fixture -Sanitized;$bad.Events[2].Fields.publication_evidence_observed_state_generation='4';Reject $bad 'sanitized absent observed state'
$bad=Fixture -Sanitized;$bad.Snapshot.ArtifactEvidenceVerdict='fail';Reject $bad 'sanitized failed snapshot'
$raw=Fixture
foreach($event in $raw.Events){$event.Fields.frontier_scope_data_session_identity=Id 'data'}
if(-not (Admit $raw).Admitted){throw 'Matching raw and retained identity changed admission'}
foreach($field in @('publication_evidence_version','publication_evidence_coordinator_id','publication_evidence_session_id',
    'publication_evidence_transfer_id','publication_evidence_boundary_id','publication_evidence_state_generation',
    'publication_evidence_observed_state_generation','publication_evidence_operation_key','publication_evidence_token_fingerprint')){
    foreach($which in @(0,1,2)){$bad=Fixture;$bad.Events[$which].Fields[$field]='wrong';Reject $bad "$which/$field"}
}
foreach($which in @(0,1,2)){$bad=Fixture;$bad.Events[$which].Fields.Remove('publication_evidence_version');Reject $bad "missing $which binding"}
$bad=Fixture;$bad.Snapshot=$null;Reject $bad 'missing snapshot'
$bad=Fixture;$bad.Snapshot.ArtifactEvidenceVerdict='fail';Reject $bad 'failed snapshot'
$bad=Fixture;$bad.Snapshot.StreamComplete=$false;Reject $bad 'incomplete snapshot'
$bad=Fixture;$bad.Snapshot.AuthoritativeFamilies=@();Reject $bad 'shadow-only'
$bad=Fixture;$bad.Snapshot.Boundaries[1].operations[0].receiptValidated=$false;Reject $bad 'unvalidated peer receipt'
$bad=Fixture;$bad.Snapshot.Boundaries[1].operations[0].receiptFingerprint=Id 'wrong';Reject $bad 'wrong receipt'
$bad=Fixture;$bad.Snapshot.Boundaries[2].afterBoundary.routePublicationGeneration='2';Reject $bad 'stale source receipt at observed generation'
$bad=Fixture;$bad.Snapshot.Boundaries[2].afterBoundary.dataSessionGeneration=2;Reject $bad 'replaced data session'
$bad=Fixture;$bad.Snapshot.Boundaries+=@($bad.Snapshot.Boundaries[1]);Reject $bad 'ambiguous boundary'
$bad=Fixture;foreach($event in $bad.Events){$event.Fields.transfer_id='foreign-transfer'};Reject $bad 'foreign raw transfer'
$bad=Fixture;foreach($event in $bad.Events){$event.Fields.session_id='foreign-session'};Reject $bad 'foreign raw session'
$bad=Fixture;$bad.Events[2].Fields.continuity_coordinator_id=Id 'foreign';Reject $bad 'conflicting hold coordinator'
$bad=Fixture;$bad.Events[1].Fields.transfer_paused='1';Reject $bad 'paused peer cannot hide poison'
$bad=Fixture;$bad.Events[1].Fields.event='file_transfer_inbound_terminal';Reject $bad 'terminal poison'
$bad=Fixture;$bad.Events[1].Fields.event='filetransfer_data_session_removed';Reject $bad 'data-session poison'
$bad=Fixture
$bad.Events[1].Fields.publication_evidence_coordinator_id=Id 'receiver'
$bad.Events[1].Fields.direction='inbound';$bad.Snapshot.Boundaries[1].direction='inbound'
$bad.Events[1].Fields.publication_evidence_state_generation='3';$bad.Events[1].Fields.publication_evidence_observed_state_generation='3'
$bad.Snapshot.Boundaries[1].coordinatorInstanceId=Id 'receiver';$bad.Snapshot.Boundaries[1].afterStateGeneration=3
$bad.Events[2].Fields.publication_evidence_observed_state_generation='4';$bad.Snapshot.Boundaries[2].afterStateGeneration=4
$restore=(Fixture).Events[0];$restore.Sequence=201;$restore.Fields.log_writer_sequence='201';$restore.Fields.publication_evidence_observed_state_generation='4'
if($null -eq (Resolve-FileTransferPublicationEvidence -Index (New-FileTransferPublicationEvidenceIndex $bad.Snapshot) -Event $bad.Events[1])){
    throw 'Same-owner ABA control must have an independently valid publication binding'
}
$bad.Events+=@($restore);Reject $bad 'same-owner ABA restoration'
$bad=Fixture;$extra=(Fixture).Events[1];$extra.Fields.route_publication_generation='9';$bad.Events+=@($extra);Reject $bad 'conflicting writer copy'
# Reprojection and identical physical duplicates preserve exact ownership.
$good.Events+=@($good.Events[1]);if(-not (Admit $good).Admitted){throw 'identical duplicate changed admission'}
foreach($event in $good.Events){$event.Sequence+=20;$event.LineNumber+=20;$event.FilePath='compact.log'}
if(-not (Admit $good).Admitted){throw 'location projection changed admission'}
foreach($good in @((Fixture),(Fixture -Sanitized))){
$direct=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $good.Events -SnapshotEvidence $good.Snapshot
$index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $good.Events -SnapshotEvidence $good.Snapshot
$indexed=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $good.Events -Index $index -SnapshotEvidence $good.Snapshot
$lines=@($good.Events | ForEach-Object {$event=$_;(@($event.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($event.Fields[$_])"}) -join '; ')+"; gui_evidence_sequence=$($event.Sequence)"})
$gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines -IncludeEpisodeAudit -SnapshotEvidence $good.Snapshot
foreach($result in @($direct,$indexed)){
    if((@($result.RawCount,$result.DistinctCount,$result.RecoveredCount,$result.UnresolvedCount) -join '/') -cne '1/1/0/1'){throw 'unresolved quartet changed'}
}
if(-not $gui.Unresolved -or @($gui.EpisodeAudit).Count -ne 1 -or $gui.EpisodeAudit[0].Recovered){throw 'GUI admission parity failed'}
}
'publication-evidence-ok'
