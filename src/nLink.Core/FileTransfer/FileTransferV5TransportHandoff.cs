namespace NLink.Core.FileTransfer;

internal enum V5TransportHandoffState
{
    None = 0,
    TransportProofPending = 1,
    NknProofPending = TransportProofPending,
    FrontierRepairOnly = 2,
    BackfillRepair = 3,
    Recovered = 4,
    WaitingForTargetTransport = 5,
    WaitingForRegularNkn = WaitingForTargetTransport,
}

public enum FileTransferTransportKind
{
    Unknown = 0,
    RegularNkn = 1,
    Tuna = 2,
}

public enum FileTransferTransportHandoffKind
{
    None = 0,
    NormalToTunaActivation = 1,
    TunaToNormalFallback = 2,
    TunaRestart = 3,
    RegularNknRecovery = 4,
}

internal sealed class TransportHandoffEpoch
{
    public long EpochId { get; init; }

    public FileTransferTransportHandoffKind Kind { get; init; } = FileTransferTransportHandoffKind.RegularNknRecovery;

    public FileTransferTransportKind SourceTransport { get; init; } = FileTransferTransportKind.Unknown;

    public FileTransferTransportKind TargetTransport { get; init; } = FileTransferTransportKind.RegularNkn;

    public FileTransferDirection Direction { get; init; }

    public string Reason { get; init; } = "transport_rebind";

    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TargetReadyUtc { get; set; }

    public int StartingCommittedChunkIndex { get; init; }

    public int StartingHighestObservedChunkIndex { get; init; } = -1;

    public DateTimeOffset? LastProofUtc { get; set; }

    public V5TransportHandoffState State { get; set; } = V5TransportHandoffState.TransportProofPending;

    public int DurableProgressSamples { get; set; }

    public int LastObservedCommittedChunkIndex { get; set; }

    public int LastObservedHighestChunkIndex { get; set; } = -1;

    public string? LastRepairRequestId { get; set; }

    public DateTimeOffset? LastStateChangeLogUtc { get; set; }
}
