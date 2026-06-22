namespace NLink.Core.FileTransfer;

public sealed record FileTransferChunkBudgetRequest(
    string TransferId,
    long FileSizeBytes,
    int RequestedChunkSizeBytes,
    int NegotiatedDataProtocolVersion);

public interface IFileTransferChunkBudgetProvider
{
    int ResolveSafeOutboundChunkSize(FileTransferChunkBudgetRequest request);
}

public enum FileTransferTransportProfileKind
{
    Default = 0,
    ConservativeNknStartup = 1,
}

public interface IFileTransferTransportProfileProvider
{
    FileTransferTransportProfileKind FileTransferTransportProfileKind { get; }
}

public interface IFileTransferProtocolCapabilities
{
    bool SupportsFileTransferV6Streaming { get; }
}

public sealed record FileTransferReceiveRecoveryRequest(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    string Reason)
{
    public string? RouteToken { get; init; }

    public int ProtocolVersion { get; init; }

    public int LiveRouteEpoch { get; init; }

    public int TransferLegGeneration { get; init; }

    public int BridgeRecoveryGeneration { get; init; }

    public long TransportEpoch { get; init; }

    public string? CheckpointRequestId { get; init; }

    public string? AuthorityReason { get; init; }
}

public interface IFileTransferReceiveRecoveryController
{
    void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request);
}

public enum FileTransferRecoveryLivenessState
{
    AuthorityActive = 0,
    BridgeRecoveryRequested = 1,
    BridgeRecoveryStarted = 2,
    BridgeRecoveryCompletedAwaitingProof = 3,
    ReceiveProofObserved = 4,
    Exhausted = 5,
    Completed = 6,
}

public sealed record FileTransferRecoveryLivenessSnapshot(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int TransferLegGeneration,
    int BridgeRecoveryGeneration,
    long TransportEpoch,
    string? CheckpointRequestId,
    string AuthorityReason,
    FileTransferRecoveryLivenessState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LivenessDeferralDeadlineUtc,
    bool BridgeRecoveryRequested,
    bool BridgeRecoveryStarted,
    bool BridgeRecoveryCompleted,
    bool ReceiveProofObserved,
    bool RecoveryExhausted,
    bool AuthorityCompleted,
    bool TerminalRecommended);

public interface IFileTransferRecoveryLivenessState
{
    bool TryGetActiveFileTransferRecoveryLivenessSnapshot(
        string sessionId,
        out FileTransferRecoveryLivenessSnapshot snapshot);
}

public sealed record FileTransferRegularV4ControlFeedbackPressure(
    string SessionId,
    string TransferId,
    long CreditExhaustedTimeMs,
    int FrontierLagChunks,
    int PendingRepairCount,
    string Reason);

public interface IFileTransferRegularV4ControlFeedbackPressureObserver
{
    void ObserveRegularV4ControlFeedbackPressure(FileTransferRegularV4ControlFeedbackPressure pressure);
}

public sealed record FileTransferPostTunaFallbackControlPlanePressure(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int TransferLegGeneration,
    int BridgeRecoveryGeneration,
    long TransportEpoch,
    string? CheckpointRequestId,
    string Kind,
    string Reason);

public interface IFileTransferPostTunaFallbackControlPlanePressureObserver
{
    void ObservePostTunaFallbackControlPlanePressure(FileTransferPostTunaFallbackControlPlanePressure pressure);

    void ClearPostTunaFallbackControlPlanePressure(string transferId, string reason);
}

public sealed record FileTransferRouteCompletedNotification(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion);

public interface IFileTransferRouteCompletionObserver
{
    void ObserveFileTransferRouteCompleted(FileTransferRouteCompletedNotification notification);
}

public sealed record FileTransferRouteHintNotification(
    string SessionId,
    string TransferId,
    string RouteToken,
    int ProtocolVersion,
    string Source);

public interface IFileTransferRouteHintObserver
{
    void ObserveFileTransferRouteHint(FileTransferRouteHintNotification notification);
}

