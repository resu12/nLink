param([ValidateSet('Start','Finish','GuiOffer','Writers')][string]$Stage)
$private:payloadFault=$env:NLINK_TEST_PAYLOAD_WRAPPER_CASE
$private:payloadClone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
if($Stage -ceq 'Start') {
    if($null -ne $payloadClone.Invoke($localWriterRequest,$null).TryStartPayloadWrapper($frontierGuiReports) -or
        $null -ne $localWriterRequest.TryStartPayloadWrapper($payloadClone.Invoke($frontierGuiReports,$null))) {
        throw 'Copied request/reports started payload adopter'
    }
    $script:payloadWrapper=$localWriterRequest.TryStartPayloadWrapper($frontierGuiReports)
    if($null -eq $script:payloadWrapper -or $script:payloadWrapper.IsCurrent -or
        $script:payloadWrapper.TryTakeAfterExit($frontierGuiReports) -or
        $null -ne $localWriterRequest.TryStartPayloadWrapper($frontierGuiReports)) {throw 'Payload start/early take/repeat changed'}
    $private:payloadWork=$script:payloadWrapper.GetType().GetField('work',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($script:payloadWrapper)
    $private:parentWork=$frontierGuiReports.ParentRegistration.GetType().GetField('parentWork',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($frontierGuiReports.ParentRegistration)
    if(-not [object]::ReferenceEquals($payloadWork,$parentWork)) {throw 'Payload work replaced original cumulative clock'}
    if($payloadFault -ceq 'delayed-valid') {
        [IO.File]::WriteAllText((Join-Path $Directory 'listener-start-ticks.txt'),[string][Diagnostics.Stopwatch]::GetTimestamp())
    }
    if($payloadFault -ceq 'spent-budget') {
        $private:wait=[Threading.ManualResetEvent]::new($false)
        $parentWork.Start()
        try {[void]$wait.WaitOne(5050)} finally {$parentWork.Stop();$wait.Dispose()}
    }
    return
}
if($Stage -ceq 'GuiOffer') {
    if($payloadFault -ceq 'delayed-valid') {
        # Passive fixture delay only. Never stop/reset either original work clock.
        $private:guiWork=[NLink.Tests.AttemptWorkProfileForTest].GetField('work',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
        $private:guiWorkTicks=$guiWork.ElapsedTicks
        if($guiWork.IsRunning) {throw 'Cannot stage delayed offer inside active work'}
        $private:listenerStarted=[long][IO.File]::ReadAllText((Join-Path $Directory 'listener-start-ticks.txt'))
        do {
            $private:listenerAgeMs=([Diagnostics.Stopwatch]::GetTimestamp()-$listenerStarted)*1000.0/[Diagnostics.Stopwatch]::Frequency
            if($listenerAgeMs -lt 105000) {Start-Sleep -Milliseconds ([int][Math]::Min(500,105000-$listenerAgeMs+1))}
        } while($listenerAgeMs -lt 105000)
        if($guiWork.IsRunning -or $guiWork.ElapsedTicks -ne $guiWorkTicks) {throw 'Delayed offer altered original GUI work'}
        'listener-delayed-offer-age-ms='+[long]$listenerAgeMs
    }
    foreach($args in @(
        @($payloadClone.Invoke($attemptSource,$null),$attemptDestination,$join),
        @($attemptSource,$payloadClone.Invoke($attemptDestination,$null),$join),
        @($attemptSource,$attemptDestination,$payloadClone.Invoke($join,$null)))) {
        if($attemptTicket.TryOfferPayloadsToWrapper($args[0],$args[1],$args[2])) {throw 'Copied payload inputs exported'}
    }
    if($payloadClone.Invoke($attemptTicket,$null).TryOfferPayloadsToWrapper($attemptSource,$attemptDestination,$join)) {
        throw 'Copied attempt exported payloads'
    }
    $private:payloadOffered=$attemptTicket.TryOfferPayloadsToWrapper($attemptSource,$attemptDestination,$join)
    $private:payloadExpected=$payloadFault -cin @('valid','delayed-valid','gui-nonzero','gui-killed') -or $payloadFault -like 'writer-*'
    $private:payloadRepeat=$null
    # Preserve the original short-circuit: never make an extra authority call.
    if($payloadOffered -eq $payloadExpected) {
        $payloadRepeat=$attemptTicket.TryOfferPayloadsToWrapper($attemptSource,$attemptDestination,$join)
    }
    if($payloadOffered -ne $payloadExpected -or $payloadRepeat) {
        [Console]::Error.WriteLine('payload-offer-contract-failed; original_result='+$payloadOffered+
            '; expected_result='+$payloadExpected+'; repeat_evaluated='+($null -ne $payloadRepeat)+'; repeat_result='+$payloadRepeat)
        throw 'Payload offer result/repeat changed'
    }
    if(-not $attemptSource.IsCurrent -or -not $attemptDestination.IsCurrent -or -not $join.IsCurrent) {throw 'Payload offer retired original owners'}
    'attempt-payload-wrapper-offer='+$payloadFault
    if($payloadFault -ceq 'gui-killed') {
        # The wrapper kills only this exact child after native provisional adoption.
        $private:wait=[Threading.ManualResetEvent]::new($false)
        try {[void]$wait.WaitOne(15000)} finally {$wait.Dispose()}
        throw 'Wrapper did not inject GUI death after adoption'
    }
    return
}
if($Stage -ceq 'Writers') {
    try {
        if($null -eq $frontierParentCustody -or $frontierParentCustody.IsCurrent -or
            $script:payloadWrapper.NativeWritersBound) {throw 'Native writer custody missing or prematurely accepted'}
        foreach($args in @(
            @($null,$frontierParentCustody),
            @($frontierGuiReports,$null),
            @($payloadClone.Invoke($frontierGuiReports,$null),$frontierParentCustody),
            @($frontierGuiReports,$payloadClone.Invoke($frontierParentCustody,$null)))) {
            if($null -ne $script:payloadWrapper.TryTakeWithWriters($args[0],$args[1])) {throw 'Copied native writer inputs consumed take'}
        }
        $private:copy=$payloadClone.Invoke($script:payloadWrapper,$null)
        if($null -ne $copy.TryTakeWithWriters($frontierGuiReports,$frontierParentCustody)) {throw 'Copied payload owner consumed writer take'}
        $copy.Dispose()
        $private:writerFields=[Reflection.BindingFlags]'Instance,NonPublic'
        switch -CaseSensitive ($payloadFault) {
            'writer-foreign' {$script:payloadWrapper.GetType().GetField('writerCreations',$writerFields).GetValue($script:payloadWrapper)[1]++}
            'writer-request' {$script:payloadWrapper.GetType().GetField('writerRequests',$writerFields).GetValue($script:payloadWrapper)[1]=[guid]::NewGuid()}
            'writer-process' {$script:payloadWrapper.GetType().GetField('writerProcesses',$writerFields).GetValue($script:payloadWrapper)[1]++}
            'writer-id' {
                $private:id=$script:payloadWrapper.GetType().GetField('helperWriter',$writerFields)
                $id.SetValue($script:payloadWrapper,($id.GetValue($script:payloadWrapper)+'-foreign'))
            }
            'writer-sequence' {$script:payloadWrapper.GetType().GetField('writerLastSequences',$writerFields).GetValue($script:payloadWrapper)[1]++}
        }
        $private:acceptedWriter=$script:payloadWrapper.TryTakeWithWriters($frontierGuiReports,$frontierParentCustody)
        if(($null -ne $acceptedWriter) -ne ($payloadFault -ceq 'writer-valid') -or -not $frontierParentCustody.IsCurrent -or
            $null -ne $script:payloadWrapper.TryTakeWithWriters($frontierGuiReports,$frontierParentCustody) -or
            $script:payloadWrapper.NativeWritersBound -ne ($payloadFault -ceq 'writer-valid')) {
            throw 'Native writer/payload association outcome changed'
        }
        if($script:payloadWrapper.LiveProofEligible -or $script:payloadWrapper.TransferIntegrityEstablished -or
            $script:payloadWrapper.PublicationCompletionIssued) {throw 'Writer association granted runtime authority'}
        if($payloadFault -cne 'writer-valid') {
            $private:files=$script:payloadWrapper.GetType().GetField('files',$writerFields).GetValue($script:payloadWrapper)
            if($script:payloadWrapper.IsCurrent -or -not $files[0].IsClosed -or -not $files[1].IsClosed) {
                throw 'Rejected writer association retained payload handles'
            }
        }
        if($payloadFault -ceq 'writer-valid') {
            $copy=$payloadClone.Invoke($frontierParentCustody,$null); $copy.Dispose()
            if(-not $script:payloadWrapper.NativeWritersBound) {throw 'Copied writer custody retired payload pair'}
            $private:heldFiles=$script:payloadWrapper.GetType().GetField('files',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($script:payloadWrapper)
            $frontierParentCustody.Dispose()
            $private:releaseClock=[Diagnostics.Stopwatch]::StartNew()
            $private:releaseWait=[Threading.ManualResetEvent]::new($false)
            try {
                while((-not $heldFiles[0].IsClosed -or -not $heldFiles[1].IsClosed) -and $releaseClock.ElapsedMilliseconds -lt 5000) {
                    [void]$releaseWait.WaitOne(10)
                }
            } finally {$releaseWait.Dispose()}
            if(-not $heldFiles[0].IsClosed -or -not $heldFiles[1].IsClosed) {
                throw 'Native writer retirement did not close payload handles independently'
            }
            if($script:payloadWrapper.IsCurrent -or $script:payloadWrapper.NativeWritersBound) {throw 'Payload outlived native writer custody'}
        }
        $script:payloadWritersChecked=$true
        Write-Host ('attempt-payload-native-writers-checked='+$payloadFault)
    } catch {
        $script:payloadWritersFailure=$_.Exception.Message
        throw
    } finally {$script:payloadWrapper.Dispose()}
    return
}
if($null -eq $script:payloadWrapper) {throw 'Original payload wrapper lost'}
try {
    $private:copy=$payloadClone.Invoke($script:payloadWrapper,$null)
    if($copy.IsCurrent -or $copy.ProvisionalCustody -or $copy.TryTakeAfterExit($frontierGuiReports) -or
        $script:payloadWrapper.TryTakeAfterExit($payloadClone.Invoke($frontierGuiReports,$null))) {throw 'Copied custody/reports consumed terminal take'}
    $copy.Dispose()
    $script:payloadTaken=$script:payloadWrapper.TryTakeAfterExit($frontierGuiReports)
    if($script:payloadTaken -ne ($payloadFault -cin @('valid','delayed-valid') -or $payloadFault -like 'writer-*') -or
        $script:payloadWrapper.TryTakeAfterExit($frontierGuiReports) -or
        $script:payloadWrapper.LiveProofEligible -or $script:payloadWrapper.TransferIntegrityEstablished -or
        $script:payloadWrapper.PublicationCompletionIssued) {throw 'Payload normal-exit gate/repeat/authority changed'}
    if($script:payloadTaken -and $payloadFault -like 'writer-*' -and
        $null -ne $script:payloadWrapper.TryTakeWithWriters($frontierGuiReports,$frontierParentCustody)) {
        throw 'Unverified payload pair consumed native writer take'
    }
    if($script:payloadTaken -and (-not $script:payloadWrapper.IsCurrent -or
        -not $script:payloadWrapper.TryVerifyFiles() -or $script:payloadWrapper.TryVerifyFiles())) {
        throw 'Original wrapper native-file verification failed'
    }
    if($payloadFault -ceq 'spent-budget' -and $parentWork.ElapsedMilliseconds -le 5000) {throw 'Payload adoption reset its exhausted budget'}
    $script:payloadChecked=$true
    Write-Host ('attempt-payload-wrapper-checked='+$payloadFault)
} finally {if($payloadFault -notlike 'writer-*') {$script:payloadWrapper.Dispose()}}
if($payloadFault -like 'writer-*') {return}
if($script:payloadWrapper.IsCurrent -or $script:payloadWrapper.ProvisionalCustody -or $script:payloadWrapper.TryVerifyFiles()) {
    throw 'Retired wrapper payload custody revived'
}
