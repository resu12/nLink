namespace NLink.Core.ScreenShare;

public static class ScreenShareVideoFragmenter
{
    // NKN delivery is bursty enough that splitting a typical reduced-mode H.264 frame
    // across multiple transport fragments materially increases whole-frame loss.
    public const int MaxFragmentRawBytes = 24_000;

    public static IReadOnlyList<ScreenShareVideoFragmentV1> FragmentAccessUnit(
        string sessionId,
        long streamEpoch,
        long frameId,
        long capturedTsUtcMs,
        int width,
        int height,
        string encoding,
        bool isKeyFrame,
        byte[] accessUnitBytes,
        int maxFragmentRawBytes = MaxFragmentRawBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(accessUnitBytes);

        if (streamEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamEpoch));
        }

        if (frameId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }

        if (capturedTsUtcMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capturedTsUtcMs));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (accessUnitBytes.Length == 0)
        {
            throw new ArgumentException("Access-unit bytes must not be empty.", nameof(accessUnitBytes));
        }

        if (maxFragmentRawBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFragmentRawBytes));
        }

        var trimmedSessionId = sessionId.Trim();
        var trimmedEncoding = encoding.Trim();
        var fragmentCount = (int)Math.Ceiling(accessUnitBytes.Length / (double)maxFragmentRawBytes);
        var fragments = new ScreenShareVideoFragmentV1[fragmentCount];

        for (var fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
        {
            var offset = fragmentIndex * maxFragmentRawBytes;
            var bytesRemaining = accessUnitBytes.Length - offset;
            var fragmentLength = Math.Min(maxFragmentRawBytes, bytesRemaining);
            var fragmentBytes = new byte[fragmentLength];
            Buffer.BlockCopy(accessUnitBytes, offset, fragmentBytes, 0, fragmentLength);

            fragments[fragmentIndex] = new ScreenShareVideoFragmentV1
            {
                SessionId = trimmedSessionId,
                StreamEpoch = streamEpoch,
                FrameId = frameId,
                CapturedTsUtcMs = capturedTsUtcMs,
                Width = width,
                Height = height,
                Encoding = trimmedEncoding,
                IsKeyFrame = isKeyFrame,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
                Data = fragmentBytes,
            };
        }

        return fragments;
    }
}
