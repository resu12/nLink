namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInboundBootstrapCheckpointOpsTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task InboundBootstrapCheckpoint_ProductionEvidenceHasFullCompactAndSharedGuiParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-inbound-bootstrap-checkpoint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var evidence = await SessionFileTransferV4SenderTests.CreateInboundBootstrapCheckpointEvidenceFixtureAsync();
            var input = Path.Combine(tempRoot, "full.log");
            await File.WriteAllTextAsync(input, evidence);
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "InboundBootstrapCheckpointEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, input]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }
}
