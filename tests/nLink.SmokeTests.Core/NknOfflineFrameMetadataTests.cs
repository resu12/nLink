using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("absent")] [InlineData("installed")] [InlineData("throwing")] [InlineData("removed")]
    public async Task OfflineFrameMetadata_ObserverDoesNotChangeActualTransportOrCompletion(string mode)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.offline.metadata.address");
            var helperClient = new FakeNknClient("helper.offline.metadata.address");
            var lane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-offline-metadata", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-offline-metadata", helperClient.Address),
                NknTunaAccelerationOptions.Disabled, lane);
            var raw = new ConcurrentQueue<byte[]>();
            hostClient.MessageReceived += (_, message) =>
            {
                if (EnvelopeCodec.TryDeserialize(message.Payload, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                    raw.Enqueue(message.Payload);
            };
            var session = await ApproveNknSessionAsync(host, helper, deadline.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var data = await helper.OpenFileTransferDataSessionAsync(session, "offline-metadata", deadline.Token);
            var observed = new List<OfflineFileFrameMetadata>();
            if (mode != "absent") helper.OfflineFrameObserverForTests = item =>
            {
                observed.Add(item);
                if (mode == "throwing") throw new InvalidOperationException("observer_only");
            };
            if (mode == "removed") helper.OfflineFrameObserverForTests = null;
            for (var index = 0; index < 2; index++)
            {
                if (index == 1) helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
                await data.SendAsync(new FileTransferChunkBatchFrameV4
                {
                    SessionId = session, TransferId = "offline-metadata", StartChunkIndex = index,
                    ChunkCount = 1, DataSegments = [new byte[1024]], BatchProfile = "v4_default_21k",
                }, deadline.Token);
            }
            await WaitUntilAsync(() => raw.Count == 1 && lane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            var beforeRejected = observed.Count;
            var stale = new FileTransferDataPathSendAuthority(session, "offline-metadata", FileTransferDirection.Outbound,
                FileTransferRouteResolver.FileTunaV4Token, 4, 0, 0, 0, 0, 0, 0,
                FileTransferRouteResolver.FileTunaV4Token, 4, FileTransferTransportKind.Tuna, "RoutePublication", "stale-test");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ((IFileTransferDataPathAuthoritySession)data).SendAsync(new FileTransferChunkBatchFrameV4
            {
                SessionId = session, TransferId = "offline-metadata", StartChunkIndex = 2,
                ChunkCount = 1, DataSegments = [new byte[1024]], BatchProfile = "v4_default_21k",
            }, stale, deadline.Token));
            Assert.Equal(beforeRejected, observed.Count);
            Assert.Single(raw); Assert.Single(lane.Sent);
            Assert.Equal(NknBridgeChannel.Bulk, lane.Sent.Single().Lane);
            if (mode is "installed" or "throwing")
            {
                Assert.Equal(2, observed.Count);
                var originals = new[] { raw.Single(), lane.Sent.Single().Payload };
                for (var index = 0; index < 2; index++)
                {
                    Assert.Equal(session, observed[index].SessionId); Assert.Equal("offline-metadata", observed[index].TransferId);
                    Assert.Equal(index, observed[index].StartChunkIndex); Assert.Equal(1, observed[index].ChunkCount);
                    Assert.False(observed[index].IsRepair); Assert.Null(observed[index].TransportEpoch);
                    Assert.Equal(originals[index].Length, observed[index].EnvelopeLength);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(originals[index])), observed[index].EnvelopeSha256);
                }
            }
            else Assert.Empty(observed);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(null, false)] [InlineData("repair-0", true)]
    public void OfflineFrameMetadata_ClassifiesOriginalRepairWithoutExposingMutablePayload(string? repair, bool expected)
    {
        var options = NknTransportOptions.Load();
        var client = new FakeNknClient("offline.metadata.classification");
        using var transport = new NknSignalingTransport(client, options, new NknIdentity("offline-metadata-classification", client.Address));
        OfflineFileFrameMetadata? observed = null;
        transport.OfflineFrameObserverForTests = item => observed = item;
        var bytes = new byte[] { 1, 2, 3 };
        var frame = new FileTransferChunkBatchFrameV6 { SessionId = "session", TransferId = "transfer", StartChunkIndex = 2,
            ChunkCount = 1, RepairRequestId = repair, TransportEpoch = 0, DataSegments = [new byte[21504]] };
        typeof(NknSignalingTransport).GetMethod("ObserveOfflineFileFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(transport, [frame, bytes]);
        Assert.NotNull(observed); Assert.Equal(expected, observed.IsRepair);
        Assert.Equal(0, observed.TransportEpoch); Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }
}
