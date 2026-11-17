using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferSessionOpenV2
{
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    public string Type { get; init; } = FileTransferProtocol.SessionOpenTypeV2;

    public string SessionId { get; init; } = string.Empty;

    public string TransferId { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } = FileTransferProtocol.ProtocolVersionV6;

    [JsonPropertyName("fileTransferRoute")]
    public string? FileTransferRoute { get; init; }

    public string SessionRole { get; init; } = string.Empty;

    public int ChunkSizeBytes { get; init; }

    public int InitialPipelineDepth { get; init; }

    [JsonPropertyName("fallbackBootstrapCorrelationVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FallbackBootstrapCorrelationVersion { get; init; }

    [JsonPropertyName("fallbackBootstrapIssuerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackBootstrapIssuerId { get; init; }
}
