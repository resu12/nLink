using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "WarningPolicy")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5WarningPolicyTests : FileTransferAcceptancePhase5TestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CompletedFallbackWarningVariance_ProjectsEffectiveVerdictWithoutHidingFailures(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // Explicit projection inputs, not synthetic evidence admitted as a live run.
        const string script = """
            param([string]$RepoRoot, [string]$Directory)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$null, [ref]$errors)
            if ($errors.Count) { throw 'wrapper_parse_failed' }
            foreach ($definition in $ast.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.FunctionDefinitionAst]
            }) { . ([scriptblock]::Create($definition.Extent.Text)) }
            $script:RouteAcceptanceExecutionMode = 'live'
            $GoodputRegressionTolerancePercent = 10D
            $scenario = [pscustomobject]@{ Name='live-switch-off-helper-64mb'; Kind='tuna'; ExpectedRouteChanges=@('file_tuna_v4','post_tuna_fallback_v6') }
            $proofs = @('routeConsistencyVerdict','liveRouteEpochProofVerdict','fallbackLegAuthorityProofVerdict',
                'cleanFallbackLegProofVerdict','bridgeLivenessIntegrationVerdict','controlPlaneIsolationVerdict')
            $variants = @('accepted','not_applied','hard_failure','other_failure','untrusted','cursor_gap','writer_gap','manifest_missing','provenance_missing',
                'sha_mismatch','incomplete','other_candidate','incomplete_candidate','semantic_unavailable') +
                @($proofs | ForEach-Object { "missing_$_"; "failed_$_" })
            foreach ($variant in $variants) {
                $result = New-RouteAcceptanceRunResult -Name $scenario.Name -ArtifactDir $Directory -ExpectedRoute file_tuna_v4 -ExpectedProtocol 4
                $result.completed = $true
                $result.integrityOk = $true
                $result.shaOk = $true
                $result.route = 'file_tuna_v4,post_tuna_fallback_v6'
                foreach ($proof in $proofs) { $result.$proof = 'pass' }
                $result.operatorVerdict = 'FAIL_EXTERNAL_TRANSPORT_CHURN'
                $result.warningCount = 2
                $result.warningKinds = 'fallback_frontier_repair_churn,fallback_receiver_state_churn'
                $result.warningCapExceededKinds = 'fallback_frontier_repair_churn'
                $result.goodputRegressionWarning = 'advisory switch-off goodput warning'
                $result.runtimeFailureCandidate = 'warning_policy'
                $result.firstProofChainFailureClass = 'warning_policy'
                Add-RouteAcceptanceFailure -Result $result -Message 'warning cap exceeded: fallback_frontier_repair_churn'
                Add-RouteAcceptanceFailure -Result $result -Message 'operator verdict is not accepted'
                $script:RouteAcceptanceEvidence = [pscustomobject]@{
                    ManifestPresent=$true; ManifestPath='projection-fixture'; ManifestSha256='fixture'
                    ManifestValidationVerdict='pass'; ProvenanceVerdict='pass'; ProvenanceFailureReasons=@()
                    CurrentRuntimeProof=$true; CurrentRuntimeProofReasons=@()
                }
                $ledger = [pscustomobject]@{ cursorIntegrityVerdict='pass'; cursorIntegrityReasons=@() }
                foreach ($property in $result.PSObject.Properties | Where-Object { $_.Name -like 'writer*' }) {
                    $ledger | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
                }
                $ledger.writerEvidenceVersion = 3L
                $ledger.writerIntegrityVerdict = $ledger.writerRecoveryVerdict = $ledger.writerChronologyVerdict = 'pass'
                switch ($variant) {
                    'hard_failure' { $result.hardFailureCount = 1; Add-RouteAcceptanceFailure -Result $result -Message 'operator hard failures observed: 1' }
                    'other_failure' { Add-RouteAcceptanceFailure -Result $result -Message 'required authenticated Tuna delivery missing' }
                    'untrusted' { $script:RouteAcceptanceEvidence.CurrentRuntimeProof = $false }
                    'cursor_gap' { $ledger.cursorIntegrityVerdict = 'fail'; $ledger.cursorIntegrityReasons = @('gap') }
                    'writer_gap' { $ledger.writerIntegrityVerdict = 'fail'; $ledger.writerGapCount = 1L }
                    'manifest_missing' { $script:RouteAcceptanceEvidence.ManifestValidationVerdict = 'missing' }
                    'provenance_missing' { $script:RouteAcceptanceEvidence.ProvenanceVerdict = 'missing' }
                    'sha_mismatch' { $result.shaOk = $false }
                    'incomplete' { $result.completed = $false }
                    'other_candidate' { $result.runtimeFailureCandidate = $result.firstProofChainFailureClass = 'active_file_tuna_v6_evidence' }
                    'incomplete_candidate' { $result.runtimeFailureCandidate = $result.firstProofChainFailureClass = 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete' }
                    'semantic_unavailable' { $result.guiReportedFailurePhase = 'semantic_analysis'; $result.guiReportedFailureReason = 'evidence_enrichment_timeout'; $result.guiReportedFailureError = 'evidence_enrichment_timeout' }
                }
                foreach ($proof in $proofs) {
                    if ($variant -ceq "missing_$proof") { $result.$proof = '(missing)' }
                    if ($variant -ceq "failed_$proof") { $result.$proof = 'fail' }
                }
                if ($variant -cne 'not_applied') { Set-Phase5CompletedLiveFallbackWarningVariance -Scenario $scenario -Result $result | Out-Null }
                $candidateBefore = $result.runtimeFailureCandidate
                $failuresBefore = @($result.failures)
                $evidence = @([pscustomobject]@{
                    artifactDir=$Directory; selected=$true; finalizationValidation=[pscustomobject]@{isFinalized=$true}
                    logLedger=$ledger
                })
                Set-RouteAcceptanceResultEvidence -Result $result -ScenarioEvidence $evidence
                $dto = New-RouteAcceptanceSummaryRunDto -Result $result -AcceptancePhase phase5
                if ($variant -ceq 'accepted') {
                    if ($dto.runtimeCorrectnessVerdict -cne 'pass' -or $dto.runtimeFailureCandidate -cne '(none)' -or
                        $dto.firstProofChainFailureClass -cne '(none)' -or $dto.acceptanceFailureClass -cne 'none' -or
                        $dto.evidenceClassification -cne 'current_runtime_proof' -or $dto.correctnessFailureCount -ne 0) {
                        throw 'accepted_warning_still_reported_as_runtime_failure'
                    }
                    $accepted = $result
                }
                elseif ($dto.runtimeCorrectnessVerdict -ceq 'pass' -or $dto.runtimeFailureCandidate -cne $candidateBefore) {
                    throw "failure_or_missing_proof_hidden: $variant"
                }
                if ($variant -in @('untrusted','cursor_gap','incomplete','incomplete_candidate','semantic_unavailable') -and
                    $dto.runtimeCorrectnessVerdict -cne 'incomplete_proof') { throw "incomplete_proof_lost: $variant" }
                if ($dto.operatorVerdict -cne 'FAIL_EXTERNAL_TRANSPORT_CHURN' -or $dto.warningCount -ne 2 -or
                    $dto.warningKinds -cne 'fallback_frontier_repair_churn,fallback_receiver_state_churn' -or
                    $dto.warningCapExceededKinds -cne 'fallback_frontier_repair_churn') { throw "raw_warning_changed: $variant" }
                if ($variant -cne 'semantic_unavailable' -and ($failuresBefore -join '|') -cne ($result.failures -join '|')) {
                    throw "acceptance_failures_changed_by_projection: $variant"
                }
            }
            # Exercise the real aggregate writer, including its text and JSON projections.
            foreach ($prefix in @('fileTransferFallbackFrontierStallGraceRecheckMissing','fileTransferFallbackFrontierRecentProgressGraceRecheckMissing')) {
                $accepted.($prefix + 'AccountingAvailability') = 'valid'
                $accepted.($prefix + 'AccountingVersion') = 2
            }
            $script:RunResults = @($accepted)
            Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $Directory -BaselinePath fixture -AcceptancePhase phase5 -ExpectedRunCount 1 -RequestedScenarios @($scenario.Name) | Out-Null
            $summary = Get-Content -LiteralPath (Join-Path $Directory 'phase4-ab-acceptance-summary.json') -Raw | ConvertFrom-Json
            $text = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Directory 'phase4-ab-acceptance-summary.txt')
            if ($summary.verdict -cne 'PASS' -or $summary.performanceVerdict -cne 'WARN' -or $summary.runtimeCorrectnessVerdict -cne 'pass' -or
                $summary.runtimeFailureCandidate -cne '(none)' -or $summary.acceptanceFailureClass -cne '(none)' -or
                $text['runtime_correctness_verdict'] -cne 'pass' -or $text['runtime_failure_candidate'] -cne '(none)' -or
                $summary.runs[0].operatorVerdict -cne 'FAIL_EXTERNAL_TRANSPORT_CHURN') { throw 'aggregate_projection_disagrees' }
            'fallback-warning-projection-ok'
            """;
        var directory = Path.Combine(Path.GetTempPath(), "nlink-warning-projection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), directory], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("fallback-warning-projection-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task RouteAcceptanceWarningPolicy_EmptyKindsRejectWithoutThrowing(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$null, [ref]$errors)
            if ($errors.Count -gt 0) { throw ($errors -join ';') }
            $definition = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq 'Test-RouteAcceptanceAllowedOperatorWarning'
            }, $true))
            if ($definition.Count -ne 1) { throw 'Warning policy function is not unique.' }
            . ([scriptblock]::Create($definition[0].Extent.Text))
            $AllowExternalTransportWarnings = $true
            foreach ($verdict in @('PASS', 'WARN_EXTERNAL_TRANSPORT', 'FAIL_PROTOCOL_OR_INTEGRITY', 'INCONCLUSIVE')) {
                if (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute file_tuna_v4 -Verdict $verdict -WarningKinds @()) {
                    throw "Empty warnings accepted: $verdict"
                }
            }
            if (-not (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute file_tuna_v4 -Verdict WARN_EXTERNAL_TRANSPORT -WarningKinds @('external_transport_churn'))) {
                throw 'Existing allowed warning was rejected.'
            }
            if (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute file_tuna_v4 -Verdict WARN_EXTERNAL_TRANSPORT -WarningKinds @('unknown_warning')) {
                throw 'Unknown warning was accepted.'
            }
            $AllowExternalTransportWarnings = $false
            if (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute file_tuna_v4 -Verdict WARN_EXTERNAL_TRANSPORT -WarningKinds @('external_transport_churn')) {
                throw 'Disabled warning policy accepted a warning.'
            }
            "warning_policy_empty_kinds_rejected"
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("warning_policy_empty_kinds_rejected", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CompletedRegularExternalTransportCapExcessIsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-completed-external-churn");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-completed-external-churn");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected completed regular-NKN Phase 5 external churn to be classified as environmental, not protocol/integrity.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.completed"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.sha_ok"]);
            Assert.Equal("external_transport_churn", summary["regular-nkn-v4-64mb.warning_cap_exceeded_kinds"]);
            Assert.Equal("live_transport_external_churn_completed_clean", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("incomplete_proof", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.current_runtime_proof"]);
            Assert.Contains("execution_mode_not_live", summary["regular-nkn-v4-64mb.current_runtime_proof_reasons"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CompletedLiveFallbackWarningCapExcessIsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-live-fallback-completed-warning-churn");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-live-fallback-completed-warning-churn");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "live-switch-off-helper-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected completed live-fallback Phase 5 warning cap noise to be classified as environmental, not correctness failure.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.completed"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.sha_ok"]);
            Assert.Equal("external_transport_churn", summary["live-switch-off-helper-64mb.warning_cap_exceeded_kinds"]);
            Assert.Equal("live_transport_recovered_fallback_churn_completed_clean", summary["live-switch-off-helper-64mb.environmental_classification"]);
            Assert.Equal("incomplete_proof", summary["live-switch-off-helper-64mb.acceptance_failure_class"]);
            Assert.Equal("0", summary["live-switch-off-helper-64mb.current_runtime_proof"]);
            Assert.Contains("execution_mode_not_live", summary["live-switch-off-helper-64mb.current_runtime_proof_reasons"], StringComparison.Ordinal);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.bridge_liveness_integration_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}
