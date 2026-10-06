using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    private sealed class AuthenticatedTransferOwner
    {
        private readonly AuthenticatedTransferOwner original;
        private TransferRun? run;
        private byte[]? envelopeBytes;
        private string? originalMessage, seededMessage;
        private FileTransferRoutePublicationEvidence? publication;

        private AuthenticatedTransferOwner() { original = this; }

        internal static async Task<AuthenticatedTransferOwner> CreateAsync(string directory)
        {
            var owner = new AuthenticatedTransferOwner();
            await NknAccelerationTransportTests.WithAuthenticatedPublicationPairAsync(async (sender, receiver, transport, packets, release, ct) =>
            {
                Directory.CreateDirectory(directory);
                var sourcePath = Path.Combine(directory, "source.bin");
                var destinationPath = Path.Combine(directory, "received.bin");
                await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, PayloadBytes).Select(i => (byte)(i % 251)).ToArray(), ct);
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var expected = Digest(source, sourcePath);
                var attempt = Guid.NewGuid();
                var sessionId = transport.CurrentSessionSecurityState.SessionId!.Value.Value;
                var transferId = "authenticated_" + attempt.ToString("N");
                var opened = new ConcurrentBag<FileStream>();
                Assert.NotNull(await sender.TryStartSendAsync(new("source.bin", PayloadBytes, transferId), _ =>
                {
                    var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    opened.Add(file);
                    return Task.FromResult<Stream>(file);
                }, ct));
                await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision, 5000);
                Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId,
                    (_, _) => Task.FromResult(CreateTempReceiveDestination(destinationPath)), ct));
                await WaitUntilAsync(() => !packets.IsEmpty, 5000);
                Assert.True(packets.TryPeek(out owner.envelopeBytes));
                Assert.True(EnvelopeCodec.TryDeserialize(owner.envelopeBytes, out var envelope));
                Assert.Equal(MsgType.FileTransferDataFrame, envelope.Type);

                // Verify bytes from the original send with this transport's
                // installed key. Never encrypt a replacement or borrow a context.
                object?[] arguments = [null];
                var key = Assert.IsType<byte[]>(NknCaptureMethod("GetFileTransferKeyWithSecurityEvidence").Invoke(transport, arguments));
                SessionSecureEnvelopePayload decoded;
                try { decoded = SessionSecureEnvelopeCodec.Decrypt(key, envelope.Payload); }
                finally { CryptographicOperations.ZeroMemory(key); }
                Assert.Equal(sessionId, decoded.Metadata.SessionId.Value);
                Assert.Equal(transferId, decoded.Metadata.RequestId);
                Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(decoded.Plaintext, out var frame));
                var batch = Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
                Assert.Equal((sessionId, transferId), (batch.SessionId, batch.TransferId));
                var security = NknCaptureMethod("CaptureFileTransferSecurityEvidence").Invoke(null, [arguments[0], decoded.Metadata, envelope.Payload]);
                var securitySuffix = Assert.IsType<string>(NknCaptureMethod("FormatFileTransferSecurityEvidenceSuffix").Invoke(null, [security]));
                Assert.Contains("security_context_status=captured;", securitySuffix, StringComparison.Ordinal);
                var envelopeEvidence = NknCaptureMethod("CreateFileTransferDataPathEnvelopeEvidence").Invoke(null, [envelope.MessageId, sessionId, transferId])!;
                var fingerprint = Assert.IsType<string>(envelopeEvidence.GetType().GetProperty("EnvelopeFingerprint")!.GetValue(envelopeEvidence));
                string? sent = null;
                await WaitUntilAsync(() =>
                {
                    sent = LocalOperationalLog.GetRecentLogText().Split('\n').SingleOrDefault(line =>
                        line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal) &&
                        line.Contains("data_path_envelope_fingerprint=" + fingerprint + ";", StringComparison.Ordinal));
                    return sent is not null;
                }, 5000);
                owner.originalMessage = sent![sent.IndexOf("event=", StringComparison.Ordinal)..].TrimEnd('\r');
                Assert.Contains(securitySuffix, owner.originalMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("log_writer_", owner.originalMessage, StringComparison.Ordinal);

                lock (GetPrivateField(sender, "gate")!)
                {
                    var context = GetPrivateField(sender, "outboundTransfer")!;
                    var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, context);
                    var token = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                    Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, token, out _));
                    Assert.Equal(FileTransferRoute.FileTunaV4, token.Route);
                    Assert.Equal((sessionId, transferId), (token.SessionId, token.TransferId));
                    var emitter = context.GetType().GetProperty("CoordinatorSnapshotEmitter")!.GetValue(context)!;
                    owner.publication = Assert.IsType<FileTransferRoutePublicationEvidence>(emitter.GetType()
                        .GetMethod("GetRoutePublicationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, [token]));
                    Assert.Contains(owner.publication.FormatLogSuffix(), owner.originalMessage, StringComparison.Ordinal);
                    Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
                    Assert.False(sender.Snapshot.Outbound!.IsTerminal);
                    Assert.False(receiver.Snapshot.Inbound.IsTerminal);
                    var fields = new TerminalLogRow(0, owner.originalMessage);
                    Assert.Equal("tuna", fields.Field("actual_transport"));
                    Assert.Equal("tuna", fields.Field("delivery_transport"));
                    Assert.Equal("1", fields.Field("sent_via_acceleration"));
                    Assert.Equal("v4_physical_submission", fields.Field("publication_use_kind"));
                    // Preserve the original authenticated record. Seed a new,
                    // explicitly labeled observation while its owner is current.
                    // Keep the content-evidence suffix last for strict redaction.
                    owner.seededMessage = owner.originalMessage.Replace("; actual_transport=tuna;", "; actual_transport=regular_nkn;")
                        .Replace("; delivery_transport=tuna;", "; delivery_transport=regular_nkn;")
                        .Replace("; sent_via_acceleration=1;", "; sent_via_acceleration=0;")
                        .Replace("event=filetransfer_data_path_send_result;", "event=filetransfer_data_path_send_result; " +
                            "offline_fixture_fault=wrong_physical_transport; offline_fixture_scope=authenticated_same_service_transfer;");
                    Assert.Equal(owner.seededMessage, SensitiveDataRedactor.Redact(owner.seededMessage));
                    LocalOperationalLog.Info("SessionSecurity", owner.seededMessage);
                }
                release();
                await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                    receiver.Snapshot.InboundState == FileTransferTransferState.Completed, 8000);
                owner.run = new(attempt, directory, sessionId, transferId, sourcePath, destinationPath, expected,
                    Assert.IsType<FileTransferTransferSnapshot>(sender.Snapshot.Outbound),
                    Assert.IsType<FileTransferTransferSnapshot>(receiver.Snapshot.Inbound));
                Assert.Equal(expected, Digest(source, sourcePath));
                sender.Dispose(); receiver.Dispose();
                Assert.NotEmpty(opened);
                await WaitUntilAsync(() => opened.All(file => !file.CanRead), 5000);
            });
            return owner;
        }

        internal AuthenticatedCapture Capture(string path)
        {
            Assert.NotNull(run);
            var files = CaptureBoundEvidence(CaptureBoundOwner(run, path));
            var originalRow = Assert.Single(files.Owner.Rows, row => row.RawLine.Contains(originalMessage!, StringComparison.Ordinal));
            var seededRow = Assert.Single(files.Owner.Rows, row => row.RawLine.Contains(seededMessage!, StringComparison.Ordinal));
            Assert.True(originalRow.LineNumber < seededRow.LineNumber);
            Assert.All(files.Terminals, row => Assert.True(row.LineNumber > seededRow.LineNumber));
            return new(this, files, envelopeBytes!, publication!, originalRow, seededRow);
        }

        internal string CheckCapture(AuthenticatedCapture capture)
        {
            if (!ReferenceEquals(this, original) || !ReferenceEquals(this, capture.Owner)) { return "authenticated_owner"; }
            if (!ReferenceEquals(run, capture.Files.Owner.Run)) { return "authenticated_transfer"; }
            if (!ReferenceEquals(envelopeBytes, capture.Envelope) || !ReferenceEquals(publication, capture.Publication)) { return "authenticated_capture"; }
            if (!ReferenceEquals(run!.Outbound, capture.Files.Terminal.Outbound) || !ReferenceEquals(run.Inbound, capture.Files.Terminal.Inbound))
            { return "authenticated_terminal"; }
            foreach (var (row, message) in new[] { (capture.Original, originalMessage), (capture.Seeded, seededMessage) })
                if (row.LineNumber < 1 || row.LineNumber > capture.Files.Owner.Rows.Length ||
                    !ReferenceEquals(row, capture.Files.Owner.Rows[row.LineNumber - 1]) ||
                    !row.RawLine.Contains(message!, StringComparison.Ordinal) ||
                    row.Field("publication_evidence_transfer_id") != publication!.TransferId) { return "authenticated_origin"; }
            return CheckBinding(capture.Files.Owner, capture.Files);
        }
    }

    private sealed record AuthenticatedCapture(AuthenticatedTransferOwner Owner, BoundCapture Files, byte[] Envelope,
        FileTransferRoutePublicationEvidence Publication, TerminalLogRow Original, TerminalLogRow Seeded);

    private static MethodInfo NknCaptureMethod(string name)
        => typeof(NknSignalingTransport).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static async Task ProduceAuthenticatedPublicationFixtureAsync(bool foreignControl)
    {
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var path = Path.Combine(directory, "nlink.log");
        Assert.Equal(path, LocalOperationalLog.LogFilePath);
        var owner = await AuthenticatedTransferOwner.CreateAsync(Path.Combine(directory, "transfer"));
        var foreign = foreignControl ? await AuthenticatedTransferOwner.CreateAsync(Path.Combine(directory, "foreign-transfer")) : null;
        await WaitUntilAsync(() => ReadTerminalRows(path).Length == (foreign is null ? 2 : 4), 5000);
        Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
        var capture = owner.Capture(path);
        Assert.Equal(Bound, owner.CheckCapture(capture));
        Assert.Equal("authenticated_capture", owner.CheckCapture(capture with { Envelope = capture.Envelope.ToArray() }));
        Assert.Equal("authenticated_capture", owner.CheckCapture(capture with { Publication = capture.Publication with { } }));
        Assert.Equal("authenticated_origin", owner.CheckCapture(capture with { Original = capture.Original with { } }));
        Assert.Equal("authenticated_origin", owner.CheckCapture(capture with { Seeded = capture.Seeded with { } }));
        Assert.Equal("authenticated_origin", owner.CheckCapture(capture with { Original = capture.Seeded }));
        Assert.Equal("authenticated_origin", owner.CheckCapture(capture with { Seeded = capture.Original }));
        Assert.Equal("authenticated_terminal", owner.CheckCapture(capture with
        {
            Files = capture.Files with { Terminal = capture.Files.Terminal with { Outbound = capture.Files.Terminal.Outbound! with { } } },
        }));
        var copy = Assert.IsType<AuthenticatedTransferOwner>(typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null));
        Assert.Equal("authenticated_owner", copy.CheckCapture(capture with { Owner = copy }));
        var controls = new Dictionary<string, string>();
        if (foreign is not null)
        {
            var other = foreign.Capture(path);
            Assert.Equal(Bound, foreign.CheckCapture(other));
            Assert.Equal(capture.Files.Terminal.Destination!.Sha256Base64, other.Files.Terminal.Destination!.Sha256Base64);
            Reject("foreign_owner", other, "authenticated_owner");
            Reject("relabeled_owner", other with { Owner = owner }, "authenticated_transfer");
            Reject("foreign_payload", capture with { Files = other.Files }, "authenticated_transfer");
            Reject("foreign_envelope", capture with { Envelope = other.Envelope }, "authenticated_capture");
            Reject("foreign_publication", capture with { Publication = other.Publication }, "authenticated_capture");
            Reject("foreign_failure", capture with { Seeded = other.Seeded }, "authenticated_origin");
            Reject("foreign_terminals", capture with { Files = capture.Files with { Terminals = other.Files.Terminals } }, "terminal_scope_mismatch");
        }
        WriteBoundDiagnostic(directory, "authenticated-terminals.json", capture.Files.Owner, capture.Files, Bound, controls);
        File.WriteAllText(Path.Combine(directory, "authenticated-producer.json"), JsonSerializer.Serialize(new
        {
            Status = "verified_offline_authenticated_transfer_producer", ForeignControl = foreignControl, Controls = controls,
            capture.Publication, capture.Original, capture.Seeded, PayloadBytes, PrivateOwnerVerified = true,
            ActualEncryptedEnvelopeVerified = true, OriginalPublicationCurrentAtSeed = true, ReceiverBytesAtSeed = 0,
            AnalyzerEvaluations = 0, TerminalConsumerEnabled = false, AcceptanceChanged = false, Live = false,
        }, JsonOptions));
        void Reject(string name, AuthenticatedCapture candidate, string expected)
        {
            var reason = owner.CheckCapture(candidate);
            Assert.Equal(expected, reason);
            controls.Add(name, reason);
        }
    }
}
