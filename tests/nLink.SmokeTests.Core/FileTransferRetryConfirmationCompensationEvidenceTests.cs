using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetryConfirmationCompensationEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedRetryConfirmation_ExactCompensationPreservesSharedEvidence(bool withPredecessor)
        => await RunSharedEvidenceAsync(withPredecessor, activePredecessor: false);

    [Fact]
    public async Task RejectedRetryConfirmation_CancelledSupersessionPreservesRestoredOpenAuthority()
        => await RunSharedEvidenceAsync(withPredecessor: true, activePredecessor: true);

    [Fact]
    public async Task RejectedRetryConfirmation_RestoredAuthorityClosesOnlyWithItsOwnLaterDispatch()
        => await RunSharedEvidenceAsync(withPredecessor: true, activePredecessor: true, settleRestoredPredecessor: true);

    [Fact]
    public async Task SuccessfulRetryConfirmation_ExactTerminalObservationRetiresOnlyPredecessor()
        => await RunSharedEvidenceAsync(withPredecessor: true, activePredecessor: true, successfulConfirmation: true);

    [Fact]
    public async Task SuccessfulRetryConfirmation_LateReportsPreserveExactTerminalWithoutEffects()
        => await RunSharedEvidenceAsync(withPredecessor: true, activePredecessor: true,
            successfulConfirmation: true, includeLateAttempts: true);

    [Fact]
    public async Task SuccessfulRetryConfirmation_SuccessorClosesOnlyWithItsOwnDispatchAfterTerminalRollover()
        => await RunSharedEvidenceAsync(withPredecessor: true, activePredecessor: true,
            successfulConfirmation: true, settleSuccessor: true);

    private async Task RunSharedEvidenceAsync(bool withPredecessor, bool activePredecessor,
        bool settleRestoredPredecessor = false, bool successfulConfirmation = false, bool includeLateAttempts = false,
        bool settleSuccessor = false)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-retry-confirmation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = successfulConfirmation
                ? FileTransferCoordinatorTests.CreateSuccessfulActiveRetryConfirmationSnapshotFixture(includeLateAttempts, settleSuccessor)
                : activePredecessor
                ? FileTransferCoordinatorTests.CreateRejectedActiveRetryConfirmationSnapshotFixture(settleRestoredPredecessor)
                : FileTransferCoordinatorTests.CreateRejectedRetryConfirmationSnapshotFixture(withPredecessor);
            var envelopes = source
                .Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var path = Path.Combine(directory, "envelopes.json");
            await File.WriteAllTextAsync(path,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core",
                "Support", successfulConfirmation ? "SuccessfulRetryConfirmationSnapshotEvidence.ps1" : "RetryConfirmationCompensationSnapshotEvidence.ps1"));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script,
                    [root, path, directory, settleSuccessor ? "successful_settled" : includeLateAttempts ? "successful_late" : settleRestoredPredecessor ? "active_settled" : activePredecessor ? "active" : "baseline"], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("retry-confirmation-compensation-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        catch
        {
            var failureDirectory = Path.Combine(root, "artifacts", "validation", "consolidation-bounded-20260907",
                "rca-remaining-stall", "confirmation-compensation", "failed-fixture-inputs",
                $"predecessor-{withPredecessor}-active-{activePredecessor}-settled-{settleRestoredPredecessor}-successful-{successfulConfirmation}-successor-settled-{settleSuccessor}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}");
            Directory.CreateDirectory(failureDirectory);
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Copy(file, Path.Combine(failureDirectory, Path.GetFileName(file)));
            }
            throw;
        }
        finally { TryDeleteDirectory(directory); }
    }
}
