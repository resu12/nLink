using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferOpsScriptSurfaceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOpsScripts_ParseWithoutSyntaxErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var scripts = new[]
            {
                "tools/FileTransfer-Ops.ps1"
            }
            .Concat(ImplementationFiles)
            .Select(relativePath => Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();

        foreach (var scriptPath in scripts)
        {
            Assert.True(File.Exists(scriptPath), $"Expected file-transfer ops script to exist: {scriptPath}");

            var result = await RunParserAsync(scriptPath);
            Assert.True(
                result.ExitCode == 0,
                $"File-transfer ops script parser validation failed for {scriptPath}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        }
    }

    [Fact]
    public void FileTransferOps_PublicModesAndParametersRemainStable()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "FileTransfer-Ops.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Equal(ExpectedFileTransferOpsModes, ExtractPowerShellValidateSetValues(scriptText, "Mode"));
        Assert.Equal(ExpectedExternalTopologyProfiles, ExtractPowerShellValidateSetValues(scriptText, "ExternalTopologyProfile"));
        Assert.Equal(ExpectedPayloadEfficiencyProfiles, ExtractPowerShellValidateSetValues(scriptText, "PayloadEfficiencyProfile"));
        Assert.Equal(new[] { "MultiToggle", "None", "RegularActivationCycle", "SwitchOff" }, ExtractPowerShellValidateSetValues(scriptText, "LiveRouteProofMode"));
        Assert.Equal(ExpectedTopLevelParameters, ExtractTopLevelPowerShellParameterNames(scriptText));
        Assert.Contains("Invoke-FileTransferRetainedAnalysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("--filetransfer-soak", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-FileTransferNknSoak.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("NknFast", scriptText, StringComparison.Ordinal);
        Assert.Contains("NknMixed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Assert-ParameterMode", scriptText, StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferBaselineComparison", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-operator-verdict.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-impairment-summary.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("mixed-screenshare-summary.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE", scriptText, StringComparison.Ordinal);
        foreach (var artifactName in RequiredLiveNknArtifactFiles)
        {
            Assert.Contains(artifactName, scriptText, StringComparison.Ordinal);
        }
        Assert.Contains("OpsSuite=ScriptSurface|OpsSuite=GuiHarness|OpsSuite=RetainedAuthorityProof|OpsSuite=RetainedRouteLaw|OpsSuite=RetainedFallback|OpsSuite=RetainedRuntimeUnlock|OpsSuite=RetainedV4Transport|OpsSuite=AcceptancePhase4|OpsSuite=AcceptancePhase5Fast|OpsSuite=AcceptancePhase5Full|OpsSuite=NknSoakAndBaseline|FullyQualifiedName~FileTransferSoakRunnerTests", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpsSuite=AcceptancePhase5|", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransferOps_RetainedAnalysisBoundedArtifactsRemainStable()
    {
        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));
        var tunaWrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var routeWrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains("FileTransferRetainedEventIndex", File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferSoak", "LogParsing.ps1")), StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-stage-diagnostics.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-stage-diagnostics.txt", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-compact-summary.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-efficiency-summary.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-efficiency-summary.txt", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferEfficiencySummaryArtifacts", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("-AlreadyChronologicallyOrdered", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("fileTunaV4DataPathDownstreamEvents", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_route_transition_physical_send_lease_registered", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_route_transition_physical_send_lease_retired", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("retained_analysis_timeout", tunaWrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-timeout-summary.json", tunaWrapperText, StringComparison.Ordinal);
        Assert.Contains("retained_analysis_timeout", routeWrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-timeout-summary.json", routeWrapperText, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransferOps_CompactCorrelatorHistoryAndRepairOrderingReuseRemainExplicit()
    {
        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(
            Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));

        Assert.Contains("correlator_history", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("$correlatorHistory.Add($historyEntry)", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairProjectionOrderedEvents", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSemanticOrderedEvents", orchestrationText, StringComparison.Ordinal);
        Assert.Contains(
            "$_.EventName -ne 'filetransfer_data_path_send_result'",
            orchestrationText,
            StringComparison.Ordinal);
        Assert.Contains("regular_v4_frontier_repair_settlement_disposition", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_frontier_repair_pressure_authority_lifecycle_split", orchestrationText, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransferOps_EfficiencySummaryUsesBoundedSemanticNameIndex()
    {
        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));
        var start = orchestrationText.IndexOf("function Write-FileTransferEfficiencySummaryArtifacts", StringComparison.Ordinal);
        var end = orchestrationText.IndexOf("function Invoke-FileTransferRetainedAnalysis", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "Expected the efficiency-summary implementation boundary.");
        var implementation = orchestrationText[start..end];
        Assert.Contains("Get-FileTransferEfficiencySemanticEventsFromIndex", implementation, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferEfficiencyIndexedMetrics", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileTransferEfficiencyAllEventsFromIndex", implementation, StringComparison.Ordinal);
        Assert.Contains("efficiency_summary_all_events_single_pass_visit_count", implementation, StringComparison.Ordinal);
        Assert.Contains("$StageDiagnostics['current_correlator'] = 'efficiency_summary'", implementation, StringComparison.Ordinal);
        Assert.Contains("$StageDiagnostics['correlator_status'] = 'started'", implementation, StringComparison.Ordinal);
        Assert.Contains("$StageDiagnostics['correlator_status'] = 'completed'", implementation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTransferOps_LargeRawAnalysisDefersAllFieldStatisticsAndLazyAggregatesPreserveResults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(
            Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));

        Assert.Contains("$deferEagerFieldStatistics = $eventsForArtifacts.Count -gt 25000", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("$includeEagerFieldStatistics = -not ($compactRequested -or $deferEagerFieldStatistics)", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("-IncludeFieldStatistics:$includeEagerFieldStatistics", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("field_statistics_mode = 'not_decided'", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("'deferred_per_scope'", orchestrationText, StringComparison.Ordinal);

        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/SoakSummaryExtraction.ps1')

            $events = @(
                [pscustomobject]@{ EventName = 'sample'; Sequence = 1L; TransferId = 'transfer'; Fields = @{ metric = '7'; total = '4' } },
                [pscustomobject]@{ EventName = 'sample'; Sequence = 2L; TransferId = 'transfer'; Fields = @{ metric = '11'; total = '6' } },
                [pscustomobject]@{ EventName = 'sample'; Sequence = 3L; TransferId = 'transfer'; Fields = @{ metric = '-3'; total = '9' } }
            )

            Reset-FileTransferRetainedEventIndex
            $eager = Initialize-FileTransferRetainedEventIndex -Events $events -IncludeFieldStatistics:$true
            $eagerMax = Get-FileTransferMaxField -Events $events -FieldName 'metric'
            $eagerSum = Get-FileTransferSumField -Events $events -FieldName 'total'

            Reset-FileTransferRetainedEventIndex
            $deferred = Initialize-FileTransferRetainedEventIndex -Events $events -IncludeFieldStatistics:$false
            if ($deferred.FieldMax.Count -ne 0 -or $deferred.FieldSum.Count -ne 0) {
                throw 'Deferred index unexpectedly computed eager field aggregates.'
            }
            $deferredMax = Get-FileTransferMaxField -Events $events -FieldName 'metric'
            $deferredSum = Get-FileTransferSumField -Events $events -FieldName 'total'

            if ($eagerMax -ne 11L -or $deferredMax -ne $eagerMax -or
                $eagerSum -ne 19L -or $deferredSum -ne $eagerSum) {
                throw "Field aggregate mismatch: eager=$eagerMax/$eagerSum deferred=$deferredMax/$deferredSum"
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Deferred field aggregate parity failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotEvaluatorHasExactFullCompactParityAndFailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

            $catalog = Get-FileTransferCoordinatorEvidenceCatalog
            $aggregate = [ordered]@{
                beforeStateGeneration = 0L
                afterStateGeneration = 1L
                beforeStateFingerprint = 'before-hash'
                afterStateFingerprint = 'after-hash'
                beforeBoundary = [ordered]@{ route = 'regular_nkn_v4_fast' }
                afterBoundary = [ordered]@{ route = 'file_tuna_v4' }
                changedFamilies = @('route_runtime')
                operations = @([ordered]@{
                    family = 'route_runtime'
                    operationRole = 'route_publication'
                    operationKey = 'route_runtime|route_publication|authority-1|1|RoutePublication'
                    operationKind = 'RoutePublication'
                    authorityId = 'authority-1'
                    predecessorAuthorityId = '(none)'
                    operationGeneration = 1L
                    beforeState = 'None'
                    afterState = 'Published'
                    commandIdentity = 'command-1'
                    commandGeneration = 1L
                    commandFingerprint = 'command-fingerprint-1'
                    receiptIdentity = 'receipt-1'
                    receiptGeneration = 1L
                    receiptStatus = 'Accepted'
                    receiptFingerprint = 'receipt-fingerprint-1'
                    receiptValidated = $true
                    stateApplied = $true
                    routeRuntime = [ordered]@{
                        role = 'route_publication'
                        boundary = [ordered]@{
                            route = 'file_tuna_v4'
                            protocolVersion = 4
                            liveRouteEpoch = 1L
                            routePublicationGeneration = 1L
                            routeCommitGeneration = 0L
                            runtimeGeneration = 1L
                            terminalGeneration = 0L
                            legGeneration = 1L
                            transportEpoch = 0L
                            bridgeRecoveryGeneration = 0L
                            checkpointGeneration = 0L
                            checkpointRequestId = '(none)'
                            dataSessionIdentity = '(none)'
                            dataSessionGeneration = 0L
                        }
                        tokenFingerprint = 'receipt-fingerprint-1'
                    }
                    routeTransition = $null
                })
            }
            $envelope = [ordered]@{
                snapshotContractVersion = 1
                snapshotSelectionMode = 'SnapshotV1Shadow'
                coordinatorInstanceId = 'coordinator-1'
                sessionId = 'session-1'
                transferId = 'transfer-1'
                direction = 'outbound'
                transitionId = 'transition-1'
                decisionKind = 'RoutePublication'
                decisionStatus = 'Accepted'
                decisionReason = 'test'
                decisionUtcMilliseconds = 1L
                aggregate = $aggregate
            }
            $json = $envelope | ConvertTo-Json -Depth 16 -Compress
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
            $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
            $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            $prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-1; session_id=session-1; transfer_id=transfer-1; direction=outbound"
            $start = "event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=before-hash; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
            $transition = "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=transition-1; before_state_generation=0; after_state_generation=1; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            $effect = "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=transition-1; coordinator_state_generation=1; operation_family=route_runtime; operation_key=route_runtime|route_publication|authority-1|1|RoutePublication; operation_authority_id=authority-1; operation_generation=1; effect_kind=route_publication_forwarded; effect_identity=receipt-1; effect_generation=1; receipt_identity=receipt-1; receipt_generation=1; receipt_status=Accepted; receipt_fingerprint=receipt-fingerprint-1; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=0; checkpoint_request_id=(none); data_session_identity=(none); data_session_generation=0; authority_effect=applied"
            $completed = "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=1; final_state_hash=after-hash; transition_count=1; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"

            $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=unrelated', $start, 'event=other', $transition, $effect, $completed)
            $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, $transition, $effect, $completed)
            if ($full.ArtifactEvidenceVerdict -ne 'pass' -or
                $compact.ArtifactEvidenceVerdict -ne 'pass' -or
                $full.BoundaryHash -ne $compact.BoundaryHash -or
                $full.BoundaryCount -ne 1 -or
                $compact.BoundaryCount -ne 1 -or
                $full.EffectCount -ne 1 -or
                $compact.EffectCount -ne 1 -or
                -not (Test-FileTransferCoordinatorSnapshotFamilyAuthoritative -Evaluation $full -Family 'route_runtime') -or
                -not (Test-FileTransferCoordinatorSnapshotFamilyAuthoritative -Evaluation $compact -Family 'route_transition') -or
                $full.LegacySelectionCount -ne 5 -or
                $full.RouteFamilyLegacySelectionCount -ne 0) {
                throw "Snapshot parity failed: $($full | ConvertTo-Json -Compress) / $($compact | ConvertTo-Json -Compress)"
            }

            $unboundEffect = $effect -replace 'receipt_fingerprint=receipt-fingerprint-1', 'receipt_fingerprint=wrong'
            $unbound = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, $transition, $unboundEffect, $completed)
            if ($unbound.ArtifactEvidenceVerdict -ne 'fail' -or
                $null -eq $unbound.FirstUnresolvedBoundary -or
                $unbound.FirstUnresolvedBoundary.taxonomyClass -ne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
                $unbound.FirstUnresolvedBoundary.unresolvedCount -ne 1) {
                throw 'A generation-stamped effect without an exact receipt binding did not fail closed.'
            }

            $duplicateEffect = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, $transition, $effect, $effect, $completed)
            if ($duplicateEffect.ArtifactEvidenceVerdict -ne 'pass' -or $duplicateEffect.EffectCount -ne 1) {
                throw 'A byte-equivalent authority-effect duplicate did not coalesce.'
            }

            $incomplete = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, $transition, $effect)
            if ($incomplete.ArtifactEvidenceVerdict -ne 'fail' -or $incomplete.StreamComplete) {
                throw 'A partial V1 stream did not fail closed.'
            }

            $corrupt = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, ($transition -replace 'before_state_generation=0', 'before_state_generation=7'), $effect, $completed)
            if ($corrupt.ArtifactEvidenceVerdict -ne 'fail') {
                throw 'A conflicting snapshot header did not fail closed.'
            }
            $duplicateHeaderConflict = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,
                $transition,
                ($transition -replace 'after_state_generation=1', 'after_state_generation=7'),
                $effect,
                $completed)
            if ($duplicateHeaderConflict.ArtifactEvidenceVerdict -ne 'fail') {
                throw 'A byte-identical duplicate with conflicting header generations did not fail closed.'
            }

            $authoritative = @($start, $transition, $effect, $completed) |
                ForEach-Object { $_ -replace 'snapshot_selection_mode=SnapshotV1Shadow', 'snapshot_selection_mode=SnapshotV1Authoritative' }
            if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $authoritative).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'Incomplete authoritative family coverage did not fail closed.'
            }
            $overlap = @($start, $transition, $effect, $completed) |
                ForEach-Object { $_ -replace 'shadow_family_coverage=;', 'shadow_family_coverage=route_runtime;' }
            if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $overlap).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'Overlapping shadow and authoritative family coverage did not fail closed.'
            }

            $unknownVersion = @($start, $transition, $effect, $completed) |
                ForEach-Object { $_ -replace 'snapshot_contract_version=1', 'snapshot_contract_version=2' }
            if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $unknownVersion).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'An unknown snapshot contract version did not fail closed.'
            }

            $conflictingEnvelope = [ordered]@{}
            foreach ($property in $envelope.GetEnumerator()) { $conflictingEnvelope[$property.Key] = $property.Value }
            $conflictingEnvelope.decisionReason = 'conflict'
            $conflictingJson = $conflictingEnvelope | ConvertTo-Json -Depth 16 -Compress
            $conflictingBytes = [Text.UTF8Encoding]::new($false).GetBytes($conflictingJson)
            $conflictingHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $conflictingBytes
            $conflictingPayload = [Convert]::ToBase64String($conflictingBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            $conflictingTransition = "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=transition-1; before_state_generation=0; after_state_generation=1; snapshot_sha256=$conflictingHash; snapshot_payload=$conflictingPayload; authority_effect=none"
            if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start, $transition, $conflictingTransition, $effect, $completed)).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'A conflicting duplicate transition did not fail closed.'
            }

            $routeScope = [ordered]@{
                route = 'regular_nkn_v4_fast'; protocolVersion = 4
                liveRouteEpoch = 1L; routePublicationGeneration = 1L
                routeCommitGeneration = 0L; runtimeGeneration = 1L
                terminalGeneration = 0L; legGeneration = 1L
                transportEpoch = 0L; bridgeRecoveryGeneration = 0L
                checkpointGeneration = 1L; checkpointRequestId = 'checkpoint-1'
                dataSessionIdentity = 'data-session-1'; dataSessionGeneration = 1L
            }
            $routeReceipts = @(
                [ordered]@{ stage='pause_bind'; commandIdentity='transition-authority:pause_bind'; commandGeneration=1L; commandFingerprint='pause-command'; receiptIdentity='transition-authority:pause_bind'; receiptGeneration=1L; receiptStatus='Installed'; receiptReason='pause_bound'; receiptFingerprint='pause-receipt'; receiptValidated=$true },
                [ordered]@{ stage='drain'; commandIdentity='transition-authority:drain'; commandGeneration=1L; commandFingerprint='drain-command'; receiptIdentity='transition-authority:drain'; receiptGeneration=1L; receiptStatus='Completed'; receiptReason='drain_completed'; receiptFingerprint='drain-receipt'; receiptValidated=$true },
                [ordered]@{ stage='commit'; commandIdentity='transition-authority:commit'; commandGeneration=1L; commandFingerprint='commit-command'; receiptIdentity='commit:1'; receiptGeneration=1L; receiptStatus='Committed'; receiptReason='committed'; receiptFingerprint='commit-receipt'; receiptValidated=$true },
                [ordered]@{ stage='disposition'; commandIdentity='transition-authority:Committed'; commandGeneration=1L; commandFingerprint='disposition-command'; receiptIdentity='transition-authority:Committed'; receiptGeneration=1L; receiptStatus='Applied'; receiptReason='applied'; receiptFingerprint='disposition-receipt'; receiptValidated=$true }
            )
            $targetScope = [ordered]@{
                route = 'file_tuna_v4'; protocolVersion = 4
                liveRouteEpoch = 2L; routePublicationGeneration = 2L
                routeCommitGeneration = 1L; runtimeGeneration = 2L
                terminalGeneration = 0L; legGeneration = 2L
                transportEpoch = 0L; bridgeRecoveryGeneration = 0L
                checkpointGeneration = 0L; checkpointRequestId = '(none)'
                dataSessionIdentity = 'data-session-1'; dataSessionGeneration = 1L
            }
            $routeOperation = [ordered]@{
                family='route_transition'; operationRole='current'
                operationKey='route_transition|current|transition-authority|1|RegularV4ToTunaV4'
                operationKind='RegularV4ToTunaV4'; authorityId='transition-authority'
                predecessorAuthorityId='(none)'; operationGeneration=1L
                beforeState='Draining'; afterState='Committed'
                commandIdentity='transition-authority:Committed'; commandGeneration=1L
                commandFingerprint='disposition-command'; receiptIdentity='transition-authority:Committed'
                receiptGeneration=1L; receiptStatus='Applied'; receiptFingerprint='disposition-receipt'
                receiptValidated=$true; stateApplied=$true; routeRuntime=$null
                routeTransition=[ordered]@{
                    role='current'; transitionKind='RegularV4ToTunaV4'
                    sourceBoundary=$routeScope; targetBoundary=$targetScope; sourceTransport='RegularNkn'
                    proofBoundaryKind='RuntimeUnlock'; proofAdmissionMode='runtime_unlock_precommit'; proofBoundaryFingerprint='proof'
                    durableCheckpointFingerprint='checkpoint'; targetRoute='file_tuna_v4'
                    targetProtocolVersion=4; targetTransport='Tuna'; handoffKind='NormalToTuna'
                    dispositionKind='Committed'; retainedFrameFingerprint='(none)'
                    retainedFrameType='(none)'
                    receipts=$routeReceipts
                }
            }
            $routeCommitOperation = [ordered]@{
                family='route_runtime'; operationRole='route_commit'
                operationKey='route_runtime|route_commit|commit:1|1|RouteCommit'; operationKind='RouteCommit'
                authorityId='commit:1'; predecessorAuthorityId='(none)'; operationGeneration=1L
                beforeState='None'; afterState='Committed'; commandIdentity='commit:1'
                commandGeneration=1L; commandFingerprint='commit-receipt'; receiptIdentity='commit:1'
                receiptGeneration=1L; receiptStatus='Committed'; receiptFingerprint='commit-receipt'
                receiptValidated=$true; stateApplied=$true
                routeRuntime=[ordered]@{ role='route_commit'; boundary=$targetScope; tokenFingerprint='commit-receipt' }
                routeTransition=$null
            }
            $routePublicationOperation = [ordered]@{
                family='route_runtime'; operationRole='route_publication'
                operationKey='route_runtime|route_publication|publication:2|2|RoutePublication'; operationKind='RoutePublication'
                authorityId='publication:2'; predecessorAuthorityId='commit:1'; operationGeneration=2L
                beforeState='None'; afterState='Published'; commandIdentity='publication:2'
                commandGeneration=2L; commandFingerprint='publication-receipt'; receiptIdentity='publication:2'
                receiptGeneration=2L; receiptStatus='Accepted'; receiptFingerprint='publication-receipt'
                receiptValidated=$true; stateApplied=$true
                routeRuntime=[ordered]@{ role='route_publication'; boundary=$targetScope; tokenFingerprint='publication-receipt' }
                routeTransition=$null
            }
            $routeAggregate = [ordered]@{
                beforeStateGeneration=0L; afterStateGeneration=1L
                beforeStateFingerprint='route-before'; afterStateFingerprint='route-after'
                beforeBoundary=$routeScope; afterBoundary=$routeScope
                changedFamilies=@('route_runtime','route_transition')
                operations=@($routeOperation,$routeCommitOperation,$routePublicationOperation)
            }
            $routeEnvelope = [ordered]@{
                snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                coordinatorInstanceId='coordinator-route'; sessionId='session-route'
                transferId='transfer-route'; direction='outbound'; decisionUtcMilliseconds=2L
                transitionId='transition-route'; decisionKind='CommitFileTransferRouteTransition'
                decisionStatus='Committed'; decisionReason='filetransfer_route_transition_committed'
                aggregate=$routeAggregate
            }
            $routeJson = $routeEnvelope | ConvertTo-Json -Depth 32 -Compress
            $routeBytes = [Text.UTF8Encoding]::new($false).GetBytes($routeJson)
            $routeHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $routeBytes
            $routePayload = [Convert]::ToBase64String($routeBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $routePrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-route; session_id=session-route; transfer_id=transfer-route; direction=outbound"
            $routeStart = "event=filetransfer_coordinator_snapshot_stream_started; $routePrefix; initial_state_generation=0; initial_state_hash=route-before; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
            $routeTransition = "event=filetransfer_coordinator_transition_snapshot; $routePrefix; transition_id=transition-route; before_state_generation=0; after_state_generation=1; snapshot_sha256=$routeHash; snapshot_payload=$routePayload; authority_effect=none"
            $routeCompleted = "event=filetransfer_coordinator_snapshot_stream_completed; $routePrefix; final_state_generation=1; final_state_hash=route-after; transition_count=1; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
            function New-RouteEffect([string]$kind,[string]$effectId,[string]$receiptId,[string]$status,[string]$fingerprint) {
                "event=filetransfer_coordinator_authority_effect; $routePrefix; transition_id=transition-route; coordinator_state_generation=1; operation_family=route_transition; operation_key=route_transition|current|transition-authority|1|RegularV4ToTunaV4; operation_authority_id=transition-authority; operation_generation=1; effect_kind=$kind; effect_identity=$effectId; effect_generation=1; receipt_identity=$receiptId; receipt_generation=1; receipt_status=$status; receipt_fingerprint=$fingerprint; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=1; checkpoint_request_id=checkpoint-1; data_session_identity=data-session-1; data_session_generation=1; authority_effect=applied"
            }
            $routeEffects = @(
                (New-RouteEffect 'pause_installed' 'pause-effect' 'transition-authority:pause_bind' 'Installed' 'pause-receipt'),
                (New-RouteEffect 'completed_drain_receipt' 'drain-effect' 'transition-authority:drain' 'Completed' 'drain-receipt'),
                (New-RouteEffect 'route_commit_adopted' 'commit-effect' 'commit:1' 'Committed' 'commit-receipt'),
                (New-RouteEffect 'target_runtime_leg_installed' 'runtime-effect' 'commit:1' 'Committed' 'commit-receipt'),
                (New-RouteEffect 'route_transition_disposition_applied' 'disposition-effect' 'transition-authority:Committed' 'Applied' 'disposition-receipt'),
                (New-RouteEffect 'transition_pause_released' 'pause-release-effect' 'transition-authority:Committed' 'Applied' 'disposition-receipt'),
                (New-RouteEffect 'recovered_epoch_publication' 'recovered-epoch-effect' 'transition-authority:Committed' 'Applied' 'disposition-receipt')
            )
            $routeEffects += "event=filetransfer_coordinator_authority_effect; $routePrefix; transition_id=transition-route; coordinator_state_generation=1; operation_family=route_runtime; operation_key=route_runtime|route_publication|publication:2|2|RoutePublication; operation_authority_id=publication:2; operation_generation=2; effect_kind=route_publication_forwarded; effect_identity=publication:2; effect_generation=2; receipt_identity=publication:2; receipt_generation=2; receipt_status=Accepted; receipt_fingerprint=publication-receipt; route=file_tuna_v4; protocol_version=4; live_route_epoch=2; route_publication_generation=2; route_commit_generation=1; runtime_generation=2; terminal_generation=0; leg_generation=2; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=0; checkpoint_request_id=(none); data_session_identity=data-session-1; data_session_generation=1; authority_effect=applied"
            $closedRoute = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@($routeStart,$routeTransition) + $routeEffects + @($routeCompleted))
            if ($closedRoute.ArtifactEvidenceVerdict -ne 'pass' -or $null -ne $closedRoute.FirstUnresolvedBoundary) {
                throw "An exact committed route-transition boundary did not close: $($closedRoute | ConvertTo-Json -Depth 8 -Compress)"
            }
            $missingDisposition = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@($routeStart,$routeTransition) + @($routeEffects | Where-Object { $_ -notlike '*effect_kind=route_transition_disposition_applied*' }) + @($routeCompleted))
            if ($missingDisposition.ArtifactEvidenceVerdict -ne 'pass' -or
                $missingDisposition.FirstUnresolvedBoundary.taxonomyClass -ne 'filetransfer_route_transition_incomplete' -or
                $missingDisposition.FirstUnresolvedBoundary.unresolvedCount -ne 1) {
                throw 'A missing exact disposition effect was not selected as the route-transition boundary.'
            }
            $missingPublication = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@($routeStart,$routeTransition) + @($routeEffects | Where-Object { $_ -notlike '*effect_kind=route_publication_forwarded*' }) + @($routeCompleted))
            if ($missingPublication.ArtifactEvidenceVerdict -ne 'pass' -or
                $missingPublication.FirstUnresolvedBoundary.taxonomyClass -ne 'publication_authority_missing') {
                throw 'A committed route without its exact publication effect was not selected.'
            }

            $legacy = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=legacy_only')
            if ($legacy.Mode -ne 'LegacyV0' -or $legacy.LegacySelectionCount -ne 1) {
                throw 'Legacy evidence mode was not selected explicitly.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Coordinator snapshot evaluator validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_LegacyV0CatalogIsPinnedAndRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var ledgerScript = File.ReadAllText(
            Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1"));
        Assert.Contains(
            "LegacyV0EvidenceCatalog.json",
            ledgerScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[System.IO.File]::ReadAllText($path",
            ledgerScript,
            StringComparison.Ordinal);

        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')

            $catalog = Get-GuiSmokeLegacyV0EvidenceCatalog
            $events = @(Get-GuiSmokeLegacyV0SemanticEvidenceEventNames)
            $registered = @(
                $catalog.correlators |
                    ForEach-Object { @($_.dependencies) } |
                    ForEach-Object { ([string]$_).ToLowerInvariant() } |
                    Sort-Object -Unique)
            if ($catalog.version -ne 'legacy-v0-pinned-20260825' -or
                $events.Count -eq 0 -or
                ($events -join "`n") -cne ($registered -join "`n")) {
                throw 'Pinned LegacyV0 registry is incomplete.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Pinned LegacyV0 catalog validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public void FileTransferOps_LargeFullAnalysisEvaluatesFullInputIndependently()
    {
        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(
            Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));

        Assert.Contains("$largeFullIndexed = $false", orchestrationText, StringComparison.Ordinal);
        Assert.Contains(
            "if ($compactRequested -or $compactForLargeSlice)",
            orchestrationText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'large_full_indexed_semantics'", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferCompactRetainedAnalysisArtifacts @compactArtifactArgs", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-full-summary.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-coordinator-snapshot-full-summary.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-coordinator-snapshot-compact-summary.json", orchestrationText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Destination (Join-Path $resolvedArtifactDir 'filetransfer-retained-full-summary.json')",
            orchestrationText,
            StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferIndexedRawEvidenceArtifact", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferCompactIndexEventsByNamesOrdered", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("Select-Object -First 250", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("selection=indexed_high_value_semantic_events", orchestrationText, StringComparison.Ordinal);

        var indexedBranch = orchestrationText.IndexOf(
            "if ($compactRequested -or $compactForLargeSlice)",
            StringComparison.Ordinal);
        var legacySummary = orchestrationText.IndexOf(
            "$summary = New-FileTransferRetainedSummary",
            indexedBranch,
            StringComparison.Ordinal);
        Assert.True(indexedBranch >= 0 && legacySummary > indexedBranch, "Expected the small-full legacy summary to remain after the bounded indexed branch.");
    }

    [Fact]
    public void FileTransferService_CoordinatorPersistentStateAssignmentsAreCentralized()
    {
        var repoRoot = FindRepoRoot();
        var sourceDirectory = Path.Combine(repoRoot, "src", "nLink.Core", "FileTransfer");
        var offenders = Directory
            .EnumerateFiles(sourceDirectory, "SessionFileTransferService*.cs")
            .Where(path => !path.EndsWith(
                "SessionFileTransferService.CoordinatorSnapshots.cs",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, lineNumber = index + 1 }))
            .Where(item => item.line.Contains(
                "CoordinatorPersistentState =",
                StringComparison.Ordinal))
            .Select(item => $"{item.path}:{item.lineNumber}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Coordinator state must be adopted through the snapshot finalizer: " +
            string.Join(", ", offenders));

        var serviceText = File.ReadAllText(Path.Combine(
            sourceDirectory,
            "SessionFileTransferService.cs"));
        Assert.DoesNotContain(
            "decision.SnapshotEnvelope is not null",
            serviceText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransferOps_Family1SnapshotCutoverPropagatesOneSharedBoundary()
    {
        var repoRoot = FindRepoRoot();
        var orchestrationText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "FileTransferOps",
            "AnalyzerOrchestration.ps1"));
        var artifactWriterText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "FileTransferSoak",
            "ArtifactWriters.ps1"));
        var guiText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "GuiSmoke-Windows.ps1"));
        var acceptanceText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "Run-FileTransferRouteAcceptance.ps1"));

        foreach (var text in new[] { orchestrationText, artifactWriterText })
        {
            Assert.Contains("coordinator_snapshot_route_family_legacy_selection_count", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_boundary_hash", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_catalog_hash", text, StringComparison.Ordinal);
        }

        Assert.Contains(
            "Get-FileTransferCoordinatorSnapshotEvaluation",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "-CoordinatorBoundaryHash ([string]$boundary.boundaryHash)",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "-CoordinatorWriterSequence ([long]$boundary.writerSequence)",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinatorSnapshotRouteFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTransferOps_Family1SnapshotCutoverGuiConsumesExactSharedBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var guiText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var script = $$"""
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { return $null }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-FileTransferCoordinatorSnapshotEvaluation {
                return [pscustomobject]@{
                    Mode = 'SnapshotV1Shadow'
                    ArtifactEvidenceVerdict = 'pass'
                    TransitionCount = 3L
                    FirstUnresolvedBoundary = [pscustomobject]@{
                        boundaryId = 'route-boundary-7'
                        boundaryHash = 'route-boundary-hash-7'
                        families = @('route_transition')
                        taxonomyClass = 'filetransfer_route_transition_incomplete'
                        phase = 'route_transition'
                        decisionReason = 'missing_exact_disposition'
                        variant = 'retained_frame'
                        rawCount = 2
                        distinctCount = 1
                        recoveredCount = 0
                        unresolvedCount = 1
                        writer = 'writer-helper'
                        writerSequence = 9961L
                    }
                }
            }
            function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative {
                param($Evaluation, [string]$Family)
                return $Family -in @('route_runtime', 'route_transition')
            }

            $root = Join-Path ([IO.Path]::GetTempPath()) (
                'nlink-family1-gui-boundary-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($root)
            try {
                $path = Join-Path $root 'evidence.log'
                [IO.File]::WriteAllText($path, 'event=filetransfer_coordinator_snapshot_stream_started')
                $actual = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($actual.ClassificationSource -ne 'coordinator_snapshot_authority_v1' -or
                    $actual.FailureClass -ne 'filetransfer_route_transition_incomplete' -or
                    $actual.Phase -ne 'route_transition' -or
                    $actual.Reason -ne 'missing_exact_disposition' -or
                    $actual.FailureVariant -ne 'retained_frame' -or
                    $actual.CoordinatorBoundaryId -ne 'route-boundary-7' -or
                    $actual.CoordinatorBoundaryHash -ne 'route-boundary-hash-7' -or
                    $actual.CoordinatorRawCount -ne 2 -or
                    $actual.CoordinatorDistinctCount -ne 1 -or
                    $actual.CoordinatorRecoveredCount -ne 0 -or
                    $actual.CoordinatorUnresolvedCount -ne 1 -or
                    $actual.CoordinatorWriter -ne 'writer-helper' -or
                    $actual.CoordinatorWriterSequence -ne 9961L) {
                    throw "GUI did not preserve the shared boundary: $($actual | ConvertTo-Json -Compress)"
                }
            }
            finally {
                if (Test-Path -LiteralPath $root) {
                    Remove-Item -LiteralPath $root -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"GUI shared-boundary validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public void RunFileTransferNknSoak_PublicParameterSetAndArtifactNamesRemainStable()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Equal(
            new[]
            {
                "Mode",
                "ExePath",
                "PayloadSizes",
                "Cycles",
                "Seed",
                "Direction",
                "ArtifactDir",
                "CycleTimeoutSeconds",
                "ProgressTimeoutSeconds",
                "TimeoutSeconds",
                "ExternalTopologyProfile",
                "PayloadEfficiencyProfile",
                "Build",
                "SafeBaselineArtifactDir",
                "StrongBaselineArtifactDir",
                "IncludeRawSlices",
                "FailOnGate"
            },
            ExtractTopLevelPowerShellParameterNames(scriptText));
        Assert.Equal(new[] { "nkn-fast", "nkn-mixed" }, ExtractPowerShellValidateSetValues(scriptText, "Mode"));
        Assert.Equal(ExpectedExternalTopologyProfiles, ExtractPowerShellValidateSetValues(scriptText, "ExternalTopologyProfile"));
        Assert.Equal(ExpectedPayloadEfficiencyProfiles, ExtractPowerShellValidateSetValues(scriptText, "PayloadEfficiencyProfile"));
        Assert.Contains("FILETRANSFER_NKN_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("FILETRANSFER_NKN_MIXED_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE", scriptText, StringComparison.Ordinal);
        Assert.Contains("Invoke-FileTransferGuiSmokeWithTimeout", scriptText, StringComparison.Ordinal);
        Assert.Contains("Stop-FileTransferProcessTree", scriptText, StringComparison.Ordinal);
        Assert.Contains("gui-smoke-stdout.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("gui-smoke-stderr.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular-nkn-tuna-state", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_STATE_ROOT", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular-nkn-app\\sidecar-isolated\\a\\b\\c\\d\\e\\f\\app", scriptText, StringComparison.Ordinal);
        Assert.Contains("Copy-FileTransferRegularNknOnlyPortable", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-FileTransferRegularNknStagingExcludedItem", scriptText, StringComparison.Ordinal);
        Assert.Contains("nlink-tuna-sidecar.exe", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_NKN_TUNA_SIDECAR_EXE", scriptText, StringComparison.Ordinal);
        Assert.Contains("$analysis.GateResult.Verdict -eq 'INCONCLUSIVE'", scriptText, StringComparison.Ordinal);
        Assert.Contains("$analysis.GateResult.Verdict -eq 'INVALID_SETUP'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Set-ProcessEnvironmentValue -Name 'NLINK_TRANSPORT' -Value 'NKN'", scriptText, StringComparison.Ordinal);
        foreach (var artifactName in RequiredLiveNknArtifactFiles)
        {
            Assert.Contains(artifactName, scriptText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunFileTransferRouteAcceptance_PublicParametersAndArtifactsRemainStable()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Equal(
            new[]
            {
                "ExePath",
                "WalletPath",
                "WalletPassword",
                "SidecarPath",
                "Runtime",
                "ArtifactRoot",
                "MatrixMode",
                "BaselineManifestPath",
                "GoodputRegressionTolerancePercent",
                "GoodputOnlyRerunLimit",
                "SetupOnlyRerunLimit",
                "TimeoutSeconds",
                "ProgressTimeoutSeconds",
                "FallbackMaxAttempts",
                "AllowExternalTransportWarnings",
                "ScenarioName",
                "EvidenceDryRun",
                "EvidenceDryRunFixtureRoot"
            },
            ExtractTopLevelPowerShellParameterNames(scriptText));

        Assert.Equal(new[] { "legacy", "phase4-ab-acceptance", "phase5-analyzer-gui-acceptance" }, ExtractPowerShellValidateSetValues(scriptText, "MatrixMode"));
        Assert.Contains("artifacts\\filetransfer-route-acceptance", scriptText, StringComparison.Ordinal);
        Assert.Contains("baseline-lock-v0.7.0-20260524\\baseline-manifest.json", scriptText, StringComparison.Ordinal);
        Assert.Contains("GoodputRegressionTolerancePercent = 10D", scriptText, StringComparison.Ordinal);
        Assert.Contains("GoodputOnlyRerunLimit = 1", scriptText, StringComparison.Ordinal);
        Assert.Contains("SetupOnlyRerunLimit = 1", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase4-ab-acceptance-summary.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase5-analyzer-gui-acceptance-summary.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase5-analyzer-gui-acceptance", scriptText, StringComparison.Ordinal);
        Assert.Contains("second-transfer-after-reactivation", scriptText, StringComparison.Ordinal);
        Assert.Contains("Ensure-RouteAcceptancePackagedBridgeRuntime", scriptText, StringComparison.Ordinal);
        Assert.Contains("Assert-RouteAcceptanceBridgeBundleRuntime", scriptText, StringComparison.Ordinal);
        Assert.Contains("bridge-runtime-{0}.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("Route acceptance app is missing the packaged NKN bridge runtime", scriptText, StringComparison.Ordinal);
        var secondTransferScenarioLines = Regex
            .Matches(scriptText, @"New-Phase4RouteAcceptanceScenario -Name 'second-transfer-after-reactivation'[^\r\n]+")
            .Select(match => match.Value)
            .ToArray();
        Assert.NotEmpty(secondTransferScenarioLines);
        Assert.All(secondTransferScenarioLines, line => Assert.Contains("-PayloadBytes 134217728L", line, StringComparison.Ordinal));
        Assert.DoesNotContain(secondTransferScenarioLines, line => line.Contains("-PayloadBytes 67108864L", StringComparison.Ordinal));
        var canonicalRepeatedToggleScenarioLines = Regex
            .Matches(scriptText, @"New-Phase4RouteAcceptanceScenario -Name 'regular-v4-live-activation-off-on-off-256mb'[^\r\n]+")
            .Select(match => match.Value)
            .ToArray();
        Assert.NotEmpty(canonicalRepeatedToggleScenarioLines);
        Assert.All(canonicalRepeatedToggleScenarioLines, line => Assert.Contains("-PayloadBytes 268435456L", line, StringComparison.Ordinal));
        Assert.DoesNotContain(canonicalRepeatedToggleScenarioLines, line => line.Contains("-PayloadBytes 134217728L", StringComparison.Ordinal));
        Assert.Contains("regular-v4-live-activation-off-on-off-256mb", scriptText, StringComparison.Ordinal);
        Assert.Contains("canonical repeated-toggle bridge liveness integration proof must pass", scriptText, StringComparison.Ordinal);
        Assert.Contains("bridge_liveness_integration_verdict", scriptText, StringComparison.Ordinal);
        Assert.Contains("Assert-RouteAcceptanceRegularNknTransportEvidence", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular NKN artifact used DevLocal transport", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular NKN artifact lacks positive NKN transport evidence", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_leg_authority_proof_verdict", scriptText, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch_proof_verdict", scriptText, StringComparison.Ordinal);
        Assert.Contains("recovered_runtime_unlock_bridge_clear", scriptText, StringComparison.Ordinal);
        Assert.Contains("recovered_regular_v4_bridge_clear", scriptText, StringComparison.Ordinal);
        Assert.Contains("route_sequence", scriptText, StringComparison.Ordinal);
        Assert.Contains("live_epoch_route_changes", scriptText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v6 route is not allowed", scriptText, StringComparison.Ordinal);
        Assert.Contains("network_variance_policy=live_transport_paired_rerun", scriptText, StringComparison.Ordinal);
        Assert.Contains("capped_external_transport_churn_requires_clean_rerun", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_nkn_external_transport_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("route-acceptance-summary.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("route-acceptance-summary.json", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular-nkn-64mb-quick", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular-nkn-128mb-target", scriptText, StringComparison.Ordinal);
        Assert.Contains("tuna-128mb-no-fault", scriptText, StringComparison.Ordinal);
        Assert.Contains("tuna-128mb-fallback", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-FileTransferNknSoak.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-FileTransferTunaGuiSmoke.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("FileTransfer-Ops.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("-Mode', 'nkn-fast'", scriptText, StringComparison.Ordinal);
        Assert.Contains("-PayloadSizes', $PayloadSize", scriptText, StringComparison.Ordinal);
        Assert.Contains("-PayloadSize '64MiB'", scriptText, StringComparison.Ordinal);
        Assert.Contains("-PayloadSize '128MiB'", scriptText, StringComparison.Ordinal);
        Assert.Contains("-RouteMode', $RouteMode", scriptText, StringComparison.Ordinal);
        Assert.Contains("-RouteMode 'preactivated'", scriptText, StringComparison.Ordinal);
        Assert.Contains("-RouteMode 'v4-restart-v6-fallback'", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-log-slice-full.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-setup-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-measured-fallback-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("setupCanceledTerminalIndex", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-RouteAcceptanceFallbackSetupTunaV4Evidence", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_nkn_v4_fast", scriptText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v4", scriptText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", scriptText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_regular_nkn_v6 route is not allowed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Tuna no-fault acceptance unexpectedly entered fallback", scriptText, StringComparison.Ordinal);
        Assert.Contains("1500000D", scriptText, StringComparison.Ordinal);
        Assert.Contains("4000000D", scriptText, StringComparison.Ordinal);
        Assert.Contains("FallbackMaxAttempts = 2", scriptText, StringComparison.Ordinal);
        Assert.Contains("AllowExternalTransportWarnings = $true", scriptText, StringComparison.Ordinal);
        Assert.Contains("route-acceptance-attempts.json", scriptText, StringComparison.Ordinal);
        Assert.Contains("attempt-{0}", scriptText, StringComparison.Ordinal);
        Assert.Contains("operatorAcceptedWithWarnings", scriptText, StringComparison.Ordinal);
        Assert.Contains("warningKinds", scriptText, StringComparison.Ordinal);
        Assert.Contains("measurementContaminated", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-Phase4RerunnableMeasurementFailure", scriptText, StringComparison.Ordinal);
        Assert.Contains("active_tuna_v4_repair_pressure", scriptText, StringComparison.Ordinal);
        Assert.Contains("active_tuna_v4_bridge_receive_recovery_window", scriptText, StringComparison.Ordinal);
        foreach (var directory in RequiredRouteAcceptanceSubdirectories)
        {
            Assert.Contains(directory, scriptText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PreReleaseCheck_RouteAcceptanceIsOptInAfterPortableBeforeInstaller()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "PreRelease-Check.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        var portableIndex = scriptText.IndexOf("Build portable ZIP", StringComparison.Ordinal);
        var routeAcceptanceIndex = scriptText.IndexOf("File transfer route acceptance gate", StringComparison.Ordinal);
        var installerIndex = scriptText.IndexOf("Build installer", StringComparison.Ordinal);

        Assert.True(portableIndex >= 0, "Expected portable build step.");
        Assert.True(routeAcceptanceIndex > portableIndex, "Expected route acceptance after portable build.");
        Assert.True(installerIndex > routeAcceptanceIndex, "Expected installer build after route acceptance.");
        Assert.Contains("RunFileTransferRouteAcceptanceGate", scriptText, StringComparison.Ordinal);
        Assert.Contains("File transfer route acceptance gate: SKIPPED", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-FileTransferRouteAcceptance.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_TEST_WALLET_PASSWORD", scriptText, StringComparison.Ordinal);
        Assert.Contains("FileTransferRouteAcceptanceWalletPassword", scriptText, StringComparison.Ordinal);
        Assert.Contains("FileTransferRouteAcceptanceFallbackMaxAttempts", scriptText, StringComparison.Ordinal);
        Assert.Contains("FileTransferRouteAcceptanceAllowExternalTransportWarnings", scriptText, StringComparison.Ordinal);
        Assert.Contains("-FallbackMaxAttempts $FileTransferRouteAcceptanceFallbackMaxAttempts", scriptText, StringComparison.Ordinal);
        Assert.Contains("-AllowExternalTransportWarnings $FileTransferRouteAcceptanceAllowExternalTransportWarnings", scriptText, StringComparison.Ordinal);
        Assert.Contains("artifacts\\portable\\nLink\\win-x64\\nLink.exe", scriptText, StringComparison.Ordinal);
        Assert.Contains("artifacts\\portable\\nLink\\win-x64\\tuna\\{0}\\nlink-tuna-sidecar.exe", scriptText, StringComparison.Ordinal);
        Assert.Contains("Build-Installer.ps1", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPortable_BridgeBundleSuccessClearsRecoveredNativeExitCode()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "installer", "Build-Portable.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        var bridgeStepIndex = scriptText.IndexOf("Building bundled NKN bridge runtime", StringComparison.Ordinal);
        var tunaStepIndex = scriptText.IndexOf("if (-not $SkipTunaSidecarBundle)", StringComparison.Ordinal);
        Assert.True(bridgeStepIndex >= 0, "Expected portable build to invoke the bridge bundle step.");
        Assert.True(tunaStepIndex > bridgeStepIndex, "Expected Tuna sidecar step after bridge bundle step.");

        var bridgeBlock = scriptText[bridgeStepIndex..tunaStepIndex];
        var scriptSucceededIndex = bridgeBlock.IndexOf("$bridgeBundleScriptSucceeded = $?", StringComparison.Ordinal);
        var assertIndex = bridgeBlock.IndexOf("Assert-BridgeBundleRuntime -BridgeDir $bridgeBundleAbs", StringComparison.Ordinal);
        var copyIndex = bridgeBlock.IndexOf("Copy-BridgeBundleToPortable", StringComparison.Ordinal);
        var clearIndex = bridgeBlock.IndexOf("$global:LASTEXITCODE = 0", StringComparison.Ordinal);

        Assert.True(scriptSucceededIndex >= 0, "Expected bridge bundle script success to be checked through PowerShell success state.");
        Assert.True(assertIndex > scriptSucceededIndex, "Expected bridge output validation after script invocation.");
        Assert.True(copyIndex > assertIndex, "Expected validated bridge output to be copied into the portable app.");
        Assert.True(clearIndex > copyIndex, "Expected recovered stale native exit code to be cleared after validation.");
        Assert.DoesNotContain("if ($LASTEXITCODE -ne 0)", bridgeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("exit $LASTEXITCODE", bridgeBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransferDocs_ExistForOperatorWorkflow()
    {
        var repoRoot = FindRepoRoot();

        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "file-transfer-soak.md")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "file-transfer-operability.md")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "file-transfer-stabilization-protocol.md")));

        var soakDoc = File.ReadAllText(Path.Combine(repoRoot, "docs", "file-transfer-soak.md"));
        Assert.Contains("filetransfer-operator-verdict.txt", soakDoc, StringComparison.Ordinal);
        Assert.Contains("FileTransfer-Ops.ps1", soakDoc, StringComparison.Ordinal);
        Assert.Contains("LocalFast", soakDoc, StringComparison.Ordinal);
        Assert.Contains("LocalImpaired", soakDoc, StringComparison.Ordinal);
        Assert.Contains("LocalMixed", soakDoc, StringComparison.Ordinal);
        Assert.Contains("NknFast", soakDoc, StringComparison.Ordinal);
        Assert.Contains("NknMixed", soakDoc, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SupportCapture_OutputMentionsRequiredEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var result = await RunFileTransferOpsAsync(repoRoot, ["-Mode", "SupportCapture"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Diagnostics", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retained logs", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filetransfer-operator-verdict.txt", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact directory", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filetransfer-live-nkn-summary.txt", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOps_RejectsModeSpecificParametersOutsideTheirMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();

        var liveParamOnLocal = await RunFileTransferOpsAsync(repoRoot, ["-Mode", "LocalFast", "-ExePath", "nLink.exe"]);
        Assert.NotEqual(0, liveParamOnLocal.ExitCode);
        Assert.Contains("Parameter -ExePath is only supported", liveParamOnLocal.Stderr + liveParamOnLocal.Stdout, StringComparison.Ordinal);

        var localParamOnLive = await RunFileTransferOpsAsync(repoRoot, ["-Mode", "NknFast", "-ImpairmentProfile", "ReorderBurst"]);
        Assert.NotEqual(0, localParamOnLive.ExitCode);
        Assert.Contains("Parameter -ImpairmentProfile is only supported", localParamOnLive.Stderr + localParamOnLive.Stdout, StringComparison.Ordinal);

        var payloadProfileOnAnalyze = await RunFileTransferOpsAsync(repoRoot, ["-Mode", "AnalyzeRetained", "-PayloadEfficiencyProfile", "Packed3x21KiB"]);
        Assert.NotEqual(0, payloadProfileOnAnalyze.ExitCode);
        Assert.Contains("Parameter -PayloadEfficiencyProfile is only supported", payloadProfileOnAnalyze.Stderr + payloadProfileOnAnalyze.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOps_NknMixedPayloadEfficiencyCandidatesRequireUnsafeOverride()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-mixed-payload-guard", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "unsafe-allowed");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var blocked = await RunFileTransferOpsAsync(
                repoRoot,
                [
                    "-Mode", "NknMixed",
                    "-PayloadEfficiencyProfile", "Packed3x21KiB",
                    "-TimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.NotEqual(0, blocked.ExitCode);
            Assert.Contains("not supported for NknMixed by default", blocked.Stderr + blocked.Stdout, StringComparison.Ordinal);

            var environment = BuildFakeLiveNknEnvironment();
            environment["NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE"] = "1";
            var allowed = await RunFileTransferOpsAsync(
                repoRoot,
                [
                    "-Mode", "NknMixed",
                    "-PayloadEfficiencyProfile", "Packed3x21KiB",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                allowed.ExitCode == 0,
                $"Expected explicit unsafe override to allow fake NknMixed candidate run.{Environment.NewLine}STDOUT:{Environment.NewLine}{allowed.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{allowed.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }
}
