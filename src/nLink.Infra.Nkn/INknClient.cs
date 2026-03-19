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
