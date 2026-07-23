using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

public abstract class SessionFileTransferServiceTestBase : CoreSmokeTestsBase
{
    protected static string InvalidStateErrorCode() => "invalid_state";

    protected static string FileSizeMismatchErrorCode() => FileTransferResultCodes.SizeMismatch;

    protected static string HashMismatchErrorCode() => FileTransferResultCodes.IntegrityMismatch;

    protected static void AssertContainsOrderedSubsequence(IReadOnlyList<FileTransferTransferState> actual, params FileTransferTransferState[] expected)
    {
        var actualIndex = 0;
        foreach (var expectedState in expected)
        {
            while (actualIndex < actual.Count && actual[actualIndex] != expectedState)
            {
                actualIndex++;
            }

            Assert.True(actualIndex < actual.Count, $"Expected state '{expectedState}' was not observed. Actual: {string.Join(", ", actual)}");
            actualIndex++;
        }
    }

    protected static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    protected static FileTransferReceiveDestination CreateTempReceiveDestination(string finalPath, Func<CancellationToken, Task>? beforeMoveAsync = null)
    {
        var directoryPath = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("Final path must include a directory.");
        Directory.CreateDirectory(directoryPath);
        var tempPath = finalPath + ".part";
        var preserveTempArtifact = false;
        var stream = new FileStream(tempPath, new FileStreamOptions { Access = FileAccess.ReadWrite, Mode = FileMode.Create, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.RandomAccess, });
        return new FileTransferReceiveDestination(stream, async ct =>
        {
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            if (beforeMoveAsync is not null)
            {
                await beforeMoveAsync(ct).ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch
            {
                preserveTempArtifact = true;
                throw;
            }
        }, finalPath: finalPath, safeFileName: Path.GetFileName(finalPath), dispose: () =>
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                if (!preserveTempArtifact && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }, disposeAsync: async () =>
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!preserveTempArtifact && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        });
    }

    protected static int GetOperationalLogLength()
    {
        return CoreSmokeTestsBase.GetOperationalLogLength();
    }

    protected static bool GetOutboundRepairOnlyMode(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        var repairOnlyProperty = context!.GetType().GetProperty("RepairOnlyModeActive", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(repairOnlyProperty);
        return (bool)(repairOnlyProperty!.GetValue(context) ?? false);
    }

    protected static string ReadOperationalLogTail(int startIndex)
    {
        return CoreSmokeTestsBase.ReadOperationalLogTail(startIndex);
    }

    protected static string GetLoopbackFrameChunkIndex(FileTransferDataFrame frame) => frame switch
    {
        FileTransferChunkBatchFrameV4 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
        _ => "(none)",
    };

    protected static string ReadOperationalLogText()
    {
        if (!File.Exists(LocalOperationalLog.LogFilePath))
        {
            return string.Empty;
        }

        using var stream = new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    protected static string ReadRetainedOperationalLogs()
    {
        var logDirectory = Path.GetDirectoryName(LocalOperationalLog.LogFilePath);
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var path in Directory.EnumerateFiles(logDirectory, "nlink*.log").OrderBy(static file => File.GetLastWriteTimeUtc(file)))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            builder.AppendLine(reader.ReadToEnd());
        }

        return builder.ToString();
    }

    protected class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport, IFileTransferProtocolCapabilities, IFileTransferRouteStatus, IFileTransferTransportProfileProvider, IFileTransferV6TransportEpochObserver, IFileTransferReceiveRecoveryController, IFileTransferBridgeRecoveryExecutionBridge, IFileTransferRegularV4ControlFeedbackPressureObserver, IFileTransferRegularV4FrontierRepairPressureInstallBridge, IFileTransferRegularV4FrontierRepairSettlementObserver, IFileTransferRouteCompletionObserver, IFileTransferRouteHintObserver, ITransportAccelerationStatus, IFileTransferSessionContextProvider, IFileTransferInitialTunaReadinessProvider, IRuntimeUnlockRouteCommitProofProvider, IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge, IRegularV4ToTunaRouteTransitionBridge
    {
        private readonly string sessionId;
        private readonly ConcurrentDictionary<string, LoopbackDataSession> dataSessions = new(StringComparer.Ordinal);
        private readonly object bridgeRecoveryExecutionGate = new();
        private readonly object regularV4ToTunaTransitionGate = new();
        private readonly Dictionary<
            FileTransferBridgeRecoveryExecutionCommand,
            TaskCompletionSource<FileTransferBridgeRecoveryExecutionReceipt>>
            bridgeRecoveryExecutions = [];
        private readonly HashSet<FileTransferBridgeRecoveryExecutionCommand>
            invalidatedBridgeRecoveryExecutions = [];
        private FileTransferBridgeRecoveryExecutionReceipt?
            lastBridgeRecoveryExecutionReceipt;
        private RegularV4ToTunaActivationPauseBindReceipt?
            currentRegularV4ToTunaPauseBinding;
        private RegularV4ToTunaRouteTransitionDispositionReceipt?
            lastRegularV4ToTunaDisposition;
        private RegularV4ToTunaRouteTransitionDispositionReceipt?
            supersededRegularV4ToTunaDisposition;
        private LoopbackFileTransferTransport? peer;
        private int activeDataSessionSends;
        private int maxConcurrentDataSessionSends;
        private int dataSessionSendCount;
        private bool shouldUseFileTransferV6ForAcceleration;
        private bool isFileTunaActiveForRouteSelection;
        public LoopbackFileTransferTransport(string sessionId)
        {
            this.sessionId = sessionId;
        }

        public bool SupportsFileTransferV6Streaming { get; set; } = true;
        public bool IsTransportAccelerationActive { get; set; }
        public bool ShouldUseFileTransferV6ForAcceleration
        {
            get => shouldUseFileTransferV6ForAcceleration;
            set => shouldUseFileTransferV6ForAcceleration = value;
        }

        public bool IsFileTunaActiveForRouteSelection
        {
            get => isFileTunaActiveForRouteSelection;
            set => isFileTunaActiveForRouteSelection = value;
        }

        public bool IsPostTunaFileFallbackActiveForRouteSelection { get; set; }

        public bool IsDiagnosticRegularNknV6RouteEnabled { get; set; }

