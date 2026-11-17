param([ValidateSet('Start','Poll','Take','AnalysisBoundary','Finish','WrapperOffer')][string]$Stage)
$private:parentFault=$env:NLINK_TEST_PAYLOAD_PARENT_CASE
$private:parentClone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$private:parentFields=[Reflection.BindingFlags]'Instance,NonPublic'
if($Stage -ceq 'WrapperOffer') {
    try {
        $private:acceptedWriter=$script:payloadWrapper.TryTakeWithWriters($frontierGuiReports,$frontierParentCustody)
        if($null -eq $acceptedWriter -or -not $script:payloadWrapper.NativeWritersBound) {throw 'Original wrapper writer acceptance missing'}
        if($FrontierRelayHandoff.TryOfferPayloadPair($parentClone.Invoke($script:payloadWrapper,$null),$acceptedWriter) -or
            $FrontierRelayHandoff.TryOfferPayloadPair($script:payloadWrapper,$parentClone.Invoke($acceptedWriter,$null)) -or
            $parentClone.Invoke($FrontierRelayHandoff,$null).TryOfferPayloadPair($script:payloadWrapper,$acceptedWriter)) {throw 'Copied inputs offered parent payload'}
        $FrontierRelayHandoff.CancelPayloadCommitForTest=($parentFault -ceq 'cancel-before-commit')
        $private:offered=$FrontierRelayHandoff.TryOfferPayloadPair($script:payloadWrapper,$acceptedWriter)
        if($offered -ne ($parentFault -cne 'cancel-before-commit') -or
            $FrontierRelayHandoff.TryOfferPayloadPair($script:payloadWrapper,$acceptedWriter) -or
            -not $script:payloadWrapper.NativeWritersBound) {throw 'Payload offer outcome/repeat/source continuity changed'}
        $script:payloadWritersChecked=$true
        Write-Host ('attempt-payload-parent-offered='+$parentFault)
    } catch {$script:payloadWritersFailure=$_.Exception.Message; throw}
    return
}
if($Stage -ceq 'Start') {
    if($null -ne $parentClone.Invoke($private:relayRequest,$null).TryStartPayloadParent($private:relayReports) -or
        $null -ne $private:relayRequest.TryStartPayloadParent($parentClone.Invoke($private:relayReports,$null))) {throw 'Copied parent/reports started payload owner'}
    $script:payloadParent=$private:relayRequest.TryStartPayloadParent($private:relayReports)
    if($null -eq $script:payloadParent -or $script:payloadParent.IsCurrent -or
        $null -ne $script:payloadParent.TryTakeAfterExit($private:relayReports) -or
        $null -ne $private:relayRequest.TryStartPayloadParent($private:relayReports)) {throw 'Parent start/early take/repeat changed'}
    if($script:payloadParent.LastTakeDiagnosticForTest -cne 'worker-running') {throw 'Early parent take diagnostic changed'}
    $private:work=$script:payloadParent.GetType().GetField('work',$parentFields).GetValue($script:payloadParent)
    $private:relayWork=$private:relayReports.Registration.GetType().GetField('transferWork',$parentFields).GetValue($private:relayReports.Registration)
    if(-not [object]::ReferenceEquals($work,$relayWork)) {throw 'Parent replaced original cumulative relay clock'}
    return
}
if($Stage -ceq 'Poll') {
    if(-not $script:payloadParent.TryPullIfOffered()) {return}
    if($script:payloadParentPulled -or ($parentFault -cne 'cancel-before-commit' -and -not $script:payloadParent.ProvisionalCustody) -or $script:payloadParent.IsCurrent) {throw 'Parent provisional custody changed'}
    $script:payloadParentPulled=$true
    $script:payloadParentFiles=$script:payloadParent.GetType().GetField('files',$parentFields).GetValue($script:payloadParent)
    if($parentFault -ceq 'foreign-writer') {
        $private:incarnations=$script:payloadParent.GetType().GetField('creations',$parentFields).GetValue($script:payloadParent)
        $incarnations[1]++; $script:payloadParentFaulted=$true
    } elseif($parentFault -ceq 'parent-budget') {
        $private:work=$script:payloadParent.GetType().GetField('work',$parentFields).GetValue($script:payloadParent)
        $private:wait=[Threading.ManualResetEvent]::new($false)
        $work.Start(); try {[void]$wait.WaitOne(5050)} finally {$work.Stop();$wait.Dispose()}
        $script:payloadParentFaulted=$true
    } elseif($parentFault -ceq 'wrapper-killed') {
        $private:relayProcess.Kill(); [void]$private:relayProcess.WaitForExit(5000); $script:payloadParentFaulted=$true
    }
    return
}
if($Stage -ceq 'Take') {
    try {
        $private:copy=$parentClone.Invoke($script:payloadParent,$null)
        if($copy.IsCurrent -or $copy.ProvisionalCustody -or
            $null -ne $copy.TryTakeAfterExit($private:relayReports) -or
            $null -ne $script:payloadParent.TryTakeAfterExit($parentClone.Invoke($private:relayReports,$null))) {throw 'Copied parent custody/reports consumed take'}
        if($copy.LastTakeDiagnosticForTest -cne 'foreign-instance' -or $script:payloadParent.LastTakeDiagnosticForTest -cne 'foreign-reports') {throw 'Copied parent take diagnostics changed'}
        $copy.Dispose()
        if($parentFault -cin @('native-accept-scope','native-begin-disposed','native-wrapper-state','native-transfer-cancel')) {
            $private:nativeControl=$private:relayRequest.GetType().GetField('frontierRelayCustody',$parentFields).GetValue($private:relayRequest)
            if($null -eq $nativeControl) {throw 'Native acceptance control lacks original relay custody'}
            if($parentFault -ceq 'native-accept-scope') {$nativeControl.GetType().GetField('scope',$parentFields).GetValue($nativeControl)[0]++}
            elseif($parentFault -ceq 'native-begin-disposed') {$nativeControl.Dispose()}
            else {
                $private:nativeTransfer=$nativeControl.GetType().GetField('transfer',$parentFields).GetValue($nativeControl)
                if($parentFault -ceq 'native-wrapper-state') {
                    $nativeTransfer.GetType().GetField('view',$parentFields).GetValue($nativeTransfer).Write(8L,2)
                    [void]$nativeControl.GetType().GetMethod('WrapperExited',$parentFields).Invoke($nativeControl,@($null,$false))
                } else {
                    [void]$nativeTransfer.GetType().GetField('cancelled',$parentFields).GetValue($nativeTransfer).Set()
                    $private:closedField=$nativeControl.GetType().GetField('closed',$parentFields)
                    $private:wait=[Threading.ManualResetEvent]::new($false); $private:clock=[Diagnostics.Stopwatch]::StartNew()
                    try {while(-not $closedField.GetValue($nativeControl) -and $clock.ElapsedMilliseconds -lt 5000) {[void]$wait.WaitOne(10)}} finally {$wait.Dispose()}
                    if(-not $closedField.GetValue($nativeControl)) {throw 'Original native cancellation callback did not retire custody'}
                }
            }
            $script:payloadParentFaulted=$true
        }
        $script:payloadParentResult=$script:payloadParent.TryTakeAfterExit($private:relayReports)
        $private:firstTakeReason=$script:payloadParent.LastTakeDiagnosticForTest
        [Console]::Error.WriteLine('parent-payload-take; call=first; result='+($null -ne $script:payloadParentResult)+'; reason='+$firstTakeReason+'; association='+$script:payloadParent.LastParentMatchDiagnosticForTest+
            '; native_begin='+$script:payloadParent.LastNativeBeginDiagnosticForTest+'; native_accept='+$script:payloadParent.LastNativeAcceptDiagnosticForTest+'; native_close='+$script:payloadParent.LastNativeCloseDiagnosticForTest+
            '; native_retirement='+$script:payloadParent.LastNativeRetirementDiagnosticForTest)
        if(($null -ne $script:payloadParentResult) -ne ($parentFault -ceq 'valid')) {throw 'Parent association outcome/repeated take changed'}
        $private:repeatedTake=$script:payloadParent.TryTakeAfterExit($private:relayReports)
        [Console]::Error.WriteLine('parent-payload-take; call=repeat; result='+($null -ne $repeatedTake)+'; reason='+$script:payloadParent.LastTakeDiagnosticForTest)
        if($null -ne $repeatedTake) {throw 'Parent association outcome/repeated take changed'}
        if($parentFault -ceq 'valid') {
            $private:native=$script:payloadParent.WriterCustody($script:payloadParentResult)
            if($null -eq $native -or $null -ne $script:payloadParentResult.TryTakeFrontierCustody() -or
                $null -ne $script:payloadParent.WriterCustody($parentClone.Invoke($script:payloadParentResult,$null))) {throw 'Exact detached native custody lost/repeated'}
            $parentClone.Invoke($native,$null).Dispose()
            if(-not $script:payloadParent.IsCurrent) {throw 'Copied native custody retired parent pair'}
        }
    } catch {$script:payloadParentFailure=$_.Exception.Message; throw}
    return
}
if($Stage -ceq 'AnalysisBoundary') {
    if($script:payloadParentFailure) {throw $script:payloadParentFailure}
    if($script:payloadParent.IsCurrent -ne ($parentFault -ceq 'valid')) {throw 'Parent payload not bound at analysis boundary'}
    if($parentFault -ceq 'valid') {
        $private:receiveRoot=if($Case -cin @('writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication')) {'native-carrier-received'} else {'native-received'}
        $private:received=@(Get-ChildItem -LiteralPath (Join-Path $Directory $receiveRoot) -File -Filter '*.bin' -Recurse -ErrorAction Stop)
        if($received.Count -ne 1) {throw 'Unexpected received-file protection inventory'}
        $private:source=Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'
        if(-not (Test-Path -LiteralPath $source -PathType Leaf)) {throw 'Source payload missing at analysis boundary'}
        foreach($path in @($source,$received[0].FullName)) {
            $private:opened=$null
            try {$opened=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)} catch [IO.IOException] {}
            if($null -ne $opened) {$opened.Dispose(); throw 'Parent payload lost continuous native file protection'}
        }
    }
    return
}
try {
    if($script:payloadParentFailure -or -not $script:payloadParentPulled -or $null -eq $script:payloadParentFiles) {throw ('Parent handoff not exercised: '+$script:payloadParentFailure+' / '+$failure)}
if($parentFault -cin @('foreign-writer','parent-budget','wrapper-killed','native-accept-scope','native-begin-disposed','native-wrapper-state','native-transfer-cancel') -and -not $script:payloadParentFaulted) {throw 'Parent fault injection omitted'}
    $private:releaseClock=[Diagnostics.Stopwatch]::StartNew(); $private:wait=[Threading.ManualResetEvent]::new($false)
    try {while((@($script:payloadParentFiles | Where-Object {-not $_.IsClosed}).Count -gt 0) -and $releaseClock.ElapsedMilliseconds -lt 5000) {[void]$wait.WaitOne(10)}} finally {$wait.Dispose()}
    if(@($script:payloadParentFiles | Where-Object {-not $_.IsClosed}).Count -ne 0) {throw 'Native relay retirement did not independently close parent payload handles'}
    if($script:payloadParent.IsCurrent -or $script:payloadParent.ProvisionalCustody -or $script:payloadParent.LiveProofEligible -or
        $script:payloadParent.TransferIntegrityEstablished -or $script:payloadParent.PublicationCompletionIssued) {throw 'Parent payload retirement/authority changed'}
    Write-Host ('attempt-payload-parent-checked='+$parentFault)
} finally {if($null -ne $script:payloadParent) {$script:payloadParent.Dispose()}}
