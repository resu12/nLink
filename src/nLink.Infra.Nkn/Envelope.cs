namespace NLink.Infra.Nkn;

internal sealed record Envelope(
    int Version,
    string Code,
    string MessageId,
    MsgType Type,
    byte[] Payload,
    long UnixTimeMs,
    string? ReplyTo);
