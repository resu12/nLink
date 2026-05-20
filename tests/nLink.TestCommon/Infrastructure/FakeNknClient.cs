using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

internal sealed class FakeNknClient : INknClient, IAuthoritativeConnectedAddressSource
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FakeNknClient> ClientsByAddress = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<FakeNknClient>> TopicSubscribers = new(StringComparer.Ordinal);

    private readonly HashSet<string> subscriptions = new(StringComparer.Ordinal);
    private readonly string initialAddress;
    private readonly string connectedAddress;
    private readonly string initialMediaAddress;
    private readonly string connectedMediaAddress;
    private readonly string initialBulkAddress;
    private readonly string connectedBulkAddress;
    private bool connected;
    private bool disposed;

    public Func<string, byte[], CancellationToken, Task>? BeforeSendAsync { get; set; }

    public Func<string, byte[], NknBridgeChannel, CancellationToken, Task>? BeforeSendCoreAsync { get; set; }

    public Func<string, byte[], CancellationToken, Task<bool>>? ShouldDeliverSendAsync { get; set; }

    public Func<string, byte[], CancellationToken, Task>? BeforePublishAsync { get; set; }

    public FakeNknClient(string address, string? connectedAddress = null)
    {
        initialAddress = address ?? throw new ArgumentNullException(nameof(address));
        this.connectedAddress = string.IsNullOrWhiteSpace(connectedAddress) ? initialAddress : connectedAddress.Trim();
        initialMediaAddress = BuildMediaAddress(initialAddress);
        connectedMediaAddress = BuildMediaAddress(this.connectedAddress);
        initialBulkAddress = BuildBulkAddress(initialAddress);
        connectedBulkAddress = BuildBulkAddress(this.connectedAddress);
    }

    public string Address
    {
        get
        {
            lock (Gate)
            {
                return connected ? connectedAddress : initialAddress;
            }
        }
    }

    public string InitialAddress => initialAddress;

    public string ConnectedAddress => connectedAddress;

    public string MediaAddress
    {
        get
        {
            lock (Gate)
            {
                return connected ? connectedMediaAddress : initialMediaAddress;
            }
        }
    }

    public string ConnectedMediaAddress => connectedMediaAddress;

    public string BulkAddress
    {
        get
        {
            lock (Gate)
            {
                return connected ? connectedBulkAddress : initialBulkAddress;
            }
        }
    }

    public string ConnectedBulkAddress => connectedBulkAddress;

    bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => connected;

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
            ClientsByAddress[connectedAddress] = this;
            ClientsByAddress[connectedMediaAddress] = this;
            ClientsByAddress[connectedBulkAddress] = this;
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
            ClientsByAddress.Remove(connectedAddress);
            ClientsByAddress.Remove(connectedMediaAddress);
            ClientsByAddress.Remove(connectedBulkAddress);

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
        await SendCoreAsync(destination, payload, NknBridgeChannel.Control, ct).ConfigureAwait(false);
    }

    public async Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct)
    {
        await SendCoreAsync(destination, payload, NknBridgeChannel.Media, ct).ConfigureAwait(false);
    }

    public async Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct)
    {
        await SendCoreAsync(destination, payload, NknBridgeChannel.Bulk, ct).ConfigureAwait(false);
    }

    private async Task SendCoreAsync(string destination, byte[] payload, NknBridgeChannel channel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        EnsureConnected();

        var beforeSendAsync = BeforeSendAsync;
        if (beforeSendAsync is not null)
        {
            await beforeSendAsync(destination, payload, ct).ConfigureAwait(false);
        }

        var beforeSendCoreAsync = BeforeSendCoreAsync;
        if (beforeSendCoreAsync is not null)
        {
            await beforeSendCoreAsync(destination, payload, channel, ct).ConfigureAwait(false);
        }

        var shouldDeliverSendAsync = ShouldDeliverSendAsync;
        if (shouldDeliverSendAsync is not null)
        {
            var shouldDeliver = await shouldDeliverSendAsync(destination, payload, ct).ConfigureAwait(false);
            if (!shouldDeliver)
            {
                await Task.CompletedTask;
                return;
            }
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
            source: channel switch
            {
                NknBridgeChannel.Media => MediaAddress,
                NknBridgeChannel.Bulk => BulkAddress,
                _ => Address,
            },
            payload: payload.AsSpan().ToArray(),
            isTopic: false,
            topic: null,
            channel: channel));

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

    private static string BuildMediaAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var normalized = address.Trim();
        var separatorIndex = normalized.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= normalized.Length - 1)
        {
            return normalized + "-media";
        }

        return normalized[..separatorIndex] + "-media" + normalized[separatorIndex..];
    }

    private static string BuildBulkAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var normalized = address.Trim();
        var separatorIndex = normalized.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= normalized.Length - 1)
        {
            return normalized + "-bulk";
        }

        return normalized[..separatorIndex] + "-bulk" + normalized[separatorIndex..];
    }
}
