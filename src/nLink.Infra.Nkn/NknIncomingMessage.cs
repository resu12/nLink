namespace NLink.Infra.Nkn;

internal enum NknBridgeChannel
{
    Control = 0,
    Media = 1,
    Bulk = 2,
}

internal sealed class NknIncomingMessage : EventArgs
{
    public NknIncomingMessage(string source, byte[] payload, bool isTopic, string? topic, NknBridgeChannel channel = NknBridgeChannel.Control)
    {
        Source = source ?? string.Empty;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        IsTopic = isTopic;
        Topic = topic;
        Channel = channel;
    }

    public string Source { get; }

    public byte[] Payload { get; }

    public bool IsTopic { get; }

    public string? Topic { get; }

    public NknBridgeChannel Channel { get; }
}
