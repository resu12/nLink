param([string]$RepoRoot, [string]$InputPath, [string]$OutputRoot,
    [Parameter(Mandatory = $true)][ValidateSet('handoff','barrier-and-history')][string]$CaseGroup)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
if ($catalog.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94') { throw 'Receiver catalog changed.' }
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Shared GUI function is not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Decode([string]$Line) {
    (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_payload)).Value
}
function Replace-Envelope([string]$Line, $Envelope) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    $Line.Replace((Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_sha256), $hash).Replace(
        (Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_payload), $payload)
}
function Exact-Subject($Envelope, [string]$Role, [string]$Kind) {
    $subjects = @($Envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'responder_probe_handoff' -and $_.operationRole -ceq $Role -and $_.operationKind -ceq $Kind
    })
    if ($subjects.Count -ne 1) { throw "Unmodified fixture subject is not unique: $Role/$Kind" }
    $subjects[0]
}
function Boundary-Semantics($Boundaries) {
    $values = @(foreach ($boundary in $Boundaries) {
        $copy = Copy-Value $boundary
        # Physical insertion may move a record. Reordering descriptors changes
        # the serialized payload SHA used as boundaryHash, not its authority.
        # Same-case full/compact and cross-host JSON/hash equality remain exact.
        $copy.PSObject.Properties.Remove('evidenceLineIndex')
        $copy.PSObject.Properties.Remove('boundaryHash')
        $copy.operations = @($copy.operations | Sort-Object operationKey)
        $copy
    })
    ConvertTo-Json -InputObject @($values | Sort-Object boundaryId) -Depth 100 -Compress
}
$original = [IO.File]::ReadAllLines($InputPath)
$snapshots = @(for ($i = 0; $i -lt $original.Count; $i++) {
    if ((Get-FileTransferCoordinatorSnapshotEventName $original[$i]) -ceq 'filetransfer_coordinator_transition_snapshot') {
        [pscustomobject]@{ Index=$i; Envelope=(Decode $original[$i]) }
    }
})
$checkpoints = @($snapshots | Where-Object { $_.Envelope.decisionKind -cin @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
    @($_.Envelope.aggregate.operations | Where-Object operationRole -CEQ 'handoff_terminal').Count -eq 1 -and
    @($_.Envelope.aggregate.operations | Where-Object operationRole -CEQ 'receiver_barrier_terminal').Count -eq 1 })
if ($checkpoints.Count -lt 2) { throw 'Expected genuine request and acceptance carrying both retained subjects.' }
$requests = @($checkpoints | Where-Object { $_.Envelope.decisionKind -ceq 'FallbackCheckpointRequested' })
if ($requests.Count -ne 1) { throw 'Explicit coexistence checkpoint request is not unique.' }
$requestScope = $requests[0].Envelope.aggregate.afterBoundary
$accepted = @($checkpoints | Where-Object { $_.Envelope.decisionKind -ceq 'FallbackCheckpointAccepted' -and
    $_.Envelope.aggregate.beforeBoundary.checkpointRequestId -ceq $requestScope.checkpointRequestId -and
    $_.Envelope.aggregate.beforeBoundary.checkpointGeneration -eq $requestScope.checkpointGeneration })
if ($accepted.Count -ne 1) { throw 'Checkpoint acceptance is not unique.' }
$candidate = $accepted[0]
$handoffBirths = @($snapshots | Where-Object { @($_.Envelope.aggregate.operations | Where-Object {
    $_.operationRole -ceq 'handoff_terminal' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Invalidated'
}).Count -eq 1 })
$barrierBirths = @($snapshots | Where-Object { @($_.Envelope.aggregate.operations | Where-Object {
    $_.operationRole -ceq 'receiver_barrier_terminal' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Invalidated'
}).Count -eq 1 })
if ($handoffBirths.Count -ne 1 -or $barrierBirths.Count -ne 1) { throw 'Expected both independently produced terminal births.' }
foreach ($checkpoint in $checkpoints) {
    $handoff = Exact-Subject $checkpoint.Envelope 'handoff_terminal' 'ResponderPeerProbeRouteTransitionHandoff'
    $barrier = Exact-Subject $checkpoint.Envelope 'receiver_barrier_terminal' 'InitialActivationReceiverBarrier'
    if ($handoff.authorityId -ceq $barrier.authorityId -or $handoff.operationKey -ceq $barrier.operationKey -or
        $handoff.receiptFingerprint -ceq $barrier.receiptFingerprint) { throw 'Independent subjects were collapsed.' }
    if (-not (Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry $checkpoint.Envelope $handoff)) {
        throw 'Exact checkpoint handoff rejected because the independent receiver barrier shares its family.'
    }
    $effects = @($original | Where-Object {
        (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' -and
        (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $checkpoint.Envelope.transitionId
    })
    if (@($effects | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'handoff_invalidated' }).Count -ne 1 -or
        @($effects | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ operation_key) -ceq $barrier.operationKey }).Count -ne 0) {
        throw 'Checkpoint must preserve its actual handoff carry effect and emit no receiver-barrier effect.'
    }
}
$cases = @('exact', 'reordered', 'exact_duplicate', 'duplicate_handoff', 'competing_handoff', 'duplicate_barrier', 'conflicting_barrier',
    'handoff_role', 'handoff_kind', 'handoff_authority', 'handoff_key', 'handoff_generation', 'handoff_command',
    'handoff_receipt', 'handoff_typed_receipt', 'handoff_source', 'handoff_effective_checkpoint', 'handoff_data_session',
    'barrier_role', 'barrier_kind', 'barrier_receipt', 'barrier_source', 'barrier_effective',
    'missing_handoff_birth', 'missing_barrier_birth', 'handoff_retirement_aba', 'barrier_retirement_aba',
    'generation_gap', 'terminal_poison', 'replacement', 'missing_handoff_effect', 'conflicting_snapshot')
