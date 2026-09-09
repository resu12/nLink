using System.Text;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferCheckpointRepairContinuityEvidenceTests : FileTransferOpsScriptsTestBase
{
    private static readonly Lazy<Task<IReadOnlyList<string>>> Evidence = new(
        SessionFileTransferV4SenderTests.CreateCheckpointRepairContinuityEvidenceFixtureAsync);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task CheckpointRepairContinuity_ProductionGeneratedEvidenceRemainsExactInAllConsumers(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-checkpoint-continuity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "service-evidence.log");
        try
        {
            await File.WriteAllLinesAsync(path, await Evidence.Value, new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot, [string]$InputPath)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/CheckpointRepairContinuityEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
                """;
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, path], host);
            Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("pass exact", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("pass source_aba", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("pass forged_effect", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("checkpoint-continuity-evidence-ok", result.Stdout, StringComparison.Ordinal);
        }
        catch
        {
            if (File.Exists(path))
            {
                var failureDirectory = Path.Combine(repoRoot, "artifacts", "validation", "consolidation-bounded-20260907",
                    "implementation-checkpoint-repair-continuity", "failed-fixture-inputs");
                Directory.CreateDirectory(failureDirectory);
                var failurePath = Path.Combine(failureDirectory,
                    $"service-evidence-{host}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.log");
                File.Copy(path, failurePath);
            }
            throw;
        }
        finally { TryDeleteDirectory(directory); }
    }
}
