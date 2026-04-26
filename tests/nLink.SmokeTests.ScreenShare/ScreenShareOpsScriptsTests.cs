using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
public sealed class ScreenShareOpsScriptsTests
{
    private const string RetainedAnalyzerManifestRelativePath = "tools/ScreenShareOps/retained-analyzer-chain.json";

    private static readonly string[] ExpectedScreenShareOpsModes =
    [
        "AnalyzeRetained",
        "ExternalTopologyAudit",
        "LocalSoak",
        "NknSoak",
        "SupportCapture",
        "Test",
        "TrackBRetained"
    ];

    private static readonly string[] ExpectedExternalTopologyProfiles =
    [
        "Default",
        "DefaultKeepAlive",
        "MediaFanout12",
        "MediaFanout8",
        "PinnedMainnetRpc",
        "PinnedSeedHttps"
    ];

    private static readonly string[] ExpectedRetainedAnalyzerScripts =
    [
        "Analyze-ScreenShareLatencyRegression.ps1",
        "Analyze-ScreenShareHelperUpstreamLatency.ps1",
        "Analyze-ScreenShareHelperReadyPath.ps1",
        "Analyze-ScreenShareHelperReceivePath.ps1",
        "Analyze-ScreenShareHelperBridgeIngress.ps1",
        "Analyze-ScreenShareHelperNknReceive.ps1",
        "Analyze-ScreenShareHelperWsReceive.ps1",
        "Analyze-ScreenShareHelperSocketReceive.ps1",
        "Analyze-ScreenShareExternalDelivery.ps1",
        "Analyze-ScreenShareExternalTransportHealth.ps1"
    ];

    private static readonly (string Stage, string FileName)[] ExpectedRetainedClassificationReports =
    [
        ("upstream_latency", "helper-upstream-latency-analysis.txt"),
        ("ready_path", "helper-ready-path-analysis.txt"),
        ("receive_path", "helper-receive-path-analysis.txt"),
        ("bridge_ingress", "helper-bridge-ingress-analysis.txt"),
        ("nkn_receive", "helper-nkn-receive-analysis.txt"),
        ("ws_receive", "helper-ws-receive-analysis.txt"),
        ("socket_receive", "helper-socket-receive-analysis.txt"),
        ("external_delivery", "helper-external-delivery-analysis.txt"),
        ("external_transport_health", "helper-external-transport-health-analysis.txt")
    ];

    private static readonly string[] ExpectedExternalTransportClassifications =
    [
        "external_receive_latency",
        "network_delivery_latency",
        "steady_external_delivery_latency"
    ];

    private static readonly string[] ScreenShareOpsImplementationFiles =
    [
        "AnalyzerOrchestration.ps1"
    ];

    private static readonly string[] NknSoakPublicParameters =
    [
        "ExePath",
        "DurationSeconds",
        "Build",
        "TimeoutSeconds",
        "StrongBaselineArtifactDir",
        "SafeBaselineArtifactDir",
        "SkipBehaviorFirstGate"
    ];

    private static readonly string[] NknSoakImplementationFiles =
    [
        "ProcessAndBridge.ps1",
        "LogParsing.ps1",
        "BaselineComparison.ps1",
        "SoakSummaryExtraction.ps1",
        "ArtifactWriters.ps1",
        "StabilizationGates.ps1"
    ];

    private static readonly string[] RequiredNknSoakArtifactFiles =
    [
        "helper-quality-summary.txt",
        "helper-upstream-latency-summary.txt",
        "helper-ready-path-summary.txt",
        "helper-receive-path-summary.txt",
        "helper-bridge-ingress-summary.txt",
        "helper-nkn-receive-summary.txt",
        "helper-ws-receive-summary.txt",
        "helper-socket-receive-summary.txt",
        "bridge-event-loop-summary.txt",
        "bridge-media-send-summary.txt",
        "bridge-transport-health-summary.txt",
        "helper-frame-loss-epoch.txt",
        "helper-epoch-timeline.txt",
        "helper-reassembler-root-cause-summary.txt",
        "helper-pressure-summary.txt",
        "helper-recovery-investigation-summary.txt",
        "health-snapshot-summary.txt",
        "quality-presentation-summary.txt",
        "reduced-promotion-summary.txt",
        "sender-cadence-summary.txt",
        "recovery-burst-summary.txt",
        "transport-mode-summary.txt",
        "baseline-comparison.txt",
        "stability-gates-summary.txt"
    ];

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareOpsScript_ParsesWithoutSyntaxErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var scriptPaths = new[]
            {
                Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1")
            }
            .Concat(ScreenShareOpsImplementationFiles.Select(fileName => Path.Combine(repoRoot, "tools", "ScreenShareOps", fileName)))
            .ToArray();

