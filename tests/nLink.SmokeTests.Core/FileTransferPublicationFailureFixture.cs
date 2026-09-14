using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static void ProducePublicationFailureFixture(string sessionId, string transferId,
        Func<FileTransferPublicationUseEvidence, string> formatEnvelopeEvidence)
    {
        // An explicitly seeded offline data-path mismatch. Coordinator states,
        // publication receipt, snapshot serialization and logger are genuine;
        // the bad transport observation is test-authored, not a live defect.
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var initial = EmptyState(FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast), FileTransferDirection.Outbound) with
        {
            SessionId = sessionId, TransferId = transferId,
            PersistentState = new FileTransferCoordinatorPersistentState { CoordinatorInstanceId = Guid.NewGuid().ToString("N") },
            CurrentLiveRouteEpoch = FileTransferCoordinator.StartLiveRouteEpoch(0, route,
                FileTransferTransportHandoffKind.None, FileTransferTransportKind.Tuna, "offline_failure_fixture"),
            LastLiveRouteEpochId = 1,
        };
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, route,
            targetTransport: FileTransferTransportKind.Tuna, initialTunaReadinessEvidence: InitialTunaEvidence(initial)), initial);
        Assert.True(started.InitialTunaRouteSelectionDecision?.Accepted, started.InitialTunaRouteSelectionDecision?.Reason);
        Assert.NotNull(started.State.CurrentRuntimeOwner);
        var state = started.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var published = FileTransferCoordinator.AuthorizeAction(state,
            new FileTransferCoordinatorActionRequest(FileTransferCoordinatorActionKind.RoutePublication,
                Source: "offline_failure_fixture", Reason: "route_publication", RuntimeOwner: owner,
                Route: route.Route, ProtocolVersion: route.ProtocolVersion, LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation, RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: state.TerminalGeneration));
        Assert.True(published.Accepted, published.RejectionReason);
        var token = published.State.CurrentRoutePublicationToken!.Value;
        var envelope = published.SnapshotEnvelope!;
        var publication = Assert.Single(envelope.Aggregate.Operations, operation => operation.OperationRole == "route_publication");

        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Drain the actual serialized emitter queue into the actual logger in
        // deterministic order, keeping all writer metadata logger-owned.
        type.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        var queue = (ConcurrentQueue<string>)type.GetField("pending", flags)!.GetValue(emitter)!;
        void Drain() { while (queue.TryDequeue(out var line)) { LocalOperationalLog.Info("SessionFileTransfer", line); } }
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [started.SnapshotEnvelope!]);
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [envelope]);
        type.GetMethod("EnqueueAuthorityEffect", flags)!.Invoke(emitter,
            [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, FileTransferDirection.Outbound,
                envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
        var evidence = Assert.IsType<FileTransferRoutePublicationEvidence>(type.GetMethod("GetRoutePublicationEvidence", flags)!
            .Invoke(emitter, [token]));
        Drain();
        var use = new FileTransferPublicationUseEvidence(evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
        LocalOperationalLog.Info("SessionSecurity",
            $"event=filetransfer_data_path_send_result; session_id={token.SessionId}; transfer_id={token.TransferId}; direction=outbound; " +
            $"route={token.RouteToken}; protocol_version={token.ProtocolVersion}; live_route_epoch={token.LiveRouteEpoch}; " +
            $"runtime_generation={token.RuntimeGeneration}; terminal_generation={token.TerminalGeneration}; leg_generation={token.LegGeneration}; " +
            $"route_publication_generation={token.PublicationGeneration}; route_commit_generation={token.RouteCommitGeneration ?? 0}; " +
            "target_transport=tuna; actual_transport=regular_nkn; delivery_transport=regular_nkn; frame_type=filetransfer.chunk_batch.v4; " +
            "raw_payload_bytes=2; batch_chunk_count=1; batch_start_chunk=0; batch_end_chunk_exclusive=1; delivery_result=sent; " +
            "offline_fixture_fault=wrong_physical_transport" + formatEnvelopeEvidence(use));
        type.GetMethod("Complete", flags)!.Invoke(emitter,
            [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, FileTransferDirection.Outbound]);
        Drain();
    }
}

public sealed partial class NknFileTransferTransportTests
{
    internal static async Task ProducePublicationFailureFixture()
        => await WithSecurityCaptureSession((sender, _, _, _, _) =>
        {
            // Real approval/installation and payload capture on FakeNknClient.
            // The envelope is never sent: only the final mismatch observation
            // is seeded, with genuine provenance from these production codecs.
            var payload = EncryptWithSecurityEvidence(sender, [17, 34]);
            var sessionId = Assert.IsType<string>(SecurityProperty(payload.Evidence, "SessionId"));
            var transferId = Assert.IsType<string>(SecurityProperty(payload.Evidence, "TransferId"));
            var envelope = new Envelope(1, sessionId, Guid.NewGuid().ToString("N"),
                MsgType.FileTransferDataFrame, payload.Bytes, 0, null);
            FileTransferCoordinatorTests.ProducePublicationFailureFixture(sessionId, transferId, use =>
            {
                var evidence = SecurityMethod("CreateFileTransferDataPathEnvelopeEvidence").Invoke(null,
                    [envelope.MessageId, sessionId, transferId])!;
                evidence.GetType().GetProperty("SecurityEvidence")!.SetValue(evidence, payload.Evidence);
                evidence.GetType().GetProperty("PublicationUse")!.SetValue(evidence, use);
                return (string)SecurityMethod("FormatFileTransferDataPathEnvelopeEvidenceSuffix").Invoke(null, [evidence])!;
            });
            return Task.CompletedTask;
        });
}
