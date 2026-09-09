using System.Text;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferCheckpointRepairContinuityEvidenceTests : FileTransferOpsScriptsTestBase
{
    private static readonly Lazy<Task<IReadOnlyList<string>>> Evidence = new(
        SessionFileTransferV4SenderTests.CreateCheckpointRepairContinuityEvidenceFixtureAsync);

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    [Trait("OpsSuite", "CoordinatorSnapshots")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task CheckpointRepairContinuity_CommittedRetirementHasExactCrossHostConsumerParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var envelopes = FileTransferCoordinatorTests.CreateCheckpointRepairContinuityCommittedRetirementFixture();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-checkpoint-retirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "committed-retirement.log");
        try
        {
            // Reuse the production stream/projector adapter. Its receipt-backed
            // effect fixtures do not assert that physical callbacks ran live.
            await File.WriteAllLinesAsync(path, FileTransferCheckpointResponderCoexistenceEvidenceTests.Emit(envelopes),
                new UTF8Encoding(false));
            const string script = """
                param([string]$RepoRoot, [string]$InputPath)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/CheckpointRepairContinuityEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath -CommittedRetirement
                """;
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [repoRoot, path], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                Assert.Contains("checkpoint-committed-retirement-evidence-ok", result.Stdout, StringComparison.Ordinal);
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("checkpoint-committed-retirement-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        catch
        {
            if (File.Exists(path))
            {
                var failureDirectory = Path.Combine(repoRoot, "artifacts", "validation", "consolidation-bounded-20260907",
                    "rca-continuity-retirement-committed", "failed-fixture-inputs");
                Directory.CreateDirectory(failureDirectory);
                File.Copy(path, Path.Combine(failureDirectory, $"committed-retirement-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.log"));
            }
            throw;
        }
        finally { TryDeleteDirectory(directory); }
    }

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
