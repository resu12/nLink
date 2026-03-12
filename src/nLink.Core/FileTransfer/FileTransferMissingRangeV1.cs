using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferChunkRangeV1
{
    [JsonPropertyName("startChunkIndex")]
    public int StartChunkIndex { get; init; }

    [JsonPropertyName("endChunkIndexInclusive")]
    public int EndChunkIndexInclusive { get; init; }
}

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

    [JsonPropertyName("ranges")]
    public IReadOnlyList<FileTransferChunkRangeV1> Ranges { get; init; } = [];

    [JsonPropertyName("nextExpectedChunkIndex")]
    public int NextExpectedChunkIndex { get; init; }

    [JsonPropertyName("highestBufferedChunkIndex")]
    public int HighestBufferedChunkIndex { get; init; }
}
