using System.Text;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferFallbackRepublicationCheckpointEvidenceTests : FileTransferOpsScriptsTestBase
{
    private static readonly Lazy<Task<IReadOnlyList<string>>> Evidence = new(() =>
        SessionFileTransferV4SenderTests.CreateFallbackRepublicationCheckpointEvidenceAsync(priorCheckpoint: true));

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackRepublicationCheckpoint")]
    public async Task FallbackRepublication_OriginalCheckpointReceiptsRemainExactInBothSnapshotForms(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "nlink-republication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "original-service-evidence.log");
            await File.WriteAllLinesAsync(path, await Evidence.Value, new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot, [string]$InputPath)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FallbackRepublicationCheckpointEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("fallback-republication-checkpoint-evidence-ok", result.Stdout, StringComparison.Ordinal);
            foreach (var control in new[] { "missing_origin", "missing_retirement", "foreign_receipt", "duplicate_retirement", "repair_owner_substitutions" })
            {
                Assert.Contains("pass " + control, result.Stdout, StringComparison.Ordinal);
            }
        }
        finally { TryDeleteDirectory(directory); }
    }
}
