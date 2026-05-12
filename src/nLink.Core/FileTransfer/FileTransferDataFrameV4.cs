using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public record FileTransferManifestFrameV4 : FileTransferDataFrame
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

public record FileTransferRangeV4
{
    public int StartChunkIndex { get; init; }

    public int ChunkCount { get; init; }
}

public enum FileTransferV4RepairDeliveryMode
{
    BulkOnly = 0,
    ControlBulkRedundant = 1,
}

public record FileTransferStateFrameV4 : FileTransferDataFrame
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

public record FileTransferChunkBatchFrameV4 : FileTransferChunkBatchFrame
{
    public FileTransferChunkBatchFrameV4()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV4;
    }

    [JsonIgnore]
    public string BatchProfile { get; init; } = string.Empty;

    [JsonIgnore]
    public FileTransferV4RepairDeliveryMode RepairDeliveryMode { get; init; } = FileTransferV4RepairDeliveryMode.BulkOnly;

    [JsonIgnore]
    public bool ForceRegularNknBulk { get; init; }
}

public record FileTransferCompleteFrameV4 : FileTransferDataFrame
{
    public FileTransferCompleteFrameV4()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV4;
    }

    public long FileSizeBytes { get; init; }

    public string Sha256Base64 { get; init; } = string.Empty;
}

public record FileTransferCancelFrameV4 : FileTransferDataFrame
{
    public FileTransferCancelFrameV4()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV4;
    }

    public string? Reason { get; init; }
}

public record FileTransferErrorFrameV4 : FileTransferDataFrame
{
    public FileTransferErrorFrameV4()
    {
        Type = FileTransferProtocol.ErrorFrameTypeV4;
    }

    public string ErrorCode { get; init; } = string.Empty;

    public string? Message { get; init; }
}

public record FileTransferPauseControlFrameV4 : FileTransferDataFrame
{
    public FileTransferPauseControlFrameV4()
    {
        Type = FileTransferProtocol.PauseControlFrameTypeV4;
    }

    public int Epoch { get; init; }

    public bool Paused { get; init; }

    public string? Reason { get; init; }
}

public abstract record FileTransferDataFrameV5Metadata
{
    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferManifestFrameV5 : FileTransferManifestFrameV4
{
    public FileTransferManifestFrameV5()
    {
        Type = FileTransferProtocol.ManifestFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferStateFrameV5 : FileTransferStateFrameV4
{
    public FileTransferStateFrameV5()
    {
        Type = FileTransferProtocol.StateFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferChunkBatchFrameV5 : FileTransferChunkBatchFrameV4
{
    public FileTransferChunkBatchFrameV5()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferCompleteFrameV5 : FileTransferCompleteFrameV4
{
    public FileTransferCompleteFrameV5()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferCancelFrameV5 : FileTransferCancelFrameV4
{
    public FileTransferCancelFrameV5()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferErrorFrameV5 : FileTransferErrorFrameV4
{
    public FileTransferErrorFrameV5()
    {
        Type = FileTransferProtocol.ErrorFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferPauseControlFrameV5 : FileTransferPauseControlFrameV4
{
    public FileTransferPauseControlFrameV5()
    {
        Type = FileTransferProtocol.PauseControlFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferHandoffFrameV5 : FileTransferDataFrame
{
    public FileTransferHandoffFrameV5()
    {
        Type = FileTransferProtocol.HandoffFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferRepairRequestFrameV5 : FileTransferDataFrame
{
    public FileTransferRepairRequestFrameV5()
    {
        Type = FileTransferProtocol.RepairRequestFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? RepairRequestId { get; init; }

    public IReadOnlyList<FileTransferRangeV4> MissingRanges { get; init; } = [];

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferRepairProofFrameV5 : FileTransferDataFrame
{
    public FileTransferRepairProofFrameV5()
    {
        Type = FileTransferProtocol.RepairProofFrameTypeV5;
    }

    public long TransportEpoch { get; init; }

    public string? RepairRequestId { get; init; }

    public int AppliedChunkCount { get; init; }

    public int CommittedChunkIndex { get; init; }

    public string? RecoveryMode { get; init; }
}

public interface IFileTransferTransportMetadataFrame
{
    long TransportEpoch { get; }

    string? BatchId { get; }

    string? RepairRequestId { get; }

    string? Priority { get; }

    string? RecoveryMode { get; }
}

public record FileTransferManifestFrameV6 : FileTransferManifestFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferManifestFrameV6()
    {
        Type = FileTransferProtocol.ManifestFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferReceiverStateFrameV6 : FileTransferStateFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferReceiverStateFrameV6()
    {
        Type = FileTransferProtocol.ReceiverStateFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferChunkBatchFrameV6 : FileTransferChunkBatchFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferChunkBatchFrameV6()
    {
        Type = FileTransferProtocol.ChunkBatchFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferTransportEpochFrameV6 : FileTransferDataFrame
{
    public FileTransferTransportEpochFrameV6()
    {
        Type = FileTransferProtocol.TransportEpochFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferTransportProbeFrameV6 : FileTransferDataFrame
{
    public FileTransferTransportProbeFrameV6()
    {
        Type = FileTransferProtocol.TransportProbeFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? ProbeId { get; init; }

    public string? TargetTransport { get; init; }
}

public record FileTransferFrontierRequestFrameV6 : FileTransferDataFrame
{
    public FileTransferFrontierRequestFrameV6()
    {
        Type = FileTransferProtocol.FrontierRequestFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? RepairRequestId { get; init; }

    public IReadOnlyList<FileTransferRangeV4> MissingRanges { get; init; } = [];

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferRepairProofFrameV6 : FileTransferDataFrame
{
    public FileTransferRepairProofFrameV6()
    {
        Type = FileTransferProtocol.RepairProofFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? RepairRequestId { get; init; }

    public int AppliedChunkCount { get; init; }

    public int CommittedChunkIndex { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferCompleteFrameV6 : FileTransferCompleteFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferCompleteFrameV6()
    {
        Type = FileTransferProtocol.SessionCompleteFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferCancelFrameV6 : FileTransferCancelFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferCancelFrameV6()
    {
        Type = FileTransferProtocol.SessionCancelFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferErrorFrameV6 : FileTransferErrorFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferErrorFrameV6()
    {
        Type = FileTransferProtocol.ErrorFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferPauseControlFrameV6 : FileTransferPauseControlFrameV4, IFileTransferTransportMetadataFrame
{
    public FileTransferPauseControlFrameV6()
    {
        Type = FileTransferProtocol.PauseControlFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public string? BatchId { get; init; }

    public string? RepairRequestId { get; init; }

    public string? Priority { get; init; }

    public string? RecoveryMode { get; init; }
}

public record FileTransferHeartbeatFrameV6 : FileTransferDataFrame
{
    public FileTransferHeartbeatFrameV6()
    {
        Type = FileTransferProtocol.HeartbeatFrameTypeV6;
    }

    public long TransportEpoch { get; init; }

    public long Sequence { get; init; }

    public long SentUnixTimeMilliseconds { get; init; }
}
