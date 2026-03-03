namespace NLink.Core.ScreenShare;

public sealed record ScreenShareMetrics(
    long FramesCaptured = 0,
    long FramesQueued = 0,
    long FramesDropped = 0,
    long ChunksSent = 0,
    long FramesCompleted = 0,
    long FramesRejectedOversize = 0,
    long FramesDecoded = 0,
    long DecodeErrors = 0,
    long FramesCoalesced = 0);
