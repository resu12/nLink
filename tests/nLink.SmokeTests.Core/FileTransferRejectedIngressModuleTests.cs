using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRejectedIngressModuleTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("mixed")]
    [InlineData("empty")]
    [InlineData("wrong_counts")]
    [InlineData("duplicate_key")]
    public async Task CandidateAccounting_ReportsExistingDecisionsWithoutReevaluation(string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $root = '{{root.Replace("'", "''", StringComparison.Ordinal)}}'
            $mode = '{{mode}}'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $root 'tools/FileTransferOps/AnalyzerOrchestration.ps1'), [ref]$tokens, [ref]$errors)
            if (@($errors).Count) { throw 'Analyzer parse failed.' }
            $function = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq 'New-FileTransferFileTunaV4CandidateAccounting'
            }, $true))
            if ($function.Count -ne 1) { throw 'Candidate accounting function missing or duplicated.' }
            . ([scriptblock]::Create($function[0].Extent.Text))
            . (Join-Path $root 'tools/FileTransferSoak/LogParsing.ps1')
            function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Extra authority evaluation.' }
            function Get-FileTransferFileTunaV4DataPathMismatchProof { throw 'Extra correlation.' }
            function Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult { throw 'Extra rejection evaluation.' }
            $records = @(for ($i=0; $i -lt 101; $i++) {
                $recovered = $i -lt 33
                $diagnostic = $null
                if (-not $recovered -or $i -eq 0) {
                    $diagnostic = [pscustomobject]@{ Recovered=$false; RejectionChainMatched=$true
                        ReceiveDisposition='rejected_before_dispatch'
                        Reason=$(if ($i % 2 -eq 0) {'missing_tuna_resend'} else {'missing_exact_tuna_receive'})
                        Variant=$(if ($i % 2 -eq 0) {'second_tuna_rejected_regular_ingress_exact_tuna_resend_missing'} else {'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'}) }
                }
                [pscustomobject]@{ DistinctKey=('private-key-'+$i); Recovered=$recovered
                    Variant=$(if ($recovered) {'(none)'} else {$diagnostic.Variant}); RejectionDiagnostic=$diagnostic
                    Event=[pscustomobject]@{FilePath='fixture.log';LineNumber=$i+1;Sequence=$i+1
                        EventName='filetransfer_data_path_receive_result';RawLine='private-raw-line'
                        Fields=@{log_writer_id='fixture-writer';log_writer_sequence=[string]($i+7)} } }
            })
            if ($mode -ceq 'empty') { $records=@() }
            if ($mode -ceq 'duplicate_key') { $records[100].DistinctKey=$records[99].DistinctKey }
            $proof = [pscustomobject]@{RawCandidateCount=$records.Count;DistinctCount=$records.Count
                RecoveredCount=$(if ($mode -ceq 'empty') {0} else {33})
                UnresolvedCount=$(if ($mode -ceq 'empty') {0} else {68});CandidateRecords=$records }
            if ($mode -ceq 'wrong_counts') { $proof.UnresolvedCount=67 }
            $before=$proof | ConvertTo-Json -Depth 8 -Compress
            $report=New-FileTransferFileTunaV4CandidateAccounting -Proof $proof
            if (($proof | ConvertTo-Json -Depth 8 -Compress) -cne $before) { throw 'Existing proof mutated.' }
            if ($report.schema -cne 'file_tuna_v4_candidate_accounting_v1' -or -not $report.diagnostic_only -or
                $report.reconciled -ne ($mode -cin @('mixed','empty')) -or
                $report.reported_candidate_count -ne $records.Count -or @($report.candidates).Count -ne $records.Count -or
                $report.reported_recovered_count -ne $proof.RecoveredCount -or
                $report.reported_unresolved_count -ne $(if ($mode -ceq 'empty') {0} else {68})) { throw 'Accounting differs.' }
            $sum=0
            foreach ($group in $report.reason_counts) { $sum += $group.count }
            if ($sum -ne $records.Count) { throw 'Reason groups lost a candidate.' }
            $json=@{file_tuna_v4_candidate_accounting=$report} | ConvertTo-Json -Depth 4 -Compress
            if ($json.Contains('private-key-') -or $json.Contains('private-raw-line')) { throw 'Private keys or raw lines exported.' }
            $roundTrip=($json | ConvertFrom-Json).file_tuna_v4_candidate_accounting
            for ($i=0; $i -lt $records.Count; $i++) {
                $row=$roundTrip.candidates[$i]
                if ($row.ordinal -ne $i+1 -or $row.source_path -cne 'fixture.log' -or $row.source_line -ne $i+1 -or
                    $row.source_sequence -ne $i+1 -or $row.writer_sequence -ne $i+7 -or $row.writer_id -cne 'fixture-writer' -or
                    $row.recovered -ne $records[$i].Recovered) { throw 'Exact source or existing decision changed.' }
            }
            if ($records.Count) {
                if (-not $roundTrip.candidates[0].recovered -or $roundTrip.candidates[0].diagnostic_recovered -ne $false -or
                    -not $roundTrip.candidates[0].diagnostic_recorded -or $roundTrip.candidates[1].diagnostic_recorded -or
                    $null -ne $roundTrip.candidates[1].diagnostic_reason) { throw 'Recovery inferred from stale or absent diagnostics.' }
                $sendGroup=@($roundTrip.reason_counts | Where-Object {-not $_.recovered -and $_.diagnostic_reason -ceq 'missing_tuna_resend'})
                $receiveGroup=@($roundTrip.reason_counts | Where-Object {-not $_.recovered -and $_.diagnostic_reason -ceq 'missing_exact_tuna_receive'})
                if ($sendGroup.Count -ne 1 -or $sendGroup[0].count -ne 34 -or
                    $receiveGroup.Count -ne 1 -or $receiveGroup[0].count -ne 34) { throw 'Unresolved reason partition differs.' }
            }
            Write-Output 'candidate-accounting-ok'
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("candidate-accounting-ok", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectedIngressSharedModule_HasOneImplementationAndNoParserReset()
    {
        var root = FindRepoRoot();
        const string corePath = "tools/FileTransferSoak/DataPathRejectedIngressEvidence.ps1";
        const string shadowPath = "tools/FileTransferOps/GuiRejectedIngressShadow.ps1";
        const string explanationPath = "tools/FileTransferSoak/DataPathRejectedIngressExplanation.ps1";
        Assert.Contains(corePath, ImplementationFiles);
        Assert.Contains(shadowPath, ImplementationFiles);
        Assert.Contains(explanationPath, ImplementationFiles);
        var core = File.ReadAllText(Path.Combine(root, corePath));
        var writers = File.ReadAllText(Path.Combine(root, "tools/FileTransferSoak/ArtifactWriters.ps1"));
        var shadow = File.ReadAllText(Path.Combine(root, shadowPath));
        var explanation = File.ReadAllText(Path.Combine(root, explanationPath));
        var names = Regex.Matches(core, @"(?m)^function ([\w-]+) \{")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(23, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in names)
        {
            Assert.DoesNotContain($"function {name} {{", writers, StringComparison.Ordinal);
            Assert.DoesNotContain($"function {name} {{", shadow, StringComparison.Ordinal);
        }
        Assert.Contains("'DataPathRejectedIngressEvidence.ps1'", writers, StringComparison.Ordinal);
        Assert.Equal(". (Join-Path $PSScriptRoot 'DataPathRejectedIngressExplanation.ps1')",
            Assert.Single(Regex.Matches(core, @"(?m)^\s*\. [^\r\n]+").Select(match => match.Value.Trim())));
        Assert.False(Regex.IsMatch(explanation, @"(?m)^\s*\.\s"), "Formatter must not import parser dependencies.");
        foreach (var broad in new[] { "ArtifactWriters.ps1", "AnalyzerOrchestration.ps1" })
        {
            Assert.DoesNotContain(broad, core, StringComparison.Ordinal);
            Assert.DoesNotContain(broad, shadow, StringComparison.Ordinal);
            Assert.DoesNotContain(broad, explanation, StringComparison.Ordinal);
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
            foreach ($path in @('{{corePath}}', '{{shadowPath}}', '{{explanationPath}}', 'tools/FileTransferSoak/ArtifactWriters.ps1')) {
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $path), [ref]$tokens, [ref]$errors)
                if (@($errors).Count) { throw ($errors | Out-String) }
                if ($path -ceq '{{explanationPath}}') {
                    $body = $ast.Find({ param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst]
                    }, $true).Body
                    if (@($body.FindAll({ param($node)
                        $node -is [Management.Automation.Language.CommandAst] -or
                        $node -is [Management.Automation.Language.InvokeMemberExpressionAst]
                    }, $true)).Count) { throw 'Formatter must not invoke I/O, evaluators or other functions.' }
                }
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