        public string TransportAccelerationStatusReason { get; set; } = "test_default_regular_nkn";
        public string? CurrentFileTransferSessionId => sessionId;
        public FileTransferTransportProfileKind FileTransferTransportProfileKind { get; set; } = FileTransferTransportProfileKind.Default;
        public int DataSessionSendDelayMs { get; set; }
        public int DataSessionSendFailureAfterCount { get; set; }
        public int ActiveDataSessionSends => Volatile.Read(ref activeDataSessionSends);
        public int MaxConcurrentDataSessionSends => Volatile.Read(ref maxConcurrentDataSessionSends);
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, bool, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideWithLaneAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferOfferV2, CancellationToken, Task<bool>>? OutboundOfferDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferAcceptV1, CancellationToken, Task<bool>>? OutboundAcceptDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferCancelV1, CancellationToken, Task<bool>>? OutboundCancelDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferPauseControlV6, CancellationToken, Task<bool>>? OutboundPauseControlDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferHeartbeatV6, CancellationToken, Task<bool>>? OutboundHeartbeatDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferTransportProbeV6, CancellationToken, Task<bool>>? OutboundTransportProbeDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferCompleteV1, CancellationToken, Task<bool>>? OutboundCompleteDeliveryOverrideAsync { get; set; }
        public FileTransferTransportKind NextDataFrameTransportKind { get; set; } = FileTransferTransportKind.RegularNkn;
        public Func<LoopbackFileTransferTransport, FileTransferSessionOpenV2, CancellationToken, Task<bool>>? OutboundSessionOpenDeliveryOverrideAsync { get; set; }
        public bool ThrowWhenUnavailableDataSessionSend { get; set; }
        public bool AllowUnavailableV4FallbackRecoveryFramesForTests { get; set; }
        public Func<FileTransferCompleteV1, CancellationToken, Task>? BeforeCompleteDeliveredAsync { get; set; }
        public Exception? OfferSendException { get; init; }
        public Exception? ReceiveRecoveryException { get; set; }
        public Action<FileTransferReceiveRecoveryRequest>? ReceiveRecoveryRequestObserver { get; set; }
        public ConcurrentQueue<FileTransferErrorV1> SentErrors { get; } = [];
        public ConcurrentQueue<FileTransferOfferV2> SentOffers { get; } = [];
        public ConcurrentQueue<FileTransferAcceptV1> SentAccepts { get; } = [];
        public ConcurrentQueue<FileTransferDeclineV1> SentDeclines { get; } = [];
        public ConcurrentQueue<FileTransferCancelV1> SentCancels { get; } = [];
        public ConcurrentQueue<FileTransferCompleteV1> SentCompletes { get; } = [];
        public ConcurrentQueue<FileTransferPauseControlV6> SentPauseControls { get; } = [];
        public ConcurrentQueue<FileTransferHeartbeatV6> SentHeartbeats { get; } = [];
        public ConcurrentQueue<FileTransferTransportEpochV6> SentTransportEpochs { get; } = [];
        public ConcurrentQueue<FileTransferTransportProbeV6> SentTransportProbes { get; } = [];
        public ConcurrentQueue<FileTransferRepairProofV6> SentRepairProofs { get; } = [];
        public ConcurrentQueue<FileTransferSessionOpenV2> SentSessionOpens { get; } = [];
        public ConcurrentQueue<FileTransferDataFrame> SentDataFrames { get; } = [];
        public ConcurrentQueue<FileTransferV6TransportEpochSnapshot> ObservedV6TransportEpochs { get; } = [];
        public ConcurrentQueue<bool> V6TransportEpochObservationCompletionStates { get; } = [];
        public ConcurrentQueue<FileTransferReceiveRecoveryRequest> ReceiveRecoveryRequests { get; } = [];
        internal ConcurrentQueue<FileTransferBridgeRecoveryExecutionCommand>
            BridgeRecoveryExecutionCommands { get; } = [];
        internal ConcurrentQueue<FileTransferBridgeRecoveryExecutionCommand>
            InvalidatedBridgeRecoveryExecutionCommands { get; } = [];
        internal Action<FileTransferBridgeRecoveryExecutionCommand>?
            BridgeRecoveryExecutionBeginObserverForTests { get; set; }
        internal ConcurrentQueue<(
            FileTransferBridgeRecoveryExecutionReceipt Receipt,
            bool Accepted,
            string Reason)> BridgeRecoveryExecutionReceiptDecisions { get; } = [];
        public ConcurrentQueue<FileTransferRegularV4ControlFeedbackPressure> RegularV4ControlFeedbackPressures { get; } = [];
        public ConcurrentQueue<FileTransferRegularV4FrontierRepairSettlement> RegularV4FrontierRepairSettlements { get; } = [];
        internal ConcurrentQueue<FileTransferRegularV4FrontierRepairPressureInstallReceipt> RegularV4PressureInstallReceipts { get; } = [];
        internal Func<FileTransferRegularV4ControlFeedbackPressure, FileTransferRegularV4FrontierRepairPressureInstallReceipt>? RegularV4PressureInstallOverride { get; set; }
        internal Func<FileTransferRegularV4FrontierRepairPressureSettlementCommand, FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt>? RegularV4PressureSettlementApplyOverride { get; set; }
        internal Func<RegularV4ToTunaRouteTransitionDispositionCommand, RegularV4ToTunaRouteTransitionDispositionReceipt?>? RegularV4ToTunaRouteTransitionDispositionOverride { get; set; }
        internal ConcurrentQueue<RuntimeUnlockUncommittedTunaFallbackAdoptionCommand> RuntimeUnlockUncommittedTunaFallbackAdoptions { get; } = [];
        internal ConcurrentQueue<RuntimeUnlockUncommittedTunaFallbackAdoptionStage> RuntimeUnlockUncommittedTunaFallbackAdoptionStages { get; } = [];
        internal ConcurrentQueue<RuntimeUnlockUncommittedTunaFallbackAdoptionStage> RuntimeUnlockUncommittedTunaFallbackAdoptionDiscardedStages { get; } = [];
        internal Func<RuntimeUnlockUncommittedTunaFallbackAdoptionStage, RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt>? RuntimeUnlockUncommittedTunaFallbackAdoptionStageOverride { get; set; }
        internal Func<RuntimeUnlockUncommittedTunaFallbackAdoptionCommand, RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt>? RuntimeUnlockUncommittedTunaFallbackAdoptionOverride { get; set; }
        internal Action<RuntimeUnlockUncommittedTunaFallbackAdoptionCommand>? RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationObserverForTests { get; set; }
        private RuntimeUnlockUncommittedTunaFallbackAdoptionStage? pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage;
        private RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt? pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation;
        private long runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration;
        private long runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration = 1;
        private bool runtimeUnlockUncommittedTunaFallbackActivationPaused = true;
        private string runtimeUnlockUncommittedTunaFallbackAvailabilityReason = "tuna_activation_negotiating";
        internal RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt? LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt { get; private set; }
        internal RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt? LastRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt { get; private set; }
        internal RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt? LastRuntimeUnlockUncommittedTunaFallbackAdoptionReceipt { get; private set; }
        public ConcurrentQueue<FileTransferRouteCompletedNotification> RouteCompletionNotifications { get; } = [];
        public ConcurrentQueue<FileTransferRouteHintNotification> ObservedRouteHints { get; } = [];
        public ConcurrentQueue<string> RuntimeUnlockProbeEvents { get; } = [];
        public ConcurrentQueue<string> RuntimeUnlockRouteCommitResults { get; } = [];
        internal RuntimeUnlockRouteCommitSnapshot? RuntimeUnlockRouteCommitSnapshot { get; set; }
        internal FileTransferInitialTunaReadinessEvidence? InitialTunaReadinessEvidence { get; set; }

        internal void SeedInitialTunaReadinessEvidenceForTests(
            string transferId,
            FileTransferDirection direction,
            long leaseGeneration = 1,
            long offerGeneration = 1,
            bool leaseCurrent = true,
            bool listenerReady = true,
            bool peerVisibleProof = true,
            bool answerReceived = true,
            bool answerAckReceived = true,
            bool tunaNegotiatedObserved = true)
        {
            var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "loopback-transfer" : transferId.Trim();
            InitialTunaReadinessEvidence = new FileTransferInitialTunaReadinessEvidence(
                sessionId,
                normalizedTransferId,
                direction,
                DateTimeOffset.UtcNow,
                leaseGeneration,
                $"loopback-listener-{leaseGeneration}",
                leaseCurrent,
                listenerReady,
                offerGeneration,
                peerVisibleProof,
                answerReceived,
                answerAckReceived,
                tunaNegotiatedObserved);
        }

        internal void SeedRuntimeUnlockRouteCommitProofForTests(
            string transferId,
            string reason,
            long transactionGeneration = 1,
            long offerGeneration = 1,
            long leaseGeneration = 1,
            long launchGeneration = 1)
        {
            var normalizedTransferId = string.IsNullOrWhiteSpace(transferId) ? "loopback-transfer" : transferId.Trim();
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "loopback_runtime_unlock_route_commit" : reason.Trim();
            var probeId = $"loopback-precommit:{transactionGeneration}:{offerGeneration}:{leaseGeneration}:{normalizedTransferId}";
            RuntimeUnlockRouteCommitSnapshot = new RuntimeUnlockRouteCommitSnapshot(
                SessionId: sessionId,
                TransferId: normalizedTransferId,
                TransactionGeneration: transactionGeneration,
                OfferGeneration: offerGeneration,
                PeerVisibleProof: true,
                PeerReceived: true,
                AnswerReceived: true,
                TargetRoute: FileTransferRoute.FileTunaV4,
                ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
                TargetTransport: FileTransferTransportKind.Tuna,
                TransactionState: RuntimeUnlockTransactionState.RouteCommitPending.ToString(),
                Reason: normalizedReason,
                PathProbeId: probeId,
                PathProbeState: RuntimeUnlockPathProbeState.Acked.ToString(),
                PathProbeTransport: FileTransferTransportKind.Tuna,
                PathProbeAckedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TunaPathLeaseRequired: true,
                TunaPathLeaseGeneration: leaseGeneration,
                TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
                TunaPathLeaseListenerRunId: $"loopback-listener-{leaseGeneration}",
                TunaPathLeaseCurrent: true,
                LaunchGeneration: launchGeneration);
        }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
        public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
        public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
        public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
        public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
        public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
        public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;
        public event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived;
        public event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived;
        public event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived;
        public event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived;
        public event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived;
        public event EventHandler<FileTransferFallbackTailYieldProofReceivedEventArgs>? FileTransferFallbackTailYieldProofReceived;
        public event EventHandler<TransportAccelerationStateChangedEventArgs>? TransportAccelerationStateChanged;
        private event EventHandler<FileTransferBridgeRecoveryEvaluationRequestedEventArgs>?
            bridgeRecoveryEvaluationRequested;
        event EventHandler<FileTransferBridgeRecoveryEvaluationRequestedEventArgs>?
            IFileTransferBridgeRecoveryExecutionBridge.BridgeRecoveryEvaluationRequested
        {
            add => bridgeRecoveryEvaluationRequested += value;
            remove => bridgeRecoveryEvaluationRequested -= value;
        }
        public void Connect(LoopbackFileTransferTransport other)
        {
            peer = other;
            other.peer = this;
        }

