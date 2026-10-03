using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static void AssertJoinedReceiverStillFallbackForTest(SessionFileTransferService service, object context,
        RoutePublicationToken publication)
    {
        var current = CaptureJoinedNknStateForTest(service, context);
        Assert.Equal(publication, current.CurrentRoutePublicationToken);
        Assert.Null(current.PersistentState.FileTransferRouteTransition.CurrentOperation);
    }

    internal static void AssertJoinedTargetHandoffContinuityForTest(SessionFileTransferService service, object context,
        FileTransferCoordinatorState initial, IFileTransferDataSession data)
    {
        var current = CaptureJoinedNknStateForTest(service, context);
        var terminal = current.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        Assert.Equal(100, terminal.Authority.DurableCheckpoint.ContiguousCommittedChunkIndex);
        Assert.Equal(255, terminal.Authority.DurableCheckpoint.DurableHighestChunkIndex);
        Assert.Equal(initial.CurrentDataSessionIdentity, current.CurrentDataSessionIdentity);
        Assert.Equal(initial.CurrentDataSessionGeneration, current.CurrentDataSessionGeneration);
        Assert.Equal(current.CurrentDataSessionGeneration, terminal.Authority.DataSessionGeneration);
        Assert.Equal(current.CurrentDataSessionGeneration, terminal.Authority.DurableCheckpoint.DataSessionGeneration);
        Assert.Same(data, GetPrivatePropertyValue(context, "DataSession"));
    }

    internal static Task StartJoinedSparseReceiveForTest(SessionFileTransferService service, object context)
    {
        // This fixture has already opened its session. Install through the real
        // Service checks under the newly committed Coordinator publication, then
        // resume that descriptor; a fresh startup would send another session-open.
        lock (GetServiceGate(service))
        {
            var before = CreateCoordinatorStateForTest(context);
            Assert.Equal(FileTransferRoute.FileTunaV4, before.RouteSelection.Route);
            Assert.Null(GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            var staged = StageSparsePumpInstall(service, context, null, "joined_committed_receive_resume");
            Assert.True(staged.Accepted, staged.Reason);
            var committed = CommitSparsePumpInstall(service, context, staged.Install!,
                Assert.IsAssignableFrom<IFileTransferDataSession>(GetPrivatePropertyValue(context, "DataSession")));
            Assert.True(committed.Accepted, committed.Reason);
            Assert.Equal(before.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken);
        }
        return StartSparseReceiveSupervisor(service, context);
    }

    internal static async Task<object[]> AssertJoinedStaleFrontierRequestsForTestAsync(
        SessionFileTransferService sender, object outbound, SessionFileTransferService receiver, object inbound,
        IFileTransferDataSession senderData,
        Func<(FileTransferFrontierRequestFrameV6 Frame, NknBridgeChannel Lane)[]> capturedRequests,
        Action<FileTransferFrontierRequestFrameV6, NknBridgeChannel> replay)
    {
        var transferId = GetPrivateProperty<string>(outbound, "TransferId");
        await WaitUntilAsync(() => GetPrivatePropertyValue(outbound, "InstalledOutboundSparsePump") is not null &&
            (int)senderData.GetType().GetField("activeReader", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(senderData)! != 0, timeoutMs: 5000);
        var before = CaptureJoinedNknStateForTest(sender, outbound);
        var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(before);
        var install = GetPrivatePropertyValue(outbound, "InstalledOutboundSparsePump");
        Assert.Equal(FileTransferRoute.FileTunaV4, before.RouteSelection.Route);
        Assert.Null(before.CurrentRepairReplayDecision);
        var logStart = GetOperationalLogLength();
        // Publish the initial receiver frontier through the real Service. The
        // analyzer must not infer this baseline from seeded progress fields.
        Assert.IsAssignableFrom<Stream>(GetPrivatePropertyValue(inbound, "WriteStream"));
        Assert.IsType<System.Collections.BitArray>(GetPrivatePropertyValue(inbound, "ReceiverSparseChunksWritten"));
        var publishState = typeof(SessionFileTransferService).GetMethod("SendInboundV6ReceiverStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(await Assert.IsAssignableFrom<Task<bool>>(publishState.Invoke(receiver,
            [inbound, "manifest_received", true, false, null])));
        var lineage = await CreateStalledInboundRepairSuccessorForHandoffAsync(receiver, inbound, configureDestination: false);
        var requests = capturedRequests();
        Assert.NotEmpty(requests);
        var successor = GetPrivateProperty<FallbackFrontierRepairAuthority>(lineage[1], "Authority");
        var original = requests.Last(value => value.Frame.RepairRequestId == successor.ProtocolRepairRequestId);
        Assert.False(string.IsNullOrWhiteSpace(original.Frame.RequestCorrelationId));
        await WaitUntilAsync(() => Rejections().Length >= requests.Length, timeoutMs: 5000);
        var originalCopies = requests.Count(value => value.Frame.RequestCorrelationId == original.Frame.RequestCorrelationId);
        await WaitUntilAsync(() => ExactRequestRejections() == originalCopies, timeoutMs: 5000);
        AssertSenderUnchanged();
        var beforeReplay = Rejections().Length;
        // Replay the actual decoded physical request at the same transport queue
        // boundary. Do not invent a new repair request or an admission receipt.
        replay(original.Frame, original.Lane);
        await WaitUntilAsync(() => Rejections().Length == beforeReplay + 1 &&
            ExactRequestRejections() == originalCopies + 1, timeoutMs: 5000);
        AssertSenderUnchanged();
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"request_correlation_id={original.Frame.RequestCorrelationId};", log, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "filetransfer_fallback_frontier_repair_authority_granted",
            "filetransfer_fallback_frontier_repair_dispatched", "filetransfer_route_transition_prepared",
            "filetransfer_route_transition_committed", "filetransfer_v6_frontier_request_receive_dispatched" })
        {
            Assert.DoesNotContain(log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), line =>
                line.Contains($"event={forbidden};", StringComparison.Ordinal) &&
                line.Contains("direction=outbound;", StringComparison.Ordinal) &&
                line.Contains($"transfer_id={transferId};", StringComparison.Ordinal));
        }
        return lineage;

        string[] Rejections() => ReadOperationalLogTail(logStart)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                line.Contains("direction=outbound;", StringComparison.Ordinal) &&
                line.Contains("frame_type=filetransfer.frontier_request.v6;", StringComparison.Ordinal) &&
                line.Contains("reason=stale_after_live_tuna_activation", StringComparison.Ordinal)).ToArray();

        int ExactRequestRejections() => ReadOperationalLogTail(logStart)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Count(line =>
                line.Contains("event=filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided;", StringComparison.Ordinal) &&
                line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                line.Contains("direction=outbound;", StringComparison.Ordinal) &&
                line.Contains($"request_correlation_id={original.Frame.RequestCorrelationId};", StringComparison.Ordinal) &&
                line.Contains("decision_status=rejected; reason_code=bootstrap_not_current; authority_effect=none;", StringComparison.Ordinal));

        void AssertSenderUnchanged()
        {
            lock (GetServiceGate(sender))
            {
                var after = CreateCoordinatorStateForTest(outbound);
                Assert.Equal(before.PersistentState, after.PersistentState);
                Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(after));
                Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
                Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
                Assert.Equal(before.CurrentRouteCommit, after.CurrentRouteCommit);
                Assert.Equal(before.CurrentDataSessionIdentity, after.CurrentDataSessionIdentity);
                Assert.Equal(before.CurrentDataSessionGeneration, after.CurrentDataSessionGeneration);
                Assert.Equal(before.LastRepairReplayGeneration, after.LastRepairReplayGeneration);
                Assert.Null(after.CurrentRepairReplayDecision);
                Assert.Same(install, GetPrivatePropertyValue(outbound, "InstalledOutboundSparsePump"));
                Assert.Same(senderData, GetPrivatePropertyValue(outbound, "DataSession"));
                Assert.Null(GetPrivatePropertyValue(outbound, "CurrentFallbackFrontierRepairContract"));
                Assert.Empty(GetQueuedV4RepairSends(outbound));
                Assert.Equal(100, GetPrivateProperty<int>(outbound, "RemoteNextExpectedChunkIndex"));
                Assert.False(GetPrivateProperty<bool>(outbound, "IsTerminal"));
            }
        }
    }

    internal static void AssertJoinedRepairHandoffForTest(SessionFileTransferService receiver, object inbound,
        object[] lineage, string log)
    {
        lock (GetServiceGate(receiver))
        {
            AssertStalledInboundRepairLineageRetiredByHandoff(inbound, lineage,
                CreateCoordinatorStateForTest(inbound).PersistentState, log);
        }
    }
}
