using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

internal sealed class FakeNknClient : INknClient
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FakeNknClient> ClientsByAddress = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<FakeNknClient>> TopicSubscribers = new(StringComparer.Ordinal);

    private readonly HashSet<string> subscriptions = new(StringComparer.Ordinal);
    private bool connected;
    private bool disposed;

    public Func<string, byte[], CancellationToken, Task>? BeforeSendAsync { get; set; }

    public Func<string, byte[], CancellationToken, Task>? BeforePublishAsync { get; set; }

    public FakeNknClient(string address)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
    }

    public string Address { get; }

    public event EventHandler<NknIncomingMessage>? MessageReceived;

    public event EventHandler? Disconnected;

    public static void ResetNetwork()
    {
        List<FakeNknClient> clients;
        lock (Gate)
        {
            clients = ClientsByAddress.Values.ToList();
            ClientsByAddress.Clear();
            TopicSubscribers.Clear();
        }

        foreach (var client in clients)
        {
            client.connected = false;
            client.subscriptions.Clear();
        }
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        lock (Gate)
        {
            ClientsByAddress[Address] = this;
            connected = true;
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        bool wasConnected;
        lock (Gate)
        {
            wasConnected = connected;
            connected = false;
            ClientsByAddress.Remove(Address);

            foreach (var topic in subscriptions.ToArray())
            {
                if (TopicSubscribers.TryGetValue(topic, out var set))
                {
                    set.Remove(this);
                    if (set.Count == 0)
                    {
                        TopicSubscribers.Remove(topic);
                    }
                }
            }

            subscriptions.Clear();
        }

        if (wasConnected)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topic, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        EnsureConnected();

        lock (Gate)
        {
            subscriptions.Add(topic);
            if (!TopicSubscribers.TryGetValue(topic, out var set))
            {
                set = new HashSet<FakeNknClient>();
                TopicSubscribers[topic] = set;
            }

            set.Add(this);
        }

        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string topic)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        lock (Gate)
        {
            subscriptions.Remove(topic);
            if (TopicSubscribers.TryGetValue(topic, out var set))
            {
                set.Remove(this);
                if (set.Count == 0)
                {
                    TopicSubscribers.Remove(topic);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        EnsureConnected();

        var beforePublishAsync = BeforePublishAsync;
        if (beforePublishAsync is not null)
        {
            await beforePublishAsync(topic, payload, ct).ConfigureAwait(false);
        }

        List<FakeNknClient> recipients;
        lock (Gate)
        {
            recipients = TopicSubscribers.TryGetValue(topic, out var set)
                ? set.ToList()
                : new List<FakeNknClient>();
        }

        foreach (var recipient in recipients)
        {
            recipient.Receive(new NknIncomingMessage(
                source: Address,
                payload: payload.AsSpan().ToArray(),
                isTopic: true,
                topic: topic));
        }

        await Task.CompletedTask;
    }

    public async Task SendAsync(string destination, byte[] payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        EnsureConnected();

        var beforeSendAsync = BeforeSendAsync;
        if (beforeSendAsync is not null)
        {
            await beforeSendAsync(destination, payload, ct).ConfigureAwait(false);
        }

        FakeNknClient? recipient;
        lock (Gate)
        {
            ClientsByAddress.TryGetValue(destination, out recipient);
        }

        if (recipient is null)
        {
            throw new InvalidOperationException("fake_destination_not_found");
        }

        recipient.Receive(new NknIncomingMessage(
            source: Address,
            payload: payload.AsSpan().ToArray(),
            isTopic: false,
            topic: null));

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = DisconnectAsync();
    }

    private void Receive(NknIncomingMessage message)
    {
        if (disposed)
        {
            return;
        }

        MessageReceived?.Invoke(this, message);
    }

    private void EnsureConnected()
    {
        if (!connected)
        {
            throw new InvalidOperationException("fake_client_not_connected");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
