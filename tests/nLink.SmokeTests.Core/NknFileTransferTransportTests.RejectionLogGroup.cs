using System.Collections;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "PublicationUseEvidence")]
    public async Task NknTransport_RejectionLogGroupKeepsCapturedTupleAcrossCompetingWrites()
        => await WithSecurityCaptureSession(async (sender, receiver, senderClient, approvalBytes, ct) =>
        {
            var session = sender.CurrentSessionSecurityState.SessionId!.Value.Value;
            var frame = PublicationUseFrame(session);
            var cycle = FileTransferPublicationUseCaptureTests.CreateCycle(session, frame.TransferId, FileTransferDirection.Inbound);
            PublishPublicationUse(receiver, cycle[1]);
            var encrypted = EncryptWithSecurityEvidence(sender, FileTransferDataFrameCodec.SerializeLegacyV4(frame));
            var envelope = new Envelope(1, "rejection-group", "rejection-group", MsgType.FileTransferDataFrame, encrypted.Bytes, 0, null);
            var decodeArgs = new object?[] { senderClient.ConnectedBulkAddress, envelope, MsgType.FileTransferDataFrame, null, null, NknBridgeChannel.Bulk, true };
            Assert.True((bool)SecurityMethod("TryDecryptFileTransferPayloadWithEvidence").Invoke(receiver, decodeArgs)!);
            var evidence = SecurityMethod("CreateFileTransferDataPathEnvelopeEvidence").Invoke(null,
                [envelope.MessageId, frame.SessionId, frame.TransferId])!;
            evidence.GetType().GetProperty("SecurityEvidence")!.SetValue(evidence, decodeArgs[4]);
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(receiver, "fileTransferStates"));
            var stateCount = states.Count;
            var marker = Guid.NewGuid().ToString("D");
            var start = $"event=rejection_group_started; marker={marker}";
            var end = $"event=rejection_group_ended; marker={marker}";
            LocalOperationalLog.Info("RejectionGroupTest", start);
            var competingWrites = 0;
            var guardedFrame = frame with { DataSegments = new CompetingLogSegments(frame.DataSegments, () =>
            {
                // Count is read during diagnostic formatting, including the old
                // ignored-message formatting after receive/mismatch were logged.
                // Complete a real competing-thread write at every such boundary.
                Task.Run(() =>
                {
                    if (Interlocked.Increment(ref competingWrites) == 1) { PublishPublicationUse(receiver, cycle[2]); }
                    LocalOperationalLog.Info("RejectionGroupTest", "event=rejection_group_competing_write");
                }, ct).WaitAsync(TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult();
            }) };
            Assert.True((bool)SecurityMethod("ShouldRejectFileTransferPayloadReceive").Invoke(receiver,
                [guardedFrame, NknBridgeChannel.Bulk, FileTransferTransportKind.RegularNkn, evidence])!);
            Assert.Equal(stateCount, states.Count);
            Assert.True(competingWrites > 1);
            LocalOperationalLog.Info("RejectionGroupTest", end);
            Assert.True(LocalOperationalLog.TryReadRetainedSlice(start, end, out var log));
            var rows = DataPathProvenanceRows(log, DataPathEnvelopeFingerprint(envelope.MessageId));
            Assert.Equal(3, rows.Length);
            var names = new[] { "filetransfer_data_path_receive_result", "filetransfer_tuna_data_path_mismatch", "filetransfer_data_frame_ignored" };
            var levels = new[] { "INFO", "ERROR", "WARN" };
            var first = long.Parse(SecurityEvidenceValue(rows[0], "log_writer_sequence"));
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Contains($"[{levels[i]}] [SessionSecurity] event={names[i]};", rows[i], StringComparison.Ordinal);
                Assert.Contains(cycle[1].Evidence.FormatLogSuffix(), rows[i], StringComparison.Ordinal);
                AssertDataPathPublication(rows[i], cycle[1].Token);
                Assert.Contains(FormatSecurityEvidence(decodeArgs[4]!), rows[i], StringComparison.Ordinal);
                Assert.Contains("publication_use_kind=receive_guard", rows[i], StringComparison.Ordinal);
                Assert.Contains("receive_disposition=rejected_before_dispatch; authority_effect=none", rows[i], StringComparison.Ordinal);
                Assert.Equal(first + i, long.Parse(SecurityEvidenceValue(rows[i], "log_writer_sequence")));
                Assert.Equal(first + i, long.Parse(SecurityEvidenceValue(rows[i], "log_writer_batch_first_sequence")));
                Assert.Equal(first + i, long.Parse(SecurityEvidenceValue(rows[i], "log_writer_batch_last_sequence")));
            }
            Assert.Equal(3, rows.Select(row => SecurityEvidenceValue(row, "log_writer_batch_generation")).Distinct().Count());
            Assert.Single(rows.Select(row => SecurityEvidenceValue(row, "log_writer_id")).Distinct());
            Assert.All(log.Split('\n').Where(line => line.Contains("event=rejection_group_competing_write;", StringComparison.Ordinal)),
                row => Assert.True(long.Parse(SecurityEvidenceValue(row, "log_writer_sequence")) < first));
            Assert.DoesNotContain("event=filetransfer_data_frame_dispatched;", log, StringComparison.Ordinal);
            var lookup = new object?[] { session, frame.TransferId, FileTransferDirection.Inbound, null, null };
            Assert.True((bool)SecurityMethod("TryGetFileTransferDataPathRouteAuthorityWithEvidence").Invoke(receiver, lookup)!);
            Assert.Equal(cycle[2].Evidence, lookup[4]); // The old guard did not republish its captured route.
            Assert.True((bool)SecurityMethod("ShouldRejectFileTransferPayloadReceive").Invoke(receiver,
                [frame, NknBridgeChannel.Bulk, FileTransferTransportKind.RegularNkn, evidence])!);
            await Task.CompletedTask;
        });

    private sealed class CompetingLogSegments(IReadOnlyList<byte[]> segments, Action beforeCount) : IReadOnlyList<byte[]>
    {
        public int Count { get { beforeCount(); return segments.Count; } }
        public byte[] this[int index] => segments[index];
        public IEnumerator<byte[]> GetEnumerator() => segments.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
