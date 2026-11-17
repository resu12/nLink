using System.Collections;
using NLink.Tests;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("RequiredConfiguration", "Debug")]
public sealed class AttemptCanonicalSparseWriteObservationTests
{
    private const int First = AttemptCanonicalTailProfile.FirstChunk, ChunkBytes = AttemptCanonicalTailProfile.ChunkBytes;
    private static readonly DateTimeOffset Due = new(2026, 10, 21, 12, 0, 1, TimeSpan.Zero);
    private static (ContextShape Context, ObservationShape Observation) Shape()
    {
        var context = new ContextShape();
        foreach (var i in new[] { 3, 5, 6, 7 }) context.ReceiverSparseChunksWritten[First + i] = true;
        var observation = new ObservationShape(context);
        context.CurrentV6FrontierRecentProgressGraceWake = observation.RecentWake;
        return (context, observation);
    }
    private static AttemptCanonicalSparseWriteObservation Capture(ContextShape c, ObservationShape o) =>
        new(c, o, Due.AddMilliseconds(-500));
    private static void Commit(ContextShape c)
    {
        c.ReceiverSparseChunksPendingWrite.Clear(); c.BufferedBytes = 0;
        c.ReceiverSparseChunksWritten[First + 2] = true;
        c.ReceiverSparseBytesWritten += ChunkBytes;
    }

