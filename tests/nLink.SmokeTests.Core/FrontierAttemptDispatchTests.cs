namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptDispatchTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "argv-changed", "environment-changed", "cancel-before",
            "parent-cancelled", "wrong-pid", "wrong-creation", "tampered-request", "gui-nonzero", "gui-killed" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalAttempt_BindsHeadlessDispatchWithoutLivePromotion(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-dispatch-20260926", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExposeAttemptDispatch(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            if($null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('TryBindAttemptForTest') -or
                $null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptDispatchForTest' -as [type])) {throw 'Test API leaked into default loader'}
            $rejected=$false
            try {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot}
            catch {if($_.Exception.Message -cne 'attempt_dispatch_profile_already_loaded') {throw}; $rejected=$true}
            if(-not $rejected) {throw 'Mixed profile admitted'}
            'attempt-default-guard-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-default-guard-ok", result.Stdout);
    }
}
