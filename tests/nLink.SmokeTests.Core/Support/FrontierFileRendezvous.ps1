param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $request) {throw 'GUI parent request missing'}
$process=[Diagnostics.Process]::new(); $children=@()
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFileRendezvousGui.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $process.StartInfo=$start; $reports=$request.StartFrontierGuiWorkerWithReports($process)
    if(-not $process.WaitForExit(90000)) {throw 'GUI rendezvous fixture deadline'}
    $out=$reports.Stdout.GetAwaiter().GetResult(); $err=$reports.Stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $out -notmatch 'file-rendezvous-gui-ok') {throw "GUI rendezvous failed: $out $err"}
    if($Case.StartsWith('parent-death-')) {
        foreach($identity in ([IO.File]::ReadAllText((Join-Path $Directory 'children.json')) | ConvertFrom-Json)) {
            $child=$null; try {$child=[Diagnostics.Process]::GetProcessById($identity.Id)} catch [ArgumentException] { }
            if($null -ne $child) {
                if($child.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $identity.Creation) {$child.Dispose(); throw 'Child incarnation changed'}
                $children+=@($child); if(-not $child.WaitForExit(10000)) {throw 'App did not reject native GUI death'}
            }
        }
        foreach($ordinal in @(0,1)) {
            $result=[IO.File]::ReadAllText((Join-Path $Directory ('app-'+$ordinal+'.json'))) | ConvertFrom-Json
            if(-not $result.ParentExited -or $result.Acknowledged -or $result.Completed) {throw 'GUI death did not revoke pending app work'}
        }
    }
    'frontier-file-rendezvous='+$Case
} finally {
    foreach($child in $children) {if(-not $child.HasExited) {$child.Kill(); [void]$child.WaitForExit(5000)}; $child.Dispose()}
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose()
}
