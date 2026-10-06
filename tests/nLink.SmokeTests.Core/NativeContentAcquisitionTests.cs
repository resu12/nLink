namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NativeContentAcquisitionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeSelectionDiagnosticUsesOnlyBoundedReturnedFields(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentSelectionOutput.ps1'
            if(-not (Test-Path -LiteralPath $path)) {throw 'Selection diagnostic helper missing'}
            . $path
            $script:witnessReads=0
            $result=[pscustomobject]@{Status='prepared_native_input_selection';FirstMissingEdge='(none)';RecordVisits=102218L;
                CodeUnitVisits=208326025L;CumulativeMilliseconds=12345L;PrivateContent='must-not-be-retained'}
            $result | Add-Member ScriptProperty IsCurrent {throw 'Diagnostic evaluated custody'}
            $result | Add-Member ScriptMethod GetWitnesses {
                $script:witnessReads++
                $count=if($this.Status -ceq 'prepared_native_input_selection') {2} else {0}
                return ,([object[]]::new($count))
            }
            Write-NativeContentSelectionOutput $null 0
            Write-NativeContentSelectionOutput $result 1
            $result.Status='unproven';$result.FirstMissingEdge='content_input_budget';$result.CumulativeMilliseconds=21000L
            Write-NativeContentSelectionOutput $result 0
            $result.Status=([string][char]1)*300;$result.FirstMissingEdge=([string][char]2)*300
            Write-NativeContentSelectionOutput $result 0
            if($script:witnessReads -ne 3) {throw 'Diagnostic repeated witness reads'}
            'content-native-selection-output=bounded-returned-fields'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host, timeout: TimeSpan.FromSeconds(15));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-selection-output=bounded-returned-fields", result.Stdout);
        Assert.DoesNotContain("must-not-be-retained", result.Stdout);
        var rows = result.Stdout.Split('\n').Where(line => line.StartsWith("content-native-selection=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.InRange(rows[i].TrimEnd('\r').Length, 1, 4096);
            using var json = System.Text.Json.JsonDocument.Parse(rows[i]["content-native-selection=".Length..]);
            var row = json.RootElement;
            Assert.Equal(10, row.EnumerateObject().Count());
            Assert.True(row.GetProperty("DiagnosticOnly").GetBoolean());
            Assert.Equal(i != 0, row.GetProperty("ResultReturned").GetBoolean());
            Assert.Equal(i == 3, row.GetProperty("StringsTruncated").GetBoolean());
            Assert.Equal(i == 1 ? 2 : 0, row.GetProperty("WitnessCount").GetInt32());
            Assert.Equal(i == 1 ? 1 : 0, row.GetProperty("CallbackCount").GetInt32());
            if (i == 0) Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("CumulativeMilliseconds").ValueKind);
            else Assert.Equal(i == 1 ? 12345 : 21000, row.GetProperty("CumulativeMilliseconds").GetInt64());
            if (i == 2) Assert.Equal("content_input_budget", row.GetProperty("FirstMissingEdge").GetString());
            if (i == 3)
                foreach (var property in new[] { "Status", "FirstMissingEdge" }) Assert.Equal(256, row.GetProperty(property).GetString()!.Length);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeWorkerAttachmentWaitIsExtendedOnlyForExactScaleFixtures(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWorkerParent.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Worker parent parse failed'}
            $assignments=@($ast.FindAll({param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -ceq '$workerAttachmentMilliseconds'
            },$true))
            if($assignments.Count -ne 1) {throw 'Exact fixture wait selection is missing'}
            $selection=[scriptblock]::Create($assignments[0].Extent.Text+'; $workerAttachmentMilliseconds')
            foreach($workerCase in @('content-native-scale','content-native-scale-profile','content-native-valid',
                'content-native-trace-timeout','content-native-read-profile-valid','content-native-scale-extra',
                'content-native-scale-profile-extra','Content-native-scale','worker-content-native-scale')) {
                $expected=if($workerCase -cin @('content-native-scale','content-native-scale-profile')) {210000} else {30000}
                if((& $selection) -ne $expected) {throw ('Worker wait selection differs: '+$workerCase)}
            }
            $source=[IO.File]::ReadAllText($path)
            if($source -cnotmatch [regex]::Escape('$signals.attached.WaitOne($workerAttachmentMilliseconds)') -or
                $source -cnotmatch [regex]::Escape('Save-NativeContentWorkerOutput $Directory $contentWorkerPhase $contentWorkerDeadline $nativeOut $nativeErr $workerAttachmentMilliseconds')) {
                throw 'Selected wait is not used by the barrier and capture'
            }
            'content-native-worker-wait=exact-scale-only'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host, timeout: TimeSpan.FromSeconds(15));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-worker-wait=exact-scale-only", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeValidationComponentTracingPreservesClockAndCancellationRejections(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        foreach (var scenario in new[] { "local-budget-buffered", "cancel-buffered" })
        {
            var directory = Path.Combine(root, "artifacts", "native-validation-components-20261001", host + "-" + scenario);
            Directory.CreateDirectory(directory);
            var result = await RunPowerShellScriptTextAsync(script,
                [root, directory, "buffered-" + scenario, "seal",
                    typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("frontier-gui-launch=", result.Stdout);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ZeroTimeWaitObservationUsesOnlyStandaloneDiagnosticEvents(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var output = Path.Combine(root, "artifacts", "native-validation-components-20261001", "zero-wait-" + host + ".json");
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/NativeZeroWaitObservation.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, output], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-zero-wait-observation=diagnostic-only", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task FreshGuiHeaderSnapshotPreservesEveryLegacyByteComparison(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1') $RepoRoot
            $checks=[NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner]::CheckHeaderSnapshotParityForTest()
            if($checks -ne 1301) {throw 'Native header comparison count differs'}
            'native-header-snapshot-parity='+$checks
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-header-snapshot-parity=1301", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("powershell", "copied-array")]
    [InlineData("pwsh", "copied-view")]
    [InlineData("pwsh", "copied-event")]
    [InlineData("pwsh", "over-witness")]
    [InlineData("pwsh", "omit-selected")]
    [InlineData("pwsh", "record-budget")]
    [InlineData("powershell", "time-budget")]
    [InlineData("pwsh", "time-budget")]
    [InlineData("powershell", "retained-extended")]
    [InlineData("pwsh", "retained-extended")]
    [InlineData("powershell", "retained-expired")]
    [InlineData("pwsh", "retained-expired")]
    [InlineData("pwsh", "code-unit-budget")]
    [InlineData("pwsh", "retained-scope")]
    [InlineData("pwsh", "stale-owner")]
    [InlineData("pwsh", "mutate-during")]
    [InlineData("pwsh", "scale")]
    [InlineData("pwsh", "scale-profile")]
    [Trait("FileTransferAnalyzerSuite", "NativeContentAcquisition")]
    public async Task OriginalNativeInputSelectionPreservesCustodyAndIndependentBounds(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-retained-integration-20261002", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-acquisition=" + scenario, result.Stdout);
        using var capture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-output.json")));
        Assert.False(capture.RootElement.GetProperty("Deadline").GetBoolean());
        Assert.Equal(scenario is "scale" or "scale-profile" ? 210000 : 30000, capture.RootElement.GetProperty("BarrierMilliseconds").GetInt32());
        Assert.Equal("completed", capture.RootElement.GetProperty("Phase").GetString());
        Assert.Equal("complete", capture.RootElement.GetProperty("Captures").GetProperty("stdout").GetProperty("State").GetString());
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
        var stages = output.Split('\n').Where(line => line.StartsWith("content-native-stage=", StringComparison.Ordinal)).ToArray();
        Assert.InRange(stages.Length, 1, 32);
        long lastElapsed = 0;
        for (var i = 0; i < stages.Length; i++)
        {
            using var stage = System.Text.Json.JsonDocument.Parse(stages[i]["content-native-stage=".Length..]);
            Assert.True(stage.RootElement.GetProperty("DiagnosticOnly").GetBoolean());
            Assert.Equal(i + 1, stage.RootElement.GetProperty("Sequence").GetInt32());
            var elapsed = stage.RootElement.GetProperty("ElapsedMilliseconds").GetInt64();
            Assert.True(elapsed >= lastElapsed);
            lastElapsed = elapsed;
        }
        Assert.Contains("\"Stage\":\"content_selection_returned\"", output);
        Assert.Contains("\"Stage\":\"cleanup_returned\"", output);
        var selectionLine = Assert.Single(output.Split('\n').Where(line => line.StartsWith("content-native-selection=", StringComparison.Ordinal)));
        Assert.InRange(selectionLine.TrimEnd('\r').Length, 1, 4096);
        using var selection = System.Text.Json.JsonDocument.Parse(selectionLine["content-native-selection=".Length..]);
        var diagnostic = selection.RootElement;
        Assert.True(diagnostic.GetProperty("DiagnosticOnly").GetBoolean());
        Assert.False(diagnostic.GetProperty("StringsTruncated").GetBoolean());
        Assert.Equal(scenario is not ("copied-array" or "copied-view" or "stale-owner"), diagnostic.GetProperty("ResultReturned").GetBoolean());
        if (scenario is "valid" or "scale" or "scale-profile" or "retained-extended")
        {
            Assert.Equal("prepared_native_input_selection", diagnostic.GetProperty("Status").GetString());
            Assert.Equal("(none)", diagnostic.GetProperty("FirstMissingEdge").GetString());
            Assert.Equal(2, diagnostic.GetProperty("WitnessCount").GetInt32());
            Assert.Equal(1, diagnostic.GetProperty("CallbackCount").GetInt32());
        }
        else if (diagnostic.GetProperty("ResultReturned").GetBoolean())
        {
            var reason = scenario switch
            {
                "copied-event" => "content_selected_event_ambiguous",
                "over-witness" => "content_witness_limit",
                "omit-selected" => "content_selected_event_omitted",
                "record-budget" or "time-budget" or "code-unit-budget" or "retained-expired" => "content_input_budget",
                "mutate-during" => "content_original_index",
                "retained-scope" => "content_native_custody",
                _ => throw new InvalidOperationException("Unexpected selection rejection fixture.")
            };
            Assert.Equal("unproven", diagnostic.GetProperty("Status").GetString());
            Assert.Equal(reason, diagnostic.GetProperty("FirstMissingEdge").GetString());
            Assert.Equal(0, diagnostic.GetProperty("WitnessCount").GetInt32());
        }
        if (scenario is "time-budget" or "retained-extended")
            Assert.Contains("content-native-retained-consumers=original-payloads-witnesses", output);
        if (scenario is "valid" or "scale" or "scale-profile" or "retained-extended" or "time-budget")
        {
            var retainedLine = Assert.Single(output.Split('\n').Where(line => line.StartsWith("content-native-retained=", StringComparison.Ordinal)));
            using var retained = System.Text.Json.JsonDocument.Parse(retainedLine["content-native-retained=".Length..]);
            Assert.False(retained.RootElement.GetProperty("WorkRunning").GetBoolean());
            Assert.InRange(retained.RootElement.GetProperty("CumulativeMilliseconds").GetInt64(), 0, 180000);
            Assert.False(retained.RootElement.GetProperty("ContentCoverageProven").GetBoolean());
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeWorkerTimeoutRetainsStagesAndErrorsAfterOriginalCleanup(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-worker-stage-rca-20261002", host + "-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-trace-timeout", "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Native worker attachment deadline", result.Stderr);
        using var capture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-output.json")));
        Assert.True(capture.RootElement.GetProperty("Deadline").GetBoolean());
        Assert.Equal(30000, capture.RootElement.GetProperty("BarrierMilliseconds").GetInt32());
        Assert.Equal("attached_wait", capture.RootElement.GetProperty("Phase").GetString());
        foreach (var stream in new[] { "stdout", "stderr" })
            Assert.Equal("complete", capture.RootElement.GetProperty("Captures").GetProperty(stream).GetProperty("State").GetString());
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
        Assert.Contains("\"Stage\":\"timeout_control_started\"", output);
        Assert.Contains("\"Stage\":\"cleanup_returned\"", output);
        Assert.DoesNotContain("content-native-acquisition=", output);
        var error = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stderr.txt"));
        Assert.Contains("content-native-timeout-control", error);
        Assert.Contains("Content native timeout control released", error);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeWorkerOutputCaptureIsBoundedAndDoesNotWaitForIncompleteTasks(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentWorkerOutput.ps1')
            $output=[Threading.Tasks.TaskCompletionSource[string]]::new();$errorTask=[Threading.Tasks.TaskCompletionSource[string]]::new()
            $output.SetResult(('o'*65537));$errorTask.SetResult(('e'*65537))
            Save-NativeContentWorkerOutput $Directory 'attached_wait' $true $output.Task $errorTask.Task
            $capture=Get-Content -Raw -LiteralPath (Join-Path $Directory 'content-native-worker-output.json') | ConvertFrom-Json
            if($capture.BarrierMilliseconds -ne 30000) {throw 'Default capture wait differs'}
            foreach($name in @('stdout','stderr')) {
                $value=$capture.Captures.$name
                if($value.State -cne 'complete' -or -not $value.Truncated -or $value.OriginalCharacters -ne 65537 -or $value.RetainedCharacters -ne 65536 -or
                    [IO.File]::ReadAllText((Join-Path $Directory ('content-native-worker-'+$name+'.txt'))).Length -ne 65536) {throw 'Output bound differs'}
            }
            $pending=[Threading.Tasks.TaskCompletionSource[string]]::new();$fault=[Threading.Tasks.TaskCompletionSource[string]]::new()
            $fault.SetException([InvalidOperationException]::new('fixture fault'))
            Save-NativeContentWorkerOutput $Directory 'startup' $false $pending.Task $fault.Task 210000
            $capture=Get-Content -Raw -LiteralPath (Join-Path $Directory 'content-native-worker-output.json') | ConvertFrom-Json
            if($capture.BarrierMilliseconds -ne 210000) {throw 'Selected capture wait differs'}
            if($capture.Captures.stdout.State -cne 'WaitingForActivation' -or $capture.Captures.stderr.State -cne 'Faulted' -or
                $capture.Captures.stdout.RetainedCharacters -ne 0 -or $capture.Captures.stderr.RetainedCharacters -ne 0) {throw 'Incomplete output task was awaited'}
            'content-native-worker-output=bounded'
            """;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-worker-stage-rca-20261002", host + "-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host, timeout: TimeSpan.FromSeconds(15));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-worker-output=bounded", result.Stdout);
    }
}
