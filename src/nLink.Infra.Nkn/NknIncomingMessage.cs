namespace NLink.Infra.Nkn;

internal sealed class NknIncomingMessage : EventArgs
{
    public NknIncomingMessage(string source, byte[] payload, bool isTopic, string? topic)
    {
        Source = source ?? string.Empty;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        IsTopic = isTopic;
        Topic = topic;
    }

    public string Source { get; }

    public byte[] Payload { get; }

    public bool IsTopic { get; }

    public string? Topic { get; }
}
