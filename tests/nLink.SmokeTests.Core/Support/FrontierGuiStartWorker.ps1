param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$name=$env:NLINK_GUI_WRITER_REQUEST; $id=[guid]$env:NLINK_GUI_WRITER_REQUEST_ID
$entered=[Threading.EventWaitHandle]::OpenExisting($name+'.test.gui-start.entered')
$finished=[Threading.EventWaitHandle]::OpenExisting($name+'.test.gui-start.finished')
$release=[Threading.EventWaitHandle]::OpenExisting($name+'.test.gui-start.release')
$owner=$null; $finalizer=$null; $watch=[Diagnostics.Stopwatch]::StartNew()
try {
    [void]$entered.Set()
    if($env:NLINK_GUI_FRONTIER_START_V1 -ceq '1') {
        $run=if($Case -ceq 'wrong-scope') {'wrong-run'} else {'gui-start-run'}
        $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($name,$id,$run,'gui-start-scenario',$Directory)
        if($null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($name,$id,'gui-start-run','gui-start-scenario',$Directory) -or
            $null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($name,$id,'gui-start-run','gui-start-scenario',$Directory)) {throw 'Readiness attachment allowed a retry'}
    } elseif($Case -cnotin @('collision-ready','collision-failed') -or -not [string]::IsNullOrEmpty($env:NLINK_GUI_FRONTIER_START_V1)) {throw 'Unexpected GUI readiness selector'}
    $watch.Stop()
    if($null -ne $owner) {
        if(-not $owner.IsCurrent -or $owner.GuiProcessId -ne $PID -or $owner.RequestId -ne $id -or $owner.LiveProofEligible -or
            $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'Native GUI identity/authority changed'}
        $clock=$owner.GetType().GetField('frontierWork',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($owner)
        if($clock.ElapsedTicks -ne 0) {throw 'Readiness idle wait charged native ledger work'}
        $origin=$owner.TryCreateLedgerOrigin()
        if($null -eq $origin -or -not $origin.IsCurrent -or -not $origin.WasCreatedEmpty) {throw 'Native launch cannot create original ledger before apps'}
    }
    $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($name,$id,'gui-start-run','gui-start-scenario',$Directory)
    [IO.File]::WriteAllText((Join-Path $Directory 'gui-start-result.json'),(@{attached=$null -ne $owner;legacyFinalizer=$null -ne $finalizer;waitMilliseconds=$watch.ElapsedMilliseconds} | ConvertTo-Json -Compress))
    [void]$finished.Set()
    if(-not $release.WaitOne(20000)) {throw 'GUI test release deadline'}
    [Console]::Out.WriteLine('ordinary GUI stdout'); [Console]::Error.Write('ordinary GUI stderr')
} finally {
    if($null -ne $finalizer) {$finalizer.Dispose()}; if($null -ne $owner) {$owner.Dispose()}
    $entered.Dispose(); $finished.Dispose(); $release.Dispose()
}
