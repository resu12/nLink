using System.Security.Cryptography;

namespace NLink.Core.Chat;

public sealed class SessionChatService : IChatService
{
    private static readonly TimeSpan WarningClockSkewWindow = TimeSpan.FromMinutes(10);

    private readonly LruMessageIdCache replayCache = new(capacity: 256);
    private readonly Func<DateTimeOffset> nowProvider;
    private ISignalingTransport? transport;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private byte[]? sessionKey;
    private bool isApproved;
    private bool disposed;

    public SessionChatService(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<ChatMessageEventArgs>? MessageReceived;

    public event EventHandler? MessageReceivedBeforeApproved;

    public event EventHandler? StateChanged;

    public bool CanSend => transport is not null && sessionKey is not null;

    public bool HasSessionKey => sessionKey is not null;

    public bool IsApproved => isApproved;

    public void SetReliabilityAttempt(SessionReliabilityAttempt? attempt)
    {
        reliabilityAttempt = attempt;
    }

    public void AttachTransport(ISignalingTransport transport)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(transport);

        if (ReferenceEquals(this.transport, transport))
        {
            return;
        }

        DetachTransport();

        this.transport = transport;
        transport.SessionKeyReady += OnSessionKeyReady;
        transport.ChatMessageReceived += OnChatMessageReceived;
        transport.Approved += OnApproved;
        transport.Rejected += OnRejectedOrDisconnected;
        transport.Disconnected += OnRejectedOrDisconnected;

        sessionKey = null;
        isApproved = false;
        replayCache.Clear();
        RaiseStateChanged();
    }

    public void DetachTransport()
    {
        if (transport is null)
        {
            sessionKey = null;
            isApproved = false;
            replayCache.Clear();
            RaiseStateChanged();
            return;
        }

        transport.SessionKeyReady -= OnSessionKeyReady;
        transport.ChatMessageReceived -= OnChatMessageReceived;
        transport.Approved -= OnApproved;
        transport.Rejected -= OnRejectedOrDisconnected;
        transport.Disconnected -= OnRejectedOrDisconnected;

        transport = null;
        sessionKey = null;
        isApproved = false;
        replayCache.Clear();
        RaiseStateChanged();
    }

    public async Task<ChatMessageRecord?> TrySendTextAsync(string text, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || transport is null || sessionKey is null)
        {
            return null;
        }

        var payload = new ChatMessagePayload
        {
            MessageId = Guid.NewGuid().ToString("N"),
            TimestampUnixMilliseconds = nowProvider().ToUnixTimeMilliseconds(),
            Text = trimmed,
        };

        var bytes = ChatEnvelopeCodec.SerializePayload(payload);
        ChatRuntimeCounters.IncrementSent();
        SessionTimeline.Record("ChatSent");
        if (reliabilityAttempt is not null)
        {
            SessionReliabilityLog.RecordStage(reliabilityAttempt, SessionReliabilityStage.ChatSent);
        }
        await transport.SendChatMessageAsync(bytes, ct);
        replayCache.TryAdd(payload.MessageId);

        return new ChatMessageRecord(
            payload.MessageId,
            payload.Text,
            DateTimeOffset.FromUnixTimeMilliseconds(payload.TimestampUnixMilliseconds),
            IsLocal: true);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DetachTransport();
    }

    private void OnSessionKeyReady(object? sender, TransportSessionKeyReadyEventArgs e)
    {
        sessionKey = e.SharedKey.AsSpan().ToArray();
        SessionTimeline.Record("SessionKeyReady");
        replayCache.Clear();
        if (reliabilityAttempt is not null)
        {
            SessionReliabilityLog.RecordStage(reliabilityAttempt, SessionReliabilityStage.SessionKeyReady);
        }
        RaiseStateChanged();
    }

    private void OnApproved(object? sender, EventArgs e)
    {
        isApproved = true;
        RaiseStateChanged();
    }

    private void OnRejectedOrDisconnected(object? sender, EventArgs e)
    {
        isApproved = false;
        RaiseStateChanged();
    }

    private void OnChatMessageReceived(object? sender, TransportChatMessageEventArgs e)
    {
        ChatRuntimeCounters.IncrementReceived();
        SessionTimeline.Record("ChatReceived");
        if (reliabilityAttempt is not null)
        {
            SessionReliabilityLog.RecordStage(reliabilityAttempt, SessionReliabilityStage.ChatReceived);
        }

        if (sessionKey is null)
        {
            ChatRuntimeCounters.IncrementDecryptFailed();
            Warn("Received chat payload before session key was ready.");
            return;
        }

        try
        {
            var payload = ChatEnvelopeCodec.DeserializePayload(e.Payload);
            if (string.IsNullOrWhiteSpace(payload.MessageId))
            {
                ChatRuntimeCounters.IncrementDecryptFailed();
                Warn("Rejected chat message with empty message id.");
                return;
            }

            if (!replayCache.TryAdd(payload.MessageId))
            {
                Warn($"Rejected duplicate chat message id: {payload.MessageId}");
                return;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(payload.TimestampUnixMilliseconds);
            var skew = (timestamp - nowProvider()).Duration();
            if (skew > WarningClockSkewWindow)
            {
                Warn($"Chat message timestamp outside warning window (message_id={payload.MessageId}).");
            }

            if (!isApproved)
            {
                MessageReceivedBeforeApproved?.Invoke(this, EventArgs.Empty);
            }

            MessageReceived?.Invoke(
                this,
                new ChatMessageEventArgs(new ChatMessageRecord(payload.MessageId, payload.Text, timestamp, IsLocal: false)));
        }
        catch (FormatException)
        {
            ChatRuntimeCounters.IncrementDecryptFailed();
            Warn("Chat decrypt failed (invalid payload encoding).");
        }
        catch (Exception)
        {
            ChatRuntimeCounters.IncrementDecryptFailed();
            Warn("Chat decrypt failed (unexpected error).");
        }
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Warn(string message)
    {
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [nLink] [CHAT] [WARN] {message}");
    }

    private sealed class LruMessageIdCache
    {
        private readonly int capacity;
        private readonly Dictionary<string, LinkedListNode<string>> map = new(StringComparer.Ordinal);
        private readonly LinkedList<string> list = new();

        public LruMessageIdCache(int capacity)
        {
            this.capacity = Math.Max(16, capacity);
        }

        public bool TryAdd(string id)
        {
            if (map.ContainsKey(id))
            {
                return false;
            }

            var node = list.AddFirst(id);
            map[id] = node;

            while (map.Count > capacity)
            {
                var last = list.Last;
                if (last is null)
                {
                    break;
                }

                map.Remove(last.Value);
                list.RemoveLast();
            }

            return true;
        }

        public void Clear()
        {
            map.Clear();
            list.Clear();
        }
    }
}
