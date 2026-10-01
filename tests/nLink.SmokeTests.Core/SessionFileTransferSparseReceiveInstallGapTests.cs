using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_InstallGapDoesNotConsumeSuccessorCheckpointWithoutBinding()
    {
        const string transferId = "sparse_receive_install_gap_checkpoint";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        // This unit fixture seeds fallback scope and replaces its publication
        // to force the gap. Pump installation and the checkpoint lease still
        // use production staging and acceptance paths.
        var context = CreateSparsePostTunaContextWithSourceFactory(transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var receiveReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reopenReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReopen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousReceiveBarrier = SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests;
        var previousReopenBarrier = SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests;
        SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = async (id, _) =>
        {
            if (id != transferId) return;
            receiveReached.TrySetResult();
            await releaseReceive.Task;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);
        var logStart = GetOperationalLogLength();
        try
        {
            await WaitUntilAsync(() => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null, timeoutMs: 5000);
            var predecessor = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var predecessorGeneration = GetPrivateProperty<long>(predecessor, "InstallGeneration");
            transport.ReceiveDeliveredDataFrame(new FileTransferStateFrameV4
            {
                SessionId = sessionId, TransferId = transferId, Epoch = 1,
                ContiguousCommittedChunkIndex = 1, DurableReceivedHighestChunkIndex = 0,
                CreditUntilChunkIndexExclusive = 1, BytesCommitted = 21 * 1024, MissingRanges = [],
            });
            await receiveReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = async (id, _) =>
            {
                if (id != transferId) return;
                reopenReached.TrySetResult();
                await releaseReopen.Task;
            };
            RoutePublicationToken successor;
            lock (GetServiceGate(service))
            {
                successor = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                Assert.Null(GetPrivatePropertyValue(context, "PendingOutboundSparsePumpInstall"));
            }
            releaseReceive.TrySetResult();
            await reopenReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(0L, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            releaseReopen.TrySetResult();
            await WaitUntilAsync(() => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                GetPrivateProperty<long>(installed, "InstallGeneration") > predecessorGeneration &&
                GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") == successor, timeoutMs: 5000);

            var request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId, TransferId = transferId, TransportEpoch = 3,
                RepairRequestId = "v6-regular-nkn-state-refresh:install-gap",
                Priority = "state_refresh", RecoveryMode = "post_tuna_fallback_state_refresh",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            };
            lock (GetServiceGate(service))
            {
                Assert.True((bool)typeof(SessionFileTransferService)
                    .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, request, "unit_test_install_gap_checkpoint"])!);
                Assert.NotNull(CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);
            }
            // One delivery after the successor is installed. A speculative
            // receive started in the preceding gap must not consume this proof.
            transport.ReceiveDeliveredDataFrame(new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId, TransferId = transferId, Epoch = 2, TransportEpoch = 3,
                RepairRequestId = request.RepairRequestId, Priority = "state_refresh",
                RecoveryMode = "regular_nkn_state_refresh", ContiguousCommittedChunkIndex = 2,
                DurableReceivedHighestChunkIndex = 1, CreditUntilChunkIndexExclusive = 2,
                BytesCommitted = 2 * 21 * 1024, MissingRanges = [],
            });
            await WaitUntilAsync(() => GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex") == 2,
                timeoutMs: 5000);
            Assert.Equal(2, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Null(CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);
            Assert.Equal(successor, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            var log = ReadOperationalLogTail(logStart);
            Assert.Contains("reason=stale_outbound_sparse_receive_descriptor", log, StringComparison.Ordinal);
            Assert.DoesNotContain("decision_reason=receive_install_missing", log, StringComparison.Ordinal);
        }
        finally
        {
            releaseReceive.TrySetResult();
            releaseReopen.TrySetResult();
            SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = previousReceiveBarrier;
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = previousReopenBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
            if (Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR") is { Length: > 0 } artifactDirectory)
            {
                Directory.CreateDirectory(artifactDirectory);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "install-gap.log"), ReadOperationalLogTail(logStart));
            }
        }
    }
}
