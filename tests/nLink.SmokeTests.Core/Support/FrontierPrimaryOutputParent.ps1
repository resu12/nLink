$receiver=$null; $transport=$null; $joined=$null
try {
    $receiver=if($null -ne $reportRun -and $reportRun.NativeHandoffReady) {$reportRun.DiagnosticReceiver} else {$inputs.TryPrepareDiagnosticReceiver()}
    if($diagnosticCase) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticRendezvousParent.ps1')
        if($rendezvousFinished) {return}
    } else {
    if($null -eq $receiver) {throw 'Primary pipe diagnostic receiver missing'}
    if($pipeCase -ceq 'copied') {
        $copy=$parentClone.Invoke($primaryOutput,$null)
        if($copy.OutputsDrainedSuccessfully -or $null -ne $copy.TryJoinDiagnostic($null)) {throw 'Copied output owner acquired original'}
        $copy.Dispose()
    }
    [void]$signals.begin.Set()
    if(-not $signals.attached.WaitOne(30000)) {
        if($nativeProcess.HasExited) {throw ('Primary pipe worker failed: '+[IO.File]::ReadAllText($stdoutPath)+' '+[IO.File]::ReadAllText($stderrPath))}
        throw 'Primary pipe offer deadline'
    }
    if(-not $receiver.TryAdopt()) {throw 'Primary pipe diagnostic adoption failed'}
    if($pipeCase -ceq 'capture-fault') {$capture.GetType().GetField('stderrTarget',$parentFlags).GetValue($capture).Dispose()}
    if($pipeCase -ceq 'repeat-start' -and $null -ne $primary.TryStartFrontierWorkerWithOutput($nativeProcess,$Directory)) {throw 'Primary output start replayed'}
    if($primaryOutput.OutputsDrainedSuccessfully -or $null -ne $primaryOutput.TryJoinDiagnostic($null)) {throw 'Live primary output acquired EOF/join'}
    [void]$signals.release.Set()
    }
    if(-not $nativeProcess.WaitForExit(30000)) {throw 'Primary pipe worker exit deadline'}
    if($pipeCase -ceq 'nonzero-exit') {
        if($nativeProcess.ExitCode -ne 7) {throw ('Failed-exit fixture returned '+$nativeProcess.ExitCode+' instead of 7')}
        if($null -ne $receiver.TakeAfterExit()) {throw 'Failed primary exit acquired transport'}
        if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{
            $primaryOutput.GetType().GetField('closed',$parentFlags).GetValue($primaryOutput)
        },5000) -or $primaryOutput.OutputsDrainedSuccessfully -or $null -ne $primaryOutput.TryJoinDiagnostic($null)) {throw 'Failed worker exit retained output ownership'}
        $primary.Dispose(); 'frontier-primary-output='+$pipeCase; return
    }
    if($nativeProcess.ExitCode -ne 0) {throw 'Primary pipe worker did not exit successfully'}
    # No Process.WaitForExit() overload that also waits on caller event readers:
    # EOF belongs to the native capture, independently of process exit.
    $transport=$receiver.TakeAfterExit()
    if($null -eq $transport) {throw 'Primary pipe native transport missing'}
    if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{-not $inputs.IsCurrent},5000) -or
        @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Primary pipe changed original input-exit cleanup'}
    $held=$pipeCase -cin @('held-stdout','held-stderr')
    if(-not $held -and $pipeCase -cnotin @('capture-fault','repeat-start')) {
        if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{$primaryOutput.OutputsDrainedSuccessfully},10000)) {throw 'Primary pipe EOF deadline'}
    }
    if($held -and $primaryOutput.OutputsDrainedSuccessfully) {throw 'Worker exit substituted for inherited pipe EOF'}
    $retained=$capture.GetType().GetField('RecoveryOutputs',$parentFlags).GetValue($capture)
    switch($pipeCase) {
        'stdout-pin' {$retained[0].GetType().GetField('file',$parentFlags).GetValue($retained[0]).Dispose()}
        'scope' {$primaryView.Write(40L,$primaryView.ReadInt64(40L)+1L)}
        'transport-dispose' {$transport.Dispose()}
    }
    if($pipeCase -ceq 'copied') {
        $copy=$parentClone.Invoke($transport,$null)
        if($null -ne $primaryOutput.TryJoinDiagnostic($copy) -or -not $transport.IsCurrent) {throw 'Copied transport acquired/retired original output join'}
    }
    $joined=$primaryOutput.TryJoinDiagnostic($transport)
    if($pipeCase.StartsWith('terminal-',[StringComparison]::Ordinal)) {
        if($null -eq $joined) {throw 'Terminal fixture lacks original output join'}
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierTerminalReceipt.ps1')
        return
    }
    $expected=$pipeCase -cin @('valid','copied','repeat-join','join-dispose','parent-cancel','after-pin')
    if(($null -ne $joined) -ne $expected) {throw ('Primary output join mismatch: '+$pipeCase)}
    if($null -ne $joined) {
        $outputHandles=@($retained | ForEach-Object {$_.GetType().GetField('file',$parentFlags).GetValue($_).SafeFileHandle})
        if(-not $joined.IsCurrent -or $joined.LiveProofEligible -or $joined.NativeWriterCustodyEstablished -or $joined.PublicationCompletionIssued) {throw 'Primary output join missing/gained authority'}
        foreach($path in @($stdoutPath,$stderrPath)) {
            # Match the retained creation handle's sharing requirements. This
            # fixture read is diagnostic only and is never input to the join.
            $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $binary=[IO.BinaryReader]::new($stream)
            try {$bytes=$binary.ReadBytes([int]$stream.Length)} finally {$binary.Dispose(); $stream.Dispose()}
            if($bytes.Length -lt 70003) {throw 'Original pipe bytes truncated'}
            for($i=0;$i -lt 70003;$i++) {if($bytes[$bytes.Length-70003+$i] -ne [byte]($i % 256)) {throw 'Original pipe bytes changed'}}
        }
        if($primaryOutput.StdoutBytes -ne (Get-Item $stdoutPath).Length -or $primaryOutput.StderrBytes -ne (Get-Item $stderrPath).Length) {throw 'Original output lengths changed'}
        if($pipeCase -ceq 'copied') {
            $copy=$parentClone.Invoke($joined,$null); if($copy.IsCurrent) {throw 'Copied output join acquired original'}
            $copy.Dispose(); if(-not $joined.IsCurrent) {throw 'Copied output join retired original'}
        }
        switch($pipeCase) {
            'repeat-join' {if($null -ne $primaryOutput.TryJoinDiagnostic($transport)) {throw 'Output join replayed'}}
            'join-dispose' {$joined.Dispose()}
            'parent-cancel' {
                $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
                try {[void]$event.Set()} finally {$event.Dispose()}
            }
            'after-pin' {$retained[1].GetType().GetField('file',$parentFlags).GetValue($retained[1]).Dispose()}
        }
        if($pipeCase -ceq 'parent-cancel' -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{
            @($outputHandles | Where-Object {-not $_.IsClosed}).Count -eq 0
        },5000)) {throw 'Idle primary cancellation retained output pins'}
        if($pipeCase -cin @('repeat-join','join-dispose','parent-cancel','after-pin') -and $joined.IsCurrent) {throw 'Revoked output join remained current'}
    }
    if($held) {
        [void]$signals['held-release'].Set()
        if($null -ne $primaryOutput.TryJoinDiagnostic($transport)) {throw 'Missing-EOF attempt revived after late pipe closure'}
    }
    if($primaryOutput.LiveProofEligible -or $primaryOutput.NativeWriterCustodyEstablished -or $primaryOutput.PublicationCompletionIssued) {throw 'Primary output capture gained authority'}
    $primary.Dispose()
    foreach($output in $retained) {if($output.GetType().GetProperty('IsCurrent',$parentFlags).GetValue($output,$null)) {throw 'Primary output pin leaked'}}
    'frontier-primary-output='+$pipeCase
} finally {
    [void]$signals['held-release'].Set()
    if($null -ne $receiver) {$receiver.Dispose()}
}
