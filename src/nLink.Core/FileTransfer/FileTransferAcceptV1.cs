using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferAcceptV1
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.AcceptTypeV1;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("acceptedDataProtocolVersion")]
    public int? AcceptedDataProtocolVersion { get; init; }

    [JsonPropertyName("fileTransferRoute")]
    public string? FileTransferRoute { get; init; }
}