        public void SetTransportAccelerationActiveForTests(bool isActive, string reason)
        {
            IsTransportAccelerationActive = isActive;
            TransportAccelerationStatusReason = reason;
            TransportAccelerationStateChanged?.Invoke(this, new TransportAccelerationStateChangedEventArgs(isActive, reason));
        }

        public void ObserveFileTransferV6TransportEpoch(FileTransferV6TransportEpochSnapshot snapshot)
        {
            V6TransportEpochObservationCompletionStates.Enqueue(snapshot.ObservationCompleted.IsCompleted);
            ObservedV6TransportEpochs.Enqueue(snapshot);
        }

        public void ObserveFileTransferRouteHint(FileTransferRouteHintNotification notification)
            => ObservedRouteHints.Enqueue(notification);

        public void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
        {
            ReceiveRecoveryRequestObserver?.Invoke(request);
            if (ReceiveRecoveryException is { } exception)
            {
                throw exception;
            }

            ReceiveRecoveryRequests.Enqueue(request);
        }

        FileTransferBridgeRecoveryExecutionStartResult
            IFileTransferBridgeRecoveryExecutionBridge.BeginBridgeRecoveryExecution(
                FileTransferBridgeRecoveryExecutionCommand command)
        {
            BridgeRecoveryExecutionBeginObserverForTests?.Invoke(command);
            TaskCompletionSource<FileTransferBridgeRecoveryExecutionReceipt> completion;
            FileTransferBridgeRecoveryExecutionStartStatus status;
            lock (bridgeRecoveryExecutionGate)
            {
                if (lastBridgeRecoveryExecutionReceipt is { } lastReceipt &&
                    lastReceipt.Command == command)
                {
                    return new FileTransferBridgeRecoveryExecutionStartResult(
                        command,
                        FileTransferBridgeRecoveryExecutionStartStatus.Idempotent,
                        "loopback_bridge_recovery_execution_already_completed",
                        Task.FromResult(lastReceipt));
                }

                if (invalidatedBridgeRecoveryExecutions.Contains(command))
                {
                    return new FileTransferBridgeRecoveryExecutionStartResult(
                        command,
                        FileTransferBridgeRecoveryExecutionStartStatus.Rejected,
                        "loopback_bridge_recovery_execution_invalidated",
                        Completion: null);
                }

                if (bridgeRecoveryExecutions.TryGetValue(command, out completion!))
                {
                    status = FileTransferBridgeRecoveryExecutionStartStatus.Idempotent;
                }
                else
                {
                    completion = new TaskCompletionSource<FileTransferBridgeRecoveryExecutionReceipt>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    bridgeRecoveryExecutions.Add(command, completion);
                    status = FileTransferBridgeRecoveryExecutionStartStatus.Started;
                }
            }

            if (status == FileTransferBridgeRecoveryExecutionStartStatus.Started)
            {
                var request = CreateReceiveRecoveryRequest(command);
                ReceiveRecoveryRequestObserver?.Invoke(request);
                if (ReceiveRecoveryException is { } exception)
                {
                    lock (bridgeRecoveryExecutionGate)
                    {
                        bridgeRecoveryExecutions.Remove(command);
                    }

                    throw exception;
                }

                ReceiveRecoveryRequests.Enqueue(request);
                BridgeRecoveryExecutionCommands.Enqueue(command);
            }

            return new FileTransferBridgeRecoveryExecutionStartResult(
                command,
                status,
                status == FileTransferBridgeRecoveryExecutionStartStatus.Started
                    ? "loopback_bridge_recovery_execution_started"
                    : "loopback_bridge_recovery_execution_idempotent",
                completion.Task);
        }

        bool IFileTransferBridgeRecoveryExecutionBridge.InvalidateBridgeRecoveryExecution(
            FileTransferBridgeRecoveryExecutionCommand command,
            string reason)
        {
            TaskCompletionSource<FileTransferBridgeRecoveryExecutionReceipt>? completion;
            FileTransferBridgeRecoveryExecutionReceipt receipt;
            lock (bridgeRecoveryExecutionGate)
            {
                if (lastBridgeRecoveryExecutionReceipt is { } lastReceipt &&
                    lastReceipt.Command == command)
                {
                    return true;
                }

                if (!invalidatedBridgeRecoveryExecutions.Add(command))
                {
                    return false;
                }

                bridgeRecoveryExecutions.Remove(command, out completion);
                receipt = new FileTransferBridgeRecoveryExecutionReceipt(
                    command,
                    FileTransferBridgeRecoveryExecutionOutcome.Discarded,
                    "loopback",
                    string.IsNullOrWhiteSpace(reason)
                        ? "loopback_bridge_recovery_execution_invalidated"
                        : reason.Trim());
                lastBridgeRecoveryExecutionReceipt = receipt;
            }

            InvalidatedBridgeRecoveryExecutionCommands.Enqueue(command);
            completion?.TrySetResult(receipt);
            return true;
        }

        internal FileTransferBridgeRecoveryExecutionReceipt CompleteBridgeRecoveryExecution(
            FileTransferBridgeRecoveryExecutionCommand command,
            FileTransferBridgeRecoveryExecutionOutcome outcome,
            string reason)
        {
            var receipt = new FileTransferBridgeRecoveryExecutionReceipt(
                command,
                outcome,
                "loopback",
                string.IsNullOrWhiteSpace(reason)
                    ? "loopback_bridge_recovery_execution_completed"
                    : reason.Trim());
            TaskCompletionSource<FileTransferBridgeRecoveryExecutionReceipt> completion;
            lock (bridgeRecoveryExecutionGate)
            {
                if (lastBridgeRecoveryExecutionReceipt is { } lastReceipt &&
                    lastReceipt.Command == command)
                {
                    if (lastReceipt != receipt)
                    {
                        throw new InvalidOperationException(
                            "The bridge recovery execution already completed with a different receipt.");
                    }

                    return lastReceipt;
                }

                if (!bridgeRecoveryExecutions.Remove(command, out completion!))
                {
                    throw new InvalidOperationException(
                        "The bridge recovery execution command is not active.");
                }

                lastBridgeRecoveryExecutionReceipt = receipt;
            }

            completion.TrySetResult(receipt);
            return receipt;
        }

        void IFileTransferBridgeRecoveryExecutionBridge.ObserveBridgeRecoveryExecutionReceiptDecision(
            FileTransferBridgeRecoveryExecutionReceipt receipt,
            bool accepted,
            string reason)
        {
            BridgeRecoveryExecutionReceiptDecisions.Enqueue((
                receipt,
                accepted,
                string.IsNullOrWhiteSpace(reason) ? "none" : reason.Trim()));
        }

        private static FileTransferReceiveRecoveryRequest CreateReceiveRecoveryRequest(
            FileTransferBridgeRecoveryExecutionCommand command)
        {
            var decision = command.Decision;
            var owner = decision.RuntimeOwner;
            var bridgeAuthority = new FileTransferBridgeRecoveryAuthority(
                owner.SessionId,
                owner.TransferId,
                owner.Direction,
                owner.RouteToken,
                decision.CurrentProtocolVersion,
                decision.CurrentLiveRouteEpoch,
                decision.CurrentRuntimeGeneration,
                decision.CurrentTerminalGeneration,
                decision.CurrentLegGeneration,
                decision.TransportEpoch,
                decision.BridgeRecoveryGeneration,
                decision.BridgeDecisionGeneration,
                decision.RecoveryKind.ToString(),
                decision.Reason);
            RuntimeUnlockRegularV4RecoveryAuthority? regularV4Authority =
                command.RecoveryLivenessLease is { } recoveryLivenessLease
                ? new RuntimeUnlockRegularV4RecoveryAuthority(
                    bridgeAuthority,
                    recoveryLivenessLease)
                : null;

            return new FileTransferReceiveRecoveryRequest(
                owner.SessionId,
                owner.TransferId,
                owner.Direction,
                decision.Reason)
            {
                RouteToken = owner.RouteToken,
                ProtocolVersion = decision.CurrentProtocolVersion,
                LiveRouteEpoch = decision.CurrentLiveRouteEpoch,
                TransferLegGeneration = decision.CurrentLegGeneration,
                BridgeRecoveryGeneration = decision.BridgeRecoveryGeneration,
                TransportEpoch = decision.TransportEpoch,
                CheckpointRequestId = command.CheckpointRequestId,
                AuthorityReason = decision.Reason,
                BridgeRecoveryAuthority = bridgeAuthority,
                RegularV4RecoveryAuthority = regularV4Authority,
                BridgeRecoveryExecutionCommand = command,
            };
        }

        public void ObserveRegularV4ControlFeedbackPressure(FileTransferRegularV4ControlFeedbackPressure pressure)
            => RegularV4ControlFeedbackPressures.Enqueue(pressure);

