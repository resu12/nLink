param([string]$RepoRoot,[string]$Directory,[string]$Mode)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'parent script parse failed'}
foreach($definition in $ast.EndBlock.Statements) {
    if($definition -is [Management.Automation.Language.FunctionDefinitionAst]) { Invoke-Expression $definition.Extent.Text }
}
# Reporting must consume persisted primary results, not invoke either evaluator.
function Compare-FileTransferCoordinatorSnapshotEvaluations { throw 'parity reevaluated' }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'snapshot reevaluated' }
function Invoke-RouteAcceptanceRetainedAnalysis { throw 'analysis rerun' }
$hash='a'*64
$catalog='c'*64
$full=[ordered]@{Mode='SnapshotV1Authoritative';ContractVersion=1;CatalogHash=$catalog;BoundaryHash=$hash;BoundaryCount=36;TransitionCount=36;EffectCount=36}
$compact=[ordered]@{Mode='SnapshotV1Authoritative';ContractVersion=1;CatalogHash=$catalog;BoundaryHash=$hash;BoundaryCount=36;TransitionCount=36;EffectCount=36}
$parity=[ordered]@{Verdict='pass';FailureReason='';FullMode=$full.Mode;CompactMode=$compact.Mode;FullBoundaryHash=$hash;CompactBoundaryHash=$hash;FullBoundaryCount=36;CompactBoundaryCount=36;FullTransitionCount=36;CompactTransitionCount=36;FullEffectCount=36;CompactEffectCount=36}
$route=[ordered]@{
    route_consistency_verdict='pass';route_selected_count=1
    'selected.1.route'='regular_nkn_v4_fast';'selected.1.protocol_version'=4
    'selected.1.runtime_profile'='regular_nkn_v4_fast';'selected.1.bridge_recovery_policy'='regular_nkn_v4_fast'
    coordinator_snapshot_mode=$full.Mode;coordinator_snapshot_contract_version=1
    coordinator_snapshot_catalog_hash=$catalog;coordinator_snapshot_boundary_hash=$hash
    coordinator_snapshot_boundary_count=36;coordinator_snapshot_transition_count=36;coordinator_snapshot_effect_count=36
    coordinator_snapshot_route_consistency_full_compact_parity_verdict='pass'
}
$expectedVerdict='invalid'
$expectedReason='snapshot_parity_artifact_conflict'
switch($Mode) {
    'pass' {$expectedVerdict='pass';$expectedReason=''}
    'fail' {
        $parity.Verdict='fail';$parity.FailureReason='snapshot_full_compact_boundaryhash_mismatch'
        $compact.BoundaryHash='b'*64;$parity.CompactBoundaryHash=$compact.BoundaryHash
        $expectedVerdict='fail';$expectedReason=$parity.FailureReason
    }
    'missing-parity' {$expectedVerdict='missing';$expectedReason='snapshot_parity_artifact_missing'}
    'missing-full' {$expectedVerdict='missing';$expectedReason='snapshot_parity_artifact_missing'}
    'missing-compact' {$expectedVerdict='missing';$expectedReason='snapshot_parity_artifact_missing'}
    'malformed' {$expectedReason='snapshot_parity_artifact_invalid'}
    'missing-field' {$parity.Remove('FullBoundaryHash');$expectedReason='snapshot_parity_artifact_invalid'}
    'wrong-type' {$parity.CompactBoundaryCount='36';$expectedReason='snapshot_parity_artifact_invalid'}
    'wrong-parity-hash' {$parity.FullBoundaryHash='d'*64}
    'wrong-compact-count' {$compact.BoundaryCount=37}
    'wrong-route-binding' {$route.coordinator_snapshot_boundary_hash='d'*64}
    'conflicting-text' {$route.coordinator_snapshot_full_compact_parity_verdict='fail'}
    'route-failure' {$route.route_consistency_verdict='fail';$expectedVerdict='pass';$expectedReason=''}
    'fail-compact-primary' {
        $parity.Verdict='fail';$parity.FailureReason='snapshot_full_compact_boundaryhash_mismatch'
        $compact.BoundaryHash='b'*64;$parity.CompactBoundaryHash=$compact.BoundaryHash
        $route.coordinator_snapshot_boundary_hash=$compact.BoundaryHash
        $expectedVerdict='fail';$expectedReason=$parity.FailureReason
    }
    'legacy' {
        $full.Mode='LegacyV0';$compact.Mode='LegacyV0';$parity.FullMode='LegacyV0';$parity.CompactMode='LegacyV0'
        $route.coordinator_snapshot_mode='LegacyV0'
        $expectedVerdict='pass';$expectedReason=''
    }
    'route-family-failure' {$route.coordinator_snapshot_route_consistency_full_compact_parity_verdict='fail';$expectedVerdict='pass';$expectedReason=''}
    'consistent-text' {
        $route.coordinator_snapshot_full_compact_parity_verdict='pass'
        $route.coordinator_snapshot_full_compact_parity_failure_reason=''
        $route.coordinator_snapshot_full_boundary_hash=$hash;$route.coordinator_snapshot_compact_boundary_hash=$hash
        $expectedVerdict='pass';$expectedReason=''
    }
    'wrong-verdict-type' {$parity.Verdict=@('pass');$expectedReason='snapshot_parity_artifact_invalid'}
    'wrong-catalog' {$compact.CatalogHash='d'*64}
    'array-parity' {$expectedReason='snapshot_parity_artifact_invalid'}
    'array-full' {$expectedReason='snapshot_parity_artifact_invalid'}
    'array-compact' {$expectedReason='snapshot_parity_artifact_invalid'}
    default {throw 'unknown mode'}
}
foreach($kind in @('full','compact','parity')) {
    if($Mode -ceq "missing-$kind") {continue}
    $value=Get-Variable -Name $kind -ValueOnly
    $json=if($Mode -ceq 'malformed' -and $kind -ceq 'parity') {'{not-json'} else {$value | ConvertTo-Json -Depth 5}
    if($Mode -ceq "array-$kind") { $json='['+$json+']' }
    [IO.File]::WriteAllText((Join-Path $Directory "filetransfer-coordinator-snapshot-$kind-summary.json"),$json)
}
[IO.File]::WriteAllLines((Join-Path $Directory 'filetransfer-route-consistency-summary.txt'),[string[]]@($route.GetEnumerator() | ForEach-Object {"$($_.Key)=$($_.Value)"}))
$original=@{}
Get-ChildItem -LiteralPath $Directory -File | ForEach-Object { $original[$_.Name]=[Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)) }
$result=New-RouteAcceptanceRunResult -Name 'regular-nkn-v4-64mb' -ArtifactDir $Directory -ExpectedRoute 'regular_nkn_v4_fast' -ExpectedProtocol 4
Assert-Phase4RouteSummary -Result $result -ExpectedRouteChanges @('regular_nkn_v4_fast')
if($result.coordinatorSnapshotFullCompactParityVerdict -cne $expectedVerdict -or
    $result.coordinatorSnapshotFullCompactParityFailureReason -cne $expectedReason) {
    throw "projection mismatch: expected=$expectedVerdict/$expectedReason actual=$($result.coordinatorSnapshotFullCompactParityVerdict)/$($result.coordinatorSnapshotFullCompactParityFailureReason)"
}
if($expectedVerdict -in @('pass','fail')) {
    if($result.coordinatorSnapshotFullBoundaryHash -cne $parity.FullBoundaryHash -or
        $result.coordinatorSnapshotCompactBoundaryHash -cne $parity.CompactBoundaryHash) {throw 'original parity hashes not projected'}
} elseif($result.coordinatorSnapshotFullBoundaryHash -cne '' -or $result.coordinatorSnapshotCompactBoundaryHash -cne '') {throw 'invalid projection retained hashes'}
if($Mode -ceq 'route-failure' -and -not ($result.failures -contains 'route consistency verdict is fail')) {throw 'reporting erased route failure'}
if($Mode -cne 'route-failure' -and $result.failures.Count -ne 0) {throw "reporting changed route acceptance: $($result.failures -join ',')"}
if($Mode -ceq 'legacy' -and $result.coordinatorSnapshotMode -cne 'LegacyV0') {throw 'parity upgraded legacy mode'}
foreach($name in $original.Keys) {
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $Directory $name))) -cne $original[$name]) {throw 'original primary artifact mutated'}
}
Write-Output 'snapshot-parity-reporting-ok'
