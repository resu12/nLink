using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferHeartbeatV6
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.HeartbeatFrameTypeV6;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("transportEpoch")]
    public long TransportEpoch { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("sentUnixTimeMilliseconds")]
    public long SentUnixTimeMilliseconds { get; init; }
}