        FileTransferRegularV4FrontierRepairPressureInstallReceipt IFileTransferRegularV4FrontierRepairPressureInstallBridge.InstallRegularV4FrontierRepairPressure(
            FileTransferRegularV4FrontierRepairPressureInstallCommand command)
        {
            var pressure = command.Pressure;
            RegularV4ControlFeedbackPressures.Enqueue(pressure);
            var installGeneration = command.InstallGeneration;
            var authority = pressure.RepairAuthority;
            FileTransferRegularV4FrontierRepairPressureInstallReceipt receipt;
            if (RegularV4PressureInstallOverride is { } installOverride)
            {
                receipt = installOverride(pressure);
            }
            else if (authority is null ||
                     authority.PredecessorAuthorityId is { IsDefault: true })
            {
                receipt = new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                    authority ?? new FileTransferRegularV4FrontierRepairAuthority(
                        pressure.SessionId,
                        pressure.TransferId,
                        FileTransferDirection.Outbound,
                        FileTransferRouteResolver.RegularNknV4FastToken,
                        FileTransferProtocol.ProtocolVersionV4,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        null,
                        "(none)",
                        0,
                        -1,
                        Math.Max(0, pressure.PendingRepairCount),
                        0,
                        pressure.Reason),
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected,
                    "regular_v4_frontier_repair_pressure_install_predecessor_missing",
                    installGeneration,
                    pressure.Reason);
            }
            else
            {
                receipt = new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                    authority,
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                    authority.PredecessorAuthorityId is null
                        ? "regular_v4_frontier_repair_initial_pressure_installed"
                        : "regular_v4_frontier_repair_pressure_installed",
                    installGeneration,
                    pressure.Reason);
            }

