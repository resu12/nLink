using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferTransportProbeV6
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.TransportProbeFrameTypeV6;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("transportEpoch")]
    public long TransportEpoch { get; init; }

    [JsonPropertyName("probeId")]
    public string? ProbeId { get; init; }

    [JsonPropertyName("targetTransport")]
    public string? TargetTransport { get; init; }
}
