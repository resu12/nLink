using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(3, false, false, false)]
    [InlineData(3, false, true, false)]
    [InlineData(0, false, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(3, true, false, false)]
    [InlineData(3, false, false, true)]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_DispatchRechecksCurrentPublicationBeforePayloadDelivery(
        int publicationIndex, bool tunaIngress, bool omitEvidence, bool controlFrame)
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            const string transfer = "security-transfer";
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            sender.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            receiver.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await sender.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = session, TransferId = transfer,
                FileName = "dispatch-guard.bin", FileSizeBytes = 2, PreferredDataProtocolVersion = 4 }, ct);
            await offered.Task.WaitAsync(ct);
            await receiver.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = session,
                TransferId = transfer, AcceptedDataProtocolVersion = 4 }, ct);
            await accepted.Task.WaitAsync(ct);
            await sender.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = session,
                TransferId = transfer, ProtocolVersion = 4, SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, ct);
            await opened.Task.WaitAsync(ct);
            using var inbound = await receiver.OpenFileTransferDataSessionAsync(session, transfer, ct);
            var cycle = FileTransferPublicationUseCaptureTests.CreateCycle(session, transfer, FileTransferDirection.Inbound);
            PublishPublicationUse(receiver, cycle[0]);
            FileTransferDataFrame frame = controlFrame
                ? new FileTransferStateFrameV4 { SessionId = session, TransferId = transfer,
                    TransferPaused = true, TransferPauseReason = "ui_pause" }
                : PublicationUseFrame(session);
            var encrypted = EncryptWithSecurityEvidence(sender, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
            var envelope = new Envelope(1, "dispatch-route", "dispatch-route", MsgType.FileTransferDataFrame, encrypted.Bytes, 0, null);
            var args = new object?[] { senderClient.ConnectedBulkAddress, NknBridgeChannel.Bulk, envelope, null };
            var accelerated = typeof(NknSignalingTransport).GetField("handlingTunaAcceleratedInboundMessage",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var previousAccelerated = accelerated.GetValue(null);
            try
            {
                accelerated.SetValue(null, tunaIngress);
                Assert.True((bool)SecurityMethod("TryPrepareFileTransferDataFrameDispatch").Invoke(receiver, args)!);
            }
            finally { accelerated.SetValue(null, previousAccelerated); }

            // Hold the real prepared callback, not a synthetic frame delivery.
            // No scheduler timing or production test hook is needed to reproduce
            // a publication change after ingress but before queued dispatch.
            var queueCount = inbound.GetType().GetField("queuedFrameCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(0, Assert.IsType<int>(queueCount.GetValue(inbound)));
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferStates"));
            var stateAtDispatch = states[transfer];
            for (var i = 1; i <= publicationIndex; i++) { PublishPublicationUse(receiver, cycle[i]); }
            if (omitEvidence) { PublishPublicationUse(receiver, cycle[publicationIndex], omitEvidence: true); }
            var start = $"event=dispatch_route_guard_started; marker={Guid.NewGuid():D}";
            var end = $"event=dispatch_route_guard_ended; marker={Guid.NewGuid():D}";
            LocalOperationalLog.Info("DispatchRouteGuardTest", start);
            Assert.IsType<Action>(SecurityProperty(args[3]!, "Dispatch"))();
            LocalOperationalLog.Info("DispatchRouteGuardTest", end);
            var reject = publicationIndex is 1 or 3 && !tunaIngress && !controlFrame;
            Assert.Equal(reject ? 0 : 1, Assert.IsType<int>(queueCount.GetValue(inbound)));
            Assert.Equal(stateAtDispatch, states[transfer]);
            Assert.True(LocalOperationalLog.TryReadRetainedSlice(start, end, out var log));
            var rows = DataPathProvenanceRows(log, DataPathEnvelopeFingerprint(envelope.MessageId));
            Assert.Equal(reject ? 3 : tunaIngress ? 2 : 1, rows.Length);
            Assert.All(rows, row =>
            {
                AssertDataPathPublication(row, cycle[publicationIndex].Token);
                Assert.Contains("receive_disposition=" + (reject ? "rejected_before_dispatch" : "dispatch_entered"), row, StringComparison.Ordinal);
                Assert.Equal(SecurityFingerprint("payload", "ftpay1-", envelope.Payload), SecurityEvidenceValue(row, "security_payload_fingerprint"));
                Assert.Equal(!omitEvidence, row.Contains("publication_use_kind=", StringComparison.Ordinal));
                if (!omitEvidence)
                {
                    Assert.Contains(cycle[publicationIndex].Evidence.FormatLogSuffix(), row, StringComparison.Ordinal);
                    Assert.Contains("publication_use_kind=" + (reject ? "receive_guard" : "receive_dispatch"), row, StringComparison.Ordinal);
                }
            });
            if (reject)
            {
                Assert.Equal(0L, Assert.IsType<long>(inbound.GetType()
                    .GetField("queuedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inbound)));
                Assert.All(rows, row => Assert.Contains("authority_effect=none", row, StringComparison.Ordinal));
                Assert.Contains("event=filetransfer_data_frame_ignored;", rows[2], StringComparison.Ordinal);
                var sequence = long.Parse(SecurityEvidenceValue(rows[0], "log_writer_sequence"));
                for (var i = 0; i < rows.Length; i++) { Assert.Equal(sequence + i, long.Parse(SecurityEvidenceValue(rows[i], "log_writer_sequence"))); }
                Assert.DoesNotContain("event=filetransfer_data_frame_dispatched;", log, StringComparison.Ordinal);
                Assert.DoesNotContain("receive_disposition=dispatch_entered", log, StringComparison.Ordinal);
            }
            else
            {
                var delivered = await inbound.ReceiveAsync(ct);
                if (controlFrame)
                {
                    Assert.True(Assert.IsType<FileTransferStateFrameV4>(delivered).TransferPaused);
                }
                else
                {
                    Assert.Equal(((FileTransferChunkBatchFrameV4)frame).DataSegments[0],
                        Assert.IsType<FileTransferChunkBatchFrameV4>(delivered).DataSegments[0]);
                }
            }
            var lookup = new object?[] { session, transfer, FileTransferDirection.Inbound, null, null };
            Assert.True((bool)SecurityMethod("TryGetFileTransferDataPathRouteAuthorityWithEvidence").Invoke(receiver, lookup)!);
            Assert.Equal(omitEvidence ? null : cycle[publicationIndex].Evidence, lookup[4]);
        });
}
