namespace NLink.Infra.Nkn;

internal interface INknClient : IDisposable
{
    string Address { get; }
    string MediaAddress { get; }
    string BulkAddress { get; }

    Task ConnectAsync(CancellationToken ct);

    Task DisconnectAsync();

    Task SubscribeAsync(string topic, CancellationToken ct);

    Task UnsubscribeAsync(string topic);

    Task PublishAsync(string topic, byte[] payload, CancellationToken ct);

    Task SendAsync(string destination, byte[] payload, CancellationToken ct);
    Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct);
    Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct);

    event EventHandler<NknIncomingMessage>? MessageReceived;

    event EventHandler? Disconnected;
}

// Return null to skip a retired handshake; an admitted frame is written in full.
internal delegate ValueTask? OwnedHandshakeWrite(Func<ValueTask> beginWrite);

internal interface IOwnedHandshakeClient
{
    Task<bool> SendOwnedHandshakeAsync(string destination, byte[] bytes, NknBridgeChannel channel, bool priority,
        OwnedHandshakeWrite admit, CancellationToken ct);
}

internal interface IRuntimeUnlockRetryPriorityNknClient
{
    Task SendRuntimeUnlockRetryControlAsync(
        string destination,
        byte[] payload,
        NknBridgeChannel channel,
        CancellationToken ct);
}
