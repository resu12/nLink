namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiClassificationContractTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("listener", false)]
    [InlineData("listener", true)]
    [InlineData("terminal", true)]
    [InlineData("unknown", true)]
    public async Task GenericClassification_JsonResponseSatisfiesStrictEnrichmentContract(
        string scenario, bool requireCompleted)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText,
            "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText,
            "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var resolveBody = ExtractPowerShellFunctionBody(guiText,
            "Resolve-TunaGuiFailureClassification", "Write-TunaGuiTopLevelFailureSummaryIfMissing");
        var classifierBody = ExtractPowerShellFunctionBody(guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var containsBody = ExtractPowerShellFunctionBody(guiText,
            "Test-TunaGuiLogLinesContain", "Test-TunaGuiLogLineContainsAll");
        var completedBody = ExtractPowerShellFunctionBody(guiText,
            "Test-TunaGuiCompletedFailureClassificationResult", "Invoke-TunaGuiFailureClassificationRequest");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Path.Combine(FindRepoRoot(), "tools", "FileTransferAuthorityMatching.ps1").Replace("'", "''", StringComparison.Ordinal)}}'
            . '{{Path.Combine(FindRepoRoot(), "tools", "FileTransferOps", "CoordinatorSnapshotEvidence.ps1").Replace("'", "''", StringComparison.Ordinal)}}'
            . '{{Path.Combine(FindRepoRoot(), "tools", "FileTransferFrontierGraceHandoff.ps1").Replace("'", "''", StringComparison.Ordinal)}}'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Resolve-TunaGuiFailureClassification { {{resolveBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Test-TunaGuiLogLinesContain { {{containsBody}} }
            function Test-TunaGuiCompletedFailureClassificationResult { {{completedBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-classification-contract-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $line = switch ('{{scenario}}') {
                    'listener' { 'event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable' }
                    'terminal' { 'event=file_transfer_inbound_terminal; state=Completed' }
                    'unknown' { 'event=unrelated_setup_observation' }
                }
                $line | Set-Content -LiteralPath $path
                $base = New-TunaGuiFileTransferFailureClassificationResult `
                    -Phase 'harness_failure_classification' -Reason 'evidence_enrichment_pending' `
                    -RouteMode 'live-reactivation-second-transfer' `
                    -ClassificationSource 'classification_pending' -ClassificationComplete $false
                $candidate = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 -ErrorMessage 'Phase 5 paired preparation readiness missing' `
                    -RouteMode 'live-reactivation-second-transfer' -ImmutableEvidencePaths @($path) `
                    -RequireCompletedClassification:${{requireCompleted.ToString().ToLowerInvariant()}}
                $response = [ordered]@{
                    schemaVersion = 1
                    event = 'filetransfer_tuna_gui_failure_classification_response'
                    classification = $candidate
                } | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $actual = $response.classification
                if (-not (Test-TunaGuiCompletedFailureClassificationResult -Classification $actual -RequiredClassification $base)) {
                    $missing = @($base.PSObject.Properties.Name | Where-Object { $actual.PSObject.Properties.Name -notcontains $_ })
                    throw "generic completed response rejected; missing=$($missing -join ',')"
                }
                $expectedPhase = switch ('{{scenario}}') {
                    'listener' { 'preactivation_readiness' }; 'terminal' { 'measured_terminal' }; 'unknown' { 'unknown' }
                }
                $expectedReason = switch ('{{scenario}}') {
                    'listener' { 'preflight_listener_unavailable' }; 'terminal' { 'terminal_before_accept' }; 'unknown' { 'unknown' }
                }
                if ($actual.Phase -ne $expectedPhase -or $actual.Reason -ne $expectedReason -or
                    $actual.RouteMode -ne $base.RouteMode -or $actual.ClassificationSource -ne 'immutable_evidence' -or
                    $actual.EvidenceLineVisitCount -ne 1 -or $actual.EvidenceParsedRecordCount -ne 1 -or
                    $actual.ListenerUnavailable -ne ('{{scenario}}' -eq 'listener') -or
                    $actual.TerminalObserved -ne ('{{scenario}}' -eq 'terminal') -or $actual.TunaActive -or $actual.RouteSelected) {
                    throw "generic classification semantics changed: $($actual | ConvertTo-Json -Compress)"
                }
                foreach ($name in @('InitialActivationAnswerAckReturnDeliveryMissing', 'InitialActivationCommittedRefreshPrevalidationRejected')) {
                    if ($actual.PSObject.Properties.Name -notcontains $name -or $actual.$name -isnot [bool] -or $actual.$name) {
                        throw "generic-only flag missing or changed: $name"
                    }
                }
                if ($actual.FailureClass -ne '(none)' -or $actual.CoordinatorBoundaryId -ne '(none)' -or
                    $actual.CoordinatorRawCount -ne 0 -or $actual.RuntimeUnlockRetryDispatchPairedYieldSuccessorMissingAfterProbeRollback) {
                    throw 'generic response acquired unrelated authority evidence'
                }
                $actual.ClassificationComplete = $false
                if (Test-TunaGuiCompletedFailureClassificationResult -Classification $actual -RequiredClassification $base) {
                    throw 'incomplete classification was accepted'
                }
                $actual.ClassificationComplete = $true
                $actual.PSObject.Properties.Remove('FailureClass')
                if (Test-TunaGuiCompletedFailureClassificationResult -Classification $actual -RequiredClassification $base) {
                    throw 'partial response was accepted'
                }
                Write-Output 'generic-classification-contract-ok'
            }
            finally {
                $resolved = [IO.Path]::GetFullPath($tempRoot)
                $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
                if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing cleanup outside temporary workspace.'
                }
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0,
            $"Generic classification contract failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("generic-classification-contract-ok", result.Stdout, StringComparison.Ordinal);
    }
}
