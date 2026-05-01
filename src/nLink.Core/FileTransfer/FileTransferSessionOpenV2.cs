namespace NLink.Core.FileTransfer;

public sealed record FileTransferSessionOpenV2
{
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    public string Type { get; init; } = FileTransferProtocol.SessionOpenTypeV2;

    public string SessionId { get; init; } = string.Empty;

    public string TransferId { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } = FileTransferProtocol.ProtocolVersionV4;

    public string SessionRole { get; init; } = string.Empty;

    public int ChunkSizeBytes { get; init; }

    public int InitialPipelineDepth { get; init; }
}
