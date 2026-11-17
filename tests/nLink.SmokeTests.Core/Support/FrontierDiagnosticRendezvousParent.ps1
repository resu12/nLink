$rv=$diagnosticCase; $rendezvousFinished=$false; $adoptionTask=$null
$polling=Get-Variable rendezvousPolling -ValueOnly -ErrorAction SilentlyContinue
$rvMap=$null; $rvView=$null; $rvSignals=@{}
$rvSuccess=$rv -cin @('valid','parent-first','offer-first','copied','repeat-wait')
try {
    if(($null -eq $receiver) -ne $rv.StartsWith('collision-')) {throw 'Rendezvous receiver mismatch'}
    if($null -ne $receiver) {
        $rvEndpoint=$primary.Name+'.frontier-primary-v1.files-v1.diagnostic-v1'
        $rvMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($rvEndpoint); $rvView=$rvMap.CreateViewAccessor()
        foreach($part in @('offered','adopted')) {$rvSignals[$part]=[Threading.EventWaitHandle]::OpenExisting($rvEndpoint+'.'+$part)}
        if($rv -ceq 'copied') {
            $copy=$parentClone.Invoke($receiver,$null)
            if($copy.TryAdoptWhenOfferedAsync().GetAwaiter().GetResult()) {throw 'Copied receiver acquired wait'}
            if($polling -and $copy.TryAdoptIfOffered()) {throw 'Copied receiver acquired polling adoption'}
            $copy.Dispose(); if(-not $receiver.IsCurrent) {throw 'Copied receiver retired original'}
        }
        if($rv -ceq 'parent-budget') {
            $watch=$receiver.GetType().GetField('work',$parentFlags).GetValue($receiver)
            $elapsed=@($watch.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
    }
    if($rv -cin @('offer-first','payload-tamper','forged-adoption','parent-exit')) {
        if(-not $signals.attached.WaitOne(30000)) {throw 'Rendezvous offered fixture deadline'}
        if($rv -ceq 'payload-tamper') {$rvView.Write(160L,[byte]($rvView.ReadByte(160L) -bxor 1))}
    }
    if($rv -ceq 'parent-exit') {
        if($nativeProcess.WaitForExit(250)) {throw 'Worker did not wait for real parent adoption'}
        [Console]::Out.WriteLine('frontier-diagnostic-rendezvous='+$prepareOwner+'/'+$rv); [Console]::Out.Flush()
        # Only this isolated fixture process exits; no managed cleanup signals.
        [Environment]::Exit(0)
    }
    $idleClock=if($null -ne $receiver) {$receiver.GetType().GetField('work',$parentFlags).GetValue($receiver)} else {$null}
    $idleTicks=if($null -ne $idleClock) {$idleClock.ElapsedTicks} else {0}
    $wall=[Diagnostics.Stopwatch]::StartNew()
    if(-not $polling -and $null -ne $receiver -and $rv -cnotin @('forged-adoption','adoption-timeout')) {$adoptionTask=$receiver.TryAdoptWhenOfferedAsync()}
    if(-not $polling -and $rv -ceq 'repeat-wait' -and $receiver.TryAdoptWhenOfferedAsync().GetAwaiter().GetResult()) {throw 'Receiver wait replay accepted'}
    if($rv -ceq 'parent-first') {
        if($polling) {
            for($probe=0;$probe -lt 25;$probe++) {if($receiver.TryAdoptIfOffered()) {throw 'Absent offer acquired adoption'}}
        } elseif($adoptionTask.Wait(250) -or $receiver.TryAdoptIfOffered()) {throw 'Idle adoption wait finished or polling acquired a pending asynchronous adoption'}
        if($idleClock.IsRunning -or $idleClock.ElapsedTicks -ne $idleTicks -or
            $receiver.GetType().GetField('adoptionAttempted',$parentFlags).GetValue($receiver)) {throw 'Idle wait consumed early adoption/work budget'}
        if($polling -and $receiver.GetType().GetField('adoptionWaitAttempted',$parentFlags).GetValue($receiver)) {throw 'Polling started the short offer wait'}
        [void]$signals.begin.Set()
    }
    if($rv -cin @('forged-offer','request-cancel','forged-adoption')) {
        if(-not $signals.attached.WaitOne(30000)) {throw 'Rendezvous fault fixture deadline'}
        switch($rv) {
            'forged-offer' {[void]$rvSignals.offered.Set()}
            'forged-adoption' {[void]$rvSignals.adopted.Set()}
            'request-cancel' {
                $rvClosed=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
                try {[void]$rvClosed.Set(); [void]$rvSignals.offered.Set()} finally {$rvClosed.Dispose()}
            }
        }
    }
    if($polling) {
        $pollDeadline=[DateTimeOffset]::UtcNow.AddSeconds(30); $adopted=$false
        do {
            $adopted=$receiver.TryAdoptIfOffered()
            if($adopted -or $receiver.GetType().GetField('adoptionAttempted',$parentFlags).GetValue($receiver) -or
                $receiver.GetType().GetField('closed',$parentFlags).GetValue($receiver) -or $nativeProcess.HasExited) {break}
            Start-Sleep -Milliseconds 25
        } while([DateTimeOffset]::UtcNow -lt $pollDeadline)
        if($adopted -ne $rvSuccess) {throw ('Polling adoption mismatch: '+$rv)}
        if($adopted -and ($receiver.TryAdoptIfOffered() -or $receiver.TryAdoptWhenOfferedAsync().GetAwaiter().GetResult() -or -not $receiver.IsCurrent)) {throw 'Polling replay acquired/retired original adoption'}
    }
    elseif($null -ne $adoptionTask) {
        if(-not $adoptionTask.Wait(30000)) {throw 'Native adoption task exceeded fixture deadline'}
        $adopted=$adoptionTask.GetAwaiter().GetResult()
        $adoptExpected=$rvSuccess -or $rv -cin @('cancel-before-commit','mutation-before-commit','worker-budget')
        if($adopted -ne $adoptExpected) {throw ('Rendezvous adoption mismatch: '+$rv)}
    }
    $wall.Stop()
    if($rv -ceq 'offer-timeout' -and ($wall.ElapsedMilliseconds -lt 19000 -or $wall.ElapsedMilliseconds -gt 30000 -or $idleClock.ElapsedTicks -ne $idleTicks)) {throw 'Idle offer deadline changed/consumed active work'}
    if($rv -ceq 'cancel-before-commit') {
        $rvClosed=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
        try {[void]$rvClosed.Set(); [void]$rvSignals.adopted.Set()} finally {$rvClosed.Dispose()}
    }
    if($rv -cin @('forged-offer','request-cancel','offer-timeout','cancel-before-commit','mutation-before-commit','worker-budget')) {[void]$signals.release.Set()}
    if(-not $nativeProcess.WaitForExit(35000)) {throw 'Rendezvous worker exit deadline'}
    $rvOutput=$reportRun.Stdout.GetAwaiter().GetResult(); $rvError=$reportRun.Stderr.GetAwaiter().GetResult()
    if($nativeProcess.ExitCode -ne 0 -or -not $rvOutput -or -not $rvError) {throw ('Rendezvous lost ordinary reports: '+$rvOutput+' '+$rvError)}
    $rows=@($rvOutput -split '\r?\n' | Where-Object {$_.StartsWith('frontier-rendezvous-worker=')})
    if($rows.Count -ne 1) {throw 'Rendezvous worker diagnostic missing'}
    $row=$rows[0].Substring('frontier-rendezvous-worker='.Length) | ConvertFrom-Json
    if($row.Committed -ne $rvSuccess -or $row.Evaluations -ne 2 -or -not $row.Unchanged) {throw 'Rendezvous changed original analysis/commit'}
    if(-not $rvSuccess) {
        if($null -ne $receiver -and $null -ne $receiver.TakeAfterExit()) {throw 'Rejected rendezvous acquired transport'}
        if($null -ne $primaryOutput.TryJoinDiagnostic($null)) {throw 'Rejected rendezvous joined output without transport'}
        if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{-not $inputs.IsCurrent},5000) -or
            @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Rejected rendezvous leaked primary inputs'}
        $rendezvousFinished=$true
    }
    'frontier-diagnostic-rendezvous='+$prepareOwner+'/'+$rv
} finally {
    if($null -ne $rvView) {$rvView.Dispose()}; if($null -ne $rvMap) {$rvMap.Dispose()}
    foreach($event in $rvSignals.Values) {$event.Dispose()}
}
