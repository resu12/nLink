param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-Effect')) {
    $definition = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definition.Count -ne 1) { throw "Definition not unique: $name" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function New-Lines([object[]]$values) {
    "event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none"
    $sequence = 0L
    foreach ($e in $values) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($e | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence++
        "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($e.transitionId); before_state_generation=$($e.aggregate.beforeStateGeneration); after_state_generation=$($e.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
        $stages = @{}
        if ($e.decisionKind -ceq 'FallbackTailQuiescent') { $stages = @{ yield=@('recovery_owner_retired_after_yield') } }
        elseif ($e.decisionKind -ceq 'ApplyFallbackTailLocalProofReceipt') { $stages = @{ local_proof=@('local_proof_sent','local_proof_applied') } }
        elseif ($e.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt') { $stages = @{ peer_proof=@('peer_proof_observed'); paired_release=@('paired_release_published') } }
        foreach ($o in @($e.aggregate.operations | Where-Object operationRole -CEQ 'yield_current')) {
            foreach ($stage in $stages.Keys) {
                $receipts = @($o.fallbackTailYield.receipts | Where-Object stage -CEQ $stage)
                if ($receipts.Count -eq 1) {
                    foreach ($kind in $stages[$stage]) { $sequence++; New-Effect $e $o $kind $sequence $receipts[0] }
                }
            }
        }
    }
    $last = $values[-1]
    "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none"
}
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Assert-Parity($values, [bool]$valid, [string]$label) {
    $script:first = $values[0]
    $lines = @(New-Lines @($values))
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid) {
        throw "$label failed: $($full.ArtifactEvidenceFailureReason)"
    }
}
Assert-Parity $envelopes $true 'exact rollback successor'
foreach ($mutation in @(
    @{ Name='foreign predecessor'; Edit={param($e) $e[-1].aggregate.operations[0].predecessorAuthorityId='foreign'} },
    @{ Name='skipped generation'; Edit={param($e) $e[-1].aggregate.operations[0].operationGeneration++} },
    @{ Name='coherent skipped generation'; Edit={param($e)
        $o=$e[-1].aggregate.operations[0]; $o.operationGeneration++; $o.commandGeneration++; $o.receiptGeneration++
        $o.operationKey=@($o.family,$o.operationRole,$o.authorityId,$o.operationGeneration,$o.operationKind) -join '|'
        $o.fallbackTailYield.receipts[0].commandGeneration++; $o.fallbackTailYield.receipts[0].receiptGeneration++
    } },
    @{ Name='unrelated reason'; Edit={param($e) $e[-1].decisionReason='unrelated'} },
    @{ Name='foreign scope'; Edit={param($e) $e[-1].aggregate.operations[0].fallbackTailYield.sourceBoundary.dataSessionGeneration++} },
    @{ Name='missing retained predecessor'; Edit={param($e) $e[-2].aggregate.operations=@($e[-2].aggregate.operations | Where-Object operationRole -CNE 'yield_retained_predecessor')} },
    @{ Name='wrong rollback kind'; Edit={param($e) $e[-2].decisionKind='InvalidateFallbackTailYield'} },
    @{ Name='wrong rollback receipt'; Edit={param($e) (@($e[-2].aggregate.operations | Where-Object operationRole -CEQ 'yield_terminal')[0]).receiptFingerprint='foreign'} },
    @{ Name='wrong retained receipt'; Edit={param($e) (@($e[-2].aggregate.operations | Where-Object operationRole -CEQ 'yield_retained_predecessor')[0]).receiptFingerprint='foreign'} },
    @{ Name='foreign pair'; Edit={param($e) $e[-1].aggregate.operations[0].fallbackTailYield.pairKey='foreign'} },
    @{ Name='invented local proof'; Edit={param($e) $e[-1].aggregate.operations[0].fallbackTailYield.localProofIdentity='foreign'} },
    @{ Name='invented consuming install'; Edit={param($e) $e[-1].aggregate.operations[0].fallbackTailYield.consumingInstallGeneration=1} },
    @{ Name='duplicate successor'; Edit={param($e) $e[-1].aggregate.operations+=Copy-Value $e[-1].aggregate.operations[0]} }
)) {
    $values = Copy-Value $envelopes
    & $mutation.Edit $values
    Assert-Parity $values $false $mutation.Name
}
Assert-Parity @($envelopes[-1]) $false 'orphan successor'
Assert-Parity @($envelopes[-2],$envelopes[-1]) $false 'unproven paired origin'
Assert-Parity @($envelopes | Where-Object decisionKind -CNE 'SupersedeFallbackTailPairedYieldAfterRejectedProbe') $false 'missing rollback'
Write-Output 'Exact rollback successor full/compact parity and negative controls passed.'
