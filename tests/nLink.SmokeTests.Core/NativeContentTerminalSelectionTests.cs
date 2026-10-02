namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class NativeContentTerminalSelectionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalAllowanceLoadsOnlyByExplicitTestOption(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$Enabled)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count -or 'TerminalSelection' -cnotin @($ast.ParamBlock.Parameters | ForEach-Object {$_.Name.VariablePath.UserPath})) {
                throw 'Explicit terminal selection option missing'
            }
            $enabledValue=[bool]::Parse($Enabled)
            . $path $RepoRoot -TerminalSelection:$enabledValue
            $type=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]
            if(($null -ne $type.GetMethod('TryAcquireTerminalContentInputsForTest')) -ne $enabledValue) {throw 'Terminal method load differs'}
            if($null -ne ('NLink.Tests.NativeContentReadProfileForTest' -as [type])) {throw 'Terminal option enabled source tracing'}
            if([NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -ne 20000 -or
                [NLink.Tools.LocalPublicationInputLimits]::EvaluationMilliseconds -ne 60000) {throw 'Shared allowance changed'}
            'content-native-terminal-load='+$Enabled
            """;
        foreach (var enabled in new[] { "False", "True" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), enabled], host, timeout: TimeSpan.FromSeconds(20));
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("content-native-terminal-load=" + enabled, result.Stdout);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task TerminalFixtureHasNoPostSelectionCustodyConsumerAndExactWaits(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentTerminalSelection.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Terminal fixture parse failed'}
            $calls=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst]},$true))
            $entry=@($calls | Where-Object {$_.Member.Extent.Text -ceq 'TryAcquireTerminalContentInputsForTest' -and $_.Arguments[0].Extent.Text -ceq '$true'})
            if($entry.Count -ne 1) {throw 'Single enabled terminal call missing'}
            $forbidden=@('Owns','GetPhysicalOrigin','TryCapturePayloads','TryCaptureWitnesses','TryResolveWitnesses','TryBindTransfer','TryAcquireContentInputsForTest')
            foreach($call in $calls) {
                if($call.Extent.StartOffset -gt $entry[0].Extent.StartOffset -and $call.Member.Extent.Text -cin $forbidden) {
                    throw ('Post-terminal custody consumer: '+$call.Member.Extent.Text)
                }
            }
            $source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1'))
            if($source -cnotmatch '(?s)if\(\$terminalSelection\) \{\s*\. .*NativeContentTerminalSelection.ps1.*?\s+return\s*\}') {
                throw 'Terminal branch does not return directly to finally cleanup'
            }
            foreach($spec in @(@('FrontierPrimaryWorkerParent.ps1','$workerAttachmentMilliseconds','$workerCase',180000,210000,30000),
                @('FrontierGuiLaunchRequest.ps1','$attachmentWait','$case',210000,240000,20000))) {
                $tokens=$null;$errors=$null
                $parsed=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$spec[0])),[ref]$tokens,[ref]$errors)
                $assignment=@($parsed.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq $spec[1]},$true))
                if($errors.Count -or $assignment.Count -ne 1) {throw 'Exact wait assignment missing'}
                $block=[scriptblock]::Create($assignment[0].Extent.Text+'; '+$spec[1])
                $prefix=if($spec[0] -ceq 'FrontierPrimaryWorkerParent.ps1') {''} else {'cursor-parent-files-facts-ack-commit-primary-worker-'}
                foreach($name in @('content-native-terminal-scale','content-native-scale','content-native-scale-profile',
                    'content-native-terminal-valid','content-native-terminal-scale-extra','Content-native-terminal-scale')) {
                    Set-Variable -Name $spec[2].Substring(1) -Value ($prefix+$name)
                    $expected=if($name -ceq 'content-native-terminal-scale') {$spec[3]} elseif($name -cin @('content-native-scale','content-native-scale-profile')) {$spec[4]} else {$spec[5]}
                    if((& $block) -ne $expected) {throw ('Wait scope differs: '+$name)}
                }
            }
            'content-native-terminal-contract=terminal-call-order-and-exact-waits'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host, timeout: TimeSpan.FromSeconds(15));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-terminal-contract=terminal-call-order-and-exact-waits", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "inherited-time")]
    [InlineData("pwsh", "time-budget")]
    [InlineData("pwsh", "record-budget")]
    [InlineData("pwsh", "code-unit-budget")]
    [InlineData("pwsh", "copied-array")]
    [InlineData("pwsh", "copied-view")]
    [InlineData("pwsh", "copied-event")]
    [InlineData("pwsh", "mutate-during")]
    [InlineData("pwsh", "remaining-consumer")]
    [InlineData("pwsh", "frontier-slot")]
    [InlineData("pwsh", "over-witness")]
    [InlineData("pwsh", "omit-selected")]
    [InlineData("pwsh", "stale-owner")]
    public async Task TerminalSelectionPreservesIdentityAndCumulativeBounds(string host, string scenario)
        => await RunTerminalAsync(host, scenario, false);

    [Fact]
    public async Task TerminalScaleProbeRemainsSeparateFromStrictScaleAcceptance()
        => await RunTerminalAsync("pwsh", "scale", true);

    private static async Task RunTerminalAsync(string host, string scenario, bool scale)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-selection-terminal-20261002",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-terminal-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-acquisition=terminal-" + scenario, result.Stdout);
        using var capture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-output.json")));
        Assert.False(capture.RootElement.GetProperty("Deadline").GetBoolean());
        Assert.Equal(scale ? 180000 : 30000, capture.RootElement.GetProperty("BarrierMilliseconds").GetInt32());
        Assert.Equal("completed", capture.RootElement.GetProperty("Phase").GetString());
        foreach (var stream in new[] { "stdout", "stderr" })
        {
            var value = capture.RootElement.GetProperty("Captures").GetProperty(stream);
            Assert.Equal("complete", value.GetProperty("State").GetString());
            Assert.False(value.GetProperty("Truncated").GetBoolean());
        }
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
        var line = Assert.Single(output.Split('\n').Where(row => row.StartsWith("content-native-terminal=", StringComparison.Ordinal)));
        Assert.InRange(line.TrimEnd('\r').Length, 1, 4096);
        using var terminal = System.Text.Json.JsonDocument.Parse(line["content-native-terminal=".Length..]);
        var fields = terminal.RootElement;
        Assert.True(fields.GetProperty("DiagnosticOnly").GetBoolean());
        Assert.True(fields.GetProperty("TerminalOnly").GetBoolean());
        Assert.False(fields.GetProperty("ContentCoverageProven").GetBoolean());
        Assert.False(fields.GetProperty("NormalScaleGateReplaced").GetBoolean());
        Assert.False(fields.GetProperty("PostReturnCustodyUsabilityProven").GetBoolean());
        Assert.False(fields.GetProperty("WorkRunning").GetBoolean());
        Assert.False(fields.GetProperty("TerminalActive").GetBoolean());
        Assert.Equal(scenario == "stale-owner", fields.GetProperty("Closed").GetBoolean());
        Assert.True(fields.GetProperty("ReturnWorkTicks").GetInt64() >= fields.GetProperty("EntryWorkTicks").GetInt64());
        var selected = Assert.Single(output.Split('\n').Where(row => row.StartsWith("content-native-selection=", StringComparison.Ordinal)));
        using var selection = System.Text.Json.JsonDocument.Parse(selected["content-native-selection=".Length..]);
        var success = scenario is "valid" or "inherited-time" or "scale";
        Assert.Equal(success ? 2 : 0, selection.RootElement.GetProperty("WitnessCount").GetInt32());
        if (success)
        {
            Assert.Equal("prepared_native_input_selection", selection.RootElement.GetProperty("Status").GetString());
            Assert.InRange(selection.RootElement.GetProperty("CumulativeMilliseconds").GetInt64(), scenario == "inherited-time" ? 70000 : 0, 150000);
            var profiles = output.Split('\n').Where(row => row.StartsWith("content-native-selection-profile=", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, profiles.Length);
            Assert.Contains("scan_completed=2", profiles[1]);
            if (scale) Assert.True(fields.GetProperty("SourceRecords").GetInt32() >= 102215);
        }
        Assert.Contains("\"Stage\":\"cleanup_returned\"", output);
    }
}