internal sealed record RuntimeUnlockRouteCommitSnapshot(
    string SessionId,
    string? TransferId,
    long TransactionGeneration,
    long OfferGeneration,
    bool PeerVisibleProof,
    bool PeerReceived,
    bool AnswerReceived,
    FileTransferRoute TargetRoute,
    int ProtocolVersion,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind TargetTransport,
    string TransactionState,
    string Reason,
    string? PathProbeId = null,
    string PathProbeState = "none",
    FileTransferTransportKind PathProbeTransport = FileTransferTransportKind.Unknown,
    long PathProbeAckedUtcMs = 0,
    string? PathProbeFailureReason = null,
    bool TunaPathLeaseRequired = false,
    long TunaPathLeaseGeneration = 0,
    string TunaPathLeaseState = "none",
    string? TunaPathLeaseListenerRunId = null,
    bool TunaPathLeaseCurrent = false,
    string? TunaPathLeaseFailureReason = null);

internal interface IRuntimeUnlockRouteCommitProofProvider
{
    void NotifyRuntimeUnlockPathProbeStarted(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        string reason);

    void NotifyRuntimeUnlockPathProbeResult(
        string sessionId,
        string transferId,
        long transportEpoch,
        string probeId,
        FileTransferTransportKind targetTransport,
        bool acked,
        string reason);

    bool TryGetRuntimeUnlockRouteCommitProof(
        string sessionId,
        string transferId,
        out RuntimeUnlockRouteCommitSnapshot snapshot);

    void NotifyRuntimeUnlockRouteCommitResult(
        string sessionId,
        string transferId,
        long transactionGeneration,
        long offerGeneration,
        bool accepted,
        string reason);
}

public interface IFileTransferSessionContextProvider
{
    string? CurrentFileTransferSessionId { get; }
}

public interface IFileTransferSignalingTransport
{
    event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
    event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
    event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
    event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
    event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
    event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
    event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;
    event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived;
    event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived;
    event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived;
    event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived;
    event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived;

    Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct);
    Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct);
    Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct);
    Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct);
    Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct);
    Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct);
    Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct);
    Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct);
    Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct);
    Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct);
    Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct);
    Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct);
    Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct);
}

public enum FileTransferControlPlaneKind
{
    Unknown = 0,
    RouteEpoch = 1,
    FallbackCheckpointRequest = 2,
    FallbackCheckpointProof = 3,
    ReceiverState = 4,
    FrontierRequest = 5,
    RuntimeUnlockOffer = 6,
    RuntimeUnlockAnswer = 7,
    RuntimeUnlockAnswerAck = 8,
    TransferCancel = 9,
    SessionEnd = 10,
    LivenessProof = 11,
}

public sealed record FileTransferControlPlaneDeliveryRequest(
    FileTransferControlPlaneKind Kind,
    FileTransferDataFrame Frame,
    string Reason)
{
    public FileTransferDirection? Direction { get; init; }

    public string? RouteToken { get; init; }

    public int ProtocolVersion { get; init; }

    public int LiveRouteEpoch { get; init; }

    public int TransferLegGeneration { get; init; }

    public int BridgeRecoveryGeneration { get; init; }

    public long TransportEpoch { get; init; }

    public string? CheckpointRequestId { get; init; }

    public bool PeerVisibleRequired { get; init; } = true;

    public bool IgnoreCallerCancellation { get; init; }

    public int PeerCopyAttempts { get; init; } = 1;

    public TimeSpan? CopyTimeout { get; init; }
}

public sealed record FileTransferControlPlaneDeliveryResult(
    FileTransferControlPlaneKind Kind,
    string TransferId,
    string SessionId,
    string MessageId,
    bool ControlQueue,
    bool ControlAck,
    bool ControlCopy,
    bool BulkCopy,
    bool PeerVisibleAny,
    bool AcceptedAny,
    string ControlQueueErrorName,
    string ControlAckErrorName,
    string ControlCopyErrorName,
    string BulkCopyErrorName);

