$commitCase=$ackCase.Substring('commit-'.Length)
$custody=$null; $custodyPins=@(); $commitMap=$null; $commitView=$null; $failureGate=$null; $gateHeld=$false
$commitLegacyExpected=$commitCase -cnotin @('repeat-prepare','parent-budget','gui-budget','missing-prepare','cancel-before','failed-completion','gui-failure','missing-commit','commit-binding','commit-state','request-dispose','output-gate','state-gate')
$commitShouldAccept=$commitLegacyExpected -and $commitCase -cnotin @('post-pin','post-body','legacy-body','legacy-scope','dispose-custody','request-signal','joined-mismatch','post-length','post-digest','post-tail','late-parent-budget')
try {
    if(-not $events['commit-start'].WaitOne(20000)) {throw 'GUI commit start deadline'}
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierParentCustody]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
        $ack.GetType().GetMethod('TryPrepareCommit').GetParameters().Length) {throw 'Custody scalar import exposed'}
    if($commitCase -ceq 'copied-ack') {
        $foreign=$parentClone.Invoke($ack,$null)
        if($null -ne $foreign.TryPrepareCommit()) {throw 'Copied acknowledgment prepared commit'}
        $foreign.Dispose(); if(-not $ack.IsCurrent) {throw 'Copied acknowledgment retired original'}
    }
    if($commitCase -ceq 'parent-budget') {
        $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($commitCase -cne 'missing-prepare') {
        $before=$clock.ElapsedTicks; $custody=$ack.TryPrepareCommit()
        if($commitCase -ceq 'parent-budget') {if($null -ne $custody -or $ack.IsCurrent) {throw 'Expired parent prepared commit'}}
        else {
            if($null -eq $custody -or $custody.IsCurrent -or $custody.FileCount -or $custody.SourceLineCount -or
                $custody.LiveProofEligible -or $custody.NativeWriterCustodyEstablished -or $custody.PublicationCompletionIssued -or
                $clock.IsRunning -or $clock.ElapsedTicks -le $before) {throw 'Escrow exposed early custody/missing work'}
            $pins=$custody.GetType().GetField('pins',$parentFlags).GetValue($custody)
            $custodyPins=@($pins.GetType().GetField('Handles',$parentFlags).GetValue($pins))
            if($custodyPins.Count -ne $pulledHandles.Count) {throw 'Incomplete escrow native set'}
            foreach($pin in $custodyPins) {if(-not [ParentPinProbe]::WriteDenied($pin)) {throw 'Escrow retained write access'}}
            if($commitCase -ceq 'copied-custody') {
                $foreign=$parentClone.Invoke($custody,$null)
                if($foreign.IsCurrent) {throw 'Copied custody current'}
                $foreign.Dispose(); if(@($custodyPins | Where-Object {$_.IsClosed}).Count) {throw 'Copied custody retired original'}
            }
            if($commitCase -ceq 'repeat-prepare') {if($null -ne $ack.TryPrepareCommit()) {throw 'Commit preparation repeated'}}
            else {
                $commitMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.commit-v1')
                $commitView=$commitMap.CreateViewAccessor(0L,65616L)
                if($commitCase -ceq 'commit-binding') {$commitView.Write(16L,[byte]($commitView.ReadByte(16L) -bxor 1))}
                if($commitCase -ceq 'commit-state') {$commitView.Write(8L,3)}
            }
        }
    }
    if($commitCase -ceq 'failed-completion') {$failureGate=[Threading.Mutex]::OpenExisting($name+'.gate'); $gateHeld=$failureGate.WaitOne(0); if(-not $gateHeld) {throw 'Completion gate fixture missing'}}
    [void]$events['commit-ready'].Set()
    if(-not $events['commit-published'].WaitOne(20000)) {
        if($process.HasExited) {throw "GUI commit failed: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"}
        throw 'GUI commit publication deadline'
    }
    if($null -ne $custody -and ($custody.IsCurrent -or $custody.FileCount)) {throw 'Custody activated before original exit/take'}
    switch($commitCase) {
        'post-pin' {$custodyPins[0].Dispose()}
        'post-body' {$commitView.Write(80L,[byte]($commitView.ReadByte(80L) -bxor 1))}
        'post-length' {$commitView.Write(12L,[int]::MaxValue)}
        'post-digest' {$commitView.Write(48L,[byte]($commitView.ReadByte(48L) -bxor 1))}
        'post-tail' {$commitView.Write(65615L,[byte]1)}
        'late-parent-budget' {
            $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
        'legacy-body' {$view.Write(16449L,[byte]88)}
        'legacy-scope' {$view.Write(64L,[byte]($view.ReadByte(64L) -bxor 1))}
        'request-signal' {
            $signal=[Threading.EventWaitHandle]::OpenExisting($name+'.closed')
            try {[void]$signal.Set()} finally {$signal.Dispose()}
        }
        'joined-mismatch' {
            # Coherently mutate both summaries before GUI exit. The unchanged
            # private fact packet must independently reject this forged join.
            $view.Write(16449L,[byte]88); $commitView.Write(81L,[byte]88)
            $body=[byte[]]::new($commitView.ReadInt32(12L)); [void]$commitView.ReadArray(80L,$body,0,$body.Length)
            $hash=[Security.Cryptography.SHA256]::Create()
            try {$digest=$hash.ComputeHash($body)} finally {$hash.Dispose()}
            for($i=0;$i -lt 32;$i++) {$commitView.Write([long](48+$i),$digest[$i])}
        }
        'request-dispose' {$request.Dispose()}
        'dispose-custody' {$custody.Dispose()}
    }
    [void]$events['commit-reviewed'].Set()
} finally {
    if($gateHeld) {$failureGate.ReleaseMutex()}; if($null -ne $failureGate) {$failureGate.Dispose()}
    [void]$events['commit-ready'].Set(); [void]$events['commit-reviewed'].Set()
}
