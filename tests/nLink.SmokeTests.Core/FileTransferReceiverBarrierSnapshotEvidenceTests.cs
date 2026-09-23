using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferReceiverBarrierSnapshotEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("none")]
    [InlineData("automatic_invalidation")]
    [InlineData("frozen_profile")]
    [InlineData("source_protocol")]
    [InlineData("receipt_stage")]
    [InlineData("pause_mutation")]
    [InlineData("negotiation_mutation")]
    [InlineData("missing_effect")]
    [InlineData("duplicate_effect")]
    [InlineData("attempt_effect")]
    [InlineData("attempt_mutation")]
    [InlineData("missing_bind")]
    [InlineData("pre_admission")]
    [InlineData("pre_admission_zero")]
    [InlineData("pre_admission_projected")]
    [InlineData("pre_admission_pwsh")]
    [InlineData("pre_admission_admission_race")]
    [InlineData("pre_admission_old_profile")]
    [InlineData("pre_admission_missing_lineage")]
    [InlineData("pre_admission_changed_scope")]
    [InlineData("pre_admission_wrong_mode")]
    [InlineData("pre_admission_changed_launch")]
    [InlineData("pre_admission_changed_terminal_witness")]
    public async Task ReceiverBarrierSnapshot_RealCoordinatorEnvelopesRemainStrict(string mutation)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var preAdmission = mutation.StartsWith("pre_admission", StringComparison.Ordinal);
        var fixture = preAdmission ? FileTransferCoordinatorTests.CreatePreAdmissionReceiverBarrierFixture() :
            ((FileTransferCoordinatorState State, InitialActivationReceiverBarrierSource Source,
            InitialActivationReceiverBarrierNegotiation Negotiation))typeof(FileTransferCoordinatorTests)
            .GetMethod("CreateReceiverBarrierFixture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(
            fixture.State, fixture.Source, 1, fixture.Negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, bound.Status);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope> { bound.SnapshotEnvelope! };
        if (preAdmission)
        {
            var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(bound.State,
                bound.Authority!, fixture.Source, 1,
                fixture.Negotiation with { LaunchGeneration = mutation == "pre_admission_zero" ? 0 : 1 },
                new(Guid.NewGuid(), 1, "receiver-lane-process", 1));
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
            envelopes.Add(prepared.SnapshotEnvelope!);
            if (mutation == "pre_admission_admission_race")
            {
                var admitted = FileTransferCoordinator.Apply(new FileTransferCoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockAdmission, prepared.State.RouteSelection,
                    FileTransferTransportHandoffKind.None, FileTransferTransportKind.Unknown, "test",
                    FileTransferLegState.Active, true, 0, -1, 0, 0,
                    RuntimeUnlockAdmissionRequest: new("receiver_barrier_test", "precommit_probe_received", prepared.State.CurrentRuntimeOwner)), prepared.State);
                envelopes.Add(admitted.SnapshotEnvelope!);
            }
            else
            {
                var closed = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State, fixture.Source,
                    new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "exact_pre_admission_close"));
                Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, closed.Status);
                envelopes.Add(closed.SnapshotEnvelope!);
            }
        }
        else if (mutation == "automatic_invalidation")
        {
            var replaced = bound.State with { CurrentDataSessionGeneration = 2 };
            envelopes.Add(FileTransferCoordinator.CreateTransitionSnapshot(bound.State, replaced,
                bound.State.PersistentState.CoordinatorInstanceId!, bound.State.PersistentState.SnapshotStateGeneration,
                "DataSessionReplacement", "Accepted", "receiver_data_session_replaced").Envelope);
        }
        else
        {
            var admitted = FileTransferCoordinator.Apply(new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockAdmission, bound.State.RouteSelection,
                FileTransferTransportHandoffKind.None, FileTransferTransportKind.Unknown, "test",
                FileTransferLegState.Active, true, 0, -1, 0, 0,
                RuntimeUnlockAdmissionRequest: new("receiver_barrier_test", "precommit_probe_received", bound.State.CurrentRuntimeOwner)), bound.State);
            envelopes.Add(admitted.SnapshotEnvelope!);
            var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted.State,
                bound.Authority!, fixture.Source, admitted.State.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
            envelopes.Add(attached.SnapshotEnvelope!);
            var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(attached.State,
                bound.Authority!, fixture.Source, 1, fixture.Negotiation, new(Guid.NewGuid(), 1, "receiver-lane-process", 1));
            envelopes.Add(prepared.SnapshotEnvelope!);
            var committed = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State,
                fixture.Source, new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "exact_bridge_release"));
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, committed.Status);
            envelopes.Add(committed.SnapshotEnvelope!);
        }

        var directory = Path.Combine(Path.GetTempPath(), "nlink-receiver-barrier-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "coordinator-envelopes.json");
            var evidence = mutation is "pre_admission_projected" or "pre_admission_pwsh"
                ? envelopes.Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting)
                : envelopes;
            await File.WriteAllTextAsync(path, "[" + string.Join(",", evidence.Select(envelope => envelope.ToCanonicalJson())) + "]", new UTF8Encoding(false));
            var result = mutation == "pre_admission_pwsh"
                ? await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), path, mutation], "pwsh")
                : await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), path, mutation]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            Assert.Equal("receiver-barrier-evidence-ok", result.Stdout.Trim());
        }
        finally { TryDeleteDirectory(directory); }
    }

    private static readonly Lazy<Task<IReadOnlyList<string>>> RoutePreparationEvidence = new(
        SessionFileTransferV4SenderTests.CreateReceiverBarrierRoutePreparationEvidenceFixtureAsync);

    [Theory]
    [InlineData("route_prepare")]
    [InlineData("reordered_operations")]
    [InlineData("exact_duplicate")]
    [InlineData("missing_current")]
    [InlineData("ambiguous_current")]
    [InlineData("attempted_only")]
    [InlineData("historical_prepared")]
    [InlineData("not_applied")]
    [InlineData("untyped_applied")]
    [InlineData("target_route")]
    [InlineData("target_protocol")]
    [InlineData("rejected_preparation")]
    [InlineData("wrong_direction")]
    [InlineData("wrong_key")]
    [InlineData("wrong_authority")]
    [InlineData("wrong_command_identity")]
    [InlineData("wrong_command_generation")]
    [InlineData("absent_command_fingerprint")]
    [InlineData("receipt_identity")]
    [InlineData("receipt_generation")]
    [InlineData("receipt_status")]
    [InlineData("receipt_fingerprint")]
    [InlineData("receipt_validated")]
    [InlineData("typed_receipt")]
    [InlineData("checkpoint_zero")]
    [InlineData("checkpoint_negative")]
    [InlineData("checkpoint_absent_identity")]
    [InlineData("checkpoint_wrong_raw_identity")]
    [InlineData("checkpoint_wrong_projected_identity")]
    [InlineData("checkpoint_target_generation")]
    [InlineData("checkpoint_target_identity")]
    [InlineData("absent_durable_fingerprint")]
    [InlineData("absent_proof_fingerprint")]
    [InlineData("source_route")]
    [InlineData("source_protocol")]
    [InlineData("source_epoch")]
    [InlineData("source_publication")]
    [InlineData("source_runtime")]
    [InlineData("source_leg")]
    [InlineData("source_transport")]
    [InlineData("source_bridge")]
    [InlineData("source_data_identity")]
    [InlineData("source_data_generation")]
    [InlineData("barrier_admission")]
    [InlineData("barrier_negotiation")]
    [InlineData("barrier_pause")]
    [InlineData("missing_barrier_removal")]
    [InlineData("duplicate_barrier_terminal")]
    [InlineData("forged_close_effect")]
    [InlineData("generation_gap")]
    [InlineData("conflicting_duplicate")]
    public async Task ReceiverBarrierSnapshot_RoutePreparationEnvelopesRemainStrict(string mutation)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var evidence = await RoutePreparationEvidence.Value;
        var directory = Path.Combine(Path.GetTempPath(), "nlink-receiver-preparation-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "service-evidence.log");
            await File.WriteAllLinesAsync(path, evidence, new UTF8Encoding(false));
            var result = await RunPowerShellScriptTextAsync(RoutePreparationHarness, [FindRepoRoot(), path, mutation]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            Assert.Equal("receiver-preparation-evidence-ok", result.Stdout.Trim());
        }
        finally { TryDeleteDirectory(directory); }
    }

    private const string RoutePreparationHarness = """
        param([string]$RepoRoot, [string]$InputPath, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
        $capturedLines = [IO.File]::ReadAllLines($InputPath)
        $preparations = @(for ($i = 0; $i -lt $capturedLines.Count; $i++) {
            if ([string]::IsNullOrWhiteSpace($capturedLines[$i])) { continue }
            if ((Get-FileTransferCoordinatorSnapshotEventName $capturedLines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
            $decoded = ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $capturedLines[$i] 'snapshot_payload')
            if ($decoded.Value.decisionKind -ceq 'PrepareFileTransferRouteTransition' -and
                @($decoded.Value.aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' -and $_.afterState -ceq 'Invalidated' }).Count -eq 1) {
                [pscustomobject]@{ Index=$i; Envelope=$decoded.Value }
            }
        })
        if ($preparations.Count -ne 1) { throw 'Expected one genuine service receiver-barrier route preparation.' }
        $preparationIndex = $preparations[0].Index
        $preparation = $preparations[0].Envelope
        $originalPreparation = $capturedLines[$preparationIndex]
        $current = @($preparation.aggregate.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'current' })[0]
        $terminal = @($preparation.aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' })[0]
        $source = $current.routeTransition.sourceBoundary
        function Copy-Operation($Operation) { ConvertFrom-Json (ConvertTo-Json -InputObject $Operation -Depth 64 -Compress) }
        switch -CaseSensitive ($Case) {
            'reordered_operations' { [array]::Reverse($preparation.aggregate.operations) }
            'missing_current' { $preparation.aggregate.operations = @($preparation.aggregate.operations | Where-Object { $_ -ne $current }) }
            'ambiguous_current' {
                $other = Copy-Operation $current
                $other.authorityId = 'second-preparation-authority'
                $other.commandIdentity = $other.authorityId
                $other.operationKey = "route_transition|current|$($other.authorityId)|$($other.operationGeneration)|RegularV4ToTunaV4"
                $preparation.aggregate.operations += $other
            }
            'attempted_only' { $current.operationRole = 'inbound'; $current.routeTransition.role = 'inbound'; $current.stateApplied = $false }
            'historical_prepared' { $current.beforeState = 'Prepared' }
            'not_applied' { $current.stateApplied = $false }
            'untyped_applied' { $current.stateApplied = 'true' }
            'target_route' { $current.routeTransition.targetBoundary.route = 'regular_nkn_v4_fast' }
            'target_protocol' { $current.routeTransition.targetBoundary.protocolVersion = 6 }
            'rejected_preparation' { $preparation.decisionStatus = 'Rejected' }
            'wrong_direction' { $preparation.direction = 'outbound' }
            'wrong_key' { $current.operationKey = 'unrelated-key' }
            'wrong_authority' { $current.authorityId = '(none)' }
            'wrong_command_identity' { $current.commandIdentity = 'unrelated-command' }
            'wrong_command_generation' { $current.commandGeneration++ }
            'absent_command_fingerprint' { $current.commandFingerprint = '(none)' }
            'receipt_identity' { $current.receiptIdentity = $current.authorityId }
            'receipt_generation' { $current.receiptGeneration = 1 }
            'receipt_status' { $current.receiptStatus = 'Installed' }
            'receipt_fingerprint' { $current.receiptFingerprint = 'a' * 64 }
            'receipt_validated' { $current.receiptValidated = $true }
            'typed_receipt' {
                $current.routeTransition.receipts = @([pscustomobject]@{
                    stage='pause_bind'; commandIdentity=$current.commandIdentity; commandGeneration=$current.commandGeneration
                    commandFingerprint=$current.commandFingerprint; receiptIdentity=$current.authorityId
                    receiptGeneration=$current.operationGeneration; receiptStatus='Installed'; receiptReason='fabricated'
                    receiptFingerprint=('a' * 64); receiptValidated=$true
                })
            }
            'checkpoint_zero' { $source.checkpointGeneration = 0 }
            'checkpoint_negative' { $source.checkpointGeneration = -1 }
            'checkpoint_absent_identity' { $source.checkpointRequestId = '(none)' }
            'checkpoint_wrong_raw_identity' { $source.checkpointRequestId = "checkpoint:$([long]$source.checkpointGeneration + 1L)" }
            'checkpoint_wrong_projected_identity' { $source.checkpointRequestId = 'fte1-' + ('a' * 24) }
            'checkpoint_target_generation' { $current.routeTransition.targetBoundary.checkpointGeneration++ }
            'checkpoint_target_identity' { $current.routeTransition.targetBoundary.checkpointRequestId = 'checkpoint:999' }
            'absent_durable_fingerprint' { $current.routeTransition.durableCheckpointFingerprint = '(none)' }
            'absent_proof_fingerprint' { $current.routeTransition.proofBoundaryFingerprint = '(none)' }
            'source_route' { $source.route = 'file_tuna_v4' }
            'source_protocol' { $source.protocolVersion = 6 }
            'source_epoch' { $source.liveRouteEpoch = 1 }
            'source_publication' { $source.routePublicationGeneration++ }
            'source_runtime' { $source.runtimeGeneration++ }
            'source_leg' { $source.legGeneration++ }
            'source_transport' { $source.transportEpoch = 1 }
            'source_bridge' { $source.bridgeRecoveryGeneration = 1 }
            'source_data_identity' { $source.dataSessionIdentity = 'unrelated-data-session' }
            'source_data_generation' { $source.dataSessionGeneration++ }
            'barrier_admission' { $terminal.receiverBarrier.admissionFingerprint = 'a' * 64 }
            'barrier_negotiation' { $terminal.receiverBarrier.negotiationFingerprint = 'a' * 64 }
            'barrier_pause' { $terminal.receiverBarrier.pauseGeneration++ }
            'missing_barrier_removal' { $preparation.aggregate.operations = @($preparation.aggregate.operations | Where-Object { $_.operationRole -cne 'receiver_barrier_current' }) }
            'duplicate_barrier_terminal' { $preparation.aggregate.operations += (Copy-Operation $terminal) }
            'generation_gap' { $preparation.aggregate.afterStateGeneration++ }
        }
        function Set-EnvelopeOnOriginalLine($Envelope) {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
            $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
            $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $line = $originalPreparation.Replace(
                (Get-FileTransferCoordinatorSnapshotLineField $originalPreparation 'snapshot_sha256'), $hash).Replace(
                (Get-FileTransferCoordinatorSnapshotLineField $originalPreparation 'snapshot_payload'), $payload)
            if ($Case -ceq 'generation_gap') {
                $line = $line.Replace("after_state_generation=$($Envelope.aggregate.afterStateGeneration - 1);",
                    "after_state_generation=$($Envelope.aggregate.afterStateGeneration);")
            }
            $line
        }
        $lines = [Collections.Generic.List[string]]::new()
        $lines.Add("event=unrelated_fixture_diagnostic; note=before_stream")
        for ($i = 0; $i -lt $capturedLines.Count; $i++) {
            $line = $capturedLines[$i]
            if ($i -eq $preparationIndex -and $Case -cnotin @('route_prepare','exact_duplicate','conflicting_duplicate','forged_close_effect')) {
                $line = Set-EnvelopeOnOriginalLine $preparation
            }
            $lines.Add($line)
            if ($i -ne $preparationIndex) { continue }
            if ($Case -ceq 'exact_duplicate') { $lines.Add($line) }
            if ($Case -ceq 'conflicting_duplicate') {
                $changed = Copy-Operation $preparation
                $changed.decisionReason = 'conflicting-preparation'
                $lines.Add((Set-EnvelopeOnOriginalLine $changed))
            }
            if ($Case -ceq 'forged_close_effect') {
                $scopeFields = @('snapshot_contract_version','snapshot_selection_mode','snapshot_catalog_version',
                    'snapshot_catalog_hash','coordinator_instance_id','session_id','transfer_id','direction','log_writer_id')
                $scope = (@($scopeFields | ForEach-Object {
                    "$_=$(Get-FileTransferCoordinatorSnapshotLineField $originalPreparation $_)"
                }) -join '; ')
                $receipt = $terminal.receiverBarrier.receipts[0]
                $barrierSource = $terminal.receiverBarrier.sourceBoundary
                $lines.Add("event=filetransfer_coordinator_authority_effect; $scope; transition_id=$($preparation.transitionId); coordinator_state_generation=$($preparation.aggregate.afterStateGeneration); operation_family=responder_probe_handoff; operation_key=$($terminal.operationKey); operation_authority_id=$($terminal.authorityId); operation_generation=$($terminal.operationGeneration); effect_kind=receiver_barrier_close_applied; effect_identity=forged-close-effect; effect_generation=1; receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($barrierSource.route); protocol_version=$($barrierSource.protocolVersion); live_route_epoch=$($barrierSource.liveRouteEpoch); route_publication_generation=$($barrierSource.routePublicationGeneration); route_commit_generation=$($barrierSource.routeCommitGeneration); runtime_generation=$($barrierSource.runtimeGeneration); terminal_generation=$($barrierSource.terminalGeneration); leg_generation=$($barrierSource.legGeneration); transport_epoch=$($barrierSource.transportEpoch); bridge_recovery_generation=$($barrierSource.bridgeRecoveryGeneration); checkpoint_generation=$($barrierSource.checkpointGeneration); checkpoint_request_id=$($barrierSource.checkpointRequestId); data_session_identity=$($barrierSource.dataSessionIdentity); data_session_generation=$($barrierSource.dataSessionGeneration); authority_effect=applied")
            }
        }
        $compactRecords = @(for ($i = 0; $i -lt $lines.Count; $i++) {
            if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
            $eventName = Get-FileTransferCoordinatorSnapshotEventName $lines[$i]
            if ($catalog.SnapshotAuthorityV1 -contains $eventName -or $catalog.GenerationStampedEffects -contains $eventName) {
                [pscustomobject]@{ Line=$lines[$i]; LineIndex=$i }
            }
        })
        if ($compactRecords.Count -ge $lines.Count) { throw 'Fixture compact input was not independently filtered.' }
        $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines.ToArray()
        $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
        $expected = if ($Case -cin @('route_prepare','reordered_operations','exact_duplicate')) { 'pass' } else { 'fail' }
        if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
            throw "case=$Case; expected=$expected; full=$($full.ArtifactEvidenceFailureReason); compact=$($compact.ArtifactEvidenceFailureReason)"
        }
        $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
        if ($parity.Verdict -cne 'pass' -or
            (ConvertTo-Json -InputObject @($full.Boundaries) -Depth 100 -Compress) -cne
            (ConvertTo-Json -InputObject @($compact.Boundaries) -Depth 100 -Compress)) { throw "Boundary parity failed: $($parity.FailureReason)" }
        $capturedTransitions = @($capturedLines | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
                (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot'
        }).Count
        $capturedEffects = @($capturedLines | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
                (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect'
        }).Count
        if ($expected -ceq 'pass' -and (-not $full.StreamComplete -or $full.LegacySelectionCount -ne 0 -or
            $full.TransitionCount -ne $capturedTransitions -or $capturedEffects -le 0 -or
            $full.EffectCount -ne $capturedEffects)) { throw 'Preparation fixture lost genuine service evidence.' }
        # All service effects remain byte-identical except the named forged-effect mutation.
        # Cross-host shared-GUI and route-consistency acceptance remain separate gates.
        Write-Output 'receiver-preparation-evidence-ok'
        """;

    private const string Harness = """
        param([string]$RepoRoot, [string]$EnvelopePath, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
        $parsed = Get-Content -LiteralPath $EnvelopePath -Raw | ConvertFrom-Json
        if ($Case.StartsWith('pre_admission')) { $catalog = Get-FileTransferCoordinatorReceiverPreAdmissionCatalog }
        $envelopes = @($parsed)
        $closed = @($envelopes[-1].aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' })[0]
        $attempt = @($envelopes[-1].aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_attempt' })
        switch -CaseSensitive ($Case) {
            'pre_admission_old_profile' { $catalog = Get-FileTransferCoordinatorActivationRetryCatalog }
            'pre_admission_missing_lineage' { $closed.receiverBarrier.PSObject.Properties.Remove('preAdmissionClose') }
            'pre_admission_changed_scope' { $closed.receiverBarrier.preAdmissionClose.negotiationWithoutLaunchFingerprint = 'a' * 64 }
            'pre_admission_wrong_mode' { $closed.receiverBarrier.preAdmissionClose.mode = 'Admitted' }
            'pre_admission_changed_launch' { $closed.receiverBarrier.preAdmissionClose.failureLaunchGeneration = -1 }
            'pre_admission_changed_terminal_witness' { $closed.receiverBarrier.preAdmissionClose.failureNegotiationFingerprint = 'a' * 64 }
            'frozen_profile' { $catalog = Get-FileTransferCoordinatorEvidenceCatalog }
            'source_protocol' { $closed.receiverBarrier.sourceBoundary.protocolVersion = 6 }
            'receipt_stage' { $closed.receiverBarrier.receipts[0].stage = 'handoff_bind' }
            'pause_mutation' { $closed.receiverBarrier.pauseGeneration = 2 }
            'negotiation_mutation' { $closed.receiverBarrier.negotiationFingerprint = 'a' * 64 }
            'attempt_mutation' { $attempt[0].stateApplied = $true }
            'missing_bind' { $envelopes = @($envelopes | Select-Object -Skip 1) }
        }
        $first = $envelopes[0]
        $last = $envelopes[-1]
        $scope = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction); log_writer_id=receiver-barrier-analyzer-fixture"
        $families = $catalog.RequiredFamilies -join ','
        $coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
        $lines = [Collections.Generic.List[string]]::new()
        $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $scope; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
        foreach ($envelope in $envelopes) {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $envelope -Depth 64 -Compress))
            $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
            $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $lines.Add("event=filetransfer_coordinator_transition_snapshot; $scope; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none")
        }
        # Analyzer-only serialization from the genuine Coordinator close receipt;
        # the production callback fixture separately verifies actual effect emission.
        if ($Case -cnotin @('missing_effect','automatic_invalidation','pre_admission_admission_race')) {
            $effectOperation = if ($Case -ceq 'attempt_effect') { $attempt[0] } else { $closed }
            $receipt = $effectOperation.receiverBarrier.receipts[0]
            $source = $effectOperation.receiverBarrier.sourceBoundary
            $effect = "event=filetransfer_coordinator_authority_effect; $scope; transition_id=$($last.transitionId); coordinator_state_generation=$($last.aggregate.afterStateGeneration); operation_family=responder_probe_handoff; operation_key=$($effectOperation.operationKey); operation_authority_id=$($effectOperation.authorityId); operation_generation=$($effectOperation.operationGeneration); effect_kind=receiver_barrier_close_applied; effect_identity=receiver-close-effect; effect_generation=1; receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($source.route); protocol_version=$($source.protocolVersion); live_route_epoch=$($source.liveRouteEpoch); route_publication_generation=$($source.routePublicationGeneration); route_commit_generation=$($source.routeCommitGeneration); runtime_generation=$($source.runtimeGeneration); terminal_generation=$($source.terminalGeneration); leg_generation=$($source.legGeneration); transport_epoch=$($source.transportEpoch); bridge_recovery_generation=$($source.bridgeRecoveryGeneration); checkpoint_generation=$($source.checkpointGeneration); checkpoint_request_id=$($source.checkpointRequestId); data_session_identity=$($source.dataSessionIdentity); data_session_generation=$($source.dataSessionGeneration); authority_effect=applied"
            $lines.Add($effect)
            if ($Case -ceq 'duplicate_effect') { $lines.Add($effect.Replace('receiver-close-effect','receiver-close-effect-duplicate')) }
        }
        $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $scope; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($envelopes.Count); $coverage; authority_effect=none")
        $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines.ToArray()
        $records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line = $lines[$i]; LineIndex = $i } })
        $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
        $expected = if ($Case -cin @('none','automatic_invalidation','pre_admission','pre_admission_zero','pre_admission_projected','pre_admission_pwsh','pre_admission_admission_race')) { 'pass' } else { 'fail' }
        if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
            throw "case=$Case; expected=$expected; full=$($full.ArtifactEvidenceFailureReason); compact=$($compact.ArtifactEvidenceFailureReason)"
        }
        $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
        if ($parity.Verdict -cne 'pass') { throw $parity.FailureReason }
        Write-Output 'receiver-barrier-evidence-ok'
        """;
}
