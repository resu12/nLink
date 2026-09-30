using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("FileTransferRuntimeSuite", "OfflineTerminalIntegrity")]
public sealed partial class FileTransferTerminalIntegrityEvidenceTests : SessionFileTransferServiceTestBase
{
    private const int PayloadBytes = 256 * 1024;
    private const string Verified = "verified_offline_terminal_integrity";

    [Fact]
    public async Task TerminalIntegrity_RealCompletedFileAndDetachedControls()
    {
        var run = await TransferAsync();
        var evidence = Observe(run);
        Assert.Equal(Verified, Check(run, evidence));
        Assert.Equal("regular_nkn_v4_fast", evidence.Outbound!.RouteToken);
        Assert.Equal(4, evidence.Outbound.ProtocolVersion);
        Assert.Equal("regular_nkn_v4_fast", evidence.Inbound!.RouteToken);
        Assert.Equal(4, evidence.Inbound.ProtocolVersion);
        Assert.False(File.Exists(run.DestinationPath + ".part"));
        WriteDiagnostic(run, evidence, "terminal-evidence.json");

        // Copies are predicate controls, never new owner-issued transfer history.
        var controls = new Dictionary<string, string>();
        foreach (var outbound in new[] { true, false })
        {
            var peer = (outbound ? evidence.Outbound : evidence.Inbound)!;
            var mutations = new (string Name, FileTransferTransferSnapshot? Snapshot, string Reason)[]
            {
                ("missing", null, "terminal_missing"),
                ("failed", peer with { State = FileTransferTransferState.Failed }, "terminal_not_completed"),
                ("nonterminal", peer with { State = FileTransferTransferState.Verifying }, "terminal_not_completed"),
                ("error", peer with { ErrorCode = "unexpected" }, "terminal_error"),
                ("session", peer with { SessionId = "foreign" }, "terminal_scope"),
                ("transfer", peer with { TransferId = "foreign" }, "terminal_scope"),
                ("direction", peer with { Direction = outbound ? FileTransferDirection.Inbound : FileTransferDirection.Outbound }, "terminal_scope"),
                ("size", peer with { FileSizeBytes = PayloadBytes - 1 }, "terminal_size"),
                ("progress", peer with { BytesTransferred = PayloadBytes - 1 }, "terminal_size"),
                ("hash", peer with { Sha256Base64 = "foreign" }, "terminal_hash"),
            };
            foreach (var mutation in mutations)
            {
                var copy = outbound ? evidence with { Outbound = mutation.Snapshot } : evidence with { Inbound = mutation.Snapshot };
                Reject((outbound ? "outbound_" : "inbound_") + mutation.Name, copy, mutation.Reason);
            }
        }
        Reject("attempt", evidence with { Attempt = Guid.NewGuid() }, "attempt_scope");
        Reject("source_path", evidence with { Source = evidence.Source with { Path = "foreign" } }, "source_scope");
        Reject("source_changed", evidence with { Source = evidence.Source with { Sha256Base64 = "foreign" } }, "source_changed");
        Reject("destination_missing", evidence with { Destination = null }, "destination_missing");
        Reject("destination_path", evidence with { Destination = evidence.Destination! with { Path = "foreign" } }, "destination_scope");
        Reject("saved_path", evidence with { Inbound = evidence.Inbound with { SavedFilePath = "foreign" } }, "destination_scope");
        Assert.Equal(Verified, Check(run, evidence));
        File.WriteAllText(Path.Combine(run.Directory, "detached-controls.json"), JsonSerializer.Serialize(new
        {
            Passed = controls.Count, DetachedOnly = true, AuthorityEvaluations = 0, AcceptanceChanged = false, Controls = controls,
        }, JsonOptions));

        void Reject(string name, Capture copy, string expected)
        {
            var reason = Check(run, copy);
            Assert.Equal(expected, reason);
            controls.Add(name, reason);
        }
    }

    [Theory]
    [InlineData("missing", "destination_missing")]
    [InlineData("truncated", "destination_size")]
    [InlineData("extra", "destination_size")]
    [InlineData("corrupt", "destination_hash")]
    [InlineData("source_changed", "source_changed")]
    public async Task TerminalIntegrity_RealAlteredFilesCannotReuseCompletion(string change, string expected)
    {
        var run = await TransferAsync();
        var original = Observe(run);
        Assert.Equal(Verified, Check(run, original));
        WriteDiagnostic(run, original, "terminal-evidence.json");
        if (change == "missing")
        {
            File.Move(run.DestinationPath, Path.Combine(run.Directory, "retained-destination.bin"));
        }
        else
        {
            var path = change == "source_changed" ? run.SourcePath : run.DestinationPath;
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (change == "truncated") { file.SetLength(file.Length - 1); }
            else if (change == "extra") { file.Position = file.Length; file.WriteByte(42); }
            else
            {
                file.Position = file.Length / 2;
                var value = file.ReadByte();
                file.Position--;
                file.WriteByte((byte)(value ^ 0xff));
            }
            file.Flush(flushToDisk: true);
        }
        var altered = Observe(run);
        Assert.Equal(expected, Check(run, altered));
        // The original observation is historical only, not a fresh file check.
        WriteDiagnostic(run, altered, "altered-file-evidence.json");
    }

