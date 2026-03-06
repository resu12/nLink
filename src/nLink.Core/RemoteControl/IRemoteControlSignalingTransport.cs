namespace NLink.Core.RemoteControl;

public interface IRemoteControlSignalingTransport
{
    event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
    event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
    event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
    event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
    event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
    event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
    event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
    event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;

    Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct);
    Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct);
    Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct);
    Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct);
    Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct);
    Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct);
    Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct);
    Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct);
}

public sealed class RemoteControlRequestReceivedEventArgs : EventArgs
{
    public RemoteControlRequestReceivedEventArgs(ControlRequestMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlRequestMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlResponseReceivedEventArgs : EventArgs
{
    public RemoteControlResponseReceivedEventArgs(ControlResponseMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlResponseMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlStartReceivedEventArgs : EventArgs
{
    public RemoteControlStartReceivedEventArgs(ControlStartMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlStartMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlStopReceivedEventArgs : EventArgs
{
    public RemoteControlStopReceivedEventArgs(ControlStopMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlStopMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlInputReceivedEventArgs : EventArgs
{
    public RemoteControlInputReceivedEventArgs(ControlInputMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlInputMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlDisplayInfoReceivedEventArgs : EventArgs
{
    public RemoteControlDisplayInfoReceivedEventArgs(ControlDisplayInfoMessageV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlDisplayInfoMessageV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlAckReceivedEventArgs : EventArgs
{
    public RemoteControlAckReceivedEventArgs(ControlInputAckV1 ack, string? peerId)
    {
        Ack = ack ?? throw new ArgumentNullException(nameof(ack));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public ControlInputAckV1 Ack { get; }

    public string? PeerId { get; }
}

public sealed class RemoteControlStateSnapshotReceivedEventArgs : EventArgs
{
    public RemoteControlStateSnapshotReceivedEventArgs(ControlStateSnapshotV1 snapshot, string source)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Source = source?.Trim() ?? string.Empty;
    }

    public ControlStateSnapshotV1 Snapshot { get; }

    public string Source { get; }
}
