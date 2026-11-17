param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'app-reports-run','app-reports-scenario',$Directory)
if($null -eq $request) {throw 'App reports GUI parent missing'}
$process=[Diagnostics.Process]::new()
try {
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppReportsGui.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='app-reports-run'
    $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='app-reports-scenario'
    $start.EnvironmentVariables['NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR']=$Directory
    $process.StartInfo=$start; $reports=$request.StartFrontierGuiWorkerWithReports($process)
    if(-not $process.WaitForExit(60000)) {throw 'App reports GUI fixture deadline'}
    $stdout=$reports.Stdout.GetAwaiter().GetResult(); $stderr=$reports.Stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $stdout -notmatch 'original-app-reports-ok') {throw "App reports GUI failed: $stdout $stderr"}
    $request.Dispose(); if($reports.ParentRegistration.IsCurrent) {throw 'App reports GUI parent leaked'}
    'frontier-app-reports='+$Case
} finally {
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose()
}
