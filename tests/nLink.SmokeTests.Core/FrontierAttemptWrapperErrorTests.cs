namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptWrapperErrorTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "primary-missing", "primary-unreadable", "primary-readable", "primary-probe-error", "primary-cleanup-error",
            "deadline-missing", "gate-missing", "gate-unreadable", "gate-readable", "gate-cleanup-error",
            "success", "success-cleanup-error", "expected-argv", "listener-handled", "listener-cleanup-error" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalWrapperError_SurvivesDiagnosticsAndCleanup(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "wrapper-first-error-controls", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptWrapperErrorControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("wrapper-first-error-control=" + scenario, result.Stdout);
    }

    public static IEnumerable<object[]> DeadlineCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "request", "state-unreadable", "request-failure", "exit-before", "scenario-race", "grace-exit",
            "hard-race", "hard-timeout", "loop-request-failure", "descendants", "descendant-reused", "metadata-failure" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(DeadlineCases))]
    public async Task OriginalDeadlineContract_PreservesRequestGraceAndBoundedCleanup(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "wrapper-deadline-controls", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDeadlineControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("wrapper-deadline-control=" + scenario, result.Stdout);
    }
}
