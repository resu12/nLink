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
        return ReadOperationalLogText().Length;
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
        var logText = ReadOperationalLogText();
        if (startIndex <= 0)
        {
            return logText;
        }

        if (startIndex >= logText.Length)
        {
            // The operational log can rotate between the initial length snapshot and the final read.
            // When that happens, returning the full current contents is more reliable than returning nothing.
            return logText;
        }

        return logText[startIndex..];
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

    protected sealed class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport, IFileTransferProtocolCapabilities, IFileTransferRouteStatus, IFileTransferTransportProfileProvider, IFileTransferV6TransportEpochObserver, IFileTransferReceiveRecoveryController, IFileTransferRouteCompletionObserver, ITransportAccelerationStatus
    {
        private readonly string sessionId;
        private readonly ConcurrentDictionary<string, LoopbackDataSession> dataSessions = new(StringComparer.Ordinal);
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
        public FileTransferTransportProfileKind FileTransferTransportProfileKind { get; set; } = FileTransferTransportProfileKind.Default;
        public int DataSessionSendDelayMs { get; set; }
        public int DataSessionSendFailureAfterCount { get; set; }
        public int ActiveDataSessionSends => Volatile.Read(ref activeDataSessionSends);
        public int MaxConcurrentDataSessionSends => Volatile.Read(ref maxConcurrentDataSessionSends);
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, bool, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideWithLaneAsync { get; set; }
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
        public ConcurrentQueue<FileTransferReceiveRecoveryRequest> ReceiveRecoveryRequests { get; } = [];
        public ConcurrentQueue<FileTransferRouteCompletedNotification> RouteCompletionNotifications { get; } = [];

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
        public event EventHandler<TransportAccelerationStateChangedEventArgs>? TransportAccelerationStateChanged;
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
            => ObservedV6TransportEpochs.Enqueue(snapshot);

        public void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
            => ReceiveRecoveryRequests.Enqueue(request);

        public void ObserveFileTransferRouteCompleted(FileTransferRouteCompletedNotification notification)
        {
            RouteCompletionNotifications.Enqueue(notification);
            if (string.Equals(notification.RouteToken, "post_tuna_fallback_v6", StringComparison.Ordinal) &&
                notification.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6)
            {
                IsPostTunaFileFallbackActiveForRouteSelection = false;
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
            return DeliverAsync(payload, (target, delivered) => target.FileTransferOfferReceived?.Invoke(target, new FileTransferOfferReceivedEventArgs(delivered, "loopback-peer")), ct);
        }

        public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
        {
            var payload = message with { SessionId = NormalizeSessionId(message.SessionId) };
            SentAccepts.Enqueue(payload);
            return DeliverAsync(payload, (target, delivered) => target.FileTransferAcceptReceived?.Invoke(target, new FileTransferAcceptReceivedEventArgs(delivered, "loopback-peer")), ct);
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
            FileTransferTransportKind targetTransport)
        {
            NextDataFrameTransportKind = targetTransport == FileTransferTransportKind.Unknown
                ? FileTransferTransportKind.RegularNkn
                : targetTransport;
            foreach (var session in dataSessions.Values)
            {
                session.RequestHandoff(reason, handoffKind, targetTransport);
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
                or FileTransferTransportEpochFrameV6;

        private static bool IsV4FallbackRecoveryFrame(FileTransferDataFrame frame)
            => frame is FileTransferStateFrameV4 or FileTransferChunkBatchFrameV4;

        protected sealed class LoopbackDataSession : IFileTransferDataSession
        {
            private readonly LoopbackFileTransferTransport owner;
            private readonly Channel<FileTransferReceivedDataFrame> frames = Channel.CreateUnbounded<FileTransferReceivedDataFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, });
            private int disposed;
            private int activeReader;
            private int available = 1;
            private string availabilityReason = "available";
            public LoopbackDataSession(LoopbackFileTransferTransport owner, string sessionId, string transferId)
            {
                this.owner = owner;
                SessionId = sessionId;
                TransferId = transferId;
            }

            public string SessionId { get; }
            public string TransferId { get; }
            public bool IsAvailable => Volatile.Read(ref available) != 0;

            public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;
            public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
                => (await ReceiveWithMetadataAsync(ct).ConfigureAwait(false)).Frame;

            public async ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            {
                if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
                {
                    LocalOperationalLog.Warn("FileTransferService", $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=loopback_session_multiple_readers");
                }

                try
                {
                    return await frames.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref activeReader, 0);
                }
            }

            public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                var availabilityBypass =
                    frame is FileTransferTransportProbeFrameV6 ||
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
            {
                if (disposed != 0)
                {
                    return;
                }

                AvailabilityChanged?.Invoke(
                    this,
                    new FileTransferDataSessionAvailabilityChangedEventArgs(
                        IsAvailable,
                        reason,
                        requiresResumeRequest: true,
                        handoffKind,
                        targetTransport));
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

