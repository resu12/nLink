namespace NLink.Core.FileTransfer;

public enum V6TransportEpochState
{
    None = 0,
    EpochStarting = 1,
    TargetProofPending = 2,
    FrontierRepairOnly = 3,
    BackfillRepair = 4,
    Recovered = 5,
    WaitingForTargetTransport = 6,
    Terminal = 7,
}

internal enum V6TransportHandoffState
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

public sealed record FileTransferV6TransportEpochSnapshot(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    long TransportEpoch,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind SourceTransport,
    FileTransferTransportKind TargetTransport,
    V6TransportEpochState State,
    string Reason,
    bool IsUnresolved)
{
    private sealed class ObservationMetadata
    {
        public TaskCompletionSource<bool> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long RecoveryBoundaryGeneration { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        FileTransferV6TransportEpochSnapshot,
        ObservationMetadata> observationMetadata = new();

    private ObservationMetadata Metadata => observationMetadata.GetValue(
        this,
        static _ => new ObservationMetadata());

    internal long RecoveryBoundaryGeneration
    {
        get => Metadata.RecoveryBoundaryGeneration;
        set => Metadata.RecoveryBoundaryGeneration = value;
    }

    internal Task ObservationCompleted => Metadata.Completion.Task;

    internal void CompleteObservation()
        => Metadata.Completion.TrySetResult(true);
}

public interface IFileTransferV6TransportEpochObserver
{
    void ObserveFileTransferV6TransportEpoch(FileTransferV6TransportEpochSnapshot snapshot);
}

internal sealed class V6TransportEpoch
{
    public RoutePublicationToken? RoutePublicationToken { get; set; }

    public long EpochId { get; init; }

    public long RecoveryBoundaryGeneration { get; set; }

    public FileTransferTransportHandoffKind Kind { get; init; } = FileTransferTransportHandoffKind.RegularNknRecovery;

    public FileTransferTransportKind SourceTransport { get; init; } = FileTransferTransportKind.Unknown;

    public FileTransferTransportKind TargetTransport { get; init; } = FileTransferTransportKind.RegularNkn;

    public FileTransferDirection Direction { get; init; }

    public string Reason { get; init; } = "transport_epoch";

    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastStateChangeUtc { get; set; }

    public DateTimeOffset? LastProbeSentUtc { get; set; }

    public DateTimeOffset? LastProofUtc { get; set; }

    public DateTimeOffset? LastAnnouncedUtc { get; set; }

    public DateTimeOffset? TerminalUtc { get; set; }

    public int StartingCommittedChunkIndex { get; init; }

    public int StartingHighestObservedChunkIndex { get; init; } = -1;

    public int LastObservedCommittedChunkIndex { get; set; }

    public int LastObservedHighestChunkIndex { get; set; } = -1;

    public string? ProbeId { get; set; }

    public string? LastRepairRequestId { get; set; }

    public string? TerminalReason { get; set; }

    public V6TransportEpochState State { get; set; } = V6TransportEpochState.EpochStarting;
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

    public V6TransportHandoffState State { get; set; } = V6TransportHandoffState.TransportProofPending;

    public int DurableProgressSamples { get; set; }

    public int LastObservedCommittedChunkIndex { get; set; }

    public int LastObservedHighestChunkIndex { get; set; } = -1;

    public string? LastRepairRequestId { get; set; }

    public DateTimeOffset? LastStateChangeLogUtc { get; set; }
}
