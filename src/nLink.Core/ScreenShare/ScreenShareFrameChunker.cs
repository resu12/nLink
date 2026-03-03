namespace NLink.Core.ScreenShare;

public static class ScreenShareFrameChunker
{
    public static IReadOnlyList<ScreenShareFrameChunkV1> ChunkFrame(
        string sessionId,
        long frameId,
        int width,
        int height,
        string encoding,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> encodedFrameBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);

        if (frameId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (timestampUnixMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampUnixMilliseconds));
        }

        if (encodedFrameBytes.IsEmpty)
        {
            throw new ArgumentException("Encoded frame bytes must not be empty.", nameof(encodedFrameBytes));
        }

        var chunkCount = (encodedFrameBytes.Length + ScreenSharePayloadCodec.MaxChunkRawBytes - 1) / ScreenSharePayloadCodec.MaxChunkRawBytes;
        var chunks = new ScreenShareFrameChunkV1[chunkCount];
        var trimmedSessionId = sessionId.Trim();
        var trimmedEncoding = encoding.Trim();

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * ScreenSharePayloadCodec.MaxChunkRawBytes;
            var count = Math.Min(ScreenSharePayloadCodec.MaxChunkRawBytes, encodedFrameBytes.Length - offset);
            var chunkBytes = encodedFrameBytes.Slice(offset, count).ToArray();

            chunks[chunkIndex] = new ScreenShareFrameChunkV1
            {
                SessionId = trimmedSessionId,
                FrameId = frameId,
                Width = width,
                Height = height,
                TimestampUnixMilliseconds = timestampUnixMilliseconds,
                Encoding = trimmedEncoding,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                DataBase64 = Convert.ToBase64String(chunkBytes),
            };
        }

        return chunks;
    }
}
