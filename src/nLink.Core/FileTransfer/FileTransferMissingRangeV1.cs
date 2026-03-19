using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferMissingRangeV1
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.MissingRangeTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("startChunkIndex")]
    public int StartChunkIndex { get; init; }

    [JsonPropertyName("endChunkIndexExclusive")]
    public int EndChunkIndexExclusive { get; init; }
}
