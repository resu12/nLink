using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Core.Chat;

public interface IChatService : IDisposable
{
    event EventHandler<ChatMessageEventArgs>? MessageReceived;

    event EventHandler? MessageReceivedBeforeApproved;

    event EventHandler? StateChanged;

    bool CanSend { get; }

    bool HasSessionKey { get; }

    bool IsApproved { get; }

    void AttachTransport(ISignalingTransport transport);

    void DetachTransport();

    Task<ChatMessageRecord?> TrySendTextAsync(string text, CancellationToken ct);
}

public readonly record struct ChatMessageRecord(string MessageId, string Text, DateTimeOffset Timestamp, bool IsLocal);

public sealed class ChatMessageEventArgs : EventArgs
{
    public ChatMessageEventArgs(ChatMessageRecord message)
    {
        Message = message;
    }

    public ChatMessageRecord Message { get; }
}