            RegularV4PressureInstallReceipts.Enqueue(receipt);
            return receipt;
        }

        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt
            IFileTransferRegularV4FrontierRepairPressureInstallBridge.ApplyRegularV4FrontierRepairPressureSettlement(
                FileTransferRegularV4FrontierRepairPressureSettlementCommand command)
        {
            RegularV4FrontierRepairSettlements.Enqueue(command.Settlement);
            if (RegularV4PressureSettlementApplyOverride is { } settlementOverride)
            {
                return settlementOverride(command);
            }

            return new(
                command,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "regular_v4_frontier_repair_pressure_settlement_applied",
                CurrentAuthorityCleared:
                    command.Settlement.Outcome != FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded,
                RetainedPredecessorCleared:
                    command.Settlement.Outcome == FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded);
        }

        public void ObserveRegularV4FrontierRepairSettlement(FileTransferRegularV4FrontierRepairSettlement settlement)
        {
            if (!RegularV4FrontierRepairSettlements.Contains(settlement))
            {
                RegularV4FrontierRepairSettlements.Enqueue(settlement);
            }
        }

        RegularV4ToTunaActivationPauseBindReceipt
            IRegularV4ToTunaRouteTransitionBridge.BindRegularV4ToTunaActivationPause(
                RegularV4ToTunaActivationPauseBindCommand command)
        {
            lock (regularV4ToTunaTransitionGate)
            {
                if (currentRegularV4ToTunaPauseBinding is { } current &&
                    current.Command == command)
                {
                    return current with
                    {
                        Status = RegularV4ToTunaActivationPauseBindStatus.Idempotent,
                        Reason = "regular_v4_to_tuna_activation_pause_bind_idempotent",
                    };
                }

                if (command.Authority.IsDefault ||
                    currentRegularV4ToTunaPauseBinding is not null ||
                    supersededRegularV4ToTunaDisposition is not null)
                {
                    return new(
                        command,
                        RegularV4ToTunaActivationPauseBindStatus.Rejected,
                        "regular_v4_to_tuna_activation_pause_binding_conflict");
                }

                var receipt = new RegularV4ToTunaActivationPauseBindReceipt(
                    command,
                    RegularV4ToTunaActivationPauseBindStatus.Installed,
                    "regular_v4_to_tuna_activation_pause_bound");
                currentRegularV4ToTunaPauseBinding = receipt;
                return receipt;
            }
        }

        RegularV4ToTunaRouteTransitionDispositionReceipt
            IRegularV4ToTunaRouteTransitionBridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                RegularV4ToTunaRouteTransitionDispositionCommand command)
        {
            if (RegularV4ToTunaRouteTransitionDispositionOverride is { } dispositionOverride &&
                dispositionOverride(command) is { } overriddenReceipt)
            {
                return overriddenReceipt;
            }

            var makeAvailable = false;
            RegularV4ToTunaRouteTransitionDispositionReceipt receipt;
            lock (regularV4ToTunaTransitionGate)
            {
                if (lastRegularV4ToTunaDisposition is { } previous &&
                    previous.Command == command)
                {
                    return previous with
                    {
                        Status =
                            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Idempotent,
                        Reason =
                            "regular_v4_to_tuna_route_transition_disposition_idempotent",
                    };
                }

                if (!FileTransferCoordinator
                        .IsRegularV4ToTunaRouteTransitionDispositionCommandValid(command) ||
                    currentRegularV4ToTunaPauseBinding is not { } current ||
                    current.Command.Authority != command.Authority)
                {
                    return new(
                        command,
                        RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
                        "regular_v4_to_tuna_activation_pause_binding_not_current");
                }

                receipt = new(
                    command,
                    RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
                    command.Kind switch
                    {
                        RegularV4ToTunaRouteTransitionDispositionKind.Committed =>
                            "regular_v4_to_tuna_route_transition_committed",
                        RegularV4ToTunaRouteTransitionDispositionKind.RolledBack =>
                            "regular_v4_to_tuna_route_transition_rolled_back",
                        RegularV4ToTunaRouteTransitionDispositionKind.Superseded =>
                            "regular_v4_to_tuna_route_transition_superseded",
                        _ =>
                            "regular_v4_to_tuna_route_transition_terminal_invalidated",
                    });
                currentRegularV4ToTunaPauseBinding = null;
                lastRegularV4ToTunaDisposition = receipt;
                supersededRegularV4ToTunaDisposition =
                    command.Kind == RegularV4ToTunaRouteTransitionDispositionKind.Superseded
                        ? receipt
                        : null;
                makeAvailable = command.Kind is
                    RegularV4ToTunaRouteTransitionDispositionKind.Committed or
                    RegularV4ToTunaRouteTransitionDispositionKind.RolledBack;
            }

            if (makeAvailable)
            {
                var targetTransport =
                    command.Kind == RegularV4ToTunaRouteTransitionDispositionKind.Committed
                        ? FileTransferTransportKind.Tuna
                        : FileTransferTransportKind.RegularNkn;
                SetAllDataSessionsAvailability(
                    isAvailable: true,
                    receipt.Reason,
                    requiresResumeRequest: false,
                    FileTransferTransportHandoffKind.None,
                    targetTransport);
            }

            return receipt;
        }

        RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt
            IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge.StageRuntimeUnlockUncommittedTunaFallbackAdoption(
                RuntimeUnlockUncommittedTunaFallbackAdoptionStage stage)
        {
            RuntimeUnlockUncommittedTunaFallbackAdoptionStages.Enqueue(stage);
            if (RuntimeUnlockUncommittedTunaFallbackAdoptionStageOverride is { } stageOverride)
            {
                var overriddenReceipt = stageOverride(stage);
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt = overriddenReceipt;
                return overriddenReceipt;
            }

            if (pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage == stage)
            {
                var idempotentReceipt = new RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt(
                    stage,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Idempotent,
                    "uncommitted_tuna_fallback_adoption_stage_already_current",
                    runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration,
                    runtimeUnlockUncommittedTunaFallbackActivationPaused,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityReason);
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt = idempotentReceipt;
                return idempotentReceipt;
            }

            if (pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage is not null)
            {
                var rejectedReceipt = new RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt(
                    stage,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Rejected,
                    "uncommitted_tuna_fallback_adoption_stage_replacement_pending",
                    runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration,
                    runtimeUnlockUncommittedTunaFallbackActivationPaused,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityReason);
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt = rejectedReceipt;
                return rejectedReceipt;
            }

            if (LastRuntimeUnlockUncommittedTunaFallbackAdoptionReceipt is { } committed &&
                stage.HandoffGeneration <= committed.Command.HandoffGeneration)
            {
                var rejectedReceipt = new RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt(
                    stage,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Rejected,
                    "uncommitted_tuna_fallback_adoption_stage_stale",
                    runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration,
                    runtimeUnlockUncommittedTunaFallbackActivationPaused,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityReason);
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt = rejectedReceipt;
                return rejectedReceipt;
            }

            pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage = stage;
            pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation = null;
            runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration++;
            var receipt = new RuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt(
                stage,
                RuntimeUnlockUncommittedTunaFallbackAdoptionStageStatus.Staged,
                "uncommitted_tuna_fallback_adoption_staged",
                runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration,
                runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration,
                runtimeUnlockUncommittedTunaFallbackActivationPaused,
                runtimeUnlockUncommittedTunaFallbackAvailabilityReason);
            LastRuntimeUnlockUncommittedTunaFallbackAdoptionStageReceipt = receipt;
            return receipt;
        }

        RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt
            IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge.PrepareRuntimeUnlockUncommittedTunaFallbackAdoption(
                RuntimeUnlockUncommittedTunaFallbackAdoptionCommand command)
        {
            if (LastRuntimeUnlockUncommittedTunaFallbackAdoptionReceipt is { } committed &&
                committed.Command == command)
            {
                var idempotent = new RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt(
                    command,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Idempotent,
                    "uncommitted_tuna_fallback_adoption_already_applied",
                    committed.PressureSettlementReceipt);
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt = idempotent;
                return idempotent;
            }

            if (pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage is not { } pending ||
                command.StageGeneration <= 0 ||
                command.StageGeneration != runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration ||
                !ReferenceEquals(command.DataSession, pending.DataSession) ||
                command.DataSessionGeneration != pending.DataSessionGeneration ||
                command.HandoffGeneration != pending.HandoffGeneration ||
                command.DrainGeneration != pending.DrainGeneration ||
                command.PeerProofEvidence != pending.PeerProofEvidence ||
                command.PressureSettlementCommand != pending.PressureSettlementCommand ||
                command.RuntimeOwner != pending.TargetRuntimeOwner ||
                command.RoutePublication != pending.TargetRoutePublication ||
                command.TransportEpoch != pending.TargetTransportEpoch ||
                command.BridgeRecoveryGeneration != pending.TargetBridgeRecoveryGeneration ||
                command.SourceAvailabilityGeneration != runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration ||
                command.SourceActivationPaused != runtimeUnlockUncommittedTunaFallbackActivationPaused ||
                !string.Equals(
                    command.SourceAvailabilityReason,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityReason,
                    StringComparison.Ordinal))
            {
                var rejected = new RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt(
                    command,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Rejected,
                    "uncommitted_tuna_fallback_adoption_stage_mismatch");
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt = rejected;
                return rejected;
            }

            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt? pressureReceipt = null;
            if (command.PressureSettlementCommand is { } pressureCommand)
            {
                pressureReceipt = RegularV4PressureSettlementApplyOverride?.Invoke(pressureCommand) ??
                    new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                        pressureCommand,
                        FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                        "regular_v4_frontier_repair_pressure_settlement_applied",
                        CurrentAuthorityCleared: true,
                        RetainedPredecessorCleared: false);
            }

            var receipt = new RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt(
                command,
                pressureReceipt is null || pressureReceipt.Status is (
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent)
                    ? RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Prepared
                    : RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Rejected,
                pressureReceipt is null || pressureReceipt.Status is (
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent)
                    ? "uncommitted_tuna_fallback_adoption_prepared"
                    : $"uncommitted_tuna_fallback_pressure_{pressureReceipt.Reason}",
                pressureReceipt);
            pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation = receipt;
            LastRuntimeUnlockUncommittedTunaFallbackAdoptionPreparationReceipt = receipt;
            RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationObserverForTests?.Invoke(command);
            return receipt;
        }

        RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt
            IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge.CommitRuntimeUnlockUncommittedTunaFallbackAdoption(
                RuntimeUnlockUncommittedTunaFallbackAdoptionCommand command)
        {
            RuntimeUnlockUncommittedTunaFallbackAdoptions.Enqueue(command);
            if (LastRuntimeUnlockUncommittedTunaFallbackAdoptionReceipt is { } committed &&
                committed.Command == command)
            {
                return committed with
                {
                    Status = RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Idempotent,
                    Reason = "uncommitted_tuna_fallback_adoption_already_applied",
                };
            }

            if (pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage is not { } pending ||
                pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation is not { } preparation ||
                preparation.Command != command ||
                preparation.Status != RuntimeUnlockUncommittedTunaFallbackAdoptionPreparationStatus.Prepared ||
                command.StageGeneration <= 0 ||
                command.StageGeneration !=
                    runtimeUnlockUncommittedTunaFallbackAdoptionStageGeneration ||
                !ReferenceEquals(command.DataSession, pending.DataSession) ||
                command.DataSessionGeneration != pending.DataSessionGeneration ||
                command.HandoffGeneration != pending.HandoffGeneration ||
                command.DrainGeneration != pending.DrainGeneration ||
                command.PeerProofEvidence != pending.PeerProofEvidence ||
                command.PressureSettlementCommand != pending.PressureSettlementCommand ||
                command.RuntimeOwner != pending.TargetRuntimeOwner ||
                command.RoutePublication != pending.TargetRoutePublication ||
                command.TransportEpoch != pending.TargetTransportEpoch ||
                command.BridgeRecoveryGeneration != pending.TargetBridgeRecoveryGeneration ||
                command.SourceAvailabilityGeneration !=
                    runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration ||
                command.SourceActivationPaused !=
                    runtimeUnlockUncommittedTunaFallbackActivationPaused ||
                !string.Equals(
                    command.SourceAvailabilityReason,
                    runtimeUnlockUncommittedTunaFallbackAvailabilityReason,
                    StringComparison.Ordinal))
            {
                return new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                    command,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected,
                    "uncommitted_tuna_fallback_adoption_stage_mismatch");
            }

            var receipt = RuntimeUnlockUncommittedTunaFallbackAdoptionOverride?.Invoke(command) ??
                          new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                              command,
                              RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Applied,
                              "coordinator_post_tuna_fallback_v6_committed");
            if (receipt.Command != command)
            {
                return new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                    command,
                    RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected,
                    "uncommitted_tuna_fallback_adoption_receipt_command_mismatch");
            }

            if (receipt.Status is RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Applied or
                RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Idempotent)
            {
                var pressureReceipt = receipt.PressureSettlementReceipt;
                if (command.PressureSettlementCommand is { } pressureCommand &&
                    pressureReceipt is null)
                {
                    pressureReceipt = ((IFileTransferRegularV4FrontierRepairPressureInstallBridge)this)
                        .ApplyRegularV4FrontierRepairPressureSettlement(pressureCommand);
                }

                if (command.PressureSettlementCommand is { } expectedPressureCommand &&
                    (pressureReceipt is null ||
                     pressureReceipt.Command != expectedPressureCommand ||
                     pressureReceipt.Status is not (
                         FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                         FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent)))
                {
                    return new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                        command,
                        RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected,
                        "uncommitted_tuna_fallback_pressure_apply_receipt_mismatch",
                        pressureReceipt);
                }

                if (command.PressureSettlementCommand is null && pressureReceipt is not null)
                {
                    return new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                        command,
                        RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected,
                        "uncommitted_tuna_fallback_pressure_apply_receipt_unexpected",
                        pressureReceipt);
                }

                receipt = receipt with { PressureSettlementReceipt = pressureReceipt };
                if (receipt.PressureSettlementReceipt != preparation.PressureSettlementReceipt)
                {
                    return new RuntimeUnlockUncommittedTunaFallbackAdoptionReceipt(
                        command,
                        RuntimeUnlockUncommittedTunaFallbackAdoptionStatus.Rejected,
                        "uncommitted_tuna_fallback_adoption_preparation_mismatch");
                }
                pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage = null;
                pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation = null;
                runtimeUnlockUncommittedTunaFallbackActivationPaused = false;
                runtimeUnlockUncommittedTunaFallbackAvailabilityReason =
                    "coordinator_post_tuna_fallback_v6_committed";
                runtimeUnlockUncommittedTunaFallbackAvailabilityGeneration++;
                LastRuntimeUnlockUncommittedTunaFallbackAdoptionReceipt = receipt;
                lock (regularV4ToTunaTransitionGate)
                {
                    supersededRegularV4ToTunaDisposition = null;
                }
            }

            return receipt;
        }

        bool IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge
            .DiscardRuntimeUnlockUncommittedTunaFallbackAdoption(
                RuntimeUnlockUncommittedTunaFallbackAdoptionStage stage,
                string reason)
        {
            if (pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage != stage)
            {
                return false;
            }

            pendingRuntimeUnlockUncommittedTunaFallbackAdoptionStage = null;
            pendingRuntimeUnlockUncommittedTunaFallbackAdoptionPreparation = null;
            RuntimeUnlockUncommittedTunaFallbackAdoptionDiscardedStages.Enqueue(stage);
            return true;
        }

        public void ObserveFileTransferRouteCompleted(FileTransferRouteCompletedNotification notification)
        {
            RouteCompletionNotifications.Enqueue(notification);
            if (string.Equals(notification.RouteToken, "post_tuna_fallback_v6", StringComparison.Ordinal) &&
                notification.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6)
            {
                IsPostTunaFileFallbackActiveForRouteSelection = false;
            }
        }

        void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockPathProbeStarted(
            string sessionId,
            string transferId,
            long transportEpoch,
            string probeId,
            FileTransferTransportKind targetTransport,
            string reason)
        {
            RuntimeUnlockProbeEvents.Enqueue($"started:{probeId}:{reason}");
            if (RuntimeUnlockRouteCommitSnapshot is { } snapshot)
            {
                RuntimeUnlockRouteCommitSnapshot = snapshot with
                {
                    PathProbeId = probeId,
                    PathProbeState = RuntimeUnlockPathProbeState.Started.ToString(),
                    PathProbeTransport = targetTransport,
                    PathProbeAckedUtcMs = 0,
                    PathProbeFailureReason = null,
                };
            }
        }

        void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockPathProbeResult(
            string sessionId,
            string transferId,
            long transportEpoch,
            string probeId,
            FileTransferTransportKind targetTransport,
            bool acked,
            string reason)
        {
            RuntimeUnlockProbeEvents.Enqueue($"{(acked ? "acked" : "failed")}:{probeId}:{reason}");
            if (RuntimeUnlockRouteCommitSnapshot is { } snapshot)
            {
                RuntimeUnlockRouteCommitSnapshot = snapshot with
                {
                    PathProbeId = probeId,
                    PathProbeState = acked
                        ? RuntimeUnlockPathProbeState.Acked.ToString()
                        : RuntimeUnlockPathProbeState.Failed.ToString(),
                    PathProbeTransport = targetTransport,
                    PathProbeAckedUtcMs = acked ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0,
                    PathProbeFailureReason = acked ? null : reason,
                    TransactionState = acked
                        ? RuntimeUnlockTransactionState.RouteCommitPending.ToString()
                        : RuntimeUnlockTransactionState.Failed.ToString(),
                };
            }
        }

        bool IRuntimeUnlockRouteCommitProofProvider.TryGetRuntimeUnlockRouteCommitProof(
            string sessionId,
            string transferId,
            out RuntimeUnlockRouteCommitSnapshot snapshot)
        {
            snapshot = RuntimeUnlockRouteCommitSnapshot!;
            return RuntimeUnlockRouteCommitSnapshot is not null;
        }

        bool IFileTransferInitialTunaReadinessProvider.TryGetInitialTunaReadinessEvidence(
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            out FileTransferInitialTunaReadinessEvidence? evidence)
        {
            evidence = InitialTunaReadinessEvidence;
            return evidence is not null;
        }

        void IRuntimeUnlockRouteCommitProofProvider.NotifyRuntimeUnlockRouteCommitResult(
            string sessionId,
            string transferId,
            FileTransferDirection direction,
            long transactionGeneration,
            long offerGeneration,
            bool accepted,
            string reason)
        {
            RuntimeUnlockRouteCommitResults.Enqueue($"{(accepted ? "accepted" : "rejected")}:{transactionGeneration}:{offerGeneration}:{reason}");
            if (RuntimeUnlockRouteCommitSnapshot is { } snapshot && accepted)
            {
                RuntimeUnlockRouteCommitSnapshot = snapshot with
                {
                    TransactionState = RuntimeUnlockTransactionState.Committed.ToString(),
                };
            }
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
        {
            if (OfferSendException is not null)
            {
                return Task.FromException(OfferSendException);
            }

            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentOffers.Enqueue(payload);
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundOfferDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, delivered) => target.FileTransferOfferReceived?.Invoke(target, new FileTransferOfferReceivedEventArgs(delivered, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentAccepts.Enqueue(payload);
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundAcceptDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, delivered) => target.FileTransferAcceptReceived?.Invoke(target, new FileTransferAcceptReceivedEventArgs(delivered, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentDeclines.Enqueue(payload);
            return DeliverAsync(payload, (target, delivered) => target.FileTransferDeclineReceived?.Invoke(target, new FileTransferDeclineReceivedEventArgs(delivered, "loopback-peer")), ct);
        }
        public Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentSessionOpens.Enqueue(payload);
            return DeliverMaybeAsync(payload, static (transport, delivered, token) => transport.OutboundSessionOpenDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false), (target, delivered) => target.FileTransferSessionOpenReceived?.Invoke(target, new FileTransferSessionOpenReceivedEventArgs(delivered, "loopback-peer")), ct);
        }
        public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
        {
            var payload = TrackCancel(message with { SessionId = NormalizeSessionId(message.SessionId) });
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundCancelDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, delivered) => target.FileTransferCancelReceived?.Invoke(target, new FileTransferCancelReceivedEventArgs(delivered, "loopback-peer")),
                ct);
        }
        public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct) => DeliverAsync(TrackError(message with { SessionId = NormalizeSessionId(message.SessionId) }), (target, payload) => target.FileTransferErrorReceived?.Invoke(target, new FileTransferErrorReceivedEventArgs(payload, "loopback-peer")), ct);
        public async Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
        {
            var payload = TrackComplete(message with { SessionId = NormalizeSessionId(message.SessionId) });
            if (BeforeCompleteDeliveredAsync is not null)
            {
                await BeforeCompleteDeliveredAsync(payload, ct);
            }

            await DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundCompleteDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, deliveredPayload) => target.FileTransferCompleteReceived?.Invoke(target, new FileTransferCompleteReceivedEventArgs(deliveredPayload, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentPauseControls.Enqueue(payload);
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundPauseControlDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, deliveredPayload) => target.FileTransferPauseControlReceived?.Invoke(target, new FileTransferPauseControlReceivedEventArgs(deliveredPayload, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentHeartbeats.Enqueue(payload);
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundHeartbeatDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, deliveredPayload) => target.FileTransferHeartbeatReceived?.Invoke(target, new FileTransferHeartbeatReceivedEventArgs(deliveredPayload, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentTransportEpochs.Enqueue(payload);
            return DeliverAsync(payload, (target, deliveredPayload) => target.FileTransferTransportEpochReceived?.Invoke(target, new FileTransferTransportEpochReceivedEventArgs(deliveredPayload, "loopback-peer")), ct);
        }

        public Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentTransportProbes.Enqueue(payload);
            return DeliverMaybeAsync(
                payload,
                static (transport, delivered, token) => transport.OutboundTransportProbeDeliveryOverrideAsync?.Invoke(transport.peer!, delivered, token) ?? Task.FromResult(false),
                (target, deliveredPayload) => target.FileTransferTransportProbeReceived?.Invoke(target, new FileTransferTransportProbeReceivedEventArgs(deliveredPayload, "loopback-peer")),
                ct);
        }

        public Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentRepairProofs.Enqueue(payload);
            return DeliverAsync(payload, (target, deliveredPayload) => target.FileTransferRepairProofReceived?.Invoke(target, new FileTransferRepairProofReceivedEventArgs(deliveredPayload, "loopback-peer")), ct);
        }

        public Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var session = GetOrCreateDataSession(NormalizeSessionId(sessionId), transferId.Trim());
            return Task.FromResult<IFileTransferDataSession>(session);
        }

        public void Dispose()
        {
            foreach (var session in dataSessions.Values)
            {
                session.Dispose();
            }
        }

        public void RaiseDisconnected()
        {
            SetAllDataSessionsAvailability(isAvailable: false, "transport_disconnected", requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: false, "transport_disconnected", requiresResumeRequest: true);
            Disconnected?.Invoke(this, EventArgs.Empty);
            peer?.Disconnected?.Invoke(peer, EventArgs.Empty);
        }

        public void RaiseReconnected()
        {
            SetAllDataSessionsAvailability(isAvailable: true, "transport_recovered", requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: true, "transport_recovered", requiresResumeRequest: true);
        }

        public void SetLocalDataSessionsUnavailableForTests(string reason)
        {
            SetAllDataSessionsAvailability(isAvailable: false, reason, requiresResumeRequest: true);
        }

        public void SetLocalDataSessionsAvailableForTests(string reason)
        {
            SetAllDataSessionsAvailability(isAvailable: true, reason, requiresResumeRequest: true);
        }

        public void SetConnectedDataSessionsUnavailableForTests(string reason)
        {
            SetAllDataSessionsAvailability(isAvailable: false, reason, requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: false, reason, requiresResumeRequest: true);
        }

        public void SetConnectedDataSessionsUnavailableForTests(
            string reason,
            FileTransferTransportHandoffKind handoffKind,
            FileTransferTransportKind targetTransport)
        {
            SetAllDataSessionsAvailability(isAvailable: false, reason, requiresResumeRequest: true, handoffKind, targetTransport);
            peer?.SetAllDataSessionsAvailability(isAvailable: false, reason, requiresResumeRequest: true, handoffKind, targetTransport);
        }

        public void SetConnectedDataSessionsAvailableForTests(string reason)
        {
            SetAllDataSessionsAvailability(isAvailable: true, reason, requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: true, reason, requiresResumeRequest: true);
        }

        public bool TryFailActiveDataSessionReceiveForTests(string transferId, Exception exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
            ArgumentNullException.ThrowIfNull(exception);
            return dataSessions.TryGetValue(transferId.Trim(), out var session) &&
                   session.TryFailActiveReceive(exception);
        }

        public (bool Active, long Ordinal) GetDataSessionReceiveStateForTests(string transferId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
            return dataSessions.TryGetValue(transferId.Trim(), out var session)
                ? (session.HasActiveReceive, session.ReceiveOrdinal)
                : (false, 0L);
        }

        public void ReceiveDeliveredSessionOpen(FileTransferSessionOpenV2 payload)
        {
            FileTransferSessionOpenReceived?.Invoke(this, new FileTransferSessionOpenReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredDataFrame(FileTransferDataFrame payload)
        {
            if (TryGetOrCreateDataSession(NormalizeSessionId(payload.SessionId), payload.TransferId, out var session))
            {
                session.Deliver(payload);
            }
            else
            {
                LocalOperationalLog.Warn("SessionSecurity", $"event=filetransfer_data_frame_ignored; transport=loopback; transfer_id={payload.TransferId}; session_id={payload.SessionId}; frame_type={payload.Type}; chunk_index={GetLoopbackFrameChunkIndex(payload)}; reason=session_id_mismatch_existing_queue");
            }
        }

        private Task DeliverAsync<TPayload>(TPayload payload, Action<LoopbackFileTransferTransport, TPayload> deliver, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            deliver(target, payload);
            return Task.CompletedTask;
        }

        private async Task DeliverMaybeAsync<TPayload>(TPayload payload, Func<LoopbackFileTransferTransport, TPayload, CancellationToken, Task<bool>> tryOverride, Action<LoopbackFileTransferTransport, TPayload> deliver, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!await tryOverride(this, payload, ct).ConfigureAwait(false))
            {
                var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
                deliver(target, payload);
            }
        }

        private string NormalizeSessionId(string? sessionId) => string.IsNullOrWhiteSpace(sessionId) ? this.sessionId : sessionId.Trim();
        private FileTransferErrorV1 TrackError(FileTransferErrorV1 message)
        {
            SentErrors.Enqueue(message);
            return message;
        }

        private FileTransferCompleteV1 TrackComplete(FileTransferCompleteV1 message)
        {
            SentCompletes.Enqueue(message);
            return message;
        }

        private FileTransferCancelV1 TrackCancel(FileTransferCancelV1 message)
        {
            SentCancels.Enqueue(message);
            return message;
        }

        private LoopbackDataSession GetOrCreateDataSession(string normalizedSessionId, string normalizedTransferId) => TryGetOrCreateDataSession(normalizedSessionId, normalizedTransferId, out var session) ? session : throw new InvalidOperationException("File-transfer data session id mismatch for existing transfer.");
        private void SetAllDataSessionsAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
        {
            foreach (var session in dataSessions.Values)
            {
                session.SetAvailability(isAvailable, reason, requiresResumeRequest);
            }
        }

        private void SetAllDataSessionsAvailability(
            bool isAvailable,
            string reason,
            bool requiresResumeRequest,
            FileTransferTransportHandoffKind handoffKind,
            FileTransferTransportKind targetTransport)
        {
            foreach (var session in dataSessions.Values)
            {
                session.SetAvailability(isAvailable, reason, requiresResumeRequest, handoffKind, targetTransport);
            }
        }

        public void RequestAllDataSessionHandoffs(
            string reason,
            FileTransferTransportHandoffKind handoffKind,
            FileTransferTransportKind targetTransport,
            bool seedRuntimeUnlockProof = true)
        {
            NextDataFrameTransportKind = targetTransport == FileTransferTransportKind.Unknown
                ? FileTransferTransportKind.RegularNkn
                : targetTransport;
            foreach (var session in dataSessions.Values)
            {
                if (seedRuntimeUnlockProof &&
                    handoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    targetTransport == FileTransferTransportKind.Tuna)
                {
                    SeedRuntimeUnlockRouteCommitProofForTests(session.TransferId, reason);
                }

                session.RequestHandoff(reason, handoffKind, targetTransport);
            }
        }

        internal void RequestAllDataSessionHandoffsWithSnapshot(
            string reason,
            FileTransferTransportHandoffKind handoffKind,
            FileTransferTransportKind targetTransport,
            RuntimeUnlockRouteCommitSnapshot snapshot)
        {
            NextDataFrameTransportKind = targetTransport == FileTransferTransportKind.Unknown
                ? FileTransferTransportKind.RegularNkn
                : targetTransport;
            foreach (var session in dataSessions.Values)
            {
                session.RequestHandoff(
                    reason,
                    handoffKind,
                    targetTransport,
                    snapshot);
            }
        }

        private bool TryGetOrCreateDataSession(string normalizedSessionId, string normalizedTransferId, out LoopbackDataSession session)
        {
            session = dataSessions.GetOrAdd(normalizedTransferId, _ => new LoopbackDataSession(this, normalizedSessionId, normalizedTransferId));
            return string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }

        private async Task DeliverDataFrameToPeerAsync(FileTransferDataFrame frame, bool useBulkLane, CancellationToken ct)
        {
            var currentInFlight = Interlocked.Increment(ref activeDataSessionSends);
            while (true)
            {
                var observedMax = Volatile.Read(ref maxConcurrentDataSessionSends);
                if (currentInFlight <= observedMax ||
                    Interlocked.CompareExchange(ref maxConcurrentDataSessionSends, currentInFlight, observedMax) == observedMax)
                {
                    break;
                }
            }

            try
            {
                var sendCount = Interlocked.Increment(ref dataSessionSendCount);
                if (DataSessionSendFailureAfterCount > 0 && sendCount >= DataSessionSendFailureAfterCount)
                {
                    throw new InvalidOperationException("Injected loopback data-session send failure.");
                }

                if (DataSessionSendDelayMs > 0)
                {
                    await Task.Delay(DataSessionSendDelayMs, ct).ConfigureAwait(false);
                }

            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            SentDataFrames.Enqueue(frame);
            var isTransportProbe = frame is FileTransferTransportProbeFrameV6;
            var availabilityBypass =
                isTransportProbe ||
                frame is FileTransferRuntimeUnlockPreCommitProbeFrameBase ||
                IsV6RecoveryFeedbackFrame(frame) ||
                (AllowUnavailableV4FallbackRecoveryFramesForTests && IsV4FallbackRecoveryFrame(frame));
            if (!TryGetOrCreateDataSession(NormalizeSessionId(frame.SessionId), frame.TransferId, out var localSession) ||
                (!localSession.IsAvailable && !availabilityBypass))
            {
                return;
            }

            if (OutboundDataFrameDeliveryOverrideWithLaneAsync is not null && await OutboundDataFrameDeliveryOverrideWithLaneAsync(target, frame, useBulkLane, ct).ConfigureAwait(false))
            {
                return;
            }

            if (OutboundDataFrameDeliveryOverrideAsync is not null && await OutboundDataFrameDeliveryOverrideAsync(target, frame, ct).ConfigureAwait(false))
            {
                return;
            }

            if (target.TryGetOrCreateDataSession(target.NormalizeSessionId(frame.SessionId), frame.TransferId, out var session))
            {
                if (!session.IsAvailable && !availabilityBypass)
                {
                    return;
                }

                var transportKind = target.NextDataFrameTransportKind;
                target.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
                session.Deliver(frame, transportKind);
                if (frame is FileTransferFallbackTailYieldFrameV6 yieldFrame)
                {
                    target.FileTransferFallbackTailYieldProofReceived?.Invoke(
                        target,
                        new FileTransferFallbackTailYieldProofReceivedEventArgs(yieldFrame, "loopback-peer"));
                }
            }
            else
            {
                LocalOperationalLog.Warn("SessionSecurity", $"event=filetransfer_data_frame_ignored; transport=loopback; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetLoopbackFrameChunkIndex(frame)}; reason=session_id_mismatch_existing_queue");
            }
            }
            finally
            {
                Interlocked.Decrement(ref activeDataSessionSends);
            }
        }

        private static bool IsV6RecoveryFeedbackFrame(FileTransferDataFrame frame)
            => frame is FileTransferReceiverStateFrameV6
                or FileTransferFrontierRequestFrameV6
                or FileTransferRepairProofFrameV6
                or FileTransferTransportEpochFrameV6
                or FileTransferFallbackTailYieldFrameV6;

        private static bool IsV4FallbackRecoveryFrame(FileTransferDataFrame frame)
            => frame is FileTransferStateFrameV4 or FileTransferChunkBatchFrameV4;

        protected sealed class LoopbackDataSession :
            IFileTransferDataSession,
            IFileTransferDataPathAuthoritySession,
            IFileTransferRuntimeUnlockTunaProbeSender
        {
            private readonly LoopbackFileTransferTransport owner;
            private readonly Channel<FileTransferReceivedDataFrame> frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, });
            private int disposed;
            private int activeReader;
            private long receiveOrdinal;
            private int available = 1;
            private string availabilityReason = "available";
            private readonly object receiveFailureGate = new();
            private TaskCompletionSource<Exception>? activeReceiveFailure;
            public LoopbackDataSession(LoopbackFileTransferTransport owner, string sessionId, string transferId)
            {
                this.owner = owner;
                SessionId = sessionId;
                TransferId = transferId;
            }

            public string SessionId { get; }
            public string TransferId { get; }
            public bool IsAvailable => Volatile.Read(ref available) != 0;
            public bool HasActiveReceive => Volatile.Read(ref activeReader) != 0;
            public long ReceiveOrdinal => Volatile.Read(ref receiveOrdinal);
            public ConcurrentQueue<FileTransferDataPathSendAuthority> SentDataPathSendAuthorities { get; } = [];

            public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;
            public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
                => (await ReceiveWithMetadataAsync(ct).ConfigureAwait(false)).Frame;

            public async ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref receiveOrdinal);
                if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
                {
                    LocalOperationalLog.Warn("FileTransferService", $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=loopback_session_multiple_readers");
                }

                var receiveFailure = new TaskCompletionSource<Exception>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (receiveFailureGate)
                {
                    activeReceiveFailure = receiveFailure;
                }

                try
                {
                    using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var readTask = frames.Reader.ReadAsync(readCancellation.Token).AsTask();
                    var completed = await Task.WhenAny(receiveFailure.Task, readTask).ConfigureAwait(false);
                    if (completed == receiveFailure.Task)
                    {
                        var injected = await receiveFailure.Task.ConfigureAwait(false);
                        readCancellation.Cancel();
                        try
                        {
                            await readTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
                        {
                        }

                        throw injected;
                    }

                    return await readTask.ConfigureAwait(false);
                }
                finally
                {
                    lock (receiveFailureGate)
                    {
                        if (ReferenceEquals(activeReceiveFailure, receiveFailure))
                        {
                            activeReceiveFailure = null;
                        }
                    }

                    Volatile.Write(ref activeReader, 0);
                }
            }

            public bool TryFailActiveReceive(Exception exception)
            {
                ArgumentNullException.ThrowIfNull(exception);
                lock (receiveFailureGate)
                {
                    return activeReceiveFailure?.TrySetResult(exception) == true;
                }
            }

            public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                var availabilityBypass =
                    frame is FileTransferTransportProbeFrameV6 ||
                    frame is FileTransferRuntimeUnlockPreCommitProbeFrameBase ||
                    IsV6RecoveryFeedbackFrame(frame) ||
                    (owner.AllowUnavailableV4FallbackRecoveryFramesForTests && IsV4FallbackRecoveryFrame(frame));
                if (owner.ThrowWhenUnavailableDataSessionSend &&
                    !IsAvailable &&
                    !availabilityBypass)
                {
                    throw new InvalidOperationException($"File-transfer data session is unavailable: {Volatile.Read(ref availabilityReason)}.");
                }

                return owner.DeliverDataFrameToPeerAsync(frame, frame is FileTransferChunkBatchFrameV4, ct);
            }

            public Task SendAsync(
                FileTransferDataFrame frame,
                FileTransferDataPathSendAuthority authority,
                CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(frame);
                if (!string.Equals(authority.SessionId, SessionId, StringComparison.Ordinal) ||
                    !string.Equals(authority.SessionId, frame.SessionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Loopback file-transfer data path authority session mismatch.");
                }

                if (!string.Equals(authority.TransferId, TransferId, StringComparison.Ordinal) ||
                    !string.Equals(authority.TransferId, frame.TransferId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Loopback file-transfer data path authority transfer mismatch.");
                }

                if (authority.RoutePublicationGeneration <= 0 ||
                    authority.RuntimeGeneration <= 0 ||
                    authority.LegGeneration <= 0)
                {
                    throw new InvalidOperationException("Loopback file-transfer data path authority is not current.");
                }

                var transportKind = authority.TargetTransport == FileTransferTransportKind.Unknown
                    ? FileTransferTransportKind.RegularNkn
                    : authority.TargetTransport;
                SentDataPathSendAuthorities.Enqueue(authority);
                if (owner.peer is { } peerTransport)
                {
                    peerTransport.NextDataFrameTransportKind = transportKind;
                }
                else
                {
                    owner.NextDataFrameTransportKind = transportKind;
                }

                return SendAsync(frame, ct);
            }

            public Task SendRuntimeUnlockTunaPreCommitProbeAsync(
                FileTransferRuntimeUnlockPreCommitProbeFrame probe,
                CancellationToken ct)
            {
                if (owner.peer is { } peerTransport)
                {
                    peerTransport.NextDataFrameTransportKind =
                        FileTransferTransportKind.Tuna;
                }
                else
                {
                    owner.NextDataFrameTransportKind =
                        FileTransferTransportKind.Tuna;
                }

                return SendAsync(probe, ct);
            }

            public Task SendRuntimeUnlockTunaPreCommitProbeAckAsync(
                FileTransferRuntimeUnlockPreCommitProbeAckFrame ack,
                CancellationToken ct)
            {
                if (owner.peer is { } peerTransport)
                {
                    peerTransport.NextDataFrameTransportKind =
                        FileTransferTransportKind.Tuna;
                }
                else
                {
                    owner.NextDataFrameTransportKind =
                        FileTransferTransportKind.Tuna;
                }

                return SendAsync(ack, ct);
            }

            public void Deliver(FileTransferDataFrame frame)
                => Deliver(frame, FileTransferTransportKind.RegularNkn);

            public void Deliver(FileTransferDataFrame frame, FileTransferTransportKind transportKind)
            {
                if (disposed != 0)
                {
                    return;
                }

                var lane = transportKind switch
                {
                    FileTransferTransportKind.Tuna => "tuna",
                    FileTransferTransportKind.RegularNkn => "regular_nkn",
                    _ => "unknown",
                };
                frames.Writer.TryWrite(new FileTransferReceivedDataFrame(frame, transportKind, lane, DateTimeOffset.UtcNow));
            }

            public void SetAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
                => SetAvailability(
                    isAvailable,
                    reason,
                    requiresResumeRequest,
                    FileTransferTransportHandoffKind.None,
                    FileTransferTransportKind.Unknown);

            public void SetAvailability(
                bool isAvailable,
                string reason,
                bool requiresResumeRequest,
                FileTransferTransportHandoffKind handoffKind,
                FileTransferTransportKind targetTransport)
            {
                if (disposed != 0)
                {
                    return;
                }

                var updated = isAvailable ? 1 : 0;
                var previous = Interlocked.Exchange(ref available, updated);
                Volatile.Write(ref availabilityReason, reason);
                if (previous == updated &&
                    (!requiresResumeRequest || handoffKind == FileTransferTransportHandoffKind.None))
                {
                    return;
                }

                AvailabilityChanged?.Invoke(
                    this,
                    new FileTransferDataSessionAvailabilityChangedEventArgs(
                        isAvailable,
                        reason,
                        requiresResumeRequest,
                        handoffKind,
                        targetTransport));
            }

            public void RequestHandoff(
                string reason,
                FileTransferTransportHandoffKind handoffKind,
                FileTransferTransportKind targetTransport)
                => RequestHandoff(
                    reason,
                    handoffKind,
                    targetTransport,
                    runtimeUnlockPreCommitProbeSnapshot: null);

            internal void RequestHandoff(
                string reason,
                FileTransferTransportHandoffKind handoffKind,
                FileTransferTransportKind targetTransport,
                RuntimeUnlockRouteCommitSnapshot? runtimeUnlockPreCommitProbeSnapshot)
            {
                if (disposed != 0)
                {
                    return;
                }

                var isCoordinatorTransitionHandoff =
                    runtimeUnlockPreCommitProbeSnapshot is { LaunchGeneration: > 0 } &&
                    handoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    targetTransport == FileTransferTransportKind.Tuna;
                if (isCoordinatorTransitionHandoff)
                {
                    Interlocked.Exchange(ref available, 0);
                    Volatile.Write(ref availabilityReason, reason);
                }

                AvailabilityChanged?.Invoke(
                    this,
                    new FileTransferDataSessionAvailabilityChangedEventArgs(
                        isCoordinatorTransitionHandoff ? false : IsAvailable,
                        reason,
                        requiresResumeRequest: true,
                        handoffKind,
                        targetTransport,
                        runtimeUnlockPreCommitProbeSnapshot));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                frames.Writer.TryComplete();
            }
        }
    }

    protected class NonDisposingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    protected sealed class DelayedWriteMemoryStream : NonDisposingMemoryStream
    {
        private readonly int delayMilliseconds;
        public DelayedWriteMemoryStream(int delayMilliseconds)
        {
            this.delayMilliseconds = delayMilliseconds;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

}

