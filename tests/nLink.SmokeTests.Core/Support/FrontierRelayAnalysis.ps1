# Called with the original accepted relay custody, after relay-request disposal.
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'RouteAcceptance parse failed'}
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess','Invoke-RouteAcceptanceRetainedAnalysis')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw ('Ambiguous analysis function: '+$name)}
    $text=$definition[0].Extent.Text
    if($name -ceq 'Invoke-RouteAcceptanceRetainedAnalysis') {
        if($text -cnotmatch '\$analysisTimeoutSeconds = 3600' -or $text -cnotmatch 'while \(-not \$process.WaitForExit\(5000\)\)') {throw 'Original analysis deadline changed'}
        $text=$text.Replace('$startInfo.FileName = ''powershell''','$startInfo.FileName = (Get-Process -Id $PID).Path')
        $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
        $text=$text.Replace("Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'","Join-Path `$script:relayRepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
        $text=$text.Replace('$process.StartInfo = $startInfo',"`$process.StartInfo = `$startInfo`nTest-RelayAnalysisLaunch `$private:frontierPreparation")
    }
    Invoke-Expression $text
}
$script:relayRepoRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'
$script:relayHash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:relayCount=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
$script:relayHashes=0; $script:relayCounts=0; $script:relayLaunches=0; $script:relayPreparation=$null; $script:relayPrimaryPins=@()
$script:relayAnalysisPath=$writer.Outputs[3].Path
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$ReadLease=$null,$OwnedSource=$null)
    if($Path -ceq $script:relayAnalysisPath) {$script:relayHashes++}
    & $script:relayHash @PSBoundParameters
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path,$ReadLease=$null,$OwnedSource=$null)
    if($Path -ceq $script:relayAnalysisPath) {$script:relayCounts++}
    & $script:relayCount @PSBoundParameters
}
function Test-RelayAnalysisLaunch($Preparation) {
    $script:relayLaunches++; $script:relayPreparation=$Preparation
    if(($null -ne $Preparation) -ne ($Case -cne 'analysis-changed')) {throw 'Ordinary source compatibility gate selected the wrong source'}
    if($null -ne $Preparation) {
        $nativePins=$Preparation.Inputs.GetType().GetField('pins',$flags).GetValue($Preparation.Inputs)
        $script:relayPrimaryPins=@($nativePins.GetType().GetField('Handles',$flags).GetValue($nativePins))
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1') -Stage Launch
        }
    }
}
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Parent introduced an authority evaluation'}
$priorLocation=Get-Location
try {
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$writer.AcceptanceRunId
    $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$writer.ScenarioRunId
    if($Case -ceq 'analysis-changed') {[IO.File]::AppendAllText($script:relayAnalysisPath,"changed ordinary input`n")}
    $probe=[IO.File]::OpenRead($script:relayAnalysisPath); $sha=[Security.Cryptography.SHA256]::Create()
    try {$expectedHash=[BitConverter]::ToString($sha.ComputeHash($probe)).Replace('-','').ToLowerInvariant()}
    finally {$sha.Dispose(); $probe.Dispose()}
    [void][IO.Directory]::CreateDirectory((Join-Path $Directory 'tools'))
    $worker=if($env:NLINK_TEST_DIAGNOSTIC_CUSTODY -ceq 'frontier-schedule') {'FrontierDiagnosticPrimaryWorker.ps1'}
        elseif($env:NLINK_TEST_DIAGNOSTIC_CUSTODY -ceq 'frontier-late-publication') {'FrontierLatePublicationPrimaryWorker.ps1'}
        elseif($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-tail-late-publication') {'FrontierCanonicalTailPrimaryWorker.ps1'}
        elseif($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE) {'FrontierAttemptPayloadPrimaryWorker.ps1'} else {'FrontierRetainedParentWorker.ps1'}
    Copy-Item -LiteralPath (Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$worker)) -Destination (Join-Path $Directory 'tools/FileTransfer-Ops.ps1')
    Write-FileTransferEvidenceAtomicJson (Join-Path $Directory 'parent-case.json') @{repoRoot=$RepoRoot;scenario=$(if($Case -ceq 'analysis-changed') {'merged-ordinary'} else {'merged-native'})}
    Set-Location -LiteralPath $Directory
    $analysisFailure=''
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $Directory -LiveRouteProofMode RegularActivationCycle -CaptureLocalSourceOwnership -CaptureFrontierReports -LocalWriterContext $writer -FrontierRelayCustody $exported}
    catch {
        if((-not $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -and -not $env:NLINK_TEST_DIAGNOSTIC_CUSTODY) -or
            -not $_.Exception.Message.StartsWith('FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for ')) {throw}
        $analysisFailure=$_.Exception.Message
    }
    $receipt=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis-attempt-receipt.json') -Raw | ConvertFrom-Json
    $observed=Get-Content -LiteralPath (Join-Path $Directory 'analysis-observed-source.json') -Raw | ConvertFrom-Json
    $expectedStatus=if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -and $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cnotin @('canonical-source','canonical-fallback')) {'proof_incomplete'} else {'completed'}
    if($env:NLINK_TEST_DIAGNOSTIC_CUSTODY -or $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-tail-late-publication') {$expectedStatus=if($analysisFailure) {'proof_incomplete'} else {'completed'}}
    Write-Host ('original parent analysis observations; owner='+$receipt.owner+'; status='+$receipt.status+'; hashes='+$script:relayHashes+'; counts='+$script:relayCounts+'; launches='+$script:relayLaunches)
    if($receipt.owner -cne 'RouteAcceptance' -or $receipt.status -cne $expectedStatus -or -not $receipt.analyzerInputUnchanged -or
        $receipt.analyzerInputSha256Before -cne $expectedHash -or $script:relayHashes -ne 2 -or $script:relayCounts -ne 2 -or $script:relayLaunches -ne 1) {throw 'Original analysis owner, source passes or terminal receipt changed'}
    if($observed.native -ne ($Case -cne 'analysis-changed')) {throw 'Analyzer silently substituted the wrong source'}
    if($Case -ceq 'analysis-changed' -and @($observed.origins | Where-Object {$_ -gt 0}).Count) {throw 'Changed input acquired native origins'}
    if($Case -cne 'analysis-changed' -and @($observed.origins | Where-Object {$_ -le 0}).Count) {throw 'Native input lost its physical origins'}
    if($null -ne $script:relayPreparation -and $script:relayPreparation.IsCurrent -or
        @($script:relayPrimaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Primary leaked transferred pins'}
    $output=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis.stdout.txt') -Raw
    if($output -notmatch 'original parent analyzer evaluations=3 nativeReads=1') {throw 'Original evaluator/source pass count changed'}
    if($env:NLINK_TEST_DIAGNOSTIC_CUSTODY) {
        $marker=if($env:NLINK_TEST_DIAGNOSTIC_CUSTODY -ceq 'frontier-late-publication') {'attempt-late-publication-primary-checked'} else {'attempt-diagnostic-primary-checked'}
        if($output -notmatch $marker -or
            $receipt.coordinatorSnapshotMode -cne 'SnapshotV1Authoritative' -or
            [bool]$receipt.completedProofArtifacts -eq [bool]$analysisFailure) {throw 'Diagnostic primary result or cleanup missing'}
        Write-Host 'attempt-diagnostic-stopped-before-route-acceptance'
        throw 'Offline diagnostic-custody verification stopped before route-cycle acceptance'
    }
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE) {
        if($output -notmatch ('attempt-payload-primary-checked='+[regex]::Escape($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE)) -or
            @($script:primaryPayloadHandles | Where-Object {-not $_.IsClosed}).Count) {throw 'Primary payload assertion or parent-side cleanup missing'}
        Write-Host ('attempt-payload-primary-analysis='+$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE)
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-tail-late-publication') {
            if($output -notmatch 'attempt-canonical-tail-same-pair-primary-checked') {throw 'Canonical same-pair primary marker missing'}
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailProof.ps1') -Directory $Directory -Receipt $receipt -AnalysisFailure $analysisFailure
            Write-Host 'attempt-canonical-tail-stopped-before-route-acceptance'
            throw 'Offline canonical-tail verification stopped before route-cycle acceptance'
        }
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-source') {
            if($analysisFailure -or -not $receipt.completedProofArtifacts -or $receipt.coordinatorSnapshotMode -cne 'SnapshotV1Authoritative' -or
                $output -notmatch 'selected_failure=\(none\); diagnostic_witness=unavailable') {throw 'Canonical source analysis or witness eligibility changed'}
            Write-Host 'attempt-payload-canonical-source-stopped-before-route-acceptance'
            throw 'Offline canonical-source verification stopped before route-cycle acceptance'
        }
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalFallbackProof.ps1') -Directory $Directory -Receipt $receipt -AnalysisFailure $analysisFailure
            Write-Host 'attempt-payload-canonical-fallback-stopped-before-route-acceptance'
            throw 'Offline canonical-fallback verification stopped before route-cycle acceptance'
        }
        elseif(-not $analysisFailure -or $receipt.completedProofArtifacts -or $receipt.coordinatorSnapshotMode -cne 'LegacyV0') {throw 'Incomplete source was promoted or rejection lost'}
        Write-Host 'attempt-payload-primary-proof-incomplete-retained'
        throw $analysisFailure
    }
    'frontier-relay-analysis='+$Case
} finally {Set-Location -LiteralPath $priorLocation}
