param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationRows.ps1')
$path=Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1'
$text=[IO.File]::ReadAllText($path);$ast=Read-AttemptAst $path
$codec=Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
foreach($name in @('Get-FileTransferCoordinatorSnapshotSha256','ConvertTo-FileTransferCoordinatorEvidenceToken')){
    Invoke-Expression (Get-AttemptFunction $codec $name)
}
foreach($name in @('Require','Pick','Named','Scope','Token','Read-FileTransferNativeFrontierScope')){
    Invoke-Expression (Get-AttemptFunction $ast $name)
}
$ownership=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and
    $n.Body.Extent.Text.Contains('$owned.Add($row)')},$true))
$start=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$progresses'},$true))
$end=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$recentRows'},$true))
if($ownership.Count -ne 1 -or $start.Count -ne 1 -or $end.Count -ne 1){throw 'Original selector seam ambiguous'}
$selection=$text.Substring($start[0].Extent.StartOffset,$end[0].Extent.StartOffset-$start[0].Extent.StartOffset)
[IO.File]::WriteAllText((Join-Path $Directory 'original-selector.ps1'),$ownership[0].Extent.Text+[Environment]::NewLine+$selection)
# Scalar diagnostic observations only; no Coordinator, transport, native custody
# or issued authority/receipt object is constructed by this selector probe.
$session='selector-session';$transfer='selector-transfer'
$base=@{event='filetransfer_fallback_frontier_repair_progress_observed';session_id=$session;transfer_id=$transfer;
    frontier_scope_session_id=(Token $session);frontier_scope_transfer_id=(Token $transfer);
    direction='inbound';log_writer_id='selector-writer';log_writer_sequence='1'}
$legacy=$base.Clone();$legacy.repair_request_key='v6-fallback-progress:6:3';$legacy.reason='chunk_batch_committed'
$legacy.committed_frontier_before='6';$legacy.committed_frontier_after='9';$legacy.requested_chunk_count='3'
$canonical=$base.Clone();$canonical.route='post_tuna_fallback_v6';$canonical.protocol_version='6'
$canonical.live_route_epoch='4';$canonical.leg_generation='5';$canonical.transport_epoch='1';$canonical.checkpoint_request_id='(none)'
$canonical.authority_id='scalar-repair';$canonical.repair_generation='4'
$canonical.previous_frontier_chunk_index='12476';$canonical.current_frontier_chunk_index='12477'
$source=[pscustomobject]@{Route='post_tuna_fallback_v6';ProtocolVersion=6;LiveRouteEpoch=4;LegGeneration=5;TransportEpoch=1;CheckpointRequestId='(none)'}
$repair=@{};$pairs=@(@('continuity_repair_authority_id','AuthorityId'),@('continuity_repair_generation','DecisionGeneration'),
    @('continuity_source_fingerprint','SourceScopeFingerprint'),@('continuity_current_scope_fingerprint','CurrentScopeFingerprint'),
    @('continuity_acceptance_id','AcceptanceIdentity'),@('continuity_accepted_state_generation','AcceptedStateGeneration'),
    @('continuity_origin_receipt_id','OriginReceiptIdentity'),@('continuity_origin_receipt_fingerprint','OriginReceiptFingerprint'),
    @('continuity_receipt_id','ContinuityReceiptIdentity'),@('continuity_receipt_fingerprint','ContinuityReceiptFingerprint'))
foreach($pair in $pairs){$value='scalar-'+$pair[1];$canonical[$pair[0]]=$value;$repair[$pair[1]]=$value}
foreach($pair in @(@('repair_request_key','CanonicalRepairKey'),@('range_fingerprint','RangeFingerprint'),
    @('primary_protocol_repair_request_id','ProtocolRepairRequestId'),@('primary_request_correlation_id','RequestCorrelationId'))){
    $value='scalar-'+$pair[1];$canonical[$pair[0]]=$value;$repair[$pair[1]]=Token $value
}
$receipt=[pscustomobject]@{Source=$source;RootGeneration=1;OriginalDeadlineUtcTicks=7500000}
$root=[pscustomobject]@{FrontierChunkIndex=12477;WakeGeneration=1;DeadlineUtcTicks=7500000}
$progressBinding=[pscustomobject]@{Before=12476;After=12477}
$f=@{log_writer_id='selector-writer'}
$failureRow=[pscustomobject]@{Identity=[pscustomobject]@{Error='';Session=(Token $session);Transfer=(Token $transfer)}}
$visit=[Action]{$script:visits++}
function Row($fields,[int]$position,[switch]$Duplicate){
    $line=(@($fields.Keys|ForEach-Object{$_+'='+$fields[$_]}) -join '; ')+';'
    if($Duplicate){$line+=' current_frontier_chunk_index=12477;'}
    $parsed=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
    [pscustomobject]@{Position=$position;Fields=$parsed;Identity=(Read-FileTransferNativeFrontierScope $line $parsed)}
}
$results=[Collections.Generic.List[object]]::new()
$cases=@('legacy-before','legacy-after','legacy-only','legacy-missing-after','legacy-number','legacy-authority',
    'legacy-route','canonical-missing-current','canonical-route','canonical-continuity','canonical-link','canonical-before',
    'canonical-duplicate','foreign-writer','foreign-session','foreign-transfer','unknown-scope','future')
