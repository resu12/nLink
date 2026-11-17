param([string]$ExePath,[string]$WalletPath,[string]$WalletPassword,[string]$SidecarPath,[string]$PayerMode,
    [string]$RouteMode,[string]$Fault,[string]$Direction,[string]$PayloadSize,[string]$ArtifactDir,
    [int]$TimeoutSeconds,[int]$ProgressTimeoutSeconds,[string]$RetainedAnalysisOwner,[string]$LiveToggleSequence)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $PSScriptRoot 'FrontierAttemptDispatchProjection.ps1')
$text=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FrontierCanonicalGuiWrapperProbe.ps1') -Raw
# Reuse the normal-loader launch probe with a headless two-app scenario child.
$text=Replace-AttemptSeam $text "'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalGuiConsumerProbe.ps1')," @'
'tests/nLink.SmokeTests.Core/Support/FrontierScenarioLaunchGui.ps1'), '-RepoRoot', `$RepoRoot, '-Directory', `$ArtifactDir, '-Case', 'valid',
'@
# Keep log deletion inside this fixture, before the original child launch.
$text=Replace-AttemptSeam $text '$script:duplicateAccepted=$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest,$ExePath,$TimeoutSeconds,$startInfo)' @'
$script:duplicateAccepted=$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest,$ExePath,$TimeoutSeconds,$startInfo)
$startInfo.EnvironmentVariables['LOCALAPPDATA']=Join-Path $ArtifactDir 'fixture-local-app-data'
'@
# This component child checks role launches directly, not the top-level scenario
# admission. Keep its consumption/cleanup evidence distinct from dispatch proof.
$text=Replace-AttemptSeam $text @'
    if($gui.Consumed -ne ($case -cin @('enabled','gui-nonzero','copied-ticket','retired-ticket','parent-cancelled')) -or
        $gui.ScenarioOwned -ne ($case -cin @('enabled','gui-nonzero')) -or $gui.ScenarioCalls -ne 2 -or
        -not $gui.ReplayRejected -or -not $gui.PrivateInputs -or -not $gui.Retired -or $gui.Pid -ne $script:childPid) {throw 'GUI scope or retirement changed'}
    [ordered]@{Checked=$true;WrapperPid=$PID;GuiPid=$script:childPid;Forwarded=$script:forwarded;
        Consumed=$gui.Consumed;ScenarioOwned=$gui.ScenarioOwned;ReplayRejected=$gui.ReplayRejected;PrivateInputs=$gui.PrivateInputs} |
'@ @'
    if($gui.Consumed -ne ($env:NLINK_TEST_CANONICAL_ROLES_CASE -cne 'unconsumed') -or
        -not $gui.Retired -or $gui.Pid -ne $script:childPid) {throw 'GUI scope or retirement changed'}
    [ordered]@{Checked=$true;WrapperPid=$PID;GuiPid=$script:childPid;Forwarded=$script:forwarded;Consumed=$gui.Consumed} |
'@
# In-memory script blocks have no file-backed script root.
$text=$text.Replace('$PSScriptRoot',("'"+$PSScriptRoot.Replace("'","''")+"'"))
& ([scriptblock]::Create($text)) @PSBoundParameters
