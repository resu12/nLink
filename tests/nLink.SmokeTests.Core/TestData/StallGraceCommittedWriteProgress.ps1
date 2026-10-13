param([string]$RepoRoot)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
# Reuse only the existing synthetic validated-publication DTO factory, not its
# tests. These DTOs exercise consumers; the runtime regression covers production.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/PublicationEvidence.ps1'),[ref]$tokens,[ref]$errors)
foreach($definition in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Id','Fixture')},$true)){
    . ([scriptblock]::Create($definition.Extent.Text))
}
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'GUI parse failed'}
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
. ([scriptblock]::Create($definition.Extent.Text))
function Build([switch]$Sanitized,[switch]$CommitBeforeLog,[switch]$CommitAfterDeadline){
    $fixture=Fixture -Sanitized:$Sanitized
    $candidate=$fixture.Events[2];$candidate.Fields.frontier_chunk_index='3';$candidate.Fields.highest_received_chunk_index='2'
    $start=[DateTimeOffset]$candidate.TimestampUtc
    function Row([string]$Name,[int]$Ms,[hashtable]$Extra,[switch]$Physical){
        $f=$candidate.Fields.Clone()
        if($Physical){foreach($key in @($f.Keys)){if($key -notin @('log_writer_id','session_id','transfer_id','transport_epoch')){$f.Remove($key)}}}
        foreach($key in $Extra.Keys){$f[$key]=[string]$Extra[$key]}
        $f.event=$Name;$f.log_writer_sequence=[string](1001+$Ms);$f.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds($Ms).UtcTicks
        [pscustomobject]@{EventName=$Name;Fields=$f;Sequence=1001+$Ms;LineNumber=1001+$Ms;TimestampUtc=$start.AddMilliseconds($Ms);FilePath='fixture.log'}
    }
    $progressMs=if($CommitAfterDeadline){900}else{600}
    $commitLogMs=if($CommitBeforeLog){$progressMs+1}else{1001}
    $recheck=Row 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1000 @{
        decision_status='stale';decision_reason='committed_write_source_changed';write_progress_binding_version='1';
        write_progress_binding='exact_current_fallback_source';write_progress_frontier='6';write_progress_highest='5';
        write_progress_utc=$start.AddMilliseconds($progressMs).ToString('O');write_progress_recheck_utc=$start.AddMilliseconds(999).ToString('O')}
    $commit=Row 'filetransfer_v6_sparse_write_committed' $commitLogMs @{
        contiguous_chunks_committed='3';next_chunk_index='6';highest_received_chunk_index='5'} -Physical
    $state=Row 'filetransfer_v6_receiver_state_sent' ($commitLogMs+20) @{
        contiguous_committed_chunk_index='6';durable_received_highest_chunk_index='5';epoch='3';transfer_paused='0'} -Physical
    $fixture.Events=@($fixture.Events[0],$candidate,$recheck,$commit,$state | Sort-Object Sequence)
    $fixture | Add-Member Candidate $candidate
    $fixture | Add-Member Recheck $recheck
    $fixture | Add-Member Commit $commit
    $fixture | Add-Member State $state
    return $fixture
}
function Closure($f){Get-FileTransferStallGraceCommittedWriteProgressClosure -Events $f.Events -Episode ([pscustomobject]@{Event=$f.Candidate}) -SnapshotEvidence $f.Snapshot}
function Open($f,[string]$label){if($null -ne (Closure $f)){throw "$label unexpectedly closed"}}
function Consumers($f,[bool]$closed){
    $expected=if($closed){'1/1/1/0'}else{'1/1/0/1'}
    $legacy=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $f.Events -SnapshotEvidence $f.Snapshot
    $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $f.Events -SnapshotEvidence $f.Snapshot
    $indexed=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $f.Events -Index $index -SnapshotEvidence $f.Snapshot
    foreach($result in @($legacy,$indexed)){
        $actual=@($result.RawCount,$result.DistinctCount,$result.RecoveredCount,$result.UnresolvedCount) -join '/'
        if($actual -cne $expected){throw "Consumer got $actual, expected $expected; indexed=$([object]::ReferenceEquals($result,$indexed)); sanitized=$sanitized; beforeLog=$beforeLog; afterDue=$afterDue"}
    }
    $lines=@($f.Events | ForEach-Object {$row=$_;(@($row.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($row.Fields[$_])"}) -join '; ')+"; gui_evidence_sequence=$($row.Sequence)"})
    $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines -IncludeEpisodeAudit -SnapshotEvidence $f.Snapshot
    if(@($gui.EpisodeAudit).Count -ne 1 -or $gui.EpisodeAudit[0].Recovered -ne $closed -or $gui.Unresolved -eq $closed){throw 'GUI/retained mismatch'}
}
foreach($sanitized in @($false,$true)){
    foreach($beforeLog in @($false,$true)){
        foreach($afterDue in @($false,$true)){
            $f=Build -Sanitized:$sanitized -CommitBeforeLog:$beforeLog -CommitAfterDeadline:$afterDue
            if($null -eq (Closure $f)){throw 'Exact caught-up progress did not close'}
            Consumers $f $true
            $f.Events+=@($f.Recheck)
            if($null -eq (Closure $f)){throw 'Identical duplicate changed closure'}
        }
    }
}
foreach($field in @('write_progress_binding_version','write_progress_binding','write_progress_frontier','write_progress_highest',
        'write_progress_utc','write_progress_recheck_utc','publication_evidence_version','publication_evidence_token_fingerprint',
        'publication_evidence_observed_state_generation','data_session_identity','data_session_generation','runtime_generation',
        'route_publication_generation','wake_generation','wake_deadline_utc','authority_effect')){
    $f=Build;$f.Recheck.Fields.Remove($field);Open $f "missing $field"
    $f=Build;$f.Recheck.Fields[$field]='wrong';Open $f "changed $field"
}
foreach($which in @('Commit','State')){
    $f=Build;$f.Events=@($f.Events | Where-Object {$_ -ne $f.$which});Open $f "missing $which";Consumers $f $false
}
foreach($mutation in @(@('Commit','next_chunk_index','7'),@('Commit','contiguous_chunks_committed','2'),
        @('State','contiguous_committed_chunk_index','7'),@('State','durable_received_highest_chunk_index','6'),@('State','transfer_paused','1'),
        @('Recheck','decision_reason','frontier_scope_changed'),@('Recheck','write_progress_frontier','3'),
        @('Recheck','write_progress_utc','2026-09-06T04:32:04Z'),@('Recheck','write_progress_recheck_utc','2026-09-06T04:32:01Z'))){
    $f=Build;$f.($mutation[0]).Fields[$mutation[1]]=$mutation[2];Open $f ($mutation -join '/');Consumers $f $false
}
$f=Build;$f.Snapshot=$null;Open $f 'no snapshot'
$f=Build;$f.Snapshot.StreamComplete=$false;Open $f 'incomplete snapshot'
$f=Build;$f.Snapshot.Boundaries[2].afterBoundary.dataSessionGeneration=2;Open $f 'changed current snapshot source'
$f=Build;$f.Recheck.Fields.Remove('write_progress_binding_version');Consumers $f $false
$f=Build;foreach($key in @($f.Recheck.Fields.Keys)){if($key.StartsWith('write_progress_') -or $key.StartsWith('publication_evidence_')){$f.Recheck.Fields.Remove($key)}}
Open $f 'legacy artifact has no witness';Consumers $f $false
foreach($name in @('file_transfer_inbound_terminal','filetransfer_data_session_removed','filetransfer_route_selected')){
    $f=Build;$poison=[pscustomobject]@{EventName=$name;Fields=$f.Candidate.Fields.Clone();Sequence=1801;LineNumber=1801}
    $poison.Fields.event=$name;$poison.Fields.log_writer_sequence='1801'
    $poison.Fields.log_writer_attempt_utc_ticks=[string]([DateTimeOffset]$f.Candidate.TimestampUtc).AddMilliseconds(800).UtcTicks
    if($name -ceq 'filetransfer_route_selected'){$poison.Fields.runtime_generation='9'}
    $f.Events=@($f.Events)+@($poison);Open $f "intervening $name";Consumers $f $false
}
$f=Build;$copy=[pscustomobject]@{EventName=$f.Recheck.EventName;Fields=$f.Recheck.Fields.Clone();Sequence=$f.Recheck.Sequence;LineNumber=$f.Recheck.LineNumber}
$copy.Fields.write_progress_frontier='9';$f.Events+=@($copy);Open $f 'conflicting writer copy';Consumers $f $false
$f=Build;foreach($row in $f.Events){$row.Sequence+=73;$row.LineNumber+=73;$row.FilePath='compact.log'}
if($null -eq (Closure $f)){throw 'Compact reprojection changed closure'};Consumers $f $true
'committed-write-progress-ok'
