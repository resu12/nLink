param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion
$captured = [IO.File]::ReadAllLines($InputPath)
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
foreach ($case in @('exact','missing_prior_delivery','mismatched_prior_receipt','incomplete_exchange','missing_ready_phase',
        'changed_reactivation_source','missing_reactivation_disposition','changed_reactivation_target','stale_retirement',
        'wrong_retained_peer','changed_disposition_frame')) {
    $lines = [string[]]$captured.Clone(); $mutated = $case -ceq 'exact'
    for ($i=0; $i -lt $lines.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
        $changed = $false
        $retirement = $envelope.decisionKind -ceq 'StartFallbackBootstrapExchange' -and
            @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.operationGeneration -eq 2 }).Count -eq 1
        $operations = [Collections.Generic.List[object]]::new()
        foreach ($operation in $envelope.aggregate.operations) {
            $oldBase = $operation.operationRole -ceq 'fallback_v6_bootstrap' -and $operation.operationGeneration -eq 1
            $oldExchange = $operation.operationRole -ceq 'fallback_bootstrap_exchange' -and $operation.operationGeneration -eq 1
            $reactivation = $operation.operationRole -cin @('current','terminal') -and
                $null -ne $operation.routeTransition -and $operation.routeTransition.transitionKind -ceq 'PostTunaFallbackV6ToTunaV4'
            if ($case -ceq 'missing_prior_delivery' -and $oldBase -and $operation.afterState -ceq 'Delivered') { $changed=$true; continue }
            if ($case -ceq 'incomplete_exchange' -and $oldExchange -and $operation.afterState -ceq 'Completed') { $changed=$true; continue }
            if ($case -ceq 'missing_ready_phase' -and $operation.operationRole -ceq 'fallback_bootstrap_phase' -and
                $operation.operationGeneration -eq 1 -and $operation.afterState -ceq 'Ready') { $changed=$true; continue }
            if ($case -ceq 'missing_reactivation_disposition' -and $reactivation -and
                $envelope.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt') { $changed=$true; continue }
            if (($case -ceq 'mismatched_prior_receipt' -and $oldBase -and $operation.afterState -ceq 'Delivered') -or
                ($case -ceq 'stale_retirement' -and $oldBase -and $retirement)) {
                $operation.receiptFingerprint = 'fte1-' + ('a' * 24)
                $operation.routeTransition.receipts[0].receiptFingerprint = $operation.receiptFingerprint
                $changed = $true
            }
            if ($case -ceq 'changed_reactivation_source' -and $reactivation -and
                $envelope.decisionKind -ceq 'PrepareFileTransferRouteTransition') {
                $operation.routeTransition.sourceBoundary.bridgeRecoveryGeneration++; $changed=$true
            }
            if ($case -ceq 'changed_reactivation_target' -and $reactivation -and
                $envelope.decisionKind -ceq 'CommitFileTransferRouteTransition' -and $operation.operationRole -ceq 'terminal') {
                $operation.routeTransition.targetBoundary.runtimeGeneration++; $changed=$true
            }
            if ($case -ceq 'wrong_retained_peer' -and $oldExchange -and $operation.routeTransition.bootstrapCorrelation.completed) {
                $operation.routeTransition.bootstrapCorrelation.retainedPeerFrameIdentityFingerprint = 'fte1-' + ('a' * 24); $changed=$true
            }
            if ($case -ceq 'changed_disposition_frame' -and $operation.operationRole -ceq 'terminal' -and
                $operation.routeTransition.transitionKind -ceq 'TunaV4ToPostTunaFallbackV6' -and
                $envelope.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt') {
                $operation.routeTransition.retainedFrameFingerprint = 'fte1-' + ('a' * 24); $changed=$true
            }
            $operations.Add($operation)
        }
        if ($changed) { $envelope.aggregate.operations = @($operations); $lines[$i] = Replace-Envelope $lines[$i] $envelope; $mutated=$true }
    }
    if (-not $mutated) { throw "No mutation target for $case" }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    $records = @(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i];LineIndex=$i} })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $retirements = Get-FileTransferBootstrapCorrelationRetirementEvidence @($full.Boundaries) $catalog
    $phaseErrors = @(Get-FileTransferBootstrapCorrelationHistoryErrors @($full.Boundaries))
    if ($case -ceq 'exact') {
        if ($retirements.Validated.Count -ne 1 -or $retirements.Errors.Count -ne 0 -or $phaseErrors.Count -ne 0) { throw 'Exact retirement history rejected' }
        # This is Coordinator history, not a persisted physical-effect/route-acceptance fixture.
        # Keep the independently missing adapter effects visible; they must not be waived by retirement closure.
        $independent = @($full.ArtifactEvidenceFailureReason -split ';')
        if ($full.ArtifactEvidenceVerdict -cne 'fail' -or $independent.Count -eq 0 -or
            @($independent | Where-Object { $_ -cnotmatch '^snapshot_(scope_invalidation|responder_probe_handoff)_required_effect_missing:' }).Count -ne 0) {
            throw "Unexpected independent evidence finding: $($full.ArtifactEvidenceFailureReason)"
        }
    } elseif ($retirements.Validated.Count -ne 0 -or $retirements.Errors.Count -ne 1 -or
        $full.ArtifactEvidenceVerdict -cne 'fail' -or
        $full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_bootstrap_correlation_retirement_history_invalid:') {
        throw "$case did not reject the retirement history: $($full.ArtifactEvidenceFailureReason)"
    }
    $retired = @($full.Boundaries | Where-Object decisionKind -CEQ 'StartFallbackBootstrapExchange' | ForEach-Object {
        $boundary = $_
        foreach ($operation in @($_.operations | Where-Object { $_.operationRole -ceq 'fallback_v6_bootstrap' -and $_.afterState -ceq 'None' })) {
            [pscustomobject]@{Boundary=$boundary;Operation=$operation}
        }
    })
    if ($retired.Count -ne 1) { throw 'Retirement fixture lost its boundary' }
    $closure = Get-FileTransferCoordinatorFallbackV6BootstrapLifecycle -History $retired -CorrelatedRetirements $retirements.Validated
    if ($closure.Closed -ne ($case -ceq 'exact')) { throw "$case lifecycle bypassed exact history" }
    $unproved = Get-FileTransferCoordinatorFallbackV6BootstrapLifecycle -History $retired -CorrelatedRetirements @{}
    if ($unproved.Closed -or $unproved.StructuralFailure -cne 'correlated_retirement_history_missing') { throw 'Legacy lineage bypassed correlation proof' }
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) {
        throw "$case full/compact parity failed"
    }
    $guiPath = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($case + '-gui.log')
    [IO.File]::WriteAllLines($guiPath, $lines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $full.FirstUnresolvedBoundary.boundaryId -or
        $gui.CoordinatorBoundaryHash -cne $full.FirstUnresolvedBoundary.boundaryHash) { throw "$case GUI boundary parity failed" }
    # Legacy independent effects at the same transition can enumerate in a different host order.
    # Compare their complete ordinal multiset; boundary JSON/hash and GUI identity remain exact checks above.
    $reasons = [string[]]($full.ArtifactEvidenceFailureReason -split ';')
    [Array]::Sort($reasons, [StringComparer]::Ordinal)
    $normalized.Add("$case|$($retirements.Validated.Count)|$($full.BoundaryHash)|$($reasons -join ';')")
}
Write-Output ('bootstrap-retirement-normalized=' + ($normalized -join '/'))
