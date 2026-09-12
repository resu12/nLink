param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$version = $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $version
$captured = [IO.File]::ReadAllLines($InputPath)
if ($captured[0] -cnotmatch ([regex]::Escape('snapshot_catalog_version=' + $version) + ';')) { throw 'correlation profile not declared' }
$frozen = [ordered]@{
    'snapshot-authority-v1'='17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2'
    'snapshot-authority-v1-receiver-barrier-v1'='e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94'
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1'='e6c3b5ff7cf8b44bcde0782ec325d2623cb96ba8ff362edca1902f9a1929059d'
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1'='ebbaf19d291129caa47b34e69395de2b38985661560791c7c762c437ed6e679b'
}
foreach ($name in $frozen.Keys) {
    $old = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $name
    if ($old.Hash -cne $frozen[$name] -or ($old.RequiredFamilies -join ',') -cne ($catalog.RequiredFamilies -join ',')) { throw 'Frozen catalog changed' }
}
if ($catalog.ParentCatalogHash -cne $frozen[$script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion] -or
    $catalog.Hash -cne '40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db') { throw 'Extension hash mismatch' }
$marker = [pscustomobject]@{operationKind='FallbackBootstrapPhase';receiptValidated=$true}
if ((Test-FileTransferCoordinatorSnapshotEffectScope $marker ([pscustomobject]@{})) -or
    (Test-FileTransferCoordinatorSnapshotEffectReceipt $marker ([pscustomobject]@{}))) { throw 'Correlation evidence granted physical authority' }
$script:FileTransferCoordinatorBootstrapCorrelationCatalog.copyFields.extra = 'string'
try {
    $rejected = $false
    try { $null = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $version }
    catch { $rejected = $_.Exception.Message -cmatch '^coordinator_snapshot_bootstrap_correlation_catalog_hash_invalid:' }
    if (-not $rejected) { throw 'Schema changed without a new hash' }
} finally { $script:FileTransferCoordinatorBootstrapCorrelationCatalog.copyFields.Remove('extra') }

