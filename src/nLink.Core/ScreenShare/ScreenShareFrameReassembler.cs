using System.Diagnostics;

namespace NLink.Core.ScreenShare;

// 0.3.0 RC: protocol freeze - additive changes only.
public sealed class ScreenShareFrameReassembler
{
    public const int MaxInFlightFramesPerSession = 2;
    public const int MaxChunkCount = 128;
    public const int MaxAssembledFrameBytes = 512_000;

    private readonly Dictionary<string, SessionAssemblyState> sessions = new(StringComparer.Ordinal);
    private long framesCompleted;
    private long framesDropped;
    private long framesRejectedOversize;

    public event EventHandler<ScreenShareFrameChunkV1>? ChunkAccepted;

    public event EventHandler<ScreenShareFrameReadyEventArgs>? FrameReady;

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesDropped: Interlocked.Read(ref framesDropped),
            FramesCompleted: Interlocked.Read(ref framesCompleted),
            FramesRejectedOversize: Interlocked.Read(ref framesRejectedOversize));
    }

    public void OnChunk(ScreenShareFrameChunkV1 chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (!TryDecodeChunk(chunk, out var chunkBytes, out var oversizeRejected))
        {
            if (oversizeRejected)
            {
                Interlocked.Increment(ref framesRejectedOversize);
            }

            InvalidateFrame(chunk);
            return;
        }

        var sessionId = chunk.SessionId.Trim();
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionAssemblyState();
            sessions.Add(sessionId, session);
        }

        if (chunk.FrameId <= session.LastCompletedFrameId)
        {
            return;
        }

        if (session.InvalidatedFrameIds.Contains(chunk.FrameId))
        {
            return;
        }

        AssertBufferBounds(session);

        if (!TryGetOrCreateAssembly(session, sessionId, chunk, out var assembly))
        {
            return;
        }

        if (assembly.ChunkBytes[chunk.ChunkIndex] is not null)
        {
            return;
        }

        ChunkAccepted?.Invoke(this, chunk);

        assembly.ChunkBytes[chunk.ChunkIndex] = chunkBytes;
        assembly.ReceivedChunkCount++;
        assembly.TotalBytes += chunkBytes.Length;

        if (assembly.TotalBytes > MaxAssembledFrameBytes)
        {
            Interlocked.Increment(ref framesRejectedOversize);
            InvalidateFrame(sessionId, chunk.FrameId);
            return;
        }

        if (assembly.ReceivedChunkCount != assembly.ChunkCount)
        {
            return;
        }

        var frameBytes = new byte[assembly.TotalBytes];
        var offset = 0;
        for (var i = 0; i < assembly.ChunkCount; i++)
        {
            var bytes = assembly.ChunkBytes[i];
            if (bytes is null)
            {
                return;
            }

            Buffer.BlockCopy(bytes, 0, frameBytes, offset, bytes.Length);
            offset += bytes.Length;
        }

        session.InFlightFrames.Remove(chunk.FrameId);
        session.LastCompletedFrameId = Math.Max(session.LastCompletedFrameId, chunk.FrameId);

        var staleFrameIds = session.InFlightFrames.Keys
            .Where(frameId => frameId < session.LastCompletedFrameId)
            .ToArray();
        foreach (var staleFrameId in staleFrameIds)
        {
            session.InFlightFrames.Remove(staleFrameId);
            Interlocked.Increment(ref framesDropped);
        }

        session.InvalidatedFrameIds.RemoveWhere(frameId => frameId <= session.LastCompletedFrameId);
        Interlocked.Increment(ref framesCompleted);

        FrameReady?.Invoke(
            this,
            new ScreenShareFrameReadyEventArgs(
                sessionId,
                assembly.FrameId,
                assembly.Width,
                assembly.Height,
                assembly.TimestampUnixMilliseconds,
                assembly.Encoding,
                frameBytes));

        if (session.InFlightFrames.Count == 0)
        {
            sessions.Remove(sessionId);
        }
    }

    public void ClearSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        sessions.Remove(sessionId.Trim());
    }

    public void ClearAll()
    {
        sessions.Clear();
    }

    private void InvalidateFrame(ScreenShareFrameChunkV1 chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk.SessionId) || chunk.FrameId < 0)
        {
            return;
        }

        InvalidateFrame(chunk.SessionId.Trim(), chunk.FrameId);
    }

    private void InvalidateFrame(string sessionId, long frameId)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionAssemblyState();
            sessions.Add(sessionId, session);
        }

        var removed = session.InFlightFrames.Remove(frameId);
        var added = session.InvalidatedFrameIds.Add(frameId);
        if (removed || added)
        {
            Interlocked.Increment(ref framesDropped);
        }
    }

    private static bool TryDecodeChunk(ScreenShareFrameChunkV1 chunk, out byte[] chunkBytes, out bool oversizeRejected)
    {
        chunkBytes = Array.Empty<byte>();
        oversizeRejected = false;

        if (chunk.FrameId < 0 ||
            chunk.Width <= 0 ||
            chunk.Height <= 0 ||
            chunk.TimestampUnixMilliseconds < 0 ||
            string.IsNullOrWhiteSpace(chunk.SessionId) ||
            string.IsNullOrWhiteSpace(chunk.Encoding) ||
            chunk.ChunkCount <= 0 ||
            chunk.ChunkIndex < 0 ||
            chunk.ChunkIndex >= chunk.ChunkCount)
        {
            return false;
        }

        if (chunk.ChunkCount > MaxChunkCount)
        {
            oversizeRejected = true;
            return false;
        }

        if ((long)chunk.ChunkCount * ScreenSharePayloadCodec.MaxChunkRawBytes > MaxAssembledFrameBytes)
        {
            oversizeRejected = true;
            return false;
        }

        try
        {
            chunkBytes = Convert.FromBase64String(chunk.DataBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (chunkBytes.Length == 0)
        {
            return false;
        }

        if (chunkBytes.Length > ScreenSharePayloadCodec.MaxChunkRawBytes)
        {
            oversizeRejected = true;
            return false;
        }

        return true;
    }

    private bool TryGetOrCreateAssembly(
        SessionAssemblyState session,
        string sessionId,
        ScreenShareFrameChunkV1 chunk,
        out AssemblyState assembly)
    {
        if (session.InFlightFrames.TryGetValue(chunk.FrameId, out assembly!))
        {
            return assembly.Matches(chunk);
        }

        while (session.InFlightFrames.Count >= MaxInFlightFramesPerSession)
        {
            var oldestFrameId = session.InFlightFrames.Keys.Min();
            if (chunk.FrameId <= oldestFrameId)
            {
                assembly = null!;
                return false;
            }

            session.InFlightFrames.Remove(oldestFrameId);
            Interlocked.Increment(ref framesDropped);
        }

        assembly = new AssemblyState(
            sessionId,
            chunk.FrameId,
            chunk.Width,
            chunk.Height,
            chunk.TimestampUnixMilliseconds,
            chunk.Encoding.Trim(),
            chunk.ChunkCount);
        session.InFlightFrames.Add(chunk.FrameId, assembly);
        AssertBufferBounds(session);
        return true;
    }

    private sealed class SessionAssemblyState
    {
        public long LastCompletedFrameId { get; set; } = -1;

        public SortedDictionary<long, AssemblyState> InFlightFrames { get; } = new();

        public HashSet<long> InvalidatedFrameIds { get; } = new();
    }

    private sealed class AssemblyState
    {
        public AssemblyState(
            string sessionId,
            long frameId,
            int width,
            int height,
            long timestampUnixMilliseconds,
            string encoding,
            int chunkCount)
        {
            SessionId = sessionId;
            FrameId = frameId;
            Width = width;
            Height = height;
            TimestampUnixMilliseconds = timestampUnixMilliseconds;
            Encoding = encoding;
            ChunkCount = chunkCount;
            ChunkBytes = new byte[chunkCount][];
        }

        public string SessionId { get; }

        public long FrameId { get; }

        public int Width { get; }

        public int Height { get; }

        public long TimestampUnixMilliseconds { get; }

        public string Encoding { get; }

        public int ChunkCount { get; }

        public byte[][] ChunkBytes { get; }

        public int ReceivedChunkCount { get; set; }

        public int TotalBytes { get; set; }

        public bool Matches(ScreenShareFrameChunkV1 chunk)
        {
            return chunk.FrameId == FrameId &&
                   chunk.ChunkCount == ChunkCount &&
                   chunk.Width == Width &&
                   chunk.Height == Height &&
                   chunk.TimestampUnixMilliseconds == TimestampUnixMilliseconds &&
                   string.Equals(chunk.Encoding, Encoding, StringComparison.Ordinal) &&
                   string.Equals(chunk.SessionId, SessionId, StringComparison.Ordinal);
        }
    }

    [Conditional("DEBUG")]
    private static void AssertBufferBounds(SessionAssemblyState session)
    {
        if (session.InFlightFrames.Count > MaxInFlightFramesPerSession)
        {
            throw new InvalidOperationException($"Screenshare receiver exceeded max of {MaxInFlightFramesPerSession} in-flight frames.");
        }
    }
}
