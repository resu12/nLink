using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferPauseControlV6
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.PauseControlFrameTypeV6;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("epoch")]
    public int Epoch { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("transportEpoch")]
    public long TransportEpoch { get; init; }

    [JsonPropertyName("batchId")]
    public string? BatchId { get; init; }

    [JsonPropertyName("repairRequestId")]
    public string? RepairRequestId { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("recoveryMode")]
    public string? RecoveryMode { get; init; }
}
