namespace NLink.Core.ScreenShare;

public sealed record ScreenShareEncodedFramePacket(
    string SessionId,
    long FrameId,
    int Width,
    int Height,
    string Encoding,
    long TimestampUnixMilliseconds,
    byte[] EncodedFrameBytes,
    bool IsKeyFrame = false,
    long StreamEpoch = 0,
    ScreenShareVideoStreamConfigV1? StreamConfig = null);
