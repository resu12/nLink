namespace NLink.SmokeTests;

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
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "RetainedAnalysisIntegration")]
    public async Task RetainedAnalysis_StageDiagnosticsPublishAndReplaceOnBothHosts(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-offline-integration-20260914", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count) { throw 'Analyzer parse failed' }
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Write-FileTransferRetainedAnalysisStageJson'},$true))
            if($definition.Count -ne 1) { throw 'Stage writer definition ambiguous' }
            Invoke-Expression $definition[0].Extent.Text
            $path=Join-Path $Directory 'stage.json'
            foreach($stage in @('started','completed')) {
                Write-FileTransferRetainedAnalysisStageJson -Path $path -Diagnostics ([ordered]@{stage=$stage;count=42})
                $read=[IO.File]::ReadAllText($path) | ConvertFrom-Json
                if($read.stage -cne $stage -or $read.count -ne 42) { throw 'Stage publication changed' }
                if(@(Get-ChildItem -LiteralPath $Directory -Filter '*.tmp').Count) { throw 'Temporary stage file leaked' }
            }
            'stage-publication=pass'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("stage-publication=pass", StringComparison.Ordinal),
            result.Stdout + Environment.NewLine + result.Stderr);
    }
}