    [Fact]
    public async Task TerminalIntegrity_RealCanceledTransferIsNotCompletion()
    {
        var run = await TransferAsync(interrupt: true);
        var evidence = Observe(run);
        Assert.Equal(FileTransferTransferState.Canceled, evidence.Outbound!.State);
        Assert.Equal(FileTransferTransferState.Canceled, evidence.Inbound!.State);
        Assert.True(evidence.Outbound.BytesTransferred < PayloadBytes);
        Assert.True(evidence.Inbound.BytesTransferred < PayloadBytes);
        Assert.Null(evidence.Destination);
        Assert.Equal("terminal_not_completed", Check(run, evidence));
        WriteDiagnostic(run, evidence, "interrupted-evidence.json");
    }

    [Fact]
    public async Task TerminalIntegrity_AnotherCompletedAttemptCannotSupplyEvidence()
    {
        var first = await TransferAsync();
        var second = await TransferAsync();
        var previous = Observe(first);
        var current = Observe(second);
        Assert.Equal(Verified, Check(first, previous));
        Assert.Equal(Verified, Check(second, current));
        Assert.Equal(previous.Destination!.Sha256Base64, current.Destination!.Sha256Base64);
        Assert.Equal("capture_owner", Check(second, previous));
        Assert.Equal("capture_owner", Check(second, previous with { Attempt = second.Attempt }));
        WriteDiagnostic(first, previous, "terminal-evidence.json");
        WriteDiagnostic(second, current, "terminal-evidence.json");
    }

