param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [switch]$Child)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
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
Import-Definitions (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1') @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
$results = [Collections.Generic.List[object]]::new()
function Assert-Parity([string]$name, [string[]]$lines, [string]$expectedId, [string]$expectedClass, [bool]$valid = $true) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compactLines = @($lines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid) {
        $bad = @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -match 'root_lifecycle_invalid:(\d+)$' } | ForEach-Object {
            $line = (@('event=unrelated') + $lines)[[int]($_ -replace '^.*:', '')]
            $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line snapshot_payload)).Value
            "$($envelope.decisionKind):$($envelope.aggregate.operations | Where-Object family -CEQ 'receive_recovery' | ConvertTo-Json -Depth 20 -Compress)"
        })
        throw "$name evidence/parity: $($full.ArtifactEvidenceFailureReason); $($bad -join ';')"
    }
    $boundary = $full.FirstUnresolvedBoundary
    if ($valid) {
        if ($expectedId -ceq '') {
            if ($null -ne $boundary) { throw "$name invented incident: $($boundary.boundaryId)/$($boundary.taxonomyClass)" }
        }
        elseif ($null -eq $boundary -or $boundary.boundaryId -cne $expectedId -or $boundary.taxonomyClass -cne $expectedClass -or
            $boundary.rawCount -ne 1 -or $boundary.distinctCount -ne 1 -or $boundary.recoveredCount -ne 0 -or $boundary.unresolvedCount -ne 1) {
            throw "$name selected wrong obligation: $($boundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Compress)"
        }
    }
    $path = Join-Path $OutputRoot "atomic-$($PSVersionTable.PSVersion.Major).log"
    [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($null -eq $boundary) {
        if ($null -ne $gui) { throw "$name GUI invented incident." }
    }
    elseif ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
        $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
        throw "$name GUI boundary differs."
    }
    $results.Add([ordered]@{name=$name;hash=$full.BoundaryHash;boundary=if ($null -ne $boundary) {
        $boundary | Select-Object * -ExcludeProperty operations,evidenceLineIndex
    } else { $null }})
}
$complete = @($envelopes | Where-Object decisionKind -CEQ 'ObserveReceiveRecoveryExecutionReceipt')[-1]
$expectedClass = 'filetransfer_receive_recovery_completed_unproven_successor_missing'
$terminal = $envelopes[-1]
if ($terminal.decisionKind -cne 'Terminalized') { throw 'Missing generated terminal boundary.' }
Assert-Parity 'terminal-poison' @(New-Lines $envelopes) $complete.transitionId $expectedClass
Assert-Parity 'open-completion' @(New-Lines $envelopes[0..($envelopes.Count-2)]) $complete.transitionId $expectedClass
$proofIndex = 0
while ($envelopes[$proofIndex].decisionKind -cne 'ObserveReceiveRecoveryProof') { $proofIndex++ }
Assert-Parity 'exact-proof-closes' @(New-Lines $envelopes[0..$proofIndex]) '' ''
$reversed = Copy-Value $envelopes
foreach ($envelope in $reversed) { [array]::Reverse($envelope.aggregate.operations) }
Assert-Parity 'reversed-atomic-roles' @(New-Lines $reversed) $complete.transitionId $expectedClass
$lines = @(New-Lines $envelopes)
Assert-Parity 'byte-identical-duplicates' (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $complete.transitionId $expectedClass

# Every mutation targets the same-authority atomic current retirement/terminal
# entrant, not the older terminal being evicted from the single terminal slot.
foreach ($mutation in @('missing-retirement','ambiguous-entrant','receipt','generation','source','data-session','before-state','aba')) {
    $values = Copy-Value $envelopes
    $last = $values[-1]
    $roots = @($last.aggregate.operations | Where-Object { $_.family -ceq 'receive_recovery' -and $_.operationRole -in @('operation_current','operation_terminal') })
    $retirement = @($roots | Where-Object operationRole -CEQ 'operation_current')[0]
    $entrant = @($roots | Where-Object afterState -CEQ 'Invalidated:None')[0]
    switch ($mutation) {
        'missing-retirement' { $last.aggregate.operations = @($last.aggregate.operations | Where-Object operationKey -CNE $retirement.operationKey) }
        'ambiguous-entrant' { $last.aggregate.operations += Copy-Value $entrant }
        'receipt' { $retirement.receiptFingerprint += '-mutated'; $retirement.receiveRecovery.receipts[0].receiptFingerprint = $retirement.receiptFingerprint }
        'generation' { $entrant.operationGeneration++; $entrant.operationKey = "receive_recovery|operation_terminal|$($entrant.authorityId)|$($entrant.operationGeneration)|$($entrant.operationKind)" }
        'source' { $entrant.receiveRecovery.sourceBoundary.transportEpoch++ }
        'data-session' { $entrant.receiveRecovery.effectiveBoundary.dataSessionIdentity += '-replacement' }
        'before-state' { $retirement.beforeState = 'Running:None' }
        'aba' { $retirement.beforeState='None'; $retirement.afterState='Completed:None' }
    }
    Assert-Parity $mutation @(New-Lines $values) '' '' $false
}
$values = Copy-Value $envelopes
$values[-1].aggregate.beforeStateFingerprint += '-gap'
Assert-Parity 'hash-gap' @(New-Lines $values) '' '' $false
$values = Copy-Value $envelopes
$values[-1].aggregate.afterStateGeneration++
Assert-Parity 'generation-gap' @(New-Lines $values) '' '' $false

$resultPath = Join-Path $OutputRoot "atomic-results-ps$($PSVersionTable.PSVersion.Major).json"
[IO.File]::WriteAllText($resultPath, ($results | ConvertTo-Json -Depth 64 -Compress))
if (-not $Child) {
    & pwsh -NoProfile -File $PSCommandPath -RepoRoot $RepoRoot -FixturePath $FixturePath -OutputRoot $OutputRoot -Child
    if ($LASTEXITCODE -ne 0) { throw 'PowerShell 7 regression failed.' }
    if ([IO.File]::ReadAllText($resultPath) -cne [IO.File]::ReadAllText((Join-Path $OutputRoot 'atomic-results-ps7.json'))) {
        $childResults = Get-Content -Raw (Join-Path $OutputRoot 'atomic-results-ps7.json') | ConvertFrom-Json
        $differences = @(for ($i=0; $i -lt $results.Count; $i++) {
            $left=$results[$i] | ConvertTo-Json -Depth 64 -Compress
            $right=$childResults[$i] | ConvertTo-Json -Depth 64 -Compress
            if ($left -cne $right) { "$left <> $right" }
        })
        if ($differences.Count -gt 0) { throw "PowerShell 5.1 and 7 normalized boundaries differ: $($differences -join ';')" }
    }
}
Write-Output "Atomic recovery roles: $($results.Count) full/compact/GUI cases passed on PowerShell $($PSVersionTable.PSVersion)."
