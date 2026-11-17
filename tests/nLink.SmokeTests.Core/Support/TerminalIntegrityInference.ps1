param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Inference controls cannot evaluate authority'}
# Execute the exact original inference block, without copying its predicates or
# rerunning an authority evaluation for each detached input control.
$body=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.ToString()
$start='$gui = Read-FileTransferCompactGuiSummary -ArtifactDir $ArtifactDir'
$end='[object[]]$routeChronologyEvents = @('
foreach($marker in @($start,$end)) {
    if($body.Split([string[]]@($marker),[StringSplitOptions]::None).Length -ne 2) {throw 'Original terminal inference seam changed'}
}
$begin=$body.IndexOf($start,[StringComparison]::Ordinal); $finish=$body.IndexOf($end,[StringComparison]::Ordinal)
if($finish -le $begin) {throw 'Original inference range changed'}
$inference=[scriptblock]::Create($body.Substring($begin,$finish-$begin))
$inbound='event=transfer_terminal; direction=inbound; session_id=session; transfer_id=transfer; error_code=(none); reason=Transfer complete.'
$outbound=$inbound.Replace('direction=inbound','direction=outbound')
$results=[Collections.Generic.List[object]]::new()
foreach($case in @('absent_summary','absent_integrity','false_integrity','true_integrity','gui_complete_false_integrity','gui_complete_true_integrity',
    'missing','duplicate_direction','duplicate_pair','foreign_transfer','foreign_session','canceled','errored','app_terminals','malformed_summary')) {
    $lines=@($inbound,$outbound); $summary=$null; $completed=$true; $integrity=$false
    switch($case) {
        'absent_integrity' {$summary=@{completed=$false}}
        'false_integrity' {$summary=@{completed=$false;integrityOk=$false}}
        'true_integrity' {$summary=@{completed=$false;integrityOk=$true}; $integrity=$true}
        'gui_complete_false_integrity' {$summary=@{completed=$true;integrityOk=$false}; $lines=@()}
        'gui_complete_true_integrity' {$summary=@{completed=$true;integrityOk=$true}; $lines=@(); $integrity=$true}
        'missing' {$lines=@($inbound); $completed=$false}
        'duplicate_direction' {$lines=@($inbound,$inbound); $completed=$false}
        'duplicate_pair' {$lines=@($inbound,$inbound,$outbound)}
        'foreign_transfer' {$lines=@($inbound,$outbound.Replace('transfer_id=transfer','transfer_id=foreign'))}
        'foreign_session' {$lines=@($inbound,$outbound.Replace('session_id=session','session_id=foreign'))}
        'canceled' {$lines=@($inbound+'; state=Canceled',$outbound+'; state=Canceled'); $completed=$false}
        'errored' {$lines=@($inbound.Replace('error_code=(none)','error_code=hash_mismatch'),$outbound); $completed=$false}
        'app_terminals' {$lines=@('event=file_transfer_inbound_terminal; state=Completed; error_code=(none)',
            'event=file_transfer_outbound_terminal; state=Completed; error_code=(none)')}
    }
    $ArtifactDir=Join-Path $Directory $case; [void][IO.Directory]::CreateDirectory($ArtifactDir)
    $path=Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if($null -ne $summary) {[IO.File]::WriteAllText($path,($summary | ConvertTo-Json -Compress))}
    if($case -ceq 'malformed_summary') {[IO.File]::WriteAllText($path,'{')}
    $events=@(for($i=0;$i -lt $lines.Count;$i++) {
        ConvertFrom-FileTransferLogLine -Line ('[2026-09-25 00:00:00Z] [INFO] [Test] '+$lines[$i]) -FilePath 'detached.log' -LineNumber ($i+1) -Sequence ($i+1)
    })
    $Index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @('detached.log')
    . $inference
    if($guiCompleted -ne $completed -or $guiIntegrityOk -ne $integrity) {throw "Terminal inference changed: $case completed=$guiCompleted integrity=$guiIntegrityOk"}
    # Completion reporting remains compatible; even foreign/duplicate pairs
    # cannot supply the missing integrity bit. This is not an owner-bound pair proof.
    $results.Add(@{Case=$case;Completed=$guiCompleted;IntegrityOk=$guiIntegrityOk})
}
[IO.File]::WriteAllText((Join-Path $Directory 'result.json'),(@{Cases=$results.ToArray();AuthorityEvaluations=0;DetachedControls=$true} | ConvertTo-Json -Depth 4 -Compress))
'terminal-evidence=pass'
