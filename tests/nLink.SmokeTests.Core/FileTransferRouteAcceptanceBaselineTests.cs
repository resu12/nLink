using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase4")]
public sealed class FileTransferRouteAcceptanceBaselineTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public void FakeBaseline_IsTrackedTestDataWithAllBaselineScenarios()
    {
        var arguments = WithFakeRouteAcceptanceBaseline(FindRepoRoot(), "Run-FileTransferRouteAcceptance.ps1",
            ["-MatrixMode", "phase4-ab-acceptance"], BuildFakeRouteAcceptanceEnvironment("baseline-test"));
        Assert.Equal("-BaselineManifestPath", arguments[^2]);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, arguments[^1]);
        using var manifest = JsonDocument.Parse(File.ReadAllText(arguments[^1]));
        Assert.Equal("synthetic-test-fixture", manifest.RootElement.GetProperty("source").GetString());
        var scenarios = manifest.RootElement.GetProperty("scenarios").EnumerateArray()
            .ToDictionary(item => item.GetProperty("scenario").GetString()!);
        Assert.Equal(5, scenarios.Count);
        Assert.Equal(1500000, scenarios["regular-nkn-v4-64mb"].GetProperty("goodputBytesPerSecond").GetInt32());
        foreach (var name in new[] { "active-tuna-v4-64mb", "live-switch-off-helpee-64mb",
            "live-switch-off-helper-64mb", "live-multi-toggle-off-on-off-64mb" })
        {
            Assert.Equal(4000000, scenarios[name].GetProperty("goodputBytesPerSecond").GetInt32());
        }
    }

    [Theory]
    [InlineData("-BaselineManifestPath")]
    [InlineData("-baselinemanifestpath")]
    [InlineData("-BaselineManifestPath:missing.json")]
    public void FakeBaseline_PreservesExplicitPaths(string parameter)
    {
        string[] arguments = [parameter, "missing.json"];
        Assert.Same(arguments, WithFakeRouteAcceptanceBaseline(FindRepoRoot(), "Run-FileTransferRouteAcceptance.ps1",
            arguments, BuildFakeRouteAcceptanceEnvironment("baseline-test")));
    }

    [Theory]
    [InlineData("Run-FileTransferRouteAcceptance.ps1", null)]
    [InlineData("Run-FileTransferRouteAcceptance.ps1", "0")]
    [InlineData("FileTransfer-Ops.ps1", "1")]
    public void FakeBaseline_DoesNotAffectLiveRunsOrOtherScripts(string script, string? fake)
    {
        string[] arguments = ["-MatrixMode", "phase4-ab-acceptance"];
        var environment = fake is null ? null : new Dictionary<string, string>
        {
            ["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI"] = fake
        };
        Assert.Same(arguments, WithFakeRouteAcceptanceBaseline(FindRepoRoot(), script, arguments, environment));
    }

    [Theory]
    [InlineData("1500000", "PASS")]
    [InlineData("1000", "FAIL")]
    public async Task FakeBaseline_SingleScenarioPreservesGoodputGate(string goodput, string verdict)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var artifactRoot = Path.Combine(root, "artifacts", "baseline-fixture-test", Guid.NewGuid().ToString("N"));
        const string timestamp = "baseline-goodput";
        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(timestamp);
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_GOODPUT_BPS"] = goodput;
            var result = await RunPowerShellFileAsync(root, Path.Combine(root, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                ["-MatrixMode", "phase4-ab-acceptance", "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot, "-GoodputOnlyRerunLimit", "0", "-SetupOnlyRerunLimit", "0"],
                environment);
            Assert.DoesNotContain("Baseline manifest missing", result.Stderr);
            var summary = ReadArtifactReport(Path.Combine(artifactRoot, timestamp), "phase4-ab-acceptance-summary.txt");
            Assert.Equal(verdict, summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal(verdict, summary["performance_verdict"]);
            Assert.Equal(verdict == "PASS", result.ExitCode == 0);
            if (verdict == "FAIL")
            {
                Assert.Contains("goodput regression exceeded",
                    File.ReadAllText(Path.Combine(artifactRoot, timestamp, "phase4-ab-acceptance-summary.txt")));
            }
        }
        finally { TryDeleteDirectory(artifactRoot); }
    }

    [Fact]
    public async Task FakeBaseline_ExplicitMissingManifestStillFails()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var artifactRoot = Path.Combine(root, "artifacts", "baseline-fixture-test", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunPowerShellFileAsync(root, Path.Combine(root, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                ["-MatrixMode", "phase4-ab-acceptance", "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot, "-BaselineManifestPath", Path.Combine(artifactRoot, "missing.json")],
                BuildFakeRouteAcceptanceEnvironment("missing-baseline"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Baseline manifest missing", result.Stderr);
        }
        finally { TryDeleteDirectory(artifactRoot); }
    }
}
