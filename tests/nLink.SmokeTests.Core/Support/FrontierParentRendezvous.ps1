param([string]$Phase)
$rendezvousCase=$case.Substring('cursor-parent-rendezvous-'.Length)
if($Phase -ceq 'register') {
    $custody=$null; $rendezvousSignals=@(); $collision=$null
    if($rendezvousCase -ceq 'event-collision') {
        $collision=[Threading.EventWaitHandle]::new($true,[Threading.EventResetMode]::ManualReset,$name+'.frontier-parent-v1.rendezvous-FactsOffered')
    }
    $parentRegistration=$request.TryRegisterFrontierParent()
    if($rendezvousCase -ceq 'event-collision') {
        if($null -ne $parentRegistration -or -not $collision.WaitOne(0)) {throw 'Foreign rendezvous event reused/reset'}
        $collision.Dispose(); $collision=$null
        return
    }
    if($null -eq $parentRegistration -or -not $parentRegistration.IsCurrent) {throw 'Rendezvous registration missing'}
    $channel=$parentRegistration.GetType().GetField('Channel',$parentFlags).GetValue($parentRegistration)
    $rendezvousSignals=$channel.GetType().GetField('rendezvousSignals',$parentFlags).GetValue($channel)
    $clock=$parentRegistration.GetType().GetField('parentWork',$parentFlags).GetValue($parentRegistration)
    for($i=0;$i -lt 20;$i++) {
        if($null -ne $parentRegistration.TryPrepareCommitIfOffered()) {throw 'Absent offer prepared commit'}
    }
    foreach($field in @('filesAttempted','factsAttempted','acknowledgmentAttempted','commitAttempted')) {
        if($parentRegistration.GetType().GetField($field,$parentFlags).GetValue($parentRegistration)) {throw 'Absent offer consumed original operation'}
    }
    if($clock.ElapsedTicks -or $clock.IsRunning -or -not $parentRegistration.IsCurrent) {throw 'Absent offer charged work or retired registration'}
    if($rendezvousCase -ceq 'copied') {
        $copy=$parentClone.Invoke($parentRegistration,$null)
        if($null -ne $copy.TryPrepareCommitIfOffered()) {throw 'Copied registration scheduled commit'}
        $copy.Dispose(); if(-not $parentRegistration.IsCurrent) {throw 'Copied probe retired original'}
    }
    switch($rendezvousCase) {
        'early-files' {
            [void]$rendezvousSignals[0].Set()
            if($null -ne $parentRegistration.TryPrepareCommitIfOffered() -or $parentRegistration.IsCurrent) {throw 'Early signal became file authority'}
        }
        'early-pulled' {[void]$rendezvousSignals[1].Set()}
        'parent-budget' {
            $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
    }
} elseif($Phase -ceq 'review') {
    $watch=[Diagnostics.Stopwatch]::StartNew(); $acted=$false; $manualPhase=0
    while(-not $process.WaitForExit(10)) {
        if($watch.ElapsedMilliseconds -gt 35000) {throw 'Rendezvous GUI watchdog deadline'}
        if($rendezvousCase -cin @('event-collision','early-files','early-pulled','missing-parent','deadline-before','repeat-waiting')) {continue}
        if($rendezvousCase -cin @('cancel-files','legacy-fallback')) {
            if(-not $acted -and $rendezvousSignals[0].WaitOne(0)) {$parentRegistration.Dispose(); $acted=$true}
            continue
        }
        if($rendezvousCase -ceq 'delayed-parent' -and -not $acted) {
            if(-not $rendezvousSignals[0].WaitOne(0)) {continue}
            if($clock.ElapsedTicks -or $clock.IsRunning) {throw 'Unscheduled parent work clock charged'}
            [void]$process.WaitForExit(700); $acted=$true
        }
        if($rendezvousCase -cin @('early-ack','early-commit','cancel-facts','cancel-commit','late-pull')) {
            if($manualPhase -eq 0 -and $rendezvousSignals[0].WaitOne(0)) {
                if($rendezvousCase -ceq 'late-pull') {[void]$process.WaitForExit(18000)}
                $pending=$parentRegistration.TryPullFiles()
                if($null -eq $pending) {throw 'Forged-signal fixture lost valid native pull'}
                [void]$rendezvousSignals[1].Set(); $manualPhase=1
            }
            if($manualPhase -eq 1 -and $rendezvousSignals[2].WaitOne(0)) {
                if($rendezvousCase -ceq 'cancel-facts') {$parentRegistration.Dispose(); $manualPhase=3; continue}
                if($rendezvousCase -ceq 'late-pull') {$manualPhase=3; continue}
                if($rendezvousCase -cin @('early-commit','cancel-commit')) {
                    $facts=$pending.TryReceiveFacts(); $ack=$facts.TryAcknowledge()
                    if($null -eq $ack) {throw 'Forged-commit fixture lost valid ACK'}
                }
                [void]$rendezvousSignals[3].Set(); $manualPhase=2
            }
            if($rendezvousCase -cin @('early-commit','cancel-commit') -and $manualPhase -eq 2 -and $rendezvousSignals[4].WaitOne(0)) {
                if($rendezvousCase -ceq 'cancel-commit') {$parentRegistration.Dispose()}
                else {[void]$rendezvousSignals[5].Set()}
                $manualPhase=3
            }
            continue
        }
        if($rendezvousCase -cin @('stale-files','signal-failure') -and -not $acted) {
            if(-not $rendezvousSignals[0].WaitOne(0)) {continue}
            if($rendezvousCase -ceq 'signal-failure') {$rendezvousSignals[1].Dispose()}
            else {
                $mutationMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1')
                $mutationView=$mutationMap.CreateViewAccessor()
                try {$mutationView.Write(16L,[byte]($mutationView.ReadByte(16L) -bxor 1))}
                finally {$mutationView.Dispose(); $mutationMap.Dispose()}
            }
            $acted=$true
        }
        $candidate=$parentRegistration.TryPrepareCommitIfOffered()
        if($null -ne $candidate) {
            if($null -ne $custody -or $candidate.IsCurrent -or $candidate.LiveProofEligible -or $candidate.NativeWriterCustodyEstablished -or $candidate.PublicationCompletionIssued) {throw 'Rendezvous replayed/promoted custody'}
            $custody=$candidate
        }
    }
    if($rendezvousCase -ceq 'late-pull' -and $manualPhase -ne 3) {throw 'Cumulative deadline fixture never reached the second wait'}
} elseif($Phase -ceq 'result') {
    $accepted=$rendezvousCase -cin @('valid','copied','delayed-parent')
    $legacy=$rendezvousCase -ceq 'legacy-fallback'
    if(($null -ne $completion) -ne ($accepted -or $legacy) -or ($null -ne $custody -and $custody.IsCurrent) -ne $accepted) {throw "Rendezvous completion/exit disagreement: $rendezvousCase"}
    if($legacy -and ($completion.LedgerId -cne 'binding-legacy' -or $completion.Outputs.Count -ne 4 -or
        $completion.SourcePath -cne (Join-Path $Directory 'legacy-only.log'))) {throw 'Rendezvous changed ordinary completion schema'}
    if($accepted) {
        if($null -ne $parentRegistration.TryPrepareCommitIfOffered() -or -not $custody.IsCurrent) {throw 'Completed probe replayed/retired custody'}
        $hashRead=$null; $primary=$null; $inputs=$null
        try {
            $hashRead=$custody.TryHashPrimarySource()
            if($null -eq $hashRead -or $hashRead.Sha256 -cne $completion.Outputs[3].Sha256) {throw 'Rendezvous lost native primary source'}
            $primary=[NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithWriter([guid]::NewGuid(),'Wrapper','frontier-gui-run','frontier-gui-scenario',$hashRead.Lease,$completion.Outputs[3].LineCount,$completion)
            $inputs=$custody.TryTransferToPrimary($primary)
            if($null -eq $inputs -or -not $inputs.IsCurrent -or $custody.IsCurrent) {throw 'Rendezvous failed original primary transfer'}
            $request.Dispose(); $hashRead.Lease.Dispose()
            if(-not $inputs.IsCurrent -or $inputs.LiveProofEligible -or $inputs.NativeWriterCustodyEstablished -or $inputs.PublicationCompletionIssued) {throw 'Rendezvous extended wrong lifetime/authority'}
        } finally {
            if($null -ne $inputs) {$inputs.Dispose()}
            if($null -ne $primary) {$primary.Dispose()}
            if($null -ne $hashRead) {$hashRead.Lease.Dispose()}
        }
    }
    if($null -ne $custody) {$custody.Dispose()}
    if($null -ne $parentRegistration) {
        $pending=$parentRegistration.GetType().GetField('PendingFiles',$parentFlags).GetValue($parentRegistration)
        if($null -ne $pending) {
            $pins=$pending.GetType().GetField('handles',$parentFlags).GetValue($pending)
            if(@($pins | Where-Object {-not $_.IsClosed}).Count) {throw 'Rendezvous retained provisional pins'}
        }
        if($clock.IsRunning) {throw 'Rendezvous retained running parent clock'}
    }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Rendezvous retained native payloads'}
    'frontier-parent-rendezvous='+$rendezvousCase
} elseif($Phase -ceq 'cleanup') {
    if(Get-Variable custody -ErrorAction SilentlyContinue) {if($null -ne $custody) {$custody.Dispose()}}
    if(Get-Variable collision -ErrorAction SilentlyContinue) {if($null -ne $collision) {$collision.Dispose()}}
}
