using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("Category", "Smoke")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class CiAnalyzerWorkflowTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public void AnalyzerMatrix_PreservesEveryExistingSublaneAndSerialTestExecution()
    {
        var root = FindRepoRoot();
        var lanes = File.ReadAllText(Path.Combine(root, "tools", "Test-Lanes.ps1"));
        var aggregate = Regex.Match(lanes, "(?s)\"FileTransferAnalyzer\" \\{.*?foreach.*?@\\((.*?)\\)").Groups[1].Value;
        var expected = Regex.Matches(aggregate, "\"(FileTransferAnalyzer\\w+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        var analyzer = Job("analyzer");
        var actual = Regex.Matches(analyzer, @"(?m)^          - (FileTransferAnalyzer\w+)\r?$")
            .Select(match => match.Groups[1].Value).ToArray();

        Assert.Equal(5, expected.Length);
        Assert.Equal(expected, actual);
        Assert.Contains("needs: runtime_contracts", analyzer);
        Assert.Contains("fail-fast: false", analyzer);
        Assert.DoesNotContain("continue-on-error:", analyzer);
        Assert.DoesNotContain("timeout-minutes:", Workflow());
        Assert.Contains("-Lane $env:NLINK_ANALYZER_LANE -Configuration Release -NoBuild", analyzer);
        Assert.Contains("CollectionBehavior(DisableTestParallelization = true)",
            File.ReadAllText(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Properties", "AssemblyInfo.cs")));
        Assert.Contains("\"parallelizeTestCollections\": false",
            File.ReadAllText(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "xunit.runner.json")));
    }

    [Fact]
    public void ReleaseJobs_RequireAllAnalyzerResultsAndPreserveRemainingGatesAndEvidence()
    {
        var runtime = Job("runtime_contracts");
        var analyzer = Job("analyzer");
        var smoke = Job("smoke");
        Assert.Contains("-Lane CoreContract ", runtime);
        Assert.Contains("-Lane FileTransferContract ", runtime);
        Assert.Contains("Build bridge bundle", runtime);
        Assert.Contains("Verify bridge bundle layout", runtime);
        Assert.Contains("name: ci-bridge-bundle", runtime);
        Assert.Contains("**/core-contract-results*.trx", runtime);
        Assert.Contains("**/filetransfer-contract-results*.trx", runtime);
        Assert.Contains("needs: [runtime_contracts, analyzer]", smoke);
        Assert.Contains("if: ${{ always() }}", smoke);
        Assert.Contains("${{ needs.runtime_contracts.result }}", smoke);
        Assert.Contains("${{ needs.analyzer.result }}", smoke);
        Assert.True(smoke.IndexOf("Require runtime and every analyzer lane", StringComparison.Ordinal) <
            smoke.IndexOf("- name: Checkout", StringComparison.Ordinal));
        foreach (var gate in new[] { "-Lane FileTransferAcceptanceFake ", "-Lane Smoke ", "--reliability-gate",
                     "Build installer + portable release assets", "Verify staged package manifests", "Verify release assets", "-Lane GuiSmoke " })
        {
            Assert.Contains(gate, smoke);
        }
        Assert.Contains("needs: smoke", Job("installer_smoke"));
        Assert.DoesNotContain("-Lane FileTransferAnalyzer ", Workflow());
        Assert.Contains("console;verbosity=detailed", analyzer);
        Assert.Contains("trx;LogFilePrefix=filetransfer-analyzer-results", analyzer);
        Assert.Contains("| Out-Host", analyzer);
        Assert.Matches(@"(?s)Upload analyzer results and per-test progress.*?if: always\(\).*?name: filetransfer-analyzer-\$\{\{ matrix.lane \}\}", analyzer);
        Assert.Contains("**/filetransfer-analyzer-results*.trx", analyzer);
        Assert.Contains("artifacts/ci-analyzer/progress.log", analyzer);
        Assert.Contains("name: ci-bridge-bundle", analyzer);
        Assert.Contains("name: ci-bridge-bundle", smoke);
    }

    [Theory]
    [InlineData("powershell", 0)]
    [InlineData("powershell", 17)]
    [InlineData("pwsh", 0)]
    [InlineData("pwsh", 17)]
    public async Task AnalyzerStep_RetainsNativePerTestProgressAndPropagatesFailure(string host, int exitCode)
    {
        if (!OperatingSystem.IsWindows()) return;

        var fixture = Path.Combine(Path.GetTempPath(), "nlink-ci-analyzer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "tools"));
        try
        {
            File.Copy(Path.Combine(FindRepoRoot(), "tools", "Test-Lanes.ps1"), Path.Combine(fixture, "tools", "Test-Lanes.ps1"));
            File.WriteAllText(Path.Combine(fixture, "nLink.sln"), "");
            File.WriteAllText(Path.Combine(fixture, "VERSION"), "0.7.0");
            var script = """
                param([string]$Fixture, [int]$FixtureExitCode)
                $ErrorActionPreference = 'Stop'
                Set-Location -LiteralPath $Fixture
                $env:NLINK_ANALYZER_LANE = 'FileTransferAnalyzerAuthority'
                function global:dotnet {
                    if ($args -notcontains 'OpsSuite=RetainedAuthorityProof' -or
                        $args -notcontains 'console;verbosity=detailed' -or
                        $args -notcontains 'trx;LogFilePrefix=filetransfer-analyzer-results-authority') {
                        throw 'Analyzer invocation changed'
                    }
                    & $env:ComSpec /d /c 'echo Passed CI_progress_fixture [1 ms]'
                    $global:LASTEXITCODE = $FixtureExitCode
                }
                """ + Environment.NewLine + StepScript("analyzer", "File-transfer analyzer contract tests");
            var result = await RunPowerShellScriptTextAsync(script, [fixture, exitCode.ToString()], host);
            Assert.Equal(exitCode == 0, result.ExitCode == 0);
            var progress = File.ReadAllText(Path.Combine(fixture, "artifacts", "ci-analyzer", "progress.log"));
            Assert.Contains("Passed CI_progress_fixture [1 ms]", progress);
            Assert.Contains("Passed CI_progress_fixture [1 ms]", result.Stdout);
            if (exitCode != 0) Assert.Contains("exit code 17", result.Stdout + result.Stderr);
        }
        finally
        {
            TryDeleteDirectory(fixture);
        }
    }

    [Theory]
    [InlineData("powershell", "success", "success", true)]
    [InlineData("powershell", "failure", "success", false)]
    [InlineData("powershell", "success", "failure", false)]
    [InlineData("powershell", "success", "skipped", false)]
    [InlineData("powershell", "success", "cancelled", false)]
    [InlineData("pwsh", "success", "success", true)]
    [InlineData("pwsh", "failure", "success", false)]
    [InlineData("pwsh", "success", "failure", false)]
    [InlineData("pwsh", "success", "skipped", false)]
    [InlineData("pwsh", "success", "cancelled", false)]
    public async Task ReleaseAdmission_RejectsFailedSkippedOrCancelledDependencies(
        string host, string runtime, string analyzer, bool admitted)
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = """
            param([string]$RuntimeResult, [string]$AnalyzerResult)
            $ErrorActionPreference = 'Stop'
            $env:RUNTIME_RESULT = $RuntimeResult
            $env:ANALYZER_RESULT = $AnalyzerResult
            """ + Environment.NewLine + StepScript("smoke", "Require runtime and every analyzer lane");
        var result = await RunPowerShellScriptTextAsync(script, [runtime, analyzer], host);
        Assert.Equal(admitted, result.ExitCode == 0);
        if (!admitted) Assert.Contains("Required qualification failed", result.Stdout + result.Stderr);
    }

    private static string Workflow() => File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml"));

    private static string Job(string id)
    {
        var match = Regex.Match(Workflow(), $@"(?ms)^  {Regex.Escape(id)}:\r?\n(.*?)(?=^  [a-z_]+:\r?$|\z)");
        Assert.True(match.Success, $"Missing CI job: {id}");
        return match.Groups[1].Value;
    }

    private static string StepScript(string job, string name)
    {
        var step = Regex.Match(Job(job), $@"(?ms)^      - name: {Regex.Escape(name)}\r?\n(.*?)(?=^      - name:|\z)");
        Assert.True(step.Success, $"Missing CI step: {name}");
        var script = Regex.Match(step.Groups[1].Value, @"(?ms)^        run: \|\r?\n(.*)");
        Assert.True(script.Success, $"Missing CI step script: {name}");
        return Regex.Replace(script.Groups[1].Value, @"(?m)^          ", "");
    }
}