    private static async Task<TransferRun> TransferAsync(bool interrupt = false, PublicationTransferOwner? publicationOwner = null)
    {
        var attempt = publicationOwner?.Attempt ?? Guid.NewGuid();
        var directory = publicationOwner?.Directory ?? Path.Combine(FindRepoRoot(), "artifacts", "terminal-integrity-evidence-20260925", attempt.ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.bin");
        var destinationPath = Path.Combine(directory, "received.bin");
        var payload = Enumerable.Range(0, PayloadBytes).Select(i => (byte)(i % 251)).ToArray();
        await using (var create = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await create.WriteAsync(payload);
        }
        // Keep the actual source read-locked across hash preparation and transfer.
        using var sourceLease = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expected = Digest(sourceLease, sourcePath);
        Assert.Equal(PayloadBytes, expected.Bytes);
        var sessionId = "terminal_session_" + attempt.ToString("N");
        var transferId = "terminal_transfer_" + attempt.ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var openedSources = new ConcurrentBag<FileStream>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (publicationOwner is not null)
        {
            Assert.False(interrupt);
            await publicationOwner.AttachAsync(sender, receiver, senderTransport, receiverTransport, sessionId, transferId, deadline.Token);
        }
        if (interrupt)
        {
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV4)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return false;
            };
        }
        Assert.NotNull(await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("source.bin", PayloadBytes, transferId),
            _ =>
            {
                var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                openedSources.Add(stream);
                return Task.FromResult<Stream>(stream);
            }, deadline.Token));
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision, 5000);
        Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId,
            (_, _) => Task.FromResult(CreateTempReceiveDestination(destinationPath)), deadline.Token));
        if (interrupt)
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.NotNull(await sender.CancelTransferAsync(transferId, "terminal_integrity_control", deadline.Token));
        }
        var state = interrupt ? FileTransferTransferState.Canceled : FileTransferTransferState.Completed;
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == state && receiver.Snapshot.InboundState == state, 10000);
        var (outbound, inbound) = publicationOwner is null
            ? (Assert.IsType<FileTransferTransferSnapshot>(sender.Snapshot.Outbound), Assert.IsType<FileTransferTransferSnapshot>(receiver.Snapshot.Inbound))
            : publicationOwner.CaptureTerminals();
        Assert.Equal(expected, Digest(sourceLease, sourcePath));
        var run = new TransferRun(attempt, directory, sessionId, transferId, sourcePath, destinationPath, expected, outbound, inbound);
        publicationOwner?.Complete(run);
        // Sparse-pump cleanup is asynchronous even after Service.Dispose().
        // Wait for the Service to close its factory streams; do not force-close
        // them or weaken sharing to make the changed-source control writable.
        sender.Dispose();
        receiver.Dispose();
        Assert.NotEmpty(openedSources);
        await WaitUntilAsync(() => openedSources.All(stream => !stream.CanRead), 5000);
        // Immutable snapshots precede disposal. Observe() now measures the
        // persisted destination after Service/stream closure, not progress.
        return run;
    }

    private sealed record FileDigest(string Path, long Bytes, string Sha256Base64);
    private sealed record TransferRun(Guid Attempt, string Directory, string SessionId, string TransferId,
        string SourcePath, string DestinationPath, FileDigest ExpectedSource,
        FileTransferTransferSnapshot Outbound, FileTransferTransferSnapshot Inbound);
    private sealed record Capture(TransferRun Owner, Guid Attempt, FileTransferTransferSnapshot? Outbound,
        FileTransferTransferSnapshot? Inbound, FileDigest Source, FileDigest? Destination);

    private static Capture Observe(TransferRun run)
    {
        // No JSON input or caller-supplied path/hash: the fixture owns both paths.
        // Read leases exclude writers and replacement during the measurement.
        using var source = new FileStream(run.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = File.Exists(run.DestinationPath)
            ? new FileStream(run.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        return new Capture(run, run.Attempt, run.Outbound, run.Inbound, Digest(source, run.SourcePath),
            destination is null ? null : Digest(destination, run.DestinationPath));
    }

    private static FileDigest Digest(FileStream stream, string path)
    {
        stream.Position = 0;
        return new FileDigest(path, stream.Length, Convert.ToBase64String(SHA256.HashData(stream)));
    }

    // Private test-only observational predicate, not an acceptance or authority API.
    private static string Check(TransferRun expected, Capture capture)
    {
        if (!ReferenceEquals(expected, capture.Owner)) { return "capture_owner"; }
        if (capture.Attempt != expected.Attempt) { return "attempt_scope"; }
        var outbound = capture.Outbound;
        var inbound = capture.Inbound;
        if (outbound is null || inbound is null) { return "terminal_missing"; }
        if (outbound.SessionId != expected.SessionId || inbound.SessionId != expected.SessionId ||
            outbound.TransferId != expected.TransferId || inbound.TransferId != expected.TransferId ||
            outbound.Direction != FileTransferDirection.Outbound || inbound.Direction != FileTransferDirection.Inbound) { return "terminal_scope"; }
        if (outbound.State != FileTransferTransferState.Completed || inbound.State != FileTransferTransferState.Completed) { return "terminal_not_completed"; }
        if (outbound.ErrorCode is not null || inbound.ErrorCode is not null) { return "terminal_error"; }
        if (outbound.FileSizeBytes != expected.ExpectedSource.Bytes || inbound.FileSizeBytes != expected.ExpectedSource.Bytes ||
            outbound.BytesTransferred != expected.ExpectedSource.Bytes || inbound.BytesTransferred != expected.ExpectedSource.Bytes) { return "terminal_size"; }
        if (outbound.Sha256Base64 != expected.ExpectedSource.Sha256Base64 || inbound.Sha256Base64 != expected.ExpectedSource.Sha256Base64) { return "terminal_hash"; }
        if (capture.Source.Path != expected.SourcePath) { return "source_scope"; }
        if (capture.Source != expected.ExpectedSource) { return "source_changed"; }
        if (capture.Destination is null) { return "destination_missing"; }
        if (capture.Destination.Path != expected.DestinationPath || inbound.SavedFilePath != expected.DestinationPath) { return "destination_scope"; }
        if (capture.Destination.Bytes != expected.ExpectedSource.Bytes) { return "destination_size"; }
        if (capture.Destination.Sha256Base64 != expected.ExpectedSource.Sha256Base64) { return "destination_hash"; }
        return Verified;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void WriteDiagnostic(TransferRun run, Capture evidence, string name)
    {
        File.WriteAllText(Path.Combine(run.Directory, name), JsonSerializer.Serialize(new
        {
            Schema = "nlink.test-terminal-integrity/v1", Scope = "offline_service_terminal_integrity",
            Status = Check(run, evidence), run.Attempt, run.SessionId, run.TransferId, ExpectedSource = run.ExpectedSource,
            evidence.Outbound, evidence.Inbound, evidence.Source, evidence.Destination,
            WholeFileBytes = PayloadBytes, AuthorityEvaluations = 0, Live = false,
            CanonicalScenarioProven = false, AcceptanceChanged = false,
        }, JsonOptions));
    }
}
