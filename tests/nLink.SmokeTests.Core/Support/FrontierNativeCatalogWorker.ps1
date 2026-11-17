param([string]$AssemblyPath,[string]$Name,[guid]$RequestId,[string]$Case,[string]$ResultPath,[string]$Mode='catalog')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$ready=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.ready')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$lease=$null
try {
    if(-not $begin.WaitOne(15000)) { throw 'Native binding timeout' }
    [void][Reflection.Assembly]::LoadFrom($AssemblyPath)
    $core=[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $AssemblyPath) 'nLink.Core.dll'))
    if([NLink.Tools.LocalFrontierAppRequest].Assembly -ne $core) { throw 'Worker did not load the shared Core native codec' }
    $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,'frontier-catalog-native','frontier-catalog-native')
    if($null -eq $lease) { throw 'Native app attachment failed' }
    if($Mode -ceq 'payload') {
        $result=[NLink.SmokeTests.SessionFileTransferV4SenderTests]::RunNativeFrontierMarkerFixtureAsync($lease,$Case,$ready,$release,$true,$true).GetAwaiter().GetResult()
    } elseif($Mode -ceq 'source') {
        $result=[NLink.SmokeTests.SessionFileTransferV4SenderTests]::RunNativeFrontierMarkerFixtureAsync($lease,$Case,$ready,$release,$true).GetAwaiter().GetResult()
    } elseif($Mode -ceq 'marker') {
        $result=[NLink.SmokeTests.SessionFileTransferV4SenderTests]::RunNativeFrontierMarkerFixtureAsync($lease,$Case,$ready,$release).GetAwaiter().GetResult()
    } elseif($Mode -ceq 'writer') {
        $result=[NLink.SmokeTests.SessionFileTransferV4SenderTests]::RunNativeFrontierWriterFixtureAsync($lease,$Case,$ready,$release).GetAwaiter().GetResult()
    } else {
        $result=[NLink.SmokeTests.SessionFileTransferV4SenderTests]::RunNativeFrontierCatalogFixtureAsync($lease,$Case,$ready,$release).GetAwaiter().GetResult()
    }
    $completed=$lease.CompleteAttachment()
    $expected=$Case -cin @('valid_v6','valid_v4','foreign_claim')
    if($Mode -ceq 'writer') { $expected=$Case -cin @('valid_v6','valid_v4','writer_failure','wrong_run','late_bind','catalog_cancel','deferred_write','receipt_capacity') }
    if($Mode -cin @('marker','source','payload')) { $expected=-not ($Case.StartsWith('cancel_') -or $Case -cin @('metadata_after_drain','lease_disposed','parent_death','attachment_completed')) }
    if($completed -ne $expected) { throw 'Unexpected native attachment completion' }
    $json=$result | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($ResultPath,$json)
    if($Case -cne 'parent_death') { 'frontier-native-catalog-worker='+$json }
} finally {
    if($null -ne $lease) { $lease.Dispose() }
    $begin.Dispose(); $ready.Dispose(); $release.Dispose()
}
