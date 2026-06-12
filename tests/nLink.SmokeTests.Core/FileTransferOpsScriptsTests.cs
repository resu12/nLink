using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferOpsScriptsTests
{
    private static readonly string[] ExpectedFileTransferOpsModes =
    [
        "AnalyzeRetained",
        "LocalFast",
        "LocalImpaired",
        "LocalMixed",
        "NknFast",
        "NknMixed",
        "SupportCapture",
        "Test"
    ];

    private static readonly string[] ExpectedExternalTopologyProfiles =
    [
        "BulkFanout12",
        "BulkFanout4Legacy",
        "BulkFanout8",
        "BulkSingle1",
        "Default",
        "DefaultKeepAlive",
        "MediaFanout12",
        "MediaFanout8",
        "PinnedMainnetRpc",
        "PinnedSeedHttps"
    ];

    private static readonly string[] ExpectedPayloadEfficiencyProfiles =
    [
        "Auto",
        "Current",
        "LargeSingle48KiB",
        "Packed3x20KiB",
        "Packed3x21KiB"
    ];

    private static readonly string[] ExpectedTopLevelParameters =
    [
        "Mode",
        "ExternalTopologyProfile",
        "LogDir",
        "LogPath",
        "ArtifactDir",
        "TransferId",
        "TailMinutes",
        "IncludeRawSlices",
        "FailOnGate",
        "Configuration",
        "PayloadSizes",
        "PayloadEfficiencyProfile",
        "Cycles",
        "Seed",
        "Direction",
        "ImpairmentProfile",
        "CycleTimeoutSeconds",
        "ProgressTimeoutSeconds",
        "NoBuild",
        "ExePath",
        "Build",
        "TimeoutSeconds",
        "SafeBaselineArtifactDir",
        "StrongBaselineArtifactDir",
        "LiveRouteProofMode"
    ];

    private static readonly string[] ImplementationFiles =
    [
        "tools/FileTransferOps/AnalyzerOrchestration.ps1",
        "tools/FileTransferSoak/LogParsing.ps1",
        "tools/FileTransferSoak/SoakSummaryExtraction.ps1",
        "tools/FileTransferSoak/StabilizationGates.ps1",
        "tools/FileTransferSoak/ArtifactWriters.ps1",
        "tools/FileTransferSoak/BaselineComparison.ps1",
        "tools/PreRelease-Check.ps1",
        "tools/Run-FileTransferNknSoak.ps1",
        "tools/Run-FileTransferRouteAcceptance.ps1",
        "tools/Run-FileTransferTunaGuiSmoke.ps1",
        "tools/Run-FileTransferReceiveStallMatrix.ps1",
        "tools/Run-NknBridgeReceiveProbe.ps1"
    ];

    private static readonly string[] RequiredArtifactFiles =
    [
        "filetransfer-operator-verdict.txt",
        "transfer-terminal-summary.txt",
        "throughput-summary.txt",
        "throughput-decomposition-summary.txt",
        "payload-efficiency-summary.txt",
        "protocol-shape-summary.txt",
        "filetransfer-route-consistency-summary.txt",
        "repair-reorder-summary.txt",
        "transport-budget-summary.txt",
        "bridge-config-summary.txt",
        "bridge-bulk-summary.txt",
        "coexistence-summary.txt",
        "external-transport-health-summary.txt",
        "stability-gates-summary.txt",
        "v4-promotion-decision.txt",
        "v4-promotion-decision.json"
    ];

    private static readonly string[] RequiredLiveNknArtifactFiles =
    [
        "filetransfer-live-nkn-summary.txt",
        "filetransfer-live-nkn-summary.json",
        "filetransfer-live-nkn-cycles.jsonl",
        "filetransfer-retained-log-slice.log",
        "baseline-comparison.txt"
    ];

    private static readonly string[] RequiredRouteAcceptanceSubdirectories =
    [
        "regular-nkn-64mb-quick",
        "regular-nkn-128mb-target",
        "tuna-128mb-no-fault",
        "tuna-128mb-fallback"
    ];

    private static readonly string[] RequiredPhase4RouteAcceptanceSubdirectories =
    [
        "regular-nkn-v4-64mb",
        "active-tuna-v4-64mb",
        "live-switch-off-helpee-64mb",
        "live-switch-off-helper-64mb",
        "live-multi-toggle-off-on-off-64mb",
        "regular-v4-live-activation-off-on-off-256mb",
        "second-transfer-after-reactivation"
    ];

    private static readonly string[] RequiredPhase5RouteAcceptanceSubdirectories =
    [
        "regular-nkn-v4-64mb",
        "active-tuna-v4-64mb",
        "live-switch-off-helpee-64mb",
        "live-switch-off-helper-64mb",
        "regular-v4-live-activation-off-on-off-256mb",
        "second-transfer-after-reactivation"
    ];

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
        Assert.Contains("FullyQualifiedName~FileTransferOpsScriptsTests|FullyQualifiedName~FileTransferSoakRunnerTests", scriptText, StringComparison.Ordinal);
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
                "AllowExternalTransportWarnings"
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
    public void RunFileTransferTunaGuiSmoke_NoFaultFallbackEvidenceIsFailure()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Tuna GUI preactivated no-fault transfer unexpectedly entered fallback", scriptText, StringComparison.Ordinal);
        Assert.Contains("$FaultMode -eq 'none'", scriptText, StringComparison.Ordinal);
        Assert.Contains("$evidence.fallbackEpochStarted", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_PreactivationAcceptFailuresAreClassified()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Get-TunaGuiFileTransferSetupFailureClassification", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiFileTransferAcceptOrThrow", scriptText, StringComparison.Ordinal);
        Assert.Contains("Tuna transport active before measured GUI file transfer", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiActiveBridgeQuietWindow", scriptText, StringComparison.Ordinal);
        Assert.Contains("tuna_gui_active_bridge_quiet_window", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_GUI_ACTIVE_BRIDGE_QUIET_MS", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_receive_stall_recovery_unproven", scriptText, StringComparison.Ordinal);
        Assert.Contains("failurePhase", scriptText, StringComparison.Ordinal);
        Assert.Contains("failureReason", scriptText, StringComparison.Ordinal);
        Assert.Contains("offer_sent_accept_not_enabled", scriptText, StringComparison.Ordinal);
        Assert.Contains("preflight_listener_unavailable", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_receive_recovery_unproven", scriptText, StringComparison.Ordinal);
        Assert.Contains("activation_offer_not_observed", scriptText, StringComparison.Ordinal);
        Assert.Contains("activation_offer_sent_waiting_answer", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_activation_offer_not_observed", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_control_send_wait_timeout", scriptText, StringComparison.Ordinal);
        Assert.Contains("activationOfferReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("measuredOfferReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("offerReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("message_type=file_transfer_offer", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_offer_received_raw", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=offer_received", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiReadinessStateAfterBookmark -Bookmark $Bookmark", scriptText, StringComparison.Ordinal);
        Assert.Contains("rawListenerUnavailable", scriptText, StringComparison.Ordinal);
        Assert.Contains("rawListenerReady", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_UsesArtifactLocalNknIdentitiesAndEmptyLogClassification()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Get-AppLaunchEnvironmentOverrides -RoleName $RoleName", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-GuiSmokeNknIdentityPathForRole", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn-identities", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_NKN_KEY_PATH", scriptText, StringComparison.Ordinal);
        Assert.Contains("[AllowEmptyCollection()][object[]]$Lines", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveSwitchOffUsesSameTransferV6FallbackProof()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-v4-switch-off\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-v4-switch-off'", guiText, StringComparison.Ordinal);
        Assert.Contains("measured_live_post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("fallbackModel = $fallbackModel", guiText, StringComparison.Ordinal);
        Assert.Contains("live_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("singleTransferLiveFallback", guiText, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("protocol_version=6", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_route_epoch_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("off_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiLiveRouteEpochProof", guiText, StringComparison.Ordinal);
        Assert.Contains("strict live-route epoch sequence", guiText, StringComparison.Ordinal);
        Assert.Contains("missing_metadata", guiText, StringComparison.Ordinal);
        Assert.Contains("postTunaFallbackV6RouteObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("same-transfer V6 post-Tuna fallback", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("@('event=filetransfer_route_selected', (\"route={0}\" -f $Route)", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveMultiToggleUsesRouteEpochSequence()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-multi-toggle\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-multi-toggle'", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiLiveMultiToggleSequence", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_live_multi_toggle_step", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_route_epoch_started", guiText, StringComparison.Ordinal);
        Assert.Contains("normal_to_tuna_activation", guiText, StringComparison.Ordinal);
        Assert.Contains("tuna_to_normal_fallback", guiText, StringComparison.Ordinal);
        Assert.Contains("liveRouteEpochs", guiText, StringComparison.Ordinal);
        Assert.Contains("liveRouteEpochRouteChanges", guiText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("same-transfer strict live-route epoch cycling", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveRegularActivationCycleStartsRegularThenCyclesRoutes()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-regular-activation-cycle\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-regular-activation-cycle'", guiText, StringComparison.Ordinal);
        Assert.Contains("regular_nkn_v4_fast", guiText, StringComparison.Ordinal);
        Assert.Contains("on,off,on,off", guiText, StringComparison.Ordinal);
        Assert.Contains("RegularActivationCycle", wrapperText, StringComparison.Ordinal);
        Assert.Contains("measured_live_regular_activation_cycle", guiText, StringComparison.Ordinal);
        Assert.Contains("$activationProofTimeoutMs = if ($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-reactivation-second-transfer') { 240000 } else { 90000 }", guiText, StringComparison.Ordinal);
        Assert.Contains("$regularActivationCycleCapBytes = [Math]::Max(1L, [long]($PayloadSizeBytes / 4))", guiText, StringComparison.Ordinal);
        Assert.Contains("AfterLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("$lastObservedLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("FallbackBookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("-FallbackBookmark $bookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("final_off_after_reactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("regular_activation_cycle_off_after_live_tuna_epoch", guiText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("regular-to-Tuna and fallback/reactivation strict live-route epoch cycling", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveReactivationSecondTransferUsesSeparateProof()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-reactivation-second-transfer\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-reactivation-second-transfer'", guiText, StringComparison.Ordinal);
        Assert.Contains("measured_live_reactivation_file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("live_reactivation_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("secondTransfer", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-second-transfer-retained-log-slice.log", guiText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiSecondTransferReadinessOrThrow", guiText, StringComparison.Ordinal);
        Assert.Contains("$lastTunaInactiveIndex", guiText, StringComparison.Ordinal);
        Assert.Contains("$requiredStableReadyPolls = 4", guiText, StringComparison.Ordinal);
        Assert.Contains("$RouteMode -eq 'live-reactivation-second-transfer') -and", guiText, StringComparison.Ordinal);
        Assert.Contains("reason=prearm_after_fallback_route_started", guiText, StringComparison.Ordinal);
        Assert.Contains("firstTransferTerminalBeforeLiveReactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("terminal_before_same_transfer_reactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI live reactivation second-transfer proof did not prove clean fallback terminal followed by second-transfer Tuna V4", guiText, StringComparison.Ordinal);
        Assert.Contains("last_tuna_inactive_index=", guiText, StringComparison.Ordinal);
        Assert.Contains("required_stable_ready_polls=", guiText, StringComparison.Ordinal);
        Assert.Contains("-FileName 'filetransfer-second-transfer-retained-log-slice.log'", guiText, StringComparison.Ordinal);
        Assert.Contains("setupFailurePhase", guiText, StringComparison.Ordinal);
        Assert.Contains("second-transfer-analysis", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Invoke-TunaGuiLiveRetainedAnalysisBestEffort", wrapperText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6,file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("Second transfer after live reactivation failed route/integrity check", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_MergesMilestoneEvidenceChronologically()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Sort-TunaGuiRetainedLogLinesChronologically", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiRetainedLogLineSortKey", scriptText, StringComparison.Ordinal);
        Assert.Contains("Sort-Object -Property SortTicks, Ordinal", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("$combined = @($missingLines.ToArray())", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("$combined += $existingText", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_ControlledFallbackFailureStillWritesMeasuredAnalysis()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-log-slice-full.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-setup-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-measured-fallback-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("measured-fallback-analysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("setup-analysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("controlledRestartAnalysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackFailurePhase", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackDiagnostics", scriptText, StringComparison.Ordinal);
        Assert.Contains("lastCommittedChunk", scriptText, StringComparison.Ordinal);
        Assert.Contains("v6ChunkSendTimeoutCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackWarningKinds", scriptText, StringComparison.Ordinal);
        Assert.Contains("sendTimeoutsPerMiB", scriptText, StringComparison.Ordinal);
        Assert.Contains("frontierRequestsPerMiB", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackRescueFreezeCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackRescueWidenCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("setupNormalizedVerdict", scriptText, StringComparison.Ordinal);
        Assert.Contains("expected_controlled_setup_cancel", scriptText, StringComparison.Ordinal);
        Assert.Contains("Write-TunaGuiControlledRestartFailureSummary", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiLateSetupCleanupLine", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiControlledSetupCancelAccepted", scriptText, StringComparison.Ordinal);
        Assert.Contains("frame_type=filetransfer.cancel.v4", scriptText, StringComparison.Ordinal);
        Assert.Contains("Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir $resolvedArtifactDir", scriptText, StringComparison.Ordinal);

        var failureAnalysisIndex = scriptText.IndexOf("Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort", StringComparison.Ordinal);
        var guiFailureBranchIndex = scriptText.IndexOf("if ($guiSmokeExitCode -ne 0)", StringComparison.Ordinal);
        var guiFailureThrowIndex = scriptText.IndexOf("GUI smoke failed with exit code", StringComparison.Ordinal);
        var missingSummaryBranchIndex = scriptText.IndexOf("if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf))", StringComparison.Ordinal);
        var missingSummaryThrowIndex = scriptText.IndexOf("GUI smoke did not write file-transfer Tuna summary", StringComparison.Ordinal);
        Assert.True(failureAnalysisIndex >= 0, "Expected best-effort measured fallback analysis helper.");
        Assert.True(guiFailureBranchIndex >= 0, "Expected GUI failure branch.");
        Assert.True(guiFailureThrowIndex > guiFailureBranchIndex, "Expected GUI failure throw.");
        Assert.Contains(
            "Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort",
            scriptText[guiFailureBranchIndex..guiFailureThrowIndex],
            StringComparison.Ordinal);
        Assert.True(missingSummaryBranchIndex >= 0, "Expected missing-summary branch.");
        Assert.True(missingSummaryThrowIndex > missingSummaryBranchIndex, "Expected missing-summary throw.");
        Assert.Contains(
            "Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort",
            scriptText[missingSummaryBranchIndex..missingSummaryThrowIndex],
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_ControlledFallbackWritesStablePhaseMarkersAndWaitsForSetupCleanup()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("phase=setup_file_tuna_v4_started", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=setup_file_tuna_v4_terminal", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=setup_file_tuna_v4_cleanup_closed", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=measured_post_tuna_fallback_v6_started", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=measured_post_tuna_fallback_v6_terminal", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_data_session_removed", scriptText, StringComparison.Ordinal);
        Assert.Contains("V4 restart precondition setup cleanup closed", scriptText, StringComparison.Ordinal);
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
    public void RunFileTransferRouteAcceptance_AllowsOnlyCurrentFallbackWarningKinds()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("fallback_v6_send_timeout_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_receiver_state_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("recovered_post_tuna_fallback_bridge_clear", scriptText, StringComparison.Ordinal);
        Assert.Contains("'file_tuna_v4' { @('external_transport_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn') }", scriptText, StringComparison.Ordinal);
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
    public void GuiSmokeWindows_FileTransferNknScenariosAreRegistered()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));

        Assert.Contains("FILETRANSFER_NKN_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("FILETRANSFER_NKN_MIXED_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScenarioFileTransferNknSoak", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScenarioFileTransferNknMixedSoak", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-live-nkn-cycles.jsonl", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakStartupTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakProgressTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferMixedScreenShareWarmupTimeoutMs", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferIntegrityResolverSearchesArtifactReceivedDirectory()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var resolveBody = ExtractPowerShellFunctionBody(scriptText, "Resolve-FileTransferLiveReceivedFilePath", "Find-FileTransferLiveReceivedFileByHash");
        var findBody = ExtractPowerShellFunctionBody(scriptText, "Find-FileTransferLiveReceivedFileByHash", "Append-FileTransferLiveHarnessDiagnostic");

        Assert.Contains("[string]$ArtifactDir", resolveBody, StringComparison.Ordinal);
        Assert.Contains("Join-Path $ArtifactDir 'received'", resolveBody, StringComparison.Ordinal);
        Assert.Contains("[string]$ArtifactDir", findBody, StringComparison.Ordinal);
        Assert.Contains("Join-Path $ArtifactDir 'received'", findBody, StringComparison.Ordinal);
        Assert.Contains("-ArtifactDir $ArtifactDir", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferTerminalWaitRequiresCurrentCycleResolvedPath()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Wait-FileTransferTerminalPairAfterBookmark", "Append-FileTransferLiveCycleArtifact");

        Assert.Contains("$requiresResolvedInboundPath", functionBody, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace($resolvedCandidatePath)", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_terminal_ignored_unresolved_saved_path", functionBody, StringComparison.Ordinal);
        Assert.Contains("current_cycle_saved_path_unresolved", functionBody, StringComparison.Ordinal);
        Assert.Contains("continue", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferProgressWatchdogCountsOnlyDataProgress()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Get-FileTransferLiveProgressScore", "Get-GuiSmokeInt64FieldValue");

        Assert.Contains("filetransfer_chunk_batch_sent_as_batch", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_binary_frame_sent", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_binary_frame_received", functionBody, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_sent", functionBody, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_received", functionBody, StringComparison.Ordinal);
        Assert.Contains("written_chunk_count", functionBody, StringComparison.Ordinal);
        Assert.Contains("written_bytes", functionBody, StringComparison.Ordinal);

        Assert.DoesNotContain("nkn_bridge_bulk_send_summary", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("chunks_accepted_for_transport", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("chunks_written", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes_written", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_AnyAllLogWaitNormalizesSingleNeedleSet()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Wait-AppLogContainsAnyAllAfterBookmark", "Unlock-TunaFromSessionHeader");

        Assert.Contains("$normalizedNeedleSets = @($NeedleSets)", functionBody, StringComparison.Ordinal);
        Assert.Contains("$flatStringNeedleSet = $true", functionBody, StringComparison.Ordinal);
        Assert.Contains("$normalizedNeedleSets = @(, $normalizedNeedleSets)", functionBody, StringComparison.Ordinal);
        Assert.Contains("foreach ($needleSet in $normalizedNeedleSets)", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_ClickElementUsesAutomationFallbacksBeforeBoundsClick()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Click-Element", "Test-ElementValueMatchesText");

        Assert.Contains("InvokePattern", functionBody, StringComparison.Ordinal);
        Assert.Contains("TryGetClickablePoint", functionBody, StringComparison.Ordinal);
        Assert.Contains("LegacyIAccessiblePattern", functionBody, StringComparison.Ordinal);
        Assert.Contains("Cannot click element without bounds (Id=", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeFileTransferPicker_AutopickFileUsesExistingEnvPathOnly()
    {
        var previous = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE");
        using var unsafeDeveloperMode = ReleaseOverridePolicy.OverrideUnsafeDeveloperModeForTests(true);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-autopick", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var payloadPath = Path.Combine(tempRoot, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, [1, 2, 3, 4, 5]);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", payloadPath);
            var selection = await NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync();

            Assert.NotNull(selection);
            Assert.Equal("payload.bin", selection.Descriptor.FileName);
            Assert.Equal(5, selection.Descriptor.FileSizeBytes);

            await using var stream = await selection.OpenReadStreamAsync(CancellationToken.None);
            Assert.Equal(5, stream.Length);

            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", Path.Combine(tempRoot, "missing.bin"));
            Assert.Null(await NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", previous);
            TryDeleteDirectory(tempRoot);
        }
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanCompletedV4Transfer_ReturnsPassAndWritesArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedTransferFixture("transfer_clean"));

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("transfer_clean", verdict["transfer_id"]);
        Assert.Equal("transfer-terminal-summary.txt", verdict["next_artifact"]);

        foreach (var fileName in RequiredArtifactFiles)
        {
            Assert.True(File.Exists(Path.Combine(result.ArtifactDir, fileName)), $"Expected artifact: {fileName}");
        }

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("inconclusive", decomposition["likely_limiter"]);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("legacy", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteConsistentRegularNknV4_ReturnsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_regular_v4";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast", route["selected_routes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteConsistentFileTunaV4_ReturnPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_file_tuna_v4";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4", route["selected_routes"]);
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("post_tuna_fallback_v6", "default_v6", "post_tuna_fallback_strict")]
    [InlineData("diagnostic_regular_nkn_v6", "primary_regular_nkn_bulk_v6", "primary_regular_nkn_quiet")]
    public async Task AnalyzeRetained_RouteConsistentV6Routes_ReturnPass(
        string routeToken,
        string runtimeProfile,
        string bridgeRecoveryPolicy)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = "transfer_route_pass_" + routeToken;
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: routeToken,
            protocolVersion: 6,
            runtimeProfile: runtimeProfile,
            bridgeRecoveryPolicy: bridgeRecoveryPolicy,
            runtimeEventName: "filetransfer_v6_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal(routeToken, route["selected_routes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV6Route_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_obsolete_file_tuna_v6";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v6",
            protocolVersion: 6,
            runtimeProfile: "default_v6",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v6_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("unknown route selected", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ObsoleteV5Evidence_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_obsolete_v5";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_v5_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=5"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("obsolete_protocol_v5", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DiagnosticRouteWithoutMarker_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_diagnostic_marker_missing";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "diagnostic_regular_nkn_v6",
            protocolVersion: 6,
            runtimeProfile: "primary_regular_nkn_bulk_v6",
            bridgeRecoveryPolicy: "primary_regular_nkn_quiet",
            runtimeEventName: "filetransfer_v6_sender_started",
            diagnosticMarkerOverride: "0"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("diagnostic route selected without diagnostic marker", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteWithV6Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_regular_v6_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v6_sender_started",
            runtimeProtocolVersion: 6));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("filetransfer-route-consistency-summary.txt", verdict["next_artifact"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
        Assert.NotEqual("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RouteWithV6Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_tuna_v4_v6_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v6_sender_started",
            runtimeProtocolVersion: 6));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackV6RouteWithV4Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_post_fallback_v4_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "post_tuna_fallback_v6",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "post_tuna_fallback_strict",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route selected protocol mismatch", routeText, StringComparison.Ordinal);
        Assert.Contains("V6 route entered regular V4 runtime", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteBridgePolicyMismatch_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_bridge_mismatch";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "post_tuna_fallback_v6",
                protocolVersion: 6,
                runtimeProfile: "default_v6",
                bridgeRecoveryPolicy: "post_tuna_fallback_strict",
                runtimeEventName: "filetransfer_v6_sender_started")
            .Append(LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=tuna_strict"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlledRestartTerminalSeparatedRoutes_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: true));

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaFallbackRouteChangeBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false));

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFallbackLegHistory_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=(none); leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; state=active; reason=new_transfer; start_committed_chunk=0; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_frozen; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=header_switch_off; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=0; state=checkpoint_pending; reason=header_switch_off; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=0"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthorityEvidence_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("2", route["fallback_leg_authority_generation_sequence"]);
        Assert.Equal("1", route["fallback_leg_authority_started_count"]);
        Assert.Equal("1", route["fallback_leg_authority_checkpoint_accepted_count"]);
        Assert.Equal("1", route["fallback_leg_authority_bridge_recovery_requested_count"]);
        Assert.Equal("1", route["fallback_leg_authority_completed_count"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_InboundOnlyFallbackRoute_DoesNotRequireSenderAuthorityProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_frozen; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=sidecar_remote_closed; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_leg_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=0; state=checkpoint_pending; reason=sidecar_remote_closed; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("none", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthoritySupersededOnly_DoesNotCreateFallbackProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; session_id=sess_redacted; transfer_id={transferId}; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; superseded_by_route=file_tuna_v4; superseded_by_protocol_version=4; source=handoff_broadcast"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("none", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("none", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
        Assert.Equal("file_tuna_v4", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SupersededRouteHint_DoesNotCreateRouteMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_regular_v4_recovery_liveness_completed; session_id=sess_redacted; transfer_id={transferId}; generation=1; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; authority_reason=session_liveness_timeout_pending; reason=superseded_by_tuna_activation; superseded_by_route=file_tuna_v4; superseded_by_protocol_version=4; source=handoff_broadcast"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailReconciliationEvidence_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:tail; authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_zero_credit_breaker; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=tail_reconciliation_zero_credit; feedback_silence_ms=180000; remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; available_credit_chunks=0; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; state_refresh_failure_count=3; rebind_generation=2"),
            LogLine($"event=filetransfer_fallback_tail_stale_frontier_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; retired_remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; reason=tail_reconciliation_zero_credit"),
            LogLine($"event=filetransfer_fallback_tail_reconciliation_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; request_sequence=42; route=post_tuna_fallback_v6; protocol_version=6; leg_generation=5; live_route_epoch=4; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; remote_frontier_chunk_index=1975; refresh_hint_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; available_credit_chunks=0; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; reason=post_tuna_fallback_tail_reconciliation"),
            LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=tail_reconciliation_forced; request_id=v6-regular-nkn-state-refresh:old; replacement_request_id=v6-regular-nkn-state-refresh:42; replacement_priority=state_refresh_tail_reconciliation"),
            LogLine($"event=filetransfer_fallback_tail_reconciliation_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; proven_committed_chunk=1975; proven_highest_observed_chunk=2100; receiver_committed_chunk=1975; receiver_highest_observed_chunk=2100; receiver_credit_until_chunk_index_exclusive=2231; priority=state_refresh_tail_reconciliation; reason=receiver_state_sparse_runtime"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:42; proven_committed_chunk=1975; proven_highest_observed_chunk=2100; reason=receiver_state_sparse_runtime"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:42; authority_reason=post_tuna_fallback_tail_reconciliation_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_reconciliation_requested_count"]);
        Assert.Equal("1", route["fallback_tail_reconciliation_accepted_count"]);
        Assert.Equal("1", route["fallback_tail_zero_credit_breaker_count"]);
        Assert.Equal("1", route["fallback_tail_stale_frontier_retired_count"]);
        Assert.Equal("1", route["fallback_tail_state_refresh_send_slot_retired_count"]);
        Assert.Equal("0", route["fallback_tail_reconciliation_metadata_missing_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackZeroCreditTailWithoutReconciliation_IsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_zero_credit_tail; request_sequence=41; priority=state_refresh; feedback_silence_ms=180000; refresh_cooldown_ms=1000; stale_credit_recovery_delay_ms=30000; transport_epoch=9; epoch_state=bridge_recovery_completed_awaiting_proof; remote_frontier_chunk_index=1975; refresh_hint_chunk_index=1975; highest_accepted_chunk_index=1975; transport_backlog_chunks=0; available_credit_chunks=0; credit_ceiling_chunk_index=1975; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; rebind_generation=2"),
            LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_state_refresh_send_timeout; request_id=v6-regular-nkn-state-refresh:41; stale_inflight_recovery=0; stale_credit_recovery=0; tail_reconciliation=0; state_refresh_send_timeout=1; recovery_reason=post_tuna_fallback_state_refresh_failed; failure_count=4; feedback_silence_ms=190000; remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1975; transport_backlog_chunks=0; available_credit_chunks=0; credit_ceiling_chunk_index=1975; rebind_generation=2; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=session_liveness_timeout; session_id=sess_redacted; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper"),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_normal_zero_credit_state_refresh_count"]);
        Assert.Equal("0", route["fallback_tail_reconciliation_requested_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_reconciliation", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_tail_normal_zero_credit_state_refresh_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("post-Tuna fallback zero-credit tail stall ended without tail reconciliation proof", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackZeroCreditTailWithCleanServiceTerminal_IsNotClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; session_id=sess_redacted; reason=feedback_stalled_with_inflight; request_sequence=41; priority=state_refresh; feedback_silence_ms=30099; refresh_cooldown_ms=5000; stale_credit_recovery_delay_ms=30000; transport_epoch=2; epoch_state=frontier_repair_only; remote_frontier_chunk_index=1963; refresh_hint_chunk_index=1963; highest_accepted_chunk_index=2474; transport_backlog_chunks=512; available_credit_chunks=0; credit_ceiling_chunk_index=2475; in_flight_frames=7; pending_repair_count=1; queued_repair_chunk_count=0; rebind_generation=1"),
            LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active"),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("none", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_normal_zero_credit_state_refresh_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthorityMissingMetadata_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=0; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("1", route["fallback_leg_authority_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("fallback leg authority metadata missing", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeLivenessDefersThenFallbackProofPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; bridge_recovery_generation=1; transport_epoch=7; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; state=bridge_recovery_completed_awaiting_proof; bridge_recovery_requested=1; bridge_recovery_started=1; bridge_recovery_completed=1; silence_ms=18000; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=bridge_receive_stall_recovery_receive_resumed; session_id=sess_redacted; exit_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["session_liveness_deferred_for_current_recovery_count"]);
        Assert.Equal("0", route["session_liveness_timeout_during_valid_recovery_count"]);
        Assert.Equal("1", route["bridge_recovery_receive_resumed_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeLivenessDeferralWithLaterFallbackProofPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_bridge_restart_send_failure"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); proven_committed_chunk=64; proven_highest_observed_chunk=96; reason=receiver_state_sparse_runtime"),
            LogLine($"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=none; authority_reason=post_tuna_fallback_bridge_restart_send_failure; state=bridgerecoverycompletedawaitingproof; bridge_recovery_requested=0; bridge_recovery_started=0; bridge_recovery_completed=1; silence_ms=20835; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state_sparse_runtime"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["session_liveness_deferred_for_current_recovery_count"]);
        Assert.Equal("0", route["bridge_liveness_stale_deferral_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LivenessTimeoutDuringValidFallbackRecoveryFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=session_liveness_timeout; session_id=sess_redacted; generation=1; silence_ms=20000; terminal_timeout_ms=18000; role=Helper"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", verdict["session_liveness_timeout_during_valid_recovery_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("session liveness timeout during valid fallback recovery authority", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveTunaLegStartBeforeRouteSelected_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_active; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; transport_epoch=0; state=active; reason=live_route_tuna_activated; start_committed_chunk=32; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegHistoryWithUnknownRoute_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .Take(2)
            .Append(LogLine($"event=filetransfer_leg_frozen; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v6; protocol_version=6; live_route_epoch=0; transport_epoch=0; reason=header_switch_off"))
            .Concat(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false).Skip(2))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("transfer leg history unknown route", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CurrentLegStartedRouteMismatch_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; transport_epoch=0; state=active; reason=header_switch_off; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route token mismatch", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaReenableRouteChangeBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveReenableFixture());

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", route["selected_route_changes"]);
        Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", route["live_route_epoch_route_changes"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaMultiCycleRouteChangesBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveMultiCycleFixture(), ["-LiveRouteProofMode", "MultiToggle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("MultiToggle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("pass", route["live_route_epoch_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleRouteChangesBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveRegularActivationCycleFixture(), ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("RegularActivationCycle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("pass", route["live_route_epoch_proof_verdict"]);
        Assert.Equal("8", route["live_route_epoch_event_count"]);
        Assert.Equal("8", route["live_route_epoch_explicit_event_count"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleEpochStartedBeforeRouteSelected_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture().ToList();
        for (var epoch = 4; epoch >= 1; epoch--)
        {
            var selectedIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_route_selected", StringComparison.Ordinal) &&
                line.Contains($"live_route_epoch={epoch}", StringComparison.Ordinal));
            var startedIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                line.Contains($"live_route_epoch={epoch}", StringComparison.Ordinal));

            Assert.True(selectedIndex >= 0);
            Assert.True(startedIndex >= 0);

            var started = lines[startedIndex];
            lines.RemoveAt(startedIndex);
            if (startedIndex < selectedIndex)
            {
                selectedIndex--;
            }

            lines.Insert(selectedIndex, started);
        }

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleLateDuplicateRecovered_DedupesByEpoch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture().ToList();
        var firstFallbackStartedIndex = lines.FindIndex(line =>
            line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
            line.Contains("route=post_tuna_fallback_v6", StringComparison.Ordinal) &&
            line.Contains("live_route_epoch=2", StringComparison.Ordinal));
        Assert.True(firstFallbackStartedIndex >= 0);

        lines.Insert(
            firstFallbackStartedIndex + 1,
            LogLine("event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; live_route_epoch=1; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=late_peer_ack"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveSwitchOffProofModeWithStrictLiveRouteEvents_ReturnsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveSwitchOffFixture(), ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("SwitchOff", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveProofModeRejectsV6TransportOnlyProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; transport_epoch=2; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=recovered"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("SwitchOff", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.Equal("1", verdict["live_route_epoch_transport_only_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
        Assert.Contains("missing live route epoch started proof", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsTransportOnlyV6Proof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Where(line => !line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                           !line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal))
            .ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; transport_epoch=4; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=recovered"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("RegularActivationCycle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.Equal("1", verdict["live_route_epoch_transport_only_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
        Assert.Contains("missing live route epoch started proof", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsRouteSelectedOnlyProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Where(line => !line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                           !line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("protocol_version")]
    [InlineData("handoff_kind")]
    [InlineData("target_transport")]
    [InlineData("live_route_epoch")]
    [InlineData("transfer_id")]
    [InlineData("session_id")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleMissingLiveRouteMetadata_ReturnsProtocolFailure(string fieldName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var removed = false;
        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line =>
            {
                if (!removed &&
                    line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                    line.Contains("live_route_epoch=1", StringComparison.Ordinal))
                {
                    removed = true;
                    return RemoveSemicolonLogField(line, fieldName);
                }

                return line;
            })
            .ToArray();
        Assert.True(removed);

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.NotEqual("0", verdict["live_route_epoch_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch metadata missing", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleWrongProtocol_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var changed = false;
        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line =>
            {
                if (!changed &&
                    line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                    line.Contains("route=file_tuna_v4", StringComparison.Ordinal) &&
                    line.Contains("protocol_version=4", StringComparison.Ordinal))
                {
                    changed = true;
                    return line.Replace("protocol_version=4", "protocol_version=6", StringComparison.Ordinal);
                }

                return line;
            })
            .ToArray();
        Assert.True(changed);

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("missing live route epoch started proof: route=file_tuna_v4", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsReusedLiveRouteEpoch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line => line.Contains("live_route_epoch=3", StringComparison.Ordinal)
                ? line.Replace("live_route_epoch=3", "live_route_epoch=2", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("missing live route epoch started proof: route=file_tuna_v4; mode=RegularActivationCycle; after_epoch=2", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsStaleSessionProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line => line.Contains("event=filetransfer_live_route_epoch_", StringComparison.Ordinal)
                ? line.Replace("session_id=sess_redacted", "session_id=sess_stale", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch session scope mismatch", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRouteEpochMissingMetadata_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveSwitchOffFixture()
            .Select(line => line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal)
                ? line.Replace("route=post_tuna_fallback_v6; ", "", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.NotEqual("0", verdict["live_route_epoch_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch metadata missing", routeText, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch_metadata_missing_count=", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_UnmarkedRouteChangeBeforeTerminal_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .Select(line => line.Replace("post_tuna_fallback_active=1", "post_tuna_fallback_active=0", StringComparison.Ordinal))
            .ToArray();
        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route changed before prior terminal", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MeasuredFallbackSliceOnly_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareMeasuredFallbackFixture());

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("post_tuna_fallback_v6", route["selected_routes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MeasuredFallbackLateSetupCancelFeedback_IsNotHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareMeasuredFallbackFixture().ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id=[redacted]; session_id=sess_redacted; frame_type=filetransfer.cancel.v4; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRecoveredBridgeQueueClear_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; reason=transport_recovered_unproven; target_transport=regular_nkn", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=22; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=850237; payload_bytes_enqueued_per_second=425119; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=19; sample_window_ms=2000", secondsOffset: 60)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("recovered post-Tuna fallback bridge queue clear", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackV6SendTimeoutChurn_ReturnsSpecificExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_v6_chunk_batch_send_timeout; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=128; chunk_count=32; timeout_ms=2500; transport_epoch=3", secondsOffset: 20),
                LogLine("event=filetransfer_v6_post_tuna_fallback_send_timeout_requeued; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; requeued_chunk_count=32; exact_frontier_requeued_chunk_count=1; frontier_chunk_index=96", secondsOffset: 50),
                LogLine("event=filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=96; removed_prepared_in_flight_count=32; queued_chunk_count=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_v6_send_timeout_churn", verdict["warning_kinds"]);
        Assert.Equal("incident", verdict["warning_cap_count_unit"]);
        Assert.Equal("fallback_v6_send_timeout_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_v6_send_timeout_churn:3", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_v6_send_timeout_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("post-Tuna fallback V6 send timeout churn", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedFrontierRepairSameGap_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request={i}", secondsOffset: 30));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairSameGapAcrossBuckets_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var secondsOffset = 30 + (i * 45);
            var eventText = (i % 4) switch
            {
                0 => "event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=512; requested_chunk_count=32; post_tuna_fallback_survival=1",
                1 => "event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; requested_chunk_count=32; rescue_step=3",
                2 => "event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; previous_rescue_step=2; rescue_step=3",
                _ => "event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=512; first_chunk_count=32"
            };
            churnLines.Add(LogLine(eventText, secondsOffset: secondsOffset));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackUnknownFrontierUsesRepairRequestIdForIncidentKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=transport_epoch; transport_epoch=1; repair_request_id=v6-frontier:1:1773:1; recovery_mode=frontier_repair_only; start_chunk_index=1773; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 5),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=transport_epoch; frontier_chunk_index=-1; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:1:1773:1; recovery_mode=frontier_repair_only; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 5),
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; transport_epoch=1; repair_request_id=v6-frontier:2008:2; recovery_mode=regular_nkn_frontier_stall_control_bulk; start_chunk_index=2008; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 10),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; frontier_chunk_index=2008; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:2008:2; recovery_mode=regular_nkn_frontier_stall_control_bulk; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 10),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=2008; previous_rescue_step=0; rescue_step=1; rescue_request_count=2", secondsOffset: 11),
                LogLine("event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=2008; first_chunk_count=1", secondsOffset: 11),
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; transport_epoch=1; repair_request_id=v6-frontier:3094:4; recovery_mode=regular_nkn_frontier_stall_control_bulk; start_chunk_index=3094; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 18),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; frontier_chunk_index=3094; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:3094:4; recovery_mode=regular_nkn_frontier_stall_control_bulk; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 18),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=3094; previous_rescue_step=0; rescue_step=1; rescue_request_count=2", secondsOffset: 19),
                LogLine("event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=3094; first_chunk_count=1", secondsOffset: 19),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedReceiverStateChurnSameBurst_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var eventName = i % 2 == 0
                ? "filetransfer_v6_receiver_state_deferred"
                : "filetransfer_v6_receiver_state_coalesced";
            churnLines.Add(LogLine($"event={eventName}; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stalled; next_chunk_index={512 + i}; highest_received_chunk_index={640 + i}", secondsOffset: 30 + (i % 60)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_receiver_state_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_receiver_state_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_receiver_state_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackReceiverStateRateOnlyOverCap_WithTerminalProofReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var chunk = i % 2 == 0 ? 512 : 768;
            var secondsOffset = i % 2 == 0 ? 0 : 2;
            var eventName = i % 2 == 0
                ? "filetransfer_v6_receiver_state_deferred"
                : "filetransfer_v6_receiver_state_coalesced";
            churnLines.Add(LogLine($"event={eventName}; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stalled; next_chunk_index={chunk}; highest_received_chunk_index={chunk + 32}", secondsOffset: secondsOffset));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_receiver_state_churn:2", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_receiver_state_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_receiver_state_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedBridgeClearSameBurst_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; reason=transport_recovered_unproven; target_transport=regular_nkn", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=13; payload_bytes_sent=3731; payload_bytes_enqueued=6523; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=3", secondsOffset: 60),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=6; frames_enqueued=9; payload_bytes_sent=3471; payload_bytes_enqueued=5327; send_failures=0; queue_clears=3; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 75),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=1", secondsOffset: 90),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=4; frames_enqueued=5; payload_bytes_sent=1946; payload_bytes_enqueued=2436; send_failures=0; queue_clears=1; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 105)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:1", verdict["warning_kind_counts"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:6", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairCountOverCapRateUnderCap_RemainsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 240);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={512 + i}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0", secondsOffset: 10 + (i * 40)));
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={512 + i}; first_chunk_count=1", secondsOffset: 11 + (i * 40)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:5", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
        Assert.Contains("post-Tuna fallback frontier repair churn", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairRateAtCountLimitWithTerminalProof_ReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var frontier = 512 + (i % 3);
            var eventText = i % 2 == 0
                ? $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={frontier}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0"
                : $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={frontier}; first_chunk_count=1";
            churnLines.Add(LogLine(eventText, secondsOffset: 5 + i));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairRateOverCap_ReturnsExternalChurnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var frontier = 512 + (i % 4);
            var eventText = i % 2 == 0
                ? $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={frontier}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0"
                : $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={frontier}; first_chunk_count=1";
            churnLines.Add(LogLine(eventText, secondsOffset: 5 + i));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
        Assert.NotEqual("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_exceeded_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteCompletedBridgeQueueClear_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_regular_queue_clear",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started")).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=4", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=20; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=848728; payload_bytes_enqueued_per_second=424364; send_failures=0; queue_clears=4; queue_depth=1; queued_bytes=427; oldest_queued_age_ms=299; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("recovered_regular_v4_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:1", verdict["warning_kind_counts"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:2", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:regular_nkn", verdict["warning_cap_contexts"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("recovered regular NKN V4 bridge queue clear", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_InitialRegularRouteBridgeQueueClearBeforeActivation_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_then_tuna_queue_clear";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 1),
            LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=4", secondsOffset: 30),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=20; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=848728; payload_bytes_enqueued_per_second=424364; send_failures=0; queue_clears=4; queue_depth=1; queued_bytes=427; oldest_queued_age_ms=299; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 60),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 61),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 180),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 180)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("recovered_regular_v4_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("recovered regular NKN V4 bridge queue clear", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteBridgeSendFailure_RemainsHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareCompletedFixture(
            "transfer_regular_send_failure",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started").ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; payload_bytes_sent=847447; payload_bytes_per_second=423724; send_failures=1; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Contains("bridge bulk send failure/clear", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4ProgressTimeoutRecoveryStorm_DowngradesQueueClearToEnvironmentalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_recovery_storm";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Where(line => !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                           !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .ToList();
        lines.Add(LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-31; raw_chunk_bytes=67108864; chunk_count=32", secondsOffset: 5));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; stall_reason=bulk_receive_stalled; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 20));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted; trigger=filetransfer_protocol_repair_only; requested_reason=regular_v4_peer_silence; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 21));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed; requested_reason=regular_v4_peer_silence; stall_reason=regular_v4_unproven_recovery_escalation; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 22));
        lines.Add(LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=2", secondsOffset: 23));
        lines.Add(LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; session_id=sess_a; reason=no useful data progress for 180s; total_wait_s=360; progress_timeout_seconds=180; receiver_next_chunk=1614; receiver_highest_chunk=1613; progress_events=23", secondsOffset: 360));
        lines.Add(LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; session_id=sess_a; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout", secondsOffset: 361));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Contains("regular_v4_transport_recovery_storm", verdict["warning_kinds"], StringComparison.Ordinal);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("regular_v4_transport_recovery_storm", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveredRuntimeUnlockBridgeQueueClear_ReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_runtime_unlock_queue_clear",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=tuna_acceleration_activation_offer_not_observed; session_id=sess_redacted; trigger=runtime_unlock; generation=3; recovery_reason=bulk_queue_cleared; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 25),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=4; frames_enqueued=9; payload_bytes_sent=1946; payload_bytes_enqueued=4321; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 30),
                LogLine("event=tuna_activation_control_send_recovery_requested; session_id=sess_redacted; trigger=runtime_unlock; reason=runtime_unlock_offer_send_not_observed; recovery_reason=bulk_queue_cleared", secondsOffset: 35),
                LogLine("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id=sess_redacted; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 40),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=abc123; recovery_count=1", secondsOffset: 45)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Contains("recovered_runtime_unlock_bridge_clear", verdict["warning_kinds"], StringComparison.Ordinal);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:1", verdict["warning_kind_counts"], StringComparison.Ordinal);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:2", verdict["warning_kind_raw_event_counts"], StringComparison.Ordinal);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:runtime_unlock", verdict["warning_cap_contexts"], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockFailureBeforeActivation_ClassifiesRecoveryCoordination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRecoveryCoordinationFailureFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_recovery_coordination", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_scheduled_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_queued_behind_active_negotiation_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);

        var routeSummary = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("regular_nkn_v4_fast", routeSummary["selected_route_changes"]);
        Assert.DoesNotContain("file_tuna_v6", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-operator-verdict.txt")), StringComparison.Ordinal);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_recovery_coordination", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockContractRetryDispatchedWithoutLivenessTimeout_IsNotRecoveryCoordinationFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRecoveryContractDispatchedFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_authority_observed_count"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryDispatchedButOfferObservationBlocked_ClassifiesNextBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchedButOfferObservationBlockedFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_offer_observation_blocked_by_receive_recovery", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.NotEqual("runtime_unlock_recovery_coordination", verdict["recovery_failure_class"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_offer_observation_blocked_by_receive_recovery", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockDispatchDeferredByRegularV4ReceiveRecovery_ClassifiesNextBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockDispatchDeferredByRegularV4ReceiveRecoveryFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_dispatch_deferred_by_regular_v4_receive_recovery", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count"]);
        Assert.Equal("0", verdict["runtime_unlock_offer_observation_blocked_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.NotEqual("runtime_unlock_offer_observation_blocked_by_receive_recovery", verdict["recovery_failure_class"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_dispatch_deferred_by_regular_v4_receive_recovery", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockListenerRearmFailure_ClassifiesListenerRearmCoordination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockListenerRearmFailureFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("tuna_listener_rearm_coordination", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["listener_rearm_required_count"]);
        Assert.Equal("0", verdict["listener_rearm_completed_count"]);
        Assert.Equal("2", verdict["listener_rearm_failed_count"]);
        Assert.Equal("0", verdict["runtime_unlock_offer_dispatched_after_listener_rearm_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("tuna_listener_rearm_coordination", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockListenerRearmCompletedAndDispatched_IsNotListenerRearmFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockListenerRearmCompletedFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["listener_rearm_required_count"]);
        Assert.Equal("1", verdict["listener_rearm_completed_count"]);
        Assert.Equal("0", verdict["listener_rearm_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_dispatched_after_listener_rearm_count"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPeerResponseMissingUnderRegularV4Recovery_ClassifiesPeerResponseBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockPeerResponseMissingUnderRegularV4RecoveryFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_peer_response_not_received_under_regular_v4_recovery", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_authority_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_attempt_count"]);
        Assert.Equal("0", verdict["runtime_unlock_cutthrough_peer_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_timeout_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_cutthrough_verdict"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_peer_response_not_received_under_regular_v4_recovery", stability["recovery_failure_class"]);
        Assert.Equal("fail", stability["runtime_unlock_cutthrough_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockCutThroughPeerReceivedWithRegularActivationCycle_IsAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockCutThroughPeerReceivedRegularActivationCycleFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_attempt_count"]);
        Assert.Equal("2", verdict["runtime_unlock_cutthrough_peer_received_count"]);
        Assert.Equal("0", verdict["runtime_unlock_cutthrough_timeout_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_cutthrough_verdict"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
        Assert.Equal("pass", stability["runtime_unlock_cutthrough_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_RecordsFrozenDefaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_defaults")
            .Append(LogLine("event=bridge_bundle_loaded; bridge_script_path=C:\\nLink\\bridge\\win-x64\\index.js; bridge_manifest_path=C:\\nLink\\bridge\\win-x64\\bridge-manifest.json; manifest_status=ok; manifest_reason=ok; manifest_version=1; app_version=0.7.0; bridge_script_sha256=abc123; manifest_bridge_script_sha256=abc123; node_version=v24.13.1; owner_pid_watchdog=true; kill_on_close_job=true"))
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=4; bulk_send_concurrency=4; bulk_send_mode=fanout; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("expected", bridgeConfig["bridge_config_status"]);
        Assert.Equal("4/8/4", bridgeConfig["expected_topology"]);
        Assert.Equal("4/8/4", bridgeConfig["observed_topology"]);
        Assert.Equal("4", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("fanout", bridgeConfig["observed_bulk_send_mode"]);
        Assert.Equal("ok", bridgeConfig["manifest_status"]);
        Assert.Equal("v24.13.1", bridgeConfig["node_version"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_FlagsUnexpectedDefaultDrift()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_drift")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=8; bulk_send_concurrency=6; bulk_send_mode=round_robin; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("unexpected_drift", bridgeConfig["bridge_config_status"]);
        Assert.Equal("4/8/4", bridgeConfig["expected_topology"]);
        Assert.Equal("4/8/8", bridgeConfig["observed_topology"]);
        Assert.Equal("6", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("round_robin", bridgeConfig["observed_bulk_send_mode"]);
        Assert.Equal("0", bridgeConfig["settings_match_expected"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_ClassifiesDiagnosticOverrideSeparately()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_diagnostic_override")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=8; bulk_send_concurrency=6; bulk_send_mode=round_robin; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE"] = "BulkFanout8",
            });

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("diagnostic_override", bridgeConfig["bridge_config_status"]);
        Assert.Equal("1", bridgeConfig["diagnostic_profile"]);
        Assert.Equal("0", bridgeConfig["settings_match_expected"]);
        Assert.Equal("BulkFanout8", bridgeConfig["external_topology_profile"]);
        Assert.Equal("4/8/8", bridgeConfig["observed_topology"]);
        Assert.Equal("6", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("round_robin", bridgeConfig["observed_bulk_send_mode"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("INVALID_SETUP", verdict["verdict"]);
    }

    public static IEnumerable<object[]> ThroughputLimiterFixtures()
    {
        yield return
        [
            "receiver_gap",
            "receiver_gap_stalled",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=300; sent_cache_bytes=7372800; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=8000000; raw_bytes_received_per_second=4000000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=180; pending_bytes=4423680; next_chunk_index=100; highest_received_chunk_index=280; late_arrival_distance=180; oldest_gap_age_ms=2200; granted_until_chunk_index_exclusive=260; granted_window_bytes=3932160; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=10",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=280; late_arrival_distance=180; stall_duration_ms=2200; pending_bytes=4423680; granted_window_bytes=3932160",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "bridge_bulk",
            "bridge_bulk_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_bridge_bulk; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=200; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=100; sent_cache_bytes=2457600; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_bridge_bulk; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2000000; raw_bytes_received_per_second=1000000; contiguous_bytes_committed=2000000; contiguous_bytes_committed_per_second=1000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=200; highest_received_chunk_index=200; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=260; granted_window_bytes=1474560; write_batch_count=2; write_batch_bytes=2000000; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=128; queued_bytes=5242880; oldest_queued_age_ms=500; in_flight=1; send_p95_ms=400; send_max_ms=650; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sender_window",
            "sender_window_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_window; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=22; chunk_count_sent=44; chunks_accepted_for_transport=44; remote_next_expected_chunk_index=20; remote_granted_until_chunk_index_exclusive=52; remote_granted_window_bytes=786432; sent_cache_chunk_count=24; sent_cache_bytes=589824; send_wait_count=3; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sender_window; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1048576; raw_bytes_received_per_second=524288; contiguous_bytes_committed=1048576; contiguous_bytes_committed_per_second=524288; pending_chunk_count=0; pending_bytes=0; next_chunk_index=44; highest_received_chunk_index=44; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=52; granted_window_bytes=196608; write_batch_count=2; write_batch_bytes=1048576; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=22; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sender_serialized",
            "sender_transport_serialized",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_serialized; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=2400000; raw_bytes_per_second=1200000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=120; chunks_accepted_for_transport=360; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=520; remote_granted_window_bytes=6021120; sent_cache_chunk_count=120; sent_cache_bytes=2580480; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sender_serialized; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2400000; raw_bytes_received_per_second=1200000; contiguous_bytes_committed=2400000; contiguous_bytes_committed_per_second=1200000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=360; highest_received_chunk_index=360; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=520; granted_window_bytes=3440640; write_batch_count=2; write_batch_bytes=2400000; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=2400000; payload_bytes_per_second=1200000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "nkn_delivery",
            "nkn_delivery_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_nkn_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=400; remote_granted_window_bytes=3932160; sent_cache_chunk_count=160; sent_cache_bytes=3932160; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_nkn_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2000000; raw_bytes_received_per_second=1000000; contiguous_bytes_committed=2000000; contiguous_bytes_committed_per_second=1000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=320; highest_received_chunk_index=320; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=400; granted_window_bytes=1966080; write_batch_count=2; write_batch_bytes=2000000; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_feedback",
            "adaptive_window_underprovisioned",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_feedback; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_feedback; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=696; remote_granted_window_bytes=4214784; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=8; in_flight_bytes_max=516096; scheduled_frames=28; completed_frames=28; failed_frames=0; fifo_wait_ms=10; fifo_wait_max_ms=15; accepted_progress_lag_bytes_max=516096; pending_bytes_limit=2097152",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=700; highest_received_chunk_index=700; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=896; granted_window_bytes=4214784; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=0; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=8388608; effective_granted_window_bytes=4214784; current_credit_chunks=196; desired_credit_chunks=392; low_watermark_credit_chunks=353; credit_remaining_chunks=196; credit_desired_chunks=392; credit_remaining_bytes=4214784; credit_desired_bytes=8429568; granted_until_chunk_index_exclusive=896; target_granted_until_chunk_index_exclusive=1092; grant_base_chunk_index=700; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=700; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=no_sparse_ahead; next_chunk_index=700; highest_received_chunk_index=700; late_arrival_distance=0; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_grant_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; frames_sent_since_last=12; control_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100; sample_window_ms=2000",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sticky_limited",
            "sticky_limited_without_pressure",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sticky_limited; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sticky_limited; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=473088; raw_bytes_per_second=236544; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=22; chunks_accepted_for_transport=1560; remote_next_expected_chunk_index=1529; remote_granted_until_chunk_index_exclusive=1554; remote_granted_window_bytes=537600; sent_cache_chunk_count=31; sent_cache_bytes=666624; send_wait_count=21; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=8; chunk_count_prepared=22; raw_bytes_prepared=473088; read_duration_ms=0; batch_prepare_duration_ms=0; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=5227; inter_schedule_gap_max_ms=5227; credit_wait_duration_ms=6918; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2472960; raw_bytes_received_per_second=1236480; contiguous_bytes_committed=2408448; contiguous_bytes_committed_per_second=1204224; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1470; highest_received_chunk_index=1478; late_arrival_distance=8; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1538; granted_window_bytes=1462272; write_batch_count=39; write_batch_bytes=2472960; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=1236480; sparse_written_ahead_bytes=64512; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; reason=ack_only; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=537600; effective_granted_window_bytes=1462272; current_credit_chunks=68; desired_credit_chunks=68; low_watermark_credit_chunks=62; credit_remaining_chunks=68; credit_desired_chunks=68; credit_remaining_bytes=1462272; credit_desired_bytes=1462272; granted_until_chunk_index_exclusive=1538; target_granted_until_chunk_index_exclusive=1538; grant_base_chunk_index=1470; grant_base_reason=sparse_ahead; sparse_ahead_bytes=129024; credit_base_chunk_index=1470; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=2623488; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); proactive_repair_pressure_state=(none); proactive_repair_age_ms=4598; same_frontier_unfilled_ms=0; limited_recovery_clean_ms=2200; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1464; highest_received_chunk_index=1469; late_arrival_distance=5; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=8; payload_bytes_sent=520000; payload_bytes_per_second=260000; payload_bytes_enqueued=520000; payload_bytes_enqueued_per_second=260000; inter_enqueue_gap_p95_ms=5227; inter_enqueue_gap_max_ms=5227; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=16; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=8; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sparse_reorder_credit",
            "sparse_reorder_credit_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=4214784; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=8; in_flight_bytes_max=516096; scheduled_frames=28; completed_frames=28; failed_frames=0; fifo_wait_ms=10; fifo_wait_max_ms=15; accepted_progress_lag_bytes_max=516096; pending_bytes_limit=2097152",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=1308; granted_until_chunk_index_exclusive=896; granted_window_bytes=4214784; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; policy=SparseTolerant; decision=soft_limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=379; soft_reorder_threshold=192; soft_gap_stall_ms=1000; sparse_ahead_gap_stall_limit_ms=750; gap_stall_age_ms=1308; current_profile=healthy_file_only_soft_limited; target_window_bytes=1048576; soft_limit_target_bytes=1048576; granted_window_bytes=4214784; next_chunk_index=500; highest_received_chunk_index=879; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_file_only_soft_limited; target_window_bytes=1048576; effective_granted_window_bytes=4214784; current_credit_chunks=196; desired_credit_chunks=48; low_watermark_credit_chunks=44; credit_remaining_chunks=196; credit_desired_chunks=48; credit_remaining_bytes=4214784; credit_desired_bytes=1032192; granted_until_chunk_index_exclusive=896; target_granted_until_chunk_index_exclusive=548; target_base_chunk_index=500; target_base_reason=gap_stall; grant_base_chunk_index=500; grant_base_reason=gap_stall; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=0; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=gap_stall; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "dominant_sparse_credit",
            "sender_feedback_loop_blocked",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1272; remote_granted_window_bytes=16602112; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1272; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=372; available_credit_bytes_after=7999488; credit_wait_active_ms=1300; send_pump_signaled=1; chunks_schedulable=372; in_flight_frames=0; in_flight_bytes=0",
                "event=filetransfer_v4_sender_credit_stall_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; wait_reason=no_credit; credit_wait_active_ms=1300; accepted_chunk_index=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=900; available_credit_chunks=0; available_credit_bytes=0; last_grant_age_ms=900; in_flight_frames=0; in_flight_bytes=0; pending_repair_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1272; granted_window_bytes=8429568; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; policy=SparseTolerant; decision=tolerated; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=379; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=700; current_profile=healthy_expanded; target_window_bytes=16777216; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=879; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; target_base_chunk_index=880; target_base_reason=sparse_ahead; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_generation",
            "grant_generation_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_generation; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_generation; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1280; remote_granted_window_bytes=16773120; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1280; granted_window_bytes=16773120; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_grant_generation; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1280; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_delivery",
            "grant_delivery_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_delivery; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_delivery; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1280; remote_granted_window_bytes=16773120; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1280; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=380; available_credit_bytes_after=8171520; credit_wait_active_ms=100; send_pump_signaled=1; chunks_schedulable=380; in_flight_frames=0; in_flight_bytes=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1280; granted_window_bytes=16773120; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1280; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "file_only_sparse_window_capacity",
            "file_only_sparse_window_capacity_proven",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4800000; raw_bytes_per_second=2400000; chunk_frames_sent=0; batch_frames_sent=75; chunk_count_sent=225; chunks_accepted_for_transport=1200; remote_next_expected_chunk_index=760; remote_granted_until_chunk_index_exclusive=1532; remote_granted_window_bytes=16602112; sent_cache_chunk_count=440; sent_cache_bytes=9461760; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=75; chunk_count_prepared=225; raw_bytes_prepared=4800000; read_duration_ms=4; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=10; inter_schedule_gap_max_ms=20; credit_wait_duration_ms=100; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=4800000; raw_bytes_received_per_second=2400000; contiguous_bytes_committed=4800000; contiguous_bytes_committed_per_second=2400000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1200; highest_received_chunk_index=1200; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1980; granted_window_bytes=16773120; write_batch_count=4; write_batch_bytes=4800000; write_duration_ms=8; sparse_mode=1; sparse_write_bytes_per_second=2400000; sparse_written_ahead_bytes=0; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; reason=target_changed; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=16773120; current_credit_chunks=300; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=300; credit_desired_chunks=780; credit_remaining_bytes=6451200; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1980; target_granted_until_chunk_index_exclusive=1980; target_base_chunk_index=1200; target_base_reason=sparse_ahead; grant_base_chunk_index=1200; grant_base_reason=sparse_ahead; sparse_ahead_bytes=0; credit_base_chunk_index=1200; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=262144; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); limited_recovery_clean_ms=0; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1200; highest_received_chunk_index=1200; late_arrival_distance=0; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=75; frames_enqueued=75; payload_bytes_sent=4900000; payload_bytes_per_second=2450000; payload_bytes_enqueued=4900000; payload_bytes_enqueued_per_second=2450000; inter_enqueue_gap_p95_ms=10; inter_enqueue_gap_max_ms=20; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=4; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=55; worker_idle_slot_samples=20; worker_saturation_percent=20; drain_wake_count=75; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sparse_frontier_gap_unrepaired",
            "sparse_frontier_gap_unrepaired_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=525; remote_granted_window_bytes=537600; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; oldest_gap_age_ms=3571; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=5698560; sparse_gap_count=6",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; stall_duration_ms=3571; pending_bytes=0; granted_window_bytes=537600; sparse_mode=1; sparse_written_ahead_bytes=5698560; sparse_gap_count=6",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=333; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=1500; gap_stall_age_ms=3571; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=537600; next_chunk_index=500; highest_received_chunk_index=833; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=gap_stall; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=gap_stall; next_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "nkn_bulk_underutilized",
            "nkn_bulk_underutilized",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=2763264; raw_bytes_per_second=1381632; chunk_frames_sent=0; batch_frames_sent=43; chunk_count_sent=129; chunks_accepted_for_transport=512; remote_next_expected_chunk_index=320; remote_granted_until_chunk_index_exclusive=516; remote_granted_window_bytes=4214784; sent_cache_chunk_count=180; sent_cache_bytes=3935232; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; configured_depth=4; effective_depth=4; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=4; in_flight_bytes_max=258048; scheduled_frames=43; completed_frames=43; failed_frames=0; fifo_wait_ms=20; fifo_wait_max_ms=24; accepted_progress_lag_bytes_max=258048; pending_bytes_limit=1048576",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=43; chunk_count_prepared=129; raw_bytes_prepared=2763264; read_duration_ms=10; batch_prepare_duration_ms=20; send_async_schedule_duration_ms=8; inter_schedule_gap_p95_ms=60; inter_schedule_gap_max_ms=90; credit_wait_duration_ms=0; pipeline_slot_wait_duration_ms=20; effective_depth=4; pending_bytes=0; pending_bytes_limit=1048576; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2763264; raw_bytes_received_per_second=1381632; contiguous_bytes_committed=2763264; contiguous_bytes_committed_per_second=1381632; pending_chunk_count=0; pending_bytes=0; next_chunk_index=512; highest_received_chunk_index=512; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=516; granted_window_bytes=86016; write_batch_count=3; write_batch_bytes=2763264; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=43; frames_enqueued=43; payload_bytes_sent=2824471; payload_bytes_per_second=1412236; payload_bytes_enqueued=2824471; payload_bytes_enqueued_per_second=1412236; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=2; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=20; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=43; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "disk_write",
            "disk_write_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_disk_write; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=240; remote_next_expected_chunk_index=160; remote_granted_until_chunk_index_exclusive=320; remote_granted_window_bytes=3932160; sent_cache_chunk_count=80; sent_cache_bytes=1966080; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_disk_write; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=4000000; raw_bytes_received_per_second=2000000; contiguous_bytes_committed=4000000; contiguous_bytes_committed_per_second=2000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=240; highest_received_chunk_index=240; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=320; granted_window_bytes=1966080; write_batch_count=2; write_batch_bytes=4000000; write_duration_ms=650",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "repair_timeout",
            "repair_or_timeout_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_repair_timeout; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=240; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=140; sent_cache_bytes=3440640; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_repair_timeout; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1000000; raw_bytes_received_per_second=500000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=40; pending_bytes=983040; next_chunk_index=100; highest_received_chunk_index=140; late_arrival_distance=40; oldest_gap_age_ms=500; granted_until_chunk_index_exclusive=260; granted_window_bytes=3932160; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=10",
                "event=filetransfer_request_timeout_detected; transfer_id=transfer_repair_timeout; session_id=sess_a; next_chunk_index=100; outstanding_count=0; timeout_ms=6000; reason=v4_timeout",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "frontier_gap_repair",
            "frontier_repair_request_not_served",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=16; chunk_count_sent=48; chunks_accepted_for_transport=160; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=148; remote_granted_window_bytes=1032192; sent_cache_chunk_count=60; sent_cache_bytes=1290240; send_wait_count=2; repair_send_count=1",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=900000; raw_bytes_received_per_second=450000; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; oldest_gap_age_ms=1500; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=450000; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; stall_duration_ms=1500; pending_bytes=0; granted_window_bytes=1032192; sparse_mode=1; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap",
                "event=nkn_bridge_bulk_send_summary; frames_sent=16; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "frontier_gap_repair_sent_unfilled",
            "frontier_repair_sent_but_not_filled",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=16; chunk_count_sent=48; chunks_accepted_for_transport=160; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=148; remote_granted_window_bytes=1032192; sent_cache_chunk_count=60; sent_cache_bytes=1290240; send_wait_count=2; repair_send_count=1",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=16; chunk_count_prepared=48; raw_bytes_prepared=1048576; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=900000; raw_bytes_received_per_second=450000; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; oldest_gap_age_ms=2200; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=450000; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; stall_duration_ms=2200; pending_bytes=0; granted_window_bytes=1032192; sparse_mode=1; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap",
                "event=filetransfer_frontier_gap_repair_sender_received; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; scheduled_chunk_count=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=160; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0",
                "event=filetransfer_frontier_gap_repair_sender_sent; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=160; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=16; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "proactive_frontier_overlimited",
            "proactive_frontier_repair_overlimited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=920; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=519; late_arrival_distance=65; highest_received_chunk_index=565; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=915; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=919; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "external_transport",
            "external_transport_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_external_transport; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=400; remote_granted_window_bytes=3932160; sent_cache_chunk_count=160; sent_cache_bytes=3932160; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_external_transport; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1000000; raw_bytes_received_per_second=500000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=280; highest_received_chunk_index=280; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=400; granted_window_bytes=2949120; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000",
                "event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1"
            }
        ];
        yield return
        [
            "proactive_frontier_repeated",
            "proactive_frontier_repair_overlimited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=920; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=915; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=919; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "proactive_frontier_repeated_after_grace",
            "proactive_frontier_gap_repeated_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=2600; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=2600; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=2600; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
    }

    [Theory]
    [MemberData(nameof(ThroughputLimiterFixtures))]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ThroughputDecomposition_ClassifiesLimiter(string fixtureName, string expectedLimiter, string[] extraLines)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = "transfer_" + fixtureName;
        var lines = BuildCleanCompletedTransferFixture(transferId)
            .Concat(extraLines.Select(LogLine))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.True(throughput.ContainsKey("max_sender_raw_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_receiver_raw_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_contiguous_commit_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_payload_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_in_flight"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_configured_concurrency"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_effective_concurrency"));
        Assert.True(throughput.ContainsKey("sender_pipeline_summary_count"));
        Assert.True(throughput.ContainsKey("sender_feed_summary_count"));
        Assert.True(throughput.ContainsKey("max_sender_pipeline_effective_depth"));
        Assert.True(throughput.ContainsKey("max_sender_pipeline_in_flight_frames"));
        Assert.True(throughput.ContainsKey("sender_feed_raw_bytes_prepared"));
        Assert.True(throughput.ContainsKey("sender_feed_credit_wait_ratio_percent"));
        Assert.True(throughput.ContainsKey("receiver_feedback_pump_started_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_pump_active_count"));
        Assert.True(throughput.ContainsKey("slice_started_after_pump_start"));
        Assert.True(throughput.ContainsKey("receiver_feedback_enqueued_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_sent_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_coalesced_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_failed_count"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_queue_depth"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_enqueue_to_send_age_ms"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_send_duration_ms"));
        Assert.True(throughput.ContainsKey("grant_send_count"));
        Assert.True(throughput.ContainsKey("grant_delivery_bulk_count"));
        Assert.True(throughput.ContainsKey("average_effective_grant_window_bytes"));
        Assert.True(throughput.ContainsKey("grant_credit_base_sparse_count"));
        Assert.True(throughput.ContainsKey("grant_credit_base_contiguous_count"));
        Assert.True(throughput.ContainsKey("grant_base_blocked_by_gap_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_topup_count"));
        Assert.True(throughput.ContainsKey("average_credit_remaining_bytes"));
        Assert.True(throughput.ContainsKey("max_sparse_credit_advance_bytes"));
        Assert.True(throughput.ContainsKey("sparse_credit_eligible_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_used_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_blocked_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_reorder_use_ratio_percent"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_eligible_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_skipped_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_sender_received_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_sender_sent_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_filled_count"));
        Assert.True(throughput.ContainsKey("max_frontier_repair_request_to_fill_ms"));
        Assert.True(throughput.ContainsKey("proactive_repair_benign_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_grace_active_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_repeated_unfilled_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_hard_limited_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_hard_limited_during_grace_count"));
        Assert.True(throughput.ContainsKey("max_proactive_repair_age_ms"));
        Assert.True(throughput.ContainsKey("max_same_frontier_unfilled_ms"));
        Assert.True(throughput.ContainsKey("stale_proactive_repair_state_reset_count"));
        Assert.True(throughput.ContainsKey("benign_gap_skip_limited_policy_count"));
        Assert.True(throughput.ContainsKey("max_sparse_write_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_sparse_written_ahead_bytes"));
        Assert.True(throughput.ContainsKey("max_sparse_gap_count"));

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal(expectedLimiter, decomposition["likely_limiter"]);
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_in_flight"));
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_effective_concurrency"));
        Assert.True(decomposition.ContainsKey("sender_pipeline_sample_count"));
        Assert.True(decomposition.ContainsKey("sender_feed_sample_count"));
        Assert.True(decomposition.ContainsKey("max_sender_pipeline_effective_depth"));
        Assert.True(decomposition.ContainsKey("max_sender_pipeline_in_flight_frames"));
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_worker_saturation_percent"));
        Assert.True(decomposition.ContainsKey("sender_feed_credit_wait_ratio_percent"));
        Assert.True(decomposition.ContainsKey("sender_grant_apply_count"));
        Assert.True(decomposition.ContainsKey("sender_grant_apply_async_pump_count"));
        Assert.True(decomposition.ContainsKey("max_sender_grant_apply_credit_wait_active_ms"));
        Assert.True(decomposition.ContainsKey("max_sender_grant_apply_available_credit_bytes_after"));
        Assert.True(decomposition.ContainsKey("sender_credit_stall_summary_count"));
        Assert.True(decomposition.ContainsKey("max_sender_credit_stall_active_ms"));
        Assert.True(decomposition.ContainsKey("max_sender_credit_stall_last_grant_age_ms"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_summary_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_should_grant_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_no_send_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_coalesce_blocked_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_pump_started_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_pump_active_count"));
        Assert.True(decomposition.ContainsKey("slice_started_after_pump_start"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_enqueued_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_sent_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_coalesced_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_failed_count"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_queue_depth"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_enqueue_to_send_age_ms"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_send_duration_ms"));
        Assert.True(decomposition.ContainsKey("grant_send_rate_per_second"));
        Assert.True(decomposition.ContainsKey("grant_delivery_bulk_count"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes"));
        Assert.True(decomposition.ContainsKey("file_only_reorder_soft_limited_sample_count"));
        Assert.True(decomposition.ContainsKey("grant_base_sparse_ahead_count"));
        Assert.True(decomposition.ContainsKey("grant_base_contiguous_frontier_count"));
        Assert.True(decomposition.ContainsKey("grant_base_gap_stall_count"));
        Assert.True(decomposition.ContainsKey("grant_base_blocked_by_gap_count"));
        Assert.True(decomposition.ContainsKey("grant_credit_base_sparse_count"));
        Assert.True(decomposition.ContainsKey("grant_credit_base_contiguous_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_topup_count"));
        Assert.True(decomposition.ContainsKey("average_credit_remaining_bytes"));
        Assert.True(decomposition.ContainsKey("max_sparse_credit_advance_bytes"));
        Assert.True(decomposition.ContainsKey("sparse_credit_eligible_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_used_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_blocked_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_reorder_use_ratio_percent"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_eligible_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_skipped_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_sender_received_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_sender_sent_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_filled_count"));
        Assert.True(decomposition.ContainsKey("max_frontier_repair_request_to_fill_ms"));
        Assert.True(decomposition.ContainsKey("proactive_repair_benign_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_grace_active_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_repeated_unfilled_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_hard_limited_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_hard_limited_during_grace_count"));
        Assert.True(decomposition.ContainsKey("max_proactive_repair_age_ms"));
        Assert.True(decomposition.ContainsKey("max_same_frontier_unfilled_ms"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes_healthy_expanded"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes_healthy_file_only_soft_limited"));
        Assert.True(decomposition.ContainsKey("sticky_limited_without_pressure_count"));
        Assert.True(decomposition.ContainsKey("limited_recovery_fast_exit_count"));
        Assert.True(decomposition.ContainsKey("stale_proactive_repair_state_reset_count"));
        Assert.True(decomposition.ContainsKey("benign_gap_skip_limited_policy_count"));
        Assert.True(decomposition.ContainsKey("max_limited_recovery_clean_ms"));
        Assert.True(decomposition.ContainsKey("limited_recovery_block_none_count"));
        Assert.True(decomposition.ContainsKey("max_file_only_target_window_bytes"));
        Assert.True(decomposition.ContainsKey("file_only_sparse_window_capacity_proven"));
        Assert.True(decomposition.ContainsKey("adaptive_window_underprovisioned_signal"));
        Assert.True(decomposition.ContainsKey("fixed_file_only_window_active_count"));
        Assert.True(decomposition.ContainsKey("max_fixed_file_only_window_bytes"));
        Assert.True(decomposition.ContainsKey("cycle_goodput_average_bytes_per_second"));
        Assert.True(decomposition.ContainsKey("gui_progress_timeout_count"));
        Assert.True(decomposition.ContainsKey("terminal_missing_after_progress_timeout"));
        Assert.True(decomposition.ContainsKey("progress_timeout_with_receiver_gap_stall"));
        Assert.True(decomposition.ContainsKey("artifact_slice_start_reason"));
        Assert.True(decomposition.ContainsKey("artifact_slice_end_reason"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_HealthyLimitedInsideRecoveryHold_IsNotStickyLimited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_limited_recovery_hold";
        var lines = BuildCleanCompletedTransferFixture(transferId)
            .Append(LogLine("event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=8; chunk_count_prepared=22; raw_bytes_prepared=473088; read_duration_ms=0; batch_prepare_duration_ms=0; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=500; inter_schedule_gap_max_ms=500; credit_wait_duration_ms=1500; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2472960; raw_bytes_received_per_second=1236480; contiguous_bytes_committed=2408448; contiguous_bytes_committed_per_second=1204224; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1470; highest_received_chunk_index=1478; late_arrival_distance=8; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1538; granted_window_bytes=1462272; write_batch_count=39; write_batch_bytes=2472960; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=1236480; sparse_written_ahead_bytes=64512; sparse_gap_count=0"))
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; reason=ack_only; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=537600; effective_granted_window_bytes=1462272; current_credit_chunks=68; desired_credit_chunks=68; low_watermark_credit_chunks=62; credit_remaining_chunks=68; credit_desired_chunks=68; credit_remaining_bytes=1462272; credit_desired_bytes=1462272; granted_until_chunk_index_exclusive=1538; target_granted_until_chunk_index_exclusive=1538; grant_base_chunk_index=1470; grant_base_reason=sparse_ahead; sparse_ahead_bytes=129024; credit_base_chunk_index=1470; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=2623488; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); proactive_repair_pressure_state=(none); proactive_repair_age_ms=400; same_frontier_unfilled_ms=0; limited_recovery_clean_ms=400; limited_recovery_hold_ms=750; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1464; highest_received_chunk_index=1469; late_arrival_distance=5; pending_chunk_count=0; pending_bytes=0"))
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=8; payload_bytes_sent=520000; payload_bytes_per_second=260000; payload_bytes_enqueued=520000; payload_bytes_enqueued_per_second=260000; inter_enqueue_gap_p95_ms=500; inter_enqueue_gap_max_ms=500; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=16; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=8; sample_window_ms=2000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");

        Assert.NotEqual("sticky_limited_without_pressure", decomposition["likely_limiter"]);
        Assert.Equal("0", decomposition["sticky_limited_without_pressure_count"]);
        Assert.Equal("1", decomposition["limited_recovery_block_none_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PumpModeEventsInferActiveReceiverFeedbackPump()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_pump_inferred")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=2; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=1; enqueue_to_send_age_ms=900; send_duration_ms=120"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("0", throughput["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", throughput["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", throughput["slice_started_after_pump_start"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", decomposition["slice_started_after_pump_start"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProgressTimeoutSparseGapClassifiesFrontierRepairStalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_sparse_progress_timeout";
        var lines = new[]
        {
            LogLine($"event=filetransfer_profile_selected; transport=nkn; transfer_id={transferId}; session_id=sess_a; protocol_version=6; profile=v4_live; target_window_bytes=16777216; granted_window_bytes=16777216"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_receiver_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; reason=seekable_readwrite_destination; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_sender_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=516096; raw_bytes_per_second=258048; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=24; chunks_accepted_for_transport=2860; remote_next_expected_chunk_index=2507; remote_granted_until_chunk_index_exclusive=2890; remote_granted_window_bytes=8232960; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=12; repair_send_count=2"),
            LogLine($"event=filetransfer_v4_receiver_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=516096; raw_bytes_received_per_second=258048; contiguous_bytes_committed=516096; contiguous_bytes_committed_per_second=258048; pending_chunk_count=0; pending_bytes=0; next_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; oldest_gap_age_ms=42000; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; write_batch_count=8; write_batch_bytes=516096; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=258048; sparse_written_ahead_bytes=7587072; sparse_gap_count=1"),
            LogLine($"event=filetransfer_v4_gap_stall_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; stall_duration_ms=42294; pending_bytes=0; granted_window_bytes=8232960"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=12000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=18000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_v4_receiver_feedback_enqueued; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=2; coalesced_count=1"),
            LogLine($"event=filetransfer_v4_receiver_feedback_sent; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=1; enqueue_to_send_age_ms=1030; send_duration_ms=649"),
            LogLine("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=200; send_failures=0; queue_clears=0; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_p95_ms=4; configured_concurrency=4; effective_concurrency=4; in_flight_max=3"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=2507; receiver_highest_chunk=2860; progress_events=4729"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Contains("progress_timeout_with_receiver_gap_stall", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("sparse_frontier_gap_repair_stalled", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", decomposition["slice_started_after_pump_start"]);
        Assert.Equal("1", decomposition["gui_progress_timeout_count"]);
        Assert.Equal("1", decomposition["terminal_missing_after_progress_timeout"]);
        Assert.Equal("1", decomposition["progress_timeout_with_receiver_gap_stall"]);
        Assert.Equal("live_soak_failure_context", decomposition["artifact_slice_start_reason"]);
        Assert.Equal("gui_progress_timeout", decomposition["artifact_slice_end_reason"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProgressTimeoutAfterSecureDataEnvelopeWithoutDispatch_ClassifiesDispatchMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_data_dispatch_missing";
        var lines = new[]
        {
            LogLine($"event=nkn_inbound_envelope_received; channel=bulk; reason=(none); envelope_type=file_transfer_data_frame; payload_len=64917; envelope_payload_len=64840; msg_id=msg1; source_len=84; source_matches_local=0; expected_source_available=1; source_matches_expected=1; is_topic=0"),
            LogLine($"event=filetransfer_envelope_received; transport=nkn; message_type=file_transfer_data_frame; transfer_id={transferId}; source=nlink-test-bulk.example"),
            LogLine("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=200; send_failures=0; queue_clears=0; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_p95_ms=4; configured_concurrency=4; effective_concurrency=4; in_flight_max=3"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=2855"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("filetransfer_data_session_dispatch_missing", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("1", decomposition["filetransfer_secure_data_frame_envelope_received_count"]);
        Assert.Equal("0", decomposition["filetransfer_data_frame_dispatched_count"]);
        Assert.Equal("1", decomposition["filetransfer_data_frame_dispatch_missing_count"]);

        var promotion = ReadArtifactReport(result.ArtifactDir, "v4-promotion-decision.txt");
        Assert.Equal("hold_inconclusive", promotion["decision"]);
        Assert.Equal("filetransfer_data_session_dispatch_missing", promotion["reason"]);
        Assert.Equal("transport_data_session_lifecycle_dispatch", promotion["next_focus"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackProgressTimeout_EmitsFallbackDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_timeout_diagnostics";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transport=nkn; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_fallback"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transport=nkn; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_fallback"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=190; oldest_gap_age_ms=45000"),
            LogLine($"event=filetransfer_v6_chunk_batch_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=101; chunk_count=32; timeout_ms=2500; transport_epoch=3"),
            LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=101; requested_chunk_count=1; post_tuna_fallback_survival=1"),
            LogLine($"event=filetransfer_v6_receiver_state_deferred; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; next_chunk_index=101; highest_received_chunk_index=190"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=101; receiver_highest_chunk=190; progress_events=4888"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Equal("terminal_evidence_missing", verdict["fallback_v6_terminal_missing_reason"]);
        Assert.Equal("100", verdict["fallback_v6_last_committed_chunk_index"]);
        Assert.Equal("190", verdict["fallback_v6_highest_observed_chunk_index"]);
        Assert.Equal("45000", verdict["fallback_v6_oldest_unrecovered_gap_age_ms"]);
        Assert.Equal("1", verdict["fallback_v6_chunk_send_timeout_count"]);
        Assert.Equal("1", verdict["fallback_v6_frontier_request_count"]);
        Assert.Equal("1", verdict["fallback_v6_receiver_state_deferred_count"]);
        Assert.Equal("1", verdict["fallback_v6_sender_still_repairing"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SparseCreditGrantSummary_ReportsCreditBaseAndTopup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sparse_credit")
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_credit; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=8388608; effective_granted_window_bytes=8440320; current_credit_chunks=120; desired_credit_chunks=392; low_watermark_credit_chunks=353; credit_remaining_chunks=120; credit_desired_chunks=392; credit_remaining_bytes=2580480; credit_desired_bytes=8429568; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1272; target_base_chunk_index=880; target_base_reason=sparse_ahead; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=3870720; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=700; highest_received_chunk_index=879; late_arrival_distance=179; pending_chunk_count=0; pending_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["grant_credit_base_sparse_count"]);
        Assert.Equal("0", throughput["grant_credit_base_contiguous_count"]);
        Assert.Equal("1", throughput["sparse_credit_topup_count"]);
        Assert.Equal("2580480.00", throughput["average_credit_remaining_bytes"]);
        Assert.Equal("301056", throughput["max_sparse_credit_advance_bytes"]);
        Assert.Equal("1", throughput["sparse_credit_eligible_count"]);
        Assert.Equal("1", throughput["sparse_credit_used_count"]);
        Assert.Equal("0", throughput["sparse_credit_blocked_count"]);
        Assert.Equal("100.000", throughput["sparse_credit_reorder_use_ratio_percent"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["grant_credit_base_sparse_count"]);
        Assert.Equal("1", decomposition["sparse_credit_topup_count"]);
        Assert.Equal("100.000", decomposition["sparse_credit_reorder_use_ratio_percent"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FixedFileOnlyWindowSummary_ReportsDiagnosticMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_fixed_file_only_window")
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_fixed_file_only_window; session_id=sess_a; reason=target_changed; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=16795648; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1780; target_granted_until_chunk_index_exclusive=1780; target_base_chunk_index=1000; target_base_reason=sparse_ahead; grant_base_chunk_index=1000; grant_base_reason=sparse_ahead; sparse_ahead_bytes=4300800; credit_base_chunk_index=1000; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=262144; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); limited_recovery_clean_ms=0; limited_recovery_block_reason=(none); fixed_file_only_window_active=1; fixed_file_only_window_bytes=16777216; next_chunk_index=800; highest_received_chunk_index=999; late_arrival_distance=199; pending_chunk_count=0; pending_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["fixed_file_only_window_active_count"]);
        Assert.Equal("16777216", decomposition["max_fixed_file_only_window_bytes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PayloadEfficiencySummary_UsesDominantConcreteBatchProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_payload_profile")
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id=transfer_payload_profile; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=64615; secure_payload_bytes=64840; bridge_payload_bytes=64917; bridge_command_bytes=65017; max_allowed_bytes=65536; batch_profile=Packed3x21KiB; batch_chunk_count=3; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id=transfer_payload_profile; session_id=sess_a; chunk_range=3-5; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=21595; secure_payload_bytes=21820; bridge_payload_bytes=21897; bridge_command_bytes=21997; max_allowed_bytes=65536; batch_profile=Current; batch_chunk_count=1; raw_to_bridge_payload_ratio=0.982; bridge_payload_fill_percent=33.41"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("Packed3x21KiB", payload["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FramesWithoutTerminal_ReturnsInconclusive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync([
            LogLine("event=filetransfer_binary_frame_sent; transfer_id=transfer_open; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; raw_chunk_bytes=49152; chunk_count=2")
        ]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE", verdict["verdict"]);
        Assert.Equal("transfer-terminal-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PayloadReject_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_reject")
            .Append(LogLine("event=filetransfer_transport_payload_rejected; transport=nkn; transfer_id=transfer_reject; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; bridge_command_bytes=70000; max_allowed_bytes=65536"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostCompletionLateSenderIgnored_DoesNotFailCleanCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_completion_late_sender";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0; reason=post_completion_late_sender_frame; source=nlink-helper-bulk.test; msg_id=late_frame_after_terminal"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostCompletionLifecycleDataFrameReject_DoesNotFailCleanCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_completion_lifecycle_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var inboundTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(inboundTerminalIndex > 0);
        lines.Insert(
            inboundTerminalIndex + 1,
            LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=lifecycle_data_frame_unsupported; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_lifecycle_after_terminal"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTerminalUnknownTransferDataFrameReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_terminal_unknown_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_frame_after_terminal"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTerminalLateSenderReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_terminal_declined_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=post_terminal_late_sender_frame_declined; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_frame_after_decline"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PreTerminalUnknownTransferDataFrameReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_pre_terminal_unknown_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=early_frame_before_terminal"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FailOnGate_ReturnsNonZeroForHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_fail_gate")
            .Append(LogLine("event=filetransfer_data_frame_decode_failed; transport=nkn; transfer_id=transfer_fail_gate; session_id=sess_a"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-FailOnGate"]);

        Assert.Equal(1, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_HighReorderButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_reorder")
            .Append(LogLine("event=filetransfer_v4_throughput_summary; transfer_id=transfer_reorder; session_id=sess_a; useful_payload_bytes_per_second=4000000; control_frames_per_mib=1.00; granted_window_bytes=4194304; chunk_size_bytes=24576; profile=healthy_expanded"))
            .Concat(Enumerable.Range(0, 50).Select(index =>
                LogLine($"event=filetransfer_reorder_pressure; transfer_id=transfer_reorder; session_id=sess_a; next_expected_chunk={index}; highest_received_chunk={index + 82}; late_arrival_distance=82; outstanding_count=4; pipeline_depth=4; chunk_size_bytes=24576")))
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_reorder; session_id=sess_a; previous_profile=healthy_expanded; updated_profile=healthy_limited; reason=high_reorder; target_window_bytes=524288; granted_window_bytes=4194304; late_arrival_distance=82; timeout_streak=0"))
            .Concat(Enumerable.Range(50, 50).Select(index =>
                LogLine($"event=filetransfer_reorder_pressure; transfer_id=transfer_reorder; session_id=sess_a; next_expected_chunk={index}; highest_received_chunk={index + 82}; late_arrival_distance=82; outstanding_count=4; pipeline_depth=4; chunk_size_bytes=24576")))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("100", repair["reorder_event_count"]);
        Assert.Equal("82", repair["max_late_arrival_distance"]);
        Assert.Equal("82", repair["p95_late_arrival_distance"]);
        Assert.Equal("4194304", repair["max_v4_granted_window_bytes"]);
        Assert.Equal("1", repair["v4_profile_changed_count"]);
        Assert.Equal("50", repair["reorder_by_profile.healthy_expanded.count"]);
        Assert.Equal("50", repair["reorder_by_profile.healthy_limited.count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanConservativeStartupFastRamp_ReportsStartupEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_startup_fast_ramp")
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; previous_profile=nkn_conservative_startup; updated_profile=nkn_conservative_startup_probe; reason=startup_probe; target_window_bytes=1048576; granted_window_bytes=524288; late_arrival_distance=0; timeout_streak=0; conservative_startup_duration_ms=550; bytes_before_startup_exit=524288; startup_exit_reason=(none); startup_probe_window_bytes=1048576; first_repair_or_timeout_before_startup_exit=0"))
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; previous_profile=nkn_conservative_startup_probe; updated_profile=healthy; reason=startup_fast_clean; target_window_bytes=2097152; granted_window_bytes=1048576; late_arrival_distance=0; timeout_streak=0; conservative_startup_duration_ms=1100; bytes_before_startup_exit=1048576; startup_exit_reason=startup_fast_clean; startup_probe_window_bytes=0; first_repair_or_timeout_before_startup_exit=0"))
            .Append(LogLine("event=filetransfer_v4_throughput_summary; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; useful_payload_bytes_per_second=5000000; control_frames_per_mib=1.00; granted_window_bytes=2097152; chunk_size_bytes=24576; profile=healthy; conservative_startup_duration_ms=1100; bytes_before_startup_exit=1048576; startup_exit_reason=startup_fast_clean; startup_probe_window_bytes=0; first_repair_or_timeout_before_startup_exit=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["conservative_startup_probe_count"]);
        Assert.Equal("1", throughput["conservative_startup_fast_clean_count"]);
        Assert.Equal("0", throughput["conservative_startup_adverse_count"]);
        Assert.Equal("1100", throughput["max_conservative_startup_duration_ms"]);
        Assert.Equal("1048576", throughput["max_bytes_before_startup_exit"]);
        Assert.Equal("1048576", throughput["max_startup_probe_window_bytes"]);
        Assert.Equal("0", throughput["first_repair_or_timeout_before_startup_exit_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverBufferPressureButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_pressure")
            .Append(LogLine("event=filetransfer_receiver_buffer_pressure_entered; transfer_id=transfer_receiver_pressure; session_id=sess_a; reason=soft_limit; pending_chunk_count=2100; pending_bytes=8601600; soft_limit_bytes=8388608; severe_limit_bytes=16777216; emergency_limit_bytes=67108864; next_chunk_index=0; highest_received_chunk_index=2100; late_arrival_distance=2100; granted_window_bytes=2097152"))
            .Append(LogLine("event=filetransfer_receiver_grant_clamped_for_buffer; transfer_id=transfer_receiver_pressure; session_id=sess_a; reason=soft_limit; pending_chunk_count=2100; pending_bytes=8601600; previous_target_granted_until_exclusive=512; clamped_target_granted_until_exclusive=128; next_chunk_index=0; highest_received_chunk_index=2100; late_arrival_distance=2100; granted_window_bytes=2097152; soft_limit_bytes=8388608; severe_limit_bytes=16777216"))
            .Append(LogLine("event=filetransfer_receiver_write_batch_committed; transfer_id=transfer_receiver_pressure; session_id=sess_a; batch_chunk_count=64; batch_bytes=262144; write_duration_ms=12; pending_chunk_count=2036; pending_bytes=8339456; next_chunk_index=64; highest_received_chunk_index=2100; late_arrival_distance=2036; granted_window_bytes=1835008"))
            .Append(LogLine("event=filetransfer_receiver_buffer_pressure_exited; transfer_id=transfer_receiver_pressure; session_id=sess_a; pending_chunk_count=1000; pending_bytes=4096000; duration_ms=1250; exit_limit_bytes=4194304; next_chunk_index=1100; highest_received_chunk_index=2100; late_arrival_distance=1000; granted_window_bytes=524288"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_buffer_pressure_entered_count"]);
        Assert.Equal("1", repair["receiver_buffer_pressure_exited_count"]);
        Assert.Equal("1", repair["receiver_buffer_grant_clamped_count"]);
        Assert.Equal("1", repair["receiver_write_batch_committed_count"]);
        Assert.Equal("8601600", repair["max_receiver_pending_bytes"]);
        Assert.Equal("262144", repair["max_receiver_write_batch_bytes"]);
        Assert.Equal("12", repair["max_receiver_write_duration_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SparseReceiverTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sparse_receiver")
            .Append(LogLine("event=filetransfer_receiver_sparse_mode_selected; transfer_id=transfer_sparse_receiver; session_id=sess_a; reason=seekable_readable_destination; stream_can_read=1; stream_can_seek=1; stream_can_write=1; file_size_bytes=1048576; chunk_count=256; chunk_size_bytes=4096"))
            .Append(LogLine("event=filetransfer_receiver_sparse_write_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; written_chunk_count=64; written_bytes=262144; sparse_write_bytes_per_second=5242880; write_duration_ms=50; pending_chunk_count=0; pending_bytes=0; queued_memory_bytes=0; sparse_written_ahead_chunks=63; sparse_written_ahead_bytes=258048; sparse_gap_count=1; next_chunk_index=0; highest_received_chunk_index=64; late_arrival_distance=64; granted_window_bytes=1048576"))
            .Append(LogLine("event=filetransfer_receiver_sparse_commit_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; contiguous_chunks_committed=64; contiguous_bytes_committed=262144; next_chunk_index=64; bytes_committed=262144; sparse_written_ahead_chunks=0; sparse_written_ahead_bytes=0; sparse_gap_count=0; pending_chunk_count=0; pending_bytes=0; queued_memory_bytes=0; highest_received_chunk_index=64; late_arrival_distance=0; granted_window_bytes=1048576"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1048576; raw_bytes_received_per_second=524288; contiguous_bytes_committed=1048576; contiguous_bytes_committed_per_second=524288; pending_chunk_count=0; pending_bytes=0; next_chunk_index=256; highest_received_chunk_index=255; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=256; granted_window_bytes=0; write_batch_count=4; write_batch_bytes=1048576; write_duration_ms=40; sparse_mode=1; sparse_write_bytes_per_second=524288; sparse_written_ahead_bytes=258048; sparse_gap_count=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("5242880", throughput["max_sparse_write_bytes_per_second"]);
        Assert.Equal("258048", throughput["max_sparse_written_ahead_bytes"]);
        Assert.Equal("1", throughput["max_sparse_gap_count"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_sparse_mode_selected_count"]);
        Assert.Equal("1", repair["receiver_sparse_write_summary_count"]);
        Assert.Equal("1", repair["receiver_sparse_commit_summary_count"]);
        Assert.Equal("258048", repair["max_sparse_written_ahead_bytes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverFeedbackPumpTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_pump_started; transfer_id=transfer_receiver_feedback; session_id=sess_a; queue_limit=64; mode=pump"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=low_watermark; mode=pump; queue_depth=1; queue_limit=64"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_coalesced; transfer_id=transfer_receiver_feedback; session_id=sess_a; previous_frame_type=filetransfer.receiver_state.v6; frame_type=filetransfer.receiver_state.v6; reason=ack_only; mode=pump; queue_depth=1; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=ack_only; mode=pump; send_duration_ms=23; enqueue_to_send_age_ms=117; queue_depth=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_summary; transfer_id=transfer_receiver_feedback; session_id=sess_a; mode=pump; queue_depth=0; queue_limit=64; enqueued=2; sent=1; coalesced=1; failed=0; max_queue_depth=1; max_enqueue_to_send_age_ms=117; max_send_duration_ms=23"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", throughput["receiver_feedback_enqueued_count"]);
        Assert.Equal("1", throughput["receiver_feedback_sent_count"]);
        Assert.Equal("1", throughput["receiver_feedback_coalesced_count"]);
        Assert.Equal("0", throughput["receiver_feedback_failed_count"]);
        Assert.Equal("1", throughput["max_receiver_feedback_queue_depth"]);
        Assert.Equal("117", throughput["max_receiver_feedback_enqueue_to_send_age_ms"]);
        Assert.Equal("23", throughput["max_receiver_feedback_send_duration_ms"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", repair["receiver_feedback_sent_count"]);
        Assert.Equal("1", repair["receiver_feedback_coalesced_count"]);
        Assert.Equal("0", repair["receiver_feedback_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SlowDirectReceiverFeedback_ClassifiesAsReceiverFeedbackBlocking()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback_blocking")
            .Append(LogLine("event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default"))
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0"))
            .Append(LogLine("event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1272; remote_granted_window_bytes=16602112; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0"))
            .Append(LogLine("event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1272; granted_window_bytes=8429568; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=low_watermark; mode=direct; send_duration_ms=900; enqueue_to_send_age_ms=0; queue_depth=0"))
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");

        Assert.Equal("receiver_feedback_blocking_limited", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["receiver_feedback_sent_count"]);
        Assert.Equal("900", decomposition["max_receiver_feedback_send_duration_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderCacheTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_cache")
            .Append(LogLine("event=filetransfer_sender_repair_cache_policy; transfer_id=transfer_sender_cache; session_id=sess_a; source_can_seek=1; seekable_target_bytes=8388608; seekable_hard_limit_bytes=16777216; non_seekable_hard_limit_bytes=67108864; cache_hard_limit_bytes=16777216"))
            .Append(LogLine("event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_cache; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=7; chunk_count_sent=28; chunks_accepted_for_transport=64; remote_next_expected_chunk_index=32; remote_granted_until_chunk_index_exclusive=96; remote_granted_window_bytes=2621440; sent_cache_chunk_count=32; sent_cache_bytes=1310720; source_can_seek=1; cache_hard_limit_bytes=16777216; cache_hit_count=2; cache_miss_count=1; source_reread_count=1; cache_eviction_count=3; repair_chunk_skipped_count=1; send_wait_count=0; repair_send_count=2"))
            .Append(LogLine("event=filetransfer_sender_repair_cache_summary; transfer_id=transfer_sender_cache; session_id=sess_a; source_can_seek=1; cache_chunk_count=32; cache_bytes=1310720; cache_hard_limit_bytes=16777216; cache_target_bytes=8388608; cache_eviction_count=3; reason=evicted_to_target"))
            .Append(LogLine("event=filetransfer_sender_repair_chunk_skipped; transfer_id=transfer_sender_cache; session_id=sess_a; reason=not_yet_sent; chunk_index=100; remote_next_expected_chunk_index=32; chunks_accepted_for_transport=64; chunk_count=256"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1310720", throughput["max_sender_repair_cache_bytes"]);
        Assert.Equal("16777216", throughput["max_sender_repair_cache_hard_limit_bytes"]);
        Assert.Equal("2", throughput["sender_repair_cache_hit_count"]);
        Assert.Equal("1", throughput["sender_repair_cache_miss_count"]);
        Assert.Equal("1", throughput["sender_repair_source_reread_count"]);
        Assert.Equal("3", throughput["sender_repair_cache_eviction_count"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_repair_chunk_skipped_count"]);
        Assert.Equal("1", repair["sender_repair_chunk_skipped_not_yet_sent_count"]);
        Assert.Equal("0", repair["sender_cache_exhausted_count"]);
        Assert.Equal("0", repair["sender_repair_unavailable_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderCacheExhausted_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_cache_exhausted")
            .Append(LogLine("event=filetransfer_sender_cache_exhausted; transfer_id=transfer_sender_cache_exhausted; session_id=sess_a; reason=non_seekable_cache_limit; chunk_index=1700; source_can_seek=0; cache_chunk_count=1700; cache_bytes=69632000; cache_hard_limit_bytes=67108864; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=1700; error_code=sender_cache_exhausted"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_cache_exhausted_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderRepairUnavailable_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_repair_unavailable")
            .Append(LogLine("event=filetransfer_sender_repair_unavailable; transfer_id=transfer_sender_repair_unavailable; session_id=sess_a; reason=non_seekable_cache_miss; chunk_index=12; source_can_seek=0; cache_chunk_count=4; cache_bytes=163840; cache_hard_limit_bytes=67108864; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=64; error_code=sender_repair_unavailable"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_repair_unavailable_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverBufferExhausted_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_exhausted")
            .Append(LogLine("event=filetransfer_receiver_buffer_exhausted; transfer_id=transfer_receiver_exhausted; session_id=sess_a; pending_chunk_count=17000; pending_bytes=69632000; emergency_limit_bytes=67108864; next_chunk_index=0; highest_received_chunk_index=17000; late_arrival_distance=17000; granted_window_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverFeedbackFailure_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback_failed")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_failed; transfer_id=transfer_receiver_feedback_failed; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=queue_exhausted; mode=pump; queue_depth=64; error_code=receiver_feedback_queue_exhausted"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_summary; transfer_id=transfer_receiver_feedback_failed; session_id=sess_a; mode=pump; queue_depth=64; queue_limit=64; enqueued=64; sent=0; coalesced=0; failed=1; max_queue_depth=64; max_enqueue_to_send_age_ms=0; max_send_duration_ms=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["receiver_feedback_failed_count"]);
        Assert.Equal("64", throughput["max_receiver_feedback_queue_depth"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepairSetActivityButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_repair_set")
            .Append(LogLine("event=filetransfer_repair_set_requested; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; next_chunk_index=20; highest_received_chunk_index=120; granted_until_chunk_index_exclusive=128; pending_chunk_count=96; pending_bytes=3932160; late_arrival_distance=100; reason=timeout"))
            .Append(LogLine("event=filetransfer_repair_set_received; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; remote_next_expected_chunk_index=20; skipped_obsolete_count=0"))
            .Append(LogLine("event=filetransfer_repair_set_sent; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; sent_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; remote_next_expected_chunk_index=20; skipped_obsolete_count=0"))
            .Append(LogLine("event=filetransfer_repair_request_suppressed; transfer_id=transfer_repair_set; session_id=sess_a; reason=duplicate_recent; range_count=3; requested_chunk_count=4; next_chunk_index=20; highest_received_chunk_index=120; granted_until_chunk_index_exclusive=128; pending_chunk_count=96; pending_bytes=3932160"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["repair_set_requested_count"]);
        Assert.Equal("1", repair["repair_set_received_count"]);
        Assert.Equal("1", repair["repair_set_sent_count"]);
        Assert.Equal("1", repair["repair_request_suppressed_count"]);
        Assert.Equal("3", repair["max_repair_set_ranges"]);
        Assert.Equal("4", repair["max_repair_set_chunks"]);
        Assert.Equal("1", repair["total_repair_control_frame_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProactiveFrontierRepair_PopulatesRepairSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_frontier_repair")
            .Append(LogLine("event=filetransfer_frontier_gap_repair_eligible; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; attempt_count=1; range_count=1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=0; same_frontier_unfilled_ms=0; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; attempt_count=1; range_count=1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=0; same_frontier_unfilled_ms=0; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_received; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; scheduled_chunk_count=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_scheduled; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; queue_depth=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_sent; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_filled; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; start_chunk_index=100; requested_chunk_count=1; request_to_fill_ms=450; next_chunk_index=149; highest_received_chunk_index=149; committed_chunk_count=49; sparse_written_ahead_bytes=0; same_frontier_unfilled_ms=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_skipped; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=duplicate_recent; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=1200; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=300; same_frontier_unfilled_ms=300; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_suppressed; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=duplicate_recent; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=1200; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=300; same_frontier_unfilled_ms=300; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_proactive_frontier_repair_state_reset; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=frontier_advanced; start_chunk_index=100; requested_chunk_count=1; next_chunk_index=101; highest_received_at_repair=148; current_highest_received_chunk_index=148; proactive_repair_age_ms=350; same_frontier_unfilled_ms=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["proactive_frontier_repair_eligible_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_requested_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_received_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_scheduled_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_sent_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_filled_count"]);
        Assert.Equal("450", repair["max_frontier_repair_request_to_fill_ms"]);
        Assert.Equal("1", repair["proactive_frontier_repair_skipped_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_skipped_duplicate_recent_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_suppressed_count"]);
        Assert.Equal("1", repair["stale_proactive_repair_state_reset_count"]);
        Assert.Equal("0", repair["benign_gap_skip_limited_policy_count"]);
        Assert.Equal("1200", repair["max_proactive_frontier_repair_gap_age_ms"]);
        Assert.Equal("4", repair["proactive_repair_benign_count"]);
        Assert.Equal("0", repair["proactive_repair_hard_limited_count"]);
        Assert.Equal("300", repair["max_proactive_repair_age_ms"]);
        Assert.Equal("300", repair["max_same_frontier_unfilled_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyNegotiationRejected_IsTrackedInProtocolShape()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_legacy_rejected")
            .Append(LogLine("event=filetransfer_legacy_negotiation_rejected; transfer_id=transfer_legacy_rejected; session_id=sess_a; direction=Inbound; offered_version=2; accepted_version=(none); reason=offer_protocol_not_v4"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["legacy_negotiation_rejected_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4Negotiation_IsTrackedInProtocolShape()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_negotiated");

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v6_negotiated_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanV4Transfer_IsFirstClassPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedV4TransferFixture("transfer_v4_clean"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("6", throughput["data_protocol_version"]);
        Assert.Equal("1.000000", throughput["v4_batch_ratio"]);
        Assert.Equal("1", throughput["v4_state_feedback_count"]);
        Assert.Equal("1", throughput["v4_feedback_redundant_success_count"]);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v6_sender_started_count"]);
        Assert.Equal("1", protocol["v6_receiver_started_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);

        var promotion = ReadArtifactReport(result.ArtifactDir, "v4-promotion-decision.txt");
        Assert.Equal("hold_inconclusive", promotion["decision"]);
        Assert.Equal("long_live_matrix_incomplete", promotion["reason"]);
        Assert.Equal("6", promotion["data_protocol_version"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanRegularNknV4Transfer_IsFirstClassPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedRegularNknV4TransferFixture("transfer_regular_v4_clean"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("4", throughput["data_protocol_version"]);
        Assert.Equal("1.000000", throughput["v4_batch_ratio"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("2", protocol["v4_negotiated_count"]);
        Assert.Equal("1", protocol["v4_sender_started_count"]);
        Assert.Equal("1", protocol["v4_receiver_started_count"]);
        Assert.Equal("0", protocol["v6_sender_started_count"]);
        Assert.Equal("0", protocol["v6_receiver_started_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V6ControlHealth_IsTrackedInThroughputSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v6_control_health";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; session_id=sess_a; reason=periodic; epoch=2; contiguous_committed_chunk_index=10; durable_received_highest_chunk_index=12; requested_until_chunk_index_exclusive=18; missing_range_count=1; bytes_committed=215040; destination_mode=sparse; transfer_paused=0"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_received; transfer_id={transferId}; session_id=sess_a; epoch=2; previous_remote_frontier_chunk_index=9; committed_frontier_chunk_index=10; diagnostic_credit_until_chunk_index_exclusive=18; missing_range_count=1; bytes_committed=215040; transfer_paused=0"))
            .Append(LogLine($"event=filetransfer_v6_receiver_request_window_sent; transfer_id={transferId}; session_id=sess_a; reason=periodic; epoch=2; requested_chunk_count=8; requested_until_chunk_index_exclusive=18; missing_range_count=1; request_window_chunks=8; frontier_stalled=1; transport_epoch=1; recovery_mode=regular_nkn"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_deferred; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; next_chunk_index=10; highest_received_chunk_index=18"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled_tail_window; current_committed_chunk_index=10; highest_received_chunk_index=18; accept_window_end_chunk_index=22; tail_chunks_remaining=5; elapsed_since_state_ms=220"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; transport_epoch=1; repair_request_id=req-a; recovery_mode=regular_nkn; start_chunk_index=10; requested_chunk_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_failed; direction=inbound; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; error=TimeoutException"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_deferred; direction=inbound; transfer_id={transferId}; session_id=sess_a; frontier_chunk_index=10; highest_received_chunk_index=18; elapsed_ms=300; stall_grace_ms=500; reason=frontier_gap; utc=2026-05-15T12:00:00.0000000Z"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_received; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; first_start_chunk_index=10; first_chunk_count=1; range_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_coalesced; direction=inbound; transfer_id={transferId}; session_id=sess_a; previous_frontier_chunk_index=10; current_frontier_chunk_index=10; elapsed_ms=120; retry_interval_ms=500; reason=frontier_stalled"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; first_start_chunk_index=10; first_chunk_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_preempted_normal_pipeline; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; normal_request_count=128; in_flight_send_count=6; requested_chunk_already_in_flight=0; sender_pipeline_generation=3"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=2; transport_epoch=1; normal_request_count=128; in_flight_send_count=6; requested_chunk_already_in_flight=0; sender_pipeline_generation=4; remote_frontier_chunk_index=10"))
            .Append(LogLine($"event=filetransfer_v6_normal_refill_deferred; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_key=normal-a; remote_frontier_chunk_index=10; pending_ahead_chunk_count=256; refill_low_watermark_chunks=64; deferred_chunk_count=128; sent_awaiting_ack_count=96; in_flight_send_count=4"))
            .Append(LogLine($"event=filetransfer_v6_normal_send_ahead_limited; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_key=normal-a; remote_frontier_chunk_index=10; send_ahead_end_exclusive=522; suppressed_chunk_count=24; sent_awaiting_ack_count=96; in_flight_send_count=4"))
            .Append(LogLine($"event=filetransfer_v6_regular_nkn_frontier_pressure_entered; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=2; remote_frontier_chunk_index=10; durable_received_highest_chunk_index=18; missing_range_count=2; pressure_until_chunk_index=522; send_ahead_limit_chunks=256; refill_low_watermark_chunks=128"))
            .Append(LogLine($"event=filetransfer_v6_regular_nkn_frontier_pressure_cleared; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=3; reason=receiver_state_progress; pressure_start_chunk_index=10; pressure_until_chunk_index=522; remote_frontier_chunk_index=522; active_ms=1200"))
            .Append(LogLine($"event=filetransfer_v6_sender_waiting_for_requests; transfer_id={transferId}; session_id=sess_a; reason=no_receiver_requests; priority_request_count=0; normal_request_count=0"))
            .Append(LogLine($"event=filetransfer_v6_unsolicited_chunk_ignored; transfer_id={transferId}; session_id=sess_a; mode=sparse_seekable; reason=outside_accept_window; chunk_index=60; committed_frontier_chunk_index=10; request_window_end_chunk_index=18; accept_window_end_chunk_index=22"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=10; batch_chunk_count=1; raw_bytes=21504; request_key=req-a; priority=1; regular_nkn_redundant=0; repair_request_id=req-a"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=18; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_deferred_for_recovery; transfer_id={transferId}; session_id=sess_a; start_chunk_index=21; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; current_transport_epoch=2; epoch_state=waiting; handoff_kind=regular_nkn_recovery; target_transport=regular_nkn; pull_transport_paused=1; resume_request_pending=0; post_tuna_recovery_active=1; unresolved_epoch=1; requeued_chunk_count=3; error=InvalidOperationException; message=Bridge_disconnected"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_timeout; transfer_id={transferId}; session_id=sess_a; start_chunk_index=24; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; timeout_ms=1500; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline; transfer_id={transferId}; session_id=sess_a; start_chunk_index=27; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; sender_pipeline_generation=5; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_completed; transfer_id={transferId}; session_id=sess_a; start_chunk_index=24; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_canceled; transfer_id={transferId}; session_id=sess_a; start_chunk_index=27; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_failed; transfer_id={transferId}; session_id=sess_a; start_chunk_index=30; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1; error=InvalidOperationException"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["v6_receiver_state_sent_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_received_count"]);
        Assert.Equal("1", throughput["v6_receiver_request_window_sent_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_deferred_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_coalesced_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_sent_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_failed_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_deferred_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_received_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_coalesced_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_duplicate_ignored_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_preempted_normal_pipeline_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_frontier_preempted_normal_pipeline_count"]);
        Assert.Equal("1", throughput["v6_normal_refill_deferred_count"]);
        Assert.Equal("1", throughput["v6_normal_send_ahead_limited_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_frontier_pressure_entered_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_frontier_pressure_cleared_count"]);
        Assert.Equal("1", throughput["v6_sender_waiting_for_requests_count"]);
        Assert.Equal("1", throughput["v6_unsolicited_chunk_ignored_count"]);
        Assert.Equal("2", throughput["v6_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_normal_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_priority_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_redundant_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_deferred_for_recovery_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_timeout_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_canceled_for_pipeline_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_completed_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_canceled_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairBatchProfile_DoesNotOverrideRunPayloadProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_profile";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=control; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("v4_default_21k", throughput["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairOnlyProfileEvidence_InfersDefaultRunPayloadProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_only_profile";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line => !line.Contains("batch_profile=v4_default_21k", StringComparison.Ordinal))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=control_bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=control_bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_GenericTransferTerminal_IsCleanTerminalEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_generic_terminal";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line =>
                !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .Append(LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var terminal = ReadArtifactReport(result.ArtifactDir, "transfer-terminal-summary.txt");
        Assert.Equal("1", terminal["inbound_terminal_count"]);
        Assert.Equal("1", terminal["outbound_terminal_count"]);
        Assert.Equal("Completed,Completed", terminal["terminal_states"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_AllTransferProgressTimeout_DoesNotOverrideCleanGenericTerminalPair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_generic_terminal_after_timeout";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line =>
                !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .Append(LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine("event=filetransfer_live_progress_timeout; transfer_id=(all); reason=no useful data progress for 120s; total_wait_s=155; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3142"))
            .Append(LogLine("event=filetransfer_artifact_slice_summary; transfer_id=(all); artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var terminal = ReadArtifactReport(result.ArtifactDir, "transfer-terminal-summary.txt");
        Assert.Equal("1", terminal["inbound_terminal_count"]);
        Assert.Equal("1", terminal["outbound_terminal_count"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["gui_progress_timeout_count"]);
        Assert.Equal("0", decomposition["terminal_missing_after_progress_timeout"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4FeedbackBothFailed_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_feedback_failed")
            .Append(LogLine("event=filetransfer_v4_feedback_both_failed; transfer_id=transfer_v4_feedback_failed; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=both_lanes_failed"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PrimaryRegularNknQuietCheckpointFeedbackCanceledAfterCompletion_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_primary_quiet_checkpoint_feedback_canceled";
        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedV4TransferFixture(transferId));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; runtime_profile=PrimaryRegularNknBulkV6; bridge_recovery_policy=primary_regular_nkn_quiet; selection_reason=conservative_regular_nkn", secondsOffset: 10),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; runtime_profile=PrimaryRegularNknBulkV6; credit_profile=v4_sparse; frame_profile=v6; recovery_profile=regular_nkn_quiet; bridge_recovery_policy=primary_regular_nkn_quiet; activation=primary_regular_nkn", secondsOffset: 10),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException", secondsOffset: 20),
                LogLine($"event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable; direction=outbound; transfer_id={transferId}; session_id=sess_a; reason=checkpoint_sync_send_timeout; recovery_action=request_bridge_recovery; request_id=v6-regular-nkn-checkpoint-sync:7; frame_type=filetransfer.frontier_request.v6; recovery_mode=regular_nkn_checkpoint_sync; priority=checkpoint_sync; failure_count=2; bridge_recovery_policy=primary_regular_nkn_quiet", secondsOffset: 20),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_id=v6-regular-nkn-checkpoint-sync:7; timeout_ms=7500", secondsOffset: 30),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_a; reason=checkpoint_sync_send_timeout; request_id=v6-regular-nkn-checkpoint-sync:7; failure_count=2; bridge_recovery_policy=primary_regular_nkn_quiet", secondsOffset: 30),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; stall_reason=control_receive_stalled; attempt=1; max_restarts=4; consecutive_zero_receive_windows=2; frames_sent_since_last=6; control_last_received_age_ms=12000; bulk_last_received_age_ms=90", secondsOffset: 40),
                LogLine("event=nkn_bridge_receive_stall_recovery_completed; connect_key=test; recovery_count=1; elapsed_ms=1200; control_ready=1; bulk_ready=1", secondsOffset: 80),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRequestCanceledDuringRecovery_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture().ToList());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=3113; requested_chunk_count=1; post_tuna_fallback_survival=1", secondsOffset: 20),
                LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; request_id=v6-regular-nkn-state-refresh:test; timeout_ms=7500", secondsOffset: 30),
                LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_state_refresh_send_timeout; request_id=v6-regular-nkn-state-refresh:test; failure_count=1; feedback_silence_ms=1200; remote_frontier_chunk_index=3113; highest_accepted_chunk_index=3120; transport_backlog_chunks=7; available_credit_chunks=0; credit_ceiling_chunk_index=3121; rebind_generation=3; bridge_recovery_policy=PostTunaFallbackStrictRecovery", secondsOffset: 30),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException", secondsOffset: 40)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackStaleFrontierRequestCanceledAfterCleanProof_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture().ToList());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stall_repair_due; transport_epoch=3; repair_request_id=v6-frontier:3:4168:39; recovery_mode=frontier_repair_only; start_chunk_index=4168; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 20),
                LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:4; proven_committed_chunk=4168; proven_highest_observed_chunk=4707; reason=receiver_state_sparse_runtime", secondsOffset: 25),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=OperationCanceledException", secondsOffset: 30)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_OperationCanceledFeedbackWithoutPrimaryQuietPolicy_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_checkpoint_feedback_canceled_strict";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException"),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_id=v6-regular-nkn-checkpoint-sync:7; timeout_ms=7500"),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4ReceiverFailure_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_receiver_failed")
            .Append(LogLine("event=filetransfer_v4_receiver_failed; transfer_id=transfer_v4_receiver_failed; session_id=sess_a; error_code=v4_sparse_destination_required; reason=destination_not_seekable"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_receiver_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyDataFrameDuringV4_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_legacy_frame")
            .Append(LogLine("event=filetransfer_binary_frame_sent; transfer_id=transfer_v4_legacy_frame; session_id=sess_a; frame_type=filetransfer.chunk_batch.legacy; chunk_index=10-11; raw_chunk_bytes=49152; chunk_count=2"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["unexpected_legacy_data_frame_during_v4_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4MissingRangeRepair_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_repair")
            .Append(LogLine("event=filetransfer_v4_state_received; transfer_id=transfer_v4_repair; session_id=sess_a; epoch=3; contiguous_committed_chunk_index=4; durable_received_highest_chunk_index=12; credit_until_chunk_index_exclusive=1024; missing_range_count=2; requested_chunk_count=5"))
            .Append(LogLine("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_repair; session_id=sess_a; range_count=2; requested_chunk_count=5; skipped_obsolete_count=1; skipped_future_count=1; skipped_out_of_bounds_count=1"))
            .Append(LogLine("event=filetransfer_v4_repair_sent; transfer_id=transfer_v4_repair; session_id=sess_a; range_count=1; sent_chunk_count=3; skipped_obsolete_count=1; skipped_future_count=1; skipped_out_of_bounds_count=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("2", repair["v4_state_received_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_scheduled_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_sent_count"]);
        Assert.Equal("5", repair["v4_repair_requested_chunk_count"]);
        Assert.Equal("3", repair["v4_repair_sent_chunk_count"]);
        Assert.Equal("2", repair["v4_repair_skipped_obsolete_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairDeliveryModes_AreReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_delivery_modes";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=4:3:4:12:4:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=first_send"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=4:3:4:12:4:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=frontier_not_advanced"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=8:3:8:18:8:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=credit_stall"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=12:3:12:24:12:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=retry"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=16:3:16:30:16:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; repair_delivery_mode=bulk_only; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control_bulk; repair_delivery_mode=control_bulk_escalated; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("4", repair["v4_repair_delivery_control_bulk_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_retry_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_credit_stall_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_frontier_not_advanced_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);
        Assert.Equal("1", repair["v4_repair_batch_bulk_only_count"]);
        Assert.Equal("1", repair["v4_repair_batch_control_bulk_count"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("4", throughput["v4_repair_delivery_control_bulk_escalated_count"]);
        Assert.Equal("1", throughput["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["v4_repair_delivery_credit_stall_escalated_count"]);
        Assert.Equal("1", decomposition["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);
        Assert.Equal("1", decomposition["v4_repair_batch_control_bulk_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepeatedMissingRangeSchedules_ClassifiesRepairSpam()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_spam";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=2385; durable_received_highest_chunk_index=2806; credit_until_chunk_index_exclusive=3121; missing_range_count=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_spam_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairRequestedButNotServed_ClassifiesRepairRequestNotServed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_not_served";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_requested_not_received_by_sender", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairSentButNotFilled_ClassifiesRepairSentButNotFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_sent_unfilled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_sent_not_observed_by_receiver", decomposition["likely_limiter"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_requested_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_sent_count"]);
        Assert.Equal("0", repair["v4_repair_chunk_observed_count"]);
        Assert.Equal("0", repair["v4_repair_filled_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairObservedButNotAccepted_ClassifiesReceiverAcceptance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_observed_stale";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; matched_key_count=1; overlap_chunk_count=1; accepted_chunk_count=0; duplicate_or_stale_chunk_count=1; frontier_before=100; frontier_after=100; frontier_advanced=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_observed_but_not_accepted", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_chunk_observed_count"]);
        Assert.Equal("1", repair["v4_repair_observed_duplicate_or_stale_chunk_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairAcceptedButFrontierNotAdvanced_ClassifiesFrontierAdvance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_observed_no_frontier";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; matched_key_count=1; overlap_chunk_count=1; accepted_chunk_count=1; duplicate_or_stale_chunk_count=0; frontier_before=100; frontier_after=100; frontier_advanced=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_accepted_but_frontier_not_advanced", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_observed_accepted_chunk_count"]);
        Assert.Equal("0", repair["v4_repair_observed_frontier_advanced_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4CompletedSlowRepairFill_ClassifiesMissingRangeRepairLimited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_completed_slow_repair_fill";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; start_chunk_index=2786; requested_chunk_count=64; frontier_stall_age_ms=4200; credit_until_chunk_index_exclusive=3121; durable_received_highest_chunk_index=2785"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; attempt_count=1; range_count=1; requested_chunk_count=64; first_start_chunk_index=2786; last_end_chunk_exclusive=2850; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; matched_key_count=1; overlap_chunk_count=64; accepted_chunk_count=64; duplicate_or_stale_chunk_count=0; frontier_before=2786; frontier_after=2850; frontier_advanced=1"),
            LogLine($"event=filetransfer_v4_repair_filled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; request_to_fill_ms=3600; contiguous_committed_chunk_index=2850; durable_received_highest_chunk_index=3120"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=1; repair_scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=0; raw_bytes_sent=1376256; repair_send_count=64; credit_exhausted_time_ms=12000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=transfer_terminal; direction=inbound; session_id=sess_a; transfer_id={transferId}; error_code=(none); reason=Transfer complete.; saved_path=(none)"),
            LogLine($"event=transfer_terminal; direction=outbound; session_id=sess_a; transfer_id={transferId}; error_code=(none); reason=Transfer complete.")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_limited", decomposition["likely_limiter"]);
        Assert.Equal("3600", decomposition["v4_repair_request_to_fill_p95_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4CleanSlowLowWorkerUtilization_ClassifiesBulkUnderutilized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_clean_slow_bulk_underutilized";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; batch_chunk_count=3; raw_bytes=64512; raw_to_bridge_payload_ratio=0.972; bridge_payload_fill_percent=97.2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=112; frames_enqueued=112; payload_bytes_sent=67108864; payload_bytes_per_second=826350; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=4; worker_utilization_percent=19; worker_saturation_percent=2; send_p95_ms=4"),
            LogLine($"event=transfer_terminal; direction=inbound; session_id=sess_a; transfer_id={transferId}; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; saved_path=(none)"),
            LogLine($"event=transfer_terminal; direction=outbound; session_id=sess_a; transfer_id={transferId}; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("nkn_bulk_underutilized", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithEmptyState_ClassifiesStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_state_mismatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=frontier_stall_repair_due; epoch=7; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=99; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=900; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["v4_missing_range_due_state_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithSameEpochDifferentFrontier_DoesNotClassifyStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_epoch_reused_different_frontier";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=chunk_batch_committed; epoch=7; contiguous_committed_chunk_index=180; durable_received_highest_chunk_index=179; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=0; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["v4_missing_range_due_state_mismatch_count"]);
        Assert.NotEqual("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithEarlierSameEpochState_DoesNotClassifyStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_epoch_reused_earlier_state";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=chunk_batch_committed; epoch=7; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=99; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["v4_missing_range_due_state_mismatch_count"]);
        Assert.NotEqual("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4ProgressTimeoutWithNoTailRepair_ClassifiesFrontierTailRepairNeeded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_needed";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=41; contiguous_committed_chunk_index=2786; durable_received_highest_chunk_index=2785; credit_until_chunk_index_exclusive=3121; missing_range_count=0; frontier_stall_age_ms=45000; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=41; contiguous_committed_chunk_index=2786; durable_received_highest_chunk_index=2785; credit_until_chunk_index_exclusive=3121; missing_range_count=0; available_credit_bytes=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=0; completed_frames=0; failed_frames=0; in_flight_frames=0; raw_bytes_sent=0; repair_send_count=0; credit_exhausted_time_ms=24000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3303"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_frontier_tail_repair_needed", decomposition["likely_limiter"]);
        Assert.Equal("-1", decomposition["last_receiver_next_chunk"]);
        Assert.Equal("1", decomposition["terminal_missing_after_progress_timeout"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4TerminalMissingWithRepairBackfill_ClassifiesRepairBeforeZeroReceiveSamples()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_backfill_timeout";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4"),
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; route=regular_nkn_v4_fast; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; chunk_size_bytes=21504; chunk_count=6242; pipeline_depth=8"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=frontier_stall_repair_due; epoch=1589; contiguous_committed_chunk_index=2904; durable_received_highest_chunk_index=5639; credit_until_chunk_index_exclusive=6242; frontier_lag_chunks=2736; missing_range_count=1; frontier_stall_age_ms=847; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1589; previous_epoch=1587; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=2904; durable_received_highest_chunk_index=5639; credit_until_chunk_index_exclusive=6242; effective_credit_until_chunk_index_exclusive=6242; available_credit_bytes=0; missing_range_count=1; bytes_committed=88574976; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; epoch=1589; attempt_count=1; range_count=1; requested_chunk_count=12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; frontier_stall_age_ms=847; repair_interval_ms=750"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; credit_exhausted_time_ms_at_repair=127766"),
            LogLine($"event=filetransfer_v4_repair_filled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; requested_chunk_count=12; attempt_count=1; request_to_fill_ms=731; contiguous_committed_chunk_index=2916; durable_received_highest_chunk_index=5639"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; scheduled_frames=8; normal_scheduled_frames=0; repair_scheduled_frames=8; completed_frames=8; failed_frames=0; raw_bytes_sent=516096; batch_frames_sent=8; chunk_count_sent=24; repair_send_count=24; raw_bytes_sent_total=147507200; normal_raw_bytes_sent_total=134217728; repair_raw_bytes_sent_total=13289472; normal_chunk_count_sent_total=6242; repair_chunk_count_sent_total=618; credit_exhausted_time_ms=128084; available_credit_bytes=0; next_unsent_chunk_index=6242; credit_ceiling_chunk_index=6242; remote_frontier_chunk_index=2904; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=344; frames_enqueued=344; payload_bytes_sent=22333168; payload_bytes_per_second=11166584; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=12; worker_saturation_percent=0; send_p95_ms=8"),
            LogLine("event=screenshare_bridge_transport_health_summary; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=346; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=5000; media_last_received_age_ms=-1; bulk_last_received_age_ms=4000"),
            LogLine("event=screenshare_bridge_transport_health_summary; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=7000; media_last_received_age_ms=-1; bulk_last_received_age_ms=6000")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE", verdict["verdict"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_limited", decomposition["likely_limiter"]);
        Assert.Equal("2", decomposition["transport_ready_sending_zero_receive_window_count"]);
        Assert.Equal("134217728", decomposition["v4_max_sender_pump_normal_raw_bytes_sent_total"]);
        Assert.Equal("1", decomposition["v4_full_normal_payload_sent"]);
        Assert.Equal("2904", decomposition["v4_latest_sender_remote_frontier_chunk_index"]);
        Assert.Equal("2736", decomposition["v4_max_frontier_lag_chunks"]);
        Assert.NotEqual("external_transport_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4TailRepairSentButProgressTimeout_ClassifiesTailRepairNotFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_unfilled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2786; requested_chunk_count=64; frontier_stall_age_ms=47000; credit_until_chunk_index_exclusive=3121; durable_received_highest_chunk_index=2785"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; attempt_count=1; range_count=1; requested_chunk_count=64; first_start_chunk_index=2786; last_end_chunk_exclusive=2850; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=1; repair_scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=0; raw_bytes_sent=1376256; repair_send_count=64; credit_exhausted_time_ms=12000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3303"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_sent_not_observed_by_receiver", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_frontier_tail_repair_due_count"]);
        Assert.Equal("0", repair["v4_frontier_tail_repair_filled_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyDataProtocolStarted_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_legacy_started")
            .Append(LogLine("event=filetransfer_session_opened; direction=inbound; transfer_id=transfer_legacy_started; session_id=sess_a; protocol_version=2; reason=role=Sender"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeBulkSendFailure_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_failure")
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=3; send_failures=1; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=2; in_flight_bytes=98304; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; in_flight_bytes_max=147456; worker_utilization_percent=75"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var bridge = ReadArtifactReport(result.ArtifactDir, "bridge-bulk-summary.txt");
        Assert.Equal("1", bridge["bulk_send_failures"]);
        Assert.Equal("3", bridge["max_bulk_in_flight_summary"]);
        Assert.Equal("4", bridge["max_bulk_effective_concurrency"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveredV6FeedbackAndBridgeFailuresDuringReceiveRecovery_ReturnExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_recovered_receive_recovery";
        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedV4TransferFixture(transferId));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=nkn_bridge_receive_stall_detected; connect_key=test; reason=control_receive_stalled; consecutive_zero_receive_windows=6; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=15; control_messages_received_since_last=0; bulk_messages_received_since_last=3; total_messages_received_since_last=3; control_last_received_age_ms=23845; bulk_last_received_age_ms=63; sample_window_ms=2000", secondsOffset: 20),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; stall_reason=control_receive_stalled; attempt=2; max_restarts=4; consecutive_zero_receive_windows=6; frames_sent_since_last=15; control_last_received_age_ms=23845; bulk_last_received_age_ms=63", secondsOffset: 50),
                LogLine($"event=filetransfer_data_session_availability_observed; session_id=sess_a; transfer_id={transferId}; is_available=0; reason=receive_stall_recovery; requires_resume_request=1; handoff_kind=regular_nkn_recovery; target_transport=regular_nkn", secondsOffset: 50),
                LogLine($"event=filetransfer_v6_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=2; handoff_kind=regular_nkn_recovery; source_transport=regular_nkn; target_transport=regular_nkn; reason=receive_stall_recovery; state=target_proof_pending; starting_committed_chunk=10; starting_highest_observed_chunk=9", secondsOffset: 50),
                LogLine($"event=filetransfer_v6_receiver_state_deferred_for_recovery; transfer_id={transferId}; session_id=sess_a; reason=transport_epoch; transport_epoch=2; recovery_mode=frontier_repair_only; error=InvalidOperationException", secondsOffset: 60),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=InvalidOperationException", secondsOffset: 60),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=InvalidOperationException", secondsOffset: 60),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; frames_enqueued=4; payload_bytes_sent=458; payload_bytes_enqueued=1832; payload_bytes_per_second=229; payload_bytes_enqueued_per_second=916; send_failures=3; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=2; effective_concurrency=2; in_flight_max=1; in_flight_bytes_max=458; worker_utilization_percent=24; worker_idle_slot_samples=50; worker_saturation_percent=0; sample_window_ms=2000", secondsOffset: 70),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=test; recovery_count=1; resume_after_recovery_ms=1755; total_messages_received_since_last=13; total_bytes_received_since_last=6246; control_messages_received_since_last=13; media_messages_received_since_last=0; bulk_messages_received_since_last=0", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external-transport-health-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("2", protocol["v4_feedback_both_failed_count"]);

        var bridge = ReadArtifactReport(result.ArtifactDir, "bridge-bulk-summary.txt");
        Assert.Equal("3", bridge["bulk_send_failures"]);

        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["receive_stall_detected_count"]);
        Assert.Equal("1", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExternalTransportHealthIssue_ReturnsExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(firstTerminalIndex, LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=2; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external-transport-health-summary.txt", verdict["next_artifact"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_TwoSparseExternalWarnings_RemainWarningUnderCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_sparse"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("incident", verdict["warning_cap_count_unit"]);
        Assert.Equal("external_transport_churn:2", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:2", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepeatedBridgeHealthChurnSameConnectKey_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_same_connect_key"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=1; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FourExternalWarnings_ExceedCountCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_count_cap"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=1; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ThreeDenseExternalWarnings_ExceedRateCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_rate_cap"), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 5),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 20)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:3", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepeatedPassiveZeroReceiveHealthSummaries_WarnWithoutCapFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_passive_zero_receive"), terminalOffsetSeconds: 120);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            Enumerable.Range(1, 6).Select(index =>
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: index * 10)));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("(none)", verdict["warning_kind_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ActiveRuntimeZeroReceiveHealthSummaries_DoNotWarnWithoutReceiveStall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_active_runtime_zero_receive",
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 120).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=23; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=10226; media_last_received_age_ms=10300; bulk_last_received_age_ms=10110", secondsOffset: 15),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=21; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=12265; media_last_received_age_ms=12340; bulk_last_received_age_ms=12149", secondsOffset: 17)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_kinds"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("2", external["ready_sending_zero_receive_window_count"]);
        Assert.Equal("0", external["receive_stall_detected_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExternalTransportEventsOutsideObservedTransferWindow_DoNotWarn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_external_outside_window",
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 120).ToList();
        lines.InsertRange(
            0,
            [
                LogLine("event=nkn_bridge_receive_stall_recovery_unproven; connect_key=setup; recovery_count=1; requires_control_proof=1; requires_bulk_proof=1; total_messages_received_since_last=1; total_bytes_received_since_last=544; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=1; control_last_received_age_ms=14250; bulk_last_received_age_ms=1095", secondsOffset: -4),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=3; active_file_transfer_sessions=0; active_file_transfer_runtime_sessions=0; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=12080; media_last_received_age_ms=-1; bulk_last_received_age_ms=12197", secondsOffset: -3)
            ]);
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=teardown; recovery_count=1; resume_after_recovery_ms=2500; total_messages_received_since_last=3; total_bytes_received_since_last=1632; control_messages_received_since_last=2; media_messages_received_since_last=0; bulk_messages_received_since_last=1", secondsOffset: 124));
        lines.Add(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=4; active_file_transfer_sessions=0; active_file_transfer_runtime_sessions=0; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=15634; media_last_received_age_ms=-1; bulk_last_received_age_ms=16342", secondsOffset: 125));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_kinds"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["receive_stall_recovery_unproven_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReadyBridgeSendingButNotReceivingBurst_CountsAsSingleExternalIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_receive_stall",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: 30),
                LogLine("event=nkn_bridge_receive_stall_detected; connect_key=test; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=12; total_messages_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000; sample_window_ms=2000", secondsOffset: 31),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; attempt=1; max_restarts=2; consecutive_zero_receive_windows=3; frames_sent_since_last=12; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: 32),
                LogLine("event=nkn_bridge_inbound_delivery_summary; channel=bulk; messages=4; payload_bytes=4096; subscriber_present_count=4; subscriber_missing_count=0; handler_failure_count=0; source_matches_local_control_count=0; source_matches_local_media_count=0; source_matches_local_bulk_count=0; source_matches_any_local_count=0; topic_count=0; last_source_len=32; last_source_hash=abc123; initial=0", secondsOffset: 33),
                LogLine("event=nkn_inbound_envelope_received; channel=bulk; reason=(none); envelope_type=file_transfer_data_frame; payload_len=1024; envelope_payload_len=768; msg_id=msg1; source_len=32; source_matches_local=0; expected_source_available=1; source_matches_expected=1; is_topic=0", secondsOffset: 34),
                LogLine("event=nkn_bridge_receive_stall_recovery_unproven; connect_key=test; recovery_count=1; requires_control_proof=1; requires_bulk_proof=1; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=4; control_last_received_age_ms=34000; bulk_last_received_age_ms=100", secondsOffset: 35),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=test; recovery_count=1; resume_after_recovery_ms=1750; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=2; media_messages_received_since_last=1; bulk_messages_received_since_last=1", secondsOffset: 36)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);

        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["ready_sending_zero_receive_window_count"]);
        Assert.Equal("1", external["receive_stall_detected_count"]);
        Assert.Equal("1", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["receive_stall_recovery_unproven_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
        Assert.Equal("1750", external["max_receive_stall_recovery_resume_after_ms"]);
        Assert.Equal("4", external["inbound_delivery_bulk_messages"]);
        Assert.Equal("0", external["inbound_delivery_subscriber_missing_count"]);
        Assert.Equal("1", external["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("33000", external["max_bulk_last_received_age_ms"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["transport_ready_sending_zero_receive_window_count"]);
        Assert.Equal("1", decomposition["receive_stall_detected_count"]);
        Assert.Equal("4", decomposition["inbound_delivery_bulk_messages"]);
        Assert.Equal("1", decomposition["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("external_transport_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DistinctReceiveStallEpisodesOverCap_ReturnsExternalChurnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_receive_stall_episodes",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 620);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);

        var churnLines = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            churnLines.Add(LogLine($"event=nkn_bridge_receive_stall_detected; connect_key=test-{i}; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=12; total_messages_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000; sample_window_ms=2000", secondsOffset: 20 + (i * 140)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("external_transport_churn:regular_nkn", verdict["warning_cap_exceeded_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlStaleButBulkFlowing_DoesNotWarnExternalTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_control_degraded")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=900000; total_bytes_received_since_last=900000; control_last_received_age_ms=32000; media_last_received_age_ms=0; bulk_last_received_age_ms=100"))
            .Append(LogLine("event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; frames_sent_since_last=12; control_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100; sample_window_ms=2000"))
            .Append(LogLine("event=nkn_bridge_control_receive_recovery_suppressed; reason=bulk_receive_active; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; recovery_count=0; control_messages_received_since_last=0; bulk_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("0", external["receive_stall_detected_count"]);
        Assert.Equal("0", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["control_receive_degraded_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_bulk_active_count"]);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["control_receive_degraded_count"]);
        Assert.Equal("1", decomposition["control_receive_recovery_suppressed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlStaleButBulkFreshZeroWindow_DoesNotWarnExternalTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_control_fresh")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=6; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=20251; media_last_received_age_ms=0; bulk_last_received_age_ms=2726"))
            .Append(LogLine("event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=7; active_file_transfer_sessions=1; frames_sent_since_last=6; control_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=20251; bulk_last_received_age_ms=2726; sample_window_ms=2000"))
            .Append(LogLine("event=nkn_bridge_control_receive_recovery_suppressed; reason=filetransfer_bulk_receive_fresh; connect_key=test; consecutive_control_zero_receive_windows=7; active_file_transfer_sessions=1; recovery_count=0; control_messages_received_since_last=0; bulk_messages_received_since_last=0; control_last_received_age_ms=20251; bulk_last_received_age_ms=2726"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("0", external["receive_stall_detected_count"]);
        Assert.Equal("0", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["control_receive_degraded_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_bulk_fresh_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ScreenShareMediaDrops_ReturnsCohabitationWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_cohab"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(firstTerminalIndex, LogLine("event=screenshare_bridge_media_send_summary; frames_sent=20; send_failures=0; queue_drops=2; queue_mode=normal; queue_depth=0; oldest_queued_age_ms=0", secondsOffset: 60));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_COHABITATION_PRESSURE", verdict["verdict"]);
        Assert.Equal("coexistence-summary.txt", verdict["next_artifact"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_NoTransferEvidence_ReturnsInvalidSetup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync([
            LogLine("event=session_connected; session_id=sess_a")
        ]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INVALID_SETUP", verdict["verdict"]);
        Assert.Equal("filetransfer-operator-verdict.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_ReadsLiveNknSummaryAndFlagsSafeRegression()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current");
        var safeDir = Path.Combine(tempRoot, "safe");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 2),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100, minimumGoodput: 100, bridgeWaiting: 0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if (-not $result.RegressionFailed) {
    throw 'Expected live NKN safe baseline regression to be detected.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("live-nkn", comparison["current_artifact_kind"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Contains("average goodput regressed", File.ReadAllText(Path.Combine(currentDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_CrossProtocolSafeBaselineIsReportOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-protocol-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current-v4");
        var safeDir = Path.Combine(tempRoot, "safe-v3");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 1.0, v4PayloadFillPercent: 96.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 1000, minimumGoodput: 1000, bridgeWaiting: 0, protocolVersion: "3"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if ($result.RegressionFailed) {
    throw 'Cross-protocol baseline mismatch must be report-only.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("1", comparison["baseline_protocol_mismatch"]);
            Assert.Equal("0", comparison["regression_failed"]);
            Assert.Equal("6", comparison["current_data_protocol_version"]);
            Assert.Equal("3", comparison["safe_data_protocol_version"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_V4SameProtocolSafeBaselineCanGate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-v4-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current-v4");
        var safeDir = Path.Combine(tempRoot, "safe-v4");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 0.40, v4PayloadFillPercent: 40.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100, minimumGoodput: 100, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 1.0, v4PayloadFillPercent: 95.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if (-not $result.RegressionFailed) {
    throw 'Expected same-protocol V4 safe baseline regression to be detected.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("0", comparison["baseline_protocol_mismatch"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Equal("6", comparison["current_data_protocol_version"]);
            Assert.Contains("V6 batch ratio regressed", File.ReadAllText(Path.Combine(currentDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeModeProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-pass");

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("fake-pass"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredRouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("4", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular_nkn_64mb_quick.route"]);
            Assert.Equal("4", summary["regular_nkn_64mb_quick.protocol"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular_nkn_128mb_target.route"]);
            Assert.Equal("4", summary["regular_nkn_128mb_target.protocol"]);
            Assert.Equal("file_tuna_v4", summary["tuna_128mb_no_fault.route"]);
            Assert.Equal("4", summary["tuna_128mb_no_fault.protocol"]);
            Assert.Equal("post_tuna_fallback_v6", summary["tuna_128mb_fallback.route"]);
            Assert.Equal("6", summary["tuna_128mb_fallback.protocol"]);
            Assert.Equal("PASS", summary["tuna_128mb_fallback.operator_verdict"]);
            Assert.Equal("1", summary["tuna_128mb_fallback.attempt_count"]);
            Assert.Equal("0", summary["tuna_128mb_fallback.retry_used"]);
            Assert.Equal("1", summary["tuna_128mb_fallback.selected_attempt"]);

            var regularQuick = ReadArtifactReport(Path.Combine(runRoot, "regular-nkn-64mb-quick"), "filetransfer-live-nkn-summary.txt");
            Assert.Equal("4", regularQuick["data_protocol_version"]);
            Assert.Equal("0", regularQuick["bridge_bulk_send_failure_count"]);
            var regularRoute = ReadArtifactReport(Path.Combine(runRoot, "regular-nkn-64mb-quick"), "filetransfer-route-consistency-summary.txt");
            Assert.Equal("pass", regularRoute["route_consistency_verdict"]);
            Assert.Equal("regular_nkn_v4_fast", regularRoute["selected_routes"]);

            var tunaNoFault = File.ReadAllText(Path.Combine(runRoot, "tuna-128mb-no-fault", "filetransfer-tuna-gui-summary.json"));
            Assert.Contains("\"goodputBytesPerSecond\"", tunaNoFault, StringComparison.Ordinal);
            Assert.Contains("\"measuredPhase\"", tunaNoFault, StringComparison.Ordinal);
            Assert.Contains("\"measured_file_tuna_v4\"", tunaNoFault, StringComparison.Ordinal);

            var tunaFallback = File.ReadAllText(Path.Combine(runRoot, "tuna-128mb-fallback", "filetransfer-tuna-gui-summary.json"));
            Assert.Contains("\"setupPhase\"", tunaFallback, StringComparison.Ordinal);
            Assert.Contains("\"setup_file_tuna_v4\"", tunaFallback, StringComparison.Ordinal);
            Assert.Contains("\"measuredPhase\"", tunaFallback, StringComparison.Ordinal);
            Assert.Contains("\"measured_post_tuna_fallback_v6\"", tunaFallback, StringComparison.Ordinal);
            Assert.Contains("\"controlledRestartAnalysis\"", tunaFallback, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "filetransfer-retained-log-slice-full.log")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "filetransfer-setup-retained-log-slice.log")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "filetransfer-measured-fallback-retained-log-slice.log")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-1")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-1", "filetransfer-tuna-gui-summary.json")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "route-acceptance-attempts.json")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4FakeModeProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-pass");

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase4-pass"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake Phase 4 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase4RouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.Equal("0", summary["correctness_failure_count"]);
            Assert.Equal("0", summary["performance_failure_count"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-nkn-v4-64mb.final_route"]);
            Assert.Equal("4", summary["regular-nkn-v4-64mb.protocol"]);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("4", summary["active-tuna-v4-64mb.protocol"]);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helpee-64mb.final_route"]);
            Assert.Equal("6", summary["live-switch-off-helpee-64mb.protocol"]);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helper-64mb.final_route"]);
            Assert.Equal("6", summary["live-switch-off-helper-64mb.protocol"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["live-multi-toggle-off-on-off-64mb.selected_route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["live-multi-toggle-off-on-off-64mb.live_route_epoch_route_changes"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.selected_route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_route_changes"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.selected_route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.live_route_epoch_route_changes"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.Equal("none", summary["second-transfer-after-reactivation.acceptance_failure_class"]);

            var secondSummaryJson = File.ReadAllText(Path.Combine(runRoot, "second-transfer-after-reactivation", "filetransfer-tuna-gui-summary.json"));
            Assert.Contains("\"secondTransfer\"", secondSummaryJson, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "filetransfer-second-transfer-retained-log-slice.log")));
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "filetransfer-route-consistency-summary.txt")));
            Assert.True(File.Exists(Path.Combine(runRoot, "phase4-network-variance-note.md")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalReceiveRecoveryExhaustionRerunsAsEnvironmentalVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-exhaustion-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-exhaustion-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected canonical receive-recovery exhaustion to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalReceiveRecoveryLivenessTimeoutRerunsAsEnvironmentalVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-liveness-timeout-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-liveness-timeout-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_LIVENESS_TIMEOUT_BEFORE_RUNTIME_UNLOCK"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected canonical receive-recovery liveness timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PersistentCanonicalReceiveRecoveryExhaustionFailsAsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-exhaustion-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-exhaustion-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Equal("environmental", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.environmental_classification"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.measurement_contaminated"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5TransientSetupFailureRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-transient-setup-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-transient-setup-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_PHASE"] = "measured_terminal";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_REASON"] = "terminal_before_accept";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_HARD_FAILURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected transient setup failure to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("2", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.Contains("terminal_before_accept", summary["live-switch-off-helpee-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "live-switch-off-helpee-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5ListenerReadinessContradictionRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-listener-readiness-contradiction-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-listener-readiness-contradiction-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_REASON"] = "listener_ready_unavailable_contradiction";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected listener readiness contradiction to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("2", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_reason"));
            Assert.Contains("listener_ready_unavailable_contradiction", summary["live-switch-off-helpee-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "live-switch-off-helpee-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularV4ReceiveRecoveryUnprovenRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-receive-recovery-unproven-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-receive-recovery-unproven-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_receive_recovery_unproven";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-V4 receive-recovery defer to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Contains("regular_v4_receive_recovery_unproven", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularStartupPeerDisconnectRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-startup-peer-disconnect-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-startup-peer-disconnect-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_PEER_DISCONNECT"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-NKN startup peer disconnect to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_startup_local_only_no_ack", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularPreTransferApprovalExpiryRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-pretransfer-expiry-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-pretransfer-expiry-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PRETRANSFER_SETUP_EXPIRED"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-NKN pre-transfer approval expiry to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("regular-nkn-v4-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-nkn-v4-64mb.setup_failure_reason"));
            Assert.Equal("none", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalTerminalEvidenceTimeoutRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-terminal-evidence-timeout-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-terminal-evidence-timeout-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "unknown";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "Timed out waiting for live file-transfer terminal evidence.";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected canonical terminal-evidence timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live file-transfer terminal evidence", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PersistentTransientSetupFailureFailsAsSetup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-transient-setup-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-transient-setup-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "activation_offer_not_observed";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_TRANSIENT_SETUP_REASON"] = "activation_offer_not_observed";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Equal("setup", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Contains("activation_offer_not_observed", summary["regular-v4-live-activation-off-on-off-256mb.rerun_failure_reason"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PerformanceFirstAttemptKeepsClassWhenRerunSetupFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-performance-first-rerun-setup-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-performance-first-rerun-setup-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_PHASE"] = "activation_offer_answer";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_REASON"] = "activation_offer_sent_waiting_answer";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected Phase 5 performance variance to be reported without failing release correctness.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.Contains("activation_offer_sent_waiting_answer", summary["live-switch-off-helpee-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.DoesNotContain("scenario rerun setup failed; preserving first-attempt evidence", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PerformanceFirstAttemptKeepsEvidenceWhenRerunHasWarningPolicyFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-performance-first-rerun-warning-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-performance-first-rerun-warning-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_RERUN_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected Phase 5 to preserve performance-only first-attempt evidence when the goodput rerun introduces warning-policy noise.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.retry_used"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["live-switch-off-helper-64mb.acceptance_failure_class"]);
            Assert.Contains("goodput regression exceeded", summary["live-switch-off-helper-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("rerun introduced warning_policy failure", summary["live-switch-off-helper-64mb.rerun_failure_reason"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SecondTransferUsesSplitProofWhenCombinedSliceIsNoisy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-second-transfer-combined-slice-noisy");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-second-transfer-combined-slice-noisy");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_COMBINED_ROUTE_ANALYSIS_CONTAMINATED"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected split second-transfer proof to override noisy combined retained analysis.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.failure_count"]);
            Assert.Equal("none", summary["second-transfer-after-reactivation.acceptance_failure_class"]);
            Assert.Equal("pass", summary["second-transfer-after-reactivation.route_consistency_verdict"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.route_sequence"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "filetransfer-route-consistency-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pass");

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-pass"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake Phase 5 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase5RouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("phase5", summary["acceptance_phase"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("6", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("none", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Equal("none", summary["active-tuna-v4-64mb.bridge_liveness_integration_verdict"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.live_epoch_route_changes"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-network-variance-note.md")));
            Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightBridgeBootstrapFailureFailsBeforeMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "nkn_bridge_bootstrap_not_ready";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), $"Expected Phase 5 preflight failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "Expected Phase 5 preflight JSON summary.");
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready", "phase5-bridge-readiness-preflight-summary.txt")), "Expected bridge preflight artifact summary.");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["preflight_verdict"]);
            Assert.Equal("preflight_bridge_bootstrap", summary["preflight_failure_class"]);
            Assert.Equal("nkn_bridge_bootstrap_not_ready", summary["preflight_failure_reason"]);
            Assert.Equal("0", summary["run_count"]);
            Assert.Equal("1", summary["failure_count"]);
            Assert.Contains("phase5-preflight-bridge-ready: nkn_bridge_bootstrap_not_ready", summaryText, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb")), "Preflight failure should not consume the Phase 5 scenario matrix.");
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Preflight failure should not create canonical stress artifacts.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
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
            Assert.Equal("none", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
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
            Assert.Equal("none", summary["live-switch-off-helper-64mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.bridge_liveness_integration_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_BRIDGE_LIVENESS_FAIL", "1", "bridge_liveness", "bridge liveness integration verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_AUTHORITY_METADATA_MISSING", "1", "fallback_authority", "fallback leg authority proof verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSPORT_ONLY_LIVE_PROOF", "1", "live_route_proof", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_METADATA", "1", "live_route_proof", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_ROUTE", "file_tuna_v6", "route_runtime", "active file_tuna_v6")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SHA_FAIL", "1", "protocol_or_integrity", "completion/integrity")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_HARD_FAILURE", "1", "protocol_or_integrity", "operator hard failures observed")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_WARNING_CAP_EXCESS", "1", "warning_policy", "warning cap exceeded")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_ROUTE", "post_tuna_fallback_v6", "route_runtime", "second transfer route mismatch after reactivation")]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeFailsStrictAcceptance(
        string environmentName,
        string environmentValue,
        string expectedFailureClass,
        string expectedFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fail");
            environment[environmentName] = environmentValue;

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), $"Expected Phase 5 failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Contains(expectedFailure, summaryText, StringComparison.Ordinal);
            var scenarioName = environmentName.Contains("REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB", StringComparison.Ordinal)
                ? "regular-v4-live-activation-off-on-off-256mb"
                : environmentName.Contains("LIVE_SWITCH_OFF_HELPEE_64MB", StringComparison.Ordinal)
                    ? "live-switch-off-helpee-64mb"
                    : environmentName.Contains("SECOND_TRANSFER_AFTER_REACTIVATION", StringComparison.Ordinal)
                        ? "second-transfer-after-reactivation"
                        : environmentName.Contains("REGULAR_NKN_V4_64MB", StringComparison.Ordinal)
                            ? "regular-nkn-v4-64mb"
                            : "active-tuna-v4-64mb";
            Assert.Equal(expectedFailureClass, summary[$"{scenarioName}.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_ROUTE", "file_tuna_v6", "active file_tuna_v6")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_HARD_FAILURE", "1", "operator hard failures observed")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SHA_FAIL", "1", "completion/integrity")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_PROOF", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSPORT_ONLY_LIVE_PROOF", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_METADATA", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_ROUTE", "post_tuna_fallback_v6", "second transfer route mismatch after reactivation")]
    public async Task RunFileTransferRouteAcceptance_Phase4FakeModeFailsStrictAcceptance(
        string environmentName,
        string environmentValue,
        string expectedFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-fail");
            environment[environmentName] = environmentValue;

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), $"Expected Phase 4 failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains(expectedFailure, summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4ScenarioExecutionFailureDoesNotAbortMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-execution-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-execution-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("measured_accept_wait", summary["active-tuna-v4-64mb.setup_failure_phase"]);
            Assert.Equal("offer_sent_accept_not_enabled", summary["active-tuna-v4-64mb.setup_failure_reason"]);
            Assert.Contains("scenario execution failed", File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), StringComparison.Ordinal);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helpee-64mb.final_route"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4GoodputRerunExecutionFailureIsScenarioFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-rerun-execution-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-rerun-execution-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.False(summary.ContainsKey("active-tuna-v4-64mb.setup_failure_phase"));
            Assert.Contains("goodput regression exceeded", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4GoodputOnlyRerunCanPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-goodput-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-goodput-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_GOODPUT_BPS"] = "1872542.416";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected goodput-only Phase 4 rerun to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("goodput regression exceeded", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RerunSetupFailurePreservesFirstAttemptEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-rerun-setup-failure-preserves-first");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-rerun-setup-failure-preserves-first");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.EndsWith(
                Path.Combine("phase4-rerun-setup-failure-preserves-first", "active-tuna-v4-64mb"),
                summary["active-tuna-v4-64mb.artifact_dir"],
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("4", summary["active-tuna-v4-64mb.protocol"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Contains("goodput regression exceeded", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("active-tuna-v4-64mb-rerun-1", summary["active-tuna-v4-64mb.rerun_artifact_dir"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularExternalTransportChurnRerunsAsNetworkVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-network-variance-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-network-variance-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected capped regular-NKN external churn to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("live_transport_paired_rerun", summary["network_variance_policy"]);
            Assert.Equal("capped_external_transport_churn_requires_clean_rerun", summary["regular_nkn_external_transport_warning_policy"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_nkn_external_transport_churn", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("(none)", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularProgressTimeoutRecoveryStormRerunsAsNetworkVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-progress-timeout-recovery-storm-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-progress-timeout-recovery-storm-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-NKN progress-timeout recovery storm to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("(none)", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentRegularProgressTimeoutRecoveryStormFailsAsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-progress-timeout-recovery-storm-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-progress-timeout-recovery-storm-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularStartupRecoveryStormRerunsAsNetworkVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-NKN startup recovery storm to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("(none)", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularStartupRecoveryStormExecutionFailureStillReruns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-execution-failure-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-execution-failure-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_POST_ARTIFACT_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected classified regular-NKN startup recovery storm with a post-artifact wrapper failure to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.DoesNotContain("regular-nkn-v4-64mb: scenario execution failed", summaryText, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentRegularStartupRecoveryStormFailsAsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_STARTUP_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentRegularExternalTransportChurnFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-network-variance-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-network-variance-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_WARNING"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("regular_v4_external_transport_churn", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Contains("regular_nkn_external_transport_churn", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4ContaminatedActiveTunaRerunCanPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-contaminated-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-contaminated-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_GOODPUT_BPS"] = "5442738.550";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected contaminated active-Tuna measurement to rerun cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("0", summary["active-tuna-v4-64mb.measurement_contaminated"]);
            Assert.Contains("measurement contaminated", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_repair_pressure", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "active-tuna-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentActiveTunaContaminationFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-contaminated-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-contaminated-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "5442738.550";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_GOODPUT_BPS"] = "5442738.550";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.measurement_contaminated"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.Equal("performance", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Contains("measurement contaminated", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_bridge_receive_recovery_window", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4SecondTransferUsesSecondSliceForContamination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-second-transfer-second-slice-clean");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-second-transfer-second-slice-clean");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_CONTAMINATED_MEASUREMENT"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected first-transfer contamination to stay out of second-transfer acceptance.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.measurement_contaminated"]);
            Assert.Equal("(none)", summary["second-transfer-after-reactivation.measurement_contamination_reasons"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.failure_count"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "repair-reorder-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4SecondTransferContaminationStillFailsWhenSecondSliceDirty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-second-transfer-second-slice-dirty");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-second-transfer-second-slice-dirty");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_CONTAMINATED_MEASUREMENT"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["second-transfer-after-reactivation.measurement_contaminated"]);
            Assert.Contains("measurement contaminated", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_repair_pressure", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_bridge_receive_recovery_window", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentGoodputRegressionFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-goodput-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-goodput-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_GOODPUT_BPS"] = "1000";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains("goodput regression exceeded", summaryText, StringComparison.Ordinal);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("0", summary["correctness_failure_count"]);
            Assert.Equal("1", summary["performance_failure_count"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            var varianceNote = File.ReadAllText(Path.Combine(runRoot, "phase4-network-variance-note.md"));
            Assert.Contains("correctness_verdict=PASS", varianceNote, StringComparison.Ordinal);
            Assert.Contains("performance_verdict=FAIL", varianceNote, StringComparison.Ordinal);
            Assert.Contains("goodput regression exceeded", varianceNote, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_ROUTE", "file_tuna_v4", "selected route mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_PROTOCOL", "6", "route consistency verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_MISSING_ROUTE_SUMMARY", "1", "missing artifact: filetransfer-route-consistency-summary.txt")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_BRIDGE_FAILURES", "1", "bridge_bulk_send_failure_count must be 0")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ZOMBIE_TERMINAL", "1", "zombie terminal state observed")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_FALLBACK", "1", "Tuna no-fault acceptance unexpectedly entered fallback")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_ROUTE", "post_tuna_fallback_v6", "selected route mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_ROUTE", "file_tuna_v4", "selected route mismatch")]
    public async Task RunFileTransferRouteAcceptance_FakeModeFailsRouteGateMismatches(
        string environmentName,
        string environmentValue,
        string expectedFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-fail");
            environment[environmentName] = environmentValue;

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-summary.txt")), $"Expected route acceptance failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "route-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains(expectedFailure, summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeTunaGoodputBelowFloorStillPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-tuna-low-goodput");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-tuna-low-goodput");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_GOODPUT_BPS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected low active-Tuna goodput to remain informational.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("file_tuna_v4", summary["tuna_128mb_no_fault.route"]);
            Assert.Equal("1.000", summary["tuna_128mb_no_fault.goodput_bytes_per_second"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeRegularGoodputBelowFloorStillPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-regular-low-goodput");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-regular-low-goodput");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_128MB_GOODPUT_BPS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected low regular-NKN goodput to remain informational.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1.000", summary["regular_nkn_64mb_quick.goodput_bytes_per_second"]);
            Assert.Equal("1.000", summary["regular_nkn_128mb_target.goodput_bytes_per_second"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeFallbackGoodputBelowTunaFloorStillPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-fallback-low-goodput");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-fallback-low-goodput");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_GOODPUT_BPS"] = "5000000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_GOODPUT_BPS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected low fallback goodput to remain informational.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary["tuna_128mb_fallback.route"]);
            Assert.Equal("1.000", summary["tuna_128mb_fallback.goodput_bytes_per_second"]);
            var tunaSummaryJson = File.ReadAllText(Path.Combine(runRoot, "tuna-128mb-fallback", "filetransfer-tuna-gui-summary.json"));
            Assert.Contains("\"setupRawOperatorVerdict\"", tunaSummaryJson, StringComparison.Ordinal);
            Assert.Contains("\"INVALID_SETUP\"", tunaSummaryJson, StringComparison.Ordinal);
            Assert.Contains("\"setupControlledCancelAccepted\"", tunaSummaryJson, StringComparison.Ordinal);
            Assert.Contains("true", tunaSummaryJson, StringComparison.Ordinal);
            Assert.Contains("\"setupNormalizedVerdict\"", tunaSummaryJson, StringComparison.Ordinal);
            Assert.Contains("\"expected_controlled_setup_cancel\"", tunaSummaryJson, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeFallbackRetriesPreMeasuredTimeoutThenPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-fallback-retry");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-fallback-retry");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ATTEMPT1"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected retryable fallback fake to pass on attempt 2.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("2", summary["tuna_128mb_fallback.attempt_count"]);
            Assert.Equal("1", summary["tuna_128mb_fallback.retry_used"]);
            Assert.Equal("2", summary["tuna_128mb_fallback.selected_attempt"]);
            Assert.Contains("progress_timeout_before_measured_fallback_route", summary["tuna_128mb_fallback.first_failure_reason"], StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-1")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-2")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-1", "filetransfer-tuna-gui-error.json")));
            Assert.True(File.Exists(Path.Combine(runRoot, "tuna-128mb-fallback", "attempt-2", "filetransfer-tuna-gui-summary.json")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeFallbackRetriesRouteNotActiveThenPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-fallback-route-not-active-retry");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-fallback-route-not-active-retry");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_ROUTE_NOT_READY_ATTEMPT1"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected route-not-active fallback fake to pass on attempt 2.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("2", summary["tuna_128mb_fallback.attempt_count"]);
            Assert.Equal("1", summary["tuna_128mb_fallback.retry_used"]);
            Assert.Equal("2", summary["tuna_128mb_fallback.selected_attempt"]);
            Assert.Equal("post_tuna_fallback_v6", summary["tuna_128mb_fallback.route"]);
            var attemptsJson = File.ReadAllText(Path.Combine(runRoot, "tuna-128mb-fallback", "route-acceptance-attempts.json"));
            Assert.Contains("measured_fallback_offer_route_not_active", attemptsJson, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_FakeFallbackExhaustedRetriesFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-fallback-exhausted");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-fallback-exhausted");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ALWAYS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "route-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains("fallback attempts exhausted before successful measured transfer", summaryText, StringComparison.Ordinal);
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("2", summary["tuna_128mb_fallback.attempt_count"]);
            Assert.Equal("1", summary["tuna_128mb_fallback.retry_used"]);
            Assert.Equal("0", summary["tuna_128mb_fallback.selected_attempt"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_EXTERNAL_WARNING", "tuna_128mb_no_fault", "external_transport_churn")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RECOVERED_BRIDGE_WARNING", "tuna_128mb_fallback", "recovered_post_tuna_fallback_bridge_clear")]
    public async Task RunFileTransferRouteAcceptance_FakeModeAcceptsAllowlistedOperatorWarnings(
        string environmentName,
        string runName,
        string expectedWarningKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "fake-warning");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("fake-warning");
            environment[environmentName] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected allowlisted operator warning to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "route-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("WARN_EXTERNAL_TRANSPORT", summary[$"{runName}.operator_verdict"]);
            Assert.Equal("1", summary[$"{runName}.operator_accepted_with_warnings"]);
            Assert.Equal(expectedWarningKind, summary[$"{runName}.warning_kinds"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeNknFastProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake live NKN fast soak to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("PASS", verdict["verdict"]);

            var summary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("live-nkn", summary["artifact_kind"]);
            Assert.Equal("nkn-fast", summary["mode"]);
            Assert.Equal("1", summary["cycles_completed"]);
            Assert.Equal("4", summary["data_protocol_version"]);
            Assert.Equal("v4_default_21k", summary["payload_efficiency_profile"]);
            Assert.Equal("expected", summary["bridge_config_status"]);
            Assert.Equal("4/8/4", summary["bridge_observed_topology"]);
            Assert.Equal("4", summary["bridge_observed_bulk_send_concurrency"]);
            Assert.Equal("fanout", summary["bridge_observed_bulk_send_mode"]);
            Assert.Equal("1.000000", summary["v4_batch_ratio"]);
            Assert.Equal("1", summary["v4_feedback_redundant_success_count"]);
            Assert.Equal("0", summary["payload_rejected_count"]);
            Assert.Equal("0", summary["bridge_bulk_send_failure_count"]);

            var protocolShape = File.ReadAllText(Path.Combine(artifactDir, "protocol-shape-summary.txt"));
            Assert.Contains("filetransfer.chunk_batch.v4", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_sender_started_count=1", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v6_sender_started_count=0", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_feedback_redundant_success_count=1", protocolShape, StringComparison.Ordinal);

            var baseline = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("4", baseline["current_data_protocol_version"]);
            Assert.Equal("1.000000", baseline["current_v4_batch_ratio"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1", promotion["current_long_proof_completed_cycle_count"]);
            Assert.Equal("0", promotion["long_proof_matrix_complete"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeV4LongProofAndBaselineRerunHoldsRegularV4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-v4-promotion-fake", Guid.NewGuid().ToString("N"));
        var safeDir = Path.Combine(tempRoot, "nkn-fast-safe");
        var rerunDir = Path.Combine(tempRoot, "nkn-fast-rerun");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var longProof = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", safeDir,
                    "-PayloadSizes", "16MiB,64MiB",
                    "-Cycles", "2",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                longProof.ExitCode == 0,
                $"Expected fake V4 long proof to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{longProof.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{longProof.Stderr}");
            AssertRequiredLiveArtifacts(safeDir);

            var longDecision = ReadArtifactReport(safeDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", longDecision["decision"]);
            Assert.Equal("non_v6_protocol", longDecision["reason"]);
            Assert.Equal("4", longDecision["data_protocol_version"]);
            Assert.Equal("1500000.000", longDecision["target_goodput_bytes_per_second"]);
            Assert.Equal("1", longDecision["current_long_proof_matrix_complete"]);
            Assert.Equal("2", longDecision["current_long_proof_16m_completed_count"]);
            Assert.Equal("2", longDecision["current_long_proof_64m_completed_count"]);
            Assert.Equal("1", longDecision["goodput_target_met"]);

            var rerun = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", rerunDir,
                    "-PayloadSizes", "64MiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30",
                    "-SafeBaselineArtifactDir", safeDir,
                    "-FailOnGate"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                rerun.ExitCode == 0,
                $"Expected fake V4 baseline rerun to complete cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{rerun.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{rerun.Stderr}");
            AssertRequiredLiveArtifacts(rerunDir);

            var promotion = ReadArtifactReport(rerunDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("hold", promotion["promotion_status"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1", promotion["safe_long_proof_matrix_complete"]);
            Assert.Equal("0", promotion["same_protocol_v6_baseline_pass"]);
            Assert.Equal("0", promotion["baseline_protocol_mismatch"]);
            Assert.Equal("0", promotion["baseline_regression_failed"]);
            Assert.Equal("4", promotion["safe_data_protocol_version"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeV4LongProofBelowTargetHoldsRegularV4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-v4-iterate-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast-slow");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "16MiB,64MiB",
                    "-Cycles", "2",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment(fakeGoodputBytesPerSecond: 1_400_000));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake V4 below-target proof to complete cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var decomposition = ReadArtifactReport(artifactDir, "throughput-decomposition-summary.txt");
            Assert.Equal("v4_sender_pump_underfed", decomposition["likely_limiter"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("hold", promotion["promotion_status"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("fix_harness_or_analyzer_evidence", promotion["next_focus"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1500000.000", promotion["target_goodput_bytes_per_second"]);
            Assert.Equal("1", promotion["long_proof_matrix_complete"]);
            Assert.Equal("0", promotion["goodput_target_met"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeProgressTimeoutWritesStableFailureFields()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-fake-timeout", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast-timeout");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var environment = BuildFakeLiveNknEnvironment();
            environment["NLINK_FILETRANSFER_NKN_SOAK_FAKE_PROGRESS_TIMEOUT"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64MiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-FailOnGate"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            AssertRequiredLiveArtifacts(artifactDir);

            var liveSummary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", liveSummary["verdict"]);
            Assert.Equal("0", liveSummary["cycles_observed"]);
            Assert.Equal("1", liveSummary["gui_progress_timeout_count"]);
            Assert.Equal("-1", liveSummary["last_receiver_next_chunk"]);
            Assert.Equal("-1", liveSummary["last_receiver_highest_chunk"]);
            Assert.Equal("4729", liveSummary["last_progress_event_count"]);
            Assert.Equal("1", liveSummary["terminal_missing_after_progress_timeout"]);

            var decomposition = ReadArtifactReport(artifactDir, "throughput-decomposition-summary.txt");
            Assert.Equal("sparse_frontier_gap_repair_stalled", decomposition["likely_limiter"]);
            Assert.Equal("1", decomposition["progress_timeout_with_receiver_gap_stall"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("progress_timeout_incomplete_long_proof", promotion["reason"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOps_NknMixedFakeRunnerProducesCleanMediaEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-mixed-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-mixed");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunFileTransferOpsAsync(
                repoRoot,
                [
                    "-Mode", "NknMixed",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                result.ExitCode == 0,
                $"Expected public NknMixed fake runner to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("PASS", verdict["verdict"]);

            var summary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("nkn-mixed", summary["mode"]);
            Assert.Equal("1", summary["cycles_completed"]);
            Assert.Equal("0", summary["media_queue_drop_count"]);
            Assert.Equal("0", summary["media_send_failure_count"]);
            Assert.Equal("0", summary["media_queue_severe_count"]);

            var retainedSlice = File.ReadAllText(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log"));
            Assert.Contains("screenshare_bridge_media_send_summary", retainedSlice, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeSafeBaselineRegressionFailsWhenGated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-regression-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "current");
        var safeDir = Path.Combine(tempRoot, "safe");
        Directory.CreateDirectory(artifactDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100_000, minimumGoodput: 100_000, bridgeWaiting: 0, protocolVersion: "4", v4BatchRatio: 1.0, v4PayloadFillPercent: 95.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-SafeBaselineArtifactDir", safeDir,
                    "-FailOnGate"
                ],
                BuildFakeLiveNknEnvironment(fakeGoodputBytesPerSecond: 1_000));

            Assert.Equal(1, result.ExitCode);
            AssertRequiredLiveArtifacts(artifactDir);

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("FAIL_REGRESSION_BUDGET", verdict["verdict"]);
            Assert.Equal("baseline-comparison.txt", verdict["next_artifact"]);

            var comparison = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("live-nkn", comparison["current_artifact_kind"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Contains("average goodput regressed", File.ReadAllText(Path.Combine(artifactDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string[] BuildRouteAwareCompletedFixture(
        string transferId,
        string route,
        int protocolVersion,
        string runtimeProfile,
        string bridgeRecoveryPolicy,
        string runtimeEventName,
        int? runtimeProtocolVersion = null,
        string? diagnosticMarkerOverride = null)
    {
        var effectiveRuntimeProtocolVersion = runtimeProtocolVersion ?? protocolVersion;
        var frameFamily = protocolVersion == 6 ? "v6" : "v4";
        var diagnosticMarker = diagnosticMarkerOverride ?? (route == "diagnostic_regular_nkn_v6" ? "1" : "0");
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; handoff_kind=none; bridge_recovery_policy={bridgeRecoveryPolicy}; liveness_terminal_policy={route}; selection_reason=test_route; file_tuna_active={(route == "file_tuna_v4" ? 1 : 0)}; post_tuna_fallback_active={(route == "post_tuna_fallback_v6" ? 1 : 0)}; diagnostic_regular_nkn_v6={diagnosticMarker}; transport_profile=default"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; handoff_kind=none; bridge_recovery_policy={bridgeRecoveryPolicy}; liveness_terminal_policy={route}; selection_reason=test_route; file_tuna_active={(route == "file_tuna_v4" ? 1 : 0)}; post_tuna_fallback_active={(route == "post_tuna_fallback_v6" ? 1 : 0)}; diagnostic_regular_nkn_v6={diagnosticMarker}; transport_profile=default"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}; selection_reason=test_route"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}; reason=role=Sender"),
            LogLine($"event={runtimeEventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={effectiveRuntimeProtocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={effectiveRuntimeProtocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    private static string[] BuildRouteAwareControlledRestartFixture(bool includeSetupTerminal)
    {
        const string transferId = "[redacted]";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_v4_receiver_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict")
        };

        if (includeSetupTerminal)
        {
            lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Canceled; error_code=canceled_remote; saved_path=(none)"));
            lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Canceled; error_code=canceled_local"));
        }

        lines.AddRange(BuildRouteAwareMeasuredFallbackFixture());
        return lines.ToArray();
    }

    private static string[] BuildRouteAwareLiveReenableFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    private static string[] BuildRouteAwareLiveSwitchOffFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    private static string[] BuildRouteAwareLiveMultiCycleFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=first_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=8-15; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=second_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    private static string[] BuildRouteAwareLiveRegularActivationCycleFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; reason=role=Sender"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=regular_nkn_v4_fast; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_unlocked_during_regular_transfer"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=first_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=8-15; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=second_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    private static string[] BuildRouteAwareMeasuredFallbackFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6; selection_reason=post_tuna_file_fallback; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6; selection_reason=post_tuna_file_fallback; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_file_fallback"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_v6_sender_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_v6_receiver_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-31; payload_bytes=67108864; serialized_payload_bytes=67108864; raw_chunk_bytes=67108864; chunk_count=32"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-31; raw_chunk_bytes=67108864; chunk_count=32"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    private static string[] BuildRuntimeUnlockRecoveryCoordinationFailureFixture()
    {
        const string transferId = "transfer_runtime_unlock_coordination";
        const string sessionId = "sess_runtime_unlock_coordination";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 2),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; reason=role=Sender", secondsOffset: 3),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 4),
            LogLine($"event=filetransfer_v4_receiver_started; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8", secondsOffset: 10),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8", secondsOffset: 11),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_activation_control_send_recovery_requested; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout", secondsOffset: 24),
            LogLine("event=nkn_bridge_receive_stall_recovery_requested; reason=core_filetransfer_request; requested_reason=tuna_activation_offer_send_timeout; stall_reason=regular_v4_unproven_recovery_escalation; regular_v4_runtime_unlock_unproven_escalation=1; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 26),
            LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=abc123; recovery_count=1; resume_after_recovery_ms=1750; total_messages_received_since_last=1", secondsOffset: 42),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 93)
        ];
    }

    private static string[] BuildRuntimeUnlockRecoveryContractDispatchedFixture()
    {
        const string transferId = "transfer_runtime_unlock_contract_dispatched";
        const string sessionId = "sess_runtime_unlock_contract_dispatched";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_authority_granted; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryqueued; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=(none)", secondsOffset: 46),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1", secondsOffset: 48),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=bulk_queue_fallback; authority_failure_reason=(none)", secondsOffset: 51),
            LogLine($"event=session_recovery_contract_retry_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=1", secondsOffset: 52),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 90),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 91)
        ];
    }

    private static string[] BuildRuntimeUnlockRetryDispatchedButOfferObservationBlockedFixture()
    {
        const string transferId = "transfer_runtime_unlock_offer_observation_blocked";
        const string sessionId = "sess_runtime_unlock_offer_observation_blocked";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1", secondsOffset: 48),
            LogLine($"event=tuna_activation_control_send_waiting_for_regular_v4_recovery; session_id={sessionId}; purpose=offer; blocker_reason=receive_stall_recovery_in_progress; blocker_remaining_ms=0; regular_v4_pressure_reason=receive_stall_recovery_in_progress; regular_v4_pressure_remaining_ms=0; recovery_age_ms=5276; wait_budget_ms=5000; reason=runtime_unlock_regular_v4_receive_stall", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose=offer; message_type=transport_acceleration_offer; reason=runtime_unlock_active_filetransfer_requires_direct_observed_send; blocker_reason=regular_v4_receive_stall_bypass", secondsOffset: 50),
            LogLine($"event=tuna_acceleration_offer_rejected; reason=runtime_unlock; session_id={sessionId}; lanes=file; payer_decision_id=8; generation=4; observed_lane=(none); queue_local_only=0; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout", secondsOffset: 51),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    private static string[] BuildRuntimeUnlockDispatchDeferredByRegularV4ReceiveRecoveryFixture()
    {
        const string transferId = "transfer_runtime_unlock_dispatch_deferred_regular_v4";
        const string sessionId = "sess_runtime_unlock_dispatch_deferred_regular_v4";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=0", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0", secondsOffset: 48),
            LogLine($"event=session_recovery_contract_retry_queued; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryqueued; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=regular_v4_receive_recovery_pending", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; retry_scheduled=1; recovery_requested=0", secondsOffset: 50),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    private static string[] BuildRuntimeUnlockListenerRearmFailureFixture()
    {
        const string transferId = "transfer_runtime_unlock_listener_rearm_failed";
        const string sessionId = "sess_runtime_unlock_listener_rearm_failed";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 4),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=session_recovery_contract_listener_rearm_required; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_listener_rearm_failed; listener_ready=1; listener_unavailable=1", secondsOffset: 44),
            LogLine($"event=session_recovery_contract_listener_rearm_failed; session_id={sessionId}; reason=runtime_unlock_listener_rearm_failed; trigger=runtime_unlock", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=runtime_unlock_listener_rearm_failed", secondsOffset: 46),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    private static string[] BuildRuntimeUnlockListenerRearmCompletedFixture()
    {
        const string transferId = "transfer_runtime_unlock_listener_rearm_completed";
        const string sessionId = "sess_runtime_unlock_listener_rearm_completed";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=session_recovery_contract_listener_rearm_required; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_listener_rearm_failed; listener_ready=0; listener_unavailable=1", secondsOffset: 44),
            LogLine($"event=session_recovery_contract_listener_rearm_completed; session_id={sessionId}; trigger=runtime_unlock", secondsOffset: 45),
            LogLine($"event=runtime_unlock_offer_dispatched_after_listener_rearm; session_id={sessionId}; payer_decision_id=8; generation=4; trigger=runtime_unlock", secondsOffset: 46),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=(none)", secondsOffset: 47),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=control_priority; authority_failure_reason=(none)", secondsOffset: 50),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 90),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 91)
        ];
    }

    private static string[] BuildRuntimeUnlockPeerResponseMissingUnderRegularV4RecoveryFixture()
    {
        const string transferId = "transfer_runtime_unlock_peer_response_missing";
        const string sessionId = "sess_runtime_unlock_peer_response_missing";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=4; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_peer_response_timeout; recovery_reason=tuna_activation_offer_peer_response_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=2; authorized_observed_lane=(none); authority_failure_reason=(none); cutthrough_pending=1; cutthrough_active=0; cutthrough_attempt=0", secondsOffset: 44),
            LogLine($"event=runtime_unlock_cutthrough_started; session_id={sessionId}; offer_generation=12; contract_generation=4; attempt=1; trigger=runtime_unlock; reason=peer_response_timeout_under_regular_v4_recovery", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=4; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_peer_response_timeout; recovery_reason=tuna_activation_offer_peer_response_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=1; authority_attempt=2; authorized_observed_lane=control_to_bulk_endpoint; authority_failure_reason=(none); cutthrough_pending=0; cutthrough_active=1; cutthrough_offer_sent=0; cutthrough_peer_received=0; cutthrough_completed=0; cutthrough_attempt=1", secondsOffset: 47),
            LogLine($"event=runtime_unlock_cutthrough_offer_sent; session_id={sessionId}; offer_generation=12; contract_generation=4; attempt=1; observed_lane=control_to_bulk_endpoint", secondsOffset: 48),
            LogLine($"event=tuna_acceleration_offer_queued; reason=runtime_unlock; session_id={sessionId}; lanes=file; payer_decision_id=9; generation=12; observed_lane=control_to_bulk_endpoint; queue_local_only=0; recovery_requested=0; recovery_reason=(none)", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout; timeout_ms=2500; session_id={sessionId}; payer_decision_id=9; generation=12; observed_lane=control_to_bulk_endpoint", secondsOffset: 52),
            LogLine($"event=runtime_unlock_cutthrough_failed; session_id={sessionId}; payer_decision_id=9; offer_generation=12; contract_generation=4; attempt=1; reason=runtime_unlock_peer_response_not_received", secondsOffset: 53),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=9; generation=12; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled=0; retry_after_recovery_armed=0; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested=0; recovery_reason=runtime_unlock_peer_response_not_received", secondsOffset: 54),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=536870912; bytes_transferred=0; chunks_transferred=0; chunk_count=24962; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    private static string[] BuildRuntimeUnlockCutThroughPeerReceivedRegularActivationCycleFixture()
    {
        return
        [
            LogLine("event=runtime_unlock_cutthrough_started; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; trigger=runtime_unlock; reason=peer_response_timeout_under_regular_v4_recovery", secondsOffset: 20),
            LogLine("event=runtime_unlock_cutthrough_offer_sent; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; observed_lane=control_to_bulk_endpoint", secondsOffset: 21),
            LogLine("event=runtime_unlock_cutthrough_peer_received; session_id=sess_redacted; payer_decision_id=9; offer_generation=12; contract_generation=4; attempt=1; source=transport_acceleration_answer", secondsOffset: 22),
            LogLine("event=runtime_unlock_cutthrough_completed; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; reason=answer_acknowledged", secondsOffset: 23),
            .. BuildRouteAwareLiveRegularActivationCycleFixture()
        ];
    }

    private static string[] BuildCleanCompletedTransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; chunk_range=0-1; chunk_frame_count=2; raw_bytes=49152; lane=bulk"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=49251; secure_payload_bytes=49476; bridge_payload_bytes=49553; bridge_command_bytes=49653; max_allowed_bytes=65536"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-1; payload_bytes=49251; serialized_payload_bytes=49251; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-1; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    private static string[] BuildCleanCompletedV4TransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_v6_negotiated; transfer_id={transferId}; session_id=sess_a; direction=outbound; negotiated_version=6"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Receiver"),
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size=21504; pump_depth=8; pending_send_bytes_limit=2097152"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; file_only=1"),
            LogLine($"event=filetransfer_v4_manifest_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_manifest_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_feedback_first_success; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; lane=bulk; secondary_lane=control"),
            LogLine($"event=filetransfer_v4_feedback_secondary_completed; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; lane=control; elapsed_ms=3"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=1000; scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent=64512; repair_send_count=0"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; batch_chunk_count=3; chunk_range=0-2; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; payload_bytes=64700; serialized_payload_bytes=64700; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512; batch_profile=v4_default_21k"),
            LogLine($"event=filetransfer_v4_chunk_batch_received; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512"),
            LogLine($"event=filetransfer_v4_sparse_write_committed; transfer_id={transferId}; session_id=sess_a; chunk_count=3; bytes_written=64512; contiguous_committed_chunk_index=3; durable_received_highest_chunk_index=2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; payload_bytes_sent=65024; payload_bytes_per_second=6502400; in_flight=1; in_flight_bytes=65024; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; in_flight_bytes_max=65024; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    private static string[] BuildCleanCompletedRegularNknV4TransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id=sess_a; direction=Outbound; protocol_version=4; activation=primary_regular_nkn; runtime_profile=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id=sess_a; direction=Inbound; protocol_version=4; activation=primary_regular_nkn; runtime_profile=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size_bytes=21504; pipeline_depth=8; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size_bytes=21504; pipeline_depth=8; reason=role=Receiver"),
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size=21504; pump_depth=8; pending_send_bytes_limit=2097152"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; session_open_chunk_size_bytes=21504; session_open_pipeline_depth=8"),
            LogLine($"event=filetransfer_v4_manifest_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.manifest.v4; payload_bytes=167; serialized_payload_bytes=167; raw_chunk_bytes=0; chunk_count=0"),
            LogLine($"event=filetransfer_v4_manifest_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0; terminal_ready=0"),
            LogLine($"event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=control; queued_frames=1; queued_bytes=1024"),
            LogLine($"event=filetransfer_v4_feedback_first_success; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=bulk; secondary_lane=control"),
            LogLine($"event=filetransfer_v4_feedback_secondary_completed; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=control; elapsed_ms=3"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=1000; scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent=64512; repair_send_count=0"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; batch_chunk_count=3; chunk_range=0-2; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64615; secure_payload_bytes=64840; bridge_payload_bytes=64917; bridge_command_bytes=65017; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.055"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; payload_bytes=64615; serialized_payload_bytes=64615; raw_chunk_bytes=64512; chunk_count=3; batch_chunk_count=3"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3; batch_chunk_count=3"),
            LogLine($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512; batch_profile=v4_default_21k"),
            LogLine($"event=filetransfer_v4_chunk_batch_received; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512"),
            LogLine($"event=filetransfer_v4_sparse_write_committed; transfer_id={transferId}; session_id=sess_a; chunk_count=3; bytes_written=64512; contiguous_committed_chunk_index=3; durable_received_highest_chunk_index=2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; payload_bytes_sent=64917; payload_bytes_per_second=6491700; in_flight=1; in_flight_bytes=64917; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; in_flight_bytes_max=64917; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    private static string[] BuildLiveSummaryLines(
        double averageGoodput,
        double minimumGoodput,
        int bridgeWaiting,
        string protocolVersion = "3",
        double v4BatchRatio = 0,
        double v4PayloadFillPercent = 0)
    {
        return
        [
            "artifact_kind=live-nkn",
            "mode=nkn-fast",
            "verdict=PASS",
            $"average_goodput_bytes_per_second={averageGoodput}",
            $"min_goodput_bytes_per_second={minimumGoodput}",
            $"data_protocol_version={protocolVersion}",
            "v4_batch_ratio=1.0",
            $"v4_batch_ratio={v4BatchRatio.ToString("F6", CultureInfo.InvariantCulture)}",
            $"v4_average_bridge_payload_fill_percent={v4PayloadFillPercent.ToString("F3", CultureInfo.InvariantCulture)}",
            "v4_feedback_both_failed_count=0",
            "v4_sender_failed_count=0",
            "v4_receiver_failed_count=0",
            "legacy_data_protocol_started_count=0",
            "unexpected_legacy_data_frame_during_v4_count=0",
            "reorder_event_count=0",
            "request_timeout_count=0",
            "retry_requested_count=0",
            "payload_rejected_count=0",
            "decode_failure_count=0",
            "message_rejected_count=0",
            "bridge_bulk_send_failure_count=0",
            "bridge_bulk_queue_clear_count=0",
            $"bridge_bulk_queue_waiting_count={bridgeWaiting}",
            "bridge_bulk_queue_severe_count=0",
            "media_queue_drop_count=0",
            "media_send_failure_count=0",
            "media_queue_severe_count=0"
        ];
    }

    private static readonly DateTimeOffset FakeLogStartUtc = new(2026, 4, 26, 11, 17, 58, TimeSpan.Zero);

    private static string LogLine(string message, int secondsOffset = 0)
        => $"[{FakeLogStartUtc.AddSeconds(secondsOffset).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}] [INFO] [FileTransferTest] {message}";

    private static string RetimestampLogLine(string line, int secondsOffset)
        => Regex.Replace(
            line,
            @"^\[[^\]]+\]",
            $"[{FakeLogStartUtc.AddSeconds(secondsOffset).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}]");

    private static string RemoveSemicolonLogField(string line, string fieldName)
        => string.Join(
            "; ",
            line.Split("; ", StringSplitOptions.None)
                .Where(part => !part.StartsWith(fieldName + "=", StringComparison.Ordinal)));

    private static List<string> StretchTransferWindowForWarningRate(IEnumerable<string> lines, int terminalOffsetSeconds = 120)
    {
        return lines
            .Select(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) ||
                            line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal)
                ? RetimestampLogLine(line, terminalOffsetSeconds)
                : line)
            .ToList();
    }

    private static async Task<AnalyzeFixtureResult> RunAnalyzeFixtureAsync(
        IReadOnlyList<string> logLines,
        IReadOnlyList<string>? extraArguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops", Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(tempRoot, "nlink.log");
        var artifactDir = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllLinesAsync(logPath, logLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var args = new List<string>
        {
            "-Mode",
            "AnalyzeRetained",
            "-LogPath",
            logPath,
            "-ArtifactDir",
            artifactDir
        };
        if (extraArguments is not null)
        {
            args.AddRange(extraArguments);
        }

        var script = await RunFileTransferOpsAsync(repoRoot, args, environment);
        return new AnalyzeFixtureResult(tempRoot, artifactDir, script);
    }

    private static async Task<ScriptResult> RunFileTransferOpsAsync(string repoRoot, IReadOnlyList<string> arguments)
        => await RunFileTransferOpsAsync(repoRoot, arguments, environment: null);

    private static async Task<ScriptResult> RunFileTransferOpsAsync(
        string repoRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var scriptPath = Path.Combine(repoRoot, "tools", "FileTransfer-Ops.ps1");
        return await RunPowerShellFileAsync(repoRoot, scriptPath, arguments, environment);
    }

    private static async Task<ScriptResult> RunPowerShellFileAsync(
        string workingDirectory,
        string scriptPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            }
        };

        ApplyProcessEnvironment(process.StartInfo, environment);

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static Dictionary<string, string> BuildFakeLiveNknEnvironment(double? fakeGoodputBytesPerSecond = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NLINK_FILETRANSFER_NKN_SOAK_FAKE_GUI"] = "1"
        };

        if (fakeGoodputBytesPerSecond.HasValue)
        {
            environment["NLINK_FILETRANSFER_NKN_SOAK_FAKE_GOODPUT_BPS"] = fakeGoodputBytesPerSecond.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        return environment;
    }

    private static Dictionary<string, string> BuildFakeRouteAcceptanceEnvironment(string timestamp)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI"] = "1",
            ["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TIMESTAMP"] = timestamp
        };
    }

    private static void AssertRequiredLiveArtifacts(string artifactDir)
    {
        foreach (var artifactName in RequiredLiveNknArtifactFiles.Concat(RequiredArtifactFiles))
        {
            Assert.True(
                File.Exists(Path.Combine(artifactDir, artifactName)),
                $"Expected live NKN artifact to exist: {Path.Combine(artifactDir, artifactName)}");
        }
    }

    private static void AssertRequiredRouteAcceptanceArtifacts(string runRoot)
    {
        Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-summary.txt")), "Expected route acceptance text summary.");
        Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-summary.json")), "Expected route acceptance JSON summary.");
        foreach (var directoryName in RequiredRouteAcceptanceSubdirectories)
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(Directory.Exists(artifactDir), $"Expected route acceptance subdirectory: {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")), $"Expected retained log slice in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-route-consistency-summary.txt")), $"Expected route consistency summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "transfer-terminal-summary.txt")), $"Expected terminal summary in {artifactDir}");
        }

        foreach (var directoryName in new[] { "regular-nkn-64mb-quick", "regular-nkn-128mb-target" })
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-live-nkn-summary.txt")), $"Expected live NKN summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-live-nkn-summary.json")), $"Expected live NKN JSON summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-live-nkn-cycles.jsonl")), $"Expected live NKN cycles in {artifactDir}");
        }

        foreach (var directoryName in new[] { "tuna-128mb-no-fault", "tuna-128mb-fallback" })
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-tuna-gui-summary.json")), $"Expected Tuna GUI summary in {artifactDir}");
        }
    }

    private static void AssertRequiredPhase4RouteAcceptanceArtifacts(string runRoot)
    {
        Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), "Expected Phase 4 route acceptance text summary.");
        Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.json")), "Expected Phase 4 route acceptance JSON summary.");
        foreach (var directoryName in RequiredPhase4RouteAcceptanceSubdirectories)
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(Directory.Exists(artifactDir), $"Expected Phase 4 route acceptance subdirectory: {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")), $"Expected retained log slice in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-route-consistency-summary.txt")), $"Expected route consistency summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "transfer-terminal-summary.txt")), $"Expected terminal summary in {artifactDir}");
        }

        Assert.True(File.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb", "filetransfer-live-nkn-summary.txt")), "Expected Phase 4 regular NKN summary.");
        foreach (var directoryName in RequiredPhase4RouteAcceptanceSubdirectories.Where(static name => name != "regular-nkn-v4-64mb"))
        {
            Assert.True(File.Exists(Path.Combine(runRoot, directoryName, "filetransfer-tuna-gui-summary.json")), $"Expected Phase 4 Tuna GUI summary in {directoryName}.");
        }
    }

    private static void AssertRequiredPhase5RouteAcceptanceArtifacts(string runRoot)
    {
        Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), "Expected Phase 5 route acceptance text summary.");
        Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "Expected Phase 5 route acceptance JSON summary.");
        foreach (var directoryName in RequiredPhase5RouteAcceptanceSubdirectories)
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(Directory.Exists(artifactDir), $"Expected Phase 5 route acceptance subdirectory: {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")), $"Expected retained log slice in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-route-consistency-summary.txt")), $"Expected route consistency summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "transfer-terminal-summary.txt")), $"Expected terminal summary in {artifactDir}");
        }

        Assert.True(File.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb", "filetransfer-live-nkn-summary.txt")), "Expected Phase 5 regular NKN summary.");
        foreach (var directoryName in RequiredPhase5RouteAcceptanceSubdirectories.Where(static name => name != "regular-nkn-v4-64mb"))
        {
            Assert.True(File.Exists(Path.Combine(runRoot, directoryName, "filetransfer-tuna-gui-summary.json")), $"Expected Phase 5 Tuna GUI summary in {directoryName}.");
        }

        Assert.False(Directory.Exists(Path.Combine(runRoot, "live-multi-toggle-off-on-off-64mb")), "Phase 5 should use the canonical 256 MiB repeated-toggle stress instead of the old 64 MiB multi-toggle row.");
    }

    private static void ApplyProcessEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
    }

    private static async Task<ScriptResult> RunParserAsync(string scriptPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops-parse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var parserHarnessPath = Path.Combine(tempRoot, "parse-filetransfer-ops.ps1");
            await File.WriteAllTextAsync(parserHarnessPath, BuildParserHarness(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(parserHarnessPath);
            process.StartInfo.ArgumentList.Add(scriptPath);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<ScriptResult> RunPowerShellScriptTextAsync(string scriptText, IReadOnlyList<string> arguments)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var scriptPath = Path.Combine(tempRoot, "harness.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static Dictionary<string, string> ReadArtifactReport(string artifactDir, string fileName)
    {
        var reportPath = Path.Combine(artifactDir, fileName);
        Assert.True(File.Exists(reportPath), $"Expected artifact report: {reportPath}");

        return File.ReadAllLines(reportPath)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);
    }

    private static string[] ExtractTopLevelPowerShellParameterNames(string scriptText)
    {
        var match = Regex.Match(scriptText, @"(?s)^param\((?<body>.*?)\)\s*Set-StrictMode");
        Assert.True(match.Success, "Could not find top-level param block before Set-StrictMode.");

        return Regex.Matches(match.Groups["body"].Value, @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["name"].Value)
            .Where(name => !string.Equals(name, "true", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "false", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string[] ExtractPowerShellValidateSetValues(string scriptText, string parameterName)
    {
        var pattern = @"\[ValidateSet\((?<body>[^)]*)\)\]\s*\[string\]\$" + Regex.Escape(parameterName) + @"\b";
        var match = Regex.Match(scriptText, pattern);
        Assert.True(match.Success, $"Could not find ValidateSet for ${parameterName}.");

        return Regex.Matches(match.Groups["body"].Value, @"[""'](?<value>[^""']+)[""']")
            .Select(match => match.Groups["value"].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ExtractPowerShellFunctionBody(string scriptText, string functionName, string nextFunctionName)
    {
        var pattern = @"(?s)function\s+" + Regex.Escape(functionName) + @"\s*\{(?<body>.*?)\r?\n\}\s*\r?\n\s*function\s+" + Regex.Escape(nextFunctionName) + @"\b";
        var match = Regex.Match(scriptText, pattern);
        Assert.True(match.Success, $"Could not find PowerShell function body for {functionName}.");

        return match.Groups["body"].Value;
    }

    private static string BuildParserHarness()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors) | Out-Null

if ($errors -and $errors.Count -gt 0) {
    foreach ($error in $errors) {
        Write-Error ("{0}:{1}:{2} {3}" -f $error.Extent.File, $error.Extent.StartLineNumber, $error.Extent.StartColumnNumber, $error.Message)
    }

    exit 1
}
""";
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "nLink.sln")) &&
                File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }

    private sealed record AnalyzeFixtureResult(string TempRoot, string ArtifactDir, ScriptResult Script);

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
