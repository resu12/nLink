# Original held source -> exact original helpee native process, over its stdin.
# The event is fixture scheduling only; app assertions and original normal-exit/
# writer/output gates still determine whether the test succeeded.
if($copied.TrySendToHelpeeForTest($SenderRequest) -or
    $Source.Value.TrySendToHelpeeForTest($ReceiverRequest) -or
    $Source.Value.TrySendToHelpeeForTest($clone.Invoke($SenderRequest,$null))) {throw 'Foreign source delivery admitted'}
$senderFault=$attemptCase.Substring('writer-source-read-'.Length)
if($senderFault.StartsWith('native-')) {$senderFault=$senderFault.Substring('native-'.Length)}
$Source.Value.GetType().GetField('SenderFault',$flags).SetValue($Source.Value,$senderFault)
try {
    if($attemptCase -ceq 'writer-source-read-native-missing') {
        # Keep an empty pipe open: a picker must not wait for a future packet.
    } elseif($attemptCase -ceq 'writer-source-read-native-partial') {
        $Context.HelpeeProc.StandardInput.BaseStream.Write([byte[]]@(100,0,0,0,1),0,5)
        $Context.HelpeeProc.StandardInput.BaseStream.Flush()
    } elseif(-not $Source.Value.TrySendToHelpeeForTest($SenderRequest) -or
        $Source.Value.TrySendToHelpeeForTest($SenderRequest)) {throw 'Original source delivery missing/repeated'}
    if($attemptCase.StartsWith('writer-source-read-native-')) {[void]$script:signals['sender-source-packet-ready'].Set()}
    if($attemptCase.StartsWith('writer-source-read-cross-process-')) {
        if($Attempt.TrySendPeerProcessesForTest($clone.Invoke($SenderRequest,$null),$ReceiverRequest,'valid') -or
            $Attempt.TrySendPeerProcessesForTest($SenderRequest,$clone.Invoke($ReceiverRequest,$null),'valid')) {throw 'Copied peer request admitted'}
        $fault=$attemptCase.Substring('writer-source-read-cross-process-'.Length)
        if($fault.StartsWith('carrier-')) {$fault=$fault.Substring('carrier-'.Length)}
        if(-not $Attempt.TrySendPeerProcessesForTest($SenderRequest,$ReceiverRequest,$fault) -or
            $Attempt.TrySendPeerProcessesForTest($SenderRequest,$ReceiverRequest,$fault)) {throw 'Original peer delivery missing/repeated'}
    }
} finally {
    if($attemptCase -cnotin @('writer-source-read-native-missing','writer-source-read-native-partial')) {$Context.HelpeeProc.StandardInput.Close()}
    if($attemptCase.StartsWith('writer-source-read-cross-process-')) {$Context.HelperProc.StandardInput.Close()}
}
$readBudget=if($attemptCase.StartsWith('writer-source-read-cross-process-carrier-full-')) {185000}
    elseif($attemptCase.StartsWith('writer-source-read-cross-process-')) {75000} else {25000}
$readClock=[Diagnostics.Stopwatch]::StartNew()
if($attemptCase -cin @('writer-source-read-cross-process-gui-valid','writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication')) {
    if($attemptCase -cin @('writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication') -and
        -not $script:signals['destination-handoff-ready'].WaitOne([Math]::Max(0,$readBudget-[int]$readClock.ElapsedMilliseconds))) {
        throw 'Original receiver handoff scheduling deadline'
    }
    $private:none=[Threading.CancellationToken]::None
    $private:cancelled=[Threading.CancellationToken]::new($true)
    if($null -ne $Attempt.TryAdoptDestination($SenderRequest,$Source.Value,$none) -or
        $null -ne $Attempt.TryAdoptDestination($clone.Invoke($ReceiverRequest,$null),$Source.Value,$none) -or
        $null -ne $Attempt.TryAdoptDestination($ReceiverRequest,$copied,$none) -or
        $null -ne $clone.Invoke($Attempt,$null).TryAdoptDestination($ReceiverRequest,$Source.Value,$none) -or
        $null -ne $Attempt.TryAdoptDestination($ReceiverRequest,$Source.Value,$cancelled)) {throw 'Foreign/cancelled destination admitted'}
    $private:destination=$Attempt.TryAdoptDestination($ReceiverRequest,$Source.Value,$none)
    if($null -eq $destination -or -not $destination.IsCurrent -or $destination.ByteCount -ne 268435456 -or
        $destination.TransferIntegrityEstablished -or $destination.LiveProofEligible -or $destination.PublicationCompletionIssued) {
        throw 'Full-size destination GUI adoption missing or promoted'
    }
    if($null -ne $Attempt.TryAdoptDestination($ReceiverRequest,$Source.Value,$none)) {throw 'Repeated destination admitted'}
    $private:destinationCopy=$clone.Invoke($destination,$null)
    if($destinationCopy.IsCurrent -or $destinationCopy.ByteCount -ne 0 -or $null -ne $destinationCopy.FileIdentity) {throw 'Copied destination current'}
    $destinationCopy.Dispose()
    $clone.Invoke($Attempt,$null).Dispose()
    if(-not $destination.IsCurrent) {throw 'Copy disposal retired destination'}
    'canonical-destination-controls-ok'
    'attempt-destination-gui-adoption-ok'
}
if(-not $script:signals['sender-read-done'].WaitOne([Math]::Max(0,$readBudget-[int]$readClock.ElapsedMilliseconds))) {throw 'Original sender read deadline'}
if($attemptCase.StartsWith('writer-source-read-cross-process-') -and
    -not $script:signals['receiver-read-done'].WaitOne([Math]::Max(0,$readBudget-[int]$readClock.ElapsedMilliseconds))) {throw 'Original receiver read deadline'}
if(-not $Source.Value.IsCurrent -or $Source.Value.TransferIntegrityEstablished -or
    $Source.Value.LiveProofEligible -or $Source.Value.PublicationCompletionIssued) {throw 'Sender read changed GUI source custody/authority'}
Assert-SourceProtected $AutopickPath
'attempt-sender-source-delivered'
