using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;

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
        "StrongBaselineArtifactDir"
    ];

    private static readonly string[] ImplementationFiles =
    [
        "tools/FileTransferOps/AnalyzerOrchestration.ps1",
        "tools/FileTransferSoak/LogParsing.ps1",
        "tools/FileTransferSoak/SoakSummaryExtraction.ps1",
        "tools/FileTransferSoak/StabilizationGates.ps1",
        "tools/FileTransferSoak/ArtifactWriters.ps1",
        "tools/FileTransferSoak/BaselineComparison.ps1",
        "tools/Run-FileTransferNknSoak.ps1",
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
        "repair-reorder-summary.txt",
        "transport-budget-summary.txt",
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
        Assert.Contains("Invoke-FileTransferGuiSmokeWithTimeout", scriptText, StringComparison.Ordinal);
        Assert.Contains("Stop-FileTransferProcessTree", scriptText, StringComparison.Ordinal);
        Assert.Contains("gui-smoke-stdout.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("gui-smoke-stderr.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ALLOW_UNSAFE_MIXED_PAYLOAD_PROFILE", scriptText, StringComparison.Ordinal);
        Assert.Contains("$analysis.GateResult.Verdict -eq 'INCONCLUSIVE'", scriptText, StringComparison.Ordinal);
        Assert.Contains("$analysis.GateResult.Verdict -eq 'INVALID_SETUP'", scriptText, StringComparison.Ordinal);
        foreach (var artifactName in RequiredLiveNknArtifactFiles)
        {
            Assert.Contains(artifactName, scriptText, StringComparison.Ordinal);
        }
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
        Assert.Contains("filetransfer-live-nkn-cycles.jsonl", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakStartupTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakProgressTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferMixedScreenShareWarmupTimeoutMs", scriptText, StringComparison.Ordinal);
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
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_grant_feedback; session_id=sess_a; frame_type=filetransfer.state.v4; chunk_index=(none); lane=bulk",
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
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; frame_type=filetransfer.state.v4; chunk_index=(none); lane=bulk",
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
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.state.v4; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1272; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=372; available_credit_bytes_after=7999488; credit_wait_active_ms=1300; send_pump_signaled=1; chunks_schedulable=372; in_flight_frames=0; in_flight_bytes=0",
                "event=filetransfer_v4_sender_credit_stall_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; wait_reason=no_credit; credit_wait_active_ms=1300; accepted_chunk_index=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=900; available_credit_chunks=0; available_credit_bytes=0; last_grant_age_ms=900; in_flight_frames=0; in_flight_bytes=0; pending_repair_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1272; granted_window_bytes=8429568; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; policy=SparseTolerant; decision=tolerated; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=379; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=700; current_profile=healthy_expanded; target_window_bytes=16777216; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=879; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; target_base_chunk_index=880; target_base_reason=sparse_ahead; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.state.v4; chunk_index=(none); lane=bulk",
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
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; frame_type=filetransfer.state.v4; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1280; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=380; available_credit_bytes_after=8171520; credit_wait_active_ms=100; send_pump_signaled=1; chunks_schedulable=380; in_flight_frames=0; in_flight_bytes=0",
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
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.state.v4; queue_depth=2; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.state.v4; queue_depth=1; enqueue_to_send_age_ms=900; send_duration_ms=120"))
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
            LogLine($"event=filetransfer_profile_selected; transport=nkn; transfer_id={transferId}; session_id=sess_a; protocol_version=4; profile=v4_live; target_window_bytes=16777216; granted_window_bytes=16777216"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_receiver_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; reason=seekable_readwrite_destination; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_sender_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=516096; raw_bytes_per_second=258048; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=24; chunks_accepted_for_transport=2860; remote_next_expected_chunk_index=2507; remote_granted_until_chunk_index_exclusive=2890; remote_granted_window_bytes=8232960; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=12; repair_send_count=2"),
            LogLine($"event=filetransfer_v4_receiver_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=516096; raw_bytes_received_per_second=258048; contiguous_bytes_committed=516096; contiguous_bytes_committed_per_second=258048; pending_chunk_count=0; pending_bytes=0; next_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; oldest_gap_age_ms=42000; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; write_batch_count=8; write_batch_bytes=516096; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=258048; sparse_written_ahead_bytes=7587072; sparse_gap_count=1"),
            LogLine($"event=filetransfer_v4_gap_stall_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; stall_duration_ms=42294; pending_bytes=0; granted_window_bytes=8232960"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=12000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=18000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_v4_receiver_feedback_enqueued; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.state.v4; queue_depth=2; coalesced_count=1"),
            LogLine($"event=filetransfer_v4_receiver_feedback_sent; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.state.v4; queue_depth=1; enqueue_to_send_age_ms=1030; send_duration_ms=649"),
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
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; lane=bulk; serialized_payload_bytes=64615; secure_payload_bytes=64840; bridge_payload_bytes=64917; bridge_command_bytes=65017; max_allowed_bytes=65536; batch_profile=Packed3x21KiB; batch_chunk_count=3; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id=transfer_payload_profile; session_id=sess_a; chunk_range=3-5; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; lane=bulk; serialized_payload_bytes=21595; secure_payload_bytes=21820; bridge_payload_bytes=21897; bridge_command_bytes=21997; max_allowed_bytes=65536; batch_profile=Current; batch_chunk_count=1; raw_to_bridge_payload_ratio=0.982; bridge_payload_fill_percent=33.41"))
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
            LogLine("event=filetransfer_binary_frame_sent; transfer_id=transfer_open; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; raw_chunk_bytes=49152; chunk_count=2")
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
            .Append(LogLine("event=filetransfer_transport_payload_rejected; transport=nkn; transfer_id=transfer_reject; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; lane=bulk; bridge_command_bytes=70000; max_allowed_bytes=65536"))
            .ToArray();

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
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.state.v4; reason=low_watermark; mode=pump; queue_depth=1; queue_limit=64"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_coalesced; transfer_id=transfer_receiver_feedback; session_id=sess_a; previous_frame_type=filetransfer.state.v4; frame_type=filetransfer.state.v4; reason=ack_only; mode=pump; queue_depth=1; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.state.v4; reason=ack_only; mode=pump; send_duration_ms=23; enqueue_to_send_age_ms=117; queue_depth=0"))
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
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; frame_type=filetransfer.state.v4; reason=low_watermark; mode=direct; send_duration_ms=900; enqueue_to_send_age_ms=0; queue_depth=0"))
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
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_failed; transfer_id=transfer_receiver_feedback_failed; session_id=sess_a; frame_type=filetransfer.state.v4; reason=queue_exhausted; mode=pump; queue_depth=64; error_code=receiver_feedback_queue_exhausted"))
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
        Assert.Equal("1", protocol["v4_negotiated_count"]);
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
        Assert.Equal("4", throughput["data_protocol_version"]);
        Assert.Equal("1.000000", throughput["v4_batch_ratio"]);
        Assert.Equal("1", throughput["v4_state_feedback_count"]);
        Assert.Equal("1", throughput["v4_feedback_redundant_success_count"]);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_sender_started_count"]);
        Assert.Equal("1", protocol["v4_receiver_started_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);

        var promotion = ReadArtifactReport(result.ArtifactDir, "v4-promotion-decision.txt");
        Assert.Equal("hold_inconclusive", promotion["decision"]);
        Assert.Equal("long_live_matrix_incomplete", promotion["reason"]);
        Assert.Equal("4", promotion["data_protocol_version"]);
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
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; lane=control; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
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
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=control_bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; lane=control_bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
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
            .Append(LogLine("event=filetransfer_v4_feedback_both_failed; transfer_id=transfer_v4_feedback_failed; session_id=sess_a; frame_type=filetransfer.state.v4; reason=both_lanes_failed"))
            .ToArray();

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
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; repair_delivery_mode=bulk_only; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control_bulk; repair_delivery_mode=control_bulk_escalated; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("3", repair["v4_repair_delivery_control_bulk_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_retry_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_credit_stall_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_frontier_not_advanced_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_batch_bulk_only_count"]);
        Assert.Equal("1", repair["v4_repair_batch_control_bulk_count"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("3", throughput["v4_repair_delivery_control_bulk_escalated_count"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["v4_repair_delivery_credit_stall_escalated_count"]);
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; batch_chunk_count=3; raw_bytes=64512; raw_to_bridge_payload_ratio=0.972; bridge_payload_fill_percent=97.2"),
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
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
    public async Task AnalyzeRetained_V4ProgressTimeoutWithNoTailRepair_ClassifiesFrontierTailRepairNeeded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_needed";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
    public async Task AnalyzeRetained_V4TailRepairSentButProgressTimeout_ClassifiesTailRepairNotFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_unfilled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4"),
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
    public async Task AnalyzeRetained_ExternalTransportHealthIssue_ReturnsExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_external")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=2; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external-transport-health-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReadyBridgeSendingButNotReceiving_ReturnsExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receive_stall")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000"))
            .Append(LogLine("event=nkn_bridge_receive_stall_detected; connect_key=test; consecutive_zero_receive_windows=3; frames_sent_since_last=12; total_messages_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000; sample_window_ms=2000"))
            .Append(LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; attempt=1; max_restarts=2; consecutive_zero_receive_windows=3; frames_sent_since_last=12; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000"))
            .Append(LogLine("event=nkn_bridge_inbound_delivery_summary; channel=bulk; messages=4; payload_bytes=4096; subscriber_present_count=4; subscriber_missing_count=0; handler_failure_count=0; source_matches_local_control_count=0; source_matches_local_media_count=0; source_matches_local_bulk_count=0; source_matches_any_local_count=0; topic_count=0; last_source_len=32; last_source_hash=abc123; initial=0"))
            .Append(LogLine("event=nkn_inbound_envelope_received; channel=bulk; reason=(none); envelope_type=file_transfer_data_frame; payload_len=1024; envelope_payload_len=768; msg_id=msg1; source_len=32; source_matches_local=0; expected_source_available=1; source_matches_expected=1; is_topic=0"))
            .Append(LogLine("event=nkn_bridge_receive_stall_recovery_unproven; connect_key=test2; recovery_count=1; requires_control_proof=1; requires_bulk_proof=1; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=4; control_last_received_age_ms=34000; bulk_last_received_age_ms=100"))
            .Append(LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=test2; recovery_count=1; resume_after_recovery_ms=1750; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=2; media_messages_received_since_last=1; bulk_messages_received_since_last=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);

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
    public async Task AnalyzeRetained_ControlStaleButBulkFlowing_ReportsControlDegraded()
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
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
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
    public async Task AnalyzeRetained_ScreenShareMediaDrops_ReturnsCohabitationWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_cohab")
            .Append(LogLine("event=screenshare_bridge_media_send_summary; frames_sent=20; send_failures=0; queue_drops=2; queue_mode=normal; queue_depth=0; oldest_queued_age_ms=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_COHABITATION_PRESSURE", verdict["verdict"]);
        Assert.Equal("coexistence-summary.txt", verdict["next_artifact"]);
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
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "4", v4BatchRatio: 1.0, v4PayloadFillPercent: 96.0),
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
            Assert.Equal("4", comparison["current_data_protocol_version"]);
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
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "4", v4BatchRatio: 0.40, v4PayloadFillPercent: 40.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100, minimumGoodput: 100, bridgeWaiting: 0, protocolVersion: "4", v4BatchRatio: 1.0, v4PayloadFillPercent: 95.0),
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
            Assert.Equal("4", comparison["current_data_protocol_version"]);
            Assert.Contains("V4 batch ratio regressed", File.ReadAllText(Path.Combine(currentDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
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
            Assert.Equal("1.000000", summary["v4_batch_ratio"]);
            Assert.Equal("1", summary["v4_feedback_redundant_success_count"]);
            Assert.Equal("0", summary["payload_rejected_count"]);
            Assert.Equal("0", summary["bridge_bulk_send_failure_count"]);

            var protocolShape = File.ReadAllText(Path.Combine(artifactDir, "protocol-shape-summary.txt"));
            Assert.Contains("filetransfer.chunk_batch.v4", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_sender_started_count=1", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_feedback_redundant_success_count=1", protocolShape, StringComparison.Ordinal);

            var baseline = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("4", baseline["current_data_protocol_version"]);
            Assert.Equal("1.000000", baseline["current_v4_batch_ratio"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("long_live_matrix_incomplete", promotion["reason"]);
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
    public async Task RunFileTransferNknSoak_FakeV4LongProofAndBaselineRerunPromotes()
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
            Assert.Equal("baseline_rerun_required", longDecision["reason"]);
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
                $"Expected fake V4 baseline rerun to promote.{Environment.NewLine}STDOUT:{Environment.NewLine}{rerun.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{rerun.Stderr}");
            AssertRequiredLiveArtifacts(rerunDir);

            var promotion = ReadArtifactReport(rerunDir, "v4-promotion-decision.txt");
            Assert.Equal("promote_v4_file_only", promotion["decision"]);
            Assert.Equal("promote", promotion["promotion_status"]);
            Assert.Equal("long_proof_and_baseline_clean", promotion["reason"]);
            Assert.Equal("1", promotion["safe_long_proof_matrix_complete"]);
            Assert.Equal("1", promotion["same_protocol_v4_baseline_pass"]);
            Assert.Equal("0", promotion["baseline_protocol_mismatch"]);
            Assert.Equal("0", promotion["baseline_regression_failed"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeV4LongProofBelowTargetSelectsSenderPumpIteration()
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
                BuildFakeLiveNknEnvironment(fakeGoodputBytesPerSecond: 1_000_000));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake V4 below-target proof to complete cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var decomposition = ReadArtifactReport(artifactDir, "throughput-decomposition-summary.txt");
            Assert.Equal("v4_sender_pump_underfed", decomposition["likely_limiter"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("iterate_sender_pump", promotion["decision"]);
            Assert.Equal("iterate", promotion["promotion_status"]);
            Assert.Equal("goodput_target_not_met", promotion["reason"]);
            Assert.Equal("sender_pump_scheduling_feed_capacity", promotion["next_focus"]);
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
            Assert.Equal("non_v4_protocol", promotion["reason"]);
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

    private static string[] BuildCleanCompletedTransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; reason=role=Sender"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; chunk_range=0-1; chunk_frame_count=2; raw_bytes=49152; lane=bulk"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; lane=bulk; serialized_payload_bytes=49251; secure_payload_bytes=49476; bridge_payload_bytes=49553; bridge_command_bytes=49653; max_allowed_bytes=65536"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-1; payload_bytes=49251; serialized_payload_bytes=49251; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-1; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    private static string[] BuildCleanCompletedV4TransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id=sess_a; direction=outbound; negotiated_version=4"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; reason=role=Receiver"),
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size=21504; pump_depth=8; pending_send_bytes_limit=2097152"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; file_only=1"),
            LogLine($"event=filetransfer_v4_manifest_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_manifest_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_feedback_first_success; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=bulk; secondary_lane=control"),
            LogLine($"event=filetransfer_v4_feedback_secondary_completed; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=control; elapsed_ms=3"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=1000; scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent=64512; repair_send_count=0"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; batch_chunk_count=3; chunk_range=0-2; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; payload_bytes=64700; serialized_payload_bytes=64700; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
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

    private static string LogLine(string message)
        => $"[2026-04-26 11:17:58Z] [INFO] [FileTransferTest] {message}";

    private static async Task<AnalyzeFixtureResult> RunAnalyzeFixtureAsync(
        IReadOnlyList<string> logLines,
        IReadOnlyList<string>? extraArguments = null)
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

        var script = await RunFileTransferOpsAsync(repoRoot, args);
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

    private static void AssertRequiredLiveArtifacts(string artifactDir)
    {
        foreach (var artifactName in RequiredLiveNknArtifactFiles.Concat(RequiredArtifactFiles))
        {
            Assert.True(
                File.Exists(Path.Combine(artifactDir, artifactName)),
                $"Expected live NKN artifact to exist: {Path.Combine(artifactDir, artifactName)}");
        }
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
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
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
