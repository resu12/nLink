param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$Name,[guid]$RequestId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$events=@{}; $relay=$null; $handoff=$null
try {
    foreach($part in @('legacy-go','legacy-attached','native-go','native-attached','review')) {$events[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part)}
    if(-not $events['legacy-go'].WaitOne(15000)) {throw 'Legacy startup not released'}
    $root=Join-Path (Join-Path $Directory 'gui') 'valid'
    $relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay($Name,$RequestId,'gui-writer-parent-run','gui-writer-parent-run:scenario:1',$root)
    if($null -eq $relay) {throw 'Original Wrapper relay unavailable'}
    [void]$events['legacy-attached'].Set()
    if(-not $events['native-go'].WaitOne(15000)) {throw 'Native startup not released'}
    if($Case -ceq 'guards') {
        $copy=$clone.Invoke($relay,$null)
        if($null -ne $copy.TryAttachFrontier() -or $copy.Forward($null)) {throw 'Copied relay consumed original'}
        $copy.Dispose()
    }
    $handoff=$relay.TryAttachFrontier()
    $admitted=$Case -cnotin @('missing','map-collision','event-collision','request-id','scope-body','wrapper-incarnation','parent-incarnation','version','padding','nonce','channel-state')
    if(($null -ne $handoff) -ne $admitted) {throw 'Native relay attachment mismatch'}
    if($null -ne $handoff) {
        if(-not $handoff.IsCurrent -or $handoff.NativeWriterCustodyEstablished -or $handoff.LiveProofEligible -or $handoff.PublicationCompletionIssued) {throw 'Native registration acquired authority'}
        if($Case -ceq 'guards') {
            $copy=$clone.Invoke($handoff,$null)
            if($copy.IsCurrent) {throw 'Copied handoff current'}
            $copy.Dispose()
            if($null -ne $relay.TryAttachFrontier() -or -not $handoff.IsCurrent) {throw 'Replay retired original handoff'}
        }
    }
    if($Case -ceq 'relay-dispose') {$relay.Dispose()}
    if($Case -ceq 'handoff-dispose') {$handoff.Dispose()}
    [void]$events['native-attached'].Set()
    if($Case -ceq 'peer-exit') {[Environment]::Exit(0)}
    if(-not $events['review'].WaitOne(15000)) {throw 'Native peer review not released'}
    $current=$admitted -and $Case -cnotin @('request-dispose','registration-dispose','legacy-after-dispose','relay-dispose','handoff-dispose','early-take','channel-regression','legacy-regression')
    if($null -ne $handoff -and $handoff.IsCurrent -ne $current) {throw 'Peer cancellation/regression not observed'}
    if($Case -cin @('legacy-forward','legacy-after-dispose')) {
        $completion=$null
        $null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot `
            -Directory (Join-Path $Directory 'gui') -SingleCase valid -AcceptedCompletion ([ref]$completion) -ForwardRelay $relay
        if($null -eq $completion -or $handoff.IsCurrent) {throw 'Legacy forward did not end pre-forward native scope'}
    }
    [Console]::Out.WriteLine('native-relay-wrapper-ok'); [Console]::Error.WriteLine('native-relay-wrapper-stderr')
} finally {
    if($null -ne $handoff) {$handoff.Dispose()}; if($null -ne $relay) {$relay.Dispose()}
    foreach($event in $events.Values) {$event.Dispose()}
}
