using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferWindowUpdateV2
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.WindowUpdateTypeV2;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("nextExpectedChunkIndex")]
    public int NextExpectedChunkIndex { get; init; }

    [JsonPropertyName("grantedUntilChunkIndexExclusive")]
    public int GrantedUntilChunkIndexExclusive { get; init; }

    [JsonPropertyName("bytesReceived")]
    public long BytesReceived { get; init; }
}
