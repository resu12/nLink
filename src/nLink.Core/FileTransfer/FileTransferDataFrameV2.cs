using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public abstract record FileTransferDataFrameV2
{
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    public string Type { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string TransferId { get; init; } = string.Empty;
}

public sealed record FileTransferManifestFrameV2 : FileTransferDataFrameV2
{
    public FileTransferManifestFrameV2()
    {
        Type = FileTransferProtocol.ManifestFrameTypeV2;
    }

    public string FileName { get; init; } = string.Empty;

    public long FileSizeBytes { get; init; }

    public int ChunkSizeBytes { get; init; }

    public int ChunkCount { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public sealed record FileTransferRequestChunksFrameV2 : FileTransferDataFrameV2
{
    public FileTransferRequestChunksFrameV2()
    {
        Type = FileTransferProtocol.RequestChunksFrameTypeV2;
    }

    public int StartChunkIndex { get; init; }

    public int RequestedChunkCount { get; init; }

    public int PipelineDepth { get; init; }
}

public sealed record FileTransferChunkDataFrameV2 : FileTransferDataFrameV2
{
    private byte[] data = [];

    public FileTransferChunkDataFrameV2()
    {
        Type = FileTransferProtocol.ChunkDataFrameTypeV2;
    }

    public int ChunkIndex { get; init; }

    public int ChunkCount { get; init; }

    public byte[] Data
    {
        get => data;
        init => data = value ?? [];
    }

    [JsonIgnore]
    public string DataBase64
    {
        get => Convert.ToBase64String(data);
        init => data = string.IsNullOrWhiteSpace(value)
            ? []
            : Convert.FromBase64String(value);
    }
}

public sealed record FileTransferChunkBatchFrameV2 : FileTransferDataFrameV2
{
    private IReadOnlyList<byte[]> dataSegments = Array.Empty<byte[]>();

    public FileTransferChunkBatchFrameV2()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV2;
    }

    public int StartChunkIndex { get; init; }

    public int ChunkCount { get; init; }

    public IReadOnlyList<byte[]> DataSegments
    {
        get => dataSegments;
        init => dataSegments = value ?? Array.Empty<byte[]>();
    }

    [JsonIgnore]
    public IReadOnlyList<string> DataBase64Segments
    {
        get => dataSegments.Select(static segment => Convert.ToBase64String(segment)).ToArray();
        init => dataSegments = value?.Select(static segment => string.IsNullOrWhiteSpace(segment)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(segment))
            .ToArray() ?? Array.Empty<byte[]>();
    }
}

public sealed record FileTransferAckProgressFrameV2 : FileTransferDataFrameV2
{
    public FileTransferAckProgressFrameV2()
    {
        Type = FileTransferProtocol.AckProgressFrameTypeV2;
    }

    public int NextExpectedChunkIndex { get; init; }

    public long BytesCommitted { get; init; }
}

public sealed record FileTransferCancelFrameV2 : FileTransferDataFrameV2
{
    public FileTransferCancelFrameV2()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV2;
    }

    public string? Reason { get; init; }
}

public sealed record FileTransferCompleteFrameV2 : FileTransferDataFrameV2
{
    public FileTransferCompleteFrameV2()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV2;
    }

    public long FileSizeBytes { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}
