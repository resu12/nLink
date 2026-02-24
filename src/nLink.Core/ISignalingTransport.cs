using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Core;

public interface ISignalingTransport : IDisposable
{
    event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

    event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    event EventHandler? Approved;

    event EventHandler? Rejected;

    event EventHandler? Disconnected;

    Task HostAsync(SessionCode code, CancellationToken ct);

    Task JoinAsync(SessionCode code, CancellationToken ct);

    Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}

public sealed class IncomingJoinRequestEventArgs : EventArgs
{
    private readonly Func<CancellationToken, Task> approveAsync;
    private readonly Func<CancellationToken, Task> rejectAsync;
    private int handled;

    public IncomingJoinRequestEventArgs(
        Func<CancellationToken, Task> approveAsync,
        Func<CancellationToken, Task> rejectAsync)
    {
        this.approveAsync = approveAsync;
        this.rejectAsync = rejectAsync;
    }

    public bool IsHandled => handled != 0;

    public Task ApproveAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref handled, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return approveAsync(ct);
    }

    public Task RejectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref handled, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return rejectAsync(ct);
    }
}

public sealed class TransportSessionKeyReadyEventArgs : EventArgs
{
    public TransportSessionKeyReadyEventArgs(byte[] sharedKey)
    {
        SharedKey = sharedKey ?? throw new ArgumentNullException(nameof(sharedKey));
    }

    public byte[] SharedKey { get; }
}

public sealed class TransportChatMessageEventArgs : EventArgs
{
    public TransportChatMessageEventArgs(byte[] payload)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public byte[] Payload { get; }
}

