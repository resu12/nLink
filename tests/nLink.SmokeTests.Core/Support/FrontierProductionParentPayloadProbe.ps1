param([ValidateSet('Started','Polled','BeforeTake','AfterTake','AnalysisBoundary','Finished','WrapperOffered')][string]$Stage)
$private:parentCase=$env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD
$private:fields=[Reflection.BindingFlags]'Instance,NonPublic'
if($Stage -ceq 'WrapperOffered') {
    if(-not $private:canonicalPayloadWrapper.NativeWritersBound -or
        -not $FrontierRelayHandoff.GetType().GetField('payloadOfferAttempted',$fields).GetValue($FrontierRelayHandoff) -or
        $FrontierRelayHandoff.TryOfferPayloadPair($private:canonicalPayloadWrapper,$private:acceptedWriter)) {throw 'Production parent offer missing/repeated'}
    Write-Host 'production-parent-payload-offered'
    return
}
if($Stage -ceq 'Started') {
    $script:productionParent=$private:canonicalPayloadParent
    $script:productionParentPulled=$false; $script:productionParentChecked=$false
    $script:productionParentFailure=''; $script:productionParentFiles=$null
    if($null -eq $script:productionParent -or $script:productionParent.IsCurrent) {throw 'Production parent listener missing'}
    $copy=[object].GetMethod('MemberwiseClone',$fields).Invoke($private:relayRequest,$null)
    if($null -ne $copy.TakeRelayAfterExitWithPayloads($private:relayReports) -or
        $null -ne $private:relayRequest.TryStartPayloadParent($private:relayReports)) {throw 'Copied/repeated parent admitted'}
    if($private:relayRequest.GetType().GetField('sealedState',$fields).GetValue($private:relayRequest) -ne 0 -or
        $private:relayRequest.GetType().GetField('closed',$fields).GetValue($private:relayRequest).WaitOne(0)) {throw 'Parent request sealed during listener start'}
    return
}
if($Stage -ceq 'Polled') {
    if($script:productionParent.ProvisionalCustody) {
        $script:productionParentPulled=$true
        $script:productionParentFiles=$script:productionParent.GetType().GetField('files',$fields).GetValue($script:productionParent)
    }
    return
}
if($Stage -ceq 'BeforeTake') {
    if(-not $private:relayOutputCaptured -or $exitCode -ne 0 -or -not $script:productionParentPulled) {throw 'Original parent completion gates missing'}
    $script:productionNative=$private:relayRequest.GetType().GetField('frontierRelayCustody',$fields).GetValue($private:relayRequest)
    if($null -eq $script:productionNative) {throw 'Original native relay missing'}
    if($parentCase -ceq 'foreign-writer') {$script:productionParent.GetType().GetField('creations',$fields).GetValue($script:productionParent)[1]++}
    elseif($parentCase -ceq 'missing-pair') {$script:productionParent.Dispose()}
    return
}
if($Stage -ceq 'AfterTake') {
    try {
        $script:productionResult=$private:relayedWriter
        if($null -eq $script:productionResult -or -not $script:productionNative.IsCurrent -or
            -not [object]::ReferenceEquals($script:productionNative.GetType().GetField('accepted',$fields).GetValue($script:productionNative),$script:productionResult) -or
            $script:productionParent.IsCurrent -ne ($parentCase -ceq 'valid')) {throw 'Payload rejection lost ordinary result or native custody'}
        if($null -ne $private:relayRequest.TakeRelayAfterExitWithPayloads($private:relayReports) -or
            -not $script:productionNative.IsCurrent) {throw 'Repeated take retired native custody'}
        if($script:productionParent.LiveProofEligible -or $script:productionParent.TransferIntegrityEstablished -or
            $script:productionParent.PublicationCompletionIssued) {throw 'Payload custody granted authority'}
        $script:productionParentChecked=$true
    } catch {$script:productionParentFailure=$_.Exception.Message; throw}
    return
}
if($Stage -ceq 'AnalysisBoundary') {
    if($script:productionParentFailure) {throw $script:productionParentFailure}
    if(-not $script:productionParentChecked -or -not [object]::ReferenceEquals($FrontierRelayCustody,$script:productionNative) -or
        -not $FrontierRelayCustody.IsCurrent -or $null -ne $script:productionResult.TryTakeFrontierCustody() -or
        $script:productionParent.IsCurrent -ne ($parentCase -ceq 'valid')) {throw 'Original export or parent lifetime lost at analysis boundary'}
    if($parentCase -ceq 'valid') {
        $received=@(Get-ChildItem -LiteralPath (Join-Path $Directory 'native-received') -Recurse -File -Filter '*.bin')
        if($received.Count -ne 1) {throw 'Received payload inventory changed'}
        foreach($path in @((Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'),$received[0].FullName)) {
            $opened=$null
            try {$opened=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)} catch [IO.IOException] {}
            if($null -ne $opened) {$opened.Dispose(); throw 'Exported parent lost file protection'}
        }
    } elseif(@($script:productionParentFiles | Where-Object {-not $_.IsClosed}).Count) {throw 'Rejected pair kept handles'}
    return
}
if($failure -or $script:productionParentFailure -or -not $script:productionParentChecked) {throw ('Production parent unfinished: '+$failure+' / '+$script:productionParentFailure)}
$clock=[Diagnostics.Stopwatch]::StartNew();$wait=[Threading.ManualResetEvent]::new($false)
try {while(@($script:productionParentFiles | Where-Object {-not $_.IsClosed}).Count -and $clock.ElapsedMilliseconds -lt 5000) {[void]$wait.WaitOne(10)}} finally {$wait.Dispose()}
if($script:productionParent.IsCurrent -or @($script:productionParentFiles | Where-Object {-not $_.IsClosed}).Count) {throw 'Original native lifetime retained payload files'}
'production-parent-payload-checked='+$parentCase
