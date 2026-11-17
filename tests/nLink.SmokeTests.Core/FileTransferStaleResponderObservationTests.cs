using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferStaleResponderObservationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task InitialResponder_StaleAvailabilityAfterDispositionIsDiagnosticOnly(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repo = FindRepoRoot();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")
            ?? Path.GetTempPath(), "stale-initial-responder-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "receiver.log");
            await File.WriteAllTextAsync(input,
                await SessionFileTransferV4SenderTests.CreateInitialStaleResponderEvidenceAsync());
            var script = await File.ReadAllTextAsync(Path.Combine(repo, "tests", "nLink.SmokeTests.Core",
                "Support", "InitialStaleResponderEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [repo, input, root], host);
            await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result.Stdout + result.Stderr);
            Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}\nSTDERR:{result.Stderr}");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR") is null)
            {
                TryDeleteDirectory(root);
            }
        }
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task<string> CreateInitialStaleResponderEvidenceAsync()
    {
        const string transfer = "initial_stale_responder_evidence";
        using var service = new SessionFileTransferService();
        using var data = new InitialBootstrapRecordingDataSession("session_" + transfer, transfer);
        using var bridge = new InitialBootstrapCheckpointTransport(data);
        var handler = typeof(SessionFileTransferService).GetMethod("HandleDataSessionAvailabilityChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var logStart = GetOperationalLogLength();
        RuntimeUnlockRouteCommitSnapshot? observed = null;
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, data,
            receiverBarrier: true, afterPreparation: (inbound, _) =>
            {
                var operation = CreateCoordinatorStateForTest(inbound).PersistentState.FileTransferRouteTransition.CurrentOperation!.Value;
                var proof = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(operation.Authority.ProofBoundary);
                observed = bridge.RuntimeUnlockRouteCommitSnapshot! with
                {
                    ResponderPeerProbeRouteTransitionObservation = proof.InitialResponderPeerProbeObservation,
                };
                // Capture the real service acceptance while the prepared initial
                // handoff is still current, before its physical drain completes.
                var task = Assert.IsAssignableFrom<Task>(handler.Invoke(service, [data, Availability(true)]));
                Assert.True(task.IsCompletedSuccessfully);
            });
        try
        {
            Assert.NotNull(observed?.ResponderPeerProbeRouteTransitionObservation);
            AssertSecondTunaCallbackPreservesRuntimeAuthority(service, context, bridge, data, () =>
            {
                var task = Assert.IsAssignableFrom<Task>(handler.Invoke(service, [data, Availability(false)]));
                Assert.True(task.IsCompletedSuccessfully);
            });
            Assert.Contains("decision_reason=data_session_availability_stale;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            TryCancelContextLifetime(context);
        }
        service.Dispose();
        var identity = GetPrivateProperty<string>(context, "CoordinatorInstanceId");
        var evidenceIdentity = "fte1-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal) &&
            line.Contains("coordinator_instance_id=" + evidenceIdentity + ";", StringComparison.Ordinal)), 5000);
        return string.Join('\n', ReadOperationalLogTail(logStart).Split('\n').Where(line =>
            line.Contains("coordinator_instance_id=" + evidenceIdentity + ";", StringComparison.Ordinal)));

        FileTransferDataSessionAvailabilityChangedEventArgs Availability(bool available)
            => new(available, "initial_responder_peer_probe_acked", true,
                FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, observed);
    }
}
