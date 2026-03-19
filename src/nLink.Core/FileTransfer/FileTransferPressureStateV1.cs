using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferPressureStateV1
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.PressureStateTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = FileTransferProtocol.PressureModeNormal;

    [JsonPropertyName("suggestedSendAheadChunks")]
    public int SuggestedSendAheadChunks { get; init; }

    [JsonPropertyName("receiverNextExpectedChunkIndex")]
    public int ReceiverNextExpectedChunkIndex { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = FileTransferProtocol.PressureReasonBulkBacklog;
}
