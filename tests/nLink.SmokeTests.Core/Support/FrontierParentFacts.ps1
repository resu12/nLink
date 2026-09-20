$factCase=$filesCase.Substring('facts-'.Length)
$factMap=$null; $factView=$null; $facts=$null
try {
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierParentFacts]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
        $pending.GetType().GetMethod('TryReceiveFacts').GetParameters().Length) {throw 'Fact capability scalar import exposed'}
    [void]$events['facts-start'].Set()
    if(-not $events['facts-offered'].WaitOne(20000)) {
        if($process.HasExited) {throw "GUI facts offer failed: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"}
        throw 'GUI facts offer deadline'
    }
    $early=$factCase -cin @('gui-budget','repeat-publish','cancel')
    if(-not $early) {
        $factMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.facts-v1')
        $factView=$factMap.CreateViewAccessor(0L,4194384L)
        if($factCase -ceq 'copied-pending') {
            $copy=$parentClone.Invoke($pending,$null)
            if($null -ne $copy.TryReceiveFacts()) {throw 'Copied pending files received facts'}
            $copy.Dispose(); if(-not $pending.IsCurrent) {throw 'Copied pending files retired original'}
        }
        if($factCase -ceq 'parent-budget') {
            $clock=$parentRegistration.GetType().GetField('parentWork',$parentFlags).GetValue($parentRegistration)
            $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
        switch($factCase) {
            'header' {$factView.Write(4L,2)}
            'binding' {$factView.Write(16L,[byte]($factView.ReadByte(16L) -bxor 1))}
            'length' {$factView.Write(12L,[int]::MaxValue)}
            'digest' {$factView.Write(48L,[byte]($factView.ReadByte(48L) -bxor 1))}
            'body' {$factView.Write(80L,[byte]($factView.ReadByte(80L) -bxor 1))}
            'tail' {$factView.Write(4194383L,[byte]1)}
            'state' {$factView.Write(8L,2)}
        }
    }
    $facts=$pending.TryReceiveFacts()
    $rejected=$early -or $factCase -cin @('parent-budget','header','binding','length','digest','body','tail','state')
    if($rejected) {if($null -ne $facts -or $pending.IsCurrent) {throw 'Invalid facts accepted'}}
    else {
        if($null -eq $facts -or -not $facts.IsCurrent -or $facts.SourceLineCount -le 0 -or $facts.OriginCount -le 0 -or
            $facts.LiveProofEligible -or $facts.NativeWriterCustodyEstablished -or $facts.PublicationCompletionIssued) {throw 'Original full fact receipt missing/authority'}
        $expectedMarkers=if($factCase -ceq 'markers') {4} else {0}
        if($facts.RecoveryMarkerCount -ne $expectedMarkers) {throw 'Full recovery tuples not transferred'}
        if($factCase -cin @('valid','markers')) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentFactsCodec.ps1')
        }
        if($factCase -ceq 'mutable-result' -and $facts.SourceLineCount -eq 99999) {throw 'Mutable result imported as facts'}
        if($factCase -ceq 'copied-facts') {
            $copy=$parentClone.Invoke($facts,$null)
            if($copy.IsCurrent -or $copy.SourceLineCount) {throw 'Copied facts current'}
            $copy.Dispose(); if(-not $facts.IsCurrent) {throw 'Copied facts retired original'}
        }
        switch($factCase) {
            'repeat-receive' {if($null -ne $pending.TryReceiveFacts()) {throw 'Facts receipt repeated'}}
            'post-body' {$factView.Write(80L,[byte]($factView.ReadByte(80L) -bxor 1))}
            'state-regression' {$factView.Write(8L,1)}
            'native-pin' {$pulledHandles[0].Dispose()}
            'dispose-facts' {$facts.Dispose()}
        }
        if($factCase -cin @('repeat-receive','post-body','state-regression','native-pin','dispose-facts') -and $facts.IsCurrent) {throw 'Stale facts retained custody'}
    }
    [void]$events['facts-reviewed'].Set()
    if($factCase.StartsWith('ack-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentAcknowledgment.ps1')
    }
} finally {
    [void]$events['facts-reviewed'].Set()
    if($null -ne $factView) {$factView.Dispose()}
    if($null -ne $factMap) {$factMap.Dispose()}
}
