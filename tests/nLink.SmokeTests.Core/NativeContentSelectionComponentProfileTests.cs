namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NativeContentSelectionComponentProfileTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ComponentProfileIsExplicitAndDoesNotEnableSourceInstrumentation(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$Enabled)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count -or 'SelectionComponentProfile' -cnotin @($ast.ParamBlock.Parameters | ForEach-Object {$_.Name.VariablePath.UserPath})) {
                throw 'Selection component profile switch missing'
            }
            $enabledValue=[bool]::Parse($Enabled)
            . $path $RepoRoot -SelectionComponentProfile:$enabledValue
            $profile='NLink.Tests.NativeContentSelectionProfileForTest' -as [type]
            if(($null -ne $profile) -ne $enabledValue) {throw 'Component profile load differs'}
            if($null -ne ('NLink.Tests.NativeContentReadProfileForTest' -as [type])) {throw 'Component profile enabled source instrumentation'}
            if($enabledValue -and $null -eq $profile.GetMethod('ComponentStarted')) {throw 'Component timing hooks missing'}
            'content-native-selection-component-load='+$Enabled
            """;
        foreach (var enabled in new[] { "False", "True" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), enabled], host, timeout: TimeSpan.FromSeconds(20));
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("content-native-selection-component-load=" + enabled, result.Stdout);
        }
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "time-budget")]
    [InlineData("pwsh", "record-budget")]
    [InlineData("pwsh", "stale-owner")]
    [InlineData("pwsh", "mutate-during")]
    public async Task ComponentObservationsPreserveOriginalSelectionAccounting(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        Dictionary<string, string>? original = null;
        foreach (var profile in new[] { "selection-profile", "selection-component-profile" })
        {
            var directory = Path.Combine(root, "artifacts", "native-selection-components-profile-20261002",
                host + "-" + scenario + "-" + profile + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
            var result = await RunPowerShellScriptTextAsync(script,
                [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-" + profile + "-" + scenario, "seal",
                    typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("content-native-acquisition=" + scenario, result.Stdout);
            var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
            Assert.DoesNotContain("content-native-read-profile=", output);
            var rows = output.Split('\n').Where(line => line.StartsWith("content-native-selection-profile=", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, rows.Length);
            foreach (var row in rows)
            {
                Assert.InRange(row.TrimEnd('\r').Length, 1, 4096);
                Assert.Contains("work_running=False", row);
            }
            var fields = rows[1]["content-native-selection-profile=".Length..].Trim().Split("; ")
                .Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1]);
            if (original == null) { original = fields; Assert.DoesNotContain("components_enabled=", rows[1]); continue; }
            foreach (var key in new[] { "scan_calls", "scan_completed", "full_rows", "check_calls", "resource_calls", "evaluation_visits", "source_lines" })
                Assert.Equal(original[key], fields[key]);
            Assert.Equal("True", fields["components_enabled"]);
            Assert.Equal("128", fields["component_sample_stride"]);
            var calls = long.Parse(fields["resource_calls"]);
            foreach (var component in new[] { "base", "scope", "files", "pins" })
            {
                Assert.Equal(calls, long.Parse(fields[component + "_calls"]));
                Assert.Equal((calls + 127) / 128, long.Parse(fields[component + "_samples"]));
                Assert.InRange(long.Parse(fields[component + "_ticks"]), 0, long.Parse(fields["resource_ticks"]));
            }
        }
    }
}
