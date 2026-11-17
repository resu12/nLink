namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierFinalizerPersistenceTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "writer-only", "positive", "default", "copy-collision", "evidence-collision",
            "legacy-collision", "legacy-throw", "cancel-after", "exit-rundown" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeFinalizer_PersistsSeparateEvidenceAndPreservesLegacyFinalization(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-finalizer-persistence-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCleanupAdoption.ps1"));
        var appCase = scenario is "positive" or "exit-rundown" ? scenario : "writer-only";
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, appCase, "persistence-" + scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-finalizer-source=persistence-" + scenario, result.Stdout);
    }
}
