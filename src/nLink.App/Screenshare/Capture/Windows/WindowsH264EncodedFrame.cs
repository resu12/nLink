using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed record WindowsH264EncodedFrame(
    byte[] EncodedBytes,
    int Width,
    int Height,
    long CapturedTsUtcMs,
    bool IsKeyFrame,
    long StreamEpoch,
    ScreenShareVideoStreamConfigV1? StreamConfig = null);
