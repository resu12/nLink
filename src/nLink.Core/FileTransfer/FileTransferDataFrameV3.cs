using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferManifestFrameV3 : FileTransferDataFrameV2
{
    public FileTransferManifestFrameV3()
    {
        Type = FileTransferProtocol.ManifestFrameTypeV3;
    }

    public string FileName { get; init; } = string.Empty;

    public long FileSizeBytes { get; init; }

    public int ChunkSizeBytes { get; init; }

    public int ChunkCount { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public sealed record FileTransferGrantWindowFrameV3 : FileTransferDataFrameV2
{
    public FileTransferGrantWindowFrameV3()
    {
        Type = FileTransferProtocol.GrantWindowFrameTypeV3;
    }

    public int NextExpectedChunkIndex { get; init; }

    public int GrantedUntilChunkIndexExclusive { get; init; }

    public long BytesCommitted { get; init; }
}

public sealed record FileTransferAckProgressFrameV3 : FileTransferDataFrameV2
{
    public FileTransferAckProgressFrameV3()
    {
        Type = FileTransferProtocol.AckProgressFrameTypeV3;
    }

    public int NextExpectedChunkIndex { get; init; }

    public long BytesCommitted { get; init; }
}

public sealed record FileTransferChunkDataFrameV3 : FileTransferChunkDataFrameV2
{
    public FileTransferChunkDataFrameV3()
    {
        Type = FileTransferProtocol.ChunkDataFrameTypeV3;
    }
}

public sealed record FileTransferChunkBatchFrameV3 : FileTransferChunkBatchFrameV2
{
    public FileTransferChunkBatchFrameV3()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV3;
    }

    [JsonIgnore]
    public string BatchProfile { get; init; } = string.Empty;
}

public sealed record FileTransferRepairRequestFrameV3 : FileTransferDataFrameV2
{
    public FileTransferRepairRequestFrameV3()
    {
        Type = FileTransferProtocol.RepairRequestFrameTypeV3;
    }

    public int StartChunkIndex { get; init; }

    public int RequestedChunkCount { get; init; }
}

public sealed record FileTransferRepairRangeV3
{
    public int StartChunkIndex { get; init; }

    public int RequestedChunkCount { get; init; }
}

public sealed record FileTransferRepairRequestSetFrameV3 : FileTransferDataFrameV2
{
    public FileTransferRepairRequestSetFrameV3()
    {
        Type = FileTransferProtocol.RepairRequestSetFrameTypeV3;
    }

    public IReadOnlyList<FileTransferRepairRangeV3> Ranges { get; init; } = [];
}
