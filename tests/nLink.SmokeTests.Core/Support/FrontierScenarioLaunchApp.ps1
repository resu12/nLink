param([string]$RepoRoot,[string]$Directory,[string]$Role,[string]$Signal,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$ready=[Threading.EventWaitHandle]::OpenExisting($Signal+'.'+$Role+'.ready')
$exit=[Threading.EventWaitHandle]::OpenExisting($Signal+'.'+$Role+'.exit')
$lease=$null
try {
    [IO.File]::AppendAllText((Join-Path $Directory 'launches.txt'),$Role+"`n")
    if($env:NLINK_APP_FRONTIER_START_V1 -ceq '1') {
        . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttachReadyFromEnvironment('scenario-launch-run','scenario-launch-scope')
        if($null -eq $lease -and $Case -cne ($Role+'-report-failure')) {throw 'Original delivered app request missing'}
        if($null -ne $lease) {[IO.File]::WriteAllText((Join-Path $Directory ($Role+'.attached')),$env:NLINK_APP_FRONTIER_REQUEST_ID)}
    }
    [IO.File]::AppendAllText((Join-Path $Directory 'live-operational-log/nlink.log'),$Role+"`n",[Text.UTF8Encoding]::new($false))
    [Console]::Out.WriteLine('ordinary-'+$Role); [Console]::Error.WriteLine('error-'+$Role)
    [void]$ready.Set()
    if(-not $exit.WaitOne(40000)) {throw 'Scenario app exit deadline'}
} finally {
    if($null -ne $lease) {$lease.Dispose()}
    $ready.Dispose(); $exit.Dispose()
}
