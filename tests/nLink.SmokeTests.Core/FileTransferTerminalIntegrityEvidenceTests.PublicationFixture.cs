using System.Collections.Concurrent;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    // Producer-side test custody only. No analyzer or terminal consumer accepts
    // this object or its diagnostic JSON as route/acceptance authority.
    private sealed class PublicationTransferOwner
    {
        internal Guid Attempt { get; } = Guid.NewGuid();
        internal string Directory { get; }
        private readonly PublicationTransferOwner original;
        private SessionFileTransferService? sender, receiver;
        private FileTransferTransferSnapshot? outbound, inbound;
        private TransferRun? run;
        private FileTransferDataPathSendAuthority? authority;
        private FileTransferPublicationUseEvidence? use;
        private string? observation;
        private int observed;

        internal PublicationTransferOwner(string directory)
        {
            original = this;
            Directory = directory;
        }

        internal async Task AttachAsync(SessionFileTransferService sender, SessionFileTransferService receiver,
            LoopbackFileTransferTransport senderTransport, LoopbackFileTransferTransport receiverTransport,
            string sessionId, string transferId, CancellationToken ct)
        {
            Assert.Null(this.sender);
            this.sender = sender; this.receiver = receiver;
            senderTransport.IsFileTunaActiveForRouteSelection = true;
            receiverTransport.IsFileTunaActiveForRouteSelection = true;
            senderTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound);
            receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
            var data = await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);
            var submissions = Assert.IsType<ConcurrentQueue<FileTransferDataPathSendAuthority>>(
                data.GetType().GetProperty("SentDataPathSendAuthorities")!.GetValue(data));
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            {
                if (frame is not FileTransferChunkBatchFrameV4 batch || Interlocked.CompareExchange(ref observed, 1, 0) != 0)
                { return Task.FromResult(false); }

                // This callback runs synchronously inside the original send:
                // the loopback recorded the exact authority before delivery.
                authority = submissions.Last();
                use = Assert.IsType<FileTransferPublicationUseEvidence>(FileTransferPublicationUseEvidence.GetSend(authority));
                Assert.Equal(FileTransferPublicationUseKind.V4PhysicalSubmission, use.Kind);
                Assert.Equal(sessionId, authority.SessionId);
                Assert.Equal(transferId, authority.TransferId);
                Assert.Equal(FileTransferDirection.Outbound, authority.Direction);
                Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, authority.RouteToken);
                Assert.Equal(4, authority.ProtocolVersion);
                Assert.Equal(FileTransferTransportKind.Tuna, authority.TargetTransport);
                Assert.Equal(TerminalToken(sessionId), use.Publication.SessionId);
                Assert.Equal(TerminalToken(transferId), use.Publication.TransferId);
                Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);
                Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);
                observation =
                    $"event=filetransfer_data_path_send_result; session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                    $"route={authority.RouteToken}; protocol_version={authority.ProtocolVersion}; live_route_epoch={authority.LiveRouteEpoch}; " +
                    $"runtime_generation={authority.RuntimeGeneration}; terminal_generation={authority.TerminalGeneration}; leg_generation={authority.LegGeneration}; " +
                    $"route_publication_generation={authority.RoutePublicationGeneration}; route_commit_generation={authority.RouteCommitGeneration ?? 0}; " +
                    "target_transport=tuna; actual_transport=regular_nkn; delivery_transport=regular_nkn; " +
                    $"frame_type={batch.Type}; raw_payload_bytes={batch.DataSegments.Sum(segment => segment.Length)}; " +
                    $"batch_chunk_count={batch.ChunkCount}; batch_start_chunk={batch.StartChunkIndex}; batch_end_chunk_exclusive={batch.StartChunkIndex + batch.ChunkCount}; " +
                    "delivery_result=sent; offline_fixture_fault=wrong_physical_transport; offline_fixture_scope=same_service_transfer" + use.FormatLogSuffix();
                LocalOperationalLog.Info("SessionSecurity", observation);
                // Only the observation is seeded. Original bytes, metadata and
                // delivery continue unchanged; this is not a real Tuna socket.
                return Task.FromResult(false);
            };
        }

        internal (FileTransferTransferSnapshot Outbound, FileTransferTransferSnapshot Inbound) CaptureTerminals()
        {
            Assert.Null(outbound); Assert.Null(inbound);
            outbound = Assert.IsType<FileTransferTransferSnapshot>(sender!.Snapshot.Outbound);
            inbound = Assert.IsType<FileTransferTransferSnapshot>(receiver!.Snapshot.Inbound);
            return (outbound, inbound);
        }

        internal void Complete(TransferRun completed)
        {
            Assert.Null(run);
            Assert.Equal(Attempt, completed.Attempt);
            Assert.Same(outbound, completed.Outbound);
            Assert.Same(inbound, completed.Inbound);
            Assert.NotNull(authority); Assert.NotNull(use); Assert.NotNull(observation);
            Assert.Equal(authority.SessionId, completed.SessionId);
            Assert.Equal(authority.TransferId, completed.TransferId);
            Assert.Equal(FileTransferTransferState.Completed, completed.Outbound.State);
            Assert.Equal(FileTransferTransferState.Completed, completed.Inbound.State);
            Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, completed.Outbound.RouteToken);
            Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, completed.Inbound.RouteToken);
            run = completed;
        }

        internal PublicationTransferCapture Capture(string logPath)
        {
            Assert.NotNull(run);
            var bound = CaptureBoundOwner(run, logPath);
            var failure = Assert.Single(bound.Rows, row =>
                row.Field("offline_fixture_scope") == "same_service_transfer" &&
                row.Field("publication_evidence_transfer_id") == use!.Publication.TransferId);
            Assert.Contains(SensitiveDataRedactor.Redact(observation!), failure.RawLine, StringComparison.Ordinal);
            return new(this, CaptureBoundEvidence(bound), authority!, use!, failure);
        }

        internal string CheckCapture(PublicationTransferCapture capture)
        {
            if (!ReferenceEquals(this, original) || !ReferenceEquals(this, capture.Owner)) { return "publication_owner"; }
            if (!ReferenceEquals(run, capture.Files.Owner.Run)) { return "publication_transfer"; }
            if (!ReferenceEquals(outbound, capture.Files.Terminal.Outbound) || !ReferenceEquals(inbound, capture.Files.Terminal.Inbound))
            { return "publication_terminal"; }
            if (!ReferenceEquals(authority, capture.Authority) || !ReferenceEquals(use, capture.Use) ||
                !ReferenceEquals(use, FileTransferPublicationUseEvidence.GetSend(authority))) { return "publication_use"; }
            if (capture.Failure.LineNumber < 1 || capture.Failure.LineNumber > capture.Files.Owner.Rows.Length ||
                !ReferenceEquals(capture.Failure, capture.Files.Owner.Rows[capture.Failure.LineNumber - 1]) ||
                capture.Failure.Field("publication_evidence_transfer_id") != use!.Publication.TransferId)
            { return "publication_origin"; }
            return CheckBinding(capture.Files.Owner, capture.Files);
        }
    }

    private sealed record PublicationTransferCapture(PublicationTransferOwner Owner, BoundCapture Files,
        FileTransferDataPathSendAuthority Authority, FileTransferPublicationUseEvidence Use, TerminalLogRow Failure);

    internal static async Task ProduceSameTransferPublicationFixtureAsync(bool foreignControl)
    {
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var path = Path.Combine(directory, "nlink.log");
        Assert.Equal(path, LocalOperationalLog.LogFilePath);
        var owner = new PublicationTransferOwner(Path.Combine(directory, "transfer"));
        await TransferAsync(publicationOwner: owner);
        var foreign = foreignControl ? new PublicationTransferOwner(Path.Combine(directory, "foreign-transfer")) : null;
        if (foreign is not null) { await TransferAsync(publicationOwner: foreign); }
        await WaitUntilAsync(() => ReadTerminalRows(path).Length == (foreign is null ? 2 : 4), 5000);
        Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
        var capture = owner.Capture(path);
        Assert.Equal(Bound, owner.CheckCapture(capture));
        Assert.Equal("publication_use", owner.CheckCapture(capture with { Authority = capture.Authority with { } }));
        Assert.Equal("publication_use", owner.CheckCapture(capture with { Use = capture.Use with { } }));
        Assert.Equal("publication_origin", owner.CheckCapture(capture with { Failure = capture.Failure with { } }));
        Assert.Equal("publication_terminal", owner.CheckCapture(capture with
        { Files = capture.Files with { Terminal = capture.Files.Terminal with { Outbound = capture.Files.Terminal.Outbound! with { } } } }));
        var copiedOwner = Assert.IsType<PublicationTransferOwner>(typeof(object)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(owner, null));
        Assert.Equal("publication_owner", copiedOwner.CheckCapture(capture with { Owner = copiedOwner }));
        Assert.Null(capture.Failure.Field("security_context_status"));
        var controls = new Dictionary<string, string>();
        if (foreign is not null)
        {
            var other = foreign.Capture(path);
            Assert.Equal(Bound, foreign.CheckCapture(other));
            Assert.Equal(capture.Files.Terminal.Destination!.Sha256Base64, other.Files.Terminal.Destination!.Sha256Base64);
            Reject("foreign_owner", other, "publication_owner");
            Reject("relabeled_owner", other with { Owner = owner }, "publication_transfer");
            Reject("foreign_payload", capture with { Files = other.Files }, "publication_transfer");
            Reject("foreign_publication", capture with { Use = other.Use }, "publication_use");
            Reject("foreign_failure", capture with { Failure = other.Failure }, "publication_origin");
            Reject("foreign_terminals", capture with { Files = capture.Files with { Terminals = other.Files.Terminals } }, "terminal_scope_mismatch");
        }
        Assert.Equal(Bound, owner.CheckCapture(capture));
        WriteBoundDiagnostic(directory, "same-transfer-terminals.json", capture.Files.Owner, capture.Files, Bound, controls);
        File.WriteAllText(Path.Combine(directory, "same-transfer-producer.json"), JsonSerializer.Serialize(new
        {
            Status = "verified_offline_same_transfer_producer", ForeignControl = foreignControl, Controls = controls,
            Publication = capture.Use.Publication, Failure = capture.Failure,
            TerminalLines = capture.Files.Terminals.Select(row => row.LineNumber), PayloadBytes,
            PrivateOwnerVerified = true, SeededObservation = true, RealServiceCompletion = true,
            AnalyzerEvaluations = 0, TerminalConsumerEnabled = false, AcceptanceChanged = false, Live = false,
        }, JsonOptions));

        void Reject(string name, PublicationTransferCapture candidate, string expected)
        {
            var reason = owner.CheckCapture(candidate);
            Assert.Equal(expected, reason);
            controls.Add(name, reason);
        }
    }
}
