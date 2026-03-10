using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferChunkV1
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.ChunkTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("dataBase64")]
    public string DataBase64 { get; init; } = string.Empty;
}
