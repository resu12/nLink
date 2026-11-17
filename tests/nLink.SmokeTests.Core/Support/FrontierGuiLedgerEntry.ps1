param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'gui-ledger-run','gui-ledger-scenario',$Directory)
if($null -eq $request) {throw 'GUI ledger parent unavailable'}
$process=[Diagnostics.Process]::new(); $foreign=$null
try {
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerEntryWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $start.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='gui-ledger-run'
    $start.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='gui-ledger-scenario'
    $start.EnvironmentVariables['NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR']=$Directory
    # The real Clear-AppLogsIfPresent may only clear this fixture's log directory.
    $start.EnvironmentVariables['LOCALAPPDATA']=Join-Path $Directory 'fixture-local-app-data'
    $process.StartInfo=$start
    if($Case -ceq 'collision-ready') {$foreign=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.frontier-gui-start-v1.ready')}
    $reports=$request.StartFrontierGuiWorkerWithReports($process)
    if(-not $process.WaitForExit(40000)) {throw 'GUI ledger entry fixture deadline'}
    $stdout=$reports.Stdout.GetAwaiter().GetResult(); $stderr=$reports.Stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $stdout.TrimEnd() -cne 'ordinary ledger entry stdout' -or $stderr -cne 'ordinary ledger entry stderr') {
        throw "GUI ledger entry reports failed: $stdout $stderr"
    }
    if($null -ne $foreign -and $foreign.WaitOne(0)) {throw 'Foreign ready event mutated'}
    $request.Dispose()
    if($null -ne $reports.ParentRegistration -and $reports.ParentRegistration.IsCurrent) {throw 'Ledger parent registration survived cleanup'}
    'frontier-gui-ledger-entry='+$Case
} finally {
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose(); if($null -ne $foreign) {$foreign.Dispose()}
}
