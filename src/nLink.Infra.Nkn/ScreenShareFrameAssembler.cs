using System.Diagnostics;
using NLink.Core.ScreenShare;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

internal sealed class ScreenShareFrameAssembler
{
    private const int MaxChunkCount = 256;
    private const int MaxAssembledFrameBytes = 2_048_000;
    private const int MaxAssemblyAgeMs = 500;
    private const long DegradedFreshnessAgeMs = 1500;
    private const long StaleFrameCutoffAgeMs = 2000;

    private AssemblyState? currentAssembly;
    private long lastCompletedFrameId = -1;
    private long latestSeenFrameId = -1;
    private long chunksDroppedOlderFrame;
    private long assembliesExpired;
    private long assembliesResetNewerFrame;
    private long framesCompleted;
    private long chunksDuplicateIgnored;
    private long chunksInvalidDropped;
    private long framesTooLargeDropped;
    private long framesDroppedStaleAge;
    private volatile string freshnessMode = "normal";

    public event EventHandler<ScreenShareFrameChunkV1>? ChunkReceived;

    public event EventHandler<ScreenShareFrameCompletedEventArgs>? FrameCompleted;

    public ScreenShareFrameAssemblerMetrics GetMetricsSnapshot()
    {
        return new ScreenShareFrameAssemblerMetrics(
            ChunksDroppedOlderFrame: Interlocked.Read(ref chunksDroppedOlderFrame),
            AssembliesExpired: Interlocked.Read(ref assembliesExpired),
            AssembliesResetNewerFrame: Interlocked.Read(ref assembliesResetNewerFrame),
            FramesCompleted: Interlocked.Read(ref framesCompleted),
            ChunksDuplicateIgnored: Interlocked.Read(ref chunksDuplicateIgnored),
            ChunksInvalidDropped: Interlocked.Read(ref chunksInvalidDropped),
            FramesTooLargeDropped: Interlocked.Read(ref framesTooLargeDropped),
            FramesDroppedStaleAge: Interlocked.Read(ref framesDroppedStaleAge),
            FreshnessMode: freshnessMode);
    }

    public void OnChunk(ScreenShareFrameChunkV1 chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.ChunkCount <= 0 ||
            chunk.ChunkCount > MaxChunkCount ||
            chunk.ChunkIndex < 0 ||
            chunk.ChunkIndex >= chunk.ChunkCount)
        {
            Interlocked.Increment(ref chunksInvalidDropped);
            return;
        }

        if (!TryDecodeChunk(chunk, out var chunkBytes))
        {
            Interlocked.Increment(ref chunksInvalidDropped);
            return;
        }

        if (chunkBytes.Length == 0)
        {
            Interlocked.Increment(ref chunksInvalidDropped);
            return;
        }

        ChunkReceived?.Invoke(this, chunk);

        var latestSeen = Interlocked.Read(ref latestSeenFrameId);
        if (chunk.FrameId > latestSeen)
        {
            Interlocked.Exchange(ref latestSeenFrameId, chunk.FrameId);
            latestSeen = chunk.FrameId;
        }
        else if (chunk.FrameId < latestSeen)
        {
            Interlocked.Increment(ref chunksDroppedOlderFrame);
            return;
        }

        if (currentAssembly is not null && currentAssembly.FrameId < latestSeen)
        {
            currentAssembly = null;
            Interlocked.Increment(ref assembliesResetNewerFrame);
        }

        if (chunk.FrameId <= lastCompletedFrameId)
        {
            return;
        }

        if (currentAssembly is null || chunk.FrameId > currentAssembly.FrameId)
        {
            if (currentAssembly is not null && chunk.FrameId > currentAssembly.FrameId)
            {
                Interlocked.Increment(ref assembliesResetNewerFrame);
            }

            currentAssembly = CreateAssembly(chunk);
        }
        else if (chunk.FrameId < currentAssembly.FrameId)
        {
            Interlocked.Increment(ref chunksDroppedOlderFrame);
            return;
        }

        if (currentAssembly.IsExpired(Stopwatch.GetTimestamp(), MaxAssemblyAgeMs))
        {
            currentAssembly = null;
            Interlocked.Increment(ref assembliesExpired);
            return;
        }

        if (!currentAssembly.Matches(chunk))
        {
            Interlocked.Increment(ref chunksInvalidDropped);
            return;
        }

        if (currentAssembly.ChunkBytes[chunk.ChunkIndex] is not null)
        {
            Interlocked.Increment(ref chunksDuplicateIgnored);
            return;
        }

        currentAssembly.ChunkBytes[chunk.ChunkIndex] = chunkBytes;
        currentAssembly.ReceivedChunkCount++;
        currentAssembly.TotalBytes += chunkBytes.Length;

        if (currentAssembly.TotalBytes > MaxAssembledFrameBytes)
        {
            currentAssembly = null;
            Interlocked.Increment(ref framesTooLargeDropped);
            return;
        }

        if (currentAssembly.ReceivedChunkCount != currentAssembly.ChunkCount)
        {
            return;
        }

