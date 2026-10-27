using System.Collections;
using System.Reflection;

namespace NLink.Tests;

// Passive fixture reads only, always called inside the original service gate.
internal sealed class AttemptCanonicalSparseWriteObservation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object Source(object observation, object context) =>
        observation.GetType().GetMethod("Source", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context])!;
    private readonly object context, observation, recent, source, repair, dataSession, stream, pending;
    private readonly BitArray written;
    internal DateTimeOffset DueUtc { get; }

    internal AttemptCanonicalSparseWriteObservation(object context, object observation, DateTimeOffset now)
    {
        this.context = context; this.observation = observation;
        recent = Field(observation, "recent") ?? throw new IOException("canonical_sparse_write_recent_missing");
        source = Field(observation, "source") ?? throw new IOException("canonical_sparse_write_source_missing");
        repair = Field(observation, "capturedRepair") ?? throw new IOException("canonical_sparse_write_repair_missing");
        dataSession = Property<object>(context, "DataSession");
        stream = Property<object>(context, "WriteStream");
        written = Property<BitArray>(context, "ReceiverSparseChunksWritten");
        pending = Property<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        DueUtc = Property<DateTimeOffset>(recent, "DueUtc");
        CheckCurrent(context, observation, now);
        CheckShape();
        if (written[AttemptCanonicalTailProfile.FirstChunk + 2] ||
            Property<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite").Count != 0)
            throw new IOException("canonical_sparse_write_not_fresh");
    }

    internal bool Read(object currentContext, object currentObservation, Func<DateTimeOffset> utcNow)
    {
        CheckCurrent(currentContext, currentObservation, utcNow());
        var ready = CheckShape();
        // A timely start cannot admit a read which completes at/after the old due.
        CheckCurrent(currentContext, currentObservation, utcNow());
        return ready;
    }

    private void CheckCurrent(object currentContext, object currentObservation, DateTimeOffset now)
    {
        if (now >= DueUtc) throw new IOException("canonical_sparse_write_outside_recent_grace");
        if (!ReferenceEquals(currentContext, context) || !ReferenceEquals(currentObservation, observation) ||
            Property<bool>(context, "IsTerminal") || Property<bool>(context, "UserPaused") ||
            Property<bool>(context, "PeerPaused") || Property<bool>(context, "PullTransportPaused") ||
            Property<bool>(observation, "IsClosed") ||
            Field(observation, "root") is not null ||
            !ReferenceEquals(Field(observation, "recent"), recent) ||
            !ReferenceEquals(Property<object>(context, "CurrentV6FrontierRecentProgressGraceWake"), recent) ||
            Property<DateTimeOffset>(recent, "DueUtc") != DueUtc ||
            !ReferenceEquals(Field(observation, "capturedRepair"), repair) ||
            !Equals(Field(observation, "source"), source) || !Equals(Source(observation, context), source) ||
            !ReferenceEquals(Property<object>(context, "DataSession"), dataSession) ||
            !ReferenceEquals(Property<object>(context, "WriteStream"), stream) ||
            !ReferenceEquals(Property<object>(context, "ReceiverSparseChunksWritten"), written) ||
            !ReferenceEquals(Property<object>(context, "ReceiverSparseChunksPendingWrite"), pending))
            throw new IOException("canonical_sparse_write_scope_changed");
        AttemptCanonicalTailProfile.RequireGeometry(Property<long>(context, "FileSizeBytes"),
            Property<int>(context, "ChunkSizeBytes"), Property<int>(context, "ChunkCount"));
    }

    private bool CheckShape()
    {
        const int first = AttemptCanonicalTailProfile.FirstChunk;
        const int bytes = AttemptCanonicalTailProfile.ChunkBytes;
        if (written.Length != AttemptCanonicalTailProfile.ChunkCount ||
            Property<int>(context, "NextChunkIndex") != first + 1 ||
            Property<int>(context, "ChunksTransferred") != first + 1 ||
            Property<int>(context, "PullHighestReceivedChunkIndex") != first + 7 ||
            Property<long>(context, "BytesTransferred") != (long)(first + 1) * bytes)
            throw new IOException("canonical_sparse_write_shape_changed");
        for (var i = 0; i < 8; i++)
            if (i != 2 && written[first + i] != (i is 3 or 5 or 6 or 7))
                throw new IOException("canonical_sparse_write_other_chunk_changed");
        var writes = (HashSet<int>)pending;
        var ready = written[first + 2];
        if (writes.Count > 1 || writes.Any(i => i != first + 2) || ready && writes.Count != 0 ||
            Property<long>(context, "BufferedBytes") != (long)writes.Count * bytes ||
            Property<long>(context, "ReceiverSparseBytesWritten") !=
                AttemptCanonicalTailProfile.FileBytes - (ready ? 2L : 3L) * bytes)
            throw new IOException("canonical_sparse_write_accounting_changed");
        return ready;
    }
}
