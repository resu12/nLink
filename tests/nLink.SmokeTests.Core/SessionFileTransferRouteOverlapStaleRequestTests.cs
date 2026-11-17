using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "CrossPeerFallbackOverlap")]
    public async Task CommittedTunaSender_TwoDeliveredOldFallbackRequestsCannotCreateRepairAuthority()
    {
        const string transferId = "transfer_tuna_sender_old_fallback_request_overlap";
        const string sessionId = "session_" + transferId;
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateFileTunaV4OutboundContext(
            transferId, remoteNextExpectedChunkIndex: 120,
            chunksAcceptedForTransport: 160, remoteGrantedUntilExclusive: 120,
            chunkCount: 256, startCoordinatorSnapshotStream: false);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));

        // This established fixture supplies a current committed-Tuna baseline,
        // not generated cross-peer commit history. The real receive/admission
        // path below is the subject; the receiver regression owns real commit.
        SeedRecoveredFileTunaV4SecondCycleScopeForTest(context);
        SeedRecoveredFileTunaV4SecondCycleScopeForTest(context);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        StartCoordinatorSnapshotStreamForTest(context);
        var committed = CreateCoordinatorStateForTest(context);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.RouteSelection.Route);
        Assert.Equal(4, committed.RouteSelection.ProtocolVersion);
        Assert.Equal(3, committed.CurrentLiveRouteEpoch!.EpochId);
        Assert.NotNull(committed.CurrentRoutePublicationToken);
        Assert.NotNull(committed.CurrentRouteCommit);
        var sourceDataIdentity = committed.CurrentDataSessionIdentity;
        var sourceDataGeneration = committed.CurrentDataSessionGeneration;
        Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(committed),
            committed.PersistentState.SnapshotStateFingerprint);
        var receiveLogStart = GetOperationalLogLength();

        // Keep the real receive supervisor running after normal pump completion;
        // this test concerns admission, not unrelated Tuna payload throughput.
        var previousCompletion = SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests;
        var completeNormalPump = 1;
        SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = (id, _) =>
            id == transferId && Interlocked.Exchange(ref completeNormalPump, 0) == 1;
        var supervisor = StartSparseReceiveSupervisor(service, context);
        try
        {
            await WaitUntilAsync(() => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null &&
                ReadV4SenderLogSnapshot(receiveLogStart).Contains(
                    "event=filetransfer_v4_receive_loop_kept_alive_after_sender_pump_completed;",
                    StringComparison.Ordinal), timeoutMs: 5000);
            FileTransferCoordinatorState before;
            string beforeFingerprint;
            object? install;
            lock (GetServiceGate(service))
            {
                before = CreateCoordinatorStateForTest(context);
                beforeFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(before);
                install = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
                Assert.Null(before.CurrentRepairReplayDecision);
                Assert.Empty(GetQueuedV4RepairSends(context));
            }
            const string correlation = "frq-cross-peer-old-fallback-copy";
            var oldRequest = new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId, TransferId = transferId,
                TransportEpoch = 1, RepairRequestId = "v6-frontier:120:18",
                RequestCorrelationId = correlation,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 120, ChunkCount = 1 }],
                Priority = "frontier", RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            };
            var rejectionLogStart = GetOperationalLogLength();
            var receiveOrdinal = transport.GetDataSessionReceiveStateForTests(transferId).Ordinal;
            for (var copy = 1; copy <= 2; copy++)
            {
                transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
                transport.ReceiveDeliveredDataFrame(oldRequest with { });
                await WaitUntilAsync(() =>
                {
                    var lines = RejectionLines();
                    return lines.Count(line =>
                        line.Contains("event=filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided;", StringComparison.Ordinal) &&
                        line.Contains($"request_correlation_id={correlation};", StringComparison.Ordinal) &&
                        line.Contains("decision_status=rejected; reason_code=bootstrap_not_current; authority_effect=none;", StringComparison.Ordinal)) == copy &&
                        lines.Count(line => line.Contains("reason=stale_after_live_tuna_activation", StringComparison.Ordinal)) == copy &&
                        lines.Count(line => line.Contains("frame_type=filetransfer.frontier_request.v6; chunk_index=(none); reason=protocol_not_v4", StringComparison.Ordinal)) == copy &&
                        transport.GetDataSessionReceiveStateForTests(transferId).Ordinal >= receiveOrdinal + copy;
                }, timeoutMs: 5000);
            }
            var rejected = RejectionLines();
            Assert.Equal(2, rejected.Count(line => line.Contains(
                "reason=stale_after_live_tuna_activation", StringComparison.Ordinal)));
            Assert.Equal(2, rejected.Count(line => line.Contains(
                "frame_type=filetransfer.frontier_request.v6; chunk_index=(none); reason=protocol_not_v4", StringComparison.Ordinal)));
            lock (GetServiceGate(service))
            {
                var after = CreateCoordinatorStateForTest(context);
                Assert.Equal(before.PersistentState, after.PersistentState);
                Assert.Equal(before.PersistentState.SnapshotStateGeneration, after.PersistentState.SnapshotStateGeneration);
                Assert.Equal(before.PersistentState.SnapshotStateFingerprint, after.PersistentState.SnapshotStateFingerprint);
                Assert.Equal(beforeFingerprint,
                    FileTransferCoordinator.CalculateSnapshotStateFingerprint(after));
                Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
                Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
                Assert.Equal(before.CurrentRouteCommit, after.CurrentRouteCommit);
                Assert.Equal(before.CurrentRepairReplayDecision, after.CurrentRepairReplayDecision);
                Assert.Equal(before.LastRepairReplayGeneration, after.LastRepairReplayGeneration);
                Assert.Same(install, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
                Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
                Assert.Equal(sourceDataIdentity, after.CurrentDataSessionIdentity);
                Assert.Equal(sourceDataGeneration, after.CurrentDataSessionGeneration);
                Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
                Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
                Assert.Null(GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
                Assert.Empty(GetQueuedV4RepairSends(context));
                Assert.Equal(120, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            }
            foreach (var forbidden in new[] { "filetransfer_fallback_frontier_repair_authority_granted",
                "filetransfer_fallback_frontier_repair_dispatched", "filetransfer_route_transition_prepared",
                "filetransfer_route_transition_committed", "filetransfer_v6_frontier_request_receive_dispatched" })
            {
                Assert.DoesNotContain(rejected, line => line.Contains($"event={forbidden};", StringComparison.Ordinal));
            }
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));

            string[] RejectionLines() => ReadOperationalLogTail(rejectionLogStart)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal))
                .ToArray();
        }
        finally
        {
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = previousCompletion;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
