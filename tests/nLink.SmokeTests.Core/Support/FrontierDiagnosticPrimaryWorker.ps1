param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$config=Get-Content -LiteralPath (Join-Path $ArtifactDir 'parent-case.json') -Raw | ConvertFrom-Json
$RepoRoot=$config.repoRoot
$script:diagnosticProbeError=$null; $script:diagnosticChecked=$false; $script:diagnosticResolverCalls=0
$script:diagnosticPayload=$null; $script:diagnosticObservation=$null; $script:diagnosticPins=@()
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$text=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParentWorker.ps1'))
$text=Replace-AttemptSeam $text ". (Join-Path `$RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')" @'
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticProbeGuards.ps1')
$definition=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.Ast.Parent.Extent.Text
$definition=$definition.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
$definition=Add-FrontierDiagnosticProbeGuards $definition
Invoke-Expression $definition
'@
# Default native loader and original parser/evaluators; no transfer-pair extension.
& ([scriptblock]::Create($text)) -Mode $Mode -LogPath $LogPath -ArtifactDir $ArtifactDir -TailMinutes $TailMinutes -LiveRouteProofMode $LiveRouteProofMode
if($null -ne $script:diagnosticProbeError) {throw $script:diagnosticProbeError}
if(-not $script:diagnosticChecked) {throw 'Original diagnostic primary did not reach capture and cleanup'}
Write-Host 'attempt-diagnostic-primary-checked'
