using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRepublicationCheckpoint")]
    public async Task FallbackRepublication_PeerCheckpointBelongsToNewOwnerNotStalledRepairs(bool priorCheckpoint)
        => await CreateFallbackRepublicationCheckpointEvidenceAsync(priorCheckpoint);

    internal static async Task<IReadOnlyList<string>> CreateFallbackRepublicationCheckpointEvidenceAsync(
        bool priorCheckpoint, bool receiveBoundState = false)
    {
        const string transferId = "republication_checkpoint";
        var sessionId = "session_" + transferId;
        var type = typeof(SessionFileTransferService);
        using var senderTransport = new PeerVisibleControlPlaneLoopbackTransport(sessionId);
        using var receiverTransport = new PeerVisibleControlPlaneLoopbackTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var outbound = CreatePostTunaFallbackV6OutboundContext(transferId, startCoordinatorSnapshotStream: false);
        var inbound = CreatePostTunaFallbackV6InboundContext(transferId);
        var senderData = await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        var receiverData = await receiverTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        SetPrivateProperty(outbound, "DataSession", senderData);
        SetPrivateProperty(inbound, "DataSession", receiverData);
        SetPrivateProperty(inbound, "V6ReceiverTransportEpoch", 3L);
        SetPrivateProperty(inbound, "V4LastStateDurableHighestChunkIndex", 255);
        var written = new System.Collections.BitArray(256, true);
        written[100] = false;
        SetPrivateProperty(inbound, "ReceiverSparseChunksWritten", written);
        type.GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, outbound);
        type.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(receiver, inbound);

        // Restart only the seeded inbound setup stream, before any repair or
        // checkpoint decision. Both captured streams then use real emitters.
        var setupEmitter = GetPrivatePropertyValue(inbound, "CoordinatorSnapshotEmitter")!;
        var emitterType = setupEmitter.GetType();
        emitterType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(setupEmitter,
            [GetPrivateProperty<string>(inbound, "CoordinatorInstanceId"), sessionId, transferId, FileTransferDirection.Inbound]);
        await WaitUntilAsync(() => (int)emitterType.GetField("drainScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(setupEmitter)! == 0, timeoutMs: 3000);
        var coordinatorId = Guid.NewGuid().ToString("N");
        inbound.GetType().GetField("<CoordinatorInstanceId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inbound, coordinatorId);
        inbound.GetType().GetField("<CoordinatorSnapshotEmitter>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inbound, Activator.CreateInstance(emitterType, nonPublic: true));
        SetPrivateProperty(inbound, "CoordinatorPersistentState", new FileTransferCoordinatorPersistentState
        {
            CoordinatorInstanceId = coordinatorId,
        });
        SetPrivateProperty(inbound, "V6DestinationMode", Enum.Parse(
            type.GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!, "SparseSeekable"));
        var logStart = GetOperationalLogLength();
        StartCoordinatorSnapshotStreamForTest(outbound);
        StartCoordinatorSnapshotStreamForTest(inbound);

        var markRequested = type.GetMethod("MarkInboundFallbackCheckpointRequestedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CheckpointRepairContinuityOrigin? oldOrigin = null;
        if (priorCheckpoint)
        {
            var initialRequest = await ReceiveCheckpointRequestAsync();
            BeginCheckpoint(initialRequest);
            await ExchangeCheckpointProofAsync(initialRequest);
            oldOrigin = Assert.IsType<CheckpointRepairContinuityOrigin>(
                CreateCoordinatorStateForTest(inbound).PersistentState.CheckpointRepairContinuityOrigin);
        }
        var sendRepair = type.GetMethod("SendInboundV6FrontierRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var immediate = await Assert.IsAssignableFrom<Task<bool>>(sendRepair.Invoke(receiver, [inbound, "chunk_batch_committed", false]));
        if (!priorCheckpoint) { Assert.False(immediate); }
        await WaitUntilAsync(() => GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract") is not null,
            timeoutMs: 5000);
        var lineage = new List<object> { GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract")! };
        await WaitUntilAsync(() => GetPrivateProperty<bool>(GetPrivatePropertyValue(lineage[0], "Lifecycle")!, "DispatchObserved"),
            timeoutMs: 5000);
        var retryInterval = Assert.IsType<TimeSpan>(type.GetMethod("ResolveInboundV6FrontierRequestRetryIntervalLocked",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [inbound, false]));
        while (lineage.Count < 3)
        {
            for (var attempt = 0; attempt < 3 &&
                ReferenceEquals(lineage[^1], GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract")); attempt++)
            {
                await Task.Delay(retryInterval + TimeSpan.FromMilliseconds(50));
                Assert.True(await Assert.IsAssignableFrom<Task<bool>>(sendRepair.Invoke(receiver, [inbound, "retry", true])));
            }
            var successor = GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract")!;
            Assert.NotSame(lineage[^1], successor);
            lineage.Add(successor);
        }
        var current = lineage[^1];
        for (var index = 1; index < lineage.Count; index++)
        {
            var prior = GetPrivateProperty<FallbackFrontierRepairAuthority>(lineage[index - 1], "Authority");
            var next = GetPrivateProperty<FallbackFrontierRepairAuthority>(lineage[index], "Authority");
            Assert.Equal(prior.AuthorityId, next.PredecessorAuthorityId);
            Assert.Equal(prior.DecisionGeneration + 1, next.DecisionGeneration);
            Assert.Equal(prior.RuntimeOwner, next.RuntimeOwner);
            Assert.Equal("SettledStalled", Outcome(lineage[index - 1]));
        }
        var source = CreateCoordinatorStateForTest(inbound);
        Assert.Same(oldOrigin, source.PersistentState.CheckpointRepairContinuityOrigin);

        // A real sender control-plane send crosses the loopback. The fixture
        // dispatches its received frame into the same production checkpoint
        // handler used by the receive loop; it does not run a GUI/receive pump.
        var received = await ReceiveCheckpointRequestAsync();
        BeginCheckpoint(received);
        var pending = CreateCoordinatorStateForTest(inbound);
        Assert.Null(pending.PersistentState.CheckpointRepairContinuityOrigin);
        var lease = Assert.IsType<FallbackCheckpointLease>(pending.CurrentFallbackCheckpointLease);
        Assert.Equal(source.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1, pending.CurrentRuntimeOwner!.Value.RuntimeGeneration);
        Assert.Equal(source.CurrentRoutePublicationToken!.Value.PublicationGeneration + 1,
            pending.CurrentRoutePublicationToken!.Value.PublicationGeneration);
        Assert.Equal(pending.CurrentRuntimeOwner.Value, lease.RuntimeOwner);
        Assert.Equal(received.RepairRequestId, lease.CheckpointRequestId);
        Assert.Equal(source.RouteSelection, pending.RouteSelection);
        Assert.Equal(source.CurrentDataSessionIdentity, pending.CurrentDataSessionIdentity);
        Assert.Equal(source.CurrentDataSessionGeneration, pending.CurrentDataSessionGeneration);
        Assert.Same(receiverData, GetPrivatePropertyValue(inbound, "DataSession"));
        Assert.Null(pending.CurrentRepairReplayDecision);
        Assert.Null(GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract"));
        Assert.Equal("StaleRejected", Outcome(current));
        Assert.Equal("runtime_owner_retired_state_refresh_received",
            GetPrivatePropertyValue(GetPrivatePropertyValue(current, "Lifecycle")!, "SettlementReason"));
        var emitter = GetPrivatePropertyValue(inbound, "CoordinatorSnapshotEmitter")!;
        var publicationEvidence = Assert.IsType<FileTransferRoutePublicationEvidence>(emitterType
            .GetMethod("GetRoutePublicationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(emitter, [pending.CurrentRoutePublicationToken]));

        await ExchangeCheckpointProofAsync(received);
        var accepted = CreateCoordinatorStateForTest(inbound);
        Assert.Null(accepted.CurrentFallbackCheckpointLease);
        Assert.Equal(pending.CurrentRuntimeOwner, accepted.CurrentRuntimeOwner);
        Assert.Equal(pending.CurrentRoutePublicationToken, accepted.CurrentRoutePublicationToken);
        var origin = Assert.IsType<CheckpointRepairContinuityOrigin>(accepted.PersistentState.CheckpointRepairContinuityOrigin).Snapshot;
        if (oldOrigin is not null)
        {
            Assert.NotEqual(oldOrigin.Snapshot.AcceptanceIdentity, origin.AcceptanceIdentity);
            Assert.NotEqual(oldOrigin.Snapshot.OriginReceiptIdentity, origin.OriginReceiptIdentity);
            Assert.NotEqual(oldOrigin.Snapshot.AcceptedCheckpoint.CheckpointRequestId, origin.AcceptedCheckpoint.CheckpointRequestId);
        }
        Assert.Equal(received.RepairRequestId, origin.AcceptedCheckpoint.CheckpointRequestId);
        Assert.Equal(lease.LeaseGeneration, origin.AcceptedCheckpoint.LeaseGeneration);
        Assert.Equal(pending.CurrentRoutePublicationToken.Value.PublicationGeneration,
            origin.SourceScope.Boundary.RoutePublicationGeneration);
        Assert.Equal(pending.CurrentRuntimeOwner.Value.RuntimeGeneration, origin.SourceScope.Boundary.RuntimeGeneration);
        Assert.Equal(pending.CurrentDataSessionIdentity, origin.SourceScope.Boundary.DataSessionIdentity);
        Assert.Equal(pending.CurrentDataSessionGeneration, origin.SourceScope.Boundary.DataSessionGeneration);
        Assert.NotEqual(source.CurrentRuntimeOwner.Value.RuntimeGeneration, origin.SourceScope.Boundary.RuntimeGeneration);

        // The exact completed request cannot create another publication, and
        // the new checkpoint cannot rewrite any old repair's terminal outcome.
        lock (GetServiceGate(receiver))
        {
            Assert.False(Assert.IsType<bool>(markRequested.Invoke(receiver, [inbound, received, "state_refresh_received"])));
        }
        Assert.Equal(accepted.CurrentRuntimeOwner, CreateCoordinatorStateForTest(inbound).CurrentRuntimeOwner);
        Assert.Equal(accepted.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(inbound).CurrentRoutePublicationToken);
        var settle = type.GetMethod("TrySettleFallbackFrontierRepairContract", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var contract in lineage)
        {
            var lifecycle = GetPrivatePropertyValue(contract, "Lifecycle")!;
            var expected = ReferenceEquals(contract, current) ? "StaleRejected" : "SettledStalled";
            Assert.Equal(expected, Outcome(contract));
            var progress = Enum.Parse(lifecycle.GetType().GetGenericArguments()[0], "ProgressObserved");
            Assert.False(Assert.IsType<bool>(settle.Invoke(null, [contract, progress, "late_republication_progress", null])));
            Assert.Equal(expected, Outcome(contract));
        }
        Assert.Equal(100, GetPrivateProperty<int>(inbound, "NextChunkIndex"));
        receiver.Dispose();
        sender.Dispose();
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Count(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal) &&
            GetDelimitedLogField(line, "transfer_id") == publicationEvidence.TransferId) == 2, timeoutMs: 3000);
        var lines = ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, line => line.Contains("event=filetransfer_fallback_frontier_repair_progress_observed;", StringComparison.Ordinal));
        var boundaryLine = Assert.Single(lines, line =>
            line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal) &&
            GetDelimitedLogField(line, "transition_id") == publicationEvidence.BoundaryId);
        var payload = GetDelimitedLogField(boundaryLine, "snapshot_payload").Replace('-', '+').Replace('_', '/');
        using var boundary = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        Assert.Equal("StartTransfer", boundary.RootElement.GetProperty("decisionKind").GetString());
        Assert.Equal("Accepted", boundary.RootElement.GetProperty("decisionStatus").GetString());
        Assert.Equal("state_refresh_received", boundary.RootElement.GetProperty("decisionReason").GetString());
        var aggregate = boundary.RootElement.GetProperty("aggregate");
        var before = aggregate.GetProperty("beforeBoundary");
        var after = aggregate.GetProperty("afterBoundary");
        Assert.Equal(source.CurrentRuntimeOwner.Value.RuntimeGeneration, before.GetProperty("runtimeGeneration").GetInt32());
        Assert.Equal(pending.CurrentRuntimeOwner.Value.RuntimeGeneration, after.GetProperty("runtimeGeneration").GetInt32());
        Assert.Equal(source.CurrentRoutePublicationToken.Value.PublicationGeneration, before.GetProperty("routePublicationGeneration").GetInt32());
        Assert.Equal(pending.CurrentRoutePublicationToken.Value.PublicationGeneration, after.GetProperty("routePublicationGeneration").GetInt32());
        Assert.Equal(before.GetProperty("dataSessionIdentity").GetString(), after.GetProperty("dataSessionIdentity").GetString());
        Assert.Equal(before.GetProperty("dataSessionGeneration").GetInt64(), after.GetProperty("dataSessionGeneration").GetInt64());
        foreach (var role in new[] { "route_publication", "runtime_owner" })
        {
            var oldState = role == "route_publication" ? "Published" : "Current";
            var operations = aggregate.GetProperty("operations").EnumerateArray()
                .Where(operation => operation.GetProperty("operationRole").GetString() == role).ToArray();
            Assert.Single(operations, operation => operation.GetProperty("beforeState").GetString() == oldState &&
                operation.GetProperty("afterState").GetString() == "None" && operation.GetProperty("stateApplied").GetBoolean());
            var replacement = Assert.Single(operations, operation => operation.GetProperty("beforeState").GetString() == "None" &&
                operation.GetProperty("afterState").GetString() == oldState && operation.GetProperty("stateApplied").GetBoolean());
            if (role == "route_publication")
            {
                Assert.Equal(publicationEvidence.OperationKey, replacement.GetProperty("operationKey").GetString());
                Assert.True(replacement.GetProperty("receiptValidated").GetBoolean());
            }
        }
        var retiredOrigins = aggregate.GetProperty("operations").EnumerateArray()
            .Where(operation => operation.GetProperty("operationRole").GetString() == "checkpoint_acceptance_retained").ToArray();
        if (oldOrigin is null) { Assert.Empty(retiredOrigins); }
        else
        {
            var retired = Assert.Single(retiredOrigins);
            Assert.Equal("Retained", retired.GetProperty("beforeState").GetString());
            Assert.Equal("None", retired.GetProperty("afterState").GetString());
            Assert.True(retired.GetProperty("stateApplied").GetBoolean());
            Assert.True(retired.GetProperty("receiptValidated").GetBoolean());
            Assert.Equal(oldOrigin.Snapshot.AcceptedStateGeneration, retired.GetProperty("operationGeneration").GetInt64());
            var project = emitterType.GetMethod("EvidenceToken", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(project.Invoke(null, [oldOrigin.Snapshot.AcceptanceIdentity]), retired.GetProperty("authorityId").GetString());
            Assert.Equal(project.Invoke(null, [oldOrigin.Snapshot.OriginReceiptIdentity]), retired.GetProperty("receiptIdentity").GetString());
            Assert.Equal(project.Invoke(null, [oldOrigin.Snapshot.OriginReceiptFingerprint]), retired.GetProperty("receiptFingerprint").GetString());
        }

        return Array.AsReadOnly(lines);

        static string? Outcome(object contract) => GetPrivatePropertyValue(
            GetPrivatePropertyValue(contract, "Lifecycle")!, "SettlementOutcome")?.ToString();

        async Task<FileTransferFrontierRequestFrameV6> ReceiveCheckpointRequestAsync()
        {
            FileTransferFrontierRequestFrameV6 request;
            lock (GetServiceGate(sender))
            {
                request = Assert.IsType<FileTransferFrontierRequestFrameV6>(type.GetMethod(
                    "CreateOutboundV4SparseRuntimeStateRefreshRequestLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(sender, [outbound, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10), 3L, "active",
                        156, 0, 256, 0, 0, "republication_checkpoint_fixture", "state_refresh"]));
            }
            type.GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sender, [outbound, senderData, request, "republication_checkpoint_fixture"]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var receivedRequest = Assert.IsType<FileTransferFrontierRequestFrameV6>(await receiverData.ReceiveAsync(timeout.Token));
            Assert.Equal(request.RepairRequestId, receivedRequest.RepairRequestId);
            Assert.True(senderTransport.LastValidationAccepted);
            Assert.Equal(FileTransferControlPlaneKind.FallbackCheckpointRequest, senderTransport.LastRequest!.Kind);
            Assert.Same(senderTransport.LastRequest.Frame, receivedRequest);
            await WaitUntilAsync(() => GetPrivateProperty<int>(outbound, "V6RegularNknStateRefreshSendInFlight") == 0, timeoutMs: 3000);
            return receivedRequest;
        }

        void BeginCheckpoint(FileTransferFrontierRequestFrameV6 request)
        {
            lock (GetServiceGate(receiver))
            {
                Assert.True(Assert.IsType<bool>(markRequested.Invoke(receiver, [inbound, request, "state_refresh_received"])));
            }
        }

        async Task ExchangeCheckpointProofAsync(FileTransferFrontierRequestFrameV6 request)
        {
            var sendMethod = receiveBoundState ? "SendInboundSparseCreditStateAsync" : "SendInboundV6ReceiverStateAsync";
            object?[] sendArgs = receiveBoundState
                ? [inbound, "regular_nkn_state_refresh", false, false, false, true, null]
                : [inbound, "regular_nkn_state_refresh", true, false, null];
            Assert.True(await Assert.IsAssignableFrom<Task<bool>>(type.GetMethod(sendMethod,
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receiver, sendArgs)));
            var delivery = receiverTransport.LastRequest!;
            Assert.Equal(FileTransferControlPlaneKind.FallbackCheckpointProof, delivery.Kind);
            Assert.True(receiverTransport.LastValidationAccepted);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            FileTransferReceivedDataFrame received;
            do { received = await senderData.ReceiveWithMetadataAsync(timeout.Token); }
            while (received.Frame is FileTransferFrontierRequestFrameV6); // Earlier real repair requests, not checkpoint proofs.
            var proof = Assert.IsType<FileTransferReceiverStateFrameV6>(received.Frame);
            Assert.Same(delivery.Frame, proof);
            Assert.Equal(request.RepairRequestId, proof.RepairRequestId);
            Assert.Equal(100, proof.ContiguousCommittedChunkIndex);
            Assert.Equal(255, proof.DurableReceivedHighestChunkIndex);
            if (receiveBoundState)
            {
                await ApplyReceiveBoundCheckpointForEvidenceAsync(sender, outbound, senderData, proof);
            }
            else
            {
                lock (GetServiceGate(sender))
                {
                    Assert.True(Assert.IsType<bool>(type.GetMethod("TryAcceptOutboundFallbackCheckpointLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [outbound, proof, "receiver_state"])));
                }
            }
            Assert.Null(CreateCoordinatorStateForTest(outbound).CurrentFallbackCheckpointLease);
            Assert.Null(CreateCoordinatorStateForTest(inbound).CurrentFallbackCheckpointLease);
        }
    }
}
