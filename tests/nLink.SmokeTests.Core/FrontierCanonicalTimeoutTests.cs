using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierCanonicalTimeoutTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task TimeoutOverride_IsCanonicalOnly()
    {
        var root = FindRepoRoot();
        var source = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/FrontierPayloadPrimaryTests.cs"));
        Assert.Contains("\"canonical-fallback\"], host, timeout: TimeSpan.FromMinutes(10)", source);
        Assert.Single(source.Split("TimeSpan.FromMinutes(10)", StringSplitOptions.None).Skip(1));
        Assert.Equal(TimeSpan.FromMinutes(5), ScriptProcessTimeout);
        var relay = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1"));
        Assert.Contains("$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback'", relay);
        Assert.Contains("elseif(-not $analysisFailure -or $receipt.completedProofArtifacts -or $receipt.coordinatorSnapshotMode -cne 'LegacyV0')", relay);
        Assert.Contains("if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-source')", relay);
    }

    public static IEnumerable<object[]> CheckpointCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "normal", "inactive", "count-limit", "byte-limit", "existing", "write-failure", "mutation" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(CheckpointCases))]
    public async Task Checkpoints_AreBoundedDiagnosticOnly(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = NewDirectory(host + "-" + scenario);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpointControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("primary-checkpoint-control=" + scenario, result.Stdout);
        Assert.Contains("original stdout", result.Stdout);
        Assert.Contains("original stderr", result.Stderr);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task Timeout_KillsTreeButRetainsCompleteDiagnosticPrefix(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = NewDirectory(host + "-timeout");
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'
            [IO.File]::WriteAllText((Join-Path $Directory 'parent-pid.txt'),[string]$PID)
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=(Get-Process -Id $PID).Path
            $script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpointControls.ps1'
            $start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -RepoRoot "'+$RepoRoot+'" -Directory "'+$Directory+'" -Case timeout-child'
            $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
            $child=[Diagnostics.Process]::Start($start)
            $child.WaitForExit()
            """, [root, directory], host, timeout: TimeSpan.FromSeconds(15));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("PowerShell harness timed out after 15s", result.Stderr);
        foreach (var name in new[] { "parent-pid.txt", "child-pid.txt" })
        {
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, name)));
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
        var path = Path.Combine(directory, "test-primary-checkpoints.ndjson");
        var records = ReadCompletePrefix(await File.ReadAllBytesAsync(path));
        Assert.Single(records);
        Assert.Equal("source-profile", records[0].GetProperty("stage").GetString());
        Assert.True(records[0].GetProperty("diagnosticOnly").GetBoolean());
        await File.AppendAllTextAsync(path, "{\"incomplete\":");
        Assert.Single(ReadCompletePrefix(await File.ReadAllBytesAsync(path)));
        await File.AppendAllTextAsync(path, "\n");
        Assert.ThrowsAny<JsonException>(() => ReadCompletePrefix(File.ReadAllBytes(path)));
        Assert.False(File.Exists(Path.Combine(directory, "filetransfer-retained-analysis-attempt-receipt.json")));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CanonicalMode_SeparatesCompletedAnalysisFromIncompleteRouteProof(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "canonical-receipt-contract-20260928", host + "-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierCanonicalFallbackProofControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-fallback-proof-controls=32", result.Stdout);
        Assert.Contains("canonical-receipt-guard-controls=30", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "bind-dispose-failure")]
    [InlineData("pwsh", "bind-dispose-failure")]
    [InlineData("powershell", "cleanup-current")]
    [InlineData("pwsh", "cleanup-current")]
    public async Task Checkpoints_PreserveOriginalErrorAndSingleObservations(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = NewDirectory(host + "-" + scenario);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierPayloadPrimaryErrorControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, scenario, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var records = ReadCompletePrefix(await File.ReadAllBytesAsync(Path.Combine(directory, "test-primary-checkpoints.ndjson")));
        Assert.Contains(records, r => r.GetProperty("stage").GetString() == (scenario.StartsWith("bind-", StringComparison.Ordinal) ? "bind-error" : "cleanup-error"));
        Assert.DoesNotContain(records, r => r.GetProperty("stage").GetString() == "finished");
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryProjection_KeepsOriginalInvocationsAndProfileScope(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = NewDirectory(host + "-projection");
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $worker=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryWorker.ps1'))
            $prefix=$worker.Substring(0,$worker.IndexOf('# The outer source contains its own param block.'))
            foreach($case in @('valid','canonical-source','canonical-fallback')) {
                $target=Join-Path $Directory $case; [void][IO.Directory]::CreateDirectory($target)
                [IO.File]::WriteAllText((Join-Path $target 'parent-case.json'),(@{repoRoot=$RepoRoot;scenario='merged-native'} | ConvertTo-Json -Compress))
                $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$case
                . ([scriptblock]::Create($prefix)) -ArtifactDir $target
                $tokens=$null; $errors=$null
                $null=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
                if($errors.Count -or [regex]::Matches($text,'\$profile\.Invoke\(\$inputs,\$null\)').Count -ne 1 -or
                    [regex]::Matches($text,'& \$realParser').Count -ne 2 -or
                    [regex]::Matches($text,'& \$realEvaluator').Count -ne 1) {throw 'Original invocation projection changed'}
                if($text.Contains("Write-FrontierPrimaryCheckpoint 'source-origins'") -ne ($case -ceq 'canonical-fallback')) {throw 'Checkpoint source projection escaped profile'}
                if((Test-Path -LiteralPath (Join-Path $target 'test-primary-checkpoints.ndjson')) -ne ($case -ceq 'canonical-fallback')) {throw 'Checkpoint launch scope changed'}
            }
            'primary-checkpoint-projection-ok'
            """, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("primary-checkpoint-projection-ok", result.Stdout);
    }

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(FindRepoRoot(), "artifacts", "canonical-timeout-implementation-20260928", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static List<JsonElement> ReadCompletePrefix(byte[] bytes)
    {
        Assert.InRange(bytes.Length, 0, 131072);
        var records = new List<JsonElement>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            Assert.InRange(i - start + 1, 1, 8192);
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes, start, i - start));
            records.Add(document.RootElement.Clone());
            Assert.Equal(records.Count, records[^1].GetProperty("sequence").GetInt32());
            start = i + 1;
        }
        Assert.InRange(records.Count, 0, 16);
        return records;
    }
}
