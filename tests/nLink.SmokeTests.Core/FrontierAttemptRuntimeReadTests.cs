using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Tests;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierAttemptRuntimeReadTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh", "valid")]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "closed")]
    [InlineData("pwsh", "disposed")]
    public async Task OriginalHeldSource_RequiresCurrentRuntimeForSuccessorReads(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-runtime-reads-20260927", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var scenario = "writer-source-read-runtime-" + fault;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        var output = string.Join("\n", await Task.WhenAll(Directory.GetFiles(directory, "*-stdout.log", SearchOption.AllDirectories)
            .Select(path => File.ReadAllTextAsync(path))));
        Assert.Contains("retained-runtime-reads=" + fault, output);
        Assert.Contains("attempt-role-writers-ok", await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log")));
    }
}

public sealed partial class NknAccelerationTransportTests
{
    internal static async Task RunNativeRuntimeReadsAsync(SessionFileTransferService.NativeSenderSelection native, string scenario)
    {
        using var suite = new NknAccelerationTransportTests();
        var probe = new RetainedRuntimeReadProbe(null!, null!, scenario, native);
        await suite.RunJoinedBootstrapCorrelationAsync(runExchange: true, runtimeReads: probe);
        probe.VerifyRetired();
    }

    internal static async Task RunRetainedRuntimeReadsAsync(AttemptSenderSourceChannel source,
        LocalFrontierAppRequest.AppLease lease, string scenario)
    {
        using var suite = new NknAccelerationTransportTests();
        var probe = new RetainedRuntimeReadProbe(source, lease, scenario);
        await suite.RunJoinedBootstrapCorrelationAsync(runExchange: true, runtimeReads: probe);
        probe.VerifyRetired();
        Console.WriteLine("retained-runtime-reads=" + scenario.Split('-')[^1]);
    }

    private sealed class RetainedRuntimeReadProbe(AttemptSenderSourceChannel source, LocalFrontierAppRequest.AppLease lease, string scenario,
        SessionFileTransferService.NativeSenderSelection? native = null)
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private AttemptSenderSourceChannel.RuntimeReads factory = null!;
        private readonly List<Stream> reads = [];
        private byte[]? firstBytes;
        private int opens;
        internal FileTransferSendDescriptor? NativeDescriptor => native?.Descriptor;
        private LocalCanonicalSenderSource NativeSource => (LocalCanonicalSenderSource)native!.GetType()
            .GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
        private async Task<Stream?> OpenSourceAsync()
        {
            if (native is null) return factory.OpenRead();
            try { return await native.OpenReadStreamAsync(CancellationToken.None); }
            catch (IOException) { return null; }
        }
        internal bool ExpectRejection => !scenario.EndsWith("-valid", StringComparison.Ordinal);
        internal readonly TaskCompletionSource Rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Bind(SessionFileTransferService service)
        {
            if (native is not null)
            {
                // The fixture wraps reads only to schedule route transitions.
                // Bind the production selection to this actual context; no
                // coordinator token, route or install is synthesized.
                Assert.True(native.TryBindForSend(service, native.Descriptor));
                Assert.False(native.TryBindForSend(service, native.Descriptor));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var gate = service.GetType().GetField("gate", flags)!.GetValue(service)!;
                var context = service.GetType().GetField("outboundTransfer", flags)!.GetValue(service)!;
                lock (gate) Assert.True((bool)native.GetType().GetMethod("BindContextLocked", flags)!.Invoke(native, [service, context])!);
                return;
            }
            using var foreign = new SessionFileTransferService();
            Assert.Null(source.BindRuntimeReads(lease, foreign));
            Assert.Null(source.BindRuntimeReads((LocalFrontierAppRequest.AppLease)Clone.Invoke(lease, null)!, service));
            var copy = (AttemptSenderSourceChannel)Clone.Invoke(source, null)!;
            Assert.Null(copy.BindRuntimeReads(lease, service));
            copy.Dispose();
            factory = Assert.IsType<AttemptSenderSourceChannel.RuntimeReads>(source.BindRuntimeReads(lease, service));
            Assert.Null(source.BindRuntimeReads(lease, service));
            Assert.Null(factory.OpenRead()); // Not the metadata execution boundary yet.
            Assert.Null(source.TakeRead(lease));
            Assert.Null(source.OpenNextRead(lease));
        }

        // Existing carrier fixtures use only the synchronous test factory.
        internal Stream Open(Task release, AttemptCanonicalTailProfile? canonicalTail = null)
            => OpenAsync(release, canonicalTail).GetAwaiter().GetResult();

