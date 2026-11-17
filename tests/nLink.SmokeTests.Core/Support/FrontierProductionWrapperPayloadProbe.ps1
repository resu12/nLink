param([ValidateSet('Started','BeforeTake','AfterTake','Finished')][string]$Stage)
$private:payloadCase=$env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD
$private:payloadFlags=[Reflection.BindingFlags]'Instance,NonPublic'
if($Stage -ceq 'Started') {
    $script:payloadWrapper=$private:canonicalPayloadWrapper
    if($null -eq $script:payloadWrapper -or $script:payloadWrapper.IsCurrent -or
        $script:payloadWrapper.TryTakeAfterExit($frontierGuiReports)) {throw 'Production listener missing or early take admitted'}
    $copy=[object].GetMethod('MemberwiseClone',$payloadFlags).Invoke($localWriterRequest,$null)
    if($null -ne $copy.TakeAfterExitWithPayloads($frontierGuiReports,$null)) {throw 'Copied request consumed original completion'}
    return
}
if($Stage -ceq 'BeforeTake') {
    if(-not $exitCodeAvailable -or $exitCode -ne 0 -or -not $outputCapture.Completed -or
        $null -eq $frontierParentCustody -or -not $script:payloadWrapper.ProvisionalCustody) {throw 'Original GUI completion/custody missing'}
    $script:originalPayloadCustody=$frontierParentCustody
    switch($payloadCase) {
        'foreign-writer' {$script:payloadWrapper.GetType().GetField('writerCreations',$payloadFlags).GetValue($script:payloadWrapper)[1]++}
        'hash-mismatch' {$script:payloadWrapper.GetType().GetField('hashes',$payloadFlags).GetValue($script:payloadWrapper)[1]='0'*64}
        'missing-custody' {$private:frontierParentCustody=$null}
        'valid' {}
        default {throw 'Unknown production wrapper payload case'}
    }
    return
}
if($Stage -ceq 'AfterTake') {
    $expectedPair=$payloadCase -ceq 'valid'
    if($null -eq $private:acceptedWriter -or -not $script:originalPayloadCustody.IsCurrent -or
        -not [object]::ReferenceEquals($script:originalPayloadCustody.GetType().GetField('acceptedCompletion',$payloadFlags).GetValue($script:originalPayloadCustody),$private:acceptedWriter) -or
        $script:payloadWrapper.NativeWritersBound -ne $expectedPair) {throw 'Optional payload changed original completion or writer binding'}
    if($null -ne $localWriterRequest.TakeAfterExitWithPayloads($frontierGuiReports,$frontierParentCustody) -or
        -not $script:originalPayloadCustody.IsCurrent) {throw 'Repeated completion take returned or retired original custody'}
    if($script:payloadWrapper.LiveProofEligible -or $script:payloadWrapper.TransferIntegrityEstablished -or
        $script:payloadWrapper.PublicationCompletionIssued) {throw 'Payload custody granted route authority'}
    $script:payloadFiles=$script:payloadWrapper.GetType().GetField('files',$payloadFlags).GetValue($script:payloadWrapper)
    # Missing optional custody closes via the original request's close event;
    # final cleanup below must release it even if that callback is still pending.
    if($payloadCase -cin @('foreign-writer','hash-mismatch') -and
        (-not $script:payloadFiles[0].IsClosed -or -not $script:payloadFiles[1].IsClosed)) {throw 'Rejected pair retained files'}
    $script:payloadChecked=$true;$script:payloadWritersChecked=$true
    return
}
if($failure -or -not $script:payloadChecked -or -not $script:payloadWritersChecked) {throw ('Production wrapper did not finish: '+$failure)}
if($script:payloadWrapper.IsCurrent -or -not $script:payloadFiles[0].IsClosed -or -not $script:payloadFiles[1].IsClosed) {throw 'Production cleanup retained payload custody'}
'production-wrapper-payload-checked='+$payloadCase
