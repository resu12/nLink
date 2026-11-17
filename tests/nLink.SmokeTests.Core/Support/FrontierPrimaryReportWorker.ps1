param([string]$RepoRoot,[string]$Directory,[string]$Signal,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$begin=[Threading.EventWaitHandle]::OpenExisting($Signal+'.begin')
try {if(-not $begin.WaitOne(20000)) {throw 'Report worker begin deadline'}} finally {$begin.Dispose()}
[IO.File]::AppendAllText((Join-Path $Directory 'starts.txt'),"started`n")
foreach($pair in @(@('stdout.bin',[Console]::OpenStandardOutput()),@('stderr.bin',[Console]::OpenStandardError()))) {
    $bytes=[IO.File]::ReadAllBytes((Join-Path $Directory $pair[0]))
    $pair[1].Write($bytes,0,$bytes.Length); $pair[1].Flush()
}
if($Case -cin @('held-stdout','held-stderr')) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class ReportPipeFixture { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind); [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags); }'
    foreach($kind in @(-11,-12)) {
        $inherit=if(($Case -ceq 'held-stdout' -and $kind -eq -11) -or ($Case -ceq 'held-stderr' -and $kind -eq -12)) {1} else {0}
        if(-not [ReportPipeFixture]::SetHandleInformation([ReportPipeFixture]::GetStdHandle($kind),1,$inherit)) {throw 'Report pipe inheritance failed'}
    }
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Command pwsh).Source
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiOutputWorker.ps1'),'-HoldPipe','-SignalName',$Signal)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $holder=[Diagnostics.Process]::Start($start)
    $ready=[Threading.EventWaitHandle]::OpenExisting($Signal+'.held-ready')
    try {if(-not $ready.WaitOne(15000)) {throw 'Report pipe holder deadline'}} finally {$ready.Dispose(); $holder.Dispose()}
}
if($Case -ceq 'nonzero') {exit 7}
