$startCase=$prepareCase.Substring('start-'.Length)
$resources=$inputs.GetType().GetField('pins',$parentFlags).GetValue($inputs)
$primaryPins=@($resources.GetType().GetField('Handles',$parentFlags).GetValue($resources))
$open=$inputs.GetType().GetMethod('TryOpenRead',$parentFlags)
if($startCase.StartsWith('analyzer-')) {
    $analyzerCase=$startCase.Substring('analyzer-'.Length)
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAnalyzerParent.ps1')
    return
}
if($startCase -ceq 'auto-terminal' -or $startCase.StartsWith('rendezvous-')) {
    $readyLaunch=$true; $primaryCase='worker-join-pipes-terminal-reports-persist-valid'
    $rendezvousCase=''; $rendezvousCollision=$null
    try {
        if($startCase.StartsWith('rendezvous-')) {
            $rendezvousCase=$startCase.Substring('rendezvous-'.Length)
            $rendezvousPolling=$rendezvousCase.StartsWith('poll-')
            if($rendezvousPolling) {$rendezvousCase=$rendezvousCase.Substring('poll-'.Length)}
            if($rendezvousCase.StartsWith('collision-')) {
                $rendezvousCollision=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
                    $primary.Name+'.frontier-primary-v1.files-v1.diagnostic-v1.'+$rendezvousCase.Substring('collision-'.Length))
            }
        }
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWorkerParent.ps1')
        if($null -ne $rendezvousCollision -and $rendezvousCollision.WaitOne(0)) {throw 'Diagnostic collision mutated foreign signal'}
    } finally {if($null -ne $rendezvousCollision) {$rendezvousCollision.Dispose()}}
    'frontier-primary-start='+$prepareOwner+'/'+$startCase; return
}
$nativeProcess=[Diagnostics.Process]::new(); $signals=@{}; $foreignEvent=$null; $run=$null
try {
    foreach($part in @('entered','finished')) {$signals[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.test.ready.'+$part)}
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryStartWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-SourcePath',$source,'-Case',$startCase,'-Directory',$Directory)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '; $nativeProcess.StartInfo=$start
    $startedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    if($startCase -ceq 'guards') {
        foreach($property in @('RedirectStandardOutput','RedirectStandardError')) {
            $start.$property=$false; $rejected=$false
            try {[void]$sourcePreparation.StartWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
            if(-not $rejected -or $null -ne $nativeProcess.Id) {throw 'Invalid redirects started worker'}
            $start.$property=$true
        }
        $copy=$parentClone.Invoke($sourcePreparation,$null); $rejected=$false
        try {[void]$copy.StartWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
        $copy.Dispose()
        if(-not $rejected -or $null -ne $nativeProcess.Id -or -not $sourcePreparation.IsCurrent) {throw 'Copied preparation consumed original launch'}
        # The outer fixture's completed GUI worker is already associated and
        # was launched with real redirects; do not replace a running StartInfo.
        $rejection=''
        try {[void]$sourcePreparation.StartWorkerWithReports($process,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejection=$_.Exception.Message}
        if($rejection -notmatch 'frontier_primary_already_started' -or $null -ne $nativeProcess.Id -or -not $sourcePreparation.IsCurrent) {throw 'Already-started process consumed original launch'}
    }
    $collision=Join-Path $Directory ('frontier-primary-output-'+$primary.RequestId.ToString('N')+'-stdout.log')
    if($startCase -ceq 'capture-failure') {[IO.File]::WriteAllText($collision,'preserve-capture-collision')}
    if($startCase -ceq 'receiver-failure') {
        $foreignEvent=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($primary.Name+'.frontier-primary-v1.files-v1.diagnostic-v1',160)
    }
    if($startCase -ceq 'bind-failure') {$primary.GetType().GetField('gate',$parentFlags).GetValue($primary).Dispose()}
    if($startCase -ceq 'handoff-failure') {if($null -eq $open.Invoke($inputs,@(0))) {throw 'Preopened source fixture failed'}}
    if($startCase.StartsWith('collision-')) {
        $foreignEvent=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.frontier-start-v1.'+$startCase.Substring('collision-'.Length))
    }
    if($startCase -ceq 'start-failure') {$start.FileName=Join-Path $Directory 'missing-readiness-worker.exe'}
    $manual=$startCase -cin @('delayed','forged-ready','both-signals','cancel','timeout','wrong-parent','cancelled-ready','parent-exit')
    $failure=''; $readySignal=$null; $failedSignal=$null
    if($manual) {
        # Delay/forge only the rendezvous, never the real binding or handoff.
        $type=$primary.GetType().GetNestedType('FrontierStartSignal',[Reflection.BindingFlags]'NonPublic')
        $signal=$type.GetMethod('Create',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@($primary.Name))
        if($null -eq $signal) {throw 'Readiness fixture signals unavailable'}
        $primary.GetType().GetField('frontierStart',$parentFlags).SetValue($primary,$signal)
        $readySignal=$type.GetField('Ready',$parentFlags).GetValue($signal); $failedSignal=$type.GetField('Failed',$parentFlags).GetValue($signal)
        $start.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$primary.Name
        $start.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$primary.RequestId.ToString('D')
        $start.EnvironmentVariables['NLINK_RETAINED_FRONTIER_START_V1']='1'
        $run=$primary.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)
        if(-not $run.SourceBound) {throw 'Delayed readiness worker not bound'}
        if($startCase -ceq 'wrong-parent') {
            $startMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($primary.Name); $startView=$startMap.CreateViewAccessor()
            try {$startView.Write(8L,$startView.ReadInt64(8L)+1L)} finally {$startView.Dispose(); $startMap.Dispose()}
        }
        if(-not $signals.entered.WaitOne(20000)) {throw 'Worker readiness entry deadline'}
        if($startCase -cne 'wrong-parent' -and $signals.finished.WaitOne(250)) {throw 'Worker did not wait for native readiness'}
        switch($startCase) {
            'delayed' {if(-not $inputs.TryPrepareWorkerHandoff()) {throw 'Delayed handoff failed'}; [void]$readySignal.Set()}
            'forged-ready' {[void]$readySignal.Set()}
            'both-signals' {[void]$failedSignal.Set(); [void]$readySignal.Set()}
            'cancel' {$primary.Dispose()}
            'cancelled-ready' {
                $closedSignal=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
                try {[void]$closedSignal.Set(); [void]$readySignal.Set()} finally {$closedSignal.Dispose()}
            }
            'parent-exit' {
                # Exit this fixture process without managed cleanup/signalling:
                # only its actual native process exit may wake the waiting child.
                [Console]::Out.WriteLine('frontier-primary-start='+$prepareOwner+'/'+$startCase); [Console]::Out.Flush()
                [Environment]::Exit(0)
            }
        }
    } else {
        try {$run=$sourcePreparation.StartWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$failure=$_.Exception.Message}
    }
    if($startCase -ceq 'start-failure' -or $startCase.StartsWith('collision-')) {
        if(-not $failure -or $null -ne $run -or $null -ne $nativeProcess.Id) {throw 'Readiness setup/start failure hidden or started worker'}
        if($startCase -ceq 'start-failure') {
            $failedSignal=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.frontier-start-v1.failed')
            try {if(-not $failedSignal.WaitOne(0)) {throw 'Start failure did not publish failure'}} finally {$failedSignal.Dispose()}
        }
        $sourcePreparation.Dispose()
        if($null -ne $foreignEvent -and $foreignEvent.WaitOne(0)) {throw 'Readiness collision mutated foreign signal'}
    } else {
        if($failure -or $null -eq $run) {throw ('Readiness lost original report tasks: '+$failure)}
        if(-not $manual) {
            $expectedReady=$startCase -cnotin @('bind-failure','handoff-failure')
            if($run.SourceBound -ne ($startCase -cne 'bind-failure') -or $run.NativeHandoffReady -ne $expectedReady -or
                ($null -ne $run.DiagnosticReceiver) -ne ($expectedReady -and $startCase -cnotin @('capture-failure','receiver-failure'))) {throw 'Native readiness publication mismatch'}
        }
        if($startCase -ceq 'replay') {
            $rejected=$false
            try {[void]$sourcePreparation.StartWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$startedUtc)} catch {$rejected=$true}
            if(-not $rejected -or -not $run.NativeHandoffReady) {throw 'Repeated launch changed original readiness'}
        }
        if(-not $nativeProcess.WaitForExit(35000)) {throw 'Readiness worker exit deadline'}
        $output=$run.Stdout.GetAwaiter().GetResult(); $errorOutput=$run.Stderr.GetAwaiter().GetResult()
        if($nativeProcess.ExitCode -ne 0 -or $errorOutput -cne 'ordinary readiness stderr') {throw ('Readiness worker/report failed: '+$output+' '+$errorOutput)}
        $rows=@($output -split '\r?\n' | Where-Object {$_.StartsWith('frontier-start-worker=')})
        if($rows.Count -ne 1) {throw ('Readiness worker count/report mismatch: '+$output)}
        $row=$rows[0].Substring('frontier-start-worker='.Length) | ConvertFrom-Json
        $attached=$startCase -cin @('auto','delayed','capture-failure','receiver-failure','guards','replay','legacy-selector')
        $native=$attached -and $startCase -cne 'legacy-selector'
        if($row.Attached -ne $attached -or $row.Native -ne $native -or $row.Complete -ne $native -or
            (-not $attached -and -not $row.RetryRejected)) {throw ('Readiness replaced attachment authority: '+$rows[0])}
        if($native -and ($row.Lines -ne $sourcePreparation.InputLineCount -or $row.Hash -cne $sourcePreparation.InputSha256)) {throw 'Ready source differs from original prepared source'}
        if($startCase -ceq 'replay' -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{$run.DiagnosticOutput.OutputsDrainedSuccessfully},5000)) {throw 'Rejected launch replay revoked original diagnostic output'}
        if($startCase -ceq 'timeout' -and ($row.Milliseconds -lt 19000 -or $row.Milliseconds -gt 30000)) {throw 'Readiness wait did not obey its bounded deadline'}
        if($startCase -ceq 'capture-failure' -and ($null -ne $run.DiagnosticOutput -or [IO.File]::ReadAllText($collision) -cne 'preserve-capture-collision')) {throw 'Capture failure changed ordinary launch/collision'}
        $output.Trim()
    }
    'frontier-primary-start='+$prepareOwner+'/'+$startCase
} finally {
    $sourcePreparation.Dispose()
    try {if($null -ne $nativeProcess.Id -and -not $nativeProcess.WaitForExit(5000)) {$nativeProcess.Kill(); [void]$nativeProcess.WaitForExit(5000)}}
    catch [InvalidOperationException] {} finally {$nativeProcess.Dispose()}
    if($null -ne $foreignEvent) {$foreignEvent.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
