param([string]$RepoRoot,[string]$Directory,[string]$Case,[int]$Ordinal)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'src/nLink.Core/bin/Debug/net8.0/nLink.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/bin/Debug/net8.0/NLink.SmokeTests.Core.dll'))
$lease=$null; $fixture=$null; $events=@{}; $parent=$null
$result=@{FilesReady=$false;Attached=$false;Acknowledged=$false;Completed=$false;ParentExited=$false;Elapsed=0}
try {
    $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttachReadyFromEnvironment('frontier-gui-run','frontier-gui-scenario')
    if($null -eq $lease) {throw 'Core app attachment unavailable'}
    foreach($part in @('files-wait','attached','submitted','finished')) {$events[$part]=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_APP_FRONTIER_REQUEST+'.test.rendezvous.'+$part)}
    $flags=[Reflection.BindingFlags]'Instance,NonPublic'
    $parent=[Diagnostics.Process]::GetProcessById($lease.GetType().GetField('parentId',$flags).GetValue($lease)); [void]$parent.Handle
    [void]$events['files-wait'].Set(); $clock=[Diagnostics.Stopwatch]::StartNew()
    $result.FilesReady=[NLink.SmokeTests.FrontierFileRendezvousTests]::WaitForFiles($lease,($Case -ceq 'copied-waits'),($Case -ceq 'repeat-waits'))
    $result.Elapsed=$clock.ElapsedMilliseconds
    if(-not $result.FilesReady) {
        if($Case -ceq 'timeout-channel' -and $Ordinal -eq 0 -and ($result.Elapsed -lt 19500 -or $result.Elapsed -gt 27000)) {throw 'File readiness deadline changed'}
        if($lease.IsCurrent) {throw 'Failed wait left request current'}
    } else {
        $fixture=[NLink.SmokeTests.FrontierFileHandoffFixture]::Create($lease,$Directory,$Ordinal,1)
        $result.Attached=$fixture.Attached
        if(-not $fixture.Attached) {
            if($Ordinal -ne 0 -or $Case -cnotin @('forged-ready','bad-channel-scope')) {throw 'Native channel attach failed'}
            if($fixture.RetryAttach()) {throw 'Ready signal reopened failed one-shot attachment'}
        } else {
            [void]$events.attached.Set()
            if(-not $fixture.Submit()) {throw 'Original app submission failed'}
            [void]$events.submitted.Set(); $clock.Restart()
            $variant=if($Ordinal -eq 0) {$Case} else {''}
            $adopted=[NLink.SmokeTests.FrontierFileRendezvousTests]::WaitForAdoption($fixture,$variant)
            $result.Elapsed=$clock.ElapsedMilliseconds
            if($Case -ceq 'timeout-adoption' -and $Ordinal -eq 0 -and ($result.Elapsed -lt 19500 -or $result.Elapsed -gt 27000)) {throw 'Adoption deadline changed'}
            if($adopted) {
                $result.Acknowledged=$fixture.TakeAck(); if(-not $result.Acknowledged) {throw 'Original acknowledgment failed'}
                $result.Completed=$lease.CompleteAttachment(); if(-not $result.Completed) {throw 'Original app completion failed'}
            } elseif($fixture.TakeAck() -or $fixture.IsCurrent) {throw 'Failed adoption wait recovered current custody'}
        }
    }
    $result.ParentExited=$parent.HasExited
    [IO.File]::WriteAllText((Join-Path $Directory ('app-'+$Ordinal+'.json')),($result | ConvertTo-Json -Compress))
    [void]$events.finished.Set()
} finally {
    if($null -ne $fixture) {$fixture.Dispose()}; if($null -ne $lease) {$lease.Dispose()}; if($null -ne $parent) {$parent.Dispose()}
    foreach($event in $events.Values) {$event.Dispose()}
}
