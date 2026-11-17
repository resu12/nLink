$factCase=$filesCase.Substring('facts-'.Length)
$factEvents=@{}
foreach($part in @('facts-start','facts-offered','facts-reviewed')) {$factEvents[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part)}
$factsRejected=$factCase -cin @('gui-budget','parent-budget','repeat-publish','repeat-receive','header','binding','length','digest','body','tail','state','post-body','state-regression','native-pin','dispose-facts','cancel')
try {
    if(-not $factEvents['facts-start'].WaitOne(20000)) {throw 'Parent facts start deadline'}
    if(-not $submission.FilesPulled) {throw 'Fact publication preceded native pull'}
    if($factCase -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($copy.TryPublishParentFacts()) {throw 'Copied binding published facts'}
        $copy.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($factCase -ceq 'mutable-result') {$result.Writers=@(); $result.SourceLineVisitCount=99999; $result.RetainedSha256='foreign'; $result.DeferredBatches=@()}
    if($factCase -ceq 'gui-budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
    }
    $before=$work.ElapsedTicks; $published=$binding.TryPublishParentFacts()
    if(-not $published -and $factCase -cin @('valid','markers')) {
        # Diagnostic codec reproduction only; no capability or original lifetime
        # can be recovered after the failed publication.
        $codec=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentFacts')
        $fileChannel=$submission.GetType().GetField('Channel',$flags).GetValue($submission)
        $body=$fileChannel.GetType().GetProperty('Body',$flags).GetValue($fileChannel,$null)
        $encoded=$codec.GetMethod('Encode',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@($preparation,$proof,$body,[Action]{}))
        $parsed=$codec.GetMethod('Parse',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@($encoded,$body,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    }
    if($factCase -ceq 'gui-budget') {if($published -or $binding.IsCurrent) {throw 'Expired GUI published facts'}}
    else {
        if(-not $published -or -not $submission.FactsPublished -or $submission.FactsReceived -or $work.IsRunning -or $work.ElapsedTicks -le $before) {throw "Original fact publication missing/work (published=$published, elapsed=$($work.ElapsedMilliseconds), before=$before)"}
        if($factCase -ceq 'repeat-publish') {if($binding.TryPublishParentFacts() -or $submission.IsCurrent) {throw 'Repeated facts retained original'}}
        if($factCase -ceq 'cancel') {$adopter.Dispose()}
    }
    [void]$factEvents['facts-offered'].Set()
    if(-not $factEvents['facts-reviewed'].WaitOne(20000)) {throw 'Parent facts receipt deadline'}
    if($factsRejected) {if($submission.IsCurrent) {throw 'Rejected facts retained original GUI'}}
    elseif(-not $submission.IsCurrent -or -not $submission.FactsReceived -or -not $preparation.IsCurrent) {throw 'Fact receipt released original GUI'}
    if($factCase.StartsWith('ack-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentAcknowledgment.ps1')
    }
} finally {foreach($event in $factEvents.Values) {$event.Dispose()}}
