using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

public sealed class DevLocalFileTransferDataSessionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string SessionId = "metadata-session";
    private const string TransferId = "metadata-transfer";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstFrameAndOpen_InterleavedLookupRetainsOneQueue(bool frameDuringOpen)
    {
        using var transport = new DevLocalTransport();
        var comparer = new InterleavingComparer();
        var field = typeof(DevLocalTransport).GetField("fileTransferDataSessions", PrivateInstance)!;
        field.SetValue(transport, Activator.CreateInstance(field.FieldType, comparer));
        using var seed = await transport.OpenFileTransferDataSessionAsync(SessionId, "seed-transfer", CancellationToken.None);
        var manifest = Manifest();
        IFileTransferDataSession? interleaved = null;
        IFileTransferDataSession? opened = null;
        try
        {
            // Force the other path after the first lookup has selected its old
            // collision chain, but before it publishes a newly created queue.
            // This is a queue-identity test, not authenticated ingress evidence.
            comparer.OnLookup = () =>
            {
                if (frameDuringOpen) Deliver(transport, manifest);
                interleaved = transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None)
                    .GetAwaiter().GetResult();
            };
            if (frameDuringOpen)
                opened = await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None);
            else
                Deliver(transport, manifest);

            using var current = await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None);
            Assert.Equal(1, comparer.Interleavings);
            Assert.NotNull(interleaved);
            Assert.Same(interleaved, current);
            if (frameDuringOpen) Assert.Same(opened, current);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.Same(manifest, await current.ReceiveAsync(deadline.Token));
        }
        finally
        {
            opened?.Dispose();
            interleaved?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstFrameBeforeOrAfterOpen_UsesTheSameQueue(bool frameFirst)
    {
        using var transport = new DevLocalTransport();
        var manifest = Manifest();
        if (frameFirst) Deliver(transport, manifest);
        using var opened = await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None);
        if (!frameFirst) Deliver(transport, manifest);
        Assert.Same(opened, await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Same(manifest, await opened.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task ConcurrentOpenAndDelivery_PreservesEveryFrameInOneQueue()
    {
        using var transport = new DevLocalTransport();
        using var start = new ManualResetEventSlim();
        var opened = new ConcurrentBag<IFileTransferDataSession>();
        var manifests = Enumerable.Range(0, 16).Select(i => Manifest() with { FileName = "frame-" + i }).ToArray();
        try
        {
            var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            {
                start.Wait();
                if (i % 2 == 0)
                    opened.Add(await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None));
                else
                    Deliver(transport, manifests[i / 2]);
            })).ToArray();
            start.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            using var current = await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None);
            Assert.Equal(16, opened.Count);
            Assert.All(opened, value => Assert.Same(current, value));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var received = new List<FileTransferDataFrame>();
            for (var i = 0; i < manifests.Length; i++) received.Add(await current.ReceiveAsync(deadline.Token));
            Assert.All(manifests, manifest => Assert.Single(received, frame => ReferenceEquals(frame, manifest)));
        }
        finally { foreach (var session in opened) session.Dispose(); }
    }

    [Fact]
    public async Task MismatchedSession_DoesNotReplaceQueueOrDeliverFrame()
    {
        using var transport = new DevLocalTransport();
        using var opened = await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = transport.OpenFileTransferDataSessionAsync("other-session", TransferId, CancellationToken.None);
        });
        Deliver(transport, Manifest() with { SessionId = "other-session" });
        var valid = Manifest();
        Deliver(transport, valid);
        Assert.Same(opened, await transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, CancellationToken.None));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Same(valid, await opened.ReceiveAsync(deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledOrDisposedOpen_DoesNotCreateQueue(bool disposed)
    {
        using var transport = new DevLocalTransport();
        using var cancellation = new CancellationTokenSource();
        if (disposed) transport.Dispose(); else cancellation.Cancel();
        Action open = () => { _ = transport.OpenFileTransferDataSessionAsync(SessionId, TransferId, cancellation.Token); };
        if (disposed) Assert.Throws<ObjectDisposedException>(open);
        else Assert.Throws<OperationCanceledException>(open);
        var sessions = typeof(DevLocalTransport).GetField("fileTransferDataSessions", PrivateInstance)!.GetValue(transport)!;
        Assert.Equal(0, sessions.GetType().GetProperty("Count")!.GetValue(sessions));
    }

    private static FileTransferManifestFrameV4 Manifest() => new()
    {
        SessionId = SessionId,
        TransferId = TransferId,
        FileName = "metadata.bin",
        FileSizeBytes = 1,
        ChunkSizeBytes = 1,
        ChunkCount = 1,
        Sha256Base64 = Convert.ToBase64String(new byte[32]),
    };

    private static void Deliver(DevLocalTransport transport, FileTransferDataFrame frame) =>
        typeof(DevLocalTransport).GetMethod("DeliverFileTransferDataFrame", PrivateInstance)!
            .Invoke(transport, [frame]);

    private sealed class InterleavingComparer : IEqualityComparer<string>
    {
        public Action? OnLookup;
        public int Interleavings { get; private set; }
        public int GetHashCode(string value) => 1;
        public bool Equals(string? left, string? right)
        {
            if ((left == "seed-transfer" && right == TransferId) ||
                (right == "seed-transfer" && left == TransferId))
            {
                var action = Interlocked.Exchange(ref OnLookup, null);
                if (action is not null) { Interleavings++; action(); }
            }
            return StringComparer.Ordinal.Equals(left, right);
        }
    }
}
