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

    protected sealed class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport, IFileTransferProtocolCapabilities, IFileTransferTransportProfileProvider
    {
        private readonly string sessionId;
        private readonly ConcurrentDictionary<string, LoopbackDataSession> dataSessions = new(StringComparer.Ordinal);
        private LoopbackFileTransferTransport? peer;
        private int activeDataSessionSends;
        private int maxConcurrentDataSessionSends;
        private int dataSessionSendCount;
        public LoopbackFileTransferTransport(string sessionId)
        {
            this.sessionId = sessionId;
        }

        public bool SupportsFileTransferV4Streaming { get; set; } = true;
        public FileTransferTransportProfileKind FileTransferTransportProfileKind { get; set; } = FileTransferTransportProfileKind.Default;
        public int DataSessionSendDelayMs { get; set; }
        public int DataSessionSendFailureAfterCount { get; set; }
        public int MaxConcurrentDataSessionSends => Volatile.Read(ref maxConcurrentDataSessionSends);
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferDataFrame, bool, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideWithLaneAsync { get; set; }
        public Func<LoopbackFileTransferTransport, FileTransferSessionOpenV2, CancellationToken, Task<bool>>? OutboundSessionOpenDeliveryOverrideAsync { get; set; }
        public Func<FileTransferCompleteV1, CancellationToken, Task>? BeforeCompleteDeliveredAsync { get; set; }
        public Exception? OfferSendException { get; init; }
        public ConcurrentQueue<FileTransferErrorV1> SentErrors { get; } = [];
        public ConcurrentQueue<FileTransferOfferV2> SentOffers { get; } = [];
        public ConcurrentQueue<FileTransferAcceptV1> SentAccepts { get; } = [];
        public ConcurrentQueue<FileTransferDeclineV1> SentDeclines { get; } = [];
        public ConcurrentQueue<FileTransferCancelV1> SentCancels { get; } = [];
        public ConcurrentQueue<FileTransferCompleteV1> SentCompletes { get; } = [];
        public ConcurrentQueue<FileTransferSessionOpenV2> SentSessionOpens { get; } = [];
        public ConcurrentQueue<FileTransferDataFrame> SentDataFrames { get; } = [];

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
        public void Connect(LoopbackFileTransferTransport other)
        {
            peer = other;
            other.peer = this;
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
        public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct) => DeliverAsync(TrackCancel(message with { SessionId = NormalizeSessionId(message.SessionId) }), (target, payload) => target.FileTransferCancelReceived?.Invoke(target, new FileTransferCancelReceivedEventArgs(payload, "loopback-peer")), ct);
        public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct) => DeliverAsync(TrackError(message with { SessionId = NormalizeSessionId(message.SessionId) }), (target, payload) => target.FileTransferErrorReceived?.Invoke(target, new FileTransferErrorReceivedEventArgs(payload, "loopback-peer")), ct);
        public async Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
        {
            var payload = TrackComplete(message with { SessionId = NormalizeSessionId(message.SessionId) });
            if (BeforeCompleteDeliveredAsync is not null)
            {
                await BeforeCompleteDeliveredAsync(payload, ct);
            }

            await DeliverAsync(payload, (target, deliveredPayload) => target.FileTransferCompleteReceived?.Invoke(target, new FileTransferCompleteReceivedEventArgs(deliveredPayload, "loopback-peer")), ct);
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
            if (!TryGetOrCreateDataSession(NormalizeSessionId(frame.SessionId), frame.TransferId, out var localSession) || !localSession.IsAvailable)
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
                if (!session.IsAvailable)
                {
                    return;
                }

                session.Deliver(frame);
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

        protected sealed class LoopbackDataSession : IFileTransferDataSession
        {
            private readonly LoopbackFileTransferTransport owner;
            private readonly Channel<FileTransferDataFrame> frames = Channel.CreateUnbounded<FileTransferDataFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, });
            private int disposed;
            private int activeReader;
            private int available = 1;
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
                return owner.DeliverDataFrameToPeerAsync(frame, frame is FileTransferChunkBatchFrameV4, ct);
            }

            public void Deliver(FileTransferDataFrame frame)
            {
                if (disposed != 0)
                {
                    return;
                }

                frames.Writer.TryWrite(frame);
            }

            public void SetAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
            {
                if (disposed != 0)
                {
                    return;
                }

                var updated = isAvailable ? 1 : 0;
                var previous = Interlocked.Exchange(ref available, updated);
                if (previous == updated)
                {
                    return;
                }

                AvailabilityChanged?.Invoke(this, new FileTransferDataSessionAvailabilityChangedEventArgs(isAvailable, reason, requiresResumeRequest));
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

