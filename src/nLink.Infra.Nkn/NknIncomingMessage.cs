namespace NLink.Infra.Nkn;

internal sealed class NknIncomingMessage : EventArgs
{
    public NknIncomingMessage(string source, byte[] payload, bool isTopic, string? topic, NknClientChannel channel = NknClientChannel.Control)
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

    public NknClientChannel Channel { get; }
}

internal enum NknClientChannel
{
    Control = 0,
    Media = 1,
}