$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'GUI parser error' }
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
function Set-Field([string]$Line, [string]$Name, [string]$Value) {
    [regex]::Replace($Line, "(^|; )$([regex]::Escape($Name))=[^;]*", {param($match) $match.Groups[1].Value + $Name + '=' + $Value})
}
function Replace-Envelope([string]$Line, $Envelope) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $line = Set-Field $Line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
    Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
}
$normalized = [Collections.Generic.List[string]]::new()
foreach ($case in @('exact','enabled_version_zero','enabled_by_invalidation','enabled_by_other_direction',
        'enabled_from_wrong_state','hidden_enablement',
        'wrong_phase_decision','hidden_phase_progress','missing_agreement','wrong_phase_agreement','missing_pinned_agreement',
        'rebound_pinned_agreement',
        'missing_proof_copy','pending_settlement','duplicate_copy','wrong_receipt_command',
        'wrong_copy_epoch','empty_settlement','missing_completed_phase','attempt_applied','old_base','old_barrier','old_continuity','old_correlation')) {
    $lines = [string[]]$captured.Clone()
    $mutated = $case -ceq 'exact'
    $pinnedAgreement = $null
    if ($case.StartsWith('old_')) {
        $profile = switch ($case) { 'old_base' { @($frozen.Keys)[0] } 'old_barrier' { @($frozen.Keys)[1] } 'old_continuity' { @($frozen.Keys)[2] } default { @($frozen.Keys)[3] } }
        for ($i=0; $i -lt $lines.Count; $i++) {
            $lines[$i] = Set-Field (Set-Field $lines[$i] 'snapshot_catalog_version' $profile) 'snapshot_catalog_hash' $frozen[$profile]
        }
        $mutated = $true
    }
    for ($i=0; -not $mutated -and $i -lt $lines.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
        $enabledAgreements = @($envelope.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'fallback_bootstrap_agreement' -and $_.afterState -ceq 'Enabled'
            })
        foreach ($operation in $enabledAgreements) { $pinnedAgreement = $operation }
        $phase = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_phase' -and $_.afterState -ceq 'Ready' } | Select-Object -First 1)
        $attempt = @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_attempt')
        if ($case -cin @('enabled_version_zero','enabled_by_invalidation','enabled_by_other_direction',
                'enabled_from_wrong_state','hidden_enablement') -and $enabledAgreements.Count -eq 1) {
            switch ($case) {
                'enabled_version_zero' { $pinnedAgreement.routeTransition.bootstrapCorrelation.version = 0 }
                'enabled_by_invalidation' { $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange' }
                'enabled_by_other_direction' {
                    $envelope.decisionKind = if ($envelope.direction -ceq 'outbound') { 'ApplyFallbackBootstrapSessionOpen' }
                        else { 'ApplyFallbackBootstrapAccept' }
                }
                'enabled_from_wrong_state' { $pinnedAgreement.beforeState = 'None' }
                'hidden_enablement' { $pinnedAgreement.beforeState = 'Enabled' }
            }
            $lines[$i] = Set-Field $lines[$i] 'decision_kind' $envelope.decisionKind
            $mutated = $true
        }
        elseif ($case -ceq 'missing_pinned_agreement' -and @($envelope.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'fallback_bootstrap_agreement' -and $_.afterState -ceq 'Enabled'
            }).Count -eq 1) {
            $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object operationRole -CNE 'fallback_bootstrap_agreement')
            $mutated = $true
        }
        elseif ($case -ceq 'attempt_applied' -and $attempt.Count -eq 1) { $attempt[0].stateApplied = $true; $mutated = $true }
        elseif ($phase.Count -eq 1 -and $case -cne 'attempt_applied') {
            $payload = $phase[0].routeTransition.bootstrapCorrelation
            switch ($case) {
                'wrong_phase_decision' {
                    if (@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_v6_bootstrap').Count -eq 0 -and
                        $envelope.decisionKind -ceq 'ApplyFallbackBootstrapPhaseReceipt') {
                        $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange'
                        $lines[$i] = Set-Field $lines[$i] 'decision_kind' $envelope.decisionKind
                        $mutated = $true
                    }
                }
                'hidden_phase_progress' {
                    if (@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_v6_bootstrap').Count -eq 0 -and
                        $envelope.decisionKind -ceq 'ApplyFallbackBootstrapPhaseReceipt') {
                        $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange'
                        $phase[0].beforeState = $phase[0].afterState
                        $lines[$i] = Set-Field $lines[$i] 'decision_kind' $envelope.decisionKind
                        $mutated = $true
                    }
                }
                'missing_agreement' { $payload.agreementFingerprint = '(none)'; $mutated = $true }
                'wrong_phase_agreement' { $payload.agreementFingerprint = 'fte1-' + ('a' * 24); $mutated = $true }
                'rebound_pinned_agreement' {
                    if ($null -eq $pinnedAgreement) { throw 'Missing pinned agreement fixture' }
                    $pinnedAgreement.beforeState = 'Enabled'
                    $pinnedAgreement.commandFingerprint = 'fte1-' + ('a' * 24)
                    $pinnedAgreement.routeTransition.bootstrapCorrelation.agreementFingerprint = $pinnedAgreement.commandFingerprint
                    $envelope.aggregate.operations = @($envelope.aggregate.operations) + @($pinnedAgreement)
                    foreach ($operation in @($envelope.aggregate.operations | Where-Object {
                            $_.operationRole -cin @('fallback_bootstrap_exchange','fallback_bootstrap_phase')
                        })) { $operation.routeTransition.bootstrapCorrelation.agreementFingerprint = $pinnedAgreement.commandFingerprint }
                    $mutated = $true
                }
                'missing_proof_copy' { $payload.deliveryCopies = @($payload.deliveryCopies | Where-Object frameType -CEQ 'filetransfer.transport_epoch.v6'); $mutated = $true }
                'pending_settlement' { $payload.settlementCopies[0].outcome = 0; $mutated = $true }
                'duplicate_copy' { $payload.settlementCopies = @($payload.settlementCopies) + @($payload.settlementCopies[0]); $mutated = $true }
                'wrong_receipt_command' { $phase[0].routeTransition.receipts[0].commandIdentity = 'fte1-' + ('a' * 24); $mutated = $true }
                'wrong_copy_epoch' { $payload.settlementCopies[0].transportEpoch++; $mutated = $true }
                'empty_settlement' { $payload.settlementCopies = @(); $mutated = $true }
                'missing_completed_phase' {
                    if (@($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.afterState -ceq 'Completed' }).Count -eq 1) {
                        $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object operationRole -CNE 'fallback_bootstrap_phase')
                        # Retain issue/delivery evidence, but remove readiness from prior snapshots too.
                        for ($j=1; $j -lt $i; $j++) {
                            $prior = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$j] 'snapshot_payload')).Value
                            $prior.aggregate.operations = @($prior.aggregate.operations | Where-Object {
                                $_.operationRole -cne 'fallback_bootstrap_phase' -or $_.afterState -cne 'Ready'
                            })
                            $lines[$j] = Replace-Envelope $lines[$j] $prior
                        }
                        $mutated = $true
                    }
                }
            }
        }
        if ($mutated) { $lines[$i] = Replace-Envelope $lines[$i] $envelope }
    }
    if (-not $mutated) { throw "No mutation target for $case" }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    $records = @(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i];LineIndex=$i} })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $inbound = $full.Boundaries[0].direction -ceq 'inbound'
    $expected = if ($case -ceq 'exact' -and -not $inbound) { 'pass' } else { 'fail' }
    if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
        throw "$case expected $expected : $($full.ArtifactEvidenceFailureReason)"
    }
    if ($case -cne 'exact' -and $full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_(bootstrap_correlation|bootstrap_initiation|operation_catalog_mismatch)') {
        throw "$case failed before the intended correlation boundary: $($full.ArtifactEvidenceFailureReason)"
    }
    if ($case -ceq 'rebound_pinned_agreement' -and
        $full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_bootstrap_correlation_agreement_mutated:') {
        throw 'Rebinding did not reach the pinned-agreement immutability check'
    }
    if ($case -ceq 'exact') {
        if (@(Get-FileTransferBootstrapCorrelationHistoryErrors @($full.Boundaries)).Count -ne 0) { throw 'Exact correlation history rejected' }
        # Modeled Coordinator history contains no physical responder-handoff effects. Do not waive that independent gate.
        $independent = @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ })
        if (($inbound -and $independent.Count -ne 4) -or
            @($independent | Where-Object { $_ -cnotmatch '^snapshot_responder_probe_handoff_required_effect_missing:' }).Count -ne 0) {
            throw "Unexpected independent evidence finding: $($full.ArtifactEvidenceFailureReason)"
        }
    }
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) {
        throw "$case full/compact parity failed"
    }
    $guiPath = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($case + '-gui.log')
    [IO.File]::WriteAllLines($guiPath, $lines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    $boundary = $full.FirstUnresolvedBoundary
    if ($case -ceq 'exact' -and -not $inbound) {
        # Negotiation and activation are now captured, but modeled receipts are still not physical route acceptance.
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        $expectedFindings = @(
            '^snapshot route authority boundary is unresolved: class=filetransfer_route_transition_incomplete;'
            '^snapshot route authority boundary is unresolved: class=publication_authority_missing;'
            '^snapshot route publication has no unique current runtime owner: route=regular_nkn_v4_fast$'
            '^snapshot route publication has no exact committed predecessor transition: route=file_tuna_v4;'
        )
        foreach ($finding in $expectedFindings) {
            if (@($route.Findings | Where-Object { $_ -cmatch $finding }).Count -ne 1) { throw 'Modeled route findings changed' }
        }
        if ($route.Findings.Count -ne $expectedFindings.Count -or
            (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') {
            throw 'Modeled route fixture changed independent findings'
        }
        $boundary = $route.FirstUnresolvedBoundary
    }
    if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
        $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash) { throw "$case GUI boundary parity failed" }
    $reasons = [string[]]($full.ArtifactEvidenceFailureReason -split ';')
    [Array]::Sort($reasons, [StringComparer]::Ordinal)
    $normalized.Add("$case|$($full.ArtifactEvidenceVerdict)|$($full.BoundaryHash)|$($reasons -join ';')")
}
Write-Output ('bootstrap-correlation-normalized=' + ($normalized -join '/'))
Write-Output 'bootstrap-correlation-evidence-ok'
