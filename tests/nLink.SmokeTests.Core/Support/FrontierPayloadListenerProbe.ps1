param([ValidateSet('Prepare','Start','AfterFinally','Gui')][string]$Stage)
$listenerCase=$env:NLINK_TEST_PAYLOAD_WRAPPER_CASE.Substring(9)
$listenerFlags=[Reflection.BindingFlags]'Instance,NonPublic'
function Get-ListenerField($Instance,[string]$Name) {$Instance.GetType().GetField($Name,$listenerFlags).GetValue($Instance)}
function Assert-ListenerRetired {
    $task=Get-ListenerField $script:payloadWrapper 'adoption'
    if($null -ne $task -and (-not $task.Wait(5000) -or $task.GetAwaiter().GetResult())) {throw 'Listener adoption did not finish unsuccessfully'}
    if($null -eq $task -and $listenerCase -cne 'startup-failure') {throw 'Listener adoption task missing'}
    if(-not (Get-ListenerField $script:payloadWrapper 'retired') -or $script:payloadWrapper.IsCurrent -or
        $script:payloadWrapper.ProvisionalCustody -or (Get-ListenerField $script:payloadWrapper 'committed') -or
        (Get-ListenerField $script:payloadWrapper 'taken')) {throw 'No-offer listener retained custody'}
    $pipe=Get-ListenerField $script:payloadWrapper 'pipe'
    if($null -ne $pipe -and -not $script:listenerPipeHandle.IsClosed) {throw 'Listener endpoint handle remained open'}
    foreach($file in (Get-ListenerField $script:payloadWrapper 'files')) {
        if($null -ne $file -and -not $file.IsClosed) {throw 'No-offer listener retained payload handle'}
    }
    if($script:listenerWork.IsRunning -or $script:listenerWork.ElapsedTicks -ne $script:listenerWorkTicks) {throw 'Listener wait consumed parent work'}
}
if($Stage -ceq 'Gui') {
    $prefix='Local\nlink-listener-test-'+$env:NLINK_GUI_WRITER_REQUEST_ID
    $ready=$null; $go=$null; $finish=$null; $connected=$null; $client=$null
    try {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while($null -eq $ready -and $clock.ElapsedMilliseconds -lt 10000) {
            try {$ready=[Threading.EventWaitHandle]::OpenExisting($prefix+'-ready')} catch {Start-Sleep -Milliseconds 10}
        }
        if($null -eq $ready -or -not $owner.IsCurrent) {throw 'Original listener GUI owner missing'}
        $go=[Threading.EventWaitHandle]::OpenExisting($prefix+'-go')
        $finish=[Threading.EventWaitHandle]::OpenExisting($prefix+'-finish')
        $connected=[Threading.EventWaitHandle]::OpenExisting($prefix+'-connected')
        [void]$ready.Set()
        if(-not $go.WaitOne(30000)) {throw 'Listener GUI control was not released'}
        if($listenerCase -cin @('stall-packet','cancel-connect') -or $listenerCase.StartsWith('connected-')) {
            $client=[IO.Pipes.NamedPipeClientStream]::new('.',('nlink-payload-wrapper-'+([guid]$env:NLINK_GUI_WRITER_REQUEST_ID).ToString('N')),
                [IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
            try {$client.Connect(1000)} catch {if($listenerCase -cne 'cancel-connect') {throw}}
            if($client.IsConnected) {[void]$connected.Set()}
            if($client.IsConnected -and $listenerCase -cne 'cancel-connect') {
                $buffer=[byte[]]::new(1)
                $read=$client.ReadAsync($buffer,0,1)
                if(-not $read.Wait(15000) -or $read.GetAwaiter().GetResult() -ne 0) {throw 'Stalled listener peer was not disconnected'}
            }
        }
        'listener-original-gui-ready='+$listenerCase
        if($listenerCase -notin @('gui-normal','gui-nonzero') -and -not $finish.WaitOne(30000)) {throw 'Listener GUI cleanup was not released'}
    } finally {
        foreach($item in @($client,$ready,$go,$finish,$connected)) {if($null -ne $item) {$item.Dispose()}}
        $owner.Dispose()
    }
    return
}
if($Stage -ceq 'Prepare') {
    $script:listenerRequest=$localWriterRequest; $script:listenerProcess=[Diagnostics.Process]::GetProcessById($process.Id)
    $script:listenerEvents=@(); $script:listenerCollision=$null
    $prefix='Local\nlink-listener-test-'+$localWriterRequest.RequestId
    foreach($suffix in @('ready','go','finish','connected')) {
        $script:listenerEvents+=,[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$prefix+'-'+$suffix)
    }
    if($listenerCase -ceq 'startup-failure') {
        $script:listenerCollision=[IO.Pipes.NamedPipeServerStream]::new('nlink-payload-wrapper-'+$localWriterRequest.RequestId.ToString('N'),[IO.Pipes.PipeDirection]::InOut,1)
    }
    return
}
if($Stage -ceq 'Start') {
    $script:listenerWork=Get-ListenerField $script:payloadWrapper 'work'
    $script:listenerWorkTicks=$script:listenerWork.ElapsedTicks
    $pipe=Get-ListenerField $script:payloadWrapper 'pipe'
    $script:listenerPipeHandle=if($null -ne $pipe) {$pipe.SafePipeHandle} else {$null}
    if(-not $script:listenerEvents[0].WaitOne(15000)) {throw 'Listener original GUI did not become ready'}
    if($listenerCase -cne 'startup-failure' -and $script:payloadWrapper.AdoptionFinished) {throw 'Listener retired before its control'}
    if($listenerCase.StartsWith('connected-')) {
        [void]$script:listenerEvents[1].Set()
        if(-not $script:listenerEvents[3].WaitOne(5000) -or -not $pipe.IsConnected) {throw 'Connected cancellation peer missing'}
    }
    switch($listenerCase) {
        'request-close' {[void](Get-ListenerField $localWriterRequest 'closed').Set()}
        'request-dispose' {$localWriterRequest.Dispose()}
        'adopter-dispose' {$script:payloadWrapper.Dispose()}
        'gui-normal' {[void]$script:listenerEvents[1].Set(); if(-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) {throw 'Normal GUI exit missing'}}
        'gui-nonzero' {[void]$script:listenerEvents[1].Set(); if(-not $process.WaitForExit(10000) -or $process.ExitCode -ne 7) {throw 'Nonzero GUI exit missing'}}
        'gui-killed' {$process.Kill(); if(-not $process.WaitForExit(5000) -or $process.ExitCode -eq 0) {throw 'Killed GUI exit missing'}}
        'startup-failure' {$script:listenerCollision.Dispose()}
        'cancel-connect' {[void]$script:listenerEvents[1].Set(); [void](Get-ListenerField $localWriterRequest 'closed').Set()}
        'connected-request-dispose' {$localWriterRequest.Dispose()}
        'connected-adopter-dispose' {$script:payloadWrapper.Dispose()}
        'connected-deadline-dispose' {Start-Sleep -Milliseconds 10000; $script:payloadWrapper.Dispose()}
        'stall-packet' {
            $clock=[Diagnostics.Stopwatch]::StartNew(); [void]$script:listenerEvents[1].Set()
            $task=Get-ListenerField $script:payloadWrapper 'adoption'
            if(-not $task.Wait(15000) -or $clock.ElapsedMilliseconds -lt 9500) {throw 'Connected exchange deadline changed'}
            Write-Host ('listener-connected-deadline-ms='+$clock.ElapsedMilliseconds)
        }
        'wrong-pid' {
            $client=[IO.Pipes.NamedPipeClientStream]::new('.',('nlink-payload-wrapper-'+$localWriterRequest.RequestId.ToString('N')),[IO.Pipes.PipeDirection]::InOut)
            try {$client.Connect(1000); Assert-ListenerRetired} finally {$client.Dispose()}
        }
        'wrapper-finally' {throw 'listener-control-unwind'}
        'watchdog-unwind' {Request-TunaGuiScenarioDeadline}
        default {throw 'Unknown listener control'}
    }
    Assert-ListenerRetired
    Write-Host ('listener-pre-finally-retired='+$listenerCase)
    throw 'listener-control-unwind'
}
try {
    if($failure -cne $(if($listenerCase -ceq 'watchdog-unwind') {'Unexpected attempt GUI deadline'} else {'listener-control-unwind'})) {
        throw ('Listener control failed before cleanup: '+$failure)
    }
    Assert-ListenerRetired
    if($script:payloadWrapper.TryTakeAfterExit($script:guiReports) -or
        $null -ne $script:listenerRequest.TryStartPayloadWrapper($script:guiReports)) {throw 'Retired listener revived'}
    $client=[IO.Pipes.NamedPipeClientStream]::new('.',('nlink-payload-wrapper-'+$script:listenerRequest.RequestId.ToString('N')),[IO.Pipes.PipeDirection]::InOut)
    try {
        try {$client.Connect(100)} catch [TimeoutException] { }
        if($client.IsConnected) {throw 'Retired listener still accepted connections'}
    } finally {$client.Dispose()}
    [void]$script:listenerEvents[1].Set(); [void]$script:listenerEvents[2].Set()
    if(-not $script:listenerProcess.HasExited -and -not $script:listenerProcess.WaitForExit(5000)) {throw 'Listener GUI did not exit during cleanup'}
    if(-not $script:guiReports.Stdout.Wait(5000) -or -not $script:guiReports.Stderr.Wait(5000)) {throw 'Original listener output reports not drained'}
    if($listenerCase -cne 'gui-killed' -and $script:guiReports.Stderr.GetAwaiter().GetResult()) {throw ('Listener GUI failed: '+$script:guiReports.Stderr.GetAwaiter().GetResult())}
    Write-Host ('listener-cleanup-checked='+$listenerCase)
} finally {
    if(-not $script:listenerProcess.HasExited) {$script:listenerProcess.Kill(); [void]$script:listenerProcess.WaitForExit(5000)}
    $script:listenerProcess.Dispose()
    foreach($item in $script:listenerEvents) {$item.Dispose()}
    if($null -ne $script:listenerCollision) {$script:listenerCollision.Dispose()}
}
