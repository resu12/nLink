using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferTransportEpochV6
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.TransportEpochFrameTypeV6;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("transportEpoch")]
    public long TransportEpoch { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "target_proof_pending";

    [JsonPropertyName("handoffKind")]
    public string? HandoffKind { get; init; }

    [JsonPropertyName("sourceTransport")]
    public string? SourceTransport { get; init; }

    [JsonPropertyName("targetTransport")]
    public string? TargetTransport { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("recoveryMode")]
    public string? RecoveryMode { get; init; }
}
