using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferBootstrapCorrelationEvidenceTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("-unknown", false)]
    public void BootstrapInitiation_RedactorPreservesOnlyTheExplicitProfile(string suffix, bool preserved)
    {
        var profile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1" + suffix;
        var line = NLink.Core.Logging.SensitiveDataRedactor.Redact($"event=filetransfer_coordinator_transition_snapshot; snapshot_catalog_version={profile}; reason=test");
        Assert.Equal(preserved, line.Contains($"snapshot_catalog_version={profile};", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, "sidecar_tuna_write_failed")]
    [InlineData(true, false, "sidecar_tuna_write_failed")]
    [InlineData(false, false, "sidecar_tuna_stream_eof")]
    [InlineData(true, false, "sidecar_tuna_stream_eof")]
    public async Task BootstrapInitiation_SourceHoldHasFullCompactGuiParity(bool outbound, bool peerEvidence, string? failureReason)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "initiation-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source-hold.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapInitiationSnapshotFixture(outbound, peerEvidence,
            failureReason: failureReason)), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapInitiationEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("bootstrap-initiation-normalized=", StringComparison.Ordinal)));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapInitiationSnapshotFixture(bool outbound, bool peerEvidence,
        bool includeCallback = false, string? failureReason = null)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var pair = CreateCorrelatedBootstrapPair(capture: Capture);
        var state = outbound ? pair.Sender : pair.Receiver;
        var peerWake = peerEvidence ? StartCorrelated(outbound ? pair.Receiver : pair.Sender).Command!.Frame : null;
        var leg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(state.CurrentRuntimeOwner!.Value,
            state.CurrentRoutePublicationToken!.Value, state.TerminalGeneration, leg.Generation, leg.TransportEpochId,
            leg.BridgeRecoveryGeneration, state.CurrentDataSessionGeneration, 3, 4, 6, 128, 2);
        var observation = new FileTransferFallbackV6ProofBootstrapObservation(state.SessionId, state.TransferId, state.Direction,
            1, 1, peerEvidence ? "correlated_bootstrap_peer_frame" : failureReason ?? "runtime_disabled",
            peerEvidence ? outbound ? "file_transfer_v6_state_frame_received" : "file_transfer_v6_frontier_request_frame_received"
                : failureReason is null ? "local_runtime_disabled" : "local_transport_failure", 10000, checkpoint)
        {
            InitiationKind = peerEvidence ? FileTransferBootstrapInitiationKind.PeerEvidence
                : failureReason is null ? FileTransferBootstrapInitiationKind.LocalRuntimeDisable : FileTransferBootstrapInitiationKind.LocalTransportFailure,
            LocalStop = peerEvidence ? null : new(BootstrapStopScopeForTests(state), state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration)
                { FailureOrigin = failureReason is null ? null : new(Guid.NewGuid(), 1, "fault-snapshot-run", 1) },
        };
        if (peerEvidence)
        {
            var rejectedOrigin = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state,
                observation with { Source = "tuna_unavailable_before_send", ProofKind = "file_transfer_v4_state_frame_sent" }, peerWake);
            Assert.False(rejectedOrigin.Accepted);
            Assert.Same(state, rejectedOrigin.State);
            Assert.All(rejectedOrigin.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
            Capture(rejectedOrigin.SnapshotEnvelope);
        }
        state = includeCallback ? ApplyCallbackBootstrapSourceHoldForTests(state, observation, peerWake, Capture)
            : ApplyEmptyBootstrapSourceHoldForTests(state, observation, peerWake, Capture);
        var issued = FileTransferCoordinator.StartFallbackBootstrapExchange(state, observation,
            new() { SessionId=state.SessionId, TransferId=state.TransferId, Epoch=3, ContiguousCommittedChunkIndex=4,
                DurableReceivedHighestChunkIndex=6, CreditUntilChunkIndexExclusive=4, BytesCommitted=4096,
                TransferPaused=true, TransferPauseReason=FileTransferBootstrapCorrelation.RecoveryMode },
            peerWake as FileTransferFallbackBootstrapRequestFrameV6);
        Assert.True(issued.Accepted, issued.Reason);
        Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(issued.SnapshotEnvelope));
        var hold = issued.State.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
        var rejected = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(issued.State,
            hold.Capture! with { PauseInstance = hold.Capture!.PauseInstance + 1 });
        Assert.False(rejected.Accepted);
        Assert.Same(issued.State, rejected.State);
        Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(rejected.SnapshotEnvelope));
        var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(issued.State, "test_source_scope_retired");
        Assert.True(invalidated.Accepted);
        Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(invalidated.SnapshotEnvelope));
        var late = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(invalidated.State,
            CorrelatedReceipt(issued.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered));
        Assert.False(late.Accepted);
        Assert.Same(invalidated.State, late.State);
        Assert.All(late.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        Capture(late.SnapshotEnvelope);
        return envelopes;

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            if ((envelope.Direction == "outbound") == outbound) { envelopes.Add(envelope); }
        }
    }
}
