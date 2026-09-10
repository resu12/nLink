using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("(none)")]
    [Trait("FileTransferInfraSuite", "FileTunaDataPathProvenance")]
    public void NknTransport_DataPathEvidenceMissingIdentityIsNotInvented(string? missing)
    {
        var evidence = typeof(NknSignalingTransport)
            .GetMethod("CreateFileTransferDataPathEnvelopeEvidence", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [missing, missing, missing])!;
        foreach (var property in new[] { "EnvelopeFingerprint", "SessionId", "TransferId" })
        {
            Assert.Equal("(none)", evidence.GetType().GetProperty(property)!.GetValue(evidence));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "FileTunaDataPathProvenance")]
    public async Task NknTransport_QueuedRegularEnvelopeRejectedOnTuna_PreservesExactProvenance(bool secondCycle)
        => await CreateRejectedDataPathEnvelopeProvenanceFixture(secondCycle);

    internal static async Task<string> CreateRejectedDataPathEnvelopeProvenanceFixture(bool secondCycle)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var receiverClient = new FakeNknClient("receiver.data-path-provenance.address");
            var senderClient = new FakeNknClient("sender.data-path-provenance.address");
            using var receiver = new NknSignalingTransport(receiverClient, options,
                new NknIdentity("data-path-receiver", receiverClient.Address));
            using var sender = new NknSignalingTransport(senderClient, options,
                new NknIdentity("data-path-sender", senderClient.Address));
            var sessionId = await ApproveNknSessionAsync(receiver, sender, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_data_path_provenance";
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            sender.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            receiver.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await sender.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "provenance.bin",
                FileSizeBytes = 131072, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, cts.Token);
            await offered.Task.WaitAsync(cts.Token);
            await receiver.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, cts.Token);
            await accepted.Task.WaitAsync(cts.Token);
            await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8,
            }, cts.Token);
            await opened.Task.WaitAsync(cts.Token);
            using var inbound = await receiver.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            using var outbound = await sender.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            var outgoing = FileTransferCoordinatorTests.CreateDataPathIngressRouteCycle(
                sessionId, transferId, FileTransferDirection.Outbound);
            var incoming = FileTransferCoordinatorTests.CreateDataPathIngressRouteCycle(
                sessionId, transferId, FileTransferDirection.Inbound);
            PublishDataPathFixtureAuthority(sender, outgoing[0]);
            PublishDataPathFixtureAuthority(receiver, incoming[0]);
            var logStart = GetOperationalLogLength();
            var held = new List<Envelope>();
            senderClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                {
                    held.Add(envelope);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            };
            var frame = new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = 12, ChunkCount = 2,
                DataSegments = [Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray()],
                BatchProfile = "v4_default_21k",
            };
            var sendAuthority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [outgoing[0], "data_path_provenance_fixture"]));
            await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(outbound)
                .SendAsync(frame, sendAuthority, cts.Token);
            var oldEnvelope = Assert.Single(held);

            for (var index = 1; index <= (secondCycle ? 3 : 1); index++)
            {
                PublishDataPathFixtureAuthority(sender, outgoing[index]);
                PublishDataPathFixtureAuthority(receiver, incoming[index]);
            }
            var rejectionStart = GetOperationalLogLength();
            var receiverStates = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferStates"));
            var stateBeforeRejection = receiverStates[transferId];
            Assert.NotNull(stateBeforeRejection);
            InjectSecureFileTransferDataFrameEnvelope(receiver, senderClient, oldEnvelope,
                senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk);
            Assert.Equal(stateBeforeRejection, receiverStates[transferId]);
            var oldFingerprint = DataPathEnvelopeFingerprint(oldEnvelope.MessageId);
            var oldRows = DataPathProvenanceRows(ReadOperationalLogTail(logStart), oldFingerprint);
            Assert.Equal(4, oldRows.Length); // one send and the exact receive/mismatch/ignore triplet
            AssertDataPathPublication(Assert.Single(oldRows,
                line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal)), outgoing[0]);
            Assert.Equal(3, oldRows.Count(line => line.Contains("receive_disposition=rejected_before_dispatch", StringComparison.Ordinal)));
            Assert.All(oldRows, line =>
            {
                AssertDataPathFrame(line);
                Assert.Contains("data_path_session_id=" + DataPathScopeFingerprint(sessionId), line, StringComparison.Ordinal);
                Assert.Contains("data_path_transfer_id=" + DataPathScopeFingerprint(transferId), line, StringComparison.Ordinal);
                Assert.DoesNotContain(oldEnvelope.MessageId, line, StringComparison.Ordinal);
            });
            Assert.All(oldRows.Where(line => line.Contains("receive_disposition=", StringComparison.Ordinal)), line =>
            {
                Assert.Contains("authority_effect=none", line, StringComparison.Ordinal);
                AssertDataPathPublication(line, incoming[secondCycle ? 3 : 1]);
            });

            // A repeated physical envelope is deduplicated. A fresh same-range
            // envelope is separately rejected and cannot borrow the old send ID.
            InjectSecureFileTransferDataFrameEnvelope(receiver, senderClient, oldEnvelope,
                senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk);
            Assert.Equal(stateBeforeRejection, receiverStates[transferId]);
            Assert.Equal(4, DataPathProvenanceRows(ReadOperationalLogTail(logStart), oldFingerprint).Length);
            var freshEnvelope = BuildSecureFileTransferDataFrameEnvelope(sender, frame,
                GetNextFileTransferSecureSequence(sender), useBulkSenderIdentity: true);
            InjectSecureFileTransferDataFrameEnvelope(receiver, senderClient, freshEnvelope,
                senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk);
            Assert.Equal(stateBeforeRejection, receiverStates[transferId]);
            var freshFingerprint = DataPathEnvelopeFingerprint(freshEnvelope.MessageId);
            Assert.NotEqual(oldFingerprint, freshFingerprint);
            var freshRows = DataPathProvenanceRows(ReadOperationalLogTail(logStart), freshFingerprint);
            Assert.Equal(3, freshRows.Length);
            Assert.DoesNotContain(freshRows, line => line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal));
            Assert.All(freshRows, line =>
            {
                AssertDataPathFrame(line);
                Assert.Contains("receive_disposition=rejected_before_dispatch", line, StringComparison.Ordinal);
                Assert.Contains("authority_effect=none", line, StringComparison.Ordinal);
                Assert.Contains("data_path_session_id=" + DataPathScopeFingerprint(sessionId), line, StringComparison.Ordinal);
                Assert.Contains("data_path_transfer_id=" + DataPathScopeFingerprint(transferId), line, StringComparison.Ordinal);
                AssertDataPathPublication(line, incoming[secondCycle ? 3 : 1]);
            });
            Assert.DoesNotContain("event=filetransfer_data_frame_dispatched;", ReadOperationalLogTail(rejectionStart), StringComparison.Ordinal);
            using var noDelivery = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inbound.ReceiveAsync(noDelivery.Token));
            return ReadOperationalLogTail(logStart);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static void PublishDataPathFixtureAuthority(NknSignalingTransport transport, RoutePublicationToken token)
    {
        var publication = FileTransferCoordinator.CreateRoutePublicationAuthority(token)!;
        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(
            new FileTransferRouteHintNotification(token.SessionId, token.TransferId, token.RouteToken,
                token.ProtocolVersion, "data_path_provenance_fixture", PublicationAuthority: publication));
    }

    private static string DataPathEnvelopeFingerprint(string messageId)
        => "ftenv1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "nLink/filetransfer/data-path-envelope/v1\0" + messageId))).ToLowerInvariant();

    private static string DataPathScopeFingerprint(string value)
        => "fte1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static string[] DataPathProvenanceRows(string log, string fingerprint)
        => log.Split('\n').Where(line => line.Contains("data_path_envelope_fingerprint=" + fingerprint, StringComparison.Ordinal)).ToArray();

    private static void AssertDataPathPublication(string line, RoutePublicationToken token)
    {
        foreach (var expected in new[]
        {
            $"route={token.RouteToken}", $"protocol_version={token.ProtocolVersion}",
            $"live_route_epoch={token.LiveRouteEpoch}", $"runtime_generation={token.RuntimeGeneration}",
            $"terminal_generation={token.TerminalGeneration}", $"leg_generation={token.LegGeneration}",
            $"route_publication_generation={token.PublicationGeneration}", $"route_commit_generation={token.RouteCommitGeneration ?? 0}",
        })
        {
            Assert.Contains("; " + expected + ";", line, StringComparison.Ordinal);
        }
    }

    private static void AssertDataPathFrame(string line)
    {
        foreach (var expected in new[]
        {
            "actual_transport=regular_nkn", "frame_type=filetransfer.chunk_batch.v4", "chunk_index=12-13",
        })
        {
            Assert.Contains("; " + expected + ";", line, StringComparison.Ordinal);
        }
        // The ignore diagnostic retains its existing frame/range fields, not
        // the expanded batch fields carried by send/receive/mismatch records.
        if (!line.Contains("event=filetransfer_data_frame_ignored;", StringComparison.Ordinal))
        {
            Assert.Contains("; start_chunk_index=12;", line, StringComparison.Ordinal);
            Assert.Contains("; last_end_chunk_exclusive=14;", line, StringComparison.Ordinal);
            Assert.Contains("; frame_chunk_count=2;", line, StringComparison.Ordinal);
            Assert.Contains("; raw_payload_bytes=2048;", line, StringComparison.Ordinal);
            Assert.Contains("; batch_chunk_count=2;", line, StringComparison.Ordinal);
        }
    }
}