    [Fact]
    public void OnlyTheNewSelectedCommitIsObservedWithoutLogsOrRecheckCallbacks()
    {
        var (c, o) = Shape(); var read = Capture(c, o);
        Assert.False(read.Read(c, o, () => Due.AddTicks(-1)));
        c.ReceiverSparseChunksPendingWrite.Add(First + 2); c.BufferedBytes = ChunkBytes;
        Assert.False(read.Read(c, o, () => Due.AddTicks(-1)));
        Commit(c);
        Assert.True(read.Read(c, o, () => Due.AddTicks(-1)));
        Assert.Equal(Due, read.DueUtc);
        Assert.Same(o.RecentWake, c.CurrentV6FrontierRecentProgressGraceWake);
        Assert.Equal(0, o.Callbacks);
        Assert.Null(o.Root);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AnAlreadyWrittenOrPendingChunkCannotBeCapturedAsFresh(bool pending)
    {
        var (c, o) = Shape();
        if (pending) { c.ReceiverSparseChunksPendingWrite.Add(First + 2); c.BufferedBytes = ChunkBytes; }
        else Commit(c);
        Assert.Equal("canonical_sparse_write_not_fresh", Assert.Throws<IOException>(() => Capture(c, o)).Message);
        Assert.Equal(0, o.Callbacks);
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(1, false)]
    [InlineData(-1, true)] [InlineData(0, true)] [InlineData(1, true)]
    public void OriginalDeadlineRemainsStrictAtCaptureAndObservation(int offset, bool observing)
    {
        var (c, o) = Shape(); var now = Due.AddTicks(offset);
        var read = observing ? Capture(c, o) : null;
        if (observing) Commit(c);
        void Check()
        {
            if (read is null) _ = new AttemptCanonicalSparseWriteObservation(c, o, now);
            else Assert.True(read.Read(c, o, () => now));
        }
        if (offset < 0) Check();
        else Assert.Equal("canonical_sparse_write_outside_recent_grace", Assert.Throws<IOException>(Check).Message);
        Assert.Equal(Due, o.RecentWake.DueUtc); Assert.Equal(0, o.Callbacks);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void TimelyReadStartCannotAdmitCompletionAtOrAfterDue(int offset)
    {
        var (c, o) = Shape(); var read = Capture(c, o); Commit(c);
        var samples = new Queue<DateTimeOffset>([Due.AddTicks(-1), Due.AddTicks(offset)]);
        Assert.Equal("canonical_sparse_write_outside_recent_grace",
            Assert.Throws<IOException>(() => read.Read(c, o, () => samples.Dequeue())).Message);
        Assert.Empty(samples); Assert.Equal(Due, read.DueUtc); Assert.Equal(0, o.Callbacks);
    }

    [Theory]
    [InlineData("context")] [InlineData("observation")] [InlineData("recent")] [InlineData("wake")]
    [InlineData("source")] [InlineData("repair")] [InlineData("session")] [InlineData("stream")]
    [InlineData("bitmap")] [InlineData("pending-set")] [InlineData("session-id")] [InlineData("transfer-id")]
    [InlineData("generation")] [InlineData("owner")] [InlineData("publication")] [InlineData("leg")]
    [InlineData("epoch")] [InlineData("terminal-generation")] [InlineData("checkpoint")]
    [InlineData("terminal")] [InlineData("user-pause")] [InlineData("peer-pause")] [InlineData("transport-pause")]
    [InlineData("closed")] [InlineData("root")] [InlineData("due")] [InlineData("file-bytes")] [InlineData("chunk-bytes")] [InlineData("chunks")]
    public void ReplacementAuthorityScopeAndGeometryNeverBecomeAWriteWitness(string change)
    {
        var (c, o) = Shape(); var read = Capture(c, o); Commit(c);
        var current = c; var observation = o;
        switch (change)
        {
            case "context": current = Shape().Context; break;
            case "observation": observation = Shape().Observation; break;
            case "recent": o.ReplaceRecent(); break;
            case "wake": c.CurrentV6FrontierRecentProgressGraceWake = new Wake(); break;
            case "source": o.ReplaceSource(); break;
            case "repair": o.ReplaceRepair(); break;
            case "session": c.DataSession = new(); break;
            case "stream": c.WriteStream = new(); break;
            case "bitmap": c.ReceiverSparseChunksWritten = new(c.ReceiverSparseChunksWritten); break;
            case "pending-set": c.ReceiverSparseChunksPendingWrite = []; break;
            case "session-id": c.SessionId += "-replacement"; break;
            case "transfer-id": c.TransferId += "-replacement"; break;
            case "generation": c.DataSessionGeneration++; break;
            case "owner": c.CurrentRuntimeOwner = new(); break;
            case "publication": c.CurrentRoutePublicationToken = new(); break;
            case "leg": c.LegGeneration++; break;
            case "epoch": c.TransportEpoch++; break;
            case "terminal-generation": c.TerminalGeneration++; break;
            case "checkpoint": c.CheckpointGeneration++; break;
            case "terminal": c.IsTerminal = true; break;
            case "user-pause": c.UserPaused = true; break;
            case "peer-pause": c.PeerPaused = true; break;
            case "transport-pause": c.PullTransportPaused = true; break;
            case "closed": o.IsClosed = true; break;
            case "root": o.ReplaceRoot(); break;
            case "due": o.RecentWake.DueUtc = Due.AddSeconds(1); break;
            case "file-bytes": c.FileSizeBytes++; break;
            case "chunk-bytes": c.ChunkSizeBytes++; break;
            case "chunks": c.ChunkCount++; break;
        }
        Assert.Throws<IOException>(() => read.Read(current, observation, () => Due.AddTicks(-1)));
        Assert.Equal(Due, read.DueUtc); Assert.Equal(0, o.Callbacks);
    }

    [Theory]
    [InlineData("next")] [InlineData("highest")] [InlineData("chunks")] [InlineData("bytes")]
    [InlineData("sparse-bytes")] [InlineData("buffered")] [InlineData("pending")]
    [InlineData("other-bit")] [InlineData("bitmap-length")] [InlineData("not-committed")]
    public void OnlyTheExactCommittedBitmapAndAccountingCanCompleteTheObservation(string change)
    {
        var (c, o) = Shape(); var read = Capture(c, o); Commit(c);
        switch (change)
        {
            case "next": c.NextChunkIndex++; break;
            case "highest": c.PullHighestReceivedChunkIndex--; break;
            case "chunks": c.ChunksTransferred++; break;
            case "bytes": c.BytesTransferred++; break;
            case "sparse-bytes": c.ReceiverSparseBytesWritten++; break;
            case "buffered": c.BufferedBytes++; break;
            case "pending": c.ReceiverSparseChunksPendingWrite.Add(First + 4); break;
            case "other-bit": c.ReceiverSparseChunksWritten[First + 1] = true; break;
            case "bitmap-length": c.ReceiverSparseChunksWritten.Length--; break;
            case "not-committed": c.ReceiverSparseChunksWritten[First + 2] = false; break;
        }
        Assert.Throws<IOException>(() => read.Read(c, o, () => Due.AddTicks(-1)));
        Assert.Equal(0, o.Callbacks);
    }

    // Value-shape fakes only: no coordinator token, native receipt or runtime authority is authored.
    private sealed class Wake { public DateTimeOffset DueUtc { get; set; } = Due; }
    private sealed class ContextShape
    {
        public string SessionId { get; set; } = "session";
        public string TransferId { get; set; } = "transfer";
        public object DataSession { get; set; } = new();
        public object WriteStream { get; set; } = new();
        public object CurrentV6FrontierRecentProgressGraceWake { get; set; } = null!;
        public object CurrentRuntimeOwner { get; set; } = new();
        public object CurrentRoutePublicationToken { get; set; } = new();
        public long DataSessionGeneration { get; set; } = 17;
        public int LegGeneration { get; set; } = 5;
        public long TransportEpoch { get; set; } = 7;
        public long TerminalGeneration { get; set; }
        public int CheckpointGeneration { get; set; } = 3;
        public bool IsTerminal { get; set; }
        public bool UserPaused { get; set; }
        public bool PeerPaused { get; set; }
        public bool PullTransportPaused { get; set; }
        public long FileSizeBytes { get; set; } = AttemptCanonicalTailProfile.FileBytes;
        public int ChunkSizeBytes { get; set; } = ChunkBytes;
        public int ChunkCount { get; set; } = AttemptCanonicalTailProfile.ChunkCount;
        public int NextChunkIndex { get; set; } = First + 1;
        public int ChunksTransferred { get; set; } = First + 1;
        public int PullHighestReceivedChunkIndex { get; set; } = First + 7;
        public long BytesTransferred { get; set; } = (long)(First + 1) * ChunkBytes;
        public long ReceiverSparseBytesWritten { get; set; } = AttemptCanonicalTailProfile.FileBytes - 3L * ChunkBytes;
        public long BufferedBytes { get; set; }
        public BitArray ReceiverSparseChunksWritten { get; set; } = new(AttemptCanonicalTailProfile.ChunkCount);
        public HashSet<int> ReceiverSparseChunksPendingWrite { get; set; } = [];
    }
    private sealed class ObservationShape
    {
        private object source, capturedRepair = new();
        private object? root;
        private Wake recent = new();
        public bool IsClosed { get; set; }
        public int Callbacks { get; private set; }
        public object? Root => root;
        internal Wake RecentWake => recent;
        internal ObservationShape(ContextShape c) => source = Source(c);
        private static object Source(ContextShape c) => new
        {
            c.SessionId, c.TransferId, c.DataSessionGeneration, c.CurrentRuntimeOwner,
            c.CurrentRoutePublicationToken, c.LegGeneration, c.TransportEpoch, c.TerminalGeneration, c.CheckpointGeneration
        };
        internal void ReplaceRecent() => recent = new();
        internal void ReplaceSource() => source = new();
        internal void ReplaceRepair() => capturedRepair = new();
        internal void ReplaceRoot() => root = new();
        public void Recent() { Callbacks++; throw new InvalidOperationException("Fixture cannot invoke Recent"); }
        public void Rechecked() { Callbacks++; throw new InvalidOperationException("Fixture cannot invoke Rechecked"); }
    }
}