        internal async Task<Stream> OpenAsync(Task release, AttemptCanonicalTailProfile? canonicalTail = null)
        {
            opens++;
            Assert.InRange(opens, 1, 4);
            foreach (var retired in reads)
            {
                Assert.False(retired.CanRead);
                Assert.Throws<IOException>(() => retired.Seek(0, SeekOrigin.Begin));
            }
            if (opens == 4 && ExpectRejection)
            {
                if (native is not null) native.Dispose();
                else if (scenario.EndsWith("-closed", StringComparison.Ordinal))
                    ((Stream)source.GetType().GetField("file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!).Dispose();
                else source.Dispose();
                Assert.Null(await OpenSourceAsync());
                Assert.Null(await OpenSourceAsync());
                Assert.False(native is null ? source.IsCurrent : NativeSource.IsCurrent);
                Rejected.TrySetResult();
                throw new IOException("retained_runtime_source_rejected");
            }
            if (native is null) Assert.Null(((AttemptSenderSourceChannel.RuntimeReads)Clone.Invoke(factory, null)!).OpenRead());
            else await Assert.ThrowsAsync<IOException>(() => ((SessionFileTransferService.NativeSenderSelection)Clone.Invoke(native, null)!).OpenReadStreamAsync(CancellationToken.None));
            var read = Assert.IsAssignableFrom<Stream>(await OpenSourceAsync());
            Assert.False(read.CanWrite);
            Assert.Equal(0, read.Position);
            Assert.Equal(268435456, read.Length);
            Assert.Null(await OpenSourceAsync()); // Overlap/replay cannot consume the next reader.
            if (native is null) Assert.Null(source.OpenNextRead(lease));
            var copy = (Stream)Clone.Invoke(read, null)!;
            Assert.False(copy.CanRead);
            copy.Dispose();
            Assert.True(read.CanRead);
            reads.Add(read);
            return opens == 1 ? read : new RetainedRuntimeReadGate(read, release, (offset, bytes) =>
            {
                if (firstBytes is not null) return;
                Assert.Equal(0, offset);
                firstBytes = bytes;
                ReadObserved.TrySetResult();
            }, opens == 4 ? canonicalTail : null);
        }

        internal void VerifyBeforeRetirement(FileTransferTransferSnapshot sender, FileTransferTransferSnapshot receiver, MemoryStream destination)
        {
            Assert.Equal(4, opens);
            Assert.Equal(4, reads.Count);
            Assert.Equal(sender.SessionId, receiver.SessionId);
            Assert.Equal(sender.TransferId, receiver.TransferId);
            Assert.Equal("post_tuna_fallback_v6", sender.RouteToken);
            Assert.Equal("post_tuna_fallback_v6", receiver.RouteToken);
            Assert.Equal(Convert.ToBase64String(Convert.FromHexString(native is null ? source.Sha256 : NativeSource.Sha256)), sender.Sha256Base64);
            Assert.Equal(sender.Sha256Base64, receiver.Sha256Base64);
            Assert.NotNull(firstBytes);
            Assert.Equal(firstBytes, destination.ToArray().Take(firstBytes.Length).ToArray());
        }

        internal void VerifyRetired()
        {
            Assert.Equal(4, opens);
            Assert.Null(OpenSourceAsync().GetAwaiter().GetResult());
            foreach (var read in reads) Assert.False(read.CanRead);
            if (!ExpectRejection) Assert.True(native is null ? source.IsCurrent : NativeSource.IsCurrent); // Runtime retirement did not close the anchor.
        }
    }

    internal sealed class RetainedRuntimeReadGate(Stream read, Task release, Action<long, byte[]> observed,
        AttemptCanonicalTailProfile? canonicalTail = null) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await release.WaitAsync(ct);
            var offset = read.Position;
            if (buffer.Length > 0 && canonicalTail is not null)
            {
                canonicalTail.BeforeRead(offset, ct);
                if (offset < AttemptCanonicalTailProfile.PrefixBytes && buffer.Length > AttemptCanonicalTailProfile.PrefixBytes - offset)
                    throw new IOException("canonical_prefix_read_crossing");
                await canonicalTail.BeforeTailReadAsync(offset, ct, () =>
                {
                    if (!read.CanRead) throw new IOException("attempt_runtime_source_retired");
                    if (read.Position != offset) throw new IOException("canonical_prefix_source_position_changed");
                });
                ct.ThrowIfCancellationRequested();
                if (!read.CanRead) throw new IOException("attempt_runtime_source_retired");
                if (read.Position != offset) throw new IOException("canonical_prefix_source_position_changed");
            }
            var count = await read.ReadAsync(buffer, ct);
            if (count > 0) observed(offset, buffer.Span[..Math.Min(count, 64)].ToArray());
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => read.CanRead;
        public override bool CanSeek => read.CanSeek;
        public override bool CanWrite => false;
        public override long Length => read.Length;
        public override long Position { get => read.Position; set => read.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => read.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) read.Dispose(); base.Dispose(disposing); }
    }
}
