param([string]$RepoRoot,[string]$SourcePath,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; $ProgressPreference='SilentlyContinue'
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
$analyzerName=$env:NLINK_RETAINED_SOURCE_REQUEST; $analyzerId=$env:NLINK_RETAINED_SOURCE_REQUEST_ID
$analyzerBegin=[Threading.EventWaitHandle]::OpenExisting($analyzerName+'.test.analyzer.begin')
$analyzerContinue=[Threading.EventWaitHandle]::OpenExisting($analyzerName+'.test.analyzer.continue')
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
$realParser=(Get-Command Read-FileTransferLogEvents).ScriptBlock
$script:evaluations=0; $script:sourceReads=0; $script:nativeReads=0; $script:observedSource=$null
function Normalize-AnalyzerBaseline([string]$Value) {
    $baselineSource=Join-Path $Directory 'baseline-source.log'
    return $Value.Replace($baselineSource.Replace('\','\\'),$SourcePath.Replace('\','\\')).Replace($baselineSource,$SourcePath).Replace('baseline-source.log',[IO.Path]::GetFileName($SourcePath))
}
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++; $value=& $realEvaluator -Lines $Lines
    if($Case -ceq 'budget-rejected' -and $script:evaluations -eq 1 -and $null -ne $script:observedSource) {
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $owner=$script:observedSource.GetType().GetField('worker',$flags).GetValue($script:observedSource)
        $inputs=$owner.GetType().GetField('frontierWorkerInputs',$flags).GetValue($owner)
        $work=$inputs.GetType().GetField('work',$flags).GetValue($inputs)
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*21))
    }
    return $value
}
function Read-FileTransferLogEvents {
    param([string[]]$LogFiles,[int]$TailMinutes,$OwnedSource=$null)
    $script:sourceReads++; $script:observedSource=$OwnedSource
    if($null -ne $OwnedSource) {$script:nativeReads++}
    & $realParser -LogFiles $LogFiles -TailMinutes $TailMinutes -OwnedSource $OwnedSource
}
if($Case -ceq 'report-failure') {
    function Write-FileTransferEfficiencySummaryArtifacts {throw 'injected original analyzer report failure'}
}
try {
    $baseline=Join-Path $Directory 'baseline'; $actual=Join-Path $Directory 'actual'
    $env:NLINK_RETAINED_SOURCE_REQUEST=''; $env:NLINK_RETAINED_SOURCE_REQUEST_ID=''
    $failures=@(); $witnesses=@(); $counts=@()
    foreach($output in @($baseline,$actual)) {
        if($output -ceq $actual) {
            $env:NLINK_RETAINED_SOURCE_REQUEST=$analyzerName; $env:NLINK_RETAINED_SOURCE_REQUEST_ID=$analyzerId
            [void]$analyzerBegin.Set()
            if(-not $analyzerContinue.WaitOne(30000)) {throw 'Analyzer fixture start deadline'}
        }
        $script:evaluations=0; $witness=$null; $failure=''
        $inputPath=if($output -ceq $baseline) {Join-Path $Directory 'baseline-source.log'} else {$SourcePath}
        try {$null=Invoke-FileTransferRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $output -LogPath @($inputPath) -TailMinutes 0 -LocalFailureWitness ([ref]$witness)}
        catch {$failure=$_.Exception.Message; [Console]::Out.WriteLine($_.ScriptStackTrace)}
        $failures+=,$failure; $witnesses+=,$witness; $counts+=,$script:evaluations
    }
    $expected=if($Case -ceq 'report-failure') {'injected original analyzer report failure'} else {''}
    # The primary pair is followed by the existing probe-sidecar classifier's
    # separate subset evaluation. Native diagnostics must add no fourth call.
    if($failures[0] -cne $expected -or $failures[1] -cne $expected -or @($counts | Where-Object {$_ -ne 3}).Count -or
        $script:sourceReads -ne 2 -or $script:nativeReads -ne 1 -or $script:observedSource.InputReadComplete) {
        throw ('Original analyzer outcome/source/evaluations changed: '+(@{Failures=$failures;Counts=$counts;Reads=$script:sourceReads;Native=$script:nativeReads} | ConvertTo-Json -Compress))
    }
    foreach($name in @('filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
        'filetransfer-coordinator-snapshot-parity-summary.json','filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','stability-gates-summary.txt')) {
        $left=Join-Path $baseline $name; $right=Join-Path $actual $name
        if((Test-Path -LiteralPath $left) -ne (Test-Path -LiteralPath $right) -or
            ((Test-Path -LiteralPath $left) -and (Normalize-AnalyzerBaseline ([IO.File]::ReadAllText($left))) -cne [IO.File]::ReadAllText($right))) {throw ('Original report changed: '+$name)}
    }
    $left=Get-Content -LiteralPath (Join-Path $baseline 'filetransfer-retained-compact-summary.json') -Raw | ConvertFrom-Json
    $right=Get-Content -LiteralPath (Join-Path $actual 'filetransfer-retained-compact-summary.json') -Raw | ConvertFrom-Json
    foreach($property in $left.PSObject.Properties) {
        if($property.Name -match '_ms$') {continue}
        if((Normalize-AnalyzerBaseline ($property.Value | ConvertTo-Json -Depth 8 -Compress)) -cne ($right.($property.Name) | ConvertTo-Json -Depth 8 -Compress)) {throw ('Compact report changed: '+$property.Name)}
    }
    if($expected -eq '') {
        if(($null -eq $witnesses[0]) -ne ($null -eq $witnesses[1])) {throw 'Original selected failure witness presence changed'}
        if($null -ne $witnesses[0] -and (Normalize-AnalyzerBaseline (ConvertTo-Json -InputObject $witnesses[0] -Depth 8 -Compress)) -cne
            (ConvertTo-Json -InputObject $witnesses[1] -Depth 8 -Compress)) {throw 'Original selected failure witness changed'}
    }
    if($right.first_proof_chain_failure_class -cne 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -or
        $right.filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_unresolved_count -ne 1) {throw 'Original unresolved failure was recovered/replaced'}
    'frontier-analyzer-failure='+$right.first_proof_chain_failure_class
    'frontier-analyzer-worker='+(@{Case=$Case;Counts=$counts;SourceReads=$script:sourceReads;NativeReads=$script:nativeReads;Failure=$expected} | ConvertTo-Json -Compress)
    $nativeFlags=[Reflection.BindingFlags]'Instance,NonPublic'
    $nativeOwner=$script:observedSource.GetType().GetField('worker',$nativeFlags).GetValue($script:observedSource)
    $nativeInputs=$nativeOwner.GetType().GetField('frontierWorkerInputs',$nativeFlags).GetValue($nativeOwner)
    $nativeCustody=$nativeInputs.GetType().GetField('evaluation',$nativeFlags).GetValue($nativeInputs)
    $nativeFacts=@{Custody=$null -ne $nativeCustody;Milliseconds=$nativeInputs.GetType().GetField('work',$nativeFlags).GetValue($nativeInputs).ElapsedMilliseconds}
    if($null -ne $nativeCustody) {foreach($field in @('fullInvocation','compactInvocation','payloadAttempted','diagnosticOfferAttempted','diagnosticCommitAttempted')) {$nativeFacts[$field]=$nativeCustody.GetType().GetField($field,$nativeFlags).GetValue($nativeCustody)}}
    'frontier-analyzer-native='+($nativeFacts | ConvertTo-Json -Compress)
    [Console]::Error.Write('original analyzer stderr')
    if($expected) {exit 7}
} finally {$analyzerBegin.Dispose(); $analyzerContinue.Dispose()}
