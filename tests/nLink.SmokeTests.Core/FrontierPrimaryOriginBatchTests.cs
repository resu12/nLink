namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPrimaryOriginBatchTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        new[] { new object[] { "powershell", "normal" }, new object[] { "pwsh", "normal" },
            new object[] { "powershell", "valid" }, new object[] { "pwsh", "valid" },
            new object[] { "pwsh", "verified-budget" } }
        .Concat(from phase in new[] { "before", "during", "closing", "after" }
                from fault in new[] { "scope", "facts", "pin", "cancel", "dispose", "budget" }
                select new object[] { "pwsh", phase + "-" + fault });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeBatch_PreservesEveryOriginAndPublishesAllOrNothing(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "primary-origin-batch-20260927", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-origin-batch-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-primary-origin-batch=" + scenario, result.Stdout);
    }
}
