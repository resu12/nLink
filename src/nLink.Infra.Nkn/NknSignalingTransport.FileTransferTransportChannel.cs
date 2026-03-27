using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    public async Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferOffer,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferAccept,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferDecline,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeRawAsync(
                MsgType.FileTransferSessionOpen,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                useBulkLane: false,
                frameType: null,
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferStartAsync(FileTransferStartV2 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferStart,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferChunkAsync(FileTransferChunkV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferChunk,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferWindowUpdateAsync(FileTransferWindowUpdateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferWindowUpdate,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferMissingRangeAsync(FileTransferMissingRangeV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferMissingRange,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferPressureStateAsync(FileTransferPressureStateV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferPressureState,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferCancel,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferError,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public async Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        message = EnsureFileTransferSessionId(message);

        if (ct.IsCancellationRequested)
        {
            await Task.FromCanceled(ct);
            return;
        }

        await SendFileTransferEnvelopeAsync(
                MsgType.FileTransferComplete,
                message.TransferId,
                FileTransferPayloadCodec.Serialize(message),
                ct)
            .ConfigureAwait(false);
    }

    public Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session id is required.", nameof(sessionId))
            : sessionId.Trim();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(normalizedTransferId, out var session))
            {
                session = new TransportFileTransferDataSession(this, normalizedSessionId, normalizedTransferId);
                fileTransferDataSessions[normalizedTransferId] = session;
            }
            else if (!string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("File-transfer data session id mismatch for existing transfer.");
            }

            return Task.FromResult<IFileTransferDataSession>(session);
        }
    }

    public int ResolveSafeOutboundChunkSize(FileTransferChunkBudgetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "File size must be positive.");
        }

        return FileTransferChunkBudget.ComputeLargestFittingRawChunkSize(
            request.RequestedChunkSizeBytes,
            candidate =>
            {
                if (!TryCalculateFileTransferChunkCount(request.FileSizeBytes, candidate, out var chunkCount))
                {
                    throw new InvalidOperationException("Couldn't determine outbound file-transfer chunk count.");
                }

                return DoesFileTransferChunkFitTransportBudget(
                    request.TransferId,
                    chunkCount,
                    candidate,
                    request.NegotiatedDataProtocolVersion);
            },
            "No valid file-transfer chunk size fits within the NKN payload budget.");
    }

    private static ControlOutboundLane ResolveControlOutboundLane(MsgType messageType, bool isLowPriorityMouseMove = false)
        => ControlOutboundQueue.ResolveLane(messageType, isLowPriorityMouseMove);

    private async Task SendFileTransferEnvelopeAsync(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        CancellationToken ct)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_no_session_context");
            Log($"SendFileTransfer{messageType}Async failed (reason=no_session_context)");
            throw new InvalidOperationException("Session context is not set.");
        }

        var destination = messageType == MsgType.FileTransferChunk
            ? remoteBulkEndpoint
            : remoteEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_no_remote_endpoint");
            Log($"SendFileTransfer{messageType}Async failed (reason=no_remote_endpoint)");
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        if (!TryValidateAndTrackFileTransferMessage(messageType, normalizedTransferId, inbound: false, applyStateChange: false, out var failureReason))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_{failureReason}");
            Log($"SendFileTransfer{messageType}Async failed (reason={failureReason}, transfer_id={normalizedTransferId})");
            throw new InvalidOperationException($"File-transfer state rejected message '{messageType}': {failureReason}.");
        }

        var securePayload = CreateSecureFileTransferPayload(messageType, normalizedTransferId, plaintextPayload);
        var envelope = CreateEnvelope(envelopeCode, messageType, securePayload, replyTo: null);
        if (messageType == MsgType.FileTransferChunk)
        {
            await SendBulkEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
        }
        else
        {
            await SendEnvelopeAsync(destination, envelope, ct).ConfigureAwait(false);
        }

        if (!TryValidateAndTrackFileTransferMessage(messageType, normalizedTransferId, inbound: false, applyStateChange: true, out _))
        {
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_state_race; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={normalizedTransferId}; source={LocalPeerAddress}");
        }

        LogFileTransferEnvelopeEvent(
            "sent",
            messageType,
            normalizedTransferId,
            source: messageType == MsgType.FileTransferChunk ? client.BulkAddress : LocalPeerAddress);
        Log($"SendFileTransfer{messageType}Async sent (msg_id={envelope.MessageId}, transfer_id_len={normalizedTransferId.Length})");
    }

    private async Task SendFileTransferEnvelopeRawAsync(
        MsgType messageType,
        string transferId,
        byte[] plaintextPayload,
        bool useBulkLane,
        string? frameType,
        CancellationToken ct)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            throw new InvalidOperationException("Session context is not set.");
        }

        var destination = useBulkLane ? remoteBulkEndpoint : remoteEndpoint;
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("Remote endpoint is not known yet.");
        }

        var securePayload = CreateSecureFileTransferPayload(messageType, normalizedTransferId, plaintextPayload);
        var envelope = CreateEnvelope(envelopeCode, messageType, securePayload, replyTo: null);
        var transportPayload = EnvelopeCodec.Serialize(envelope);
        var lane = useBulkLane ? "bulk" : "control";
        var bridgeCommandBytes = NknBridgePayloadAccounting.MeasureSendFrameBytes(destination, transportPayload);
        LogFileTransferPayloadBudget(
            normalizedTransferId,
            messageType,
            frameType,
            lane,
            plaintextPayload.Length,
            securePayload.Length,
            transportPayload.Length,
            bridgeCommandBytes,
            rejected: false);
        if (transportPayload.Length > FileTransferMaxBridgePayloadBytes)
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_bridge_send_payload_too_large");
            LogFileTransferPayloadBudget(
                normalizedTransferId,
                messageType,
                frameType,
                lane,
                plaintextPayload.Length,
                securePayload.Length,
                transportPayload.Length,
                bridgeCommandBytes,
                rejected: true);
            throw new InvalidOperationException(
                $"Bridge payload too large for 'send' (max {FileTransferMaxBridgePayloadBytes} bytes).");
        }

        if (useBulkLane)
        {
            await SendBulkEnvelopeAsync(destination, envelope, transportPayload, ct).ConfigureAwait(false);
        }
        else
        {
            await SendEnvelopeAsync(destination, envelope, transportPayload, ct).ConfigureAwait(false);
        }

        LogFileTransferEnvelopeEvent(
            "sent",
            messageType,
            normalizedTransferId,
            source: useBulkLane ? client.BulkAddress : LocalPeerAddress);
    }

    private bool DoesFileTransferChunkFitTransportBudget(
        string transferId,
        int chunkCount,
        int rawChunkSize,
        int negotiatedDataProtocolVersion)
    {
        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            throw new InvalidOperationException("Session security state does not have an active session id.");
        }

        if (!TryGetCurrentEnvelopeCode(out var envelopeCode))
        {
            throw new InvalidOperationException("Session context is not set.");
        }

        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var chunkIndex = Math.Max(0, chunkCount - 1);
        var estimateFrame = CreateChunkFrameForTransportBudgetEstimate(
            negotiatedDataProtocolVersion,
            sessionId.Value,
            normalizedTransferId,
            chunkIndex,
            chunkCount,
            rawChunkSize);
        var plaintextPayload = FileTransferDataFrameCodec.Serialize(estimateFrame);

        var securePayload = CreateSecureFileTransferPayloadForBudgetEstimate(
            MsgType.FileTransferDataFrame,
            normalizedTransferId,
            plaintextPayload);
        var envelope = new Envelope(
            Version: EnvelopeVersion,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.FileTransferDataFrame,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
        var transportPayload = EnvelopeCodec.Serialize(envelope);
        var bridgeCommandBytes = NknBridgePayloadAccounting.MeasureSendFrameBytes(remoteBulkEndpoint ?? string.Empty, transportPayload);
        LogFileTransferPayloadBudget(
            normalizedTransferId,
            MsgType.FileTransferDataFrame,
            estimateFrame.Type,
            "bulk_estimate",
            plaintextPayload.Length,
            securePayload.Length,
            transportPayload.Length,
            bridgeCommandBytes,
            rejected: transportPayload.Length > FileTransferMaxBridgePayloadBytes);
        return transportPayload.Length <= FileTransferMaxBridgePayloadBytes;
    }

    private static FileTransferDataFrameV2 CreateChunkFrameForTransportBudgetEstimate(
        int negotiatedDataProtocolVersion,
        string sessionId,
        string transferId,
        int chunkIndex,
        int chunkCount,
        int rawChunkSize)
        => negotiatedDataProtocolVersion switch
        {
            FileTransferProtocol.ProtocolVersionV3 => new FileTransferChunkDataFrameV3
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = new byte[rawChunkSize],
            },
            _ => new FileTransferChunkDataFrameV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = new byte[rawChunkSize],
            },
        };

    private static bool TryCalculateFileTransferChunkCount(long fileSizeBytes, int chunkSizeBytes, out int chunkCount)
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

    private void SetFileTransferDataSessionsAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
    {
        TransportFileTransferDataSession[] sessions;
        lock (gate)
        {
            if (fileTransferDataSessions.Count == 0)
            {
                return;
            }

            sessions = fileTransferDataSessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            session.SetAvailability(isAvailable, reason, requiresResumeRequest);
        }
    }

    private void OnClientMessageReceived(object? sender, NknIncomingMessage e)
    {
        if (disposed)
        {
            return;
        }

        if (IsSelfSource(e.Source))
        {
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("self_source");
            Log($"Envelope ignored from self (source={e.Source})");
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.Source))
        {
            lastPeerAddress = e.Source;
        }

        if (!EnvelopeCodec.TryDeserialize(e.Payload, out var env))
        {
            NknRuntimeDiagnostics.SetLastError("envelope_parse_failed");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("parse_failed");
            Log($"Envelope parse failed (payload_len={e.Payload.Length})");
            return;
        }

        NknRuntimeDiagnostics.SetLastEnvelopeType(env.Type.ToString());

        if (env.Type != MsgType.JoinRequest &&
            !string.IsNullOrWhiteSpace(env.Code) &&
            TryGetCurrentEnvelopeCode(out var expectedEnvelopeCode) &&
            !string.Equals(env.Code, expectedEnvelopeCode, StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.SetLastError("envelope_code_mismatch");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("code_mismatch");
            Log($"Envelope ignored (type={env.Type}, payload_len={env.Payload.Length}, msg_id={env.MessageId}, code={env.Code})");
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dedupTimestamp = env.UnixTimeMs > 0 ? env.UnixTimeMs : nowUnixMs;
        if (!seenMessageIds.TryAdd(env.MessageId, dedupTimestamp))
        {
            if ((env.Type == MsgType.JoinRequest ||
                 env.Type == MsgType.Chat ||
                 env.Type == MsgType.SessionHandshakeStart) &&
                !string.IsNullOrWhiteSpace(e.Source))
            {
                SendAckFireAndForget(e.Source, env.Code, env.MessageId);
            }

            Log($"Envelope duplicate ignored (type={env.Type}, msg_id={env.MessageId})");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("duplicate");
            return;
        }

        NknRuntimeDiagnostics.IncrementMessagesReceived();
        Log($"Envelope received (type={env.Type}, payload_len={env.Payload.Length}, msg_id={env.MessageId}, reply_to={env.ReplyTo ?? "-"})");

        try
        {
            envelopeRouter.RouteInboundMessage(e.Source, e.Channel, env);
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
            Log($"Envelope dispatch failed (type={env.Type}, msg_id={env.MessageId}, ex={ex.GetType().Name})");
        }
    }

    private void HandleInboundFileTransferEnvelope(string source, NknBridgeChannel channel, Envelope env)
    {
        if (!TryPrepareInboundFileTransferDispatch(source, channel, env, out var work))
        {
            return;
        }

        if (work.UsesDedicatedChunkDispatch)
        {
            DispatchInboundFileTransferChunk(work);
            return;
        }

        EnqueueInboundFileTransferControlDispatch(work);
    }

    internal void RouteControlEnvelope(string source, Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.ControlRequest:
                HandleControlRequest(source, env);
                break;
            case MsgType.ControlResponse:
                HandleControlResponse(source, env);
                break;
            case MsgType.ControlStart:
                HandleControlStart(source, env);
                break;
            case MsgType.ControlStop:
                HandleControlStop(source, env);
                break;
            case MsgType.ControlInput:
                HandleControlInput(source, env);
                break;
            case MsgType.ControlAck:
                HandleControlAck(source, env);
                break;
            case MsgType.ControlStateSnapshot:
                HandleControlStateSnapshot(source, env);
                break;
            case MsgType.ControlDisplayInfo:
                HandleControlDisplayInfo(source, env);
                break;
            default:
                throw new InvalidOperationException($"Control channel cannot route {env.Type}.");
        }
    }

    internal void RouteScreenShareEnvelope(string source, Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.ScreenShareFrame:
                HandleScreenShareFrame(source, env);
                break;
            case MsgType.ScreenShareStop:
                HandleScreenShareStop(source, env);
                break;
            default:
                throw new InvalidOperationException($"Screen share channel cannot route {env.Type}.");
        }
    }

    internal void RouteFileTransferEnvelope(string source, NknBridgeChannel channel, Envelope env)
        => HandleInboundFileTransferEnvelope(source, channel, env);

    internal void HandleUnexpectedEnvelopeType(Envelope env)
    {
        NknRuntimeDiagnostics.SetLastError("unexpected_message_type");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason("unexpected_type");
        Log($"Unexpected envelope type (type={env.Type}, msg_id={env.MessageId})");
    }

    private void EnqueueInboundFileTransferControlDispatch(InboundFileTransferDispatchWork work)
    {
        var shouldStartDrainer = false;

        lock (inboundFileTransferDispatchGate)
        {
            var dispatchOrder = Interlocked.Increment(ref nextInboundFileTransferControlDispatchOrder);
            work = work with
            {
                Generation = Volatile.Read(ref inboundFileTransferDispatchGeneration),
                Order = dispatchOrder,
            };
            pendingInboundFileTransferControlDispatch[dispatchOrder] = work;
            if (work.BlocksChunkDispatch && work.LifecycleCompletion is not null)
            {
                inboundFileTransferLifecycleDispatchTail = work.LifecycleCompletion.Task;
            }

            if (!inboundFileTransferControlDispatchActive)
            {
                inboundFileTransferControlDispatchActive = true;
                shouldStartDrainer = true;
            }
        }

        if (shouldStartDrainer)
        {
            _ = Task.Run(ProcessInboundFileTransferControlDispatchQueueAsync, CancellationToken.None);
        }
    }

    private void DispatchInboundFileTransferChunk(InboundFileTransferDispatchWork work)
    {
        Task lifecycleBarrier;
        lock (inboundFileTransferDispatchGate)
        {
            work = work with { Generation = Volatile.Read(ref inboundFileTransferDispatchGeneration) };
            lifecycleBarrier = inboundFileTransferLifecycleDispatchTail;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await lifecycleBarrier.ConfigureAwait(false);
                    if (disposed || work.Generation != Volatile.Read(ref inboundFileTransferDispatchGeneration))
                    {
                        return;
                    }

                    work.Dispatch();
                }
                catch (Exception ex)
                {
                    NknRuntimeDiagnostics.SetLastError(ex);
                    NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
                    Log($"Envelope dispatch failed (type={work.Type}, transfer_id={work.TransferId}, ex={ex.GetType().Name})");
                }
            },
            CancellationToken.None);
    }

    private async Task ProcessInboundFileTransferControlDispatchQueueAsync()
    {
        await Task.Yield();

        while (true)
        {
            InboundFileTransferDispatchWork work;
            lock (inboundFileTransferDispatchGate)
            {
                if (!pendingInboundFileTransferControlDispatch.TryGetValue(nextInboundFileTransferControlDispatchToProcess, out work))
                {
                    inboundFileTransferControlDispatchActive = false;
                    return;
                }

                pendingInboundFileTransferControlDispatch.Remove(nextInboundFileTransferControlDispatchToProcess);
                nextInboundFileTransferControlDispatchToProcess++;
            }

            try
            {
                if (!disposed && work.Generation == Volatile.Read(ref inboundFileTransferDispatchGeneration))
                {
                    work.Dispatch();
                }
            }
            catch (Exception ex)
            {
                NknRuntimeDiagnostics.SetLastError(ex);
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
                Log($"Envelope dispatch failed (type={work.Type}, transfer_id={work.TransferId}, ex={ex.GetType().Name})");
            }
            finally
            {
                work.LifecycleCompletion?.TrySetResult(true);
            }
        }
    }

    private void ResetInboundFileTransferDispatchQueue()
    {
        List<TaskCompletionSource<bool>>? pendingLifecycleCompletions = null;
        lock (inboundFileTransferDispatchGate)
        {
            foreach (var work in pendingInboundFileTransferControlDispatch.Values)
            {
                if (work.LifecycleCompletion is not null)
                {
                    pendingLifecycleCompletions ??= [];
                    pendingLifecycleCompletions.Add(work.LifecycleCompletion);
                }
            }

            pendingInboundFileTransferControlDispatch.Clear();
            nextInboundFileTransferControlDispatchToProcess = 1;
            nextInboundFileTransferControlDispatchOrder = 0;
            inboundFileTransferControlDispatchActive = false;
            Interlocked.Increment(ref inboundFileTransferDispatchGeneration);
            inboundFileTransferLifecycleDispatchTail = Task.CompletedTask;
        }

        if (pendingLifecycleCompletions is null)
        {
            return;
        }

        foreach (var completion in pendingLifecycleCompletions)
        {
            completion.TrySetResult(true);
        }
    }

    private void HandleFileTransferOffer(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferOffer, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeOffer(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_offer_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_offer_payload_invalid");
            Log($"FileTransferOffer payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_offer", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_offer", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_offer", MsgType.FileTransferOffer, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferOffer, message.TransferId, source);
        Log($"FileTransferOffer received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, file_name_len={message.FileName.Length}, size_bytes={message.FileSizeBytes})");
        FileTransferOfferReceived?.Invoke(this, new FileTransferOfferReceivedEventArgs(message, source));
    }

    private void HandleFileTransferAccept(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferAccept, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeAccept(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_accept_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_accept_payload_invalid");
            Log($"FileTransferAccept payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_accept", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_accept", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_accept", MsgType.FileTransferAccept, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferAccept, message.TransferId, source);
        Log($"FileTransferAccept received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length})");
        FileTransferAcceptReceived?.Invoke(this, new FileTransferAcceptReceivedEventArgs(message, source));
    }

    private void HandleFileTransferDecline(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferDecline, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeDecline(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_decline_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_decline_payload_invalid");
            Log($"FileTransferDecline payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_decline", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_decline", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_decline", MsgType.FileTransferDecline, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDecline, message.TransferId, source);
        Log($"FileTransferDecline received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
        FileTransferDeclineReceived?.Invoke(this, new FileTransferDeclineReceivedEventArgs(message, source));
    }

    private void HandleFileTransferStart(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferStart, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeStart(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_start_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_start_payload_invalid");
            Log($"FileTransferStart payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_start", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_start", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_start", MsgType.FileTransferStart, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferStart, message.TransferId, source);
        Log($"FileTransferStart received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk_count={message.ChunkCount}, chunk_size={message.ChunkSizeBytes})");
        FileTransferStartReceived?.Invoke(this, new FileTransferStartReceivedEventArgs(message, source));
    }

    private void HandleFileTransferChunk(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferChunk, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeChunk(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_chunk_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_chunk_payload_invalid");
            Log($"FileTransferChunk payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_chunk", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_chunk", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_chunk", MsgType.FileTransferChunk, message.TransferId, env.MessageId, source))
        {
            return;
        }

        Log($"FileTransferChunk received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk={message.ChunkIndex + 1}/{message.ChunkCount})");
        FileTransferChunkReceived?.Invoke(this, new FileTransferChunkReceivedEventArgs(message, source));
    }

    private void HandleFileTransferWindowUpdate(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferWindowUpdate, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeWindowUpdate(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_window_update_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_window_update_payload_invalid");
            Log($"FileTransferWindowUpdate payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_window_update", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_window_update", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_window_update", MsgType.FileTransferWindowUpdate, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferWindowUpdate, message.TransferId, source);
        FileTransferWindowUpdateReceived?.Invoke(this, new FileTransferWindowUpdateReceivedEventArgs(message, source));
    }

    private void HandleFileTransferMissingRange(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferMissingRange, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeMissingRange(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_missing_range_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_missing_range_payload_invalid");
            Log($"FileTransferMissingRange payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_missing_range", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_missing_range", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_missing_range", MsgType.FileTransferMissingRange, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferMissingRange, message.TransferId, source);
        FileTransferMissingRangeReceived?.Invoke(this, new FileTransferMissingRangeReceivedEventArgs(message, source));
    }

    private void HandleFileTransferCancel(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferCancel, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeCancel(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_cancel_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_cancel_payload_invalid");
            Log($"FileTransferCancel payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_cancel", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_cancel", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_cancel", MsgType.FileTransferCancel, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferCancel, message.TransferId, source);
        Log($"FileTransferCancel received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
        FileTransferCancelReceived?.Invoke(this, new FileTransferCancelReceivedEventArgs(message, source));
    }

    private void HandleFileTransferError(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferError, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeError(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_error_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_error_payload_invalid");
            Log($"FileTransferError payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_error", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_error", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_error", MsgType.FileTransferError, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferError, message.TransferId, source);
        Log($"FileTransferError received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, error_code={message.ErrorCode})");
        FileTransferErrorReceived?.Invoke(this, new FileTransferErrorReceivedEventArgs(message, source));
    }

    private void HandleFileTransferComplete(string source, Envelope env)
    {
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferComplete, out var securePayload))
        {
            return;
        }

        if (!FileTransferPayloadCodec.TryDeserializeComplete(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_complete_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_complete_payload_invalid");
            Log($"FileTransferComplete payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_complete", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_complete", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_complete", MsgType.FileTransferComplete, message.TransferId, env.MessageId, source))
        {
            return;
        }

        LogFileTransferEnvelopeEvent("received", MsgType.FileTransferComplete, message.TransferId, source);
        Log($"FileTransferComplete received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, size_bytes={message.FileSizeBytes})");
        FileTransferCompleteReceived?.Invoke(this, new FileTransferCompleteReceivedEventArgs(message, source));
    }

    private bool TryPrepareInboundFileTransferDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        switch (env.Type)
        {
            case MsgType.FileTransferOffer:
                return TryPrepareFileTransferOfferDispatch(source, env, out work);
            case MsgType.FileTransferAccept:
                return TryPrepareFileTransferAcceptDispatch(source, env, out work);
            case MsgType.FileTransferDecline:
                return TryPrepareFileTransferDeclineDispatch(source, env, out work);
            case MsgType.FileTransferStart:
                return TryPrepareFileTransferStartDispatch(source, env, out work);
            case MsgType.FileTransferChunk:
                return TryPrepareFileTransferChunkDispatch(source, channel, env, out work);
            case MsgType.FileTransferWindowUpdate:
                return TryPrepareFileTransferWindowUpdateDispatch(source, env, out work);
            case MsgType.FileTransferMissingRange:
                return TryPrepareFileTransferMissingRangeDispatch(source, env, out work);
            case MsgType.FileTransferPressureState:
                return TryPrepareFileTransferPressureStateDispatch(source, env, out work);
            case MsgType.FileTransferCancel:
                return TryPrepareFileTransferCancelDispatch(source, env, out work);
            case MsgType.FileTransferError:
                return TryPrepareFileTransferErrorDispatch(source, env, out work);
            case MsgType.FileTransferComplete:
                return TryPrepareFileTransferCompleteDispatch(source, env, out work);
            case MsgType.FileTransferSessionOpen:
                return TryPrepareFileTransferSessionOpenDispatch(source, env, out work);
            case MsgType.FileTransferDataFrame:
                return TryPrepareFileTransferDataFrameDispatch(source, channel, env, out work);
            default:
                NknRuntimeDiagnostics.SetLastError("unexpected_filetransfer_message_type");
                NknRuntimeDiagnostics.SetLastEnvelopeDropReason("unexpected_filetransfer_type");
                Log($"Unexpected file-transfer envelope type (type={env.Type}, msg_id={env.MessageId})");
                work = default;
                return false;
        }
    }

    private bool TryPrepareFileTransferOfferDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferOffer, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeOffer(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_offer_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_offer_payload_invalid");
            Log($"FileTransferOffer payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_offer", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_offer", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_offer", MsgType.FileTransferOffer, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferOffer,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferOffer, message.TransferId, source);
                Log($"FileTransferOffer received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, file_name_len={message.FileName.Length}, size_bytes={message.FileSizeBytes})");
                FileTransferOfferReceived?.Invoke(this, new FileTransferOfferReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferAcceptDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferAccept, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeAccept(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_accept_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_accept_payload_invalid");
            Log($"FileTransferAccept payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_accept", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_accept", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_accept", MsgType.FileTransferAccept, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferAccept,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferAccept, message.TransferId, source);
                Log($"FileTransferAccept received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length})");
                FileTransferAcceptReceived?.Invoke(this, new FileTransferAcceptReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferDeclineDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferDecline, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeDecline(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_decline_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_decline_payload_invalid");
            Log($"FileTransferDecline payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_decline", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_decline", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_decline", MsgType.FileTransferDecline, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferDecline,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDecline, message.TransferId, source);
                Log($"FileTransferDecline received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
                FileTransferDeclineReceived?.Invoke(this, new FileTransferDeclineReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferStartDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferStart, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeStart(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_start_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_start_payload_invalid");
            Log($"FileTransferStart payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_start", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_start", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_start", MsgType.FileTransferStart, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferStart,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferStart, message.TransferId, source);
                Log($"FileTransferStart received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk_count={message.ChunkCount}, chunk_size={message.ChunkSizeBytes})");
                FileTransferStartReceived?.Invoke(this, new FileTransferStartReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferChunkDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferChunk, out var securePayload))
        {
            LogFileTransferChunkRejected(null, null, env.Payload.Length, channel, "decrypt_or_secure_validation_failed");
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeChunk(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_chunk_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_chunk_payload_invalid");
            Log($"FileTransferChunk payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            LogFileTransferChunkRejected(null, null, env.Payload.Length, channel, "payload_invalid");
            return false;
        }

        var payloadBytes = GetBase64PayloadBytes(message.DataBase64);
        LogFileTransferChunkIngress(message.TransferId, message.ChunkIndex, payloadBytes, channel);

        if (!TryValidateFileTransferSecureMetadata("file_transfer_chunk", securePayload.Metadata, message.TransferId, env.MessageId))
        {
            LogFileTransferChunkRejected(message.TransferId, message.ChunkIndex, payloadBytes, channel, "secure_metadata");
            return false;
        }

        if (!TryValidateFileTransferMessageSession("file_transfer_chunk", message.SessionId, message.TransferId, env.MessageId, source))
        {
            LogFileTransferChunkRejected(message.TransferId, message.ChunkIndex, payloadBytes, channel, "session_mismatch");
            return false;
        }

        if (!TryValidateFileTransferDispatchState("file_transfer_chunk", MsgType.FileTransferChunk, message.TransferId, env.MessageId, source))
        {
            LogFileTransferChunkRejected(message.TransferId, message.ChunkIndex, payloadBytes, channel, "dispatch_state");
            return false;
        }

        LogFileTransferChunkValidated(message.TransferId, message.ChunkIndex, payloadBytes, channel);
        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferChunk,
            message.TransferId,
            () =>
            {
                LogFileTransferChunkDispatched(message.TransferId, message.ChunkIndex, payloadBytes, channel);
                Log($"FileTransferChunk received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, chunk={message.ChunkIndex + 1}/{message.ChunkCount})");
                FileTransferChunkReceived?.Invoke(this, new FileTransferChunkReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferWindowUpdateDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferWindowUpdate, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeWindowUpdate(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_window_update_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_window_update_payload_invalid");
            Log($"FileTransferWindowUpdate payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_window_update", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_window_update", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_window_update", MsgType.FileTransferWindowUpdate, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferWindowUpdate,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferWindowUpdate, message.TransferId, source);
                FileTransferWindowUpdateReceived?.Invoke(this, new FileTransferWindowUpdateReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferMissingRangeDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferMissingRange, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeMissingRange(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_missing_range_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_missing_range_payload_invalid");
            Log($"FileTransferMissingRange payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_missing_range", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_missing_range", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_missing_range", MsgType.FileTransferMissingRange, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferMissingRange,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferMissingRange, message.TransferId, source);
                FileTransferMissingRangeReceived?.Invoke(this, new FileTransferMissingRangeReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferPressureStateDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferPressureState, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializePressureState(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_pressure_state_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_pressure_state_payload_invalid");
            Log($"FileTransferPressureState payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_pressure_state", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_pressure_state", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_pressure_state", MsgType.FileTransferPressureState, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferPressureState,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferPressureState, message.TransferId, source);
                FileTransferPressureStateReceived?.Invoke(this, new FileTransferPressureStateReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferCancelDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferCancel, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeCancel(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_cancel_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_cancel_payload_invalid");
            Log($"FileTransferCancel payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_cancel", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_cancel", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_cancel", MsgType.FileTransferCancel, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferCancel,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferCancel, message.TransferId, source);
                Log($"FileTransferCancel received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, has_reason={message.Reason is not null})");
                FileTransferCancelReceived?.Invoke(this, new FileTransferCancelReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferErrorDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferError, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeError(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_error_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_error_payload_invalid");
            Log($"FileTransferError payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_error", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_error", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_error", MsgType.FileTransferError, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferError,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferError, message.TransferId, source);
                Log($"FileTransferError received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, error_code={message.ErrorCode})");
                FileTransferErrorReceived?.Invoke(this, new FileTransferErrorReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferCompleteDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferComplete, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeComplete(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_complete_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_complete_payload_invalid");
            Log($"FileTransferComplete payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_complete", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_complete", message.SessionId, message.TransferId, env.MessageId, source) ||
            !TryValidateFileTransferDispatchState("file_transfer_complete", MsgType.FileTransferComplete, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferComplete,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferComplete, message.TransferId, source);
                Log($"FileTransferComplete received (msg_id={env.MessageId}, transfer_id_len={message.TransferId.Length}, size_bytes={message.FileSizeBytes})");
                FileTransferCompleteReceived?.Invoke(this, new FileTransferCompleteReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferSessionOpenDispatch(string source, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferSessionOpen, out var securePayload))
        {
            return false;
        }

        if (!FileTransferPayloadCodec.TryDeserializeSessionOpen(securePayload.Plaintext, out var message))
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_session_open_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_session_open_payload_invalid");
            Log($"FileTransferSessionOpen payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_session_open", securePayload.Metadata, message.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_session_open", message.SessionId, message.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferSessionOpen,
            message.TransferId,
            () =>
            {
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferSessionOpen, message.TransferId, source);
                FileTransferSessionOpenReceived?.Invoke(this, new FileTransferSessionOpenReceivedEventArgs(message, source));
            });
        return true;
    }

    private bool TryPrepareFileTransferDataFrameDispatch(string source, NknBridgeChannel channel, Envelope env, out InboundFileTransferDispatchWork work)
    {
        work = default;
        if (!TryDecryptFileTransferPayload(source, env, MsgType.FileTransferDataFrame, out var securePayload))
        {
            return false;
        }

        if (!FileTransferDataFrameCodec.TryDeserialize(securePayload.Plaintext, out var frame) || frame is null)
        {
            NknRuntimeDiagnostics.SetLastError("filetransfer_data_frame_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("filetransfer_data_frame_payload_invalid");
            Log($"FileTransferDataFrame payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_data_frame_decode_failed; transport=nkn; transfer_id=(unknown); session_id={securePayload.Metadata.SessionId.Value}; message_id={env.MessageId}");
            return false;
        }

        if (!TryValidateFileTransferSecureMetadata("file_transfer_data_frame", securePayload.Metadata, frame.TransferId, env.MessageId) ||
            !TryValidateFileTransferMessageSession("file_transfer_data_frame", frame.SessionId, frame.TransferId, env.MessageId, source) ||
            !TryValidateKnownInboundFileTransferDataPath("file_transfer_data_frame", frame.TransferId, env.MessageId, source))
        {
            return false;
        }

        work = CreateInboundFileTransferDispatchWork(
            MsgType.FileTransferDataFrame,
            frame.TransferId,
            () =>
            {
                DeliverFileTransferDataFrame(frame, channel);
                LogFileTransferEnvelopeEvent("received", MsgType.FileTransferDataFrame, frame.TransferId, source);
            });
        return true;
    }

    private static InboundFileTransferDispatchWork CreateInboundFileTransferDispatchWork(
        MsgType messageType,
        string transferId,
        Action dispatch)
    {
        var blocksChunkDispatch = messageType is MsgType.FileTransferOffer
            or MsgType.FileTransferAccept
            or MsgType.FileTransferDecline
            or MsgType.FileTransferStart
            or MsgType.FileTransferSessionOpen
            or MsgType.FileTransferCancel
            or MsgType.FileTransferError
            or MsgType.FileTransferComplete;
        return new InboundFileTransferDispatchWork(
            Generation: 0,
            Order: 0,
            Type: messageType,
            TransferId: transferId,
            UsesDedicatedChunkDispatch: messageType is MsgType.FileTransferChunk or MsgType.FileTransferDataFrame,
            BlocksChunkDispatch: blocksChunkDispatch,
            Dispatch: dispatch,
            LifecycleCompletion: blocksChunkDispatch
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null);
    }

    private bool TryValidateFileTransferDispatchState(
        string messageType,
        MsgType transportMessageType,
        string transferId,
        string messageId,
        string? source)
    {
        if (TryValidateAndTrackFileTransferMessage(transportMessageType, transferId, inbound: true, applyStateChange: true, out var failureReason))
        {
            return true;
        }

        if (IsBenignLateFileTransferControlRejection(transportMessageType, failureReason))
        {
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_message_ignored; message_type={messageType}; reason={failureReason}; transfer_id={transferId}; source={source ?? "(none)"}; msg_id={messageId}");
            Log($"FileTransfer message ignored (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={transferId})");
            return false;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; transfer_id={transferId}; source={source ?? "(none)"}; msg_id={messageId}");
        Log($"FileTransfer message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={transferId})");
        return false;
    }

    private bool TryValidateKnownInboundFileTransferDataPath(
        string messageType,
        string transferId,
        string messageId,
        string? source)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (controlSecureStateGate)
        {
            if (fileTransferStates.TryGetValue(normalizedTransferId, out var currentState) &&
                !currentState.IsTerminal)
            {
                return true;
            }
        }

        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason=unknown_transfer_id; session_id={CurrentSessionSecurityState.SessionId?.Value ?? "(none)"}; transfer_id={normalizedTransferId}; source={source ?? "(none)"}");
        Log($"FileTransfer message rejected (type={messageType}, msg_id={messageId}, reason=unknown_transfer_id, transfer_id={normalizedTransferId})");
        return false;
    }

    private static bool IsBenignLateFileTransferControlRejection(MsgType transportMessageType, string failureReason)
        => failureReason == "unknown_transfer_id" &&
           transportMessageType is MsgType.FileTransferWindowUpdate or MsgType.FileTransferPressureState;

    private void HandleControlRequest(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlRequest, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlRequest(securePayload.Plaintext, out var request))
        {
            NknRuntimeDiagnostics.SetLastError("controlrequest_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlrequest_payload_invalid");
            Log($"ControlRequest payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_request", securePayload.Metadata, request.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_request",
                request.SessionId,
                env.MessageId,
                request.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlRequestReceived,
                request.RequestId,
                source,
                Decision: null));
        Log($"ControlRequest received (msg_id={env.MessageId}, request_id_len={request.RequestId.Length}, has_reason={request.Reason is not null})");
        RemoteControlRequestReceived?.Invoke(this, new RemoteControlRequestReceivedEventArgs(request, source));
    }

    private void HandleControlResponse(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlResponse, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlResponse(securePayload.Plaintext, out var response))
        {
            NknRuntimeDiagnostics.SetLastError("controlresponse_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlresponse_payload_invalid");
            Log($"ControlResponse payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_response", securePayload.Metadata, response.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_response",
                response.SessionId,
                env.MessageId,
                response.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlResponseReceived,
                response.RequestId,
                source,
                response.Decision));
        Log($"ControlResponse received (msg_id={env.MessageId}, request_id_len={response.RequestId.Length}, decision={response.Decision})");
        RemoteControlResponseReceived?.Invoke(this, new RemoteControlResponseReceivedEventArgs(response, source));
    }

    private void HandleControlStart(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStart, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStart(securePayload.Plaintext, out var start))
        {
            NknRuntimeDiagnostics.SetLastError("controlstart_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstart_payload_invalid");
            Log($"ControlStart payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_start", securePayload.Metadata, start.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_start",
                start.SessionId,
                env.MessageId,
                start.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlStartReceived,
                start.RequestId,
                source,
                Decision: null));
        Log($"ControlStart received (msg_id={env.MessageId}, request_id_len={start.RequestId.Length}, has_token={start.ConsentToken is not null})");
        RemoteControlStartReceived?.Invoke(this, new RemoteControlStartReceivedEventArgs(start, source));
    }

    private void HandleControlStop(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStop, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStop(securePayload.Plaintext, out var stop))
        {
            NknRuntimeDiagnostics.SetLastError("controlstop_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstop_payload_invalid");
            Log($"ControlStop payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_stop", securePayload.Metadata, stop.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_stop",
                stop.SessionId,
                env.MessageId,
                stop.RequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.ControlStopReceived,
                stop.RequestId,
                source,
                Decision: null));
        Log($"ControlStop received (msg_id={env.MessageId}, request_id_len={stop.RequestId.Length}, has_reason={stop.Reason is not null})");
        RemoteControlStopReceived?.Invoke(this, new RemoteControlStopReceivedEventArgs(stop, source));
    }

    private void HandleControlInput(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlInput, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlInput(securePayload.Plaintext, out var input))
        {
            NknRuntimeDiagnostics.SetLastError("controlinput_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlinput_payload_invalid");
            Log($"ControlInput payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_input", securePayload.Metadata, input.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_input",
                input.SessionId,
                env.MessageId,
                input.RequestId,
                source))
        {
            return;
        }

        LogControlInputReceived(env, input);
        RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(input, source));
    }

    private void HandleControlAck(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlAck, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlAck(securePayload.Plaintext, out var ack))
        {
            NknRuntimeDiagnostics.SetLastError("controlack_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlack_payload_invalid");
            Log($"ControlAck payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_ack", securePayload.Metadata, ack.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_ack",
                ack.SessionId,
                env.MessageId,
                ack.RequestId,
                source))
        {
            return;
        }

        Log($"ControlAck received (msg_id={env.MessageId}, request_id_len={ack.RequestId.Length}, ack_seq={ack.AckSeq})");
        RemoteControlAckReceived?.Invoke(this, new RemoteControlAckReceivedEventArgs(ack, source));
    }

    private void HandleControlStateSnapshot(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlStateSnapshot, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(securePayload.Plaintext, out var snapshot))
        {
            NknRuntimeDiagnostics.SetLastError("controlstatesnapshot_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controlstatesnapshot_payload_invalid");
            Log($"ControlStateSnapshot payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_state_snapshot", securePayload.Metadata, snapshot.RequestId, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_state_snapshot",
                snapshot.SessionId,
                env.MessageId,
                snapshot.RequestId,
                source))
        {
            return;
        }

        Log($"ControlStateSnapshot received (msg_id={env.MessageId}, request_id_len={snapshot.RequestId.Length}, seq={snapshot.Seq}, buttons_mask={snapshot.MouseButtonsMask}, modifiers_mask={snapshot.ModifiersMask})");
        RemoteControlStateSnapshotReceived?.Invoke(this, new RemoteControlStateSnapshotReceivedEventArgs(snapshot, source));
    }

    private void LogControlInputReceived(Envelope env, ControlInputMessageV1 input)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var shouldLogInput = false;
        var suppressedCountToReport = 0;

        lock (controlInputReceiveLogGate)
        {
            if (controlInputReceiveLogWindowStartTicks == 0)
            {
                controlInputReceiveLogWindowStartTicks = nowTicks;
            }
            else if (Stopwatch.GetElapsedTime(controlInputReceiveLogWindowStartTicks, nowTicks) >= ControlInputReceiveLogWindow)
            {
                suppressedCountToReport = controlInputReceiveLogSuppressed;
                controlInputReceiveLogSuppressed = 0;
                controlInputReceiveLogCount = 0;
                controlInputReceiveLogWindowStartTicks = nowTicks;
            }

            if (controlInputReceiveLogCount < ControlInputReceiveLogBurst)
            {
                controlInputReceiveLogCount++;
                shouldLogInput = true;
            }
            else
            {
                controlInputReceiveLogSuppressed++;
            }
        }

        if (suppressedCountToReport > 0)
        {
            Log($"ControlInput received (suppressed={suppressedCountToReport} in previous {ControlInputReceiveLogWindow.TotalSeconds:0.#}s window)");
        }

        if (shouldLogInput)
        {
            Log($"ControlInput received (msg_id={env.MessageId}, request_id_len={input.RequestId.Length}, kind={input.Kind}, seq={input.Seq})");
        }
    }

    private void HandleControlDisplayInfo(string source, Envelope env)
    {
        if (!TryDecryptControlPayload(source, env, MsgType.ControlDisplayInfo, out var securePayload))
        {
            return;
        }

        if (!RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(securePayload.Plaintext, out var displayInfo))
        {
            NknRuntimeDiagnostics.SetLastError("controldisplayinfo_payload_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason("controldisplayinfo_payload_invalid");
            Log($"ControlDisplayInfo payload invalid (msg_id={env.MessageId}, payload_len={env.Payload.Length})");
            return;
        }

        if (!TryValidateControlSecureMetadata("control_display_info", securePayload.Metadata, requestId: null, env.MessageId))
        {
            return;
        }

        if (!TryValidateControlMessageSession(
                "control_display_info",
                displayInfo.SessionId,
                env.MessageId,
                transportRemoteControlState.CurrentControlRequestId,
                source))
        {
            return;
        }

        transportRemoteControlState = TransportRemoteControlCoordinator.Apply(
            transportRemoteControlState,
            new TransportRemoteControlEvent(
                TransportRemoteControlEventKind.DisplayInfoChanged,
                transportRemoteControlState.CurrentControlRequestId,
                source,
                Decision: null));
        Log($"ControlDisplayInfo received (msg_id={env.MessageId}, display_id={displayInfo.DisplayId}, revision={displayInfo.Revision}, frame={displayInfo.FrameWidth}x{displayInfo.FrameHeight})");
        RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(displayInfo, source));
    }

    private bool TryValidateControlMessageSession(
        string messageType,
        string? messageSessionId,
        string messageId,
        string? requestId,
        string? source)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = ResolveExpectedRemotePeerAddressForCurrentSession();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) &&
                 !AddressMatchesForSessionPolicy(normalizedSource, expectedSource))
        {
            failureReason = "source_identity_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=control_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; request_id={requestId ?? "(none)"}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}");
        Log($"Control message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, request_id={requestId ?? "(none)"})");
        return false;
    }

    private bool TryValidateScreenShareMessageSession(
        string messageType,
        string? messageSessionId,
        string messageId,
        string? requestId,
        string? source)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = string.Equals(messageType, "screenshare_stop", StringComparison.Ordinal)
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : ResolveExpectedRemoteMediaPeerAddressForCurrentSession();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) &&
                 !AddressMatchesForSessionPolicy(normalizedSource, expectedSource))
        {
            failureReason = "source_identity_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=screen_share_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}; request_id={requestId ?? "(none)"}; msg_id={messageId}");
        Log($"ScreenShare message rejected (type={messageType}, msg_id={messageId}, request_id={requestId ?? "(none)"}, reason={failureReason})");
        return false;
    }

    private byte[] CreateSecureControlPayload(MsgType messageType, string? requestId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.RemoteControl,
            MessageType: MapSecureControlMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundControlSecureSequence),
            RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim());
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptControlPayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_session_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = messageType == MsgType.FileTransferChunk
            ? ResolveExpectedRemoteBulkPeerAddressForCurrentSession()
            : ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_expected_sender_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_session_key_unavailable");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.RemoteControl,
                    MessageType: MapSecureControlMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={MapSecureControlMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundControlReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureControlMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureControlMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={MapSecureControlMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateControlSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string? requestId,
        string messageId)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        var normalizedMetadataRequestId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        if (!string.Equals(normalizedMetadataRequestId, normalizedRequestId, StringComparison.Ordinal))
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_request_id_mismatch");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_request_id_mismatch");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=control_message_rejected; message_type={messageType}; reason=secure_request_id_mismatch; request_id={normalizedRequestId ?? "(none)"}; secure_request_id={normalizedMetadataRequestId ?? "(none)"}");
            Log($"Control secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_request_id_mismatch)");
            return false;
        }

        return true;
    }

    private byte[] CreateSecureLifecyclePayload(MsgType messageType, string? requestId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.Lifecycle,
            MessageType: MapSecureLifecycleMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundLifecycleSecureSequence),
            RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim());
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureScreenSharePayload(MsgType messageType, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = messageType == MsgType.ScreenShareStop
            ? ResolveLocalPeerAddressForSecureEnvelope()
            : ResolveLocalMediaPeerAddressForSecureEnvelope();
        var key = GetControlSessionSharedKeyOrThrow();
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.ScreenShare,
            MessageType: MapSecureScreenShareMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundScreenShareSecureSequence),
            RequestId: null);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureFileTransferPayload(MsgType messageType, string transferId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = messageType == MsgType.FileTransferChunk
            ? ResolveLocalBulkPeerAddressForSecureEnvelope()
            : ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetFileTransferSessionSharedKeyOrThrow();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.FileTransfer,
            MessageType: MapSecureFileTransferMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Interlocked.Increment(ref nextOutboundFileTransferSecureSequence),
            RequestId: normalizedTransferId);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private byte[] CreateSecureFileTransferPayloadForBudgetEstimate(MsgType messageType, string transferId, byte[] plaintextPayload)
    {
        ArgumentNullException.ThrowIfNull(plaintextPayload);

        var sessionId = currentSessionSecurityState.SessionId
            ?? throw new InvalidOperationException("Session security state does not have an active session id.");
        var senderIdentity = messageType == MsgType.FileTransferChunk
            ? ResolveLocalBulkPeerAddressForSecureEnvelope()
            : ResolveLocalPeerAddressForSecureEnvelope();
        var key = GetFileTransferSessionSharedKeyOrThrow();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var metadata = new SessionSecureEnvelopeMetadata(
            Family: SessionSecureMessageFamily.FileTransfer,
            MessageType: MapSecureFileTransferMessageType(messageType),
            SessionId: sessionId,
            SenderIdentity: senderIdentity,
            Sequence: Math.Max(1L, Interlocked.Read(ref nextOutboundFileTransferSecureSequence) + 1),
            RequestId: normalizedTransferId);
        return SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintextPayload);
    }

    private bool TryDecryptLifecyclePayload(
        string? source,
        string messageId,
        string messageType,
        byte[] encodedPayload,
        byte[]? key,
        SessionSecureEnvelopeExpectation expectation,
        SessionReplayWindow replayWindow,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (key is null || key.Length == 0)
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_session_key_unavailable");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(key, encodedPayload, expectation);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=lifecycle_message_rejected; message_type={messageType}; reason=secure_envelope_invalid; session_id={expectation.SessionId?.Value ?? "(none)"}; source={source ?? "(none)"}; expected_source={expectation.SenderIdentity?.Value ?? "(none)"}; msg_id={messageId}; ex={ex.GetType().Name}");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = replayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{messageType}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=lifecycle_message_rejected; message_type={messageType}; reason={replayReason}; session_id={securePayload.Metadata.SessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={messageId}");
            Log($"Lifecycle secure envelope rejected (type={messageType}, msg_id={messageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryDecryptScreenSharePayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_session_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = messageType == MsgType.ScreenShareStop
            ? ResolveExpectedRemotePeerAddressForCurrentSession()
            : ResolveExpectedRemoteMediaPeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_expected_sender_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetControlSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_session_key_unavailable");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.ScreenShare,
                    MessageType: MapSecureScreenShareMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={MapSecureScreenShareMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundScreenShareReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureScreenShareMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureScreenShareMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={MapSecureScreenShareMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateScreenShareSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string messageId)
    {
        if (!string.IsNullOrWhiteSpace(metadata.RequestId))
        {
            NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_request_id_present");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_request_id_present");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=screen_share_message_rejected; message_type={messageType}; reason=secure_request_id_present; secure_request_id={metadata.RequestId}");
            Log($"ScreenShare secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_request_id_present)");
            return false;
        }

        return true;
    }

    private bool TryDecryptFileTransferPayload(
        string? source,
        Envelope env,
        MsgType messageType,
        out SessionSecureEnvelopePayload securePayload)
    {
        securePayload = default;

        if (currentSessionSecurityState.SessionId is not SessionId sessionId)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_session_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_unavailable)");
            return false;
        }

        var expectedSender = messageType == MsgType.FileTransferChunk
            ? ResolveExpectedRemoteBulkPeerAddressForCurrentSession()
            : ResolveExpectedRemotePeerAddressForCurrentSession();
        if (string.IsNullOrWhiteSpace(expectedSender) || !PeerAddress.TryParse(expectedSender, out var senderIdentity))
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_expected_sender_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_expected_sender_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=expected_sender_unavailable)");
            return false;
        }

        byte[] key;
        try
        {
            key = GetFileTransferSessionSharedKeyOrThrow();
        }
        catch (InvalidOperationException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_session_key_unavailable");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_session_key_unavailable");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=session_key_unavailable)");
            return false;
        }

        try
        {
            securePayload = SessionSecureEnvelopeCodec.Decrypt(
                key,
                env.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.FileTransfer,
                    MessageType: MapSecureFileTransferMessageType(messageType),
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException or FormatException)
        {
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_secure_envelope_invalid");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_secure_envelope_invalid");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={MapSecureFileTransferMessageType(messageType)}; reason=secure_envelope_invalid; session_id={sessionId.Value}; source={source ?? "(none)"}; expected_source={expectedSender}; msg_id={env.MessageId}; ex={ex.GetType().Name}");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason=secure_envelope_invalid, ex={ex.GetType().Name})");
            return false;
        }

        SessionReplaySequenceResult replay;
        lock (controlSecureStateGate)
        {
            replay = inboundFileTransferReplayWindow.EvaluateAndTrack(securePayload.Metadata.Sequence);
        }

        if (replay != SessionReplaySequenceResult.Accepted)
        {
            var replayReason = replay switch
            {
                SessionReplaySequenceResult.Duplicate => "replay_duplicate",
                SessionReplaySequenceResult.Stale => "replay_stale",
                SessionReplaySequenceResult.TooFarAhead => "replay_too_far_ahead",
                _ => "replay_invalid",
            };
            NknRuntimeDiagnostics.SetLastError($"{MapSecureFileTransferMessageType(messageType)}_{replayReason}");
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{MapSecureFileTransferMessageType(messageType)}_{replayReason}");
            LocalOperationalLog.Warn(
                "SessionSecurity",
                $"event=filetransfer_message_rejected; message_type={MapSecureFileTransferMessageType(messageType)}; reason={replayReason}; session_id={sessionId.Value}; source={source ?? "(none)"}; sequence={securePayload.Metadata.Sequence}; msg_id={env.MessageId}");
            Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={env.MessageId}, reason={replayReason}, seq={securePayload.Metadata.Sequence})");
            return false;
        }

        return true;
    }

    private bool TryValidateFileTransferSecureMetadata(
        string messageType,
        SessionSecureEnvelopeMetadata metadata,
        string transferId,
        string messageId)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var normalizedMetadataTransferId = string.IsNullOrWhiteSpace(metadata.RequestId) ? null : metadata.RequestId.Trim();
        if (string.Equals(normalizedMetadataTransferId, normalizedTransferId, StringComparison.Ordinal))
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_secure_transfer_id_mismatch");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_secure_transfer_id_mismatch");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason=secure_transfer_id_mismatch; transfer_id={normalizedTransferId}; secure_transfer_id={normalizedMetadataTransferId ?? "(none)"}");
        Log($"FileTransfer secure envelope rejected (type={messageType}, msg_id={messageId}, reason=secure_transfer_id_mismatch)");
        return false;
    }

    private bool TryValidateFileTransferMessageSession(
        string messageType,
        string? messageSessionId,
        string transferId,
        string messageId,
        string? source)
    {
        var expectedSessionId = currentSessionSecurityState.SessionId?.Value;
        var expectedSource = messageType == "file_transfer_chunk"
            ? ResolveExpectedRemoteBulkPeerAddressForCurrentSession()
            : ResolveExpectedRemotePeerAddressForCurrentSession();
        var normalizedMessageSessionId = string.IsNullOrWhiteSpace(messageSessionId) ? null : messageSessionId.Trim();
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        string failureReason;

        if (string.IsNullOrWhiteSpace(normalizedMessageSessionId))
        {
            failureReason = "missing_session_id";
        }
        else if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            failureReason = "session_unavailable";
        }
        else if (!string.Equals(normalizedMessageSessionId, expectedSessionId, StringComparison.Ordinal))
        {
            failureReason = "session_id_mismatch";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) && string.IsNullOrWhiteSpace(normalizedSource))
        {
            failureReason = "missing_source_identity";
        }
        else if (!string.IsNullOrWhiteSpace(expectedSource) &&
                 !AddressMatchesForSessionPolicy(normalizedSource, expectedSource))
        {
            failureReason = "source_identity_mismatch";
        }
        else
        {
            return true;
        }

        NknRuntimeDiagnostics.SetLastError($"{messageType}_{failureReason}");
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"{messageType}_{failureReason}");
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_message_rejected; message_type={messageType}; reason={failureReason}; session_id={normalizedMessageSessionId ?? "(none)"}; expected_session_id={expectedSessionId ?? "(none)"}; transfer_id={normalizedTransferId}; source={normalizedSource ?? "(none)"}; expected_source={expectedSource ?? "(none)"}");
        Log($"FileTransfer message rejected (type={messageType}, msg_id={messageId}, reason={failureReason}, transfer_id={normalizedTransferId})");
        return false;
    }

    private PeerAddress ResolveLocalPeerAddressForSecureEnvelope()
    {
        if (PeerAddress.TryParse(LocalPeerAddress, out var localAddress))
        {
            return localAddress;
        }

        throw new InvalidOperationException("Local peer address is not available for secure control payloads.");
    }

    private PeerAddress ResolveLocalMediaPeerAddressForSecureEnvelope()
    {
        var mediaAddress = string.IsNullOrWhiteSpace(client.MediaAddress) ? LocalPeerAddress : client.MediaAddress;
        if (PeerAddress.TryParse(mediaAddress, out var localAddress))
        {
            return localAddress;
        }

        throw new InvalidOperationException("Local media peer address is not available for secure screen-share payloads.");
    }

    private PeerAddress ResolveLocalBulkPeerAddressForSecureEnvelope()
    {
        var bulkAddress = string.IsNullOrWhiteSpace(client.BulkAddress) ? LocalPeerAddress : client.BulkAddress;
        if (PeerAddress.TryParse(bulkAddress, out var localAddress))
        {
            return localAddress;
        }

        throw new InvalidOperationException("Local bulk peer address is not available for secure file-transfer payloads.");
    }

    private byte[] GetControlSessionSharedKeyOrThrow()
    {
        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is null || controlSessionSharedKey.Length == 0)
            {
                throw new InvalidOperationException("Session shared key is not available.");
            }

            return controlSessionSharedKey.AsSpan().ToArray();
        }
    }

    private byte[]? TryGetControlSessionSharedKey()
    {
        lock (controlSecureStateGate)
        {
            return controlSessionSharedKey is null || controlSessionSharedKey.Length == 0
                ? null
                : controlSessionSharedKey.AsSpan().ToArray();
        }
    }

    private void SetControlSessionSharedKey(byte[] sharedKey)
    {
        ArgumentNullException.ThrowIfNull(sharedKey);

        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
            }

            if (fileTransferSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(fileTransferSessionSharedKey);
            }

            controlSessionSharedKey = sharedKey.AsSpan().ToArray();
            fileTransferSessionSharedKey = SessionKeyDerivation.DeriveFileTransferKey(sharedKey);
            nextOutboundControlSecureSequence = 0;
            nextOutboundChatSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundChatReplayWindow.Reset();
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }

        ResetInboundFileTransferDispatchQueue();
    }

    private void ResetControlSecureState()
    {
        lock (controlSecureStateGate)
        {
            if (controlSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(controlSessionSharedKey);
                controlSessionSharedKey = null;
            }

            if (fileTransferSessionSharedKey is not null)
            {
                CryptographicOperations.ZeroMemory(fileTransferSessionSharedKey);
                fileTransferSessionSharedKey = null;
            }

            nextOutboundControlSecureSequence = 0;
            nextOutboundChatSecureSequence = 0;
            nextOutboundLifecycleSecureSequence = 0;
            nextOutboundScreenShareSecureSequence = 0;
            nextOutboundFileTransferSecureSequence = 0;
            inboundChatReplayWindow.Reset();
            inboundControlReplayWindow.Reset();
            inboundLifecycleReplayWindow.Reset();
            inboundScreenShareReplayWindow.Reset();
            inboundFileTransferReplayWindow.Reset();
            fileTransferStates.Clear();
        }

        ResetInboundFileTransferDispatchQueue();
    }

    private byte[] GetFileTransferSessionSharedKeyOrThrow()
    {
        lock (controlSecureStateGate)
        {
            if (fileTransferSessionSharedKey is null || fileTransferSessionSharedKey.Length == 0)
            {
                throw new InvalidOperationException("File transfer session shared key is not available.");
            }

            return fileTransferSessionSharedKey.AsSpan().ToArray();
        }
    }

    private bool TryValidateAndTrackFileTransferMessage(
        MsgType messageType,
        string transferId,
        bool inbound,
        bool applyStateChange,
        out string failureReason)
    {
        var normalizedTransferId = NormalizeRequiredFileTransferId(transferId);

        lock (controlSecureStateGate)
        {
            if (!TryGetNextFileTransferState(messageType, normalizedTransferId, inbound, out var nextState, out failureReason))
            {
                return false;
            }

            if (applyStateChange)
            {
                CommitFileTransferStateLocked(normalizedTransferId, nextState);
            }

            return true;
        }
    }

    private bool TryGetNextFileTransferState(
        MsgType messageType,
        string transferId,
        bool inbound,
        out FileTransferTransportState nextState,
        out string failureReason)
    {
        var hasExisting = fileTransferStates.TryGetValue(transferId, out var currentState);
        nextState = default;
        failureReason = string.Empty;

        if (messageType == MsgType.FileTransferOffer)
        {
            if (hasExisting)
            {
                failureReason = currentState.IsTerminal ? "transfer_id_reused_after_terminal_state" : "duplicate_transfer_id";
                return false;
            }

            var initiatedLocally = !inbound;
            if (HasActiveFileTransferLocked(initiatedLocally))
            {
                failureReason = "concurrent_transfer_busy";
                return false;
            }

            nextState = new FileTransferTransportState(initiatedLocally, FileTransferTransportPhase.Offered);
            return true;
        }

        if (!hasExisting)
        {
            failureReason = "unknown_transfer_id";
            return false;
        }

        if (currentState.IsTerminal)
        {
            failureReason = "transfer_already_terminal";
            return false;
        }

        if (messageType == MsgType.FileTransferAccept)
        {
            if (currentState.Phase != FileTransferTransportPhase.Offered)
            {
                failureReason = "accept_requires_offer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_accept_for_remote_offer" : "unexpected_outbound_accept_for_local_offer";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Accepted };
            return true;
        }

        if (messageType == MsgType.FileTransferDecline)
        {
            if (currentState.Phase != FileTransferTransportPhase.Offered)
            {
                failureReason = "decline_requires_offer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_decline_for_remote_offer" : "unexpected_outbound_decline_for_local_offer";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Declined };
            return true;
        }

        if (messageType == MsgType.FileTransferStart)
        {
            if (currentState.Phase != FileTransferTransportPhase.Accepted)
            {
                failureReason = "start_requires_accept";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_start_for_local_receiver" : "unexpected_outbound_start_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Started };
            return true;
        }

        if (messageType == MsgType.FileTransferChunk)
        {
            if (currentState.Phase is not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "chunk_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != !inbound)
            {
                failureReason = inbound ? "unexpected_inbound_chunk_for_local_receiver" : "unexpected_outbound_chunk_for_local_receiver";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Transferring };
            return true;
        }

        if (messageType is MsgType.FileTransferWindowUpdate or MsgType.FileTransferMissingRange or MsgType.FileTransferPressureState)
        {
            if (currentState.Phase is not FileTransferTransportPhase.Accepted and not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = messageType == MsgType.FileTransferWindowUpdate
                    ? "window_update_requires_start"
                    : messageType == MsgType.FileTransferMissingRange
                        ? "missing_range_requires_start"
                        : "pressure_state_requires_start";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = messageType == MsgType.FileTransferWindowUpdate
                    ? (inbound ? "unexpected_inbound_window_update_for_local_receiver" : "unexpected_outbound_window_update_for_remote_receiver")
                    : messageType == MsgType.FileTransferMissingRange
                        ? (inbound ? "unexpected_inbound_missing_range_for_local_receiver" : "unexpected_outbound_missing_range_for_remote_receiver")
                        : (inbound ? "unexpected_inbound_pressure_state_for_local_receiver" : "unexpected_outbound_pressure_state_for_remote_receiver");
                return false;
            }

            nextState = currentState;
            return true;
        }

        if (messageType == MsgType.FileTransferComplete)
        {
            if (currentState.Phase is not FileTransferTransportPhase.Started and not FileTransferTransportPhase.Transferring)
            {
                failureReason = "complete_requires_transfer";
                return false;
            }

            if (currentState.InitiatedLocally != inbound)
            {
                failureReason = inbound ? "unexpected_inbound_complete_for_remote_sender" : "unexpected_outbound_complete_for_local_sender";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Completed };
            return true;
        }

        if (messageType == MsgType.FileTransferCancel)
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "cancel_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Canceled };
            return true;
        }

        if (messageType == MsgType.FileTransferError)
        {
            if (!CanTransitionToTerminalFileTransferState(currentState.Phase))
            {
                failureReason = "error_not_allowed_in_current_state";
                return false;
            }

            nextState = currentState with { Phase = FileTransferTransportPhase.Failed };
            return true;
        }

        failureReason = "unsupported_message_type";
        return false;
    }

    private bool HasActiveFileTransferLocked(bool initiatedLocally)
    {
        foreach (var state in fileTransferStates.Values)
        {
            if (!state.IsTerminal && state.InitiatedLocally == initiatedLocally)
            {
                return true;
            }
        }

        return false;
    }

    private void CommitFileTransferStateLocked(string transferId, FileTransferTransportState nextState)
    {
        if (nextState.IsTerminal)
        {
            fileTransferStates.Remove(transferId);
            return;
        }

        fileTransferStates[transferId] = nextState;
    }

    private static bool CanTransitionToTerminalFileTransferState(FileTransferTransportPhase phase)
        => phase is FileTransferTransportPhase.Offered or
            FileTransferTransportPhase.Accepted or
            FileTransferTransportPhase.Started or
            FileTransferTransportPhase.Transferring;

    private static string NormalizeRequiredFileTransferId(string? transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            throw new ArgumentException("Transfer id is required.", nameof(transferId));
        }

        var normalized = transferId.Trim();
        if (normalized.Length == 0 || normalized.Length > FileTransferProtocol.MaxTransferIdLength)
        {
            throw new ArgumentException("Transfer id is invalid.", nameof(transferId));
        }

        return normalized;
    }

    private static string MapSecureControlMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.ControlRequest => "control_request",
            MsgType.ControlResponse => "control_response",
            MsgType.ControlStart => "control_start",
            MsgType.ControlStop => "control_stop",
            MsgType.ControlInput => "control_input",
            MsgType.ControlAck => "control_ack",
            MsgType.ControlStateSnapshot => "control_state_snapshot",
            MsgType.ControlDisplayInfo => "control_display_info",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure control message type."),
        };
    }

    private static string MapSecureLifecycleMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.Approve => "approve",
            MsgType.Reject => "reject",
            MsgType.SessionEnd => "session_end",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure lifecycle message type."),
        };
    }

    private static string MapSecureScreenShareMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.ScreenShareFrame => "screenshare_frame",
            MsgType.ScreenShareStop => "screenshare_stop",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure screen-share message type."),
        };
    }

    private static string MapSecureFileTransferMessageType(MsgType messageType)
    {
        return messageType switch
        {
            MsgType.FileTransferOffer => "file_transfer_offer",
            MsgType.FileTransferAccept => "file_transfer_accept",
            MsgType.FileTransferDecline => "file_transfer_decline",
            MsgType.FileTransferStart => "file_transfer_start",
            MsgType.FileTransferChunk => "file_transfer_chunk",
            MsgType.FileTransferWindowUpdate => "file_transfer_window_update",
            MsgType.FileTransferMissingRange => "file_transfer_missing_range",
            MsgType.FileTransferPressureState => "file_transfer_pressure_state",
            MsgType.FileTransferCancel => "file_transfer_cancel",
            MsgType.FileTransferError => "file_transfer_error",
            MsgType.FileTransferComplete => "file_transfer_complete",
            MsgType.FileTransferSessionOpen => "file_transfer_session_open",
            MsgType.FileTransferDataFrame => "file_transfer_data_frame",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported secure file-transfer message type."),
        };
    }

    private static string MapSecureChatMessageType() => "chat_message";

    private void DeliverFileTransferDataFrame(FileTransferDataFrameV2 frame, NknBridgeChannel channel)
    {
        TransportFileTransferDataSession session;
        lock (gate)
        {
            if (!fileTransferDataSessions.TryGetValue(frame.TransferId, out session))
            {
                session = new TransportFileTransferDataSession(this, frame.SessionId, frame.TransferId);
                fileTransferDataSessions[frame.TransferId] = session;
            }
            else if (!string.Equals(session.SessionId, frame.SessionId, StringComparison.Ordinal))
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; reason=session_id_mismatch_existing_queue");
                return;
            }
        }

        session.Deliver(frame, channel);
    }

    private static void LogFileTransferEnvelopeEvent(string direction, MsgType messageType, string transferId, string? source)
    {
        if (messageType == MsgType.FileTransferChunk)
        {
            return;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_envelope_{direction}; transport=nkn; message_type={MapSecureFileTransferMessageType(messageType)}; transfer_id={transferId}; source={source ?? "(none)"}");
    }

    private static void LogFileTransferChunkIngress(string transferId, int chunkIndex, int payloadBytes, NknBridgeChannel channel)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_chunk_ingress; transport=nkn; transfer_id={transferId}; chunk_index={chunkIndex}; payload_bytes={payloadBytes}; bridge_channel={MapBridgeChannel(channel)}; dispatch_path=chunk");
    }

    private static void LogFileTransferChunkValidated(string transferId, int chunkIndex, int payloadBytes, NknBridgeChannel channel)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_chunk_validated; transport=nkn; transfer_id={transferId}; chunk_index={chunkIndex}; payload_bytes={payloadBytes}; bridge_channel={MapBridgeChannel(channel)}; dispatch_path=chunk");
    }

    private static void LogFileTransferChunkDispatched(string transferId, int chunkIndex, int payloadBytes, NknBridgeChannel channel)
    {
        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event=filetransfer_chunk_dispatched; transport=nkn; transfer_id={transferId}; chunk_index={chunkIndex}; payload_bytes={payloadBytes}; bridge_channel={MapBridgeChannel(channel)}; dispatch_path=chunk");
    }

    private static void LogFileTransferChunkRejected(string? transferId, int? chunkIndex, int payloadBytes, NknBridgeChannel channel, string reason)
    {
        LocalOperationalLog.Warn(
            "SessionSecurity",
            $"event=filetransfer_chunk_rejected; transport=nkn; transfer_id={transferId ?? "(unknown)"}; chunk_index={(chunkIndex?.ToString() ?? "(unknown)")}; payload_bytes={payloadBytes}; bridge_channel={MapBridgeChannel(channel)}; dispatch_path=chunk; reason={reason}");
    }

    private static int GetBase64PayloadBytes(string? dataBase64)
    {
        if (string.IsNullOrWhiteSpace(dataBase64))
        {
            return 0;
        }

        var normalized = dataBase64.Trim();
        var paddingCount = 0;
        if (normalized.EndsWith("==", StringComparison.Ordinal))
        {
            paddingCount = 2;
        }
        else if (normalized.EndsWith("=", StringComparison.Ordinal))
        {
            paddingCount = 1;
        }

        return Math.Max(0, (normalized.Length * 3 / 4) - paddingCount);
    }

    private static string MapBridgeChannel(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => "media",
            NknBridgeChannel.Bulk => "bulk",
            _ => "control",
        };

    private FileTransferOfferV2 EnsureFileTransferSessionId(FileTransferOfferV2 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferAcceptV1 EnsureFileTransferSessionId(FileTransferAcceptV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferDeclineV1 EnsureFileTransferSessionId(FileTransferDeclineV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferSessionOpenV2 EnsureFileTransferSessionId(FileTransferSessionOpenV2 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferStartV2 EnsureFileTransferSessionId(FileTransferStartV2 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferChunkV1 EnsureFileTransferSessionId(FileTransferChunkV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferWindowUpdateV1 EnsureFileTransferSessionId(FileTransferWindowUpdateV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferMissingRangeV1 EnsureFileTransferSessionId(FileTransferMissingRangeV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferPressureStateV1 EnsureFileTransferSessionId(FileTransferPressureStateV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferCancelV1 EnsureFileTransferSessionId(FileTransferCancelV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferErrorV1 EnsureFileTransferSessionId(FileTransferErrorV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private FileTransferCompleteV1 EnsureFileTransferSessionId(FileTransferCompleteV1 message)
        => message with
        {
            SessionId = ResolveControlSessionId(message.SessionId),
            TransferId = NormalizeRequiredFileTransferId(message.TransferId),
        };

    private enum FileTransferTransportPhase
    {
        Offered = 0,
        Accepted = 1,
        Started = 2,
        Transferring = 3,
        Completed = 4,
        Declined = 5,
        Canceled = 6,
        Failed = 7,
    }

    private readonly record struct FileTransferTransportState(
        bool InitiatedLocally,
        FileTransferTransportPhase Phase)
    {
        public bool IsTerminal =>
            Phase is FileTransferTransportPhase.Completed or
                FileTransferTransportPhase.Declined or
                FileTransferTransportPhase.Canceled or
                FileTransferTransportPhase.Failed;
    }

    private readonly record struct InboundFileTransferDispatchWork(
        long Generation,
        long Order,
        MsgType Type,
        string TransferId,
        bool UsesDedicatedChunkDispatch,
        bool BlocksChunkDispatch,
        Action Dispatch,
        TaskCompletionSource<bool>? LifecycleCompletion);

    private sealed class TransportFileTransferDataSession : IFileTransferDataSession
    {
        private readonly NknSignalingTransport owner;
        private readonly Channel<FileTransferDataFrameV2> frames = Channel.CreateUnbounded<FileTransferDataFrameV2>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private int disposed;
        private int activeReader;
        private int available = 1;

        public TransportFileTransferDataSession(NknSignalingTransport owner, string sessionId, string transferId)
        {
            this.owner = owner;
            SessionId = sessionId;
            TransferId = transferId;
        }

        public string SessionId { get; }

        public string TransferId { get; }

        public bool IsAvailable => Volatile.Read(ref available) != 0;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public async ValueTask<FileTransferDataFrameV2> ReceiveAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
            {
                LocalOperationalLog.Warn(
                    "FileTransferService",
                    $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=transport_session_multiple_readers");
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

        public Task SendAsync(FileTransferDataFrameV2 frame, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            ArgumentNullException.ThrowIfNull(frame);

            if (frame is FileTransferChunkBatchFrameV2 batch && batch.DataSegments.Count > 0)
            {
                return SendChunkBatchAsSingleFramesAsync(batch, ct);
            }

            var useBulkLane = ShouldUseBulkLane(frame);
            var serializedFrame = FileTransferDataFrameCodec.Serialize(frame);

            return owner.SendFileTransferEnvelopeRawAsync(
                MsgType.FileTransferDataFrame,
                TransferId,
                serializedFrame,
                useBulkLane,
                frame.Type,
                ct);
        }

        private async Task SendChunkBatchAsSingleFramesAsync(FileTransferChunkBatchFrameV2 batch, CancellationToken ct)
        {
            var perFrameRawBytes = string.Join(",", batch.DataSegments.Select(static segment => segment?.Length ?? 0));
            var finalChunkIndex = batch.StartChunkIndex + batch.DataSegments.Count - 1;
            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_chunk_batch_split_for_transport; transport=nkn; transfer_id={batch.TransferId}; session_id={batch.SessionId}; original_frame_type={batch.Type}; split_chunk_range={batch.StartChunkIndex}-{finalChunkIndex}; chunk_frame_count={batch.DataSegments.Count}; per_frame_raw_bytes={perFrameRawBytes}; lane=bulk");

            for (var segmentOffset = 0; segmentOffset < batch.DataSegments.Count; segmentOffset++)
            {
                ct.ThrowIfCancellationRequested();
                var chunkFrame = CreateSplitChunkFrame(batch, segmentOffset);

                var serializedFrame = FileTransferDataFrameCodec.Serialize(chunkFrame);
                await owner.SendFileTransferEnvelopeRawAsync(
                        MsgType.FileTransferDataFrame,
                        TransferId,
                        serializedFrame,
                        useBulkLane: true,
                        chunkFrame.Type,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        private static FileTransferChunkDataFrameV2 CreateSplitChunkFrame(FileTransferChunkBatchFrameV2 batch, int segmentOffset)
        {
            var chunkIndex = batch.StartChunkIndex + segmentOffset;
            var data = batch.DataSegments[segmentOffset] ?? [];

            return batch switch
            {
                FileTransferChunkBatchFrameV3 => new FileTransferChunkDataFrameV3
                {
                    SessionId = batch.SessionId,
                    TransferId = batch.TransferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = batch.ChunkCount,
                    Data = data,
                },
                _ => new FileTransferChunkDataFrameV2
                {
                    SessionId = batch.SessionId,
                    TransferId = batch.TransferId,
                    ChunkIndex = chunkIndex,
                    ChunkCount = batch.ChunkCount,
                    Data = data,
                },
            };
        }

        public void Deliver(FileTransferDataFrameV2 frame, NknBridgeChannel channel)
        {
            if (disposed != 0)
            {
                return;
            }

            LocalOperationalLog.Info(
                "SessionSecurity",
                $"event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetFileTransferDataFrameChunkIndex(frame)}; lane={MapBridgeChannel(channel)}");
            frames.Writer.TryWrite(frame);
        }

        public void SetAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
        {
            if (disposed != 0)
            {
                return;
            }

            var next = isAvailable ? 1 : 0;
            var previous = Interlocked.Exchange(ref available, next);
            if (previous == next)
            {
                return;
            }

            AvailabilityChanged?.Invoke(
                this,
                new FileTransferDataSessionAvailabilityChangedEventArgs(
                    isAvailable,
                    reason,
                    requiresResumeRequest));
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

    private static string GetFileTransferDataFrameChunkIndex(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.ChunkIndex.ToString(),
            FileTransferChunkBatchFrameV2 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };

    private static bool ShouldUseBulkLane(FileTransferDataFrameV2 frame)
        => frame is FileTransferChunkDataFrameV2 or FileTransferChunkBatchFrameV2;

    private static void LogFileTransferPayloadBudget(
        string transferId,
        MsgType messageType,
        string? frameType,
        string lane,
        int serializedPayloadBytes,
        int securePayloadBytes,
        int bridgePayloadBytes,
        int bridgeCommandBytes,
        bool rejected)
    {
        if (messageType != MsgType.FileTransferDataFrame ||
            frameType is not (
                FileTransferProtocol.ChunkDataFrameTypeV2 or
                FileTransferProtocol.ChunkBatchFrameTypeV2 or
                FileTransferProtocol.ChunkDataFrameTypeV3 or
                FileTransferProtocol.ChunkBatchFrameTypeV3))
        {
            return;
        }

        LocalOperationalLog.Info(
            "SessionSecurity",
            $"event={(rejected ? "filetransfer_transport_payload_rejected" : "filetransfer_transport_payload_budget")}; transport=nkn; transfer_id={transferId}; message_type={MapSecureFileTransferMessageType(messageType)}; frame_type={frameType}; lane={lane}; serialized_payload_bytes={serializedPayloadBytes}; secure_payload_bytes={securePayloadBytes}; bridge_payload_bytes={bridgePayloadBytes}; bridge_command_bytes={bridgeCommandBytes}; max_allowed_bytes={FileTransferMaxBridgePayloadBytes}");
    }
}
