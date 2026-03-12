namespace NLink.Core.ScreenShare;

public interface IScreenShareMediaTransport
{
    event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
    event EventHandler? ScreenShareStopped;

    bool IsCongested { get; }

    Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}
