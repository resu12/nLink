param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cne 'canonical-tail-late-publication' -or $env:NLINK_TEST_DIAGNOSTIC_CUSTODY) {
    throw 'Canonical tail primary requires its separate actual-pair profile'
}
$config=Get-Content -LiteralPath (Join-Path $ArtifactDir 'parent-case.json') -Raw | ConvertFrom-Json
$RepoRoot=$config.repoRoot
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
function Get-CanonicalTailPrimaryWorkerForTest([string]$Root) {
    $text=[IO.File]::ReadAllText((Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryWorker.ps1'))
    # Bind precedes Capture. On closure, the late probe runs after native
    # disposal and before the existing pair-cleanup probe, even if Bind failed.
    return (Replace-AttemptSeam $text '$definition=Add-AttemptPayloadPrimaryProbeGuards $definition' @'
$definition=Add-AttemptPayloadPrimaryProbeGuards $definition
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationProbeGuards.ps1')
$definition=Add-FrontierLatePublicationProbeGuards $definition -CanonicalTail
'@)
}
function Clear-CanonicalTailPrimaryReferencesForTest {
    foreach($name in @('canonicalTailReceipt','transferEvaluation','latePublicationPayload','latePublicationState',
        'latePublicationFailure','latePublicationFull','latePublicationCompact','latePublicationOmissionReason','latePublicationProbeError')) {
        Set-Variable -Scope Script -Name $name -Value $null
    }
    foreach($name in @('workerPayloadHandles','latePublicationWitnesses','latePublicationPins','latePublicationCandidates')) {
        Set-Variable -Scope Script -Name $name -Value @()
    }
}
$script:canonicalTailReceipt=$null; $script:canonicalTailJoined=$false
$script:latePublicationProbeError=$null; $script:latePublicationChecked=$false; $script:latePublicationResolverCalls=0
$script:latePublicationResolved=$false; $script:latePublicationOutput=$false; $script:latePublicationOmissionReason=$null
$script:latePublicationReportsBefore=$null; $script:latePublicationReportsAfter=$null; $script:latePublicationWitnesses=@()
$script:latePublicationPayload=$null; $script:latePublicationObservation=$null; $script:latePublicationPins=@()
try {
    $text=Get-CanonicalTailPrimaryWorkerForTest $RepoRoot
    & ([scriptblock]::Create($text)) -Mode $Mode -LogPath $LogPath -ArtifactDir $ArtifactDir -TailMinutes $TailMinutes -LiveRouteProofMode $LiveRouteProofMode
    if($null -ne $script:latePublicationProbeError) {throw $script:latePublicationProbeError}
    if(-not $script:canonicalTailJoined -or -not $script:latePublicationChecked -or -not $script:primaryPayloadChecked) {
        throw 'Canonical tail primary did not complete its original same-pair witness and cleanup'
    }
    Write-Host 'attempt-canonical-tail-same-pair-primary-checked'
} finally {Clear-CanonicalTailPrimaryReferencesForTest}
