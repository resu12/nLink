param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$Mode='bounded')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$support=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support'
$output=Join-Path $Directory ('primary-'+$PSVersionTable.PSVersion.Major)
[void][IO.Directory]::CreateDirectory($output)
$completion=$null
$null=& (Join-Path $support 'LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $output 'gui') `
    -AcceptedCompletion ([ref]$completion) -GeneratedSource (Join-Path $Directory 'nlink.log') -GeneratedRun $RunId -GeneratedScenario $ScenarioId
if($null -eq $completion) { throw 'Actual writer completion missing' }
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$RunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$ScenarioId
$lease=$null; $request=$null; $process=$null
try {
    $lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($completion.Outputs[3].Path)).Lease
    $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId ([guid]::NewGuid().ToString('N')) `
        -Owner Wrapper -LineCount $completion.Outputs[3].LineCount -WriterContext $completion
    if($null -eq $request) { throw 'Actual retained request missing' }
    $settings=Join-Path $output 'settings.json'
    [IO.File]::WriteAllText($settings,(@{path=$completion.Outputs[3].Path;output=$output;mode=$Mode;
        receipt=(Join-Path $Directory 'writer-receipt.json')} | ConvertTo-Json))
    $process=[Diagnostics.Process]::new(); $process.StartInfo.FileName=(Get-Process -Id $PID).Path
    $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $support 'FrontierPublicationPrimaryWorker.ps1')+'" "'+$RepoRoot+'" "'+$settings+'"'
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
    if(-not $process.Start()) { throw 'Worker launch failed' }
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $request.BindWorker($process)) { throw 'Actual worker binding failed' }
    if(-not $process.WaitForExit(120000)) { throw 'Offline primary worker timeout' }
    if($process.ExitCode -ne 0) { throw ($stdout.Result+$stderr.Result) }
    $stdout.Result
} finally {
    if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
    if($null -ne $request) { $request.Dispose() }; if($null -ne $lease) { $lease.Dispose() }
}
