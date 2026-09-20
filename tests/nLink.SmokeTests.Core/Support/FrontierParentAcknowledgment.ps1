$ackCase=$factCase.Substring('ack-'.Length)
$ack=$null; $ackMap=$null; $ackView=$null
$ackEarly=$ackCase -cin @('parent-budget','repeat-ack','missing-ack')
$ackRejected=$ackEarly -or $ackCase -cin @('gui-budget','repeat-confirm','header','offer','facts','reserved','state','post-body','state-regression','pin','dispose-ack','request-dispose','cancel')
try {
    if(-not $events['ack-start'].WaitOne(20000)) {throw 'GUI acknowledgment start deadline'}
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierParentAcknowledgment]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
        $facts.GetType().GetMethod('TryAcknowledge').GetParameters().Length) {throw 'Acknowledgment scalar import exposed'}
    if($ackCase -ceq 'copied-facts') {
        $copy=$parentClone.Invoke($facts,$null)
        if($null -ne $copy.TryAcknowledge()) {throw 'Copied facts acknowledged'}
        $copy.Dispose(); if(-not $facts.IsCurrent) {throw 'Copied facts retired original'}
    }
    $clock=$parentRegistration.GetType().GetField('parentWork',$parentFlags).GetValue($parentRegistration)
    if($ackCase -ceq 'parent-budget') {
        $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($ackCase -cne 'missing-ack') {
        $before=$clock.ElapsedTicks; $ack=$facts.TryAcknowledge()
        if($ackCase -ceq 'parent-budget') {if($null -ne $ack -or $facts.IsCurrent) {throw 'Expired parent acknowledged facts'}}
        else {
            if($null -eq $ack -or -not $ack.IsCurrent -or $ack.GuiConfirmed -or $clock.IsRunning -or $clock.ElapsedTicks -le $before -or
                $ack.LiveProofEligible -or $ack.NativeWriterCustodyEstablished -or $ack.PublicationCompletionIssued) {throw 'Original acknowledgment missing/work/authority'}
            if($ackCase -ceq 'copied-ack') {
                $copy=$parentClone.Invoke($ack,$null)
                if($copy.IsCurrent -or $copy.GuiConfirmed) {throw 'Copied acknowledgment current'}
                $copy.Dispose(); if(-not $ack.IsCurrent) {throw 'Copied acknowledgment retired original'}
            }
            if($ackCase -ceq 'repeat-ack') {if($null -ne $facts.TryAcknowledge() -or $ack.IsCurrent) {throw 'Repeated acknowledgment retained original'}}
        }
    }
    if(-not $ackEarly) {
        $ackMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.ack-v1')
        $ackView=$ackMap.CreateViewAccessor(0L,80L)
        if($ackCase -ceq 'foreign-open') {
            $type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentAcknowledgmentChannel')
            $foreign=$type.GetMethod('Open',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@(($name+'.frontier-parent-v1.files-v1'),[byte[]]::new(32),[byte[]]::new(32)))
            if($null -ne $foreign -or -not $ack.IsCurrent) {throw 'Foreign acknowledgment open affected original'}
        }
        switch($ackCase) {
            'header' {$ackView.Write(4L,2)}
            'offer' {$ackView.Write(16L,[byte]($ackView.ReadByte(16L) -bxor 1))}
            'facts' {$ackView.Write(48L,[byte]($ackView.ReadByte(48L) -bxor 1))}
            'reserved' {$ackView.Write(12L,1)}
            'state' {$ackView.Write(8L,2)}
        }
    }
    [void]$events['ack-offered'].Set()
    if(-not $events['ack-confirmed'].WaitOne(20000)) {throw 'GUI acknowledgment confirmation deadline'}
    $confirmed=$ackCase.StartsWith('commit-',[StringComparison]::Ordinal) -or $ackCase -cin @('valid','copied-facts','copied-ack','copied-binding','foreign-open','post-body','state-regression','pin','dispose-ack','request-dispose','cancel','legacy-complete','legacy-invalid','gui-exit','gui-failure')
    if($confirmed) {
        if($null -eq $ack -or -not $ack.GuiConfirmed -or -not $facts.IsCurrent -or -not $pending.IsCurrent) {throw 'Two-sided acknowledgment not preserved'}
        switch($ackCase) {
            'post-body' {$factView.Write(80L,[byte]($factView.ReadByte(80L) -bxor 1))}
            'state-regression' {$ackView.Write(8L,1)}
            'pin' {$pulledHandles[0].Dispose()}
            'dispose-ack' {$ack.Dispose()}
            'request-dispose' {$request.Dispose()}
        }
    }
    if($ackRejected -and $ackCase -cne 'cancel' -and $null -ne $ack -and $ack.IsCurrent) {throw 'Rejected acknowledgment retained original'}
    [void]$events['ack-reviewed'].Set()
    if($ackCase.StartsWith('commit-',[StringComparison]::Ordinal)) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentCommit.ps1')}
} finally {
    [void]$events['ack-offered'].Set(); [void]$events['ack-reviewed'].Set()
    if($null -ne $ackView) {$ackView.Dispose()}
    if($null -ne $ackMap) {$ackMap.Dispose()}
}
