using System.Buffers.Binary;
using System.Collections;
using System.IO.Pipes;
using System.Security.Cryptography;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class AttemptCanonicalPrefixBarrierTests : IDisposable
{
    private const long Boundary = AttemptCanonicalTailProfile.PrefixBytes;
    private readonly string? previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
    public AttemptCanonicalPrefixBarrierTests() => Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "3");
    public void Dispose() => Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, previous);
    private static AttemptCanonicalTailProfile Profile() => new(268435456, 21504, 12484);
    private static AttemptCanonicalPrefixWitness Witness() => new(Guid.NewGuid(), Guid.NewGuid(), "session", "transfer");

    [Fact]
    public async Task ExactTailReadWaitsOnceWithoutConsumingEmptyReadsOrSeekAndPreservesPartialReads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        profile.BindPrefix(handoff.Task, () => checks++);
        using var source = new PositionedStream(Boundary, maxRead: 3);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, Task.CompletedTask, (_, _) => { }, profile);
        read.Seek(0, SeekOrigin.Current);
        Assert.Equal(0, await read.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(0, source.Reads);
        Assert.Throws<IOException>(profile.RequirePrefixConsumed);
        var pending = read.ReadAsync(new byte[8]).AsTask();
        Assert.False(pending.IsCompleted); Assert.Equal(0, source.Reads);
        handoff.SetResult();
        Assert.Equal(3, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, await read.ReadAsync(new byte[8]));
        Assert.Equal(Boundary + 6, read.Position); Assert.Equal(1, checks);
        profile.RequirePrefixConsumed();
        Assert.Throws<IOException>(() => profile.BindPrefix(Task.CompletedTask, () => { }));
    }

    [Theory]
    [InlineData("missing")] [InlineData("past")] [InlineData("overlap")]
    [InlineData("cancel")] [InlineData("dispose")] [InlineData("source_retired")]
    [InlineData("scope_retired")] [InlineData("seek_during_wait")] [InlineData("crossing")]
    public async Task InvalidOrRetiredFirstReadsNeverReachTheUnderlyingSource(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        using var cancel = new CancellationTokenSource();
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fault != "missing") profile.BindPrefix(handoff.Task, () =>
        { if (fault == "scope_retired") throw new IOException("retired"); });
        using var source = new PositionedStream(Boundary + (fault == "past" ? 1 : fault == "crossing" ? -1 : 0));
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, Task.CompletedTask, (_, _) => Assert.Fail("Data escaped"), profile);
        var pending = read.ReadAsync(new byte[8], cancel.Token).AsTask();
        if (fault == "overlap") await Assert.ThrowsAsync<IOException>(() => read.ReadAsync(new byte[8]).AsTask());
        if (fault == "cancel") cancel.Cancel();
        if (fault == "dispose") profile.Dispose();
        if (fault == "source_retired") source.Retired = true;
        if (fault == "seek_during_wait") read.Position++;
        handoff.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, source.Reads);
        Assert.ThrowsAny<Exception>(profile.RequirePrefixConsumed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArmedCachedTailCannotBypassReadEntryOrPendingHandoff(bool entered)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = Profile();
        profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, CancellationToken.None);
        profile.BindPrefix(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task, () => { });
        var pending = entered ? profile.BeforeTailReadAsync(Boundary, CancellationToken.None, () => { }).AsTask() : null;
        var bytes = new byte[16];
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer", profile);
        descriptors.Observe(new OfflineFileFrameMetadata("session", "transfer", false, 12476, 1, 0,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        var error = Assert.Throws<IOException>(() => descriptors.Consume(0, NknBridgeChannel.Bulk, bytes));
        Assert.Equal("canonical_prefix_tail_bypass", error.InnerException!.Message);
        profile.Dispose();
        if (pending is not null) Assert.ThrowsAny<Exception>(() => pending.GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SubmittedPrefixWritesAndFeedbackContinueWhileFirstTailReadWaits(int alignment)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var profile = Profile();
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.BindPrefix(handoff.Task, () => { });
        var start = (long)(AttemptCanonicalTailProfile.PrearmChunk - alignment) * 21504;
        using var source = new PositionedStream(start);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, Task.CompletedTask, (_, _) => { }, profile);
        var buffer = new byte[21504];
        // The already-captured three-segment batch finishes before the tail.
        for (var i = 0; i < 3; i++) Assert.Equal(21504, await read.ReadAsync(buffer, deadline.Token));
        while (read.Position < Boundary) Assert.Equal(21504, await read.ReadAsync(buffer, deadline.Token));
        var tail = read.ReadAsync(buffer, deadline.Token).AsTask();
        Assert.False(tail.IsCompleted);
        var pipeName = "nlink-prefix-progress-" + Guid.NewGuid().ToString("N");
        using var receiver = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var sender = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(receiver.WaitForConnectionAsync(deadline.Token), sender.ConnectAsync(deadline.Token));
        await using var destination = new FileStream(Path.Combine(Path.GetTempPath(), pipeName + ".bin"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var serviceGate = new object();
        var context = new PrefixShape { NextChunkIndex = 12475, ChunksTransferred = 12475,
            BytesTransferred = Boundary - 21504, ReceiverSparseBytesWritten = Boundary - 21504 };
        context.ReceiverSparseChunksPendingWrite.Add(12475);
        var feedback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = AttemptOfflineLatePublicationProgress.WaitForCommittedPrefixAsync(() =>
        {
            lock (serviceGate) return AttemptCanonicalPrefixScope.ReadReady(context, "session", "transfer");
        }, () => { }, () =>
        {
            Assert.False(Monitor.IsEntered(serviceGate)); Assert.True(feedback.Task.IsCompleted);
            handoff.SetResult(); return Task.CompletedTask;
        }, deadline.Token);
        await Task.Run(async () =>
        {
            // Real independent pipe delivery, async file write and reverse
            // feedback finish with the actual source wrapper still waiting.
            var send = sender.WriteAsync(buffer, deadline.Token).AsTask();
            var received = new byte[buffer.Length];
            await receiver.ReadExactlyAsync(received, deadline.Token);
            await send;
            await destination.WriteAsync(received, deadline.Token);
            await destination.FlushAsync(deadline.Token);
            var reverse = receiver.WriteAsync(new byte[] { 1 }, deadline.Token).AsTask();
            var ack = new byte[1]; await sender.ReadExactlyAsync(ack, deadline.Token);
            await reverse;
            Assert.Equal(1, ack[0]); Assert.Equal(21504, destination.Length);
            lock (serviceGate)
            {
                Assert.False(tail.IsCompleted);
                context.ReceiverSparseChunksPendingWrite.Clear();
                context.NextChunkIndex = context.ChunksTransferred = 12476;
                context.BytesTransferred = context.ReceiverSparseBytesWritten = Boundary;
                feedback.SetResult();
            }
        }, deadline.Token);
        await prefix; Assert.Equal(21504, await tail);
    }

    [Fact]
    public void ExactCommittedPrefixWithClearedBitmapIsReadyAndPendingPrefixIsNot()
    {
        var context = new PrefixShape();
        Assert.All(context.ReceiverSparseChunksWritten.Cast<bool>(), value => Assert.False(value));
        Assert.True(AttemptCanonicalPrefixScope.ReadReady(context, "session", "transfer"));
        context.NextChunkIndex--; context.ChunksTransferred--;
        context.BytesTransferred -= 21504; context.ReceiverSparseBytesWritten -= 21504;
        context.ReceiverSparseChunksPendingWrite.Add(12475);
        Assert.False(AttemptCanonicalPrefixScope.ReadReady(context, "session", "transfer"));
    }

    [Theory]
    [InlineData("SessionId")] [InlineData("TransferId")] [InlineData("FileSizeBytes")]
    [InlineData("ChunkSizeBytes")] [InlineData("ChunkCount")] [InlineData("PullManifestReceived")]
    [InlineData("IsTerminal")] [InlineData("UserPaused")] [InlineData("PeerPaused")]
    [InlineData("NextChunkIndex")] [InlineData("ChunksTransferred")] [InlineData("PullHighestReceivedChunkIndex")]
    [InlineData("BytesTransferred")] [InlineData("ReceiverSparseBytesWritten")] [InlineData("BufferedBytes")]
    [InlineData("pending")] [InlineData("bitmap")] [InlineData("prior_repair")]
    public void ForeignInconsistentPausedTerminalAndEarlyTailShapesFailClosed(string fault)
    {
        var context = new PrefixShape();
        if (fault == "pending") context.ReceiverSparseChunksPendingWrite.Add(12476);
        else if (fault == "bitmap") context.ReceiverSparseChunksWritten = new BitArray(12483);
        else if (fault == "prior_repair") context.FrontierBoundaryObservation = new PriorObservation();
        else
        {
            var property = typeof(PrefixShape).GetProperty(fault)!;
            var value = property.GetValue(context);
            property.SetValue(context, value switch { bool b => !b, int i => i + 1, long n => n + 1, _ => "foreign" });
        }
        Assert.Throws<IOException>(() => AttemptCanonicalPrefixScope.ReadReady(context, "session", "transfer"));
    }

    [Theory]
    [InlineData("valid")] [InlineData("empty")] [InlineData("header_short")] [InlineData("body_short")]
    [InlineData("zero")] [InlineData("negative")] [InlineData("oversize")]
    [InlineData("schema")] [InlineData("sender")] [InlineData("receiver")] [InlineData("session")]
    [InlineData("transfer")] [InlineData("geometry")] [InlineData("trailing")] [InlineData("duplicate")]
    public async Task BoundedRecordRequiresExactIdentityGeometryAndEof(string fault)
    {
        var expected = Witness();
        var actual = fault switch
        {
            "sender" => expected with { SenderRequest = Guid.NewGuid() },
            "receiver" => expected with { ReceiverRequest = Guid.NewGuid() },
            "session" => expected with { Session = "foreign" },
            "transfer" => expected with { Transfer = "foreign" }, _ => expected
        };
        var body = actual.Encode();
        if (fault == "schema") body[1] ^= 1;
        if (fault == "geometry") body[^1] ^= 1;
        var packet = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, fault switch { "zero" => 0, "negative" => -1, "oversize" => 513, _ => body.Length });
        body.CopyTo(packet, 4);
        packet = fault switch
        {
            "empty" => [], "header_short" => packet[..3], "body_short" => packet[..^1],
            "trailing" => [..packet, 1], "duplicate" => [..packet, ..packet], _ => packet
        };
        using var stream = new MemoryStream(packet, false);
        var task = AttemptCanonicalPrefixHandoff.ReadRecordAsync(stream, expected, CancellationToken.None);
        if (fault == "valid") { await task; Assert.Equal(packet.Length, stream.Position); }
        else await Assert.ThrowsAnyAsync<IOException>(() => task);
        if (fault is "trailing" or "duplicate") Assert.Equal(5 + body.Length, stream.Position);
        if (fault is "zero" or "negative" or "oversize") Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData("valid")] [InlineData("cancel")] [InlineData("dispose")]
    [InlineData("retired")] [InlineData("missing_peer")]
    public async Task DedicatedAsyncPipeIsOneShotAndCleanupNeverReleasesAWaiter(string fault)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = "nlink-prefix-unit-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));
        var current = fault != "missing_peer";
        void Check() { if (!current) throw new IOException("retained_peer_unavailable"); }
        var witness = Witness();
        await using var consumer = new AttemptCanonicalPrefixHandoff(server, witness, true, Check, deadline.Token);
        await using var producer = new AttemptCanonicalPrefixHandoff(client, witness, false, () => { }, deadline.Token);
        if (fault == "cancel") deadline.Cancel();
        else if (fault == "dispose") await consumer.DisposeAsync();
        else
        {
            if (fault == "retired") current = false;
            if (fault != "missing_peer") await producer.PublishAsync();
        }
        if (fault == "valid")
        {
            await consumer.Received;
            await Assert.ThrowsAsync<IOException>(producer.PublishAsync);
            consumer.Check();
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => consumer.Received.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompleteBodyCannotReleaseBeforeEofAndRechecksRetainedPeerAfterEof(bool retire)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = "nlink-prefix-eof-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));
        var current = true;
        var witness = Witness();
        await using var consumer = new AttemptCanonicalPrefixHandoff(server, witness, true,
            () => { if (!current) throw new IOException("retired_after_body"); }, deadline.Token);
        var body = witness.Encode();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await client.WriteAsync(header, deadline.Token); await client.WriteAsync(body, deadline.Token);
        await client.FlushAsync(deadline.Token);
        Assert.False(consumer.Received.IsCompleted);
        if (retire) current = false;
        client.Dispose();
        if (retire) await Assert.ThrowsAsync<IOException>(() => consumer.Received);
        else await consumer.Received;
    }

    private sealed class PositionedStream(long position, int maxRead = int.MaxValue) : Stream
    {
        internal bool Retired;
        internal int Reads;
        public override long Position { get; set; } = position;
        public override long Length => AttemptCanonicalTailProfile.FileBytes;
        public override bool CanRead => !Retired;
        public override bool CanSeek => !Retired;
        public override bool CanWrite => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Retired) throw new IOException("retired");
            var count = Math.Min(buffer.Length, maxRead);
            if (count != 0) Reads++;
            buffer.Span[..count].Fill(17); Position += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = offset + (origin == SeekOrigin.Current ? Position : origin == SeekOrigin.End ? Length : 0);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixShape
    {
        public string SessionId { get; set; } = "session";
        public string TransferId { get; set; } = "transfer";
        public long FileSizeBytes { get; set; } = 268435456;
        public int ChunkSizeBytes { get; set; } = 21504;
        public int ChunkCount { get; set; } = 12484;
        public bool PullManifestReceived { get; set; } = true;
        public bool IsTerminal { get; set; }
        public bool UserPaused { get; set; }
        public bool PeerPaused { get; set; }
        public int NextChunkIndex { get; set; } = 12476;
        public int ChunksTransferred { get; set; } = 12476;
        public int PullHighestReceivedChunkIndex { get; set; } = 12475;
        public long BytesTransferred { get; set; } = Boundary;
        public long ReceiverSparseBytesWritten { get; set; } = Boundary;
        public HashSet<int> ReceiverSparseChunksPendingWrite { get; } = [];
        public long BufferedBytes { get; set; }
        public BitArray ReceiverSparseChunksWritten { get; set; } = new(12484);
        public object? FrontierBoundaryObservation { get; set; }
    }

    private sealed class PriorObservation
    {
        public bool IsClosed => false;
#pragma warning disable CS0414
        private readonly object? source = null;
        private readonly object capturedRepair = new();
#pragma warning restore CS0414
    }
}
