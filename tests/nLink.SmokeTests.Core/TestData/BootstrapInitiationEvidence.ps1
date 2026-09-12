param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog=Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion
$parent=Get-FileTransferCoordinatorBootstrapCorrelationCatalog
if ($parent.Hash -cne 'ebbaf19d291129caa47b34e69395de2b38985661560791c7c762c437ed6e679b' -or
    $catalog.ParentCatalogHash -cne $parent.Hash -or $catalog.Hash -cne '40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db') { throw 'Catalog lineage changed' }
$marker=[pscustomobject]@{operationKind='FallbackBootstrapSourceHold';receiptValidated=$true}
if ((Test-FileTransferCoordinatorSnapshotEffectScope $marker ([pscustomobject]@{})) -or
    (Test-FileTransferCoordinatorSnapshotEffectReceipt $marker ([pscustomobject]@{}))) { throw 'Source hold evidence granted physical authority' }
$captured=[IO.File]::ReadAllLines($InputPath)
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'GUI parser error' }
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
function Set-Field([string]$Line, [string]$Name, [string]$Value) {
    [regex]::Replace($Line, "(^|; )$([regex]::Escape($Name))=[^;]*", {param($m) $m.Groups[1].Value + $Name + '=' + $Value})
}
function Replace-Envelope([string]$Line, $Envelope) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $line=Set-Field $Line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
    Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
}
$normalized=[Collections.Generic.List[string]]::new()
foreach ($case in @('exact','relabeled_origin','wrong_proof_kind','altered_source','extra_field','missing_hold','missing_capture','missing_drain',
    'wrong_drain_command','unsettled_work','changed_frozen_checkpoint','wrong_base_command','missing_exchange_link',
    'hidden_drain','rejected_applied','late_phase_applied','old_profile','identical_duplicate')) {
    $lines=[string[]]$captured.Clone(); $mutated=$case -ceq 'exact'
    if ($case -ceq 'old_profile') {
        for ($i=0; $i -lt $lines.Count; $i++) { $lines[$i]=Set-Field (Set-Field $lines[$i] 'snapshot_catalog_version' $parent.Version) 'snapshot_catalog_hash' $parent.Hash }
        $mutated=$true
    }
    if ($case -ceq 'identical_duplicate') {
        $duplicate=@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot' })[0]
        $lines=@(foreach ($line in $lines) { $line; if ($line -ceq $duplicate) { $line } }); $mutated=$true
    }
    for ($i=0; $i -lt $lines.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        if ($mutated -and $case -cne 'missing_hold') { break }
        $envelope=(ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
        $hold=@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_hold')
        $exchange=@($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.beforeState -ceq 'None' })
        $attempt=@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_attempt')
        $changed=$false
        if ($case -ceq 'missing_hold' -and $hold.Count -gt 0) {
            $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationRole -CNE 'fallback_bootstrap_source_hold'); $changed=$true
        }
        if ($hold.Count -eq 1) {
            $p=$hold[0].routeTransition.bootstrapInitiation
            if ($case -ceq 'relabeled_origin') { $p.kind=if ($p.kind -ceq 'PeerEvidence') { 'LocalRuntimeDisable' } else { 'PeerEvidence' }; $changed=$true }
            if ($case -ceq 'wrong_proof_kind') { $p.proofKind='unrelated_observation'; $changed=$true }
            if ($case -ceq 'extra_field') { $p | Add-Member NoteProperty grantsRouteAuthority $true; $changed=$true }
            if ($case -ceq 'altered_source' -and $hold[0].afterState -ceq 'Applied') { $p.sourceFingerprint='fte1-' + ('a' * 24); $changed=$true }
            if (($case -ceq 'missing_capture' -and $hold[0].afterState -ceq 'Applied') -or
                ($case -ceq 'missing_drain' -and $hold[0].afterState -ceq 'Drained')) {
                $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationRole -CNE 'fallback_bootstrap_source_hold'); $changed=$true
            }
            if ($hold[0].afterState -ceq 'Drained') {
                switch ($case) {
                    'wrong_drain_command' { $hold[0].routeTransition.receipts[0].commandFingerprint='fte1-' + ('a' * 24); $changed=$true }
                    'unsettled_work' { $p.settledWorkFingerprint='fte1-' + ('a' * 24); $changed=$true }
                    'hidden_drain' {
                        $envelope.decisionKind='InvalidateFallbackBootstrapSourceHold'
                        $lines[$i]=Set-Field $lines[$i] 'decision_kind' $envelope.decisionKind
                        $changed=$true
                    }
                }
            }
        }
        if ($exchange.Count -eq 1) {
            $p=$exchange[0].routeTransition.bootstrapInitiation
            switch ($case) {
                'changed_frozen_checkpoint' { $p.frozenCheckpointFingerprint='fte1-' + ('a' * 24); $exchange[0].routeTransition.durableCheckpointFingerprint=$p.frozenCheckpointFingerprint; $changed=$true }
                'wrong_base_command' { $p.baseCommandFingerprint='fte1-' + ('a' * 24); $changed=$true }
                'missing_exchange_link' { $exchange[0].routeTransition.PSObject.Properties.Remove('bootstrapInitiation'); $changed=$true }
            }
        }
        if ($case -ceq 'rejected_applied' -and $attempt.Count -eq 1) { $attempt[0].stateApplied=$true; $changed=$true }
        if ($case -ceq 'late_phase_applied' -and $attempt.Count -eq 1 -and $envelope.decisionKind -ceq 'ApplyFallbackBootstrapPhaseReceipt') {
            $attempt[0].stateApplied=$true; $changed=$true
        }
        if ($changed) { $lines[$i]=Replace-Envelope $lines[$i] $envelope; $mutated=$true }
    }
    if (-not $mutated) { throw "No mutation target: $case" }
    $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    # Each consumer reparses its own serialized records; no decoded operation objects are shared.
    $records=@(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=[string]$lines[$i];LineIndex=$i} })
    $compact=Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $positive=$case -cin @('exact','identical_duplicate')
    if ($positive) {
        if (@(Get-FileTransferBootstrapInitiationHistoryErrors @($full.Boundaries)).Count -ne 0 -or
            $full.ArtifactEvidenceFailureReason -cmatch 'snapshot_bootstrap_(initiation|correlation|callback)') { throw "$case initiation history invalid: $($full.ArtifactEvidenceFailureReason)" }
        $independent=@($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ })
        $inbound=$full.Boundaries[0].direction -ceq 'inbound'
        if (($inbound -and $independent.Count -ne 4) -or (-not $inbound -and $independent.Count -ne 0) -or
            @($independent | Where-Object { $_ -cnotmatch '^snapshot_responder_probe_handoff_required_effect_missing:' }).Count -ne 0) {
            throw "$case unexpected independent findings: $($full.ArtifactEvidenceFailureReason)"
        }
    } elseif ($full.ArtifactEvidenceVerdict -cne 'fail' -or
        ($full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_bootstrap_(initiation|correlation)' -and
        # The new callback profile checks the hold's repeated source binding before initiation history.
        -not ($case -ceq 'altered_source' -and $full.ArtifactEvidenceFailureReason -cmatch 'snapshot_bootstrap_callback_operation_invalid:'))) {
        throw "$case failed outside the intended evidence boundary: $($full.ArtifactEvidenceFailureReason)"
    }
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) { throw "$case full/compact parity failed" }
    $guiPath=Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($case + '-gui.log')
    [IO.File]::WriteAllLines($guiPath, $lines)
    $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    $boundary=$full.FirstUnresolvedBoundary
    if ($positive -and $full.ArtifactEvidenceVerdict -ceq 'pass') {
        $route=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        if ($route.Verdict -cne 'fail') { throw 'Modeled source-hold history was mistaken for physical route acceptance' }
        $boundary=$route.FirstUnresolvedBoundary
    }
    if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash) { throw "$case GUI parity failed" }
    $reasons=[string[]]($full.ArtifactEvidenceFailureReason -split ';'); [Array]::Sort($reasons, [StringComparer]::Ordinal)
    $normalized.Add("$case|$($full.ArtifactEvidenceVerdict)|$($full.BoundaryHash)|$($reasons -join ';')")
}
'bootstrap-initiation-normalized=' + ($normalized -join '/')
