param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$FinalizerCase = '')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'cleanup-adoption-run','cleanup-adoption-scope',$Directory)
if($null -eq $request) {throw 'Cleanup parent request missing'}
$process=[Diagnostics.Process]::new()
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCleanupAdoptionGui.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    if($FinalizerCase) {$arguments+=@('-FinalizerCase',$FinalizerCase)}
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $process.StartInfo=$start; $reports=$request.StartFrontierGuiWorkerWithReports($process)
    $custody=$null
    if($FinalizerCase.StartsWith('persistence-')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFinalizerHandoff.ps1') -Phase wait
    } elseif(-not $process.WaitForExit(80000)) {throw 'Cleanup adoption fixture deadline'}
    $out=$reports.Stdout.GetAwaiter().GetResult(); $err=$reports.Stderr.GetAwaiter().GetResult()
    $expectedExit=if($FinalizerCase -ceq 'persistence-handoff-gui-failure') {7} else {0}
    if($process.ExitCode -ne $expectedExit -or $out -notmatch 'cleanup-adoption-gui-ok') {throw "Cleanup adoption failed: $out $err"}
    if($FinalizerCase.StartsWith('persistence-handoff-')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFinalizerHandoff.ps1') -Phase result
    }
    if($FinalizerCase) {
        if(-not $out.Contains('frontier-finalizer-source='+$FinalizerCase)) {throw 'Finalizer source result missing'}
        'frontier-finalizer-source='+$FinalizerCase
    }
    'frontier-cleanup-adoption='+$Case
} finally {
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose()
}
