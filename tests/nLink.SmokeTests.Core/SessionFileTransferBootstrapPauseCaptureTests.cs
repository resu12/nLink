using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task BootstrapSourceHold_PauseStaleCallbackDoesNotPoisonNextPeerWake()
    {
        const string transferId = "bootstrap_pause_capture";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var session = new SourceFailureDataSession("session_" + transferId, transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        using var peer = new LoopbackFileTransferTransport(session.SessionId);
        bridge.Connect(peer);
        service.AttachTransport(bridge);
        // Seed a correlated Tuna source; callback admission, transport pause,
        // peer-frame custody and hold issuance below use production paths.
        var context = CreateRegularNknV4OutboundContext(transferId, 4, 4, 5,
            DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128,
            sourceFactory: _ => Task.FromResult<Stream>(new MemoryStream(new byte[128 * 21 * 1024], writable: false)));
        SetPrivateProperty(context, "DataSession", session);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        SetPrivateProperty(context, "V4LastStateEpoch", 3);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
        AdoptSourceFailureTunaForTest(service, context);
        var peerWake = PeerWakeForSourceFailureTest(context);
        session.PhysicalSubmissionForTest = (frame, ct) =>
        {
            var sent = session.SendAsync(frame, ct);
            return new(sent, sent);
        };
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests;
        SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = (id, _) =>
        {
            if (id != transferId) return Task.CompletedTask;
            reached.TrySetResult();
            return release.Task;
        };
        Task? supervisor = null;
        try
        {
            supervisor = StartSparseReceiveSupervisor(service, context);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var callback = Assert.Single(((IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Values.Cast<object>());
            var authority = GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority");
            var before = CreateCoordinatorStateForTest(context);
            lock (GetServiceGate(service))
            {
                Assert.True((bool)typeof(SessionFileTransferService)
                    .GetMethod("TryPauseOutboundTransportLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, "remote_header_switch_off", true])!);
            }
            var beforeFirstWake = CreateCoordinatorStateForTest(context);
            ObservePeerWake();
            Assert.Empty(bridge.Executions);
            Assert.Null(GetPrivatePropertyValue(callback, "Capture"));
            Assert.Equal(beforeFirstWake.PersistentState.FileTransferRouteTransition,
                CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition);
            release.TrySetResult();
            await WaitUntilAsync(() => ((IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Count == 0,
                timeoutMs: 5000);
            // Callback completion is diagnostic-only. The current supervisor
            // independently reevaluates the retained exact peer proof.
            Assert.Equal(4, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
            await WaitUntilAsync(() => bridge.Executions.Count == 1, timeoutMs: 5000);
            var hold = PhysicalSourceHoldForTest(context);
            Assert.False(hold.Invalidated);
            Assert.NotNull(hold.Receipt);
            Assert.Empty(hold.Capture!.OutboundCallbacks);
            Assert.Equal(peerWake, hold.Command.PeerWake);
            var after = CreateCoordinatorStateForTest(context);
            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            var consumed = Assert.Single(ReadCallbackPayloadsForTest(logStart).Where(payload =>
                payload.GetProperty("stage").GetString() == "Consumed" &&
                payload.GetProperty("leaseGeneration").GetInt64() == authority.LeaseGeneration));
            Assert.Equal("Stale", consumed.GetProperty("disposition").GetString());
            Assert.Equal("none", consumed.GetProperty("allowedEffect").GetString());
        }
        finally
        {
            release.TrySetResult();
            SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = previous;
            TryCancelContextLifetime(context);
            foreach (var phase in bridge.Executions)
            {
                var held = new FileTransferBootstrapPhaseReceipt(phase.Command,
                    FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "test_cleanup_no_submission");
                phase.Delivery.TrySetResult(held);
                phase.Settlement.TrySetResult(held);
            }
            if (supervisor is not null) await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
            if (Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR") is { Length: > 0 } artifactDirectory)
            {
                Directory.CreateDirectory(artifactDirectory);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "pause-capture.log"), ReadOperationalLogTail(logStart));
            }
        }

        void ObservePeerWake()
            => Assert.True((bool)typeof(SessionFileTransferService)
                .GetMethod("TryHandleBootstrapCorrelationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [FileTransferDirection.Outbound, session,
                    new FileTransferReceivedDataFrame(peerWake, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)])!);
    }
}
