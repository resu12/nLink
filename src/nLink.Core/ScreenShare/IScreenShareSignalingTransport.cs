namespace NLink.Core.ScreenShare;

public interface IScreenShareSignalingTransport
{
    event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    event EventHandler? ScreenShareStopped;
    event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
    event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
    event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
    event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;
    event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;

    Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct);
    Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct);
    Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct);
    Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct);
    Task SendScreenShareCursorStateAsync(ScreenShareCursorStateV1 message, CancellationToken ct);
}

public interface IScreenShareTransportBackpressureProbe
{
    bool IsScreenShareTransportCongested { get; }
    bool IsScreenShareTransportSeverelyCongested { get; }
    int ScreenShareTransportQueueDepth { get; }
    int ScreenShareTransportQueuedBytes { get; }
    long ScreenShareTransportOldestQueuedAgeMs { get; }
    long ScreenShareTransportRecentDropCount { get; }
    long ScreenShareTransportRecentHealthIssueCount { get; }
    bool IsScreenShareTransportHealthSeverelyDegraded { get; }
}

public interface IScreenShareTransportPolicyController
{
    Task SetScreenShareTransportCatchUpOnlyAsync(bool active, CancellationToken ct);
    void FlushScreenShareTransportQueue(string reason);
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
    string? SessionId = null,
    bool IsKeyFrame = false,
    long StreamEpoch = 0,
    ScreenShareVideoStreamConfigV1? StreamConfig = null,
    ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal,
    long FrameReadyObservedUtcMs = 0);

public sealed class ScreenSharePressureStateReceivedEventArgs : EventArgs
{
    public ScreenSharePressureStateReceivedEventArgs(ScreenSharePressureStateV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ScreenSharePressureStateV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class ScreenShareVideoStreamConfigReceivedEventArgs : EventArgs
{
    public ScreenShareVideoStreamConfigReceivedEventArgs(ScreenShareVideoStreamConfigV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ScreenShareVideoStreamConfigV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class ScreenShareRecoveryReceiptReceivedEventArgs : EventArgs
{
    public ScreenShareRecoveryReceiptReceivedEventArgs(ScreenShareRecoveryReceiptV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ScreenShareRecoveryReceiptV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class ScreenShareVideoKeyframeRequestReceivedEventArgs : EventArgs
{
    public ScreenShareVideoKeyframeRequestReceivedEventArgs(ScreenShareVideoKeyframeRequestV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ScreenShareVideoKeyframeRequestV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class ScreenShareCursorStateReceivedEventArgs : EventArgs
{
    public ScreenShareCursorStateReceivedEventArgs(ScreenShareCursorStateV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ScreenShareCursorStateV1 Message { get; }

    public string? PeerId { get; }
}
