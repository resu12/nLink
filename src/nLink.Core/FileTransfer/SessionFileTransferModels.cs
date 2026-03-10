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
    AwaitingStart = 4,
    Sending = 5,
    AwaitingCompletion = 6,
    Receiving = 7,
    Verifying = 8,
    Completed = 9,
    Declined = 10,
    Canceled = 11,
    Failed = 12,
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
    string Sha256Base64);

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
    string? SavedFileName = null)
{
    public bool IsTerminal
        => State is FileTransferTransferState.Completed or
            FileTransferTransferState.Declined or
            FileTransferTransferState.Canceled or
            FileTransferTransferState.Failed;

    public double ProgressFraction
        => FileSizeBytes <= 0
            ? 0d
            : Math.Clamp((double)BytesTransferred / FileSizeBytes, 0d, 1d);
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
