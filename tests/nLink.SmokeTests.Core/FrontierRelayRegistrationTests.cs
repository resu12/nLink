namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierRelayRegistrationTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[]
        {
            "valid", "guards", "missing", "map-collision", "event-collision",
            "request-id", "scope-body", "wrapper-incarnation", "parent-incarnation",
            "version", "padding", "nonce", "channel-state", "channel-regression", "legacy-regression",
            "request-dispose", "registration-dispose", "relay-dispose", "handoff-dispose", "early-take",
            "peer-exit", "legacy-forward", "legacy-after-dispose"
        }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeRelayRegistration_BindsOnlyOriginalPeersWithoutGrantingCustody(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-relay-registration-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierRelayRegistration.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-relay-registration=" + scenario, result.Stdout);
    }
}
