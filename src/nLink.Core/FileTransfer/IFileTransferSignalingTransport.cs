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
    bool SupportsFileTransferV5Streaming { get; }
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

    Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct);
    Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct);
    Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct);
    Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct);
    Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct);
    Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct);
    Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct);
    Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct);
}

public interface IFileTransferDataSession : IDisposable
{
    string SessionId { get; }

    string TransferId { get; }

    bool IsAvailable { get; }

    event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

    ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct);

    Task SendAsync(FileTransferDataFrame frame, CancellationToken ct);
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
