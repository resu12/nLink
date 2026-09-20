param([string]$RepoRoot,[string]$AssemblyPath,[string]$Directory,[string]$OnlyCase='',[string]$Mode='catalog')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
$cases=@('valid_v6','valid_v4','foreign_claim','cancel_before_create','cancel_before_prepare','cancel_during_prepare','cancel_after_prepare',
    'cancel_after_take','metadata_after_prepare','lease_disposed','attachment_completed')
if($Mode -ceq 'writer') {
    $cases=@('valid_v6','valid_v4','cancel_before_writer','cancel_before_write','cancel_after_write','metadata_after_write',
        'writer_failure','wrong_run','late_bind','catalog_cancel','attachment_completed','lease_disposed','deferred_write','receipt_capacity')
}
if($Mode -cin @('marker','source')) {
    $cases=@('valid_v6','valid_v4','two_payloads','early_marker','completion_failure','marker_failure','deferred_marker',
        'drain_failure','deferred_drain','abandoned_drain','missing_marker','late_marker','duplicate_marker',
        'cancel_before_marker','cancel_after_marker','cancel_after_drain','metadata_after_drain',
        'catalog_cancel','attachment_completed','lease_disposed','after_drain_write')
}
if($Mode -ceq 'source') { $cases+=@('source_substituted','source_mutated') }
if($Mode -ceq 'payload') {
    $cases=@('valid_v6','valid_v4','two_payloads','early_output','payload_bytes_fault',
        'source_substituted','source_mutated','cancel_after_drain','metadata_after_drain',
        'catalog_cancel','attachment_completed','lease_disposed','after_drain_write','deferred_drain')
}
if($OnlyCase) { $cases=@($OnlyCase) }
$results=[Collections.Generic.List[object]]::new()
foreach($case in $cases) {
    $owned=Join-Path $Directory $case
    [void][IO.Directory]::CreateDirectory($owned)
    $request=[NLink.Tools.LocalFrontierAppRequest]::TryCreate('frontier-catalog-native','frontier-catalog-native')
    if($null -eq $request) { throw 'Native request unavailable' }
    $name=$request.Name; $events=@{}; $process=$null
    foreach($part in @('begin','ready','release')) { $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part) }
    try {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=(Get-Command pwsh -ErrorAction Stop).Source
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR']=$owned
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH']=(Join-Path $owned 'writer.log')
        $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='frontier-catalog-native'
        $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='frontier-catalog-native'
        $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierNativeCatalogWorker.ps1'
        $resultPath=Join-Path $owned 'result.json'
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$worker,'-AssemblyPath',$AssemblyPath,'-Name',$name,'-RequestId',$request.RequestId.ToString('D'),'-Case',$case,'-ResultPath',$resultPath,'-Mode',$Mode)
        $start.Arguments=($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' }) -join ' '
        $process=[Diagnostics.Process]::Start($start)
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $request.BindApp($process)) { throw 'Native app binding failed' }
        [void]$events.begin.Set()
        if(-not $events.ready.WaitOne(20000)) {
            if($process.HasExited) { throw ('Worker failed: '+$stdout.GetAwaiter().GetResult()+' '+$stderr.GetAwaiter().GetResult()) }
            throw ('Worker checkpoint timed out: '+$case)
        }
        if($case -ceq 'parent_death') {
            'frontier-native-catalog-parent-death='+(@{ProcessId=$process.Id;Creation=$process.StartTime.ToUniversalTime().ToFileTimeUtc();Result=$resultPath} | ConvertTo-Json -Compress)
            [Environment]::Exit(0)
        }
        if($case.StartsWith('cancel_')) { $request.Cancel() }
        if($case -cin @('metadata_after_prepare','metadata_after_write','metadata_after_drain')) {
            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name); $view=$map.CreateViewAccessor()
            try { $view.Write(64L,[byte]0) } finally { $view.Dispose(); $map.Dispose() }
        }
        [void]$events.release.Set()
        if(-not $process.WaitForExit(20000)) { throw ('Worker exit timed out: '+$case) }
        $process.WaitForExit()
        $out=$stdout.GetAwaiter().GetResult(); $err=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0 -or $out -notmatch 'frontier-native-catalog-worker=') { throw ('Worker failed: '+$case+' '+$out+' '+$err) }
        $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if(-not $result.Passed -or $result.PayloadPublished) { throw 'Invalid child result' }
        $completion=$request.TakeAfterExit()
        $expected=$case -cin @('valid_v6','valid_v4','foreign_claim','attachment_completed')
        if($Mode -ceq 'writer') { $expected=$case -cin @('valid_v6','valid_v4','writer_failure','wrong_run','late_bind','catalog_cancel','attachment_completed','deferred_write','receipt_capacity') }
        if($Mode -cin @('marker','source','payload')) { $expected=-not ($case.StartsWith('cancel_') -or $case -cin @('metadata_after_drain','lease_disposed','parent_death')) }
        if(($null -ne $completion) -ne $expected -or $null -ne $request.TakeAfterExit()) { throw 'Wrong parent attachment receipt' }
        if($null -ne $completion -and ($completion.LiveProofEligible -or $completion.NativeWriterCustodyEstablished -or $completion.PublicationCompletionIssued)) { throw 'Attachment granted publication authority' }
        $results.Add($result)
    } finally {
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        $request.Dispose()
        foreach($event in $events.Values) { $event.Dispose() }
    }
    $leftover=$null
    try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name) } catch [IO.FileNotFoundException] { }
    if($null -ne $leftover) { $leftover.Dispose(); throw 'Native request leaked after cleanup' }
}
'frontier-native-catalog='+(@{Cases=$results.Count;Results=@($results.ToArray())} | ConvertTo-Json -Depth 4 -Compress)
