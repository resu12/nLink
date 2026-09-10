using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRejectedIngressModuleTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task RejectedIngressSharedModule_HasOneImplementationAndNoParserReset()
    {
        var root = FindRepoRoot();
        const string corePath = "tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1";
        const string shadowPath = "tools/FileTransferOps/GuiRejectedIngressShadow.ps1";
        Assert.Contains(corePath, ImplementationFiles);
        Assert.Contains(shadowPath, ImplementationFiles);
        var core = File.ReadAllText(Path.Combine(root, corePath));
        var writers = File.ReadAllText(Path.Combine(root, "tools/FileTransferSoak/ArtifactWriters.ps1"));
        var shadow = File.ReadAllText(Path.Combine(root, shadowPath));
        var names = Regex.Matches(core, @"(?m)^function ([\w-]+) \{")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(22, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in names)
        {
            Assert.DoesNotContain($"function {name} {{", writers, StringComparison.Ordinal);
            Assert.DoesNotContain($"function {name} {{", shadow, StringComparison.Ordinal);
        }
        Assert.Contains("'DataPathRejectedIngressEvidence.ps1'", writers, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(core, @"(?m)^\s*\.\s"), "Shared core must not import or reset parser dependencies.");
        foreach (var broad in new[] { "ArtifactWriters.ps1", "AnalyzerOrchestration.ps1" })
        {
            Assert.DoesNotContain(broad, core, StringComparison.Ordinal);
            Assert.DoesNotContain(broad, shadow, StringComparison.Ordinal);
        }
        Assert.Contains("function Get-FileTransferFileTunaV4DataPathEventNames {", writers, StringComparison.Ordinal);
        Assert.Contains("function Get-FileTransferFileTunaV4DataPathMismatchProof {", writers, StringComparison.Ordinal);
        foreach (var livePath in new[] { "GuiSmoke-Windows.ps1", "Run-FileTransferTunaGuiSmoke.ps1", "Run-FileTransferRouteAcceptance.ps1" })
        {
            var live = File.ReadAllText(Path.Combine(root, "tools", livePath));
            Assert.DoesNotContain("GuiRejectedIngressShadow", live, StringComparison.Ordinal);
            Assert.DoesNotContain("Get-TunaGuiRejectedIngressShadowDiagnostics", live, StringComparison.Ordinal);
        }
        if (!OperatingSystem.IsWindows()) { return; }
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $root = '{{root.Replace("'", "''", StringComparison.Ordinal)}}'
            foreach ($path in @('{{corePath}}', '{{shadowPath}}', 'tools/FileTransferSoak/ArtifactWriters.ps1')) {
                $tokens = $null; $errors = $null
                [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root $path), [ref]$tokens, [ref]$errors)
                if (@($errors).Count) { throw ($errors | Out-String) }
            }
            . (Join-Path $root 'tools/FileTransferSoak/LogParsing.ps1')
            $marker = [pscustomobject]@{ Marker='existing-index' }
            $script:FileTransferRetainedEventIndex = $marker
            $script:FileTransferRetainedEventQueryCache = @{ marker=$marker }
            . (Join-Path $root '{{corePath}}')
            if (-not [object]::ReferenceEquals($marker, $script:FileTransferRetainedEventIndex) -or
                -not [object]::ReferenceEquals($marker, $script:FileTransferRetainedEventQueryCache.marker)) {
                throw 'Shared core reset retained parser state.'
            }
            $index = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events @()
            if ($index.OrderedEvents.Count -ne 0 -or $index.Statistics.build_visit_count -ne 0) { throw 'Empty index changed.' }
            Write-Output 'shared-ingress-module-ok'
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("shared-ingress-module-ok", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectedIngressShadow_AuthoritativeGuiEarlyReturnNeverCallsAdapter()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var gui = Path.Combine(FindRepoRoot(), "tools/GuiSmoke-Windows.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{gui.Replace("'", "''", StringComparison.Ordinal)}}', [ref]$tokens, [ref]$errors)
            if (@($errors).Count) { throw 'GUI parse failed.' }
            $branches = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.StartsWith('if ($coordinatorSnapshotEvaluation.Mode -eq ''SnapshotV1Authoritative'') {', [StringComparison]::Ordinal)
            }, $true))
            if ($branches.Count -ne 1) { throw 'Authoritative early-return branch must be unique.' }
            function Get-TunaGuiRejectedIngressShadowDiagnostics { throw 'Shadow adapter invoked by authoritative GUI.' }
            function Get-TunaGuiAuthoritativePassHarnessClassification { return $script:harnessResult }
            function New-TunaGuiFileTransferFailureClassificationResult {
                return [pscustomobject]@{ Arguments=$args; FailClosed=$true }
            }
            $body = [scriptblock]::Create($branches[0].Extent.Text + "`nthrow 'Authoritative branch fell through.'")
            $coordinatorSnapshotEvaluation = [pscustomobject]@{ Mode='SnapshotV1Authoritative'; TransitionCount=3L }
            $ErrorMessage=''; $RouteMode='live-regular-activation-cycle'; $harnessRouteEpochRecords=@()
            $TestMetrics=$null; $DiagnosticContext=$null; $snapshotEvaluationLineVisitCount=3L; $explicitResult=$null
            $classificationStopwatch=[Diagnostics.Stopwatch]::StartNew()
            $script:harnessResult=$null; $RequireCompletedClassification=$false
            if ($null -ne (& $body)) { throw 'Authority pass no longer returns null.' }
            $script:harnessResult=[pscustomobject]@{ FailureClass='exact-harness-failure' }
            if (-not [object]::ReferenceEquals($script:harnessResult, (& $body))) { throw 'Harness failure changed.' }
            $script:harnessResult=$null; $RequireCompletedClassification=$true
            $closed = & $body
            if (-not $closed.FailClosed -or 'authoritative_snapshot_pass_harness_failure_unclassified' -notin $closed.Arguments) {
                throw 'Incomplete harness classification no longer fails closed.'
            }
            Write-Output 'authoritative-gui-shadow-not-called'
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("authoritative-gui-shadow-not-called", result.Stdout, StringComparison.Ordinal);
        }
    }
}
