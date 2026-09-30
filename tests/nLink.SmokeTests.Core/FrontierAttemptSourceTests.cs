namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierAttemptSourceTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "writer-source-valid", "writer-source-cancel", "writer-source-closed",
            "writer-source-dispose", "writer-source-size", "writer-source-open-writer" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalAttempt_HoldsPreSendSourceThroughWriterClosure(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-source-20260926", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        var refused = scenario is "writer-source-size" or "writer-source-open-writer";
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains(refused ? "attempt-source-refusal-ok" : "attempt-source-custody-ok", output);
        Assert.Contains("attempt-role-writers-ok", output);
        if (!refused)
        {
            var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
            Assert.Equal("268435456", measurement[0]);
            Assert.Matches("^[0-9a-f]{64}$", measurement[1]);
            Assert.NotEmpty(measurement[2]);
            // Independent post-release corroboration only, not ownership input.
            await using var file = File.OpenRead(Path.Combine(directory, "filetransfer-tuna-gui-payload.bin"));
            Assert.Equal(268435456L, file.Length);
            var digest = await System.Security.Cryptography.SHA256.HashDataAsync(file);
            Assert.Equal(measurement[1], Convert.ToHexString(digest).ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExposeTestSourceCustody(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource' -as [type]
            if($null -eq $type -or $null -ne $type.GetMethod('TryBindClosedWritersForTest') -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch].GetMethod('TryHoldSourceForTest')) {
                throw 'Test source custody leaked or production source missing'
            }
            'source-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("source-default-ok", result.Stdout);
    }
}
