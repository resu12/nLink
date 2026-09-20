param([string]$RepoRoot, [string]$InputPath, [string]$Scenario = 'matrix')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferAuthorityMatching.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1')
$captured = [IO.File]::ReadAllLines($InputPath)
$source = $null; $startIndex = -1
for ($i=0; $i -lt $captured.Count; $i++) {
    if ((Get-FileTransferCoordinatorSnapshotEventName $captured[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
    $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $captured[$i] 'snapshot_payload')).Value
    foreach ($op in @($envelope.aggregate.operations | Where-Object {
        $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.operationGeneration -eq 2
    })) {
        if ($op.beforeState -ceq 'None' -and $op.afterState -ceq 'Pending') { $startIndex = $i }
        if ($op.afterState -ceq 'Completed') { $source = $op.routeTransition.sourceBoundary }
    }
}
if ($startIndex -lt 0 -or $null -eq $source -or $source.liveRouteEpoch -ne 3) { throw 'Missing real second-cycle fixture' }
$scope = 'session_id=sess_[redacted]; transfer_id=[redacted]; direction=inbound'
$sourceFields = @(
    "source_route=$($source.route)", "source_protocol_version=$($source.protocolVersion)",
    "source_live_route_epoch=$($source.liveRouteEpoch)", "source_runtime_generation=$($source.runtimeGeneration)",
    "source_route_publication_generation=$($source.routePublicationGeneration)", "source_route_commit_generation=$($source.routeCommitGeneration)",
    "source_terminal_generation=$($source.terminalGeneration)", "source_leg_generation=$($source.legGeneration)",
    "source_transport_epoch=$($source.transportEpoch)", "source_bridge_recovery_generation=$($source.bridgeRecoveryGeneration)",
    "source_checkpoint_request_id=$($source.checkpointRequestId)", "source_checkpoint_generation=$($source.checkpointGeneration)",
    "data_session_generation=$($source.dataSessionGeneration)") -join '; '
$episode = @("event=tuna_fallback_started; $scope; fallback_epoch=2",
    "event=filetransfer_fallback_nkn_proof_pending; $scope; pending_generation=3")
$diagnostic = @(
    "event=tuna_fallback_nkn_frame_received; $scope; fallback_epoch=2; message_type=file_transfer_data_frame; channel=bulk",
    "event=filetransfer_fallback_nkn_proof_unconfirmed; $scope; proof=file_transfer_bulk_frame_received; bulk_seen=1; control_seen=0; requires_control_proof=1; requires_v6_epoch_recovery=0",
    "event=filetransfer_fallback_v6_proof_bootstrap_trigger_bound; $scope; status=Ready; pending_generation=3; fallback_epoch=2; $sourceFields; frame_fingerprint=[redacted]; bulk_seen=1; control_seen=1; authority_effect=none",
    "event=filetransfer_fallback_nkn_proof_unconfirmed; $scope; proof=nkn_control_ack_received; bulk_seen=1; control_seen=1; requires_control_proof=0; requires_v6_epoch_recovery=1",
    "event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch; $scope; proof=nkn_control_ack_received")
$lines = @($captured[0..($startIndex-1)]) + $episode + @($captured[$startIndex..($captured.Count-1)]) + $diagnostic
for ($i=0; $i -lt $lines.Count; $i++) {
    $lines[$i] = [regex]::Replace($lines[$i], '; log_writer_id=[^;]+; log_writer_sequence=[^;]+$', '') +
        "; log_writer_id=bootstrap-correlation-fixture; log_writer_sequence=$($i+1)"
}
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete) {
    throw "Modeled source is not valid: $($full.ArtifactEvidenceFailureReason)"
}
$events = @(for ($i=0; $i -lt $lines.Count; $i++) {
    [pscustomobject]@{ RawLine=$lines[$i]; Sequence=$i+1; LineNumber=$i+1; FilePath=$InputPath }
})
$legacy = Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines
if ($legacy.UnresolvedCount -ne 1) { throw 'Fixture did not reproduce the legacy diagnostic' }
$proof = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $full -Family 'route_transition' -TaxonomyClasses @(
    'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing')
