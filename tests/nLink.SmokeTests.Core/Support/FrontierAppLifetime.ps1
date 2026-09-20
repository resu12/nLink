param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $request) {throw 'GUI parent request missing'}
$process=[Diagnostics.Process]::new()
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppLifetimeGui.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $process.StartInfo=$start; $reports=$request.StartFrontierGuiWorkerWithReports($process)
    if(-not $process.WaitForExit(90000)) {throw 'GUI app-lifetime fixture deadline'}
    $out=$reports.Stdout.GetAwaiter().GetResult(); $err=$reports.Stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $out -notmatch 'app-lifetime-gui-ok') {throw "GUI app-lifetime failed: $out $err"}
    'frontier-app-lifetime='+$Case
} finally {
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose()
}
