$pipeCase=$mode.Substring('pipes-'.Length)
$savedMode=$mode; $mode='output-valid'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticOutputWorker.ps1')
$mode=$savedMode
if($pipeCase.StartsWith('terminal-',[StringComparison]::Ordinal)) {
    $fullView | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $TerminalDirectory 'filetransfer-coordinator-snapshot-full-summary.json') -Encoding UTF8
    $compactView | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $TerminalDirectory 'filetransfer-coordinator-snapshot-compact-summary.json') -Encoding UTF8
    [pscustomobject]@{Verdict='pass'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TerminalDirectory 'filetransfer-coordinator-snapshot-parity-summary.json') -Encoding UTF8
}
if($pipeCase -cin @('held-stdout','held-stderr')) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class PrimaryPipeFixture { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind); [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags); }'
    foreach($kind in @(-11,-12)) {
        $inherit=if(($pipeCase -ceq 'held-stdout' -and $kind -eq -11) -or ($pipeCase -ceq 'held-stderr' -and $kind -eq -12)) {1} else {0}
        if(-not [PrimaryPipeFixture]::SetHandleInformation([PrimaryPipeFixture]::GetStdHandle($kind),1,$inherit)) {throw 'Primary pipe inheritance fixture failed'}
    }
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Command pwsh).Source
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $args=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiOutputWorker.ps1'),'-HoldPipe','-SignalName',($Name+'.test.worker'))
    $start.Arguments=($args | ForEach-Object {'"'+$_+'"'}) -join ' '
    $holder=[Diagnostics.Process]::Start($start)
    $ready=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.held-ready')
    try {if(-not $ready.WaitOne(15000)) {throw 'Primary pipe holder deadline'}} finally {$ready.Dispose(); $holder.Dispose()}
}
$bytes=[byte[]]::new(70003); for($i=0;$i -lt $bytes.Length;$i++) {$bytes[$i]=[byte]($i % 256)}
$out=[Console]::OpenStandardOutput(); $err=[Console]::OpenStandardError()
$out.Write($bytes,0,$bytes.Length); $out.Flush(); $err.Write($bytes,0,$bytes.Length); $err.Flush()
