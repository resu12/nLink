namespace NLink.Core.ScreenShare;

public sealed record ScreenShareVideoFragmentV1
{
    public string Kind { get; init; } = "screenshare";

    public string Type { get; init; } = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public long StreamEpoch { get; init; }

    public long FrameId { get; init; }

    public long CapturedTsUtcMs { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Encoding { get; init; } = "h264";

    public bool IsKeyFrame { get; init; }

    public int FragmentIndex { get; init; }

    public int FragmentCount { get; init; }

    public byte[] Data { get; init; } = Array.Empty<byte>();
}