$combined = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $events -SnapshotProof $proof -SnapshotEvaluation $full
$expectedLegacy = if ($Scenario -ceq 'hold_invalidated') { 1 } else { 0 }
if ($combined.UnresolvedCount -ne ($proof.UnresolvedCount + $expectedLegacy) -or $combined.RawCount -ne ($proof.RawCount + $expectedLegacy)) {
    throw "$Scenario expected $expectedLegacy legacy obligations, got $($combined.UnresolvedCount - $proof.UnresolvedCount)"
}
$records = @(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i];LineIndex=$i} })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
$compactProof = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $compact -Family 'route_transition' -TaxonomyClasses @(
    'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing')
$compactCombined = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $events -SnapshotProof $compactProof -SnapshotEvaluation $compact
if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (ConvertTo-Json $combined -Depth 64 -Compress) -cne (ConvertTo-Json $compactCombined -Depth 64 -Compress)) { throw 'Full/compact ownership mismatch' }

$tokens = $null; $parseErrors = $null
$guiAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'GUI parser error' }
foreach ($name in @('Get-TunaGuiLogLineFieldValue','Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure',
        'New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($guiAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -eq 0) { throw "Missing GUI function: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
if ((Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure -Lines $lines -SnapshotEvaluation $full).Unresolved -ne ($expectedLegacy -eq 1)) { throw 'GUI correlated ownership mismatch' }
if (-not (Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure -Lines $lines).Unresolved) { throw 'GUI waived legacy diagnostic without snapshots' }

# The shared helper only selects this diagnostic's owner. Independent route and
# artifact failures remain authoritative even in the exact completed case.
$independent = [pscustomobject]@{ Event=$events[0]; Class='publication_authority_missing'; Reason='test_independent'; Variant='none'; Recovered=$false }
$independentProof = [pscustomobject]@{
    RawCount=1; DistinctCount=1; RecoveredCount=0; UnresolvedCount=1
    CandidateRecords=@($independent); RecoveredCandidateRecords=@(); UnresolvedCandidateRecords=@($independent); AuthoritativeRecords=@($independent)
}
$preserved = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $events -SnapshotProof $independentProof -SnapshotEvaluation $full
if ($preserved.Verdict -cne 'fail' -or $preserved.UnresolvedCount -ne (1 + $expectedLegacy) -or
    -not [object]::ReferenceEquals($preserved.UnresolvedCandidateRecords[0], $independent)) { throw 'Independent snapshot failure was waived' }
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
if ($route.Verdict -cne 'fail') { throw 'Modeled input unexpectedly proved physical route publication' }
$guiPath = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) 'ownership-gui.log'
[IO.File]::WriteAllLines($guiPath, $lines)
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $route.FirstUnresolvedBoundary.boundaryId) { throw 'GUI waived independent route failure' }
function Assert-LegacyRetained([string]$Name, [string[]]$InputLines, $Evaluation) {
    $result = Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $InputLines -SnapshotEvaluation $Evaluation
    if ($result.RawCount -ne 1 -or $result.UnresolvedCount -ne 1) { throw "$Name waived the legacy obligation" }
}
function Clone($Value) { ConvertFrom-Json (ConvertTo-Json $Value -Depth 64 -Compress) }

