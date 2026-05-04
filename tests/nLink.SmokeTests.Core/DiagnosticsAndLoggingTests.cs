using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Configuration;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class DiagnosticsAndLoggingTests : CoreSmokeTestsBase
{
[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Diagnostics_CopyExport_IncludesRuntimeBasics_AndNoPayloadOrChatHistory()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var previousInviteMode = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar);
        var previousLegacyModeOverride = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar);
        var previousInviteSigningKey = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar);
        var previousLegacyInviteOverride = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar);
        var previousUnboundInviteOverride = Environment.GetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar);
        var previousSeqGate = Environment.GetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE");
        var previousPreflightRpc = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var previousScreenShareMaxFps = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_MAX_FPS");
        var previousScreenShareTransportMaxFps = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS");
        var previousScreenShareTransportAutotune = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE");
        var previousScreenShareScale = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE");
        try
        {
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
            SessionTimeline.Clear();
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Disconnected", "timeout; session_id=session-123; helper_identity=nlink-helper-123");
            NknRuntimeDiagnostics.SetLastError("event=failure; session_id=session-123; source=nlink-source-123");
            NknRuntimeDiagnostics.SetLastDisconnectReason("peer_id=nlink-peer-123; reply_to=req-123");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, null);
            Environment.SetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar, null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_MAX_FPS", null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS", null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE", null);
            var config = TransportRuntimeConfig.Select();
            var inviteSecurity = InviteSecurityDiagnostics.Snapshot();
            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
            runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test");
            runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "test");
            runtime.TryTransitionTransportStateForTests(TransportState.Failed, "test");
            await runtime.FailAsync(
                TransportFailure.Create(TransportFailureCategory.HandshakeTimeout, "Timed out", exceptionType: nameof(TimeoutException), rawError: "timeout", isTransient: true),
                "No response yet.");
            var metrics = new MetricsRegistry();
            metrics.Counter("transport_connect_attempts_total", transport: "NKN", scenario: "A").Inc(2);
            metrics.Counter("transport_connect_success_total", transport: "NKN", scenario: "A").Inc(1);
            metrics.Histogram("transport_connect_duration_ms", transport: "NKN", scenario: "A").Observe(10);
            metrics.Histogram("transport_connect_duration_ms", transport: "NKN", scenario: "A").Observe(30);
            var vm = new DiagnosticsPageViewModel(static () => { }, config, sessionRuntime: runtime, metricsRegistry: metrics);

            string? copied = null;
            vm.CopyReliabilityLogRequested += (_, text) => copied = text;

            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.False(string.IsNullOrWhiteSpace(copied));
            Assert.Contains("App version:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS:", copied!, StringComparison.Ordinal);
            Assert.Contains("Process architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("Bridge RID:", copied!, StringComparison.Ordinal);
            Assert.Contains("current_state:", copied!, StringComparison.Ordinal);
            Assert.Contains("session_ui_state:", copied!, StringComparison.Ordinal);
            Assert.Contains("attempt:", copied!, StringComparison.Ordinal);
            Assert.Contains("runtime_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("authorization_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_authorization_denial_reason:", copied!, StringComparison.Ordinal);
            Assert.Contains("session_security_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("remote_control_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("file_transfer_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains("file_transfer_inbound_state:", copied!, StringComparison.Ordinal);
            Assert.Contains("file_transfer_outbound_state:", copied!, StringComparison.Ordinal);
            Assert.Contains("file_transfer_last_failure_code:", copied!, StringComparison.Ordinal);
            Assert.Contains("file_transfer_last_saved_path:", copied!, StringComparison.Ordinal);
            Assert.Contains("Transport:", copied!, StringComparison.Ordinal);
            Assert.Contains("Forced by environment:", copied!, StringComparison.Ordinal);
            Assert.Contains("bridge_process_status:", copied!, StringComparison.Ordinal);
            Assert.Contains("bridge_manifest_summary:", copied!, StringComparison.Ordinal);
            Assert.Contains($"invite_security_mode: {inviteSecurity.Mode}", copied!, StringComparison.Ordinal);
            Assert.Contains($"invite_signing_configuration: {inviteSecurity.SigningConfiguration}", copied!, StringComparison.Ordinal);
            Assert.Contains($"invite_public_flow: {inviteSecurity.PublicInviteFlow}", copied!, StringComparison.Ordinal);
            Assert.Contains($"invite_security_release_ready: {(inviteSecurity.ReleaseReady ? "Yes" : "No")}", copied!, StringComparison.Ordinal);
            Assert.Contains($"invite_security_warning: {inviteSecurity.Warning}", copied!, StringComparison.Ordinal);
            Assert.Contains("security_relevant_overrides: none", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_outbound_busy_drops:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_messages_sent:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_payload_bytes_sent:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_bridge_bytes_sent:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_capture_presets:", copied!, StringComparison.Ordinal);
            Assert.Contains("balanced_default: capture_fps=15, transport_fps=8, scale=1.00", copied!, StringComparison.Ordinal);
            Assert.Contains("high_quality: capture_fps=20, transport_fps=12, scale=1.00", copied!, StringComparison.Ordinal);
            Assert.Contains("high_performance: capture_fps=10, transport_fps=6, scale=0.60", copied!, StringComparison.Ordinal);
            Assert.Contains("Screenshare evidence", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_evidence_status:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_operator_verdict:", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_next_operator_action:", copied!, StringComparison.Ordinal);
            Assert.Contains("high_priority_control_queue_overflows:", copied!, StringComparison.Ordinal);
            Assert.Contains("high_priority_control_rejected:", copied!, StringComparison.Ordinal);
            Assert.Contains("high_priority_control_coalesced:", copied!, StringComparison.Ordinal);
            Assert.Contains("high_priority_control_dropped_for_stop:", copied!, StringComparison.Ordinal);
            Assert.Contains("authoritative_connected_address:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_rejected_message:", copied!, StringComparison.Ordinal);
            Assert.Contains("helper_address_source:", copied!, StringComparison.Ordinal);
            Assert.Contains("helper_address_authoritative:", copied!, StringComparison.Ordinal);
            Assert.Contains("helper_verification_code_visible:", copied!, StringComparison.Ordinal);
            Assert.Contains("helper_identity_regenerated_count:", copied!, StringComparison.Ordinal);
            Assert.Contains("helper_identity_last_regenerated_utc:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_connect_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_handshake_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_bridge_start_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_failure_category:", copied!, StringComparison.Ordinal);
            Assert.Contains("last_failure_message:", copied!, StringComparison.Ordinal);
            Assert.Contains("Metrics snapshot", copied!, StringComparison.Ordinal);
            Assert.Contains("connect_attempts_total:", copied!, StringComparison.Ordinal);
            Assert.Contains("connect_success_rate_pct:", copied!, StringComparison.Ordinal);
            Assert.Contains("transport_connect_duration_ms:", copied!, StringComparison.Ordinal);
            Assert.Contains("Session timeline (last 30)", copied!, StringComparison.Ordinal);
            Assert.Contains("Started", copied!, StringComparison.Ordinal);
            Assert.Contains("Disconnected | timeout", copied!, StringComparison.Ordinal);

            Assert.DoesNotContain("payloadBase64", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NKN address:", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("last_bridge_message_source:", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hello from helper", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("session_id=session-123", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("helper_identity=nlink-helper-123", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source=nlink-source-123", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("peer_id=nlink-peer-123", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reply_to=req-123", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", copied!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastError("(none)");
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, previousInviteMode);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, previousLegacyModeOverride);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, previousInviteSigningKey);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, previousLegacyInviteOverride);
            Environment.SetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar, previousUnboundInviteOverride);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", previousSeqGate);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", previousPreflightRpc);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_MAX_FPS", previousScreenShareMaxFps);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS", previousScreenShareTransportMaxFps);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", previousScreenShareTransportAutotune);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE", previousScreenShareScale);
        }
    }

