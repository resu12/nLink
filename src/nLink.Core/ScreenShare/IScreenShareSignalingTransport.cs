namespace NLink.Core.ScreenShare;

public interface IScreenShareSignalingTransport
{
    event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    event EventHandler? ScreenShareStopped;

    Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}

public interface IScreenShareTransportBackpressureProbe
{
    bool IsScreenShareTransportCongested { get; }
}

public sealed record ScreenShareFrameCompletedEventArgs(
    long FrameId,
    int Width,
    int Height,
    string Encoding,
    byte[] EncodedFrameBytes,
    long CapturedTsUtcMs = 0,
    long ChunksDroppedOlderFrame = 0,
    long AssembliesExpired = 0,
    string? SessionId = null);
