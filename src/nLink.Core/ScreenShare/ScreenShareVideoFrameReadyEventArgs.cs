namespace NLink.Core.ScreenShare;

public sealed record ScreenShareVideoFrameReadyEventArgs(
    string SessionId,
    long StreamEpoch,
    long FrameId,
    int Width,
    int Height,
    long CapturedTsUtcMs,
    string Encoding,
    bool IsKeyFrame,
    byte[] EncodedFrameBytes,
    ScreenShareVideoStreamConfigV1? StreamConfig,
    ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal,
    long FrameReadyObservedUtcMs = 0);