$cases = @($cases | Where-Object {
    $handoffCase = $_ -cin @('reordered','exact_duplicate','duplicate_handoff','competing_handoff') -or
        ($_.StartsWith('handoff_', [StringComparison]::Ordinal) -and $_ -cne 'handoff_retirement_aba')
    $_ -ceq 'exact' -or $handoffCase -eq ($CaseGroup -ceq 'handoff')
})
if ($cases.Count -ne $(if ($CaseGroup -ceq 'handoff') { 16 } else { 17 })) { throw 'Coexistence case-group coverage changed.' }
$outputs = [Collections.Generic.List[object]]::new()
$baselineBoundaries = $null
$baselineRoute = $null
foreach ($case in $cases) {
    Write-Output "checkpoint-coexistence-case=$case"
    $lines = @($original)
    $item = if ($case -cin @('missing_handoff_birth','handoff_retirement_aba')) { $handoffBirths[0] }
        elseif ($case -cin @('missing_barrier_birth','barrier_retirement_aba')) { $barrierBirths[0] }
        else { $candidate }
    $envelope = Copy-Value $item.Envelope
    $handoffs = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'handoff_terminal' })
    $barriers = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' })
    $handoff = if ($handoffs.Count -eq 1) { $handoffs[0] } else { $null }
    $barrier = if ($barriers.Count -eq 1) { $barriers[0] } else { $null }
    switch -CaseSensitive ($case) {
        'reordered' { [array]::Reverse($envelope.aggregate.operations) }
        'duplicate_handoff' { $envelope.aggregate.operations += (Copy-Value $handoff) }
        'competing_handoff' {
            $other = Copy-Value $handoff
            $other.authorityId = 'fte1-' + ('a' * 24)
            $other.operationKey = $other.operationKey.Replace($handoff.authorityId, $other.authorityId)
            $envelope.aggregate.operations += $other
        }
        'duplicate_barrier' { $envelope.aggregate.operations += (Copy-Value $barrier) }
        'conflicting_barrier' {
            $other = Copy-Value $barrier
            $other.receiptFingerprint = 'fte1-' + ('d' * 24)
            $other.receiverBarrier.receipts[0].receiptFingerprint = $other.receiptFingerprint
            $envelope.aggregate.operations += $other
        }
        'handoff_role' { $handoff.operationRole = 'handoff_current'; $handoff.responderProbeHandoff.role = 'handoff_current' }
        'handoff_kind' { $handoff.operationKind = 'InitialActivationReceiverBarrier' }
        'handoff_authority' { $handoff.authorityId = 'fte1-' + ('b' * 24) }
        'handoff_key' { $handoff.operationKey += '-changed' }
        'handoff_generation' { $handoff.operationGeneration++ }
        'handoff_command' { $handoff.commandIdentity += '-changed' }
        'handoff_receipt' { $handoff.receiptFingerprint += '-changed' }
        'handoff_typed_receipt' { $handoff.responderProbeHandoff.receipts[0].receiptValidated = $false }
        'handoff_source' { $handoff.responderProbeHandoff.sourceBoundary.checkpointGeneration++ }
        'handoff_effective_checkpoint' { $handoff.responderProbeHandoff.effectiveBoundary.checkpointGeneration++ }
        'handoff_data_session' { $handoff.responderProbeHandoff.effectiveBoundary.dataSessionGeneration++ }
        'barrier_role' { $barrier.operationRole = 'receiver_barrier_current' }
        'barrier_kind' { $barrier.operationKind = 'ResponderPeerProbeRouteTransitionHandoff' }
        'barrier_receipt' { $barrier.receiptFingerprint += '-changed' }
        'barrier_source' { $barrier.receiverBarrier.sourceBoundary.dataSessionGeneration++ }
        'barrier_effective' { $barrier.receiverBarrier.effectiveBoundary.checkpointGeneration++ }
        'missing_handoff_birth' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $handoff }) }
        'missing_barrier_birth' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $barrier }) }
        'handoff_retirement_aba' { $handoff.afterState = 'None' }
        'barrier_retirement_aba' { $barrier.afterState = 'None' }
        'generation_gap' { $envelope.aggregate.beforeStateGeneration++ }
        'terminal_poison' { $envelope.aggregate.afterBoundary.terminalGeneration++ }
        'replacement' { $envelope.aggregate.afterBoundary.dataSessionGeneration++ }
        'conflicting_snapshot' { $handoff.responderProbeHandoff.probeId += '-conflict' }
    }
    if ($case -cnotin @('exact','exact_duplicate','missing_handoff_effect')) {
        $changed = Replace-Envelope $lines[$item.Index] $envelope
        if ($changed -ceq $lines[$item.Index]) { throw "Mutation did not change evidence: $case" }
        if ($case -ceq 'conflicting_snapshot') {
            $lines = @($lines[0..$item.Index]) + @($changed) + @($lines[($item.Index + 1)..($lines.Count - 1)])
        }
        else { $lines[$item.Index] = $changed }
    }
    if ($case -ceq 'exact_duplicate') {
        $lines = @($lines[0..$candidate.Index]) + @($lines[$candidate.Index]) + @($lines[($candidate.Index + 1)..($lines.Count - 1)])
    }
    if ($case -ceq 'missing_handoff_effect') {
        $lines = @($lines | Where-Object { -not (
            (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $candidate.Envelope.transitionId -and
            (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'handoff_invalidated') })
        if ($lines.Count -ne $original.Count - 1) { throw 'Expected exactly one genuine checkpoint handoff effect.' }
    }
    $fullLines = @('event=unrelated_fixture_diagnostic; note=independent_compaction') + $lines
    $compactRecords = @(for ($i = 0; $i -lt $fullLines.Count; $i++) {
        $name = Get-FileTransferCoordinatorSnapshotEventName $fullLines[$i]
        if ($catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name) {
            [pscustomobject]@{ Line=$fullLines[$i]; LineIndex=$i }
        }
    })
    if ($compactRecords.Count -ge $fullLines.Count) { throw 'Compact input was not independently selected.' }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
    $expected = if ($case -cin @('exact','reordered','exact_duplicate')) { 'pass' } else { 'fail' }
    if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
        throw "$case expected=$expected; full=$($full.ArtifactEvidenceFailureReason); compact=$($compact.ArtifactEvidenceFailureReason)"
    }
    $json = ConvertTo-Json -InputObject @($full.Boundaries) -Depth 100 -Compress
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $json -cne (ConvertTo-Json -InputObject @($compact.Boundaries) -Depth 100 -Compress)) { throw "$case normalized full/compact parity failed." }
    if ($full.LegacySelectionCount -ne 0) { throw "$case fell back to LegacyV0." }
    $boundary = $full.FirstUnresolvedBoundary
    if ($expected -ceq 'pass') {
        if (-not $full.StreamComplete) { throw "$case lost the complete production fixture stream." }
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') { throw "$case route-result parity failed." }
        $routeJson = ConvertTo-Json -InputObject @($route.Findings) -Depth 100 -Compress
        $semantics = Boundary-Semantics @($full.Boundaries)
        if ($case -ceq 'exact') { $baselineBoundaries = $semantics; $baselineRoute = $routeJson }
        elseif ($semantics -cne $baselineBoundaries -or $routeJson -cne $baselineRoute) { throw "$case changed an independent lifecycle finding." }
        if ($null -eq $boundary) { $boundary = $route.FirstUnresolvedBoundary }
    }
    $guiOutputs = @()
    foreach ($input in @([pscustomobject]@{ Name='full'; Lines=$fullLines }, [pscustomobject]@{ Name='compact'; Lines=@($compactRecords | ForEach-Object Line) })) {
        $path = Join-Path $OutputRoot ($input.Name + '-gui.log')
        [IO.File]::WriteAllLines($path, [string[]]$input.Lines)
        $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
        if ($null -eq $boundary) {
            if ($null -ne $gui) { throw "$case GUI invented a boundary." }
        }
        elseif ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
            throw "$case GUI normalized boundary differs from shared evaluation."
        }
        $guiOutputs += if ($null -eq $gui) { $null } else { [ordered]@{ BoundaryId=$gui.CoordinatorBoundaryId; BoundaryHash=$gui.CoordinatorBoundaryHash } }
    }
    $outputs.Add([ordered]@{ Case=$case; Verdict=$full.ArtifactEvidenceVerdict; Boundaries=$json; Gui=$guiOutputs })
}
'checkpoint-coexistence-normalized=' + (ConvertTo-Json -InputObject $outputs.ToArray() -Depth 100 -Compress)
'checkpoint-coexistence-evidence-ok'
