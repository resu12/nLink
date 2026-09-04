param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot,
    [string]$FreshFailedTransitionId, [string]$QueuedRecheckTransitionId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen catalog changed.'
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json

# Reuse the existing exact peer-origin effect and full/compact/GUI parity harness.
foreach ($source in @(
    @{
        Path='tests/nLink.SmokeTests.Core/Support/PeerFirstFailedYieldSnapshotEvidence.ps1'
        Names=@('Copy-Value','Subject','New-Effect','Convert-Envelope','New-Lines','Assert-Parity')
    },
    @{
        Path='tools/GuiSmoke-Windows.ps1'
        Names=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')
    })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $RepoRoot $source.Path),[ref]$null,[ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Definition not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}

$acceptedValues = @($envelopes | Where-Object {
    [string]$_.transitionId -ceq $FreshFailedTransitionId -and
        [string]$_.decisionKind -ceq 'FallbackTailFailed' -and
        [string]$_.decisionStatus -ceq 'Accepted'
})
if ($acceptedValues.Count -ne 1) {
    throw "Fresh Failed envelope not unique: expected_transition_id=$FreshFailedTransitionId; matching_count=$($acceptedValues.Count); fixture_count=$($envelopes.Count)."
}
$accepted = $acceptedValues[0]
$current = Subject $accepted 'yield_current'
$peerFirst = [string]$current.afterState -ceq 'PeerProofObserved'
$oldRechecks = @($envelopes | Where-Object {
    [string]$_.transitionId -ceq $QueuedRecheckTransitionId -and
        [string]$_.coordinatorInstanceId -ceq [string]$accepted.coordinatorInstanceId -and
        [string]$_.decisionKind -ceq 'ApplyFallbackTailYieldRetryDispatchRecheckReceipt' -and
        [string]$_.decisionStatus -ceq 'Accepted'
} | ForEach-Object {
    $_.aggregate.operations | Where-Object {
        [string]$_.operationRole -ceq 'retry_recheck_terminal' -and
            [string]$_.beforeState -ceq 'None' -and [string]$_.afterState -ceq 'Queued'
    }
})
if ($oldRechecks.Count -ne 1 -or
    -not [bool]$oldRechecks[0].receiptValidated -or
    [string]$oldRechecks[0].authorityId -ceq [string]$current.authorityId -or
    [long]$oldRechecks[0].operationGeneration -le 0) {
    throw 'Exact historical queued recheck not retained under its original owner.'
}
$oldRecheck = $oldRechecks[0]
$oldJson = $oldRecheck | ConvertTo-Json -Depth 64 -Compress
if ([long]$current.fallbackTailYield.retryRecheckGeneration -ne 0 -or
    @($accepted.aggregate.operations | Where-Object {
        [string]$_.operationRole -ceq 'retry_recheck_terminal'
    }).Count -ne 0 -or
    @($accepted.aggregate.operations).Count -ne $(if ($peerFirst) { 2 } else { 1 })) {
    throw 'Fresh Failed snapshot inherited historical recheck ownership.'
}

$attempts = @($envelopes | Where-Object {
    [string]$_.decisionKind -ceq 'FallbackTailFailed' -and
        [string]$_.coordinatorInstanceId -ceq [string]$accepted.coordinatorInstanceId -and
        @($_.aggregate.operations | Where-Object {
            [string]$_.operationRole -ceq 'attempted_decision' -and
                [string]$_.operationKind -ceq 'FallbackTailFailed' -and
                [string]$_.authorityId -ceq [string]$current.authorityId -and
                [long]$_.operationGeneration -eq [long]$current.operationGeneration
        }).Count -eq 1
})
$idempotentValues = @($attempts | Where-Object decisionStatus -CEQ 'Idempotent')
$rejectedValues = @($attempts | Where-Object decisionStatus -CEQ 'Rejected')
if ($idempotentValues.Count -ne 1 -or $rejectedValues.Count -ne 1) {
    throw 'Exact fresh Failed authority attempts not unique.'
}
$idempotent = $idempotentValues[0]
$rejected = $rejectedValues[0]
$chain = @($accepted, $idempotent)
$acceptedIndex = 0
if ($peerFirst) {
    $retiredPending = Subject $accepted 'peer_proof_pending_initial'
    $origin = @($envelopes | Where-Object {
        [string]$_.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
            [string]$_.decisionStatus -ceq 'Accepted' -and
            [string]$_.coordinatorInstanceId -ceq [string]$accepted.coordinatorInstanceId -and
            [long]$_.aggregate.afterStateGeneration -eq [long]$accepted.aggregate.beforeStateGeneration -and
            @($_.aggregate.operations | Where-Object {
                [string]$_.operationKey -ceq [string]$retiredPending.operationKey -and
                    [string]$_.operationRole -ceq 'peer_proof_pending_initial' -and
                    [string]$_.afterState -ceq 'Pending'
            }).Count -eq 1
    })
    if ($origin.Count -ne 1) { throw 'Exact immediately preceding peer-proof origin missing.' }
    $chain = @($origin[0], $accepted, $idempotent)
    $acceptedIndex = 1
}
$first = $chain[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"

foreach ($envelope in @($accepted, $idempotent, $rejected)) {
    if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $envelope)) {
        throw "Valid Failed envelope was rejected: $($envelope.decisionStatus)"
    }
    foreach ($operation in @($envelope.aggregate.operations)) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $envelope $operation)) {
            throw 'Valid Failed operation was rejected.'
        }
    }
}
Assert-Parity (New-Lines $chain) $true
Assert-Parity (New-Lines @($rejected)) $true

