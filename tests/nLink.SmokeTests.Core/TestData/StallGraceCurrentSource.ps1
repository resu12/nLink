param([string]$RepoRoot)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'GUI parse failed'}
$functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))
if($functions.Count -ne 1){throw 'Expected actual GUI selector'}
Invoke-Expression $functions[0].Extent.Text
function Fixture {
    $n=0
    foreach($line in [IO.File]::ReadLines((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceCurrentSource.txt'))){
        $n++; ConvertFrom-FileTransferLogLine -Line $line -FilePath 'fixture.txt' -LineNumber $n -Sequence $n
    }
}
function At([object[]]$Events,[string]$Sequence){return @($Events | Where-Object {$_.Fields.log_writer_sequence -ceq $Sequence})[0]}
function Closure([object[]]$Events){
    Get-FileTransferStallGraceCurrentSourceRecovery -Index (New-FileTransferStallGraceCurrentSourceIndex $Events) `
        -Candidate (At $Events '14842') -HistoricalProgress (At $Events '13579')
}
function Gui([object[]]$Events){
    $lines=@(foreach($event in ($Events | Sort-Object Sequence)){
        '[2026-09-06 04:33:00Z] [INFO] [Fixture] '+(@($event.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($event.Fields[$_])"}) -join '; ')+"; gui_evidence_sequence=$($event.Sequence)"
    })
    Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines
}
function Retained([object[]]$Events){
    $result=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $Events -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $Events)
    return @($result.RawCount,$result.DistinctCount,$result.RecoveredCount,$result.UnresolvedCount) -join '/'
}
function Reject([object[]]$Events,[string]$Label){
    if($null -ne (Closure $Events).RecoveryEvent){throw "$Label shared recovered"}
    if(-not (Gui $Events).Unresolved){throw "$Label GUI recovered or excluded"}
    if((Retained $Events) -cne '1/1/0/1'){throw "$Label retained did not stay unresolved"}
}
$good=@(Fixture)
$proof=Closure $good
if($proof.CurrentSource.Fields.runtime_generation -cne '7' -or $proof.HistoricalProgress.Fields.runtime_generation -cne '6' -or
    $proof.RecoveryEvent.Fields.log_writer_sequence -cne '15454'){throw 'Wrong current/historical/recovery binding'}
foreach($width in @(1,8,16)){
    $script:FileTransferFallbackFrontierGracePartitionWidth=$width
    if((Retained $good) -cne '1/1/1/0'){throw "width $width retained recovery missing"}
    if((Gui $good).Unresolved){throw "width $width actual GUI recovery missing"}
}
$script:FileTransferFallbackFrontierGracePartitionWidth=1
foreach($mutation in @(
    @('14165','runtime_generation','6'),@('14622','route_publication_generation','8'),@('14623','contiguous_committed_chunk_index','4361'),
    @('15175','authority_id','other'),@('15175','repair_generation','0'),@('15175','runtime_generation','6'),
    @('15175','authority_schema','1'),@('15175','repair_request_key','other'),@('15175','range_count','2'),
    @('15175','log_writer_attempt_utc_ticks','639242659873490000'),@('15194','request_correlation_id','other'),
    @('15194','accepted_any','0'),@('15194','repair_request_key','other'),@('15203','dispatch_observed','0'),
    @('15194','control_plane_generation','0'),@('15194','first_start_chunk_index','4361'),
    @('15205','repair_request_id','other'),@('15443','settlement_outcome','Superseded'),@('15443','dispatch_observed','0'),
    @('15444','current_frontier_chunk_index','4362'),@('15447','data_session_identity','other'),
    @('15447','data_session_generation','2'),@('15447','receiver_state_epoch','62'),@('15447','decision_status','Rejected'),
    @('15454','transport_epoch','2'),@('15454','transfer_paused','1'),@('15454','epoch','62'))){
    $events=@(Fixture); (At $events $mutation[0]).Fields[$mutation[1]]=$mutation[2]
    Reject $events ($mutation -join ':')
}
foreach($missing in @('15175','15194','15203','15205','15443','15444','15447','15454')){
    Reject @(Fixture | Where-Object {$_.Fields.log_writer_sequence -cne $missing}) "missing $missing"
}
Reject @(Fixture | Where-Object {$_.Fields.log_writer_sequence -notin @('14165','14622')}) 'missing current publication'
Reject @(Fixture | Where-Object {$_.Fields.log_writer_sequence -notin @('14221','14623')}) 'missing current receiver state'
foreach($poison in @('filetransfer_data_session_removed','filetransfer_data_session_recreated','file_transfer_inbound_terminal',
        'filetransfer_live_route_epoch_terminal','filetransfer_terminalized_by_session_end')){
    $events=@(Fixture);$extra=At @(Fixture) '14842';$extra.Fields.event=$poison;$extra.EventName=$poison
    $extra.Fields.log_writer_sequence='14900'
    # Integer sequence locations can contain more than one distinct writer record.
    $extra.Sequence=(At $events '15175').Sequence
    Reject (@($events)+@($extra)) $poison
}
$events=@(Fixture);$conflict=At @(Fixture) '15194';$conflict.Fields.accepted_any='0'
Reject (@($events)+@($conflict)) 'conflicting physical copy'
$events=@(Fixture);$conflict=At @(Fixture) '15454';$conflict.Fields.transfer_paused='1';$conflict.Sequence+=50
Reject (@($events)+@($conflict)) 'nonadjacent conflicting closing copy'
$events=@(Fixture);$competing=At @(Fixture) '15175';$competing.Fields.log_writer_sequence='15176';$competing.Fields.authority_id='other'
Reject (@($events)+@($competing)) 'competing grant'
foreach($reuse in @('authority_id','repair_generation')){
    $events=@(Fixture);$old=(At $events '13579').Fields[$reuse];$current=(At $events '15175').Fields.authority_id
    foreach($event in $events){if($event.Fields['authority_id'] -ceq $current){$event.Fields[$reuse]=$old}}
    Reject $events "retired $reuse reused across source change"
}
$events=@(Fixture);$bad=At @(Fixture) '14842';$bad.Fields.runtime_generation='8';$bad.Fields.log_writer_sequence='14843'
$bad.Fields.event='filetransfer_route_selected';$bad.EventName='filetransfer_route_selected'
$restored=At @(Fixture) '14842';$restored.Fields.log_writer_sequence='14844'
$restored.Fields.event='filetransfer_route_selected';$restored.EventName='filetransfer_route_selected'
Reject (@($events)+@($bad,$restored)) 'ABA current source'
$duplicates=@($good)+@((At $good '15194'),(At $good '15454'))
if($null -eq (Closure $duplicates).RecoveryEvent -or (Gui $duplicates).Unresolved){throw 'Identical duplicates changed proof'}
# Location projection and unrelated records must not change writer-bound proof.
$baseline=@($proof.CurrentSource.Fields,$proof.HistoricalProgress.Fields,$proof.RecoveryEvent.Fields | ForEach-Object {
    $fields=[ordered]@{};foreach($key in ($_.Keys | Sort-Object)){$fields[$key]=$_[$key]};$fields
}) | ConvertTo-Json -Depth 4 -Compress
foreach($offset in @(0,17,53)){
    $events=@(Fixture)
    foreach($event in $events){$event.Sequence+=$offset;$event.LineNumber+=$offset;$event.FilePath="projection-$offset.txt"}
    $projected=Closure $events
    $normalized=@($projected.CurrentSource.Fields,$projected.HistoricalProgress.Fields,$projected.RecoveryEvent.Fields | ForEach-Object {
        $fields=[ordered]@{};foreach($key in ($_.Keys | Sort-Object)){$fields[$key]=$_[$key]};$fields
    }) | ConvertTo-Json -Depth 4 -Compress
    if($normalized -cne $baseline -or $projected.RecoveryEvent.Fields.log_writer_sequence -cne '15454' -or (Gui $events).Unresolved -or
        (Retained $events) -cne '1/1/1/0'){throw "projection $offset differs"}
}
'current-source-ok'
