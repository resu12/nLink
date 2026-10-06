namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NativeContentSelectionProfileTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SelectionProfileIsExplicitAndDoesNotEnableSourceInstrumentation(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$Enabled)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count -or 'SelectionProfile' -cnotin @($ast.ParamBlock.Parameters | ForEach-Object {$_.Name.VariablePath.UserPath})) {
                throw 'Selection-only profile switch missing'
            }
            $enabledValue=[bool]::Parse($Enabled)
            . $path $RepoRoot -SelectionProfile:$enabledValue
            $profile='NLink.Tests.NativeContentSelectionProfileForTest' -as [type]
            if(($null -ne $profile) -ne $enabledValue) {throw 'Selection profile load differs'}
            if($null -ne ('NLink.Tests.NativeContentReadProfileForTest' -as [type])) {throw 'Selection profile enabled source instrumentation'}
            'content-native-selection-profile-load='+$Enabled
            """;
        foreach (var enabled in new[] { "False", "True" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), enabled], host, timeout: TimeSpan.FromSeconds(20));
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("content-native-selection-profile-load=" + enabled, result.Stdout);
        }
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "time-budget")]
    [InlineData("pwsh", "record-budget")]
    [InlineData("pwsh", "stale-owner")]
    [InlineData("pwsh", "mutate-during")]
    public async Task SelectionScanObservationsPreserveSuccessAndRejections(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-selection-scan-profile-20261002", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-selection-profile-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-acquisition=" + scenario, result.Stdout);
        Assert.Contains("AddedAuthorityEvaluations\":0", result.Stdout);
        Assert.Contains("ContentCoverageProven\":false", result.Stdout);
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
        Assert.DoesNotContain("content-native-read-profile=", output);
        var rows = output.Split('\n').Where(line => line.StartsWith("content-native-selection-profile=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            Assert.InRange(row.TrimEnd('\r').Length, 1, 4096);
            Assert.Contains("diagnostic_only=True;", row);
            Assert.Contains("work_running=False;", row);
        }
        Assert.Contains("sequence=1; phase=entry;", rows[0]);
        Assert.Contains("sequence=2; phase=returned;", rows[1]);
        Assert.Contains("scan_calls=0;", rows[0]);
        Assert.Contains("check_calls=0;", rows[0]);
        if (scenario == "valid")
        {
            Assert.Contains("scan_calls=2;", rows[1]);
            Assert.Contains("scan_completed=2;", rows[1]);
        }
        if (scenario == "time-budget") Assert.Contains("resource_calls=0;", rows[1]);
        if (scenario == "record-budget") Assert.Contains("resource_calls=2;", rows[1]);
        if (scenario == "stale-owner") Assert.Contains("scan_calls=0;", rows[1]);
    }
}
