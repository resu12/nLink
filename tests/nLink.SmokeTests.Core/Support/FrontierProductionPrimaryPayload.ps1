param([string]$RepoRoot,[string]$Directory,[string]$Fault)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_TEST_GUI_CLOSED_PAYLOAD_HANDOFF='1'
$env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD='valid'
$env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD=if($Fault -ceq 'parent-rejected') {'foreign-writer'} else {'valid'}
$env:NLINK_TEST_PRODUCTION_PRIMARY_PAYLOAD=$Fault
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1'
$text=[IO.File]::ReadAllText($path)
$original=Get-AttemptFunction (Read-AttemptAst $path) 'Invoke-RouteAcceptanceRetainedAnalysis'
$text=Replace-AttemptSeam $text $original @'
function Invoke-RouteAcceptanceRetainedAnalysis {
    param($ArtifactDir,$LiveRouteProofMode,[switch]$CaptureLocalSourceOwnership,[switch]$CaptureFrontierReports,
        $LocalWriterContext=$null,$LocalPublication=$null,$FrontierRelayCustody=$null)
    $script:analysisCalls++
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage AnalysisBoundary
    $writer=$LocalWriterContext; $exported=$FrontierRelayCustody
    $flags=[Reflection.BindingFlags]'Instance,NonPublic'; $Case='analysis'
    $analysis=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1'))
    $analysis=Replace-AttemptSeam $analysis "'FrontierAttemptPayloadPrimaryWorker.ps1'" "'FrontierProductionPrimaryPayloadWorker.ps1'"
    $analysis=Replace-AttemptSeam $analysis 'FrontierAttemptPayloadPrimaryProbe.ps1' 'FrontierProductionPrimaryPayloadProbe.ps1'
    & ([scriptblock]::Create($analysis))
}
'@
# The existing parent cleanup probe expects no analysis failure. Exclude only
# the exact ordinary incomplete-proof outcome that the enclosing driver checks.
$text=Replace-AttemptSeam $text "if(`$env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD) {. (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage Finished}" @'
$savedFailure=$failure
try {
    if($failure.StartsWith('FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for ')) {$failure=''}
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage Finished
} finally {$failure=$savedFailure}
'@
& ([scriptblock]::Create($text)) -RepoRoot $RepoRoot -Directory $Directory -Case 'writer-source-read-cross-process-gui-valid' `
    -PayloadWrapperCase 'writer-valid' -PrimaryPayloadCase 'production-primary'
'production-primary-payload-checked='+$Fault
