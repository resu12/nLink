param($Mode,[string]$LogPath,[string]$ArtifactDir,$TailMinutes,$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=Get-Content -LiteralPath (Join-Path $ArtifactDir 'case.json') -Raw | ConvertFrom-Json
# This is the exact process launched and bound by the real retained parent.
# Real generated inputs, original evaluations and C1m issuer; reports below are
# controlled lifecycle fixtures, not claims about a real GUI failure.
& (Join-Path $settings.repoRoot 'tests/nLink.SmokeTests.Core/Support/PrimaryPublicationWitnessWorker.ps1') -RepoRoot $settings.repoRoot -SettingsPath $settings.workerSettings
$worker=Get-Content -LiteralPath (Join-Path $ArtifactDir 'worker.json') -Raw | ConvertFrom-Json
if($worker.Evaluations -ne 2) { throw 'Original evaluation count changed' }
function Save([string]$Name,[string]$Text) { [IO.File]::WriteAllText((Join-Path $ArtifactDir $Name),$Text,[Text.UTF8Encoding]::new($false)) }
$pass=$settings.mode -cnotin @('proof-incomplete','return-mutation')
$verdict=if($pass) {'PASS'} else {'FAIL'}; $hard=if($pass) {0} else {1}; $gate=if($pass) {'pass'} else {'fail'}
Save 'filetransfer-operator-verdict.txt' "verdict=$verdict`nhard_failure_count=$hard`n"
Save 'stability-gates-summary.txt' "gate_status=$gate`nhard_failure_count=$hard`n"
Save 'filetransfer-route-consistency-summary.txt' @'
route_consistency_verdict=pass
live_route_epoch_proof_verdict=pass
fallback_leg_authority_proof_verdict=pass
clean_fallback_leg_proof_verdict=pass
bridge_liveness_integration_verdict=pass
control_plane_isolation_verdict=pass
'@
$catalogHash=$worker.Catalog.catalogHash; if($settings.mode -ceq 'wrong-catalog') { $catalogHash='0'*64 }
$snapshot=@{Mode='SnapshotV1Authoritative';StreamComplete=$true;ContractVersion=$worker.Catalog.contractVersion
    CatalogVersion=$worker.Catalog.catalogVersion;CatalogHash=$catalogHash;BoundaryHash=$worker.Catalog.boundaryHash
    ArtifactEvidenceVerdict='pass';LegacySelectionCount=0;RouteFamilyLegacySelectionCount=0;RuntimeUnlockRetryFamilyLegacySelectionCount=0
    ReceiveRecoveryFamilyLegacySelectionCount=0;FallbackTailYieldFamilyLegacySelectionCount=0;RegularV4FrontierRepairPressureFamilyLegacySelectionCount=0
    ResponderProbeHandoffFamilyLegacySelectionCount=0} | ConvertTo-Json -Compress
Save 'filetransfer-coordinator-snapshot-full-summary.json' $snapshot
Save 'filetransfer-coordinator-snapshot-compact-summary.json' $snapshot
Save 'filetransfer-coordinator-snapshot-parity-summary.json' '{"Verdict":"pass"}'
Save 'filetransfer-retained-compact-summary.json' ('{"verdict":"'+$verdict+'","fixture":"lifecycle-only"}')
Save 'filetransfer-retained-analysis-stage-diagnostics.json' '{"stage":"primary-reports-persisted"}'
if($settings.mode -ceq 'missing-artifact') { [IO.File]::Delete((Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt')) }
if($settings.mode -ceq 'nonzero') { exit 9 }
if($settings.mode -ceq 'timeout') { [Threading.Thread]::Sleep(30000); Save 'late-optional.json' '{}' }
Write-Output 'controlled-primary-finished'
