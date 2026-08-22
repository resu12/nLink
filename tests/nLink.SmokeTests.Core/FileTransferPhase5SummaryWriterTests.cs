using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "SummaryWriter")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5SummaryWriterTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    public async Task RunFileTransferRouteAcceptance_EfficiencySummaryValidationSeparatesPerformanceFromCorrectness()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var assertionBody = ExtractPowerShellFunctionBody(
            script,
            "Assert-Phase4EfficiencySummary",
            "Test-RouteAcceptanceSemanticAnalysisUnavailableFailure");
        var performanceBody = ExtractPowerShellFunctionBody(
            script,
            "Test-Phase4PerformanceFailureLine",
            "Get-Phase4FailureClass");
        var summaryRunDtoBody = ExtractPowerShellFunctionBody(
            script,
            "New-RouteAcceptanceSummaryRunDto",
            "Write-Phase4RouteAcceptanceSummaryFiles");
        var summaryRunDtoSetupEnd = summaryRunDtoBody.IndexOf("return [pscustomobject]", StringComparison.Ordinal);
        Assert.True(summaryRunDtoSetupEnd > 0, "Expected the summary DTO failure projection before its return value.");
        var summaryRunDtoSetupBody = summaryRunDtoBody[..summaryRunDtoSetupEnd];

        Assert.Contains("efficiencySummaryValidationVerdict = [string]$Result.efficiencySummaryValidationVerdict", script, StringComparison.Ordinal);
        Assert.Contains("efficiencyVerdict = $efficiencyVerdict", script, StringComparison.Ordinal);
        Assert.Contains("{0}.efficiency_failure_class={1}", script, StringComparison.Ordinal);
        Assert.Contains("{0}.fallback_tail_efficiency_treadmill_unresolved_count={1}", script, StringComparison.Ordinal);

        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Assert-Phase4EfficiencySummary { {{assertionBody}} }
            function Test-Phase4PerformanceFailureLine { {{performanceBody}} }
            function ConvertTo-RouteAcceptanceSummaryStringArray {
                param($Values, [int]$Limit)
                return @($Values | Select-Object -First $Limit)
            }
            function Get-TestSummaryFailureProjection {
                {{summaryRunDtoSetupBody}}
                return [pscustomobject]@{
                    failures = @($resultFailureLines)
                    performanceFailureCount = $resultPerformanceFailureCount
                    correctnessFailureCount = $resultCorrectnessFailureCount
                }
            }
            function Read-RouteAcceptanceKeyValueArtifact {
                param([string]$Path)
                $values = @{}
                if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $values }
                foreach ($line in Get-Content -LiteralPath $Path) {
                    $separator = $line.IndexOf('=')
                    if ($separator -gt 0) {
                        $values[$line.Substring(0, $separator).Trim()] = $line.Substring($separator + 1).Trim()
                    }
                }
                return $values
            }
            function Get-RouteAcceptanceReportValue {
                param($Report, [string]$Name, [string]$DefaultValue = '')
                if ($Report.ContainsKey($Name)) { return [string]$Report[$Name] }
                return $DefaultValue
            }
            function Add-RouteAcceptanceFailure {
                param($Result, [string]$Message)
                [void]$Result.failures.Add($Message)
            }
            function New-TestResult {
                param([string]$ArtifactDir)
                return [pscustomobject]@{
                    artifactDir = $ArtifactDir
                    baselineGoodputBytesPerSecond = 0D
                    efficiencySummaryValidationVerdict = 'not_evaluated'
                    efficiencyVerdict = 'not_evaluated'
                    efficiencyFailureClass = 'none'
                    efficiencyPostYieldBoundarySequence = 0L
                    efficiencyPostYieldRecoveryActivityCount = 0L
                    efficiencyPostYieldRepeatedUnchangedFrontierTupleCount = 0L
                    efficiencyPostYieldScopedRecoveryEffectCount = 0L
                    efficiencyPostYieldScopedRecoveryDistinctOperationCount = 0L
                    efficiencyPostYieldExactWindowCount = 0L
                    efficiencyPostYieldScopedPressureEffectCount = 0L
                    efficiencyDuplicateStateReplayAuthorityCount = 0L
                    fallbackTailEfficiencyTreadmillRawCount = 0L
                    fallbackTailEfficiencyTreadmillDistinctCount = 0L
                    fallbackTailEfficiencyTreadmillRecoveredCount = 0L
                    fallbackTailEfficiencyTreadmillUnresolvedCount = 0L
                    fallbackTailEfficiencyTreadmillVerdict = 'none'
                    failures = [System.Collections.Generic.List[string]]::new()
                }
            }
            function Write-TestEfficiencySummary {
                param(
                    [string]$ArtifactDir,
                    [string]$Verdict,
                    [string]$FailureClass,
                    [string]$RecoveryCount = '4',
                    [string]$TreadmillEffect = '',
                    [string]$DuplicateReplayCount = '0')
                if ([string]::IsNullOrWhiteSpace($TreadmillEffect)) {
                    $TreadmillEffect = if ($Verdict -eq 'fail' -and
                        $FailureClass -eq 'fallback_tail_efficiency_treadmill') { '1' } else { '0' }
                }
                $treadmillVerdict = if ($TreadmillEffect -eq '1') { 'fail' } else { 'none' }
                @(
                    "efficiency_verdict=$Verdict",
                    "efficiency_failure_class=$FailureClass",
                    'post_yield_boundary_sequence=10529',
                    "post_yield_recovery_activity_count=$RecoveryCount",
                    'post_yield_repeated_unchanged_frontier_tuple_count=2',
                    "post_yield_scoped_recovery_effect_count=$RecoveryCount",
                    'post_yield_scoped_recovery_distinct_operation_count=2',
                    'post_yield_exact_window_count=1',
                    "post_yield_scoped_pressure_effect_count=$treadmillEffect",
                    "post_tuna_fallback_duplicate_state_replay_authority_count=$DuplicateReplayCount",
                    "fallback_tail_efficiency_treadmill_raw_count=$treadmillEffect",
                    "fallback_tail_efficiency_treadmill_distinct_count=$treadmillEffect",
                    'fallback_tail_efficiency_treadmill_recovered_count=0',
                    "fallback_tail_efficiency_treadmill_unresolved_count=$treadmillEffect",
                    "fallback_tail_efficiency_treadmill_verdict=$treadmillVerdict") |
                    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-efficiency-summary.txt')
            }

            $root = Join-Path ([IO.Path]::GetTempPath()) ('nlink-efficiency-propagation-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($root)
            try {
                $cases = @(
                    [pscustomobject]@{ Name = 'pass'; Verdict = 'pass'; Class = 'none'; Recovery = '4'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'warning'; Verdict = 'warning'; Class = 'efficiency_warning'; Recovery = '8'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'not_evaluated'; Verdict = 'not_evaluated_due_correctness_failure'; Class = 'none'; Recovery = '0'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'fail'; Verdict = 'fail'; Class = 'fallback_tail_efficiency_treadmill'; Recovery = '75'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'duplicate_fail'; Verdict = 'fail'; Class = 'filetransfer_post_tuna_fallback_duplicate_state_replay_amplification'; Recovery = '4'; Treadmill = '0'; Duplicate = '1'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'mixed'; Verdict = 'fail'; Class = 'fallback_tail_efficiency_treadmill'; Recovery = '75'; Treadmill = ''; Duplicate = '0'; Correctness = $true; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'contradictory_treadmill'; Verdict = 'warning'; Class = 'efficiency_warning'; Recovery = '8'; Treadmill = '1'; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'contradictory_duplicate'; Verdict = 'pass'; Class = 'none'; Recovery = '4'; Treadmill = '0'; Duplicate = '1'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'missing'; Verdict = ''; Class = ''; Recovery = '0'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $false },
                    [pscustomobject]@{ Name = 'malformed'; Verdict = 'fail'; Class = 'fallback_tail_efficiency_treadmill'; Recovery = 'NaN'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true },
                    [pscustomobject]@{ Name = 'negative'; Verdict = 'warning'; Class = 'efficiency_warning'; Recovery = '-1'; Treadmill = ''; Duplicate = '0'; Correctness = $false; WriteSummary = $true }
                )
                $output = foreach ($case in $cases) {
                    $dir = Join-Path $root $case.Name
                    [void][IO.Directory]::CreateDirectory($dir)
                    Set-Content -LiteralPath (Join-Path $dir 'filetransfer-operator-verdict.txt') -Value 'verdict=PASS'
                    if ($case.WriteSummary) {
                        Write-TestEfficiencySummary -ArtifactDir $dir -Verdict $case.Verdict -FailureClass $case.Class -RecoveryCount $case.Recovery -TreadmillEffect $case.Treadmill -DuplicateReplayCount $case.Duplicate
                    }
                    $result = New-TestResult -ArtifactDir $dir
                    Assert-Phase4EfficiencySummary -Result $result
                    if ($case.Correctness) {
                        Add-RouteAcceptanceFailure -Result $result -Message 'terminal evidence missing'
                    }
                    $performanceFailures = @($result.failures | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) })
                    [pscustomobject]@{
                        name = $case.Name
                        validation = $result.efficiencySummaryValidationVerdict
                        verdict = $result.efficiencyVerdict
                        failureClass = $result.efficiencyFailureClass
                        boundary = $result.efficiencyPostYieldBoundarySequence
                        recovery = $result.efficiencyPostYieldRecoveryActivityCount
                        scopedRecovery = $result.efficiencyPostYieldScopedRecoveryEffectCount
                        exactWindowCount = $result.efficiencyPostYieldExactWindowCount
                        treadmillUnresolved = $result.fallbackTailEfficiencyTreadmillUnresolvedCount
                        treadmillVerdict = $result.fallbackTailEfficiencyTreadmillVerdict
                        failureCount = $result.failures.Count
                        performanceFailureCount = $performanceFailures.Count
                        correctnessFailureCount = $result.failures.Count - $performanceFailures.Count
                    }
                }
                $wideResult = [pscustomobject]@{
                    failures = [System.Collections.Generic.List[string]]::new()
                }
                foreach ($index in 1..140) {
                    [void]$wideResult.failures.Add("correctness-$index")
                }
                [void]$wideResult.failures.Add('efficiency_verdict=fail; efficiency_failure_class=fallback_tail_efficiency_treadmill')
                $wideProjection = Get-TestSummaryFailureProjection -Result $wideResult -AcceptancePhase phase5
                $output = @($output) + [pscustomobject]@{
                    name = 'wide_mixed'
                    validation = 'pass'
                    verdict = 'fail'
                    failureClass = 'fallback_tail_efficiency_treadmill'
                    boundary = 0
                    recovery = 0
                    scopedRecovery = 0
                    exactWindowCount = 0
                    treadmillUnresolved = 1
                    treadmillVerdict = 'fail'
                    failureCount = $wideProjection.failures.Count
                    performanceFailureCount = $wideProjection.performanceFailureCount
                    correctnessFailureCount = $wideProjection.correctnessFailureCount
                    retainedEfficiencyFailure = @($wideProjection.failures | Where-Object { $_ -like 'efficiency_verdict=fail*' }).Count
                }
                $output | ConvertTo-Json -Depth 4 -Compress
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        var execution = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            execution.ExitCode == 0,
            $"Expected efficiency propagation contract to execute.{Environment.NewLine}STDOUT:{Environment.NewLine}{execution.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{execution.Stderr}");

        using var document = JsonDocument.Parse(execution.Stdout.Trim());
        var cases = document.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => item.Clone(),
            StringComparer.Ordinal);

        Assert.Equal("pass", cases["pass"].GetProperty("validation").GetString());
        Assert.Equal(0, cases["pass"].GetProperty("failureCount").GetInt32());
        Assert.Equal("warning", cases["warning"].GetProperty("verdict").GetString());
        Assert.Equal(0, cases["warning"].GetProperty("failureCount").GetInt32());
        Assert.Equal("not_evaluated_due_correctness_failure", cases["not_evaluated"].GetProperty("verdict").GetString());
        Assert.Equal(0, cases["not_evaluated"].GetProperty("failureCount").GetInt32());

        Assert.Equal("pass", cases["fail"].GetProperty("validation").GetString());
        Assert.Equal("fallback_tail_efficiency_treadmill", cases["fail"].GetProperty("failureClass").GetString());
        Assert.Equal(10529L, cases["fail"].GetProperty("boundary").GetInt64());
        Assert.Equal(75L, cases["fail"].GetProperty("recovery").GetInt64());
        Assert.Equal(75L, cases["fail"].GetProperty("scopedRecovery").GetInt64());
        Assert.Equal(1L, cases["fail"].GetProperty("exactWindowCount").GetInt64());
        Assert.Equal(1L, cases["fail"].GetProperty("treadmillUnresolved").GetInt64());
        Assert.Equal("fail", cases["fail"].GetProperty("treadmillVerdict").GetString());
        Assert.Equal(1, cases["fail"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(0, cases["fail"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal("pass", cases["duplicate_fail"].GetProperty("validation").GetString());
        Assert.Equal(1, cases["duplicate_fail"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(0, cases["duplicate_fail"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal(1, cases["mixed"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(1, cases["mixed"].GetProperty("correctnessFailureCount").GetInt32());

        Assert.Equal("malformed", cases["contradictory_treadmill"].GetProperty("validation").GetString());
        Assert.Equal(1, cases["contradictory_treadmill"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal("malformed", cases["contradictory_duplicate"].GetProperty("validation").GetString());
        Assert.Equal(1, cases["contradictory_duplicate"].GetProperty("correctnessFailureCount").GetInt32());

        Assert.Equal("missing", cases["missing"].GetProperty("validation").GetString());
        Assert.Equal(0, cases["missing"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(1, cases["missing"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal("malformed", cases["malformed"].GetProperty("validation").GetString());
        Assert.Equal(0, cases["malformed"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(1, cases["malformed"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal("malformed", cases["negative"].GetProperty("validation").GetString());
        Assert.Equal(0, cases["negative"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(1, cases["negative"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal(1, cases["wide_mixed"].GetProperty("performanceFailureCount").GetInt32());
        Assert.Equal(140, cases["wide_mixed"].GetProperty("correctnessFailureCount").GetInt32());
        Assert.Equal(128, cases["wide_mixed"].GetProperty("failureCount").GetInt32());
        Assert.Equal(1, cases["wide_mixed"].GetProperty("retainedEfficiencyFailure").GetInt32());
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ProofStatePublicationCountersExistOnFatalRunResults()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var constructorStart = script.IndexOf(
            "function New-RouteAcceptanceRunResult",
            StringComparison.Ordinal);
        var constructorEnd = script.IndexOf(
            "function Add-RouteAcceptanceFailure",
            constructorStart,
            StringComparison.Ordinal);

        Assert.True(constructorStart >= 0);
        Assert.True(constructorEnd > constructorStart);
        var constructor = script[constructorStart..constructorEnd];

        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = 0",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = 0",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = 0",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = 0",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount = 0",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedUnresolvedCount = 0",
            constructor,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ProofStateRecheckHeartbeatSuppressionCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionVerdict = if ($fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressedUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RouteTransitionTransportGraceCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = [int]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount += [int]$runDto.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = if ($fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_SubmissionBarrierDrainCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = if ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_CompletedUnprovenSuccessorCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = if ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_CompletedProvenSuccessorCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = if ($fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$Result.regularV4FrontierRepairStalledRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "regularV4FrontierRepairStalledRawCount = [int]$Result.regularV4FrontierRepairStalledRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$regularV4FrontierRepairStalledRawCount += [int]$runDto.regularV4FrontierRepairStalledRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.regular_v4_frontier_repair_stalled_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "regularV4FrontierRepairStalledVerdict = if ($regularV4FrontierRepairStalledUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ReceiveRecoveryOperationCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryOperationRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryOperationRawCount = [int]$Result.fileTransferReceiveRecoveryOperationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryOperationRawCount += [int]$runDto.fileTransferReceiveRecoveryOperationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "filetransfer_receive_recovery_operation_unresolved_count={0}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryOperationVerdict = if ($fileTransferReceiveRecoveryOperationUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SingleScenarioSummaryUsesFilteredRunCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-single-scenario");

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-single-scenario"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake single-scenario Phase 5 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("1", summary["expected_run_count"]);
            Assert.Equal("regular-v4-live-activation-off-on-off-256mb", summary["requested_scenarios"]);
            Assert.Equal("0", summary["summary_json_failed"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            const string scenarioPrefix = "regular-v4-live-activation-off-on-off-256mb.";
            string[] efficiencyTextFields =
            [
                "efficiency_summary_validation_verdict",
                "efficiency_verdict",
                "efficiency_failure_class",
                "post_yield_boundary_sequence",
                "post_yield_recovery_activity_count",
                "post_yield_repeated_unchanged_frontier_tuple_count",
                "post_yield_scoped_recovery_effect_count",
                "post_yield_scoped_recovery_distinct_operation_count",
                "post_yield_exact_window_count",
                "post_yield_scoped_pressure_effect_count",
                "post_tuna_fallback_duplicate_state_replay_authority_count",
                "fallback_tail_efficiency_treadmill_raw_count",
                "fallback_tail_efficiency_treadmill_distinct_count",
                "fallback_tail_efficiency_treadmill_recovered_count",
                "fallback_tail_efficiency_treadmill_unresolved_count",
                "fallback_tail_efficiency_treadmill_verdict"
            ];
            foreach (var field in efficiencyTextFields)
            {
                Assert.True(summary.ContainsKey(field), $"Expected aggregate TXT efficiency field '{field}'.");
                Assert.True(summary.ContainsKey(scenarioPrefix + field), $"Expected per-run TXT efficiency field '{field}'.");
                Assert.Equal(summary[field], summary[scenarioPrefix + field]);
            }
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Matches("^[0-9a-f]{64}$", summary["manifest_sha256"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("complete", summary["transfer_evidence_status"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count"]);
            Assert.Equal(
                "none",
                summary["filetransfer_receive_recovery_completed_unproven_successor_verdict"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_proven_successor_missing_raw_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count"]);
            Assert.Equal(
                "none",
                summary["filetransfer_receive_recovery_completed_proven_successor_verdict"]);
            Assert.Equal(
                "0",
                summary[
                    "regular-v4-live-activation-off-on-off-256mb." +
                    "filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count"]);
            Assert.Equal(
                "0",
                summary[
                    "regular-v4-live-activation-off-on-off-256mb." +
                    "filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_stalled_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_stalled_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_stalled_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_stalled_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_stalled_verdict"]);
            Assert.Equal(
                "0",
                summary[
                    "regular-v4-live-activation-off-on-off-256mb." +
                    "regular_v4_frontier_repair_stalled_unresolved_count"]);
            Assert.Equal(
                "none",
                summary[
                    "regular-v4-live-activation-off-on-off-256mb." +
                    "regular_v4_frontier_repair_stalled_verdict"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_projection_reauthorization_verdict"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal("none", summary["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count"]);
            Assert.Equal("none", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.manifest_present"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.cursor_integrity_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.current_runtime_proof"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_warning_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_efficiency_class"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict"]);
            var manifestPath = Path.Combine(runRoot, "route-acceptance-run-manifest.json");
            Assert.True(File.Exists(manifestPath), "Expected immutable route-acceptance manifest.");
            await using (var manifestStream = File.OpenRead(manifestPath))
            using (var manifest = await JsonDocument.ParseAsync(manifestStream))
            {
                Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("fake", manifest.RootElement.GetProperty("executionMode").GetString());
                Assert.False(manifest.RootElement.GetProperty("validation").GetProperty("currentRuntimeProof").GetBoolean());
                Assert.Single(manifest.RootElement.GetProperty("scenarioAttempts").EnumerateArray());
            }
            var summaryJsonPath = Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json");
            Assert.True(File.Exists(summaryJsonPath), "Expected Phase 5 JSON summary.");
            await using (var summaryJsonStream = File.OpenRead(summaryJsonPath))
            using (var summaryJson = await JsonDocument.ParseAsync(summaryJsonStream))
            {
                var root = summaryJson.RootElement;
                Assert.Equal("none", root.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());

                var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
                string[] efficiencyJsonProperties =
                [
                    "efficiencySummaryValidationVerdict",
                    "efficiencyVerdict",
                    "efficiencyFailureClass",
                    "postYieldBoundarySequence",
                    "postYieldRecoveryActivityCount",
                    "postYieldRepeatedUnchangedFrontierTupleCount",
                    "postYieldScopedRecoveryEffectCount",
                    "postYieldScopedRecoveryDistinctOperationCount",
                    "postYieldExactWindowCount",
                    "postYieldScopedPressureEffectCount",
                    "postTunaFallbackDuplicateStateReplayAuthorityCount",
                    "fallbackTailEfficiencyTreadmillRawCount",
                    "fallbackTailEfficiencyTreadmillDistinctCount",
                    "fallbackTailEfficiencyTreadmillRecoveredCount",
                    "fallbackTailEfficiencyTreadmillUnresolvedCount",
                    "fallbackTailEfficiencyTreadmillVerdict"
                ];
                foreach (var property in efficiencyJsonProperties)
                {
                    Assert.Equal(
                        root.GetProperty(property).GetRawText(),
                        run.GetProperty(property).GetRawText());
                }
                Assert.Equal("none", run.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());
            }
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-summary-writer-diagnostics.txt")), "Expected Phase 5 summary writer diagnostics.");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-summary-writer-diagnostics.json")), "Expected Phase 5 summary writer JSON diagnostics.");

            var diagnostics = ReadArtifactReport(runRoot, "phase5-summary-writer-diagnostics.txt");
            Assert.Equal("regular-v4-live-activation-off-on-off-256mb", diagnostics["requested_scenarios"]);
            Assert.Equal("1", diagnostics["expected_run_count"]);
            Assert.Equal("1", diagnostics["observed_run_count"]);
            Assert.Equal("0", diagnostics["summary_json_failed"]);
            Assert.Equal("PASS", diagnostics["final_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_CurrentListenerAdoptionCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-current-listener-adoption-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-current-listener-adoption-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_CURRENT_LISTENER_ADOPTION_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake current-listener-adoption propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("2", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count"]);
            Assert.Equal("2", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count"]);
            Assert.Equal("3", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count"]);
            Assert.Equal("2", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_retry_dispatch_current_listener_adoption_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count"]);
            Assert.Equal("3", summary["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_retry_dispatch_current_listener_adoption_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_retry_dispatch_current_listener_adoption_verdict"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionMissingUnresolvedCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionLiveProjectionMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionLiveProjectionMissingUnresolvedCount").GetInt32());
            Assert.Equal(3, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredRawCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredDistinctCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionVerdict").GetString());
            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal("runtime_unlock_retry_dispatch_current_listener_adoption_missing", run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionMissingUnresolvedCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionLiveProjectionMissingUnresolvedCount").GetInt32());
            Assert.Equal(3, run.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchCurrentListenerAdoptionRestartExpiredUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ResponderAckedProbeHandoffVariantsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_responder_acked_probe_route_transition_handoff_missing";
        const string failureVariant = "runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-responder-acked-probe-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-responder-acked-probe-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RESPONDER_ACKED_PROBE_HANDOFF_SUMMARY_FIXTURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_SHA_FAIL"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")),
                $"Expected fake responder ACKed-probe summary propagation output.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_unresolved_count"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count"]);
            Assert.Equal("none", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_binding_missing_unresolved_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_recovered_count"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_unresolved_count"]);
            Assert.Equal("2", evidence["filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count"]);
            Assert.Equal(failureVariant, evidence["failure_variant"]);

            using (var compact = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(
                           runRoot,
                           scenario,
                           "filetransfer-retained-compact-summary.json"))))
            {
                Assert.Equal("compact", compact.RootElement.GetProperty("retained_analysis_mode").GetString());
                Assert.Equal(candidate, compact.RootElement.GetProperty("runtime_failure_candidate").GetString());
                Assert.Equal(failureVariant, compact.RootElement.GetProperty("failure_variant").GetString());
            }

            using (var diagnostics = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(
                           runRoot,
                           scenario,
                           "filetransfer-retained-analysis-diagnostics.json"))))
            {
                Assert.Equal(0, diagnostics.RootElement.GetProperty("timed_out").GetInt32());
                Assert.Equal(0, diagnostics.RootElement.GetProperty("exit_code").GetInt32());
            }

            using (var stageDiagnostics = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(
                           runRoot,
                           scenario,
                           "filetransfer-retained-analysis-stage-diagnostics.json"))))
            {
                Assert.Equal(1, stageDiagnostics.RootElement.GetProperty("compact_requested").GetInt32());
                Assert.Equal(1, stageDiagnostics.RootElement.GetProperty("compact_mode").GetInt32());
                Assert.Equal("completed", stageDiagnostics.RootElement.GetProperty("correlator_status").GetString());
                Assert.True(stageDiagnostics.RootElement.GetProperty("event_count").GetInt64() > 0);
                Assert.Equal(
                    stageDiagnostics.RootElement.GetProperty("event_count").GetInt64(),
                    stageDiagnostics.RootElement.GetProperty("indexed_event_count").GetInt64());
            }

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["current_runtime_proof"]);
            Assert.Equal("pass", summary["manifest_validation_verdict"]);
            Assert.Equal("pass", summary["provenance_verdict"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("3", summary["writer_evidence_version"]);
            Assert.Equal("pass", summary["writer_integrity_verdict"]);
            Assert.Equal("pass", summary["writer_recovery_verdict"]);
            Assert.Equal("pass", summary["writer_chronology_verdict"]);
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal(failureVariant, summary["failure_variant"]);
            Assert.Equal("route_runtime", summary["acceptance_failure_class"]);
            Assert.Equal("incomplete", summary["transfer_evidence_status"]);
            Assert.Equal("fail", summary["runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("85724", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("37479", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("0", summary["runtime_unlock_responder_acked_probe_binding_missing_unresolved_count"]);
            Assert.Equal("1", summary["runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count"]);
            Assert.Equal("0", summary["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_unresolved_count"]);
            Assert.Equal("2", summary["filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal(failureVariant, summary[$"{scenario}.failure_variant"]);
            Assert.Equal("1", summary[$"{scenario}.current_runtime_proof"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete", summary[$"{scenario}.transfer_evidence_status"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_responder_acked_probe_binding_missing_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count"]);
            Assert.Equal("2", summary[$"{scenario}.filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(failureVariant, root.GetProperty("failureVariant").GetString());
            Assert.Equal("route_runtime", root.GetProperty("acceptanceFailureClass").GetString());
            Assert.True(root.GetProperty("currentRuntimeProof").GetBoolean());
            Assert.Equal("incomplete", root.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("fail", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(0, root.GetProperty("runtimeUnlockResponderPeerProbeBindingMissingUnresolvedCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbeRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbeDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbeRecoveredCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbeUnresolvedCount").GetInt32());
            Assert.Equal(2, root.GetProperty("fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(failureVariant, run.GetProperty("failureVariant").GetString());
            Assert.True(run.GetProperty("currentRuntimeProof").GetBoolean());
            Assert.Equal("route_runtime", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete", run.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("fail", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(0, run.GetProperty("runtimeUnlockResponderPeerProbeBindingMissingUnresolvedCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbeRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbeDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbeRecoveredCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbeUnresolvedCount").GetInt32());
            Assert.Equal(2, run.GetProperty("fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_InitialResponderPeerAcceptanceScopeVariantReachesTrustedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_responder_acked_probe_route_transition_handoff_missing";
        const string failureVariant = "runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-initial-responder-peer-scope-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-initial-responder-peer-scope-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RESPONDER_INITIAL_PEER_ACCEPTANCE_SCOPE_REJECTED_SUMMARY_FIXTURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_SHA_FAIL"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")),
                $"Expected initial responder scope summary propagation output.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_route_transition_handoff_missing_unresolved_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict"]);
            Assert.Equal("0", evidence["runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count"]);
            Assert.Equal(failureVariant, evidence["failure_variant"]);
            Assert.Equal("runtime_unlock_peer_precommit_answer_acceptance_observed", evidence["first_proof_chain_failure_event"]);
            Assert.Equal("20396", evidence["first_proof_chain_failure_sequence"]);
            Assert.Equal("7493", evidence["first_proof_chain_failure_writer_sequence"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["current_runtime_proof"]);
            Assert.Equal("pass", summary["manifest_validation_verdict"]);
            Assert.Equal("pass", summary["provenance_verdict"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("3", summary["writer_evidence_version"]);
            Assert.Equal("pass", summary["writer_integrity_verdict"]);
            Assert.Equal("pass", summary["writer_recovery_verdict"]);
            Assert.Equal("pass", summary["writer_chronology_verdict"]);
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal(failureVariant, summary["failure_variant"]);
            Assert.Equal("route_runtime", summary["acceptance_failure_class"]);
            Assert.Equal("fail", summary["runtime_correctness_verdict"]);
            Assert.Equal("1", summary["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(failureVariant, root.GetProperty("failureVariant").GetString());
            Assert.Equal("route_runtime", root.GetProperty("acceptanceFailureClass").GetString());
            Assert.True(root.GetProperty("currentRuntimeProof").GetBoolean());
            Assert.Equal("fail", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejectedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejectedVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(failureVariant, run.GetProperty("failureVariant").GetString());
            Assert.Equal("route_runtime", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejectedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejectedVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ConclusiveRouteRuntimeRequiresTrustedCompletedCompactEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var incompleteBody = ExtractPowerShellFunctionBody(
            script,
            "Test-RouteAcceptanceIncompleteProofRuntimeCandidate",
            "Test-RouteAcceptanceConclusiveRouteRuntimeCandidate");
        var candidateBody = ExtractPowerShellFunctionBody(
            script,
            "Test-RouteAcceptanceConclusiveRouteRuntimeCandidate",
            "Test-RouteAcceptanceConclusiveRouteRuntimeEvidence");
        var evidenceBody = ExtractPowerShellFunctionBody(
            script,
            "Test-RouteAcceptanceConclusiveRouteRuntimeEvidence",
            "Set-RouteAcceptanceResultEvidence");
        var setEvidenceBody = ExtractPowerShellFunctionBody(
            script,
            "Set-RouteAcceptanceResultEvidence",
            "Complete-RouteAcceptanceRunEvidence");
        var semanticUnavailableBody = ExtractPowerShellFunctionBody(
            script,
            "Test-RouteAcceptanceSemanticAnalysisUnavailableFailure",
            "Assert-Phase4ScenarioRun");
        var failureClassBody = ExtractPowerShellFunctionBody(
            script,
            "Get-RouteAcceptanceResultFailureClass",
            "Assert-Phase5ScenarioRun");

        Assert.DoesNotContain("active_file_tuna_v6_evidence", failureClassBody, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime_unlock_responder_acked_probe_route_transition_handoff_missing',", failureClassBody, StringComparison.Ordinal);
        Assert.Contains("Test-RouteAcceptanceConclusiveRouteRuntimeEvidence", failureClassBody, StringComparison.Ordinal);
        Assert.Contains("$selectedRuntimeEvidenceIncomplete", script, StringComparison.Ordinal);
        Assert.Contains("$runtimeCandidateDtos[0].runtimeCorrectnessVerdict", script, StringComparison.Ordinal);

        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            $script:RouteAcceptanceExecutionMode = 'live'
            function Test-RouteAcceptanceIncompleteProofRuntimeCandidate { {{incompleteBody}} }
            function Test-RouteAcceptanceConclusiveRouteRuntimeCandidate { {{candidateBody}} }
            function Test-RouteAcceptanceConclusiveRouteRuntimeEvidence { {{evidenceBody}} }
            function Set-RouteAcceptanceResultEvidence { {{setEvidenceBody}} }
            function Test-RouteAcceptanceSemanticAnalysisUnavailableFailure { {{semanticUnavailableBody}} }
            function Get-RouteAcceptanceResultFailureClass { {{failureClassBody}} }
            function Get-Phase5FailureClass { return 'protocol_or_integrity' }
            function ConvertTo-RouteAcceptanceInt {
                param($Value)
                $parsed = 0L
                if ([long]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }
                return 0L
            }
            function Get-JsonPropertyValue {
                param($Object, [string]$Name, $DefaultValue = $null)
                if ($null -eq $Object) { return $DefaultValue }
                $property = $Object.PSObject.Properties[$Name]
                if ($null -eq $property) { return $DefaultValue }
                return $property.Value
            }
            function Read-RouteAcceptanceKeyValueArtifact {
                param([string]$Path)
                $report = @{}
                foreach ($line in Get-Content -LiteralPath $Path) {
                    $separator = $line.IndexOf('=')
                    if ($separator -gt 0) { $report[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
                }
                return $report
            }
            function Get-RouteAcceptanceReportValue {
                param($Report, [string]$Name, [string]$DefaultValue = '')
                if ($Report.ContainsKey($Name)) { return [string]$Report[$Name] }
                return $DefaultValue
            }
            function Add-RouteAcceptanceFailure {
                param($Result, [string]$Message)
                [void]$Result.failures.Add($Message)
            }
            function Test-Phase4PerformanceFailureLine { param([string]$Line) return $false }
            function Get-RouteAcceptanceEvidenceSummaryState {
                return [pscustomobject]@{
                    ManifestPresent = $true
                    ManifestPath = 'manifest.json'
                    ManifestSha256 = 'manifest-sha'
                    ManifestValidationVerdict = 'pass'
                    ProvenanceVerdict = 'pass'
                    ProvenanceFailureReasons = @()
                    CurrentRuntimeProof = $true
                    CurrentRuntimeProofReasons = @()
                }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-route-runtime-trust-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $candidate = 'filetransfer_bridge_recovery_execution_payload_hold_missing'
                $compact = [ordered]@{
                    retained_analysis_mode = 'compact'
                    first_proof_chain_failure_class = $candidate
                    first_proof_chain_failure_event = 'filetransfer_v4_sender_failed'
                    first_proof_chain_failure_reason = 'payload_hold_not_applied'
                    first_proof_chain_failure_sequence = 92177
                    first_proof_chain_failure_writer_sequence = 50849
                    semantic_failure_domain = 'route_runtime'
                }
                $compact["${candidate}_raw_count"] = 2
                $compact["${candidate}_distinct_count"] = 1
                $compact["${candidate}_recovered_count"] = 0
                $compact["${candidate}_unresolved_count"] = 1
                $compact | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-compact-summary.json')
                @{ timed_out = 0; exit_code = 0 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-analysis-diagnostics.json')
                @{ compact_requested = 1; compact_mode = 1; correlator_status = 'completed'; event_count = 20; indexed_event_count = 20 } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-analysis-stage-diagnostics.json')
                @(
                    "first_proof_chain_failure_class=$candidate",
                    'first_proof_chain_failure_event=filetransfer_v4_sender_failed',
                    'first_proof_chain_failure_reason=payload_hold_not_applied',
                    'first_proof_chain_failure_sequence=92177',
                    'first_proof_chain_failure_writer_sequence=50849',
                    'semantic_failure_domain=route_runtime',
                    "${candidate}_raw_count=2",
                    "${candidate}_distinct_count=1",
                    "${candidate}_recovered_count=0",
                    "${candidate}_unresolved_count=1") |
                    Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-operator-verdict.txt')
                Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-log-slice.log') -Value 'event=evidence'

                function New-TrustResult {
                    return [pscustomobject]@{
                        artifactDir = $tempRoot
                        runtimeFailureCandidate = $candidate
                        failureVariant = '(none)'
                        semanticFailureDomain = 'route_runtime'
                        firstProofChainFailureClass = $candidate
                        firstProofChainFailureEvent = 'filetransfer_v4_sender_failed'
                        firstProofChainFailureReason = 'payload_hold_not_applied'
                        firstProofChainFailureSequence = 92177L
                        firstProofChainFailureWriterSequence = 50849L
                        manifestPresent = $false
                        manifestPath = ''
                        manifestSha256 = ''
                        currentRuntimeProof = $false
                        currentRuntimeProofReasons = @()
                        manifestValidationVerdict = 'missing'
                        provenanceVerdict = 'missing'
                        provenanceFailureReasons = @()
                        cursorIntegrityVerdict = 'missing'
                        writerEvidenceVersion = 0L
                        writerIntegrityVerdict = 'missing'
                        writerGapCount = 0L
                        writerDuplicateCount = 0L
                        writerIdentityMismatchCount = 0L
                        writerWriteFailureCount = 0L
                        writerDeferredBatchCount = 0L
                        writerRecoveredBatchCount = 0L
                        writerUnresolvedDeferredBatchCount = 0L
                        writerTerminalFailureCount = 0L
                        writerRecoveryMismatchCount = 0L
                        writerChronologyInversionCount = 0L
                        writerChronologyCollisionCount = 0L
                        writerDrainMissingCount = 0L
                        writerRecoveryVerdict = 'missing'
                        writerChronologyVerdict = 'missing'
                        completed = $false
                        transferEvidenceStatus = 'not_started'
                        runtimeCorrectnessVerdict = 'incomplete_proof'
                        evidenceClassification = 'incomplete_proof'
                        guiReportedFailurePhase = 'route_runtime'
                        guiReportedFailureReason = 'evidence_enrichment_timeout'
                        guiReportedFailureError = 'evidence_enrichment_timeout'
                        failures = [System.Collections.Generic.List[string]]::new()
                    }
                }
                $ledger = [pscustomobject]@{
                    cursorIntegrityVerdict = 'pass'
                    cursorIntegrityReasons = @()
                    writerEvidenceVersion = 3L
                    writerIntegrityVerdict = 'pass'
                    writerGapCount = 0L
                    writerDuplicateCount = 0L
                    writerIdentityMismatchCount = 0L
                    writerWriteFailureCount = 0L
                    writerDeferredBatchCount = 0L
                    writerRecoveredBatchCount = 0L
                    writerUnresolvedDeferredBatchCount = 0L
                    writerTerminalFailureCount = 0L
                    writerRecoveryMismatchCount = 0L
                    writerChronologyInversionCount = 0L
                    writerChronologyCollisionCount = 0L
                    writerDrainMissingCount = 0L
                    writerRecoveryVerdict = 'pass'
                    writerChronologyVerdict = 'pass'
                }
                $scenarioEvidence = @([pscustomobject]@{ artifactDir = $tempRoot; logLedger = $ledger })

                $trusted = New-TrustResult
                Set-RouteAcceptanceResultEvidence -Result $trusted -ScenarioEvidence $scenarioEvidence
                if ($trusted.semanticFailureDomain -ne 'route_runtime' -or
                    $trusted.runtimeCorrectnessVerdict -ne 'fail' -or
                    $trusted.transferEvidenceStatus -ne 'incomplete' -or
                    $trusted.failures.Count -ne 0 -or
                    -not (Test-RouteAcceptanceConclusiveRouteRuntimeEvidence -Result $trusted)) {
                    throw "trusted generic payload-hold evidence lost route-runtime precedence: $($trusted | ConvertTo-Json -Depth 5 -Compress)"
                }

                $variant = 'payload_hold_current_submission_epoch'
                $compact['failure_variant'] = $variant
                $compact["${variant}_raw_count"] = 2
                $compact["${variant}_distinct_count"] = 1
                $compact["${variant}_recovered_count"] = 0
                $compact["${variant}_unresolved_count"] = 1
                $compact | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-compact-summary.json')
                Add-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-operator-verdict.txt') -Value @(
                    "failure_variant=$variant",
                    "${variant}_raw_count=2",
                    "${variant}_distinct_count=1",
                    "${variant}_recovered_count=0",
                    "${variant}_unresolved_count=1")
                $trusted.failureVariant = $variant
                if (-not (Test-RouteAcceptanceConclusiveRouteRuntimeEvidence -Result $trusted)) {
                    throw 'matching failure-variant quartet was not trusted'
                }
                $operatorPath = Join-Path $tempRoot 'filetransfer-operator-verdict.txt'
                $operatorText = Get-Content -LiteralPath $operatorPath -Raw
                $operatorText.Replace("${variant}_unresolved_count=1", "${variant}_unresolved_count=0") |
                    Set-Content -LiteralPath $operatorPath
                if (Test-RouteAcceptanceConclusiveRouteRuntimeEvidence -Result $trusted) {
                    throw 'mismatched failure-variant quartet was trusted'
                }
                $trusted.failureVariant = '(none)'

                @{ compact_requested = 1; compact_mode = 1; correlator_status = 'completed'; event_count = 20; indexed_event_count = 19 } |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot 'filetransfer-retained-analysis-stage-diagnostics.json')

                $untrustedComplete = New-TrustResult
                $untrustedComplete.completed = $true
                $untrustedComplete.guiReportedFailurePhase = ''
                $untrustedComplete.guiReportedFailureReason = ''
                $untrustedComplete.guiReportedFailureError = ''
                Set-RouteAcceptanceResultEvidence -Result $untrustedComplete -ScenarioEvidence $scenarioEvidence
                if (-not $untrustedComplete.currentRuntimeProof -or
                    $untrustedComplete.semanticFailureDomain -ne 'route_runtime' -or
                    $untrustedComplete.transferEvidenceStatus -ne 'complete' -or
                    $untrustedComplete.runtimeCorrectnessVerdict -ne 'incomplete_proof' -or
                    $untrustedComplete.evidenceClassification -ne 'incomplete_proof' -or
                    $untrustedComplete.failures.Count -ne 0 -or
                    (Test-RouteAcceptanceConclusiveRouteRuntimeEvidence -Result $untrustedComplete) -or
                    (Get-RouteAcceptanceResultFailureClass -AcceptancePhase phase5 -Result $untrustedComplete) -ne 'incomplete_proof') {
                    throw "complete transfer trusted only by corrupt compact evidence did not fail closed: $($untrustedComplete | ConvertTo-Json -Depth 5 -Compress)"
                }

                $unavailableComplete = New-TrustResult
                $unavailableComplete.completed = $true
                Set-RouteAcceptanceResultEvidence -Result $unavailableComplete -ScenarioEvidence $scenarioEvidence
                if (-not $unavailableComplete.currentRuntimeProof -or
                    $unavailableComplete.semanticFailureDomain -ne 'unavailable' -or
                    $unavailableComplete.transferEvidenceStatus -ne 'complete' -or
                    $unavailableComplete.runtimeCorrectnessVerdict -ne 'incomplete_proof' -or
                    $unavailableComplete.evidenceClassification -ne 'incomplete_proof' -or
                    $unavailableComplete.failures.Count -ne 1 -or
                    (Get-RouteAcceptanceResultFailureClass -AcceptancePhase phase5 -Result $unavailableComplete) -ne 'incomplete_proof') {
                    throw "complete transfer with enrichment timeout did not remain incomplete proof: $($unavailableComplete | ConvertTo-Json -Depth 5 -Compress)"
                }

                $untrusted = New-TrustResult
                Set-RouteAcceptanceResultEvidence -Result $untrusted -ScenarioEvidence $scenarioEvidence
                if ($untrusted.semanticFailureDomain -ne 'unavailable' -or
                    $untrusted.runtimeCorrectnessVerdict -ne 'incomplete_proof' -or
                    $untrusted.failures.Count -ne 1 -or
                    (Test-RouteAcceptanceConclusiveRouteRuntimeEvidence -Result $untrusted)) {
                    throw "untrusted compact/enrichment evidence did not fail closed: $($untrusted | ConvertTo-Json -Depth 5 -Compress)"
                }
                Write-Output 'route-runtime-trust-precedence-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Route-runtime trust predicate regression failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("route-runtime-trust-precedence-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_FallbackFrontierCheckpointAcceptanceRecheckCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-frontier-checkpoint-acceptance-recheck-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-fallback-frontier-checkpoint-acceptance-recheck-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_FRONTIER_CHECKPOINT_ACCEPTANCE_RECHECK_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake checkpoint-acceptance recheck propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("7", evidence["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count"]);
            Assert.Equal("1", evidence["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count"]);
            Assert.Equal("0", evidence["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count"]);
            Assert.Equal("1", evidence["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict"]);
            Assert.Equal("75", evidence["filetransfer_fallback_frontier_stall_grace_recheck_missing_raw_count"]);
            Assert.Equal("75", evidence["filetransfer_fallback_frontier_stall_grace_recheck_missing_distinct_count"]);
            Assert.Equal("74", evidence["filetransfer_fallback_frontier_stall_grace_recheck_missing_recovered_count"]);
            Assert.Equal("1", evidence["filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["filetransfer_fallback_frontier_stall_grace_recheck_missing_verdict"]);
            Assert.Equal("7", evidence["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count"]);
            Assert.Equal("1", evidence["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count"]);
            Assert.Equal("0", evidence["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count"]);
            Assert.Equal("1", evidence["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count"]);
            Assert.Equal("1", evidence["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count"]);
            Assert.Equal("0", evidence["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count"]);
            Assert.Equal("1", evidence["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count"]);
            Assert.Equal("0", evidence["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("7", summary["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count"]);
            Assert.Equal("1", summary["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary["filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict"]);
            Assert.Equal("75", summary["filetransfer_fallback_frontier_stall_grace_recheck_missing_raw_count"]);
            Assert.Equal("74", summary["filetransfer_fallback_frontier_stall_grace_recheck_missing_recovered_count"]);
            Assert.Equal("1", summary["filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary["filetransfer_fallback_frontier_stall_grace_recheck_missing_verdict"]);
            Assert.Equal("7", summary["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count"]);
            Assert.Equal("1", summary["filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count"]);
            Assert.Equal("1", summary["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count"]);
            Assert.Equal("0", summary["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count"]);
            Assert.Equal("pass", summary["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict"]);
            Assert.Equal("1", summary["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count"]);
            Assert.Equal("0", summary["filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(7, root.GetProperty("fileTransferFallbackFrontierCheckpointAcceptanceRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferFallbackFrontierCheckpointAcceptanceRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("fileTransferFallbackFrontierCheckpointAcceptanceRecheckMissingVerdict").GetString());
            Assert.Equal(75, root.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingRawCount").GetInt32());
            Assert.Equal(74, root.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingVerdict").GetString());
            Assert.Equal(7, root.GetProperty("fileTransferCheckpointPendingFrontierRequestNotRecheckedAfterCheckpointAcceptanceRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferCheckpointPendingFrontierRequestNotRecheckedAfterCheckpointAcceptanceUnresolvedCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("pass", root.GetProperty("fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffVerdict").GetString());
            Assert.Equal(1, root.GetProperty("fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffDeliveredPeerResponseRecheckRecoveredCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffDeliveredPeerResponseRecheckUnresolvedCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(7, run.GetProperty("fileTransferFallbackFrontierCheckpointAcceptanceRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fileTransferFallbackFrontierCheckpointAcceptanceRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal(75, run.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingRawCount").GetInt32());
            Assert.Equal(74, run.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fileTransferFallbackFrontierStallGraceRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fileTransferCheckpointPendingFrontierRequestNotRecheckedAfterCheckpointAcceptanceUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationFailsTerminalBeforeFallbackSwitchOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-before-fallback-switch-off");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-before-fallback-switch-off");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_FALLBACK_BOUNDARY"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("terminal_before_fallback_switch_off", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("terminal_before_fallback_switch_off", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PairingEvidencePropagatesToScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pairing-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pairing-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_BRIDGE_RECOVERY_GENERATION_PAIRING_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake pairing-summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            foreach (var reportName in new[]
                     {
                         "filetransfer-route-consistency-summary.txt",
                         "filetransfer-operator-verdict.txt"
                     })
            {
                var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), reportName);
                Assert.Equal("fail", evidence["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
                Assert.Equal("4", evidence["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
                Assert.Equal("1", evidence["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
                Assert.Equal("0", evidence["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
                Assert.Equal("1", evidence["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            }

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("fail", summary["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", summary["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", summary["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", summary["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal("fail", root.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
            Assert.Equal(4, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal("fail", run.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
            Assert.Equal(4, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RegularV4ProjectionCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-projection-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-projection-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_FRONTIER_REPAIR_PROJECTION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake projection-summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("3", summary["regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("2", summary["regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("3", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("2", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_preservation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(3, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(2, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(3, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(2, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ProjectionReauthorizationFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-projection-reauthorization-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-projection-reauthorization-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PROJECTION_REAUTHORIZATION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake projection-reauthorization summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_projection_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("fresh_projection_required_before_unrestricted_state", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("4675", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1438", summary["regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_projection_reauthorization_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("1438", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1438, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1438, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PeerSilenceReplayRangePreservationReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-peer-silence-replay-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-peer-silence-replay-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_PEER_SILENCE_REPLAY_RANGE_PRESERVATION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake peer-silence replay summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_authority_granted", summary["first_proof_chain_failure_event"]);
            Assert.Equal("peer_silence_replay_range_rotated_after_missing_dispatch", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("2898", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.False(summary.ContainsKey($"{scenario}.gui_reported_failure_phase"));
            Assert.False(summary.ContainsKey($"{scenario}.gui_reported_failure_reason"));

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());
            Assert.Equal(string.Empty, run.GetProperty("guiReportedFailurePhase").GetString());
            Assert.Equal(string.Empty, run.GetProperty("guiReportedFailureReason").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_SuccessorPressureInstallFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_successor_pressure_install_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-successor-pressure-install-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-successor-pressure-install-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_SUCCESSOR_PRESSURE_INSTALL_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake successor-pressure summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_settlement_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("regular_v4_frontier_repair_pressure_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("2792", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_successor_pressure_install_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RouteCommitSendDrainFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_regular_v4_route_commit_send_drain_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-route-commit-send-drain-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-route-commit-send-drain-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_ROUTE_COMMIT_SEND_DRAIN_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake route-commit send-drain summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_data_path_send_result", summary["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_regular_v4_send_completed_after_tuna_owner_install",
                summary["first_proof_chain_failure_reason"]);
            Assert.Equal("3900", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1315", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("6", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("1315", summary[$"{scenario}.first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);
            Assert.Equal("6", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(6, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainVerdict").GetString());
            Assert.Equal(1315, root.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1315, run.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(6, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PeerEpochReauthorizationFailureIsConclusiveWithIncompleteTransfer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-peer-epoch-reauthorization-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-peer-epoch-reauthorization-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_TRANSPORT_EPOCH_REAUTHORIZATION_SUMMARY_FIXTURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_SHA_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")),
                $"Expected fake peer-epoch reauthorization failure summary.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_fallback_tail_local_yield_blocked", summary["first_proof_chain_failure_event"]);
            Assert.Equal("local_yield_transport_epoch_reauthorization_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("27225", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("13864", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("fail", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete", summary[$"{scenario}.transfer_evidence_status"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete", root.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(13864, root.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal("fail", root.GetProperty("fallbackTailTransportEpochReauthorizationVerdict").GetString());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingUnresolvedCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete", run.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(13864, run.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal("fail", run.GetProperty("fallbackTailTransportEpochReauthorizationVerdict").GetString());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PreCommitProbeStartHandoffCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_precommit_probe_start_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-precommit-probe-start-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-precommit-probe-start-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_PRECOMMIT_PROBE_START_HANDOFF_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake precommit-probe summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("runtime_unlock_precommit_probe_start_timeout", summary["first_proof_chain_failure_event"]);
            Assert.Equal("10960", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("2647", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_distinct_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count"]);
            Assert.Equal("pass", summary["runtime_unlock_retry_dispatch_post_grant_hold_verdict"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_probe_start_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_precommit_probe_start_handoff_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_recovered_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count"]);
            Assert.Equal("pass", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_precommit_probe_start_handoff_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("PASS", root.GetProperty("correctnessVerdict").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldDistinctCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount").GetInt32());
            Assert.Equal("pass", root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount").GetInt32());
            Assert.Equal("pass", run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldVerdict").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_SettlementDispositionFailureIsIncompleteProofInScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_settlement_disposition_unverifiable";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-settlement-disposition-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-settlement-disposition-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_SETTLEMENT_DISPOSITION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake settlement-disposition summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_settlement_accepted", summary["first_proof_chain_failure_event"]);
            Assert.Equal("accepted_settlement_disposition_evidence_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("5298", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_settlement_disposition_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairSettlementDispositionVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairSettlementDispositionVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PressureAuthorityLifecycleSplitReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_pressure_authority_lifecycle_split";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pressure-authority-lifecycle-split-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pressure-authority-lifecycle-split-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PRESSURE_AUTHORITY_LIFECYCLE_SPLIT_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake pressure-lifecycle summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_v4_receive_liveness_summary", summary["first_proof_chain_failure_event"]);
            Assert.Equal("coordinator_repair_authority_outlived_legacy_pressure_ttl", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("4224", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PressureRecoveryHandoffCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_pressure_recovery_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pressure-recovery-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pressure-recovery-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PRESSURE_RECOVERY_HANDOFF_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake pressure-recovery handoff summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_recovery_authority_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("installed_repair_pressure_not_retired_before_receive_recovery", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("1769", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1287", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_pressure_recovery_handoff_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ClosureWindowLivenessCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-closure-liveness-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-closure-liveness-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECOVERY_LEASE_CLOSURE_LIVENESS_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake closure-liveness propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_BridgeRecoveryStartupPayloadIsolationReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "filetransfer_bridge_recovery_execution_startup_payload_isolation_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(
            repoRoot,
            "artifacts",
            "filetransfer-route-acceptance-test",
            Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(
            artifactRoot,
            "phase5-bridge-recovery-startup-payload-isolation-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-bridge-recovery-startup-payload-isolation-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_BRIDGE_RECOVERY_STARTUP_PAYLOAD_ISOLATION_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake bridge-recovery startup propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal(candidate, evidence["first_proof_chain_failure_class"]);
            Assert.Equal("nkn_bridge_recovery_execution_completed", evidence["first_proof_chain_failure_event"]);
            Assert.Equal(
                "bridge_recovery_execution_timeout_after_pre_ready_payload_submission",
                evidence["first_proof_chain_failure_reason"]);
            Assert.Equal("3258", evidence["first_proof_chain_failure_sequence"]);
            Assert.Equal("1597", evidence["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count"]);
            Assert.Equal("0", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_FallbackTailUnblockRecheckCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-tail-unblock-recheck-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-tail-unblock-recheck-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_UNBLOCK_RECHECK_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake fallback-tail recheck propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count"]);
            Assert.Equal("0", evidence["runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairHoldBindingMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairHoldBindingMissingUnresolvedCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairSettlementRecheckAfterBoundHoldMissingUnresolvedCount").GetInt32());
            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairHoldBindingMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairHoldBindingMissingUnresolvedCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackFrontierRepairSettlementRecheckAfterBoundHoldMissingUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_CurrentArtifactEquivalentIsIncompleteProofWithRetryCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-current-artifact-equivalent");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-current-artifact-equivalent");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RETRY_DISPATCH_SUCCESSOR_ATTEMPT_MISMATCH"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-EvidenceDryRun",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            Assert.Equal("incomplete_proof", summary[$"{prefix}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{prefix}.runtime_correctness_verdict"]);
            Assert.Equal("incomplete", summary[$"{prefix}.transfer_evidence_status"]);
            Assert.Equal("fail", summary[$"{prefix}.cursor_integrity_verdict"]);
            Assert.Equal("0", summary[$"{prefix}.current_runtime_proof"]);
            Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", summary[$"{prefix}.first_proof_chain_failure_event"]);
            Assert.Equal("retry_dispatch_successor_attempt_mismatch", summary[$"{prefix}.first_proof_chain_failure_reason"]);
            Assert.NotEqual("fallback_authority", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("evidence_dry_run", JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runRoot, "route-acceptance-run-manifest.json"))).RootElement.GetProperty("executionMode").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_EvidenceDryRunValidatesSuppliedCursorWithoutClaimingLiveProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var testRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var sourceArtifactRoot = Path.Combine(testRoot, "source");
        var sourceRunRoot = Path.Combine(sourceArtifactRoot, "phase5-evidence-source");
        var dryArtifactRoot = Path.Combine(testRoot, "dry");
        var dryRunRoot = Path.Combine(dryArtifactRoot, "phase5-evidence-dry-run");
        var tamperedDryArtifactRoot = Path.Combine(testRoot, "dry-tampered-writer-marker");
        var tamperedDryRunRoot = Path.Combine(tamperedDryArtifactRoot, "phase5-evidence-dry-run-tampered-writer-marker");

        try
        {
            var source = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", sourceArtifactRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-evidence-source"));
            Assert.True(source.ExitCode == 0, $"Expected source fixture run to pass.{Environment.NewLine}{source.Stdout}{Environment.NewLine}{source.Stderr}");

            var dryEnvironment = BuildFakeRouteAcceptanceEnvironment("phase5-evidence-dry-run");
            var dry = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", dryArtifactRoot,
                    "-ExePath", Path.Combine(sourceRunRoot, "evidence-fixture", "nLink.fake.exe"),
                    "-EvidenceDryRun",
                    "-EvidenceDryRunFixtureRoot", sourceRunRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                dryEnvironment);

            Assert.NotEqual(0, dry.ExitCode);
            var summary = ReadArtifactReport(dryRunRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("3", summary["writer_evidence_version"]);
            Assert.Equal("pass", summary["writer_integrity_verdict"]);
            Assert.Equal("0", summary["writer_gap_count"]);
            Assert.Equal("0", summary["writer_duplicate_count"]);
            Assert.Equal("0", summary["writer_identity_mismatch_count"]);
            Assert.Equal("0", summary["writer_write_failure_count"]);
            Assert.Equal("0", summary["writer_chronology_inversion_count"]);
            Assert.Equal("0", summary["writer_chronology_collision_count"]);
            Assert.Equal("pass", summary["writer_chronology_verdict"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dryRunRoot, "route-acceptance-run-manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("evidence_dry_run", root.GetProperty("executionMode").GetString());
            Assert.Equal("PASS", root.GetProperty("validation").GetProperty("cursorIntegrityVerdict").GetString());
            Assert.False(root.GetProperty("validation").GetProperty("currentRuntimeProof").GetBoolean());
            Assert.True(root.GetProperty("runtime").GetProperty("hashStable").GetBoolean());
            Assert.True(root.GetProperty("harness").GetProperty("hashStable").GetBoolean());
            Assert.True(root.GetProperty("bridge").GetProperty("hashStable").GetBoolean());
            var ledgerEvidence = root.GetProperty("scenarioAttempts")[0].GetProperty("logLedger");
            Assert.Equal(3, ledgerEvidence.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal("PASS", ledgerEvidence.GetProperty("writerIntegrityVerdict").GetString());
            Assert.Equal("pass", ledgerEvidence.GetProperty("producerWriterIntegrityVerdict").GetString());

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(dryRunRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var summaryRoot = summaryJson.RootElement;
            Assert.Equal(3, summaryRoot.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal(0, summaryRoot.GetProperty("writerChronologyInversionCount").GetInt64());
            Assert.Equal(0, summaryRoot.GetProperty("writerChronologyCollisionCount").GetInt64());
            Assert.Equal("pass", summaryRoot.GetProperty("writerChronologyVerdict").GetString());
            var summaryRun = Assert.Single(summaryRoot.GetProperty("runs").EnumerateArray());
            Assert.Equal(3, summaryRun.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal(0, summaryRun.GetProperty("writerChronologyInversionCount").GetInt64());
            Assert.Equal(0, summaryRun.GetProperty("writerChronologyCollisionCount").GetInt64());
            Assert.Equal("pass", summaryRun.GetProperty("writerChronologyVerdict").GetString());

            const string scenario = "regular-v4-live-activation-off-on-off-256mb";
            var sourceLedger = await File.ReadAllBytesAsync(Path.Combine(sourceRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            var importedLedger = await File.ReadAllBytesAsync(Path.Combine(dryRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            Assert.Equal(sourceLedger, importedLedger);

            var sourceWriterMarkerPath = Path.Combine(
                sourceRunRoot,
                scenario,
                "filetransfer-gui-log-writer-failure-markers.log");
            await File.WriteAllTextAsync(
                sourceWriterMarkerPath,
                "event=operational_log_write_failed; log_writer_id=tampered; log_writer_sequence=2; failure_code=io_error" + Environment.NewLine);

            var tamperedDryEnvironment = BuildFakeRouteAcceptanceEnvironment("phase5-evidence-dry-run-tampered-writer-marker");
            var tamperedDry = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", tamperedDryArtifactRoot,
                    "-ExePath", Path.Combine(sourceRunRoot, "evidence-fixture", "nLink.fake.exe"),
                    "-EvidenceDryRun",
                    "-EvidenceDryRunFixtureRoot", sourceRunRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                tamperedDryEnvironment);

            Assert.NotEqual(0, tamperedDry.ExitCode);
            var tamperedSummary = ReadArtifactReport(
                tamperedDryRunRoot,
                "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("fail", tamperedSummary["writer_integrity_verdict"]);
            Assert.Equal("fail", tamperedSummary["cursor_integrity_verdict"]);
            Assert.Equal("0", tamperedSummary["current_runtime_proof"]);

            using var tamperedManifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(tamperedDryRunRoot, "route-acceptance-run-manifest.json")));
            var tamperedManifestRoot = tamperedManifest.RootElement;
            Assert.Equal(
                "FAIL",
                tamperedManifestRoot.GetProperty("validation").GetProperty("cursorIntegrityVerdict").GetString());
            var tamperedLedgerEvidence = tamperedManifestRoot
                .GetProperty("scenarioAttempts")[0]
                .GetProperty("logLedger");
            Assert.Equal("FAIL", tamperedLedgerEvidence.GetProperty("writerIntegrityVerdict").GetString());
            Assert.Equal("pass", tamperedLedgerEvidence.GetProperty("producerWriterIntegrityVerdict").GetString());
            Assert.Contains(
                tamperedLedgerEvidence.GetProperty("writerIntegrityReasons").EnumerateArray(),
                reason => reason.GetString() == "cursor_writer_failure_marker_sha256_mismatch");
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesRegularV4RepairBeforeSecondTuna()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-repair-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-repair-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "regular_v4_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_frontier_repair_liveness_settlement_missing";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("regular_v4_frontier_repair", summary["regular-v4-live-activation-off-on-off-256mb.gui_reported_failure_phase"]);
            Assert.Equal("regular_v4_frontier_repair_liveness_settlement_missing", summary["regular-v4-live-activation-off-on-off-256mb.gui_reported_failure_reason"]);
            Assert.Equal("regular_v4_frontier_repair_liveness_settlement_missing", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesReceiveRecoveryRejectedBeforeRuntimeUnlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-rejected-before-runtime-unlock");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-rejected-before-runtime-unlock");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "runtime_unlock_retry";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_receive_recovery_rejected_before_runtime_unlock";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesRetrySourcePublicationMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-runtime-unlock-source-publication-mismatch");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-runtime-unlock-source-publication-mismatch");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "runtime_unlock_retry";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "runtime_unlock_retry_source_publication_mismatch";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.attempt_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal(
                "runtime_unlock_retry_source_publication_mismatch",
                summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesFallbackRepairAsRuntimeFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-repair-runtime-failure");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-repair-runtime-failure");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("fallback_frontier_repair_stalled", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Tuna GUI setup failure: phase=fallback_frontier_repair", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_Phase5PreservesWorkerHandoffCandidateUnderIncompleteProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-worker-handoff-runtime-failure");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-worker-handoff-runtime-failure");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "outbound_sparse_worker_handoff";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "post_tuna_fallback_outbound_receive_worker_handoff_missing";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary[$"{prefix}.final_route"]);
            Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", summary[$"{prefix}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{prefix}.runtime_correctness_verdict"]);
            Assert.False(summary.ContainsKey($"{prefix}.setup_failure_phase"));
            Assert.False(summary.ContainsKey($"{prefix}.setup_failure_reason"));
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains(
                "Tuna GUI runtime failure: phase=outbound_sparse_worker_handoff; reason=post_tuna_fallback_outbound_receive_worker_handoff_missing",
                summaryText,
                StringComparison.Ordinal);
            Assert.DoesNotContain("fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesFallbackRepairBeforeMissingSecondTuna()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-repair-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-repair-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("fallback_frontier_repair_stalled", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("second_tuna_activation_not_proven", summaryText, StringComparison.Ordinal);
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Tuna GUI setup failure: phase=fallback_frontier_repair", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationKeepsRouteInvariantBeforeFallbackRepair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-route-invariant-before-fallback-repair");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-route-invariant-before-fallback-repair");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("file_tuna_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("incomplete_proof", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("active file_tuna_v6", summaryText, StringComparison.Ordinal);
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationFailsTerminalBeforeSecondTunaActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_LIVE_REACTIVATION"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("second_tuna_activation_not_proven", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("terminal_before_second_tuna_activation", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationAllowsCleanTerminalAfterSecondTunaBeforeFinalFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-after-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-after-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_FALLBACK_BOUNDARY"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected clean terminal-before-fallback-boundary prefix to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SummaryJsonFailurePreservesTextSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-summary-json-forced-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-summary-json-forced-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FORCE_SUMMARY_JSON_FAILURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), "Expected text summary to survive JSON failure.");
            Assert.False(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "JSON summary should not be reported as successful when serialization fails.");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("1", summary["expected_run_count"]);
            Assert.Equal("1", summary["summary_json_failed"]);
            Assert.Contains("forced_summary_json_failure", summary["summary_json_failure_reason"], StringComparison.Ordinal);

            var diagnostics = ReadArtifactReport(runRoot, "phase5-summary-writer-diagnostics.txt");
            Assert.Equal("1", diagnostics["summary_json_failed"]);
            Assert.Contains("forced_summary_json_failure", diagnostics["summary_json_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("FAIL", diagnostics["final_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RetryDispatchInstallGrantEvidenceFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_retry_dispatch_install_grant_evidence_incomplete";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-retry-dispatch-install-grant-evidence-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-retry-dispatch-install-grant-evidence-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RETRY_DISPATCH_INSTALL_GRANT_EVIDENCE_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake retry-dispatch install-grant evidence summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("runtime_unlock_retry_dispatch_authority_granted", summary["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retry_dispatch_grant_request_active_predecessor_supersession_missing",
                summary["first_proof_chain_failure_reason"]);
            Assert.Equal("5510", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_authority_granted_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_authority_granted_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_retry_dispatch_install_grant_evidence_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_authority_granted_raw_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_authority_granted_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedRawCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedRawCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PostTunaFallbackTransportProofHandoffFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-post-tuna-fallback-transport-proof-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-post-tuna-fallback-transport-proof-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_POST_TUNA_FALLBACK_TRANSPORT_PROOF_HANDOFF_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake post-Tuna fallback transport-proof handoff summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("tuna_acceleration_timeline", summary["first_proof_chain_failure_event"]);
            Assert.Equal("scheduled_runtime_unlock_handoff_not_started", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("22360", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public void RunFileTransferRouteAcceptance_LiveRetainedAnalyzerScopesCompactModeToChildProcess()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$startInfo.Environment['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT'] = '1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($script:RouteAcceptanceExecutionMode -eq 'live')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$oldRetainedAnalysisCompact = $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item Env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT -ErrorAction SilentlyContinue",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = $oldRetainedAnalysisCompact",
            script,
            StringComparison.Ordinal);

        var childScopeAssignments = script.Split(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'",
            StringSplitOptions.None).Length - 1;
        Assert.Equal(2, childScopeAssignments);
    }
}
