param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$config=Get-Content -LiteralPath (Join-Path $ArtifactDir 'parent-case.json') -Raw | ConvertFrom-Json
$RepoRoot=$config.repoRoot
$script:primaryPayloadChecked=$false; $script:primaryPayloadBound=$false; $script:transferEvaluation=$null; $script:workerPayloadHandles=@()
$script:primaryPayloadBindError=$null
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpoints.ps1') $ArtifactDir
    Write-FrontierPrimaryCheckpoint 'started' 'primary fixture started'
}
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$text=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParentWorker.ps1'))
$text=Replace-AttemptSeam $text ". (Join-Path `$RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')" ". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') `$RepoRoot"
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {
    $text=Replace-AttemptSeam $text "Write-Host ('frontier-primary-origin-batches; events='+`$events.Count+'; origins='+`$origins.Count+'; batches='+`$batchCount)" @'
$originDiagnostic='frontier-primary-origin-batches; events='+$events.Count+'; origins='+$origins.Count+'; batches='+$batchCount
Write-Host $originDiagnostic
Write-FrontierPrimaryCheckpoint 'source-origins' $originDiagnostic
'@
    $text=Replace-AttemptSeam $text 'if($null -ne $profile) {Write-Host $profile.Invoke($inputs,$null)}' @'
if($null -ne $profile) {
    $profileDiagnostic=$profile.Invoke($inputs,$null)
    Write-Host $profileDiagnostic
    Write-FrontierPrimaryCheckpoint 'source-profile' $profileDiagnostic
}
'@
}
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cin @('canonical-source','canonical-fallback')) {
    $text=Replace-AttemptSeam $text '$failure=$_.Exception.Message;' @'
$failure=$_.Exception.Message;
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'worker-error' $failure}
if($null -ne $script:observedSource) {
    $flags=[Reflection.BindingFlags]'Instance,NonPublic'
    $inputs=$script:observedSource.GetType().GetField('frontier',$flags).GetValue($script:observedSource)
    if($null -ne $inputs) {
        $work=$inputs.GetType().GetField('work',$flags).GetValue($inputs)
        [Console]::Error.WriteLine('canonical-source-failure; reason='+$failure+'; active_ms='+$work.ElapsedMilliseconds+'; lines_read='+$script:observedSource.LinesRead)
        $owner=$script:observedSource.GetType().GetField('worker',$flags).GetValue($script:observedSource)
        [Console]::Error.WriteLine('primary-source-currentness; rejected='+$inputs.GetType().GetField('PrimarySourceRejectionForTest',$flags).GetValue($inputs)+
            '; base='+$owner.GetType().GetField('PrimaryBaseRejectionForTest',$flags).GetValue($owner)+
            '; close='+$inputs.GetType().GetField('PrimarySourceCloseForTest',$flags).GetValue($inputs)+
            '; exception='+$inputs.GetType().GetField('PrimarySourceExceptionForTest',$flags).GetValue($inputs)+
            '; '+$inputs.GetType().GetField('PrimarySourceSnapshotForTest',$flags).GetValue($inputs))
        [Console]::Error.WriteLine('primary-source-first-close; '+$inputs.GetType().GetField('PrimarySourceCloseSnapshotForTest',$flags).GetValue($inputs))
        $profileDiagnostic=$inputs.GetType().GetMethod('GetPrimaryWorkProfileForTest',$flags).Invoke($inputs,$null)
        [Console]::Error.WriteLine($profileDiagnostic)
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'source-profile' $profileDiagnostic}
    }
}
'@
}
$text=Replace-AttemptSeam $text ". (Join-Path `$RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')" @'
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryGuards.ps1')
$definition=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.Ast.Parent.Extent.Text
$definition=$definition.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
$definition=Add-AttemptPayloadPrimaryProbeGuards $definition
Invoke-Expression $definition
'@
# The outer source contains its own param block. Invoke it with the original argv.
try {
    & ([scriptblock]::Create($text)) -Mode $Mode -LogPath $LogPath -ArtifactDir $ArtifactDir -TailMinutes $TailMinutes -LiveRouteProofMode $LiveRouteProofMode
    if(-not $script:primaryPayloadChecked) {throw 'Original primary did not reach payload binding and cleanup'}
} catch {
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'worker-error' $_.Exception.Message}
    throw
}
Write-Host ('attempt-payload-primary-checked='+$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE)
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'finished' 'original parent analyzer evaluations=3 nativeReads=1; payload binding and cleanup checked'}