# Project the same modeled decisions into each supported initiation catalog and
# validate the complete stream before exercising ownership. The initiation-only
# projection omits the later callback extension, not any source-hold evidence.
if ($Scenario -cin @('hold_invalidated','committed_cleanup')) {
    foreach ($profile in @($script:FileTransferCoordinatorBootstrapInitiationCatalogVersion,
            $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion,
            $script:FileTransferCoordinatorRepairOwnerCatalogVersion,
            $script:FileTransferCoordinatorActivationRetryCatalogVersion)) {
        $profileLines = [string[]]$lines.Clone()
        $profileCatalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $profile
        for ($i=0; $i -lt $profileLines.Count; $i++) {
            $profileLines[$i] = [regex]::Replace($profileLines[$i], '(?<=snapshot_catalog_version=)[^;]*', $profile)
            $profileLines[$i] = [regex]::Replace($profileLines[$i], '(?<=snapshot_catalog_hash=)[^;]*', $profileCatalog.Hash)
            if ($profile -cne $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion -or
                (Get-FileTransferCoordinatorSnapshotEventName $profileLines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
            $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $profileLines[$i] 'snapshot_payload')).Value
            if (@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_callback').Count) {
                throw 'Initiation projection cannot remove callback decisions'
            }
            foreach ($op in $envelope.aggregate.operations) {
                if ($null -ne $op.routeTransition) { $op.routeTransition.PSObject.Properties.Remove('bootstrapCallbacks') }
            }
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json $envelope -Depth 64 -Compress))
            $profileLines[$i] = [regex]::Replace($profileLines[$i], '(?<=snapshot_sha256=)[^;]*', (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes))
            $profileLines[$i] = [regex]::Replace($profileLines[$i], '(?<=snapshot_payload=)[^;]*', [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
        }
        $profileFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $profileLines
        $profileRecords = @(for ($i=0; $i -lt $profileLines.Count; $i++) { [pscustomobject]@{Line=$profileLines[$i];LineIndex=$i} })
        $profileCompact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $profileRecords
        if ($profileFull.ArtifactEvidenceVerdict -cne 'pass' -or -not $profileFull.StreamComplete -or
            $profileFull.CatalogVersion -cne $profile -or
            (Compare-FileTransferCoordinatorSnapshotEvaluations $profileFull $profileCompact).Verdict -cne 'pass') {
            throw "$profile modeled catalog input invalid: $($profileFull.ArtifactEvidenceFailureReason)"
        }
        foreach ($view in @($profileFull,$profileCompact)) {
            $result = Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $profileLines -SnapshotEvaluation $view
            if ($result.RawCount -ne $expectedLegacy -or $result.UnresolvedCount -ne $expectedLegacy) { throw "$profile lost source-hold semantics" }
            # Deliberately predicate-only mutation of the validated view.
            $missingHold = Clone $view
            foreach ($boundary in $missingHold.Boundaries) {
                $boundary.operations = @($boundary.operations | Where-Object operationRole -CNE 'fallback_bootstrap_source_hold')
            }
            Assert-LegacyRetained "$profile missing hold" $profileLines $missingHold
            $preserved = Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof -Events $events -SnapshotProof $independentProof -SnapshotEvaluation $view
            if ($preserved.Verdict -cne 'fail' -or $preserved.UnresolvedCount -ne (1 + $expectedLegacy) -or
                -not [object]::ReferenceEquals($preserved.UnresolvedCandidateRecords[0], $independent)) { throw "$profile waived independent failure" }
        }
    }
    $unknownLines = @($lines | ForEach-Object {
        [regex]::Replace($_, '(?<=snapshot_catalog_version=)[^;]*', $script:FileTransferCoordinatorActivationRetryCatalogVersion + '-unknown')
    })
    $unknown = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $unknownLines
    if ($unknown.ArtifactEvidenceVerdict -cne 'fail' -or $unknown.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_catalog_mismatch:') {
        throw 'Unknown catalog input was not rejected'
    }
    Assert-LegacyRetained 'unknown catalog input' $unknownLines $unknown
    # Predicate-only control: even an otherwise valid view cannot admit a suffix.
    $unknown = Clone $full
    $unknown.CatalogVersion = $script:FileTransferCoordinatorActivationRetryCatalogVersion + '-unknown'
    Assert-LegacyRetained 'unknown catalog selector' $lines $unknown
    Write-Output "bootstrap-ownership-catalog-coverage-ok:$Scenario"
}