public interface IFileTransferControlPlaneDeliveryTransport
{
    Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
        FileTransferControlPlaneDeliveryRequest request,
        CancellationToken ct);
}

public sealed record FileTransferReceivedDataFrame(
    FileTransferDataFrame Frame,
    FileTransferTransportKind TransportKind,
    string Lane,
    DateTimeOffset ReceivedUtc);

public interface IFileTransferDataSession : IDisposable
{
    string SessionId { get; }

    string TransferId { get; }

    bool IsAvailable { get; }

    event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

    ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct);

    ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct);

    Task SendAsync(FileTransferDataFrame frame, CancellationToken ct);
}

public interface IFileTransferRuntimeUnlockPreCommitProbeAckAuthorizer
{
    void AuthorizeRuntimeUnlockPreCommitProbeAck(
        FileTransferRuntimeUnlockPreCommitProbeFrame probe,
        FileTransferTransportKind receivedTransportKind);
}

public sealed class FileTransferDataSessionAvailabilityChangedEventArgs : EventArgs
{
    public FileTransferDataSessionAvailabilityChangedEventArgs(
        bool isAvailable,
        string reason,
        bool requiresResumeRequest,
        FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? "transport_state_changed" : reason.Trim();
        RequiresResumeRequest = requiresResumeRequest;
        HandoffKind = handoffKind;
        TargetTransport = targetTransport;
    }

    public bool IsAvailable { get; }

    public string Reason { get; }

    public bool RequiresResumeRequest { get; }

    public FileTransferTransportHandoffKind HandoffKind { get; }

    public FileTransferTransportKind TargetTransport { get; }
}

public sealed class FileTransferOfferReceivedEventArgs : EventArgs
{
    public FileTransferOfferReceivedEventArgs(FileTransferOfferV2 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferOfferV2 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferAcceptReceivedEventArgs : EventArgs
{
    public FileTransferAcceptReceivedEventArgs(FileTransferAcceptV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferAcceptV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferDeclineReceivedEventArgs : EventArgs
{
    public FileTransferDeclineReceivedEventArgs(FileTransferDeclineV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferDeclineV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferCancelReceivedEventArgs : EventArgs
{
    public FileTransferCancelReceivedEventArgs(FileTransferCancelV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferCancelV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferSessionOpenReceivedEventArgs : EventArgs
{
    public FileTransferSessionOpenReceivedEventArgs(FileTransferSessionOpenV2 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferSessionOpenV2 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferErrorReceivedEventArgs : EventArgs
{
    public FileTransferErrorReceivedEventArgs(FileTransferErrorV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferErrorV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferCompleteReceivedEventArgs : EventArgs
{
    public FileTransferCompleteReceivedEventArgs(FileTransferCompleteV1 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferCompleteV1 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferPauseControlReceivedEventArgs : EventArgs
{
    public FileTransferPauseControlReceivedEventArgs(FileTransferPauseControlV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferPauseControlV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferHeartbeatReceivedEventArgs : EventArgs
{
    public FileTransferHeartbeatReceivedEventArgs(FileTransferHeartbeatV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferHeartbeatV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferTransportEpochReceivedEventArgs : EventArgs
{
    public FileTransferTransportEpochReceivedEventArgs(FileTransferTransportEpochV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferTransportEpochV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferTransportProbeReceivedEventArgs : EventArgs
{
    public FileTransferTransportProbeReceivedEventArgs(FileTransferTransportProbeV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferTransportProbeV6 Message { get; }

    public string? PeerId { get; }
}

public sealed class FileTransferRepairProofReceivedEventArgs : EventArgs
{
    public FileTransferRepairProofReceivedEventArgs(FileTransferRepairProofV6 message, string? peerId)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        PeerId = string.IsNullOrWhiteSpace(peerId) ? null : peerId.Trim();
    }

    public FileTransferRepairProofV6 Message { get; }

    public string? PeerId { get; }
}
