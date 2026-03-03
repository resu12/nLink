namespace NLink.Core.ScreenShare;

public sealed record ScreenShareFrameReadyEventArgs(
    string SessionId,
    long FrameId,
    int Width,
    int Height,
    long TimestampUnixMilliseconds,
    string Encoding,
    byte[] EncodedFrameBytes);
