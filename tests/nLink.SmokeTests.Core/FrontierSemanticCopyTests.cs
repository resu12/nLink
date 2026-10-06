namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierSemanticCopyTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SemanticCopy_BoundedRetainedHandlePrimitive(string host)
        => await RunAsync(host, "primitive", true);

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "legacy-isolation", "empty", "early", "missing-cursor", "active-work", "copied-binding", "copied-copy",
            "mutable-result", "collision", "hash", "lines", "closed-output", "closed-copy", "repeat", "cancel", "complete", "budget", "cursor-dispose" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SemanticCopy_RequiresOriginalCursorAndCopyOperation(string host, string scenario)
        => await RunAsync(host, scenario, false);

    private async Task RunAsync(string host, string scenario, bool primitive)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-semantic-copy-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support",
            primitive ? "FrontierSemanticCopy.ps1" : "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, primitive ? [root, directory] :
            [root, directory, "cursor-copy-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains(primitive ? "frontier-semantic-copy=" : "frontier-gui-launch=", result.Stdout);
    }
}
