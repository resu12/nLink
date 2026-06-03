using System.IO;

namespace NLink.Core.FileTransfer;

public delegate Task<Stream> FileTransferReadStreamFactory(CancellationToken ct);

public delegate Task<Stream> FileTransferWriteStreamFactory(FileTransferIncomingOffer offer, CancellationToken ct);

public delegate Task<FileTransferReceiveDestination> FileTransferWriteDestinationFactory(FileTransferIncomingOffer offer, CancellationToken ct);

public sealed class FileTransferReceiveDestination : IDisposable, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task>? finalizeAsync;
    private readonly Action? dispose;
    private readonly Func<ValueTask>? disposeAsync;
    private bool finalized;

    public FileTransferReceiveDestination(
        Stream stream,
        Func<CancellationToken, Task>? finalizeAsync = null,
        Action? dispose = null,
        Func<ValueTask>? disposeAsync = null,
        string? finalPath = null,
        string? safeFileName = null)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.finalizeAsync = finalizeAsync;
        this.dispose = dispose;
        this.disposeAsync = disposeAsync;
        FinalPath = string.IsNullOrWhiteSpace(finalPath) ? null : finalPath;
        SafeFileName = string.IsNullOrWhiteSpace(safeFileName) ? null : safeFileName;
    }

    public Stream Stream { get; }

    public bool IsFinalized => finalized;

    public string? FinalPath { get; }

    public string? SafeFileName { get; }

    public string? DirectoryPath
        => string.IsNullOrWhiteSpace(FinalPath)
            ? null
            : Path.GetDirectoryName(FinalPath);

    public static FileTransferReceiveDestination FromStream(Stream stream)
        => new(stream);

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (finalized)
        {
            return;
        }

        if (finalizeAsync is not null)
        {
            await finalizeAsync(ct).ConfigureAwait(false);
        }

        finalized = true;
    }

    public void Dispose()
    {
        if (dispose is not null)
        {
            dispose();
            return;
        }

        Stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposeAsync is not null)
        {
            await disposeAsync().ConfigureAwait(false);
            return;
        }

        if (dispose is not null)
        {
            dispose();
            return;
        }

        await Stream.DisposeAsync().ConfigureAwait(false);
    }
}

public enum FileTransferDirection
{
    Outbound = 0,
    Inbound = 1,
}

public enum FileTransferTransferState
{
    Idle = 0,
    Offering = 1,
    AwaitingAcceptance = 2,
    PendingDecision = 3,
    AwaitingMetadata = 4,
    PreparingMetadata = 5,
    AwaitingStart = 6,
    Sending = 7,
    AwaitingCompletion = 8,
    Receiving = 9,
    Verifying = 10,
    Completed = 11,
    Declined = 12,
    Canceled = 13,
    Failed = 14,
}

internal enum FileTransferFlowControlMode
{
    InteractiveCritical = 0,
    Interactive = 1,
    Background = 2,
}

internal readonly record struct FileTransferFlowControlPolicy(
    FileTransferFlowControlMode Mode,
    int GrantChunks,
    int LowWatermarkChunks,
    int StartupGrantChunks)
{
    public static FileTransferFlowControlPolicy ForMode(FileTransferFlowControlMode mode)
    {
        return mode switch
        {
            FileTransferFlowControlMode.InteractiveCritical => new(mode, GrantChunks: 24, LowWatermarkChunks: 8, StartupGrantChunks: 24),
            FileTransferFlowControlMode.Interactive => new(mode, GrantChunks: 96, LowWatermarkChunks: 32, StartupGrantChunks: 64),
            _ => new(FileTransferFlowControlMode.Background, GrantChunks: 192, LowWatermarkChunks: 64, StartupGrantChunks: 128),
        };
    }
}

public sealed record FileTransferSendDescriptor(
    string FileName,
    long FileSizeBytes,
    string? TransferId = null,
    int? ChunkSizeBytes = null);

public sealed record FileTransferIncomingOffer(
    string SessionId,
    string TransferId,
    string FileName,
    long FileSizeBytes,
    string? Sha256Base64);

public sealed record FileTransferTransferSnapshot(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferTransferState State,
    string FileName,
    long FileSizeBytes,
    string? Sha256Base64,
    long BytesTransferred,
    int ChunksTransferred,
    int ChunkCount,
    int ChunkSizeBytes,
    string? ErrorCode,
    string? StatusMessage,
    string? SavedFilePath = null,
    string? SavedDirectoryPath = null,
    string? SavedFileName = null,
    long? BytesAcceptedForTransport = null,
    long? BytesAcknowledgedByReceiver = null,
    bool IsPaused = false,
    string? PauseReason = null,
    bool IsPeerPaused = false,
    string? PeerPauseReason = null,
    string? RouteToken = null,
    int? ProtocolVersion = null)
{
    public bool IsTerminal
        => State is FileTransferTransferState.Completed or
            FileTransferTransferState.Declined or
            FileTransferTransferState.Canceled or
            FileTransferTransferState.Failed;

    public long ProgressBytes
    {
        get
        {
            var visible = Direction == FileTransferDirection.Outbound
                ? BytesAcknowledgedByReceiver ?? BytesTransferred
                : BytesAcceptedForTransport ?? BytesTransferred;
            return Math.Clamp(visible, 0L, Math.Max(0L, FileSizeBytes));
        }
    }

    public double ProgressFraction
        => FileSizeBytes <= 0
            ? 0d
            : Math.Clamp((double)ProgressBytes / FileSizeBytes, 0d, 1d);
}

public sealed record SessionFileTransferSnapshot(
    FileTransferTransferSnapshot? Outbound,
    FileTransferTransferSnapshot? Inbound)
{
    public FileTransferTransferState OutboundState
        => Outbound?.State ?? FileTransferTransferState.Idle;

    public FileTransferTransferState InboundState
        => Inbound?.State ?? FileTransferTransferState.Idle;
}

public sealed class SessionFileTransferSnapshotChangedEventArgs : EventArgs
{
    public SessionFileTransferSnapshotChangedEventArgs(SessionFileTransferSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public SessionFileTransferSnapshot Snapshot { get; }
}
