namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "RerunPolicy")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRecoveryRetryPolicyTests : FileTransferOpsScriptsTestBase
{
    // Explicit policy inputs, not an admitted analyzer result or live authority proof.
    private const string Harness = """
        param([string]$RepoRoot, [string]$Directory, [string]$Variant)
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$null, [ref]$errors)
        if ($errors.Count) { throw 'wrapper_parse_failed' }
        foreach ($definition in $ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.FunctionDefinitionAst]
        }) { . ([scriptblock]::Create($definition.Extent.Text)) }
        $script:RouteAcceptanceExecutionMode = 'evidence_dry_run'
        $script:RouteAcceptanceRetainedMarkerIndexCache = @{}
        """;

    [Theory]
    [InlineData("startup")]
    [InlineData("post_artifact")]
    [InlineData("persistent")]
    public async Task StartupRecoveryPolicy_ClassifiesMeasurementWithoutAdmittingProof(string variant)
    {
        const string body = """
            @('data_protocol_version=4','verdict=FAIL_PROTOCOL_OR_INTEGRITY','cycles_observed=0',
                'bridge_bulk_queue_clear_count=2','v4_feedback_both_failed_count=0',
                'gui_progress_timeout_count=0','terminal_missing_after_progress_timeout=0') |
                Set-Content (Join-Path $Directory 'filetransfer-live-nkn-summary.txt')
            'bridge bulk send failure/clear' | Set-Content (Join-Path $Directory 'stability-gates-summary.txt')
            $attempts = if ($Variant -ceq 'persistent') { 2 } else { 1 }
            for ($attempt = 1; $attempt -le $attempts; $attempt++) {
                $result = New-RouteAcceptanceRunResult -Name regular-nkn-v4-64mb -ArtifactDir $Directory -ExpectedRoute regular_nkn_v4_fast -ExpectedProtocol 4
                $result.finalRoute = 'regular_nkn_v4_fast'
                $result.operatorVerdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
                $result.hardFailureCount = 1
                Add-RouteAcceptanceFailure -Result $result -Message 'bridge bulk send failure/clear'
                Set-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $result
                if (-not $result.measurementContaminated -or $result.environmentalClassification -cne 'regular_v4_transport_recovery_storm') { throw 'storm_not_classified' }
                Add-RouteAcceptanceFailure -Result $result -Message 'measurement contaminated: regular_v4_transport_recovery_storm'
                if ($Variant -ceq 'post_artifact') { Add-RouteAcceptanceFailure -Result $result -Message 'scenario execution failed: injected after artifacts' }
                if (-not (Test-Phase4RerunnableMeasurementFailure -Result $result)) { throw 'classified_measurement_not_retryable' }
                if ($result.completed -or $result.integrityOk -or $result.currentRuntimeProof) { throw 'classification_granted_completion' }
                Add-RouteAcceptanceFailure -Result $result -Message 'missing route publication authority'
                if (Test-Phase4RerunnableMeasurementFailure -Result $result) { throw 'missing_authority_became_measurement_retry' }
            }
            $clean = New-RouteAcceptanceRunResult -Name regular-nkn-v4-64mb -ArtifactDir $Directory -ExpectedRoute regular_nkn_v4_fast -ExpectedProtocol 4
            if (Test-Phase4RerunnableMeasurementFailure -Result $clean) { throw 'clean_result_retried' }
            'bridge bulk send failure/clear; hard protocol/integrity event: invalid frame' |
                Set-Content (Join-Path $Directory 'stability-gates-summary.txt')
            if (Test-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $result) { throw 'protocol_failure_classified_as_variance' }
            if (Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts -ArtifactDir $Directory) { throw 'policy_fixture_admitted_as_proof' }
            'recovery-policy-ok'
            """;
        await VerifyPolicyAsync(body, variant);
    }

    [Theory]
    [InlineData("exhaustion")]
    [InlineData("liveness")]
    [InlineData("persistent")]
    public async Task CanonicalRecoveryPolicy_PreservesIncompleteProofAndRequiresRecoveryEvidence(string variant)
    {
        const string body = """
            $candidate = if ($Variant -ceq 'liveness') { 'runtime_unlock_liveness_timeout' } else { 'runtime_unlock_offer_observation_blocked_by_receive_recovery' }
            $operator = @("recovery_failure_class=$candidate",'runtime_unlock_offer_not_observed_count=1',
                'runtime_unlock_retry_dispatched_count=1','runtime_unlock_retry_authority_observed_count=1',
                'runtime_unlock_offer_observation_blocked_count=1','session_liveness_timeout_after_runtime_unlock_count=1')
            $operator | Set-Content (Join-Path $Directory 'filetransfer-operator-verdict.txt')
            'bridge_recovery_receive_resumed_count=0' | Set-Content (Join-Path $Directory 'filetransfer-route-consistency-summary.txt')
            $log = Join-Path $Directory 'filetransfer-retained-log-slice.log'
            @('event=tuna_activation_control_send_waiting_for_regular_v4_recovery',
                'event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed',
                'event=nkn_bridge_receive_stall_recovery_completed; stall_reason=regular_v4_unproven_recovery_escalation; requires_control_proof=1; requires_bulk_proof=1',
                'event=nkn_bridge_receive_stall_recovery_failed; reason=max_restarts_reached') | Set-Content $log
            $scenario = [pscustomobject]@{Name='regular-v4-live-activation-off-on-off-256mb';Kind='tuna'}
            $attempts = if ($Variant -ceq 'persistent') { 2 } else { 1 }
            for ($attempt = 1; $attempt -le $attempts; $attempt++) {
                $result = New-RouteAcceptanceRunResult -Name $scenario.Name -ArtifactDir $Directory -ExpectedRoute regular_nkn_v4_fast -ExpectedProtocol 4
                $result.routeConsistencyVerdict = 'pass'
                $result.finalRoute = 'regular_nkn_v4_fast'
                $result.route = 'regular_nkn_v4_fast'
                $result.selectedRouteChanges = @('regular_nkn_v4_fast')
                $result.runtimeFailureCandidate = $candidate
                if (-not (Set-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $scenario -Result $result)) { throw 'recovery_not_classified' }
                if ($result.environmentalClassification -cne 'live_transport_receive_recovery_exhausted_before_runtime_unlock' -or -not $result.measurementContaminated) { throw 'recovery_classification_missing' }
                if ((Get-RouteAcceptanceResultFailureClass -AcceptancePhase phase5 -Result $result) -cne 'incomplete_proof') { throw 'environmental_label_overrode_incomplete_proof' }
                if ($result.completed -or $result.integrityOk -or $result.currentRuntimeProof) { throw 'classification_granted_completion' }
            }
            ($operator -replace 'runtime_unlock_retry_authority_observed_count=1','runtime_unlock_retry_authority_observed_count=0') |
                Set-Content (Join-Path $Directory 'filetransfer-operator-verdict.txt')
            if (Test-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $scenario -Result $result) { throw 'missing_authority_observation_accepted' }
            $operator | Set-Content (Join-Path $Directory 'filetransfer-operator-verdict.txt')
            'event=nkn_bridge_receive_stall_recovery_receive_resumed' | Add-Content $log
            $script:RouteAcceptanceRetainedMarkerIndexCache.Clear()
            if (Test-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $scenario -Result $result) { throw 'resumed_receive_classified_as_exhausted' }
            if (Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts -ArtifactDir $Directory) { throw 'policy_fixture_admitted_as_proof' }
            'recovery-policy-ok'
            """;
        await VerifyPolicyAsync(body, variant);
    }

    private static async Task VerifyPolicyAsync(string body, string variant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), "nlink-recovery-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body, [FindRepoRoot(), root, variant]);
            Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains("recovery-policy-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally { TryDeleteDirectory(root); }
    }
}
