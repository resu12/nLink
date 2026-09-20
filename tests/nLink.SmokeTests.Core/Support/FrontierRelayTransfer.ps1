param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay([guid]::NewGuid(),'cleanup-adoption-run','cleanup-adoption-scope',$Directory)
$go=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.fixture-go')
$process=[Diagnostics.Process]::new(); $custody=$null; $exported=$null
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.Arguments=(@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayTransferWorker.ps1'),'-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case,'-Name',$request.Name,'-Id',$request.RequestId) | ForEach-Object {'"'+$_+'"'}) -join ' '
    $process.StartInfo=$start
    if(-not $process.Start() -or -not $request.BindWorker($process)) {throw 'Relay fixture failed to bind actual Wrapper'}
    $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
    $registration=$request.TryRegisterFrontierRelay(); if($null -eq $registration) {throw 'Relay registration missing'}
    if($Case -ceq 'pull-budget') {
        $work=$registration.GetType().GetField('transferWork',$flags).GetValue($registration)
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    $mutated=$false
    [void]$go.Set(); $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not $process.WaitForExit(25)) {
        if($watch.ElapsedMilliseconds -gt 100000) {throw 'Relay fixture deadline'}
        if($null -eq $custody) {
            if($Case -ceq 'transfer-tamper' -and -not $mutated) {
                $ready=$null
                try {$ready=[Threading.EventWaitHandle]::OpenExisting($request.Name+'.frontier-relay-v1.transfer-v1.ready')} catch {}
                if($null -ne $ready) {
                    try {
                        if($ready.WaitOne(0)) {
                            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name+'.frontier-relay-v1.transfer-v1')
                            $view=$map.CreateViewAccessor()
                            try {$view.Write(24L,[byte]($view.ReadByte(24L) -bxor 1)); $mutated=$true}
                            finally {$view.Dispose(); $map.Dispose()}
                        }
                    } finally {$ready.Dispose()}
                }
                if(-not $mutated) {continue}
            }
            $custody=$registration.TryPullIfOffered()
            if($null -ne $custody) {
                if($custody.IsCurrent -or $custody.LiveProofEligible -or $custody.NativeWriterCustodyEstablished -or $custody.PublicationCompletionIssued) {throw 'Provisional relay offer granted authority'}
                if($null -ne $registration.TryPullIfOffered()) {throw 'Repeated pull issued custody'}
                if($Case -ceq 'registration-dispose') {$registration.Dispose()}
            }
        }
    }
    $stdout=$out.GetAwaiter().GetResult(); $stderr=$err.GetAwaiter().GetResult()
    if($stdout -notmatch 'relay-transfer-worker-ok' -or $process.ExitCode -ne $(if($Case -ceq 'wrapper-nonzero'){7}else{0})) {throw "Relay worker failed: $stdout $stderr"}
    if($Case -cin @('map-collision','event-collision','transfer-tamper','pull-budget')) {
        $completion=$request.TakeRelayAfterExit()
        if($null -ne $custody -or $null -eq $completion -or $null -ne $completion.TryTakeFrontierCustody()) {throw 'Rejected native offer changed legacy result or granted custody'}
        'frontier-relay-transfer='+$Case; return
    }
    if($null -eq $custody) {throw 'No native pins were pulled'}
    $pins=$custody.GetType().GetField('pins',$flags).GetValue($custody)
    $handles=$pins.GetType().GetField('Handles',$flags).GetValue($pins)
    if($Case -ceq 'body-mismatch') {
        $view=$request.GetType().GetField('view',$flags).GetValue($request)
        $position=64L+16384L
        $view.Write($position,[byte]($view.ReadByte($position) -bxor 1))
    }
    if($Case -ceq 'request-dispose') {$request.Dispose()}
    $completion=if($Case -cne 'no-take') {$request.TakeRelayAfterExit()} else {$null}
    if($null -ne $completion) {
        if($Case -ceq 'guards') {
            $copy=$completion.GetType().GetMethod('MemberwiseClone',$flags).Invoke($completion,@())
            if($null -ne $copy.TryTakeFrontierCustody()) {throw 'Copied completion detached native custody'}
            $copyCustody=$custody.GetType().GetMethod('MemberwiseClone',$flags).Invoke($custody,@())
            $copyCustody.Dispose(); if($copyCustody.IsCurrent) {throw 'Copied custody remained usable'}
        }
        $exported=$completion.TryTakeFrontierCustody()
        if($null -ne $completion.TryTakeFrontierCustody()) {throw 'Repeated detach issued custody'}
    }
    $expected=$Case -cin @('valid','guards','analysis','analysis-changed')
    if(($null -ne $exported) -ne $expected) {throw "Relay terminal adoption mismatch: $Case"}
    $request.Dispose()
    if($expected) {
        if($Case -ceq 'guards') {
            foreach($callback in @('TransferCancelled','WrapperExited')) {
                $exported.GetType().GetMethod($callback,$flags).Invoke($exported,@($null,$false))
            }
        }
        if(-not $exported.IsCurrent -or $exported.FileCount -lt 5) {throw 'Detached pins did not survive request disposal'}
        if($exported.LiveProofEligible -or $exported.NativeWriterCustodyEstablished -or $exported.PublicationCompletionIssued) {throw 'Relay transfer granted evaluation authority'}
        if($Case.StartsWith('analysis')) {
            $writer=$completion.WriterContext
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1')
        }
        $exported.Dispose()
    }
    if(@($handles | Where-Object {-not $_.IsClosed}).Count) {throw 'Relay file pins leaked'}
    'frontier-relay-transfer='+$Case
} finally {
    if($null -ne $exported) {$exported.Dispose()}; if($null -ne $custody) {$custody.Dispose()}
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] {}
    $process.Dispose(); $request.Dispose(); $go.Dispose()
}
