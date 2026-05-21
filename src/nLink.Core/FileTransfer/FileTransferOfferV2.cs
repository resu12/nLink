using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferOfferV2
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.OfferTypeV2;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; init; }

    [JsonPropertyName("preferredDataProtocolVersion")]
    public int? PreferredDataProtocolVersion { get; init; }

    [JsonPropertyName("fileTransferRoute")]
    public string? FileTransferRoute { get; init; }
}
