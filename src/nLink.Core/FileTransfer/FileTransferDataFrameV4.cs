using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public sealed record FileTransferManifestFrameV4 : FileTransferDataFrame
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

public sealed record FileTransferStateFrameV4 : FileTransferDataFrame
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

    public bool TransferPaused { get; init; }

    public string? TransferPauseReason { get; init; }
}

public sealed record FileTransferChunkBatchFrameV4 : FileTransferChunkBatchFrame
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

public sealed record FileTransferCompleteFrameV4 : FileTransferDataFrame
{
    public FileTransferCompleteFrameV4()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV4;
    }

    public long FileSizeBytes { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public sealed record FileTransferCancelFrameV4 : FileTransferDataFrame
{
    public FileTransferCancelFrameV4()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV4;
    }

    public string? Reason { get; init; }
}

public sealed record FileTransferErrorFrameV4 : FileTransferDataFrame
{
    public FileTransferErrorFrameV4()
    {
        Type = FileTransferProtocol.ErrorFrameTypeV4;
    }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Message { get; init; }
}

public sealed record FileTransferPauseControlFrameV4 : FileTransferDataFrame
{
    public FileTransferPauseControlFrameV4()
    {
        Type = FileTransferProtocol.PauseControlFrameTypeV4;
    }

    public int Epoch { get; init; }

    public bool Paused { get; init; }

    public string? Reason { get; init; }
}
