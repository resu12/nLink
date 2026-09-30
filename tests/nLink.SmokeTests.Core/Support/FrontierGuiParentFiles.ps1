$filesCase=$mode.Substring('files-'.Length)
$fileEvents=@{}
foreach($part in @('files-offered','files-reviewed','files-retired','files-finish')) {
    $fileEvents[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part)
}
$preparation=$null; $submission=$null
try {
    if($filesCase -cne 'no-preparation') {$preparation=$binding.TryPrepareParentFiles(); if($null -eq $preparation) {throw 'File offer preparation missing'}}
    if($filesCase -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($null -ne $copy.TryPublishParentFiles()) {throw 'Copied binding published files'}
        $copy.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($filesCase -ceq 'closed-pin') {
        $pins=$preparation.GetType().GetField('Files',$flags).GetValue($preparation)
        $pins.GetType().GetField('entries',$flags).GetValue($pins)[0].GetType().GetField('Handle',$flags).GetValue($pins.GetType().GetField('entries',$flags).GetValue($pins)[0]).Dispose()
    }
    if($filesCase -ceq 'budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
    }
    $before=$work.ElapsedTicks; $submission=$binding.TryPublishParentFiles()
    if($filesCase -cin @('no-preparation','closed-pin','budget')) {
        if($null -ne $submission -or $binding.IsCurrent) {throw 'Invalid file offer accepted'}
    } else {
        if($null -eq $submission -or -not $submission.IsCurrent -or $submission.FilesPulled -or $work.IsRunning -or $work.ElapsedTicks -le $before -or
            $submission.LiveProofEligible -or $submission.NativeWriterCustodyEstablished -or $submission.PublicationCompletionIssued) {throw 'Original file offer missing/authority'}
        if($filesCase -ceq 'copied-submission') {
            $copy=$clone.Invoke($submission,$null)
            if($copy.IsCurrent -or $copy.FilesPulled) {throw 'Copied submission current'}
            $copy.Dispose(); if(-not $submission.IsCurrent) {throw 'Copied submission retired original'}
        }
        if($filesCase -ceq 'repeat-publish') {if($null -ne $binding.TryPublishParentFiles() -or $submission.IsCurrent) {throw 'Repeated publication retained original'}}
    }
    [void]$fileEvents['files-offered'].Set()
    if($filesCase.StartsWith('facts-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentFacts.ps1')
        if($filesCase.StartsWith('facts-ack-commit-',[StringComparison]::Ordinal)) {return}
        if($filesCase -cin @('facts-legacy-complete','facts-gui-exit')) {$filesCase=$filesCase.Substring('facts-'.Length)}
        if($filesCase -cin @('facts-ack-legacy-complete','facts-ack-legacy-invalid','facts-ack-gui-exit','facts-ack-gui-failure')) {$filesCase=$filesCase.Substring('facts-ack-'.Length)}
    }
    if(-not $fileEvents['files-reviewed'].WaitOne(20000)) {throw 'Parent file pull review deadline'}
    $rejected=$filesCase -cin @('no-preparation','closed-pin','budget','repeat-publish','repeat-pull','header','binding','nonce','digest','reserved','length','body','tail','state','state-regression',
        'descriptor-handle','descriptor-identity','descriptor-length','descriptor-role','descriptor-count','descriptor-slot','pending-pin','parent-dispose','request-dispose','post-body','post-scope')
    if($filesCase.StartsWith('facts-',[StringComparison]::Ordinal)) {$rejected=$factsRejected}
    if($rejected) {
        if($null -ne $submission -and $submission.IsCurrent) {throw 'Rejected parent pull retained GUI offer'}
    } else {
        if(-not $submission.IsCurrent -or -not $submission.FilesPulled -or -not $preparation.IsCurrent -or -not $evidence.IsCurrent) {throw 'Native pull did not preserve GUI facts/pins'}
        if($filesCase -ceq 'gui-exit') {'frontier-gui-worker=native-gui-exit-with-parent-pins'; [Environment]::Exit(0)}
        if($filesCase -ceq 'gui-failure') {'frontier-gui-worker=native-gui-failure-with-parent-pins'; [Environment]::Exit(7)}
        if($filesCase -cin @('legacy-complete','legacy-invalid')) {
            $legacyId=if($filesCase -ceq 'legacy-invalid') {''} else {'binding-legacy'}
            $completed=$finalizer.Complete($legacyId,[IO.Path]::Combine($Directory,'legacy-only.log'),3,0,
                [string[]]@(('a'*64),('b'*64),('c'*64)),[string[]]@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680'),
                [long[]]@(12,12,4),[long[]]@(3,3,1),[string[]]@('writer'),[long[]]@(3))
            if($completed -ne ($filesCase -ceq 'legacy-complete')) {throw 'Parent pull changed legacy completion'}
        } elseif($filesCase -ceq 'cancel') {$adopter.Dispose()}
        else {$submission.Dispose()}
        if($submission.IsCurrent -or $preparation.IsCurrent -or $binding.IsCurrent) {throw 'GUI retirement retained offer'}
    }
    [void]$fileEvents['files-retired'].Set()
    if(-not $fileEvents['files-finish'].WaitOne(20000)) {throw 'Parent cleanup deadline'}
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Parent native pins leaked payloads'}
    [IO.File]::AppendAllText($source,'parent-files-released')
} finally {
    if($null -ne $submission) {$submission.Dispose()}
    foreach($event in $fileEvents.Values) {$event.Dispose()}
}
