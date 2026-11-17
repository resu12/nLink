using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPressureSettlementGenerationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PressureSettlementGenerationDomains_ValidateCoordinatorEnvelopesAndRejectMismatches(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var artifactRoot = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
        var tempRoot = Path.Combine(artifactRoot ?? Path.GetTempPath(), "pressure-settlement-generations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
            foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
            foreach (var fileTuna in new[] { false, true })
            foreach (var independent in new[] { false, true })
            {
                envelopes.Add(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(
                    FileTransferCoordinatorTests.CreatePressureSettlementGenerationDomainsFixture(direction, fileTuna, independent)));
            }
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath, "[" + string.Join(",", envelopes.Select(e => e.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core", "Support", "PressureSettlementGenerationDomains.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixturePath], host);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "result.txt"), result.Stdout + result.Stderr);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { if (artifactRoot is null) { TryDeleteDirectory(tempRoot); } }
    }
}
