using System.Collections;
using System.Reflection;
using NLink.App.ViewModels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferDisplayProgressTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData("regular_nkn_v4_fast", 4)]
    [InlineData("post_tuna_fallback_v6", 6)]
    public async Task SparseGap_DisplayMatchesActualReceiverReport(string route, int protocol)
    {
        var transferId = "display_progress_" + route;
        var sessionId = "session_" + transferId;
        var payload = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        foreach (var transport in new[] { senderTransport, receiverTransport })
        {
            transport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
            transport.IsPostTunaFileFallbackActiveForRouteSelection = protocol == 6;
        }
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
            Task.FromResult(frame is FileTransferChunkBatchFrameV4 batch && batch.StartChunkIndex == 0);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(new FileTransferSendDescriptor("progress.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId,
            (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { BytesTransferred: 0, BytesAcceptedForTransport: > 65536 },
            timeoutMs: 10000);

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "inspect_progress", CancellationToken.None));
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.IsPeerPaused == true);
        // Pause control can arrive before the corresponding progress report.
        await WaitUntilAsync(() => protocol == 6
            ? receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any()
            : receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(s => s.TransferPaused) &&
              sender.Snapshot.Outbound?.ProgressBytes == receiver.Snapshot.Inbound?.ProgressBytes);
        var report = protocol == 6
            ? receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last()
            : receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Last(s => s.TransferPaused);
        var inbound = receiver.Snapshot.Inbound!;
        var outbound = sender.Snapshot.Outbound!;
        Assert.Equal(route, inbound.RouteToken);
        Assert.Equal(protocol, inbound.ProtocolVersion);
        Assert.Equal(protocol == 6, report is FileTransferReceiverStateFrameV6);
        Assert.Equal(0, inbound.BytesTransferred);
        Assert.True(inbound.BytesAcceptedForTransport > 65536); // Real sparse writes, not queued sends.
        Assert.Equal(protocol == 6 ? 0 : inbound.BytesAcceptedForTransport!.Value, report.BytesCommitted);
        Assert.Equal(report.BytesCommitted, outbound.ProgressBytes);
        var receivedCard = FileTransferPanelItemViewModel.FromSnapshot(inbound)!;
        var sentCard = FileTransferPanelItemViewModel.FromSnapshot(outbound)!;
        Assert.Equal(report.BytesCommitted, receivedCard.ProgressBytes);
        Assert.Equal(sentCard.ProgressFraction, receivedCard.ProgressFraction);
        Assert.Equal(sentCard.ProgressText, receivedCard.ProgressText);
    }

    [Theory]
    [InlineData(false, "normal", 700)]
    [InlineData(true, "normal", 700)]
    [InlineData(false, "handoff", 100)]
    [InlineData(true, "handoff", 100)]
    [InlineData(false, "recovery", 100)]
    [InlineData(true, "recovery", 100)]
    public void V4Display_MatchesStateFrameDuringHandoffAndRecovery(bool tuna, string phase, long expected)
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        var route = FileTransferRouteResolver.Resolve(tuna ? FileTransferRoute.FileTunaV4 : FileTransferRoute.RegularNknV4Fast);
        var context = Activator.CreateInstance(contextType, new object[]
        {
            new FileTransferOfferV2
            {
                SessionId = "display_session", TransferId = "display_transfer", FileName = "progress.bin",
                FileSizeBytes = 1000, PreferredDataProtocolVersion = 4, FileTransferRoute = route.TelemetryToken,
            }, route,
        })!;
        void Set(string property, object value) => contextType.GetProperty(property)!.SetValue(context, value);
        try
        {
            Set("State", FileTransferTransferState.Receiving);
            Set("ChunkSizeBytes", 100);
            Set("ChunkCount", 10);
            Set("NextChunkIndex", 1);
            Set("BytesTransferred", 100L);
            Set("ReceiverSparseWriteActive", true);
            Set("ReceiverSparseBytesWritten", 700L);
            var written = new BitArray(10);
            written[0] = true;
            for (var i = 2; i < 8; i++) written[i] = true;
            Set("ReceiverSparseChunksWritten", written);
            Set("UserPaused", true); // Build a report without scheduling frontier repair.
            Set("PullPostTunaRecoveryActive", phase == "recovery");
            if (phase == "handoff") Set("V6TransportHandoff", new TransportHandoffEpoch { EpochId = 1 });

            var report = Assert.IsType<FileTransferStateFrameV4>(serviceType
                .GetMethod("CreateInboundV4StateLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "display_test", false, false]));
            var epoch = contextType.GetProperty("V4StateEpoch")!.GetValue(context);
            var snapshot = Assert.IsType<FileTransferTransferSnapshot>(contextType.GetMethod("ToSnapshot")!.Invoke(context, null));
            Assert.Equal(expected, report.BytesCommitted);
            Assert.Equal(report.BytesCommitted, snapshot.PeerComparableProgressBytes);
            Assert.Equal(report.BytesCommitted, FileTransferPanelItemViewModel.FromSnapshot(snapshot)!.ProgressBytes);
            Assert.Equal(700, snapshot.ProgressBytes); // Raw sparse-write telemetry is unchanged.
            Assert.Equal(100, snapshot.BytesTransferred);
            Assert.Equal(1, contextType.GetProperty("NextChunkIndex")!.GetValue(context));
            Assert.Equal(epoch, contextType.GetProperty("V4StateEpoch")!.GetValue(context));
        }
        finally
        {
            contextType.GetMethod("CancelLifetime")!.Invoke(context, null);
            contextType.GetMethod("DisposeResources")!.Invoke(context, null);
        }
    }
}
