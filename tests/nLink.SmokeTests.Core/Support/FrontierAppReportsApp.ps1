param([string]$RepoRoot,[string]$Directory,[string]$Signal,[string]$Case,[string]$Name='', [guid]$RequestId=[guid]::Empty,[switch]$HoldPipe)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$events=@{}; $lease=$null
foreach($part in @('emit','emitted','exit','held','holder-exit','waiting','attached')) {$events[$part]=[Threading.EventWaitHandle]::OpenExisting($Signal+'.'+$part)}
try {
    if($HoldPipe) {[void]$events.held.Set(); if(-not $events['holder-exit'].WaitOne(20000)) {throw 'Holder deadline'}; exit 0}
    [IO.File]::AppendAllText((Join-Path $Directory 'app-launches.txt'),"$PID`n")
    if($Case -ceq 'native-ready' -or $Case.StartsWith('ready-collision-')) {
        . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
        [void]$events.waiting.Set()
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttachReadyFromEnvironment('app-reports-run','app-reports-scenario')
        if(($null -ne $lease) -ne ($Case -ceq 'native-ready')) {throw 'Wrong delivered readiness result'}
        if($Case.StartsWith('ready-collision-') -and ($env:NLINK_APP_FRONTIER_START_V1 -or $env:NLINK_APP_FRONTIER_REQUEST -or $env:NLINK_APP_FRONTIER_REQUEST_ID)) {throw 'Failed readiness left child locators'}
        [void]$events.attached.Set()
    }
    if(-not $events.emit.WaitOne(20000)) {throw 'App emit deadline'}
    if($Name -and -not $Case.StartsWith('ready-collision-') -and $Case -cnotin @('native-ready','collision-stdout','collision-stderr','bind-failure','budget-before','owner-disposed','setup-failure')) {
        . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,'app-reports-run','app-reports-scenario')
        if($null -eq $lease) {throw 'App original attachment missing'}
    }
    $bytes=[byte[]]@()
    if($Case -cne 'empty') {
        $text="ordinary-one`r`n`r`ncarriage`rline`n"+[char]0x03b1+[char]0x4e2d+"`nlast-tail"
        if($Case -cin @('large','setup-failure','ordinary-write-failure')) {$text=('x'*70003)+"`n"+('y'*70003)+"`nlast-tail"}
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes($text)
        if($Case -ceq 'unicode-framing') {$bytes=[byte[]](@(0xef,0xbb,0xbf)+$bytes+@(0xff,0xfe))}
        foreach($stream in @([Console]::OpenStandardOutput(),[Console]::OpenStandardError())) {$stream.Write($bytes,0,$bytes.Length); $stream.Flush()}
    }
    [IO.File]::WriteAllBytes((Join-Path $Directory 'expected-raw.bin'),$bytes)
    if($null -ne $lease) {
        $completed=$lease.CompleteAttachment()
        if($Case -cne 'cancel-after' -and -not $completed) {throw 'App attachment completion failed'}
    }
    if($Case -cin @('early-eof','held-pipe')) {
        Add-Type 'using System; using System.Runtime.InteropServices; public static class AppReportPipe { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind); [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle); [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags); }'
        if($Case -ceq 'early-eof') {
            foreach($kind in @(-11,-12)) {if(-not [AppReportPipe]::CloseHandle([AppReportPipe]::GetStdHandle($kind))) {throw 'Early EOF fixture failed'}}
        } else {
            foreach($kind in @(-11,-12)) {if(-not [AppReportPipe]::SetHandleInformation([AppReportPipe]::GetStdHandle($kind),1,$(if($kind -eq -11){1}else{0}))) {throw 'Pipe inheritance failed'}}
            $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path); $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $args=@('-NoProfile','-File',$PSCommandPath,'-HoldPipe','-Signal',$Signal,'-Directory',$Directory)
            $start.Arguments=($args | ForEach-Object {'"'+$_+'"'}) -join ' '
            $holder=[Diagnostics.Process]::Start($start)
            try {if(-not $events.held.WaitOne(10000)) {throw 'Pipe holder startup failed'}} finally {$holder.Dispose()}
        }
    }
    [void]$events.emitted.Set()
    if(-not $events.exit.WaitOne(20000)) {throw 'App exit deadline'}
    if($Case -ceq 'nonzero') {exit 7}
} finally {if($null -ne $lease) {$lease.Dispose()}; foreach($event in $events.Values) {$event.Dispose()}}
