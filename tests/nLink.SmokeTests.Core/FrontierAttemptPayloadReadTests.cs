using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Tests;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptPayloadReadTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "closed", "disposed" }
        select new object[] { host, "writer-source-read-payload-" + scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalHelpee_ReadsHeldSourceThroughV4PayloadPump(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-payload-read-20260926", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-source-custody-ok", gui);
        Assert.Contains("attempt-role-writers-ok", gui);
        var app = string.Join("\n", await Task.WhenAll(Directory.GetFiles(directory, "*-stdout.log", SearchOption.AllDirectories)
            .Select(path => File.ReadAllTextAsync(path))));
        if (scenario.EndsWith("-valid", StringComparison.Ordinal))
        {
            var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
            Assert.Contains("sender-payload-read=one-v4-chunk; source-sha256=" + measurement[1], app);
        }
        else Assert.Contains("sender-payload-read-rejected", app);
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    private static async Task ReadAttemptPayloadAsync(AttemptSenderSourceChannel source,
        LocalFrontierAppRequest.AppLease lease, string scenario)
    {
        var sessionId = "attempt-payload-" + lease.RequestId.ToString("N");
        var transferId = "payload-" + Guid.NewGuid().ToString("N");
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = SessionFileTransferService.CreateForApp();
        service.AttachTransport(transport);
        var prepared = new TaskCompletionSource<FileTransferTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<FileTransferTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TransferChanged += (_, args) =>
        {
            if (args.Snapshot.Outbound is not { } snapshot) return;
            if (snapshot.State == FileTransferTransferState.AwaitingStart) prepared.TrySetResult(snapshot);
            if (snapshot.State == FileTransferTransferState.Failed) failed.TrySetResult(snapshot);
        };
        AttemptSourceReadStream? metadata = null, payload = null;
        var opens = 0;
        var rejected = !scenario.EndsWith("-valid", StringComparison.Ordinal);
        try
        {
            await service.TryStartSendAsync(new FileTransferSendDescriptor("filetransfer-tuna-gui-payload.bin", 268435456, transferId),
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    var ordinal = Interlocked.Increment(ref opens);
                    Assert.InRange(ordinal, 1, 2);
                    Assert.True(source.IsCurrent);
                    if (ordinal == 2)
                    {
                        Assert.NotNull(metadata);
                        Assert.True(metadata.Closed.Task.IsCompletedSuccessfully);
                        if (scenario.EndsWith("-closed", StringComparison.Ordinal))
                            ((Stream)source.GetType().GetField("file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!).Dispose();
                        if (scenario.EndsWith("-disposed", StringComparison.Ordinal)) source.Dispose();
                    }
                    var file = source.OpenNextRead(lease);
                    if (ordinal == 2 && rejected)
                    {
                        Assert.Null(file);
                        Assert.False(source.IsCurrent);
                        Assert.Null(source.OpenNextRead(lease));
                        sourceRejected.TrySetResult();
                        throw new IOException("attempt_payload_source_unavailable");
                    }
                    Assert.NotNull(file);
                    Assert.False(file.CanWrite);
                    Assert.Equal(0, file.Position);
                    Assert.Null(source.OpenNextRead(lease)); // no overlapping reads
                    Assert.Null(source.TakeRead(lease)); // no mixed one-shot path
                    Assert.Null(source.BindRuntimeReads(lease, service)); // no mixed successor path
                    var read = new AttemptSourceReadStream(file, () => source.IsCurrent, seekable: ordinal == 2);
                    if (ordinal == 1) metadata = read; else payload = read;
                    return Task.FromResult<Stream>(read);
                }, CancellationToken.None);
            Assert.Equal(0, opens);
            var offer = Assert.Single(transport.SentOffers);
            await peer.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = offer.SessionId, TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, CancellationToken.None);
            var snapshot = await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(metadata);
            await metadata.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(268435456L, metadata.BytesRead);
            Assert.True(metadata.ReachedEnd);
            Assert.Equal(Convert.ToBase64String(Convert.FromHexString(source.Sha256)), snapshot.Sha256Base64);
            Assert.Equal(sessionId, snapshot.SessionId);
            Assert.Equal(transferId, snapshot.TransferId);
            if (rejected)
            {
                // Factory exceptions are caught by Core. Require these exact
                // rejection assertions to finish, not just any Failed state.
                await sourceRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(transferId, failure.TransferId);
                Assert.Equal(2, opens);
                Assert.Null(payload);
                Assert.DoesNotContain(transport.SentDataFrames, frame => frame is FileTransferChunkBatchFrame);
                Console.WriteLine("sender-payload-read-rejected");
                return;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var dataSession = await peer.OpenFileTransferDataSessionAsync(sessionId, transferId, deadline.Token);
            var manifest = Assert.IsType<FileTransferManifestFrameV4>(await dataSession.ReceiveAsync(deadline.Token));
            Assert.Equal(snapshot.Sha256Base64, manifest.Sha256Base64);
            Assert.Equal(268435456L, manifest.FileSizeBytes);
            await dataSession.SendAsync(new FileTransferStateFrameV4
            {
                SessionId = sessionId, TransferId = transferId, Epoch = 1,
                ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1, MissingRanges = [], BytesCommitted = 0,
            }, deadline.Token);
            // Actual loopback delivery, not merely the outbound queue marker.
            var batch = Assert.IsType<FileTransferChunkBatchFrameV4>(await dataSession.ReceiveAsync(deadline.Token));
            Assert.Equal(sessionId, batch.SessionId);
            Assert.Equal(transferId, batch.TransferId);
            Assert.Equal(0, batch.StartChunkIndex);
            Assert.Equal(1, batch.ChunkCount);
            var bytes = Assert.Single(batch.DataSegments);
            Assert.Equal(manifest.ChunkSizeBytes, bytes.Length);
            Assert.NotNull(payload);
            Assert.Equal(0, payload.FirstReadOffset);
            Assert.NotNull(payload.FirstRead);
            Assert.Equal(bytes, payload.FirstRead);
            Assert.True(payload.BytesRead >= bytes.Length);
            Assert.Equal(2, opens);
            service.ResetSessionState();
            await payload.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(service.Snapshot.Outbound);
            Assert.True(source.IsCurrent); // metadata/pump disposal did not close the anchor
            Assert.Null(source.OpenNextRead(lease)); // exactly two reads, no third/revival
            Assert.True(source.IsCurrent);
            source.Dispose();
            Assert.False(source.IsCurrent);
            Assert.Null(source.OpenNextRead(lease));
            Assert.Empty(transport.SentCompletes);
            Assert.DoesNotContain(transport.SentDataFrames, frame => frame is FileTransferCompleteFrameV4 or FileTransferChunkBatchFrameV6);
            Console.WriteLine("sender-payload-read=one-v4-chunk; source-sha256=" + source.Sha256);
        }
        finally
        {
            service.ResetSessionState();
            if (payload is not null) await payload.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
