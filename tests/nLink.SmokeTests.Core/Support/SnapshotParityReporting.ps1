param([string]$RepoRoot,[string]$Directory,[string]$Mode)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferRouteAcceptanceEvidence.ps1')
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
    coordinator_snapshot_stream_complete='true';coordinator_snapshot_artifact_evidence_verdict='pass'
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
    default {
        if(-not $Mode.StartsWith('cycle-', [StringComparison]::Ordinal)) {throw 'unknown mode'}
        $expectedVerdict='pass';$expectedReason=''
    }
}
$expectedRoutes=@('regular_nkn_v4_fast')
if($Mode -in @('cycle-interleaved','cycle-wrong-protocol','cycle-forbidden-tuna-v6','cycle-forbidden-regular-v6')) {
    $expectedRoutes=@('regular_nkn_v4_fast','file_tuna_v4','post_tuna_fallback_v6','file_tuna_v4','post_tuna_fallback_v6')
    $publications=@(0,1,2,0,1,2,3,3,4,4)
    $route.route_selected_count=$publications.Count
    for($i=0;$i -lt $publications.Count;$i++) {
        $token=$expectedRoutes[$publications[$i]]
        $metadata=Get-RouteAcceptanceRouteMetadata -Route $token
        $route["selected.$($i+1).route"]=$token
        $route["selected.$($i+1).protocol_version"]=$metadata.Protocol
        $route["selected.$($i+1).runtime_profile"]=$metadata.Runtime
        $route["selected.$($i+1).bridge_recovery_policy"]=$metadata.Bridge
    }
    if($Mode -ceq 'cycle-wrong-protocol') {$route['selected.2.protocol_version']=6}
    if($Mode -in @('cycle-forbidden-tuna-v6','cycle-forbidden-regular-v6')) {
        $token=if($Mode -ceq 'cycle-forbidden-tuna-v6') {'file_tuna_v6'} else {'diagnostic_regular_nkn_v6'}
        $metadata=Get-RouteAcceptanceRouteMetadata -Route $token
        $route['selected.2.route']=$token
        $route['selected.2.protocol_version']=$metadata.Protocol
        $route['selected.2.runtime_profile']=$metadata.Runtime
        $route['selected.2.bridge_recovery_policy']=$metadata.Bridge
    }
}
$normalized=[ordered]@{source='SnapshotAuthorityV1';verdict='pass';selectedRouteChanges=$expectedRoutes}
switch($Mode) {
    'cycle-empty' {$normalized.selectedRouteChanges=@()}
    'cycle-wrong-type' {$normalized.selectedRouteChanges=@(4)}
    'cycle-peer-disagreement' {$route.route_consistency_verdict='fail';$normalized.verdict='fail'}
    'cycle-incomplete' {$route.coordinator_snapshot_stream_complete='false'}
    'cycle-artifact-fail' {$route.coordinator_snapshot_artifact_evidence_verdict='fail'}
}
$normalizedJson=$normalized | ConvertTo-Json -Depth 5 -Compress
switch($Mode) {
    'cycle-missing-normalized' {$normalizedJson=''}
    'cycle-malformed' {$normalizedJson='{not-json'}
    'cycle-array-root' {$normalizedJson='['+$normalizedJson+']'}
}
$route.selected_route_changes=$expectedRoutes -join ','
foreach($side in @('','full_','compact_')) {
    $route["coordinator_snapshot_route_consistency_${side}normalized_json"]=$normalizedJson
    $route["coordinator_snapshot_route_consistency_${side}boundary_hash"]=Get-RouteAcceptanceEvidenceTextSha256 -Value $normalizedJson
}
switch($Mode) {
    'cycle-conflicting-csv' {$route.selected_route_changes='file_tuna_v4'}
    'cycle-wrong-hash' {$route.coordinator_snapshot_route_consistency_boundary_hash='f'*64}
    'cycle-full-compact-conflict' {
        $route.coordinator_snapshot_route_consistency_compact_normalized_json=$normalizedJson+' '
        $route.coordinator_snapshot_route_consistency_compact_boundary_hash=Get-RouteAcceptanceEvidenceTextSha256 -Value ($normalizedJson+' ')
    }
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
Assert-Phase4RouteSummary -Result $result -ExpectedRouteChanges $expectedRoutes
if($result.coordinatorSnapshotFullCompactParityVerdict -cne $expectedVerdict -or
    $result.coordinatorSnapshotFullCompactParityFailureReason -cne $expectedReason) {
    throw "projection mismatch: expected=$expectedVerdict/$expectedReason actual=$($result.coordinatorSnapshotFullCompactParityVerdict)/$($result.coordinatorSnapshotFullCompactParityFailureReason)"
}
if($expectedVerdict -in @('pass','fail')) {
    if($result.coordinatorSnapshotFullBoundaryHash -cne $parity.FullBoundaryHash -or
        $result.coordinatorSnapshotCompactBoundaryHash -cne $parity.CompactBoundaryHash) {throw 'original parity hashes not projected'}
} elseif($result.coordinatorSnapshotFullBoundaryHash -cne '' -or $result.coordinatorSnapshotCompactBoundaryHash -cne '') {throw 'invalid projection retained hashes'}
if($Mode -in @('route-failure','cycle-peer-disagreement')) {
    if(-not ($result.failures -contains 'route consistency verdict is fail')) {throw 'reporting erased route failure'}
} elseif($Mode -ceq 'cycle-wrong-protocol') {
    if(-not ($result.failures -contains 'route selected protocol mismatch: route=file_tuna_v4; expected 4; actual 6')) {throw 'publication protocol was not checked'}
} elseif($Mode -in @('cycle-forbidden-tuna-v6','cycle-forbidden-regular-v6')) {
    $message=if($Mode -ceq 'cycle-forbidden-tuna-v6') {'active file_tuna_v6 route is not allowed during acceptance'} else {'diagnostic_regular_nkn_v6 route is not allowed during acceptance'}
    if(-not ($result.failures -contains $message)) {throw 'forbidden publication route was not checked'}
} elseif(($expectedVerdict -cne 'pass' -or $Mode -ceq 'route-family-failure' -or
    ($Mode.StartsWith('cycle-', [StringComparison]::Ordinal) -and $Mode -cne 'cycle-interleaved')) -and $Mode -cne 'legacy') {
    if(-not ($result.failures -contains 'coordinator normalized route cycle evidence invalid')) {throw 'invalid logical cycle was not rejected'}
} elseif($result.failures.Count -ne 0 -or ($result.selectedRouteSequence -join ',') -cne ($expectedRoutes -join ',')) {
    throw "logical route comparison mismatch: $($result.failures -join ',')"
}
if($Mode -ceq 'legacy' -and $result.coordinatorSnapshotMode -cne 'LegacyV0') {throw 'parity upgraded legacy mode'}
foreach($name in $original.Keys) {
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $Directory $name))) -cne $original[$name]) {throw 'original primary artifact mutated'}
}
Write-Output 'snapshot-parity-reporting-ok'
