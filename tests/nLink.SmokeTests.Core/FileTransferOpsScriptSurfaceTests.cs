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
    public void FileTransferOps_RecoveryHandoffSnapshotEffectsUseExactSubjects()
    {
        var repoRoot = FindRepoRoot();
        var serviceText = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "nLink.Core",
            "FileTransfer",
            "SessionFileTransferService.CoordinatorSnapshots.cs"));
        var evaluatorText = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "FileTransferOps",
            "CoordinatorSnapshotEvidence.ps1"));

        const string helperName =
            "CreateRegularV4PressureRecoveryHandoffAuthorityEffectBindings(";
        var helperCall = serviceText.IndexOf(
            helperName,
            StringComparison.Ordinal);
        var helperStart = serviceText.IndexOf(
            helperName,
            helperCall + helperName.Length,
            StringComparison.Ordinal);
        var routeCommitStart = serviceText.IndexOf(
            "CreateRegularV4PressureRouteCommitAuthorityEffectBindings(",
            helperStart,
            StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && routeCommitStart > helperStart);
        var helper = serviceText[helperStart..routeCommitStart];
        Assert.Contains("pressure_settlement_terminal", helper, StringComparison.Ordinal);
        Assert.Contains("recovery_handoff_terminal", helper, StringComparison.Ordinal);
        Assert.Contains("operation_current", helper, StringComparison.Ordinal);
        Assert.Contains("ReceiptMatchesOperation", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("matches[0]", helper, StringComparison.Ordinal);
        Assert.Contains(
            "expectedOperationKey = exactBinding.OperationKey",
            serviceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid",
            evaluatorText,
            StringComparison.Ordinal);
        Assert.Contains(
            "recovery_handoff_commit_effects_bound_to_retired_pressure_install_receipt",
            evaluatorText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-FileTransferCoordinatorSnapshotExactEffectBinding",
            evaluatorText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTransferOps_FallbackTailPendingCommandsDoNotRequireCompletedReceipts()
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
            $scope = [pscustomobject]@{
                route='post_tuna_fallback_v6'; protocolVersion=6
                liveRouteEpoch=2L; routePublicationGeneration=3L
                routeCommitGeneration=0L; runtimeGeneration=3L
                terminalGeneration=0L; legGeneration=3L
                transportEpoch=1L; bridgeRecoveryGeneration=1L
                checkpointGeneration=1L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session'; dataSessionGeneration=1L
            }
            function New-Pending([string]$role, [string]$before, [string]$after) {
                $kind = if ($role -eq 'retry_request_pending') { 'RetryRequest' } else { 'RetryDispatchRecheck' }
                $receipt = [pscustomobject]@{
                    stage=$kind; commandIdentity="$kind-command"; commandGeneration=1L
                    commandFingerprint="$kind-fingerprint"; receiptIdentity='(none)'
                    receiptGeneration=0L; receiptFingerprint='(none)'
                    receiptStatus='Pending'; receiptReason='Pending'; receiptValidated=$false
                }
                [pscustomobject]@{
                    family='fallback_tail_yield'; operationRole=$role; operationKind=$kind
                    operationKey="fallback_tail_yield|$role|yield-1|1|$kind"
                    authorityId='yield-1'; predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState=$before; afterState=$after; stateApplied=$true
                    commandIdentity=$receipt.commandIdentity; commandGeneration=1L
                    commandFingerprint=$receipt.commandFingerprint; receiptIdentity='(none)'
                    receiptGeneration=0L; receiptFingerprint='(none)'
                    receiptStatus='Pending'; receiptValidated=$false
                    fallbackTailYield=[pscustomobject]@{
                        role=$role; yieldKind='Quiescent'; pairingState='Paired'
                        ownerKey='owner'; pairKey='pair'; localProofIdentity='local-proof'
                        localProofFingerprint='local-fingerprint'; peerProofAuthorityId='peer'
                        peerProofIdentity='peer-proof'; peerProofFingerprint='peer-fingerprint'
                        pairedReleaseAuthorityId='yield-1'; failedPredecessorAuthorityId='(none)'
                        invalidationReason='(none)'; sourceBoundary=$scope
                        effectiveBoundary=$scope; targetBoundary=$scope; receipts=@($receipt)
                    }
                }
            }
            function New-Envelope([object[]]$operations, [long]$generation, [string]$kind='ApplyFallbackTailLocalProofReceipt', [string]$status='Accepted') {
                [pscustomobject]@{
                    snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Authoritative'
                    coordinatorInstanceId='pending-coordinator'; sessionId='session'; transferId='transfer'
                    direction='outbound'; decisionUtcMilliseconds=$generation
                    transitionId="pending-$generation"; decisionKind=$kind; decisionStatus=$status
                    decisionReason='pending-command-test'
                    aggregate=[pscustomobject]@{
                        beforeStateGeneration=($generation-1); afterStateGeneration=$generation
                        beforeStateFingerprint="state-$($generation-1)"; afterStateFingerprint="state-$generation"
                        beforeBoundary=$scope; afterBoundary=$scope
                        changedFamilies=@('fallback_tail_yield'); operations=$operations
                    }
                }
            }
            function Assert-Catalog([object]$operation, [bool]$expected) {
                $envelope = New-Envelope @($operation) 1
                $actual = Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $envelope $operation
                if ($actual -ne $expected) { throw "Unexpected command validation: $($operation | ConvertTo-Json -Depth 16 -Compress)" }
            }
            foreach ($role in @('retry_request_pending','retry_recheck_pending')) {
                foreach ($states in @(@('None','Pending'),@('Pending','Pending'),@('Pending','None'))) {
                    Assert-Catalog (New-Pending $role $states[0] $states[1]) $true
                }
                $mutations = @(
                    { param($o) $o.operationKind='Foreign' },
                    { param($o) $o.operationRole=$o.operationRole.ToUpperInvariant(); $o.fallbackTailYield.role=$o.operationRole; $o.operationKey="fallback_tail_yield|$($o.operationRole)|yield-1|1|$($o.operationKind)" },
                    { param($o) $o.operationKey='wrong-key' },
                    { param($o) $o.authorityId='other-authority' },
                    { param($o) $o.operationGeneration=2L },
                    { param($o) $o.commandGeneration=0L },
                    { param($o) $o.commandIdentity='(none)' },
                    { param($o) $o.commandFingerprint='wrong-fingerprint' },
                    { param($o) $o.receiptIdentity='forged' },
                    { param($o) $o.receiptGeneration=1L },
                    { param($o) $o.receiptFingerprint='forged' },
                    { param($o) $o.receiptStatus='Delivered' },
                    { param($o) $o.receiptValidated=$true },
                    { param($o) $o.beforeState='Applied' },
                    { param($o) $o.fallbackTailYield.role='yield_current' },
                    { param($o) $o.fallbackTailYield.receipts=@() },
                    { param($o) $o.fallbackTailYield.receipts=@($o.fallbackTailYield.receipts[0],$o.fallbackTailYield.receipts[0]) },
                    { param($o) $o.fallbackTailYield.receipts[0].stage='Foreign' },
                    { param($o) $o.fallbackTailYield.receipts[0].commandGeneration=2L },
                    { param($o) $o.fallbackTailYield.receipts[0].receiptReason='Delivered' },
                    { param($o) $o.fallbackTailYield.receipts[0].receiptIdentity='forged' },
                    { param($o) $o.fallbackTailYield.receipts[0].receiptGeneration=1L },
                    { param($o) $o.fallbackTailYield.receipts[0].receiptValidated=$true },
                    { param($o) $o.fallbackTailYield.sourceBoundary.dataSessionIdentity='' }
                )
                foreach ($mutation in $mutations) {
                    $invalid = New-Pending $role 'None' 'Pending' | ConvertTo-Json -Depth 16 | ConvertFrom-Json
                    & $mutation $invalid
                    Assert-Catalog $invalid $false
                }
            }
            $carry = New-Pending 'retry_request_pending' 'Pending' 'Pending' | ConvertTo-Json -Depth 16 | ConvertFrom-Json
            $carry.fallbackTailYield.effectiveBoundary.transportEpoch=2L
            $carry.fallbackTailYield.targetBoundary.transportEpoch=2L
            Assert-Catalog $carry $true
            $terminal = New-Pending 'retry_recheck_pending' 'Pending' 'None'
            $terminal.operationRole='retry_recheck_terminal'
            $terminal.fallbackTailYield.role='retry_recheck_terminal'
            $terminal.operationKey='fallback_tail_yield|retry_recheck_terminal|yield-1|1|RetryDispatchRecheck'
            Assert-Catalog $terminal $false

            $coverage = $catalog.RequiredFamilies -join ','
            $prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=pending-coordinator; session_id=session; transfer_id=transfer; direction=outbound; log_writer_id=pending-writer"
            function Convert-Envelope([object]$envelope) {
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 24 -Compress))
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $prefix; log_writer_sequence=$($envelope.aggregate.afterStateGeneration); transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            $start="event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=state-0; emitted_family_coverage=$coverage; shadow_family_coverage=; authoritative_family_coverage=$coverage; authority_effect=none"
            $end="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=state-3; transition_count=3; emitted_family_coverage=$coverage; shadow_family_coverage=; authoritative_family_coverage=$coverage; authority_effect=none"
            $issued=Convert-Envelope (New-Envelope @((New-Pending 'retry_request_pending' 'None' 'Pending')) 1)
            $held=Convert-Envelope (New-Envelope @($carry,(New-Pending 'retry_recheck_pending' 'None' 'Pending')) 2 'IssueFallbackTailYieldRetryDispatchRecheck')
            $retired=Convert-Envelope (New-Envelope @((New-Pending 'retry_request_pending' 'Pending' 'None'),(New-Pending 'retry_recheck_pending' 'Pending' 'None')) 3 'AuthorizeRuntimeUnlockRetryDispatch' 'Granted')
            $fullLines=@($start,'event=unrelated',$issued,$issued,$held,$retired,$end)
            $compactLines=@($fullLines | Where-Object { $_ -notlike 'event=unrelated*' })
            $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
            $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
            $parity=Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
            if ($full.ArtifactEvidenceVerdict -ne 'pass' -or $compact.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $full.FirstUnresolvedBoundary -or $full.EffectCount -ne 0 -or
                $parity.Verdict -ne 'pass' -or $full.BoundaryHash -cne $compact.BoundaryHash) {
                throw "Pending command full/compact parity failed: $($full | ConvertTo-Json -Depth 8 -Compress)"
            }
            foreach ($status in @('Idempotent','Rejected')) {
                $operation=New-Pending 'retry_request_pending' 'Pending' 'Pending'
                $operation.stateApplied=$false
                $neutral=New-Envelope @($operation) 1 'ApplyFallbackTailLocalProofReceipt' $status
                $neutral.transitionId="neutral-$status"
                $neutral.aggregate.beforeStateGeneration=1L
                $neutral.aggregate.beforeStateFingerprint='state-1'
                $neutral.aggregate.changedFamilies=@()
                $neutralEvaluation=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                    $start,$issued,(Convert-Envelope $neutral),$held,$retired,$end.Replace('transition_count=3','transition_count=4'))
                if ($neutralEvaluation.ArtifactEvidenceVerdict -ne 'pass' -or $neutralEvaluation.EffectCount -ne 0) {
                    throw "Unchanged $status pending command was rejected."
                }
            }
            $invalid=New-Pending 'retry_request_pending' 'None' 'Pending'
            $invalid.receiptValidated=$true
            $invalidLine=Convert-Envelope (New-Envelope @($invalid) 1)
            $invalidEvaluation=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start,$invalidLine,$held,$retired,$end)
            if ($invalidEvaluation.ArtifactEvidenceVerdict -ne 'fail' -or
                $invalidEvaluation.FirstUnresolvedBoundary.variant -cne 'family4_pending_retry_command_validated_as_completed_receipt') {
                throw 'Malformed command did not retain the precise fail-closed variant.'
            }
            $conflict=New-Pending 'retry_request_pending' 'None' 'Pending'
            $conflict.commandFingerprint='different'
            $conflictLine=Convert-Envelope (New-Envelope @($conflict) 1)
            $conflicting=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($start,$issued,$conflictLine,$held,$retired,$end)
            if ($conflicting.ArtifactEvidenceVerdict -ne 'fail') { throw 'Conflicting duplicate passed.' }
            $guiAst=[Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
            foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult',
                    'Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
                $definitions=@($guiAst.FindAll({ param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
                },$true))
                if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
                . ([scriptblock]::Create($definitions[0].Extent.Text))
            }
            $guiInput=Join-Path ([IO.Path]::GetTempPath()) ("pending-command-$([guid]::NewGuid().ToString('N')).log")
            try {
                [IO.File]::WriteAllLines($guiInput,[string[]]@($start,$invalidLine,$held,$retired,$end))
                $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'test evidence' `
                    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiInput)
                $boundary=$invalidEvaluation.FirstUnresolvedBoundary
                if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
                    $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
                    $gui.CoordinatorWriter -cne $boundary.writer -or
                    $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
                    $gui.FailureVariant -cne $boundary.variant -or
                    $gui.CoordinatorRawCount -ne $boundary.rawCount -or
                    $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
                    throw 'GUI did not preserve the shared pending-command boundary.'
                }
            }
            finally { if (Test-Path -LiteralPath $guiInput) { Remove-Item -LiteralPath $guiInput -Force } }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(result.ExitCode == 0,
            $"Pending Family-4 command regression failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_FallbackTailQuiescentCatalogAcceptsLocalAndPeerFirstShapes()
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

            $scope = [pscustomobject]@{
                route='post_tuna_fallback_v6'; protocolVersion=6
                liveRouteEpoch=2L; routePublicationGeneration=3L
                routeCommitGeneration=0L; runtimeGeneration=3L
                terminalGeneration=0L; legGeneration=3L
                transportEpoch=1L; bridgeRecoveryGeneration=1L
                checkpointGeneration=1L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session'; dataSessionGeneration=1L
            }
            function New-Receipt([string]$stage,[string]$status,[string]$id) {
                [pscustomobject]@{
                    stage=$stage; commandIdentity="$id-command"
                    commandGeneration=1L; commandFingerprint="$id-command-fingerprint"
                    receiptIdentity="$id-receipt"; receiptGeneration=1L
                    receiptStatus=$status; receiptReason='accepted'
                    receiptFingerprint="$id-receipt-fingerprint"
                    receiptValidated=$true
                }
            }
            function New-Payload(
                [string]$role,[string]$kind,[string]$state,
                [object[]]$receipts) {
                [pscustomobject]@{
                    role=$role; yieldKind=$kind; pairingState=$state
                    ownerKey='owner'; pairKey='pair'
                    localProofIdentity='(none)'; localProofFingerprint='(none)'
                    peerProofAuthorityId='peer-authority'
                    peerProofIdentity='peer-receipt'
                    peerProofFingerprint='peer-frame'
                    peerProofYieldGeneration=1L
                    pairedReleaseAuthorityId='(none)'; pairedReleaseGeneration=0L
                    pairedReleaseOutcome='(none)'; pairingGeneration=0L
                    failedPredecessorAuthorityId='(none)'
                    failedSupersessionCarriedPeerProof=$false
                    reauthorizationGeneration=0L
                    reauthorizationSourceTransportEpoch=1L
                    reauthorizationTargetTransportEpoch=1L
                    reservationGeneration=0L; reservedRetryAuthorityId='(none)'
                    consumingInstallGeneration=0L; retryHoldGeneration=0L
                    retryRecheckGeneration=0L; invalidationReason='(none)'
                    sourceBoundary=$scope; effectiveBoundary=$scope
                    targetBoundary=$scope; receipts=$receipts
                }
            }
            function New-Operation(
                [string]$role,[string]$kind,[string]$authority,
                [string]$before,[string]$after,[object]$payload,
                [object]$primaryReceipt) {
                [pscustomobject]@{
                    family='fallback_tail_yield'; operationRole=$role
                    operationKey="fallback_tail_yield|$role|$authority|1|$kind"
                    operationKind=$kind; authorityId=$authority
                    predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState=$before; afterState=$after
                    commandIdentity=$primaryReceipt.commandIdentity
                    commandGeneration=$primaryReceipt.commandGeneration
                    commandFingerprint=$primaryReceipt.commandFingerprint
                    receiptIdentity=$primaryReceipt.receiptIdentity
                    receiptGeneration=$primaryReceipt.receiptGeneration
                    receiptStatus=$primaryReceipt.receiptStatus
                    receiptFingerprint=$primaryReceipt.receiptFingerprint
                    receiptValidated=$true; stateApplied=$true
                    fallbackTailYield=$payload
                }
            }
            function New-Envelope([object[]]$operations,[long]$before,[long]$after) {
                [pscustomobject]@{
                    decisionKind='FallbackTailQuiescent'; decisionStatus='Accepted'
                    decisionReason='chunk_batch_committed'
                    coordinatorInstanceId='coordinator'; transitionId='current'
                    aggregate=[pscustomobject]@{
                        beforeStateGeneration=$before; afterStateGeneration=$after
                        beforeStateFingerprint='before'; afterStateFingerprint='after'
                        beforeBoundary=$scope; afterBoundary=$scope
                        changedFamilies=@('fallback_tail_yield')
                        operations=$operations
                    }
                }
            }

            $yieldReceipt=New-Receipt 'yield' 'Accepted' 'yield'
            $localOperation=New-Operation 'yield_current' 'Quiescent' `
                'local-authority' 'None' 'YieldIssued' `
                (New-Payload 'yield_current' 'Quiescent' 'YieldIssued' @($yieldReceipt)) `
                $yieldReceipt
            $localEnvelope=New-Envelope @($localOperation) 4L 5L
            if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $localEnvelope) -or
                -not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $localEnvelope $localOperation)) {
                throw 'Exact local FallbackTailQuiescent issuance was rejected.'
            }

            $peerReceipt=New-Receipt 'peer_proof' 'Observed' 'peer'
            $pending=New-Operation 'peer_proof_pending_initial' 'PeerProof' `
                'pending-authority' 'Pending' 'None' `
                (New-Payload 'peer_proof_pending_initial' 'PeerProof' 'Pending' @($peerReceipt)) `
                $peerReceipt
            $currentPayload=New-Payload 'yield_current' 'Quiescent' `
                'PeerProofObserved' @($yieldReceipt,$peerReceipt)
            $current=New-Operation 'yield_current' 'Quiescent' `
                'current-authority' 'None' 'PeerProofObserved' `
                $currentPayload $peerReceipt
            $peerEnvelope=New-Envelope @($pending,$current) 8L 9L
            if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $peerEnvelope) -or
                -not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $peerEnvelope $pending) -or
                -not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $peerEnvelope $current)) {
                throw 'Exact peer-first FallbackTailQuiescent issuance was rejected.'
            }

            $priorPending=New-Operation 'peer_proof_pending_initial' 'PeerProof' `
                'pending-authority' 'None' 'Pending' `
                (New-Payload 'peer_proof_pending_initial' 'PeerProof' 'Pending' @($peerReceipt)) `
                $peerReceipt
            $priorBoundary=[pscustomobject]@{
                coordinatorInstanceId='coordinator'; boundaryId='prior'
                afterStateGeneration=8L; decisionUtcMilliseconds=1L
                operations=@($priorPending)
            }
            $currentBoundary=[pscustomobject]@{
                coordinatorInstanceId='coordinator'; boundaryId='current'
                afterStateGeneration=9L; decisionUtcMilliseconds=2L
                decisionKind='FallbackTailQuiescent'; decisionStatus='Accepted'
                operations=@($pending,$current)
            }
            $effect=[pscustomobject]@{
                CoordinatorInstanceId='coordinator'; TransitionId='prior'
                StateGeneration=8L; Family='fallback_tail_yield'
                OperationKey=$priorPending.operationKey
                AuthorityId=$priorPending.authorityId; OperationGeneration=1L
                EffectKind='peer_proof_observed'
                ReceiptIdentity=$peerReceipt.receiptIdentity
                ReceiptGeneration=1L; ReceiptStatus='Observed'
                ReceiptFingerprint=$peerReceipt.receiptFingerprint
                Route=$scope.route; ProtocolVersion=$scope.protocolVersion
                LiveRouteEpoch=$scope.liveRouteEpoch
                RoutePublicationGeneration=$scope.routePublicationGeneration
                RouteCommitGeneration=$scope.routeCommitGeneration
                RuntimeGeneration=$scope.runtimeGeneration
                TerminalGeneration=$scope.terminalGeneration
                LegGeneration=$scope.legGeneration
                TransportEpoch=$scope.transportEpoch
                BridgeRecoveryGeneration=$scope.bridgeRecoveryGeneration
                CheckpointGeneration=$scope.checkpointGeneration
                CheckpointRequestId=$scope.checkpointRequestId
                DataSessionIdentity=$scope.dataSessionIdentity
                DataSessionGeneration=$scope.dataSessionGeneration
            }
            $entry=[pscustomobject]@{Boundary=$currentBoundary;Operation=$current}
            $history=@(
                [pscustomobject]@{Boundary=$priorBoundary;Operation=$priorPending},
                $entry)
            if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect `
                    $entry $history @($effect))) {
                throw 'The exact pending-operation peer proof was not carried.'
            }
            $forged=$effect.PSObject.Copy()
            $forged.OperationKey=$current.operationKey
            $forged.AuthorityId=$current.authorityId
            $forged.TransitionId='current'; $forged.StateGeneration=9L
            if (Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect `
                    $entry $history @($forged)) {
                throw 'A copied peer-proof effect on the new yield was accepted.'
            }
            $mutated=$peerEnvelope.PSObject.Copy()
            $mutated.aggregate=$peerEnvelope.aggregate.PSObject.Copy()
            $mutated.aggregate.operations=@($pending,$current,$localOperation)
            if (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $mutated) {
                throw 'An extra Family-4 operation did not fail closed.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"FallbackTailQuiescent snapshot validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
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

            $boundaryWithoutFields = [pscustomobject]@{ Sequence = 4L; BoundaryId = 'boundary-4' }
            if ((Get-FileTransferEventField -Event $boundaryWithoutFields -Name 'missing' -Default 'fallback') -ne 'fallback' -or
                (Get-FileTransferEventInt64Field -Event $boundaryWithoutFields -Name 'missing' -Default 17L) -ne 17L) {
                throw 'Missing Fields property did not preserve the requested defaults.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Deferred field aggregate parity failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotRouteTransitionLifecycleJoinsRoleRolloverExactly()
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

            $source = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=1L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=1L; checkpointRequestId='checkpoint-1'
                dataSessionIdentity='data-session-1'; dataSessionGeneration=1L
            }
            $target = [ordered]@{
                route='file_tuna_v4'; protocolVersion=4
                liveRouteEpoch=2L; routePublicationGeneration=2L
                routeCommitGeneration=1L; runtimeGeneration=2L
                terminalGeneration=0L; legGeneration=2L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session-1'; dataSessionGeneration=1L
            }
            $receipts = @(
                [pscustomobject]@{ stage='pause_bind'; commandIdentity='pause'; commandGeneration=1L; commandFingerprint='pause-command'; receiptIdentity='pause'; receiptGeneration=1L; receiptStatus='Installed'; receiptFingerprint='pause-receipt'; receiptValidated=$true },
                [pscustomobject]@{ stage='drain'; commandIdentity='drain'; commandGeneration=1L; commandFingerprint='drain-command'; receiptIdentity='drain'; receiptGeneration=1L; receiptStatus='Completed'; receiptFingerprint='drain-receipt'; receiptValidated=$true },
                [pscustomobject]@{ stage='commit'; commandIdentity='commit'; commandGeneration=1L; commandFingerprint='commit-command'; receiptIdentity='commit'; receiptGeneration=1L; receiptStatus='Committed'; receiptFingerprint='commit-receipt'; receiptValidated=$true },
                [pscustomobject]@{ stage='disposition'; commandIdentity='disposition'; commandGeneration=1L; commandFingerprint='disposition-command'; receiptIdentity='disposition'; receiptGeneration=1L; receiptStatus='Applied'; receiptFingerprint='disposition-receipt'; receiptValidated=$true }
            )
            function New-RouteLifecycleEntry(
                [string]$boundaryId,
                [long]$time,
                [string]$decisionKind,
                [string]$decisionStatus,
                [string]$role,
                [string]$operationKind,
                [string]$beforeState,
                [string]$afterState,
                [bool]$stateApplied,
                [object[]]$operationReceipts,
                [string]$authority = 'route-authority',
                [object]$sourceBoundary = $source) {
                $operationKey = "route_transition|$role|$authority|1|$operationKind"
                [pscustomobject]@{
                    Boundary = [pscustomobject]@{
                        coordinatorInstanceId='coordinator-1'
                        writer='writer-1'
                        boundaryId=$boundaryId
                        decisionUtcMilliseconds=$time
                        afterStateGeneration=$time
                        decisionKind=$decisionKind
                        decisionStatus=$decisionStatus
                    }
                    Operation = [pscustomobject]@{
                        family='route_transition'; operationRole=$role
                        operationKey=$operationKey; operationKind=$operationKind
                        authorityId=$authority; operationGeneration=1L
                        beforeState=$beforeState; afterState=$afterState
                        stateApplied=$stateApplied
                        routeTransition=[pscustomobject]@{
                            transitionKind='RegularV4ToTunaV4'
                            sourceBoundary=$sourceBoundary; targetBoundary=$target
                            sourceTransport='RegularNkn'
                            proofBoundaryKind='RuntimeUnlock'
                            proofBoundaryFingerprint='proof'
                            durableCheckpointFingerprint='checkpoint'
                            targetRoute='file_tuna_v4'; targetProtocolVersion=4
                            targetTransport='Tuna'; handoffKind='NormalToTuna'
                            retainedFrameFingerprint='(none)'
                            retainedFrameType='(none)'
                            receipts=$operationReceipts
                        }
                    }
                }
            }
            function New-RouteLifecycleEffect(
                [string]$transitionId,
                [string]$operationKey,
                [string]$kind) {
                [pscustomobject]@{
                    CoordinatorInstanceId='coordinator-1'
                    TransitionId=$transitionId
                    OperationKey=$operationKey
                    EffectKind=$kind
                }
            }

            $prepared = New-RouteLifecycleEntry 'prepared' 1 'PrepareFileTransferRouteTransition' 'Prepared' 'current' 'RegularV4ToTunaV4' 'None' 'Prepared' $true @()
            $draining = New-RouteLifecycleEntry 'draining' 2 'BeginFileTransferRouteTransitionPhysicalDrain' 'Draining' 'current' 'RegularV4ToTunaV4' 'Prepared' 'Draining' $true @($receipts[0])
            $commitCurrent = New-RouteLifecycleEntry 'commit' 4 'CommitFileTransferRouteTransition' 'Committed' 'current' 'RegularV4ToTunaV4' 'Draining' 'None' $true @($receipts[0])
            $commitTerminal = New-RouteLifecycleEntry 'commit' 4 'CommitFileTransferRouteTransition' 'Committed' 'terminal' 'RegularV4ToTunaV4' 'None' 'Committed' $true $receipts
            $commitAttempt = New-RouteLifecycleEntry 'commit' 4 'CommitFileTransferRouteTransition' 'Committed' 'outbound' 'CommitFileTransferRouteTransition' 'Current' 'Committed' $false @($receipts[1])
            $disposition = New-RouteLifecycleEntry 'disposition' 5 'ApplyFileTransferRouteTransitionDisposition' 'Applied' 'terminal' 'RegularV4ToTunaV4' 'Committed' 'Committed' $true $receipts
            $dispositionAttempt = New-RouteLifecycleEntry 'disposition' 5 'ApplyFileTransferRouteTransitionDisposition' 'Applied' 'outbound' 'ApplyFileTransferRouteTransitionDisposition' 'Current' 'Applied' $false @($receipts[3])
            $retired = New-RouteLifecycleEntry 'retired' 6 'FinalizeFileTransferContext' 'Accepted' 'terminal' 'RegularV4ToTunaV4' 'Committed' 'None' $true $receipts
            $effects = @(
                (New-RouteLifecycleEffect 'draining' $draining.Operation.operationKey 'pause_installed'),
                (New-RouteLifecycleEffect 'commit' $commitAttempt.Operation.operationKey 'completed_drain_receipt'),
                (New-RouteLifecycleEffect 'commit' $commitTerminal.Operation.operationKey 'route_commit_adopted'),
                (New-RouteLifecycleEffect 'commit' $commitTerminal.Operation.operationKey 'target_runtime_leg_installed'),
                (New-RouteLifecycleEffect 'disposition' $dispositionAttempt.Operation.operationKey 'route_transition_disposition_applied'),
                (New-RouteLifecycleEffect 'disposition' $dispositionAttempt.Operation.operationKey 'transition_pause_released'),
                (New-RouteLifecycleEffect 'disposition' $dispositionAttempt.Operation.operationKey 'recovered_epoch_publication')
            )
            $successHistory = @(
                $prepared, $draining, $commitCurrent, $commitTerminal,
                $commitAttempt, $disposition, $dispositionAttempt, $retired)
            $success = Get-FileTransferCoordinatorRouteTransitionLifecycleResult -History $successHistory -Effects $effects
            if ($success.HasCandidate -or $success.UnresolvedCount -ne 0) {
                throw "A role-specific current-to-terminal commit rollover remained open: $($success | ConvertTo-Json -Compress)"
            }

            $rejectedReceipt = [pscustomobject]@{
                stage='drain'; commandIdentity='drain'; commandGeneration=1L
                commandFingerprint='drain-command'; receiptIdentity='drain'
                receiptGeneration=1L; receiptStatus='Rejected'
                receiptFingerprint='drain-rejected'; receiptValidated=$false
            }
            $rejected = New-RouteLifecycleEntry 'rejected' 3 'CommitFileTransferRouteTransition' 'Rejected' 'outbound' 'CommitFileTransferRouteTransition' 'Current' 'Rejected' $false @($rejectedReceipt)
            $failed = Get-FileTransferCoordinatorRouteTransitionLifecycleResult -History @($prepared,$draining,$rejected) -Effects @($effects[0])
            if (-not $failed.HasCandidate -or $failed.RawCount -ne 1 -or
                $failed.RecoveredCount -ne 0 -or $failed.UnresolvedCount -ne 1 -or
                $failed.Candidate.Boundary.boundaryId -ne 'rejected') {
                throw "An exact rejected commit was not retained as the unresolved boundary: $($failed | ConvertTo-Json -Compress)"
            }
            $rejectedCopy = New-RouteLifecycleEntry 'rejected-copy' 3 'CommitFileTransferRouteTransition' 'Rejected' 'outbound' 'CommitFileTransferRouteTransition' 'Current' 'Rejected' $false @($rejectedReceipt)
            $duplicateFailure = Get-FileTransferCoordinatorRouteTransitionLifecycleResult -History @($prepared,$draining,$rejected,$rejectedCopy) -Effects @($effects[0])
            if ($duplicateFailure.RawCount -ne 2 -or
                $duplicateFailure.RecoveredCount -ne 0 -or
                $duplicateFailure.UnresolvedCount -ne 1) {
                throw "Duplicate rejected attempts did not retain raw/distinct incident semantics: $($duplicateFailure | ConvertTo-Json -Compress)"
            }

            $invalidateCurrent = New-RouteLifecycleEntry 'invalidate' 4 'InvalidateFileTransferRouteTransition' 'Invalidated' 'current' 'RegularV4ToTunaV4' 'Draining' 'None' $true @($receipts[0])
            $invalidateTerminal = New-RouteLifecycleEntry 'invalidate' 4 'InvalidateFileTransferRouteTransition' 'Invalidated' 'terminal' 'RegularV4ToTunaV4' 'None' 'Invalidated' $true @($receipts[0],$receipts[3])
            $invalidateAttempt = New-RouteLifecycleEntry 'invalidate' 4 'InvalidateFileTransferRouteTransition' 'Invalidated' 'outbound' 'InvalidateFileTransferRouteTransition' 'Current' 'Invalidated' $false @($receipts[3])
            $invalidationEffects = @(
                (New-RouteLifecycleEffect 'draining' $draining.Operation.operationKey 'pause_installed'),
                (New-RouteLifecycleEffect 'invalidate' $invalidateAttempt.Operation.operationKey 'route_transition_disposition_applied'),
                (New-RouteLifecycleEffect 'invalidate' $invalidateAttempt.Operation.operationKey 'transition_pause_released')
            )
            $invalidatedFailure = Get-FileTransferCoordinatorRouteTransitionLifecycleResult -History @($prepared,$draining,$rejected,$invalidateCurrent,$invalidateTerminal,$invalidateAttempt) -Effects $invalidationEffects
            if ($invalidatedFailure.RecoveredCount -ne 0 -or
                $invalidatedFailure.UnresolvedCount -ne 1 -or
                $invalidatedFailure.Candidate.Boundary.boundaryId -ne 'rejected') {
                throw "A later terminal invalidation incorrectly recovered an accepted-drain commit rejection: $($invalidatedFailure | ConvertTo-Json -Compress)"
            }

            $recovered = Get-FileTransferCoordinatorRouteTransitionLifecycleResult -History (@($prepared,$draining,$rejected) + $successHistory[2..7]) -Effects $effects
            if (-not $recovered.HasCandidate -or $recovered.RawCount -ne 1 -or
                $recovered.RecoveredCount -ne 1 -or $recovered.UnresolvedCount -ne 0 -or
                $recovered.Candidate.Boundary.boundaryId -ne 'rejected') {
                throw "An exact later same-authority commit did not recover the rejected attempt: $($recovered | ConvertTo-Json -Compress)"
            }

            $mutatedSource = $source | ConvertTo-Json -Depth 8 | ConvertFrom-Json
            $mutatedSource.checkpointRequestId = 'checkpoint-mutated'
            $mutated = New-RouteLifecycleEntry 'mutated' 7 'CommitFileTransferRouteTransition' 'Committed' 'terminal' 'RegularV4ToTunaV4' 'None' 'Committed' $true $receipts 'route-authority' $mutatedSource
            $originalKey = Get-FileTransferCoordinatorRouteTransitionLifecycleKey -Boundary $commitTerminal.Boundary -Operation $commitTerminal.Operation
            $mutatedKey = Get-FileTransferCoordinatorRouteTransitionLifecycleKey -Boundary $mutated.Boundary -Operation $mutated.Operation
            if ($originalKey -ceq $mutatedKey) {
                throw 'A complete source-scope mutation was admitted into the same route-transition lifecycle.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Route-transition snapshot lifecycle regression failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotResponderHandoffReducerConsumesOnlyExactPostCommitDuplicates()
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

            $source = [pscustomobject]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=0L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session-1'; dataSessionGeneration=1L
            }
            $target = [pscustomobject]@{
                route='file_tuna_v4'; protocolVersion=4
                liveRouteEpoch=1L; routePublicationGeneration=2L
                routeCommitGeneration=1L; runtimeGeneration=2L
                terminalGeneration=0L; legGeneration=2L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session-1'; dataSessionGeneration=1L
            }
            function New-ResponderPayload(
                [string]$role,
                [object]$effective,
                [string]$linkedAuthority='(none)',
                [long]$linkedGeneration=0L) {
                [pscustomobject]@{
                    role=$role; transitionMode='initial_regular_v4'
                    sourceBoundary=$source; effectiveBoundary=$effective
                    targetBoundary=$target; transactionGeneration=1L
                    offerGeneration=6L; pendingAnswerAckGeneration=6L
                    payerDecisionId='4'; launchGeneration=1L
                    tunaPathLeaseGeneration=1L; tunaPathLeaseListenerRunId='listener-1'
                    ackMessageId='ack-1'; ackSourceFingerprint='ack-fingerprint-1'
                    peerProbeFrameFingerprint='probe-fingerprint-1'; probeId='probe-1'
                    ownerKey='(none)'; pairKey='(none)'; localProofIdentity='(none)'
                    localProofFingerprint='(none)'; peerProofIdentity='(none)'
                    peerProofFingerprint='(none)'; pairedReleaseAuthorityId='(none)'
                    pairedReleaseGeneration=0L; pairingGeneration=0L
                    admissionGeneration=1L; pendingPreCommitGeneration=1L
                    linkedTransitionAuthorityId=$linkedAuthority
                    linkedTransitionGeneration=$linkedGeneration
                }
            }
            function New-ResponderOperation(
                [string]$role,
                [string]$kind,
                [string]$afterState,
                [string]$receiptStatus,
                [object]$payload,
                [string]$receiptFingerprint,
                [bool]$validated=$true) {
                [pscustomobject]@{
                    family='responder_probe_handoff'; operationRole=$role
                    operationKey="responder_probe_handoff|$role|handoff-1|1|$kind"
                    operationKind=$kind; authorityId='handoff-1'
                    operationGeneration=1L; beforeState='None'; afterState=$afterState
                    commandIdentity='observation-1'; commandGeneration=1L
                    commandFingerprint='observation-fingerprint-1'
                    receiptIdentity='observation-receipt-1'; receiptGeneration=1L
                    receiptStatus=$receiptStatus; receiptFingerprint=$receiptFingerprint
                    receiptValidated=$validated; stateApplied=$false
                    responderProbeHandoff=$payload
                }
            }
            function New-RouteOperation(
                [string]$role,
                [string]$beforeState,
                [string]$afterState,
                [string]$receiptStatus,
                [bool]$validated) {
                [pscustomobject]@{
                    family='route_transition'; operationRole=$role
                    operationKey="route_transition|$role|route-transition-1|1|RegularV4ToTunaV4"
                    operationKind='RegularV4ToTunaV4'; authorityId='route-transition-1'
                    operationGeneration=1L; beforeState=$beforeState; afterState=$afterState
                    receiptStatus=$receiptStatus; receiptValidated=$validated
                }
            }
            function New-PublicationOperation {
                [pscustomobject]@{
                    family='route_runtime'; operationRole='route_publication'
                    operationKind='RouteCommitPublication'; beforeState='None'
                    afterState='Published'; receiptValidated=$true
                    routeRuntime=[pscustomobject]@{ boundary=$target }
                }
            }
            function New-Entry(
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$decisionKind,
                [string]$decisionStatus,
                [object]$operation,
                [object[]]$otherOperations=@(),
                [string]$decisionReason='accepted') {
                $operations = @($operation) + @($otherOperations)
                [pscustomobject]@{
                    Boundary=[pscustomobject]@{
                        boundaryId="$decisionKind-$beforeGeneration-$afterGeneration"
                        coordinatorInstanceId='coordinator-1'; writer='writer-helper'
                        sessionId='session-1'; transferId='transfer-1'; direction='inbound'
                        decisionKind=$decisionKind; decisionStatus=$decisionStatus
                        decisionReason=$decisionReason
                        beforeStateGeneration=$beforeGeneration
                        afterStateGeneration=$afterGeneration
                        beforeStateHash="hash-$beforeGeneration"
                        afterStateHash="hash-$afterGeneration"
                        operations=$operations
                    }
                    Operation=$operation
                }
            }

            $accepted = New-Entry 0 0 `
                'ObserveRuntimeUnlockResponderPeerProbeHandoff' 'Idempotent' `
                (New-ResponderOperation 'peer_acceptance_attempt' `
                    'ObserveRuntimeUnlockResponderPeerProbeHandoff' 'Accepted' `
                    'Accepted' (New-ResponderPayload 'peer_acceptance_attempt' $source) `
                    'accepted-receipt-fingerprint')
            $prepared = New-Entry 0 1 'PrepareFileTransferRouteTransition' 'Prepared' `
                (New-ResponderOperation 'route_transition_handoff_pending' `
                    'PrepareFileTransferRouteTransition' 'Prepared' 'Prepared' `
                    (New-ResponderPayload 'route_transition_handoff_pending' $source `
                        'route-transition-1' 1L) 'prepared-receipt-fingerprint') `
                @((New-RouteOperation 'current' 'None' 'Prepared' '(none)' $false))
            $draining = New-Entry 1 2 `
                'BeginFileTransferRouteTransitionPhysicalDrain' 'Draining' `
                (New-ResponderOperation 'route_transition_handoff_pending' `
                    'BeginFileTransferRouteTransitionPhysicalDrain' 'Draining' `
                    'Draining' (New-ResponderPayload 'route_transition_handoff_pending' `
                        $source 'route-transition-1' 1L) 'draining-receipt-fingerprint') `
                @((New-RouteOperation 'current' 'Prepared' 'Draining' 'Installed' $true))
            $committed = New-Entry 2 3 'CommitFileTransferRouteTransition' 'Committed' `
                (New-ResponderOperation 'route_transition_handoff_pending' `
                    'CommitFileTransferRouteTransition' 'Committed' 'Committed' `
                    (New-ResponderPayload 'route_transition_handoff_pending' $target) `
                    'committed-receipt-fingerprint') `
                @(
                    (New-RouteOperation 'terminal' 'None' 'Committed' 'Committed' $true),
                    (New-PublicationOperation))
            $disposition = New-Entry 3 4 `
                'ApplyFileTransferRouteTransitionDispositionReceipt' 'Committed' `
                (New-ResponderOperation 'route_transition_handoff_pending' `
                    'ApplyFileTransferRouteTransitionDispositionReceipt' 'Committed' `
                    'Committed' (New-ResponderPayload 'route_transition_handoff_pending' `
                        $target) 'disposition-receipt-fingerprint') `
                @((New-RouteOperation 'terminal' 'Committed' 'Committed' 'Applied' $true))
            $candidate = New-Entry 4 4 `
                'ObserveRuntimeUnlockResponderPeerProbeHandoff' 'Rejected' `
                (New-ResponderOperation 'attempted_decision' `
                    'ObserveRuntimeUnlockResponderPeerProbeHandoff' 'Rejected' `
                    'Rejected' (New-ResponderPayload 'attempted_decision' $target) `
                    'rejected-receipt-fingerprint')

            $history = @($accepted,$prepared,$draining,$committed,$disposition)
            $duplicates = @(1..55 | ForEach-Object { $candidate })
            foreach ($duplicate in $duplicates) {
                if (-not (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                        -History ($history + $duplicates) -Candidate $duplicate)) {
                    throw 'An exact post-disposition duplicate was not consumed.'
                }
            }
            if (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                    -History @($accepted,$prepared,$draining,$candidate) `
                    -Candidate $candidate) {
                throw 'A partial Prepared/Draining lineage consumed a rejection.'
            }

            $mutatedCandidate = $candidate | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $mutatedCandidate.Operation.responderProbeHandoff.probeId = 'probe-2'
            $originalKey = Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey `
                -Boundary $candidate.Boundary -Operation $candidate.Operation
            $mutatedKey = Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey `
                -Boundary $mutatedCandidate.Boundary -Operation $mutatedCandidate.Operation
            if ($originalKey -ceq $mutatedKey) {
                throw 'A probe mutation reused the completed responder lineage key.'
            }

            function New-AggregateEntry([object]$entry) {
                [pscustomobject]@{
                    Boundary=$entry.Boundary; BeforeBoundary=$target; AfterBoundary=$target
                    SessionId='session-1'; TransferId='transfer-1'; Direction='inbound'
                }
            }
            $closedHistory=$history | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            foreach ($entry in $closedHistory) {
                $entry.Boundary.beforeStateGeneration+=8L
                $entry.Boundary.afterStateGeneration+=8L
                $entry.Boundary.beforeStateHash="hash-$($entry.Boundary.beforeStateGeneration)"
                $entry.Boundary.afterStateHash="hash-$($entry.Boundary.afterStateGeneration)"
            }
            $late=$candidate | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $late.Boundary.boundaryId='late-3108'
            $late.Boundary.beforeStateGeneration=15L
            $late.Boundary.afterStateGeneration=15L
            $late.Boundary.beforeStateHash='hash-15'
            $late.Boundary.afterStateHash='hash-15'
            $lateCopy=$late | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $lateCopy.Boundary.boundaryId='late-3141'
            $unrelated=@(12L..14L | ForEach-Object {
                New-AggregateEntry (New-Entry $_ ($_+1L) 'PressureMutation' 'Accepted' ([pscustomobject]@{
                    family='regular_v4_frontier_repair_pressure'; operationRole='pressure_binding_installed'
                    operationKey="pressure-$_"; authorityId="pressure-$_"; operationGeneration=$_
                    beforeState='Pending'; afterState='Installed'; stateApplied=$true
                }))
            })
            $entries=@((New-AggregateEntry $closedHistory[-1]))+$unrelated+@(
                (New-AggregateEntry $late),(New-AggregateEntry $lateCopy))
            function Assert-LateConsumed([object[]]$interval, [bool]$expected) {
                $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $interval
                $actual=Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                    -History (@($closedHistory)+@($late,$lateCopy)) -Candidate $late -AggregateIndex $index
                if ($actual -ne $expected) { throw "Unexpected aggregate-interval consumption: expected=$expected" }
            }
            Assert-LateConsumed $entries $true
            $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $entries
            if (-not (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                    -History (@($closedHistory)+@($late,$lateCopy)) -Candidate $lateCopy -AggregateIndex $index)) {
                throw 'Second exact late duplicate was not consumed.'
            }
            if (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                    -History (@($closedHistory)+@($late)) -Candidate $late) {
                throw 'A later generation was consumed without aggregate interval proof.'
            }
            Assert-LateConsumed @($entries[0],$entries[1],$entries[3],$entries[4],$entries[5]) $false
            Assert-LateConsumed ($entries+@($entries[2])) $false
            foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration',
                    'routeCommitGeneration','runtimeGeneration','terminalGeneration','legGeneration',
                    'transportEpoch','bridgeRecoveryGeneration','checkpointGeneration','checkpointRequestId',
                    'dataSessionIdentity','dataSessionGeneration')) {
                $mutated=$entries | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                if ($mutated[1].AfterBoundary.$field -is [string]) { $mutated[1].AfterBoundary.$field='changed' }
                else { $mutated[1].AfterBoundary.$field++ }
                # Restore A after B: the candidate still has the original target scope.
                $mutated[2].BeforeBoundary=$mutated[1].AfterBoundary
                Assert-LateConsumed $mutated $false
            }
            foreach ($mutation in @(
                    { param($e) $e[2].Boundary.beforeStateHash='wrong-hash' },
                    { param($e) $e[2].Boundary.afterStateGeneration=12L },
                    { param($e) $e[2].Boundary.writer='other-writer' },
                    { param($e) $e[2].Boundary.coordinatorInstanceId='other-coordinator' },
                    { param($e) $e[2].SessionId='other-session' },
                    { param($e) $e[2].TransferId='other-transfer' },
                    { param($e) $e[2].Direction='outbound' },
                    { param($e) $e[4].BeforeBoundary.dataSessionGeneration=2L },
                    { param($e) $e[4].SessionId='other-session' })) {
                $mutated=$entries | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                & $mutation $mutated
                Assert-LateConsumed $mutated $false
            }
            $carry=$closedHistory[-1].Operation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $carry.beforeState=$carry.afterState
            $carry.stateApplied=$true
            $carried=$entries | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $carried[2].Boundary.operations+=@($carry)
            Assert-LateConsumed $carried $true
            foreach ($mutation in @(
                    { param($o) $o.afterState='Invalidated' },
                    { param($o) $o.authorityId='successor-authority' },
                    { param($o) $o.commandFingerprint='conflict' },
                    { param($o) $o.responderProbeHandoff.probeId='replacement-probe' })) {
                $mutated=$carried | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                & $mutation $mutated[2].Boundary.operations[-1]
                Assert-LateConsumed $mutated $false
            }
            $retired=$entries | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $retired[2].Boundary.operations+=@((New-RouteOperation 'terminal' 'Committed' 'None' 'Invalidated' $true))
            Assert-LateConsumed $retired $false
            foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration')) {
                $wrong=$late | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                if ($wrong.Operation.$field -is [string]) { $wrong.Operation.$field='wrong' }
                else { $wrong.Operation.$field++ }
                if (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                        -History (@($closedHistory)+@($wrong)) -Candidate $wrong -AggregateIndex $index) {
                    throw "Wrong observation $field was consumed."
                }
            }
            $other=$entries | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            foreach ($entry in $other) { $entry.Boundary.coordinatorInstanceId='other-cycle'; $entry.Boundary.writer='other-writer' }
            Assert-LateConsumed ($entries+$other) $true

            function New-PreparationIntervalEntry([object]$entry) {
                [pscustomobject]@{
                    Boundary=$entry.Boundary; BeforeBoundary=$source; AfterBoundary=$source
                    SessionId='session-1'; TransferId='transfer-1'; Direction='inbound'
                }
            }
            function Assert-PreparationFirst([object[]]$h, [object[]]$interval, [bool]$expected) {
                $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $interval
                $actual=Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                    -History $h -Candidate $candidate -AggregateIndex $index
                if ($actual -ne $expected) { throw "Preparation-first acceptance: expected=$expected" }
            }
            foreach ($observationGeneration in @(1L,2L)) {
                $preparationFirst=$history | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $observation=$preparationFirst[0]
                $observation.Boundary.boundaryId="acceptance-at-$observationGeneration"
                $observation.Boundary.beforeStateGeneration=$observationGeneration
                $observation.Boundary.afterStateGeneration=$observationGeneration
                $observation.Boundary.beforeStateHash="hash-$observationGeneration"
                $observation.Boundary.afterStateHash="hash-$observationGeneration"
                $observation.Operation.responderProbeHandoff.linkedTransitionAuthorityId='route-transition-1'
                $observation.Operation.responderProbeHandoff.linkedTransitionGeneration=1L
                $interval=@($preparationFirst | ForEach-Object { New-PreparationIntervalEntry $_ })
                Assert-PreparationFirst $preparationFirst $interval $true
                $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $interval
                foreach ($copy in @(1..65 | ForEach-Object { $candidate })) {
                    if (-not (Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                        -History ($preparationFirst+@($copy)) -Candidate $copy -AggregateIndex $index)) {
                        throw 'An exact preparation-first completed lineage reopened.'
                    }
                }
                Assert-PreparationFirst $preparationFirst @() $false
                foreach ($mutation in @(
                    { param($h) $h[0].Boundary.decisionStatus='Accepted' },
                    { param($h) $h[0].Boundary.beforeStateGeneration-- },
                    { param($h) $h[0].Boundary.beforeStateHash='conflict' },
                    { param($h) $h[0].Operation.stateApplied=$true },
                    { param($h) $h[0].Operation.receiptValidated=$false },
                    { param($h) $h[0].Operation.receiptStatus='Held' },
                    { param($h) $h[0].Operation.commandFingerprint='wrong-observation' },
                    { param($h) $h[0].Operation.receiptIdentity='wrong-receipt' },
                    { param($h) $h[0].Operation.responderProbeHandoff.linkedTransitionAuthorityId='wrong-transition' },
                    { param($h) $h[0].Operation.responderProbeHandoff.linkedTransitionGeneration=2L },
                    { param($h) $h[0].Operation.responderProbeHandoff.pendingPreCommitGeneration=2L },
                    { param($h) $h[0].Operation.responderProbeHandoff.admissionGeneration=2L },
                    { param($h) $h[0].Operation.responderProbeHandoff.probeId='wrong-probe' },
                    { param($h) $h[0].Operation.responderProbeHandoff.sourceBoundary.dataSessionIdentity='other-data' },
                    { param($h) $h[0].Operation.responderProbeHandoff.effectiveBoundary.checkpointGeneration=2L },
                    { param($h) $h[0].Operation.responderProbeHandoff.targetBoundary.dataSessionGeneration=2L })) {
                    $wrong=$preparationFirst | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                    & $mutation $wrong
                    Assert-PreparationFirst $wrong @($wrong | ForEach-Object { New-PreparationIntervalEntry $_ }) $false
                }
                foreach ($missing in @(1,2,3,4)) {
                    Assert-PreparationFirst @($preparationFirst | Where-Object { $_ -ne $preparationFirst[$missing] }) $interval $false
                }
                $afterCommit=$preparationFirst | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $afterCommit[0].Boundary.beforeStateGeneration=3L
                $afterCommit[0].Boundary.afterStateGeneration=3L
                $afterCommit[0].Boundary.beforeStateHash='hash-3'
                $afterCommit[0].Boundary.afterStateHash='hash-3'
                Assert-PreparationFirst $afterCommit @($afterCommit | ForEach-Object { New-PreparationIntervalEntry $_ }) $false
                if ($observationGeneration -eq 2L) {
                    Assert-PreparationFirst $preparationFirst @($interval | Where-Object { $_ -ne $interval[2] }) $false
                    Assert-PreparationFirst $preparationFirst ($interval+@($interval[2])) $false
                    foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration',
                        'routeCommitGeneration','runtimeGeneration','terminalGeneration','legGeneration',
                        'transportEpoch','bridgeRecoveryGeneration','checkpointGeneration','checkpointRequestId',
                        'dataSessionIdentity','dataSessionGeneration')) {
                        $wrong=$interval | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                        if ($wrong[2].AfterBoundary.$field -is [string]) { $wrong[2].AfterBoundary.$field='mutated' }
                        else { $wrong[2].AfterBoundary.$field++ }
                        Assert-PreparationFirst $preparationFirst $wrong $false
                    }
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Responder-handoff lifecycle reducer regression failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
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

            $locatedUnboundEffect =
                '[2026-01-01 00:00:01Z] [INFO] [FileTransferCoordinator] ' +
                ($effect -replace
                    'operation_key=route_runtime\|route_publication\|authority-1\|1\|RoutePublication',
                    'operation_key=route_runtime|route_publication|authority-1|1|[redacted]') +
                '; log_writer_id=writer-helper; log_writer_sequence=249'
            $laterTransitionFailure =
                '[2026-01-01 00:00:02Z] [INFO] [FileTransferCoordinator] ' +
                ($transition -replace 'snapshot_contract_version=1', 'snapshot_contract_version=2') +
                '; log_writer_id=writer-sender; log_writer_sequence=9286'
            $orderedFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                'event=irrelevant',
                $start,
                $transition,
                $locatedUnboundEffect,
                $laterTransitionFailure,
                $completed)
            $orderedCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,
                $transition,
                $locatedUnboundEffect,
                $laterTransitionFailure,
                $completed)
            if ($orderedFull.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_authority_effect_unbound' -or
                $orderedFull.FirstUnresolvedBoundary.eventName -ne
                    'filetransfer_coordinator_authority_effect' -or
                $orderedFull.FirstUnresolvedBoundary.writer -ne 'writer-helper' -or
                $orderedFull.FirstUnresolvedBoundary.writerSequence -ne 249L -or
                $orderedFull.FirstUnresolvedBoundary.rawCount -ne 1 -or
                $orderedFull.FirstUnresolvedBoundary.variant -ne
                    'structural_operation_key_tail_redacted_by_generic_long_token_filter' -or
                -not $orderedFull.ArtifactEvidenceFailureReason.StartsWith(
                    'snapshot_authority_effect_unbound:',
                    [StringComparison]::Ordinal) -or
                $orderedFull.BoundaryHash -ne $orderedCompact.BoundaryHash -or
                $orderedFull.FirstUnresolvedBoundary.boundaryHash -ne
                    $orderedCompact.FirstUnresolvedBoundary.boundaryHash -or
                $orderedCompact.FirstUnresolvedBoundary.writerSequence -ne 249L) {
                throw "Snapshot validation failures were not arbitrated by exact evidence location: $($orderedFull | ConvertTo-Json -Depth 8 -Compress) / $($orderedCompact | ConvertTo-Json -Depth 8 -Compress)"
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
            $routeAttemptOperation = [ordered]@{
                family='route_transition'; operationRole='outbound'
                operationKey='route_transition|outbound|transition-authority|1|CommitFileTransferRouteTransition'
                operationKind='CommitFileTransferRouteTransition'; authorityId='transition-authority'
                predecessorAuthorityId='(none)'; operationGeneration=1L
                beforeState='Current'; afterState='Committed'
                commandIdentity='transition-authority:drain'; commandGeneration=1L
                commandFingerprint='drain-command'; receiptIdentity='transition-authority:drain'
                receiptGeneration=1L; receiptStatus='Completed'; receiptFingerprint='drain-receipt'
                receiptValidated=$true; stateApplied=$false; routeRuntime=$null
                routeTransition=[ordered]@{
                    role='outbound'; transitionKind='RegularV4ToTunaV4'
                    sourceBoundary=$routeScope; targetBoundary=$targetScope; sourceTransport='RegularNkn'
                    proofBoundaryKind='RuntimeUnlock'; proofAdmissionMode='runtime_unlock_precommit'; proofBoundaryFingerprint='proof'
                    durableCheckpointFingerprint='checkpoint'; targetRoute='file_tuna_v4'
                    targetProtocolVersion=4; targetTransport='Tuna'; handoffKind='NormalToTuna'
                    dispositionKind='Committed'; retainedFrameFingerprint='(none)'
                    retainedFrameType='(none)'
                    receipts=@([ordered]@{
                        stage='drain'; commandIdentity='transition-authority:drain'
                        commandGeneration=1L; commandFingerprint='drain-command'
                        receiptIdentity='transition-authority:drain'; receiptGeneration=1L
                        receiptStatus='Completed'; receiptReason='drain_completed'
                        receiptFingerprint='drain-receipt'; receiptValidated=$true
                    })
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
                operations=@($routeOperation,$routeAttemptOperation,$routeCommitOperation,$routePublicationOperation)
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
            $malformedAttemptEnvelope = $routeEnvelope |
                ConvertTo-Json -Depth 32 -Compress |
                ConvertFrom-Json
            $malformedAttempt = @(
                $malformedAttemptEnvelope.aggregate.operations |
                    Where-Object { -not [bool]$_.stateApplied })[0]
            $malformedAttempt.routeTransition.receipts[0].stage =
                'CommitFileTransferRouteTransition'
            $malformedAttemptJson = $malformedAttemptEnvelope |
                ConvertTo-Json -Depth 32 -Compress
            $malformedAttemptBytes =
                [Text.UTF8Encoding]::new($false).GetBytes($malformedAttemptJson)
            $malformedAttemptHash =
                Get-FileTransferCoordinatorSnapshotSha256 `
                    -Bytes $malformedAttemptBytes
            $malformedAttemptPayload =
                [Convert]::ToBase64String($malformedAttemptBytes).
                    TrimEnd('=').Replace('+','-').Replace('/','_')
            $malformedAttemptTransition = "event=filetransfer_coordinator_transition_snapshot; $routePrefix; transition_id=transition-route; before_state_generation=0; after_state_generation=1; snapshot_sha256=$malformedAttemptHash; snapshot_payload=$malformedAttemptPayload; authority_effect=none"
            $malformedAttemptResult =
                Get-FileTransferCoordinatorSnapshotEvaluation `
                    -Lines (@($routeStart,$malformedAttemptTransition) +
                        $routeEffects + @($routeCompleted))
            if ($malformedAttemptResult.ArtifactEvidenceVerdict -ne 'fail' -or
                $malformedAttemptResult.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_operation_catalog_mismatch' -or
                $malformedAttemptResult.FirstUnresolvedBoundary.variant -ne
                    'route_transition_attempt_receipt_stage_projected_as_decision_kind_instead_of_typed_stage' -or
                $malformedAttemptResult.FirstUnresolvedBoundary.rawCount -ne 1) {
                throw "A decision-kind route attempt receipt did not fail at its exact typed stage: $($malformedAttemptResult | ConvertTo-Json -Depth 8 -Compress)"
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

            $retryScope = [ordered]@{
                route='post_tuna_fallback_v6'; protocolVersion=6
                liveRouteEpoch=2L; routePublicationGeneration=2L
                routeCommitGeneration=2L; runtimeGeneration=2L
                terminalGeneration=0L; legGeneration=2L
                transportEpoch=3L; bridgeRecoveryGeneration=4L
                checkpointGeneration=5L; checkpointRequestId='checkpoint-retry'
                dataSessionIdentity='data-session-retry'; dataSessionGeneration=6L
            }
            function New-RetryOperation(
                [string]$authorityId,
                [string]$predecessorAuthorityId,
                [string]$role,
                [long]$authorityGeneration,
                [string]$beforeState,
                [string]$afterState,
                [bool]$dispatchObserved,
                [object[]]$receipts) {
                [ordered]@{
                    family='runtime_unlock_retry_lifecycle'; operationRole=$role
                    operationKey="runtime_unlock_retry_lifecycle|$role|$authorityId|$authorityGeneration|RetryAttempt"
                    operationKind='RetryAttempt'; authorityId=$authorityId
                    predecessorAuthorityId=$predecessorAuthorityId
                    operationGeneration=$authorityGeneration
                    beforeState=$beforeState; afterState=$afterState
                    commandIdentity=[string]$receipts[-1].commandIdentity
                    commandGeneration=[long]$receipts[-1].commandGeneration
                    commandFingerprint=[string]$receipts[-1].commandFingerprint
                    receiptIdentity=[string]$receipts[-1].receiptIdentity
                    receiptGeneration=[long]$receipts[-1].receiptGeneration
                    receiptStatus=[string]$receipts[-1].receiptStatus
                    receiptFingerprint=[string]$receipts[-1].receiptFingerprint
                    receiptValidated=$true; stateApplied=$true
                    routeRuntime=$null; routeTransition=$null
                    runtimeUnlockRetry=[ordered]@{
                        role=$role; sourceBoundary=$retryScope
                        authorityGeneration=$authorityGeneration
                        recoveryContractGeneration=7L
                        dispatchAttemptGeneration=[int]$authorityGeneration
                        missingDispatchSuccessorGeneration=[int]($authorityGeneration - 1L)
                        retiredOfferGeneration=8L; offerGeneration=8L
                        offerNonceFingerprint="nonce-$authorityGeneration"
                        dispatchDeadlineUtcMs=9000L
                        dispatchObserved=$dispatchObserved
                        releaseKind='FallbackPairedYield'
                        releaseAuthorityId='paired-release-1'; releaseGeneration=3
                        releaseOutcome='Paired'; listenerRearmRequirement='Required'
                        listenerExecutionMode='Default'
                        requestActivePredecessorSupersession=$false
                        successorAuthorityId='(none)'; receipts=$receipts
                    }
                }
            }
            function New-RetryEnvelopeLine(
                [string]$transitionId,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [object]$operation,
                [long]$decisionTime,
                [string]$decisionKind = 'ObserveRuntimeUnlockRetryDispatchSettlement',
                [string]$decisionStatus = 'Accepted',
                [string]$decisionReason = 'retry_dispatch_settlement_accepted') {
                $aggregate = [ordered]@{
                    beforeStateGeneration=$beforeGeneration
                    afterStateGeneration=$afterGeneration
                    beforeStateFingerprint=$beforeHash
                    afterStateFingerprint=$afterHash
                    beforeBoundary=$retryScope; afterBoundary=$retryScope
                    changedFamilies=@('runtime_unlock_retry_lifecycle')
                    operations=@($operation)
                }
                $envelope = [ordered]@{
                    snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='coordinator-retry'; sessionId='session-retry'
                    transferId='transfer-retry'; direction='outbound'
                    decisionUtcMilliseconds=$decisionTime; transitionId=$transitionId
                    decisionKind=$decisionKind
                    decisionStatus=$decisionStatus
                    decisionReason=$decisionReason
                    aggregate=$aggregate
                }
                $json = $envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $retryPrefix; transition_id=$transitionId; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            function New-RetryEffect(
                [string]$transitionId,
                [long]$stateGeneration,
                [string]$authorityId,
                [long]$authorityGeneration,
                [string]$role,
                [string]$kind,
                [string]$receiptId,
                [long]$receiptGeneration,
                [string]$status,
                [string]$fingerprint) {
                "event=filetransfer_coordinator_authority_effect; $retryPrefix; transition_id=$transitionId; coordinator_state_generation=$stateGeneration; operation_family=runtime_unlock_retry_lifecycle; operation_key=runtime_unlock_retry_lifecycle|$role|$authorityId|$authorityGeneration|RetryAttempt; operation_authority_id=$authorityId; operation_generation=$authorityGeneration; effect_kind=$kind; effect_identity=$receiptId; effect_generation=$receiptGeneration; receipt_identity=$receiptId; receipt_generation=$receiptGeneration; receipt_status=$status; receipt_fingerprint=$fingerprint; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=2; route_commit_generation=2; runtime_generation=2; terminal_generation=0; leg_generation=2; transport_epoch=3; bridge_recovery_generation=4; checkpoint_generation=5; checkpoint_request_id=checkpoint-retry; data_session_identity=data-session-retry; data_session_generation=6; authority_effect=applied"
            }
            $retryPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-retry; session_id=session-retry; transfer_id=transfer-retry; direction=outbound"
            $retryStart = "event=filetransfer_coordinator_snapshot_stream_started; $retryPrefix; initial_state_generation=0; initial_state_hash=retry-before; emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none"
            $missingReceipt = [ordered]@{
                stage='settlement'; commandIdentity='retry-a:settlement'; commandGeneration=8L
                commandFingerprint='retry-a-command'; receiptIdentity='retry-a:settlement'
                receiptGeneration=8L; receiptStatus='MissingDispatch'
                receiptReason='missing_dispatch'; receiptFingerprint='retry-a-settlement'
                receiptValidated=$true
            }
            $retryA = New-RetryOperation 'retry-a' '(none)' 'attempt_terminal' 1L 'SendInFlight' 'Settled' $false @($missingReceipt)
            $retryTransitionA = New-RetryEnvelopeLine 'retry-transition-a' 0L 1L 'retry-before' 'retry-after-a' $retryA 10L
            $retryEffectA = New-RetryEffect 'retry-transition-a' 1L 'retry-a' 1L 'attempt_terminal' 'settlement_observed' 'retry-a:settlement' 8L 'MissingDispatch' 'retry-a-settlement'
            $retryCompletedA = "event=filetransfer_coordinator_snapshot_stream_completed; $retryPrefix; final_state_generation=1; final_state_hash=retry-after-a; transition_count=1; emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none"
            $retryUnresolved = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($retryStart,$retryTransitionA,$retryEffectA,$retryCompletedA)
            if ($retryUnresolved.ArtifactEvidenceVerdict -ne 'pass' -or
                $retryUnresolved.FirstUnresolvedBoundary.taxonomyClass -ne 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch' -or
                $retryUnresolved.RuntimeUnlockRetryFamilyLegacySelectionCount -ne 0 -or
                $retryUnresolved.LegacySelectionCount -ne 6) {
                throw "A MissingDispatch attempt without an exact successor was not selected by Snapshot V1: $($retryUnresolved | ConvertTo-Json -Depth 8 -Compress)"
            }

            $closureDecisionReceipt = [ordered]@{
                stage='decision'; commandIdentity='retry-a:successor:2'
                commandGeneration=2L; commandFingerprint='retry-a-successor-command'
                receiptIdentity='retry-a:successor:2:decision'; receiptGeneration=2L
                receiptStatus='Rejected'
                receiptReason='retry_dispatch_missing_successor_limit_exhausted'
                receiptFingerprint='retry-a-successor-rejected'; receiptValidated=$true
            }
            $closureRejectedOperation = New-RetryOperation 'retry-a' '(none)' 'attempt_terminal' 1L 'Settled' 'Settled' $false @($closureDecisionReceipt)
            $closureRejectedOperation.stateApplied = $false
            $closureRejected = New-RetryEnvelopeLine 'retry-closure-rejected' 1L 1L 'retry-after-a' 'retry-after-a' $closureRejectedOperation 11L 'PrepareRuntimeUnlockRetryDispatchSuccessor' 'Rejected' 'retry_dispatch_missing_successor_limit_exhausted'
            $retryClosureMissingCompleted = "event=filetransfer_coordinator_snapshot_stream_completed; $retryPrefix; final_state_generation=1; final_state_hash=retry-after-a; transition_count=2; emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none"
            $retryClosureMissing = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($retryStart,$retryTransitionA,$retryEffectA,$closureRejected,$retryClosureMissingCompleted)
            $retryClosureMissingCorrelation = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $retryClosureMissing -Family 'runtime_unlock_retry_lifecycle'
            if ($retryClosureMissing.FirstUnresolvedBoundary.taxonomyClass -ne 'runtime_unlock_retry_dispatch_chain_closure_incomplete' -or
                $retryClosureMissingCorrelation.RawCount -ne 1 -or
                $retryClosureMissingCorrelation.DistinctCount -ne 1 -or
                $retryClosureMissingCorrelation.RecoveredCount -ne 0 -or
                $retryClosureMissingCorrelation.UnresolvedCount -ne 1) {
                throw "A rejected bounded successor without exact closure was not selected: $($retryClosureMissing | ConvertTo-Json -Depth 8 -Compress)"
            }

            $closureReceipt = [ordered]@{
                stage='bounded_closure'; commandIdentity='retry-a:successor:2'
                commandGeneration=2L; commandFingerprint='retry-a-successor-command'
                receiptIdentity='retry-a:closure:1'; receiptGeneration=1L
                receiptStatus='Closed'; receiptReason='retry_dispatch_chain_closed'
                receiptFingerprint='retry-a-closure'; receiptValidated=$true
            }
            $closureOperation = New-RetryOperation 'retry-a' '(none)' 'bounded_closure' 1L 'None' 'Closed' $false @($closureReceipt)
            $closureOperation.operationKey = 'runtime_unlock_retry_lifecycle|bounded_closure|retry-a|1|BoundedClosure'
            $closureOperation.operationKind = 'BoundedClosure'
            $closureOperation.runtimeUnlockRetry.role = 'bounded_closure'
            $closureTransition = New-RetryEnvelopeLine 'retry-closure-applied' 1L 2L 'retry-after-a' 'retry-after-closure' $closureOperation 12L 'CloseRuntimeUnlockRetryDispatchChain' 'Accepted' 'retry_dispatch_chain_closed'
            $retryClosureCompleted = "event=filetransfer_coordinator_snapshot_stream_completed; $retryPrefix; final_state_generation=2; final_state_hash=retry-after-closure; transition_count=3; emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none"
            $retryClosureRecovered = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($retryStart,$retryTransitionA,$retryEffectA,$closureRejected,$closureTransition,$retryClosureCompleted)
            $retryClosureRecoveredCorrelation = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $retryClosureRecovered -Family 'runtime_unlock_retry_lifecycle'
            if ($null -ne $retryClosureRecovered.FirstUnresolvedBoundary -or
                $retryClosureRecoveredCorrelation.RawCount -ne 1 -or
                $retryClosureRecoveredCorrelation.DistinctCount -ne 1 -or
                $retryClosureRecoveredCorrelation.RecoveredCount -ne 1 -or
                $retryClosureRecoveredCorrelation.UnresolvedCount -ne 0) {
                throw "An exact bounded closure did not recover the rejected successor boundary: $($retryClosureRecovered | ConvertTo-Json -Depth 8 -Compress)"
            }

            $installReceipt = [ordered]@{
                stage='install'; commandIdentity='retry-b:install'; commandGeneration=9L
                commandFingerprint='retry-b-install-command'; receiptIdentity='retry-b:install'
                receiptGeneration=9L; receiptStatus='Settled'; receiptReason='retry_dispatch_install_receipt'
                receiptFingerprint='retry-b-install'; receiptValidated=$true
            }
            $executionReceipt = [ordered]@{
                stage='execution'; commandIdentity='retry-b:execution'; commandGeneration=9L
                commandFingerprint='retry-b-execution-command'; receiptIdentity='retry-b:offer:9'
                receiptGeneration=9L; receiptStatus='Settled'; receiptReason='dispatch_observed'
                receiptFingerprint='retry-b-execution'; receiptValidated=$true
            }
            $settlementReceipt = [ordered]@{
                stage='settlement'; commandIdentity='retry-b:settlement'; commandGeneration=9L
                commandFingerprint='retry-b-settlement-command'; receiptIdentity='retry-b:settlement'
                receiptGeneration=9L; receiptStatus='DispatchObserved'; receiptReason='dispatch_observed'
                receiptFingerprint='retry-b-settlement'; receiptValidated=$true
            }
            $retryB = New-RetryOperation 'retry-b' 'retry-a' 'attempt_terminal' 2L 'Installed' 'Settled' $true @($installReceipt,$executionReceipt,$settlementReceipt)
            $retryTransitionB = New-RetryEnvelopeLine 'retry-transition-b' 1L 2L 'retry-after-a' 'retry-after-b' $retryB 20L
            $retryEffectsB = @(
                (New-RetryEffect 'retry-transition-b' 2L 'retry-b' 2L 'attempt_terminal' 'install_committed' 'retry-b:install' 9L 'Settled' 'retry-b-install'),
                (New-RetryEffect 'retry-transition-b' 2L 'retry-b' 2L 'attempt_terminal' 'execution_observed' 'retry-b:offer:9' 9L 'Settled' 'retry-b-execution'),
                (New-RetryEffect 'retry-transition-b' 2L 'retry-b' 2L 'attempt_terminal' 'settlement_observed' 'retry-b:settlement' 9L 'DispatchObserved' 'retry-b-settlement'))
            $retryCompletedB = "event=filetransfer_coordinator_snapshot_stream_completed; $retryPrefix; final_state_generation=2; final_state_hash=retry-after-b; transition_count=2; emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none"
            $retryFullLines = @('event=unrelated',$retryStart,$retryTransitionA,$retryEffectA,'event=other',$retryTransitionB) + $retryEffectsB + @($retryCompletedB)
            $retryCompactLines = @($retryStart,$retryTransitionA,$retryEffectA,$retryTransitionB) + $retryEffectsB + @($retryCompletedB)
            $retryFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $retryFullLines
            $retryCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $retryCompactLines
            $retryParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $retryFull -Compact $retryCompact
            $retryRecovered = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $retryFull -Family 'runtime_unlock_retry_lifecycle'
            if ($retryFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $retryParity.Verdict -ne 'pass' -or
                $null -ne $retryFull.FirstUnresolvedBoundary -or
                $retryRecovered.RawCount -ne 1 -or
                $retryRecovered.DistinctCount -ne 1 -or
                $retryRecovered.RecoveredCount -ne 1 -or
                $retryRecovered.UnresolvedCount -ne 0) {
                throw "An exact MissingDispatch successor did not recover with full/compact parity: $($retryFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            $receiveScope = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=1L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=2L; bridgeRecoveryGeneration=3L
                checkpointGeneration=4L; checkpointRequestId='root-checkpoint'
                dataSessionIdentity='receive-data-session'; dataSessionGeneration=5L
            }
            $effectiveReceiveScope = [ordered]@{}
            foreach ($entry in $receiveScope.GetEnumerator()) {
                $effectiveReceiveScope[$entry.Key] = $entry.Value
            }
            $effectiveReceiveScope.checkpointGeneration = 6L
            $effectiveReceiveScope.checkpointRequestId = 'child-checkpoint'
            function New-ReceiveRecoveryOperation(
                [string]$role,
                [string]$beforeState,
                [string]$afterState,
                [string]$executionState,
                [string]$closureState,
                [string]$proofIdentity) {
                [ordered]@{
                    family='receive_recovery'; operationRole=$role
                    operationKey="receive_recovery|$role|receive-authority|1|BridgeReceiveStallRecovery"
                    operationKind='BridgeReceiveStallRecovery'
                    authorityId='receive-authority'; predecessorAuthorityId='(none)'
                    operationGeneration=1L; beforeState=$beforeState; afterState=$afterState
                    commandIdentity='receive-command'; commandGeneration=1L
                    commandFingerprint='receive-command-fingerprint'
                    receiptIdentity='(none)'; receiptGeneration=0L
                    receiptStatus='Pending'; receiptFingerprint='(none)'
                    receiptValidated=$false; stateApplied=$true
                    routeRuntime=$null; routeTransition=$null; runtimeUnlockRetry=$null
                    receiveRecovery=[ordered]@{
                        role=$role; sourceBoundary=$receiveScope
                        effectiveBoundary=$effectiveReceiveScope
                        recoveryKind='BridgeReceiveStallRecovery'
                        bridgeDecisionGeneration=3L
                        recoveryLivenessLeaseGeneration=4L
                        executionState=$executionState; closureState=$closureState
                        closureGeneration=if ($closureState -eq 'Adopted') { 1L } else { 0L }
                        proofIdentity=$proofIdentity
                        proofObservedUtcMs=if ($proofIdentity -eq '(none)') { 0L } else { 100L }
                        heldRetryAuthorityId='(none)'; reservedRetryAuthorityId='(none)'
                        initialActivationHoldGeneration=0L; envelopeRefreshGeneration=0L
                        checkpointBindingGeneration=6L; checkpointDeliveryGeneration=0L
                        checkpointResumeGeneration=0L; checkpointFrontierHoldGeneration=0L
                        checkpointFrontierRecheckGeneration=0L; receipts=@()
                    }
                }
            }
            function New-ReceiveRecoveryEnvelopeLine(
                [string]$transitionId,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [object]$operation,
                [long]$decisionTime,
                [string]$decisionKind,
                [string]$decisionStatus,
                [string]$decisionReason) {
                $aggregate = [ordered]@{
                    beforeStateGeneration=$beforeGeneration
                    afterStateGeneration=$afterGeneration
                    beforeStateFingerprint=$beforeHash; afterStateFingerprint=$afterHash
                    beforeBoundary=$receiveScope; afterBoundary=$receiveScope
                    changedFamilies=@('receive_recovery'); operations=@($operation)
                }
                $envelope = [ordered]@{
                    snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='coordinator-receive'; sessionId='session-receive'
                    transferId='transfer-receive'; direction='inbound'
                    decisionUtcMilliseconds=$decisionTime; transitionId=$transitionId
                    decisionKind=$decisionKind; decisionStatus=$decisionStatus
                    decisionReason=$decisionReason; aggregate=$aggregate
                }
                $json = $envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $receivePrefix; transition_id=$transitionId; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            $receivePrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-receive; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $receiveStart = "event=filetransfer_coordinator_snapshot_stream_started; $receivePrefix; initial_state_generation=0; initial_state_hash=receive-before; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $receiveIssuedOperation = New-ReceiveRecoveryOperation 'operation_current' 'None' 'Issued:None' 'Issued' 'None' '(none)'
            $receiveIssued = New-ReceiveRecoveryEnvelopeLine 'receive-issued' 0L 1L 'receive-before' 'receive-issued-hash' $receiveIssuedOperation 30L 'IssueReceiveRecoveryOperation' 'Accepted' 'filetransfer_receive_recovery_operation_issued'
            $receiveIncompleteEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $receivePrefix; final_state_generation=1; final_state_hash=receive-issued-hash; transition_count=1; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $receiveIncomplete = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($receiveStart,$receiveIssued,$receiveIncompleteEnd)
            if ($receiveIncomplete.ArtifactEvidenceVerdict -ne 'pass' -or
                $receiveIncomplete.FirstUnresolvedBoundary.taxonomyClass -ne 'filetransfer_receive_recovery_operation_incomplete' -or
                $receiveIncomplete.ReceiveRecoveryFamilyLegacySelectionCount -ne 0 -or
                $receiveIncomplete.LegacySelectionCount -ne 6) {
                throw "An incomplete receive-recovery operation was not selected exactly: $($receiveIncomplete | ConvertTo-Json -Depth 8 -Compress)"
            }

            $receiveTerminalOperation = New-ReceiveRecoveryOperation 'operation_terminal' 'Issued:None' 'Completed:Adopted' 'Completed' 'Adopted' 'receive-proof-1'
            $receiveProofReceipt = [ordered]@{
                stage='operation'; commandIdentity='receive-command'
                commandGeneration=1L; commandFingerprint='receive-command-fingerprint'
                receiptIdentity='receive-authority:operation:receipt'
                receiptGeneration=1L; receiptStatus='Adopted'
                receiptReason='proof_adopted'; receiptFingerprint='receive-proof-fingerprint'
                receiptValidated=$true
            }
            $receiveTerminalOperation.receiptIdentity = $receiveProofReceipt.receiptIdentity
            $receiveTerminalOperation.receiptGeneration = 1L
            $receiveTerminalOperation.receiptStatus = 'Adopted'
            $receiveTerminalOperation.receiptFingerprint = 'receive-proof-fingerprint'
            $receiveTerminalOperation.receiptValidated = $true
            $receiveTerminalOperation.receiveRecovery.receipts = @($receiveProofReceipt)
            $receiveCompleted = New-ReceiveRecoveryEnvelopeLine 'receive-completed' 1L 2L 'receive-issued-hash' 'receive-completed-hash' $receiveTerminalOperation 31L 'ObserveReceiveRecoveryProof' 'Accepted' 'filetransfer_receive_recovery_proof_adopted'
            $receiveProofEffect = "event=filetransfer_coordinator_authority_effect; $receivePrefix; transition_id=receive-completed; coordinator_state_generation=2; operation_family=receive_recovery; operation_key=$($receiveTerminalOperation.operationKey); operation_authority_id=receive-authority; operation_generation=1; effect_kind=receive_proof_observed; effect_identity=receive-proof-effect; receipt_identity=$($receiveProofReceipt.receiptIdentity); receipt_generation=1; receipt_status=Adopted; receipt_fingerprint=receive-proof-fingerprint; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=4; checkpoint_request_id=root-checkpoint; data_session_identity=receive-data-session; data_session_generation=5"
            $receiveCompletedEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $receivePrefix; final_state_generation=2; final_state_hash=receive-completed-hash; transition_count=2; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $receiveFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=irrelevant',$receiveStart,$receiveIssued,'event=other',$receiveCompleted,$receiveProofEffect,$receiveCompletedEnd)
            $receiveCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($receiveStart,$receiveIssued,$receiveCompleted,$receiveProofEffect,$receiveCompletedEnd)
            $receiveParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $receiveFull -Compact $receiveCompact
            if ($receiveFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $receiveParity.Verdict -ne 'pass' -or
                $null -ne $receiveFull.FirstUnresolvedBoundary -or
                $receiveFull.BoundaryHash -ne $receiveCompact.BoundaryHash) {
                throw "Receive-recovery full/compact parity failed: $($receiveFull | ConvertTo-Json -Depth 8 -Compress)"
            }
            $unknownReceiveRole = New-ReceiveRecoveryOperation 'unknown_role' 'None' 'Issued:None' 'Issued' 'None' '(none)'
            $unknownReceive = New-ReceiveRecoveryEnvelopeLine 'receive-unknown' 0L 1L 'receive-before' 'receive-issued-hash' $unknownReceiveRole 30L 'IssueReceiveRecoveryOperation' 'Accepted' 'filetransfer_receive_recovery_operation_issued'
            if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($receiveStart,$unknownReceive,$receiveIncompleteEnd)).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'An unknown receive-recovery role did not fail artifact evidence.'
            }

            $pressureHandoffOperation = New-ReceiveRecoveryOperation 'operation_current' 'None' 'Issued:None' 'Issued' 'None' '(none)'
            $pressureHandoffTransition = New-ReceiveRecoveryEnvelopeLine `
                'receive-pressure-handoff' 0L 1L 'receive-before' `
                'receive-issued-hash' $pressureHandoffOperation 32L `
                'CommitRegularV4FrontierRepairPressureRecoveryHandoff' `
                'Committed' `
                'regular_v4_frontier_repair_pressure_recovery_handoff_committed'
            $pressureHandoffFull = Get-FileTransferCoordinatorSnapshotEvaluation `
                -Lines @('event=irrelevant',$receiveStart,'event=other',$pressureHandoffTransition,$receiveIncompleteEnd)
            $pressureHandoffCompact = Get-FileTransferCoordinatorSnapshotEvaluation `
                -Lines @($receiveStart,$pressureHandoffTransition,$receiveIncompleteEnd)
            $pressureHandoffParity = Compare-FileTransferCoordinatorSnapshotEvaluations `
                -Full $pressureHandoffFull `
                -Compact $pressureHandoffCompact
            if ($pressureHandoffFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $pressureHandoffCompact.ArtifactEvidenceVerdict -ne 'pass' -or
                $pressureHandoffParity.Verdict -ne 'pass' -or
                $pressureHandoffFull.BoundaryHash -cne
                    $pressureHandoffCompact.BoundaryHash -or
                $pressureHandoffFull.FirstUnresolvedBoundary.taxonomyClass -ne
                    'filetransfer_receive_recovery_operation_incomplete') {
                throw "An exact Family 5/3 pressure-recovery handoff did not validate with full/compact parity: $($pressureHandoffFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            foreach ($unrelatedFamily5Decision in @(
                    'StageRegularV4FrontierRepairPressureBinding',
                    'CommitRegularV4FrontierRepairPressureBinding',
                    'PrepareRegularV4FrontierRepairPressureSettlement',
                    'CommitRegularV4FrontierRepairPressureSettlement',
                    'PrepareRegularV4FrontierRepairPressureRecoveryHandoff')) {
                $unrelatedTransition = New-ReceiveRecoveryEnvelopeLine `
                    "receive-unrelated-$unrelatedFamily5Decision" 0L 1L `
                    'receive-before' 'receive-issued-hash' `
                    (New-ReceiveRecoveryOperation 'operation_current' 'None' 'Issued:None' 'Issued' 'None' '(none)') `
                    32L $unrelatedFamily5Decision 'Accepted' 'unrelated_family5_decision'
                $unrelatedEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation `
                    -Lines @($receiveStart,$unrelatedTransition,$receiveIncompleteEnd)
                if ($unrelatedEvaluation.ArtifactEvidenceVerdict -ne 'fail' -or
                    $unrelatedEvaluation.FirstUnresolvedBoundary.decisionReason -ne
                        'snapshot_operation_catalog_mismatch') {
                    throw "An unrelated Family 5 decision admitted a Family 3 mutation: $unrelatedFamily5Decision"
                }
            }

            $mutatedPressureHandoffOperation = New-ReceiveRecoveryOperation 'operation_current' 'None' 'Issued:None' 'Issued' 'None' '(none)'
            $mutatedPressureHandoffOperation.operationKey =
                'receive_recovery|operation_current|wrong-authority|1|BridgeReceiveStallRecovery'
            $mutatedPressureHandoffTransition = New-ReceiveRecoveryEnvelopeLine `
                'receive-pressure-handoff-mutated' 0L 1L 'receive-before' `
                'receive-issued-hash' $mutatedPressureHandoffOperation 32L `
                'CommitRegularV4FrontierRepairPressureRecoveryHandoff' `
                'Committed' `
                'regular_v4_frontier_repair_pressure_recovery_handoff_committed'
            if ((Get-FileTransferCoordinatorSnapshotEvaluation `
                    -Lines @($receiveStart,$mutatedPressureHandoffTransition,$receiveIncompleteEnd)).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'A mutated Family 3 operation key under the exact pressure handoff did not fail closed.'
            }

            $incompletePressureHandoffOperation = New-ReceiveRecoveryOperation 'operation_current' 'None' 'Issued:None' 'Issued' 'None' '(none)'
            $incompletePressureHandoffOperation.receiveRecovery.sourceBoundary =
                [ordered]@{ route='regular_nkn_v4_fast' }
            $incompletePressureHandoffTransition = New-ReceiveRecoveryEnvelopeLine `
                'receive-pressure-handoff-incomplete' 0L 1L 'receive-before' `
                'receive-issued-hash' $incompletePressureHandoffOperation 32L `
                'CommitRegularV4FrontierRepairPressureRecoveryHandoff' `
                'Committed' `
                'regular_v4_frontier_repair_pressure_recovery_handoff_committed'
            if ((Get-FileTransferCoordinatorSnapshotEvaluation `
                    -Lines @($receiveStart,$incompletePressureHandoffTransition,$receiveIncompleteEnd)).ArtifactEvidenceVerdict -ne 'fail') {
                throw 'An incomplete Family 3 scope under the exact pressure handoff did not fail closed.'
            }

            function New-FallbackTailReceipt([string]$stage) {
                [ordered]@{
                    stage=$stage; commandIdentity="fallback-$stage-command"
                    commandGeneration=1L; commandFingerprint="fallback-$stage-command-fingerprint"
                    receiptIdentity="fallback-$stage-receipt"; receiptGeneration=1L
                    receiptStatus='Accepted'; receiptReason='accepted'
                    receiptFingerprint="fallback-$stage-receipt-fingerprint"
                    receiptValidated=$true
                }
            }
            function New-FallbackTailOperation([object[]]$receipts) {
                [ordered]@{
                    family='fallback_tail_yield'; operationRole='yield_current'
                    operationKey='fallback_tail_yield|yield_current|fta2-fallback_tail_yield-snapshot|1|Quiescent'
                    operationKind='Quiescent'; authorityId='fta2-fallback_tail_yield-snapshot'
                    predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState='YieldIssued'; afterState='Paired'
                    commandIdentity='fallback-tail-command'; commandGeneration=1L
                    commandFingerprint='fallback-tail-command-fingerprint'
                    receiptIdentity='fallback-tail-receipt'; receiptGeneration=1L
                    receiptStatus='Accepted'; receiptFingerprint='fallback-tail-receipt-fingerprint'
                    receiptValidated=$false; stateApplied=$true
                    fallbackTailYield=[ordered]@{
                        role='yield_current'; sourceBoundary=$receiveScope
                        effectiveBoundary=$receiveScope; targetBoundary=$receiveScope
                        yieldKind='Quiescent'; pairingState='Paired'
                        ownerKey='owner-key'; pairKey='pair-key'
                        localProofIdentity='local-proof'; localProofFingerprint='local-fingerprint'
                        peerProofAuthorityId='peer-authority'; peerProofGeneration=1L
                        peerProofIdentity='peer-proof'; peerProofFingerprint='peer-fingerprint'
                        pairedReleaseAuthorityId='fta2-fallback_tail_yield-snapshot'
                        pairedReleaseGeneration=1L; pairingGeneration=1L
                        failedPredecessorAuthorityId='(none)'; failedPredecessorGeneration=0L
                        failedSupersessionCarriedPeerProof=$false
                        reauthorizationGeneration=0L; reauthorizationSourceTransportEpoch=0L
                        reauthorizationTargetTransportEpoch=0L
                        reservationGeneration=0L; consumingInstallAuthorityId='(none)'
                        consumingInstallGeneration=0L; retryHoldGeneration=0L
                        retryRecheckGeneration=0L; invalidationReason='(none)'
                        receipts=$receipts
                    }
                }
            }
            function New-FallbackTailEnvelopeLine([object]$operation) {
                $aggregate = [ordered]@{
                    beforeStateGeneration=0L; afterStateGeneration=1L
                    beforeStateFingerprint='fallback-before'; afterStateFingerprint='fallback-after'
                    beforeBoundary=$receiveScope; afterBoundary=$receiveScope
                    changedFamilies=@('fallback_tail_yield'); operations=@($operation)
                }
                $envelope = [ordered]@{
                    snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='coordinator-fallback'; sessionId='session-receive'
                    transferId='transfer-receive'; direction='inbound'
                    decisionUtcMilliseconds=40L; transitionId='fallback-paired'
                    decisionKind='ApplyFallbackTailPeerProofReceipt'; decisionStatus='Accepted'
                    decisionReason='fallback_tail_peer_proof_receipt_applied'; aggregate=$aggregate
                }
                $json = $envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $fallbackPrefix; transition_id=fallback-paired; before_state_generation=0; after_state_generation=1; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            $fallbackPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-fallback; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $fallbackCoverage = 'route_runtime,route_transition,runtime_unlock_retry_lifecycle,receive_recovery,fallback_tail_yield'
            $fallbackStart = "event=filetransfer_coordinator_snapshot_stream_started; $fallbackPrefix; initial_state_generation=0; initial_state_hash=fallback-before; emitted_family_coverage=$fallbackCoverage; shadow_family_coverage=; authoritative_family_coverage=$fallbackCoverage; authority_effect=none"
            $fallbackEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $fallbackPrefix; final_state_generation=1; final_state_hash=fallback-after; transition_count=1; emitted_family_coverage=$fallbackCoverage; shadow_family_coverage=; authoritative_family_coverage=$fallbackCoverage; authority_effect=none"
            function New-FallbackTailEffect([string]$stage, [string]$kind) {
                "event=filetransfer_coordinator_authority_effect; $fallbackPrefix; transition_id=fallback-paired; coordinator_state_generation=1; operation_family=fallback_tail_yield; operation_key=fallback_tail_yield|yield_current|fta2-fallback_tail_yield-snapshot|1|Quiescent; operation_authority_id=fta2-fallback_tail_yield-snapshot; operation_generation=1; effect_kind=$kind; effect_identity=fallback-$kind-effect; effect_generation=1; receipt_identity=fallback-$stage-receipt; receipt_generation=1; receipt_status=Accepted; receipt_fingerprint=fallback-$stage-receipt-fingerprint; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=4; checkpoint_request_id=root-checkpoint; data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied"
            }
            $invalidFallbackTransition = New-FallbackTailEnvelopeLine (New-FallbackTailOperation @())
            $invalidFallback = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($fallbackStart,$invalidFallbackTransition,$fallbackEnd)
            if ($invalidFallback.ArtifactEvidenceVerdict -ne 'pass' -or
                $invalidFallback.FirstUnresolvedBoundary.taxonomyClass -ne 'fallback_tail_yield_authority_invalid' -or
                $invalidFallback.FallbackTailYieldFamilyLegacySelectionCount -ne 0 -or
                $invalidFallback.LegacySelectionCount -ne 2) {
                throw "An invalid paired fallback-tail boundary was not selected exactly: $($invalidFallback | ConvertTo-Json -Depth 8 -Compress)"
            }
            $validFallbackTransition = New-FallbackTailEnvelopeLine (New-FallbackTailOperation @(
                (New-FallbackTailReceipt 'yield'),
                (New-FallbackTailReceipt 'local_proof'),
                (New-FallbackTailReceipt 'peer_proof'),
                (New-FallbackTailReceipt 'paired_release')))
            $fallbackEffects = @(
                (New-FallbackTailEffect 'yield' 'recovery_owner_retired_after_yield'),
                (New-FallbackTailEffect 'local_proof' 'local_proof_sent'),
                (New-FallbackTailEffect 'local_proof' 'local_proof_applied'),
                (New-FallbackTailEffect 'peer_proof' 'peer_proof_observed'),
                (New-FallbackTailEffect 'paired_release' 'paired_release_published'))
            $fallbackFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=irrelevant',$fallbackStart,$validFallbackTransition) + $fallbackEffects + @($fallbackEnd))
            $fallbackCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@($fallbackStart,$validFallbackTransition) + $fallbackEffects + @($fallbackEnd))
            $fallbackParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $fallbackFull -Compact $fallbackCompact
            if ($fallbackFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $fallbackFull.FirstUnresolvedBoundary -or
                $fallbackParity.Verdict -ne 'pass' -or
                $fallbackFull.BoundaryHash -ne $fallbackCompact.BoundaryHash) {
                throw "Fallback-tail full/compact parity failed: $($fallbackFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            $pressureAuthority = 'fta2-repair_replay-pressure-snapshot'
            $pressureOperationKey = "regular_v4_frontier_repair_pressure|pressure_binding_pending|$pressureAuthority|1|V4Repair"
            $pressureReceipt = [ordered]@{
                stage='attempted_decision'; commandIdentity='pressure-stage-command'
                commandGeneration=1L; commandFingerprint='pressure-stage-command-fingerprint'
                receiptIdentity='pressure-stage-receipt'; receiptGeneration=1L
                receiptStatus='Accepted'; receiptReason='pressure_staged'
                receiptFingerprint='pressure-stage-receipt-fingerprint'
                receiptValidated=$true
            }
            $pressureOperation = [ordered]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='pressure_binding_pending'
                operationKey=$pressureOperationKey; operationKind='V4Repair'
                authorityId=$pressureAuthority; predecessorAuthorityId='(none)'
                operationGeneration=1L; beforeState='None'; afterState='Pending'
                commandIdentity=$pressureReceipt.commandIdentity
                commandGeneration=1L; commandFingerprint=$pressureReceipt.commandFingerprint
                receiptIdentity=$pressureReceipt.receiptIdentity
                receiptGeneration=1L; receiptStatus='Accepted'
                receiptFingerprint=$pressureReceipt.receiptFingerprint
                receiptValidated=$true; stateApplied=$true
                regularV4FrontierRepairPressure=[ordered]@{
                    role='pressure_binding_pending'; sourceBoundary=$receiveScope
                    effectiveBoundary=$receiveScope; repairKind='V4Repair'
                    repairKey='regular-v4-pressure:snapshot'; frontierChunkIndex=12L
                    durableReceivedHighestChunkIndex=13L; pendingRepairCount=2
                    targetTransport='RegularNkn'; pressureReason='frontier_due'
                    decisionGeneration=1L; pressureInstallGeneration=1L
                    bindingState='Pending'; settlementOutcome='(none)'
                    settlementReason='(none)'; currentAuthorityCleared=$false
                    retainedPredecessorCleared=$false
                    successorAuthorityId='(none)'; recoveryAuthorityId='(none)'
                    recoveryOperationGeneration=0L; recoveryKind='(none)'
                    receipts=@($pressureReceipt)
                }
            }
            $pressureAggregate = [ordered]@{
                beforeStateGeneration=0L; afterStateGeneration=1L
                beforeStateFingerprint='pressure-before'; afterStateFingerprint='pressure-after'
                beforeBoundary=$receiveScope; afterBoundary=$receiveScope
                changedFamilies=@('regular_v4_frontier_repair_pressure')
                operations=@($pressureOperation)
            }
            $pressureEnvelope = [ordered]@{
                snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                coordinatorInstanceId='coordinator-pressure'; sessionId='session-receive'
                transferId='transfer-receive'; direction='inbound'
                decisionUtcMilliseconds=50L; transitionId='pressure-stage'
                decisionKind='StageRegularV4FrontierRepairPressureBinding'
                decisionStatus='Accepted'; decisionReason='pressure_staged'
                aggregate=$pressureAggregate
            }
            $pressureJson = $pressureEnvelope | ConvertTo-Json -Depth 32 -Compress
            $pressureBytes = [Text.UTF8Encoding]::new($false).GetBytes($pressureJson)
            $pressureHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $pressureBytes
            $pressurePayload = [Convert]::ToBase64String($pressureBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $pressurePrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-pressure; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $pressureCoverage = 'route_runtime,route_transition,runtime_unlock_retry_lifecycle,receive_recovery,fallback_tail_yield,regular_v4_frontier_repair_pressure'
            $pressureStart = "event=filetransfer_coordinator_snapshot_stream_started; $pressurePrefix; initial_state_generation=0; initial_state_hash=pressure-before; emitted_family_coverage=$pressureCoverage; shadow_family_coverage=; authoritative_family_coverage=$pressureCoverage; authority_effect=none"
            $pressureTransition = "event=filetransfer_coordinator_transition_snapshot; $pressurePrefix; transition_id=pressure-stage; before_state_generation=0; after_state_generation=1; snapshot_sha256=$pressureHash; snapshot_payload=$pressurePayload; authority_effect=none"
            $pressureEffect = "event=filetransfer_coordinator_authority_effect; $pressurePrefix; transition_id=pressure-stage; coordinator_state_generation=1; operation_family=regular_v4_frontier_repair_pressure; operation_key=$pressureOperationKey; operation_authority_id=$pressureAuthority; operation_generation=1; effect_kind=pressure_install_staged; effect_identity=pressure-stage-effect; effect_generation=1; receipt_identity=$($pressureReceipt.receiptIdentity); receipt_generation=1; receipt_status=Accepted; receipt_fingerprint=$($pressureReceipt.receiptFingerprint); route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=4; checkpoint_request_id=root-checkpoint; data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied"
            $pressureEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $pressurePrefix; final_state_generation=1; final_state_hash=pressure-after; transition_count=1; emitted_family_coverage=$pressureCoverage; shadow_family_coverage=; authoritative_family_coverage=$pressureCoverage; authority_effect=none"
            $pressureFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=irrelevant',$pressureStart,$pressureTransition,$pressureEffect,$pressureEnd)
            $pressureCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($pressureStart,$pressureTransition,$pressureEffect,$pressureEnd)
            $pressureParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $pressureFull -Compact $pressureCompact
            if ($pressureFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $pressureFull.FirstUnresolvedBoundary.taxonomyClass -ne 'regular_v4_frontier_repair_initial_pressure_install_missing' -or
                $pressureFull.RegularV4FrontierRepairPressureFamilyLegacySelectionCount -ne 0 -or
                $pressureFull.LegacySelectionCount -ne 1 -or
                $pressureParity.Verdict -ne 'pass' -or
                $pressureFull.BoundaryHash -ne $pressureCompact.BoundaryHash) {
                throw "Regular-V4 pressure full/compact parity failed: $($pressureFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            function New-RepairReplayPayload([string]$role, [object[]]$receipts) {
                [ordered]@{
                    role=$role; sourceBoundary=$receiveScope
                    effectiveBoundary=$receiveScope; repairKind='V4Repair'
                    repairKey='(none)'; frontierChunkIndex=0L
                    durableReceivedHighestChunkIndex=-1L; pendingRepairCount=0
                    targetTransport='RegularNkn'; pressureReason='repair_authorized'
                    decisionGeneration=1L; pressureInstallGeneration=0L
                    bindingState='Authorized'; settlementOutcome='(none)'
                    settlementReason='(none)'; currentAuthorityCleared=$false
                    retainedPredecessorCleared=$false
                    successorAuthorityId='(none)'; recoveryAuthorityId='(none)'
                    recoveryOperationGeneration=0L; recoveryKind='(none)'
                    receipts=$receipts
                }
            }
            $repairAuthority = 'fta2-repair_replay-authority-snapshot'
            $repairCurrent = [ordered]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_current'
                operationKey="regular_v4_frontier_repair_pressure|repair_authority_current|$repairAuthority|1|V4Repair"
                operationKind='V4Repair'; authorityId=$repairAuthority
                predecessorAuthorityId='(none)'; operationGeneration=1L
                beforeState='None'; afterState='Authorized'
                commandIdentity=$repairAuthority; commandGeneration=1L
                commandFingerprint='repair-authority-command'
                receiptIdentity='(none)'; receiptGeneration=0L
                receiptStatus='(none)'; receiptFingerprint='(none)'
                receiptValidated=$false; stateApplied=$true
                regularV4FrontierRepairPressure=(New-RepairReplayPayload `
                    'repair_authority_current' @())
            }
            $repairAttemptReceipt = [ordered]@{
                stage='attempted_decision'; commandIdentity="$repairAuthority`:RepairReplay"
                commandGeneration=1L; commandFingerprint='repair-attempt-command'
                receiptIdentity="$repairAuthority`:RepairReplay:decision"
                receiptGeneration=1L; receiptStatus='Accepted'
                receiptReason='accepted'; receiptFingerprint='repair-attempt-receipt'
                receiptValidated=$true
            }
            $repairAttempt = [ordered]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='attempted_decision'
                operationKey="regular_v4_frontier_repair_pressure|attempted_decision|$repairAuthority|1|RepairReplay"
                operationKind='RepairReplay'; authorityId=$repairAuthority
                predecessorAuthorityId='(none)'; operationGeneration=1L
                beforeState='Authorized'; afterState='Authorized'
                commandIdentity=$repairAttemptReceipt.commandIdentity
                commandGeneration=1L; commandFingerprint=$repairAttemptReceipt.commandFingerprint
                receiptIdentity=$repairAttemptReceipt.receiptIdentity
                receiptGeneration=1L; receiptStatus='Accepted'
                receiptFingerprint=$repairAttemptReceipt.receiptFingerprint
                receiptValidated=$true; stateApplied=$false
                regularV4FrontierRepairPressure=(New-RepairReplayPayload `
                    'attempted_decision' @($repairAttemptReceipt))
            }
            $repairAggregate = [ordered]@{
                beforeStateGeneration=0L; afterStateGeneration=1L
                beforeStateFingerprint='repair-before'; afterStateFingerprint='repair-after'
                beforeBoundary=$receiveScope; afterBoundary=$receiveScope
                changedFamilies=@('regular_v4_frontier_repair_pressure')
                operations=@($repairCurrent,$repairAttempt)
            }
            $repairEnvelope = [ordered]@{
                snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Shadow'
                coordinatorInstanceId='coordinator-repair'; sessionId='session-receive'
                transferId='transfer-receive'; direction='inbound'
                decisionUtcMilliseconds=45L; transitionId='repair-authorized'
                decisionKind='RepairReplay'; decisionStatus='Accepted'
                decisionReason='accepted'; aggregate=$repairAggregate
            }
            $repairJson = $repairEnvelope | ConvertTo-Json -Depth 32 -Compress
            $repairBytes = [Text.UTF8Encoding]::new($false).GetBytes($repairJson)
            $repairHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $repairBytes
            $repairPayload = [Convert]::ToBase64String($repairBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $repairPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-repair; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $repairStart = "event=filetransfer_coordinator_snapshot_stream_started; $repairPrefix; initial_state_generation=0; initial_state_hash=repair-before; emitted_family_coverage=regular_v4_frontier_repair_pressure; shadow_family_coverage=; authoritative_family_coverage=regular_v4_frontier_repair_pressure; authority_effect=none"
            $repairTransition = "event=filetransfer_coordinator_transition_snapshot; $repairPrefix; transition_id=repair-authorized; before_state_generation=0; after_state_generation=1; snapshot_sha256=$repairHash; snapshot_payload=$repairPayload; authority_effect=none"
            $repairEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $repairPrefix; final_state_generation=1; final_state_hash=repair-after; transition_count=1; emitted_family_coverage=regular_v4_frontier_repair_pressure; shadow_family_coverage=; authoritative_family_coverage=regular_v4_frontier_repair_pressure; authority_effect=none"
            $repairFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=irrelevant',$repairStart,$repairTransition,$repairEnd)
            $repairCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($repairStart,$repairTransition,$repairEnd)
            $repairParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $repairFull -Compact $repairCompact
            if ($repairFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $repairFull.FirstUnresolvedBoundary -ne $null -or
                $repairParity.Verdict -ne 'pass' -or
                $repairFull.BoundaryHash -ne $repairCompact.BoundaryHash) {
                throw "RepairReplay authorization full/compact parity failed: $($repairFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            $malformedRepairAggregate = [ordered]@{}
            foreach ($entry in $repairAggregate.GetEnumerator()) {
                $malformedRepairAggregate[$entry.Key] = $entry.Value
            }
            $malformedRepairAggregate.operations = @()
            $malformedRepairAggregate.changedFamilies = @()
            $malformedRepairEnvelope = [ordered]@{}
            foreach ($entry in $repairEnvelope.GetEnumerator()) {
                $malformedRepairEnvelope[$entry.Key] = $entry.Value
            }
            $malformedRepairEnvelope.aggregate = $malformedRepairAggregate
            $malformedRepairJson = $malformedRepairEnvelope | ConvertTo-Json -Depth 32 -Compress
            $malformedRepairBytes = [Text.UTF8Encoding]::new($false).GetBytes($malformedRepairJson)
            $malformedRepairHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $malformedRepairBytes
            $malformedRepairPayload = [Convert]::ToBase64String($malformedRepairBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $malformedRepairTransition = "event=filetransfer_coordinator_transition_snapshot; $repairPrefix; transition_id=repair-authorized; before_state_generation=0; after_state_generation=1; snapshot_sha256=$malformedRepairHash; snapshot_payload=$malformedRepairPayload; authority_effect=none"
            $malformedRepair = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($repairStart,$malformedRepairTransition,$repairEnd)
            if ($malformedRepair.FirstUnresolvedBoundary.decisionReason -ne
                'snapshot_regular_v4_repair_replay_authority_transition_missing') {
                throw "Malformed RepairReplay authorization did not fail closed: $($malformedRepair | ConvertTo-Json -Depth 8 -Compress)"
            }

            $settlementPayload = New-RepairReplayPayload `
                'pressure_settlement_terminal' @()
            $settlementPayload.bindingState = 'Settled'
            $settlementPayload.settlementOutcome = 'ProgressObserved'
            $settlementPayload.currentAuthorityCleared = $true
            $settlementOperation = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='pressure_settlement_terminal'
                operationKind='PressureSettlement'; authorityId=$repairAuthority
                operationGeneration=1L; beforeState='None'; afterState='Applied'
                receiptIdentity='settlement-a-receipt'; receiptGeneration=1L
                receiptStatus='Applied'; receiptFingerprint='settlement-a-fingerprint'
                stateApplied=$true
                receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementPayload
            }
            $settlementMirrorPayload = New-RepairReplayPayload `
                'repair_authority_terminal' @()
            $settlementMirrorPayload.bindingState = 'Settled'
            $settlementMirrorPayload.settlementOutcome = 'ProgressObserved'
            $settlementMirrorPayload.currentAuthorityCleared = $true
            $settlementMirror = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_terminal'
                operationKind='PressureSettlement'; authorityId=$repairAuthority
                operationGeneration=1L; beforeState='None'; afterState='Applied'
                receiptIdentity='settlement-a-receipt'; receiptGeneration=1L
                receiptStatus='Applied'; receiptFingerprint='settlement-a-fingerprint'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementMirrorPayload
            }
            $retirementPayload = New-RepairReplayPayload `
                'repair_authority_current' @()
            $retirementOperation = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_current'
                operationKind='V4Repair'; authorityId=$repairAuthority
                operationGeneration=1L; beforeState='Authorized'
                afterState='None'; stateApplied=$true
                regularV4FrontierRepairPressure=$retirementPayload
            }
            $settlementAttemptPayload = New-RepairReplayPayload `
                'attempted_decision' @()
            $settlementAttempt = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='attempted_decision'
                operationKind='CommitRegularV4FrontierRepairPressureSettlement'
                authorityId=$repairAuthority; operationGeneration=1L
                beforeState='Installed'; afterState='Installed'
                receiptStatus='Accepted'; stateApplied=$false
                receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementAttemptPayload
            }
            $settlementEnvelope = [pscustomobject]@{
                decisionKind='CommitRegularV4FrontierRepairPressureSettlement'
                decisionStatus='Accepted'
                aggregate=[pscustomobject]@{
                    operations=@(
                        $settlementOperation,
                        $settlementMirror,
                        $retirementOperation,
                        $settlementAttempt)
                }
            }
            $settlementEnvelope = $settlementEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $settlementValidation =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $settlementEnvelope
            if ($settlementValidation -ne '') {
                throw "Exact pressure settlement authority retirement was rejected: $settlementValidation"
            }

            $retainedAuthority = 'fta2-repair_replay-retained-a'
            $currentAuthority = 'fta2-repair_replay-current-b'
            $retainedSettlementPayload = New-RepairReplayPayload `
                'pressure_settlement_terminal' @()
            $retainedSettlementPayload.decisionGeneration = 15L
            $retainedSettlementPayload.pressureInstallGeneration = 16L
            $retainedSettlementPayload.bindingState = 'Settled'
            $retainedSettlementPayload.settlementOutcome = 'Superseded'
            $retainedSettlementPayload.currentAuthorityCleared = $false
            $retainedSettlementPayload.retainedPredecessorCleared = $true
            $retainedSettlementPayload.successorAuthorityId = $currentAuthority
            $retainedSettlement = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='pressure_settlement_terminal'
                operationKind='PressureSettlement'; authorityId=$retainedAuthority
                predecessorAuthorityId='(none)'; operationGeneration=15L
                beforeState='None'; afterState='Applied'
                receiptIdentity='retained-settlement-receipt'
                receiptGeneration=16L; receiptStatus='Applied'
                receiptFingerprint='retained-settlement-fingerprint'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$retainedSettlementPayload
            }
            $retainedSettlementMirrorPayload = $retainedSettlementPayload |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $retainedSettlementMirrorPayload.role = 'repair_authority_terminal'
            $retainedSettlementMirror = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_terminal'
                operationKind='PressureSettlement'; authorityId=$retainedAuthority
                predecessorAuthorityId='(none)'; operationGeneration=15L
                beforeState='None'; afterState='Applied'
                receiptIdentity='retained-settlement-receipt'
                receiptGeneration=16L; receiptStatus='Applied'
                receiptFingerprint='retained-settlement-fingerprint'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$retainedSettlementMirrorPayload
            }
            $retainedBindingPayload = New-RepairReplayPayload `
                'pressure_binding_retained_predecessor' @()
            $retainedBindingPayload.decisionGeneration = 15L
            $retainedBindingPayload.pressureInstallGeneration = 15L
            $retainedBindingPayload.bindingState = 'RetainedPredecessor'
            $retainedBinding = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='pressure_binding_retained_predecessor'
                operationKind='regular_v4_frontier_repair_request'
                authorityId=$retainedAuthority
                predecessorAuthorityId='(none)'; operationGeneration=15L
                beforeState='RetainedPredecessor'; afterState='None'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$retainedBindingPayload
            }
            $currentAttemptPayload = New-RepairReplayPayload `
                'attempted_decision' @()
            $currentAttemptPayload.decisionGeneration = 16L
            $currentAttemptPayload.pressureInstallGeneration = 16L
            $currentAttempt = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='attempted_decision'
                operationKind='CommitRegularV4FrontierRepairPressureSettlement'
                authorityId=$currentAuthority
                predecessorAuthorityId=$retainedAuthority
                operationGeneration=16L; beforeState='Installed'
                afterState='Installed'
                commandFingerprint='retained-settlement-fingerprint'
                commandGeneration=16L; receiptGeneration=16L
                receiptStatus='Accepted'; stateApplied=$false
                receiptValidated=$true
                regularV4FrontierRepairPressure=$currentAttemptPayload
            }
            $retainedSettlementEnvelope = [pscustomobject]@{
                decisionKind='CommitRegularV4FrontierRepairPressureSettlement'
                decisionStatus='Accepted'
                aggregate=[pscustomobject]@{
                    operations=@(
                        $retainedSettlement,
                        $retainedSettlementMirror,
                        $retainedBinding,
                        $currentAttempt)
                }
            }
            $retainedSettlementEnvelope = $retainedSettlementEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $retainedSettlementValidation =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $retainedSettlementEnvelope
            if ($retainedSettlementValidation -ne '') {
                throw "Exact retained-predecessor settlement was rejected: $retainedSettlementValidation"
            }
            $wrongRetainedAttempt = $retainedSettlementEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $wrongRetainedAttempt.aggregate.operations[3].predecessorAuthorityId =
                'fta2-repair_replay-wrong-predecessor'
            $wrongRetainedValidation =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $wrongRetainedAttempt
            if ($wrongRetainedValidation -ne
                'snapshot_regular_v4_pressure_settlement_transition_invalid|pressure_settlement_attempt_binding_invalid') {
                throw "Wrong retained-predecessor attempt did not fail exactly: $wrongRetainedValidation"
            }

            $settlementEnvelope.aggregate.operations = @(
                $settlementOperation,
                $settlementMirror,
                $settlementAttempt)
            $settlementEnvelope = $settlementEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $missingRetirement =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $settlementEnvelope
            if ($missingRetirement -ne
                'snapshot_regular_v4_repair_replay_authority_transition_missing|accepted_pressure_settlement_declared_current_authority_clear_without_repair_authority_retirement') {
                throw "Missing pressure settlement authority retirement did not fail exactly: $missingRetirement"
            }

            $settlementBAuthority = 'fta2-repair_replay-authority-rollover'
            $settlementBPayload = New-RepairReplayPayload `
                'pressure_settlement_terminal' @()
            $settlementBPayload.decisionGeneration = 2L
            $settlementBPayload.bindingState = 'Settled'
            $settlementBPayload.settlementOutcome = 'SettledStalled'
            $settlementBPayload.currentAuthorityCleared = $true
            $settlementB = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='pressure_settlement_terminal'
                operationKind='PressureSettlement'; authorityId=$settlementBAuthority
                operationGeneration=2L; beforeState='None'; afterState='Applied'
                receiptIdentity='settlement-b-receipt'; receiptGeneration=2L
                receiptStatus='Applied'; receiptFingerprint='settlement-b-fingerprint'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementBPayload
            }
            $settlementBMirrorPayload = New-RepairReplayPayload `
                'repair_authority_terminal' @()
            $settlementBMirrorPayload.decisionGeneration = 2L
            $settlementBMirrorPayload.bindingState = 'Settled'
            $settlementBMirrorPayload.settlementOutcome = 'SettledStalled'
            $settlementBMirrorPayload.currentAuthorityCleared = $true
            $settlementBMirror = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_terminal'
                operationKind='PressureSettlement'; authorityId=$settlementBAuthority
                operationGeneration=2L; beforeState='None'; afterState='Applied'
                receiptIdentity='settlement-b-receipt'; receiptGeneration=2L
                receiptStatus='Applied'; receiptFingerprint='settlement-b-fingerprint'
                stateApplied=$true; receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementBMirrorPayload
            }
            $settlementARetired = $settlementOperation.PSObject.Copy()
            $settlementARetired.beforeState = 'Applied'
            $settlementARetired.afterState = 'None'
            $settlementAMirrorRetired = $settlementMirror.PSObject.Copy()
            $settlementAMirrorRetired.beforeState = 'Applied'
            $settlementAMirrorRetired.afterState = 'None'
            $settlementBRetirementPayload = New-RepairReplayPayload `
                'repair_authority_current' @()
            $settlementBRetirementPayload.decisionGeneration = 2L
            $settlementBRetirement = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_current'
                operationKind='V4Repair'; authorityId=$settlementBAuthority
                operationGeneration=2L; beforeState='Authorized'
                afterState='None'; stateApplied=$true
                regularV4FrontierRepairPressure=$settlementBRetirementPayload
            }
            $settlementBAttemptPayload = New-RepairReplayPayload `
                'attempted_decision' @()
            $settlementBAttemptPayload.decisionGeneration = 2L
            $settlementBAttempt = [pscustomobject]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='attempted_decision'
                operationKind='CommitRegularV4FrontierRepairPressureSettlement'
                authorityId=$settlementBAuthority; operationGeneration=2L
                beforeState='Installed'; afterState='Installed'
                receiptStatus='Accepted'; stateApplied=$false
                receiptValidated=$true
                regularV4FrontierRepairPressure=$settlementBAttemptPayload
            }
            $rolloverEnvelope = [pscustomobject]@{
                decisionKind='CommitRegularV4FrontierRepairPressureSettlement'
                decisionStatus='Accepted'
                aggregate=[pscustomobject]@{
                    operations=@(
                        $settlementARetired,
                        $settlementB,
                        $settlementAMirrorRetired,
                        $settlementBMirror,
                        $settlementBRetirement,
                        $settlementBAttempt)
                }
            }
            $rolloverEnvelope = $rolloverEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $rolloverValidation =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $rolloverEnvelope
            if ($rolloverValidation -ne '') {
                throw "Exact pressure settlement terminal rollover was rejected: $rolloverValidation"
            }
            $rolloverWithObservedRepairScope = $rolloverEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $rolloverCurrentRetirement = @(
                $rolloverWithObservedRepairScope.aggregate.operations |
                    Where-Object {
                        $_.operationRole -eq 'repair_authority_current'
                    })[0]
            $rolloverCurrentRetirement.regularV4FrontierRepairPressure.
                sourceBoundary.bridgeRecoveryGeneration = 1L
            $observedRepairScopeValidation =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $rolloverWithObservedRepairScope
            if ($observedRepairScopeValidation -ne '') {
                throw "Settlement rollover rejected the exact current repair identity after an observation-scope advance: $observedRepairScopeValidation"
            }
            $rolloverEnvelope.aggregate.operations = @(
                $settlementARetired,
                $settlementB,
                $settlementAMirrorRetired,
                $settlementBRetirement,
                $settlementBAttempt)
            $rolloverEnvelope = $rolloverEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $missingRolloverMirror =
                Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                    -Envelope $rolloverEnvelope
            if ($missingRolloverMirror -ne
                'snapshot_regular_v4_pressure_settlement_transition_invalid|pressure_settlement_entering_terminal_mirror_invalid') {
                throw "Missing settlement rollover mirror did not fail exactly: $missingRolloverMirror"
            }

            $responderScope = [ordered]@{}
            foreach ($entry in $receiveScope.GetEnumerator()) {
                $responderScope[$entry.Key] = $entry.Value
            }
            $responderScope.route = 'post_tuna_fallback_v6'
            $responderScope.protocolVersion = 6
            $responderScope.liveRouteEpoch = 2L
            $responderScope.routePublicationGeneration = 3L
            $responderScope.runtimeGeneration = 3L
            $responderScope.legGeneration = 3L
            function New-ResponderReceipt([string]$stage, [string]$status) {
                [ordered]@{
                    stage=$stage; commandIdentity="responder-$stage-command"
                    commandGeneration=1L; commandFingerprint="responder-$stage-command-fingerprint"
                    receiptIdentity="responder-$stage-receipt"; receiptGeneration=1L
                    receiptStatus=$status; receiptReason="responder_$stage"
                    receiptFingerprint="responder-$stage-receipt-fingerprint"
                    receiptValidated=$true
                }
            }
            function New-ResponderOperation(
                [string]$role,
                [string]$status,
                [string]$stage,
                [string]$linkedTransitionAuthorityId,
                [long]$linkedTransitionGeneration,
                [bool]$stateApplied) {
                $receipt = New-ResponderReceipt $stage $status
                $authority = 'responder-handoff-1'
                [ordered]@{
                    family='responder_probe_handoff'; operationRole=$role
                    operationKey="responder_probe_handoff|$role|$authority|1|post_fallback_v6_paired"
                    operationKind='post_fallback_v6_paired'; authorityId=$authority
                    predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState='Accepted'; afterState=$status
                    commandIdentity=$receipt.commandIdentity; commandGeneration=1L
                    commandFingerprint=$receipt.commandFingerprint
                    receiptIdentity=$receipt.receiptIdentity; receiptGeneration=1L
                    receiptStatus=$status; receiptFingerprint=$receipt.receiptFingerprint
                    receiptValidated=$true; stateApplied=$stateApplied
                    responderProbeHandoff=[ordered]@{
                        role=$role; transitionMode='post_fallback_v6_paired'
                        sourceBoundary=$responderScope; effectiveBoundary=$responderScope
                        targetBoundary=$responderScope; transactionGeneration=1L
                        offerGeneration=2L; pendingAnswerAckGeneration=2L
                        payerDecisionId=3L; launchGeneration=4L
                        tunaPathLeaseGeneration=5L
                        tunaPathLeaseListenerRunId='listener-5'; lanes='file'
                        ackMessageId='ack-1'; ackSourceFingerprint='ack-fingerprint-1'
                        peerProbeFrameFingerprint='probe-fingerprint-1'
                        probeId='probe-1'; probeState='Acked'; probeTransport='tuna'
                        probeAckedUtcMs=100L; admissionGeneration=1
                        pendingPreCommitGeneration=1; ownerKey='owner-1'; pairKey='pair-1'
                        localProofIdentity='local-proof-1'; localProofFingerprint='local-fingerprint-1'
                        peerProofIdentity='peer-proof-1'; peerProofFingerprint='peer-fingerprint-1'
                        pairedReleaseAuthorityId='paired-release-1'; pairedReleaseGeneration=1
                        pairingGeneration=1L; handoffAuthorityId=$authority; handoffGeneration=1
                        linkedTransitionAuthorityId=$linkedTransitionAuthorityId
                        linkedTransitionGeneration=$linkedTransitionGeneration
                        decisionStatus=$status; decisionReason="responder_$stage"
                        receipts=@($receipt)
                    }
                }
            }
            function New-ResponderEnvelopeLine(
                [string]$transitionId,
                [string]$decisionKind,
                [string]$decisionStatus,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [long]$time,
                [object]$operation) {
                $aggregate = [ordered]@{
                    beforeStateGeneration=$beforeGeneration; afterStateGeneration=$afterGeneration
                    beforeStateFingerprint=$beforeHash; afterStateFingerprint=$afterHash
                    beforeBoundary=$responderScope; afterBoundary=$responderScope
                    changedFamilies=@('responder_probe_handoff'); operations=@($operation)
                }
                $envelope = [ordered]@{
                    snapshotContractVersion=1; snapshotSelectionMode='SnapshotV1Authoritative'
                    coordinatorInstanceId='coordinator-responder'; sessionId='session-receive'
                    transferId='transfer-receive'; direction='inbound'
                    decisionUtcMilliseconds=$time; transitionId=$transitionId
                    decisionKind=$decisionKind; decisionStatus=$decisionStatus
                    decisionReason="responder_$decisionStatus"; aggregate=$aggregate
                }
                $json = $envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $responderPrefix; transition_id=$transitionId; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            function New-ResponderEffect(
                [string]$transitionId,
                [string]$role,
                [string]$stage,
                [string]$status,
                [string]$kind,
                [long]$generation) {
                "event=filetransfer_coordinator_authority_effect; $responderPrefix; transition_id=$transitionId; coordinator_state_generation=$generation; operation_family=responder_probe_handoff; operation_key=responder_probe_handoff|$role|responder-handoff-1|1|post_fallback_v6_paired; operation_authority_id=responder-handoff-1; operation_generation=1; effect_kind=$kind; effect_identity=responder-$transitionId-effect; effect_generation=1; receipt_identity=responder-$stage-receipt; receipt_generation=1; receipt_status=$status; receipt_fingerprint=responder-$stage-receipt-fingerprint; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=3; route_commit_generation=0; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=4; checkpoint_request_id=root-checkpoint; data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied"
            }
            $responderCoverage = 'route_runtime,route_transition,runtime_unlock_retry_lifecycle,receive_recovery,fallback_tail_yield,regular_v4_frontier_repair_pressure,responder_probe_handoff'
            $responderPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-responder; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $responderStart = "event=filetransfer_coordinator_snapshot_stream_started; $responderPrefix; initial_state_generation=0; initial_state_hash=responder-before; emitted_family_coverage=$responderCoverage; shadow_family_coverage=; authoritative_family_coverage=$responderCoverage; authority_effect=none"
            $heldOperation = New-ResponderOperation 'owner_work_hold' 'Held' 'handoff_hold' '(none)' 0L $false
            $heldTransition = New-ResponderEnvelopeLine 'responder-held' 'HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork' 'Held' 0L 0L 'responder-before' 'responder-before' 60L $heldOperation
            $heldEffect = New-ResponderEffect 'responder-held' 'owner_work_hold' 'handoff_hold' 'Held' 'handoff_held' 0L
            $preparedOperation = New-ResponderOperation 'route_transition_handoff_pending' 'Prepared' 'route_transition_prepare_handoff' 'route-transition-1' 1L $true
            $preparedTransition = New-ResponderEnvelopeLine 'responder-prepared' 'PrepareFileTransferRouteTransition' 'Accepted' 0L 1L 'responder-before' 'responder-after' 61L $preparedOperation
            $preparedEffect = New-ResponderEffect 'responder-prepared' 'route_transition_handoff_pending' 'route_transition_prepare_handoff' 'Prepared' 'route_transition_handoff_supplied' 1L
            $responderEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $responderPrefix; final_state_generation=1; final_state_hash=responder-after; transition_count=2; emitted_family_coverage=$responderCoverage; shadow_family_coverage=; authoritative_family_coverage=$responderCoverage; authority_effect=none"
            $responderFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @('event=irrelevant',$responderStart,$heldTransition,$heldEffect,$preparedTransition,$preparedEffect,$responderEnd)
            $responderCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($responderStart,$heldTransition,$heldEffect,$preparedTransition,$preparedEffect,$responderEnd)
            $responderParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $responderFull -Compact $responderCompact
            $responderCorrelation = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $responderFull -Family 'responder_probe_handoff' -TaxonomyClasses @('runtime_unlock_responder_acked_probe_route_transition_handoff_missing')
            if ($responderFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $responderFull.Mode -ne 'SnapshotV1Authoritative' -or
                $responderFull.LegacySelectionCount -ne 0 -or
                $responderFull.ResponderProbeHandoffFamilyLegacySelectionCount -ne 0 -or
                $null -ne $responderFull.FirstUnresolvedBoundary -or
                $responderCorrelation.RawCount -ne 1 -or
                $responderCorrelation.DistinctCount -ne 1 -or
                $responderCorrelation.RecoveredCount -ne 1 -or
                $responderCorrelation.UnresolvedCount -ne 0 -or
                $responderParity.Verdict -ne 'pass' -or
                $responderFull.BoundaryHash -ne $responderCompact.BoundaryHash) {
                throw "Responder full/compact authoritative parity failed: $($responderFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            $checkpointBefore = [ordered]@{
                route='post_tuna_fallback_v6'; protocolVersion=6
                liveRouteEpoch=4L; routePublicationGeneration=4L
                routeCommitGeneration=3L; runtimeGeneration=4L
                terminalGeneration=0L; legGeneration=4L
                transportEpoch=2L; bridgeRecoveryGeneration=3L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='receive-data-session'; dataSessionGeneration=5L
            }
            $checkpointAfter = $checkpointBefore |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $checkpointAfter.checkpointGeneration = 7L
            $checkpointAfter.checkpointRequestId = 'fallback-checkpoint-7'
            $checkpointRefreshScope = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=0L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=1L; checkpointRequestId='(none)'
                dataSessionIdentity='receive-data-session'; dataSessionGeneration=5L
            }
            $checkpointRefreshReceipt = [ordered]@{
                stage='initial_activation_envelope_refresh'
                commandIdentity='checkpoint-refresh-command'
                commandGeneration=1L
                commandFingerprint='checkpoint-refresh-command-fingerprint'
                receiptIdentity='checkpoint-refresh-receipt'
                receiptGeneration=1L
                receiptStatus='Accepted'
                receiptReason='Accepted'
                receiptFingerprint='checkpoint-refresh-receipt-fingerprint'
                receiptValidated=$true
            }
            $checkpointRefreshOperation = [ordered]@{
                family='receive_recovery'
                operationRole='initial_activation_envelope_refresh'
                operationKey='receive_recovery|initial_activation_envelope_refresh|checkpoint-refresh-authority|1|InitialActivationEnvelopeRefresh'
                operationKind='InitialActivationEnvelopeRefresh'
                authorityId='checkpoint-refresh-authority'
                predecessorAuthorityId='(none)'
                operationGeneration=1L
                beforeState='Committed'; afterState='Committed'
                commandIdentity=$checkpointRefreshReceipt.commandIdentity
                commandGeneration=1L
                commandFingerprint=$checkpointRefreshReceipt.commandFingerprint
                receiptIdentity=$checkpointRefreshReceipt.receiptIdentity
                receiptGeneration=1L
                receiptStatus='Accepted'
                receiptFingerprint=$checkpointRefreshReceipt.receiptFingerprint
                receiptValidated=$true; stateApplied=$true
                receiveRecovery=[ordered]@{
                    role='initial_activation_envelope_refresh'
                    sourceBoundary=$checkpointRefreshScope
                    effectiveBoundary=$checkpointRefreshScope
                    recoveryKind='BridgeReceiveStallRecovery'
                    bridgeDecisionGeneration=1L
                    recoveryLivenessLeaseGeneration=1L
                    executionState='Invalidated'; closureState='None'
                    closureGeneration=1L; proofIdentity='checkpoint-proof'
                    proofObservedUtcMs=1L; heldRetryAuthorityId='(none)'
                    reservedRetryAuthorityId='(none)'
                    initialActivationHoldGeneration=1L
                    envelopeRefreshGeneration=1L
                    checkpointBindingGeneration=0L
                    checkpointDeliveryGeneration=0L
                    checkpointResumeGeneration=0L
                    checkpointFrontierHoldGeneration=0L
                    checkpointFrontierRecheckGeneration=0L
                    receipts=@($checkpointRefreshReceipt)
                }
            }
            $checkpointReceipt = [ordered]@{
                stage='pressure_settlement_terminal'
                commandIdentity='fallback-checkpoint-command'
                commandGeneration=7L
                commandFingerprint='fallback-checkpoint-command-fingerprint'
                receiptIdentity='fallback-checkpoint-receipt'
                receiptGeneration=7L
                receiptStatus='Applied'
                receiptReason='peer_current_state_requested_checkpoint'
                receiptFingerprint='fallback-checkpoint-receipt-fingerprint'
                receiptValidated=$true
            }
            $checkpointPressurePayload = New-RepairReplayPayload `
                'repair_authority_terminal' @($checkpointReceipt)
            $checkpointPressurePayload.sourceBoundary = $checkpointAfter
            $checkpointPressurePayload.effectiveBoundary = $checkpointAfter
            $checkpointPressurePayload.bindingState = 'Settled'
            $checkpointPressurePayload.settlementOutcome = 'ProgressObserved'
            $checkpointPressurePayload.settlementReason =
                'peer_current_state_requested_checkpoint'
            $checkpointPressureOperation = [ordered]@{
                family='regular_v4_frontier_repair_pressure'
                operationRole='repair_authority_terminal'
                operationKey='regular_v4_frontier_repair_pressure|repair_authority_terminal|checkpoint-pressure-authority|7|PressureSettlement'
                operationKind='PressureSettlement'
                authorityId='checkpoint-pressure-authority'
                predecessorAuthorityId='(none)'
                operationGeneration=7L
                beforeState='Applied'; afterState='Applied'
                commandIdentity=$checkpointReceipt.commandIdentity
                commandGeneration=7L
                commandFingerprint=$checkpointReceipt.commandFingerprint
                receiptIdentity=$checkpointReceipt.receiptIdentity
                receiptGeneration=7L
                receiptStatus='Applied'
                receiptFingerprint=$checkpointReceipt.receiptFingerprint
                receiptValidated=$true; stateApplied=$true
                regularV4FrontierRepairPressure=$checkpointPressurePayload
            }
            $checkpointAggregate = [ordered]@{
                beforeStateGeneration=0L; afterStateGeneration=1L
                beforeStateFingerprint='checkpoint-before'
                afterStateFingerprint='checkpoint-after'
                beforeBoundary=$checkpointBefore; afterBoundary=$checkpointAfter
                changedFamilies=@(
                    'receive_recovery',
                    'regular_v4_frontier_repair_pressure')
                operations=@(
                    $checkpointRefreshOperation,
                    $checkpointPressureOperation)
            }
            $checkpointEnvelope = [ordered]@{
                snapshotContractVersion=1
                snapshotSelectionMode='SnapshotV1Shadow'
                coordinatorInstanceId='coordinator-checkpoint'
                sessionId='session-receive'; transferId='transfer-receive'
                direction='inbound'; transitionId='checkpoint-requested'
                decisionKind='FallbackCheckpointRequested'
                decisionStatus='Accepted'
                decisionReason='peer_current_state_requested_checkpoint'
                decisionUtcMilliseconds=70L
                aggregate=$checkpointAggregate
            }
            $checkpointJson = $checkpointEnvelope |
                ConvertTo-Json -Depth 32 -Compress
            $checkpointBytes = [Text.UTF8Encoding]::new($false).GetBytes(
                $checkpointJson)
            $checkpointHash = Get-FileTransferCoordinatorSnapshotSha256 `
                -Bytes $checkpointBytes
            $checkpointPayload = [Convert]::ToBase64String($checkpointBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $checkpointCoverage = 'receive_recovery,regular_v4_frontier_repair_pressure'
            $checkpointPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-checkpoint; session_id=session-receive; transfer_id=transfer-receive; direction=inbound"
            $checkpointStart = "event=filetransfer_coordinator_snapshot_stream_started; $checkpointPrefix; initial_state_generation=0; initial_state_hash=checkpoint-before; emitted_family_coverage=$checkpointCoverage; shadow_family_coverage=; authoritative_family_coverage=$checkpointCoverage; authority_effect=none"
            $checkpointTransition = "event=filetransfer_coordinator_transition_snapshot; $checkpointPrefix; transition_id=checkpoint-requested; before_state_generation=0; after_state_generation=1; snapshot_sha256=$checkpointHash; snapshot_payload=$checkpointPayload; authority_effect=none"
            $checkpointEffect = "event=filetransfer_coordinator_authority_effect; $checkpointPrefix; transition_id=checkpoint-requested; coordinator_state_generation=1; operation_family=regular_v4_frontier_repair_pressure; operation_key=$($checkpointPressureOperation.operationKey); operation_authority_id=$($checkpointPressureOperation.authorityId); operation_generation=7; effect_kind=regular_v4_pressure_decision_adopted; effect_identity=checkpoint-adoption-effect; effect_generation=7; receipt_identity=$($checkpointReceipt.receiptIdentity); receipt_generation=7; receipt_status=Applied; receipt_fingerprint=$($checkpointReceipt.receiptFingerprint); route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; route_publication_generation=4; route_commit_generation=3; runtime_generation=4; terminal_generation=0; leg_generation=4; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=7; checkpoint_request_id=fallback-checkpoint-7; data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied"
            $checkpointEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $checkpointPrefix; final_state_generation=1; final_state_hash=checkpoint-after; transition_count=1; emitted_family_coverage=$checkpointCoverage; shadow_family_coverage=; authoritative_family_coverage=$checkpointCoverage; authority_effect=none"
            $checkpointFull = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                'event=irrelevant', $checkpointStart, $checkpointTransition,
                $checkpointEffect, $checkpointEnd)
            $checkpointCompact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $checkpointStart, $checkpointTransition, $checkpointEffect,
                $checkpointEnd)
            $checkpointParity = Compare-FileTransferCoordinatorSnapshotEvaluations `
                -Full $checkpointFull -Compact $checkpointCompact
            if ($checkpointFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $checkpointFull.FirstUnresolvedBoundary -or
                $checkpointParity.Verdict -ne 'pass' -or
                $checkpointFull.BoundaryHash -ne $checkpointCompact.BoundaryHash) {
                throw "Fallback checkpoint full/compact parity failed: $($checkpointFull | ConvertTo-Json -Depth 8 -Compress)"
            }
            $checkpointRefreshEffect = "event=filetransfer_coordinator_authority_effect; $checkpointPrefix; transition_id=checkpoint-requested; coordinator_state_generation=1; operation_family=receive_recovery; operation_key=$($checkpointRefreshOperation.operationKey); operation_authority_id=$($checkpointRefreshOperation.authorityId); operation_generation=1; effect_kind=initial_activation_envelope_refresh_observed; effect_identity=checkpoint-refresh-effect; effect_generation=1; receipt_identity=$($checkpointRefreshReceipt.receiptIdentity); receipt_generation=1; receipt_status=Accepted; receipt_fingerprint=$($checkpointRefreshReceipt.receiptFingerprint); route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=1; checkpoint_request_id=(none); data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied"
            $reEmittedRefresh =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                    $checkpointStart, $checkpointTransition,
                    $checkpointEffect, $checkpointRefreshEffect,
                    $checkpointEnd)
            if ($reEmittedRefresh.ArtifactEvidenceVerdict -ne 'fail' -or
                $reEmittedRefresh.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_checkpoint_refresh_carry_effect_invalid') {
                throw "A committed checkpoint refresh carry re-emitted its one-shot effect: $($reEmittedRefresh | ConvertTo-Json -Depth 8 -Compress)"
            }
            $invalidRefreshEnvelope = $checkpointEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $invalidRefresh = @(
                $invalidRefreshEnvelope.aggregate.operations | Where-Object {
                    [string]$_.operationRole -ceq
                        'initial_activation_envelope_refresh'
                })[0]
            $invalidRefresh.receiptValidated = $false
            $invalidRefresh.receiveRecovery.receipts[0].receiptValidated =
                $false
            $invalidRefreshJson = $invalidRefreshEnvelope |
                ConvertTo-Json -Depth 32 -Compress
            $invalidRefreshBytes =
                [Text.UTF8Encoding]::new($false).GetBytes(
                    $invalidRefreshJson)
            $invalidRefreshHash =
                Get-FileTransferCoordinatorSnapshotSha256 `
                    -Bytes $invalidRefreshBytes
            $invalidRefreshPayload = [Convert]::ToBase64String(
                $invalidRefreshBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $invalidRefreshTransition = "event=filetransfer_coordinator_transition_snapshot; $checkpointPrefix; transition_id=checkpoint-requested; before_state_generation=0; after_state_generation=1; snapshot_sha256=$invalidRefreshHash; snapshot_payload=$invalidRefreshPayload; authority_effect=none"
            $invalidRefreshEvaluation =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                    $checkpointStart, $invalidRefreshTransition,
                    $checkpointEffect, $checkpointEnd)
            if ($invalidRefreshEvaluation.ArtifactEvidenceVerdict -ne 'fail' -or
                $invalidRefreshEvaluation.FirstUnresolvedBoundary.variant -ne
                    'fallback_checkpoint_committed_initial_activation_envelope_refresh_carry_invalid') {
                throw "An invalid committed checkpoint refresh carry did not fail precisely: $($invalidRefreshEvaluation | ConvertTo-Json -Depth 8 -Compress)"
            }
            $missingCheckpointEffect =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                    $checkpointStart, $checkpointTransition, $checkpointEnd)
            if ($missingCheckpointEffect.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_regular_v4_pressure_required_effect_missing') {
                throw "Missing fallback-checkpoint Family-5 adoption effect did not fail closed: $($missingCheckpointEffect | ConvertTo-Json -Depth 8 -Compress)"
            }
            $mutatedCheckpointEnvelope = $checkpointEnvelope |
                ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $mutatedCheckpointEnvelope.aggregate.afterBoundary.runtimeGeneration = 5L
            $mutatedCheckpointJson = $mutatedCheckpointEnvelope |
                ConvertTo-Json -Depth 32 -Compress
            $mutatedCheckpointBytes =
                [Text.UTF8Encoding]::new($false).GetBytes($mutatedCheckpointJson)
            $mutatedCheckpointHash = Get-FileTransferCoordinatorSnapshotSha256 `
                -Bytes $mutatedCheckpointBytes
            $mutatedCheckpointPayload = [Convert]::ToBase64String($mutatedCheckpointBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $mutatedCheckpointTransition = "event=filetransfer_coordinator_transition_snapshot; $checkpointPrefix; transition_id=checkpoint-requested; before_state_generation=0; after_state_generation=1; snapshot_sha256=$mutatedCheckpointHash; snapshot_payload=$mutatedCheckpointPayload; authority_effect=none"
            $mutatedCheckpoint =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                    $checkpointStart, $mutatedCheckpointTransition,
                    $checkpointEffect, $checkpointEnd)
            if ($mutatedCheckpoint.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_operation_catalog_mismatch' -or
                $mutatedCheckpoint.FirstUnresolvedBoundary.variant -ne
                    'fallback_checkpoint_scope_transition_omitted_from_cross_family_catalog') {
                throw "Mutated fallback checkpoint did not fail with the exact catalog variant: $($mutatedCheckpoint | ConvertTo-Json -Depth 8 -Compress)"
            }

            function New-RouteCommitSettlementOperation(
                [string]$role,
                [string]$beforeState,
                [string]$afterState) {
                $authority = 'fte1-111111111111111111111111'
                $receipt = [ordered]@{
                    stage='pressure_settlement'
                    commandIdentity='settlement-command'
                    commandGeneration=7L
                    commandFingerprint='settlement-command-fingerprint'
                    receiptIdentity='settlement-receipt'
                    receiptGeneration=7L
                    receiptStatus='Applied'
                    receiptReason='regular_v4_frontier_repair_pressure_settlement_applied'
                    receiptFingerprint='settlement-receipt-fingerprint'
                    receiptValidated=$true
                }
                [ordered]@{
                    family='regular_v4_frontier_repair_pressure'
                    operationRole=$role
                    operationKey="regular_v4_frontier_repair_pressure|$role|$authority|7|PressureSettlement"
                    operationKind='PressureSettlement'
                    authorityId=$authority
                    predecessorAuthorityId='(none)'
                    operationGeneration=7L
                    beforeState=$beforeState
                    afterState=$afterState
                    commandIdentity=$receipt.commandIdentity
                    commandGeneration=$receipt.commandGeneration
                    commandFingerprint=$receipt.commandFingerprint
                    receiptIdentity=$receipt.receiptIdentity
                    receiptGeneration=$receipt.receiptGeneration
                    receiptStatus=$receipt.receiptStatus
                    receiptFingerprint=$receipt.receiptFingerprint
                    receiptValidated=$true
                    stateApplied=$true
                    regularV4FrontierRepairPressure=[ordered]@{
                        role=$role
                        sourceBoundary=$receiveScope
                        effectiveBoundary=$receiveScope
                        repairKind='V4Repair'
                        repairKey='regular-v4-pressure:route-commit'
                        frontierChunkIndex=12L
                        durableReceivedHighestChunkIndex=13L
                        pendingRepairCount=0
                        targetTransport='RegularNkn'
                        pressureReason='route_commit'
                        decisionGeneration=7L
                        pressureInstallGeneration=7L
                        bindingState='Settled'
                        settlementOutcome='ProgressObserved'
                        settlementReason='route_commit'
                        currentAuthorityCleared=$true
                        retainedPredecessorCleared=$false
                        successorAuthorityId='(none)'
                        recoveryAuthorityId='(none)'
                        recoveryOperationGeneration=0L
                        recoveryKind='(none)'
                        receipts=@($receipt)
                    }
                }
            }
            function New-RouteCommitSettlementTransition(
                [string]$transitionId,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [string]$beforeState,
                [string]$afterState,
                [long]$decisionTime) {
                $operations = @(
                    (New-RouteCommitSettlementOperation `
                        'pressure_settlement_terminal' $beforeState $afterState),
                    (New-RouteCommitSettlementOperation `
                        'repair_authority_terminal' $beforeState $afterState))
                $envelope = [ordered]@{
                    snapshotContractVersion=1
                    snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='coordinator-route-commit-effects'
                    sessionId='session-route-commit-effects'
                    transferId='transfer-route-commit-effects'
                    direction='outbound'
                    transitionId=$transitionId
                    decisionKind='CommitFileTransferRouteTransition'
                    decisionStatus='Committed'
                    decisionReason='filetransfer_route_transition_committed'
                    decisionUtcMilliseconds=$decisionTime
                    aggregate=[ordered]@{
                        beforeStateGeneration=$beforeGeneration
                        afterStateGeneration=$afterGeneration
                        beforeStateFingerprint=$beforeHash
                        afterStateFingerprint=$afterHash
                        beforeBoundary=$receiveScope
                        afterBoundary=$receiveScope
                        changedFamilies=@('regular_v4_frontier_repair_pressure')
                        operations=$operations
                    }
                }
                $json = $envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $routeCommitEffectPrefix; transition_id=$transitionId; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }
            function New-RouteCommitSettlementEffect(
                [string]$transitionId,
                [long]$stateGeneration,
                [string]$kind,
                [long]$writerSequence) {
                $operationKey = 'regular_v4_frontier_repair_pressure|repair_authority_terminal|fte1-111111111111111111111111|7|PressureSettlement'
                $identity = "$operationKey`:$kind"
                "[2026-01-01 00:00:$($writerSequence.ToString('00'))Z] [INFO] [FileTransferCoordinator] event=filetransfer_coordinator_authority_effect; $routeCommitEffectPrefix; transition_id=$transitionId; coordinator_state_generation=$stateGeneration; operation_family=regular_v4_frontier_repair_pressure; operation_key=$operationKey; operation_authority_id=fte1-111111111111111111111111; operation_generation=7; effect_kind=$kind; effect_identity=$identity; effect_generation=7; receipt_identity=settlement-receipt; receipt_generation=7; receipt_status=Applied; receipt_fingerprint=settlement-receipt-fingerprint; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=2; bridge_recovery_generation=3; checkpoint_generation=4; checkpoint_request_id=root-checkpoint; data_session_identity=receive-data-session; data_session_generation=5; authority_effect=applied; log_writer_id=writer-sender; log_writer_sequence=$writerSequence"
            }
            $routeCommitEffectPrefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-route-commit-effects; session_id=session-route-commit-effects; transfer_id=transfer-route-commit-effects; direction=outbound"
            $routeCommitCoverage = 'regular_v4_frontier_repair_pressure'
            $routeCommitStart = "event=filetransfer_coordinator_snapshot_stream_started; $routeCommitEffectPrefix; initial_state_generation=0; initial_state_hash=route-commit-before; emitted_family_coverage=$routeCommitCoverage; shadow_family_coverage=; authoritative_family_coverage=$routeCommitCoverage; authority_effect=none"
            $routeCommitEntering = New-RouteCommitSettlementTransition `
                'route-commit-entering' 0 1 'route-commit-before' `
                'route-commit-settled' 'None' 'Applied' 100
            $routeCommitCarried = New-RouteCommitSettlementTransition `
                'route-commit-carried' 1 2 'route-commit-settled' `
                'route-commit-after' 'Applied' 'Applied' 200
            $closureKinds = @(
                'pressure_settlement_bridge_applied',
                'local_contract_settled',
                'local_contract_reference_cleared')
            $enteringEffects = @()
            $sequence = 10L
            foreach ($kind in $closureKinds) {
                $enteringEffects += New-RouteCommitSettlementEffect `
                    'route-commit-entering' 1 $kind $sequence
                $sequence++
            }
            $enteringEffects += New-RouteCommitSettlementEffect `
                'route-commit-entering' 1 `
                'route_commit_local_lifecycle_adopted' $sequence
            $carriedAdoption = New-RouteCommitSettlementEffect `
                'route-commit-carried' 2 `
                'route_commit_local_lifecycle_adopted' 20
            $routeCommitEnd = "event=filetransfer_coordinator_snapshot_stream_completed; $routeCommitEffectPrefix; final_state_generation=2; final_state_hash=route-commit-after; transition_count=2; emitted_family_coverage=$routeCommitCoverage; shadow_family_coverage=; authoritative_family_coverage=$routeCommitCoverage; authority_effect=none"
            $routeCommitValidLines = @($routeCommitStart,$routeCommitEntering) +
                $enteringEffects + @($routeCommitCarried,$carriedAdoption,$routeCommitEnd)
            $routeCommitValid = Get-FileTransferCoordinatorSnapshotEvaluation `
                -Lines $routeCommitValidLines
            if ($routeCommitValid.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $routeCommitValid.FirstUnresolvedBoundary) {
                throw "Transition-local route-commit effects did not pass: $($routeCommitValid | ConvertTo-Json -Depth 8 -Compress)"
            }
            $reusedEffects = @()
            $sequence = 21L
            foreach ($kind in $closureKinds) {
                $reusedEffects += New-RouteCommitSettlementEffect `
                    'route-commit-carried' 2 $kind $sequence
                $sequence++
            }
            $routeCommitReuseFull =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines (
                    @('event=irrelevant',$routeCommitStart,$routeCommitEntering) +
                    $enteringEffects + @($routeCommitCarried,$carriedAdoption) +
                    $reusedEffects + @($routeCommitEnd))
            $routeCommitReuseCompact =
                Get-FileTransferCoordinatorSnapshotEvaluation -Lines (
                    @($routeCommitStart,$routeCommitEntering) +
                    $enteringEffects + @($routeCommitCarried,$carriedAdoption) +
                    $reusedEffects + @($routeCommitEnd))
            if ($routeCommitReuseFull.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_authority_effect_identity_reused_across_transitions' -or
                $routeCommitReuseFull.FirstUnresolvedBoundary.variant -ne
                    'route_commit_reemitted_already_applied_pressure_settlement_lifecycle' -or
                $routeCommitReuseFull.FirstUnresolvedBoundary.rawCount -ne 3 -or
                $routeCommitReuseFull.FirstUnresolvedBoundary.distinctCount -ne 1 -or
                $routeCommitReuseFull.FirstUnresolvedBoundary.unresolvedCount -ne 1 -or
                $routeCommitReuseFull.BoundaryHash -ne
                    $routeCommitReuseCompact.BoundaryHash -or
                $routeCommitReuseFull.FirstUnresolvedBoundary.boundaryHash -ne
                    $routeCommitReuseCompact.FirstUnresolvedBoundary.boundaryHash) {
                throw "Cross-transition settlement-effect reuse did not fail with full/compact parity: $($routeCommitReuseFull | ConvertTo-Json -Depth 8 -Compress)"
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
    public async Task FileTransferOps_CoordinatorSnapshotHeldProofPublicationIsStateNotDelivery()
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
            $scope = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=0L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=8L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='data-session-held'; dataSessionGeneration=1L
            }
            $receipt = [ordered]@{
                stage='proof_publication_pending'
                commandIdentity='publication-command-8'
                commandGeneration=8L
                commandFingerprint='publication-command-fingerprint-8'
                receiptIdentity='publication-held-receipt-8'
                receiptGeneration=8L
                receiptStatus='Held'
                receiptReason='filetransfer_receive_recovery_proof_state_publication_held'
                receiptFingerprint='publication-held-receipt-fingerprint-8'
                receiptValidated=$true
            }
            $operation = [ordered]@{
                family='receive_recovery'
                operationRole='proof_publication_pending'
                operationKey='receive_recovery|proof_publication_pending|recovery-authority-8|8|ProofPublication'
                operationKind='ProofPublication'
                authorityId='recovery-authority-8'
                predecessorAuthorityId='recovery-authority-7'
                operationGeneration=8L
                beforeState='Pending'; afterState='Held'
                commandIdentity=$receipt.commandIdentity
                commandGeneration=$receipt.commandGeneration
                commandFingerprint=$receipt.commandFingerprint
                receiptIdentity=$receipt.receiptIdentity
                receiptGeneration=$receipt.receiptGeneration
                receiptStatus=$receipt.receiptStatus
                receiptFingerprint=$receipt.receiptFingerprint
                receiptValidated=$true; stateApplied=$true
                routeRuntime=$null; routeTransition=$null
                runtimeUnlockRetry=$null
                receiveRecovery=[ordered]@{
                    role='proof_publication_pending'
                    sourceBoundary=$scope; effectiveBoundary=$scope
                    recoveryKind='BridgeReceiveStallRecovery'
                    bridgeDecisionGeneration=8L
                    recoveryLivenessLeaseGeneration=8L
                    executionState='Completed'; closureState='Retained'
                    closureGeneration=8L; proofIdentity='(none)'
                    proofObservedUtcMs=0L
                    heldRetryAuthorityId='(none)'
                    reservedRetryAuthorityId='(none)'
                    initialActivationHoldGeneration=0L
                    envelopeRefreshGeneration=0L
                    checkpointBindingGeneration=0L
                    checkpointDeliveryGeneration=0L
                    checkpointResumeGeneration=0L
                    checkpointFrontierHoldGeneration=0L
                    checkpointFrontierRecheckGeneration=0L
                    receipts=@($receipt)
                }
                fallbackTailYield=$null
                regularV4FrontierRepairPressure=$null
                responderProbeHandoff=$null
            }
            function New-TransitionLine(
                [object]$operationValue,
                [string]$status,
                [string]$reason,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash) {
                $envelope=[ordered]@{
                    snapshotContractVersion=1
                    snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='coordinator-held-publication'
                    sessionId='session-held'; transferId='transfer-held'
                    direction='inbound'; decisionUtcMilliseconds=10L
                    transitionId='transition-held'
                    decisionKind='ApplyReceiveRecoveryProofStatePublicationReceipt'
                    decisionStatus=$status; decisionReason=$reason
                    aggregate=[ordered]@{
                        beforeStateGeneration=$beforeGeneration
                        afterStateGeneration=$afterGeneration
                        beforeStateFingerprint=$beforeHash
                        afterStateFingerprint=$afterHash
                        beforeBoundary=$scope; afterBoundary=$scope
                        changedFamilies=@('receive_recovery')
                        operations=@($operationValue)
                    }
                }
                $json=$envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=transition-held; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=writer-helper; log_writer_sequence=8703"
            }
            $prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-held-publication; session_id=session-held; transfer_id=transfer-held; direction=inbound"
            $start="event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=before-held; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $heldTransition=New-TransitionLine $operation 'Accepted' `
                'filetransfer_receive_recovery_proof_state_publication_held' `
                0 1 'before-held' 'after-held'
            $completed="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=1; final_state_hash=after-held; transition_count=1; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"

            $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                'event=irrelevant',$start,$heldTransition,$completed)
            $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$heldTransition,$completed)
            if ($full.ArtifactEvidenceVerdict -ne 'pass' -or
                $compact.ArtifactEvidenceVerdict -ne 'pass' -or
                $full.BoundaryHash -ne $compact.BoundaryHash) {
                throw "Held publication was misclassified as delivery evidence: $($full | ConvertTo-Json -Depth 8 -Compress)"
            }

            $spuriousEffect="event=filetransfer_coordinator_authority_effect; $prefix; transition_id=transition-held; coordinator_state_generation=1; operation_family=receive_recovery; operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=8; effect_kind=proof_publication_delivered; effect_identity=spurious-held-delivery; effect_generation=8; receipt_identity=$($receipt.receiptIdentity); receipt_generation=8; receipt_status=Held; receipt_fingerprint=$($receipt.receiptFingerprint); route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=8; checkpoint_generation=0; checkpoint_request_id=(none); data_session_identity=data-session-held; data_session_generation=1; authority_effect=applied"
            $spurious=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$heldTransition,$spuriousEffect,$completed)
            if ($spurious.ArtifactEvidenceVerdict -ne 'fail' -or
                $spurious.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_proof_publication_transition_invalid') {
                throw "A Held publication admitted a delivery effect: $($spurious | ConvertTo-Json -Depth 8 -Compress)"
            }

            $malformed=$operation | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
            $malformed.beforeState='Pending'; $malformed.afterState='Pending'
            $malformed.stateApplied=$false
            $malformed.receiptStatus='Idempotent'
            $malformed.receiveRecovery.receipts[0].stage='attempted_decision'
            $malformed.receiveRecovery.receipts[0].receiptStatus='Idempotent'
            $malformedTransition=New-TransitionLine $malformed 'Idempotent' `
                'filetransfer_receive_recovery_proof_state_publication_held' `
                0 0 'before-held' 'before-held'
            $malformedCompleted="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=0; final_state_hash=before-held; transition_count=1; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $malformedResult=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$malformedTransition,$malformedCompleted)
            if ($malformedResult.ArtifactEvidenceVerdict -ne 'fail' -or
                $malformedResult.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_proof_publication_transition_invalid' -or
                $malformedResult.FirstUnresolvedBoundary.variant -ne
                    'held_receipt_omitted_from_pending_publication_state' -or
                $malformedResult.FirstUnresolvedBoundary.writerSequence -ne 8703L) {
                throw "Malformed Held evidence did not fail at the exact boundary: $($malformedResult | ConvertTo-Json -Depth 8 -Compress)"
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Held proof-publication evaluator validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotProofPublicationTerminalRolloverIsExact()
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
            $scope = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=0L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=0L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='publication-rollover-session'
                dataSessionGeneration=1L
            }
            function New-PublicationReceipt(
                [long]$generation,
                [string]$outcome,
                [bool]$validated) {
                [ordered]@{
                    stage='proof_publication_terminal'
                    commandIdentity='stable-publication-command'
                    commandGeneration=$generation
                    commandFingerprint="publication-command-fingerprint-$generation"
                    receiptIdentity='stable-publication-receipt'
                    receiptGeneration=$generation
                    receiptStatus=$outcome
                    receiptReason=$outcome
                    receiptFingerprint="publication-receipt-fingerprint-$generation-$outcome"
                    receiptValidated=$validated
                }
            }
            function New-PublicationOperation(
                [string]$role,
                [string]$before,
                [string]$after,
                [long]$generation,
                [object]$receipt) {
                $hasReceipt=$null -ne $receipt
                $commandFingerprint="publication-command-fingerprint-$generation"
                $receipts=[System.Collections.Generic.List[object]]::new()
                if($hasReceipt){$receipts.Add($receipt)}
                [ordered]@{
                    family='receive_recovery'; operationRole=$role
                    operationKey="receive_recovery|$role|publication-authority|$generation|ProofPublication"
                    operationKind='ProofPublication'
                    authorityId='publication-authority'
                    predecessorAuthorityId='(none)'
                    operationGeneration=$generation
                    beforeState=$before; afterState=$after
                    commandIdentity=if($hasReceipt){$receipt.commandIdentity}else{"pending-command-$generation"}
                    commandGeneration=$generation
                    commandFingerprint=$commandFingerprint
                    receiptIdentity=if($hasReceipt){$receipt.receiptIdentity}else{'(none)'}
                    receiptGeneration=if($hasReceipt){$receipt.receiptGeneration}else{0L}
                    receiptStatus=if($hasReceipt){$receipt.receiptStatus}else{'Pending'}
                    receiptFingerprint=if($hasReceipt){$receipt.receiptFingerprint}else{'(none)'}
                    receiptValidated=if($hasReceipt){$receipt.receiptValidated}else{$false}
                    stateApplied=$true
                    routeRuntime=$null; routeTransition=$null
                    runtimeUnlockRetry=$null
                    receiveRecovery=[ordered]@{
                        role=$role; sourceBoundary=$scope
                        effectiveBoundary=$scope
                        recoveryKind='BridgeReceiveStallRecovery'
                        bridgeDecisionGeneration=1L
                        recoveryLivenessLeaseGeneration=1L
                        executionState='Completed'; closureState='Retained'
                        closureGeneration=1L; proofIdentity='(none)'
                        proofObservedUtcMs=0L
                        heldRetryAuthorityId='(none)'
                        reservedRetryAuthorityId='(none)'
                        initialActivationHoldGeneration=0L
                        envelopeRefreshGeneration=0L
                        checkpointBindingGeneration=0L
                        checkpointDeliveryGeneration=0L
                        checkpointResumeGeneration=0L
                        checkpointFrontierHoldGeneration=0L
                        checkpointFrontierRecheckGeneration=0L
                        receipts=$receipts
                    }
                    fallbackTailYield=$null
                    regularV4FrontierRepairPressure=$null
                    responderProbeHandoff=$null
                }
            }
            function New-RecheckOperation(
                [string]$before,
                [string]$after) {
                [ordered]@{
                    family='receive_recovery'
                    operationRole='proof_publication_recheck'
                    operationKey='receive_recovery|proof_publication_recheck|publication-authority|1|ProofPublicationRecheck'
                    operationKind='ProofPublicationRecheck'
                    authorityId='publication-authority'
                    predecessorAuthorityId='(none)'
                    operationGeneration=1L
                    beforeState=$before; afterState=$after
                    commandIdentity='publication-recheck-command'
                    commandGeneration=1L
                    commandFingerprint='publication-recheck-command-fingerprint'
                    receiptIdentity='(none)'; receiptGeneration=0L
                    receiptStatus='Pending'; receiptFingerprint='(none)'
                    receiptValidated=$false; stateApplied=$true
                    routeRuntime=$null; routeTransition=$null
                    runtimeUnlockRetry=$null
                    receiveRecovery=[ordered]@{
                        role='proof_publication_recheck'
                        sourceBoundary=$scope; effectiveBoundary=$scope
                        recoveryKind='BridgeReceiveStallRecovery'
                        bridgeDecisionGeneration=1L
                        recoveryLivenessLeaseGeneration=1L
                        executionState='Completed'; closureState='Retained'
                        closureGeneration=1L; proofIdentity='(none)'
                        proofObservedUtcMs=0L
                        heldRetryAuthorityId='(none)'
                        reservedRetryAuthorityId='(none)'
                        initialActivationHoldGeneration=0L
                        envelopeRefreshGeneration=0L
                        checkpointBindingGeneration=0L
                        checkpointDeliveryGeneration=0L
                        checkpointResumeGeneration=0L
                        checkpointFrontierHoldGeneration=0L
                        checkpointFrontierRecheckGeneration=0L
                        receipts=@()
                    }
                    fallbackTailYield=$null
                    regularV4FrontierRepairPressure=$null
                    responderProbeHandoff=$null
                }
            }
            function New-TransitionLine(
                [string]$id,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [string]$reason,
                [object[]]$operations,
                [long]$sequence,
                [string]$decisionKind='ApplyReceiveRecoveryProofStatePublicationReceipt') {
                $envelope=[ordered]@{
                    snapshotContractVersion=1
                    snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='publication-rollover-coordinator'
                    sessionId='publication-rollover-session'
                    transferId='publication-rollover-transfer'
                    direction='inbound'
                    decisionUtcMilliseconds=$sequence
                    transitionId=$id
                    decisionKind=$decisionKind
                    decisionStatus='Accepted'; decisionReason=$reason
                    aggregate=[ordered]@{
                        beforeStateGeneration=$beforeGeneration
                        afterStateGeneration=$afterGeneration
                        beforeStateFingerprint=$beforeHash
                        afterStateFingerprint=$afterHash
                        beforeBoundary=$scope; afterBoundary=$scope
                        changedFamilies=@('receive_recovery')
                        operations=$operations
                    }
                }
                $json=$envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$id; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=writer-helper; log_writer_sequence=$sequence"
            }
            function New-DeliveryEffect(
                [string]$transitionId,
                [object]$operation,
                [long]$stateGeneration,
                [long]$sequence) {
                "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$transitionId; coordinator_state_generation=$stateGeneration; operation_family=receive_recovery; operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=proof_publication_delivered; effect_identity=delivery-$transitionId; effect_generation=$($operation.operationGeneration); receipt_identity=$($operation.receiptIdentity); receipt_generation=$($operation.receiptGeneration); receipt_status=$($operation.receiptStatus); receipt_fingerprint=$($operation.receiptFingerprint); route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=0; checkpoint_request_id=(none); data_session_identity=publication-rollover-session; data_session_generation=1; authority_effect=applied; log_writer_id=writer-helper; log_writer_sequence=$sequence"
            }
            function New-EvaluationLines([string]$outcome,[bool]$includeExtra) {
                $validated=$outcome -eq 'Delivered'
                $receipt1=New-PublicationReceipt 1 'Delivered' $true
                $pending1=New-PublicationOperation 'proof_publication_pending' 'Pending' 'None' 1 $null
                $terminal1=New-PublicationOperation 'proof_publication_terminal' 'None' 'Delivered' 1 $receipt1
                $receipt2=New-PublicationReceipt 2 $outcome $validated
                $pending2=New-PublicationOperation 'proof_publication_pending' 'Pending' 'None' 2 $null
                $retired1=New-PublicationOperation 'proof_publication_terminal' 'Delivered' 'None' 1 $receipt1
                $terminal2=New-PublicationOperation 'proof_publication_terminal' 'None' $outcome 2 $receipt2
                $rolloverOperations=@($pending2,$retired1,$terminal2)
                if($includeExtra){$rolloverOperations += $retired1}
                $lines=@(
                    $start
                    (New-TransitionLine 'publication-1' 0 1 'hash-0' 'hash-1' 'filetransfer_receive_recovery_proof_state_publication_delivered' @($pending1,$terminal1) 10)
                    (New-DeliveryEffect 'publication-1' $terminal1 1 11)
                    (New-TransitionLine 'publication-2' 1 2 'hash-1' 'hash-2' "filetransfer_receive_recovery_proof_state_publication_$($outcome.ToLowerInvariant())" $rolloverOperations 20)
                )
                if($outcome -eq 'Delivered'){
                    $lines += New-DeliveryEffect 'publication-2' $terminal2 2 21
                }
                $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=2; final_state_hash=hash-2; transition_count=2; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
                return $lines
            }

            $prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=publication-rollover-coordinator; session_id=publication-rollover-session; transfer_id=publication-rollover-transfer; direction=inbound"
            $start="event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=hash-0; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"

            foreach($outcome in @('Delivered','Rejected')){
                $lines=New-EvaluationLines $outcome $false
                $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                    (@('event=irrelevant') + @($lines))
                $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
                if($full.ArtifactEvidenceVerdict -ne 'pass' -or
                    $compact.ArtifactEvidenceVerdict -ne 'pass' -or
                    $full.BoundaryHash -ne $compact.BoundaryHash){
                    throw "$outcome rollover failed full/compact parity: $($full | ConvertTo-Json -Depth 8 -Compress)"
                }
            }

            $duplicateLines=[System.Collections.Generic.List[string]]::new()
            foreach($line in @(New-EvaluationLines 'Delivered' $false)){
                $duplicateLines.Add($line)
                if($line -like '*effect_identity=delivery-publication-2;*'){
                    $duplicateLines.Add($line)
                }
            }
            $duplicate=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                $duplicateLines.ToArray()
            if($duplicate.ArtifactEvidenceVerdict -ne 'pass'){
                throw "A byte-identical delivery-effect copy did not coalesce: $($duplicate | ConvertTo-Json -Depth 8 -Compress)"
            }

            $wrongEffectLines=@(New-EvaluationLines 'Delivered' $false | ForEach-Object {
                if($_ -like '*effect_identity=delivery-publication-2;*'){
                    $_.Replace(
                        'proof_publication_terminal|publication-authority|2|ProofPublication',
                        'proof_publication_terminal|publication-authority|1|ProofPublication').Replace(
                        'operation_generation=2;',
                        'operation_generation=1;').Replace(
                        'effect_generation=2;',
                        'effect_generation=1;').Replace(
                        'receipt_generation=2;',
                        'receipt_generation=1;').Replace(
                        'publication-receipt-fingerprint-2-Delivered',
                        'publication-receipt-fingerprint-1-Delivered')
                }
                else { $_ }
            })
            $wrongEffect=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                $wrongEffectLines
            if($wrongEffect.ArtifactEvidenceVerdict -ne 'fail' -or
                $wrongEffect.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_proof_publication_transition_invalid'){
                throw "An outgoing-terminal delivery effect did not fail closed: $($wrongEffect | ConvertTo-Json -Depth 8 -Compress)"
            }

            $malformed=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                (New-EvaluationLines 'Delivered' $true)
            if($malformed.ArtifactEvidenceVerdict -ne 'fail' -or
                $malformed.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_proof_publication_transition_invalid' -or
                $malformed.FirstUnresolvedBoundary.variant -ne
                    'valid_terminal_proof_publication_rollover_rejected_as_extra_operation'){
                throw "Malformed rollover did not fail closed: $($malformed | ConvertTo-Json -Depth 8 -Compress)"
            }

            $receipt1=New-PublicationReceipt 1 'Delivered' $true
            $pending1=New-PublicationOperation `
                'proof_publication_pending' 'Pending' 'None' 1 $null
            $terminal1=New-PublicationOperation `
                'proof_publication_terminal' 'None' 'Delivered' 1 $receipt1
            $arm=New-RecheckOperation 'None' 'Armed'
            $armLine=New-TransitionLine 'publication-arm' 0 1 `
                'hash-0' 'hash-1' `
                'filetransfer_receive_recovery_proof_state_publication_delivered' `
                @($pending1,$arm,$terminal1) 10
            $delivery1=New-DeliveryEffect 'publication-arm' $terminal1 1 11

            $closure=New-RecheckOperation 'Armed' 'None'
            $pending2=New-PublicationOperation `
                'proof_publication_pending' 'None' 'Pending' 2 $null
            $closureLine=New-TransitionLine 'publication-recheck' 1 2 `
                'hash-1' 'hash-2' `
                'filetransfer_receive_recovery_proof_state_republication_issued' `
                @($closure,$pending2) 20 `
                'ObserveReceiveRecoveryProofStatePublicationRecheckEvidence'

            $receipt2=New-PublicationReceipt 2 'Delivered' $true
            $pending2Retired=New-PublicationOperation `
                'proof_publication_pending' 'Pending' 'None' 2 $null
            $terminal1Retired=New-PublicationOperation `
                'proof_publication_terminal' 'Delivered' 'None' 1 $receipt1
            $terminal2=New-PublicationOperation `
                'proof_publication_terminal' 'None' 'Delivered' 2 $receipt2
            $deliveryLine=New-TransitionLine 'publication-recheck-delivered' 2 3 `
                'hash-2' 'hash-3' `
                'filetransfer_receive_recovery_proof_state_publication_delivered' `
                @($pending2Retired,$terminal1Retired,$terminal2) 30
            $delivery2=New-DeliveryEffect `
                'publication-recheck-delivered' $terminal2 3 31
            $completed3="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=hash-3; transition_count=3; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $recheckedLines=@(
                $start,$armLine,$delivery1,$closureLine,$deliveryLine,
                $delivery2,$completed3)
            $recheckedFull=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                (@('event=unrelated') + $recheckedLines)
            $recheckedCompact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                $recheckedLines
            if($recheckedFull.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $recheckedFull.FirstUnresolvedBoundary -or
                $recheckedFull.BoundaryHash -ne $recheckedCompact.BoundaryHash){
                throw "A consumed proof-publication recheck remained armed: $($recheckedFull | ConvertTo-Json -Depth 8 -Compress)"
            }

            $rejectedReceipt2=New-PublicationReceipt 2 'Rejected' $false
            $rejectedTerminal2=New-PublicationOperation `
                'proof_publication_terminal' 'None' 'Rejected' 2 `
                $rejectedReceipt2
            $rejectedLine=New-TransitionLine 'publication-recheck-rejected' 2 3 `
                'hash-2' 'hash-3' `
                'filetransfer_receive_recovery_proof_state_publication_rejected' `
                @($pending2Retired,$terminal1Retired,$rejectedTerminal2) 30
            $rejected=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$armLine,$delivery1,$closureLine,$rejectedLine,$completed3)
            if($rejected.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $rejected.FirstUnresolvedBoundary){
                throw "A consumed recheck with an exact Rejected publication stayed open: $($rejected | ConvertTo-Json -Depth 8 -Compress)"
            }

            $completed1="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=1; final_state_hash=hash-1; transition_count=1; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $open=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                @($start,$armLine,$delivery1,$completed1)
            if($open.ArtifactEvidenceVerdict -ne 'pass' -or
                $open.FirstUnresolvedBoundary.taxonomyClass -ne
                    'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed' -or
                $open.FirstUnresolvedBoundary.rawCount -ne 1 -or
                $open.FirstUnresolvedBoundary.distinctCount -ne 1 -or
                $open.FirstUnresolvedBoundary.recoveredCount -ne 0 -or
                $open.FirstUnresolvedBoundary.unresolvedCount -ne 1){
                throw "A singleton recheck arm did not remain unresolved: $($open | ConvertTo-Json -Depth 8 -Compress)"
            }

            $completed2="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=2; final_state_hash=hash-2; transition_count=2; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $missingDelivery=Get-FileTransferCoordinatorSnapshotEvaluation -Lines `
                @($start,$armLine,$delivery1,$closureLine,$completed2)
            if($missingDelivery.ArtifactEvidenceVerdict -ne 'pass' -or
                $missingDelivery.FirstUnresolvedBoundary.taxonomyClass -ne
                    'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing'){
                throw "A consumed recheck without successor delivery was not classified as publication missing: $($missingDelivery | ConvertTo-Json -Depth 8 -Compress)"
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Proof-publication rollover evaluator validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotReceiveProofDuplicateUsesExactRecoveryOperation()
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
            $scope = [ordered]@{
                route='regular_nkn_v4_fast'; protocolVersion=4
                liveRouteEpoch=0L; routePublicationGeneration=1L
                routeCommitGeneration=0L; runtimeGeneration=1L
                terminalGeneration=0L; legGeneration=1L
                transportEpoch=0L; bridgeRecoveryGeneration=1L
                checkpointGeneration=0L; checkpointRequestId='(none)'
                dataSessionIdentity='proof-duplicate-data-session'
                dataSessionGeneration=1L
            }
            $attempt=[ordered]@{
                stage='attempted_decision'
                commandIdentity='proof-duplicate-command'
                commandGeneration=1L
                commandFingerprint='proof-duplicate-command-fingerprint'
                receiptIdentity='proof-duplicate-receipt'
                receiptGeneration=1L
                receiptStatus='Idempotent'
                receiptReason='filetransfer_receive_recovery_proof_duplicate'
                receiptFingerprint='proof-duplicate-receipt-fingerprint'
                receiptValidated=$true
            }
            function New-ReceivePayload([string]$role) {
                [ordered]@{
                    role=$role; sourceBoundary=$scope; effectiveBoundary=$scope
                    recoveryKind='BridgeReceiveStallRecovery'
                    bridgeDecisionGeneration=1L
                    recoveryLivenessLeaseGeneration=1L
                    executionState='Completed'; closureState='Adopted'
                    closureGeneration=1L
                    proofIdentity='proof-duplicate-identity'
                    proofObservedUtcMs=100L
                    heldRetryAuthorityId='(none)'
                    reservedRetryAuthorityId='(none)'
                    initialActivationHoldGeneration=1L
                    envelopeRefreshGeneration=1L
                    checkpointBindingGeneration=0L
                    checkpointDeliveryGeneration=0L
                    checkpointResumeGeneration=0L
                    checkpointFrontierHoldGeneration=0L
                    checkpointFrontierRecheckGeneration=0L
                    receipts=@($attempt)
                }
            }
            function New-Operation(
                [string]$role,
                [string]$kind,
                [string]$key) {
                [ordered]@{
                    family='receive_recovery'; operationRole=$role
                    operationKey=$key; operationKind=$kind
                    authorityId='proof-duplicate-authority'
                    predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState='Completed:Adopted'
                    afterState='Completed:Adopted'
                    commandIdentity=$attempt.commandIdentity
                    commandGeneration=$attempt.commandGeneration
                    commandFingerprint=$attempt.commandFingerprint
                    receiptIdentity=$attempt.receiptIdentity
                    receiptGeneration=$attempt.receiptGeneration
                    receiptStatus=$attempt.receiptStatus
                    receiptFingerprint=$attempt.receiptFingerprint
                    receiptValidated=$true; stateApplied=$false
                    routeRuntime=$null; routeTransition=$null
                    runtimeUnlockRetry=$null
                    receiveRecovery=(New-ReceivePayload $role)
                    fallbackTailYield=$null
                    regularV4FrontierRepairPressure=$null
                    responderProbeHandoff=$null
                }
            }
            function New-Transition(
                [object]$operation,
                [string]$transitionId,
                [long]$writerSequence) {
                $envelope=[ordered]@{
                    snapshotContractVersion=1
                    snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId='proof-duplicate-coordinator'
                    sessionId='proof-duplicate-session'
                    transferId='proof-duplicate-transfer'
                    direction='outbound'; decisionUtcMilliseconds=20L
                    transitionId=$transitionId
                    decisionKind='ObserveReceiveRecoveryProof'
                    decisionStatus='Idempotent'
                    decisionReason='filetransfer_receive_recovery_proof_duplicate'
                    aggregate=[ordered]@{
                        beforeStateGeneration=0L; afterStateGeneration=0L
                        beforeStateFingerprint='proof-before-hash'
                        afterStateFingerprint='proof-before-hash'
                        beforeBoundary=$scope; afterBoundary=$scope
                        changedFamilies=@('receive_recovery')
                        operations=@($operation)
                    }
                }
                $json=$envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$transitionId; before_state_generation=0; after_state_generation=0; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=writer-sender; log_writer_sequence=$writerSequence"
            }

            $prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=proof-duplicate-coordinator; session_id=proof-duplicate-session; transfer_id=proof-duplicate-transfer; direction=outbound"
            $start="event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=proof-before-hash; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"
            $completed="event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=0; final_state_hash=proof-before-hash; transition_count=1; emitted_family_coverage=receive_recovery; shadow_family_coverage=; authoritative_family_coverage=receive_recovery; authority_effect=none"

            $operationKey='receive_recovery|operation_current|proof-duplicate-authority|1|BridgeReceiveStallRecovery'
            $operation=New-Operation 'operation_current' `
                'BridgeReceiveStallRecovery' $operationKey
            $transition=New-Transition $operation 'proof-duplicate-transition' 901
            $effect="event=filetransfer_coordinator_authority_effect; $prefix; transition_id=proof-duplicate-transition; coordinator_state_generation=0; operation_family=receive_recovery; operation_key=$operationKey; operation_authority_id=proof-duplicate-authority; operation_generation=1; effect_kind=receive_proof_observed; effect_identity=proof-duplicate-effect; effect_generation=1; receipt_identity=$($attempt.receiptIdentity); receipt_generation=1; receipt_status=Idempotent; receipt_fingerprint=$($attempt.receiptFingerprint); route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=1; checkpoint_generation=0; checkpoint_request_id=(none); data_session_identity=proof-duplicate-data-session; data_session_generation=1; authority_effect=applied"
            $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                'event=irrelevant',$start,$transition,$effect,$completed)
            $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$transition,$effect,$completed)
            if ($full.ArtifactEvidenceVerdict -ne 'pass' -or
                $compact.ArtifactEvidenceVerdict -ne 'pass' -or
                $full.BoundaryHash -ne $compact.BoundaryHash) {
                throw "Exact duplicate-proof operation failed: $($full | ConvertTo-Json -Depth 8 -Compress)"
            }

            $childKey='receive_recovery|initial_activation_envelope_refresh|proof-duplicate-authority|1|InitialActivationEnvelopeRefresh'
            $child=New-Operation 'initial_activation_envelope_refresh' `
                'InitialActivationEnvelopeRefresh' $childKey
            $malformedTransition=New-Transition $child 'proof-child-transition' 901
            $malformed=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$malformedTransition,$completed)
            if ($malformed.ArtifactEvidenceVerdict -ne 'fail' -or
                $malformed.FirstUnresolvedBoundary.decisionReason -ne
                    'snapshot_receive_recovery_required_effect_missing' -or
                $malformed.FirstUnresolvedBoundary.variant -ne
                    'initial_activation_envelope_refresh_lexically_selected_over_current_receive_recovery_operation' -or
                $malformed.FirstUnresolvedBoundary.writerSequence -ne 901L) {
                throw "Malformed child subject was not classified exactly: $($malformed | ConvertTo-Json -Depth 8 -Compress)"
            }

            $childEffect=$effect.Replace(
                'transition_id=proof-duplicate-transition',
                'transition_id=proof-child-transition').Replace(
                "operation_key=$operationKey",
                "operation_key=$childKey")
            $forged=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(
                $start,$malformedTransition,$childEffect,$completed)
            if ($forged.ArtifactEvidenceVerdict -ne 'fail' -or
                $forged.FirstUnresolvedBoundary.variant -ne
                    'initial_activation_envelope_refresh_lexically_selected_over_current_receive_recovery_operation') {
                throw "Child-bound proof effect was accepted: $($forged | ConvertTo-Json -Depth 8 -Compress)"
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Receive-proof subject evaluator validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    [Fact]
    public async Task FileTransferOps_CoordinatorSnapshotFallbackBootstrapRolloverIsExactAndTotal()
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
            function New-Scope([string]$route,[int]$protocol,[long]$epoch) {
                [ordered]@{
                    route=$route; protocolVersion=$protocol
                    liveRouteEpoch=$epoch; routePublicationGeneration=3L
                    routeCommitGeneration=2L; runtimeGeneration=4L
                    terminalGeneration=0L; legGeneration=4L
                    transportEpoch=0L; bridgeRecoveryGeneration=0L
                    checkpointGeneration=0L; checkpointRequestId='(none)'
                    dataSessionIdentity='data-session-1'; dataSessionGeneration=1L
                }
            }
            $sourceScope = New-Scope 'file_tuna_v4' 4 3

            function New-BootstrapOperation(
                [string]$authority,
                [long]$generation,
                [string]$beforeState,
                [string]$afterState,
                [bool]$delivered,
                [object]$source = $sourceScope) {
                $receipt = if ($delivered) {
                    @([ordered]@{
                        stage='fallback_v6_bootstrap'
                        commandIdentity=$authority; commandGeneration=$generation
                        commandFingerprint="command-$authority"
                        receiptIdentity=$authority; receiptGeneration=$generation
                        receiptStatus='Delivered'; receiptReason='delivered'
                        receiptFingerprint="receipt-$authority"
                        receiptValidated=$true
                    })
                }
                else { @() }
                [ordered]@{
                    family='route_transition'
                    operationRole='fallback_v6_bootstrap'
                    operationKey="route_transition|fallback_v6_bootstrap|$authority|$generation|FallbackV6ProofBootstrap"
                    operationKind='FallbackV6ProofBootstrap'
                    authorityId=$authority; predecessorAuthorityId='(none)'
                    operationGeneration=$generation
                    beforeState=$beforeState; afterState=$afterState
                    commandIdentity=$authority; commandGeneration=$generation
                    commandFingerprint="command-$authority"
                    receiptIdentity=if ($delivered) { $authority } else { '(none)' }
                    receiptGeneration=if ($delivered) { $generation } else { 0L }
                    receiptStatus=if ($delivered) { 'Delivered' } else { '(none)' }
                    receiptFingerprint=if ($delivered) { "receipt-$authority" } else { '(none)' }
                    receiptValidated=$delivered; stateApplied=$true
                    routeRuntime=$null
                    routeTransition=[ordered]@{
                        role='fallback_v6_bootstrap'
                        transitionKind='TunaV4ToPostTunaFallbackV6'
                        sourceBoundary=$source
                        targetBoundary=(New-Scope 'post_tuna_fallback_v6' 6 $generation)
                        sourceTransport='Tuna'
                        proofBoundaryKind='file_transfer_v4_state_frame_received'
                        proofAdmissionMode='fallback_v6_proof_bootstrap'
                        proofBoundaryFingerprint="proof-$authority"
                        durableCheckpointFingerprint="checkpoint-$authority"
                        targetRoute='post_tuna_fallback_v6'; targetProtocolVersion=6
                        targetTransport='RegularNkn'; handoffKind='TunaToNormalFallback'
                        dispositionKind='(none)'; retainedFrameType='(none)'
                        retainedFrameFingerprint='(none)'; receipts=@($receipt)
                    }
                }
            }

            function New-TransitionLine(
                [string]$prefix,
                [string]$id,
                [long]$beforeGeneration,
                [long]$afterGeneration,
                [string]$beforeHash,
                [string]$afterHash,
                [long]$time,
                [string]$kind,
                [string]$status,
                [object[]]$operations) {
                $envelope = [ordered]@{
                    snapshotContractVersion=1
                    snapshotSelectionMode='SnapshotV1Shadow'
                    coordinatorInstanceId=($prefix -replace '^.*coordinator_instance_id=([^;]+).*$','$1')
                    sessionId='session-1'; transferId='transfer-1'
                    direction=($prefix -replace '^.*direction=([^;]+).*$','$1')
                    transitionId=$id; decisionKind=$kind
                    decisionStatus=$status; decisionReason='test'
                    decisionUtcMilliseconds=$time
                    aggregate=[ordered]@{
                        beforeStateGeneration=$beforeGeneration
                        afterStateGeneration=$afterGeneration
                        beforeStateFingerprint=$beforeHash
                        afterStateFingerprint=$afterHash
                        beforeBoundary=$sourceScope; afterBoundary=$sourceScope
                        changedFamilies=@('route_transition')
                        operations=$operations
                    }
                }
                $json=$envelope | ConvertTo-Json -Depth 32 -Compress
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$id; before_state_generation=$beforeGeneration; after_state_generation=$afterGeneration; snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none"
            }

            function New-BootstrapStream([string]$direction,[string]$variant) {
                $instance="coordinator-$direction-$variant"
                $prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$instance; session_id=session-1; transfer_id=transfer-1; direction=$direction"
                $start="event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=h0; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                $gen1Pending=New-BootstrapOperation 'bootstrap-1' 1 'None' 'Pending' $false
                $gen1Delivered=New-BootstrapOperation 'bootstrap-1' 1 'Pending' 'Delivered' $true
                $gen1Retired=New-BootstrapOperation 'bootstrap-1' 1 'Delivered' 'None' $true
                $gen2Pending=New-BootstrapOperation 'bootstrap-2' 2 'None' 'Pending' $false
                $gen2Delivered=New-BootstrapOperation 'bootstrap-2' 2 'Pending' 'Delivered' $true
                $lines=@(
                    $start
                    (New-TransitionLine $prefix 'transition-1' 0 1 'h0' 'h1' 1 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Pending))
                    (New-TransitionLine $prefix 'transition-2' 1 2 'h1' 'h2' 2 'ApplyFileTransferFallbackV6ProofBootstrapReceipt' 'Accepted' @($gen1Delivered))
                )
                if ($variant -ceq 'complete') {
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Retired,$gen2Pending)
                    $lines += New-TransitionLine $prefix 'transition-4' 3 4 'h3' 'h4' 4 'ApplyFileTransferFallbackV6ProofBootstrapReceipt' 'Accepted' @($gen2Delivered)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=4; final_state_hash=h4; transition_count=4; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'missing-successor') {
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Retired)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=h3; transition_count=3; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'ambiguous-successor') {
                    $gen3Pending=New-BootstrapOperation 'bootstrap-3' 3 'None' 'Pending' $false
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Retired,$gen2Pending,$gen3Pending)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=h3; transition_count=3; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'mutated-successor') {
                    $mutated=New-Scope 'file_tuna_v4' 4 4
                    $mutatedPending=New-BootstrapOperation 'bootstrap-2' 2 'None' 'Pending' $false $mutated
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Retired,$mutatedPending)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=h3; transition_count=3; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'regressed-successor') {
                    $regressedPending=New-BootstrapOperation 'bootstrap-2' 1 'None' 'Pending' $false
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($gen1Retired,$regressedPending)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=h3; transition_count=3; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'missing-delivery-receipt') {
                    $missingReceipt=New-BootstrapOperation 'bootstrap-1' 1 'Pending' 'Delivered' $false
                    $lines=@($start,$lines[1],(New-TransitionLine $prefix 'transition-2' 1 2 'h1' 'h2' 2 'ApplyFileTransferFallbackV6ProofBootstrapReceipt' 'Accepted' @($missingReceipt)))
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=2; final_state_hash=h2; transition_count=2; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'retirement-without-delivery') {
                    $pendingRetired=New-BootstrapOperation 'bootstrap-1' 1 'Pending' 'None' $false
                    $lines=@($start,$lines[1],(New-TransitionLine $prefix 'transition-2' 1 2 'h1' 'h2' 2 'IssueFileTransferFallbackV6ProofBootstrap' 'Accepted' @($pendingRetired,$gen2Pending)))
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=2; final_state_hash=h2; transition_count=2; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'invalidation') {
                    $lines += New-TransitionLine $prefix 'transition-3' 2 3 'h2' 'h3' 3 'InvalidateFileTransferRouteTransition' 'Invalidated' @($gen1Retired)
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=3; final_state_hash=h3; transition_count=3; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                elseif ($variant -ceq 'pending') {
                    $lines=@($start,$lines[1])
                    $lines += "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=1; final_state_hash=h1; transition_count=1; emitted_family_coverage=route_runtime,route_transition; shadow_family_coverage=; authoritative_family_coverage=route_runtime,route_transition; authority_effect=none"
                }
                return @($lines)
            }

            foreach ($direction in @('outbound','inbound')) {
                $compact=@(New-BootstrapStream $direction 'complete')
                $full=@('event=unrelated') + $compact + @('event=other')
                $fullEvaluation=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $full
                $compactEvaluation=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compact
                $parity=Compare-FileTransferCoordinatorSnapshotEvaluations -Full $fullEvaluation -Compact $compactEvaluation
                if ($fullEvaluation.ArtifactEvidenceVerdict -ne 'pass' -or
                    $null -ne $fullEvaluation.FirstUnresolvedBoundary -or
                    $parity.Verdict -ne 'pass' -or
                    $fullEvaluation.BoundaryHash -cne $compactEvaluation.BoundaryHash) {
                    throw "A valid $direction bootstrap rollover did not close identically: $($fullEvaluation | ConvertTo-Json -Depth 8 -Compress)"
                }
                $duplicate=Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@($compact[0..2]) + @($compact[2]) + @($compact[3..5]))
                if ($duplicate.ArtifactEvidenceVerdict -ne 'pass') {
                    throw "A byte-identical bootstrap duplicate did not coalesce for $direction."
                }
            }

            foreach ($variant in @('missing-successor','ambiguous-successor','mutated-successor','regressed-successor','missing-delivery-receipt','retirement-without-delivery')) {
                $evaluation=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(New-BootstrapStream 'outbound' $variant)
                if ($evaluation.ArtifactEvidenceVerdict -ne 'fail' -or
                    $evaluation.ArtifactEvidenceFailureReason -notlike 'snapshot_fallback_v6_bootstrap_*') {
                    throw "Malformed bootstrap variant $variant did not fail closed: $($evaluation | ConvertTo-Json -Depth 8 -Compress)"
                }
            }
            $invalidated=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(New-BootstrapStream 'outbound' 'invalidation')
            if ($invalidated.ArtifactEvidenceVerdict -ne 'pass' -or
                $null -ne $invalidated.FirstUnresolvedBoundary) {
                throw 'An exact typed bootstrap invalidation did not close.'
            }
            $pending=Get-FileTransferCoordinatorSnapshotEvaluation -Lines @(New-BootstrapStream 'outbound' 'pending')
            if ($pending.ArtifactEvidenceVerdict -ne 'pass' -or
                $pending.FirstUnresolvedBoundary.taxonomyClass -ne
                    'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing' -or
                $pending.FirstUnresolvedBoundary.unresolvedCount -ne 1) {
                throw 'A genuinely pending bootstrap did not remain an unresolved runtime boundary.'
            }
            """;

        var result = await RunPowerShellScriptTextAsync(script, [repoRoot]);
        Assert.True(
            result.ExitCode == 0,
            $"Coordinator bootstrap rollover validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
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
            Assert.Contains("coordinator_snapshot_runtime_unlock_retry_family_legacy_selection_count", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_receive_recovery_family_legacy_selection_count", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_fallback_tail_yield_family_legacy_selection_count", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_regular_v4_frontier_repair_pressure_family_legacy_selection_count", text, StringComparison.Ordinal);
            Assert.Contains("coordinator_snapshot_responder_probe_handoff_family_legacy_selection_count", text, StringComparison.Ordinal);
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
        Assert.Contains(
            "coordinatorSnapshotRuntimeUnlockRetryFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinatorSnapshotReceiveRecoveryFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinatorSnapshotFallbackTailYieldFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinatorSnapshotRegularV4FrontierRepairPressureFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinatorSnapshotResponderProbeHandoffFamilyLegacySelectionCount",
            acceptanceText,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Family 'fallback_tail_yield'",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoordinatorSnapshotFallbackTailYieldFamilyLegacySelectionCount",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoordinatorSnapshotRegularV4FrontierRepairPressureFamilyLegacySelectionCount",
            guiText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoordinatorSnapshotResponderProbeHandoffFamilyLegacySelectionCount",
            guiText,
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
            $script:artifactInvalid = $false
            function Get-FileTransferCoordinatorSnapshotEvaluation {
                if ($script:artifactInvalid) {
                    return [pscustomobject]@{
                        Mode = 'SnapshotV1Authoritative'
                        ArtifactEvidenceVerdict = 'fail'
                        TransitionCount = 4L
                        FallbackTailYieldFamilyLegacySelectionCount = 0L
                        RegularV4FrontierRepairPressureFamilyLegacySelectionCount = 0L
                        ResponderProbeHandoffFamilyLegacySelectionCount = 0L
                        FirstUnresolvedBoundary = [pscustomobject]@{
                            boundaryId = 'invalid-pressure-boundary-8'
                            boundaryHash = 'invalid-pressure-boundary-hash-8'
                            families = @('regular_v4_frontier_repair_pressure')
                            taxonomyClass = 'filetransfer_coordinator_snapshot_evidence_invalid'
                            phase = 'evidence_finalization'
                            decisionReason = 'snapshot_regular_v4_repair_replay_authority_transition_invalid'
                            variant = 'specialized_family5_adoption_resurrected_settled_repair_authority'
                            rawCount = 2
                            distinctCount = 1
                            recoveredCount = 0
                            unresolvedCount = 1
                            writer = 'writer-helper'
                            writerSequence = 776L
                        }
                    }
                }
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

                $script:artifactInvalid = $true
                $invalid = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($invalid.ClassificationSource -ne 'coordinator_snapshot_evidence' -or
                    $invalid.FailureClass -ne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
                    $invalid.Phase -ne 'evidence_finalization' -or
                    $invalid.Reason -ne 'coordinator_snapshot_evidence_invalid' -or
                    $invalid.FailureVariant -ne 'specialized_family5_adoption_resurrected_settled_repair_authority' -or
                    $invalid.CoordinatorBoundaryId -ne 'invalid-pressure-boundary-8' -or
                    $invalid.CoordinatorBoundaryHash -ne 'invalid-pressure-boundary-hash-8' -or
                    $invalid.CoordinatorRawCount -ne 2 -or
                    $invalid.CoordinatorDistinctCount -ne 1 -or
                    $invalid.CoordinatorRecoveredCount -ne 0 -or
                    $invalid.CoordinatorUnresolvedCount -ne 1 -or
                    $invalid.CoordinatorWriter -ne 'writer-helper' -or
                    $invalid.CoordinatorWriterSequence -ne 776L -or
                    $invalid.CoordinatorSnapshotFallbackTailYieldFamilyLegacySelectionCount -ne 0L -or
                    $invalid.CoordinatorSnapshotRegularV4FrontierRepairPressureFamilyLegacySelectionCount -ne 0L -or
                    $invalid.CoordinatorSnapshotResponderProbeHandoffFamilyLegacySelectionCount -ne 0L) {
                    throw "GUI did not preserve the strict invalid boundary: $($invalid | ConvertTo-Json -Compress)"
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
    public async Task FileTransferOps_PreparationFirstResponderUsesSharedSnapshotEvaluation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-preparation-first", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = FileTransferCoordinatorTests.CreatePreparationFirstResponderSnapshotFixture()
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "PreparationFirstResponderSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Theory]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Outbound)]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Inbound)]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Outbound, true)]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Inbound, true)]
    public async Task FileTransferOps_FileTunaPressureSettlementUsesSharedSnapshotEvaluation(
        NLink.Core.FileTransfer.FileTransferDirection direction, bool crossRoute = false)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tuna-settlement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = (crossRoute
                    ? FileTransferCoordinatorTests.CreateCrossRoutePressureSettlementSnapshotFixture(direction)
                    : FileTransferCoordinatorTests.CreateFileTunaPressureSettlementSnapshotFixture(direction))
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "FileTunaPressureSettlementSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task FileTransferOps_ProofPublicationCancellationUsesParentRecoveryGeneration(long recoveryGeneration)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-proof-cancellation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = FileTransferCoordinatorTests.CreateProofPublicationCancellationSnapshotFixture(recoveryGeneration)
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "ProofPublicationCancellationSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Theory]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Outbound)]
    [InlineData(NLink.Core.FileTransfer.FileTransferDirection.Inbound)]
    public async Task FileTransferOps_RetainedHandoffReceiptUsesSharedSnapshotEvaluation(
        NLink.Core.FileTransfer.FileTransferDirection direction)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-retained-handoff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = FileTransferCoordinatorTests.CreateRetainedHandoffReceiptSnapshotFixture(direction)
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "FileTunaPressureSettlementSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileTransferOps_StagedRetryInstallReceiptUsesExactSharedValidation(bool fallback)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-staged-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = FileTransferCoordinatorTests.CreateStagedRetryInstallSnapshotFixture(fallback)
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "StagedRetryInstallSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileTransferOps_PendingScopeInvalidationUsesExactSharedValidation(bool fallback)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-pending-invalidation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = FileTransferCoordinatorTests.CreatePendingScopeInvalidationSnapshotFixture(fallback)
                .Select(NLink.Core.FileTransfer.SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "PendingScopeInvalidationSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath, tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    [Fact]
    public async Task FileTransferOps_Family2SnapshotCutoverGuiConsumesExactSharedBoundary()
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
            $script:family3 = $false
            $script:family4 = $false
            $script:family5 = $false
            $script:family6 = $false
            function Get-FileTransferCoordinatorSnapshotEvaluation {
                if ($script:family6) {
                    return [pscustomobject]@{
                        Mode = 'SnapshotV1Authoritative'
                        ArtifactEvidenceVerdict = 'pass'
                        TransitionCount = 6L
                        ResponderProbeHandoffFamilyLegacySelectionCount = 0L
                        FirstUnresolvedBoundary = [pscustomobject]@{
                            boundaryId = 'responder-boundary-6'
                            boundaryHash = 'responder-boundary-hash-6'
                            families = @('responder_probe_handoff')
                            taxonomyClass = 'runtime_unlock_responder_acked_probe_route_transition_handoff_missing'
                            phase = 'runtime_unlock_retry'
                            decisionReason = 'runtime_unlock_responder_peer_probe_handoff_owner_work_pending'
                            variant = 'after_bound_handoff_missing'
                            rawCount = 1
                            distinctCount = 1
                            recoveredCount = 0
                            unresolvedCount = 1
                            writer = 'writer-helper'
                            writerSequence = 7947L
                        }
                    }
                }
                if ($script:family5) {
                    return [pscustomobject]@{
                        Mode = 'SnapshotV1Shadow'
                        ArtifactEvidenceVerdict = 'pass'
                        TransitionCount = 5L
                        RegularV4FrontierRepairPressureFamilyLegacySelectionCount = 0L
                        FirstUnresolvedBoundary = [pscustomobject]@{
                            boundaryId = 'pressure-boundary-5'
                            boundaryHash = 'pressure-boundary-hash-5'
                            families = @('regular_v4_frontier_repair_pressure')
                            taxonomyClass = 'regular_v4_frontier_repair_pressure_authority_lifecycle_split'
                            phase = 'route_transition'
                            decisionReason = 'committed_route_transition_pressure_settlement_not_adopted_by_local_repair_lifecycle'
                            variant = '(none)'
                            rawCount = 8
                            distinctCount = 2
                            recoveredCount = 0
                            unresolvedCount = 2
                            writer = 'writer-helper'
                            writerSequence = 40653L
                        }
                    }
                }
                if ($script:family4) {
                    return [pscustomobject]@{
                        Mode = 'SnapshotV1Shadow'
                        ArtifactEvidenceVerdict = 'pass'
                        TransitionCount = 2L
                        FirstUnresolvedBoundary = [pscustomobject]@{
                            boundaryId = 'fallback-tail-boundary-4'
                            boundaryHash = 'fallback-tail-boundary-hash-4'
                            families = @('fallback_tail_yield')
                            taxonomyClass = 'fallback_tail_yield_authority_invalid'
                            phase = 'fallback_tail_yield'
                            decisionReason = 'paired_release_receipt_missing'
                            variant = '(none)'
                            rawCount = 1
                            distinctCount = 1
                            recoveredCount = 0
                            unresolvedCount = 1
                            writer = 'writer-helper'
                            writerSequence = 14938L
                        }
                    }
                }
                if ($script:family3) {
                    return [pscustomobject]@{
                        Mode = 'SnapshotV1Shadow'
                        ArtifactEvidenceVerdict = 'pass'
                        TransitionCount = 3L
                        FirstUnresolvedBoundary = [pscustomobject]@{
                            boundaryId = 'receive-boundary-3'
                            boundaryHash = 'receive-boundary-hash-3'
                            families = @('receive_recovery')
                            taxonomyClass = 'filetransfer_receive_recovery_operation_incomplete'
                            phase = 'bridge_recovery_execution'
                            decisionReason = 'receive_recovery_execution_still_running'
                            variant = '(none)'
                            rawCount = 1
                            distinctCount = 1
                            recoveredCount = 0
                            unresolvedCount = 1
                            writer = 'writer-helper'
                            writerSequence = 49857L
                        }
                    }
                }
                return [pscustomobject]@{
                    Mode = 'SnapshotV1Shadow'
                    ArtifactEvidenceVerdict = 'pass'
                    TransitionCount = 4L
                    FirstUnresolvedBoundary = [pscustomobject]@{
                        boundaryId = 'retry-boundary-11'
                        boundaryHash = 'retry-boundary-hash-11'
                        families = @('runtime_unlock_retry_lifecycle')
                        taxonomyClass = 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
                        phase = 'runtime_unlock_retry'
                        decisionReason = 'settled_missing_dispatch_predecessor_offer_not_retired_to_wake_exact_successor'
                        variant = '(none)'
                        rawCount = 2
                        distinctCount = 1
                        recoveredCount = 0
                        unresolvedCount = 1
                        writer = 'writer-sender'
                        writerSequence = 14073L
                    }
                }
            }
            function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative {
                param($Evaluation, [string]$Family)
                return $Family -in @(
                    'route_runtime',
                    'route_transition',
                    'runtime_unlock_retry_lifecycle',
                    'receive_recovery',
                    'fallback_tail_yield',
                    'regular_v4_frontier_repair_pressure',
                    'responder_probe_handoff')
            }

            $root = Join-Path ([IO.Path]::GetTempPath()) (
                'nlink-family2-gui-boundary-' + [guid]::NewGuid().ToString('N'))
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
                    $actual.FailureClass -ne 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch' -or
                    $actual.Phase -ne 'runtime_unlock_retry' -or
                    $actual.CoordinatorBoundaryId -ne 'retry-boundary-11' -or
                    $actual.CoordinatorWriterSequence -ne 14073L) {
                    throw "GUI did not preserve the Family 2 shared boundary: $($actual | ConvertTo-Json -Compress)"
                }
                $script:family3 = $true
                $family3 = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($family3.ClassificationSource -ne 'coordinator_snapshot_authority_v1' -or
                    $family3.FailureClass -ne 'filetransfer_receive_recovery_operation_incomplete' -or
                    $family3.Phase -ne 'bridge_recovery_execution' -or
                    $family3.CoordinatorBoundaryId -ne 'receive-boundary-3' -or
                    $family3.CoordinatorWriterSequence -ne 49857L) {
                    throw "GUI did not preserve the Family 3 shared boundary: $($family3 | ConvertTo-Json -Compress)"
                }
                $script:family3 = $false
                $script:family4 = $true
                $family4 = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($family4.ClassificationSource -ne 'coordinator_snapshot_authority_v1' -or
                    $family4.FailureClass -ne 'fallback_tail_yield_authority_invalid' -or
                    $family4.Phase -ne 'fallback_tail_yield' -or
                    $family4.CoordinatorBoundaryId -ne 'fallback-tail-boundary-4' -or
                    $family4.CoordinatorWriterSequence -ne 14938L) {
                    throw "GUI did not preserve the Family 4 shared boundary: $($family4 | ConvertTo-Json -Compress)"
                }
                $script:family4 = $false
                $script:family5 = $true
                $family5 = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($family5.ClassificationSource -ne 'coordinator_snapshot_authority_v1' -or
                    $family5.FailureClass -ne 'regular_v4_frontier_repair_pressure_authority_lifecycle_split' -or
                    $family5.Phase -ne 'route_transition' -or
                    $family5.Reason -ne 'committed_route_transition_pressure_settlement_not_adopted_by_local_repair_lifecycle' -or
                    $family5.CoordinatorBoundaryId -ne 'pressure-boundary-5' -or
                    $family5.CoordinatorBoundaryHash -ne 'pressure-boundary-hash-5' -or
                    $family5.CoordinatorRawCount -ne 8 -or
                    $family5.CoordinatorDistinctCount -ne 2 -or
                    $family5.CoordinatorRecoveredCount -ne 0 -or
                    $family5.CoordinatorUnresolvedCount -ne 2 -or
                    $family5.CoordinatorWriter -ne 'writer-helper' -or
                    $family5.CoordinatorWriterSequence -ne 40653L -or
                    $family5.CoordinatorSnapshotRegularV4FrontierRepairPressureFamilyLegacySelectionCount -ne 0L) {
                    throw "GUI did not preserve the Family 5 shared boundary: $($family5 | ConvertTo-Json -Compress)"
                }
                $script:family5 = $false
                $script:family6 = $true
                $family6 = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'scenario failed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($family6.ClassificationSource -ne 'coordinator_snapshot_authority_v1' -or
                    $family6.FailureClass -ne 'runtime_unlock_responder_acked_probe_route_transition_handoff_missing' -or
                    $family6.Phase -ne 'runtime_unlock_retry' -or
                    $family6.Reason -ne 'runtime_unlock_responder_peer_probe_handoff_owner_work_pending' -or
                    $family6.FailureVariant -ne 'after_bound_handoff_missing' -or
                    $family6.CoordinatorBoundaryId -ne 'responder-boundary-6' -or
                    $family6.CoordinatorBoundaryHash -ne 'responder-boundary-hash-6' -or
                    $family6.CoordinatorRawCount -ne 1 -or
                    $family6.CoordinatorDistinctCount -ne 1 -or
                    $family6.CoordinatorRecoveredCount -ne 0 -or
                    $family6.CoordinatorUnresolvedCount -ne 1 -or
                    $family6.CoordinatorWriter -ne 'writer-helper' -or
                    $family6.CoordinatorWriterSequence -ne 7947L -or
                    $family6.CoordinatorSnapshotResponderProbeHandoffFamilyLegacySelectionCount -ne 0L) {
                    throw "GUI did not preserve the Family 6 shared boundary: $($family6 | ConvertTo-Json -Compress)"
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
            $"GUI Family 2 shared-boundary validation failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
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
