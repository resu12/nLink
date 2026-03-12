using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed class SessionFileTransferService : IDisposable
{
    private const int DefaultOutboundChunkWindow = 8;
    private const int Stage1InitialOutboundChunkGrant = 48;
    private const string BusyReason = FileTransferResultCodes.Busy;
    private const string DeclinedReason = FileTransferResultCodes.Declined;
    private const string CanceledReason = FileTransferResultCodes.CanceledLocal;
    private const string DisconnectedErrorCode = FileTransferResultCodes.TransportDisconnected;
    private const string DetachedErrorCode = FileTransferResultCodes.TransportDetached;
    private const string InvalidStateErrorCode = FileTransferResultCodes.InvalidState;
    private const string SessionMismatchErrorCode = FileTransferResultCodes.SessionMismatch;
    private const string FileSizeMismatchErrorCode = FileTransferResultCodes.SizeMismatch;
    private const string HashMismatchErrorCode = FileTransferResultCodes.IntegrityMismatch;
    private const string StreamOpenFailedErrorCode = FileTransferResultCodes.WriteOpenFailed;
    private const string StreamReadFailedErrorCode = FileTransferResultCodes.ReadFailed;
    private const string PayloadBudgetExceededErrorCode = FileTransferResultCodes.PayloadBudgetExceeded;
    private const string StreamWriteFailedErrorCode = FileTransferResultCodes.WriteFailed;
    private const string FinalizeFailedErrorCode = FileTransferResultCodes.FinalizeFailed;
    private const string WindowTimeoutErrorCode = FileTransferResultCodes.WindowTimeout;
    private static readonly TimeSpan OutboundWindowUpdateTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WindowUpdateRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WindowUpdateIdleRefreshDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CriticalBulkClampDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MissingRangeRepairThreshold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MissingRangeRepairCooldown = TimeSpan.FromMilliseconds(500);
    private const long MissingRangeBufferPressureBytes = 512L * 1024;
    private const long MissingRangeSeverePressureBytes = 1024L * 1024;
    private const int InitialRepairRangeChunks = 8;
    private const int EscalatedRepairRangeChunks = 16;

    private readonly object gate = new();
    private readonly object inboundDispatchGate = new();
    private readonly Func<string> transferIdFactory;
    private IFileTransferSignalingTransport? transport;
    private ISignalingTransport? transportLifecycle;
    private OutboundTransferContext? outboundTransfer;
    private InboundTransferContext? inboundTransfer;
    private FileTransferFlowControlPolicy currentFlowControlPolicy = FileTransferFlowControlPolicy.InteractiveDefault;
    private Task inboundDispatchTail = Task.CompletedTask;
    private bool disposed;
    private long windowUpdatesSent;
    private long windowUpdatesReceived;
    private long windowUpdateSendFailures;
    private long windowUpdateCoalesced;
    private long windowUpdateRefreshResends;
    private long windowUpdateSuppressedIdentical;
    private long windowUpdateSuppressedSmallDelta;
    private long windowUpdateSuppressedNoExtension;
    private long windowUpdateSuppressedTail;
    private long startupWindowRefreshSent;
    private long lowWatermarkWindowSends;
    private long missingRangeRequestsSent;
    private long missingRangeRequestsReceived;
    private long missingRangeSendFailures;
    private long missingRangeSuppressedCooldown;
    private long repairTriggerByTimeCount;
    private long repairTriggerByBufferPressureCount;
    private long repairTriggerBySeverePressureCount;
    private long missingRangeEscalatedCount;
    private long bulkClampEnteredCount;
    private long bulkClampReleasedCount;
    private int startupPolicyMode = (int)FileTransferFlowControlMode.Interactive;
    private int maxGrantedUntilExclusive;
    private int initialGrantedUntilExclusive;
    private long bulkClampTotalMs;
    private long maxBufferedOutOfOrderBytes;
    private long duplicateChunksReceived;
    private long repairChunksResent;
    private int senderWaitingForWindow;
    private long totalWindowWaitMs;
    private long windowTimeoutCount;
    private string? lastWindowUpdateSendError;
    private string? lastRepairRange;
    private long lastRepairLatencyMs = -1;

    public SessionFileTransferService(Func<string>? transferIdFactory = null)
    {
        this.transferIdFactory = transferIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public event EventHandler<SessionFileTransferSnapshotChangedEventArgs>? TransferChanged;

    public SessionFileTransferSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    public FileTransferFlowControlDiagnosticsSnapshot GetFlowControlDiagnosticsSnapshot()
    {
        FileTransferFlowControlPolicy policy;
        lock (gate)
        {
            policy = currentFlowControlPolicy;
        }

        return new(
            FlowMode: policy.Mode,
            StartupPolicyMode: (FileTransferFlowControlMode)Volatile.Read(ref startupPolicyMode),
            TargetOutstandingBytes: policy.TargetOutstandingBytes,
            ReorderSlackBytes: policy.ReorderSlackBytes,
            HardOutstandingCapBytes: policy.HardOutstandingCapBytes,
            WindowUpdatesSent: Interlocked.Read(ref windowUpdatesSent),
            WindowUpdatesReceived: Interlocked.Read(ref windowUpdatesReceived),
            WindowUpdateSendFailures: Interlocked.Read(ref windowUpdateSendFailures),
            WindowUpdateCoalesced: Interlocked.Read(ref windowUpdateCoalesced),
            WindowUpdateRefreshResends: Interlocked.Read(ref windowUpdateRefreshResends),
            WindowUpdateSuppressedIdentical: Interlocked.Read(ref windowUpdateSuppressedIdentical),
            WindowUpdateSuppressedSmallDelta: Interlocked.Read(ref windowUpdateSuppressedSmallDelta),
            WindowUpdateSuppressedNoExtension: Interlocked.Read(ref windowUpdateSuppressedNoExtension),
            WindowUpdateSuppressedTail: Interlocked.Read(ref windowUpdateSuppressedTail),
            MissingRangeRequestsSent: Interlocked.Read(ref missingRangeRequestsSent),
            MissingRangeRequestsReceived: Interlocked.Read(ref missingRangeRequestsReceived),
            MissingRangeSendFailures: Interlocked.Read(ref missingRangeSendFailures),
            MissingRangeSuppressedCooldown: Interlocked.Read(ref missingRangeSuppressedCooldown),
            RepairTriggerByTimeCount: Interlocked.Read(ref repairTriggerByTimeCount),
            RepairTriggerByBufferPressureCount: Interlocked.Read(ref repairTriggerByBufferPressureCount),
            RepairTriggerBySeverePressureCount: Interlocked.Read(ref repairTriggerBySeverePressureCount),
            MissingRangeEscalatedCount: Interlocked.Read(ref missingRangeEscalatedCount),
            BulkClampEnteredCount: Interlocked.Read(ref bulkClampEnteredCount),
            BulkClampReleasedCount: Interlocked.Read(ref bulkClampReleasedCount),
            MaxGrantedUntilExclusive: Volatile.Read(ref maxGrantedUntilExclusive),
            InitialGrantedUntilExclusive: Volatile.Read(ref initialGrantedUntilExclusive),
            AdvertisedGrantedUntilExclusive: CaptureAdvertisedGrantedUntilExclusive(),
            RemoteGrantedUntilExclusive: CaptureRemoteGrantedUntilExclusive(),
            LastWindowSentNextExpectedChunkIndex: CaptureLastWindowSentNextExpectedChunkIndex(),
            LastWindowSentGrantedUntilExclusive: CaptureLastWindowSentGrantedUntilExclusive(),
            DesiredWindowGrantedUntilExclusive: CaptureDesiredWindowGrantedUntilExclusive(),
            RemainingAdvertisedRunwayChunks: CaptureRemainingAdvertisedRunwayChunks(),
            BufferedOutOfOrderChunks: CaptureBufferedOutOfOrderChunks(),
            BufferedOutOfOrderBytes: CaptureBufferedOutOfOrderBytes(),
            MaxBufferedOutOfOrderBytes: Interlocked.Read(ref maxBufferedOutOfOrderBytes),
            OldestGapChunkIndex: CaptureOldestGapChunkIndex(),
            OldestGapAgeMs: CaptureOldestGapAgeMs(),
            DuplicateChunksReceived: Interlocked.Read(ref duplicateChunksReceived),
            StartupWindowRefreshSent: Interlocked.Read(ref startupWindowRefreshSent),
            LowWatermarkWindowSends: Interlocked.Read(ref lowWatermarkWindowSends),
            BulkClampActive: CaptureBulkClampActive(),
            BulkClampTotalMs: Interlocked.Read(ref bulkClampTotalMs),
            LastWindowUpdateSendError: Volatile.Read(ref lastWindowUpdateSendError),
            LastRepairRange: CaptureLastRepairRange(),
            LastRepairLatencyMs: CaptureLastRepairLatencyMs(),
            RepairChunksResent: Interlocked.Read(ref repairChunksResent),
            SenderWaitingForWindow: Volatile.Read(ref senderWaitingForWindow) != 0,
            TotalWindowWaitMs: Interlocked.Read(ref totalWindowWaitMs),
            WindowTimeoutCount: Interlocked.Read(ref windowTimeoutCount));
    }

    public void SetFlowControlPolicy(FileTransferFlowControlPolicy policy)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var normalizedPolicy = FileTransferFlowControlPolicy.Normalize(policy);
        InboundTransferContext? inboundContextToRefresh = null;
        IFileTransferSignalingTransport? currentTransport = null;
        OutboundTransferContext? outboundContextToSignal = null;
        FileTransferFlowControlPolicy previousPolicy;
        bool policyChanged;
        bool enteredBulkClamp = false;
        bool releasedBulkClamp = false;

        lock (gate)
        {
            previousPolicy = currentFlowControlPolicy;
            policyChanged = previousPolicy != normalizedPolicy;
            if (!policyChanged)
            {
                return;
            }

            currentFlowControlPolicy = normalizedPolicy;
            currentTransport = transport;

            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                outboundTransfer.FlowControlPolicy = normalizedPolicy;
                outboundContextToSignal = outboundTransfer;
                if (normalizedPolicy.Mode == FileTransferFlowControlMode.InteractiveCritical)
                {
                    enteredBulkClamp = EnterOutboundBulkClampLocked(outboundTransfer);
                }
                else
                {
                    releasedBulkClamp = ReleaseOutboundBulkClampLocked(outboundTransfer);
                }
            }

            if (inboundTransfer is not null && !inboundTransfer.IsTerminal)
            {
                inboundTransfer.FlowControlPolicy = normalizedPolicy;
                inboundContextToRefresh = inboundTransfer;
            }
        }

        if (currentTransport is IFileTransferFlowControlPolicyAwareTransport policyAwareTransport)
        {
            policyAwareTransport.SetFileTransferFlowControlPolicy(normalizedPolicy);
        }

        SignalOutboundBulkClampState(outboundContextToSignal);

        if (enteredBulkClamp &&
            previousPolicy.Mode != FileTransferFlowControlMode.InteractiveCritical &&
            outboundContextToSignal is not null)
        {
            RecordBulkClampEntered(outboundContextToSignal);
        }
        else if (releasedBulkClamp &&
                 previousPolicy.Mode == FileTransferFlowControlMode.InteractiveCritical &&
                 outboundContextToSignal is not null)
        {
            RecordBulkClampReleased(outboundContextToSignal);
        }

        if (inboundContextToRefresh is not null)
        {
            _ = RefreshInboundWindowAfterPolicyChangeAsync(inboundContextToRefresh);
        }
    }

    public void AttachTransport(IFileTransferSignalingTransport transport)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(transport);

        if (ReferenceEquals(this.transport, transport))
        {
            return;
        }

        DetachTransportCore(markActiveTransfersFailed: true, failureCode: DetachedErrorCode, failureMessage: "Transport was detached.");

        this.transport = transport;
        transport.FileTransferOfferReceived += OnFileTransferOfferReceived;
        transport.FileTransferAcceptReceived += OnFileTransferAcceptReceived;
        transport.FileTransferDeclineReceived += OnFileTransferDeclineReceived;
        transport.FileTransferStartReceived += OnFileTransferStartReceived;
        transport.FileTransferChunkReceived += OnFileTransferChunkReceived;
        transport.FileTransferWindowUpdateReceived += OnFileTransferWindowUpdateReceived;
        transport.FileTransferMissingRangeReceived += OnFileTransferMissingRangeReceived;
        transport.FileTransferCancelReceived += OnFileTransferCancelReceived;
        transport.FileTransferErrorReceived += OnFileTransferErrorReceived;
        transport.FileTransferCompleteReceived += OnFileTransferCompleteReceived;

        if (transport is ISignalingTransport lifecycleTransport)
        {
            transportLifecycle = lifecycleTransport;
            lifecycleTransport.Rejected += OnTransportRejectedOrDisconnected;
            lifecycleTransport.Disconnected += OnTransportRejectedOrDisconnected;
        }

        if (transport is IFileTransferFlowControlPolicyAwareTransport policyAwareTransport)
        {
            policyAwareTransport.SetFileTransferFlowControlPolicy(currentFlowControlPolicy);
        }

        RaiseTransferChanged(CreateSnapshot());
    }

    public void DetachTransport()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        DetachTransportCore(markActiveTransfersFailed: true, failureCode: DetachedErrorCode, failureMessage: "Transport was detached.");
    }

    public async Task<FileTransferTransferSnapshot?> TryStartSendAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(openReadStreamAsync);

        var normalizedDescriptor = NormalizeSendDescriptor(descriptor, transferIdFactory);
        OutboundTransferContext context;
        var enteredBulkClamp = false;

        lock (gate)
        {
            if (transport is null)
            {
                return null;
            }

            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                return null;
            }

            context = new OutboundTransferContext(normalizedDescriptor, openReadStreamAsync, currentFlowControlPolicy);
            outboundTransfer = context;
            if (currentFlowControlPolicy.Mode == FileTransferFlowControlMode.InteractiveCritical)
            {
                enteredBulkClamp = EnterOutboundBulkClampLocked(context);
            }
        }

        if (enteredBulkClamp)
        {
            RecordBulkClampEntered(context);
        }

        RaiseTransferChanged(CreateSnapshot());

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.LifetimeCts.Token);

        try
        {
            var prepared = await PrepareOutboundTransferAsync(context, linkedCts.Token).ConfigureAwait(false);
            if (!prepared)
            {
                return CaptureCurrentOutboundSnapshot();
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: "Transfer start canceled.",
                notifyPeer: false,
                cancelReason: CanceledReason,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }
        catch (Exception ex)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamReadFailedErrorCode,
                statusMessage: ex.Message,
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }

        var offerMessage = new FileTransferOfferV1
        {
            SessionId = string.Empty,
            TransferId = context.TransferId,
            FileName = context.FileName,
            FileSizeBytes = context.FileSizeBytes,
            Sha256Base64 = context.Sha256Base64!,
        };

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferOfferAsync(offerMessage, linkedCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "offer_sent",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes);
            return CaptureCurrentOutboundSnapshot();
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested || ct.IsCancellationRequested)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: "Transfer offer canceled.",
                notifyPeer: false,
                cancelReason: CanceledReason,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }
        catch (Exception ex)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }
    }

    public async Task<FileTransferTransferSnapshot?> AcceptIncomingTransferAsync(
        string transferId,
        FileTransferWriteStreamFactory openWriteStreamAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openWriteStreamAsync);

        return await AcceptIncomingTransferAsync(
                transferId,
                async (offer, linkedCt) =>
                {
                    var stream = await openWriteStreamAsync(offer, linkedCt).ConfigureAwait(false);
                    return FileTransferReceiveDestination.FromStream(stream);
                },
                ct)
            .ConfigureAwait(false);
    }

    public async Task<FileTransferTransferSnapshot?> AcceptIncomingTransferAsync(
        string transferId,
        FileTransferWriteDestinationFactory openWriteDestinationAsync,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(openWriteDestinationAsync);

        InboundTransferContext? context;
        FileTransferIncomingOffer offer;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision ||
                context.AcceptInProgress ||
                !string.Equals(context.TransferId, NormalizeTransferId(transferId), StringComparison.Ordinal))
            {
                return null;
            }

            context.AcceptInProgress = true;
            offer = context.CreateOffer();
        }

        FileTransferReceiveDestination? destination = null;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.LifetimeCts.Token);
            destination = await openWriteDestinationAsync(offer, linkedCts.Token).ConfigureAwait(false);
            var stream = destination.Stream;
            ValidateWritableStream(stream);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || context.LifetimeCts.IsCancellationRequested)
        {
            SetInboundAcceptInProgress(context, false);
            return CaptureCurrentInboundSnapshot();
        }
        catch (Exception ex)
        {
            SetInboundAcceptInProgress(context, false);
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamOpenFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not open the destination stream.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            destination?.Dispose();
            return CaptureCurrentInboundSnapshot();
        }

        SessionFileTransferSnapshot snapshot;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision)
            {
                context.AcceptInProgress = false;
                destination.Dispose();
                return CaptureCurrentInboundSnapshot();
            }

            context.WriteDestination = destination;
            context.WriteStream = destination.Stream;
            context.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            context.AcceptInProgress = false;
            context.State = FileTransferTransferState.AwaitingStart;
            context.StatusMessage = "Waiting for sender to start.";
            snapshot = CreateSnapshotLocked();
        }

        RaiseTransferChanged(snapshot);

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                },
                ct).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send the accept response.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }
    }

    public async Task<FileTransferTransferSnapshot?> DeclineIncomingTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? context;
        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.PendingDecision ||
                !string.Equals(context.TransferId, NormalizeTransferId(transferId), StringComparison.Ordinal))
            {
                return null;
            }
        }

        await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Declined,
                errorCode: NormalizeReason(reason) == FileTransferResultCodes.Busy ? FileTransferResultCodes.Busy : null,
                statusMessage: NormalizeReason(reason) ?? "Transfer declined.",
                sendError: false,
                errorMessage: null,
            cancelReason: NormalizeReason(reason) ?? DeclinedReason,
            ct: ct).ConfigureAwait(false);
        return CaptureCurrentInboundSnapshot();
    }

    public async Task<FileTransferTransferSnapshot?> CancelTransferAsync(
        string transferId,
        string? reason,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalizedTransferId = NormalizeTransferId(transferId);

        OutboundTransferContext? outboundContext;
        InboundTransferContext? inboundContext;
        lock (gate)
        {
            outboundContext = outboundTransfer is not null &&
                              !outboundTransfer.IsTerminal &&
                              string.Equals(outboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inboundContext = inboundTransfer is not null &&
                             !inboundTransfer.IsTerminal &&
                             string.Equals(inboundTransfer.TransferId, normalizedTransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outboundContext is not null)
        {
            await TransitionOutboundToTerminalAsync(
                outboundContext,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: NormalizeReason(reason) ?? "Transfer canceled.",
                notifyPeer: true,
                cancelReason: NormalizeReason(reason) ?? CanceledReason,
                ct: ct).ConfigureAwait(false);
            return CaptureCurrentOutboundSnapshot();
        }

        if (inboundContext is not null)
        {
            await TransitionInboundToTerminalAsync(
                inboundContext,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledLocal,
                statusMessage: NormalizeReason(reason) ?? "Transfer canceled.",
                sendError: false,
                errorMessage: null,
                cancelReason: NormalizeReason(reason) ?? CanceledReason,
                ct: ct).ConfigureAwait(false);
            return CaptureCurrentInboundSnapshot();
        }

        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DetachTransportCore(markActiveTransfersFailed: false, failureCode: DetachedErrorCode, failureMessage: "Service disposed.");

        InboundTransferContext? inbound;
        OutboundTransferContext? outbound;
        lock (gate)
        {
            inbound = inboundTransfer;
            outbound = outboundTransfer;
            inboundTransfer = null;
            outboundTransfer = null;
        }

        inbound?.DisposeResources();
        outbound?.DisposeResources();
    }

    private async Task<bool> PrepareOutboundTransferAsync(OutboundTransferContext context, CancellationToken ct)
    {
        using var stream = await context.OpenReadStreamAsync(ct).ConfigureAwait(false);
        ValidateReadableStream(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(context.ChunkSizeBytes, 4096, 64 * 1024));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesReadTotal = 0;
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
                bytesReadTotal += bytesRead;
                if (bytesReadTotal > context.FileSizeBytes)
                {
                    throw new InvalidOperationException("Source stream length exceeded the declared file size.");
                }
            }

            if (bytesReadTotal != context.FileSizeBytes)
            {
                throw new InvalidOperationException("Source stream length did not match the declared file size.");
            }

            var hashBytes = hash.GetHashAndReset();
            SessionFileTransferSnapshot snapshot;
            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    return false;
                }

                context.ChunkSizeBytes = ResolveSafeOutboundChunkSize(context, transport);
                context.Sha256Base64 = Convert.ToBase64String(hashBytes);
                context.ChunkCount = checked((int)((context.FileSizeBytes + context.ChunkSizeBytes - 1) / context.ChunkSizeBytes));
                context.State = FileTransferTransferState.AwaitingAcceptance;
                context.StatusMessage = "Waiting for receiver acceptance.";
                snapshot = CreateSnapshotLocked();
            }

            LogTransferInfo(
                "offer_prepared",
                FileTransferDirection.Outbound,
                context.TransferId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                reason: $"chunk_count={context.ChunkCount}; chunk_size_bytes={context.ChunkSizeBytes}");
            RaiseTransferChanged(snapshot);
            return true;
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async void OnFileTransferOfferReceived(object? sender, FileTransferOfferReceivedEventArgs e)
        => EnqueueInboundDispatch("offer", () => HandleIncomingOfferAsync(e.Message));

    private async void OnFileTransferAcceptReceived(object? sender, FileTransferAcceptReceivedEventArgs e)
        => EnqueueInboundDispatch("accept", () => HandleIncomingAcceptAsync(e.Message));

    private async void OnFileTransferDeclineReceived(object? sender, FileTransferDeclineReceivedEventArgs e)
        => EnqueueInboundDispatch("decline", () => HandleIncomingDeclineAsync(e.Message));

    private async void OnFileTransferStartReceived(object? sender, FileTransferStartReceivedEventArgs e)
        => EnqueueInboundDispatch("start", () => HandleIncomingStartAsync(e.Message));

    private async void OnFileTransferChunkReceived(object? sender, FileTransferChunkReceivedEventArgs e)
        => EnqueueInboundDispatch("chunk", () => HandleIncomingChunkAsync(e.Message));

    private async void OnFileTransferWindowUpdateReceived(object? sender, FileTransferWindowUpdateReceivedEventArgs e)
        => EnqueueInboundDispatch("window update", () => HandleIncomingWindowUpdateAsync(e.Message));

    private async void OnFileTransferMissingRangeReceived(object? sender, FileTransferMissingRangeReceivedEventArgs e)
        => EnqueueInboundDispatch("missing range", () => HandleIncomingMissingRangeAsync(e.Message));

    private async void OnFileTransferCancelReceived(object? sender, FileTransferCancelReceivedEventArgs e)
        => DispatchPriorityInbound("cancel", () => HandleIncomingCancelAsync(e.Message));

    private async void OnFileTransferErrorReceived(object? sender, FileTransferErrorReceivedEventArgs e)
        => EnqueueInboundDispatch("error", () => HandleIncomingErrorAsync(e.Message));

    private async void OnFileTransferCompleteReceived(object? sender, FileTransferCompleteReceivedEventArgs e)
        => EnqueueInboundDispatch("complete", () => HandleIncomingCompleteAsync(e.Message));

    private async void OnTransportRejectedOrDisconnected(object? sender, EventArgs e)
        => EnqueueInboundDispatch("transport disconnect", HandleTransportRejectedOrDisconnectedAsync);

    private void EnqueueInboundDispatch(string operation, Func<Task> work)
    {
        lock (inboundDispatchGate)
        {
            inboundDispatchTail = inboundDispatchTail
                .ContinueWith(
                    static (_, state) => ((InboundDispatchWork)state!).Service.RunInboundDispatchAsync(
                        ((InboundDispatchWork)state!).Operation,
                        ((InboundDispatchWork)state!).Work),
                    new InboundDispatchWork(this, operation, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    // Cancel is terminal and should not sit behind a long backlog of chunk handlers.
    private void DispatchPriorityInbound(string operation, Func<Task> work)
        => _ = Task.Run(() => RunInboundDispatchAsync(operation, work));

    private async Task RunInboundDispatchAsync(string operation, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn($"{operation} handler failed: {ex.Message}");
        }
    }

    private async Task HandleTransportRejectedOrDisconnectedAsync()
    {
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
        }

        if (outbound is not null)
        {
            await TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Transport disconnected.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }

        if (inbound is not null)
        {
            await TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Failed,
                errorCode: DisconnectedErrorCode,
                statusMessage: "Transport disconnected.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingOfferAsync(FileTransferOfferV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? busyTransfer = null;
        SessionFileTransferSnapshot? snapshotToRaise = null;
        var incoming = new InboundTransferContext(message, currentFlowControlPolicy);

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (inboundTransfer is not null && !inboundTransfer.IsTerminal)
            {
                busyTransfer = inboundTransfer;
            }
            else
            {
                inboundTransfer = incoming;
                snapshotToRaise = CreateSnapshotLocked();
            }
        }

        if (snapshotToRaise is not null)
        {
            LogTransferInfo(
                "offer_received",
                FileTransferDirection.Inbound,
                incoming.TransferId,
                sessionId: incoming.SessionId,
                fileName: incoming.FileName,
                fileSizeBytes: incoming.FileSizeBytes);
            RaiseTransferChanged(snapshotToRaise);
            return;
        }

        if (busyTransfer is not null)
        {
            await SendDeclineAsync(message.SessionId, message.TransferId, BusyReason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task HandleIncomingAcceptAsync(FileTransferAcceptV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.AwaitingAcceptance ||
                context.SendStarted ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            context.SendStarted = true;
            context.SessionId = message.SessionId;
        }

        LogTransferInfo(
            "accept_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId);
        _ = RunOutboundSendLoopAsync(context);
        return Task.CompletedTask;
    }

    private Task HandleIncomingDeclineAsync(FileTransferDeclineV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }
        }

        LogTransferInfo(
            "decline_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId,
            reason: NormalizeReason(message.Reason));
        return TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Declined,
            errorCode: NormalizeReason(message.Reason) == FileTransferResultCodes.Busy ? FileTransferResultCodes.Busy : null,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer declined.",
                notifyPeer: false,
                cancelReason: null,
            ct: CancellationToken.None);
    }

    private async Task HandleIncomingStartAsync(FileTransferStartV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? context;
        SessionFileTransferSnapshot? snapshot = null;
        string? terminalErrorCode = null;
        string? terminalStatus = null;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.State != FileTransferTransferState.AwaitingStart)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start message arrived in an invalid state.";
            }
            else if (!string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal) ||
                     !string.Equals(context.FileName, message.FileName, StringComparison.Ordinal) ||
                     context.FileSizeBytes != message.FileSizeBytes ||
                     !string.Equals(context.Sha256Base64, message.Sha256Base64, StringComparison.Ordinal))
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start metadata did not match the original offer.";
            }
            else if (message.ChunkCount <= 0 || message.ChunkSizeBytes <= 0)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start metadata was invalid.";
            }
            else if (!TryCalculateExpectedChunkCount(context.FileSizeBytes, message.ChunkSizeBytes, out var expectedChunkCount) ||
                     message.ChunkCount != expectedChunkCount)
            {
                terminalErrorCode = InvalidStateErrorCode;
                terminalStatus = "Start chunk metadata did not match the declared file size.";
            }
            else if (context.WriteStream is null || context.Hash is null)
            {
                terminalErrorCode = StreamOpenFailedErrorCode;
                terminalStatus = "Destination stream was not prepared before accept.";
            }
            else
            {
                context.ChunkCount = message.ChunkCount;
                context.ChunkSizeBytes = message.ChunkSizeBytes;
                context.NextChunkIndex = 0;
                context.PendingChunks.Clear();
                context.BufferedOutOfOrderBytes = 0;
                context.HighestBufferedChunkIndex = -1;
                context.BytesTransferred = 0;
                context.ChunksTransferred = 0;
                context.FlowControlPolicy = currentFlowControlPolicy;
                context.StartupPolicyMode = currentFlowControlPolicy.Mode;
                context.HasAdvertisedWindowUpdate = false;
                context.LastAdvertisedNextExpectedChunkIndex = -1;
                context.LastAdvertisedGrantedUntilExclusive = 0;
                context.DesiredWindowNextExpectedChunkIndex = -1;
                context.DesiredWindowGrantedUntilExclusive = 0;
                context.LastWindowSentNextExpectedChunkIndex = -1;
                context.LastWindowSentGrantedUntilExclusive = 0;
                context.WindowUpdatePumpRunning = false;
                context.WindowUpdateSendRequested = false;
                context.WindowUpdateForceRequested = false;
                context.WindowUpdateRefreshRequested = false;
                context.WindowUpdateRefreshScheduled = false;
                context.LastWindowProgressTimestamp = Stopwatch.GetTimestamp();
                context.StartupWindowRefreshEnabled = true;
                context.StartupWindowRefreshGrantedUntilExclusive = 0;
                context.OldestGapChunkIndex = -1;
                context.OldestGapFirstSeenTimestamp = 0;
                context.CurrentGapMaxBufferedOutOfOrderBytes = 0;
                context.LastMissingRangeRequestTimestamp = 0;
                context.LastRequestedMissingRangeStart = -1;
                context.LastRequestedMissingRangeEnd = -1;
                context.LastRequestedMissingRangeSentTimestamp = 0;
                context.State = FileTransferTransferState.Receiving;
                context.StatusMessage = "Receiving file data.";
                Volatile.Write(ref startupPolicyMode, (int)context.StartupPolicyMode);
                Volatile.Write(ref initialGrantedUntilExclusive, 0);
                snapshot = CreateSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            LogTransferInfo(
                "start_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                fileName: message.FileName,
                fileSizeBytes: message.FileSizeBytes,
                reason: $"chunk_count={message.ChunkCount}; chunk_size_bytes={message.ChunkSizeBytes}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_startup_policy_applied; direction=inbound; transfer_id={message.TransferId}; session_id={message.SessionId}; startup_policy_mode={context!.StartupPolicyMode}; target_outstanding_bytes={context.FlowControlPolicy.TargetOutstandingBytes}; reorder_slack_bytes={context.FlowControlPolicy.ReorderSlackBytes}");
            QueueWindowUpdatePump(context!, force: true);

            return;
        }

        if (context is not null && terminalErrorCode is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: terminalErrorCode,
                statusMessage: terminalStatus ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: terminalStatus,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingChunkAsync(FileTransferChunkV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? context;
        Stream? writeStream = null;
        IncrementalHash? hash = null;
        List<byte[]> contiguousChunkBytes = [];
        int contiguousChunkCount = 0;
        long nextBytesTransferred = 0;
        int nextChunksTransferred = 0;
        int nextChunkIndex = 0;
        bool shouldFinalize = false;
        string? failureCode = null;
        string? failureMessage = null;
        bool bufferedWithoutProgress = false;
        bool duplicateChunkIgnored = false;
        int duplicateChunkIndex = -1;

        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.State != FileTransferTransferState.Receiving)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Chunk arrived in an invalid state.";
            }
            else if (message.ChunkCount != context.ChunkCount)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Chunk metadata did not match the active transfer.";
            }
            else if (context.WriteStream is null || context.Hash is null)
            {
                failureCode = StreamOpenFailedErrorCode;
                failureMessage = "Destination stream is unavailable.";
            }
            else
            {
                var chunkBytes = Array.Empty<byte>();
                try
                {
                    chunkBytes = Convert.FromBase64String(message.DataBase64);
                }
                catch (FormatException)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk payload was not valid base64.";
                }

                if (failureCode is null && (message.ChunkIndex < 0 || message.ChunkIndex >= context.ChunkCount))
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk index exceeded the declared transfer bounds.";
                }

                if (failureCode is null && message.ChunkIndex < context.NextChunkIndex)
                {
                    duplicateChunkIgnored = true;
                    duplicateChunkIndex = message.ChunkIndex;
                }

                if (failureCode is null &&
                    !duplicateChunkIgnored &&
                    context.PendingChunks.ContainsKey(message.ChunkIndex))
                {
                    duplicateChunkIgnored = true;
                    duplicateChunkIndex = message.ChunkIndex;
                }

                var pendingBufferedBytes = 0L;
                if (failureCode is null)
                {
                    foreach (var pendingChunk in context.PendingChunks.Values)
                    {
                        pendingBufferedBytes += pendingChunk.Length;
                    }
                }

                if (failureCode is null &&
                    !duplicateChunkIgnored &&
                    (chunkBytes.Length == 0 ||
                     chunkBytes.Length > context.ChunkSizeBytes ||
                     context.BytesTransferred + pendingBufferedBytes + chunkBytes.Length > context.FileSizeBytes))
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk payload exceeded the declared transfer bounds.";
                }

                if (failureCode is null && !duplicateChunkIgnored)
                {
                    context.PendingChunks[message.ChunkIndex] = chunkBytes;
                    context.BufferedOutOfOrderBytes += chunkBytes.Length;
                    if (context.BufferedOutOfOrderBytes > context.CurrentGapMaxBufferedOutOfOrderBytes)
                    {
                        context.CurrentGapMaxBufferedOutOfOrderBytes = context.BufferedOutOfOrderBytes;
                    }
                    if (message.ChunkIndex > context.HighestBufferedChunkIndex)
                    {
                        context.HighestBufferedChunkIndex = message.ChunkIndex;
                    }
                    UpdateMaximum(ref maxBufferedOutOfOrderBytes, context.BufferedOutOfOrderBytes);
                    writeStream = context.WriteStream;
                    hash = context.Hash;
                    nextBytesTransferred = context.BytesTransferred;
                    nextChunksTransferred = context.ChunksTransferred;
                    nextChunkIndex = context.NextChunkIndex;
                    while (context.PendingChunks.TryGetValue(nextChunkIndex, out var pendingChunkBytes))
                    {
                        contiguousChunkBytes.Add(pendingChunkBytes);
                        nextBytesTransferred += pendingChunkBytes.Length;
                        nextChunksTransferred++;
                        nextChunkIndex++;
                    }

                    contiguousChunkCount = contiguousChunkBytes.Count;
                    shouldFinalize = nextChunkIndex == context.ChunkCount;
                    bufferedWithoutProgress = contiguousChunkCount == 0;
                    if (bufferedWithoutProgress)
                    {
                        UpdateGapTrackingLocked(context, hasContiguousProgress: false);
                    }
                }
            }
        }

        if (failureCode is not null && context is not null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Inbound transfer failed.",
                sendError: true,
                errorMessage: failureMessage,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (duplicateChunkIgnored)
        {
            RecordDuplicateChunkReceived(context!, duplicateChunkIndex);
            return;
        }

        if (bufferedWithoutProgress)
        {
            ScheduleMissingRangeProbe(context!);
            return;
        }

        try
        {
            foreach (var contiguousChunk in contiguousChunkBytes)
            {
                await writeStream!.WriteAsync(contiguousChunk, CancellationToken.None).ConfigureAwait(false);
                hash!.AppendData(contiguousChunk);
            }
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context!,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not write the received chunk.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context!.IsTerminal)
            {
                for (var index = context.NextChunkIndex; index < nextChunkIndex; index++)
                {
                    if (context.PendingChunks.TryGetValue(index, out var removedChunk))
                    {
                        context.PendingChunks.Remove(index);
                        context.BufferedOutOfOrderBytes = Math.Max(0L, context.BufferedOutOfOrderBytes - removedChunk.Length);
                    }
                }

                context.BytesTransferred = nextBytesTransferred;
                context.ChunksTransferred = nextChunksTransferred;
                context.NextChunkIndex = nextChunkIndex;
                context.HighestBufferedChunkIndex = context.PendingChunks.Count == 0
                    ? nextChunkIndex - 1
                    : context.PendingChunks.Keys.Max();
                if (nextChunkIndex > 0)
                {
                    context.StartupWindowRefreshEnabled = false;
                }
                context.State = shouldFinalize
                    ? FileTransferTransferState.Verifying
                    : FileTransferTransferState.Receiving;
                context.StatusMessage = shouldFinalize
                    ? "Verifying received file."
                    : "Receiving file data.";
                UpdateGapTrackingLocked(context, hasContiguousProgress: true);
                snapshot = CreateSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context!, FileTransferDirection.Inbound);
        }

        if (!shouldFinalize)
        {
            QueueWindowUpdatePump(context!);
            return;
        }

        await FinalizeInboundTransferAsync(context!, CancellationToken.None).ConfigureAwait(false);
    }

    private Task HandleIncomingWindowUpdateAsync(FileTransferWindowUpdateV2 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        TaskCompletionSource<bool>? waiter = null;
        string? failureCode = null;
        string? failureMessage = null;
        int grantedUntilExclusive = 0;

        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (context.State is not FileTransferTransferState.Sending and not FileTransferTransferState.AwaitingCompletion)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Window update arrived in an invalid state.";
            }
            else if (!string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                failureCode = SessionMismatchErrorCode;
                failureMessage = "Window update session did not match the active transfer.";
            }
            else if (message.NextExpectedChunkIndex < 0 || message.NextExpectedChunkIndex > context.ChunkCount)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Window update next chunk index exceeded the declared transfer bounds.";
            }
            else if (message.GrantedUntilChunkIndexExclusive < message.NextExpectedChunkIndex)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Window update grant moved before the cumulative ack point.";
            }
            else if (message.BytesReceived < 0 || message.BytesReceived > context.FileSizeBytes)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Window update bytes received exceeded the declared transfer bounds.";
            }
            else
            {
                grantedUntilExclusive = message.GrantedUntilChunkIndexExclusive;
                if (grantedUntilExclusive > context.ChunkCount)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Window update grant exceeded the declared transfer bounds.";
                }
                else if (grantedUntilExclusive - message.NextExpectedChunkIndex > FileTransferProtocol.MaxWindowGrantChunkCount)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Window update grant exceeded the protocol bounds.";
                }
                else
                {
                    context.RemoteWindowInitialized = true;
                    context.RemoteNextExpectedChunkIndex = Math.Max(context.RemoteNextExpectedChunkIndex, message.NextExpectedChunkIndex);
                    context.RemoteGrantedUntilExclusive = Math.Max(context.RemoteGrantedUntilExclusive, grantedUntilExclusive);
                    grantedUntilExclusive = context.RemoteGrantedUntilExclusive;
                    PruneOutboundChunkCacheLocked(context);
                    waiter = context.WindowUpdateSignal;
                }
            }
        }

        if (failureCode is not null && context is not null)
        {
            return TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Outbound transfer failed.",
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        RecordWindowUpdateReceived(message, grantedUntilExclusive, FileTransferDirection.Outbound);
        waiter?.TrySetResult(true);
        return Task.CompletedTask;
    }

    private Task HandleIncomingMissingRangeAsync(FileTransferMissingRangeV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        TaskCompletionSource<bool>? waiter = null;
        string? failureCode = null;
        string? failureMessage = null;
        FileTransferChunkRangeV1? requestedRange = null;
        var queuedRepairChunks = 0;

        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (context.State is not FileTransferTransferState.Sending and not FileTransferTransferState.AwaitingCompletion)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Missing-range request arrived in an invalid state.";
            }
            else if (!string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                failureCode = SessionMismatchErrorCode;
                failureMessage = "Missing-range request session did not match the active transfer.";
            }
            else if (message.Ranges.Count == 0)
            {
                failureCode = InvalidStateErrorCode;
                failureMessage = "Missing-range request did not include a repair range.";
            }
            else
            {
                requestedRange = message.Ranges[0];
                if (requestedRange.StartChunkIndex < 0 ||
                    requestedRange.EndChunkIndexInclusive < requestedRange.StartChunkIndex ||
                    requestedRange.EndChunkIndexInclusive >= context.ChunkCount)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Missing-range request exceeded the declared transfer bounds.";
                }
                else if (message.NextExpectedChunkIndex < 0 ||
                         message.NextExpectedChunkIndex > context.ChunkCount ||
                         message.HighestBufferedChunkIndex < -1 ||
                         (message.HighestBufferedChunkIndex >= 0 && message.HighestBufferedChunkIndex < message.NextExpectedChunkIndex))
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Missing-range request metadata was inconsistent.";
                }
                else if (requestedRange.EndChunkIndexInclusive < context.RemoteNextExpectedChunkIndex)
                {
                    return Task.CompletedTask;
                }
                else
                {
                    queuedRepairChunks = QueueRepairRangeLocked(context, requestedRange.StartChunkIndex, requestedRange.EndChunkIndexInclusive);
                    waiter = context.WindowUpdateSignal;
                }
            }
        }

        if (failureCode is not null && context is not null)
        {
            return TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: failureCode,
                statusMessage: failureMessage ?? "Outbound transfer failed.",
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        if (context is null || requestedRange is null)
        {
            return Task.CompletedTask;
        }

        RecordMissingRangeReceived(message, requestedRange, queuedRepairChunks);
        waiter?.TrySetResult(true);
        return Task.CompletedTask;
    }

    private Task HandleIncomingCancelAsync(FileTransferCancelV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = outboundTransfer is not null &&
                       !outboundTransfer.IsTerminal &&
                       string.Equals(outboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inbound = inboundTransfer is not null &&
                      !inboundTransfer.IsTerminal &&
                      string.Equals(inboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
            return TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledRemote,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer canceled by peer.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        if (inbound is not null)
        {
            LogTransferInfo(
                "cancel_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: NormalizeReason(message.Reason));
            return TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Canceled,
                errorCode: FileTransferResultCodes.CanceledRemote,
                statusMessage: NormalizeReason(message.Reason) ?? "Transfer canceled by peer.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingErrorAsync(FileTransferErrorV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;
        lock (gate)
        {
            outbound = outboundTransfer is not null &&
                       !outboundTransfer.IsTerminal &&
                       string.Equals(outboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? outboundTransfer
                : null;
            inbound = inboundTransfer is not null &&
                      !inboundTransfer.IsTerminal &&
                      string.Equals(inboundTransfer.TransferId, message.TransferId, StringComparison.Ordinal)
                ? inboundTransfer
                : null;
        }

        if (outbound is not null)
        {
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Outbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
            return TransitionOutboundToTerminalAsync(
                outbound,
                FileTransferTransferState.Failed,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                statusMessage: NormalizeReason(message.Message) ?? "Transfer failed on peer.",
                notifyPeer: false,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        if (inbound is not null)
        {
            LogTransferInfo(
                "error_received",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                reason: NormalizeReason(message.Message));
            return TransitionInboundToTerminalAsync(
                inbound,
                FileTransferTransferState.Failed,
                errorCode: NormalizeErrorCode(message.ErrorCode),
                statusMessage: NormalizeReason(message.Message) ?? "Transfer failed on peer.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingCompleteAsync(FileTransferCompleteV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                context.FileSizeBytes != message.FileSizeBytes ||
                !string.Equals(context.Sha256Base64, message.Sha256Base64, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (context.State != FileTransferTransferState.AwaitingCompletion)
            {
                context.PendingRemoteComplete = message;
                return Task.CompletedTask;
            }
        }

        LogTransferInfo(
            "complete_received",
            FileTransferDirection.Outbound,
            message.TransferId,
            sessionId: message.SessionId,
            fileSizeBytes: message.FileSizeBytes);
        return TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Completed,
            errorCode: null,
            statusMessage: "Transfer complete.",
            notifyPeer: false,
            cancelReason: null,
            ct: CancellationToken.None);
    }

    private async Task RunOutboundSendLoopAsync(OutboundTransferContext context)
    {
        List<OutboundChunkSendWork>? inFlightChunks = null;
        try
        {
            var currentTransport = GetTransportOrThrow();
            var startMessage = new FileTransferStartV1
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                FileName = context.FileName,
                FileSizeBytes = context.FileSizeBytes,
                Sha256Base64 = context.Sha256Base64!,
                ChunkCount = context.ChunkCount,
                ChunkSizeBytes = context.ChunkSizeBytes,
            };

            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Sending file metadata.");
            LogTransferInfo(
                "start_sent",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                reason: $"chunk_count={context.ChunkCount}; chunk_size_bytes={context.ChunkSizeBytes}");
            await currentTransport.SendFileTransferStartAsync(startMessage, context.LifetimeCts.Token).ConfigureAwait(false);

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);

            var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
            try
            {
                long totalBytesRead = 0;
                long totalBytesSent = 0;
                int nextChunkIndexToSchedule = 0;
                var completedChunkIndexes = new HashSet<int>();
                inFlightChunks = new List<OutboundChunkSendWork>(GetLocalOutboundChunkWindow(context));
                while (nextChunkIndexToSchedule < context.ChunkCount || inFlightChunks.Count > 0 || HasPendingRepairChunks(context))
                {
                    while (inFlightChunks.Count < GetLocalOutboundChunkWindow(context))
                    {
                        if (TryDequeueRepairChunkSendWork(context, out var repairChunkWork))
                        {
                            repairChunkWork = repairChunkWork with
                            {
                                SendTask = currentTransport.SendFileTransferChunkAsync(
                                    repairChunkWork.Message,
                                    context.LifetimeCts.Token),
                            };

                            inFlightChunks.Add(repairChunkWork);
                            continue;
                        }

                        if (nextChunkIndexToSchedule >= context.ChunkCount)
                        {
                            break;
                        }

                        if (ShouldPauseNormalOutboundChunkScheduling(context))
                        {
                            break;
                        }

                        var remoteGrantedUntilExclusive = GetRemoteGrantedUntilExclusive(context);
                        if (nextChunkIndexToSchedule >= remoteGrantedUntilExclusive)
                        {
                            break;
                        }

                        var remaining = context.FileSizeBytes - totalBytesRead;
                        var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, targetReadSize), context.LifetimeCts.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        totalBytesRead += read;
                        var chunkWork = CreateOutboundChunkSendWork(context, nextChunkIndexToSchedule, read, buffer);
                        CacheOutboundChunk(context, chunkWork);
                        chunkWork = chunkWork with
                        {
                            SendTask = currentTransport.SendFileTransferChunkAsync(
                                chunkWork.Message,
                                context.LifetimeCts.Token),
                        };

                        inFlightChunks.Add(chunkWork);
                        nextChunkIndexToSchedule++;
                    }

                    if (inFlightChunks.Count == 0)
                    {
                        if (nextChunkIndexToSchedule < context.ChunkCount || HasPendingRepairChunks(context))
                        {
                            if (!HasPendingRepairChunks(context) &&
                                IsOutboundBulkClampActive(context))
                            {
                                await AwaitOutboundBulkClampAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                                continue;
                            }

                            await AwaitOutboundWindowCreditAsync(context, nextChunkIndexToSchedule, context.LifetimeCts.Token).ConfigureAwait(false);
                            continue;
                        }

                        break;
                    }

                    var completedSendTask = await Task.WhenAny(inFlightChunks.Select(static work => work.SendTask)).ConfigureAwait(false);
                    var completedIndex = inFlightChunks.FindIndex(work => ReferenceEquals(work.SendTask, completedSendTask));
                    if (completedIndex < 0)
                    {
                        throw new InvalidOperationException("Outbound file-transfer chunk completion could not be matched to in-flight work.");
                    }

                    var completedChunk = inFlightChunks[completedIndex];
                    await completedChunk.SendTask.ConfigureAwait(false);
                    inFlightChunks.RemoveAt(completedIndex);

                    if (completedChunk.IsRepairResend)
                    {
                        RecordRepairChunkResent(context, completedChunk.ChunkIndex);
                        continue;
                    }

                    if (!completedChunkIndexes.Add(completedChunk.ChunkIndex))
                    {
                        throw new InvalidOperationException($"Outbound file-transfer chunk {completedChunk.ChunkIndex} completed more than once.");
                    }

                    totalBytesSent += completedChunk.BytesRead;
                    var completedChunkCount = completedChunkIndexes.Count;

                    var nextState = completedChunkCount == context.ChunkCount
                        ? FileTransferTransferState.AwaitingCompletion
                        : FileTransferTransferState.Sending;
                    UpdateOutboundState(
                        context,
                        nextState,
                        totalBytesSent,
                        completedChunkCount,
                        nextState == FileTransferTransferState.AwaitingCompletion ? "Waiting for receiver verification." : "Sending file data.");
                }

                if (totalBytesRead != context.FileSizeBytes ||
                    totalBytesSent != context.FileSizeBytes ||
                    nextChunkIndexToSchedule != context.ChunkCount ||
                    completedChunkIndexes.Count != context.ChunkCount)
                {
                    await TransitionOutboundToTerminalAsync(
                        context,
                        FileTransferTransferState.Failed,
                        errorCode: FileSizeMismatchErrorCode,
                        statusMessage: "Source stream did not match the declared file size.",
                        notifyPeer: true,
                        cancelReason: null,
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await TryApplyPendingOutboundCompleteAsync(context).ConfigureAwait(false);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
            if (inFlightChunks is { Count: > 0 })
            {
                _ = ObserveOutboundChunkSendTasksAsync(inFlightChunks);
            }

            // Local cancel/disconnect path already transitioned the state.
        }
        catch (Exception ex)
        {
            if (inFlightChunks is { Count: > 0 })
            {
                context.CancelLifetime();
                await DrainOutboundChunkSendTasksAsync(inFlightChunks).ConfigureAwait(false);
            }

            var errorCode =
                ex is TimeoutException timeoutException &&
                string.Equals(timeoutException.Data["nlink_reason"] as string, WindowTimeoutErrorCode, StringComparison.Ordinal)
                    ? WindowTimeoutErrorCode
                    : ex is InvalidOperationException invalidOperationException &&
                      invalidOperationException.Message.Contains("payload exceeded safe budget", StringComparison.OrdinalIgnoreCase)
                        ? PayloadBudgetExceededErrorCode
                        : StreamReadFailedErrorCode;
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: errorCode,
                statusMessage: ex.Message,
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task DrainOutboundChunkSendTasksAsync(IEnumerable<OutboundChunkSendWork> inFlightChunks)
    {
        var sendTasks = inFlightChunks
            .Select(static work => work.SendTask)
            .Where(static task => task is not null)
            .ToArray();
        if (sendTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(sendTasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static Task ObserveOutboundChunkSendTasksAsync(IEnumerable<OutboundChunkSendWork> inFlightChunks)
        => Task.Run(
            async () =>
            {
                try
                {
                    await DrainOutboundChunkSendTasksAsync(inFlightChunks).ConfigureAwait(false);
                }
                catch
                {
                }
            });

    private Task TryApplyPendingOutboundCompleteAsync(OutboundTransferContext context)
    {
        FileTransferCompleteV1? pendingComplete = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.State != FileTransferTransferState.AwaitingCompletion)
            {
                return Task.CompletedTask;
            }

            pendingComplete = context.PendingRemoteComplete;
            context.PendingRemoteComplete = null;
        }

        return pendingComplete is null
            ? Task.CompletedTask
            : HandleIncomingCompleteAsync(pendingComplete);
    }

    private async Task FinalizeInboundTransferAsync(InboundTransferContext context, CancellationToken ct)
    {
        Stream? writeStream;
        IncrementalHash? hash;
        long bytesTransferred;
        string expectedHash;
        string sessionId;
        string transferId;
        FileTransferReceiveDestination? destination;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            writeStream = context.WriteStream;
            hash = context.Hash;
            bytesTransferred = context.BytesTransferred;
            expectedHash = context.Sha256Base64;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            destination = context.WriteDestination;
        }

        if (writeStream is null || hash is null)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamOpenFailedErrorCode,
                statusMessage: "Destination stream became unavailable.",
                sendError: true,
                errorMessage: "Destination stream became unavailable.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (bytesTransferred != context.FileSizeBytes)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: FileSizeMismatchErrorCode,
                statusMessage: "Received bytes did not match the declared file size.",
                sendError: true,
                errorMessage: "Received bytes did not match the declared file size.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string computedHash;
        try
        {
            await writeStream.FlushAsync(ct).ConfigureAwait(false);
            computedHash = Convert.ToBase64String(hash.GetHashAndReset());
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not finalize the destination stream.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(computedHash, expectedHash, StringComparison.Ordinal))
        {
            LogTransferInfo(
                "integrity_verify_failed",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                errorCode: HashMismatchErrorCode,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes);
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: HashMismatchErrorCode,
                statusMessage: "File hash verification failed.",
                sendError: true,
                errorMessage: "File hash verification failed.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        LogTransferInfo(
            "integrity_verify_passed",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes);

        try
        {
            if (destination is not null)
            {
                await destination.FinalizeAsync(ct).ConfigureAwait(false);
            }
            LogTransferInfo(
                "temp_finalize_succeeded",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                savedPath: destination?.FinalPath);
        }
        catch (Exception ex)
        {
            LogTransferInfo(
                "temp_finalize_failed",
                FileTransferDirection.Inbound,
                transferId,
                sessionId: sessionId,
                errorCode: FinalizeFailedErrorCode,
                reason: ex.GetType().Name,
                fileName: context.FileName,
                fileSizeBytes: context.FileSizeBytes,
                savedPath: destination?.FinalPath);
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: FinalizeFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not finalize the received file.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.SavedFilePath = destination?.FinalPath;
                context.SavedDirectoryPath = destination?.DirectoryPath;
                context.SavedFileName = destination?.SafeFileName ?? context.FileName;
            }
        }

        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferCompleteAsync(
                new FileTransferCompleteV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = context.FileSizeBytes,
                    Sha256Base64 = computedHash,
                },
                ct).ConfigureAwait(false);

            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Completed,
                errorCode: null,
                statusMessage: "Transfer complete.",
                sendError: false,
                errorMessage: null,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not send the completion acknowledgment.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task TransitionOutboundToTerminalAsync(
        OutboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool notifyPeer,
        string? cancelReason,
        CancellationToken ct)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldNotifyPeer;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            ReleaseOutboundBulkClampLocked(context);
            context.CancelLifetime();
            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            snapshot = CreateSnapshotLocked();
            shouldNotifyPeer = notifyPeer;
        }

        RaiseTransferChanged(snapshot);
        context.DisposeResources();
        SignalOutboundBulkClampState(context);
        RecordBulkClampReleased(context);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount);

        if (!shouldNotifyPeer)
        {
            return;
        }

        if (terminalState == FileTransferTransferState.Canceled)
        {
            await SendCancelAsync(context.SessionId, context.TransferId, cancelReason, ct).ConfigureAwait(false);
            return;
        }

        if (terminalState == FileTransferTransferState.Failed)
        {
            await SendErrorAsync(context.SessionId, context.TransferId, context.ErrorCode ?? InvalidStateErrorCode, context.StatusMessage, ct).ConfigureAwait(false);
        }
    }

    private async Task TransitionInboundToTerminalAsync(
        InboundTransferContext context,
        FileTransferTransferState terminalState,
        string? errorCode,
        string statusMessage,
        bool sendError,
        string? errorMessage,
        string? cancelReason,
        CancellationToken ct)
    {
        SessionFileTransferSnapshot? snapshot = null;
        bool shouldSendError;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            context.AcceptInProgress = false;
            snapshot = CreateSnapshotLocked();
            shouldSendError = sendError;
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: statusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath);

        if (terminalState == FileTransferTransferState.Declined)
        {
            await SendDeclineAsync(sessionId, transferId, cancelReason ?? DeclinedReason, ct).ConfigureAwait(false);
            return;
        }

        if (terminalState == FileTransferTransferState.Canceled)
        {
            await SendCancelAsync(sessionId, transferId, cancelReason ?? CanceledReason, ct).ConfigureAwait(false);
            return;
        }

        if (shouldSendError)
        {
            await SendErrorAsync(sessionId, transferId, normalizedErrorCode ?? InvalidStateErrorCode, errorMessage ?? statusMessage, ct).ConfigureAwait(false);
        }
    }

    private void UpdateOutboundState(
        OutboundTransferContext context,
        FileTransferTransferState state,
        long bytesTransferred,
        int chunksTransferred,
        string statusMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.State = state;
            context.BytesTransferred = bytesTransferred;
            context.ChunksTransferred = chunksTransferred;
            context.StatusMessage = statusMessage;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private void SetInboundAcceptInProgress(InboundTransferContext context, bool acceptInProgress)
    {
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.AcceptInProgress = acceptInProgress;
            }
        }
    }

    private async Task SendDeclineAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferDeclineAsync(
                new FileTransferDeclineV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"decline send failed: {ex.Message}");
        }
    }

    private async Task SendCancelAsync(string sessionId, string transferId, string? reason, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferCancelAsync(
                new FileTransferCancelV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = NormalizeReason(reason),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"cancel send failed: {ex.Message}");
        }
    }

    private async Task SendErrorAsync(string sessionId, string transferId, string errorCode, string? message, CancellationToken ct)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferErrorAsync(
                new FileTransferErrorV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = NormalizeErrorCode(errorCode) ?? InvalidStateErrorCode,
                    Message = NormalizeReason(message),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"error send failed: {ex.Message}");
        }
    }

    private int GetRemoteGrantedUntilExclusive(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return 0;
            }

            return context.RemoteWindowInitialized ? context.RemoteGrantedUntilExclusive : 0;
        }
    }

    private int GetLocalOutboundChunkWindow(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return DefaultOutboundChunkWindow;
            }

            return Math.Max(1, context.FlowControlPolicy.LocalInFlightChunkSends);
        }
    }

    private static long ResolveStopwatchDeadline(TimeSpan delay)
        => checked(Stopwatch.GetTimestamp() + (long)Math.Ceiling(delay.TotalSeconds * Stopwatch.Frequency));

    private static TimeSpan GetRemainingDeadline(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private bool EnterOutboundBulkClampLocked(OutboundTransferContext context)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            return false;
        }

        if (context.BulkClampActive)
        {
            return false;
        }

        context.BulkClampActive = true;
        context.BulkClampStartedTimestamp = Stopwatch.GetTimestamp();
        context.BulkClampUntilTimestamp = ResolveStopwatchDeadline(CriticalBulkClampDuration);
        return true;
    }

    private bool ReleaseOutboundBulkClampLocked(OutboundTransferContext context)
    {
        if (!ReferenceEquals(outboundTransfer, context) || !context.BulkClampActive)
        {
            return false;
        }

        context.LastBulkClampDurationMs = context.BulkClampStartedTimestamp > 0
            ? (long)Math.Max(0d, Stopwatch.GetElapsedTime(context.BulkClampStartedTimestamp).TotalMilliseconds)
            : 0;
        context.BulkClampActive = false;
        context.BulkClampUntilTimestamp = 0;
        context.BulkClampStartedTimestamp = 0;
        return true;
    }

    private void SignalOutboundBulkClampState(OutboundTransferContext? context)
    {
        if (context is null)
        {
            return;
        }

        TaskCompletionSource<bool>? signal = null;
        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer, context))
            {
                signal = context.BulkClampSignal;
                if (signal is not null && !context.BulkClampActive)
                {
                    context.BulkClampSignal = null;
                }
            }
        }

        signal?.TrySetResult(true);
    }

    private bool IsOutboundBulkClampActive(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return false;
            }

            if (!context.BulkClampActive)
            {
                return false;
            }

            if (GetRemainingDeadline(context.BulkClampUntilTimestamp) > TimeSpan.Zero)
            {
                return true;
            }

            ReleaseOutboundBulkClampLocked(context);
        }

        SignalOutboundBulkClampState(context);
        RecordBulkClampReleased(context);
        return false;
    }

    private bool ShouldPauseNormalOutboundChunkScheduling(OutboundTransferContext context)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.FlowControlPolicy.Mode != FileTransferFlowControlMode.InteractiveCritical ||
                context.PendingRepairChunkIndexes.Count > 0)
            {
                return false;
            }

            if (context.BulkClampActive &&
                GetRemainingDeadline(context.BulkClampUntilTimestamp) > TimeSpan.Zero)
            {
                return true;
            }

            if (!context.BulkClampActive)
            {
                return false;
            }

            ReleaseOutboundBulkClampLocked(context);
        }

        SignalOutboundBulkClampState(context);
        RecordBulkClampReleased(context);
        return false;
    }

    private async Task AwaitOutboundBulkClampAsync(OutboundTransferContext context, CancellationToken ct)
    {
        while (true)
        {
            Task waitTask;
            TimeSpan remainingDelay;

            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    throw new OperationCanceledException(ct);
                }

                if (!context.BulkClampActive)
                {
                    return;
                }

                remainingDelay = GetRemainingDeadline(context.BulkClampUntilTimestamp);
                if (remainingDelay <= TimeSpan.Zero)
                {
                    ReleaseOutboundBulkClampLocked(context);
                    waitTask = Task.CompletedTask;
                }
                else
                {
                    if (context.BulkClampSignal is null || context.BulkClampSignal.Task.IsCompleted)
                    {
                        context.BulkClampSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    waitTask = context.BulkClampSignal.Task;
                }
            }

            if (waitTask.IsCompleted)
            {
                SignalOutboundBulkClampState(context);
                RecordBulkClampReleased(context);
                return;
            }

            var delayTask = Task.Delay(remainingDelay, ct);
            var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
            if (completed == waitTask)
            {
                await waitTask.ConfigureAwait(false);
                return;
            }

            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, context) &&
                    !context.IsTerminal &&
                    context.BulkClampActive &&
                    GetRemainingDeadline(context.BulkClampUntilTimestamp) <= TimeSpan.Zero)
                {
                    ReleaseOutboundBulkClampLocked(context);
                }
            }

            SignalOutboundBulkClampState(context);
            RecordBulkClampReleased(context);
            return;
        }
    }

    private async Task AwaitOutboundWindowCreditAsync(OutboundTransferContext context, int nextChunkIndexToSchedule, CancellationToken ct)
    {
        Task waitTask;
        bool startedWaiting = false;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                throw new OperationCanceledException(ct);
            }

            if (context.PendingRepairChunkIndexes.Count > 0)
            {
                return;
            }

            if (context.RemoteWindowInitialized && nextChunkIndexToSchedule < context.RemoteGrantedUntilExclusive)
            {
                return;
            }

            if (context.WindowUpdateSignal is null || context.WindowUpdateSignal.Task.IsCompleted)
            {
                context.WindowUpdateSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            waitTask = context.WindowUpdateSignal.Task;
            if (!context.IsWaitingForWindow)
            {
                context.IsWaitingForWindow = true;
                context.WindowWaitStartedTimestamp = Stopwatch.GetTimestamp();
                startedWaiting = true;
            }
        }

        if (startedWaiting)
        {
            Interlocked.Exchange(ref senderWaitingForWindow, 1);
            LogWindowWaitStarted(context, nextChunkIndexToSchedule);
        }

        try
        {
            await waitTask.WaitAsync(OutboundWindowUpdateTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RecordWindowWaitReleased(context, nextChunkIndexToSchedule, timedOut: true);
            Interlocked.Increment(ref windowTimeoutCount);
            throw CreateWindowUpdateTimeoutException(context.TransferId, nextChunkIndexToSchedule);
        }
        catch
        {
            RecordWindowWaitReleased(context, nextChunkIndexToSchedule, timedOut: false);
            throw;
        }

        RecordWindowWaitReleased(context, nextChunkIndexToSchedule, timedOut: false);
    }

    private void FailOutboundLocally(OutboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            ReleaseOutboundBulkClampLocked(context);
            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            snapshot = CreateSnapshotLocked();
        }

        context.DisposeResources();
        SignalOutboundBulkClampState(context);
        RecordBulkClampReleased(context);
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Outbound,
            context.TransferId,
            sessionId: context.SessionId,
            errorCode: context.ErrorCode,
            reason: context.StatusMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount);
    }

    private void FailInboundLocally(InboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;
        string sessionId;
        string transferId;
        string? normalizedErrorCode;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            context.AcceptInProgress = false;
            snapshot = CreateSnapshotLocked();
            sessionId = context.SessionId;
            transferId = context.TransferId;
            normalizedErrorCode = context.ErrorCode;
        }

        context.DisposeResources();
        RaiseTransferChanged(snapshot);
        LogTransferInfo(
            "transfer_terminal",
            FileTransferDirection.Inbound,
            transferId,
            sessionId: sessionId,
            errorCode: normalizedErrorCode,
            reason: failureMessage,
            fileName: context.FileName,
            fileSizeBytes: context.FileSizeBytes,
            bytesTransferred: context.BytesTransferred,
            chunksTransferred: context.ChunksTransferred,
            chunkCount: context.ChunkCount,
            savedPath: context.SavedFilePath);
    }

    private void DetachTransportCore(bool markActiveTransfersFailed, string failureCode, string failureMessage)
    {
        IFileTransferSignalingTransport? previousTransport;
        ISignalingTransport? previousLifecycle;
        OutboundTransferContext? outbound;
        InboundTransferContext? inbound;

        lock (gate)
        {
            previousTransport = transport;
            previousLifecycle = transportLifecycle;
            outbound = markActiveTransfersFailed && outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = markActiveTransfersFailed && inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;
            transport = null;
            transportLifecycle = null;
        }

        if (previousTransport is not null)
        {
            previousTransport.FileTransferOfferReceived -= OnFileTransferOfferReceived;
            previousTransport.FileTransferAcceptReceived -= OnFileTransferAcceptReceived;
            previousTransport.FileTransferDeclineReceived -= OnFileTransferDeclineReceived;
            previousTransport.FileTransferStartReceived -= OnFileTransferStartReceived;
            previousTransport.FileTransferChunkReceived -= OnFileTransferChunkReceived;
            previousTransport.FileTransferWindowUpdateReceived -= OnFileTransferWindowUpdateReceived;
            previousTransport.FileTransferMissingRangeReceived -= OnFileTransferMissingRangeReceived;
            previousTransport.FileTransferCancelReceived -= OnFileTransferCancelReceived;
            previousTransport.FileTransferErrorReceived -= OnFileTransferErrorReceived;
            previousTransport.FileTransferCompleteReceived -= OnFileTransferCompleteReceived;
        }

        if (previousLifecycle is not null)
        {
            previousLifecycle.Rejected -= OnTransportRejectedOrDisconnected;
            previousLifecycle.Disconnected -= OnTransportRejectedOrDisconnected;
        }

        if (outbound is not null)
        {
            FailOutboundLocally(outbound, failureCode, failureMessage);
        }

        if (inbound is not null)
        {
            FailInboundLocally(inbound, failureCode, failureMessage);
        }
    }

    private IFileTransferSignalingTransport GetTransportOrThrow()
        => transport ?? throw new InvalidOperationException("No file-transfer transport is attached.");

    private SessionFileTransferSnapshot CreateSnapshot()
    {
        lock (gate)
        {
            return CreateSnapshotLocked();
        }
    }

    private SessionFileTransferSnapshot CreateSnapshotLocked()
        => new(
            Outbound: outboundTransfer?.ToSnapshot(),
            Inbound: inboundTransfer?.ToSnapshot());

    private FileTransferTransferSnapshot? CaptureCurrentOutboundSnapshot()
    {
        lock (gate)
        {
            return outboundTransfer?.ToSnapshot();
        }
    }

    private FileTransferTransferSnapshot? CaptureCurrentInboundSnapshot()
    {
        lock (gate)
        {
            return inboundTransfer?.ToSnapshot();
        }
    }

    private int CaptureAdvertisedGrantedUntilExclusive()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.LastAdvertisedGrantedUntilExclusive : 0;
        }
    }

    private int CaptureRemoteGrantedUntilExclusive()
    {
        lock (gate)
        {
            return outboundTransfer is { IsTerminal: false, RemoteWindowInitialized: true } outbound ? outbound.RemoteGrantedUntilExclusive : 0;
        }
    }

    private int CaptureLastWindowSentNextExpectedChunkIndex()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.LastWindowSentNextExpectedChunkIndex : -1;
        }
    }

    private int CaptureLastWindowSentGrantedUntilExclusive()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.LastWindowSentGrantedUntilExclusive : 0;
        }
    }

    private int CaptureDesiredWindowGrantedUntilExclusive()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.DesiredWindowGrantedUntilExclusive : 0;
        }
    }

    private int CaptureRemainingAdvertisedRunwayChunks()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound
                ? Math.Max(0, inbound.LastWindowSentGrantedUntilExclusive - inbound.NextChunkIndex)
                : 0;
        }
    }

    private int CaptureBufferedOutOfOrderChunks()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.PendingChunks.Count : 0;
        }
    }

    private long CaptureBufferedOutOfOrderBytes()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound ? inbound.BufferedOutOfOrderBytes : 0L;
        }
    }

    private int? CaptureOldestGapChunkIndex()
    {
        lock (gate)
        {
            return inboundTransfer is { IsTerminal: false } inbound && inbound.OldestGapChunkIndex >= 0
                ? inbound.OldestGapChunkIndex
                : null;
        }
    }

    private long? CaptureOldestGapAgeMs()
    {
        lock (gate)
        {
            if (inboundTransfer is not { IsTerminal: false } inbound ||
                inbound.OldestGapChunkIndex < 0 ||
                inbound.OldestGapFirstSeenTimestamp <= 0)
            {
                return null;
            }

            return (long)Math.Max(0d, Stopwatch.GetElapsedTime(inbound.OldestGapFirstSeenTimestamp).TotalMilliseconds);
        }
    }

    private string? CaptureLastRepairRange()
        => Volatile.Read(ref lastRepairRange);

    private long? CaptureLastRepairLatencyMs()
    {
        var value = Interlocked.Read(ref lastRepairLatencyMs);
        return value >= 0 ? value : null;
    }

    private bool CaptureBulkClampActive()
    {
        lock (gate)
        {
            return outboundTransfer is { IsTerminal: false, BulkClampActive: true } outbound &&
                   GetRemainingDeadline(outbound.BulkClampUntilTimestamp) > TimeSpan.Zero;
        }
    }

    private void CacheOutboundChunk(OutboundTransferContext context, OutboundChunkSendWork chunkWork)
    {
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || chunkWork.IsRepairResend)
            {
                return;
            }

            context.SentChunkCache[chunkWork.ChunkIndex] = chunkWork.ChunkBytes.AsSpan().ToArray();
            PruneOutboundChunkCacheLocked(context);
        }
    }

    private bool HasPendingRepairChunks(OutboundTransferContext context)
    {
        lock (gate)
        {
            return ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.PendingRepairChunkIndexes.Count > 0;
        }
    }

    private bool TryDequeueRepairChunkSendWork(OutboundTransferContext context, out OutboundChunkSendWork chunkWork)
    {
        lock (gate)
        {
            while (ReferenceEquals(outboundTransfer, context) &&
                   !context.IsTerminal &&
                   context.PendingRepairChunkIndexes.Count > 0)
            {
                var chunkIndex = context.PendingRepairChunkIndexes.Dequeue();
                context.PendingRepairChunkIndexSet.Remove(chunkIndex);
                if (!context.SentChunkCache.TryGetValue(chunkIndex, out var chunkBytes))
                {
                    continue;
                }

                chunkWork = CreateRepairChunkSendWork(context, chunkIndex, chunkBytes);
                return true;
            }
        }

        chunkWork = default!;
        return false;
    }

    private Task RefreshInboundWindowAfterPolicyChangeAsync(InboundTransferContext context)
    {
        QueueWindowUpdatePump(context, force: true);
        return Task.CompletedTask;
    }

    private int QueueRepairRangeLocked(OutboundTransferContext context, int startChunkIndex, int endChunkIndexInclusive)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
        {
            return 0;
        }

        var queuedCount = 0;
        var boundedEnd = Math.Min(endChunkIndexInclusive, startChunkIndex + EscalatedRepairRangeChunks - 1);
        for (var chunkIndex = startChunkIndex; chunkIndex <= boundedEnd; chunkIndex++)
        {
            if (chunkIndex < context.RemoteNextExpectedChunkIndex ||
                !context.SentChunkCache.ContainsKey(chunkIndex) ||
                !context.PendingRepairChunkIndexSet.Add(chunkIndex))
            {
                continue;
            }

            context.PendingRepairChunkIndexes.Enqueue(chunkIndex);
            queuedCount++;
        }

        return queuedCount;
    }

    private void QueueWindowUpdatePump(InboundTransferContext context, bool force = false, bool refresh = false)
    {
        var shouldStartPump = false;
        var shouldCountCoalesced = false;
        var desiredNextExpectedChunkIndex = -1;
        var desiredGrantedUntilExclusive = 0;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (!refresh)
            {
                context.LastWindowProgressTimestamp = Stopwatch.GetTimestamp();
            }

            if (!TryBuildWindowUpdateCandidateLocked(context, force, out var message, out var grantedUntilExclusive, out _))
            {
                return;
            }

            desiredNextExpectedChunkIndex = message!.NextExpectedChunkIndex;
            desiredGrantedUntilExclusive = grantedUntilExclusive;
            var desiredChanged =
                context.DesiredWindowNextExpectedChunkIndex != desiredNextExpectedChunkIndex ||
                context.DesiredWindowGrantedUntilExclusive != desiredGrantedUntilExclusive;

            shouldCountCoalesced =
                desiredChanged &&
                (context.WindowUpdatePumpRunning || context.WindowUpdateSendRequested);

            context.DesiredWindowNextExpectedChunkIndex = desiredNextExpectedChunkIndex;
            context.DesiredWindowGrantedUntilExclusive = desiredGrantedUntilExclusive;
            context.WindowUpdateSendRequested = true;
            context.WindowUpdateForceRequested |= force;
            context.WindowUpdateRefreshRequested |= refresh;

            if (!context.WindowUpdatePumpRunning)
            {
                context.WindowUpdatePumpRunning = true;
                shouldStartPump = true;
            }
        }

        if (shouldCountCoalesced)
        {
            RecordWindowUpdateCoalesced(context, desiredNextExpectedChunkIndex, desiredGrantedUntilExclusive);
        }

        if (shouldStartPump)
        {
            _ = Task.Run(() => RunWindowUpdatePumpAsync(context));
        }
    }

    private async Task RunWindowUpdatePumpAsync(InboundTransferContext context)
    {
        while (true)
        {
            var shouldRetryAfterDelay = false;
            var shouldSendRefresh = false;
            IFileTransferSignalingTransport? currentTransport = null;
            bool force;
            bool refresh;

            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                {
                    context.WindowUpdatePumpRunning = false;
                    context.WindowUpdateSendRequested = false;
                    context.WindowUpdateForceRequested = false;
                    context.WindowUpdateRefreshRequested = false;
                    return;
                }

                if (!context.WindowUpdateSendRequested)
                {
                    context.WindowUpdatePumpRunning = false;
                    return;
                }

                force = context.WindowUpdateForceRequested;
                refresh = context.WindowUpdateRefreshRequested;
                context.WindowUpdateSendRequested = false;
                context.WindowUpdateForceRequested = false;
                context.WindowUpdateRefreshRequested = false;
                shouldSendRefresh = refresh;
                currentTransport = transport;
            }

            if (currentTransport is null)
            {
                shouldRetryAfterDelay = true;
            }
            else
            {
                try
                {
                    await SendWindowUpdateIfNeededAsync(context, force, refresh, context.LifetimeCts.Token).ConfigureAwait(false);
                    if (shouldSendRefresh)
                    {
                        RecordWindowUpdateRefreshSent(context);
                    }

                    ScheduleWindowUpdateIdleRefresh(context);
                }
                catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RecordWindowUpdateSendFailure(context, ex, refresh: shouldSendRefresh);
                    shouldRetryAfterDelay = true;
                }
            }

            if (!shouldRetryAfterDelay)
            {
                continue;
            }

            try
            {
                await Task.Delay(WindowUpdateRetryDelay, context.LifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }

            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                {
                    context.WindowUpdatePumpRunning = false;
                    return;
                }

                context.WindowUpdateSendRequested = true;
                context.WindowUpdateForceRequested = true;
                context.WindowUpdateRefreshRequested |= shouldSendRefresh;
            }
        }
    }

    private void ScheduleWindowUpdateIdleRefresh(InboundTransferContext context)
    {
        var shouldSchedule = false;
        long progressTimestamp = 0;

        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.HasAdvertisedWindowUpdate &&
                (context.StartupWindowRefreshEnabled ||
                 context.PendingChunks.Count > 0 ||
                 context.DesiredWindowGrantedUntilExclusive > context.LastWindowSentGrantedUntilExclusive ||
                 context.LastAdvertisedGrantedUntilExclusive > context.NextChunkIndex) &&
                !context.WindowUpdateRefreshScheduled)
            {
                context.WindowUpdateRefreshScheduled = true;
                progressTimestamp = context.LastWindowProgressTimestamp;
                shouldSchedule = true;
            }
        }

        if (!shouldSchedule)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var shouldReschedule = false;
            try
            {
                await Task.Delay(WindowUpdateIdleRefreshDelay, context.LifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
                return;
            }

            var shouldRefresh = false;
            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                {
                    context.WindowUpdateRefreshScheduled = false;
                    return;
                }

                context.WindowUpdateRefreshScheduled = false;
                shouldRefresh =
                    context.HasAdvertisedWindowUpdate &&
                    context.LastWindowProgressTimestamp == progressTimestamp &&
                    (context.StartupWindowRefreshEnabled ||
                     context.PendingChunks.Count > 0 ||
                     context.DesiredWindowGrantedUntilExclusive > context.LastWindowSentGrantedUntilExclusive ||
                     context.LastAdvertisedGrantedUntilExclusive > context.NextChunkIndex);
                shouldReschedule =
                    !shouldRefresh &&
                    context.HasAdvertisedWindowUpdate &&
                    context.LastWindowProgressTimestamp != progressTimestamp &&
                    (context.StartupWindowRefreshEnabled ||
                     context.PendingChunks.Count > 0 ||
                     context.DesiredWindowGrantedUntilExclusive > context.LastWindowSentGrantedUntilExclusive ||
                     context.LastAdvertisedGrantedUntilExclusive > context.NextChunkIndex);
            }

            if (shouldRefresh)
            {
                QueueWindowUpdatePump(context, force: true, refresh: true);
            }
            else if (shouldReschedule)
            {
                ScheduleWindowUpdateIdleRefresh(context);
            }
        });
    }

    private void PruneOutboundChunkCacheLocked(OutboundTransferContext context)
    {
        if (!ReferenceEquals(outboundTransfer, context))
        {
            return;
        }

        while (context.SentChunkCache.Count > 0)
        {
            var first = context.SentChunkCache.First();
            if (first.Key >= context.RemoteNextExpectedChunkIndex)
            {
                break;
            }

            context.SentChunkCache.Remove(first.Key);
            context.PendingRepairChunkIndexSet.Remove(first.Key);
        }
    }

    private void UpdateGapTrackingLocked(InboundTransferContext context, bool hasContiguousProgress)
    {
        if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
        {
            return;
        }

        var previousGapStart = context.OldestGapChunkIndex;
        var hadResolvedRequestedRange =
            context.LastRequestedMissingRangeEnd >= 0 &&
            context.NextChunkIndex > context.LastRequestedMissingRangeEnd;
        var resolvedRequestStart = context.LastRequestedMissingRangeStart;
        var resolvedRequestEnd = context.LastRequestedMissingRangeEnd;
        var resolvedRequestSentTimestamp = context.LastRequestedMissingRangeSentTimestamp;

        if (context.PendingChunks.Count == 0)
        {
            if (previousGapStart >= 0 &&
                hadResolvedRequestedRange &&
                resolvedRequestSentTimestamp > 0)
            {
                var repairLatencyMs = (long)Math.Max(0d, Stopwatch.GetElapsedTime(resolvedRequestSentTimestamp).TotalMilliseconds);
                Interlocked.Exchange(ref lastRepairLatencyMs, repairLatencyMs);
                Volatile.Write(ref lastRepairRange, $"{resolvedRequestStart}-{resolvedRequestEnd}");
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_gap_resolved; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; resolved_gap_start={previousGapStart}; next_expected_chunk_index={context.NextChunkIndex}; repair_latency_ms={repairLatencyMs}");
            }

            context.OldestGapChunkIndex = -1;
            context.OldestGapFirstSeenTimestamp = 0;
            context.CurrentGapMaxBufferedOutOfOrderBytes = 0;
            context.LastRequestedMissingRangeStart = -1;
            context.LastRequestedMissingRangeEnd = -1;
            context.LastRequestedMissingRangeSentTimestamp = 0;
            return;
        }

        var nextGapStart = context.NextChunkIndex;
        if (context.OldestGapChunkIndex != nextGapStart)
        {
            context.OldestGapChunkIndex = nextGapStart;
            context.OldestGapFirstSeenTimestamp = Stopwatch.GetTimestamp();
            context.CurrentGapMaxBufferedOutOfOrderBytes = context.BufferedOutOfOrderBytes;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_gap_detected; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; oldest_gap_chunk_index={context.OldestGapChunkIndex}; highest_buffered_chunk_index={context.HighestBufferedChunkIndex}; buffered_out_of_order_chunks={context.PendingChunks.Count}; buffered_out_of_order_bytes={context.BufferedOutOfOrderBytes}");
        }
        else if (hasContiguousProgress && hadResolvedRequestedRange && resolvedRequestSentTimestamp > 0)
        {
            var repairLatencyMs = (long)Math.Max(0d, Stopwatch.GetElapsedTime(resolvedRequestSentTimestamp).TotalMilliseconds);
            Interlocked.Exchange(ref lastRepairLatencyMs, repairLatencyMs);
            Volatile.Write(ref lastRepairRange, $"{resolvedRequestStart}-{resolvedRequestEnd}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_gap_resolved; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; resolved_gap_start={resolvedRequestStart}; next_expected_chunk_index={context.NextChunkIndex}; repair_latency_ms={repairLatencyMs}");
            context.LastRequestedMissingRangeStart = -1;
            context.LastRequestedMissingRangeEnd = -1;
            context.LastRequestedMissingRangeSentTimestamp = 0;
        }

        if (context.BufferedOutOfOrderBytes > context.CurrentGapMaxBufferedOutOfOrderBytes)
        {
            context.CurrentGapMaxBufferedOutOfOrderBytes = context.BufferedOutOfOrderBytes;
        }
    }

    private void ScheduleMissingRangeProbe(InboundTransferContext context)
    {
        var shouldSchedule = false;
        var delay = MissingRangeRepairThreshold;
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) &&
                !context.IsTerminal &&
                context.PendingChunks.Count > 0 &&
                !context.MissingRangeProbeScheduled)
            {
                context.MissingRangeProbeScheduled = true;
                if (context.BufferedOutOfOrderBytes >= MissingRangeBufferPressureBytes)
                {
                    delay = TimeSpan.Zero;
                }
                shouldSchedule = true;
            }
        }

        if (!shouldSchedule)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, context.LifetimeCts.Token).ConfigureAwait(false);
                }

                await ProbeMissingRangeAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
            {
            }
        });
    }

    private async Task ProbeMissingRangeAsync(InboundTransferContext context)
    {
        FileTransferMissingRangeV1? message = null;
        FileTransferChunkRangeV1? requestedRange = null;
        string? triggerReason = null;
        bool escalated = false;
        long maxBufferedOutOfOrderBytesDuringGap = 0;
        IFileTransferSignalingTransport? currentTransport = null;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                context.MissingRangeProbeScheduled = false;
                return;
            }

            context.MissingRangeProbeScheduled = false;
            if (!TryBuildMissingRangeRequestLocked(
                    context,
                    out message,
                    out requestedRange,
                    out triggerReason,
                    out escalated,
                    out maxBufferedOutOfOrderBytesDuringGap))
            {
                if (context.PendingChunks.Count > 0)
                {
                    ScheduleMissingRangeProbe(context);
                }

                return;
            }

            currentTransport = transport;
        }

        if (message is null || requestedRange is null || currentTransport is null)
        {
            return;
        }

        try
        {
            await currentTransport.SendFileTransferMissingRangeAsync(message, context.LifetimeCts.Token).ConfigureAwait(false);
            RecordMissingRangeSent(message, requestedRange, triggerReason!, escalated, maxBufferedOutOfOrderBytesDuringGap);
            ScheduleMissingRangeProbe(context);
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordMissingRangeSendFailure(context, requestedRange, triggerReason!, escalated, maxBufferedOutOfOrderBytesDuringGap, ex);
            ScheduleMissingRangeProbe(context);
        }
    }

    private bool TryBuildMissingRangeRequestLocked(
        InboundTransferContext context,
        out FileTransferMissingRangeV1? message,
        out FileTransferChunkRangeV1? requestedRange,
        out string? triggerReason,
        out bool escalated,
        out long maxBufferedOutOfOrderBytesDuringGap)
    {
        message = null;
        requestedRange = null;
        triggerReason = null;
        escalated = false;
        maxBufferedOutOfOrderBytesDuringGap = 0;

        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.PendingChunks.Count == 0 ||
            context.OldestGapChunkIndex < 0 ||
            context.OldestGapFirstSeenTimestamp <= 0)
        {
            return false;
        }

        if (!TryResolveMissingRangeTriggerReasonLocked(context, out triggerReason))
        {
            return false;
        }

        var isSameOldestGap = context.LastRequestedMissingRangeStart == context.OldestGapChunkIndex;
        if (context.LastMissingRangeRequestTimestamp > 0 &&
            Stopwatch.GetElapsedTime(context.LastMissingRangeRequestTimestamp) < MissingRangeRepairCooldown &&
            isSameOldestGap)
        {
            Interlocked.Increment(ref missingRangeSuppressedCooldown);
            return false;
        }

        var firstBufferedChunkIndex = context.PendingChunks.Keys.First();
        var startChunkIndex = context.NextChunkIndex;
        var repairRangeChunks = InitialRepairRangeChunks;
        if (isSameOldestGap &&
            context.LastMissingRangeRequestTimestamp > 0 &&
            Stopwatch.GetElapsedTime(context.LastMissingRangeRequestTimestamp) >= MissingRangeRepairCooldown)
        {
            repairRangeChunks = EscalatedRepairRangeChunks;
            escalated = true;
        }

        var endChunkIndexInclusive = Math.Min(firstBufferedChunkIndex - 1, startChunkIndex + repairRangeChunks - 1);
        if (endChunkIndexInclusive < startChunkIndex)
        {
            return false;
        }

        requestedRange = new FileTransferChunkRangeV1
        {
            StartChunkIndex = startChunkIndex,
            EndChunkIndexInclusive = endChunkIndexInclusive,
        };
        message = new FileTransferMissingRangeV1
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Ranges = [requestedRange],
            NextExpectedChunkIndex = context.NextChunkIndex,
            HighestBufferedChunkIndex = context.HighestBufferedChunkIndex,
        };

        context.LastMissingRangeRequestTimestamp = Stopwatch.GetTimestamp();
        context.LastRequestedMissingRangeStart = requestedRange.StartChunkIndex;
        context.LastRequestedMissingRangeEnd = requestedRange.EndChunkIndexInclusive;
        context.LastRequestedMissingRangeSentTimestamp = context.LastMissingRangeRequestTimestamp;
        maxBufferedOutOfOrderBytesDuringGap = Math.Max(context.CurrentGapMaxBufferedOutOfOrderBytes, context.BufferedOutOfOrderBytes);
        return true;
    }

    private bool TryResolveMissingRangeTriggerReasonLocked(InboundTransferContext context, out string? triggerReason)
    {
        triggerReason = null;

        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.PendingChunks.Count == 0 ||
            context.OldestGapChunkIndex < 0 ||
            context.OldestGapFirstSeenTimestamp <= 0)
        {
            return false;
        }

        if (context.BufferedOutOfOrderBytes >= MissingRangeSeverePressureBytes)
        {
            triggerReason = "severe_pressure";
            return true;
        }

        if (context.BufferedOutOfOrderBytes >= MissingRangeBufferPressureBytes)
        {
            triggerReason = "buffer_pressure";
            return true;
        }

        if (Stopwatch.GetElapsedTime(context.OldestGapFirstSeenTimestamp) >= MissingRangeRepairThreshold)
        {
            triggerReason = "time";
            return true;
        }

        return false;
    }

    private void RaiseTransferChanged(SessionFileTransferSnapshot snapshot)
    {
        try
        {
            TransferChanged?.Invoke(this, new SessionFileTransferSnapshotChangedEventArgs(snapshot));
        }
        catch
        {
        }
    }

    private static FileTransferSendDescriptor NormalizeSendDescriptor(FileTransferSendDescriptor descriptor, Func<string> transferIdFactory)
    {
        var normalizedFileName = NormalizeRequiredBounded(descriptor.FileName, FileTransferProtocol.MaxFileNameLength, nameof(descriptor.FileName));
        if (descriptor.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.FileSizeBytes), "File size must be positive.");
        }

        var chunkSizeBytes = descriptor.ChunkSizeBytes ?? FileTransferProtocol.MaxChunkRawBytes;
        if (chunkSizeBytes <= 0 || chunkSizeBytes > FileTransferProtocol.MaxChunkRawBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.ChunkSizeBytes), $"Chunk size must be between 1 and {FileTransferProtocol.MaxChunkRawBytes} bytes.");
        }

        var transferId = string.IsNullOrWhiteSpace(descriptor.TransferId)
            ? NormalizeTransferId(transferIdFactory())
            : NormalizeTransferId(descriptor.TransferId);

        return descriptor with
        {
            FileName = normalizedFileName,
            TransferId = transferId,
            ChunkSizeBytes = chunkSizeBytes,
        };
    }

    private static string NormalizeTransferId(string? transferId)
    {
        return NormalizeRequiredBounded(transferId, FileTransferProtocol.MaxTransferIdLength, nameof(transferId));
    }

    private static string NormalizeRequiredBounded(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(paramName, $"Value exceeds {maxLength} characters.");
        }

        return normalized;
    }

    private static string? NormalizeReason(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > FileTransferProtocol.MaxErrorCodeLength
            ? normalized[..FileTransferProtocol.MaxErrorCodeLength]
            : normalized;
    }

    private static void ValidateReadableStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Source stream must be readable.");
        }

        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }
    }

    private static void ValidateWritableStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new InvalidOperationException("Destination stream must be writable.");
        }
    }

    private static bool TryCalculateExpectedChunkCount(long fileSizeBytes, int chunkSizeBytes, out int chunkCount)
    {
        chunkCount = 0;
        if (fileSizeBytes <= 0 || chunkSizeBytes <= 0)
        {
            return false;
        }

        try
        {
            chunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes));
            return chunkCount > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int ResolveSafeOutboundChunkSize(
        OutboundTransferContext context,
        IFileTransferSignalingTransport? currentTransport)
    {
        if (currentTransport is IFileTransferChunkBudgetProvider chunkBudgetProvider)
        {
            return chunkBudgetProvider.ResolveSafeOutboundChunkSize(
                new FileTransferChunkBudgetRequest(
                    context.TransferId,
                    context.FileSizeBytes,
                    context.ChunkSizeBytes));
        }

        var budgetSessionId = string.IsNullOrWhiteSpace(context.SessionId)
            ? new string('s', 96)
            : context.SessionId;
        var candidateChunkSize = context.ChunkSizeBytes;
        while (true)
        {
            if (!TryCalculateExpectedChunkCount(context.FileSizeBytes, candidateChunkSize, out var chunkCount))
            {
                throw new InvalidOperationException("Couldn't determine outbound file-transfer chunk count.");
            }

            var safeChunkSize = FileTransferPayloadCodec.ComputeSafeRawChunkSizeForBudget(
                budgetSessionId,
                context.TransferId,
                chunkCount,
                candidateChunkSize);
            if (safeChunkSize >= candidateChunkSize)
            {
                return candidateChunkSize;
            }

            candidateChunkSize = safeChunkSize;
        }
    }

    private static OutboundChunkSendWork CreateOutboundChunkSendWork(
        OutboundTransferContext context,
        int chunkIndex,
        int bytesRead,
        byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(buffer);

        if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        if (bytesRead <= 0 || bytesRead > context.ChunkSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesRead));
        }

        var chunkBytes = new byte[bytesRead];
        Array.Copy(buffer, 0, chunkBytes, 0, bytesRead);
        var message = new FileTransferChunkV1
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            ChunkIndex = chunkIndex,
            ChunkCount = context.ChunkCount,
            DataBase64 = Convert.ToBase64String(chunkBytes),
        };

        return new OutboundChunkSendWork(
            chunkIndex,
            context.ChunkCount,
            context.ChunkSizeBytes,
            bytesRead,
            chunkBytes,
            message,
            IsRepairResend: false,
            Task.CompletedTask);
    }

    private static OutboundChunkSendWork CreateRepairChunkSendWork(OutboundTransferContext context, int chunkIndex, byte[] chunkBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(chunkBytes);

        if (chunkIndex < 0 || chunkIndex >= context.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        var safeBytes = chunkBytes.AsSpan().ToArray();
        var message = new FileTransferChunkV1
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            ChunkIndex = chunkIndex,
            ChunkCount = context.ChunkCount,
            DataBase64 = Convert.ToBase64String(safeBytes),
        };

        return new OutboundChunkSendWork(
            chunkIndex,
            context.ChunkCount,
            context.ChunkSizeBytes,
            safeBytes.Length,
            safeBytes,
            message,
            IsRepairResend: true,
            Task.CompletedTask);
    }

    private async Task SendWindowUpdateIfNeededAsync(InboundTransferContext context, bool force, bool refresh, CancellationToken ct)
    {
        FileTransferWindowUpdateV2? message;
        int grantedUntilExclusive;
        var lowWatermarkTriggered = false;

        lock (gate)
        {
            if (!TryBuildWindowUpdateCandidateLocked(context, force, out message, out grantedUntilExclusive, out lowWatermarkTriggered))
            {
                return;
            }
        }

        var currentTransport = GetTransportOrThrow();
        await currentTransport.SendFileTransferWindowUpdateAsync(message!, ct).ConfigureAwait(false);

        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                var initialWindowAdvertisement = !context.HasAdvertisedWindowUpdate;
                context.HasAdvertisedWindowUpdate = true;
                context.LastAdvertisedNextExpectedChunkIndex = message!.NextExpectedChunkIndex;
                context.LastAdvertisedGrantedUntilExclusive = grantedUntilExclusive;
                context.LastWindowSentNextExpectedChunkIndex = message.NextExpectedChunkIndex;
                context.LastWindowSentGrantedUntilExclusive = grantedUntilExclusive;
                if (initialWindowAdvertisement)
                {
                    Interlocked.Exchange(ref initialGrantedUntilExclusive, grantedUntilExclusive);
                }
                if (context.StartupWindowRefreshEnabled && message.NextExpectedChunkIndex <= 0)
                {
                    if (context.StartupWindowRefreshGrantedUntilExclusive <= 0)
                    {
                        context.StartupWindowRefreshGrantedUntilExclusive = grantedUntilExclusive;
                    }
                    else if (grantedUntilExclusive > context.StartupWindowRefreshGrantedUntilExclusive)
                    {
                        context.StartupWindowRefreshEnabled = false;
                    }
                }
                else if (message.NextExpectedChunkIndex > 0)
                {
                    context.StartupWindowRefreshEnabled = false;
                }
            }
        }

        if (refresh && context.StartupWindowRefreshEnabled && message!.NextExpectedChunkIndex <= 0)
        {
            RecordStartupWindowRefreshSent(context, grantedUntilExclusive);
        }

        if (lowWatermarkTriggered)
        {
            RecordWindowUpdateLowWatermarkSent(context, message!, grantedUntilExclusive);
        }

        RecordWindowUpdateSent(message!, grantedUntilExclusive, FileTransferDirection.Inbound);
    }

    private bool TryBuildWindowUpdateCandidateLocked(
        InboundTransferContext context,
        bool force,
        out FileTransferWindowUpdateV2? message,
        out int grantedUntilExclusive,
        out bool lowWatermarkTriggered)
    {
        message = null;
        grantedUntilExclusive = 0;
        lowWatermarkTriggered = false;

        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.ChunkCount <= 0 ||
            context.NextChunkIndex < 0 ||
            context.NextChunkIndex >= context.ChunkCount)
        {
            return false;
        }

        var nextExpectedChunkIndex = context.NextChunkIndex;
        grantedUntilExclusive = ResolveDesiredGrantedUntilExclusiveLocked(context);
        if (grantedUntilExclusive <= nextExpectedChunkIndex)
        {
            return false;
        }

        if (context.HasAdvertisedWindowUpdate)
        {
            var extensionBy = Math.Max(0, grantedUntilExclusive - context.LastWindowSentGrantedUntilExclusive);
            var remainingAdvertisedChunks = Math.Max(0, context.LastWindowSentGrantedUntilExclusive - nextExpectedChunkIndex);
            var lowWatermarkChunks = Math.Max(1, context.FlowControlPolicy.LowWatermarkChunks);
            var unresolvedGap = context.PendingChunks.Count > 0 && context.OldestGapChunkIndex >= 0;
            var startupRefresh = context.StartupWindowRefreshEnabled && nextExpectedChunkIndex <= 0;
            var grantUnchanged = grantedUntilExclusive == context.LastWindowSentGrantedUntilExclusive;
            var advertisedRunwayOutstanding = context.LastAdvertisedGrantedUntilExclusive > nextExpectedChunkIndex;
            var identical =
                nextExpectedChunkIndex == context.LastWindowSentNextExpectedChunkIndex &&
                grantedUntilExclusive == context.LastWindowSentGrantedUntilExclusive;
            var progressOnlyAdvance =
                grantUnchanged &&
                nextExpectedChunkIndex > context.LastWindowSentNextExpectedChunkIndex;
            var shouldSendRefresh =
                force &&
                (startupRefresh ||
                 unresolvedGap ||
                 advertisedRunwayOutstanding ||
                 grantedUntilExclusive > context.LastWindowSentGrantedUntilExclusive);

            if (identical && !shouldSendRefresh)
            {
                RecordWindowUpdateSuppressed(context, "identical");
                return false;
            }

            if (!force &&
                progressOnlyAdvance &&
                !startupRefresh &&
                !unresolvedGap)
            {
                RecordWindowUpdateSuppressed(
                    context,
                    context.LastWindowSentGrantedUntilExclusive >= context.ChunkCount ? "tail" : "no_extension");
                return false;
            }

            if (!force &&
                remainingAdvertisedChunks > lowWatermarkChunks &&
                extensionBy < Math.Max(1, context.FlowControlPolicy.MinExtensionStepChunks))
            {
                RecordWindowUpdateSuppressed(context, "small_delta");
                return false;
            }

            lowWatermarkTriggered = !force && remainingAdvertisedChunks <= lowWatermarkChunks;
        }

        message = new FileTransferWindowUpdateV2
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            NextExpectedChunkIndex = nextExpectedChunkIndex,
            GrantedUntilChunkIndexExclusive = grantedUntilExclusive,
            BytesReceived = context.BytesTransferred,
        };
        return true;
    }

    private int ResolveDesiredGrantedUntilExclusiveLocked(InboundTransferContext context)
    {
        var chunkSizeBytes = context.ChunkSizeBytes;
        if (chunkSizeBytes <= 0)
        {
            return context.NextChunkIndex;
        }

        var baseChunks = ResolveWindowBudgetChunks(context.FlowControlPolicy.TargetOutstandingBytes, chunkSizeBytes);
        var slackChunks = ResolveWindowBudgetChunks(context.FlowControlPolicy.ReorderSlackBytes, chunkSizeBytes);
        var capChunks = ResolveWindowBudgetChunks(context.FlowControlPolicy.HardOutstandingCapBytes, chunkSizeBytes);
        if (baseChunks <= 0 && slackChunks <= 0 && capChunks <= 0)
        {
            return context.NextChunkIndex;
        }

        var highestSeenExclusive = Math.Max(context.NextChunkIndex, context.HighestBufferedChunkIndex + 1);
        var baseGrant = Math.Min(context.ChunkCount, checked(context.NextChunkIndex + baseChunks));
        var reorderGrant = Math.Min(context.ChunkCount, checked(highestSeenExclusive + slackChunks));
        var capGrant = Math.Min(context.ChunkCount, checked(context.NextChunkIndex + capChunks));
        var desiredGrant = Math.Min(
            capGrant,
            Math.Max(
                context.LastWindowSentGrantedUntilExclusive,
                Math.Max(baseGrant, reorderGrant)));
        return Math.Max(context.NextChunkIndex, desiredGrant);
    }

    private static int ResolveWindowBudgetChunks(long targetBytes, int chunkSizeBytes)
    {
        if (chunkSizeBytes <= 0 || targetBytes <= 0)
        {
            return 0;
        }

        var targetChunks = checked((int)Math.Ceiling(targetBytes / (double)chunkSizeBytes));
        return Math.Clamp(targetChunks, 0, FileTransferProtocol.MaxWindowGrantChunkCount);
    }

    private static string FormatYesNo(bool value) => value ? "yes" : "no";

    private static void Warn(string message)
    {
        LocalOperationalLog.Warn("FileTransferService", $"event=warning; message={message}");
    }

    private static void LogTransferInfo(
        string eventName,
        FileTransferDirection direction,
        string transferId,
        string? sessionId = null,
        string? errorCode = null,
        string? reason = null,
        string? fileName = null,
        long? fileSizeBytes = null,
        long? bytesTransferred = null,
        int? chunksTransferred = null,
        int? chunkCount = null,
        string? savedPath = null)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId ?? "(none)"}; file_name_len={(string.IsNullOrWhiteSpace(fileName) ? 0 : fileName.Trim().Length)}; file_size_bytes={(fileSizeBytes?.ToString() ?? "(none)")}; bytes_transferred={(bytesTransferred?.ToString() ?? "(none)")}; chunks_transferred={(chunksTransferred?.ToString() ?? "(none)")}; chunk_count={(chunkCount?.ToString() ?? "(none)")}; error_code={errorCode ?? "(none)"}; reason={reason ?? "(none)"}; saved_path={savedPath ?? "(none)"}");
    }

    private void RecordWindowUpdateSent(FileTransferWindowUpdateV2 message, int grantedUntilExclusive, FileTransferDirection direction)
    {
        Interlocked.Increment(ref windowUpdatesSent);
        UpdateMaximum(ref maxGrantedUntilExclusive, grantedUntilExclusive);
        Volatile.Write(ref lastWindowUpdateSendError, null);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_sent; direction={direction.ToString().ToLowerInvariant()}; transfer_id={message.TransferId}; session_id={message.SessionId}; next_expected_chunk_index={message.NextExpectedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; bytes_received={message.BytesReceived}");
    }

    private void RecordWindowUpdateReceived(FileTransferWindowUpdateV2 message, int grantedUntilExclusive, FileTransferDirection direction)
    {
        Interlocked.Increment(ref windowUpdatesReceived);
        UpdateMaximum(ref maxGrantedUntilExclusive, grantedUntilExclusive);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_received; direction={direction.ToString().ToLowerInvariant()}; transfer_id={message.TransferId}; session_id={message.SessionId}; next_expected_chunk_index={message.NextExpectedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; bytes_received={message.BytesReceived}");
    }

    private void RecordMissingRangeSent(
        FileTransferMissingRangeV1 message,
        FileTransferChunkRangeV1 range,
        string triggerReason,
        bool escalated,
        long maxBufferedOutOfOrderBytesDuringGap)
    {
        Interlocked.Increment(ref missingRangeRequestsSent);
        RecordMissingRangeTrigger(triggerReason);
        Volatile.Write(ref lastRepairRange, $"{range.StartChunkIndex}-{range.EndChunkIndexInclusive}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_missing_range_sent; direction=inbound; transfer_id={message.TransferId}; session_id={message.SessionId}; range_start={range.StartChunkIndex}; range_end={range.EndChunkIndexInclusive}; next_expected_chunk_index={message.NextExpectedChunkIndex}; highest_buffered_chunk_index={message.HighestBufferedChunkIndex}; trigger_reason={triggerReason}; max_buffered_out_of_order_bytes_during_gap={maxBufferedOutOfOrderBytesDuringGap}");

        if (!escalated)
        {
            return;
        }

        Interlocked.Increment(ref missingRangeEscalatedCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_missing_range_escalated; direction=inbound; transfer_id={message.TransferId}; session_id={message.SessionId}; range_start={range.StartChunkIndex}; range_end={range.EndChunkIndexInclusive}; trigger_reason={triggerReason}");
    }

    private void RecordMissingRangeReceived(FileTransferMissingRangeV1 message, FileTransferChunkRangeV1 range, int queuedRepairChunks)
    {
        Interlocked.Increment(ref missingRangeRequestsReceived);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_missing_range_received; direction=outbound; transfer_id={message.TransferId}; session_id={message.SessionId}; range_start={range.StartChunkIndex}; range_end={range.EndChunkIndexInclusive}; next_expected_chunk_index={message.NextExpectedChunkIndex}; highest_buffered_chunk_index={message.HighestBufferedChunkIndex}; queued_repair_chunks={queuedRepairChunks}");
    }

    private void RecordMissingRangeTrigger(string triggerReason)
    {
        switch (triggerReason)
        {
            case "time":
                Interlocked.Increment(ref repairTriggerByTimeCount);
                break;
            case "buffer_pressure":
                Interlocked.Increment(ref repairTriggerByBufferPressureCount);
                break;
            case "severe_pressure":
                Interlocked.Increment(ref repairTriggerBySeverePressureCount);
                break;
        }
    }

    private void RecordWindowUpdateCoalesced(InboundTransferContext context, int desiredNextExpectedChunkIndex, int desiredGrantedUntilExclusive)
    {
        Interlocked.Increment(ref windowUpdateCoalesced);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_coalesced; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; desired_next_expected_chunk_index={desiredNextExpectedChunkIndex}; desired_granted_until_exclusive={desiredGrantedUntilExclusive}");
    }

    private void RecordWindowUpdateSendFailure(InboundTransferContext context, Exception ex, bool refresh)
    {
        Interlocked.Increment(ref windowUpdateSendFailures);
        Volatile.Write(ref lastWindowUpdateSendError, $"{ex.GetType().Name}: {ex.Message}");
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_window_update_send_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; refresh={FormatYesNo(refresh)}; desired_next_expected_chunk_index={context.DesiredWindowNextExpectedChunkIndex}; desired_granted_until_exclusive={context.DesiredWindowGrantedUntilExclusive}; ex={ex.GetType().Name}");
    }

    private void RecordWindowUpdateRefreshSent(InboundTransferContext context)
    {
        Interlocked.Increment(ref windowUpdateRefreshResends);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_refresh_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk_index={context.LastWindowSentNextExpectedChunkIndex}; granted_until_exclusive={context.LastWindowSentGrantedUntilExclusive}");
    }

    private void RecordStartupWindowRefreshSent(InboundTransferContext context, int grantedUntilExclusive)
    {
        Interlocked.Increment(ref startupWindowRefreshSent);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_startup_refresh_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk_index={context.NextChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private void RecordWindowUpdateLowWatermarkSent(InboundTransferContext context, FileTransferWindowUpdateV2 message, int grantedUntilExclusive)
    {
        Interlocked.Increment(ref lowWatermarkWindowSends);
        var remainingRunwayChunks = Math.Max(0, context.LastWindowSentGrantedUntilExclusive - message.NextExpectedChunkIndex);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_update_low_watermark_sent; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk_index={message.NextExpectedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; remaining_advertised_runway_chunks={remainingRunwayChunks}; low_watermark_chunks={context.FlowControlPolicy.LowWatermarkChunks}");
    }

    private void RecordBulkClampEntered(OutboundTransferContext context)
    {
        Interlocked.Increment(ref bulkClampEnteredCount);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_clamp_entered; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; mode={context.FlowControlPolicy.Mode}; clamp_duration_ms={(long)CriticalBulkClampDuration.TotalMilliseconds}");
    }

    private void RecordBulkClampReleased(OutboundTransferContext context)
    {
        long elapsedMs;
        lock (gate)
        {
            elapsedMs = context.LastBulkClampDurationMs;
            context.LastBulkClampDurationMs = 0;
            if (elapsedMs <= 0)
            {
                return;
            }
        }

        Interlocked.Increment(ref bulkClampReleasedCount);
        if (elapsedMs > 0)
        {
            Interlocked.Add(ref bulkClampTotalMs, elapsedMs);
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_clamp_released; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; elapsed_ms={elapsedMs}; mode={context.FlowControlPolicy.Mode}");
    }

    private void RecordWindowUpdateSuppressed(InboundTransferContext context, string reason)
    {
        switch (reason)
        {
            case "identical":
                Interlocked.Increment(ref windowUpdateSuppressedIdentical);
                break;
            case "small_delta":
                Interlocked.Increment(ref windowUpdateSuppressedSmallDelta);
                break;
            case "no_extension":
                Interlocked.Increment(ref windowUpdateSuppressedNoExtension);
                break;
            case "tail":
                Interlocked.Increment(ref windowUpdateSuppressedTail);
                break;
        }

        if (string.Equals(reason, "tail", StringComparison.Ordinal))
        {
            return;
        }

        var eventName = reason switch
        {
            "no_extension" => "filetransfer_window_update_suppressed_no_extension",
            "tail" => "filetransfer_window_update_suppressed_tail",
            _ => "filetransfer_window_update_suppressed",
        };

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}; next_expected_chunk_index={context.NextChunkIndex}; desired_granted_until_exclusive={context.DesiredWindowGrantedUntilExclusive}; last_window_sent_next_expected_chunk_index={context.LastWindowSentNextExpectedChunkIndex}; last_window_sent_granted_until_exclusive={context.LastWindowSentGrantedUntilExclusive}");
    }

    private void RecordMissingRangeSendFailure(
        InboundTransferContext context,
        FileTransferChunkRangeV1 requestedRange,
        string triggerReason,
        bool escalated,
        long maxBufferedOutOfOrderBytesDuringGap,
        Exception ex)
    {
        Interlocked.Increment(ref missingRangeSendFailures);
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_missing_range_send_failed; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; range_start={requestedRange.StartChunkIndex}; range_end={requestedRange.EndChunkIndexInclusive}; trigger_reason={triggerReason}; escalated={FormatYesNo(escalated)}; max_buffered_out_of_order_bytes_during_gap={maxBufferedOutOfOrderBytesDuringGap}; ex={ex.GetType().Name}");
    }

    private void RecordDuplicateChunkReceived(InboundTransferContext context, int chunkIndex)
    {
        Interlocked.Increment(ref duplicateChunksReceived);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_duplicate_chunk_ignored; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; next_expected_chunk_index={context.NextChunkIndex}");
    }

    private void RecordRepairChunkResent(OutboundTransferContext context, int chunkIndex)
    {
        Interlocked.Increment(ref repairChunksResent);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_repair_chunk_resent; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; remote_next_expected_chunk_index={context.RemoteNextExpectedChunkIndex}");
    }

    private void LogWindowWaitStarted(OutboundTransferContext context, int nextChunkIndexToSchedule)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_window_wait_started; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_index={nextChunkIndexToSchedule}; remote_granted_until_exclusive={context.RemoteGrantedUntilExclusive}");
    }

    private void RecordWindowWaitReleased(OutboundTransferContext context, int nextChunkIndexToSchedule, bool timedOut)
    {
        long elapsedMs;

        lock (gate)
        {
            if (!context.IsWaitingForWindow)
            {
                return;
            }

            context.IsWaitingForWindow = false;
            context.WindowUpdateSignal = null;
            var startedTimestamp = context.WindowWaitStartedTimestamp;
            context.WindowWaitStartedTimestamp = 0;
            elapsedMs = startedTimestamp <= 0
                ? 0
                : (long)Math.Max(0d, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        }

        Interlocked.Exchange(ref senderWaitingForWindow, 0);
        Interlocked.Add(ref totalWindowWaitMs, elapsedMs);

        LocalOperationalLog.Info(
            "FileTransferService",
            timedOut
                ? $"event=filetransfer_window_timeout; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_index={nextChunkIndexToSchedule}; elapsed_ms={elapsedMs}; remote_granted_until_exclusive={context.RemoteGrantedUntilExclusive}"
                : $"event=filetransfer_window_wait_released; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_index={nextChunkIndexToSchedule}; elapsed_ms={elapsedMs}; remote_granted_until_exclusive={context.RemoteGrantedUntilExclusive}");
    }

    private static TimeoutException CreateWindowUpdateTimeoutException(string transferId, int nextChunkIndexToSchedule)
    {
        var exception = new TimeoutException("Receiver window update was not received in time.");
        exception.Data["nlink_reason"] = WindowTimeoutErrorCode;
        exception.Data["nlink_transfer_id"] = transferId;
        exception.Data["nlink_next_chunk_index"] = nextChunkIndexToSchedule;
        return exception;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static void MaybeLogProgressMilestone(OutboundTransferContext context, FileTransferDirection direction)
    {
        var nextProgressMilestonePercent = context.NextProgressMilestonePercent;
        MaybeLogProgressMilestone(
            direction,
            context.TransferId,
            context.SessionId,
            context.FileName,
            context.FileSizeBytes,
            context.BytesTransferred,
            context.ChunksTransferred,
            context.ChunkCount,
            ref nextProgressMilestonePercent);
        context.NextProgressMilestonePercent = nextProgressMilestonePercent;
    }

    private static void MaybeLogProgressMilestone(InboundTransferContext context, FileTransferDirection direction)
    {
        var nextProgressMilestonePercent = context.NextProgressMilestonePercent;
        MaybeLogProgressMilestone(
            direction,
            context.TransferId,
            context.SessionId,
            context.FileName,
            context.FileSizeBytes,
            context.BytesTransferred,
            context.ChunksTransferred,
            context.ChunkCount,
            ref nextProgressMilestonePercent);
        context.NextProgressMilestonePercent = nextProgressMilestonePercent;
    }

    private static void MaybeLogProgressMilestone(
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        long bytesTransferred,
        int chunksTransferred,
        int chunkCount,
        ref int nextProgressMilestonePercent)
    {
        if (fileSizeBytes <= 0 || bytesTransferred <= 0 || nextProgressMilestonePercent > 100)
        {
            return;
        }

        var percentComplete = (int)Math.Floor((double)bytesTransferred * 100d / fileSizeBytes);
        if (percentComplete < nextProgressMilestonePercent)
        {
            return;
        }

        LogTransferInfo(
            "progress_milestone",
            direction,
            transferId,
            sessionId: sessionId,
            fileName: fileName,
            fileSizeBytes: fileSizeBytes,
            bytesTransferred: bytesTransferred,
            chunksTransferred: chunksTransferred,
            chunkCount: chunkCount,
            reason: $"percent={Math.Min(percentComplete, 100)}");

        while (nextProgressMilestonePercent <= percentComplete)
        {
            nextProgressMilestonePercent += 25;
        }
    }

    private sealed class OutboundTransferContext
    {
        public FileTransferSendDescriptor Descriptor { get; }

        public FileTransferReadStreamFactory OpenReadStreamAsync { get; }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public string SessionId { get; set; } = string.Empty;

        public string TransferId => Descriptor.TransferId!;

        public string FileName => Descriptor.FileName;

        public long FileSizeBytes => Descriptor.FileSizeBytes;

        public int ChunkSizeBytes { get; set; }

        public int ChunkCount { get; set; }

        public string? Sha256Base64 { get; set; }

        public long BytesTransferred { get; set; }

        public int ChunksTransferred { get; set; }

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.Offering;

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Preparing transfer offer.";

        public bool SendStarted { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public FileTransferCompleteV1? PendingRemoteComplete { get; set; }

        public FileTransferFlowControlPolicy FlowControlPolicy { get; set; }

        public bool RemoteWindowInitialized { get; set; }

        public int RemoteNextExpectedChunkIndex { get; set; }

        public int RemoteGrantedUntilExclusive { get; set; }

        public TaskCompletionSource<bool>? WindowUpdateSignal { get; set; }

        public TaskCompletionSource<bool>? BulkClampSignal { get; set; }

        public SortedDictionary<int, byte[]> SentChunkCache { get; } = new();

        public Queue<int> PendingRepairChunkIndexes { get; } = new();

        public HashSet<int> PendingRepairChunkIndexSet { get; } = [];

        public bool IsWaitingForWindow { get; set; }

        public long WindowWaitStartedTimestamp { get; set; }

        public bool BulkClampActive { get; set; }

        public long BulkClampStartedTimestamp { get; set; }

        public long BulkClampUntilTimestamp { get; set; }

        public long LastBulkClampDurationMs { get; set; }

        public bool IsTerminal => ToSnapshot().IsTerminal;

        public FileTransferTransferSnapshot ToSnapshot()
            => new(
                SessionId,
                TransferId,
                FileTransferDirection.Outbound,
                State,
                FileName,
                FileSizeBytes,
                Sha256Base64,
                BytesTransferred,
                ChunksTransferred,
                ChunkCount,
                ChunkSizeBytes,
                ErrorCode,
                StatusMessage);

        public OutboundTransferContext(FileTransferSendDescriptor descriptor, FileTransferReadStreamFactory openReadStreamAsync, FileTransferFlowControlPolicy flowControlPolicy)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            OpenReadStreamAsync = openReadStreamAsync ?? throw new ArgumentNullException(nameof(openReadStreamAsync));
            ChunkSizeBytes = descriptor.ChunkSizeBytes ?? FileTransferProtocol.MaxChunkRawBytes;
            FlowControlPolicy = FileTransferFlowControlPolicy.Normalize(flowControlPolicy);
        }

        public void CancelLifetime()
        {
            try
            {
                LifetimeCts.Cancel();
            }
            catch
            {
            }
        }

        public void DisposeResources()
        {
            BulkClampSignal?.TrySetResult(true);
            try
            {
                LifetimeCts.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed record OutboundChunkSendWork(
        int ChunkIndex,
        int ChunkCount,
        int ChunkSizeBytes,
        int BytesRead,
        byte[] ChunkBytes,
        FileTransferChunkV1 Message,
        bool IsRepairResend,
        Task SendTask);

    private sealed record InboundDispatchWork(
        SessionFileTransferService Service,
        string Operation,
        Func<Task> Work);

    private sealed class InboundTransferContext
    {
        public InboundTransferContext(FileTransferOfferV1 offer, FileTransferFlowControlPolicy flowControlPolicy)
        {
            ArgumentNullException.ThrowIfNull(offer);
            SessionId = offer.SessionId;
            TransferId = offer.TransferId;
            FileName = offer.FileName;
            FileSizeBytes = offer.FileSizeBytes;
            Sha256Base64 = offer.Sha256Base64;
            FlowControlPolicy = FileTransferFlowControlPolicy.Normalize(flowControlPolicy);
        }

        public CancellationTokenSource LifetimeCts { get; } = new();

        public string SessionId { get; }

        public string TransferId { get; }

        public string FileName { get; }

        public long FileSizeBytes { get; }

        public string Sha256Base64 { get; }

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.PendingDecision;

        public long BytesTransferred { get; set; }

        public int ChunksTransferred { get; set; }

        public int ChunkCount { get; set; }

        public int ChunkSizeBytes { get; set; }

        public int NextChunkIndex { get; set; }

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Incoming transfer offer pending.";

        public string? SavedFilePath { get; set; }

        public string? SavedDirectoryPath { get; set; }

        public string? SavedFileName { get; set; }

        public bool AcceptInProgress { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public bool HasAdvertisedWindowUpdate { get; set; }

        public int LastAdvertisedNextExpectedChunkIndex { get; set; } = -1;

        public int LastAdvertisedGrantedUntilExclusive { get; set; }

        public int DesiredWindowNextExpectedChunkIndex { get; set; } = -1;

        public int DesiredWindowGrantedUntilExclusive { get; set; }

        public int LastWindowSentNextExpectedChunkIndex { get; set; } = -1;

        public int LastWindowSentGrantedUntilExclusive { get; set; }

        public bool WindowUpdatePumpRunning { get; set; }

        public bool WindowUpdateSendRequested { get; set; }

        public bool WindowUpdateForceRequested { get; set; }

        public bool WindowUpdateRefreshRequested { get; set; }

        public bool WindowUpdateRefreshScheduled { get; set; }

        public long LastWindowProgressTimestamp { get; set; }

        public bool StartupWindowRefreshEnabled { get; set; }

        public int StartupWindowRefreshGrantedUntilExclusive { get; set; }

        public FileTransferFlowControlMode StartupPolicyMode { get; set; } = FileTransferFlowControlMode.Interactive;

        public int HighestBufferedChunkIndex { get; set; } = -1;

        public long BufferedOutOfOrderBytes { get; set; }

        public int OldestGapChunkIndex { get; set; } = -1;

        public long OldestGapFirstSeenTimestamp { get; set; }

        public long CurrentGapMaxBufferedOutOfOrderBytes { get; set; }

        public bool MissingRangeProbeScheduled { get; set; }

        public long LastMissingRangeRequestTimestamp { get; set; }

        public int LastRequestedMissingRangeStart { get; set; } = -1;

        public int LastRequestedMissingRangeEnd { get; set; } = -1;

        public long LastRequestedMissingRangeSentTimestamp { get; set; }

        public FileTransferFlowControlPolicy FlowControlPolicy { get; set; }

        public FileTransferReceiveDestination? WriteDestination { get; set; }

        public Stream? WriteStream { get; set; }

        public IncrementalHash? Hash { get; set; }

        public SortedDictionary<int, byte[]> PendingChunks { get; } = new();

        public bool IsTerminal => ToSnapshot().IsTerminal;

        public FileTransferIncomingOffer CreateOffer()
            => new(SessionId, TransferId, FileName, FileSizeBytes, Sha256Base64);

        public FileTransferTransferSnapshot ToSnapshot()
            => new(
                SessionId,
                TransferId,
                FileTransferDirection.Inbound,
                State,
                FileName,
                FileSizeBytes,
                Sha256Base64,
                BytesTransferred,
                ChunksTransferred,
                ChunkCount,
                ChunkSizeBytes,
                ErrorCode,
                StatusMessage,
                SavedFilePath,
                SavedDirectoryPath,
                SavedFileName);

        public void CancelLifetime()
        {
            try
            {
                LifetimeCts.Cancel();
            }
            catch
            {
            }
        }

        public void DisposeResources()
        {
            try
            {
                WriteDestination?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (WriteDestination is null)
                {
                    WriteStream?.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                Hash?.Dispose();
            }
            catch
            {
            }

            try
            {
                LifetimeCts.Dispose();
            }
            catch
            {
            }

            WriteDestination = null;
            WriteStream = null;
            Hash = null;
        }
    }
}
