param([string]$RepoRoot, [string]$SourcePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'GUI parse failed'}
$gui=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure'},$true))
Invoke-Expression $gui[0].Extent.Text
$lines=@(Get-Content -LiteralPath $SourcePath)
$source=@(for($i=0;$i -lt $lines.Count;$i++){
    ConvertFrom-FileTransferLogLine -Line $lines[$i] -FilePath $SourcePath -LineNumber ($i+1) -Sequence ($i+1)
})
$selected=@($source|Where-Object { $_.EventName -eq 'filetransfer_v6_sparse_write_committed' -and $_.Fields.next_chunk_index -eq '102' })
if($selected.Count -ne 1){throw 'Expected one actual frontier commit'}
$original=$selected[0]; $deadline=[DateTimeOffset]::Parse($original.Fields.wake_deadline_utc)
if([long]$original.Fields.log_writer_attempt_utc_ticks -le $deadline.UtcTicks){throw 'Fixture did not delay log delivery'}
$scope=@('direction','route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation',
    'transport_epoch','bridge_recovery_generation','bridge_decision_generation','checkpoint_request_id','checkpoint_generation',
    'route_publication_generation','route_commit_generation','data_session_identity','data_session_generation','frontier_chunk_index',
    'highest_received_chunk_index','wake_generation','committed_progress_utc','wake_deadline_utc',
    'frontier_scope_session_id','frontier_scope_transfer_id','frontier_scope_data_session_identity')
$cases=@('exact','duplicate','at_deadline','missing_clock','malformed_clock','late_commit','future_clock','stale_source','authority_effect','wrong_count',
    'missing_arm','rearm_after_candidate','conflict','pause','terminal','source_replacement') + $scope
foreach($case in $cases){
    $events=@(foreach($event in $source){$copy=$event.PSObject.Copy();$copy.Fields=$event.Fields.Clone();$copy})
    $commit=@($events|Where-Object { $_.EventName -eq 'filetransfer_v6_sparse_write_committed' -and $_.Fields.next_chunk_index -eq '102' })[0]
    $arm=@($events|Where-Object { $_.EventName -eq 'filetransfer_v6_frontier_recent_progress_grace_deferred' -and
        $_.Fields.wake_generation -eq $commit.Fields.wake_generation -and [long]$_.Sequence -lt [long]$commit.Sequence }|Select-Object -Last 1)[0]
    switch($case){
        'duplicate' {$copy=$commit.PSObject.Copy();$copy.Fields=$commit.Fields.Clone();$events+=@($copy)}
        'at_deadline' {$commit.Fields.commit_decision_utc=$deadline.ToString('o')}
        'missing_clock' {$commit.Fields.Remove('commit_decision_utc')}
        'malformed_clock' {$commit.Fields.commit_decision_utc='invalid'}
        'late_commit' {$commit.Fields.commit_decision_utc=$deadline.AddTicks(1).ToString('o')}
        'future_clock' {$commit.Fields.commit_decision_utc=([DateTimeOffset]::new([long]$commit.Fields.log_writer_attempt_utc_ticks+1,[TimeSpan]::Zero)).ToString('o')}
        'stale_source' {$commit.Fields.commit_source_status='stale'}
        'authority_effect' {$commit.Fields.authority_effect='repair'}
        'wrong_count' {$commit.Fields.contiguous_chunks_committed='2'}
        'missing_arm' {$events=@($events|Where-Object {$_ -ne $arm})}
        'rearm_after_candidate' {
            $copy=$arm.PSObject.Copy();$copy.Fields=$arm.Fields.Clone();$copy.Sequence=[long]$commit.Sequence-1
            $copy.Fields.log_writer_sequence='999999';$copy.Fields.wake_generation='999999';$events+=@($copy)
        }
        'conflict' {
            $copy=$commit.PSObject.Copy();$copy.Fields=$commit.Fields.Clone();$copy.Fields.session_id='foreign';$events+=@($copy)
        }
        {$_ -in @('pause','terminal','source_replacement')} {
            $copy=$arm.PSObject.Copy();$copy.Fields=$arm.Fields.Clone();$copy.Sequence=[long]$commit.Sequence-1
            $copy.Fields.log_writer_sequence='999999'
            $copy.EventName=switch($case){'pause' {'filetransfer_v6_receiver_state_sent'} 'terminal' {'file_transfer_inbound_terminal'} 'source_replacement' {'filetransfer_data_session_removed'}}
            $copy.Fields.event=$copy.EventName;$copy.Fields.transfer_paused='1';$events+=@($copy)
        }
        {$_ -in $scope} {$commit.Fields[$case]='wrong'}
    }
    $expected=$case -in @('exact','duplicate','at_deadline')
    $plain=& $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation -Events $events
    $indexed=Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $events -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $events)
    foreach($result in @($plain,$indexed)){
        if($result.DistinctCount -ne 1 -or $result.RecoveredCount -ne [int]$expected -or $result.UnresolvedCount -ne [int](-not $expected)){
            throw "$case retained mismatch: $($result.RawCount)/$($result.DistinctCount)/$($result.RecoveredCount)/$($result.UnresolvedCount); flat=$($plain.RecoveredCount) indexed=$($indexed.RecoveredCount)"
        }
    }
    $serialized=@($events|Sort-Object Sequence,LineNumber|ForEach-Object {
        $f=$_.Fields;$parts=@($f.Keys|Sort-Object|ForEach-Object {"$_=$($f[$_])"})
        "[$($_.TimestampUtc.ToString('yyyy-MM-dd HH:mm:ss.fffffffZ'))] [INFO] [FileTransfer] $($parts -join '; '); gui_evidence_sequence=$($_.Sequence)"
    })
    $catalog=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
    $compact=@($serialized|Where-Object {$_ -match '(?:^|; )event=([^;]+)' -and $Matches[1] -cin $catalog})
    foreach($inputLines in @($serialized,$compact)){
        $result=Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure -Lines $inputLines
        if($result.Unresolved -eq $expected){throw "$case GUI mismatch"}
    }
    "pass $case"
}
"recent-progress-commit-clock-ok: $($cases.Count) cases"