# These mutations exercise selector boundaries, not artifact validity. The
# unmodified scenarios above are emitted by real Coordinator decisions.
if ($Scenario -ceq 'committed_cleanup') {
    foreach ($case in @('missing_hold','wrong_hold_command','wrong_hold_source','missing_hold_drain',
            'missing_prepare','missing_commit','missing_disposition','late_commit','late_disposition',
            'wrong_source','wrong_checkpoint','wrong_peer','wrong_target','wrong_commit_authority',
            'wrong_disposition_generation','wrong_commit_stream','unvalidated_commit','unvalidated_disposition',
            'non_applied_commit','non_applied_disposition','receipt_mismatch','restored_hold')) {
        $copy = Clone $full
        $commit = @($copy.Boundaries | Where-Object {
            $_.decisionKind -ceq 'CommitFileTransferRouteTransition' -and $_.afterBoundary.route -ceq 'post_tuna_fallback_v6'
        })[-1]
        $commitOp = @($commit.operations | Where-Object operationRole -CEQ 'terminal')[0]
        $prepared = @($copy.Boundaries | Where-Object {
            $_.decisionKind -ceq 'PrepareFileTransferRouteTransition' -and
            @($_.operations | Where-Object authorityId -CEQ $commitOp.authorityId).Count -gt 0
        })[-1]
        $disposed = @($copy.Boundaries | Where-Object {
            $_.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and
            @($_.operations | Where-Object authorityId -CEQ $commitOp.authorityId).Count -gt 0
        })[-1]
        $disposedOp = @($disposed.operations | Where-Object operationRole -CEQ 'terminal')[0]
        $lost = @($copy.Boundaries | Where-Object decisionKind -CEQ 'InvalidateFallbackBootstrapSourceHold')[-1]
        $holdOps = @(foreach ($boundary in $copy.Boundaries) {
            $boundary.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_source_hold' -and $_.operationGeneration -eq 2 }
        })
        switch ($case) {
            missing_hold { foreach ($op in $holdOps) { $op.authorityId='fte1-000000000000000000000000' } }
            wrong_hold_command { foreach ($op in $holdOps) { $op.routeTransition.bootstrapInitiation.holdCommandFingerprint='(none)' } }
            wrong_hold_source { foreach ($op in $holdOps) { $op.routeTransition.bootstrapInitiation.sourceBoundary.runtimeGeneration=999 } }
            missing_hold_drain { foreach ($op in $holdOps) { $op.routeTransition.bootstrapInitiation.drainReceiptFingerprint='(none)' } }
            missing_prepare { $prepared.decisionKind='unrelated' }
            missing_commit { $commit.decisionKind='unrelated' }
            missing_disposition { $disposed.decisionKind='unrelated' }
            late_commit { $commit.writerSequence=[long]$lost.writerSequence+1 }
            late_disposition { $disposed.writerSequence=[long]$lost.writerSequence+1 }
            wrong_source { $commitOp.routeTransition.sourceBoundary.runtimeGeneration=999 }
            wrong_checkpoint { $commitOp.routeTransition.durableCheckpointFingerprint='(none)' }
            wrong_peer { $commitOp.routeTransition.retainedFrameFingerprint='(none)' }
            wrong_target { $commit.afterBoundary.protocolVersion=4 }
            wrong_commit_authority { $commitOp.authorityId='fte1-000000000000000000000000' }
            wrong_disposition_generation { $disposedOp.operationGeneration=999 }
            wrong_commit_stream { $commit.coordinatorInstanceId='fte1-000000000000000000000000' }
            unvalidated_commit { @($commitOp.routeTransition.receipts | Where-Object stage -CEQ 'commit')[0].receiptValidated=$false }
            unvalidated_disposition { @($disposedOp.routeTransition.receipts | Where-Object stage -CEQ 'disposition')[0].receiptValidated=$false }
            non_applied_commit { $commitOp.stateApplied=$false }
            non_applied_disposition { $disposedOp.stateApplied=$false }
            receipt_mismatch { @($disposedOp.routeTransition.receipts | Where-Object stage -CEQ 'drain')[0].receiptFingerprint='(none)' }
            restored_hold {
                $restored = Clone $lost; $restored.writerSequence=[long]$lost.writerSequence+1
                foreach ($op in @($restored.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_hold')) {
                    $op.beforeState='Invalidated'; $op.afterState='Drained'
                }
                $copy.Boundaries += $restored; $commit.decisionKind='unrelated'
            }
        }
        Assert-LegacyRetained $case $lines $copy
    }
}
if ($Scenario -ceq 'hold_invalidated') {
    foreach ($case in @('rejected','unrelated_hold')) {
        $copy = Clone $full
        $lost = @($copy.Boundaries | Where-Object decisionKind -CEQ 'InvalidateFallbackBootstrapSourceHold')[-1]
        if ($case -ceq 'rejected') { $lost.decisionStatus='Rejected' }
        else { @($lost.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_hold')[0].authorityId='fte1-000000000000000000000000' }
        if ((Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines -SnapshotEvaluation $copy).RawCount -ne 0) {
            throw "$case source-hold observation changed ownership"
        }
    }
}
if ($Scenario -cne 'matrix') { Write-Output 'bootstrap-ownership-ok'; return }

# The older correlation catalog has no initiation/source-hold contract. This
# predicate compatibility check does not recatalog the real fixture as evidence.
$copy = Clone $full; $copy.CatalogVersion=$script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion
foreach ($boundary in $copy.Boundaries) {
    $boundary.operations=@($boundary.operations | Where-Object operationRole -CNE 'fallback_bootstrap_source_hold')
}
if ((Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines -SnapshotEvaluation $copy).RawCount -ne 0) {
    throw 'Correlation-only catalog unexpectedly required a source hold'
}
Assert-LegacyRetained 'absent snapshot' $lines $null
foreach ($case in @('legacy','incomplete','invalid','old_catalog','shadow_route','non_authoritative_route')) {
    $copy = Clone $full
    switch ($case) {
        legacy { $copy.Mode='LegacyV0' }
        incomplete { $copy.StreamComplete=$false }
        invalid { $copy.ArtifactEvidenceVerdict='fail' }
        old_catalog { $copy.CatalogVersion='snapshot-authority-v1' }
        shadow_route { $copy.ShadowFamilies=@('route_transition') }
        non_authoritative_route { $copy.AuthoritativeFamilies=@('receive_recovery') }
    }
    Assert-LegacyRetained $case $lines $copy
}
foreach ($field in @('source_route','source_protocol_version','source_live_route_epoch','source_runtime_generation',
        'source_route_publication_generation','source_route_commit_generation','source_terminal_generation','source_leg_generation',
        'source_transport_epoch','source_bridge_recovery_generation','source_checkpoint_request_id','source_checkpoint_generation',
        'data_session_generation','transfer_id','direction','session_id','log_writer_id')) {
    $copy = [string[]]$lines.Clone()
    for ($i=0; $i -lt $copy.Count; $i++) {
        if ($copy[$i] -cmatch '^event=filetransfer_fallback_v6_proof_bootstrap_trigger_bound;') {
            $copy[$i] = [regex]::Replace($copy[$i], "(?<=; )$field=[^;]*", "$field=wrong")
        }
    }
    Assert-LegacyRetained "wrong $field" $copy $full
}
foreach ($case in @('missing_ready','conflicting_field','wrong_wait_transfer','malformed_redaction','new_pending','new_episode')) {
    $copy = [string[]]$lines.Clone()
    switch ($case) {
        missing_ready { $copy = @($copy | Where-Object { $_ -cnotmatch '^event=filetransfer_fallback_v6_proof_bootstrap_trigger_bound;' }) }
        conflicting_field { $copy = @($copy | ForEach-Object { if ($_ -cmatch '^event=filetransfer_fallback_v6_proof_bootstrap_trigger_bound;') { $_ + '; source_runtime_generation=999' } else { $_ } }) }
        wrong_wait_transfer { $copy[-1] = $copy[-1].Replace('transfer_id=[redacted]', 'transfer_id=unrelated') }
        malformed_redaction { $copy = @($copy | ForEach-Object { $_.Replace('transfer_id=[redacted]', 'transfer_id=wrong-[redacted]') }) }
        new_pending { $copy = @($captured) + $episode + $diagnostic }
        new_episode { $copy = @($captured) + $episode + $diagnostic; $copy = @($copy | ForEach-Object { $_.Replace('fallback_epoch=2','fallback_epoch=3') }) }
    }
    if ($case -cin @('new_pending','new_episode')) {
        for ($i=0; $i -lt $copy.Count; $i++) {
            $copy[$i] = [regex]::Replace($copy[$i], '; log_writer_id=[^;]+; log_writer_sequence=[^;]+$', '') +
                "; log_writer_id=bootstrap-correlation-fixture; log_writer_sequence=$($i+1)"
        }
        $evaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $copy
    } else { $evaluation = $full }
    Assert-LegacyRetained $case $copy $evaluation
}

# Predicate controls below deliberately mutate already-evaluated objects; they
# test fail-closed selection, not claims of valid Coordinator history.
foreach ($case in @('late','rejected','not_applied','incomplete_exchange','invalidated','disabled_agreement',
        'wrong_peer','missing_peer_frame','ambiguous_stream','prior_ambiguous_stream','replaced_then_restored')) {
    $copy = Clone $full
    $boundary = @($copy.Boundaries | Where-Object {
        @($_.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.operationGeneration -eq 2 -and $_.afterState -ceq 'Completed' }).Count -gt 0
    })[-1]
    $op = @($boundary.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_exchange')[0]
    switch ($case) {
        late { $boundary.writerSequence=$lines.Count+1 }
        rejected { $boundary.decisionStatus='Rejected' }
        not_applied { $op.stateApplied=$false }
        incomplete_exchange { $op.afterState='Pending'; $op.routeTransition.bootstrapCorrelation.completed=$false }
        invalidated { $op.afterState='Invalidated'; $op.routeTransition.bootstrapCorrelation.invalidated=$true }
        disabled_agreement { $op.routeTransition.bootstrapCorrelation.agreementEnabled=$false }
        wrong_peer { $op.routeTransition.bootstrapCorrelation.completedPeerIdFingerprint='fte1-000000000000000000000000' }
        missing_peer_frame { $op.routeTransition.bootstrapCorrelation.retainedPeerFrameIdentityFingerprint='(none)' }
        ambiguous_stream {
            $other = Clone $boundary; $other.coordinatorInstanceId='fte1-000000000000000000000000'; $copy.Boundaries += $other
        }
        prior_ambiguous_stream {
            $other = Clone $boundary; $other.coordinatorInstanceId='fte1-000000000000000000000000'
            $other.writerSequence=1; $copy.Boundaries += $other
        }
        replaced_then_restored {
            $other = Clone $boundary; $other.writerSequence=[long]$boundary.writerSequence+1
            $otherOp = @($other.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_exchange')[0]
            $otherOp.operationKey += '-replacement'; $otherOp.afterState='Invalidated'
            $otherOp.routeTransition.sourceBoundary.runtimeGeneration=999
            $restored = Clone $boundary; $restored.writerSequence=[long]$boundary.writerSequence+2
            foreach ($restoredOp in $restored.operations) { $restoredOp.beforeState=$restoredOp.afterState }
            $copy.Boundaries += @($other,$restored)
        }
    }
    Assert-LegacyRetained $case $lines $copy
}

# Rejected unrelated callbacks remain diagnostic-only, not ownership changes.
$copy = Clone $full
$other = Clone $boundary; $other.decisionStatus='Rejected'; $other.writerSequence=$lines.Count-1
foreach ($operation in $other.operations) { $operation.operationKey += '-rejected'; $operation.stateApplied=$false }
$copy.Boundaries += $other
if ((Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines -SnapshotEvaluation $copy).RawCount -ne 0) { throw 'Rejected callback changed ownership' }
$identified = @($lines | ForEach-Object {
    $_.Replace('session_id=sess_[redacted]', "session_id=$($full.Boundaries[0].sessionId)").Replace('transfer_id=[redacted]', "transfer_id=$($full.Boundaries[0].transferId)")
})
if ((Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $identified -SnapshotEvaluation $full).RawCount -ne 0) { throw 'Exact evidence-token identity rejected' }

# Exercise the real integrity evaluator, not just its result gate: valid hashes
# cannot make a changed peer identity, missing phase receipt or agreement valid.
foreach ($case in @('peer','receipt','agreement')) {
    $copy = [string[]]$lines.Clone(); $mutated=$false
    for ($i=0; $i -lt $copy.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $copy[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $copy[$i] 'snapshot_payload')).Value
        $exchange = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.operationGeneration -eq 2 -and $_.beforeState -ceq 'Pending' -and $_.afterState -ceq 'Completed' })
        if ($exchange.Count -ne 1) { continue }
        switch ($case) {
            peer { $exchange[0].routeTransition.bootstrapCorrelation.completedPeerIdFingerprint='fte1-000000000000000000000000' }
            agreement { $exchange[0].routeTransition.bootstrapCorrelation.agreementEnabled=$false }
            receipt { @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_phase')[0].routeTransition.receipts=@() }
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json $envelope -Depth 64 -Compress))
        $copy[$i] = [regex]::Replace($copy[$i], '(?<=snapshot_sha256=)[^;]*', (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes))
        $copy[$i] = [regex]::Replace($copy[$i], '(?<=snapshot_payload=)[^;]*', [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
        $mutated=$true; break
    }
    $evaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $copy
    if (-not $mutated -or $evaluation.ArtifactEvidenceVerdict -cne 'fail') { throw "$case integrity mutation was not rejected" }
    Assert-LegacyRetained "invalid $case history" $copy $evaluation
}

# Each production consumer must supply its already-computed authoritative input.
foreach ($mapping in @(@('tools/FileTransferSoak/ArtifactWriters.ps1','Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof'),
        @('tools/FileTransferOps/AnalyzerOrchestration.ps1','Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof'),
        @('tools/GuiSmoke-Windows.ps1','Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure'))) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $mapping[0]), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Parser errors in $($mapping[0])" }
    $calls = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq $mapping[1]}, $true))
    if ($calls.Count -ne 1 -or $calls[0].Extent.Text -cnotmatch '-SnapshotEvaluation \$') { throw "Missing ownership input in $($mapping[0])" }
}
Write-Output 'bootstrap-ownership-ok'
