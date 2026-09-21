using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackCheckpointReceiveEvidence")]
    public async Task FallbackCheckpoint_ReceiveBoundStateHasWireDiagnosticButNoPeerAcceptanceReceipt()
    {
        var lines = await CreateFallbackRepublicationCheckpointEvidenceAsync(priorCheckpoint: true, receiveBoundState: true);
        var acceptances = lines.Where(line =>
                line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal))
            .Select(line =>
            {
                var payload = GetDelimitedLogField(line, "snapshot_payload").Replace('-', '+').Replace('_', '/');
                using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                return json.RootElement.Clone();
            })
            .Where(envelope => envelope.GetProperty("direction").GetString() == "outbound" &&
                envelope.GetProperty("decisionKind").GetString() == "FallbackCheckpointAccepted").ToArray();
        Assert.Equal(2, acceptances.Length);
        Assert.All(acceptances, envelope =>
        {
            Assert.Equal("Accepted", envelope.GetProperty("decisionStatus").GetString());
            Assert.Equal("receiver_state_sparse_runtime", envelope.GetProperty("decisionReason").GetString());
            // Lock down the current evidence gap, not a passing peer-join rule.
            Assert.Empty(envelope.GetProperty("aggregate").GetProperty("operations").EnumerateArray());
        });
    }

    private static async Task ApplyReceiveBoundCheckpointForEvidenceAsync(
        SessionFileTransferService service, object context, IFileTransferDataSession dataSession,
        FileTransferReceiverStateFrameV6 proof)
    {
        var type = typeof(SessionFileTransferService);
        object install;
        lock (GetServiceGate(service))
        {
            // Stage/commit a real local installation from the current coordinator
            // tokens. No fabricated install or publication is assigned to context.
            var staged = StageSparsePumpInstall(service, context,
                GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"), "checkpoint_receive_evidence");
            Assert.True(staged.Accepted, staged.Reason);
            var committed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
            Assert.True(committed.Accepted, committed.Reason);
            install = committed.Install!;
        }
        var source = CreateCoordinatorStateForTest(context);
        Assert.NotNull(source.CurrentFallbackCheckpointLease);
        var stateFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(source);
        var stateCount = GetPrivateProperty<int>(context, "PullV4StateReceivedCountTotal");
        var remoteFrontier = GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex");
        var apply = type.GetMethod("ApplyOutboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var logStart = GetOperationalLogLength();

        // These are invalid incoming continuation arguments, not mutations of
        // the actual owner/data session. Each must reject before accepting proof.
        using var foreignData = new CompletingDataSession(dataSession.SessionId, dataSession.TransferId);
        object?[][] invalid =
        [
            [context, proof, null, source.CurrentRuntimeOwner, dataSession, source.CurrentDataSessionGeneration],
            [context, proof, install, source.CurrentRuntimeOwner!.Value with { RuntimeGeneration = source.CurrentRuntimeOwner.Value.RuntimeGeneration + 1 }, dataSession, source.CurrentDataSessionGeneration],
            [context, proof, install, source.CurrentRuntimeOwner, foreignData, source.CurrentDataSessionGeneration],
            [context, proof, install, source.CurrentRuntimeOwner, dataSession, source.CurrentDataSessionGeneration + 1],
        ];
        foreach (var args in invalid)
        {
            var task = Assert.IsAssignableFrom<Task>(apply.Invoke(service, args));
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.Equal("Rejected", GetPrivatePropertyValue(result, "Status")?.ToString());
            Assert.Equal("state_receive_scope_stale", GetPrivatePropertyValue(result, "Reason"));
            Assert.Equal(stateFingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(context)));
            Assert.Equal(stateCount, GetPrivateProperty<int>(context, "PullV4StateReceivedCountTotal"));
            Assert.Equal(remoteFrontier, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        }
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_accepted;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);

        // Exercise the production receive-dispatch entry with its captured
        // install/owner/session tuple. The queue is real; the supervisor loop and
        // encrypted transport are not started by this bounded evidence fixture.
        var acceptedTask = Assert.IsAssignableFrom<Task>(apply.Invoke(service,
            [context, proof, install, source.CurrentRuntimeOwner, dataSession, source.CurrentDataSessionGeneration]));
        await acceptedTask;
        var accepted = acceptedTask.GetType().GetProperty("Result")!.GetValue(acceptedTask)!;
        Assert.Equal("Applied", GetPrivatePropertyValue(accepted, "Status")?.ToString());
        Assert.Null(CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);
        Assert.Equal(source.CurrentRuntimeOwner, CreateCoordinatorStateForTest(context).CurrentRuntimeOwner);
        Assert.Equal(source.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken);
        Assert.Equal(stateCount + 1, GetPrivateProperty<int>(context, "PullV4StateReceivedCountTotal"));
        var fingerprint = Assert.IsType<string>(type.GetMethod("CreateFileTransferV4StateWireFingerprint",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [proof]));
        var receivedLine = Assert.Single(ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("event=filetransfer_v4_state_received;", StringComparison.Ordinal));
        Assert.Equal(fingerprint, GetDelimitedLogField(receivedLine, "v4_state_wire_fingerprint"));
        Assert.Equal("1", GetDelimitedLogField(receivedLine, "applied"));
    }
}