        var frameBytes = new byte[currentAssembly.TotalBytes];
        var offset = 0;
        for (var i = 0; i < currentAssembly.ChunkCount; i++)
        {
            var bytes = currentAssembly.ChunkBytes[i];
            if (bytes is null)
            {
                Interlocked.Increment(ref chunksInvalidDropped);
                currentAssembly = null;
                return;
            }

            Buffer.BlockCopy(bytes, 0, frameBytes, offset, bytes.Length);
            offset += bytes.Length;
        }

        var completed = new ScreenShareFrameCompletedEventArgs(
            currentAssembly.FrameId,
            currentAssembly.Width,
            currentAssembly.Height,
            currentAssembly.Encoding,
            frameBytes,
            currentAssembly.CapturedTsUtcMs,
            ChunksDroppedOlderFrame: Interlocked.Read(ref chunksDroppedOlderFrame),
            AssembliesExpired: Interlocked.Read(ref assembliesExpired),
            SessionId: currentAssembly.SessionId);
        var frameAgeMs = GetFrameAgeMs(currentAssembly.CapturedTsUtcMs);
        freshnessMode = frameAgeMs >= DegradedFreshnessAgeMs ? "degraded" : "normal";

        lastCompletedFrameId = currentAssembly.FrameId;
        currentAssembly = null;
        if (frameAgeMs > StaleFrameCutoffAgeMs)
        {
            Interlocked.Increment(ref framesDroppedStaleAge);
            LogStaleFrameDropped(completed.SessionId, completed.FrameId, frameAgeMs);
            return;
        }

        Interlocked.Increment(ref framesCompleted);
        FrameCompleted?.Invoke(this, completed);
    }

    private static long GetFrameAgeMs(long capturedTsUtcMs)
    {
        if (capturedTsUtcMs < 1_577_836_800_000L)
        {
            return 0;
        }

        return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - capturedTsUtcMs);
    }

    private static void LogStaleFrameDropped(string sessionId, long frameId, long ageMs)
    {
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_frame_dropped_stale; session_id={sessionId}; frame_id={frameId}; age_ms={ageMs}");
    }

    private static bool TryDecodeChunk(ScreenShareFrameChunkV1 chunk, out byte[] chunkBytes)
    {
        chunkBytes = Array.Empty<byte>();

        if (chunk.FrameId < 0 ||
            chunk.Width <= 0 ||
            chunk.Height <= 0 ||
            string.IsNullOrWhiteSpace(chunk.SessionId) ||
            string.IsNullOrWhiteSpace(chunk.Encoding) ||
            string.IsNullOrWhiteSpace(chunk.DataBase64))
        {
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
        catch (ArgumentNullException)
        {
            return false;
        }

        return chunkBytes.Length <= ScreenSharePayloadCodec.MaxChunkRawBytes;
    }

    private static AssemblyState CreateAssembly(ScreenShareFrameChunkV1 chunk)
    {
        return new AssemblyState(
            chunk.SessionId,
            chunk.FrameId,
            chunk.Width,
            chunk.Height,
            chunk.Encoding,
            chunk.ChunkCount,
            chunk.TimestampUnixMilliseconds);
    }

    private sealed class AssemblyState
    {
        public AssemblyState(
            string sessionId,
            long frameId,
            int width,
            int height,
            string encoding,
            int chunkCount,
            long capturedTsUtcMs)
        {
            SessionId = sessionId;
            FrameId = frameId;
            Width = width;
            Height = height;
            Encoding = encoding;
            ChunkCount = chunkCount;
            ChunkBytes = new byte[chunkCount][];
            StartedTick = Stopwatch.GetTimestamp();
            CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
        }

        public string SessionId { get; }

        public long FrameId { get; }

        public int Width { get; }

        public int Height { get; }

        public string Encoding { get; }

        public int ChunkCount { get; }

        public byte[][] ChunkBytes { get; }

        public long StartedTick { get; }

        public long CapturedTsUtcMs { get; }

        public int ReceivedChunkCount { get; set; }

        public int TotalBytes { get; set; }

        public bool IsExpired(long nowTick, int maxAgeMs)
        {
            return Stopwatch.GetElapsedTime(StartedTick, nowTick) > TimeSpan.FromMilliseconds(maxAgeMs);
        }

        public bool Matches(ScreenShareFrameChunkV1 chunk)
        {
            return chunk.FrameId == FrameId &&
                   chunk.ChunkCount == ChunkCount &&
                   chunk.Width == Width &&
                   chunk.Height == Height &&
                   chunk.TimestampUnixMilliseconds == CapturedTsUtcMs &&
                   string.Equals(chunk.Encoding, Encoding, StringComparison.Ordinal) &&
                   string.Equals(chunk.SessionId, SessionId, StringComparison.Ordinal);
        }
    }
}

internal sealed record ScreenShareFrameAssemblerMetrics(
    long ChunksDroppedOlderFrame,
    long AssembliesExpired,
    long AssembliesResetNewerFrame,
    long FramesCompleted,
    long ChunksDuplicateIgnored,
    long ChunksInvalidDropped,
    long FramesTooLargeDropped,
    long FramesDroppedStaleAge,
    string FreshnessMode);
