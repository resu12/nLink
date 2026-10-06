namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAnalyzerIntegrationTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from owner in new[] { "Wrapper", "RouteAcceptance" }
        from scenario in new[] { "valid", "missing-receiver", "no-adoption", "report-failure", "budget-rejected" }
        where owner == "Wrapper" || scenario == "valid"
        select new object[] { host, owner, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeAnalyzer_PreservesOriginalEvaluationsReportsAndFailures(string host, string owner, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-analyzer-integration-20260919", host + "-" + owner + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-prepare-" + owner + "-start-analyzer-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-analyzer-integration=" + owner + "/" + scenario, result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NativeAnalyzer_RawEvidenceScanPreservesLinesAndPathFallback(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-analyzer-integration-20260919", host + "-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            $path=Join-Path $Directory 'raw.log'
            [string[]]$lines=@('unparsed preamble','','Bridge process started (pid=42, node=n); log_writer_id=w; log_writer_sequence=1',
                '[nkn-bridge] Bridge started; log_writer_id=w; log_writer_sequence=2',
                'Bulk queue transient not-ready reason=Not connected; log_writer_id=w; log_writer_sequence=3',
                'Control queue send failed: Not connected; log_writer_id=w; log_writer_sequence=4',
                'Bridge hello_ok + pong; log_writer_id=w; log_writer_sequence=5','Connected bridge (n); log_writer_id=w; log_writer_sequence=6')
            [IO.File]::WriteAllLines($path,$lines)
            $events=@([pscustomobject]@{FilePath=$path;LineNumber=1;Sequence=101})
            $baseline=@(Get-FileTransferBridgeRecoveryStartupRawEvidence -Events $events)
            $lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            try {
                $actual=@(Get-FileTransferBridgeRecoveryStartupRawEvidence -Events $events -RawSourceLines @{$path=$lines})
                if($baseline.Count -ne 6 -or ($actual | ConvertTo-Json -Depth 8 -Compress) -cne ($baseline | ConvertTo-Json -Depth 8 -Compress) -or
                    $actual[0].LineNumber -ne 3 -or $actual[0].Sequence -ne 103) {throw 'Raw evidence scan dropped lines/positions or changed fields'}
                foreach($cache in @($null,@{'other.log'=$lines})) {
                    $failed=$false
                    try {$null=Get-FileTransferBridgeRecoveryStartupRawEvidence -Events $events -RawSourceLines $cache} catch {$failed=$true}
                    if(-not $failed) {throw 'Missing/foreign cache bypassed original path read'}
                }
            } finally {$lock.Dispose()}
            'native-raw-evidence=pass'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("native-raw-evidence=pass", result.Stdout);
    }
}