[Fact]
public void ScreenShareEvidenceLocator_ReportsNoneFound_WhenNoArtifactsExist()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-empty-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);

    try
    {
        var snapshot = new ScreenShareEvidenceLocator([tempRoot]).ReadLatest();

        Assert.Equal(ScreenShareEvidenceStatus.NoneFound, snapshot.Status);
        Assert.Equal("none_found", snapshot.StatusKey);
        Assert.Contains("screenshare_evidence_status: none_found", snapshot.ToReportText(), StringComparison.Ordinal);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void ScreenShareEvidenceLocator_ReportsArtifactWithoutVerdict_WhenLatestArtifactIsUnanalyzed()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-missing-verdict-" + Guid.NewGuid().ToString("N"));
    var artifactDir = Path.Combine(tempRoot, "20260423-010101");
    Directory.CreateDirectory(artifactDir);
    File.WriteAllText(Path.Combine(artifactDir, "stability-gates-summary.txt"), "behavior_first_gate_status=pass");

    try
    {
        var snapshot = new ScreenShareEvidenceLocator([tempRoot]).ReadLatest();

        Assert.Equal(ScreenShareEvidenceStatus.ArtifactWithoutVerdict, snapshot.Status);
        Assert.Equal("artifact_without_verdict", snapshot.StatusKey);
        Assert.Equal("20260423-010101", snapshot.ArtifactName);
        Assert.Equal("screenshare-operator-verdict.txt", snapshot.MissingRequiredInputs);
        Assert.Contains("AnalyzeRetained", snapshot.NextOperatorAction, StringComparison.Ordinal);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void ScreenShareEvidenceLocator_ParsesLatestVerdict_AndRedactsReportPaths()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-verdict-" + Guid.NewGuid().ToString("N"));
    var olderArtifact = Path.Combine(tempRoot, "20260423-010101");
    var latestArtifact = Path.Combine(tempRoot, "20260423-020202");
    Directory.CreateDirectory(olderArtifact);
    Directory.CreateDirectory(latestArtifact);
    File.WriteAllText(Path.Combine(olderArtifact, "stability-gates-summary.txt"), "behavior_first_gate_status=pass");
    WriteScreenShareVerdict(
        latestArtifact,
        "fail_live_transport_evidence",
        "Live transport remained outside the local runtime.",
        "Attach the artifact if support requests raw evidence.",
        "external_transport_health",
        "steady_external_delivery_latency");

    try
    {
        var snapshot = new ScreenShareEvidenceLocator([tempRoot]).ReadLatest();
        var report = snapshot.ToReportText();

        Assert.Equal(ScreenShareEvidenceStatus.VerdictAvailable, snapshot.Status);
        Assert.Equal("verdict_available", snapshot.StatusKey);
        Assert.Equal("20260423-020202", snapshot.ArtifactName);
        Assert.Equal("fail_live_transport_evidence", snapshot.OperatorVerdict);
        Assert.Equal("external_transport_health", snapshot.DeepestTrackBStage);
        Assert.Equal("steady_external_delivery_latency", snapshot.DeepestTrackBClassification);
        Assert.Contains("screenshare_artifact_dir: [REDACTED_PATH]/20260423-020202", report, StringComparison.Ordinal);
        Assert.Contains("screenshare_verdict_path: [REDACTED_PATH]/screenshare-operator-verdict.txt", report, StringComparison.Ordinal);
        Assert.DoesNotContain(tempRoot, report, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void ScreenShareEvidenceLocator_ReadLatest_DoesNotModifyArtifactFiles()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-readonly-" + Guid.NewGuid().ToString("N"));
    var artifactDir = Path.Combine(tempRoot, "20260423-025252");
    Directory.CreateDirectory(artifactDir);
    WriteScreenShareVerdict(
        artifactDir,
        "pass",
        "Read-only evidence was available.",
        "No action needed.",
        "external_transport_health",
        "steady_external_delivery_latency");
    var verdictPath = Path.Combine(artifactDir, "screenshare-operator-verdict.txt");
    var beforeEntries = Directory.GetFileSystemEntries(tempRoot, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(tempRoot, path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
    var beforeVerdictWriteUtc = File.GetLastWriteTimeUtc(verdictPath);

    try
    {
        var snapshot = new ScreenShareEvidenceLocator([tempRoot]).ReadLatest();
        var afterEntries = Directory.GetFileSystemEntries(tempRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(tempRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ScreenShareEvidenceStatus.VerdictAvailable, snapshot.Status);
        Assert.Equal(beforeEntries, afterEntries);
        Assert.Equal(beforeVerdictWriteUtc, File.GetLastWriteTimeUtc(verdictPath));
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void DiagnosticsCopy_IncludesScreenshareEvidenceSnapshot_AndRedactsArtifactPath()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-diagnostics-" + Guid.NewGuid().ToString("N"));
    var artifactDir = Path.Combine(tempRoot, "20260423-030303");
    Directory.CreateDirectory(artifactDir);
    WriteScreenShareVerdict(
        artifactDir,
        "fail_live_transport_evidence",
        "Live transport evidence was collected.",
        "Share copied diagnostics first.",
        "external_transport_health",
        "steady_external_delivery_latency");

    try
    {
        var config = TransportRuntimeConfig.Select();
        var vm = new DiagnosticsPageViewModel(
            new ScreenShareEvidenceLocator([tempRoot]),
            static () => { },
            config);

        var copied = vm.BuildDiagnosticsCopyTextForTests();

        Assert.Contains("Screenshare evidence", copied, StringComparison.Ordinal);
        Assert.Contains("screenshare_evidence_status: verdict_available", copied, StringComparison.Ordinal);
        Assert.Contains("screenshare_operator_verdict: fail_live_transport_evidence", copied, StringComparison.Ordinal);
        Assert.Contains("screenshare_deepest_classification: steady_external_delivery_latency", copied, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_PATH]/20260423-030303", copied, StringComparison.Ordinal);
        Assert.DoesNotContain(tempRoot, copied, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void ScreenShareEvidenceLocator_BuildsCompactSupportSummaries_FromRetainedFiles()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-compact-" + Guid.NewGuid().ToString("N"));
    var artifactDir = Path.Combine(tempRoot, "20260423-050505");
    Directory.CreateDirectory(artifactDir);
    WriteScreenShareVerdict(
        artifactDir,
        "pass",
        "Screenshare evidence is available.",
        "Continue support triage.",
        "external_transport_health",
        "steady_external_delivery_latency");
    File.WriteAllLines(
        Path.Combine(artifactDir, "quality-presentation-summary.txt"),
        [
            "active_encode_target_width=1440",
            "active_encode_target_height=810",
            "active_encode_target_bitrate=6000000",
            "active_encode_target_fps=8",
            "encoder_profile=normal",
            "sender_freshness_mode=normal",
            "effective_quality_preset=text_first_1x",
            "actual_encoded_displayable_fps=7.50",
            "raw_source_readback_fps=8.00",
            "sender_process_cpu_percent=15.2",
            "last_preprocess_duration_ms=37",
            "raw_source_gpu_scale_enabled=1",
            "preprocess_resize_path=direct_nv12",
            "cursor_delivery_mode=helper_overlay",
            "cursor_capture_desired_enabled=0",
            "cursor_capture_enabled=0",
            "cursor_capture_apply_status=applied",
        ]);
    File.WriteAllLines(
        Path.Combine(artifactDir, "helper-quality-summary.txt"),
        [
            "pre_candidate_gap_tail_emitted_to_viewer_count=0",
            "actionable_late_fragment_count=0",
            "h264_reference_taint_active=0",
            "h264_reference_quarantine_active=0",
        ]);
    File.WriteAllLines(
        Path.Combine(artifactDir, "external-topology-summary.txt"),
        [
            "external_topology_profile=Default",
            "selected_rpc_key=bb9d9798",
            "media_subclients=8",
            "external_topology_classification=external_delivery_candidate",
        ]);

    try
    {
        var snapshot = new ScreenShareEvidenceLocator([tempRoot]).ReadLatest();

        Assert.Contains("target=1440x810@8fps", snapshot.QualityProfileSummary, StringComparison.Ordinal);
        Assert.Contains("sender_cpu_pct=15.2", snapshot.PerformanceSummary, StringComparison.Ordinal);
        Assert.Contains("mode=helper_overlay", snapshot.CursorSummary, StringComparison.Ordinal);
        Assert.Contains("unsafe_tail=0", snapshot.VisualSafetySummary, StringComparison.Ordinal);
        Assert.Contains("media_subclients=8", snapshot.ExternalTopologySummary, StringComparison.Ordinal);
        Assert.Contains("screenshare_quality_profile:", snapshot.ToReportText(), StringComparison.Ordinal);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Fact]
public void DiagnosticsPageViewModel_UsesSupportFirstLabels_AndHidesEmptyBugReport()
{
    var config = CreateDevLocalTestConfig();
    using var vm = new DiagnosticsPageViewModel(static () => { }, config, linksConfig: new ShareMessageConfig(null));
    using var bugVm = new DiagnosticsPageViewModel(static () => { }, config, linksConfig: new ShareMessageConfig(null, "https://example.test/repo"));

    Assert.Equal("Diagnostics", vm.PageTitle);
    Assert.Contains("Support status", vm.PageSubtitle, StringComparison.Ordinal);
    Assert.False(vm.ShowReportBug);
    Assert.True(bugVm.ShowReportBug);
    Assert.Equal("(none)", vm.ScreenShareLiveProfileSummary);
    Assert.Contains("sender_cpu_pct=(none)", vm.ScreenShareLiveCpuSummary, StringComparison.Ordinal);
}

[Fact]
public void DiagnosticsPageViewModel_ScreenSharePresetCommands_UpdateDisplayedSummary()
{
    var previousMaxFps = Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareMaxFpsVariable);
    var previousTransportMaxFps = Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable);
    var previousScale = Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareScaleVariable);
    try
    {
        ScreenShareQualitySettings.ResetMigrationStateForTests();
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, "10");
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "8");
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareScaleVariable, "1");
        var config = CreateDevLocalTestConfig();
        var changed = new List<string?>();
        using var vm = new DiagnosticsPageViewModel(
            static () => { },
            config,
            linksConfig: new ShareMessageConfig(null),
            screenSharePresetPersistence: static (_, _, _, _) => { });
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("preset=Custom; capture_fps=10; transport_fps=8; scale=1; legacy_migrated=No", vm.AdvancedScreenShareSettingsSummary);
        Assert.True(vm.ShowScreenShareResetHint);

        vm.ApplyHighQualityScreenSharePresetCommand.Execute(null);

        Assert.Equal("preset=High quality; capture_fps=20; transport_fps=12; scale=1; legacy_migrated=No", vm.AdvancedScreenShareSettingsSummary);
        Assert.False(vm.ShowScreenShareResetHint);
        Assert.Contains(nameof(DiagnosticsPageViewModel.AdvancedScreenShareSettingsSummary), changed);
        Assert.Contains(nameof(DiagnosticsPageViewModel.ShowScreenShareResetHint), changed);
    }
    finally
    {
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, previousMaxFps);
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, previousTransportMaxFps);
        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareScaleVariable, previousScale);
        ScreenShareQualitySettings.ResetMigrationStateForTests();
    }
}

[Fact]
public void DiagnosticsPageView_KeepsPrimarySurfaceSupportFocused()
{
    var viewPath = FindFileUpwards(Path.Combine("src", "nLink.App", "Views", "DiagnosticsPageView.axaml"));
    var xaml = File.ReadAllText(viewPath);

    Assert.Contains("Copy diagnostics", xaml, StringComparison.Ordinal);
    Assert.Contains("Save Hang Report", xaml, StringComparison.Ordinal);
    Assert.Contains("Open logs folder", xaml, StringComparison.Ordinal);
    Assert.Contains("Screen share health", xaml, StringComparison.Ordinal);
    Assert.Contains("Advanced diagnostics", xaml, StringComparison.Ordinal);
    Assert.Contains("Balanced", xaml, StringComparison.Ordinal);
    Assert.Contains("High quality", xaml, StringComparison.Ordinal);
    Assert.Contains("High performance", xaml, StringComparison.Ordinal);
    Assert.DoesNotContain("Feature Flags", xaml, StringComparison.Ordinal);
    Assert.DoesNotContain("ScreenShare Capture Tuning", xaml, StringComparison.Ordinal);
    Assert.DoesNotContain("Apply CPU saver", xaml, StringComparison.Ordinal);
    Assert.DoesNotContain("Apply sharper text", xaml, StringComparison.Ordinal);
}

[Fact]
public void HangReport_WritesScreenshareEvidenceText()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-evidence-hang-" + Guid.NewGuid().ToString("N"));
    var artifactDir = Path.Combine(tempRoot, "20260423-040404");
    var hangRoot = Path.Combine(tempRoot, "hang");
    Directory.CreateDirectory(artifactDir);
    WriteScreenShareVerdict(
        artifactDir,
        "pass",
        "Screenshare evidence is green.",
        "Continue with normal support triage.",
        "external_transport_health",
        "steady_external_delivery_latency");

    try
    {
        var service = new HangReportService(
            new ScreenShareEvidenceLocator([tempRoot]),
            nowProvider: () => new DateTimeOffset(2026, 4, 23, 4, 4, 4, TimeSpan.Zero),
            hangArtifactsRootProvider: () => hangRoot);

        var result = service.Capture(HangReportTriggerKind.ManualDiagnostics, "test", diagnosticsTextOverride: "diag");
        var evidencePath = Path.Combine(result.FolderPath, "screenshare-evidence.txt");

        Assert.True(File.Exists(evidencePath), $"Expected hang-report screenshare evidence: {evidencePath}");
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains("screenshare_evidence_status: verdict_available", evidence, StringComparison.Ordinal);
        Assert.Contains("screenshare_operator_verdict: pass", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(tempRoot, evidence, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void Diagnostics_CopyExport_ReportsSecurityRelevantOverrides()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var previousSeqGate = Environment.GetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE");
        var previousSeqGateOptIn = Environment.GetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar);
        var previousPreflightRpc = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var previousScreenShareScale = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE");
        var previousScreenShareTransportAutotune = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE");

        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", "0");
            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, "1");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", "true");
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE", "0.6");
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "false");

            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);

            string? copied = null;
            vm.CopyReliabilityLogRequested += (_, text) => copied = text;

            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(copied);
            Assert.Contains("security_relevant_overrides:", copied!, StringComparison.Ordinal);
            Assert.Contains("remote_control_seq_gate=off", copied!, StringComparison.Ordinal);
            Assert.Contains("nkn_preflight_rpc=on", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_capture_scale=0.6", copied!, StringComparison.Ordinal);
            Assert.Contains("screenshare_transport_autotune=off", copied!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", previousSeqGate);
            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, previousSeqGateOptIn);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", previousPreflightRpc);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCALE", previousScreenShareScale);
            Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", previousScreenShareTransportAutotune);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void LocalOperationalLog_LogAppStart_WritesInviteSecurityStatus()
    {
        var previousInviteMode = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar);
        var previousLegacyModeOverride = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar);
        var previousInviteSigningKey = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar);
        var previousLegacyInviteOverride = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar);
        var previousUnboundInviteOverride = Environment.GetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, null);
            Environment.SetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var inviteSecurity = InviteSecurityDiagnostics.Snapshot();

            LocalOperationalLog.LogAppStart("0.0.0-invite-security-" + Guid.NewGuid().ToString("N"));

            var appended = string.Join(
                Environment.NewLine,
                File.ReadLines(LocalOperationalLog.LogFilePath).TakeLast(4));

            Assert.Contains(
                "event=invite_security_status;",
                appended,
                StringComparison.Ordinal);
            Assert.Contains(
                $"mode={inviteSecurity.Mode};",
                appended,
                StringComparison.Ordinal);
            Assert.Contains(
                $"signing={inviteSecurity.SigningConfiguration};",
                appended,
                StringComparison.Ordinal);
            Assert.Contains(
                $"release_ready={(inviteSecurity.ReleaseReady ? "yes" : "no")};",
                appended,
                StringComparison.Ordinal);
            Assert.Contains(
                $"warning={inviteSecurity.Warning}",
                appended,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, previousInviteMode);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, previousLegacyModeOverride);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, previousInviteSigningKey);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, previousLegacyInviteOverride);
            Environment.SetEnvironmentVariable(InviteSecurityDiagnostics.AllowInsecureUnboundPublicInvitesEnvVar, previousUnboundInviteOverride);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknSignalingTransport_InitializationLog_DoesNotContainKeyPath()
    {
        var previousKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var previousIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
        var previousConsoleOut = Console.Out;
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-nkn-init-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var keyPath = Path.Combine(tempDir, "identity.json");

        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        try
        {
            var uniqueIdentifier = "nkn-init-log-test-" + Guid.NewGuid().ToString("N")[..8];
            using var consoleCapture = new StringWriter();

            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", uniqueIdentifier);
            Console.SetOut(consoleCapture);

            using var transport = new NknSignalingTransport();

            Console.Out.Flush();
            var output = consoleCapture.ToString();

            Assert.Contains("Initialized | address=", output, StringComparison.Ordinal);
            Assert.DoesNotContain("key_path=", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(keyPath, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"identifier={uniqueIdentifier}", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("identifier=[redacted]", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(previousConsoleOut);
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", previousKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", previousIdentifier);
            try { CleanupDirectoryIfExists(tempDir); } catch { }
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void DiagnosticsPageViewModel_ExportsMetricsJson_ToArtifactsDiagnostics_WithDeterministicTimestamp()
    {
        var metrics = new MetricsRegistry();
        metrics.Counter("transport_connect_attempts_total", transport: "NKN").Inc();

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-metrics-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var config = CreateDevLocalTestConfig();
            var vm = new DiagnosticsPageViewModel(
                static () => { },
                config,
                metricsRegistry: metrics,
                nowProvider: static () => new DateTimeOffset(2026, 2, 24, 12, 34, 56, TimeSpan.Zero),
                diagnosticsExportRootProvider: () => tempRoot);

            var path = vm.ExportMetricsJsonForTests();
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRoot, "metrics-20260224-123456.json")), path);
            Assert.True(File.Exists(path));

            var json = File.ReadAllText(path);
            Assert.Contains("\"Counters\"", json, StringComparison.Ordinal);
            Assert.Contains("transport_connect_attempts_total", json, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void Diagnostics_And_OperationalLog_Redact_Sensitive_Content()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var uniqueChatText = "hello-from-helper-" + Guid.NewGuid().ToString("N");
        var sensitive = string.Join(' ', new[]
        {
            "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==",
            "sharedKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "seedBase64=QkFTRTY0U0VFRA==",
            "seed=supersecretseedvalue",
            "private_key=-----BEGINPRIVATEKEY-----abc123",
            @"key_path=C:\Users\Juraj\AppData\Local\nLink\identity.json",
            "identifier=nlink-private-identifier",
            $"chat={uniqueChatText}"
        });

        try
        {
            SessionTimeline.Clear();
            SessionTimeline.Record("ChatReceived", sensitive);
            NknRuntimeDiagnostics.SetLastDisconnectReason(sensitive);
            NknRuntimeDiagnostics.SetLastError("NKN_START_FAILED: " + sensitive);

            var runtimeSnapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.DoesNotContain("payloadBase64", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private_key", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key_path", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, runtimeSnapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payloadBase64", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private_key", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key_path", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, runtimeSnapshot.LastDisconnectReason, StringComparison.OrdinalIgnoreCase);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? diagnostics = null;
            vm.CopyReliabilityLogRequested += (_, text) => diagnostics = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(diagnostics);
            Assert.DoesNotContain("payloadBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private_key", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key_path", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", diagnostics!, StringComparison.OrdinalIgnoreCase);

            var source = "UnitTestPrivacy" + Guid.NewGuid().ToString("N")[..8];
            LocalOperationalLog.Info(source, sensitive);

            var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
            var matchingLine = logText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains($"[{source}]", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(matchingLine));
            Assert.DoesNotContain("payloadBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private_key", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key_path", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", matchingLine!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            NknRuntimeDiagnostics.SetLastError("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void RealNknClientAdapter_BridgeDiagnosticFormatter_Redacts_Sensitive_Content()
    {
        var sensitive = "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/== key_path=C:\\Users\\Juraj\\AppData\\Local\\nLink\\identity.json seedBase64=QkFTRTY0U0VFRA==";
        var method = typeof(RealNknClientAdapter).GetMethod(
            "BuildBridgeDiagnosticLogMessage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var formatted = Assert.IsType<string>(method!.Invoke(null, new object?[] { "bridge stderr", sensitive }));

        Assert.StartsWith("bridge stderr:", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadBase64", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key_path", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seedBase64", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", formatted, StringComparison.OrdinalIgnoreCase);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionTimeline_IsCappedAt30_AndDiagnosticsExportUsesLatestEntries()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            SessionTimeline.Clear();
            for (var i = 0; i < 35; i++)
            {
                SessionTimeline.Record("Event" + i.ToString("D2"));
            }

            var snapshot = SessionTimeline.SnapshotRecent(100);
            Assert.Equal(30, snapshot.Count);
            Assert.Equal("Event05", snapshot[0].EventName);
            Assert.Equal("Event34", snapshot[^1].EventName);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? export = null;
            vm.CopyReliabilityLogRequested += (_, text) => export = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(export);
            Assert.Contains("Event34", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event00", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event01", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event02", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event03", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event04", export!, StringComparison.Ordinal);
        }
        finally
        {
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    private static void WriteScreenShareVerdict(
        string artifactDir,
        string verdict,
        string summary,
        string nextAction,
        string deepestStage,
        string deepestClassification)
    {
        Directory.CreateDirectory(artifactDir);
        File.WriteAllLines(
            Path.Combine(artifactDir, "screenshare-operator-verdict.txt"),
            [
                "operator_verdict=" + verdict,
                "operator_summary=" + summary,
                "next_operator_action=" + nextAction,
                "artifact_dir=" + artifactDir,
                "missing_required_inputs=(none)",
                "deepest_track_b_stage=" + deepestStage,
                "deepest_track_b_classification=" + deepestClassification
            ]);
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

}
