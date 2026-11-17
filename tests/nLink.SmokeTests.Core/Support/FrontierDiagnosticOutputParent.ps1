$outputMode=$workerCase.Substring('join-output-'.Length)
$receiver=$null; $transport=$null; $diagnosticMap=$null; $diagnosticView=$null; $escrowPins=@()
try {
    if($outputMode -ceq 'copied-capabilities') {
        $copy=$parentClone.Invoke($inputs,$null)
        if($null -ne $copy.TryPrepareDiagnosticReceiver() -or -not $inputs.IsCurrent) {throw 'Copied inputs prepared/retired receiver'}
    }
    if($outputMode -cne 'missing-receiver') {
        $receiver=$inputs.TryPrepareDiagnosticReceiver()
        if($null -eq $receiver -or -not $receiver.IsCurrent) {throw 'Diagnostic parent escrow unavailable'}
        $pinSet=$receiver.GetType().GetField('pins',$parentFlags).GetValue($receiver)
        $escrowPins=@($pinSet.GetType().GetField('Handles',$parentFlags).GetValue($pinSet))
        if($escrowPins.Count -ne 11) {throw 'Diagnostic escrow pin count changed'}
        $diagnosticMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($endpoint+'.diagnostic-v1')
        $diagnosticView=$diagnosticMap.CreateViewAccessor()
        if($outputMode -ceq 'repeat-prepare' -and $null -ne $inputs.TryPrepareDiagnosticReceiver()) {throw 'Receiver preparation replayed'}
        if($outputMode -ceq 'copied-capabilities') {
            $copy=$parentClone.Invoke($receiver,$null)
            if($copy.IsCurrent -or $copy.TryAdopt() -or $null -ne $copy.TakeAfterExit()) {throw 'Copied receiver acquired original'}
            $copy.Dispose(); if(-not $receiver.IsCurrent) {throw 'Copied receiver retired original'}
        }
    }
    [void]$signals.begin.Set()
    if(-not $signals.attached.WaitOne(30000)) {
        if($nativeProcess.HasExited) {throw "Output worker failed before offer: $($nativeOut.GetAwaiter().GetResult()) $($nativeErr.GetAwaiter().GetResult())"}
        throw 'Output offer deadline'
    }
    switch($outputMode) {
        'header-length' {$diagnosticView.Write(12L,[int]::MaxValue)}
        'payload-tamper' {$diagnosticView.Write(160L,[byte]($diagnosticView.ReadByte(160L) -bxor 1))}
        'packet-mismatch' {
            # Keep the wire digest coherent but substitute the bound request ID.
            $diagnosticView.Write(168L,[byte]($diagnosticView.ReadByte(168L) -bxor 1))
            $body=[byte[]]::new($diagnosticView.ReadInt32(12L)); [void]$diagnosticView.ReadArray(160L,$body,0,$body.Length)
            $sha=[Security.Cryptography.SHA256]::Create()
            try {$digest=$sha.ComputeHash($body); $diagnosticView.WriteArray(48L,$digest,0,32)} finally {$sha.Dispose()}
        }
        'escrow-pin' {$escrowPins[0].Dispose()}
        'parent-budget' {
            $watch=$receiver.GetType().GetField('work',$parentFlags).GetValue($receiver)
            $elapsed=@($watch.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
        'parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
    }
    $adoptExpected=$outputMode -cnotin @('missing-receiver','worker-budget','repeat-offer','header-length','payload-tamper','packet-mismatch','escrow-pin','parent-budget','parent-cancel','repeat-prepare','no-offer')
    if($null -ne $receiver -and $receiver.TryAdopt() -ne $adoptExpected) {throw ('Output adoption mismatch: '+$outputMode)}
    if($outputMode -ceq 'repeat-adopt' -and $receiver.TryAdopt()) {throw 'Output adoption replayed'}
    if($outputMode -ceq 'preexit-take' -and $null -ne $receiver.TakeAfterExit()) {throw 'Output consumed before worker exit'}
    if($outputMode -ceq 'state-only') {$diagnosticView.Write(8L,4)}
    [void]$signals.release.Set()
    if(-not $nativeProcess.WaitForExit(30000)) {throw 'Output worker exit deadline'}
    $nativeProcess.WaitForExit(); $output=$nativeOut.GetAwaiter().GetResult(); $nativeError=$nativeErr.GetAwaiter().GetResult()
    $expectedExit=if($outputMode -ceq 'worker-failure') {7} else {0}
    if($nativeProcess.ExitCode -ne $expectedExit -or $output -notmatch 'frontier-diagnostic-output-worker=') {throw "Output worker fixture failed: $output $nativeError"}
    if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{$inputs.IsCurrent -eq $false},5000) -or
        @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Original worker-exit input cleanup changed'}
    if($outputMode -ceq 'secret-tamper') {$diagnosticView.Write(112L,[byte]($diagnosticView.ReadByte(112L) -bxor 1))}
    $takeExpected=$outputMode -cin @('valid','copied-capabilities','repeat-take','cancel-after-take','copy-budget','pin-after-take')
    if($null -ne $receiver) {$transport=$receiver.TakeAfterExit()}
    if(($null -ne $transport) -ne $takeExpected) {throw ('Output take mismatch: '+$outputMode)}
    if($null -ne $transport) {
        $originalRows=@($output -split '\r?\n' | Where-Object {$_.StartsWith('frontier-diagnostic-original=')})
        if($originalRows.Count -ne 1) {throw 'Missing original fixture diagnostic'}
        $originalTransport=$originalRows[0].Substring('frontier-diagnostic-original='.Length) | ConvertFrom-Json
        if(-not $transport.IsCurrent -or $transport.WitnessCount -lt 1 -or $transport.WitnessCount -gt 64 -or
            $transport.WitnessCount -ne $originalTransport.Witnesses -or $transport.FullInvocationId -ne [guid]$originalTransport.Full -or
            $transport.CompactInvocationId -ne [guid]$originalTransport.Compact -or $transport.ObservationId -ne [guid]$originalTransport.Observation -or
            $transport.DiagnosticSnapshot -cne $originalTransport.Snapshot -or $transport.LiveProofEligible -or
            $transport.NativeWriterCustodyEstablished -or $transport.PublicationCompletionIssued -or
            @($escrowPins | Where-Object {$_.IsClosed}).Count) {
            throw ('Output transport lost escrow or gained authority: '+(@{Current=$transport.IsCurrent;Witnesses=$transport.WitnessCount;
                Full=$transport.FullInvocationId;Compact=$transport.CompactInvocationId;Observation=$transport.ObservationId;
                Snapshot=$transport.DiagnosticSnapshot;ClosedPins=@($escrowPins | Where-Object {$_.IsClosed}).Count} | ConvertTo-Json -Compress)+' worker='+$output)
        }
        $first=$transport.CopyPayload(); $first[0]=0
        $second=$transport.CopyPayload()
        if($second.Length -lt 1 -or $second.Length -gt 4194304 -or $second[0] -eq 0) {throw 'Export mutated original diagnostic bytes'}
        if($outputMode -ceq 'copied-capabilities') {
            $copy=$parentClone.Invoke($transport,$null)
            if($copy.IsCurrent -or $null -ne $copy.CopyPayload()) {throw 'Copied transport acquired original'}
            $copy.Dispose(); if(-not $transport.IsCurrent) {throw 'Copied transport retired original'}
        }
        if($outputMode -ceq 'repeat-take') {
            if($null -ne $receiver.TakeAfterExit() -or $transport.IsCurrent -or $null -ne $transport.CopyPayload()) {throw 'Diagnostic transport replay retained original'}
        }
        if($outputMode -ceq 'cancel-after-take') {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
            if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{$null -eq $transport.DiagnosticSnapshot},5000)) {throw 'Idle cancellation retained transport'}
        }
        if($outputMode -ceq 'copy-budget') {
            $watch=$receiver.GetType().GetField('work',$parentFlags).GetValue($receiver)
            $elapsed=@($watch.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
        if($outputMode -ceq 'pin-after-take') {$escrowPins[4].Dispose()}
        if($outputMode -cin @('copy-budget','pin-after-take') -and ($null -ne $transport.CopyPayload() -or $transport.IsCurrent -or
            $null -ne $transport.DiagnosticSnapshot)) {throw 'Revoked transport exported diagnostic bytes'}
    }
    if($null -ne $receiver -and ($receiver.LiveProofEligible -or $receiver.NativeWriterCustodyEstablished -or $receiver.PublicationCompletionIssued)) {throw 'Receiver gained authority'}
    $primary.Dispose()
    if(@($escrowPins | Where-Object {-not $_.IsClosed}).Count -or ($null -ne $transport -and ($transport.IsCurrent -or $null -ne $transport.DiagnosticSnapshot))) {throw 'Diagnostic escrow leaked after original disposal'}
    $output.Trim()
    'frontier-diagnostic-output='+$outputMode
} finally {
    if($null -ne $receiver) {$receiver.Dispose()}
    if($null -ne $diagnosticView) {$diagnosticView.Dispose()}; if($null -ne $diagnosticMap) {$diagnosticMap.Dispose()}
}