$variant = 'fresh_failed_yield_rebound_unrelated_terminal_retry_recheck_receipt'
foreach ($mode in @('rebound-child-and-metadata', 'rebound-child-only', 'metadata-only')) {
    $bad = Copy-Value $accepted
    $badCurrent = Subject $bad 'yield_current'
    if ($mode -cne 'rebound-child-only') {
        $badCurrent.fallbackTailYield.retryRecheckGeneration = $oldRecheck.operationGeneration
    }
    if ($mode -cne 'metadata-only') {
        $rebound = Copy-Value $oldRecheck
        $rebound.authorityId = $badCurrent.authorityId
        $rebound.predecessorAuthorityId = '(none)'
        $rebound.operationKey = @('fallback_tail_yield','retry_recheck_terminal',
            $rebound.authorityId,$rebound.operationGeneration,'RetryDispatchRecheck') -join '|'
        $rebound.commandIdentity = "$($rebound.authorityId):retry_recheck_terminal:$($rebound.operationGeneration)"
        $rebound.receiptIdentity = "$($rebound.commandIdentity):receipt"
        $rebound.fallbackTailYield = Copy-Value $badCurrent.fallbackTailYield
        $rebound.fallbackTailYield.role = 'retry_recheck_terminal'
        $rebound.fallbackTailYield.retryRecheckGeneration = $oldRecheck.operationGeneration
        $rebound.fallbackTailYield.receipts = @(Copy-Value $oldRecheck.fallbackTailYield.receipts[0])
        $rebound.fallbackTailYield.receipts[0].commandIdentity = $rebound.commandIdentity
        $rebound.fallbackTailYield.receipts[0].receiptIdentity = $rebound.receiptIdentity
        $bad.aggregate.operations += $rebound
    }
    if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
        throw "Retained recheck leakage was admitted: $mode"
    }
    if ((Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant $bad) -cne $variant) {
        throw "Retained recheck leakage lost its precise diagnostic: $mode"
    }
    $badChain = @($chain | ForEach-Object { Copy-Value $_ })
    $badChain[$acceptedIndex] = $bad
    Assert-Parity (New-Lines $badChain) $false $variant
}

# Other malformed fresh-yield shapes remain rejected without being relabeled as recheck ownership.
$malformed = Copy-Value $accepted
$malformedCurrent = Subject $malformed 'yield_current'
$malformedCurrent.fallbackTailYield.sourceBoundary.dataSessionGeneration++
if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $malformed) {
    throw 'Fresh Failed scope mismatch was admitted.'
}
if ((Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant $malformed) -ceq $variant) {
    throw 'Unrelated malformed shape was relabeled as retained recheck ownership.'
}
foreach ($missing in @('typed-null', 'typed-missing', 'generation-missing')) {
    $bad = Copy-Value $accepted
    $badCurrent = Subject $bad 'yield_current'
    if ($missing -ceq 'typed-null') {
        $badCurrent.fallbackTailYield = $null
    }
    elseif ($missing -ceq 'typed-missing') {
        $badCurrent.PSObject.Properties.Remove('fallbackTailYield')
    }
    else {
        $badCurrent.fallbackTailYield.PSObject.Properties.Remove('retryRecheckGeneration')
    }
    if ((Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant $bad) -ceq $variant) {
        throw "Missing metadata was relabeled as retained recheck ownership: $missing"
    }
    if ($missing -cne 'typed-missing' -and
        (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad)) {
        throw "Missing Failed metadata was admitted: $missing"
    }
}
if ($peerFirst) {
    Assert-Parity (New-Lines $chain 'missing-origin') $false `
        'peer_first_failed_yield_peer_proof_provenance_invalid'
    Assert-Parity (New-Lines $chain 'copied-peer') $false `
        'peer_first_failed_yield_emitted_copied_authority_effect'
}
if (($oldRecheck | ConvertTo-Json -Depth 64 -Compress) -cne $oldJson) {
    throw 'Malformed projection controls mutated the original receipt fixture.'
}
Write-Output 'Retained recheck owner: strict local/peer-first catalog and full/compact/shared-GUI parity passed.'
