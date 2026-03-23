using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferStartV2
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    [JsonPropertyName("type")]
    public string Type { get; init; } = FileTransferProtocol.StartTypeV2;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; init; }

    [JsonPropertyName("sha256Base64")]
    public string Sha256Base64 { get; init; } = string.Empty;

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("chunkSizeBytes")]
    public int ChunkSizeBytes { get; init; }

    public static implicit operator FileTransferStartV1(FileTransferStartV2 value)
        => new()
        {
            Kind = FileTransferProtocol.Kind,
            Type = FileTransferProtocol.StartTypeV1,
            SessionId = value.SessionId,
            TransferId = value.TransferId,
            FileName = value.FileName,
            FileSizeBytes = value.FileSizeBytes,
            Sha256Base64 = value.Sha256Base64,
            ChunkCount = value.ChunkCount,
            ChunkSizeBytes = value.ChunkSizeBytes,
        };
}
