$mergeCase=$Case.Substring('merge-'.Length)
# Match the existing merge fixture's SHA-256 helper when the controlled Windows
# PowerShell process cannot autoload Get-FileHash. Native custody still uses the
# real C# hash-and-hold operation; this only supplies the ordinary cmdlet surface.
if($null -eq (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
    function Get-FileHash {
        param([string]$LiteralPath,[string]$Algorithm)
        $stream=[IO.File]::OpenRead($LiteralPath); $sha=[Security.Cryptography.SHA256]::Create()
        try {return [pscustomobject]@{Hash=([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-','')}}
        finally {$sha.Dispose(); $stream.Dispose()}
    }
}
$script:mergeOriginal=$prepared
$path=$completion.Outputs[3].Path
$milestonePath=Join-Path $Directory 'filetransfer-tuna-gui-milestone-evidence.log'
$sourceLines=[IO.File]::ReadAllLines($path)
$originalSha=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
if($originalSha -cne $prepared.InputSha256) {throw 'GUI native and ordinary pre-merge sources differ'}
foreach($name in @('Get-TunaGuiRetainedLogLineSortKey','Sort-TunaGuiRetainedLogLinesChronologically',
    'Get-TunaGuiStrictUtf8LineCount','Write-TunaGuiDerivedAnalyzerInputReceipt','Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice')) {
    Invoke-Expression (Definition $name)
}
$routeLine=@($sourceLines | Where-Object {$_ -cmatch 'event=filetransfer_route_selected;'} | Select-Object -First 1)[0]
if([string]::IsNullOrEmpty($routeLine)) {throw 'Genuine fixture has no existing route milestone'}
$dates=@($sourceLines | ForEach-Object {[DateTimeOffset]::ParseExact($_.Substring(1,20),'yyyy-MM-dd HH:mm:ss\Z',
    [Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)} | Sort-Object)
$extraTime=if($mergeCase -ceq 'reorder') {$dates[0].AddSeconds(-1)} else {$dates[-1].AddSeconds(1)}
$extra='['+$extraTime.ToString('yyyy-MM-dd HH:mm:ss\Z')+'] [INFO] [GuiSmoke] event=filetransfer_live_route_epoch_started; fixture=merge-append; route=file_tuna_v4; protocol_version=4'
switch($mergeCase) {
    'ignored' {[IO.File]::WriteAllText($milestonePath,'event=not_a_route_milestone')}
    'duplicate' {[IO.File]::WriteAllText($milestonePath,$routeLine+[Environment]::NewLine+$routeLine)}
    'append' {[IO.File]::WriteAllText($milestonePath,$extra)}
    'merge-write-failure' {[IO.File]::WriteAllText($milestonePath,$extra)}
    'reorder' {[IO.File]::WriteAllText($milestonePath,$extra)}
    'encoding-only' {[IO.File]::WriteAllText($path,($sourceLines -join [Environment]::NewLine),[Text.Encoding]::UTF8)}
    'ledger-missing' {Move-Item -LiteralPath (Join-Path $Directory 'filetransfer-gui-log-ledger.json') -Destination (Join-Path $Directory 'fixture-ledger-backup.json')}
}
function Write-TunaGuiAtomicJson {
    param($Path,$Value)
    foreach($name in @('PreparedFrontierSource','FrontierPreparation','mergePreparation')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw ('Merge capability leaked into formatter: '+$name)}
    }
    if($script:mergeOriginal.PSObject.Methods.Name -contains 'TryBeginAnalysisInput') {
        if($script:mergeOriginal.TryClaimForAnalysis('Wrapper',$completion.AcceptanceRunId,$completion.ScenarioRunId,$completion.Outputs[3].Path,$completion)) {throw 'Primary claimed before derived receipt persisted'}
        $startFailure=''
        try {$null=$script:mergeOriginal.StartWorkerWithReports($null,$Directory,'RegularActivationCycle',[DateTimeOffset]::UtcNow.ToString('O'))}
        catch {$startFailure=$_.Exception.Message}
        if($startFailure -notmatch 'frontier_start_unavailable_or_repeated') {throw 'Direct native launch admitted pending merge input'}
    }
    if($mergeCase -ceq 'receipt-failure') {throw 'injected derived receipt failure'}
    if($mergeCase -ceq 'replaced-reference') {$script:prepared='preserve-new-owner'}
    & $originalAtomic @PSBoundParameters
}
$mergeArgs=@{ArtifactDir=$Directory}
# The pre-fix reproduction runs the unchanged merge rather than failing merely
# because the optional handoff parameter is not implemented yet.
if((Get-Command Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice).Parameters.ContainsKey('PreparedFrontierSource')) {
    $mergeArgs.PreparedFrontierSource=[ref]$prepared
    $copy=$parentClone.Invoke($prepared,$null)
    if($copy.TryBeginAnalysisInput($completion.AcceptanceRunId,$completion.ScenarioRunId,$path) -or
        $prepared.TryBeginAnalysisInput('foreign-run',$completion.ScenarioRunId,$path) -or
        $prepared.TryBeginAnalysisInput($completion.AcceptanceRunId,'foreign-scenario',$path) -or
        $prepared.TryBeginAnalysisInput($completion.AcceptanceRunId,$completion.ScenarioRunId,(Join-Path $Directory 'foreign.log')) -or
        $prepared.TryCompleteAnalysisInput()) {throw 'Foreign or premature merge input admitted'}
    $copy.Dispose(); if(-not $prepared.IsCurrent) {throw 'Rejected merge arguments retired original ownership'}
}
$mergeFailure=''; $mergeException=$null; $mergeLock=$null
if($mergeCase -ceq 'hash-failure') {$mergeLock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
if($mergeCase -ceq 'merge-write-failure') {$mergeLock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
try {Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice @mergeArgs}
catch {$mergeFailure=$_.Exception.Message; $mergeException=$_.Exception}
finally {if($null -ne $mergeLock) {$mergeLock.Dispose()}}
if($mergeCase -ceq 'replaced-reference') {
    if($mergeFailure -or $prepared -cne 'preserve-new-owner' -or $script:mergeOriginal.IsCurrent) {throw 'Reentrant output replacement leaked or retired another owner'}
    'frontier-milestone-compatibility='+$mergeCase; return
}
$expectNative=$mergeCase -cin @('absent','ignored','duplicate')
if(($null -ne $prepared) -ne $expectNative) {throw ('Post-merge native handoff selection mismatch: '+$mergeCase)}
if($mergeCase -ceq 'receipt-failure') {
    if($mergeFailure -cne 'injected derived receipt failure' -or $script:mergeOriginal.IsCurrent) {throw 'Receipt failure changed error or retained native ownership'}
    'frontier-milestone-compatibility='+$mergeCase; return
}
if($mergeCase -cin @('hash-failure','merge-write-failure')) {
    if($null -eq $mergeException -or $mergeException.GetBaseException() -isnot [IO.IOException] -or $script:mergeOriginal.IsCurrent) {throw ('Original merge/hash failure lost or retained ownership: '+$mergeFailure)}
    if([IO.File]::Exists((Join-Path $Directory 'primary-pid.txt'))) {throw 'Failed merge started primary'}
    $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()
    'frontier-milestone-compatibility='+$mergeCase; return
}
if($mergeFailure) {throw $mergeFailure}
$derivedLines=[IO.File]::ReadAllLines($path)
$derivedSha=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
$changed=$mergeCase -cin @('append','reorder','encoding-only')
$added=$mergeCase -cin @('append','reorder')
if(($derivedSha -cne $originalSha) -ne $changed -or $derivedLines.Length -ne ($sourceLines.Length+[int]$added)) {throw 'Original merge contents/count changed'}
if($added -and (($mergeCase -ceq 'reorder' -and $derivedLines[0] -cne $extra) -or
    ($mergeCase -ceq 'append' -and $derivedLines[-1] -cne $extra))) {throw 'Original chronological merge order changed'}
if($mergeCase -cne 'ledger-missing') {
    $derived=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-log-slice-derived-receipt.json') -Raw | ConvertFrom-Json
    if($derived.sourceAnalyzerInputSha256 -cne $originalSha -or $derived.derivedAnalyzerInputSha256 -cne $derivedSha -or
        $derived.derivedAnalyzerInputLineCount -ne $derivedLines.Length) {throw 'Original derived receipt changed'}
}
$blocked=$false
try {$probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()} catch [IO.IOException] {$blocked=$true}
if($blocked -ne $expectNative -or $script:mergeOriginal.IsCurrent -ne $expectNative) {throw 'Post-merge source pin lifetime mismatch'}
if($expectNative) {
    if($prepared.TryBeginAnalysisInput($completion.AcceptanceRunId,$completion.ScenarioRunId,$path)) {throw 'Repeated merge consumed original preparation'}
    $hashFailure=''; try {$null=$prepared.HashAndHoldAnalysisInput()} catch {$hashFailure=$_.Exception.Message}
    if($hashFailure -notmatch 'frontier_analysis_input_unavailable_or_repeated') {throw 'Original merge hash pass repeated'}
    $copy=$parentClone.Invoke($prepared,$null); $copy.Dispose()
    if(-not $prepared.IsCurrent) {throw 'Copied preparation retired original post-merge input'}
}
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
foreach($name in @('Read-TunaGuiReportValue','Write-TunaGuiRetainedAnalysisTimeoutArtifacts',
    'Test-TunaGuiRetainedAnalysisCompletedProofArtifacts','Stop-TunaGuiRetainedAnalysisProcess')) {Invoke-Expression (Definition $name)}
$analysis=Definition 'Invoke-TunaGuiRetainedAnalysis'
$analysis=$analysis.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")
$analysis=$analysis.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
$analysis=$analysis.Replace("Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'","Join-Path `$script:mergeRepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
$script:mergeRepoRoot=$RepoRoot; $script:analysisLaunches=0
$analysis=$analysis.Replace('$process.StartInfo = $startInfo',"`$process.StartInfo = `$startInfo`n`$script:analysisLaunches++")
Invoke-Expression $analysis
[void][IO.Directory]::CreateDirectory((Join-Path $Directory 'tools'))
Copy-Item -LiteralPath (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParentWorker.ps1') -Destination (Join-Path $Directory 'tools/FileTransfer-Ops.ps1')
[IO.File]::WriteAllText((Join-Path $Directory 'parent-case.json'),(@{repoRoot=$RepoRoot;scenario=$(if($expectNative) {'merged-native'} else {'merged-ordinary'})} | ConvertTo-Json))
$RouteMode='live-regular-activation-cycle'; $analysisFailure=''
try {Invoke-TunaGuiRetainedAnalysis -RepoRoot $Directory -AnalysisDir $Directory -LogPath $path -LiveRouteProofMode RegularActivationCycle `
    -CaptureLocalSourceOwnership -CaptureFrontierReports -LocalWriterContext $completion -PreparedFrontierSource $prepared}
catch {$analysisFailure=$_.Exception.Message}
$receipt=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis-attempt-receipt.json') -Raw | ConvertFrom-Json
if($script:analysisLaunches -ne 1 -or $receipt.exitCode -ne 0 -or $receipt.status -cne 'proof_incomplete' -or -not $receipt.analyzerInputUnchanged -or
    $receipt.analyzerInputSha256Before -cne $derivedSha -or $receipt.analyzerInputLineCountBefore -ne $derivedLines.Length -or
    $analysisFailure -notlike 'Retained analysis exited successfully without complete proof artifacts.*') {throw ('Original post-merge primary/receipt changed: '+$analysisFailure)}
$observed=Get-Content -LiteralPath (Join-Path $Directory 'analysis-observed-source.json') -Raw | ConvertFrom-Json
if($observed.native -ne $expectNative -or @($observed.lines).Count -ne $derivedLines.Length) {throw 'Primary read wrong input or native provenance'}
for($i=0;$i -lt $derivedLines.Length;$i++) {
    if($observed.lines[$i] -cne $derivedLines[$i] -or (($observed.origins[$i] -gt 0) -ne $expectNative)) {throw 'Primary reordered/dropped input or assigned foreign native origin'}
}
$stdout=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stdout.txt') -Raw
if($stdout -cnotmatch 'original parent analyzer evaluations=3 nativeReads=1') {throw 'Primary evaluations/source reads changed'}
if($script:mergeOriginal.IsCurrent) {throw 'Primary retained the original preparation'}
$probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()
'frontier-milestone-compatibility='+$mergeCase
