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
        Assert.Contains("OpsSuite=ScriptSurface|OpsSuite=GuiHarness|OpsSuite=RetainedRouteLaw|OpsSuite=RetainedFallback|OpsSuite=RetainedRuntimeUnlock|OpsSuite=RetainedV4Transport|OpsSuite=AcceptancePhase4|OpsSuite=AcceptancePhase5Fast|OpsSuite=NknSoakAndBaseline|FullyQualifiedName~FileTransferSoakRunnerTests", scriptText, StringComparison.Ordinal);
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
        Assert.Contains("retained_analysis_timeout", tunaWrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-timeout-summary.json", tunaWrapperText, StringComparison.Ordinal);
        Assert.Contains("retained_analysis_timeout", routeWrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-analysis-timeout-summary.json", routeWrapperText, StringComparison.Ordinal);
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
                "ScenarioName"
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