        foreach (var scriptPath in scriptPaths)
        {
            Assert.True(File.Exists(scriptPath), $"Expected screenshare ops script to exist: {scriptPath}");

            var result = await RunParserAsync(scriptPath);
            Assert.True(
                result.ExitCode == 0,
                $"ScreenShare ops script parser validation failed for {scriptPath}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareOpsSupportCapture_OutputMentionsDiagnosticsEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var result = await RunScreenShareOpsAsync(repoRoot, ["-Mode", "SupportCapture"]);

        Assert.True(
            result.ExitCode == 0,
            $"Expected SupportCapture to print instructions.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        Assert.Contains("screenshare evidence summary", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshare-evidence.txt", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full screenshare soak artifact", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScreenShareOps_PublicModesRemainClosedSet_AndDelegatesToStableEntrypoints()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Equal(ExpectedScreenShareOpsModes, ExtractPowerShellValidateSetValues(scriptText, "Mode"));
        Assert.Equal(ExpectedExternalTopologyProfiles, ExtractPowerShellValidateSetValues(scriptText, "ExternalTopologyProfile"));
        Assert.Contains("Test-Lanes.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScreenShareNknSoak.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Invoke-ScreenShareRetainedAnalyzerChain", scriptText, StringComparison.Ordinal);
        Assert.Contains("Write-ScreenShareOperatorVerdictReport", scriptText, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedAnalyzerManifest_DefinesStableAnalyzerChain()
    {
        var repoRoot = FindRepoRoot();
        var manifest = LoadRetainedAnalyzerManifest(repoRoot);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(ExpectedRetainedAnalyzerScripts, manifest.RetainedAnalyzers.Select(analyzer => analyzer.Script).ToArray());
        Assert.Equal(ExpectedRetainedClassificationReports, GetRetainedClassificationReports(manifest));
        Assert.Equal(ExpectedExternalTransportClassifications, manifest.ExternalTransportClassifications);
        Assert.All(manifest.RetainedAnalyzers, analyzer =>
        {
            Assert.False(string.IsNullOrWhiteSpace(analyzer.Id));
            Assert.EndsWith(".ps1", analyzer.Script, StringComparison.Ordinal);
            Assert.EndsWith(".txt", analyzer.Report, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(repoRoot, "tools", analyzer.Script)), $"Expected retained analyzer script: {analyzer.Script}");
        });

        Assert.Equal(manifest.RetainedAnalyzers.Length, manifest.RetainedAnalyzers.Select(analyzer => analyzer.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(manifest.RetainedAnalyzers.Length, manifest.RetainedAnalyzers.Select(analyzer => analyzer.Script).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(manifest.RetainedAnalyzers.Length, manifest.RetainedAnalyzers.Select(analyzer => analyzer.Report).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            GetRetainedClassificationReports(manifest).Length,
            GetRetainedClassificationReports(manifest).Select(report => report.Stage).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunScreenShareNknSoakScripts_ParseWithoutSyntaxErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var scripts = new[]
            {
                Path.Combine(repoRoot, "tools", "Run-ScreenShareNknSoak.ps1")
            }
            .Concat(NknSoakImplementationFiles.Select(fileName => Path.Combine(repoRoot, "tools", "ScreenShareSoak", fileName)))
            .ToArray();

        foreach (var scriptPath in scripts)
        {
            Assert.True(File.Exists(scriptPath), $"Expected NKN soak script to exist: {scriptPath}");

            var result = await RunParserAsync(scriptPath);
            Assert.True(
                result.ExitCode == 0,
                $"NKN soak script parser validation failed for {scriptPath}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        }
    }

    [Fact]
    public void RunScreenShareNknSoak_PublicParameterSetRemainsStable()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-ScreenShareNknSoak.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Equal(NknSoakPublicParameters, ExtractTopLevelPowerShellParameterNames(scriptText));
    }

    [Fact]
    public void RunScreenShareNknSoak_RefactorKeepsFacadeAndRetainedArtifactWriters()
    {
        var repoRoot = FindRepoRoot();
        var facadePath = Path.Combine(repoRoot, "tools", "Run-ScreenShareNknSoak.ps1");
        var implementationRoot = Path.Combine(repoRoot, "tools", "ScreenShareSoak");

        var facadeText = File.ReadAllText(facadePath);
        Assert.Contains("ScreenShareSoak", facadeText, StringComparison.Ordinal);
        foreach (var fileName in NknSoakImplementationFiles)
        {
            Assert.Contains(fileName, facadeText, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(implementationRoot, fileName)), $"Expected NKN soak implementation file: {fileName}");
        }

        var artifactWriterText = File.ReadAllText(Path.Combine(implementationRoot, "ArtifactWriters.ps1")) +
            Environment.NewLine +
            File.ReadAllText(Path.Combine(implementationRoot, "StabilizationGates.ps1"));
        foreach (var artifactFile in RequiredNknSoakArtifactFiles)
        {
            Assert.Contains(artifactFile, artifactWriterText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScreenShareOps_ExternalTopologyProfiles_AreOperatorOnlyEnvironmentScopes()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1"));
        var bridgeText = File.ReadAllText(Path.Combine(repoRoot, "tools", "nkn-bridge", "index.js"));

        Assert.Contains("Set-ExternalTopologyProfileEnvironment", scriptText, StringComparison.Ordinal);
        Assert.Contains("Restore-ExternalTopologyProfileEnvironment", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_SCREENSHARE_EXTERNAL_TOPOLOGY_PROFILE", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_NKN_SEED_RPC", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_BRIDGE_REUSE_MODE", scriptText, StringComparison.Ordinal);
        Assert.Contains("https://mainnet-rpc-node-0001.nkn.org/mainnet/api/wallet", scriptText, StringComparison.Ordinal);
        Assert.Contains("https://seed.nkn.org:30003", scriptText, StringComparison.Ordinal);
        Assert.Contains("\"MediaFanout12\"", scriptText, StringComparison.Ordinal);
        Assert.Contains("-Value \"12\"", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("http://seed.nkn.org:30003", scriptText, StringComparison.Ordinal);
        Assert.Contains("const DEFAULT_NUM_SUBCLIENTS = 4;", bridgeText, StringComparison.Ordinal);
        Assert.Contains("const DEFAULT_MEDIA_NUM_SUBCLIENTS = 8;", bridgeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StabilizationGate_TreatsBufferedFollowerWindowAsResolvedAfterVisibleRecoverySuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var harnessPath = Path.Combine(tempRoot, "run-gate.ps1");
            var gatePath = Path.Combine(repoRoot, "tools", "ScreenShareSoak", "StabilizationGates.ps1");
            File.WriteAllText(
                harnessPath,
                BuildResolvedFollowerGateHarness(gatePath, tempRoot),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptAsync(repoRoot, harnessPath, []);
            Assert.True(
                result.ExitCode == 0,
                $"Expected stabilization gate harness to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var gateSummary = File.ReadAllText(Path.Combine(tempRoot, "stability-gates-summary.txt"));
            Assert.Contains("behavior_first_gate_status=pass", gateSummary, StringComparison.Ordinal);
            Assert.Contains("resolved_post_recovery_follower_window=1", gateSummary, StringComparison.Ordinal);
            Assert.Contains("resolved_recovery_owner_replacement=1", gateSummary, StringComparison.Ordinal);
            Assert.Contains("invariant_failure_count=0", gateSummary, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Theory]
    [InlineData("pass", "pass", "no_material_latency_regression", "1", "0", "0", "steady_external_delivery_latency")]
    [InlineData("fail_local_regression", "fail", "real_helper_latency_regression", "1", "0", "0", "local_reader_backlog_latency")]
    [InlineData("fail_live_transport_evidence", "fail", "real_helper_latency_regression", "1", "0", "0", "steady_external_delivery_latency")]
    [InlineData("inconclusive_mixed", "pass", "no_material_latency_regression", "1", "0", "0", "mixed_or_inconclusive")]
    public async Task ScreenShareOpsAnalyzeRetained_WritesOperatorVerdict(
        string expectedVerdict,
        string behaviorFirstGateStatus,
        string regressionClassification,
        string effectiveMediaPlaneActive,
        string steadyStateUsedControlFallback,
        string recoveryCompletionAccountingMismatch,
        string deepestClassification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-verdict", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                behaviorFirstGateStatus,
                regressionClassification,
                effectiveMediaPlaneActive,
                steadyStateUsedControlFallback,
                recoveryCompletionAccountingMismatch,
                deepestClassification);

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Expected verdict generation to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var report = ReadVerdictReport(tempRoot);
            Assert.Equal(expectedVerdict, report["operator_verdict"]);
            Assert.Equal(tempRoot, report["artifact_dir"]);
            Assert.Equal(deepestClassification, report["deepest_track_b_classification"]);
            Assert.Equal("(none)", report["missing_required_inputs"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_WritesInvalidNoSessionVerdict()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-no-session", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateNoSessionArtifact(tempRoot);

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Expected invalid no-session verdict generation to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var report = ReadVerdictReport(tempRoot);
            Assert.Equal("invalid_no_screenshare_session", report["operator_verdict"]);
            Assert.Equal("1", report["no_screenshare_session"]);
            Assert.Contains("no_frames_sent", report["no_screenshare_session_reason"], StringComparison.Ordinal);
            Assert.Contains("no_helper_apply_samples", report["no_screenshare_session_reason"], StringComparison.Ordinal);
            Assert.Equal("0", report["no_screenshare_frames_sent"]);
            Assert.Equal("no_visible_baseline", report["no_screenshare_helper_session_phase"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task LatencyRegressionAnalyzer_NoSessionArtifact_WritesInvalidReportWithoutHealthEvents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-latency-no-session", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateNoSessionArtifact(tempRoot);
            File.Delete(Path.Combine(tempRoot, "latency-regression-analysis.txt"));

            var result = await RunPowerShellScriptAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Analyze-ScreenShareLatencyRegression.ps1"),
                [
                    "-CandidateArtifactDir",
                    tempRoot,
                    "-ReferenceArtifactDirs",
                    tempRoot
                ]);

            Assert.True(
                result.ExitCode == 0,
                $"Expected no-session latency analyzer to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var report = ReadArtifactReport(tempRoot, "latency-regression-analysis.txt");
            Assert.Equal("invalid_no_screenshare_session", report["comparison_status"]);
            Assert.Equal("invalid_no_screenshare_session", report["regression_classification"]);
            Assert.Equal("(skipped)", report["reference_artifact_dirs"]);
            Assert.Contains("no_helper_apply_samples", report["classification_evidence"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void GuiSmokeWindows_ConnectionWaitFailuresIncludeStatusContext()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));

        Assert.Contains("Get-ConnectionWaitDiagnosticContext", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_status=", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_banner=", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_status=", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_banner=", scriptText, StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for helpee Allow approval UI. $(Get-ConnectionWaitDiagnosticContext -Context $Context)", scriptText, StringComparison.Ordinal);
        Assert.Contains("Helper reached Connection failed before helpee approval. $(Get-ConnectionWaitDiagnosticContext -Context $Context)", scriptText, StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for connected chat on both helper and helpee. $(Get-ConnectionWaitDiagnosticContext -Context $Context)", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_IncludesOptionalQualityPresentationEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-verdict", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "pass",
                "no_material_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            File.WriteAllLines(
                Path.Combine(tempRoot, "quality-presentation-summary.txt"),
                [
                    "active_encode_target_width=1440",
                    "active_encode_target_height=810",
                    "active_encode_target_bitrate=6000000",
                    "active_encode_target_fps=8",
                    "encoder_profile=normal",
                    "sender_freshness_mode=normal",
                    "sender_operating_state=normal",
                    "effective_quality_preset=text_first_1x",
                    "capture_scale=1",
                    "actual_encoded_displayable_fps=7.9",
                    "encode_cadence_target_fps=8",
                    "sender_process_cpu_percent=18.4",
                    "raw_capture_event_count=74",
                    "raw_frames_skipped_before_encode=11",
                    "helper_surface_interpolation_mode=none",
                    "helper_surface_frame_width=1440",
                    "helper_surface_frame_height=810",
                    "helper_surface_viewport_width=1521",
                    "helper_surface_viewport_height=856",
                    "helper_surface_render_scaling=1.25",
                    "helper_surface_scale_ratio=1.321",
                    "h264_reference_taint_active=1",
                    "h264_reference_taint_enter_count=3",
                    "h264_reference_taint_release_count=2",
                    "h264_reference_taint_last_reason=recovery_runway_overflow",
                    "h264_reference_taint_dropped_non_key_count=4",
                    "h264_reference_taint_decoder_reset_count=2",
                    "h264_reference_taint_stale_visible_stable_enter_count=1",
                    "stale_normal_non_key_visible_suppress_count=6",
                    "decoded_stale_visible_suppress_count=5",
                    "post_quarantine_settle_suppress_count=2",
                    "h264_reference_quarantine_active=1",
                    "h264_reference_quarantine_release_blocked_count=2",
                    "h264_reference_quarantine_last_blocker=quiet_window_recent_visible_stable_stale_p_frame_drop",
                    "h264_reference_quarantine_quiet_release_count=1",
                    "motion_integrity_guard_active=1",
                    "motion_integrity_sampled_ratio=0.427",
                    "motion_integrity_peak_sampled_ratio=0.511",
                    "motion_integrity_scroll_active_band_count=7",
                    "motion_integrity_scroll_peak_band_ratio=0.625",
                    "motion_integrity_high_motion_frame_count=9",
                    "motion_integrity_scroll_trigger_count=4",
                    "motion_integrity_burst_enter_count=2",
                    "motion_integrity_burst_exit_count=1",
                    "motion_integrity_forced_keyframe_count=5",
                    "motion_integrity_last_trigger_kind=strong_scroll_motion",
                    "motion_integrity_last_reason=motion_keyframe_due",
                    "motion_integrity_idr_frame_ratio=0.67",
                    "motion_integrity_forced_idr_requested_count=5",
                    "motion_integrity_forced_idr_confirmed_count=4",
                    "motion_integrity_forced_idr_missed_count=1",
                    "motion_integrity_forced_idr_pending_count=0",
                    "motion_integrity_forced_idr_consecutive_miss_count=0",
                    "motion_integrity_forced_idr_burst_miss_count=1",
                    "motion_integrity_active_idr_frame_ratio=0.71",
                    "motion_integrity_forced_idr_last_miss_reason=next_displayable_output_was_not_idr",
                    "motion_integrity_encoder_rebuild_count=1",
                    "motion_integrity_encoder_rebuild_suppressed_count=2",
                    "motion_integrity_encoder_rebuild_pending=0",
                    "motion_integrity_encoder_rebuild_last_reason=encoder_rebuild_due_to_forced_idr_miss",
                    "cursor_capture_enabled=0",
                    "cursor_capture_desired_enabled=0",
                    "cursor_capture_control_supported=1",
                    "cursor_capture_apply_status=applied",
                    "cursor_capture_fallback_reason=(none)",
                    "cursor_delivery_mode=helper_overlay",
                    "cursor_sender_delivery_mode=helper_overlay",
                    "cursor_overlay_updates_sent_count=28",
                    "cursor_overlay_send_failure_count=0",
                    "cursor_overlay_mapping_failure_count=0",
                    "cursor_overlay_sender_last_status=captured_cursor_disabled",
                    "cursor_overlay_visible=1",
                    "cursor_overlay_updates_received_count=27",
                    "cursor_overlay_updates_applied_count=27",
                    "cursor_overlay_update_hz=29.5",
                    "cursor_overlay_last_age_ms=18",
                    "cursor_overlay_stale_count=0",
                    "cursor_overlay_last_status=captured_cursor_disabled"
                ]);

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Expected verdict generation to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var report = ReadVerdictReport(tempRoot);
            Assert.Equal("pass", report["operator_verdict"]);
            Assert.Equal("1440", report["quality_active_encode_target_width"]);
            Assert.Equal("810", report["quality_active_encode_target_height"]);
            Assert.Equal("8", report["quality_active_encode_target_fps"]);
            Assert.Equal("normal", report["quality_encoder_profile"]);
            Assert.Equal("text_first_1x", report["quality_effective_quality_preset"]);
            Assert.Equal("7.9", report["quality_actual_encoded_displayable_fps"]);
            Assert.Equal("8", report["quality_encode_cadence_target_fps"]);
            Assert.Equal("18.4", report["quality_sender_process_cpu_percent"]);
            Assert.Equal("74", report["quality_raw_capture_event_count"]);
            Assert.Equal("11", report["quality_raw_frames_skipped_before_encode"]);
            Assert.Equal("none", report["quality_helper_surface_interpolation_mode"]);
            Assert.Equal("1.321", report["quality_helper_surface_scale_ratio"]);
            Assert.Equal("1", report["quality_h264_reference_taint_active"]);
            Assert.Equal("3", report["quality_h264_reference_taint_enter_count"]);
            Assert.Equal("2", report["quality_h264_reference_taint_release_count"]);
            Assert.Equal("recovery_runway_overflow", report["quality_h264_reference_taint_last_reason"]);
            Assert.Equal("4", report["quality_h264_reference_taint_dropped_non_key_count"]);
            Assert.Equal("2", report["quality_h264_reference_taint_decoder_reset_count"]);
            Assert.Equal("1", report["quality_h264_reference_taint_stale_visible_stable_enter_count"]);
            Assert.Equal("6", report["quality_stale_normal_non_key_visible_suppress_count"]);
            Assert.Equal("5", report["quality_decoded_stale_visible_suppress_count"]);
            Assert.Equal("2", report["quality_post_quarantine_settle_suppress_count"]);
            Assert.Equal("1", report["quality_h264_reference_quarantine_active"]);
            Assert.Equal("2", report["quality_h264_reference_quarantine_release_blocked_count"]);
            Assert.Equal("quiet_window_recent_visible_stable_stale_p_frame_drop", report["quality_h264_reference_quarantine_last_blocker"]);
            Assert.Equal("1", report["quality_h264_reference_quarantine_quiet_release_count"]);
            Assert.Equal("1", report["quality_motion_integrity_guard_active"]);
            Assert.Equal("0.427", report["quality_motion_integrity_sampled_ratio"]);
            Assert.Equal("0.511", report["quality_motion_integrity_peak_sampled_ratio"]);
            Assert.Equal("7", report["quality_motion_integrity_scroll_active_band_count"]);
            Assert.Equal("0.625", report["quality_motion_integrity_scroll_peak_band_ratio"]);
            Assert.Equal("9", report["quality_motion_integrity_high_motion_frame_count"]);
            Assert.Equal("4", report["quality_motion_integrity_scroll_trigger_count"]);
            Assert.Equal("2", report["quality_motion_integrity_burst_enter_count"]);
            Assert.Equal("1", report["quality_motion_integrity_burst_exit_count"]);
            Assert.Equal("5", report["quality_motion_integrity_forced_keyframe_count"]);
            Assert.Equal("strong_scroll_motion", report["quality_motion_integrity_last_trigger_kind"]);
            Assert.Equal("motion_keyframe_due", report["quality_motion_integrity_last_reason"]);
            Assert.Equal("0.67", report["quality_motion_integrity_idr_frame_ratio"]);
            Assert.Equal("5", report["quality_motion_integrity_forced_idr_requested_count"]);
            Assert.Equal("4", report["quality_motion_integrity_forced_idr_confirmed_count"]);
            Assert.Equal("1", report["quality_motion_integrity_forced_idr_missed_count"]);
            Assert.Equal("0", report["quality_motion_integrity_forced_idr_pending_count"]);
            Assert.Equal("0", report["quality_motion_integrity_forced_idr_consecutive_miss_count"]);
            Assert.Equal("1", report["quality_motion_integrity_forced_idr_burst_miss_count"]);
            Assert.Equal("0.71", report["quality_motion_integrity_active_idr_frame_ratio"]);
            Assert.Equal("next_displayable_output_was_not_idr", report["quality_motion_integrity_forced_idr_last_miss_reason"]);
            Assert.Equal("1", report["quality_motion_integrity_encoder_rebuild_count"]);
            Assert.Equal("2", report["quality_motion_integrity_encoder_rebuild_suppressed_count"]);
            Assert.Equal("0", report["quality_motion_integrity_encoder_rebuild_pending"]);
            Assert.Equal("encoder_rebuild_due_to_forced_idr_miss", report["quality_motion_integrity_encoder_rebuild_last_reason"]);
            Assert.Equal("0", report["quality_cursor_capture_enabled"]);
            Assert.Equal("0", report["quality_cursor_capture_desired_enabled"]);
            Assert.Equal("1", report["quality_cursor_capture_control_supported"]);
            Assert.Equal("applied", report["quality_cursor_capture_apply_status"]);
            Assert.Equal("helper_overlay", report["quality_cursor_delivery_mode"]);
            Assert.Equal("helper_overlay", report["quality_cursor_sender_delivery_mode"]);
            Assert.Equal("28", report["quality_cursor_overlay_updates_sent_count"]);
            Assert.Equal("0", report["quality_cursor_overlay_send_failure_count"]);
            Assert.Equal("0", report["quality_cursor_overlay_mapping_failure_count"]);
            Assert.Equal("captured_cursor_disabled", report["quality_cursor_overlay_sender_last_status"]);
            Assert.Equal("1", report["quality_cursor_overlay_visible"]);
            Assert.Equal("27", report["quality_cursor_overlay_updates_received_count"]);
            Assert.Equal("27", report["quality_cursor_overlay_updates_applied_count"]);
            Assert.Equal("29.5", report["quality_cursor_overlay_update_hz"]);
            Assert.Equal("18", report["quality_cursor_overlay_last_age_ms"]);
            Assert.Equal("0", report["quality_cursor_overlay_stale_count"]);
            Assert.Equal("captured_cursor_disabled", report["quality_cursor_overlay_last_status"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Theory]
    [InlineData("external", "external_delivery_driven_catch_up", "external_delivery")]
    [InlineData("external_resolved_helper_history", "external_delivery_driven_catch_up", "external_delivery")]
    [InlineData("external_visible_stable_normal_low_fps", "external_delivery_driven_catch_up", "external_delivery")]
    [InlineData("external_visible_stable_low_apply_ratio", "external_delivery_driven_catch_up", "external_delivery")]
    [InlineData("external_visible_stable_stale_continuity_reason", "external_delivery_driven_catch_up", "external_delivery")]
    [InlineData("resolved_corridor_stale_health", "no_low_fps_catch_up_evidence", "none")]
    [InlineData("helper_recovery", "helper_recovery_or_visibility_catch_up", "helper_recovery_or_visibility")]
    [InlineData("helper_cadence", "helper_apply_cadence_limited", "helper_apply_cadence")]
    [InlineData("sender_budget", "sender_capture_or_encode_budget_limited", "sender_capture_or_encode_budget")]
    [InlineData("policy_hysteresis", "sender_policy_hysteresis", "sender_policy_hysteresis")]
    [InlineData("healthy", "no_low_fps_catch_up_evidence", "none")]
    [InlineData("healthy_no_recent_entries", "no_low_fps_catch_up_evidence", "none")]
    public async Task ScreenShareOpsAnalyzeRetained_WritesLowFpsCatchUpClassification(
        string scenario,
        string expectedClassification,
        string expectedPrimaryBlocker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-low-fps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "fail",
                "real_helper_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            CreateLowFpsScenarioArtifacts(tempRoot, scenario);

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Expected verdict generation to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var lowFpsReport = ReadArtifactReport(tempRoot, "low-fps-catch-up-summary.txt");
            Assert.Equal(expectedClassification, lowFpsReport["classification"]);
            Assert.Equal(expectedPrimaryBlocker, lowFpsReport["primary_blocker"]);
            Assert.True(lowFpsReport.ContainsKey("effective_apply_fps"));
            Assert.True(lowFpsReport.ContainsKey("sender_mode_counts"));
            Assert.Equal("0", lowFpsReport["candidate_queue_depth"]);
            Assert.Equal("0", lowFpsReport["candidate_queue_drops"]);
            Assert.Equal("0", lowFpsReport["candidate_send_failures"]);
            if (expectedClassification == "no_low_fps_catch_up_evidence")
            {
                Assert.Equal("healthy", lowFpsReport["remote_pressure_reason"]);
            }

            var verdict = ReadVerdictReport(tempRoot);
            Assert.Equal(expectedClassification, verdict["low_fps_catch_up_classification"]);
            Assert.Equal(expectedPrimaryBlocker, verdict["low_fps_primary_blocker"]);
            Assert.Equal(lowFpsReport["effective_apply_fps"], verdict["low_fps_effective_apply_fps"]);
            Assert.Equal(lowFpsReport["sender_mode_counts"], verdict["low_fps_sender_mode_counts"]);
            Assert.False(string.IsNullOrWhiteSpace(verdict["low_fps_next_action"]));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_WritesExternalTopologySummaryAndVerdictFields()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-topology", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "fail",
                "real_helper_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            CreateExternalTopologySourceArtifacts(
                tempRoot,
                profile: "MediaFanout8",
                selectedRpcKey: "bb9d9798",
                mediaSubClients: 8,
                socketMedianMs: 118,
                socketP95Ms: 240,
                queueDepth: 0,
                queueDrops: 0,
                sendFailures: 0);

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Expected verdict generation to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var topology = ReadArtifactReport(tempRoot, "external-topology-summary.txt");
            Assert.Equal("MediaFanout8", topology["external_topology_profile"]);
            Assert.Equal("external_delivery_candidate", topology["external_topology_classification"]);
            Assert.Equal("bb9d9798", topology["selected_rpc_key"]);
            Assert.Equal("8", topology["media_subclients"]);
            Assert.Equal("118", topology["socket_receive_median_ms"]);
            Assert.Equal("240", topology["socket_receive_p95_ms"]);

            var verdict = ReadVerdictReport(tempRoot);
            Assert.Equal("MediaFanout8", verdict["external_topology_profile"]);
            Assert.Equal("bb9d9798", verdict["external_topology_selected_rpc_key"]);
            Assert.Equal("8", verdict["external_topology_media_subclients"]);
            Assert.Equal("external_delivery_candidate", verdict["external_topology_classification"]);
            Assert.False(string.IsNullOrWhiteSpace(verdict["external_topology_next_action"]));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Theory]
    [InlineData("winner", "winner", "PinnedSeedHttps")]
    [InlineData("no_change", "no_change", "(none)")]
    [InlineData("regression", "regression", "(none)")]
    [InlineData("local_queue_regression", "local_queue_regression", "(none)")]
    public async Task ScreenShareOpsExternalTopologyAudit_ClassifiesTopologyMatrix(
        string scenario,
        string expectedClassification,
        string expectedWinnerProfile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-topology-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var artifactDirs = new List<string>
            {
                CreateExternalTopologySummaryArtifact(tempRoot, "default-1", "Default", "external_delivery_candidate", 200, 400),
                CreateExternalTopologySummaryArtifact(tempRoot, "default-2", "Default", "external_delivery_candidate", 210, 420),
                CreateExternalTopologySummaryArtifact(tempRoot, "default-3", "Default", "external_delivery_candidate", 190, 380)
            };

            switch (scenario)
            {
                case "winner":
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-1", "PinnedSeedHttps", "external_delivery_candidate", 120, 250));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-2", "PinnedSeedHttps", "external_delivery_candidate", 130, 260));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-3", "PinnedSeedHttps", "external_delivery_candidate", 125, 240));
                    break;
                case "no_change":
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-1", "PinnedSeedHttps", "external_delivery_candidate", 180, 360));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-2", "PinnedSeedHttps", "external_delivery_candidate", 175, 350));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-3", "PinnedSeedHttps", "external_delivery_candidate", 185, 370));
                    break;
                case "regression":
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-1", "PinnedSeedHttps", "external_delivery_candidate", 280, 600));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-2", "PinnedSeedHttps", "external_delivery_candidate", 290, 610));
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-3", "PinnedSeedHttps", "external_delivery_candidate", 210, 420));
                    break;
                default:
                    artifactDirs.Add(CreateExternalTopologySummaryArtifact(tempRoot, "candidate-1", "PinnedSeedHttps", "local_queue_regression", 120, 250, queueDepth: 2));
                    break;
            }

            var outputPath = Path.Combine(tempRoot, "audit.txt");
            var args = new List<string>
            {
                "-Mode",
                "ExternalTopologyAudit",
                "-ArtifactDirs",
                string.Join(';', artifactDirs),
                "-OutputPath"
            };
            args.Add(outputPath);

            var result = await RunScreenShareOpsAsync(repoRoot, args);
            Assert.True(
                result.ExitCode == 0,
                $"Expected topology audit to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var audit = ReadArtifactReport(tempRoot, "audit.txt");
            Assert.Equal(expectedClassification, audit["audit_classification"]);
            Assert.Equal(expectedWinnerProfile, audit["winner_profile"]);
            Assert.Contains("artifact|profile|classification", File.ReadAllText(outputPath), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_WritesMissingArtifactVerdictAndReturnsFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-verdict", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "pass",
                "no_material_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            File.Delete(Path.Combine(tempRoot, "recovery-burst-summary.txt"));

            var result = await RunVerdictOnlyAsync(repoRoot, tempRoot);
            Assert.True(
                result.ExitCode != 0,
                $"Expected missing artifact verdict generation to fail.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var report = ReadVerdictReport(tempRoot);
            Assert.Equal("inconclusive_missing_artifact", report["operator_verdict"]);
            Assert.Contains("recovery-burst-summary.txt", report["missing_required_inputs"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_RunsManifestAnalyzersInOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var manifest = LoadRetainedAnalyzerManifest(repoRoot);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-chain", Guid.NewGuid().ToString("N"));
        var analyzerRoot = Path.Combine(tempRoot, "fake-analyzers");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(analyzerRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "pass",
                "no_material_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            CreateFakeAnalyzerScripts(analyzerRoot, manifest.RetainedAnalyzers);

            var result = await RunAnalyzeRetainedAsync(
                repoRoot,
                tempRoot,
                new Dictionary<string, string>
                {
                    ["NLINK_SCREENSHARE_OPS_ANALYZER_ROOT"] = analyzerRoot
                });
            Assert.True(
                result.ExitCode == 0,
                $"Expected retained analyzer chain to succeed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var orderPath = Path.Combine(tempRoot, "analyzer-order.txt");
            Assert.True(File.Exists(orderPath), $"Expected analyzer order file: {orderPath}");
            Assert.Equal(ExpectedRetainedAnalyzerScripts, File.ReadAllLines(orderPath));

            var report = ReadVerdictReport(tempRoot);
            Assert.Equal("pass", report["operator_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ScreenShareOpsAnalyzeRetained_StopsWhenManifestAnalyzerFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var manifest = LoadRetainedAnalyzerManifest(repoRoot);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-chain", Guid.NewGuid().ToString("N"));
        var analyzerRoot = Path.Combine(tempRoot, "fake-analyzers");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(analyzerRoot);

        try
        {
            CreateCompleteArtifact(
                tempRoot,
                "pass",
                "no_material_latency_regression",
                "1",
                "0",
                "0",
                "steady_external_delivery_latency");
            CreateFakeAnalyzerScripts(analyzerRoot, manifest.RetainedAnalyzers);

            var failingAnalyzer = manifest.RetainedAnalyzers[3].Script;
            var result = await RunAnalyzeRetainedAsync(
                repoRoot,
                tempRoot,
                new Dictionary<string, string>
                {
                    ["NLINK_SCREENSHARE_OPS_ANALYZER_ROOT"] = analyzerRoot,
                    ["NLINK_SCREENSHARE_OPS_FAIL_ANALYZER"] = failingAnalyzer
                });
            Assert.Equal(23, result.ExitCode);

            var orderPath = Path.Combine(tempRoot, "analyzer-order.txt");
            Assert.True(File.Exists(orderPath), $"Expected analyzer order file: {orderPath}");
            Assert.Equal(ExpectedRetainedAnalyzerScripts.Take(4), File.ReadAllLines(orderPath));
            Assert.False(File.Exists(Path.Combine(tempRoot, "screenshare-operator-verdict.txt")));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string FindRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static RetainedAnalyzerManifest LoadRetainedAnalyzerManifest(string repoRoot)
    {
        var manifestPath = Path.Combine(repoRoot, RetainedAnalyzerManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(manifestPath), $"Expected retained analyzer manifest: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<RetainedAnalyzerManifest>(File.ReadAllText(manifestPath));
        Assert.NotNull(manifest);
        Assert.NotNull(manifest.RetainedAnalyzers);
        Assert.NotNull(manifest.ExternalTransportClassifications);
        return manifest;
    }

    private static (string Stage, string FileName)[] GetRetainedClassificationReports(RetainedAnalyzerManifest manifest)
    {
        return manifest.RetainedAnalyzers
            .Where(analyzer => !string.IsNullOrWhiteSpace(analyzer.ClassificationStage))
            .Select(analyzer => (analyzer.ClassificationStage, analyzer.Report))
            .ToArray();
    }

    private static void CreateCompleteArtifact(
        string artifactDir,
        string behaviorFirstGateStatus,
        string regressionClassification,
        string effectiveMediaPlaneActive,
        string steadyStateUsedControlFallback,
        string recoveryCompletionAccountingMismatch,
        string deepestClassification)
    {
        File.WriteAllLines(
            Path.Combine(artifactDir, "stability-gates-summary.txt"),
            ["behavior_first_gate_status=" + behaviorFirstGateStatus]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "latency-regression-analysis.txt"),
            ["regression_classification=" + regressionClassification]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "transport-mode-summary.txt"),
            [
                "effective_media_plane_active=" + effectiveMediaPlaneActive,
                "steady_state_used_control_fallback=" + steadyStateUsedControlFallback
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "recovery-burst-summary.txt"),
            ["recovery_completion_accounting_mismatch=" + recoveryCompletionAccountingMismatch]);

        var classificationReports = GetRetainedClassificationReports(LoadRetainedAnalyzerManifest(FindRepoRoot()));
        for (var index = 0; index < classificationReports.Length; index++)
        {
            var (_, fileName) = classificationReports[index];
            var classification = index == classificationReports.Length - 1
                ? deepestClassification
                : "diagnostic_stage_latency";
            File.WriteAllLines(
                Path.Combine(artifactDir, fileName),
                [
                    "classification=" + classification,
                    "smallest_next_fix_area=test fixture"
                ]);
        }
    }

    private static void CreateNoSessionArtifact(string artifactDir)
    {
        CreateCompleteArtifact(
            artifactDir,
            "pass",
            "invalid_no_screenshare_session",
            "-1",
            "-1",
            "0",
            "steady_external_delivery_latency");

        File.WriteAllLines(
            Path.Combine(artifactDir, "stability-gates-summary.txt"),
            [
                "behavior_first_gate_status=pass",
                "current_no_screenshare_session=1"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "latency-regression-analysis.txt"),
            [
                "comparison_status=invalid_no_screenshare_session",
                "regression_classification=invalid_no_screenshare_session",
                "smallest_next_fix_area=setup/connect before screenshare"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "transport-mode-summary.txt"),
            [
                "effective_media_plane_active=-1",
                "steady_state_used_control_fallback=-1",
                "media_plane_frames_sent=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-quality-summary.txt"),
            [
                "visible_apply_ratio=-1",
                "helper_apply_ms_avg=-1",
                "helper_apply_ms_p95=-1",
                "baseline_established=0",
                "baseline_capture_to_render_ms=-1",
                "reassembler_loss_count=0",
                "gap_count=0",
                "resync_count=0",
                "dominant_helper_admission_reject_reason=(none)",
                "dominant_helper_pressure_blocker=(none)"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-pressure-summary.txt"),
            [
                "baseline_established=0",
                "baseline_reseed_in_progress=0",
                "baseline_frozen_due_to_stall_count=0",
                "baseline_reseed_after_recovery_count=0",
                "cadence_stall_window_count=0",
                "cadence_stall_trigger_count=0",
                "actionable_high_frame_age_count=0",
                "dominant_helper_pressure_blocker=(none)"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "health-snapshot-summary.txt"),
            [
                "sender_operating_state=normal",
                "sender_guard_state=none",
                "helper_session_phase=no_visible_baseline",
                "helper_recovery_mechanism=none",
                "dominant_loss_class=benign_stale_cleanup",
                "dominant_pressure_blocker=none",
                "dominant_trouble_domain=none",
                "recovery_active=0",
                "baseline_established=0",
                "steady_visible_progress_active=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "bridge-media-send-summary.txt"),
            [
                "frames_sent=0",
                "send_failures=0",
                "queue_drops=0",
                "queue_depth=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "quality-presentation-summary.txt"),
            [
                "active_encode_target_width=-1",
                "active_encode_target_height=-1",
                "active_encode_target_fps=-1",
                "encoder_profile=(none)",
                "sender_freshness_mode=(none)",
                "sender_operating_state=normal",
                "helper_surface_interpolation_mode=(none)"
            ]);
    }

    private static void CreateLowFpsScenarioArtifacts(string artifactDir, string scenario)
    {
        var activeTargetFps = 5;
        var avgApplyIntervalMs = 180.0;
        var normalCount = 0;
        var reducedCount = 4;
        var catchUpCount = 1;
        var latestMode = "reduced";
        var remotePressureMode = "none";
        var remotePressureReason = "healthy";
        var helperSessionPhase = "visible_stable";
        var helperRecoveryMechanism = "none";
        var senderGuardState = "none";
        var recoveryActive = 0;
        var steadyVisibleProgressActive = 1;
        var dominantPressureBlocker = "none";
        var dominantTroubleDomain = "sender";
        var helperPressureBlocker = "none";
        var visibleApplyRatio = "1";
        var gapCount = "0";
        var resyncCount = "0";
        var admissionRejectReason = "none";
        var pendingVisibleRecoveryCount = "0";
        var recoveryLockTimeMs = "0";
        var promotionHelperPressureTicks = "0";
        var promotionRecoveryLockTicks = "0";
        var promotionCaptureAgeTicks = "0";
        var promotionEncodeBudgetTicks = "0";
        var promotionEncodeSoftSpikeCount = "0";
        var blockedByEncodeBudget = "0";
        var blockedByEncodeBudgetAlone = "0";
        var healthyTickResetReasonCounts = "(none)";
        var postReceiptBlockerSuppressedCount = "0";
        var networkResidualMs = "0";
        var includeRecentEntries = true;

        switch (scenario)
        {
            case "external":
                remotePressureMode = "reduce_fps";
                remotePressureReason = "high_frame_age";
                networkResidualMs = "245";
                break;
            case "external_resolved_helper_history":
                remotePressureMode = "reduce_fps";
                remotePressureReason = "high_frame_age";
                networkResidualMs = "245";
                gapCount = "5";
                resyncCount = "4";
                recoveryLockTimeMs = "125";
                promotionHelperPressureTicks = "12";
                helperPressureBlocker = "high_frame_age";
                break;
            case "external_visible_stable_normal_low_fps":
                activeTargetFps = 8;
                avgApplyIntervalMs = 171.4;
                normalCount = 2;
                reducedCount = 0;
                catchUpCount = 0;
                latestMode = "normal";
                remotePressureMode = "none";
                remotePressureReason = "healthy";
                networkResidualMs = "143";
                gapCount = "8";
                resyncCount = "5";
                pendingVisibleRecoveryCount = "8";
                promotionHelperPressureTicks = "13";
                promotionRecoveryLockTicks = "4";
                promotionCaptureAgeTicks = "6";
                helperPressureBlocker = "slow_apply_cadence";
                dominantPressureBlocker = "helper_pressure";
                break;
            case "external_visible_stable_low_apply_ratio":
                activeTargetFps = 3;
                avgApplyIntervalMs = 343.8;
                normalCount = 0;
                reducedCount = 3;
                catchUpCount = 11;
                latestMode = "catch_up";
                remotePressureMode = "catch_up_only";
                remotePressureReason = "bridge_health";
                networkResidualMs = "324";
                visibleApplyRatio = "0.91";
                gapCount = "6";
                resyncCount = "5";
                pendingVisibleRecoveryCount = "7";
                promotionHelperPressureTicks = "15";
                helperPressureBlocker = "none";
                dominantPressureBlocker = "helper_pressure";
                break;
            case "external_visible_stable_stale_continuity_reason":
                activeTargetFps = 8;
                avgApplyIntervalMs = 154.5;
                normalCount = 2;
                reducedCount = 0;
                catchUpCount = 0;
                latestMode = "normal";
                remotePressureMode = "none";
                remotePressureReason = "continuity_loss";
                networkResidualMs = "178";
                visibleApplyRatio = "0.93";
                gapCount = "6";
                resyncCount = "3";
                promotionHelperPressureTicks = "3";
                promotionRecoveryLockTicks = "3";
                promotionCaptureAgeTicks = "2";
                promotionEncodeBudgetTicks = "3";
                promotionEncodeSoftSpikeCount = "3";
                helperPressureBlocker = "none";
                dominantPressureBlocker = "capture_age";
                dominantTroubleDomain = "none";
                break;
            case "resolved_corridor_stale_health":
                activeTargetFps = 8;
                avgApplyIntervalMs = 142.6;
                normalCount = 4;
                reducedCount = 0;
                catchUpCount = 0;
                latestMode = "normal";
                networkResidualMs = "143";
                helperSessionPhase = "recovering";
                helperRecoveryMechanism = "recovery_corridor";
                senderGuardState = "recovery_locked";
                recoveryActive = 1;
                steadyVisibleProgressActive = 1;
                dominantPressureBlocker = "transition_grace";
                dominantTroubleDomain = "helper";
                helperPressureBlocker = "none";
                visibleApplyRatio = "1";
                gapCount = "1";
                resyncCount = "1";
                pendingVisibleRecoveryCount = "2";
                recoveryLockTimeMs = "15656";
                break;
            case "helper_recovery":
                helperSessionPhase = "recovering";
                helperRecoveryMechanism = "waiting_for_recovery_keyframe";
                senderGuardState = "recovery_locked";
                recoveryActive = 1;
                steadyVisibleProgressActive = 0;
                dominantPressureBlocker = "helper_pressure";
                dominantTroubleDomain = "helper";
                helperPressureBlocker = "high_frame_age";
                visibleApplyRatio = "0.93";
                gapCount = "2";
                resyncCount = "1";
                admissionRejectReason = "waiting_for_recovery_keyframe";
                recoveryLockTimeMs = "1245";
                promotionHelperPressureTicks = "12";
                promotionRecoveryLockTicks = "8";
                break;
            case "helper_cadence":
                activeTargetFps = 8;
                avgApplyIntervalMs = 250.0;
                remotePressureMode = "reduce_fps";
                remotePressureReason = "slow_apply_cadence";
                helperPressureBlocker = "slow_apply_cadence";
                break;
            case "sender_budget":
                promotionCaptureAgeTicks = "5";
                promotionEncodeBudgetTicks = "3";
                promotionEncodeSoftSpikeCount = "2";
                blockedByEncodeBudget = "1";
                break;
            case "policy_hysteresis":
                promotionRecoveryLockTicks = "3";
                healthyTickResetReasonCounts = "recovery_lock_active:1";
                postReceiptBlockerSuppressedCount = "2";
                break;
            case "healthy":
                activeTargetFps = 8;
                avgApplyIntervalMs = 125.0;
                normalCount = 5;
                reducedCount = 0;
                catchUpCount = 0;
                latestMode = "normal";
                break;
            case "healthy_no_recent_entries":
                activeTargetFps = 8;
                avgApplyIntervalMs = 125.0;
                normalCount = 5;
                reducedCount = 0;
                catchUpCount = 0;
                latestMode = "normal";
                includeRecentEntries = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown low-FPS test scenario.");
        }

        var qualityHelperSessionPhase = scenario == "resolved_corridor_stale_health"
            ? "visible_stable"
            : helperSessionPhase;
        var qualityHelperRecoveryMechanism = scenario == "resolved_corridor_stale_health"
            ? "none"
            : helperRecoveryMechanism;
        var recoveryWindowActive = scenario == "resolved_corridor_stale_health" ? "0" : "0";
        var recoveryProgressCorridorSuccessCount = scenario == "resolved_corridor_stale_health" ? "2" : "0";

        File.WriteAllLines(
            Path.Combine(artifactDir, "quality-presentation-summary.txt"),
            [
                "active_encode_target_width=1280",
                "active_encode_target_height=720",
                "active_encode_target_bitrate=3000000",
                "active_encode_target_fps=" + activeTargetFps.ToString(),
                "encoder_profile=" + latestMode,
                "sender_freshness_mode=" + latestMode,
                "sender_operating_state=" + latestMode,
                "effective_quality_preset=text_first_1x",
                "capture_scale=1",
                "normal_mode_summary_count=" + normalCount.ToString(),
                "reduced_mode_summary_count=" + reducedCount.ToString(),
                "catch_up_mode_summary_count=" + catchUpCount.ToString(),
                "helper_surface_interpolation_mode=high_quality",
                "helper_surface_scale_ratio=1.552",
                "",
                "freshness_summary_lines:",
                "[2026-04-24 14:00:00Z] [INFO] [ScreenShareTransport] event=screenshare_freshness_summary; sender_freshness_mode=normal; remote_pressure_mode=none; active_encode_target_fps=8",
                "[2026-04-24 14:00:02Z] [INFO] [ScreenShareTransport] event=screenshare_freshness_summary; sender_freshness_mode=" + latestMode + "; remote_pressure_mode=" + remotePressureMode + "; active_encode_target_fps=" + activeTargetFps.ToString()
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-quality-summary.txt"),
            [
                "visible_apply_ratio=" + visibleApplyRatio,
                "avg_decode_complete_to_visible_apply_ms=1.0",
                "avg_ui_post_apply_ms=0.2",
                "gap_count=" + gapCount,
                "resync_count=" + resyncCount,
                "dominant_helper_admission_reject_reason=" + admissionRejectReason,
                "recovery_keyframe_pending_visible_apply_count=" + pendingVisibleRecoveryCount,
                "dominant_helper_pressure_blocker=" + helperPressureBlocker,
                "worst_epoch_recovery_lock_time_ms=" + recoveryLockTimeMs,
                "helper_session_phase=" + qualityHelperSessionPhase,
                "helper_recovery_mechanism=" + qualityHelperRecoveryMechanism,
                "recovery_window_active=" + recoveryWindowActive,
                "recovery_progress_corridor_success_count=" + recoveryProgressCorridorSuccessCount,
                "pre_candidate_gap_tail_emitted_to_viewer_count=0",
                "actionable_late_fragment_count=0",
                "",
                "helper_quality_summary_lines:",
                "[2026-04-24 14:00:02Z] [INFO] [ScreenShare] event=screenshare_helper_quality_summary; helper_session_phase=" + qualityHelperSessionPhase + "; helper_recovery_mechanism=" + qualityHelperRecoveryMechanism + "; visible_apply_ratio=" + visibleApplyRatio + "; avg_apply_interval_ms=" + avgApplyIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; avg_decode_complete_to_visible_apply_ms=1.0; avg_ui_post_apply_ms=0.2; gap_count=" + gapCount + "; resync_count=" + resyncCount + "; dominant_helper_admission_reject_reason=" + admissionRejectReason + "; recovery_window_active=" + recoveryWindowActive + "; recovery_progress_corridor_success_count=" + recoveryProgressCorridorSuccessCount + "; pre_candidate_gap_tail_emitted_to_viewer_count=0; actionable_late_fragment_count=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-pressure-summary.txt"),
            [
                "dominant_helper_pressure_blocker=" + helperPressureBlocker,
                "worst_epoch_recovery_lock_time_ms=" + recoveryLockTimeMs,
                "actionable_high_frame_age_count=0",
                "cadence_stall_window_count=0",
                "cadence_stall_trigger_count=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "health-snapshot-summary.txt"),
            [
                "sender_operating_state=" + latestMode,
                "sender_guard_state=" + senderGuardState,
                "helper_session_phase=" + helperSessionPhase,
                "helper_recovery_mechanism=" + helperRecoveryMechanism,
                "dominant_pressure_blocker=" + dominantPressureBlocker,
                "dominant_trouble_domain=" + dominantTroubleDomain,
                "recovery_active=" + recoveryActive.ToString(),
                "steady_visible_progress_active=" + steadyVisibleProgressActive.ToString()
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "reduced-promotion-summary.txt"),
            [
                "promotion_blocker_helper_pressure_ticks=" + promotionHelperPressureTicks,
                "promotion_blocker_recovery_lock_ticks=" + promotionRecoveryLockTicks,
                "promotion_blocker_capture_age_ticks=" + promotionCaptureAgeTicks,
                "promotion_blocker_encode_budget_ticks=" + promotionEncodeBudgetTicks,
                "promotion_blocker_transition_grace_ticks=0",
                "promotion_encode_soft_spike_count=" + promotionEncodeSoftSpikeCount,
                "blocked_by_encode_budget=" + blockedByEncodeBudget,
                "blocked_by_encode_budget_alone=" + blockedByEncodeBudgetAlone,
                "healthy_tick_reset_reason_counts=" + healthyTickResetReasonCounts,
                "post_receipt_blocker_suppressed_count=" + postReceiptBlockerSuppressedCount,
                "last_post_receipt_blocker_suppressed_set=(none)",
                includeRecentEntries
                    ? "recent_entries=140000|h=0>0|blockers=none|reset=none|cap=100/250|enc=60/70|pressure=" + remotePressureMode + "/" + remotePressureReason + "|apply=1|steady=1|head=1|gap_apply=1|nmi=0|stale=0|bridge=none:0|rg=0|qe=0|sup=0|lock=0|grace=0"
                    : "recent_entries=(none)"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-external-delivery-analysis.txt"),
            [
                "classification=network_delivery_latency",
                "smallest_next_fix_area=external NKN/network receive backlog work",
                "candidate_network_delivery_residual_ms=" + networkResidualMs,
                "candidate_local_sender_delta_ms=0",
                "candidate_queue_depth=0",
                "candidate_queue_drops=0",
                "candidate_send_failures=0"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-external-transport-health-analysis.txt"),
            [
                "classification=steady_external_delivery_latency",
                "smallest_next_fix_area=external NKN/network receive backlog work"
            ]);
    }

    private static void CreateExternalTopologySourceArtifacts(
        string artifactDir,
        string profile,
        string selectedRpcKey,
        int mediaSubClients,
        int socketMedianMs,
        int socketP95Ms,
        int queueDepth,
        int queueDrops,
        int sendFailures)
    {
        File.WriteAllLines(
            Path.Combine(artifactDir, "bridge-transport-health-summary.txt"),
            [
                "selected_rpc=https://example.invalid/rpc",
                "selected_rpc_key=" + selectedRpcKey,
                "selected_rpc_stage=initial",
                "disconnect_count_since_last=0",
                "connect_failed_count_since_last=0",
                "ws_error_count_since_last=0",
                "rpc_fallback_attempt_count_since_last=0",
                "control_subclients=4",
                "media_subclients=" + mediaSubClients,
                "bulk_subclients=4",
                "unique_selected_rpc_count=1",
                "external_topology_profile=" + profile
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-external-delivery-analysis.txt"),
            [
                "classification=network_delivery_latency",
                "candidate_envelope_send_to_socket_data_event_emitted_median_ms=" + socketMedianMs,
                "candidate_network_delivery_residual_ms=" + socketMedianMs,
                "candidate_local_sender_delta_ms=0",
                "candidate_queue_depth=" + queueDepth,
                "candidate_queue_drops=" + queueDrops,
                "candidate_send_failures=" + sendFailures
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-socket-receive-analysis.txt"),
            [
                "classification=external_receive_latency",
                "candidate_envelope_send_to_socket_data_event_emitted_median_ms=" + socketMedianMs,
                "candidate_envelope_send_to_socket_data_event_emitted_p95_ms=" + socketP95Ms
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "helper-external-transport-health-analysis.txt"),
            [
                "classification=steady_external_delivery_latency"
            ]);
        File.WriteAllLines(
            Path.Combine(artifactDir, "low-fps-catch-up-summary.txt"),
            [
                "classification=external_delivery_driven_catch_up",
                "effective_apply_fps=6.5",
                "sender_mode_counts=normal:2,reduced:0,catch_up:0"
            ]);
    }

    private static string CreateExternalTopologySummaryArtifact(
        string root,
        string name,
        string profile,
        string classification,
        int socketMedianMs,
        int socketP95Ms,
        int queueDepth = 0)
    {
        var artifactDir = Path.Combine(root, name);
        Directory.CreateDirectory(artifactDir);
        File.WriteAllLines(
            Path.Combine(artifactDir, "external-topology-summary.txt"),
            [
                "external_topology_profile=" + profile,
                "external_topology_classification=" + classification,
                "external_topology_next_action=test",
                "selected_rpc_key=bb9d9798",
                "selected_rpc_stage=initial",
                "media_subclients=" + (profile == "MediaFanout8" ? "8" : "4"),
                "socket_receive_median_ms=" + socketMedianMs,
                "socket_receive_p95_ms=" + socketP95Ms,
                "local_sender_delta_ms=0",
                "queue_depth=" + queueDepth,
                "queue_drops=0",
                "send_failures=0",
                "low_fps_catch_up_classification=external_delivery_driven_catch_up",
                "effective_apply_fps=6.5",
                "sender_mode_counts=normal:2,reduced:0,catch_up:0"
            ]);

        return artifactDir;
    }

    private static void CreateFakeAnalyzerScripts(string analyzerRoot, IReadOnlyList<RetainedAnalyzerEntry> analyzers)
    {
        foreach (var analyzer in analyzers)
        {
            File.WriteAllText(
                Path.Combine(analyzerRoot, analyzer.Script),
                BuildFakeAnalyzerScript(analyzer.Script),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static async Task<ScriptResult> RunVerdictOnlyAsync(string repoRoot, string artifactDir)
    {
        return await RunAnalyzeRetainedAsync(
            repoRoot,
            artifactDir,
            new Dictionary<string, string>
            {
                ["NLINK_SCREENSHARE_OPS_VERDICT_ONLY"] = "1"
            });
    }

    private static async Task<ScriptResult> RunAnalyzeRetainedAsync(
        string repoRoot,
        string artifactDir,
        IReadOnlyDictionary<string, string> environment)
    {
        var arguments = new[]
        {
            "-Mode",
            "AnalyzeRetained",
            "-ArtifactDir",
            artifactDir
        };
        return await RunScreenShareOpsAsync(repoRoot, arguments, environment);
    }

    private static async Task<ScriptResult> RunScreenShareOpsAsync(
        string repoRoot,
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
                WorkingDirectory = repoRoot,
            }
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1"));
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

    private static async Task<ScriptResult> RunPowerShellScriptAsync(
        string repoRoot,
        string scriptPath,
        IReadOnlyList<string> arguments)
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
                WorkingDirectory = repoRoot,
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

    private static async Task<ScriptResult> RunParserAsync(string scriptPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-screenshare-ops-parse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var parserHarnessPath = Path.Combine(tempRoot, "parse-screenshare-ops.ps1");
            File.WriteAllText(parserHarnessPath, BuildParserHarness(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

    private static Dictionary<string, string> ReadVerdictReport(string artifactDir)
    {
        return ReadArtifactReport(artifactDir, "screenshare-operator-verdict.txt");
    }

    private static Dictionary<string, string> ReadArtifactReport(string artifactDir, string fileName)
    {
        var reportPath = Path.Combine(artifactDir, fileName);
        Assert.True(File.Exists(reportPath), $"Expected artifact report: {reportPath}");

        return File.ReadAllLines(reportPath)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static string[] ExtractTopLevelPowerShellParameterNames(string scriptText)
    {
        var match = Regex.Match(scriptText, @"(?s)^param\((?<body>.*?)\)\s*Set-StrictMode");
        Assert.True(match.Success, "Could not find top-level param block before Set-StrictMode.");

        return Regex.Matches(match.Groups["body"].Value, @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["name"].Value)
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

    private static string BuildFakeAnalyzerScript(string scriptName)
    {
        return $$"""
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateArtifactDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Content -LiteralPath (Join-Path $CandidateArtifactDir 'analyzer-order.txt') -Value '{{scriptName}}'
if ($env:NLINK_SCREENSHARE_OPS_FAIL_ANALYZER -eq '{{scriptName}}') {
    exit 23
}

exit 0
""";
    }

    private static string BuildResolvedFollowerGateHarness(string gatePath, string artifactDir)
    {
        var escapedGatePath = gatePath.Replace("'", "''", StringComparison.Ordinal);
        var escapedArtifactDir = artifactDir.Replace("'", "''", StringComparison.Ordinal);
        return """
function New-BaselineComparisonReport {
    param(
        [string]$Label,
        $CurrentMetrics,
        $BaselineMetrics
    )

    return @("comparison=$Label")
}

. '__GATE_PATH__'
$summary = [pscustomobject]@{
    LatestRecoveryOwnerReplacedBeforeAckCount = 0
    LatestHelperPreCandidateGapTailEmittedToViewerCount = 0
    LatestHelperActionableLateFragmentCount = 0
    LatestHelperRecoveryOwnerReplacedCount = 1
    LatestHelperRecoveryProgressCorridorSuccessCount = 4
    LatestHelperRecoveryWindowActive = 1
    LatestHealthHelperSessionPhase = 'visible_stable'
    LatestHealthHelperRecoveryMechanism = 'none'
    LatestHealthRecoveryActive = 0
    LatestHelperRecoveryRunwayOverflowRejectCount = 2
    LatestHelperStartupCorridorReleaseCount = 0
    LatestHelperRecoveryFollowerWindowBufferedCount = 3
    LatestRecoveryCompletionAccountingMismatch = 0
    RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount = 0
    LatestHelperBridgeHealthActionableWithoutQueueOrDropCount = 0
    DominantReassemblerRootCause = 'future_tail_pruned_while_gap_active'
    LatestRecoveryPostAckHoldStartedCount = 1
    LatestRecoveryPostAckHoldExpiredCount = 1
}
$current = @{
    latency_proxy_name = 'helper_apply_ms_avg'
    latency_proxy_ms = 324
    helper_apply_ms_avg = 324
    no_screenshare_session = 0
    no_screenshare_frames_sent = 20
    no_screenshare_media_plane_frames_sent = 331
    no_screenshare_helper_apply_sample_count = 171
    no_screenshare_helper_session_phase = 'visible_stable'
    visible_apply_ratio = 0.98
    reassembler_loss_count = 15
}
$result = Write-StabilizationArtifacts -ArtifactDir '__ARTIFACT_DIR__' -Summary $summary -CurrentMetrics $current -StrongBaselineMetrics $null -SafeBaselineMetrics $null
if ($result.GateStatus -ne 'pass') {
    throw "expected pass, got $($result.GateStatus): $($result.InvariantFailures -join ',')"
}
"""
            .Replace("__GATE_PATH__", escapedGatePath, StringComparison.Ordinal)
            .Replace("__ARTIFACT_DIR__", escapedArtifactDir, StringComparison.Ordinal);
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

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RetainedAnalyzerManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("retained_analyzers")]
        public RetainedAnalyzerEntry[] RetainedAnalyzers { get; init; } = [];

        [JsonPropertyName("external_transport_classifications")]
        public string[] ExternalTransportClassifications { get; init; } = [];
    }

    private sealed class RetainedAnalyzerEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("script")]
        public string Script { get; init; } = "";

        [JsonPropertyName("report")]
        public string Report { get; init; } = "";

        [JsonPropertyName("classification_stage")]
        public string ClassificationStage { get; init; } = "";
    }
}
