param([string]$Phase)
$handoffCase=if($FinalizerCase.StartsWith('persistence-handoff-')) {$FinalizerCase.Substring('persistence-handoff-'.Length)} else {''}
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
if($Phase -ceq 'wait') {
    $registration=$reports.ParentRegistration
    if($null -eq $registration -or -not $registration.IsCurrent) {throw 'Finalizer parent registration unavailable'}
    $channel=$registration.GetType().GetField('Channel',$flags).GetValue($registration)
    $readiness=$channel.GetType().GetField('rendezvousSignals',$flags).GetValue($channel)
    $parentWork=$registration.GetType().GetField('parentWork',$flags).GetValue($registration)
    if($handoffCase -ceq 'parent-budget') {
        $elapsed=@($parentWork.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($parentWork,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    $watch=[Diagnostics.Stopwatch]::StartNew(); $acted=$false; $manualPhase=0; $offered=$false
    while(-not $process.WaitForExit(25)) {
        if($watch.ElapsedMilliseconds -gt 80000) {throw 'Finalizer handoff fixture watchdog deadline'}
        if($null -ne $custody -or -not $registration.IsCurrent) {continue}
        if(-not $offered -and $readiness[0].WaitOne(0)) {
            $offered=$true
            if(-not [IO.File]::Exists((Join-Path $Directory 'filetransfer-gui-log-ledger.json'))) {throw 'Handoff offered before ordinary atomic persistence'}
        }
        if($handoffCase -ceq 'missing-parent') {continue}
        if($handoffCase -ceq 'parent-cancel') {
            if($offered) {$registration.Dispose()}
            continue
        }
        if($handoffCase -ceq 'early-commit') {
            if($manualPhase -eq 0 -and $offered) {
                $pending=$registration.TryPullFiles()
                if($null -eq $pending) {throw 'Early-commit fixture missing original file pull'}
                [void]$readiness[1].Set(); $manualPhase=1
            }
            if($manualPhase -eq 1 -and $readiness[2].WaitOne(0)) {
                $facts=$pending.TryReceiveFacts(); $ack=$facts.TryAcknowledge()
                if($null -eq $ack) {throw 'Early-commit fixture missing original ACK'}
                [void]$readiness[3].Set(); $manualPhase=2
            }
            if($manualPhase -eq 2 -and $readiness[4].WaitOne(0)) {[void]$readiness[5].Set(); $manualPhase=3}
            continue
        }
        if($handoffCase -cin @('signal-failure','stale-files') -and -not $acted) {
            if(-not $offered) {continue}
            if($handoffCase -ceq 'signal-failure') {$readiness[1].Dispose()}
            else {
                $mutationMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name+'.frontier-parent-v1.files-v1')
                $mutationView=$mutationMap.CreateViewAccessor()
                try {$mutationView.Write(16L,[byte]($mutationView.ReadByte(16L) -bxor 1))}
                finally {$mutationView.Dispose(); $mutationMap.Dispose()}
            }
            $acted=$true
        }
        $candidate=$registration.TryPrepareCommitIfOffered()
        if($null -ne $candidate) {
            if($candidate.IsCurrent -or $candidate.LiveProofEligible -or $candidate.NativeWriterCustodyEstablished -or $candidate.PublicationCompletionIssued) {throw 'Finalizer scheduling granted authority'}
            $custody=$candidate
        }
    }
} elseif($Phase -ceq 'result') {
    $accepted=$handoffCase -cin @('valid','positive')
    $legacyExpected=$handoffCase -cnotin @('early-commit','gui-failure','stage-failure')
    $completion=$request.TakeAfterExit()
    if(($null -ne $completion) -ne $legacyExpected -or ($null -ne $custody -and $custody.IsCurrent) -ne $accepted) {throw "Real finalizer completion/custody mismatch: $handoffCase; completion=$($null -ne $completion); custody=$($null -ne $custody)"}
    if($handoffCase -ceq 'stage-failure' -and $offered) {throw 'Persistence-stage failure still offered native files'}
    $preparation=$null
    try {
        if($accepted) {
            $path=Join-Path $Directory 'filetransfer-retained-log-slice.log'
            $preparation=$custody.TryPreparePrimarySource([guid]::NewGuid(),'Wrapper','cleanup-adoption-run','cleanup-adoption-scope',$path,$completion)
            if($null -eq $preparation -or -not $preparation.IsCurrent -or $custody.IsCurrent) {throw 'Real finalizer custody failed exact primary preparation'}
            $request.Dispose()
            if(-not $preparation.IsCurrent -or $preparation.NativeWriterCustodyEstablished -or $preparation.LiveProofEligible -or $preparation.PublicationCompletionIssued) {throw 'Real finalizer primary transfer lost lifetime or gained authority'}
        }
    } finally {if($null -ne $preparation) {$preparation.Dispose()}; if($null -ne $custody) {$custody.Dispose()}}
    $pending=$registration.GetType().GetField('PendingFiles',$flags).GetValue($registration)
    if($null -ne $pending) {
        $pins=$pending.GetType().GetField('handles',$flags).GetValue($pending)
        if(@($pins | Where-Object {-not $_.IsClosed}).Count) {throw 'Real finalizer retained provisional parent pins'}
    }
    if($parentWork.IsRunning -or @([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Real finalizer retained work/native resources'}
    foreach($file in @('filetransfer-frontier-retained-log-slice.log','filetransfer-frontier-finalizer-evidence.json')) {
        $path=Join-Path $Directory $file
        if([IO.File]::Exists($path)) {$probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()}
    }
    'frontier-finalizer-handoff='+$handoffCase
}
