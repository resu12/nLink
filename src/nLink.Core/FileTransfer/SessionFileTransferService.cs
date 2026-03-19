using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed class SessionFileTransferService : IDisposable
{
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
    private const string TransportIncompatibleErrorCode = FileTransferResultCodes.TransportIncompatible;
    private const string WindowTimeoutErrorCode = FileTransferResultCodes.WindowTimeout;
    private const string PayloadBudgetExceededErrorCode = FileTransferResultCodes.PayloadBudgetExceeded;
    private const string StreamWriteFailedErrorCode = FileTransferResultCodes.WriteFailed;
    private const string FinalizeFailedErrorCode = FileTransferResultCodes.FinalizeFailed;
    private const string PullSessionStalledErrorCode = FileTransferResultCodes.PullSessionStalled;
    private const int WindowUpdateWatchdogDelayMs = 500;
    private const int MissingRangeWatchdogDelayMs = 100;
    private const int MissingRangeTriggerDelayMs = 100;
    private const int MissingRangeCooldownMs = 250;
    private const int RepeatedMissingRangeMinIntervalMs = 500;
    private const int MissingRangeMaxChunks = 8;
    private const int RepairResendIntervalMs = 150;
    private const int RepairAckTimeoutMs = 750;
    private const int RepairBatchSize = 2;
    private const int DegradedRepairGrantChunks = 32;
    private const int DegradedRepairLowWatermarkChunks = 8;
    private const int DegradedRepairGapPersistenceMs = 500;
    private const int DegradedRepairMissingRangeBurstThreshold = 3;
    private const int DegradedRepairMissingRangeBurstWindowMs = 1000;
    private const int DegradedRepairRecoveryHoldMs = 1000;
    private const int BulkFallbackGrantChunks = 8;
    private const int BulkFallbackLowWatermarkChunks = 2;
    private const int BulkUnhealthyDetectionWindowMs = 1000;
    private const int BulkUnhealthyDetectionConfirmations = 2;
    private const int BulkFallbackRecoveryHoldMs = 2000;
    private const int PressureStateRecoveryHoldMs = 2000;
    private const int PressureStateNormalSuggestedSendAheadChunks = 32;
    private const int PressureStateCatchUpOnlySuggestedSendAheadChunks = 2;
    private const int PressureStateCatchUpOnlySuggestedSendAheadWhileScreenshareChunks = 1;
    private const int PressureStateObsoleteChunkThreshold = 4;
    private const double PressureStateObsoleteChunkRatioThreshold = 0.25;
    private const int PressureStateBulkDispatchThreshold = 4;
    private const int PressureStateNoProgressWindowMs = 1500;
    private const int PressuredWatchdogResendMinIntervalMs = 1000;
    private const int RepairChurnDetectionWindowMs = 2000;
    private const int RepairChurnMissingRangeThreshold = 3;
    private const int RepairChurnMaxHealthyProgressChunks = 6;
    private const int LocalSendAheadClampChunks = 32;
    private const int LocalSendAheadClampDegradedChunks = 8;
    private const int LocalSendAheadClampDegradedWhileScreenshareChunks = 4;
    private const int ScreenShareActiveStartupGrantCapChunks = 32;
    private const int PullHealthyPipelineDepth = 6;
    private const int PullHealthyMinimumPipelineDepth = 2;
    private const int PullHealthyLowWatermarkChunks = 1;
    private const int PullHealthyTargetInFlightBytes = 144 * 1024;
    private const int PullHealthyLowWatermarkBytes = 24 * 1024;
    private const int PullHealthyMaximumPipelineDepthCap = 6;
    private const int PullScreensharePipelineDepth = 3;
    private const int PullScreenshareLowWatermarkChunks = 2;
    private const int PullDegradedPipelineDepth = 4;
    private const int PullDegradedLowWatermarkChunks = 1;
    private const int PullDegradedScreensharePipelineDepth = 1;
    private const int PullDegradedScreenshareLowWatermarkChunks = 0;
    private const int PullHealthyDefaultChunkSizeBytes = 24576;
    private const int PullScreenshareDefaultChunkSizeBytes = 24576;
    private const int PullDegradedDefaultChunkSizeBytes = 8192;
    private const int PullDegradedScreenshareDefaultChunkSizeBytes = 2048;
    private const int PullSessionHealthyRequestTimeoutMs = 3000;
    private const int PullSessionDegradedRequestTimeoutMs = 5000;
    private const int PullSessionHealthyRetryResendGateMs = 1000;
    private const int PullSessionDegradedRetryResendGateMs = 1500;
    private const int PullSessionReceivePollDelayMs = 250;
    private const int PullSessionTransportRecoveryGraceMs = 15000;
    private const int PullSessionRecoveryHoldMs = 5000;
    private const int PullHealthyReorderStepDownHoldMs = 1500;
    private const int PullSessionFirstChunkStallTimeouts = 3;
    private const int PullSessionDegradedEntryTimeoutStreakThreshold = 2;
    private const int PullSessionHealthyAckCoalesceDelayMs = 1000;
    private const int PullSessionScreenshareAckCoalesceDelayMs = 250;
    private const int PullSessionDegradedAckCoalesceDelayMs = 500;
    private const int PullControlChatterWindowMs = 2000;
    private const int PullLateArrivalDistanceThreshold = 4;
    private const int PullGapFocusBufferedThreshold = 3;
    private const int PullTimeoutOutstandingStepDownThreshold = 4;
    private const int PullProfileAdjustmentCooldownMs = 500;
    private const int PullHealthyBundledRawBytesCap = 48 * 1024;
    private static readonly TimeSpan OutboundWindowTimeout = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly object inboundDispatchGate = new();
    private readonly object inboundChunkDispatchGate = new();
    private readonly Func<string> transferIdFactory;
    private IFileTransferSignalingTransport? transport;
    private ISignalingTransport? transportLifecycle;
    private OutboundTransferContext? outboundTransfer;
    private InboundTransferContext? inboundTransfer;
    private Task inboundDispatchTail = Task.CompletedTask;
    private Task inboundChunkDispatchTail = Task.CompletedTask;
    private FileTransferFlowControlPolicy flowControlPolicy = FileTransferFlowControlPolicy.ForMode(FileTransferFlowControlMode.Background);
    private bool sessionScreenShareActive;
    private bool sessionScreenShareDegraded;
    private bool disposed;

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

    public bool IsTransferDegraded
    {
        get
        {
            lock (gate)
            {
                return IsTransferDegradedLocked();
            }
        }
    }

    public bool IsCatchUpOnlyPressureActive
    {
        get
        {
            lock (gate)
            {
                return IsCatchUpOnlyPressureActiveLocked();
            }
        }
    }

    internal void SetFlowControlMode(FileTransferFlowControlMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        var startupPhasePending = false;
        var nextPolicy = FileTransferFlowControlPolicy.ForMode(mode);

        lock (gate)
        {
            if (flowControlPolicy == nextPolicy)
            {
                return;
            }

            flowControlPolicy = nextPolicy;
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying &&
                !inboundTransfer.PullSessionActive)
            {
                receivingContext = inboundTransfer;
                startupPhasePending = !inboundTransfer.StartupPhaseCompleted;
            }
        }

        if (receivingContext is not null)
        {
            _ = SendWindowUpdateAsync(
                receivingContext,
                startupPhasePending ? WindowUpdateTrigger.StartupResend : WindowUpdateTrigger.SteadyStateResend,
                CancellationToken.None);
        }
    }

    internal void SetSessionScreenShareActive(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        var startupPhasePending = false;
        OutboundTransferContext? activeOutboundContext = null;
        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;

        lock (gate)
        {
            if (sessionScreenShareActive == active)
            {
                return;
            }

            sessionScreenShareActive = active;
            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                activeOutboundContext = outboundTransfer;
                UpdateOutboundPressureDerivedStateLocked(outboundTransfer);
                outboundTransfer.PullCurrentPipelineDepth = ResolveOutboundInitialPipelineDepth(outboundTransfer);
            }
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying)
            {
                receivingContext = inboundTransfer;
                startupPhasePending = !inboundTransfer.StartupPhaseCompleted;
                previousInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                inboundTransfer.PullCurrentPipelineDepth = Math.Min(
                    inboundTransfer.PullCurrentPipelineDepth > 0 ? inboundTransfer.PullCurrentPipelineDepth : ResolveInboundMaximumPipelineDepthLocked(inboundTransfer),
                    ResolveInboundMaximumPipelineDepthLocked(inboundTransfer));
                updatedInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                if (inboundTransfer.PullSessionActive)
                {
                    activeInboundPullContext = inboundTransfer;
                    inboundTransfer.PullRecoverySinceUtc = null;
                }
            }
        }

        if (receivingContext is not null && startupPhasePending)
        {
            _ = SendWindowUpdateAsync(receivingContext, WindowUpdateTrigger.StartupResend, CancellationToken.None);
        }

        if (activeOutboundContext is not null)
        {
            lock (gate)
            {
                if (ReferenceEquals(outboundTransfer, activeOutboundContext) && !activeOutboundContext.IsTerminal)
                {
                    activeOutboundContext.SignalControlActivity();
                }
            }
        }

        if (activeInboundPullContext is not null)
        {
            LogPullProfileClampForScreenshare(
                activeInboundPullContext.TransferId,
                activeInboundPullContext.SessionId,
                active ? "active" : "inactive",
                activeInboundPullContext.ChunkSizeBytes,
                updatedInboundPipelineDepth);

            if (updatedInboundPipelineDepth != previousInboundPipelineDepth)
            {
                LogPullPipelineChanged(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    FileTransferDirection.Inbound,
                    updatedInboundPipelineDepth,
                    activeInboundPullContext.PullSessionDegraded || sessionScreenShareDegraded);
            }

            _ = MaybeSendNextChunkRequestAsync(activeInboundPullContext, forceResendOldestOutstanding: false);
        }
    }

    internal void SetSessionScreenShareDegraded(bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        InboundTransferContext? receivingContext = null;
        InboundTransferContext? activeInboundPullContext = null;
        int previousInboundPipelineDepth = 0;
        int updatedInboundPipelineDepth = 0;
        lock (gate)
        {
            if (sessionScreenShareDegraded == active)
            {
                return;
            }

            sessionScreenShareDegraded = active;
            if (inboundTransfer is not null &&
                !inboundTransfer.IsTerminal &&
                inboundTransfer.State is FileTransferTransferState.Receiving or FileTransferTransferState.Verifying)
            {
                receivingContext = inboundTransfer;
                inboundTransfer.PullSessionDegraded = active || inboundTransfer.PullSessionDegraded;
                previousInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                inboundTransfer.PullCurrentPipelineDepth = Math.Min(
                    inboundTransfer.PullCurrentPipelineDepth > 0 ? inboundTransfer.PullCurrentPipelineDepth : ResolveInboundMaximumPipelineDepthLocked(inboundTransfer),
                    ResolveInboundMaximumPipelineDepthLocked(inboundTransfer));
                updatedInboundPipelineDepth = inboundTransfer.PullCurrentPipelineDepth;
                if (inboundTransfer.PullSessionActive)
                {
                    activeInboundPullContext = inboundTransfer;
                    inboundTransfer.PullRecoverySinceUtc = null;
                }
            }
            if (outboundTransfer is not null && !outboundTransfer.IsTerminal)
            {
                outboundTransfer.PullSessionDegraded = active;
            }
        }

        if (receivingContext is not null)
        {
            _ = MaybeSendPressureStateAsync(receivingContext, CancellationToken.None);
        }

        if (activeInboundPullContext is not null)
        {
            if (active)
            {
                LogPullProfileClampForScreenshare(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    "degraded",
                    activeInboundPullContext.ChunkSizeBytes,
                    updatedInboundPipelineDepth);
            }
            else
            {
                LogPullProfileRecoveredAfterScreenshare(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    activeInboundPullContext.ChunkSizeBytes,
                    updatedInboundPipelineDepth);
            }

            if (updatedInboundPipelineDepth != previousInboundPipelineDepth)
            {
                LogPullPipelineChanged(
                    activeInboundPullContext.TransferId,
                    activeInboundPullContext.SessionId,
                    FileTransferDirection.Inbound,
                    updatedInboundPipelineDepth,
                    activeInboundPullContext.PullSessionDegraded || sessionScreenShareDegraded);
            }

            _ = MaybeSendNextChunkRequestAsync(activeInboundPullContext, forceResendOldestOutstanding: false);
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
        transport.FileTransferSessionOpenReceived += OnFileTransferSessionOpenReceived;
        transport.FileTransferStartReceived += OnFileTransferStartReceived;
        transport.FileTransferChunkReceived += OnFileTransferChunkReceived;
        transport.FileTransferWindowUpdateReceived += OnFileTransferWindowUpdateReceived;
        transport.FileTransferMissingRangeReceived += OnFileTransferMissingRangeReceived;
        transport.FileTransferPressureStateReceived += OnFileTransferPressureStateReceived;
        transport.FileTransferCancelReceived += OnFileTransferCancelReceived;
        transport.FileTransferErrorReceived += OnFileTransferErrorReceived;
        transport.FileTransferCompleteReceived += OnFileTransferCompleteReceived;

        if (transport is ISignalingTransport lifecycleTransport)
        {
            transportLifecycle = lifecycleTransport;
            lifecycleTransport.Rejected += OnTransportRejectedOrDisconnected;
            lifecycleTransport.Disconnected += OnTransportRejectedOrDisconnected;
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

            context = new OutboundTransferContext(normalizedDescriptor, openReadStreamAsync);
            outboundTransfer = context;
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
                errorCode: ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode),
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
                errorCode: ClassifyOutboundFailureErrorCode(ex, InvalidStateErrorCode),
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
            context.StatusMessage = "Waiting for sender session.";
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
            LogPullChunkProfile(
                context.TransferId,
                context.SessionId,
                context.ChunkSizeBytes,
                pipelineDepth: ResolveOutboundInitialPipelineDepth(),
                screenshareActive: sessionScreenShareActive,
                screenshareDegraded: sessionScreenShareDegraded);
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

    private async void OnFileTransferSessionOpenReceived(object? sender, FileTransferSessionOpenReceivedEventArgs e)
        => EnqueueInboundDispatch("session_open", () => HandleIncomingSessionOpenAsync(e.Message));

    private async void OnFileTransferStartReceived(object? sender, FileTransferStartReceivedEventArgs e)
        => EnqueueInboundDispatch("start", () => HandleIncomingStartAsync(e.Message));

    private async void OnFileTransferChunkReceived(object? sender, FileTransferChunkReceivedEventArgs e)
        => EnqueueInboundChunkDispatch("chunk", () => HandleIncomingChunkAsync(e.Message));

    private async void OnFileTransferWindowUpdateReceived(object? sender, FileTransferWindowUpdateReceivedEventArgs e)
        => EnqueueInboundDispatch("window_update", () => HandleIncomingWindowUpdateAsync(e.Message));

    private async void OnFileTransferMissingRangeReceived(object? sender, FileTransferMissingRangeReceivedEventArgs e)
        => EnqueueInboundDispatch("missing_range", () => HandleIncomingMissingRangeAsync(e.Message));

    private async void OnFileTransferPressureStateReceived(object? sender, FileTransferPressureStateReceivedEventArgs e)
        => EnqueueInboundDispatch("pressure_state", () => HandleIncomingPressureStateAsync(e.Message));

    private async void OnFileTransferCancelReceived(object? sender, FileTransferCancelReceivedEventArgs e)
        => EnqueueInboundDispatch("cancel", () => HandleIncomingCancelAsync(e.Message));

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

    private void EnqueueInboundChunkDispatch(string operation, Func<Task> work)
    {
        Task lifecycleBarrier;
        lock (inboundDispatchGate)
        {
            lifecycleBarrier = inboundDispatchTail;
        }

        lock (inboundChunkDispatchGate)
        {
            inboundChunkDispatchTail = Task.WhenAll(inboundChunkDispatchTail, lifecycleBarrier)
                .ContinueWith(
                    static async (_, state) =>
                    {
                        var dispatch = (InboundDispatchWork)state!;
                        await dispatch.Service.RunInboundDispatchAsync(dispatch.Operation, dispatch.Work).ConfigureAwait(false);
                    },
                    new InboundDispatchWork(this, operation, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

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
        string? outboundPausedTransferId = null;
        string? outboundPausedSessionId = null;
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        lock (gate)
        {
            outbound = outboundTransfer is { IsTerminal: false } ? outboundTransfer : null;
            inbound = inboundTransfer is { IsTerminal: false } ? inboundTransfer : null;

            if (outbound is not null &&
                (TryPauseOutboundTransportLocked(outbound, "transport_disconnected", true) || outbound.PullTransportPaused))
            {
                if (outboundPausedTransferId is null)
                {
                    outboundPausedTransferId = outbound.TransferId;
                    outboundPausedSessionId = outbound.SessionId;
                }

                outbound = null;
            }

            if (inbound is not null &&
                (TryPauseInboundTransportLocked(inbound, "transport_disconnected", true) || inbound.PullTransportPaused))
            {
                if (inboundPausedTransferId is null)
                {
                    inboundPausedTransferId = inbound.TransferId;
                    inboundPausedSessionId = inbound.SessionId;
                }

                inbound = null;
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, "transport_disconnected");
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, "transport_disconnected");
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
        var incoming = new InboundTransferContext(message);

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
        _ = RunOutboundPullSendLoopAsync(context);
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

    private async Task HandleIncomingSessionOpenAsync(FileTransferSessionOpenV2 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        InboundTransferContext? context;
        lock (gate)
        {
            context = inboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.State != FileTransferTransferState.AwaitingStart ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal) ||
                !string.Equals(message.SessionRole, FileTransferProtocol.SessionRoleSender, StringComparison.Ordinal))
            {
                context = null;
            }
        }

        if (context is null)
        {
            return;
        }

        try
        {
            var session = await GetTransportOrThrow()
                .OpenFileTransferDataSessionAsync(message.SessionId, message.TransferId, context.LifetimeCts.Token)
                .ConfigureAwait(false);

            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                {
                    session.Dispose();
                    return;
                }

                ReplaceInboundDataSessionLocked(context, session);
                context.PullSessionActive = true;
                context.PullSessionDegraded = sessionScreenShareDegraded;
                context.PullCurrentPipelineDepth = ResolveInboundMaximumPipelineDepthLocked(context);
                context.StatusMessage = "Negotiating file-transfer session.";
            }

            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Inbound,
                message.TransferId,
                sessionId: message.SessionId,
                reason: $"role={message.SessionRole}; chunk_size_bytes={message.ChunkSizeBytes}; pipeline_depth={message.InitialPipelineDepth}");
            LogPullChunkProfile(
                message.TransferId,
                message.SessionId,
                message.ChunkSizeBytes,
                context.PullCurrentPipelineDepth,
                screenshareActive: sessionScreenShareActive,
                screenshareDegraded: sessionScreenShareDegraded);
            _ = RunInboundPullReceiveLoopAsync(context, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: InvalidStateErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: "Could not open the dedicated file-transfer session.",
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnDataSessionAvailabilityChanged(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
    {
        if (sender is not IFileTransferDataSession dataSession)
        {
            return;
        }

        EnqueueInboundDispatch(
            "filetransfer data session availability changed",
            () => HandleDataSessionAvailabilityChangedAsync(dataSession, e));
    }

    private async Task HandleDataSessionAvailabilityChangedAsync(
        IFileTransferDataSession dataSession,
        FileTransferDataSessionAvailabilityChangedEventArgs availability)
    {
        OutboundTransferContext? outboundToResume = null;
        InboundTransferContext? inboundToResume = null;
        string? outboundPausedTransferId = null;
        string? outboundPausedSessionId = null;
        string? inboundPausedTransferId = null;
        string? inboundPausedSessionId = null;
        bool outboundResumed = false;
        bool inboundResumed = false;

        lock (gate)
        {
            if (ReferenceEquals(outboundTransfer?.DataSession, dataSession) &&
                outboundTransfer is { IsTerminal: false } outbound)
            {
                if (availability.IsAvailable)
                {
                    outboundResumed = TryResumeOutboundTransportLocked(outbound, availability.Reason, availability.RequiresResumeRequest);
                    if (outboundResumed)
                    {
                        outboundToResume = outbound;
                    }
                }
                else if (TryPauseOutboundTransportLocked(outbound, availability.Reason, availability.RequiresResumeRequest))
                {
                    outboundPausedTransferId = outbound.TransferId;
                    outboundPausedSessionId = outbound.SessionId;
                }
            }

            if (ReferenceEquals(inboundTransfer?.DataSession, dataSession) &&
                inboundTransfer is { IsTerminal: false } inbound)
            {
                if (availability.IsAvailable)
                {
                    inboundResumed = TryResumeInboundTransportLocked(inbound, availability.Reason, availability.RequiresResumeRequest);
                    if (inboundResumed)
                    {
                        inboundToResume = inbound;
                    }
                }
                else if (TryPauseInboundTransportLocked(inbound, availability.Reason, availability.RequiresResumeRequest))
                {
                    inboundPausedTransferId = inbound.TransferId;
                    inboundPausedSessionId = inbound.SessionId;
                }
            }
        }

        if (outboundPausedTransferId is not null && outboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Outbound, outboundPausedTransferId, outboundPausedSessionId, availability.Reason);
        }

        if (inboundPausedTransferId is not null && inboundPausedSessionId is not null)
        {
            LogTransportPaused(FileTransferDirection.Inbound, inboundPausedTransferId, inboundPausedSessionId, availability.Reason);
        }

        if (outboundResumed && outboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Outbound, outboundToResume.TransferId, outboundToResume.SessionId, availability.Reason, availability.RequiresResumeRequest);
        }

        if (inboundResumed && inboundToResume is not null)
        {
            LogTransportResumed(FileTransferDirection.Inbound, inboundToResume.TransferId, inboundToResume.SessionId, availability.Reason, availability.RequiresResumeRequest);
            if (availability.RequiresResumeRequest)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_transport_rebind_started; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}");
                try
                {
                    await MaybeSendNextChunkRequestAsync(inboundToResume, forceResendOldestOutstanding: true).ConfigureAwait(false);
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_succeeded; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_transport_rebind_failed; direction=inbound; transfer_id={inboundToResume.TransferId}; session_id={inboundToResume.SessionId}; reason={availability.Reason}; error={ex.Message}");
                    await TransitionInboundToTerminalAsync(
                        inboundToResume,
                        FileTransferTransferState.Failed,
                        errorCode: DisconnectedErrorCode,
                        statusMessage: "Transport disconnected.",
                        sendError: true,
                        errorMessage: "Transport disconnected.",
                        cancelReason: null,
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
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
                context.BufferedBytes = 0;
                context.HighestBufferedChunkIndex = -1;
                context.LastAdvertisedGrantedUntilExclusive = 0;
                context.LastAdvertisedNextChunkIndex = 0;
                context.LastAdvertisedCreditFrontier = 0;
                context.LastWindowUpdateSentUtc = null;
                context.LastForcedWindowUpdateSentUtc = null;
                context.LastContiguousProgressUtc = DateTimeOffset.UtcNow;
                context.LastBufferedFrontierAdvanceUtc = DateTimeOffset.UtcNow;
                context.LastUsefulBulkProgressUtc = DateTimeOffset.UtcNow;
                context.LastBulkDispatchedChunkUtc = null;
                context.BulkDispatchedChunksSinceLastUsefulProgress = 0;
                context.ObsoleteChunksArrivedSinceLastUsefulProgress = 0;
                context.BulkUnhealthyDetected = false;
                context.BulkFallbackModeActive = false;
                context.ConsecutiveBulkUnhealthyDetections = 0;
                context.LastBulkUnhealthyLogUtc = null;
                context.StartupPhaseCompleted = false;
                context.SteadyStateWindowAdvertised = false;
                context.OldestGapStartChunkIndex = null;
                context.OldestGapFirstSeenUtc = null;
                context.OutstandingMissingRange = null;
                context.LastMissingRangeSentUtc = null;
                context.RecentMissingRangeSentUtc.Clear();
                context.RecentGapProgressAckSentUtc.Clear();
                context.RecentContiguousProgressChunkUtc.Clear();
                context.RecentObsoleteChunkArrivalUtc.Clear();
                context.PendingChunks.Clear();
                context.BytesTransferred = 0;
                context.ChunksTransferred = 0;
                context.State = FileTransferTransferState.Receiving;
                context.StatusMessage = "Receiving file data.";
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
            _ = SendWindowUpdateAsync(context, WindowUpdateTrigger.Startup, CancellationToken.None);
            _ = RunInboundWindowRefreshWatchdogAsync(context);
            _ = RunInboundGapRecoveryWatchdogAsync(context);
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

        byte[] chunkBytes;
        try
        {
            chunkBytes = Convert.FromBase64String(message.DataBase64);
        }
        catch (FormatException)
        {
            chunkBytes = Array.Empty<byte>();
        }

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
        bool duplicateIgnored = false;
        bool shouldLogDeferredGapExtension = false;
        int deferredGapHighestBufferedChunkIndex = -1;
        int deferredGapGrantedUntilExclusive = 0;
        WindowUpdateTrigger? windowUpdateTrigger = null;
        bool shouldRequestMissingRange = false;
        bool shouldLogStartupCompleted = false;
        long startupCompletedBytesReceived = 0;
        int startupCompletedNextExpectedChunk = 0;
        int startupCompletedHighestBufferedChunk = -1;
        var now = DateTimeOffset.UtcNow;

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
                    context.LastBulkDispatchedChunkUtc = now;
                    context.BulkDispatchedChunksSinceLastUsefulProgress++;

                    if (chunkBytes.Length == 0)
                    {
                        failureCode = InvalidStateErrorCode;
                        failureMessage = "Chunk payload was not valid base64.";
                }
                else if (message.ChunkIndex < 0 || message.ChunkIndex >= context.ChunkCount)
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk index exceeded the declared transfer bounds.";
                }
                else if (message.ChunkIndex < context.NextChunkIndex)
                {
                    duplicateIgnored = true;
                    context.ObsoleteChunksArrivedSinceLastUsefulProgress++;
                    context.RecentObsoleteChunkArrivalUtc.Enqueue(now);
                }
                else if (context.PendingChunks.ContainsKey(message.ChunkIndex))
                {
                    duplicateIgnored = true;
                }
                else if (
                    (chunkBytes.Length == 0 ||
                     chunkBytes.Length > context.ChunkSizeBytes ||
                     context.BytesTransferred + context.BufferedBytes + chunkBytes.Length > context.FileSizeBytes))
                {
                    failureCode = InvalidStateErrorCode;
                    failureMessage = "Chunk payload exceeded the declared transfer bounds.";
                }

                if (failureCode is null && !duplicateIgnored)
                {
                    context.PendingChunks[message.ChunkIndex] = chunkBytes;
                    context.BufferedBytes += chunkBytes.Length;
                    if (message.ChunkIndex > context.HighestBufferedChunkIndex)
                    {
                        context.HighestBufferedChunkIndex = message.ChunkIndex;
                        context.LastBufferedFrontierAdvanceUtc = now;
                        RecordInboundUsefulBulkProgressLocked(context, now, clearGapState: false);
                    }
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
                        UpdateOldestGapTrackingLocked(context);
                        UpdateInboundDegradedRepairModeLocked(context);
                        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                        var creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
                        var rawTargetGrantedUntilExclusive = Math.Min(context.ChunkCount, creditFrontier + flowControlPolicy.GrantChunks);
                        if (ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, rawTargetGrantedUntilExclusive) &&
                            ShouldLogGapDeferredLocked(context))
                        {
                            shouldLogDeferredGapExtension = true;
                            deferredGapHighestBufferedChunkIndex = highestBufferedChunkIndex;
                            deferredGapGrantedUntilExclusive = context.LastAdvertisedGrantedUntilExclusive;
                        }
                        shouldRequestMissingRange = ShouldRequestMissingRangeLocked(context);
                    }
                }
            }
        }

        if (duplicateIgnored)
        {
            return;
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

        if (bufferedWithoutProgress)
        {
            if (shouldLogDeferredGapExtension)
            {
                LogWindowExtensionDeferredDueToGap(
                    context!.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    deferredGapHighestBufferedChunkIndex,
                    deferredGapGrantedUntilExclusive);
            }

            if (shouldRequestMissingRange)
            {
                _ = SendMissingRangeAsync(context!, CancellationToken.None);
            }

            _ = MaybeSendPressureStateAsync(context!, CancellationToken.None);

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
                var previousNextChunkIndex = context.NextChunkIndex;
                for (var index = context.NextChunkIndex; index < nextChunkIndex; index++)
                {
                    if (context.PendingChunks.Remove(index, out var pendingChunkBytes))
                    {
                        context.BufferedBytes -= pendingChunkBytes.Length;
                    }
                }

                context.BytesTransferred = nextBytesTransferred;
                context.ChunksTransferred = nextChunksTransferred;
                context.NextChunkIndex = nextChunkIndex;
                var contiguousProgressChunkCount = Math.Max(0, nextChunkIndex - previousNextChunkIndex);
                if (contiguousProgressChunkCount > 0)
                {
                    RecordInboundContiguousProgressLocked(context, now, contiguousProgressChunkCount);
                }
                RefreshHighestBufferedChunkIndexLocked(context);
                UpdateOldestGapTrackingLocked(context);
                UpdateInboundDegradedRepairModeLocked(context);
                if (context.OutstandingMissingRange is not null &&
                    nextChunkIndex >= context.OutstandingMissingRange.Value.EndChunkIndexExclusive)
                {
                    context.OutstandingMissingRange = null;
                }
                context.State = shouldFinalize
                    ? FileTransferTransferState.Verifying
                    : FileTransferTransferState.Receiving;
                context.StatusMessage = shouldFinalize
                    ? "Verifying received file."
                    : "Receiving file data.";
                var progressAdvanced = nextChunkIndex > previousNextChunkIndex;
                if (progressAdvanced)
                {
                    context.LastContiguousProgressUtc = now;
                    RecordInboundUsefulBulkProgressLocked(context, now, clearGapState: context.OldestGapStartChunkIndex is null);
                    if (!context.StartupPhaseCompleted && nextChunkIndex > 0)
                    {
                        context.StartupPhaseCompleted = true;
                        context.LastForcedWindowUpdateSentUtc = null;
                        shouldLogStartupCompleted = true;
                        startupCompletedBytesReceived = context.BytesTransferred;
                        startupCompletedNextExpectedChunk = context.NextChunkIndex;
                        startupCompletedHighestBufferedChunk = GetCurrentHighestBufferedChunkIndexLocked(context);
                    }
                }

                if (!shouldFinalize && TryGetWindowUpdateRefreshTriggerLocked(context, out var trigger))
                {
                    windowUpdateTrigger = trigger;
                }
                snapshot = CreateSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context!, FileTransferDirection.Inbound);
        }

        if (shouldLogStartupCompleted)
        {
            LogWindowStartupCompleted(
                context!.TransferId,
                context.SessionId,
                startupCompletedNextExpectedChunk,
                startupCompletedHighestBufferedChunk,
                startupCompletedBytesReceived);
        }

        if (!shouldFinalize)
        {
            _ = MaybeSendPressureStateAsync(context!, CancellationToken.None);
            if (windowUpdateTrigger is not null)
            {
                _ = SendWindowUpdateAsync(context!, windowUpdateTrigger.Value, CancellationToken.None);
            }
            return;
        }

        await FinalizeInboundTransferAsync(context!, CancellationToken.None).ConfigureAwait(false);
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
        bool completionEligible;
        lock (gate)
        {
            context = outboundTransfer;
            completionEligible = context is not null &&
                !context.IsTerminal &&
                string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) &&
                (
                    context.State == FileTransferTransferState.AwaitingCompletion ||
                    (context.PullSessionActive && context.ChunksTransferred >= context.ChunkCount && context.ChunkCount > 0) ||
                    (context.NextChunkIndexToRead >= context.ChunkCount && context.PendingRepairChunkIndices.Count == 0)
                ) &&
                context.FileSizeBytes == message.FileSizeBytes &&
                string.Equals(context.Sha256Base64, message.Sha256Base64, StringComparison.Ordinal);

            if (context is null ||
                !completionEligible)
            {
                return Task.CompletedTask;
            }

            context.BytesTransferred = context.FileSizeBytes;
            context.ChunksTransferred = context.ChunkCount;
            context.BytesAcceptedForTransport = context.FileSizeBytes;
            context.ChunksAcceptedForTransport = context.ChunkCount;
            context.State = FileTransferTransferState.AwaitingCompletion;
            context.StatusMessage = "Waiting for receiver verification.";
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
                while (true)
                {
                    context.LifetimeCts.Token.ThrowIfCancellationRequested();

                    FileTransferChunkV1? repairChunk = null;
                    string? repairLogEvent = null;
                    int repairChunkIndex = -1;
                    int repairRangeStartChunkIndex = -1;
                    int repairRangeEndChunkExclusive = -1;
                    int pendingRepairBatchCount = 0;
                    TimeSpan repairWaitDelay = TimeSpan.Zero;
                    bool repairModeActive = false;
                    int nextChunkIndexToRead;
                    int remoteGrantedUntilExclusive;
                    int remoteNextExpectedChunkIndex;
                    int effectiveSendLimitExclusive;
                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        repairModeActive = TryPrepareRepairChunkLocked(
                            context,
                            GetEffectiveSendLimitExclusiveLocked(context),
                            out repairChunk,
                            out repairWaitDelay,
                            out repairLogEvent,
                            out repairChunkIndex,
                            out repairRangeStartChunkIndex,
                            out repairRangeEndChunkExclusive,
                            out pendingRepairBatchCount);
                        nextChunkIndexToRead = context.NextChunkIndexToRead;
                        remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
                        remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
                        effectiveSendLimitExclusive = GetEffectiveSendLimitExclusiveLocked(context);
                    }

                    if (repairLogEvent is not null)
                    {
                        LogRepairChunkEvent(
                            repairLogEvent,
                            context.TransferId,
                            context.SessionId,
                            repairChunkIndex,
                            repairRangeStartChunkIndex,
                            repairRangeEndChunkExclusive,
                            remoteNextExpectedChunkIndex,
                            remoteGrantedUntilExclusive,
                            context.CurrentRepairBatchSize,
                            pendingRepairBatchCount);
                    }

                    if (repairChunk is not null)
                    {
                        await currentTransport.SendFileTransferChunkAsync(repairChunk, context.LifetimeCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (repairModeActive)
                    {
                        LogRepairModeBatchWait(
                            context.TransferId,
                            context.SessionId,
                            repairRangeStartChunkIndex,
                            repairRangeEndChunkExclusive,
                            remoteNextExpectedChunkIndex,
                            remoteGrantedUntilExclusive,
                            pendingRepairBatchCount);
                        await WaitForOutboundRepairActivityAsync(context, repairWaitDelay).ConfigureAwait(false);
                        continue;
                    }

                    if (context.RepairOnlyModeActive &&
                        context.RemotePressureMode != FileTransferPressureMode.CatchUpOnly &&
                        nextChunkIndexToRead > remoteNextExpectedChunkIndex)
                    {
                        await WaitForOutboundControlActivityAsync(context, "repair_only").ConfigureAwait(false);
                        continue;
                    }

                    if (nextChunkIndexToRead >= context.ChunkCount)
                    {
                        UpdateOutboundState(
                            context,
                            FileTransferTransferState.AwaitingCompletion,
                            context.BytesTransferred,
                            context.ChunksTransferred,
                            "Waiting for receiver verification.");
                        await WaitForOutboundCompletionSignalAsync(context).ConfigureAwait(false);
                        continue;
                    }

                    if (nextChunkIndexToRead >= effectiveSendLimitExclusive)
                    {
                        await WaitForOutboundControlActivityAsync(context, "window").ConfigureAwait(false);
                        continue;
                    }

                    var fileOffset = (long)nextChunkIndexToRead * context.ChunkSizeBytes;
                    if (stream.CanSeek && stream.Position != fileOffset)
                    {
                        stream.Seek(fileOffset, SeekOrigin.Begin);
                    }

                    var remaining = context.FileSizeBytes - fileOffset;
                    var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, targetReadSize), context.LifetimeCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Failed,
                            errorCode: FileSizeMismatchErrorCode,
                            statusMessage: "Source stream did not match the declared file size.",
                            notifyPeer: true,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    var chunkBytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);
                    var chunkMessage = new FileTransferChunkV1
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        ChunkIndex = nextChunkIndexToRead,
                        ChunkCount = context.ChunkCount,
                        DataBase64 = Convert.ToBase64String(chunkBytes),
                    };

                    SessionFileTransferSnapshot? snapshot = null;
                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        context.NextChunkIndexToRead = nextChunkIndexToRead + 1;
                        context.SentChunkCache[nextChunkIndexToRead] = chunkMessage;
                        context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, context.NextChunkIndexToRead);
                        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                            ? context.FileSizeBytes
                            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                        snapshot = CreateSnapshotLocked();
                    }

                    if (snapshot is not null)
                    {
                        RaiseTransferChanged(snapshot);
                    }

                    await currentTransport.SendFileTransferChunkAsync(chunkMessage, context.LifetimeCts.Token).ConfigureAwait(false);
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
            // Local cancel path already transitioned the state.
        }
        catch (Exception ex)
        {
            var errorCode =
                ex is InvalidOperationException invalidOperationException &&
                invalidOperationException.Message.Contains("payload exceeded safe budget", StringComparison.OrdinalIgnoreCase)
                    ? PayloadBudgetExceededErrorCode
                    : ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode);
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
            if (context.PullSessionActive && context.DataSession is not null)
            {
                await context.DataSession.SendAsync(
                    new FileTransferCompleteFrameV2
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        FileSizeBytes = context.FileSizeBytes,
                        Sha256Base64 = computedHash,
                    },
                    context.LifetimeCts.Token).ConfigureAwait(false);
            }
            else
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
            }

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

    private async Task SendWindowUpdateAsync(InboundTransferContext context, WindowUpdateTrigger trigger, CancellationToken ct)
    {
        try
        {
            FileTransferWindowUpdateV1? message = null;
            string? triggerReason = null;
            string? phase = null;
            var highestBufferedChunkIndex = -1;
            var creditFrontier = 0;
            lock (gate)
            {
                WindowUpdateTrigger currentTrigger = default;
                if (!ReferenceEquals(inboundTransfer, context) ||
                    context.IsTerminal ||
                    context.State is not FileTransferTransferState.Receiving and not FileTransferTransferState.Verifying)
                {
                    return;
                }

                if ((trigger == WindowUpdateTrigger.LowWatermark ||
                     trigger == WindowUpdateTrigger.BufferedFrontier ||
                     trigger == WindowUpdateTrigger.GapProgressAck) &&
                    !TryGetWindowUpdateRefreshTriggerLocked(context, out currentTrigger))
                {
                    return;
                }

                if ((trigger == WindowUpdateTrigger.LowWatermark ||
                     trigger == WindowUpdateTrigger.BufferedFrontier ||
                     trigger == WindowUpdateTrigger.GapProgressAck) &&
                    currentTrigger != trigger)
                {
                    return;
                }

                UpdateInboundDegradedRepairModeLocked(context);
                UpdateInboundBulkHealthLocked(context);
                highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
                var grantedUntilExclusive = GetTargetGrantedUntilExclusiveLocked(context, trigger, creditFrontier);
                message = new FileTransferWindowUpdateV1
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    GrantedUntilChunkIndexExclusive = grantedUntilExclusive,
                    BytesReceived = context.BytesTransferred,
                };
                context.LastAdvertisedGrantedUntilExclusive = grantedUntilExclusive;
                context.LastAdvertisedNextChunkIndex = context.NextChunkIndex;
                context.LastAdvertisedCreditFrontier = creditFrontier;
                context.LastWindowUpdateSentUtc = DateTimeOffset.UtcNow;
                if (trigger is WindowUpdateTrigger.StartupResend or WindowUpdateTrigger.SteadyStateResend)
                {
                    context.LastForcedWindowUpdateSentUtc = context.LastWindowUpdateSentUtc;
                }

                if (trigger is not WindowUpdateTrigger.Startup and
                    not WindowUpdateTrigger.StartupResend and
                    not WindowUpdateTrigger.GapProgressAck)
                {
                    context.SteadyStateWindowAdvertised = true;
                }

                if (trigger == WindowUpdateTrigger.GapProgressAck)
                {
                    context.RecentGapProgressAckSentUtc.Enqueue(context.LastWindowUpdateSentUtc.Value);
                }

                triggerReason = trigger switch
                {
                    WindowUpdateTrigger.Startup => "startup",
                    WindowUpdateTrigger.StartupResend => "startup_resend",
                    WindowUpdateTrigger.GapProgressAck => "gap_progress_ack",
                    WindowUpdateTrigger.BufferedFrontier => "buffered_frontier",
                    WindowUpdateTrigger.SteadyStateResend => "watchdog_forced",
                    _ => "low_watermark",
                };
                phase = trigger is WindowUpdateTrigger.Startup or WindowUpdateTrigger.StartupResend
                    ? "startup"
                    : "steady_state";
            }

            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferWindowUpdateAsync(message, ct).ConfigureAwait(false);
            LogWindowUpdateSent(message!, phase!, triggerReason!, highestBufferedChunkIndex, creditFrontier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"window_update send failed: {ex.Message}");
        }
    }

    private async Task RunInboundWindowRefreshWatchdogAsync(InboundTransferContext context)
    {
        try
        {
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(WindowUpdateWatchdogDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                WindowUpdateTrigger? trigger = null;
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, context) &&
                        !context.IsTerminal &&
                        context.State == FileTransferTransferState.Receiving)
                    {
                        UpdateInboundBulkHealthLocked(context);
                        if (TryGetWindowUpdateRefreshTriggerLocked(context, out var refreshTrigger))
                        {
                            trigger = refreshTrigger;
                        }
                        else if (TryGetWatchdogWindowUpdateTriggerLocked(context, out var watchdogTrigger))
                        {
                            trigger = watchdogTrigger;
                        }
                    }
                }

                if (trigger is null)
                {
                    lock (gate)
                    {
                        if (!ReferenceEquals(inboundTransfer, context) ||
                            context.IsTerminal ||
                            context.State != FileTransferTransferState.Receiving)
                        {
                            return;
                        }
                    }

                    continue;
                }

                await MaybeSendPressureStateAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
                await SendWindowUpdateAsync(context, trigger.Value, context.LifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunInboundGapRecoveryWatchdogAsync(InboundTransferContext context)
    {
        try
        {
            while (!context.LifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(MissingRangeWatchdogDelayMs, context.LifetimeCts.Token).ConfigureAwait(false);

                var shouldSendMissingRange = false;
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, context) &&
                        !context.IsTerminal &&
                        context.State == FileTransferTransferState.Receiving)
                    {
                        UpdateInboundBulkHealthLocked(context);
                        shouldSendMissingRange = ShouldRequestMissingRangeLocked(context);
                    }
                }

                if (!shouldSendMissingRange)
                {
                    lock (gate)
                    {
                        if (!ReferenceEquals(inboundTransfer, context) ||
                            context.IsTerminal ||
                            context.State != FileTransferTransferState.Receiving)
                        {
                            return;
                        }
                    }

                    continue;
                }

                await SendMissingRangeAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendMissingRangeAsync(InboundTransferContext context, CancellationToken ct)
    {
        try
        {
            FileTransferMissingRangeV1? message = null;
            int nextExpectedChunkIndex;
            int highestBufferedChunkIndex;
            bool shouldLogDeferredGapExtension = false;
            int deferredGapGrantedUntilExclusive = 0;
            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) ||
                    context.IsTerminal ||
                    context.State != FileTransferTransferState.Receiving)
                {
                    return;
                }

                if (!TryBuildMissingRangeLocked(context, out message))
                {
                    return;
                }

                nextExpectedChunkIndex = context.NextChunkIndex;
                highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
                if (highestBufferedChunkIndex >= nextExpectedChunkIndex &&
                    ShouldLogGapDeferredLocked(context))
                {
                    shouldLogDeferredGapExtension = true;
                    deferredGapGrantedUntilExclusive = context.LastAdvertisedGrantedUntilExclusive;
                }
            }

            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferMissingRangeAsync(message, ct).ConfigureAwait(false);
            if (shouldLogDeferredGapExtension)
            {
                LogWindowExtensionDeferredDueToGap(
                    context.TransferId,
                    context.SessionId,
                    nextExpectedChunkIndex,
                    highestBufferedChunkIndex,
                    deferredGapGrantedUntilExclusive);
            }
            LogMissingRangeSent(message, nextExpectedChunkIndex, highestBufferedChunkIndex);
            await MaybeSendPressureStateAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"missing_range send failed: {ex.Message}");
        }
    }

    private bool TryGetWindowUpdateRefreshTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
    {
        trigger = default;

        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
        var creditFrontier = GetCreditFrontierLocked(context, highestBufferedChunkIndex);
        var rawTargetGrantedUntilExclusive = GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);
        var shouldDeferGrantExtension = ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, rawTargetGrantedUntilExclusive);
        if (shouldDeferGrantExtension)
        {
            if (context.LastAdvertisedGrantedUntilExclusive > 0 &&
                context.NextChunkIndex > context.LastAdvertisedNextChunkIndex)
            {
                trigger = WindowUpdateTrigger.GapProgressAck;
                return true;
            }

            if (ShouldLogGapDeferredLocked(context))
            {
                LogWindowExtensionDeferredDueToGap(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    highestBufferedChunkIndex,
                    context.LastAdvertisedGrantedUntilExclusive);
            }
            return false;
        }

        if (!context.StartupPhaseCompleted)
        {
            return false;
        }

        var targetGrantedUntilExclusive = GetTargetGrantedUntilExclusiveLocked(context, WindowUpdateTrigger.BufferedFrontier, creditFrontier);
        if (!context.SteadyStateWindowAdvertised &&
            targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive)
        {
            trigger = context.BulkFallbackModeActive
                ? WindowUpdateTrigger.LowWatermark
                : WindowUpdateTrigger.BufferedFrontier;
            return true;
        }

        var hasDelta =
            context.NextChunkIndex > context.LastAdvertisedNextChunkIndex ||
            (!context.BulkFallbackModeActive && creditFrontier > context.LastAdvertisedCreditFrontier) ||
            targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive;
        if (!hasDelta)
        {
            return false;
        }

        var remainingContiguousRunway = context.LastAdvertisedGrantedUntilExclusive - context.NextChunkIndex;
        var lowWatermarkChunks = GetEffectiveLowWatermarkChunksLocked(context);
        if (remainingContiguousRunway <= lowWatermarkChunks)
        {
            trigger = WindowUpdateTrigger.LowWatermark;
            return true;
        }

        if (context.BulkFallbackModeActive)
        {
            return false;
        }

        var remainingFrontierRunway = context.LastAdvertisedGrantedUntilExclusive - creditFrontier;
        if (creditFrontier > context.NextChunkIndex &&
            remainingFrontierRunway <= lowWatermarkChunks)
        {
            trigger = WindowUpdateTrigger.BufferedFrontier;
            return true;
        }

        return false;
    }

    private bool TryGetWatchdogWindowUpdateTriggerLocked(InboundTransferContext context, out WindowUpdateTrigger trigger)
    {
        trigger = default;
        if (context.LastWindowUpdateSentUtc is null)
        {
            return false;
        }

        if (TryGetWindowUpdateRefreshTriggerLocked(context, out _))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var forcedResendMinIntervalMs =
            context.LocalPressureMode == FileTransferPressureMode.CatchUpOnly &&
            context.LastAdvertisedNextChunkIndex == context.NextChunkIndex &&
            context.LastAdvertisedGrantedUntilExclusive > 0
                ? PressuredWatchdogResendMinIntervalMs
                : WindowUpdateWatchdogDelayMs;
        if (context.LastForcedWindowUpdateSentUtc is not null &&
            now - context.LastForcedWindowUpdateSentUtc.Value < TimeSpan.FromMilliseconds(forcedResendMinIntervalMs))
        {
            return false;
        }

        if (!context.StartupPhaseCompleted)
        {
            if (now - context.LastWindowUpdateSentUtc.Value < TimeSpan.FromMilliseconds(WindowUpdateWatchdogDelayMs))
            {
                return false;
            }

            trigger = WindowUpdateTrigger.StartupResend;
            return true;
        }

        var lastActivityUtc = MaxDateTimeOffset(
            context.LastContiguousProgressUtc,
            context.LastBufferedFrontierAdvanceUtc,
            context.LastWindowUpdateSentUtc);
        if (now - lastActivityUtc < TimeSpan.FromMilliseconds(WindowUpdateWatchdogDelayMs))
        {
            return false;
        }

        trigger = WindowUpdateTrigger.SteadyStateResend;
        return true;
    }

    private void UpdateOldestGapTrackingLocked(InboundTransferContext context)
    {
        if (context.PendingChunks.Count == 0)
        {
            context.OldestGapStartChunkIndex = null;
            context.OldestGapFirstSeenUtc = null;
            context.OutstandingMissingRange = null;
            context.LastMissingRangeHighestBufferedChunkIndex = -1;
            context.GapFreeSinceUtc ??= DateTimeOffset.UtcNow;
            return;
        }

        var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
        if (firstBufferedChunkIndex <= context.NextChunkIndex)
        {
            context.OldestGapStartChunkIndex = null;
            context.OldestGapFirstSeenUtc = null;
            context.OutstandingMissingRange = null;
            context.LastMissingRangeHighestBufferedChunkIndex = -1;
            context.GapFreeSinceUtc ??= DateTimeOffset.UtcNow;
            return;
        }

        if (context.OldestGapStartChunkIndex == context.NextChunkIndex)
        {
            return;
        }

        context.OldestGapStartChunkIndex = context.NextChunkIndex;
        context.OldestGapFirstSeenUtc = DateTimeOffset.UtcNow;
        context.OutstandingMissingRange = null;
        context.LastMissingRangeSentUtc = null;
        context.LastMissingRangeHighestBufferedChunkIndex = -1;
        context.GapFreeSinceUtc = null;
    }

    private void UpdateInboundDegradedRepairModeLocked(InboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        while (context.RecentMissingRangeSentUtc.Count > 0 &&
               now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(DegradedRepairMissingRangeBurstWindowMs))
        {
            context.RecentMissingRangeSentUtc.Dequeue();
        }

        if (context.OldestGapStartChunkIndex is null)
        {
            context.GapFreeSinceUtc ??= now;
            if (context.DegradedRepairModeActive &&
                now - context.GapFreeSinceUtc.Value >= TimeSpan.FromMilliseconds(DegradedRepairRecoveryHoldMs))
            {
                context.DegradedRepairModeActive = false;
                context.RecentMissingRangeSentUtc.Clear();
            }

            return;
        }

        context.GapFreeSinceUtc = null;
        if (context.DegradedRepairModeActive)
        {
            return;
        }

        var persistentGap =
            context.OldestGapFirstSeenUtc is not null &&
            now - context.OldestGapFirstSeenUtc.Value >= TimeSpan.FromMilliseconds(DegradedRepairGapPersistenceMs);
        var burstMissingRanges = context.RecentMissingRangeSentUtc.Count >= DegradedRepairMissingRangeBurstThreshold;
        if (persistentGap || burstMissingRanges)
        {
            context.DegradedRepairModeActive = true;
        }
    }

    private static void RecordInboundUsefulBulkProgressLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        bool clearGapState)
    {
        context.LastUsefulBulkProgressUtc = now;
        context.BulkDispatchedChunksSinceLastUsefulProgress = 0;
        context.ObsoleteChunksArrivedSinceLastUsefulProgress = 0;
        context.ConsecutiveBulkUnhealthyDetections = 0;
        if (clearGapState)
        {
            context.BulkUnhealthyDetected = false;
        }
    }

    private static void RecordInboundContiguousProgressLocked(
        InboundTransferContext context,
        DateTimeOffset now,
        int contiguousProgressChunkCount)
    {
        for (var index = 0; index < contiguousProgressChunkCount; index++)
        {
            context.RecentContiguousProgressChunkUtc.Enqueue(now);
        }
    }

    private void UpdateInboundBulkHealthLocked(InboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        while (context.RecentMissingRangeSentUtc.Count > 0 &&
               now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
        {
            context.RecentMissingRangeSentUtc.Dequeue();
        }

        while (context.RecentGapProgressAckSentUtc.Count > 0 &&
               now - context.RecentGapProgressAckSentUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
        {
            context.RecentGapProgressAckSentUtc.Dequeue();
        }

        while (context.RecentContiguousProgressChunkUtc.Count > 0 &&
               now - context.RecentContiguousProgressChunkUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
        {
            context.RecentContiguousProgressChunkUtc.Dequeue();
        }
        while (context.RecentObsoleteChunkArrivalUtc.Count > 0 &&
               now - context.RecentObsoleteChunkArrivalUtc.Peek() > TimeSpan.FromMilliseconds(RepairChurnDetectionWindowMs))
        {
            context.RecentObsoleteChunkArrivalUtc.Dequeue();
        }

        var lastUsefulProgressUtc = MaxDateTimeOffset(context.LastUsefulBulkProgressUtc, context.LastContiguousProgressUtc, context.LastBufferedFrontierAdvanceUtc);
        var recentBulkDispatch =
            context.LastBulkDispatchedChunkUtc is not null &&
            now - context.LastBulkDispatchedChunkUtc.Value <= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
        var noUsefulProgress = now - lastUsefulProgressUtc >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
        var stagnantBufferedFrontier =
            highestBufferedChunkIndex <= context.NextChunkIndex ||
            context.LastBufferedFrontierAdvanceUtc is null ||
            now - context.LastBufferedFrontierAdvanceUtc.Value >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs);
        var repeatedGapSignals =
            context.OldestGapStartChunkIndex is not null ||
            context.RecentMissingRangeSentUtc.Count > 0 ||
            context.RecentGapProgressAckSentUtc.Count > 0;
        var recentContiguousProgressChunks = context.RecentContiguousProgressChunkUtc.Count;
        var sustainedRecoveryChurn =
            context.RecentMissingRangeSentUtc.Count >= BulkUnhealthyDetectionConfirmations ||
            context.RecentGapProgressAckSentUtc.Count >= BulkUnhealthyDetectionConfirmations;
        var repairChurnUnhealthy =
            context.OldestGapStartChunkIndex is not null &&
            context.RecentMissingRangeSentUtc.Count >= RepairChurnMissingRangeThreshold &&
            recentContiguousProgressChunks <= RepairChurnMaxHealthyProgressChunks;
        var unhealthy =
            repeatedGapSignals &&
            (
                (
                    noUsefulProgress &&
                    stagnantBufferedFrontier &&
                    (
                        (recentBulkDispatch && context.BulkDispatchedChunksSinceLastUsefulProgress > 0) ||
                        sustainedRecoveryChurn
                    )
                ) ||
                repairChurnUnhealthy
            );

        if (unhealthy)
        {
            context.ConsecutiveBulkUnhealthyDetections++;
            if (context.LastBulkUnhealthyLogUtc is null ||
                now - context.LastBulkUnhealthyLogUtc.Value >= TimeSpan.FromMilliseconds(BulkUnhealthyDetectionWindowMs))
            {
                context.LastBulkUnhealthyLogUtc = now;
                context.BulkUnhealthyDetected = true;
                LogBulkUnhealthyDetected(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    highestBufferedChunkIndex,
                    context.LastAdvertisedGrantedUntilExclusive,
                    context.BulkDispatchedChunksSinceLastUsefulProgress,
                    context.ObsoleteChunksArrivedSinceLastUsefulProgress,
                    context.RecentObsoleteChunkArrivalUtc.Count,
                    context.RecentMissingRangeSentUtc.Count,
                    context.RecentGapProgressAckSentUtc.Count,
                    recentContiguousProgressChunks);
            }

            if (!context.BulkFallbackModeActive &&
                context.ConsecutiveBulkUnhealthyDetections >= BulkUnhealthyDetectionConfirmations)
            {
                context.BulkFallbackModeActive = true;
                LogBulkFallbackEntered(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    highestBufferedChunkIndex,
                    context.LastAdvertisedGrantedUntilExclusive);
            }

            return;
        }

        context.ConsecutiveBulkUnhealthyDetections = 0;
        if (context.BulkUnhealthyDetected)
        {
            context.BulkUnhealthyDetected = false;
            LogBulkHealthyResumed(
                context.TransferId,
                context.SessionId,
                context.NextChunkIndex,
                highestBufferedChunkIndex,
                context.LastAdvertisedGrantedUntilExclusive);
        }

        if (context.BulkFallbackModeActive &&
            context.OldestGapStartChunkIndex is null &&
            context.GapFreeSinceUtc is not null &&
            now - context.GapFreeSinceUtc.Value >= TimeSpan.FromMilliseconds(BulkFallbackRecoveryHoldMs) &&
            context.RecentMissingRangeSentUtc.Count == 0)
        {
            context.BulkFallbackModeActive = false;
            LogBulkFallbackExited(
                context.TransferId,
                context.SessionId,
                context.NextChunkIndex,
                highestBufferedChunkIndex,
                context.LastAdvertisedGrantedUntilExclusive);
        }
    }

    private async Task MaybeSendPressureStateAsync(InboundTransferContext context, CancellationToken ct)
    {
        try
        {
            FileTransferPressureStateV1? message = null;
            bool pressureStateChanged = false;
            SessionFileTransferSnapshot? snapshot = null;

            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) ||
                    context.IsTerminal ||
                    context.State is not FileTransferTransferState.Receiving and not FileTransferTransferState.Verifying)
                {
                    return;
                }

                UpdateInboundBulkHealthLocked(context);
                pressureStateChanged = TryTransitionInboundPressureStateLocked(context, out message);
                if (pressureStateChanged)
                {
                    snapshot = CreateSnapshotLocked();
                }
            }

            if (message is null)
            {
                return;
            }

            var currentTransport = GetTransportOrThrow();
            await currentTransport.SendFileTransferPressureStateAsync(message, ct).ConfigureAwait(false);
            LogPressureStateSent(message);
            if (pressureStateChanged && snapshot is not null)
            {
                RaiseTransferChanged(snapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"pressure_state send failed: {ex.Message}");
        }
    }

    private bool TryTransitionInboundPressureStateLocked(InboundTransferContext context, out FileTransferPressureStateV1? message)
    {
        message = null;
        var now = DateTimeOffset.UtcNow;
        var obsoleteChunkCountRecent = context.RecentObsoleteChunkArrivalUtc.Count;
        var missingRangeCountRecent = context.RecentMissingRangeSentUtc.Count;
        var obsoleteChunkArrivalRatio = context.BulkDispatchedChunksSinceLastUsefulProgress <= 0
            ? 0D
            : (double)context.ObsoleteChunksArrivedSinceLastUsefulProgress / context.BulkDispatchedChunksSinceLastUsefulProgress;
        var mediaProtection = sessionScreenShareActive && sessionScreenShareDegraded;
        var staleBulkBacklog =
            obsoleteChunkCountRecent >= PressureStateObsoleteChunkThreshold &&
            obsoleteChunkArrivalRatio >= PressureStateObsoleteChunkRatioThreshold;
        var noContiguousProgress =
            context.LastContiguousProgressUtc is null ||
            now - context.LastContiguousProgressUtc.Value >= TimeSpan.FromMilliseconds(PressureStateNoProgressWindowMs);
        var stalledBulkBacklog =
            context.BulkDispatchedChunksSinceLastUsefulProgress >= PressureStateBulkDispatchThreshold &&
            noContiguousProgress;
        var catchUpRequested = mediaProtection || staleBulkBacklog || stalledBulkBacklog;
        if (context.LocalPressureMode == FileTransferPressureMode.CatchUpOnly && !catchUpRequested)
        {
            var cleanForRecovery =
                context.OldestGapStartChunkIndex is null &&
                obsoleteChunkCountRecent == 0 &&
                context.BulkDispatchedChunksSinceLastUsefulProgress <= 1 &&
                !mediaProtection;
            if (!cleanForRecovery)
            {
                context.PressureRecoverySinceUtc = null;
                catchUpRequested = true;
            }
            else
            {
                context.PressureRecoverySinceUtc ??= now;
                catchUpRequested = now - context.PressureRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PressureStateRecoveryHoldMs);
            }
        }
        else if (catchUpRequested)
        {
            context.PressureRecoverySinceUtc = null;
        }
        else
        {
            context.PressureRecoverySinceUtc = null;
        }

        var desiredMode = catchUpRequested
            ? FileTransferPressureMode.CatchUpOnly
            : FileTransferPressureMode.Normal;
        var desiredReason = catchUpRequested
            ? mediaProtection
                ? FileTransferPressureReason.MediaProtection
                : context.OldestGapStartChunkIndex is not null
                    ? FileTransferPressureReason.GapRepair
                    : staleBulkBacklog || stalledBulkBacklog
                    ? FileTransferPressureReason.BulkBacklog
                    : FileTransferPressureReason.BulkBacklog
            : FileTransferPressureReason.BulkBacklog;
        var desiredSuggestedSendAheadChunks = desiredMode == FileTransferPressureMode.CatchUpOnly
            ? sessionScreenShareActive
                ? PressureStateCatchUpOnlySuggestedSendAheadWhileScreenshareChunks
                : PressureStateCatchUpOnlySuggestedSendAheadChunks
            : PressureStateNormalSuggestedSendAheadChunks;
        var desiredReceiverNextExpectedChunkIndex = context.NextChunkIndex;
        if (context.LocalPressureMode == desiredMode &&
            context.LocalPressureSuggestedSendAheadChunks == desiredSuggestedSendAheadChunks &&
            context.LocalPressureReceiverNextExpectedChunkIndex == desiredReceiverNextExpectedChunkIndex)
        {
            context.LocalPressureReason = desiredReason;
            return false;
        }

        var previousMode = context.LocalPressureMode;
        context.LocalPressureMode = desiredMode;
        context.LocalPressureReason = desiredReason;
        context.LocalPressureSuggestedSendAheadChunks = desiredSuggestedSendAheadChunks;
        context.LocalPressureReceiverNextExpectedChunkIndex = desiredReceiverNextExpectedChunkIndex;
        context.LocalPressureRevision++;
        if (desiredMode == FileTransferPressureMode.CatchUpOnly)
        {
            context.PressureRecoverySinceUtc = null;
        }

        if (previousMode != desiredMode)
        {
            if (desiredMode == FileTransferPressureMode.CatchUpOnly)
            {
                LogBulkCatchUpOnlyEntered(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    GetCurrentHighestBufferedChunkIndexLocked(context),
                    obsoleteChunkCountRecent,
                    obsoleteChunkArrivalRatio,
                    missingRangeCountRecent,
                    context.RecentContiguousProgressChunkUtc.Count,
                    desiredReason);
            }
            else
            {
                LogBulkCatchUpOnlyExited(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    GetCurrentHighestBufferedChunkIndexLocked(context),
                    desiredSuggestedSendAheadChunks);
            }
        }

        message = new FileTransferPressureStateV1
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            Revision = context.LocalPressureRevision,
            Mode = FormatPressureMode(desiredMode),
            SuggestedSendAheadChunks = desiredSuggestedSendAheadChunks,
            ReceiverNextExpectedChunkIndex = desiredReceiverNextExpectedChunkIndex,
            Reason = FormatPressureReason(desiredReason),
        };
        return true;
    }

    private void RecordMissingRangeSentLocked(InboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.RecentMissingRangeSentUtc.Enqueue(now);
        while (context.RecentMissingRangeSentUtc.Count > 0 &&
               now - context.RecentMissingRangeSentUtc.Peek() > TimeSpan.FromMilliseconds(Math.Max(DegradedRepairMissingRangeBurstWindowMs, RepairChurnDetectionWindowMs)))
        {
            context.RecentMissingRangeSentUtc.Dequeue();
        }

        UpdateInboundDegradedRepairModeLocked(context);
    }

    private static void RefreshHighestBufferedChunkIndexLocked(InboundTransferContext context)
    {
        context.HighestBufferedChunkIndex = context.PendingChunks.Count == 0
            ? context.NextChunkIndex - 1
            : Math.Max(context.NextChunkIndex - 1, context.PendingChunks.Keys.Max());
    }

    private int GetCreditFrontierLocked(InboundTransferContext context, int highestBufferedChunkIndex)
    {
        if (context.BulkFallbackModeActive)
        {
            return context.NextChunkIndex;
        }

        var bufferedExclusive = Math.Max(context.NextChunkIndex, highestBufferedChunkIndex + 1);
        return Math.Min(bufferedExclusive, context.NextChunkIndex + GetEffectiveGrantChunksLocked(context));
    }

    private int GetRawTargetGrantedUntilExclusiveLocked(InboundTransferContext context, int creditFrontier)
    {
        if (!context.StartupPhaseCompleted)
        {
            return Math.Min(context.ChunkCount, context.NextChunkIndex + GetEffectiveStartupGrantChunksLocked());
        }

        var effectiveGrantChunks = GetEffectiveGrantChunksLocked(context);
        return Math.Min(context.ChunkCount, creditFrontier + effectiveGrantChunks);
    }

    private int GetTargetGrantedUntilExclusiveLocked(
        InboundTransferContext context,
        WindowUpdateTrigger trigger,
        int creditFrontier)
    {
        if (trigger is WindowUpdateTrigger.Startup or WindowUpdateTrigger.StartupResend || !context.StartupPhaseCompleted)
        {
            return Math.Min(context.ChunkCount, context.NextChunkIndex + GetEffectiveStartupGrantChunksLocked());
        }

        if (trigger == WindowUpdateTrigger.GapProgressAck)
        {
            return context.LastAdvertisedGrantedUntilExclusive;
        }

        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
        if (ShouldDeferGrantExtensionDueToGapLocked(context, highestBufferedChunkIndex, GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier)) &&
            context.LastAdvertisedGrantedUntilExclusive > 0)
        {
            return context.LastAdvertisedGrantedUntilExclusive;
        }

        if (context.BulkFallbackModeActive)
        {
            var fallbackCap = Math.Min(context.ChunkCount, context.NextChunkIndex + BulkFallbackGrantChunks);
            return Math.Max(context.LastAdvertisedGrantedUntilExclusive, fallbackCap);
        }

        if (context.DegradedRepairModeActive)
        {
            var degradedCap = Math.Min(context.ChunkCount, context.NextChunkIndex + DegradedRepairGrantChunks);
            return Math.Max(context.LastAdvertisedGrantedUntilExclusive, degradedCap);
        }

        return GetRawTargetGrantedUntilExclusiveLocked(context, creditFrontier);
    }

    private static DateTimeOffset MaxDateTimeOffset(params DateTimeOffset?[] values)
    {
        DateTimeOffset? max = null;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (max is null || value.Value > max.Value)
            {
                max = value.Value;
            }
        }

        return max ?? DateTimeOffset.UtcNow;
    }

    private static int GetCurrentHighestBufferedChunkIndexLocked(InboundTransferContext context)
    {
        return context.PendingChunks.Count == 0
            ? context.NextChunkIndex - 1
            : Math.Max(context.NextChunkIndex - 1, context.HighestBufferedChunkIndex);
    }

    private int GetEffectiveGrantChunksLocked(InboundTransferContext context)
        => context.BulkFallbackModeActive
            ? BulkFallbackGrantChunks
            : context.DegradedRepairModeActive
                ? Math.Min(flowControlPolicy.GrantChunks, DegradedRepairGrantChunks)
                : flowControlPolicy.GrantChunks;

    private int GetEffectiveStartupGrantChunksLocked()
        => sessionScreenShareActive
            ? Math.Min(flowControlPolicy.StartupGrantChunks, ScreenShareActiveStartupGrantCapChunks)
            : flowControlPolicy.StartupGrantChunks;

    private int GetEffectiveLowWatermarkChunksLocked(InboundTransferContext context)
        => context.BulkFallbackModeActive
            ? BulkFallbackLowWatermarkChunks
            : context.DegradedRepairModeActive
                ? DegradedRepairLowWatermarkChunks
                : flowControlPolicy.LowWatermarkChunks;

    private static bool ShouldRequestMissingRangeLocked(InboundTransferContext context)
    {
        if (context.PendingChunks.Count == 0 ||
            context.OldestGapStartChunkIndex is null ||
            context.OldestGapFirstSeenUtc is null)
        {
            return false;
        }

        if (context.OutstandingMissingRange is not null &&
            context.LastMissingRangeSentUtc is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var sinceLastMissingRange = now - context.LastMissingRangeSentUtc.Value;
            if (sinceLastMissingRange < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
            {
                return false;
            }

            if (sinceLastMissingRange < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
            {
                var gapStartChunkIndex = context.OldestGapStartChunkIndex.Value;
                var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
                var nextRange = new MissingRange(
                    gapStartChunkIndex,
                    Math.Min(firstBufferedChunkIndex, gapStartChunkIndex + MissingRangeMaxChunks));
                var narrowedRange =
                    nextRange.StartChunkIndex >= context.OutstandingMissingRange.Value.StartChunkIndex &&
                    nextRange.EndChunkIndexExclusive <= context.OutstandingMissingRange.Value.EndChunkIndexExclusive &&
                    (nextRange.StartChunkIndex > context.OutstandingMissingRange.Value.StartChunkIndex ||
                     nextRange.EndChunkIndexExclusive < context.OutstandingMissingRange.Value.EndChunkIndexExclusive);
                var highestBufferedAdvanced = GetCurrentHighestBufferedChunkIndexLocked(context) > context.LastMissingRangeHighestBufferedChunkIndex;
                if (!narrowedRange && !highestBufferedAdvanced)
                {
                    return false;
                }
            }
        }

        return DateTimeOffset.UtcNow - context.OldestGapFirstSeenUtc.Value >= TimeSpan.FromMilliseconds(MissingRangeTriggerDelayMs);
    }

    private bool TryBuildMissingRangeLocked(InboundTransferContext context, out FileTransferMissingRangeV1 message)
    {
        message = default!;
        if (context.PendingChunks.Count == 0 || context.OldestGapStartChunkIndex is null)
        {
            return false;
        }

        var firstBufferedChunkIndex = context.PendingChunks.Keys.Min();
        var gapStartChunkIndex = context.OldestGapStartChunkIndex.Value;
        if (firstBufferedChunkIndex <= gapStartChunkIndex)
        {
            return false;
        }

        var endChunkIndexExclusive = Math.Min(firstBufferedChunkIndex, gapStartChunkIndex + MissingRangeMaxChunks);
        if (endChunkIndexExclusive <= gapStartChunkIndex)
        {
            return false;
        }

        var highestBufferedChunkIndex = GetCurrentHighestBufferedChunkIndexLocked(context);
        var range = new MissingRange(gapStartChunkIndex, endChunkIndexExclusive);
        var narrowedRange =
            context.OutstandingMissingRange is not null &&
            range.StartChunkIndex >= context.OutstandingMissingRange.Value.StartChunkIndex &&
            range.EndChunkIndexExclusive <= context.OutstandingMissingRange.Value.EndChunkIndexExclusive &&
            (range.StartChunkIndex > context.OutstandingMissingRange.Value.StartChunkIndex ||
             range.EndChunkIndexExclusive < context.OutstandingMissingRange.Value.EndChunkIndexExclusive);
        var highestBufferedAdvanced = highestBufferedChunkIndex > context.LastMissingRangeHighestBufferedChunkIndex;
        if (context.OutstandingMissingRange == range &&
            !highestBufferedAdvanced &&
            context.LastMissingRangeSentUtc is not null &&
            DateTimeOffset.UtcNow - context.LastMissingRangeSentUtc.Value < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
        {
            return false;
        }

        if (context.OutstandingMissingRange is not null &&
            !narrowedRange &&
            !highestBufferedAdvanced &&
            context.LastMissingRangeSentUtc is not null &&
            DateTimeOffset.UtcNow - context.LastMissingRangeSentUtc.Value < TimeSpan.FromMilliseconds(RepeatedMissingRangeMinIntervalMs))
        {
            return false;
        }

        context.OutstandingMissingRange = range;
        context.LastMissingRangeSentUtc = DateTimeOffset.UtcNow;
        context.LastMissingRangeHighestBufferedChunkIndex = highestBufferedChunkIndex;
        RecordMissingRangeSentLocked(context);
        message = new FileTransferMissingRangeV1
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = range.StartChunkIndex,
            EndChunkIndexExclusive = range.EndChunkIndexExclusive,
        };
        return true;
    }

    private static bool ShouldDeferGrantExtensionDueToGapLocked(
        InboundTransferContext context,
        int highestBufferedChunkIndex,
        int targetGrantedUntilExclusive)
    {
        if (context.OldestGapStartChunkIndex is null ||
            highestBufferedChunkIndex < context.NextChunkIndex)
        {
            return false;
        }

        return targetGrantedUntilExclusive > context.LastAdvertisedGrantedUntilExclusive ||
               highestBufferedChunkIndex > context.LastAdvertisedCreditFrontier;
    }

    private static bool ShouldLogGapDeferredLocked(InboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.LastGapExtensionDeferredLogUtc is not null &&
            now - context.LastGapExtensionDeferredLogUtc.Value < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
        {
            return false;
        }

        context.LastGapExtensionDeferredLogUtc = now;
        return true;
    }

    private int GetLocalSendAheadClampChunksLocked(OutboundTransferContext context)
    {
        if (context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly)
        {
            return sessionScreenShareActive
                ? LocalSendAheadClampDegradedWhileScreenshareChunks
                : LocalSendAheadClampDegradedChunks;
        }

        if (context.RepairModeActive)
        {
            return sessionScreenShareActive
                ? LocalSendAheadClampDegradedWhileScreenshareChunks
                : LocalSendAheadClampDegradedChunks;
        }

        return LocalSendAheadClampChunks;
    }

    private int GetEffectiveSendLimitExclusiveLocked(OutboundTransferContext context)
    {
        var localClampExclusive = context.RemoteNextExpectedChunkIndex + GetLocalSendAheadClampChunksLocked(context);
        return Math.Min(context.RemoteGrantedUntilExclusive, localClampExclusive);
    }

    private static bool ShouldLogSendAheadClampLocked(OutboundTransferContext context)
    {
        var now = DateTimeOffset.UtcNow;
        if (context.LastSendAheadClampLogUtc is not null &&
            now - context.LastSendAheadClampLogUtc.Value < TimeSpan.FromMilliseconds(MissingRangeCooldownMs))
        {
            return false;
        }

        context.LastSendAheadClampLogUtc = now;
        return true;
    }

    private void MaybeLogSendAheadClamp(
        OutboundTransferContext context,
        int nextChunkIndexToRead,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int effectiveSendLimitExclusive)
    {
        if (effectiveSendLimitExclusive >= remoteGrantedUntilExclusive)
        {
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || !ShouldLogSendAheadClampLocked(context))
            {
                return;
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_send_ahead_clamped; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}");
    }

    private bool IsTransferDegradedLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            (outboundTransfer.PullSessionDegraded ||
             outboundTransfer.RepairModeActive ||
             outboundTransfer.RemotePressureMode == FileTransferPressureMode.CatchUpOnly)) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            (inboundTransfer.PullSessionDegraded ||
             inboundTransfer.BulkFallbackModeActive ||
             inboundTransfer.DegradedRepairModeActive ||
             inboundTransfer.LocalPressureMode == FileTransferPressureMode.CatchUpOnly));

    private bool IsCatchUpOnlyPressureActiveLocked()
        => (outboundTransfer is not null &&
            !outboundTransfer.IsTerminal &&
            outboundTransfer.RemotePressureMode == FileTransferPressureMode.CatchUpOnly) ||
           (inboundTransfer is not null &&
            !inboundTransfer.IsTerminal &&
            inboundTransfer.LocalPressureMode == FileTransferPressureMode.CatchUpOnly);

    private async Task WaitForOutboundControlActivityAsync(OutboundTransferContext context, string reason)
    {
        Task signalTask;
        DateTimeOffset deadlineUtc;
        int nextChunkIndexToRead = 0;
        int remoteNextExpectedChunkIndex = 0;
        int remoteGrantedUntilExclusive = 0;
        int effectiveSendLimitExclusive = 0;
        bool repairOnlyModeActive = false;
        DateTimeOffset? lastWindowUpdateUtc = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (string.Equals(reason, "window", StringComparison.Ordinal) &&
                (context.NextChunkIndexToRead < GetEffectiveSendLimitExclusiveLocked(context) || context.PendingRepairChunkIndices.Count > 0))
            {
                return;
            }

            if (string.Equals(reason, "completion", StringComparison.Ordinal) &&
                (context.RemoteNextExpectedChunkIndex >= context.ChunkCount || context.PendingRepairChunkIndices.Count > 0))
            {
                return;
            }

            if (string.Equals(reason, "repair_only", StringComparison.Ordinal) &&
                (!context.RepairOnlyModeActive || context.NextChunkIndexToRead <= context.RemoteNextExpectedChunkIndex))
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            deadlineUtc = context.LastWindowUpdateUtc + OutboundWindowTimeout;
            nextChunkIndexToRead = context.NextChunkIndexToRead;
            remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
            remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
            effectiveSendLimitExclusive = GetEffectiveSendLimitExclusiveLocked(context);
            repairOnlyModeActive = context.RepairOnlyModeActive;
            lastWindowUpdateUtc = context.LastWindowUpdateUtc;
        }

        if (string.Equals(reason, "window", StringComparison.Ordinal))
        {
            MaybeLogSendAheadClamp(context, nextChunkIndexToRead, remoteNextExpectedChunkIndex, remoteGrantedUntilExclusive, effectiveSendLimitExclusive);
            if (context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly &&
                effectiveSendLimitExclusive < remoteGrantedUntilExclusive)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=sequential_send_blocked_by_pressure; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; pressure_mode={FormatPressureMode(context.RemotePressureMode)}; pressure_revision={context.RemotePressureRevision}; last_window_update_utc={lastWindowUpdateUtc:O}");
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=window_waiting_for_credit; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; last_window_update_utc={lastWindowUpdateUtc:O}");
            }
        }
        else if (string.Equals(reason, "repair_only", StringComparison.Ordinal) && repairOnlyModeActive)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=repair_only_waiting_for_catchup; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; next_chunk_to_read={nextChunkIndexToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; effective_send_limit={effectiveSendLimitExclusive}; last_window_update_utc={lastWindowUpdateUtc:O}");
        }

        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: WindowTimeoutErrorCode,
                statusMessage: "Receiver window update was not received in time.",
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(signalTask, Task.Delay(remaining, context.LifetimeCts.Token)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
            return;
        }

        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: WindowTimeoutErrorCode,
            statusMessage: "Receiver window update was not received in time.",
            notifyPeer: true,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WaitForOutboundCompletionSignalAsync(OutboundTransferContext context)
    {
        Task signalTask;
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                context.PendingRepairChunkIndices.Count > 0 ||
                context.RepairModeActive)
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            cancellationToken = context.LifetimeCts.Token;
        }

        var completed = await Task.WhenAny(
            signalTask,
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
        }
    }

    private async Task WaitForOutboundRepairActivityAsync(OutboundTransferContext context, TimeSpan delay)
    {
        Task signalTask;
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !context.RepairModeActive)
            {
                return;
            }

            signalTask = context.ResetAndGetControlSignalTask();
            cancellationToken = context.LifetimeCts.Token;
        }

        if (delay <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        var completed = await Task.WhenAny(
            signalTask,
            Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        if (completed == signalTask)
        {
            await signalTask.ConfigureAwait(false);
        }
    }

    private static bool TryPrepareRepairChunkLocked(
        OutboundTransferContext context,
        int effectiveSendLimitExclusive,
        out FileTransferChunkV1? message,
        out TimeSpan waitDelay,
        out string? logEvent,
        out int chunkIndex,
        out int repairRangeStartChunkIndex,
        out int repairRangeEndChunkExclusive,
        out int pendingBatchCount)
    {
        message = null;
        waitDelay = TimeSpan.Zero;
        logEvent = null;
        chunkIndex = -1;
        repairRangeStartChunkIndex = context.RepairRangeStartChunkIndex ?? -1;
        repairRangeEndChunkExclusive = context.RepairRangeEndChunkExclusive ?? -1;
        pendingBatchCount = context.PendingRepairChunkIndices.Count;

        if (!context.RepairModeActive ||
            context.RepairRangeStartChunkIndex is null ||
            context.RepairRangeEndChunkExclusive is null)
        {
            return false;
        }

        if (context.RemoteNextExpectedChunkIndex >= context.RepairRangeEndChunkExclusive.Value)
        {
            PromoteDeferredRepairRangeOrClearLocked(context);
            return false;
        }

        if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out var unavailableChunkIndex))
        {
            context.LastRepairSendUtc = DateTimeOffset.UtcNow;
            context.LastRepairChunkSentIndex = chunkIndex;
            logEvent = context.RepairSendCycle == 0 ? "repair_chunk_sent" : "repair_chunk_resent";
            pendingBatchCount = context.PendingRepairChunkIndices.Count;
            return true;
        }

        if (unavailableChunkIndex >= 0)
        {
            context.LastRepairSendUtc = DateTimeOffset.UtcNow;
            context.LastRepairChunkSentIndex = unavailableChunkIndex;
            logEvent = "repair_chunk_unavailable";
            chunkIndex = unavailableChunkIndex;
            waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
            pendingBatchCount = context.PendingRepairChunkIndices.Count;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (context.PendingRepairChunkIndices.Count == 0 &&
            !context.RepairBatchInFlight)
        {
            EnqueueNextRepairBatchLocked(context, effectiveSendLimitExclusive);
            if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out unavailableChunkIndex))
            {
                context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                context.LastRepairChunkSentIndex = chunkIndex;
                logEvent = context.RepairSendCycle == 0 ? "repair_chunk_sent" : "repair_chunk_resent";
                pendingBatchCount = context.PendingRepairChunkIndices.Count;
                return true;
            }

            if (unavailableChunkIndex >= 0)
            {
                context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                context.LastRepairChunkSentIndex = unavailableChunkIndex;
                logEvent = "repair_chunk_unavailable";
                chunkIndex = unavailableChunkIndex;
                waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
                pendingBatchCount = context.PendingRepairChunkIndices.Count;
                return true;
            }
        }

        if (context.RepairBatchInFlight)
        {
            var resendReferenceUtc = context.LastRepairSendUtc ?? now;
            if (context.LastRepairEvidenceUtc is not null &&
                context.LastRepairEvidenceUtc.Value > resendReferenceUtc)
            {
                resendReferenceUtc = context.LastRepairEvidenceUtc.Value;
            }

            var resendDueUtc = resendReferenceUtc.AddMilliseconds(GetRepairAckTimeoutMsLocked(context));
            if (context.LastRepairSendUtc is not null && now >= resendDueUtc)
            {
                context.RepairSendCycle++;
                ReleaseRepairBatchLocked(context);
                ClearPendingRepairQueueLocked(context);
                EnqueueNextRepairBatchLocked(context, effectiveSendLimitExclusive);
                if (TryDequeueRepairChunkLocked(context, effectiveSendLimitExclusive, out message, out chunkIndex, out unavailableChunkIndex))
                {
                    context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                    context.LastRepairChunkSentIndex = chunkIndex;
                    logEvent = "repair_chunk_resent";
                    pendingBatchCount = context.PendingRepairChunkIndices.Count;
                    return true;
                }

                if (unavailableChunkIndex >= 0)
                {
                    context.LastRepairSendUtc = DateTimeOffset.UtcNow;
                    context.LastRepairChunkSentIndex = unavailableChunkIndex;
                    logEvent = "repair_chunk_unavailable";
                    chunkIndex = unavailableChunkIndex;
                    waitDelay = TimeSpan.FromMilliseconds(GetRepairAckTimeoutMsLocked(context));
                    pendingBatchCount = context.PendingRepairChunkIndices.Count;
                    return true;
                }
            }

            waitDelay = resendDueUtc - DateTimeOffset.UtcNow;
            if (waitDelay < TimeSpan.Zero)
            {
                waitDelay = TimeSpan.Zero;
            }
        }

        pendingBatchCount = context.PendingRepairChunkIndices.Count;
        return true;
    }

    private static bool TryDequeueRepairChunkLocked(
        OutboundTransferContext context,
        int effectiveSendLimitExclusive,
        out FileTransferChunkV1? message,
        out int chunkIndex,
        out int unavailableChunkIndex)
    {
        chunkIndex = -1;
        unavailableChunkIndex = -1;
        while (context.PendingRepairChunkIndices.Count > 0)
        {
            chunkIndex = context.PendingRepairChunkIndices.Dequeue();
            context.PendingRepairChunkIndicesSet.Remove(chunkIndex);
            if (chunkIndex >= effectiveSendLimitExclusive)
            {
                context.PendingRepairChunkIndices.Enqueue(chunkIndex);
                context.PendingRepairChunkIndicesSet.Add(chunkIndex);
                message = null;
                unavailableChunkIndex = -1;
                return false;
            }

            if (chunkIndex < context.RemoteNextExpectedChunkIndex)
            {
                LogRepairChunkEvent(
                    "repair_chunk_skipped_obsolete",
                    context.TransferId,
                    context.SessionId,
                    chunkIndex,
                    context.RepairRangeStartChunkIndex ?? -1,
                    context.RepairRangeEndChunkExclusive ?? -1,
                    context.RemoteNextExpectedChunkIndex,
                    context.RemoteGrantedUntilExclusive,
                    context.CurrentRepairBatchSize,
                    context.PendingRepairChunkIndices.Count);
                continue;
            }

            if (context.SentChunkCache.TryGetValue(chunkIndex, out var cachedMessage))
            {
                message = cachedMessage;
                return true;
            }

            unavailableChunkIndex = chunkIndex;
            message = null;
            return false;
        }

        message = null;
        return false;
    }

    private static void ClearPendingRepairQueueLocked(OutboundTransferContext context)
    {
        context.PendingRepairChunkIndices.Clear();
        context.PendingRepairChunkIndicesSet.Clear();
    }

    private static void EnqueueRepairRangeLocked(OutboundTransferContext context, int rangeStartChunkIndex, int rangeEndChunkExclusive)
    {
        ClearPendingRepairQueueLocked(context);

        var start = Math.Max(rangeStartChunkIndex, context.RemoteNextExpectedChunkIndex);
        var end = Math.Min(rangeEndChunkExclusive, context.NextChunkIndexToRead);
        for (var chunkIndex = start; chunkIndex < end; chunkIndex++)
        {
            if (!context.PendingRepairChunkIndicesSet.Add(chunkIndex))
            {
                continue;
            }

            context.PendingRepairChunkIndices.Enqueue(chunkIndex);
        }
    }

    private static void EnqueueNextRepairBatchLocked(OutboundTransferContext context, int effectiveSendLimitExclusive)
    {
        if (context.RepairRangeStartChunkIndex is null || context.RepairRangeEndChunkExclusive is null)
        {
            ClearPendingRepairQueueLocked(context);
            ReleaseRepairBatchLocked(context);
            return;
        }

        var start = Math.Max(context.RepairRangeStartChunkIndex.Value, context.RemoteNextExpectedChunkIndex);
        var end = Math.Min(context.RepairRangeEndChunkExclusive.Value, Math.Min(start + context.CurrentRepairBatchSize, effectiveSendLimitExclusive));
        if (end <= start)
        {
            ClearPendingRepairQueueLocked(context);
            ReleaseRepairBatchLocked(context);
            return;
        }

        EnqueueRepairRangeLocked(context, start, end);
        context.RepairBatchInFlight = context.PendingRepairChunkIndices.Count > 0;
        context.OutstandingRepairBatchStartChunkIndex = start;
        context.OutstandingRepairBatchEndChunkExclusive = end;
    }

    private static void ReleaseRepairBatchLocked(OutboundTransferContext context)
    {
        context.RepairBatchInFlight = false;
        context.OutstandingRepairBatchStartChunkIndex = null;
        context.OutstandingRepairBatchEndChunkExclusive = null;
    }

    private void UpdateOutboundPressureDerivedStateLocked(OutboundTransferContext context)
    {
        var batchSize =
            context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly && sessionScreenShareActive
                ? 1
                : RepairBatchSize;
        if (context.RepairSingleChunkModeActive)
        {
            batchSize = 1;
        }

        context.CurrentRepairBatchSize = batchSize;
    }

    private static int GetRepairAckTimeoutMsLocked(OutboundTransferContext context)
        => context.RemotePressureMode == FileTransferPressureMode.CatchUpOnly
            ? 1000
            : RepairAckTimeoutMs;

    private static void ClearRepairModeLocked(OutboundTransferContext context)
    {
        context.RepairModeActive = false;
        context.RepairRangeStartChunkIndex = null;
        context.RepairRangeEndChunkExclusive = null;
        context.DeferredRepairRangeStartChunkIndex = null;
        context.DeferredRepairRangeEndChunkExclusive = null;
        context.LastRepairSendUtc = null;
        context.LastRepairAckObservedUtc = null;
        context.LastRepairEvidenceUtc = null;
        context.LastRepairRangeRequestedUtc = null;
        context.LastRepairChunkSentIndex = null;
        context.RepairSendCycle = 0;
        context.RepairSingleChunkModeActive = false;
        ReleaseRepairBatchLocked(context);
        ClearPendingRepairQueueLocked(context);
    }

    private static void EnterRepairOnlyModeLocked(OutboundTransferContext context)
    {
        if (context.RepairOnlyModeActive)
        {
            return;
        }

        context.RepairOnlyModeActive = true;
        LogRepairOnlyModeEvent(
            "repair_only_mode_entered",
            context.TransferId,
            context.SessionId,
            context.NextChunkIndexToRead,
            context.RemoteNextExpectedChunkIndex,
            context.RemoteGrantedUntilExclusive);
    }

    private static void TryExitRepairOnlyModeLocked(OutboundTransferContext context)
    {
        if (!context.RepairOnlyModeActive)
        {
            return;
        }

        if (context.RepairModeActive ||
            context.PendingRepairChunkIndices.Count > 0 ||
            context.RemoteNextExpectedChunkIndex < context.NextChunkIndexToRead)
        {
            return;
        }

        context.RepairOnlyModeActive = false;
        LogRepairOnlyModeEvent(
            "repair_only_mode_exited",
            context.TransferId,
            context.SessionId,
            context.NextChunkIndexToRead,
            context.RemoteNextExpectedChunkIndex,
            context.RemoteGrantedUntilExclusive);
    }

    private static void PromoteDeferredRepairRangeOrClearLocked(OutboundTransferContext context)
    {
        if (context.DeferredRepairRangeStartChunkIndex is not null &&
            context.DeferredRepairRangeEndChunkExclusive is not null &&
            context.DeferredRepairRangeEndChunkExclusive.Value > context.RemoteNextExpectedChunkIndex)
        {
            ActivateRepairRangeLocked(
                context,
                context.DeferredRepairRangeStartChunkIndex.Value,
                context.DeferredRepairRangeEndChunkExclusive.Value);
            context.DeferredRepairRangeStartChunkIndex = null;
            context.DeferredRepairRangeEndChunkExclusive = null;
            return;
        }

        ClearRepairModeLocked(context);
    }

    private static void ActivateRepairRangeLocked(OutboundTransferContext context, int rangeStartChunkIndex, int rangeEndChunkExclusive)
    {
        context.RepairModeActive = true;
        context.RepairRangeStartChunkIndex = rangeStartChunkIndex;
        context.RepairRangeEndChunkExclusive = rangeEndChunkExclusive;
        context.LastRepairSendUtc = null;
        context.LastRepairAckObservedUtc = null;
        context.LastRepairEvidenceUtc = DateTimeOffset.UtcNow;
        context.LastRepairRangeRequestedUtc = DateTimeOffset.UtcNow;
        context.LastRepairChunkSentIndex = null;
        context.RepairSendCycle = 0;
        context.RepairSingleChunkModeActive = true;
        ReleaseRepairBatchLocked(context);
        EnqueueNextRepairBatchLocked(context, context.RemoteGrantedUntilExclusive);
    }

    private static void PruneSentChunkCache(OutboundTransferContext context, int nextExpectedChunkIndex)
    {
        if (context.SentChunkCache.Count == 0)
        {
            return;
        }

        List<int>? staleKeys = null;
        foreach (var key in context.SentChunkCache.Keys)
        {
            if (key >= nextExpectedChunkIndex)
            {
                continue;
            }

            staleKeys ??= [];
            staleKeys.Add(key);
        }

        if (staleKeys is null)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            context.SentChunkCache.Remove(key);
            context.PendingRepairChunkIndicesSet.Remove(key);
        }
    }

    private static void UpdateOutboundAcknowledgedProgressLocked(OutboundTransferContext context)
    {
        var acknowledgedChunks = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var acknowledgedBytes = acknowledgedChunks >= context.ChunkCount
            ? context.FileSizeBytes
            : Math.Min(context.FileSizeBytes, (long)acknowledgedChunks * context.ChunkSizeBytes);
        var awaitingCompletion =
            context.NextChunkIndexToRead >= context.ChunkCount &&
            context.PendingRepairChunkIndices.Count == 0 &&
            !context.RepairModeActive;

        context.BytesTransferred = acknowledgedBytes;
        context.ChunksTransferred = acknowledgedChunks;
        context.State = awaitingCompletion || acknowledgedChunks >= context.ChunkCount
            ? FileTransferTransferState.AwaitingCompletion
            : FileTransferTransferState.Sending;
        context.StatusMessage = awaitingCompletion || acknowledgedChunks >= context.ChunkCount
            ? "Waiting for receiver verification."
            : "Sending file data.";
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

            context.CancelLifetime();
            context.State = terminalState;
            context.ErrorCode = NormalizeErrorCode(errorCode);
            context.StatusMessage = NormalizeReason(statusMessage) ?? statusMessage;
            if (terminalState == FileTransferTransferState.Completed)
            {
                context.BytesTransferred = context.FileSizeBytes;
                context.ChunksTransferred = context.ChunkCount;
                context.BytesAcceptedForTransport = context.FileSizeBytes;
                context.ChunksAcceptedForTransport = context.ChunkCount;
            }
            snapshot = CreateSnapshotLocked();
            shouldNotifyPeer = notifyPeer;
        }

        RaiseTransferChanged(snapshot);
        context.DisposeResources();
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

    private void FailOutboundLocally(OutboundTransferContext context, string failureCode, string failureMessage)
    {
        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.CancelLifetime();
            context.State = FileTransferTransferState.Failed;
            context.ErrorCode = NormalizeErrorCode(failureCode);
            context.StatusMessage = NormalizeReason(failureMessage) ?? failureMessage;
            snapshot = CreateSnapshotLocked();
        }

        context.DisposeResources();
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
            previousTransport.FileTransferSessionOpenReceived -= OnFileTransferSessionOpenReceived;
            previousTransport.FileTransferStartReceived -= OnFileTransferStartReceived;
            previousTransport.FileTransferChunkReceived -= OnFileTransferChunkReceived;
            previousTransport.FileTransferWindowUpdateReceived -= OnFileTransferWindowUpdateReceived;
            previousTransport.FileTransferMissingRangeReceived -= OnFileTransferMissingRangeReceived;
            previousTransport.FileTransferPressureStateReceived -= OnFileTransferPressureStateReceived;
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

        var chunkSizeBytes = descriptor.ChunkSizeBytes ?? PullHealthyDefaultChunkSizeBytes;
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

    private Task HandleIncomingWindowUpdateAsync(FileTransferWindowUpdateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboundTransferContext? context;
        SessionFileTransferSnapshot? snapshot = null;
        string? repairAckLogEvent = null;
        int repairAckChunkIndex = -1;
        int repairRangeStartChunkIndex = -1;
        int repairRangeEndChunkExclusive = -1;
        int remoteGrantedUntilExclusive = 0;
        int remoteNextExpectedChunkIndex = 0;

        lock (gate)
        {
            context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (context.ChunkCount > 0 &&
                (message.NextExpectedChunkIndex < 0 ||
                 message.NextExpectedChunkIndex > context.ChunkCount ||
                 message.GrantedUntilChunkIndexExclusive < message.NextExpectedChunkIndex ||
                 message.GrantedUntilChunkIndexExclusive > context.ChunkCount))
            {
                return Task.CompletedTask;
            }

            if (message.NextExpectedChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                var previousNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
                context.RemoteNextExpectedChunkIndex = message.NextExpectedChunkIndex;
                PruneSentChunkCache(context, message.NextExpectedChunkIndex);
                if (context.RepairModeActive &&
                    context.RepairRangeStartChunkIndex is not null &&
                    context.RepairRangeEndChunkExclusive is not null &&
                    message.NextExpectedChunkIndex > previousNextExpectedChunkIndex)
                {
                    context.LastRepairAckObservedUtc = DateTimeOffset.UtcNow;
                    context.LastRepairEvidenceUtc = context.LastRepairAckObservedUtc;
                    context.LastRepairChunkSentIndex = null;
                    context.RepairSingleChunkModeActive = false;
                    repairAckLogEvent = "repair_chunk_acknowledged";
                    repairAckChunkIndex = Math.Min(
                        message.NextExpectedChunkIndex - 1,
                        context.RepairRangeEndChunkExclusive.Value - 1);
                    repairRangeStartChunkIndex = context.RepairRangeStartChunkIndex.Value;
                    repairRangeEndChunkExclusive = context.RepairRangeEndChunkExclusive.Value;
                    if (message.NextExpectedChunkIndex >= context.RepairRangeEndChunkExclusive.Value)
                    {
                        PromoteDeferredRepairRangeOrClearLocked(context);
                    }
                    else
                    {
                        context.RepairRangeStartChunkIndex = Math.Max(
                            message.NextExpectedChunkIndex,
                            context.RepairRangeStartChunkIndex.Value);
                        ReleaseRepairBatchLocked(context);
                        context.LastRepairSendUtc = null;
                        context.RepairSendCycle = 0;
                        ClearPendingRepairQueueLocked(context);
                    }

                    UpdateOutboundPressureDerivedStateLocked(context);
                }
            }

            if (message.GrantedUntilChunkIndexExclusive > context.RemoteGrantedUntilExclusive)
            {
                context.RemoteGrantedUntilExclusive = message.GrantedUntilChunkIndexExclusive;
            }

            context.LastWindowUpdateUtc = DateTimeOffset.UtcNow;
            TryExitRepairOnlyModeLocked(context);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            remoteGrantedUntilExclusive = context.RemoteGrantedUntilExclusive;
            remoteNextExpectedChunkIndex = context.RemoteNextExpectedChunkIndex;
            snapshot = CreateSnapshotLocked();
        }

        LogWindowUpdateReceived(message);
        if (repairAckLogEvent is not null)
        {
            LogRepairChunkEvent(
                repairAckLogEvent,
                message.TransferId,
                message.SessionId,
                repairAckChunkIndex,
                repairRangeStartChunkIndex,
                repairRangeEndChunkExclusive,
                remoteNextExpectedChunkIndex,
                remoteGrantedUntilExclusive,
                context!.CurrentRepairBatchSize,
                pendingBatchCount: 0);
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context!, FileTransferDirection.Outbound);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingMissingRangeAsync(FileTransferMissingRangeV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            var context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var start = Math.Max(message.StartChunkIndex, context.RemoteNextExpectedChunkIndex);
            var end = Math.Min(message.EndChunkIndexExclusive, context.NextChunkIndexToRead);
            if (end > start)
            {
                var now = DateTimeOffset.UtcNow;
                EnterRepairOnlyModeLocked(context);
                var hasActiveRange =
                    context.RepairModeActive &&
                    context.RepairRangeStartChunkIndex is not null &&
                    context.RepairRangeEndChunkExclusive is not null &&
                    context.RemoteNextExpectedChunkIndex < context.RepairRangeEndChunkExclusive.Value;
                var activeRangeStart = context.RepairRangeStartChunkIndex ?? -1;
                var activeRangeEnd = context.RepairRangeEndChunkExclusive ?? -1;
                var repeatsActiveRange =
                    hasActiveRange &&
                    start == activeRangeStart &&
                    end == activeRangeEnd;
                var evolvedActiveRange =
                    hasActiveRange &&
                    start >= activeRangeStart &&
                    end <= activeRangeEnd &&
                    (start > activeRangeStart || end < activeRangeEnd);
                var canReplaceActiveRange =
                    !hasActiveRange ||
                    start <= activeRangeStart;
                if (repeatsActiveRange)
                {
                    context.LastRepairRangeRequestedUtc = now;
                    context.RepairSingleChunkModeActive = true;
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (evolvedActiveRange)
                {
                    context.RepairRangeStartChunkIndex = start;
                    context.RepairRangeEndChunkExclusive = end;
                    context.LastRepairRangeRequestedUtc = now;
                    context.LastRepairEvidenceUtc = now;
                    context.LastRepairSendUtc = null;
                    context.LastRepairChunkSentIndex = null;
                    context.RepairSendCycle = 0;
                    context.RepairSingleChunkModeActive = true;
                    ReleaseRepairBatchLocked(context);
                    ClearPendingRepairQueueLocked(context);
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (canReplaceActiveRange)
                {
                    ActivateRepairRangeLocked(context, start, end);
                    UpdateOutboundPressureDerivedStateLocked(context);
                }
                else if (context.DeferredRepairRangeStartChunkIndex is null ||
                         start <= context.DeferredRepairRangeStartChunkIndex.Value)
                {
                    context.DeferredRepairRangeStartChunkIndex = start;
                    context.DeferredRepairRangeEndChunkExclusive = end;
                }
                else if (context.DeferredRepairRangeEndChunkExclusive is not null)
                {
                    context.DeferredRepairRangeEndChunkExclusive = Math.Max(
                        context.DeferredRepairRangeEndChunkExclusive.Value,
                        end);
                }
            }

            LogMissingRangeReceived(
                message,
                context.RemoteNextExpectedChunkIndex,
                context.NextChunkIndexToRead - 1);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return Task.CompletedTask;
    }

    private Task HandleIncomingPressureStateAsync(FileTransferPressureStateV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        SessionFileTransferSnapshot? snapshot = null;

        lock (gate)
        {
            var context = outboundTransfer;
            if (context is null ||
                context.IsTerminal ||
                !string.Equals(context.TransferId, message.TransferId, StringComparison.Ordinal) ||
                !string.Equals(context.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (message.Revision <= context.RemotePressureRevision ||
                !TryParsePressureMode(message.Mode, out var mode) ||
                !TryParsePressureReason(message.Reason, out var reason))
            {
                return Task.CompletedTask;
            }

            context.RemotePressureRevision = message.Revision;
            context.RemotePressureMode = mode;
            context.RemotePressureReason = reason;
            context.RemotePressureSuggestedSendAheadChunks = Math.Max(0, message.SuggestedSendAheadChunks);
            context.RemotePressureReceiverNextExpectedChunkIndex = Math.Max(context.RemotePressureReceiverNextExpectedChunkIndex, message.ReceiverNextExpectedChunkIndex);
            if (message.ReceiverNextExpectedChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                context.RemoteNextExpectedChunkIndex = message.ReceiverNextExpectedChunkIndex;
                PruneSentChunkCache(context, context.RemoteNextExpectedChunkIndex);
            }

            UpdateOutboundPressureDerivedStateLocked(context);
            context.SignalControlActivity();
            UpdateOutboundAcknowledgedProgressLocked(context);
            snapshot = CreateSnapshotLocked();
        }

        LogPressureStateReceived(message);
        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        return Task.CompletedTask;
    }

    private async Task RunOutboundPullSendLoopAsync(OutboundTransferContext context)
    {
        try
        {
            var currentTransport = GetTransportOrThrow();
            var initialPipelineDepth = ResolveOutboundInitialPipelineDepth();
            var sessionOpen = new FileTransferSessionOpenV2
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV2,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = context.ChunkSizeBytes,
                InitialPipelineDepth = initialPipelineDepth,
            };

            var dataSession = await currentTransport
                .OpenFileTransferDataSessionAsync(context.SessionId, context.TransferId, context.LifetimeCts.Token)
                .ConfigureAwait(false);

            lock (gate)
            {
                if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                {
                    dataSession.Dispose();
                    return;
                }

                ReplaceOutboundDataSessionLocked(context, dataSession);
                context.PullSessionActive = true;
                context.PullCurrentPipelineDepth = initialPipelineDepth;
                context.RequestedButUnsent.Clear();
                context.GrantedOutstandingChunks.Clear();
                context.PullSentChunkCache.Clear();
            }

            await currentTransport.SendFileTransferSessionOpenAsync(sessionOpen, context.LifetimeCts.Token).ConfigureAwait(false);
            LogTransferInfo(
                "filetransfer_session_opened",
                FileTransferDirection.Outbound,
                context.TransferId,
                sessionId: context.SessionId,
                reason: $"role={sessionOpen.SessionRole}; chunk_size_bytes={sessionOpen.ChunkSizeBytes}; pipeline_depth={sessionOpen.InitialPipelineDepth}");
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Outbound, initialPipelineDepth, degraded: sessionScreenShareDegraded);

            using var stream = await context.OpenReadStreamAsync(context.LifetimeCts.Token).ConfigureAwait(false);
            ValidateReadableStream(stream);

            await dataSession.SendAsync(
                    new FileTransferManifestFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        FileName = context.FileName,
                        FileSizeBytes = context.FileSizeBytes,
                        ChunkSizeBytes = context.ChunkSizeBytes,
                        ChunkCount = context.ChunkCount,
                        Sha256Base64 = context.Sha256Base64!,
                    },
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);
            LogPullBinaryFrameSent(
                context.TransferId,
                context.SessionId,
                new FileTransferManifestFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    FileName = context.FileName,
                    FileSizeBytes = context.FileSizeBytes,
                    ChunkSizeBytes = context.ChunkSizeBytes,
                    ChunkCount = context.ChunkCount,
                    Sha256Base64 = context.Sha256Base64!,
                },
                payloadBytes: 0);

            UpdateOutboundState(context, FileTransferTransferState.Sending, 0, 0, "Waiting for receiver requests.");

            Task<FileTransferDataFrameV2>? pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedOutboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferRequestChunksFrameV2 request:
                        await SendRequestedChunksAsync(context, stream, dataSession, request).ConfigureAwait(false);
                        break;
                    case FileTransferAckProgressFrameV2 ack:
                        ApplyOutboundAckProgress(context, ack);
                        await SendPendingRequestedChunksAsync(context, stream, dataSession).ConfigureAwait(false);
                        break;
                    case FileTransferCancelFrameV2 cancel:
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Canceled,
                            errorCode: CanceledReason,
                            statusMessage: cancel.Reason ?? "Transfer canceled by receiver.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    case FileTransferCompleteFrameV2:
                        await TransitionOutboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Completed,
                            errorCode: null,
                            statusMessage: "Transfer complete.",
                            notifyPeer: false,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_outbound_frame");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TransitionOutboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: ClassifyOutboundFailureErrorCode(ex, StreamReadFailedErrorCode),
                statusMessage: ex.Message,
                notifyPeer: true,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunInboundPullReceiveLoopAsync(InboundTransferContext context, FileTransferSessionOpenV2 sessionOpen)
    {
        try
        {
            var dataSession = context.DataSession ?? await GetTransportOrThrow()
                .OpenFileTransferDataSessionAsync(sessionOpen.SessionId, sessionOpen.TransferId, context.LifetimeCts.Token)
                .ConfigureAwait(false);

            if (!ReferenceEquals(context.DataSession, dataSession))
            {
                lock (gate)
                {
                    if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                    {
                        ReplaceInboundDataSessionLocked(context, dataSession);
                    }
                }
            }

            FileTransferManifestFrameV2? manifest = null;
            Task<FileTransferDataFrameV2>? pendingReceiveTask = null;
            while (manifest is null)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                if (frame is FileTransferManifestFrameV2 receivedManifest)
                {
                    manifest = receivedManifest;
                }
                else if (frame is FileTransferCancelFrameV2 cancel)
                {
                    await TransitionInboundToTerminalAsync(
                        context,
                        FileTransferTransferState.Canceled,
                        errorCode: CanceledReason,
                        statusMessage: cancel.Reason ?? "Transfer canceled by sender.",
                        sendError: false,
                        errorMessage: null,
                        cancelReason: null,
                        ct: CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                else
                {
                    LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "waiting_for_manifest");
                }
            }

            await InitializeInboundPullManifestAsync(context, manifest).ConfigureAwait(false);
            await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: false).ConfigureAwait(false);

            pendingReceiveTask = null;
            while (true)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                if (pendingReceiveTask is null)
                {
                    pendingReceiveTask = dataSession.ReceiveAsync(context.LifetimeCts.Token).AsTask();
                }
                else if (pendingReceiveTask.IsCompleted)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_receive_loop_overlap_detected; transfer_id={context.TransferId}; session_id={context.SessionId}; reason=completed_receive_task_reused");
                }

                var completed = await Task.WhenAny(
                        pendingReceiveTask,
                        Task.Delay(PullSessionReceivePollDelayMs, context.LifetimeCts.Token))
                    .ConfigureAwait(false);
                if (completed != pendingReceiveTask)
                {
                    if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
                    {
                        return;
                    }

                    await MaybeHandlePullRequestTimeoutAsync(context).ConfigureAwait(false);
                    continue;
                }

                var frame = await pendingReceiveTask.ConfigureAwait(false);
                pendingReceiveTask = null;
                LogPullDataFrameReceived(context.TransferId, context.SessionId, frame);
                switch (frame)
                {
                    case FileTransferChunkDataFrameV2 chunk:
                        await HandleInboundPullChunkAsync(context, chunk).ConfigureAwait(false);
                        break;
                    case FileTransferChunkBatchFrameV2 batch:
                        await HandleInboundPullChunkBatchAsync(context, batch).ConfigureAwait(false);
                        break;
                    case FileTransferCancelFrameV2 cancel:
                        await TransitionInboundToTerminalAsync(
                            context,
                            FileTransferTransferState.Canceled,
                            errorCode: CanceledReason,
                            statusMessage: cancel.Reason ?? "Transfer canceled by sender.",
                            sendError: false,
                            errorMessage: null,
                            cancelReason: null,
                            ct: CancellationToken.None).ConfigureAwait(false);
                        return;
                    default:
                        LogPullDataFrameIgnored(context.TransferId, context.SessionId, frame, "unexpected_inbound_frame");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.LifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: StreamWriteFailedErrorCode,
                statusMessage: ex.Message,
                sendError: true,
                errorMessage: ex.Message,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private int ResolveOutboundInitialPipelineDepth(OutboundTransferContext? context = null)
        => ResolveOutboundPipelineDepth(context);

    private int ResolveOutboundPipelineDepth(OutboundTransferContext? context = null)
    {
        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        return sessionScreenShareActive
            ? PullScreensharePipelineDepth
            : ResolveHealthyPipelineDepth(context?.ChunkSizeBytes ?? PullHealthyDefaultChunkSizeBytes);
    }

    private int ResolveInboundMaximumPipelineDepthLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        if (sessionScreenShareActive)
        {
            return PullScreensharePipelineDepth;
        }

        return context.PullSessionDegraded
            ? PullDegradedPipelineDepth
            : ResolveHealthyPipelineDepth(context.ChunkSizeBytes);
    }

    private int ResolveInboundMinimumPipelineDepthLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullDegradedScreensharePipelineDepth;
        }

        return sessionScreenShareActive
            ? PullScreensharePipelineDepth
            : PullDegradedPipelineDepth;
    }

    private int ResolveInboundRequestLowWatermarkLocked(InboundTransferContext context)
    {
        var pipelineDepth = context.PullCurrentPipelineDepth > 0
            ? context.PullCurrentPipelineDepth
            : ResolveInboundMaximumPipelineDepthLocked(context);
        if (pipelineDepth <= PullDegradedScreensharePipelineDepth)
        {
            return PullDegradedScreenshareLowWatermarkChunks;
        }

        if (sessionScreenShareActive)
        {
            return PullScreenshareLowWatermarkChunks;
        }

        if (pipelineDepth <= PullDegradedPipelineDepth)
        {
            return PullDegradedLowWatermarkChunks;
        }

        return ResolveHealthyLowWatermarkChunks(context.ChunkSizeBytes, pipelineDepth);
    }

    private int ResolveInboundAckThresholdLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return 1;
        }

        if (sessionScreenShareActive || context.PullSessionDegraded)
        {
            return 2;
        }

        return 8;
    }

    private int ResolveInboundAckCoalesceDelayMsLocked(InboundTransferContext context)
    {
        if (sessionScreenShareDegraded)
        {
            return PullSessionScreenshareAckCoalesceDelayMs;
        }

        if (sessionScreenShareActive)
        {
            return PullSessionScreenshareAckCoalesceDelayMs;
        }

        return context.PullSessionDegraded
            ? PullSessionDegradedAckCoalesceDelayMs
            : PullSessionHealthyAckCoalesceDelayMs;
    }

    private static int GetPullSessionRequestTimeoutMs(InboundTransferContext context)
        => context.PullSessionDegraded
            ? PullSessionDegradedRequestTimeoutMs
            : PullSessionHealthyRequestTimeoutMs;

    private static int GetPullSessionRequestTimeoutMsForOutbound(OutboundTransferContext context)
        => context.PullSessionDegraded
            ? PullSessionDegradedRequestTimeoutMs
            : PullSessionHealthyRequestTimeoutMs;

    private int GetPullSessionRetryResendGateMsForOutbound(OutboundTransferContext context)
        => context.PullSessionDegraded || sessionScreenShareActive || sessionScreenShareDegraded
            ? PullSessionDegradedRetryResendGateMs
            : PullSessionHealthyRetryResendGateMs;

    private int ResolveInboundPipelineDepthForLogging(InboundTransferContext context)
        => ResolveInboundMaximumPipelineDepthLocked(context);

    private static int ResolveHealthyPipelineDepth(int chunkSizeBytes)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        var depthByBudget = (int)Math.Ceiling((double)PullHealthyTargetInFlightBytes / normalizedChunkSize);
        return Math.Clamp(depthByBudget, PullHealthyMinimumPipelineDepth, PullHealthyMaximumPipelineDepthCap);
    }

    private static int ResolveHealthyLowWatermarkChunks(int chunkSizeBytes, int pipelineDepth)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        var chunksByBudget = PullHealthyLowWatermarkBytes / normalizedChunkSize;
        return Math.Clamp(chunksByBudget, PullHealthyLowWatermarkChunks, Math.Max(PullHealthyLowWatermarkChunks, pipelineDepth - 1));
    }

    private static int NextLowerPipelineDepth(int currentDepth, int minimumDepth)
    {
        if (currentDepth > PullHealthyPipelineDepth)
        {
            return Math.Max(PullHealthyPipelineDepth, minimumDepth);
        }

        if (currentDepth > PullDegradedPipelineDepth)
        {
            return Math.Max(PullDegradedPipelineDepth, minimumDepth);
        }

        if (currentDepth > PullScreensharePipelineDepth)
        {
            return Math.Max(PullScreensharePipelineDepth, minimumDepth);
        }

        if (currentDepth > PullDegradedScreensharePipelineDepth)
        {
            return Math.Max(PullDegradedScreensharePipelineDepth, minimumDepth);
        }

        return minimumDepth;
    }

    private static int NextHigherPipelineDepth(int currentDepth, int maximumDepth)
    {
        if (currentDepth < PullScreensharePipelineDepth)
        {
            return Math.Min(PullScreensharePipelineDepth, maximumDepth);
        }

        if (currentDepth < PullDegradedPipelineDepth)
        {
            return Math.Min(PullDegradedPipelineDepth, maximumDepth);
        }

        if (currentDepth < PullHealthyPipelineDepth)
        {
            return Math.Min(PullHealthyPipelineDepth, maximumDepth);
        }

        return maximumDepth;
    }

    private static int ResolveHealthyBundledChunkFrameCount(int chunkSizeBytes)
    {
        var normalizedChunkSize = Math.Max(1, chunkSizeBytes);
        return Math.Max(1, PullHealthyBundledRawBytesCap / normalizedChunkSize);
    }

    private bool RefreshInboundPullPipelineDepthLocked(InboundTransferContext context, bool allowRecoveryIncrease, out int previousDepth, out int updatedDepth)
    {
        previousDepth = context.PullCurrentPipelineDepth;
        var maximumDepth = ResolveInboundMaximumPipelineDepthLocked(context);
        var minimumDepth = ResolveInboundMinimumPipelineDepthLocked(context);

        if (previousDepth <= 0)
        {
            updatedDepth = maximumDepth;
        }
        else if (previousDepth > maximumDepth)
        {
            updatedDepth = maximumDepth;
        }
        else if (previousDepth < minimumDepth)
        {
            updatedDepth = minimumDepth;
        }
        else if (!allowRecoveryIncrease ||
                 previousDepth >= maximumDepth ||
                 context.PullLateArrivalDistance > 1)
        {
            updatedDepth = previousDepth;
        }
        else if (context.PullRecoverySinceUtc is null ||
                 DateTimeOffset.UtcNow - context.PullRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PullSessionRecoveryHoldMs))
        {
            updatedDepth = previousDepth;
        }
        else
        {
            updatedDepth = NextHigherPipelineDepth(previousDepth, maximumDepth);
            if (updatedDepth > previousDepth)
            {
                context.PullRecoverySinceUtc = DateTimeOffset.UtcNow;
                context.PullCurrentPipelineStep = updatedDepth;
            }
        }

        context.PullCurrentPipelineDepth = updatedDepth;
        return previousDepth != updatedDepth;
    }

    private static void LogPullPipelineChanged(string transferId, string sessionId, FileTransferDirection direction, int pipelineDepth, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pipeline_changed; direction={direction}; transfer_id={transferId}; session_id={sessionId}; pipeline_depth={pipelineDepth}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullPipelineRecoveryStep(string transferId, string sessionId, int previousDepth, int updatedDepth, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_pipeline_recovery_step; transfer_id={transferId}; session_id={sessionId}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullProfileStepDown(string transferId, string sessionId, string reason, int previousDepth, int updatedDepth, int chunkSizeBytes, int outstandingCount, int lateArrivalDistance)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_step_down; transfer_id={transferId}; session_id={sessionId}; reason={reason}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; chunk_size_bytes={chunkSizeBytes}; outstanding_count={outstandingCount}; late_arrival_distance={lateArrivalDistance}");
    }

    private static void LogPullProfileStepUp(string transferId, string sessionId, int previousDepth, int updatedDepth, int chunkSizeBytes, bool degraded)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_step_up; transfer_id={transferId}; session_id={sessionId}; previous_pipeline_depth={previousDepth}; updated_pipeline_depth={updatedDepth}; chunk_size_bytes={chunkSizeBytes}; degraded={(degraded ? "yes" : "no")}");
    }

    private static void LogPullReorderPressure(string transferId, string sessionId, int nextExpectedChunkIndex, int highestReceivedChunkIndex, int lateArrivalDistance, int outstandingCount, int pipelineDepth, int chunkSizeBytes)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_reorder_pressure; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; late_arrival_distance={lateArrivalDistance}; outstanding_count={outstandingCount}; pipeline_depth={pipelineDepth}; chunk_size_bytes={chunkSizeBytes}");
    }

    private static void LogGapFocusChanged(string transferId, string sessionId, bool active, int nextExpectedChunkIndex, int highestReceivedChunkIndex, int lateArrivalDistance)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_gap_focus_{(active ? "entered" : "exited")}; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_received_chunk={highestReceivedChunkIndex}; late_arrival_distance={lateArrivalDistance}");
    }

    private static void LogPullProfileClampForScreenshare(string transferId, string sessionId, string reason, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_clamped_for_screenshare; transfer_id={transferId}; session_id={sessionId}; reason={reason}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private static void LogPullProfileRecoveredAfterScreenshare(string transferId, string sessionId, int chunkSizeBytes, int pipelineDepth)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_recovered_after_screenshare; transfer_id={transferId}; session_id={sessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}");
    }

    private bool TryStepDownInboundPipelineLocked(InboundTransferContext context, string reason, int outstandingCount, out int previousDepth, out int updatedDepth)
    {
        previousDepth = context.PullCurrentPipelineDepth > 0
            ? context.PullCurrentPipelineDepth
            : ResolveInboundMaximumPipelineDepthLocked(context);
        updatedDepth = previousDepth;
        var minimumDepth = ResolveInboundMinimumPipelineDepthLocked(context);
        var now = DateTimeOffset.UtcNow;
        if (previousDepth <= minimumDepth)
        {
            return false;
        }

        if (context.PullLastProfileAdjustmentUtc is not null &&
            now - context.PullLastProfileAdjustmentUtc.Value < TimeSpan.FromMilliseconds(PullProfileAdjustmentCooldownMs))
        {
            return false;
        }

        updatedDepth = NextLowerPipelineDepth(previousDepth, minimumDepth);
        if (updatedDepth == previousDepth)
        {
            return false;
        }

        context.PullCurrentPipelineDepth = updatedDepth;
        context.PullCurrentPipelineStep = updatedDepth;
        context.PullRecoverySinceUtc = null;
        context.PullLastProfileAdjustmentUtc = now;
        ClearPendingInboundReorderStepDownLocked(context);
        LogPullProfileStepDown(
            context.TransferId,
            context.SessionId,
            reason,
            previousDepth,
            updatedDepth,
            context.ChunkSizeBytes,
            outstandingCount,
            context.PullLateArrivalDistance);
        return true;
    }

    private static void ClearPendingInboundReorderStepDownLocked(InboundTransferContext context)
    {
        context.PullReorderPressureSinceUtc = null;
        context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
    }

    private bool ShouldDelayHealthyReorderStepDownLocked(InboundTransferContext context, DateTimeOffset now)
    {
        if (sessionScreenShareActive ||
            sessionScreenShareDegraded ||
            context.PullSessionDegraded ||
            context.PullCurrentPipelineDepth < PullHealthyPipelineDepth)
        {
            ClearPendingInboundReorderStepDownLocked(context);
            return false;
        }

        if (context.PullReorderPressureSinceUtc is null)
        {
            context.PullReorderPressureSinceUtc = now;
            context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
            return true;
        }

        if (context.NextChunkIndex > context.PullReorderPressureFrontierChunkIndex)
        {
            context.PullReorderPressureSinceUtc = now;
            context.PullReorderPressureFrontierChunkIndex = context.NextChunkIndex;
            return true;
        }

        return now - context.PullReorderPressureSinceUtc.Value < TimeSpan.FromMilliseconds(PullHealthyReorderStepDownHoldMs);
    }

    private static void LogPullChunkProfile(string transferId, string? sessionId, int chunkSizeBytes, int pipelineDepth, bool screenshareActive, bool screenshareDegraded)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "(none)" : sessionId.Trim();
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_chunk_profile; transfer_id={transferId}; session_id={normalizedSessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}; screenshare_active={(screenshareActive ? "yes" : "no")}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_profile_selected; transfer_id={transferId}; session_id={normalizedSessionId}; chunk_size_bytes={chunkSizeBytes}; pipeline_depth={pipelineDepth}; screenshare_active={(screenshareActive ? "yes" : "no")}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");
    }

    private static void LogPullBatchCommit(string transferId, string sessionId, int contiguousChunkCount, int nextExpectedChunkIndex, long bytesCommitted)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_batch_commit; transfer_id={transferId}; session_id={sessionId}; contiguous_chunk_count={contiguousChunkCount}; next_expected_chunk={nextExpectedChunkIndex}; bytes_committed={bytesCommitted}");
    }

    private static void TrimRecentEvents(Queue<DateTimeOffset> events, DateTimeOffset now)
    {
        while (events.Count > 0 && now - events.Peek() > TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            events.Dequeue();
        }
    }

    private static void MaybeLogPullControlChatterWindow(InboundTransferContext context, string transferId, string sessionId, DateTimeOffset now)
    {
        TrimRecentEvents(context.RecentPullAckSentUtc, now);
        TrimRecentEvents(context.RecentPullRequestSentUtc, now);
        TrimRecentEvents(context.RecentPullChunkSentUtc, now);
        if (context.LastPullControlChatterLogUtc is not null &&
            now - context.LastPullControlChatterLogUtc.Value < TimeSpan.FromMilliseconds(PullControlChatterWindowMs))
        {
            return;
        }

        context.LastPullControlChatterLogUtc = now;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_control_chatter_window; transfer_id={transferId}; session_id={sessionId}; ack_count_recent={context.RecentPullAckSentUtc.Count}; request_count_recent={context.RecentPullRequestSentUtc.Count}; chunk_sent_count_recent={context.RecentPullChunkSentUtc.Count}; duplicate_request_ignored_count_recent={context.PullDuplicateRequestIgnoredCountRecent}; resend_suppressed_count_recent={context.PullResendSuppressedCountRecent}");
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_useful_payload_window; transfer_id={transferId}; session_id={sessionId}; useful_payload_bytes_recent={context.PullUsefulPayloadBytesRecent}; ack_count_recent={context.RecentPullAckSentUtc.Count}; request_count_recent={context.RecentPullRequestSentUtc.Count}; chunk_sent_count_recent={context.RecentPullChunkSentUtc.Count}; duplicate_request_ignored_count_recent={context.PullDuplicateRequestIgnoredCountRecent}; resend_suppressed_count_recent={context.PullResendSuppressedCountRecent}");
        context.PullDuplicateRequestIgnoredCountRecent = 0;
        context.PullResendSuppressedCountRecent = 0;
        context.PullUsefulPayloadBytesRecent = 0;
    }

    private async Task SendRequestedChunksAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        FileTransferRequestChunksFrameV2 request)
    {
        List<int> chunkIndicesToSend;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                if (request.RequestedChunkCount == 1)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_retry_abandoned_terminal; transfer_id={request.TransferId}; session_id={request.SessionId}; chunk_index={request.StartChunkIndex}");
                }
                return;
            }

            context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
            var startChunkIndex = Math.Max(0, request.StartChunkIndex);
            var maxChunkIndexExclusive = Math.Min(context.ChunkCount, startChunkIndex + Math.Max(1, request.RequestedChunkCount));
            for (var chunkIndex = startChunkIndex; chunkIndex < maxChunkIndexExclusive; chunkIndex++)
            {
                var isExplicitRetryRequest =
                    request.RequestedChunkCount == 1 &&
                    context.GrantedOutstandingChunks.Contains(chunkIndex) &&
                    context.LastChunkSentUtc.ContainsKey(chunkIndex);

                if (chunkIndex < context.ChunksTransferred)
                {
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_request_ignored_obsolete; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; next_expected_chunk={context.ChunksTransferred}");
                    continue;
                }

                if (isExplicitRetryRequest)
                {
                    var resendGateMs = GetPullSessionRetryResendGateMsForOutbound(context);
                    var lastSentUtc = context.LastChunkSentUtc[chunkIndex];
                    var millisecondsSinceLastSend = Math.Max(0, (int)(now - lastSentUtc).TotalMilliseconds);
                    var resendCountSinceAck = context.ChunkResendCountSinceAck.TryGetValue(chunkIndex, out var resendCount)
                        ? resendCount
                        : 0;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_retry_requested; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; milliseconds_since_last_send={millisecondsSinceLastSend}; resend_gate_ms={resendGateMs}; resend_count_since_ack={resendCountSinceAck}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");

                    if (now - lastSentUtc < TimeSpan.FromMilliseconds(resendGateMs))
                    {
                        context.PullResendSuppressedCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_retry_gate_blocked; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; milliseconds_since_last_send={millisecondsSinceLastSend}; resend_gate_ms={resendGateMs}; resend_count_since_ack={resendCountSinceAck}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");
                        continue;
                    }

                    context.RequestedButUnsent.Add(chunkIndex);
                    context.GrantedOutstandingChunks.Add(chunkIndex);
                    continue;
                }

                if (context.GrantedOutstandingChunks.Contains(chunkIndex))
                {
                    if (context.SentAwaitingAck.TryGetValue(chunkIndex, out var sentAtUtc) &&
                        now - sentAtUtc < TimeSpan.FromMilliseconds(GetPullSessionRequestTimeoutMsForOutbound(context)))
                    {
                        context.PullDuplicateRequestIgnoredCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_request_duplicate_ignored; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}");
                        continue;
                    }

                    if (context.RequestedButUnsent.Contains(chunkIndex))
                    {
                        context.PullResendSuppressedCountRecent++;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_resend_suppressed; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}");
                        continue;
                    }
                }

                context.RequestedButUnsent.Add(chunkIndex);
                context.GrantedOutstandingChunks.Add(chunkIndex);
            }
            chunkIndicesToSend = GetSendableRequestedChunksLocked(context);
        }

        if (chunkIndicesToSend.Count == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_grant_drain; transfer_id={context.TransferId}; session_id={context.SessionId}; sendable_chunk_count={chunkIndicesToSend.Count}; first_chunk_index={chunkIndicesToSend[0]}; last_chunk_index={chunkIndicesToSend[^1]}; sender_sendability_source=grant_only");

        await SendQueuedChunkIndicesAsync(context, stream, dataSession, chunkIndicesToSend).ConfigureAwait(false);
    }

    private List<int> GetSendableRequestedChunksLocked(OutboundTransferContext context)
        => context.RequestedButUnsent.ToList();

    private async Task SendPendingRequestedChunksAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession)
    {
        List<int> chunkIndicesToSend;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.PullCurrentPipelineDepth = ResolveOutboundPipelineDepth(context);
            chunkIndicesToSend = GetSendableRequestedChunksLocked(context);
        }

        if (chunkIndicesToSend.Count == 0)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_sender_grant_drain_after_ack; transfer_id={context.TransferId}; session_id={context.SessionId}; sendable_chunk_count={chunkIndicesToSend.Count}; first_chunk_index={chunkIndicesToSend[0]}; last_chunk_index={chunkIndicesToSend[^1]}; sender_sendability_source=grant_only");

        await SendQueuedChunkIndicesAsync(context, stream, dataSession, chunkIndicesToSend).ConfigureAwait(false);
    }

    private async Task SendQueuedChunkIndicesAsync(
        OutboundTransferContext context,
        Stream stream,
        IFileTransferDataSession dataSession,
        List<int> chunkIndicesToSend)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(context.ChunkSizeBytes);
        try
        {
            for (var chunkListIndex = 0; chunkListIndex < chunkIndicesToSend.Count; chunkListIndex++)
            {
                context.LifetimeCts.Token.ThrowIfCancellationRequested();
                var chunkIndex = chunkIndicesToSend[chunkListIndex];
                if (TryBuildBundledChunkFrame(
                        context,
                        stream,
                        buffer,
                        chunkIndicesToSend,
                        chunkListIndex,
                        out var bundledFrame,
                        out var bundledChunkIndexes))
                {
                    await dataSession.SendAsync(bundledFrame, context.LifetimeCts.Token).ConfigureAwait(false);
                    LogPullBinaryFrameSent(
                        context.TransferId,
                        context.SessionId,
                        bundledFrame,
                        bundledFrame.DataSegments.Sum(static segment => segment.Length));

                    foreach (var bundledChunkIndex in bundledChunkIndexes)
                    {
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={bundledChunkIndex}; chunk_bytes={bundledFrame.DataSegments[bundledChunkIndex - bundledFrame.StartChunkIndex].Length}");
                    }

                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        var sentUtc = DateTimeOffset.UtcNow;
                        foreach (var bundledChunkIndex in bundledChunkIndexes)
                        {
                            context.RequestedButUnsent.Remove(bundledChunkIndex);
                            context.SentAwaitingAck[bundledChunkIndex] = sentUtc;
                            context.LastChunkSentUtc[bundledChunkIndex] = sentUtc;
                            context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, bundledChunkIndex + 1);
                        }

                        context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                            ? context.FileSizeBytes
                            : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                        context.StatusMessage = "Streaming requested chunks.";
                        foreach (var _ in bundledChunkIndexes)
                        {
                            context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                        }
                        TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                        context.PullUsefulPayloadBytesRecent += bundledFrame.DataSegments.Sum(static segment => segment.Length);
                    }

                    chunkListIndex += bundledChunkIndexes.Count - 1;
                    continue;
                }

                FileTransferChunkDataFrameV2? frameToSend = null;
                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    context.PullSentChunkCache.TryGetValue(chunkIndex, out frameToSend);
                }

                if (frameToSend is null)
                {
                    var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
                    if (stream.CanSeek && stream.Position != fileOffset)
                    {
                        stream.Seek(fileOffset, SeekOrigin.Begin);
                    }

                    var remaining = context.FileSizeBytes - fileOffset;
                    var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, targetReadSize), context.LifetimeCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new InvalidOperationException("Source stream did not match the declared file size.");
                    }

                    var chunkBytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);
                    frameToSend = new FileTransferChunkDataFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        ChunkIndex = chunkIndex,
                        ChunkCount = context.ChunkCount,
                        Data = chunkBytes,
                    };

                    lock (gate)
                    {
                        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                        {
                            return;
                        }

                        context.PullSentChunkCache[chunkIndex] = frameToSend;
                    }
                }

                await dataSession.SendAsync(
                        frameToSend,
                        context.LifetimeCts.Token)
                    .ConfigureAwait(false);
                LogPullBinaryFrameSent(context.TransferId, context.SessionId, frameToSend, frameToSend.Data.Length);
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_chunk_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={frameToSend.Data.Length}");

                lock (gate)
                {
                    if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
                    {
                        return;
                    }

                    context.RequestedButUnsent.Remove(chunkIndex);
                    var sentUtc = DateTimeOffset.UtcNow;
                    var isRetrySend = context.LastChunkSentUtc.ContainsKey(chunkIndex);
                    context.SentAwaitingAck[chunkIndex] = sentUtc;
                    context.LastChunkSentUtc[chunkIndex] = sentUtc;
                    if (isRetrySend)
                    {
                        context.LastChunkResentUtc[chunkIndex] = sentUtc;
                        context.ChunkResendCountSinceAck[chunkIndex] = context.ChunkResendCountSinceAck.TryGetValue(chunkIndex, out var resendCount)
                            ? resendCount + 1
                            : 1;
                        LocalOperationalLog.Info(
                            "FileTransferService",
                            $"event=filetransfer_chunk_retry_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; resend_count_since_ack={context.ChunkResendCountSinceAck[chunkIndex]}; screenshare_active={(sessionScreenShareActive ? "yes" : "no")}; screenshare_degraded={(sessionScreenShareDegraded ? "yes" : "no")}");
                    }
                    context.ChunksAcceptedForTransport = Math.Max(context.ChunksAcceptedForTransport, chunkIndex + 1);
                    context.BytesAcceptedForTransport = context.ChunksAcceptedForTransport >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(context.FileSizeBytes, (long)context.ChunksAcceptedForTransport * context.ChunkSizeBytes);
                    context.StatusMessage = "Streaming requested chunks.";
                    context.RecentPullChunkSentUtc.Enqueue(sentUtc);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, sentUtc);
                    context.PullUsefulPayloadBytesRecent += frameToSend.Data.Length;
                }
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryBuildBundledChunkFrame(
        OutboundTransferContext context,
        Stream stream,
        byte[] buffer,
        IReadOnlyList<int> chunkIndicesToSend,
        int chunkListIndex,
        out FileTransferChunkBatchFrameV2 bundledFrame,
        out List<int> bundledChunkIndexes)
    {
        bundledFrame = null!;
        bundledChunkIndexes = [];
        var bundledChunkFrameCount = ResolveHealthyBundledChunkFrameCount(context.ChunkSizeBytes);

        if (!CanBundleOutboundChunkFrames(context) ||
            bundledChunkFrameCount <= 1 ||
            chunkListIndex + bundledChunkFrameCount - 1 >= chunkIndicesToSend.Count)
        {
            return false;
        }

        var firstChunkIndex = chunkIndicesToSend[chunkListIndex];
        lock (gate)
        {
            for (var segmentOffset = 0; segmentOffset < bundledChunkFrameCount; segmentOffset++)
            {
                var chunkIndex = chunkIndicesToSend[chunkListIndex + segmentOffset];
                if (chunkIndex != firstChunkIndex + segmentOffset ||
                    context.LastChunkSentUtc.ContainsKey(chunkIndex))
                {
                    return false;
                }
            }
        }

        var segments = new byte[bundledChunkFrameCount][];
        var totalBytes = 0;
        for (var segmentOffset = 0; segmentOffset < bundledChunkFrameCount; segmentOffset++)
        {
            var chunkIndex = chunkIndicesToSend[chunkListIndex + segmentOffset];
            var fileOffset = (long)chunkIndex * context.ChunkSizeBytes;
            if (stream.CanSeek && stream.Position != fileOffset)
            {
                stream.Seek(fileOffset, SeekOrigin.Begin);
            }

            var remaining = context.FileSizeBytes - fileOffset;
            var targetReadSize = (int)Math.Min(context.ChunkSizeBytes, remaining);
            var read = stream.Read(buffer, 0, targetReadSize);
            if (read <= 0)
            {
                throw new InvalidOperationException("Source stream did not match the declared file size.");
            }

            var chunkBytes = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunkBytes, 0, read);
            totalBytes += read;
            if (totalBytes > PullHealthyBundledRawBytesCap)
            {
                return false;
            }

            segments[segmentOffset] = chunkBytes;
        }

        var candidateFrame = new FileTransferChunkBatchFrameV2
        {
            SessionId = context.SessionId,
            TransferId = context.TransferId,
            StartChunkIndex = firstChunkIndex,
            ChunkCount = context.ChunkCount,
            DataSegments = segments,
        };

        _ = FileTransferDataFrameCodec.Serialize(candidateFrame);
        bundledFrame = candidateFrame;
        bundledChunkIndexes = Enumerable.Range(firstChunkIndex, bundledChunkFrameCount).ToList();
        return true;
    }

    private bool CanBundleOutboundChunkFrames(OutboundTransferContext context)
        => !sessionScreenShareActive &&
           !sessionScreenShareDegraded &&
           !context.PullSessionDegraded &&
           context.ChunkSizeBytes == PullHealthyDefaultChunkSizeBytes;

    private void ApplyOutboundAckProgress(OutboundTransferContext context, FileTransferAckProgressFrameV2 ack)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            context.ChunksTransferred = Math.Max(context.ChunksTransferred, Math.Min(ack.NextExpectedChunkIndex, context.ChunkCount));
            context.BytesTransferred = Math.Max(context.BytesTransferred, Math.Min(ack.BytesCommitted, context.FileSizeBytes));
            context.RequestedButUnsent.RemoveWhere(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex);
            context.GrantedOutstandingChunks.RemoveWhere(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex);
            foreach (var chunkIndex in context.SentAwaitingAck.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.SentAwaitingAck.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.LastChunkSentUtc.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.LastChunkSentUtc.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.LastChunkResentUtc.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.LastChunkResentUtc.Remove(chunkIndex);
            }
            foreach (var chunkIndex in context.ChunkResendCountSinceAck.Keys.Where(chunkIndex => chunkIndex < ack.NextExpectedChunkIndex).ToArray())
            {
                context.ChunkResendCountSinceAck.Remove(chunkIndex);
            }
            TrimOutboundPullSentChunkCache(context, ack.NextExpectedChunkIndex);
            context.StatusMessage = context.ChunksTransferred >= context.ChunkCount
                ? "Waiting for receiver verification."
                : "Receiver is acknowledging requested chunks.";
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }
    }

    private async Task InitializeInboundPullManifestAsync(InboundTransferContext context, FileTransferManifestFrameV2 manifest)
    {
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return;
            }

            if (!string.Equals(context.FileName, manifest.FileName, StringComparison.Ordinal) ||
                context.FileSizeBytes != manifest.FileSizeBytes ||
                !string.Equals(context.Sha256Base64, manifest.Sha256Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Manifest metadata did not match the original offer.");
            }

            context.ChunkCount = manifest.ChunkCount;
            context.ChunkSizeBytes = manifest.ChunkSizeBytes;
            context.State = FileTransferTransferState.Receiving;
            context.StatusMessage = "Receiving requested chunks.";
            context.PullManifestReceived = true;
            RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: false, out _, out _);
            context.PullCurrentPipelineStep = context.PullCurrentPipelineDepth;
            context.PullCurrentChunkSizeStep = context.ChunkSizeBytes;
            context.PullLastProgressUtc = DateTimeOffset.UtcNow;
            snapshot = CreateSnapshotLocked();
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
        }

        LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
    }

    private async Task<bool> MaybeSendNextChunkRequestAsync(InboundTransferContext context, bool forceResendOldestOutstanding)
    {
        FileTransferRequestChunksFrameV2? request = null;
        int? blockedOldestOutstandingChunk = null;
        int blockedRequestedUntilExclusive = 0;
        int batchExtensionCount = 0;
        bool retryingOldestOutstanding = false;
        int retryAttemptCount = 0;
        int previousPipelineDepth = 0;
        int updatedPipelineDepth = 0;
        bool pipelineDepthChanged = false;
        DateTimeOffset requestSentUtc = default;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived ||
                context.ChunkCount <= 0)
            {
                return false;
            }

            pipelineDepthChanged = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: true, out previousPipelineDepth, out updatedPipelineDepth);
            var outstandingCount = context.OutstandingChunkRequests.Count;
            if (forceResendOldestOutstanding && context.OutstandingChunkRequests.Count > 0)
            {
                var oldest = context.OutstandingChunkRequests.Keys.Min();
                request = new FileTransferRequestChunksFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    StartChunkIndex = oldest,
                    RequestedChunkCount = 1,
                    PipelineDepth = context.PullCurrentPipelineDepth,
                };
                context.OutstandingChunkRequests[oldest] = DateTimeOffset.UtcNow;
                context.RequestedChunks.Add(oldest);
                context.ChunkAttemptCounts[oldest] = context.ChunkAttemptCounts.TryGetValue(oldest, out var attempts)
                    ? attempts + 1
                    : 1;
                context.PullLastRequestSentUtc = DateTimeOffset.UtcNow;
                retryingOldestOutstanding = true;
                retryAttemptCount = context.ChunkAttemptCounts[oldest];
            }
            else
            {
                if (outstandingCount > 0)
                {
                    var oldestOutstanding = context.OutstandingChunkRequests.Keys.Min();
                    var lowWatermark = ResolveInboundRequestLowWatermarkLocked(context);
                    var desiredOutstanding = context.PullCurrentPipelineDepth;
                    var canExtendBatch =
                        !context.PullGapFocusActive &&
                        !sessionScreenShareDegraded &&
                        oldestOutstanding == context.NextChunkIndex &&
                        context.PendingChunks.Count == 0 &&
                        outstandingCount <= lowWatermark &&
                        context.PullRequestedFrontierExclusive < context.ChunkCount;

                    if (canExtendBatch)
                    {
                        var missingTailCount = desiredOutstanding - outstandingCount;
                        var requestCount = Math.Min(missingTailCount, context.ChunkCount - context.PullRequestedFrontierExclusive);
                        if (requestCount > 0)
                        {
                            var startChunkIndex = context.PullRequestedFrontierExclusive;
                            request = new FileTransferRequestChunksFrameV2
                            {
                                SessionId = context.SessionId,
                                TransferId = context.TransferId,
                                StartChunkIndex = startChunkIndex,
                                RequestedChunkCount = requestCount,
                                PipelineDepth = context.PullCurrentPipelineDepth,
                            };
                            requestSentUtc = DateTimeOffset.UtcNow;
                            for (var chunkIndex = startChunkIndex; chunkIndex < startChunkIndex + requestCount; chunkIndex++)
                            {
                                context.OutstandingChunkRequests[chunkIndex] = requestSentUtc;
                                context.RequestedChunks.Add(chunkIndex);
                                context.ChunkAttemptCounts[chunkIndex] = 1;
                            }

                            context.PullRequestedFrontierExclusive = startChunkIndex + requestCount;
                            context.PullLastRequestSentUtc = requestSentUtc;
                            batchExtensionCount = requestCount;
                        }
                    }

                    if (request is null)
                    {
                        blockedOldestOutstandingChunk = oldestOutstanding;
                        blockedRequestedUntilExclusive = context.PullRequestedFrontierExclusive;
                    }
                }
                else
                {
                    var desiredOutstanding = context.PullCurrentPipelineDepth;
                    var requestCount = desiredOutstanding;
                    if (requestCount <= 0)
                    {
                        return false;
                    }

                    var startChunkIndex = context.NextChunkIndex;
                    if (startChunkIndex >= context.ChunkCount)
                    {
                        return false;
                    }

                    requestCount = Math.Min(requestCount, context.ChunkCount - startChunkIndex);
                    if (requestCount <= 0)
                    {
                        return false;
                    }

                    request = new FileTransferRequestChunksFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        StartChunkIndex = startChunkIndex,
                        RequestedChunkCount = requestCount,
                        PipelineDepth = context.PullCurrentPipelineDepth,
                    };
                    requestSentUtc = DateTimeOffset.UtcNow;
                    for (var chunkIndex = startChunkIndex; chunkIndex < startChunkIndex + requestCount; chunkIndex++)
                    {
                        context.OutstandingChunkRequests[chunkIndex] = requestSentUtc;
                        context.RequestedChunks.Add(chunkIndex);
                        context.ChunkAttemptCounts[chunkIndex] = 1;
                    }

                    context.PullRequestedFrontierExclusive = startChunkIndex + requestCount;
                    context.PullLastRequestSentUtc = requestSentUtc;
                }
            }
        }

        if (blockedOldestOutstandingChunk is not null)
        {
            if (blockedOldestOutstandingChunk.Value == context.NextChunkIndex)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_request_window_blocked_by_oldest_gap; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; oldest_outstanding_chunk={blockedOldestOutstandingChunk.Value}; requested_until_exclusive={blockedRequestedUntilExclusive}");
            }
            else
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_refill_skipped_above_low_watermark; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; oldest_outstanding_chunk={blockedOldestOutstandingChunk.Value}; requested_until_exclusive={blockedRequestedUntilExclusive}");
            }
            return false;
        }

        if (pipelineDepthChanged)
        {
            if (updatedPipelineDepth > previousPipelineDepth)
            {
                LogPullProfileStepUp(context.TransferId, context.SessionId, previousPipelineDepth, updatedPipelineDepth, context.ChunkSizeBytes, context.PullSessionDegraded || sessionScreenShareDegraded);
            }
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
        }

        if (request is null || context.DataSession is null)
        {
            return false;
        }

        await context.DataSession.SendAsync(request, context.LifetimeCts.Token).ConfigureAwait(false);
        LogPullBinaryFrameSent(context.TransferId, context.SessionId, request, payloadBytes: 0);
        lock (gate)
        {
            if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
            {
                context.RecentPullRequestSentUtc.Enqueue(requestSentUtc == default ? DateTimeOffset.UtcNow : requestSentUtc);
                MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, DateTimeOffset.UtcNow);
            }
        }

        if (retryingOldestOutstanding)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_retry_oldest_chunk; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={request.StartChunkIndex}; attempt_count={retryAttemptCount}; pipeline_depth={request.PipelineDepth}");
        }
        else if (batchExtensionCount > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_grant_window_refilled; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_batch_extended; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_request_refill; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        }
        else
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_grant_window_opened; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        }
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_request_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; start_chunk={request.StartChunkIndex}; requested_chunk_count={request.RequestedChunkCount}; pipeline_depth={request.PipelineDepth}");
        return true;
    }

    private bool TryPauseOutboundTransportLocked(OutboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullSessionActive || context.DataSession is null)
        {
            return false;
        }

        if (context.PullTransportPaused)
        {
            context.PullTransportResumeRequestPending |= requiresResumeRequest;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = now;
        context.PullTransportGraceDeadlineUtc = now.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryPauseInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullSessionActive || context.DataSession is null)
        {
            return false;
        }

        if (context.PullTransportPaused)
        {
            context.PullTransportResumeRequestPending |= requiresResumeRequest;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = now;
        context.PullTransportGraceDeadlineUtc = now.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = reason;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryResumeOutboundTransportLocked(OutboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullTransportPaused)
        {
            return false;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        return true;
    }

    private bool TryResumeInboundTransportLocked(InboundTransferContext context, string reason, bool requiresResumeRequest)
    {
        if (!context.PullTransportPaused)
        {
            return false;
        }

        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = requiresResumeRequest;
        context.PullTimeoutOldestChunkIndex = null;
        context.PullTimeoutStreak = 0;
        context.PullFirstChunkTimeoutCount = 0;
        context.PullRecoverySinceUtc = null;
        return true;
    }

    private async Task<bool> HandlePausedOutboundTransportAsync(OutboundTransferContext context)
    {
        DateTimeOffset? graceDeadlineUtc;
        string reason;
        lock (gate)
        {
            if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            if (!context.PullTransportPaused)
            {
                return false;
            }

            graceDeadlineUtc = context.PullTransportGraceDeadlineUtc;
            reason = context.PullTransportPauseReason ?? "transport_disconnected";
        }

        if (graceDeadlineUtc is not null && DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_transport_grace_exhausted; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}");
        await TransitionOutboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Transport disconnected.",
            notifyPeer: false,
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandlePausedInboundTransportAsync(InboundTransferContext context)
    {
        DateTimeOffset? graceDeadlineUtc;
        string reason;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
            {
                return true;
            }

            if (!context.PullTransportPaused)
            {
                return false;
            }

            graceDeadlineUtc = context.PullTransportGraceDeadlineUtc;
            reason = context.PullTransportPauseReason ?? "transport_disconnected";
        }

        if (graceDeadlineUtc is not null && DateTimeOffset.UtcNow < graceDeadlineUtc.Value)
        {
            return false;
        }

        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_transport_grace_exhausted; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; reason={reason}");
        await TransitionInboundToTerminalAsync(
            context,
            FileTransferTransferState.Failed,
            errorCode: DisconnectedErrorCode,
            statusMessage: "Transport disconnected.",
            sendError: true,
            errorMessage: "Transport disconnected.",
            cancelReason: null,
            ct: CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private void ReplaceOutboundDataSessionLocked(OutboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return;
        }

        if (context.DataSession is not null)
        {
            context.DataSession.AvailabilityChanged -= OnDataSessionAvailabilityChanged;
            context.DataSession.Dispose();
        }

        context.DataSession = session;
        session.AvailabilityChanged += OnDataSessionAvailabilityChanged;
    }

    private void ReplaceInboundDataSessionLocked(InboundTransferContext context, IFileTransferDataSession session)
    {
        if (ReferenceEquals(context.DataSession, session))
        {
            return;
        }

        if (context.DataSession is not null)
        {
            context.DataSession.AvailabilityChanged -= OnDataSessionAvailabilityChanged;
            context.DataSession.Dispose();
        }

        context.DataSession = session;
        session.AvailabilityChanged += OnDataSessionAvailabilityChanged;
    }

    private async Task MaybeHandlePullRequestTimeoutAsync(InboundTransferContext context)
    {
        if (await HandlePausedInboundTransportAsync(context).ConfigureAwait(false))
        {
            return;
        }

        bool shouldResend = false;
        bool degradedChanged = false;
        bool failStalledFirstChunk = false;
        bool pipelineDepthChanged = false;
        bool gapFocusChanged = false;
        int oldestOutstandingChunkIndex = -1;
        int timeoutStreak = 0;
        int outstandingCount = 0;
        int pipelineDepth = 0;
        int highestReceivedChunkIndex = -1;
        int lateArrivalDistance = 0;
        bool screenshareDegraded = false;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.OutstandingChunkRequests.Count == 0)
            {
                return;
            }

            oldestOutstandingChunkIndex = context.OutstandingChunkRequests.Keys.Min();
            var oldest = context.OutstandingChunkRequests[oldestOutstandingChunkIndex];
            var timeoutMs = GetPullSessionRequestTimeoutMs(context);
            if (DateTimeOffset.UtcNow - oldest < TimeSpan.FromMilliseconds(timeoutMs))
            {
                TryRecoverInboundPullSessionLocked(context);
                return;
            }

            shouldResend = true;
            if (context.PullTimeoutOldestChunkIndex == oldestOutstandingChunkIndex)
            {
                context.PullTimeoutStreak++;
            }
            else
            {
                context.PullTimeoutOldestChunkIndex = oldestOutstandingChunkIndex;
                context.PullTimeoutStreak = 1;
            }

            timeoutStreak = context.PullTimeoutStreak;
            outstandingCount = context.OutstandingChunkRequests.Count;
            pipelineDepth = context.PullCurrentPipelineDepth;
            screenshareDegraded = sessionScreenShareDegraded;
            highestReceivedChunkIndex = context.PullHighestReceivedChunkIndex;
            lateArrivalDistance = context.PullLateArrivalDistance;

            if (outstandingCount > PullTimeoutOutstandingStepDownThreshold)
            {
                pipelineDepthChanged = TryStepDownInboundPipelineLocked(context, "oldest_chunk_timeout", outstandingCount, out _, out pipelineDepth);
            }

            var shouldEnterDegraded = sessionScreenShareDegraded || context.PullTimeoutStreak >= PullSessionDegradedEntryTimeoutStreakThreshold;
            degradedChanged = shouldEnterDegraded && !context.PullSessionDegraded;
            if (shouldEnterDegraded)
            {
                context.PullSessionDegraded = true;
                context.PullDegradedSinceUtc ??= DateTimeOffset.UtcNow;
            }

            pipelineDepthChanged = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: false, out _, out pipelineDepth) || pipelineDepthChanged;

            context.PullRecoverySinceUtc = null;
            if (!context.PullGapFocusActive &&
                (context.PullLateArrivalDistance >= PullGapFocusBufferedThreshold ||
                 context.PendingChunks.Count >= PullGapFocusBufferedThreshold))
            {
                context.PullGapFocusActive = true;
                gapFocusChanged = true;
            }
            if (context.NextChunkIndex == 0 && oldestOutstandingChunkIndex == 0)
            {
                context.PullFirstChunkTimeoutCount++;
                failStalledFirstChunk = context.PullFirstChunkTimeoutCount >= PullSessionFirstChunkStallTimeouts;
            }
            else
            {
                context.PullFirstChunkTimeoutCount = 0;
            }
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_request_timeout_detected; transfer_id={context.TransferId}; session_id={context.SessionId}; oldest_chunk={oldestOutstandingChunkIndex}; timeout_streak={timeoutStreak}; outstanding_count={outstandingCount}; pipeline_depth={pipelineDepth}; screenshare_degraded={(screenshareDegraded ? "yes" : "no")}");

        LogPullReorderPressure(
            context.TransferId,
            context.SessionId,
            context.NextChunkIndex,
            highestReceivedChunkIndex,
            lateArrivalDistance,
            outstandingCount,
            pipelineDepth,
            context.ChunkSizeBytes);

        if (degradedChanged)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_degraded_entered; transfer_id={context.TransferId}; session_id={context.SessionId}");
        }

        if (gapFocusChanged)
        {
            LogGapFocusChanged(context.TransferId, context.SessionId, active: true, context.NextChunkIndex, highestReceivedChunkIndex, lateArrivalDistance);
        }

        if (pipelineDepthChanged)
        {
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, pipelineDepth, degraded: context.PullSessionDegraded || screenshareDegraded);
        }

        if (failStalledFirstChunk)
        {
            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_pull_session_stalled_at_first_chunk; transfer_id={context.TransferId}; session_id={context.SessionId}");
            await TransitionInboundToTerminalAsync(
                context,
                FileTransferTransferState.Failed,
                errorCode: PullSessionStalledErrorCode,
                statusMessage: PullSessionStalledErrorCode,
                sendError: true,
                errorMessage: PullSessionStalledErrorCode,
                cancelReason: null,
                ct: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (shouldResend)
        {
            await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: true).ConfigureAwait(false);
        }
    }

    private bool ShouldSendPullAckLocked(InboundTransferContext context, int contiguousChunkCount, bool completed, bool sentRequestImmediately)
    {
        if (context.PullAckDebtChunks <= 0 || contiguousChunkCount <= 0)
        {
            return false;
        }

        if (completed)
        {
            return true;
        }

        if (context.PullAckDebtChunks >= ResolveInboundAckThresholdLocked(context))
        {
            return true;
        }

        if (context.OutstandingChunkRequests.Count == 0)
        {
            return true;
        }

        if (context.PullLastAckSentUtc is null)
        {
            return false;
        }

        if (sentRequestImmediately)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - context.PullLastAckSentUtc.Value >= TimeSpan.FromMilliseconds(ResolveInboundAckCoalesceDelayMsLocked(context));
    }

    private async Task HandleInboundPullChunkAsync(InboundTransferContext context, FileTransferChunkDataFrameV2 chunk)
    {
        byte[] chunkBytes;
        if (chunk.Data.Length == 0 || chunk.Data.Length > FileTransferProtocol.MaxChunkRawBytes)
        {
            throw new InvalidOperationException("Chunk payload exceeded the V2 raw payload budget.");
        }
        chunkBytes = chunk.Data;

        await HandleInboundPullChunksAsync(
            context,
            [(chunk.ChunkIndex, chunkBytes)]).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunkBatchAsync(InboundTransferContext context, FileTransferChunkBatchFrameV2 batch)
    {
        var chunks = new List<(int ChunkIndex, byte[] ChunkBytes)>(batch.DataSegments.Count);
        for (var segmentOffset = 0; segmentOffset < batch.DataSegments.Count; segmentOffset++)
        {
            var chunkBytes = batch.DataSegments[segmentOffset];
            if (chunkBytes.Length == 0 || chunkBytes.Length > FileTransferProtocol.MaxChunkRawBytes)
            {
                throw new InvalidOperationException("Chunk batch payload exceeded the V2 raw payload budget.");
            }

            chunks.Add((batch.StartChunkIndex + segmentOffset, chunkBytes));
        }

        await HandleInboundPullChunksAsync(context, chunks).ConfigureAwait(false);
    }

    private async Task HandleInboundPullChunksAsync(InboundTransferContext context, IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        List<byte[]> contiguousChunkBytes = [];
        int ackDebtChunks = 0;
        long ackDebtBytes = 0;
        bool completed = false;
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.WriteStream is null ||
                context.Hash is null)
            {
                return;
            }

            foreach (var (chunkIndex, chunkBytes) in chunks)
            {
                context.OutstandingChunkRequests.Remove(chunkIndex);
                context.RequestedChunks.Remove(chunkIndex);
                if (chunkIndex < context.NextChunkIndex)
                {
                    continue;
                }

                if (!context.PendingChunks.ContainsKey(chunkIndex))
                {
                    context.PendingChunks[chunkIndex] = chunkBytes;
                    context.PullHighestReceivedChunkIndex = Math.Max(context.PullHighestReceivedChunkIndex, chunkIndex);
                    var now = DateTimeOffset.UtcNow;
                    context.RecentPullChunkSentUtc.Enqueue(now);
                    TrimRecentEvents(context.RecentPullChunkSentUtc, now);
                    context.PullUsefulPayloadBytesRecent += chunkBytes.Length;
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_chunk_received; transfer_id={context.TransferId}; session_id={context.SessionId}; chunk_index={chunkIndex}; chunk_bytes={chunkBytes.Length}");
                }
            }

            while (context.PendingChunks.Remove(context.NextChunkIndex, out var contiguous))
            {
                contiguousChunkBytes.Add(contiguous);
                context.OutstandingChunkRequests.Remove(context.NextChunkIndex);
                context.RequestedChunks.Remove(context.NextChunkIndex);
                context.ChunkAttemptCounts.Remove(context.NextChunkIndex);
                context.NextChunkIndex++;
                context.ChunksTransferred++;
                context.BytesTransferred = Math.Min(context.FileSizeBytes, context.BytesTransferred + contiguous.Length);
            }

            context.PullLateArrivalDistance = Math.Max(0, context.PullHighestReceivedChunkIndex - context.NextChunkIndex);

            if (contiguousChunkBytes.Count > 0)
            {
                context.PullLastProgressUtc = DateTimeOffset.UtcNow;
                context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
                context.PullFirstChunkTimeoutCount = 0;
                context.PullTimeoutOldestChunkIndex = null;
                context.PullTimeoutStreak = 0;
                context.PullCommittedFrontier = context.NextChunkIndex;
                if (context.PullGapFocusActive && contiguousChunkBytes.Count >= 2)
                {
                    context.PullGapFocusActive = false;
                    LogGapFocusChanged(context.TransferId, context.SessionId, active: false, context.NextChunkIndex, context.PullHighestReceivedChunkIndex, context.PullLateArrivalDistance);
                }
                ackDebtChunks = contiguousChunkBytes.Count;
                ackDebtBytes = contiguousChunkBytes.Sum(static bytes => (long)bytes.Length);
                context.PullAckDebtChunks += ackDebtChunks;
                context.PullAckDebtBytes += ackDebtBytes;
            }

            if (context.PullLateArrivalDistance < PullLateArrivalDistanceThreshold)
            {
                ClearPendingInboundReorderStepDownLocked(context);
            }

            if (context.PullLateArrivalDistance >= PullLateArrivalDistanceThreshold)
            {
                var outstandingCount = context.OutstandingChunkRequests.Count;
                var now = DateTimeOffset.UtcNow;
                if (!ShouldDelayHealthyReorderStepDownLocked(context, now) &&
                    TryStepDownInboundPipelineLocked(context, "late_arrival_distance", outstandingCount, out _, out _))
                {
                    LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, context.PullCurrentPipelineDepth, context.PullSessionDegraded || sessionScreenShareDegraded);
                }
            }

            if (context.PullLateArrivalDistance > 0)
            {
                LogPullReorderPressure(
                    context.TransferId,
                    context.SessionId,
                    context.NextChunkIndex,
                    context.PullHighestReceivedChunkIndex,
                    context.PullLateArrivalDistance,
                    context.OutstandingChunkRequests.Count,
                    context.PullCurrentPipelineDepth,
                    context.ChunkSizeBytes);
            }

            completed = context.NextChunkIndex >= context.ChunkCount && context.ChunkCount > 0;
            snapshot = CreateSnapshotLocked();
        }

        foreach (var bytes in contiguousChunkBytes)
        {
            await context.WriteStream!.WriteAsync(bytes, context.LifetimeCts.Token).ConfigureAwait(false);
            context.Hash!.AppendData(bytes);
        }

        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Inbound);
        }

        if (contiguousChunkBytes.Count > 0)
        {
            LogPullBatchCommit(context.TransferId, context.SessionId, contiguousChunkBytes.Count, context.NextChunkIndex, context.BytesTransferred);
        }

        var sentRequestImmediately = false;
        if (!completed)
        {
            sentRequestImmediately = await MaybeSendNextChunkRequestAsync(context, forceResendOldestOutstanding: false).ConfigureAwait(false);
        }

        if (context.DataSession is not null &&
            ShouldSendPullAckLocked(context, ackDebtChunks, completed, sentRequestImmediately))
        {
            await context.DataSession.SendAsync(
                    new FileTransferAckProgressFrameV2
                    {
                        SessionId = context.SessionId,
                        TransferId = context.TransferId,
                        NextExpectedChunkIndex = context.NextChunkIndex,
                        BytesCommitted = context.BytesTransferred,
                    },
                    context.LifetimeCts.Token)
                .ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_progress_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_batch_sent; transfer_id={context.TransferId}; session_id={context.SessionId}; contiguous_chunk_count={context.PullAckDebtChunks}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_debt_flushed; transfer_id={context.TransferId}; session_id={context.SessionId}; ack_debt_chunks={context.PullAckDebtChunks}; ack_debt_bytes={context.PullAckDebtBytes}");
            LogPullBinaryFrameSent(
                context.TransferId,
                context.SessionId,
                new FileTransferAckProgressFrameV2
                {
                    SessionId = context.SessionId,
                    TransferId = context.TransferId,
                    NextExpectedChunkIndex = context.NextChunkIndex,
                    BytesCommitted = context.BytesTransferred,
                },
                payloadBytes: 0);

            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) && !context.IsTerminal)
                {
                    var now = DateTimeOffset.UtcNow;
                    context.PullLastAckSentUtc = now;
                    context.PullLastAckSentChunkIndex = context.NextChunkIndex;
                    context.PullAckDebtChunks = 0;
                    context.PullAckDebtBytes = 0;
                    context.RecentPullAckSentUtc.Enqueue(now);
                    MaybeLogPullControlChatterWindow(context, context.TransferId, context.SessionId, now);
                }
            }
        }
        else if (contiguousChunkBytes.Count > 0 && sentRequestImmediately)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_suppressed_coalesced; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; bytes_received={context.BytesTransferred}");
        }
        else if (contiguousChunkBytes.Count > 0)
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_ack_flush_skipped; transfer_id={context.TransferId}; session_id={context.SessionId}; next_expected_chunk={context.NextChunkIndex}; ack_debt_chunks={context.PullAckDebtChunks}; outstanding_count={context.OutstandingChunkRequests.Count}");
        }

        if (completed)
        {
            await FinalizeInboundTransferAsync(context, context.LifetimeCts.Token).ConfigureAwait(false);
            return;
        }
    }

    private void TryRecoverInboundPullSessionLocked(InboundTransferContext context)
    {
        var maximumDepth = ResolveInboundMaximumPipelineDepthLocked(context);
        if (sessionScreenShareDegraded ||
            context.PullCurrentPipelineDepth >= maximumDepth ||
            context.PullLateArrivalDistance > 1 ||
            context.PullGapFocusActive ||
            context.PullTimeoutStreak > 0 ||
            context.PullHighestReceivedChunkIndex - context.NextChunkIndex >= 3)
        {
            context.PullRecoverySinceUtc = null;
            return;
        }

        context.PullRecoverySinceUtc ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - context.PullRecoverySinceUtc.Value < TimeSpan.FromMilliseconds(PullSessionRecoveryHoldMs))
        {
            return;
        }

        var changed = RefreshInboundPullPipelineDepthLocked(context, allowRecoveryIncrease: true, out var previousDepth, out var updatedPipelineDepth);
        if (changed)
        {
            LogPullPipelineRecoveryStep(context.TransferId, context.SessionId, previousDepth, updatedPipelineDepth, degraded: context.PullSessionDegraded || sessionScreenShareDegraded);
            LogPullProfileStepUp(context.TransferId, context.SessionId, previousDepth, updatedPipelineDepth, context.ChunkSizeBytes, context.PullSessionDegraded || sessionScreenShareDegraded);
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, degraded: context.PullSessionDegraded || sessionScreenShareDegraded);
        }

        if (context.PullSessionDegraded && !sessionScreenShareActive && updatedPipelineDepth >= PullDegradedPipelineDepth)
        {
            context.PullSessionDegraded = false;
            context.PullDegradedSinceUtc = null;
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_session_degraded_exited; transfer_id={context.TransferId}; session_id={context.SessionId}");
            LogPullPipelineChanged(context.TransferId, context.SessionId, FileTransferDirection.Inbound, updatedPipelineDepth, degraded: false);
        }
    }

    private static void TrimOutboundPullSentChunkCache(OutboundTransferContext context, int nextExpectedChunkIndex)
    {
        if (context.PullSentChunkCache.Count == 0)
        {
            return;
        }

        foreach (var obsoleteChunkIndex in context.PullSentChunkCache.Keys.Where(chunkIndex => chunkIndex < nextExpectedChunkIndex).ToArray())
        {
            context.PullSentChunkCache.Remove(obsoleteChunkIndex);
        }
    }

    private static void LogPullDataFrameReceived(string transferId, string sessionId, FileTransferDataFrameV2 frame)
    {
        LogPullBinaryFrameReceived(transferId, sessionId, frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}");
    }

    private static void LogPullDataFrameIgnored(string transferId, string sessionId, FileTransferDataFrameV2 frame, string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; reason={reason}");
    }

    private static string GetFrameChunkIndex(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.ChunkIndex.ToString(),
            FileTransferChunkBatchFrameV2 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };

    private static void LogPullBinaryFrameSent(string transferId, string sessionId, FileTransferDataFrameV2 frame, int payloadBytes)
    {
        var serializedPayloadBytes = FileTransferDataFrameCodec.Serialize(frame).Length;
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; payload_bytes={serializedPayloadBytes}; serialized_payload_bytes={serializedPayloadBytes}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

    private static void LogPullBinaryFrameReceived(string transferId, string sessionId, FileTransferDataFrameV2 frame)
    {
        var rawChunkBytes = GetFrameRawChunkBytes(frame);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type={frame.Type}; chunk_index={GetFrameChunkIndex(frame)}; raw_chunk_bytes={rawChunkBytes}; chunk_count={GetFrameChunkCount(frame)}");
    }

    private static string ClassifyOutboundFailureErrorCode(Exception ex, string fallbackErrorCode)
    {
        return IsTransportIncompatible(ex)
            ? TransportIncompatibleErrorCode
            : IsPayloadBudgetExceeded(ex)
                ? PayloadBudgetExceededErrorCode
            : fallbackErrorCode;
    }

    private static bool IsTransportIncompatible(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not InvalidOperationException invalidOperationException)
            {
                continue;
            }

            if (invalidOperationException.Message.Contains(
                    "bridge_protocol_outdated_bulk_missing",
                    StringComparison.OrdinalIgnoreCase) ||
                invalidOperationException.Message.Contains(
                    "Installed bridge does not support",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPayloadBudgetExceeded(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not InvalidOperationException invalidOperationException)
            {
                continue;
            }

            if (invalidOperationException.Message.Contains(
                    "payload exceeded safe budget",
                    StringComparison.OrdinalIgnoreCase) ||
                invalidOperationException.Message.Contains(
                    "Bridge payload too large",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetFrameRawChunkBytes(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.Data.Length,
            FileTransferChunkBatchFrameV2 batch => batch.DataSegments.Sum(static segment => segment.Length),
            _ => 0,
        };

    private static int GetFrameChunkCount(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 => 1,
            FileTransferChunkBatchFrameV2 batch => batch.DataSegments.Count,
            _ => 0,
        };

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

    private int ResolveSafeOutboundChunkSize(
        OutboundTransferContext context,
        IFileTransferSignalingTransport? currentTransport)
    {
        var requestedChunkSize = ResolvePreferredOutboundChunkSize(context);
        if (currentTransport is IFileTransferChunkBudgetProvider chunkBudgetProvider)
        {
            return chunkBudgetProvider.ResolveSafeOutboundChunkSize(
                new FileTransferChunkBudgetRequest(
                    context.TransferId,
                    context.FileSizeBytes,
                    requestedChunkSize));
        }

        var candidateChunkSize = requestedChunkSize;
        while (true)
        {
            try
            {
                var payload = FileTransferDataFrameCodec.Serialize(
                    new FileTransferChunkDataFrameV2
                    {
                        SessionId = string.IsNullOrWhiteSpace(context.SessionId) ? new string('s', 32) : context.SessionId,
                        TransferId = context.TransferId,
                        ChunkIndex = 0,
                        ChunkCount = 1,
                        Data = new byte[candidateChunkSize],
                    });
                if (payload.Length <= FileTransferProtocol.MaxSerializedChunkPayloadBytes)
                {
                    return candidateChunkSize;
                }
            }
            catch (InvalidOperationException)
            {
            }

            var safeChunkSize = candidateChunkSize - 512;
            if (safeChunkSize <= 0)
            {
                throw new InvalidOperationException("No valid file-transfer chunk size fits within the V2 payload budget.");
            }

            if (safeChunkSize >= candidateChunkSize)
            {
                return candidateChunkSize;
            }

            candidateChunkSize = safeChunkSize;
        }
    }

    private static void Warn(string message)
    {
        LocalOperationalLog.Warn("FileTransferService", $"event=warning; message={message}");
    }

    private static void LogWindowUpdateSent(
        FileTransferWindowUpdateV1 message,
        string phase,
        string triggerReason,
        int highestBufferedChunkIndex,
        int creditFrontier)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_update_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; phase={phase}; next_expected_chunk={message.NextExpectedChunkIndex}; granted_until_exclusive={message.GrantedUntilChunkIndexExclusive}; highest_buffered_chunk={highestBufferedChunkIndex}; credit_frontier={creditFrontier}; bytes_received={message.BytesReceived}; reason={triggerReason}");
    }

    private static void LogWindowUpdateReceived(FileTransferWindowUpdateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_update_received; transfer_id={message.TransferId}; session_id={message.SessionId}; next_expected_chunk={message.NextExpectedChunkIndex}; granted_until_exclusive={message.GrantedUntilChunkIndexExclusive}; bytes_received={message.BytesReceived}");
    }

    private static void LogPressureStateSent(FileTransferPressureStateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=pressure_state_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; revision={message.Revision}; mode={message.Mode}; suggested_send_ahead_chunks={message.SuggestedSendAheadChunks}; receiver_next_expected_chunk={message.ReceiverNextExpectedChunkIndex}; reason={message.Reason}");
    }

    private static void LogPressureStateReceived(FileTransferPressureStateV1 message)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=pressure_state_received; transfer_id={message.TransferId}; session_id={message.SessionId}; revision={message.Revision}; mode={message.Mode}; suggested_send_ahead_chunks={message.SuggestedSendAheadChunks}; receiver_next_expected_chunk={message.ReceiverNextExpectedChunkIndex}; reason={message.Reason}");
    }

    private static void LogMissingRangeSent(FileTransferMissingRangeV1 message, int nextExpectedChunkIndex, int highestBufferedChunkIndex)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=missing_range_sent; transfer_id={message.TransferId}; session_id={message.SessionId}; start_chunk={message.StartChunkIndex}; end_chunk_exclusive={message.EndChunkIndexExclusive}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}");
    }

    private static void LogMissingRangeReceived(FileTransferMissingRangeV1 message, int nextExpectedChunkIndex, int highestBufferedChunkIndex)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=missing_range_received; transfer_id={message.TransferId}; session_id={message.SessionId}; start_chunk={message.StartChunkIndex}; end_chunk_exclusive={message.EndChunkIndexExclusive}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}");
    }

    private static void LogRepairChunkEvent(
        string eventName,
        string transferId,
        string sessionId,
        int chunkIndex,
        int rangeStartChunkIndex,
        int rangeEndChunkExclusive,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int batchSize,
        int pendingBatchCount)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; transfer_id={transferId}; session_id={sessionId}; chunk_index={chunkIndex}; range_start={rangeStartChunkIndex}; range_end_exclusive={rangeEndChunkExclusive}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; batch_size={batchSize}; pending_batch_count={pendingBatchCount}");
    }

    private static void LogRepairModeBatchWait(
        string transferId,
        string sessionId,
        int rangeStartChunkIndex,
        int rangeEndChunkExclusive,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive,
        int pendingBatchCount)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=repair_mode_batch_wait; transfer_id={transferId}; session_id={sessionId}; range_start={rangeStartChunkIndex}; range_end_exclusive={rangeEndChunkExclusive}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}; pending_batch_count={pendingBatchCount}");
    }

    private static void LogRepairOnlyModeEvent(
        string eventName,
        string transferId,
        string sessionId,
        int nextChunkToRead,
        int remoteNextExpectedChunkIndex,
        int remoteGrantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event={eventName}; transfer_id={transferId}; session_id={sessionId}; next_chunk_to_read={nextChunkToRead}; remote_next_expected={remoteNextExpectedChunkIndex}; remote_granted_until={remoteGrantedUntilExclusive}");
    }

    private static void LogWindowExtensionDeferredDueToGap(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_extension_deferred_due_to_gap; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogWindowStartupCompleted(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        long bytesReceived)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=window_startup_completed; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; bytes_received={bytesReceived}");
    }

    private static void LogBulkUnhealthyDetected(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive,
        long bulkDispatchedSinceProgress,
        long obsoleteChunksSinceProgress,
        int obsoleteChunkCountRecent,
        int missingRangeCountRecent,
        int gapProgressAckCountRecent,
        int contiguousProgressChunksRecent)
    {
        var obsoleteChunkArrivalRatio = bulkDispatchedSinceProgress <= 0
            ? 0D
            : (double)obsoleteChunksSinceProgress / bulkDispatchedSinceProgress;
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_bulk_unhealthy_detected; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; bulk_dispatched_since_progress={bulkDispatchedSinceProgress}; obsolete_chunks_since_progress={obsoleteChunksSinceProgress}; obsolete_chunk_count_recent={obsoleteChunkCountRecent}; obsolete_chunk_arrival_ratio={obsoleteChunkArrivalRatio:F3}; missing_range_count_recent={missingRangeCountRecent}; gap_progress_ack_count_recent={gapProgressAckCountRecent}; contiguous_progress_chunks_recent={contiguousProgressChunksRecent}");
    }

    private static void LogBulkHealthyResumed(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_healthy_resumed; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogBulkFallbackEntered(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=filetransfer_bulk_fallback_entered; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}; grant_chunks={BulkFallbackGrantChunks}; low_watermark_chunks={BulkFallbackLowWatermarkChunks}");
    }

    private static void LogBulkFallbackExited(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int grantedUntilExclusive)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_bulk_fallback_exited; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; granted_until_exclusive={grantedUntilExclusive}");
    }

    private static void LogBulkCatchUpOnlyEntered(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int obsoleteChunkCountRecent,
        double obsoleteChunkArrivalRatio,
        int missingRangeCountRecent,
        int contiguousProgressChunksRecent,
        FileTransferPressureReason reason)
    {
        LocalOperationalLog.Warn(
            "FileTransferService",
            $"event=bulk_catchup_only_entered; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; obsolete_chunk_count_recent={obsoleteChunkCountRecent}; obsolete_chunk_arrival_ratio={obsoleteChunkArrivalRatio:F3}; missing_range_count_recent={missingRangeCountRecent}; contiguous_progress_chunks_recent={contiguousProgressChunksRecent}; reason={FormatPressureReason(reason)}");
    }

    private static void LogBulkCatchUpOnlyExited(
        string transferId,
        string sessionId,
        int nextExpectedChunkIndex,
        int highestBufferedChunkIndex,
        int suggestedSendAheadChunks)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=bulk_catchup_only_exited; transfer_id={transferId}; session_id={sessionId}; next_expected_chunk={nextExpectedChunkIndex}; highest_buffered_chunk={highestBufferedChunkIndex}; suggested_send_ahead_chunks={suggestedSendAheadChunks}");
    }

    private static bool TryParsePressureMode(string? value, out FileTransferPressureMode mode)
    {
        mode = default;
        if (string.Equals(value, FileTransferProtocol.PressureModeNormal, StringComparison.OrdinalIgnoreCase))
        {
            mode = FileTransferPressureMode.Normal;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.OrdinalIgnoreCase))
        {
            mode = FileTransferPressureMode.CatchUpOnly;
            return true;
        }

        return false;
    }

    private static string FormatPressureMode(FileTransferPressureMode mode)
        => mode == FileTransferPressureMode.CatchUpOnly
            ? FileTransferProtocol.PressureModeCatchUpOnly
            : FileTransferProtocol.PressureModeNormal;

    private static bool TryParsePressureReason(string? value, out FileTransferPressureReason reason)
    {
        reason = default;
        if (string.Equals(value, FileTransferProtocol.PressureReasonGapRepair, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.GapRepair;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureReasonMediaProtection, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.MediaProtection;
            return true;
        }

        if (string.Equals(value, FileTransferProtocol.PressureReasonBulkBacklog, StringComparison.OrdinalIgnoreCase))
        {
            reason = FileTransferPressureReason.BulkBacklog;
            return true;
        }

        return false;
    }

    private static string FormatPressureReason(FileTransferPressureReason reason)
        => reason switch
        {
            FileTransferPressureReason.GapRepair => FileTransferProtocol.PressureReasonGapRepair,
            FileTransferPressureReason.MediaProtection => FileTransferProtocol.PressureReasonMediaProtection,
            _ => FileTransferProtocol.PressureReasonBulkBacklog,
        };

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

    private static void LogTransportPaused(FileTransferDirection direction, string transferId, string sessionId, string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_paused; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={reason}");

    private static void LogTransportResumed(FileTransferDirection direction, string transferId, string sessionId, string reason, bool requiresResumeRequest)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_transport_resumed; direction={direction.ToString().ToLowerInvariant()}; transfer_id={transferId}; session_id={sessionId}; reason={reason}; requires_resume_request={(requiresResumeRequest ? "yes" : "no")}");

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
        private TaskCompletionSource<bool> controlSignal = CreateSignal();

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

        public long BytesAcceptedForTransport { get; set; }

        public int ChunksAcceptedForTransport { get; set; }

        public FileTransferTransferState State { get; set; } = FileTransferTransferState.Offering;

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Preparing transfer offer.";

        public bool SendStarted { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public int NextChunkIndexToRead { get; set; }

        public int RemoteNextExpectedChunkIndex { get; set; }

        public int RemoteGrantedUntilExclusive { get; set; }

        public DateTimeOffset LastWindowUpdateUtc { get; set; } = DateTimeOffset.UtcNow;

        public Dictionary<int, FileTransferChunkV1> SentChunkCache { get; } = new();

        public Queue<int> PendingRepairChunkIndices { get; } = new();

        public HashSet<int> PendingRepairChunkIndicesSet { get; } = [];

        public bool RepairModeActive { get; set; }

        public int? RepairRangeStartChunkIndex { get; set; }

        public int? RepairRangeEndChunkExclusive { get; set; }

        public int? DeferredRepairRangeStartChunkIndex { get; set; }

        public int? DeferredRepairRangeEndChunkExclusive { get; set; }

        public DateTimeOffset? LastRepairSendUtc { get; set; }

        public DateTimeOffset? LastRepairAckObservedUtc { get; set; }

        public DateTimeOffset? LastRepairEvidenceUtc { get; set; }

        public DateTimeOffset? LastRepairRangeRequestedUtc { get; set; }

        public int? LastRepairChunkSentIndex { get; set; }

        public bool RepairBatchInFlight { get; set; }

        public int? OutstandingRepairBatchStartChunkIndex { get; set; }

        public int? OutstandingRepairBatchEndChunkExclusive { get; set; }

        public int RepairSendCycle { get; set; }

        public bool RepairOnlyModeActive { get; set; }

        public bool RepairSingleChunkModeActive { get; set; }

        public int RemotePressureRevision { get; set; }

        public FileTransferPressureMode RemotePressureMode { get; set; } = FileTransferPressureMode.Normal;

        public FileTransferPressureReason RemotePressureReason { get; set; } = FileTransferPressureReason.BulkBacklog;

        public int RemotePressureSuggestedSendAheadChunks { get; set; } = PressureStateNormalSuggestedSendAheadChunks;

        public int RemotePressureReceiverNextExpectedChunkIndex { get; set; }

        public int CurrentRepairBatchSize { get; set; } = RepairBatchSize;

        public DateTimeOffset? LastSendAheadClampLogUtc { get; set; }

        public IFileTransferDataSession? DataSession { get; set; }

        public bool PullSessionActive { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public SortedSet<int> RequestedButUnsent { get; } = [];

        public SortedSet<int> GrantedOutstandingChunks { get; } = [];

        public Dictionary<int, DateTimeOffset> SentAwaitingAck { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkSentUtc { get; } = new();

        public Dictionary<int, DateTimeOffset> LastChunkResentUtc { get; } = new();

        public Dictionary<int, int> ChunkResendCountSinceAck { get; } = new();

        public Dictionary<int, FileTransferChunkDataFrameV2> PullSentChunkCache { get; } = new();

        public bool PullTransportPaused { get; set; }

        public DateTimeOffset? PullTransportPausedSinceUtc { get; set; }

        public DateTimeOffset? PullTransportGraceDeadlineUtc { get; set; }

        public string? PullTransportPauseReason { get; set; }

        public bool PullTransportResumeRequestPending { get; set; }

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

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
                StatusMessage,
                BytesAcceptedForTransport: BytesAcceptedForTransport,
                BytesAcknowledgedByReceiver: BytesTransferred);

        public OutboundTransferContext(FileTransferSendDescriptor descriptor, FileTransferReadStreamFactory openReadStreamAsync)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            OpenReadStreamAsync = openReadStreamAsync ?? throw new ArgumentNullException(nameof(openReadStreamAsync));
            ChunkSizeBytes = descriptor.ChunkSizeBytes ?? FileTransferProtocol.MaxChunkRawBytes;
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
            try
            {
                DataSession?.Dispose();
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

            DataSession = null;
        }

        public Task ResetAndGetControlSignalTask()
        {
            controlSignal = CreateSignal();
            return controlSignal.Task;
        }

        public void SignalControlActivity()
        {
            controlSignal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record InboundDispatchWork(
        SessionFileTransferService Service,
        string Operation,
        Func<Task> Work);

    private sealed class InboundTransferContext
    {
        public InboundTransferContext(FileTransferOfferV1 offer)
        {
            ArgumentNullException.ThrowIfNull(offer);
            SessionId = offer.SessionId;
            TransferId = offer.TransferId;
            FileName = offer.FileName;
            FileSizeBytes = offer.FileSizeBytes;
            Sha256Base64 = offer.Sha256Base64;
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

        public long BufferedBytes { get; set; }

        public int HighestBufferedChunkIndex { get; set; } = -1;

        public int LastAdvertisedGrantedUntilExclusive { get; set; }

        public int LastAdvertisedNextChunkIndex { get; set; }

        public int LastAdvertisedCreditFrontier { get; set; }

        public DateTimeOffset? LastWindowUpdateSentUtc { get; set; }

        public DateTimeOffset? LastForcedWindowUpdateSentUtc { get; set; }

        public DateTimeOffset? LastContiguousProgressUtc { get; set; }

        public DateTimeOffset? LastBufferedFrontierAdvanceUtc { get; set; }

        public bool StartupPhaseCompleted { get; set; }

        public bool SteadyStateWindowAdvertised { get; set; }

        public bool DegradedRepairModeActive { get; set; }

        public bool BulkFallbackModeActive { get; set; }

        public bool BulkUnhealthyDetected { get; set; }

        public FileTransferPressureMode LocalPressureMode { get; set; } = FileTransferPressureMode.Normal;

        public FileTransferPressureReason LocalPressureReason { get; set; } = FileTransferPressureReason.BulkBacklog;

        public int LocalPressureRevision { get; set; }

        public int LocalPressureSuggestedSendAheadChunks { get; set; } = PressureStateNormalSuggestedSendAheadChunks;

        public int LocalPressureReceiverNextExpectedChunkIndex { get; set; }

        public DateTimeOffset? PressureRecoverySinceUtc { get; set; }

        public int ConsecutiveBulkUnhealthyDetections { get; set; }

        public long BulkDispatchedChunksSinceLastUsefulProgress { get; set; }

        public long ObsoleteChunksArrivedSinceLastUsefulProgress { get; set; }

        public DateTimeOffset? LastBulkDispatchedChunkUtc { get; set; }

        public DateTimeOffset? LastUsefulBulkProgressUtc { get; set; }

        public DateTimeOffset? LastBulkUnhealthyLogUtc { get; set; }

        public int? OldestGapStartChunkIndex { get; set; }

        public DateTimeOffset? OldestGapFirstSeenUtc { get; set; }

        public DateTimeOffset? GapFreeSinceUtc { get; set; }

        public MissingRange? OutstandingMissingRange { get; set; }

        public DateTimeOffset? LastMissingRangeSentUtc { get; set; }

        public int LastMissingRangeHighestBufferedChunkIndex { get; set; } = -1;

        public Queue<DateTimeOffset> RecentMissingRangeSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentGapProgressAckSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentContiguousProgressChunkUtc { get; } = new();

        public Queue<DateTimeOffset> RecentObsoleteChunkArrivalUtc { get; } = new();

        public DateTimeOffset? LastGapExtensionDeferredLogUtc { get; set; }

        public string? ErrorCode { get; set; }

        public string? StatusMessage { get; set; } = "Incoming transfer offer pending.";

        public string? SavedFilePath { get; set; }

        public string? SavedDirectoryPath { get; set; }

        public string? SavedFileName { get; set; }

        public bool AcceptInProgress { get; set; }

        public int NextProgressMilestonePercent { get; set; } = 25;

        public FileTransferReceiveDestination? WriteDestination { get; set; }

        public Stream? WriteStream { get; set; }

        public IncrementalHash? Hash { get; set; }

        public SortedDictionary<int, byte[]> PendingChunks { get; } = new();

        public IFileTransferDataSession? DataSession { get; set; }

        public bool PullSessionActive { get; set; }

        public bool PullManifestReceived { get; set; }

        public bool PullSessionDegraded { get; set; }

        public int PullCurrentPipelineDepth { get; set; }

        public int PullRequestedFrontierExclusive { get; set; }

        public int PullCommittedFrontier { get; set; }

        public DateTimeOffset? PullLastRequestSentUtc { get; set; }

        public DateTimeOffset? PullLastProgressUtc { get; set; }

        public DateTimeOffset? PullDegradedSinceUtc { get; set; }

        public DateTimeOffset? PullRecoverySinceUtc { get; set; }

        public DateTimeOffset? PullReorderPressureSinceUtc { get; set; }

        public int PullReorderPressureFrontierChunkIndex { get; set; }

        public int? PullTimeoutOldestChunkIndex { get; set; }

        public int PullTimeoutStreak { get; set; }

        public int PullHighestReceivedChunkIndex { get; set; } = -1;

        public int PullLateArrivalDistance { get; set; }

        public bool PullGapFocusActive { get; set; }

        public int PullCurrentPipelineStep { get; set; }

        public int PullCurrentChunkSizeStep { get; set; }

        public DateTimeOffset? PullLastProfileAdjustmentUtc { get; set; }

        public DateTimeOffset? PullLastAckSentUtc { get; set; }

        public int PullLastAckSentChunkIndex { get; set; }

        public int PullAckDebtChunks { get; set; }

        public long PullAckDebtBytes { get; set; }

        public bool PullTransportPaused { get; set; }

        public DateTimeOffset? PullTransportPausedSinceUtc { get; set; }

        public DateTimeOffset? PullTransportGraceDeadlineUtc { get; set; }

        public string? PullTransportPauseReason { get; set; }

        public bool PullTransportResumeRequestPending { get; set; }

        public Queue<DateTimeOffset> RecentPullAckSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullRequestSentUtc { get; } = new();

        public Queue<DateTimeOffset> RecentPullChunkSentUtc { get; } = new();

        public int PullDuplicateRequestIgnoredCountRecent { get; set; }

        public int PullResendSuppressedCountRecent { get; set; }

        public long PullUsefulPayloadBytesRecent { get; set; }

        public DateTimeOffset? LastPullControlChatterLogUtc { get; set; }

        public Dictionary<int, DateTimeOffset> OutstandingChunkRequests { get; } = new();

        public HashSet<int> RequestedChunks { get; } = [];

        public Dictionary<int, int> ChunkAttemptCounts { get; } = new();

        public int PullFirstChunkTimeoutCount { get; set; }

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
                DataSession?.Dispose();
            }
            catch
            {
            }

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
            DataSession = null;
        }
    }

    private int ResolvePreferredOutboundChunkSize(OutboundTransferContext context)
    {
        var preferredChunkSize = context.Descriptor.ChunkSizeBytes ?? PullHealthyDefaultChunkSizeBytes;
        if (sessionScreenShareDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, PullDegradedScreenshareDefaultChunkSizeBytes);
        }
        else if (context.PullSessionDegraded)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, PullDegradedDefaultChunkSizeBytes);
        }
        else if (sessionScreenShareActive)
        {
            preferredChunkSize = Math.Min(preferredChunkSize, PullScreenshareDefaultChunkSizeBytes);
        }
        else
        {
            preferredChunkSize = Math.Min(preferredChunkSize, PullHealthyDefaultChunkSizeBytes);
        }

        return Math.Clamp(preferredChunkSize, 1, FileTransferProtocol.MaxChunkRawBytes);
    }

    private enum WindowUpdateTrigger
    {
        Startup,
        StartupResend,
        GapProgressAck,
        LowWatermark,
        BufferedFrontier,
        SteadyStateResend,
    }

    private enum FileTransferPressureMode
    {
        Normal,
        CatchUpOnly,
    }

    private enum FileTransferPressureReason
    {
        GapRepair,
        MediaProtection,
        BulkBacklog,
    }

    private readonly record struct MissingRange(int StartChunkIndex, int EndChunkIndexExclusive);
}
