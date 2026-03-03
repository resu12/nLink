using NLink.Core.ScreenShare;

namespace NLink.Infra.Nkn;

internal sealed class ScreenShareFrameAssembler
{
    private const int MaxChunkCount = 256;
    private const int MaxAssembledFrameBytes = ScreenSharePayloadCodec.MaxChunkRawBytes * MaxChunkCount;

    private AssemblyState? currentAssembly;
    private long lastCompletedFrameId = -1;

    public event EventHandler<ScreenShareFrameChunkV1>? ChunkReceived;

    public event EventHandler<ScreenShareFrameCompletedEventArgs>? FrameCompleted;

    public void OnChunk(ScreenShareFrameChunkV1 chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (!TryDecodeChunk(chunk, out var chunkBytes))
        {
            return;
        }

        ChunkReceived?.Invoke(this, chunk);

        if (chunk.FrameId <= lastCompletedFrameId)
        {
            return;
        }

        if (currentAssembly is null || chunk.FrameId > currentAssembly.FrameId)
        {
            currentAssembly = CreateAssembly(chunk);
        }
        else if (chunk.FrameId < currentAssembly.FrameId)
        {
            return;
        }

        if (!currentAssembly.Matches(chunk))
        {
            return;
        }

        if (currentAssembly.ChunkBytes[chunk.ChunkIndex] is not null)
        {
            return;
        }

        currentAssembly.ChunkBytes[chunk.ChunkIndex] = chunkBytes;
        currentAssembly.ReceivedChunkCount++;
        currentAssembly.TotalBytes += chunkBytes.Length;

        if (currentAssembly.TotalBytes > MaxAssembledFrameBytes)
        {
            currentAssembly = null;
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
            frameBytes);

        lastCompletedFrameId = currentAssembly.FrameId;
        currentAssembly = null;
        FrameCompleted?.Invoke(this, completed);
    }

    private static bool TryDecodeChunk(ScreenShareFrameChunkV1 chunk, out byte[] chunkBytes)
    {
        chunkBytes = Array.Empty<byte>();

        if (chunk.FrameId < 0 ||
            chunk.Width <= 0 ||
            chunk.Height <= 0 ||
            string.IsNullOrWhiteSpace(chunk.SessionId) ||
            string.IsNullOrWhiteSpace(chunk.Encoding) ||
            chunk.ChunkCount <= 0 ||
            chunk.ChunkCount > MaxChunkCount ||
            chunk.ChunkIndex < 0 ||
            chunk.ChunkIndex >= chunk.ChunkCount)
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
            chunk.ChunkCount);
    }

    private sealed class AssemblyState
    {
        public AssemblyState(string sessionId, long frameId, int width, int height, string encoding, int chunkCount)
        {
            SessionId = sessionId;
            FrameId = frameId;
            Width = width;
            Height = height;
            Encoding = encoding;
            ChunkCount = chunkCount;
            ChunkBytes = new byte[chunkCount][];
        }

        public string SessionId { get; }

        public long FrameId { get; }

        public int Width { get; }

        public int Height { get; }

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
                   string.Equals(chunk.Encoding, Encoding, StringComparison.Ordinal) &&
                   string.Equals(chunk.SessionId, SessionId, StringComparison.Ordinal);
        }
    }
}

internal sealed record ScreenShareFrameCompletedEventArgs(
    long FrameId,
    int Width,
    int Height,
    string Encoding,
    byte[] EncodedFrameBytes);
