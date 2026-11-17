param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$completion=$null
$null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $Directory 'gui') -AcceptedCompletion ([ref]$completion)
if($null -eq $completion) { throw 'Actual GUI parent did not supply completion' }
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$completion.AcceptanceRunId
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$completion.ScenarioRunId
$sourcePath=$completion.Outputs[3].Path; $writerPath=$completion.Outputs[1].Path
$sourceBytes=[IO.File]::ReadAllBytes($sourcePath); $writerBytes=[IO.File]::ReadAllBytes($writerPath)
if($completion.Outputs[3].LineCount -ne 1 -or $completion.Outputs[1].LineCount -ne 3) { throw 'Actual generated fixture changed' }
$rows=[Collections.Generic.List[object]]::new()
foreach($mode in @('valid','null-context','forged-context','source-changed','milestone-merged','wrong-path','wrong-run','wrong-count',
    'writer-changed','writer-replaced','writer-short','partial-source','source-no-eof','early-writer','source-disposed',
    'partial-writer','cancel-writer','source-disposed-during-writer','budget','wrong-request','closed','malformed-request')) {
    $output=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($output)
    $path=$sourcePath; $candidate=$completion; $lineCount=$completion.Outputs[3].LineCount
    $run=$completion.AcceptanceRunId; $lease=$null; $request=$null; $process=$null; $replaced=$false
    try {
        switch($mode) {
            'null-context' { $candidate=$null }
            'forged-context' { $candidate=$completion | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
            'source-changed' { $bytes=[byte[]]$sourceBytes.Clone(); $bytes[0]=[byte][char]'X'; [IO.File]::WriteAllBytes($sourcePath,$bytes) }
            'milestone-merged' { [IO.File]::AppendAllText($sourcePath,"`nevent=test_milestone",[Text.UTF8Encoding]::new($false)); $lineCount++ }
            'wrong-path' { $path=Join-Path $output 'copy.log'; [IO.File]::WriteAllBytes($path,$sourceBytes) }
            'wrong-run' { $run+=':wrong' }
            'wrong-count' { $lineCount++ }
            'writer-changed' { $bytes=[byte[]]$writerBytes.Clone(); $bytes[0]=[byte][char]'X'; [IO.File]::WriteAllBytes($writerPath,$bytes) }
            'writer-replaced' { [IO.File]::Move($writerPath,$writerPath+'.original'); $replaced=$true; [IO.File]::WriteAllBytes($writerPath,$writerBytes) }
            'writer-short' { [IO.File]::WriteAllBytes($writerPath,[byte[]]@(1,2,3)) }
        }
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$run
        $lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($path)).Lease
        $attempt=[guid]::NewGuid().ToString('N')
        $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId $attempt -Owner Wrapper -LineCount $lineCount -WriterContext $candidate
        if($null -eq $request) { throw "Source ownership lost: $mode" }
        if($mode -ceq 'malformed-request') {
            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name); $view=$map.CreateViewAccessor()
            try { $view.Write(20L,98305) } finally { $view.Dispose(); $map.Dispose() }
        }
        $settings=Join-Path $output 'settings.json'; $resultPath=Join-Path $output 'result.json'
        [IO.File]::WriteAllText($settings,(@{mode=$mode;path=$path;result=$resultPath} | ConvertTo-Json))
        $process=[Diagnostics.Process]::new()
        $process.StartInfo.FileName=(Get-Process -Id $PID).Path
        $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalRetainedWriterWorker.ps1')+'" "'+$RepoRoot+'" "'+$settings+'"'
        $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
        if(-not $process.Start()) { throw 'Retained worker did not start' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $request.BindWorker($process)) { throw 'Retained worker bind failed' }
        if($mode -ceq 'closed') { $request.Dispose() }
        if(-not $process.WaitForExit(15000)) { throw 'Retained worker test timeout' }
        if($process.ExitCode -ne 0) { throw "$mode : $($stdout.Result) $($stderr.Result)" }
        $result=[IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
        $attached=$mode -cnotin @('wrong-request','closed','malformed-request')
        $context=$attached -and $mode -cnotin @('null-context','forged-context','source-changed','milestone-merged','wrong-path','wrong-run','wrong-count')
        $opened=$context -and $mode -cnotin @('writer-replaced','writer-short','partial-source','source-no-eof','early-writer','source-disposed')
        $verified=$mode -ceq 'valid'
        if($result.attached -ne $attached -or $result.writerContext -ne $context -or $result.writerOpened -ne $opened -or
            $result.writerVerified -ne $verified -or $result.verifiedAfterDispose -or ($context -and $result.guiRequest -cne $completion.RequestId.ToString('D')) -or
            ($attached -and $result.retainedRequest.Replace('-','') -cne $attempt)) { throw "Retained binding drift: $mode : $($result | ConvertTo-Json -Compress)" }
        $rows.Add(@{Mode=$mode;Attached=$attached;WriterContext=$context;WriterOpened=$opened;WriterVerified=$verified})
    } finally {
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        if($null -ne $request) { $request.Dispose() }
        if($null -ne $lease) { $lease.Dispose() }
        [IO.File]::WriteAllBytes($sourcePath,$sourceBytes)
        if($replaced) { Remove-Item -LiteralPath $writerPath -Force; [IO.File]::Move($writerPath+'.original',$writerPath) }
        else { [IO.File]::WriteAllBytes($writerPath,$writerBytes) }
    }
}
'retained-writer-binding='+(@{Cases=$rows.ToArray();ActualGuiCompletion=$true;AuthorityEvaluations=0;LiveExport=$false} | ConvertTo-Json -Depth 5 -Compress)
