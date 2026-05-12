using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferRepairProofV6
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.RepairProofFrameTypeV6;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("transportEpoch")]
    public long TransportEpoch { get; init; }

    [JsonPropertyName("repairRequestId")]
    public string? RepairRequestId { get; init; }

    [JsonPropertyName("appliedChunkCount")]
    public int AppliedChunkCount { get; init; }

    [JsonPropertyName("committedChunkIndex")]
    public int CommittedChunkIndex { get; init; }

    [JsonPropertyName("recoveryMode")]
    public string? RecoveryMode { get; init; }
}
