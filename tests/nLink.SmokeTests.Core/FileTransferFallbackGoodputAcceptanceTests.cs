using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "GoodputPolicy")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferFallbackGoodputAcceptanceTests : FileTransferAcceptancePhase5TestBase
{
    [Theory]
    [InlineData("phase4", "live-switch-off-helpee-64mb", "", "PASS", "WARN")]
    [InlineData("phase4", "live-switch-off-helper-64mb", "", "PASS", "WARN")]
    [InlineData("phase5", "live-switch-off-helpee-64mb", "", "PASS", "WARN")]
    [InlineData("phase5", "live-switch-off-helper-64mb", "", "PASS", "WARN")]
    [InlineData("phase5", "live-switch-off-helpee-64mb", "SHA_FAIL", "FAIL", "WARN")]
    [InlineData("phase5", "live-switch-off-helper-64mb", "MISSING_LIVE_PROOF", "FAIL", "WARN")]
    [InlineData("phase5", "live-switch-off-helper-64mb", "HARD_FAILURE", "FAIL", "WARN")]
    [InlineData("phase4", "active-tuna-v4-64mb", "", "PASS", "FAIL")]
    [InlineData("phase4", "regular-nkn-v4-64mb", "", "PASS", "FAIL")]
    [InlineData("phase4", "regular-nkn-v4-64mb", "HARD_FAILURE", "FAIL", "FAIL")]
    [InlineData("phase4", "live-multi-toggle-off-on-off-64mb", "", "PASS", "FAIL")]
    public async Task RunFileTransferRouteAcceptance_SwitchOffGoodputIsAdvisoryOnly(
        string phase, string scenario, string fault, string correctness, string performance)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        const string timestamp = "fallback-goodput-policy";
        var runRoot = Path.Combine(artifactRoot, timestamp);
        var summaryName = phase == "phase5" ? "phase5-analyzer-gui-acceptance" : "phase4-ab-acceptance";
        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(timestamp);
            var prefix = "NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_" + scenario.Replace('-', '_').ToUpperInvariant();
            environment[prefix + "_GOODPUT_BPS"] = "1000000";
            if (fault.Length > 0) { environment[prefix + "_" + fault] = "1"; }
            var result = await RunPowerShellFileAsync(repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                ["-MatrixMode", phase == "phase5" ? "phase5-analyzer-gui-acceptance" : "phase4-ab-acceptance",
                    "-ScenarioName", scenario, "-ArtifactRoot", artifactRoot,
                    "-GoodputOnlyRerunLimit", correctness == "PASS" && performance == "WARN" ? "1" : "0",
                    "-SetupOnlyRerunLimit", "0", "-TimeoutSeconds", "30", "-ProgressTimeoutSeconds", "30"],
                environment);
            var accepted = correctness == "PASS" && (phase == "phase5" || performance != "FAIL");
            var summary = ReadArtifactReport(runRoot, summaryName + "-summary.txt");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(runRoot, summaryName + "-summary.json")));
            var root = document.RootElement;
            var failures = root.GetProperty("failures").ToString();
            Assert.True((result.ExitCode == 0) == accepted, result.Stdout + Environment.NewLine + result.Stderr + failures);
            if (correctness.Length > 0)
            {
                Assert.True(correctness == summary["correctness_verdict"], failures);
            }
            Assert.Equal(performance, summary["performance_verdict"]);
            Assert.Equal("0", summary[scenario + ".retry_used"]);
            Assert.Equal("1000000.000", summary[scenario + ".goodput_bytes_per_second"]);
            Assert.Equal(scenario == "regular-nkn-v4-64mb" ? "1350000.000" : "3600000.000",
                summary[scenario + ".goodput_floor_bytes_per_second"]);
            Assert.Equal(performance == "WARN" ? "1" : "0", summary["performance_warning_count"]);
            Assert.Equal(performance == "FAIL" ? "1" : "0", summary["performance_failure_count"]);
            Assert.Equal(performance, root.GetProperty("performanceVerdict").GetString());
            Assert.Equal(performance == "WARN" ? 1 : 0, root.GetProperty("performanceWarnings").GetArrayLength());
            if (performance == "WARN")
            {
                var warning = summary[scenario + ".goodput_regression_warning"];
                Assert.Contains("goodput regression exceeded", warning, StringComparison.Ordinal);
                Assert.Equal(warning, root.GetProperty("runs")[0].GetProperty("goodputRegressionWarning").GetString());
                Assert.Contains(warning, root.GetProperty("performanceWarnings")[0].GetString()!, StringComparison.Ordinal);
                var note = File.ReadAllText(Path.Combine(runRoot, phase + "-network-variance-note.md"));
                Assert.Contains("Performance warnings:", note, StringComparison.Ordinal);
                Assert.Contains(warning, note, StringComparison.Ordinal);
            }
            if (correctness == "FAIL")
            {
                Assert.True(int.Parse(summary["correctness_failure_count"]) > 0);
                Assert.Equal("FAIL", summary["verdict"]);
            }
        }
        finally { TryDeleteDirectory(artifactRoot); }
    }
}
