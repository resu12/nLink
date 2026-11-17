using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferReceiverBarrierRecoveryDecisionEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ReceiverBarrier_RecoveryDecisionOverlayHasExactCrossHostConsumerParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var bindings = new Dictionary<string, FileTransferCoordinatorAuthorityEffectBinding>(StringComparer.Ordinal);
        var envelopes = FileTransferCoordinatorTests.CreateReceiverBarrierRecoveryDecisionOverlayFixture(
            observeRecoveryDecision: decision =>
            {
                if (decision.SnapshotAuthorityEffectBinding is { } binding)
                {
                    bindings.Add(decision.SnapshotEnvelope!.TransitionId, binding);
                }
            });
        var directory = Path.Combine(Path.GetTempPath(), "nlink-receiver-recovery-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "projected-evidence.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes, bindings), new UTF8Encoding(false));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), path, directory], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                Assert.Contains("receiver-recovery-evidence-ok", result.Stdout);
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("receiver-recovery-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    private static string[] Emit(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes,
        IReadOnlyDictionary<string, FileTransferCoordinatorAuthorityEffectBinding> bindings)
    {
        var service = typeof(SessionFileTransferService);
        var emitterType = service.GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(emitterType, nonPublic: true)!;
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        // Replace only the asynchronous sink. Projection, continuity and receipt
        // indexing execute production code, as in the recovery-successor fixture.
        emitterType.GetField("drainScheduled", instance)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(true, emitterType.GetMethod("Start", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));
        var pressure = service.GetMethods(statics).Single(method =>
            method.Name == "EnqueueRegularV4PressureDecisionAuthorityEffect" && method.GetParameters()[0].ParameterType == emitterType);
        var recoveries = service.GetMethods(statics).Where(method =>
            method.Name == "EnqueueReceiveRecoveryAuthorityEffect" && method.GetParameters()[0].ParameterType == emitterType).ToArray();
        var exactRecovery = recoveries.Single(method => method.GetParameters()[^1].ParameterType == typeof(FileTransferCoordinatorAuthorityEffectBinding));
        var roleRecovery = recoveries.Single(method => method.GetParameters().Length == 9);
        var recoveryKind = service.GetMethod("GetReceiveRecoveryDecisionEffect", statics)!;
        foreach (var envelope in envelopes)
        {
            emitterType.GetMethod("Enqueue", instance)!.Invoke(emitter, [envelope]);
            object?[] common = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId,
                envelope.TransferId, direction, envelope.Aggregate.AfterStateGeneration, envelope];
            pressure.Invoke(null, common);
            if (bindings.TryGetValue(envelope.TransitionId, out var binding))
            {
                exactRecovery.Invoke(null, [.. common, binding]);
            }
            else if (recoveryKind.Invoke(null, [envelope]) is ValueTuple<string, string> effect)
            {
                roleRecovery.Invoke(null, [.. common, effect.Item1, effect.Item2]);
            }

            // Analyzer-only projection of the genuine supplied-handoff receipt.
            // This does not claim that a physical service callback ran. That is
            // covered by ReceiverBarrier_RoutePreparationUsesRealServiceEffectsWithoutCloseEffect.
            foreach (var operation in envelope.Aggregate.Operations.Where(operation =>
                operation.OperationRole == "route_transition_handoff_pending" && operation.ResponderProbeHandoff is not null))
            {
                foreach (var receipt in operation.ResponderProbeHandoff!.Receipts.Where(receipt => receipt.ReceiptValidated))
                {
                    emitterType.GetMethod("EnqueueAuthorityEffect", instance)!.Invoke(emitter,
                        [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                            envelope.Aggregate.AfterStateGeneration, operation.Family, operation.AuthorityId,
                            operation.OperationGeneration, receipt.ReceiptIdentity, receipt.ReceiptGeneration,
                            receipt.ReceiptStatus, receipt.ReceiptFingerprint, "route_transition_handoff_supplied",
                            $"{operation.OperationKey}:route_transition_handoff_supplied", Math.Max(1, receipt.ReceiptGeneration), null]);
                }
            }
        }
        emitterType.GetMethod("Complete", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)emitterType.GetField("pending", instance)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=receiver-recovery-fixture; log_writer_sequence={index + 1}")).ToArray();
    }

    private const string Harness = """
        param([string]$RepoRoot, [string]$InputPath, [string]$FixtureRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
        if ($catalog.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94') { throw 'Receiver catalog changed.' }
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
        foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
            $functions = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
            }, $true))
            if ($functions.Count -ne 1) { throw "Missing shared GUI function: $name" }
            . ([scriptblock]::Create($functions[0].Extent.Text))
        }
        $original = [IO.File]::ReadAllLines($InputPath)
        function Decode($Line) {
            (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_payload)).Value
        }
        function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
        function Replace-Envelope([string]$Line, $Envelope) {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
            $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
            $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $Line.Replace((Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_sha256), $hash).Replace(
                (Get-FileTransferCoordinatorSnapshotLineField $Line snapshot_payload), $payload)
        }
        $snapshots = @(for ($i = 0; $i -lt $original.Count; $i++) {
            if ((Get-FileTransferCoordinatorSnapshotEventName $original[$i]) -ceq 'filetransfer_coordinator_transition_snapshot') {
                [pscustomobject]@{ Index=$i; Envelope=(Decode $original[$i]) }
            }
        })
        $bind = @($snapshots | Where-Object { $_.Envelope.decisionKind -ceq 'BindInitialActivationReceiverBarrier' })
        $proof = @($snapshots | Where-Object { $_.Envelope.decisionKind -ceq 'ObserveReceiveRecoveryProof' })
        $complete = @($snapshots | Where-Object { $_.Envelope.decisionKind -ceq 'ObserveReceiveRecoveryExecutionReceipt' })
        $handoff = @($snapshots | Where-Object { $_.Envelope.decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff' })
        $prepare = @($snapshots | Where-Object { $_.Envelope.decisionKind -ceq 'PrepareFileTransferRouteTransition' })
        if ($bind.Count -ne 1 -or $proof.Count -ne 1 -or $complete.Count -ne 1 -or $handoff.Count -ne 1 -or $prepare.Count -ne 1) { throw 'Genuine overlay chain missing.' }
        $cases = @('exact', 'reordered', 'exact_duplicate', 'missing_current', 'attempted_only', 'ambiguous_current',
            'current_removed', 'wrong_decision', 'wrong_kind', 'not_applied', 'untyped_applied', 'source_bridge',
            'effective_bridge', 'source_route', 'source_protocol', 'source_epoch', 'source_publication', 'source_runtime',
            'source_leg', 'source_transport', 'source_data_identity', 'source_data_generation', 'source_terminal',
            'recovery_source', 'recovery_effective', 'recovery_data_session', 'recovery_terminal', 'recovery_aba',
            'orphan_carry', 'bad_before_state', 'removal_rebirth', 'duplicate_current_roots',
            'preparation_source_bridge', 'preparation_effective_bridge', 'missing_proof_effect')
        $outputs = [Collections.Generic.List[object]]::new()
        foreach ($case in $cases) {
            $changed = @($original)
            $item = if ($case -cin @('current_removed', 'recovery_aba', 'removal_rebirth')) { $complete[0] }
                elseif ($case -ceq 'bad_before_state') { $proof[0] }
                elseif ($case -cin @('missing_current','attempted_only','ambiguous_current','wrong_decision','wrong_kind',
                    'not_applied','untyped_applied','recovery_source','recovery_effective','recovery_data_session','recovery_terminal',
                    'orphan_carry','duplicate_current_roots')) { $handoff[0] }
                elseif ($case -like 'preparation_*') { $prepare[0] } else { $bind[0] }
            $envelope = Copy-Value $item.Envelope
            $barriers = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_current' })
            $recoveries = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' })
            if ($barriers.Count -gt 1 -or $recoveries.Count -gt 1) { throw "$case has an ambiguous unmodified fixture subject." }
            $barrier = $barriers | Select-Object -First 1
            $recovery = $recoveries | Select-Object -First 1
            $needsRecovery = $case -cin @('missing_current','attempted_only','ambiguous_current','current_removed',
                'wrong_decision','wrong_kind','not_applied','untyped_applied','recovery_source','recovery_effective',
                'recovery_data_session','recovery_terminal','recovery_aba','orphan_carry','bad_before_state',
                'removal_rebirth','duplicate_current_roots')
            $needsBarrier = $case -like 'source_*' -or $case -ceq 'effective_bridge' -or $case -ceq 'preparation_effective_bridge'
            if ($needsRecovery -and $recoveries.Count -ne 1) { throw "$case requires one genuine recovery subject." }
            if ($needsBarrier -and $barriers.Count -ne 1) { throw "$case requires one genuine receiver barrier." }
            $route = $null
            if ($case -ceq 'preparation_source_bridge') {
                $routes = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'current' })
                if ($routes.Count -ne 1) { throw "$case requires one genuine route preparation." }
                $route = $routes[0]
            }
            $source = if ($null -ne $barrier) { $barrier.receiverBarrier.sourceBoundary } else { $null }
            switch -CaseSensitive ($case) {
                'reordered' { [array]::Reverse($envelope.aggregate.operations) }
                'missing_current' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $recovery }) }
                'attempted_only' { $recovery.operationRole = 'operation_attempt'; $recovery.receiveRecovery.role = 'operation_attempt'; $recovery.stateApplied = $false }
                'ambiguous_current' {
                    $other = Copy-Value $recovery
                    $other.authorityId = 'fte1-' + ('a' * 24)
                    $other.operationKey = $other.operationKey.Replace($recovery.authorityId, $other.authorityId)
                    $envelope.aggregate.operations += $other
                }
                'current_removed' { $recovery.afterState = 'None'; $recovery.stateApplied = $true }
                'wrong_decision' { $recovery.receiveRecovery.bridgeDecisionGeneration++ }
                'wrong_kind' { $recovery.operationKind = 'FallbackCheckpointRecovery'; $recovery.receiveRecovery.recoveryKind = 'FallbackCheckpointRecovery' }
                'not_applied' { $recovery.stateApplied = $false }
                'untyped_applied' { $recovery.stateApplied = 'true' }
                'source_bridge' { $source.bridgeRecoveryGeneration = 2 }
                'effective_bridge' { $barrier.receiverBarrier.effectiveBoundary.bridgeRecoveryGeneration = 0 }
                'source_route' { $source.route = 'file_tuna_v4' }
                'source_protocol' { $source.protocolVersion = 6 }
                'source_epoch' { $source.liveRouteEpoch++ }
                'source_publication' { $source.routePublicationGeneration++ }
                'source_runtime' { $source.runtimeGeneration++ }
                'source_leg' { $source.legGeneration++ }
                'source_transport' { $source.transportEpoch++ }
                'source_data_identity' { $source.dataSessionIdentity = 'fte1-' + ('b' * 24) }
                'source_data_generation' { $source.dataSessionGeneration++ }
                'source_terminal' { $source.terminalGeneration++ }
                'recovery_source' { $recovery.receiveRecovery.sourceBoundary.bridgeRecoveryGeneration = 1 }
                'recovery_effective' { $recovery.receiveRecovery.effectiveBoundary.bridgeRecoveryGeneration = 1 }
                'recovery_data_session' { $recovery.receiveRecovery.effectiveBoundary.dataSessionGeneration++ }
                'recovery_terminal' { $recovery.receiveRecovery.effectiveBoundary.terminalGeneration++ }
                'recovery_aba' { $recovery.afterState = 'None'; $recovery.stateApplied = $true }
                'orphan_carry' { $recovery.beforeState = $recovery.afterState }
                'bad_before_state' { $recovery.beforeState = 'Issued:None' }
                'removal_rebirth' { $recovery.afterState = 'None'; $recovery.stateApplied = $true }
                'duplicate_current_roots' { $envelope.aggregate.operations += (Copy-Value $recovery) }
                'preparation_source_bridge' { $route.routeTransition.sourceBoundary.bridgeRecoveryGeneration = 1 }
                'preparation_effective_bridge' { $barrier.receiverBarrier.effectiveBoundary.bridgeRecoveryGeneration = 0 }
            }
            if ($case -cnotin @('exact', 'exact_duplicate', 'missing_proof_effect')) {
                $changed[$item.Index] = Replace-Envelope $changed[$item.Index] $envelope
                if ($changed[$item.Index] -ceq $original[$item.Index]) { throw "$case did not change its intended envelope." }
            }
            if ($case -cin @('recovery_aba', 'removal_rebirth')) {
                $restored = Copy-Value $proof[0].Envelope
                $restoredRecoveries = @($restored.aggregate.operations | Where-Object { $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' })
                if ($restoredRecoveries.Count -ne 1) { throw "$case requires one genuine recovery proof subject." }
                $restoredRecovery = $restoredRecoveries[0]
                $restoredRecovery.beforeState = 'None'
                if ($case -ceq 'removal_rebirth') { $restoredRecovery.afterState = 'Issued:None' }
                $changed[$proof[0].Index] = Replace-Envelope $changed[$proof[0].Index] $restored
            }
            if ($case -ceq 'exact_duplicate') {
                $duplicateIndex = [int]$bind[0].Index
                if ($duplicateIndex -ge $changed.Count - 1) { throw 'Bind must precede stream completion.' }
                $changed = @($changed[0..$duplicateIndex]) + @($changed[$duplicateIndex]) +
                    @($changed[($duplicateIndex + 1)..($changed.Count - 1)])
            }
            if ($case -ceq 'missing_proof_effect') {
                $changed = @($changed | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -cne 'receive_proof_observed' })
                if ($changed.Count -ne $original.Count - 1) { throw 'Expected exactly one genuine proof effect.' }
            }
            $fullLines = @('event=unrelated_fixture_diagnostic; note=independent_compaction') + $changed
            $compactRecords = @(for ($i = 0; $i -lt $fullLines.Count; $i++) {
                $eventName = Get-FileTransferCoordinatorSnapshotEventName $fullLines[$i]
                if ($catalog.SnapshotAuthorityV1 -contains $eventName -or $catalog.GenerationStampedEffects -contains $eventName) {
                    [pscustomobject]@{ Line=$fullLines[$i]; LineIndex=$i }
                }
            })
            if ($compactRecords.Count -ge $fullLines.Count) { throw 'Compact input was not independently filtered.' }
            $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
            $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
            $expected = if ($case -cin @('exact', 'reordered', 'exact_duplicate')) { 'pass' } else { 'fail' }
            if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
                throw "$case expected=$expected; full=$($full.ArtifactEvidenceFailureReason); compact=$($compact.ArtifactEvidenceFailureReason)"
            }
            $json = ConvertTo-Json -InputObject @($full.Boundaries) -Depth 100 -Compress
            if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
                $json -cne (ConvertTo-Json -InputObject @($compact.Boundaries) -Depth 100 -Compress)) { throw "$case normalized full/compact parity failed." }
            if (-not $full.StreamComplete -or $full.LegacySelectionCount -ne 0) { throw "$case lost complete authoritative fixture scope." }
            $selectedBoundary = $full.FirstUnresolvedBoundary
            if ($expected -ceq 'pass') {
                $fullRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
                $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
                if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $fullRoute $compactRoute).Verdict -cne 'pass') { throw "$case route-boundary parity failed." }
                $unresolved = @($full.Boundaries | Where-Object { $_.unresolvedCount -gt 0 })
                if ($unresolved.Count -ne 1 -or $unresolved[0].taxonomyClass -cne 'filetransfer_route_transition_incomplete' -or
                    $unresolved[0].boundaryId -cne $prepare[0].Envelope.transitionId -or
                    $unresolved[0].rawCount -ne 1 -or $unresolved[0].distinctCount -ne 1 -or
                    $unresolved[0].recoveredCount -ne 0 -or $unresolved[0].unresolvedCount -ne 1) {
                    throw "$case lost the exact genuine incomplete Prepared boundary."
                }
                $preparedFinding = "snapshot route authority boundary is unresolved: class=filetransfer_route_transition_incomplete; transition_id=$($prepare[0].Envelope.transitionId)"
                if ($fullRoute.Verdict -cne 'fail' -or @($fullRoute.Findings).Count -ne 2 -or
                    @($fullRoute.Findings) -cnotcontains $preparedFinding -or
                    @($fullRoute.Findings) -cnotcontains 'snapshot authoritative route stream contains no route publications') {
                    throw "$case changed the fixture's exact Prepared and missing-publication findings: $($fullRoute.Findings -join '|')"
                }
                if ($null -eq $selectedBoundary) { $selectedBoundary = $fullRoute.FirstUnresolvedBoundary }
            }
            $guiResults = @()
            foreach ($input in @([pscustomobject]@{Name='full';Lines=$fullLines}, [pscustomobject]@{Name='compact';Lines=@($compactRecords | ForEach-Object Line)})) {
                $guiPath = Join-Path $FixtureRoot ($input.Name + '-gui.log')
                [IO.File]::WriteAllLines($guiPath, [string[]]$input.Lines)
                $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
                $boundary = $selectedBoundary
                if ($null -eq $boundary) {
                    if ($null -ne $gui) { throw "$case GUI invented a boundary." }
                }
                elseif ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
                    $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
                    $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
                    $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
                    $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw "$case GUI normalized boundary mismatch." }
                $guiResults += if ($null -eq $gui) { $null } else {
                    [pscustomobject]@{ BoundaryId=$gui.CoordinatorBoundaryId; BoundaryHash=$gui.CoordinatorBoundaryHash }
                }
            }
            # The fixture starts from an initialized Regular owner and ends at
            # Prepared. Route-cycle acceptance is not claimed by this evidence gate.
            $outputs.Add([ordered]@{ Case=$case; Verdict=$full.ArtifactEvidenceVerdict; Boundaries=$json; Gui=$guiResults })
        }
        'receiver-recovery-normalized=' + (ConvertTo-Json -InputObject $outputs.ToArray() -Depth 100 -Compress)
        'receiver-recovery-evidence-ok'
        """;
}
