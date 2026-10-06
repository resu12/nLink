using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Tests;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptSenderReadTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "request", "creation", "foreign-file", "closed" }
        select new object[] { host, "writer-source-read-" + scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalHelpee_ReadsHeldSourceThroughServiceMetadata(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-sender-read-20260926", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
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
            Assert.Contains("sender-source-read=268435456; sha256=" + measurement[1], app);
        }
        else Assert.Contains("sender-source-read-rejected", app);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExposeSenderSourceChannel(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            if($null -ne ('NLink.Tests.AttemptSenderSourceChannel' -as [type])) {throw 'Sender channel API leaked'}
            'sender-source-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("sender-source-default-ok", result.Stdout);
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task ReadAttemptSourceAsync(SessionFileTransferService.FrontierAppLifetime lifetime, string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lease = (LocalFrontierAppRequest.AppLease)lifetime.GetType().GetField("lease", flags)!.GetValue(lifetime)!;
        var clone = typeof(object).GetMethod("MemberwiseClone", flags)!;
        var leaseCopy = (LocalFrontierAppRequest.AppLease)clone.Invoke(lease, null)!;
        using var input = Console.OpenStandardInput();
        Assert.Null(AttemptSenderSourceChannel.Receive(leaseCopy, Stream.Null));
        using var source = await Task.Run(() => AttemptSenderSourceChannel.Receive(lease, input)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(AttemptSenderSourceChannel.Receive(lease, Stream.Null));
        if (scenario is "writer-source-read-request" or "writer-source-read-creation" or "writer-source-read-foreign-file")
        {
            Assert.Null(source);
            Assert.True(lease.IsCurrent);
            Console.WriteLine("sender-source-read-rejected");
            return;
        }
        Assert.NotNull(source);
        var copy = (AttemptSenderSourceChannel)clone.Invoke(source, null)!;
        Assert.False(copy.IsCurrent);
        Assert.Null(copy.TakeRead(lease));
        copy.Dispose();
        Assert.True(source.IsCurrent);
        Assert.Null(source.TakeRead(leaseCopy));
        if (scenario.StartsWith("writer-source-read-runtime-", StringComparison.Ordinal))
        {
            await NknAccelerationTransportTests.RunRetainedRuntimeReadsAsync(source, lease, scenario);
            return;
        }
        if (scenario.StartsWith("writer-source-read-payload-", StringComparison.Ordinal))
        {
            Assert.Null(copy.OpenNextRead(lease));
            Assert.Null(source.OpenNextRead(leaseCopy));
            await ReadAttemptPayloadAsync(source, lease, scenario);
            return;
        }
        if (scenario == "writer-source-read-closed")
        {
            ((Stream)source.GetType().GetField("file", flags)!.GetValue(source)!).Dispose();
            Assert.False(source.IsCurrent);
            Assert.Null(source.TakeRead(lease));
            Console.WriteLine("sender-source-read-rejected");
            return;
        }

        var sessionId = "attempt-source-" + lease.RequestId.ToString("N");
        var transferId = "source-" + Guid.NewGuid().ToString("N");
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = SessionFileTransferService.CreateForApp();
        service.AttachTransport(transport);
        var prepared = new TaskCompletionSource<FileTransferTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.TransferChanged += (_, args) =>
        {
            if (args.Snapshot.Outbound is not { State: FileTransferTransferState.AwaitingStart } snapshot) return;
            try
            {
                // Stop at the real metadata boundary, before payload dispatch.
                service.ResetSessionState();
                prepared.TrySetResult(snapshot);
            }
            catch (Exception ex) { prepared.TrySetException(ex); }
        };
        AttemptSourceReadStream? read = null;
        var opens = 0;
        await service.TryStartSendAsync(new FileTransferSendDescriptor("filetransfer-tuna-gui-payload.bin", 268435456, transferId),
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                Assert.Equal(1, Interlocked.Increment(ref opens));
                read = new AttemptSourceReadStream(Assert.IsType<FileStream>(source.TakeRead(lease)), () => lease.IsCurrent);
                Assert.Null(source.TakeRead(lease));
                Assert.Null(source.BindRuntimeReads(lease, service));
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
        Assert.NotNull(read);
        await read.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, opens);
        Assert.Equal(268435456L, read.BytesRead);
        Assert.True(read.ReachedEnd);
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(source.Sha256)), snapshot.Sha256Base64);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(transferId, snapshot.TransferId);
        Assert.Null(service.Snapshot.Outbound);
        Assert.False(source.IsCurrent);
        Assert.DoesNotContain(transport.SentDataFrames, frame => frame is FileTransferChunkBatchFrameV4 or FileTransferChunkBatchFrameV6);
        Console.WriteLine("sender-source-read=268435456; sha256=" + source.Sha256);
    }

    private sealed class AttemptSourceReadStream(FileStream file, Func<bool> current, bool seekable = false) : Stream
    {
        internal long BytesRead;
        internal bool ReachedEnd;
        internal byte[]? FirstRead;
        internal long FirstReadOffset;
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!current()) throw new IOException("attempt_source_lease_retired");
            var offset = file.Position;
            var count = await file.ReadAsync(buffer, cancellationToken);
            if (seekable && FirstRead is null && count > 0)
            {
                FirstReadOffset = offset;
                FirstRead = buffer.Span[..count].ToArray();
            }
            BytesRead += count; ReachedEnd |= count == 0;
            return count;
        }
        public override bool CanRead => file.CanRead;
        public override bool CanSeek => seekable && file.CanSeek;
        public override bool CanWrite => false;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!seekable) throw new NotSupportedException();
            if (!current()) throw new IOException("attempt_source_lease_retired");
            return file.Seek(offset, origin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { file.Dispose(); Closed.TrySetResult(); }
            base.Dispose(disposing);
        }
    }
}
