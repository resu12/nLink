param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='',
    [string]$CoreAssembly,[string]$FixtureAssembly,[switch]$App,[int]$Ordinal=0,[int]$PayloadCount=1)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$genuine=$Case.StartsWith('genuine_',[StringComparison]::Ordinal)
if($App) {
    $lease=$null; $fixture=$null; $held=$false; $mutex=$null; $before=''
    try {
        $command=[Console]::ReadLine()
        if($command -ceq 'exit') { exit 0 }
        if($command -cne 'attach') { throw 'Unexpected initial command' }
        if($genuine) {
            $env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH=Join-Path $Directory 'shared.log'
            $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR=$Directory
            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='frontier-gui-run'
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='frontier-gui-scenario'
        }
        [void][Reflection.Assembly]::LoadFrom($CoreAssembly)
        [void][Reflection.Assembly]::LoadFrom($FixtureAssembly)
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario')
        if($null -eq $lease) { throw 'Original Core app lease unavailable' }
        if($genuine) {
            $fixture=[NLink.SmokeTests.FrontierGenuineSubmissionFixture]::Create($lease,$Case.Substring(8),$Ordinal)
            [Console]::WriteLine($fixture.IsCurrent)
        } else {
            $fixture=[NLink.SmokeTests.FrontierFileHandoffFixture]::Create($lease,$Directory,$Ordinal,$PayloadCount)
            [Console]::WriteLine($fixture.Attached)
        }
        while($null -ne ($command=[Console]::ReadLine())) {
            switch -CaseSensitive ($command) {
                'prepare' {
                    $output=[Console]::Out
                    try { [Console]::SetOut([IO.TextWriter]::Null); [void]$fixture.PrepareAsync().GetAwaiter().GetResult() }
                    finally { [Console]::SetOut($output) }
                    [Console]::WriteLine($fixture.IsCurrent)
                }
                'late-write' { $fixture.LateWrite(); [Console]::WriteLine('written') }
                'copies' { $fixture.CheckCopies(); [Console]::WriteLine($fixture.IsCurrent) }
                'submit' {
                    if($genuine) {
                        $catalog=$fixture.GetType().GetField('catalog',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($fixture)
                        $reg=$catalog.GetType().GetField('writer',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($catalog)
                        $drain=$reg.GetType().GetField('Drain',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($reg)
                        $revoked=$reg.GetType().GetField('Revoked',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($reg)
                        $sequence=[NLink.Core.Logging.LocalOperationalLog].GetProperty('WriterSequenceForTests',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
                        $recent=([NLink.Core.Logging.LocalOperationalLog]::GetRecentLogText() -split "`n" | Select-Object -Last 4 | ForEach-Object {[regex]::Match($_,'event=[^;]+').Value}) -join ','
                        $before="revoked=$revoked drain=$($null -ne $drain) sequence=$sequence recent=$recent"
                    }
                    [Console]::WriteLine($fixture.Submit())
                }
                'failure' {
                    $catalog=$fixture.GetType().GetField('catalog',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($fixture)
                    [Console]::WriteLine("app=$Ordinal failure=$($catalog.FailureReason) before=$before")
                }
                'ack' { [Console]::WriteLine($fixture.TakeAck()) }
                'retry' { [Console]::WriteLine($fixture.RetryAttach()) }
                'bind-writer' { [Console]::WriteLine($fixture.TryBindWriter()) }
                'complete' { [Console]::WriteLine($lease.CompleteAttachment()) }
                'cancel' { $fixture.CancelCatalog(); [Console]::WriteLine('cancelled') }
                'cancel-race' { [Console]::WriteLine($fixture.CancelWhileAcknowledgmentChecksCatalog()) }
                'probe' { [Console]::WriteLine($fixture.FilesCurrent) }
                'gate' { $mutex=[Threading.Mutex]::OpenExisting($Name+'.files-v1.gate'); $held=$mutex.WaitOne(0); [Console]::WriteLine($held) }
                'ungate' { $mutex.ReleaseMutex(); $held=$false; [Console]::WriteLine('released') }
                'crash' { [Environment]::Exit(7) }
                'exit' { [Console]::WriteLine('exiting'); return }
                default { throw "Unknown app command: $command" }
            }
        }
    } finally {
        if($held) { $mutex.ReleaseMutex() }
        if($null -ne $mutex) { $mutex.Dispose() }
        if($null -ne $fixture) { $fixture.Dispose() }
        if($null -ne $lease) { $lease.Dispose() }
    }
    exit 0
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$owner=$null; $finalizer=$null
$requests=[Collections.Generic.List[object]]::new(); $apps=[Collections.Generic.List[object]]::new()
function Send($Entry,[string]$Command) {
    $Entry.Process.StandardInput.WriteLine($Command); $Entry.Process.StandardInput.Flush()
    $read=$Entry.Process.StandardOutput.ReadLineAsync()
    if(-not $read.Wait(10000)) { throw "App command timed out: $Command" }
    $answer=$read.GetAwaiter().GetResult()
    if($null -eq $answer) { throw ("App closed output for $Command`: "+$Entry.Err.GetAwaiter().GetResult()) }
    return $answer
}
function Expect($Entry,[string]$Command,[string]$Expected='True') {
    $actual=Send $Entry $Command
    if($actual -cne $Expected) {
        $detail=if($genuine -and $Command -ceq 'submit') { Send $Entry 'failure' } else {''}
        throw "Wrong $Command response: expected=$Expected actual=$actual case=$Case $detail"
    }
}
function Mutate([int]$Offset,[string]$Kind='byte') {
    $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($requests[0].Name+'.files-v1'); $view=$map.CreateViewAccessor()
    try {
        if($Kind -ceq 'state') { $view.Write([long]$Offset,4) }
        else { $view.Write([long]$Offset,[byte]($view.ReadByte($Offset) -bxor 1)) }
    } finally { $view.Dispose(); $map.Dispose() }
}
function Exit-App($Entry,[bool]$Crash=$false) {
    if(-not $Entry.Process.HasExited) {
        if($Crash) { $Entry.Process.StandardInput.WriteLine('crash'); $Entry.Process.StandardInput.Flush() }
        else { Expect $Entry 'exit' 'exiting' }
    }
    if(-not $Entry.Process.WaitForExit(10000)) { throw 'App exit deadline' }
    $expected=if($Crash) {7} else {0}
    if($Entry.Process.ExitCode -ne $expected) { throw ('App failed: '+$Entry.Err.GetAwaiter().GetResult()) }
}
try {
    if(-not $begin.WaitOne(20000)) { throw 'GUI bind deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner) { throw 'Original GUI launcher unavailable' }
    if(-not $genuine) { [IO.File]::WriteAllBytes((Join-Path $Directory 'shared.log'),[byte[]]::new(1024)) }
    for($i=0;$i -lt 2;$i++) {
        $request=$owner.TryCreateAppRequest(); if($null -eq $request) { throw 'Original native request unavailable' }; $requests.Add($request)
    }
    if($Case -ceq 'before-seal') {
        if($owner.TryEnableFileHandoff() -or $owner.IsCurrent) { throw 'Unsealed launch enabled channel' }
    } else {
        for($i=0;$i -lt 2;$i++) {
            $count=if($Case -ceq 'maximum') {16} elseif($i -eq 0) {1} else {0}
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=(Get-Command pwsh).Source; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
            $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-App','-Name',$requests[$i].Name,'-RequestId',$requests[$i].RequestId.ToString('D'),
                '-Directory',$Directory,'-CoreAssembly',$CoreAssembly,'-FixtureAssembly',$FixtureAssembly,'-Ordinal',"$i",'-PayloadCount',"$count",'-Case',$Case)
            $start.Arguments=($args | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
            $process=[Diagnostics.Process]::Start($start); [void]$process.Handle
            $entry=@{Process=$process;Err=$process.StandardError.ReadToEndAsync()}; $apps.Add($entry)
            if($null -eq $requests[$i].BindApp($process)) { throw 'Original app binding failed' }
        }
        if(-not $owner.SealLaunches()) { throw 'Launch seal failed' }
        $enabled=$Case -cnotin @('no-channel','attach-before-ready')
        if($enabled -and -not $owner.TryEnableFileHandoff()) { throw 'Original GUI channel registration failed' }
        foreach($entry in $apps) { Expect $entry 'attach' ($enabled.ToString()) }
        if(-not $enabled) { foreach($entry in $apps) { Expect $entry 'bind-writer' 'False' } }
        if($Case -ceq 'attach-before-ready') {
            if(-not $owner.TryEnableFileHandoff()) { throw 'Later channel registration failed' }
            foreach($entry in $apps) { Expect $entry 'retry' 'False' }
        }
        if($enabled) {
            if($genuine) { foreach($entry in $apps) { Expect $entry 'prepare' } }
            if($Case -cin @('copied','genuine_copied')) {
                foreach($entry in $apps) { Expect $entry 'copies' }
                $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($requests[0],$null)
                if($copy.TryAdoptFiles() -or $copy.FileSubmissionAvailable) { throw 'Copied GUI request used file channel' }
                $copy.Dispose(); if(-not $owner.IsCurrent) { throw 'Copied request revoked original' }
            }
            if($Case -ceq 'genuine_catalog_cancel') { Expect $apps[0] 'cancel' 'cancelled' }
            if($Case -ceq 'genuine_late_write') { Expect $apps[0] 'late-write' 'written' }
            if($Case -cin @('genuine_missing_marker','genuine_early_submit','genuine_payload_fault','genuine_catalog_cancel','genuine_late_write')) {
                Expect $apps[0] 'submit' 'False'
                if($requests[0].FileSubmissionAvailable) { throw 'Failed genuine issuer exposed a prefix' }
                if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Failed genuine issuer leaked a payload' }
            } else {
                foreach($entry in $apps) { Expect $entry 'submit' }
                if(-not $requests[0].FileSubmissionAvailable -or -not $requests[1].FileSubmissionAvailable) { throw 'Original submission readiness unavailable' }
            }
        }
    }
    [void]$attached.Set()
    if($Case -ceq 'parent-death') {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while($owner.IsCurrent -and $clock.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 20 }
        if($owner.IsCurrent -or @($apps | Where-Object {$_.Process.HasExited}).Count) { throw 'Parent death not isolated from runtime apps' }
        [IO.File]::WriteAllText($DeathResult,'{"ParentDeathRejected":true,"AppsStillRunning":true,"FilesRevoked":true}')
        return
    }
    if(-not $release.WaitOne(20000)) { throw 'GUI release deadline' }
    $positive=$Case -cin @('valid','maximum','copied','genuine_valid','genuine_copied','genuine_two_payloads','genuine_both_positive')
    switch -CaseSensitive ($Case) {
        'repeat-enable' { if($owner.TryEnableFileHandoff()) { throw 'File channel re-enabled' } }
        'attach-before-ready' { if($requests[0].TryAdoptFiles()) { throw 'Missing original attachment adopted' } }
        'premature-ack' { Expect $apps[0] 'ack' 'False' }
        'repeat-submit' { Expect $apps[0] 'submit' 'False' }
        'genuine_repeat_submit' { Expect $apps[0] 'submit' 'False' }
        'genuine_after_submit_cancel' { Expect $apps[0] 'cancel' 'cancelled' }
        'genuine_after_submit_write' { Expect $apps[0] 'late-write' 'written' }
        'early-complete' { Expect $apps[0] 'complete' }
        'catalog-cancel' { Expect $apps[0] 'cancel' 'cancelled' }
        'catalog-cancel-race' { Expect $apps[0] 'cancel-race' }
        'request-cancel' { $owner.Dispose() }
        'body-before' { Mutate 202 }
        'event-only' { $event=[Threading.EventWaitHandle]::OpenExisting($requests[0].Name+'.files-v1.adopted'); try {[void]$event.Set()} finally {$event.Dispose()}; Expect $apps[0] 'ack' 'False' }
        'gate-busy' { Expect $apps[0] 'gate' }
        'app-crash' { Exit-App $apps[0] $true }
        'budget' {
            $adopter=$owner.GetType().GetField('fileAdoption',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($owner)
            $work=$adopter.GetType().GetField('work',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($adopter)
            $elapsed=@($work.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
    }
    if($positive -or $Case -cin @('body-after','ack-nonce','repeat-adopt','repeat-ack','state-replay','nonzero','complete-after-adopt','genuine_after_complete_write')) {
        if(-not $requests[0].TryAdoptFiles()) { throw 'First original native adoption failed' }
        switch -CaseSensitive ($Case) {
            'body-after' { Mutate 202 }
            'complete-after-adopt' { Expect $apps[0] 'complete' }
            'genuine_after_complete_write' { Expect $apps[0] 'ack'; Expect $apps[0] 'complete'; Expect $apps[0] 'late-write' 'written' }
            'ack-nonce' { Mutate 144; Expect $apps[0] 'ack' 'False' }
            'repeat-adopt' { if($requests[0].TryAdoptFiles()) { throw 'Repeated adoption accepted' } }
            'repeat-ack' { Expect $apps[0] 'ack'; Expect $apps[0] 'ack' 'False' }
            'state-replay' { Expect $apps[0] 'ack'; [void]$owner.IsCurrent; Mutate 8 'state' }
            default {
                Expect $apps[0] 'ack'
                if(-not $requests[1].TryAdoptFiles()) { throw 'Second original native adoption failed' }
                Expect $apps[1] 'ack'
                if(-not $owner.AllFileHandoffsAcknowledged -or $owner.AllAppRequestsCompletedSuccessfully) { throw 'Bad acknowledgment/early completion gates' }
                foreach($entry in $apps) { Expect $entry 'complete' }
                Exit-App $apps[0] ($Case -ceq 'nonzero'); Exit-App $apps[1]
            }
        }
    } elseif($Case -cnotin @('before-seal','no-channel')) {
        if($requests[0].TryAdoptFiles()) { throw 'Rejected contribution adopted' }
    }
    if($Case -ceq 'gate-busy') { Expect $apps[0] 'ungate' 'released' }
    if($Case -ceq 'no-channel') {
        foreach($entry in $apps) { Expect $entry 'complete'; Exit-App $entry }
        if(-not $owner.AllAppRequestsCompletedSuccessfully -or $owner.AllFileHandoffsAcknowledged) { throw 'Missing optional endpoint broke original app completion' }
    } elseif($positive) {
        if(-not $owner.AllFileHandoffsAcknowledged -or -not $owner.AllAppRequestsCompletedSuccessfully) { throw 'Original handoff did not survive native app exits' }
        $expected=if($Case -ceq 'maximum') {32} elseif($Case -cin @('genuine_two_payloads','genuine_both_positive')) {2} else {1}
        if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count -ne $expected) { throw 'GUI did not retain native payloads' }
    } elseif($owner.IsCurrent -or $owner.AllFileHandoffsAcknowledged) { throw "Failed handoff retained ownership: $Case" }
    # The optional handoff must not consume the existing finalizer attach slot.
    if($Case -cne 'cancel-after') {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'Legacy finalizer affected by file handshake' }
    }
    'frontier-gui-worker='+(@{Case=$Case;Acknowledged=$positive;LiveProofEligible=$false;ProductionActivated=$false} | ConvertTo-Json -Compress)
} finally {
    if($null -ne $owner) { $owner.Dispose() }
    foreach($entry in $apps) {
        if(-not $entry.Process.HasExited) {
            $entry.Process.StandardInput.WriteLine('exit'); $entry.Process.StandardInput.Flush()
            if(-not $entry.Process.WaitForExit(5000)) { $entry.Process.Kill(); [void]$entry.Process.WaitForExit(5000) }
        }
        $entry.Process.Dispose()
    }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose()
}
if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Native payload handles leaked after cleanup' }
