$ackCase=$factCase.Substring('ack-'.Length)
$ackEvents=@{}
foreach($part in @('ack-start','ack-offered','ack-confirmed','ack-reviewed')) {$ackEvents[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part)}
$factsRejected=$ackCase -cin @('parent-budget','gui-budget','repeat-ack','repeat-confirm','missing-ack','header','offer','facts','reserved','state','post-body','state-regression','pin','dispose-ack','request-dispose','cancel')
try {
    [void]$ackEvents['ack-start'].Set()
    if(-not $ackEvents['ack-offered'].WaitOne(20000)) {throw 'Parent acknowledgment deadline'}
    if($binding.GetType().GetMethod('TryConfirmParentAcknowledgment').GetParameters().Length) {throw 'GUI acknowledgment scalar import exposed'}
    if($ackCase -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($copy.TryConfirmParentAcknowledgment()) {throw 'Copied binding confirmed acknowledgment'}
        $copy.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($ackCase -ceq 'gui-budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
    }
    $before=$work.ElapsedTicks; $confirmed=$binding.TryConfirmParentAcknowledgment()
    $rejectConfirmation=$ackCase -cin @('parent-budget','gui-budget','repeat-ack','missing-ack','header','offer','facts','reserved','state')
    if($rejectConfirmation) {if($confirmed -or $submission.IsCurrent) {throw 'Invalid acknowledgment confirmed'}}
    else {
        if(-not $confirmed -or -not $submission.ParentAcknowledgmentConfirmed -or -not $preparation.IsCurrent -or
            $work.IsRunning -or $work.ElapsedTicks -le $before) {throw 'Original GUI confirmation missing/work'}
        if($ackCase -ceq 'repeat-confirm') {if($binding.TryConfirmParentAcknowledgment() -or $submission.IsCurrent) {throw 'Repeated confirmation retained original'}}
    }
    [void]$ackEvents['ack-confirmed'].Set()
    if(-not $ackEvents['ack-reviewed'].WaitOne(20000)) {throw 'Parent acknowledgment review deadline'}
    if($ackCase -ceq 'cancel') {$adopter.Dispose()}
    if($factsRejected) {if($submission.IsCurrent) {throw 'Rejected acknowledgment retained GUI resources'}}
    elseif(-not $submission.ParentAcknowledgmentConfirmed -or -not $preparation.IsCurrent) {throw 'Acknowledgment released original GUI resources'}
    if($ackCase.StartsWith('commit-',[StringComparison]::Ordinal)) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentCommit.ps1')}
} finally {
    [void]$ackEvents['ack-confirmed'].Set()
    foreach($event in $ackEvents.Values) {$event.Dispose()}
}
