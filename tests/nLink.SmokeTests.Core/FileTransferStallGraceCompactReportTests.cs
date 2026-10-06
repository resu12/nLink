namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceCompactReportTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("OpsSuite", "SupersededSourceAccounting")]
    public async Task CompactReportExports_PreserveOnlySelectedStallDiagnostic(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/CompactStallDiagnostic.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("compact-stall-diagnostic-exports-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell", "stall")]
    [InlineData("pwsh", "stall")]
    [InlineData("powershell", "recent_progress")]
    [InlineData("pwsh", "recent_progress")]
    [Trait("OpsSuite", "SupersededSourceAccounting")]
    public async Task CompactReportExports_PreserveVersionedStallGraceAccounting(string host, string kind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot,[string]$Kind)
            Set-StrictMode -Version Latest
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/StallGraceAccounting.ps1')
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'orchestration parse failed' }
            $prefix="filetransfer_fallback_frontier_${Kind}_grace_recheck_missing_"
            # Exercise the actual export expressions, without running unrelated
            # correlators or fabricating positive Coordinator evidence.
            $fileTransferFallbackFrontierStallGraceRecheckProof=[pscustomobject]@{
                AccountingVersion=2; RawCount=8; DistinctCount=6; RecoveredCount=2;
                SupersededCount=1; UnresolvedCount=3; Verdict='fail'
            }
            $fileTransferFallbackFrontierRecentProgressGraceRecheckProof=$fileTransferFallbackFrontierStallGraceRecheckProof
            function CheckReport($report) {
                $r=ConvertFrom-FileTransferGraceAccountingReport $report -Kind $Kind
                if ($r.AccountingVersion -ne 2 -or $r.RawCount -ne 8 -or $r.DistinctCount -ne 6 -or
                    $r.RecoveredCount -ne 2 -or $r.SupersededCount -ne 1 -or $r.UnresolvedCount -ne 3 -or
                    $report[$prefix+'verdict'] -cne 'fail') { throw 'compact export lost versioned accounting' }
            }
            $groups=@{}
            foreach ($node in $ast.FindAll({param($n) $n -is [Management.Automation.Language.BinaryExpressionAst] -and
                    $n.Operator -eq 'Format' -and $n.Left -is [Management.Automation.Language.StringConstantExpressionAst] -and
                    $n.Left.Value.StartsWith($prefix,[StringComparison]::Ordinal)},$true)) {
                $parent=$node.Parent
                while ($null -ne $parent -and $parent -isnot [Management.Automation.Language.ArrayExpressionAst]) { $parent=$parent.Parent }
                if ($null -eq $parent) { throw 'export outside report array' }
                $key=[string]$parent.Extent.StartOffset
                if (-not $groups.ContainsKey($key)) { $groups[$key]=@{} }
                $line=Invoke-Expression $node.Extent.Text
                $pair=$line -split '=',2
                if ($groups[$key].ContainsKey($pair[0])) { throw 'duplicate compact report field' }
                $groups[$key][$pair[0]]=$pair[1]
            }
            if ($groups.Count -ne 4) { throw 'expected four compact text exports' }
            foreach ($report in $groups.Values) { CheckReport $report }
            $jsonGroups=0
            foreach ($table in $ast.FindAll({param($n) $n -is [Management.Automation.Language.HashtableAst]},$true)) {
                $report=@{}
                foreach ($pair in $table.KeyValuePairs) {
                    if ($pair.Item1 -is [Management.Automation.Language.StringConstantExpressionAst] -and
                        $pair.Item1.Value.StartsWith($prefix,[StringComparison]::Ordinal)) {
                        $report[$pair.Item1.Value]=Invoke-Expression $pair.Item2.Extent.Text
                    }
                }
                if ($report.Count) { CheckReport $report; $jsonGroups++ }
            }
            if ($jsonGroups -ne 1) { throw 'expected one compact JSON export' }
            Write-Output 'compact-stall-accounting-exports-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), kind], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("compact-stall-accounting-exports-ok", result.Stdout, StringComparison.Ordinal);
    }
}
