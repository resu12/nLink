using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NLink.App;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;
using Xunit.Sdk;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSoakRunnerTests : CoreSmokeTestsBase
{
    [Fact]
    public void Program_Parses_FileTransferSoak_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasFileTransferSoakArgument", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSoak = (bool)method!.Invoke(null, new object[] { new[] { "--filetransfer-soak" } })!;
        var noSoak = (bool)method.Invoke(null, new object[] { new[] { "--screenshare-soak" } })!;

        Assert.True(hasSoak);
        Assert.False(noSoak);
    }

    [Fact]
    public void FileTransferSoakRunner_Parses_Defaults_And_Overrides()
    {
        Assert.True(FileTransferSoakRunner.TryParseOptionsForTests(new[] { "--filetransfer-soak" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.Equal("local-fast", defaults!.Mode);
        Assert.Equal(new[] { 1L * 1024 * 1024, 16L * 1024 * 1024, 64L * 1024 * 1024 }, defaults.PayloadSizes);
        Assert.Equal(3, defaults.Cycles);
        Assert.Equal("alternate", defaults.Direction);
        Assert.Equal(1_313_625_684, defaults.Seed);
        Assert.Equal("None", defaults.ImpairmentProfile);
        Assert.Equal("Current", defaults.PayloadEfficiencyProfile);
        Assert.Equal(120, defaults.CycleTimeoutSeconds);

        Assert.True(FileTransferSoakRunner.TryParseOptionsForTests(
            new[]
            {
                "--filetransfer-soak",
                "local-fast",
                "--payload-sizes",
                "4KiB,8192,1MiB",
                "--cycles",
                "5",
                "--direction",
                "helpee-to-helper",
                "--seed",
                "42",
                "--impairment-profile",
                "None",
                "--artifact-dir",
                "artifacts/custom",
                "--cycle-timeout-seconds",
                "9",
                "--payload-efficiency-profile",
                "Packed3x21KiB",
                "--keep-received-files"
            },
            out var custom,
            out var customError));

        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal(new[] { 4096L, 8192L, 1L * 1024 * 1024 }, custom!.PayloadSizes);
        Assert.Equal(5, custom.Cycles);
        Assert.Equal("helpee-to-helper", custom.Direction);
        Assert.Equal(42, custom.Seed);
        Assert.Equal("None", custom.ImpairmentProfile);
        Assert.Equal("Packed3x21KiB", custom.PayloadEfficiencyProfile);
        Assert.Equal("artifacts/custom", custom.ArtifactDir);
        Assert.Equal(9, custom.CycleTimeoutSeconds);
        Assert.True(custom.KeepReceivedFiles);
    }

    [Fact]
    public void FileTransferSoakRunner_Parses_Impairment_Mode_Defaults()
    {
        Assert.True(FileTransferSoakRunner.TryParseOptionsForTests(new[] { "--filetransfer-soak", "local-impaired" }, out var impaired, out var impairedError));
        Assert.NotNull(impaired);
        Assert.Equal(string.Empty, impairedError);
        Assert.Equal("local-impaired", impaired!.Mode);
        Assert.Equal("ReorderBurst", impaired.ImpairmentProfile);

        Assert.True(FileTransferSoakRunner.TryParseOptionsForTests(new[] { "--filetransfer-soak", "local-mixed" }, out var mixed, out var mixedError));
        Assert.NotNull(mixed);
        Assert.Equal(string.Empty, mixedError);
        Assert.Equal("local-mixed", mixed!.Mode);
        Assert.Equal("ScreenSharePressure", mixed.ImpairmentProfile);

        Assert.True(FileTransferSoakRunner.TryParseOptionsForTests(
            new[] { "--filetransfer-soak", "--mode", "local-impaired", "--impairment-profile", "DelayJitter" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal("DelayJitter", custom!.ImpairmentProfile);
    }

    [Theory]
    [InlineData("--payload-sizes", "0")]
    [InlineData("--payload-sizes", "nope")]
    [InlineData("--cycles", "0")]
    [InlineData("--direction", "sideways")]
    [InlineData("--impairment-profile", "NotAProfile")]
    [InlineData("--payload-efficiency-profile", "NotAProfile")]
    [InlineData("--cycle-timeout-seconds", "0")]
    public void FileTransferSoakRunner_Rejects_Invalid_Options(string key, string value)
    {
        Assert.False(FileTransferSoakRunner.TryParseOptionsForTests(new[] { "--filetransfer-soak", key, value }, out var options, out var error));
        Assert.Null(options);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task FileTransferSoakRunner_DeterministicPayload_IsStableBySeedAndCycle()
    {
        var first = await FileTransferSoakRunner.ComputeSha256Base64ForTestsAsync(64 * 1024, 42, 0);
        var second = await FileTransferSoakRunner.ComputeSha256Base64ForTestsAsync(64 * 1024, 42, 0);
        var differentSeed = await FileTransferSoakRunner.ComputeSha256Base64ForTestsAsync(64 * 1024, 43, 0);
        var differentCycle = await FileTransferSoakRunner.ComputeSha256Base64ForTestsAsync(64 * 1024, 42, 1);

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentSeed);
        Assert.NotEqual(first, differentCycle);

        await using var stream = FileTransferSoakRunner.CreatePayloadStreamForTests(64 * 1024, 42, 0);
        var streamHash = Convert.ToBase64String(await SHA256.HashDataAsync(stream));
        Assert.Equal(first, streamHash);
    }

    [Fact]
    public void FileTransferSoakRunner_Cleanup_DeletesOnlyReceivedFile_NotParentDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-cleanup-safety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var receivedFile = Path.Combine(tempRoot, "received.bin");
        var siblingFile = Path.Combine(tempRoot, "keep.bin");
        File.WriteAllBytes(receivedFile, [1, 2, 3]);
        File.WriteAllBytes(siblingFile, [4, 5, 6]);

        try
        {
            FileTransferSoakRunner.TryDeleteReceivedFileForTests(receivedFile);

            Assert.True(Directory.Exists(tempRoot));
            Assert.False(File.Exists(receivedFile));
            Assert.True(File.Exists(siblingFile));

            FileTransferSoakRunner.TryDeleteReceivedFileForTests(tempRoot);

            Assert.True(Directory.Exists(tempRoot));
            Assert.True(File.Exists(siblingFile));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DevLocalImpairmentPolicy_None_ProducesNoDelayOrDrop()
    {
        var policy = new DevLocalImpairmentPolicy(new DevLocalImpairmentOptions(DevLocalImpairmentProfile.None, 42));
        var decision = policy.ObserveFileTransferDataFrame(CreateChunkFrame("transfer_none", 0), "transfer_none");
        var mediaDecision = policy.ObserveScreenShareMediaPayload(1024);

        Assert.False(decision.Drop);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
        Assert.False(mediaDecision.Drop);
        Assert.Equal(TimeSpan.Zero, mediaDecision.Delay);
    }

    [Fact]
    public void DevLocalImpairmentPolicy_ReorderBurst_DelaysSelectedDataFrames()
    {
        var policy = new DevLocalImpairmentPolicy(new DevLocalImpairmentOptions(DevLocalImpairmentProfile.ReorderBurst, 42));
        var decision = policy.ObserveFileTransferDataFrame(CreateChunkFrame("transfer_reorder", 0), "transfer_reorder");

        Assert.False(decision.Drop);
        Assert.True(decision.Delay > TimeSpan.Zero);
        Assert.True(decision.Reordered);

        var snapshot = policy.GetSnapshot();
        Assert.Equal(1, snapshot.FileTransferDataFramesDelayed);
        Assert.Equal(1, snapshot.FileTransferDataFramesReordered);
    }

    [Fact]
    public void DevLocalImpairmentPolicy_LossBurst_DropsOnlyFirstSendForSelectedChunk()
    {
        var policy = new DevLocalImpairmentPolicy(new DevLocalImpairmentOptions(DevLocalImpairmentProfile.LossBurst, 42));
        var first = policy.ObserveFileTransferDataFrame(CreateChunkFrame("transfer_loss", 0), "transfer_loss");
        var retry = policy.ObserveFileTransferDataFrame(CreateChunkFrame("transfer_loss", 0), "transfer_loss");

        Assert.True(first.Drop);
        Assert.False(retry.Drop);

        var snapshot = policy.GetSnapshot();
        Assert.Equal(1, snapshot.FileTransferDataFramesDropped);
    }

    [Fact]
    public void DevLocalImpairmentPolicy_ScreenSharePressure_DoesNotAffectFileTransferControlFrames()
    {
        var policy = new DevLocalImpairmentPolicy(new DevLocalImpairmentOptions(DevLocalImpairmentProfile.ScreenSharePressure, 42));
        var manifest = new FileTransferManifestFrameV6
        {
            SessionId = "sess",
            TransferId = "transfer_manifest",
            FileName = "file.bin",
            FileSizeBytes = 1024,
            ChunkSizeBytes = 1024,
            ChunkCount = 1,
            Sha256Base64 = Convert.ToBase64String(SHA256.HashData(new byte[] { 1 })),
        };
        var fileTransferDecision = policy.ObserveFileTransferDataFrame(manifest, "transfer_manifest");
        var mediaDecision = policy.ObserveScreenShareMediaPayload(1024);

        Assert.False(fileTransferDecision.Drop);
        Assert.Equal(TimeSpan.Zero, fileTransferDecision.Delay);
        Assert.False(mediaDecision.Drop);
        Assert.True(mediaDecision.Delay > TimeSpan.Zero);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferSoakRunner_LocalFastTinyPayloads_CompletesWithV4AndWritesArtifacts()
    {
        var repoRoot = FindRepoRoot();
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-localfast", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await FileTransferSoakRunner.RunAsync(
                new[]
                {
                    "--filetransfer-soak",
                    "local-fast",
                    "--payload-sizes",
                    "4KiB,8KiB",
                    "--cycles",
                    "2",
                    "--artifact-dir",
                    artifactDir,
                    "--cycle-timeout-seconds",
                    "30"
                },
                output,
                error,
                CancellationToken.None);

            try
            {
                await AssertLocalV4SoakSuccessAsync(artifactDir, exitCode, "local-fast", expectedCyclesRequested: "2");
                Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-local-soak-summary.json")));
                Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-local-soak-cycles.jsonl")));
                Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-local-soak-summary.txt")));
                Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")));
                Assert.True(File.Exists(Path.Combine(artifactDir, "baseline-comparison.txt")));
                Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-operator-verdict.txt")));
            }
            catch (Exception ex) when (ex is not XunitException)
            {
                throw;
            }
            catch (XunitException ex)
            {
                throw new XunitException(BuildSoakFailureMessage(
                    artifactDir,
                    exitCode,
                    output.ToString(),
                    error.ToString(),
                    ex.Message));
            }
        }
        finally
        {
            TryDeleteDirectory(artifactDir);
        }

        _ = repoRoot;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferSoakRunner_LocalImpairedReorderBurst_CompletesWithV4AndWritesImpairmentArtifacts()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-localimpaired-reorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await FileTransferSoakRunner.RunAsync(
                new[]
                {
                    "--filetransfer-soak",
                    "local-impaired",
                    "--payload-sizes",
                    "64KiB",
                    "--cycles",
                    "1",
                    "--artifact-dir",
                    artifactDir,
                    "--cycle-timeout-seconds",
                    "45",
                    "--impairment-profile",
                    "ReorderBurst"
                },
                output,
                error,
                CancellationToken.None);

            await AssertLocalV4SoakSuccessAsync(artifactDir, exitCode, "local-impaired", "ReorderBurst");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-impairment-summary.txt")));
            Assert.True(File.Exists(Path.Combine(artifactDir, "mixed-screenshare-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactDir);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferSoakRunner_LocalImpairedLossBurst_CompletesWithV4()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-localimpaired-loss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await FileTransferSoakRunner.RunAsync(
                new[]
                {
                    "--filetransfer-soak",
                    "local-impaired",
                    "--payload-sizes",
                    "128KiB",
                    "--cycles",
                    "1",
                    "--artifact-dir",
                    artifactDir,
                    "--cycle-timeout-seconds",
                    "60",
                    "--impairment-profile",
                    "LossBurst"
                },
                output,
                error,
                CancellationToken.None);

            await AssertLocalV4SoakSuccessAsync(artifactDir, exitCode, "local-impaired", "LossBurst");
        }
        finally
        {
            TryDeleteDirectory(artifactDir);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferSoakRunner_LocalMixed_FailsAsV4FileOnlyUnsupportedWhenMixedDisabledByEnvironment()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-localmixed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "0");

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await FileTransferSoakRunner.RunAsync(
                new[]
                {
                    "--filetransfer-soak",
                    "local-mixed",
                    "--payload-sizes",
                    "64KiB",
                    "--cycles",
                    "1",
                    "--artifact-dir",
                    artifactDir,
                    "--cycle-timeout-seconds",
                    "60"
                },
                output,
                error,
                CancellationToken.None);

            await AssertV4FileOnlyUnsupportedSoakFailureAsync(artifactDir, exitCode, "local-mixed", "ScreenSharePressure");
            var summary = ReadArtifactReport(artifactDir, "filetransfer-local-soak-summary.txt");
            Assert.Equal("local-mixed", summary["mode"]);
            Assert.Equal("ScreenSharePressure", summary["impairment_profile"]);
            Assert.True(long.Parse(summary["screen_share_frames_emitted"]) > 0);
            Assert.True(long.Parse(summary["screen_share_media_delayed_count"]) > 0);
            Assert.Equal("0", summary["screen_share_media_dropped_count"]);

            var mixed = ReadArtifactReport(artifactDir, "mixed-screenshare-summary.txt");
            Assert.Equal("1", mixed["mixed_screenshare_exercised"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
            TryDeleteDirectory(artifactDir);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferSoakRunner_LocalMixed_WithV4MixedDefault_CompletesWithScreenShareEvidence()
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-localmixed-enabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, null);

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await FileTransferSoakRunner.RunAsync(
                new[]
                {
                    "--filetransfer-soak",
                    "local-mixed",
                    "--payload-sizes",
                    "128KiB",
                    "--cycles",
                    "1",
                    "--artifact-dir",
                    artifactDir,
                    "--cycle-timeout-seconds",
                    "60"
                },
                output,
                error,
                CancellationToken.None);

            await AssertLocalV4SoakSuccessAsync(artifactDir, exitCode, "local-mixed", "ScreenSharePressure");
            var summary = ReadArtifactReport(artifactDir, "filetransfer-local-soak-summary.txt");
            Assert.Equal("4", summary["data_protocol_version"]);
            Assert.True(long.Parse(summary["v4_mixed_enabled_count"]) > 0);
            Assert.True(long.Parse(summary["v4_chunk_batch_frame_count"]) > 0);

            var mixed = ReadArtifactReport(artifactDir, "mixed-screenshare-summary.txt");
            Assert.Equal("1", mixed["mixed_screenshare_exercised"]);
            Assert.Equal("4", mixed["data_protocol_version"]);
            Assert.True(long.Parse(mixed["v4_mixed_enabled_count"]) > 0);
            Assert.True(long.Parse(mixed["screen_share_frames_emitted"]) > 0);

            var logSlice = await File.ReadAllTextAsync(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log"), Encoding.UTF8);
            Assert.Contains("event=filetransfer_v6_mixed_enabled", logSlice, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", logSlice, StringComparison.Ordinal);
            Assert.DoesNotContain("v4_file_only_required", logSlice, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
            TryDeleteDirectory(artifactDir);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOps_LocalFast_CommandWritesBaselineArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops-localfast", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif

        try
        {
            var result = await RunFileTransferOpsAsync(
                repoRoot,
                [
                    "-Mode",
                    "LocalFast",
                    "-NoBuild",
                    "-Configuration",
                    configuration,
                    "-PayloadSizes",
                    "4KiB",
                    "-Cycles",
                    "1",
                    "-ArtifactDir",
                    artifactDir,
                    "-CycleTimeoutSeconds",
                    "30"
                ]);

            await AssertLocalV4SoakSuccessAsync(artifactDir, result.ExitCode, "local-fast");
            Assert.True(File.Exists(Path.Combine(artifactDir, "baseline-comparison.txt")));
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-operator-verdict.txt")));
            var baseline = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("0", baseline["safe_baseline_available"]);
            Assert.Equal("0", baseline["regression_failed"]);
        }
        finally
        {
            TryDeleteDirectory(artifactDir);
        }
    }

    private static Dictionary<string, string> ReadArtifactReport(string artifactDir, string fileName)
    {
        return File.ReadAllLines(Path.Combine(artifactDir, fileName))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static string BuildSoakFailureMessage(
        string artifactDir,
        int exitCode,
        string stdout,
        string stderr,
        string assertionMessage)
    {
        var files = Directory.Exists(artifactDir)
            ? Directory.GetFiles(artifactDir, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(artifactDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];

        var summaryPath = Path.Combine(artifactDir, "filetransfer-local-soak-summary.txt");
        var verdictPath = Path.Combine(artifactDir, "filetransfer-operator-verdict.txt");
        var cyclesPath = Path.Combine(artifactDir, "filetransfer-local-soak-cycles.jsonl");
        var logPath = Path.Combine(artifactDir, "filetransfer-retained-log-slice.log");

        return string.Join(
            Environment.NewLine,
            [
                "File transfer local-fast soak failed.",
                $"Assertion: {assertionMessage}",
                $"ExitCode: {exitCode}",
                $"ArtifactDir: {artifactDir}",
                $"Artifacts: {(files.Length == 0 ? "(none)" : string.Join(", ", files))}",
                "Stdout:",
                string.IsNullOrWhiteSpace(stdout) ? "(empty)" : stdout.Trim(),
                "Stderr:",
                string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr.Trim(),
                "Summary:",
                ReadFileIfExists(summaryPath),
                "Verdict:",
                ReadFileIfExists(verdictPath),
                "Cycles tail:",
                ReadFileTailIfExists(cyclesPath, 8),
                "Retained log tail:",
                ReadFileTailIfExists(logPath, 40)
            ]);
    }

    private static string ReadFileIfExists(string path)
        => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "(missing)";

    private static string ReadFileTailIfExists(string path, int maxLines)
    {
        if (!File.Exists(path))
        {
            return "(missing)";
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    private static async Task AssertLocalV4SoakSuccessAsync(
        string artifactDir,
        int exitCode,
        string expectedMode,
        string? expectedImpairmentProfile = null,
        string expectedCyclesRequested = "1")
    {
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-local-soak-summary.txt")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-operator-verdict.txt")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")));

        var summary = ReadArtifactReport(artifactDir, "filetransfer-local-soak-summary.txt");
        Assert.Contains(summary["verdict"], new[] { "PASS", "WARN_RECOVERED_PRESSURE" });
        Assert.Equal(expectedMode, summary["mode"]);
        Assert.Equal(expectedCyclesRequested, summary["cycles_requested"]);
        Assert.Equal(expectedCyclesRequested, summary["cycles_completed"]);
        if (expectedImpairmentProfile is not null)
        {
            Assert.Equal(expectedImpairmentProfile, summary["impairment_profile"]);
        }

        var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
        Assert.Contains(verdict["verdict"], new[] { "PASS", "WARN_RECOVERED_PRESSURE" });

        var logSlice = await File.ReadAllTextAsync(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log"), Encoding.UTF8);
        Assert.Contains("event=filetransfer_route_selected", logSlice, StringComparison.Ordinal);
        Assert.Contains("route=regular_nkn_v4_fast", logSlice, StringComparison.Ordinal);
        Assert.Contains("protocol_version=4", logSlice, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_started", logSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_sender_started", logSlice, StringComparison.Ordinal);
        Assert.Contains("event=transfer_terminal", logSlice, StringComparison.Ordinal);
        Assert.Contains("error_code=(none)", logSlice, StringComparison.Ordinal);
    }

    private static async Task AssertV4FileOnlyUnsupportedSoakFailureAsync(
        string artifactDir,
        int exitCode,
        string expectedMode,
        string? expectedImpairmentProfile = null,
        string expectedCyclesRequested = "1")
    {
        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-local-soak-summary.txt")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-operator-verdict.txt")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")));

        var summary = ReadArtifactReport(artifactDir, "filetransfer-local-soak-summary.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", summary["verdict"]);
        Assert.Equal(expectedMode, summary["mode"]);
        Assert.Equal(expectedCyclesRequested, summary["cycles_requested"]);
        Assert.Equal("0", summary["cycles_completed"]);
        if (expectedImpairmentProfile is not null)
        {
            Assert.Equal(expectedImpairmentProfile, summary["impairment_profile"]);
        }

        var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.True(
            summary.Values.Any(static value => value.Contains("v4_file_only_required", StringComparison.Ordinal)),
            "Expected the soak summary to include v4_file_only_required.");

        var logSlice = await File.ReadAllTextAsync(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log"), Encoding.UTF8);
    }

    private static FileTransferChunkBatchFrameV6 CreateChunkFrame(string transferId, int chunkIndex)
        => new()
        {
            SessionId = "sess",
            TransferId = transferId,
            StartChunkIndex = chunkIndex,
            ChunkCount = 1,
            DataSegments = new[] { Enumerable.Repeat((byte)(chunkIndex + 1), 1024).ToArray() },
            BatchProfile = "v4_default_21k",
        };

    private static async Task<ScriptResult> RunFileTransferOpsAsync(string repoRoot, IReadOnlyList<string> arguments)
    {
        var scriptPath = Path.Combine(repoRoot, "tools", "FileTransfer-Ops.ps1");
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

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
