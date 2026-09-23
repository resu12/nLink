using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferProbeRollbackRetryRetirementEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ProbeRollbackRetirement_ExactPendingAppliedSnapshotsPreserveSharedEvidence()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-probe-rollback-retirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (state, _) = FileTransferCoordinatorTests.CreateProbeRollbackRetryRetirementFixture();
            var prepared = FileTransferCoordinator.PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(state,
                FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason);
            Assert.True(prepared.Accepted, prepared.Reason);
            var committed = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(prepared.State,
                new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(prepared.Command!,
                    RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "retry_dispatch_scope_invalidation_applied"));
            Assert.True(committed.Accepted, committed.Reason);
            var envelopes = new[] { prepared.SnapshotEnvelope!, committed.SnapshotEnvelope! }
                .Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            var path = Path.Combine(directory, "envelopes.json");
            await File.WriteAllTextAsync(path, "[" + string.Join(",", envelopes.Select(e => e.ToCanonicalJson())) + "]");
            var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support",
                "ProbeRollbackRetryRetirementSnapshotEvidence.ps1"));
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [root, path, directory], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            }
        }
        catch
        {
            var output = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var retained = Path.Combine(output, "failed-evidence-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff"));
                Directory.CreateDirectory(retained);
                foreach (var path in Directory.GetFiles(directory)) { File.Copy(path, Path.Combine(retained, Path.GetFileName(path))); }
            }
            throw;
        }
        finally { TryDeleteDirectory(directory); }
    }
}
