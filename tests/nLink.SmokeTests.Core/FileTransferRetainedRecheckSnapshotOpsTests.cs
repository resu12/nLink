using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedRecheckSnapshotOpsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileTransferOps_FailedYieldRejectsRetainedRetryRecheckRebinding(bool peerFirst)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-retained-recheck-owner", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var snapshots = FileTransferCoordinatorTests.CreateFailedYieldAfterRetainedRecheckSnapshotFixture(
                peerFirst, out var freshFailedEnvelope, out var queuedRecheckEnvelope);
            var envelopes = snapshots.Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var freshFailed = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(freshFailedEnvelope);
            var queuedRecheck = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(queuedRecheckEnvelope);
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "RetainedRetryRecheckOwnerSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script,
                [repoRoot, fixturePath, tempRoot, freshFailed.TransitionId, queuedRecheck.TransitionId]);
            Assert.True(result.ExitCode == 0,
                $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }
}
