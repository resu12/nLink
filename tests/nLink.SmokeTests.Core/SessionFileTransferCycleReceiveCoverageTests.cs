using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_PendingTunaReadFailureCannotRetireFallbackSuccessor()
    {
        const string transferId = "cycle_pending_receive_failure";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);
        ReplaceSparseRouteAuthority(context, FileTransferRoute.FileTunaV4);
        var supervisor = StartSparseReceiveSupervisor(service, context);
        try
        {
            await WaitUntilAsync(() => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null, timeoutMs: 5000);
            var predecessorRead = transport.GetDataSessionReceiveStateForTests(transferId);
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var predecessorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
            RoutePublicationToken successor;
            lock (GetServiceGate(service))
            {
                // This focused test replaces authority synthetically; the joined
                // cycle separately proves the negotiated route exchange.
                successor = ReplaceSparseRouteAuthority(context, FileTransferRoute.PostTunaFallbackV6);
            }
            await WaitUntilAsync(() => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") == successor,
                timeoutMs: 5000);
            Assert.Equal(predecessorRead, transport.GetDataSessionReceiveStateForTests(transferId));
            Assert.NotSame(predecessorInstall, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            var before = CreateCoordinatorStateForTest(context);
            Assert.NotEqual(predecessorOwner, before.CurrentRuntimeOwner);
            var frontier = GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex");
            var acknowledged = GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver");
            var logStart = GetOperationalLogLength();

            Assert.True(transport.TryFailActiveDataSessionReceiveForTests(transferId,
                new IOException("Injected failure of the actual pending Tuna receive.")));
            await WaitUntilAsync(() => transport.GetDataSessionReceiveStateForTests(transferId) is
                { Active: true } current && current.Ordinal > predecessorRead.Ordinal, timeoutMs: 5000);
            var log = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound;", log);
            Assert.Contains("source=v4_sender_failed", log);
            Assert.Contains("reason=runtime_generation_mismatch", log);
            Assert.Equal(before.CurrentRuntimeOwner, CreateCoordinatorStateForTest(context).CurrentRuntimeOwner);
            Assert.Equal(successor, CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken);
            Assert.Equal(frontier, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(acknowledged, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.Empty(transport.SentErrors);
            Assert.Empty(transport.SentCancels);
        }
        finally
        {
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
