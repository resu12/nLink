param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalGuiWriterRequest+FrontierOutputCapture',$true)
$checks=0
foreach($case in @('empty','ascii','utf8-bom','utf16-bom','invalid-utf8','split-unicode','trailing-newline','capture-fault','revoke','over-retention','nonzero','held-stdout','held-stderr','read-fault')) {
    $root=[IO.Path]::Combine($Directory,$case); [void][IO.Directory]::CreateDirectory($root)
    $bytes=[Text.Encoding]::UTF8.GetBytes("ordinary report`r`n")
    switch($case) {
        'empty' {$bytes=[byte[]]::new(0)}
        'utf8-bom' {$bytes=[byte[]](@(239,187,191)+$bytes)}
        'utf16-bom' {$bytes=[byte[]]([Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes("unicode $([char]0x20ac)"))}
        'invalid-utf8' {$bytes=[byte[]]@(240,159,255,65,192,175)}
        'split-unicode' {$bytes=[Text.Encoding]::UTF8.GetBytes(('x'*4095)+[char]::ConvertFromUtf32(0x1f642)+('y'*4095)+[char]0x20ac)}
        'trailing-newline' {$bytes=[Text.Encoding]::UTF8.GetBytes("line`r`n`r`n")}
        'over-retention' {$bytes=[Text.Encoding]::ASCII.GetBytes('z'*(64MB+1))}
    }
    [IO.File]::WriteAllBytes((Join-Path $root 'stdout.bin'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $root 'stderr.bin'),[Text.Encoding]::UTF8.GetBytes('ordinary stderr'))
    $signal='Local\nlink-report-test-'+[guid]::NewGuid().ToString('N'); $events=@{}
    foreach($name in @('begin','held-ready','held-release')) {$events[$name]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.'+$name)}
    $process=[Diagnostics.Process]::new(); $start=$process.StartInfo
    $start.FileName=(Get-Process -Id $PID).Path; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'FrontierPrimaryReportWorker.ps1'),'-RepoRoot',$RepoRoot,'-Directory',$root,'-Signal',$signal,'-Case',$case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $prefix=[IO.Path]::Combine($root,'raw'); $capture=$type.GetConstructors($flags)[0].Invoke([object[]]@($prefix))
    try {
        if(-not $process.Start()) {throw 'Report fixture did not start'}
        $encoding=$process.StandardOutput.CurrentEncoding
        if($case -ceq 'read-fault') {$process.StandardOutput.BaseStream.Dispose()}
        $tasks=$type.GetMethod('StartReports',$flags).Invoke($capture,[object[]]@($process))
        if($case -ceq 'capture-fault') {$type.GetField('stdoutTarget',$flags).GetValue($capture).Dispose()}
        if($case -ceq 'revoke') {$type.GetMethod('Revoke',$flags).Invoke($capture,$null)}
        [void]$events.begin.Set()
        if(-not $process.WaitForExit(20000)) {throw 'Report fixture process deadline'}
        if($case -cin @('held-stdout','held-stderr')) {
            $held=if($case -ceq 'held-stdout') {0} else {1}
            if($tasks[$held].IsCompleted -or $type.GetProperty('Drained',$flags).GetValue($capture,$null)) {throw 'Process exit substituted for report EOF'}
            [void]$events['held-release'].Set()
        }
        if($case -ceq 'read-fault') {
            $failed=$false; try {[void]$tasks[0].GetAwaiter().GetResult()} catch {$failed=$true}
            if(-not $failed) {throw 'Primary read failure was swallowed'}
        } else {
            if($process.ExitCode -ne $(if($case -ceq 'nonzero') {7} else {0})) {throw 'Original exit code changed'}
            for($i=0;$i -lt 2;$i++) {
                $inputPath=Join-Path $root $(if($i -eq 0) {'stdout.bin'} else {'stderr.bin'})
                $reader=[IO.StreamReader]::new($inputPath,$encoding,$true)
                try {$expected=$reader.ReadToEnd()} finally {$reader.Dispose()}
                $actual=$tasks[$i].GetAwaiter().GetResult()
                if($actual -cne $expected) {throw ('Original report text changed: '+$case)}
                Set-Content -LiteralPath (Join-Path $root ($i.ToString()+'.actual.txt')) -Value $actual -Encoding UTF8
                Set-Content -LiteralPath (Join-Path $root ($i.ToString()+'.expected.txt')) -Value $expected -Encoding UTF8
                $hashes=@(foreach($kind in @('actual','expected')) {
                    $stream=[IO.File]::OpenRead((Join-Path $root ($i.ToString()+'.'+$kind+'.txt'))); $sha=[Security.Cryptography.SHA256]::Create()
                    try {[BitConverter]::ToString($sha.ComputeHash($stream))} finally {$sha.Dispose(); $stream.Dispose()}
                })
                if($hashes[0] -cne $hashes[1]) {throw 'Original Set-Content bytes changed'}
            }
            $retained=$case -cnotin @('capture-fault','revoke','over-retention')
            if($type.GetProperty('Drained',$flags).GetValue($capture,$null) -ne $retained) {throw ('Diagnostic capture status mismatch: '+$case)}
        }
        if(@([IO.File]::ReadAllLines((Join-Path $root 'starts.txt'))).Count -ne 1) {throw 'Worker started more than once'}
        $checks++
    } finally {
        [void]$events['held-release'].Set(); $type.GetMethod('Revoke',$flags).Invoke($capture,$null)
        if(-not $process.HasExited) {$process.Kill()}; $process.Dispose()
        foreach($event in $events.Values) {$event.Dispose()}
    }
}
'frontier-primary-reports='+$checks
