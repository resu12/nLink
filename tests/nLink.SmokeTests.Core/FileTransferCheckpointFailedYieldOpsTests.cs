using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferCheckpointFailedYieldOpsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    public async Task FileTransferOps_CheckpointFailedPeerFirstYieldUsesExactSharedHistory(
        FileTransferDirection direction, bool priorTerminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-checkpoint-failed-yield", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var snapshots = FileTransferCoordinatorTests.CreateCheckpointFailedYieldSnapshotFixture(
                direction, priorTerminal, out var checkpointEnvelope, out var failedEnvelope,
                out var priorTerminalEnvelope);
            var envelopes = snapshots.Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var checkpoint = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(checkpointEnvelope);
            var failed = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(failedEnvelope);
            var terminalTransitionId = priorTerminalEnvelope is null ? "(none)" :
                SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(priorTerminalEnvelope).TransitionId;
            var fixturePath = Path.Combine(tempRoot, "envelopes.json");
            await File.WriteAllTextAsync(fixturePath,
                "[" + string.Join(",", envelopes.Select(envelope => envelope.ToCanonicalJson())) + "]");
            var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "CheckpointFailedYieldSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script,
                [repoRoot, fixturePath, tempRoot, checkpoint.TransitionId, failed.TransitionId, terminalTransitionId,
                    (direction == FileTransferDirection.Outbound && priorTerminal).ToString()]);
            Assert.True(result.ExitCode == 0,
                $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }
}
