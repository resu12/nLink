using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    private const string JsonName = "filetransfer-retained-analysis-stage-diagnostics.json";
    private const string TextName = "filetransfer-retained-analysis-stage-diagnostics.txt";
    private const string SuccessMarker = "retained-diagnostics-ok";

    [Fact]
    public async Task RetainedStageDiagnostics_LockedTextPreservesJsonAndCorrelatorResult()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var textPath = Path.Combine(artifactDir, TextName);
            await File.WriteAllTextAsync(textPath, "previous-complete-text");
            using var blocker = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await RunDiagnosticsHarnessAsync(artifactDir, """
                $result = @(Invoke-FileTransferCompactCorrelator -Name 'test-correlator' -InputCount 7 -Action {
                    [pscustomobject]@{ Marker = 'actual-result'; IndexBuildVisitCount = 7; BoundedLookupVisitCount = 3 }
                })
                if ($result.Count -ne 1 -or $result[0].Marker -cne 'actual-result') { throw 'correlator result contaminated' }
                $document = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
                if ($document.correlator_status -cne 'completed' -or $document.correlator_history.Count -ne 1 -or
                    $document.correlator_history[0].status -cne 'completed' -or
                    $document.correlator_visit_count -ne 7 -or $document.correlator_lookup_count -ne 3 -or
                    $null -eq $document.json_write_ms -or $document.json_write_ms -lt 0) {
                    throw 'required JSON did not preserve completed correlator diagnostics'
                }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr), "Optional TXT failure must remain observable on stderr.");
            Assert.InRange(result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 1, 2);
            Assert.Equal("previous-complete-text", await File.ReadAllTextAsync(textPath));
        });
    }

    [Fact]
    public async Task RetainedStageDiagnostics_TransientJsonSharingViolationPublishesCompleteSnapshot()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var jsonPath = Path.Combine(artifactDir, JsonName);
            await File.WriteAllTextAsync(jsonPath, "{\"generation\":0}");
            var blocker = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var run = RunDiagnosticsHarnessAsync(artifactDir, """
                $output = @(Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics)
                if ($output.Count -ne 0) { throw 'diagnostics publisher emitted success-stream data' }
                $document = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
                if ($document.generation -ne 1 -or $document.payload -cne 'diagnostics-payload' -or
                    $null -eq $document.json_write_ms -or $document.json_write_ms -lt 0) { throw 'incomplete required JSON' }
                Write-Output 'retained-diagnostics-ok'
                """);
            var observedTemporaryFile = false;
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!run.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (Directory.EnumerateFiles(artifactDir, "*.tmp").Any())
                    {
                        observedTemporaryFile = true;
                        await Task.Delay(25);
                        break;
                    }
                    await Task.Delay(2);
                }
            }
            finally { blocker.Dispose(); }

            var result = await run;
            Assert.True(observedTemporaryFile, "The JSON lock must overlap a real atomic publication attempt.");
            AssertSucceeded(result);
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        });
    }

    [Fact]
    public async Task RetainedStageDiagnostics_PersistentJsonSharingViolationFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var jsonPath = Path.Combine(artifactDir, JsonName);
            const string previousJson = "{\"generation\":0,\"complete\":true}";
            await File.WriteAllTextAsync(jsonPath, previousJson);
            using var blocker = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await RunDiagnosticsHarnessAsync(artifactDir, """
                $watch = [Diagnostics.Stopwatch]::StartNew()
                try {
                    Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
                }
                catch {
                    $baseException = $_.Exception.GetBaseException()
                    if ($baseException -isnot [IO.IOException] -or (($baseException.HResult -band 0xffff) -notin @(5, 32, 33)) -or
                        $_.Exception.Message -notlike '*retained_analysis_diagnostics_write_failed*') { throw }
                    if ($watch.ElapsedMilliseconds -gt 5000) { throw 'required JSON sharing retry was not bounded' }
                    [Console]::Error.WriteLine('expected-required-json-failure')
                    exit 23
                }
                throw 'required JSON sharing failure was swallowed'
                """);
            Assert.Equal(23, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
            Assert.Contains("expected-required-json-failure", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(previousJson, await File.ReadAllTextAsync(jsonPath));
        });
    }

    [Fact]
    public async Task RetainedStageDiagnostics_CompatibleConcurrentReadersSeeOnlyCompleteJson()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var jsonPath = Path.Combine(artifactDir, JsonName);
            var payload = new string('x', 8192);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new { generation = 0, payload, complete = true }));
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                var generations = new HashSet<int>();
                do
                {
                    string json;
                    using (var stream = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var textReader = new StreamReader(stream))
                    {
                        json = textReader.ReadToEnd();
                    }
                    using var document = JsonDocument.Parse(json);
                    var generation = document.RootElement.GetProperty("generation").GetInt32();
                    Assert.InRange(generation, 0, 40);
                    Assert.True(document.RootElement.GetProperty("complete").GetBoolean());
                    Assert.Equal(payload, document.RootElement.GetProperty("payload").GetString());
                    generations.Add(generation);
                    await Task.Delay(1);
                } while (!stop.IsCancellationRequested);
                return generations;
            });
            ScriptResult result;
            HashSet<int> observedGenerations;
            try
            {
                result = await RunDiagnosticsHarnessAsync(artifactDir, """
                    $StageDiagnostics['payload'] = 'x' * 8192
                    $StageDiagnostics['complete'] = $true
                    foreach ($generation in 1..40) {
                        $StageDiagnostics['generation'] = $generation
                        $output = @(Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics)
                        if ($output.Count -ne 0) { throw 'diagnostics publisher emitted success-stream data' }
                        Start-Sleep -Milliseconds 5
                    }
                    Write-Output 'retained-diagnostics-ok'
                    """);
            }
            finally
            {
                stop.Cancel();
                observedGenerations = await reader;
            }
            AssertSucceeded(result);
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains(0, observedGenerations);
            Assert.Contains(observedGenerations, generation => generation is > 0 and < 40);
            using var finalDocument = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            Assert.Equal(40, finalDocument.RootElement.GetProperty("generation").GetInt32());
        });
    }

    [Fact]
    public async Task RetainedStageDiagnostics_ActionFailureRemainsPrimaryWhenJsonPublicationAlsoFails()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var result = await RunDiagnosticsHarnessAsync(artifactDir, """
                $script:jsonLock = $null
                $script:actionRan = $false
                try {
                    try {
                        $result = @(Invoke-FileTransferCompactCorrelator -Name 'failing-action' -InputCount 1 -Action {
                            $script:actionRan = $true
                            $script:jsonLock = [IO.File]::Open($jsonPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                            throw [InvalidOperationException]::new('original-action-failure')
                        })
                        throw 'action failure was swallowed'
                    }
                    catch {
                        $baseException = $_.Exception.GetBaseException()
                        if ($baseException -isnot [InvalidOperationException] -or $baseException.Message -cne 'original-action-failure') { throw }
                    }
                }
                finally { if ($null -ne $script:jsonLock) { $script:jsonLock.Dispose() } }
                if (-not $script:actionRan -or $StageDiagnostics['correlator_status'] -cne 'failed' -or
                    $StageDiagnostics['correlator_history'].Count -ne 1 -or
                    $StageDiagnostics['correlator_history'][0]['status'] -cne 'failed') { throw 'actual action failure history lost' }
                $document = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
                if ($document.correlator_status -cne 'started') { throw 'failed JSON publication replaced the prior complete snapshot' }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
            Assert.False(string.IsNullOrWhiteSpace(result.Stderr), "Secondary diagnostics failure must remain observable.");
        });
    }

    [Fact]
    public async Task RetainedStageDiagnostics_RequiredJsonFailureDoesNotRelabelSuccessfulCorrelator()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var result = await RunDiagnosticsHarnessAsync(artifactDir, """
                $script:jsonLock = $null
                $script:actionRan = $false
                try {
                    try {
                        $result = @(Invoke-FileTransferCompactCorrelator -Name 'successful-action' -InputCount 7 -Action {
                            $script:actionRan = $true
                            $script:jsonLock = [IO.File]::Open($jsonPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                            [pscustomobject]@{ Marker = 'actual-result'; IndexBuildVisitCount = 7; BoundedLookupVisitCount = 3 }
                        })
                        throw 'required completion JSON failure was swallowed'
                    }
                    catch {
                        $baseException = $_.Exception.GetBaseException()
                        if ($baseException -isnot [IO.IOException] -or (($baseException.HResult -band 0xffff) -notin @(5, 32, 33)) -or
                            $_.Exception.Message -notlike '*retained_analysis_diagnostics_write_failed*') { throw }
                    }
                }
                finally { if ($null -ne $script:jsonLock) { $script:jsonLock.Dispose() } }
                if (-not $script:actionRan -or $StageDiagnostics['correlator_status'] -cne 'completed' -or
                    $StageDiagnostics['correlator_history'].Count -ne 1 -or
                    $StageDiagnostics['correlator_history'][0]['status'] -cne 'completed' -or
                    $StageDiagnostics['correlator_visit_count'] -ne 7 -or $StageDiagnostics['correlator_lookup_count'] -ne 3) {
                    throw 'successful action was relabeled as a failed correlator'
                }
                $document = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
                if ($document.correlator_status -cne 'started') { throw 'failed JSON publication replaced the prior complete snapshot' }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
        });
    }

    [Fact]
    public async Task FullReportStages_RecordSequentialWorkAndExactReuseCountsWithoutChangingResults()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var result = await RunDiagnosticsHarnessAsync(artifactDir, """
                $observer = New-FileTransferFullReportStageObserver -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
                $output = @(Invoke-FileTransferFullReportAction -StageObserver $observer -Action {
                    & $observer -Name 'full_transfer_snapshot' -Status Started -InputCount 1
                    & $observer -Status Completed -MissCount 1
                    & $observer -Name 'full_classifier_snapshot' -Status Started -InputCount 7
                    & $observer -Status Completed -ReuseCount 1
                    & $observer -Name 'full_compatibility' -Status Started -InputCount 7
                    & $observer -Name 'full_inline_classification' -Status Started -InputCount 7
                    [pscustomobject]@{ Marker = 'actual-proof'; Quartet = '1/1/0/1' }
                })
                if ($output.Count -ne 1 -or $output[0].Marker -cne 'actual-proof' -or $output[0].Quartet -cne '1/1/0/1') {
                    throw 'full-report stage diagnostics changed the action result'
                }
                $document = [IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
                if ($document.correlator_history.Count -ne 4 -or $document.correlator_status -cne 'completed' -or
                    $document.current_correlator -cne 'full_inline_classification' -or
                    $document.snapshot_evaluation_reuse_count -ne 1 -or $document.snapshot_evaluation_miss_count -ne 1 -or
                    $document.correlator_history[0].snapshot_evaluation_miss_count -ne 1 -or
                    $document.correlator_history[1].snapshot_evaluation_reuse_count -ne 1) { throw 'full-report stage accounting lost' }
                foreach ($entry in $document.correlator_history) {
                    if ($entry.status -cne 'completed' -or $entry.elapsed_ms -lt 0 -or
                        [string]::IsNullOrWhiteSpace($entry.started_utc) -or [string]::IsNullOrWhiteSpace($entry.completed_utc)) {
                        throw 'full-report stage did not close exactly'
                    }
                }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullReportStages_PreserveActionOutcomeWhenRequiredReportingFails(bool actionFails)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var result = await RunDiagnosticsHarnessAsync(artifactDir, $$"""
                $actionFails = ${{actionFails.ToString().ToLowerInvariant()}}
                $observer = New-FileTransferFullReportStageObserver -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
                $script:jsonLock = $null
                try {
                    try {
                        Invoke-FileTransferFullReportAction -StageObserver $observer -Action {
                            & $observer -Name 'full_completed_prefix' -Status Started -InputCount 1
                            & $observer -Name 'full_exact_active_stage' -Status Started -InputCount 7
                            $script:jsonLock = [IO.File]::Open($jsonPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                            if ($actionFails) { throw [InvalidOperationException]::new('original-full-stage-failure') }
                            [pscustomobject]@{ Marker = 'actual-result' }
                        }
                        throw 'required failure swallowed'
                    }
                    catch {
                        $baseException = $_.Exception.GetBaseException()
                        if ($actionFails) {
                            if ($baseException -isnot [InvalidOperationException] -or $baseException.Message -cne 'original-full-stage-failure') { throw }
                        }
                        elseif ($baseException -isnot [IO.IOException] -or $_.Exception.Message -notlike '*retained_analysis_diagnostics_write_failed*') { throw }
                    }
                }
                finally { if ($null -ne $script:jsonLock) { $script:jsonLock.Dispose() } }
                $expectedStatus = if ($actionFails) { 'failed' } else { 'completed' }
                if ($StageDiagnostics['correlator_history'].Count -ne 2 -or
                    $StageDiagnostics['correlator_history'][0]['status'] -cne 'completed' -or
                    $StageDiagnostics['correlator_history'][1]['status'] -cne $expectedStatus -or
                    $StageDiagnostics['current_correlator'] -cne 'full_exact_active_stage') { throw 'wrong full stage blamed' }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FullReportStages_ProductionWriterAttributesPostClassifierWork(bool cached, bool actionFails)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await WithArtifactDirectoryAsync(async artifactDir =>
        {
            var result = await RunDiagnosticsHarnessAsync(artifactDir, $$"""
                $cached = ${{cached.ToString().ToLowerInvariant()}}
                $actionFails = ${{actionFails.ToString().ToLowerInvariant()}}
                $writerAst = [Management.Automation.Language.Parser]::ParseFile(
                    (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1'), [ref]$tokens, [ref]$parseErrors)
                if (@($parseErrors).Count) { throw 'writer parse failed' }
                $writers = @($writerAst.FindAll({ param($n)
                    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $n.Name -ceq 'Write-FileTransferDiagnosticsArtifacts'
                }, $true))
                if ($writers.Count -ne 1) { throw 'expected one writer' }
                $statements = @($writers[0].Body.EndBlock.Statements)
                $classification = @($statements | Where-Object {
                    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $_.Left.Extent.Text -ceq '$recoveryClassification'
                })
                $resolution = @($statements | Where-Object {
                    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $_.Left.Extent.Text -ceq '$reportProofs'
                })
                if ($classification.Count -ne 1 -or $resolution.Count -ne 1) { throw 'writer boundaries changed' }
                $prefix = @($statements | Where-Object {
                    $_.Extent.StartOffset -ge $classification[0].Extent.StartOffset -and
                    $_.Extent.EndOffset -lt $resolution[0].Extent.StartOffset
                })
                if ($prefix.Count -ne 2 -or $prefix[1] -isnot [Management.Automation.Language.IfStatementAst] -or
                    -not $prefix[1].Extent.Text.Contains("-Name 'full_writer_proofs_ledger_and_verdict' -Status Started")) {
                    throw 'post-classifier writer work has no exact stage before proof resolution'
                }
                $publication = @($statements | Where-Object {
                    $_ -is [Management.Automation.Language.IfStatementAst] -and
                    $_.Extent.Text.Contains("-Name 'full_writer_operator_and_snapshot_artifacts' -Status Started")
                })
                if ($publication.Count -ne 1) { throw 'publication boundary changed' }
                $prefixBlock = [scriptblock]::Create(($prefix.Extent.Text -join "`n"))
                $publicationBlock = [scriptblock]::Create($publication[0].Extent.Text)
                $Summary = [pscustomobject]@{ AllEvents = @(1, 2, 3) }
                $writerAllEventCount = 3
                $LiveRouteProofMode = 'None'
                $CoordinatorSnapshotEvaluationHandoff = $null
                $reportProofHandoff = $null
                $StageObserver = New-FileTransferFullReportStageObserver -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
                $script:expectedClassification = [pscustomobject]@{ Marker = 'exact-classification'; Quartet = '1/1/0/1' }
                function Get-FileTransferRecoveryFailureClassification {
                    param($Summary, $LiveRouteProofMode, $CoordinatorSnapshotEvaluationHandoff, $StageObserver, $ReportProofHandoff)
                    if (-not $cached) {
                        & $StageObserver -Name 'classifier-test-prefix' -Status Started -InputCount 3
                        & $StageObserver -Status Completed
                    }
                    return $script:expectedClassification
                }
                try {
                    $output = @(Invoke-FileTransferFullReportAction -StageObserver $StageObserver -Action {
                        . $prefixBlock
                        if ($actionFails) { throw [InvalidOperationException]::new('writer-proof-failed') }
                        . $publicationBlock
                        $recoveryClassification
                    })
                    if ($actionFails) { throw 'writer failure swallowed' }
                    if ($output.Count -ne 1 -or -not [object]::ReferenceEquals($output[0], $script:expectedClassification)) {
                        throw 'stage changed classification output'
                    }
                }
                catch {
                    if (-not $actionFails -or $_.Exception.GetBaseException().Message -cne 'writer-proof-failed') { throw }
                }
                $stages = @($StageDiagnostics['correlator_history'] | Where-Object {
                    $_.name -ceq 'full_writer_proofs_ledger_and_verdict'
                })
                $expectedStatus = if ($actionFails) { 'failed' } else { 'completed' }
                if ($stages.Count -ne 1 -or $stages[0].status -cne $expectedStatus -or
                    $stages[0].input_count -ne 3 -or $stages[0].elapsed_ms -lt 0) { throw 'writer work misattributed' }
                if ($actionFails -and $StageDiagnostics['current_correlator'] -cne 'full_writer_proofs_ledger_and_verdict') {
                    throw 'earlier classifier blamed for writer failure'
                }
                Write-Output 'retained-diagnostics-ok'
                """);
            AssertSucceeded(result);
        });
    }

    private static Task<ScriptResult> RunDiagnosticsHarnessAsync(string artifactDir, string body) =>
        RunPowerShellScriptTextAsync(HarnessPrefix + Environment.NewLine + body, [FindRepoRoot(), artifactDir]);

    private static void AssertSucceeded(ScriptResult result)
    {
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal(SuccessMarker, result.Stdout.Trim());
    }

    private static async Task WithArtifactDirectoryAsync(Func<string, Task> action)
    {
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-retained-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        try
        {
            await action(artifactDir);
            Assert.DoesNotContain(Directory.EnumerateFiles(artifactDir), path =>
                path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
            Assert.All(Directory.EnumerateFiles(artifactDir), path => Assert.Contains(Path.GetFileName(path), new[] { TextName, JsonName }));
        }
        finally { TryDeleteDirectory(artifactDir); }
    }

    private const string HarnessPrefix = """
        param([string]$RepoRoot, [string]$ArtifactDir)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 is required' }
        $source = Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors -and $parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
        $functions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
        foreach ($name in @('Write-FileTransferRetainedAnalysisStageJson', 'Write-FileTransferRetainedAnalysisStageDiagnostics', 'Invoke-FileTransferCompactCorrelator', 'New-FileTransferFullReportStageObserver', 'Invoke-FileTransferFullReportAction')) {
            $definition = @($functions | Where-Object { $_.Name -ceq $name })
            if ($definition.Count -ne 1) { throw ('expected exactly one production function: ' + $name) }
            . ([scriptblock]::Create($definition[0].Extent.Text))
        }
        $jsonPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis-stage-diagnostics.json'
        $StageDiagnostics = [ordered]@{
            stage = 'compact_artifacts'
            generation = 1
            payload = 'diagnostics-payload'
            json_write_ms = 0
            correlator_history = New-Object System.Collections.Generic.List[object]
        }
        """;
}
