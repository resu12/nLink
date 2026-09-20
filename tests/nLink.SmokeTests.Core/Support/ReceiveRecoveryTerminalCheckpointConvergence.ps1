param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function History-Entries($values) {
    for ($i = 0; $i -lt $values.Count; $i++) {
        $e = $values[$i]
        [pscustomobject]@{
            Boundary = [pscustomobject]@{
                coordinatorInstanceId = $e.coordinatorInstanceId; writer = 'fixture-writer'; boundaryId = $e.transitionId
                beforeStateGeneration = $e.aggregate.beforeStateGeneration; afterStateGeneration = $e.aggregate.afterStateGeneration
                beforeStateHash = $e.aggregate.beforeStateFingerprint; afterStateHash = $e.aggregate.afterStateFingerprint
                decisionKind = $e.decisionKind; decisionStatus = $e.decisionStatus; decisionReason = $e.decisionReason
                operations = $e.aggregate.operations; evidenceLineIndex = $i
            }
            BeforeBoundary = $e.aggregate.beforeBoundary; AfterBoundary = $e.aggregate.afterBoundary
            SessionId = $e.sessionId; TransferId = $e.transferId; Direction = $e.direction
        }
    }
}
function Root-Lifecycle($history) {
    $roots = @(foreach ($entry in $history) {
        foreach ($operation in $entry.Boundary.operations) {
            if ($operation.family -ceq 'receive_recovery' -and
                $operation.operationRole -in @('operation_current','operation_terminal')) {
                [pscustomobject]@{ Boundary = $entry.Boundary; Operation = $operation }
            }
        }
    })
    Get-FileTransferCoordinatorReceiveRecoveryRootLifecycle $roots `
        (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex $history) @{}
}
$history = @(History-Entries $envelopes)
$baseline = Root-Lifecycle $history[0..($history.Count-2)]
if ($baseline.Errors.Count -ne 0) { throw "Pre-replacement lifecycle invalid: $($baseline.Errors)" }
$result = Root-Lifecycle $history
if ($envelopes[0].direction -ceq 'outbound') {
    if ($result.Errors.Count -ne 1 -or $result.Errors[0] -notlike 'snapshot_receive_recovery_root_lifecycle_invalid:*') {
        throw 'Receiver-only checkpoint convergence admitted an outbound terminal.'
    }
    Write-Output 'Outbound convergence remains outside the receiver-only contract.'
    return
}
if ($result.Errors.Count -ne 0) { throw "Accepted leg replacement rejected: $($result.Errors)" }
if ($result.Latest.Operation.afterState -cne 'Invalidated:None' -or
    (Copy-Value $baseline.Poisoned | ConvertTo-Json -Depth 64 -Compress) -cne
        (Copy-Value $result.Poisoned | ConvertTo-Json -Depth 64 -Compress)) {
    throw 'Checkpoint convergence changed terminal authority or a poisoned obligation.'
}
$prior = $baseline.Latest.Operation
$target = $history[-1]
$terminal = $result.Latest.Operation
$mutations = [ordered]@{
    'receipt' = { param($p,$o,$e) $o.receiptFingerprint += '-foreign' }
    'proof' = { param($p,$o,$e) $o.receiveRecovery.proofIdentity += '-foreign' }
    'command' = { param($p,$o,$e) $o.commandFingerprint += '-foreign' }
    'authority' = { param($p,$o,$e) $o.authorityId += '-foreign' }
    'operation-generation' = { param($p,$o,$e) $o.operationGeneration++ }
    'current-role' = { param($p,$o,$e) $o.operationRole = 'operation_current' }
    'not-applied' = { param($p,$o,$e) $o.stateApplied = $false }
    'completed' = { param($p,$o,$e) $o.afterState = 'Completed:None' }
    'asymmetric-checkpoint' = { param($p,$o,$e) $o.receiveRecovery.effectiveBoundary.checkpointGeneration = 1 }
    'prior-asymmetric-checkpoint' = { param($p,$o,$e) $p.receiveRecovery.effectiveBoundary.checkpointGeneration++ }
    'new-request' = { param($p,$o,$e) $o.receiveRecovery.sourceBoundary.checkpointRequestId = 'foreign' }
    'prior-request' = { param($p,$o,$e) $p.receiveRecovery.sourceBoundary.checkpointRequestId = 'foreign' }
    'root-data' = { param($p,$o,$e) $o.receiveRecovery.sourceBoundary.dataSessionIdentity += '-foreign' }
    'root-route' = { param($p,$o,$e) $o.receiveRecovery.sourceBoundary.route = 'file_tuna_v4' }
    'root-transport' = { param($p,$o,$e) $o.receiveRecovery.sourceBoundary.transportEpoch++ }
    'child-generation' = { param($p,$o,$e) $o.receiveRecovery.checkpointBindingGeneration++ }
    'wrong-decision' = { param($p,$o,$e) $e.Boundary.decisionKind = 'ObserveReceiveRecoveryProof' }
    'rejected' = { param($p,$o,$e) $e.Boundary.decisionStatus = 'Rejected' }
    'state-gap' = { param($p,$o,$e) $e.Boundary.afterStateGeneration++ }
    'same-state-hash' = { param($p,$o,$e) $e.Boundary.afterStateHash = $e.Boundary.beforeStateHash }
    'foreign-direction' = { param($p,$o,$e) $e.Direction = 'outbound' }
    'aggregate-data' = { param($p,$o,$e) $e.AfterBoundary.dataSessionIdentity += '-foreign' }
    'aggregate-protocol' = { param($p,$o,$e) $e.AfterBoundary.protocolVersion = 4 }
    'aggregate-bridge' = { param($p,$o,$e) $e.AfterBoundary.bridgeRecoveryGeneration++ }
    'aggregate-checkpoint' = { param($p,$o,$e) $e.BeforeBoundary.checkpointGeneration = 1 }
    'leg-gap' = { param($p,$o,$e) $e.AfterBoundary.legGeneration++ }
    'runtime-gap' = { param($p,$o,$e) $e.AfterBoundary.runtimeGeneration++ }
    'publication-gap' = { param($p,$o,$e) $e.AfterBoundary.routePublicationGeneration++ }
    'missing-retirement' = { param($p,$o,$e) $e.Boundary.operations = @($e.Boundary.operations | Where-Object operationRole -CNE 'checkpoint_acceptance_retained') }
    'retirement-receipt' = { param($p,$o,$e) ($e.Boundary.operations | Where-Object operationRole -CEQ 'checkpoint_acceptance_retained').receiptFingerprint += '-foreign' }
    'retained-checkpoint' = { param($p,$o,$e) ($e.Boundary.operations | Where-Object operationRole -CEQ 'checkpoint_acceptance_retained').checkpointRepairContinuity.currentScope.retainedLegCheckpointGeneration++ }
    'missing-owner' = { param($p,$o,$e) $e.Boundary.operations = @($e.Boundary.operations | Where-Object { -not ($_.operationRole -ceq 'runtime_owner' -and $_.afterState -ceq 'Current') }) }
    'duplicate-publication' = { param($p,$o,$e) $e.Boundary.operations += Copy-Value ($e.Boundary.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published' }) }
    'owner-scope' = { param($p,$o,$e) ($e.Boundary.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.afterState -ceq 'Current' }).routeRuntime.boundary.runtimeGeneration++ }
    'publication-receipt' = { param($p,$o,$e) ($e.Boundary.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published' }).receiptFingerprint += '-foreign' }
    'retired-owner-token' = { param($p,$o,$e) ($e.Boundary.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.afterState -ceq 'None' }).commandFingerprint += '-foreign' }
}
foreach ($case in $mutations.Keys) {
    $p = Copy-Value $prior; $o = Copy-Value $terminal; $e = Copy-Value $target
    & $mutations[$case] $p $o $e
    if (Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry $p $o $e.Boundary $e) {
        throw "Malformed checkpoint convergence admitted: $case"
    }
}

# Use existing fixture line emitters, not their frozen catalog or test bodies.
function Import-Definitions([string]$path, [string[]]$names) {
    $ast = [Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$null)
    foreach ($name in $names) {
        $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
        if ($definitions.Count -ne 1) { throw "Function not unique: $name" }
        . ([scriptblock]::Create(($definitions[0].Extent.Text -replace "function $name", "function script:$name")))
    }
}
Import-Definitions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1') @('New-Effect','New-Lines')
$script:NewRouteLines = (Get-Command New-Lines).ScriptBlock
Import-Definitions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/ReceiveRecoveryRouteCarrySnapshotEvidence.ps1') @('New-Lines')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorActivationRetryCatalogVersion
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
$lines = @(New-Lines $envelopes)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $compact.ArtifactEvidenceVerdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass') {
    throw "Fresh full/compact convergence rejected: $($full.ArtifactEvidenceFailureReason)"
}
foreach ($evaluation in @($full, $compact)) {
    $route = Get-FileTransferCoordinatorSnapshotRouteConsistency $evaluation
    if ($route.Verdict -cne 'pass') { throw "Converged terminal lacks valid publication history: $($route.Findings)" }
}
foreach ($case in @('missing-retirement','prior-hash-gap')) {
    $values = Copy-Value $envelopes
    if ($case -ceq 'missing-retirement') {
        $values[-1].aggregate.operations = @($values[-1].aggregate.operations | Where-Object operationRole -CNE 'checkpoint_acceptance_retained')
    }
    else { $values[-2].aggregate.beforeStateFingerprint += '-gap' }
    $bad = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(New-Lines $values)
    if ($bad.ArtifactEvidenceVerdict -cne 'fail') { throw "Malformed full history admitted: $case" }
}
Write-Output "Exact terminal convergence, $($mutations.Count) negative controls and fresh full/compact parity passed."