try{foreach($case in $cases){
    $old=$legacy.Clone();$current=$canonical.Clone();$failurePosition=30;$expected='';$duplicate=$false
    switch($case){
        'legacy-only' {$expected='native_frontier_progress_missing'}
        'legacy-missing-after' {$old.Remove('committed_frontier_after');$expected='native_frontier_progress_shape'}
        'legacy-number' {$old.committed_frontier_after='bad';$expected='native_frontier_progress_shape'}
        'legacy-authority' {$old.authority_id='foreign';$expected='native_frontier_progress_shape'}
        'legacy-route' {$old.route='post_tuna_fallback_v6';$expected='native_frontier_progress_shape'}
        'canonical-missing-current' {$current.Remove('current_frontier_chunk_index');$expected='native_frontier_progress_shape'}
        'canonical-route' {$current.route='file_tuna_v4';$expected='native_frontier_source_scope'}
        'canonical-continuity' {$current.continuity_current_scope_fingerprint='foreign';$expected='native_frontier_repair_identity'}
        'canonical-link' {$current.range_fingerprint='foreign';$expected='native_frontier_repair_link'}
        'canonical-before' {$current.previous_frontier_chunk_index='12475';$expected='native_frontier_root'}
        'canonical-duplicate' {$duplicate=$true;$expected='native_frontier_duplicate_field'}
        'foreign-writer' {$current.log_writer_id='foreign';$expected='native_frontier_progress_missing'}
        'foreign-session' {$current.session_id='foreign';$current.frontier_scope_session_id=Token 'foreign';$expected='native_frontier_progress_missing'}
        'foreign-transfer' {$current.transfer_id='foreign';$current.frontier_scope_transfer_id=Token 'foreign';$expected='native_frontier_progress_missing'}
        'unknown-scope' {$current.Remove('frontier_scope_session_id');$expected='native_frontier_scope_invalid'}
        'future' {$failurePosition=5;$expected='native_frontier_progress_missing'}
    }
    $legacyRow=Row $old 0;$canonicalRow=Row $current 10 -Duplicate:$duplicate
    $rows=@($legacyRow,$canonicalRow)
    if($case -ceq 'legacy-after'){$legacyRow.Position=20;$rows=@($canonicalRow,$legacyRow)}
    if($case -ceq 'legacy-only'){$rows=@($legacyRow)}
    # Other negative controls isolate the unchanged coordinator guards.
    if($case -cin @('canonical-missing-current','canonical-route','canonical-continuity','canonical-link','canonical-before',
        'canonical-duplicate','foreign-writer','foreign-session','foreign-transfer','unknown-scope','future')){$rows=@($canonicalRow)}
    $owned=[Collections.Generic.List[object]]::new();$selected=[Collections.Generic.SortedSet[int]]::new()
    $script:visits=0;$reason='';$progress=$null
    try{Invoke-Expression $ownership[0].Extent.Text;Invoke-Expression $selection}catch{$reason=$_.Exception.Message}
    $results.Add([ordered]@{case=$case;expected=$expected;actual=$reason;selected=@($selected);visits=$script:visits})
    if($reason -cne $expected){throw ('Progress selector mismatch: case='+$case+'; expected='+$(if($expected){$expected}else{'accepted'})+'; actual='+$reason)}
    if(!$expected -and (-not [object]::ReferenceEquals($progress,$canonicalRow) -or $selected.Count -ne 1 -or -not $selected.Contains(11))){
        throw 'Legacy diagnostic was selected as coordinator evidence'
    }
}}
finally{
    [IO.File]::WriteAllText((Join-Path $Directory 'selector-results.json'),(@{cases=$results.ToArray();authorityObjects=0;nativeCustodyObjects=0;selectorReplaysOnly=$true;primaryEvaluations=0}|ConvertTo-Json -Depth 6))
}
'progress-selector=18; accepted=2; rejected=16; no-authority'
