param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$Name,[guid]$Id)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$go=[Threading.EventWaitHandle]::OpenExisting($Name+'.fixture-go')
if(-not $go.WaitOne(20000)) {throw 'Relay registration fixture timeout'}
$go.Dispose()
$relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay($Name,$Id,'cleanup-adoption-run','cleanup-adoption-scope',$Directory)
if($null -eq $relay) {throw 'Original relay unavailable'}
$handoff=$relay.TryAttachFrontier(); if($null -eq $handoff) {throw 'Original native relay unavailable'}
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'cleanup-adoption-run','cleanup-adoption-scope',$Directory)
$process=[Diagnostics.Process]::new(); $custody=$null; $foreign=$null; $foreignView=$null
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $FinalizerCase='persistence-handoff-positive'
    $start.Arguments=(@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCleanupAdoptionGui.ps1'),'-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case','positive','-FinalizerCase',$FinalizerCase) | ForEach-Object {'"'+$_+'"'}) -join ' '
    $process.StartInfo=$start; $reports=$request.StartFrontierGuiWorkerWithReports($process)
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFinalizerHandoff.ps1') -Phase wait
    $stdout=$reports.Stdout.GetAwaiter().GetResult(); $stderr=$reports.Stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $stdout -notmatch 'cleanup-adoption-gui-ok') {throw "Actual native GUI fixture failed: $stdout $stderr"}
    $completion=$request.TakeAfterExit()
    if($null -eq $completion -or $null -eq $custody -or -not $custody.IsCurrent) {throw 'Actual GUI custody unavailable'}
    if($Case -ceq 'guards') {
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $copy=$custody.GetType().GetMethod('MemberwiseClone',$flags).Invoke($custody,@())
        if($handoff.TryOfferAndWait($copy,$completion) -or -not $custody.IsCurrent) {throw 'Copied custody consumed the original offer'}
    }
    if($Case -ceq 'map-collision') {
        $foreign=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($Name+'.frontier-relay-v1.transfer-v1',128)
        $foreignView=$foreign.CreateViewAccessor(); $foreignView.Write(0L,12345)
    }
    if($Case -ceq 'event-collision') {$foreign=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$Name+'.frontier-relay-v1.transfer-v1.cancelled')}
    $offered=$handoff.TryOfferAndWait($custody,$completion)
    if(-not $handoff.GetType().GetField('offerAttempted',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($handoff)) {throw 'Eligible failed offer remained retryable'}
    if(-not $offered -and $Case -cnotin @('registration-dispose','map-collision','event-collision','transfer-tamper','pull-budget')) {throw 'Native relay offer/pull failed'}
    if($Case -ceq 'map-collision' -and $foreignView.ReadInt32(0L) -ne 12345) {throw 'Foreign transfer mapping changed'}
    if($Case -ceq 'event-collision' -and $foreign.WaitOne(0)) {throw 'Foreign transfer cancellation signalled'}
    if($custody.IsCurrent -or $handoff.TryOfferAndWait($custody,$completion)) {throw 'Relay offer reused raw GUI custody'}
    $request.Dispose()
    if($Case -ceq 'handoff-cancel') {$handoff.Dispose()}
    if(-not $relay.Forward($completion)) {throw 'Original legacy Forward failed'}
    'relay-transfer-worker-ok'
} finally {
    if($null -ne $foreignView) {$foreignView.Dispose()}; if($null -ne $foreign) {$foreign.Dispose()}
    if($null -ne $custody) {$custody.Dispose()}; $request.Dispose(); $handoff.Dispose(); $relay.Dispose()
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] {}
    $process.Dispose()
}
if($Case -ceq 'wrapper-nonzero') {exit 7}
