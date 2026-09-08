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
    public async Task ReceiverBarrierSnapshot_RealCoordinatorEnvelopesRemainStrict(string mutation)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var fixture = ((FileTransferCoordinatorState State, InitialActivationReceiverBarrierSource Source,
            InitialActivationReceiverBarrierNegotiation Negotiation))typeof(FileTransferCoordinatorTests)
            .GetMethod("CreateReceiverBarrierFixture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(
            fixture.State, fixture.Source, 1, fixture.Negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, bound.Status);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope> { bound.SnapshotEnvelope! };
        if (mutation == "automatic_invalidation")
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
            await File.WriteAllTextAsync(path, "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]", new UTF8Encoding(false));
            var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), path, mutation]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            Assert.Equal("receiver-barrier-evidence-ok", result.Stdout.Trim());
        }
        finally { TryDeleteDirectory(directory); }
    }

    private const string Harness = """
        param([string]$RepoRoot, [string]$EnvelopePath, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
        $parsed = Get-Content -LiteralPath $EnvelopePath -Raw | ConvertFrom-Json
        $envelopes = @($parsed)
        $closed = @($envelopes[-1].aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' })[0]
        $attempt = @($envelopes[-1].aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_attempt' })
        switch -CaseSensitive ($Case) {
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
        if ($Case -cnotin @('missing_effect','automatic_invalidation')) {
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
        $expected = if ($Case -cin @('none','automatic_invalidation')) { 'pass' } else { 'fail' }
        if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
            throw "case=$Case; expected=$expected; full=$($full.ArtifactEvidenceFailureReason); compact=$($compact.ArtifactEvidenceFailureReason)"
        }
        $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
        if ($parity.Verdict -cne 'pass') { throw $parity.FailureReason }
        Write-Output 'receiver-barrier-evidence-ok'
        """;
}
