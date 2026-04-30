using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferManifestFrameV4 : FileTransferDataFrameV2
{
    public FileTransferManifestFrameV4()
    {
        Type = FileTransferProtocol.ManifestFrameTypeV4;
    }

    public string FileName { get; init; } = string.Empty;

    public long FileSizeBytes { get; init; }

    public int ChunkSizeBytes { get; init; }

    public int ChunkCount { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public sealed record FileTransferRangeV4
{
    public int StartChunkIndex { get; init; }

    public int ChunkCount { get; init; }
}

public enum FileTransferV4RepairDeliveryMode
{
    BulkOnly = 0,
    ControlBulkRedundant = 1,
}

public sealed record FileTransferStateFrameV4 : FileTransferDataFrameV2
{
    public FileTransferStateFrameV4()
    {
        Type = FileTransferProtocol.StateFrameTypeV4;
    }

    public int Epoch { get; init; }

    public int ContiguousCommittedChunkIndex { get; init; }

    public int DurableReceivedHighestChunkIndex { get; init; }

    public int CreditUntilChunkIndexExclusive { get; init; }

    public IReadOnlyList<FileTransferRangeV4> MissingRanges { get; init; } = [];

    public long BytesCommitted { get; init; }

    public bool ReceiverMemoryPressure { get; init; }

    public bool ReceiverDiskPressure { get; init; }

    public bool TerminalReady { get; init; }
}

public sealed record FileTransferChunkBatchFrameV4 : FileTransferChunkBatchFrameV2
{
    public FileTransferChunkBatchFrameV4()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV4;
    }

    [JsonIgnore]
    public string BatchProfile { get; init; } = string.Empty;

    [JsonIgnore]
    public FileTransferV4RepairDeliveryMode RepairDeliveryMode { get; init; } = FileTransferV4RepairDeliveryMode.BulkOnly;
}

public sealed record FileTransferCompleteFrameV4 : FileTransferDataFrameV2
{
    public FileTransferCompleteFrameV4()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV4;
    }

    public long FileSizeBytes { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public sealed record FileTransferCancelFrameV4 : FileTransferDataFrameV2
{
    public FileTransferCancelFrameV4()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV4;
    }

    public string? Reason { get; init; }
}

public sealed record FileTransferErrorFrameV4 : FileTransferDataFrameV2
{
    public FileTransferErrorFrameV4()
    {
        Type = FileTransferProtocol.ErrorFrameTypeV4;
    }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Message { get; init; }
}
