namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferRetainedAnalysisIntegrationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "RetainedAnalysisIntegration")]
    public async Task RetainedAnalysis_EmptyFallbackBindingsRemainAnEmptyCandidateSet(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count) { throw 'Analyzer parse failed' }
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelation'},$true))
            if($definition.Count -ne 1) { throw 'Correlation definition ambiguous' }
            Invoke-Expression $definition[0].Extent.Text
            foreach($withProperty in @($true,$false)) {
                $view=[pscustomobject]@{Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass';StreamComplete=$true
                    AuthoritativeFamilies=@('fallback_tail_yield');Boundaries=@()}
                if($withProperty) { $view | Add-Member NoteProperty __FallbackTailPeerProofBindings @() }
                $result=Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelation -Events @() -SnapshotEvaluation $view
                if($result.Verdict -cne 'pass' -or $result.RawCount -ne 0 -or $result.DistinctCount -ne 0 -or
                    $result.RecoveredCount -ne 0 -or $result.UnresolvedCount -ne 0 -or $result.UnresolvedCandidateRecords.Count -ne 0) {
                    throw 'Empty bindings generated proof candidates'
                }
            }
            'empty-bindings=pass'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("empty-bindings=pass", StringComparison.Ordinal),
            result.Stdout + Environment.NewLine + result.Stderr);
    }

    [Theory]
    [InlineData("powershell", 0)]
    [InlineData("pwsh", 0)]
    [InlineData("powershell", 227)]
    [InlineData("pwsh", 227)]
    [InlineData("powershell", 270)]
    [InlineData("pwsh", 270)]
    [Trait("FileTransferAnalyzerSuite", "RetainedAnalysisIntegration")]
    public async Task RetainedAnalysis_StageDiagnosticsPublishAndReplaceOnBothHosts(string host, int targetPathLength)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-stage-diagnostics", Guid.NewGuid().ToString("N"));
        // Bound the root independently of checkout length, leaving room for both
        // exact-length paths and their GUID suffix within a 255-character filename.
        Assert.InRange(directory.Length, 51, 180);
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "stage.json");
            if (targetPathLength != 0)
            {
                path = Path.Combine(directory, new string('s', targetPathLength - directory.Length - 6) + ".json");
                Assert.Equal(targetPathLength, path.Length);
                Assert.True(path.Length + 37 > 260, "The GUID-suffixed temporary path must exceed MAX_PATH.");
            }
            Assert.False(File.Exists(path));
            const string script = """
                param([string]$RepoRoot,[string]$Directory,[string]$Path)
                $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
                $tokens=$null; $errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile(
                    (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
                if($errors.Count) { throw 'Analyzer parse failed' }
                $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $n.Name -ceq 'Write-FileTransferRetainedAnalysisStageJson'},$true))
                if($definition.Count -ne 1) { throw 'Stage writer definition ambiguous' }
                Invoke-Expression $definition[0].Extent.Text
                foreach($stage in @('started','completed')) {
                    Write-FileTransferRetainedAnalysisStageJson -Path $Path -Diagnostics ([ordered]@{stage=$stage;count=42})
                    $read=[IO.File]::ReadAllText($Path) | ConvertFrom-Json
                    if($read.stage -cne $stage -or $read.count -ne 42) { throw 'Stage publication changed' }
                    if(@(Get-ChildItem -LiteralPath $Directory -Filter '*.tmp').Count) { throw 'Temporary stage file leaked' }
                }
                'stage-publication=pass'
                """;
            var result = await RunPowerShellScriptTextAsync(script, [root, directory, path], host);
            Assert.True(result.ExitCode == 0 && result.Stdout.Contains("stage-publication=pass", StringComparison.Ordinal),
                result.Stdout + Environment.NewLine + result.Stderr);
        }
        finally { TryDeleteDirectory(directory); }
    }
}
