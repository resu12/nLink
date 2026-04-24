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
        "LocalSoak",
        "NknSoak",
        "SupportCapture",
        "Test",
        "TrackBRetained"
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
        var reportPath = Path.Combine(artifactDir, "screenshare-operator-verdict.txt");
        Assert.True(File.Exists(reportPath), $"Expected verdict report: {reportPath}");

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
        var pattern = @"(?s)\[ValidateSet\((?<body>.*?)\)\]\s*\[string\]\$" + Regex.Escape(parameterName) + @"\b";
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
